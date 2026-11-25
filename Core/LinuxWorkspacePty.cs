using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Lokn.Core;

/// <summary>Runs one Linux terminal session and delivers output before its exit callback.</summary>
internal sealed partial class LinuxWorkspacePty : IWorkspacePty
{
	/// <summary>Defines the errno reported when a signal interrupts the operation.</summary>
	private const int Interrupted = 4;

	/// <summary>Defines the terminal driver request that sets the window size.</summary>
	private const nuint SetWindowSize = 0x5414;

	/// <summary>Defines the signal that ends the terminal session.</summary>
	private const int HangupSignal = 1;

	/// <summary>Defines the pidfd_open system call number, the same on every Linux architecture .NET runs on.</summary>
	private const nint PidFdOpenCall = 434;

	/// <summary>Stores the terminal master, input wakeup and child exit descriptors.</summary>
	private readonly int _master, _wakeup, _process;

	/// <summary>Stores the session leader to signal and reap.</summary>
	private readonly int _pid;

	/// <summary>Serializes input and resize against descriptor cleanup.</summary>
	private readonly Lock _gate = new();

	/// <summary>Queues input so the caller never waits for the child to read.</summary>
	private readonly Queue<byte[]> _writes = new();

	/// <summary>Receives decoded output on the terminal thread.</summary>
	private readonly Action<string> _onOutput;

	/// <summary>Receives the exit code on the terminal thread.</summary>
	private readonly Action<int> _onExited;

	/// <summary>Tracks whether disposal has requested a hangup.</summary>
	private bool _stopping;

	/// <summary>Tracks whether the terminal thread has closed the descriptors.</summary>
	private bool _closed;

	/// <summary>Starts the terminal thread with the open descriptors.</summary>
	private LinuxWorkspacePty( int master, int wakeup, int process, int pid, Action<string> output, Action<int> exited )
	{
		(_master, _wakeup, _process, _pid) = (master, wakeup, process, pid);
		(_onOutput, _onExited) = (output, exited);
		new Thread( Run ) { IsBackground = true, Name = "Terminal I/O" }.Start();
	}

	/// <summary>Starts a program with its own session and controlling terminal.</summary>
	internal static unsafe LinuxWorkspacePty Start( string file, IReadOnlyList<string> arguments, string? directory, int columns, int rows,
		IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited )
	{
		if ( !OperatingSystem.IsLinux() )
			throw new PlatformNotSupportedException( "Local Unix terminals require Linux." );

		ArgumentException.ThrowIfNullOrWhiteSpace( file );
		const int ReadWrite = 2, NoControllingTerminal = 0x100;
		var name = stackalloc byte[128];
		var master = CheckErrno( PosixOpenPt( ReadWrite | NoControllingTerminal ), "posix_openpt" );
		var slave = -1;
		var wakeup = -1;
		var process = -1;
		var pid = 0;
		try
		{
			CheckErrno( GrantPt( master ), "grantpt" );
			CheckErrno( UnlockPt( master ), "unlockpt" );
			Check( PtsName( master, name, 128 ), "ptsname_r" );
			slave = CheckErrno( Open( name, ReadWrite | NoControllingTerminal ), "open" );
			Ioctl( master, SetWindowSize, GetSize( columns, rows ) );
			const int SetDescriptorFlags = 2, CloseOnExec = 1, SetStatusFlags = 4, NonBlocking = 0x800, EventCloseOnExec = 0x80000;
			CheckErrno( Fcntl( master, SetDescriptorFlags, CloseOnExec ), "fcntl" );
			CheckErrno( Fcntl( slave, SetDescriptorFlags, CloseOnExec ), "fcntl" );
			CheckErrno( Fcntl( master, SetStatusFlags, NonBlocking ), "fcntl" );
			wakeup = CheckErrno( EventFd( 0, EventCloseOnExec | NonBlocking ), "eventfd" );
			pid = Spawn( file, arguments, directory, environment, master, slave, name );
			process = CheckErrno( (int)PidFdOpen( PidFdOpenCall, pid, 0 ), "pidfd_open" );
			return new( master, wakeup, process, pid, output, exited );
		}
		catch
		{
			if ( pid != 0 )
				HangUp( pid );
			Close( master );
			if ( process >= 0 )
				Close( process );
			if ( wakeup >= 0 )
				Close( wakeup );
			if ( pid != 0 )
				_ = Task.Run( () => Reap( pid ) );
			throw;
		}
		finally
		{
			if ( slave >= 0 )
				Close( slave );
		}
	}

	/// <summary>Queues text as UTF-8 for the running program.</summary>
	public void Write( string text )
	{
		lock ( _gate )
		{
			if ( _closed || _stopping || text.Length == 0 )
				return;

			_writes.Enqueue( Encoding.UTF8.GetBytes( text ) );
			Wake();
		}
	}

	/// <summary>Changes the terminal size and notifies the foreground process group.</summary>
	public void Resize( int columns, int rows )
	{
		lock ( _gate )
		{
			if ( !_closed && !_stopping )
				Ioctl( _master, SetWindowSize, GetSize( columns, rows ) );
		}
	}

	/// <summary>Requests a hangup and wakes the terminal thread without waiting.</summary>
	public void Dispose()
	{
		lock ( _gate )
		{
			if ( _closed || _stopping )
				return;

			_stopping = true;
			HangUp( _pid );
			Wake();
		}
	}

	/// <summary>Signals the terminal thread while the descriptor is still open.</summary>
	private unsafe void Wake()
	{
		ulong increment = 1;
		while ( WriteFd( _wakeup, &increment, sizeof( ulong ) ) < 0 && Marshal.GetLastPInvokeError() == Interrupted ) { }
	}

	/// <summary>Transfers terminal data until the child exits or the session is disposed.</summary>
	private unsafe void Run()
	{
		var descriptors = stackalloc PollDescriptor[3] { new( _master, PollEvents.Input ), new( _wakeup, PollEvents.Input ), new( _process, PollEvents.Input ) };
		var bytes = new byte[64 * 1024];
		var characters = new char[Encoding.UTF8.GetMaxCharCount( bytes.Length )];
		var decoder = Encoding.UTF8.GetDecoder();
		byte[]? pending = null;
		var offset = 0;
		try
		{
			while ( true )
			{
				descriptors[0].Events = PollEvents.Input | (pending is not null ? PollEvents.Output : 0);
				if ( Poll( descriptors, 3, -1 ) < 0 )
				{
					if ( Marshal.GetLastPInvokeError() == Interrupted )
						continue;
					break;
				}

				var wake = descriptors[1].Ready != 0;
				if ( wake )
				{
					ulong value;
					ReadFd( _wakeup, &value, sizeof( ulong ) );
				}

				bool stopping;
				lock ( _gate )
					stopping = _stopping;
				var finished = stopping || descriptors[2].Ready != 0;
				var events = descriptors[0].Ready;
				if ( finished || (events & (PollEvents.Input | PollEvents.End)) != 0 )
				{
					var drain = !stopping && (finished || (events & PollEvents.End) != 0);
					fixed ( byte* buffer = bytes )
					{
						while ( true )
						{
							var count = (int)ReadFd( _master, buffer, (nuint)bytes.Length );
							if ( count < 0 && Marshal.GetLastPInvokeError() == Interrupted )
								continue;
							if ( count <= 0 )
								break;

							var length = decoder.GetChars( bytes, 0, count, characters, 0, flush: false );
							if ( length > 0 )
								_onOutput( new string( characters, 0, length ) );
							if ( !drain || Volatile.Read( ref _stopping ) )
								break;
						}
					}
				}

				if ( finished )
					break;

				if ( (events & PollEvents.End) != 0 )
					descriptors[0].Fd = -1;
				else if ( descriptors[0].Fd >= 0 && (wake || (events & PollEvents.Output) != 0) )
					WritePending( ref pending, ref offset );
			}

			var tail = decoder.GetChars( [], 0, 0, characters, 0, flush: true );
			if ( tail > 0 )
				_onOutput( new string( characters, 0, tail ) );
		}
		finally
		{
			lock ( _gate )
			{
				if ( !_stopping && descriptors[2].Ready == 0 )
					HangUp( _pid );
				_closed = true;
				_writes.Clear();
				Close( _master );
				Close( _wakeup );
				Close( _process );
			}
			_onExited( Reap( _pid ) );
		}
	}

	/// <summary>Writes queued bytes in order until the terminal input buffer is full.</summary>
	private unsafe void WritePending( ref byte[]? pending, ref int offset )
	{
		while ( true )
		{
			if ( pending is null )
			{
				lock ( _gate )
				{
					if ( _stopping || !_writes.TryDequeue( out pending ) )
						return;
				}
				offset = 0;
			}

			fixed ( byte* data = pending )
			{
				var count = (int)WriteFd( _master, data + offset, (nuint)(pending.Length - offset) );
				if ( count < 0 && Marshal.GetLastPInvokeError() == Interrupted )
					continue;
				if ( count <= 0 )
					return;
				offset += count;
			}

			if ( offset == pending.Length )
				pending = null;
		}
	}

	/// <summary>Sends SIGHUP to the session leader before reaping it.</summary>
	private static void HangUp( int pid ) => Kill( pid, HangupSignal );

	/// <summary>Reaps a child and converts its wait status to a shell exit code.</summary>
	private static int Reap( int pid )
	{
		int result, status;
		do
			result = WaitPid( pid, out status, 0 );
		while ( result < 0 && Marshal.GetLastPInvokeError() == Interrupted );

		var signal = status & 0x7f;
		return result < 0 ? -1 : signal == 0 ? (status >> 8) & 0xff : 128 + signal;
	}

	/// <summary>Spawns a session leader that acquires its controlling terminal on open.</summary>
	private static unsafe int Spawn( string file, IReadOnlyList<string> arguments, string? directory, IReadOnlyDictionary<string, string>? extra,
		int master, int slave, byte* name )
	{
		// glibc reserves 336 bytes for attributes and up to 80 for file actions on supported Linux architectures.
		var attributes = stackalloc nint[336 / sizeof( nint )];
		var actions = stackalloc nint[80 / sizeof( nint )];
		Check( SpawnAttributesInit( attributes ), "posix_spawnattr_init" );
		try
		{
			const short SetSession = 0x80;
			Check( SpawnAttributesSetFlags( attributes, SetSession ), "posix_spawnattr_setflags" );
			Check( SpawnActionsInit( actions ), "posix_spawn_file_actions_init" );
			try
			{
				Check( SpawnActionsClose( actions, master ), "posix_spawn_file_actions_addclose" );
				Check( SpawnActionsClose( actions, slave ), "posix_spawn_file_actions_addclose" );
				// Opening without O_NOCTTY in the new session makes the slave its controlling terminal.
				Check( SpawnActionsOpen( actions, 0, name, 2, 0 ), "posix_spawn_file_actions_addopen" );
				Check( SpawnActionsDup( actions, 0, 1 ), "posix_spawn_file_actions_adddup2" );
				Check( SpawnActionsDup( actions, 0, 2 ), "posix_spawn_file_actions_adddup2" );
				if ( directory is not null )
					Check( SpawnActionsChdir( actions, directory ), "posix_spawn_file_actions_addchdir_np" );

				var args = new string[arguments.Count + 1];
				args[0] = file;
				for ( var index = 0; index < arguments.Count; index++ )
					args[index + 1] = arguments[index];
				var variables = Environment.GetEnvironmentVariables();
				variables["TERM"] = "xterm-256color";
				variables["COLORTERM"] = "truecolor";
				if ( extra is not null )
				{
					foreach ( var (key, value) in extra )
						variables[key] = value;
				}

				var environment = new string[variables.Count];
				var entryIndex = 0;
				foreach ( DictionaryEntry entry in variables )
					environment[entryIndex++] = $"{entry.Key}={entry.Value}";

				fixed ( byte* argBytes = new byte[GetVectorSize( args )] )
				fixed ( byte* envBytes = new byte[GetVectorSize( environment )] )
				fixed ( byte** argv = new byte*[args.Length + 1] )
				fixed ( byte** envp = new byte*[environment.Length + 1] )
				{
					EncodeVector( args, argBytes, argv );
					EncodeVector( environment, envBytes, envp );
					Check( PosixSpawn( out var pid, argv[0], actions, attributes, argv, envp ), "posix_spawnp" );
					return pid;
				}
			}
			finally
			{
				SpawnActionsDestroy( actions );
			}
		}
		finally
		{
			SpawnAttributesDestroy( attributes );
		}
	}

	/// <summary>Counts the UTF-8 bytes and null terminators for a native string vector.</summary>
	private static int GetVectorSize( string[] values )
	{
		var size = 0;
		foreach ( var value in values )
			size = checked(size + Encoding.UTF8.GetByteCount( value ) + 1);
		return size;
	}

	/// <summary>Writes UTF-8 strings into zeroed storage and records their addresses.</summary>
	private static unsafe void EncodeVector( string[] values, byte* storage, byte** pointers )
	{
		for ( var index = 0; index < values.Length; index++ )
		{
			var value = values[index];
			pointers[index] = storage;
			storage += Encoding.UTF8.GetBytes( value, new Span<byte>( storage, Encoding.UTF8.GetByteCount( value ) ) ) + 1;
		}
	}

	/// <summary>Clamps a cell size to the range accepted by the terminal driver.</summary>
	private static WindowSize GetSize( int columns, int rows ) => new( (ushort)Math.Clamp( rows, 1, ushort.MaxValue ), (ushort)Math.Clamp( columns, 1, ushort.MaxValue ), 0, 0 );

	/// <summary>Checks a native return value that reports failure through errno.</summary>
	private static int CheckErrno( int result, string operation )
	{
		if ( result < 0 )
			Check( Marshal.GetLastPInvokeError(), operation );
		return result;
	}

	/// <summary>Reports a native error through the workspace start contract.</summary>
	private static void Check( int error, string operation )
	{
		if ( error != 0 )
			throw new Win32Exception( error, $"{operation}: {new Win32Exception( error ).Message}" );
	}

	/// <summary>Defines the size passed to the Linux terminal driver.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private readonly record struct WindowSize( ushort Rows, ushort Columns, ushort PixelWidth, ushort PixelHeight );

	/// <summary>Defines one entry in the native poll array.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct PollDescriptor( int fd, PollEvents events )
	{
		/// <summary>Names the file descriptor, or -1 to skip it.</summary>
		public int Fd = fd;

		/// <summary>Names the requested events.</summary>
		public PollEvents Events = events;

		/// <summary>Receives the reported events.</summary>
		public PollEvents Ready = 0;
	}

	/// <summary>Names Linux poll event flags.</summary>
	[Flags]
	private enum PollEvents : short
	{
		/// <summary>Reports readable input.</summary>
		Input = 0x01,

		/// <summary>Reports writable output.</summary>
		Output = 0x04,

		/// <summary>Reports error, hangup or an invalid descriptor.</summary>
		End = 0x08 | 0x10 | 0x20,
	}

	/// <summary>Opens a new PTY master.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_openpt", SetLastError = true )]
	private static partial int PosixOpenPt( int flags );

	/// <summary>Gives the user ownership of the PTY slave.</summary>
	[LibraryImport( "libc", EntryPoint = "grantpt", SetLastError = true )]
	private static partial int GrantPt( int master );

	/// <summary>Allows the PTY slave to be opened.</summary>
	[LibraryImport( "libc", EntryPoint = "unlockpt", SetLastError = true )]
	private static partial int UnlockPt( int master );

	/// <summary>Writes the PTY slave path and returns an error number, or 0 on success.</summary>
	[LibraryImport( "libc", EntryPoint = "ptsname_r" )]
	private static unsafe partial int PtsName( int master, byte* name, nuint length );

	/// <summary>Opens a path.</summary>
	[LibraryImport( "libc", EntryPoint = "open", SetLastError = true )]
	private static unsafe partial int Open( byte* path, int flags );

	/// <summary>Sets flags on a file descriptor.</summary>
	[LibraryImport( "libc", EntryPoint = "fcntl", SetLastError = true )]
	private static partial int Fcntl( int fd, int command, int flags );

	/// <summary>Sends a control request to the terminal driver.</summary>
	[LibraryImport( "libc", EntryPoint = "ioctl" )]
	private static partial int Ioctl( int fd, nuint request, in WindowSize size );

	/// <summary>Creates a pollable wakeup counter.</summary>
	[LibraryImport( "libc", EntryPoint = "eventfd", SetLastError = true )]
	private static partial int EventFd( uint value, int flags );

	/// <summary>Opens a pollable descriptor for child exit through <c>syscall</c>, since glibc exports <c>pidfd_open</c> only from 2.36.</summary>
	[LibraryImport( "libc", EntryPoint = "syscall", SetLastError = true )]
	private static partial nint PidFdOpen( nint number, int pid, uint flags );

	/// <summary>Waits for descriptor events.</summary>
	[LibraryImport( "libc", EntryPoint = "poll", SetLastError = true )]
	private static unsafe partial int Poll( PollDescriptor* descriptors, nuint count, int timeout );

	/// <summary>Reads available bytes from a descriptor.</summary>
	[LibraryImport( "libc", EntryPoint = "read", SetLastError = true )]
	private static unsafe partial nint ReadFd( int fd, void* buffer, nuint count );

	/// <summary>Writes available bytes to a descriptor.</summary>
	[LibraryImport( "libc", EntryPoint = "write", SetLastError = true )]
	private static unsafe partial nint WriteFd( int fd, void* buffer, nuint count );

	/// <summary>Closes a descriptor.</summary>
	[LibraryImport( "libc", EntryPoint = "close" )]
	private static partial int Close( int fd );

	/// <summary>Sends a signal to a process.</summary>
	[LibraryImport( "libc", EntryPoint = "kill" )]
	private static partial int Kill( int pid, int signal );

	/// <summary>Collects a child's wait status.</summary>
	[LibraryImport( "libc", EntryPoint = "waitpid", SetLastError = true )]
	private static partial int WaitPid( int pid, out int status, int options );

	/// <summary>Initializes the spawn attributes.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawnattr_init" )]
	private static unsafe partial int SpawnAttributesInit( nint* attributes );

	/// <summary>Frees the spawn attributes.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawnattr_destroy" )]
	private static unsafe partial int SpawnAttributesDestroy( nint* attributes );

	/// <summary>Sets the spawn flags.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawnattr_setflags" )]
	private static unsafe partial int SpawnAttributesSetFlags( nint* attributes, short flags );

	/// <summary>Initializes a list of child actions.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawn_file_actions_init" )]
	private static unsafe partial int SpawnActionsInit( nint* actions );

	/// <summary>Frees a list of child actions.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawn_file_actions_destroy" )]
	private static unsafe partial int SpawnActionsDestroy( nint* actions );

	/// <summary>Closes a descriptor in the child before opening the controlling terminal.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawn_file_actions_addclose" )]
	private static unsafe partial int SpawnActionsClose( nint* actions, int fd );

	/// <summary>Adds a child action to open a path on a descriptor.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawn_file_actions_addopen" )]
	private static unsafe partial int SpawnActionsOpen( nint* actions, int fd, byte* path, int flags, uint mode );

	/// <summary>Adds a child action to duplicate a descriptor.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawn_file_actions_adddup2" )]
	private static unsafe partial int SpawnActionsDup( nint* actions, int from, int to );

	/// <summary>Adds a child action to set the working directory.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8 )]
	private static unsafe partial int SpawnActionsChdir( nint* actions, string path );

	/// <summary>Starts the program directly or searches PATH for its name.</summary>
	[LibraryImport( "libc", EntryPoint = "posix_spawnp" )]
	private static unsafe partial int PosixSpawn( out int pid, byte* file, nint* actions, nint* attributes, byte** argv, byte** envp );
}
