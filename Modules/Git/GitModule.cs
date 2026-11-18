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

	/// <summary>Stores one shared reader per watched folder, keyed by its full path.</summary>
	private readonly Dictionary<string, GitSource> _sources = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Tracks open Git panels so opening a repository's Git panel again reuses the one showing it.</summary>
	private readonly Dictionary<Guid, GitContent> _panels = [];

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
		.Panel( "panel", "Git", BuildPanel, "\uF003" )
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
			FixAsync = ( progress, cancellationToken ) => ToolLocator.InstallWingetAsync( "Git.Git", progress, cancellationToken ),
		} );

	/// <summary>Checks that git runs, reporting its version and path.</summary>
	private static async Task<SetupStatus> CheckGitAsync( CancellationToken cancellationToken )
	{
		try
		{
			var output = await GitProcess.RunAsync( AppContext.BaseDirectory, cancellationToken, "--version" ).ConfigureAwait( false );
			return new( SetupState.Ready, $"{output.Trim().Replace( "git version ", "", StringComparison.Ordinal )} · {GitProcess.Executable}" );
		}
		catch ( Exception error ) when ( error is InvalidOperationException or System.ComponentModel.Win32Exception )
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

	/// <summary>Starts watching a folder's git state, sharing the reader with other holders of the same folder; call on the UI thread.</summary>
	/// <returns>The holder; dispose it to stop watching.</returns>
	public GitWatch Watch( string directory )
	{
		var context = _context ?? throw new InvalidOperationException( "The Git module is not running." );
		var key = Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );
		if ( !_sources.TryGetValue( key, out var source ) )
			_sources.Add( key, source = new GitSource( context, key, released => _sources.Remove( released.Directory ) ) );

		return source.Hold();
	}

	/// <summary>Shows the Git panel of the repository holding a folder, on its graph or on one changed file's diff, reusing an open one for that
	/// repository or opening one beside the panel that asked; call on the UI thread.</summary>
	/// <param name="app">The application.</param>
	/// <param name="besidePanelId">The panel that asked, such as a chat or the Files side view.</param>
	/// <param name="directory">A folder inside the repository.</param>
	/// <param name="focus">The full path of a changed file whose diff shows once the changes load, or null for the graph beside the working tree.</param>
	/// <param name="staged">Whether the file shows its staged change first.</param>
	/// <param name="keepFocus">Whether the panel that asked keeps the focus, as the side view's file list does so the reader can go on through its files.</param>
	public void OpenPanel( LoknApplication app, Guid besidePanelId, string directory, string? focus = null, bool staged = false, bool keepFocus = false )
	{
		Forget( app, _panels );
		if ( _panels.FirstOrDefault( item => item.Value.Shows( directory ) ) is { Value: { } open } existing )
		{
			open.Reveal( focus, staged );
			if ( !keepFocus || !app.Panels.IsShown( existing.Key ) )
				app.Panels.Activate( existing.Key );
			if ( keepFocus )
				app.Panels.Activate( besidePanelId );
			return;
		}

		app.Panels.OpenBeside( PanelType, besidePanelId, new GitPanelInput( directory, Focus: focus, Staged: staged ) );
		if ( keepFocus )
			app.Panels.Activate( besidePanelId );
	}

	/// <summary>Defines the Files side view's Changes mode, which a newly opened Git panel shows beside it.</summary>
	private const string ChangesMode = "git.changes";

	/// <summary>Opens the Files side view in its Changes mode beside a Git panel the user just opened in the open project, so its files are a click away.</summary>
	/// <remarks>Only a panel on screen whose folder is the project's, or inside it, does; a restored panel, or one on another repository, leaves the side view as it is.</remarks>
	private static void ShowChanges( PanelContext context, string directory )
	{
		var app = context.App;
		if ( !app.Panels.IsShown( context.Id ) || app.Services.Get<IWorkingFolder>()?.Current is not { Length: > 0 } project
			|| app.Services.Get<IFileSideView>() is not { } side || !IsInside( directory, project ) && !IsInside( project, directory ) )
			return;

		side.Show( app, ChangesMode );
	}

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

	/// <summary>Gets the folder a panel opened from the palette shows: the open project's, else the one Lokn runs in.</summary>
	private static string ComputeDefaultDirectory( LoknApplication app ) => app.Services.Get<IWorkingFolder>()?.Current ?? Environment.CurrentDirectory;

	/// <summary>Opens a Git panel on its requested or restored folder; from the palette, on the working folder.</summary>
	/// <remarks>Layouts saved by Conversations, and diff panels saved before the Git panel showed files, restore here through their former identifiers; their folder carries over.</remarks>
	private GitContent BuildPanel( PanelContext context )
	{
		var input = context.RestoredState?.Deserialize<GitPanelInput>() is { Directory.Length: > 0 } restored ? restored : context.Input as GitPanelInput;
		var isNew = context.RestoredState is null;
		input ??= new GitPanelInput( ComputeDefaultDirectory( context.App ) );
		Forget( context.App, _panels );
		// The panel is placed once this returns, so the side view opens after it, and only for a panel the user opened now.
		if ( isNew )
			context.Post( () => ShowChanges( context, input.Directory ) );
		return _panels[context.Id] = new GitContent( context, input );
	}
}
