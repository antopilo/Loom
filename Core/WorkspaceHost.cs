using System.Diagnostics;
using System.Text;

namespace Lokn.Core;

/// <summary>Names what happened to a path a <see cref="IWorkspaceHost.Watch"/> follows.</summary>
/// <remarks>Maps to the <c>kind</c> of the <c>fs.changed</c> push in docs/remote.md, in lower case.</remarks>
public enum WorkspaceChange
{
	/// <summary>A file or folder appeared.</summary>
	Created,

	/// <summary>A file's content, size or write time changed.</summary>
	Changed,

	/// <summary>A file or folder went away.</summary>
	Deleted,

	/// <summary>A file or folder got its name; the path is the new one.</summary>
	Renamed,

	/// <summary>Changes were lost, as when the watcher's buffer overflowed; the path is the watched folder, and everything under it may have changed.</summary>
	Lost,
}

/// <summary>Holds what one finished process reported.</summary>
/// <remarks>Maps to the answer of <c>proc.run</c>: <c>{ code, out, err }</c>.</remarks>
/// <param name="Code">The exit code; zero is success.</param>
/// <param name="Output">The standard output, decoded as UTF-8.</param>
/// <param name="Error">The standard error, decoded as UTF-8.</param>
public readonly record struct WorkspaceProcessResult( int Code, string Output, string Error );

/// <summary>Describes one path on the workspace's disk.</summary>
/// <remarks>Maps to the answer of <c>fs.stat</c>: <c>{ exists, dir, size, mtime }</c>.</remarks>
/// <param name="Exists">Whether a file or folder is there.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Size">The file's length in bytes; zero for a folder or a missing path.</param>
/// <param name="Modified">The last write time in UTC; <see cref="DateTime.MinValue"/> for a missing path.</param>
public readonly record struct WorkspaceStat( bool Exists, bool IsDirectory, long Size, DateTime Modified )
{
	/// <summary>Gets the answer for a path where nothing is.</summary>
	public static WorkspaceStat Missing => new( false, false, 0, DateTime.MinValue );

	/// <summary>Gets whether a file, not a folder, is there.</summary>
	public bool IsFile => Exists && !IsDirectory;
}

/// <summary>Describes one entry of a folder.</summary>
/// <remarks>Maps to one item of the <c>entries</c> of <c>fs.list</c>: <c>{ name, dir, size, mtime }</c>.</remarks>
/// <param name="Name">The entry's name inside the folder.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Size">The file's length in bytes; zero for a folder.</param>
/// <param name="Modified">The last write time in UTC.</param>
public readonly record struct WorkspaceEntry( string Name, bool IsDirectory, long Size, DateTime Modified );

/// <summary>Reaches the disk, processes and terminals the user's work lives on: this PC, or a paired host over the remote protocol.</summary>
/// <remarks>
/// Git, Files, Editor and Terminal reach a host only through <see cref="PanelContext.Workspace"/> (their panel's host) or <see cref="LoknHost.Workspace"/>;
/// a host that is not connected has an <see cref="OfflineWorkspaceHost"/>. Each member maps to one workspace frame of
/// docs/remote.md, named in its remarks. Paths are the host's full paths. Tasks may finish before they return, as the local host's file
/// operations do, so a caller on the UI thread can use <see cref="WorkspaceHostExtensions.WhenDone{TTask}"/> to go on at once when they did.
/// File operations fail with <see cref="IOException"/> (or a subclass such as <see cref="FileNotFoundException"/>) or
/// <see cref="UnauthorizedAccessException"/>.
/// </remarks>
public interface IWorkspaceHost
{
	/// <summary>Runs a program without a shell and waits for it; cancelling kills it with its process tree.</summary>
	/// <remarks>Maps to <c>proc.run { file, args, cwd, input?, env? }</c>.</remarks>
	/// <param name="file">The program, a full path or a name found on the host's PATH.</param>
	/// <param name="arguments">The arguments, each passed as one argument.</param>
	/// <param name="directory">The folder the program runs in.</param>
	/// <param name="input">Text written to standard input as UTF-8 and then closed, or null for no standard input.</param>
	/// <param name="environment">Variables set on top of the host's own, or null.</param>
	/// <param name="cancellationToken">Cancels the run and kills the program.</param>
	/// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
	Task<WorkspaceProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken );

	/// <summary>Describes a path; a missing path is not an error.</summary>
	/// <remarks>Maps to <c>fs.stat { path }</c>.</remarks>
	Task<WorkspaceStat> StatAsync( string path, CancellationToken cancellationToken );

	/// <summary>Lists a folder's entries, in no particular order.</summary>
	/// <remarks>Maps to <c>fs.list { path }</c>.</remarks>
	Task<IReadOnlyList<WorkspaceEntry>> ListAsync( string path, CancellationToken cancellationToken );

	/// <summary>Reads a whole file, even one another program has open for writing.</summary>
	/// <remarks>Maps to <c>fs.read { path }</c>.</remarks>
	Task<byte[]> ReadAsync( string path, CancellationToken cancellationToken );

	/// <summary>Creates or replaces a file with the given bytes.</summary>
	/// <remarks>Maps to <c>fs.write { path, data }</c>.</remarks>
	Task WriteAsync( string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken );

	/// <summary>Deletes a file, read-only or not, or a folder with everything in it; a missing path is not an error.</summary>
	/// <remarks>Maps to <c>fs.delete { path }</c>.</remarks>
	Task DeleteAsync( string path, CancellationToken cancellationToken );

	/// <summary>Moves or renames a file or a folder; a file replaces a file already at the destination.</summary>
	/// <remarks>Maps to <c>fs.move { path, to }</c>.</remarks>
	Task MoveAsync( string path, string to, CancellationToken cancellationToken );

	/// <summary>Creates a folder and any missing parents; an existing folder is not an error.</summary>
	/// <remarks>Maps to <c>fs.mkdir { path }</c>.</remarks>
	Task CreateDirectoryAsync( string path, CancellationToken cancellationToken );

	/// <summary>Follows changes under a folder until the returned handle is disposed.</summary>
	/// <remarks>Maps to <c>fs.watch { path, recursive }</c>, its <c>fs.changed { watch, path, kind }</c> pushes, and <c>fs.unwatch { watch }</c> on dispose.</remarks>
	/// <param name="path">The folder.</param>
	/// <param name="recursive">Whether changes in subfolders count.</param>
	/// <param name="changed">Receives each change's full path and kind on a background thread.</param>
	/// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
	IDisposable Watch( string path, bool recursive, Action<string, WorkspaceChange> changed );

	/// <summary>Starts a program attached to a new pseudoterminal of the given size.</summary>
	/// <remarks>Maps to <c>pty.open { file, args, cwd, cols, rows, env? }</c>; output arrives as <c>pty.out</c>, the exit as <c>pty.exit</c>.</remarks>
	/// <param name="file">The program, a full path or a name found on the host's PATH.</param>
	/// <param name="arguments">The arguments, each passed as one argument.</param>
	/// <param name="directory">The starting folder, or null for the host's own.</param>
	/// <param name="columns">The initial width in cells.</param>
	/// <param name="rows">The initial height in cells.</param>
	/// <param name="environment">Variables set on top of the host's own, or null.</param>
	/// <param name="output">Receives decoded terminal output on a background thread.</param>
	/// <param name="exited">Receives the exit code on a background thread after the last output; -1 when it is unknown.</param>
	/// <exception cref="System.ComponentModel.Win32Exception">The pseudoterminal or the program could not be created.</exception>
	IWorkspacePty OpenPty( string file, IReadOnlyList<string> arguments, string? directory, int columns, int rows, IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited );
}

/// <summary>Drives one program running in a pseudoterminal from <see cref="IWorkspaceHost.OpenPty"/>; dispose it to end the program.</summary>
/// <remarks>Dispose maps to <c>pty.close { pty }</c>.</remarks>
public interface IWorkspacePty : IDisposable
{
	/// <summary>Sends text, as UTF-8, to the program's input without blocking.</summary>
	/// <remarks>Maps to <c>pty.in { pty, data }</c>.</remarks>
	void Write( string text );

	/// <summary>Resizes the pseudoterminal; the program repaints for the new size.</summary>
	/// <remarks>Maps to <c>pty.resize { pty, cols, rows }</c>.</remarks>
	void Resize( int columns, int rows );
}

/// <summary>Adds the questions callers ask most to <see cref="IWorkspaceHost"/>, and continues finished work on the UI thread.</summary>
public static class WorkspaceHostExtensions
{
	/// <summary>Reports whether a file, not a folder, is at a path.</summary>
	public static async Task<bool> FileExistsAsync( this IWorkspaceHost host, string path, CancellationToken cancellationToken ) =>
		(await host.StatAsync( path, cancellationToken ).ConfigureAwait( false )).IsFile;

	/// <summary>Reports whether a folder is at a path.</summary>
	public static async Task<bool> DirectoryExistsAsync( this IWorkspaceHost host, string path, CancellationToken cancellationToken ) =>
		(await host.StatAsync( path, cancellationToken ).ConfigureAwait( false )) is { Exists: true, IsDirectory: true };

	/// <summary>Runs work with a finished task: now when it already finished, as the local host's file operations do, else on the UI thread
	/// through <paramref name="post"/> once it finishes.</summary>
	/// <param name="task">The task, which may have failed or been cancelled; <paramref name="done"/> checks.</param>
	/// <param name="post">Posts to the UI thread, such as <see cref="PanelContext.Post"/>.</param>
	/// <param name="done">Receives the finished task.</param>
	public static void WhenDone<TTask>( this TTask task, Func<Action, bool> post, Action<TTask> done ) where TTask : Task
	{
		if ( task.IsCompleted )
		{
			done( task );
			return;
		}

		_ = task.ContinueWith( _ => post( () => done( task ) ), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default );
	}
}

/// <summary>Reaches this PC's disk, processes and pseudoconsoles directly.</summary>
/// <remarks>File operations run before they return and hand back a finished task, so callers on the UI thread keep their synchronous behavior.</remarks>
public sealed class LocalWorkspaceHost : IWorkspaceHost
{
	/// <summary>Gets the one local host; it keeps no state of its own.</summary>
	public static LocalWorkspaceHost Shared { get; } = new();

	/// <summary>Creates the host; use <see cref="Shared"/>.</summary>
	private LocalWorkspaceHost()
	{
	}

	/// <summary>Runs a program with redirected pipes and waits for it; cancelling kills it with its process tree.</summary>
	public async Task<WorkspaceProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken )
	{
		var start = new ProcessStartInfo( file )
		{
			WorkingDirectory = directory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = input is not null,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		if ( input is not null )
			start.StandardInputEncoding = new UTF8Encoding( false );

		if ( environment is not null )
		{
			foreach ( var (name, value) in environment )
				start.Environment[name] = value;
		}

		foreach ( var argument in arguments )
			start.ArgumentList.Add( argument );

		// The child owns native pipe handles; release them as soon as the short-lived command finishes.
		using var process = new Process
		{
			StartInfo = start,
		};
		process.Start();
		var output = process.StandardOutput.ReadToEndAsync( cancellationToken );
		var error = process.StandardError.ReadToEndAsync( cancellationToken );
		try
		{
			if ( input is not null )
			{
				await process.StandardInput.WriteAsync( input.AsMemory(), cancellationToken ).ConfigureAwait( false );
				process.StandardInput.Close();
			}

			await process.WaitForExitAsync( cancellationToken ).ConfigureAwait( false );
			var text = await output.ConfigureAwait( false );
			var diagnostic = await error.ConfigureAwait( false );
			return new( process.ExitCode, text, diagnostic );
		}
		catch ( OperationCanceledException )
		{
			// A closed panel or a newer read must not leave its command running.
			if ( !process.HasExited )
				process.Kill( entireProcessTree: true );

			throw;
		}
	}

	/// <summary>Describes a path from the file system.</summary>
	public Task<WorkspaceStat> StatAsync( string path, CancellationToken cancellationToken ) => Complete( cancellationToken, () =>
	{
		if ( Directory.Exists( path ) )
			return new WorkspaceStat( true, true, 0, Directory.GetLastWriteTimeUtc( path ) );

		var file = new FileInfo( path );
		return file.Exists ? new WorkspaceStat( true, false, file.Length, file.LastWriteTimeUtc ) : WorkspaceStat.Missing;
	} );

	/// <summary>Lists a folder's files and subfolders, hidden ones included.</summary>
	public Task<IReadOnlyList<WorkspaceEntry>> ListAsync( string path, CancellationToken cancellationToken ) => Complete<IReadOnlyList<WorkspaceEntry>>( cancellationToken, () =>
	[
		.. new DirectoryInfo( path ).EnumerateFileSystemInfos().Select( item => item is FileInfo file
			? new WorkspaceEntry( file.Name, false, file.Length, file.LastWriteTimeUtc )
			: new WorkspaceEntry( item.Name, true, 0, item.LastWriteTimeUtc ) ),
	] );

	/// <summary>Reads a whole file, sharing it with writers and deleters.</summary>
	public Task<byte[]> ReadAsync( string path, CancellationToken cancellationToken ) => Complete( cancellationToken, () =>
	{
		using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
		if ( stream.Length > Array.MaxLength )
			throw new IOException( $"The file '{path}' is too large to read." );

		var bytes = new byte[stream.Length];
		stream.ReadExactly( bytes );
		return bytes;
	} );

	/// <summary>Creates or truncates a file and writes the bytes.</summary>
	public Task WriteAsync( string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken ) => Complete( cancellationToken, () =>
	{
		using var stream = new FileStream( path, FileMode.Create, FileAccess.Write, FileShare.Read );
		stream.Write( data.Span );
		return true;
	} );

	/// <summary>Deletes a folder recursively, or a file after clearing its read-only flag.</summary>
	public Task DeleteAsync( string path, CancellationToken cancellationToken ) => Complete( cancellationToken, () =>
	{
		if ( Directory.Exists( path ) )
			Directory.Delete( path, recursive: true );
		else if ( File.Exists( path ) )
		{
			File.SetAttributes( path, FileAttributes.Normal );
			File.Delete( path );
		}
		return true;
	} );

	/// <summary>Moves a folder, or moves a file over any file at the destination.</summary>
	public Task MoveAsync( string path, string to, CancellationToken cancellationToken ) => Complete( cancellationToken, () =>
	{
		if ( Directory.Exists( path ) )
			Directory.Move( path, to );
		else
			File.Move( path, to, overwrite: true );
		return true;
	} );

	/// <summary>Creates a folder and its missing parents.</summary>
	public Task CreateDirectoryAsync( string path, CancellationToken cancellationToken ) => Complete( cancellationToken, () =>
	{
		Directory.CreateDirectory( path );
		return true;
	} );

	/// <summary>Starts a <see cref="FileSystemWatcher"/> over the folder.</summary>
	public IDisposable Watch( string path, bool recursive, Action<string, WorkspaceChange> changed )
	{
		if ( !Directory.Exists( path ) )
			throw new DirectoryNotFoundException( $"The folder '{path}' does not exist." );

		var watcher = new FileSystemWatcher( path )
		{
			IncludeSubdirectories = recursive,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
		};
		try
		{
			watcher.Created += ( _, change ) => changed( change.FullPath, WorkspaceChange.Created );
			watcher.Changed += ( _, change ) => changed( change.FullPath, WorkspaceChange.Changed );
			watcher.Deleted += ( _, change ) => changed( change.FullPath, WorkspaceChange.Deleted );
			watcher.Renamed += ( _, change ) => changed( change.FullPath, WorkspaceChange.Renamed );
			// A full event buffer lost changes; the holder reads everything again.
			watcher.Error += ( _, _ ) => changed( path, WorkspaceChange.Lost );
			watcher.EnableRaisingEvents = true;
			return watcher;
		}
		catch
		{
			watcher.Dispose();
			throw;
		}
	}

	/// <summary>Starts the program in a Windows pseudoconsole, its arguments joined into one command line, or elsewhere in a Linux pseudoterminal.</summary>
	public IWorkspacePty OpenPty( string file, IReadOnlyList<string> arguments, string? directory, int columns, int rows, IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited ) =>
		OperatingSystem.IsWindows()
			? LocalWorkspacePty.Start( BuildCommandLine( file, arguments ), directory, columns, rows, environment, output, exited )
			: LinuxWorkspacePty.Start( file, arguments, directory, columns, rows, environment, output, exited );

	/// <summary>Starts a command line in a Windows pseudoconsole exactly as written, such as a shell setting, whose program may read its own command line
	/// by rules that splitting it into arguments and joining them again would not keep, as <c>cmd /k</c> does. Elsewhere <c>/bin/sh</c> runs it in a Linux
	/// pseudoterminal.</summary>
	/// <param name="commandLine">The program and its arguments, as <c>CreateProcess</c> or <c>/bin/sh</c> takes them.</param>
	/// <param name="directory">The starting folder, or null for this process's own.</param>
	/// <param name="columns">The initial width in cells.</param>
	/// <param name="rows">The initial height in cells.</param>
	/// <param name="environment">Variables set on top of this process's own, or null.</param>
	/// <param name="output">Receives decoded terminal output on a background thread.</param>
	/// <param name="exited">Receives the exit code on a background thread after the last output; -1 when it is unknown.</param>
	/// <exception cref="System.ComponentModel.Win32Exception">The pseudoterminal or the program could not be created.</exception>
	public IWorkspacePty OpenPty( string commandLine, string? directory, int columns, int rows, IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited ) =>
		OperatingSystem.IsWindows()
			? LocalWorkspacePty.Start( commandLine, directory, columns, rows, environment, output, exited )
			: LinuxWorkspacePty.Start( "/bin/sh", ["-c", commandLine], directory, columns, rows, environment, output, exited );

	/// <summary>Joins a program and its arguments into a Windows command line that splits back into the same arguments.</summary>
	internal static string BuildCommandLine( string file, IReadOnlyList<string> arguments )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( file );
		// A program path cannot hold a quote, and CreateProcess reads it up to the closing quote without backslash rules.
		var line = new StringBuilder( file.Contains( ' ' ) || file.Contains( '\t' ) ? $"\"{file}\"" : file );
		foreach ( var argument in arguments )
		{
			line.Append( ' ' );
			AppendArgument( line, argument );
		}
		return line.ToString();
	}

	/// <summary>Appends one argument, quoted with the backslash rules programs split their command line by when it needs quoting.</summary>
	private static void AppendArgument( StringBuilder line, string argument )
	{
		if ( argument.Length > 0 && argument.IndexOfAny( [' ', '\t', '"'] ) < 0 )
		{
			line.Append( argument );
			return;
		}

		line.Append( '"' );
		var backslashes = 0;
		foreach ( var character in argument )
		{
			if ( character == '\\' )
			{
				backslashes++;
				continue;
			}

			// Backslashes double before a quote, which is escaped itself; elsewhere they stand for themselves.
			line.Append( '\\', character == '"' ? backslashes * 2 + 1 : backslashes );
			backslashes = 0;
			line.Append( character );
		}
		line.Append( '\\', backslashes * 2 ).Append( '"' );
	}

	/// <summary>Runs a file operation now and returns its finished task, failed or cancelled as the operation was.</summary>
	private static Task<T> Complete<T>( CancellationToken cancellationToken, Func<T> operation )
	{
		if ( cancellationToken.IsCancellationRequested )
			return Task.FromCanceled<T>( cancellationToken );

		try
		{
			return Task.FromResult( operation() );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			return Task.FromException<T>( error );
		}
	}
}
