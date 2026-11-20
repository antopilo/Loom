using System.Reflection;

namespace Lokn.Modules.Remote;

/// <summary>Identifies the build a host or client runs, so Loom on this PC notices a daemon started from another build (docs/remote.md, Chat and store frames, <c>sync</c>).</summary>
public static class RemoteProtocol
{
	/// <summary>Gets the running build: the application's version and the Remote module's, as <c>app+module</c>.</summary>
	/// <remarks>Both sides of the pipe run from one install, so any difference means one of them was updated or rebuilt since the daemon started.</remarks>
	public static string AppVersion { get; } =
		$"{Assembly.GetEntryAssembly()?.GetName().Version?.ToString( 3 ) ?? "0.0.0"}+{typeof( RemoteProtocol ).Assembly.GetName().Version?.ToString( 3 ) ?? "0.0.0"}";
}

/// <summary>One client connected to the host: who it is, what it may do, and the peer its frames go through.</summary>
/// <param name="DeviceId">The paired device, or <c>local</c> for Loom on this PC over the named pipe.</param>
/// <param name="Name">The device name shown in the Remote access panel.</param>
/// <param name="Scopes">The device's scopes, <see cref="DeviceScopes.View"/> and maybe <see cref="DeviceScopes.Control"/>.</param>
/// <param name="Peer">The connection's request and push peer; services register handlers on it before it starts.</param>
public sealed record RemoteConnection( string DeviceId, string Name, IReadOnlyList<string> Scopes, RpcPeer Peer )
{
	/// <summary>Gets whether the device may change things: prompts, store operations, workspace writes, stream input.</summary>
	public bool CanControl => Scopes.Contains( DeviceScopes.Control );

	/// <summary>Throws the protocol's <c>scope</c> error when the device may only look.</summary>
	public void RequireControl()
	{
		if ( !CanControl )
			throw new RemoteException( RemoteErrors.Scope, "This device can only view. Allow control in Remote access on the host." );
	}
}

/// <summary>A feature the host offers each connection, such as chats, the workspace or the window stream.</summary>
/// <remarks>
/// The gateway calls <see cref="Register"/> for every new connection, on the host's UI thread, before the peer starts; the
/// service registers its <c>On</c> and <c>OnPush</c> handlers and its own cleanup on <see cref="RpcPeer.Closed"/>. Handlers
/// run on the peer's threads, so a service posts to the UI thread itself before it touches application state.
/// </remarks>
public interface IHostService
{
	/// <summary>Registers this service's frame handlers for one connection.</summary>
	/// <param name="connection">The connection.</param>
	void Register( RemoteConnection connection );
}
