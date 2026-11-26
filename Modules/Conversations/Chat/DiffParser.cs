using System.Text.Json;
using System.Text.RegularExpressions;
using Lokn.Modules.Git;

namespace Lokn.Modules.Chat;

/// <summary>Reads Omp's file-changing tool calls into diff files.</summary>
internal static partial class DiffParser
{
	/// <summary>Reads the files a tool call changed: an edit's reported diff, or a write's content as added lines.</summary>
	/// <param name="name">The tool name.</param>
	/// <param name="arguments">The tool call's arguments.</param>
	/// <param name="result">The tool's result or live execution payload, when one exists.</param>
	/// <param name="root">The project directory that paths are shown relative to, or empty.</param>
	internal static IReadOnlyList<DiffFile> ParseTool( string name, JsonElement arguments, JsonElement? result, string root )
	{
		if ( result is { ValueKind: JsonValueKind.Object } failed && ChatContent.ReadText( failed, "isError" ) is "True" or "true" )
			return [];

		if ( name == "write" )
		{
			var path = ChatContent.ReadText( arguments, "path" );
			if ( path.Length == 0 || arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty( "content", out var content ) || content.ValueKind != JsonValueKind.String )
				return [];

			var lines = SplitLines( content.GetString() ?? "" );
			// A trailing newline ends the last line rather than starting an empty one.
			var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
			return [new( ComputePath( path, root ), "write", [.. lines.Take( count ).Select( ( line, index ) => new DiffLine( DiffLineKind.Added, index + 1, line ) )] )];
		}

		if ( result is not { ValueKind: JsonValueKind.Object } body || !body.TryGetProperty( "details", out var details ) || details.ValueKind != JsonValueKind.Object )
			return [];

		var files = new List<DiffFile>();
		if ( details.TryGetProperty( "perFileResults", out var perFile ) && perFile.ValueKind == JsonValueKind.Array )
		{
			foreach ( var item in perFile.EnumerateArray() )
				AddFile( files, item, ChatContent.ReadText( item, "path" ), root );
		}
		else
			AddFile( files, details, ChatContent.ReadText( details, "path", ComputeInputPath( arguments ) ), root );

		return files;
	}

	/// <summary>Adds one file when its reported diff has at least one line.</summary>
	private static void AddFile( List<DiffFile> files, JsonElement item, string path, string root )
	{
		var lines = Parse( ChatContent.ReadText( item, "diff" ) );
		if ( lines.Count > 0 )
			files.Add( new( ComputePath( path.Length > 0 ? path : "file", root ), ChatContent.ReadText( item, "op", "update" ), lines ) );
	}

	/// <summary>Parses Omp's numbered diff, whose rows read <c>" 42|text"</c>, <c>"-42|text"</c> or <c>"+42|text"</c>.</summary>
	/// <remarks>Context and removed rows carry old line numbers and added rows new ones; a jump in either starts a new hunk.</remarks>
	internal static IReadOnlyList<DiffLine> Parse( string diff )
	{
		var lines = new List<DiffLine>();
		int? lastOld = null;
		int? lastNew = null;
		var offset = 0;
		foreach ( var raw in SplitLines( diff ) )
		{
			var match = LinePattern().Match( raw );
			if ( !match.Success || !int.TryParse( match.Groups[2].ValueSpan, out var number ) )
				continue;

			var text = match.Groups[3].Value;
			var marker = match.Groups[1].ValueSpan[0];
			if ( marker == '+' )
			{
				AddGap( lines, lastNew, number );
				lines.Add( new( DiffLineKind.Added, number, text ) );
				lastNew = number;
				offset++;
				continue;
			}

			AddGap( lines, lastOld, number );
			lastOld = number;
			if ( marker == '-' )
			{
				lines.Add( new( DiffLineKind.Removed, number, text ) );
				offset--;
			}
			else
			{
				lines.Add( new( DiffLineKind.Context, number + offset, text ) );
				lastNew = number + offset;
			}
		}

		return lines;
	}

	/// <summary>Marks unchanged lines skipped between two hunks.</summary>
	private static void AddGap( List<DiffLine> lines, int? last, int number )
	{
		if ( last is { } previous && number != previous + 1 )
			lines.Add( new( DiffLineKind.Gap, Math.Max( 0, number - previous - 1 ), "" ) );
	}

	/// <summary>Shows a path relative to the project when it lies inside it.</summary>
	private static string ComputePath( string path, string root )
	{
		if ( root.Length == 0 || !Path.IsPathFullyQualified( path ) )
			return path;

		var relative = Path.GetRelativePath( root, path );
		return relative.StartsWith( "..", StringComparison.Ordinal ) || Path.IsPathFullyQualified( relative ) ? path : relative;
	}

	/// <summary>Reads the first <c>[path#TAG]</c> header of a hashline edit's input.</summary>
	internal static string ComputeInputPath( JsonElement arguments )
	{
		var match = InputPathPattern().Match( ChatContent.ReadText( arguments, "input" ) );
		return match.Success ? match.Groups[1].Value : "";
	}

	/// <summary>Splits text into lines regardless of its line endings.</summary>
	private static string[] SplitLines( string text ) => text.Replace( "\r\n", "\n" ).Split( '\n' );

	/// <summary>Matches one numbered diff row: its marker, line number and text.</summary>
	[GeneratedRegex( @"^([ +-])\s*(\d+)\|(.*)$" )]
	private static partial Regex LinePattern();

	/// <summary>Matches a hashline edit's file header.</summary>
	[GeneratedRegex( @"^\[(.+?)#[0-9A-Fa-f]{4}\]", RegexOptions.Multiline )]
	private static partial Regex InputPathPattern();
}
