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

	/// <summary>Tracks collapsed groups.</summary>
	private readonly State<HashSet<Guid>> _collapsed = new( [] );

	/// <summary>Tracks the row whose context menu is open.</summary>
	private readonly State<Guid> _menu = new( Guid.Empty );

	/// <summary>Stores where on its row a right-click opened the menu, in the row's coordinates, or null when its … button opened it.</summary>
	private ElementRect? _menuAt;

	/// <summary>Targets each row, so a right-click menu opens at the pointer on it.</summary>
	private readonly Dictionary<Guid, ElementHandle> _rows = [];

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

	/// <summary>Builds a group's container: its heading row and rows, set visibly apart from what comes before.</summary>
	public static Container BuildGroup( Guid id, List<Blob> children ) => new()
	{
		Key = id.ToString(),
		Gap = 2,
		// Each group starts a visibly separate block.
		Padding = new Edges( Theme.Current.Spacing * 2, 0, 0, 0 ),
		Children = children,
	};

	/// <summary>Builds a row highlighted edge to edge while selected, under the pointer or showing its menu or + grid, with its actions inside the highlight; right-clicking it opens its menu at the pointer.</summary>
	/// <param name="id">The row.</param>
	/// <param name="isSelected">Whether the row is highlighted as the open item.</param>
	/// <param name="children">The row's content and actions.</param>
	/// <param name="order">The row's place among rows the user reorders by dragging, which then drop on it; null for a row that stays put.</param>
	public Row BuildHoverRow( Guid id, bool isSelected, List<Blob> children, RowOrder? order = null ) => new()
	{
		Handle = _rows.TryGetValue( id, out var known ) ? known : _rows[id] = new ElementHandle(),
		AlignItems = AlignItems.Center,
		BorderRadius = Theme.Current.Radius,
		BackgroundColor = isSelected || _hovered.Value == id || _menu.Value == id || _starters.Value == id ? Theme.Current.Hover : Color.Transparent,
		DropTarget = order is null ? null : BuildRowDrop( id, order ),
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
		Children = order is not null && _dropAt.Value.Row == id ? [.. children, BuildDropLine( _dropAt.Value.IsBelow )] : children,
	};

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
