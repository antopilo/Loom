using System.Threading.Channels;

namespace Lokn.Modules.Remote;

/// <summary>One end of an in-process message connection, for loopback and checks.</summary>
public sealed class MemoryConnection : MessageConnection
{
	/// <summary>Holds the messages this end receives.</summary>
	private readonly Channel<CarrierMessage> _inbox = Channel.CreateUnbounded<CarrierMessage>( new() { SingleReader = true } );

	/// <summary>Stores the other end, set once by <see cref="CreatePair"/>.</summary>
	private MemoryConnection _other = null!;

	/// <summary>Creates an end; <see cref="CreatePair"/> connects two.</summary>
	private MemoryConnection()
	{
	}

	/// <summary>Gets or sets a hook every sent message passes through, so a check can change or drop it (null drops it).</summary>
	public Func<CarrierMessage, CarrierMessage?>? Intercept
	{
		get; set;
	}

	/// <summary>Gets whether this end was closed, by either side.</summary>
	public bool IsClosed => _inbox.Reader.Completion.IsCompleted;

	/// <summary>Creates two connected ends.</summary>
	public static (MemoryConnection First, MemoryConnection Second) CreatePair()
	{
		var first = new MemoryConnection();
		var second = new MemoryConnection();
		first._other = second;
		second._other = first;
		return (first, second);
	}

	/// <summary>Sends a message to the other end after <see cref="Intercept"/>; sending on a closed connection throws.</summary>
	/// <exception cref="IOException">The connection closed.</exception>
	public override Task SendMessageAsync( CarrierMessage message )
	{
		var sent = Intercept is { } intercept ? intercept( message ) : message;
		return sent is null ? Task.CompletedTask : Deliver( sent );
	}

	/// <summary>Sends a message to the other end as is, past <see cref="Intercept"/>, such as to replay a recorded one.</summary>
	/// <exception cref="IOException">The connection closed.</exception>
	public Task SendRawAsync( CarrierMessage message ) => Deliver( message );

	/// <summary>Receives the next message, or null once either end closed.</summary>
	/// <exception cref="InvalidDataException">The message is larger than <see cref="MessageConnection.MaxMessageBytes"/>.</exception>
	public override async Task<CarrierMessage?> ReceiveMessageAsync( CancellationToken cancellationToken )
	{
		CarrierMessage message;
		try
		{
			message = await _inbox.Reader.ReadAsync( cancellationToken ).ConfigureAwait( false );
		}
		catch ( ChannelClosedException )
		{
			return null;
		}
		return message.Data.Length > MaxMessageBytes ? throw new InvalidDataException( "A message is too large." ) : message;
	}

	/// <summary>Closes both ends.</summary>
	public override ValueTask DisposeAsync()
	{
		_inbox.Writer.TryComplete();
		_other._inbox.Writer.TryComplete();
		return ValueTask.CompletedTask;
	}

	/// <summary>Queues a message on the other end.</summary>
	private Task Deliver( CarrierMessage message ) => _other._inbox.Writer.TryWrite( message )
		? Task.CompletedTask
		: Task.FromException( new IOException( "The connection closed." ) );
}
