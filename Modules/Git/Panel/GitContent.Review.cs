using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Runs the actions other modules add (<see cref="GitAction"/>) and shows the review one of them linked: its state, verdict and findings, marked on the diff.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines the share of the panel's height the review banner takes at most, in percent; more findings scroll.</summary>
	private const double ReviewPercent = 40;

	/// <summary>Stores the linked review: the key of the action that linked it and its identifier, or null before the first review.</summary>
	private (string Action, string Id)? _review;

	/// <summary>Stores the linked review's status read by the latest build, or null when it is gone or its action is not loaded.</summary>
	private GitReviewStatus? _reviewStatus;

	/// <summary>Gets the findings the local-changes diff marks inline.</summary>
	private IReadOnlyList<GitReviewFinding> ReviewFindings => _reviewStatus?.Result?.Findings ?? [];

	/// <summary>Lists the loaded actions of one scope, in load order.</summary>
	private IEnumerable<GitAction> ListActions( GitActionScope scope ) =>
		_context.App.Extensions.Get<GitAction>( GitAction.Point ).Where( action => action.Scope == scope );

	/// <summary>Finds the action that linked the review, or null when its module is not loaded.</summary>
	private GitAction? FindReviewAction() =>
		_review is { } review ? _context.App.Extensions.Get<GitAction>( GitAction.Point ).FirstOrDefault( action => action.Key == review.Action ) : null;

	/// <summary>Reads the linked review during a build, so a review whose state is observable rebuilds this panel when it finishes.</summary>
	private void UpdateReviewResult()
	{
		if ( FindReviewAction() is not { ReadReview: { } read } || _review is not { } review )
		{
			_reviewStatus = null;
			return;
		}

		try
		{
			_reviewStatus = read( _context.App, review.Id );
		}
		catch ( Exception error )
		{
			_reviewStatus = null;
			_context.App.ReportError( "Git review", error );
		}
	}

	/// <summary>Runs an action on the shown repository: on the working tree with the user's review notes, or on a commit or branch.</summary>
	private void RunAction( GitAction action, string? commit = null, string? branch = null )
	{
		if ( _root is not { } root )
			return;

		var notes = action.Scope == GitActionScope.Changes ? _changes.ReviewAnnotations : [];
		action.Run( new GitActionContext( _context.App, _context.Id, root, commit, branch, notes, id => LinkReview( action, id ) ) );
	}

	/// <summary>Links the review an action started: shows it here, keeps it in the saved state, and clears the notes it took.</summary>
	private void LinkReview( GitAction action, string id )
	{
		_review = (action.Key, id);
		_reviewStatus = null;
		_changes.ClearReviewAnnotations();
		if ( _layout == GitLayout.Full )
			ClearSelection();
		_error = null;
		MarkChanged();
	}

	/// <summary>Builds a button for each working-tree action: its icon with the review notes it takes along as a badge, or its name when it has no icon.</summary>
	private IEnumerable<Blob> BuildChangeActions()
	{
		var notes = _changes.ReviewAnnotationCount;
		foreach ( var action in ListActions( GitActionScope.Changes ) )
		{
			var item = action;
			var name = $"{item.Name}: local changes";
			var isEnabled = IsIdle && _changes.HasChanges;
			if ( item.Icon is { } icon )
				yield return BuildIconTool( "action:" + item.Key, icon, name, isEnabled, () => RunAction( item ), badge: notes );
			else
				yield return BuildToolButton( notes > 0 ? $"{item.Name} ({notes:N0} notes)" : item.Name, name, isEnabled, () => RunAction( item ) );
		}
	}

	/// <summary>Builds the menu entries of the commit or branch actions, after a separator; none when no module adds any.</summary>
	private IEnumerable<MenuEntry> BuildActionEntries( GitActionScope scope, string? commit, string? branch )
	{
		var actions = ListActions( scope ).ToArray();
		if ( actions.Length == 0 )
			yield break;

		yield return new MenuSeparator();
		foreach ( var action in actions )
		{
			var item = action;
			yield return BuildCommand( item.Name, () => RunAction( item, commit, branch ) );
		}
	}

	/// <summary>Builds the linked review's state and clickable P0-P3 findings below the toolbar; null without a linked review.</summary>
	private Blob? BuildReviewBanner()
	{
		if ( _review is null )
			return null;

		var theme = Theme.Current;
		var children = new List<Blob>();
		var canOpen = FindReviewAction() is { OpenReview: not null };
		if ( _reviewStatus?.Result is not { } review )
		{
			children.Add( new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				FlexWrap = _layout == GitLayout.Full ? FlexWrap.NoWrap : FlexWrap.Wrap,
				Children =
				[
					new Text { Content = "The review is running. Findings appear here when it finishes.", FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Color = theme.Muted },
					BuildToolButton( "Open review", "Open the review", canOpen, OpenReview ),
				],
			} );
		}
		else
		{
			var verdict = string.Equals( review.OverallCorrectness, "correct", StringComparison.OrdinalIgnoreCase ) ? "Ready" : "Changes requested";
			children.Add( new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				FlexWrap = _layout == GitLayout.Full ? FlexWrap.NoWrap : FlexWrap.Wrap,
				Children =
				[
					new Text { Content = $"Review · {verdict} · {review.Confidence:P0}", FontWeight = 600 },
					new Text { Content = review.Explanation, FlexGrow = 1, FlexShrink = 1, MinWidth = 0, TextMaxLines = 2, TextTrimming = TextTrimming.Ellipsis },
					BuildToolButton( "Open review", "Open the review", canOpen, OpenReview ),
				],
			} );
			foreach ( var finding in review.Findings.OrderBy( item => item.Priority ).ThenByDescending( item => item.Confidence ) )
			{
				var item = finding;
				children.Add( new Button
				{
					AccessibilityName = $"Show P{Math.Clamp( item.Priority, 0, 3 )} finding in {item.FilePath}",
					Cursor = Cursor.Pointer,
					Focusable = true,
					Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
					BackgroundColor = Color.Transparent,
					OnClick = () => _context.Run( () => ShowReviewFinding( item ) ),
					Children =
					[
						new Row
						{
							AlignItems = AlignItems.Center,
							Gap = theme.Spacing,
							Children =
							[
								new Text { Content = $"P{Math.Clamp( item.Priority, 0, 3 )}", FontWeight = 600, Color = item.Priority <= 1 ? theme.Error : theme.AnsiYellow, Width = 28 },
								new Text { Content = item.Title, FontWeight = 600, FlexGrow = 1, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
								new Text { Content = $"{item.FilePath}:{item.LineStart} · {item.Confidence:P0}", Color = theme.Muted, FlexShrink = 2, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
							],
						},
					],
				} );
			}
		}

		return new Container
		{
			Key = "git-review",
			FlexShrink = 0,
			MaxHeight = Length.Percent( ReviewPercent ),
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.GetScrollbar(),
			Padding = theme.Spacing,
			Gap = theme.Spacing / 2,
			BackgroundColor = theme.Accent.WithAlpha( 0.08 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children = children,
		};
	}

	/// <summary>Shows the linked review through the action that linked it, such as its chat.</summary>
	private void OpenReview()
	{
		if ( _review is { } review && FindReviewAction() is { OpenReview: { } open } )
			open( _context.App, review.Id );
	}

	/// <summary>Shows one finding's file: in the panel, as its diff; in the side view, in the Git panel.</summary>
	private void ShowReviewFinding( GitReviewFinding finding )
	{
		if ( _layout != GitLayout.Full )
		{
			if ( _root is { } root )
				_context.App.GetModule<GitModule>().OpenPanel( _context.App, _context.Id, root, Path.GetFullPath( Path.Combine( root, finding.FilePath ) ) );
			return;
		}

		if ( _changes.FindChange( finding, out var staged ) is { } file )
			ShowFile( new GitFileTarget( file.Path, staged ) );
	}
}
