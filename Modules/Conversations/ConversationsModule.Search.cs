using Goo;
using Lokn.Core;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Lists the active profile's chats and projects of every host in the Search palette, opening them the way the sidebar does.</summary>
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

	/// <summary>Lists every host's tasks of the active profile, with and without a chat, pinned first and then most recently active, each with its project's
	/// name and, off This PC, its host's.</summary>
	private IEnumerable<SearchItem> ListChats( LoknApplication app )
	{
		if ( _hosts is not { } hosts )
			return [];

		var profile = app.Settings.ActiveProfile;
		return hosts.All
			.SelectMany( host =>
			{
				var projects = host.Projects.Projects( profile ).ToDictionary( project => project.Id, project => project.Name );
				return host.Store.Conversations( profile )
					.Concat( projects.Keys.SelectMany( id => host.Store.Conversations( profile, id ) ) )
					.Select( conversation => (Host: host, Conversation: conversation, Project: conversation.ProjectId is { } id ? projects.GetValueOrDefault( id ) : null) );
			} )
			.OrderByDescending( item => item.Conversation.IsPinned )
			.ThenByDescending( item => item.Conversation.UpdatedAt )
			.Select( item =>
			{
				var (host, conversation, project) = item;
				var chat = host.Ref( conversation.Id );
				return new SearchItem( chat.ToString(), conversation.Title, () => SelectConversation( app, chat ) )
				{
					Detail = host.IsLocal ? project : project is null ? host.Name : FormatTitle( project, host ),
					Glyph = conversation.HasChat ? ChatGlyph : SidebarRows.TaskGlyph,
					LastUsed = conversation.UpdatedAt,
					Actions =
					[
						new MenuAction( conversation.IsPinned ? "Unpin" : "Pin", () => host.Store.SetPinned( conversation.Id, !conversation.IsPinned ) ) { AccessKey = Key.P },
						.. conversation.HasChat
							? Array.Empty<MenuAction>()
							: [new MenuAction( "Start a chat here", () => StartChat( app, chat ) ) { AccessKey = Key.C }],
						new MenuAction( "Delete task", () => DeleteConversation( app, chat ) ) { AccessKey = Key.D, IsDestructive = true },
					],
				};
			} );
	}

	/// <summary>Lists every host's projects of the active profile as Home does, a paired host's with its name after the directory; running one opens its
	/// session, at first with its latest chat.</summary>
	private IEnumerable<SearchItem> ListProjects( LoknApplication app )
	{
		if ( _hosts is not { } hosts )
			return [];

		return hosts.All.SelectMany( host => ComputeEntries( app, host ) )
			.Where( entry => entry.Project is not null )
			.Select( entry =>
			{
				var project = entry.Project!;
				var item = ProjectsModule.BuildProjectItem( project, () => OpenProject( app, entry.Host, project ), entry.LastUsed,
				[
					new MenuAction( "New chat", () => NewConversation( app, entry.Host, project.Id ) ) { AccessKey = Key.N },
					new MenuAction( "New task", () => NewTask( app, entry.Host, project.Id ) ) { AccessKey = Key.T },
				] );
				return entry.Host.IsLocal ? item : item with { Id = entry.Key, Detail = FormatTitle( project.Directory, entry.Host ) };
			} );
	}
}
