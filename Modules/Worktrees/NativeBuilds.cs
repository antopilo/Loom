using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lokn.Modules.Git;

namespace Lokn.Modules.Worktrees;

/// <summary>Tells whether two commits have the same native sources, and whether a checkout's native build is the one recorded for it.</summary>
/// <remarks>
/// A commit's native fingerprint hashes git's object id of each native input path, so two commits share it exactly when
/// no file under those paths differs, the same test as an empty `git diff` over them. A recorded build is trusted only
/// while its output files keep the sizes and write times they had when it was recorded.
/// </remarks>
internal static class NativeBuilds
{
	/// <summary>Defines how many fingerprint characters identify it in the panel.</summary>
	internal const int ShortLength = 12;

	/// <summary>Computes the native fingerprint of each revision, keyed by the revision as given.</summary>
	/// <param name="repository">Any directory of the repository.</param>
	/// <param name="revisions">Commit hashes or full reference names.</param>
	/// <param name="inputs">The native input paths, relative to the repository root.</param>
	/// <param name="cancellationToken">Stops git.</param>
	internal static async Task<Dictionary<string, string>> ComputeFingerprintsAsync( string repository, IReadOnlyCollection<string> revisions, IReadOnlyList<string> inputs, CancellationToken cancellationToken )
	{
		var result = new Dictionary<string, string>( StringComparer.Ordinal );
		var distinct = revisions.Where( revision => revision.Length > 0 ).Distinct( StringComparer.Ordinal ).ToArray();
		if ( distinct.Length == 0 || inputs.Count == 0 )
			return result;

		var paths = inputs.Order( StringComparer.Ordinal ).ToArray();
		var request = new StringBuilder();
		foreach ( var revision in distinct )
		{
			foreach ( var path in paths )
				request.Append( revision ).Append( ':' ).Append( path ).Append( '\n' );
		}

		// One process answers every revision and path: each request line gets one "<id> <type> <size>" or "<name> missing" line.
		var output = await GitProcess.RunAsync( GitWorktrees.Workspace, repository, request.ToString(), cancellationToken, "cat-file", "--batch-check" ).ConfigureAwait( false );
		var lines = output.ReplaceLineEndings( "\n" ).Split( '\n', StringSplitOptions.RemoveEmptyEntries );
		if ( lines.Length != distinct.Length * paths.Length )
			throw new InvalidOperationException( "git cat-file returned an unexpected number of lines while reading native sources." );

		for ( var index = 0; index < distinct.Length; index++ )
		{
			var text = new StringBuilder();
			for ( var path = 0; path < paths.Length; path++ )
			{
				var line = lines[index * paths.Length + path];
				var id = line.EndsWith( " missing", StringComparison.Ordinal ) || line.EndsWith( " ambiguous", StringComparison.Ordinal ) ? "missing" : line.Split( ' ' )[0];
				text.Append( paths[path] ).Append( '\0' ).Append( id ).Append( '\n' );
			}

			result[distinct[index]] = Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( text.ToString() ) ) );
		}

		return result;
	}

	/// <summary>Reports whether a checkout has uncommitted or untracked changes under its native input paths.</summary>
	internal static async Task<bool> HasChangesAsync( string checkout, IReadOnlyList<string> inputs, CancellationToken cancellationToken )
	{
		var output = await GitProcess.RunAsync( GitWorktrees.Workspace, checkout, cancellationToken, ["status", "--porcelain", "--", .. inputs] ).ConfigureAwait( false );
		return output.Trim().Length > 0;
	}

	/// <summary>Lists the files of a native output folder in a stable order; links inside it are not followed.</summary>
	internal static NativeFile[] ReadFiles( string output )
	{
		if ( !Directory.Exists( output ) )
			return [];

		var options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint,
		};
		return new DirectoryInfo( output )
			.EnumerateFiles( "*", options )
			.Select( file => new NativeFile( Path.GetRelativePath( output, file.FullName ).Replace( '\\', '/' ), file.Length, file.LastWriteTimeUtc.Ticks ) )
			.OrderBy( file => file.Path, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	/// <summary>Reports whether a checkout's native output folder still holds exactly the recorded build.</summary>
	internal static bool Matches( NativeBuildRecord record, string checkout, RepositorySetup setup )
	{
		return record.Output == setup.NativeOutput && record.Files.Length > 0
			&& ReadFiles( Path.Combine( checkout, setup.NativeOutput ) ).SequenceEqual( record.Files );
	}

	/// <summary>Checks with the checkout's HEAD reflog that its native sources did not change between the last native build and now.</summary>
	/// <param name="checkout">The checkout whose build is being recorded.</param>
	/// <param name="inputs">The native input paths.</param>
	/// <param name="fingerprint">The fingerprint of the checkout's HEAD.</param>
	/// <param name="files">The output folder's files; the newest write is taken as the build's time.</param>
	/// <param name="cancellationToken">Stops git.</param>
	/// <returns>Null when every HEAD since the build had these native sources, otherwise why the build cannot be recorded.</returns>
	/// <remarks>Uncommitted native edits at build time leave no trace in the reflog, so a build is only recorded from clean native sources.</remarks>
	internal static async Task<string?> VerifyBuiltFromAsync( string checkout, IReadOnlyList<string> inputs, string fingerprint, IReadOnlyList<NativeFile> files, CancellationToken cancellationToken )
	{
		if ( files.Count == 0 )
			return "The native output folder is empty. Build native first.";

		var built = new DateTimeOffset( files.Max( file => file.WriteTicks ), TimeSpan.Zero );
		var reflog = ParseReflog( await GitProcess.RunAsync( GitWorktrees.Workspace, checkout, cancellationToken, "log", "-g", "--date=unix", "--format=%H %gd", "HEAD" ).ConfigureAwait( false ) );

		// The entries after the build, and the last one before it: the commit HEAD was on when it built. Reflog times have
		// whole seconds, so an entry in the build's own second counts as after it; the one before must be strictly earlier.
		var second = DateTimeOffset.FromUnixTimeSeconds( built.ToUnixTimeSeconds() );
		var after = reflog.TakeWhile( entry => entry.Time >= second ).ToList();
		if ( reflog.Count == after.Count )
			return $"The HEAD reflog does not reach back to before the last native build ({built.ToLocalTime():g}), so Lokn cannot tell which sources it was built from. Build native again, then record it.";

		var relevant = after.Append( reflog[after.Count] ).ToArray();
		var fingerprints = await ComputeFingerprintsAsync( checkout, relevant.Select( entry => entry.Commit ).ToArray(), inputs, cancellationToken ).ConfigureAwait( false );
		var changed = relevant.FirstOrDefault( entry => fingerprints[entry.Commit] != fingerprint );
		return changed is null
			? null
			: $"The native sources changed after the last native build ({built.ToLocalTime():g}): HEAD was at {changed.Commit[..10]} on {changed.Time.ToLocalTime():g}. Build native again, then record it.";
	}

	/// <summary>Parses `git log -g --date=unix --format="%H %gd"` lines, newest first.</summary>
	internal static IReadOnlyList<ReflogEntry> ParseReflog( string output )
	{
		var entries = new List<ReflogEntry>();
		foreach ( var line in output.ReplaceLineEndings( "\n" ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var space = line.IndexOf( ' ' );
			var open = line.IndexOf( "@{", StringComparison.Ordinal );
			var close = line.LastIndexOf( '}' );
			if ( space < 0 || open < 0 || close < open
				|| !long.TryParse( line.AsSpan( open + 2, close - open - 2 ), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds ) )
				continue;

			entries.Add( new( line[..space], DateTimeOffset.FromUnixTimeSeconds( seconds ) ) );
		}

		return entries;
	}

	/// <summary>Describes one HEAD reflog entry.</summary>
	/// <param name="Commit">The commit HEAD moved to.</param>
	/// <param name="Time">When HEAD moved.</param>
	internal sealed record ReflogEntry( string Commit, DateTimeOffset Time );
}
