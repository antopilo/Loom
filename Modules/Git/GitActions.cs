using System.Text.Json.Serialization;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Names where the Git views offer an action.</summary>
public enum GitActionScope
{
	/// <summary>On the working tree's changes: a button in the Git panel's toolbar and in the side view's Working tree header.</summary>
	Changes,

	/// <summary>On a commit: an entry in its context menu.</summary>
	Commit,

	/// <summary>On a local branch: an entry in its context menu.</summary>
	Branch,
}

/// <summary>An action another module adds to the Git views, such as reviewing the changes in a chat; contribute it to <see cref="Point"/>.</summary>
/// <param name="Key">A stable identifier, unique among the actions; a panel saves the review an action linked under it.</param>
/// <param name="Name">The button or menu entry's label.</param>
/// <param name="Scope">Where the action shows.</param>
/// <param name="Run">Runs the action on the UI thread.</param>
public sealed record GitAction( string Key, string Name, GitActionScope Scope, Action<GitActionContext> Run )
{
	/// <summary>The extension point the Git module reads actions from.</summary>
	public const string Point = "git.actions";

	/// <summary>Reads a review this action linked with <see cref="GitActionContext.LinkReview"/>, by its identifier: its status, or null once it is gone.</summary>
	/// <remarks>Called during builds; reading observable state there redraws the panel when the review finishes.</remarks>
	public Func<LoknApplication, string, GitReviewStatus?>? ReadReview { get; init; }

	/// <summary>Shows a review this action linked, such as its chat.</summary>
	public Action<LoknApplication, string>? OpenReview { get; init; }

	/// <summary>Gets a line icon that shows in place of <see cref="Name"/> on the Git panel's toolbar, with the name as its tooltip; null shows the name.</summary>
	/// <remarks>Parse it on a 24 by 24 grid, such as a Lucide icon's outline; it draws with round strokes 2 units wide.</remarks>
	public Goo.VectorPath? Icon { get; init; }
}

/// <summary>What a <see cref="GitAction"/> acts on.</summary>
public sealed class GitActionContext
{
	/// <summary>Stores what linking a review does in the panel that ran the action.</summary>
	private readonly Action<string> _linkReview;

	/// <summary>Creates the context of one run.</summary>
	internal GitActionContext( LoknApplication app, Guid panelId, string root, string? commit, string? branch, IReadOnlyList<GitReviewAnnotation> annotations, Action<string> linkReview )
	{
		App = app;
		PanelId = panelId;
		Root = root;
		Commit = commit;
		Branch = branch;
		Annotations = annotations;
		_linkReview = linkReview;
	}

	/// <summary>Gets the application.</summary>
	public LoknApplication App
	{
		get;
	}

	/// <summary>Gets the panel the action ran from, the Git panel or the Files side view, to open things beside it.</summary>
	public Guid PanelId
	{
		get;
	}

	/// <summary>Gets the repository's working tree folder.</summary>
	public string Root
	{
		get;
	}

	/// <summary>Gets the full identifier of the commit a <see cref="GitActionScope.Commit"/> action runs on, else null.</summary>
	public string? Commit
	{
		get;
	}

	/// <summary>Gets the short name of the branch a <see cref="GitActionScope.Branch"/> action runs on, else null.</summary>
	public string? Branch
	{
		get;
	}

	/// <summary>Gets the notes the user left on diff lines for the next review, in file and line order; empty outside <see cref="GitActionScope.Changes"/>.</summary>
	public IReadOnlyList<GitReviewAnnotation> Annotations
	{
		get;
	}

	/// <summary>Links a review the action started, such as a chat: the panel shows its findings on the diff, keeps the link in its saved state, and clears the notes it was given.</summary>
	/// <param name="id">The review's identifier, which <see cref="GitAction.ReadReview"/> and <see cref="GitAction.OpenReview"/> receive.</param>
	public void LinkReview( string id ) => _linkReview( id );
}

/// <summary>Writes commit messages from a repository's changes, such as with a language model; contribute it to <see cref="Point"/>. The commit box uses the first one loaded.</summary>
/// <param name="Name">What writes the message, such as the chat's model, shown on hover.</param>
/// <param name="Write">Writes the message; it starts on the UI thread, should not block it, and receives a token cancelled when the user starts over or the view closes.</param>
public sealed record GitMessageWriter( string Name, Func<GitMessageRequest, CancellationToken, Task<GitMessage>> Write )
{
	/// <summary>The extension point the Git module reads message writers from.</summary>
	public const string Point = "git.messages";
}

/// <summary>What a commit message is written from.</summary>
/// <param name="App">The application.</param>
/// <param name="Profile">The settings profile the asking panel works in, whose account writes the message.</param>
/// <param name="Root">The repository's working tree folder.</param>
/// <param name="Patch">The staged changes as a unified diff, or every uncommitted change when nothing is staged; cut to a bounded length.</param>
/// <param name="Summary">The summary the user typed, which a writer keeps; empty to write one.</param>
public sealed record GitMessageRequest( LoknApplication App, SettingsProfile Profile, string Root, string Patch, string Summary );

/// <summary>A written commit message.</summary>
/// <param name="Summary">The subject line.</param>
/// <param name="Description">Why the change was made, the body after a blank line; may be empty.</param>
public sealed record GitMessage( string Summary, string Description );

/// <summary>Where a linked review stands.</summary>
/// <param name="Result">The finished review's verdict and findings, or null while it runs.</param>
public sealed record GitReviewStatus( GitReviewResult? Result );

/// <summary>Identifies one annotated row in a Git diff.</summary>
/// <param name="Path">The repository-relative file path.</param>
/// <param name="Line">The new-file line for context and additions, or old-file line for removals.</param>
/// <param name="Kind">The diff row kind.</param>
/// <param name="RawLine">The row content without its diff marker.</param>
public readonly record struct GitReviewLine( string Path, int Line, DiffLineKind Kind, string RawLine );

/// <summary>Attaches the user's guidance to one Git diff row.</summary>
/// <param name="Line">The row being explained.</param>
/// <param name="Note">The user's review instruction.</param>
public sealed record GitReviewAnnotation( GitReviewLine Line, string Note );

/// <summary>Describes one review finding, which the Git panel marks on its diff line.</summary>
public sealed record GitReviewFinding
{
	/// <summary>Gets the concise finding title.</summary>
	[JsonPropertyName( "title" )]
	public string Title { get; init; } = "";

	/// <summary>Gets the detailed finding explanation.</summary>
	[JsonPropertyName( "body" )]
	public string Body { get; init; } = "";

	/// <summary>Gets the release priority, where zero is most severe.</summary>
	[JsonPropertyName( "priority" )]
	public int Priority { get; init; } = 3;

	/// <summary>Gets review confidence from zero through one.</summary>
	[JsonPropertyName( "confidence" )]
	public double Confidence { get; init; }

	/// <summary>Gets the repository-relative path.</summary>
	[JsonPropertyName( "file_path" )]
	public string FilePath { get; init; } = "";

	/// <summary>Gets the first affected line.</summary>
	[JsonPropertyName( "line_start" )]
	public int LineStart { get; init; }

	/// <summary>Gets the last affected line.</summary>
	[JsonPropertyName( "line_end" )]
	public int LineEnd { get; init; }
}

/// <summary>Describes a finished review: its verdict and findings.</summary>
public sealed record GitReviewResult
{
	/// <summary>Gets whether the reviewed change is correct.</summary>
	[JsonPropertyName( "overall_correctness" )]
	public string OverallCorrectness { get; init; } = "";

	/// <summary>Gets the review verdict summary.</summary>
	[JsonPropertyName( "explanation" )]
	public string Explanation { get; init; } = "";

	/// <summary>Gets review confidence from zero through one.</summary>
	[JsonPropertyName( "confidence" )]
	public double Confidence { get; init; }

	/// <summary>Gets concrete review findings.</summary>
	[JsonPropertyName( "findings" )]
	public IReadOnlyList<GitReviewFinding> Findings { get; init; } = [];
}
