using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes content that may enter with motion.</summary>
/// <param name="Content">The content to show.</param>
/// <param name="Animate">Whether the content fades in and rises into place when it mounts; false shows it at rest. Only a mount's first input counts, so later builds never replay the entrance.</param>
/// <param name="DelayMs">How long the entrance waits after mounting, in milliseconds, to stagger content that enters together.</param>
/// <param name="Cover">Whether the wrapper covers its parent's whole box, for overlays whose content is placed against the parent's edges.</param>
/// <param name="Blur">Whether the entrance also sharpens the content from a blur, as new steps of the agent's work arrive.</param>
internal sealed record RevealInput( Blob Content, bool Animate, double DelayMs = 0, bool Cover = false, bool Blur = false );

/// <summary>Shows content that enters once: it fades in while it rises <see cref="ChatMotion.Rise"/> pixels into place.</summary>
/// <remarks>
/// The entrance binds opacity and translation to animations this cell owns, so it never rebuilds the cell or moves the layout.
/// They are bound on a plain wrapper, never on the content: Goo freezes a blob once it is returned, and the content is part of
/// an input snapshot that later builds reuse. The wrapper sizes like an ordinary block; overlays set <see cref="RevealInput.Cover"/>.
/// Goo removes unmounted content at once, so nothing can animate out from here; an owner that controls the removal wraps the
/// cell in <see cref="BuildExit"/>, keeps it for <see cref="ChatMotion.FastMs"/> while it leaves, then removes it.
/// </remarks>
internal sealed class Reveal : GuardedCell<RevealInput>
{
	/// <summary>Tracks the entrance's opacity, or null when the content shows at rest.</summary>
	private Anim<double>? _opacity;

	/// <summary>Tracks the entrance's offset from the content's place, or null when the content shows at rest.</summary>
	private Anim<Point>? _offset;

	/// <summary>Reports whether the first input was read, after which inputs no longer decide the entrance.</summary>
	private bool _hasInput;

	/// <summary>Tracks the fade of the blur over the entering content, or null once it has cleared.</summary>
	private Anim<double>? _blur;

	/// <summary>Reports whether the cell was built with a blur layer, which keeps the content inside a holder for the cell's life.</summary>
	private bool _hasBlurHolder;

	/// <summary>Defines the blur radius new content sharpens from, in logical pixels.</summary>
	private const double EnterBlur = 6;

	/// <summary>Starts the content hidden and lowered when its first input asks for an entrance.</summary>
	protected override void InputChanged( RevealInput input )
	{
		if ( _hasInput )
			return;

		_hasInput = true;
		if ( !input.Animate )
			return;

		_opacity = AnimateValue( 0.0 );
		_offset = AnimateValue( new Point { X = 0, Y = ChatMotion.Rise } );
		// The blur is a layer over the content that fades away; Goo blurs what is behind an element, not the element itself.
		if ( input.Blur && !input.Cover && Motion.TimeScale > 0 )
		{
			_blur = AnimateValue( 1.0 );
			_hasBlurHolder = true;
		}
	}

	/// <summary>Plays the entrance once the content has its place.</summary>
	protected override void Mounted()
	{
		if ( _opacity is null || _offset is null )
			return;

		var spec = Motion.Tween( ChatMotion.BaseMs, ChatMotion.Ease );
		if ( Input.DelayMs > 0 )
			spec = Motion.Delay( Input.DelayMs, spec );
		_opacity.To( 1.0, spec );
		_offset.To( new Point { X = 0, Y = 0 }, spec );
		if ( _blur is not null )
		{
			_blur.To( 0.0, spec );
			// The blur layer costs a pass every frame while present, so it leaves once the entrance has played.
			After( (Input.DelayMs + ChatMotion.BaseMs) / Motion.TimeScale + ChatMotion.FastMs, () =>
			{
				_blur = null;
				Rebuild();
			} );
		}
	}

	/// <summary>Builds a fresh wrapper carrying the entrance's fade and offset around the content.</summary>
	protected override Blob BuildView( RevealInput input )
	{
		var wrapper = new Container
		{
			OpacityAnim = _opacity,
			TranslateAnim = _offset,
			Children = [input.Content],
		};
		if ( input.Cover )
		{
			wrapper.Position = PositionType.Absolute;
			wrapper.Left = 0;
			wrapper.Top = 0;
			wrapper.Right = 0;
			wrapper.Bottom = 0;
		}
		if ( !_hasBlurHolder )
			return wrapper;
		return new Container
		{
			Children = _blur is null ? [wrapper] :
			[
				wrapper,
				new Container
				{
					Key = "reveal-blur",
					Position = PositionType.Absolute,
					Left = 0,
					Top = 0,
					Right = 0,
					Bottom = 0,
					BackdropBlur = EnterBlur,
					OpacityAnim = _blur,
				},
			],
		};
	}

	/// <summary>Wraps content its owner removes after a short exit: build it this way while present, then with <paramref name="isLeaving"/> set for <see cref="ChatMotion.FastMs"/> before removing it.</summary>
	/// <param name="key">The wrapper's stable sibling key.</param>
	/// <param name="content">The content that leaves; a <see cref="Reveal"/> goes inside, so its entrance and this exit move different elements.</param>
	/// <param name="isLeaving">Whether the content fades out and sinks back by <see cref="ChatMotion.Rise"/>, the entrance reversed.</param>
	/// <remarks>The wrapper is a plain container: Goo styles are write-only, so it cannot copy the content's flex sizing. Use it where the content needs none.</remarks>
	internal static Container BuildExit( string key, Blob content, bool isLeaving ) => new()
	{
		Key = key,
		Opacity = isLeaving ? 0 : 1,
		Transform = isLeaving ? new PanelTransform { TranslateY = ChatMotion.Rise } : new PanelTransform(),
		TransitionMs = ChatMotion.FastMs,
		TransitionEasing = ChatMotion.Ease,
		TransitionProperties = [TransitionProperty.Opacity, TransitionProperty.Transform],
		Children = [content],
	};
}
