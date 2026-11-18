namespace Lokn.Modules.Git;

/// <summary>Turns git's unified diff into the rows the History mode's commit details draw.</summary>
internal static class GitDiff
{
	/// <summary>Parses git's unified diff into one file per <c>diff --git</c> section, with gaps for unchanged lines between hunks.</summary>
	internal static IReadOnlyList<DiffFile> Parse( string patch ) => [.. GitPatch.Parse( patch ).Select( ToDiffFile )];

	/// <summary>Flattens a file's hunks into diff rows, with a gap before each hunk that unchanged lines precede.</summary>
	private static DiffFile ToDiffFile( GitFilePatch file )
	{
		var lines = new List<DiffLine>();
		var oldEnd = 0;
		foreach ( var hunk in file.Hunks )
		{
			// A hunk of a new file starts at old line zero; elsewhere the lines before it are unchanged.
			var start = Math.Max( hunk.OldStart, 1 );
			if ( start > oldEnd + 1 && hunk.OldStart > 0 )
				lines.Add( new( DiffLineKind.Gap, start - oldEnd - 1, "" ) );

			foreach ( var line in hunk.Lines )
			{
				if ( line.Kind != DiffLineKind.Added )
					oldEnd = line.OldNumber;
				lines.Add( new( line.Kind, line.Kind == DiffLineKind.Removed ? line.OldNumber : line.NewNumber, line.Text ) );
			}
		}
		var operation = file.Operation == "rename" ? "update" : file.Operation;
		return new( file.Path.Replace( '/', Path.DirectorySeparatorChar ), operation, lines, file.Note );
	}
}
