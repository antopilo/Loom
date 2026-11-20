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
			Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		window.Open();
		PumpUntil( window, semantics, () => HasText( semantics, "Omp 1.2.3" ), "Setup shows the detected Omp." );
		CoreChecks.Check( HasText( semantics, "fixture · 2 models" ) && HasText( semantics, "this account's Omp default" ), "Work shows its signed-in account and the model Omp starts it with." );
		CoreChecks.Check( HasText( semantics, "Not signed in" ) && HasText( semantics, "sign in to choose one" ), "Personal says it is not signed in and has no model." );
		var capture = Array.IndexOf( args, "--capture" );
		if ( capture >= 0 && capture + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[capture + 1], "omp-setup" );

		Click( window, semantics, FindIn( semantics, "Personal", "Account", "Change" ) );
		PumpUntil( window, semantics, () => HasButton( semantics, "Use team for Personal" ), "Change on Personal's account lists the other accounts." );
		Click( window, semantics, AccessibilityRole.Button, "Use team for Personal" );
		PumpUntil( window, semantics, () => app.Settings.Get( OmpModule.ProfileName, SettingsScope.Personal ) == "team" && !HasButton( semantics, "Use team for Personal" ),
			"Choosing an account saves it and closes the list." );
		CoreChecks.Check( !app.Settings.HasValue( OmpModule.ProfileName, SettingsScope.Work ), "Choosing an account saves it for that profile only." );

		Click( window, semantics, FindIn( semantics, "Work", "Model", "Change" ) );
		PumpUntil( window, semantics, () => HasButton( semantics, "Select fixture/two" ), "Change on Work's model lists the models." );
		Click( window, semantics, AccessibilityRole.Button, "Select fixture/two" );
		PumpUntil( window, semantics, () => app.Settings.Get( OmpModule.Model, SettingsScope.Work ) == "fixture/two" && HasText( semantics, "chosen in Lokn" ),
			"Choosing a model saves it and shows it as chosen in Lokn." );
		CoreChecks.Check( app.Settings.Get( OmpModule.Model, SettingsScope.Personal ) == "", "Choosing a model saves it for that profile only." );
		Click( window, semantics, FindIn( semantics, "Work", "Model", "Use Omp's default" ) );
		PumpUntil( window, semantics, () => !HasText( semantics, "chosen in Lokn" ), "Use Omp's default resets the model." );
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
			Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		window.Open();
		app.MarkStarted();
		try
		{
			// Personal is signed out and Omp's default profile is signed in: one click adopts it.
			PumpUntil( window, semantics, () => HasText( semantics, "Your Omp account \"default\" is signed in" ), "Get started offers the signed-in default account." );
			Capture( window, captures, "1-adopt" );
			Click( window, semantics, AccessibilityRole.Button, "Sign in" );
			PumpUntil( window, semantics, () => app.Settings.Get( OmpModule.ProfileName, SettingsScope.Personal ) == "default" && HasText( semantics, "Loom is ready." ),
				"Sign in adopts the signed-in default account and Get started reports ready." );
			Capture( window, captures, "2-ready" );

			// Now every profile is signed out until the fixture login writes the marker.
			app.Settings.Reset( OmpModule.ProfileName, SettingsScope.Personal );
			app.Settings.Reset( OmpModule.ProfileName, SettingsScope.Work );
			Environment.SetEnvironmentVariable( OmpUiFixture.SignInVariable, marker );
			Click( window, semantics, AccessibilityRole.Button, "Check again" );
			PumpUntil( window, semantics, () => HasText( semantics, "Not signed in" ) && !HasText( semantics, "Loom is ready." ), "Check again finds every account signed out." );
			Capture( window, captures, "3-signed-out" );
			Click( window, semantics, AccessibilityRole.Button, "Sign in" );
			PumpUntil( window, semantics, () => HasButton( semantics, "Connect Fixture" ), "Sign in lists the providers." );
			Capture( window, captures, "4-providers" );

			// The browser provider names its page, signs in, and Get started checks again on its own.
			Click( window, semantics, AccessibilityRole.Button, "Connect Fixture" );
			PumpUntil( window, semantics, () => HasText( semantics, "Signed in to Fixture" ), "The browser provider signs in." );
			PumpUntil( window, semantics, () => HasText( semantics, "Loom is ready." ), "An in-app sign-in completes Get started's fix, which checks again and reports ready." );
			Capture( window, captures, "5-signed-in" );
			Click( window, semantics, Flatten( FindSignInPage( semantics ) ).First( node => node.Role == AccessibilityRole.Button && node.Name == "Close" ) );
			PumpUntil( window, semantics, () => !app.Panels.Panels.Any( panel => panel.TypeId == "omp.accounts" ), "Close closes the sign-in page." );

			// Signed out again: a provider that needs a key fails in RPC, and omp login runs in a Loom terminal instead.
			File.Delete( marker );
			Click( window, semantics, AccessibilityRole.Button, "Check again" );
			PumpUntil( window, semantics, () => HasText( semantics, "Not signed in" ) && !HasText( semantics, "Loom is ready." ), "Check again finds the account signed out again." );
			Click( window, semantics, AccessibilityRole.Button, "Sign in" );
			PumpUntil( window, semantics, () => HasButton( semantics, "Connect Keyed" ), "Sign in lists the key provider." );
			Click( window, semantics, AccessibilityRole.Button, "Connect Keyed" );
			PumpUntil( window, semantics, () => HasText( semantics, "asks for a secret" ) && HasButton( semantics, "Sign in in terminal" ),
				"A key provider's sign-in offers the terminal." );
			Capture( window, captures, "6-needs-terminal" );
			Click( window, semantics, Nodes( semantics ).First( node => node.Role == AccessibilityRole.Button && node.Name == "Sign in in terminal" ) );
			// The terminal runs the fixture's omp login, which signs in and exits; a terminal whose command succeeds closes itself.
			PumpUntil( window, semantics, () => File.Exists( marker ), "A key provider's sign-in runs omp login in a Loom terminal." );
			Click( window, semantics, Flatten( FindSignInPage( semantics ) ).First( node => node.Role == AccessibilityRole.Button && node.Name == "Check again" ) );
			PumpUntil( window, semantics, () => HasText( semantics, "Loom is ready." ), "Checking again after the terminal sign-in completes Get started's fix." );
			Capture( window, captures, "7-ready" );
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

	/// <summary>Finds the sign-in page's group.</summary>
	private static AccessibilityNode FindSignInPage( SemanticAdapter semantics ) =>
		Nodes( semantics ).First( node => node.Role == AccessibilityRole.Group && node.Name == "Omp sign-in" );

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

	/// <summary>Reports whether a button with the name shows.</summary>
	private static bool HasButton( SemanticAdapter semantics, string name ) => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == name );

	/// <summary>Finds the one control with the role and name, and clicks it through native pointer input.</summary>
	private static void Click( Window window, SemanticAdapter semantics, AccessibilityRole role, string name ) =>
		Click( window, semantics, Nodes( semantics ).Single( node => node.Role == role && node.Name == name ) );

	/// <summary>Clicks a control, followed by its identifier, through native pointer input after focusing it in code (<see cref="UiInput"/>), so the click
	/// lands even when the window lost the system focus.</summary>
	private static void Click( Window window, SemanticAdapter semantics, AccessibilityNode node ) =>
		UiInput.Click( window, () => Nodes( semantics ).FirstOrDefault( candidate => candidate.Id == node.Id ), () => Pump( window ), node.Name ?? node.Role.ToString() );

	/// <summary>Pumps until the state an action changes holds, failing with what was expected and what shows after 20 seconds, as local CLI and RPC
	/// work completes.</summary>
	private static void PumpUntil( Window window, SemanticAdapter semantics, Func<bool> predicate, string what )
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		while ( !predicate() )
		{
			if ( timer.Elapsed > TimeSpan.FromSeconds( 20 ) )
			{
				var shown = string.Join( " | ", Nodes( semantics ).Select( node => node.Value ?? node.Name ).Where( text => text is { Length: > 0 } ).Distinct() );
				CoreChecks.Check( false, $"{what} (timed out; showing: {shown[..Math.Min( shown.Length, 3000 )]})" );
			}
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
