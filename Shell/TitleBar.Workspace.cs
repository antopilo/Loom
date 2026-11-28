using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Supplies workspace actions owned by the shell to the window titlebar.</summary>
/// <param name="ToggleSidebar">Shows or hides the sidebar.</param>
/// <param name="RunCommand">Runs a command by id, such as <c>shell.open.settings</c>, for the Loom menu.</param>
/// <param name="OpenSearch">Opens the Search palette.</param>
/// <param name="IsInSidebar">Whether the bar is the shown sidebar's top row, with the sidebar button at its end and no window controls.</param>
/// <param name="Left">Module contributions shown after the workspace actions.</param>
/// <param name="Right">Module contributions shown before the window controls, such as the Notifications module's bell.</param>
/// <param name="Middle">Content that fills the middle of the titlebar, such as the tabs; null leaves it empty for dragging. With content, the sidebar button is left out: tabs cannot be hidden.</param>
internal sealed record TitleBarInput(
	Action ToggleSidebar,
	Action<string> RunCommand,
	Action OpenSearch,
	bool IsInSidebar,
	IReadOnlyList<SlotItem> Left,
	IReadOnlyList<SlotItem> Right,
	Blob? Middle );

/// <summary>Builds compact workspace icon controls inside the native drag region.</summary>
internal sealed partial class TitleBar
{
	/// <summary>Defines the workspace icon button width in logical pixels.</summary>
	private const int ActionWidth = 32;

	/// <summary>Defines the workspace icon button height in logical pixels.</summary>
	private const int ActionHeight = 28;

	/// <summary>Defines the workspace icon size in logical pixels.</summary>
	private const int ActionFontSize = 14;

	/// <summary>Defines the workspace action gap in logical pixels.</summary>
	private const int ActionGap = 4;

	/// <summary>Defines the workspace action cluster's horizontal inset in logical pixels.</summary>
	private const int ActionInset = 8;

	/// <summary>Defines the size of Loom's mark in the menu button, in logical pixels.</summary>
	private const int LogoSize = 18;

	/// <summary>Defines the native DockLeft icon used to show or hide the sidebar.</summary>
	private const string SidebarGlyph = "\uE90C";

	/// <summary>Defines the label of the Loom menu button, also its tooltip and handle key.</summary>
	private const string LogoLabel = "Loom menu";

	/// <summary>Defines Loom's mark, <c>assets\brand\loom-mark.svg</c>, with its mask drawn as two arcs; <c>{0}</c> is the ink color.</summary>
	private const string LogoMarkup = """
		<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" fill="none" stroke="{0}" stroke-linecap="round" stroke-width="6">
		<path d="M49.58 22.46A20 20 0 0 0 12.59 27.16"/>
		<path d="M14.02 40.77A20 20 0 0 0 51.6 35.99"/>
		<path d="M8 36C16 22 24 46 32 32S48 18 56 28"/>
		</svg>
		""";

	/// <summary>Stores the parsed mark and the ink it was drawn in, parsed again only when the theme's text color changes.</summary>
	private (Color Ink, VectorAsset Mark)? _logo;

	/// <summary>Stores the workspace action whose tooltip is visible.</summary>
	private string? _hoveredAction;

	/// <summary>Reports whether the Loom menu is open.</summary>
	private bool _isMenuOpen;

	/// <summary>Targets each workspace action by label, so its tooltip keeps one anchor across rebuilds.</summary>
	private readonly Dictionary<string, ElementHandle> _actionHandles = [];

	/// <summary>Builds the workspace controls at the start of the titlebar: Loom's mark, which opens the Loom menu, then, across the window with the
	/// sidebar hidden, the sidebar button.</summary>
	private Blob BuildWorkspaceActions( TitleBarInput input )
	{
		List<Blob> actions = [BuildLogo( input )];
		if ( !input.IsInSidebar && input.Middle is null )
			actions.Add( BuildSidebarAction( input ) );
		return new Row
		{
			Padding = new Edges( 0, ActionInset ),
			Gap = ActionGap,
			AlignItems = AlignItems.Center,
			Children = actions,
		};
	}

	/// <summary>Builds the button that shows or hides the sidebar.</summary>
	private Blob BuildSidebarAction( TitleBarInput input ) =>
		BuildWorkspaceAction( "Toggle sidebar", BuildGlyph( SidebarGlyph ), "Sidebar  ·  Ctrl+B", input.ToggleSidebar );

	/// <summary>Builds Loom's mark as a button, and the Loom menu under it while open: Settings, Search, the theme and updates, then Exit.</summary>
	private Blob BuildLogo( TitleBarInput input )
	{
		var ink = Theme.Current.Text;
		if ( _logo is not { } logo || logo.Ink != ink )
		{
			var hex = $"#{(int)Math.Round( ink.R * 255 ):x2}{(int)Math.Round( ink.G * 255 ):x2}{(int)Math.Round( ink.B * 255 ):x2}";
			_logo = logo = (ink, Goo.Svg.Svg.Parse( string.Format( LogoMarkup, hex ) ));
		}
		var button = BuildWorkspaceAction( LogoLabel, new Container { Width = LogoSize, Height = LogoSize, Children = [logo.Mark.Render( ShapeFit.Contain, "logo" )] },
			"Loom", () =>
			{
				_isMenuOpen = !_isMenuOpen;
				Rebuild();
			} );
		if ( !_isMenuOpen )
			return button;

		void Run( Action action )
		{
			_isMenuOpen = false;
			Rebuild();
			InvokeInputCallback( action );
		}
		List<MenuEntry> entries =
		[
			new MenuAction( "Settings", () => Run( () => input.RunCommand( "shell.open.settings" ) ) ) { AccessKey = Key.S },
			new MenuAction( "Search", () => Run( input.OpenSearch ) ) { AccessKey = Key.F },
			new MenuSeparator(),
			new MenuAction( "Edit theme", () => Run( () => input.RunCommand( "theme.edit" ) ) ) { AccessKey = Key.T },
			new MenuAction( "Check for updates", () => Run( () => input.RunCommand( "updates.check" ) ) ) { AccessKey = Key.U },
			new MenuSeparator(),
			new MenuAction( "Exit Loom", () => Run( Host.RequestClose ) ) { AccessKey = Key.X },
		];
		return new Container
		{
			Key = "logo-menu",
			Children =
			[
				button,
				Mount<ContextMenuInput, ContextMenu>( "menu", new( _actionHandles[LogoLabel], "Loom menu", entries, () =>
				{
					_isMenuOpen = false;
					Rebuild();
				} ) ),
			],
		};
	}

	/// <summary>Builds a workspace action's glyph.</summary>
	private static Text BuildGlyph( string glyph ) => new()
	{
		Content = glyph,
		FontFamily = CaptionFont.Family,
		FontSize = ActionFontSize,
	};

	/// <summary>Builds a named, keyboard-accessible icon button with hover help.</summary>
	private Blob BuildWorkspaceAction( string label, Blob icon, string help, Action callback )
	{
		var handle = _actionHandles.TryGetValue( label, out var known ) ? known : _actionHandles[label] = new ElementHandle();
		Action activate = () =>
		{
			_hoveredAction = null;
			Rebuild();
			InvokeInputCallback( callback );
		};
		var children = new List<Blob> { icon };
		if ( _hoveredAction == label )
			children.Add( NativePopup.BuildTooltip( handle, help ) );

		return new Button
		{
			Key = label,
			Handle = handle,
			Width = ActionWidth,
			Height = ActionHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = Theme.Current.Radius,
			Focusable = true,
			AccessibilityName = label,
			Cursor = Cursor.Pointer,
			OnClick = activate,
			OnPointerEnter = _ =>
			{
				_hoveredAction = label;
				Rebuild();
			},
			OnPointerLeave = _ =>
			{
				_hoveredAction = null;
				Rebuild();
			},
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, activate ),
				new( Key.Space, KeyModifiers.None, activate ),
			],
			HoverStyle = new Style
			{
				BackgroundColor = Theme.Current.CaptionHover,
				Color = Theme.Current.Text,
			},
			FocusStyle = new Style
			{
				BackgroundColor = Theme.Current.CaptionHover,
			},
			Children = children,
		};
	}
}
