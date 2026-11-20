using System.Text.Json;
using Lokn.Modules.Chat;

namespace Lokn.Modules.Conversations;

/// <summary>A chat channel that fails every call with IOException("Not connected to the host."); used by <see cref="ChatHost.Detach"/>.</summary>
internal sealed class OfflineChatChannel : IChatChannel
{
	/// <summary>Gets the one channel every detached host's sessions share.</summary>
	public static OfflineChatChannel Instance { get; } = new();

	/// <summary>Creates the shared channel.</summary>
	private OfflineChatChannel()
	{
	}

	/// <summary>Fails the call: no host answers.</summary>
	public Task<JsonElement> CallAsync( string method, JsonElement[] args, CancellationToken cancellationToken ) =>
		Task.FromException<JsonElement>( new IOException( "Not connected to the host." ) );
}
