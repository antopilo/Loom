using System.ComponentModel;
using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Terminal;

/// <summary>Runs one shell in a pseudoconsole and shows it as an interactive terminal grid.</summary>
internal sealed partial class TerminalContent : PanelContent
{
	/// <summary>Defines the grid size used until the panel is measured.</summary>
	private const int InitialColumns = 80;

	/// <summary>Defines the grid height used until the panel is measured.</summary>
	private const int InitialRows = 24;

	/// <summary>Stores the panel's guarded dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the shell this panel runs.</summary>
	private readonly ShellProfile _profile;

	/// <summary>Stores the variables the shell gets on top of the host's, which programs choose 24-bit color and terminal-specific behavior from.</summary>
	private static readonly Dictionary<string, string> ShellVariables = new()
	{
		["COLORTERM"] = "truecolor",
		["TERM_PROGRAM"] = "Lokn",
	};

	/// <summary>Stores the folders the shell may start in, best first: the restored one, then the working folder such as the open project's, when it is on
	/// the panel's host.</summary>
	private readonly string[] _directories;

	/// <summary>Stores the directory the shell starts in: the first of <see cref="_directories"/> that exists, else This PC's user folder, or null for a
	/// paired host's own default.</summary>
	private string? _directory;

	/// <summary>Stores the terminal state the shell's output drives.</summary>
	private readonly TerminalEmulator _terminal;

	/// <summary>Collects output from the reader thread until the UI thread applies it.</summary>
	private readonly System.Text.StringBuilder _pending = new();

	/// <summary>Counts changes to the terminal so the grid rebuilds; the grid reads it.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the running shell, or null before it starts and after it exits.</summary>
	private IWorkspacePty? _session;

	/// <summary>Tracks whether applying output is already queued on the UI thread.</summary>
	private bool _isFlushQueued;

	/// <summary>Tracks whether starting the shell is queued or done for the current run.</summary>
	private bool _isStarted;

	/// <summary>Tracks whether the shell ended and Enter restarts it.</summary>
	private bool _hasExited;

	/// <summary>Creates the panel for an explicit shell, a restored one, or the configured default, in the folder an agent asked for, else its restored
	/// folder, else the working folder such as the open project's, else the user's home: This PC's user folder, or the paired host's own.</summary>
	/// <remarks>A paired host gets nothing of This PC's own: no shell path detected here (<see cref="ShellProfile.ForHost"/>) and no folder of this disk.</remarks>
	internal TerminalContent( PanelContext context )
	{
		_context = context;
		var restored = ReadRestored( context.RestoredState );
		var launch = context.Input as TerminalLaunch;
		_profile = launch?.Profile
			?? context.Input as ShellProfile
			?? restored.Profile
			?? ShellProfile.Resolve( context.App.Settings.Get( TerminalModule.Shell ), context.Host.IsLocal );
		if ( !context.Host.IsLocal )
			_profile = _profile.ForHost();
		_directories = [.. new[] { launch?.Directory, restored.Directory, context.WorkingFolder }.OfType<string>()];
		_directory = _directories.FirstOrDefault() ?? Home;
		_terminal = new( InitialColumns, InitialRows, context.App.Settings.Get( TerminalModule.Scrollback ) );
		_terminal.Replied += Write;
		_terminal.Copied += text => context.App.Window?.Clipboard.WriteText( text );
	}

	/// <summary>Captures the shell and its last reported directory so a restart reopens it there.</summary>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( new SavedTerminal( _profile.Name, _profile.CommandLine, _terminal.WorkingDirectory ?? _directory ) );

	/// <summary>Ends the shell when the panel closes.</summary>
	public override void Closed()
	{
		_session?.Dispose();
		_session = null;
	}

	/// <summary>Starts the shell at the measured grid size, in the first of its folders the workspace has.</summary>
	private void Start()
	{
		_hasExited = false;
		_grid.Focus();
		var workspace = _context.Workspace;
		FindDirectoryAsync( workspace, _directories, _context.Lifetime ).WhenDone( _context.Post, found =>
		{
			_directory = found.IsCompletedSuccessfully && found.Result is { } directory ? directory : Home;
			Open( workspace );
		} );
	}

	/// <summary>Gets the folder a shell starts in when none of its folders exists: This PC's user folder, or null on a paired host, which then starts it in
	/// its own user's folder.</summary>
	private string? Home => _context.Host.IsLocal ? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ) : null;

	/// <summary>Opens the shell's pseudoterminal through the workspace, or explains why it could not start.</summary>
	/// <remarks>This PC gets the shell setting's command line as written, since programs such as <c>cmd</c> read their own command line by rules that
	/// splitting it into arguments would not keep; a remote host gets the program and its arguments.</remarks>
	private void Open( IWorkspaceHost workspace )
	{
		try
		{
			Action<int> exited = code => _context.Post( () => UpdateExited( code ) );
			if ( workspace is LocalWorkspaceHost local )
				_session = local.OpenPty( _profile.CommandLine, _directory, _terminal.Columns, _terminal.Rows, ShellVariables, QueueOutput, exited );
			else
			{
				var (file, arguments) = _profile.Split();
				_session = workspace.OpenPty( file, arguments, _directory, _terminal.Columns, _terminal.Rows, ShellVariables, QueueOutput, exited );
			}
		}
		catch ( Exception error ) when ( error is Win32Exception or System.Runtime.InteropServices.COMException )
		{
			_hasExited = true;
			_terminal.Feed( $"\x1b[0m\x1b[31mCould not start {_profile.CommandLine}: {error.Message}\x1b[0m\r\nChange the Terminal shell setting, then press Enter to try again.\r\n" );
			UpdateVersion();
		}
	}

	/// <summary>Finds the first folder the workspace has, or null when it has none of them.</summary>
	private static async Task<string?> FindDirectoryAsync( IWorkspaceHost workspace, IReadOnlyList<string> directories, CancellationToken cancellationToken )
	{
		foreach ( var directory in directories )
		{
			try
			{
				if ( await workspace.DirectoryExistsAsync( directory, cancellationToken ).ConfigureAwait( false ) )
					return directory;
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException )
			{
				// A folder that cannot be checked is passed over, as a missing one is.
			}
		}
		return null;
	}

	/// <summary>Collects output on the reader thread and queues one UI-thread apply for a burst.</summary>
	private void QueueOutput( string text )
	{
		lock ( _pending )
		{
			_pending.Append( text );
			if ( _isFlushQueued )
				return;

			_isFlushQueued = true;
		}

		if ( !_context.Post( ApplyOutput ) )
		{
			lock ( _pending )
				_isFlushQueued = false;
		}
	}

	/// <summary>Feeds queued output to the terminal, keeping a scrolled-back view on the same lines.</summary>
	private void ApplyOutput()
	{
		string text;
		lock ( _pending )
		{
			text = _pending.ToString();
			_pending.Clear();
			_isFlushQueued = false;
		}

		var firstLine = _terminal.FirstScreenLine;
		_terminal.Feed( text );
		if ( _scrollOffset > 0 )
			_scrollOffset = (int)Math.Clamp( _scrollOffset + _terminal.FirstScreenLine - firstLine, 0, _terminal.History.Count );

		UpdateVersion();
		UpdateInputArea();
	}

	/// <summary>Closes the panel after a clean exit, or reports the exit code and offers a restart.</summary>
	private void UpdateExited( int code )
	{
		ApplyOutput();
		_session?.Dispose();
		_session = null;
		if ( code == 0 )
		{
			_context.App.Panels.Close( _context.Id );
			return;
		}

		_hasExited = true;
		_terminal.Feed( $"\x1b[0m\r\n\x1b[2m[Process exited with code {code}. Press Enter to restart.]\x1b[0m\r\n" );
		UpdateVersion();
	}

	/// <summary>Starts the shell again after it exited.</summary>
	private void UpdateRestart()
	{
		_isStarted = true;
		Start();
	}

	/// <summary>Sends input to the shell.</summary>
	private void Write( string text ) => _session?.Write( text );

	/// <summary>Requests a grid rebuild.</summary>
	private void UpdateVersion() => _version.Value++;

	/// <summary>Reads the shell and directory saved by <see cref="CaptureState"/>.</summary>
	private static (ShellProfile? Profile, string? Directory) ReadRestored( JsonElement? state )
	{
		if ( state is not { ValueKind: JsonValueKind.Object } element )
			return (null, null);

		try
		{
			var saved = element.Deserialize<SavedTerminal>();
			return saved is { Command.Length: > 0 }
				? (new ShellProfile( saved.Name, saved.Command ), saved.Directory)
				: (null, saved?.Directory);
		}
		catch ( JsonException )
		{
			return (null, null);
		}
	}

	/// <summary>Holds the panel state saved across restarts.</summary>
	/// <param name="Name">The shell's display name.</param>
	/// <param name="Command">The shell's command line.</param>
	/// <param name="Directory">The directory to start in.</param>
	private sealed record SavedTerminal( string Name, string Command, string? Directory );
}
