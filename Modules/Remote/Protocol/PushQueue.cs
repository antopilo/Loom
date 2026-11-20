using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Sends one connection's pushes in order from any thread; a push with a key replaces the same key's push not sent yet, and a connection that
/// falls too far behind, or a push too large to send, closes the connection.</summary>
/// <param name="peer">The connection's peer.</param>
/// <param name="maxPending">The most pushes waiting before the peer counts as stopped reading.</param>
internal sealed class PushQueue( RpcPeer peer, int maxPending )
{
	/// <summary>Holds the pushes not sent yet with their keys; lock it to read or change it and <see cref="_isSending"/>.</summary>
	private readonly List<(JsonObject Push, string? Key)> _pending = [];

	/// <summary>Stores whether a send loop runs.</summary>
	private bool _isSending;

	/// <summary>Queues a push behind the earlier ones, replacing the waiting push of the same key.</summary>
	/// <param name="push">The push.</param>
	/// <param name="key">A key such as a tab id, whose newer push makes an older one waiting pointless; null for none.</param>
	public void Send( JsonObject push, string? key = null )
	{
		if ( peer.IsClosed )
			return;

		bool isStarting;
		lock ( _pending )
		{
			if ( key is not null && _pending.FindIndex( pending => pending.Key == key ) is >= 0 and var index )
			{
				_pending[index] = (push, key);
				return;
			}
			if ( _pending.Count >= maxPending )
			{
				_pending.Clear();
				_ = peer.DisposeAsync();
				return;
			}
			_pending.Add( (push, key) );
			isStarting = !_isSending;
			_isSending = true;
		}
		if ( isStarting )
			_ = DrainAsync();
	}

	/// <summary>Sends the queued pushes in order until none is left.</summary>
	private async Task DrainAsync()
	{
		while ( true )
		{
			JsonObject push;
			lock ( _pending )
			{
				if ( _pending.Count == 0 || peer.IsClosed )
				{
					_pending.Clear();
					_isSending = false;
					return;
				}
				push = _pending[0].Push;
				_pending.RemoveAt( 0 );
			}

			try
			{
				await peer.PushAsync( push ).ConfigureAwait( false );
			}
			catch ( Exception exception ) when ( peer.IsClosed || exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException )
			{
				// The client would miss a change it cannot notice; closing makes it sync again when it reconnects.
				await peer.DisposeAsync().ConfigureAwait( false );
			}
		}
	}
}
