using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lokn.Core;
using Lokn.Modules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lokn.Tests;

/// <summary>Exercises module updates: version selection, the compatibility gate, fallback, staging, and downloading from a feed.</summary>
internal static class UpdateChecks
{
	/// <summary>Defines how long a check waits for the file watcher or posted work, in seconds.</summary>
	private const int WaitSeconds = 20;

	/// <summary>Defines a stand-in for a newer Lokn.Core: a higher assembly version, the members the probe uses, and one member the real Core lacks.</summary>
	private const string NewerCoreSource = """
		[assembly: System.Reflection.AssemblyVersion( "99.0.0.0" )]
		namespace Lokn.Core
		{
			public sealed class ModuleBuilder { }
			public interface ILoknModule
			{
				string Id { get; }
				string Title { get; }
				void Register( ModuleBuilder builder );
			}
			public sealed class LoknApplication
			{
				public string? LastError => null;
				public void NotInThisCore() { }
			}
		}
		""";

	/// <summary>Defines the probe module; {0} is its version and {1} the body of a method that is compiled but never run.</summary>
	private const string ProbeSource = """
		[assembly: System.Reflection.AssemblyVersion( "{0}" )]
		public sealed class ProbeModule : Lokn.Core.ILoknModule
		{{
			public string Id => "probe";
			public string Title => "Probe {0}";
			public void Register( Lokn.Core.ModuleBuilder builder ) {{ }}
			public static void Unused( Lokn.Core.LoknApplication app ) {{ {1} }}
		}}
		""";

	/// <summary>Runs the update checks without opening a native window.</summary>
	internal static void Run()
	{
		CheckCompatibility();
		CheckSelection();
		CheckFeed();
		Console.WriteLine( "PASS module compatibility gate, version-independent binding to Core, newest-fitting update selection, refusal with fallback to the bundled module, staged updates, and downloading updates from a feed." );
	}

	/// <summary>Verifies the gate passes a real module and reads module versions; refusals are checked through the host, which binds Core by name.</summary>
	private static void CheckCompatibility()
	{
		var welcome = typeof( WelcomeModule ).Assembly;
		CoreChecks.Check( ModuleCompatibility.FindProblem( welcome, File.ReadAllBytes( welcome.Location ) ) is null, "A module built with this Core passes the compatibility check." );
		CoreChecks.Check( ModuleCompatibility.ReadVersion( BuildProbe( "1.2.3.0", "", isNewerCore: false ) ) == new Version( 1, 2, 3, 0 ) && ModuleCompatibility.ReadVersion( "not an assembly"u8.ToArray() ) == new Version( 0, 0 ),
			"A module's version is read from its image, and bytes that are no assembly read as 0.0." );
	}

	/// <summary>Verifies the host loads the newest update that fits, falls back when one does not, and applies staged updates.</summary>
	private static void CheckSelection()
	{
		var root = CreateRoot();
		var bundled = Path.Combine( root, "modules" );
		var updates = Path.Combine( root, "module-updates" );
		var log = Path.Combine( root, "modules.log" );
		Directory.CreateDirectory( bundled );
		Directory.CreateDirectory( Path.Combine( updates, "staged" ) );
		File.WriteAllBytes( Path.Combine( bundled, "Lokn.Modules.Probe.dll" ), BuildProbe( "1.0.0.0", "", isNewerCore: false ) );
		// Built against a Core with a higher version: it uses only members this Core has, so it must load.
		File.WriteAllBytes( Path.Combine( updates, "Lokn.Modules.Probe.dll" ), BuildProbe( "1.1.0.0", "_ = app.LastError;", isNewerCore: true ) );

		var (app, queued) = CreateApp();
		var host = new ModuleHost( app, bundled, updates, log, new ModuleStates( Path.Combine( root, "modules.json" ), isOffByDefault: false ) );
		var refusals = new List<string>();
		host.Refused += ( name, version, _ ) => refusals.Add( $"{name} {version}" );
		host.Start();
		CoreChecks.Check( ReadTitle( app ) == "Probe 1.1.0.0" && host.Versions["Lokn.Modules.Probe"] == new Version( 1, 1, 0, 0 ),
			"An update newer than the bundled module loads, though it was built against a Core of another version." );

		var update = Path.Combine( updates, "Lokn.Modules.Probe.dll" );
		File.WriteAllBytes( update, BuildProbe( "1.2.0.0", "app.NotInThisCore();", isNewerCore: true ) );
		File.SetLastWriteTimeUtc( update, DateTime.UtcNow.AddMinutes( 1 ) );
		RunNext( queued );
		CoreChecks.Check( ReadTitle( app ) == "Probe 1.0.0.0" && !File.Exists( update ) && refusals.SequenceEqual( ["Lokn.Modules.Probe 1.2.0.0"] ),
			"An update that does not fit is refused and deleted, and the bundled module loads instead." );
		CoreChecks.Check( File.ReadAllText( log ).Contains( "SKIPPED Lokn.Modules.Probe 1.2.0.0", StringComparison.Ordinal ), "A refused update is recorded for scripts with its version." );

		var fits = BuildProbe( "1.3.0.0", "_ = app.LastError;", isNewerCore: true );
		var refused = host.Check( [("Lokn.Modules.Probe", fits), ("Lokn.Modules.Other", BuildProbe( "1.0.0.0", "app.NotInThisCore();", isNewerCore: true ))] );
		CoreChecks.Check( refused.Count == 1 && refused["Lokn.Modules.Other"].Contains( "NotInThisCore", StringComparison.Ordinal ) && ReadTitle( app ) == "Probe 1.0.0.0",
			"Checking candidates refuses only the one that does not fit and leaves the running module alone." );

		File.WriteAllBytes( Path.Combine( updates, "staged", "Lokn.Modules.Probe.dll" ), fits );
		CoreChecks.Check( host.ApplyStaged() == 1, "Applying moves the staged update into use." );
		RunNext( queued );
		CoreChecks.Check( ReadTitle( app ) == "Probe 1.3.0.0", "An applied update reloads its module." );

		host.Stop();
		app.Shutdown();
		Directory.Delete( root, recursive: true );
	}

	/// <summary>Verifies the updater downloads the newest version that fits from a folder feed, skipping a newer one that does not, and refuses a corrupt download.</summary>
	private static void CheckFeed()
	{
		var root = CreateRoot();
		var bundled = Path.Combine( root, "modules" );
		var updates = Path.Combine( root, "module-updates" );
		var feedModules = Path.Combine( root, "feed", "modules" );
		Directory.CreateDirectory( bundled );
		Directory.CreateDirectory( feedModules );
		File.WriteAllBytes( Path.Combine( bundled, "Lokn.Modules.Probe.dll" ), BuildProbe( "1.0.0.0", "", isNewerCore: false ) );
		ModuleRelease Publish( string version, byte[] image )
		{
			var file = $"Lokn.Modules.Probe-{version}.dll";
			File.WriteAllBytes( Path.Combine( feedModules, file ), image );
			return new( "Lokn.Modules.Probe", version, file, Convert.ToHexString( SHA256.HashData( image ) ), "0.1.0" );
		}

		ModuleRelease[] releases = [Publish( "1.1.0", BuildProbe( "1.1.0.0", "", isNewerCore: false ) ), Publish( "1.2.0", BuildProbe( "1.2.0.0", "app.NotInThisCore();", isNewerCore: true ) )];
		File.WriteAllText( Path.Combine( feedModules, UpdateFeed.IndexFile ), JsonSerializer.Serialize( new ModuleIndex( releases ), ModuleIndex.JsonOptions ) );

		var (app, queued) = CreateApp();
		var host = new ModuleHost( app, bundled, updates, Path.Combine( root, "modules.log" ), new ModuleStates( Path.Combine( root, "modules.json" ), isOffByDefault: false ) );
		host.Start();
		var context = new ModuleContext( app, CancellationToken.None );
		var feed = UpdateFeed.Parse( Path.Combine( root, "feed" ) )!;
		var updater = new ModuleUpdater( host );

		var staged = Pump( queued, updater.StageAsync( feed, context, CancellationToken.None ) );
		CoreChecks.Check( staged.Select( release => release.Version ).SequenceEqual( ["1.1.0"] ) && File.Exists( Path.Combine( updates, "staged", "Lokn.Modules.Probe.dll" ) ),
			"The newest published version that fits is staged; a newer one that does not fit is skipped." );
		CoreChecks.Check( ReadTitle( app ) == "Probe 1.0.0.0", "A staged update waits until it is applied." );

		File.WriteAllText( Path.Combine( feedModules, UpdateFeed.IndexFile ), JsonSerializer.Serialize( new ModuleIndex( [releases[0] with { Version = "1.4.0", Sha256 = "00" }] ), ModuleIndex.JsonOptions ) );
		var isRefused = false;
		try
		{
			Pump( queued, new ModuleUpdater( host ).StageAsync( feed, context, CancellationToken.None ) );
		}
		catch ( InvalidDataException )
		{
			isRefused = true;
		}

		CoreChecks.Check( isRefused, "A download that does not match the published hash is refused." );
		host.Stop();
		app.Shutdown();
		Directory.Delete( root, recursive: true );
	}

	/// <summary>Compiles the probe module against the real Core, or against a stand-in for a newer one.</summary>
	private static byte[] BuildProbe( string version, string unusedBody, bool isNewerCore )
	{
		IEnumerable<MetadataReference> references = ReadPlatformReferences();
		if ( isNewerCore )
		{
			var core = Compile( "Lokn.Core", NewerCoreSource, ReadPlatformReferences().Where( reference => !IsCore( reference ) ) );
			references = references.Where( reference => !IsCore( reference ) ).Append( MetadataReference.CreateFromImage( core ) );
		}

		return Compile( "Lokn.Modules.Probe", string.Format( ProbeSource, version, unusedBody ), references );
	}

	/// <summary>Compiles one source file into an assembly image.</summary>
	private static byte[] Compile( string name, string source, IEnumerable<MetadataReference> references )
	{
		var compilation = CSharpCompilation.Create( name, [CSharpSyntaxTree.ParseText( source )], references,
			new( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable ) );
		using var image = new MemoryStream();
		var result = compilation.Emit( image );
		if ( !result.Success )
			throw new InvalidOperationException( string.Join( Environment.NewLine, result.Diagnostics ) );

		return image.ToArray();
	}

	/// <summary>Lists every assembly the test process can load as a compile reference.</summary>
	private static IEnumerable<MetadataReference> ReadPlatformReferences()
	{
		return ((string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )!)
			.Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries )
			.Select( path => (MetadataReference)MetadataReference.CreateFromFile( path ) );
	}

	/// <summary>Tells whether a reference is the real Lokn.Core.</summary>
	private static bool IsCore( MetadataReference reference ) => reference is PortableExecutableReference { FilePath: { } path } && Path.GetFileName( path ) == "Lokn.Core.dll";

	/// <summary>Creates an application whose posted work queues until a check runs it.</summary>
	private static (LoknApplication App, List<Action> Queued) CreateApp()
	{
		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );

			return true;
		} );
		return (app, queued);
	}

	/// <summary>Creates an empty temporary folder.</summary>
	private static string CreateRoot()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( root );
		return root;
	}

	/// <summary>Reads the probe module's title, which carries the version that loaded.</summary>
	private static string? ReadTitle( LoknApplication app ) => app.AvailableModules.SingleOrDefault( module => module.Id == "probe" )?.Title;

	/// <summary>Runs the next posted action, waiting for the file watcher to post it.</summary>
	private static void RunNext( List<Action> queued )
	{
		var clock = Stopwatch.StartNew();
		while ( clock.Elapsed.TotalSeconds < WaitSeconds )
		{
			if ( TakeNext( queued ) is { } action )
			{
				action();
				return;
			}

			Thread.Sleep( 20 );
		}

		throw new TimeoutException( "Nothing was posted to the UI thread." );
	}

	/// <summary>Runs posted actions until a task completes, as the UI thread would, and returns its result.</summary>
	private static T Pump<T>( List<Action> queued, Task<T> task )
	{
		var clock = Stopwatch.StartNew();
		while ( !task.IsCompleted && clock.Elapsed.TotalSeconds < WaitSeconds )
		{
			if ( TakeNext( queued ) is { } action )
				action();
			else
				Thread.Sleep( 5 );
		}

		return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException( "The update task did not finish." );
	}

	/// <summary>Removes and returns the oldest posted action, or null.</summary>
	private static Action? TakeNext( List<Action> queued )
	{
		lock ( queued )
		{
			if ( queued.Count == 0 )
				return null;

			var action = queued[0];
			queued.RemoveAt( 0 );
			return action;
		}
	}
}
