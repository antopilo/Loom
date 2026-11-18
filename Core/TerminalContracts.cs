namespace Lokn.Core;

/// <summary>Opens a terminal panel that runs one command line, such as an interactive sign-in; the Terminal module publishes it.</summary>
/// <remarks>Only code can run commands this way, never a <c>lokn://</c> link, so a link in a chat cannot start a program.</remarks>
public interface ITerminalLauncher
{
	/// <summary>Opens a terminal panel running a command line; call it on the UI thread.</summary>
	/// <param name="title">The terminal's name, such as "Omp sign-in".</param>
	/// <param name="commandLine">The full command line, with paths that contain spaces quoted.</param>
	void Run( string title, string commandLine );
}
