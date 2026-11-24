using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Shows what Lokn found: the Omp install, and for Work and Personal the Omp account and model they use, each changeable in one click.</summary>
/// <remarks>
/// Everything is detected rather than typed: Omp's executable and version, every Omp profile on disk with the providers it is signed
/// in to, and each profile's own default model. Choices are saved at once to the Work or Personal settings layer they belong to.
/// </remarks>
internal sealed class OmpSetupContent : PanelContent
{
	/// <summary>Defines page padding in logical pixels.</summary>
	private const int ContentPadding = 24;

	/// <summary>Defines the widest the page grows, in logical pixels.</summary>
	private const int PageWidth = 720;

	/// <summary>Defines the heading size in logical pixels.</summary>
	internal const int HeadingSize = 22;

	/// <summary>Defines the card title and label weight.</summary>
	internal const int TitleWeight = 600;

	/// <summary>Defines the label column width in logical pixels.</summary>
	private const int LabelWidth = 72;

	/// <summary>Defines metadata text size in logical pixels.</summary>
	internal const int SmallSize = 12;

	/// <summary>Defines the status dot diameter in logical pixels.</summary>
	private const int DotSize = 7;

	/// <summary>Stores the panel's context.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the service that detects and runs Omp.</summary>
	private readonly OmpService _service;

	/// <summary>Holds the detection shown before the first one finishes.</summary>
	private static readonly OmpDetection Pending = new( null, "", [] );

	/// <summary>Retains the last detection, or <see cref="Pending"/> before the first one finishes.</summary>
	private readonly State<OmpDetection> _detection = new( Pending );

	/// <summary>Reports whether a detection is running.</summary>
	private readonly State<bool> _isDetecting = new( false );

	/// <summary>Retains the profile whose card is emphasized, from the chat that opened setup.</summary>
	private readonly State<SettingsProfile> _focus;

	/// <summary>Retains the name of the profile whose account list is open, or empty.</summary>
	private readonly State<string> _choosingAccount = new( "" );

	/// <summary>Retains the name of the profile whose model picker is open, or empty.</summary>
	private readonly State<string> _choosingModel = new( "" );

	/// <summary>Retains the Omp profile waiting for its login window to close, or empty.</summary>
	private readonly State<string> _signingIn = new( "" );

	/// <summary>Retains the typed name of a new Omp profile.</summary>
	private readonly State<string> _newProfile = new( "" );

	/// <summary>Reports whether the executable path is being edited.</summary>
	private readonly State<bool> _isEditingExecutable = new( false );

	/// <summary>Reports whether Omp is being installed.</summary>
	private readonly State<bool> _isInstalling = new( false );

	/// <summary>Retains the install's last progress line, or empty.</summary>
	private readonly State<string> _installLine = new( "" );

	/// <summary>Creates setup, emphasizing the requested or restored profile, and starts detecting.</summary>
	internal OmpSetupContent( PanelContext context, OmpService service )
	{
		_context = context;
		_service = service;
		_focus = new( ComputeInitialFocus( context ) );
		_ = UpdateDetectionAsync();
	}

	/// <summary>Emphasizes a profile's card when another chat opens setup.</summary>
	internal void UpdateFocus( SettingsProfile profile ) => _focus.Value = profile;

	/// <summary>Builds the install line, a card per Lokn profile, and the model picker over them when open.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var page = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Children =
				[
					new Text { Content = "Omp setup", FontSize = HeadingSize, FlexGrow = 1 },
					BuildAction( _isDetecting.Value ? "Detecting…" : "Detect again", () => _ = UpdateDetectionAsync(), !_isDetecting.Value ),
				],
			},
			BuildInstall(),
		};
		if ( _detection.Value.Install is not null )
		{
			page.Add( BuildCard( SettingsProfile.Work ) );
			page.Add( BuildCard( SettingsProfile.Personal ) );
			page.Add( new Text
			{
				Content = "Each Omp profile keeps its own sign-ins, settings and sessions; credentials stay in Omp. Open chats keep the account they started with.",
				Color = theme.Muted,
				FontSize = SmallSize,
			} );
		}

		var root = new List<Blob> { BuildColumn( page ) };
		if ( Enum.TryParse<SettingsProfile>( _choosingModel.Value, out var picking ) )
			root.Add( BuildModelPicker( picking ) );
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Omp setup",
			FlexGrow = 1,
			MinHeight = 0,
			Children = root,
		};
	}

	/// <summary>Remembers the emphasized profile for layout restoration.</summary>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( _focus.Value.ToString() );

	/// <summary>Builds the Omp install line: version and where it came from, or why none runs and how to fix it.</summary>
	private Blob BuildInstall()
	{
		var theme = Theme.Current;
		var detection = _detection.Value;
		var children = new List<Blob>();
		if ( ReferenceEquals( detection, Pending ) )
			children.Add( BuildLine( null, "Looking for Omp…", "", [] ) );
		else if ( detection.Install is { } install )
		{
			children.Add( BuildLine( theme.Success, $"Omp {install.Version}", $"{install.Source} · {install.Path}",
				[BuildAction( "Use another omp.exe", () => _isEditingExecutable.Value = !_isEditingExecutable.Value )] ) );
		}
		else
		{
			var actions = new List<Blob>();
			if ( _isInstalling.Value )
				actions.Add( new Text { Content = _installLine.Value.Length == 0 ? "Installing Omp…" : _installLine.Value, Color = theme.Accent, FontSize = SmallSize } );
			else
				actions.Add( BuildAction( "Install Omp", () => _ = UpdateInstallAsync() ) );
			actions.Add( BuildAction( "Choose omp.exe", () => _isEditingExecutable.Value = true ) );
			// A broken path in settings hides an install Lokn could use instead.
			if ( _context.App.Settings.HasValue( OmpModule.Executable, SettingsScope.Global ) && OmpService.ResolveExecutable( OmpService.DefaultExecutable ) is not null )
				actions.Insert( 0, BuildAction( "Use detected Omp", () => UpdateExecutable( null ) ) );
			children.Add( BuildLine( theme.Error, "Omp not found", detection.Error, actions ) );
		}

		if ( _isEditingExecutable.Value )
		{
			children.Add( new TextEntry
			{
				Key = "executable",
				Value = _context.App.Settings.Get( OmpModule.Executable, SettingsScope.Global ),
				Placeholder = @"C:\path\to\omp.exe",
				AccessibilityName = "Omp executable",
				MinHeight = theme.ControlHeight + theme.Spacing,
				Padding = theme.Spacing,
				BackgroundColor = theme.Panel,
				BorderRadius = theme.Radius,
				OnSubmit = value => _context.Run( () => UpdateExecutable( value.Trim().Trim( '"' ) ) ),
			} );
			children.Add( new Text { Content = "Press Enter to use this path. A bare name such as omp is looked up on PATH.", Color = theme.Muted, FontSize = SmallSize } );
		}

		return BuildSurface( "install", isFocused: false, children );
	}

	/// <summary>Builds a Lokn profile's card: the Omp account it uses and its model, with what needs fixing and how.</summary>
	private Blob BuildCard( SettingsProfile profile )
	{
		var theme = Theme.Current;
		var settings = _context.App.Settings;
		var scope = profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal;
		var name = _service.GetProfileName( profile );
		var account = _detection.Value.Profiles.FirstOrDefault( item => item.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
		var children = new List<Blob>
		{
			new Text { Content = profile.ToString(), FontWeight = TitleWeight, Color = theme.Text },
		};

		// Account: which Omp profile, and whether it can use any model.
		var accountActions = new List<Blob>();
		if ( _signingIn.Value == name )
			accountActions.Add( new Text { Content = "Finish signing in on the Sign in page…", Color = theme.Accent, FontSize = SmallSize } );
		else if ( account is not { IsSignedIn: true } )
		{
			// An Omp account already signed in elsewhere is one click away, instead of a second sign-in.
			if ( OmpModule.FindSignedInAccount( _detection.Value, name ) is { } signedIn )
				accountActions.Add( BuildAction( $"Use {signedIn.Name}", () => settings.Set( OmpModule.ProfileName, scope, signedIn.Name ) ) );
			accountActions.Add( BuildAction( "Sign in", () => _ = UpdateLoginAsync( profile, name ) ) );
		}
		var isChoosing = _choosingAccount.Value == profile.ToString();
		accountActions.Add( BuildAction( isChoosing ? "Done" : "Change", () => _choosingAccount.Value = isChoosing ? "" : profile.ToString() ) );
		children.Add( BuildField( "Account", account is { IsSignedIn: true } ? theme.Success : theme.Error, name, DescribeAccount( account ), accountActions ) );
		if ( isChoosing )
			children.Add( BuildAccounts( profile, scope, name ) );

		// Model: the one chats start with, chosen in Lokn or taken from the account's own Omp default.
		var chosen = settings.Get( OmpModule.Model, scope ).Trim();
		var catalog = _service.GetCachedModels( profile );
		var modelActions = new List<Blob>();
		Color? modelState;
		string model, detail;
		if ( chosen.Length > 0 )
		{
			var isAvailable = catalog is null || catalog.Any( item => item.Selector == chosen );
			(modelState, model, detail) = (isAvailable ? theme.Success : theme.Error, chosen, isAvailable ? "chosen in Lokn" : "not available on this account");
			if ( account?.DefaultModel is { Length: > 0 } )
				modelActions.Add( BuildAction( "Use Omp's default", () => settings.Set( OmpModule.Model, scope, "" ) ) );
		}
		else if ( account?.DefaultModel is { Length: > 0 } inherited )
			(modelState, model, detail) = (theme.Success, inherited, "this account's Omp default");
		else if ( account is { IsSignedIn: true } )
			// Without a default Omp starts with one of the account's own models, so this is a choice, not a fault.
			(modelState, model, detail) = (theme.Inactive, "Omp's choice", "no default set; Omp picks one of this account's models");
		else
			(modelState, model, detail) = (theme.Error, "No model", "sign in to choose one");
		if ( account is { IsSignedIn: true } )
			modelActions.Add( BuildAction( "Change", () => _choosingModel.Value = profile.ToString() ) );
		children.Add( BuildField( "Model", modelState, model, detail, modelActions ) );
		return BuildSurface( profile.ToString(), _focus.Value == profile, children );
	}

	/// <summary>Builds the list of Omp profiles to choose from, each with its sign-in state, then a field for a new one.</summary>
	private Blob BuildAccounts( SettingsProfile profile, SettingsScope scope, string current )
	{
		var theme = Theme.Current;
		var rows = new List<Blob>();
		foreach ( var account in _detection.Value.Profiles )
		{
			var isCurrent = account.Name.Equals( current, StringComparison.OrdinalIgnoreCase );
			var row = BuildAction( account.Name, () =>
			{
				_context.App.Settings.Set( OmpModule.ProfileName, scope, account.Name );
				_choosingAccount.Value = "";
			} );
			row.AccessibilityName = $"Use {account.Name} for {profile}";
			row.FlexDirection = FlexDirection.Row;
			row.JustifyContent = JustifyContent.FlexStart;
			row.AlignItems = AlignItems.Center;
			row.Gap = theme.Spacing;
			row.BackgroundColor = isCurrent ? theme.Hover : Color.Transparent;
			row.Children =
			[
				BuildDot( account.IsSignedIn ? theme.Success : theme.Inactive ),
				new Text { Content = account.Name, FontWeight = isCurrent ? TitleWeight : 400, Color = theme.Text, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				new Text { Content = DescribeAccount( account ), FontSize = SmallSize, Color = theme.Muted, MinWidth = 0, FlexShrink = 1, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
			];
			rows.Add( row );
		}

		rows.Add( new TextEntry
		{
			Key = "new-profile",
			Value = _newProfile.Value,
			Controlled = true,
			Placeholder = "New profile name, then Enter to sign in",
			AccessibilityName = "New Omp profile",
			MinHeight = theme.ControlHeight,
			Padding = new Edges( 0, theme.Spacing ),
			BackgroundColor = theme.Panel,
			BorderRadius = theme.Radius,
			OnChange = value => _context.Run( () => _newProfile.Value = value ),
			OnSubmit = value => _context.Run( () =>
			{
				var name = value.Trim();
				// Omp creates a profile the first time it is used; signing in is that first use.
				if ( name.Length == 0 || !_context.App.Settings.Set( OmpModule.ProfileName, scope, name ) )
					return;
				_newProfile.Value = "";
				_choosingAccount.Value = "";
				_ = UpdateLoginAsync( profile, name );
			} ),
		} );
		return new Container
		{
			Key = "accounts",
			Gap = theme.Spacing / 4,
			Padding = new Edges( 0, 0, 0, LabelWidth + theme.Spacing ),
			Children = rows,
		};
	}

	/// <summary>Builds the model picker for a profile; choosing saves the model for that profile's new chats.</summary>
	private Blob BuildModelPicker( SettingsProfile profile )
	{
		var scope = profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal;
		var chosen = _context.App.Settings.Get( OmpModule.Model, scope ).Trim();
		var fallback = _detection.Value.Profiles.FirstOrDefault( item => item.Name.Equals( _service.GetProfileName( profile ), StringComparison.OrdinalIgnoreCase ) )?.DefaultModel ?? "";
		var current = chosen.Length > 0 ? chosen : fallback;
		return Cell.Mount<OmpModelPickerInput, OmpModelPicker>( "model-picker", new(
			_service,
			profile,
			current,
			current,
			$"Use for {profile}",
			model => _context.Run( () =>
			{
				_choosingModel.Value = "";
				_context.App.Settings.Set( OmpModule.Model, scope, model.Selector );
			} ),
			null,
			null,
			() => _context.Run( () =>
			{
				_choosingModel.Value = "";
				OmpModule.OpenRouting( _context.App, profile );
			} ),
			() => _context.Run( () => _choosingModel.Value = "" ) ) );
	}

	/// <summary>Builds one labelled line: a status dot, a value with its detail, and actions on the right.</summary>
	private static Row BuildField( string label, Color? state, string value, string detail, List<Blob> actions )
	{
		var theme = Theme.Current;
		var line = BuildLine( state, value, detail, actions );
		line.Children.Insert( 0, new Text { Content = label, Width = LabelWidth, FlexShrink = 0, Color = theme.Muted } );
		line.AccessibilityName = label;
		return line;
	}

	/// <summary>Builds a status dot, an emphasized value that wraps only once the detail has no room left, a muted detail that shortens when narrow, and actions.</summary>
	internal static Row BuildLine( Color? state, string value, string detail, List<Blob> actions )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( state is { } color )
			children.Add( BuildDot( color ) );
		children.Add( new Text { Content = value, Color = theme.Text, MinWidth = 0 } );
		children.Add( new Text
		{
			Content = detail,
			FontSize = SmallSize,
			Color = theme.Muted,
			FlexGrow = 1,
			FlexShrink = 1,
			FlexBasis = 0,
			MinWidth = 0,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		} );
		children.AddRange( actions );
		return new Row
		{
			AccessibilityRole = AccessibilityRole.Group,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinHeight = theme.ControlHeight,
			Children = children,
		};
	}

	/// <summary>Builds the scrolling page that centers its content in a column no wider than <see cref="PageWidth"/>.</summary>
	internal static Container BuildColumn( List<Blob> page ) => new()
	{
		FlexGrow = 1,
		MinHeight = 0,
		Padding = ContentPadding,
		OverflowY = Overflow.Scroll,
		ScrollbarY = ViewStyle.BuildScrollbar(),
		AlignItems = AlignItems.Center,
		Children =
		[
			new Container
			{
				Width = Length.Percent( 100 ),
				MaxWidth = PageWidth,
				FlexShrink = 0,
				Gap = Theme.Current.Spacing * 2,
				Children = page,
			},
		],
	};

	/// <summary>Builds a raised card, outlined in the accent color when it is the profile setup was opened for.</summary>
	internal static Container BuildSurface( string key, bool isFocused, List<Blob> children ) => new()
	{
		Key = key,
		AccessibilityRole = AccessibilityRole.Group,
		AccessibilityName = key,
		Gap = Theme.Current.Spacing,
		Padding = new Edges( Theme.Current.Spacing * 1.5, Theme.Current.Spacing * 2 ),
		BackgroundColor = Theme.Current.Raised,
		BorderRadius = Theme.Current.Radius * 2,
		BorderWidth = Theme.Current.BorderWidth,
		BorderColor = isFocused ? Theme.Current.Accent : Theme.Current.Border,
		Children = children,
	};

	/// <summary>Builds a small status dot.</summary>
	internal static Container BuildDot( Color color ) => new()
	{
		Width = DotSize,
		Height = DotSize,
		FlexShrink = 0,
		BorderRadius = DotSize / 2.0,
		BackgroundColor = color,
	};

	/// <summary>Builds a compact action; a disabled one stays visible but muted.</summary>
	private Button BuildAction( string label, Action action, bool isEnabled = true )
	{
		var button = Theme.Current.BuildButton( label, () => _context.Run( action ), isEnabled );
		button.FlexShrink = 0;
		return button;
	}

	/// <summary>Describes an Omp profile's sign-in state, such as "anthropic · 21 models" or "Not signed in".</summary>
	internal static string DescribeAccount( OmpProfileInfo? account ) => account switch
	{
		null => "Not found",
		{ Error.Length: > 0 } => "Could not read: " + account.Error.Split( '\n' )[0],
		{ IsSignedIn: false } => "Not signed in",
		_ => $"{string.Join( ", ", account.Providers )} · {account.ModelCount} models",
	};

	/// <summary>Saves another Omp executable, or clears the setting to use the detected one, then detects again.</summary>
	private void UpdateExecutable( string? path )
	{
		if ( path is null || path.Length == 0 )
			_context.App.Settings.Reset( OmpModule.Executable, SettingsScope.Global );
		else
			_context.App.Settings.Set( OmpModule.Executable, SettingsScope.Global, path );
		_isEditingExecutable.Value = false;
		_ = UpdateDetectionAsync();
	}

	/// <summary>Opens the Sign in page for a profile and detects again once it signed in or was closed.</summary>
	private async Task UpdateLoginAsync( SettingsProfile profile, string profileName )
	{
		_signingIn.Value = profileName;
		try
		{
			await OmpModule.SignInAsync( _context.App, profile );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => _context.App.ReportError( "Omp sign-in", error ) );
		}
		_context.Post( () =>
		{
			_signingIn.Value = "";
			_ = UpdateDetectionAsync();
		} );
	}

	/// <summary>Installs Omp with its official installer, showing its last output line, then detects again.</summary>
	private async Task UpdateInstallAsync()
	{
		_isInstalling.Value = true;
		_installLine.Value = "";
		try
		{
			// A late line must not bring back the progress text once the install has finished.
			var progress = new Progress<string>( line => _context.Post( () => _installLine.Value = _isInstalling.Value ? line.Trim() : "" ) );
			await Task.Run( () => _service.InstallAsync( progress, _context.Lifetime ) );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => _context.App.ReportError( "Omp install", error ) );
		}
		_context.Post( () =>
		{
			_isInstalling.Value = false;
			_installLine.Value = "";
			_ = UpdateDetectionAsync();
		} );
	}

	/// <summary>Detects the install and profiles off the UI thread and shows the result.</summary>
	private async Task UpdateDetectionAsync()
	{
		if ( _isDetecting.Value )
			return;

		_isDetecting.Value = true;
		try
		{
			var detection = await Task.Run( () => _service.DetectAsync( _context.Lifetime ) );
			_context.Post( () => _detection.Value = detection );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => _detection.Value = new( null, error.Message, [] ) );
		}
		finally
		{
			_context.Post( () => _isDetecting.Value = false );
		}
	}

	/// <summary>Gets the profile to emphasize: the one restored, the one requested, or the active one.</summary>
	private static SettingsProfile ComputeInitialFocus( PanelContext context )
	{
		if ( context.RestoredState is { ValueKind: JsonValueKind.String } saved && Enum.TryParse<SettingsProfile>( saved.GetString(), out var restored ) )
			return restored;
		return context.Input is SettingsProfile input ? input : context.App.Settings.ActiveProfile;
	}
}
