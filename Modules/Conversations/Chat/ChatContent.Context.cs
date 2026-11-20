using Goo;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Shows how full the session's context is and when Omp compacts it, as the meter along the prompt's top edge.</summary>
/// <remarks>The session follows the context's use and the profile's compaction point; see <see cref="ChatSession"/>.</remarks>
public sealed partial class ChatContent
{
	/// <summary>Builds the meter, or null until Omp reports the model's window.</summary>
	private Blob? BuildContextMeter()
	{
		var window = session.ContextWindow;
		if ( window <= 0 )
			return null;

		var model = session.ConnectedModel;
		var compactAt = session.IsAutoCompaction ? session.Compaction?.ComputeThreshold( window, model ) : null;
		var conversation = store.Get( conversationId );
		var info = session.ModelInfo;
		return Cell.Mount<ContextMeterInput, ContextMeter>( "context-meter", new(
			session.ContextTokens,
			window,
			compactAt,
			session.IsCompacting || session.IsAutoCompacting,
			info?.Name ?? backend.GetModelName( conversation.Profile, model ),
			session.Effort,
			info?.InputCost,
			info?.OutputCost,
			session.CompactionCount,
			session.AuxiliaryCallCount,
			context.App.Settings.Get( OmpModule.Economy, ComputeScope( conversation.Profile ) ) ) );
	}
}
