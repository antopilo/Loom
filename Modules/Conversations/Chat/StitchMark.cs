using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Draws a running stitch sewn along the Loom weft while the agent works.</summary>
/// <remarks>
/// The seam is the weft of <c>assets/brand/loom-mark.svg</c>, scaled into a small box and dashed into stitches. Each cycle
/// sews it one stitch at a time, each easing in and out like a hand's push; then the thread is pulled through from its
/// start. Stitches keep their place in the fabric while the thread's ends move. The cycle is a Goo animation, so it advances
/// with each rendered frame rather than a timer; with reduced motion the mark shows the finished seam and does not move.
/// </remarks>
internal sealed class StitchMark : GuardedCell
{
	/// <summary>Defines the mark's width in logical pixels.</summary>
	private const int Width = 22;

	/// <summary>Defines the mark's height in logical pixels.</summary>
	private const int Height = 14;

	/// <summary>Defines one cycle of sewing and pulling the thread through, in milliseconds.</summary>
	private const double CycleMs = 2200;

	/// <summary>Defines the share of the cycle spent sewing; the rest pulls the thread through.</summary>
	private const double SewShare = 0.75;

	/// <summary>Defines when, as a share of the cycle, the thread's start begins to follow.</summary>
	private const double PullStart = 0.55;

	/// <summary>Defines a stitch's length in logical pixels.</summary>
	private const double StitchLength = 2.6;

	/// <summary>Defines the gap between stitches, where the thread runs under the fabric; narrower gaps blur shut at 100% scale.</summary>
	private const double StitchGap = 2.2;

	/// <summary>Defines one stitch and its gap.</summary>
	private const double StitchPeriod = StitchLength + StitchGap;

	/// <summary>Defines the thread's stroke width.</summary>
	private const double ThreadWidth = 1.25;

	/// <summary>Holds the box the path is drawn in, the same size as the mark, so path units are logical pixels.</summary>
	private static readonly ElementRect Box = new() { Width = Width, Height = Height };

	/// <summary>Holds the weft <c>M8 36 C16 22 24 46 32 32 S48 18 56 28</c> mapped into the box.</summary>
	private static readonly (double X, double Y)[][] Curves =
	[
		[(1, 9.67), (4.33, 3.83), (7.67, 13.83), (11, 8)],
		// The smooth curve's first control point mirrors the previous one about their shared point.
		[(11, 8), (14.33, 2.17), (17.67, 2.17), (21, 6.33)],
	];

	/// <summary>Holds the seam's path, with stitches as dashes.</summary>
	private static readonly VectorPath Seam = VectorPath.Parse( string.Create( CultureInfo.InvariantCulture,
		$"M{Curves[0][0].X} {Curves[0][0].Y} C{Curves[0][1].X} {Curves[0][1].Y} {Curves[0][2].X} {Curves[0][2].Y} {Curves[0][3].X} {Curves[0][3].Y} C{Curves[1][1].X} {Curves[1][1].Y} {Curves[1][2].X} {Curves[1][2].Y} {Curves[1][3].X} {Curves[1][3].Y}" ), Box );

	/// <summary>Holds the seam's length in logical pixels.</summary>
	private static readonly double Length = MeasureSeam();

	/// <summary>Holds the share of the cycle, from 0 to 1, advanced by Goo on each frame; reading it rebuilds the mark.</summary>
	private Anim<double>? _cycle;

	/// <summary>Starts the cycle and restarts it each time it ends; Goo stops it when the mark unmounts.</summary>
	protected override void Mounted()
	{
		if ( IsStill() )
			return;

		_cycle = Animate( 0.0 );
		_cycle.Completed += reason =>
		{
			if ( reason == MotionCompletionReason.Finished )
				Play();
		};
		Play();
	}

	/// <summary>Builds the part of the seam sewn so far.</summary>
	protected override Blob BuildView()
	{
		// The seam is empty at both ends of the cycle, so the restart between two cycles does not show.
		var phase = _cycle is { } cycle && !IsStill() ? cycle.Value : SewShare;
		// Whole stitches that fit the seam; each one eases from rest to rest.
		var count = Math.Max( 1, (int)(Length / StitchPeriod) );
		var sewn = Math.Clamp( phase / SewShare, 0, 1 ) * count;
		var head = (Math.Floor( sewn ) + Ease( sewn - Math.Floor( sewn ) )) * StitchPeriod / Length;
		var tail = Ease( (phase - PullStart) / (1 - PullStart) );
		return new Shape
		{
			Path = Seam,
			Fit = ShapeFit.Contain,
			Width = Width,
			Height = Height,
			FlexShrink = 0,
			Accessibility = new Accessibility { Hidden = true },
			StrokeStart = Math.Min( tail, head ),
			StrokeEnd = head,
			// Dashes restart at the trimmed start, so their phase moves with it and the stitches stay put in the fabric.
			Stroke = new VectorStroke( ThreadWidth, Theme.Current.Accent )
			{
				Cap = StrokeCap.Round,
				Dashes = new DashPattern( [StitchLength, StitchGap], tail * Length ),
			},
		};
	}

	/// <summary>Reports whether the system or Lokn asks for no motion.</summary>
	private bool IsStill() => Motion.TimeScale <= 0 || Host.Preferences.ReducedMotion == true;

	/// <summary>Plays one cycle at an even speed; the easing is in the thread's ends, not in the clock.</summary>
	private void Play() => _cycle?.Play( 0, 1, Motion.Tween( CycleMs, Easing.Linear ) );

	/// <summary>Eases a share from 0 to 1, clamped, slow at both ends.</summary>
	private static double Ease( double share )
	{
		var t = Math.Clamp( share, 0, 1 );
		return t * t * (3 - 2 * t);
	}

	/// <summary>Measures the seam by summing short chords along both curves.</summary>
	private static double MeasureSeam()
	{
		const int perCurve = 48;
		var length = 0.0;
		foreach ( var curve in Curves )
		{
			var (x, y) = curve[0];
			for ( var step = 1; step <= perCurve; step++ )
			{
				var t = (double)step / perCurve;
				var (a, b, c, d) = ((1 - t) * (1 - t) * (1 - t), 3 * (1 - t) * (1 - t) * t, 3 * (1 - t) * t * t, t * t * t);
				var next = (X: a * curve[0].X + b * curve[1].X + c * curve[2].X + d * curve[3].X, Y: a * curve[0].Y + b * curve[1].Y + c * curve[2].Y + d * curve[3].Y);
				length += Math.Sqrt( (next.X - x) * (next.X - x) + (next.Y - y) * (next.Y - y) );
				(x, y) = next;
			}
		}

		return length;
	}
}
