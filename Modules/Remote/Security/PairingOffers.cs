using System.Buffers.Text;
using System.Security.Cryptography;

namespace Lokn.Modules.Remote;

/// <summary>A pairing link the host shows, valid until <paramref name="Expires"/> and for one pairing.</summary>
/// <param name="Link">The link with its secret.</param>
/// <param name="Expires">When the secret stops working.</param>
/// <param name="Control">Whether the device that pairs through it gets control at once, instead of view only until the user allows control.</param>
public sealed record PairingOffer( PairingLink Link, DateTimeOffset Expires, bool Control = false );

/// <summary>The host's live pairing offer: one at a time, each secret works for <see cref="Lifetime"/> and once. Safe from any thread.</summary>
/// <param name="time">The clock, replaceable by checks; the system clock by default.</param>
public sealed class PairingOffers( TimeProvider? time = null )
{
	/// <summary>How long a pairing secret works.</summary>
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes( 5 );

	/// <summary>Holds the offers not yet used or expired; at most one.</summary>
	private readonly List<PairingOffer> _offers = [];

	/// <summary>Raised with the offer a device just paired through, on the handshake's thread-pool thread; post to the UI thread before touching state.</summary>
	public event Action<PairingOffer>? Used;

	/// <summary>Gets the clock.</summary>
	private TimeProvider Time => time ?? TimeProvider.System;

	/// <summary>Creates an offer with a new 32-byte secret for the host and the addresses it listens on, withdrawing every earlier offer.</summary>
	/// <param name="host">The host's keys.</param>
	/// <param name="addrs">The addresses the host listens on.</param>
	/// <param name="relay">The relay's address, or null.</param>
	/// <param name="control">Whether the device that pairs through it gets control at once.</param>
	public PairingOffer Create( RemoteKeys host, IReadOnlyList<string> addrs, string? relay, bool control = false )
	{
		var link = new PairingLink( host.HostId, host.PublicKeyText, Base64Url.EncodeToString( RandomNumberGenerator.GetBytes( 32 ) ), addrs, relay );
		var offer = new PairingOffer( link, Time.GetUtcNow() + Lifetime, control );
		lock ( _offers )
		{
			_offers.Clear();
			_offers.Add( offer );
		}
		return offer;
	}

	/// <summary>Withdraws an offer, such as when the pairing dialog closes.</summary>
	public void Cancel( PairingOffer offer )
	{
		lock ( _offers )
			_offers.Remove( offer );
	}

	/// <summary>Withdraws every offer.</summary>
	public void CancelAll()
	{
		lock ( _offers )
			_offers.Clear();
	}

	/// <summary>Checks a device's pairing MAC against every live offer and uses up the one it matches.</summary>
	/// <param name="hostId">The host's id.</param>
	/// <param name="deviceKey">The device's public key, base64url SPKI, as the device sent it.</param>
	/// <param name="mac">The MAC the device sent.</param>
	/// <returns>The offer the MAC matched, which tells whether the device gets control at once, or null when it matched none.</returns>
	public PairingOffer? TryConsume( string hostId, string deviceKey, string mac )
	{
		byte[] received;
		try
		{
			received = Base64Url.DecodeFromChars( mac );
		}
		catch ( FormatException )
		{
			return null;
		}

		var now = Time.GetUtcNow();
		PairingOffer? used = null;
		lock ( _offers )
		{
			_offers.RemoveAll( offer => offer.Expires <= now );
			foreach ( var offer in _offers )
			{
				var expected = Base64Url.DecodeFromChars( PairingLink.ComputeMac( offer.Link.Secret, hostId, deviceKey ) );
				if ( offer.Link.HostId == hostId && CryptographicOperations.FixedTimeEquals( expected, received ) )
				{
					used = offer;
					break;
				}
			}
			if ( used is not null )
				_offers.Remove( used );
		}
		if ( used is null )
			return null;
		Used?.Invoke( used );
		return used;
	}
}
