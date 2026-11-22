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

	/// <summary>Defines the Segoe Fluent Cancel glyph of the button that dismisses a question.</summary>
	private const string CloseGlyph = "\uE711";

	/// <summary>Defines the Segoe Fluent Return glyph of the Ctrl+Enter key caps.</summary>
	private const string ReturnGlyph = "\uE751";

	/// <summary>Defines the height of a key cap in the shortcut hint, in logical pixels.</summary>
	private const int KeyCapHeight = 18;

	/// <summary>Defines the size of the button that dismisses a question, in logical pixels.</summary>
	private const int CloseSize = 22;

	/// <summary>Describes one answer button of a question.</summary>
	/// <param name="Key">The button's element key.</param>
	/// <param name="Label">The exact option label, or the action's name.</param>
	/// <param name="Description">Text under the label; empty for none.</param>
	/// <param name="Marker">A ☐, ☑, ○ or ◉ symbol; empty for none.</param>
	/// <param name="IsSelected">Whether the answer is picked in a questionnaire or checked in a menu.</param>
	/// <param name="IsPrimary">Whether it is the recommended answer, which Ctrl+Enter gives.</param>
	/// <param name="Answer">Sends or picks the answer.</param>
	private sealed record DialogChoice( string Key, string Label, string Description, string Marker, bool IsSelected, bool IsPrimary, Action Answer );

	/// <summary>Holds second editors over the dialog editors' documents, for copies of a question shown outside the chat, such as in the notification feed or a banner, by surface and editor key.</summary>
	/// <remarks>A <see cref="TextEditorController"/> mounts one editor, and the chat's own copy and each outside copy may be on screen at the same time.</remarks>
	private readonly Dictionary<(string Surface, string Key), TextEditorController> _outsideEditors = [];

	/// <summary>Changes when the pending questions or their selections change, so views outside the chat rebuild only then, not on every streamed frame.</summary>
	private readonly State<int> _dialogVersion = new( 0 );

	/// <summary>Holds the questions answered or cancelled whose exit is playing, oldest first; the session no longer counts them as pending.</summary>
	private readonly List<JsonElement> _leavingDialogs = [];

	/// <summary>Builds the first pending question for a view outside the chat, answering it as the chat would, or null when none is pending.</summary>
	/// <param name="surface">Names the view, such as <c>feed</c> or <c>banner</c>; views shown at the same time need different names, as each gets its own text editors.</param>
	internal Blob? BuildPendingDialog( string surface )
	{
		_ = _dialogVersion.Value;
		return session.PendingDialog is { } dialog ? BuildDialog( dialog, surface ) : null;
	}

	/// <summary>Moves <see cref="_dialogVersion"/> when the session's pending questions or their selections changed.</summary>
	private void UpdateDialogsChanged() => _dialogVersion.Value++;

	/// <summary>Plays the exit of a question that stopped being pending, then removes it and releases its editors and selections.</summary>
	private void UpdateDialogLeft( JsonElement dialog )
	{
		var id = ReadText( dialog, "id" );
		_leavingDialogs.Add( dialog );
		UpdateAfterExit( () =>
		{
			_leavingDialogs.RemoveAll( leaving => ReadText( leaving, "id" ) == id );
			ReleaseDialogEditors( id );
			session.ReleaseSelections( id );
			MarkChanged();
		} );
	}

	/// <summary>Marks this panel read once an answer reached Omp, wherever it came from, such as the notification feed.</summary>
	private void UpdateDialogAnswered() => context.App.Notifications.MarkPanelRead( context.Id );

	/// <summary>Builds a pending select, confirm, text editor, or questionnaire as a card: the question, then its answers as a row of buttons, the
	/// recommended one filled and showing its Ctrl+Enter shortcut.</summary>
	/// <param name="dialog">The question.</param>
	/// <param name="surface">The view outside the chat it shows in, whose text editors get their own controllers over the same documents; null in the chat.</param>
	private Blob BuildDialog( JsonElement dialog, string? surface = null )
	{
		var theme = Theme.Current;
		var id = ReadText( dialog, "id" );
		var method = ReadText( dialog, "method" );
		var choices = ListChoices( dialog, surface );
		// A lone question is the card's heading, unless the tool gave the card its own title; a form's heading is its first title or paragraph.
		var title = ReadText( dialog, "title" );
		var lone = ReadLoneQuestion( dialog );
		var form = method == "form" ? ReadForm( dialog ) : null;
		var lead = title.Length > 0 ? title
			: lone is { } only ? ReadText( only, "question" )
			: form is not null ? ReadFormLead( form ).Lead
			: "Your input is needed";
		var close = method == "open_url"
			? theme.BuildButton( "Dismiss", () => context.Run( () => session.DismissDialog( id ) ) )
			: theme.BuildButton( "Cancel", () => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, cancelled = true } ) );
		close.Key = "dialog-close";
		close.Width = CloseSize;
		close.Height = CloseSize;
		close.Padding = 0;
		close.FlexShrink = 0;
		close.Children = [ChatIcons.BuildIcon( CloseGlyph )];
		var children = new List<Blob>
		{
			new Row
			{
				Key = "head",
				AlignItems = AlignItems.FlexStart,
				Gap = theme.Spacing,
				Children = [new Container { FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Children = [MarkdownView.Build( lead, OpenLink )] }, close],
			},
		};
		var message = ReadText( dialog, "message" );
		if ( message.Length > 0 )
			children.Add( MarkdownView.Build( message, OpenLink ) );

		var hasChoiceRow = true;
		if ( method == "open_url" )
			children.Add( BuildText( ReadText( dialog, "instructions" ), muted: true ) );
		else if ( form is not null )
		{
			// A form whose one field is a single choice answers on a click, so its options are the field's own buttons.
			var isQuick = ReadQuickField( form ) is not null;
			children.AddRange( BuildForm( dialog, form, surface, isQuick ? choices : null ) );
			hasChoiceRow = !isQuick;
		}
		else if ( method == "ask" && dialog.TryGetProperty( "questions", out var questions ) )
		{
			// A lone single-choice question answers on a click, so its options are the question's own buttons.
			var isQuick = ReadQuickQuestion( dialog ) is not null;
			foreach ( var question in questions.EnumerateArray() )
				children.Add( BuildQuestion( dialog, question, surface, showsText: title.Length > 0 || lone is null, isQuick ? choices : null ) );
			hasChoiceRow = !isQuick;
		}
		else if ( method is "input" or "editor" )
		{
			var editor = EnsureDialogEditor( id, ReadText( dialog, "prefill", ReadText( dialog, "value" ) ) );
			// In the chat the message box below takes a plain answer; secret answers and views outside the chat keep their own box.
			if ( surface is null && ComposerAnswerKey() == id )
				children.Add( BuildText( "Type your answer in the message box below, then press Enter.", muted: true ) );
			else if ( method == "input" )
				children.Add( BuildAnswerEntry( "input:" + id, editor, ReadText( dialog, "placeholder" ), "Tool answer", ReadText( dialog, "secret" ) is "True" or "true",
					value => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, value } ) ) );
			else
				children.Add( BuildDialogEditor( surface is null ? editor : EnsureOutsideEditor( surface, id, editor ), "Tool answer", "answer:" + id ) );
		}
		if ( hasChoiceRow && choices.Count > 0 )
			children.Add( BuildChoices( "answers", choices ) );

		var primary = choices.FirstOrDefault( choice => choice.IsPrimary );
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Tool question",
			Key = "dialog:" + id,
			MaxHeight = DialogHeight,
			OverflowY = Overflow.Scroll,
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 1.5, theme.Spacing * 1.5, theme.Spacing * 2 ),
			Gap = theme.Spacing * 1.25,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + 3,
			// Ctrl+Enter anywhere in the card gives the recommended answer; a text box that submits its own text binds it first.
			KeyBindings = primary is null ? [] : [new( Key.Enter, KeyModifiers.Primary, primary.Answer )],
			Children = children,
		};
	}

	/// <summary>Lists a question's answer buttons: a select's options, Confirm and Decline, a lone single-choice question's options, the open
	/// page button, or the button that submits typed or picked answers. The one marked primary is what Ctrl+Enter gives.</summary>
	/// <param name="dialog">The question.</param>
	/// <param name="surface">The view outside the chat it shows in; null in the chat, where the message box can take a typed answer.</param>
	private List<DialogChoice> ListChoices( JsonElement dialog, string? surface )
	{
		var id = ReadText( dialog, "id" );
		var choices = new List<DialogChoice>();
		switch ( ReadText( dialog, "method" ) )
		{
			case "open_url":
				var destination = ReadText( dialog, "launchUrl", ReadText( dialog, "url" ) );
				if ( Uri.TryCreate( destination, UriKind.Absolute, out var uri ) && uri.Scheme is "https" or "http" )
					choices.Add( new( "open", "Open authorization page", "", "", false, true, () => context.Run( () =>
						Process.Start( new ProcessStartInfo( destination )
						{
							UseShellExecute = true,
						} ) ) ) );
				break;
			case "select" when dialog.TryGetProperty( "options", out var options ):
				var checkedIndices = dialog.TryGetProperty( "checkedIndices", out var selected )
					? selected.EnumerateArray().Select( item => item.GetInt32() ).ToHashSet() : [];
				var marker = ReadText( dialog, "selectionMarker" );
				var markableCount = dialog.TryGetProperty( "markableCount", out var count ) ? count.GetInt32() : options.GetArrayLength();
				// Only a plain list recommends its first-focused option; a marked list shows its state with the markers.
				var initialIndex = dialog.TryGetProperty( "initialIndex", out var initial ) ? initial.GetInt32() : -1;
				var details = dialog.TryGetProperty( "optionDetails", out var found ) ? found : default;
				var index = 0;
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
					choices.Add( new( "option:" + index, value, description, mark, isSelected, marker.Length == 0 && index == initialIndex,
						() => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, value } ) ) );
					index++;
				}
				break;
			case "confirm":
				choices.Add( new( "confirm", "Confirm", "", "", false, true, () => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, confirmed = true } ) ) );
				choices.Add( new( "decline", "Decline", "", "", false, false, () => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, confirmed = false } ) ) );
				break;
			case "ask":
				if ( ReadQuickQuestion( dialog ) is { } question )
				{
					var recommended = question.TryGetProperty( "recommended", out var pick ) && pick.ValueKind == JsonValueKind.Number && pick.TryGetInt32( out var at ) ? at : -1;
					var position = 0;
					foreach ( var option in question.GetProperty( "options" ).EnumerateArray() )
					{
						var label = ReadText( option, "label" );
						choices.Add( new( "option:" + label, label, ReadText( option, "description" ), "", false, position++ == recommended,
							() => UpdateQuickAnswer( dialog, question, label ) ) );
					}
				}
				else
					choices.Add( new( "submit", "Submit answers", "", "", false, true, () => UpdateQuestionsAnswer( dialog ) ) );
				break;
			case "input" or "editor" when surface is not null || ComposerAnswerKey() != id:
				var prefill = ReadText( dialog, "prefill", ReadText( dialog, "value" ) );
				choices.Add( new( "submit", "Submit answer", "", "", false, true, () => UpdateDialogAnswer( id,
					new
					{
						type = "extension_ui_response",
						id,
						value = EnsureDialogEditor( id, prefill ).Document.GetText()
					} ) ) );
				break;
			case "form":
				var form = ReadForm( dialog );
				if ( ReadQuickField( form ) is { } field )
				{
					for ( var at = 0; at < field.Options.Count; at++ )
					{
						var option = field.Options[at];
						var picked = at;
						if ( !option.IsDisabled )
							choices.Add( new( "option:" + at, option.Label, "", "", false, option.IsChecked, () => UpdateFormPick( dialog, field, picked ) ) );
					}
				}
				else
					choices.Add( new( "submit", "Submit", "", "", false, true, () => UpdateFormSubmit( dialog ) ) );
				break;
		}
		return choices;
	}

	/// <summary>Gets what Ctrl+Enter in the message box answers to the pending question, as its filled button shows; null when nothing is pending or
	/// the question recommends no answer.</summary>
	private Action? ReadPrimaryAnswer() =>
		session.PendingDialog is { } dialog ? ListChoices( dialog, null ).FirstOrDefault( choice => choice.IsPrimary )?.Answer : null;

	/// <summary>Gets the only question of a questionnaire that asks one, or null.</summary>
	private static JsonElement? ReadLoneQuestion( JsonElement dialog ) =>
		ReadText( dialog, "method" ) == "ask" && dialog.TryGetProperty( "questions", out var questions ) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() == 1
			? questions[0] : null;

	/// <summary>Gets the only question of a questionnaire when it takes one choice, so a click on an option answers it; null otherwise.</summary>
	private static JsonElement? ReadQuickQuestion( JsonElement dialog ) =>
		ReadLoneQuestion( dialog ) is { } question && ReadText( question, "multi" ) is not ("True" or "true") ? question : null;

	/// <summary>Lays answer buttons out in a wrapping row, or one under another when any has a description, so descriptions stay readable.</summary>
	private static Blob BuildChoices( string key, IReadOnlyList<DialogChoice> choices )
	{
		var buttons = choices.Select( choice => (Blob)BuildChoice( choice ) ).ToList();
		return choices.Any( choice => choice.Description.Length > 0 )
			? new Container { Key = key, Gap = Theme.Current.Spacing * 0.75, Children = buttons }
			: new Row { Key = key, FlexWrap = FlexWrap.Wrap, Gap = Theme.Current.Spacing, Children = buttons };
	}

	/// <summary>Builds one answer as a rounded button: the primary one filled in the text color and showing its Ctrl+Enter shortcut, a selected one
	/// outlined in the accent color, the others on a soft fill.</summary>
	private static Button BuildChoice( DialogChoice choice )
	{
		var theme = Theme.Current;
		var color = choice.IsPrimary ? theme.Canvas : theme.Text;
		var text = new List<Blob> { new Text { Content = choice.Label, Color = color, MinWidth = 0 } };
		if ( choice.Description.Length > 0 )
			text.Add( new Text { Content = choice.Description, Color = choice.IsPrimary ? theme.Canvas.WithAlpha( 0.7 ) : theme.Muted, FontSize = ViewStyle.SmallSize, MinWidth = 0 } );
		var row = new List<Blob>();
		if ( choice.Marker.Length > 0 )
			row.Add( MarkdownView.BuildMarker( choice.Marker ) );
		row.Add( new Container { FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Gap = 2, Children = text } );
		if ( choice.IsPrimary )
			row.Add( BuildShortcut( theme.Canvas ) );
		return new Button
		{
			Key = choice.Key,
			AccessibilityName = choice.Description.Length > 0 ? $"{choice.Label}: {choice.Description}" : choice.Label,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinHeight = theme.ControlHeight + 2,
			Padding = new Edges( theme.Spacing * 0.5, theme.Spacing * 1.25 ),
			BorderRadius = theme.Radius + 1,
			BorderWidth = theme.BorderWidth,
			BorderColor = choice.IsSelected ? theme.Accent : Color.Transparent,
			BackgroundColor = choice.IsPrimary ? theme.Text : choice.IsSelected ? theme.Accent.WithAlpha( 0.16 ) : theme.Text.WithAlpha( 0.07 ),
			Color = color,
			FontSize = theme.FontSize,
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = choice.Answer,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, choice.Answer ),
				new( Key.Space, KeyModifiers.None, choice.Answer ),
			],
			HoverStyle = new Style
			{
				BackgroundColor = choice.IsPrimary ? theme.Text.WithAlpha( 0.86 ) : choice.IsSelected ? theme.Accent.WithAlpha( 0.24 ) : theme.Text.WithAlpha( 0.13 ),
			},
			FocusStyle = new Style { BorderColor = theme.Accent },
			Children = row,
		};
	}

	/// <summary>Builds the Ctrl+Enter shortcut as two small key caps in a color that reads on the button they sit on.</summary>
	private static Row BuildShortcut( Color color ) => new()
	{
		FlexShrink = 0,
		AlignItems = AlignItems.Center,
		Gap = 3,
		Children =
		[
			BuildKeyCap( new Text { Content = "Ctrl", Color = color, FontSize = ViewStyle.SmallSize - 1, TextWrap = TextWrap.NoWrap }, color ),
			BuildKeyCap( ChatIcons.BuildIcon( ReturnGlyph, color ), color ),
		],
	};

	/// <summary>Builds one key cap: a thin rounded outline around a key's name or glyph.</summary>
	private static Container BuildKeyCap( Blob face, Color color ) => new()
	{
		Height = KeyCapHeight,
		MinWidth = KeyCapHeight,
		Padding = new Edges( 0, 4 ),
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		BorderRadius = 3,
		BorderWidth = 1,
		BorderColor = color.WithAlpha( 0.4 ),
		Children = [face],
	};

	/// <summary>Builds one question of a questionnaire: its text, its options, and a way to write a custom answer.</summary>
	/// <param name="dialog">The questionnaire.</param>
	/// <param name="question">The question.</param>
	/// <param name="surface">The view outside the chat it shows in; null in the chat.</param>
	/// <param name="showsText">Whether to show the question's text; false when the card's heading already is that text.</param>
	/// <param name="quick">The answer buttons of a lone single-choice question, which answer on a click; null for options that are picked, then submitted.</param>
	private Blob BuildQuestion( JsonElement dialog, JsonElement question, string? surface, bool showsText, IReadOnlyList<DialogChoice>? quick )
	{
		var key = ReadText( dialog, "id" ) + "." + ReadText( question, "id" );
		var multi = ReadText( question, "multi" ) is "True" or "true";
		var children = new List<Blob>();
		if ( showsText )
			children.Add( MarkdownView.Build( ReadText( question, "question" ), OpenLink ) );
		if ( quick is null )
		{
			var selected = session.ReadSelection( key );
			var options = new List<DialogChoice>();
			foreach ( var option in question.GetProperty( "options" ).EnumerateArray() )
			{
				var label = ReadText( option, "label" );
				var isSelected = selected.Contains( label );
				options.Add( new( "option:" + label, label, ReadText( option, "description" ), multi ? (isSelected ? "☑" : "☐") : "", isSelected, false,
					() => context.Run( () => session.UpdateSelection( key, label, multi ) ) ) );
			}
			children.Add( BuildChoices( "options", options ) );
		}
		else if ( quick.Count > 0 )
			children.Add( BuildChoices( "options", quick ) );

		// A lone question in the chat takes its own answer from the message box below instead of a second text box.
		if ( surface is null && ComposerAnswerKey() == key )
			children.Add( BuildText( multi ? "Or type your own answer in the message box below." : "Or type your own answer in the message box below; it replaces the choice.",
				muted: true, size: ViewStyle.SmallSize ) );
		else
		{
			children.Add( BuildText( multi ? "Or add your own answer" : "Or write your own answer", muted: true, size: ViewStyle.SmallSize ) );
			var custom = EnsureDialogEditor( key, "" );
			var box = BuildDialogEditor( surface is null ? custom : EnsureOutsideEditor( surface, key, custom ), "Custom answer", "answer:" + key );
			children.Add( box );
			if ( quick is not null )
			{
				// Options answer on a click, so the written answer has its own button, and Ctrl+Enter in its box sends it rather than the recommended option.
				void Submit() => UpdateQuestionsAnswer( dialog );
				box.KeyBindings = [new( Key.Enter, KeyModifiers.Primary, Submit )];
				var submit = BuildChoice( new( "submit", "Submit answer", "", "", false, false, Submit ) );
				submit.AlignSelf = AlignSelf.FlexStart;
				children.Add( submit );
			}
		}
		return new Container
		{
			Key = "question:" + key,
			Gap = Theme.Current.Spacing,
			Children = children,
		};
	}

	/// <summary>Answers a lone single-choice question with one of its options, as clicking the option does.</summary>
	private void UpdateQuickAnswer( JsonElement dialog, JsonElement question, string label )
	{
		var id = ReadText( dialog, "id" );
		UpdateDialogAnswer( id, new
		{
			type = "extension_ui_response",
			id,
			answers = new[] { new { id = ReadText( question, "id" ), selectedOptions = new[] { label }, customInput = "" } },
		} );
	}

	/// <summary>Gets an outside view's editor over a dialog editor's document, replacing one left over a document a reconnect discarded.</summary>
	private TextEditorController EnsureOutsideEditor( string surface, string key, TextEditorController editor )
	{
		if ( !_outsideEditors.TryGetValue( (surface, key), out var outside ) || outside.Document != editor.Document )
			_outsideEditors[(surface, key)] = outside = new( editor.Document );
		return outside;
	}

	/// <summary>Builds a single-line answer box over a dialog editor's document; Enter submits what it holds.</summary>
	/// <param name="key">The box's element key.</param>
	/// <param name="editor">The editor whose document keeps the typed text.</param>
	/// <param name="placeholder">Text shown while the box is empty.</param>
	/// <param name="name">The box's name for screen readers.</param>
	/// <param name="isSecret">Whether the typed text is masked.</param>
	/// <param name="submit">Submits the typed text.</param>
	private TextEntry BuildAnswerEntry( string key, TextEditorController editor, string placeholder, string name, bool isSecret, Action<string> submit ) => new()
	{
		Key = key,
		Value = editor.Document.GetText(),
		Controlled = true,
		Password = isSecret,
		Placeholder = placeholder,
		AccessibilityName = name,
		Height = AnswerHeight,
		Padding = Theme.Current.Spacing,
		BackgroundColor = Theme.Current.Panel,
		OnChange = value => context.Run( () => editor.Document.Apply( new TextChange( new TextRange( 0, editor.Document.Length ), value ) ) ),
		OnSubmit = submit,
	};

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
	/// questionnaire question or a form's lone single choice that takes its own answer, or null when the question takes no typed answer or has several
	/// text boxes.</summary>
	private string? ComposerAnswerKey()
	{
		if ( session.PendingDialog is not { } dialog )
			return null;

		var id = ReadText( dialog, "id" );
		return ReadText( dialog, "method" ) switch
		{
			// The message box shows what is typed, so a secret keeps its masked box.
			"input" when ReadText( dialog, "secret" ) is not ("True" or "true") => id,
			"ask" when dialog.TryGetProperty( "questions", out var questions ) && questions.GetArrayLength() == 1 => id + "." + ReadText( questions[0], "id" ),
			"form" when ReadQuickField( ReadForm( dialog ) ) is { HasOther: true } field => id + "." + field.Name,
			_ => null,
		};
	}

	/// <summary>Answers the pending question with text typed in the message box; false when the question does not take it there.</summary>
	private bool UpdateComposerAnswer( string text )
	{
		if ( session.PendingDialog is not { } dialog || ComposerAnswerKey() is not { } key )
			return false;

		var id = ReadText( dialog, "id" );
		var method = ReadText( dialog, "method" );
		if ( method == "input" )
		{
			UpdateDialogAnswer( id, new { type = "extension_ui_response", id, value = text } );
			return true;
		}
		if ( method == "form" && ReadQuickField( ReadForm( dialog ) ) is { } field )
		{
			UpdateFormOther( dialog, field, text );
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
			var selectedOptions = !isMulti && customInput.Length > 0 ? [] : session.ReadSelection( key ).ToArray();
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

	/// <summary>Sends one tool answer through the session, which removes its question only after the pipe accepts it.</summary>
	private void UpdateDialogAnswer( string id, object response ) => session.AnswerDialog( id, response );

	/// <summary>Releases completed dialog text, parsed forms and their errors and pictures, so old prompts do not accumulate.</summary>
	private void ReleaseDialogEditors( string id )
	{
		foreach ( var key in _dialogEditors.Keys.Where( key => key == id || key.StartsWith( id + ".", StringComparison.Ordinal ) ).ToArray() )
			_dialogEditors.Remove( key );
		foreach ( var key in _outsideEditors.Keys.Where( key => key.Key == id || key.Key.StartsWith( id + ".", StringComparison.Ordinal ) ).ToArray() )
			_outsideEditors.Remove( key );
		ReleaseForm( id );
	}
}
