using System.Globalization;
using System.Text;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Builds the compact question card that views outside the chat show, such as the notification banner, the bell popover and the
/// Notifications page: the question, its options as numbered list rows, an answer of one's own, and a footer with the hint and the actions.</summary>
/// <remarks>The host draws the card's frame and its header; this content keeps a 40 pixel gutter on the left, where the number keys sit. Each view
/// keeps its own expanded text, step, filter and touched fields in <see cref="_compactDialogs"/>, released when the question leaves.</remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines the left gutter of the compact card's text and options, in logical pixels; the number keys sit in it.</summary>
	private const int CompactIndent = 40;

	/// <summary>Defines the right inset of the compact card's content, in logical pixels.</summary>
	private const int CompactInset = 12;

	/// <summary>Defines the distance between a number key's left edge and a row's, in logical pixels, measured from the end of the gutter.</summary>
	private const int CompactKeyOffset = 26;

	/// <summary>Defines the size of an option's number key or check box, in logical pixels.</summary>
	private const int CompactKeySize = 16;

	/// <summary>Defines the size of a form check box, in logical pixels.</summary>
	private const int CompactCheckSize = 14;

	/// <summary>Defines the height of the footer's buttons, in logical pixels.</summary>
	private const int CompactButtonHeight = 26;

	/// <summary>Defines the size of the send button in an answer box, in logical pixels.</summary>
	private const int CompactSendSize = 22;

	/// <summary>Defines the line height of the compact card's text as a multiple of its font size.</summary>
	private const double CompactLineHeight = 1.45;

	/// <summary>Defines the size of the smallest text, such as field errors and the Recommended tag, in logical pixels.</summary>
	private const int CompactTinySize = 11;

	/// <summary>Defines how many lines the question and message show before Show all.</summary>
	private const int CompactClampLines = 3;

	/// <summary>Defines how many characters one line of the card holds, as an estimate: the card cannot measure its text before it is laid out.</summary>
	private const int CompactLineCharacters = 46;

	/// <summary>Defines the longest run of characters without a break opportunity before one is added, so long paths and links wrap.</summary>
	private const int CompactUnbrokenRun = 20;

	/// <summary>Defines how many options get a number key, and how many a list shows before it gets a filter.</summary>
	private const int CompactNumberedOptions = 9;

	/// <summary>Defines how many matches a filtered list shows before "+ N more".</summary>
	private const int CompactFilteredOptions = 4;

	/// <summary>Defines how many lines an answer box grows to before it scrolls.</summary>
	private const int CompactAnswerLines = 4;

	/// <summary>Defines the opacity of a primary button whose answer is not valid yet.</summary>
	private const double CompactUnreadyOpacity = 0.45;

	/// <summary>Defines the width of a form's label column, in logical pixels.</summary>
	private const int CompactLabelWidth = 96;

	/// <summary>Defines the longest label that fits the label column in two lines; a longer one goes above its field.</summary>
	private const int CompactLongLabel = 28;

	/// <summary>Defines the most options a single choice shows as a segmented bar.</summary>
	private const int CompactMaxSegments = 4;

	/// <summary>Defines the longest option label a segmented bar shows.</summary>
	private const int CompactSegmentLabel = 14;

	/// <summary>Defines the most fields one step of a form shows.</summary>
	private const int CompactPageFields = 6;

	/// <summary>Defines the longest prose block a form shows in the card; a longer one makes the form too big for it.</summary>
	private const int CompactRichText = 240;

	/// <summary>Defines the Segoe Fluent CheckMark glyph of a picked option.</summary>
	private const string CompactCheckGlyph = "\uE73E";

	/// <summary>Defines the Segoe Fluent Search glyph of the options filter.</summary>
	private const string CompactSearchGlyph = "\uE721";

	/// <summary>Defines the Segoe Fluent Send glyph of an answer box.</summary>
	private const string CompactSendGlyph = "\uE724";

	/// <summary>Defines the Segoe Fluent Back glyph of the step footer.</summary>
	private const string CompactBackGlyph = "\uE72B";

	/// <summary>Defines the Segoe Fluent ChevronDown glyph of a dropdown.</summary>
	private const string CompactDropGlyph = "\uE70D";

	/// <summary>Holds what each compact card shows of its question, by the view it is in and the question's id.</summary>
	private readonly Dictionary<(string Surface, string Id), CompactDialog> _compactDialogs = [];

	/// <summary>Holds what one view shows of one pending question in its compact card.</summary>
	private sealed class CompactDialog
	{
		/// <summary>Gets or sets whether the question shows all its text rather than three lines.</summary>
		public bool ShowsLead { get; set; }

		/// <summary>Gets or sets whether the message shows all its text rather than three lines.</summary>
		public bool ShowsMessage { get; set; }

		/// <summary>Gets or sets the step shown: a questionnaire's question, or its review after the last; a form's page.</summary>
		public int Step { get; set; }

		/// <summary>Gets or sets the text that filters a long option list.</summary>
		public string Filter { get; set; } = "";

		/// <summary>Gets or sets whether a long option list shows every match rather than the first few.</summary>
		public bool ShowsOptions { get; set; }

		/// <summary>Gets the names of the form fields whose dropdown is open.</summary>
		public HashSet<string> OpenFields { get; } = [];

		/// <summary>Gets the names of the form fields that show their error: those edited, and those a send or Next refused.</summary>
		public HashSet<string> Touched { get; } = [];

		/// <summary>Gets or sets what the card's look depends on that typing changes, such as answer box heights and visible errors, as last built.</summary>
		public string Shape { get; set; } = "";

		/// <summary>Moves to another step, which starts with its question's text clamped and its options unfiltered.</summary>
		public void MoveTo( int step )
		{
			Step = step;
			ShowsLead = false;
			Filter = "";
			ShowsOptions = false;
		}
	}

	/// <summary>One page of a compact form.</summary>
	/// <param name="Title">The section title the step line shows, as Markdown; empty for none.</param>
	/// <param name="Blocks">The page's prose and fields in document order; a section title inside the page is a heading block.</param>
	private sealed record CompactFormPage( string Title, List<FormBlock> Blocks );

	/// <summary>Builds the compact card's content for a pending question shown outside the chat.</summary>
	/// <param name="dialog">The question.</param>
	/// <param name="surface">The view it is in, which has its own text editors and card state.</param>
	private Blob BuildCompactDialog( JsonElement dialog, string surface )
	{
		var id = ReadText( dialog, "id" );
		if ( !_compactDialogs.TryGetValue( (surface, id), out var state ) )
			_compactDialogs.Add( (surface, id), state = new() );

		var card = ReadText( dialog, "method" ) switch
		{
			"ask" when dialog.TryGetProperty( "questions", out var questions ) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() >= 2
				=> BuildCompactSteps( dialog, surface, state, questions ),
			"ask" => BuildCompactAsk( dialog, surface, state ),
			"form" => BuildCompactForm( dialog, surface, state ),
			"input" or "editor" => BuildCompactInput( dialog, surface, state ),
			"open_url" => BuildCompactOpenUrl( dialog, surface, state ),
			_ => BuildCompactSelect( dialog, surface, state ),
		};
		state.Shape = ComputeCompactShape( dialog, state );
		return card;
	}

	/// <summary>Builds a select or confirm: the question, its options as numbered rows that answer on a click, and Skip.</summary>
	private Container BuildCompactSelect( JsonElement dialog, string surface, CompactDialog state )
	{
		var id = ReadText( dialog, "id" );
		var body = new List<Blob>();
		AddCompactLead( body, ReadTitle( dialog ), state );
		AddCompactMessage( body, dialog, state );
		var choices = ListChoices( dialog, surface );
		var numbered = AddCompactOptions( body, state, choices, isToggle: false, showsTag: ReadText( dialog, "method" ) == "select" );
		body.Add( BuildCompactFooter( null, FormatPressHint( numbered.Count ), [BuildCompactSkip( id )] ) );
		return BuildCompactCard( id, body, numbered, choices.FirstOrDefault( choice => choice.IsPrimary )?.Answer );
	}

	/// <summary>Builds a questionnaire with one question: its options as numbered rows, which answer on a click for a single choice and are picked,
	/// then submitted, for several; and a box for an answer of one's own.</summary>
	private Container BuildCompactAsk( JsonElement dialog, string surface, CompactDialog state )
	{
		var id = ReadText( dialog, "id" );
		var title = ReadText( dialog, "title" );
		var body = new List<Blob>();
		if ( ReadLoneQuestion( dialog ) is not { } question )
		{
			AddCompactLead( body, title.Length > 0 ? title : "Your input is needed", state );
			AddCompactMessage( body, dialog, state );
			body.Add( BuildCompactFooter( null, "", [BuildCompactSkip( id )] ) );
			return BuildCompactCard( id, body, [], null );
		}

		if ( title.Length > 0 )
			body.Add( BuildCompactCaption( title ) );
		AddCompactLead( body, ReadText( question, "question" ), state );
		AddCompactMessage( body, dialog, state );
		var key = id + "." + ReadText( question, "id" );
		var custom = EnsureDialogEditor( key, "" );
		var isTyped = custom.Document.GetText().Trim().Length > 0;
		void Submit() => UpdateQuestionsAnswer( dialog );
		if ( !IsMultiQuestion( question ) )
		{
			// A single choice answers on a click; a written answer has its own send button, and Ctrl+Enter sends it once there is one.
			var choices = ListChoices( dialog, surface );
			var quick = AddCompactOptions( body, state, choices, isToggle: false, showsTag: true );
			body.Add( BuildCompactAnswer( dialog, state, key, EnsureOutsideEditor( surface, key, custom ), "Other answer…", 1, Submit, showsSend: true ) );
			body.Add( BuildCompactFooter( null, JoinHints( FormatPressHint( quick.Count ), isTyped ? "Ctrl ⏎ sends your answer" : "" ), [BuildCompactSkip( id )] ) );
			return BuildCompactCard( id, body, quick, isTyped ? Submit : choices.FirstOrDefault( choice => choice.IsPrimary )?.Answer );
		}

		var picks = session.ReadSelection( key );
		var options = ListQuestionChoices( question, picks, isMulti: true, label => () => UpdateCompact( () => session.UpdateSelection( key, label, true ) ) );
		var numbered = AddCompactOptions( body, state, options, isToggle: true, showsTag: true );
		body.Add( BuildCompactAnswer( dialog, state, key, EnsureOutsideEditor( surface, key, custom ), "Other answer…", 1, Submit, showsSend: false ) );
		var hint = picks.Count > 0 ? $"{picks.Count.ToString( CultureInfo.InvariantCulture )} picked" : FormatPressHint( numbered.Count );
		body.Add( BuildCompactFooter( null, hint, [BuildCompactSkip( id ), BuildCompactButton( "submit", "Submit · Ctrl ⏎", Submit, isPrimary: true )] ) );
		return BuildCompactCard( id, body, numbered, Submit );
	}

	/// <summary>Builds a questionnaire with several questions one question per step, with a step bar, Back and Next, and a review of every answer
	/// after the last, which sends them all.</summary>
	private Container BuildCompactSteps( JsonElement dialog, string surface, CompactDialog state, JsonElement questions )
	{
		var id = ReadText( dialog, "id" );
		var title = ReadText( dialog, "title" );
		var count = questions.GetArrayLength();
		var step = Math.Clamp( state.Step, 0, count );
		var body = new List<Blob> { BuildCompactStepBar( count, step ) };
		void Go( int to ) => UpdateCompact( () => state.MoveTo( to ) );
		var back = step > 0 ? BuildCompactBack( () => Go( step - 1 ) ) : null;
		void Send() => UpdateQuestionsAnswer( dialog );
		if ( step == count )
		{
			body.Add( BuildCompactCaption( title.Length > 0 ? "Review · " + title : "Review your answers" ) );
			var rows = new List<Blob>();
			var jumps = new List<Action>();
			var answered = 0;
			for ( var at = 0; at < count; at++ )
			{
				var question = questions[at];
				var value = FormatQuestionAnswer( id + "." + ReadText( question, "id" ), IsMultiQuestion( question ) );
				answered += value.Length > 0 ? 1 : 0;
				var to = at;
				jumps.Add( () => Go( to ) );
				rows.Add( BuildCompactReviewRow( at + 1, ReadText( question, "question" ), value, jumps[^1] ) );
			}
			body.Add( new Container { Key = "review", MinWidth = 0, Padding = new Edges( 0, 0, 4, 0 ), Children = rows } );
			var progress = $"{answered.ToString( CultureInfo.InvariantCulture )} of {count.ToString( CultureInfo.InvariantCulture )} answered";
			body.Add( BuildCompactFooter( back, progress, [BuildCompactSkip( id ), BuildCompactButton( "send", "Send answers · Ctrl ⏎", Send, isPrimary: true )] ) );
			return BuildCompactCard( id, body, jumps, Send );
		}

		var current = questions[step];
		var isMulti = IsMultiQuestion( current );
		var key = id + "." + ReadText( current, "id" );
		var picks = session.ReadSelection( key );
		var custom = EnsureDialogEditor( key, "" );
		var isTyped = custom.Document.GetText().Trim().Length > 0;
		void Next() => Go( step + 1 );
		body.Add( BuildCompactCaption( $"Question {(step + 1).ToString( CultureInfo.InvariantCulture )} of {count.ToString( CultureInfo.InvariantCulture )}"
			+ (title.Length > 0 ? " · " + title : "") ) );
		AddCompactLead( body, ReadText( current, "question" ), state );
		if ( step == 0 )
			AddCompactMessage( body, dialog, state );

		// A single choice picks on a click and moves on; several are picked, then Next moves on.
		Action Pick( string label ) => isMulti
			? () => UpdateCompact( () => session.UpdateSelection( key, label, true ) )
			: () => UpdateCompact( () =>
			{
				if ( !session.ReadSelection( key ).Contains( label ) )
					session.UpdateSelection( key, label, false );
				state.MoveTo( step + 1 );
			} );
		var options = ListQuestionChoices( current, picks, isMulti, Pick );
		var numbered = AddCompactOptions( body, state, options, isMulti, showsTag: true );
		body.Add( BuildCompactAnswer( dialog, state, key, EnsureOutsideEditor( surface, key, custom ), "Other answer…", 1, Next, showsSend: !isMulti ) );
		var showsNext = isMulti || picks.Count > 0 || isTyped;
		var buttons = new List<Blob> { BuildCompactSkip( id ) };
		if ( showsNext )
			buttons.Add( BuildCompactButton( "next", "Next", Next, isPrimary: true ) );
		var hint = isMulti && picks.Count > 0 ? $"{picks.Count.ToString( CultureInfo.InvariantCulture )} picked" : FormatPressHint( numbered.Count );
		body.Add( BuildCompactFooter( back, hint, buttons ) );
		return BuildCompactCard( id, body, numbered, showsNext ? Next : options.FirstOrDefault( choice => choice.IsPrimary )?.Answer );
	}

	/// <summary>Builds a one-line input or a multiline editor: the question and the answer box, which is the answer, sent with Send.</summary>
	private Container BuildCompactInput( JsonElement dialog, string surface, CompactDialog state )
	{
		var id = ReadText( dialog, "id" );
		var isEditor = ReadText( dialog, "method" ) == "editor";
		var body = new List<Blob>();
		AddCompactLead( body, ReadTitle( dialog ), state );
		AddCompactMessage( body, dialog, state );
		var editor = EnsureDialogEditor( id, ReadText( dialog, "prefill", ReadText( dialog, "value" ) ) );
		// Outside the chat the question lists its own submit answer, which sends the editor's text.
		var send = ListChoices( dialog, surface ) is [{ } submit, ..] ? submit.Answer : null;
		var placeholder = ReadText( dialog, "placeholder" ) is { Length: > 0 } given ? given : "Type your answer…";
		if ( isEditor )
			body.Add( BuildCompactAnswer( dialog, state, id, EnsureOutsideEditor( surface, id, editor ), placeholder, 2, send, showsSend: false ) );
		else
		{
			var entry = BuildCompactEntry( dialog, state, "input:" + id, editor, placeholder, "Tool answer", ReadText( dialog, "secret" ) is "True" or "true", false, null,
				send is { } sending ? _ => sending() : null );
			entry.Height = ComputeCompactLineHeight();
			entry.Padding = 0;
			entry.BorderWidth = 0;
			entry.BackgroundColor = Color.Transparent;
			entry.FlexGrow = 1;
			entry.FlexShrink = 1;
			body.Add( BuildCompactAnswerBox( "answer-box:" + id, entry, send ) );
		}

		var buttons = new List<Blob> { BuildCompactSkip( id ) };
		if ( send is not null )
			buttons.Add( BuildCompactButton( "send", "Send · Ctrl ⏎", send, isPrimary: true ) );
		body.Add( BuildCompactFooter( null, isEditor ? "Ctrl ⏎ sends" : "Enter sends", buttons ) );
		return BuildCompactCard( id, body, [], send );
	}

	/// <summary>Gets the height of one line of the card's body text, in logical pixels.</summary>
	private static double ComputeCompactLineHeight() => Theme.Current.FontSize * CompactLineHeight;

	/// <summary>Builds an authorization request: its text and instructions, Dismiss, and the button that opens the page.</summary>
	private Container BuildCompactOpenUrl( JsonElement dialog, string surface, CompactDialog state )
	{
		var id = ReadText( dialog, "id" );
		var body = new List<Blob>();
		AddCompactLead( body, ReadTitle( dialog ), state );
		AddCompactMessage( body, dialog, state );
		var instructions = ReadText( dialog, "instructions" );
		if ( instructions.Length > 0 )
		{
			var text = BuildCompactPlain( instructions, Theme.Current.Muted, ViewStyle.SmallSize, 0 );
			text.Selectable = true;
			body.Add( new Container { Key = "instructions", MinWidth = 0, Padding = new Edges( 0, CompactInset, 4, CompactIndent ), Children = [text] } );
		}
		var open = ListChoices( dialog, surface ).FirstOrDefault( choice => choice.IsPrimary );
		var buttons = new List<Blob> { BuildCompactButton( "dialog-close", "Dismiss", () => context.Run( () => session.DismissDialog( id ) ), isPrimary: false ) };
		if ( open is not null )
			buttons.Add( BuildCompactButton( "open", open.Label, open.Answer, isPrimary: true ) );
		body.Add( BuildCompactFooter( null, "", buttons ) );
		return BuildCompactCard( id, body, [], open?.Answer );
	}

	/// <summary>Builds a form: a lone single choice as numbered rows that answer on a click; a form too rich for the card as its summary; any other
	/// form as label and control rows, on one page or one section per step.</summary>
	private Container BuildCompactForm( JsonElement dialog, string surface, CompactDialog state )
	{
		var id = ReadText( dialog, "id" );
		var form = ReadForm( dialog );
		var title = ReadText( dialog, "title" );
		var lead = title.Length > 0 ? title : ReadFormLead( form ).Lead;
		var body = new List<Blob>();
		if ( IsRichForm( form ) )
		{
			AddCompactLead( body, lead, state );
			var pictures = form.Sections.SelectMany( section => section.Blocks ).Count( block => block is FormImage or FormSvg );
			var summary = pictures > 0 ? $"Form with {Count( pictures, "image", "images" )} and {Count( form.Fields.Count, "field", "fields" )}"
				: $"Form with {Count( form.Fields.Count, "field", "fields" )}";
			body.Add( BuildCompactCaption( summary ) );
			body.Add( BuildCompactFooter( null, "Answer this in the chat", [] ) );
			return BuildCompactCard( id, body, [], null );
		}

		if ( ReadQuickField( form ) is { } field )
		{
			AddCompactLead( body, lead, state );
			if ( field.Label.Length > 0 && field.Label != lead )
				body.Add( BuildCompactCaption( ReadPlainText( field.Label ) ) );
			AddCompactMessage( body, dialog, state );
			var choices = ListChoices( dialog, surface );
			var quick = AddCompactOptions( body, state, choices, isToggle: false, showsTag: false );
			var isTyped = false;
			Action? sendOther = null;
			if ( field.HasOther )
			{
				var otherKey = id + "." + field.Name + ".other";
				var other = EnsureDialogEditor( otherKey, "" );
				isTyped = other.Document.GetText().Trim().Length > 0;
				sendOther = () => UpdateFormOther( dialog, field, other.Document.GetText() );
				body.Add( BuildCompactAnswer( dialog, state, otherKey, EnsureOutsideEditor( surface, otherKey, other ), "Other answer…", 1, sendOther, showsSend: true ) );
			}
			body.Add( BuildCompactFooter( null, JoinHints( FormatPressHint( quick.Count ), isTyped ? "Ctrl ⏎ sends your answer" : "" ), [BuildCompactSkip( id )] ) );
			return BuildCompactCard( id, body, quick, isTyped ? sendOther : choices.FirstOrDefault( choice => choice.IsPrimary )?.Answer );
		}

		var pages = ListFormPages( form );
		var page = Math.Clamp( state.Step, 0, pages.Count - 1 );
		var isLast = page == pages.Count - 1;
		var errors = ComputeFormErrors( id, form );
		var pageFields = pages[page].Blocks.OfType<FormFieldBlock>().Select( block => block.Field.Name ).Where( name => name.Length > 0 ).ToList();
		var pageErrors = pageFields.Count( errors.ContainsKey );
		void Send()
		{
			if ( errors.Count == 0 )
			{
				UpdateFormSubmit( dialog );
				return;
			}

			// A refused send shows every error, and moves to the first page with one.
			UpdateCompact( () =>
			{
				state.Touched.UnionWith( errors.Keys );
				var first = pages.FindIndex( candidate => candidate.Blocks.OfType<FormFieldBlock>().Any( block => errors.ContainsKey( block.Field.Name ) ) );
				if ( first >= 0 && first != page )
					state.MoveTo( first );
			} );
		}
		void Next() => UpdateCompact( () =>
		{
			if ( pageErrors > 0 )
				state.Touched.UnionWith( pageFields.Where( errors.ContainsKey ) );
			else
				state.MoveTo( page + 1 );
		} );
		var primary = isLast ? (Action)Send : Next;

		if ( pages.Count > 1 )
		{
			body.Add( BuildCompactStepBar( pages.Count, page ) );
			var pageTitle = pages[page].Title.Length > 0 ? ReadPlainText( pages[page].Title ) : "";
			body.Add( BuildCompactCaption( $"Step {(page + 1).ToString( CultureInfo.InvariantCulture )} of {pages.Count.ToString( CultureInfo.InvariantCulture )}"
				+ (pageTitle.Length > 0 ? " · " + pageTitle : "") ) );
		}
		if ( page == 0 )
		{
			AddCompactLead( body, lead, state );
			AddCompactMessage( body, dialog, state );
		}

		var rows = new List<Blob>();
		foreach ( var block in pages[page].Blocks )
		{
			rows.Add( block switch
			{
				FormMarkdown { IsHeading: true } heading => BuildCompactPlain( ReadPlainText( heading.Markdown ), Theme.Current.Muted, CompactTinySize, 1, isBold: true ),
				FormMarkdown text => BuildCompactInline( text.Markdown, Theme.Current.Muted, ViewStyle.SmallSize, CompactClampLines ),
				FormFieldBlock { Field: var shown } => BuildCompactField( dialog, surface, state, shown, errors, primary ),
				_ => new Container(),
			} );
		}
		body.Add( new Container { Key = "form:" + page.ToString( CultureInfo.InvariantCulture ), MinWidth = 0, Gap = 10, Padding = new Edges( 0, CompactInset, 0, CompactIndent ), Children = rows } );

		var unready = isLast ? errors.Count : pageErrors;
		var hint = unready > 0 ? $"{Count( unready, "field", "fields" )} to fix" : pageFields.Count > 1 ? "Tab to move" : "";
		var back = page > 0 ? BuildCompactBack( () => UpdateCompact( () => state.MoveTo( page - 1 ) ) ) : null;
		body.Add( BuildCompactFooter( back, hint,
			[BuildCompactSkip( id ), BuildCompactButton( isLast ? "send" : "next", isLast ? "Send · Ctrl ⏎" : "Next", primary, isPrimary: true, isReady: unready == 0 )] ) );
		return BuildCompactCard( id, body, [], primary );
	}

	/// <summary>Builds one form field as a row of its label and its control, or with the label above when it is too long for the label column,
	/// and the field's error under the control once it shows.</summary>
	/// <param name="dialog">The form dialog.</param>
	/// <param name="surface">The view the card is in.</param>
	/// <param name="state">The card's state.</param>
	/// <param name="field">The field.</param>
	/// <param name="errors">The message of each field that is not valid, by field name.</param>
	/// <param name="submit">Does what Enter in a one-line box does: Next, or Send on the last page.</param>
	private Blob BuildCompactField( JsonElement dialog, string surface, CompactDialog state, FormField field, IReadOnlyDictionary<string, string> errors, Action submit )
	{
		var theme = Theme.Current;
		var key = ReadText( dialog, "id" ) + "." + field.Name;
		var error = state.Touched.Contains( field.Name ) && errors.TryGetValue( field.Name, out var found ) ? found : null;
		var isInvalid = error is not null;
		var control = field switch
		{
			FormChoiceField choice => BuildCompactChoiceField( dialog, state, choice, key, isInvalid ),
			FormTextField text => BuildCompactTextField( dialog, surface, state, text, key, isInvalid, submit ),
			_ => new Container(),
		};
		var column = new List<Blob> { control };
		if ( error is not null )
			column.Add( new Text { Content = error, Color = theme.Error, FontFamily = ViewStyle.FontFamily, FontSize = CompactTinySize, MinWidth = 0, TextMaxLines = 2, TextTrimming = TextTrimming.Ellipsis } );

		var label = ReadFieldLabel( field );
		var isStacked = label.Length > CompactLongLabel;
		var spans = new List<TextSpan> { new( SoftenBreaks( label ), new Style() ) };
		if ( field.IsRequired )
			spans.Add( new( " *", new Style { Color = theme.Error } ) );
		var caption = new Text( [.. spans] )
		{
			Color = theme.Muted,
			FontFamily = ViewStyle.FontFamily,
			FontSize = ViewStyle.SmallSize,
			MinWidth = 0,
			TextMaxLines = isStacked ? 0 : 2,
			TextTrimming = TextTrimming.Ellipsis,
		};
		if ( isStacked )
			return new Container { Key = "field:" + key, MinWidth = 0, Gap = 4, Children = [caption, .. column] };

		return new Row
		{
			Key = "field:" + key,
			MinWidth = 0,
			Gap = 10,
			AlignItems = AlignItems.FlexStart,
			Children =
			[
				new Container { Width = CompactLabelWidth, FlexShrink = 0, Padding = new Edges( 6, 0, 0, 0 ), Children = [caption] },
				new Container { FlexGrow = 1, FlexShrink = 1, FlexBasis = 0, MinWidth = 0, Gap = 4, Children = column },
			],
		};
	}

	/// <summary>Builds a choice field's control: a segmented bar for a few short single choices, check boxes for several choices, and a dropdown that
	/// opens its options as rows otherwise; with a box for an answer of one's own when the field offers one.</summary>
	private Blob BuildCompactChoiceField( JsonElement dialog, CompactDialog state, FormChoiceField field, string key, bool isInvalid )
	{
		var theme = Theme.Current;
		var picks = ReadFormPicks( key );
		var options = field.Options.Select( (option, index) => (Index: index, Option: option) ).Where( item => !item.Option.IsDisabled ).ToList();
		Action Pick( int index ) => () => UpdateCompact( () =>
		{
			state.Touched.Add( field.Name );
			if ( !field.IsMulti )
				state.OpenFields.Remove( field.Name );
			// A single choice keeps its pick when it is picked again, as a radio button does.
			if ( field.IsMulti || !picks.Contains( index ) )
				session.UpdateSelection( key, index.ToString( CultureInfo.InvariantCulture ), field.IsMulti );
		} );

		Blob control;
		if ( !field.IsMulti && !field.IsSelect && options.Count is > 1 and <= CompactMaxSegments && options.All( item => item.Option.Label.Length <= CompactSegmentLabel ) )
			control = BuildCompactSegments( options, picks, Pick, isInvalid );
		else if ( field.IsMulti && !field.IsSelect )
			control = BuildCompactChecks( options, picks, Pick );
		else
		{
			var isOpen = state.OpenFields.Contains( field.Name );
			var picked = options.Where( item => picks.Contains( item.Index ) ).Select( item => item.Option.Label ).ToList();
			var value = string.Join( ", ", picked );
			void Toggle() => UpdateCompact( () =>
			{
				if ( !state.OpenFields.Remove( field.Name ) )
					state.OpenFields.Add( field.Name );
			} );
			var dropdown = new Button
			{
				Key = "dropdown",
				AccessibilityName = $"{ReadFieldLabel( field )}: {(value.Length > 0 ? value : "none picked")}",
				FlexDirection = FlexDirection.Row,
				AlignItems = AlignItems.Center,
				Gap = 6,
				Height = theme.ControlHeight,
				MinWidth = 0,
				Padding = new Edges( 0, 8 ),
				BackgroundColor = theme.Canvas,
				BorderColor = isInvalid ? theme.Error : isOpen ? theme.Accent : theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				Focusable = true,
				Cursor = Cursor.Pointer,
				OnClick = Toggle,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, Toggle ), new( Key.Space, KeyModifiers.None, Toggle )],
				FocusStyle = new Style { BorderColor = theme.Accent },
				Children =
				[
					new Text
					{
						Content = value.Length > 0 ? value : "Choose…",
						Color = value.Length > 0 ? theme.Text : theme.Muted,
						FontFamily = ViewStyle.FontFamily,
						FontSize = theme.FontSize,
						FlexGrow = 1,
						FlexShrink = 1,
						MinWidth = 0,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
					BuildCompactGlyph( CompactDropGlyph, theme.Muted, 9 ),
				],
			};
			if ( !isOpen )
				control = dropdown;
			else
			{
				var rows = options.Select( item => (Blob)BuildCompactRow( new( "option:" + item.Index.ToString( CultureInfo.InvariantCulture ), item.Option.Label, "", "",
					picks.Contains( item.Index ), false, Pick( item.Index ) ), 0, isToggle: true, showsTag: false, gutter: 32, inset: 8 ) ).ToList();
				control = new Container
				{
					MinWidth = 0,
					Gap = 4,
					Children =
					[
						dropdown,
						new Container
						{
							Key = "dropdown-options",
							MinWidth = 0,
							Padding = new Edges( 2, 0 ),
							BackgroundColor = theme.Canvas,
							BorderColor = theme.Border,
							BorderWidth = theme.BorderWidth,
							BorderRadius = theme.Radius,
							Children = rows,
						},
					],
				};
			}
		}
		if ( !field.HasOther )
			return control;

		var other = BuildCompactEntry( dialog, state, "other:" + key, EnsureDialogEditor( key + ".other", "" ), "Other…", "Other answer", false, false,
			() => state.Touched.Add( field.Name ), null );
		return new Container { MinWidth = 0, Gap = 4, Children = [control, other] };
	}

	/// <summary>Builds a single choice as a segmented bar: equal segments split by lines, the picked one tinted and outlined in the accent color.</summary>
	private static Row BuildCompactSegments( List<(int Index, FormOption Option)> options, HashSet<int> picks, Func<int, Action> pick, bool isInvalid )
	{
		var theme = Theme.Current;
		var segments = new List<Blob>();
		for ( var at = 0; at < options.Count; at++ )
		{
			var (index, option) = options[at];
			var isPicked = picks.Contains( index );
			var action = pick( index );
			var segment = new Button
			{
				Key = "segment:" + index.ToString( CultureInfo.InvariantCulture ),
				AccessibilityName = isPicked ? option.Label + ", picked" : option.Label,
				FlexGrow = 1,
				FlexShrink = 1,
				FlexBasis = 0,
				MinWidth = 0,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Padding = new Edges( 0, 6 ),
				BorderWidth = at < options.Count - 1 ? new Edges( 0, theme.BorderWidth, 0, 0 ) : 0,
				BorderColor = theme.Border,
				BackgroundColor = isPicked ? theme.Accent.WithAlpha( 0.16 ) : Color.Transparent,
				Focusable = true,
				Cursor = Cursor.Pointer,
				OnClick = action,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, action ), new( Key.Space, KeyModifiers.None, action )],
				HoverStyle = new Style { BackgroundColor = isPicked ? theme.Accent.WithAlpha( 0.24 ) : theme.Hover },
				FocusStyle = new Style { BackgroundColor = isPicked ? theme.Accent.WithAlpha( 0.24 ) : theme.Hover },
				Children =
				[
					new Text
					{
						Content = option.Label,
						Color = isPicked ? theme.Text : theme.Muted,
						FontFamily = ViewStyle.FontFamily,
						FontSize = ViewStyle.SmallSize,
						MinWidth = 0,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
				],
			};
			if ( isPicked )
			{
				// An inward outline draws the picked segment's accent ring inside its bounds, as an inset shadow would.
				segment.OutlineColor = theme.Accent;
				segment.OutlineWidth = 1;
				segment.OutlineOffset = -1;
			}
			segments.Add( segment );
		}
		return new Row
		{
			Key = "segments",
			Height = theme.ControlHeight,
			MinWidth = 0,
			BackgroundColor = theme.Canvas,
			BorderColor = isInvalid ? theme.Error : theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			Overflow = Overflow.Hidden,
			Children = segments,
		};
	}

	/// <summary>Builds several choices as small check boxes in a wrapping row.</summary>
	private static Row BuildCompactChecks( List<(int Index, FormOption Option)> options, HashSet<int> picks, Func<int, Action> pick )
	{
		var theme = Theme.Current;
		var checks = new List<Blob>();
		foreach ( var (index, option) in options )
		{
			var isPicked = picks.Contains( index );
			var action = pick( index );
			checks.Add( new Button
			{
				Key = "check:" + index.ToString( CultureInfo.InvariantCulture ),
				AccessibilityName = isPicked ? option.Label + ", checked" : option.Label,
				FlexDirection = FlexDirection.Row,
				AlignItems = AlignItems.Center,
				Gap = 6,
				MinWidth = 0,
				MaxWidth = Length.Percent( 100 ),
				BorderRadius = 3,
				Focusable = true,
				Cursor = Cursor.Pointer,
				OnClick = action,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, action ), new( Key.Space, KeyModifiers.None, action )],
				FocusStyle = new Style { BackgroundColor = theme.Hover },
				Children =
				[
					BuildCompactBox( isPicked ? CompactCheckGlyph : "", CompactCheckSize, isPicked, isPicked ),
					new Text
					{
						Content = option.Label,
						Color = theme.Text,
						FontFamily = ViewStyle.FontFamily,
						FontSize = ViewStyle.SmallSize,
						MinWidth = 0,
						FlexShrink = 1,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
				],
			} );
		}
		return new Row
		{
			Key = "checks",
			MinWidth = 0,
			FlexWrap = FlexWrap.Wrap,
			RowGap = 4,
			ColumnGap = 12,
			Padding = new Edges( 5, 0, 0, 0 ),
			Children = checks,
		};
	}

	/// <summary>Builds a text field's control: a one-line box, or a multiline editor two lines tall that grows to four.</summary>
	private Blob BuildCompactTextField( JsonElement dialog, string surface, CompactDialog state, FormTextField field, string key, bool isInvalid, Action submit )
	{
		var theme = Theme.Current;
		var editor = EnsureDialogEditor( key, field.Value );
		var name = ReadFieldLabel( field );
		if ( !field.IsMultiline )
			return BuildCompactEntry( dialog, state, "input:" + key, editor, ReadFormPlaceholder( field ), name, field.Type == "password", isInvalid,
				() => state.Touched.Add( field.Name ), _ => submit() );

		var lines = Math.Max( 2, ComputeAnswerLines( editor.Document ) );
		return new TextEditor
		{
			Key = "answer:" + key,
			Controller = EnsureOutsideEditor( surface, key, editor ),
			AccessibilityName = name,
			AccessibilityRole = AccessibilityRole.TextEditor,
			Placeholder = field.Placeholder,
			PlaceholderColor = theme.Muted,
			CaretColor = theme.Accent,
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			LineHeight = CompactLineHeight,
			Color = theme.Text,
			Height = lines * ComputeCompactLineHeight() + 10,
			MinWidth = 0,
			Padding = new Edges( 5, 8 ),
			BackgroundColor = theme.Canvas,
			BorderColor = isInvalid ? theme.Error : theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			FocusStyle = new Style { BorderColor = isInvalid ? theme.Error : theme.Accent },
			OnChange = _ => context.Run( () =>
			{
				state.Touched.Add( field.Name );
				UpdateCompactShape( dialog, state );
			} ),
			KeyBindings = [new( Key.Enter, KeyModifiers.Primary, submit )],
		};
	}

	/// <summary>Builds a one-line box over a dialog editor's document, in the card's field style.</summary>
	/// <param name="dialog">The question.</param>
	/// <param name="state">The card's state, whose look the typed text may change.</param>
	/// <param name="key">The box's element key.</param>
	/// <param name="editor">The editor whose document keeps the typed text.</param>
	/// <param name="placeholder">Text shown while the box is empty.</param>
	/// <param name="name">The box's name for screen readers.</param>
	/// <param name="isSecret">Whether the typed text is masked.</param>
	/// <param name="isInvalid">Whether the box shows its field's error border.</param>
	/// <param name="edited">Notes that the field was edited, before the card's look is checked; null for none.</param>
	/// <param name="submit">Runs on Enter with the typed text; null for nothing.</param>
	private TextEntry BuildCompactEntry( JsonElement dialog, CompactDialog state, string key, TextEditorController editor, string placeholder, string name, bool isSecret,
		bool isInvalid, Action? edited, Action<string>? submit )
	{
		var theme = Theme.Current;
		return new TextEntry
		{
			Key = key,
			Value = editor.Document.GetText(),
			Controlled = true,
			Password = isSecret,
			Placeholder = placeholder,
			PlaceholderColor = theme.Muted,
			AccessibilityName = name,
			Height = theme.ControlHeight,
			MinWidth = 0,
			Padding = new Edges( 0, 8 ),
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			BackgroundColor = theme.Canvas,
			BorderColor = isInvalid ? theme.Error : theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			FocusStyle = new Style { BorderColor = isInvalid ? theme.Error : theme.Accent },
			OnChange = value => context.Run( () =>
			{
				editor.Document.Apply( new TextChange( new TextRange( 0, editor.Document.Length ), value ) );
				edited?.Invoke();
				UpdateCompactShape( dialog, state );
			} ),
			OnSubmit = submit,
		};
	}

	/// <summary>Builds an answer box over a dialog editor: one line that grows to four as the text wraps or breaks, then scrolls; Ctrl+Enter sends.</summary>
	/// <param name="dialog">The question.</param>
	/// <param name="state">The card's state, whose look the typed text may change.</param>
	/// <param name="key">The editor's key, which names the box.</param>
	/// <param name="editor">The view's own editor over the answer's document.</param>
	/// <param name="placeholder">Text shown while the box is empty.</param>
	/// <param name="minLines">The fewest lines the box shows.</param>
	/// <param name="submit">Sends or moves on with the answer, on Ctrl+Enter and the send button; null for nothing.</param>
	/// <param name="showsSend">Whether the box has its own send button.</param>
	private Row BuildCompactAnswer( JsonElement dialog, CompactDialog state, string key, TextEditorController editor, string placeholder, int minLines, Action? submit, bool showsSend )
	{
		var theme = Theme.Current;
		var lines = Math.Max( minLines, ComputeAnswerLines( editor.Document ) );
		var box = new TextEditor
		{
			Key = "answer:" + key,
			Controller = editor,
			AccessibilityName = "Your own answer",
			AccessibilityRole = AccessibilityRole.TextEditor,
			Placeholder = placeholder,
			PlaceholderColor = theme.Muted,
			CaretColor = theme.Accent,
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			LineHeight = CompactLineHeight,
			Color = theme.Text,
			Height = lines * ComputeCompactLineHeight(),
			MinWidth = 0,
			FlexGrow = 1,
			FlexShrink = 1,
			BackgroundColor = Color.Transparent,
			OnChange = _ => context.Run( () => UpdateCompactShape( dialog, state ) ),
			KeyBindings = submit is null ? [] : [new( Key.Enter, KeyModifiers.Primary, submit )],
		};
		return BuildCompactAnswerBox( "answer-box:" + key, box, showsSend ? submit : null );
	}

	/// <summary>Frames an answer field in the card's box, with a send button at its right when it has one.</summary>
	private static Row BuildCompactAnswerBox( string key, Blob field, Action? send )
	{
		var theme = Theme.Current;
		var children = new List<Blob> { field };
		if ( send is not null )
		{
			children.Add( new Button
			{
				Key = "send",
				AccessibilityName = "Send answer",
				Width = CompactSendSize,
				Height = CompactSendSize,
				FlexShrink = 0,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				BorderRadius = 4,
				Focusable = true,
				Cursor = Cursor.Pointer,
				OnClick = send,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, send ), new( Key.Space, KeyModifiers.None, send )],
				HoverStyle = new Style { BackgroundColor = theme.Hover },
				FocusStyle = new Style { BackgroundColor = theme.Hover },
				Children = [BuildCompactGlyph( CompactSendGlyph, theme.Accent, 11 )],
			} );
		}
		return new Row
		{
			Key = key,
			AlignItems = AlignItems.FlexEnd,
			Gap = 6,
			MinWidth = 0,
			MinHeight = 30,
			Margin = new Edges( 6, CompactInset, 0, CompactIndent ),
			Padding = new Edges( 4, 4, 4, 10 ),
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			Children = children,
		};
	}

	/// <summary>Adds an option list: the options as numbered rows, after a filter when there are more than nine, which then shows the first few
	/// matches and "+ N more". Returns what the number keys 1 to 9 do, in order.</summary>
	/// <param name="body">The card's content.</param>
	/// <param name="state">The card's state, which keeps the filter.</param>
	/// <param name="choices">The options.</param>
	/// <param name="isToggle">Whether a click picks or unpicks the option rather than answering, so its key is a check box.</param>
	/// <param name="showsTag">Whether the recommended option says so.</param>
	private List<Action> AddCompactOptions( List<Blob> body, CompactDialog state, IReadOnlyList<DialogChoice> choices, bool isToggle, bool showsTag )
	{
		var theme = Theme.Current;
		var shown = choices;
		var hidden = 0;
		if ( choices.Count > CompactNumberedOptions )
		{
			body.Add( BuildCompactFilter( state, choices.Count ) );
			var filter = state.Filter.Trim();
			IReadOnlyList<DialogChoice> matches = filter.Length == 0 ? choices
				: [.. choices.Where( choice => choice.Label.Contains( filter, StringComparison.OrdinalIgnoreCase ) || choice.Description.Contains( filter, StringComparison.OrdinalIgnoreCase ) )];
			shown = state.ShowsOptions ? matches : [.. matches.Take( CompactFilteredOptions )];
			hidden = matches.Count - shown.Count;
			if ( matches.Count == 0 )
				body.Add( new Container { Key = "no-match", Padding = new Edges( 4, CompactInset, 4, CompactIndent ), Children = [BuildCompactPlain( "No option matches", theme.Muted, ViewStyle.SmallSize, 1 )] } );
		}

		var rows = new List<Blob>();
		for ( var at = 0; at < shown.Count; at++ )
			rows.Add( BuildCompactRow( shown[at], at < CompactNumberedOptions ? at + 1 : 0, isToggle, showsTag, CompactIndent, CompactInset ) );
		body.Add( new Container { Key = "options", MinWidth = 0, Children = rows } );
		if ( hidden > 0 )
			body.Add( BuildCompactLink( "more", $"+ {hidden.ToString( CultureInfo.InvariantCulture )} more", () => UpdateCompact( () => state.ShowsOptions = true ) ) );
		return [.. shown.Take( CompactNumberedOptions ).Select( choice => choice.Answer )];
	}

	/// <summary>Builds the box that filters a long option list.</summary>
	private Row BuildCompactFilter( CompactDialog state, int count )
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "filter",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Height = 30,
			MinWidth = 0,
			Margin = new Edges( 2, CompactInset, 4, CompactIndent ),
			Padding = new Edges( 0, 10 ),
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			Children =
			[
				BuildCompactGlyph( CompactSearchGlyph, theme.Muted, 11 ),
				new TextEntry
				{
					Key = "filter-entry",
					Value = state.Filter,
					Controlled = true,
					Placeholder = $"Filter {count.ToString( CultureInfo.InvariantCulture )} options…",
					PlaceholderColor = theme.Muted,
					AccessibilityName = "Filter options",
					Height = 24,
					MinWidth = 0,
					FlexGrow = 1,
					FlexShrink = 1,
					FontFamily = ViewStyle.FontFamily,
					FontSize = theme.FontSize,
					Color = theme.Text,
					BackgroundColor = Color.Transparent,
					OnChange = value => UpdateCompact( () =>
					{
						state.Filter = value;
						state.ShowsOptions = false;
					} ),
				},
			],
		};
	}

	/// <summary>Builds one option as a list row: its number key or check box in the gutter, its label in up to two lines, its description in one,
	/// and "Recommended" at the right for the recommended option.</summary>
	/// <param name="choice">The option.</param>
	/// <param name="number">The number key that picks it, 1 to 9; 0 for none.</param>
	/// <param name="isToggle">Whether a click picks or unpicks it, so its key is a check box.</param>
	/// <param name="showsTag">Whether the recommended option says so.</param>
	/// <param name="gutter">The row's left padding, where the key sits.</param>
	/// <param name="inset">The row's right padding.</param>
	private static Button BuildCompactRow( DialogChoice choice, int number, bool isToggle, bool showsTag, int gutter, int inset )
	{
		var theme = Theme.Current;
		var text = new List<Blob>
		{
			new Text
			{
				Content = SoftenBreaks( choice.Label ),
				Color = theme.Text,
				FontFamily = ViewStyle.FontFamily,
				FontSize = theme.FontSize,
				LineHeight = CompactLineHeight,
				MinWidth = 0,
				TextMaxLines = 2,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( choice.Description.Length > 0 )
			text.Add( new Text
			{
				Content = choice.Description,
				Color = theme.Muted,
				FontFamily = ViewStyle.FontFamily,
				FontSize = ViewStyle.SmallSize,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextMaxLines = 1,
				TextTrimming = TextTrimming.Ellipsis,
			} );

		var face = choice.IsSelected ? CompactCheckGlyph : isToggle || number == 0 ? "" : number.ToString( CultureInfo.InvariantCulture );
		var key = BuildCompactBox( face, CompactKeySize, choice.IsSelected, choice.IsPrimary );
		key.Position = PositionType.Absolute;
		key.Left = gutter - CompactKeyOffset;
		key.Top = 8;
		// The label takes the room the tag leaves, so a narrow card cuts the label before the tag.
		var children = new List<Blob> { key, new Container { FlexGrow = 1, FlexShrink = 1, FlexBasis = 0, MinWidth = 0, Gap = 1, Children = text } };
		if ( showsTag && choice.IsPrimary )
			children.Add( new Text
			{
				Content = "Recommended",
				Color = theme.Accent,
				FontFamily = ViewStyle.FontFamily,
				FontSize = CompactTinySize,
				MinWidth = 0,
				FlexShrink = 1,
				Padding = new Edges( 2, 0, 0, 0 ),
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );

		var name = choice.Description.Length > 0 ? $"{choice.Label}: {choice.Description}" : choice.Label;
		return new Button
		{
			Key = choice.Key,
			AccessibilityName = choice.IsSelected ? name + ", picked" : choice.IsPrimary && showsTag ? name + ", recommended" : name,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.FlexStart,
			Gap = 10,
			MinWidth = 0,
			Padding = new Edges( 7, inset, 7, gutter ),
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = choice.Answer,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, choice.Answer ), new( Key.Space, KeyModifiers.None, choice.Answer )],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = children,
		};
	}

	/// <summary>Builds a small rounded box: an option's number key, or a check box filled in the accent color when picked.</summary>
	/// <param name="face">The digit, the check glyph, or empty.</param>
	/// <param name="size">The box's size in logical pixels.</param>
	/// <param name="isFilled">Whether the box is filled in the accent color, for a picked option.</param>
	/// <param name="isAccent">Whether the box is outlined in the accent color, for the recommended option.</param>
	private static Container BuildCompactBox( string face, int size, bool isFilled, bool isAccent )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( face == CompactCheckGlyph )
			children.Add( BuildCompactGlyph( face, theme.Canvas, size - 7 ) );
		else if ( face.Length > 0 )
			children.Add( new Text { Content = face, Color = isAccent ? theme.Accent : theme.Muted, FontFamily = ViewStyle.FontFamily, FontSize = 10, TextWrap = TextWrap.NoWrap } );
		return new Container
		{
			Width = size,
			Height = size,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = 3,
			BorderWidth = 1,
			BorderColor = isFilled || isAccent ? theme.Accent : theme.Border,
			BackgroundColor = isFilled ? theme.Accent : Color.Transparent,
			Children = children,
		};
	}

	/// <summary>Builds one review row: the question's number and text in muted type, then its answer at the right; a click goes back to it.</summary>
	private static Button BuildCompactReviewRow( int number, string question, string answer, Action open )
	{
		var theme = Theme.Current;
		var label = ReadPlainText( question );
		return new Button
		{
			Key = "review:" + number.ToString( CultureInfo.InvariantCulture ),
			AccessibilityName = $"{label}: {(answer.Length > 0 ? answer : "no answer")}",
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = 6,
			MinWidth = 0,
			Padding = new Edges( 4, CompactInset, 4, CompactIndent ),
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = open,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, open ), new( Key.Space, KeyModifiers.None, open )],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				new Text
				{
					Content = $"{number.ToString( CultureInfo.InvariantCulture )} {label}",
					Color = theme.Muted,
					FontFamily = ViewStyle.FontFamily,
					FontSize = ViewStyle.SmallSize,
					FlexGrow = 1,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				new Text
				{
					Content = answer.Length > 0 ? answer : "No answer",
					Color = answer.Length > 0 ? theme.Text : theme.Muted,
					FontFamily = ViewStyle.FontFamily,
					FontSize = ViewStyle.SmallSize,
					FlexShrink = 1,
					MinWidth = 0,
					MaxWidth = Length.Percent( 60 ),
					TextAlign = TextAlign.Right,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
			],
		};
	}

	/// <summary>Builds the step bar: one thin segment per step, done ones in the accent color and the current one in the text color.</summary>
	private static Row BuildCompactStepBar( int count, int current )
	{
		var theme = Theme.Current;
		var segments = new List<Blob>();
		for ( var at = 0; at < count; at++ )
			segments.Add( new Container
			{
				FlexGrow = 1,
				FlexBasis = 0,
				Height = 3,
				BorderRadius = 2,
				BackgroundColor = at < current ? theme.Accent : at == current ? theme.Text : theme.Border,
			} );
		return new Row
		{
			Key = "steps",
			AccessibilityName = $"Step {Math.Min( current + 1, count ).ToString( CultureInfo.InvariantCulture )} of {count.ToString( CultureInfo.InvariantCulture )}",
			Gap = 4,
			MinWidth = 0,
			Padding = new Edges( 2, CompactInset, 8, CompactIndent ),
			Children = segments,
		};
	}

	/// <summary>Adds the question's text: three lines with Show all when it is long, or all of it as Markdown once shown.</summary>
	private void AddCompactLead( List<Blob> body, string markdown, CompactDialog state )
	{
		var isLong = IsLongText( markdown );
		var isShown = isLong && state.ShowsLead;
		var children = new List<Blob>
		{
			isShown ? MarkdownView.Build( markdown, OpenLink ) : BuildCompactInline( markdown, Theme.Current.Text, Theme.Current.FontSize, CompactClampLines ),
		};
		body.Add( new Container { Key = "lead", MinWidth = 0, Padding = new Edges( 0, CompactInset, isLong ? 2 : 10, CompactIndent ), Children = children } );
		if ( isLong )
			body.Add( BuildCompactLink( "lead-toggle", isShown ? "Show less" : "Show all", () => UpdateCompact( () => state.ShowsLead = !state.ShowsLead ) ) );
	}

	/// <summary>Adds the question's message, if any, in a bordered box: three lines with Show all when it is long, or all of it once shown.</summary>
	private void AddCompactMessage( List<Blob> body, JsonElement dialog, CompactDialog state )
	{
		var message = ReadText( dialog, "message" );
		if ( message.Length == 0 )
			return;

		var theme = Theme.Current;
		var isLong = IsLongText( message );
		var isShown = isLong && state.ShowsMessage;
		body.Add( new Container
		{
			Key = "message",
			MinWidth = 0,
			Margin = new Edges( 0, CompactInset, isLong ? 2 : 8, CompactIndent ),
			Padding = new Edges( 8, 10 ),
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			Color = theme.Muted,
			Children = [isShown ? MarkdownView.Build( message, OpenLink ) : BuildCompactInline( message, theme.Muted, ViewStyle.SmallSize, CompactClampLines )],
		} );
		if ( isLong )
			body.Add( BuildCompactLink( "message-toggle", isShown ? "Show less" : "Show all", () => UpdateCompact( () => state.ShowsMessage = !state.ShowsMessage ) ) );
	}

	/// <summary>Builds a muted small line at the card's indent, such as "Question 2 of 3 · Title", cut with an ellipsis.</summary>
	private static Container BuildCompactCaption( string text ) => new()
	{
		MinWidth = 0,
		Padding = new Edges( 0, CompactInset, 4, CompactIndent ),
		Children = [BuildCompactPlain( text, Theme.Current.Muted, ViewStyle.SmallSize, 1 )],
	};

	/// <summary>Builds plain text that wraps at long unbroken runs and shows at most a number of lines.</summary>
	/// <param name="text">The text.</param>
	/// <param name="color">Its color.</param>
	/// <param name="size">Its size in logical pixels.</param>
	/// <param name="lines">The most lines it shows; 0 for all.</param>
	/// <param name="isBold">Whether it is set in semibold.</param>
	private static Text BuildCompactPlain( string text, Color color, int size, int lines, bool isBold = false ) => new()
	{
		Content = SoftenBreaks( text ),
		Color = color,
		FontFamily = ViewStyle.FontFamily,
		FontSize = size,
		FontWeight = isBold ? AuthorWeight : 400,
		LineHeight = CompactLineHeight,
		MinWidth = 0,
		TextWrap = lines == 1 ? TextWrap.NoWrap : TextWrap.Wrap,
		TextMaxLines = lines,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Builds Markdown as one run of styled inline text, its blocks joined into one paragraph, showing at most a number of lines.</summary>
	private static Text BuildCompactInline( string markdown, Color color, int size, int lines )
	{
		var spans = MarkdownView.ParseInline( FlattenMarkdown( markdown ) ).Select( span => new TextSpan( SoftenBreaks( span.Text ), span.Style ) ).ToArray();
		return new Text( spans )
		{
			Color = color,
			FontFamily = ViewStyle.FontFamily,
			FontSize = size,
			LineHeight = CompactLineHeight,
			MinWidth = 0,
			TextMaxLines = lines,
			TextTrimming = TextTrimming.Ellipsis,
		};
	}

	/// <summary>Builds a small accent text button at the card's indent, such as Show all or "+ N more".</summary>
	private static Button BuildCompactLink( string key, string label, Action action )
	{
		var theme = Theme.Current;
		return new Button
		{
			Key = key,
			AccessibilityName = label,
			AlignSelf = AlignSelf.FlexStart,
			Margin = new Edges( 0, CompactInset, 8, CompactIndent ),
			Padding = new Edges( 1, 2 ),
			BorderRadius = 3,
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = action,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, action ), new( Key.Space, KeyModifiers.None, action )],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = [new Text { Content = label, Color = theme.Accent.WithAlpha( 0.9 ), FontFamily = ViewStyle.FontFamily, FontSize = ViewStyle.SmallSize, TextWrap = TextWrap.NoWrap }],
		};
	}

	/// <summary>Builds the footer: a divider above, Back when there is a step before, the hint cut with an ellipsis, then the buttons.</summary>
	private static Row BuildCompactFooter( Blob? back, string hint, List<Blob> buttons )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( back is not null )
			children.Add( back );
		children.Add( new Text
		{
			Content = hint,
			Color = theme.Muted,
			FontFamily = ViewStyle.FontFamily,
			FontSize = ViewStyle.SmallSize,
			FlexGrow = 1,
			FlexShrink = 1,
			MinWidth = 0,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		} );
		children.AddRange( buttons );
		return new Row
		{
			Key = "footer",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinWidth = 0,
			Margin = new Edges( 10, 0, 0, 0 ),
			Padding = new Edges( 10, CompactInset ),
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			BorderColor = theme.Divider,
			Children = children,
		};
	}

	/// <summary>Builds a footer button: a filled accent one for the card's main action, or a quiet one; a main action that is not ready yet is
	/// dimmed, and a click then shows why instead of sending.</summary>
	private static Button BuildCompactButton( string key, string label, Action action, bool isPrimary, bool isReady = true )
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( label, action );
		var color = isPrimary ? theme.Canvas : theme.Text;
		button.Key = key;
		button.Height = CompactButtonHeight;
		button.Padding = new Edges( 0, 12 );
		button.FlexShrink = 0;
		button.Color = color;
		button.BackgroundColor = isPrimary ? theme.Accent : theme.Text.WithAlpha( 0.07 );
		button.HoverStyle = new Style { BackgroundColor = isPrimary ? theme.Accent.WithAlpha( 0.86 ) : theme.Hover };
		button.FocusStyle = new Style { BackgroundColor = isPrimary ? theme.Accent.WithAlpha( 0.86 ) : theme.Hover };
		if ( !isReady )
			button.Opacity = CompactUnreadyOpacity;
		button.Children = [new Text { Content = label, Color = color, FontFamily = ViewStyle.FontFamily, FontSize = theme.FontSize, TextWrap = TextWrap.NoWrap }];
		return button;
	}

	/// <summary>Builds Skip, which cancels the question as the chat card's Cancel does.</summary>
	private Button BuildCompactSkip( string id ) =>
		BuildCompactButton( "dialog-close", "Skip", () => UpdateDialogAnswer( id, new { type = "extension_ui_response", id, cancelled = true } ), isPrimary: false );

	/// <summary>Builds the footer's Back button, which goes to the step before.</summary>
	private static Button BuildCompactBack( Action back )
	{
		var theme = Theme.Current;
		return new Button
		{
			Key = "back",
			AccessibilityName = "Back",
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			FlexShrink = 0,
			Gap = 6,
			Height = CompactButtonHeight,
			Padding = new Edges( 0, 6 ),
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = back,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, back ), new( Key.Space, KeyModifiers.None, back )],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				BuildCompactGlyph( CompactBackGlyph, theme.Muted, 10 ),
				new Text { Content = "Back", Color = theme.Muted, FontFamily = ViewStyle.FontFamily, FontSize = ViewStyle.SmallSize, TextWrap = TextWrap.NoWrap },
			],
		};
	}

	/// <summary>Builds a Segoe Fluent Icons glyph at a size.</summary>
	private static Text BuildCompactGlyph( string glyph, Color color, int size ) => new()
	{
		Content = glyph,
		Color = color,
		FontFamily = ViewStyle.IconFontFamily,
		FontSize = size,
		FlexShrink = 0,
		TextWrap = TextWrap.NoWrap,
	};

	/// <summary>Wraps the card's content: a group screen readers name "Tool question", where the number keys pick the numbered options and
	/// Ctrl+Enter does the main action.</summary>
	/// <param name="id">The question's id.</param>
	/// <param name="body">The content, footer last.</param>
	/// <param name="numbered">What the number keys 1 to 9 do, in order.</param>
	/// <param name="primary">What Ctrl+Enter does; null for nothing.</param>
	private Container BuildCompactCard( string id, List<Blob> body, IReadOnlyList<Action> numbered, Action? primary ) => new()
	{
		Key = "compact:" + id,
		AccessibilityRole = AccessibilityRole.Group,
		AccessibilityName = "Tool question",
		MinWidth = 0,
		Focusable = true,
		KeyBindings = primary is null ? [] : [new( Key.Enter, KeyModifiers.Primary, primary )],
		OnKeyDown = key => PickNumbered( key, numbered ),
		Children = body,
	};

	/// <summary>Runs the numbered action a number key picks, unless the key types into a text box or comes with a modifier.</summary>
	private void PickNumbered( KeyEvent key, IReadOnlyList<Action> numbered )
	{
		if ( key.TargetEditsText || key.IsComposing || key.Modifiers.Ctrl || key.Modifiers.Alt || key.Modifiers.Shift || key.Modifiers.Super )
			return;

		var number = (key.Key is >= Key.Number1 and <= Key.Number9) ? key.Key - Key.Number1
			: (key.Key is >= Key.Keypad1 and <= Key.Keypad9) ? key.Key - Key.Keypad1
			: -1;
		if ( number < 0 || number >= numbered.Count )
			return;

		key.StopPropagation();
		context.Run( numbered[number] );
	}

	/// <summary>Changes what a compact card shows, then rebuilds the views outside the chat.</summary>
	private void UpdateCompact( Action change ) => context.Run( () =>
	{
		change();
		UpdateDialogsChanged();
	} );

	/// <summary>Rebuilds the views outside the chat when typing changed what a card's look depends on, such as an answer box's height, whether
	/// there is a typed answer, or a field's error.</summary>
	private void UpdateCompactShape( JsonElement dialog, CompactDialog state )
	{
		if ( ComputeCompactShape( dialog, state ) != state.Shape )
			UpdateDialogsChanged();
	}

	/// <summary>Gets what a card's look depends on that typing changes: each of the question's answer boxes' height and whether it holds text, and,
	/// for a form, each field's error and whether it shows.</summary>
	private string ComputeCompactShape( JsonElement dialog, CompactDialog state )
	{
		var id = ReadText( dialog, "id" );
		var shape = new StringBuilder();
		if ( ReadText( dialog, "method" ) == "form" && ReadForm( dialog ) is var form && !IsRichForm( form ) )
		{
			foreach ( var (name, error) in ComputeFormErrors( id, form ) )
				shape.Append( state.Touched.Contains( name ) ? '!' : '?' ).Append( name ).Append( '=' ).Append( error ).Append( ';' );
		}
		foreach ( var (key, editor) in _dialogEditors )
		{
			if ( key == id || key.StartsWith( id + ".", StringComparison.Ordinal ) )
				shape.Append( key ).Append( ':' ).Append( ComputeAnswerLines( editor.Document ) ).Append( editor.Document.GetText().Trim().Length > 0 ? '+' : '-' ).Append( ';' );
		}
		return shape.ToString();
	}

	/// <summary>Forgets what every view's compact card showed of a question that left.</summary>
	private void ReleaseCompactDialogs( string id )
	{
		foreach ( var key in _compactDialogs.Keys.Where( key => key.Id == id ).ToArray() )
			_compactDialogs.Remove( key );
	}

	/// <summary>Gets the heading of a select, confirm, input, editor or authorization request: its title, or a generic one.</summary>
	private static string ReadTitle( JsonElement dialog ) => ReadText( dialog, "title" ) is { Length: > 0 } title ? title : "Your input is needed";

	/// <summary>Gets whether a questionnaire question takes several options.</summary>
	private static bool IsMultiQuestion( JsonElement question ) => ReadText( question, "multi" ) is "True" or "true";

	/// <summary>Lists a questionnaire question's options as answers, picked ones selected and the recommended one primary.</summary>
	/// <param name="question">The question.</param>
	/// <param name="picks">The labels picked so far.</param>
	/// <param name="isMulti">Whether the question takes several options, so each option is a check box.</param>
	/// <param name="answer">Gets what a click on an option does, by its label.</param>
	private static List<DialogChoice> ListQuestionChoices( JsonElement question, IReadOnlySet<string> picks, bool isMulti, Func<string, Action> answer )
	{
		var recommended = question.TryGetProperty( "recommended", out var pick ) && pick.ValueKind == JsonValueKind.Number && pick.TryGetInt32( out var at ) ? at : -1;
		var choices = new List<DialogChoice>();
		if ( !question.TryGetProperty( "options", out var options ) || options.ValueKind != JsonValueKind.Array )
			return choices;

		var position = 0;
		foreach ( var option in options.EnumerateArray() )
		{
			var label = ReadText( option, "label" );
			var isPicked = picks.Contains( label );
			choices.Add( new( "option:" + label, label, ReadText( option, "description" ), isMulti ? (isPicked ? "☑" : "☐") : "", isPicked, position++ == recommended, answer( label ) ) );
		}
		return choices;
	}

	/// <summary>Gets a questionnaire question's answer as the review shows it: the written answer for a single choice that has one, otherwise the
	/// picked labels and any written answer; empty for none.</summary>
	private string FormatQuestionAnswer( string key, bool isMulti )
	{
		var custom = EnsureDialogEditor( key, "" ).Document.GetText().Trim();
		if ( !isMulti && custom.Length > 0 )
			return custom.ReplaceLineEndings( " " );

		var parts = session.ReadSelection( key ).ToList();
		if ( custom.Length > 0 )
			parts.Add( custom.ReplaceLineEndings( " " ) );
		return string.Join( ", ", parts );
	}

	/// <summary>Gets whether a form is too rich for the card: it has pictures, or prose longer than a few lines, a table or code.</summary>
	private static bool IsRichForm( HtmlForm form )
	{
		var (_, _, skipsBlock) = ReadFormLead( form );
		for ( var at = 0; at < form.Sections.Count; at++ )
		{
			var blocks = form.Sections[at].Blocks;
			for ( var index = 0; index < blocks.Count; index++ )
			{
				if ( at == 0 && index == 0 && skipsBlock )
					continue;

				if ( blocks[index] is FormImage or FormSvg || blocks[index] is FormMarkdown { IsHeading: false } text && IsRichText( text.Markdown ) )
					return true;
			}
		}
		return false;
	}

	/// <summary>Gets whether a form's prose block is too big for the card: long, more than three lines, a table, or code.</summary>
	private static bool IsRichText( string markdown )
	{
		var lines = markdown.Split( '\n' ).Select( line => line.Trim() ).Where( line => line.Length > 0 ).ToList();
		return markdown.Length > CompactRichText || lines.Count > CompactClampLines || markdown.Contains( "```", StringComparison.Ordinal )
			|| lines.Count( line => line.StartsWith( '|' ) ) >= 2;
	}

	/// <summary>Splits a form into the card's pages: all of it on one page when it has at most six fields and one fieldset; otherwise one page per
	/// section, a section with more than six fields split, and a section without fields joined to the next page.</summary>
	private static List<CompactFormPage> ListFormPages( HtmlForm form )
	{
		var (_, skipsTitle, skipsBlock) = ReadFormLead( form );
		var isOnePage = form.Fields.Count <= CompactPageFields && form.Sections.Count( section => section.IsFieldset ) <= 1;
		var pages = new List<CompactFormPage>();
		var pending = new List<FormBlock>();
		for ( var at = 0; at < form.Sections.Count; at++ )
		{
			var section = form.Sections[at];
			var title = section.Title.Length > 0 && !(at == 0 && skipsTitle) ? section.Title : "";
			var blocks = section.Blocks.Where( ( _, index ) => !(at == 0 && index == 0 && skipsBlock) ).ToList();
			if ( isOnePage || !blocks.Any( block => block is FormFieldBlock ) )
			{
				// On one page, and before a page's fields, a section's title is a heading.
				if ( title.Length > 0 )
					pending.Add( new FormMarkdown( title, true ) );
				pending.AddRange( blocks );
				continue;
			}

			var page = new List<FormBlock>( pending );
			pending.Clear();
			var fields = 0;
			foreach ( var block in blocks )
			{
				if ( block is FormFieldBlock && fields == CompactPageFields )
				{
					// The prose after the page's last field introduces the next field, so it moves to the next page with it.
					var last = page.FindLastIndex( candidate => candidate is FormFieldBlock );
					var carried = page.GetRange( last + 1, page.Count - last - 1 );
					page.RemoveRange( last + 1, page.Count - last - 1 );
					pages.Add( new( title, page ) );
					page = carried;
					fields = 0;
				}
				fields += block is FormFieldBlock ? 1 : 0;
				page.Add( block );
			}
			pages.Add( new( title, page ) );
		}
		if ( pages.Count == 0 )
			pages.Add( new( "", pending ) );
		else
			pages[^1].Blocks.AddRange( pending );
		return pages;
	}

	/// <summary>Gets the label a form field shows: its label as plain text, or its name in words for a group without one.</summary>
	private static string ReadFieldLabel( FormField field )
	{
		if ( field.Label.Length > 0 )
			return ReadPlainText( field.Label );

		var name = field.Name.Replace( '_', ' ' ).Replace( '-', ' ' ).Trim();
		return name.Length > 0 ? char.ToUpper( name[0], CultureInfo.InvariantCulture ) + name[1..] : "Answer";
	}

	/// <summary>Gets Markdown's text without its formatting, on one line.</summary>
	private static string ReadPlainText( string markdown ) => string.Concat( MarkdownView.ParseInline( FlattenMarkdown( markdown ) ).Select( span => span.Text ) );

	/// <summary>Joins Markdown's lines into one paragraph without heading marks, for text the card shows in a few lines.</summary>
	private static string FlattenMarkdown( string markdown )
	{
		var lines = markdown.Split( '\n' ).Select( line => line.Trim() ).Where( line => line.Length > 0 ).Select( line => line.TrimStart( '#' ).TrimStart() );
		return string.Join( " ", lines );
	}

	/// <summary>Gets whether text likely takes more than three lines of the card, or has structure its three-line preview loses, so it offers Show
	/// all. An estimate: the card cannot measure its text before it is laid out.</summary>
	private static bool IsLongText( string markdown )
	{
		var lines = markdown.Split( '\n' ).Count( line => line.Trim().Length > 0 );
		return FlattenMarkdown( markdown ).Length > CompactClampLines * CompactLineCharacters || lines > CompactClampLines
			|| markdown.Contains( "```", StringComparison.Ordinal );
	}

	/// <summary>Gets how many lines an answer box shows for its text: its lines, wrapped as estimated, from one to four.</summary>
	private static int ComputeAnswerLines( TextDocument document )
	{
		var lines = 0;
		foreach ( var line in document.GetText().Split( '\n' ) )
			lines += Math.Max( 1, (line.Length + CompactLineCharacters - 1) / CompactLineCharacters );
		return Math.Clamp( lines, 1, CompactAnswerLines );
	}

	/// <summary>Joins footer hints with a middle dot, skipping empty ones.</summary>
	private static string JoinHints( params string[] hints ) => string.Join( " · ", hints.Where( hint => hint.Length > 0 ) );

	/// <summary>Gets the hint that names the number keys, such as "Press 1–3"; empty when no option has one.</summary>
	private static string FormatPressHint( int count ) => count switch
	{
		0 => "",
		1 => "Press 1",
		_ => $"Press 1–{count.ToString( CultureInfo.InvariantCulture )}",
	};

	/// <summary>Adds invisible break opportunities to long runs without one, such as paths and links, so they wrap instead of widening the card.</summary>
	private static string SoftenBreaks( string text )
	{
		var builder = new StringBuilder( text.Length );
		var run = 0;
		foreach ( var character in text )
		{
			builder.Append( character );
			if ( char.IsWhiteSpace( character ) )
			{
				run = 0;
				continue;
			}

			run++;
			if ( char.IsHighSurrogate( character ) )
				continue;

			// Breaks go after separators once a run is long, and anywhere once it is longer still.
			if ( (run >= CompactUnbrokenRun / 2 && character is '/' or '\\' or '_' or '.' or '-' or ':' or '=' or '&' or '?') || run >= CompactUnbrokenRun )
			{
				builder.Append( '\u200B' );
				run = 0;
			}
		}
		return builder.ToString();
	}
}
