using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Gives one holder a folder's live git state; dispose it to stop watching.</summary>
/// <remarks>Holders of the same folder share one reader and one set of file watchers.</remarks>
public sealed class GitWatch : IDisposable
{
	/// <summary>Stores the shared reader until this holder lets go.</summary>
	private GitSource? _source;

	/// <summary>Creates a holder of a shared reader.</summary>
	internal GitWatch( GitSource source )
	{
		_source = source;
		Directory = source.Directory;
	}

	/// <summary>Gets the watched folder.</summary>
	public string Directory
	{
		get;
	}

	/// <summary>Gets the latest state, or null before the first read finishes; reading it in a view build rebuilds that view when it changes.</summary>
	public GitState? State => _source?.State;

	/// <summary>Reads the state again now, as after a change the watchers cannot see.</summary>
	public void Refresh() => _source?.Refresh();

	/// <summary>Stops watching for this holder; the reader stops with its last holder.</summary>
	public void Dispose()
	{
		_source?.Release();
		_source = null;
	}
}

/// <summary>Reads one folder's git state and reads it again, once changes settle, when its files, index, HEAD or refs change.</summary>
/// <remarks>State changes on the UI thread; reads and file watcher events run off it.</remarks>
internal sealed class GitSource
{
	/// <summary>Defines how long changes must settle before a read, in milliseconds.</summary>
	private const int SettleDelay = 600;

	/// <summary>Lists the files inside a git folder whose change can alter the state; refs below <c>refs</c> count too.</summary>
	private static readonly string[] GitFiles = ["HEAD", "index", "packed-refs", "FETCH_HEAD", "ORIG_HEAD", "MERGE_HEAD"];

	/// <summary>Stores the owning module's context, for posting results to the UI thread.</summary>
	private readonly ModuleContext _context;

	/// <summary>Removes this reader from its module when the last holder lets go.</summary>
	private readonly Action<GitSource> _released;

	/// <summary>Stores the latest state, or null before the first read.</summary>
	private GitState? _state;

	/// <summary>Counts state changes; views read it to rebuild when the state changes.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Delays a read until changes settle.</summary>
	private readonly Timer _settle;

	/// <summary>Stores the file watchers: the working tree, and the git folders when they lie outside it.</summary>
	private readonly List<FileSystemWatcher> _watchers = [];

	/// <summary>Stores the running read, or null when none runs.</summary>
	private CancellationTokenSource? _read;

	/// <summary>Stores whether a change arrived during the running read, so another follows it.</summary>
	private bool _isStale;

	/// <summary>Stores the repository the watchers follow, so they move only when it changes.</summary>
	private string? _watchedRoot;

	/// <summary>Stores how many holders share this reader.</summary>
	private int _holders;

	/// <summary>Stores whether the reader stopped.</summary>
	private bool _isStopped;

	/// <summary>Creates a reader for a folder; it reads once its first holder arrives.</summary>
	internal GitSource( ModuleContext context, string directory, Action<GitSource> released )
	{
		_context = context;
		_released = released;
		Directory = directory;
		_settle = new Timer( _ => _context.Post( Refresh ), null, Timeout.Infinite, Timeout.Infinite );
	}

	/// <summary>Gets the watched folder.</summary>
	internal string Directory
	{
		get;
	}

	/// <summary>Gets the latest state, subscribing the reading view.</summary>
	internal GitState? State
	{
		get
		{
			_ = _version.Value;
			return _state;
		}
	}

	/// <summary>Adds a holder, reading the state the first time.</summary>
	internal GitWatch Hold()
	{
		if ( _holders++ == 0 )
		{
			Refresh();
			UpdateWatchers( Directory );
		}
		return new( this );
	}

	/// <summary>Removes a holder, stopping when it was the last.</summary>
	internal void Release()
	{
		if ( --_holders > 0 || _isStopped )
			return;

		Stop();
		_released( this );
	}

	/// <summary>Reads the state in the background, or marks the running read stale so another follows it.</summary>
	internal void Refresh()
	{
		if ( _isStopped )
			return;

		if ( _read is not null )
		{
			_isStale = true;
			return;
		}

		var read = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_read = read;
		_ = Task.Run( async () =>
		{
			GitState state;
			try
			{
				state = await GitState.ReadAsync( Directory, read.Token ).ConfigureAwait( false );
			}
			catch ( OperationCanceledException )
			{
				return;
			}
			catch ( Exception error ) when ( error is not OutOfMemoryException )
			{
				state = GitState.BuildEmpty( Directory, error.Message );
			}
			_context.Post( () => Apply( read, state ) );
		} );
	}

	/// <summary>Stops reading and watching.</summary>
	internal void Stop()
	{
		_isStopped = true;
		_read?.Cancel();
		_settle.Dispose();
		foreach ( var watcher in _watchers )
			watcher.Dispose();
		_watchers.Clear();
	}

	/// <summary>Publishes a finished read, moves the watchers to its repository, and reads again when changes arrived meanwhile.</summary>
	private void Apply( CancellationTokenSource read, GitState state )
	{
		if ( _read != read )
			return;

		_read = null;
		read.Dispose();
		if ( _isStopped )
			return;

		if ( state != _state )
		{
			_state = state;
			_version.Value++;
		}

		if ( state.Root != _watchedRoot && state.Root is { } root )
			_ = UpdateGitFoldersAsync( root );

		if ( _isStale )
		{
			_isStale = false;
			Refresh();
		}
	}

	/// <summary>Finds a repository's git folders, which a linked worktree keeps outside its tree, and watches them with the tree.</summary>
	private async Task UpdateGitFoldersAsync( string root )
	{
		_watchedRoot = root;
		string[] folders = [];
		try
		{
			var output = await GitProcess.RunAsync( root, _context.Lifetime, "rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir" ).ConfigureAwait( false );
			folders = [.. output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).Select( Path.GetFullPath ).Distinct( StringComparer.OrdinalIgnoreCase )];
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The tree watcher still sees a git folder inside it.
		}
		_context.Post( () =>
		{
			if ( !_isStopped && _watchedRoot == root )
				UpdateWatchers( root, folders );
		} );
	}

	/// <summary>Replaces the watchers with one over a working tree and one over each git folder outside it.</summary>
	private void UpdateWatchers( string tree, IReadOnlyList<string>? gitFolders = null )
	{
		foreach ( var watcher in _watchers )
			watcher.Dispose();
		_watchers.Clear();

		var treeWatcher = TryWatch( tree, MarkTreeChanged );
		if ( treeWatcher is not null )
			_watchers.Add( treeWatcher );

		var inside = Path.TrimEndingDirectorySeparator( tree ) + Path.DirectorySeparatorChar;
		foreach ( var folder in gitFolders ?? [] )
		{
			if ( (folder + Path.DirectorySeparatorChar).StartsWith( inside, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( TryWatch( folder, MarkGitChanged ) is { } gitWatcher )
				_watchers.Add( gitWatcher );
		}
	}

	/// <summary>Starts a recursive watcher, or returns null when the folder cannot be watched.</summary>
	private FileSystemWatcher? TryWatch( string folder, FileSystemEventHandler changed )
	{
		if ( !System.IO.Directory.Exists( folder ) )
			return null;

		try
		{
			var watcher = new FileSystemWatcher( folder )
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			watcher.Changed += changed;
			watcher.Created += changed;
			watcher.Deleted += changed;
			watcher.Renamed += ( sender, change ) => changed( sender, change );
			// A full event buffer lost changes; read once to catch up.
			watcher.Error += ( _, _ ) => MarkChanged();
			watcher.EnableRaisingEvents = true;
			return watcher;
		}
		catch ( Exception error ) when ( error is IOException or ArgumentException or UnauthorizedAccessException )
		{
			return null;
		}
	}

	/// <summary>Restarts the settle delay for a working-tree change, or a git folder change inside the tree that matters.</summary>
	/// <remarks>Runs on a watcher thread.</remarks>
	private void MarkTreeChanged( object sender, FileSystemEventArgs change )
	{
		var marker = $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}";
		var inside = change.FullPath.IndexOf( marker, StringComparison.OrdinalIgnoreCase );
		if ( inside < 0 || IsGitChange( change.FullPath[(inside + marker.Length)..] ) )
			MarkChanged();
	}

	/// <summary>Restarts the settle delay for a change in a git folder outside the tree that matters.</summary>
	/// <remarks>Runs on a watcher thread.</remarks>
	private void MarkGitChanged( object sender, FileSystemEventArgs change ) =>
		MarkChanged( IsGitChange( change.Name ?? "" ) );

	/// <summary>Reports whether a path inside a git folder can change the state: HEAD, the index, refs, and a linked worktree's own HEAD and index.</summary>
	internal static bool IsGitChange( string relative )
	{
		if ( relative.EndsWith( ".lock", StringComparison.OrdinalIgnoreCase ) )
			return false;

		var parts = relative.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
		return parts[0] == "refs" || parts[^1] is var name && GitFiles.Contains( name ) && (parts.Length == 1 || parts is ["worktrees", _, _]);
	}

	/// <summary>Restarts the settle delay when the change matters.</summary>
	private void MarkChanged( bool matters = true )
	{
		if ( !matters || _isStopped )
			return;

		try
		{
			_settle.Change( SettleDelay, Timeout.Infinite );
		}
		catch ( ObjectDisposedException )
		{
			// The reader stopped while the watcher reported.
		}
	}
}
