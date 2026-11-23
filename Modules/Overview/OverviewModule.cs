using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Overview;

/// <summary>Contributes a project's overview: one page with its chats and tasks and their state, its activity, and the designs and reports its agents made.</summary>
/// <remarks>The sidebar opens it with the overview button on a project's heading, through its <c>lokn://overview/project/…</c> link, so Conversations
/// does not reference this module.</remarks>
public sealed class OverviewModule : ILoknModule
{
	/// <summary>Defines the overview panel's type id, which the sidebar opens by name.</summary>
	public const string PanelType = "overview.project";

	/// <summary>Stores the open overview panels, so opening a project's overview again shows the one already on screen.</summary>
	private readonly List<ProjectOverviewContent> _panels = [];

	/// <summary>Gets the module identifier.</summary>
	public string Id => "overview";

	/// <summary>Gets the module title.</summary>
	public string Title => "Project overview";

	/// <summary>Gets the modules whose chats, projects and reports the page lists.</summary>
	public IReadOnlyList<string> Dependencies => ["conversations", "browser"];

	/// <summary>Registers the overview panel, its palette command, and the <c>lokn://overview/project/&lt;project&gt;[@host]</c> link the sidebar opens it with.</summary>
	public void Register( ModuleBuilder builder ) => builder
		.Panel( "project", "Project overview", Create, "\uE8A9", onHost: true )
		.Command( "open", "Project overview", context => Open( context.App ) )
		.Route( "project", request => OpenLink( request.App, request.Path ) );

	/// <summary>Opens the overview a link names: <c>&lt;project&gt;</c> for This PC's project, <c>&lt;project&gt;@&lt;host&gt;</c> for a paired host's.</summary>
	private void OpenLink( LoknApplication app, string path )
	{
		var at = path.IndexOf( '@', StringComparison.Ordinal );
		if ( !Guid.TryParseExact( at < 0 ? path : path[..at], "N", out var project ) )
			throw new ArgumentException( $"{path} names no project." );
		Open( app, project, at < 0 ? LoknHost.LocalId : path[(at + 1)..] );
	}

	/// <summary>Opens a project's overview, or brings the one on screen forward; without a project, the open session's project, else a page that
	/// lists the projects to choose from.</summary>
	/// <param name="app">The application.</param>
	/// <param name="projectId">The project, or null for the open session's.</param>
	/// <param name="hostId">The host the project is on, or null for the open session's host.</param>
	public void Open( LoknApplication app, Guid? projectId = null, string? hostId = null )
	{
		if ( projectId is null && ComputeSessionProject( app ) is { } current )
			(projectId, hostId) = current;
		hostId ??= app.Hosts.Current.Id;
		_panels.RemoveAll( panel => panel.IsClosed );
		if ( _panels.FirstOrDefault( panel => panel.ProjectId == projectId && panel.ProjectHostId == hostId && app.Panels.IsShown( panel.Id ) ) is { } shown )
		{
			app.Panels.Activate( shown.Id );
			return;
		}
		app.Panels.Open( PanelType, input: projectId, hostId: hostId );
	}

	/// <summary>Creates an overview panel and tracks it.</summary>
	private ProjectOverviewContent Create( PanelContext context )
	{
		var content = new ProjectOverviewContent( context, context.App.GetModule<ConversationsModule>(), context.App.GetModule<Browser.BrowserModule>() );
		_panels.Add( content );
		return content;
	}

	/// <summary>Finds the project of the open session and its host: a project's session, or a chat's session whose chat is in a project; null otherwise.</summary>
	private static (Guid ProjectId, string HostId)? ComputeSessionProject( LoknApplication app )
	{
		if ( !app.Modules.Any( module => module.Id == "workspaces" ) || app.GetModule<WorkspacesModule>().CurrentSession is not { } key )
			return null;

		if ( key.StartsWith( ProjectsModule.SessionPrefix, StringComparison.Ordinal ) )
		{
			var rest = key[ProjectsModule.SessionPrefix.Length..];
			var at = rest.IndexOf( '@', StringComparison.Ordinal );
			var host = at < 0 ? LoknHost.LocalId : rest[(at + 1)..];
			return Guid.TryParseExact( at < 0 ? rest : rest[..at], "N", out var id ) ? (id, host) : null;
		}

		const string ChatPrefix = "conversation:";
		return key.StartsWith( ChatPrefix, StringComparison.Ordinal ) && ChatRef.TryParse( key[ChatPrefix.Length..], out var chat )
			&& app.GetModule<ConversationsModule>().Hosts.Find( chat ) is { Conversation.ProjectId: { } project }
			? (project, chat.HostId)
			: null;
	}
}
