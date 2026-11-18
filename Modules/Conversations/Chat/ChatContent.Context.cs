using System.Text.Json;
using Goo;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Follows how full the session's context is and when Omp compacts it, for the meter along the prompt's top edge.</summary>
/// <remarks>
/// Omp reports the context's tokens and window in <c>get_state</c>, read on connecting and after each turn, model switch and
/// compaction. Omp's RPC does not report the compaction point, so Lokn reads the profile's compaction settings with
/// <c>omp config list</c> on connecting and applies Omp's rule to the model's window.
/// </remarks>
public sealed partial class ChatContent
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

	/// <summary>Builds the meter, or null until Omp reports the model's window.</summary>
	private Blob? BuildContextMeter()
	{
		if ( _contextWindow <= 0 )
			return null;

		var compactAt = _isAutoCompaction ? _compaction?.ComputeThreshold( _contextWindow, _connectedModel ) : null;
		var conversation = store.Get( conversationId );
		return Cell.Mount<ContextMeterInput, ContextMeter>( "context-meter", new(
			_contextTokens,
			_contextWindow,
			compactAt,
			_isCompacting || _isAutoCompacting,
			_modelInfo?.Name ?? backend.GetModelName( conversation.Profile, _connectedModel ),
			_effort,
			_modelInfo?.InputCost,
			_modelInfo?.OutputCost,
			_compactionCount,
			_auxiliaryCallCount,
			context.App.Settings.Get( OmpModule.Economy, ComputeScope( conversation.Profile ) ) ) );
	}

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
			state = await session.RequestAsync( "get_state", cancellationToken: context.Lifetime );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The meter is a convenience: it keeps its last reading.
		}

		context.Post( () =>
		{
			_isContextRequested = false;
			if ( state is { } read && _session == session )
			{
				UpdateContextUsage( read );
				UpdateEffortState( read );
				MarkChanged();
			}
		} );
	}

	/// <summary>Reads the profile's compaction settings in the background, which place the meter's compaction mark.</summary>
	private async Task UpdateCompactionSettingsAsync()
	{
		try
		{
			var compaction = await backend.GetCompactionAsync( store.Get( conversationId ).Profile, context.Lifetime );
			context.Post( () =>
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
			var models = backend.GetCachedModels( profile ) ?? await backend.GetModelsAsync( profile, context.Lifetime ).ConfigureAwait( false );
			var model = models.FirstOrDefault( item => item.Selector == selector );
			context.Post( () =>
			{
				if ( _connectedModel != selector )
					return;
				_modelInfo = model;
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// Model rates are optional metadata; the panel keeps token counts without them.
		}
	}
}
