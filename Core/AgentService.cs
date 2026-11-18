namespace Lokn.Core;

/// <summary>Answers one-off questions with the user's agent backend, such as naming a chat or writing a commit message, without a chat or tools.</summary>
/// <remarks>The Omp module publishes it with <see cref="ModuleBuilder.Service{T}"/>; resolve it with <c>app.Services.Get&lt;IAgentService&gt;()</c> on each use, and leave the feature out when it is null.</remarks>
public interface IAgentService
{
	/// <summary>Asks a profile's chosen model one question about some material.</summary>
	/// <param name="profile">The profile whose account answers.</param>
	/// <param name="question">The instruction.</param>
	/// <param name="material">What the question is about, of any length.</param>
	/// <param name="cancellationToken">Stops the question.</param>
	Task<string> AskAsync( SettingsProfile profile, string question, string material, CancellationToken cancellationToken = default );

	/// <summary>Asks a profile's small, fast model one bounded question about some material.</summary>
	/// <param name="profile">The profile whose account answers.</param>
	/// <param name="question">The instruction.</param>
	/// <param name="material">What the question is about.</param>
	/// <param name="cancellationToken">Stops the question.</param>
	Task<string> AskSmallAsync( SettingsProfile profile, string question, string material, CancellationToken cancellationToken = default );
}
