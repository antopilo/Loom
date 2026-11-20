namespace Lokn.Core;

/// <summary>Opens a terminal panel that runs one command line, such as an interactive sign-in; the Terminal module publishes it.</summary>
/// <remarks>Only code can run commands this way, never a <c>lokn://</c> link, so a link in a chat cannot start a program.</remarks>
public interface ITerminalLauncher
{
	/// <summary>Opens a terminal panel running a command line; call it on the UI thread.</summary>
	/// <param name="title">The terminal's name, such as "Omp sign-in".</param>
	/// <param name="commandLine">The full command line, with paths that contain spaces quoted.</param>
	void Run( string title, string commandLine );

	/// <summary>Reports whether a code block's language names a shell this machine can run, such as bash, pwsh or cmd.</summary>
	/// <param name="language">The fenced-code language, such as "bash" or "powershell".</param>
	bool CanRunSnippet( string language );

	/// <summary>Opens a terminal beside the active panel that runs a shell snippet in a matching shell and stays open; call it on the UI thread.</summary>
	/// <param name="code">The snippet, possibly several lines.</param>
	/// <param name="language">The fenced-code language that picks the shell.</param>
	void RunSnippet( string code, string language );
}
