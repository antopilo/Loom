using System.Text.Json;
using Goo;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Shows the session's thinking effort as a ring beside the model, which steps to the next effort the model supports when pressed.</summary>
/// <remarks>
/// Omp lists the live model's efforts with <c>get_available_thinking_levels</c>, <c>off</c> first, read on connecting and
/// after a model change. The current effort comes from <c>get_state</c>. Pressing the ring sends <c>set_thinking_level</c>
/// with the next effort, wrapping from the most back to off; Omp keeps it with the session.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Stores the efforts the live model supports, <c>off</c> first, or none before Omp lists them.</summary>
	private string[] _efforts = [];

	/// <summary>Stores the session's effort as Omp last reported or Lokn last set it, or empty when unknown.</summary>
	private string _effort = "";

	/// <summary>Builds the effort ring, or null until the model offers more than one effort.</summary>
	private Button? BuildEffort()
	{
		if ( _session is null || _efforts.Length < 2 )
			return null;

		var index = Array.IndexOf( _efforts, _effort );
		var button = BuildRailButton( "", () => context.Run( UpdateEffort ) );
		button.Key = "conversation-effort";
		button.AccessibilityName = $"Effort: {(_effort.Length > 0 ? _effort : "default")}. Press for {_efforts[(index + 1) % _efforts.Length]}";
		button.FlexShrink = 0;
		button.Children = [ChatIcons.BuildLevel( index <= 0 ? 0 : (double)index / (_efforts.Length - 1) )];
		return button;
	}

	/// <summary>Steps the session to the next effort the model supports, showing it at once and going back if Omp refuses it.</summary>
	private void UpdateEffort()
	{
		if ( _session is not { } session || _efforts.Length < 2 )
			return;

		var previous = _effort;
		_effort = _efforts[(Array.IndexOf( _efforts, _effort ) + 1) % _efforts.Length];
		MarkChanged();
		_ = UpdateEffortAsync( session, _effort, previous );
	}

	/// <summary>Asks Omp for an effort; on failure the ring goes back to the effort before.</summary>
	private async Task UpdateEffortAsync( OmpSession session, string effort, string previous )
	{
		try
		{
			await session.RequestAsync( "set_thinking_level", new
			{
				level = effort,
			}, context.Lifetime );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			context.Post( () =>
			{
				if ( _effort == effort )
					_effort = previous;
				_status = "Could not change effort: " + error.Message;
				MarkChanged();
			} );
		}
	}

	/// <summary>Reads the efforts the live model supports in the background; without them the ring stays hidden.</summary>
	private async Task UpdateEffortsAsync( OmpSession session )
	{
		try
		{
			var data = await session.RequestAsync( "get_available_thinking_levels", cancellationToken: context.Lifetime );
			string[] efforts = data.ValueKind == JsonValueKind.Object && data.TryGetProperty( "levels", out var levels ) && levels.ValueKind == JsonValueKind.Array
				? [.. levels.EnumerateArray().Where( level => level.ValueKind == JsonValueKind.String ).Select( level => level.GetString()! )]
				: [];
			context.Post( () =>
			{
				if ( _session != session )
					return;

				_efforts = efforts;
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// An Omp without effort discovery keeps the ring hidden.
		}
	}

	/// <summary>Reads the session's effort from a <c>get_state</c> answer.</summary>
	private void UpdateEffortState( JsonElement state )
	{
		if ( state.ValueKind == JsonValueKind.Object && state.TryGetProperty( "thinkingLevel", out var level ) && level.ValueKind == JsonValueKind.String )
			_effort = level.GetString() ?? "";
	}
}
