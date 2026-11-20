using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Registers the shell's essential window commands and the Settings panel, whose Modules tab turns modules on and off.</summary>
internal sealed class ShellModule : ILoknModule
{
	/// <summary>Gets the stable shell module identifier.</summary>
	public string Id => "shell";

	/// <summary>Gets the shell module display name.</summary>
	public string Title => "Shell";

	/// <summary>Keeps the shell commands available for the application lifetime.</summary>
	public bool CanUnload => false;


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

	/// <summary>Opens the Settings panel once, or returns to its existing panel.</summary>
	internal static void OpenSettings( LoknApplication app )
	{
		if ( app.Panels.Panels.FirstOrDefault( panel => panel.TypeId == "shell.settings" ) is { } settings )
			app.Panels.Activate( settings.Id );
		else
			app.Panels.Open( "shell.settings" );
	}
}

