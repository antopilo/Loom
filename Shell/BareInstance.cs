using System.Security.Cryptography;
using System.Text;
using Lokn.Core;

namespace Lokn;

/// <summary>Knows a bare instance, <c>Loom.exe [--host] --bare &lt;folder&gt;</c>: a second, isolated Loom for demos, checks and benchmarks that changes
/// nothing outside its folder (docs/architecture.md, Bare instances).</summary>
/// <remarks>
/// Its data, logs, temporary files and Goo's pipeline cache are in the folder; its app ID, single-instance election, daemon mutex, stop event and pipe
/// carry the folder's hash; it never runs Velopack, so it applies no update and runs no installer hook; and it never writes the user's Run key, the
/// <c>loom</c> and <c>lokn-notification</c> protocols or the Start menu shortcut, which belong to the user's own Loom.
/// </remarks>
internal static class BareInstance
{
	/// <summary>Defines the variable Goo reads its Vulkan pipeline cache folder from.</summary>
	private const string PipelineCacheVariable = "GOO_VK_PIPELINE_CACHE_DIR";

	/// <summary>Reads the bare folder from the command line, the argument after <c>--bare</c>, as a full path; null for the user's own Loom.</summary>
	/// <param name="args">The command line.</param>
	internal static string? Parse( string[] args ) =>
		Array.IndexOf( args, "--bare" ) is var index and >= 0 && index + 1 < args.Length ? Path.GetFullPath( args[index + 1] ) : null;

	/// <summary>Builds the app ID Windows groups the windows under and Goo elects a single instance by: the user's, or one per bare folder, so several
	/// bare instances run side by side with the user's Loom.</summary>
	/// <param name="applicationId">The user's app ID.</param>
	/// <param name="bare">The bare folder, or null.</param>
	internal static string ComputeApplicationId( string applicationId, string? bare ) => bare is null ? applicationId
		: $"{applicationId}.bare.{Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( bare.ToUpperInvariant() ) ) )[..8]}";

	/// <summary>Moves this process into the bare folder before anything reads a path: <see cref="LoknPaths.DataRoot"/>, and with it every data file,
	/// log and temporary file, and Goo's pipeline cache unless the environment names one.</summary>
	/// <param name="bare">The bare folder.</param>
	internal static void Enter( string bare )
	{
		LoknPaths.UseDataRoot( bare );
		if ( string.IsNullOrEmpty( Environment.GetEnvironmentVariable( PipelineCacheVariable ) ) )
			Environment.SetEnvironmentVariable( PipelineCacheVariable, Path.Combine( LoknPaths.DataRoot, "cache", "vulkan" ) );
	}
}
