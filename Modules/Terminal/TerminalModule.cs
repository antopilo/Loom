using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Terminal;

/// <summary>Contributes terminal panels that run any shell or console program in a Windows pseudoconsole, and lets other modules open one for a command.</summary>
public sealed class TerminalModule : ILoknModule, ITerminalLauncher
{
	/// <summary>Defines the command line new terminals run; blank picks the first installed of PowerShell, Windows PowerShell and Command Prompt.</summary>
	internal static readonly Setting<string> Shell = new(
		"terminal.shell",
		"Shell",
		OperatingSystem.IsWindows() ? "The command new terminals run, such as pwsh or wsl. Blank picks one." : "The command new terminals run, such as bash or zsh. Blank uses your login shell.",
		defaultValue: "",
		category: "Terminal" );

	/// <summary>Defines the terminal text size.</summary>
	internal static readonly Setting<int> FontSize = new(
		"terminal.font-size",
		"Font size",
		"Text size in terminals.",
		defaultValue: 13,
		category: "Terminal",
		isValid: value => value is >= 8 and <= 32 ) { Unit = "px", Minimum = 8, Maximum = 32 };

	/// <summary>Defines how many scrolled-off lines each terminal keeps.</summary>
	internal static readonly Setting<int> Scrollback = new(
		"terminal.scrollback",
		"Scrollback",
		"Lines kept above the screen.",
		defaultValue: 10000,
		category: "Terminal",
		isValid: value => value is >= 0 and <= 100000 ) { Unit = "lines", Step = 1000, Minimum = 0, Maximum = 100000 };

	/// <summary>Stores the application after startup, where launched terminals open.</summary>
	private LoknApplication? _app;

	/// <summary>Gets the feature's stable module identifier.</summary>
	public string Id => "terminal";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "Terminal";

	/// <summary>Registers the settings, the terminal panel, the launcher, the terminal a new task can start with, the agents' tool that opens one, and
	/// one command per installed shell.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Service<ITerminalLauncher>( this )
			.Setting( Shell )
			.Setting( FontSize )
			.Setting( Scrollback )
			.Panel( "shell", "Terminal", context => new TerminalContent( context ), "\uE756", onHost: true )
			// Notifications from a terminal panel show its icon; without the Notifications module this is unused.
			.Contribute( NotificationKind.Point, new NotificationKind( "terminal", "\uE756" ) )
			.Contribute( TaskStarter.Point, new TaskStarter( "Terminal", "\uE756", 10, app => app.Panels.Open( "terminal.shell" ) ) )
			.Tool( "open",
				"Opens a terminal panel in Loom beside your conversation, where the user sees it and can type, optionally running a command that keeps running there: a dev server, a watcher, an interactive program, or a shell for the user. Use it instead of starting a console window (start, Start-Process, cmd /c start, wt, a new PowerShell window). For a command whose output you need, use your own shell tool; you cannot read this panel's output.",
				"""
				{
					"type": "object",
					"properties": {
						"command": { "type": "string", "description": "Commands to run in the new terminal, which stays open after them. Omit for a plain shell." },
						"shell": { "type": "string", "enum": ["powershell", "bash", "cmd"], "description": "The language of the command; defaults to powershell. Without a command the panel runs the user's Terminal shell setting." },
						"cwd": { "type": "string", "description": "An absolute folder on this PC to start in; defaults to the conversation's project folder." }
					},
					"additionalProperties": false
				}
				""",
				OpenForAgent );

		foreach ( var profile in ShellProfile.Detect() )
		{
			var id = "new-" + profile.Name.ToLowerInvariant().Replace( ' ', '-' );
			builder.Command( id, $"Terminal: new {profile.Name}", context => context.App.Panels.Open( "terminal.shell", input: profile ) );
		}
	}

	/// <summary>Keeps the application so launched terminals can open.</summary>
	public void Start( ModuleContext context ) => _app = context.App;

	/// <summary>Opens a terminal panel running a command line on this PC, such as Omp's sign-in, whichever host is current.</summary>
	public void Run( string title, string commandLine )
	{
		var app = _app ?? throw new InvalidOperationException( "The Terminal module has not started." );
		app.Panels.Open( "terminal.shell", input: new ShellProfile( title, commandLine ), hostId: LoknHost.LocalId );
	}

	/// <summary>Reports whether the language names a shell family; Bash snippets fall back to PowerShell, so every family runs on Windows.</summary>
	public bool CanRunSnippet( string language ) => ShellSnippet.FindKind( language ) is not null;

	/// <summary>Opens a terminal on this PC beside the active panel that runs the snippet and stays open.</summary>
	/// <remarks>The snippet's script and shell paths are this PC's, so it runs here whichever host is current, as <see cref="Run"/> does.</remarks>
	public void RunSnippet( string code, string language )
	{
		var app = _app ?? throw new InvalidOperationException( "The Terminal module has not started." );
		if ( ShellSnippet.Build( code, language ) is { } profile )
			app.Panels.Open( "terminal.shell", input: profile, hostId: LoknHost.LocalId );
		else
			app.ReportError( "Terminal", new InvalidOperationException( $"No installed shell runs {language} code." ) );
	}

	/// <summary>Opens a terminal on this PC for an agent, beside its conversation when a panel of this window shows it, running its command if it gave one.</summary>
	/// <remarks>The daemon has no window to show panels in, so a chat it runs gets an error rather than a terminal nobody sees.</remarks>
	private static AgentToolResult OpenForAgent( AgentToolContext context, JsonElement arguments )
	{
		var app = context.App;
		if ( app.IsHost )
			return AgentToolResult.Error( "Terminal panels need a Loom window, and this chat runs in the background service. Use your own shell tool." );

		var command = ReadString( arguments, "command" );
		var shell = ReadString( arguments, "shell" ) ?? "powershell";
		ShellProfile profile;
		if ( command is null )
			profile = ShellProfile.Resolve( app.Settings.Get( Shell ) );
		else if ( ShellSnippet.Build( command, shell ) is { } snippet )
			profile = snippet;
		else
			return AgentToolResult.Error( $"No installed shell runs {shell} commands." );

		var launch = new TerminalLaunch( profile, ReadString( arguments, "cwd" ) );
		var panels = app.Panels;
		var isBeside = context.Caller is { } caller && panels.Panels.Any( panel => panel.Id == panels.ResolvePanelId( caller ) );
		var opened = isBeside
			? panels.OpenBeside( "terminal.shell", context.Caller!.Value, launch, LoknHost.LocalId )
			: panels.Open( "terminal.shell", null, launch, LoknHost.LocalId );
		var what = command is null ? $"a {profile.Name} shell" : $"\"{profile.Name}\"";
		return new( $"Opened terminal panel {opened.ToString( "N" )[..8]} running {what}{( isBeside ? " beside your conversation" : "" )}. The user sees it; its output does not come back to you." );
	}

	/// <summary>Reads a non-blank string argument, or null.</summary>
	private static string? ReadString( JsonElement arguments, string name ) =>
		arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace( text ) ? text : null;
}
