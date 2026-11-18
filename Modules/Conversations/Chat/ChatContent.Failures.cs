using System.Text.Json;
using System.Text.RegularExpressions;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Chat;

/// <summary>Explains a reply the model provider failed, which Omp reports as an assistant message with an error and often no content.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the stop reason Omp gives a reply the provider failed; a stop by the user is "aborted" and shows nothing.</summary>
	private const string FailedStopReason = "error";

	/// <summary>Defines the pattern of the wait in milliseconds a provider asks for before the next attempt.</summary>
	private const string RetryAfterPattern = @"retry-after-ms=(\d+)";

	/// <summary>Reads why the provider failed a reply, in words for the transcript, or null when the reply did not fail.</summary>
	internal static string? ReadFailure( ConversationMessage message )
	{
		if ( message.Role != "assistant" || message.RichContent is not { } rich || ReadText( rich, "stopReason" ) != FailedStopReason )
			return null;

		return DescribeProviderError( ReadText( rich, "errorMessage" ), message.CreatedAt );
	}

	/// <summary>Turns a provider error such as `429 {"error":{"message":"…"}} retry-after-ms=1723000` into one readable sentence.</summary>
	/// <param name="error">The error text as Omp recorded it.</param>
	/// <param name="failedAt">When the reply failed, which the provider's wait counts from.</param>
	internal static string DescribeProviderError( string error, DateTimeOffset failedAt )
	{
		var reason = ReadProviderMessage( error ) ?? error.Trim();
		if ( reason.Length == 0 )
			reason = "No reason was given.";

		var description = "The model provider did not answer: " + reason;
		var retry = Regex.Match( error, RetryAfterPattern );
		if ( !retry.Success || !long.TryParse( retry.Groups[1].Value, out var milliseconds ) )
			return description;

		return $"{description} Try again after {failedAt.AddMilliseconds( milliseconds ).ToLocalTime():t}.";
	}

	/// <summary>Reads the message of the JSON body inside a provider error, or null when the error holds none.</summary>
	private static string? ReadProviderMessage( string error )
	{
		var start = error.IndexOf( '{' );
		var end = error.LastIndexOf( '}' );
		if ( start < 0 || end < start )
			return null;

		try
		{
			var body = JsonSerializer.Deserialize<JsonElement>( error[start..(end + 1)] );
			var message = body.ValueKind == JsonValueKind.Object && body.TryGetProperty( "error", out var inner ) && inner.ValueKind == JsonValueKind.Object
				? ReadText( inner, "message" )
				: ReadText( body, "message" );
			return message.Length > 0 ? message : null;
		}
		catch ( JsonException )
		{
			// The braces were not JSON, so the caller shows the error as it is.
			return null;
		}
	}
}
