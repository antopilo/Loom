using Lokn.Core;

namespace Lokn.Modules.Projects;

/// <summary>Gives the working folder of a Workspaces session other than a project's own, such as a task's; contribute it to <see cref="Point"/>.</summary>
/// <remarks>The working folder is the first folder any contribution gives for the current session.</remarks>
/// <param name="Compute">Gets the folder for a session key, or null for a session it does not cover or one outside the active profile.</param>
public sealed record SessionFolder( Func<LoknApplication, string, string?> Compute )
{
	/// <summary>The extension point the Projects module reads session folders from.</summary>
	public const string Point = "projects.folders";
}
