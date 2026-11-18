using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes the prompt's own caret.</summary>
/// <param name="Editor">The prompt editor whose caret it draws.</param>
/// <param name="Controller">The prompt's editing controller, which holds the selection.</param>
/// <param name="IsFocused">Whether the prompt has the keyboard; the caret shows only then.</param>
/// <param name="Mode">The caret animation setting value.</param>
internal sealed record PromptCaretInput( ElementHandle Editor, TextEditorController Controller, bool IsFocused, string Mode );

/// <summary>Draws the prompt's caret in place of Goo's, so it can glide and blink, and so it leaves no trail of stale pixels as text is typed.</summary>
/// <remarks>
/// The caret is an ordinary element over the editor, placed at the editor's retained caret rectangle; Goo repaints it like any element.
/// It reads the rectangle every frame tick while focused and rebuilds only when the rectangle or blink phase changes.
/// </remarks>
internal sealed class PromptCaret : GuardedCell<PromptCaretInput>
{
	/// <summary>Defines the caret animation value that draws a still caret.</summary>
	internal const string Off = "off";

	/// <summary>Defines the caret animation value that only blinks.</summary>
	internal const string Blink = "blink";

	/// <summary>Defines the caret animation value that only glides to its new place.</summary>
	internal const string Glide = "glide";

	/// <summary>Defines the caret animation value that glides and blinks.</summary>
	internal const string GlideBlink = "glide-blink";

	/// <summary>Defines the caret width in logical pixels.</summary>
	private const double Width = 2;

	/// <summary>Defines how often the caret rectangle is read, in milliseconds.</summary>
	private const double PollMs = 16;

	/// <summary>Defines a full blink cycle, shown then hidden, in milliseconds.</summary>
	private const double BlinkMs = 1060;

	/// <summary>Defines the glide to a new place, in milliseconds.</summary>
	private const int GlideMs = 90;

	/// <summary>Defines the blink fade, in milliseconds.</summary>
	private const int FadeMs = 150;

	/// <summary>Stores the caret rectangle in the editor's coordinates, or null when the editor has none.</summary>
	private ElementRect? _rect;

	/// <summary>Stores the time the caret last moved, which restarts the blink so a moving caret stays visible.</summary>
	private DateTime _movedAt = DateTime.UtcNow;

	/// <summary>Tracks whether the blink is in its hidden half.</summary>
	private bool _isHidden;

	/// <summary>Builds the caret bar at its rectangle, or nothing while the prompt has no keyboard.</summary>
	protected override Blob BuildView( PromptCaretInput input )
	{
		if ( !input.IsFocused || _rect is not { } rect )
			return new Container { Key = "caret-none", Width = 0, Height = 0 };

		var glides = input.Mode is Glide or GlideBlink;
		var properties = glides ? new[] { TransitionProperty.Transform, TransitionProperty.Opacity } : [TransitionProperty.Opacity];
		return new Container
		{
			Key = "caret",
			Position = PositionType.Absolute,
			Left = 0,
			Top = 0,
			Width = Width,
			Height = rect.Height,
			BorderRadius = Width / 2,
			BackgroundColor = Theme.Current.Accent,
			Transform = new PanelTransform { TranslateX = rect.X - Width / 2, TranslateY = rect.Y },
			Opacity = _isHidden ? 0 : 1,
			TransitionMs = glides ? GlideMs : input.Mode == Blink ? FadeMs : 0,
			TransitionProperties = properties,
		};
	}

	/// <summary>Starts reading the caret rectangle and blinking.</summary>
	protected override void Mounted() => Every( PollMs, UpdateCaret );

	/// <summary>Reads the caret rectangle and blink phase, rebuilding only when either changed.</summary>
	private void UpdateCaret()
	{
		var input = Input;
		if ( !input.IsFocused )
			return;

		ElementRect? rect = input.Editor.TryGetTextCaretRect( input.Controller.Selection.Active, TextCoordinateSpace.Element, out var found ) ? found : null;
		var changed = rect != _rect;
		if ( changed )
		{
			_rect = rect;
			_movedAt = DateTime.UtcNow;
		}

		var hidden = input.Mode is Blink or GlideBlink && (DateTime.UtcNow - _movedAt).TotalMilliseconds % BlinkMs >= BlinkMs / 2;
		if ( hidden != _isHidden )
		{
			_isHidden = hidden;
			changed = true;
		}

		if ( changed )
			Rebuild();
	}
}
