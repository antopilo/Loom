using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Holds how one hunk's lines pair up: each removed line with the added line in the same place of its change, which shows what changed
/// inside the line, and the rows of the side-by-side layout.</summary>
/// <param name="Hunk">The hunk.</param>
/// <param name="Partners">Each line's partner index in the hunk, or -1 when it has none.</param>
/// <param name="Pairs">The side-by-side rows: the old line's index on the left and the new line's on the right, -1 for a blank side.</param>
internal sealed record GitHunkLayout( GitHunk Hunk, int[] Partners, IReadOnlyList<(int Left, int Right)> Pairs )
{
	/// <summary>Pairs a hunk's lines: context lines with themselves, and the removed lines of each change with its added lines in order.</summary>
	internal static GitHunkLayout Build( GitHunk hunk )
	{
		var lines = hunk.Lines;
		var partners = new int[lines.Count];
		Array.Fill( partners, -1 );
		var pairs = new List<(int, int)>( lines.Count );
		var index = 0;
		while ( index < lines.Count )
		{
			if ( lines[index].Kind == DiffLineKind.Context )
			{
				pairs.Add( (index, index) );
				index++;
				continue;
			}

			var removedStart = index;
			while ( index < lines.Count && lines[index].Kind == DiffLineKind.Removed )
				index++;
			var addedStart = index;
			while ( index < lines.Count && lines[index].Kind == DiffLineKind.Added )
				index++;

			var removed = addedStart - removedStart;
			var added = index - addedStart;
			for ( var offset = 0; offset < Math.Max( removed, added ); offset++ )
			{
				var left = offset < removed ? removedStart + offset : -1;
				var right = offset < added ? addedStart + offset : -1;
				pairs.Add( (left, right) );
				if ( left >= 0 && right >= 0 )
				{
					partners[left] = right;
					partners[right] = left;
				}
			}
		}

		return new( hunk, partners, pairs );
	}
}

/// <summary>Lists and builds the file view's rows: each hunk's header with its actions, then its lines one under another or side by side.</summary>
internal sealed partial class GitFileView
{
	/// <summary>Defines how many lines or pairs one row holds, so a long file is built and laid out a screen at a time.</summary>
	private const int BlockLines = 50;

	/// <summary>Defines the approximate height of one line in logical pixels, for estimates before layout.</summary>
	private const double LineHeight = 18;

	/// <summary>Defines a hunk header's height in logical pixels.</summary>
	private const int HunkHeight = 28;

	/// <summary>Defines the diff text size in logical pixels.</summary>
	private const int CodeSize = 12;

	/// <summary>Defines the change bar width in logical pixels.</summary>
	private const int BarWidth = 3;

	/// <summary>Defines the line number column width per digit, in ems of the code font.</summary>
	private const double DigitEms = 0.6;

	/// <summary>Defines the opacity of added and removed row tints.</summary>
	private const double TintOpacity = 0.12;

	/// <summary>Defines the opacity of the tint behind the changed part of a line.</summary>
	private const double ChangeOpacity = 0.32;

	/// <summary>Defines the width of the review note column in logical pixels.</summary>
	private const int NoteWidth = 30;

	/// <summary>Defines the spaces one tab expands to.</summary>
	private const string Tab = "    ";

	/// <summary>Lists each hunk's header and its lines in runs of <see cref="BlockLines"/>.</summary>
	private List<GitFileRow> ComputeRows()
	{
		var revision = HashCode.Combine( _revision, _pane.AnnotationRevision, _pane.ReviewFindings );
		var rows = new List<GitFileRow>();
		for ( var hunk = 0; hunk < _layouts.Length; hunk++ )
		{
			rows.Add( new( "h:" + hunk, GitFileRowKind.Hunk, hunk, 0, 0, revision ) );
			var count = _split ? _layouts[hunk].Pairs.Count : _layouts[hunk].Hunk.Lines.Count;
			var kind = _split ? GitFileRowKind.Pairs : GitFileRowKind.Lines;
			for ( var start = 0; start < count; start += BlockLines )
				rows.Add( new( $"{(_split ? "p" : "l")}:{hunk}:{start}", kind, hunk, start, Math.Min( BlockLines, count - start ), revision ) );
		}
		return rows;
	}

	/// <summary>Estimates a row's height until layout measures it.</summary>
	private static double ComputeEstimate( GitFileRow row ) => row.Kind == GitFileRowKind.Hunk ? HunkHeight + 8 : row.Count * LineHeight;

	/// <summary>Builds one row by its kind.</summary>
	private Blob BuildRow( GitFileRow row )
	{
		if ( row.Hunk >= _layouts.Length )
			return new Container();

		return row.Kind switch
		{
			GitFileRowKind.Hunk => BuildHunk( row.Hunk ),
			GitFileRowKind.Pairs => BuildPairs( _layouts[row.Hunk], row.Start, row.Count ),
			_ => BuildLines( _layouts[row.Hunk], row.Start, row.Count ),
		};
	}

	/// <summary>Gets whether hunks of the shown file can be staged, unstaged or discarded on their own: only a modified working-tree file's.</summary>
	/// <remarks>Added, deleted, renamed, untracked and conflicted files change as a whole with the file's own buttons.</remarks>
	private bool CanEditHunks => _target is { Commit: null } && _change is { Status: 'M' };

	/// <summary>Builds a hunk's header: its ranges and section, then Discard and Stage hunk, or Unstage hunk; the hunk the navigation is on is marked.</summary>
	private Container BuildHunk( int index )
	{
		var theme = Theme.Current;
		var hunk = _layouts[index].Hunk;
		var header = hunk.Header;
		var close = header.IndexOf( "@@", 2, StringComparison.Ordinal );
		var range = close > 0 ? header[..(close + 2)] : header;
		var section = close > 0 ? header[(close + 2)..].Trim() : "";
		var children = new List<Blob>
		{
			new Text { Content = range, FontFamily = ViewStyle.CodeFontFamily, FontSize = CodeSize, Color = theme.Accent, FlexShrink = 0, TextWrap = TextWrap.NoWrap },
			new Text { Content = section, FontFamily = ViewStyle.CodeFontFamily, FontSize = CodeSize, Color = theme.Muted, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
			new Spacer(),
		};
		if ( CanEditHunks && _root() is { } root && _patch is { } patch )
		{
			var text = GitPatch.BuildHunkPatch( patch, hunk );
			if ( _target is { Staged: true } )
				children.Add( BuildAction( "Unstage hunk", $"Unstage change {index + 1}", () => RunHunk( index, "Unstaging", token => GitRepository.UnstageHunkAsync( root, text, token ) ) ) );
			else
			{
				var key = "hunk:" + index;
				children.Add( _confirm == key
					? BuildAction( "Discard?", $"Confirm discarding change {index + 1}", () => RunHunk( index, "Discarding", token => GitRepository.DiscardHunkAsync( root, text, token ) ), theme.Error, strong: true )
					: BuildAction( "Discard", $"Discard change {index + 1}", () => Confirm( key ), theme.Error ) );
				children.Add( BuildAction( "Stage hunk", $"Stage change {index + 1}", () => RunHunk( index, "Staging", token => GitRepository.StageHunkAsync( root, text, token ) ), theme.Accent, strong: true ) );
			}
		}

		var isCurrent = index == _hunk;
		return new Container
		{
			Padding = new Edges( index == 0 ? 0 : theme.Spacing, 0, 0, 0 ),
			Children =
			[
				new Row
				{
					Height = HunkHeight,
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing - BarWidth ),
					BackgroundColor = theme.Accent.WithAlpha( isCurrent ? 0.12 : 0.06 ),
					BorderWidth = new Edges( 0, 0, 0, BarWidth ),
					BorderColor = isCurrent ? theme.Accent : theme.Accent.WithAlpha( 0.4 ),
					AccessibilityRole = AccessibilityRole.Group,
					AccessibilityName = $"Change {index + 1}: {range}",
					OnPointerDown = _ => _context.Run( () => SelectHunk( index ) ),
					Children = children,
				},
			],
		};
	}

	/// <summary>Makes a hunk the one the navigation is on.</summary>
	private void SelectHunk( int index )
	{
		if ( _hunk == index )
			return;

		_hunk = index;
		MarkChanged();
	}

	/// <summary>Runs a command on one hunk.</summary>
	private void RunHunk( int index, string verb, Func<CancellationToken, Task> operation )
	{
		_hunk = index;
		_confirm = null;
		_run( $"{verb} change {index + 1} of {Path.GetFileName( _target?.Path ?? "" )}…", operation );
		MarkChanged();
	}

	/// <summary>Builds a run of a hunk's lines one under another: old and new numbers, the review note column, the marker and the code.</summary>
	private Container BuildLines( GitHunkLayout layout, int start, int count )
	{
		var digits = ComputeDigits();
		var grammar = CodeHighlighter.FindForPath( _target?.Path ?? "" );
		var lines = new List<Blob>( count );
		for ( var index = start; index < start + count; index++ )
		{
			var line = layout.Hunk.Lines[index];
			var partner = layout.Partners[index];
			var children = new List<Blob>
			{
				BuildBar( line.Kind ),
				BuildNumber( line.Kind == DiffLineKind.Added ? 0 : line.OldNumber, digits ),
				BuildNumber( line.Kind == DiffLineKind.Removed ? 0 : line.NewNumber, digits ),
			};
			if ( BuildNote( line ) is { } note )
				children.Add( note );
			children.Add( BuildMarker( line.Kind ) );
			children.Add( BuildCode( line, partner >= 0 ? layout.Hunk.Lines[partner].Text : null, grammar ) );
			lines.Add( new Row { MinWidth = 0, BackgroundColor = ComputeTint( line.Kind ), Children = children } );
		}

		return BuildBlock( lines );
	}

	/// <summary>Builds a run of a hunk's pairs side by side: the old line on the left and the new one on the right, blank where a side has none.</summary>
	private Container BuildPairs( GitHunkLayout layout, int start, int count )
	{
		var theme = Theme.Current;
		var digits = ComputeDigits();
		var grammar = CodeHighlighter.FindForPath( _target?.Path ?? "" );
		var rows = new List<Blob>( count );
		for ( var index = start; index < start + count; index++ )
		{
			var (left, right) = layout.Pairs[index];
			rows.Add( new Row
			{
				MinWidth = 0,
				AlignItems = AlignItems.Stretch,
				Children =
				[
					BuildHalf( layout, left, isOld: true, digits, grammar ),
					new Container { Width = theme.BorderWidth, FlexShrink = 0, BackgroundColor = theme.Border },
					BuildHalf( layout, right, isOld: false, digits, grammar ),
				],
			} );
		}

		return BuildBlock( rows );
	}

	/// <summary>Builds one side of a pair: its number, marker and code, or a blank shaded side.</summary>
	private Row BuildHalf( GitHunkLayout layout, int index, bool isOld, int digits, CodeHighlighter.Grammar? grammar )
	{
		var theme = Theme.Current;
		if ( index < 0 )
			return new Row { FlexGrow = 1, FlexBasis = 0, MinWidth = 0, BackgroundColor = theme.Canvas.WithAlpha( 0.6 ) };

		var line = layout.Hunk.Lines[index];
		var partner = layout.Partners[index];
		var children = new List<Blob>
		{
			BuildBar( line.Kind ),
			BuildNumber( isOld ? line.OldNumber : line.NewNumber, digits ),
		};
		if ( BuildNote( line ) is { } note )
			children.Add( note );
		children.Add( BuildMarker( line.Kind ) );
		children.Add( BuildCode( line, partner >= 0 ? layout.Hunk.Lines[partner].Text : null, grammar ) );
		return new Row { FlexGrow = 1, FlexBasis = 0, MinWidth = 0, BackgroundColor = ComputeTint( line.Kind ), Children = children };
	}

	/// <summary>Wraps a run of rows in the code font, clipped at the right edge.</summary>
	private static Container BuildBlock( List<Blob> rows ) => new()
	{
		MinWidth = 0,
		Overflow = Overflow.Hidden,
		FontFamily = ViewStyle.CodeFontFamily,
		FontSize = CodeSize,
		Children = rows,
	};

	/// <summary>Gets the digit count of the shown patch's widest line number, so every row shares one number column.</summary>
	private int ComputeDigits()
	{
		var widest = 1;
		foreach ( var layout in _layouts )
			widest = Math.Max( widest, Math.Max( layout.Hunk.OldStart + layout.Hunk.OldCount, layout.Hunk.NewStart + layout.Hunk.NewCount ) );
		return widest.ToString().Length;
	}

	/// <summary>Gets a row's tint by its kind.</summary>
	private static Color ComputeTint( DiffLineKind kind ) => kind switch
	{
		DiffLineKind.Added => Theme.Current.Success.WithAlpha( TintOpacity ),
		DiffLineKind.Removed => Theme.Current.Error.WithAlpha( TintOpacity ),
		_ => Color.Transparent,
	};

	/// <summary>Builds the change bar at a row's left edge.</summary>
	private static Container BuildBar( DiffLineKind kind ) => new()
	{
		Width = BarWidth,
		FlexShrink = 0,
		BackgroundColor = kind switch
		{
			DiffLineKind.Added => Theme.Current.Success,
			DiffLineKind.Removed => Theme.Current.Error,
			_ => Color.Transparent,
		},
	};

	/// <summary>Builds a right-aligned line number, blank for zero.</summary>
	private static Text BuildNumber( int number, int digits ) => new()
	{
		Content = number > 0 ? number.ToString() : "",
		Width = Length.Em( digits * DigitEms ) + Theme.Current.Spacing * 1.5,
		Padding = new Edges( 0, Theme.Current.Spacing / 2, 0, 0 ),
		FlexShrink = 0,
		TextAlign = TextAlign.Right,
		Color = Theme.Current.Muted,
	};

	/// <summary>Builds the plus or minus marker of a changed line, blank for context.</summary>
	private static Text BuildMarker( DiffLineKind kind ) => new()
	{
		Content = kind switch
		{
			DiffLineKind.Added => "+",
			DiffLineKind.Removed => "−",
			_ => " ",
		},
		Width = Length.Em( 1.4 ),
		FlexShrink = 0,
		TextAlign = TextAlign.Center,
		Color = kind switch
		{
			DiffLineKind.Added => Theme.Current.Success,
			DiffLineKind.Removed => Theme.Current.Error,
			_ => Theme.Current.Muted,
		},
	};

	/// <summary>Builds a working-tree line's review note button, showing its note dot and P0-P3 finding; a line without either shows its + only under
	/// the pointer, so it does not read as a diff marker. Null for a commit's file.</summary>
	private Button? BuildNote( GitPatchLine line )
	{
		if ( _target is not { Commit: null } )
			return null;

		var theme = Theme.Current;
		var row = ToReviewLine( line );
		var label = _pane.DescribeReviewMarker( ReviewPath, row );
		return new Button
		{
			AccessibilityName = label is null ? $"Annotate line {row.Number}" : $"Edit line {row.Number} annotation or finding {label}",
			Focusable = false,
			Cursor = Cursor.Pointer,
			Width = NoteWidth,
			FlexShrink = 0,
			Padding = 0,
			BackgroundColor = label is null ? Color.Transparent : theme.Accent.WithAlpha( 0.18 ),
			Color = label is null ? Color.Transparent : theme.Accent,
			HoverStyle = new Style { BackgroundColor = theme.Hover, Color = label is null ? theme.Muted : theme.Accent },
			OnClick = () => _context.Run( () =>
			{
				_pane.BeginAnnotation( ReviewPath, row );
				MarkChanged();
			} ),
			Children =
			[
				new Text
				{
					Content = label ?? "+",
					FontSize = CodeSize - 1,
					TextAlign = TextAlign.Center,
				},
			],
		};
	}

	/// <summary>Gets the line a review note or finding refers to: its new line, or its old one for a removed line.</summary>
	private static DiffLine ToReviewLine( GitPatchLine line ) =>
		new( line.Kind, line.Kind == DiffLineKind.Removed ? line.OldNumber : line.NewNumber, line.Text );

	/// <summary>Builds a line's code, colored by its language, with the part that differs from its partner line marked: on a tint while lines
	/// run past the edge, in bold change color while they wrap, since Goo draws backgrounds only behind whole elements.</summary>
	private Blob BuildCode( GitPatchLine line, string? partner, CodeHighlighter.Grammar? grammar )
	{
		var text = Expand( line.Text );
		var (start, length) = partner is null || line.Kind == DiffLineKind.Context ? default : ComputeChange( text, Expand( partner ) );
		var isInComment = false;
		List<(int Start, int Length, CodeToken Token)> tokens = grammar is null ? [(0, text.Length, CodeToken.Plain)] : CodeHighlighter.Scan( text, grammar, ref isInComment );
		var ink = line.Kind == DiffLineKind.Added ? Theme.Current.Success : Theme.Current.Error;
		if ( length == 0 || _wrap )
		{
			var code = BuildPiece( text, tokens, 0, text.Length, (start, start + length), ink );
			code.FlexGrow = 1;
			code.FlexShrink = 1;
			code.Padding = new Edges( 0, Theme.Current.Spacing, 0, 0 );
			code.TextWrap = _wrap ? TextWrap.Wrap : TextWrap.NoWrap;
			return code;
		}

		var before = BuildPiece( text, tokens, 0, start, default, ink );
		before.FlexShrink = 0;
		var after = BuildPiece( text, tokens, start + length, text.Length, default, ink );
		after.FlexShrink = 1;
		return new Row
		{
			FlexGrow = 1,
			FlexShrink = 1,
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			Padding = new Edges( 0, Theme.Current.Spacing, 0, 0 ),
			Children =
			[
				before,
				new Container
				{
					FlexShrink = 0,
					BorderRadius = 2,
					BackgroundColor = ink.WithAlpha( ChangeOpacity ),
					Children = [BuildPiece( text, tokens, start, start + length, default, ink )],
				},
				after,
			],
		};
	}

	/// <summary>Expands tabs, cuts a line too long to lay out, and keeps an empty line one row tall.</summary>
	private static string Expand( string text ) => text.Length == 0 ? " " : ViewStyle.CutCodeLine( text.Replace( "\t", Tab ) );

	/// <summary>Finds the part of a line that differs from its partner: what is left between their common start and common end; empty when they are equal.</summary>
	internal static (int Start, int Length) ComputeChange( string text, string partner )
	{
		var prefix = 0;
		var limit = Math.Min( text.Length, partner.Length );
		while ( prefix < limit && text[prefix] == partner[prefix] )
			prefix++;

		var suffix = 0;
		while ( suffix < limit - prefix && text[text.Length - 1 - suffix] == partner[partner.Length - 1 - suffix] )
			suffix++;

		// A change that covers almost the whole line says nothing a tinted row does not already say.
		var length = text.Length - prefix - suffix;
		return length <= 0 || length >= text.Length * 0.9 ? default : (prefix, length);
	}

	/// <summary>Builds one part of a line, from <paramref name="from"/> to <paramref name="to"/>, in its language's colors; characters inside
	/// <paramref name="emphasis"/> show bold in <paramref name="ink"/>.</summary>
	private static Text BuildPiece( string text, List<(int Start, int Length, CodeToken Token)> tokens, int from, int to, (int Start, int End) emphasis, Color ink )
	{
		var spans = new List<TextSpan>( tokens.Count + 2 );
		foreach ( var (start, length, token) in tokens )
		{
			var style = CodeHighlighter.BuildStyle( token );
			var position = Math.Max( start, from );
			var stop = Math.Min( start + length, to );
			while ( position < stop )
			{
				// Each piece ends at the next emphasis boundary inside the token, so the emphasis starts and stops exactly.
				var inside = emphasis.End > emphasis.Start && position >= emphasis.Start && position < emphasis.End;
				var next = inside ? Math.Min( stop, emphasis.End ) : position < emphasis.Start ? Math.Min( stop, emphasis.Start ) : stop;
				var piece = text[position..next];
				spans.Add( inside ? new TextSpan( piece, new Style { Color = ink, FontWeight = 700 } ) : new TextSpan( piece, style! ) );
				position = next;
			}
		}

		var code = spans.Count == 0 ? new Text { Content = "" } : new Text( [.. spans] );
		code.Selectable = true;
		code.MinWidth = 0;
		code.Color = Theme.Current.Text;
		code.TextWrap = TextWrap.NoWrap;
		return code;
	}
}
