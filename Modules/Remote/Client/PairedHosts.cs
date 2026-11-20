using System.Text.Json;

namespace Lokn.Modules.Remote;

/// <summary>The hosts this PC paired with, kept in <c>remote-hosts.json</c> in the data folder, readable by the current user only (docs/remote.md).</summary>
/// <remarks>
/// Each change reads the list first, so a list that does not parse is never overwritten: it stays as it is until the user fixes or deletes it. The
/// single <c>remote-host.json</c> of the former <c>remote</c> role is imported once, then deleted. Called on the Remote module's UI thread.
/// </remarks>
public static class PairedHosts
{
	/// <summary>Defines the file's format version.</summary>
	private const int FileVersion = 1;

	/// <summary>Writes and reads the file in camelCase.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new( JsonSerializerDefaults.Web ) { WriteIndented = true };

	/// <summary>Gets the list in a data folder, <c>remote-hosts.json</c>.</summary>
	public static string ComputePath( string dataFolder ) => Path.Combine( dataFolder, "remote-hosts.json" );

	/// <summary>Gets the single paired host of the former <c>remote</c> role in a data folder, <c>remote-host.json</c>.</summary>
	public static string ComputeLegacyPath( string dataFolder ) => Path.Combine( dataFolder, "remote-host.json" );

	/// <summary>Reads the list; imports a legacy remote-host.json once (appended with Name "", the file deleted after the list is written).</summary>
	/// <exception cref="InvalidDataException">The list does not parse; it is left untouched and never overwritten this session.</exception>
	/// <exception cref="IOException">The list could not be read or the import written.</exception>
	/// <exception cref="UnauthorizedAccessException">The list could not be read or the import written.</exception>
	public static IReadOnlyList<PairedHost> Read( string dataFolder )
	{
		var hosts = ReadList( ComputePath( dataFolder ) );
		var legacyPath = ComputeLegacyPath( dataFolder );
		if ( !File.Exists( legacyPath ) || ReadLegacy( legacyPath ) is not { } legacy )
			return hosts;

		if ( hosts.All( host => host.HostId != legacy.HostId ) )
		{
			hosts.Add( legacy with
			{
				Name = "",
				Added = legacy.Added == default ? new DateTimeOffset( File.GetLastWriteTimeUtc( legacyPath ), TimeSpan.Zero ) : legacy.Added,
			} );
			Save( dataFolder, hosts );
		}
		File.Delete( legacyPath );
		return hosts;
	}

	/// <summary>Writes the list through a temporary file readable by the current user only.</summary>
	public static void Save( string dataFolder, IReadOnlyList<PairedHost> hosts ) =>
		SecretFile.WriteAllText( ComputePath( dataFolder ), JsonSerializer.Serialize( new HostsDocument { Version = FileVersion, Hosts = [.. hosts] }, JsonOptions ) );

	/// <summary>Adds a host, or replaces the entry with its id, such as after pairing with it again, keeping the name the user gave it and the name the host
	/// last reported unless the new entry names them.</summary>
	/// <exception cref="InvalidDataException">The list does not parse, so it is not overwritten.</exception>
	public static void Upsert( string dataFolder, PairedHost host )
	{
		var hosts = Read( dataFolder ).ToList();
		var index = hosts.FindIndex( entry => entry.HostId == host.HostId );
		if ( index < 0 )
			hosts.Add( host );
		else
		{
			var previous = hosts[index];
			hosts[index] = host with
			{
				Name = host.Name.Length > 0 ? host.Name : previous.Name,
				HostName = host.HostName.Length > 0 ? host.HostName : previous.HostName,
			};
		}
		Save( dataFolder, hosts );
	}

	/// <summary>Removes a host; nothing changes when it is not in the list.</summary>
	/// <exception cref="InvalidDataException">The list does not parse, so it is not overwritten.</exception>
	public static void Remove( string dataFolder, string hostId )
	{
		var hosts = Read( dataFolder ).ToList();
		if ( hosts.RemoveAll( entry => entry.HostId == hostId ) > 0 )
			Save( dataFolder, hosts );
	}

	/// <summary>Saves what a host reported when it connected: the addresses it listens on now, so the next dial reaches it after its address changed without
	/// pairing again, and its name, which shows while it is offline; nothing changes when both are the same or the host is not in the list.</summary>
	/// <exception cref="InvalidDataException">The list does not parse, so it is not overwritten.</exception>
	public static void UpdateReported( string dataFolder, string hostId, IReadOnlyList<string> addrs, string hostName )
	{
		var hosts = Read( dataFolder ).ToList();
		var index = hosts.FindIndex( entry => entry.HostId == hostId );
		if ( index < 0 || (hosts[index].Addrs.SequenceEqual( addrs, StringComparer.Ordinal ) && hosts[index].HostName == hostName) )
			return;

		hosts[index] = hosts[index] with { Addrs = [.. addrs], HostName = hostName };
		Save( dataFolder, hosts );
	}

	/// <summary>Reads the list's entries, or none when the file does not exist.</summary>
	/// <exception cref="InvalidDataException">The file does not parse, is of a newer format, or an entry lacks its id, key or device id.</exception>
	private static List<PairedHost> ReadList( string path )
	{
		if ( !File.Exists( path ) )
			return [];

		HostsDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<HostsDocument>( File.ReadAllText( path ), JsonOptions );
		}
		catch ( JsonException exception )
		{
			throw new InvalidDataException( $"{path} is damaged, so no paired host connects. Fix or delete it, then restart Loom. {exception.Message}", exception );
		}

		if ( document is null || document.Version != FileVersion || document.Hosts is null
			|| document.Hosts.Any( host => host is null || string.IsNullOrEmpty( host.HostId ) || string.IsNullOrEmpty( host.HostKey ) || string.IsNullOrEmpty( host.DeviceId ) ) )
			throw new InvalidDataException( $"{path} is not a list of paired hosts this Loom reads, so no paired host connects. Fix or delete it, then restart Loom." );

		return [.. document.Hosts.Select( host => host with { Addrs = host.Addrs ?? [], Name = host.Name ?? "", HostName = host.HostName ?? "" } )];
	}

	/// <summary>Reads the former <c>remote</c> role's paired host, or null when it is unreadable, which leaves it in place.</summary>
	private static PairedHost? ReadLegacy( string path )
	{
		try
		{
			return JsonSerializer.Deserialize<PairedHost>( File.ReadAllText( path ), JsonOptions ) is { HostId.Length: > 0, HostKey.Length: > 0, DeviceId.Length: > 0 } host
				? host with { Addrs = host.Addrs ?? [] }
				: null;
		}
		catch ( Exception exception ) when ( exception is JsonException or IOException or UnauthorizedAccessException )
		{
			return null;
		}
	}

	/// <summary>The file's shape: its version and the hosts in the order they were added.</summary>
	private sealed record HostsDocument
	{
		/// <summary>Gets the format version.</summary>
		public int Version
		{
			get; init;
		}

		/// <summary>Gets the paired hosts.</summary>
		public List<PairedHost>? Hosts
		{
			get; init;
		}
	}
}
