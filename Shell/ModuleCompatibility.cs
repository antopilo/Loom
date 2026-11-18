using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Lokn;

/// <summary>Checks that a module assembly fits the running application before its modules start.</summary>
/// <remarks>
/// A module DLL names every type and member it uses from Core, Goo, .NET and other modules. Resolving each of those references
/// against the assemblies actually loaded finds a module built against an API that this application does not have, such as an update
/// built after Core changed, before any of its code runs. This is what keeps module updates and the application compatible: a
/// module that passes uses only what exists.
/// </remarks>
internal static class ModuleCompatibility
{
	/// <summary>Stores stand-ins for the type parameters of generic members, which resolving needs as a generic context.</summary>
	private static readonly Type[] _genericContext = Enumerable.Repeat( typeof( object ), 16 ).ToArray();

	/// <summary>Finds the first type or member a loaded module assembly uses that the running assemblies lack.</summary>
	/// <param name="assembly">The assembly, loaded in the context that resolves its references.</param>
	/// <param name="image">The bytes it was loaded from, whose metadata lists its references.</param>
	/// <returns>A readable description of the first missing type or member, or null when everything resolves.</returns>
	internal static string? FindProblem( Assembly assembly, byte[] image )
	{
		try
		{
			// Loading every type also finds an interface member the module does not implement, or a base member it overrides that is gone.
			assembly.GetTypes();
		}
		catch ( ReflectionTypeLoadException error )
		{
			return error.LoaderExceptions.FirstOrDefault( exception => exception is not null )?.Message ?? error.Message;
		}

		using var reader = new PEReader( new MemoryStream( image, writable: false ) );
		var metadata = reader.GetMetadataReader();
		var module = assembly.ManifestModule;
		foreach ( var handle in metadata.TypeReferences )
		{
			try
			{
				module.ResolveType( MetadataTokens.GetToken( handle ) );
			}
			catch ( Exception error ) when ( IsMissing( error ) )
			{
				return error.Message;
			}
		}

		foreach ( var handle in metadata.MemberReferences )
		{
			var token = MetadataTokens.GetToken( handle );
			try
			{
				module.ResolveMember( token );
			}
			catch ( ArgumentException )
			{
				// A member of a generic type used with a type parameter needs a generic context. With object in its place most resolve;
				// one whose constraints refuse object is left to the runtime.
				try
				{
					module.ResolveMember( token, _genericContext, _genericContext );
				}
				catch ( ArgumentException )
				{
				}
				catch ( Exception error ) when ( IsMissing( error ) )
				{
					return error.Message;
				}
			}
			catch ( Exception error ) when ( IsMissing( error ) )
			{
				return error.Message;
			}
		}

		return null;
	}

	/// <summary>Reads the assembly version from a module image, or 0.0 when the bytes are not an assembly.</summary>
	internal static Version ReadVersion( byte[] image )
	{
		try
		{
			using var reader = new PEReader( new MemoryStream( image, writable: false ) );
			return reader.GetMetadataReader().GetAssemblyDefinition().Version;
		}
		catch ( Exception error ) when ( error is BadImageFormatException or InvalidOperationException )
		{
			return new( 0, 0 );
		}
	}

	/// <summary>Tells whether resolving failed because a type, member or assembly is missing or does not match.</summary>
	private static bool IsMissing( Exception error ) => error is MissingMemberException or TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException;
}
