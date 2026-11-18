using System.Diagnostics;
using System.Text;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Holds what one finished git command reported.</summary>
/// <param name="ExitCode">The process exit code; zero is success.</param>
/// <param name="Output">The standard output text.</param>
/// <param name="Diagnostic">The standard error text.</param>
public readonly record struct GitResult( int ExitCode, string Output, string Diagnostic );

/// <summary>Runs git without a shell: the one place Lokn starts a git process.</summary>
/// <remarks>
/// Every command runs with <c>--no-optional-locks</c>, so a read never refreshes the index and wakes the file watchers that
/// asked for it, and with <c>GIT_TERMINAL_PROMPT=0</c>, so a command that needs credentials fails with git's message instead
/// of waiting on a prompt nobody can see. A cancelled command is killed with its process tree.
/// </remarks>
public static class GitProcess
{
	/// <summary>Caches git's resolved path; null until git is found, so a git installed after Lokn started is found by the next command.</summary>
	private static string? _executable;

	/// <summary>Gets the git executable: Git for Windows' machine or per-user install, else git on PATH, else the bare name.</summary>
	public static string Executable => (_executable ??= ToolLocator.Find( "git",
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "Git", "cmd", "git.exe" ),
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Programs", "Git", "cmd", "git.exe" ) )) ?? "git";

	/// <summary>Runs git and returns its output, throwing its diagnostic on failure.</summary>
	public static Task<string> RunAsync( string directory, CancellationToken cancellationToken, params string[] arguments ) =>
		RunAsync( directory, null, cancellationToken, arguments );

	/// <summary>Runs git with text on its standard input and returns its output, throwing its diagnostic on failure.</summary>
	public static async Task<string> RunAsync( string directory, string? input, CancellationToken cancellationToken, params string[] arguments )
	{
		var result = await RunProcessAsync( directory, input, cancellationToken, arguments ).ConfigureAwait( false );
		if ( result.ExitCode != 0 )
			throw BuildError( result );

		return result.Output;
	}

	/// <summary>Runs git and returns its output, or null when it exits with an error.</summary>
	public static async Task<string?> TryRunAsync( string directory, CancellationToken cancellationToken, params string[] arguments )
	{
		var result = await RunProcessAsync( directory, null, cancellationToken, arguments ).ConfigureAwait( false );
		return result.ExitCode == 0 ? result.Output : null;
	}

	/// <summary>Builds the error for a failed git command from its diagnostic, or from its exit code when it printed none.</summary>
	public static InvalidOperationException BuildError( GitResult result )
	{
		var diagnostic = result.Diagnostic.Trim();
		return new( diagnostic.Length > 0 ? diagnostic : $"git exited with code {result.ExitCode}." );
	}

	/// <summary>Runs git and returns its exit code and output, for commands whose non-zero exit is an answer.</summary>
	public static Task<GitResult> RunProcessAsync( string directory, string? input, CancellationToken cancellationToken, params string[] arguments ) =>
		RunProcessAsync( directory, input, null, cancellationToken, arguments );

	/// <summary>Runs git with extra environment variables, such as <c>GIT_EDITOR</c>, and returns its exit code and output.</summary>
	public static async Task<GitResult> RunProcessAsync( string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, params string[] arguments )
	{
		var start = new ProcessStartInfo( Executable )
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

		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		if ( environment is not null )
		{
			foreach ( var (name, value) in environment )
				start.Environment[name] = value;
		}

		start.ArgumentList.Add( "--no-optional-locks" );
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
			// A closed panel or a newer read must not leave its git command running.
			if ( !process.HasExited )
				process.Kill( entireProcessTree: true );

			throw;
		}
	}
}
