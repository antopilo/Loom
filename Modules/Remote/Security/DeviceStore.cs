using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>A device paired with this host, as <c>devices.json</c> stores it.</summary>
public sealed record PairedDevice
{
	/// <summary>Gets the device's id, a GUID in <c>N</c> form.</summary>
	public required string DeviceId
	{
		get; init;
	}

	/// <summary>Gets the name the device gave.</summary>
	public required string Name
	{
		get; init;
	}

	/// <summary>Gets the device's static public key, base64url SPKI.</summary>
	public required string DeviceKey
	{
		get; init;
	}

	/// <summary>Gets what the device may do: <see cref="DeviceScopes.View"/>, and <see cref="DeviceScopes.Control"/> once raised in the Remote access panel.</summary>
	public required IReadOnlyList<string> Scopes
	{
		get; init;
	}

	/// <summary>Gets when the device paired.</summary>
	public DateTimeOffset Paired
	{
		get; init;
	}
}

/// <summary>The scopes a device can hold.</summary>
public static class DeviceScopes
{
	/// <summary>Lets the device read chats and the store.</summary>
	public const string View = "view";

	/// <summary>Lets the device prompt, change the store, and use the host's workspace and input.</summary>
	public const string Control = "control";

	/// <summary>The scopes a new pairing gets.</summary>
	public static readonly IReadOnlyList<string> Paired = [View];

	/// <summary>The scopes the Remote access panel raises a device to.</summary>
	public static readonly IReadOnlyList<string> Controlling = [View, Control];
}

/// <summary>The devices paired with this host, kept in <c>devices.json</c>. Safe from any thread; events fire on the thread that made the change.</summary>
public sealed class DeviceStore
{
	/// <summary>Writes and reads the file in camelCase.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new( JsonSerializerDefaults.Web ) { WriteIndented = true };

	/// <summary>Holds the devices by id.</summary>
	private readonly Dictionary<string, PairedDevice> _devices = new( StringComparer.Ordinal );

	/// <summary>Stores the file path.</summary>
	private readonly string _path;

	/// <summary>Loads the devices saved at <paramref name="path"/>; a missing or unreadable file means no device is paired.</summary>
	public DeviceStore( string path )
	{
		_path = path;
		try
		{
			if ( File.Exists( path ) && JsonSerializer.Deserialize<PairedDevice[]>( File.ReadAllText( path ), JsonOptions ) is { } devices )
			{
				foreach ( var device in devices )
					_devices[device.DeviceId] = device;
			}
		}
		catch ( JsonException )
		{
		}
	}

	/// <summary>Gets the default file, <c>%LOCALAPPDATA%\Lokn\devices.json</c> (or the data folder's); a bare host keeps its own in its folder.</summary>
	public static string DefaultPath => LoknPaths.Combine( "devices.json" );

	/// <summary>Raised after a device was paired, changed or revoked.</summary>
	public event Action? Changed;

	/// <summary>Raised with a device id after it was revoked, so its connections close.</summary>
	public event Action<string>? Revoked;

	/// <summary>Raised with a device after it newly paired, after <see cref="Changed"/>, on the thread that paired it.</summary>
	public event Action<PairedDevice>? Added;

	/// <summary>Gets the paired devices, oldest first.</summary>
	public IReadOnlyList<PairedDevice> Devices
	{
		get
		{
			lock ( _devices )
				return [.. _devices.Values.OrderBy( device => device.Paired )];
		}
	}

	/// <summary>Finds a paired device, or null when it is unknown or was revoked.</summary>
	public PairedDevice? Find( string deviceId )
	{
		lock ( _devices )
			return _devices.GetValueOrDefault( deviceId );
	}

	/// <summary>Adds a device, or replaces the one with its id, and saves.</summary>
	public void Save( PairedDevice device )
	{
		lock ( _devices )
		{
			_devices[device.DeviceId] = device;
			Write();
		}
		Changed?.Invoke();
	}

	/// <summary>Adds a newly paired device and saves; false, changing nothing, when its id is already paired.</summary>
	public bool TryAdd( PairedDevice device )
	{
		lock ( _devices )
		{
			if ( !_devices.TryAdd( device.DeviceId, device ) )
				return false;
			Write();
		}
		Changed?.Invoke();
		Added?.Invoke( device );
		return true;
	}

	/// <summary>Changes a device's scopes and saves; false when the device is unknown.</summary>
	public bool SetScopes( string deviceId, IReadOnlyList<string> scopes )
	{
		lock ( _devices )
		{
			if ( !_devices.TryGetValue( deviceId, out var device ) )
				return false;
			_devices[deviceId] = device with { Scopes = scopes };
			Write();
		}
		Changed?.Invoke();
		return true;
	}

	/// <summary>Deletes a device and saves, then raises <see cref="Revoked"/> so its connections close; false when the device is unknown.</summary>
	public bool Revoke( string deviceId )
	{
		lock ( _devices )
		{
			if ( !_devices.Remove( deviceId ) )
				return false;
			Write();
		}
		Revoked?.Invoke( deviceId );
		Changed?.Invoke();
		return true;
	}

	/// <summary>Writes every device to the file, readable by the current user only, under the lock.</summary>
	private void Write() =>
		SecretFile.WriteAllText( _path, JsonSerializer.Serialize( _devices.Values.OrderBy( device => device.Paired ).ToArray(), JsonOptions ) );
}
