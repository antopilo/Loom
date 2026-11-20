using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Qualifies Workspaces session keys with the host they belong to: This PC's keys stay as they were, a paired host's end with <c>@hostId</c>.</summary>
internal static class HostSessionKeys
{
	/// <summary>Gets a host's session key: the key itself for This PC, else the key followed by <c>@</c> and the host.</summary>
	/// <param name="key">The key, such as <c>conversation:&lt;id&gt;</c>.</param>
	/// <param name="hostId">The host.</param>
	public static string Qualify( string key, string hostId ) => hostId == LoknHost.LocalId ? key : key + "@" + hostId;

	/// <summary>Splits a session key into its bare key and its host; a key without <c>@</c> is This PC's.</summary>
	/// <param name="key">The session key.</param>
	public static (string Key, string HostId) Split( string key )
	{
		var at = key.IndexOf( '@' );
		return at < 0 ? (key, LoknHost.LocalId) : (key[..at], key[(at + 1)..]);
	}
}
