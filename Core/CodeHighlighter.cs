using Goo;

namespace Lokn.Core;

/// <summary>Classifies a run of source text for coloring.</summary>
public enum CodeToken
{
	/// <summary>Ordinary text, identifiers and punctuation.</summary>
	Plain,

	/// <summary>A language keyword or literal such as <c>true</c>.</summary>
	Keyword,

	/// <summary>A quoted string.</summary>
	String,

	/// <summary>A numeric literal.</summary>
	Number,

	/// <summary>A comment.</summary>
	Comment,

	/// <summary>An identifier followed by an opening parenthesis.</summary>
	Function,

	/// <summary>A capitalized type name, a shell variable, or a JSON key.</summary>
	Type,
}

/// <summary>Colors code a line at a time with a small lexer per language family; it favors speed and never fails over precision.</summary>
/// <remarks>Shared by every view that shows code, such as chat Markdown and the editor.</remarks>
public static class CodeHighlighter
{
	/// <summary>Holds the C-family grammar, shared by C#, JavaScript, TypeScript, Java, Go, Rust and C++.</summary>
	private static readonly Grammar CFamily = new(
		[
			"abstract", "as", "async", "await", "base", "bool", "break", "case", "catch", "class", "const", "continue", "default",
			"delegate", "do", "double", "else", "enum", "export", "extends", "false", "finally", "fn", "for", "foreach", "from",
			"func", "function", "get", "if", "impl", "implements", "import", "in", "init", "int", "interface", "internal", "is",
			"let", "long", "match", "mut", "namespace", "new", "null", "nullptr", "object", "of", "out", "override", "package",
			"private", "protected", "pub", "public", "readonly", "record", "ref", "return", "sealed", "self", "set", "static",
			"string", "struct", "super", "switch", "this", "throw", "true", "try", "type", "typeof", "undefined", "use", "using",
			"var", "virtual", "void", "when", "where", "while", "with", "yield",
		],
		LineComment: "//", HasBlockComments: true, HasBacktickStrings: true, HasTypes: true );

	/// <summary>Holds the Python grammar.</summary>
	private static readonly Grammar Python = new(
		[
			"and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "False",
			"finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not", "or", "pass",
			"raise", "return", "self", "True", "try", "while", "with", "yield",
		],
		LineComment: "#", HasTypes: true );

	/// <summary>Holds the shell grammar, shared by Bash and PowerShell; variables color as types.</summary>
	private static readonly Grammar Shell = new(
		[
			"case", "do", "done", "elif", "else", "esac", "export", "fi", "for", "foreach", "function", "if", "in", "local", "param",
			"return", "then", "until", "while", "$true", "$false", "$null",
		],
		LineComment: "#", HasVariables: true );

	/// <summary>Holds the JSON grammar; object keys color as types.</summary>
	private static readonly Grammar Json = new( ["true", "false", "null"], LineComment: "//", HasKeys: true );

	/// <summary>Gets the grammar for a file's extension, or null to show it uncolored.</summary>
	public static Grammar? FindForPath( string path ) => Path.GetExtension( path ).TrimStart( '.' ) is { Length: > 0 } extension ? Find( extension ) : null;

	/// <summary>Gets the grammar for a fenced-code or tool language name, or null to show it uncolored.</summary>
	public static Grammar? Find( string language ) => language.Trim().ToLowerInvariant() switch
	{
		"cs" or "csharp" or "c#" or "js" or "javascript" or "jsx" or "ts" or "typescript" or "tsx" or "java" or "go" or "rust"
			or "rs" or "c" or "cpp" or "c++" or "h" or "hpp" or "kotlin" or "swift" => CFamily,
		"py" or "python" => Python,
		"sh" or "bash" or "shell" or "zsh" or "ps1" or "powershell" or "pwsh" => Shell,
		"json" or "jsonc" or "jsonl" => Json,
		_ => null,
	};

	/// <summary>Splits one line into colored spans; <paramref name="isInComment"/> carries an open block comment to the next line.</summary>
	public static TextSpan[] BuildSpans( string line, Grammar grammar, ref bool isInComment )
	{
		var spans = new List<TextSpan>();
		foreach ( var (start, length, token) in Scan( line, grammar, ref isInComment ) )
			spans.Add( new( line.Substring( start, length ), BuildStyle( token )! ) );
		return [.. spans];
	}

	/// <summary>Lexes one line into consecutive, non-empty runs that cover it exactly.</summary>
	public static List<(int Start, int Length, CodeToken Token)> Scan( string line, Grammar grammar, ref bool isInComment )
	{
		var runs = new List<(int, int, CodeToken)>();
		var plain = 0;
		var index = 0;
		void Emit( int start, int end, CodeToken token )
		{
			if ( plain < start )
				runs.Add( (plain, start - plain, CodeToken.Plain) );
			runs.Add( (start, end - start, token) );
			plain = end;
		}

		if ( isInComment )
		{
			var close = line.IndexOf( "*/", StringComparison.Ordinal );
			index = close < 0 ? line.Length : close + 2;
			isInComment = close < 0;
			Emit( 0, index, CodeToken.Comment );
		}

		while ( index < line.Length )
		{
			var character = line[index];
			if ( grammar.HasBlockComments && string.CompareOrdinal( line, index, "/*", 0, 2 ) == 0 )
			{
				var close = line.IndexOf( "*/", index + 2, StringComparison.Ordinal );
				isInComment = close < 0;
				var end = close < 0 ? line.Length : close + 2;
				Emit( index, end, CodeToken.Comment );
				index = end;
			}
			else if ( string.CompareOrdinal( line, index, grammar.LineComment, 0, grammar.LineComment.Length ) == 0 && IsCommentStart( line, index, grammar ) )
			{
				Emit( index, line.Length, CodeToken.Comment );
				index = line.Length;
			}
			else if ( character is '"' or '\'' || character == '`' && grammar.HasBacktickStrings )
			{
				var end = index + 1;
				while ( end < line.Length && line[end] != character )
					end += line[end] == '\\' ? 2 : 1;
				end = Math.Min( end + 1, line.Length );
				var isKey = grammar.HasKeys && line.AsSpan( end ).TrimStart().StartsWith( ":" );
				Emit( index, end, isKey ? CodeToken.Type : CodeToken.String );
				index = end;
			}
			else if ( char.IsAsciiDigit( character ) && (index == 0 || !IsWordCharacter( line[index - 1] )) )
			{
				var end = index + 1;
				while ( end < line.Length && (char.IsAsciiLetterOrDigit( line[end] ) || line[end] is '.' or '_') )
					end++;
				Emit( index, end, CodeToken.Number );
				index = end;
			}
			else if ( char.IsLetter( character ) || character == '_' || character == '$' && (grammar.HasVariables || index + 1 < line.Length && IsWordCharacter( line[index + 1] )) )
			{
				var end = index + 1;
				while ( end < line.Length && (IsWordCharacter( line[end] ) || grammar.HasVariables && line[end] == '-' && end + 1 < line.Length && char.IsLetter( line[end + 1] )) )
					end++;
				var word = line[index..end];
				var token = grammar.Keywords.Contains( word ) ? CodeToken.Keyword
					: grammar.HasVariables && character == '$' ? CodeToken.Type
					: grammar.HasVariables && IsCommandStart( line, index ) ? CodeToken.Function
					: line.AsSpan( end ).TrimStart().StartsWith( "(" ) && !grammar.HasKeys ? CodeToken.Function
					: grammar.HasTypes && char.IsUpper( character ) && word.Length > 1 ? CodeToken.Type
					: CodeToken.Plain;
				if ( token != CodeToken.Plain )
					Emit( index, end, token );
				index = end;
			}
			else
				index++;
		}

		if ( plain < line.Length )
			runs.Add( (plain, line.Length - plain, CodeToken.Plain) );
		return runs;
	}

	/// <summary>Reports whether a comment marker starts a comment rather than sitting inside a word or a URL, such as # in C# or https://.</summary>
	private static bool IsCommentStart( string line, int index, Grammar grammar ) =>
		index == 0 || (grammar.LineComment == "#" ? char.IsWhiteSpace( line[index - 1] ) : line[index - 1] != ':');

	/// <summary>Reports whether a shell word is a command: the first word of the line or of a pipeline or command list.</summary>
	private static bool IsCommandStart( string line, int index )
	{
		var before = line.AsSpan( 0, index ).TrimEnd();
		return before.Length == 0 || before[^1] is '|' or ';' or '&' or '(' or '{';
	}

	/// <summary>Reports whether a character continues an identifier.</summary>
	private static bool IsWordCharacter( char character ) => char.IsLetterOrDigit( character ) || character == '_';

	/// <summary>Gets the span style for a token in the current theme; null for plain text, which keeps the line's own color.</summary>
	public static Style? BuildStyle( CodeToken token )
	{
		var theme = Theme.Current;
		return token switch
		{
			CodeToken.Keyword => new Style { Color = theme.AnsiMagenta },
			CodeToken.String => new Style { Color = theme.AnsiGreen },
			CodeToken.Number => new Style { Color = theme.AnsiYellow },
			CodeToken.Comment => new Style { Color = theme.Muted, FontStyle = FontStyle.Italic },
			CodeToken.Function => new Style { Color = theme.AnsiBlue },
			CodeToken.Type => new Style { Color = theme.AnsiCyan },
			_ => null,
		};
	}

	/// <summary>Describes how one language family is lexed.</summary>
	/// <param name="Keywords">The words colored as keywords, matched case-sensitively.</param>
	/// <param name="LineComment">The marker that comments out the rest of a line.</param>
	/// <param name="HasBlockComments">Whether <c>/* */</c> comments exist.</param>
	/// <param name="HasBacktickStrings">Whether backticks quote strings.</param>
	/// <param name="HasTypes">Whether capitalized identifiers color as types.</param>
	/// <param name="HasVariables">Whether <c>$name</c> and <c>-Flag</c>-joined words are shell variables and commands.</param>
	/// <param name="HasKeys">Whether a string followed by a colon is an object key.</param>
	public sealed record Grammar(
		HashSet<string> Keywords,
		string LineComment,
		bool HasBlockComments = false,
		bool HasBacktickStrings = false,
		bool HasTypes = false,
		bool HasVariables = false,
		bool HasKeys = false );
}
