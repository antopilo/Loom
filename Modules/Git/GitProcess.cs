using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Holds what one finished git command reported.</summary>
/// <param name="ExitCode">The process exit code; zero is success.</param>
/// <param name="Output">The standard output text.</param>
/// <param name="Diagnostic">The standard error text.</param>
public readonly record struct GitResult( int ExitCode, string Output, string Diagnostic );

/// <summary>Runs git without a shell on a workspace host, this PC's or a paired host's: the one place Lokn starts a git process.</summary>
/// <remarks>
/// Every command runs with <c>--no-optional-locks</c>, so a read never refreshes the index and wakes the file watchers that
/// asked for it, and with <c>GIT_TERMINAL_PROMPT=0</c>, so a command that needs credentials fails with git's message instead
/// of waiting on a prompt nobody can see. A cancelled command is killed with its process tree.
/// </remarks>
public static class GitProcess
{
	/// <summary>Caches git's resolved path; null until git is found, so a git installed after Lokn started is found by the next command.</summary>
	private static string? _executable;

	/// <summary>Gets the git executable on this PC: Git for Windows' machine or per-user install, else git on PATH, else the bare name.</summary>
	public static string Executable => (_executable ??= ToolLocator.Find( "git",
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "Git", "cmd", "git.exe" ),
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Programs", "Git", "cmd", "git.exe" ) )) ?? "git";

	/// <summary>Runs git and returns its output, throwing its diagnostic on failure.</summary>
	/// <param name="workspace">The host git runs on, such as the panel's <see cref="PanelContext.Workspace"/>.</param>
	/// <param name="directory">The folder git runs in.</param>
	/// <param name="cancellationToken">Cancels and kills the command.</param>
	/// <param name="arguments">The git arguments.</param>
	public static Task<string> RunAsync( IWorkspaceHost workspace, string directory, CancellationToken cancellationToken, params string[] arguments ) =>
		RunAsync( workspace, directory, null, cancellationToken, arguments );

	/// <summary>Runs git with text on its standard input and returns its output, throwing its diagnostic on failure.</summary>
	public static async Task<string> RunAsync( IWorkspaceHost workspace, string directory, string? input, CancellationToken cancellationToken, params string[] arguments )
	{
		var result = await RunProcessAsync( workspace, directory, input, cancellationToken, arguments ).ConfigureAwait( false );
		if ( result.ExitCode != 0 )
			throw BuildError( result );

		return result.Output;
	}

	/// <summary>Runs git and returns its output, or null when it exits with an error.</summary>
	public static async Task<string?> TryRunAsync( IWorkspaceHost workspace, string directory, CancellationToken cancellationToken, params string[] arguments )
	{
		var result = await RunProcessAsync( workspace, directory, null, cancellationToken, arguments ).ConfigureAwait( false );
		return result.ExitCode == 0 ? result.Output : null;
	}

	/// <summary>Builds the error for a failed git command from its diagnostic, or from its exit code when it printed none.</summary>
	public static InvalidOperationException BuildError( GitResult result )
	{
		var diagnostic = result.Diagnostic.Trim();
		return new( diagnostic.Length > 0 ? diagnostic : $"git exited with code {result.ExitCode}." );
	}

	/// <summary>Runs git and returns its exit code and output, for commands whose non-zero exit is an answer.</summary>
	public static Task<GitResult> RunProcessAsync( IWorkspaceHost workspace, string directory, string? input, CancellationToken cancellationToken, params string[] arguments ) =>
		RunProcessAsync( workspace, directory, input, null, cancellationToken, arguments );

	/// <summary>Runs git with extra environment variables, such as <c>GIT_EDITOR</c>, and returns its exit code and output.</summary>
	public static async Task<GitResult> RunProcessAsync( IWorkspaceHost workspace, string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, params string[] arguments )
	{
		var variables = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["GIT_TERMINAL_PROMPT"] = "0",
		};
		if ( environment is not null )
		{
			foreach ( var (name, value) in environment )
				variables[name] = value;
		}

		// Another host finds its own git on its PATH.
		var file = workspace is LocalWorkspaceHost ? Executable : "git";
		var result = await workspace.RunAsync( file, ["--no-optional-locks", .. arguments], directory, input, variables, cancellationToken ).ConfigureAwait( false );
		return new( result.Code, result.Output, result.Error );
	}
}
