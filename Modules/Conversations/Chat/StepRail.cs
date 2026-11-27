using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Describes one step's piece of the line in the gutter.</summary>
/// <param name="IsThinking">Whether the step is reasoning, whose mark the rail draws; a tool's head draws its own.</param>
/// <param name="JoinsAbove">Whether a step comes before it in the same run.</param>
/// <param name="JoinsBelow">Whether a step comes after it in the same run.</param>
/// <param name="GapAbove">The space between it and the step before, in logical pixels.</param>
/// <param name="Enters">Whether the step is new on screen, so its line grows down to it. Only the first input counts.</param>
internal sealed record StepRailInput( bool IsThinking, bool JoinsAbove, bool JoinsBelow, double GapAbove, bool Enters );

/// <summary>Draws a step's piece of the line that joins a run's reasoning and tool steps, left of the reading column.</summary>
/// <remarks>
/// A new step draws the line as one stroke travelling down: the step above extends its line to its bottom, then the new step's line grows
/// from there to its mark, and a reasoning mark fades in as the line reaches it. The segments grow by scale from their top, which never moves the layout.
/// </remarks>
internal sealed class StepRail : GuardedCell<StepRailInput>
{
	/// <summary>Defines how long each segment of the line takes to grow, in milliseconds.</summary>
	internal const double GrowMs = ChatMotion.FastMs;

	/// <summary>Defines the space between the line and a mark, in logical pixels.</summary>
	private const double LineGap = 4;

	/// <summary>Defines the line's opacity over the muted text color: the theme's border color was too faint to follow on dark themes.</summary>
	private const double LineOpacity = 0.4;

	/// <summary>Tracks the growth of the segment above the mark, or null when it shows at rest.</summary>
	private Anim<double>? _above;

	/// <summary>Tracks the growth of the segment below the mark, or null when it shows at rest.</summary>
	private Anim<double>? _below;

	/// <summary>Tracks the reasoning mark's fade, or null when it shows at rest.</summary>
	private Anim<double>? _mark;

	/// <summary>Reports whether the first input was read, after which only a new step below animates.</summary>
	private bool _hasInput;

	/// <summary>Reports whether the last input joined a step below, so a step that arrives below grows the line to it.</summary>
	private bool _joinedBelow;

	/// <summary>Starts a new step's line and mark hidden, and grows the line below once a step arrives under it.</summary>
	protected override void InputChanged( StepRailInput input )
	{
		var isMoving = Motion.TimeScale > 0;
		if ( !_hasInput )
		{
			_hasInput = true;
			_joinedBelow = input.JoinsBelow;
			if ( input.Enters && isMoving )
			{
				if ( input.JoinsAbove )
					_above = AnimateValue( 0.0 );
				if ( input.IsThinking )
					_mark = AnimateValue( 0.0 );
			}
			return;
		}

		if ( input.JoinsBelow && !_joinedBelow && isMoving )
		{
			_below ??= AnimateValue( 0.0 );
			_below.Snap( 0.0 );
			_below.To( 1.0, Motion.Tween( GrowMs, ChatMotion.Ease ) );
		}
		_joinedBelow = input.JoinsBelow;
	}

	/// <summary>Grows a new step's line once the step above has drawn its own to its bottom, then shows the reasoning mark.</summary>
	protected override void Mounted()
	{
		var reach = Input.JoinsAbove ? GrowMs * 2 : 0;
		_above?.To( 1.0, Motion.Delay( GrowMs, Motion.Tween( GrowMs, ChatMotion.Ease ) ) );
		_mark?.To( 1.0, Motion.Delay( reach, Motion.Tween( ChatMotion.FastMs, ChatMotion.Ease ) ) );
	}

	/// <summary>Builds the rail: the segment above the mark, room for the mark, and the segment down to the step's bottom.</summary>
	protected override Blob BuildView( StepRailInput input )
	{
		var theme = Theme.Current;
		// The mark sits centered on the first line: a tool's head is one control tall, reasoning starts with a line of body text.
		var lineHeight = input.IsThinking ? theme.FontSize * MarkdownView.BodyLineHeight : theme.ControlHeight;
		var markTop = (lineHeight - ChatIcons.MarkSize) / 2;
		var above = input.JoinsAbove ? input.GapAbove : 0;
		var color = theme.Muted.WithAlpha( LineOpacity );
		return new Container
		{
			Key = "step-rail",
			Position = PositionType.Absolute,
			Left = -ChatContent.StepInset,
			Top = -above,
			Bottom = 0,
			Width = ChatIcons.MarkSize,
			AlignItems = AlignItems.Center,
			Children =
			[
				BuildSegment( input.JoinsAbove ? color : Color.Transparent, _above, Math.Max( 0, above + markTop - LineGap ) ),
				new Container
				{
					Height = ChatIcons.MarkSize + LineGap * 2,
					FlexShrink = 0,
					JustifyContent = JustifyContent.Center,
					OpacityAnim = _mark,
					Children = input.IsThinking ? [ChatIcons.BuildIcon( ChatIcons.ThinkingGlyph )] : [],
				},
				BuildSegment( input.JoinsBelow ? color : Color.Transparent, _below, null ),
			],
		};
	}

	/// <summary>Builds one segment of the line, growing down from its top while its animation runs.</summary>
	/// <param name="color">The line color, transparent where no step joins.</param>
	/// <param name="growth">The segment's growth, or null at rest.</param>
	/// <param name="height">The segment's height, or null to fill the rest of the rail.</param>
	private static Container BuildSegment( Color color, Anim<double>? growth, double? height ) => new()
	{
		Width = Theme.Current.BorderWidth,
		Height = height is { } fixedHeight ? fixedHeight : Length.Auto,
		FlexGrow = height is null ? 1 : 0,
		FlexShrink = 0,
		BackgroundColor = color,
		ScaleAnim = growth,
		TransformOriginY = 0,
	};
}
