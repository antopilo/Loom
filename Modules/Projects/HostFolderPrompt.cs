using Goo;
using Lokn.Core;

namespace Lokn.Modules.Projects;

/// <summary>Asks for a folder on a paired host, whose disk no native folder chooser shows: a dialog card with a path entry, checked on the host before it is taken.</summary>
/// <remarks>
/// <see cref="Show"/> opens the question for a panel and <see cref="Build"/>, which that panel's view includes in its build, mounts it while it is open;
/// one question is open per panel at a time. This PC's callers keep the native folder chooser. Everything happens on the UI thread.
/// </remarks>
public static class HostFolderPrompt
{
	/// <summary>Holds the open question of each panel, by the panel's identifier.</summary>
	private static readonly State<IReadOnlyDictionary<Guid, HostFolderRequest>> _requests = new( new Dictionary<Guid, HostFolderRequest>() );

	/// <summary>Asks for a folder on a host in a panel, replacing a question the panel already asked.</summary>
	/// <param name="context">The panel the question belongs to; its view mounts it through <see cref="Build"/>.</param>
	/// <param name="host">The host the folder is on.</param>
	/// <param name="title">The question, such as "Open a project folder".</param>
	/// <param name="initial">The path the entry starts with, or null for an empty one.</param>
	/// <param name="chosen">Runs on the UI thread with a path the host has a folder at.</param>
	public static void Show( PanelContext context, LoknHost host, string title, string? initial, Action<string> chosen ) =>
		_requests.Value = new Dictionary<Guid, HostFolderRequest>( _requests.Value ) { [context.Id] = new( context, host, title, initial ?? "", chosen ) };

	/// <summary>Builds a panel's open question, or null when it has none; reading it rebuilds the panel when a question opens or closes.</summary>
	/// <param name="context">The panel.</param>
	public static Blob? Build( PanelContext context )
	{
		if ( !_requests.Value.TryGetValue( context.Id, out var request ) )
			return null;

		// A question whose view closed goes with it.
		if ( request.Context.Lifetime.IsCancellationRequested )
		{
			context.Post( () => Close( request ) );
			return null;
		}

		return Cell.Mount<HostFolderRequest, HostFolderDialog>( "host-folder-prompt", request );
	}

	/// <summary>Closes a question, unless another one replaced it.</summary>
	internal static void Close( HostFolderRequest request )
	{
		if ( !_requests.Value.TryGetValue( request.Context.Id, out var open ) || open != request )
			return;

		var requests = new Dictionary<Guid, HostFolderRequest>( _requests.Value );
		requests.Remove( request.Context.Id );
		_requests.Value = requests;
	}
}

/// <summary>One open question for a folder on a host.</summary>
/// <param name="Context">The panel that asked.</param>
/// <param name="Host">The host the folder is on.</param>
/// <param name="Title">The question.</param>
/// <param name="Initial">The path the entry starts with.</param>
/// <param name="Chosen">Runs with the path once the host has a folder there.</param>
internal sealed record HostFolderRequest( PanelContext Context, LoknHost Host, string Title, string Initial, Action<string> Chosen );

/// <summary>The dialog of a <see cref="HostFolderRequest"/>: a card over a scrim, like <see cref="ConfirmDialog"/>, with the path entry, why a path was refused,
/// and Cancel and Choose; Enter chooses and Escape cancels.</summary>
internal sealed class HostFolderDialog : GuardedCell<HostFolderRequest>
{
	/// <summary>Defines the card's width in logical pixels.</summary>
	private const int CardWidth = 440;

	/// <summary>Defines the opacity of the scrim over the window.</summary>
	private const double ScrimOpacity = 0.45;

	/// <summary>Holds the typed path; it counts only once <see cref="_isTyped"/> is set, until then the entry shows the initial path.</summary>
	private readonly State<string> _path = new( "" );

	/// <summary>Holds whether the user typed in the entry, so the initial path no longer shows.</summary>
	private readonly State<bool> _isTyped = new( false );

	/// <summary>Holds why the last path was refused, or empty.</summary>
	private readonly State<string> _error = new( "" );

	/// <summary>Holds whether the host is checking the path now.</summary>
	private readonly State<bool> _isChecking = new( false );

	/// <summary>Targets the path entry, which holds focus when the dialog opens.</summary>
	private readonly ElementHandle _entry = new();

	/// <summary>Tracks the card's entrance from 0, faded out, scaled down and lowered, to 1 at rest.</summary>
	private Anim<double>? _appear;

	/// <summary>Starts the entrance on the first input.</summary>
	protected override void InputChanged( HostFolderRequest input ) => _appear ??= PopupMotion.Start( Animate( 0.0 ) );

	/// <summary>Hides native surfaces, such as browser pages, which would draw over the scrim and card.</summary>
	protected override void Mounted() => Input.Context.App.CoverNativeSurfaces( this, true );

	/// <summary>Shows native surfaces again.</summary>
	protected override void Unmounting() => Input.Context.App.CoverNativeSurfaces( this, false );

	/// <summary>Builds the scrim and the centered card.</summary>
	protected override Blob BuildView( HostFolderRequest input )
	{
		var theme = Theme.Current;
		var path = _isTyped.Value ? _path.Value : input.Initial;
		var isChecking = _isChecking.Value;
		var card = new List<Blob>
		{
			new Text { Key = "title", Content = input.Title, FontWeight = 600, FontSize = theme.FontSize + 1, Color = theme.Text },
			new Text { Key = "message", Content = $"Type the path of a folder on {input.Host.Name}.", Color = theme.Muted },
			new TextEntry
			{
				Key = "path",
				Handle = _entry,
				Value = path,
				Controlled = true,
				Placeholder = @"C:\path\to\folder",
				AccessibilityName = $"Folder on {input.Host.Name}",
				Height = theme.ControlHeight,
				OnChange = value =>
				{
					_path.Value = value;
					_isTyped.Value = true;
					_error.Value = "";
				},
				OnSubmit = _ => Choose(),
				KeyBindings = [new( Key.Escape, KeyModifiers.None, Cancel )],
			},
		};
		if ( _error.Value.Length > 0 )
			card.Add( new Text { Key = "error", Content = _error.Value, Color = theme.Error, Selectable = true } );

		var cancel = theme.BuildButton( "Cancel", Cancel, !isChecking );
		cancel.Key = "cancel";
		var choose = theme.BuildButton( isChecking ? "Checking…" : "Choose", Choose, !isChecking && path.Trim().Length > 0 );
		choose.Key = "accept";
		choose.BackgroundColor = theme.Accent.WithAlpha( 0.22 );
		card.Add( new Row
		{
			Key = "buttons",
			Margin = new Edges( theme.Spacing, 0, 0, 0 ),
			Gap = theme.Spacing,
			JustifyContent = JustifyContent.FlexEnd,
			Children = [cancel, choose],
		} );

		// The portal fills the window and centers the card; the modal scope keeps focus and input inside it.
		var portal = new Portal
		{
			Key = "host-folder-dialog",
			Left = 0,
			Top = 0,
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Scrim = Color.Black.WithAlpha( ScrimOpacity ),
			FocusScope = new FocusScopeOptions
			{
				Modal = true,
				InitialFocus = _entry,
			},
			AccessibilityRole = AccessibilityRole.Dialog,
			AccessibilityName = input.Title,
			OnDismiss = _ => Cancel(),
		};
		var surface = new Container
		{
			Key = "card",
			Width = CardWidth,
			MaxWidth = Length.Percent( 90 ),
			Padding = theme.Spacing * 2,
			Gap = theme.Spacing,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + 3,
			BoxShadow = new BoxShadow { OffsetY = 6, Blur = 18, Color = Color.Black.WithAlpha( 0.35 ) },
			Children = card,
		};
		PopupMotion.Apply( surface, _appear?.Value ?? 1, PopupMotion.DialogRise );
		portal.Add( surface );
		return portal;
	}

	/// <summary>Closes the question without a folder, unless the host is checking one now.</summary>
	private void Cancel()
	{
		if ( !_isChecking.Value )
			HostFolderPrompt.Close( Input );
	}

	/// <summary>Asks the host whether the typed path is a folder; the question closes with it when it is.</summary>
	private void Choose()
	{
		var path = (_isTyped.Value ? _path.Value : Input.Initial).Trim();
		if ( _isChecking.Value || path.Length == 0 )
			return;

		_isChecking.Value = true;
		_error.Value = "";
		_ = CheckAsync( Input, path );
	}

	/// <summary>Checks a path on the host off the UI thread, then takes it or says why not.</summary>
	private async Task CheckAsync( HostFolderRequest request, string path )
	{
		string? refusal;
		try
		{
			refusal = await request.Host.Workspace.DirectoryExistsAsync( path, request.Context.Lifetime ).ConfigureAwait( false )
				? null
				: $"{request.Host.Name} has no folder at {path}.";
		}
		catch ( OperationCanceledException )
		{
			return;
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			refusal = error.Message;
		}

		request.Context.Post( () =>
		{
			_isChecking.Value = false;
			if ( refusal is not null )
			{
				_error.Value = refusal;
				return;
			}

			HostFolderPrompt.Close( request );
			request.Chosen( path );
		} );
	}
}
