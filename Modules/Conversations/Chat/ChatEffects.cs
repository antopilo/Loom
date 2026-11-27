using Goo;

namespace Lokn.Modules.Chat;

/// <summary>Loads the chat's Goo shader effect programs, embedded in this module by <c>scripts/shaders.ps1</c>, once each.</summary>
internal static class ChatEffects
{
	/// <summary>Gets the shimmer that sweeps light across the working line's label.</summary>
	internal static ShaderEffectProgram Shimmer => _shimmer.Value;

	/// <summary>Gets the light that runs around the prompt's edge while the agent works.</summary>
	internal static ShaderEffectProgram Glow => _glow.Value;

	/// <summary>Holds the shimmer program, read on first use.</summary>
	private static readonly Lazy<ShaderEffectProgram> _shimmer = new( () => Load( "shimmer" ) );

	/// <summary>Holds the glow program, read on first use.</summary>
	private static readonly Lazy<ShaderEffectProgram> _glow = new( () => Load( "glow" ) );

	/// <summary>Reads one embedded program by name.</summary>
	private static ShaderEffectProgram Load( string name )
	{
		using var stream = typeof( ChatEffects ).Assembly.GetManifestResourceStream( $"Lokn.Chat.{name}.goo-effect" )
			?? throw new InvalidOperationException( $"The {name} shader effect is not embedded." );
		using var bytes = new MemoryStream();
		stream.CopyTo( bytes );
		return new ShaderEffectProgram( bytes.ToArray(), false );
	}
}
