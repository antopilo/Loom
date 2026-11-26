using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Builds file diffs: a compact card embedded in the transcript, and full files for the diff panel.</summary>
internal static class DiffView
{
	/// <summary>Defines how many rows a transcript card shows before pointing to the panel.</summary>
	internal const int CardLines = 12;

	/// <summary>Defines the diff text size in logical pixels.</summary>
	private const int CodeSize = 12;

	/// <summary>Defines the change bar width in logical pixels.</summary>
	private const int BarWidth = 3;

	/// <summary>Defines the opacity of added and removed row tints.</summary>
	private const double TintOpacity = 0.12;

	/// <summary>Defines the file header height in logical pixels.</summary>
	private const int HeaderHeight = 30;

	/// <summary>Defines the line number column width per digit, in ems of the code font.</summary>
	private const double DigitEms = 0.6;

	/// <summary>Defines the spaces one tab expands to.</summary>
	private const string Tab = "    ";

	/// <summary>Builds a file's compact card for the transcript: clicking it opens the diff panel, and a fold below shows every row in place.</summary>
	/// <param name="key">The card's stable key.</param>
	/// <param name="file">The file's diff.</param>
	/// <param name="open">Opens the file in the diff panel.</param>
	/// <param name="expanded">Whether the card shows every row rather than the first <see cref="CardLines"/>.</param>
	/// <param name="toggle">Shows or folds the rows past the first <see cref="CardLines"/>.</param>
	/// <param name="header">Whether the card names its file and counts; false when the tool line above it already does.</param>
	internal static Container BuildCard( string key, DiffFile file, Action open, bool expanded, Action toggle, bool header = true )
	{
		var grammar = CodeHighlighter.FindForPath( file.Path );
		var body = new List<Blob>();
		if ( header )
			body.Add( BuildHeader( file, expanded: null, open ) );
		body.Add( BuildLines( file.Lines, expanded ? file.Lines.Count : CardLines, selectable: false, grammar ) );
		var card = new Button
		{
			Key = "card",
			AccessibilityName = $"Open diff of {file.Path}",
			FlexDirection = FlexDirection.Column,
			AlignItems = AlignItems.Stretch,
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			BackgroundColor = Theme.Current.Panel,
			BorderColor = Theme.Current.Border,
			BorderWidth = Theme.Current.BorderWidth,
			// Without its own header the card is part of the tool line above it, rounded like the chat's other code.
			BorderRadius = header ? Theme.Current.Radius : Theme.Current.Radius * 2,
			Cursor = Cursor.Pointer,
			Focusable = true,
			OnClick = open,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, open ),
				new( Key.Space, KeyModifiers.None, open ),
			],
			HoverStyle = new Style
			{
				BorderColor = Theme.Current.Muted,
			},
			FocusStyle = new Style
			{
				BorderColor = Theme.Current.Accent,
			},
			Children = body,
		};
		var children = new List<Blob> { card };
		if ( file.Lines.Count > CardLines )
		{
			var fold = Theme.Current.BuildButton( expanded ? "Show less" : $"{file.Lines.Count - CardLines:N0} more lines · Show all", toggle );
			fold.Key = "fold";
			fold.AlignSelf = AlignSelf.FlexStart;
			fold.Color = Theme.Current.Muted;
			fold.FontSize = CodeSize;
			fold.Padding = new Edges( 0, Theme.Current.Spacing / 2 );
			children.Add( fold );
		}

		return new Container
		{
			Key = key,
			MinWidth = 0,
			Gap = Theme.Current.Spacing / 2,
			Children = children,
		};
	}

	/// <summary>Builds a file's header row for the diff panel; clicking it collapses or expands the file.</summary>
	internal static Button BuildFileHeader( DiffFile file, bool expanded, Action toggle )
	{
		var corner = Theme.Current.Radius;
		var open = expanded && (file.Lines.Count > 0 || file.Note is not null);
		return new Button
		{
			AccessibilityName = $"{(expanded ? "Collapse" : "Expand")} {file.Path}",
			FlexDirection = FlexDirection.Column,
			AlignItems = AlignItems.Stretch,
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			BorderColor = Theme.Current.Border,
			BorderWidth = Theme.Current.BorderWidth,
			BorderTopLeftRadius = corner,
			BorderTopRightRadius = corner,
			BorderBottomLeftRadius = open ? 0 : corner,
			BorderBottomRightRadius = open ? 0 : corner,
			Cursor = Cursor.Pointer,
			Focusable = true,
			OnClick = toggle,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, toggle ),
				new( Key.Space, KeyModifiers.None, toggle ),
			],
			Children = [BuildHeader( file, expanded, null )],
		};
	}

	/// <summary>Builds a run of a file's rows for the diff panel, optionally with one annotation control per source row.</summary>
	internal static Container BuildLineBlock(
		DiffFile file,
		int start,
		int count,
		int digits,
		bool isLast,
		Action<DiffLine>? annotate = null,
		Func<DiffLine, string?>? marker = null )
	{
		var corner = isLast ? Theme.Current.Radius : 0;
		var grammar = CodeHighlighter.FindForPath( file.Path );
		Blob body = file.Note is { } note
			? new Text { Content = note, Padding = Theme.Current.Spacing, FontSize = CodeSize, Color = Theme.Current.Muted, }
			: new Container
			{
				MinWidth = 0,
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = CodeSize,
				Children = [.. Enumerable.Range( start, count ).Select( index => BuildLine( file.Lines[index], digits, selectable: true, grammar, annotate, marker ) )],
			};
		return new Container
		{
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			Padding = new Edges( start == 0 ? Theme.Current.Spacing / 2 : 0, 0, isLast ? Theme.Current.Spacing / 2 : 0, 0 ),
			BackgroundColor = Theme.Current.Panel,
			BorderColor = Theme.Current.Border,
			BorderWidth = new Edges( 0, Theme.Current.BorderWidth, isLast ? Theme.Current.BorderWidth : 0, Theme.Current.BorderWidth ),
			BorderBottomLeftRadius = corner,
			BorderBottomRightRadius = corner,
			Children = [body],
		};
	}

	/// <summary>Gets the digit count of a file's widest line number, so every run of its rows shares one number column.</summary>
	internal static int ComputeDigits( DiffFile file ) =>
		file.Lines.Where( line => line.Kind != DiffLineKind.Gap ).Select( line => line.Number ).DefaultIfEmpty( 1 ).Max().ToString().Length;

	/// <summary>Builds the file name, folder, and change counts; a disclosure chevron in the panel, an open marker on cards.</summary>
	private static Row BuildHeader( DiffFile file, bool? expanded, Action? open )
	{
		var children = new List<Blob>();
		if ( expanded is { } isExpanded )
			children.Add( Theme.Current.BuildChevron( isExpanded ) );

		var name = Path.GetFileName( file.Path );
		var folder = Path.GetDirectoryName( file.Path ) ?? "";
		children.Add( new Text
		{
			Content = name.Length > 0 ? name : file.Path,
			Color = Theme.Current.Text,
			FlexShrink = 0,
			TextWrap = TextWrap.NoWrap,
		} );
		children.Add( new Text
		{
			Content = folder,
			Color = Theme.Current.Muted,
			FontSize = CodeSize,
			FlexShrink = 1,
			MinWidth = 0,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		} );
		children.Add( new Spacer() );
		if ( file.Added > 0 )
			children.Add( new Text { Content = $"+{file.Added:N0}", Color = Theme.Current.Success, FontSize = CodeSize, FlexShrink = 0, } );
		if ( file.Removed > 0 )
			children.Add( new Text { Content = $"−{file.Removed:N0}", Color = Theme.Current.Error, FontSize = CodeSize, FlexShrink = 0, } );
		if ( open is not null )
			children.Add( new Text { Content = "↗", Color = Theme.Current.Muted, FlexShrink = 0, } );

		return new Row
		{
			MinHeight = HeaderHeight,
			FlexShrink = 0,
			Padding = new Edges( 0, Theme.Current.Spacing ),
			Gap = Theme.Current.Spacing,
			AlignItems = AlignItems.Center,
			BackgroundColor = Theme.Current.Raised,
			BorderColor = Theme.Current.Border,
			BorderWidth = new Edges( 0, 0, Theme.Current.BorderWidth, 0 ),
			Children = children,
		};
	}

	/// <summary>Builds up to <paramref name="limit"/> rows with a shared line number column, colored by <paramref name="grammar"/>.</summary>
	private static Container BuildLines( IReadOnlyList<DiffLine> lines, int limit, bool selectable, CodeHighlighter.Grammar? grammar )
	{
		var shown = lines.Take( limit ).ToArray();
		var digits = shown.Where( line => line.Kind != DiffLineKind.Gap ).Select( line => line.Number ).DefaultIfEmpty( 1 ).Max().ToString().Length;
		return new Container
		{
			Key = "lines",
			MinWidth = 0,
			Padding = new Edges( Theme.Current.Spacing / 2, 0 ),
			FontFamily = ViewStyle.CodeFontFamily,
			FontSize = CodeSize,
			Children = [.. shown.Select( line => BuildLine( line, digits, selectable, grammar ) )],
		};
	}

	/// <summary>Builds one row: change bar, line number, optional review marker, diff marker, and text with syntax colors over a tint by its kind.</summary>
	private static Row BuildLine(
		DiffLine line,
		int digits,
		bool selectable,
		CodeHighlighter.Grammar? grammar,
		Action<DiffLine>? annotate = null,
		Func<DiffLine, string?>? marker = null )
	{
		if ( line.Kind == DiffLineKind.Gap )
			return new Row
			{
				Padding = new Edges( 3, Theme.Current.Spacing ),
				// Rules above and below set the skipped lines apart from the code around them.
				BorderWidth = new Edges( Theme.Current.BorderWidth, 0 ),
				BorderColor = Theme.Current.Divider,
				Children =
				[
					new Text
					{
						Content = line.Number > 0 ? $"⋯ {line.Number:N0} unchanged {(line.Number == 1 ? "line" : "lines")}" : "⋯",
						Color = Theme.Current.Muted,
					},
				],
			};

		var color = line.Kind switch
		{
			DiffLineKind.Added => Theme.Current.Success,
			DiffLineKind.Removed => Theme.Current.Error,
			_ => Color.Transparent,
		};
		var children = new List<Blob>
		{
			new Container
			{
				Width = BarWidth,
				FlexShrink = 0,
				BackgroundColor = color,
			},
			new Text
			{
				Content = line.Number.ToString(),
				Width = Length.Em( digits * DigitEms ) + Theme.Current.Spacing * 2,
				Padding = new Edges( 0, Theme.Current.Spacing ),
				FlexShrink = 0,
				TextAlign = TextAlign.Right,
				Color = Theme.Current.Muted,
			},
		};
		if ( annotate is not null )
		{
			var label = marker?.Invoke( line );
			children.Add( new Button
			{
				AccessibilityName = label is null ? $"Annotate line {line.Number}" : $"Edit line {line.Number} annotation or finding {label}",
				Focusable = false,
				Cursor = Cursor.Pointer,
				Width = 34,
				FlexShrink = 0,
				Padding = 0,
				BackgroundColor = label is null ? Color.Transparent : Theme.Current.Accent.WithAlpha( 0.18 ),
				OnClick = () => annotate( line ),
				Children =
				[
					new Text
					{
						Content = label ?? "+",
						FontSize = CodeSize - 1,
						TextAlign = TextAlign.Center,
						Color = label is null ? Theme.Current.Muted : Theme.Current.Accent,
					},
				],
			} );
		}
		children.Add( new Text
		{
			Content = line.Kind switch
			{
				DiffLineKind.Added => "+",
				DiffLineKind.Removed => "−",
				_ => " ",
			},
			Width = Length.Em( 1 ),
			FlexShrink = 0,
			Color = line.Kind == DiffLineKind.Context ? Theme.Current.Muted : color,
		} );
		// Each row is colored alone: a diff shows fragments, so a block comment's state across rows is not known.
		var code = BuildCode( line.Text );
		var isInComment = false;
		children.Add( new Text( grammar is null || line.Text.Length == 0 ? [new TextSpan( code, null! )] : CodeHighlighter.BuildSpans( code, grammar, ref isInComment ) )
		{
			Selectable = selectable,
			FlexGrow = 1,
			FlexShrink = 1,
			MinWidth = 0,
			Padding = new Edges( 0, Theme.Current.Spacing, 0, 0 ),
			Color = Theme.Current.Text,
		} );
		return new Row
		{
			MinWidth = 0,
			BackgroundColor = line.Kind == DiffLineKind.Context ? Color.Transparent : color.WithAlpha( TintOpacity ),
			Children = children,
		};
	}

	/// <summary>Expands tabs and keeps an empty line one row tall.</summary>
	private static string BuildCode( string text ) => text.Length == 0 ? " " : ViewStyle.CutCodeLine( text.Replace( "\t", Tab ) );
}
