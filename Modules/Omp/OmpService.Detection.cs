using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Describes the Omp executable Lokn runs.</summary>
/// <param name="Path">The resolved executable path.</param>
/// <param name="Version">The version Omp reports, such as 18.22.0.</param>
/// <param name="Source">Where it came from: "Tern", "Installer", "Bun", "PATH", or "Settings".</param>
public sealed record OmpInstall( string Path, string Version, string Source );

/// <summary>Describes one Omp profile: its own credentials, settings and sessions.</summary>
/// <param name="Name">The profile name passed to <c>omp --profile</c>.</param>
/// <param name="Providers">The providers it is signed in to, in order.</param>
/// <param name="ModelCount">How many models it can use; zero means it is not signed in.</param>
/// <param name="DefaultModel">The model Omp starts with for this profile, or empty when it has none.</param>
/// <param name="Error">Why the profile could not be read, or empty.</param>
public sealed record OmpProfileInfo( string Name, IReadOnlyList<string> Providers, int ModelCount, string DefaultModel, string Error = "" )
{
	/// <summary>Gets whether the profile can use at least one model.</summary>
	public bool IsSignedIn => ModelCount > 0;
}

/// <summary>Reports what Lokn found: the Omp install, or why there is none, and every Omp profile with its sign-in state.</summary>
/// <param name="Install">The Omp install, or null when none runs.</param>
/// <param name="Error">Why no install was found, or empty.</param>
/// <param name="Profiles">Every known profile: Omp's default, those on disk, and the ones Lokn's chats use.</param>
public sealed record OmpDetection( OmpInstall? Install, string Error, IReadOnlyList<OmpProfileInfo> Profiles );

/// <summary>Finds the Omp install and its profiles, so setup shows choices instead of asking for paths and names.</summary>
public sealed partial class OmpService
{
	/// <summary>Defines the name of Omp's own default profile.</summary>
	internal const string DefaultProfile = "default";

	/// <summary>Gets or sets the folder that holds Omp's named profiles; checks point it at a temporary folder.</summary>
	internal string ProfilesDirectory { get; set; } = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".omp", "profiles" );

	/// <summary>Gets the Omp profile a Lokn profile uses: its setting, or the isolated "work" or "personal" profile when blank.</summary>
	public string GetProfileName( SettingsProfile profile )
	{
		var configured = settings.Get( OmpModule.ProfileName, ComputeScope( profile ) ).Trim();
		return configured.Length == 0 ? profile.ToString().ToLowerInvariant() : configured;
	}

	/// <summary>Gets the user-level MCP config of the Omp profile a Lokn profile uses, the file Omp's <c>/mcp</c> commands write.</summary>
	public string GetMcpConfigPath( SettingsProfile profile )
	{
		var name = GetProfileName( profile );
		var agent = string.Equals( name, DefaultProfile, StringComparison.OrdinalIgnoreCase )
			? Path.Combine( Path.GetDirectoryName( ProfilesDirectory )!, "agent" )
			: Path.Combine( ProfilesDirectory, name, "agent" );
		return Path.Combine( agent, "mcp.json" );
	}

	/// <summary>Finds the configured Omp executable and reads every profile's sign-in state and default model, all in parallel.</summary>
	public async Task<OmpDetection> DetectAsync( CancellationToken cancellationToken = default )
	{
		var configured = settings.Get( OmpModule.Executable, SettingsScope.Global );
		var path = ResolveExecutable( configured );
		if ( path is null )
			return new( null, Path.IsPathRooted( configured ) ? $"Omp was not found at {configured}." : "Omp is not installed.", [] );

		string version;
		try
		{
			var output = await RunCliAsync( path, ["--version"], cancellationToken ).ConfigureAwait( false );
			version = output.Split( '\n' )[0].Trim().Replace( "omp/", "", StringComparison.OrdinalIgnoreCase );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			return new( null, $"{path} did not run: {error.Message}", [] );
		}

		var source = DescribeSource( configured, path );
		var names = new List<string> { DefaultProfile, GetProfileName( SettingsProfile.Work ), GetProfileName( SettingsProfile.Personal ) };
		if ( Directory.Exists( ProfilesDirectory ) )
			names.AddRange( Directory.EnumerateDirectories( ProfilesDirectory ).Select( directory => Path.GetFileName( directory ) ).Order( StringComparer.OrdinalIgnoreCase ) );

		var profiles = await Task.WhenAll( names.Distinct( StringComparer.OrdinalIgnoreCase ).Select( name => DetectProfileAsync( path, name, cancellationToken ) ) ).ConfigureAwait( false );
		return new( new( path, version, source ), "", profiles );
	}

	/// <summary>Reads one profile's models, remembering them for the model picker, and its default model.</summary>
	private async Task<OmpProfileInfo> DetectProfileAsync( string executable, string name, CancellationToken cancellationToken )
	{
		try
		{
			var models = RunCliAsync( executable, ["--profile", name, "models", "--json"], cancellationToken );
			var roles = RunCliAsync( executable, ["--profile", name, "config", "get", "modelRoles", "--json"], cancellationToken );
			var catalog = BuildModels( await models.ConfigureAwait( false ) );
			lock ( _gate )
				_catalogs[ComputeCatalogKey( executable, name )] = catalog;
			return new( name, [.. catalog.Select( model => model.Provider ).Distinct()], catalog.Count, ReadDefaultModel( await roles.ConfigureAwait( false ) ) );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			return new( name, [], 0, "", error.Message );
		}
	}

	/// <summary>Opens Omp's own login window for a profile and completes when the user closes it.</summary>
	public async Task OpenLoginAsync( string profileName )
	{
		var start = new ProcessStartInfo
		{
			FileName = ComputeLoginExecutable(),
			UseShellExecute = true,
		};
		foreach ( var argument in new[] { "--profile", profileName, "login" } )
			start.ArgumentList.Add( argument );
		using var process = Process.Start( start );
		if ( process is not null )
			await process.WaitForExitAsync().ConfigureAwait( false );
	}

	/// <summary>Builds the command line of Omp's interactive login for a profile, such as <c>"C:\…\omp.exe" --profile work login</c>, for a terminal to run.</summary>
	/// <remarks>The profile name needs no quoting: <see cref="OmpModule.ProfileName"/> allows only letters, digits, '-', '_' and '.'.</remarks>
	internal string ComputeLoginCommandLine( string profileName ) => $"\"{ComputeLoginExecutable()}\" --profile {profileName} login";

	/// <summary>Gets the executable Omp's login runs: the configured one, resolved like every other start.</summary>
	private string ComputeLoginExecutable() => ResolveExecutable( settings.Get( OmpModule.Executable ) ) ?? settings.Get( OmpModule.Executable );

	/// <summary>Installs Omp with its official installer into <c>%LOCALAPPDATA%\omp</c>, then checks that it runs.</summary>
	/// <param name="progress">Receives the installer's output lines.</param>
	/// <param name="cancellationToken">Stops the install.</param>
	public async Task InstallAsync( IProgress<string> progress, CancellationToken cancellationToken )
	{
		await ToolLocator.RunPowerShellAsync( "irm https://omp.sh/install.ps1 | iex", progress, cancellationToken ).ConfigureAwait( false );
		var path = ResolveExecutable( settings.Get( OmpModule.Executable, SettingsScope.Global ) ) ?? ResolveExecutable( DefaultExecutable )
			?? throw new InvalidOperationException( "The Omp installer finished, but omp.exe was not found." );
		try
		{
			progress.Report( (await RunCliAsync( path, ["--version"], cancellationToken ).ConfigureAwait( false )).Split( '\n' )[0].Trim() );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			throw new InvalidOperationException( $"Omp was installed at {path} but did not run: {error.Message}", error );
		}
	}

	/// <summary>Finds an executable: a rooted path that exists, or for a bare name its known install locations, then PATH; null when there is none.</summary>
	/// <remarks>For <c>omp</c> the order is Tern's bundled Omp, <c>%PI_INSTALL_DIR%</c>, the official installer's folder, bun's global folder, then PATH.</remarks>
	internal static string? ResolveExecutable( string executable )
	{
		if ( string.IsNullOrWhiteSpace( executable ) )
			return null;
		if ( Path.IsPathRooted( executable ) )
			return File.Exists( executable ) ? executable : null;

		var isOmp = executable.Equals( DefaultExecutable, StringComparison.OrdinalIgnoreCase ) || executable.Equals( DefaultExecutable + ".exe", StringComparison.OrdinalIgnoreCase );
		return ToolLocator.Find( executable, isOmp ? KnownExecutables.Select( known => known.Path ) : [] );
	}

	/// <summary>Names where a resolved executable came from: a known install location, the setting's own path, or PATH.</summary>
	internal static string DescribeSource( string configured, string path )
	{
		if ( Path.IsPathRooted( configured ) )
			return "Settings";
		foreach ( var (known, source) in KnownExecutables )
		{
			if ( path.Equals( known, StringComparison.OrdinalIgnoreCase ) )
				return source;
		}

		return "PATH";
	}

	/// <summary>Gets where Omp installers put omp.exe, in the order they are checked, each with the source name the user sees.</summary>
	private static IEnumerable<(string Path, string Source)> KnownExecutables
	{
		get
		{
			var local = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
			yield return (Path.Combine( local, "Tern", "omp", "bin", "omp.exe" ), "Tern");
			if ( Environment.GetEnvironmentVariable( "PI_INSTALL_DIR" ) is { Length: > 0 } custom )
				yield return (Path.Combine( custom, "omp.exe" ), "Installer");
			yield return (Path.Combine( local, "omp", "omp.exe" ), "Installer");
			yield return (Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".bun", "bin", "omp.exe" ), "Bun");
		}
	}

	/// <summary>Reads the default role from <c>omp config get modelRoles --json</c>, without Omp's thinking-level suffix.</summary>
	internal static string ReadDefaultModel( string json )
	{
		using var document = JsonDocument.Parse( json );
		return document.RootElement.TryGetProperty( "value", out var roles ) && roles.ValueKind == JsonValueKind.Object
			&& roles.TryGetProperty( "default", out var model ) && model.GetString() is { } selector
			? selector.Split( ':' )[0]
			: "";
	}

	/// <summary>Runs a short Omp command and returns its output, failing with its diagnostic when it exits with an error.</summary>
	/// <param name="executable">The Omp executable.</param>
	/// <param name="arguments">The command's arguments.</param>
	/// <param name="cancellationToken">Stops the command.</param>
	/// <param name="timeout">How long the command may run; <see cref="DiscoverySeconds"/> when null.</param>
	/// <param name="workingDirectory">The folder it runs in; Lokn's own when null.</param>
	internal static async Task<string> RunCliAsync( string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null, string? workingDirectory = null )
	{
		var start = BuildCliStartInfo( executable );
		if ( workingDirectory is not null )
			start.WorkingDirectory = workingDirectory;
		foreach ( var argument in arguments )
			start.ArgumentList.Add( argument );

		// The command owns native pipe handles; release them once it finishes.
		using var process = new Process
		{
			StartInfo = start,
		};
		process.Start();
		// Omp reads a piped stdin to its end as part of the prompt, so an open one would keep it waiting until the deadline.
		process.StandardInput.Close();
		var limit = timeout ?? TimeSpan.FromSeconds( DiscoverySeconds );
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		deadline.CancelAfter( limit );
		var output = process.StandardOutput.ReadToEndAsync( deadline.Token );
		var error = process.StandardError.ReadToEndAsync( deadline.Token );
		try
		{
			await process.WaitForExitAsync( deadline.Token ).ConfigureAwait( false );
			var text = await output.ConfigureAwait( false );
			var diagnostic = await error.ConfigureAwait( false );
			if ( process.ExitCode != 0 )
				throw new InvalidOperationException( string.IsNullOrWhiteSpace( diagnostic ) ? $"Omp exited with code {process.ExitCode}." : diagnostic.Trim() );

			return text;
		}
		catch ( Exception failure )
		{
			if ( !process.HasExited )
				process.Kill( entireProcessTree: true );
			await process.WaitForExitAsync().ConfigureAwait( false );
			try
			{
				await Task.WhenAll( output, error ).ConfigureAwait( false );
			}
			catch ( OperationCanceledException ) { }
			if ( failure is OperationCanceledException && !cancellationToken.IsCancellationRequested )
				throw new TimeoutException( $"Omp did not finish within {limit.TotalSeconds:0} seconds.", failure );
			throw;
		}
	}

	/// <summary>Builds a shell-free, UTF-8 start for a short Omp command.</summary>
	private static ProcessStartInfo BuildCliStartInfo( string executable ) => new()
	{
		FileName = executable,
		UseShellExecute = false,
		CreateNoWindow = true,
		RedirectStandardInput = true,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		StandardInputEncoding = new UTF8Encoding( encoderShouldEmitUTF8Identifier: false ),
		StandardOutputEncoding = new UTF8Encoding( encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true ),
		StandardErrorEncoding = new UTF8Encoding( encoderShouldEmitUTF8Identifier: false ),
	};

	/// <summary>Gets the settings layer of a Lokn profile.</summary>
	private static SettingsScope ComputeScope( SettingsProfile profile ) => profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal;
}
