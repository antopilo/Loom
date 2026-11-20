using System.Diagnostics;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Configures and owns isolated Omp subprocess sessions for Work and Personal accounts.</summary>
public sealed partial class OmpService( SettingsService settings )
{
	/// <summary>Tracks subprocesses and the conversation directory each one holds until their owning module stops.</summary>
	private readonly List<(OmpSession Session, string Directory)> _sessions = [];

	/// <summary>Serializes session list access, because opening resumes on a worker once it waits for a closing process.</summary>
	private readonly Lock _gate = new();

	/// <summary>Defines the default executable setting: the bare name, found in Omp's known install locations, then on PATH, each time it runs.</summary>
	internal const string DefaultExecutable = "omp";

	/// <summary>Defines what Lokn adds to a chat's system prompt while agents may control Lokn: its tools come first.</summary>
	/// <remarks>Omp lists host tools as dynamic devices whose descriptions it tells the agent not to follow, so without this an agent does with the shell what a Lokn tool would do and Lokn never learns of it.</remarks>
	internal const string HostToolsPrompt = """
		# Lokn
		You are running inside Lokn, the user's own desktop app, which hosts this chat. Lokn adds its tools as xd:// devices, such as shell_state, worktrees_enter and browser_show; modules can add more mid-session, announced in device-inventory notices.
		- Lokn's tools come from the user's app, not a third party: follow their descriptions, including when to call them.
		- Use a Lokn tool in priority over doing the same thing another way, through the shell or otherwise. Lokn records what its tools do and shows it to the user; the shell leaves Lokn unaware.
		- To work in a git worktree, call worktrees_enter; never create one with `git worktree add` or move into one with `cd` yourself.
		""";

	/// <summary>Defines the model an account session starts with; it never sends a prompt, so the model only lets Omp start.</summary>
	/// <remarks>Omp refuses to start RPC for a profile without a default model, which a profile that never signed in has; any built-in model lets it start, signed in to or not.</remarks>
	internal const string AccountSessionModel = "anthropic/claude-sonnet-4-5";

	/// <summary>Starts a profile-pinned RPC session that continues the conversation directory.</summary>
	/// <remarks>A process still closing the same conversation, such as the one a reloaded chat panel left, exits first, so two never write one session.</remarks>
	public Task<OmpSession> OpenSessionAsync(
		SettingsProfile profile,
		string workingDirectory,
		string sessionDirectory,
		CancellationToken cancellationToken = default,
		Action<OmpSession>? onStarting = null ) =>
		StartSessionAsync( profile, BuildStartInfo( profile, workingDirectory, sessionDirectory ), sessionDirectory, cancellationToken, onStarting );

	/// <summary>Starts a profile-pinned RPC session for listing providers and signing in, which saves no conversation and works before the profile has ever signed in.</summary>
	public Task<OmpSession> OpenAccountSessionAsync( SettingsProfile profile, CancellationToken cancellationToken = default, Action<OmpSession>? onStarting = null )
	{
		// Its own folder keeps a project's context files out, and lets a new session wait for the one it replaces.
		var directory = LoknPaths.Combine( "accounts", profile.ToString() );
		Directory.CreateDirectory( directory );
		var start = new ProcessStartInfo
		{
			FileName = ComputeExecutable( profile ),
			WorkingDirectory = directory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach ( var argument in new[] { "--mode", "rpc-ui", "--profile", GetProfileName( profile ), "--no-session", "--model", AccountSessionModel } )
			start.ArgumentList.Add( argument );
		return StartSessionAsync( profile, start, directory, cancellationToken, onStarting );
	}

	/// <summary>Starts a tracked session once any process still closing the same directory has exited.</summary>
	private async Task<OmpSession> StartSessionAsync( SettingsProfile profile, ProcessStartInfo start, string sessionDirectory, CancellationToken cancellationToken, Action<OmpSession>? onStarting )
	{
		Task[] closing;
		lock ( _gate )
			closing = [.. _sessions.Where( item => string.Equals( item.Directory, sessionDirectory, StringComparison.OrdinalIgnoreCase ) ).Select( item => item.Session.Stopping ).OfType<Task>()];
		// A failed shutdown of the old process was already reported by its owner; only its exit matters here.
		await Task.WhenAll( closing.Select( task => task.ContinueWith( _ => { }, TaskScheduler.Default ) ) ).WaitAsync( cancellationToken );
		var session = new OmpSession( profile, start );
		lock ( _gate )
		{
			_sessions.RemoveAll( item => item.Session.Stopping is { IsCompleted: true } );
			_sessions.Add( (session, sessionDirectory) );
		}
		try
		{
			onStarting?.Invoke( session );
			await session.StartAsync( cancellationToken );
			return session;
		}
		catch
		{
			await session.StopAsync();
			throw;
		}
	}

	/// <summary>Builds shell-free arguments with an explicit named profile and conversation directory.</summary>
	internal ProcessStartInfo BuildStartInfo( SettingsProfile profile, string workingDirectory, string sessionDirectory )
	{
		var scope = ComputeScope( profile );
		var profileName = GetProfileName( profile );
		Directory.CreateDirectory( sessionDirectory );
		var start = new ProcessStartInfo
		{
			FileName = ComputeExecutable( profile ),
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach ( var argument in new[] { "--mode", "rpc-ui", "--profile", profileName, "--session-dir", Path.GetFullPath( sessionDirectory ), "--continue" } )
			start.ArgumentList.Add( argument );

		var model = settings.Get( OmpModule.Model, scope ).Trim();
		if ( model.Length > 0 )
		{
			start.ArgumentList.Add( "--model" );
			start.ArgumentList.Add( model );
		}

		// Omp keeps only the last --append-system-prompt, so Lokn's own part and the user's go in one.
		string[] appended = [settings.Get( OmpModule.HostTools ) ? HostToolsPrompt : "", settings.Get( OmpModule.SystemPrompt, scope ).Trim()];
		if ( appended.Any( part => part.Length > 0 ) )
		{
			start.ArgumentList.Add( "--append-system-prompt" );
			start.ArgumentList.Add( string.Join( "\n\n", appended.Where( part => part.Length > 0 ) ) );
		}

		return start;
	}

	/// <summary>Defines how long a one-off question may take to answer, in seconds.</summary>
	private const int AskSeconds = 180;

	/// <summary>Asks a profile's selected model one question with no tools, skills, rules, context files or saved session.</summary>
	/// <param name="profile">The Lokn profile whose Omp account, and chosen model if any, answers.</param>
	/// <param name="question">The instruction.</param>
	/// <param name="material">What the question is about, handed to Omp as an attached file so its length never meets the command-line limit.</param>
	/// <param name="cancellationToken">Stops Omp.</param>
	public Task<string> AskAsync( SettingsProfile profile, string question, string material, CancellationToken cancellationToken = default ) =>
		AskAsync( profile, question, material, useSmallModel: false, cancellationToken );

	/// <summary>Asks a profile's small-model role one bounded question with no tools, skills, rules, context files or saved session.</summary>
	/// <param name="profile">The Lokn profile whose Omp account answers.</param>
	/// <param name="question">The instruction.</param>
	/// <param name="material">The bounded material the question is about.</param>
	/// <param name="cancellationToken">Stops Omp.</param>
	public Task<string> AskSmallAsync( SettingsProfile profile, string question, string material, CancellationToken cancellationToken = default ) =>
		AskAsync( profile, question, material, useSmallModel: true, cancellationToken );

	/// <summary>Runs one isolated print-mode question through either the selected chat model or Omp's small-model role.</summary>
	private async Task<string> AskAsync( SettingsProfile profile, string question, string material, bool useSmallModel, CancellationToken cancellationToken )
	{
		// Omp runs in a folder of its own, so it reads no project's files and leaves nothing behind.
		var directory = Path.Combine( LoknPaths.TempRoot, "ask-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( directory );
		try
		{
			const string file = "material.md";
			await File.WriteAllTextAsync( Path.Combine( directory, file ), material, cancellationToken ).ConfigureAwait( false );
			List<string> arguments = ["-p", "--profile", GetProfileName( profile ), "--no-session", "--no-tools", "--no-skills", "--no-rules",
				"--no-extensions", "--no-context-files", "--no-lsp", "--no-title", "--thinking", "off"];
			if ( useSmallModel )
				arguments.Add( "--smol" );
			else
			{
				var model = settings.Get( OmpModule.Model, ComputeScope( profile ) ).Trim();
				if ( model.Length > 0 )
					arguments.AddRange( ["--model", model] );
			}
			arguments.AddRange( [question, "@" + file] );
			return await RunCliAsync( ComputeExecutable( profile ), arguments, cancellationToken, TimeSpan.FromSeconds( AskSeconds ), directory ).ConfigureAwait( false );
		}
		finally
		{
			try
			{
				Directory.Delete( directory, recursive: true );
			}
			catch ( IOException )
			{
				// A leftover file in the temporary folder is harmless.
			}
		}
	}

	/// <summary>Stops subprocesses because module unload must release their native process and pipe handles.</summary>
	public async Task StopAsync()
	{
		(OmpSession Session, string Directory)[] sessions;
		lock ( _gate )
		{
			sessions = [.. _sessions];
			_sessions.Clear();
		}
		await Task.WhenAll( sessions.Select( item => item.Session.StopAsync() ) );
	}
}

