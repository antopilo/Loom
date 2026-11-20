using System.Text;
using Lokn.Core;

namespace Lokn.Modules.Terminal;

/// <summary>Names a shell and the command line that starts it.</summary>
/// <param name="Name">The display name.</param>
/// <param name="CommandLine">The full command line; <see cref="Split"/> turns it into the program and arguments the workspace starts.</param>
internal sealed record ShellProfile( string Name, string CommandLine )
{
	/// <summary>Defines the shell a paired host starts when the shell setting is blank: Windows PowerShell, which every Windows has on its search path.</summary>
	private static readonly ShellProfile HostDefault = new( "Windows PowerShell", "powershell.exe -NoLogo" );

	/// <summary>Gets the command line that starts the same shell on another PC, naming its program the way that PC finds it rather than where this PC has
	/// it; null for a profile that names its program as written, such as the shell setting's or a restored terminal's.</summary>
	internal string? PortableCommandLine
	{
		get; init;
	}

	/// <summary>Finds the shells installed on this machine, PowerShell first.</summary>
	internal static IReadOnlyList<ShellProfile> Detect()
	{
		var system = Environment.GetFolderPath( Environment.SpecialFolder.System );
		// Each shell's program as another PC finds it: by name on its search path, and Git Bash, which has no name of its own there, in Git's default folder.
		var candidates = new (string Name, string? Path, string Arguments, string Portable)[]
		{
			("PowerShell", ToolLocator.Find( "pwsh.exe" ) ?? FindExisting( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "PowerShell", "7", "pwsh.exe" ) ), " -NoLogo", "pwsh.exe"),
			("Windows PowerShell", FindExisting( Path.Combine( system, "WindowsPowerShell", "v1.0", "powershell.exe" ) ), " -NoLogo", "powershell.exe"),
			("Command Prompt", FindExisting( Environment.GetEnvironmentVariable( "ComSpec" ) ) ?? FindExisting( Path.Combine( system, "cmd.exe" ) ), "", "cmd.exe"),
			("Git Bash", FindGitBash(), " -i -l", "\"C:\\Program Files\\Git\\bin\\bash.exe\""),
			("WSL", FindExisting( Path.Combine( system, "wsl.exe" ) ), "", "wsl.exe"),
		};

		return candidates
			.Where( candidate => candidate.Path is not null )
			.Select( candidate => new ShellProfile( candidate.Name, Quote( candidate.Path! ) + candidate.Arguments ) { PortableCommandLine = candidate.Portable + candidate.Arguments } )
			.ToArray();
	}

	/// <summary>Builds the profile for a configured command line, or when it is blank, the first shell detected on This PC, or for a paired host Windows
	/// PowerShell, since what this PC has installed says nothing of the host.</summary>
	/// <param name="commandLine">The shell setting.</param>
	/// <param name="isLocal">Whether the shell runs on This PC.</param>
	internal static ShellProfile Resolve( string commandLine, bool isLocal = true )
	{
		if ( !string.IsNullOrWhiteSpace( commandLine ) )
			return new( commandLine.Trim(), commandLine.Trim() );

		return isLocal ? Detect().FirstOrDefault() ?? new( "Command Prompt", "cmd.exe" ) : HostDefault;
	}

	/// <summary>Gets the profile that starts this shell on a paired host: a detected shell by its program's name there (<see cref="PortableCommandLine"/>),
	/// any other as written.</summary>
	internal ShellProfile ForHost() => PortableCommandLine is { } portable ? new( Name, portable ) : this;

	/// <summary>Splits the command line into the program and its arguments, by the rules Windows programs split their own command line with.</summary>
	/// <remarks>The program ends at the first space, or is quoted without escapes; arguments honour quotes, <c>""</c> inside quotes and backslashes before a quote.</remarks>
	internal (string File, string[] Arguments) Split()
	{
		var line = CommandLine.Trim();
		var end = line.StartsWith( '"' ) ? line.IndexOf( '"', 1 ) : line.IndexOfAny( [' ', '\t'] );
		var file = line.StartsWith( '"' ) ? line[1..(end < 0 ? line.Length : end)] : line[..(end < 0 ? line.Length : end)];
		var index = end < 0 ? line.Length : line.StartsWith( '"' ) ? end + 1 : end;

		var arguments = new List<string>();
		var current = new StringBuilder();
		var isQuoted = false;
		var hasArgument = false;
		while ( index < line.Length )
		{
			var character = line[index];
			if ( character == '\\' )
			{
				var count = 0;
				for ( ; index < line.Length && line[index] == '\\'; index++ )
					count++;

				// Backslashes halve before a quote, and an odd one left escapes it; elsewhere they stand for themselves.
				var beforeQuote = index < line.Length && line[index] == '"';
				current.Append( '\\', beforeQuote ? count / 2 : count );
				if ( beforeQuote && count % 2 == 1 )
				{
					current.Append( '"' );
					index++;
				}
				hasArgument = true;
				continue;
			}

			if ( character == '"' )
			{
				if ( isQuoted && index + 1 < line.Length && line[index + 1] == '"' )
				{
					current.Append( '"' );
					index++;
				}
				else
					isQuoted = !isQuoted;
				hasArgument = true;
			}
			else if ( !isQuoted && character is ' ' or '\t' )
			{
				if ( hasArgument )
					arguments.Add( current.ToString() );
				current.Clear();
				hasArgument = false;
			}
			else
			{
				current.Append( character );
				hasArgument = true;
			}
			index++;
		}

		if ( hasArgument )
			arguments.Add( current.ToString() );
		return (file, [.. arguments]);
	}

	/// <summary>Finds PowerShell 7 on PATH or in its default install directory.</summary>
	internal static string? FindPowerShell() =>
		ToolLocator.Find( "pwsh.exe" ) ?? FindExisting( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "PowerShell", "7", "pwsh.exe" ) );

	/// <summary>Finds Windows PowerShell in the system directory.</summary>
	internal static string? FindWindowsPowerShell() =>
		FindExisting( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.System ), "WindowsPowerShell", "v1.0", "powershell.exe" ) );

	/// <summary>Finds the Command Prompt.</summary>
	internal static string? FindCommandPrompt() =>
		FindExisting( Environment.GetEnvironmentVariable( "ComSpec" ) ) ?? FindExisting( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.System ), "cmd.exe" ) );

	/// <summary>Finds Git for Windows' bash beside git on PATH or in the default install directory.</summary>
	internal static string? FindGitBash()
	{
		if ( ToolLocator.Find( "git.exe" ) is { } git && Path.GetDirectoryName( Path.GetDirectoryName( git ) ) is { } root
			&& FindExisting( Path.Combine( root, "bin", "bash.exe" ) ) is { } bash )
			return bash;

		return FindExisting( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "Git", "bin", "bash.exe" ) );
	}

	/// <summary>Returns the path when the file exists.</summary>
	private static string? FindExisting( string? path ) => path is { Length: > 0 } && File.Exists( path ) ? path : null;

	/// <summary>Quotes a path that contains spaces.</summary>
	internal static string Quote( string path ) => path.Contains( ' ' ) ? $"\"{path}\"" : path;
}
