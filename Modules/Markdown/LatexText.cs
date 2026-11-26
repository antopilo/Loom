using System.Text;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Turns the LaTeX math agents write, such as <c>\frac{a}{b}</c>, <c>x^2</c> or <c>\textcolor{red}{x}</c>, into Unicode text with optional colors.</summary>
/// <remarks>
/// It is not a typesetter: fractions become <c>a/b</c>, scripts use Unicode superscript and subscript letters where they exist and <c>^(…)</c> otherwise,
/// and an unknown command shows its name. Colors come from the theme's ANSI colors for named colors, so they read on every theme.
/// </remarks>
internal static class LatexText
{
	/// <summary>Holds one piece of converted math: its text and its optional text and background colors.</summary>
	/// <param name="Text">The text.</param>
	/// <param name="Color">The text color, or null for the surrounding color.</param>
	/// <param name="Background">The background color, or null for none.</param>
	internal readonly record struct Segment( string Text, Color? Color = null, Color? Background = null );

	/// <summary>Maps commands that stand for one symbol to it.</summary>
	private static readonly Dictionary<string, string> Symbols = new( StringComparer.Ordinal )
	{
		["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
		["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π",
		["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "ϕ",
		["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
		["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ",
		["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
		["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮", ["partial"] = "∂", ["nabla"] = "∇",
		["infty"] = "∞", ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["cdots"] = "⋯", ["ldots"] = "…", ["dots"] = "…",
		["vdots"] = "⋮", ["ddots"] = "⋱", ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘", ["bullet"] = "•", ["oplus"] = "⊕", ["otimes"] = "⊗",
		["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥", ["neq"] = "≠", ["ne"] = "≠", ["approx"] = "≈", ["equiv"] = "≡", ["sim"] = "∼",
		["simeq"] = "≃", ["cong"] = "≅", ["propto"] = "∝", ["ll"] = "≪", ["gg"] = "≫", ["prec"] = "≺", ["succ"] = "≻",
		["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂", ["supset"] = "⊃", ["subseteq"] = "⊆", ["supseteq"] = "⊇", ["cup"] = "∪",
		["cap"] = "∩", ["emptyset"] = "∅", ["varnothing"] = "∅", ["setminus"] = "∖", ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄",
		["neg"] = "¬", ["lnot"] = "¬", ["land"] = "∧", ["wedge"] = "∧", ["lor"] = "∨", ["vee"] = "∨", ["top"] = "⊤", ["bot"] = "⊥", ["perp"] = "⊥",
		["parallel"] = "∥", ["mid"] = "∣", ["angle"] = "∠", ["triangle"] = "△", ["square"] = "□", ["checkmark"] = "✓",
		["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["gets"] = "←", ["leftrightarrow"] = "↔", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐",
		["Leftrightarrow"] = "⇔", ["implies"] = "⟹", ["iff"] = "⟺", ["mapsto"] = "↦", ["uparrow"] = "↑", ["downarrow"] = "↓",
		["longrightarrow"] = "⟶", ["longleftarrow"] = "⟵",
		["langle"] = "⟨", ["rangle"] = "⟩", ["lceil"] = "⌈", ["rceil"] = "⌉", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lbrace"] = "{", ["rbrace"] = "}",
		["vert"] = "|", ["Vert"] = "‖", ["|"] = "‖", ["{"] = "{", ["}"] = "}", ["%"] = "%", ["$"] = "$", ["&"] = "&", ["#"] = "#", ["_"] = "_",
		["hbar"] = "ℏ", ["ell"] = "ℓ", ["Re"] = "ℜ", ["Im"] = "ℑ", ["aleph"] = "ℵ", ["deg"] = "°", ["degree"] = "°", ["prime"] = "′",
		["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["cot"] = "cot", ["sec"] = "sec", ["csc"] = "csc", ["arcsin"] = "arcsin", ["arccos"] = "arccos",
		["arctan"] = "arctan", ["sinh"] = "sinh", ["cosh"] = "cosh", ["tanh"] = "tanh", ["log"] = "log", ["ln"] = "ln", ["lg"] = "lg", ["exp"] = "exp",
		["lim"] = "lim", ["max"] = "max", ["min"] = "min", ["sup"] = "sup", ["inf"] = "inf", ["det"] = "det", ["dim"] = "dim", ["gcd"] = "gcd",
		["arg"] = "arg", ["Pr"] = "Pr", ["mod"] = "mod", ["bmod"] = "mod",
		[","] = "\u2009", [":"] = "\u2005", [";"] = "\u2005", ["!"] = "", [" "] = " ", ["quad"] = "\u2003", ["qquad"] = "\u2003\u2003", ["\\"] = "\n",
		["cr"] = "\n", ["newline"] = "\n",
	};

	/// <summary>Maps characters to their Unicode superscript.</summary>
	private static readonly Dictionary<char, char> Superscripts = Zip( "0123456789+-=()niabcdefghijklmoprstuvwxyzABDEGHIJKLMNOPRTUVW",
		"⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐᵒᵖʳˢᵗᵘᵛʷˣʸᶻᴬᴮᴰᴱᴳᴴᴵᴶᴷᴸᴹᴺᴼᴾᴿᵀᵁⱽᵂ" );

	/// <summary>Maps characters to their Unicode subscript.</summary>
	private static readonly Dictionary<char, char> Subscripts = Zip( "0123456789+-=()aehijklmnoprstuvx", "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ" );

	/// <summary>Maps letters to their double-struck form, as <c>\mathbb</c> writes them.</summary>
	private static readonly Dictionary<char, string> DoubleStruck = new()
	{
		['C'] = "ℂ", ['H'] = "ℍ", ['N'] = "ℕ", ['P'] = "ℙ", ['Q'] = "ℚ", ['R'] = "ℝ", ['Z'] = "ℤ", ['1'] = "𝟙",
	};

	/// <summary>Reports whether the LaTeX being converted is text, such as a color command in prose, whose spaces count; math ignores its spaces.</summary>
	[ThreadStatic]
	private static bool _isText;

	/// <summary>Converts LaTeX math or a color command to text segments.</summary>
	/// <param name="latex">The LaTeX, without its <c>$</c> delimiters.</param>
	/// <param name="isText">Whether it is text, such as <c>\textcolor{red}{two words}</c> in prose, whose spaces show; math drops them.</param>
	internal static IReadOnlyList<Segment> Convert( string latex, bool isText = false )
	{
		var segments = new List<Segment>();
		var position = 0;
		var wasText = _isText;
		_isText = isText;
		try
		{
			Append( latex, ref position, latex.Length, null, null, segments );
		}
		finally
		{
			_isText = wasText;
		}
		return segments;
	}

	/// <summary>Converts LaTeX math to plain text, dropping colors.</summary>
	internal static string ConvertPlain( string latex ) => string.Concat( Convert( latex ).Select( segment => segment.Text ) );

	/// <summary>Appends the segments of LaTeX from a position up to an end, in the given colors.</summary>
	private static void Append( string text, ref int position, int end, Color? color, Color? background, List<Segment> segments )
	{
		var plain = new StringBuilder();
		void Flush()
		{
			if ( plain.Length == 0 )
				return;
			if ( segments.Count > 0 && segments[^1].Color == color && segments[^1].Background == background )
				segments[^1] = segments[^1] with { Text = segments[^1].Text + plain };
			else
				segments.Add( new( plain.ToString(), color, background ) );
			plain.Clear();
		}

		while ( position < end )
		{
			var character = text[position];
			if ( character == '\\' )
			{
				var name = ReadCommand( text, ref position, end );
				switch ( name )
				{
					case "textcolor" or "color":
					{
						// A model such as [HTML] before the color only says how it is written; the color reads either way.
						if ( SkipSpaces( text, ref position, end ) && text[position] == '[' )
							ReadUntil( text, ref position, end, ']' );
						var named = ParseColor( ReadGroup( text, ref position, end ) );
						Flush();
						AppendGroup( text, ref position, end, named ?? color, background, segments );
						continue;
					}
					case "colorbox":
					{
						var fill = ParseColor( ReadGroup( text, ref position, end ) );
						Flush();
						AppendGroup( text, ref position, end, color, fill?.WithAlpha( 0.35 ) ?? background, segments );
						continue;
					}
					case "fcolorbox":
					{
						var frame = ParseColor( ReadGroup( text, ref position, end ) );
						var fill = ParseColor( ReadGroup( text, ref position, end ) );
						Flush();
						AppendGroup( text, ref position, end, frame ?? color, fill?.WithAlpha( 0.35 ) ?? background, segments );
						continue;
					}
					case "frac" or "dfrac" or "tfrac":
					{
						var numerator = ConvertPlain( ReadGroup( text, ref position, end ) );
						var denominator = ConvertPlain( ReadGroup( text, ref position, end ) );
						plain.Append( Wrap( numerator ) ).Append( '/' ).Append( Wrap( denominator ) );
						continue;
					}
					case "sqrt":
					{
						var degree = SkipSpaces( text, ref position, end ) && text[position] == '[' ? ReadUntil( text, ref position, end, ']' ) : "";
						var radicand = ConvertPlain( ReadGroup( text, ref position, end ) );
						plain.Append( degree switch { "" or "2" => "", "3" => "∛", "4" => "∜", _ => ToScript( degree, Superscripts ) } );
						plain.Append( degree is "3" or "4" ? "" : "√" ).Append( Wrap( radicand ) );
						continue;
					}
					case "text" or "textrm" or "textit" or "textbf" or "texttt" or "mathrm" or "mathit" or "mathbf" or "mathsf" or "mathtt" or "operatorname"
						or "boldsymbol" or "bm" or "mbox" or "emph" or "displaystyle" or "textstyle" or "mathcal" or "mathscr" or "mathfrak" or "hat" or "bar"
						or "vec" or "tilde" or "dot" or "ddot" or "overline" or "underline" or "widehat" or "widetilde":
					{
						if ( name is "displaystyle" or "textstyle" )
							continue;
						var inner = ReadGroup( text, ref position, end );
						var converted = name.StartsWith( "text", StringComparison.Ordinal ) || name is "mbox" or "emph" ? inner : ConvertPlain( inner );
						plain.Append( converted ).Append( name switch
						{
							"hat" or "widehat" => "\u0302",
							"bar" or "overline" => "\u0305",
							"vec" => "\u20D7",
							"tilde" or "widetilde" => "\u0303",
							"dot" => "\u0307",
							"ddot" => "\u0308",
							_ => "",
						} );
						continue;
					}
					case "mathbb":
					{
						foreach ( var letter in ConvertPlain( ReadGroup( text, ref position, end ) ) )
							plain.Append( DoubleStruck.TryGetValue( letter, out var struck ) ? struck : letter.ToString() );
						continue;
					}
					case "left" or "right" or "big" or "Big" or "bigg" or "Bigg" or "bigl" or "bigr" or "Bigl" or "Bigr":
					{
						// The delimiter after it shows as itself; a "." delimiter shows nothing.
						if ( position < end && text[position] == '.' )
							position++;
						continue;
					}
					case "begin" or "end":
					{
						ReadGroup( text, ref position, end );
						continue;
					}
					case "binom":
					{
						var top = ConvertPlain( ReadGroup( text, ref position, end ) );
						var bottom = ConvertPlain( ReadGroup( text, ref position, end ) );
						plain.Append( '(' ).Append( top ).Append( " choose " ).Append( bottom ).Append( ')' );
						continue;
					}
					case "not":
					{
						var negated = position < end ? ReadAtom( text, ref position, end ) : "";
						plain.Append( negated ).Append( '\u0338' );
						continue;
					}
					default:
						plain.Append( Symbols.TryGetValue( name, out var symbol ) ? symbol : name );
						continue;
				}
			}

			if ( character is '^' or '_' )
			{
				position++;
				var script = ConvertPlain( ReadAtom( text, ref position, end ) );
				plain.Append( ToScript( script, character == '^' ? Superscripts : Subscripts, character ) );
				continue;
			}

			if ( character == '{' )
			{
				Flush();
				AppendGroup( text, ref position, end, color, background, segments );
				continue;
			}

			if ( character is '}' )
			{
				position++;
				continue;
			}

			// Math ignores spaces; an alignment mark in an aligned block separates columns.
			if ( !_isText && char.IsWhiteSpace( character ) && character != '\n' )
			{
				position++;
				continue;
			}
			plain.Append( character == '&' ? ' ' : character == '~' ? '\u00A0' : character );
			position++;
		}
		Flush();
	}

	/// <summary>Appends a braced group, or the next single token when no brace follows, in the given colors.</summary>
	private static void AppendGroup( string text, ref int position, int end, Color? color, Color? background, List<Segment> segments )
	{
		if ( !SkipSpaces( text, ref position, end ) )
			return;
		if ( text[position] != '{' )
		{
			var atom = ReadAtom( text, ref position, end );
			var atomPosition = 0;
			Append( atom, ref atomPosition, atom.Length, color, background, segments );
			return;
		}

		var close = FindClose( text, position, end );
		position++;
		Append( text, ref position, close, color, background, segments );
		position = Math.Min( close + 1, end );
	}

	/// <summary>Reads a command's name after its backslash: a run of letters, or one other character.</summary>
	private static string ReadCommand( string text, ref int position, int end )
	{
		position++;
		if ( position >= end )
			return "";
		var start = position;
		if ( !char.IsAsciiLetter( text[position] ) )
			return text[position++].ToString();
		while ( position < end && char.IsAsciiLetter( text[position] ) )
			position++;
		var name = text[start..position];
		// A space after a command name only ends the name.
		if ( position < end && text[position] == ' ' )
			position++;
		return name;
	}

	/// <summary>Reads a braced group's source without its braces, or the next single token when no brace follows.</summary>
	private static string ReadGroup( string text, ref int position, int end )
	{
		if ( !SkipSpaces( text, ref position, end ) )
			return "";
		if ( text[position] != '{' )
			return ReadAtom( text, ref position, end );

		var close = FindClose( text, position, end );
		var inner = text[(position + 1)..close];
		position = Math.Min( close + 1, end );
		return inner;
	}

	/// <summary>Reads the next token's source: a braced group's inside, a command with its argument, or one character.</summary>
	private static string ReadAtom( string text, ref int position, int end )
	{
		if ( !SkipSpaces( text, ref position, end ) )
			return "";
		if ( text[position] == '{' )
			return ReadGroup( text, ref position, end );
		if ( text[position] == '\\' )
		{
			var start = position;
			var name = ReadCommand( text, ref position, end );
			// A command that takes an argument, such as \frac, keeps its arguments.
			var arguments = name switch { "frac" or "dfrac" or "tfrac" or "binom" => 2, "sqrt" or "mathbb" or "mathbf" or "mathrm" or "text" or "vec" or "hat" or "bar" => 1, _ => 0 };
			for ( var index = 0; index < arguments; index++ )
				ReadGroup( text, ref position, end );
			return text[start..position];
		}
		var single = char.IsSurrogatePair( text, position ) ? 2 : 1;
		var atom = text.Substring( position, single );
		position += single;
		return atom;
	}

	/// <summary>Reads up to a closing character, past it, and returns what came before.</summary>
	private static string ReadUntil( string text, ref int position, int end, char close )
	{
		var start = position + 1;
		var found = text.IndexOf( close, start );
		var stop = found < 0 || found >= end ? end : found;
		position = Math.Min( stop + 1, end );
		return text[start..stop];
	}

	/// <summary>Skips spaces and reports whether anything follows before the end.</summary>
	private static bool SkipSpaces( string text, ref int position, int end )
	{
		while ( position < end && char.IsWhiteSpace( text[position] ) )
			position++;
		return position < end;
	}

	/// <summary>Finds the brace that closes the one at a position, or the end when none does.</summary>
	private static int FindClose( string text, int open, int end )
	{
		var depth = 0;
		for ( var index = open; index < end; index++ )
		{
			if ( text[index] == '\\' )
			{
				index++;
				continue;
			}
			if ( text[index] == '{' )
				depth++;
			else if ( text[index] == '}' && --depth == 0 )
				return index;
		}
		return end;
	}

	/// <summary>Writes a script in Unicode script letters when every character has one, else as <c>^(…)</c> or <c>_(…)</c>.</summary>
	private static string ToScript( string script, Dictionary<char, char> letters, char mark = '^' )
	{
		if ( script.Length > 0 && script.All( letters.ContainsKey ) )
			return string.Concat( script.Select( letter => letters[letter] ) );
		return script.Length == 1 ? $"{mark}{script}" : $"{mark}({script})";
	}

	/// <summary>Puts parentheses around a fraction part longer than one term.</summary>
	private static string Wrap( string part ) => part.Length <= 1 || part.All( char.IsLetterOrDigit ) ? part : $"({part})";

	/// <summary>Reads a color: a named color in the theme's ANSI colors, or any color <see cref="Color.TryParse"/> reads, such as <c>#ff8800</c>; null when unknown.</summary>
	internal static Color? ParseColor( string name )
	{
		var theme = Theme.Current;
		var trimmed = name.Trim();
		return trimmed.ToLowerInvariant() switch
		{
			"red" => theme.AnsiRed,
			"green" => theme.AnsiGreen,
			"blue" => theme.AnsiBlue,
			"yellow" => theme.AnsiYellow,
			"orange" => theme.Accent,
			"purple" or "magenta" or "violet" => theme.AnsiMagenta,
			"cyan" or "teal" => theme.AnsiCyan,
			"gray" or "grey" => theme.Muted,
			"black" => theme.AnsiBlack,
			"white" => theme.AnsiWhite,
			_ => Color.TryParse( trimmed ) is { } parsed ? parsed
				: trimmed.Length == 6 && trimmed.All( char.IsAsciiHexDigit ) ? Color.TryParse( "#" + trimmed ) : null,
		};
	}

	/// <summary>Builds a character map from two strings of the same length in text elements.</summary>
	private static Dictionary<char, char> Zip( string from, string to )
	{
		var map = new Dictionary<char, char>();
		for ( var index = 0; index < from.Length && index < to.Length; index++ )
			map.TryAdd( from[index], to[index] );
		return map;
	}
}
