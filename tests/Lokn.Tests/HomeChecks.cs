using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks Home as the empty conversation panel: reuse when a chat opens, project sessions with their layouts, folders as projects, and the logo's mesh and gradient.</summary>
internal static class HomeChecks
{
	/// <summary>Runs the Home checks headlessly, with a fixture chat instead of Omp.</summary>
	internal static async Task RunAsync()
	{
		var root = Path.Combine( Path.GetTempPath(), "lokn-home-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( Path.Combine( root, "app" ) );
		var app = new LoknApplication();
		var profile = app.Settings.ActiveProfile;
		var store = new ConversationStore( new ProjectStore() );
		var project = store.ProjectStore.Create( profile, "App", Path.Combine( root, "app" ) );
		app.Load( new WorkspacesModule( Path.Combine( root, "workspaces.json" ) ) );
		app.Load( new ProjectsModule( store.ProjectStore ) );
		var conversations = new ConversationsModule( store, ( context, history, id ) => new ChatFixture( id, history.Get( id ).Profile ) )
		{
			Dependencies = ["workspaces", "projects"],
		};
		CoreChecks.Check( app.Load( conversations ), "Conversations loads after Workspaces and Projects, with Home inside it." );
		app.Load( new FixtureModule( "scratch", builder => builder.Panel( "panel", "Scratch", _ => new FixtureContent() ) ) );
		var scratch = app.Panels.Open( "scratch.panel" );

		CoreChecks.Check( await app.Commands.ExecuteAsync( "conversations.home" ) && IsHomeAlone( app ) && app.Panels.Panels.Any( panel => panel.Id == scratch ),
			"Home is one empty conversation panel alone, keeping what was open before hidden." );
		var home = app.Panels.ActivePanelId!.Value;

		var earlier = store.CreateConversation( profile, title: "Earlier" );
		CoreChecks.Check( conversations.OpenConversation( app, earlier.Id ) == home && app.Panels.Panes.Count == 1 && Shown( app, home )?.ConversationId == earlier.Id,
			"Opening a conversation from Home shows it in the same panel instead of opening another." );
		conversations.OpenHome( app );
		CoreChecks.Check( IsHomeAlone( app ) && app.Panels.ActivePanelId == home && CountPanels( app ) == 1, "Going home again empties that idle panel rather than opening another." );

		conversations.StartConversation( app, "Explain this", panelId: home );
		var started = Shown( app, home )?.ConversationId;
		CoreChecks.Check( started is { } id && store.Get( id ).Draft == "Explain this" && store.Get( id ).ProjectId is null && CountPanels( app ) == 1,
			"Home's prompt starts its chat in the Home panel itself." );
		conversations.DeleteConversation( app, started!.Value );
		CoreChecks.Check( Shown( app, home )?.IsEmpty == true && app.Panels.Panels.Any( panel => panel.Id == home ), "Deleting the shown conversation brings Home back in its panel." );

		conversations.OpenProject( app, project );
		var projectChat = app.Panels.ActivePanelId!.Value;
		CoreChecks.Check( Shown( app, projectChat )?.ConversationId is { } first && store.Get( first ).ProjectId == project.Id && app.Panels.Panes.Count == 1,
			"A project's first opening shows a new chat in the project." );
		conversations.StartConversation( app, "In the project", conversations.ComputeSessionProject( app ), projectChat );
		CoreChecks.Check( store.Conversations( profile, project.Id ).Count == 2, "A chat started in a project's session belongs to the project." );

		conversations.OpenHome( app );
		CoreChecks.Check( IsHomeAlone( app ) && app.Panels.Panels.Any( panel => panel.Id == projectChat ), "Going home keeps the project's chat running, hidden." );
		conversations.OpenProject( app, project );
		CoreChecks.Check( app.Panels.IsShown( projectChat ) && app.Panels.Panes.Count == 1, "Opening the project again restores its layout with the same panel." );

		var entries = conversations.ComputeEntries( app );
		CoreChecks.Check( entries.Count == 2 && entries.Any( entry => entry is { Key: ConversationsModule.ChatsSession, Project: null } ) && entries.Any( entry => entry.Project?.Id == project.Id ),
			"Home's cards are the projects and the chats outside them." );

		conversations.OpenFolder( app, Path.Combine( root, "app" ) + Path.DirectorySeparatorChar );
		CoreChecks.Check( store.ProjectStore.Projects( profile ).Count == 1 && app.Panels.IsShown( projectChat ), "Opening a project's folder opens that project instead of a copy." );
		var tools = Path.Combine( root, "tools" );
		Directory.CreateDirectory( tools );
		conversations.OpenFolder( app, tools );
		CoreChecks.Check( store.ProjectStore.Projects( profile ).Any( item => item.Name == "tools" && item.Directory == tools ), "A new folder becomes a project named after it." );

		Color[] palette = [Color.Parse( "#ff0000" ), Color.Parse( "#00ff00" ), Color.Parse( "#0000ff" ), Color.Parse( "#ffffff" )];
		CoreChecks.Check( HomeLogo.Sample( palette, 0 ) == Color.FromNormalized( 1, 0, 0, 1 ) && HomeLogo.Sample( palette, 0.25 ) == Color.FromNormalized( 0, 1, 0, 1 )
			&& HomeLogo.Sample( palette, 1.25 ) == HomeLogo.Sample( palette, 0.25 ), "The logo gradient reaches each palette color and cycles." );
		double Ring( HomeLogo.LogoPoint point ) => Math.Abs( Math.Sqrt( Math.Pow( point.X / HomeLogo.Scale - 32, 2 ) + Math.Pow( point.Y / HomeLogo.Scale - 32, 2 ) ) - HomeLogo.RingRadius );
		double Weft( HomeLogo.LogoPoint point ) => HomeLogo.MeasureWeft( point.X / HomeLogo.Scale, point.Y / HomeLogo.Scale ).Distance;
		var parts = HomeLogo.Points.GroupBy( point => point.Kind ).ToDictionary( group => group.Key, group => group.ToArray() );
		CoreChecks.Check( parts.Count == 2 && parts[HomeLogo.LogoPart.Weft].All( point => Weft( point ) <= HomeLogo.HalfStroke )
			&& parts[HomeLogo.LogoPart.Ring].All( point => Ring( point ) <= HomeLogo.HalfStroke && Weft( point ) > HomeLogo.GapRadius ),
			"The mark draws its ring and the wave through it, with a gap cut out of the ring around the wave." );
		CoreChecks.Check( parts[HomeLogo.LogoPart.Weft].Min( point => point.Along ) < 0.05 && parts[HomeLogo.LogoPart.Weft].Max( point => point.Along ) > 0.95
			&& Enumerable.Range( 0, 90 ).Select( step => HomeLogo.ComputeCrest( step * 0.1 ) ).Min() < 0 && Enumerable.Range( 0, 90 ).Select( step => HomeLogo.ComputeCrest( step * 0.1 ) ).Max() > 1,
			"The weave's crest travels the whole weft, entering and leaving past its ends." );
		var heights = Enumerable.Range( 0, 400 ).Select( step => HomeLogo.ComputeHeight( step % 37 * HomeLogo.Size / 36, step % 11 * HomeLogo.Size / 10, step * 0.37 ) ).ToArray();
		CoreChecks.Check( heights.All( height => height is >= -1 and <= 1 ) && heights.Min() < -0.5 && heights.Max() > 0.5
			&& HomeLogo.ComputeFill( -1 ) == HomeLogo.Ramp[0] && HomeLogo.ComputeFill( 1 ) == HomeLogo.Ramp[^1],
			"The mesh waves stay within their range and reach troughs and crests, which pick the sparsest and densest characters." );
		var accent = Color.Parse( "#d5a583" );
		var turned = HomeLogo.RotateHue( accent, 360 );
		CoreChecks.Check( Math.Abs( turned.R - accent.R ) + Math.Abs( turned.G - accent.G ) + Math.Abs( turned.B - accent.B ) < 0.01 && HomeLogo.RotateHue( accent, 180 ) != accent,
			"The gradient's hues turn and come back around after a full turn." );
		var now = DateTimeOffset.UtcNow;
		CoreChecks.Check( HomeContent.FormatAge( now.AddMinutes( -5 ), now ) == "5 min ago" && HomeContent.FormatAge( now.AddDays( -1.5 ), now ) == "yesterday", "Card ages read naturally." );
		CheckProjectIcons( root );

		app.Shutdown();
		Directory.Delete( root, recursive: true );
		Console.WriteLine( "PASS Home: the empty conversation panel, reused when a chat opens or starts, emptied on return, back after a delete; project sessions; folders as projects; cards and project icons; logo mesh, characters and gradient." );
	}

	/// <summary>Checks conventional icon discovery and its explicit Lokn override.</summary>
	private static void CheckProjectIcons( string root )
	{
		var directory = Path.Combine( root, "icons" );
		Directory.CreateDirectory( directory );
		var logo = Path.Combine( directory, "logo.png" );
		File.WriteAllBytes( logo, [] );
		CoreChecks.Check( ProjectIcons.FindPath( directory ) == logo, "A project picks up a conventional image from its working directory." );

		var overrideDirectory = Path.Combine( directory, ".lokn" );
		Directory.CreateDirectory( overrideDirectory );
		var explicitIcon = Path.Combine( overrideDirectory, "icon.jpg" );
		File.WriteAllBytes( explicitIcon, [] );
		CoreChecks.Check( ProjectIcons.FindPath( directory ) == explicitIcon, "A project's explicit .lokn icon takes priority over conventional images." );

		File.Delete( explicitIcon );
		File.Delete( logo );
		File.WriteAllBytes( Path.Combine( directory, "icon.svg" ), [] );
		CoreChecks.Check( ProjectIcons.FindPath( directory ) is null, "Unsupported project image formats are ignored." );

		var sbox = Path.Combine( root, "sbox" );
		var sboxGame = Path.Combine( root, "sbox-engine" );
		var sboxIcon = Path.Combine( sboxGame, "addons", "tools", "assets", "window", "icon.png" );
		Directory.CreateDirectory( sbox );
		Directory.CreateDirectory( Path.GetDirectoryName( sboxIcon )! );
		File.WriteAllText( Path.Combine( sbox, "example.sbproj" ), "{}" );
		File.WriteAllBytes( Path.Combine( sbox, "logo.png" ), [] );
		File.WriteAllBytes( sboxIcon, [] );
		CoreChecks.Check( ProjectIcons.FindPath( sbox, sboxGame ) == sboxIcon,
			"An s&box project uses the s&box program icon before a conventional project image." );
	}

	/// <summary>Reports whether one empty conversation panel is all that shows.</summary>
	private static bool IsHomeAlone( LoknApplication app ) =>
		app.Panels.Panes is [{ PanelId: { } id }] && Shown( app, id ) is { IsEmpty: true };

	/// <summary>Gets an open conversation panel by identifier, or null.</summary>
	private static ConversationPanel? Shown( LoknApplication app, Guid panelId ) =>
		app.Panels.Sessions.FirstOrDefault( session => session.Info.Id == panelId )?.Content as ConversationPanel;

	/// <summary>Counts open conversation panels, shown or hidden.</summary>
	private static int CountPanels( LoknApplication app ) => app.Panels.Sessions.Count( session => session.Content is ConversationPanel );

	/// <summary>Stands in for a chat, identified by its conversation.</summary>
	private sealed class ChatFixture( Guid conversationId, SettingsProfile profile ) : PanelContent
	{
		/// <summary>Builds the conversation identifier.</summary>
		public override Blob Build() => new Text( conversationId.ToString() );

		/// <summary>Captures the conversation the panel shows, as a chat panel does.</summary>
		public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( new ConversationPanelInput( conversationId, profile ) );
	}
}
