using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Names the kinds of rows beside the graph when a commit is selected.</summary>
internal enum GitInspectorKind
{
	/// <summary>The commit's subject, people, identifiers, parents and message.</summary>
	Info,

	/// <summary>A muted line, such as "Reading files…" or an error.</summary>
	Note,

	/// <summary>The changed files' heading: their count and kinds, the lines added and removed, and the path and tree toggles.</summary>
	Files,

	/// <summary>A folder of the file tree.</summary>
	Folder,

	/// <summary>A changed file.</summary>
	File,
}

/// <summary>One row beside the graph when a commit is selected.</summary>
/// <param name="Key">The stable row key.</param>
/// <param name="Kind">What the row shows.</param>
/// <param name="Label">The note, folder name or file name.</param>
/// <param name="Depth">The tree level.</param>
/// <param name="File">The changed file of a file row.</param>
/// <param name="Count">How many files a folder holds.</param>
/// <param name="IsOpen">Whether a folder shows its contents.</param>
/// <param name="Revision">Changes whenever the panel's loaded refs or the file view's options change what the row draws.</param>
internal sealed record GitInspectorRow( string Key, GitInspectorKind Kind, string Label = "", int Depth = 0, GitFileChange? File = null, int Count = 0, bool IsOpen = true, int Revision = 0 ) : IMeasuredRow;

/// <summary>Builds the selected commit's column beside the graph: its message, author, identifiers and parents, then its files as a tree or a
/// list; clicking a file shows its diff. A newly selected commit fades in.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines the column's widest width in logical pixels.</summary>
	private const int InspectorWidth = 380;

	/// <summary>Defines the column's share of the graph view's width, in percent, so a narrow panel keeps room for the table.</summary>
	private const double InspectorPercent = 40;

	/// <summary>Defines a file or folder row's height in logical pixels.</summary>
	private const int InspectorRowHeight = 24;

	/// <summary>Defines the indent per tree level in logical pixels.</summary>
	private const int InspectorIndent = 14;

	/// <summary>Builds one row beside the graph; kept as one delegate so the list reuses unchanged rows.</summary>
	private readonly Func<GitInspectorRow, Blob> _buildInspectorRow;

	/// <summary>Stores the folded folders of the commit's file tree.</summary>
	private readonly HashSet<string> _inspectorFolded = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Reports whether the commit's files show as a flat list of paths rather than a tree.</summary>
	private bool _inspectorAsList;

	/// <summary>Builds the selected commit's details beside the graph, or nothing when no commit is selected; the working tree lives in the Files side view's Changes mode.</summary>
	private Container? BuildInspector()
	{
		if ( _isWorkingTree || _selectedId is null )
			return null;

		var theme = Theme.Current;
		List<Blob> children =
		[
			BuildInspectorHeader(),
			Cell.Mount<GitRevealInput, GitReveal>( "git-inspector-reveal:" + _selectedId, new( new Virtual
			{
				Key = "git-inspector-commit:" + _selectedId,
				Source = new MeasuredRowsSource<GitInspectorRow>( ComputeInspectorRows(), _buildInspectorRow, ComputeInspectorEstimate, FollowEnd: false ),
				AccessibilityName = "Commit details",
				FlexGrow = 1,
				MinWidth = 0,
				MinHeight = 0,
				Padding = new Edges( 0, theme.Spacing, theme.Spacing * 2, theme.Spacing ),
				OverflowX = Overflow.Hidden,
				ScrollbarY = ViewStyle.GetScrollbar(),
			} ) ),
		];
		return new Container
		{
			Key = "git-inspector",
			Width = Length.Percent( InspectorPercent ),
			MaxWidth = InspectorWidth,
			FlexShrink = 0,
			MinHeight = 0,
			BorderWidth = new Edges( 0, 0, 0, theme.BorderWidth ),
			BorderColor = theme.Border,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Commit",
			Children = children,
		};
	}

	/// <summary>Builds the commit column's heading: Commit, its short SHA, copying the SHA, and closing the column.</summary>
	private Row BuildInspectorHeader()
	{
		var theme = Theme.Current;
		var id = _selectedId ?? "";
		var shortId = id.Length > 7 ? id[..7] : id;
		return new Row
		{
			Key = "git-inspector-header",
			MinHeight = TableHeaderHeight + theme.Spacing,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing * 1.5 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				GitIcons.Build( GitIcons.Commit, 14, theme.Muted ),
				new Text { Content = "Commit", FontWeight = EmphasisWeight, FlexShrink = 0, TextWrap = TextWrap.NoWrap },
				new Text { Content = shortId, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0, TextWrap = TextWrap.NoWrap },
				new Spacer(),
				BuildIconTool( "inspector-copy", GitIcons.Copy, "Copy the SHA", id.Length > 0, () => _context.App.Window?.Clipboard.WriteText( id ) ),
				BuildIconTool( "inspector-close", GitIcons.Close, "Close the commit details", true, SelectWorkingTree ),
			],
		};
	}

	/// <summary>Lists the commit's information, then its files' heading and the files as a tree or a list.</summary>
	private List<GitInspectorRow> ComputeInspectorRows()
	{
		var revision = HashCode.Combine( _refsByCommit, _inspectorAsList, _inspectorFolded.Count );
		var rows = new List<GitInspectorRow> { new( "info:" + _selectedId, GitInspectorKind.Info, Revision: revision ) };
		if ( _detailsError is { } error )
		{
			rows.Add( new( "note", GitInspectorKind.Note, error ) );
			return rows;
		}
		if ( _commitFiles is not { } files )
		{
			rows.Add( new( "note", GitInspectorKind.Note, "Reading files…" ) );
			return rows;
		}

		rows.Add( new( "files", GitInspectorKind.Files, Count: files.Count, Revision: revision ) );
		if ( _inspectorAsList )
		{
			foreach ( var file in files )
				rows.Add( new( "f:" + file.Path, GitInspectorKind.File, Path.GetFileName( file.Path ), File: file, Revision: revision ) );
			return rows;
		}

		foreach ( var entry in PathTree.Build( files, file => file.Path, path => _inspectorFolded.Contains( path ) ) )
			rows.Add( entry.Item is { } file
				? new( "f:" + file.Path, GitInspectorKind.File, entry.Label, entry.Depth, file, Revision: revision )
				: new( "d:" + entry.Path, GitInspectorKind.Folder, entry.Label, entry.Depth, Count: entry.Count, IsOpen: entry.IsOpen, Revision: revision ) );
		return rows;
	}

	/// <summary>Estimates a row's height until layout measures it.</summary>
	private static double ComputeInspectorEstimate( GitInspectorRow row ) => row.Kind switch
	{
		GitInspectorKind.Info => 190,
		GitInspectorKind.Files => 56,
		_ => InspectorRowHeight,
	};

	/// <summary>Builds one row beside the graph by its kind.</summary>
	private Blob BuildInspectorRow( GitInspectorRow row )
	{
		var theme = Theme.Current;
		switch ( row.Kind )
		{
			case GitInspectorKind.Info:
				var commit = _selectedId is { } id && _history is { } history && history.Index.TryGetValue( id, out var index ) ? history.Commits[index] : null;
				return new Container
				{
					Padding = new Edges( theme.Spacing * 1.5, theme.Spacing / 2, theme.Spacing * 1.5, theme.Spacing / 2 ),
					Children = [BuildCommitInfo( new GitDetailRow( row.Key, GitDetailKind.Info, Commit: commit, Stash: _selectedStash, Refs: commit is null ? null : _refsByCommit.GetValueOrDefault( commit.Id ) ) )],
				};

			case GitInspectorKind.Files:
				return BuildFilesHeading( row.Count );

			case GitInspectorKind.Folder:
				return BuildInspectorFolder( row );

			case GitInspectorKind.File when row.File is { } file:
				return BuildInspectorFile( row, file );

			default:
				return new Text { Content = row.Label, Padding = new Edges( theme.Spacing / 2, theme.Spacing ), Color = theme.Muted };
		}
	}

	/// <summary>Builds the files' heading: Files and the count, the path and tree toggles, then the kinds of change and the lines added and removed.</summary>
	private Container BuildFilesHeading( int count )
	{
		var theme = Theme.Current;
		var files = _commitFiles ?? [];
		var kinds = files
			.GroupBy( file => file.Status switch
			{
				'A' or 'C' => "added",
				'D' => "deleted",
				'R' => "renamed",
				_ => "modified",
			} )
			.OrderByDescending( group => group.Count() )
			.Select( group => $"{group.Count():N0} {group.Key}" );
		var added = files.Sum( file => file.Added );
		var removed = files.Sum( file => file.Removed );
		var counts = new List<Blob>();
		if ( added > 0 )
			counts.Add( new Text { Content = $"+{added:N0}", FontSize = ViewStyle.SmallSize, Color = theme.Success, FlexShrink = 0 } );
		if ( removed > 0 )
			counts.Add( new Text { Content = $"−{removed:N0}", FontSize = ViewStyle.SmallSize, Color = theme.Error, FlexShrink = 0 } );

		return new Container
		{
			Gap = 2,
			Padding = new Edges( theme.Spacing, theme.Spacing / 2, theme.Spacing / 2, theme.Spacing / 2 ),
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing / 2,
					Children =
					[
						new Text { Content = "Files", FontWeight = EmphasisWeight, FlexShrink = 0 },
						BuildChip( count.ToString( "N0" ), theme.Muted ),
						new Spacer(),
						BuildIconTool( "inspector-list", GitIcons.List, "Show files as a list", true, () => UpdateInspectorList( true ), isOn: _inspectorAsList ),
						BuildIconTool( "inspector-tree", GitIcons.Folder, "Show files as folders", true, () => UpdateInspectorList( false ), isOn: !_inspectorAsList ),
					],
				},
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing / 2,
					Children =
					[
						new Text { Content = string.Join( " · ", kinds ), FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
						new Spacer(),
						.. counts,
					],
				},
			],
		};
	}

	/// <summary>Builds a folder of the file tree: its chevron, glyph, joined name and file count; clicking folds or unfolds it.</summary>
	private Button BuildInspectorFolder( GitInspectorRow row )
	{
		var theme = Theme.Current;
		var path = row.Key[2..];
		var folder = theme.BuildButton( row.Label, () => _context.Run( () => UpdateInspectorFolded( path ) ) );
		folder.Height = Length.Auto;
		folder.MinHeight = InspectorRowHeight;
		folder.FlexDirection = FlexDirection.Row;
		folder.JustifyContent = JustifyContent.FlexStart;
		folder.AlignItems = AlignItems.Center;
		folder.Gap = theme.Spacing / 2;
		folder.Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing / 2 + row.Depth * InspectorIndent );
		folder.AccessibilityName = $"{(row.IsOpen ? "Fold" : "Unfold")} {row.Label}, {row.Count:N0} changed";
		folder.Children =
		[
			theme.BuildChevron( row.IsOpen ),
			GitIcons.Build( GitIcons.Folder, 13, theme.Muted ),
			new Text { Content = row.Label, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
			new Spacer(),
			new Text { Content = row.Count.ToString( "N0" ), Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 0 },
		];
		return folder;
	}

	/// <summary>Builds a changed file: its status letter, name, folder in the list, and line counts; clicking shows its diff.</summary>
	private Button BuildInspectorFile( GitInspectorRow row, GitFileChange file )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Container
			{
				Width = 15,
				MinHeight = 15,
				FlexShrink = 0,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				BorderRadius = 3,
				BackgroundColor = ComputeStatusColor( file.Status ).WithAlpha( 0.18 ),
				Children =
				[
					new Text
					{
						Content = file.Status.ToString(),
						FontFamily = ViewStyle.CodeFontFamily,
						FontSize = ViewStyle.SmallSize - 2,
						FontWeight = EmphasisWeight,
						LineHeight = 1,
						Color = ComputeStatusColor( file.Status ),
					},
				],
			},
			new Text { Content = row.Label, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
		};
		if ( _inspectorAsList && Path.GetDirectoryName( file.Path ) is { Length: > 0 } folder )
			children.Add( new Text { Content = folder.Replace( Path.DirectorySeparatorChar, '/' ), Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 4, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis } );
		children.Add( new Spacer() );
		if ( file.IsBinary )
			children.Add( new Text { Content = "binary", Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		if ( file.Added > 0 )
			children.Add( new Text { Content = $"+{file.Added:N0}", Color = theme.Success, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		if ( file.Removed > 0 )
			children.Add( new Text { Content = $"−{file.Removed:N0}", Color = theme.Error, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );

		var commit = _selectedId;
		var button = theme.BuildButton( file.Path, () => _context.Run( () =>
		{
			if ( commit is not null )
				ShowFile( new GitFileTarget( file.Path, false, commit ) );
		} ) );
		button.Height = Length.Auto;
		button.MinHeight = InspectorRowHeight;
		button.FlexDirection = FlexDirection.Row;
		button.JustifyContent = JustifyContent.FlexStart;
		button.AlignItems = AlignItems.Center;
		button.Gap = theme.Spacing / 2;
		button.Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing / 2 + row.Depth * InspectorIndent + (_inspectorAsList ? 0 : theme.Spacing * 1.5) );
		button.AccessibilityName = $"Show the diff of {file.Path}";
		button.Children = children;
		return button;
	}

	/// <summary>Shows the commit's files as a list or a tree.</summary>
	private void UpdateInspectorList( bool asList )
	{
		_inspectorAsList = asList;
		MarkChanged();
	}

	/// <summary>Folds or unfolds a folder of the commit's file tree.</summary>
	private void UpdateInspectorFolded( string path )
	{
		if ( !_inspectorFolded.Remove( path ) )
			_inspectorFolded.Add( path );
		MarkChanged();
	}
}
