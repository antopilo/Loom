using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Lokn.Modules.Terminal;

/// <summary>Runs one command line attached to a Windows pseudoconsole and streams its terminal output.</summary>
/// <remarks>
/// Output and the exit notification arrive on a background thread, output first: the exit code is reported only
/// after the pseudoconsole has flushed and closed its output pipe.
/// </remarks>
internal sealed partial class ShellSession : IDisposable
{
	/// <summary>Defines the attribute that attaches a new process to a pseudoconsole.</summary>
	private const nint PseudoConsoleAttribute = 0x00020016;

	/// <summary>Defines the process creation flag for an extended startup structure.</summary>
	private const uint ExtendedStartupInfoPresent = 0x00080000;

	/// <summary>Defines the process creation flag for a UTF-16 environment block.</summary>
	private const uint CreateUnicodeEnvironment = 0x00000400;

	/// <summary>Defines the startup flag that makes the child use the standard handles given, here none.</summary>
	/// <remarks>Without it a child of a process whose own standard handles are pipes inherits those instead of the pseudoconsole.</remarks>
	private const int UseStandardHandles = 0x00000100;

	/// <summary>Defines the exit code Windows reports while a process is still running.</summary>
	private const uint StillActive = 259;

	/// <summary>Defines the size of one output read in bytes.</summary>
	private const int ReadSize = 64 * 1024;

	/// <summary>Stores the pseudoconsole handle until it is closed.</summary>
	private readonly nint _console;

	/// <summary>Stores the attached process handle for its exit code.</summary>
	private readonly SafeProcessHandle _process;

	/// <summary>Stores the pipe end that receives the pseudoconsole's VT output.</summary>
	private readonly FileStream _output;

	/// <summary>Stores the pipe end that sends keyboard input to the pseudoconsole.</summary>
	private readonly FileStream _input;

	/// <summary>Queues input so a child that stops reading never blocks the UI thread.</summary>
	private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>( new() { SingleReader = true } );

	/// <summary>Receives decoded output on the reader thread.</summary>
	private readonly Action<string> _onOutput;

	/// <summary>Receives the exit code once all output is delivered.</summary>
	private readonly Action<int> _onExited;

	/// <summary>Serializes resizing against closing the pseudoconsole.</summary>
	private readonly Lock _gate = new();

	/// <summary>Stores the wait that closes the pseudoconsole once the process exits.</summary>
	private readonly RegisteredWaitHandle _exitWait;

	/// <summary>Tracks whether the pseudoconsole handle has been closed.</summary>
	private bool _isClosed;

	/// <summary>Gets the started process identifier.</summary>
	internal int ProcessId
	{
		get;
	}

	/// <summary>Takes ownership of a started pseudoconsole, its pipes and process, and starts streaming.</summary>
	private ShellSession( nint console, SafeProcessHandle process, int processId, SafeFileHandle output, SafeFileHandle input, Action<string> onOutput, Action<int> onExited )
	{
		_console = console;
		_process = process;
		ProcessId = processId;
		_output = new FileStream( output, FileAccess.Read, 0 );
		_input = new FileStream( input, FileAccess.Write, 0 );
		_onOutput = onOutput;
		_onExited = onExited;
		_ = WriteAllAsync();
		new Thread( ReadAll )
		{
			IsBackground = true,
			Name = "Terminal output",
		}.Start();
		_exitWait = ThreadPool.RegisterWaitForSingleObject( new ProcessWait( process ), ( _, _ ) => CloseConsole(), null, Timeout.Infinite, executeOnlyOnce: true );
	}

	/// <summary>Starts a command line in a new pseudoconsole of the given size.</summary>
	/// <param name="commandLine">The full command line; the executable is found the way CreateProcess finds it, including PATH.</param>
	/// <param name="directory">The starting directory, or null for the application's.</param>
	/// <param name="columns">The initial width in cells.</param>
	/// <param name="rows">The initial height in cells.</param>
	/// <param name="onOutput">Receives decoded VT output on a background thread.</param>
	/// <param name="onExited">Receives the exit code on a background thread after the last output.</param>
	/// <exception cref="System.ComponentModel.Win32Exception">The pseudoconsole or the process could not be created.</exception>
	internal static ShellSession Start( string commandLine, string? directory, int columns, int rows, Action<string> onOutput, Action<int> onExited )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( commandLine );
		if ( !CreatePipe( out var inputRead, out var inputWrite, 0, 0 ) )
			throw new System.ComponentModel.Win32Exception();

		if ( !CreatePipe( out var outputRead, out var outputWrite, 0, 0 ) )
		{
			inputRead.Dispose();
			inputWrite.Dispose();
			throw new System.ComponentModel.Win32Exception();
		}

		nint console = 0;
		var attributes = nint.Zero;
		try
		{
			Marshal.ThrowExceptionForHR( CreatePseudoConsole( ToCoord( columns, rows ), inputRead, outputWrite, 0, out console ) );

			// The pseudoconsole holds its own copies; ours would keep the output pipe open after it closes.
			inputRead.Dispose();
			outputWrite.Dispose();

			nint size = 0;
			InitializeProcThreadAttributeList( 0, 1, 0, ref size );
			attributes = Marshal.AllocHGlobal( size );
			if ( !InitializeProcThreadAttributeList( attributes, 1, 0, ref size )
				|| !UpdateProcThreadAttribute( attributes, 0, PseudoConsoleAttribute, console, nint.Size, 0, 0 ) )
				throw new System.ComponentModel.Win32Exception();

			var startup = new StartupInfoEx
			{
				StartupInfo = new StartupInfo
				{
					Size = Marshal.SizeOf<StartupInfoEx>(),
					Flags = UseStandardHandles,
				},
				AttributeList = attributes,
			};
			var information = CreateProcess( commandLine, directory, ref startup );
			CloseHandle( information.Thread );
			return new ShellSession( console, new SafeProcessHandle( information.Process, ownsHandle: true ), information.ProcessId, outputRead, inputWrite, onOutput, onExited );
		}
		catch
		{
			if ( console != 0 )
				ClosePseudoConsole( console );

			inputRead.Dispose();
			inputWrite.Dispose();
			outputRead.Dispose();
			outputWrite.Dispose();
			throw;
		}
		finally
		{
			if ( attributes != 0 )
			{
				DeleteProcThreadAttributeList( attributes );
				Marshal.FreeHGlobal( attributes );
			}
		}
	}

	/// <summary>Queues UTF-8 input for the attached process.</summary>
	internal void Write( string text )
	{
		if ( text.Length > 0 )
			_writes.Writer.TryWrite( Encoding.UTF8.GetBytes( text ) );
	}

	/// <summary>Resizes the pseudoconsole; the shell repaints for the new size.</summary>
	internal void Resize( int columns, int rows )
	{
		lock ( _gate )
		{
			if ( !_isClosed )
				ResizePseudoConsole( _console, ToCoord( columns, rows ) );
		}
	}

	/// <summary>Closes the pseudoconsole, which ends its processes, without blocking the caller.</summary>
	public void Dispose()
	{
		_writes.Writer.TryComplete();
		_ = Task.Run( CloseConsole );
	}

	/// <summary>Closes the pseudoconsole once; it flushes remaining output and then closes the output pipe.</summary>
	private void CloseConsole()
	{
		lock ( _gate )
		{
			if ( _isClosed )
				return;

			_isClosed = true;
		}

		_exitWait.Unregister( null );
		ClosePseudoConsole( _console );
	}

	/// <summary>Reads and decodes output until the pipe closes, then reports the exit code.</summary>
	private void ReadAll()
	{
		var decoder = Encoding.UTF8.GetDecoder();
		var bytes = new byte[ReadSize];
		var characters = new char[Encoding.UTF8.GetMaxCharCount( ReadSize )];
		try
		{
			while ( true )
			{
				var count = _output.Read( bytes, 0, bytes.Length );
				if ( count == 0 )
					break;

				var length = decoder.GetChars( bytes, 0, count, characters, 0, flush: false );
				if ( length > 0 )
					_onOutput( new string( characters, 0, length ) );
			}
		}
		catch ( IOException )
		{
			// A broken pipe is the normal end of a pseudoconsole's output.
		}
		finally
		{
			_output.Dispose();
		}

		_writes.Writer.TryComplete();
		var exitCode = GetExitCodeProcess( _process, out var code ) && code != StillActive ? unchecked((int)code) : -1;
		_process.Dispose();
		_onExited( exitCode );
	}

	/// <summary>Writes queued input in order until the session ends.</summary>
	private async Task WriteAllAsync()
	{
		try
		{
			await foreach ( var bytes in _writes.Reader.ReadAllAsync() )
			{
				_input.Write( bytes );
				_input.Flush();
			}
		}
		catch ( IOException )
		{
			// The pseudoconsole closed; later input has nowhere to go.
		}
		catch ( ObjectDisposedException )
		{
		}
		finally
		{
			_input.Dispose();
		}
	}

	/// <summary>Creates the process with a pseudoconsole attribute list and the inherited environment.</summary>
	private static unsafe ProcessInformation CreateProcess( string commandLine, string? directory, ref StartupInfoEx startup )
	{
		// CreateProcessW may write into the command line buffer, so it must be a private copy.
		var command = (commandLine + '\0').ToCharArray();
		var environment = BuildEnvironment();
		fixed ( char* commandPointer = command )
		fixed ( char* environmentPointer = environment )
		{
			if ( !CreateProcessW( null, commandPointer, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, (nint)environmentPointer, directory, ref startup, out var information ) )
				throw new System.ComponentModel.Win32Exception();

			return information;
		}
	}

	/// <summary>Builds a sorted UTF-16 environment block from this process's variables plus terminal identification.</summary>
	private static char[] BuildEnvironment()
	{
		var variables = new SortedDictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( DictionaryEntry entry in Environment.GetEnvironmentVariables() )
		{
			if ( entry.Key is string key && entry.Value is string value )
				variables[key] = value;
		}

		// Programs choose 24-bit color and terminal-specific behavior from these.
		variables["COLORTERM"] = "truecolor";
		variables["TERM_PROGRAM"] = "Lokn";

		var block = new StringBuilder();
		foreach ( var (key, value) in variables )
			block.Append( key ).Append( '=' ).Append( value ).Append( '\0' );

		return block.Append( '\0' ).ToString().ToCharArray();
	}

	/// <summary>Clamps a cell size to the console coordinate range.</summary>
	private static Coord ToCoord( int columns, int rows ) => new( (short)Math.Clamp( columns, 1, short.MaxValue ), (short)Math.Clamp( rows, 1, short.MaxValue ) );

	/// <summary>Waits on a process handle owned elsewhere.</summary>
	private sealed class ProcessWait : WaitHandle
	{
		/// <summary>Wraps the process handle without taking ownership.</summary>
		internal ProcessWait( SafeProcessHandle process ) => SafeWaitHandle = new SafeWaitHandle( process.DangerousGetHandle(), ownsHandle: false );
	}

	/// <summary>Mirrors the Win32 COORD structure.</summary>
	/// <param name="X">The column count.</param>
	/// <param name="Y">The row count.</param>
	[StructLayout( LayoutKind.Sequential )]
	private readonly record struct Coord( short X, short Y );

	/// <summary>Mirrors the Win32 STARTUPINFOW structure.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct StartupInfo
	{
		/// <summary>The structure size in bytes.</summary>
		public int Size;

		/// <summary>Reserved.</summary>
		public nint Reserved;

		/// <summary>The desktop name, unused.</summary>
		public nint Desktop;

		/// <summary>The console title, unused.</summary>
		public nint Title;

		/// <summary>The window left edge, unused.</summary>
		public int X;

		/// <summary>The window top edge, unused.</summary>
		public int Y;

		/// <summary>The window width, unused.</summary>
		public int XSize;

		/// <summary>The window height, unused.</summary>
		public int YSize;

		/// <summary>The console buffer width, unused.</summary>
		public int XCountChars;

		/// <summary>The console buffer height, unused.</summary>
		public int YCountChars;

		/// <summary>The console colors, unused.</summary>
		public int FillAttribute;

		/// <summary>The fields that apply.</summary>
		public int Flags;

		/// <summary>The show command, unused.</summary>
		public short ShowWindow;

		/// <summary>Reserved.</summary>
		public short Reserved2Size;

		/// <summary>Reserved.</summary>
		public nint Reserved2;

		/// <summary>The standard input handle, none.</summary>
		public nint StandardInput;

		/// <summary>The standard output handle, none.</summary>
		public nint StandardOutput;

		/// <summary>The standard error handle, none.</summary>
		public nint StandardError;
	}

	/// <summary>Mirrors the Win32 STARTUPINFOEXW structure.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct StartupInfoEx
	{
		/// <summary>The basic startup fields.</summary>
		public StartupInfo StartupInfo;

		/// <summary>The process attribute list carrying the pseudoconsole.</summary>
		public nint AttributeList;
	}

	/// <summary>Mirrors the Win32 PROCESS_INFORMATION structure.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct ProcessInformation
	{
		/// <summary>The process handle.</summary>
		public nint Process;

		/// <summary>The primary thread handle.</summary>
		public nint Thread;

		/// <summary>The process identifier.</summary>
		public int ProcessId;

		/// <summary>The primary thread identifier.</summary>
		public int ThreadId;
	}

	/// <summary>Creates an anonymous pipe.</summary>
	[LibraryImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool CreatePipe( out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size );

	/// <summary>Creates a pseudoconsole over two pipes.</summary>
	[LibraryImport( "kernel32.dll" )]
	private static partial int CreatePseudoConsole( Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console );

	/// <summary>Resizes a pseudoconsole.</summary>
	[LibraryImport( "kernel32.dll" )]
	private static partial int ResizePseudoConsole( nint console, Coord size );

	/// <summary>Closes a pseudoconsole and ends its attached processes.</summary>
	[LibraryImport( "kernel32.dll" )]
	private static partial void ClosePseudoConsole( nint console );

	/// <summary>Sizes or initializes a process attribute list.</summary>
	[LibraryImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool InitializeProcThreadAttributeList( nint list, int count, int flags, ref nint size );

	/// <summary>Sets one process attribute.</summary>
	[LibraryImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool UpdateProcThreadAttribute( nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize );

	/// <summary>Releases a process attribute list.</summary>
	[LibraryImport( "kernel32.dll" )]
	private static partial void DeleteProcThreadAttributeList( nint list );

	/// <summary>Creates a process.</summary>
	[LibraryImport( "kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16 )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static unsafe partial bool CreateProcessW( string? application, char* commandLine, nint processAttributes, nint threadAttributes, [MarshalAs( UnmanagedType.Bool )] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information );

	/// <summary>Reads a process exit code.</summary>
	[LibraryImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool GetExitCodeProcess( SafeProcessHandle process, out uint exitCode );

	/// <summary>Closes a kernel handle.</summary>
	[LibraryImport( "kernel32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool CloseHandle( nint handle );
}
