using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes a task's progress in an agent's todo list.</summary>
internal enum TaskState
{
	/// <summary>The task has not started.</summary>
	Pending,

	/// <summary>The agent is working on the task.</summary>
	Active,

	/// <summary>The task is finished.</summary>
	Done,

	/// <summary>The task was dropped without finishing.</summary>
	Dropped,
}

/// <summary>Builds the chat's drawn status marks, progress rings, tool icons and badges in the current theme.</summary>
internal static class ChatIcons
{
	/// <summary>Defines the square size of status marks and rings in logical pixels.</summary>
	internal const int MarkSize = 14;

	/// <summary>Defines the stroke width of drawn rings and checks in logical pixels.</summary>
	private const double StrokeWidth = 1.5;

	/// <summary>Defines the diameter of the dot inside an active task's ring in logical pixels.</summary>
	private const int ActiveDotSize = 6;

	/// <summary>Defines the opacity of a progress ring's unfilled track.</summary>
	private const double TrackOpacity = 0.35;

	/// <summary>Defines tool icon glyph size in logical pixels.</summary>
	private const int IconSize = 12;

	/// <summary>Defines icon font weight on the OpenType scale.</summary>
	private const int IconWeight = 400;

	/// <summary>Defines the badge text inset in logical pixels.</summary>
	private const int BadgeInset = 6;

	/// <summary>Defines the badge corner radius in logical pixels.</summary>
	private const int BadgeRadius = 4;

	/// <summary>Defines the reasoning glyph from Segoe Fluent Icons.</summary>
	internal const string ThinkingGlyph = "\uEA80";

	/// <summary>Defines the checklist glyph from Segoe Fluent Icons.</summary>
	internal const string TodoGlyph = "\uE9D5";

	/// <summary>Holds the drawing box shared by every mark.</summary>
	private static readonly ElementRect Box = new() { Width = MarkSize, Height = MarkSize };

	/// <summary>Holds a full circle starting at the top and running clockwise, so a trimmed stroke reads as progress.</summary>
	private static readonly VectorPath Ring = VectorPath.Parse( "M7 1.5 A5.5 5.5 0 1 1 7 12.5 A5.5 5.5 0 1 1 7 1.5", Box );

	/// <summary>Holds the check drawn inside a finished task's disc.</summary>
	private static readonly VectorPath Check = VectorPath.Parse( "M4.4 7.3 L6.2 9.1 L9.7 5.2", Box );

	/// <summary>Holds the cross drawn for failures and dropped tasks.</summary>
	private static readonly VectorPath Cross = VectorPath.Parse( "M4.8 4.8 L9.2 9.2 M9.2 4.8 L4.8 9.2", Box );

	/// <summary>Defines the arrow's square drawing size in logical pixels.</summary>
	private const int ArrowSize = 14;

	/// <summary>Defines the arrow's stroke width in logical pixels.</summary>
	private const double ArrowStroke = 2;

	/// <summary>Holds an upward arrow: a stem and a chevron head.</summary>
	private static readonly VectorPath Arrow = VectorPath.Parse( "M7 12 L7 2.5 M2.75 6.75 L7 2.5 L11.25 6.75", Box );

	/// <summary>Retains the registered icon font.</summary>
	private static readonly FontSource IconFont = SystemFonts.Register( "Lokn Chat Icons", IconWeight, italic: false, "SegoeIcons.ttf" );

	/// <summary>Builds a task's status mark: an empty ring, a ring around a dot, a checked disc, or a crossed ring.</summary>
	internal static Blob BuildTaskMark( TaskState state )
	{
		var theme = Theme.Current;
		return state switch
		{
			TaskState.Active => BuildLayers( BuildRing( theme.Accent ), new Container
			{
				Width = ActiveDotSize,
				Height = ActiveDotSize,
				BorderRadius = ActiveDotSize / 2.0,
				BackgroundColor = theme.Accent,
			} ),
			TaskState.Done => new Container
			{
				Width = MarkSize,
				Height = MarkSize,
				FlexShrink = 0,
				BorderRadius = MarkSize / 2.0,
				BackgroundColor = theme.Success,
				Children = [BuildStroke( Check, theme.Panel, StrokeWidth + 0.25 )],
			},
			TaskState.Dropped => BuildLayers( BuildRing( theme.Muted ), BuildStroke( Cross, theme.Muted, StrokeWidth ) ),
			_ => BuildRing( theme.Muted ),
		};
	}

	/// <summary>Builds a ring filled clockwise by a fraction: accent while under way, success once complete.</summary>
	internal static Blob BuildProgress( double fraction )
	{
		var theme = Theme.Current;
		var color = fraction >= 1 ? theme.Success : theme.Accent;
		var track = BuildRing( theme.Muted );
		track.Opacity = TrackOpacity;
		var filled = BuildRing( color );
		filled.StrokeEnd = Math.Clamp( fraction, 0, 1 );
		return BuildLayers( track, filled );
	}

	/// <summary>Builds a quiet ring filled clockwise by a fraction, for a level such as the model's effort: empty when off, full at the most.</summary>
	internal static Blob BuildLevel( double fraction )
	{
		var theme = Theme.Current;
		var track = BuildRing( theme.Muted );
		track.Opacity = TrackOpacity;
		var filled = BuildRing( theme.Text );
		filled.StrokeEnd = Math.Clamp( fraction, 0, 1 );
		return BuildLayers( track, filled );
	}

	/// <summary>Builds the red cross that marks a failed tool.</summary>
	internal static Blob BuildFailure() => BuildStroke( Cross, Theme.Current.Error, StrokeWidth + 0.25 );

	/// <summary>Builds the muted check that marks a finished run of tools.</summary>
	internal static Blob BuildFinished() => BuildStroke( Check, Theme.Current.Muted, StrokeWidth );

	/// <summary>Builds a spinning arc for work in progress; it animates on its own and stops when unmounted.</summary>
	internal static Blob BuildSpinner( string key ) => BuildSpinner( key, Theme.Current.Accent, MarkSize, StrokeWidth );

	/// <summary>Builds a spinning arc of a color, square size and stroke width; it animates on its own and stops when unmounted.</summary>
	internal static Blob BuildSpinner( string key, Color color, int size, double width ) => Cell.Mount<SpinnerInput, Spinner>( key, new( color, size, width ) );

	/// <summary>Builds an upward arrow, a stem under a chevron head, for round buttons; rotate it to point elsewhere.</summary>
	internal static Shape BuildArrow( Color color ) => new()
	{
		Path = Arrow,
		Fit = ShapeFit.Contain,
		Width = ArrowSize,
		Height = ArrowSize,
		FlexShrink = 0,
		Stroke = new VectorStroke( ArrowStroke, color )
		{
			Cap = StrokeCap.Round,
			Join = StrokeJoin.Round,
		},
	};

	/// <summary>Builds a Segoe Fluent Icons glyph at the tool icon size, centered in a mark-sized box so icons line up with marks.</summary>
	internal static Blob BuildIcon( string glyph, Color? color = null ) => new Container
	{
		Width = MarkSize,
		Height = MarkSize,
		FlexShrink = 0,
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		Children =
		[
			new Text
			{
				Content = glyph,
				FontFamily = IconFont.Family,
				FontSize = IconSize,
				Color = color ?? Theme.Current.Muted,
			},
		],
	};

	/// <summary>Builds a small rounded label, such as a code language.</summary>
	internal static Blob BuildBadge( string text ) => new Container
	{
		FlexShrink = 0,
		Padding = new Edges( 1, BadgeInset ),
		BorderRadius = BadgeRadius,
		BackgroundColor = Theme.Current.Hover,
		Children =
		[
			new Text
			{
				Content = text,
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize - 1,
				Color = Theme.Current.Muted,
				TextWrap = TextWrap.NoWrap,
			},
		],
	};

	/// <summary>Gets the Segoe Fluent Icons glyph for a tool by what it does.</summary>
	internal static string ComputeToolGlyph( string name ) => name switch
	{
		"read" => "\uE8A5",
		"write" => "\uE74E",
		"edit" or "ast_edit" => "\uE70F",
		"bash" => "\uE756",
		"eval" => "\uE943",
		"grep" or "glob" or "find" or "ast_grep" or "search" => "\uE721",
		"web_search" => "\uE774",
		"fetch" or "web_fetch" => "\uE71B",
		"ask" => "\uE897",
		"task" => "\uE716",
		"wait" => "\uE823",
		"todo" or "todo_write" => TodoGlyph,
		_ when name.StartsWith( "shell_", StringComparison.Ordinal ) || name.StartsWith( "workspace_", StringComparison.Ordinal ) => "\uE8A9",
		_ => "\uE90F",
	};

	/// <summary>Builds an unfilled ring stroke.</summary>
	private static Shape BuildRing( Color color ) => BuildStroke( Ring, color, StrokeWidth );

	/// <summary>Builds one stroked path at the mark size.</summary>
	private static Shape BuildStroke( VectorPath path, Color color, double width ) => BuildStroke( path, color, width, MarkSize );

	/// <summary>Builds one stroked path scaled into a square box.</summary>
	private static Shape BuildStroke( VectorPath path, Color color, double width, int size ) => new()
	{
		Path = path,
		Fit = ShapeFit.Contain,
		Width = size,
		Height = size,
		FlexShrink = 0,
		Stroke = new VectorStroke( width, color )
		{
			Cap = StrokeCap.Round,
			Join = StrokeJoin.Round,
		},
	};

	/// <summary>Stacks marks centered in one mark-sized box; the first is the base, the rest are drawn over it.</summary>
	private static Container BuildLayers( Blob first, params Blob[] rest )
	{
		var children = new List<Blob> { first };
		foreach ( var layer in rest )
		{
			children.Add( new Container
			{
				Position = PositionType.Absolute,
				Left = 0,
				Top = 0,
				Width = MarkSize,
				Height = MarkSize,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Children = [layer],
			} );
		}

		return new Container
		{
			Width = MarkSize,
			Height = MarkSize,
			FlexShrink = 0,
			Children = children,
		};
	}

	/// <summary>Describes a spinner's arc.</summary>
	/// <param name="Color">The arc color.</param>
	/// <param name="Size">The square size in logical pixels.</param>
	/// <param name="Width">The stroke width in logical pixels.</param>
	private sealed record SpinnerInput( Color Color, int Size, double Width );

	/// <summary>Turns a three-quarter arc once per period with a linear transition, so it spins smoothly between rare rebuilds.</summary>
	/// <remarks>It turns while it is attended (<see cref="LiveMotion.IsAttended"/>) and otherwise comes to rest after the turn in progress.</remarks>
	private sealed class Spinner : GuardedCell<SpinnerInput>
	{
		/// <summary>Defines the time for one full turn in milliseconds.</summary>
		private const double TurnMs = 900;

		/// <summary>Defines how much of the ring the arc covers.</summary>
		private const double ArcLength = 0.7;

		/// <summary>Counts completed turns; each one adds a full rotation for the transition to animate.</summary>
		private readonly State<int> _turns = new( 0 );

		/// <summary>Starts and stops turning as the spinner comes into and out of view.</summary>
		private readonly MotionSwitch _motion;

		/// <summary>Holds the turn timer while the spinner turns, or null; Goo stops it when the spinner unmounts.</summary>
		private WindowTimer? _clock;

		/// <summary>Creates the spinner; a build that finds it attended starts it turning.</summary>
		public Spinner() => _motion = new( UpdateTurning );

		/// <summary>Starts a turn at once and one per period while live; otherwise stops after the turn in progress.</summary>
		private void UpdateTurning( bool isLive )
		{
			_clock?.Dispose();
			_clock = null;
			if ( !isLive )
				return;

			_turns.Value++;
			_clock = Every( TurnMs, () => _turns.Value++ );
		}

		/// <summary>Builds the arc at its next whole turn.</summary>
		protected override Blob BuildView( SpinnerInput input )
		{
			_motion.Update( Host, LiveMotion.IsAttended( Host ) );
			var arc = BuildStroke( Ring, input.Color, input.Width, input.Size );
			arc.StrokeEnd = ArcLength;
			arc.Transform = new PanelTransform { Rotate = _turns.Value * 360.0 };
			arc.TransitionMs = TurnMs;
			arc.TransitionEasing = Easing.Linear;
			arc.TransitionProperties = [TransitionProperty.Transform];
			return arc;
		}
	}
}
