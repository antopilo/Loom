using Goo;
using Lokn.Core;
using Lokn.Modules.Terminal;

namespace Lokn.Tests;

/// <summary>Exercises the VT emulator, key and pointer encoding, module registration, and a real pseudoconsole round trip.</summary>
internal static class TerminalChecks
{
	/// <summary>Defines how long a pseudoconsole check waits for the shell, in seconds.</summary>
	private const int ShellTimeoutSeconds = 20;

	/// <summary>Runs the terminal checks without opening a native window.</summary>
	internal static async Task RunAsync()
	{
		CheckPrinting();
		CheckStyles();
		CheckScreens();
		CheckReplies();
		CheckResize();
		CheckEncoding();
		CheckModule();
		await CheckPseudoConsoleAsync();
		Console.WriteLine( "PASS terminal emulation, input encoding, registration, and a real pseudoconsole shell round trip." );
	}

	/// <summary>Verifies wrapping, pending wraps, cursor addressing, wide and combining characters, line drawing and split sequences.</summary>
	private static void CheckPrinting()
	{
		var terminal = new TerminalEmulator( 10, 3, 100 );
		terminal.Feed( "abcdefghijKL" );
		CoreChecks.Check( Text( terminal, 0 ) == "abcdefghij" && Text( terminal, 1 ) == "KL" && terminal.GetScreenLine( 0 ).IsWrapped, "Text past the last column wraps onto a continuation line." );
		CoreChecks.Check( (terminal.CursorRow, terminal.CursorColumn) == (1, 2), "The cursor follows wrapped text." );
		CoreChecks.Check( terminal.BuildText( terminal.FirstScreenLine, 0, terminal.FirstScreenLine + 1, 9 ) == "abcdefghijKL", "Copying joins wrapped lines without a line break." );

		terminal = new TerminalEmulator( 10, 3, 100 );
		terminal.Feed( "0123456789\rX" );
		CoreChecks.Check( Text( terminal, 0 ) == "X123456789" && Text( terminal, 1 ) == "", "A carriage return after a full line cancels the pending wrap." );

		terminal.Feed( "\x1b[3;4HY\x1b[1;1H\x1b[K" );
		CoreChecks.Check( Text( terminal, 2 ) == "   Y" && Text( terminal, 0 ) == "", "Cursor addressing places text and erase-line clears it." );

		terminal = new TerminalEmulator( 10, 3, 100 );
		terminal.Feed( "a中b" );
		var cells = terminal.GetScreenLine( 0 ).Cells;
		CoreChecks.Check( cells[1].Text == "中" && cells[2].IsWideTail && cells[3].Text == "b" && terminal.CursorColumn == 4, "Wide characters take two cells." );
		terminal.Feed( "\x1b[1;3Hx" );
		CoreChecks.Check( cells[1].Text == " " && cells[2].Text == "x", "Overwriting half of a wide character blanks the other half." );

		terminal = new TerminalEmulator( 10, 3, 100 );
		terminal.Feed( "e\u0301\U0001F600!" );
		cells = terminal.GetScreenLine( 0 ).Cells;
		CoreChecks.Check( cells[0].Text == "e\u0301" && cells[1].Text == "\U0001F600" && cells[2].IsWideTail && cells[3].Text == "!", "Combining marks join their base and surrogate pairs form one wide character." );

		terminal = new TerminalEmulator( 10, 3, 100 );
		terminal.Feed( "\x1b(0lqk\x1b(Bq" );
		CoreChecks.Check( Text( terminal, 0 ) == "┌─┐q", "The DEC line-drawing set maps to box characters until ASCII is selected again." );

		terminal = new TerminalEmulator( 10, 3, 100 );
		terminal.Feed( "\x1b[3" );
		terminal.Feed( "1mR\x1b]0;ti" );
		terminal.Feed( "tle\x07Z" );
		CoreChecks.Check( terminal.GetScreenLine( 0 ).Cells[0].Style.Foreground == 1 && terminal.Title == "title" && Text( terminal, 0 ) == "RZ", "Sequences split across reads still apply." );
	}

	/// <summary>Verifies SGR rendition, 256 colors and 24-bit colors in semicolon and colon forms.</summary>
	private static void CheckStyles()
	{
		var terminal = new TerminalEmulator( 20, 2, 0 );
		terminal.Feed( "\x1b[1;4;31;48;2;1;2;3mA\x1b[0;38:2::10:20:30mB\x1b[38;5;200;7mC\x1b[4:0;27;39mD\x1b[mE" );
		var cells = terminal.GetScreenLine( 0 ).Cells;
		CoreChecks.Check( cells[0].Style == new CellStyle( 1, TerminalColor.FromRgb( 1, 2, 3 ), CellFlags.Bold | CellFlags.Underline ), "SGR sets rendition, palette and 24-bit colors." );
		CoreChecks.Check( cells[1].Style == new CellStyle( TerminalColor.FromRgb( 10, 20, 30 ), TerminalColor.Default, CellFlags.None ), "Colon-form 24-bit colors with a color space slot are read." );
		CoreChecks.Check( cells[2].Style == new CellStyle( 200, TerminalColor.Default, CellFlags.Inverse ), "256-color indices are read." );
		CoreChecks.Check( cells[3].Style == CellStyle.Default && cells[4].Style == CellStyle.Default, "Resets clear rendition and colors." );

		terminal.Feed( "\x1b[44m\x1b[2K" );
		CoreChecks.Check( terminal.GetScreenLine( 0 ).Cells[0].Style.Background == 4, "Erasing fills with the current background." );
	}

	/// <summary>Verifies history, scroll regions, line insertion and the alternate screen.</summary>
	private static void CheckScreens()
	{
		var terminal = new TerminalEmulator( 10, 3, 100 );
		terminal.Feed( "1\r\n2\r\n3\r\n4\r\n5" );
		CoreChecks.Check( terminal.History.Count == 2 && terminal.GetLine( terminal.FirstScreenLine - 2 )!.BuildText( 0, 10 ) == "1" && Text( terminal, 2 ) == "5", "Lines scrolled off the top go to history in order." );

		terminal.Feed( "\x1b[?1049h\x1b[HALT\r\n\r\n\r\n\r\n" );
		CoreChecks.Check( terminal.IsAlternateScreen && terminal.History.Count == 2, "The alternate screen keeps no history." );
		terminal.Feed( "\x1b[?1049l" );
		CoreChecks.Check( !terminal.IsAlternateScreen && Text( terminal, 2 ) == "5" && (terminal.CursorRow, terminal.CursorColumn) == (2, 1), "Leaving the alternate screen restores the main screen and cursor." );

		terminal = new TerminalEmulator( 10, 4, 100 );
		terminal.Feed( "a\r\nb\r\nc\r\nd\x1b[2;3r\x1b[3;1H\n" );
		CoreChecks.Check( Text( terminal, 0 ) == "a" && Text( terminal, 1 ) == "c" && Text( terminal, 2 ) == "" && Text( terminal, 3 ) == "d" && terminal.History.Count == 0, "Scrolling inside a region leaves lines outside it and history alone." );

		terminal = new TerminalEmulator( 10, 4, 100 );
		terminal.Feed( "a\r\nb\r\nc\x1b[2;1H\x1b[L" );
		CoreChecks.Check( Text( terminal, 1 ) == "" && Text( terminal, 2 ) == "b" && Text( terminal, 3 ) == "c", "Insert line pushes lines below the cursor down." );
		terminal.Feed( "\x1b[2M" );
		CoreChecks.Check( Text( terminal, 1 ) == "c" && Text( terminal, 2 ) == "", "Delete line pulls lines up." );
	}

	/// <summary>Verifies status, attribute, mode and color replies plus reported directories and clipboard writes.</summary>
	private static void CheckReplies()
	{
		var terminal = new TerminalEmulator( 10, 3, 100 )
		{
			DefaultBackground = Color.Rgb( 0x22, 0x22, 0x20 ),
		};
		var replies = new List<string>();
		var copies = new List<string>();
		terminal.Replied += replies.Add;
		terminal.Copied += copies.Add;
		terminal.Feed( "ab\x1b[6n\x1b[c\x1b[?2004h\x1b[?2004$p\x1b]11;?\x1b\\\x1b]52;c;aGVsbG8=\x07\x1b]7;file://host/C:/Work%20Dir\x07" );
		CoreChecks.Check( replies.SequenceEqual( ["\x1b[1;3R", "\x1b[?62;22c", "\x1b[?2004;1$y", "\x1b]11;rgb:2222/2222/2020\x1b\\"] ), "Cursor, device, mode and color queries are answered in order." );
		CoreChecks.Check( terminal.IsBracketedPaste && copies.SequenceEqual( ["hello"] ), "Modes are set and clipboard writes are decoded." );
		CoreChecks.Check( terminal.WorkingDirectory == @"C:\Work Dir", "A reported working directory is read from its file URL." );

		terminal.Feed( "\x1b[?1002h\x1b[?1006h\x1b[?1h\x1b=" );
		CoreChecks.Check( terminal.MouseTracking == MouseTracking.Drag && terminal.IsSgrMouse && terminal.IsApplicationCursorKeys && terminal.IsApplicationKeypad, "Pointer tracking and key modes are set." );
	}

	/// <summary>Verifies resizing keeps the cursor line visible and growing returns history lines.</summary>
	private static void CheckResize()
	{
		var terminal = new TerminalEmulator( 10, 5, 100 );
		terminal.Feed( "1\r\n2\r\n3\r\n4\r\n5" );
		terminal.Resize( 6, 3 );
		CoreChecks.Check( terminal.History.Count == 2 && Text( terminal, 2 ) == "5" && terminal.CursorRow == 2 && terminal.Columns == 6, "Shrinking moves lines above the cursor into history." );
		terminal.Resize( 6, 5 );
		CoreChecks.Check( terminal.History.Count == 0 && Text( terminal, 0 ) == "1" && terminal.CursorRow == 4, "Growing pulls history back above the cursor." );

		terminal = new TerminalEmulator( 10, 5, 100 );
		terminal.Feed( "top\x1b[H" );
		terminal.Resize( 10, 2 );
		CoreChecks.Check( Text( terminal, 0 ) == "top" && terminal.History.Count == 0, "Shrinking drops blank rows below the cursor first." );
	}

	/// <summary>Verifies key, paste and pointer encodings.</summary>
	private static void CheckEncoding()
	{
		static string? Encode( Key key, KeyModifiers modifiers, bool isApplication = false ) => TerminalInput.EncodeKey( key, modifiers, isApplication, false, Translate );

		var ctrl = new KeyModifiers { Ctrl = true };
		var alt = new KeyModifiers { Alt = true };
		var shift = new KeyModifiers { Shift = true };
		CoreChecks.Check( Encode( Key.Up, KeyModifiers.None ) == "\x1b[A" && Encode( Key.Up, KeyModifiers.None, isApplication: true ) == "\x1bOA" && Encode( Key.Left, ctrl ) == "\x1b[1;5D", "Cursor keys follow DECCKM and carry modifiers." );
		CoreChecks.Check( Encode( Key.F1, KeyModifiers.None ) == "\x1bOP" && Encode( Key.F5, KeyModifiers.None ) == "\x1b[15~" && Encode( Key.Delete, shift ) == "\x1b[3;2~", "Function and editing keys use xterm sequences." );
		CoreChecks.Check( Encode( Key.C, ctrl ) == "\x03" && Encode( Key.LeftBracket, ctrl ) == "\x1b" && Encode( Key.Space, ctrl ) == "\0" && Encode( Key.X, alt ) == "\x1bx", "Ctrl makes control characters and Alt prefixes ESC." );
		CoreChecks.Check( Encode( Key.Tab, shift ) == "\x1b[Z" && Encode( Key.Backspace, KeyModifiers.None ) == "\x7f" && Encode( Key.Backspace, ctrl ) == "\b" && Encode( Key.Enter, KeyModifiers.None ) == "\r", "Tab, Backspace and Enter send their terminal bytes." );
		CoreChecks.Check( Encode( Key.A, KeyModifiers.None ) is null && Encode( Key.A, shift ) is null, "Typing keys arrive as text input instead." );
		CoreChecks.Check( TerminalInput.EncodeKey( Key.Q, new KeyModifiers { Ctrl = true, Alt = true }, false, false, ( key, chord ) => chord == KeyChord.AltGraph ? "@" : Translate( key, chord ) ) is null, "AltGr characters are left to text input." );

		CoreChecks.Check( TerminalInput.EncodePaste( "a\r\nb\nc", isBracketed: false ) == "a\rb\rc" && TerminalInput.EncodePaste( "x\x1b[201~y", isBracketed: true ) == "\x1b[200~xy\x1b[201~", "Paste normalizes line breaks and cannot end its bracket early." );
		CoreChecks.Check( TerminalInput.EncodePointer( 0, KeyModifiers.None, 4, 2, isRelease: false, isSgr: true ) == "\x1b[<0;5;3M" && TerminalInput.EncodePointer( 0, ctrl, 4, 2, isRelease: true, isSgr: true ) == "\x1b[<16;5;3m", "SGR pointer reports carry the button, modifiers and release." );
		CoreChecks.Check( TerminalInput.EncodePointer( 2, KeyModifiers.None, 0, 0, isRelease: true, isSgr: false ) == "\x1b[M#!!" && TerminalInput.EncodePointer( 0, KeyModifiers.None, 300, 0, false, false ) is null, "Legacy pointer reports encode releases as button 3 and drop unencodable positions." );
	}

	/// <summary>Verifies the module registers its panel, settings and a command per detected shell.</summary>
	private static void CheckModule()
	{
		var app = new LoknApplication();
		CoreChecks.Check( app.Load( new TerminalModule() ), "The terminal module loads." );
		var commands = app.Commands.Items.Select( command => command.Id ).ToHashSet();
		CoreChecks.Check( commands.Contains( "terminal.open.shell" ) && app.Panels.Types.Any( type => type.Id == "terminal.shell" ), "The terminal panel and its open command are registered." );
		CoreChecks.Check( ShellProfile.Detect().Any( profile => profile.Name == "Command Prompt" ) && commands.Contains( "terminal.new-command-prompt" ), "Installed shells get their own command." );
		CoreChecks.Check( ShellProfile.Resolve( " nu -l " ) == new ShellProfile( "nu -l", "nu -l" ), "A configured command line is run as given." );
	}

	/// <summary>Runs Command Prompt in a real pseudoconsole: types a command, reads its echo through the emulator, and exits.</summary>
	private static async Task CheckPseudoConsoleAsync()
	{
		var terminal = new TerminalEmulator( 100, 30, 1000 );
		var output = new System.Text.StringBuilder();
		var exited = new TaskCompletionSource<int>( TaskCreationOptions.RunContinuationsAsynchronously );
		var directory = Path.GetTempPath();
		using var session = ShellSession.Start( "cmd.exe /d /k prompt $g", directory, terminal.Columns, terminal.Rows, text =>
		{
			lock ( output )
				output.Append( text );
		}, exited.SetResult );
		terminal.Replied += session.Write;

		session.Resize( 90, 30 );
		session.Write( "echo lokn-%COMSPEC:~-7%-check\r" );
		await WaitForAsync( () => ReadOutput( output ).Contains( "lokn-cmd.exe-check" ), "The shell's output arrives through the pseudoconsole." );
		session.Write( "exit 7\r" );
		var code = await exited.Task.WaitAsync( TimeSpan.FromSeconds( ShellTimeoutSeconds ) );

		terminal.Resize( 90, 30 );
		terminal.Feed( ReadOutput( output ) );
		var screen = string.Join( "\n", Enumerable.Range( 0, terminal.Rows ).Select( row => Text( terminal, row ) ) );
		CoreChecks.Check( code == 7, "The shell's exit code is reported after its output." );
		CoreChecks.Check( screen.Contains( "lokn-cmd.exe-check" ) && screen.Contains( ">echo lokn-%COMSPEC:~-7%-check" ), "The emulator shows the typed command and its result." );

		var failed = false;
		try
		{
			ShellSession.Start( "lokn-no-such-shell.exe", null, 80, 24, _ => { }, _ => { } ).Dispose();
		}
		catch ( System.ComponentModel.Win32Exception )
		{
			failed = true;
		}

		CoreChecks.Check( failed, "A missing shell fails to start with a Windows error." );
	}

	/// <summary>Polls a condition until it holds or the shell timeout passes.</summary>
	private static async Task WaitForAsync( Func<bool> condition, string message )
	{
		var deadline = DateTime.UtcNow.AddSeconds( ShellTimeoutSeconds );
		while ( !condition() )
		{
			CoreChecks.Check( DateTime.UtcNow < deadline, message );
			await Task.Delay( 50 );
		}
	}

	/// <summary>Reads collected output.</summary>
	private static string ReadOutput( System.Text.StringBuilder output )
	{
		lock ( output )
			return output.ToString();
	}

	/// <summary>Reads a screen row's text.</summary>
	private static string Text( TerminalEmulator terminal, int row ) => terminal.GetScreenLine( row ).BuildText( 0, terminal.Columns );

	/// <summary>Stands in for the keyboard layout with US characters.</summary>
	private static string? Translate( Key key, KeyChord chord )
	{
		if ( chord == KeyChord.AltGraph )
			return null;
		if ( key is >= Key.A and <= Key.Z )
			return ((char)((chord == KeyChord.Shift ? 'A' : 'a') + (key - Key.A))).ToString();

		return key switch
		{
			Key.LeftBracket => "[",
			Key.Space => " ",
			_ => null,
		};
	}
}
