using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the fixed sidebar left of the docking area: a module's navigation above the profile switcher, with a draggable edge.</summary>
internal sealed partial class ShellView
{
	/// <summary>Defines the width of the sidebar edge that can be dragged, in logical pixels.</summary>
	private const int SidebarEdgeWidth = 6;

	/// <summary>Defines the width of the window's left edge that brings the hidden sidebar out, in logical pixels.</summary>
	private const int PeekEdgeWidth = 16;

	/// <summary>Defines how long the pointer rests at the edge before the hidden sidebar comes out, in milliseconds, so crossing the edge does nothing.</summary>
	private const double PeekShowMs = 60;

	/// <summary>Defines how long the floating sidebar stays after the pointer leaves it, in milliseconds, so a short slip outside does not close it.</summary>
	private const double PeekHideMs = 350;

	/// <summary>Stores the sidebar width while its edge is dragged, before it is saved, or null when not dragging.</summary>
	private double? _sidebarDrag;

	/// <summary>Reports whether the pointer is over the sidebar edge.</summary>
	private bool _isSidebarEdgeHovered;

	/// <summary>Reports whether the hidden sidebar floats out over the workspace.</summary>
	private bool _isPeeking;

	/// <summary>Holds the timer that brings the hidden sidebar out or sends it back, or null while none waits.</summary>
	private WindowTimer? _peekTimer;

	/// <summary>Stores the sidebar width the last build laid out, zero while hidden.</summary>
	private double _builtSidebarWidth;

	/// <summary>Reports whether this build changes the sidebar width, so panels take their new place at once instead of sliding inside a workspace that moved.</summary>
	private bool _isSidebarResized;

	/// <summary>Reports whether the navigation is the shown sidebar.</summary>
	private bool IsSidebarShown => _app.Settings.Get( ShellSettings.ShowSidebar ) && _app.Settings.Get( ShellSettings.Navigation ) != ShellSettings.TabsNavigation;

	/// <summary>Reports whether the navigation is a sidebar the user hid, which the window's left edge brings out.</summary>
	private bool IsSidebarHidden => !_app.Settings.Get( ShellSettings.ShowSidebar ) && _app.Settings.Get( ShellSettings.Navigation ) != ShellSettings.TabsNavigation;

	/// <summary>Reports whether the navigation is the shown tab strip, which then fills the middle of the titlebar.</summary>
	private bool IsTabsShown => _app.Settings.Get( ShellSettings.Navigation ) == ShellSettings.TabsNavigation;

	/// <summary>Builds the area under the titlebar: the sidebar when shown, then the docking workspace, with the sidebar's edge over the workspace's outer gap;
	/// with the sidebar hidden, the window's left edge that floats it out over the workspace.</summary>
	/// <remarks>The edge floats in the gap instead of taking its own column, so the space beside the sidebar equals the gap between panels.</remarks>
	private Blob BuildBody()
	{
		var sidebarWidth = IsSidebarShown ? _sidebarDrag ?? _app.Settings.Get( ShellSettings.SidebarWidth ) : 0;
		_isSidebarResized = sidebarWidth != _builtSidebarWidth;
		_builtSidebarWidth = sidebarWidth;
		var children = new List<Blob>();
		if ( IsSidebarShown )
			children.Add( BuildSidebar() );

		children.Add( BuildWorkspace() );
		// Added last, so it draws and takes the pointer over the workspace's edge.
		if ( IsSidebarShown )
			children.Add( BuildSidebarEdge() );
		if ( IsSidebarHidden )
		{
			children.Add( BuildPeekEdge() );
			children.Add( Mount<SidebarPeekInput, SidebarPeek>( "sidebar-peek", new( _isPeeking, _app.Settings.Get( ShellSettings.SidebarWidth ), BuildSidebar(),
				() => UpdatePeek( true, 0 ), () => UpdatePeek( false, PeekHideMs ) ) ) );
		}
		else if ( _isPeeking )
			_isPeeking = false;

		return new Row
		{
			Key = "body",
			FlexGrow = 1,
			MinHeight = 0,
			Children = children,
			KeyBindings = _isPeeking ? [new( Key.Escape, KeyModifiers.None, () => UpdatePeek( false, 0 ) )] : null,
		};
	}

	/// <summary>Builds the strip at the window's left edge that brings the hidden sidebar out once the pointer rests on it.</summary>
	private Container BuildPeekEdge() => new()
	{
		Key = "sidebar-peek-edge",
		Position = PositionType.Absolute,
		Left = 0,
		Top = 0,
		Bottom = 0,
		Width = PeekEdgeWidth,
		AccessibilityName = "Show sidebar",
		OnPointerEnter = _ => UpdatePeek( true, PeekShowMs ),
		OnPointerLeave = _ => UpdatePeek( false, PeekHideMs ),
	};

	/// <summary>Brings the hidden sidebar out or sends it back after a delay; a later call replaces a waiting one, so passing back and forth keeps it as it is.</summary>
	/// <param name="isOut">Whether the sidebar should float out.</param>
	/// <param name="delayMs">How long to wait first, in milliseconds; 0 acts now.</param>
	private void UpdatePeek( bool isOut, double delayMs )
	{
		_peekTimer?.Dispose();
		_peekTimer = null;
		if ( delayMs <= 0 )
		{
			SetPeek( isOut );
			return;
		}

		_peekTimer = After( delayMs, () =>
		{
			_peekTimer = null;
			SetPeek( isOut );
		} );
	}

	/// <summary>Floats the hidden sidebar out or back, rebuilding only on change.</summary>
	private void SetPeek( bool isOut )
	{
		if ( _isPeeking == isOut )
			return;

		_isPeeking = isOut;
		Rebuild();
	}

	/// <summary>Builds the sidebar column: the modules' sections stacked in slot order, then the profile switcher pinned to the bottom.</summary>
	private Blob BuildSidebar()
	{
		var children = new List<Blob>();
		foreach ( var item in _app.Slots.Get( ShellSlots.Sidebar ) )
		{
			children.Add( new Container
			{
				Key = $"sidebar-section:{item.Id}",
				// The first section fills the column; later ones take only the height they need.
				FlexGrow = children.Count == 0 ? 1 : 0,
				FlexShrink = 1,
				MinHeight = 0,
				// The sliding content stays inside the sidebar rather than painting over the workspace.
				Overflow = Overflow.Hidden,
				// A section slides only what changes with the profile itself, through ProfileSlide, so shared actions keep still.
				Children = [Mount<ContentSurface>( $"sidebar-surface:{item.Id}", () => new( item ) )],
			} );
		}
		if ( children.Count == 0 )
			children.Add( new Spacer() );

		children.Add( BuildProfileSwitcher() );
		return new Container
		{
			Key = "sidebar",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Sidebar",
			Width = _sidebarDrag ?? _app.Settings.Get( ShellSettings.SidebarWidth ),
			FlexShrink = 0,
			MinHeight = 0,
			Children = children,
		};
	}

	/// <summary>Builds the titlebar's middle: the modules' tab contributions in slot order, then the profile switcher.</summary>
	private Blob BuildTabStrip()
	{
		var children = new List<Blob>();
		foreach ( var item in _app.Slots.Get( ShellSlots.Tabs ) )
		{
			children.Add( new Container
			{
				Key = $"tabs-section:{item.Id}",
				// The first section fills the row; later ones take only the width they need.
				FlexGrow = children.Count == 0 ? 1 : 0,
				FlexShrink = 1,
				MinWidth = 0,
				Overflow = Overflow.Hidden,
				Children = [Mount<ContentSurface>( $"tabs-surface:{item.Id}", () => new( item.Content, $"The {item.ModuleId} tabs" ) )],
			} );
		}
		if ( children.Count == 0 )
			children.Add( new Spacer() );

		children.Add( BuildProfileSwitcher( isInline: true ) );
		return new Row
		{
			Key = "tabs",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Tabs",
			AlignItems = AlignItems.Center,
			FlexGrow = 1,
			MinWidth = 0,
			Children = children,
		};
	}

	/// <summary>Builds the profile switcher, at the sidebar's bottom or inline in the titlebar.</summary>
	private Blob BuildProfileSwitcher( bool isInline = false ) => Mount<ProfileSwitcherInput, ProfileSwitcher>( "profiles", new(
		_app.Settings.ActiveProfile,
		_app.Settings.Get( ShellSettings.ProfileIcon, SettingsScope.Work ),
		_app.Settings.Get( ShellSettings.ProfileIcon, SettingsScope.Personal ),
		profile => _app.Settings.SetActiveProfile( profile ),
		( profile, icon ) => _app.Settings.Set( ShellSettings.ProfileIcon, profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal, icon ),
		isInline ) );

	/// <summary>Builds the sidebar's right edge: dragging it resizes the sidebar live and saves the width on release.</summary>
	private Blob BuildSidebarEdge()
	{
		var isHighlighted = _isSidebarEdgeHovered || _sidebarDrag is not null;
		return new Container
		{
			Key = "sidebar-edge",
			Position = PositionType.Absolute,
			Left = _sidebarDrag ?? _app.Settings.Get( ShellSettings.SidebarWidth ),
			Top = 0,
			Bottom = 0,
			Width = SidebarEdgeWidth,
			Cursor = Cursor.ResizeHorizontal,
			AlignItems = AlignItems.Center,
			AccessibilityRole = AccessibilityRole.Separator,
			AccessibilityName = "Resize sidebar",
			OnPointerEnter = _ => UpdateSidebarEdge( true ),
			OnPointerLeave = _ => UpdateSidebarEdge( false ),
			OnPointerDown = item =>
			{
				if ( item.Button != PointerButton.Primary )
					return;

				_sidebarDrag = _app.Settings.Get( ShellSettings.SidebarWidth );
				item.Capture();
				item.PreventDefault();
				Rebuild();
			},
			OnPointerMove = item =>
			{
				if ( _sidebarDrag is null )
					return;

				// The sidebar starts at the window's left edge, so the pointer's position is the new width.
				_sidebarDrag = Math.Clamp( item.WindowPosition.X, ShellSettings.MinSidebarWidth, ShellSettings.MaxSidebarWidth );
				Rebuild();
			},
			OnPointerUp = item =>
			{
				item.ReleaseCapture();
				UpdateSidebarWidth();
			},
			OnPointerCancel = _ => UpdateSidebarWidth(),
			Children =
			[
				new Container
				{
					FlexGrow = 1,
					Width = DividerLineWidth,
					BorderRadius = DividerLineWidth / 2.0,
					BackgroundColor = Theme.Current.Accent.WithAlpha( isHighlighted ? 1 : 0 ),
					TransitionMs = HighlightFadeMs,
					TransitionProperties = [TransitionProperty.BackgroundColor],
				},
			],
		};
	}

	/// <summary>Marks the sidebar edge as hovered, rebuilding only on change.</summary>
	private void UpdateSidebarEdge( bool isHovered )
	{
		if ( _isSidebarEdgeHovered == isHovered )
			return;

		_isSidebarEdgeHovered = isHovered;
		Rebuild();
	}

	/// <summary>Saves a dragged sidebar width and ends the drag.</summary>
	private void UpdateSidebarWidth()
	{
		if ( _sidebarDrag is not { } width )
			return;

		_sidebarDrag = null;
		_app.Settings.Set( ShellSettings.SidebarWidth, SettingsScope.Global, (int)Math.Round( width ) );
		Rebuild();
	}
}

/// <summary>Builds module content outside a panel, such as slot contributions and notification views, containing its build failures like a panel's.</summary>
/// <param name="content">The content.</param>
/// <param name="subject">Names the content in a failure, such as "The git status item".</param>
internal sealed class ContentSurface( PanelContent content, string subject ) : Cell
{
	/// <summary>Creates the surface of a slot contribution.</summary>
	internal ContentSurface( SlotItem item ) : this( item.Content, $"{item.ModuleId} in {item.Slot}" )
	{
	}

	/// <summary>Builds the content, or a contained failure in its place.</summary>
	public override Blob Build()
	{
		try
		{
			return content.Build() ?? throw new InvalidOperationException( "The content returned nothing." );
		}
		catch ( Exception error )
		{
			return new Container
			{
				Padding = Theme.Current.Spacing,
				Gap = Theme.Current.Spacing,
				Children =
				[
					new Text { Content = $"{subject} could not render", Color = Theme.Current.Accent },
					new Text { Content = error.Message, Color = Theme.Current.Muted },
				],
			};
		}
	}
}
