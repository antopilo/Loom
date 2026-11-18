using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Registers the shell's types in the Search palette: Panels, every open panel; Actions, every command that can run now; and Settings, every setting.</summary>
internal static class ShellSearch
{
	/// <summary>Defines the Segoe Fluent OpenPane glyph of a panel.</summary>
	private const string PanelGlyph = "\uE8A0";

	/// <summary>Defines the Segoe Fluent LightningBolt glyph of a command.</summary>
	private const string ActionGlyph = "\uE945";

	/// <summary>Defines the Segoe Fluent Settings glyph of a setting.</summary>
	private const string SettingGlyph = "\uE713";

	/// <summary>Adds the shell's search providers to its module.</summary>
	internal static ModuleBuilder AddShellSearch( this ModuleBuilder builder, ShellModule module ) => builder
		.SearchProvider( "panels", "Panels", 40, context => ListPanels( context.App ) )
		.SearchProvider( "actions", "Actions", 50, context => ListActions( context.App ) )
		.SearchProvider( "settings", "Settings", 60, context => ListSettings( context.App, module ) );

	/// <summary>Lists the open panels, the active one first, then those on screen, then the hidden ones other tasks hold.</summary>
	private static IEnumerable<SearchItem> ListPanels( LoknApplication app )
	{
		var types = app.Panels.Types.ToDictionary( type => type.Id, StringComparer.Ordinal );
		var modules = app.Modules.ToDictionary( module => module.Id, module => module.Title, StringComparer.Ordinal );
		return app.Panels.Panels
			.Select( panel => (Panel: panel, IsShown: app.Panels.IsShown( panel.Id )) )
			.OrderByDescending( item => item.Panel.Id == app.Panels.ActivePanelId )
			.ThenByDescending( item => item.IsShown )
			.Select( item =>
			{
				var panel = item.Panel;
				var type = types.GetValueOrDefault( panel.TypeId );
				var owner = type is null ? null : modules.GetValueOrDefault( type.ModuleId );
				return new SearchItem( panel.Id.ToString(), panel.Title, () => ShowPanel( app, panel ) )
				{
					Detail = item.IsShown ? owner : owner is null ? "Hidden" : $"{owner} · hidden",
					Glyph = PanelGlyph,
					Keywords = type is null ? [panel.TypeId] : [panel.TypeId, type.Title],
					Actions = [new MenuAction( "Close panel", () => app.Panels.Close( panel.Id ) ) { AccessKey = Key.C, IsDestructive = true }],
				};
			} );
	}

	/// <summary>Brings a panel forward: one on screen is activated; a hidden one travels to the task that holds it, as opening its notification does.</summary>
	/// <remarks>Modules that know where panels belong, such as Conversations, take the panel through <see cref="NotificationOpener"/>; without one, the panel comes into the current layout.</remarks>
	private static void ShowPanel( LoknApplication app, PanelInfo panel )
	{
		if ( !app.Panels.IsShown( panel.Id ) )
		{
			var request = new LoknNotification( Guid.NewGuid(), DateTimeOffset.UtcNow, panel.Title, "", panel.Id, IsRead: true );
			foreach ( var opener in app.Extensions.Get<NotificationOpener>( NotificationOpener.Point ) )
			{
				if ( opener.Open( app, request ) )
					return;
			}
		}

		app.Panels.Activate( panel.Id );
	}

	/// <summary>Lists the commands that can run now by title, each with its shortcut.</summary>
	private static IEnumerable<SearchItem> ListActions( LoknApplication app ) => app.Commands.Items
		.Where( command => app.Commands.CanExecute( command.Id ) )
		.OrderBy( command => command.Title, StringComparer.CurrentCultureIgnoreCase )
		.Select( command => new SearchItem( command.Id, command.Title, () => _ = app.Commands.ExecuteAsync( command.Id ) )
		{
			Detail = ShellView.ComputeShortcutLabel( command.Shortcut ),
			Glyph = ActionGlyph,
			Keywords = [command.Id],
		} );

	/// <summary>Lists every setting with its category; running one opens Settings at it.</summary>
	private static IEnumerable<SearchItem> ListSettings( LoknApplication app, ShellModule module ) => app.Settings.Definitions
	.Select( setting => new SearchItem( setting.Id, setting.Title, () => ShowSetting( app, setting ) )
		{
			Detail = setting.Category,
			Glyph = SettingGlyph,
			Keywords = [setting.Id, setting.Description],
		} );

	/// <summary>Opens Settings, or returns to it, on the tab that holds a setting, with the setting highlighted and scrolled into view.</summary>
	private static void ShowSetting( LoknApplication app, SettingDefinition setting )
	{
		ShellModule.OpenSettings( app );
		app.Panels.FindContent<SettingsContent>()?.Content.ShowSetting( setting );
	}
}
