using System.Text;
using System.Text.RegularExpressions;

namespace Lokn.Modules.Git;

/// <summary>Describes one row of a patch hunk.</summary>
/// <param name="Kind">Whether the line is unchanged, added or removed; never a gap.</param>
/// <param name="OldNumber">The old file's line number; zero for an added line.</param>
/// <param name="NewNumber">The new file's line number; zero for a removed line.</param>
/// <param name="Text">The line's content without its marker or a trailing carriage return.</param>
internal readonly record struct GitPatchLine( DiffLineKind Kind, int OldNumber, int NewNumber, string Text );

/// <summary>Describes one hunk of a file's patch, with the exact text git apply needs to apply it alone.</summary>
/// <param name="Header">The whole <c>@@ -a,b +c,d @@</c> line, section heading included, without a trailing carriage return.</param>
/// <param name="OldStart">The old file's first line of the hunk, or the line before an insertion into it.</param>
/// <param name="OldCount">How many old lines the hunk covers; one when git omits the count.</param>
/// <param name="NewStart">The new file's first line of the hunk, or the line before a removal from it.</param>
/// <param name="NewCount">How many new lines the hunk covers; one when git omits the count.</param>
/// <param name="Lines">The hunk's context, added and removed rows.</param>
/// <param name="Text">The hunk's raw patch text from its <c>@@</c> line through its last line, a <c>\ No newline at end of file</c> marker included, every line ending in a line feed and any carriage return kept.</param>
internal sealed record GitHunk( string Header, int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<GitPatchLine> Lines, string Text );

/// <summary>Describes one file's section of a patch.</summary>
/// <param name="Path">The repository-relative path with forward slashes: the new path, or the old one for a deletion.</param>
/// <param name="OldPath">The old path of a rename; null otherwise.</param>
/// <param name="Operation">The change: <c>update</c>, <c>create</c>, <c>delete</c> or <c>rename</c>.</param>
/// <param name="Header">The raw text from the <c>diff --git</c> line up to the first hunk, every line ending in a line feed and any carriage return kept.</param>
/// <param name="Hunks">The file's hunks in order.</param>
/// <param name="Note">Why the file has no lines, such as a binary file; null when it has them or simply changed no lines.</param>
internal sealed record GitFilePatch( string Path, string? OldPath, string Operation, string Header, IReadOnlyList<GitHunk> Hunks, string? Note )
{
	/// <summary>Gets the number of added lines over all hunks.</summary>
	public int Added { get; } = Hunks.Sum( hunk => hunk.Lines.Count( line => line.Kind == DiffLineKind.Added ) );

	/// <summary>Gets the number of removed lines over all hunks.</summary>
	public int Removed { get; } = Hunks.Sum( hunk => hunk.Lines.Count( line => line.Kind == DiffLineKind.Removed ) );
}

/// <summary>Parses git's unified patches into files and hunks, and builds the patch that applies one hunk.</summary>
internal static partial class GitPatch
{
	/// <summary>Parses the output of <c>git diff -p</c>, <c>git diff-tree -p</c> or <c>git diff --no-index</c> into one entry per <c>diff --git</c> section.</summary>
	/// <param name="patch">The patch text; lines are split on line feeds only, so carriage returns of CRLF content stay in the raw texts.</param>
	internal static IReadOnlyList<GitFilePatch> Parse( string patch )
	{
		var files = new List<GitFilePatch>();
		var rows = patch.Split( '\n' );
		// A final line feed ends the last line rather than starting an empty one.
		var count = rows.Length > 0 && rows[^1].Length == 0 ? rows.Length - 1 : rows.Length;

		string? path = null, oldPath = null, minusPath = null, note = null;
		var operation = "update";
		var header = new StringBuilder();
		var hunks = new List<GitHunk>();

		var hunkHeader = "";
		int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;
		int oldLine = 0, newLine = 0, oldLeft = 0, newLeft = 0;
		var hunkLines = new List<GitPatchLine>();
		var hunkText = new StringBuilder();
		var inHunk = false;

		void EndHunk()
		{
			if ( !inHunk )
				return;

			hunks.Add( new( hunkHeader, oldStart, oldCount, newStart, newCount, [.. hunkLines], hunkText.ToString() ) );
			inHunk = false;
		}

		void EndFile()
		{
			EndHunk();
			if ( path is not null )
				files.Add( new( path, oldPath, operation, header.ToString(), [.. hunks], note ) );
		}

		for ( var index = 0; index < count; index++ )
		{
			var raw = rows[index];
			var line = raw.EndsWith( '\r' ) ? raw[..^1] : raw;
			if ( inHunk )
			{
				if ( line.StartsWith( '\\' ) )
				{
					// "\ No newline at end of file" belongs to the line before it, so git apply needs it with the hunk.
					hunkText.Append( raw ).Append( '\n' );
					continue;
				}
				if ( (oldLeft > 0 || newLeft > 0) && ReadHunkLine( line ) )
				{
					hunkText.Append( raw ).Append( '\n' );
					continue;
				}
				EndHunk();
			}

			if ( line.StartsWith( "diff --git ", StringComparison.Ordinal ) )
			{
				EndFile();
				path = ParseGitLine( line["diff --git ".Length..] );
				oldPath = minusPath = note = null;
				operation = "update";
				header.Clear().Append( raw ).Append( '\n' );
				hunks.Clear();
				continue;
			}
			if ( path is null )
				continue;

			if ( line.StartsWith( "@@ ", StringComparison.Ordinal ) && HunkPattern().Match( line ) is { Success: true } match )
			{
				hunkHeader = line;
				oldStart = int.Parse( match.Groups[1].Value );
				oldCount = match.Groups[2].Success ? int.Parse( match.Groups[2].Value ) : 1;
				newStart = int.Parse( match.Groups[3].Value );
				newCount = match.Groups[4].Success ? int.Parse( match.Groups[4].Value ) : 1;
				(oldLine, newLine, oldLeft, newLeft) = (oldStart, newStart, oldCount, newCount);
				hunkLines.Clear();
				hunkText.Clear().Append( raw ).Append( '\n' );
				inHunk = true;
				continue;
			}
			// Lines after a file's hunks are not part of its header.
			if ( hunks.Count > 0 )
				continue;

			header.Append( raw ).Append( '\n' );
			if ( line.StartsWith( "new file mode", StringComparison.Ordinal ) )
				operation = "create";
			else if ( line.StartsWith( "deleted file mode", StringComparison.Ordinal ) )
				operation = "delete";
			else if ( line.StartsWith( "rename from ", StringComparison.Ordinal ) )
			{
				operation = "rename";
				oldPath = Unquote( line["rename from ".Length..] );
			}
			else if ( line.StartsWith( "rename to ", StringComparison.Ordinal ) )
			{
				operation = "rename";
				path = Unquote( line["rename to ".Length..] );
			}
			else if ( line.StartsWith( "--- ", StringComparison.Ordinal ) )
				minusPath = ParseName( line[4..], "a/" );
			else if ( line.StartsWith( "+++ ", StringComparison.Ordinal ) )
				path = ParseName( line[4..], "b/" ) ?? minusPath ?? path;
			else if ( line.StartsWith( "Binary files ", StringComparison.Ordinal ) || line == "GIT binary patch" )
				note = "Binary file";
		}
		EndFile();
		return files;

		bool ReadHunkLine( string content )
		{
			switch ( content.Length > 0 ? content[0] : ' ' )
			{
				case ' ':
					hunkLines.Add( new( DiffLineKind.Context, oldLine++, newLine++, content.Length > 0 ? content[1..] : "" ) );
					oldLeft--;
					newLeft--;
					return true;
				case '-':
					hunkLines.Add( new( DiffLineKind.Removed, oldLine++, 0, content[1..] ) );
					oldLeft--;
					return true;
				case '+':
					hunkLines.Add( new( DiffLineKind.Added, 0, newLine++, content[1..] ) );
					newLeft--;
					return true;
				default:
					return false;
			}
		}
	}

	/// <summary>Builds the patch that applies one hunk of a file: the file's header followed by the hunk.</summary>
	/// <param name="file">The file the hunk belongs to.</param>
	/// <param name="hunk">The hunk, from <paramref name="file"/>'s hunks.</param>
	internal static string BuildHunkPatch( GitFilePatch file, GitHunk hunk ) => file.Header + hunk.Text;

	/// <summary>Reads the new path from the text after <c>diff --git </c>, used when no later header names the file.</summary>
	private static string ParseGitLine( string text )
	{
		if ( text.EndsWith( '"' ) )
		{
			// A quoted new path starts after the old path, which is quoted too or ends at the last space before the opening quote.
			var start = text.StartsWith( '"' ) ? FindClosingQuote( text ) + 2 : text.LastIndexOf( " \"", StringComparison.Ordinal ) + 1;
			return StripPrefix( Unquote( text[Math.Clamp( start, 0, text.Length )..] ), "b/" );
		}

		var separator = text.LastIndexOf( " b/", StringComparison.Ordinal );
		if ( separator >= 0 )
			return text[(separator + 3)..];

		// Without prefixes both paths are the same, so the text is two equal halves around a space.
		var middle = text.Length / 2;
		return text.Length % 2 == 1 && text[middle] == ' ' && text[..middle] == text[(middle + 1)..] ? text[(middle + 1)..] : text;
	}

	/// <summary>Reads the path of a <c>---</c> or <c>+++</c> line, or null for <c>/dev/null</c>.</summary>
	private static string? ParseName( string text, string prefix )
	{
		if ( text == "/dev/null" )
			return null;

		// git ends a name containing spaces with a tab, which a quoted name never needs.
		return StripPrefix( text.StartsWith( '"' ) ? Unquote( text ) : text.TrimEnd( '\t' ), prefix );
	}

	/// <summary>Removes git's <c>a/</c> or <c>b/</c> path prefix when present.</summary>
	private static string StripPrefix( string path, string prefix ) => path.StartsWith( prefix, StringComparison.Ordinal ) ? path[prefix.Length..] : path;

	/// <summary>Finds the quote that closes a quoted path starting at index zero, skipping escaped characters.</summary>
	private static int FindClosingQuote( string text )
	{
		for ( var index = 1; index < text.Length; index++ )
		{
			if ( text[index] == '\\' )
				index++;
			else if ( text[index] == '"' )
				return index;
		}
		return text.Length - 1;
	}

	/// <summary>Decodes a path git quoted for its unusual characters: C escapes, and octal escapes holding UTF-8 bytes.</summary>
	private static string Unquote( string text )
	{
		if ( text.Length < 2 || text[0] != '"' || text[^1] != '"' )
			return text;

		var bytes = new List<byte>( text.Length );
		var index = 1;
		var end = text.Length - 1;
		while ( index < end )
		{
			var escape = text.IndexOf( '\\', index, end - index );
			var stop = escape < 0 ? end : escape;
			bytes.AddRange( Encoding.UTF8.GetBytes( text[index..stop] ) );
			if ( escape < 0 || escape + 1 >= end )
			{
				if ( escape >= 0 )
					bytes.Add( (byte)'\\' );
				break;
			}

			index = escape + 1;
			var marker = text[index];
			if ( marker is >= '0' and <= '7' )
			{
				var value = 0;
				for ( var digits = 0; digits < 3 && index < end && text[index] is >= '0' and <= '7'; digits++ )
					value = value * 8 + (text[index++] - '0');
				bytes.Add( (byte)value );
				continue;
			}

			index++;
			var control = marker switch
			{
				'a' => 7,
				'b' => 8,
				't' => 9,
				'n' => 10,
				'v' => 11,
				'f' => 12,
				'r' => 13,
				_ => -1,
			};
			if ( control >= 0 )
				bytes.Add( (byte)control );
			else
				bytes.AddRange( Encoding.UTF8.GetBytes( text[(index - 1)..index] ) );
		}
		return Encoding.UTF8.GetString( [.. bytes] );
	}

	/// <summary>Matches a hunk header's old and new start lines and their optional counts.</summary>
	[GeneratedRegex( @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@" )]
	private static partial Regex HunkPattern();
}
