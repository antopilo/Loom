using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Registers the shell's essential window commands and the Settings panel, whose Modules tab turns modules on and off, and the
/// <c>lokn://shell/settings/&lt;setting id&gt;</c> link that opens Settings at a setting, so modules show their settings without referencing the shell.</summary>
internal sealed class ShellModule : ILoknModule
{
	/// <summary>Gets the stable shell module identifier.</summary>
	public string Id => "shell";

	/// <summary>Gets the shell module display name.</summary>
	public string Title => "Shell";

	/// <summary>Keeps the shell commands available for the application lifetime.</summary>
	public bool CanUnload => false;

	/// <summary>Defines the route of <c>lokn://shell/settings/&lt;setting id&gt;</c>, which opens Settings at that setting.</summary>
	internal const string SettingsRoute = "settings";

	/// <summary>Registers shell commands, panels and settings.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( ShellSettings.Backdrop )
			.Setting( ShellSettings.CanvasOpacity )
			.Setting( ShellSettings.PanelOpacity )
			.Setting( ShellSettings.ShowStatusBar )
			.Setting( ShellSettings.MoveDuration )
			.Setting( ShellSettings.PanelRadius )
			.Setting( ShellSettings.PanelGap )
			.Setting( ShellSettings.Navigation )
			.Setting( ShellSettings.ShowSidebar )
			.Setting( ShellSettings.SidebarWidth )
			.Setting( ShellSettings.ProfileIcon )
			.Setting( DataBackups.Backups )
			.PanelOnly( "settings", "Settings", context => new SettingsContent( context ) )
			.Command( "open.settings", "Open Settings", context => OpenSettings( context.App ) )
			.Route( SettingsRoute, request =>
			{
				if ( request.App.Settings.Definitions.FirstOrDefault( item => item.Id == Uri.UnescapeDataString( request.Path ) ) is { } setting )
					ShowSetting( request.App, setting );
				else
					OpenSettings( request.App );
			} )
			.Command( "split-right", "Shell: split right", context => context.App.Panels.SplitActive( SplitAxis.Horizontal ),
				new( Key.BackSlash, KeyModifiers.Primary ) )
			.Command( "split-down", "Shell: split down", context => context.App.Panels.SplitActive( SplitAxis.Vertical ),
				new( Key.BackSlash, KeyModifiers.PrimaryShift ) )
			.Command( "toggle-sidebar", "Shell: show or hide sidebar", context =>
					context.App.Settings.Set( ShellSettings.ShowSidebar, SettingsScope.Global, !context.App.Settings.Get( ShellSettings.ShowSidebar ) ),
				new( Key.B, KeyModifiers.Primary ),
				context => context.App.Settings.Get( ShellSettings.Navigation ) != ShellSettings.TabsNavigation )
			.Command( "profile-work", "Profile: switch to Work", context => context.App.Settings.SetActiveProfile( SettingsProfile.Work ),
				new( Key.Number1, new KeyModifiers { Alt = true } ),
				context => context.App.Settings.ActiveProfile != SettingsProfile.Work )
			.Command( "profile-personal", "Profile: switch to Personal", context => context.App.Settings.SetActiveProfile( SettingsProfile.Personal ),
				new( Key.Number2, new KeyModifiers { Alt = true } ),
				context => context.App.Settings.ActiveProfile != SettingsProfile.Personal )
			.Command( "close", "Shell: close active panel", context => context.App.Panels.CloseActive(),
				new( Key.W, KeyModifiers.Primary ),
				context => context.App.Panels.ActivePanelId is not null || context.App.Panels.Panes.Count > 1 )
			.AddShellTools()
			.AddShellSearch( this );
	}

	/// <summary>Opens Settings in a popup window, or brings its open window to the front.</summary>
	internal static void OpenSettings( LoknApplication app ) => app.Popups.Open( "shell.settings", options: new() { Width = 1000, Height = 720 } );

	/// <summary>Opens Settings on the tab holding a setting, highlighted and scrolled into view.</summary>
	internal static void ShowSetting( LoknApplication app, SettingDefinition setting )
	{
		OpenSettings( app );
		app.Popups.FindContent<SettingsContent>()?.ShowSetting( setting );
	}
}

