using System.Text.RegularExpressions;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Marks the lines of a saved file that differ from git's HEAD, for the editor's gutter.</summary>
internal static partial class GitLineMarks
{
	/// <summary>Gets the source the Git module contributes to the editor.</summary>
	internal static LineMarkSource Source { get; } = new( "git", ComputeAsync );

	/// <summary>Computes a file's changes against HEAD; a file outside a repository, untracked or unchanged has none.</summary>
	internal static async Task<IReadOnlyList<LineMark>> ComputeAsync( string path, CancellationToken cancellation )
	{
		if ( Path.GetDirectoryName( path ) is not { } directory || !Directory.Exists( directory ) )
			return [];

		var diff = await GitProcess.TryRunAsync( directory, cancellation, "diff", "--no-color", "--no-ext-diff", "-U0", "HEAD", "--", Path.GetFileName( path ) );
		return diff is null ? [] : Parse( diff );
	}

	/// <summary>Reads a zero-context diff into marks: added runs, modified runs with the lines they replaced, and deletions between lines.</summary>
	internal static IReadOnlyList<LineMark> Parse( string diff )
	{
		var marks = new List<LineMark>();
		var lines = diff.Split( '\n' );
		for ( var index = 0; index < lines.Length; index++ )
		{
			if ( HunkPattern().Match( lines[index] ) is not { Success: true } hunk )
				continue;

			var oldCount = hunk.Groups[2].Success ? int.Parse( hunk.Groups[2].Value ) : 1;
			var newStart = int.Parse( hunk.Groups[3].Value );
			var newCount = hunk.Groups[4].Success ? int.Parse( hunk.Groups[4].Value ) : 1;
			var removed = new List<string>();
			for ( var next = index + 1; next < lines.Length && !lines[next].StartsWith( "@@", StringComparison.Ordinal ); next++ )
			{
				if ( lines[next].StartsWith( '-' ) )
					removed.Add( lines[next][1..].TrimEnd( '\r' ) );
			}

			if ( newCount == 0 )
				// "+12,0" means after line 12, which is line 12 zero-based.
				marks.Add( new( newStart, LineChange.Deleted, removed ) );
			else
			{
				var change = oldCount == 0 ? LineChange.Added : LineChange.Modified;
				for ( var line = 0; line < newCount; line++ )
					marks.Add( new( newStart - 1 + line, change, line == 0 ? removed : [] ) );
			}
		}

		return marks;
	}

	/// <summary>Matches a hunk header such as <c>@@ -3,2 +3,4 @@</c>.</summary>
	[GeneratedRegex( @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@" )]
	private static partial Regex HunkPattern();
}
