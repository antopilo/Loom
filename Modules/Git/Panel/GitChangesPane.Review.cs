using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Keeps the operator's review notes on diff lines and finds the linked review's findings, which the Git panel's file view marks.</summary>
internal sealed partial class GitChangesPane
{
	/// <summary>Stores operator review notes by stable diff row.</summary>
	private readonly Dictionary<GitReviewLine, string> _annotations = [];

	/// <summary>Stores the diff row whose review note is being edited, or null.</summary>
	private GitReviewLine? _editingAnnotation;

	/// <summary>Stores the controlled review-note editor value.</summary>
	private string _annotationText = "";

	/// <summary>Gets a number that changes whenever a note is saved, removed or opened for editing, so rows that show notes rebuild.</summary>
	internal int AnnotationRevision { get; private set; }

	/// <summary>Builds the editor for the note being edited when it belongs to a path; null otherwise.</summary>
	/// <param name="path">The repository-relative path with the platform's separators.</param>
	internal Blob? BuildAnnotationEditor( string path )
	{
		_ = _version.Value;
		if ( _editingAnnotation is not { } annotation || !string.Equals( annotation.Path, path, StringComparison.OrdinalIgnoreCase ) )
			return null;

		var theme = Theme.Current;
		return new Container
		{
			Key = "git-review-annotation",
			FlexShrink = 0,
			Padding = theme.Spacing,
			Gap = theme.Spacing / 2,
			BackgroundColor = theme.Accent.WithAlpha( 0.08 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				new Text { Content = $"Review note · {annotation.Path}:{annotation.Line}", FontWeight = 600 },
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Children =
					[
						new TextEntry
						{
							Value = _annotationText,
							Controlled = true,
							Placeholder = "What should Omp's reviewers check here?",
							AccessibilityName = "Review annotation",
							FlexGrow = 1,
							MinWidth = 0,
							MinHeight = theme.ControlHeight,
							Padding = new Edges( 0, theme.Spacing ),
							BackgroundColor = theme.Raised,
							BorderRadius = theme.Radius,
							AutoFocus = true,
							OnChange = value =>
							{
								_annotationText = value;
								MarkChanged();
							},
							OnSubmit = _ => _context.Run( SaveAnnotation ),
						},
						theme.BuildButton( "Save", () => _context.Run( SaveAnnotation ) ),
						theme.BuildButton( "Cancel", () => _context.Run( CancelAnnotation ) ),
					],
				},
			],
		};
	}

	/// <summary>Opens the note editor on one line, with its saved note.</summary>
	/// <param name="path">The repository-relative path with the platform's separators.</param>
	/// <param name="line">The line: its kind, its new number or the old one for a removed line, and its text.</param>
	internal void BeginAnnotation( string path, DiffLine line )
	{
		var key = new GitReviewLine( path, line.Number, line.Kind, line.Text );
		_editingAnnotation = key;
		_annotationText = _annotations.GetValueOrDefault( key, "" );
		AnnotationRevision++;
		MarkChanged();
	}

	/// <summary>Saves the edited line's trimmed note, or removes it when blank.</summary>
	private void SaveAnnotation()
	{
		if ( _editingAnnotation is not { } key )
			return;
		var note = _annotationText.Trim();
		if ( note.Length == 0 )
			_annotations.Remove( key );
		else
			_annotations[key] = note;
		_editingAnnotation = null;
		_annotationText = "";
		AnnotationRevision++;
		MarkChanged();
	}

	/// <summary>Closes the note editor without changing its saved note.</summary>
	private void CancelAnnotation()
	{
		_editingAnnotation = null;
		_annotationText = "";
		AnnotationRevision++;
		MarkChanged();
	}

	/// <summary>Gets one line's marker: its highest P0-P3 finding and a dot when it has a note; null when it has neither.</summary>
	/// <param name="path">The repository-relative path with the platform's separators.</param>
	/// <param name="line">The line, as given to <see cref="BeginAnnotation"/>.</param>
	internal string? DescribeReviewMarker( string path, DiffLine line )
	{
		var annotation = new GitReviewLine( path, line.Number, line.Kind, line.Text );
		var finding = FindReviewFinding( path, line.Number );
		var priority = finding is null ? null : $"P{Math.Clamp( finding.Priority, 0, 3 )}";
		return (priority, _annotations.ContainsKey( annotation )) switch
		{
			(null, false) => null,
			(null, true) => "•",
			(var value, false) => value,
			(var value, true) => value + " •",
		};
	}

	/// <summary>Finds the highest-priority review result covering one repository-relative line.</summary>
	private GitReviewFinding? FindReviewFinding( string path, int line )
	{
		GitReviewFinding? best = null;
		foreach ( var finding in _reviewFindings() )
		{
			if ( PathsMatch( finding.FilePath, path ) && line >= finding.LineStart && line <= Math.Max( finding.LineStart, finding.LineEnd )
				&& (best is null || finding.Priority < best.Priority) )
				best = finding;
		}
		return best;
	}

	/// <summary>Finds the working-tree change a finding names, unstaged first; null when the working tree no longer changes its file.</summary>
	/// <param name="finding">The finding.</param>
	/// <param name="staged">Set to whether the change found is staged.</param>
	internal GitFileChange? FindChange( GitReviewFinding finding, out bool staged )
	{
		staged = false;
		if ( _changes is not { } changes )
			return null;

		if ( changes.Unstaged.FirstOrDefault( item => PathsMatch( finding.FilePath, item.Path ) ) is { } unstaged )
			return unstaged;

		staged = true;
		return changes.Staged.FirstOrDefault( item => PathsMatch( finding.FilePath, item.Path ) );
	}

	/// <summary>Matches review paths regardless of slash direction or a leading repository prefix, without allocating normalized copies per visible line.</summary>
	private static bool PathsMatch( string findingPath, string diffPath )
	{
		var findingStart = RelativePathStart( findingPath );
		var diffStart = RelativePathStart( diffPath );
		var findingLength = findingPath.Length - findingStart;
		var diffLength = diffPath.Length - diffStart;
		if ( findingLength < diffLength )
			return false;

		var findingOffset = findingPath.Length - diffLength;
		for ( var index = 0; index < diffLength; index++ )
		{
			var findingCharacter = NormalizePathCharacter( findingPath[findingOffset + index] );
			var diffCharacter = NormalizePathCharacter( diffPath[diffStart + index] );
			if ( char.ToUpperInvariant( findingCharacter ) != char.ToUpperInvariant( diffCharacter ) )
				return false;
		}

		return findingOffset == findingStart || NormalizePathCharacter( findingPath[findingOffset - 1] ) == '/';
	}

	/// <summary>Skips only relative-path prefixes, preserving meaningful leading periods such as <c>.github</c>.</summary>
	private static int RelativePathStart( string path )
	{
		var start = 0;
		while ( start < path.Length && NormalizePathCharacter( path[start] ) == '/' )
			start++;
		while ( start + 1 < path.Length && path[start] == '.' && NormalizePathCharacter( path[start + 1] ) == '/' )
		{
			start += 2;
			while ( start < path.Length && NormalizePathCharacter( path[start] ) == '/' )
				start++;
		}
		return start;
	}

	/// <summary>Normalizes one path separator without creating a replacement string.</summary>
	private static char NormalizePathCharacter( char character ) => character == '\\' ? '/' : character;

	/// <summary>Gets the saved operator annotations in stable file and line order.</summary>
	internal IReadOnlyList<GitReviewAnnotation> ReviewAnnotations =>
		[.. _annotations.OrderBy( item => item.Key.Path, StringComparer.OrdinalIgnoreCase ).ThenBy( item => item.Key.Line ).Select( item => new GitReviewAnnotation( item.Key, item.Value ) )];

	/// <summary>Gets the number of operator notes included with the next review.</summary>
	internal int ReviewAnnotationCount => _annotations.Count;

	/// <summary>Gets whether the shown working tree has any staged or unstaged changes.</summary>
	internal bool HasChanges => _changes is { } changes && ( changes.Staged.Count > 0 || changes.Unstaged.Count > 0 );

	/// <summary>Clears every operator annotation after a review starts.</summary>
	internal void ClearReviewAnnotations()
	{
		_annotations.Clear();
		_editingAnnotation = null;
		_annotationText = "";
		AnnotationRevision++;
		MarkChanged();
	}
}
