using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Names the kinds of rows in the commit details list.</summary>
internal enum GitDetailKind
{
	/// <summary>The commit's subject, identifiers, people, refs, parents and message.</summary>
	Info,

	/// <summary>A muted line, such as "Reading files…" or an error.</summary>
	Note,

	/// <summary>The changed file count with Expand all.</summary>
	FilesHeader,

	/// <summary>A changed file's header, which expands or collapses it.</summary>
	FileHeader,

	/// <summary>A run of a file's diff lines, or its note.</summary>
	Lines,
}

/// <summary>One row of the commit details list.</summary>
/// <param name="Key">The stable row key.</param>
/// <param name="Kind">What the row shows.</param>
/// <param name="Text">The note's text, the files header's label, or a file's path key.</param>
/// <param name="Commit">The commit an info row describes, or null for a stash outside the log.</param>
/// <param name="Stash">The stash an info row describes, or null.</param>
/// <param name="Refs">The refs pointing at the info row's commit.</param>
/// <param name="File">The file a header or run of lines belongs to.</param>
/// <param name="Expanded">Whether a file shows its lines, or for the files header whether every file does.</param>
/// <param name="Start">The index of a run's first line.</param>
/// <param name="Count">The number of lines in a run.</param>
/// <param name="Digits">The digit count of the file's widest line number.</param>
/// <param name="IsLast">Whether a run closes its file.</param>
/// <param name="IsFirst">Whether a file header is the first file.</param>
internal sealed record GitDetailRow(
	string Key,
	GitDetailKind Kind,
	string Text = "",
	GitCommit? Commit = null,
	GitStash? Stash = null,
	IReadOnlyList<GitRef>? Refs = null,
	DiffFile? File = null,
	bool Expanded = false,
	int Start = 0,
	int Count = 0,
	int Digits = 0,
	bool IsLast = false,
	bool IsFirst = false ) : IMeasuredRow;

/// <summary>One file of the Changes tab's list.</summary>
/// <param name="Key">The file's path key, the stable row key.</param>
/// <param name="File">The changed file.</param>
/// <param name="IsSelected">Whether the file's diff is shown.</param>
internal sealed record GitChangesTabRow( string Key, GitFileChange File, bool IsSelected ) : IMeasuredRow;

/// <summary>Builds the selected commit's details: the Commit tab with its information and expandable file diffs, and the Changes tab with a file list beside one file's diff.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines how many changed lines a commit may have for its whole patch to load with its files; larger commits load each file when it is expanded.</summary>
	private const int PreloadLines = 4000;

	/// <summary>Defines how many lines one diff row holds.</summary>
	private const int BlockLines = 50;

	/// <summary>Defines the approximate height of one diff line in logical pixels, for estimates before layout.</summary>
	private const double DiffLineHeight = 17;

	/// <summary>Defines the widest the Changes tab's file list gets, in logical pixels.</summary>
	private const int FileListWidth = 260;

	/// <summary>Defines the Changes tab's file list share of the tab's width, in percent, so a narrow side view keeps room for the diff.</summary>
	private const double FileListPercent = 40;

	/// <summary>Defines the label column width of the commit information in logical pixels.</summary>
	private const int LabelWidth = 52;

	/// <summary>Defines the author's avatar diameter in the commit information, in logical pixels.</summary>
	private const double AuthorAvatarSize = 38;

	/// <summary>Targets the details list.</summary>
	private readonly ElementHandle _detailList = new();

	/// <summary>Builds one details row; kept as one delegate so the list reuses unchanged rows.</summary>
	private readonly Func<GitDetailRow, Blob> _buildDetailRow;

	/// <summary>Builds one file of the Changes tab's list; kept as one delegate so the list reuses unchanged rows.</summary>
	private readonly Func<GitChangesTabRow, Blob> _buildFileItem;

	/// <summary>Stores the loaded diffs of the selected commit's files by path key.</summary>
	private readonly Dictionary<string, DiffFile> _patches = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the path keys of files whose diff is loading.</summary>
	private readonly HashSet<string> _loadingPatches = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the path keys of expanded files in the Commit tab.</summary>
	private readonly HashSet<string> _expandedFiles = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Cancels the running details load when the selection changes or the panel closes.</summary>
	private CancellationTokenSource? _details;

	/// <summary>Stores the selected commit's changed files, or null while they load.</summary>
	private IReadOnlyList<GitFileChange>? _commitFiles;

	/// <summary>Stores why the details could not load, or null.</summary>
	private string? _detailsError;

	/// <summary>Stores whether the Changes tab is shown rather than the Commit tab.</summary>
	private bool _isChangesTab;

	/// <summary>Stores the path key of the file shown in the Changes tab, or null for the first.</summary>
	private string? _selectedFile;

	/// <summary>Builds the tabs and the shown tab, below the commit list.</summary>
	private Container BuildDetails()
	{
		var theme = Theme.Current;
		Blob body = _isChangesTab ? BuildChangesTab() : BuildDetailList( "git-details-commit", ComputeCommitRows() );
		return new Container
		{
			Key = "git-details",
			FlexGrow = 45,
			FlexBasis = 0,
			MinHeight = 0,
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				new Row
				{
					Key = "git-tabs",
					FlexShrink = 0,
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing / 2,
					Padding = new Edges( 0, theme.Spacing ),
					BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
					BorderColor = theme.Border,
					Children =
					[
						BuildTab( "Commit", !_isChangesTab, () => _isChangesTab = false ),
						BuildTab( "Changes", _isChangesTab, () => _isChangesTab = true ),
						new Spacer(),
						BuildCloseDetails(),
					],
				},
				new Container
				{
					Key = "git-details-body",
					FlexGrow = 1,
					MinHeight = 0,
					Children = [body],
				},
			],
		};
	}

	/// <summary>Builds one tab, underlined in the accent while shown.</summary>
	private Button BuildTab( string label, bool isSelected, Action select )
	{
		var theme = Theme.Current;
		var tab = theme.BuildButton( label, () => _context.Run( () =>
		{
			select();
			MarkChanged();
		} ) );
		tab.BorderRadius = 0;
		tab.Color = isSelected ? theme.Text : theme.Muted;
		tab.BorderWidth = new Edges( 0, 0, 2, 0 );
		tab.BorderColor = isSelected ? theme.Accent : Color.Transparent;
		tab.AccessibilityName = isSelected ? $"{label} tab, shown" : $"{label} tab";
		return tab;
	}

	/// <summary>Builds the button that hides the details.</summary>
	private Button BuildCloseDetails()
	{
		var close = Theme.Current.BuildButton( "×", () => _context.Run( () =>
		{
			ClearSelection();
			MarkChanged();
		} ) );
		close.AccessibilityName = "Hide commit details";
		close.Width = Theme.Current.ControlHeight;
		close.Padding = 0;
		close.Color = Theme.Current.Muted;
		return close;
	}

	/// <summary>Builds a virtual list of details rows.</summary>
	private Virtual BuildDetailList( string key, IReadOnlyList<GitDetailRow> rows ) => new()
	{
		Key = key,
		Handle = _detailList,
		Source = new MeasuredRowsSource<GitDetailRow>( rows, _buildDetailRow, ComputeDetailEstimate, FollowEnd: false ),
		AccessibilityName = "Commit details",
		FlexGrow = 1,
		MinWidth = 0,
		MinHeight = 0,
		Padding = new Edges( Theme.Current.Spacing, Theme.Current.Spacing * 2 ),
		OverflowX = Overflow.Hidden,
		ScrollbarY = ViewStyle.GetScrollbar(),
	};

	/// <summary>Lists the Commit tab's rows: the information, then the files with the expanded ones' lines.</summary>
	private List<GitDetailRow> ComputeCommitRows()
	{
		var commit = _selectedId is { } id && _history is { } history && history.Index.TryGetValue( id, out var index ) ? history.Commits[index] : null;
		var rows = new List<GitDetailRow>
		{
			new( "info:" + _selectedId, GitDetailKind.Info, Commit: commit, Stash: _selectedStash, Refs: commit is null ? null : _refsByCommit.GetValueOrDefault( commit.Id ) ),
		};
		if ( _detailsError is { } error )
		{
			rows.Add( new( "note", GitDetailKind.Note, error ) );
			return rows;
		}
		if ( _commitFiles is not { } files )
		{
			rows.Add( new( "note", GitDetailKind.Note, "Reading files…" ) );
			return rows;
		}

		var allExpanded = files.Count > 0 && files.All( file => _expandedFiles.Contains( ComputePathKey( file.Path ) ) );
		rows.Add( new( "files", GitDetailKind.FilesHeader, files.Count == 1 ? "1 file changed" : $"{files.Count:N0} files changed", Expanded: allExpanded ) );
		foreach ( var change in files )
		{
			var key = ComputePathKey( change.Path );
			var expanded = _expandedFiles.Contains( key );
			AddFileRows( rows, "c:", change, key, expanded, isFirst: false );
		}
		return rows;
	}

	/// <summary>Adds a file's header and, when it is expanded, its lines in runs, or a loading note while its diff loads.</summary>
	private void AddFileRows( List<GitDetailRow> rows, string prefix, GitFileChange change, string key, bool expanded, bool isFirst )
	{
		var loaded = _patches.GetValueOrDefault( key );
		var file = loaded ?? BuildPlaceholder( change, null );
		rows.Add( new( prefix + "h:" + key, GitDetailKind.FileHeader, key, File: file, Expanded: expanded, IsFirst: isFirst ) );
		if ( !expanded )
			return;

		if ( loaded is null || loaded.Note is not null || loaded.Lines.Count == 0 )
		{
			var note = loaded ?? BuildPlaceholder( change, "Loading…" );
			if ( note.Note is null )
				note = note with { Note = "No changes to show" };
			rows.Add( new( prefix + "n:" + key, GitDetailKind.Lines, key, File: note, Expanded: true, IsLast: true ) );
			return;
		}

		var digits = DiffView.ComputeDigits( loaded );
		for ( var start = 0; start < loaded.Lines.Count; start += BlockLines )
		{
			var count = Math.Min( BlockLines, loaded.Lines.Count - start );
			rows.Add( new( $"{prefix}b:{key}:{start}", GitDetailKind.Lines, key, File: loaded, Expanded: true, Start: start, Count: count, Digits: digits,
				IsLast: start + count >= loaded.Lines.Count ) );
		}
	}

	/// <summary>Builds the file list on the left and the selected file's diff on the right.</summary>
	private Blob BuildChangesTab()
	{
		var theme = Theme.Current;
		if ( _detailsError is { } error )
			return BuildMessage( error );
		if ( _commitFiles is not { } files )
			return BuildMessage( "Reading files…" );
		if ( files.Count == 0 )
			return BuildMessage( "This commit changes no files." );

		var selected = files.FirstOrDefault( file => string.Equals( ComputePathKey( file.Path ), _selectedFile, StringComparison.OrdinalIgnoreCase ) ) ?? files[0];
		var selectedKey = ComputePathKey( selected.Path );
		EnsurePatch( selected );
		var items = new List<GitChangesTabRow>( files.Count );
		foreach ( var file in files )
		{
			var key = ComputePathKey( file.Path );
			items.Add( new( key, file, string.Equals( key, selectedKey, StringComparison.OrdinalIgnoreCase ) ) );
		}

		var rows = new List<GitDetailRow>();
		AddFileRows( rows, "x:", selected, selectedKey, expanded: true, isFirst: true );
		return new Row
		{
			Key = "git-changes-tab",
			FlexGrow = 1,
			MinHeight = 0,
			Children =
			[
				new Virtual
				{
					Key = "git-changes-files",
					Source = new MeasuredRowsSource<GitChangesTabRow>( items, _buildFileItem, static _ => Theme.Current.ControlHeight, FollowEnd: false ),
					AccessibilityName = "Changed files",
					Width = Length.Percent( FileListPercent ),
					MaxWidth = FileListWidth,
					FlexShrink = 0,
					MinHeight = 0,
					OverflowX = Overflow.Hidden,
					ScrollbarY = ViewStyle.GetScrollbar(),
					Padding = theme.Spacing / 2,
					BorderWidth = new Edges( 0, theme.BorderWidth, 0, 0 ),
					BorderColor = theme.Border,
				},
				BuildDetailList( "git-details-changes", rows ),
			],
		};
	}

	/// <summary>Builds one file of the Changes tab's list: its status letter, name and change counts.</summary>
	private Button BuildFileItem( GitChangesTabRow row )
	{
		var theme = Theme.Current;
		var file = row.File;
		var key = row.Key;
		var children = new List<Blob>
		{
			new Text
			{
				Content = file.Status.ToString(),
				Width = theme.Spacing * 1.5,
				FlexShrink = 0,
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize,
				Color = ComputeStatusColor( file.Status ),
			},
			new Text
			{
				Content = Path.GetFileName( key ) is { Length: > 0 } name ? name : key,
				FlexShrink = 1,
				MinWidth = 0,
				FontSize = ViewStyle.SmallSize,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			new Spacer(),
		};
		if ( file.Added > 0 )
			children.Add( new Text { Content = $"+{file.Added:N0}", Color = theme.Success, FontSize = ViewStyle.SmallSize, FlexShrink = 0, } );
		if ( file.Removed > 0 )
			children.Add( new Text { Content = $"−{file.Removed:N0}", Color = theme.Error, FontSize = ViewStyle.SmallSize, FlexShrink = 0, } );

		var button = theme.BuildButton( key, () => _context.Run( () =>
		{
			_selectedFile = key;
			MarkChanged();
		} ) );
		button.Key = "file:" + key;
		button.AccessibilityName = $"Show {key}";
		button.Height = Length.Auto;
		button.MinHeight = theme.ControlHeight;
		button.FlexDirection = FlexDirection.Row;
		button.JustifyContent = JustifyContent.FlexStart;
		button.Gap = theme.Spacing / 2;
		button.BackgroundColor = row.IsSelected ? theme.Accent.WithAlpha( SelectionOpacity ) : Color.Transparent;
		button.Children = children;
		return button;
	}

	/// <summary>Gets the color of a change's status letter.</summary>
	private static Color ComputeStatusColor( char status ) => status switch
	{
		'A' or '?' => Theme.Current.Success,
		'D' or 'U' => Theme.Current.Error,
		'R' or 'C' => Theme.Current.AnsiBlue,
		_ => Theme.Current.AnsiYellow,
	};

	/// <summary>Estimates a details row's height until layout measures it.</summary>
	private static double ComputeDetailEstimate( GitDetailRow row ) => row.Kind switch
	{
		GitDetailKind.Info => 150,
		GitDetailKind.FilesHeader => 40,
		GitDetailKind.FileHeader => 38,
		GitDetailKind.Lines => Math.Max( 1, row.Count ) * DiffLineHeight + 8,
		_ => 30,
	};

	/// <summary>Builds one details row.</summary>
	private Blob BuildDetailRow( GitDetailRow row )
	{
		var theme = Theme.Current;
		switch ( row.Kind )
		{
			case GitDetailKind.Info:
				return BuildCommitInfo( row );

			case GitDetailKind.FilesHeader:
				var toggle = theme.BuildButton( row.Expanded ? "Collapse all" : "Expand all", () => _context.Run( UpdateExpandAll ) );
				toggle.Color = theme.Accent;
				return new Row
				{
					AlignItems = AlignItems.Center,
					Padding = new Edges( theme.Spacing, 0, theme.Spacing / 2, 0 ),
					Children =
					[
						new Text { Content = row.Text, FontWeight = EmphasisWeight, FlexGrow = 1, MinWidth = 0, },
						toggle,
					],
				};

			case GitDetailKind.FileHeader when row.File is { } file:
				var key = row.Text;
				return new Container
				{
					Padding = new Edges( row.IsFirst ? 0 : theme.Spacing, 0, 0, 0 ),
					Children = [DiffView.BuildFileHeader( file, row.Expanded, () => _context.Run( () => UpdateExpanded( key ) ) )],
				};

			case GitDetailKind.Lines when row.File is { } file:
				return DiffView.BuildLineBlock( file, row.Start, row.Count, row.Digits, row.IsLast );

			default:
				return new Text
				{
					Content = row.Text,
					Padding = new Edges( theme.Spacing, 0 ),
					Color = theme.Muted,
				};
		}
	}

	/// <summary>Builds the commit's subject, its branches and tags, its author with avatar and dates, its SHA and parents, and its message; a stash
	/// outside the log shows its name, message and SHA.</summary>
	/// <remarks>Every one-line value keeps to one line and ends in an ellipsis when the column is narrow; only the subject and the message wrap.</remarks>
	private Container BuildCommitInfo( GitDetailRow row )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( row.Commit is not { } commit )
		{
			children.Add( BuildTitle( row.Stash?.Message is { Length: > 0 } message ? message : row.Stash?.Name ?? "Commit" ) );
			if ( row.Stash is { } stash )
			{
				children.Add( BuildDivider() );
				children.Add( BuildInfoLine( "Stash", BuildCode( stash.Name ) ) );
				children.Add( BuildInfoLine( "SHA", BuildCode( stash.Commit ) ) );
			}
			return BuildInfoCard( children );
		}

		children.Add( BuildTitle( commit.Subject ) );
		if ( row.Refs is { Count: > 0 } refs )
			children.Add( new Row
			{
				Gap = theme.Spacing / 2,
				FlexWrap = FlexWrap.Wrap,
				Children = [.. ComputeRefLabels( refs ).Select( BuildRefChip )],
			} );
		children.Add( BuildPerson( commit ) );
		children.Add( BuildDivider() );
		children.Add( BuildInfoLine( "SHA", BuildCode( commit.Id ) ) );
		if ( commit.Parents.Count > 0 )
			children.Add( BuildInfoLine( commit.Parents.Count == 1 ? "Parent" : "Parents", new Row
			{
				Gap = theme.Spacing / 2,
				FlexWrap = FlexWrap.Wrap,
				FlexShrink = 1,
				MinWidth = 0,
				Children = [.. commit.Parents.Select( BuildParentLink )],
			} ) );
		if ( commit.Body.Trim() is { Length: > 0 } body )
		{
			children.Add( BuildDivider() );
			children.Add( new Text { Content = body, Selectable = true, Color = theme.Text.WithAlpha( 0.85 ), LineHeight = 1.4 } );
		}
		return BuildInfoCard( children );
	}

	/// <summary>Builds the commit's subject as the column's title, wrapping.</summary>
	private static Text BuildTitle( string subject ) => new()
	{
		Content = subject,
		Selectable = true,
		FontSize = Theme.Current.FontSize + 2,
		FontWeight = EmphasisWeight,
		LineHeight = 1.3,
		Color = Theme.Current.Text,
	};

	/// <summary>Builds the author: an avatar beside their name and address, when they wrote it, and when it was committed and by whom when that differs.</summary>
	private Row BuildPerson( GitCommit commit )
	{
		var theme = Theme.Current;
		var details = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				MinWidth = 0,
				Children =
				[
					new Text { Content = commit.AuthorName, Selectable = true, FontWeight = EmphasisWeight, Color = theme.Text, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
					new Text { Content = commit.AuthorEmail, Selectable = true, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 3, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				],
			},
			new Text { Content = FormatPersonDate( commit.AuthorDate ), FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
		};
		var isOtherCommitter = commit.CommitterName != commit.AuthorName || commit.CommitterEmail != commit.AuthorEmail;
		if ( isOtherCommitter || commit.CommitDate != commit.AuthorDate )
			details.Add( new Row
			{
				Gap = theme.Spacing,
				MinWidth = 0,
				Padding = new Edges( 2, 0, 0, 0 ),
				Children =
				[
					new Text { Content = "Committed", FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0, TextWrap = TextWrap.NoWrap },
					new Text
					{
						Content = isOtherCommitter ? $"{FormatDay( commit.CommitDate )} by {commit.CommitterName}" : FormatDay( commit.CommitDate ),
						FontSize = ViewStyle.SmallSize,
						Color = theme.Text.WithAlpha( 0.8 ),
						FlexShrink = 1,
						MinWidth = 0,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
				],
			} );

		Blob avatar = _pictures is { } pictures
			? Cell.Mount<GitAvatarInput, GitAvatar>( "author-avatar", new( pictures, commit.AuthorName, commit.AuthorEmail, AuthorAvatarSize, ComputeLaneColor( ComputeAuthorColor( commit.AuthorEmail ) ) ) )
			: new Container { Width = AuthorAvatarSize, Height = AuthorAvatarSize, FlexShrink = 0 };
		return new Row
		{
			AlignItems = AlignItems.FlexStart,
			Gap = theme.Spacing * 1.5,
			MinWidth = 0,
			Padding = new Edges( theme.Spacing / 2, 0, 0, 0 ),
			Children =
			[
				avatar,
				new Container { FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Gap = 2, Children = details },
			],
		};
	}

	/// <summary>Gets a stable lane color index for an author, so their initials keep one color everywhere.</summary>
	private static int ComputeAuthorColor( string email )
	{
		var hash = 17;
		foreach ( var character in email.Trim().ToLowerInvariant() )
			hash = hash * 31 + character;
		return hash & 0x7fffffff;
	}

	/// <summary>Builds a branch or tag as a quiet chip: its icon, name and a globe when it is on a remote too.</summary>
	private static Row BuildRefChip( GitRefLabel label )
	{
		var theme = Theme.Current;
		var isCurrent = label.Ref.IsCurrent;
		var children = new List<Blob>
		{
			GitIcons.Build( label.Icon, 13, isCurrent ? theme.Accent : theme.Text.WithAlpha( 0.7 ) ),
			new Text { Content = label.Label, FontSize = ViewStyle.SmallSize, Color = theme.Text, FlexShrink = 1, MinWidth = 0, LineHeight = 1, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
		};
		if ( label.IsOnRemote )
			children.Add( GitIcons.Build( GitIcons.Globe, 13, theme.Text.WithAlpha( 0.6 ) ) );
		return new Row
		{
			MinHeight = 20,
			MaxWidth = Length.Percent( 100 ),
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing * 0.75 ),
			BorderRadius = theme.Radius,
			BorderWidth = theme.BorderWidth,
			BorderColor = isCurrent ? theme.Accent.WithAlpha( 0.7 ) : theme.Border,
			BackgroundColor = isCurrent ? theme.Accent.WithAlpha( 0.18 ) : theme.Raised,
			Children = children,
		};
	}

	/// <summary>Builds a thin rule between groups of the information.</summary>
	private static Container BuildDivider() => new() { Height = Theme.Current.BorderWidth, FlexShrink = 0, BackgroundColor = Theme.Current.Border, Margin = new Edges( Theme.Current.Spacing / 2, 0 ) };

	/// <summary>Wraps the information.</summary>
	private static Container BuildInfoCard( List<Blob> children ) => new()
	{
		Gap = Theme.Current.Spacing,
		MinWidth = 0,
		Padding = new Edges( 0, 0, Theme.Current.Spacing, 0 ),
		Children = children,
	};

	/// <summary>Builds one labelled information line on one line.</summary>
	private static Row BuildInfoLine( string label, Blob value ) => new()
	{
		AlignItems = AlignItems.Center,
		Gap = Theme.Current.Spacing,
		MinWidth = 0,
		Children =
		[
			new Text { Content = label, Width = LabelWidth, FlexShrink = 0, Color = Theme.Current.Muted, FontSize = ViewStyle.SmallSize, TextWrap = TextWrap.NoWrap },
			value,
		],
	};

	/// <summary>Builds a selectable identifier in the code font, on one line.</summary>
	private static Text BuildCode( string content ) => new()
	{
		Content = content,
		Selectable = true,
		FontFamily = ViewStyle.CodeFontFamily,
		FontSize = ViewStyle.SmallSize,
		Color = Theme.Current.Text.WithAlpha( 0.85 ),
		FlexShrink = 1,
		MinWidth = 0,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Builds a parent's short identifier as a chip that selects it; a parent the log does not hold is plain text.</summary>
	private Blob BuildParentLink( string parent )
	{
		var theme = Theme.Current;
		var label = parent.Length > 7 ? parent[..7] : parent;
		if ( _history?.Index.ContainsKey( parent ) != true )
			return BuildCode( label );

		return new Button
		{
			MinHeight = 20,
			FlexShrink = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = 4,
			Padding = new Edges( 0, theme.Spacing * 0.75 ),
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Accent.WithAlpha( 0.12 ),
			HoverStyle = new Style { BackgroundColor = theme.Accent.WithAlpha( 0.24 ) },
			TransitionMs = RowFadeMs,
			TransitionProperties = [TransitionProperty.BackgroundColor],
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = $"Select parent {label}",
			OnClick = () => _context.Run( () => SelectCommit( parent, reveal: true ) ),
			Children =
			[
				GitIcons.Build( GitIcons.Commit, 12, theme.Accent ),
				new Text { Content = label, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Accent, LineHeight = 1, TextWrap = TextWrap.NoWrap },
			],
		};
	}

	/// <summary>Formats when a person wrote a commit: its day, its time and how long ago, such as <c>Nov 16, 2026 · 06:20 · 1d ago</c>.</summary>
	private static string FormatPersonDate( DateTimeOffset date ) => $"{FormatDay( date )} · {FormatAge( date )}";

	/// <summary>Formats a day and time, such as <c>Nov 16, 2026 · 06:20</c>.</summary>
	private static string FormatDay( DateTimeOffset date ) => date.ToLocalTime().ToString( "MMM d, yyyy · HH:mm", CultureInfo.CurrentCulture );

	/// <summary>Formats how long ago a date is, in its largest whole unit, such as <c>15h ago</c>.</summary>
	internal static string FormatAge( DateTimeOffset date )
	{
		var age = DateTimeOffset.Now - date;
		return age switch
		{
			{ TotalMinutes: < 1 } => "just now",
			{ TotalHours: < 1 } => $"{(int)age.TotalMinutes}m ago",
			{ TotalDays: < 1 } => $"{(int)age.TotalHours}h ago",
			{ TotalDays: < 14 } => $"{(int)age.TotalDays}d ago",
			{ TotalDays: < 60 } => $"{(int)(age.TotalDays / 7)}w ago",
			{ TotalDays: < 365 } => $"{(int)(age.TotalDays / 30)}mo ago",
			_ => $"{(int)(age.TotalDays / 365)}y ago",
		};
	}

	/// <summary>Loads the selected commit's or stash's changed files and, when small enough, its whole patch, in the background.</summary>
	private void LoadDetails()
	{
		_details?.Cancel();
		_details = null;
		_commitFiles = null;
		_detailsError = null;
		_selectedFile = null;
		_patches.Clear();
		_loadingPatches.Clear();
		_expandedFiles.Clear();
		if ( _selectedId is not { } id || _root is not { } root )
			return;

		var load = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_details = load;
		_ = Task.Run( async () =>
		{
			try
			{
				var files = await GitRepository.ReadCommitFilesAsync( _context.Workspace, root, id, load.Token ).ConfigureAwait( false );
				IReadOnlyList<DiffFile>? patch = null;
				if ( files.Sum( file => file.Added + file.Removed ) <= PreloadLines )
					patch = GitDiff.Parse( await GitRepository.ReadCommitPatchAsync( _context.Workspace, root, id, null, load.Token ).ConfigureAwait( false ) );
				_context.Post( () => ApplyDetails( load, files, patch, null ) );
			}
			catch ( OperationCanceledException ) when ( load.IsCancellationRequested )
			{
			}
			catch ( Exception error ) when ( error is not OutOfMemoryException )
			{
				_context.Post( () => ApplyDetails( load, null, null, error.Message ) );
			}
		} );
	}

	/// <summary>Shows loaded details unless the selection changed meanwhile.</summary>
	private void ApplyDetails( CancellationTokenSource load, IReadOnlyList<GitFileChange>? files, IReadOnlyList<DiffFile>? patch, string? error )
	{
		if ( _details != load )
			return;

		_detailsError = error;
		_commitFiles = files;
		if ( files is not null && patch is not null )
			StorePatch( files, patch );
		MarkChanged();
	}

	/// <summary>Keeps parsed diffs by path key; a file the patch leaves out gets a note, so it does not load again.</summary>
	private void StorePatch( IReadOnlyList<GitFileChange> files, IReadOnlyList<DiffFile> patch )
	{
		foreach ( var file in patch )
			_patches[ComputePathKey( file.Path )] = file;
		foreach ( var change in files )
		{
			var key = ComputePathKey( change.Path );
			_loadingPatches.Remove( key );
			if ( !_patches.ContainsKey( key ) )
				_patches[key] = BuildPlaceholder( change, change.IsBinary ? "Binary file" : "No changes to show" );
		}
	}

	/// <summary>Loads one file's diff in the background unless it is loaded or loading.</summary>
	private void EnsurePatch( GitFileChange change )
	{
		var key = ComputePathKey( change.Path );
		if ( _patches.ContainsKey( key ) || !_loadingPatches.Add( key ) || _details is not { } load || _selectedId is not { } id || _root is not { } root )
			return;

		if ( change.IsBinary )
		{
			_patches[key] = BuildPlaceholder( change, "Binary file" );
			return;
		}

		_ = Task.Run( async () =>
		{
			try
			{
				var patch = GitDiff.Parse( await GitRepository.ReadCommitPatchAsync( _context.Workspace, root, id, change.Path, load.Token ).ConfigureAwait( false ) );
				_context.Post( () =>
				{
					if ( _details != load )
						return;

					StorePatch( [change], patch );
					MarkChanged();
				} );
			}
			catch ( OperationCanceledException ) when ( load.IsCancellationRequested )
			{
			}
			catch ( Exception error ) when ( error is not OutOfMemoryException )
			{
				_context.Post( () =>
				{
					if ( _details != load )
						return;

					_patches[key] = BuildPlaceholder( change, error.Message );
					_loadingPatches.Remove( key );
					MarkChanged();
				} );
			}
		} );
	}

	/// <summary>Expands or collapses one file of the Commit tab, loading its diff when needed.</summary>
	private void UpdateExpanded( string key )
	{
		if ( !_expandedFiles.Remove( key ) )
		{
			_expandedFiles.Add( key );
			if ( _commitFiles?.FirstOrDefault( file => string.Equals( ComputePathKey( file.Path ), key, StringComparison.OrdinalIgnoreCase ) ) is { } change )
				EnsurePatch( change );
		}
		MarkChanged();
	}

	/// <summary>Collapses every file when all are expanded, else expands every file, loading the missing diffs.</summary>
	private void UpdateExpandAll()
	{
		if ( _commitFiles is not { } files )
			return;

		if ( files.All( file => _expandedFiles.Contains( ComputePathKey( file.Path ) ) ) )
			_expandedFiles.Clear();
		else
		{
			foreach ( var file in files )
			{
				_expandedFiles.Add( ComputePathKey( file.Path ) );
				EnsurePatch( file );
			}
		}
		MarkChanged();
	}

	/// <summary>Builds a diff file without lines for a change whose diff is not loaded, with an optional note in place of its lines.</summary>
	private static DiffFile BuildPlaceholder( GitFileChange change, string? note ) => new(
		ComputePathKey( change.Path ),
		change.Status switch
		{
			'A' or '?' => "create",
			'D' => "delete",
			_ => "update",
		},
		[],
		note );

	/// <summary>Gets a repository path in the form the diff parser gives, which keys loaded diffs.</summary>
	private static string ComputePathKey( string path ) => path.Replace( '/', Path.DirectorySeparatorChar );
}
