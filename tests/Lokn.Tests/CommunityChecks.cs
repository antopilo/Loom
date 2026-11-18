using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Hotload;

namespace Lokn.Tests;

/// <summary>Exercises community modules: reading a hub, installing into a folder Hotload loads, updating, refusing bad files, and removing.</summary>
internal static class CommunityChecks
{
	/// <summary>Defines how long a check waits for the file watcher, in seconds.</summary>
	private const int WaitSeconds = 20;

	/// <summary>Defines the hub module's source; {0} is its panel title.</summary>
	private const string NotesSource = """
		using Goo;
		using Lokn.Core;

		public sealed class NotesModule : ILoknModule
		{{
			public string Id => "notes";
			public string Title => "Notes";

			public void Register( ModuleBuilder builder )
			{{
				builder.Panel( "note", "{0}", context => new NoteContent() );
			}}
		}}

		internal sealed class NoteContent : PanelContent
		{{
			public override Blob Build() => new Text {{ Content = "{0}" }};
		}}
		""";

	/// <summary>Runs the community module checks without opening a native window.</summary>
	internal static void Run()
	{
		CheckNames();
		CheckInstall();
		Console.WriteLine( "PASS community modules: hub index, install into a Hotload folder, update with stale files removed, refused hashes and paths, and removal." );
	}

	/// <summary>Verifies the ids and file names a hub module may use.</summary>
	private static void CheckNames()
	{
		CoreChecks.Check( CommunityHub.IsValidId( "now-playing" ) && !CommunityHub.IsValidId( "Notes" ) && !CommunityHub.IsValidId( "../x" ) && !CommunityHub.IsValidId( "" ),
			"Module ids are lowercase letters, digits and dashes." );
		CoreChecks.Check( CommunityHub.IsAllowedFile( "NotesModule.cs" ) && CommunityHub.IsAllowedFile( "module.json" ) && CommunityHub.IsAllowedFile( "README.md" ),
			"A module may hold C# files, its description and a readme." );
		CoreChecks.Check( !CommunityHub.IsAllowedFile( "../evil.cs" ) && !CommunityHub.IsAllowedFile( "sub/a.cs" ) && !CommunityHub.IsAllowedFile( "tool.dll" ) && !CommunityHub.IsAllowedFile( ".hidden.cs" ),
			"Paths, binaries and hidden files are refused." );
	}

	/// <summary>Verifies a module installs from a folder hub and loads through Hotload, updates, refuses bad downloads, and is removed.</summary>
	private static void CheckInstall()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var hubFolder = Path.Combine( root, "hub" );
		var installRoot = Path.Combine( root, "community" );
		Directory.CreateDirectory( installRoot );
		Publish( hubFolder, "1.0.0", new() { ["NotesModule.cs"] = string.Format( NotesSource, "Notes one" ), ["Old.cs"] = "// Removed in the next version." } );

		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );

			return true;
		} );
		CoreChecks.Check( app.Load( new HotloadModule( Path.Combine( root, "user" ) ) ), "The hotload module loads." );
		app.ModuleFolders.Set( "community", [installRoot] );
		var hub = new CommunityHub( hubFolder, installRoot );

		var index = Wait( hub.ReadIndexAsync( CancellationToken.None ) );
		var notes = index.Modules.Single();
		CoreChecks.Check( notes is { Id: "notes", Name: "Notes", Author: "tester", Version: "1.0.0", Files.Count: 3 }, "The hub index lists each module with its files." );

		Wait( hub.InstallAsync( index, notes, CancellationToken.None ) );
		RunNext( queued );
		CoreChecks.Check( app.Modules.Any( module => module.Id == "notes" ) && app.Panels.Types.Any( type => type.Title == "Notes one" ), "An installed module compiles and loads through Hotload." );
		CoreChecks.Check( hub.ReadInstalled().SequenceEqual( [new InstalledModule( "notes", "Notes", "1.0.0" )] ), "The installed version is read from the module's description." );

		Publish( hubFolder, "1.1.0", new() { ["NotesModule.cs"] = string.Format( NotesSource, "Notes two" ) } );
		index = Wait( hub.ReadIndexAsync( CancellationToken.None ) );
		var updates = CommunityHub.FindUpdates( index, hub.ReadInstalled() );
		CoreChecks.Check( updates.Count == 1 && updates[0].Version == "1.1.0", "A newer hub version is found as an update." );
		Wait( hub.InstallAsync( index, updates[0], CancellationToken.None ) );
		RunNext( queued );
		CoreChecks.Check( app.Panels.Types.Any( type => type.Title == "Notes two" ) && !File.Exists( Path.Combine( installRoot, "notes", "Old.cs" ) ),
			"An update reloads the module and removes files the new version no longer has." );

		var tampered = index.Modules[0] with { Version = "1.2.0", Files = [.. index.Modules[0].Files.Select( file => file with { Sha256 = new string( '0', 64 ) } )] };
		CoreChecks.Check( Throws<InvalidDataException>( () => Wait( hub.InstallAsync( index, tampered, CancellationToken.None ) ) ) && hub.ReadInstalled()[0].Version == "1.1.0",
			"A file that does not match the index is refused, and the installed version stays." );
		var escaping = index.Modules[0] with { Files = [new HubFile( "../escape.cs", "" )] };
		CoreChecks.Check( Throws<InvalidDataException>( () => Wait( hub.InstallAsync( index, escaping, CancellationToken.None ) ) ) && !File.Exists( Path.Combine( installRoot, "escape.cs" ) ),
			"A file name that leaves the module's folder is refused." );

		hub.Remove( "notes" );
		RunNext( queued );
		CoreChecks.Check( !Directory.Exists( Path.Combine( installRoot, "notes" ) ) && app.AvailableModules.All( module => module.Id != "notes" ), "Removing a module deletes its folder and unloads it." );

		app.Shutdown();
		Directory.Delete( root, recursive: true );
	}

	/// <summary>Writes the notes module into a folder hub, replacing its files, and writes the index as the hub's CI would.</summary>
	private static void Publish( string hubFolder, string version, Dictionary<string, string> sources )
	{
		var folder = Path.Combine( hubFolder, "modules", "notes" );
		if ( Directory.Exists( folder ) )
			Directory.Delete( folder, recursive: true );
		Directory.CreateDirectory( folder );
		sources[CommunityHub.DescriptionFile] = JsonSerializer.Serialize( new { id = "notes", name = "Notes", author = "tester", description = "Takes notes.", version } );
		foreach ( var (name, text) in sources )
			File.WriteAllText( Path.Combine( folder, name ), text );

		var files = sources.Keys.Order( StringComparer.Ordinal ).Select( name => new HubFile( name, Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( Path.Combine( folder, name ) ) ) ) ) ).ToArray();
		var index = new CommunityIndex( "local", [new HubModule( "notes", "Notes", "tester", "Takes notes.", version, files )] );
		File.WriteAllText( Path.Combine( hubFolder, "index.json" ), JsonSerializer.Serialize( index, CommunityIndex.JsonOptions ) );
	}

	/// <summary>Waits for a task and returns its result, rethrowing its own exception.</summary>
	private static T Wait<T>( Task<T> task ) => task.GetAwaiter().GetResult();

	/// <summary>Waits for a task, rethrowing its own exception.</summary>
	private static void Wait( Task task ) => task.GetAwaiter().GetResult();

	/// <summary>Tells whether an action throws the given exception type.</summary>
	private static bool Throws<TException>( Action action ) where TException : Exception
	{
		try
		{
			action();
			return false;
		}
		catch ( TException )
		{
			return true;
		}
	}

	/// <summary>Runs the next action the file watcher posts to the UI thread.</summary>
	private static void RunNext( List<Action> queued )
	{
		var clock = Stopwatch.StartNew();
		while ( clock.Elapsed.TotalSeconds < WaitSeconds )
		{
			Action? action = null;
			lock ( queued )
			{
				if ( queued.Count > 0 )
				{
					action = queued[0];
					queued.RemoveAt( 0 );
				}
			}

			if ( action is not null )
			{
				action();
				return;
			}

			Thread.Sleep( 20 );
		}

		throw new TimeoutException( "Hotload posted nothing to the UI thread." );
	}
}
