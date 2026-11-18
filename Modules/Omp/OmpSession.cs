using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Owns a profile-pinned Omp RPC child and its ordered JSONL transport.</summary>
public sealed class OmpSession
{
	/// <summary>Defines the startup deadline in seconds.</summary>
	private const int StartupSeconds = 60;

	/// <summary>Owns the child process and redirected native pipe handles.</summary>
	private readonly Process _process;

	/// <summary>Serializes writes so concurrent requests remain whole JSONL frames.</summary>
	private readonly SemaphoreSlim _writes = new( 1 );

	/// <summary>Correlates concurrently outstanding responses by request identifier.</summary>
	private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

	/// <summary>Signals the initial advertised protocol capabilities.</summary>
	private readonly TaskCompletionSource<JsonElement> _ready = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Retains the output drain until the child exits.</summary>
	private Task? _reader;

	/// <summary>Retains the stderr drain so startup failures include their actual diagnostic.</summary>
	private Task? _diagnostics;

	/// <summary>Retains recent startup diagnostics without exposing credential stores.</summary>
	private string _diagnostic = "";

	/// <summary>Serializes panel-close and module-shutdown access to the shared cleanup task.</summary>
	private readonly object _stopGate = new();

	/// <summary>Retains cleanup completion so every stop caller waits for the same child release.</summary>
	private Task? _stopTask;

	/// <summary>Suppresses transport-failure notices after intentional shutdown begins.</summary>
	private int _stopped;

	/// <summary>Gets the captured profile, which never follows later titlebar toggles.</summary>
	public SettingsProfile Profile
	{
		get;
	}

	/// <summary>Delivers raw cloned RPC frames on the output-reader thread; UI consumers must post.</summary>
	public event Action<JsonElement>? FrameReceived;

	/// <summary>Constructs a process before launching it.</summary>
	internal OmpSession( SettingsProfile profile, ProcessStartInfo start )
	{
		// Omp writes UTF-8 JSONL; Windows console encodings corrupt raw emoji and punctuation.
		start.StandardInputEncoding = new UTF8Encoding( encoderShouldEmitUTF8Identifier: false );
		start.StandardOutputEncoding = new UTF8Encoding( encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true );
		start.StandardErrorEncoding = new UTF8Encoding( encoderShouldEmitUTF8Identifier: false );
		Profile = profile;
		_process = new()
		{
			StartInfo = start,
		};
	}

	/// <summary>Launches the child, drains both pipes, and negotiates lossless protocol v2 when advertised.</summary>
	internal async Task StartAsync( CancellationToken cancellationToken )
	{
		_process.Start();
		_diagnostics = ReadDiagnosticsAsync();
		_reader = ReadFramesAsync();
		var ready = await _ready.Task.WaitAsync( TimeSpan.FromSeconds( StartupSeconds ), cancellationToken ).ConfigureAwait( false );
		if ( ready.TryGetProperty( "supportedProtocolVersions", out var versions ) && versions.EnumerateArray().Any( version => version.GetInt32() == 2 ) )
			await RequestAsync( "negotiate_protocol", new
			{
				protocolVersion = 2
			}, cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Sends a named RPC command and returns its cloned data, throwing server failures.</summary>
	public async Task<JsonElement> RequestAsync( string type, object? fields = null, CancellationToken cancellationToken = default )
	{
		var id = Guid.NewGuid().ToString( "N" );
		var frame = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode( fields )!.AsObject();
		frame["type"] = type;
		frame["id"] = id;
		var response = new TaskCompletionSource<JsonElement>( TaskCreationOptions.RunContinuationsAsynchronously );
		_pending[id] = response;
		try
		{
			await SendAsync( frame, cancellationToken );
			return await response.Task.WaitAsync( cancellationToken );
		}
		finally
		{
			_pending.TryRemove( id, out _ );
		}
	}

	/// <summary>Sends a side-channel frame such as an extension dialog answer without awaiting a response.</summary>
	public async Task SendAsync( object frame, CancellationToken cancellationToken = default )
	{
		await _writes.WaitAsync( cancellationToken );
		try
		{
			await _process.StandardInput.WriteLineAsync( JsonSerializer.Serialize( frame ).AsMemory(), cancellationToken );
			await _process.StandardInput.FlushAsync( cancellationToken );
		}
		finally
		{
			_writes.Release();
		}
	}

	/// <summary>Closes stdin, drains remaining output, and terminates a child that does not finish promptly.</summary>
	public Task StopAsync()
	{
		// A panel can start cleanup while module shutdown concurrently waits on another thread.
		lock ( _stopGate )
			return _stopTask ??= ReleaseProcessAsync();
	}

	/// <summary>Gets the child release once a stop began, or null while the session still runs.</summary>
	internal Task? Stopping
	{
		get
		{
			lock ( _stopGate )
				return _stopTask;
		}
	}

	/// <summary>Completes the owned child release independently of the caller's UI synchronization context.</summary>
	private async Task ReleaseProcessAsync()
	{
		Volatile.Write( ref _stopped, 1 );

		try
		{
			if ( _reader is null )
				return;

			_process.StandardInput.Close();
			try
			{
				await _process.WaitForExitAsync().WaitAsync( TimeSpan.FromSeconds( 3 ) ).ConfigureAwait( false );
			}
			catch ( TimeoutException )
			{
				_process.Kill( entireProcessTree: true );
				await _process.WaitForExitAsync().ConfigureAwait( false );
			}

			await _reader.ConfigureAwait( false );
		}
		finally
		{
			// The module owns this native child; closing panels must not orphan agent processes.
			_process.Dispose();
		}
	}

	/// <summary>Reads frames continuously so output backpressure cannot prevent normal child shutdown.</summary>
	private async Task ReadFramesAsync()
	{
		var decoder = new OmpFrameDecoder();
		Exception? failure = null;
		try
		{
			while ( await _process.StandardOutput.ReadLineAsync().ConfigureAwait( false ) is { } line )
			{
				if ( string.IsNullOrWhiteSpace( line ) )
					continue;

				if ( decoder.Decode( line ) is not { } frame )
					continue;

				var type = frame.GetProperty( "type" ).GetString();
				if ( type == "ready" )
					_ready.TrySetResult( frame );
				if ( type == "response" && frame.TryGetProperty( "id", out var id ) && _pending.TryGetValue( id.GetString()!, out var pending ) )
				{
					if ( frame.TryGetProperty( "success", out var success ) && success.ValueKind == JsonValueKind.False )
						pending.TrySetException( new InvalidOperationException( frame.GetProperty( "error" ).GetString() ) );
					else
						pending.TrySetResult( frame.TryGetProperty( "data", out var data ) ? data.Clone() : JsonSerializer.SerializeToElement<object?>( null ) );
				}

				FrameReceived?.Invoke( frame );
			}
		}
		catch ( Exception error )
		{
			failure = error;
		}
		finally
		{
			if ( _diagnostics is not null )
				await _diagnostics.ConfigureAwait( false );

			failure ??= new IOException( $"Omp closed its output. {_diagnostic}" );
			_ready.TrySetException( failure );
			foreach ( var pending in _pending.Values )
				pending.TrySetException( failure );

			if ( Volatile.Read( ref _stopped ) == 0 )
				FrameReceived?.Invoke( JsonSerializer.SerializeToElement( new
				{
					type = "notice",
					level = "error",
					source = "omp-transport",
					message = failure.Message
				} ) );
		}
	}

	/// <summary>Drains stderr independently and retains only the latest diagnostic line.</summary>
	private async Task ReadDiagnosticsAsync()
	{
		try
		{
			while ( await _process.StandardError.ReadLineAsync().ConfigureAwait( false ) is { } line )
			{
				// Omp follows its actionable startup error with generic instructions and a path.
				if ( line.Contains( "No default model selected", StringComparison.OrdinalIgnoreCase ) || _diagnostic.Length == 0 && !string.IsNullOrWhiteSpace( line ) )
					_diagnostic = line;
			}
		}
		catch ( IOException ) { }
		catch ( ObjectDisposedException ) { }
	}
}

/// <summary>Reassembles bounded, ordered Omp protocol-v2 UTF-8 chunk sequences.</summary>
internal sealed class OmpFrameDecoder
{
	/// <summary>Defines Omp's maximum reassembled logical frame size in bytes.</summary>
	private const int MaximumBytes = 67108864;

	/// <summary>Accumulates the current chunk sequence and its advertised identity.</summary>
	private (string Id, int Count, int Bytes, int Index, MemoryStream Data)? _chunk;

	/// <summary>Returns a complete cloned frame or null while a chunk sequence remains incomplete.</summary>
	internal JsonElement? Decode( string line )
	{
		using var document = JsonDocument.Parse( line );
		var root = document.RootElement;
		if ( root.GetProperty( "type" ).GetString() != "rpc_chunk" )
		{
			if ( _chunk is not null )
				throw new InvalidDataException( "Omp interrupted a chunk sequence." );

			return root.Clone();
		}

		var id = root.GetProperty( "chunkId" ).GetString()!;
		var index = root.GetProperty( "index" ).GetInt32();
		var count = root.GetProperty( "count" ).GetInt32();
		var bytes = root.GetProperty( "byteLength" ).GetInt32();
		if ( count <= 0 || bytes <= 0 || bytes > MaximumBytes || count > bytes )
			throw new InvalidDataException( "Omp advertised an invalid chunk size." );

		var chunk = _chunk ?? (id, count, bytes, 0, new MemoryStream());
		if ( chunk.Id != id || chunk.Count != count || chunk.Bytes != bytes || chunk.Index != index )
			throw new InvalidDataException( "Omp emitted an out-of-order chunk." );

		var data = Convert.FromBase64String( root.GetProperty( "data" ).GetString()! );
		if ( chunk.Data.Length + data.Length > bytes )
			throw new InvalidDataException( "Omp exceeded its declared frame size." );

		chunk.Data.Write( data );
		chunk.Index++;
		_chunk = chunk;
		if ( chunk.Index < count )
			return null;

		if ( chunk.Data.Length != bytes )
			throw new InvalidDataException( "Omp emitted an incomplete frame." );

		var text = new UTF8Encoding( false, true ).GetString( chunk.Data.ToArray() );
		chunk.Data.Dispose();
		_chunk = null;
		using var complete = JsonDocument.Parse( text );
		return complete.RootElement.Clone();
	}
}
