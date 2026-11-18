using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the Search palette: a modal search over every module's <see cref="SearchRegistry"/> providers, with a tab per type.</summary>
/// <remarks>
/// With an empty query, All shows Recents and the first items of each type; a query ranks matches, grouped by type in All and in one list
/// in a type's tab. Up and Down select, Enter runs, Alt+Enter opens the selected item's other actions, Left and Right at either end of the
/// query (or Ctrl+Left and Ctrl+Right anywhere) change tab, <c>/</c> starting the query picks a type, and Escape closes.
/// </remarks>
internal sealed partial class ShellView
{
	/// <summary>Defines the palette width in logical pixels.</summary>
	private const int PaletteWidth = 640;

	/// <summary>Defines the palette height in logical pixels; it stays the same while the results change.</summary>
	private const int PaletteHeight = 480;

	/// <summary>Defines the palette top offset in logical pixels.</summary>
	private const int PaletteTop = 90;

	/// <summary>Defines the maximum palette height as a viewport percentage.</summary>
	private const int MaximumHeightPercent = 80;

	/// <summary>Defines the maximum palette width as a viewport percentage.</summary>
	private const int MaximumWidthPercent = 95;

	/// <summary>Defines the top inset as a fraction of a small viewport's height.</summary>
	private const double TopInsetFraction = 0.2;

	/// <summary>Defines the palette scrim opacity from zero to one.</summary>
	private const double ScrimOpacity = 0.45;

	/// <summary>Defines the search field row height in logical pixels.</summary>
	private const int SearchHeight = 46;

	/// <summary>Defines the search field's text size in logical pixels.</summary>
	private const int SearchFontSize = 15;

	/// <summary>Defines a result row's height in logical pixels.</summary>
	private const int ResultHeight = 36;

	/// <summary>Defines the size of a result's icon tile in logical pixels.</summary>
	private const int ResultIconSize = 24;

	/// <summary>Defines the size of a result's icon glyph in logical pixels.</summary>
	private const int ResultGlyphSize = 13;

	/// <summary>Defines the size of the quiet text in headings, details and the footer in logical pixels.</summary>
	private const int SmallFontSize = 12;

	/// <summary>Defines the weight of a selected tab's name.</summary>
	private const int TabWeight = 600;

	/// <summary>Defines the type the pane menu's "Open a panel…" searches: the Workspace module's commands.</summary>
	private const string ActionsType = "actions";

	/// <summary>Defines the Segoe Fluent Cancel glyph of the close button.</summary>
	private const string CloseSearchGlyph = "\uE711";

	/// <summary>Defines the Segoe Fluent More glyph of the button that opens an item's other actions.</summary>
	private const string MoreActionsGlyph = "\uE712";

	/// <summary>Retains the search input handle across palette rebuilds.</summary>
	private readonly ElementHandle _searchHandle = new();

	/// <summary>Targets the selected result, which keyboard moves scroll into view.</summary>
	private readonly ElementHandle _selectedHandle = new();

	/// <summary>Anchors the selected result's actions menu to its ••• button.</summary>
	private readonly ElementHandle _actionsHandle = new();

	/// <summary>Stores the text in the search field.</summary>
	private string _query = "";

	/// <summary>Stores the shown tab's type, or null for All.</summary>
	private string? _searchType;

	/// <summary>Stores the selected row's position among the results or type choices.</summary>
	private int _selectedResult;

	/// <summary>Stores the result whose actions menu is open, or null.</summary>
	private SearchHit? _actionsHit;

	/// <summary>Describes what the palette shows for the current text and tab.</summary>
	/// <param name="Types">The tabs after All.</param>
	/// <param name="Type">The shown tab's type, or null for All.</param>
	/// <param name="Groups">The result headings and their items.</param>
	/// <param name="Hits">The results in order, which the selection moves through.</param>
	/// <param name="Choices">The types a <c>/</c> prefix is choosing between, or null.</param>
	private sealed record SearchView( IReadOnlyList<SearchType> Types, string? Type, IReadOnlyList<SearchGroup> Groups, IReadOnlyList<SearchHit> Hits, IReadOnlyList<SearchType>? Choices )
	{
		/// <summary>Gets how many rows can be selected.</summary>
		internal int Count => Choices?.Count ?? Hits.Count;
	}

	/// <summary>Opens the palette, optionally with text already typed and on one type's tab.</summary>
	/// <param name="query">The text to start with.</param>
	/// <param name="type">The tab to start on, or null for All.</param>
	private void UpdatePalette( string query = "", string? type = null )
	{
		_query = query;
		_searchType = type;
		_selectedResult = 0;
		_actionsHit = null;
		_isPaletteOpen = true;
		MarkChanged();
	}

	/// <summary>Closes the palette and restores eligible prior focus through its scope.</summary>
	private void ReleasePalette()
	{
		_isPaletteOpen = false;
		_actionsHit = null;
		MarkChanged();
	}

	/// <summary>Computes the tabs and what the text finds in the shown one; a tab that lists nothing now falls back to All.</summary>
	private SearchView ComputeSearch()
	{
		var types = _app.Search.ComputeTypes();
		var type = types.Any( item => item.Id == _searchType ) ? _searchType : null;
		var filter = SearchRegistry.ReadFilter( _query, types );
		if ( filter.Choices is { } choices )
			return new( types, type, [], [], choices );

		var groups = _app.Search.Search( filter.Query, type );
		return new( types, type, groups, [.. groups.SelectMany( group => group.Hits )], null );
	}

	/// <summary>Builds the palette's modal scope: the search field, the type tabs, the results and the key hints.</summary>
	private Blob BuildPalette()
	{
		var theme = Theme.Current;
		var view = ComputeSearch();
		var selected = Math.Clamp( _selectedResult, 0, Math.Max( 0, view.Count - 1 ) );
		var portal = new Portal
		{
			Key = "palette",
			Top = Math.Min( PaletteTop, Host.Size.Height * TopInsetFraction ),
			Height = PaletteHeight,
			MaxHeight = Length.Percent( MaximumHeightPercent ),
			Left = Math.Max( theme.Spacing, (Host.Size.Width - PaletteWidth) / 2 ),
			Width = PaletteWidth,
			MaxWidth = Length.Percent( MaximumWidthPercent ),
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Overflow = Overflow.Hidden,
			Scrim = Color.Black.WithAlpha( ScrimOpacity ),
			FocusScope = new FocusScopeOptions
			{
				Modal = true,
				InitialFocus = _searchHandle,
			},
			AccessibilityRole = AccessibilityRole.Dialog,
			AccessibilityName = "Search",
			OnDismiss = _ => ReleasePalette(),
		};
		portal.Add( BuildSearchHeader() );
		portal.Add( BuildSearchTabs( view ) );
		portal.Add( BuildSearchResults( view, selected ) );
		portal.Add( BuildSearchFooter() );
		if ( _actionsHit is { } hit )
		{
			portal.Add( Mount<ContextMenuInput, ContextMenu>( "search-actions", new( _actionsHandle, $"{hit.Item.Title} actions",
				[.. hit.Item.Actions.Select( action => action with { Run = () => UpdateSearchAction( hit, action ) } )], ReleaseSearchActions )
			{
				Placement = PortalPlacement.BottomEnd,
				Width = 220,
			} ) );
		}

		return portal;
	}

	/// <summary>Builds the search field and the close button.</summary>
	private Row BuildSearchHeader()
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "search-header",
			Height = SearchHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing * 2 ),
			Children =
			[
				new TextEntry
				{
					Key = "search",
					Handle = _searchHandle,
					AutoFocus = true,
					Value = _query,
					Controlled = true,
					Placeholder = "Search",
					FontSize = SearchFontSize,
					FlexGrow = 1,
					MinWidth = 0,
					Padding = 0,
					BorderWidth = 0,
					BackgroundColor = Color.Transparent,
					AccessibilityName = "Search",
					OnChange = UpdateSearchQuery,
				},
				BuildSearchIcon( "Close search", CloseSearchGlyph, ReleasePalette ),
			],
		};
	}

	/// <summary>Builds the tab row: All, then each type that lists something now.</summary>
	private Row BuildSearchTabs( SearchView view )
	{
		var theme = Theme.Current;
		var children = new List<Blob> { BuildSearchTab( null, "All", view.Type is null ) };
		foreach ( var type in view.Types )
			children.Add( BuildSearchTab( type.Id, type.Title, view.Type == type.Id ) );

		return new Row
		{
			Key = "search-tabs",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing * 1.5, theme.Spacing, theme.Spacing * 1.5 ),
			BorderColor = theme.Border,
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			OverflowX = Overflow.Scroll,
			AccessibilityRole = AccessibilityRole.TabList,
			AccessibilityName = "Result types",
			Children = children,
		};
	}

	/// <summary>Builds one tab; the shown one is raised and bold.</summary>
	private Button BuildSearchTab( string? type, string title, bool isShown )
	{
		var theme = Theme.Current;
		return new Button
		{
			Key = "tab:" + (type ?? ""),
			FlexShrink = 0,
			Height = theme.ControlHeight - 4,
			Padding = new Edges( 0, theme.Spacing * 1.25 ),
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			BackgroundColor = isShown ? theme.Hover : Color.Transparent,
			Color = isShown ? theme.Text : theme.Muted,
			Focusable = false,
			Cursor = Cursor.Pointer,
			AccessibilityRole = AccessibilityRole.Tab,
			AccessibilityName = isShown ? $"{title}, shown" : title,
			OnClick = () => UpdateSearchType( type ),
			HoverStyle = new Style { Color = theme.Text },
			Children = [new Text { Content = title, FontWeight = isShown ? TabWeight : 400 }],
		};
	}

	/// <summary>Builds the scrolling results: headings and rows, the type choices while <c>/</c> picks one, or a note when nothing matches.</summary>
	private Container BuildSearchResults( SearchView view, int selected )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( view.Choices is { } choices )
		{
			children.Add( BuildSearchHeading( "Filter by type", isFirst: true ) );
			foreach ( var (index, type) in choices.Index() )
				children.Add( BuildSearchRow( "type:" + type.Id, index, index == selected, null, type.Title, "/" + type.Id, [], () => UpdateSearchChoice( type ) ) );
			if ( choices.Count == 0 )
				children.Add( BuildSearchNote( "No type has that name." ) );
		}
		else
		{
			var index = 0;
			foreach ( var group in view.Groups )
			{
				children.Add( BuildSearchHeading( group.Title, isFirst: children.Count == 0 ) );
				foreach ( var hit in group.Hits )
				{
					var row = index;
					children.Add( BuildSearchRow( $"{group.Title}:{hit.ProviderId}:{hit.Item.Id}", row, row == selected, hit.Item.Glyph, hit.Item.Title, hit.Item.Detail,
						hit.Item.Actions, () => UpdateSearchRun( hit ) ) );
					index++;
				}
			}

			if ( view.Hits.Count == 0 )
				children.Add( BuildSearchNote( _query.Trim().Length > 0 ? "Nothing matches your search." : "Nothing to show yet." ) );
		}

		return new Container
		{
			Key = "search-results",
			FlexGrow = 1,
			FlexShrink = 1,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			Gap = 1,
			Children = children,
		};
	}

	/// <summary>Builds a group heading, such as Recents or a type's name.</summary>
	private static Text BuildSearchHeading( string title, bool isFirst )
	{
		var theme = Theme.Current;
		return new Text
		{
			Key = "heading:" + title,
			Content = title,
			FontSize = SmallFontSize,
			Color = theme.Muted,
			Padding = new Edges( isFirst ? theme.Spacing / 2 : theme.Spacing * 1.5, theme.Spacing, theme.Spacing / 2, theme.Spacing ),
		};
	}

	/// <summary>Builds the muted line shown when there is nothing to list.</summary>
	private static Text BuildSearchNote( string text ) => new()
	{
		Key = "note",
		Content = text,
		Padding = Theme.Current.Spacing,
		Color = Theme.Current.Muted,
	};

	/// <summary>Builds one result row: its icon, title and detail; the selected row also shows ••• for its other actions and the Enter hint.</summary>
	private Row BuildSearchRow( string key, int index, bool isSelected, string? glyph, string title, string? detail, IReadOnlyList<MenuAction> actions, Action run )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Button
			{
				Key = "run",
				FlexGrow = 1,
				FlexShrink = 1,
				MinWidth = 0,
				Height = ResultHeight,
				FlexDirection = FlexDirection.Row,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Padding = new Edges( 0, theme.Spacing ),
				BackgroundColor = Color.Transparent,
				Focusable = false,
				Cursor = Cursor.Pointer,
				AccessibilityName = detail is { Length: > 0 } ? $"{title}, {detail}" : title,
				OnClick = run,
				Children =
				[
					BuildSearchGlyph( glyph ),
					new Text
					{
						Content = title,
						Color = theme.Text,
						MinWidth = 0,
						FlexShrink = 1,
						TextWrap = TextWrap.NoWrap,
						TextMaxLines = 1,
						TextTrimming = TextTrimming.Ellipsis,
					},
					new Spacer(),
					.. detail is { Length: > 0 }
						? [new Text { Content = detail, FontSize = SmallFontSize, Color = theme.Muted, FlexShrink = 0, MaxWidth = PaletteWidth / 3, TextWrap = TextWrap.NoWrap, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis }]
						: Array.Empty<Blob>(),
				],
			},
		};
		if ( isSelected && actions.Count > 0 )
		{
			var more = BuildSearchIcon( $"{title} actions", MoreActionsGlyph, UpdateSearchActions );
			more.Handle = _actionsHandle;
			children.Add( more );
		}
		if ( isSelected )
			children.Add( new Text { Content = "↵", Color = theme.Muted, FlexShrink = 0, Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing / 2 ) } );

		return new Row
		{
			Key = key,
			Handle = isSelected ? _selectedHandle : null,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			BorderRadius = theme.Radius,
			BackgroundColor = isSelected ? theme.Hover : Color.Transparent,
			OnPointerEnter = _ =>
			{
				if ( _selectedResult == index )
					return;

				_selectedResult = index;
				Rebuild();
			},
			Children = children,
		};
	}

	/// <summary>Builds a result's icon tile: its glyph from the icon font, or a dot.</summary>
	private static Container BuildSearchGlyph( string? glyph )
	{
		var theme = Theme.Current;
		return new Container
		{
			Width = ResultIconSize,
			Height = ResultIconSize,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			Color = theme.Muted,
			Children =
			[
				glyph is null
					? new Text { Content = "•" }
					: new Text { Content = glyph, FontFamily = TitleBar.CaptionFont.Family, FontSize = ResultGlyphSize },
			],
		};
	}

	/// <summary>Builds a small icon button in the palette, such as close or •••.</summary>
	private static Button BuildSearchIcon( string name, string glyph, Action action )
	{
		var theme = Theme.Current;
		return new Button
		{
			Key = name,
			Width = theme.ControlHeight,
			Height = theme.ControlHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			BackgroundColor = Color.Transparent,
			Color = theme.Muted,
			Focusable = false,
			Cursor = Cursor.Pointer,
			AccessibilityName = name,
			OnClick = action,
			HoverStyle = new Style { BackgroundColor = theme.CaptionHover, Color = theme.Text },
			Children = [new Text { Content = glyph, FontFamily = TitleBar.CaptionFont.Family, FontSize = ResultGlyphSize }],
		};
	}

	/// <summary>Builds the key hints along the bottom.</summary>
	private static Row BuildSearchFooter()
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "search-footer",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing * 2,
			Padding = new Edges( theme.Spacing, theme.Spacing * 2 ),
			BorderColor = theme.Border,
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			Children =
			[
				BuildSearchHint( "Close", "Esc" ),
				BuildSearchHint( "Change type", "← →" ),
				BuildSearchHint( "Filters", "/" ),
				new Spacer(),
				BuildSearchHint( "Actions", "Alt ↵" ),
			],
		};
	}

	/// <summary>Builds one key hint: what it does, then the key in a small cap.</summary>
	private static Row BuildSearchHint( string label, string key )
	{
		var theme = Theme.Current;
		return new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			FlexShrink = 0,
			Children =
			[
				new Text { Content = label, FontSize = SmallFontSize, Color = theme.Muted },
				new Text
				{
					Content = key,
					FontSize = SmallFontSize,
					Color = theme.Muted,
					Padding = new Edges( 0, theme.Spacing / 2 ),
					BorderRadius = theme.Radius,
					BorderColor = theme.Border,
					BorderWidth = theme.BorderWidth,
				},
			],
		};
	}

	/// <summary>Takes typed text; a finished <c>/type </c> prefix switches to that type's tab and leaves what follows it.</summary>
	private void UpdateSearchQuery( string value )
	{
		if ( SearchRegistry.ReadFilter( value, _app.Search.ComputeTypes() ) is { Type: { } type } filter )
		{
			_searchType = type.Id;
			value = filter.Query;
		}

		_query = value;
		_selectedResult = 0;
		Rebuild();
	}

	/// <summary>Shows a type's tab, or All for null.</summary>
	private void UpdateSearchType( string? type )
	{
		_searchType = type;
		_selectedResult = 0;
		Rebuild();
	}

	/// <summary>Shows the tab a step away from the shown one, wrapping around past All.</summary>
	private void UpdateSearchType( SearchView view, int step )
	{
		string?[] tabs = [null, .. view.Types.Select( type => type.Id )];
		var index = Array.IndexOf( tabs, view.Type );
		UpdateSearchType( tabs[((index + step) % tabs.Length + tabs.Length) % tabs.Length] );
	}

	/// <summary>Shows the type a <c>/</c> prefix chose and clears the prefix.</summary>
	private void UpdateSearchChoice( SearchType type )
	{
		_query = "";
		UpdateSearchType( type.Id );
	}

	/// <summary>Closes the palette, then remembers and runs a result.</summary>
	private void UpdateSearchRun( SearchHit hit )
	{
		ReleasePalette();
		_app.Search.Run( hit );
	}

	/// <summary>Closes the palette, then remembers the result and runs one of its other actions.</summary>
	private void UpdateSearchAction( SearchHit hit, MenuAction action )
	{
		ReleasePalette();
		_app.Search.Run( hit, action );
	}

	/// <summary>Opens the selected result's actions menu, when it has other actions.</summary>
	private void UpdateSearchActions()
	{
		var view = ComputeSearch();
		if ( view.Choices is not null || view.Hits.Count == 0 )
			return;

		var hit = view.Hits[Math.Clamp( _selectedResult, 0, view.Hits.Count - 1 )];
		if ( hit.Item.Actions.Count == 0 )
			return;

		_actionsHit = hit;
		Rebuild();
	}

	/// <summary>Closes the actions menu and returns focus to the search field.</summary>
	private void ReleaseSearchActions()
	{
		_actionsHit = null;
		Rebuild();
		_searchHandle.Focus();
	}

	/// <summary>Routes palette keys before the search field sees them: selection, running, tabs, actions and closing.</summary>
	/// <remarks>While the actions menu is open, the menu handles every key.</remarks>
	private bool UpdatePaletteKey( Key key, KeyModifiers modifiers )
	{
		if ( _actionsHit is not null )
			return false;

		var view = ComputeSearch();
		if ( key is Key.Left or Key.Right && (modifiers == KeyModifiers.Primary || (modifiers == KeyModifiers.None && IsCaretAtEdge( key == Key.Left ))) )
		{
			UpdateSearchType( view, key == Key.Left ? -1 : 1 );
			return true;
		}

		if ( key == Key.Enter && modifiers == new KeyModifiers { Alt = true } )
		{
			UpdateSearchActions();
			return true;
		}

		if ( modifiers != KeyModifiers.None )
			return false;

		if ( key == Key.Escape )
		{
			// Escape first leaves a type being chosen, then closes.
			if ( view.Choices is not null )
				UpdateSearchQuery( "" );
			else
				ReleasePalette();
			return true;
		}

		if ( key is Key.Up or Key.Down )
		{
			_selectedResult = Math.Clamp( _selectedResult + (key == Key.Down ? 1 : -1), 0, Math.Max( 0, view.Count - 1 ) );
			Rebuild();
			// The selected row mounts its handle on the next build, so it scrolls into view after that.
			Post( () => _selectedHandle.ScrollIntoView() );
			return true;
		}

		if ( key != Key.Enter )
			return false;

		var selected = Math.Clamp( _selectedResult, 0, Math.Max( 0, view.Count - 1 ) );
		if ( view.Choices is { Count: > 0 } choices )
			UpdateSearchChoice( choices[selected] );
		else if ( view.Hits.Count > 0 )
			UpdateSearchRun( view.Hits[selected] );
		return true;
	}

	/// <summary>Reports whether the caret is at the start (or end) of the search text with nothing selected, so Left (or Right) changes tab.</summary>
	private bool IsCaretAtEdge( bool isStart )
	{
		if ( Host.Focus.Selection is not { } selection )
			return true;

		var edge = isStart ? 0 : _query.Length;
		return selection.Anchor.Offset == edge && selection.Active.Offset == edge;
	}

	/// <summary>Formats a command's optional keyboard shortcut for display.</summary>
	internal static string ComputeShortcutLabel( CommandShortcut? shortcut )
	{
		if ( shortcut is not { } value )
			return "";

		var modifiers = value.Modifiers;
		return (modifiers.Ctrl ? "Ctrl+" : "")
			+ (modifiers.Shift ? "Shift+" : "")
			+ (modifiers.Alt ? "Alt+" : "")
			+ (value.Key == Key.BackSlash ? "\\" : value.Key.ToString());
	}
}
