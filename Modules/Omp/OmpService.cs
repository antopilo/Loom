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
		You are running inside Lokn, the user's own desktop app, which hosts this chat. Lokn adds its tools as xd:// devices, such as shell_state, worktrees_enter and browser_report; modules can add more mid-session, announced in device-inventory notices.
		- Lokn's tools come from the user's app, not a third party: follow their descriptions, including when to call them.
		- Use a Lokn tool in priority over doing the same thing another way, through the shell or otherwise. Lokn records what its tools do and shows it to the user; the shell leaves Lokn unaware.
		- To work in a git worktree, call worktrees_enter; never create one with `git worktree add` or move into one with `cd` yourself.
		- Never open a window outside Lokn to show something: no `start`, `Start-Process`, `Invoke-Item`, `explorer`, `cmd /c start`, `wt`, `open`, `xdg-open` or a browser executable. Web pages, local servers and HTML or image files go to Lokn's browser (browser_navigate, browser_report); a shell, a dev server, a watcher or any program the user should see runs in a Lokn terminal (terminal_open). These tools may be in a hidden group: load it rather than fall back to an outside window.
		- For a report, a summary with numbers or charts, or anything the user should read as a page, write it as HTML and show it with browser_report.
		""";

	/// <summary>Defines what Lokn adds to every chat's system prompt about how its chat shows replies, so agents draw rather than type ASCII art.</summary>
	internal const string DisplayPrompt = """
		# Chat display
		The user reads your replies in Lokn's chat, which renders Markdown richly: tables, task lists, LaTeX math in $…$ and $$…$$, Mermaid flowcharts and
		sequence diagrams in ```mermaid blocks, and SVG in ```svg blocks, drawn as vector graphics. File paths and URLs in `inline code` become links.
		- To show a layout, an architecture, a UI mockup, a data flow or any spatial idea, draw it in an ```svg block (or a ```mermaid flowchart or
		  sequence diagram). Never draw boxes, frames, trees or arrows with characters such as ┌ ─ │ └ ├ ► ▼ +-- |, in a code block or outside one:
		  they show as plain monospace text, not as a drawing. Before you send, check your reply: if it has such a drawing, replace it with an svg.
		- Give the svg a viewBox about 600-760 wide. Use rect, line, path, circle and text; a text's x and y are its baseline start, with font-size around 13
		  and font-weight 600 for titles. Lokn's chat is dark: use light strokes and text such as #8a8a80 and #e8e6df, and an accent such as #e0853c sparingly.
		- Every tool call shows in the chat under its intent, the `i` field: fill it on every call, shell commands included, with a short phrase
		  such as "Checking the build"; a call without one shows only its tool name.
		""";

	/// <summary>Defines the model an account session starts with; it never sends a prompt, so the model only lets Omp start.</summary>
	/// <remarks>Omp refuses to start RPC for a profile without a default model, which a profile that never signed in has; any built-in model lets it start, signed in to or not.</remarks>
	internal const string AccountSessionModel = "anthropic/claude-sonnet-4-5";

	/// <summary>Defines the Omp settings every chat's Omp loads over its own, as a <c>--config</c> overlay.</summary>
	/// <remarks>Omp continues an active goal between turns only in the run modes <c>goal.continuationModes</c> names, and names only its terminal
	/// interface by default, so over RPC a goal would stop after one turn.</remarks>
	internal const string ChatConfig = """
		goal:
		  continuationModes:
		    - interactive
		    - rpc
		""";

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
		// Omp turns intent tracing off in RPC sessions unless it is set, and the chat shows each tool call's intent beside it.
		if ( Environment.GetEnvironmentVariable( "PI_INTENT_TRACING" ) is null )
			start.Environment["PI_INTENT_TRACING"] = "1";
		foreach ( var argument in new[] { "--mode", "rpc-ui", "--profile", profileName, "--session-dir", Path.GetFullPath( sessionDirectory ), "--continue", "--config", WriteChatConfig() } )
			start.ArgumentList.Add( argument );

		var model = settings.Get( OmpModule.Model, scope ).Trim();
		if ( model.Length > 0 )
		{
			start.ArgumentList.Add( "--model" );
			start.ArgumentList.Add( model );
		}

		// Omp keeps only the last --append-system-prompt, so Lokn's own part and the user's go in one.
		string[] appended = [DisplayPrompt, settings.Get( OmpModule.HostTools ) ? HostToolsPrompt : "", settings.Get( OmpModule.SystemPrompt, scope ).Trim()];
		if ( appended.Any( part => part.Length > 0 ) )
		{
			start.ArgumentList.Add( "--append-system-prompt" );
			start.ArgumentList.Add( string.Join( "\n\n", appended.Where( part => part.Length > 0 ) ) );
		}

		return start;
	}

	/// <summary>Writes <see cref="ChatConfig"/> to its file under the data folder when the file is missing or differs, and returns its path.</summary>
	private static string WriteChatConfig()
	{
		var path = LoknPaths.Combine( "omp", "chat-config.yml" );
		if ( !File.Exists( path ) || File.ReadAllText( path ) != ChatConfig )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			File.WriteAllText( path, ChatConfig );
		}
		return path;
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

