using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;

namespace Lokn.Modules.Conversations;

/// <summary>Draws the Loom mark in ASCII: a thick ring with a wave through it, riding a rippling mesh under a drifting gradient.</summary>
/// <remarks>
/// The geometry is <c>assets/brand/loom-mark.svg</c> on its 64-unit grid. The ring and the wave are filled with characters on
/// a fine grid, and a gap is cut out of the ring around the wave where it passes over, as in the icon. Each character sits
/// on a vertex of a mesh displaced by travelling waves: crests pick denser characters and brighter colors, and a few
/// characters scramble each tick. A brighter crest travels along the wave. Only this cell rebuilds on each frame. The mark holds still with reduced
/// motion requested, and while it is not seen or its window does not have the keyboard (<see cref="LiveMotion.IsAttended"/>), so a hidden or background Home draws no frames.
/// </remarks>
internal sealed class HomeLogo : GuardedCell
{
	/// <summary>Defines the mark's grid size in units, as in its SVG view box.</summary>
	internal const double Units = 64;

	/// <summary>Defines logical pixels per mark unit.</summary>
	internal const double Scale = 4;

	/// <summary>Defines the mark's width and height in logical pixels.</summary>
	internal const double Size = Units * Scale;

	/// <summary>Defines the mark's center, in units.</summary>
	private const double Center = 32;

	/// <summary>Defines the ring's radius, in units.</summary>
	internal const double RingRadius = 20;

	/// <summary>Defines half the width of the ring's and the wave's strokes, in units.</summary>
	internal const double HalfStroke = 3;

	/// <summary>Defines how far from the wave's center line the gap cut out of the ring reaches, in units.</summary>
	internal const double GapRadius = 7;

	/// <summary>Defines the spacing of the fill grid, in logical pixels.</summary>
	internal const double FillStep = 5;

	/// <summary>Defines the size of the fill characters.</summary>
	private const int FillSize = 9;

	/// <summary>Defines the bold weight that makes the fill characters read as solid strokes.</summary>
	private const int FillWeight = 700;

	/// <summary>Defines a monospace character's advance as a fraction of its size, which centers it on its vertex.</summary>
	private const double Advance = 0.55;

	/// <summary>Defines the room kept around the mark so displaced characters stay inside the logo's box.</summary>
	private const int Margin = 8;

	/// <summary>Defines the furthest a wave lifts a vertex, in logical pixels.</summary>
	private const double Lift = 4;

	/// <summary>Defines the furthest a wave sways a vertex sideways, in logical pixels.</summary>
	private const double Sway = 1.5;

	/// <summary>Defines the time between animation frames, in milliseconds.</summary>
	private const double FrameMs = 50;

	/// <summary>Defines how many frames the gradient takes to travel one full palette cycle.</summary>
	private const double CycleFrames = 240;

	/// <summary>Defines how many frames the palette's hues take to turn once around the color wheel.</summary>
	private const double HueFrames = 900;

	/// <summary>Defines how many frames one scramble pattern lasts.</summary>
	private const int ScrambleFrames = 3;

	/// <summary>Defines the share of fill characters scrambled at any moment.</summary>
	private const double ScrambleChance = 0.04;

	/// <summary>Defines how much of the palette cycle spans the mark's diagonal.</summary>
	private const double Spread = 0.85;

	/// <summary>Defines how long the weave's crest takes to travel the weft once, in seconds, including a rest off its ends.</summary>
	private const double WeaveSeconds = 4.5;

	/// <summary>Defines the weave crest's half width along the weft, as a share of the weft's length.</summary>
	private const double WeaveWidth = 0.09;

	/// <summary>Defines the fill characters from a wave's trough to its crest, sparse to dense.</summary>
	internal const string Ramp = ":-=+*#%@";

	/// <summary>Defines the characters a scrambled vertex flickers through.</summary>
	private const string Scramble = "!?$&<>/\\{}[]01";

	/// <summary>Holds the weft's center line, sampled evenly along its two cubic curves, in units.</summary>
	internal static readonly IReadOnlyList<(double X, double Y)> Weft = BuildWeft();

	/// <summary>Holds every character's resting place, computed once from the mark's geometry.</summary>
	internal static readonly IReadOnlyList<LogoPoint> Points = BuildPoints();

	/// <summary>Counts animation frames; reading it rebuilds the mark each frame.</summary>
	private readonly State<int> _frame = new( 0 );

	/// <summary>Starts and stops the frame timer as the mark comes into and out of view.</summary>
	private readonly MotionSwitch _motion;

	/// <summary>Holds the frame timer while the mark moves, or null while it holds still; Goo stops it when the cell unmounts.</summary>
	private WindowTimer? _clock;

	/// <summary>Creates the mark, still until a build finds it attended.</summary>
	public HomeLogo() => _motion = new( UpdateClock );

	/// <summary>Builds every character at its displaced vertex in its gradient color.</summary>
	protected override Blob BuildView()
	{
		var theme = Theme.Current;
		_motion.Update( Host, LiveMotion.IsAttended( Host ) && Host.Preferences.ReducedMotion != true );
		var frame = _frame.Value;
		var time = frame * FrameMs / 1000;
		var hue = frame / HueFrames * 360;
		Color[] palette = [.. new[] { theme.Accent, theme.AnsiMagenta, theme.AnsiBlue, theme.AnsiCyan }.Select( color => RotateHue( color, hue ) )];
		var phase = frame / CycleFrames;
		var tick = frame / ScrambleFrames;
		var crest = ComputeCrest( time );
		var family = ViewStyle.CodeFontFamily;

		var glyphs = new List<Blob>( Points.Count );
		for ( var index = 0; index < Points.Count; index++ )
		{
			var point = Points[index];
			var height = ComputeHeight( point.X, point.Y, time );
			var (x, y) = Displace( point.X, point.Y, time );
			var weave = point.Kind == LogoPart.Weft ? Bell( (point.Along - crest) / WeaveWidth ) : 0;
			var level = Math.Clamp( 0.5 + 0.5 * height + 0.6 * weave, 0, 1 );
			// Troughs start a few steps up the ramp, so the strokes never thin out to dots and the mark keeps its shape.
			var glyph = ComputeFill( 1.4 * level - 0.4 );
			if ( ComputeNoise( index, tick ) < ScrambleChance )
				glyph = Scramble[(int)(ComputeNoise( index, tick + 1 ) * Scramble.Length)];
			var strength = (point.Kind == LogoPart.Weft ? 0.6 : 0.5) + 0.4 * level;
			glyphs.Add( new Text
			{
				Content = glyph.ToString(),
				Position = PositionType.Absolute,
				Left = Margin + x - FillSize * Advance / 2,
				Top = Margin + y - FillSize / 2.0,
				FontFamily = family,
				FontSize = FillSize,
				FontWeight = FillWeight,
				LineHeight = 1,
				Color = Mix( theme.Canvas, Sample( palette, ComputePosition( point.X, point.Y ) + phase ), Math.Clamp( strength, 0, 1 ) ),
			} );
		}

		return new Container
		{
			Width = Size + Margin * 2,
			Height = Size + Margin * 2,
			FlexShrink = 0,
			AlignSelf = AlignSelf.Center,
			AccessibilityRole = AccessibilityRole.Image,
			AccessibilityName = "Loom",
			Children =
			[
				new Container
				{
					Width = Length.Percent( 100 ),
					Height = Length.Percent( 100 ),
					Accessibility = new Accessibility { Hidden = true },
					Children = glyphs,
				},
			],
		};
	}

	/// <summary>Runs the frame timer while the mark is live and stops it otherwise; the mark keeps its last frame and goes on from it.</summary>
	private void UpdateClock( bool isLive )
	{
		_clock?.Dispose();
		_clock = isLive ? Every( FrameMs, () => _frame.Value++ ) : null;
	}

	/// <summary>Gets where the weave's crest is along the weft, from 0 at its left end to 1 at its right, running a little past both ends so it enters and leaves softly.</summary>
	internal static double ComputeCrest( double time )
	{
		var cycle = time / WeaveSeconds - Math.Floor( time / WeaveSeconds );
		var eased = cycle * cycle * (3 - 2 * cycle);
		return -0.25 + 1.5 * eased;
	}

	/// <summary>Gets a smooth bump that is 1 at zero and fades to nothing past ±1.</summary>
	private static double Bell( double distance ) => Math.Exp( -2 * distance * distance );

	/// <summary>Gets the mesh's wave height at a point of the mark, from -1 in a trough to 1 on a crest.</summary>
	/// <param name="x">The horizontal position in logical pixels from the mark's left edge.</param>
	/// <param name="y">The vertical position in logical pixels from the mark's top edge.</param>
	/// <param name="time">The animation time in seconds.</param>
	internal static double ComputeHeight( double x, double y, double time )
	{
		var u = x / Size;
		var v = y / Size;
		var ripple = Math.Sqrt( Math.Pow( u - 0.5 - 0.3 * Math.Sin( time * 0.4 ), 2 ) + Math.Pow( v - 0.5, 2 ) );
		return 0.55 * Math.Sin( Math.Tau * (1.3 * u - 0.35 * time) + 1.7 * v )
			+ 0.3 * Math.Sin( Math.Tau * (0.8 * v + 0.6 * u + 0.23 * time) )
			+ 0.15 * Math.Sin( Math.Tau * 3 * ripple - 2.1 * time );
	}

	/// <summary>Moves a resting point to its place on the displaced mesh: lifted by the wave's height and swayed sideways.</summary>
	private static (double X, double Y) Displace( double x, double y, double time )
	{
		var sway = Sway * Math.Sin( Math.Tau * (0.9 * y / Size - 0.17 * time) + 3 * x / Size );
		return (x + sway, y - Lift * ComputeHeight( x, y, time ));
	}

	/// <summary>Picks a fill character for a wave height: sparse in troughs, dense on crests.</summary>
	internal static char ComputeFill( double height )
	{
		var level = Math.Clamp( 0.5 + 0.5 * height, 0, 1 );
		return Ramp[Math.Min( Ramp.Length - 1, (int)(level * Ramp.Length) )];
	}

	/// <summary>Gets a repeatable pseudo-random number from 0 up to 1 for a character and a scramble tick.</summary>
	private static double ComputeNoise( int index, int tick )
	{
		var hash = (uint)index * 0x9E3779B1u ^ (uint)tick * 0x85EBCA77u;
		hash ^= hash >> 15;
		hash *= 0x2C1B3C6Du;
		hash ^= hash >> 12;
		return (hash & 0xFFFFFF) / (double)0x1000000;
	}

	/// <summary>Gets a point's place along the tilted gradient, before the animation phase: 0 at the top left, <see cref="Spread"/> at the bottom right.</summary>
	private static double ComputePosition( double x, double y ) => (x + y * 0.5) / (Size * 1.5) * Spread;

	/// <summary>Samples a cyclic palette at a position, easing between neighboring colors.</summary>
	internal static Color Sample( IReadOnlyList<Color> palette, double position )
	{
		var scaled = (position - Math.Floor( position )) * palette.Count;
		var index = (int)scaled % palette.Count;
		var amount = scaled - Math.Floor( scaled );
		return Mix( palette[index], palette[(index + 1) % palette.Count], amount * amount * (3 - 2 * amount) );
	}

	/// <summary>Blends two opaque colors; 0 gives the first and 1 the second.</summary>
	private static Color Mix( Color from, Color to, double amount ) => Color.FromNormalized(
		(float)(from.R + (to.R - from.R) * amount),
		(float)(from.G + (to.G - from.G) * amount),
		(float)(from.B + (to.B - from.B) * amount),
		1f );

	/// <summary>Turns a color's hue around the color wheel while keeping its brightness, as CSS hue-rotate does.</summary>
	/// <param name="color">The color to turn.</param>
	/// <param name="degrees">How far to turn it; 360 is a full turn.</param>
	internal static Color RotateHue( Color color, double degrees )
	{
		var radians = degrees * Math.PI / 180;
		var cos = Math.Cos( radians );
		var sin = Math.Sin( radians );
		var r = color.R;
		var g = color.G;
		var b = color.B;
		return Color.FromNormalized(
			(float)Math.Clamp( (0.213 + 0.787 * cos - 0.213 * sin) * r + (0.715 - 0.715 * cos - 0.715 * sin) * g + (0.072 - 0.072 * cos + 0.928 * sin) * b, 0, 1 ),
			(float)Math.Clamp( (0.213 - 0.213 * cos + 0.143 * sin) * r + (0.715 + 0.285 * cos + 0.140 * sin) * g + (0.072 - 0.072 * cos - 0.283 * sin) * b, 0, 1 ),
			(float)Math.Clamp( (0.213 - 0.213 * cos - 0.787 * sin) * r + (0.715 - 0.715 * cos + 0.715 * sin) * g + (0.072 + 0.928 * cos + 0.072 * sin) * b, 0, 1 ),
			(float)color.A );
	}

	/// <summary>Samples the weft, <c>M8 36 C16 22 24 46 32 32 S48 18 56 28</c>, as evenly spaced points along its two curves.</summary>
	private static List<(double X, double Y)> BuildWeft()
	{
		const int perCurve = 200;
		(double X, double Y)[][] curves =
		[
			[(8, 36), (16, 22), (24, 46), (32, 32)],
			// The smooth curve's first control point mirrors the previous one about their shared point.
			[(32, 32), (40, 18), (48, 18), (56, 28)],
		];
		var points = new List<(double X, double Y)>( perCurve * 2 + 1 );
		foreach ( var curve in curves )
		{
			for ( var step = points.Count == 0 ? 0 : 1; step <= perCurve; step++ )
			{
				var t = (double)step / perCurve;
				var a = (1 - t) * (1 - t) * (1 - t);
				var b = 3 * (1 - t) * (1 - t) * t;
				var c = 3 * (1 - t) * t * t;
				var d = t * t * t;
				points.Add( (a * curve[0].X + b * curve[1].X + c * curve[2].X + d * curve[3].X, a * curve[0].Y + b * curve[1].Y + c * curve[2].Y + d * curve[3].Y) );
			}
		}

		return points;
	}

	/// <summary>Finds a point's distance from the weft's center line, in units, and how far along the weft its nearest point lies, from 0 to 1.</summary>
	internal static (double Distance, double Along) MeasureWeft( double x, double y )
	{
		var best = double.MaxValue;
		var along = 0.0;
		for ( var index = 0; index < Weft.Count; index++ )
		{
			var distance = Math.Pow( Weft[index].X - x, 2 ) + Math.Pow( Weft[index].Y - y, 2 );
			if ( distance < best )
			{
				best = distance;
				along = (double)index / (Weft.Count - 1);
			}
		}

		return (Math.Sqrt( best ), along);
	}

	/// <summary>Places the characters on the fill grid: the wave, then the ring outside the gap around the wave.</summary>
	private static List<LogoPoint> BuildPoints()
	{
		var points = new List<LogoPoint>();
		var count = (int)(Size / FillStep);
		for ( var row = 0; row < count; row++ )
		{
			for ( var column = 0; column < count; column++ )
			{
				var x = (column + 0.5) * FillStep;
				var y = (row + 0.5) * FillStep;
				var (weft, along) = MeasureWeft( x / Scale, y / Scale );
				var ring = Math.Abs( Math.Sqrt( Math.Pow( x / Scale - Center, 2 ) + Math.Pow( y / Scale - Center, 2 ) ) - RingRadius );
				if ( weft <= HalfStroke )
					points.Add( new( x, y, LogoPart.Weft, along ) );
				else if ( weft > GapRadius && ring <= HalfStroke )
					points.Add( new( x, y, LogoPart.Ring, 0 ) );
			}
		}

		return points;
	}

	/// <summary>Names the part of the mark a character belongs to.</summary>
	internal enum LogoPart
	{
		/// <summary>The ring.</summary>
		Ring,

		/// <summary>The wave that crosses the ring.</summary>
		Weft,
	}

	/// <summary>Describes one character's resting place, its center in logical pixels from the mark's top left.</summary>
	/// <param name="X">The horizontal center.</param>
	/// <param name="Y">The vertical center.</param>
	/// <param name="Kind">The part of the mark it draws.</param>
	/// <param name="Along">For a weft character, how far along the weft it lies, from 0 at the left end to 1 at the right; otherwise 0.</param>
	internal readonly record struct LogoPoint( double X, double Y, LogoPart Kind, double Along );
}
