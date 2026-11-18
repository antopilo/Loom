using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>One row of the commit list.</summary>
/// <param name="Key">The commit's full identifier, the stable row key.</param>
/// <param name="Commit">The commit.</param>
/// <param name="Graph">The row's drawn graph.</param>
/// <param name="GraphWidth">The graph column width shared by every row.</param>
/// <param name="Refs">The branches and tags pointing at the commit.</param>
/// <param name="IsHead">Whether the commit is checked out.</param>
/// <param name="IsSelected">Whether the commit's details are shown.</param>
internal sealed record GitLogRow( string Key, GitCommit Commit, GitGraphPaths Graph, double GraphWidth, IReadOnlyList<GitRef> Refs, bool IsHead, bool IsSelected ) : IMeasuredRow;

/// <summary>Builds the History mode's commits: the searchable list with its graph, and the selected commit's details below it; and the commit
/// selection both layouts share.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines the short identifier column width in logical pixels.</summary>
	private const int ShaWidth = 64;

	/// <summary>Defines how old a commit can be and still show a relative date.</summary>
	private static readonly TimeSpan RelativeAge = TimeSpan.FromDays( 7 );

	/// <summary>Targets the commit list, to reveal the selected commit.</summary>
	private readonly ElementHandle _logList = new();

	/// <summary>Stores one handle per built row or sidebar item, by key, so a context menu can open beside it.</summary>
	private readonly Dictionary<string, ElementHandle> _handles = [];

	/// <summary>Builds one commit row; kept as one delegate so the list reuses unchanged rows.</summary>
	private readonly Func<GitLogRow, Blob> _buildLogRow;

	/// <summary>Stores the commit search text.</summary>
	private string _search = "";

	/// <summary>Stores the full identifier of the selected commit, or null.</summary>
	private string? _selectedId;

	/// <summary>Stores the selected stash, whose commit is not in the log, or null.</summary>
	private GitStash? _selectedStash;

	/// <summary>Stores the rows of the last build with the inputs they were computed from, so unchanged builds reuse them.</summary>
	private (GitHistory? History, string Search, string? Selected, string? Head, object Refs, IReadOnlyList<GitLogRow> Rows) _logRows = (null, "", null, null, new object(), Array.Empty<GitLogRow>());

	/// <summary>Builds the search box, the commit list and, with a commit selected, its details below.</summary>
	private Container BuildCommits()
	{
		var rows = ComputeLogRows();
		var children = new List<Blob> { BuildSearch( rows.Count ) };

		Blob list = _history is null
			? BuildMessage( _isLoading ? "Reading history…" : "No history loaded." )
			: rows.Count == 0
			? BuildMessage( _history.Commits.Count == 0 ? "No commits yet." : "No commit matches the search." )
			: new Virtual
			{
				Key = "git-log",
				Handle = _logList,
				Source = new MeasuredRowsSource<GitLogRow>( rows, _buildLogRow, static _ => GitHistory.RowHeight, FollowEnd: false ),
				AccessibilityName = "Commits",
				FlexGrow = 1,
				MinWidth = 0,
				MinHeight = 0,
				OverflowX = Overflow.Hidden,
				ScrollbarY = ViewStyle.BuildScrollbar(),
			};
		var hasDetails = _selectedId is not null;
		children.Add( new Container
		{
			Key = "git-log-area",
			FlexGrow = hasDetails ? 55 : 1,
			FlexBasis = 0,
			MinHeight = 0,
			Children = [list],
		} );
		if ( hasDetails )
			children.Add( BuildDetails() );

		return new Container
		{
			Key = "git-commits",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Children = children,
		};
	}

	/// <summary>Builds the History mode's commit search box with the count of commits it shows.</summary>
	private Row BuildSearch( int count )
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "git-search",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				new TextEntry
				{
					Key = "git-search-entry",
					Value = _search,
					Controlled = true,
					Placeholder = "Search subject, author or SHA",
					AccessibilityName = "Search commits",
					FlexGrow = 1,
					MinWidth = 0,
					Height = theme.ControlHeight,
					Padding = new Edges( 0, theme.Spacing ),
					BackgroundColor = theme.Raised,
					BorderRadius = theme.Radius,
					OnChange = value => _context.Run( () =>
					{
						_search = value;
						MarkChanged();
					} ),
				},
				new Text
				{
					Content = _history is null ? (_isLoading ? "Loading…" : "") : $"{count:N0} commits",
					Color = theme.Muted,
					FontSize = ViewStyle.SmallSize,
					FlexShrink = 0,
				},
			],
		};
	}

	/// <summary>Lists the commits matching the search with their graph, refs and selection, reusing the last list when nothing it depends on changed.</summary>
	private IReadOnlyList<GitLogRow> ComputeLogRows()
	{
		var head = ComputeHeadId();
		var cached = _logRows;
		if ( cached.History == _history && cached.Search == _search && cached.Selected == _selectedId && cached.Head == head && cached.Refs == _refsByCommit )
			return cached.Rows;

		var rows = new List<GitLogRow>();
		if ( _history is { } history )
		{
			var width = ComputeGraphWidth();
			var query = _search.Trim();
			for ( var index = 0; index < history.Commits.Count; index++ )
			{
				var commit = history.Commits[index];
				if ( query.Length > 0 && !IsMatch( commit, query ) )
					continue;

				rows.Add( new( commit.Id, commit, history.Graph[index], width, _refsByCommit.GetValueOrDefault( commit.Id ) ?? [],
					commit.Id == head, commit.Id == _selectedId ) );
			}
		}

		_logRows = (_history, _search, _selectedId, head, _refsByCommit, rows);
		return rows;
	}

	/// <summary>Reports whether a commit's subject, author or identifier contains the search, ignoring case.</summary>
	private static bool IsMatch( GitCommit commit, string query ) =>
		commit.Subject.Contains( query, StringComparison.OrdinalIgnoreCase )
		|| commit.AuthorName.Contains( query, StringComparison.OrdinalIgnoreCase )
		|| commit.AuthorEmail.Contains( query, StringComparison.OrdinalIgnoreCase )
		|| commit.Id.StartsWith( query, StringComparison.OrdinalIgnoreCase );

	/// <summary>Gets the checked-out commit's full identifier: the current branch's target, else the loaded commit matching the state's short HEAD.</summary>
	private string? ComputeHeadId()
	{
		if ( _refs.FirstOrDefault( item => item.IsCurrent ) is { } current )
			return current.Target;
		if ( _state?.Commit is not { Length: > 0 } shortId || _history is null )
			return null;

		return _history.Commits.FirstOrDefault( commit => commit.Id.StartsWith( shortId, StringComparison.OrdinalIgnoreCase ) )?.Id;
	}

	/// <summary>Builds one History row: graph, ref badges, subject and short identifier; click selects it, right-click opens its menu.</summary>
	private Blob BuildLogRow( GitLogRow row )
	{
		var theme = Theme.Current;
		var commit = row.Commit;
		var children = new List<Blob> { BuildGraph( row.Graph, row.GraphWidth, row.IsHead ) };
		if ( row.IsHead && !row.Refs.Any( item => item.IsCurrent ) )
			children.Add( BuildHeadBadge() );
		foreach ( var reference in row.Refs )
			children.Add( BuildBadge( reference ) );
		children.Add( new Text
		{
			Content = commit.Subject,
			FlexGrow = 1,
			FlexShrink = 1,
			MinWidth = 0,
			FontWeight = row.IsHead ? EmphasisWeight : NormalWeight,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		} );
		// The side view is too narrow for author and date columns; the selected commit's details show them.
		var sha = BuildColumn( commit.ShortId, ShaWidth );
		sha.FontFamily = ViewStyle.CodeFontFamily;
		children.Add( sha );

		var id = commit.Id;
		var key = "commit:" + id;
		void Select() => _context.Run( () => SelectCommit( id, reveal: false ) );
		return new Button
		{
			Handle = EnsureHandle( key ),
			Height = GitHistory.RowHeight,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing / 2 ),
			BackgroundColor = row.IsSelected ? theme.Accent.WithAlpha( SelectionOpacity ) : Color.Transparent,
			HoverStyle = new Style { BackgroundColor = row.IsSelected ? theme.Accent.WithAlpha( SelectionOpacity ) : theme.Hover },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityRole = AccessibilityRole.ListItem,
			AccessibilityName = $"{commit.Subject}, {commit.AuthorName}, {commit.ShortId}",
			OnClick = Select,
			OnPointerDown = pointer => _context.Run( () =>
			{
				if ( pointer.Button != PointerButton.Secondary )
					return;

				SelectCommit( id, reveal: false );
				OpenMenu( key, $"Commit {commit.ShortId} options", BuildCommitMenu( commit ), pointer.Position );
			} ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Select ),
				new( Key.Space, KeyModifiers.None, Select ),
				new( Key.Up, KeyModifiers.None, () => _context.Run( () => SelectNeighbor( id, -1 ) ) ),
				new( Key.Down, KeyModifiers.None, () => _context.Run( () => SelectNeighbor( id, 1 ) ) ),
			],
			Children = children,
		};
	}

	/// <summary>Builds a fixed-width muted column of a commit row.</summary>
	private static Text BuildColumn( string content, int width ) => new()
	{
		Content = content,
		Width = width,
		FlexShrink = 0,
		FontSize = ViewStyle.SmallSize,
		Color = Theme.Current.Muted,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Builds a ref badge: local branches filled in the accent, the current one bold, remote branches outlined, tags in yellow.</summary>
	private static Container BuildBadge( GitRef reference )
	{
		var theme = Theme.Current;
		var (fill, border, text) = reference.Kind switch
		{
			GitRefKind.LocalBranch => (theme.Accent.WithAlpha( 0.22 ), theme.Accent, theme.Text),
			GitRefKind.RemoteBranch => (Color.Transparent, theme.Border, theme.Muted),
			_ => (theme.AnsiYellow.WithAlpha( 0.18 ), theme.AnsiYellow, theme.Text),
		};
		return BuildBadge( reference.IsCurrent ? "✓ " + reference.ShortName : reference.ShortName, fill, border, text, reference.IsCurrent );
	}

	/// <summary>Builds the badge of a detached HEAD.</summary>
	private static Container BuildHeadBadge() => BuildBadge( "HEAD", Theme.Current.Accent, Theme.Current.Accent, Theme.Current.Panel, true );

	/// <summary>Builds one small rounded label.</summary>
	private static Container BuildBadge( string label, Color fill, Color border, Color text, bool isBold ) => new()
	{
		FlexShrink = 0,
		MaxWidth = 180,
		Padding = new Edges( 0, Theme.Current.Spacing / 2 ),
		BackgroundColor = fill,
		BorderColor = border,
		BorderWidth = Theme.Current.BorderWidth,
		BorderRadius = Theme.Current.Radius,
		Children =
		[
			new Text
			{
				Content = label,
				FontSize = ViewStyle.SmallSize,
				FontWeight = isBold ? EmphasisWeight : NormalWeight,
				Color = text,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		],
	};

	/// <summary>Formats a date relative to now within a week, else as a local date and time.</summary>
	private static string FormatDate( DateTimeOffset date )
	{
		var age = DateTimeOffset.Now - date;
		if ( age < TimeSpan.Zero || age >= RelativeAge )
			return date.ToLocalTime().ToString( "yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture );
		if ( age.TotalMinutes < 1 )
			return "just now";
		if ( age.TotalHours < 1 )
			return $"{(int)age.TotalMinutes} min ago";
		if ( age.TotalDays < 1 )
			return (int)age.TotalHours == 1 ? "1 hour ago" : $"{(int)age.TotalHours} hours ago";
		return (int)age.TotalDays == 1 ? "yesterday" : $"{(int)age.TotalDays} days ago";
	}

	/// <summary>Selects a commit, shows it beside the graph and loads its details; optionally scrolls the list to it.</summary>
	private void SelectCommit( string id, bool reveal )
	{
		_isWorkingTree = false;
		if ( _selectedId != id || _selectedStash is not null )
		{
			_selectedStash = null;
			_selectedId = id;
			LoadDetails();
		}

		if ( reveal )
		{
			// A search that hides the commit would leave nothing to scroll to.
			if ( _search.Length > 0 && _history?.Commits.FirstOrDefault( commit => commit.Id == id ) is { } commit && !IsMatch( commit, _search.Trim() ) )
				_search = "";
			_logList.ScrollToItem( id, ScrollAlignment.Center, true );
		}
		MarkChanged();
	}

	/// <summary>Selects a stash and loads its changes.</summary>
	private void SelectStash( GitStash stash )
	{
		_isWorkingTree = false;
		_selectedStash = stash;
		_selectedId = stash.Commit;
		LoadDetails();
		MarkChanged();
	}

	/// <summary>Selects the commit above or below one in the shown list and focuses its row.</summary>
	private void SelectNeighbor( string id, int step )
	{
		var rows = ComputeLogRows();
		var index = -1;
		for ( var i = 0; i < rows.Count; i++ )
		{
			if ( rows[i].Key == id )
			{
				index = i;
				break;
			}
		}
		if ( index < 0 || index + step < 0 || index + step >= rows.Count )
			return;

		var next = rows[index + step].Key;
		SelectCommit( next, reveal: false );
		_logList.ScrollToItem( next, ScrollAlignment.Nearest, true );
		_context.Post( () => EnsureHandle( "commit:" + next ).Focus() );
	}

	/// <summary>Drops the selection when a reload no longer holds its commit or stash.</summary>
	private void UpdateSelectionAfterLoad()
	{
		if ( _selectedStash is { } stash )
		{
			if ( _stashes.FirstOrDefault( item => item.Commit == stash.Commit ) is { } kept )
				_selectedStash = kept;
			else
				ClearSelection();
		}
		else if ( _selectedId is { } id && _history?.Index.ContainsKey( id ) != true )
			ClearSelection();

		// Rows built for commits that are gone keep no handle.
		foreach ( var key in _handles.Keys.Where( key => key.StartsWith( "commit:", StringComparison.Ordinal ) && _history?.Index.ContainsKey( key[7..] ) != true ).ToArray() )
			_handles.Remove( key );
	}

	/// <summary>Clears the selected commit or stash and its details; the panel shows the working tree beside the graph again.</summary>
	private void ClearSelection()
	{
		_isWorkingTree = true;
		_selectedId = null;
		_selectedStash = null;
		LoadDetails();
	}

	/// <summary>Shows the working tree beside the graph.</summary>
	private void SelectWorkingTree()
	{
		ClearSelection();
		MarkChanged();
	}

	/// <summary>Gets the handle kept for a row or item key, creating it the first time.</summary>
	private ElementHandle EnsureHandle( string key )
	{
		if ( !_handles.TryGetValue( key, out var handle ) )
			_handles.Add( key, handle = new ElementHandle() );
		return handle;
	}
}
