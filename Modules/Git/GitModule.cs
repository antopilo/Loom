using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Runs git for the other features, keeps each watched folder's repository, branch and changes current, and draws the Git panel
/// and the Files side view's Changes and History modes.</summary>
/// <remarks>
/// A feature that shows git state declares <c>git</c> as a dependency and calls <see cref="Watch"/>; one that runs git commands uses
/// <see cref="GitProcess"/>; one that shows a repository or a changed file's diff calls <see cref="OpenPanel"/>. Other modules add actions to the
/// Git views through <see cref="GitAction.Point"/>. The side-view modes reach the Files module only through Core's <see cref="FileTreeMode"/> contract.
/// </remarks>
public sealed class GitModule : ILoknModule
{
	/// <summary>Defines the Git panel's type.</summary>
	public const string PanelType = "git.panel";

	/// <summary>Defines whether the Git panel shows authors' pictures, fetched from GitHub for no-reply addresses and from Gravatar otherwise.</summary>
	internal static readonly Setting<bool> Avatars = new(
		"git.avatars",
		"Author pictures",
		"Show commit authors' pictures in the Git panel, fetched from GitHub for GitHub no-reply addresses and from Gravatar otherwise. Off shows initials only and fetches nothing.",
		defaultValue: true,
		category: "Git" );

	/// <summary>Defines the Changes mode's glyph, a pencil.</summary>
	private const string ChangesGlyph = "\uE70F";

	/// <summary>Defines the History mode's glyph, a clock with a back arrow.</summary>
	private const string HistoryGlyph = "\uE81C";

	/// <summary>Defines the glyph of the Git panel a new task can start with, a branch.</summary>
	private const string BranchGlyph = "\uF003";

	/// <summary>Stores one shared reader per watched folder on each host, keyed by the host's id and the folder's full path.</summary>
	private readonly Dictionary<(string Host, string Directory), GitSource> _sources = new( new SourceKeyComparer() );

	/// <summary>Tracks open Git panels so opening a repository's Git panel again reuses the one showing it.</summary>
	private readonly Dictionary<Guid, GitContent> _panels = [];

	/// <summary>Stores the panel a Git panel opens beside while <see cref="OpenPanel"/> opens it, for the panel's factory; null otherwise.</summary>
	private Guid? _openingBeside;

	/// <summary>Stores the running module context, or null while the module is stopped.</summary>
	private ModuleContext? _context;

	/// <summary>Gets the authors' pictures; null while the module is stopped.</summary>
	internal GitAvatars? Pictures { get; private set; }

	/// <summary>Gets the module identifier.</summary>
	public string Id => "git";

	/// <summary>Gets the display name.</summary>
	public string Title => "Git";

	/// <summary>Registers the action and message-writer points, the Git panel under its former identifiers too (the diff panel's among them), the
	/// Git panel a new task can start with, the side-view modes, and the editor's gutter marks.</summary>
	/// <remarks>Without the Files or Editor module, the mode and gutter contributions are unused.</remarks>
	public void Register( ModuleBuilder builder ) => builder
		.Setting( Avatars )
		.ExtensionPoint<GitAction>( "actions" )
		.ExtensionPoint<GitMessageWriter>( "messages" )
		.Panel( "panel", "Git", BuildPanel, "\uF003", onHost: true )
		.PanelAlias( "conversations.git", "panel" )
		// The diff panel became the Git panel's file view; saved diff panels restore as Git panels on the same folder.
		.PanelAlias( "git.diff", "panel" )
		.PanelAlias( "conversations.diff", "panel" )
		.Contribute( TaskStarter.Point, new TaskStarter( "Git", BranchGlyph, 20, app => app.Panels.Open( "git.panel" ) ) )
		.Contribute( FileTreeMode.Point, new FileTreeMode( "git.changes", ChangesGlyph, "Changes", 10, context => new GitSideMode( context, GitLayout.Changes ) ) )
		.Contribute( FileTreeMode.Point, new FileTreeMode( "git.history", HistoryGlyph, "History", 20, context => new GitSideMode( context, GitLayout.History ) ) )
		.Contribute( LineMarkSource.Point, GitLineMarks.Source )
		.Contribute( SetupItem.Point, new SetupItem
		{
			Key = "git",
			Title = "Git",
			Purpose = "Shows changes, history and worktrees",
			Order = 10,
			CheckAsync = CheckGitAsync,
			FixLabel = "Install",
			FixAsync = OperatingSystem.IsWindows() ? ( progress, cancellationToken ) => ToolLocator.InstallWingetAsync( "Git.Git", progress, cancellationToken ) : null,
		} );

	/// <summary>Checks that git runs on this PC, where the Install fix puts it, reporting its version and path.</summary>
	private static async Task<SetupStatus> CheckGitAsync( CancellationToken cancellationToken )
	{
		try
		{
			var result = await LocalWorkspaceHost.Shared.RunAsync( GitProcess.Executable, ["--version"], AppContext.BaseDirectory, null, null, cancellationToken ).ConfigureAwait( false );
			if ( result.Code != 0 )
				return new( SetupState.Missing, "Not installed" );

			return new( SetupState.Ready, $"{result.Output.Trim().Replace( "git version ", "", StringComparison.Ordinal )} · {GitProcess.Executable}" );
		}
		catch ( System.ComponentModel.Win32Exception )
		{
			return new( SetupState.Missing, "Not installed" );
		}
	}

	/// <summary>Keeps the context that reads post their results through, and starts the picture loader.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		Pictures = new GitAvatars( context );
	}

	/// <summary>Stops every reader and its file watchers.</summary>
	public void Stop()
	{
		foreach ( var source in _sources.Values )
			source.Stop();
		_sources.Clear();
		_panels.Clear();
		Pictures?.Dispose();
		Pictures = null;
		_context = null;
	}

	/// <summary>Starts watching a folder's git state on a host, sharing the reader with other holders of the same folder on that host; call on the UI thread.</summary>
	/// <param name="host">The host the folder is on, such as the panel's <see cref="PanelContext.Host"/>; the same path on another host is another folder.</param>
	/// <param name="directory">The folder.</param>
	/// <returns>The holder; dispose it to stop watching.</returns>
	public GitWatch Watch( LoknHost host, string directory )
	{
		var context = _context ?? throw new InvalidOperationException( "The Git module is not running." );
		var folder = Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );
		if ( !_sources.TryGetValue( (host.Id, folder), out var source ) )
			_sources.Add( (host.Id, folder), source = new GitSource( context, host, folder, released => _sources.Remove( (released.Host.Id, released.Directory) ) ) );

		return source.Hold();
	}

	/// <summary>Shows the Git panel of the repository holding a folder, on its graph or on one changed file's diff, reusing an open one for that
	/// repository on the same host or opening one beside the panel that asked, on that panel's host; call on the UI thread.</summary>
	/// <param name="app">The application.</param>
	/// <param name="besidePanelId">The panel that asked, such as a chat or the Files side view; its host is the folder's.</param>
	/// <param name="directory">A folder inside the repository.</param>
	/// <param name="focus">The full path of a changed file whose diff shows once the changes load, or null for the graph beside the working tree.</param>
	/// <param name="staged">Whether the file shows its staged change first.</param>
	/// <param name="keepFocus">Whether the panel that asked keeps the focus, as the side view's file list does so the reader can go on through its files.</param>
	public void OpenPanel( LoknApplication app, Guid besidePanelId, string directory, string? focus = null, bool staged = false, bool keepFocus = false )
	{
		Forget( app, _panels );
		var host = app.Panels.GetHostId( besidePanelId ) ?? app.Hosts.Current.Id;
		if ( _panels.FirstOrDefault( item => item.Value.Shows( directory ) && app.Panels.GetHostId( item.Key ) == host ) is { Value: { } open } existing )
		{
			open.Reveal( focus, staged );
			if ( !keepFocus || !app.Panels.IsShown( existing.Key ) )
				app.Panels.Activate( existing.Key );
			if ( keepFocus )
				app.Panels.Activate( besidePanelId );
			return;
		}

		_openingBeside = besidePanelId;
		try
		{
			app.Panels.OpenBeside( PanelType, besidePanelId, new GitPanelInput( directory, Focus: focus, Staged: staged ), host );
		}
		finally
		{
			_openingBeside = null;
		}
		if ( keepFocus )
			app.Panels.Activate( besidePanelId );
	}

	/// <summary>Defines the Files side view's Changes mode, which a newly opened Git panel shows beside it.</summary>
	internal const string ChangesMode = "git.changes";

	/// <summary>Gets the Files side view when its Changes mode shows a folder's repository: the folder is the open project's, inside it, or holds it.</summary>
	/// <returns>The side view, or null without the Files module, without an open project, or for a folder of another repository.</returns>
	internal static IFileSideView? FindSideView( PanelContext context, string directory ) =>
		context.WorkingFolder is { Length: > 0 } project && (IsInside( directory, project ) || IsInside( project, directory ))
			? context.App.Services.Get<IFileSideView>()
			: null;

	/// <summary>Opens the Files side view in its Changes mode beside a Git panel the user just opened in the open project, so its files are a click away,
	/// and keeps the Git panel next to it.</summary>
	/// <remarks>Only a panel on screen whose folder is the project's, or inside it, does; a restored panel, or one on another repository, leaves the side view as it is.</remarks>
	/// <param name="context">The new Git panel's context.</param>
	/// <param name="directory">The folder the Git panel shows.</param>
	/// <param name="beside">The panel the Git panel opened beside, or null.</param>
	private static void ShowChanges( PanelContext context, string directory, Guid? beside )
	{
		var app = context.App;
		if ( !app.Panels.IsShown( context.Id ) || FindSideView( context, directory ) is not { } side )
			return;

		side.Show( app, ChangesMode );
		if ( beside is { } source )
			MoveNextToSideView( app.Panels, context.Id, source );
	}

	/// <summary>Moves a Git panel that opened right of a panel to that panel's left, so it sits next to the side view instead of leaving that panel between them.</summary>
	/// <remarks>The side view opens at the left edge. A Git panel opened beside a chat splits the chat's pane to its right, which put the chat between the
	/// two Git views. Nothing moves when the panel the Git panel opened beside is at the left edge itself, such as the side view, or is not its left neighbor.</remarks>
	private static void MoveNextToSideView( PanelWorkspace panels, Guid gitId, Guid besideId )
	{
		var root = panels.Root;
		if ( FindParent( root, gitId ) is not { Axis: SplitAxis.Horizontal, Second: DockPane { PanelId: { } second } } split || second != gitId )
			return;

		var left = FindEdge( split.First, last: true );
		if ( left.PanelId != besideId || FindEdge( root, last: false ).Id == left.Id )
			return;

		panels.Move( gitId, left.Id, PanelPlacement.Left );
	}

	/// <summary>Finds the split one of whose two children is the pane holding a panel.</summary>
	private static DockSplit? FindParent( DockNode node, Guid panelId ) => node switch
	{
		DockSplit { First: DockPane { PanelId: var first } } split when first == panelId => split,
		DockSplit { Second: DockPane { PanelId: var second } } split when second == panelId => split,
		DockSplit split => FindParent( split.First, panelId ) ?? FindParent( split.Second, panelId ),
		_ => null,
	};

	/// <summary>Finds the first or the last pane of a layout, following each split's first or second child.</summary>
	private static DockPane FindEdge( DockNode node, bool last ) => node switch
	{
		DockPane pane => pane,
		DockSplit split => FindEdge( last ? split.Second : split.First, last ),
		_ => throw new InvalidOperationException( "The layout has an unknown node." ),
	};

	/// <summary>Reports whether a folder is another one or inside it, whatever the separators or letter case.</summary>
	private static bool IsInside( string folder, string parent )
	{
		var inner = Path.TrimEndingDirectorySeparator( Path.GetFullPath( folder ) );
		var outer = Path.TrimEndingDirectorySeparator( Path.GetFullPath( parent ) );
		return string.Equals( inner, outer, StringComparison.OrdinalIgnoreCase ) || inner.StartsWith( outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>Drops closed panels from a map.</summary>
	private static void Forget<T>( LoknApplication app, Dictionary<Guid, T> panels )
	{
		foreach ( var closed in panels.Keys.Where( id => app.Panels.Panels.All( panel => panel.Id != id ) ).ToArray() )
			panels.Remove( closed );
	}

	/// <summary>Gets the folder a panel opened from the palette shows: the open project's on the panel's host, else on This PC the one Lokn runs in; null on a
	/// paired host without one, whose disk has no such folder.</summary>
	private static string? ComputeDefaultDirectory( PanelContext context ) => context.WorkingFolder ?? (context.Host.IsLocal ? Environment.CurrentDirectory : null);

	/// <summary>Opens a Git panel on its requested or restored folder; from the palette, on the working folder, or without one on a paired host, a panel
	/// that says to open a project there.</summary>
	/// <remarks>Layouts saved by Conversations, and diff panels saved before the Git panel showed files, restore here through their former identifiers; their folder carries over.</remarks>
	private PanelContent BuildPanel( PanelContext context )
	{
		var input = context.RestoredState?.Deserialize<GitPanelInput>() is { Directory.Length: > 0 } restored ? restored : context.Input as GitPanelInput;
		var isNew = context.RestoredState is null;
		if ( input is null )
		{
			if ( ComputeDefaultDirectory( context ) is not { } directory )
				return new NoFolderContent( context );
			input = new GitPanelInput( directory );
		}
		Forget( context.App, _panels );
		// The panel is placed once this returns, so the side view opens after it, and only for a panel the user opened now.
		if ( isNew )
		{
			var beside = _openingBeside;
			context.Post( () => ShowChanges( context, input.Directory, beside ) );
		}
		return _panels[context.Id] = new GitContent( context, input );
	}

	/// <summary>A Git panel opened on a paired host without a project open there: no folder of that host to show yet.</summary>
	/// <param name="context">The panel's context.</param>
	private sealed class NoFolderContent( PanelContext context ) : PanelContent
	{
		/// <summary>Builds the note.</summary>
		public override Goo.Blob Build() => new Goo.Text
		{
			Content = $"Open a project on {context.Host.Name} to see its repository.",
			Color = Theme.Current.Muted,
			Padding = Theme.Current.Spacing * 2,
		};
	}

	/// <summary>Compares reader keys: host ids exactly, folders whatever their letter case.</summary>
	private sealed class SourceKeyComparer : IEqualityComparer<(string Host, string Directory)>
	{
		/// <summary>Reports whether two keys name the same folder on the same host.</summary>
		public bool Equals( (string Host, string Directory) x, (string Host, string Directory) y ) =>
			string.Equals( x.Host, y.Host, StringComparison.Ordinal ) && string.Equals( x.Directory, y.Directory, StringComparison.OrdinalIgnoreCase );

		/// <summary>Hashes a key consistently with <see cref="Equals"/>: the host id exactly, the folder whatever its letter case.</summary>
		public int GetHashCode( (string Host, string Directory) key ) =>
			HashCode.Combine( StringComparer.Ordinal.GetHashCode( key.Host ), StringComparer.OrdinalIgnoreCase.GetHashCode( key.Directory ) );
	}
}
