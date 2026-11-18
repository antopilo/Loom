using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Renders Omp's supported native tool dialogs and returns their actual wire answers.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the maximum question card height in logical pixels; taller questions scroll.</summary>
	private const int DialogHeight = 440;

	/// <summary>Defines the single-line answer height in logical pixels.</summary>
	private const int AnswerHeight = 36;

	/// <summary>Retains selected labels for each opt-in multi-question dialog.</summary>
	private readonly Dictionary<string, HashSet<string>> _answers = [];

	/// <summary>Holds second editors over the dialog editors' documents, for copies of a question shown outside the chat, such as in the notification feed or a banner, by surface and editor key.</summary>
	/// <remarks>A <see cref="TextEditorController"/> mounts one editor, and the chat's own copy and each outside copy may be on screen at the same time.</remarks>
	private readonly Dictionary<(string Surface, string Key), TextEditorController> _outsideEditors = [];

	/// <summary>Changes when the pending questions or their selections change, so views outside the chat rebuild only then, not on every streamed frame.</summary>
	private readonly State<int> _dialogVersion = new( 0 );

	/// <summary>Stores what the pending questions looked like at the last change, to tell when <see cref="_dialogVersion"/> must move.</summary>
	private string _dialogSignature = "";

	/// <summary>Counts selection changes in questionnaires, which the signature includes.</summary>
	private int _answerRevision;

	/// <summary>Holds the dialogs answered or cancelled whose exit is playing; they stay in <see cref="_dialogs"/> until it ends but are no longer pending.</summary>
	private readonly HashSet<string> _leavingDialogs = new( StringComparer.Ordinal );

	/// <summary>Gets the first dialog still waiting for an answer, or null when none is.</summary>
	private JsonElement? PendingDialog
	{
		get
		{
			foreach ( var dialog in _dialogs )
			{
				if ( !_leavingDialogs.Contains( ReadText( dialog, "id" ) ) )
					return dialog;
			}
			return null;
		}
	}

	/// <summary>Counts the dialogs still waiting for an answer.</summary>
	private int PendingDialogCount => _dialogs.Count - _dialogs.Count( dialog => _leavingDialogs.Contains( ReadText( dialog, "id" ) ) );

	/// <summary>Gets whether the agent is waiting on a question, subscribing the reading view to question changes only.</summary>
	internal bool HasPendingDialog
	{
		get
		{
			_ = _dialogVersion.Value;
			return PendingDialog is not null;
		}
	}

	/// <summary>Builds the first pending question for a view outside the chat, answering it as the chat would, or null when none is pending.</summary>
	/// <param name="surface">Names the view, such as <c>feed</c> or <c>banner</c>; views shown at the same time need different names, as each gets its own text editors.</param>
	internal Blob? BuildPendingDialog( string surface )
	{
		_ = _dialogVersion.Value;
		return PendingDialog is { } dialog ? BuildDialog( dialog, surface ) : null;
	}

	/// <summary>Moves <see cref="_dialogVersion"/> when the pending questions changed since the last change.</summary>
	private void UpdateDialogSignature()
	{
		var signature = PendingDialog is { } dialog ? $"{PendingDialogCount}:{ReadText( dialog, "id" )}:{_answerRevision}" : "";
		if ( signature == _dialogSignature )
			return;

		_dialogSignature = signature;
		_dialogVersion.Value++;
	}

	/// <summary>Plays a dialog's exit in the chat, then removes it and releases its editors; it stops counting as pending at once.</summary>
	private void UpdateDialogLeave( string id )
	{
		if ( !_leavingDialogs.Add( id ) )
			return;

		UpdateAfterExit( () =>
		{
			_leavingDialogs.Remove( id );
			_dialogs.RemoveAll( dialog => ReadText( dialog, "id" ) == id );
			ReleaseDialogEditors( id );
			MarkChanged();
		} );
	}

	/// <summary>Updates ordered dialog state or applies a backend presentation notification.</summary>
	private void UpdateDialog( JsonElement frame )
	{
		var method = ReadText( frame, "method" );
		var id = ReadText( frame, "id" );
		if ( method == "cancel" )
			UpdateDialogLeave( ReadText( frame, "targetId" ) );
		else if ( method is "select" or "confirm" or "input" or "editor" or "ask" or "open_url" )
		{
			_dialogs.RemoveAll( dialog => ReadText( dialog, "id" ) == id );
			_dialogs.Add( frame.Clone() );
			_status = "Waiting for your answer";
		}
		else if ( method == "set_editor_text" )
		{
			var text = ReadText( frame, "text" );
			_composer.Document.Apply( new TextChange( new TextRange( 0, _composer.Document.Length ), text ) );
		}
		else if ( method is "notify" or "setStatus" && ReadText( frame, "message", ReadText( frame, "text", ReadText( frame, "content" ) ) ) is { Length: > 0 } notice )
			_status = notice;
	}

	/// <summary>Builds a pending select, confirm, text editor, or questionnaire using native Goo controls.</summary>
	/// <param name="dialog">The question.</param>
	/// <param name="surface">The view outside the chat it shows in, whose text editors get their own controllers over the same documents; null in the chat.</param>
	private Blob BuildDialog( JsonElement dialog, string? surface = null )
	{
		var id = ReadText( dialog, "id" );
		var method = ReadText( dialog, "method" );
		var children = new List<Blob>();
		children.Add( new Container
		{
			FontWeight = AuthorWeight,
			Children = [MarkdownView.Build( ReadText( dialog, "title", "Your input is needed" ), OpenLink )],
		} );
		var message = ReadText( dialog, "message" );
		if ( message.Length > 0 )
			children.Add( MarkdownView.Build( message, OpenLink ) );
		var actions = new List<Blob>();

		if ( method == "open_url" )
		{
			var destination = ReadText( dialog, "launchUrl", ReadText( dialog, "url" ) );
			if ( Uri.TryCreate( destination, UriKind.Absolute, out var uri ) && uri.Scheme is "https" or "http" )
				children.Add( Theme.Current.BuildButton( "Open authorization page", () => context.Run( () =>
					Process.Start( new ProcessStartInfo( destination )
					{
						UseShellExecute = true,
					} ) ) ) );
			children.Add( BuildText( ReadText( dialog, "instructions" ), muted: true ) );
		}
		else if ( method == "select" && dialog.TryGetProperty( "options", out var options ) )
		{
			var index = 0;
			var checkedIndices = dialog.TryGetProperty( "checkedIndices", out var selected )
				? selected.EnumerateArray().Select( item => item.GetInt32() ).ToHashSet() : [];
			var marker = ReadText( dialog, "selectionMarker" );
			var markableCount = dialog.TryGetProperty( "markableCount", out var count ) ? count.GetInt32() : options.GetArrayLength();
			var initialIndex = dialog.TryGetProperty( "initialIndex", out var initial ) ? initial.GetInt32() : 0;
			var details = dialog.TryGetProperty( "optionDetails", out var found ) ? found : default;
			foreach ( var option in options.EnumerateArray() )
			{
				var value = option.GetString() ?? "";
				var mark = "";
				var isSelected = false;
				if ( index < markableCount && marker == "checkbox" )
					mark = (isSelected = checkedIndices.Contains( index )) ? "☑" : "☐";
				else if ( index < markableCount && marker == "radio" )
					mark = (isSelected = index == initialIndex) ? "◉" : "○";
				var description = details.ValueKind == JsonValueKind.Array && index < details.GetArrayLength() ? ReadText( details[index], "description" ) : "";
				children.Add( BuildOptionCard( "option:" + index, value, description, mark, isSelected,
					() => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, value } ) ) );
				index++;
			}
		}
		else if ( method == "confirm" )
		{
			actions.Add( Theme.Current.BuildButton( "Decline", () => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, confirmed = false } ) ) );
			actions.Add( BuildPrimaryButton( "Confirm", () => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, confirmed = true } ) ) );
		}
		else if ( method == "ask" && dialog.TryGetProperty( "questions", out var questions ) )
		{
			foreach ( var question in questions.EnumerateArray() )
				children.Add( BuildQuestion( id, question, surface ) );
			actions.Add( BuildPrimaryButton( "Submit answers", () => UpdateQuestionsAnswer( dialog ) ) );
		}
		else if ( method is "input" or "editor" )
		{
			var editor = EnsureDialogEditor( id, ReadText( dialog, "prefill", ReadText( dialog, "value" ) ) );
			// In the chat the message box below takes a plain answer; secret answers and views outside the chat keep their own box.
			var isInComposer = surface is null && ComposerAnswerKey() == id;
			if ( isInComposer )
				children.Add( BuildText( "Type your answer in the message box below, then press Enter.", muted: true ) );
			else if ( method == "input" )
			{
				children.Add( new TextEntry
				{
					Key = "input:" + id,
					Value = editor.Document.GetText(),
					Controlled = true,
					Password = ReadText( dialog, "secret" ) is "True" or "true",
					Placeholder = ReadText( dialog, "placeholder" ),
					AccessibilityName = "Tool answer",
					Height = AnswerHeight,
					Padding = Theme.Current.Spacing,
					BackgroundColor = Theme.Current.Panel,
					OnChange = value => context.Run( () => editor.Document.Apply( new TextChange( new TextRange( 0, editor.Document.Length ), value ) ) ),
					OnSubmit = value => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, value } ),
				} );
			}
			else
				children.Add( BuildDialogEditor( surface is null ? editor : EnsureOutsideEditor( surface, id, editor ), "Tool answer", "answer:" + id ) );
			if ( !isInComposer )
				actions.Add( BuildPrimaryButton( "Submit answer", () => UpdateDialogAnswer( id,
					new
					{
						type = "extension_ui_response",
						id,
						value = editor.Document.GetText()
					} ) ) );
		}
		var dismiss = method == "open_url"
			? Theme.Current.BuildButton( "Dismiss", () => context.Run( () =>
			{
				UpdateDialogLeave( id );
				MarkChanged();
			} ) )
			: Theme.Current.BuildButton( "Cancel", () => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, cancelled = true } ) );
		dismiss.Color = Theme.Current.Muted;
		actions.Insert( 0, dismiss );
		children.Add( new Row
		{
			Key = "actions",
			JustifyContent = JustifyContent.FlexEnd,
			FlexWrap = FlexWrap.Wrap,
			Gap = Theme.Current.Spacing,
			Children = actions,
		} );
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Tool question",
			Key = "dialog:" + id,
			MaxHeight = DialogHeight,
			OverflowY = Overflow.Scroll,
			Padding = new Edges( Theme.Current.Spacing * 2, Theme.Current.Spacing * 2.5 ),
			Gap = Theme.Current.Spacing * 1.5,
			BackgroundColor = Theme.Current.Raised,
			BorderColor = Theme.Current.Accent,
			BorderWidth = Theme.Current.BorderWidth,
			BorderRadius = Theme.Current.Radius,
			Children = children,
		};
	}

	/// <summary>Builds the action that completes a question, set apart from the others by an accent outline.</summary>
	private static Button BuildPrimaryButton( string label, Action action )
	{
		var button = Theme.Current.BuildButton( label, action );
		button.Color = Theme.Current.Accent;
		button.BorderColor = Theme.Current.Accent;
		button.BorderWidth = Theme.Current.BorderWidth;
		return button;
	}

	/// <summary>Builds one choice as a card: its marker, its label, and its description under it; the selected choice has an accent outline.</summary>
	/// <param name="key">The card's element key.</param>
	/// <param name="label">The exact option label.</param>
	/// <param name="description">Text under the label; empty for none.</param>
	/// <param name="marker">A ☐, ☑, ○ or ◉ symbol; empty for none.</param>
	/// <param name="isSelected">Whether the choice is selected.</param>
	/// <param name="action">Runs when the card is picked.</param>
	private static Button BuildOptionCard( string key, string label, string description, string marker, bool isSelected, Action action )
	{
		var theme = Theme.Current;
		var text = new List<Blob> { new Text { Content = label, FontWeight = AuthorWeight, MinWidth = 0 } };
		if ( description.Length > 0 )
			text.Add( new Text { Content = description, Color = theme.Muted, MinWidth = 0 } );
		var row = new List<Blob>();
		if ( marker.Length > 0 )
			row.Add( MarkdownView.BuildMarker( marker ) );
		row.Add( new Container { FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Gap = 2, Children = text } );
		return new Button
		{
			Key = key,
			AccessibilityName = description.Length > 0 ? $"{label}: {description}" : label,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.FlexStart,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing * 0.75, theme.Spacing ),
			BorderRadius = theme.Radius,
			BorderWidth = theme.BorderWidth,
			BorderColor = isSelected ? theme.Accent : theme.Border,
			BackgroundColor = theme.Panel,
			Color = theme.Text,
			FontSize = theme.FontSize,
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = action,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, action ),
				new( Key.Space, KeyModifiers.None, action ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = row,
		};
	}

	/// <summary>Builds one labelled multi-question form with optional custom input.</summary>
	private Blob BuildQuestion( string dialogId, JsonElement question, string? surface )
	{
		var key = dialogId + "." + ReadText( question, "id" );
		if ( !_answers.TryGetValue( key, out var selected ) )
			_answers.Add( key, selected = [] );
		var multi = ReadText( question, "multi" ) is "True" or "true";
		var children = new List<Blob> { MarkdownView.Build( ReadText( question, "question" ), OpenLink ) };
		foreach ( var option in question.GetProperty( "options" ).EnumerateArray() )
		{
			var label = ReadText( option, "label" );
			var isSelected = selected.Contains( label );
			children.Add( BuildOptionCard( "option:" + label, label, ReadText( option, "description" ), multi ? (isSelected ? "☑" : "☐") : (isSelected ? "◉" : "○"), isSelected, () => context.Run( () =>
			{
				if ( selected.Contains( label ) )
					selected.Remove( label );
				else
				{
					if ( !multi )
						selected.Clear();
					selected.Add( label );
				}
				_answerRevision++;
				MarkChanged();
			} ) ) );
		}
		// A lone question in the chat takes its own answer from the message box below instead of a second text box.
		if ( surface is null && ComposerAnswerKey() == key )
			children.Add( BuildText( multi ? "Or type your own answer in the message box below." : "Or type your own answer in the message box below; it replaces the choice.", muted: true ) );
		else
		{
			children.Add( BuildText( multi ? "Or add your own answer" : "Or write your own answer", muted: true ) );
			var custom = EnsureDialogEditor( key, "" );
			children.Add( BuildDialogEditor( surface is null ? custom : EnsureOutsideEditor( surface, key, custom ), "Custom answer", "answer:" + key ) );
		}
		return new Container
		{
			Key = "question:" + key,
			Gap = Theme.Current.Spacing,
			Children = children,
		};
	}

	/// <summary>Gets an outside view's editor over a dialog editor's document, replacing one left over a document a reconnect discarded.</summary>
	private TextEditorController EnsureOutsideEditor( string surface, string key, TextEditorController editor )
	{
		if ( !_outsideEditors.TryGetValue( (surface, key), out var outside ) || outside.Document != editor.Document )
			_outsideEditors[(surface, key)] = outside = new( editor.Document );
		return outside;
	}

	/// <summary>Builds a multiline native text input used by tool questionnaires.</summary>
	private static Blob BuildDialogEditor( TextEditorController editor, string name, string key ) => new TextEditor
	{
		Key = key,
		Controller = editor,
		AccessibilityName = name,
		AccessibilityRole = AccessibilityRole.TextEditor,
		Height = ComposerHeight,
		Padding = Theme.Current.Spacing,
		BackgroundColor = Theme.Current.Panel,
	};

	/// <summary>Gets a dialog-specific editor initialized once from the backend's suggested text.</summary>
	private TextEditorController EnsureDialogEditor( string id, string initial )
	{
		if ( !_dialogEditors.TryGetValue( id, out var editor ) )
			_dialogEditors.Add( id, editor = new( new TextDocument( initial ) ) );
		return editor;
	}

	/// <summary>Gets the answer the message box types for the pending question: the question's id for a one-line input, the question's key for a lone
	/// questionnaire question, or null when the question takes no typed answer or has several text boxes.</summary>
	private string? ComposerAnswerKey()
	{
		if ( PendingDialog is not { } dialog )
			return null;

		var id = ReadText( dialog, "id" );
		return ReadText( dialog, "method" ) switch
		{
			// The message box shows what is typed, so a secret keeps its masked box.
			"input" when ReadText( dialog, "secret" ) is not ("True" or "true") => id,
			"ask" when dialog.TryGetProperty( "questions", out var questions ) && questions.GetArrayLength() == 1 => id + "." + ReadText( questions[0], "id" ),
			_ => null,
		};
	}

	/// <summary>Answers the pending question with text typed in the message box; false when the question does not take it there.</summary>
	private bool UpdateComposerAnswer( string text )
	{
		if ( PendingDialog is not { } dialog || ComposerAnswerKey() is not { } key )
			return false;

		var id = ReadText( dialog, "id" );
		if ( ReadText( dialog, "method" ) == "input" )
		{
			UpdateDialogAnswer( id, new { type = "extension_ui_response", id, value = text } );
			return true;
		}

		var custom = EnsureDialogEditor( key, "" );
		custom.Document.Apply( new TextChange( new TextRange( 0, custom.Document.Length ), text ) );
		UpdateQuestionsAnswer( dialog );
		return true;
	}

	/// <summary>Serializes all answers in request order with exact option labels.</summary>
	private void UpdateQuestionsAnswer( JsonElement dialog )
	{
		var id = ReadText( dialog, "id" );
		var answers = dialog.GetProperty( "questions" ).EnumerateArray().Select( question =>
		{
			var questionId = ReadText( question, "id" );
			var key = id + "." + questionId;
			var customInput = EnsureDialogEditor( key, "" ).Document.GetText().Trim();
			var isMulti = ReadText( question, "multi" ) is "True" or "true";
			var selectedOptions = !isMulti && customInput.Length > 0 ? [] : _answers.GetValueOrDefault( key )?.ToArray() ?? [];
			return new
			{
				id = questionId,
				selectedOptions,
				customInput
			};
		} ).ToArray();
		UpdateDialogAnswer( id, new
		{
			type = "extension_ui_response",
			id,
			answers
		} );
	}

	/// <summary>Sends one tool answer and removes its dialog only after the pipe accepts it; a dialog already leaving takes no second answer.</summary>
	private void UpdateDialogAnswer( string id, object response )
	{
		if ( _session is not { } session || _leavingDialogs.Contains( id ) )
			return;
		_ = UpdateDialogAnswerAsync( session, id, response );
	}

	/// <summary>Contains side-channel write failures and retains unanswered dialogs for retry.</summary>
	private async Task UpdateDialogAnswerAsync( Omp.OmpSession session, string id, object response )
	{
		try
		{
			await session.SendAsync( response, context.Lifetime );
			context.Post( () =>
			{
				UpdateDialogLeave( id );
				_status = "Working…";
				// Answering is dealing with it, wherever the answer came from, such as the notification feed.
				context.App.Notifications.MarkPanelRead( context.Id );
				if ( PendingDialog is null )
					context.ClearAttention();
				MarkChanged();
			} );
		}
		catch ( Exception error )
		{
			context.Post( () =>
			{
				_status = error.Message;
				MarkChanged();
			} );
		}
	}

	/// <summary>Releases completed dialog text and selections so old prompts do not accumulate.</summary>
	private void ReleaseDialogEditors( string id )
	{
		foreach ( var key in _dialogEditors.Keys.Where( key => key == id || key.StartsWith( id + ".", StringComparison.Ordinal ) ).ToArray() )
			_dialogEditors.Remove( key );
		foreach ( var key in _answers.Keys.Where( key => key.StartsWith( id + ".", StringComparison.Ordinal ) ).ToArray() )
			_answers.Remove( key );
		foreach ( var key in _outsideEditors.Keys.Where( key => key.Key == id || key.Key.StartsWith( id + ".", StringComparison.Ordinal ) ).ToArray() )
			_outsideEditors.Remove( key );
	}
}
