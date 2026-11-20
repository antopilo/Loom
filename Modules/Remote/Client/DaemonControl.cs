using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Lokn.Core;
using Microsoft.Win32;

namespace Lokn.Modules.Remote;

/// <summary>Starts and stops this PC's daemon, <c>Loom.exe --host</c>, and keeps its sign-in entry in the user's Run key (docs/remote.md, Roles).</summary>
[SupportedOSPlatform( "windows" )]
internal static class DaemonControl
{
	/// <summary>Defines the Run key the sign-in entry lives in.</summary>
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

	/// <summary>Defines the sign-in entry's value name; the shell's HostRunner removes the same value on uninstall.</summary>
	private const string RunValue = "LoomHost";

	/// <summary>Defines the daemon's stop event name, before a bare folder's hash; the same as the shell's HostRunner.</summary>
	private const string StopEventName = @"Local\LoomHost.stop";

	/// <summary>Defines the daemon's single-instance mutex name, before a bare folder's hash; the same as the shell's HostRunner.</summary>
	private const string MutexName = @"Local\LoomHost";

	/// <summary>Gets the bare folder this process shares with its daemon, or null for the user's daemon.</summary>
	public static string? BareFolder => LoknPaths.IsCustom ? LoknPaths.DataRoot : null;

	/// <summary>Gets the daemon's pipe name for this process.</summary>
	public static string PipeName => PipeHost.ComputePipeName( BareFolder );

	/// <summary>Gets the daemon's single-instance mutex name for this process: <c>Local\LoomHost</c>, with the bare folder's hash in a bare instance.</summary>
	internal static string MutexObjectName => ComputeName( MutexName );

	/// <summary>Gets the name of the event that stops this process's daemon: <c>Local\LoomHost.stop</c>, with the bare folder's hash in a bare instance.</summary>
	internal static string StopEventObjectName => ComputeName( StopEventName );

	/// <summary>Gets whether this process may change the daemon's sign-in entry: never in a bare instance, since the entry is the user's own daemon's.</summary>
	internal static bool CanChangeLoginItem => !LoknPaths.IsCustom;

	/// <summary>Gets whether this process's daemon runs, which holds its mutex.</summary>
	public static bool IsRunning
	{
		get
		{
			if ( !Mutex.TryOpenExisting( MutexObjectName, out var mutex ) )
				return false;
			mutex.Dispose();
			return true;
		}
	}

	/// <summary>Starts the daemon hidden and detached from this process, with this process's executable or another Loom executable.</summary>
	/// <param name="executable">The Loom executable to start, or null for this process's own; checks start the built <c>Loom.exe</c> this way.</param>
	/// <returns>The started process; the caller disposes its handle, while the daemon outlives this window.</returns>
	/// <exception cref="InvalidOperationException">The process has no executable path.</exception>
	/// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
	public static Process Start( string? executable = null )
	{
		var info = new ProcessStartInfo( executable ?? Environment.ProcessPath ?? throw new InvalidOperationException( "Loom cannot find its own executable to start the daemon." ) )
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
		};
		foreach ( var argument in BuildArguments() )
			info.ArgumentList.Add( argument );

		return Process.Start( info ) ?? throw new InvalidOperationException( "The daemon's process did not start." );
	}

	/// <summary>Asks the running daemon to stop through its stop event; does nothing when none runs.</summary>
	public static void Stop()
	{
		if ( !EventWaitHandle.TryOpenExisting( StopEventObjectName, out var stop ) )
			return;

		using ( stop )
			stop.Set();
	}

	/// <summary>Asks the running daemon to stop and waits until it released its mutex, which it does once it saved and shut down.</summary>
	/// <param name="timeout">How long to wait.</param>
	/// <returns>Whether no daemon runs any more.</returns>
	public static bool StopAndWait( TimeSpan timeout )
	{
		Stop();
		if ( !Mutex.TryOpenExisting( MutexObjectName, out var mutex ) )
			return true;

		using ( mutex )
		{
			try
			{
				if ( !mutex.WaitOne( timeout ) )
					return false;
			}
			catch ( AbandonedMutexException )
			{
				// The daemon ended without releasing it; this thread owns it now.
			}
			mutex.ReleaseMutex();
			return true;
		}
	}

	/// <summary>Adds or removes the daemon's sign-in entry, <c>"&lt;exe&gt;" --host</c>, in the user's Run key; a bare instance never touches it
	/// (<see cref="CanChangeLoginItem"/>).</summary>
	public static void UpdateLoginItem( bool isOn )
	{
		if ( !CanChangeLoginItem )
			return;

		using var key = Registry.CurrentUser.CreateSubKey( RunKey );
		if ( isOn && Environment.ProcessPath is { } path )
			key.SetValue( RunValue, string.Join( ' ', new[] { path }.Concat( BuildArguments() ).Select( QuoteArgument ) ) );
		else if ( key.GetValue( RunValue ) is not null )
			key.DeleteValue( RunValue );
	}

	/// <summary>Quotes one command-line argument the way the C runtime parses it back: quotes around one with a space, tab or quote, quotes inside
	/// escaped, and backslashes before a quote or the closing quote doubled.</summary>
	internal static string QuoteArgument( string argument )
	{
		if ( argument.Length > 0 && argument.IndexOfAny( [' ', '\t', '"'] ) < 0 )
			return argument;

		var quoted = new StringBuilder( "\"" );
		var backslashes = 0;
		foreach ( var character in argument )
		{
			if ( character == '\\' )
			{
				backslashes++;
				continue;
			}
			quoted.Append( '\\', character == '"' ? backslashes * 2 + 1 : backslashes );
			backslashes = 0;
			quoted.Append( character );
		}
		quoted.Append( '\\', backslashes * 2 );
		return quoted.Append( '"' ).ToString();
	}

	/// <summary>Builds a daemon object name, with the bare folder's hash when there is one.</summary>
	private static string ComputeName( string name ) => BareFolder is { } bare ? $"{name}.{PipeHost.ComputeBareHash( bare )}" : name;

	/// <summary>Builds the daemon's arguments: <c>--host</c>, and <c>--bare &lt;folder&gt;</c> for a bare folder, so a bare instance only ever starts its own
	/// daemon.</summary>
	internal static string[] BuildArguments() => BareFolder is { } bare ? ["--host", "--bare", bare] : ["--host"];
}
