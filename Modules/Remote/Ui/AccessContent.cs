using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Remote access, the only row of Settings › Remote and the editor of <see cref="RemoteModule.Access"/>, laid out as Tern's: Serve over, Pair,
/// Devices, and the PCs this one connects to.</summary>
/// <remarks>
/// In the <c>daemon</c> role it reads the daemon through <c>admin.*</c> requests over the pipe, refreshing every few seconds while shown, and shows
/// the note Loom leaves when it moved This PC's chats by itself. A request made with <see cref="RemoteModule.OpenAccess"/> focuses the pairing link
/// field or pairs with a link at once.
/// </remarks>
internal sealed partial class AccessContent : GuardedCell<RemoteModule>
{
	/// <summary>Defines the request that focuses the pairing link field.</summary>
	internal const string AddHostRequest = "add-host";

	/// <summary>Defines the section heading size in logical pixels.</summary>
	private const int SectionSize = 15;

	/// <summary>Defines the width of text entries in logical pixels.</summary>
	private const int EntryWidth = 320;

	/// <summary>Defines how often the view refreshes its countdown and, in the daemon role, the daemon's status, in seconds.</summary>
	private const double Tick = 1;

	/// <summary>Stores the view's guarded dispatch and lifetime; the first build sets it, before any callback can run.</summary>
	private ViewContext _context = null!;

	/// <summary>Stores the module; every build sets it, the first before any callback can run.</summary>
	private RemoteModule _module = null!;

	/// <summary>Counts changes that need a rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the last error or result line, or empty.</summary>
	private string _message = "";

	/// <summary>Stores whether the last message is an error.</summary>
	private bool _isError;

	/// <summary>Stores whether a request runs, which disables the buttons that send one.</summary>
	private bool _isBusy;

	/// <summary>Builds the notice and result line, Serve over, Pair, Devices with the pairings waiting for an answer, and the PCs this one connects to,
	/// after taking a request <see cref="RemoteModule.OpenAccess"/> made.</summary>
	protected override Blob BuildView( RemoteModule module )
	{
		_module = module;
		_context ??= new ViewContext( module.Context.App, Lifetime, action => Post( action ) );
		_ = _version.Value;
		if ( module.AccessRequest is { Length: > 0 } request )
			Post( () => TakeRequest( request ) );

		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( module.Notice is { } notice )
		{
			children.Add( BuildParagraph( notice, theme.Attention, selectable: true ) );
			children.Add( theme.BuildButton( "Dismiss", () => _context.Run( module.DismissNotice ) ) );
		}
		if ( _message.Length > 0 )
			children.Add( BuildParagraph( _message, _isError ? theme.Error : theme.Success, selectable: true ) );

		children.AddRange( BuildServe( theme ) );
		children.AddRange( BuildPair( theme ) );
		children.AddRange( BuildDevices( theme ) );
		children.AddRange( BuildPending( theme ) );
		children.AddRange( BuildHosts( theme ) );
		return new Container { Gap = theme.Spacing, Children = children };
	}

	/// <summary>Reads the daemon's status at once, then ticks every second for the pairing countdown and the status refresh, while the view shows.</summary>
	protected override void Mounted()
	{
		UpdateDaemonStatus();
		Every( Tick, UpdateTick );
	}

	/// <summary>Withdraws the pairing offer the view shows, since its link works only while it is shown here; the daemon also withdraws it once this Loom's
	/// connection closes, such as when Loom exits before the request goes out.</summary>
	protected override void Unmounting()
	{
		if ( _pairing is null || _module is not { Daemon: { } client } )
			return;

		_pairing = null;
		_ = WithdrawPairingAsync( client );
	}

	/// <summary>Takes a request <see cref="RemoteModule.OpenAccess"/> made: <see cref="AddHostRequest"/> focuses the pairing link field, and a
	/// <c>loom://pair#…</c> link pairs at once, as when a browser hands one to Loom.</summary>
	private void TakeRequest( string request )
	{
		if ( _module.TakeAccessRequest() != request )
			return;

		if ( request == AddHostRequest )
			_isAddFocused = true;
		else if ( request.StartsWith( PairingLink.UriPrefix, StringComparison.OrdinalIgnoreCase ) )
		{
			_hostLink = request;
			UpdateAddHost();
		}
		_version.Value++;
	}

	/// <summary>Builds a section heading.</summary>
	private static Text BuildSection( string title, Theme theme ) => new()
	{
		Content = title,
		FontSize = SectionSize,
		Margin = new Edges( theme.Spacing, 0, 0, 0 ),
	};

	/// <summary>Builds a wrapping paragraph.</summary>
	private static Text BuildParagraph( string text, Color color, bool selectable = false ) => new()
	{
		Content = text,
		Color = color,
		TextWrap = TextWrap.Wrap,
		Selectable = selectable,
	};

	/// <summary>Builds a small round dot.</summary>
	internal static Container BuildDot( Color color ) => new()
	{
		Width = 8,
		Height = 8,
		FlexShrink = 0,
		BorderRadius = 4,
		BackgroundColor = color,
	};

	/// <summary>Builds a labelled text entry, focused when it first shows if <paramref name="autoFocus"/> is set.</summary>
	private Row BuildEntry( string key, string label, string value, string placeholder, Action<string> change, Action<string>? submit = null, bool autoFocus = false )
	{
		var theme = Theme.Current;
		return new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Children =
			[
				new Text { Content = label, Width = 90, FlexShrink = 0, },
				new TextEntry
				{
					Key = key,
					Value = value,
					Controlled = true,
					AutoFocus = autoFocus,
					Placeholder = placeholder,
					AccessibilityName = label,
					Width = EntryWidth,
					MinWidth = 0,
					FlexShrink = 1,
					Height = theme.ControlHeight,
					Padding = new Edges( 0, theme.Spacing ),
					BackgroundColor = theme.Raised,
					BorderColor = theme.Border,
					BorderWidth = theme.BorderWidth,
					BorderRadius = theme.Radius,
					FocusStyle = new Style { BorderColor = theme.Accent },
					OnChange = text => _context.Run( () =>
					{
						change( text );
						_version.Value++;
					} ),
					OnSubmit = submit is null ? null : text => _context.Run( () => submit( text ) ),
				},
			],
		};
	}

	/// <summary>Shows a result or error line.</summary>
	private void ShowMessage( string message, bool isError )
	{
		_message = message;
		_isError = isError;
		_version.Value++;
	}

	/// <summary>Runs a request in the background with the buttons disabled, showing its failure, and rebuilds once it ends.</summary>
	/// <param name="request">The request; it starts on the UI thread and its continuations post back through the view.</param>
	/// <param name="success">The line shown when it succeeds, or null to keep the current one.</param>
	private void RunRequest( Func<Task> request, string? success = null ) => _context.Run( () =>
	{
		if ( _isBusy )
			return;

		_isBusy = true;
		_message = "";
		_version.Value++;
		_ = RunRequestAsync( request, success );
	} );

	/// <summary>Awaits a request and shows its result on the UI thread; any failure, such as a damaged pairing link's, shows as an error and frees the buttons.</summary>
	private async Task RunRequestAsync( Func<Task> request, string? success )
	{
		string? failure = null;
		try
		{
			await request().ConfigureAwait( false );
		}
		catch ( Exception exception )
		{
			failure = exception.Message;
		}

		_context.Post( () =>
		{
			_isBusy = false;
			if ( failure is not null )
				ShowMessage( failure, true );
			else if ( success is not null )
				ShowMessage( success, false );
			else
				_version.Value++;
		} );
	}

	/// <summary>The view's application, lifetime and guarded dispatch, as a panel's context gives a panel.</summary>
	/// <param name="App">The application.</param>
	/// <param name="Lifetime">Cancelled once the view is gone.</param>
	/// <param name="Dispatch">Posts an action to the UI thread while the view is shown.</param>
	private sealed record ViewContext( LoknApplication App, CancellationToken Lifetime, Action<Action> Dispatch )
	{
		/// <summary>Runs a callback on the UI thread while the view is shown, reporting its failure instead of throwing.</summary>
		public void Run( Action action )
		{
			if ( Lifetime.IsCancellationRequested )
				return;

			try
			{
				action();
			}
			catch ( Exception error ) { App.ReportError( "Remote access", error ); }
		}

		/// <summary>Posts guarded work from a background task to the UI thread.</summary>
		public void Post( Action action ) => Dispatch( () => Run( action ) );
	}
}
