using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Lokn.Modules.Remote;

/// <summary>Connects to the daemon's named pipe as the current user.</summary>
public static class PipeDialer
{
	/// <summary>Asks <see cref="OpenProcess"/> for the right to read a process's token.</summary>
	private const int QueryLimitedInformation = 0x1000;

	/// <summary>Asks <see cref="OpenProcessToken"/> for the right to read the token.</summary>
	private const int TokenQuery = 0x0008;

	/// <summary>Selects the token's mandatory label in <see cref="GetTokenInformation"/>.</summary>
	private const int TokenIntegrityLevel = 25;

	/// <summary>Connects to a pipe, or returns null when no daemon answers within <paramref name="timeout"/>, so the caller can start one.</summary>
	/// <param name="pipeName">The pipe name without <c>\\.\pipe\</c>, usually <see cref="PipeHost.ComputePipeName"/>.</param>
	/// <param name="timeout">How long to wait for a free pipe instance.</param>
	/// <param name="cancellationToken">Cancels the wait.</param>
	/// <exception cref="UnauthorizedAccessException">The pipe belongs to another user, or to a process of this user at a lower integrity level.</exception>
	public static async Task<PipeConnection?> ConnectAsync( string pipeName, TimeSpan timeout, CancellationToken cancellationToken )
	{
		// CurrentUserOnly also checks that the server runs as the current user, so another user's process cannot pose as the daemon.
		var client = new NamedPipeClientStream( ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly );
		try
		{
			await client.ConnectAsync( timeout, cancellationToken ).ConfigureAwait( false );
			// A sandboxed or low-integrity process of the same user passes CurrentUserOnly; it must not pose as the daemon and receive tool calls.
			if ( OperatingSystem.IsWindows() )
				CheckServerIntegrity( client );
			return new PipeConnection( client );
		}
		catch ( TimeoutException )
		{
			await client.DisposeAsync().ConfigureAwait( false );
			return null;
		}
		catch
		{
			await client.DisposeAsync().ConfigureAwait( false );
			throw;
		}
	}

	/// <summary>Checks that the pipe's server runs at this process's integrity level or above.</summary>
	/// <exception cref="UnauthorizedAccessException">The server runs lower, or its level cannot be read.</exception>
	[SupportedOSPlatform( "windows" )]
	private static void CheckServerIntegrity( NamedPipeClientStream client )
	{
		if ( !GetNamedPipeServerProcessId( client.SafePipeHandle, out var serverId ) )
			throw new UnauthorizedAccessException( "Loom cannot tell which process serves the daemon's pipe.", new Win32Exception() );
		if ( serverId == (uint)Environment.ProcessId )
			return;

		using var server = OpenProcess( QueryLimitedInformation, false, serverId );
		if ( server.IsInvalid )
			throw new UnauthorizedAccessException( "Loom cannot inspect the process that serves the daemon's pipe.", new Win32Exception() );
		using var own = OpenProcess( QueryLimitedInformation, false, (uint)Environment.ProcessId );
		if ( ReadIntegrity( server ) < ReadIntegrity( own ) )
			throw new UnauthorizedAccessException( "The daemon's pipe is served by a process with less rights than Loom; it was refused." );
	}

	/// <summary>Reads a process's integrity level, the last part of its mandatory label's SID, such as 8192 for medium.</summary>
	/// <exception cref="UnauthorizedAccessException">The level cannot be read.</exception>
	[SupportedOSPlatform( "windows" )]
	private static int ReadIntegrity( SafeProcessHandle process )
	{
		if ( !OpenProcessToken( process, TokenQuery, out var token ) )
			throw new UnauthorizedAccessException( "Loom cannot read a process's integrity level.", new Win32Exception() );
		using ( token )
		{
			_ = GetTokenInformation( token, TokenIntegrityLevel, 0, 0, out var size );
			var buffer = Marshal.AllocHGlobal( size );
			try
			{
				if ( !GetTokenInformation( token, TokenIntegrityLevel, buffer, size, out _ ) )
					throw new UnauthorizedAccessException( "Loom cannot read a process's integrity level.", new Win32Exception() );
				// TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field points to the label's SID, S-1-16-<level>.
				var sid = new SecurityIdentifier( Marshal.ReadIntPtr( buffer ) ).Value;
				return int.Parse( sid[(sid.LastIndexOf( '-' ) + 1)..], CultureInfo.InvariantCulture );
			}
			finally
			{
				Marshal.FreeHGlobal( buffer );
			}
		}
	}

	/// <summary>Gets the id of the process that serves a pipe the client connected to.</summary>
	[SupportedOSPlatform( "windows" )]
	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool GetNamedPipeServerProcessId( SafePipeHandle pipe, out uint processId );

	/// <summary>Opens a process with the given rights.</summary>
	[SupportedOSPlatform( "windows" )]
	[DllImport( "kernel32.dll", SetLastError = true )]
	private static extern SafeProcessHandle OpenProcess( int access, [MarshalAs( UnmanagedType.Bool )] bool inherit, uint processId );

	/// <summary>Opens a process's access token.</summary>
	[SupportedOSPlatform( "windows" )]
	[DllImport( "advapi32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool OpenProcessToken( SafeProcessHandle process, int access, out SafeAccessTokenHandle token );

	/// <summary>Reads a piece of a token's information into a buffer, or its size when the buffer is too small.</summary>
	[SupportedOSPlatform( "windows" )]
	[DllImport( "advapi32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool GetTokenInformation( SafeAccessTokenHandle token, int kind, nint buffer, int length, out int needed );
}
