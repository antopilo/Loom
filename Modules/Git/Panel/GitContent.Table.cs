using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>One branch or tag as the table's Branch / Tag column shows it.</summary>
/// <param name="Ref">The reference.</param>
/// <param name="Label">Its name, without its remote for a remote branch.</param>
/// <param name="Icon">A branch, a globe for a branch only on a remote, or a tag.</param>
/// <param name="IsOnRemote">Whether a local branch's remote branch points at the same commit, drawn as a globe after its name.</param>
internal sealed record GitRefLabel( GitRef Ref, string Label, VectorPath Icon, bool IsOnRemote );

/// <summary>Builds the Git panel's graph: the commit table with its Branch / Tag, Graph, Commit message, Date and SHA columns under a pinned
/// Working tree row, with the selected commit's details beside it.</summary>
/// <remarks>
/// Each row tints the graph from its commit to the message in its lane's color and marks the message with a bar of that color, so a branch
/// reads across the table. A commit a branch or tag points at shows its author's avatar in place of its dot, joined to its badges by a line.
/// </remarks>
internal sealed partial class GitContent
{
	/// <summary>Defines the Branch / Tag column width in logical pixels.</summary>
	private const int RefWidth = 190;

	/// <summary>Defines the table's date column width in logical pixels.</summary>
	private const int TableDateWidth = 72;

	/// <summary>Defines the table's SHA column width in logical pixels.</summary>
	private const int TableShaWidth = 72;

	/// <summary>Defines the table header's height in logical pixels.</summary>
	private const int TableHeaderHeight = 26;

	/// <summary>Defines the room left of the first lane, so an avatar on it is not cut off, in logical pixels.</summary>
	private const double GraphInset = 10;

	/// <summary>Defines the room right of the last lane, in logical pixels.</summary>
	private const double GraphTail = 10;

	/// <summary>Defines the narrowest the table's graph column gets, so its heading fits, in logical pixels.</summary>
	private const double MinGraphWidth = 56;

	/// <summary>Defines a branch tip's avatar diameter in logical pixels.</summary>
	private const double TipAvatarSize = 18;

	/// <summary>Defines a badge's height in logical pixels.</summary>
	private const double BadgeHeight = 18;

	/// <summary>Defines the opacity of the lane color tinting a row's graph from its commit to the message.</summary>
	private const double BandOpacity = 0.2;

	/// <summary>Defines the opacity of the line joining a tip's badges to its avatar.</summary>
	private const double ConnectorOpacity = 0.55;

	/// <summary>Defines the width of the lane-colored bar before a message, in logical pixels.</summary>
	private const double MessageBarWidth = 3;

	/// <summary>Defines how long a row's fill takes to change on hover and selection, in milliseconds.</summary>
	private const int RowFadeMs = 120;

	/// <summary>Gets the table's graph column width: the lanes with room for avatars at both ends, or wide enough for its heading.</summary>
	private double TableGraphWidth => Math.Max( GraphInset + Math.Min( _history?.Lanes ?? 1, MaxLanes ) * GitHistory.LaneWidth + GraphTail, MinGraphWidth );

	/// <summary>Holds the hollow circle the Working tree row draws on the first lane.</summary>
	private static readonly VectorPath WorkingTreeDot = VectorPath.Parse(
		FormattableString.Invariant( $"M{GitHistory.LaneWidth / 2 - GitHistory.DotRadius} {GitHistory.RowHeight / 2} a{GitHistory.DotRadius} {GitHistory.DotRadius} 0 1 0 {GitHistory.DotRadius * 2} 0 a{GitHistory.DotRadius} {GitHistory.DotRadius} 0 1 0 {-GitHistory.DotRadius * 2} 0 Z" ),
		new ElementRect { Width = GitHistory.LaneWidth, Height = GitHistory.RowHeight } );

	/// <summary>Builds the commit table, with the selected commit's details beside it when one is selected.</summary>
	private Row BuildGraphView() => new()
	{
		Key = "git-graph",
		FlexGrow = 1,
		MinWidth = 0,
		MinHeight = 0,
		Children = BuildInspector() is { } inspector ? [BuildTable(), inspector] : [BuildTable()],
	};

	/// <summary>Builds the column headings, the Working tree row and the commits, which fade in once they first load.</summary>
	private Container BuildTable()
	{
		var rows = ComputeLogRows();
		Blob list = _history is null
			? BuildMessage( _isLoading ? "Reading history…" : "No history loaded." )
			: rows.Count == 0
			? BuildMessage( "No commits yet." )
			: Cell.Mount<GitRevealInput, GitReveal>( "git-log-reveal", new( new Virtual
			{
				Key = "git-log",
				Handle = _logList,
				Source = new MeasuredRowsSource<GitLogRow>( rows, _buildLogRow, static _ => GitHistory.RowHeight, FollowEnd: false ),
				AccessibilityName = "Commits",
				FlexGrow = 1,
				MinWidth = 0,
				MinHeight = 0,
				OverflowX = Overflow.Hidden,
				ScrollbarY = ViewStyle.GetScrollbar(),
			} ) );
		return new Container
		{
			Key = "git-table",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			OverflowX = Overflow.Hidden,
			Children = [BuildTableHeader(), BuildWorkingTreeRow(), list],
		};
	}

	/// <summary>Builds the column headings, aligned with the rows below.</summary>
	private Row BuildTableHeader()
	{
		var theme = Theme.Current;
		Text Heading( string label, double? width, double inset = 0 ) => new()
		{
			Content = label,
			Width = width is { } fixedWidth ? Length.Pixels( fixedWidth ) : Length.Auto,
			FlexGrow = width is null ? 1 : 0,
			FlexShrink = width is null ? 1 : 0,
			MinWidth = 0,
			Padding = new Edges( 0, 0, 0, inset ),
			FontSize = ViewStyle.SmallSize,
			Color = theme.Muted,
			TextWrap = TextWrap.NoWrap,
		};
		return new Row
		{
			Key = "git-table-header",
			MinHeight = TableHeaderHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Padding = new Edges( 0, theme.Spacing, 0, 0 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				Heading( "Branch / Tag", RefWidth, theme.Spacing ),
				Heading( "Graph", TableGraphWidth, GraphInset ),
				Heading( "Commit message", null, theme.Spacing + MessageBarWidth ),
				Heading( "Date", TableDateWidth ),
				Heading( "SHA", TableShaWidth ),
			],
		};
	}

	/// <summary>Builds the pinned Working tree row: a hollow circle, and how many files are staged and unstaged; selecting it hides the commit details.</summary>
	private Button BuildWorkingTreeRow()
	{
		var theme = Theme.Current;
		var state = _state;
		var unstaged = (state?.Unstaged ?? 0) + (state?.Untracked ?? 0) + (state?.Conflicted ?? 0);
		var staged = state?.Staged ?? 0;
		var message = new List<Blob>
		{
			GitIcons.Build( GitIcons.Pencil, 13, theme.Muted ),
			new Text { Content = "Working tree", Color = theme.Text, FlexShrink = 0, TextWrap = TextWrap.NoWrap },
		};
		if ( staged > 0 )
			message.Add( BuildChip( $"{staged:N0} staged", theme.Success ) );
		if ( unstaged > 0 )
			message.Add( BuildChip( $"{unstaged:N0} unstaged", theme.AnsiYellow ) );
		if ( unstaged == 0 && staged == 0 )
			message.Add( new Text { Content = "No local changes", FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0, TextWrap = TextWrap.NoWrap } );

		void Select() => _context.Run( SelectWorkingTree );
		var row = BuildTableRowShell( "git-working-tree", _isWorkingTree, $"Working tree, {unstaged:N0} unstaged, {staged:N0} staged", Select );
		row.Key = "git-working-tree";
		row.BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 );
		row.BorderColor = theme.Border;
		row.KeyBindings = [new( Key.Enter, KeyModifiers.None, Select ), new( Key.Space, KeyModifiers.None, Select )];
		row.Children =
		[
			new Container { Width = RefWidth, FlexShrink = 0 },
			new Container
			{
				Width = TableGraphWidth,
				Height = GitHistory.RowHeight,
				FlexShrink = 0,
				Children =
				[
					new Container
					{
						Position = PositionType.Absolute,
						Left = GraphInset,
						Top = 0,
						Children = [BuildLayer( WorkingTreeDot, GitHistory.LaneWidth, new Shape { Fill = new VectorPaint( theme.Panel ), Stroke = new VectorStroke( 1.5, theme.AnsiBlue ) } )],
					},
				],
			},
			new Row
			{
				FlexGrow = 1,
				FlexShrink = 1,
				MinWidth = 0,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing + MessageBarWidth ),
				Overflow = Overflow.Hidden,
				Children = message,
			},
		];
		return row;
	}

	/// <summary>Builds a small rounded count in a color.</summary>
	private static Container BuildChip( string label, Color color ) => new()
	{
		FlexShrink = 0,
		MinHeight = 17,
		JustifyContent = JustifyContent.Center,
		Padding = new Edges( 0, Theme.Current.Spacing * 0.75 ),
		BorderRadius = Theme.Current.Radius,
		BackgroundColor = color.WithAlpha( 0.16 ),
		Children = [new Text { Content = label, FontSize = ViewStyle.SmallSize - 1, Color = color, TextWrap = TextWrap.NoWrap, LineHeight = 1 }],
	};

	/// <summary>Builds one table row: its branches and tags, graph, lane-marked subject, date and short SHA; click selects it, right-click opens its menu.</summary>
	private Blob BuildTableRow( GitLogRow row )
	{
		var theme = Theme.Current;
		var commit = row.Commit;
		var id = commit.Id;
		var key = "commit:" + id;
		var lane = ComputeLaneColor( row.Graph.Color );
		void Select() => _context.Run( () => SelectCommit( id, reveal: false ) );
		var button = BuildTableRowShell( key, row.IsSelected, $"{commit.Subject}, {commit.AuthorName}, {commit.ShortId}", Select );
		button.Handle = EnsureHandle( key );
		button.OnPointerDown = pointer => _context.Run( () =>
		{
			// A branch or tag badge opens its own menu.
			if ( pointer.Button != PointerButton.Secondary || pointer.IsFromInteractiveChild )
				return;

			SelectCommit( id, reveal: false );
			OpenMenu( key, $"Commit {commit.ShortId} options", BuildCommitMenu( commit ), pointer.Position );
		} );
		button.KeyBindings =
		[
			new( Key.Enter, KeyModifiers.None, Select ),
			new( Key.Space, KeyModifiers.None, Select ),
			new( Key.Up, KeyModifiers.None, () => _context.Run( () => SelectNeighbor( id, -1 ) ) ),
			new( Key.Down, KeyModifiers.None, () => _context.Run( () => SelectNeighbor( id, 1 ) ) ),
		];
		button.Children =
		[
			BuildRefCell( row, lane ),
			BuildTableGraph( row, lane ),
			new Row
			{
				FlexGrow = 1,
				FlexShrink = 1,
				MinWidth = 0,
				AlignItems = AlignItems.Center,
				Children =
				[
					new Container { Width = MessageBarWidth, Height = GitHistory.RowHeight - 6, FlexShrink = 0, BorderRadius = 1.5, BackgroundColor = lane },
					new Text
					{
						Content = commit.Subject,
						FlexGrow = 1,
						FlexShrink = 1,
						MinWidth = 0,
						Padding = new Edges( 0, theme.Spacing ),
						FontWeight = row.IsHead ? EmphasisWeight : NormalWeight,
						Color = theme.Text,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
				],
			},
			BuildColumn( FormatShortDate( commit.AuthorDate ), TableDateWidth ),
			BuildShaColumn( commit.ShortId ),
		];
		return button;
	}

	/// <summary>Builds the button every table row is: full height, its fill easing between rest, hover and selection, outlined while selected.</summary>
	private static Button BuildTableRowShell( string key, bool isSelected, string name, Action select )
	{
		var theme = Theme.Current;
		var selected = theme.Accent.WithAlpha( SelectionOpacity );
		return new Button
		{
			Height = GitHistory.RowHeight,
			FlexShrink = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Padding = new Edges( 0, theme.Spacing, 0, 0 ),
			BackgroundColor = isSelected ? selected : Color.Transparent,
			HoverStyle = new Style { BackgroundColor = isSelected ? selected : theme.Hover },
			BorderWidth = theme.BorderWidth,
			BorderColor = isSelected ? theme.Accent.WithAlpha( 0.8 ) : Color.Transparent,
			TransitionMs = RowFadeMs,
			TransitionProperties = [TransitionProperty.BackgroundColor, TransitionProperty.BorderColor],
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityRole = AccessibilityRole.ListItem,
			AccessibilityName = name,
			OnClick = select,
		};
	}

	/// <summary>Builds the SHA column in the code font.</summary>
	private static Text BuildShaColumn( string shortId )
	{
		var sha = BuildColumn( shortId, TableShaWidth );
		sha.FontFamily = ViewStyle.CodeFontFamily;
		return sha;
	}

	/// <summary>Builds a row's graph cell: the lane tint from its commit rightward, the line from its badges, the graph's lines, and its dot or avatar.</summary>
	private Container BuildTableGraph( GitLogRow row, Color lane )
	{
		var theme = Theme.Current;
		var graph = row.Graph;
		var x = GraphInset + GitHistory.ComputeX( graph.Lane );
		var middle = GitHistory.RowHeight / 2;
		var isTip = row.Refs.Count > 0;
		var lines = new List<Blob>();
		foreach ( var (color, path) in graph.Lines )
			lines.Add( BuildLayer( path, graph.Width, new Shape { Stroke = new VectorStroke( LineWidth, ComputeLaneColor( color ) ) { Cap = StrokeCap.Round } } ) );
		if ( !isTip || _pictures is null )
			lines.Add( BuildLayer( graph.Dot, graph.Width, row.IsHead
				? new Shape { Fill = new VectorPaint( theme.Panel ), Stroke = new VectorStroke( 2, lane ) }
				: new Shape { Fill = new VectorPaint( lane ) } ) );

		var children = new List<Blob>
		{
			new Container { Position = PositionType.Absolute, Left = x, Right = 0, Top = 3, Bottom = 3, BackgroundColor = lane.WithAlpha( BandOpacity ) },
		};
		if ( isTip )
			children.Add( new Container { Position = PositionType.Absolute, Left = 0, Width = x, Top = middle - 0.5, Height = 1, BackgroundColor = lane.WithAlpha( ConnectorOpacity ) } );
		children.Add( new Container { Position = PositionType.Absolute, Left = GraphInset, Top = 0, Width = graph.Width, Height = GitHistory.RowHeight, Children = lines } );
		if ( isTip && _pictures is { } pictures )
			children.Add( new Container
			{
				Position = PositionType.Absolute,
				Left = x - TipAvatarSize / 2,
				Top = middle - TipAvatarSize / 2,
				Children = [Cell.Mount<GitAvatarInput, GitAvatar>( "avatar", new( pictures, row.Commit.AuthorName, row.Commit.AuthorEmail, TipAvatarSize, lane ) )],
			} );

		return new Container
		{
			Width = TableGraphWidth,
			Height = GitHistory.RowHeight,
			FlexShrink = 0,
			Overflow = Overflow.Hidden,
			Children = children,
		};
	}

	/// <summary>Builds a row's Branch / Tag cell: a HEAD badge when detached, the first branch or tag, how many more point there, and a line on to the graph.</summary>
	private Row BuildRefCell( GitLogRow row, Color lane )
	{
		var theme = Theme.Current;
		var labels = ComputeRefLabels( row.Refs );
		var children = new List<Blob>();
		if ( row.IsHead && !row.Refs.Any( item => item.IsCurrent ) )
			children.Add( BuildHeadBadge() );
		if ( labels.Count > 0 )
			children.Add( BuildRefBadge( labels[0] ) );
		if ( labels.Count > 1 )
			children.Add( BuildMoreRefs( row.Commit.Id, [.. labels.Skip( 1 ).Select( label => label.Ref )] ) );
		if ( children.Count > 0 )
			children.Add( new Container { FlexGrow = 1, MinWidth = theme.Spacing, Height = 1, BackgroundColor = lane.WithAlpha( ConnectorOpacity ) } );
		return new Row
		{
			Width = RefWidth,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, 0, 0, theme.Spacing ),
			Overflow = Overflow.Hidden,
			Children = children,
		};
	}

	/// <summary>Lists a commit's branches and tags as the table shows them: local branches first, each with its remote branch of the same name
	/// folded in as a globe, then branches only on a remote by their plain name, then tags.</summary>
	internal static List<GitRefLabel> ComputeRefLabels( IReadOnlyList<GitRef> refs )
	{
		var remotes = refs.Where( item => item.Kind == GitRefKind.RemoteBranch ).ToList();
		var labels = new List<GitRefLabel>();
		foreach ( var local in refs.Where( item => item.Kind == GitRefKind.LocalBranch ) )
		{
			var match = remotes.FindIndex( remote => StripRemote( remote ) == local.ShortName );
			if ( match >= 0 )
				remotes.RemoveAt( match );
			labels.Add( new( local, local.ShortName, GitIcons.Branch, match >= 0 ) );
		}
		foreach ( var remote in remotes )
		{
			var label = StripRemote( remote );
			if ( labels.All( item => item.Label != label ) )
				labels.Add( new( remote, label, GitIcons.Globe, false ) );
		}
		labels.AddRange( refs.Where( item => item.Kind == GitRefKind.Tag ).Select( tag => new GitRefLabel( tag, tag.ShortName, GitIcons.Tag, false ) ) );
		return labels;
	}

	/// <summary>Gets a remote branch's name without its remote, such as <c>main</c> for <c>origin/main</c>.</summary>
	private static string StripRemote( GitRef remote )
	{
		var prefix = (remote.Remote ?? ComputeRemote( remote.ShortName )) + "/";
		return remote.ShortName.StartsWith( prefix, StringComparison.Ordinal ) ? remote.ShortName[prefix.Length..] : remote.ShortName;
	}

	/// <summary>Builds a branch or tag badge: its icon, name and a globe when it is on a remote too; click selects its commit, double-click checks it
	/// out, right-click opens its menu.</summary>
	private Button BuildRefBadge( GitRefLabel label )
	{
		var theme = Theme.Current;
		var reference = label.Ref;
		var key = "ref:" + reference.Name;
		var isCurrent = reference.IsCurrent;
		var children = new List<Blob>
		{
			GitIcons.Build( label.Icon, 13, isCurrent ? theme.Accent : theme.Text.WithAlpha( 0.7 ) ),
			new Text
			{
				Content = label.Label,
				FontSize = ViewStyle.SmallSize,
				FontWeight = isCurrent ? EmphasisWeight : NormalWeight,
				Color = theme.Text,
				FlexShrink = 1,
				MinWidth = 0,
				LineHeight = 1,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( label.IsOnRemote )
			children.Add( GitIcons.Build( GitIcons.Globe, 13, theme.Text.WithAlpha( 0.6 ) ) );

		return new Button
		{
			Handle = EnsureHandle( key ),
			MinHeight = BadgeHeight,
			FlexShrink = 1,
			MinWidth = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing * 0.75 ),
			BorderRadius = theme.Radius,
			BorderWidth = theme.BorderWidth,
			BorderColor = isCurrent ? theme.Accent.WithAlpha( 0.7 ) : theme.Border,
			BackgroundColor = isCurrent ? theme.Accent.WithAlpha( 0.18 ) : theme.Raised,
			HoverStyle = new Style { BorderColor = isCurrent ? theme.Accent : theme.Muted },
			Cursor = Cursor.Pointer,
			AccessibilityName = reference.Kind switch
			{
				GitRefKind.Tag => $"Tag {reference.ShortName}",
				GitRefKind.RemoteBranch => $"Remote branch {reference.ShortName}",
				_ => $"Branch {reference.ShortName}{(isCurrent ? ", current" : "")}{(label.IsOnRemote ? ", also on the remote" : "")}",
			},
			OnClick = () => _context.Run( () => SelectCommit( reference.Target, reveal: false ) ),
			OnPointerDown = pointer => _context.Run( () =>
			{
				if ( pointer.Button == PointerButton.Secondary )
					OpenMenu( key, $"{reference.ShortName} options", BuildRefMenu( reference ), pointer.Position );
			} ),
			OnPointerUp = pointer => _context.Run( () =>
			{
				if ( pointer.Button == PointerButton.Primary && pointer.ClickCount == 2 )
					Checkout( reference );
			} ),
			Children = children,
		};
	}

	/// <summary>Builds the count of further branches and tags at a commit; clicking lists them to check one out.</summary>
	private Button BuildMoreRefs( string commit, IReadOnlyList<GitRef> others )
	{
		var theme = Theme.Current;
		var key = "more-refs:" + commit;
		return new Button
		{
			Handle = EnsureHandle( key ),
			MinHeight = BadgeHeight,
			FlexShrink = 0,
			JustifyContent = JustifyContent.Center,
			Padding = new Edges( 0, theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			HoverStyle = new Style { BorderColor = theme.Muted },
			Cursor = Cursor.Pointer,
			AccessibilityName = $"{others.Count:N0} more: {string.Join( ", ", others.Select( item => item.ShortName ) )}",
			OnClick = () => _context.Run( () => OpenMenu( key, "More branches and tags", [.. others.Select( item => (MenuEntry)BuildCommand( $"Checkout {item.ShortName}", () => Checkout( item ), !item.IsCurrent ) )], new Point { X = 0, Y = BadgeHeight } ) ),
			Children = [new Text { Content = $"+{others.Count:N0}", FontSize = ViewStyle.SmallSize - 1, Color = theme.Muted, LineHeight = 1, TextWrap = TextWrap.NoWrap }],
		};
	}

	/// <summary>Lists a branch's or tag's actions by its kind.</summary>
	private List<MenuEntry> BuildRefMenu( GitRef reference ) => reference.Kind switch
	{
		GitRefKind.LocalBranch => BuildLocalBranchMenu( reference ),
		GitRefKind.RemoteBranch => BuildRemoteBranchMenu( reference ),
		_ => BuildTagMenu( reference ),
	};

	/// <summary>Formats a date for the table: the time today, the month and day this year, else the whole date.</summary>
	private static string FormatShortDate( DateTimeOffset date )
	{
		var local = date.ToLocalTime();
		var now = DateTimeOffset.Now;
		if ( local.Date == now.Date )
			return local.ToString( "HH:mm", CultureInfo.CurrentCulture );
		return local.Year == now.Year
			? local.ToString( "MMM d", CultureInfo.CurrentCulture )
			: local.ToString( "yyyy-MM-dd", CultureInfo.CurrentCulture );
	}
}
