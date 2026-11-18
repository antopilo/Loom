using System.Security.Cryptography;
using Lokn.Core;

namespace Lokn;

/// <summary>Downloads module updates that fit the running Loom and stages them until the user applies them or Loom restarts.</summary>
/// <remarks>
/// For each loaded module it takes the newest published version above the one running, checks the download's hash, and asks
/// <see cref="ModuleHost.Check"/> whether it fits beside the running modules. A version that does not fit is remembered and the next
/// older one is tried, so an older Loom still gets the newest module that works with it. <see cref="ModuleHost"/> checks again when the
/// update loads, because the application may have changed in between.
/// </remarks>
/// <param name="host">The module host, which knows the running versions and checks candidates.</param>
internal sealed class ModuleUpdater( ModuleHost host )
{
	/// <summary>Defines how many rounds of older versions are tried after refusals.</summary>
	private const int MaximumRounds = 3;

	/// <summary>Stores the versions refused this session as <c>name@version</c>, so they are not downloaded again.</summary>
	private readonly HashSet<string> _refused = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Downloads and stages the module updates that fit; runs off the UI thread.</summary>
	/// <param name="feed">The feed to read.</param>
	/// <param name="context">The Updates module's context, which runs host calls on the UI thread.</param>
	/// <param name="cancellationToken">Cancels the downloads.</param>
	/// <returns>The staged releases, waiting to be applied; empty when every module is up to date.</returns>
	internal async Task<IReadOnlyList<ModuleRelease>> StageAsync( UpdateFeed feed, ModuleContext context, CancellationToken cancellationToken )
	{
		if ( host.StagedDirectory is not { } staged )
			return [];

		var index = await feed.ReadIndexAsync( cancellationToken ).ConfigureAwait( false );
		if ( index.Modules.Count == 0 )
			return [];

		// A staged update counts as the running version, so it is not downloaded twice; it is checked again with the new candidates.
		var current = new Dictionary<string, Version>( await RunAsync( context, () => host.Versions ).ConfigureAwait( false ), StringComparer.OrdinalIgnoreCase );
		var accepted = new Dictionary<string, (ModuleRelease Release, byte[] Image)>( StringComparer.OrdinalIgnoreCase );
		Directory.CreateDirectory( staged );
		foreach ( var file in Directory.GetFiles( staged, "*.dll" ) )
		{
			var name = Path.GetFileNameWithoutExtension( file );
			var image = await File.ReadAllBytesAsync( file, cancellationToken ).ConfigureAwait( false );
			var version = ModuleCompatibility.ReadVersion( image );
			// An application update can bring a module past what was staged for it.
			if ( !current.TryGetValue( name, out var running ) || version <= running )
				continue;

			current[name] = version;
			accepted[name] = (new( name, version.ToString( 3 ), Path.GetFileName( file ), "", null ), image);
		}

		for ( var round = 0; round < MaximumRounds; round++ )
		{
			var candidates = new List<(ModuleRelease Release, byte[] Image)>();
			foreach ( var release in FindNewest( index, current ) )
				candidates.Add( (release, await DownloadAsync( feed, release, cancellationToken ).ConfigureAwait( false )) );
			if ( candidates.Count == 0 )
				break;

			// Every candidate is checked with the ones already accepted, since an update can need another one.
			foreach ( var candidate in candidates )
				accepted[candidate.Release.Name] = candidate;
			var refused = await RunAsync( context, () => host.Check( [.. accepted.Values.Select( item => (item.Release.Name, item.Image) )] ) ).ConfigureAwait( false );
			foreach ( var (name, _) in refused )
			{
				if ( accepted.Remove( name, out var item ) )
					_refused.Add( $"{name}@{item.Release.Version}" );
			}

			foreach ( var (name, item) in accepted )
				current[name] = ParseVersion( item.Release.Version );
			if ( refused.Count == 0 )
				break;
		}

		// Each staged file is written whole under a temporary name first, so a crash never leaves half a DLL to load.
		foreach ( var file in Directory.GetFiles( staged, "*.dll" ).Where( file => !accepted.ContainsKey( Path.GetFileNameWithoutExtension( file ) ) ) )
			File.Delete( file );
		foreach ( var (name, item) in accepted )
		{
			var target = Path.Combine( staged, name + ".dll" );
			if ( File.Exists( target ) && item.Release.Sha256.Length == 0 )
				continue;

			await File.WriteAllBytesAsync( target + ".partial", item.Image, cancellationToken ).ConfigureAwait( false );
			File.Move( target + ".partial", target, overwrite: true );
		}

		return [.. accepted.Values.Select( item => item.Release )];
	}

	/// <summary>Picks, per loaded module, the newest published version above the current one that was not refused.</summary>
	private IEnumerable<ModuleRelease> FindNewest( ModuleIndex index, IReadOnlyDictionary<string, Version> current )
	{
		return index.Modules
			.Where( release => current.TryGetValue( release.Name, out var running ) && ParseVersion( release.Version ) > running )
			.Where( release => !_refused.Contains( $"{release.Name}@{release.Version}" ) )
			.GroupBy( release => release.Name, StringComparer.OrdinalIgnoreCase )
			.Select( group => group.MaxBy( release => ParseVersion( release.Version ) )! );
	}

	/// <summary>Reads a published version with all four parts, as an assembly version has them, so 1.4.0 equals 1.4.0.0; a malformed one reads as 0.0.0.0.</summary>
	private static Version ParseVersion( string value )
	{
		return System.Version.TryParse( value, out var version )
			? new( version.Major, version.Minor, Math.Max( version.Build, 0 ), Math.Max( version.Revision, 0 ) )
			: new( 0, 0, 0, 0 );
	}

	/// <summary>Downloads one module DLL and checks it against the hash the index publishes.</summary>
	/// <exception cref="InvalidDataException">The download does not match the index.</exception>
	private static async Task<byte[]> DownloadAsync( UpdateFeed feed, ModuleRelease release, CancellationToken cancellationToken )
	{
		var image = await feed.ReadModuleFile( release.File, cancellationToken ).ConfigureAwait( false );
		if ( !string.Equals( Convert.ToHexString( SHA256.HashData( image ) ), release.Sha256, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( $"The download of {release.File} does not match the update feed." );

		return image;
	}

	/// <summary>Runs work on the UI thread and returns its result.</summary>
	/// <exception cref="OperationCanceledException">The module stopped before the work ran.</exception>
	private static Task<T> RunAsync<T>( ModuleContext context, Func<T> work )
	{
		var done = new TaskCompletionSource<T>( TaskCreationOptions.RunContinuationsAsynchronously );
		var isPosted = context.Post( () =>
		{
			try
			{
				done.SetResult( work() );
			}
			catch ( Exception error )
			{
				done.SetException( error );
			}
		} );
		if ( !isPosted )
			done.TrySetCanceled();

		return done.Task;
	}
}
