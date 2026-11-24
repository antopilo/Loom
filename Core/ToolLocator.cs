using System.Diagnostics;
using System.Text;

namespace Lokn.Core;

/// <summary>Finds and installs the command-line tools Loom runs, such as git and omp, without asking the user for paths.</summary>
/// <remarks>
/// Loom's PATH is fixed when it starts, so a tool installed later is not on it. <see cref="RefreshPath"/> adds the directories the
/// installers wrote to the user and machine PATH in the registry, so every later child process, including those started by name,
/// finds the new tool.
/// </remarks>
public static class ToolLocator
{
	/// <summary>Finds a tool: the first known location that exists, else the name on PATH; null when there is neither.</summary>
	/// <param name="name">The command name, such as <c>git</c>; <c>.exe</c> and <c>.cmd</c> are tried when it has no extension.</param>
	/// <param name="knownPaths">Full paths where installers put the tool, checked first.</param>
	public static string? Find( string name, params IEnumerable<string> knownPaths )
	{
		foreach ( var path in knownPaths )
		{
			if ( path.Length > 0 && File.Exists( path ) )
				return path;
		}

		RefreshPath();
		var names = Path.HasExtension( name ) ? [name] : new[] { name + ".exe", name + ".cmd", name };
		foreach ( var directory in (Environment.GetEnvironmentVariable( "PATH" ) ?? "").Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			foreach ( var candidate in names )
			{
				try
				{
					var path = Path.Combine( directory.Trim( '"' ), candidate );
					if ( File.Exists( path ) )
						return path;
				}
				catch ( ArgumentException )
				{
					// A malformed PATH entry names nothing.
				}
			}
		}

		return null;
	}

	/// <summary>Adds to this process's PATH the directories of the user and machine PATH it does not have yet, such as one an installer just added.</summary>
	public static void RefreshPath()
	{
		var current = Environment.GetEnvironmentVariable( "PATH" ) ?? "";
		var known = new HashSet<string>( current.Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ), StringComparer.OrdinalIgnoreCase );
		var added = new List<string>();
		foreach ( var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine } )
		{
			foreach ( var entry in (Environment.GetEnvironmentVariable( "PATH", target ) ?? "").Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			{
				var expanded = Environment.ExpandEnvironmentVariables( entry );
				if ( known.Add( expanded ) )
					added.Add( expanded );
			}
		}

		if ( added.Count > 0 )
			Environment.SetEnvironmentVariable( "PATH", string.Join( Path.PathSeparator, [current, .. added] ) );
	}

	/// <summary>Installs a package with winget, silently and for the current user where the package allows it, then refreshes PATH.</summary>
	/// <param name="id">The winget package ID, such as <c>Git.Git</c>.</param>
	/// <param name="progress">Receives winget's output lines.</param>
	/// <param name="cancellationToken">Stops the install.</param>
	public static async Task InstallWingetAsync( string id, IProgress<string> progress, CancellationToken cancellationToken )
	{
		if ( !OperatingSystem.IsWindows() )
			throw new PlatformNotSupportedException( "winget is available only on Windows." );

		var winget = Find( "winget", Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Microsoft", "WindowsApps", "winget.exe" ) )
			?? throw new InvalidOperationException( "winget is not available. Install App Installer from the Microsoft Store, then try again." );
		await RunAsync( winget, ["install", "--id", id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"], progress, cancellationToken ).ConfigureAwait( false );
		RefreshPath();
	}

	/// <summary>Runs a PowerShell script hidden, such as a vendor's install script, then refreshes PATH.</summary>
	/// <param name="script">The script text.</param>
	/// <param name="progress">Receives its output lines.</param>
	/// <param name="cancellationToken">Stops the script.</param>
	public static async Task RunPowerShellAsync( string script, IProgress<string> progress, CancellationToken cancellationToken )
	{
		if ( !OperatingSystem.IsWindows() )
			throw new PlatformNotSupportedException( "Windows PowerShell is available only on Windows." );

		var powershell = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.System ), "WindowsPowerShell", "v1.0", "powershell.exe" );
		await RunAsync( powershell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], progress, cancellationToken ).ConfigureAwait( false );
		RefreshPath();
	}

	/// <summary>Runs a program without a window, reporting each output line, and fails with its last lines when it exits with an error.</summary>
	/// <param name="executable">The full path of the program.</param>
	/// <param name="arguments">Its arguments.</param>
	/// <param name="progress">Receives each non-empty output line.</param>
	/// <param name="cancellationToken">Stops the program and its children.</param>
	public static async Task RunAsync( string executable, IEnumerable<string> arguments, IProgress<string> progress, CancellationToken cancellationToken )
	{
		var start = new ProcessStartInfo( executable )
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach ( var argument in arguments )
			start.ArgumentList.Add( argument );

		var tail = new Queue<string>();
		void Report( string? line )
		{
			if ( string.IsNullOrWhiteSpace( line ) )
				return;
			lock ( tail )
			{
				tail.Enqueue( line.Trim() );
				if ( tail.Count > 3 )
					tail.Dequeue();
			}

			progress.Report( line.Trim() );
		}

		using var process = new Process { StartInfo = start };
		process.OutputDataReceived += ( _, data ) => Report( data.Data );
		process.ErrorDataReceived += ( _, data ) => Report( data.Data );
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		try
		{
			await process.WaitForExitAsync( cancellationToken ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException )
		{
			if ( !process.HasExited )
				process.Kill( entireProcessTree: true );
			throw;
		}

		if ( process.ExitCode != 0 )
		{
			string detail;
			lock ( tail )
				detail = string.Join( " ", tail );
			throw new InvalidOperationException( $"{Path.GetFileName( executable )} exited with code {process.ExitCode}. {detail}".Trim() );
		}
	}
}
