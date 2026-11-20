namespace Lokn.Modules.Remote;

/// <summary>Bounds what runs at once, such as handshakes or connections, in total and per remote address, so unauthenticated peers cannot pile them up. Safe from any thread.</summary>
/// <param name="total">The most entries at once.</param>
/// <param name="perAddress">The most entries at once from one address; connections without an address count only toward <paramref name="total"/>.</param>
public sealed class ConnectionLimiter( int total, int perAddress )
{
	/// <summary>Holds the running entries by address.</summary>
	private readonly Dictionary<string, int> _byAddress = new( StringComparer.Ordinal );

	/// <summary>Stores the running entries in total; guarded with <see cref="_byAddress"/>.</summary>
	private int _total;

	/// <summary>Gets the entries running now.</summary>
	public int Running
	{
		get
		{
			lock ( _byAddress )
				return _total;
		}
	}

	/// <summary>Counts a new entry in, or refuses it when a limit is reached.</summary>
	/// <param name="address">The peer's address, or null.</param>
	/// <returns>Whether the entry may run; call <see cref="Exit"/> after it when true.</returns>
	public bool TryEnter( string? address )
	{
		lock ( _byAddress )
		{
			if ( _total >= total )
				return false;
			if ( address is not null )
			{
				var count = _byAddress.GetValueOrDefault( address );
				if ( count >= perAddress )
					return false;
				_byAddress[address] = count + 1;
			}
			_total++;
			return true;
		}
	}

	/// <summary>Counts an entry out that <see cref="TryEnter"/> let in.</summary>
	public void Exit( string? address )
	{
		lock ( _byAddress )
		{
			_total--;
			if ( address is not null && _byAddress.TryGetValue( address, out var count ) )
			{
				if ( count <= 1 )
					_byAddress.Remove( address );
				else
					_byAddress[address] = count - 1;
			}
		}
	}
}
