using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Shows the saved workspaces as a grid of tiles, each with a miniature of its layout; clicking one switches to it.</summary>
/// <remarks>Right-click a tile to rename or delete it, double-click to rename, and drag tiles to reorder them.</remarks>
internal sealed class WorkspacesContent : PanelContent
{
	/// <summary>Defines the narrowest tile in logical pixels; the grid fits as many columns as this allows.</summary>
	private const int MinimumTileWidth = 84;

	/// <summary>Defines the most columns the grid uses.</summary>
	private const int MaximumColumns = 6;

	/// <summary>Defines a tile's height in logical pixels.</summary>
	private const int TileHeight = 76;

	/// <summary>Defines the gap between tiles in logical pixels.</summary>
	private const int TileGap = 8;

	/// <summary>Defines the gap between panes in a tile's miniature, in logical pixels.</summary>
	private const int PreviewGap = 2;

	/// <summary>Defines the tile name size in logical pixels.</summary>
	private const int NameSize = 12;

	/// <summary>Defines the name line's height in logical pixels, shared by the name and its editor so renaming does not resize the tile.</summary>
	private const int NameHeight = NameSize + 8;

	/// <summary>Stores the panel's dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the module that owns the workspaces.</summary>
	private readonly WorkspacesModule _module;

	/// <summary>Targets the grid to measure how many columns fit.</summary>
	private readonly ElementHandle _grid = new();

	/// <summary>Targets each tile so its menu opens at the pointer on it.</summary>
	private readonly Dictionary<Guid, ElementHandle> _tiles = [];

	/// <summary>Stores how many tiles fit in a row.</summary>
	private readonly State<int> _columns = new( 2 );

	/// <summary>Stores the workspace being renamed, or empty.</summary>
	private readonly State<Guid> _renaming = new( Guid.Empty );

	/// <summary>Stores the name being typed.</summary>
	private readonly State<string> _draft = new( "" );

	/// <summary>Targets the name editor so renaming starts with the whole name selected.</summary>
	private readonly ElementHandle _nameEntry = new();

	/// <summary>Stores the workspace whose menu is open, or empty.</summary>
	private readonly State<Guid> _menu = new( Guid.Empty );

	/// <summary>Stores where on its tile the open menu was asked for, in the tile's coordinates.</summary>
	private ElementRect _menuAt;

	/// <summary>Creates the grid and follows its width.</summary>
	internal WorkspacesContent( PanelContext context, WorkspacesModule module )
	{
		_context = context;
		_module = module;
		_grid.MetricsChanged += metrics =>
		{
			if ( !metrics.IsMounted )
				return;

			var columns = Math.Clamp( (int)((metrics.ContentBox.Width + TileGap) / (MinimumTileWidth + TileGap)), 1, MaximumColumns );
			if ( columns != _columns.Value )
				_context.Post( () => _columns.Value = columns );
		};
	}

	/// <summary>Builds the tiles in rows of equal width, with a tile that saves the current layout as a new workspace.</summary>
	public override Blob Build()
	{
		var store = _module.Store;
		var items = store.Items;
		var current = store.Current;
		var columns = _columns.Value;
		var tiles = items.Select( item => (Key: item.Id.ToString(), Build: (Func<Blob>)(() => BuildTile( item, item.Id == current ))) )
			.Append( (Key: "new", Build: (Func<Blob>)BuildNewTile) ).ToArray();
		var rows = new List<BuiltRow>();
		for ( var start = 0; start < tiles.Length; start += columns )
		{
			var slice = tiles[start..Math.Min( start + columns, tiles.Length )];
			rows.Add( new( slice[0].Key, () =>
			{
				var row = slice.Select( tile => tile.Build() ).ToList();
				// Fillers keep the last row's tiles the same width as the rows above.
				while ( row.Count < columns )
					row.Add( new Container { FlexGrow = 1, FlexBasis = 0, MinWidth = 0 } );
				return new Container { Padding = new Edges( 0, 0, TileGap, 0 ), Children = [new Row { Gap = TileGap, Height = TileHeight, Children = row }] };
			} ) );
		}

		var children = new List<Blob>
		{
			new Virtual
			{
				Key = "workspaces-grid",
				Handle = _grid,
				Source = new MeasuredRowsSource<BuiltRow>( rows, static row => row.Build(), static _ => TileHeight + TileGap, FollowEnd: false ),
				FlexGrow = 1,
				MinWidth = 0,
				MinHeight = 0,
				Padding = new Edges( TileGap, TileGap, 0, TileGap ),
				OverflowX = Overflow.Hidden,
				ScrollbarY = ViewStyle.GetScrollbar(),
				AccessibilityName = "Workspaces",
			},
		};
		if ( _menu.Value is var menu && menu != Guid.Empty && items.FirstOrDefault( item => item.Id == menu ) is { } target )
			children.Add( BuildMenu( target, items.Count > 1 ) );

		return new Container
		{
			Key = "workspaces",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			FontFamily = Theme.Current.FontFamily,
			FontSize = Theme.Current.FontSize,
			Color = Theme.Current.Text,
			Children = children,
		};
	}

	/// <summary>Builds one workspace tile: its layout in miniature over its name, highlighted when current.</summary>
	private Blob BuildTile( SavedWorkspace item, bool isCurrent )
	{
		var theme = Theme.Current;
		var tree = isCurrent ? _context.App.Panels.Root : item.Layout is { } layout ? PanelWorkspace.ReadLayoutTree( layout ) : null;
		var handle = _tiles.TryGetValue( item.Id, out var known ) ? known : _tiles[item.Id] = new ElementHandle();
		var isRenaming = _renaming.Value == item.Id;
		// Clicks, Enter and Space inside the name editor edit the name rather than switching workspace.
		void Open() => _context.Run( () =>
		{
			if ( !isRenaming )
				_module.Switch( _context.App, item.Id );
		} );
		Blob name = isRenaming
			? new TextEntry
			{
				Key = "rename",
				Handle = _nameEntry,
				AutoFocus = true,
				Value = _draft.Value,
				Controlled = true,
				Height = NameHeight,
				Padding = new Edges( 0, theme.Spacing / 2 - theme.BorderWidth ),
				FontSize = NameSize,
				FontWeight = isCurrent ? 600 : 400,
				Color = theme.Text,
				TextAlign = TextAlign.Center,
				BackgroundColor = theme.Panel,
				BorderColor = theme.Accent,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				AccessibilityName = "Workspace name",
				OnChange = value => _context.Run( () => _draft.Value = value ),
				OnSubmit = value => _context.Run( () => UpdateName( item.Id, value ) ),
				OnBlur = _ => _context.Run( () => UpdateName( item.Id, _draft.Value ) ),
				KeyBindings = [new( Key.Escape, KeyModifiers.None, () => _context.Run( () => _renaming.Value = Guid.Empty ) )],
			}
			: new Container
			{
				Height = NameHeight,
				JustifyContent = JustifyContent.Center,
				MinWidth = 0,
				Children =
				[
					new Text
					{
						Content = item.Name,
						FontSize = NameSize,
						FontWeight = isCurrent ? 600 : 400,
						Color = isCurrent ? theme.Text : theme.Muted,
						TextAlign = TextAlign.Center,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
						MinWidth = 0,
					},
				],
			};

		return new Button
		{
			Key = item.Id.ToString(),
			Handle = handle,
			FlexGrow = 1,
			FlexBasis = 0,
			MinWidth = 0,
			Padding = 6,
			Gap = 6,
			FlexDirection = FlexDirection.Column,
			AlignItems = AlignItems.Stretch,
			BorderRadius = theme.Radius + 3,
			BorderWidth = theme.BorderWidth,
			BorderColor = isCurrent ? theme.Accent : Color.Transparent,
			BackgroundColor = isCurrent ? theme.Accent.WithAlpha( 0.12 ) : theme.Raised,
			TransitionMs = 150,
			TransitionProperties = [TransitionProperty.BackgroundColor, TransitionProperty.BorderColor],
			HoverStyle = new Style { BackgroundColor = isCurrent ? theme.Accent.WithAlpha( 0.18 ) : theme.Hover },
			FocusStyle = new Style { BorderColor = theme.Accent },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityRole = AccessibilityRole.ListItem,
			AccessibilityName = isCurrent ? $"{item.Name}, current workspace" : $"Switch to {item.Name}",
			OnClick = Open,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Open ),
				new( Key.Space, KeyModifiers.None, Open ),
				new( Key.F2, KeyModifiers.None, () => _context.Run( () => UpdateRenaming( item ) ) ),
			],
			OnPointerDown = pointer => _context.Run( () =>
			{
				if ( pointer.Button != PointerButton.Secondary )
					return;

				_menuAt = new ElementRect { X = pointer.Position.X, Y = pointer.Position.Y };
				_menu.Value = item.Id;
			} ),
			OnPointerUp = pointer => _context.Run( () =>
			{
				if ( pointer.Button == PointerButton.Primary && pointer.ClickCount == 2 )
					UpdateRenaming( item );
			} ),
			DragSource = new DragSource( _ => new DragData( new WorkspaceDrag( item.Id ), DragEffect.Move ), _ => { } ),
			DropTarget = new DropTarget( QueryDrop, drop => _context.Run( () => UpdateDrop( drop, item.Id ) ) ),
			Children =
			[
				new Container
				{
					Key = "preview",
					FlexGrow = 1,
					MinHeight = 0,
					Children = [BuildPreview( tree, isCurrent )],
				},
				name,
			],
		};
	}

	/// <summary>Builds the tile that saves the current layout as a new workspace and starts naming it.</summary>
	private Blob BuildNewTile()
	{
		var theme = Theme.Current;
		void Create() => _context.Run( () =>
		{
			var id = _module.Create( _context.App );
			if ( _module.Store.Items.FirstOrDefault( item => item.Id == id ) is { } created )
				UpdateRenaming( created );
		} );

		return new Button
		{
			Key = "new",
			FlexGrow = 1,
			FlexBasis = 0,
			MinWidth = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius + 3,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BorderColor = theme.Accent },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = "Save layout as new workspace",
			OnClick = Create,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Create ),
				new( Key.Space, KeyModifiers.None, Create ),
			],
			Children = [new Text { Content = "+", FontSize = 20, Color = theme.Muted }],
		};
	}

	/// <summary>Draws a layout tree as nested blocks in proportion to its splits; panes with a panel are filled.</summary>
	private static Blob BuildPreview( DockNode? node, bool isCurrent )
	{
		var theme = Theme.Current;
		if ( node is DockSplit split )
		{
			var first = BuildPreview( split.First, isCurrent );
			var second = BuildPreview( split.Second, isCurrent );
			first.FlexGrow = split.Ratio;
			second.FlexGrow = 1 - split.Ratio;
			return new Container
			{
				FlexDirection = split.Axis == SplitAxis.Horizontal ? FlexDirection.Row : FlexDirection.Column,
				Gap = PreviewGap,
				FlexGrow = 1,
				FlexBasis = 0,
				MinWidth = 0,
				MinHeight = 0,
				Children = [first, second],
			};
		}

		var isFilled = node is DockPane { PanelId: not null };
		var tint = isCurrent ? theme.Accent : theme.Muted;
		return new Container
		{
			FlexGrow = 1,
			FlexBasis = 0,
			MinWidth = 0,
			MinHeight = 0,
			BorderRadius = 2,
			BackgroundColor = tint.WithAlpha( isFilled ? 0.35 : 0.1 ),
		};
	}

	/// <summary>Builds a tile's menu at the pointer: rename, and delete unless it is the last workspace.</summary>
	private Blob BuildMenu( SavedWorkspace item, bool canDelete ) => Cell.Mount<ContextMenuInput, ContextMenu>( "workspace-menu", new(
		_tiles[item.Id],
		$"{item.Name} options",
		[
			new MenuAction( "Rename", () => _context.Run( () => UpdateRenaming( item ) ) ) { AccessKey = Key.R },
			new MenuSeparator(),
			new MenuAction( "Delete", () => _context.Run( () => _module.Store.Delete( _context.App.Panels, item.Id ) ) )
			{
				AccessKey = Key.D,
				IsDestructive = true,
				IsEnabled = canDelete,
			},
		],
		() => _context.Run( () => _menu.Value = Guid.Empty ) )
	{
		AnchorRect = _menuAt,
		Width = 160,
	} );

	/// <summary>Starts renaming a workspace with its current name selected for typing over.</summary>
	private void UpdateRenaming( SavedWorkspace item )
	{
		if ( _renaming.Value == item.Id )
			return;

		_draft.Value = item.Name;
		_renaming.Value = item.Id;
		_nameEntry.SelectText( 0, item.Name.Length );
	}

	/// <summary>Finishes renaming; a blank name keeps the old one.</summary>
	private void UpdateName( Guid id, string name )
	{
		if ( _renaming.Value != id )
			return;

		_renaming.Value = Guid.Empty;
		_module.Store.Rename( id, name );
	}

	/// <summary>Accepts only workspace tiles dragged within this grid.</summary>
	private static DragEffect QueryDrop( DragEvent drop ) => drop.Data.Value is WorkspaceDrag ? DragEffect.Move : DragEffect.None;

	/// <summary>Moves a dropped tile to the place of the tile it was dropped on.</summary>
	private void UpdateDrop( DragEvent drop, Guid target )
	{
		if ( drop.Kind != DragEventKind.Drop || drop.Data.Value is not WorkspaceDrag dragged || dragged.Id == target )
			return;

		var index = _module.Store.Items.ToList().FindIndex( item => item.Id == target );
		_module.Store.Reorder( dragged.Id, index );
	}

	/// <summary>Identifies a workspace tile being dragged.</summary>
	private sealed record WorkspaceDrag( Guid Id );
}
