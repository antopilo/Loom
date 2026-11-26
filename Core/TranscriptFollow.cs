using Goo;

namespace Lokn.Core;

/// <summary>Tracks whether the reader is at the transcript's end, releasing the pin once they scroll up and restoring it when they return.</summary>
internal sealed class TranscriptFollow
{
	/// <summary>Tracks whether content growth keeps the newest message in view; views that read it rebuild when it changes.</summary>
	private readonly State<bool> _isFollowing = new( true );

	/// <summary>Stores the last observed vertical scroll offset to tell the reader scrolling up from scrolling down.</summary>
	private double _offset;

	/// <summary>Stores the nearest end seen since the offset started moving down: the farthest a scroll begun then can aim.</summary>
	private double _aimedEnd = double.MaxValue;

	/// <summary>Stores the last observed scroll range, to tell content that shrank from the reader scrolling up.</summary>
	private double _range;

	/// <summary>Subscribes to the transcript scroller's geometry for the lifetime of its panel.</summary>
	public TranscriptFollow() => Handle.MetricsChanged += UpdateMetrics;

	/// <summary>Targets the transcript scroller.</summary>
	public ElementHandle Handle { get; } = new();

	/// <summary>Reports whether the transcript is pinned to its newest message, subscribing the building view.</summary>
	public bool IsFollowing => _isFollowing.Value;

	/// <summary>Resumes following and jumps to the newest message.</summary>
	/// <remarks>
	/// An animated scroll aims at the end it saw when it started; a prompt's echo arriving a frame later grows the
	/// transcript, cancels that animation where it began and releases the pin, so the view stayed scrolled up.
	/// </remarks>
	public void ScrollToEnd()
	{
		_isFollowing.Value = true;
		Handle.JumpTo( null, Handle.ScrollRange.Y );
	}

	/// <summary>Tracks whether the reader left or returned to the end; the transcript list keeps the end in view while following.</summary>
	/// <remarks>
	/// Growth and following never move the offset backwards, so an offset that went back while the content did not shrink, and is not at the
	/// end, is the reader's. Content that shrank pulls the offset back on its own: a fold closing, or rows of a chat opened again measured
	/// shorter than their estimate, so that never releases the pin. A scroll down aims at the end it saw when it was asked for; rows
	/// streaming in meanwhile leave it short of the new end, so reaching an end seen during the scroll counts as returning. The range
	/// changes on almost every report while rows stream in, so no report is skipped.
	/// </remarks>
	private void UpdateMetrics( ElementMetrics metrics )
	{
		if ( !metrics.IsMounted )
		{
			// A remount starts from the origin, so its first offset is not a reader scroll and no end was seen yet.
			_offset = 0;
			_range = 0;
			_aimedEnd = double.MaxValue;
			return;
		}

		var offset = metrics.ScrollOffset.Y;
		var range = metrics.ScrollRange.Y;
		_aimedEnd = offset > _offset ? Math.Min( _aimedEnd, range ) : range;
		if ( metrics.IsAtEndY || (offset > _offset && offset >= _aimedEnd - 1) )
			_isFollowing.Value = true;
		else if ( offset < _offset && range >= _range )
			_isFollowing.Value = false;
		_offset = offset;
		_range = range;
	}
}
