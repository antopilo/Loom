using System.Numerics;
using Goo;

namespace Lokn.Modules.Workspaces;

/// <summary>Ends a sidebar title too long for its row with a soft fade to transparent instead of an ellipsis, as Arc does.</summary>
/// <remarks>The fade is a Goo shader effect compiled from <c>Shaders/fade.slang</c> by <c>scripts/shaders.ps1</c>; it scales the text's own pixels, so it
/// shows the same over any row color and over a translucent window.</remarks>
public static class SidebarFade
{
	/// <summary>Defines the fade's length as a multiple of the text's height.</summary>
	private const float LengthInHeights = 2.2f;

	/// <summary>Holds the effect, shared by every faded title, created on first use.</summary>
	private static readonly Lazy<ShaderEffect> _effect = new( () =>
	{
		using var stream = typeof( SidebarFade ).Assembly.GetManifestResourceStream( "Lokn.Workspaces.fade.goo-effect" )
			?? throw new InvalidOperationException( "The sidebar fade effect is not embedded." );
		using var bytes = new MemoryStream();
		stream.CopyTo( bytes );
		var effect = new ShaderEffect( new ShaderEffectProgram( bytes.ToArray(), false ), false, 0, 0 );
		effect.SetParameter( 0, new Vector4( LengthInHeights, 0, 0, 0 ) );
		return effect;
	} );

	/// <summary>Makes a one-line title fill its row's room and fade out at the end of it, rather than end in an ellipsis.</summary>
	/// <param name="text">The title, which must not wrap.</param>
	/// <returns>The same title.</returns>
	public static Text Apply( Text text )
	{
		text.TextTrimming = TextTrimming.None;
		text.Overflow = Overflow.Hidden;
		// The fade sits at the end of the room, so a title that fits keeps all its letters.
		text.FlexGrow = 1;
		text.ShaderEffect = _effect.Value;
		return text;
	}
}
