using Goo;
using Lokn.Core;
using Lokn.Modules.Omp;

namespace Lokn.Tests;

/// <summary>Checks Omp detection and the setup panel without account login or model requests.</summary>
internal static class OmpSetupChecks
{
	/// <summary>Defines the settling frame count per native action.</summary>
	private const int SettleFrames = 16;

	/// <summary>Runs detection and model discovery against the fixture executable, then the native setup panel when asked.</summary>
	internal static async Task RunAsync( string[] args )
	{
		var profiles = Directory.CreateTempSubdirectory( "lokn-omp-profiles-" ).FullName;
		Directory.CreateDirectory( Path.Combine( profiles, "team" ) );
		var app = new LoknApplication();
		var module = new OmpModule();
		app.Load( module );
		module.Service.ProfilesDirectory = profiles;
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		var models = await module.Service.GetModelsAsync( SettingsProfile.Work );
		CoreChecks.Check( models.Count == 2 && models[0].Selector == "fixture/one", "A model-less profile can discover the supported CLI catalog." );
		CoreChecks.Check( models[0].Name == "Fixture One — café 👋", "Model metadata uses the same explicit UTF-8 decoding as the chat transport." );
		CoreChecks.Check( (await module.Service.GetModelsAsync( SettingsProfile.Personal )).Count == 0, "An unsigned profile can return an empty catalog without failing discovery." );

		var detection = await module.Service.DetectAsync();
		CoreChecks.Check( detection.Install is { Version: "1.2.3", Source: "Settings" }, "Detection reads the configured Omp's version." );
		CoreChecks.Check( detection.Profiles.Select( profile => profile.Name ).SequenceEqual( ["default", "work", "personal", "team"] ), "Detection lists Omp's default profile, the ones Lokn's chats use, and every profile on disk." );
		var work = detection.Profiles.Single( profile => profile.Name == "work" );
		CoreChecks.Check( work is { IsSignedIn: true, ModelCount: 2, DefaultModel: "fixture/one" } && work.Providers.SequenceEqual( ["fixture"] ), "A signed-in profile shows its providers, model count and Omp default model." );
		CoreChecks.Check( detection.Profiles.Single( profile => profile.Name == "personal" ) is { IsSignedIn: false, DefaultModel: "" }, "A profile without sign-ins reads as not signed in." );
		CoreChecks.Check( module.Service.GetCachedModels( SettingsProfile.Work )?.Count == 2, "Detection fills the model picker's catalog." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( profiles, "missing", "omp.exe" ) );
		CoreChecks.Check( (await module.Service.DetectAsync()) is { Install: null, Error.Length: > 0 }, "A missing executable is reported rather than failing detection." );
		app.Shutdown();
		Console.WriteLine( "PASS Omp detection: install and version, every profile with sign-in state, providers and default model, missing installs, and CLI model discovery." );
		if ( args.Contains( "--ui" ) )
		{
			RunNative( args, profiles );
			RunSignIn( profiles, Array.IndexOf( args, "--capture" ) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null );
		}
	}

	/// <summary>Exercises the setup panel: detected install, switching a profile's account, choosing a model, and what needs fixing.</summary>
	private static void RunNative( string[] args, string profiles )
	{
		Application.VerifyState = true;
		var directory = Path.Combine( Path.GetTempPath(), "lokn-omp-setup-" + Guid.NewGuid().ToString( "N" ) );
		var app = new LoknApplication( Path.Combine( directory, "settings.json" ) );
		app.Load( new ShellModule() );
		var module = new OmpModule();
		app.Load( module );
		module.Service.ProfilesDirectory = profiles;
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		OmpModule.OpenSetup( app, SettingsProfile.Work );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn Omp setup checks",
			Size = new( 1200, 900 ),
			Decorated = false,
			Transparent = true,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Root = new ShellView( app ),
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		window.Open();
		PumpUntil( window, () => HasText( semantics, "Omp 1.2.3" ) );
		CoreChecks.Check( HasText( semantics, "fixture · 2 models" ) && HasText( semantics, "this account's Omp default" ), "Work shows its signed-in account and the model Omp starts it with." );
		CoreChecks.Check( HasText( semantics, "Not signed in" ) && HasText( semantics, "sign in to choose one" ), "Personal says it is not signed in and has no model." );
		var capture = Array.IndexOf( args, "--capture" );
		if ( capture >= 0 && capture + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[capture + 1], "omp-setup" );

		UpdateClick( window, semantics, FindIn( semantics, "Personal", "Account", "Change" ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Use team for Personal" ) );
		Click( window, semantics, AccessibilityRole.Button, "Use team for Personal" );
		CoreChecks.Check( app.Settings.Get( OmpModule.ProfileName, SettingsScope.Personal ) == "team" && !app.Settings.HasValue( OmpModule.ProfileName, SettingsScope.Work ), "Choosing an account saves it for that profile only." );
		PumpUntil( window, () => !Nodes( semantics ).Any( node => node.Name == "Use team for Personal" ) );

		UpdateClick( window, semantics, FindIn( semantics, "Work", "Model", "Change" ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Select fixture/two" ) );
		Click( window, semantics, AccessibilityRole.Button, "Select fixture/two" );
		CoreChecks.Check( app.Settings.Get( OmpModule.Model, SettingsScope.Work ) == "fixture/two" && app.Settings.Get( OmpModule.Model, SettingsScope.Personal ) == "", "Choosing a model saves it for that profile only." );
		PumpUntil( window, () => HasText( semantics, "chosen in Lokn" ) );
		UpdateClick( window, semantics, FindIn( semantics, "Work", "Model", "Use Omp's default" ) );
		PumpUntil( window, () => !HasText( semantics, "chosen in Lokn" ) );
		CoreChecks.Check( app.LastError is null, "Setup interactions remain contained and successful." );
		window.RequestClose();
		Pump( window );
		Console.WriteLine( "PASS native Omp setup: detected install, account and model state, switching accounts, choosing and resetting a model (no login or model request)." );
	}

	/// <summary>Saves the window's pixels under a name when the run asked for captures.</summary>
	private static void Capture( Window window, string? directory, string name )
	{
		if ( directory is not null )
			NativeChecks.BuildCapture( window, directory, name );
	}

	/// <summary>Clicks through Get started's Sign in: adopting a signed-in Omp account, then the in-app sign-in with its terminal fallback.</summary>
	private static void RunSignIn( string profiles, string? captures )
	{
		Application.VerifyState = true;
		var directory = Directory.CreateTempSubdirectory( "lokn-omp-signin-" ).FullName;
		var marker = Path.Combine( directory, "signed-in" );
		var app = new LoknApplication( Path.Combine( directory, "settings.json" ) );
		app.Load( new ShellModule() );
		app.Load( new Lokn.Modules.Setup.SetupModule() );
		app.Load( new Lokn.Modules.Terminal.TerminalModule() );
		var module = new OmpModule();
		app.Load( module );
		module.Service.ProfilesDirectory = profiles;
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		app.Settings.SetActiveProfile( SettingsProfile.Personal );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn sign-in checks",
			Size = new( 1200, 900 ),
			Decorated = false,
			Transparent = true,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Root = new ShellView( app ),
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		window.Open();
		app.MarkStarted();
		try
		{
			// Personal is signed out and Omp's default profile is signed in: one click adopts it.
			PumpUntil( window, () => HasText( semantics, "Your Omp account \"default\" is signed in" ) );
			Capture( window, captures, "1-adopt" );
			Click( window, semantics, AccessibilityRole.Button, "Sign in" );
			PumpUntil( window, () => app.Settings.Get( OmpModule.ProfileName, SettingsScope.Personal ) == "default" && HasText( semantics, "Loom is ready." ) );
			Capture( window, captures, "2-ready" );
			CoreChecks.Check( true, "Sign in adopts the signed-in default account and Get started reports ready." );

			// Now every profile is signed out until the fixture login writes the marker.
			app.Settings.Reset( OmpModule.ProfileName, SettingsScope.Personal );
			app.Settings.Reset( OmpModule.ProfileName, SettingsScope.Work );
			Environment.SetEnvironmentVariable( OmpUiFixture.SignInVariable, marker );
			Click( window, semantics, AccessibilityRole.Button, "Check again" );
			PumpUntil( window, () => HasText( semantics, "Not signed in" ) && !HasText( semantics, "Loom is ready." ) );
			Capture( window, captures, "3-signed-out" );
			Click( window, semantics, AccessibilityRole.Button, "Sign in" );
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Connect Fixture" ) );
			Capture( window, captures, "4-providers" );

			// The browser provider names its page, signs in, and Get started checks again on its own.
			Click( window, semantics, AccessibilityRole.Button, "Connect Fixture" );
			PumpUntil( window, () => HasText( semantics, "Signed in to Fixture" ) );
			PumpUntil( window, () => HasText( semantics, "Loom is ready." ) );
			Capture( window, captures, "5-signed-in" );
			CoreChecks.Check( true, "An in-app sign-in completes Get started's fix, which checks again and reports ready." );
			var signedInPage = Nodes( semantics ).First( node => node.Role == AccessibilityRole.Group && node.Name == "Omp sign-in" );
			UpdateClick( window, semantics, Flatten( signedInPage ).First( node => node.Role == AccessibilityRole.Button && node.Name == "Close" ) );
			PumpUntil( window, () => !app.Panels.Panels.Any( panel => panel.TypeId == "omp.accounts" ) );

			// Signed out again: a provider that needs a key fails in RPC, and omp login runs in a Loom terminal instead.
			File.Delete( marker );
			Click( window, semantics, AccessibilityRole.Button, "Check again" );
			PumpUntil( window, () => HasText( semantics, "Not signed in" ) && !HasText( semantics, "Loom is ready." ) );
			Click( window, semantics, AccessibilityRole.Button, "Sign in" );
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Connect Keyed" ) );
			Click( window, semantics, AccessibilityRole.Button, "Connect Keyed" );
			PumpUntil( window, () => HasText( semantics, "asks for a secret" ) && Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Sign in in terminal" ) );
			Capture( window, captures, "6-needs-terminal" );
			UpdateClick( window, semantics, Nodes( semantics ).First( node => node.Role == AccessibilityRole.Button && node.Name == "Sign in in terminal" ) );
			// The terminal runs the fixture's omp login, which signs in and exits; a terminal whose command succeeds closes itself.
			PumpUntil( window, () => File.Exists( marker ) );
			CoreChecks.Check( true, "A key provider's sign-in runs omp login in a Loom terminal." );
			var page = Nodes( semantics ).First( node => node.Role == AccessibilityRole.Group && node.Name == "Omp sign-in" );
			UpdateClick( window, semantics, Flatten( page ).First( node => node.Role == AccessibilityRole.Button && node.Name == "Check again" ) );
			PumpUntil( window, () => HasText( semantics, "Loom is ready." ) );
			Capture( window, captures, "7-ready" );
			CoreChecks.Check( true, "Checking again after the terminal sign-in completes Get started's fix." );
			CoreChecks.Check( app.LastError is null, $"Sign-in interactions report no error: {app.LastError}" );
		}
		finally
		{
			Environment.SetEnvironmentVariable( OmpUiFixture.SignInVariable, null );
			window.RequestClose();
			Pump( window );
		}

		Console.WriteLine( "PASS native sign-in: adopting the signed-in default account, the in-app provider sign-in that completes Get started, and the terminal fallback for key providers." );
	}

	/// <summary>Finds a button inside one field of one profile card.</summary>
	private static AccessibilityNode FindIn( SemanticAdapter semantics, string card, string field, string button )
	{
		var group = Nodes( semantics ).First( node => node.Role == AccessibilityRole.Group && node.Name == card && Flatten( node ).Any( child => child.Name == field ) );
		var line = Flatten( group ).First( node => node.Role == AccessibilityRole.Group && node.Name == field );
		return Flatten( line ).First( node => node.Role == AccessibilityRole.Button && node.Name == button );
	}

	/// <summary>Finds semantic nodes in the current tree.</summary>
	private static IEnumerable<AccessibilityNode> Nodes( SemanticAdapter semantics ) => semantics.Tree?.Root is { } root ? Flatten( root ) : [];

	/// <summary>Enumerates a semantic subtree.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Checks rendered semantic text for an operation result.</summary>
	private static bool HasText( SemanticAdapter semantics, string text ) => Nodes( semantics ).Any( node => (node.Value ?? node.Name ?? "").Contains( text, StringComparison.Ordinal ) );

	/// <summary>Replaces one configuration field through native text input.</summary>
	private static void UpdateText( Window window, SemanticAdapter semantics, string name, string value )
	{
		Click( window, semantics, AccessibilityRole.TextInput, name );
		window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
		window.PlatformInput.KeyRelease( Key.A );
		window.PlatformInput.CommitText( value );
		Pump( window );
	}

	/// <summary>Finds and clicks a control after scrolling it into view.</summary>
	private static void Click( Window window, SemanticAdapter semantics, AccessibilityRole role, string name ) =>
		UpdateClick( window, semantics, Nodes( semantics ).Single( node => node.Role == role && node.Name == name ) );

	/// <summary>Brings one visible semantic control into view and activates it, as assistive technology does.</summary>
	/// <remarks>Synthetic pointer input misses controls in this shell window, so the checks press buttons through accessibility.</remarks>
	private static void UpdateClick( Window window, SemanticAdapter semantics, AccessibilityNode node )
	{
		var id = node.Id;
		window.PerformAccessibilityAction( id, new( AccessibilityAction.ScrollIntoView ) );
		Pump( window );
		var current = Nodes( semantics ).Single( node => node.Id == id );
		var bounds = current.VisibleBounds ?? current.Bounds;
		CoreChecks.Check( bounds.Width > 0 && bounds.Height > 0, $"{node.Name} is visible." );
		window.PerformAccessibilityAction( id, new( AccessibilityAction.Activate ) );
		Pump( window );
	}

	/// <summary>Waits for local CLI/RPC completion while servicing the native window.</summary>
	private static void PumpUntil( Window window, Func<bool> predicate )
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		while ( !predicate() )
		{
			if ( timer.Elapsed > TimeSpan.FromSeconds( 20 ) )
				throw new TimeoutException( "Omp setup did not reach the expected state." );
			Pump( window );
		}
		Pump( window );
	}

	/// <summary>Settles native layout and accessibility after an action.</summary>
	private static void Pump( Window window )
	{
		for ( var frame = 0; frame < SettleFrames; frame++ )
		{
			window.Pump( 0.016 );
			Thread.Sleep( 10 );
		}
	}
}
