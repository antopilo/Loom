using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>Selects what one row of the notification list shows.</summary>
internal enum NotificationRowKind
{
	Day, Message, Activity
}

/// <summary>Names where a notification came from, which picks its icon.</summary>
internal enum NotificationSource
{
	Agent, Browser, Other
}

/// <summary>Names what the list shows: everything, what waits for the user, what is unread, or one source or project.</summary>
internal enum NotificationFilterKind
{
	All, Waiting, Unread, Source, Project
}

/// <summary>Describes one row of the notification list.</summary>
/// <param name="Key">The stable row key.</param>
/// <param name="Kind">A day heading, one notification worth reading, or a run of short updates folded into one row.</param>
/// <param name="Label">The day's name for a heading, the run's summary, or the notification's title.</param>
/// <param name="Items">The notifications the row shows, newest first; none for a heading.</param>
internal sealed record NotificationRow( string Key, NotificationRowKind Kind, string Label, IReadOnlyList<LoknNotification> Items );

/// <summary>Describes the list's filter.</summary>
/// <param name="Kind">What it keeps.</param>
/// <param name="Value">The source's or project's name for those filters; null otherwise.</param>
internal sealed record NotificationFilter( NotificationFilterKind Kind, string? Value = null );

/// <summary>Shows notifications newest first, grouped by day, in the popover under the titlebar bell and in the full Notifications page.</summary>
/// <remarks>
/// Each row has its source's icon with a mark for how it went, its title, its age and one line of detail. Short updates in a row, such as
/// GitHub checks or chats finishing, fold into one row that lists its first updates. A question an agent waits on is answered in place: under
/// its row in the popover, in the reading pane of the page. The page adds filters by state, source and project, a search, and a reading
/// pane for the selected row. Opening a notification marks it read and shows its conversation's workspace, its panel, or its link.
/// </remarks>
internal sealed partial class NotificationsContent( PanelContext context, Action? dismiss = null ) : PanelContent
{
	/// <summary>Defines how far apart two short updates may be and still fold into one run.</summary>
	private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes( 10 );

	/// <summary>Defines the longest single-line message still read as a short update rather than a reply.</summary>
	private const int ShortLimit = 72;

	/// <summary>Defines how many updates of a run its row lists before "N more".</summary>
	private const int GroupPreview = 2;

	/// <summary>Defines the unread dot's diameter in logical pixels.</summary>
	private const int UnreadDotSize = 5;

	/// <summary>Defines the tallest question card under a row in the popover, in logical pixels; taller ones scroll.</summary>
	private const int PromptHeight = 360;

	/// <summary>Defines the Segoe Fluent Robot glyph, for agents.</summary>
	internal const string AgentGlyph = "\uE99A";

	/// <summary>Defines the Segoe Fluent Globe glyph, for the browser.</summary>
	internal const string BrowserGlyph = "\uE774";

	/// <summary>Defines the Segoe Fluent Ringer glyph, for any other source and the sidebar's Notifications row.</summary>
	internal const string BellGlyph = "\uEA8F";

	/// <summary>Defines the Segoe Fluent Setting glyph of the way to the notification settings.</summary>
	private const string SettingsGlyph = "\uE713";

	/// <summary>Defines the command that opens Settings.</summary>
	private const string SettingsCommand = "shell.open.settings";

	/// <summary>Tracks what the list shows.</summary>
	private readonly State<NotificationFilter> _filter = new( new( NotificationFilterKind.All ) );

	/// <summary>Tracks the search text of the page's list.</summary>
	private readonly State<string> _query = new( "" );

	/// <summary>Tracks the page's selected row by key, or null for the first.</summary>
	private readonly State<string?> _selected = new( null );

	/// <summary>Tracks the runs opened onto all their updates, by row key.</summary>
	private readonly State<HashSet<string>> _expanded = new( [] );

	/// <summary>Gets whether this is the popover under the bell rather than the page.</summary>
	private bool IsPopover => dismiss is not null;

	/// <summary>Builds the popover or the page.</summary>
	public override Blob Build() => IsPopover ? BuildPopover() : BuildPage();

	/// <summary>Holds what one build reads: the notifications, the questions waiting, which notification each question belongs to, and the time.</summary>
	/// <param name="Items">The notifications, newest first.</param>
	/// <param name="Prompts">The questions waiting.</param>
	/// <param name="Waiting">The question each notification announces, by notification.</param>
	/// <param name="Kinds">The kinds modules contributed.</param>
	/// <param name="Now">The current time.</param>
	private sealed record FeedView( IReadOnlyList<LoknNotification> Items, IReadOnlyList<NotificationPrompt> Prompts, IReadOnlyDictionary<Guid, NotificationPrompt> Waiting,
		IReadOnlyList<NotificationKind> Kinds, DateTimeOffset Now )
	{
		/// <summary>Gets the questions no notification announces, such as one a chat on screen asked while Loom had focus.</summary>
		internal IEnumerable<NotificationPrompt> Unannounced => Prompts.Where( prompt => !Waiting.Values.Contains( prompt ) );

		/// <summary>Gets how many questions wait.</summary>
		internal int WaitingCount => Prompts.Count;
	}

	/// <summary>Reads the feed, the questions waiting and the kinds for one build.</summary>
	private FeedView ReadView()
	{
		var items = context.App.Notifications.Items;
		var prompts = NotificationPromptSource.ListAll( context.App );
		return new( items, prompts, ComputeWaiting( items, prompts, id => context.App.Panels.ResolvePanelId( id ) ),
			context.App.Extensions.Get<NotificationKind>( NotificationKind.Point ), DateTimeOffset.Now );
	}

	/// <summary>Pairs each waiting question with the newest notification that announced it: its panel's, or for a chat no panel shows, the one whose link opens it.</summary>
	/// <param name="newestFirst">The notifications, newest first.</param>
	/// <param name="prompts">The questions waiting.</param>
	/// <param name="resolve">Maps a saved panel id to the panel's current id.</param>
	internal static Dictionary<Guid, NotificationPrompt> ComputeWaiting( IReadOnlyList<LoknNotification> newestFirst, IReadOnlyList<NotificationPrompt> prompts, Func<Guid, Guid> resolve )
	{
		var waiting = new Dictionary<Guid, NotificationPrompt>();
		foreach ( var prompt in prompts )
		{
			var item = newestFirst.FirstOrDefault( item => (prompt.PanelId is { } panelId && item.PanelId is { } itemPanel && resolve( itemPanel ) == panelId)
				|| (prompt.PanelId is null && item.Url is { } url && url.Contains( prompt.Key, StringComparison.OrdinalIgnoreCase )) );
			if ( item is not null )
				waiting.TryAdd( item.Id, prompt );
		}
		return waiting;
	}

	/// <summary>Keeps the notifications a filter and search text let through, newest first.</summary>
	internal static List<LoknNotification> ComputeVisible( FeedViewInput view, NotificationFilter filter, string query, Func<Guid, string?> panelType ) =>
	[
		.. view.Items.Where( item => filter.Kind switch
		{
			NotificationFilterKind.Waiting => view.WaitingIds.Contains( item.Id ) || (item.Tone == NotificationTone.Attention && !item.IsRead),
			NotificationFilterKind.Unread => !item.IsRead,
			NotificationFilterKind.Source => ComputeSourceName( item, panelType ) == filter.Value,
			NotificationFilterKind.Project => item.Project == filter.Value,
			_ => true,
		} && Matches( item, query ) ),
	];

	/// <summary>The part of a build the filter reads, kept apart so checks can call <see cref="ComputeVisible"/> without a panel.</summary>
	/// <param name="Items">The notifications, newest first.</param>
	/// <param name="WaitingIds">The notifications that announce a question still waiting.</param>
	internal sealed record FeedViewInput( IReadOnlyList<LoknNotification> Items, IReadOnlySet<Guid> WaitingIds );

	/// <summary>Reports whether a notification's title, message or project holds the search text; empty text matches all.</summary>
	private static bool Matches( LoknNotification item, string query ) => query.Length == 0
		|| item.Title.Contains( query, StringComparison.CurrentCultureIgnoreCase )
		|| item.Message.Contains( query, StringComparison.CurrentCultureIgnoreCase )
		|| (item.Project?.Contains( query, StringComparison.CurrentCultureIgnoreCase ) ?? false);

	/// <summary>Lists the visible notifications as rows under the current filter and search.</summary>
	private List<NotificationRow> ComputeRows( FeedView view, NotificationFilter filter, string query ) =>
		ComputeRows( ComputeVisible( new( view.Items, view.Waiting.Keys.ToHashSet() ), filter, query, ComputePanelType ), view.Now, view.Kinds, view.Waiting.ContainsKey );

	/// <summary>Lists the rows newest first: a heading for each day, each notification worth reading, and each run of short updates.</summary>
	/// <param name="newestFirst">The notifications as the feed stores them, newest first.</param>
	/// <param name="now">The current time, which names today and yesterday.</param>
	/// <param name="kinds">The kinds modules contributed, which mark short updates and word their summaries.</param>
	/// <param name="isWaiting">Reports a notification whose question still waits, which never folds into a run so it can be answered; null for none.</param>
	internal static List<NotificationRow> ComputeRows( IReadOnlyList<LoknNotification> newestFirst, DateTimeOffset now, IReadOnlyList<NotificationKind> kinds, Func<Guid, bool>? isWaiting = null )
	{
		var rows = new List<NotificationRow>( newestFirst.Count + 4 );
		var run = new List<LoknNotification>();
		DateTime? day = null;

		void Flush()
		{
			if ( run.Count == 0 )
				return;

			// A summary reads in the order things happened, oldest first.
			rows.Add( new( "run:" + run[^1].Id.ToString( "N" ), NotificationRowKind.Activity, ComputeSummary( [.. Enumerable.Reverse( run )], kinds ), [.. run] ) );
			run.Clear();
		}

		foreach ( var item in newestFirst )
		{
			var date = item.Time.ToLocalTime().Date;
			if ( day != date )
			{
				Flush();
				day = date;
				rows.Add( new( "day:" + date.ToString( "yyyyMMdd", CultureInfo.InvariantCulture ), NotificationRowKind.Day, FormatDay( date, now.ToLocalTime().Date ), [] ) );
			}

			if ( IsShort( item, kinds ) && isWaiting?.Invoke( item.Id ) != true )
			{
				// Short updates from one source fold together; another source or a long pause starts a new run.
				if ( run.Count > 0 && (run[^1].Time - item.Time > GroupWindow || ComputeGroup( item ) != ComputeGroup( run[0] )) )
					Flush();
				run.Add( item );
			}
			else
			{
				Flush();
				rows.Add( new( item.Id.ToString( "N" ), NotificationRowKind.Message, item.Title, [item] ) );
			}
		}

		Flush();
		return rows;
	}

	/// <summary>Reports whether a notification is a short update rather than something to read: its kind says so, or its text is one short line; one with buttons is read in full.</summary>
	internal static bool IsShort( LoknNotification item, IReadOnlyList<NotificationKind> kinds ) => item.Actions is not { Count: > 0 }
		&& (FindKind( kinds, item )?.IsUpdate == true || (item.Message.Length <= ShortLimit && !item.Message.Contains( '\n' )));

	/// <summary>Summarizes a run of short updates by kind in order of first appearance, worded by each kind's module, such as "2 checks passed, Build fix finished".</summary>
	/// <param name="run">The run, oldest first.</param>
	/// <param name="kinds">The kinds modules contributed.</param>
	internal static string ComputeSummary( IReadOnlyList<LoknNotification> run, IReadOnlyList<NotificationKind> kinds )
	{
		var keys = new List<string>();
		var counts = new Dictionary<string, int>( StringComparer.Ordinal );
		var firsts = new Dictionary<string, LoknNotification>( StringComparer.Ordinal );
		foreach ( var item in run )
		{
			var key = ComputeKind( item );
			if ( !counts.ContainsKey( key ) )
			{
				keys.Add( key );
				firsts[key] = item;
			}
			counts[key] = counts.GetValueOrDefault( key ) + 1;
		}

		var phrases = keys.Select( key =>
		{
			var count = counts[key];
			var first = firsts[key];
			if ( first.Kind is { } kind && kinds.FirstOrDefault( candidate => candidate.Kind == kind )?.Summarize is { } summarize )
				return summarize( count, first );
			return key switch
			{
				"finished" => count == 1 ? $"{first.Title} finished" : $"{count} chats finished",
				"needs" => count == 1 ? $"{first.Title} needs you" : $"{count} chats need you",
				// Beside other phrases, one update reads as its title: its whole message would crowd the summary.
				_ => count == 1 ? (keys.Count > 1 ? first.Title : first.Message) : $"{Count( count, "update", "updates" )} from {first.Title}",
			};
		} );
		var summary = string.Join( ", ", phrases );
		return summary.Length == 0 ? "Updates" : char.ToUpperInvariant( summary[0] ) + summary[1..];
	}

	/// <summary>Classifies a short update for its run's summary: by the kind it was posted with, else as a chat that finished or needs the user, else by source and title.</summary>
	private static string ComputeKind( LoknNotification item )
	{
		if ( item.Kind is { } kind )
			return kind;

		return ComputeSource( item, _ => null ) switch
		{
			NotificationSource.Agent when item.Message.StartsWith( "Finished", StringComparison.Ordinal ) => "finished",
			NotificationSource.Agent => "needs",
			var source => "other:" + source + ":" + item.Title,
		};
	}

	/// <summary>Gets what a run groups by: the posting module, else the title.</summary>
	private static string ComputeGroup( LoknNotification item ) => item.Source?.Split( '.' )[0] ?? item.Title;

	/// <summary>Finds the kind a module contributed for a notification: by its kind, else its source, else its source's module.</summary>
	internal static NotificationKind? FindKind( IReadOnlyList<NotificationKind> kinds, LoknNotification item )
	{
		var module = item.Source?.Split( '.' )[0];
		foreach ( var key in new[] { item.Kind, item.Source, module } )
		{
			if ( key is not null && kinds.FirstOrDefault( kind => kind.Kind == key ) is { } found )
				return found;
		}
		return null;
	}

	/// <summary>Formats a count with the singular or plural noun.</summary>
	private static string Count( int count, string one, string many ) => $"{count} {(count == 1 ? one : many)}";

	/// <summary>Finds where a notification came from: an agent's chat, the browser, or anything else.</summary>
	/// <remarks>Notifications saved before sources were kept fall back to their panel, while it is open.</remarks>
	internal static NotificationSource ComputeSource( LoknNotification item, Func<Guid, string?> panelType ) => ComputeModule( item, panelType ) switch
	{
		"conversations" => NotificationSource.Agent,
		"browser" => NotificationSource.Browser,
		_ => NotificationSource.Other,
	};

	/// <summary>Gets the module a notification came from, such as <c>conversations</c> or <c>github</c>; empty when unknown.</summary>
	private static string ComputeModule( LoknNotification item, Func<Guid, string?> panelType ) =>
		(item.Source ?? (item.PanelId is { } panelId ? panelType( panelId ) : null) ?? "").Split( '.' )[0];

	/// <summary>Names a notification's source for the page's filters: Chats, GitHub, Browser, the module's name, or Other.</summary>
	internal static string ComputeSourceName( LoknNotification item, Func<Guid, string?> panelType ) => ComputeModule( item, panelType ) switch
	{
		"conversations" => "Chats",
		"github" => "GitHub",
		"" => "Other",
		var module => char.ToUpperInvariant( module[0] ) + module[1..],
	};

	/// <summary>Names a day of notifications: Today, Yesterday, a weekday within the week, then the date.</summary>
	internal static string FormatDay( DateTime day, DateTime today ) => (today - day.Date).Days switch
	{
		0 => "Today",
		1 => "Yesterday",
		> 1 and < 7 => day.ToString( "dddd", CultureInfo.CurrentCulture ),
		_ => day.ToString( day.Year == today.Year ? "MMMM d" : "MMMM d, yyyy", CultureInfo.CurrentCulture ),
	};

	/// <summary>Gets the mark a row shows: a waiting question, else the notification's own; for a run, a failure first, then a question, then done when all are.</summary>
	private static NotificationTone ComputeTone( NotificationRow row, FeedView view )
	{
		if ( row.Items.Any( item => view.Waiting.ContainsKey( item.Id ) ) )
			return NotificationTone.Attention;
		if ( row.Items is [var only] )
			return only.Tone;
		if ( row.Items.Any( item => item.Tone == NotificationTone.Failure ) )
			return NotificationTone.Failure;
		if ( row.Items.Any( item => item.Tone == NotificationTone.Attention && !item.IsRead ) )
			return NotificationTone.Attention;
		return row.Items.All( item => item.Tone == NotificationTone.Success ) ? NotificationTone.Success : NotificationTone.None;
	}

	/// <summary>Gets a row's title: the notification's for one worth reading or one update whose summary is only its message, else the run's summary.</summary>
	private static string ComputeTitle( NotificationRow row ) => row.Kind == NotificationRowKind.Message || IsPlainUpdate( row ) ? row.Items[0].Title : row.Label;

	/// <summary>Reports whether a row is one update whose summary only repeats its message, so it reads better as its title over its message.</summary>
	private static bool IsPlainUpdate( NotificationRow row ) =>
		row is { Kind: NotificationRowKind.Activity, Items: [var item] } && string.Equals( row.Label, item.Message, StringComparison.OrdinalIgnoreCase );

	/// <summary>Gets a row's line of detail: the question waiting, else the message for one worth reading, else the message, project and facts of one update; empty for none.</summary>
	private static string ComputeDetail( NotificationRow row, FeedView view )
	{
		if ( row.Items is not [var item] )
			return "";
		if ( view.Waiting.ContainsKey( item.Id ) )
			return "Is waiting for you";
		if ( row.Kind == NotificationRowKind.Message )
			return NotificationViews.ComputePreview( item.Message );

		var facts = new List<string>();
		if ( IsPlainUpdate( row ) )
			facts.Add( NotificationViews.ComputePreview( item.Message ) );
		if ( item.Project is { Length: > 0 } project )
			facts.Add( project );
		facts.AddRange( item.Details ?? [] );
		return string.Join( " · ", facts );
	}

	/// <summary>Builds one row: the icon with its mark, the title, the age, a line of detail, and for a run its first updates; selected rows are raised with an accent edge.</summary>
	/// <param name="row">The row.</param>
	/// <param name="view">What the build reads.</param>
	/// <param name="isSelected">Whether the page shows this row in its reading pane.</param>
	/// <param name="activate">Runs when the row is clicked: opening it from the popover, selecting it in the page.</param>
	private Button BuildRow( NotificationRow row, FeedView view, bool isSelected, Action activate )
	{
		var theme = Theme.Current;
		var latest = row.Items[0];
		var isUnread = row.Items.Any( item => !item.IsRead );
		var tone = ComputeTone( row, view );
		var background = isSelected ? theme.Raised : IsPopover ? theme.Panel : theme.Canvas;
		var head = new List<Blob>
		{
			new Text
			{
				Content = ComputeTitle( row ),
				FontWeight = isUnread ? NotificationViews.TitleWeight : 400,
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( row.Items.Count > 1 )
			head.Add( NotificationViews.BuildCount( row.Items.Count ) );
		head.Add( new Spacer() );
		head.Add( new Text { Content = NotificationViews.FormatAge( latest.Time, view.Now ), FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0, TextWrap = TextWrap.NoWrap } );

		var body = new List<Blob> { new Row { Key = "head", AlignItems = AlignItems.Center, Gap = theme.Spacing * 0.75, MinWidth = 0, Children = head } };
		if ( ComputeDetail( row, view ) is { Length: > 0 } detail )
			body.Add( new Text
			{
				Key = "detail",
				Content = detail,
				Color = tone == NotificationTone.Attention && view.Waiting.ContainsKey( latest.Id ) ? theme.Attention : theme.Muted,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );
		if ( row.Items.Count > 1 )
			body.Add( BuildRunLines( row ) );

		var children = new List<Blob>
		{
			NotificationViews.BuildIcon( BuildRowIcon( row ), tone, background ),
			new Container { Key = "body", FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Gap = 2, Children = body },
		};
		if ( isUnread )
			children.Add( new Container
			{
				Key = "unread",
				Position = PositionType.Absolute,
				Left = 4,
				Top = 16,
				Width = UnreadDotSize,
				Height = UnreadDotSize,
				BorderRadius = UnreadDotSize / 2.0,
				BackgroundColor = theme.Attention,
			} );

		return new Button
		{
			Key = "row:" + row.Key,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.FlexStart,
			Gap = theme.Spacing * 1.25,
			MinWidth = 0,
			FlexShrink = 0,
			Padding = new Edges( theme.Spacing, theme.Spacing * 1.5 ),
			BackgroundColor = isSelected ? theme.Raised : Color.Transparent,
			BorderWidth = new Edges( 0, 0, 0, isSelected ? 2 : 0 ),
			BorderColor = theme.Accent,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"{(isUnread ? "Unread: " : "")}{ComputeTitle( row )}{(ComputeDetail( row, view ) is { Length: > 0 } said ? ", " + said : "")}",
			OnClick = () => context.Run( activate ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, () => context.Run( activate ) ),
				new( Key.Space, KeyModifiers.None, () => context.Run( activate ) ),
			],
			HoverStyle = new Style { BackgroundColor = isSelected ? theme.Raised : theme.Hover },
			FocusStyle = new Style { BackgroundColor = isSelected ? theme.Raised : theme.Hover },
			Children = children,
		};
	}

	/// <summary>Builds a run's first updates under its summary, and the link that lists them all or fewer again.</summary>
	private Container BuildRunLines( NotificationRow row )
	{
		var theme = Theme.Current;
		var isExpanded = _expanded.Value.Contains( row.Key );
		var shown = isExpanded ? row.Items : row.Items.Take( GroupPreview ).ToList();
		var lines = new List<Blob>();
		foreach ( var item in shown )
			lines.Add( new Text
			{
				Key = "line:" + item.Id.ToString( "N" ),
				Content = "· " + (ComputeSource( item, ComputePanelType ) == NotificationSource.Agent ? item.Title : NotificationViews.ComputePreview( item.Message )),
				Color = theme.Muted,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );
		if ( row.Items.Count > GroupPreview )
		{
			var more = isExpanded ? "Show fewer" : $"+ {row.Items.Count - GroupPreview} more";
			var toggle = NotificationViews.BuildQuietButton( more, () => context.Run( () => UpdateExpanded( row.Key ) ) );
			toggle.Key = "more";
			toggle.Color = theme.Accent;
			toggle.Padding = new Edges( 0, 0 );
			toggle.Height = 18;
			toggle.AlignSelf = AlignSelf.FlexStart;
			toggle.HoverStyle = new Style { Color = theme.Text };
			lines.Add( toggle );
		}
		return new Container { Key = "lines", Gap = 2, MinWidth = 0, Children = lines };
	}

	/// <summary>Builds a row's source icon: the notification's, or for a run of several sources, the bell.</summary>
	private Blob BuildRowIcon( NotificationRow row )
	{
		var sources = row.Items.Select( item => ComputeModule( item, ComputePanelType ) ).Distinct().Count();
		return sources == 1 ? BuildSourceIcon( row.Items[0] ) : BuildGlyph( BellGlyph );
	}

	/// <summary>Builds the row of a question no notification announced: the agent's icon marked as waiting, the chat's title and "Is waiting for you".</summary>
	private Button BuildPromptRow( NotificationPrompt prompt, bool isSelected, Action activate )
	{
		var theme = Theme.Current;
		var background = isSelected ? theme.Raised : IsPopover ? theme.Panel : theme.Canvas;
		return new Button
		{
			Key = "prompt:" + prompt.Key,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.FlexStart,
			Gap = theme.Spacing * 1.25,
			MinWidth = 0,
			FlexShrink = 0,
			Padding = new Edges( theme.Spacing, theme.Spacing * 1.5 ),
			BackgroundColor = isSelected ? theme.Raised : Color.Transparent,
			BorderWidth = new Edges( 0, 0, 0, isSelected ? 2 : 0 ),
			BorderColor = theme.Accent,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"{prompt.Title} is waiting for you",
			OnClick = () => context.Run( activate ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, () => context.Run( activate ) ),
				new( Key.Space, KeyModifiers.None, () => context.Run( activate ) ),
			],
			HoverStyle = new Style { BackgroundColor = isSelected ? theme.Raised : theme.Hover },
			Children =
			[
				NotificationViews.BuildIcon( BuildGlyph( AgentGlyph ), NotificationTone.Attention, background ),
				new Container
				{
					FlexGrow = 1,
					FlexShrink = 1,
					MinWidth = 0,
					Gap = 2,
					Children =
					[
						new Text { Content = prompt.Title, FontWeight = NotificationViews.TitleWeight, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
						new Text { Content = "Is waiting for you", Color = theme.Attention, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
					],
				},
			],
		};
	}

	/// <summary>Builds a question's answering card: its controls in a frame outlined in the waiting color; null when it shows nothing here.</summary>
	/// <param name="prompt">The question.</param>
	/// <param name="surface">The view's name, which gives the card its own text boxes.</param>
	/// <param name="maxHeight">The tallest the card grows before it scrolls; null for no limit.</param>
	private static Container? BuildPromptCard( NotificationPrompt prompt, string surface, double? maxHeight )
	{
		if ( prompt.Build( surface ) is not { } controls )
			return null;

		var theme = Theme.Current;
		var card = new Container
		{
			Key = "prompt-card:" + prompt.Key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{prompt.Title} asks",
			FlexShrink = 0,
			MinWidth = 0,
			Padding = new Edges( theme.Spacing * 1.25, 0, 0, 0 ),
			BackgroundColor = theme.Panel,
			BorderColor = theme.Attention,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Overflow = Overflow.Hidden,
			Children = [controls],
		};
		if ( maxHeight is { } height )
		{
			card.MaxHeight = height;
			card.OverflowY = Overflow.Scroll;
			card.ScrollbarY = ViewStyle.BuildScrollbar();
		}
		return card;
	}

	/// <summary>Builds the muted uppercase name of a day above its rows.</summary>
	private static Text BuildDay( string key, string label ) => new()
	{
		Key = key,
		Content = label.ToUpper( CultureInfo.CurrentCulture ),
		FontSize = ViewStyle.SmallSize - 1,
		LetterSpacing = 0.5,
		Color = Theme.Current.Muted,
		Padding = new Edges( Theme.Current.Spacing * 1.5, Theme.Current.Spacing * 1.5, Theme.Current.Spacing / 2, Theme.Current.Spacing * 1.5 ),
	};

	/// <summary>Opens or closes a run onto all its updates.</summary>
	private void UpdateExpanded( string key )
	{
		var expanded = new HashSet<string>( _expanded.Value );
		if ( !expanded.Remove( key ) )
			expanded.Add( key );
		_expanded.Value = expanded;
	}

	/// <summary>Shows only what a filter keeps.</summary>
	private void UpdateFilter( NotificationFilter filter )
	{
		if ( _filter.Value != filter )
			_filter.Value = filter;
	}

	/// <summary>Opens a notification where it belongs, closing the popover.</summary>
	private void Open( LoknNotification item )
	{
		dismiss?.Invoke();
		NotificationsModule.Open( context.App, item, IsPopover ? null : context.Id );
	}

	/// <summary>Opens where a question comes from, closing the popover; another profile's question opens after switching to it.</summary>
	private void Open( NotificationPrompt prompt )
	{
		dismiss?.Invoke();
		prompt.Open();
	}

	/// <summary>Removes the notifications already read.</summary>
	private void UpdateClearRead()
	{
		foreach ( var item in context.App.Notifications.Items.Where( item => item.IsRead ).ToArray() )
			context.App.Notifications.Remove( item.Id );
	}

	/// <summary>Opens Settings, where the notification alerts are, closing the popover.</summary>
	private void OpenSettings()
	{
		dismiss?.Invoke();
		_ = context.App.Commands.ExecuteAsync( SettingsCommand );
	}

	/// <summary>Builds the empty list's line.</summary>
	private static Container BuildEmpty( string text ) => new()
	{
		Key = "empty",
		FlexGrow = 1,
		Padding = Theme.Current.Spacing * 3,
		AlignItems = AlignItems.Center,
		Children = [new Text { Content = text, Color = Theme.Current.Muted, TextAlign = TextAlign.Center }],
	};

	/// <summary>Builds a notification's text as Markdown with the Markdown module's renderer, such as an agent's closing message with lists and code; plain text without the module.</summary>
	/// <remarks>The renderer is looked up on each build, so loading or reloading the Markdown module redraws the feed with it.</remarks>
	private Blob BuildBody( string message ) =>
		context.App.Services.Get<IMarkdownRenderer>() is { } markdown ? markdown.Build( message, OpenLink ) : NotificationViews.BuildText( message );

	/// <summary>Builds a notification's icon: the one a module contributed for its kind or source, else its source's built-in icon.</summary>
	private Blob BuildSourceIcon( LoknNotification item ) => BuildSourceIcon( context.App, item, ComputePanelType );

	/// <summary>Builds a notification's icon: the one a module contributed for its kind or source, as a vector or a glyph, else its source's built-in icon.</summary>
	internal static Blob BuildSourceIcon( LoknApplication app, LoknNotification item, Func<Guid, string?> panelType ) =>
		FindKind( app.Extensions.Get<NotificationKind>( NotificationKind.Point ), item ) switch
		{
			{ Icon: { } icon } => new Shape
			{
				Path = icon,
				Fit = ShapeFit.Contain,
				Width = NotificationViews.SourceIconSize,
				Height = NotificationViews.SourceIconSize,
				FlexShrink = 0,
				Fill = new VectorPaint( Theme.Current.Muted ),
			},
			{ } kind => BuildGlyph( kind.Glyph ),
			null => BuildSourceIcon( ComputeSource( item, panelType ) ),
		};

	/// <summary>Builds a source's built-in icon: a glyph for agents, the browser and the rest.</summary>
	internal static Blob BuildSourceIcon( NotificationSource source ) => source switch
	{
		NotificationSource.Agent => BuildGlyph( AgentGlyph ),
		NotificationSource.Browser => BuildGlyph( BrowserGlyph ),
		_ => BuildGlyph( BellGlyph ),
	};

	/// <summary>Builds a notification's buttons, each following its link; null when it has none.</summary>
	/// <param name="item">The notification.</param>
	/// <param name="open">Follows a link where the view opens links.</param>
	internal static Row? BuildActions( LoknNotification item, Action<Uri> open ) => item.Actions is { Count: > 0 } actions
		? new Row
		{
			Key = "actions",
			Gap = Theme.Current.Spacing,
			FlexWrap = FlexWrap.Wrap,
			Children = [.. actions.Select( action => (Blob)NotificationViews.BuildButton( action.Label, () => open( action.Link ) ) )],
		}
		: null;

	/// <summary>Builds one source glyph at the source icon size.</summary>
	private static Text BuildGlyph( string glyph ) => NotificationViews.BuildGlyph( glyph, size: NotificationViews.SourceIconSize );

	/// <summary>Follows a link in a notification's text, beside the page or, from the popover, wherever links open.</summary>
	private void OpenLink( Uri uri ) => context.Run( () =>
	{
		dismiss?.Invoke();
		context.App.OpenLink( uri, IsPopover ? null : context.Id );
	} );

	/// <summary>Gets the type of an open panel, or null when it has closed.</summary>
	private string? ComputePanelType( Guid panelId ) => context.App.Panels.Panels.FirstOrDefault( panel => panel.Id == panelId )?.TypeId;
}
