using System.Diagnostics;
using Goo;

namespace Lokn.Core;

/// <summary>Identifies one row of a measured-height virtual list.</summary>
public interface IMeasuredRow
{
	/// <summary>Gets the stable key, unique among the list's rows.</summary>
	string Key
	{
		get;
	}
}

/// <summary>Supplies one build's rows to a Goo <see cref="Virtual"/> element.</summary>
/// <param name="Rows">Every row in order.</param>
/// <param name="Build">Builds one row's content; pass the same delegate on every build so unchanged rows are reused.</param>
/// <param name="Estimate">Gives the height assumed for a row until it is measured, in logical pixels.</param>
/// <param name="FollowEnd">Whether new rows and size changes keep the end in view.</param>
/// <param name="Placed">Receives the committed rows and each row's top edge, with the content height last, after each placement; the array is reused afterwards, so read it during the call.</param>
/// <typeparam name="TRow">The immutable row value; a row equal to the one already mounted under its key is not built again.</typeparam>
public sealed record MeasuredRowsSource<TRow>( IReadOnlyList<TRow> Rows, Func<TRow, Blob> Build, Func<TRow, double> Estimate, bool FollowEnd, Action<IReadOnlyList<TRow>, double[]>? Placed = null ) : VirtualSource
	where TRow : class, IMeasuredRow, IEquatable<TRow>
{
	/// <summary>Continues the retained state with these rows pending, or starts one for a new element.</summary>
	public VirtualState State( VirtualState? current )
	{
		var state = current as MeasuredRowsState<TRow> ?? new MeasuredRowsState<TRow>();
		state.Pending = this;
		return state;
	}
}

/// <summary>Places measured-height rows near the viewport, so a long list only builds and lays out what can be seen.</summary>
/// <remarks>
/// Heights measured by layout replace the estimate per row. While the reader is not at the end, the first visible row
/// keeps its place in the viewport when rows above it change height. While following the end, growth of up to one
/// viewport glides to the new end, each frame closing a share of the distance left that grows with the frame's time
/// (<see cref="GlideMs"/>), and lands on it exactly; an end that keeps moving is chased without restarting. The
/// first placement, a resize, a longer distance or motion turned off still jump. Every change stays pending until <see cref="Commit"/>.
/// </remarks>
/// <typeparam name="TRow">The immutable row value.</typeparam>
internal sealed class MeasuredRowsState<TRow> : VirtualState
	where TRow : class, IMeasuredRow, IEquatable<TRow>
{
	/// <summary>Defines how far a measured height may differ from the stored one before rows are placed again.</summary>
	private const double Tolerance = 0.5;

	/// <summary>Defines the least distance mounted beyond each edge of the viewport, in logical pixels.</summary>
	private const double MinimumOverscan = 200;

	/// <summary>Defines the glide's time constant in milliseconds: each such span closes about two thirds of the distance left, the chat's slow motion.</summary>
	private const double GlideMs = 75;

	/// <summary>Defines the time the first step of a glide counts, in milliseconds: one frame at 60 Hz.</summary>
	private const double FirstStepMs = 1000.0 / 60;

	/// <summary>Gets or sets the rows supplied by the latest build, until a realization commits them.</summary>
	internal MeasuredRowsSource<TRow>? Pending
	{
		get; set;
	}

	/// <summary>Stores the committed rows.</summary>
	private MeasuredRowsSource<TRow>? _source;

	/// <summary>Stores measured heights by row key; rows outside this map use the estimate.</summary>
	private readonly Dictionary<string, double> _heights = [];

	/// <summary>Stores the heights measured since the last commit, applied to <see cref="_heights"/> by it.</summary>
	private readonly Dictionary<string, double> _measured = [];

	/// <summary>Stores each row's top edge, with the content height as the final entry.</summary>
	private double[] _tops = [0];

	/// <summary>Stores the row tops of the next commit.</summary>
	private double[] _pendingTops = [0];

	/// <summary>Stores the mounted row values by key, to skip rebuilding rows that did not change.</summary>
	private Dictionary<string, TRow> _mounted = [];

	/// <summary>Stores the mounted rows of the next commit.</summary>
	private Dictionary<string, TRow> _pendingMounted = [];

	/// <summary>Stores the committed range of mounted row indexes, end exclusive.</summary>
	private (int Start, int End) _range;

	/// <summary>Stores the mounted range of the next commit.</summary>
	private (int Start, int End) _pendingRange;

	/// <summary>Stores the committed viewport size.</summary>
	private LayoutSize _size;

	/// <summary>Stores the viewport size of the next commit.</summary>
	private LayoutSize _pendingSize;

	/// <summary>Stores the retained context generation the mounted content was built for.</summary>
	private long _context = -1;

	/// <summary>Stores the context generation of the next commit.</summary>
	private long _pendingContext;

	/// <summary>Reports whether a realization awaits <see cref="Commit"/> or <see cref="Cancel"/>.</summary>
	private bool _isRealized;

	/// <summary>Stores the committed glide step toward the end, or null when the view rests.</summary>
	private Glide? _glide;

	/// <summary>Stores the glide of the next commit.</summary>
	private Glide? _pendingGlide;

	/// <summary>Stores the committed scroll offset, to tell the reader scrolling during a glide from the glide itself.</summary>
	private double _scroll;

	/// <summary>Stores the scroll offset of the next commit.</summary>
	private double _pendingScroll;

	/// <summary>Describes the last step of a glide of the scroll offset toward the end.</summary>
	/// <param name="Stepped">When it stepped, as a <see cref="Stopwatch"/> timestamp.</param>
	private readonly record struct Glide( long Stepped );

	/// <summary>Places the rows near the scroll position, following the end or holding the first visible row in place.</summary>
	public override void Realize( VirtualViewport viewport, VirtualOutput output )
	{
		var input = Pending ?? _source ?? throw new InvalidOperationException( "The list has no rows to place." );
		var rows = input.Rows;
		var size = viewport.Size;
		var reuse = _source is { } committed && committed.Build == input.Build && _context == viewport.ContextVersion;

		_measured.Clear();
		for ( var i = 0; i < viewport.ChildCount; i++ )
		{
			var child = viewport.Child( i );
			if ( child.Measured )
				_measured[child.Key] = child.Size.Height;
		}
		if ( _pendingTops.Length != rows.Count + 1 )
			_pendingTops = new double[rows.Count + 1];
		for ( var i = 0; i < rows.Count; i++ )
			_pendingTops[i + 1] = _pendingTops[i] + ComputeHeight( rows[i], input.Estimate );

		var total = _pendingTops[rows.Count];
		var end = Math.Max( 0, total - size.Height );
		var scroll = Math.Clamp( viewport.ScrollOffset.Y, 0, end );
		// The last placement's end, at the taller of its viewport and this one: a viewport that grew clamps the offset
		// back to its own end, as the first placement's zero-size viewport always does, and that is still the end.
		var wasPinned = _source is null || viewport.ScrollOffset.Y >= _tops[^1] - Math.Max( _size.Height, size.Height ) - Tolerance;
		// A glide continues while the offset has not gone back from where it left it: the reader scrolling up ends it, and a
		// smooth scroll still settling toward the end does not. Ending it there stopped following until the next row arrived.
		var isGliding = _glide is not null && viewport.ScrollOffset.Y >= _scroll - 1;
		_pendingGlide = null;
		if ( input.FollowEnd && (Pending is not null || wasPinned || isGliding) )
		{
			scroll = ComputeFollow( viewport.ScrollOffset.Y, end, size, isGliding );
		}
		else if ( viewport.ScrollOffset.Y > 0 && viewport.VisibleAnchor is { } anchor && IndexOf( rows, anchor.Key ) is var index and >= 0 )
		{
			// Move by however far the anchor row's top moved. Its reported offset is snapped to whole pixels, so
			// deriving the scroll from that offset crept the view up by up to a pixel on every rebuild.
			var previous = _source is { } shown ? IndexOf( shown.Rows, anchor.Key ) : -1;
			var moved = previous >= 0 && previous < _tops.Length ? _pendingTops[index] - _tops[previous] : 0;
			scroll = Math.Clamp( viewport.ScrollOffset.Y + moved, 0, end );
			if ( reuse )
				output.AnchorKey = anchor.Key;
		}

		_pendingRange = ComputeRange( _pendingTops, scroll, size.Height );
		_pendingMounted.Clear();
		void Add( int i )
		{
			var row = rows[i];
			var unchanged = reuse && _mounted.TryGetValue( row.Key, out var mounted ) && mounted.Equals( row );
			_pendingMounted[row.Key] = row;
			output.Add( new VirtualItem
			{
				Key = row.Key,
				Index = i,
				Y = _pendingTops[i],
				Width = size.Width,
				Content = unchanged ? null : input.Build( row ),
			} );
		}
		for ( var i = _pendingRange.Start; i < _pendingRange.End; i++ )
			Add( i );
		if ( viewport.FocusedChild >= 0 && IndexOf( rows, viewport.Child( viewport.FocusedChild ).Key ) is var focused and >= 0
			&& (focused < _pendingRange.Start || focused >= _pendingRange.End) )
			Add( focused );
		output.ContentSize = new LayoutSize( size.Width, total );
		output.ItemCount = rows.Count;
		output.ScrollOffset = new Point { X = viewport.ScrollOffset.X, Y = scroll };
		_pendingScroll = scroll;
		_pendingSize = size;
		_pendingContext = viewport.ContextVersion;
		_isRealized = true;
	}

	/// <summary>Gets the offset that follows the end: on the way there while a glide runs, else the end itself.</summary>
	/// <param name="offset">The current scroll offset.</param>
	/// <param name="end">The offset that shows the end.</param>
	/// <param name="size">The viewport size of this placement.</param>
	/// <param name="isGliding">Whether the committed glide is still undisturbed.</param>
	/// <remarks>
	/// A glide of fixed length restarted from no progress whenever a row arrived, so a stream of rows held the view a row
	/// or more short of the end. The result never goes back from <paramref name="offset"/>, which is how the view tells
	/// the reader scrolling up, even when a smooth scroll still settling is ahead of the glide.
	/// </remarks>
	private double ComputeFollow( double offset, double end, LayoutSize size, bool isGliding )
	{
		var distance = end - offset;
		var isResized = Math.Abs( size.Width - _size.Width ) > Tolerance || Math.Abs( size.Height - _size.Height ) > Tolerance;
		if ( _source is null || isResized || Motion.TimeScale <= 0 || distance <= Tolerance || distance > size.Height )
			return end;

		var now = Stopwatch.GetTimestamp();
		var elapsed = isGliding && _glide is { } last ? Stopwatch.GetElapsedTime( last.Stepped, now ).TotalMilliseconds : FirstStepMs;
		// Ease out: quick to start, settling softly on the end.
		var next = offset + distance * (1 - Math.Exp( -elapsed * Motion.TimeScale / GlideMs ));
		if ( end - next <= Tolerance )
			return end;

		_pendingGlide = new Glide( now );
		return next;
	}

	/// <summary>Reports a resize, a context change, a scroll near the edge of the mounted rows, a row whose height changed, or a glide still under way.</summary>
	public override bool NeedsRealize( VirtualViewport viewport )
	{
		if ( _source is not { } source )
			return false;
		if ( _glide is not null || viewport.ContextVersion != _context || Math.Abs( viewport.Size.Width - _size.Width ) > Tolerance || Math.Abs( viewport.Size.Height - _size.Height ) > Tolerance )
			return true;

		var scroll = viewport.ScrollOffset.Y;
		var margin = ComputeOverscan( viewport.Size.Height ) / 2;
		if ( (_range.Start > 0 && scroll - margin < _tops[_range.Start]) || (_range.End < source.Rows.Count && scroll + viewport.Size.Height + margin > _tops[_range.End]) )
			return true;

		for ( var i = 0; i < viewport.ChildCount; i++ )
		{
			var child = viewport.Child( i );
			if ( child.Measured && (!_heights.TryGetValue( child.Key, out var height ) || Math.Abs( height - child.Size.Height ) > Tolerance) )
				return true;
		}
		return false;
	}

	/// <summary>Returns the scroll offset that puts a row at the top of the viewport.</summary>
	public override Point? OffsetOf( VirtualViewport viewport, string key ) =>
		_source is { } source && IndexOf( source.Rows, key ) is var index and >= 0 ? new Point { X = viewport.ScrollOffset.X, Y = _tops[index] } : null;

	/// <summary>Returns a row's measured or estimated bounds in content coordinates.</summary>
	public override ElementRect? BoundsOf( VirtualViewport viewport, string key )
	{
		var (rows, tops) = _isRealized ? ((Pending ?? _source)?.Rows, _pendingTops) : (_source?.Rows, _tops);
		if ( rows is null || IndexOf( rows, key ) is not (var index and >= 0) )
			return null;

		return new ElementRect { Y = tops[index], Width = viewport.Size.Width, Height = tops[index + 1] - tops[index] };
	}

	/// <summary>Makes the last realization current, and reports the placement when the rows or their tops changed.</summary>
	public override void Commit()
	{
		var isMoved = Pending is not null || !_pendingTops.AsSpan().SequenceEqual( _tops );
		_source = Pending ?? _source;
		Pending = null;
		foreach ( var (key, height) in _measured )
			_heights[key] = height;
		_measured.Clear();
		(_tops, _pendingTops) = (_pendingTops, _tops);
		(_mounted, _pendingMounted) = (_pendingMounted, _mounted);
		_range = _pendingRange;
		_size = _pendingSize;
		_context = _pendingContext;
		_glide = _pendingGlide;
		_scroll = _pendingScroll;
		_isRealized = false;
		if ( isMoved )
			_source?.Placed?.Invoke( _source.Rows, _tops );
	}

	/// <summary>Discards the last realization and the rows it was given.</summary>
	public override void Cancel()
	{
		Pending = null;
		_measured.Clear();
		_isRealized = false;
	}

	/// <summary>Releases the retained rows and measurements.</summary>
	public override void Dispose()
	{
		Cancel();
		_source = null;
		_glide = null;
		_heights.Clear();
		_mounted.Clear();
	}

	/// <summary>Gets a row's height measured in this realization, else its stored measurement, else the estimate.</summary>
	private double ComputeHeight( TRow row, Func<TRow, double> estimate ) =>
		_measured.TryGetValue( row.Key, out var height ) || _heights.TryGetValue( row.Key, out height ) ? height : estimate( row );

	/// <summary>Gets the mounted margin beyond each viewport edge: a quarter viewport. Goo realizes again in the same frame after a scroll, so a larger
	/// margin never shows more rows; it only mounts more off-screen rows, which Goo's scene compiler still walks every frame.</summary>
	private static double ComputeOverscan( double viewportHeight ) => Math.Max( MinimumOverscan, viewportHeight / 4 );

	/// <summary>Finds the rows that intersect the viewport and its overscan, end exclusive.</summary>
	private static (int Start, int End) ComputeRange( double[] tops, double scroll, double viewportHeight )
	{
		var overscan = ComputeOverscan( viewportHeight );
		var count = tops.Length - 1;
		var start = Math.Max( 0, FindRow( tops, scroll - overscan ) );
		var end = Math.Min( count, FindRow( tops, scroll + viewportHeight + overscan ) + 1 );
		return (start, Math.Max( start, end ));
	}

	/// <summary>Returns the index of the row containing a content coordinate, by binary search over row tops.</summary>
	private static int FindRow( double[] tops, double y )
	{
		int low = 0, high = tops.Length - 2;
		while ( low < high )
		{
			var middle = (low + high + 1) / 2;
			if ( tops[middle] <= y )
				low = middle;
			else
				high = middle - 1;
		}
		return low;
	}

	/// <summary>Returns the index of the row with a key, or -1.</summary>
	private static int IndexOf( IReadOnlyList<TRow> rows, string key )
	{
		for ( var i = rows.Count - 1; i >= 0; i-- )
		{
			if ( rows[i].Key == key )
				return i;
		}
		return -1;
	}
}

/// <summary>Identifies one row of a page built by a delegate; rows compare by reference, so each page build builds its visible rows again.</summary>
/// <param name="key">The stable row key, unique among the page's rows.</param>
/// <param name="build">Builds the row's content.</param>
public sealed class BuiltRow( string key, Func<Blob> build ) : IMeasuredRow, IEquatable<BuiltRow>
{
	/// <summary>Gets the stable row key.</summary>
	public string Key => key;

	/// <summary>Builds the row's content.</summary>
	public Blob Build() => build();

	/// <summary>Compares rows by reference.</summary>
	public bool Equals( BuiltRow? other ) => ReferenceEquals( this, other );

	/// <summary>Compares rows by reference.</summary>
	public override bool Equals( object? obj ) => ReferenceEquals( this, obj );

	/// <summary>Hashes the row by its key.</summary>
	public override int GetHashCode() => key.GetHashCode( StringComparison.Ordinal );
}
