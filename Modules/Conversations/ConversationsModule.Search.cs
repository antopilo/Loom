using Goo;
using Lokn.Core;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Lists the active profile's chats and projects in the Search palette, opening them the way the sidebar does.</summary>
public sealed partial class ConversationsModule
{
	/// <summary>Defines the place of Chats among the Search palette's types: after Projects.</summary>
	private const int ChatsSearchOrder = 30;

	/// <summary>Defines the Segoe Fluent Message glyph of a task with a chat.</summary>
	private const string ChatGlyph = "\uE8BD";

	/// <summary>Adds the Chats and Projects search providers; the Projects module lists projects itself only while this module is not loaded.</summary>
	private void RegisterSearch( ModuleBuilder builder ) => builder
		.SearchProvider( "chats", "Chats", ChatsSearchOrder, context => ListChats( context.App ) )
		.SearchProvider( "projects", "Projects", ProjectsModule.ProjectsSearchOrder, context => ListProjects( context.App ) );

	/// <summary>Lists the active profile's tasks, with and without a chat, pinned first and then most recently active, each with its project's name.</summary>
	private IEnumerable<SearchItem> ListChats( LoknApplication app )
	{
		var profile = app.Settings.ActiveProfile;
		var projects = store.ProjectStore.Projects( profile ).ToDictionary( project => project.Id, project => project.Name );
		return store.Conversations( profile )
			.Concat( projects.Keys.SelectMany( id => store.Conversations( profile, id ) ) )
			.OrderByDescending( conversation => conversation.IsPinned )
			.ThenByDescending( conversation => conversation.UpdatedAt )
			.Select( conversation => new SearchItem( conversation.Id.ToString(), conversation.Title, () => SelectConversation( app, conversation.Id ) )
			{
				Detail = conversation.ProjectId is { } id ? projects.GetValueOrDefault( id ) : null,
				Glyph = conversation.HasChat ? ChatGlyph : SidebarRows.TaskGlyph,
				LastUsed = conversation.UpdatedAt,
				Actions =
				[
					new MenuAction( conversation.IsPinned ? "Unpin" : "Pin", () => store.SetPinned( conversation.Id, !conversation.IsPinned ) ) { AccessKey = Key.P },
					.. conversation.HasChat
						? Array.Empty<MenuAction>()
						: [new MenuAction( "Start a chat here", () => StartChat( app, conversation.Id ) ) { AccessKey = Key.C }],
					new MenuAction( "Delete task", () => DeleteConversation( app, conversation.Id ) ) { AccessKey = Key.D, IsDestructive = true },
				],
			} );
	}

	/// <summary>Lists the active profile's projects as Home does; running one opens its session, at first with its latest chat.</summary>
	private IEnumerable<SearchItem> ListProjects( LoknApplication app ) => ComputeEntries( app )
		.Where( entry => entry.Project is not null )
		.Select( entry => ProjectsModule.BuildProjectItem( entry.Project!, () => OpenProject( app, entry.Project! ), entry.LastUsed,
		[
			new MenuAction( "New chat", () => NewConversation( app, entry.Project!.Id ) ) { AccessKey = Key.N },
			new MenuAction( "New task", () => NewTask( app, entry.Project!.Id ) ) { AccessKey = Key.T },
		] ) );
}
