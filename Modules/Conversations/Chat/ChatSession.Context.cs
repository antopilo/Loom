using System.Text.Json;
using Lokn.Modules.Omp;
using static Lokn.Modules.Chat.ChatContent;

namespace Lokn.Modules.Chat;

/// <summary>Follows how full the session's context is, when Omp compacts it, the session's thinking effort and its model.</summary>
/// <remarks>
/// Omp reports the context's tokens and window in <c>get_state</c>, read on connecting and after each turn, model switch and
/// compaction. Omp's RPC does not report the compaction point, so Lokn reads the profile's compaction settings with
/// <c>omp config list</c> on connecting and applies Omp's rule to the model's window. Omp lists the live model's efforts with
/// <c>get_available_thinking_levels</c>, <c>off</c> first, read on connecting and after a model change.
/// </remarks>
public sealed partial class ChatSession
{
	/// <summary>Stores the context's tokens, or null when Omp has not measured them yet.</summary>
	private long? _contextTokens;

	/// <summary>Stores the model's context window in tokens, or zero when unknown.</summary>
	private long _contextWindow;

	/// <summary>Tracks whether the session compacts automatically, which <c>/compact auto</c> can switch.</summary>
	private bool _isAutoCompaction = true;

	/// <summary>Stores the profile's compaction settings once read, or null.</summary>
	private OmpCompaction? _compaction;

	/// <summary>Tracks whether a request is under way, so frames arriving meanwhile do not queue more.</summary>
	private bool _isContextRequested;

	/// <summary>Tracks whether an automatic compaction runs now, which the meter shows while the chat keeps working.</summary>
	private bool _isAutoCompacting;

	/// <summary>Stores the efforts the live model supports, <c>off</c> first, or none before Omp lists them.</summary>
	private string[] _efforts = [];

	/// <summary>Stores the session's effort as Omp last reported or Lokn last set it, or empty when unknown.</summary>
	private string _effort = "";

	/// <summary>Gets the context's tokens, or null when Omp has not measured them yet.</summary>
	public long? ContextTokens => _contextTokens;

	/// <summary>Gets the model's context window in tokens, or zero when unknown.</summary>
	public long ContextWindow => _contextWindow;

	/// <summary>Gets whether the session compacts automatically.</summary>
	public bool IsAutoCompaction => _isAutoCompaction;

	/// <summary>Gets the profile's compaction settings once read, or null.</summary>
	public OmpCompaction? Compaction => _compaction;

	/// <summary>Gets whether an automatic compaction runs now.</summary>
	public bool IsAutoCompacting => _isAutoCompacting;

	/// <summary>Gets the efforts the live model supports, <c>off</c> first.</summary>
	public string[] Efforts => _efforts;

	/// <summary>Gets the session's effort, or empty when unknown.</summary>
	public string Effort => _effort;

	/// <summary>Reads the context's use from a <c>get_state</c> answer.</summary>
	private void UpdateContextUsage( JsonElement state )
	{
		if ( state.ValueKind != JsonValueKind.Object )
			return;

		if ( state.TryGetProperty( "autoCompactionEnabled", out var auto ) && auto.ValueKind is JsonValueKind.True or JsonValueKind.False )
			_isAutoCompaction = auto.GetBoolean();
		if ( state.TryGetProperty( "contextUsage", out var usage ) && usage.ValueKind == JsonValueKind.Object )
		{
			_contextWindow = usage.TryGetProperty( "contextWindow", out var window ) && window.TryGetInt64( out var size ) ? size : _contextWindow;
			_contextTokens = usage.TryGetProperty( "tokens", out var tokens ) && tokens.TryGetInt64( out var count ) ? count : null;
		}
		else if ( state.TryGetProperty( "model", out var model ) && model.ValueKind == JsonValueKind.Object
			&& model.TryGetProperty( "contextWindow", out var window ) && window.TryGetInt64( out var size ) )
		{
			// Before the first reply Omp leaves the usage out; the model still names its window.
			_contextWindow = size;
			_contextTokens = null;
		}
	}

	/// <summary>Asks Omp for the context's use after a turn, a model switch or a compaction, one request at a time.</summary>
	private void UpdateContextRequest()
	{
		if ( _isContextRequested || _session is not { } session )
			return;

		_isContextRequested = true;
		_ = UpdateContextRequestAsync( session );
	}

	/// <summary>Reads the state and applies it on the UI thread; a failure leaves the meter as it was.</summary>
	private async Task UpdateContextRequestAsync( OmpSession session )
	{
		JsonElement? state = null;
		try
		{
			state = await session.RequestAsync( "get_state", cancellationToken: Lifetime );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The meter is a convenience: it keeps its last reading.
		}

		Post( () =>
		{
			_isContextRequested = false;
			if ( state is { } read && _session == session )
			{
				UpdateContextUsage( read );
				UpdateEffortState( read );
				UpdateGoalState( read );
				MarkChanged();
			}
		} );
	}

	/// <summary>Reads the profile's compaction settings in the background, which place the meter's compaction mark.</summary>
	private async Task UpdateCompactionSettingsAsync()
	{
		try
		{
			var compaction = await backend.GetCompactionAsync( store.Get( conversationId ).Profile, Lifetime );
			Post( () =>
			{
				_compaction = compaction;
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// Without the settings the meter shows the use without a compaction mark.
		}
	}

	/// <summary>Loads the connected model's catalog prices and limits without making a provider request.</summary>
	private async Task UpdateModelInfoAsync( string selector )
	{
		if ( selector.Length == 0 )
			return;
		try
		{
			var profile = store.Get( conversationId ).Profile;
			var models = backend.GetCachedModels( profile ) ?? await backend.GetModelsAsync( profile, Lifetime ).ConfigureAwait( false );
			var model = models.FirstOrDefault( item => item.Selector == selector );
			Post( () =>
			{
				if ( _connectedModel != selector )
					return;
				_modelInfo = model;
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// Model rates are optional metadata; the chat keeps token counts without them.
		}
	}

	/// <summary>Steps the session to the next effort the model supports, showing it at once and going back if Omp refuses it.</summary>
	public void UpdateEffort()
	{
		if ( _session is not { } session || _efforts.Length < 2 )
			return;

		var previous = _effort;
		_effort = _efforts[(Array.IndexOf( _efforts, _effort ) + 1) % _efforts.Length];
		MarkChanged();
		_ = UpdateEffortAsync( session, _effort, previous );
	}

	/// <summary>Asks Omp for an effort; on failure the effort goes back to the one before.</summary>
	private async Task UpdateEffortAsync( OmpSession session, string effort, string previous )
	{
		try
		{
			await session.RequestAsync( "set_thinking_level", new
			{
				level = effort,
			}, Lifetime );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			Post( () =>
			{
				if ( _effort == effort )
					_effort = previous;
				_status = "Could not change effort: " + error.Message;
				MarkChanged();
			} );
		}
	}

	/// <summary>Reads the efforts the live model supports in the background; without them no effort can be chosen.</summary>
	private async Task UpdateEffortsAsync( OmpSession session )
	{
		try
		{
			var data = await session.RequestAsync( "get_available_thinking_levels", cancellationToken: Lifetime );
			string[] efforts = data.ValueKind == JsonValueKind.Object && data.TryGetProperty( "levels", out var levels ) && levels.ValueKind == JsonValueKind.Array
				? [.. levels.EnumerateArray().Where( level => level.ValueKind == JsonValueKind.String ).Select( level => level.GetString()! )]
				: [];
			Post( () =>
			{
				if ( _session != session )
					return;

				_efforts = efforts;
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// An Omp without effort discovery keeps the effort hidden.
		}
	}

	/// <summary>Reads the session's effort from a <c>get_state</c> answer.</summary>
	private void UpdateEffortState( JsonElement state )
	{
		if ( state.ValueKind == JsonValueKind.Object && state.TryGetProperty( "thinkingLevel", out var level ) && level.ValueKind == JsonValueKind.String )
			_effort = level.GetString() ?? "";
	}

	/// <summary>Switches this chat's session to a model, connecting first when needed.</summary>
	public void UpdateModel( OmpModelInfo model )
	{
		_status = $"Switching to {model.Name}…";
		MarkChanged();
		_ = UpdateModelAsync( EnsureConnection(), model );
	}

	/// <summary>Asks Omp to use a model for this session, which it records with the session so a reconnect keeps it.</summary>
	private async Task UpdateModelAsync( Task<OmpSession> connection, OmpModelInfo model )
	{
		try
		{
			var session = await connection;
			await session.RequestAsync( "set_model", new
			{
				provider = model.Provider,
				modelId = model.Id,
			}, Lifetime );
			Post( () =>
			{
				_connectedModel = model.Selector;
				_modelInfo = model;
				UpdateContextRequest();
				if ( _session is { } connected )
					_ = UpdateEffortsAsync( connected );
				_status = $"Using {model.Name}";
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			Post( () =>
			{
				_status = "Could not switch model: " + error.Message;
				MarkChanged();
			} );
		}
	}
}
