using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Lokn.Core;
using Microsoft.Win32;

namespace Lokn;

/// <summary>Runs Loom as the headless daemon, <c>Loom.exe --host [--bare &lt;folder&gt;]</c>: no window, only the modules that run chats, until told to stop.</summary>
/// <remarks>
/// One daemon runs per user, or per bare folder, held by the mutex <c>Local\LoomHost[.&lt;hash&gt;]</c> (docs/remote.md); a second one exits at once.
/// The main thread becomes the daemon's UI thread: a queue loop that runs every posted action, module start and stop, command and tool, with a
/// <see cref="SynchronizationContext"/> that brings awaits back to it. The daemon stops when the event <c>Local\LoomHost.stop[.&lt;hash&gt;]</c> is set
/// (<c>Loom.exe --host --stop</c>, or Stop daemon in its menu in the notification area), on Ctrl+C or a console close, when the Windows session
/// ends, or when the process exits, and exits on its own when its Remote module does not start or a shutdown hangs. While the Remote module's
/// status asks for it (<c>remote.tray</c>), <see cref="HostTray"/> shows the daemon's icon near the clock. It logs to <c>%TEMP%\Lokn\host.log</c>,
/// or <c>host.log</c> in the bare folder.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal sealed class HostRunner
{
	/// <summary>Defines the daemon's single-instance mutex name, before the bare folder's hash.</summary>
	private const string MutexName = @"Local\LoomHost";

	/// <summary>Defines the name of the event that stops the daemon, before the bare folder's hash.</summary>
	private const string StopEventName = @"Local\LoomHost.stop";

	/// <summary>Defines how long a stop request waits for the daemon to finish shutting down, in milliseconds.</summary>
	private const int StopTimeoutMilliseconds = 10000;

	/// <summary>Defines the Run key the daemon's sign-in entry lives in.</summary>
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

	/// <summary>Defines the sign-in entry's value name; the same as <c>DaemonControl</c> in Modules/Remote, which writes it.</summary>
	private const string RunValue = "LoomHost";

	/// <summary>Defines the module the daemon exists for: without it there is no pipe.</summary>
	private const string RemoteModuleId = "remote";

	/// <summary>Defines the environment variable with which checks make a bare daemon fail at once: an integer there is its exit code.</summary>
	private const string TestExitVariable = "LOKN_TEST_HOST_EXIT";

	/// <summary>Defines the only modules the daemon loads (docs/remote.md, Roles). Browser loads without web views: its tools run in an attached window.</summary>
	private static readonly HashSet<string> AllowedModules = new( ["markdown", "omp", "projects", "workspaces", "git", "conversations", "browser", "remote"], StringComparer.Ordinal );

	/// <summary>Stores the actions waiting to run on the loop thread; adding completes once the daemon shut down.</summary>
	private readonly BlockingCollection<Action> _queue = new();

	/// <summary>Set once the loop finished and the application shut down.</summary>
	private readonly ManualResetEventSlim _stopped = new();

	/// <summary>Stores the bare folder, or null for the user's daemon.</summary>
	private readonly string? _bare;

	/// <summary>Stores the log file.</summary>
	private readonly string _logPath;

	/// <summary>Stores the managed id of the loop thread.</summary>
	private int _loopThread;

	/// <summary>Stores 1 once a stop was requested; changed with <see cref="Interlocked"/>.</summary>
	private int _isStopping;

	/// <summary>Stores the hosted application once created on the loop thread.</summary>
	private LoknApplication? _app;

	/// <summary>Stores the module host once created on the loop thread.</summary>
	private ModuleHost? _modules;

	/// <summary>Stores the follower that reloads <c>settings.json</c> when Loom on this PC saves it, or null.</summary>
	private IDisposable? _settingsWatch;

	/// <summary>Ends the process when a requested shutdown does not finish in time, such as when a module's stop hangs.</summary>
	private Timer? _forceExit;

	/// <summary>Stores the event that stops the daemon while it serves; the notification-area menu sets it as <c>--stop</c> does.</summary>
	private EventWaitHandle? _stopEvent;

	/// <summary>Stores the icon in the notification area once the status asked for it; on the loop thread.</summary>
	private HostTray? _tray;

	/// <summary>Creates a daemon for the data folder <see cref="LoknPaths.DataRoot"/> names: the user's, or a bare folder.</summary>
	/// <param name="bare">The bare folder, or null for the user's data.</param>
	private HostRunner( string? bare )
	{
		_bare = bare;
		_logPath = Path.Combine( LoknPaths.TempRoot, "host.log" );
	}

	/// <summary>Runs the daemon until it is told to stop, or with <paramref name="isStop"/> stops the running one; returns at once when another daemon holds
	/// the mutex. A bare daemon whose environment holds an integer in <c>LOKN_TEST_HOST_EXIT</c> logs it and exits with it at once, before the mutex, as
	/// the daemon fallback check needs a daemon that keeps failing. <c>Main</c> moved a bare daemon's data folder to <paramref name="bare"/> already.</summary>
	/// <param name="bare">The bare folder, or null for the user's daemon.</param>
	/// <param name="isStop">Whether to stop the running daemon instead of running one.</param>
	internal static void Run( string? bare, bool isStop )
	{
		if ( isStop )
		{
			Stop( bare );
			return;
		}

		if ( bare is not null && int.TryParse( Environment.GetEnvironmentVariable( TestExitVariable ), System.Globalization.NumberStyles.Integer,
			System.Globalization.CultureInfo.InvariantCulture, out var exitCode ) )
		{
			LogFile.Append( Path.Combine( LoknPaths.TempRoot, "host.log" ), $"EXIT test hook {exitCode}" );
			Environment.Exit( exitCode );
		}

		new HostRunner( bare ).Serve();
	}

	/// <summary>Asks the running daemon of the user or of a bare folder to stop, and waits a while for it to exit; does nothing when none runs.</summary>
	/// <param name="bare">The bare folder, or null for the user's daemon.</param>
	internal static void Stop( string? bare )
	{
		if ( !EventWaitHandle.TryOpenExisting( ComputeStopEventName( bare ), out var stop ) )
			return;

		using ( stop )
			stop.Set();

		if ( !Mutex.TryOpenExisting( ComputeMutexName( bare ), out var mutex ) )
			return;

		// The mutex is free once the daemon shut down, so taking it waits for the daemon rather than for a fixed time.
		using ( mutex )
		{
			try
			{
				if ( mutex.WaitOne( StopTimeoutMilliseconds ) )
					mutex.ReleaseMutex();
			}
			catch ( AbandonedMutexException )
			{
				mutex.ReleaseMutex();
			}
		}
	}

	/// <summary>Removes the daemon's sign-in entry, <c>LoomHost</c> in the user's Run key, as uninstalling does; the Remote module writes it.</summary>
	internal static void RemoveLoginItem()
	{
		using var key = Registry.CurrentUser.OpenSubKey( RunKey, writable: true );
		if ( key?.GetValue( RunValue ) is not null )
			key.DeleteValue( RunValue );
	}

	/// <summary>Gets the file that records, between an app update's two hooks, that the user's daemon ran before the update.</summary>
	internal static string UpdateMarkerPath => LoknPaths.Combine( "daemon-update.flag" );

	/// <summary>Gets whether the daemon of the user or of a bare folder runs, which holds its mutex.</summary>
	/// <param name="bare">The bare folder, or null for the user's daemon.</param>
	internal static bool IsRunning( string? bare )
	{
		if ( !Mutex.TryOpenExisting( ComputeMutexName( bare ), out var mutex ) )
			return false;
		mutex.Dispose();
		return true;
	}

	/// <summary>Before an app update replaces the files: records whether the daemon runs, then stops it gracefully, so it saves its chats and lets go
	/// of the installed files; <see cref="StartAfterUpdate"/> starts it again from the new build.</summary>
	/// <param name="bare">The bare folder, or null for the user's daemon.</param>
	/// <param name="marker">The marker file, or null for <see cref="UpdateMarkerPath"/>.</param>
	internal static void StopForUpdate( string? bare = null, string? marker = null )
	{
		marker ??= UpdateMarkerPath;
		if ( IsRunning( bare ) )
		{
			try
			{
				Directory.CreateDirectory( Path.GetDirectoryName( marker )! );
				File.WriteAllText( marker, Environment.ProcessId.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
			}
			catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
			{
				// Without the marker the daemon still starts again when its sign-in entry is on.
			}
		}
		Stop( bare );
	}

	/// <summary>After an app update: starts the daemon again from the new build when it ran before the update (<see cref="StopForUpdate"/>) or its
	/// sign-in entry is on; does nothing otherwise.</summary>
	/// <param name="executable">The updated Loom.exe.</param>
	/// <param name="bare">The bare folder, or null for the user's daemon.</param>
	/// <param name="marker">The marker file, or null for <see cref="UpdateMarkerPath"/>.</param>
	/// <returns>Whether it started the daemon.</returns>
	internal static bool StartAfterUpdate( string executable, string? bare = null, string? marker = null )
	{
		marker ??= UpdateMarkerPath;
		var wasRunning = File.Exists( marker );
		if ( wasRunning )
		{
			try
			{
				File.Delete( marker );
			}
			catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
			{
			}
		}
		if ( !ShouldStartAfterUpdate( wasRunning, bare is null && IsLoginItem() ) || IsRunning( bare ) )
			return false;

		var info = new ProcessStartInfo( executable ) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
		info.ArgumentList.Add( "--host" );
		if ( bare is not null )
		{
			info.ArgumentList.Add( "--bare" );
			info.ArgumentList.Add( bare );
		}
		Process.Start( info )?.Dispose();
		return true;
	}

	/// <summary>Decides whether the daemon starts again after an app update: when it ran before it, or its sign-in entry is on.</summary>
	internal static bool ShouldStartAfterUpdate( bool wasRunning, bool isLoginItem ) => wasRunning || isLoginItem;

	/// <summary>Reads whether the daemon's sign-in entry, <c>LoomHost</c> in the user's Run key, exists.</summary>
	internal static bool IsLoginItem()
	{
		using var key = Registry.CurrentUser.OpenSubKey( RunKey );
		return key?.GetValue( RunValue ) is not null;
	}

	/// <summary>Builds the daemon's single-instance mutex name: <c>Local\LoomHost</c>, or <c>Local\LoomHost.&lt;hash&gt;</c> for a bare folder.</summary>
	/// <param name="bare">The bare folder, or null for the user's daemon.</param>
	internal static string ComputeMutexName( string? bare ) => bare is null ? MutexName : $"{MutexName}.{ComputeBareHash( bare )}";

	/// <summary>Builds the name of the event that stops the daemon: <c>Local\LoomHost.stop</c>, or <c>Local\LoomHost.stop.&lt;hash&gt;</c> for a bare
	/// folder.</summary>
	/// <param name="bare">The bare folder, or null for the user's daemon.</param>
	internal static string ComputeStopEventName( string? bare ) => bare is null ? StopEventName : $"{StopEventName}.{ComputeBareHash( bare )}";

	/// <summary>Hashes a bare folder: the first 16 lowercase hex digits of SHA-256 over its full upper-case path without a trailing separator.</summary>
	/// <remarks>The same formula as <c>PipeHost.ComputeBareHash</c> in Modules/Remote, repeated because the shell never references a module; keep the two equal.</remarks>
	internal static string ComputeBareHash( string bare )
	{
		var path = Path.TrimEndingDirectorySeparator( Path.GetFullPath( bare ) ).ToUpperInvariant();
		return Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( path ) ) )[..16];
	}

	/// <summary>Takes the single-instance mutex and runs the daemon while holding it.</summary>
	/// <remarks>The stop event exists before the mutex is taken: <see cref="IsRunning"/> reads the mutex, so a stop asked for as soon as the daemon
	/// reads as running must find the event, and an auto-reset event set before the daemon waits on it stays set until it does.</remarks>
	private void Serve()
	{
		using var stopEvent = new EventWaitHandle( false, EventResetMode.AutoReset, ComputeStopEventName( _bare ) );
		using var mutex = new Mutex( false, ComputeMutexName( _bare ) );
		try
		{
			if ( !mutex.WaitOne( 0 ) )
				return;
		}
		catch ( AbandonedMutexException )
		{
			// A daemon that crashed left the mutex behind; this one now owns it.
		}

		try
		{
			ServeOwned( stopEvent );
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	/// <summary>Starts the modules, runs the loop until a stop request shut the application down, then reports it stopped.</summary>
	/// <param name="stopEvent">The stop event, which <see cref="Stop"/> sets.</param>
	private void ServeOwned( EventWaitHandle stopEvent )
	{
		Log( $"START Loom {typeof( HostRunner ).Assembly.GetName().Version?.ToString( 3 )}, process {Environment.ProcessId}{( _bare is null ? "" : $", bare {_bare}" )}" );
		AppDomain.CurrentDomain.UnhandledException += ( _, failure ) => Log( $"CRASH {failure.ExceptionObject}" );
		TaskScheduler.UnobservedTaskException += ( _, failure ) => Log( $"UNOBSERVED {failure.Exception.GetBaseException()}" );
		AppDomain.CurrentDomain.ProcessExit += ( _, _ ) => StopAtExit();

		_stopEvent = stopEvent;
		var stopWait = ThreadPool.RegisterWaitForSingleObject( stopEvent, ( _, _ ) => RequestStop( "stop event" ), null, Timeout.Infinite, executeOnlyOnce: true );
		// Without a console these never fire; a daemon started from one stops cleanly on Ctrl+C, Ctrl+Break or closing it.
		using var interrupt = PosixSignalRegistration.Create( PosixSignal.SIGINT, HandleSignal );
		using var quit = PosixSignalRegistration.Create( PosixSignal.SIGQUIT, HandleSignal );
		using var terminate = PosixSignalRegistration.Create( PosixSignal.SIGTERM, HandleSignal );
		using var hangUp = PosixSignalRegistration.Create( PosixSignal.SIGHUP, HandleSignal );
		SessionEndWatcher.Start( () => StopAndWait( "session end" ) );

		_loopThread = Environment.CurrentManagedThreadId;
		SynchronizationContext.SetSynchronizationContext( new LoopContext( this ) );
		try
		{
			Start();
		}
		catch ( Exception error )
		{
			Log( $"FAILED start: {error}" );
			RequestStop( "failed start" );
		}

		foreach ( var action in _queue.GetConsumingEnumerable() )
			Execute( action );

		stopWait.Unregister( null );
		Log( "STOPPED" );
		_stopped.Set();
		_forceExit?.Dispose();
	}

	/// <summary>Creates the application on the loop thread, binds its dispatchers to the loop and loads the allowed modules; a daemon whose Remote
	/// module did not start has no pipe and no purpose, so it logs why and stops, rather than holding the mutex and leaving Loom waiting.</summary>
	private void Start()
	{
		// Before anything reads or writes the data, back it up when this version first runs on it; Loom does the same under the same mutex, so
		// whichever starts first backs up once. A restore the user asked for waits for the desktop, which stops this daemon first.
		var upgrade = DataUpgrade.Prepare( LoknPaths.DataRoot, typeof( HostRunner ).Assembly.GetName().Version?.ToString( 3 ) ?? "1.0.0", canRestore: false );
		if ( upgrade.BackupFolder is { } backup )
			Log( $"BACKUP {upgrade.PreviousVersion} to {backup}" );
		if ( upgrade.Error is { } upgradeError )
			Log( $"FAILED backup: {upgradeError.Message}" );
		// The workspace file stays the desktop's and notifications stay in memory: the daemon has no layout, and its feed must not overwrite the desktop's.
		// The browser tabs are the session's, which the daemon owns while it runs; Loom on this PC mirrors them and never writes the file meanwhile.
		var app = new LoknApplication( SettingsService.DefaultPath, browserTabsPath: BrowserTabs.DefaultPath ) { IsHost = true };
		_app = app;
		app.Changed += LogError;
		app.BindDispatcher( Post );
		app.Commands.BindDispatcher( operation => InvokeAsync( operation ) );
		app.Tools.BindDispatcher( operation => InvokeAsync( operation ) );
		// The settings file is shared with Loom on this PC; its changes, such as the model or the network mode, reach the daemon's modules.
		_settingsWatch = app.Settings.Watch( Post );
		// The daemon runs the allowed modules the window's Settings › Modules turns on, from the same module states file (bare: its folder), and
		// follows that file live; its Remote module, with the modules it needs, always runs. It reloads rebuilt and updated modules as the window
		// does, but only once no chat's agent works; the window applies staged updates, which the daemon then picks up the same way.
		_modules = new ModuleHost( app, ModuleHost.DefaultDirectory, _bare is null ? Installation.ModuleUpdatesDirectory : null, _logPath, ModuleStates.Create( _bare is not null ),
			new( AllowedModules, new HashSet<string>( [RemoteModuleId], StringComparer.Ordinal ) ) );
		_modules.Start();
		app.MarkStarted();
		Log( $"READY modules: {string.Join( ", ", app.Modules.Select( module => module.Id ) )}" );
		if ( !app.Modules.Any( module => module.Id == RemoteModuleId ) )
		{
			Log( $"FAILED the {RemoteModuleId} module did not start: {app.LastError ?? "see the lines above"}" );
			RequestStop( "no remote module" );
			return;
		}

		app.HostStatusChanged += UpdateTray;
		UpdateTray();
	}

	/// <summary>Shows, changes or removes the icon in the notification area for the status the Remote module published; on the loop thread.
	/// The icon's thread starts the first time the status asks for the icon and runs until the daemon shuts down.</summary>
	private void UpdateTray()
	{
		var status = _app?.HostStatus;
		if ( _tray is null && status is { IsTrayShown: true } && Volatile.Read( ref _isStopping ) == 0 )
			_tray = new HostTray( new( OpenLoom, () => _bare is null && IsLoginItem(), SetStartAtLogin, () => _stopEvent?.Set(), Log ) );
		_tray?.Update( status );
	}

	/// <summary>Opens Loom on this PC, for the bare folder when the daemon is bare, optionally following a link; an open Loom gets the request forwarded
	/// and comes forward. Runs on the icon's thread.</summary>
	/// <param name="link">The link to follow, such as Remote access's, or null.</param>
	private void OpenLoom( string? link )
	{
		if ( Environment.ProcessPath is not { } executable )
		{
			Log( "FAILED open Loom: the daemon has no executable path" );
			return;
		}

		var info = new ProcessStartInfo( executable ) { UseShellExecute = false };
		if ( _bare is not null )
		{
			info.ArgumentList.Add( "--bare" );
			info.ArgumentList.Add( _bare );
		}
		if ( link is not null )
		{
			info.ArgumentList.Add( AttentionNotifier.OpenLinkArgument );
			info.ArgumentList.Add( link );
		}

		try
		{
			Process.Start( info )?.Dispose();
		}
		catch ( System.ComponentModel.Win32Exception error )
		{
			Log( $"FAILED open Loom: {error.Message}" );
		}
	}

	/// <summary>Turns the sign-in entry on or off through the Remote module on the loop thread, as the Remote access panel does.</summary>
	private void SetStartAtLogin( bool isOn ) => Post( () => _app?.Services.Get<IHostLoginItem>()?.Set( isOn ) );

	/// <summary>Queues an action for the loop thread.</summary>
	/// <returns>False once the daemon shut down and runs nothing more.</returns>
	private bool Post( Action action )
	{
		try
		{
			_queue.Add( action );
			return true;
		}
		catch ( InvalidOperationException )
		{
			return false;
		}
	}

	/// <summary>Runs an async operation on the loop thread and completes with its result, its failure, or cancelled when the daemon has shut down.</summary>
	private Task<T> InvokeAsync<T>( Func<Task<T>> operation )
	{
		var completion = new TaskCompletionSource<T>( TaskCreationOptions.RunContinuationsAsynchronously );
		if ( !Post( () => _ = ForwardAsync( operation, completion ) ) )
			completion.TrySetCanceled();
		return completion.Task;
	}

	/// <summary>Awaits an operation on the loop thread and copies its outcome to a completion.</summary>
	private static async Task ForwardAsync<T>( Func<Task<T>> operation, TaskCompletionSource<T> completion )
	{
		try
		{
			completion.TrySetResult( await operation() );
		}
		catch ( OperationCanceledException )
		{
			completion.TrySetCanceled();
		}
		catch ( Exception error )
		{
			completion.TrySetException( error );
		}
	}

	/// <summary>Runs one queued action, containing its failure as an application error so the loop keeps running.</summary>
	private void Execute( Action action )
	{
		try
		{
			action();
		}
		catch ( Exception error )
		{
			Log( $"ERROR Host: {error}" );
			_app?.ReportError( "Host", error );
		}
	}

	/// <summary>Writes the application's latest error to the log, then clears it, so the same error reported again is logged again.</summary>
	private void LogError()
	{
		if ( _app?.LastError is not { } error )
			return;

		Log( $"ERROR {error}" );
		_app.ClearError();
	}

	/// <summary>Queues the shutdown once: the module host stops watching, the application stops its modules on the loop thread, and the loop ends.
	/// When that has not finished after <see cref="StopTimeoutMilliseconds"/>, such as with a hung loop or module, the process exits anyway.</summary>
	/// <param name="reason">What asked to stop, for the log.</param>
	private void RequestStop( string reason )
	{
		if ( Interlocked.Exchange( ref _isStopping, 1 ) != 0 )
			return;

		Log( $"STOP {reason}" );
		_forceExit = new Timer( _ =>
		{
			if ( _stopped.IsSet )
				return;
			Log( $"TIMEOUT {reason}: the shutdown did not finish within {StopTimeoutMilliseconds / 1000} s; exiting" );
			// The exit handler waits for this signal; the process is ending either way.
			_stopped.Set();
			Environment.Exit( 1 );
		}, null, StopTimeoutMilliseconds, Timeout.Infinite );
		Post( () =>
		{
			try
			{
				ShutDown();
			}
			finally
			{
				_queue.CompleteAdding();
			}
		} );
	}

	/// <summary>Removes the notification-area icon, stops watching module and settings files and shuts the application down; runs on the loop thread.</summary>
	private void ShutDown()
	{
		_app?.HostStatusChanged -= UpdateTray;
		_tray?.Dispose();
		_tray = null;
		_settingsWatch?.Dispose();
		_modules?.Stop();
		_app?.Shutdown();
	}

	/// <summary>Requests a stop and waits for the shutdown to finish, for callers after which the process ends.</summary>
	/// <param name="reason">What asked to stop, for the log.</param>
	private void StopAndWait( string reason )
	{
		RequestStop( reason );
		if ( !_stopped.Wait( StopTimeoutMilliseconds ) )
			Log( $"TIMEOUT {reason}: the shutdown did not finish in time" );
	}

	/// <summary>Shuts down before the process exits, unless the loop already did; a process exit asked on the loop thread itself shuts down in place.</summary>
	private void StopAtExit()
	{
		if ( _stopped.IsSet )
			return;

		if ( Environment.CurrentManagedThreadId != _loopThread )
		{
			StopAndWait( "process exit" );
			return;
		}

		Log( "STOP process exit" );
		ShutDown();
		Log( "STOPPED" );
	}

	/// <summary>Stops on a console signal; a console close or system shutdown ends the process after the handler, so it waits for the shutdown.</summary>
	private void HandleSignal( PosixSignalContext context )
	{
		context.Cancel = true;
		if ( context.Signal is PosixSignal.SIGTERM or PosixSignal.SIGHUP )
			StopAndWait( context.Signal.ToString() );
		else
			RequestStop( context.Signal.ToString() );
	}

	/// <summary>Appends one timestamped line to the log; a logging failure never stops the daemon.</summary>
	private void Log( string line ) => LogFile.Append( _logPath, line );

	/// <summary>Brings awaits and posted callbacks back to the daemon's loop thread.</summary>
	/// <param name="host">The daemon whose loop runs the callbacks.</param>
	private sealed class LoopContext( HostRunner host ) : SynchronizationContext
	{
		/// <summary>Queues a callback on the loop; it is dropped once the daemon shut down.</summary>
		public override void Post( SendOrPostCallback callback, object? state ) => host.Post( () => callback( state ) );

		/// <summary>Runs a callback on the loop and waits for it; on the loop thread it runs at once.</summary>
		/// <exception cref="InvalidOperationException">The daemon has shut down.</exception>
		public override void Send( SendOrPostCallback callback, object? state )
		{
			if ( Environment.CurrentManagedThreadId == host._loopThread )
			{
				callback( state );
				return;
			}

			using var done = new ManualResetEventSlim();
			Exception? failure = null;
			var isQueued = host.Post( () =>
			{
				try
				{
					callback( state );
				}
				catch ( Exception error )
				{
					failure = error;
				}
				finally
				{
					done.Set();
				}
			} );
			if ( !isQueued )
				throw new InvalidOperationException( "The Loom daemon has shut down." );

			done.Wait();
			if ( failure is not null )
				ExceptionDispatchInfo.Throw( failure );
		}

		/// <summary>Returns this context; it holds no per-operation state.</summary>
		public override SynchronizationContext CreateCopy() => this;
	}
}

/// <summary>Appends timestamped lines to the daemon's and the module host's logs, one writer at a time in this process, starting a new file once
/// one passes 1 MiB (the previous one is kept as <c>.1</c>).</summary>
internal static class LogFile
{
	/// <summary>Defines the size at which a log starts over.</summary>
	private const long MaxBytes = 1024 * 1024;

	/// <summary>Serializes appends to every log of this process.</summary>
	private static readonly Lock Gate = new();

	/// <summary>Appends one timestamped line; a logging failure never stops the caller.</summary>
	/// <param name="path">The log file.</param>
	/// <param name="line">The line, without the timestamp.</param>
	internal static void Append( string path, string line )
	{
		lock ( Gate )
		{
			try
			{
				Directory.CreateDirectory( Path.GetDirectoryName( path )! );
				var file = new FileInfo( path );
				if ( file.Exists && file.Length > MaxBytes )
					File.Move( path, path + ".1", overwrite: true );
				File.AppendAllText( path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}" );
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
			{
			}
		}
	}
}
