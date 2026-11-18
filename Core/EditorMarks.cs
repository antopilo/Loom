namespace Lokn.Core;

/// <summary>Names how a line differs from the version a mark source compares against.</summary>
public enum LineChange
{
	/// <summary>The line is new.</summary>
	Added,

	/// <summary>The line replaced one or more lines.</summary>
	Modified,

	/// <summary>Lines were removed just above this line; the mark sits between lines.</summary>
	Deleted,
}

/// <summary>One mark beside a line in the editor's gutter, such as a change against git.</summary>
/// <param name="Line">The zero-based line in the saved file.</param>
/// <param name="Change">How the line changed.</param>
/// <param name="Removed">For <see cref="LineChange.Modified"/> and <see cref="LineChange.Deleted"/>, the old lines, shown on hover; empty otherwise.</param>
public sealed record LineMark( int Line, LineChange Change, IReadOnlyList<string> Removed );

/// <summary>Computes the line marks of a saved file, such as its changes against git; contribute it to <see cref="Point"/>.</summary>
/// <remarks>
/// The editor asks every source when a file opens or is saved, on the UI thread, and shows the marks of the first that returns any.
/// The source does its work off the UI thread; return an empty list for a file it does not cover.
/// </remarks>
/// <param name="Name">Names the source, such as <c>git</c>.</param>
/// <param name="Compute">Computes the marks of the file at the full path.</param>
public sealed record LineMarkSource( string Name, Func<string, CancellationToken, Task<IReadOnlyList<LineMark>>> Compute )
{
	/// <summary>The extension point the Editor module reads mark sources from.</summary>
	public const string Point = "editor.marks";
}
