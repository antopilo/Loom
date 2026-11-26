using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Builds the sidebar's rows the way the chat sidebar draws them, for every module that lists projects, chats or tasks there.</summary>
/// <remarks>
/// One instance belongs to one sidebar view and holds its row state: the row under the pointer, whose actions show, the row whose
/// menu is open, the title being renamed in place, the collapsed groups, and the search. Rows are highlighted edge to edge on
/// hover or selection; a right-click opens the row's menu at the pointer, its … button below the button.
/// </remarks>
/// <param name="context">The view's dispatch, which every action runs through.</param>
public sealed partial class SidebarRows( PanelContext context )
{
	/// <summary>Defines the sidebar content inset in logical pixels.</summary>
	public const int ContentPadding = 12;

	/// <summary>Defines a group heading's emphasis weight, such as a project's name.</summary>
	public const int GroupWeight = 600;

	/// <summary>Defines a group heading's icon width and height in logical pixels, such as a project's icon.</summary>
	public const int GroupIconSize = 16;

	/// <summary>Defines the width of a linked heading's chevron button in logical pixels, the first column of the sidebar's grid.</summary>
	public const int ChevronColumn = 16;

	/// <summary>Defines how far a group's rows are inset so their titles start where the linked heading's name does: past the chevron and the icon column.</summary>
	public const int ChildIndent = ChevronColumn + GroupIconSize;

	/// <summary>Gets how far a group's rows are inset so their titles start where its linked heading's name does: past the icon column only when the group has an icon.</summary>
	/// <param name="hasIcon">Whether the heading shows an icon.</param>
	public static int ComputeChildIndent( bool hasIcon ) => hasIcon ? ChildIndent : ChildIndent - GroupIconSize - Theme.Current.Spacing / 2;

	/// <summary>Defines the fallback selection movement duration when the shell setting is unavailable, in milliseconds.</summary>
	private const int DefaultMoveDuration = 140;

	/// <summary>Tracks collapsed groups.</summary>
	private readonly State<HashSet<Guid>> _collapsed = new( [] );

	/// <summary>Tracks the row whose context menu is open.</summary>
	private readonly State<Guid> _menu = new( Guid.Empty );

	/// <summary>Stores where on its row a right-click opened the menu, in the row's coordinates, or null when its … button opened it.</summary>
	private ElementRect? _menuAt;

	/// <summary>Targets each row, so a right-click menu opens at the pointer on it.</summary>
	private readonly Dictionary<Guid, ElementHandle> _rows = [];

	/// <summary>Targets the sidebar content whose selected row the moving highlight follows.</summary>
	private readonly ElementHandle _surface = new();

	/// <summary>Targets each row's … button, so its menu opens below it.</summary>
	private readonly Dictionary<Guid, ElementHandle> _moreButtons = [];

	/// <summary>Tracks the row whose title is being edited in place.</summary>
	private readonly State<Guid> _renaming = new( Guid.Empty );

	/// <summary>Retains the title being typed while renaming.</summary>
	private readonly State<string> _renameDraft = new( "" );

	/// <summary>Targets the title editor so renaming starts with the whole title selected.</summary>
	private readonly ElementHandle _renameEntry = new();

	/// <summary>Tracks the row under the pointer, whose actions are revealed.</summary>
	private readonly State<Guid> _hovered = new( Guid.Empty );

	/// <summary>Reports whether a row is under the pointer, subscribing the building view.</summary>
	public bool IsHovered( Guid id ) => _hovered.Value == id;

	/// <summary>Reports whether a row's menu is open, subscribing the building view.</summary>
	public bool IsMenuOpen( Guid id ) => _menu.Value == id;

	/// <summary>Reports whether a row's hidden actions show: the pointer is on it, or its menu or its + grid is open.</summary>
	public bool IsRevealed( Guid id ) => IsHovered( id ) || IsMenuOpen( id ) || IsStartersOpen( id );

	/// <summary>Closes the open menu.</summary>
	public void CloseMenu() => _menu.Value = Guid.Empty;

	/// <summary>Reports whether a group is collapsed, subscribing the building view.</summary>
	public bool IsCollapsed( Guid id ) => _collapsed.Value.Contains( id );

	/// <summary>Collapses an expanded group or expands a collapsed one.</summary>
	public void ToggleCollapsed( Guid id )
	{
		var collapsed = new HashSet<Guid>( _collapsed.Value );
		if ( !collapsed.Remove( id ) )
			collapsed.Add( id );
		_collapsed.Value = collapsed;
	}

	/// <summary>Builds a left-aligned accessible sidebar action.</summary>
	public Button BuildAction( string title, Action action, string? accessibilityName = null )
	{
		var button = Theme.Current.BuildButton( title, () => context.Run( action ) );
		button.JustifyContent = JustifyContent.Center;
		button.AlignItems = AlignItems.FlexStart;
		button.AccessibilityName = accessibilityName ?? title;
		return button;
	}

	/// <summary>Builds the Search action, drawn like the actions around it, that opens the Search palette.</summary>
	public Button BuildSearch() => BuildIconAction( "Search", SearchGlyph, context.App.Search.Open );

	/// <summary>Builds a muted section heading over rows outside any group, such as Chats or Tasks.</summary>
	public static Text BuildSectionHeading( string title ) => new()
	{
		Content = title,
		Color = Theme.Current.Muted,
		Padding = new Edges( Theme.Current.Spacing * 2, Theme.Current.Spacing, Theme.Current.Spacing / 2, Theme.Current.Spacing ),
	};

	/// <summary>Builds the muted line shown when the sidebar has no rows.</summary>
	public static Text BuildEmpty( string message ) => new()
	{
		Content = message,
		Color = Theme.Current.Muted,
		Padding = new Edges( Theme.Current.Spacing, Theme.Current.Spacing ),
	};

	/// <summary>Builds a group's bold heading: its icon, its name, and the disclosure chevron; clicking it folds or unfolds the group.</summary>
	/// <param name="id">The group, whose folding <see cref="IsCollapsed"/> reports.</param>
	/// <param name="name">The group's name.</param>
	/// <param name="icon">The icon before the name, or null for none.</param>
	/// <param name="isCollapsed">Whether the chevron shows the group folded.</param>
	/// <param name="accessibilityName">The heading's accessible name.</param>
	/// <param name="trailing">Marks after the chevron, such as an activity dot.</param>
	public Button BuildGroupHeading( Guid id, string name, Blob? icon, bool isCollapsed, string accessibilityName, IEnumerable<Blob>? trailing = null )
	{
		var heading = BuildAction( name, () => ToggleCollapsed( id ), accessibilityName );
		heading.FlexDirection = FlexDirection.Row;
		heading.JustifyContent = JustifyContent.FlexStart;
		heading.AlignItems = AlignItems.Center;
		heading.Gap = Theme.Current.Spacing / 2;
		heading.FlexGrow = 1;
		heading.MinWidth = 0;
		heading.Children =
		[
			.. icon is null ? Array.Empty<Blob>() : [icon],
			new Text
			{
				Content = name,
				// The name is the user's, so emoji in it fall back to the emoji face.
				FontFamily = HeadingFontFamily,
				FontSize = Theme.Current.FontSize + HeadingSizeStep,
				FontWeight = GroupWeight,
				MinWidth = 0,
				FlexShrink = 1,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			Theme.Current.BuildChevron( !isCollapsed ),
			.. trailing ?? [],
		];
		return heading;
	}

	/// <summary>Builds a group's heading whose name opens the group, such as a project's overview: the icon, then the name in the heading face with an
	/// optional muted count, which opens it. The chevron that folds or unfolds the group hangs in the sidebar's left margin, outside the row, so the name
	/// starts at the row's edge; it shows while the row is hovered or focused, and always while the group is folded.</summary>
	/// <param name="id">The group, whose folding <see cref="IsCollapsed"/> reports.</param>
	/// <param name="name">The group's name.</param>
	/// <param name="icon">The icon before the name, or null for none, which leaves no empty column.</param>
	/// <param name="isCollapsed">Whether the chevron shows the group folded.</param>
	/// <param name="accessibilityName">The name's accessible name, such as "Lokn, open overview".</param>
	/// <param name="open">Runs when the name is clicked.</param>
	/// <param name="badge">A muted count after the name, or null for none.</param>
	/// <param name="trailing">Marks after the name, such as an activity dot.</param>
	/// <returns>The heading and its name button, which takes key bindings such as F2.</returns>
	public (Row Heading, Button Name) BuildLinkedGroupHeading( Guid id, string name, Blob? icon, bool isCollapsed, string accessibilityName, Action open, string? badge = null,
		IEnumerable<Blob>? trailing = null )
	{
		var theme = Theme.Current;
		var isShown = isCollapsed || IsRevealed( id );
		var chevron = new Button
		{
			Key = "chevron",
			Position = PositionType.Absolute,
			Left = -ContentPadding,
			Top = 0,
			Width = ContentPadding,
			Height = theme.ControlHeight,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = isCollapsed ? $"Expand {name}" : $"Collapse {name}",
			// See-through rather than removed while hidden, so it keeps its place and the keyboard still reaches it.
			Opacity = isShown ? 1 : 0,
			TransitionMs = RevealMs,
			TransitionProperties = [TransitionProperty.Opacity],
			HoverStyle = new Style { Opacity = 1 },
			FocusStyle = new Style { Opacity = 1 },
			OnClick = () => context.Run( () => ToggleCollapsed( id ) ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, () => context.Run( () => ToggleCollapsed( id ) ) ),
				new( Key.Space, KeyModifiers.None, () => context.Run( () => ToggleCollapsed( id ) ) ),
			],
			Children = [theme.BuildChevron( !isCollapsed )],
		};
		var label = BuildAction( name, open, accessibilityName );
		label.FlexDirection = FlexDirection.Row;
		label.JustifyContent = JustifyContent.FlexStart;
		label.AlignItems = AlignItems.Center;
		label.Gap = theme.Spacing / 2;
		label.FlexGrow = 1;
		label.MinWidth = 0;
		label.Padding = new Edges( 0, theme.Spacing / 2 );
		label.Children =
		[
			.. icon is null ? Array.Empty<Blob>() : [new Container
			{
				Key = "icon",
				Width = GroupIconSize,
				FlexShrink = 0,
				AlignItems = AlignItems.Center,
				Children = [icon],
			}],
			new Text
			{
				Content = name,
				FontFamily = HeadingFontFamily,
				FontSize = theme.FontSize + HeadingSizeStep,
				FontWeight = GroupWeight,
				MinWidth = 0,
				FlexShrink = 1,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			.. badge is null ? Array.Empty<Blob>() : [new Text { Key = "badge", Content = badge, Color = theme.Muted, FontSize = theme.FontSize - 1, FlexShrink = 0 }],
			.. trailing ?? [],
		];
		var heading = new Row
		{
			AlignItems = AlignItems.Center,
			FlexGrow = 1,
			MinWidth = 0,
			Children = [chevron, label],
		};
		return (heading, label);
	}

	/// <summary>Defines the space between rows in logical pixels.</summary>
	public const int RowGap = 2;

	/// <summary>Builds a group's container: its heading row and rows.</summary>
	public static Container BuildGroup( Guid id, List<Blob> children ) => new()
	{
		Key = id.ToString(),
		Gap = RowGap,
		Children = children,
	};

	/// <summary>Defines the height assumed for a list row until layout measures it, in logical pixels.</summary>
	private const double EstimatedRowHeight = 32;

	/// <summary>Builds one list row with the space above it; one delegate for every build.</summary>
	private static readonly Func<SidebarRow, Blob> BuildListRow = row => new Container { Padding = new Edges( row.Gap, 0, 0, 0 ), Children = [row.Build()] };

	/// <summary>Builds a sidebar: its actions, which stay in place, over its rows, which scroll and build only while in view.</summary>
	/// <param name="name">The sidebar's accessible name.</param>
	/// <param name="actions">The actions at the top, such as New task and Search.</param>
	/// <param name="rows">The rows, top first.</param>
	/// <param name="selectedId">The active row, or null when no visible row is active.</param>
	public Container BuildSidebar( string name, List<Blob> actions, List<SidebarRow> rows, Guid? selectedId ) => new()
	{
		AccessibilityRole = AccessibilityRole.Group,
		AccessibilityName = name,
		FlexGrow = 1,
		MinWidth = 0,
		MinHeight = 0,
		Children = [BuildActions( actions ), BuildRows( rows, selectedId )],
	};

	/// <summary>Builds a sidebar's actions, which stay in place above its rows.</summary>
	public static Container BuildActions( List<Blob> actions ) =>
		new() { Key = "sidebar-actions", Padding = new Edges( ContentPadding, ContentPadding, 0, ContentPadding ), Gap = RowGap, Children = actions };

	/// <summary>Builds a sidebar's rows, which scroll and build only while in view, over the selection rectangle that moves to the active row.</summary>
	/// <remarks>The rows and the rectangle share one clipped layer, so an outer profile animation moves them together.</remarks>
	/// <param name="rows">The rows, top first.</param>
	/// <param name="selectedId">The active row, or null when no visible row is active.</param>
	public Container BuildRows( List<SidebarRow> rows, Guid? selectedId )
	{
		var target = selectedId is { } id && _rows.TryGetValue( id, out var row ) ? row : null;
		var duration = context.App.Settings.Definitions.FirstOrDefault( definition => definition.Id == "shell.move-duration" ) is { } motion
			&& context.App.Settings.GetValue( motion ) is int configured ? configured : DefaultMoveDuration;
		return new Container
		{
			Key = "sidebar-rows",
			FlexGrow = 1,
			MinHeight = 0,
			OverflowX = Overflow.Hidden,
			OverflowY = Overflow.Hidden,
			Children =
			[
				Cell.Mount<SidebarSelectionInput, SidebarSelection>( "selection", new( _surface, target, duration ) ),
				new Virtual
				{
					Key = "list",
					Handle = _surface,
					Source = new MeasuredRowsSource<SidebarRow>( rows, BuildListRow, static _ => EstimatedRowHeight, FollowEnd: false ),
					FlexGrow = 1,
					MinHeight = 0,
					Padding = new Edges( 0, ContentPadding, ContentPadding, ContentPadding ),
					OverflowX = Overflow.Hidden,
					ScrollbarY = ViewStyle.GetScrollbar(),
				},
			],
		};
	}

	/// <summary>Builds a row highlighted edge to edge under the pointer or while showing its menu or + grid, with its actions inside the highlight; right-clicking it opens its menu at the pointer.</summary>
	/// <param name="id">The row.</param>
	/// <param name="children">The row's content and actions.</param>
	/// <param name="order">The row's place among rows the user reorders by dragging, which then drop on it; null for a row that stays put.</param>
	public Row BuildHoverRow( Guid id, List<Blob> children, RowOrder? order = null )
	{
		var row = new Row
		{
			Handle = _rows.TryGetValue( id, out var known ) ? known : _rows[id] = new ElementHandle(),
			AlignItems = AlignItems.Center,
			BorderRadius = Theme.Current.Radius,
			BackgroundColor = _hovered.Value == id || _menu.Value == id || _starters.Value == id ? Theme.Current.Hover : Color.Transparent,
			DropTarget = order is null ? null : BuildRowDrop( order ),
			OnPointerEnter = _ => UpdateHovered( id ),
			OnPointerLeave = _ =>
			{
				if ( _hovered.Value == id )
					UpdateHovered( Guid.Empty );
			},
			OnPointerDown = pointer => context.Run( () =>
			{
				if ( pointer.Button != PointerButton.Secondary )
					return;

				_menuAt = new ElementRect { X = pointer.Position.X, Y = pointer.Position.Y };
				_menu.Value = id;
			} ),
			Children = children,
		};
		if ( order is not null )
			ApplyDrag( row, id, order );
		return row;
	}

	/// <summary>Builds a row's … button, which opens the row's menu below it.</summary>
	public Button BuildMoreButton( Guid id, string accessibilityName, bool isVisible )
	{
		var button = BuildIconButton( MoreGlyph, accessibilityName, () =>
		{
			_menuAt = null;
			_menu.Value = _menu.Value == id ? Guid.Empty : id;
		}, isVisible );
		button.Handle = _moreButtons.TryGetValue( id, out var known ) ? known : _moreButtons[id] = new ElementHandle();
		return button;
	}

	/// <summary>Builds a row's open menu: below its … button, or at the pointer when a right-click opened it.</summary>
	public Blob BuildMenu( Guid id, string name, IReadOnlyList<MenuEntry> entries ) => Cell.Mount<ContextMenuInput, ContextMenu>( "menu", new(
		_menuAt is null ? _moreButtons[id] : _rows[id],
		name,
		entries,
		() => context.Run( CloseMenu ) )
	{
		AnchorRect = _menuAt,
	} );

	/// <summary>Builds a row's main button, filling the row; F2 or a double-click starts renaming it in place.</summary>
	/// <param name="title">The row's title.</param>
	/// <param name="action">Runs on a click, such as opening the row's item.</param>
	/// <param name="beginRename">Starts renaming the row.</param>
	/// <param name="accessibilityName">The accessible name, or null for the title.</param>
	public Button BuildRowButton( string title, Action action, Action beginRename, string? accessibilityName = null )
	{
		var button = BuildAction( title, action, accessibilityName );
		button.FlexGrow = 1;
		button.MinWidth = 0;
		button.FlexDirection = FlexDirection.Row;
		button.JustifyContent = JustifyContent.FlexStart;
		button.AlignItems = AlignItems.Center;
		button.Gap = Theme.Current.Spacing;
		button.KeyBindings = [new( Key.F2, KeyModifiers.None, () => context.Run( beginRename ) )];
		button.OnPointerUp = pointer => context.Run( () =>
		{
			if ( pointer.Button == PointerButton.Primary && pointer.ClickCount == 2 )
				beginRename();
		} );
		return button;
	}

	/// <summary>Reports whether a row's title is being edited, subscribing the building view.</summary>
	public bool IsRenaming( Guid id ) => _renaming.Value == id;

	/// <summary>Turns a row's title into an editor holding its current title, selected so typing replaces it.</summary>
	public void BeginRename( Guid id, string title )
	{
		if ( _renaming.Value == id )
			return;

		_renameDraft.Value = title;
		_renaming.Value = id;
		_renameEntry.SelectText( 0, title.Length );
	}

	/// <summary>Builds the in-place title editor of the row being renamed: Enter or leaving it saves, Escape cancels.</summary>
	/// <param name="id">The row being renamed.</param>
	/// <param name="title">The title it had, which an unchanged or empty one keeps.</param>
	/// <param name="accessibilityName">The editor's accessible name, such as "Rename task".</param>
	/// <param name="save">Saves a changed, trimmed, non-empty title.</param>
	public TextEntry BuildRenameEntry( Guid id, string title, string accessibilityName, Action<string> save ) => new()
	{
		// The editor takes the title's exact place: the row's height, and an inset that keeps the text where it was drawn.
		Key = "rename",
		Handle = _renameEntry,
		AutoFocus = true,
		Value = _renameDraft.Value,
		Controlled = true,
		FlexGrow = 1,
		MinWidth = 0,
		Height = Theme.Current.ControlHeight,
		Padding = new Edges( 0, Theme.Current.Spacing - Theme.Current.BorderWidth ),
		FontSize = Theme.Current.FontSize,
		FontFamily = ViewStyle.FontFamily,
		Color = Theme.Current.Text,
		BackgroundColor = Theme.Current.Panel,
		BorderColor = Theme.Current.Accent,
		BorderWidth = Theme.Current.BorderWidth,
		BorderRadius = Theme.Current.Radius,
		AccessibilityName = accessibilityName,
		OnChange = value => context.Run( () => _renameDraft.Value = value ),
		OnSubmit = value => context.Run( () => UpdateRename( id, title, value, save ) ),
		OnBlur = _ => context.Run( () => UpdateRename( id, title, _renameDraft.Value, save ) ),
		KeyBindings = [new( Key.Escape, KeyModifiers.None, () => context.Run( () => _renaming.Value = Guid.Empty ) )],
	};

	/// <summary>Ends renaming a row, saving a typed title that is not empty and differs from the old one.</summary>
	private void UpdateRename( Guid id, string title, string typed, Action<string> save )
	{
		if ( _renaming.Value != id )
			return;

		_renaming.Value = Guid.Empty;
		if ( typed.Trim() is { Length: > 0 } trimmed && trimmed != title )
			save( trimmed );
	}

	/// <summary>Marks the row under the pointer, rebuilding only when it changes.</summary>
	private void UpdateHovered( Guid id )
	{
		if ( _hovered.Value != id )
			_hovered.Value = id;
	}
}

/// <summary>Supplies the sidebar content and selected row that its moving selection rectangle follows.</summary>
/// <param name="Surface">The scrolling list that holds the selected row.</param>
/// <param name="Target">The selected row, or null when none is visible.</param>
/// <param name="Duration">The movement duration in milliseconds.</param>
internal sealed record SidebarSelectionInput( ElementHandle Surface, ElementHandle? Target, int Duration );

/// <summary>Describes the selected row in sidebar-content coordinates.</summary>
/// <param name="Left">The selected row's left edge.</param>
/// <param name="Top">The selected row's top edge.</param>
/// <param name="Width">The selected row's width.</param>
/// <param name="Height">The selected row's height.</param>
internal readonly record struct SidebarSelectionBounds( double Left, double Top, double Width, double Height );

/// <summary>Draws one rectangle behind the selected sidebar row and moves it when that row changes.</summary>
internal sealed class SidebarSelection : GuardedCell<SidebarSelectionInput>
{
	/// <summary>Stores the selected row's latest bounds and rebuilds when placement changes them.</summary>
	private readonly State<SidebarSelectionBounds?> _bounds = new( null );

	/// <summary>Stores the followed sidebar content while mounted.</summary>
	private ElementHandle? _surface;

	/// <summary>Stores the followed selected row while mounted.</summary>
	private ElementHandle? _target;

	/// <summary>Stores the selected row followed last, which stays set while unmounted, so a new row starts a move.</summary>
	private ElementHandle? _followed;

	/// <summary>Tracks whether geometry subscriptions are active.</summary>
	private bool _mounted;

	/// <summary>Stores the height the selection layer scrolls through: the list's viewport height plus its scroll range.</summary>
	private readonly State<double> _extent = new( 0 );

	/// <summary>Targets the selection layer, which follows the list's scroll offset.</summary>
	private readonly ElementHandle _layer = new();

	/// <summary>Stores when the selection last changed rows, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
	/// <remarks>The rectangle animates only during that move, so it stays fixed to its row while the list scrolls.</remarks>
	private long _movedAt = long.MinValue / 2;

	/// <summary>Follows a replacement selected row.</summary>
	protected override void InputChanged( SidebarSelectionInput input )
	{
		if ( _mounted )
			Follow( input );
	}

	/// <summary>Starts following the sidebar and selected row once their handles can mount.</summary>
	protected override void Mounted()
	{
		_mounted = true;
		_layer.MetricsChanged += UpdateMetrics;
		Follow( Input );
	}

	/// <summary>Stops following sidebar geometry.</summary>
	protected override void Unmounting()
	{
		_mounted = false;
		_layer.MetricsChanged -= UpdateMetrics;
		Unfollow();
	}

	/// <summary>Builds the selected row's rectangle, or an empty keyed declaration until its geometry exists, in a layer under the list. The layer is a second
	/// scroll viewport that jumps to the list's offset before each frame paints, so the rectangle never trails its row.</summary>
	protected override Blob BuildView( SidebarSelectionInput input )
	{
		var highlight = _bounds.Value is { } bounds
			? new Container
			{
				Key = "selection",
				Position = PositionType.Absolute,
				Left = bounds.Left,
				Top = bounds.Top,
				Width = bounds.Width,
				Height = bounds.Height,
				BorderRadius = Theme.Current.Radius,
				BackgroundColor = Theme.Current.Hover,
				HitTestSelf = false,
				LayoutTransition = input.Duration > 0 && Environment.TickCount64 - _movedAt < input.Duration ? new( input.Duration, Easing.EaseOut ) : null,
			}
			: new Container { Key = "empty-selection", Position = PositionType.Absolute, Width = 0, Height = 0, HitTestSelf = false };

		return new Container
		{
			Key = "selection-layer",
			Handle = _layer,
			Position = PositionType.Absolute,
			Left = 0,
			Top = 0,
			Right = 0,
			Bottom = 0,
			OverflowY = Overflow.Scroll,
			ScrollbarVisibilityY = ScrollbarVisibility.Hidden,
			HitTestSelf = false,
			Children = [new Container { Height = _extent.Value, Children = [highlight] }],
		};
	}

	/// <summary>Subscribes to the current content and selected row, then reads their geometry.</summary>
	private void Follow( SidebarSelectionInput input )
	{
		Unfollow();
		if ( _bounds.Value is not null && !ReferenceEquals( input.Target, _followed ) )
			_movedAt = Environment.TickCount64;
		_followed = input.Target;
		_surface = input.Surface;
		_target = input.Target;
		_surface.MetricsChanged += UpdateMetrics;
		if ( _target is not null )
			_target.MetricsChanged += UpdateMetrics;
		UpdateBounds();
	}

	/// <summary>Releases current geometry subscriptions.</summary>
	private void Unfollow()
	{
		if ( _surface is not null )
			_surface.MetricsChanged -= UpdateMetrics;
		if ( _target is not null )
			_target.MetricsChanged -= UpdateMetrics;
		_surface = null;
		_target = null;
	}

	/// <summary>Updates the selection bounds after either followed element moves.</summary>
	private void UpdateMetrics( ElementMetrics _ ) => UpdateBounds();

	/// <summary>Stores the selected row's bounds relative to the sidebar content and moves the selection layer to the list's scroll offset.</summary>
	private void UpdateBounds()
	{
		SidebarSelectionBounds? bounds = null;
		if ( _surface is { IsMounted: true } surface )
		{
			var surfaceBox = surface.BorderBox;
			var scrollY = surface.ScrollOffset.Y;
			var extent = surfaceBox.Height + surface.ScrollRange.Y;
			if ( _extent.Value != extent )
				_extent.Value = extent;
			if ( _layer.ScrollOffset.Y != scrollY )
				_layer.JumpTo( y: scrollY );
			if ( _target is { IsMounted: true } target )
			{
				var targetBox = target.BorderBox;
				bounds = new( targetBox.X - surfaceBox.X, targetBox.Y - surfaceBox.Y + scrollY, targetBox.Width, targetBox.Height );
			}
		}

		if ( _bounds.Value != bounds )
			_bounds.Value = bounds;
	}
}

/// <summary>Describes one row of a sidebar's list, built only while it is in view.</summary>
/// <param name="Key">The stable row key, unique in the list.</param>
/// <param name="Gap">The space above the row in logical pixels.</param>
/// <param name="Build">Builds the row's content.</param>
public sealed record SidebarRow( string Key, double Gap, Func<Blob> Build ) : IMeasuredRow;
