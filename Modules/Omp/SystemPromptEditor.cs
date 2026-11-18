using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Builds the chat system prompt's editor: several lines, saved when it loses focus or the settings close.</summary>
internal static class SystemPromptEditor
{
	/// <summary>Defines how many lines of text the editor shows.</summary>
	private const int VisibleLines = 6;

	/// <summary>Defines a line's height as a multiple of the font size.</summary>
	private const double LineHeight = 1.5;

	/// <summary>Builds the editor of the scope being edited; it starts afresh whenever the saved value changes, such as on reset.</summary>
	internal static Blob Build( LoknApplication app, SettingsScope scope )
	{
		var value = app.Settings.Get( OmpModule.SystemPrompt, scope );
		return Cell.Mount<EditorInput, EditorCell>( $"system-prompt:{scope}:{value.GetHashCode()}", new( app, scope, value ) );
	}

	/// <summary>Holds the scope an editor saves to and the value it started from.</summary>
	/// <param name="App">The application whose settings it edits.</param>
	/// <param name="Scope">The global or profile layer being edited.</param>
	/// <param name="Value">The value shown when the editor opened, inherited or not.</param>
	private sealed record EditorInput( LoknApplication App, SettingsScope Scope, string Value );

	/// <summary>Keeps the text being typed across the settings panel's rebuilds.</summary>
	private sealed class EditorCell : GuardedCell<EditorInput>
	{
		/// <summary>Holds the document being edited, created from the value at the first build.</summary>
		private TextEditorController? _editor;

		/// <summary>Builds a bordered multiline editor shaped like the settings panel's other fields.</summary>
		protected override Blob BuildView( EditorInput input )
		{
			var theme = Theme.Current;
			_editor ??= new( new TextDocument( input.Value ) );
			return new TextEditor
			{
				Controller = _editor,
				AccessibilityName = OmpModule.SystemPrompt.Title,
				AccessibilityRole = AccessibilityRole.TextEditor,
				Placeholder = "Instructions for every chat",
				Height = theme.FontSize * LineHeight * VisibleLines + theme.Spacing * 2,
				Padding = theme.Spacing,
				BackgroundColor = theme.Panel,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				OnBlur = _ => Save(),
			};
		}

		/// <summary>Saves text left unsaved when the settings close or the value is replaced, then releases the editor.</summary>
		protected override void Unmounting()
		{
			Save();
			_editor?.Dispose();
			_editor = null;
		}

		/// <summary>Saves the text unless it still matches what was shown, since storing an unchanged value would turn an inherited one into an override.</summary>
		private void Save()
		{
			// At application exit the Omp module may already be unloaded; its setting can no longer be stored.
			if ( _editor?.Document.GetText() is not { } text || text == Input.Value || !Input.App.Settings.Definitions.Contains( OmpModule.SystemPrompt ) )
				return;

			Input.App.Settings.Set( OmpModule.SystemPrompt, Input.Scope, text );
		}
	}
}
