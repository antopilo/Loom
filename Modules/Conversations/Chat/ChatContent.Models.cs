using Goo;
using Lokn.Core;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Switches the chat's model from the model picker, for this chat or as its profile's default.</summary>
public sealed partial class ChatContent
{
	/// <summary>Reports whether the model picker is open over the chat.</summary>
	private bool _isPickingModel;

	/// <summary>Builds the model picker over the chat while it is open; it fades and rises in as it opens.</summary>
	/// <remarks>
	/// It closes at once: it holds focus while open, and keeping it for an exit would hold the keyboard away from the prompt.
	/// </remarks>
	private Blob? BuildModelPicker()
	{
		if ( !_isPickingModel )
			return null;

		var profile = store.Get( conversationId ).Profile;
		var scope = ComputeScope( profile );
		var picker = Cell.Mount<OmpModelPickerInput, OmpModelPicker>( "model-picker", new(
			backend,
			profile,
			session.ConnectedModel,
			context.App.Settings.Get( OmpModule.Model, scope ),
			"Use for this chat",
			model => context.Run( () => UpdateModel( model ) ),
			model => context.Run( () =>
			{
				context.App.Settings.Set( OmpModule.Model, scope, model.Selector );
				session.UpdateStatus( $"New {profile} chats start with {model.Name}." );
			} ),
			() => context.Run( () =>
			{
				UpdatePicking( false );
				OmpModule.OpenSetup( context.App, profile );
			} ),
			() => context.Run( () =>
			{
				UpdatePicking( false );
				OmpModule.OpenRouting( context.App, profile );
			} ),
			() => context.Run( () => UpdatePicking( false ) ) ) );
		// The picker covers the chat from its parent's edges, so a layer over the same box carries it; mounted only while open, it enters once per opening.
		var layer = new Container
		{
			Key = "model-picker-layer",
			Position = PositionType.Absolute,
			Left = 0,
			Top = 0,
			Right = 0,
			Bottom = 0,
			Children = [picker],
		};
		return Cell.Mount<RevealInput, Reveal>( "model-picker-reveal", new( layer, true, Cover: true ) );
	}

	/// <summary>Opens or closes the model picker.</summary>
	private void UpdatePicking( bool isPicking )
	{
		_isPickingModel = isPicking;
		MarkChanged();
	}

	/// <summary>Closes the picker and switches this chat's session to a model, connecting first when needed.</summary>
	private void UpdateModel( OmpModelInfo model )
	{
		UpdatePicking( false );
		session.UpdateModel( model );
	}

	/// <summary>Gets the settings layer of an account profile.</summary>
	internal static SettingsScope ComputeScope( SettingsProfile profile ) => profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal;
}
