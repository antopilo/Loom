using Goo;
using Lokn.Core;
using Lokn.Modules.Terminal;

namespace Lokn.Tests;

/// <summary>Drives a real terminal panel running Command Prompt through a native Goo window.</summary>
/// <remarks>Every key and text goes to the terminal after focusing it in code (<see cref="UiInput"/>), so the input lands even when the window lost
/// the system focus to another window on the desktop.</remarks>
internal static class TerminalUiChecks
{
	/// <summary>Defines the frame count used to settle layout and accessibility updates.</summary>
	private const int SettleFrames = 16;

	/// <summary>Defines how long the shell may take to respond, in seconds.</summary>
	private const int TimeoutSeconds = 20;

	/// <summary>Names the terminal's accessible group.</summary>
	private const string TerminalName = "Terminal: Command Prompt";

	/// <summary>Types into the shell, checks that Ctrl+W reaches it instead of closing the panel, and exits it.</summary>
	internal static void Run( string[] args )
	{
		Application.VerifyState = true;
		var app = new LoknApplication();
		app.Load( new ShellModule() );
		app.Load( new TerminalModule() );
		app.Panels.Open( "terminal.shell", input: new ShellProfile( "Command Prompt", "cmd.exe /d /k prompt $g" ) );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn terminal checks",
			Size = new( 1000, 640 ),
			Decorated = false,
			Transparent = true,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		window.Open();
		Pump( window );

		PumpUntil( window, semantics, () => FindTerminal( semantics ) is not null && Nodes( semantics ).Any( node => (node.Value ?? node.Name)?.Trim() == ">" ),
			"The shell's prompt renders." );
		Type( window, semantics, "echo lokn-%COMSPEC:~-7%-ui" );
		Press( window, semantics, Key.Enter, KeyModifiers.None );
		PumpUntil( window, semantics, () => HasText( semantics, "lokn-cmd.exe-ui" ), "Typed text reaches the shell and its output renders." );

		// A long run must end where the next run starts, or text drifts out of its cells across a line.
		Type( window, semantics, $"prompt {new string( 'W', 60 )}$e[31mX$e[0m$g" );
		Press( window, semantics, Key.Enter, KeyModifiers.None );
		PumpUntil( window, semantics, () => Nodes( semantics ).Any( node => node.Value == "X" ), "A colored prompt renders as separate runs." );
		var run = Nodes( semantics ).Last( node => node.Value == new string( 'W', 60 ) ).Bounds;
		var next = Nodes( semantics ).Last( node => node.Value == "X" ).Bounds;
		CoreChecks.Check( Math.Abs( run.X + run.Width - next.X ) <= 1, $"Cells match the font's advance: a 60-cell run ends at {run.X + run.Width:0.00} and the next cell starts at {next.X:0.00}." );

		// Ctrl+W goes to the shell, which echoes it as ^W; the shell then still takes typed text, so the panel never closed.
		Press( window, semantics, Key.W, KeyModifiers.Primary );
		Press( window, semantics, Key.Escape, KeyModifiers.None );
		Type( window, semantics, "echo lokn-after-ctrl-w" );
		Press( window, semantics, Key.Enter, KeyModifiers.None );
		PumpUntil( window, semantics, () => Nodes( semantics ).Any( node => node.Value?.Trim() == "lokn-after-ctrl-w" ), "The shell still runs commands after Ctrl+W." );
		CoreChecks.Check( app.Panels.Panels.Count == 1, "Ctrl+W goes to the focused shell instead of closing the panel." );

		var capture = Array.IndexOf( args, "--capture" );
		if ( capture >= 0 && capture + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[capture + 1], "terminal" );

		Type( window, semantics, "exit" );
		Press( window, semantics, Key.Enter, KeyModifiers.None );
		PumpUntil( window, semantics, () => app.Panels.Panels.Count == 0, "A clean shell exit closes its panel." );
		CoreChecks.Check( app.LastError is null, "The terminal runs without contained failures." );
		window.RequestClose();
		Pump( window );
		Console.WriteLine( "PASS native terminal prompt, typed input and output, claimed Ctrl+W, and exit-to-close." );
	}

	/// <summary>Finds the terminal's accessible group, or null while it does not show.</summary>
	private static AccessibilityNode? FindTerminal( SemanticAdapter semantics ) => Nodes( semantics ).FirstOrDefault( node => node.Name == TerminalName );

	/// <summary>Focuses the terminal in code, which also lets the window take native input after it lost the system focus.</summary>
	private static void FocusTerminal( Window window, SemanticAdapter semantics )
	{
		var terminal = FindTerminal( semantics );
		CoreChecks.Check( terminal is not null && UiInput.Focus( window, terminal ), "The terminal shows and takes the focus." );
	}

	/// <summary>Commits text to the terminal through native input.</summary>
	private static void Type( Window window, SemanticAdapter semantics, string text )
	{
		FocusTerminal( window, semantics );
		window.PlatformInput.CommitText( text );
		Pump( window );
	}

	/// <summary>Presses and releases a key on the terminal through native input.</summary>
	private static void Press( Window window, SemanticAdapter semantics, Key key, KeyModifiers modifiers )
	{
		FocusTerminal( window, semantics );
		window.PlatformInput.KeyPress( key, modifiers );
		window.PlatformInput.KeyRelease( key, modifiers );
		Pump( window );
	}

	/// <summary>Gets semantic nodes in source order.</summary>
	private static IEnumerable<AccessibilityNode> Nodes( SemanticAdapter semantics ) => semantics.Tree?.Root is { } root ? Flatten( root ) : [];

	/// <summary>Enumerates a semantic subtree.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Checks semantic text for a rendered fragment.</summary>
	private static bool HasText( SemanticAdapter semantics, string fragment ) =>
		Nodes( semantics ).Any( node => (node.Name?.Contains( fragment, StringComparison.Ordinal ) ?? false) || (node.Value?.Contains( fragment, StringComparison.Ordinal ) ?? false) );

	/// <summary>Pumps until a condition holds or the shell timeout passes, reporting the rendered text on failure.</summary>
	private static void PumpUntil( Window window, SemanticAdapter semantics, Func<bool> predicate, string message )
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		while ( !predicate() )
		{
			if ( timer.Elapsed > TimeSpan.FromSeconds( TimeoutSeconds ) )
			{
				var texts = Nodes( semantics ).Where( node => node.Role == AccessibilityRole.Text ).Select( node => node.Value );
				CoreChecks.Check( false, $"{message} Rendered text: {string.Join( " | ", texts )}" );
			}
			Pump( window );
		}

		Pump( window );
	}

	/// <summary>Settles layout and accessibility delivery.</summary>
	private static void Pump( Window window )
	{
		for ( var index = 0; index < SettleFrames; index++ )
		{
			window.Pump( 0.016 );
			Thread.Sleep( 10 );
		}
	}
}
