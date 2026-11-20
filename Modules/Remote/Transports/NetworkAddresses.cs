using System.Net;
using System.Net.Sockets;

namespace Lokn.Modules.Remote;

/// <summary>Classifies IP addresses, for outgoing connections that must reach only the public internet or only private networks.</summary>
public static class NetworkAddresses
{
	/// <summary>Determines whether an address is on the public internet: not loopback, unspecified, private (RFC 1918, unique local), link-local,
	/// shared (100.64/10, CGNAT and Tailscale), multicast or reserved, also when an IPv6 address embeds such an IPv4 one.</summary>
	public static bool IsPublic( IPAddress address )
	{
		if ( address.IsIPv4MappedToIPv6 )
			address = address.MapToIPv4();
		if ( address.AddressFamily == AddressFamily.InterNetwork )
		{
			var b = address.GetAddressBytes();
			return !(b[0] is 0 or 10 or 127 || b[0] >= 224
				|| b[0] == 100 && (b[1] & 0xC0) == 64
				|| b[0] == 169 && b[1] == 254
				|| b[0] == 172 && (b[1] & 0xF0) == 16
				|| b[0] == 192 && b[1] == 168
				|| b[0] == 192 && b[1] == 0 && b[2] is 0 or 2
				|| b[0] == 198 && (b[1] & 0xFE) == 18
				|| b[0] == 198 && b[1] == 51 && b[2] == 100
				|| b[0] == 203 && b[1] == 0 && b[2] == 113);
		}
		if ( address.AddressFamily != AddressFamily.InterNetworkV6 )
			return false;
		if ( IPAddress.IsLoopback( address ) || address.Equals( IPAddress.IPv6Any ) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
			|| address.IsIPv6UniqueLocal || address.IsIPv6Teredo )
			return false;
		var bytes = address.GetAddressBytes();
		// 64:ff9b::/96 (NAT64) and 2002::/16 (6to4) carry an IPv4 address, which must be public too.
		if ( bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B )
			return IsPublic( new IPAddress( bytes.AsSpan( 12, 4 ) ) );
		if ( bytes[0] == 0x20 && bytes[1] == 0x02 )
			return IsPublic( new IPAddress( bytes.AsSpan( 2, 4 ) ) );
		// Documentation (2001:db8::/32) and the rest of ::/8 are not on the internet.
		return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) && bytes[0] != 0;
	}

	/// <summary>Determines whether an address is on this PC or a private network: loopback, private (RFC 1918, unique local), link-local or shared
	/// (100.64/10, CGNAT and Tailscale), also as an IPv4-mapped IPv6 address. Documentation, reserved and multicast addresses are neither public nor private.</summary>
	public static bool IsPrivate( IPAddress address )
	{
		if ( address.IsIPv4MappedToIPv6 )
			address = address.MapToIPv4();
		if ( address.AddressFamily == AddressFamily.InterNetwork )
		{
			var b = address.GetAddressBytes();
			return b[0] is 10 or 127
				|| b[0] == 100 && (b[1] & 0xC0) == 64
				|| b[0] == 169 && b[1] == 254
				|| b[0] == 172 && (b[1] & 0xF0) == 16
				|| b[0] == 192 && b[1] == 168;
		}
		return address.AddressFamily == AddressFamily.InterNetworkV6
			&& (IPAddress.IsLoopback( address ) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal);
	}
}
