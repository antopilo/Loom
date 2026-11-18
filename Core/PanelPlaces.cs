namespace Lokn.Core;

/// <summary>Tells which workspace panels belong to, so a feature opened for one panel, such as an agent's browser, stays in that panel's workspace.</summary>
/// <remarks>Publish it with <see cref="ModuleBuilder.Service{T}"/>; read it with <c>app.Services.Get&lt;IPanelPlaces&gt;()</c> on each use.</remarks>
public interface IPanelPlaces
{
	/// <summary>Reports whether two open panels are in the same workspace: both on screen, or both in one saved layout.</summary>
	/// <param name="first">One panel's identifier.</param>
	/// <param name="second">The other panel's identifier.</param>
	bool AreTogether( Guid first, Guid second );
}
