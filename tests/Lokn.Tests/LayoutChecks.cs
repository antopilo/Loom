using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Projects;

namespace Lokn.Tests;

/// <summary>Checks workspace restart persistence without rerunning unrelated docking interactions.</summary>
internal static class LayoutChecks
{
	/// <summary>Runs focused layout, panel state, missing-module, and corrupt-file checks.</summary>
	internal static void Run()
	{
		CheckGap();
		var directory = Path.Combine( Path.GetTempPath(), "lokn-layout-" + Guid.NewGuid().ToString( "N" ) );
		var path = Path.Combine( directory, "workspace.json" );
		var app = BuildApp( path );
		var first = app.Panels.Open( "layout.panel", input: "conversation-one" );
		app.Panels.SplitActive( SplitAxis.Vertical );
		var second = app.Panels.Open( "layout.panel", input: "conversation-two" );
		app.Panels.SplitActive( SplitAxis.Horizontal );
		app.Panels.UpdateSplitRatio( app.Panels.Root.Id, 0.37 );
		var tree = app.Panels.Root;
		var active = app.Panels.ActivePaneId;
		app.Shutdown();
		var saved = File.ReadAllText( path );
		app.Shutdown();
		CoreChecks.Check( File.ReadAllText( path ) == saved, "Repeated shutdown never overwrites the saved layout with unloaded panels." );

		var reopened = BuildApp( path );
		reopened.Panels.Open( "layout.panel", input: "default-to-replace" );
		CoreChecks.Check( reopened.RestoreWorkspace(), "An existing layout restores after modules register." );
		CoreChecks.Check( reopened.Panels.Root == tree && reopened.Panels.ActivePaneId == active, "Tree identifiers, orientation, fractions, empty panes and focus survive restart." );
		CoreChecks.Check( reopened.Panels.Panels.Select( panel => panel.Id ).ToHashSet().SetEquals( [first, second] ), "Panel instance identifiers survive restart." );
		CoreChecks.Check( reopened.Panels.Sessions.All( session => session.Content is FixtureContent { Value: "conversation-one" or "conversation-two" } ), "Feature-owned panel state restores before its factory builds content." );
		reopened.Shutdown();

		var withoutModule = new LoknApplication();
		CoreChecks.Check( withoutModule.Panels.RestoreLayout( path ) && withoutModule.Panels.Panels.Count == 0, "Unavailable modules are omitted without failing startup." );
		CoreChecks.Check( withoutModule.Panels.Panes.Count == 1, "Unavailable panels collapse while legitimate empty panes survive." );
		withoutModule.Shutdown();

		var corruptPath = Path.Combine( directory, "corrupt.json" );
		File.WriteAllText( corruptPath, "{broken" );
		var corrupt = BuildApp( corruptPath );
		corrupt.Panels.Open( "layout.panel" );
		var current = corrupt.Panels.Root;
		CoreChecks.Check( !corrupt.RestoreWorkspace() && corrupt.Panels.Root == current, "Malformed layouts preserve the live workspace." );
		corrupt.Shutdown();
		CoreChecks.Check( File.ReadAllText( corruptPath ) == "{broken", "Malformed source layouts remain available for recovery." );

		var invalidPath = Path.Combine( directory, "invalid.json" );
		File.WriteAllText( invalidPath, saved.Replace( "\"Ratio\": 0.37", "\"Ratio\": 4" ) );
		CoreChecks.Check( !new LoknApplication().Panels.RestoreLayout( invalidPath ), "Invalid split fractions are rejected before session creation." );
		var blocked = Path.Combine( directory, "blocked" );
		File.WriteAllText( blocked, "original" );
		CoreChecks.Check( !new LoknApplication().Panels.SaveLayout( Path.Combine( blocked, "workspace.json" ) ) && File.ReadAllText( blocked ) == "original", "Failed writes preserve existing files." );
		CheckConversations( directory );
		CheckFactoryFailure( path );
		CheckFormerPanelTypes( directory );
		Console.WriteLine( "PASS workspace restart topology, real conversation/history integration, panel state, missing modules, factory errors, repeated shutdown, corrupt files, failed writes, "
			+ "and layouts holding the Git and diff panels under their former Conversations identifiers." );
	}

	/// <summary>Checks that the panel gap sets the margins and the space between panes, while dividers stay draggable without a gap.</summary>
	private static void CheckGap()
	{
		var left = new DockPane( Guid.NewGuid(), null );
		var right = new DockPane( Guid.NewGuid(), null );
		var split = new DockSplit( Guid.NewGuid(), SplitAxis.Horizontal, 0.5, left, right );
		var size = new LayoutSize( 1000, 600 );

		var wide = DockGeometry.Build( split, size, 16 );
		CoreChecks.Check( wide.Panes[left.Id] is { X: 16, Y: 16, Height: 568 } && wide.Panes[right.Id].X - (wide.Panes[left.Id].X + wide.Panes[left.Id].Width) == 16, "A 16 pixel gap insets the workspace and separates panes by 16 pixels." );
		CoreChecks.Check( wide.Dividers[split.Id].Width == 16, "A gap wider than the divider minimum is the divider." );

		var none = DockGeometry.Build( split, size, 0 );
		var leftEdge = none.Panes[left.Id].X + none.Panes[left.Id].Width;
		CoreChecks.Check( none.Panes[left.Id] is { X: 0, Y: 0, Height: 600 } && none.Panes[right.Id].X == leftEdge, "Without a gap panes fill the workspace and touch." );
		CoreChecks.Check( none.Dividers[split.Id] is var divider && divider.Width == DockGeometry.DividerHitWidth && divider.X + divider.Width / 2 == leftEdge, "Without a gap the divider keeps its draggable width, centered on the seam." );
	}

	/// <summary>Checks real conversation identity, history, and account pinning across workspace restart.</summary>
	private static void CheckConversations( string directory )
	{
		var workspacePath = Path.Combine( directory, "conversations-workspace.json" );
		var historyPath = Path.Combine( directory, "conversations-history.json" );
		var app = new LoknApplication( workspacePath: workspacePath );
		var projectsPath = Path.Combine( directory, "conversations-projects.json" );
		var store = new ConversationStore( new ProjectStore( projectsPath ), historyPath );
		var module = new ConversationsModule( store, ( context, history, id ) => new ConversationFixture( history.Get( id ) ) );
		CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( module ), "The real conversations module loads for restart checks." );
		app.Settings.SetActiveProfile( SettingsProfile.Work );
		var project = store.ProjectStore.Create( SettingsProfile.Work, "Office", directory );
		var conversationId = module.CreateConversation( app, project.Id );
		store.AppendMessage( conversationId, new( Guid.NewGuid(), "assistant", "Saved reply", DateTimeOffset.UtcNow ) );
		store.SaveBackendSession( conversationId, Path.Combine( directory, "omp-session" ) );
		var panelId = app.Panels.ActivePanelId;
		var tree = app.Panels.Root;
		app.Settings.SetActiveProfile( SettingsProfile.Personal );
		app.Shutdown();

		var reopened = new LoknApplication( workspacePath: workspacePath );
		var reloadedStore = new ConversationStore( new ProjectStore( projectsPath ), historyPath );
		var restoredModule = new ConversationsModule( reloadedStore, ( context, history, id ) => new ConversationFixture( history.Get( id ) ) );
		CoreChecks.Check( ProjectChecks.LoadProjects( reopened, reloadedStore ) && reopened.Load( restoredModule ) && reopened.RestoreWorkspace(), "The real conversation panels restore from independent files." );
		CoreChecks.Check( reopened.Panels.Root == tree && reopened.Panels.ActivePanelId == panelId, "Real conversation panels retain their docking tree and active panel." );
		CoreChecks.Check( reopened.Settings.ActiveProfile == SettingsProfile.Personal && reopened.Panels.Sessions.Any( session => session.Content is ConversationPanel { Content: ConversationFixture fixture } && fixture.Input == new ConversationPanelInput( conversationId, SettingsProfile.Work ) ), "Restoring a Work conversation while Personal is active preserves its original account." );
		CoreChecks.Check( reloadedStore.GetMessages( conversationId ).Single().Text == "Saved reply" && reloadedStore.Get( conversationId ).ProjectId == project.Id && reloadedStore.Get( conversationId ).BackendSessionPath is not null, "Conversation history, project identity, and backend resumption survive the same restart." );
		reopened.Settings.SetActiveProfile( SettingsProfile.Work );
		CoreChecks.Check( restoredModule.OpenConversation( reopened, conversationId ) == panelId && reopened.Panels.Panels.Count == 1, "History reuses a restored conversation instead of creating a duplicate panel." );
		reopened.Shutdown();
	}

	/// <summary>Checks that a failing panel factory leaves restored topology and neighboring panels usable.</summary>
	private static void CheckFactoryFailure( string path )
	{
		var app = new LoknApplication();
		app.Load( new FailingModule() );
		CoreChecks.Check( app.Panels.RestoreLayout( path ), "Panel factory errors remain contained during layout restoration." );
		CoreChecks.Check( app.Panels.Panels.Count == 2 && app.Panels.Sessions.All( session => session.Failure == "Fixture failure" ), "Failed factories keep their addressable panel slots and error surfaces." );
		CoreChecks.Check( app.Panels.Panes.Count == 3 && app.Panels.Root is DockSplit, "Factory failures preserve the validated split tree and empty panes." );
		app.Shutdown();
	}

	/// <summary>Checks that a layout saved while Conversations drew the Git and diff panels restores both as the Git module's Git panel, at startup and as a saved workspace.</summary>
	private static void CheckFormerPanelTypes( string directory )
	{
		var path = Path.Combine( directory, "former-workspace.json" );
		var old = new LoknApplication( workspacePath: path );
		CoreChecks.Check( old.Load( new FormerConversationsModule( directory ) ), "A fixture registers the former conversations.git and conversations.diff panels." );
		var gitId = old.Panels.Open( "conversations.git" );
		old.Panels.SplitActive( SplitAxis.Vertical );
		var diffId = old.Panels.Open( "conversations.diff" );
		old.Shutdown();
		CoreChecks.Check( File.ReadAllText( path ).Contains( "\"conversations.git\"", StringComparison.Ordinal ), "The old layout names the former Git panel type." );

		var reopened = new LoknApplication( workspacePath: path );
		CoreChecks.Check( reopened.Load( new GitModule() ) && reopened.RestoreWorkspace(), "The old layout restores with the Git module loaded." );
		var restored = reopened.Panels.Panels.ToDictionary( panel => panel.Id );
		CoreChecks.Check( restored.Count == 2 && restored[gitId].TypeId == GitModule.PanelType && restored[diffId].TypeId == GitModule.PanelType,
			"The former Conversations Git and diff panels both restore as git.panel with their identifiers." );
		CoreChecks.Check( reopened.Panels.Sessions.Single( session => session.Info.Id == gitId ).Content is GitContent git && git.Shows( directory )
			&& reopened.Panels.Sessions.Single( session => session.Info.Id == diffId ).Content is GitContent diff && diff.Shows( directory ),
			"The restored panels read the folder the former panels saved." );
		var layout = reopened.Panels.CaptureLayout();
		reopened.Shutdown();

		using var saved = JsonDocument.Parse( File.ReadAllText( path ) );
		var workspace = new LoknApplication();
		CoreChecks.Check( workspace.Load( new GitModule() ) && workspace.Panels.ApplyLayout( saved.RootElement ) && workspace.Panels.Panels.Count == 2
			&& workspace.Panels.Panels.All( panel => panel.TypeId == GitModule.PanelType ),
			"A saved workspace holding the former identifiers applies with the Git module's panels." );
		CoreChecks.Check( layout.GetRawText().Contains( "\"git.panel\"", StringComparison.Ordinal ), "Layouts captured afterwards name the new panel type." );
		workspace.Shutdown();
	}

	/// <summary>Registers the Git and diff panels the way Conversations did before the Git module drew them, saving the same state.</summary>
	/// <param name="folder">The folder the panels save.</param>
	private sealed class FormerConversationsModule( string folder ) : ILoknModule
	{
		/// <summary>Gets the former owner's identifier.</summary>
		public string Id => "conversations";

		/// <summary>Gets the fixture module name.</summary>
		public string Title => "Former Conversations fixture";

		/// <summary>Registers both panels with the state their old factories captured.</summary>
		public void Register( ModuleBuilder builder ) => builder
			.Panel( "git", "Git", _ => new StateContent( JsonSerializer.SerializeToElement( new { Directory = folder, ReviewConversationId = (Guid?)null } ) ) )
			.Panel( "diff", "Diff", _ => new StateContent( JsonSerializer.SerializeToElement( new { Directory = folder, Focus = (string?)null } ) ) );
	}

	/// <summary>Captures a fixed state.</summary>
	/// <param name="state">The state.</param>
	private sealed class StateContent( JsonElement state ) : PanelContent
	{
		/// <summary>Builds a minimal declaration.</summary>
		public override Blob Build() => new Text( "former" );

		/// <summary>Captures the fixed state.</summary>
		public override JsonElement? CaptureState() => state;
	}

	/// <summary>Registers the same fixture panel identifier with a deliberately failing factory.</summary>
	private sealed class FailingModule : ILoknModule
	{
		/// <summary>Gets the fixture module identifier.</summary>
		public string Id => "layout";

		/// <summary>Gets the fixture module name.</summary>
		public string Title => "Failing layout fixture";

		/// <summary>Registers a factory error to exercise restore containment.</summary>
		public void Register( ModuleBuilder builder ) => builder.Panel( "panel", "Failure", _ => throw new InvalidOperationException( "Fixture failure" ) );
	}

	/// <summary>Stores the same restorable identity contract as a real chat panel without starting a backend.</summary>
	private sealed class ConversationFixture( ConversationInfo conversation ) : PanelContent
	{
		/// <summary>Gets the conversation and account identity supplied by the real module factory.</summary>
		internal ConversationPanelInput Input => new( conversation.Id, conversation.Profile );

		/// <summary>Builds a minimal declaration without starting a model session.</summary>
		public override Blob Build() => new Text( conversation.Title );

		/// <summary>Captures the conversation identifier and its pinned account profile.</summary>
		public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( Input );
	}

	/// <summary>Creates an isolated application with the fixture panel type registered.</summary>
	private static LoknApplication BuildApp( string path )
	{
		var app = new LoknApplication( workspacePath: path );
		CoreChecks.Check( app.Load( new FixtureModule() ), "The layout fixture loads." );
		return app;
	}

	/// <summary>Registers the fixture's restorable panel type.</summary>
	private sealed class FixtureModule : ILoknModule
	{
		/// <summary>Gets the fixture module identifier.</summary>
		public string Id => "layout";

		/// <summary>Gets the fixture module name.</summary>
		public string Title => "Layout fixture";

		/// <summary>Registers one factory that consumes restored state.</summary>
		public void Register( ModuleBuilder builder ) => builder.Panel( "panel", "Restorable", context => new FixtureContent( context.RestoredState?.GetString() ?? context.Input as string ?? "default" ) );
	}

	/// <summary>Retains a conversation-like identifier across application restart.</summary>
	private sealed class FixtureContent( string value ) : PanelContent
	{
		/// <summary>Gets the feature-owned identifier.</summary>
		internal string Value => value;

		/// <summary>Builds a minimal declaration for the persistence fixture.</summary>
		public override Blob Build() => new Text( value );

		/// <summary>Captures the feature-owned identifier as JSON.</summary>
		public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( value );
	}
}
