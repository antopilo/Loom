using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Builds the History mode's refs: the repository, a filter, and the Branches, Remotes, Tags and Stashes sections.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines a sidebar item's height in logical pixels.</summary>
	private const int ItemHeight = 24;

	/// <summary>Defines the indent per nesting level in logical pixels.</summary>
	private const int ItemIndent = 14;

	/// <summary>Stores the keys of collapsed sections and remote groups.</summary>
	private readonly HashSet<string> _collapsed = [];

	/// <summary>Stores the sidebar filter text.</summary>
	private string _filter = "";

	/// <summary>Builds the refs, stacked above the History mode's log.</summary>
	private Container BuildSidebar()
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Text
			{
				Key = "repository",
				Content = RepositoryName,
				FontWeight = EmphasisWeight,
				Padding = new Edges( theme.Spacing / 2, theme.Spacing, 0, theme.Spacing ),
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( _state is { Branch: var current, Commit: var shortId } && (current ?? shortId) is { } head )
			children.Add( new Text
			{
				Key = "head",
				Content = current is null ? $"Detached at {head}" : head,
				FontSize = ViewStyle.SmallSize,
				Color = theme.Muted,
				Padding = new Edges( 0, theme.Spacing, theme.Spacing / 2, theme.Spacing ),
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );

		children.Add( new TextEntry
		{
			Key = "filter",
			Value = _filter,
			Controlled = true,
			Placeholder = "Filter branches",
			AccessibilityName = "Filter branches, tags and stashes",
			Height = theme.ControlHeight,
			Margin = new Edges( theme.Spacing / 2, 0 ),
			Padding = new Edges( 0, theme.Spacing ),
			BackgroundColor = theme.Raised,
			BorderRadius = theme.Radius,
			OnChange = value => _context.Run( () =>
			{
				_filter = value;
				MarkChanged();
			} ),
		} );

		var filter = _filter.Trim();
		var locals = _refs.Where( item => item.Kind == GitRefKind.LocalBranch && IsMatch( item.ShortName, filter ) ).ToArray();
		AddSection( children, "branches", "Branches", locals.Length, filter, items =>
		{
			foreach ( var branch in locals )
				items.Add( BuildLocalBranch( branch ) );
		} );

		var remotes = _refs.Where( item => item.Kind == GitRefKind.RemoteBranch && IsMatch( item.ShortName, filter ) )
			.GroupBy( item => item.Remote ?? ComputeRemote( item.ShortName ) ).ToArray();
		AddSection( children, "remotes", "Remotes", remotes.Sum( group => group.Count() ), filter, items =>
		{
			foreach ( var group in remotes )
			{
				var key = "remote:" + group.Key;
				var isOpen = filter.Length > 0 || !_collapsed.Contains( key );
				items.Add( BuildHeading( key, group.Key, null, isOpen, 1 ) );
				if ( !isOpen )
					continue;

				foreach ( var branch in group )
					items.Add( BuildRemoteBranch( branch, group.Key ) );
			}
		} );

		var tags = _refs.Where( item => item.Kind == GitRefKind.Tag && IsMatch( item.ShortName, filter ) ).ToArray();
		AddSection( children, "tags", "Tags", tags.Length, filter, items =>
		{
			foreach ( var tag in tags )
				items.Add( BuildTag( tag ) );
		} );

		var stashes = _stashes.Where( item => IsMatch( item.Name + " " + item.Message, filter ) ).ToArray();
		AddSection( children, "stashes", "Stashes", stashes.Length, filter, items =>
		{
			foreach ( var stash in stashes )
				items.Add( BuildStash( stash ) );
		} );

		return new Container
		{
			Key = "git-sidebar",
			Width = Length.Percent( 100 ),
			MaxHeight = Length.Percent( HistoryRefsPercent ),
			FlexShrink = 1,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			Padding = new Edges( theme.Spacing / 2, theme.Spacing / 2 ),
			Gap = 1,
			BackgroundColor = theme.Canvas,
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Repository",
			Children = children,
		};
	}

	/// <summary>Adds a collapsible section heading and, while it is open, its items; a filter opens every section and hides empty ones.</summary>
	private void AddSection( List<Blob> children, string key, string title, int count, string filter, Action<List<Blob>> addItems )
	{
		if ( filter.Length > 0 && count == 0 )
			return;

		var isOpen = filter.Length > 0 || !_collapsed.Contains( key );
		children.Add( BuildHeading( key, title, $"{count:N0}", isOpen, 0 ) );
		if ( isOpen )
			addItems( children );
	}

	/// <summary>Builds a section or remote heading with its chevron and count; clicking it folds or unfolds it.</summary>
	private Button BuildHeading( string key, string title, string? count, bool isOpen, int depth )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			theme.BuildChevron( isOpen ),
			new Text
			{
				Content = title,
				FontSize = ViewStyle.SmallSize,
				FontWeight = depth == 0 ? EmphasisWeight : NormalWeight,
				Color = depth == 0 ? theme.Muted : theme.Text,
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			new Spacer(),
		};
		if ( count is not null )
			children.Add( new Text { Content = count, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0, } );

		var heading = BuildItemButton( title, () =>
		{
			if ( !_collapsed.Remove( key ) )
				_collapsed.Add( key );
		}, depth );
		heading.Key = "heading:" + key;
		heading.Margin = new Edges( depth == 0 ? theme.Spacing : 0, 0, 0, 0 );
		heading.AccessibilityName = $"{(isOpen ? "Fold" : "Unfold")} {title}";
		heading.Children = children;
		return heading;
	}

	/// <summary>Builds a local branch: a check on the current one, its name, how far it is from its upstream, and a gone upstream marked.</summary>
	/// <remarks>Click selects its commit, double-click checks it out, right-click opens its menu.</remarks>
	private Button BuildLocalBranch( GitRef branch )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Text
			{
				Content = branch.IsCurrent ? "✓" : "",
				Width = ItemIndent,
				FlexShrink = 0,
				Color = theme.Accent,
			},
			BuildItemName( branch.ShortName, branch.IsCurrent ),
			new Spacer(),
		};
		if ( branch.IsUpstreamGone )
			children.Add( new Text { Content = "gone", FontSize = ViewStyle.SmallSize, Color = theme.Error, FlexShrink = 0, } );
		else if ( (branch.Ahead > 0 ? $"{branch.Ahead}↑" : "") + (branch.Ahead > 0 && branch.Behind > 0 ? " " : "") + (branch.Behind > 0 ? $"{branch.Behind}↓" : "") is { Length: > 0 } sync )
			children.Add( new Text { Content = sync, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0, } );

		var key = "branch:" + branch.Name;
		var item = BuildRefItem( key, branch, 0, $"{branch.ShortName} options", () => BuildLocalBranchMenu( branch ) );
		item.AccessibilityName = $"{branch.ShortName}{(branch.IsCurrent ? ", current" : "")}{(branch.IsUpstreamGone ? ", upstream gone" : "")}, {branch.Ahead} ahead, {branch.Behind} behind";
		item.Children = children;
		return item;
	}

	/// <summary>Builds a remote branch under its remote, without the remote's prefix.</summary>
	private Button BuildRemoteBranch( GitRef branch, string remote )
	{
		var name = branch.ShortName.StartsWith( remote + "/", StringComparison.Ordinal ) ? branch.ShortName[(remote.Length + 1)..] : branch.ShortName;
		var item = BuildRefItem( "remote-branch:" + branch.Name, branch, 1, $"{branch.ShortName} options", () => BuildRemoteBranchMenu( branch ) );
		item.AccessibilityName = branch.ShortName;
		item.Children = [new Container { Width = ItemIndent, FlexShrink = 0, }, BuildItemName( name, false )];
		return item;
	}

	/// <summary>Builds a tag.</summary>
	private Button BuildTag( GitRef tag )
	{
		var item = BuildRefItem( "tag:" + tag.Name, tag, 0, $"Tag {tag.ShortName} options", () => BuildTagMenu( tag ) );
		item.AccessibilityName = $"Tag {tag.ShortName}";
		item.Children = [new Container { Width = ItemIndent, FlexShrink = 0, }, BuildItemName( tag.ShortName, false )];
		return item;
	}

	/// <summary>Builds a stash: its message, with its name muted; click shows its changes, right-click opens its menu.</summary>
	private Button BuildStash( GitStash stash )
	{
		var theme = Theme.Current;
		var key = "stash:" + stash.Commit;
		var item = BuildItemButton( stash.Message, () => SelectStash( stash ), 0 );
		item.Handle = EnsureHandle( key );
		item.AccessibilityName = $"{stash.Name}: {stash.Message}";
		item.BackgroundColor = _selectedStash?.Commit == stash.Commit ? theme.Accent.WithAlpha( SelectionOpacity ) : Color.Transparent;
		item.OnPointerDown = pointer => _context.Run( () =>
		{
			if ( pointer.Button == PointerButton.Secondary )
				OpenMenu( key, $"{stash.Name} options", BuildStashMenu( stash ), pointer.Position );
		} );
		item.Children =
		[
			new Container { Width = ItemIndent, FlexShrink = 0, },
			BuildItemName( stash.Message.Length > 0 ? stash.Message : stash.Name, false ),
			new Spacer(),
			new Text { Content = $"{{{stash.Index}}}", FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0, },
		];
		return item;
	}

	/// <summary>Builds a branch or tag item: click selects its commit, double-click checks it out, right-click opens its menu.</summary>
	private Button BuildRefItem( string key, GitRef reference, int depth, string menuName, Func<IReadOnlyList<MenuEntry>> menu )
	{
		var theme = Theme.Current;
		var item = BuildItemButton( reference.ShortName, () => SelectCommit( reference.Target, reveal: true ), depth );
		item.Handle = EnsureHandle( key );
		item.BackgroundColor = _selectedId == reference.Target && _selectedStash is null ? theme.Accent.WithAlpha( SelectionOpacity / 2 ) : Color.Transparent;
		item.OnPointerDown = pointer => _context.Run( () =>
		{
			if ( pointer.Button == PointerButton.Secondary )
				OpenMenu( key, menuName, menu(), pointer.Position );
		} );
		item.OnPointerUp = pointer => _context.Run( () =>
		{
			if ( pointer.Button == PointerButton.Primary && pointer.ClickCount == 2 )
				Checkout( reference );
		} );
		return item;
	}

	/// <summary>Builds a sidebar button that runs <paramref name="action"/> and rebuilds.</summary>
	private Button BuildItemButton( string label, Action action, int depth )
	{
		var theme = Theme.Current;
		var item = theme.BuildButton( label, () => _context.Run( () =>
		{
			action();
			MarkChanged();
		} ) );
		item.Height = ItemHeight;
		item.FlexShrink = 0;
		item.FlexDirection = FlexDirection.Row;
		item.JustifyContent = JustifyContent.FlexStart;
		item.Gap = theme.Spacing / 2;
		item.Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing / 2 + depth * ItemIndent );
		return item;
	}

	/// <summary>Builds an item's name, bold for the current branch.</summary>
	private static Text BuildItemName( string name, bool isBold ) => new()
	{
		Content = name,
		FontWeight = isBold ? EmphasisWeight : NormalWeight,
		FlexShrink = 1,
		MinWidth = 0,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Gets a remote branch's remote from its short name when git did not give one.</summary>
	private static string ComputeRemote( string shortName ) => shortName.IndexOf( '/' ) is var slash and > 0 ? shortName[..slash] : shortName;

	/// <summary>Reports whether text contains the filter, ignoring case; an empty filter matches everything.</summary>
	private static bool IsMatch( string text, string filter ) => filter.Length == 0 || text.Contains( filter, StringComparison.OrdinalIgnoreCase );
}
