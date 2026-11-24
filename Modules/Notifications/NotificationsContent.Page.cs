using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>The full Notifications page: the filter rail, the list with its search, and the reading pane of the selected row.</summary>
internal sealed partial class NotificationsContent
{
	/// <summary>Defines the filter rail's width in logical pixels.</summary>
	private const int RailWidth = 208;

	/// <summary>Defines the list's width in logical pixels.</summary>
	private const int ListWidth = 380;

	/// <summary>Defines the narrowest the list shrinks to in logical pixels.</summary>
	private const int ListMinWidth = 260;

	/// <summary>Defines the widest reading column in logical pixels, like a chat's.</summary>
	private const int ReadingWidth = 720;

	/// <summary>Defines a project dot's diameter in logical pixels.</summary>
	private const int ProjectDotSize = 8;

	/// <summary>Defines the Segoe Fluent BulletedList glyph of the All filter.</summary>
	private const string AllGlyph = "\uE8FD";

	/// <summary>Defines the Segoe Fluent Help glyph of the Waiting filter.</summary>
	private const string WaitingGlyph = "\uE9CE";

	/// <summary>Defines the Segoe Fluent Mail glyph of the Unread filter.</summary>
	private const string UnreadGlyph = "\uE715";

	/// <summary>Defines the Segoe Fluent Search glyph of the search box.</summary>
	private const string SearchGlyph = "\uE721";

	/// <summary>Defines the Segoe Fluent OpenInNewWindow glyph of the button that opens a notification.</summary>
	private const string OpenGlyph = "\uE8A7";

	/// <summary>Defines the Segoe Fluent CheckMark glyph of the button that marks a notification read.</summary>
	private const string ReadGlyph = "\uE73E";

	/// <summary>Defines the Segoe Fluent Delete glyph of the button that removes a notification.</summary>
	private const string RemoveGlyph = "\uE74D";

	/// <summary>Defines the Segoe Fluent Back glyph that leads from a notification back to the list when the page is narrow.</summary>
	private const string BackGlyph = "\uE72B";

	/// <summary>Defines the narrowest page that shows the filter rail beside the list and reading pane, in logical pixels.</summary>
	private const int WideWidth = 1000;

	/// <summary>Defines the narrowest page that shows the list and reading pane side by side, in logical pixels; narrower, one replaces the other.</summary>
	private const int MediumWidth = 640;

	/// <summary>Defines the list's width in logical pixels when the rail is hidden.</summary>
	private const int MediumListWidth = 320;

	/// <summary>Follows the page's size, which picks its layout.</summary>
	private readonly ElementHandle _pageArea = new();

	/// <summary>Tracks the layout the page's width allows; it starts wide until the first measure.</summary>
	private readonly State<PageLayout> _layout = new( PageLayout.Wide );

	/// <summary>Tracks whether a narrow page shows the picked notification instead of the list.</summary>
	private readonly State<bool> _isReading = new( false );

	/// <summary>Tracks whether the page's size is followed.</summary>
	private bool _isPageWatched;

	/// <summary>Names how much of the page fits side by side.</summary>
	private enum PageLayout
	{
		/// <summary>The rail, the list and the reading pane.</summary>
		Wide,

		/// <summary>The list, with the rail's filters as tabs, and the reading pane.</summary>
		Medium,

		/// <summary>The list or the reading pane, one at a time.</summary>
		Narrow,
	}

	/// <summary>Builds the page for the width it has: the rail, the list and the reading pane side by side when wide; the list with filter
	/// tabs and the reading pane when narrower; and when narrow, the list alone, a picked notification replacing it with a way back.</summary>
	private Container BuildPage()
	{
		var theme = Theme.Current;
		var view = ReadView();
		var filter = _filter.Value;
		var query = _query.Value;
		var rows = ComputeRows( view, filter, query );
		var prompts = filter.Kind is NotificationFilterKind.All or NotificationFilterKind.Waiting
			? view.Unannounced.Where( prompt => query.Length == 0 || prompt.Title.Contains( query, StringComparison.CurrentCultureIgnoreCase ) ).ToList()
			: [];
		var selected = ComputeSelected( rows, prompts );
		if ( !_isPageWatched )
		{
			_isPageWatched = true;
			_pageArea.MetricsChanged += metrics => context.Run( () => UpdateLayout( metrics.ContentBox.Width ) );
		}

		var layout = _layout.Value;
		List<Blob> children = layout switch
		{
			PageLayout.Wide => [BuildRail( view ), BuildList( view, rows, prompts, selected, layout ), BuildReading( view, rows, prompts, selected, layout )],
			PageLayout.Medium => [BuildList( view, rows, prompts, selected, layout ), BuildReading( view, rows, prompts, selected, layout )],
			_ when _isReading.Value && selected is not null => [BuildReading( view, rows, prompts, selected, layout )],
			_ => [BuildList( view, rows, prompts, null, layout )],
		};
		return new Container
		{
			Key = "notifications",
			Handle = _pageArea,
			// Clipped, so the panel's width sets the page's, and the measure picks a layout that fits instead of growing past it.
			Width = Length.Percent( 100 ),
			Overflow = Overflow.Hidden,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Notification feed",
			FlexGrow = 1,
			FlexDirection = FlexDirection.Row,
			MinWidth = 0,
			MinHeight = 0,
			BackgroundColor = theme.Canvas,
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			Children = children,
		};
	}

	/// <summary>Picks the page's layout for its width, rebuilding only when it changes; a page that turns narrow starts on its list.</summary>
	private void UpdateLayout( double width )
	{
		var layout = width >= WideWidth ? PageLayout.Wide : width >= MediumWidth ? PageLayout.Medium : PageLayout.Narrow;
		if ( _layout.Value == layout )
			return;

		_layout.Value = layout;
		_isReading.Value = false;
	}

	/// <summary>Gets the selected row's key: the one picked while it is still listed, else the first question, else the first notification row.</summary>
	private string? ComputeSelected( List<NotificationRow> rows, List<NotificationPrompt> prompts )
	{
		var picked = _selected.Value;
		if ( picked is not null && (rows.Any( row => row.Key == picked ) || prompts.Any( prompt => "prompt:" + prompt.Key == picked )) )
			return picked;
		return prompts.Count > 0 ? "prompt:" + prompts[0].Key : rows.FirstOrDefault( row => row.Kind != NotificationRowKind.Day )?.Key;
	}

	/// <summary>Selects a row and marks its notifications read, as reading it in the pane counts as reading it; when narrow, the row then replaces the list.</summary>
	private void UpdateSelected( NotificationRow row )
	{
		_selected.Value = row.Key;
		_isReading.Value = true;
		foreach ( var item in row.Items.Where( item => !item.IsRead ) )
			context.App.Notifications.MarkRead( item.Id );
	}

	/// <summary>Builds the filter rail: All, Waiting and Unread, then each source and each project the feed holds, and the way to the alert settings.</summary>
	private Container BuildRail( FeedView view )
	{
		var theme = Theme.Current;
		var items = view.Items;
		var unread = items.Count( item => !item.IsRead );
		var waitingCount = view.WaitingCount + items.Count( item => !view.Waiting.ContainsKey( item.Id ) && item.Tone == NotificationTone.Attention && !item.IsRead );
		var children = new List<Blob>
		{
			BuildRailItem( new( NotificationFilterKind.All ), "All", NotificationViews.BuildGlyph( AllGlyph, size: 14 ), items.Count ),
			BuildRailItem( new( NotificationFilterKind.Waiting ), "Waiting for you", NotificationViews.BuildGlyph( WaitingGlyph, size: 14 ), waitingCount, isAttention: waitingCount > 0 ),
			BuildRailItem( new( NotificationFilterKind.Unread ), "Unread", NotificationViews.BuildGlyph( UnreadGlyph, size: 14 ), unread ),
		};

		var sources = items.GroupBy( item => ComputeSourceName( item, ComputePanelType ) ).OrderByDescending( group => group.Count() ).ToList();
		if ( sources.Count > 0 )
		{
			children.Add( BuildRailHeading( "Source" ) );
			foreach ( var source in sources )
			{
				var icon = BuildSourceIcon( source.First() );
				if ( icon is Text glyph )
					glyph.FontSize = 14;
				else if ( icon is Shape shape )
				{
					shape.Width = 14;
					shape.Height = 14;
				}
				children.Add( BuildRailItem( new( NotificationFilterKind.Source, source.Key ), source.Key, icon, source.Count() ) );
			}
		}

		var projects = items.Where( item => item.Project is { Length: > 0 } ).GroupBy( item => item.Project! ).OrderByDescending( group => group.Count() ).ToList();
		if ( projects.Count > 0 )
		{
			children.Add( BuildRailHeading( "Project" ) );
			foreach ( var project in projects )
				children.Add( BuildRailItem( new( NotificationFilterKind.Project, project.Key ), project.Key, BuildProjectDot( project.Key ), project.Count() ) );
		}

		children.Add( new Spacer() );
		var settings = BuildRailButton( "notifications-settings", "Notification settings", NotificationViews.BuildGlyph( SettingsGlyph, size: 14 ), null, false, false, () => OpenSettings() );
		children.Add( settings );
		return new Container
		{
			Key = "notifications-rail",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Filters",
			Width = RailWidth,
			FlexShrink = 0,
			MinHeight = 0,
			Gap = 2,
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing ),
			BorderWidth = new Edges( 0, theme.BorderWidth, 0, 0 ),
			BorderColor = theme.Divider,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			Children = children,
		};
	}

	/// <summary>Builds one filter of the rail, raised while it is on.</summary>
	private Button BuildRailItem( NotificationFilter filter, string label, Blob icon, int count, bool isAttention = false ) =>
		BuildRailButton( "filter:" + filter.Kind + ":" + filter.Value, label, icon, count, _filter.Value == filter, isAttention, () => UpdateFilter( filter ) );

	/// <summary>Builds one row of the rail: its icon, its name, and a count at the right.</summary>
	private Button BuildRailButton( string key, string label, Blob icon, int? count, bool isOn, bool isAttention, Action action )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Container { Width = 16, FlexShrink = 0, AlignItems = AlignItems.Center, Children = [icon] },
			new Text { Content = label, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, Color = isOn ? theme.Text : theme.Muted },
			new Spacer(),
		};
		if ( count is { } shown )
			children.Add( isAttention ? NotificationViews.BuildCount( shown, isAttention: true )
				: new Text { Content = shown.ToString( CultureInfo.CurrentCulture ), FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0 } );
		return new Button
		{
			Key = key,
			Height = theme.ControlHeight,
			FlexShrink = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 0, theme.Spacing ),
			BorderRadius = theme.Radius,
			BackgroundColor = isOn ? theme.Raised : Color.Transparent,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = count is { } total ? $"{label}, {total}" : label,
			OnClick = () => context.Run( action ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, () => context.Run( action ) ),
				new( Key.Space, KeyModifiers.None, () => context.Run( action ) ),
			],
			HoverStyle = new Style { BackgroundColor = isOn ? theme.Raised : theme.Hover },
			FocusStyle = new Style { BackgroundColor = isOn ? theme.Raised : theme.Hover },
			Children = children,
		};
	}

	/// <summary>Builds the muted uppercase heading of a group of rail filters.</summary>
	private static Text BuildRailHeading( string label ) => new()
	{
		Key = "heading:" + label,
		Content = label.ToUpper( CultureInfo.CurrentCulture ),
		FontSize = ViewStyle.SmallSize - 1,
		LetterSpacing = 0.5,
		Color = Theme.Current.Muted,
		Padding = new Edges( Theme.Current.Spacing * 1.5, Theme.Current.Spacing, Theme.Current.Spacing / 2, Theme.Current.Spacing ),
	};

	/// <summary>Builds a project's dot, colored from its name so a project keeps its color.</summary>
	private static Container BuildProjectDot( string project )
	{
		var theme = Theme.Current;
		Color[] colors = [theme.Accent, theme.AnsiGreen, theme.AnsiMagenta, theme.AnsiCyan, theme.AnsiYellow, theme.AnsiBlue, theme.AnsiRed];
		var hash = 0;
		foreach ( var character in project )
			hash = unchecked(hash * 31 + character);
		return new Container
		{
			Width = ProjectDotSize,
			Height = ProjectDotSize,
			BorderRadius = ProjectDotSize / 2.0,
			BackgroundColor = colors[(int)((uint)hash % colors.Length)],
		};
	}

	/// <summary>Builds the list: the filter's name and count with its actions, the search box, then the waiting questions and the rows by day.</summary>
	private Container BuildList( FeedView view, List<NotificationRow> rows, List<NotificationPrompt> prompts, string? selected, PageLayout layout )
	{
		var theme = Theme.Current;
		var filter = _filter.Value;
		var unread = context.App.Notifications.UnreadCount;
		var list = new List<Blob>();
		foreach ( var prompt in prompts )
			list.Add( BuildPromptRow( prompt, selected == "prompt:" + prompt.Key, () =>
			{
				_selected.Value = "prompt:" + prompt.Key;
				_isReading.Value = true;
			} ) );
		foreach ( var row in rows )
		{
			if ( row.Kind == NotificationRowKind.Day )
			{
				list.Add( BuildDay( row.Key, row.Label ) );
				continue;
			}

			list.Add( BuildRow( row, view, row.Key == selected, () => UpdateSelected( row ) ) );
			if ( BuildRowActions( row ) is { } actions )
				list.Add( actions );
		}
		if ( list.Count == 0 )
			list.Add( BuildEmpty( _query.Value.Length > 0 ? "No notification matches." : "Nothing here." ) );

		var count = rows.Sum( row => row.Items.Count ) + prompts.Count;
		var header = new List<Blob>
		{
			new Text { Content = FormatFilter( filter ), FontWeight = NotificationViews.TitleWeight, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
			NotificationViews.BuildCount( count ),
			new Spacer(),
			NotificationViews.BuildQuietButton( "Mark all read", () => context.Run( () => context.App.Notifications.MarkAllRead() ), unread > 0 ),
		};
		if ( layout != PageLayout.Narrow )
			header.Add( NotificationViews.BuildQuietButton( "Clear read", () => context.Run( UpdateClearRead ), view.Items.Any( item => item.IsRead ) ) );
		var children = new List<Blob>
		{
			new Row
			{
				Key = "list-header",
				FlexShrink = 0,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing / 2,
				Height = 44,
				Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing * 1.5 ),
				BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
				BorderColor = theme.Divider,
				Children = header,
			},
		};
		// Without the rail, its filters show as tabs over the list: the states first, then each source and project.
		if ( layout != PageLayout.Wide )
			children.Add( BuildTabs( view, unread, ListFacetFilters( view ) ) );
		children.Add( BuildSearch() );
		children.Add( new Container
		{
			Key = "rows",
			FlexGrow = 1,
			MinHeight = 0,
			Padding = new Edges( 0, 0, theme.Spacing * 1.5, 0 ),
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			Children = list,
		} );
		return new Container
		{
			Key = "notifications-list",
			Width = layout == PageLayout.Narrow ? Length.Percent( 100 ) : layout == PageLayout.Medium ? MediumListWidth : ListWidth,
			MinWidth = layout == PageLayout.Narrow ? 0 : ListMinWidth,
			FlexShrink = 1,
			MinHeight = 0,
			BorderWidth = new Edges( 0, layout == PageLayout.Narrow ? 0 : theme.BorderWidth, 0, 0 ),
			BorderColor = theme.Divider,
			Children = children,
		};
	}

	/// <summary>Lists the source and project filters the feed holds, most used first, for the tabs that replace the rail.</summary>
	private List<(string Label, NotificationFilter Filter)> ListFacetFilters( FeedView view ) =>
	[
		.. view.Items.GroupBy( item => ComputeSourceName( item, ComputePanelType ) ).OrderByDescending( group => group.Count() )
			.Select( group => (group.Key, new NotificationFilter( NotificationFilterKind.Source, group.Key )) ),
		.. view.Items.Where( item => item.Project is { Length: > 0 } ).GroupBy( item => item.Project! ).OrderByDescending( group => group.Count() )
			.Select( group => (group.Key, new NotificationFilter( NotificationFilterKind.Project, group.Key )) ),
	];

	/// <summary>Names a filter for the list's header.</summary>
	private static string FormatFilter( NotificationFilter filter ) => filter.Kind switch
	{
		NotificationFilterKind.Waiting => "Waiting for you",
		NotificationFilterKind.Unread => "Unread",
		NotificationFilterKind.Source or NotificationFilterKind.Project => filter.Value ?? "",
		_ => "All",
	};

	/// <summary>Builds the search box, which keeps the rows whose title, message or project holds its text.</summary>
	private Row BuildSearch()
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "search",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Height = theme.ControlHeight,
			Margin = new Edges( theme.Spacing * 1.25, theme.Spacing * 1.5, theme.Spacing / 2, theme.Spacing * 1.5 ),
			Padding = new Edges( 0, theme.Spacing ),
			BackgroundColor = theme.Panel,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			Children =
			[
				NotificationViews.BuildGlyph( SearchGlyph ),
				new TextEntry
				{
					Key = "search-entry",
					Value = _query.Value,
					Controlled = true,
					Placeholder = "Search notifications",
					PlaceholderColor = theme.Muted,
					AccessibilityName = "Search notifications",
					FlexGrow = 1,
					MinWidth = 0,
					Height = theme.ControlHeight - 2,
					FontSize = theme.FontSize,
					Color = theme.Text,
					BackgroundColor = Color.Transparent,
					BorderWidth = 0,
					OnChange = value => context.Run( () => _query.Value = value ),
				},
			],
		};
	}

	/// <summary>Builds the reading pane of the selected row: its header with Open, Mark read and Remove, then its facts, its text, its buttons, and the question it waits on.</summary>
	private Container BuildReading( FeedView view, List<NotificationRow> rows, List<NotificationPrompt> prompts, string? selected, PageLayout layout )
	{
		var theme = Theme.Current;
		Blob header;
		var column = new List<Blob>();
		if ( prompts.FirstOrDefault( prompt => "prompt:" + prompt.Key == selected ) is { } prompt )
		{
			header = BuildReadingHeader( NotificationViews.BuildIcon( BuildGlyph( AgentGlyph ), NotificationTone.Attention, theme.Canvas ), prompt.Title, "waiting",
				[NotificationViews.BuildButton( "Open chat", () => context.Run( () => Open( prompt ) ), glyph: OpenGlyph )] );
			if ( BuildPromptCard( prompt, "page", null ) is { } card )
				column.Add( card );
		}
		else if ( rows.FirstOrDefault( row => row.Key == selected ) is { } row )
		{
			var latest = row.Items[0];
			var tone = ComputeTone( row, view );
			var actions = new List<Blob>();
			if ( row.Items.Count == 1 )
				actions.Add( NotificationViews.BuildButton( OpenLabel( latest ), () => context.Run( () => Open( latest ) ), glyph: OpenGlyph ) );
			if ( row.Items.Any( item => !item.IsRead ) )
				actions.Add( NotificationViews.BuildIconButton( "Mark read", ReadGlyph, () => context.Run( () =>
				{
					foreach ( var item in row.Items )
						context.App.Notifications.MarkRead( item.Id );
				} ) ) );
			actions.Add( NotificationViews.BuildIconButton( row.Items.Count == 1 ? "Remove" : $"Remove these {row.Items.Count}", RemoveGlyph, () => context.Run( () =>
			{
				foreach ( var item in row.Items )
					context.App.Notifications.Remove( item.Id );
				_selected.Value = null;
			} ) ) );
			var subtitle = string.Join( " · ", new[] { latest.Project, tone == NotificationTone.Attention && view.Waiting.ContainsKey( latest.Id ) ? "waiting" : null }.OfType<string>() );
			header = BuildReadingHeader( NotificationViews.BuildIcon( BuildRowIcon( row ), tone, theme.Canvas ), ComputeTitle( row ), subtitle, actions );

			column.Add( BuildFacts( row ) );
			if ( row.Items.Count == 1 && view.Waiting.TryGetValue( latest.Id, out var waiting ) && BuildPromptCard( waiting, "page", null ) is { } card )
			{
				// The question says what the chat needs, so its notification's text, such as "Your input is needed", would only repeat it.
				column.Add( card );
			}
			else if ( row.Items.Count == 1 )
			{
				column.Add( BuildBody( latest.Message ) );
				if ( BuildActions( latest, OpenLink ) is { } buttons )
					column.Add( buttons );
			}
			else
				column.Add( BuildRunItems( row, view ) );
		}
		else
			return new Container
			{
				Key = "notifications-reading",
				FlexGrow = 1,
				MinWidth = 0,
				Children = [BuildEmpty( "Pick a notification to read it." )],
			};

		if ( layout == PageLayout.Narrow )
		{
			// Narrow, the pane stands in for the list, so its header leads back to it.
			var back = NotificationViews.BuildIconButton( "Back to notifications", BackGlyph, () => context.Run( () => _isReading.Value = false ) );
			back.Margin = new Edges( 0, 0, 0, theme.Spacing / 2 );
			header = new Row
			{
				Key = "reading-header-back",
				FlexShrink = 0,
				AlignItems = AlignItems.Center,
				BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
				BorderColor = theme.Divider,
				Children = [back, new Container { FlexGrow = 1, MinWidth = 0, Margin = new Edges( 0, 0, -theme.BorderWidth, 0 ), Children = [header] }],
			};
		}

		return new Container
		{
			Key = "notifications-reading",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Notification",
			FlexGrow = 1,
			FlexShrink = 1,
			FlexBasis = 0,
			MinWidth = 0,
			MinHeight = 0,
			Children =
			[
				header,
				new Container
				{
					Key = "reading-scroll",
					FlexGrow = 1,
					MinHeight = 0,
					OverflowY = Overflow.Scroll,
					ScrollbarY = ViewStyle.BuildScrollbar(),
					Children =
					[
						new Container
						{
							Key = "reading-column:" + selected,
							Width = Length.Percent( 100 ),
							MaxWidth = ReadingWidth,
							AlignSelf = AlignSelf.Center,
							FlexShrink = 0,
							Gap = theme.Spacing * 1.75,
							Padding = new Edges( theme.Spacing * 2.5, theme.Spacing * 3, theme.Spacing * 5, theme.Spacing * 3 ),
							Children = column,
						},
					],
				},
			],
		};
	}

	/// <summary>Names the button that opens a notification after what it opens: its chat, its panel, or its link.</summary>
	private string OpenLabel( LoknNotification item ) => ComputeSource( item, ComputePanelType ) == NotificationSource.Agent ? "Open chat"
		: item.PanelId is not null ? "Open"
		: item.Url is not null ? "Open link"
		: "Open";

	/// <summary>Builds the reading pane's header: the icon, the title, a muted subtitle, and the actions at the right.</summary>
	private static Row BuildReadingHeader( Blob icon, string title, string subtitle, List<Blob> actions )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			icon,
			new Text { Content = title, FontWeight = NotificationViews.TitleWeight, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
		};
		// The subtitle gives way first, so a narrow header keeps the title.
		if ( subtitle.Length > 0 )
			children.Add( new Text { Content = "· " + subtitle, Color = theme.Muted, FlexShrink = 100, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis } );
		children.Add( new Spacer() );
		children.AddRange( actions );
		return new Row
		{
			Key = "reading-header",
			Height = 44,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinWidth = 0,
			Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing * 2 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Divider,
			Children = children,
		};
	}

	/// <summary>Builds a row's facts as small outlined pills, then when it came.</summary>
	private static Row BuildFacts( NotificationRow row )
	{
		var theme = Theme.Current;
		var latest = row.Items[0];
		var children = new List<Blob>();
		foreach ( var fact in latest.Details ?? [] )
			children.Add( new Container
			{
				Key = "fact:" + fact,
				Padding = new Edges( 0, theme.Spacing ),
				BorderRadius = 999,
				BorderWidth = theme.BorderWidth,
				BorderColor = theme.Border,
				Children = [new Text { Content = fact, FontSize = ViewStyle.SmallSize, Color = theme.Muted, TextWrap = TextWrap.NoWrap }],
			} );
		var time = latest.Time.ToLocalTime();
		var clock = time.ToString( "t", CultureInfo.CurrentCulture );
		var when = time.Date == DateTime.Today ? $"Today, {clock}" : $"{time.ToString( "MMMM d", CultureInfo.CurrentCulture )}, {clock}";
		if ( row.Items.Count > 1 )
			when = $"{Count( row.Items.Count, "update", "updates" )} · {when}";
		children.Add( new Text { Key = "when", Content = when, FontSize = ViewStyle.SmallSize, Color = theme.Muted } );
		return new Row { Key = "facts", FlexWrap = FlexWrap.Wrap, AlignItems = AlignItems.Center, Gap = theme.Spacing * 0.75, Children = children };
	}

	/// <summary>Builds each update of a run as a line that opens it: its icon and mark, its text, and its time.</summary>
	private Container BuildRunItems( NotificationRow row, FeedView view )
	{
		var theme = Theme.Current;
		var lines = new List<Blob>();
		foreach ( var item in row.Items )
		{
			var single = new NotificationRow( item.Id.ToString( "N" ), NotificationRowKind.Activity, ComputeSummary( [item], view.Kinds ), [item] );
			lines.Add( BuildRow( single, view, isSelected: false, () => Open( item ) ) );
		}
		return new Container
		{
			Key = "run-items",
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BorderRadius = theme.Radius * 2,
			Overflow = Overflow.Hidden,
			Children = lines,
		};
	}
}
