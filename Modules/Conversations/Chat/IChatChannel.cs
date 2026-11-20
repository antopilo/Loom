using System.Text.Json;

namespace Lokn.Modules.Chat;

/// <summary>Carries one chat's calls from a <see cref="RemoteChatSession"/> to the host that runs the chat, such as a <c>call</c> request over the Remote module's connection.</summary>
/// <remarks>The host's pushes for the chat go the other way, into <see cref="RemoteChatSession"/>'s <c>Apply</c> methods, on the UI thread.</remarks>
public interface IChatChannel
{
	/// <summary>Invokes one of <see cref="ChatSessionCall.Methods"/> on the host's session of the chat.</summary>
	/// <param name="method">The method's name.</param>
	/// <param name="args">The arguments in parameter order, built by <see cref="ChatSessionCall.BuildArgs"/>.</param>
	/// <param name="cancellationToken">Stops waiting for the answer.</param>
	/// <returns>The method's result as JSON, or JSON null for none; may complete off the UI thread.</returns>
	Task<JsonElement> CallAsync( string method, JsonElement[] args, CancellationToken cancellationToken );
}
