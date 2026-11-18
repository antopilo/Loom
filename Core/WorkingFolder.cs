namespace Lokn.Core;

/// <summary>Gives the folder the user works in now, such as the open project's, so a feature starts its tools there.</summary>
/// <remarks>Publish it with <see cref="ModuleBuilder.Service{T}"/>; read it with <c>app.Services.Get&lt;IWorkingFolder&gt;()</c> on each use.</remarks>
public interface IWorkingFolder
{
	/// <summary>Gets the folder of the active profile's open place, or null when that place has none.</summary>
	string? Current
	{
		get;
	}
}
