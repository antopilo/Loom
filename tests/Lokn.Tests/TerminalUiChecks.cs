using Goo;
using Lokn.Core;
using Lokn.Modules.Terminal;

namespace Lokn.Tests;

/// <summary>Drives a real terminal panel running Command Prompt through a native Goo window.</summary>
internal static class TerminalUiChecks
{
	/// <summary>Defines the frame count used to settle layout and accessibility updates.</summary>
	private const int SettleFrames = 16;

	/// <summary>Defines how long the shell may take to respond, in seconds.</summary>
	private const int TimeoutSeconds = 20;

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
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Root = new ShellView( app ),
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		window.Open();
		Pump( window );

		var input = window.PlatformInput;
		PumpUntil( window, semantics, () => Nodes( semantics ).Any( node => (node.Value ?? node.Name)?.Trim() == ">" ), "The shell's prompt renders." );
		input.CommitText( "echo lokn-%COMSPEC:~-7%-ui" );
		Press( window, Key.Enter, KeyModifiers.None );
		PumpUntil( window, semantics, () => HasText( semantics, "lokn-cmd.exe-ui" ), "Typed text reaches the shell and its output renders." );

		// A long run must end where the next run starts, or text drifts out of its cells across a line.
		input.CommitText( $"prompt {new string( 'W', 60 )}$e[31mX$e[0m$g" );
		Press( window, Key.Enter, KeyModifiers.None );
		PumpUntil( window, semantics, () => Nodes( semantics ).Any( node => node.Value == "X" ), "A colored prompt renders as separate runs." );
		var run = Nodes( semantics ).Last( node => node.Value == new string( 'W', 60 ) ).Bounds;
		var next = Nodes( semantics ).Last( node => node.Value == "X" ).Bounds;
		CoreChecks.Check( Math.Abs( run.X + run.Width - next.X ) <= 1, $"Cells match the font's advance: a 60-cell run ends at {run.X + run.Width:0.00} and the next cell starts at {next.X:0.00}." );

		Press( window, Key.W, KeyModifiers.Primary );
		CoreChecks.Check( app.Panels.Panels.Count == 1, "Ctrl+W goes to the focused shell instead of closing the panel." );
		Press( window, Key.Escape, KeyModifiers.None );

		var capture = Array.IndexOf( args, "--capture" );
		if ( capture >= 0 && capture + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[capture + 1], "terminal" );

		input.CommitText( "exit" );
		Press( window, Key.Enter, KeyModifiers.None );
		PumpUntil( window, semantics, () => app.Panels.Panels.Count == 0, "A clean shell exit closes its panel." );
		CoreChecks.Check( app.LastError is null, "The terminal runs without contained failures." );
		window.RequestClose();
		Pump( window );
		Console.WriteLine( "PASS native terminal prompt, typed input and output, claimed Ctrl+W, and exit-to-close." );
	}

	/// <summary>Presses and releases a key through native input.</summary>
	private static void Press( Window window, Key key, KeyModifiers modifiers )
	{
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
