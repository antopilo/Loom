using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Contributes Omp account management and profile-isolated agent connections, and answers one-off questions for other modules as <see cref="IAgentService"/>.</summary>
public sealed class OmpModule : ILoknModule, IAgentService
{
	/// <summary>Defines the Omp executable path, inherited independently by both profiles.</summary>
	public static readonly Setting<string> Executable = new(
		"omp.executable", "Omp executable", OperatingSystem.IsWindows() ? "Path to omp.exe, or its name on PATH." : "Path to omp, or its name on PATH.",
		OmpService.DefaultExecutable, "Omp", value => !string.IsNullOrWhiteSpace( value ) );

	/// <summary>Defines the named Omp credential profile; blank selects work or personal automatically.</summary>
	public static readonly Setting<string> ProfileName = new(
		"omp.profile-name", "Account profile", "Blank keeps Work and Personal apart. \"default\" uses your existing Omp account.",
		"", "Omp", value => value.All( character => char.IsAsciiLetterOrDigit( character ) || character is '-' or '_' or '.' ) );

	/// <summary>Defines the provider/model identifier used by new sessions, or Omp's default when blank.</summary>
	public static readonly Setting<string> Model = new(
		"omp.model", "Model", "A provider/model identifier. Blank uses the account's default.",
		"", "Omp" );

	/// <summary>Defines instructions added to the end of Omp's own system prompt for every chat, or nothing when blank.</summary>
	/// <remarks>Omp keeps its own prompt, which tells the agent how to use its tools; this only adds to it.</remarks>
	public static readonly Setting<string> SystemPrompt = new(
		"omp.system-prompt", "System prompt",
		"Added to every chat's instructions. Open chats use it after a restart.",
		"", "Omp" )
	{
		Editor = SystemPromptEditor.Build,
	};

	/// <summary>Defines whether Omp chats get Lokn's agent tools, letting the agent see and arrange panels and run commands.</summary>
	public static readonly Setting<bool> HostTools = new(
		"omp.host-tools", "Let agents control Loom", "Agents can arrange panels and run commands.",
		true, "Omp" );

	/// <summary>Defines whether new chats start at low reasoning effort and Lokn tools load by group.</summary>
	public static readonly Setting<bool> Economy = new(
		"omp.economy", "Economy mode", "Use fewer tokens: lower effort and fewer tools at start.",
		false, "Omp" );

	/// <summary>Gets the module identifier.</summary>
	public string Id => "omp";

	/// <summary>Gets the display name.</summary>
	public string Title => "Omp";

	/// <summary>Gets the backend service after module startup.</summary>
	public OmpService Service { get; private set; } = null!;

	/// <summary>Stores the application's settings after startup, so setup checks read the active profile.</summary>
	private SettingsService? _settings;

	/// <summary>Stores the module context after startup, so work that starts off the UI thread can return to it.</summary>
	private ModuleContext? _context;

	/// <summary>Registers account settings, account management, model routing, usage, the native Agent Hub, and the question service.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Service<IAgentService>( this )
			.Setting( Executable )
			.Setting( ProfileName )
			.Setting( Model )
			.Setting( SystemPrompt )
			.Setting( HostTools )
			.Setting( Economy )
			.Panel( "setup", "Omp setup", context => new OmpSetupContent( context, Service ) )
			.Panel( "accounts", "Omp accounts", context => new OmpAccountsContent( context, Service ) )
			.Panel( "routing", "Omp model routing", context => new OmpRoutingContent( context, Service,
				context.RestoredState is { } state ? state.Deserialize<SettingsProfile>() : context.Input is SettingsProfile profile ? profile : context.App.Settings.ActiveProfile ) )
			.Panel( "agents", "Omp Agent Hub", context => new OmpAgentHubContent( context, context.Input is OmpAgentHubInput input ? input.Session : null ) )
			.Panel( "usage", "Omp usage", context => new OmpUsageContent( context, Service ) )
			.Panel( "mcp", "Omp MCP servers", context => new OmpMcpContent( context, Service,
				context.RestoredState is { } state ? state.Deserialize<SettingsProfile>() : context.Input is SettingsProfile profile ? profile : context.App.Settings.ActiveProfile ) )
			.Contribute( SetupItem.Point, new SetupItem
			{
				Key = "omp",
				Title = "Omp",
				Purpose = "The agent that runs your chats",
				IsRequired = true,
				Order = 20,
				CheckAsync = CheckInstallAsync,
				FixLabel = "Install",
				FixAsync = OperatingSystem.IsWindows() ? ( progress, cancellationToken ) => Service.InstallAsync( progress, cancellationToken ) : null,
			} )
			.Contribute( SetupItem.Point, new SetupItem
			{
				Key = "omp-sign-in",
				Title = "Sign in",
				Purpose = "An AI provider account for your chats",
				IsRequired = true,
				Order = 30,
				CheckAsync = CheckSignInAsync,
				// The label cannot change with the check, so the fix decides: use a signed-in Omp account, or open the Sign in page.
				FixLabel = "Sign in",
				FixAsync = FixSignInAsync,
			} );
	}

	/// <summary>Gets the Lokn profile chats start in, or Work before startup.</summary>
	private SettingsProfile ActiveProfile => _settings?.ActiveProfile ?? SettingsProfile.Work;

	/// <summary>Checks that an Omp install runs, reporting its version and path.</summary>
	private async Task<SetupStatus> CheckInstallAsync( CancellationToken cancellationToken )
	{
		var detection = await Service.DetectAsync( cancellationToken ).ConfigureAwait( false );
		return detection.Install is { } install
			? new( SetupState.Ready, $"{install.Version} · {install.Path}" )
			: new( SetupState.Missing, detection.Error.Length > 0 ? detection.Error : "Not installed" );
	}

	/// <summary>Checks that the active Lokn profile's Omp profile is signed in to at least one provider, or names a signed-in account the fix would use instead.</summary>
	private async Task<SetupStatus> CheckSignInAsync( CancellationToken cancellationToken )
	{
		var detection = await Service.DetectAsync( cancellationToken ).ConfigureAwait( false );
		if ( detection.Install is null )
			return new( SetupState.Blocked, "Install Omp first" );

		var profile = ActiveProfile;
		var name = Service.GetProfileName( profile );
		var account = detection.Profiles.FirstOrDefault( item => item.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
		if ( account is { IsSignedIn: true } )
			return new( SetupState.Ready, OmpSetupContent.DescribeAccount( account ) );
		if ( FindSignedInAccount( detection, name ) is { } signedIn )
		{
			var users = string.Join( " and ", ComputeAdoptingScopes( profile ).Select( scope => scope.ToString() ) );
			return new( SetupState.Missing, $"Your Omp account \"{signedIn.Name}\" is signed in ({OmpSetupContent.DescribeAccount( signedIn )}). Sign in will use it for {users}" );
		}

		return new( SetupState.Missing, "Not signed in. Sign in lists the providers to connect" );
	}

	/// <summary>Uses an Omp account that is already signed in when there is one, otherwise opens the Sign in page and completes once it signed in or was closed.</summary>
	private async Task FixSignInAsync( IProgress<string> progress, CancellationToken cancellationToken )
	{
		var context = _context ?? throw new InvalidOperationException( "Omp has not started." );
		var detection = await Service.DetectAsync( cancellationToken ).ConfigureAwait( false );
		var profile = ActiveProfile;
		var name = Service.GetProfileName( profile );
		if ( detection.Profiles.Any( item => item.IsSignedIn && item.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ) )
			return;

		if ( FindSignedInAccount( detection, name ) is { } signedIn )
		{
			progress.Report( $"Using your Omp account \"{signedIn.Name}\"" );
			await PostAsync( context, () =>
			{
				foreach ( var scope in ComputeAdoptingScopes( profile ) )
					context.App.Settings.Set( ProfileName, scope, signedIn.Name );
				return true;
			} ).WaitAsync( cancellationToken ).ConfigureAwait( false );
			return;
		}

		progress.Report( "Choose a provider on the Sign in page" );
		await SignInAsync( context, profile ).WaitAsync( cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Gets the settings layers adopting an existing account changes: the active profile's, so its check passes, and the other one's while it still has no account of its own.</summary>
	private List<SettingsScope> ComputeAdoptingScopes( SettingsProfile profile )
	{
		var active = profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal;
		var other = active == SettingsScope.Work ? SettingsScope.Personal : SettingsScope.Work;
		List<SettingsScope> scopes = [SettingsScope.Work, SettingsScope.Personal];
		if ( _settings is { } settings && settings.Get( ProfileName, other ).Trim().Length > 0 )
			scopes.Remove( other );
		return scopes;
	}

	/// <summary>Finds a signed-in Omp account other than the current one, preferring Omp's <c>default</c>, or null when there is none.</summary>
	internal static OmpProfileInfo? FindSignedInAccount( OmpDetection detection, string current )
	{
		var others = detection.Profiles.Where( item => item.IsSignedIn && !item.Name.Equals( current, StringComparison.OrdinalIgnoreCase ) ).ToList();
		return others.FirstOrDefault( item => item.Name.Equals( OmpService.DefaultProfile, StringComparison.OrdinalIgnoreCase ) ) ?? others.FirstOrDefault();
	}

	/// <summary>Asks the profile's chosen model through <see cref="OmpService.AskAsync(SettingsProfile, string, string, CancellationToken)"/>.</summary>
	public Task<string> AskAsync( SettingsProfile profile, string question, string material, CancellationToken cancellationToken = default ) =>
		Service.AskAsync( profile, question, material, cancellationToken );

	/// <summary>Asks the profile's small model through <see cref="OmpService.AskSmallAsync"/>.</summary>
	public Task<string> AskSmallAsync( SettingsProfile profile, string question, string material, CancellationToken cancellationToken = default ) =>
		Service.AskSmallAsync( profile, question, material, cancellationToken );

	/// <summary>Opens or focuses setup for a conversation's captured account profile.</summary>
	public static void OpenSetup( LoknApplication app, SettingsProfile profile )
	{
		if ( app.Panels.FindContent<OmpSetupContent>() is (var id, var setup) )
		{
			setup.UpdateFocus( profile );
			app.Panels.Activate( id );
			return;
		}

		app.Panels.Open( "omp.setup", input: profile );
	}

	/// <summary>Opens or focuses the Sign in page for a profile; callable from any thread.</summary>
	/// <returns>A task that completes when a sign-in succeeds on that page or the page closes.</returns>
	public static Task SignInAsync( LoknApplication app, SettingsProfile profile ) =>
		SignInAsync( app.GetModule<OmpModule>()._context ?? throw new InvalidOperationException( "Omp has not started." ), profile );

	/// <summary>Opens or focuses the Sign in page on the module's UI thread and waits for it to sign in or close.</summary>
	private static async Task SignInAsync( ModuleContext context, SettingsProfile profile )
	{
		var app = context.App;
		var completion = await PostAsync( context, () =>
		{
			if ( app.Panels.FindContent<OmpAccountsContent>() is (var id, var open) )
			{
				app.Panels.Activate( id );
				return open.UpdateSignIn( profile );
			}

			app.Panels.Open( "omp.accounts", input: profile );
			return app.Panels.FindContent<OmpAccountsContent>()!.Value.Content.UpdateSignIn( profile );
		} ).ConfigureAwait( false );
		await completion.ConfigureAwait( false );
	}

	/// <summary>Runs a function on the UI thread and returns its result, failing with its exception and cancelling when the module unloads first.</summary>
	private static Task<T> PostAsync<T>( ModuleContext context, Func<T> action )
	{
		var result = new TaskCompletionSource<T>( TaskCreationOptions.RunContinuationsAsynchronously );
		var isPosted = context.Post( () =>
		{
			try
			{
				result.TrySetResult( action() );
			}
			catch ( Exception error )
			{
				result.TrySetException( error );
			}
		} );
		if ( !isPosted )
			result.TrySetCanceled();
		// A post that never runs because the module unloaded must not leave the caller waiting.
		return result.Task.WaitAsync( context.Lifetime );
	}

	/// <summary>Opens or focuses model routing for one account profile.</summary>
	public static void OpenRouting( LoknApplication app, SettingsProfile profile )
	{
		if ( app.Panels.FindContent<OmpRoutingContent>() is (var id, var routing) )
		{
			routing.UpdateProfile( profile );
			app.Panels.Activate( id );
			return;
		}

		app.Panels.Open( "omp.routing", input: profile );
	}

	/// <summary>Opens or retargets the native Agent Hub beside its connected chat.</summary>
	public static void OpenAgentHub( LoknApplication app, OmpSession session, Guid besidePanelId )
	{
		if ( app.Panels.FindContent<OmpAgentHubContent>() is (var id, var hub) )
		{
			hub.UpdateSession( session );
			app.Panels.Activate( id );
			return;
		}

		app.Panels.OpenBeside( "omp.agents", besidePanelId, new OmpAgentHubInput( session ) );
	}

	/// <summary>Creates the service using the application's live scoped settings, and opens setup at startup when Lokn was launched with <c>--omp-setup</c>.</summary>
	public void Start( ModuleContext context )
	{
		Service = new( context.App.Settings );
		_settings = context.App.Settings;
		_context = context;
		// Posted after the other modules' startup views, such as Home, so setup shows beside what they opened.
		if ( Environment.GetCommandLineArgs().Contains( "--omp-setup" ) )
			context.WhenStarted( () => context.Post( () => OpenSetup( context.App, context.App.Settings.ActiveProfile ) ) );
	}

	/// <summary>Stops owned subprocesses before the module's native resources can be abandoned.</summary>
	public void Stop() => Task.Run( Service.StopAsync ).GetAwaiter().GetResult();
}
