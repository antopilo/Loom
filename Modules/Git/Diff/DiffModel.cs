namespace Lokn.Modules.Git;

/// <summary>Classifies one row of a rendered diff.</summary>
public enum DiffLineKind
{
	/// <summary>An unchanged line shown around changes.</summary>
	Context,

	/// <summary>An added line.</summary>
	Added,

	/// <summary>A removed line.</summary>
	Removed,

	/// <summary>A run of hidden unchanged lines between hunks.</summary>
	Gap,
}

/// <summary>Describes one diff row.</summary>
/// <param name="Kind">Whether the line is unchanged, added, removed, or a run of hidden unchanged lines.</param>
/// <param name="Number">The new file's line number for context and added lines, the old file's for removed lines, and the hidden line count for a gap (zero when unknown).</param>
/// <param name="Text">The line's content without its marker; empty for a gap.</param>
internal readonly record struct DiffLine( DiffLineKind Kind, int Number, string Text );

/// <summary>Describes the changed lines of one file.</summary>
/// <param name="Path">The file path, relative to the project or repository when it lies inside it.</param>
/// <param name="Operation">The change, such as <c>update</c>, <c>create</c>, <c>delete</c> or <c>write</c>.</param>
/// <param name="Lines">The changed rows with their surrounding context.</param>
/// <param name="Note">Why the file's lines are not shown, such as a binary or oversized file; null when they are.</param>
internal sealed record DiffFile( string Path, string Operation, IReadOnlyList<DiffLine> Lines, string? Note = null )
{
	/// <summary>Gets the number of added lines.</summary>
	public int Added { get; } = Lines.Count( line => line.Kind == DiffLineKind.Added );

	/// <summary>Gets the number of removed lines.</summary>
	public int Removed { get; } = Lines.Count( line => line.Kind == DiffLineKind.Removed );
}
