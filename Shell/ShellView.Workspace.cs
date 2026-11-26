using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds independent pane tiles, pane grips, docking targets, and captured split resizing.</summary>
internal sealed partial class ShellView
{
	/// <summary>Defines docking edge target size as a fraction of the panel body.</summary>
	private const double DockEdgeFraction = 0.25;

	/// <summary>Defines keyboard divider adjustments as a fraction of the split extent.</summary>
	private const double ResizeStep = 0.025;

	/// <summary>Defines docking preview opacity from zero to one.</summary>
	private const double PreviewOpacity = 0.5;

	/// <summary>Defines the height of a pane's top section that reveals its grip, in logical pixels.</summary>
	private const int GripZoneHeight = 32;

	/// <summary>Defines the diameter of one grip dot in logical pixels.</summary>
	private const int GripDotSize = 3;

	/// <summary>Defines the spacing between grip dots in logical pixels.</summary>
	private const int GripDotGap = 3;

	/// <summary>Defines the number of dots in one grip row.</summary>
	private const int GripDotColumns = 3;

	/// <summary>Defines the number of grip dot rows.</summary>
	private const int GripDotRows = 2;

	/// <summary>Defines the grip and divider highlight fade duration in milliseconds.</summary>
	private const int HighlightFadeMs = 120;

	/// <summary>Defines how long a waiting panel's outline takes to fade in or out, in milliseconds.</summary>
	private const int AttentionFadeMs = 400;

	/// <summary>Defines the visible divider highlight thickness in logical pixels.</summary>
	private const int DividerLineWidth = 2;

	/// <summary>Gets the configured panel corner radius; reading it during a build follows setting changes.</summary>
	private int PaneRadius => _app.Settings.Get( ShellSettings.PanelRadius );

	/// <summary>Gets the configured space around and between panels.</summary>
	private int PaneGap => _app.Settings.Get( ShellSettings.PanelGap );

	/// <summary>Stores the currently negotiated docking preview.</summary>
	private DropPreview? _dropPreview;

	/// <summary>Stores the captured divider resize origin.</summary>
	private ResizeState? _resize;

	/// <summary>Stores the pane whose top section is under the pointer, revealing its grip.</summary>
	private Guid? _gripPaneId;

	/// <summary>Stores the divider under the pointer.</summary>
	private Guid? _hoveredDividerId;

	/// <summary>Stores the divider holding keyboard focus.</summary>
	private Guid? _focusedDividerId;

	/// <summary>Builds stable sibling panel views, their grips, dividers, and the docking preview.</summary>
	private Blob BuildWorkspace()
	{
		var panels = _app.Panels;
		var children = new List<Blob>();
		var slots = new List<DockSlot>();
		// A maximized panel fills the workspace alone; the other panels stay mounted, hidden, so they keep their state.
		var maximized = panels.MaximizedPanelId is { } maximizedId ? panels.Panes.FirstOrDefault( pane => pane.PanelId == maximizedId ) : null;
		var panes = maximized is null ? panels.Panes : new[] { maximized };
		foreach ( var panelId in _panelHandles.Keys.Where( panelId => panels.Sessions.All( session => session.Info.Id != panelId ) ).ToArray() )
			_panelHandles.Remove( panelId );

		var isSplit = maximized is null && panels.Root is DockSplit;
		var hasHeaders = HasPaneHeaders;
		var bodies = new Dictionary<Guid, ElementHandle>();

		foreach ( var pane in panes.Where( pane => pane.PanelId is null ) )
		{
			var handle = bodies[pane.Id] = new ElementHandle();
			children.Add( BuildEmptyPane( pane, handle, isSplit ) );
			slots.Add( new( DockSlotKind.Body, pane.Id ) );
		}

		// Panel views remain siblings when their panes move or swap, preserving nested Cell identity. A panel the
		// current layout hides stays mounted, invisible, so switching workspaces back keeps its state.
		foreach ( var session in panels.Sessions )
		{
			var handle = _panelHandles.GetValueOrDefault( session.Info.Id );
			if ( handle is null )
				_panelHandles.Add( session.Info.Id, handle = new() );
			if ( panes.FirstOrDefault( pane => pane.PanelId == session.Info.Id ) is not { } pane )
			{
				children.Add( BuildHiddenBody( session, handle ) );
				slots.Add( new( DockSlotKind.Hidden, session.Info.Id ) );
				continue;
			}

			bodies[pane.Id] = handle;
			children.Add( BuildPanelBody( session, pane, handle, isSplit ) );
			slots.Add( new( DockSlotKind.Body, pane.Id ) );
		}

		// Grips follow every body so they paint and hit-test above panel content; with headers the header is the grip.
		foreach ( var pane in panes.Where( _ => !hasHeaders ) )
		{
			children.Add( BuildGrip( pane, bodies[pane.Id] ) );
			slots.Add( new( DockSlotKind.Grip, pane.Id ) );
		}

		foreach ( var split in maximized is null ? EnumerateSplits( panels.Root ) : [] )
		{
			children.Add( BuildDivider( split ) );
			slots.Add( new( DockSlotKind.Divider, split.Id ) );
		}

		if ( _dropPreview is { } preview && panes.Any( pane => pane.Id == preview.PaneId ) )
		{
			children.Add( new Container
			{
				Key = "drop-preview",
				BackgroundColor = Theme.Current.Drop.WithAlpha( PreviewOpacity ),
				BorderColor = Theme.Current.Accent,
				BorderWidth = Theme.Current.BorderWidth,
				BorderRadius = PaneRadius,
			} );
			slots.Add( new( DockSlotKind.Preview, preview.PaneId, preview.Placement ) );
		}

		return new Container
		{
			Key = "workspace",
			Handle = _workspaceHandle,
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Overflow = Overflow.Hidden,
			Layout = new DockLayout( maximized ?? panels.Root, slots, PaneGap ),
			Children = children,
		};
	}

	/// <summary>Captures one visible panel without opening a Window stream, preferring a native provider so child surfaces such as WebView2 are included.</summary>
	internal async Task<PanelCaptureImage> CapturePanelAsync( Guid panelId, CancellationToken cancellation )
	{
		if ( !_app.Panels.IsShown( panelId ) )
			throw new InvalidOperationException( "That panel is not visible in the current workspace; focus it before capturing it." );
		if ( !_panelHandles.TryGetValue( panelId, out var handle ) || !handle.IsMounted )
			throw new InvalidOperationException( "That panel is not mounted in the Loom window." );

		var window = _app.Window ?? throw new InvalidOperationException( "Loom has no mounted window to capture." );
		var bounds = handle.BorderBox;
		var size = window.Size;
		if ( bounds.Width < 1 || bounds.Height < 1 || size.Width < 1 || size.Height < 1 )
			throw new InvalidOperationException( "That panel has no visible area to capture." );
		var region = new PanelCaptureRegion( bounds.X / size.Width, bounds.Y / size.Height, bounds.Width / size.Width, bounds.Height / size.Height );
		foreach ( var provider in _app.Extensions.Get<PanelCaptureProvider>( ShellPanelCapture.Point ) )
		{
			if ( await provider.CaptureAsync( window, region, cancellation ) is { } image )
				return image;
		}

		return await CaptureSceneAsync( window, region, cancellation );
	}

	/// <summary>Captures and crops Goo's current scene when no native provider is available.</summary>
	private static async Task<PanelCaptureImage> CaptureSceneAsync( Window window, PanelCaptureRegion region, CancellationToken cancellation )
	{
		var status = window.RequestCapture();
		if ( status != WindowCaptureRequestStatus.Accepted )
			throw new InvalidOperationException( $"Loom could not start a panel capture ({status})." );

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds( 2 );
		WindowCapture? capture;
		while ( (capture = window.PollCapture()) is null )
		{
			if ( DateTime.UtcNow >= deadline )
				throw new InvalidOperationException( "Loom's panel capture did not finish." );
			await Task.Delay( 15, cancellation );
		}

		return await Task.Run( () => EncodeRegion( capture, region ), cancellation );
	}

	/// <summary>Crops a captured scene to one panel and encodes its premultiplied RGBA pixels as PNG.</summary>
	private static PanelCaptureImage EncodeRegion( WindowCapture capture, PanelCaptureRegion region )
	{
		var (x, y, width, height) = region.ToPixels( checked((int)capture.Width), checked((int)capture.Height) );
		var pixels = new byte[checked(width * height * 4)];
		var sourceStride = checked((int)capture.RowBytes);
		var targetStride = checked(width * 4);
		for ( var row = 0; row < height; row++ )
			Buffer.BlockCopy( capture.Pixels, (y + row) * sourceStride + x * 4, pixels, row * targetStride, targetStride );

		using var image = new ImageSource( width, height, pixels );
		return new( ".png", width, height, image.EncodePng() );
	}

	/// <summary>Builds a pane's dotted grip: hidden until its top section is hovered, dragged to dock, right-clicked for pane actions.</summary>
	private Blob BuildGrip( DockPane pane, ElementHandle body )
	{
		var panel = pane.PanelId is { } panelId ? _app.Panels.Panels.FirstOrDefault( item => item.Id == panelId ) : null;
		var isMenuOpen = _menuPaneId == pane.Id;
		var isVisible = isMenuOpen || _gripPaneId == pane.Id;
		var rows = new List<Blob>();
		for ( var row = 0; row < GripDotRows; row++ )
		{
			var dots = new List<Blob>();
			for ( var column = 0; column < GripDotColumns; column++ )
				dots.Add( new Container
				{
					Width = GripDotSize,
					Height = GripDotSize,
					BorderRadius = GripDotSize / 2.0,
					BackgroundColor = Theme.Current.Muted,
				} );

			rows.Add( new Row
			{
				Gap = GripDotGap,
				Children = dots,
			} );
		}

		void Open() => UpdatePaneMenu( pane.Id );

		return new Button
		{
			Key = $"grip:{pane.Id}",
			Handle = isMenuOpen ? _menuAnchor : null,
			Focusable = true,
			AccessibilityName = panel is null ? "Empty pane options" : $"{ComputePanelLabel( panel )} options",
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Gap = GripDotGap,
			BorderRadius = Theme.Current.Radius,
			BackgroundColor = isMenuOpen ? Theme.Current.Hover : Theme.Current.Raised,
			Opacity = isVisible ? 1 : 0,
			TransitionMs = HighlightFadeMs,
			TransitionProperties = [TransitionProperty.Opacity, TransitionProperty.BackgroundColor],
			Cursor = panel is null ? Cursor.Pointer : Cursor.Move,
			HoverStyle = new Style
			{
				Opacity = 1,
				BackgroundColor = Theme.Current.Hover,
			},
			FocusStyle = new Style
			{
				Opacity = 1,
				BackgroundColor = Theme.Current.Hover,
			},
			// A left press starts a drag; the menu opens on a right click, or Enter and Space from the keyboard.
			OnPointerDown = pointer =>
			{
				if ( pointer.Button == PointerButton.Secondary )
					Open();
			},
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Open ),
				new( Key.Space, KeyModifiers.None, Open ),
			],
			DragSource = panel is null ? null : new DragSource(
				_ => new DragData( new PanelDrag( _app.Panels, panel.Id ), DragEffect.Move ),
				_ => UpdateDropPreview( null ) ),
			DropTarget = new DropTarget( QueryPanelDrop, item => UpdateBodyDrop( item, pane.Id, body ) ),
			Children = rows,
		};
	}

	/// <summary>Builds an empty pane that accepts a new panel or an existing panel drop: the same grid of starters as a task's +, then any panel through the palette.</summary>
	private Blob BuildEmptyPane( DockPane pane, ElementHandle handle, bool isSplit )
	{
		var theme = Theme.Current;
		var tiles = new List<Blob>();
		foreach ( var starter in _app.Extensions.Get<TaskStarter>( TaskStarter.Point ).OrderBy( item => item.Order ) )
		{
			// The pane becomes active first, so the starter's panel fills it instead of splitting another.
			tiles.Add( BuildStarterTile( pane.Id, starter.Title, starter.Glyph, () => starter.Open( _app ) ) );
		}
		tiles.Add( BuildStarterTile( pane.Id, "More…", OtherPanelGlyph, () => UpdatePalette( "Open ", ActionsType ) ) );

		var starters = new Container
		{
			Key = "starters",
			FlexGrow = 1,
			MinHeight = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Gap = theme.Spacing * 2,
			Children =
			[
				new Text { Content = "Make room for your next idea", Color = theme.Muted },
				new Row
				{
					AccessibilityRole = AccessibilityRole.Group,
					AccessibilityName = "Open in this pane",
					FlexWrap = FlexWrap.Wrap,
					JustifyContent = JustifyContent.Center,
					MaxWidth = StarterColumns * (StarterSize + theme.Spacing),
					Gap = theme.Spacing,
					Children = tiles,
				},
			],
		};
		return new Container
		{
			Key = $"empty:{pane.Id}",
			Handle = handle,
			Overflow = Overflow.Hidden,
			BackgroundColor = theme.Panel.WithAlpha( PanelAlpha ),
			BorderColor = ComputeOutline( pane, isSplit ),
			BorderWidth = theme.BorderWidth,
			BorderRadius = PaneRadius,
			OnPointerDown = _ => _app.Panels.ActivatePane( pane.Id ),
			OnPointerMove = item => UpdateGripZone( pane.Id, handle, item ),
			OnPointerLeave = _ => ReleaseGripZone( pane.Id ),
			DropTarget = new DropTarget( QueryPanelDrop, item => UpdateBodyDrop( item, pane.Id, handle ) ),
			Children = HasPaneHeaders ? [BuildPaneHeader( pane, null, handle, null, isOverlaid: false ), starters] : [starters],
		};
	}

	/// <summary>Builds one tile of an empty pane's grid: an icon over its title; choosing it makes the pane active, then runs <paramref name="open"/>.</summary>
	private Button BuildStarterTile( Guid paneId, string title, string glyph, Action open )
	{
		var theme = Theme.Current;
		void Choose()
		{
			_app.Panels.ActivatePane( paneId );
			open();
		}

		return new Button
		{
			Key = "starter:" + title,
			Width = StarterSize,
			Height = StarterSize,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Gap = theme.Spacing,
			BorderRadius = theme.Radius + 2,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = $"Open {title} here",
			OnClick = Choose,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Choose ),
				new( Key.Space, KeyModifiers.None, Choose ),
			],
			Children =
			[
				new Text { Content = glyph, FontFamily = TitleBar.CaptionFont.Family, FontSize = StarterGlyphSize, Color = theme.Text },
				new Text { Content = title, Color = theme.Muted, FontSize = theme.FontSize - 1, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, MaxWidth = StarterSize - theme.Spacing },
			],
		};
	}

	/// <summary>Defines an empty pane's tile width and height in logical pixels.</summary>
	private const int StarterSize = 76;

	/// <summary>Defines a tile's glyph size in logical pixels.</summary>
	private const int StarterGlyphSize = 20;

	/// <summary>Defines how many tiles a row of the grid holds.</summary>
	private const int StarterColumns = 4;

	/// <summary>Defines the Segoe Fluent Icons glyph of the tile that opens the palette for any panel.</summary>
	private const string OtherPanelGlyph = "\uE712";

	/// <summary>Builds a visible panel wrapper whose identity survives pane movement and swapping.</summary>
	private Blob BuildPanelBody( PanelSession session, DockPane pane, ElementHandle handle, bool isSplit )
	{
		// A lone panel is the one the user is looking at, so it needs no outline telling it apart.
		var isWaiting = isSplit && _app.Panels.GetAttention( session.Info.Id ) is not null;
		return new Container
		{
			Key = $"panel:{session.Info.Id}",
			Handle = handle,
			MinWidth = 0,
			MinHeight = 0,
			// With a header, the header lies over this room at the top.
			Padding = HasPaneHeaders ? new Edges( PaneHeaderHeight, 0, 0, 0 ) : new Edges( 0, 0, 0, 0 ),
			Overflow = Overflow.Hidden,
			BackgroundColor = Theme.Current.Panel.WithAlpha( PanelAlpha ),
			// A panel waiting for the user keeps a calm, thicker outline until the user activates it.
			BorderColor = isWaiting ? Theme.Current.Attention : ComputeOutline( pane, isSplit ),
			BorderWidth = isWaiting ? Theme.Current.BorderWidth * 2 : Theme.Current.BorderWidth,
			BorderRadius = PaneRadius,
			// The outline eases in when the panel starts waiting and settles quickly once the user activates it.
			TransitionMs = isWaiting ? AttentionFadeMs : HighlightFadeMs,
			TransitionProperties = [TransitionProperty.BorderColor, TransitionProperty.Opacity],
			// A panel a workspace switch brings back fades in while it slides from the full area into its pane.
			Opacity = 1,
			LayoutTransition = _resize is null && !_isSidebarResized ? new( _app.Settings.Get( ShellSettings.MoveDuration ), Easing.EaseOut ) : null,
			OnPointerDown = _ => _app.Panels.Activate( session.Info.Id ),
			OnPointerMove = item => UpdateGripZone( pane.Id, handle, item ),
			OnPointerLeave = _ => ReleaseGripZone( pane.Id ),
			DropTarget = new DropTarget( QueryPanelDrop, item => UpdateBodyDrop( item, pane.Id, handle ) ),
			Children = BuildPanelLayers( session, pane, handle ),
		};
	}

	/// <summary>Builds a shown panel's content, its header when panes have one, and for a panel on a paired host its host chip and, while the host is not
	/// online, the overlay that says so.</summary>
	/// <remarks>The content comes first, as it does in the body of a hidden panel, so that it keeps its place when a layout hides or shows the panel.</remarks>
	private List<Blob> BuildPanelLayers( PanelSession session, DockPane pane, ElementHandle body )
	{
		var host = FindPanelHost( session.Info.Id );
		var hasHeader = HasPaneHeaders;
		// Content under a modal window or under the offline overlay is not seen, so its continuous motion rests.
		var layers = new List<Blob>
		{
			BuildPanelSurface( session, !IsWorkspaceCovered && host is not { IsOnline: false } ),
		};
		if ( hasHeader )
			layers.Add( BuildPaneHeader( pane, session.Info, body, host, isOverlaid: true ) );
		if ( host is null )
			return layers;

		// The content stays mounted under the overlay, so it keeps its state and comes back as it was once the host reconnects.
		if ( !host.IsOnline )
			layers.Add( BuildHostOverlay( session, host, hasHeader ? PaneHeaderHeight : 0 ) );
		// With a header the chip sits in it.
		if ( !hasHeader )
			layers.Add( BuildHostChip( host, isFloating: true ) );
		return layers;
	}

	/// <summary>Builds the small chip that names the paired host a panel works on, with the host's status dot: floating in the pane's top-right corner, or
	/// in the flow of the pane's header.</summary>
	private static Blob BuildHostChip( LoknHost host, bool isFloating )
	{
		var theme = Theme.Current;
		var color = ComputeHostColor( host );
		var isFilled = host.State is LoknHostState.Connected && host.CanControl;
		var chip = new Container
		{
			Key = "host-chip",
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( HostChipPadding, theme.Spacing ),
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			// The chip names the host; clicks reach the panel controls under it.
			HitTestSelf = false,
			AccessibilityName = $"On {host.Name}",
			Children =
			[
				new Container
				{
					Width = HostDotSize,
					Height = HostDotSize,
					BorderRadius = HostDotSize / 2.0,
					BackgroundColor = isFilled ? color : color.WithAlpha( 0 ),
					BorderColor = color,
					BorderWidth = theme.BorderWidth,
				},
				new Text
				{
					Content = host.Name,
					Color = theme.Muted,
					FontSize = ViewStyle.SmallSize,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
					MaxWidth = HostChipMaxWidth,
				},
			],
		};
		if ( isFloating )
		{
			chip.Position = PositionType.Absolute;
			chip.Top = theme.Spacing / 2;
			chip.Right = theme.Spacing;
		}

		return chip;
	}

	/// <summary>Builds the cover over a panel whose paired host is not online: offline with the link's status, or removed with a way to close the panel.</summary>
	/// <param name="session">The panel.</param>
	/// <param name="host">The panel's host.</param>
	/// <param name="top">The room left uncovered at the top, such as for the pane's header, so its buttons still work.</param>
	private Blob BuildHostOverlay( PanelSession session, LoknHost host, double top )
	{
		var theme = Theme.Current;
		var isRemoved = host.State is LoknHostState.Removed;
		var title = isRemoved ? $"{host.Name} was removed" : $"{host.Name} is offline";
		var children = new List<Blob>
		{
			new Text { Content = title, Color = theme.Text },
		};
		if ( host.StatusText.Length > 0 )
			children.Add( new Text { Content = host.StatusText, Color = theme.Muted, FontSize = ViewStyle.SmallSize, TextWrap = TextWrap.Wrap } );
		if ( isRemoved )
		{
			var panelId = session.Info.Id;
			children.Add( theme.BuildButton( "Close panel", () => _app.Panels.Close( panelId ) ) );
		}

		return new Container
		{
			Key = "host-overlay",
			Position = PositionType.Absolute,
			Left = 0,
			Top = top,
			Right = 0,
			Bottom = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Gap = theme.Spacing,
			Padding = theme.Spacing * 3,
			BackgroundColor = theme.Panel.WithAlpha( HostOverlayAlpha ),
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = title,
			Children = children,
		};
	}

	/// <summary>Picks a host's status color: green while usable, the error color after a failed attempt, muted otherwise.</summary>
	private static Color ComputeHostColor( LoknHost host ) => host.State switch
	{
		LoknHostState.Local or LoknHostState.Connected => Theme.Current.Success,
		LoknHostState.Failed => Theme.Current.Error,
		_ => Theme.Current.Muted,
	};

	/// <summary>Gets the paired host a panel works on, or null for This PC and for panels that are not host-bound.</summary>
	private LoknHost? FindPanelHost( Guid panelId ) => _app.Panels.GetHost( panelId ) is { IsLocal: false } host ? host : null;

	/// <summary>Gets the label of a panel in pane menus and accessibility names: its title, followed by its host when that is a paired host.</summary>
	private string ComputePanelLabel( PanelInfo panel ) => FindPanelHost( panel.Id ) is { } host ? $"{panel.Title} · {host.Name}" : panel.Title;

	/// <summary>Defines the diameter of a host chip's status dot in logical pixels.</summary>
	private const int HostDotSize = 7;

	/// <summary>Defines a host chip's vertical padding in logical pixels.</summary>
	private const int HostChipPadding = 2;

	/// <summary>Defines the widest a host chip's name grows before it is cut, in logical pixels.</summary>
	private const int HostChipMaxWidth = 160;

	/// <summary>Defines the opacity of the cover over a panel whose host is not online.</summary>
	private const double HostOverlayAlpha = 0.92;

	/// <summary>Builds a panel the current layout hides: same key, handle and content, so it keeps its state, but not drawn, not hit and not laid
	/// out, since every switch otherwise lays out each hidden panel at full size (docs/performance.md#switching).</summary>
	private static Blob BuildHiddenBody( PanelSession session, ElementHandle handle ) => new Container
	{
		Key = $"panel:{session.Info.Id}",
		Handle = handle,
		MinWidth = 0,
		MinHeight = 0,
		Overflow = Overflow.Hidden,
		Visibility = Visibility.Hidden,
		Display = Display.None,
		Opacity = 0,
		Accessibility = new Accessibility { Hidden = true },
		Children =
		[
			BuildPanelSurface( session, false ),
		],
	};

	/// <summary>Mounts a panel's content under whether it is on screen (<see cref="LiveMotion.IsShown"/>), the same way shown or hidden, so the panel keeps
	/// its state as layouts change and its continuous motion runs only while it is seen.</summary>
	private static Blob BuildPanelSurface( PanelSession session, bool isShown ) =>
		LiveMotion.IsShown.Scope( isShown, () => Mount<PanelSurface>( session.Info.Id.ToString(), () => new( session ) ) );

	/// <summary>Gets whether a modal window, such as Search or a popup panel, covers the workspace.</summary>
	private bool IsWorkspaceCovered => _isPaletteOpen || _app.Popups.Top is not null;

	/// <summary>Outlines the active pane in the accent color once the workspace is split.</summary>
	private Color ComputeOutline( DockPane pane, bool isSplit )
	{
		return isSplit && pane.Id == _app.Panels.ActivePaneId ? Theme.Current.Accent : Theme.Current.Border;
	}

	/// <summary>Reveals a pane's grip while the pointer is inside the pane's top section; with headers there is no grip to reveal.</summary>
	private void UpdateGripZone( Guid paneId, ElementHandle body, PointerEvent item )
	{
		if ( HasPaneHeaders )
			return;

		if ( item.WindowPosition.Y - body.BorderBox.Y < GripZoneHeight )
			UpdateGripPane( paneId );
		else
			ReleaseGripZone( paneId );
	}

	/// <summary>Hides a pane's grip once the pointer leaves its top section.</summary>
	private void ReleaseGripZone( Guid paneId )
	{
		if ( _gripPaneId == paneId )
			UpdateGripPane( null );
	}

	/// <summary>Rebuilds only when the pane showing its grip changes.</summary>
	private void UpdateGripPane( Guid? paneId )
	{
		if ( _gripPaneId == paneId )
			return;

		_gripPaneId = paneId;
		Rebuild();
	}

	/// <summary>Builds a captured pointer and keyboard-accessible split divider with a thin centered highlight.</summary>
	private Blob BuildDivider( DockSplit split )
	{
		var isHorizontal = split.Axis == SplitAxis.Horizontal;
		var isHighlighted = _hoveredDividerId == split.Id || _focusedDividerId == split.Id || _resize?.SplitId == split.Id;
		return new Container
		{
			Key = $"divider:{split.Id}",
			Focusable = true,
			AccessibilityRole = AccessibilityRole.Separator,
			AccessibilityName = isHorizontal ? "Resize columns" : "Resize rows",
			Cursor = isHorizontal ? Cursor.ResizeHorizontal : Cursor.ResizeVertical,
			FlexDirection = isHorizontal ? FlexDirection.Column : FlexDirection.Row,
			AlignItems = AlignItems.Center,
			// The line stops where pane corners start rounding, so it never meets a pane outline.
			Padding = isHorizontal ? new Edges( PaneRadius, 0 ) : new Edges( 0, PaneRadius ),
			OnPointerEnter = _ => UpdateDividerHighlight( ref _hoveredDividerId, split.Id ),
			OnPointerLeave = _ => ReleaseDividerHighlight( ref _hoveredDividerId, split.Id ),
			OnFocus = _ => UpdateDividerHighlight( ref _focusedDividerId, split.Id ),
			OnBlur = _ => ReleaseDividerHighlight( ref _focusedDividerId, split.Id ),
			OnPointerDown = item =>
			{
				if ( item.Button != PointerButton.Primary )
					return;

				var workspace = _workspaceHandle.BorderBox;
				var geometry = DockGeometry.Build( _app.Panels.Root, new( workspace.Width, workspace.Height ), PaneGap );
				var bounds = geometry.Splits[split.Id];
				_resize = new( split.Id, split.Axis, new()
				{
					X = workspace.X + bounds.X,
					Y = workspace.Y + bounds.Y,
					Width = bounds.Width,
					Height = bounds.Height,
				} );
				item.Capture();
				item.PreventDefault();
				Rebuild();
			},
			OnPointerMove = UpdateDivider,
			OnPointerUp = item =>
			{
				_resize = null;
				item.ReleaseCapture();
				Rebuild();
			},
			OnPointerCancel = _ =>
			{
				_resize = null;
				Rebuild();
			},
			KeyBindings =
			[
				new( isHorizontal ? Key.Left : Key.Up, KeyModifiers.None,
					() => _app.Panels.UpdateSplitRatio( split.Id, split.Ratio - ResizeStep ) ),
				new( isHorizontal ? Key.Right : Key.Down, KeyModifiers.None,
					() => _app.Panels.UpdateSplitRatio( split.Id, split.Ratio + ResizeStep ) ),
			],
			Children =
			[
				new Container
				{
					FlexGrow = 1,
					Width = isHorizontal ? DividerLineWidth : Length.Auto,
					Height = isHorizontal ? Length.Auto : DividerLineWidth,
					BorderRadius = DividerLineWidth / 2.0,
					BackgroundColor = Theme.Current.Accent.WithAlpha( isHighlighted ? 1 : 0 ),
					TransitionMs = HighlightFadeMs,
					TransitionProperties = [TransitionProperty.BackgroundColor],
				},
			],
		};
	}

	/// <summary>Marks a divider as hovered or focused, rebuilding only on change.</summary>
	private void UpdateDividerHighlight( ref Guid? field, Guid splitId )
	{
		if ( field == splitId )
			return;

		field = splitId;
		Rebuild();
	}

	/// <summary>Clears a divider's hovered or focused mark, rebuilding only on change.</summary>
	private void ReleaseDividerHighlight( ref Guid? field, Guid splitId )
	{
		if ( field != splitId )
			return;

		field = null;
		Rebuild();
	}

	/// <summary>Updates a captured divider directly without animating the live resize.</summary>
	private void UpdateDivider( PointerEvent item )
	{
		if ( _resize is not { } resize )
			return;

		var isHorizontal = resize.Axis == SplitAxis.Horizontal;
		var size = isHorizontal ? resize.Bounds.Width : resize.Bounds.Height;
		var gap = Math.Min( PaneGap, size );
		var extent = size - gap;
		if ( extent <= 0 )
			return;

		var coordinate = isHorizontal ? item.WindowPosition.X - resize.Bounds.X : item.WindowPosition.Y - resize.Bounds.Y;
		_app.Panels.UpdateSplitRatio( resize.SplitId, (coordinate - gap / 2.0) / extent );
	}

	/// <summary>Accepts only live panel drags from this application workspace.</summary>
	private DragEffect QueryPanelDrop( DragEvent item )
	{
		return item.Data.Value is PanelDrag drag && drag.Workspace == _app.Panels
			&& _app.Panels.Panels.Any( panel => panel.Id == drag.PanelId ) ? DragEffect.Move : DragEffect.None;
	}

	/// <summary>Negotiates an edge split or center swap from the pointer's position over a pane body.</summary>
	private void UpdateBodyDrop( DragEvent item, Guid paneId, ElementHandle body )
	{
		if ( item.Kind == DragEventKind.Leave )
		{
			UpdateDropPreview( null );
			return;
		}

		// Window coordinates let the grip, which sits over the body, negotiate as the body itself.
		var bounds = body.BorderBox;
		var x = bounds.Width > 0 ? (item.WindowPosition.X - bounds.X) / bounds.Width : DefaultCoordinate;
		var y = bounds.Height > 0 ? (item.WindowPosition.Y - bounds.Y) / bounds.Height : DefaultCoordinate;
		var placement = x < DockEdgeFraction ? PanelPlacement.Left
			: x > 1.0 - DockEdgeFraction ? PanelPlacement.Right
			: y < DockEdgeFraction ? PanelPlacement.Top
			: y > 1.0 - DockEdgeFraction ? PanelPlacement.Bottom
			: PanelPlacement.Swap;
		if ( item.Kind == DragEventKind.Drop && item.Data.Value is PanelDrag drag )
		{
			_app.Panels.Move( drag.PanelId, paneId, placement );
			UpdateDropPreview( null );
			return;
		}

		UpdateDropPreview( new( paneId, placement ) );
	}

	/// <summary>Defines the normalized midpoint used for an empty drop rectangle.</summary>
	private const double DefaultCoordinate = 0.5;

	/// <summary>Refreshes docking feedback only when the negotiated destination changes.</summary>
	private void UpdateDropPreview( DropPreview? preview )
	{
		if ( _dropPreview == preview )
			return;

		_dropPreview = preview;
		Rebuild();
	}

	/// <summary>Enumerates every split divider in tree order.</summary>
	private static IEnumerable<DockSplit> EnumerateSplits( DockNode node )
	{
		if ( node is not DockSplit split )
			yield break;

		yield return split;
		foreach ( var child in EnumerateSplits( split.First ).Concat( EnumerateSplits( split.Second ) ) )
			yield return child;
	}

	/// <summary>Identifies the originating workspace and panel for an in-app drag.</summary>
	private sealed record PanelDrag( PanelWorkspace Workspace, Guid PanelId );

	/// <summary>Stores the current docking destination and placement.</summary>
	private readonly record struct DropPreview( Guid PaneId, PanelPlacement Placement );

	/// <summary>Stores one divider's pointer capture geometry in window logical pixels.</summary>
	private readonly record struct ResizeState( Guid SplitId, SplitAxis Axis, ElementRect Bounds );
}
