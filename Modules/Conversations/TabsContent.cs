using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds the tab strip used instead of the sidebar: the chats the user actually uses, grouped by project, then a + that starts a chat outside projects.</summary>
/// <remarks>
/// Each project's group shows its icon and name, which open the project, its chats picked by <see cref="RecentTabs.ComputeShown"/>
/// with their working or waiting marks, and a + that starts a chat in it; the chats outside projects form the Chats group. A chat's
/// last use is its last activity or the last time its workspace opened, whichever is later. Every chat stays in the Search palette.
/// </remarks>
internal sealed class TabsContent( PanelContext context, ConversationsModule module ) : PanelContent
{
	/// <summary>Defines project icons' width and height in logical pixels.</summary>
	private const int ProjectIconSize = 14;

	/// <summary>Picks, orders and builds the chat groups.</summary>
	private readonly RecentTabs _tabs = new( context );

	/// <summary>Builds the groups and the trailing +, scrolling sideways when they do not fit.</summary>
	public override Blob Build()
	{
		var app = context.App;
		var profile = app.Settings.ActiveProfile;
		var waiting = module.ComputeWaiting( app );
		var selected = module.GetSelected( app );
		var opened = (ConversationsModule.FindWorkspaces( app )?.Sessions ?? []).ToDictionary( session => session.Key, session => session.LastOpened, StringComparer.Ordinal );
		var projects = module.Store.ProjectStore.Projects( profile ).ToDictionary( project => ProjectsModule.SessionKey( project.Id ), StringComparer.Ordinal );

		RecentTabGroup BuildGroup( string key, Guid? projectId ) =>
			new( key, [.. module.Store.Conversations( profile, projectId ).Select( conversation => BuildTab( conversation, opened, waiting.Contains( conversation.Id ) ) )] );

		var groups = projects.Select( pair => BuildGroup( pair.Key, pair.Value.Id ) ).Append( BuildGroup( ConversationsModule.ChatsSession, null ) );
		var current = selected is { } id && module.Store.Find( id ) is { } chat
			? chat.ProjectId is { } projectId ? ProjectsModule.SessionKey( projectId ) : ConversationsModule.ChatsSession
			: null;

		var children = new List<Blob>();
		foreach ( var group in _tabs.Arrange( groups, current, selected ) )
		{
			if ( projects.TryGetValue( group.Key, out var project ) )
			{
				var icon = module.Projects.GetIcon( project ) is { } source ? ProjectsModule.BuildIcon( source, project.Name, ProjectIconSize ) : null;
				children.Add( _tabs.BuildGroup( group, project.Name, icon, group.Key == current, selected,
					() => module.OpenProject( app, project ),
					conversation => module.SelectConversation( app, conversation ),
					() => module.NewConversation( app, project.Id ),
					$"New chat in {project.Name}" ) );
			}
			else
			{
				children.Add( _tabs.BuildGroup( group, ConversationsModule.ChatsTitle, null, group.Key == current, selected,
					() => module.OpenChats( app ),
					conversation => module.SelectConversation( app, conversation ),
					() => module.NewConversation( app ),
					"New chat" ) );
			}
		}
		children.Add( _tabs.BuildAdd( "new-chat", "New chat", () => module.NewConversation( app ) ) );
		return RecentTabs.BuildStrip( "Chats", children );
	}

	/// <summary>Builds one chat's tab: its title, last used at its last activity or when its workspace last opened, with its working or waiting mark.</summary>
	private RecentTab BuildTab( ConversationInfo conversation, Dictionary<string, DateTimeOffset> opened, bool isWaiting )
	{
		var isWorking = module.Store.IsWorking( conversation.Id );
		var lastOpened = opened.GetValueOrDefault( ConversationsModule.ConversationSession( conversation.Id ), DateTimeOffset.MinValue );
		return new RecentTab( conversation.Id, conversation.Title, conversation.IsPinned, lastOpened > conversation.UpdatedAt ? lastOpened : conversation.UpdatedAt )
		{
			Marks = isWorking ? [Cell.Mount<Color, HistoryContent.ActivityOrb>( "activity", Theme.Current.Accent )]
				: isWaiting ? [HistoryContent.BuildWaitingDot()]
				: [],
			AccessibilityName = isWorking ? $"{conversation.Title}, working" : isWaiting ? $"{conversation.Title}, waiting for you" : null,
		};
	}
}
