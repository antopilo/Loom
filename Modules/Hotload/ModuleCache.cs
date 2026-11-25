using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Lokn.Core;

namespace Lokn.Modules.Hotload;

/// <summary>Loads source modules, reusing the assembly built for the same sources by an earlier run instead of compiling again.</summary>
/// <remarks>
/// A build is keyed by the module's name and sources and by every assembly the application can reference, so a changed
/// source file, a rebuilt Lokn or Goo, or another .NET runtime compiles afresh. Only the newest build of each module
/// directory is kept. This class never touches Roslyn, so a run that finds every build cached does not load the compiler.
/// </remarks>
internal static class ModuleCache
{
	/// <summary>Defines the cache layout version, part of every key.</summary>
	private const int FormatVersion = 1;

	/// <summary>Stores the fingerprint of the assemblies compilations reference, computed once per process.</summary>
	private static readonly Lazy<byte[]> _references = new( ComputeReferences );

	/// <summary>Gets the assembly name a module's build carries.</summary>
	internal static string BuildAssemblyName( string name ) => $"Lokn.Hotload.{name}";

	/// <summary>Creates a module from its sources, from the cached build when there is one, else compiling and caching it.</summary>
	/// <param name="name">The module's name, its directory's name.</param>
	/// <param name="directory">The module directory, whose older builds a new one replaces.</param>
	/// <param name="sources">The module's sources.</param>
	/// <param name="cacheDirectory">Where builds are kept, or null to always compile.</param>
	/// <exception cref="InvalidDataException">The sources do not compile, or do not hold exactly one module.</exception>
	/// <exception cref="PlatformNotSupportedException">The module's assembly supports only other operating systems.</exception>
	internal static ILoknModule Load( string name, string directory, IReadOnlyList<SourceFile> sources, string? cacheDirectory )
	{
		if ( cacheDirectory is null )
		{
			var (image, symbols) = ModuleCompiler.Emit( name, sources );
			return Create( LoadType( name, image, symbols ) );
		}

		var scope = ComputeHash( Encoding.UTF8.GetBytes( Path.GetFullPath( directory ).ToUpperInvariant() ) )[..16];
		var stem = Path.Combine( cacheDirectory, $"{scope}-{ComputeKey( name, sources )}" );
		if ( TryLoadCached( name, stem ) is { } cached )
			return Create( cached );

		var built = ModuleCompiler.Emit( name, sources );
		Type type;
		try
		{
			type = LoadType( name, built.Image, built.Symbols );
		}
		catch ( PlatformNotSupportedException )
		{
			Store( cacheDirectory, scope, stem, built.Image, built.Symbols );
			throw;
		}
		Store( cacheDirectory, scope, stem, built.Image, built.Symbols );
		return Create( type );
	}

	/// <summary>Loads a build into its own collectible context and finds its single module type.</summary>
	/// <remarks>A collectible context lets the GC reclaim a replaced implementation once nothing references it.
	/// Lokn.Core and Goo resolve from the default context, so their types stay shared with the shell.</remarks>
	private static Type LoadType( string name, byte[] image, byte[] symbols )
	{
		var context = new AssemblyLoadContext( BuildAssemblyName( name ), isCollectible: true );
		var assembly = context.LoadFromStream( new MemoryStream( image ), new MemoryStream( symbols ) );
		if ( OperatingSystem.IsLinux() )
		{
			var platforms = assembly.GetCustomAttributes<SupportedOSPlatformAttribute>().Select( attribute => attribute.PlatformName ).ToArray();
			if ( platforms.Length > 0 && !platforms.Any( platform => OperatingSystem.IsOSPlatform( new string( [.. platform.TakeWhile( char.IsLetter )] ) ) ) )
			{
				context.Unload();
				throw new PlatformNotSupportedException( $"This module supports only {string.Join( ", ", platforms )}." );
			}
		}

		var types = assembly.GetTypes()
			.Where( type => !type.IsAbstract && typeof( ILoknModule ).IsAssignableFrom( type ) )
			.ToArray();
		if ( types.Length != 1 )
			throw new InvalidDataException( $"Expected one ILoknModule implementation, found {types.Length}." );

		return types[0];
	}

	/// <summary>Creates the module instance through its parameterless constructor.</summary>
	private static ILoknModule Create( Type type ) => (ILoknModule)Activator.CreateInstance( type )!;

	/// <summary>Loads the cached build of a key, or null when there is none or it cannot be loaded, so the sources compile again.</summary>
	private static Type? TryLoadCached( string name, string stem )
	{
		try
		{
			return File.Exists( stem + ".dll" ) && File.Exists( stem + ".pdb" )
				? LoadType( name, File.ReadAllBytes( stem + ".dll" ), File.ReadAllBytes( stem + ".pdb" ) )
				: null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException
			or System.Reflection.ReflectionTypeLoadException or TypeLoadException )
		{
			return null;
		}
	}

	/// <summary>Keeps a new build, replacing the directory's older ones; a failure only costs a compile next time.</summary>
	private static void Store( string cacheDirectory, string scope, string stem, byte[] image, byte[] symbols )
	{
		try
		{
			Directory.CreateDirectory( cacheDirectory );
			foreach ( var stale in Directory.EnumerateFiles( cacheDirectory, scope + "-*" ) )
				File.Delete( stale );

			// Symbols go first: a build is only used once its image exists.
			File.WriteAllBytes( stem + ".pdb.tmp", symbols );
			File.Move( stem + ".pdb.tmp", stem + ".pdb", overwrite: true );
			File.WriteAllBytes( stem + ".dll.tmp", image );
			File.Move( stem + ".dll.tmp", stem + ".dll", overwrite: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
		}
	}

	/// <summary>Computes the key of a module's build from its name, its sources and the referenced assemblies.</summary>
	private static string ComputeKey( string name, IReadOnlyList<SourceFile> sources )
	{
		var text = new StringBuilder().Append( FormatVersion ).Append( '\0' ).Append( name ).Append( '\0' );
		foreach ( var source in sources )
			text.Append( source.FilePath ).Append( '\0' ).Append( source.Text ).Append( '\0' );
		return ComputeHash( [.. Encoding.UTF8.GetBytes( text.ToString() ), .. _references.Value] )[..32];
	}

	/// <summary>Fingerprints every assembly compilations reference by path, size and last write, and the runtime version.</summary>
	private static byte[] ComputeReferences()
	{
		var text = new StringBuilder( Environment.Version.ToString() );
		foreach ( var path in ((string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )!).Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries ).Order( StringComparer.OrdinalIgnoreCase ) )
		{
			var file = new FileInfo( path );
			text.Append( '\0' ).Append( path ).Append( '|' ).Append( file.Exists ? file.Length : -1 ).Append( '|' ).Append( file.Exists ? file.LastWriteTimeUtc.Ticks : 0 );
		}
		return Encoding.UTF8.GetBytes( text.ToString() );
	}

	/// <summary>Hashes bytes to lowercase hexadecimal.</summary>
	private static string ComputeHash( byte[] data ) => Convert.ToHexStringLower( SHA256.HashData( data ) );
}
