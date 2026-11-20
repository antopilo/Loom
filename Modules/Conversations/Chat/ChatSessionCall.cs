using System.Text.Json;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Chat;

/// <summary>Lists the <see cref="IChatSession"/> methods another process may call by name in a <c>call</c> request, and invokes them on a host's session.</summary>
/// <remarks>
/// Arguments are a JSON array in the method's parameter order, serialized with <see cref="StoreOp.JsonOptions"/>; <see cref="AnswerDialog"/>'s
/// answer is the JSON Omp reads. Members a proxy answers from its last state are not listed: <see cref="IChatSession.ComputeFolderRefusal"/>,
/// <see cref="IChatSession.ComputeTaskCheckDue"/> and <see cref="IChatSession.ReadSelection"/>; nor is <see cref="IChatSession.EnsureConnection"/>,
/// whose process stays in the host.
/// </remarks>
public static class ChatSessionCall
{
	/// <summary>Names <see cref="IChatSession.Attach"/>: panel identifier.</summary>
	public const string Attach = nameof( IChatSession.Attach );

	/// <summary>Names <see cref="IChatSession.Detach"/>: panel identifier.</summary>
	public const string Detach = nameof( IChatSession.Detach );

	/// <summary>Names <see cref="IChatSession.UpdateStatus"/>: status.</summary>
	public const string UpdateStatus = nameof( IChatSession.UpdateStatus );

	/// <summary>Names <see cref="IChatSession.UpdateConnect"/>.</summary>
	public const string UpdateConnect = nameof( IChatSession.UpdateConnect );

	/// <summary>Names <see cref="IChatSession.Send"/>: text, attachments.</summary>
	public const string Send = nameof( IChatSession.Send );

	/// <summary>Names <see cref="IChatSession.Continue"/>.</summary>
	public const string Continue = nameof( IChatSession.Continue );

	/// <summary>Names <see cref="IChatSession.Clear"/>.</summary>
	public const string Clear = nameof( IChatSession.Clear );

	/// <summary>Names <see cref="IChatSession.Interject"/>: text, attachments, steer; answers whether it was handed over.</summary>
	public const string Interject = nameof( IChatSession.Interject );

	/// <summary>Names <see cref="IChatSession.UpdateCardPrompt"/>: message.</summary>
	public const string UpdateCardPrompt = nameof( IChatSession.UpdateCardPrompt );

	/// <summary>Names <see cref="IChatSession.Stop"/>.</summary>
	public const string Stop = nameof( IChatSession.Stop );

	/// <summary>Names <see cref="IChatSession.UpdateEffort"/>.</summary>
	public const string UpdateEffort = nameof( IChatSession.UpdateEffort );

	/// <summary>Names <see cref="IChatSession.UpdateModel"/>: model.</summary>
	public const string UpdateModel = nameof( IChatSession.UpdateModel );

	/// <summary>Names <see cref="IChatSession.DismissDialog"/>: question identifier.</summary>
	public const string DismissDialog = nameof( IChatSession.DismissDialog );

	/// <summary>Names <see cref="IChatSession.AnswerDialog"/>: question identifier, answer.</summary>
	public const string AnswerDialog = nameof( IChatSession.AnswerDialog );

	/// <summary>Names <see cref="IChatSession.UpdateSelection"/>: key, label, whether several may be picked.</summary>
	public const string UpdateSelection = nameof( IChatSession.UpdateSelection );

	/// <summary>Names <see cref="IChatSession.ReleaseSelections"/>: question identifier.</summary>
	public const string ReleaseSelections = nameof( IChatSession.ReleaseSelections );

	/// <summary>Names <see cref="IChatSession.UpdatePromote"/>: text.</summary>
	public const string UpdatePromote = nameof( IChatSession.UpdatePromote );

	/// <summary>Names <see cref="IChatSession.UpdateUnqueue"/>: text, whether it is steering.</summary>
	public const string UpdateUnqueue = nameof( IChatSession.UpdateUnqueue );

	/// <summary>Names <see cref="IChatSession.UpdateDirectory"/>: folder; answers whether it was set.</summary>
	public const string UpdateDirectory = nameof( IChatSession.UpdateDirectory );

	/// <summary>Names <see cref="IChatSession.CheckNewTaskAsync"/>: text; answers whether the prompt looks like a new task.</summary>
	public const string CheckNewTask = nameof( IChatSession.CheckNewTaskAsync );

	/// <summary>Lists every method a <c>call</c> may name.</summary>
	public static IReadOnlySet<string> Methods { get; } = new HashSet<string>( StringComparer.Ordinal )
	{
		Attach, Detach, UpdateStatus, UpdateConnect, Send, Continue, Clear, Interject, UpdateCardPrompt, Stop, UpdateEffort, UpdateModel,
		DismissDialog, AnswerDialog, UpdateSelection, ReleaseSelections, UpdatePromote, UpdateUnqueue, UpdateDirectory, CheckNewTask,
	};

	/// <summary>Gets the JSON null a method without a result answers.</summary>
	private static JsonElement None { get; } = JsonSerializer.SerializeToElement<object?>( null );

	/// <summary>Serializes a call's arguments, in the method's parameter order, with the protocol's JSON options.</summary>
	public static JsonElement[] BuildArgs( params object?[] values ) => [.. values.Select( value => JsonSerializer.SerializeToElement( value, StoreOp.JsonOptions ) )];

	/// <summary>Invokes a listed method on a host's session, on the UI thread, and answers its result as JSON, or JSON null for none.</summary>
	/// <param name="session">The session that runs the chat.</param>
	/// <param name="method">One of <see cref="Methods"/>.</param>
	/// <param name="args">The arguments in parameter order.</param>
	/// <param name="cancellationToken">Stops a method that waits, such as <see cref="CheckNewTask"/>.</param>
	/// <exception cref="ArgumentException">The method is not listed, or an argument is missing or of the wrong kind.</exception>
	public static async Task<JsonElement> InvokeAsync( IChatSession session, string method, JsonElement[] args, CancellationToken cancellationToken = default )
	{
		switch ( method )
		{
			case Attach:
				session.Attach( Read<Guid>( method, args, 0 ) );
				return None;
			case Detach:
				session.Detach( Read<Guid>( method, args, 0 ) );
				return None;
			case UpdateStatus:
				session.UpdateStatus( Read<string>( method, args, 0 ) );
				return None;
			case UpdateConnect:
				session.UpdateConnect();
				return None;
			case Send:
				session.Send( Read<string>( method, args, 0 ), Read<string[]>( method, args, 1 ) );
				return None;
			case Continue:
				session.Continue();
				return None;
			case Clear:
				session.Clear();
				return None;
			case Interject:
				return Answer( session.Interject( Read<string>( method, args, 0 ), Read<string[]>( method, args, 1 ), Read<bool>( method, args, 2 ) ) );
			case UpdateCardPrompt:
				session.UpdateCardPrompt( Read<string>( method, args, 0 ) );
				return None;
			case Stop:
				session.Stop();
				return None;
			case UpdateEffort:
				session.UpdateEffort();
				return None;
			case UpdateModel:
				session.UpdateModel( Read<OmpModelInfo>( method, args, 0 ) );
				return None;
			case DismissDialog:
				session.DismissDialog( Read<string>( method, args, 0 ) );
				return None;
			case AnswerDialog:
				// The answer goes to Omp as the JSON it is.
				session.AnswerDialog( Read<string>( method, args, 0 ), Read<JsonElement>( method, args, 1 ) );
				return None;
			case UpdateSelection:
				session.UpdateSelection( Read<string>( method, args, 0 ), Read<string>( method, args, 1 ), Read<bool>( method, args, 2 ) );
				return None;
			case ReleaseSelections:
				session.ReleaseSelections( Read<string>( method, args, 0 ) );
				return None;
			case UpdatePromote:
				session.UpdatePromote( Read<string>( method, args, 0 ) );
				return None;
			case UpdateUnqueue:
				session.UpdateUnqueue( Read<string>( method, args, 0 ), Read<bool>( method, args, 1 ) );
				return None;
			case UpdateDirectory:
				return Answer( session.UpdateDirectory( Read<string>( method, args, 0 ) ) );
			case CheckNewTask:
				return Answer( await session.CheckNewTaskAsync( Read<string>( method, args, 0 ), cancellationToken ).ConfigureAwait( false ) );
			default:
				throw new ArgumentException( $"The chat session has no method {method} to call." );
		}
	}

	/// <summary>Reads one argument of a call.</summary>
	/// <exception cref="ArgumentException">The argument is missing, null or of the wrong kind.</exception>
	private static T Read<T>( string method, JsonElement[] args, int index )
	{
		try
		{
			if ( index < args.Length && args[index].Deserialize<T>( StoreOp.JsonOptions ) is { } value )
				return value;
		}
		catch ( JsonException error )
		{
			throw new ArgumentException( $"The chat call {method} has a malformed argument {index}: {error.Message}", error );
		}

		throw new ArgumentException( $"The chat call {method} needs argument {index}." );
	}

	/// <summary>Serializes a method's result.</summary>
	private static JsonElement Answer<T>( T value ) => JsonSerializer.SerializeToElement( value, StoreOp.JsonOptions );
}
