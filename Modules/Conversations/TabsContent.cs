using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds the tab strip used instead of the sidebar: the chats the user actually uses on the current host, grouped by project, then a + that starts a chat outside projects.</summary>
/// <remarks>
/// Each project's group shows its icon and name, which open the project, its chats picked by <see cref="RecentTabs.ComputeShown"/>
/// with their working or waiting marks, and a + that starts a chat in it; the chats outside projects form the Chats group. A chat's
/// last use is its last activity or the last time its workspace opened, whichever is later. Every chat stays in the Search palette.
/// While more than one host exists, a host chip leads the strip with the host switcher's menu, and a paired host's group titles name it.
/// </remarks>
internal sealed class TabsContent( PanelContext context, ConversationsModule module ) : PanelContent
{
	/// <summary>Defines project icons' width and height in logical pixels.</summary>
	private const int ProjectIconSize = 14;

	/// <summary>Picks, orders and builds the chat groups.</summary>
	private readonly RecentTabs _tabs = new( context );

	/// <summary>Builds the host chip and its menu.</summary>
	private HostSwitcher Switcher => field ??= new( context, module );

	/// <summary>Builds the host chip, the groups and the trailing +, scrolling sideways when they do not fit.</summary>
	public override Blob Build()
	{
		var app = context.App;
		var profile = app.Settings.ActiveProfile;
		var host = module.Hosts.Current;
		var waiting = module.ComputeWaiting( app );
		// One host's chats show at a time, so the tabs keep their conversation identifiers.
		var selected = module.GetSelected( app ) is { } chosen && chosen.HostId == host.Id ? chosen.ConversationId : (Guid?)null;
		var opened = (ConversationsModule.FindWorkspaces( app )?.Sessions ?? []).ToDictionary( session => session.Key, session => session.LastOpened, StringComparer.Ordinal );
		var projects = host.Projects.Projects( profile ).ToDictionary( project => ConversationsModule.ProjectSession( host, project.Id ), StringComparer.Ordinal );
		var chats = ConversationsModule.ChatsSession( host );

		RecentTabGroup BuildGroup( string key, Guid? projectId ) =>
			new( key, [.. host.Store.Conversations( profile, projectId ).Select( conversation => BuildTab( host, conversation, opened, waiting.Contains( host.Ref( conversation.Id ) ) ) )] );

		var groups = projects.Select( pair => BuildGroup( pair.Key, pair.Value.Id ) ).Append( BuildGroup( chats, null ) );
		var current = selected is { } id && host.Store.Find( id ) is { } chat
			? chat.ProjectId is { } projectId ? ConversationsModule.ProjectSession( host, projectId ) : chats
			: null;

		var children = new List<Blob>();
		if ( Switcher.BuildChip() is { } chip )
			children.Add( chip );
		foreach ( var group in _tabs.Arrange( groups, current, selected ) )
		{
			if ( projects.TryGetValue( group.Key, out var project ) )
			{
				var icon = module.Projects.BuildProjectIcon( project, host.Id, ProjectIconSize );
				children.Add( _tabs.BuildGroup( group, ConversationsModule.FormatTitle( project.Name, host ), icon, group.Key == current, selected,
					() => module.OpenProject( app, host, project ),
					conversation => module.SelectConversation( app, host.Ref( conversation ) ),
					() => module.NewConversation( app, host, project.Id ),
					$"New chat in {project.Name}" ) );
			}
			else
			{
				children.Add( _tabs.BuildGroup( group, ConversationsModule.FormatTitle( ConversationsModule.ChatsTitle, host ), null, group.Key == current, selected,
					() => module.OpenChats( app, host ),
					conversation => module.SelectConversation( app, host.Ref( conversation ) ),
					() => module.NewConversation( app, host ),
					"New chat" ) );
			}
		}
		children.Add( _tabs.BuildAdd( "new-chat", "New chat", () => module.NewConversation( app, host ) ) );
		return RecentTabs.BuildStrip( "Chats", children );
	}

	/// <summary>Builds one chat's tab: its title, last used at its last activity or when its workspace last opened, with its working or waiting mark.</summary>
	private static RecentTab BuildTab( ChatHost host, ConversationInfo conversation, Dictionary<string, DateTimeOffset> opened, bool isWaiting )
	{
		var isWorking = host.Store.IsWorking( conversation.Id );
		var lastOpened = opened.GetValueOrDefault( ConversationsModule.ConversationSession( host.Ref( conversation.Id ) ), DateTimeOffset.MinValue );
		return new RecentTab( conversation.Id, conversation.Title, conversation.IsPinned, lastOpened > conversation.UpdatedAt ? lastOpened : conversation.UpdatedAt )
		{
			Marks = isWorking ? [Cell.Mount<Color, HistoryContent.ActivityOrb>( "activity", Theme.Current.Accent )]
				: isWaiting ? [HistoryContent.BuildWaitingDot()]
				: [],
			AccessibilityName = isWorking ? $"{conversation.Title}, working" : isWaiting ? $"{conversation.Title}, waiting for you" : null,
		};
	}
}
