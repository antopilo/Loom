using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Lokn.Modules.Remote;

/// <summary>Accepts any number of plaintext connections on the daemon's named pipe, which only the current user can open.</summary>
/// <param name="pipeName">The pipe name without <c>\\.\pipe\</c>, usually <see cref="ComputePipeName"/>.</param>
/// <param name="accept">Runs each connection; it owns the connection and the pipe closes when it ends.</param>
[SupportedOSPlatform( "windows" )]
public sealed class PipeHost( string pipeName, Func<PipeConnection, Task> accept ) : IAsyncDisposable
{
	/// <summary>Ends the accept loop.</summary>
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>Stores the accept loop once started.</summary>
	private Task? _loop;

	/// <summary>Raised when a connection's handler or the accept loop fails, on a thread-pool thread.</summary>
	public event Action<Exception>? Failed;

	/// <summary>Gets the pipe name.</summary>
	public string PipeName => pipeName;

	/// <summary>Builds the daemon's pipe name, <c>loom-host-&lt;user SID&gt;</c>, with <c>-&lt;hash&gt;</c> for a bare daemon.</summary>
	/// <param name="bareFolder">The bare daemon's folder, or null for the user's daemon.</param>
	public static string ComputePipeName( string? bareFolder = null )
	{
#if LOKN_WINDOWS
		var name = "loom-host-" + WindowsIdentity.GetCurrent().User!.Value;
		return bareFolder is null ? name : name + "-" + ComputeBareHash( bareFolder );
#else
		throw new PlatformNotSupportedException( "The Loom daemon is only supported on Windows." );
#endif
	}

	/// <summary>Builds the daemon's single-instance mutex name, <c>Local\LoomHost</c>, or <c>Local\LoomHost.&lt;hash&gt;</c> for a bare daemon.</summary>
	/// <param name="bareFolder">The bare daemon's folder, or null for the user's daemon.</param>
	public static string ComputeMutexName( string? bareFolder = null ) => bareFolder is null ? @"Local\LoomHost" : @"Local\LoomHost." + ComputeBareHash( bareFolder );

	/// <summary>Hashes a bare daemon's folder: the first 16 lowercase hex digits of SHA-256 over its full upper-case path without a trailing separator.</summary>
	public static string ComputeBareHash( string bareFolder )
	{
		var path = Path.TrimEndingDirectorySeparator( Path.GetFullPath( bareFolder ) ).ToUpperInvariant();
		return Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( path ) ) )[..16];
	}

	/// <summary>Starts accepting connections; the first pipe instance fails when another process already owns the name.</summary>
	/// <exception cref="UnauthorizedAccessException">Another process owns the pipe name.</exception>
	public void Start()
	{
		if ( _loop is not null )
			throw new InvalidOperationException( "The pipe host has already started." );
		var first = CreateServer( true );
		_loop = Task.Run( () => AcceptLoopAsync( first ) );
	}

	/// <summary>Stops accepting; connections already accepted keep running until their handlers end.</summary>
	public async ValueTask DisposeAsync()
	{
		await _stopping.CancelAsync().ConfigureAwait( false );
		if ( _loop is { } loop )
			await loop.ConfigureAwait( false );
	}

	/// <summary>Creates one pipe instance whose ACL grants only the current user.</summary>
	private NamedPipeServerStream CreateServer( bool first )
	{
#if LOKN_WINDOWS
		var security = new PipeSecurity();
		security.AddAccessRule( new PipeAccessRule( WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow ) );
		var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
		return NamedPipeServerStreamAcl.Create( pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options, 0, 0, security );
#else
		throw new PlatformNotSupportedException( "The Loom daemon is only supported on Windows." );
#endif
	}

	/// <summary>Waits for a client on one instance, hands it to the handler, and opens the next instance, until stopped.</summary>
	private async Task AcceptLoopAsync( NamedPipeServerStream server )
	{
		try
		{
			while ( true )
			{
				try
				{
					await server.WaitForConnectionAsync( _stopping.Token ).ConfigureAwait( false );
				}
				catch
				{
					await server.DisposeAsync().ConfigureAwait( false );
					throw;
				}
				_ = RunAsync( new PipeConnection( server ) );
				server = CreateServer( false );
			}
		}
		catch ( OperationCanceledException ) when ( _stopping.IsCancellationRequested )
		{
		}
		catch ( Exception exception )
		{
			Failed?.Invoke( exception );
		}
	}

	/// <summary>Runs the handler of one connection, reporting a failure, and closes the connection when it ends.</summary>
	private async Task RunAsync( PipeConnection connection )
	{
		try
		{
			await accept( connection ).ConfigureAwait( false );
		}
		catch ( Exception exception )
		{
			Failed?.Invoke( exception );
		}
		finally
		{
			await connection.DisposeAsync().ConfigureAwait( false );
		}
	}
}
