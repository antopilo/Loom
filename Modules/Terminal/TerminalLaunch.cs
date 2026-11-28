namespace Lokn.Modules.Terminal;

/// <summary>Asks a new terminal panel to run a shell in a given folder, as an agent's <c>terminal_open</c> does.</summary>
/// <param name="Profile">The shell, or the snippet to run in one.</param>
/// <param name="Directory">The folder to start in; the panel falls back to its usual folders when it does not exist.</param>
internal sealed record TerminalLaunch( ShellProfile Profile, string? Directory );
