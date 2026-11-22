using System.Globalization;
using System.Text.Json;

namespace Lokn.Modules.Chat;

/// <summary>Describes a <c>/goal</c> command: the operation of Omp's <c>goal</c> RPC command it runs, and the objective of a new goal.</summary>
/// <param name="Op">The operation: <c>get</c>, <c>create</c>, <c>pause</c>, <c>resume</c> or <c>drop</c>.</param>
/// <param name="Objective">The objective of a new goal, or empty.</param>
internal sealed record GoalCommand( string Op, string Objective );

/// <summary>Holds the parts of an assistant reply around the plan it wrote.</summary>
/// <param name="Before">The text before the plan.</param>
/// <param name="Plan">The plan's Markdown, without its tags.</param>
/// <param name="After">The text after the plan.</param>
/// <param name="IsClosed">Whether the plan's end tag arrived; false while the plan still streams.</param>
internal readonly record struct PlanParts( string Before, string Plan, string After, bool IsClosed );

/// <summary>Reads and writes the prompts of <c>/plan</c> and <c>/goal</c>, which Omp runs only in its terminal interface, not over RPC.</summary>
/// <remarks>
/// Plan mode is a prompt, as in Codex: <c>/plan &lt;task&gt;</c> sends the task with instructions in a <c>&lt;lokn-plan-mode&gt;</c>
/// block, the agent explores, asks its questions and ends its reply with the plan in a <c>&lt;lokn-plan&gt;</c> block, and the chat
/// shows it as a card and offers Implement and Revise. <c>/goal</c> runs Omp's <c>goal</c> RPC command.
/// </remarks>
internal static class ChatModes
{
	/// <summary>Defines the name of the plan command.</summary>
	internal const string PlanName = "plan";

	/// <summary>Defines the name of the goal command.</summary>
	internal const string GoalName = "goal";

	/// <summary>Marks the start of the plan instructions in a sent prompt.</summary>
	private const string RequestStart = "<lokn-plan-mode>";

	/// <summary>Marks the end of the plan instructions in a sent prompt.</summary>
	private const string RequestEnd = "</lokn-plan-mode>";

	/// <summary>Marks the start of the plan in the agent's reply.</summary>
	private const string PlanStart = "<lokn-plan>";

	/// <summary>Marks the end of the plan in the agent's reply.</summary>
	private const string PlanEnd = "</lokn-plan>";

	/// <summary>Defines the instructions a <c>/plan</c> prompt gives the agent after the task.</summary>
	internal const string PlanInstructions = """
		Plan mode: plan the task above, do not do it yet.
		- Change nothing: no file edits or writes, and no commands that change files, git, packages or settings. Reading, searching and read-only commands are fine.
		- Explore the code until you know what the change needs.
		- When a decision is mine to make, ask me with the ask tool before you plan around it.
		- End your reply with the plan in Markdown between <lokn-plan> and </lokn-plan>: the goal, the steps with the files each one changes, the risks, and how to verify the result.
		- If this chat already has a plan, revise it with what I wrote above.
		- Then stop. Implement the plan only after I approve it.
		""";

	/// <summary>Defines the prompt Implement sends for the latest plan.</summary>
	internal const string ImplementPrompt = "I approve the plan. Implement it now.";

	/// <summary>Gets the argument of a prompt that is the slash command <paramref name="name"/>, trimmed; null when the prompt is not that command.</summary>
	private static string? ReadArgument( string text, string name )
	{
		var trimmed = text.Trim();
		if ( trimmed.Length < name.Length + 1 || trimmed[0] != '/' || string.CompareOrdinal( trimmed, 1, name, 0, name.Length ) != 0 )
			return null;

		var rest = trimmed[(name.Length + 1)..];
		return rest.Length == 0 ? "" : char.IsWhiteSpace( rest[0] ) ? rest.Trim() : null;
	}

	/// <summary>Gets the task of a <c>/plan</c> prompt, empty when none was typed; null when the prompt is not <c>/plan</c>.</summary>
	internal static string? ReadPlanTask( string text ) => ReadArgument( text, PlanName );

	/// <summary>Writes what a <c>/plan</c> prompt sends Omp: the task, then the plan instructions in their block.</summary>
	internal static string ComposePlan( string task ) => $"{task}\n\n{RequestStart}\n{PlanInstructions}\n{RequestEnd}";

	/// <summary>Gets whether a sent prompt holds plan instructions.</summary>
	internal static bool IsPlanRequest( string text ) => text.Contains( RequestStart, StringComparison.Ordinal );

	/// <summary>Removes the plan instructions from a sent prompt, leaving the task as the user typed it.</summary>
	internal static string StripPlanRequest( string text )
	{
		var start = text.IndexOf( RequestStart, StringComparison.Ordinal );
		if ( start < 0 )
			return text;

		var end = text.IndexOf( RequestEnd, start, StringComparison.Ordinal );
		var after = end < 0 ? "" : text[(end + RequestEnd.Length)..];
		return (text[..start].TrimEnd() + after).Trim();
	}

	/// <summary>Splits an assistant text block at the plan it holds; null when it holds none.</summary>
	internal static PlanParts? SplitPlan( string text )
	{
		var start = text.IndexOf( PlanStart, StringComparison.Ordinal );
		if ( start < 0 )
			return null;

		var body = start + PlanStart.Length;
		var end = text.IndexOf( PlanEnd, body, StringComparison.Ordinal );
		return end < 0
			? new( text[..start], text[body..], "", false )
			: new( text[..start], text[body..end], text[(end + PlanEnd.Length)..], true );
	}

	/// <summary>Reads a <c>/goal</c> prompt: no argument or <c>show</c> reads the goal, <c>pause</c>, <c>resume</c> and <c>drop</c> act on it,
	/// and any other text, after <c>set</c> or not, is a new objective that replaces the goal; null when the prompt is not <c>/goal</c>.</summary>
	internal static GoalCommand? ReadGoal( string text )
	{
		if ( ReadArgument( text, GoalName ) is not { } argument )
			return null;

		var space = argument.IndexOfAny( [' ', '\t', '\r', '\n'] );
		var word = (space < 0 ? argument : argument[..space]).ToLowerInvariant();
		var rest = space < 0 ? "" : argument[space..].Trim();
		return word switch
		{
			"" or "show" when rest.Length == 0 => new( "get", "" ),
			"pause" or "resume" or "drop" when rest.Length == 0 => new( word, "" ),
			"set" => new( "create", rest ),
			_ => new( "create", argument ),
		};
	}

	/// <summary>Gets the status of an Omp goal as the chat shows it.</summary>
	internal static string DescribeGoalStatus( JsonElement goal ) => ChatContent.ReadText( goal, "status" ) switch
	{
		"active" => "Active",
		"paused" => "Paused",
		"budget-limited" => "Budget reached",
		"complete" => "Complete",
		"dropped" => "Dropped",
		var other => other,
	};

	/// <summary>Gets the tokens an Omp goal used, with its budget when it has one, such as <c>12,400 of 200,000 tokens</c>.</summary>
	internal static string DescribeGoalTokens( JsonElement goal )
	{
		var used = goal.TryGetProperty( "tokensUsed", out var tokens ) && tokens.TryGetInt64( out var count ) ? count : 0;
		var budget = goal.TryGetProperty( "tokenBudget", out var limit ) && limit.TryGetInt64( out var most ) ? most : 0;
		return budget > 0
			? string.Format( CultureInfo.CurrentCulture, "{0:N0} of {1:N0} tokens", used, budget )
			: string.Format( CultureInfo.CurrentCulture, "{0:N0} tokens", used );
	}

	/// <summary>Describes an Omp goal for the transcript: its status and objective, then what it used; <c>No goal.</c> for none.</summary>
	internal static string DescribeGoal( JsonElement? goal ) => goal is { ValueKind: JsonValueKind.Object } value
		? $"Goal ({DescribeGoalStatus( value ).ToLowerInvariant()}): {ChatContent.ReadText( value, "objective" )}\n{DescribeGoalTokens( value )}"
		: "No goal.";
}
