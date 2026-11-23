using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Builds the bottom-docked Omp model rail and prompt editor.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the prompt frame inset in logical pixels.</summary>
	private static int PromptInset => Theme.Current.Spacing;

	/// <summary>Defines the prompt line height as a multiple of its font size.</summary>
	private const double PromptLineHeight = 1.5;

	/// <summary>Defines how many lines the prompt grows to before it scrolls.</summary>
	private const int PromptMaxLines = 8;

	/// <summary>Stores the visible prompt line count of the last build, so typing rebuilds only when the editor must grow or shrink.</summary>
	private int _promptLines = 1;

	/// <summary>Stores the prompt's last measured row count, wrapped rows included.</summary>
	private int _promptRows = 1;

	/// <summary>Stores whether the prompt held text at the last build, so typing rebuilds only when the send button lights up or dims.</summary>
	private bool _promptHasText;

	/// <summary>Anchors the provider usage and reset popover to its prompt-rail button.</summary>
	private readonly ElementHandle _usageHandle = new();

	/// <summary>Stores the latest provider usage report shown by this chat.</summary>
	private OmpUsageReport? _usageReport;

	/// <summary>Reports whether the provider usage and reset popover is open.</summary>
	private bool _isUsageOpen;

	/// <summary>Reports whether this chat is refreshing provider usage.</summary>
	private bool _isUsageBusy;

	/// <summary>Stores the last provider usage loading error, or empty.</summary>
	private string _usageError = "";

	/// <summary>Defines the blur radius of the transcript under the floating prompt, in logical pixels.</summary>
	private const double FloatingBlur = 6;

	/// <summary>Defines how much of the panel color veils the transcript under the floating prompt; the blurred text still shows through faintly.</summary>
	private const double FloatingVeil = 0.5;

	/// <summary>Defines the height of the fade above the floating prompt, where the transcript dims over a few lines before it blurs.</summary>
	private static int FloatingFade => Theme.Current.Spacing * 5;

	/// <summary>Retains the floating prompt, whose height the transcript pads its end by.</summary>
	private readonly ElementHandle _floating = new();

	/// <summary>Tracks the floating prompt's height, fade included, in logical pixels; views that read it rebuild when it changes.</summary>
	private readonly State<int> _floatingHeight = new( ComposerHeight );

	/// <summary>Tracks whether the floating prompt's height is being followed.</summary>
	private bool _isFloatingWatched;

	/// <summary>Gets the floating prompt's height, fade included, subscribing the building view.</summary>
	private int ReadFloatingHeight() => _floatingHeight.Value;

	/// <summary>Builds the prompt and the question above it floating over the transcript's end: a fade, then the transcript blurred and veiled under them.</summary>
	/// <remarks>Only the controls take the pointer; the wheel and clicks elsewhere reach the transcript beneath.</remarks>
	private Container BuildFloating( List<Blob> stack )
	{
		if ( !_isFloatingWatched )
		{
			_isFloatingWatched = true;
			_floating.MetricsChanged += metrics => context.Run( () => UpdateFloatingHeight( metrics ) );
		}

		var veil = Theme.Current.Panel.WithAlpha( FloatingVeil );
		return new Container
		{
			Key = "conversation-floating",
			Handle = _floating,
			Position = PositionType.Absolute,
			// Inset by the scrollbar's track on both sides: the thumb stays uncovered at the end, and the prompt stays centered.
			Left = ViewStyle.TrackWidth,
			Right = ViewStyle.TrackWidth,
			Bottom = 0,
			HitTestSelf = false,
			Children =
			[
				new Container
				{
					Key = "conversation-floating-fade",
					Height = FloatingFade,
					FlexShrink = 0,
					HitTestSelf = false,
					BackgroundGradient = new LinearGradient( Theme.Current.Panel.WithAlpha( 0 ), veil ),
				},
				new Container
				{
					Key = "conversation-floating-body",
					HitTestSelf = false,
					BackdropBlur = FloatingBlur,
					BackgroundColor = veil,
					Children = stack,
				},
			],
		};
	}

	/// <summary>Records the floating prompt's height once it is laid out.</summary>
	private void UpdateFloatingHeight( ElementMetrics metrics )
	{
		if ( !metrics.IsMounted )
			return;

		var height = (int)Math.Ceiling( metrics.BorderBox.Height );
		if ( _floatingHeight.Value != height )
			_floatingHeight.Value = height;
	}

	/// <summary>Builds a floating prompt box with a quiet folder, git, model and status line beneath it, and the pinned todo list above it when given.</summary>
	private Blob BuildComposer( ConversationInfo conversation, Blob? todo )
	{
		var scope = conversation.Profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal;
		var model = session.ConnectedModel.Length > 0 ? session.ConnectedModel : context.App.Settings.Get( OmpModule.Model, scope );
		var configure = BuildRailButton( model.Length > 0 ? backend.GetModelName( conversation.Profile, model ) : "Choose model", () => context.Run( () => UpdatePicking( true ) ) );
		configure.Key = "conversation-model";
		configure.AccessibilityName = "Switch model";
		var status = session.Status;
		var isBusy = session.IsBusy;
		var isReady = status == "Ready";
		var working = BuildWorking();
		// The chat's folder and git state lead the rail; the status takes the free middle and the model stays at the right.
		var rail = BuildFolderRail( conversation );
		// The working indicator above the prompt tells the phase while it shows; a new status cross-fades over the last.
		rail.Add( Cell.Mount<RollingTextInput, RollingText>( "conversation-status", new( isReady || working is not null ? "" : status, ViewStyle.SmallSize, isBusy ? Theme.Current.Accent : Theme.Current.Muted )
		{
			FontFamily = ViewStyle.FontFamily,
			MaxLines = 3,
			Grow = true,
		} ) );
		rail.Add( BuildUsage( conversation ) );
		// The Agent Hub opens over the chat's own Omp process, which a chat running in a host keeps there.
		if ( !session.IsRemote )
		{
			var agents = BuildRailButton( "Agents", () => context.Run( UpdateOpenAgents ) );
			agents.Key = "conversation-agents";
			agents.AccessibilityName = "Open Omp Agent Hub";
			rail.Add( agents );
		}
		if ( BuildEffort() is { } effort )
			rail.Add( effort );
		rail.Add( configure );
		if ( !session.IsConnected && !isBusy )
		{
			var connect = BuildRailButton( "Connect", () => context.Run( session.UpdateConnect ) );
			connect.Key = "conversation-connect";
			connect.Color = Theme.Current.Text;
			rail.Add( connect );
		}

		var frame = new List<Blob>();
		if ( BuildQueue() is { } queue )
			frame.Add( queue );
		if ( _attachments.Count > 0 )
			frame.Add( BuildAttachments() );
		frame.Add( new Row
		{
			Key = "conversation-prompt-row",
			AlignItems = AlignItems.FlexEnd,
			Gap = Theme.Current.Spacing,
			Children = isBusy && session.IsConnected
				? [BuildAttachButton(), BuildPrompt(), BuildSendMode(), BuildSubmit()]
				: [BuildAttachButton(), BuildPrompt(), BuildSubmit()],
		} );
		var column = new List<Blob>();
		if ( todo is not null )
			column.Add( todo );
		if ( working is not null )
			column.Add( working );
		UpdateCommandQuery();
		if ( _commandQuery is { } query )
			column.Add( BuildCommands( query ) );
		if ( BuildNewTaskCard() is { } newTask )
			column.Add( newTask );
		if ( BuildPlanActions() is { } plan )
			column.Add( plan );
		if ( BuildGoalBar() is { } goal )
			column.Add( goal );
		var frameBox = new List<Blob>
		{
			new Container
			{
				Key = "conversation-prompt-frame",
				Padding = Theme.Current.Spacing * 3 / 4,
				Gap = Theme.Current.Spacing / 2,
				BorderColor = _isFileDragOver ? Theme.Current.Accent : Theme.Current.Border,
				BorderWidth = Theme.Current.BorderWidth,
				BorderRadius = ChatStyle.BubbleRadius,
				BackgroundColor = Theme.Current.Raised,
				// A soft shadow lifts the prompt off the transcript it floats over.
				BoxShadow = new BoxShadow { OffsetY = 4, Blur = 16, Color = Color.Black.WithAlpha( 0.25 ) },
				DropTarget = new DropTarget( QueryFileDrop, item => context.Run( () => UpdateFileDrop( item ) ) ),
				Children = frame,
			},
		};
		// The context meter is the frame's top border: it lies over that edge, placed against this unpadded box, and draws last.
		if ( BuildContextMeter() is { } meter )
			frameBox.Add( meter );
		column.AddRange(
		[
			new Container
			{
				Key = "conversation-prompt-box",
				Children = frameBox,
			},
			new Row
			{
				Key = "conversation-model-rail",
				AlignItems = AlignItems.Center,
				Gap = Theme.Current.Spacing,
				Padding = new Edges( 0, Theme.Current.Spacing ),
				Children = rail,
			},
		] );
		return new Container
		{
			// Stable keys keep conditional rail and attachment children from remounting the editor's controller.
			Key = "conversation-composer",
			FlexShrink = 0,
			Padding = new Edges( 0, ContentPadding, PromptInset, ContentPadding ),
			Children = [BuildColumn( "conversation-composer-column", column, Theme.Current.Spacing / 2 )],
		};
	}

	/// <summary>Opens the native Agent Hub, connecting the chat first when necessary.</summary>
	private void UpdateOpenAgents()
	{
		if ( session.Connected is { } connected )
		{
			OmpModule.OpenAgentHub( context.App, connected, context.Id );
			return;
		}

		if ( _isOpeningAgents )
			return;
		_isOpeningAgents = true;
		session.UpdateStatus( "Connecting Agent Hub…" );
		_ = UpdateOpenAgentsAsync();
	}

	/// <summary>Waits for the chat connection, then opens its Agent Hub on the panel thread.</summary>
	private async Task UpdateOpenAgentsAsync()
	{
		try
		{
			var connected = await session.EnsureConnection().ConfigureAwait( false );
			context.Post( () =>
			{
				_isOpeningAgents = false;
				OmpModule.OpenAgentHub( context.App, connected, context.Id );
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			context.Post( () =>
			{
				_isOpeningAgents = false;
				session.UpdateStatus( error.Message );
			} );
		}
	}

	/// <summary>Builds the prompt-rail usage button and its on-demand provider limit popover.</summary>
	private Blob BuildUsage( ConversationInfo conversation )
	{
		var label = BuildUsageLabel();
		var button = BuildRailButton( label, () => context.Run( () => UpdateUsageOpen( conversation.Profile ) ) );
		button.Key = "conversation-usage";
		button.Handle = _usageHandle;
		button.FlexShrink = 0;
		button.AccessibilityName = _isUsageOpen ? "Close usage and resets" : "Open usage and resets";
		button.KeyBindings = [new( Key.Escape, KeyModifiers.None, () => context.Run( () => UpdateUsageOpen( conversation.Profile, close: true ) ) )];
		var children = new List<Blob> { button };
		if ( _isUsageOpen )
			children.Add( BuildUsagePanel( conversation.Profile ) );
		return new Container
		{
			Key = "conversation-usage-control",
			FlexShrink = 0,
			Children = children,
		};
	}

	/// <summary>Builds the compact rail label from the most spent limit for the connected provider.</summary>
	private string BuildUsageLabel()
	{
		if ( _usageReport is null )
			return _isUsageBusy ? "Usage…" : "Usage";
		var model = session.ConnectedModel;
		var separator = model.IndexOf( '/' );
		var provider = separator > 0 ? model[..separator] : "";
		OmpUsageLimit? mostUsed = null;
		foreach ( var account in _usageReport.Accounts )
		{
			if ( provider.Length > 0 && !string.Equals( account.Provider, provider, StringComparison.OrdinalIgnoreCase ) )
				continue;
			foreach ( var limit in account.Limits )
			{
				if ( mostUsed is null || limit.UsedFraction > mostUsed.UsedFraction )
					mostUsed = limit;
			}
		}
		if ( mostUsed is null && provider.Length > 0 )
		{
			foreach ( var limit in _usageReport.Accounts.SelectMany( account => account.Limits ) )
			{
				if ( mostUsed is null || limit.UsedFraction > mostUsed.UsedFraction )
					mostUsed = limit;
			}
		}
		return mostUsed is { } used ? $"Usage {Math.Round( (1 - used.UsedFraction) * 100 )}% left" : "Usage";
	}

	/// <summary>Builds provider accounts, limit bars, reset times, refresh state and errors above the usage button.</summary>
	private Blob BuildUsagePanel( SettingsProfile profile )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = "Usage and resets", FontWeight = 600, FlexGrow = 1, },
					theme.BuildButton( _isUsageBusy ? "Refreshing…" : "Refresh", () => context.Run( () => UpdateUsageRefresh( profile ) ), isEnabled: !_isUsageBusy ),
				],
			},
			new Text
			{
				Content = _usageReport is { } report ? $"{profile} profile · updated {report.FetchedAt.ToLocalTime():t}" : $"{profile} profile",
				Color = theme.Muted,
			},
		};
		if ( _usageError.Length > 0 )
			children.Add( new Text { Content = _usageError, Color = theme.Error, Selectable = true } );
		if ( _usageReport is { Accounts.Count: 0 } )
			children.Add( new Text { Content = "No signed-in account of this profile reports usage limits.", Color = theme.Muted } );
		foreach ( var account in _usageReport?.Accounts ?? [] )
			children.Add( OmpUsageContent.BuildAccount( account, DateTimeOffset.Now ) );
		if ( _usageReport is null && _usageError.Length == 0 )
			children.Add( new Text { Content = "Reading provider usage…", Color = theme.Muted } );

		// A native popup draws over native child surfaces, such as browser pages, which a portal cannot; Escape or a press outside closes it.
		var panel = new Container
		{
			Width = 560,
			MaxHeight = 420,
			Padding = theme.Spacing * 1.5,
			Gap = theme.Spacing,
			OverflowY = Overflow.Scroll,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			Color = theme.Text,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			Children = children,
		};
		return Cell.Mount<NativePopupInput, NativePopup>( "usage-panel", new( _usageHandle, panel, () => context.Run( () => UpdateUsageOpen( profile, close: true ) ) )
		{
			Placement = PortalPlacement.TopEnd,
			Kind = PopupKind.Menu,
			Maximum = new LayoutSize( 560, 420 ),
			Role = AccessibilityRole.Dialog,
			Name = "Usage and resets",
		} );
	}

	/// <summary>Opens or closes usage; opening shows the profile cache immediately and refreshes it in the background.</summary>
	private void UpdateUsageOpen( SettingsProfile profile, bool close = false )
	{
		_isUsageOpen = close ? false : !_isUsageOpen;
		if ( _isUsageOpen )
		{
			_usageReport ??= backend.GetCachedUsage( profile );
			UpdateUsageRefresh( profile );
		}
		MarkChanged();
	}

	/// <summary>Starts one explicit usage refresh; ordinary chat rendering never reads provider usage.</summary>
	private void UpdateUsageRefresh( SettingsProfile profile )
	{
		if ( _isUsageBusy )
			return;
		_isUsageBusy = true;
		_usageError = "";
		MarkChanged();
		_ = UpdateUsageAsync( profile );
	}

	/// <summary>Reads profile usage without an agent turn and applies the result on the panel's UI thread.</summary>
	private async Task UpdateUsageAsync( SettingsProfile profile )
	{
		try
		{
			var report = await backend.GetUsageAsync( profile, context.Lifetime ).ConfigureAwait( false );
			context.Post( () =>
			{
				_usageReport = report;
				_isUsageBusy = false;
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			context.Post( () =>
			{
				_isUsageBusy = false;
				_usageError = error.Message;
				MarkChanged();
			} );
		}
	}

	/// <summary>Builds a small, muted text button for the line under the prompt.</summary>
	private static Button BuildRailButton( string label, Action action )
	{
		var button = Theme.Current.BuildButton( label, action );
		button.Height = Theme.Current.ControlHeight - Theme.Current.Spacing;
		button.Padding = new Edges( 0, Theme.Current.Spacing / 2 );
		button.MinWidth = 0;
		button.FlexShrink = 1;
		button.Color = Theme.Current.Muted;
		button.Children =
		[
			new Text
			{
				Content = label,
				FontSize = ViewStyle.SmallSize,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		];
		return button;
	}

	/// <summary>Targets the prompt editor, whose caret rectangle the drawn caret follows.</summary>
	private readonly ElementHandle _promptHandle = new();

	/// <summary>Tracks whether the prompt has the keyboard, so the drawn caret shows.</summary>
	private bool _isPromptFocused;

	/// <summary>Defines how the prompt's caret moves and blinks.</summary>
	internal static readonly Setting<string> CaretAnimation = new(
		"conversations.caret-animation",
		"Caret animation",
		"How the prompt's caret moves and blinks.",
		defaultValue: PromptCaret.GlideBlink,
		category: "Conversations",
		isValid: value => value is PromptCaret.Off or PromptCaret.Blink or PromptCaret.Glide or PromptCaret.GlideBlink )
	{
		Choices = [new( PromptCaret.Off, "Off" ), new( PromptCaret.Blink, "Blink only" ), new( PromptCaret.Glide, "Smooth glide" ), new( PromptCaret.GlideBlink, "Glide and blink" )],
	};

	/// <summary>Builds the prompt editor, one line tall and growing upward with line breaks and wrapped rows up to <see cref="PromptMaxLines"/>, with the drawn caret over it.</summary>
	/// <remarks>Goo's own caret is made transparent: it left stale pixels behind while typing, and it cannot glide or blink.</remarks>
	private Container BuildPrompt()
	{
		_promptLines = ReadPromptLines();
		var lineHeight = Theme.Current.FontSize * PromptLineHeight;
		// Vertical padding centers a single line against the buttons beside it.
		var inset = Math.Max( 0, (Theme.Current.ControlHeight - lineHeight) / 2 );
		var editor = new TextEditor
		{
			Key = "conversation-prompt",
			Handle = _promptHandle,
			Controller = _composer,
			AccessibilityName = "Message",
			AccessibilityRole = AccessibilityRole.TextEditor,
			// While the agent works, Enter queues the message for after the turn or steers it, as the switch beside Send says, and Ctrl+Enter does the other; a question that takes typed text takes it from here.
			// Kept short: the prompt is one line beside its buttons, and a long hint was cut off in narrow chats.
			Placeholder = ComposerAnswerKey() is not null ? "Type your answer…"
				: session.IsBusy && _isSteerMode ? "Steer… (Ctrl+Enter queues)"
				: session.IsBusy ? "Queue a message… (Ctrl+Enter steers)" : "Message Omp…",
			PlaceholderColor = Theme.Current.Muted,
			CaretColor = Color.Transparent,
			FontFamily = ViewStyle.FontFamily,
			FontSize = Theme.Current.FontSize,
			LineHeight = PromptLineHeight,
			Padding = new Edges( inset, 0 ),
			Height = _promptLines * lineHeight + inset * 2,
			MinWidth = 0,
			FlexGrow = 1,
			OnChange = _ => UpdatePromptChange(),
			OnFocus = _ => context.Run( () => UpdatePromptFocus( true ) ),
			OnBlur = _ => context.Run( () => UpdatePromptFocus( false ) ),
			KeyBindings = BuildPromptKeys(),
		};
		var mode = context.App.Settings.Get( CaretAnimation );
		return new Container
		{
			Key = "conversation-prompt-frame",
			MinWidth = 0,
			FlexGrow = 1,
			Children = [editor, Cell.Mount<PromptCaretInput, PromptCaret>( "prompt-caret", new( _promptHandle, _composer, _isPromptFocused, mode, lineHeight, UpdatePromptRows ) )],
		};
	}

	/// <summary>Shows or hides the drawn caret as the prompt gains or loses the keyboard.</summary>
	private void UpdatePromptFocus( bool isFocused )
	{
		if ( _isPromptFocused == isFocused )
			return;

		_isPromptFocused = isFocused;
		MarkChanged();
	}

	/// <summary>Sends on Enter, or queues or steers it while the agent works; Ctrl+Enter does the other, or gives a pending question its recommended answer
	/// while the prompt is empty; Shift+Enter breaks lines. While the command list is open, the arrows, Tab and Escape drive it instead of the editor.</summary>
	private KeyBinding[] BuildPromptKeys()
	{
		KeyBinding[] keys =
		[
			new( Key.Enter, KeyModifiers.None, () => context.Run( UpdateEnter ) ),
			new( Key.Enter, KeyModifiers.Primary, () => context.Run( UpdateModifiedEnter ) ),
			new( Key.Enter, new KeyModifiers { Shift = true }, () => _composer.Execute( new TextCommand( TextCommandKind.Insert, "\n", false ) ) ),
			new( Key.V, KeyModifiers.Primary, () => context.Run( UpdatePaste ) ),
		];
		return _commandQuery is null ? keys :
		[
			.. keys,
			new( Key.Up, KeyModifiers.None, () => context.Run( () => UpdateCommandSelection( -1 ) ) ),
			new( Key.Down, KeyModifiers.None, () => context.Run( () => UpdateCommandSelection( 1 ) ) ),
			new( Key.Tab, KeyModifiers.None, () => context.Run( () => UpdateCommandSelected( run: false ) ) ),
			new( Key.Escape, KeyModifiers.None, () => context.Run( UpdateCommandDismiss ) ),
		];
	}

	/// <summary>Builds the button that opens the file chooser.</summary>
	private Button BuildAttachButton()
	{
		var attach = Theme.Current.BuildButton( "+", () => context.Run( UpdateBrowse ), context.App.Window is not null );
		attach.Key = "conversation-attach";
		attach.AccessibilityName = "Attach files";
		attach.Width = Theme.Current.ControlHeight;
		attach.Padding = 0;
		attach.BorderRadius = Theme.Current.ControlHeight / 2.0;
		attach.FlexShrink = 0;
		attach.Color = Theme.Current.Muted;
		return attach;
	}

	/// <summary>Builds the round submit button, which sends, queues a typed message while the agent works, or stops the running turn when the prompt is empty.</summary>
	private Blob BuildSubmit()
	{
		_promptHasText = _composer.Document.Length > 0;
		var hasDraft = _promptHasText || _attachments.Count > 0;
		var isBusy = session.IsBusy;
		var input = new SubmitInput( (isBusy && !hasDraft) || _taskCheck is not null, hasDraft, !isBusy || session.IsConnected,
			() => context.Run( UpdateSend ), () => context.Run( UpdateStop ) );
		return Cell.Mount<SubmitInput, SubmitButton>( "conversation-submit", input );
	}

	/// <summary>Runs the selected slash command or sends on Enter, except while an input method is composing, when Enter commits the composition.</summary>
	private void UpdateEnter()
	{
		if ( _composer.Composition is not null )
		{
			_composer.CommitComposition();
			return;
		}
		if ( !UpdateCommandSelected( run: true ) )
			UpdateSend();
	}

	/// <summary>Gives the pending question its recommended answer when the prompt is empty, as the answer's Ctrl+Enter hint shows; otherwise steers or queues.</summary>
	private void UpdateModifiedEnter()
	{
		if ( _composer.Document.Length == 0 && _attachments.Count == 0 && ReadPrimaryAnswer() is { } answer )
			answer();
		else
			UpdateSteer();
	}

	/// <summary>Rebuilds only when the prompt's visible line count changes, or it fills or empties, which lights or dims the send button.</summary>
	private void UpdatePromptLines()
	{
		if ( ReadPromptLines() != _promptLines || _composer.Document.Length > 0 != _promptHasText )
			MarkChanged();
	}

	/// <summary>Takes the prompt's row count, wrapped rows included, as its caret measures it.</summary>
	private void UpdatePromptRows( int rows )
	{
		_promptRows = rows;
		UpdatePromptLines();
	}

	/// <summary>Gets the prompt's visible line count: its explicit lines or wrapped rows, whichever is more, up to <see cref="PromptMaxLines"/>.</summary>
	private int ReadPromptLines() => Math.Clamp( Math.Max( _composer.Document.LineCount, _promptRows ), 1, PromptMaxLines );
}
