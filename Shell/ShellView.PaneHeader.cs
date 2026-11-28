using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the header every pane shows: the panel's title, its maximize or restore button and its close button, dragged to dock or swap the pane.</summary>
internal sealed partial class ShellView
{
	/// <summary>Defines a pane header's height in logical pixels.</summary>
	private const int PaneHeaderHeight = 30;

	/// <summary>Defines the side of a header button in logical pixels.</summary>
	private const int HeaderButtonSize = 22;

	/// <summary>Defines the size of a header button's glyph in logical pixels.</summary>
	private const int HeaderGlyphSize = 10;

	/// <summary>Defines the Segoe Fluent Icons glyph of the maximize button, a square.</summary>
	private const string MaximizeGlyph = "\uE922";

	/// <summary>Defines the Segoe Fluent Icons glyph of the restore button, two overlapping squares.</summary>
	private const string RestoreGlyph = "\uE923";

	/// <summary>Defines the Segoe Fluent Icons glyph of the close button, a cross.</summary>
	private const string HeaderCloseGlyph = "\uE8BB";

	/// <summary>Gets whether panes show a header; reading it during a build follows setting changes.</summary>
	private bool HasPaneHeaders => _app.Settings.Get( ShellSettings.PaneHeaders );

	/// <summary>Builds a pane's header: the title, the panel's paired host when it has one, then maximize or restore and close buttons.</summary>
	/// <remarks>
	/// Dragging the header docks or swaps the pane as the grip does, right-clicking it or pressing Enter opens the pane menu, and double-clicking
	/// it maximizes the panel or restores it. A lone pane has nothing to maximize and, empty, nothing to close.
	/// </remarks>
	/// <param name="pane">The pane.</param>
	/// <param name="panel">The panel the pane holds, or null for an empty pane.</param>
	/// <param name="body">The pane's body, which the drag negotiates its docking against.</param>
	/// <param name="host">The paired host the panel works on, or null.</param>
	/// <param name="isOverlaid">Whether the header lies over the top of its pane, as a panel's does, rather than leading its pane's children.</param>
	private Blob BuildPaneHeader( DockPane pane, PanelInfo? panel, ElementHandle body, LoknHost? host, bool isOverlaid )
	{
		var theme = Theme.Current;
		var isMenuOpen = _menuPaneId == pane.Id;
		var label = panel is null ? "Empty pane" : panel.Title;
		var name = panel is null ? label : ComputePanelLabel( panel );
		var isMaximized = panel is not null && _app.Panels.MaximizedPanelId == panel.Id;
		var canMaximize = panel is not null && (isMaximized || _app.Panels.Panes.Count > 1);
		var canClose = panel is not null || _app.Panels.Panes.Count > 1;
		void Open() => UpdatePaneMenu( pane.Id );
		void Maximize() => _app.Panels.ToggleMaximize( panel!.Id );
		void Close()
		{
			if ( panel is not null )
				_app.Panels.Close( panel.Id );
			else
				RunPaneAction( pane, "shell.close", null );
		}

		var children = new List<Blob>
		{
			new Text
			{
				Content = label,
				Color = theme.Text,
				FontSize = ViewStyle.SmallSize,
				FontWeight = PaneTitleWeight,
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( host is not null )
			children.Add( BuildHostChip( host, isFloating: false ) );
		children.Add( new Spacer() );
		// The buttons show while the pointer is on the header, the pane menu is open, or a button has the keyboard.
		var isVisible = isMenuOpen || _headerPaneId == pane.Id;
		void Reveal() => UpdateHeaderPane( pane.Id );
		if ( canMaximize )
			children.Add( BuildHeaderButton( $"maximize:{pane.Id}", isMaximized ? RestoreGlyph : MaximizeGlyph, isMaximized ? $"Restore {name}" : $"Maximize {name}", Maximize, isVisible, Reveal ) );
		if ( canClose )
			children.Add( BuildHeaderButton( $"close:{pane.Id}", HeaderCloseGlyph, $"Close {name}", Close, isVisible, Reveal ) );
		var hasWindowControls = pane.Id == _controlsPaneId;
		if ( hasWindowControls )
			children.Add( WindowControls.Build( Host, PaneHeaderHeight, PaneRadius ) );

		var header = new Button
		{
			Key = $"header:{pane.Id}",
			Handle = isMenuOpen ? _menuAnchor : null,
			Height = PaneHeaderHeight,
			FlexShrink = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			// The window controls reach the pane's right edge.
			Padding = new Edges( 0, hasWindowControls ? 0 : theme.Spacing / 2, 0, theme.Spacing ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Cursor = panel is null ? Cursor.Default : Cursor.Move,
			Focusable = true,
			AccessibilityName = $"{name} header",
			OnPointerMove = _ => Reveal(),
			OnPointerLeave = _ => ReleaseHeaderPane( pane.Id ),
			// A left press starts a drag; the menu opens on a right click, or Enter and Space from the keyboard.
			OnPointerDown = pointer =>
			{
				if ( pointer.Button == PointerButton.Secondary && !pointer.IsFromInteractiveChild )
					Open();
			},
			OnPointerUp = pointer =>
			{
				if ( canMaximize && pointer.Button == PointerButton.Primary && pointer.ClickCount == 2 && !pointer.IsFromInteractiveChild )
					Maximize();
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
			Children = children,
		};
		// A panel's header lies over the top of its body, which leaves room for it, so the panel's content keeps its place among the body's children
		// whether the panel shows or its layout hides it; moving it would remount its text editors.
		if ( isOverlaid )
		{
			header.Position = PositionType.Absolute;
			header.Left = 0;
			header.Top = 0;
			header.Right = 0;
		}

		return header;
	}

	/// <summary>Defines the font weight of a pane header's title.</summary>
	private const double PaneTitleWeight = 600;

	/// <summary>Stores the pane whose header is under the pointer, which shows its buttons.</summary>
	private Guid? _headerPaneId;

	/// <summary>Shows a pane header's buttons, rebuilding only when the header under the pointer changes.</summary>
	private void UpdateHeaderPane( Guid paneId )
	{
		if ( _headerPaneId == paneId )
			return;

		_headerPaneId = paneId;
		Rebuild();
	}

	/// <summary>Hides a pane header's buttons once the pointer leaves it.</summary>
	private void ReleaseHeaderPane( Guid paneId )
	{
		if ( _headerPaneId != paneId )
			return;

		_headerPaneId = null;
		Rebuild();
	}

	/// <summary>Builds one small glyph button of a pane header, drawn only while <paramref name="isVisible"/>, or while it has the keyboard.</summary>
	private static Button BuildHeaderButton( string key, string glyph, string name, Action action, bool isVisible, Action reveal )
	{
		var theme = Theme.Current;
		return new Button
		{
			Key = key,
			Width = HeaderButtonSize,
			Height = HeaderButtonSize,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			Opacity = isVisible ? 1 : 0,
			TransitionMs = HighlightFadeMs,
			TransitionProperties = [TransitionProperty.Opacity, TransitionProperty.BackgroundColor],
			HoverStyle = new Style { Opacity = 1, BackgroundColor = theme.Hover },
			FocusStyle = new Style { Opacity = 1, BackgroundColor = theme.Hover },
			OnPointerEnter = _ => reveal(),
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = name,
			OnClick = action,
			Children = [new Text { Content = glyph, FontFamily = TitleBar.CaptionFont.Family, FontSize = HeaderGlyphSize, Color = theme.Muted }],
		};
	}
}
