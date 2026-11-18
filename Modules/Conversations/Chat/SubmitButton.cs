using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes the prompt's round submit button.</summary>
/// <param name="IsBusy">Whether the agent is working, so the button stops the turn instead of sending.</param>
/// <param name="HasDraft">Whether the prompt or its attachments hold something to send.</param>
/// <param name="IsEnabled">Whether the button can be pressed at all.</param>
/// <param name="Send">Sends the prompt.</param>
/// <param name="Stop">Stops the running turn.</param>
internal sealed record SubmitInput( bool IsBusy, bool HasDraft, bool IsEnabled, Action Send, Action Stop );

/// <summary>Draws the send arrow and the stop square in one round button, morphing between them as a turn starts and ends.</summary>
/// <remarks>
/// Both glyphs stay mounted under stable keys, so a change of state transitions their opacity and transform instead of swapping text.
/// While busy a thin arc turns just inside the edge; it mounts only then, so an idle chat runs no timer.
/// The morph and the hover and press feedback take <see cref="ChatMotion.BaseMs"/> on the chat's shared curve.
/// </remarks>
internal sealed class SubmitButton : GuardedCell<SubmitInput>
{
	/// <summary>Defines the stop square's side in logical pixels.</summary>
	private const int StopSize = 10;

	/// <summary>Defines the busy arc's stroke width in logical pixels.</summary>
	private const double ArcStroke = 1.5;

	/// <summary>Defines how far the busy arc sits inside the button's edge, in logical pixels.</summary>
	private const int ArcInset = 3;

	/// <summary>Defines the busy arc's opacity over the accent fill.</summary>
	private const double ArcOpacity = 0.6;

	/// <summary>Defines the properties every layer animates.</summary>
	private static readonly TransitionProperty[] Animated = [TransitionProperty.Opacity, TransitionProperty.Transform, TransitionProperty.BackgroundColor];

	/// <summary>Builds the button: an accent disc while there is something to send or a turn to stop, a quiet one otherwise.</summary>
	protected override Blob BuildView( SubmitInput input )
	{
		var theme = Theme.Current;
		var size = theme.ControlHeight;
		var isActive = input.IsBusy || input.HasDraft;
		var glyph = isActive ? theme.Panel : theme.Muted;
		var action = input.IsBusy ? input.Stop : input.Send;
		var children = new List<Blob>
		{
			BuildLayer( "submit-arrow", size, ChatIcons.BuildArrow( glyph ),
			// Sending lifts the arrow away; ending the turn drops it back in.
			input.IsBusy ? 0 : 1,
			input.IsBusy ? new PanelTransform { TranslateY = -size / 3, Scale = 0.6 } : new PanelTransform() ),
			BuildLayer( "submit-stop", size, new Container
			{
				Width = StopSize,
				Height = StopSize,
				BorderRadius = 2.5,
				BackgroundColor = theme.Panel,
			},
			input.IsBusy ? 1 : 0,
			input.IsBusy ? new PanelTransform() : new PanelTransform { Scale = 0.3, Rotate = -90 } ),
		};
		if ( input.IsBusy )
		{
			var arc = new Container
			{
				Key = "submit-arc",
				Position = PositionType.Absolute,
				Left = 0,
				Top = 0,
				Width = size,
				Height = size,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Opacity = ArcOpacity,
				Children = [ChatIcons.BuildSpinner( "submit-spinner", theme.Panel, size - ArcInset * 2, ArcStroke )],
			};
			children.Add( arc );
		}

		return new Button
		{
			Key = "conversation-submit",
			Width = size,
			Height = size,
			FlexShrink = 0,
			BorderRadius = size / 2.0,
			BackgroundColor = isActive ? theme.Accent : theme.Hover,
			Focusable = true,
			Disabled = !input.IsEnabled,
			Cursor = Cursor.Pointer,
			AccessibilityName = input.IsBusy ? "Stop" : "Send",
			OnClick = action,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, action ),
				new( Key.Space, KeyModifiers.None, action ),
			],
			TransitionMs = ChatMotion.BaseMs,
			TransitionEasing = ChatMotion.Ease,
			TransitionProperties = Animated,
			HoverStyle = new Style { Transform = new PanelTransform { Scale = 1.08 } },
			FocusStyle = new Style { Transform = new PanelTransform { Scale = 1.08 } },
			ActiveStyle = new Style { Transform = new PanelTransform { Scale = 0.92 } },
			Children = children,
		};
	}

	/// <summary>Centers one glyph over the whole button, so the glyphs can cross over each other while they morph.</summary>
	private static Container BuildLayer( string key, int size, Blob glyph, double opacity, PanelTransform transform ) => new()
	{
		Key = key,
		Position = PositionType.Absolute,
		Left = 0,
		Top = 0,
		Width = size,
		Height = size,
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		Opacity = opacity,
		Transform = transform,
		TransitionMs = ChatMotion.BaseMs,
		TransitionEasing = ChatMotion.Ease,
		TransitionProperties = Animated,
		Children = [glyph],
	};
}
