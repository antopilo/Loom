using System.Text.Json;
using Lokn.Modules.Omp;
using static Lokn.Modules.Chat.ChatContent;

namespace Lokn.Modules.Chat;

/// <summary>Runs <c>/goal</c> through Omp's <c>goal</c> RPC command and follows the chat's goal.</summary>
/// <remarks>
/// Omp runs <c>/goal</c> only in its terminal interface; over RPC it offers the <c>goal</c> command with the same lifecycle, and reports
/// every change, the agent's own included, in <c>goal_updated</c> frames and <c>get_state</c>. A goal continues on its own over RPC
/// because Lokn starts Omp with <see cref="OmpService.ChatConfig"/>.
/// </remarks>
public sealed partial class ChatSession
{
	/// <summary>Defines the status while a <c>/goal</c> command waits for Omp.</summary>
	private const string GoalStatus = "Updating the goal…";

	/// <summary>Stores the chat's goal as Omp last reported it, or null without one.</summary>
	private JsonElement? _goal;

	/// <summary>Gets the chat's goal as Omp reports it: <c>objective</c>, <c>status</c>, <c>tokensUsed</c> and <c>tokenBudget</c>; null without one.</summary>
	public JsonElement? Goal => _goal;

	/// <summary>Reads the goal from a <c>goal</c> answer, a <c>goal_updated</c> frame or <c>get_state</c>; leaves it when the payload names none.</summary>
	private void UpdateGoalState( JsonElement data )
	{
		if ( data.ValueKind == JsonValueKind.Object && data.TryGetProperty( "goal", out var goal ) )
			_goal = goal.ValueKind == JsonValueKind.Object ? goal.Clone() : null;
	}

	/// <summary>Runs a <c>/goal</c> command, connecting first when needed; its result shows in the transcript under the command.</summary>
	private void UpdateGoalCommand( string command, GoalCommand goal )
	{
		if ( goal.Op == "create" && goal.Objective.Length == 0 )
		{
			AppendCommandOutput( command, "Type the objective after /goal, such as /goal Make the test suite pass." );
			return;
		}

		if ( !_isBusy )
			_status = GoalStatus;
		MarkChanged();
		_ = UpdateGoalAsync( EnsureConnection(), command, goal );
	}

	/// <summary>Sends the goal command; a new objective first drops the goal it replaces, since Omp creates one only when none is active or paused.</summary>
	private async Task UpdateGoalAsync( Task<OmpSession> connection, string command, GoalCommand goal )
	{
		try
		{
			var session = await connection;
			if ( goal.Op == "create" )
			{
				var current = await session.RequestAsync( "goal", new { op = "get" }, Lifetime );
				if ( current.ValueKind == JsonValueKind.Object && current.TryGetProperty( "goal", out var held ) && held.ValueKind == JsonValueKind.Object )
					await session.RequestAsync( "goal", new { op = "drop" }, Lifetime );
			}
			var result = goal.Op == "create"
				? await session.RequestAsync( "goal", new { op = goal.Op, objective = goal.Objective }, Lifetime )
				: await session.RequestAsync( "goal", new { op = goal.Op }, Lifetime );
			Post( () =>
			{
				UpdateGoalState( result );
				UpdateGoalDone( command, goal.Op == "drop" ? "Goal dropped." : ChatModes.DescribeGoal( _goal ) );
			} );
		}
		catch ( Exception error )
		{
			Post( () => UpdateGoalDone( command, error is OperationCanceledException ? "Stopped" : DescribeFailure( error ) ) );
		}
	}

	/// <summary>Shows a goal command's result under it, and the session as ready unless a turn runs, such as the goal turn Omp started.</summary>
	private void UpdateGoalDone( string command, string output )
	{
		if ( !_isBusy )
			_status = "Ready";
		AppendCommandOutput( command, output );
		MarkChanged();
	}
}
