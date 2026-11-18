using Lokn.Core;

namespace Lokn.Modules.Terminal;

/// <summary>Names a shell and the command line that starts it.</summary>
/// <param name="Name">The display name.</param>
/// <param name="CommandLine">The full command line passed to the pseudoconsole.</param>
internal sealed record ShellProfile( string Name, string CommandLine )
{
	/// <summary>Finds the shells installed on this machine, PowerShell first.</summary>
	internal static IReadOnlyList<ShellProfile> Detect()
	{
		var system = Environment.GetFolderPath( Environment.SpecialFolder.System );
		var candidates = new (string Name, string? Path, string Arguments)[]
		{
			("PowerShell", ToolLocator.Find( "pwsh.exe" ) ?? FindExisting( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "PowerShell", "7", "pwsh.exe" ) ), " -NoLogo"),
			("Windows PowerShell", FindExisting( Path.Combine( system, "WindowsPowerShell", "v1.0", "powershell.exe" ) ), " -NoLogo"),
			("Command Prompt", FindExisting( Environment.GetEnvironmentVariable( "ComSpec" ) ) ?? FindExisting( Path.Combine( system, "cmd.exe" ) ), ""),
			("Git Bash", FindGitBash(), " -i -l"),
			("WSL", FindExisting( Path.Combine( system, "wsl.exe" ) ), ""),
		};

		return candidates
			.Where( candidate => candidate.Path is not null )
			.Select( candidate => new ShellProfile( candidate.Name, Quote( candidate.Path! ) + candidate.Arguments ) )
			.ToArray();
	}

	/// <summary>Builds the profile for a configured command line, or the first detected shell when it is blank.</summary>
	internal static ShellProfile Resolve( string commandLine )
	{
		if ( !string.IsNullOrWhiteSpace( commandLine ) )
			return new( commandLine.Trim(), commandLine.Trim() );

		return Detect().FirstOrDefault() ?? new( "Command Prompt", "cmd.exe" );
	}

	/// <summary>Finds Git for Windows' bash beside git on PATH or in the default install directory.</summary>
	private static string? FindGitBash()
	{
		if ( ToolLocator.Find( "git.exe" ) is { } git && Path.GetDirectoryName( Path.GetDirectoryName( git ) ) is { } root
			&& FindExisting( Path.Combine( root, "bin", "bash.exe" ) ) is { } bash )
			return bash;

		return FindExisting( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "Git", "bin", "bash.exe" ) );
	}

	/// <summary>Returns the path when the file exists.</summary>
	private static string? FindExisting( string? path ) => path is { Length: > 0 } && File.Exists( path ) ? path : null;

	/// <summary>Quotes a path that contains spaces.</summary>
	private static string Quote( string path ) => path.Contains( ' ' ) ? $"\"{path}\"" : path;
}
