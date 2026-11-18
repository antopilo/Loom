using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes one of the user's prompts by where it starts in the transcript.</summary>
/// <param name="Key">The prompt's transcript row key.</param>
/// <param name="Label">The prompt's first line, for its accessible name.</param>
/// <param name="Top">The row's top edge in content coordinates, in logical pixels.</param>
/// <param name="Fraction">The row's top as a fraction of the content height, which places its line along the scrollbar.</param>
internal readonly record struct TranscriptMark( string Key, string Label, double Top, double Fraction );

/// <summary>Keeps where the user's prompts sit in the transcript, from each placement of its rows.</summary>
/// <param name="post">Posts to the UI thread; placements report during layout, so the marks change after it.</param>
internal sealed class TranscriptMarks( Func<Action, bool> post )
{
	/// <summary>Defines how far, as a fraction of the content height, a mark may move before its line is placed again.</summary>
	private const double Tolerance = 0.001;

	/// <summary>Stores the current marks in transcript order; the marks view rebuilds when they change.</summary>
	private readonly State<TranscriptMark[]> _items = new( [] );

	/// <summary>Gets the marks in transcript order, subscribing the building view.</summary>
	public TranscriptMark[] Items => _items.Value;

	/// <summary>Reads the prompts' places from a placement of the transcript rows.</summary>
	/// <param name="rows">The placed rows.</param>
	/// <param name="tops">Each row's top edge, with the content height last.</param>
	public void Update( IReadOnlyList<TranscriptRow> rows, double[] tops )
	{
		var total = tops[rows.Count];
		var marks = new List<TranscriptMark>();
		for ( var i = 0; total > 0 && i < rows.Count; i++ )
		{
			if ( rows[i] is { IsActivity: false, Changes: null, Parts: [{ Start: 0, Message: { Role: "user" } message }] } )
				marks.Add( new( rows[i].Key, message.Text.Split( '\n' )[0], tops[i], tops[i] / total ) );
		}

		var current = _items.Value;
		if ( marks.Count == current.Length && marks.Index().All( pair => pair.Item.Key == current[pair.Index].Key
			&& Math.Abs( pair.Item.Fraction - current[pair.Index].Fraction ) < Tolerance && Math.Abs( pair.Item.Top - current[pair.Index].Top ) < 1 ) )
			return;

		TranscriptMark[] next = [.. marks];
		post( () => _items.Value = next );
	}
}

/// <summary>Supplies the prompt marks and the transcript scroller they lie along.</summary>
/// <param name="Marks">The prompts' places.</param>
/// <param name="Scroller">The transcript scroller, which a click scrolls and whose position picks the current prompt.</param>
internal sealed record TranscriptMarksInput( TranscriptMarks Marks, ElementHandle Scroller );

/// <summary>Draws a line along the transcript scrollbar for each of the user's prompts, in the accent color for the one being read; clicking a line jumps to its prompt.</summary>
/// <remarks>A line sits where the scrollbar thumb's top is when its prompt is at the top of the view. Lines show only while the transcript scrolls.</remarks>
internal sealed class TranscriptMarksView : GuardedCell<TranscriptMarksInput>
{
	/// <summary>Defines a line's length at rest in logical pixels.</summary>
	private const int LineWidth = 6;

	/// <summary>Defines a line's thickness in logical pixels.</summary>
	private const int LineHeight = 2;

	/// <summary>Defines the height of a line's click target in logical pixels, centered on the line.</summary>
	private const int HitHeight = 8;

	/// <summary>Stores the prompt being read, the last one whose top is at or above the top of the view; empty for none.</summary>
	private readonly State<string> _current = new( "" );

	/// <summary>Stores whether the transcript is taller than its view, so the scrollbar shows.</summary>
	private readonly State<bool> _isScrollable = new( false );

	/// <summary>Stores the key of the line under the pointer; empty for none.</summary>
	private readonly State<string> _hovered = new( "" );

	/// <summary>Follows the scroller so the current prompt changes as the reader scrolls.</summary>
	protected override void Mounted()
	{
		Input.Scroller.MetricsChanged += UpdateMetrics;
		_isScrollable.Value = Input.Scroller.IsMounted && Input.Scroller.ScrollRange.Y > 0;
	}

	/// <summary>Stops following the scroller.</summary>
	protected override void Unmounting() => Input.Scroller.MetricsChanged -= UpdateMetrics;

	/// <summary>Builds the lines over the scrollbar track, or nothing while the transcript fits its view.</summary>
	protected override Blob BuildView( TranscriptMarksInput input )
	{
		var marks = input.Marks.Items;
		if ( !_isScrollable.Value || marks.Length == 0 )
			return new Container { Key = "transcript-marks", Position = PositionType.Absolute, Width = 0, Height = 0 };

		// The stored prompt only triggers rebuilds; marks can change without a scroll, so the current one is found again here.
		_ = _current.Value;
		var current = FindCurrent( marks, input.Scroller.ScrollOffset.Y );
		var hovered = _hovered.Value;
		return new Container
		{
			Key = "transcript-marks",
			Position = PositionType.Absolute,
			Top = 0,
			Bottom = 0,
			Right = 0,
			Width = ViewStyle.TrackWidth,
			Children = [.. marks.Select( mark => BuildLine( mark, mark.Key == current, mark.Key == hovered ) )],
		};
	}

	/// <summary>Builds one prompt's line and its click target.</summary>
	private Button BuildLine( TranscriptMark mark, bool isCurrent, bool isHovered )
	{
		var theme = Theme.Current;
		void Jump() => Input.Scroller.ScrollToItem( mark.Key, ScrollAlignment.Start, true );
		return new Button
		{
			Key = mark.Key,
			AccessibilityName = "Jump to your message: " + mark.Label,
			Position = PositionType.Absolute,
			Top = Length.Percent( mark.Fraction * 100 ),
			Margin = new Edges( -HitHeight / 2.0, 0, 0, 0 ),
			Left = 0,
			Right = 0,
			Height = HitHeight,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Cursor = Cursor.Pointer,
			OnClick = Jump,
			OnPointerEnter = _ => _hovered.Value = mark.Key,
			OnPointerLeave = _ => _hovered.Value = _hovered.Value == mark.Key ? "" : _hovered.Value,
			Children =
			[
				new Container
				{
					Width = isCurrent || isHovered ? ViewStyle.TrackWidth : LineWidth,
					Height = LineHeight,
					BorderRadius = LineHeight / 2.0,
					BackgroundColor = isCurrent ? theme.Accent : isHovered ? theme.Text : theme.Muted,
				},
			],
		};
	}

	/// <summary>Tracks whether the transcript scrolls and which prompt is being read, rebuilding only when either changes.</summary>
	private void UpdateMetrics( ElementMetrics metrics )
	{
		var isScrollable = metrics.IsMounted && metrics.ScrollRange.Y > 0;
		if ( _isScrollable.Value != isScrollable )
			_isScrollable.Value = isScrollable;
		var current = metrics.IsMounted ? FindCurrent( Input.Marks.Items, metrics.ScrollOffset.Y ) : "";
		if ( _current.Value != current )
			_current.Value = current;
	}

	/// <summary>Finds the last prompt whose top is at or above a scroll offset, the first prompt when all lie below it, or empty without prompts.</summary>
	private static string FindCurrent( TranscriptMark[] marks, double offset )
	{
		var current = marks.Length > 0 ? marks[0].Key : "";
		foreach ( var mark in marks )
		{
			if ( mark.Top > offset + 1 )
				break;
			current = mark.Key;
		}
		return current;
	}
}
