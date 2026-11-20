using System.Text.Json;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Follows the messages waiting for the running turn, and moves or takes them back.</summary>
/// <remarks>
/// Omp owns the queue and reports it with <c>queue_update</c>: steering messages, which the agent reads at its next step, then
/// follow-ups, which start a turn once this one ends. The first <c>liveSteered</c> steering messages are already in the model's
/// request and can no longer be taken back. A message shows here as soon as it is sent, until Omp's next report replaces it.
/// </remarks>
public sealed partial class ChatSession
{
	/// <summary>Stores the steering messages Omp holds, as their queue text, oldest first.</summary>
	private string[] _queuedSteering = [];

	/// <summary>Stores the follow-up messages Omp holds, as their queue text, oldest first.</summary>
	private string[] _queuedFollowUps = [];

	/// <summary>Stores how many leading steering messages the model already received.</summary>
	private int _liveSteered;

	/// <summary>Gets the steering messages Omp holds, oldest first.</summary>
	public string[] QueuedSteering => _queuedSteering;

	/// <summary>Gets the follow-up messages Omp holds, oldest first.</summary>
	public string[] QueuedFollowUps => _queuedFollowUps;

	/// <summary>Gets how many leading steering messages the model already received.</summary>
	public int LiveSteered => _liveSteered;

	/// <summary>Reads Omp's queue snapshot.</summary>
	private void UpdateQueue( JsonElement frame )
	{
		_queuedSteering = ReadQueue( frame, "steering" );
		_queuedFollowUps = ReadQueue( frame, "followUp" );
		_liveSteered = frame.TryGetProperty( "liveSteered", out var live ) && live.TryGetInt32( out var count ) ? count : 0;
	}

	/// <summary>Forgets the queue, when the turn settles or the connection ends.</summary>
	private void ClearQueue()
	{
		_queuedSteering = [];
		_queuedFollowUps = [];
		_liveSteered = 0;
	}

	/// <summary>Shows a message the moment it is sent, before Omp reports its queue.</summary>
	private void UpdateQueueSent( string text, bool steer )
	{
		if ( steer )
			_queuedSteering = [.. _queuedSteering, text];
		else
			_queuedFollowUps = [.. _queuedFollowUps, text];
	}

	/// <summary>Moves a waiting follow-up into the steering queue, so the agent reads it at its next step.</summary>
	public void UpdatePromote( string text )
	{
		if ( _session is { } session )
			_ = UpdateQueueRequestAsync( session, "promote_queued_message", new { message = text }, restore: null );
	}

	/// <summary>Takes a waiting message back from Omp, and offers it to the views' empty composer through <see cref="UnqueueRestored"/>.</summary>
	public void UpdateUnqueue( string text, bool steering )
	{
		if ( _session is { } session )
			_ = UpdateQueueRequestAsync( session, "remove_queued_message", new { message = text, queue = steering ? "steering" : "followUp" }, restore: text );
	}

	/// <summary>Sends a queue request; Omp answers with a new <c>queue_update</c>, and a refusal shows in the status line.</summary>
	private async Task UpdateQueueRequestAsync( OmpSession session, string type, object fields, string? restore )
	{
		try
		{
			await session.RequestAsync( type, fields, Lifetime );
			if ( restore is null )
				return;

			Post( () => UnqueueRestored?.Invoke( restore ) );
		}
		catch ( Exception error )
		{
			Post( () =>
			{
				_status = error is OperationCanceledException ? "Stopped" : DescribeFailure( error );
				MarkChanged();
			} );
		}
	}

	/// <summary>Reads one queue's messages.</summary>
	private static string[] ReadQueue( JsonElement frame, string name ) =>
		frame.TryGetProperty( name, out var queue ) && queue.ValueKind == JsonValueKind.Array
			? [.. queue.EnumerateArray().Where( item => item.ValueKind == JsonValueKind.String ).Select( item => item.GetString()! )]
			: [];
}
