using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Owns a native rich conversation view of one <see cref="IChatSession"/>, which runs the conversation's profile-pinned Omp process here or in a host.</summary>
/// <remarks>While the Git module is on, the rail under the prompt shows the chat folder's repository, branch and changes, and diffs open beside the chat; without it the chat works the same, without them.</remarks>
public sealed partial class ChatContent : PanelContent
{
	/// <summary>Stores the panel this view shows in.</summary>
	private readonly PanelContext context;

	/// <summary>Stores the history the conversation is recorded in.</summary>
	private readonly ConversationStore store;

	/// <summary>Stores the session that runs the conversation this view shows.</summary>
	private readonly IChatSession session;

	/// <summary>Stores the conversation this view shows.</summary>
	private readonly Guid conversationId;

	/// <summary>Stores the Omp service the session runs on, for model names and provider usage.</summary>
	private readonly OmpService backend;

	/// <summary>Shows a conversation's session in a panel, following its changes and attaching the panel as the one its agent's tools see.</summary>
	/// <param name="context">The panel this view shows in.</param>
	/// <param name="store">The history the conversation is recorded in.</param>
	/// <param name="session">The session that runs the conversation.</param>
	public ChatContent( PanelContext context, ConversationStore store, IChatSession session )
	{
		this.context = context;
		this.store = store;
		this.session = session;
		conversationId = session.ConversationId;
		backend = session.Backend;
		_composer = new( new TextDocument( store.Get( conversationId ).Draft ?? "" ) );
		_marks = new( action => context.Post( action ) );
		UpdateSubscription( isSubscribed: true );
		session.Attach( context.Id );
	}

	/// <summary>Gets the session this view shows.</summary>
	internal IChatSession Session => session;

	/// <summary>Defines the transcript inset in logical pixels.</summary>
	internal static int ContentPadding => Theme.Current.Spacing * 2;

	/// <summary>Defines the vertical space between transcript messages in logical pixels.</summary>
	private static int MessageGap => Theme.Current.Spacing * 3;

	/// <summary>Defines the composer height in logical pixels.</summary>
	private const int ComposerHeight = 92;

	/// <summary>Defines the height assumed for a transcript row until layout measures it, in logical pixels.</summary>
	private const double EstimatedRowHeight = 80;

	/// <summary>Tracks asynchronous connection, dialog, and activity changes on the UI thread.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Retains the multiline prompt and its selection independently of docking.</summary>
	private readonly TextEditorController _composer;

	/// <summary>Targets the transcript scroller and keeps it on the newest message until the reader scrolls up.</summary>
	private readonly TranscriptFollow _transcript = new();

	/// <summary>Keeps where the user's prompts sit, for their lines along the transcript scrollbar.</summary>
	private readonly TranscriptMarks _marks;

	/// <summary>Receives each placement of the transcript rows; kept as one delegate across builds.</summary>
	private Action<IReadOnlyList<TranscriptRow>, double[]>? _placeRows;

	/// <summary>Retains expanded rich blocks by stable message and content identifiers.</summary>
	private readonly HashSet<string> _expanded = [];

	/// <summary>Retains the reasoning and activity folds the reader closed while they ran, which otherwise show open until the agent writes or stops.</summary>
	private readonly HashSet<string> _runningClosed = [];

	/// <summary>Retains editable dialog values independently of composer input.</summary>
	private readonly Dictionary<string, TextEditorController> _dialogEditors = [];

	/// <summary>Builds one transcript row; kept as one delegate so the virtual list reuses rows across builds.</summary>
	private Func<TranscriptRow, Blob>? _buildRow;

	/// <summary>Maps each tool call to its latest recorded result, refreshed on every build.</summary>
	private readonly Dictionary<string, ConversationMessage> _toolResults = new( StringComparer.Ordinal );

	/// <summary>Maps each tool call to the message that made it, so live tool progress rebuilds only that row.</summary>
	private readonly Dictionary<string, Guid> _toolOwners = new( StringComparer.Ordinal );

	/// <summary>Counts view-state changes per message that its stored content does not reflect.</summary>
	private readonly Dictionary<Guid, int> _revisions = [];

	/// <summary>Holds the keys of rows and dialogs already shown or present when the history loaded; a key not in it enters with motion once.</summary>
	private readonly HashSet<string> _shown = new( StringComparer.Ordinal );

	/// <summary>Holds the keys of rows built at least once, so content that later joins a row on screen can tell it arrived live.</summary>
	private readonly HashSet<string> _built = new( StringComparer.Ordinal );

	/// <summary>Stores the first message of the last build, to tell a replaced history from one that grew.</summary>
	private Guid? _firstMessage;

	/// <summary>Reports whether the panel built its transcript before.</summary>
	private bool _hasBuilt;

	/// <summary>Reports whether the row being built was built before, so a tool line or image new to it arrived while it was on screen.</summary>
	private bool _isRowLive;

	/// <summary>Reports whether the row being built is the run the agent works on now, whose reasoning and activity folds show open.</summary>
	private bool _isRowRunning;

	/// <summary>Reports whether the chat is connecting so it can open the Agent Hub once.</summary>
	private bool _isOpeningAgents;

	/// <summary>Reports whether the panel closed and detached from its session.</summary>
	private bool _isClosed;

	/// <summary>Builds the retained transcript, pending dialog, and multiline composer.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var conversation = store.Get( conversationId );
		UpdateGitWatch( store.GetDirectory( conversationId ) );
		var messages = store.PeekMessages( conversationId );
		// While the messages file is read off the UI thread the chat is neither empty nor shown yet; its rows count as present once it loads.
		var isLoading = store.IsLoadingMessages( conversationId );
		var rows = ComputeRows( messages );
		if ( !isLoading )
			UpdateShown( messages, rows );
		var todo = ComputeCurrentTodo( messages );
		_buildRow ??= BuildRow;
		_placeRows ??= _marks.Update;
		// The prompt floats over the transcript's end; the transcript pads its end by the prompt's height so the newest message clears it.
		// It floats in an empty chat too, so the first message does not move the editor to another parent and drop its focus.
		Blob view = isLoading ? BuildLoading()
			: rows.Count == 0 ? BuildEmpty()
			: new Virtual
			{
				// Only rows near the viewport are built and laid out, so long transcripts stay cheap to stream into and resize.
				Key = "conversation-transcript",
				Handle = _transcript.Handle,
				Source = new MeasuredRowsSource<TranscriptRow>( rows, _buildRow, static _ => EstimatedRowHeight, _transcript.IsFollowing, _placeRows ),
				AccessibilityName = "Conversation transcript",
				FlexGrow = 1,
				MinHeight = 0,
				Padding = new Edges( ContentPadding, ContentPadding, ReadFloatingHeight(), ContentPadding ),
				OverflowX = Overflow.Hidden,
				ScrollbarY = ViewStyle.GetScrollbar(),
			};
		var stack = new List<Blob>();
		// A question that was answered stays first while its exit plays; the next pending one shows after it.
		if ( (_leavingDialogs.Count > 0 ? _leavingDialogs[0] : session.PendingDialog) is { ValueKind: JsonValueKind.Object } dialog )
		{
			var id = ReadText( dialog, "id" );
			// The exit wraps the entrance, so the two animate different elements and neither overrides the other's opacity.
			var question = Cell.Mount<RevealInput, Reveal>( "dialog:" + id, new( BuildDialog( dialog ), _shown.Add( "dialog:" + id ) ) );
			var column = BuildColumn( "conversation-dialog", [Reveal.BuildExit( "dialog-exit", question, _leavingDialogs.Count > 0 )], 0 );
			// The composer's context meter sits on its top edge, so the question keeps clear of it.
			column.Padding = new Edges( 0, 0, Theme.Current.Spacing * 2, 0 );
			column.FlexShrink = 1;
			column.MaxHeight = DialogHeight;
			column.OverflowY = Overflow.Scroll;
			column.ScrollbarY = ViewStyle.GetScrollbar();
			stack.Add( column );
		}
		stack.Add( BuildComposer( conversation, BuildTodoPin( todo, isBeside: false ) ) );

		var area = new List<Blob> { view };
		if ( rows.Count > 0 )
			area.Add( Cell.Mount<TranscriptMarksInput, TranscriptMarksView>( "transcript-marks", new( _marks, _transcript.Handle ) ) );
		if ( BuildTodoPin( todo, isBeside: true ) is { } margin )
			area.Add( margin );
		area.Add( BuildFloating( stack ) );
		if ( !_transcript.IsFollowing )
		{
			var latest = BuildLatest( _transcript.ScrollToEnd );
			latest.Bottom = ReadFloatingHeight();
			area.Add( latest );
		}
		var children = new List<Blob>();
		// An empty chat, new or just cleared, looks like Home, which has no title; a chat still loading keeps it, as its transcript will.
		if ( rows.Count > 0 || isLoading )
			children.Add( new Text
			{
				Key = "conversation-title",
				Content = conversation.Title,
				Padding = new Edges( Theme.Current.Spacing, ContentPadding, 0, ContentPadding ),
				FontSize = ViewStyle.SmallSize,
				Color = Theme.Current.Muted,
				TextAlign = TextAlign.Center,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );
		children.Add( new Container
		{
			Key = "conversation-transcript-area",
			Handle = _transcriptArea,
			FlexGrow = 1,
			MinHeight = 0,
			Children = area,
		} );
		// Overlays cover the whole chat, so they come last and draw on top.
		if ( BuildModelPicker() is { } picker )
			children.Add( picker );
		if ( BuildViewer() is { } viewer )
			children.Add( viewer );
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Chat conversation",
			Key = "conversation",
			FlexGrow = 1,
			FontFamily = ViewStyle.FontFamily,
			FontSize = Theme.Current.FontSize,
			Color = Theme.Current.Text,
			MinWidth = 0,
			MinHeight = 0,
			Children = children,
		};
	}

	/// <summary>Builds the transcript's place while the chat's messages are read: empty, so neither the empty chat nor a partial transcript flashes.</summary>
	private static Container BuildLoading() => new()
	{
		Key = "conversation-transcript-loading",
		AccessibilityRole = AccessibilityRole.Group,
		AccessibilityName = "Loading conversation",
		FlexGrow = 1,
		MinHeight = 0,
	};

	/// <summary>Builds the empty chat, new or cleared, as Home shows it: the wordmark and its host's recent cards, centered above the composer.</summary>
	private Column BuildEmpty()
	{
		var children = new List<Blob> { new Spacer(), Cell.Mount( "home-logo", () => new HomeLogo() ) };
		// A chat hosted without the Conversations module, as in focused checks, has no projects to offer.
		if ( context.App.Modules.Any( module => module.Id == "conversations" ) )
		{
			var conversations = context.App.GetModule<ConversationsModule>();
			children.Add( HomeContent.BuildCards( context, conversations, conversations.Hosts.Find( context.HostId ) ?? conversations.Hosts.Local, session.UpdateStatus ) );
		}
		children.Add( new Spacer() );

		return new Column
		{
			Key = "conversation-transcript-empty",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Empty conversation",
			FlexGrow = 1,
			MinHeight = 0,
			AlignItems = AlignItems.Center,
			Padding = new Edges( ContentPadding, ContentPadding, ReadFloatingHeight(), ContentPadding ),
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.GetScrollbar(),
			Children = children,
		};
	}

	/// <summary>Lists the visible messages as transcript rows, pairing tool results with their calls and spacing agent turns as one flow.</summary>
	/// <remarks>
	/// Consecutive reasoning and tool calls, across messages, become one activity row; the agent's prose between them ends a run.
	/// A finished turn that edited or wrote files ends with a card listing them; the turn still running gets its card when the agent's run ends,
	/// even while background work, such as jobs or subagents, keeps the session busy.
	/// </remarks>
	private List<TranscriptRow> ComputeRows( IReadOnlyList<ConversationMessage> messages )
	{
		_toolResults.Clear();
		_toolOwners.Clear();
		foreach ( var message in messages )
		{
			var facts = ReadFacts( message );
			if ( facts.ResultCallId is { Length: > 0 } resultId )
				_toolResults[resultId] = message;
			foreach ( var id in facts.ToolCallIds )
				_toolOwners[id] = message.Id;
		}

		var rows = new List<TranscriptRow>( messages.Count );
		var activity = new List<TranscriptPart>();
		// The index of the row the last reasoning or tool run became, so the run the agent works on now shows open.
		var lastRun = -1;
		string? previous = null;
		void Add( TranscriptPart[] parts, bool isActivity )
		{
			var first = parts[0];
			var results = new List<ConversationMessage>();
			// A module reload can add, replace or remove the card a row's tool calls draw with.
			var revision = context.App.Tools.CardVersion;
			for ( var i = 0; i < parts.Length; i++ )
			{
				var message = parts[i].Message;
				if ( parts.Take( i ).Any( part => part.Message.Id == message.Id ) )
					continue;

				revision += _revisions.GetValueOrDefault( message.Id );
				foreach ( var id in ReadFacts( message ).ToolCallIds )
				{
					if ( _toolResults.GetValueOrDefault( id ) is { } result )
						results.Add( result );
				}
			}
			// An agent turn reads as one flow of lines; the wider gap only separates it from the user's turns.
			var gap = previous is null ? 0 : previous == "user" || first.Message.Role == "user" ? MessageGap : Theme.Current.Spacing;
			rows.Add( new( $"{first.Message.Id:N}:{first.Start}", parts, isActivity, [.. results], revision, gap ) );
			previous = first.Message.Role;
		}
		void Flush()
		{
			if ( activity.Count == 0 )
				return;

			// Reasoning alone keeps its own fold; a summary line only replaces runs that used tools.
			Add( [.. activity], activity.Any( HasToolCall ) );
			lastRun = rows.Count - 1;
			activity.Clear();
		}

		// A turn runs from one of the user's prompts to the next; its changes are summed as it goes and shown after its last row.
		var changes = new Dictionary<string, ChangedFile>( StringComparer.OrdinalIgnoreCase );
		ConversationMessage? turn = null;
		void AddChanges()
		{
			Flush();
			if ( turn is not null && changes.Count > 0 )
			{
				rows.Add( new( $"{turn.Id:N}:changes", [], false, [], _revisions.GetValueOrDefault( turn.Id ), previous is null ? 0 : Theme.Current.Spacing * 2, [.. changes.Values] ) );
				previous = "assistant";
			}
			changes.Clear();
		}

		foreach ( var message in messages )
		{
			var facts = ReadFacts( message );
			if ( message.Role == "user" )
			{
				AddChanges();
				turn = message;
			}
			turn ??= message;
			if ( facts.HasEdits )
				AddTurnChanges( message, changes );
			if ( facts.ResultCallId is { } resultId && _toolOwners.ContainsKey( resultId ) )
				continue;
			if ( !facts.IsVisible )
				continue;

			var blocks = facts.Blocks;
			if ( blocks.Length == 0 )
			{
				Flush();
				Add( [TranscriptPart.Whole( message )], false );
				continue;
			}

			for ( var start = 0; start < blocks.Length; )
			{
				var isActivity = facts.IsActivity[start];
				var end = start + 1;
				while ( end < blocks.Length && facts.IsActivity[end] == isActivity )
					end++;
				// The last run also carries the message's own extras, such as an error notice.
				var part = new TranscriptPart( message, start, end == blocks.Length ? int.MaxValue : end );
				if ( isActivity )
					activity.Add( part );
				else
				{
					Flush();
					Add( [part], false );
				}
				start = end;
			}
		}
		Flush();
		if ( !session.IsBusy || session.IsSettling )
		{
			_runningClosed.Clear();
			AddChanges();
		}
		else if ( lastRun >= 0 && lastRun == rows.Count - 1 )
			rows[lastRun] = rows[lastRun] with { IsRunning = true };
		return rows;
	}

	/// <summary>Marks every row as shown when the chat first builds or its history is loaded or replaced, so only rows that arrive later enter with motion.</summary>
	/// <remarks>
	/// Loading the backend's history replaces every message with new identifiers, and a load into an empty chat brings several
	/// messages at once; a growing transcript keeps its first message and adds one message at a time.
	/// </remarks>
	private void UpdateShown( IReadOnlyList<ConversationMessage> messages, List<TranscriptRow> rows )
	{
		Guid? first = messages.Count > 0 ? messages[0].Id : null;
		var isLoaded = !_hasBuilt || (_firstMessage is not null && first is not null && first != _firstMessage) || (_firstMessage is null && messages.Count > 1);
		if ( isLoaded )
		{
			foreach ( var row in rows )
				_shown.Add( row.Key );
		}
		_hasBuilt = true;
		_firstMessage = first;
	}

	/// <summary>Builds one transcript row: its messages, activity summary or change card centered in the reading column below its gap.</summary>
	/// <remarks>
	/// A row new since the history loaded fades and rises in once. Rows scrolled back into view were shown before and build at
	/// rest, as do tool lines and images a row already held when it was first built.
	/// </remarks>
	private Blob BuildRow( TranscriptRow row )
	{
		var isNew = _shown.Add( row.Key );
		_isRowLive = !_built.Add( row.Key );
		_isRowRunning = row.IsRunning;
		var column = BuildColumn( "message", row.Changes is { } changes ? [BuildChanges( row.Key, changes )] : row.IsActivity ? [BuildActivity( row )] : [.. row.Parts.Select( BuildMessage ).OfType<Blob>()], Theme.Current.Spacing );
		_isRowLive = false;
		_isRowRunning = false;
		// Folded tool lines were not built, so they count as shown now and an opened fold does not play them.
		foreach ( var part in row.Parts )
			UpdateToolsShown( part );
		return new Container
		{
			Width = Length.Percent( 100 ),
			Padding = new Edges( row.Gap, 0, 0, 0 ),
			Children = [Cell.Mount<RevealInput, Reveal>( "message-reveal", new( column, isNew ) )],
		};
	}

	/// <summary>Runs a removal once an exit built with <see cref="Reveal.BuildExit"/> has played, or at once when motion is off.</summary>
	private void UpdateAfterExit( Action action )
	{
		var scale = Motion.TimeScale;
		if ( scale <= 0 )
		{
			action();
			return;
		}

		_ = Task.Delay( TimeSpan.FromMilliseconds( ChatMotion.FastMs / scale ), context.Lifetime )
			.ContinueWith( _ => context.Post( action ), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default );
	}

	/// <summary>Rebuilds one message's row after view state it does not store, such as an opened fold, changes.</summary>
	private void MarkRowChanged( Guid messageId )
	{
		_revisions[messageId] = _revisions.GetValueOrDefault( messageId ) + 1;
		MarkChanged();
	}

	/// <summary>Centers content in the shared reading column, full width in narrow panels.</summary>
	internal static Container BuildColumn( string key, List<Blob> children, int gap ) => new()
	{
		Key = key,
		Width = Length.Percent( 100 ),
		MaxWidth = ChatStyle.ColumnWidth,
		AlignSelf = AlignSelf.Center,
		FlexShrink = 0,
		Gap = gap,
		Children = children,
	};

	/// <summary>Builds the round button, centered at the bottom of the transcript just above the prompt, that returns to the newest message once the reader scrolls up.</summary>
	internal static Button BuildLatest( Action scrollToEnd )
	{
		var theme = Theme.Current;
		var size = theme.ControlHeight + 4;
		var lifted = new Style { Transform = new PanelTransform { Scale = 1.08 }, BackgroundColor = theme.Hover };
		return new Button
		{
			Key = "conversation-latest",
			AccessibilityName = "Latest",
			Position = PositionType.Absolute,
			// Centered by its left edge at half the width, pulled back by half its own.
			Left = Length.Percent( 50 ),
			Margin = new Edges( 0, 0, 0, -size / 2 ),
			Bottom = theme.Spacing * 2,
			Width = size,
			Height = size,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = size / 2.0,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BackgroundColor = theme.Raised,
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = scrollToEnd,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, scrollToEnd ),
				new( Key.Space, KeyModifiers.None, scrollToEnd ),
			],
			TransitionMs = 150,
			TransitionEasing = Easing.EaseOut,
			TransitionProperties = [TransitionProperty.Transform, TransitionProperty.BackgroundColor],
			HoverStyle = lifted,
			FocusStyle = lifted,
			ActiveStyle = new Style { Transform = new PanelTransform { Scale = 0.92 } },
			Children = [new Container { Transform = new PanelTransform { Rotate = 180 }, Children = [ChatIcons.BuildArrow( theme.Text )] }],
		};
	}

	/// <summary>Captures the conversation identity, its immutable account profile and its host for layout restoration.</summary>
	/// <remarks>A reload stops the running turn with the module, so the capture also asks the reopened chat to continue it.</remarks>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( ConversationsModule.BuildInput(
		new ChatRef( context.HostId ?? LoknHost.LocalId, conversationId ), store.Get( conversationId ).Profile, session.IsBusy && context.App.Panels.IsSuspending ) );

	/// <summary>Saves the unsent prompt, then detaches from the session, which stops its Omp process once no panel shows it and its agent is idle.</summary>
	/// <remarks>A conversation deleted meanwhile, such as from another device, has no draft to keep.</remarks>
	public override void Closed()
	{
		_isClosed = true;
		UpdateSubscription( isSubscribed: false );
		if ( store.Find( conversationId ) is not null )
			store.SaveDraft( conversationId, _composer.Document.GetText() );
		session.Detach( context.Id );
		_gitWatch?.Dispose();
		_gitWatch = null;
		ReleaseImages();
		ReleaseFormPictures();
	}

	/// <summary>Builds selectable body text in the shell's typography.</summary>
	internal static Text BuildText( string text, bool muted = false, int? size = null ) => new()
	{
		Content = text,
		Color = muted ? Theme.Current.Muted : Theme.Current.Text,
		FontFamily = ViewStyle.FontFamily,
		FontSize = size ?? Theme.Current.FontSize,
		Selectable = true,
		MinWidth = 0,
	};

	/// <summary>Rebuilds this panel after a UI-thread state transition.</summary>
	private void MarkChanged() => _version.Value++;

	/// <summary>Reads an optional text property without losing unknown rich payloads.</summary>
	internal static string ReadText( JsonElement element, string property, string fallback = "" ) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out var value )
			? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString()
			: fallback;
}
