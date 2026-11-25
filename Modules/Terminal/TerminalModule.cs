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

	/// <summary>Registers the settings, the terminal panel, the launcher, the terminal a new task can start with, and one command per installed shell.</summary>
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
			.Contribute( TaskStarter.Point, new TaskStarter( "Terminal", "\uE756", 10, app => app.Panels.Open( "terminal.shell" ) ) );

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
}
