using System.Text;

namespace Lokn.Modules.Terminal;

/// <summary>Builds the shell profile that runs a code block from a chat and then stays open for more commands.</summary>
internal static class ShellSnippet
{
	/// <summary>Defines the longest terminal title taken from a snippet's first line.</summary>
	private const int TitleLength = 40;

	/// <summary>Names the shell family a fenced-code language belongs to, or null for a language that is not a shell.</summary>
	internal static ShellKind? FindKind( string language ) => language.Trim().ToLowerInvariant() switch
	{
		"powershell" or "pwsh" or "ps1" or "ps" => ShellKind.PowerShell,
		"cmd" or "bat" or "batch" => ShellKind.CommandPrompt,
		"bash" or "sh" or "shell" or "zsh" or "console" or "shellsession" => ShellKind.Bash,
		_ => null,
	};

	/// <summary>Builds the profile that runs a snippet, or null when no shell for its language is installed.</summary>
	/// <remarks>
	/// PowerShell takes the snippet encoded, so no quoting can break it. Bash and the Command Prompt run it from a file in
	/// <c>%TEMP%\Lokn\snippets</c>. A Bash snippet runs in PowerShell when Git Bash is not installed, which suits simple commands.
	/// </remarks>
	internal static ShellProfile? Build( string code, string language )
	{
		if ( FindKind( language ) is not { } kind )
			return null;

		code = kind == ShellKind.Bash ? StripPrompts( code ) : code;
		var title = ComputeTitle( code );
		if ( kind == ShellKind.Bash && ShellProfile.FindGitBash() is { } bash )
		{
			var path = WriteScript( code.Replace( "\r\n", "\n" ), ".sh", new UTF8Encoding( false ) ).Replace( '\\', '/' );
			return new( title, $"{ShellProfile.Quote( bash )} -l -i -c \"source '{path}'; exec bash -l -i\"" );
		}

		if ( kind == ShellKind.CommandPrompt && ShellProfile.FindCommandPrompt() is { } cmd )
		{
			var path = WriteScript( "@chcp 65001 >nul\r\n" + code.Replace( "\r\n", "\n" ).Replace( "\n", "\r\n" ), ".cmd", new UTF8Encoding( false ) );
			return new( title, $"{ShellProfile.Quote( cmd )} /K \"{path}\"" );
		}

		if ( (ShellProfile.FindPowerShell() ?? ShellProfile.FindWindowsPowerShell()) is not { } powerShell )
			return null;

		var encoded = Convert.ToBase64String( Encoding.Unicode.GetBytes( code ) );
		return new( title, $"{ShellProfile.Quote( powerShell )} -NoLogo -NoExit -EncodedCommand {encoded}" );
	}

	/// <summary>Removes a leading "$ " prompt from each line, as console transcripts show commands.</summary>
	private static string StripPrompts( string code ) =>
		string.Join( "\n", code.Replace( "\r\n", "\n" ).Split( '\n' ).Select( line => line.StartsWith( "$ ", StringComparison.Ordinal ) ? line[2..] : line ) );

	/// <summary>Names the terminal after the snippet's first non-blank line.</summary>
	private static string ComputeTitle( string code )
	{
		var line = code.Replace( "\r\n", "\n" ).Split( '\n' ).FirstOrDefault( item => item.Trim().Length > 0 )?.Trim() ?? "Snippet";
		return line.Length <= TitleLength ? line : line[..(TitleLength - 1)] + "…";
	}

	/// <summary>Writes a snippet to a new file in Lokn's temporary snippets folder and returns its path.</summary>
	private static string WriteScript( string content, string extension, Encoding encoding )
	{
		var directory = Path.Combine( Path.GetTempPath(), "Lokn", "snippets" );
		Directory.CreateDirectory( directory );
		var path = Path.Combine( directory, Guid.NewGuid().ToString( "N" ) + extension );
		File.WriteAllText( path, content, encoding );
		return path;
	}
}

/// <summary>Names the shell family that runs a snippet.</summary>
internal enum ShellKind
{
	/// <summary>PowerShell 7, else Windows PowerShell.</summary>
	PowerShell,

	/// <summary>The Command Prompt.</summary>
	CommandPrompt,

	/// <summary>Git Bash, else PowerShell.</summary>
	Bash,
}
