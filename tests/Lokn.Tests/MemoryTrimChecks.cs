using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks when the daemon trims its memory, and that an aggressive collection gives committed memory back as the trim relies on.</summary>
internal static class MemoryTrimChecks
{
	/// <summary>Runs the checks.</summary>
	internal static void Run()
	{
		const long mb = 1024L * 1024;
		CoreChecks.Check( MemoryTrim.IsDue( 300 * mb, 4 * mb, 0, TimeSpan.FromMinutes( 5 ) ), "Much unused committed memory with no agent working trims." );
		CoreChecks.Check( !MemoryTrim.IsDue( 300 * mb, 4 * mb, 1, TimeSpan.FromMinutes( 5 ) ), "No trim while an agent works." );
		CoreChecks.Check( !MemoryTrim.IsDue( 300 * mb, 4 * mb, 0, TimeSpan.FromSeconds( 30 ) ), "No second trim within the minimum gap." );
		CoreChecks.Check( !MemoryTrim.IsDue( 60 * mb, 10 * mb, 0, TimeSpan.FromMinutes( 5 ) ), "Little unused memory is not worth a trim." );

		// A burst of short-lived allocations, as loading chats makes, leaves the GC with committed memory that only an aggressive collection returns.
		var burst = new List<byte[]>();
		for ( var i = 0; i < 300; i++ )
			burst.Add( new byte[1024 * 1024] );
		burst.Clear();
		GC.Collect();
		var before = GC.GetGCMemoryInfo().TotalCommittedBytes;
		GC.Collect( GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true );
		var after = GC.GetGCMemoryInfo().TotalCommittedBytes;
		CoreChecks.Check( after < before - 100 * mb, $"An aggressive collection returns a burst's committed memory ({before / mb} MB before, {after / mb} MB after)." );
		Console.WriteLine( $"PASS memory trim: trims only with much unused memory, no working agent and a gap since the last; an aggressive collection returned {( before - after ) / mb} MB of a 300 MB burst." );
	}
}
