using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Lays the Git views out for the Files side view: the Changes mode's working tree and commit box, and the History mode's refs above the log;
/// and shows the side view's Changes mode from the panel.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines the stash button's glyph, a box.</summary>
	private const string StashGlyph = "\uE7B8";

	/// <summary>Defines the share of the History mode's height the refs take at most, in percent.</summary>
	private const double HistoryRefsPercent = 40;

	/// <summary>Builds the Changes mode: the operation, progress and review notices, the Working tree header with its tools, both sections, the commit box, and the HEAD footer.</summary>
	private List<Blob> BuildChangesMode()
	{
		var children = new List<Blob>();
		AddNotices( children );
		if ( BuildRepositoryMessage() is { } message )
		{
			children.Add( message );
			return children;
		}

		var stash = _changes.BuildIconButton( StashGlyph, "Stash every change", false, () =>
		{
			if ( IsIdle && _state is { Files: > 0 } )
				AskStash();
		} );
		children.Add( _changes.BuildSideHeader( [stash, .. BuildChangeActions()] ) );
		children.Add( _changes.BuildSide() );
		children.Add( BuildSideFooter() );
		return children;
	}

	/// <summary>Builds the History mode: the wrapping toolbar and notices, then branches, remotes, tags and stashes above the commit log and the selected commit.</summary>
	private List<Blob> BuildHistoryMode()
	{
		var children = new List<Blob> { BuildToolbar() };
		AddNotices( children );
		if ( BuildRepositoryMessage() is { } message )
		{
			children.Add( message );
			return children;
		}

		children.Add( new Container
		{
			Key = "git-history",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Children = [BuildSidebar(), BuildCommits()],
		} );
		return children;
	}

	/// <summary>Shows the Files side view in its Changes mode, opening it when closed, while the panel's repository is the open project's.</summary>
	private void ShowSideChanges()
	{
		if ( GitModule.FindSideView( _context, _root ?? _directory ) is { } side )
			side.Show( _context.App, GitModule.ChangesMode );
	}

	/// <summary>Defines the connected-nodes glyph that stands for the branch in the footer.</summary>
	private const string BranchGlyph = "\uF003";

	/// <summary>Builds the Changes mode's footer: the branch and short HEAD commit, and how many files changed.</summary>
	private Row BuildSideFooter()
	{
		var theme = Theme.Current;
		var head = _state is { Branch: var branch, Commit: var commit } ? string.Join( " ", new[] { branch, commit }.OfType<string>() ) : "";
		var files = _state?.Files ?? 0;
		return new Row
		{
			Key = "git-side-footer",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			BorderColor = theme.Border,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"HEAD {head}, {files:N0} changed",
			Children =
			[
				GitChangesPane.BuildInlineIcon( BranchGlyph, theme.Muted ),
				new Text { Content = head.Length > 0 ? head : "no commit", FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				new Spacer(),
				new Text { Content = $"{files:N0} changed", FontSize = ViewStyle.SmallSize, Color = files > 0 ? theme.AnsiYellow : theme.Muted, FlexShrink = 0 },
			],
		};
	}
}
