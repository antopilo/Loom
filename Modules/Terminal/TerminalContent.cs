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

	/// <summary>Stores the directory the shell starts in.</summary>
	private readonly string _directory;

	/// <summary>Stores the terminal state the shell's output drives.</summary>
	private readonly TerminalEmulator _terminal;

	/// <summary>Collects output from the reader thread until the UI thread applies it.</summary>
	private readonly System.Text.StringBuilder _pending = new();

	/// <summary>Counts changes to the terminal so the grid rebuilds; the grid reads it.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the running shell, or null before it starts and after it exits.</summary>
	private ShellSession? _session;

	/// <summary>Tracks whether applying output is already queued on the UI thread.</summary>
	private bool _isFlushQueued;

	/// <summary>Tracks whether starting the shell is queued or done for the current run.</summary>
	private bool _isStarted;

	/// <summary>Tracks whether the shell ended and Enter restarts it.</summary>
	private bool _hasExited;

	/// <summary>Creates the panel for an explicit shell, a restored one, or the configured default, in its restored folder, else the working folder such as the open project's, else the user's home.</summary>
	internal TerminalContent( PanelContext context )
	{
		_context = context;
		var restored = ReadRestored( context.RestoredState );
		_profile = context.Input as ShellProfile
			?? restored.Profile
			?? ShellProfile.Resolve( context.App.Settings.Get( TerminalModule.Shell ) );
		_directory = restored.Directory is { } directory && Directory.Exists( directory ) ? directory
			: context.App.Services.Get<IWorkingFolder>()?.Current is { } current && Directory.Exists( current ) ? current
			: Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
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

	/// <summary>Starts the shell at the measured grid size.</summary>
	private void Start()
	{
		_hasExited = false;
		_grid.Focus();
		try
		{
			_session = ShellSession.Start( _profile.CommandLine, _directory, _terminal.Columns, _terminal.Rows, QueueOutput, code => _context.Post( () => UpdateExited( code ) ) );
		}
		catch ( Exception error ) when ( error is Win32Exception or System.Runtime.InteropServices.COMException )
		{
			_hasExited = true;
			_terminal.Feed( $"\x1b[0m\x1b[31mCould not start {_profile.CommandLine}: {error.Message}\x1b[0m\r\nChange the Terminal shell setting, then press Enter to try again.\r\n" );
			UpdateVersion();
		}
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
