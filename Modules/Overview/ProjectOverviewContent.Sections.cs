using System.Globalization;
using Goo;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Conversations;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Overview;

/// <summary>Lays out the body of a project's brief as Dia's Morning Brief does: Top to-dos, each with a check circle; New updates, numbered; and Your
/// day, a timeline of the day's chats beside the one picked, with a sticker that opens it.</summary>
/// <remarks>Every source is mixed in: the project's chats, designs and reports, and the items other modules add (<see cref="BriefSection"/>), each
/// item with its source in its line.</remarks>
internal sealed partial class ProjectOverviewContent
{
	/// <summary>Defines how many items New updates shows at most.</summary>
	private const int UpdateLimit = 5;

	/// <summary>Defines how many items New updates shows at least, older ones filling in when less changed in the last day.</summary>
	private const int UpdateMinimum = 3;

	/// <summary>Defines the most characters of a chat's last message a description shows.</summary>
	private const int DescriptionLength = 170;

	/// <summary>Gets the sticker's fill: the theme's accent, as Dia's yellow is its own.</summary>
	private static Color StickerColor => BriefPaper.Current.Accent;

	/// <summary>Gets the sticker's ink, readable on the accent under every theme.</summary>
	private static Color StickerInk => BriefPaper.Current.AccentInk;

	/// <summary>Stores the chat picked in Your day, or null for the default: the one at work, else the latest.</summary>
	private readonly State<string?> _dayPick = new( null );

	/// <summary>Counts changes to the items checked off today, so the page shows them.</summary>
	private readonly State<int> _doneVersion = new( 0 );

	/// <summary>Holds one item of the brief from any source.</summary>
	/// <param name="Key">Identifies the item on the page, its source first, such as <c>chat:&lt;id&gt;</c>.</param>
	/// <param name="Title">The headline.</param>
	/// <param name="Describe">Reads the sentence under the headline when its row builds, so a chat's last message is read only for the rows shown.</param>
	/// <param name="Source">Where it comes from and its state, such as "Pull requests · Checks failing".</param>
	/// <param name="Glyph">Its source's icon.</param>
	/// <param name="Time">When it last changed, or null.</param>
	/// <param name="NeedsYou">Whether it waits on the user.</param>
	/// <param name="Open">Opens it, or null for an item that only informs.</param>
	private sealed record BriefEntry( string Key, string Title, Func<string> Describe, string Source, string Glyph, DateTimeOffset? Time, bool NeedsYou,
		Action? Open );

	/// <summary>Adds the brief's sections: Top to-dos, New updates with the modules' notes and links, Your day, then the designs and reports.</summary>
	private void AddBrief( List<BuiltRow> rows, ChatHost host, IReadOnlyList<ChatEntry> chats, IReadOnlyList<BrowserReport> reports,
		IReadOnlyList<(BriefSection Section, BriefContent Content)> sections, DateTimeOffset now, PageLayout layout )
	{
		var titles = chats.ToDictionary( chat => chat.Conversation.Id, chat => chat.Conversation.Title );
		var added = sections.SelectMany( entry => entry.Content.Items.Select( item => ComputeEntry( entry.Section, item ) ) ).ToList();
		var todos = chats.Where( chat => chat.State == ChatState.Waiting ).Select( chat => ComputeEntry( host, chat ) )
			.Concat( added.Where( entry => entry.NeedsYou ) )
			.ToList();
		List<BriefEntry> changes =
		[
			.. chats.Where( chat => chat.State == ChatState.Idle ).Select( chat => ComputeEntry( host, chat ) ),
			.. _designs.Value.Select( design => ComputeEntry( host, design ) ),
			.. reports.Select( report => ComputeEntry( report, report.Conversation is { } id && titles.TryGetValue( id, out var title ) ? title : null ) ),
			.. added.Where( entry => !entry.NeedsYou && entry.Time is not null ),
		];
		changes.Sort( ( a, b ) => Nullable.Compare( b.Time, a.Time ) );
		var recent = changes.TakeWhile( entry => now - entry.Time < TimeSpan.FromDays( 1 ) ).Count();
		var updates = changes.Take( Math.Clamp( recent, UpdateMinimum, UpdateLimit ) ).ToList();

		var doneKey = $"{BriefKey}|{DateTime.Now:yyyy-MM-dd}|";
		List<BuiltRow> todoRows = [.. todos.Select( entry => new BuiltRow( entry.Key, () => BuildTodo( entry, doneKey + entry.Key ) ) )];
		if ( todoRows.Count == 0 )
			todoRows.Add( new( "none", () => BuildNote( "none", "Nothing waits for you." ) ) );
		AddSection( rows, "todos", "Top to-dos", todoRows, layout );

		List<BuiltRow> updateRows = [.. updates.Select( entry => new BuiltRow( entry.Key, () => BuildUpdate( entry, now ) ) )];
		var notes = sections.Where( entry => entry.Content.Items.Count == 0 && entry.Content.Empty.Length > 0 ).Select( entry => entry.Content.Empty ).ToList();
		var links = sections.SelectMany( entry => entry.Content.Links ).ToList();
		if ( updateRows.Count == 0 && todos.Count == 0 )
			updateRows.Add( new( "none", () => BuildNote( "none", "Nothing new yet. Type above to start a chat." ) ) );
		if ( notes.Count > 0 || links.Count > 0 )
			updateRows.Add( new( "modules", () => BuildModuleNotes( notes, links ) ) );
		if ( updateRows.Count > 0 )
			AddSection( rows, "updates", "New updates", updateRows, layout, SectionStyle.Numbered, updates.Count );

		var today = DateTime.Now.Date;
		var day = chats.Where( chat => chat.State == ChatState.Working || chat.Conversation.UpdatedAt.ToLocalTime().Date == today )
			.OrderBy( chat => chat.Conversation.UpdatedAt )
			.ToList();
		if ( day.Count > 0 )
			AddSection( rows, "day", "Your day", [new( "timeline", () => BuildDay( host, day, layout ) )], layout, SectionStyle.Full );

		AddSection( rows, "artifacts", "Designs and reports", BuildArtifacts( host, chats, _designs.Value, reports, now, layout ), layout );
	}

	/// <summary>Makes a brief item of a chat or task: its last message describes it, and its source says whether it waits or works.</summary>
	private BriefEntry ComputeEntry( ChatHost host, ChatEntry chat )
	{
		var conversation = chat.Conversation;
		var kind = conversation.HasChat ? "Chat" : "Task";
		return new( "chat:" + conversation.Id.ToString( "N" ), conversation.Title, () => DescribeChat( host, conversation.Id ),
			chat.State switch
			{
				ChatState.Waiting => $"{kind} · waiting for you",
				ChatState.Working => $"{kind} · working",
				_ => kind,
			},
			conversation.HasChat ? "\uE8BD" : "\uE9D5", conversation.UpdatedAt, chat.State == ChatState.Waiting, () => OpenChat( host.Ref( conversation.Id ) ) );
	}

	/// <summary>Makes a brief item of a design.</summary>
	private BriefEntry ComputeEntry( ChatHost host, DesignEntry design )
	{
		var variants = design.Variants switch { 0 => "One page", 1 => "One variant", var count => $"{count} variants" };
		return new( "design:" + design.Name, design.Name, () => $"{variants} in .lokn\\design.", "Design", "\uE790", design.Modified, false,
			host.IsLocal ? () => OpenDesign( host, design ) : null );
	}

	/// <summary>Makes a brief item of a report a chat showed.</summary>
	private BriefEntry ComputeEntry( BrowserReport report, string? chat ) =>
		new( "report:" + report.Path, report.Title, () => chat is null ? "" : $"Made in {chat}.", "Report", "\uE9F9", report.Created, false,
			() => _browser.ShowReport( _context.App, report, _context.Id ) );

	/// <summary>Makes a brief item of an item another module added, its section's title and the item's state as its source.</summary>
	private static BriefEntry ComputeEntry( BriefSection section, BriefItem item ) =>
		new( $"{section.Id}:{item.Key}", item.Title, () => item.Description, item.Detail.Length > 0 ? $"{section.Title} · {item.Detail}" : section.Title,
			item.Glyph.Length > 0 ? item.Glyph : "\uE8A5", item.Time, item.NeedsYou, item.Open );

	/// <summary>Reads a chat's last answer, or its last message, as one plain sentence; empty while its messages are read in the background.</summary>
	private static string DescribeChat( ChatHost host, Guid conversationId )
	{
		var messages = host.Store.PeekMessages( conversationId );
		var last = messages.LastOrDefault( message => message.Role == "assistant" && message.Text.Trim().Length > 0 )
			?? messages.LastOrDefault( message => message.Text.Trim().Length > 0 );
		return last is null ? "" : CleanBrief( last.Text, DescriptionLength );
	}

	/// <summary>Builds a to-do: a circle that checks it off for today, then its headline with its source's icon and state, and what to do under it.</summary>
	private Row BuildTodo( BriefEntry entry, string doneKey )
	{
		_ = _doneVersion.Value;
		var paper = BriefPaper.Current;
		var isDone = _module.Done.Contains( doneKey );
		void Toggle() => _context.Run( () =>
		{
			if ( !_module.Done.Remove( doneKey ) )
				_module.Done.Add( doneKey );
			_doneVersion.Value++;
		} );
		var check = new Button
		{
			Key = "check",
			Width = 18,
			Height = 18,
			FlexShrink = 0,
			Margin = new Edges( 3, 0, 0, 0 ),
			BorderRadius = 9,
			BorderWidth = 1.5,
			BorderColor = isDone ? paper.Ink3 : paper.Ink2,
			BackgroundColor = isDone ? paper.Ink3 : Color.Transparent,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = isDone ? $"Mark {entry.Title} as not done" : $"Mark {entry.Title} as done for today",
			OnClick = Toggle,
			KeyBindings = [new( Key.Space, KeyModifiers.None, Toggle )],
			Children = isDone ? [new Text { Content = "\uE73E", FontFamily = ViewStyle.SymbolFontFamily, FontSize = 10, Color = paper.Paper }] : [],
		};
		var body = BuildItem( entry, sourceInline: false );
		body.Opacity = isDone ? 0.45 : 1;
		return new Row { Key = "todo", Gap = 14, AlignItems = AlignItems.FlexStart, Padding = new Edges( 0, 0, 20, 0 ), Children = [check, body] };
	}

	/// <summary>Builds an update: its headline with its source in a faint italic, and what changed under it.</summary>
	private Container BuildUpdate( BriefEntry entry, DateTimeOffset now )
	{
		var item = BuildItem( entry with { Source = entry.Time is { } time ? $"{entry.Source} · {FormatAge( time, now )}" : entry.Source }, sourceInline: true );
		return new Container { Key = "update", Padding = new Edges( 0, 0, 20, 0 ), Children = [item] };
	}

	/// <summary>Builds an item's text, which opens it when clicked: the headline in bold with its source's icon and, inline or in a faint line, its
	/// source; then its description.</summary>
	private Button BuildItem( BriefEntry entry, bool sourceInline )
	{
		var paper = BriefPaper.Current;
		var theme = Theme.Current;
		void Run()
		{
			if ( entry.Open is { } open )
				_context.Run( open );
		}
		var icon = SidebarRows.BuildIcon( entry.Glyph );
		icon.Color = paper.Ink3;
		icon.FontSize = 13;
		var source = new Text
		{
			Key = "source",
			Content = entry.Source,
			FontFamily = BriefArt.SerifFontFamily,
			FontStyle = FontStyle.Italic,
			FontSize = 13,
			Color = entry.NeedsYou ? paper.Ink2 : paper.Ink3,
			MinWidth = 0,
			TextMaxLines = 1,
			TextTrimming = TextTrimming.Ellipsis,
		};
		var description = entry.Describe();
		return new Button
		{
			Key = "item",
			FlexGrow = 1,
			MinWidth = 0,
			FlexDirection = FlexDirection.Column,
			AlignItems = AlignItems.Stretch,
			Gap = 4,
			Padding = new Edges( 2, 8 ),
			Margin = new Edges( -2, -8 ),
			BorderRadius = theme.Radius + 2,
			BackgroundColor = Color.Transparent,
			HoverStyle = entry.Open is null ? null : new Style { BackgroundColor = paper.Hover },
			FocusStyle = new Style { BackgroundColor = paper.Hover },
			Cursor = entry.Open is null ? Cursor.Default : Cursor.Pointer,
			Focusable = true,
			AccessibilityName = $"{entry.Title}. {entry.Source}",
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run )],
			Children =
			[
				new Row
				{
					Key = "head",
					AlignItems = AlignItems.Center,
					Gap = 8,
					MinWidth = 0,
					Children =
					[
						new Text { Key = "title", Content = entry.Title, FontWeight = 600, FontSize = 15, Color = paper.Ink, MinWidth = 0, FlexShrink = 1, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
						.. sourceInline ? [source] : Array.Empty<Blob>(),
						icon,
					],
				},
				.. description.Length > 0
					? [new Text { Key = "description", Content = description, FontSize = 13, LineHeight = 1.5, Color = paper.Ink2, TextWrap = TextWrap.Wrap, TextMaxLines = 2, TextTrimming = TextTrimming.Ellipsis, MinWidth = 0 }]
					: Array.Empty<Blob>(),
				.. sourceInline ? Array.Empty<Blob>() : [source],
			],
		};
	}

	/// <summary>Builds the notes and links other modules add under New updates, such as a sign-in hint and Open pull requests.</summary>
	private Column BuildModuleNotes( IReadOnlyList<string> notes, IReadOnlyList<BriefLink> links ) => new()
	{
		Key = "modules",
		Gap = 2,
		Padding = new Edges( 0, 0, 12, 0 ),
		Children =
		[
			.. notes.Select( note => new Text { Content = note, FontSize = 12, Color = BriefPaper.Current.Ink3, TextWrap = TextWrap.Wrap } ),
			.. links.Count == 0 ? Array.Empty<Blob>() : [new Row { Key = "links", Gap = 4, Margin = new Edges( 0, -10 ), Children = [.. links.Select( link => BuildLink( "link:" + link.Label, link.Label, link.Open ) )] }],
		],
	};

	/// <summary>Builds Your day: the day's chats in time order with the time each last changed, beside the one picked, which shows what its agent said
	/// last and a sticker that opens it; narrow, the list goes above it.</summary>
	private Container BuildDay( ChatHost host, IReadOnlyList<ChatEntry> day, PageLayout layout )
	{
		var paper = BriefPaper.Current;
		var picked = day.FirstOrDefault( chat => chat.Conversation.Id.ToString( "N" ) == _dayPick.Value )
			?? day.LastOrDefault( chat => chat.State == ChatState.Working )
			?? day[^1];
		string When( ChatEntry chat ) => chat.State == ChatState.Working ? "now" : chat.Conversation.UpdatedAt.ToLocalTime().ToString( "t", CultureInfo.CurrentCulture );

		var list = new Column
		{
			Key = "list",
			Gap = 2,
			Width = layout.IsWide ? AsideWidth - 16 : Length.Percent( 100 ),
			FlexShrink = 0,
			Children =
			[
				.. day.Select( chat =>
				{
					var id = chat.Conversation.Id.ToString( "N" );
					var isPicked = chat == picked;
					void Pick() => _context.Run( () => _dayPick.Value = id );
					return new Button
					{
						Key = id,
						FlexDirection = FlexDirection.Row,
						AlignItems = AlignItems.Center,
						JustifyContent = JustifyContent.FlexStart,
						Gap = 10,
						MinHeight = 30,
						Padding = new Edges( 0, 8 ),
						BorderRadius = 4,
						BackgroundColor = isPicked ? paper.Hover : Color.Transparent,
						HoverStyle = new Style { BackgroundColor = paper.Hover },
						Cursor = Cursor.Pointer,
						Focusable = true,
						AccessibilityName = $"{chat.Conversation.Title}, {When( chat )}",
						OnClick = Pick,
						KeyBindings = [new( Key.Enter, KeyModifiers.None, Pick )],
						Children =
						[
							new Text { Content = When( chat ), FontFamily = ViewStyle.CodeFontFamily, FontSize = 10, Color = paper.Ink3, Width = 58, FlexShrink = 0, TextMaxLines = 1 },
							new Text { Content = chat.Conversation.Title, FontSize = 13, FontWeight = isPicked ? 600 : 400, Color = paper.Ink, MinWidth = 0, FlexShrink = 1, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
						],
					};
				} ),
			],
		};

		var description = DescribeChat( host, picked.Conversation.Id );
		var detail = new Column
		{
			Key = "detail",
			FlexGrow = 1,
			MinWidth = 0,
			Gap = 6,
			Children =
			[
				new Text
				{
					Key = "title",
					Content = picked.State == ChatState.Working ? $"{picked.Conversation.Title} — working now" : $"{picked.Conversation.Title} — {When( picked )}",
					FontWeight = 600,
					FontSize = 15,
					Color = paper.Ink,
					TextWrap = TextWrap.Wrap,
					MinWidth = 0,
				},
				new Text
				{
					Key = "description",
					Content = description.Length > 0 ? description : "No messages yet.",
					FontSize = 13,
					LineHeight = 1.5,
					Color = paper.Ink2,
					TextWrap = TextWrap.Wrap,
					TextMaxLines = 3,
					TextTrimming = TextTrimming.Ellipsis,
					MinWidth = 0,
				},
				new Row
				{
					Key = "actions",
					JustifyContent = JustifyContent.FlexEnd,
					Children = [BuildSticker( picked.State == ChatState.Working ? "Look in" : "Pick it up", () => OpenChat( host.Ref( picked.Conversation.Id ) ) )],
				},
			],
		};
		return new Container
		{
			Key = "day",
			FlexDirection = layout.IsWide ? FlexDirection.Row : FlexDirection.Column,
			AlignItems = layout.IsWide ? AlignItems.FlexStart : AlignItems.Stretch,
			Gap = layout.IsWide ? 16 : 14,
			Children = [list, detail],
		};
	}

	/// <summary>Builds Dia's yellow sticker: a sixteen-point burst made of four turned squares, with an italic label, which runs an action.</summary>
	private Button BuildSticker( string label, Action action )
	{
		const double size = 82;
		const double square = 58;
		void Run() => _context.Run( action );
		Container Ray( double angle ) => new()
		{
			Position = PositionType.Absolute,
			Left = (size - square) / 2,
			Top = (size - square) / 2,
			Width = square,
			Height = square,
			BorderRadius = 5,
			BackgroundColor = StickerColor,
			Transform = new PanelTransform { Rotate = angle },
		};
		return new Button
		{
			Key = "sticker",
			Width = size,
			Height = size,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BackgroundColor = Color.Transparent,
			Transform = new PanelTransform { Rotate = -8 },
			HoverStyle = new Style { Transform = new PanelTransform { Rotate = -8, Scale = 1.06 } },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = label,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )],
			Children =
			[
				Ray( 0 ),
				Ray( 22.5 ),
				Ray( 45 ),
				Ray( 67.5 ),
				new Text
				{
					Content = label + " →",
					Width = square,
					TextAlign = TextAlign.Center,
					TextWrap = TextWrap.Wrap,
					FontFamily = BriefArt.SerifFontFamily,
					FontStyle = FontStyle.Italic,
					FontSize = 14,
					LineHeight = 1.05,
					Color = StickerInk,
				},
			],
		};
	}
}
