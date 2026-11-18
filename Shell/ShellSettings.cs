using Lokn.Core;

namespace Lokn;

/// <summary>Defines the essential workspace settings contributed by the shell module.</summary>
internal static class ShellSettings
{
	/// <summary>Defines the disabled native window backdrop value.</summary>
	internal const string NoBackdrop = "none";

	/// <summary>Defines the Windows Mica window backdrop value.</summary>
	internal const string MicaBackdrop = "mica";

	/// <summary>Defines the Windows Acrylic window backdrop value.</summary>
	internal const string AcrylicBackdrop = "acrylic";

	/// <summary>Defines the native material visible through the translucent shell canvas.</summary>
	internal static readonly Setting<string> Backdrop = new(
		"shell.backdrop",
		"Window backdrop",
		"Use an opaque window, wallpaper-based Mica, or desktop-blurring Acrylic.",
		defaultValue: MicaBackdrop,
		category: "Appearance",
		isValid: value => value is NoBackdrop or MicaBackdrop or AcrylicBackdrop )
	{
		Choices = [new( NoBackdrop, "Off" ), new( MicaBackdrop, "Mica" ), new( AcrylicBackdrop, "Acrylic" )],
	};

	/// <summary>Defines the opacity of the window canvas, titlebar and sidebar over the native material, in percent.</summary>
	internal static readonly Setting<int> CanvasOpacity = new(
		"shell.canvas-opacity",
		"Window opacity",
		"How much the window background hides the backdrop. Needs Mica or Acrylic.",
		defaultValue: 82,
		category: "Appearance",
		isValid: value => value is >= 0 and <= 100 ) { Unit = "%", Step = 5, Minimum = 0, Maximum = 100 };

	/// <summary>Defines the opacity of panel backgrounds over the native material, in percent.</summary>
	internal static readonly Setting<int> PanelOpacity = new(
		"shell.panel-opacity",
		"Panel opacity",
		"How much panel backgrounds hide the backdrop. Needs Mica or Acrylic.",
		defaultValue: 100,
		category: "Appearance",
		isValid: value => value is >= 0 and <= 100 ) { Unit = "%", Step = 5, Minimum = 0, Maximum = 100 };

	/// <summary>Defines whether the bottom workspace status row is visible.</summary>
	internal static readonly Setting<bool> ShowStatusBar = new(
		"shell.show-status-bar",
		"Show status bar",
		"Panel and module counts at the bottom of the window.",
		defaultValue: true,
		category: "Workspace" );

	/// <summary>Defines panel movement animation duration in milliseconds.</summary>
	internal static readonly Setting<int> MoveDuration = new(
		"shell.move-duration",
		"Panel animation",
		"How long panels take to move. 0 is instant.",
		defaultValue: 140,
		category: "Workspace",
		isValid: value => value is >= 0 and <= 500 ) { Unit = "ms", Step = 20, Minimum = 0, Maximum = 500 };

	/// <summary>Defines the panel corner rounding in logical pixels.</summary>
	internal static readonly Setting<int> PanelRadius = new(
		"shell.panel-radius",
		"Corner rounding",
		"Round the corners of panels. 0 is square.",
		defaultValue: DockGeometry.DefaultRadius,
		category: "Workspace",
		isValid: value => value is >= 0 and <= 24 ) { Unit = "px", Minimum = 0, Maximum = 24 };

	/// <summary>Defines the space around and between panels in logical pixels.</summary>
	internal static readonly Setting<int> PanelGap = new(
		"shell.panel-gap",
		"Panel spacing",
		"Space between panels and around the edge.",
		defaultValue: DockGeometry.DefaultGap,
		category: "Workspace",
		isValid: value => value is >= 0 and <= 32 ) { Unit = "px", Minimum = 0, Maximum = 32 };

	/// <summary>Defines the sidebar navigation layout value.</summary>
	internal const string SidebarNavigation = "sidebar";

	/// <summary>Defines the tab strip navigation layout value.</summary>
	internal const string TabsNavigation = "tabs";

	/// <summary>Defines whether projects, chats and the profile switcher show in a sidebar at the left or in a tab strip at the top.</summary>
	internal static readonly Setting<string> Navigation = new(
		"shell.navigation",
		"Navigation",
		"Show chats and the profile switcher in a sidebar at the left, or as tabs at the top.",
		defaultValue: SidebarNavigation,
		category: "Workspace",
		isValid: value => value is SidebarNavigation or TabsNavigation )
	{
		Choices = [new( SidebarNavigation, "Sidebar" ), new( TabsNavigation, "Tabs" )],
	};

	/// <summary>Defines whether the sidebar is shown; tabs are always shown.</summary>
	internal static readonly Setting<bool> ShowSidebar = new(
		"shell.show-sidebar",
		"Show sidebar",
		"Projects, chats and the profile switcher. Ctrl+B toggles it. Tabs are always shown.",
		defaultValue: true,
		category: "Workspace" );

	/// <summary>Defines the sidebar width in logical pixels.</summary>
	internal static readonly Setting<int> SidebarWidth = new(
		"shell.sidebar-width",
		"Sidebar width",
		"You can also drag the sidebar edge.",
		defaultValue: 260,
		category: "Workspace",
		isValid: value => value is >= MinSidebarWidth and <= MaxSidebarWidth ) { Unit = "px", Step = 20, Minimum = MinSidebarWidth, Maximum = MaxSidebarWidth };

	/// <summary>Defines the narrowest sidebar in logical pixels.</summary>
	internal const int MinSidebarWidth = 180;

	/// <summary>Defines the widest sidebar in logical pixels.</summary>
	internal const int MaxSidebarWidth = 480;

	/// <summary>Defines the emoji that stands for a profile in the profile switcher; each profile sets its own.</summary>
	internal static readonly Setting<string> ProfileIcon = new(
		"shell.profile-icon",
		"Profile icon",
		"An emoji for this profile. Blank shows its first letter.",
		defaultValue: "",
		category: "Profiles",
		isValid: value => value.Length <= 16 );
}
