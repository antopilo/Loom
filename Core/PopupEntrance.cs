using Goo;

namespace Lokn.Core;

/// <summary>Supplies a <see cref="PopupEntrance"/> with the surface it plays the entrance on.</summary>
/// <param name="Content">The surface, with its own background, border and padding; rebuilt by the owner like any child.</param>
/// <param name="Rise">How far the surface starts below its place, in logical pixels; negative starts above it.</param>
public sealed record PopupEntranceInput( Blob Content, double Rise )
{
	/// <summary>Gets whether the surface grows to fill its parent, such as a portal with a fixed size.</summary>
	public bool Fill { get; init; }
}

/// <summary>Plays <see cref="PopupMotion"/>'s entrance on a floating surface that is not a <see cref="NativePopup"/> or <see cref="ModalWindow"/>,
/// such as a portal a module builds itself.</summary>
/// <remarks>Mount it with <c>Cell.Mount&lt;PopupEntranceInput, PopupEntrance&gt;</c> inside the portal; the entrance plays once, when it mounts.</remarks>
public sealed class PopupEntrance : GuardedCell<PopupEntranceInput>
{
	/// <summary>Tracks the entrance from 0, faded out and offset, to 1 at rest.</summary>
	private Anim<double>? _appear;

	/// <summary>Starts the entrance on the first input.</summary>
	protected override void InputChanged( PopupEntranceInput input ) => _appear ??= PopupMotion.Start( Animate( 0.0 ) );

	/// <summary>Builds the surface at the entrance's current point.</summary>
	protected override Blob BuildView( PopupEntranceInput input )
	{
		var surface = new Container { Children = [input.Content] };
		if ( input.Fill )
			surface.FlexGrow = 1;
		PopupMotion.Apply( surface, _appear?.Value ?? 1, input.Rise );
		return surface;
	}
}
