using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Overview;

/// <summary>Contributes a project's overview: one page, set as a daily brief, with its chats and tasks and their state, its designs and reports, and
/// the cards other modules add to <see cref="BriefSection.Point"/>, such as the project's pull requests.</summary>
/// <remarks>The sidebar opens it with the overview button on a project's heading, through its <c>lokn://overview/project/…</c> link, so Conversations
/// does not reference this module.</remarks>
public sealed class OverviewModule : ILoknModule
{
	/// <summary>Defines the overview panel's type id, which the sidebar opens by name.</summary>
	public const string PanelType = "overview.project";

	/// <summary>Stores the open overview panels, so opening a project's overview again shows the one already on screen.</summary>
	private readonly List<ProjectOverviewContent> _panels = [];

	/// <summary>Gets each project's last written brief line by <see cref="ProjectOverviewContent.BriefKey"/>, shared by its pages so reopening one
	/// does not ask the model again.</summary>
	internal Dictionary<string, BriefLine> Briefs { get; } = [];

	/// <summary>Gets the brief's to-dos the user checked off, by brief, day and item, so a checked one stays checked on every page of the project that
	/// day; a new day's keys start empty.</summary>
	internal HashSet<string> Done { get; } = [];

	/// <summary>Gets the module identifier.</summary>
	public string Id => "overview";

	/// <summary>Gets the module title.</summary>
	public string Title => "Project overview";

	/// <summary>Gets the modules whose chats, projects and reports the page lists.</summary>
	public IReadOnlyList<string> Dependencies => ["conversations", "browser"];

	/// <summary>Registers the overview panel, its palette command, the <c>lokn://overview/project/&lt;project&gt;[@host]</c> link the sidebar opens it with,
	/// and the point other modules add brief sections to.</summary>
	public void Register( ModuleBuilder builder ) => builder
		.Panel( "project", "Project overview", Create, "\uE8A9", onHost: true )
		.Command( "open", "Project overview", context => Open( context.App ) )
		.Route( "project", request => OpenLink( request.App, request.Path ) )
		.ExtensionPoint<BriefSection>( "sections" );

	/// <summary>Opens the overview a link names: <c>&lt;project&gt;</c> for This PC's project, <c>&lt;project&gt;@&lt;host&gt;</c> for a paired host's.</summary>
	private void OpenLink( LoknApplication app, string path )
	{
		var at = path.IndexOf( '@', StringComparison.Ordinal );
		if ( !Guid.TryParseExact( at < 0 ? path : path[..at], "N", out var project ) )
			throw new ArgumentException( $"{path} names no project." );
		Open( app, project, at < 0 ? LoknHost.LocalId : path[(at + 1)..] );
	}

	/// <summary>Opens a project's overview as its own page: the overview session of the project, which shows the overview alone, full window, the
	/// first time, then as the user left it. Without a project it opens the open session's project, else a page that lists the projects.</summary>
	/// <remarks>Without the Workspaces module there are no sessions, so the overview opens as a panel in the current layout.</remarks>
	/// <param name="app">The application.</param>
	/// <param name="projectId">The project, or null for the open session's.</param>
	/// <param name="hostId">The host the project is on, or null for the open session's host.</param>
	public void Open( LoknApplication app, Guid? projectId = null, string? hostId = null )
	{
		if ( projectId is null && ComputeSessionProject( app ) is { } current )
			(projectId, hostId) = current;
		hostId ??= app.Hosts.Current.Id;
		if ( projectId is { } id && FindWorkspaces( app ) is { } workspaces )
		{
			var name = app.GetModule<ConversationsModule>().Hosts.Find( hostId )?.Projects.Find( id )?.Name ?? "Project";
			// The host is current before the layout opens, so panels the page opens without a host of their own open on it.
			app.Hosts.SetCurrent( hostId );
			workspaces.OpenSession( app, SessionKey( id, hostId ), $"{name} overview", () =>
			{
				app.Panels.HideAll();
				app.Panels.Open( PanelType, input: projectId, hostId: hostId );
			} );
		}

		// The page's own overview is the one its layout shows; an overview hidden in another session, such as one opened before pages existed,
		// stays there rather than joining this page beside it.
		_panels.RemoveAll( panel => panel.IsClosed );
		if ( _panels.FirstOrDefault( panel => panel.ProjectId == projectId && panel.ProjectHostId == hostId && app.Panels.IsShown( panel.Id ) ) is { } open )
		{
			app.Panels.Activate( open.Id );
			return;
		}
		// The user closed the overview on its page, or there are no sessions: it opens in the current layout.
		app.Panels.Open( PanelType, input: projectId, hostId: hostId );
	}

	/// <summary>Gets the session key of a project's overview: <c>overview:&lt;project&gt;</c>, with <c>@&lt;host&gt;</c> for a paired host's project.</summary>
	internal static string SessionKey( Guid projectId, string hostId ) =>
		hostId == LoknHost.LocalId ? $"{SessionPrefix}{projectId:N}" : $"{SessionPrefix}{projectId:N}@{hostId}";

	/// <summary>Defines the prefix of an overview session's key.</summary>
	private const string SessionPrefix = "overview:";

	/// <summary>Finds the Workspaces module while it is loaded.</summary>
	private static WorkspacesModule? FindWorkspaces( LoknApplication app ) =>
		app.Modules.Any( module => module.Id == "workspaces" ) ? app.GetModule<WorkspacesModule>() : null;

	/// <summary>Creates an overview panel and tracks it.</summary>
	private ProjectOverviewContent Create( PanelContext context )
	{
		var content = new ProjectOverviewContent( context, this, context.App.GetModule<ConversationsModule>(), context.App.GetModule<Browser.BrowserModule>() );
		_panels.Add( content );
		return content;
	}

	/// <summary>Finds the project of the open session and its host: a project's or its overview's session, or a chat's session whose chat is in a
	/// project; null otherwise.</summary>
	private static (Guid ProjectId, string HostId)? ComputeSessionProject( LoknApplication app )
	{
		if ( FindWorkspaces( app )?.CurrentSession is not { } key )
			return null;

		foreach ( var prefix in (string[])[ProjectsModule.SessionPrefix, SessionPrefix] )
		{
			if ( !key.StartsWith( prefix, StringComparison.Ordinal ) )
				continue;
			var rest = key[prefix.Length..];
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

/// <summary>Holds a project's brief line as the model last wrote it.</summary>
/// <param name="Text">The line, or empty when the model could not write one.</param>
/// <param name="Written">When it was asked for.</param>
/// <param name="Fingerprint">Which of the project's chats waited or worked then, so a change can ask again.</param>
internal sealed record BriefLine( string Text, DateTimeOffset Written, string Fingerprint );
