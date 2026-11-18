using System.Text.RegularExpressions;

namespace Lokn.Modules.Chat;

/// <summary>Converts terminal output to readable native text without displaying ANSI control sequences.</summary>
internal static partial class TerminalText
{
	/// <summary>Removes ANSI color and terminal-link controls while preserving Unicode and visible text.</summary>
	internal static string BuildPlain( string text ) => AnsiPattern().Replace( text, "" );

	/// <summary>Matches CSI formatting and OSC terminal metadata emitted by command output.</summary>
	[GeneratedRegex( @"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)" )]
	private static partial Regex AnsiPattern();
}
