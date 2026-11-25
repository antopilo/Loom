using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Signs an Omp profile in to AI providers in the app: it lists Omp's providers, runs a chosen one's browser sign-in, and falls back to Omp's terminal login.</summary>
/// <remarks>Credentials stay in Omp; Lokn only relays the sign-in dialogs. Omp's RPC refuses providers that ask for a secret such as an API key, so those sign in in the terminal.</remarks>
internal sealed class OmpAccountsContent : PanelContent
{
	/// <summary>Stores the panel's context.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the service that starts Omp.</summary>
	private readonly OmpService _service;

	/// <summary>Retains the Lokn profile whose Omp account signs in.</summary>
	private readonly State<SettingsProfile> _profile;

	/// <summary>Retains the providers Omp offers, or none before they load.</summary>
	private readonly State<JsonElement[]> _providers = new( [] );

	/// <summary>Reports whether Omp is starting or listing providers.</summary>
	private readonly State<bool> _isConnecting = new( false );

	/// <summary>Retains why Omp could not start or list providers, or empty.</summary>
	private readonly State<string> _connectError = new( "" );

	/// <summary>Retains the name of the provider being signed in to, or empty.</summary>
	private readonly State<string> _signingIn = new( "" );

	/// <summary>Retains the sign-in web page the browser was sent to, or empty.</summary>
	private readonly State<string> _url = new( "" );

	/// <summary>Retains Omp's latest instruction or notice for the current sign-in, or empty.</summary>
	private readonly State<string> _notice = new( "" );

	/// <summary>Retains a sign-in dialog awaiting the user's answer, or the default element when there is none.</summary>
	private readonly State<JsonElement> _input = new( default );

	/// <summary>Retains why the last sign-in failed, or empty.</summary>
	private readonly State<string> _loginError = new( "" );

	/// <summary>Reports whether Omp's login runs in a terminal, waiting for the user to say it finished.</summary>
	private readonly State<bool> _isInTerminal = new( false );

	/// <summary>Retains the name of the provider the last sign-in succeeded for, or empty.</summary>
	private readonly State<string> _signedIn = new( "" );

	/// <summary>Completes when a sign-in succeeds or the panel closes; replaced when a new sign-in is requested after it completed.</summary>
	private TaskCompletionSource _completion = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Owns the Omp session while this panel stays open.</summary>
	private OmpSession? _session;

	/// <summary>Creates the page for the requested or restored profile and starts connecting.</summary>
	internal OmpAccountsContent( PanelContext context, OmpService service )
	{
		_context = context;
		_service = service;
		_profile = new( ComputeInitialProfile( context ) );
		_ = UpdateConnectionAsync( completesWhenSignedIn: false );
	}

	/// <summary>Shows the page for a profile, reconnecting when it changed or could not connect.</summary>
	/// <returns>A task that completes when a sign-in succeeds on this page or it closes.</returns>
	internal Task UpdateSignIn( SettingsProfile profile )
	{
		if ( _completion.Task.IsCompleted )
			_completion = new( TaskCreationOptions.RunContinuationsAsynchronously );
		if ( profile != _profile.Value || (_connectError.Value.Length > 0 && !_isConnecting.Value) )
		{
			_profile.Value = profile;
			_ = UpdateConnectionAsync( completesWhenSignedIn: false );
		}
		return _completion.Task;
	}

	/// <summary>Builds the heading, the current sign-in's state, and the provider list.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var profile = _profile.Value;
		var page = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Children =
				[
					new Text { Content = "Sign in", FontSize = OmpSetupContent.HeadingSize, FlexGrow = 1 },
					BuildAction( "Omp setup", () => OmpModule.OpenSetup( _context.App, profile ) ),
				],
			},
			new Text
			{
				Content = $"Connect an AI provider to the Omp account \"{_service.GetProfileName( profile )}\", used by {profile} chats. Credentials stay in Omp.",
				Color = theme.Muted,
			},
		};
		if ( BuildStatus() is { } status )
			page.Add( status );
		if ( _providers.Value.Length > 0 )
			page.Add( BuildProviders() );
		page.Add( new Text
		{
			Content = "Providers that ask for an API key sign in in the terminal.",
			Color = theme.Muted,
			FontSize = OmpSetupContent.SmallSize,
		} );
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Omp sign-in",
			FlexGrow = 1,
			MinHeight = 0,
			Children = [OmpSetupContent.BuildColumn( page )],
		};
	}

	/// <summary>Builds the card for what is happening: connecting, signing in, waiting for the terminal, a failure, or success; null when idle.</summary>
	private Container? BuildStatus()
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( _isConnecting.Value )
			children.Add( OmpSetupContent.BuildLine( null, "Connecting to Omp…", "", [] ) );
		else if ( _connectError.Value is { Length: > 0 } connectError )
		{
			children.Add( OmpSetupContent.BuildLine( theme.Error, "Could not start Omp", "",
				[BuildAction( "Try again", () => _ = UpdateConnectionAsync( completesWhenSignedIn: false ) ), BuildAction( "Sign in in terminal", UpdateTerminalSignIn )] ) );
			children.Add( BuildMessage( connectError ) );
		}
		else if ( _signingIn.Value is { Length: > 0 } provider )
		{
			children.Add( OmpSetupContent.BuildLine( theme.Accent, $"Signing in to {provider}", "", [] ) );
			if ( _url.Value is { Length: > 0 } url )
			{
				children.Add( OmpSetupContent.BuildLine( null, "Finish signing in in your browser…", "", [BuildAction( "Open page again", () => UpdateSignInPage( url ) )] ) );
				children.Add( BuildMessage( url ) );
			}
			if ( _notice.Value is { Length: > 0 } notice )
				children.Add( BuildMessage( notice ) );
			if ( _input.Value is { ValueKind: JsonValueKind.Object } input )
				children.AddRange( BuildDialog( input ) );
		}
		else if ( _isInTerminal.Value )
		{
			children.Add( OmpSetupContent.BuildLine( theme.Accent, "Finish signing in in the terminal", "then check again here", [BuildAction( "Check again", () => _ = UpdateConnectionAsync( completesWhenSignedIn: true ) )] ) );
			if ( _notice.Value is { Length: > 0 } notice )
				children.Add( BuildMessage( notice ) );
		}
		else if ( _loginError.Value is { Length: > 0 } loginError )
		{
			children.Add( OmpSetupContent.BuildLine( theme.Error, "Sign-in did not finish", "", [BuildAction( "Sign in in terminal", UpdateTerminalSignIn )] ) );
			if ( _notice.Value is { Length: > 0 } notice )
				children.Add( BuildMessage( notice ) );
			children.Add( BuildMessage( loginError ) );
		}
		else if ( _signedIn.Value is { Length: > 0 } signedIn )
			children.Add( OmpSetupContent.BuildLine( theme.Success, $"Signed in to {signedIn}", "Chats can use it now", [BuildAction( "Close", () => _context.App.Panels.Close( _context.Id ) )] ) );
		else
			return null;

		return OmpSetupContent.BuildSurface( "status", isFocused: false, children );
	}

	/// <summary>Builds the controls of an Omp sign-in dialog: a confirmation, a choice, or a text field, with Cancel.</summary>
	private List<Blob> BuildDialog( JsonElement input )
	{
		var theme = Theme.Current;
		var children = new List<Blob> { new Text { Content = ReadText( input, "title" ), Color = theme.Text } };
		var method = ReadText( input, "method" );
		var actions = new List<Blob>();
		if ( method == "confirm" )
		{
			children.Add( BuildMessage( ReadText( input, "message" ) ) );
			actions.Add( BuildAction( "Confirm", () => UpdateAnswer( input, "true" ) ) );
		}
		else if ( method == "select" && input.TryGetProperty( "options", out var options ) )
		{
			foreach ( var option in options.EnumerateArray() )
			{
				var label = option.GetString() ?? "";
				actions.Add( BuildAction( label, () => UpdateAnswer( input, label ) ) );
			}
		}
		else
		{
			children.Add( new TextEntry
			{
				Key = "answer",
				Placeholder = ReadText( input, "placeholder", "Paste the authorization callback or code" ),
				AccessibilityName = "Omp authorization callback",
				MinHeight = theme.ControlHeight + theme.Spacing,
				Padding = theme.Spacing,
				BackgroundColor = theme.Panel,
				BorderRadius = theme.Radius,
				OnSubmit = value => _context.Run( () => UpdateAnswer( input, value ) ),
			} );
		}

		actions.Add( BuildAction( "Cancel", () => UpdateAnswer( input, null ) ) );
		children.Add( new Row { Gap = theme.Spacing, FlexWrap = FlexWrap.Wrap, Children = actions } );
		return children;
	}

	/// <summary>Builds the provider card: a row per provider with its sign-in state and the action that signs in to it.</summary>
	private Container BuildProviders()
	{
		var theme = Theme.Current;
		var isIdle = _signingIn.Value.Length == 0 && !_isConnecting.Value;
		var children = new List<Blob> { new Text { Content = "Providers", FontWeight = OmpSetupContent.TitleWeight, Color = theme.Text } };
		foreach ( var provider in _providers.Value )
		{
			var id = ReadText( provider, "id" );
			var name = ReadText( provider, "name", id );
			var isAuthenticated = provider.TryGetProperty( "authenticated", out var authenticated ) && authenticated.ValueKind == JsonValueKind.True;
			var action = BuildAction( isAuthenticated ? "Add account" : "Connect", () => UpdateLogin( id, name ), isIdle );
			action.AccessibilityName = $"{(isAuthenticated ? "Add account for" : "Connect")} {name}";
			var row = new List<Blob>
			{
				OmpSetupContent.BuildDot( isAuthenticated ? theme.Success : theme.Inactive ),
				new Text { Content = name, Color = theme.Text, FlexGrow = 1, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
			};
			if ( isAuthenticated )
				row.Add( new Text { Content = "Signed in", Color = theme.Success, FontSize = OmpSetupContent.SmallSize, FlexShrink = 0 } );
			row.Add( action );
			children.Add( new Row
			{
				Key = id,
				AccessibilityRole = AccessibilityRole.Group,
				AccessibilityName = name,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				MinHeight = theme.ControlHeight,
				Children = row,
			} );
		}

		return OmpSetupContent.BuildSurface( "providers", isFocused: false, children );
	}

	/// <summary>Builds a muted, wrapping line, such as an error or a sign-in address.</summary>
	private static Text BuildMessage( string content ) => new()
	{
		Content = content,
		Color = Theme.Current.Muted,
		FontSize = OmpSetupContent.SmallSize,
		TextWrap = TextWrap.Wrap,
	};

	/// <summary>Builds a compact action; a disabled one stays visible but muted.</summary>
	private Button BuildAction( string label, Action action, bool isEnabled = true )
	{
		var button = Theme.Current.BuildButton( label, () => _context.Run( action ), isEnabled );
		button.FlexShrink = 0;
		return button;
	}

	/// <summary>Starts Omp for the profile, replacing any earlier session, and lists its providers.</summary>
	/// <param name="completesWhenSignedIn">Whether a provider found signed in counts as the sign-in finishing, after the terminal login.</param>
	private async Task UpdateConnectionAsync( bool completesWhenSignedIn )
	{
		if ( _isConnecting.Value )
			return;

		var profile = _profile.Value;
		_isConnecting.Value = true;
		_connectError.Value = "";
		_providers.Value = [];
		_signingIn.Value = "";
		_url.Value = "";
		_notice.Value = "";
		_input.Value = default;
		_loginError.Value = "";
		_signedIn.Value = "";
		try
		{
			if ( _session is { } previous )
			{
				_session = null;
				previous.FrameReceived -= UpdateFrame;
				await previous.StopAsync();
			}

			var session = await _service.OpenAccountSessionAsync( profile, _context.Lifetime, onStarting: starting =>
			{
				_session = starting;
				starting.FrameReceived += UpdateFrame;
			} );
			var providers = await ReadProvidersAsync( session );
			_context.Post( () =>
			{
				_providers.Value = providers;
				if ( completesWhenSignedIn && _isInTerminal.Value && providers.Any( IsAuthenticated ) )
				{
					_signedIn.Value = string.Join( ", ", providers.Where( IsAuthenticated ).Select( provider => ReadText( provider, "name", ReadText( provider, "id" ) ) ) );
					_completion.TrySetResult();
				}
				_isInTerminal.Value = false;
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => _connectError.Value = error.Message );
		}
		finally
		{
			_context.Post( () => _isConnecting.Value = false );
		}
	}

	/// <summary>Asks Omp for its sign-in providers.</summary>
	private async Task<JsonElement[]> ReadProvidersAsync( OmpSession session )
	{
		var data = await session.RequestAsync( "get_login_providers", cancellationToken: _context.Lifetime );
		var providers = data.ValueKind == JsonValueKind.Array ? data : data.GetProperty( "providers" );
		return [.. providers.EnumerateArray().Select( provider => provider.Clone() )];
	}

	/// <summary>Signs in to a provider; Omp's sign-in page opens in the browser as soon as Omp names it.</summary>
	private async void UpdateLogin( string providerId, string providerName )
	{
		if ( _session is not { } session || _signingIn.Value.Length > 0 )
			return;

		_signingIn.Value = providerName;
		_url.Value = "";
		_notice.Value = "";
		_input.Value = default;
		_loginError.Value = "";
		_signedIn.Value = "";
		_isInTerminal.Value = false;
		try
		{
			await session.RequestAsync( "login", new
			{
				providerId
			}, _context.Lifetime );
			_context.Post( () =>
			{
				_signingIn.Value = "";
				_url.Value = "";
				_input.Value = default;
				_signedIn.Value = providerName;
				_completion.TrySetResult();
			} );
			// The list only refreshes the Signed in tags; the sign-in already succeeded.
			var providers = await ReadProvidersAsync( session );
			_context.Post( () => _providers.Value = providers );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () =>
			{
				if ( _signedIn.Value != providerName )
					_loginError.Value = error.Message;
			} );
		}
		finally
		{
			_context.Post( () =>
			{
				_signingIn.Value = "";
				_url.Value = "";
				_input.Value = default;
			} );
		}
	}

	/// <summary>Opens Omp's interactive login in a Loom terminal, or in Omp's own console window when the Terminal module is not loaded.</summary>
	private void UpdateTerminalSignIn()
	{
		var name = _service.GetProfileName( _profile.Value );
		_isInTerminal.Value = true;
		_loginError.Value = "";
		_notice.Value = "";
		if ( _context.App.Services.Get<ITerminalLauncher>() is { } terminal )
		{
			terminal.Run( "Omp sign-in", _service.ComputeLoginCommandLine( name ) );
			return;
		}

		_ = UpdateConsoleSignInAsync( name );
	}

	/// <summary>Runs Omp's login in its console window and checks the providers again once the user closes it.</summary>
	private async Task UpdateConsoleSignInAsync( string profileName )
	{
		try
		{
			await _service.OpenLoginAsync( profileName );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => _notice.Value = error.Message );
		}
		_context.Post( () => _ = UpdateConnectionAsync( completesWhenSignedIn: true ) );
	}

	/// <summary>Posts sign-in events from the reader thread to the page.</summary>
	private void UpdateFrame( JsonElement frame )
	{
		if ( ReadText( frame, "type" ) != "extension_ui_request" )
			return;

		_context.Post( () =>
		{
			switch ( ReadText( frame, "method" ) )
			{
				case "open_url":
					var url = ReadText( frame, "launchUrl", ReadText( frame, "url" ) );
					_url.Value = url;
					_notice.Value = ReadText( frame, "instructions" );
					// The user just chose Connect, so the page opens without another click.
					if ( _signingIn.Value.Length > 0 && url.Length > 0 )
						UpdateSignInPage( url );
					break;
				case "input":
				case "editor":
				case "select":
				case "confirm":
					if ( frame.TryGetProperty( "secret", out var secret ) && secret.ValueKind == JsonValueKind.True )
					{
						// Omp's RPC rejects secret prompts; the terminal login can ask for them.
						UpdateAnswer( frame, null );
						_notice.Value = "This provider asks for a secret, such as an API key.";
						break;
					}

					_input.Value = frame;
					break;
				case "ask":
					UpdateAnswer( frame, null );
					_notice.Value = "An extension asked a question this page cannot show.";
					break;
				case "notify":
					_notice.Value = ReadText( frame, "message" );
					break;
				case "cancel":
					_input.Value = default;
					break;
			}
		} );
	}

	/// <summary>Answers or cancels the current sign-in dialog.</summary>
	private async void UpdateAnswer( JsonElement request, string? value )
	{
		if ( _session is not { } session )
			return;

		_input.Value = default;
		try
		{
			var id = ReadText( request, "id" );
			object response = value is null
				? new
				{
					type = "extension_ui_response",
					id,
					cancelled = true
				}
				: ReadText( request, "method" ) == "confirm"
					? new
					{
						type = "extension_ui_response",
						id,
						confirmed = true
					}
					: new
					{
						type = "extension_ui_response",
						id,
						value
					};
			await session.SendAsync( response, _context.Lifetime );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => _notice.Value = error.Message );
		}
	}

	/// <summary>Opens Omp's sign-in web page in the browser, showing why when it cannot.</summary>
	private void UpdateSignInPage( string url )
	{
		if ( !Uri.TryCreate( url, UriKind.Absolute, out var target ) || target.Scheme is not ("https" or "http") )
		{
			_notice.Value = "Omp returned an invalid sign-in address.";
			return;
		}

		try
		{
			Process.Start( new ProcessStartInfo( target.AbsoluteUri )
			{
				UseShellExecute = true,
			} )?.Dispose();
		}
		catch ( Exception error )
		{
			_notice.Value = $"Could not open the browser: {error.Message}";
		}
	}

	/// <summary>Reports whether Omp lists a provider as signed in.</summary>
	private static bool IsAuthenticated( JsonElement provider ) => provider.TryGetProperty( "authenticated", out var authenticated ) && authenticated.ValueKind == JsonValueKind.True;

	/// <summary>Reads optional protocol text without assuming every field is present.</summary>
	private static string ReadText( JsonElement value, string property, string fallback = "" ) => value.TryGetProperty( property, out var text ) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } content ? content : fallback;

	/// <summary>Gets the requested profile, the restored one, or the active one.</summary>
	private static SettingsProfile ComputeInitialProfile( PanelContext context )
	{
		if ( context.Input is SettingsProfile profile )
			return profile;
		if ( context.RestoredState is { } saved && saved.TryGetInt32( out var value ) && Enum.IsDefined( (SettingsProfile)value ) )
			return (SettingsProfile)value;
		return context.App.Settings.ActiveProfile;
	}

	/// <summary>Remembers the profile for layout restoration.</summary>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( _profile.Value );

	/// <summary>Stops the Omp session and releases whoever waits for this page.</summary>
	public override void Closed()
	{
		_completion.TrySetResult();
		if ( _session is { } session )
		{
			session.FrameReceived -= UpdateFrame;
			_ = session.StopAsync();
		}
	}
}
