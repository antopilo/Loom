using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The Remote access panel: <b>This PC</b>, where its chats run and, with the daemon, its status, network, pairing, devices and the pairings
/// waiting for an answer; and <b>Hosts</b>, the hosts this PC paired with, each renamed, reconnected or removed here, and the form that adds one.</summary>
/// <remarks>
/// Choosing where This PC's chats run moves them while Loom runs: the panel shows the agents the move waits for, with Cancel, and the note Loom leaves
/// when it moved them by itself. In the <c>daemon</c> role it manages the daemon through <c>admin.*</c> requests over the pipe, refreshing every few
/// seconds while open. Opened with the input <c>add-host</c> it focuses the pairing link field; <c>hosts</c> opens it as it is. The network modes and
/// paired hosts are marked as previews.
/// </remarks>
public sealed partial class AccessContent : PanelContent
{
	/// <summary>Defines the panel type id within the module.</summary>
	public const string PanelId = "access";

	/// <summary>Defines the panel type id, <c>remote.access</c>, which other modules and the daemon's menu open.</summary>
	public const string TypeId = "remote." + PanelId;

	/// <summary>Defines the input that opens the panel with the Add host field focused.</summary>
	public const string AddHostInput = "add-host";

	/// <summary>Defines the panel title.</summary>
	public const string Title = "Remote access";

	/// <summary>Defines the panel content inset in logical pixels.</summary>
	private const int ContentPadding = 16;

	/// <summary>Defines the heading size in logical pixels.</summary>
	private const int HeadingSize = 18;

	/// <summary>Defines the section heading size in logical pixels.</summary>
	private const int SectionSize = 15;

	/// <summary>Defines the switch track's width in logical pixels.</summary>
	private const int SwitchWidth = 40;

	/// <summary>Defines the switch track's height in logical pixels.</summary>
	private const int SwitchHeight = 22;

	/// <summary>Defines the gap between the switch track and its knob in logical pixels.</summary>
	private const int KnobInset = 3;

	/// <summary>Defines the width of text entries in logical pixels.</summary>
	private const int EntryWidth = 320;

	/// <summary>Defines how often the panel refreshes its countdown and, in the daemon role, the daemon's status.</summary>
	private static readonly TimeSpan Tick = TimeSpan.FromSeconds( 1 );

	/// <summary>Lists the roles a user picks from for This PC's chats, with their labels and explanations.</summary>
	private static readonly (string Role, string Label, string Text)[] RoleChoices =
	[
		(RemoteModule.LocalRole, "This Loom", "Chats run in this window and stop when it closes. Loom stops the daemon on this PC when it starts this way."),
		(RemoteModule.DaemonRole, "Daemon on this PC", "Chats run in a background Loom that keeps working when this window closes, and other devices can reach them."),
	];

	/// <summary>Stores the panel's guarded dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the module.</summary>
	private readonly RemoteModule _module;

	/// <summary>Counts changes that need a rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the last error or result line, or empty.</summary>
	private string _message = "";

	/// <summary>Stores whether the last message is an error.</summary>
	private bool _isError;

	/// <summary>Stores whether a request runs, which disables the buttons that send one.</summary>
	private bool _isBusy;

	/// <summary>Creates the panel and, in the daemon role, starts refreshing the daemon's status.</summary>
	/// <param name="context">The panel's context; its input <see cref="AddHostInput"/> focuses the pairing link field.</param>
	/// <param name="module">The module.</param>
	public AccessContent( PanelContext context, RemoteModule module )
	{
		_context = context;
		_module = module;
		_isAddFocused = context.Input as string == AddHostInput;
		_ = RunTicksAsync();
	}

	/// <summary>Builds the This PC section, with the daemon's section in the daemon role, and the Hosts section.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Text { Content = Title, FontSize = HeadingSize, },
		};
		if ( _module.Notice is { } notice )
		{
			children.Add( BuildParagraph( notice, theme.Attention, selectable: true ) );
			children.Add( theme.BuildButton( "Dismiss", () => _context.Run( _module.DismissNotice ) ) );
		}
		if ( _message.Length > 0 )
			children.Add( BuildParagraph( _message, _isError ? theme.Error : theme.Success, selectable: true ) );

		children.AddRange( BuildRoles( theme ) );
		if ( _module.RunningRole == RemoteModule.DaemonRole )
			children.AddRange( BuildDaemon( theme ) );
		children.AddRange( BuildHosts( theme ) );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = Title,
			FlexGrow = 1,
			MinHeight = 0,
			Padding = ContentPadding,
			Gap = theme.Spacing,
			OverflowY = Overflow.Scroll,
			Children = children,
		};
	}

	/// <summary>Withdraws the pairing offer the panel shows, since its link works only while it is shown here; the daemon also withdraws it once this Loom's
	/// connection closes, such as when Loom exits before the request goes out.</summary>
	public override void Closed()
	{
		if ( _pairing is null || _module.Daemon is not { } client )
			return;

		_pairing = null;
		_ = WithdrawPairingAsync( client );
	}

	/// <summary>Builds the This PC section's role choice: one button per role, the chosen one marked, and while the chats move, what the move waits for
	/// with Cancel.</summary>
	private IEnumerable<Blob> BuildRoles( Theme theme )
	{
		var chosen = _context.App.Settings.Get( RemoteModule.Role, SettingsScope.Global );
		yield return BuildSection( "This PC", theme );
		yield return BuildParagraph( "Chats on this PC run in", theme.Muted );
		foreach ( var (role, label, text) in RoleChoices )
		{
			var isChosen = role == chosen;
			void Choose() => _context.Run( () => _context.App.Settings.Set( RemoteModule.Role, SettingsScope.Global, role ) );
			yield return new Button
			{
				Key = "role:" + role,
				Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
				BorderRadius = theme.Radius,
				BorderWidth = theme.BorderWidth,
				BorderColor = isChosen ? theme.Accent : theme.Border,
				Focusable = true,
				Cursor = Cursor.Pointer,
				AccessibilityName = $"{label}{(isChosen ? ", chosen" : "")}",
				OnClick = Choose,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, Choose ), new( Key.Space, KeyModifiers.None, Choose )],
				HoverStyle = new Style { BackgroundColor = theme.Hover, },
				FocusStyle = new Style { BackgroundColor = theme.Hover, },
				Children =
				[
					new Text { Content = label, Color = isChosen ? theme.Accent : theme.Text, },
					BuildParagraph( text, theme.Muted ),
				],
			};
		}

		if ( _module.Switching is not { } pending )
			yield break;

		var target = RoleChoices.Where( choice => choice.Role == pending.Role ).Select( choice => choice.Label ).FirstOrDefault() ?? pending.Role;
		var waiting = _module.CountWaiting();
		if ( !pending.IsWaiting || waiting == 0 )
		{
			yield return BuildParagraph( $"Moving this PC's chats to \u201c{target}\u201d\u2026", theme.Attention );
			yield break;
		}

		yield return BuildParagraph( $"Waiting for {waiting} {(waiting == 1 ? "agent" : "agents")} to finish. Chats then move to \u201c{target}\u201d; the chat panels stay open.",
			theme.Attention );
		var cancel = theme.BuildButton( "Cancel", () => _context.Run( _module.CancelSwitch ) );
		cancel.AccessibilityName = "Cancel moving the chats";
		yield return cancel;
	}

	/// <summary>Builds a dot in the connection's color and its status line.</summary>
	private static Row BuildStatusRow( RemoteClient client, Theme theme ) => new()
	{
		AlignItems = AlignItems.Center,
		Gap = theme.Spacing,
		Children =
		[
			BuildDot( RemoteStatusContent.ComputeColor( client.Status, theme ) ),
			new Text { Content = client.StatusText, Selectable = true, FlexShrink = 1, TextWrap = TextWrap.Wrap, },
		],
	};

	/// <summary>Builds a section heading, with a <see cref="BuildPreviewTag">Preview</see> tag for a feature still in preview.</summary>
	private static Blob BuildSection( string title, Theme theme, bool isPreview = false )
	{
		var margin = new Edges( theme.Spacing, 0, 0, 0 );
		if ( !isPreview )
			return new Text { Content = title, FontSize = SectionSize, Margin = margin, };

		return new Row
		{
			Margin = margin,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Children = [new Text { Content = title, FontSize = SectionSize, }, BuildPreviewTag( theme )],
		};
	}

	/// <summary>Builds the small muted tag that marks a feature still in preview.</summary>
	private static Text BuildPreviewTag( Theme theme ) => new()
	{
		Content = "Preview",
		Color = theme.Muted,
		FontSize = theme.FontSize - 2,
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

	/// <summary>Builds an on and off switch with its label: the track in the accent color with the knob at the end when on.</summary>
	private Row BuildSwitch( string label, bool isOn, bool isEnabled, Action toggle )
	{
		var theme = Theme.Current;
		void Toggle() => _context.Run( toggle );
		const int knob = SwitchHeight - KnobInset * 2;
		return new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Children =
			[
				new Button
				{
					Width = SwitchWidth,
					Height = SwitchHeight,
					FlexShrink = 0,
					Padding = KnobInset,
					FlexDirection = FlexDirection.Row,
					JustifyContent = isOn ? JustifyContent.FlexEnd : JustifyContent.FlexStart,
					BorderRadius = SwitchHeight / 2.0,
					BackgroundColor = isOn ? theme.Accent : theme.Hover,
					Opacity = isEnabled ? 1 : 0.4,
					Focusable = isEnabled,
					Cursor = isEnabled ? Cursor.Pointer : Cursor.Default,
					AccessibilityName = $"{label}: {(isOn ? "on" : "off")}",
					OnClick = isEnabled ? Toggle : null,
					KeyBindings = isEnabled ? [new( Key.Enter, KeyModifiers.None, Toggle ), new( Key.Space, KeyModifiers.None, Toggle )] : [],
					FocusStyle = new Style { BorderColor = theme.Text, BorderWidth = theme.BorderWidth },
					Children = [new Container { Width = knob, Height = knob, BorderRadius = knob / 2.0, BackgroundColor = isOn ? theme.Canvas : theme.Muted }],
				},
				new Text { Content = label, },
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
	/// <param name="request">The request; it starts on the UI thread and its continuations post back through the panel.</param>
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

	/// <summary>Reads the daemon's status at once, then ticks every second for the pairing countdown and the status refresh, while the panel is open.</summary>
	private async Task RunTicksAsync()
	{
		_context.Post( UpdateDaemonStatus );
		try
		{
			while ( !_context.Lifetime.IsCancellationRequested )
			{
				await Task.Delay( Tick, _context.Lifetime ).ConfigureAwait( false );
				_context.Post( UpdateTick );
			}
		}
		catch ( OperationCanceledException )
		{
		}
	}
}
