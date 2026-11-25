using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Shows the HTML forms Omp's ask tool sends with native controls, and answers them with the submitted form data.</summary>
/// <remarks>Choice fields keep their picks in the session, by option index, so the feed and remote clients share them; typed text stays in the
/// dialog editors, keyed by dialog and field name.</remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines the widest an inline SVG picture is drawn, in logical pixels.</summary>
	private const int MaxSvgWidth = 560;

	/// <summary>Defines the narrowest an inline SVG picture is drawn, in logical pixels, so a small view box stays readable.</summary>
	private const int MinSvgWidth = 160;

	/// <summary>Holds each pending form, parsed once, by dialog id.</summary>
	private readonly Dictionary<string, HtmlForm> _forms = [];

	/// <summary>Holds the message of each field the last submit refused, by field key.</summary>
	private readonly Dictionary<string, string> _formErrors = [];

	/// <summary>Holds each form picture, decoded once: an <see cref="ImageSource"/>, a <see cref="VectorAsset"/>, or null when it cannot be shown; by
	/// dialog id and picture index.</summary>
	private readonly Dictionary<string, object?> _formPictures = [];

	/// <summary>Gets a form dialog's parsed form, parsing it on first use.</summary>
	private HtmlForm ReadForm( JsonElement dialog )
	{
		var id = ReadText( dialog, "id" );
		if ( !_forms.TryGetValue( id, out var form ) )
			_forms.Add( id, form = HtmlForm.Parse( ReadText( dialog, "html" ) ) );
		return form;
	}

	/// <summary>Gets a form's only field when it is a single choice, which a click answers; null when the form has more to fill.</summary>
	private static FormChoiceField? ReadQuickField( HtmlForm form ) => form.Fields is [FormChoiceField { IsMulti: false } field] ? field : null;

	/// <summary>Gets the text that heads a form's card, and whether it is the first section's title or the first paragraph, which the body then skips.</summary>
	private static (string Lead, bool IsTitle, bool IsBlock) ReadFormLead( HtmlForm form )
	{
		if ( form.Sections.Count > 0 && form.Sections[0] is var first )
		{
			if ( first.Title.Length > 0 )
				return (first.Title, true, false);
			if ( first.Blocks is [FormMarkdown text, ..] )
				return (text.Markdown, false, true);
		}
		return ("Your input is needed", false, false);
	}

	/// <summary>Builds a form's sections in the chat's card: their titles, prose, pictures and fields, without the text that already heads the card.</summary>
	/// <param name="dialog">The form dialog.</param>
	/// <param name="form">Its parsed form.</param>
	/// <param name="quick">The answer buttons of a form whose one field is a single choice; null for a form that is filled, then submitted.</param>
	private List<Blob> BuildForm( JsonElement dialog, HtmlForm form, IReadOnlyList<DialogChoice>? quick )
	{
		var id = ReadText( dialog, "id" );
		var (_, skipsTitle, skipsBlock) = ReadFormLead( form );
		var children = new List<Blob>();
		var picture = 0;
		for ( var at = 0; at < form.Sections.Count; at++ )
		{
			var section = form.Sections[at];
			if ( section.Title.Length > 0 && !(at == 0 && skipsTitle) )
				children.Add( new Container { Key = "section:" + at, FontWeight = AuthorWeight, Children = [MarkdownView.Build( section.Title, OpenLink )] } );
			for ( var index = 0; index < section.Blocks.Count; index++ )
			{
				if ( at == 0 && index == 0 && skipsBlock )
					continue;

				children.Add( section.Blocks[index] switch
				{
					FormMarkdown { IsHeading: true } text => new Container { FontWeight = AuthorWeight, Children = [MarkdownView.Build( text.Markdown, OpenLink )] },
					FormMarkdown text => MarkdownView.Build( text.Markdown, OpenLink ),
					FormImage image => BuildFormPicture( $"{id}:{picture++}", image.Source, image.Alt, () => LoadFormImage( image.Source ) ),
					FormSvg svg => BuildFormPicture( $"{id}:{picture++}", "", svg.Alt, () => ParseSvg( svg.Markup ) ),
					FormFieldBlock { Field: FormChoiceField choice } => BuildFormChoice( dialog, choice, quick ),
					FormFieldBlock { Field: FormTextField text } => BuildFormText( dialog, text ),
					_ => new Container(),
				} );
			}
		}
		return children;
	}

	/// <summary>Builds a choice field: its label, its options as buttons, a box for an answer of one's own when the field offers one, and the error
	/// the last submit gave it.</summary>
	private Blob BuildFormChoice( JsonElement dialog, FormChoiceField field, IReadOnlyList<DialogChoice>? quick )
	{
		var key = ReadText( dialog, "id" ) + "." + field.Name;
		var children = new List<Blob>();
		if ( field.Label.Length > 0 )
			children.Add( MarkdownView.Build( field.Label, OpenLink ) );
		if ( quick is not null )
		{
			children.Add( BuildChoices( "options", quick ) );
			// The message box below takes the answer of one's own when it can; otherwise the field has its own box.
			if ( field.HasOther && ComposerAnswerKey() == key )
				children.Add( BuildText( "Or type your own answer in the message box below.", muted: true, size: ViewStyle.SmallSize ) );
			else if ( field.HasOther )
				children.Add( BuildAnswerEntry( "other:" + key, EnsureDialogEditor( key + ".other", "" ), "Other", "Other answer", false,
					text => context.Run( () => UpdateFormOther( dialog, field, text ) ) ) );
		}
		else
		{
			var picked = ReadFormPicks( key );
			var options = new List<DialogChoice>();
			for ( var at = 0; at < field.Options.Count; at++ )
			{
				var option = field.Options[at];
				if ( option.IsDisabled )
					continue;

				var index = at.ToString( CultureInfo.InvariantCulture );
				var isSelected = picked.Contains( at );
				options.Add( new( "option:" + index, option.Label, "", field.IsMulti ? (isSelected ? "☑" : "☐") : "", isSelected, false,
					() => context.Run( () => session.UpdateSelection( key, index, field.IsMulti ) ) ) );
			}
			children.Add( BuildChoices( "options", options ) );
			if ( field.HasOther )
				children.Add( BuildAnswerEntry( "other:" + key, EnsureDialogEditor( key + ".other", "" ), "Other", "Other answer", false,
					_ => context.Run( () => UpdateFormSubmit( dialog ) ) ) );
		}
		AddFormError( children, key );
		return new Container { Key = "field:" + key, Gap = Theme.Current.Spacing, Children = children };
	}

	/// <summary>Builds a text field: its label, a one-line box or a multiline editor, and the error the last submit gave it.</summary>
	private Blob BuildFormText( JsonElement dialog, FormTextField field )
	{
		var key = ReadText( dialog, "id" ) + "." + field.Name;
		var editor = EnsureDialogEditor( key, field.Value );
		var name = field.Name.Length > 0 ? field.Name.Replace( '_', ' ' ).Replace( '-', ' ' ) : "Answer";
		var children = new List<Blob>();
		if ( field.Label.Length > 0 )
			children.Add( MarkdownView.Build( field.Label, OpenLink ) );
		if ( field.IsMultiline )
			children.Add( BuildDialogEditor( editor, name, "answer:" + key ) );
		else
			children.Add( BuildAnswerEntry( "input:" + key, editor, ReadFormPlaceholder( field ), name, field.Type == "password", _ => context.Run( () => UpdateFormSubmit( dialog ) ) ) );
		AddFormError( children, key );
		return new Container { Key = "field:" + key, Gap = Theme.Current.Spacing * 0.75, Children = children };
	}

	/// <summary>Gets the text a one-line text field shows while empty: its placeholder, or the format its type expects.</summary>
	private static string ReadFormPlaceholder( FormTextField field ) => field.Placeholder.Length > 0 ? field.Placeholder : field.Type switch
	{
		"date" => "YYYY-MM-DD",
		"email" => "name@example.com",
		"url" => "https://",
		"number" or "range" when field.Min is { } min && field.Max is { } max => $"{min.ToString( CultureInfo.InvariantCulture )}–{max.ToString( CultureInfo.InvariantCulture )}",
		_ => "",
	};

	/// <summary>Adds the message the last submit gave a field, if any.</summary>
	private void AddFormError( List<Blob> children, string key )
	{
		if ( _formErrors.TryGetValue( key, out var error ) )
			children.Add( new Text { Content = error, Color = Theme.Current.Error, FontSize = ViewStyle.SmallSize, MinWidth = 0 } );
	}

	/// <summary>Builds a picture of a form: an image or SVG drawing at most as large as the chat's inline images, a link to a web image, or its
	/// description when it cannot be shown.</summary>
	/// <param name="key">The picture's cache key.</param>
	/// <param name="source">The image's address, for a link when it is on the web; empty for an SVG drawing.</param>
	/// <param name="alt">The picture's description.</param>
	/// <param name="load">Decodes the picture once.</param>
	private Blob BuildFormPicture( string key, string source, string alt, Func<object?> load )
	{
		if ( !_formPictures.TryGetValue( key, out var picture ) )
			_formPictures.Add( key, picture = load() );
		switch ( picture )
		{
			case ImageSource image:
				var height = Math.Min( ImageHeight, image.Height );
				return new Image
				{
					Key = "picture:" + key,
					Source = image,
					Fit = ImageFit.Contain,
					AlignSelf = AlignSelf.FlexStart,
					Width = height * (double)image.Width / image.Height,
					AspectRatio = (double)image.Width / image.Height,
					MaxWidth = Length.Percent( 100 ),
					AccessibilityName = alt,
				};
			case VectorAsset drawing when drawing.ViewBoxWidth > 0 && drawing.ViewBoxHeight > 0:
				return new Container
				{
					Key = "picture:" + key,
					AlignSelf = AlignSelf.FlexStart,
					Width = Math.Clamp( drawing.ViewBoxWidth, MinSvgWidth, MaxSvgWidth ),
					AspectRatio = drawing.ViewBoxWidth / drawing.ViewBoxHeight,
					MaxWidth = Length.Percent( 100 ),
					AccessibilityName = alt,
					Children = [drawing.Render( ShapeFit.Contain )],
				};
		}
		// A web image is not fetched; its link opens it in the browser.
		if ( Uri.TryCreate( source, UriKind.Absolute, out var uri ) && uri.Scheme is "https" or "http" )
			return MarkdownView.Build( $"[{(alt.Length > 0 ? alt : "Open image")}]({uri.AbsoluteUri})", OpenLink );
		return BuildText( alt.Length > 0 ? alt : "Picture could not be shown", muted: true );
	}

	/// <summary>Decodes a form image from a data URI or a file, relative to the chat's folder, as an image or an SVG drawing; null for a web address,
	/// a missing file, such as one on another PC's disk, or a format that cannot be decoded.</summary>
	private object? LoadFormImage( string source )
	{
		try
		{
			if ( source.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ) )
			{
				var comma = source.IndexOf( ',' );
				if ( comma < 0 )
					return null;

				var header = source[..comma];
				var payload = source[(comma + 1)..];
				var bytes = header.EndsWith( ";base64", StringComparison.OrdinalIgnoreCase )
					? Convert.FromBase64String( payload )
					: Encoding.UTF8.GetBytes( Uri.UnescapeDataString( payload ) );
				return header.StartsWith( "data:image/svg+xml", StringComparison.OrdinalIgnoreCase )
					? ParseSvg( Encoding.UTF8.GetString( bytes ) )
					: DecodeThumbnail( bytes );
			}

			// A file path or file URI is read; any other address, such as a web image, is not.
			string path;
			if ( Uri.TryCreate( source, UriKind.Absolute, out var uri ) && uri.IsFile )
				path = uri.LocalPath;
			else if ( uri is not null )
				return null;
			else
				path = Path.Combine( store.GetDirectory( conversationId ), source );
			if ( !File.Exists( path ) )
				return null;

			return path.EndsWith( ".svg", StringComparison.OrdinalIgnoreCase )
				? ParseSvg( File.ReadAllText( path ) )
				: ImageSource.LoadThumbnail( path, ThumbnailMaxWidth, ThumbnailPixels );
		}
		// Goo rejects formats it cannot decode with NotSupportedException.
		catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException or IOException
			or UnauthorizedAccessException )
		{
			return null;
		}
	}

	/// <summary>Parses SVG markup into a drawing, or null when Goo does not support it.</summary>
	private static VectorAsset? ParseSvg( string markup )
	{
		try
		{
			return Goo.Svg.Svg.Parse( markup );
		}
		catch ( Goo.Svg.SvgParseException )
		{
			return null;
		}
	}

	/// <summary>Gets the option indices picked for a choice field.</summary>
	private HashSet<int> ReadFormPicks( string key )
	{
		var picks = new HashSet<int>();
		foreach ( var pick in session.ReadSelection( key ) )
		{
			if ( int.TryParse( pick, NumberStyles.None, CultureInfo.InvariantCulture, out var index ) )
				picks.Add( index );
		}
		return picks;
	}

	/// <summary>Starts a form's data with its hidden values, which every answer carries.</summary>
	private static JsonObject StartFormData( HtmlForm form )
	{
		var data = new JsonObject();
		foreach ( var (name, value) in form.Hidden )
			data[name] = value;
		return data;
	}

	/// <summary>Answers a form whose one field is a single choice with one of its options, as clicking the option does.</summary>
	private void UpdateFormPick( JsonElement dialog, FormChoiceField field, int index )
	{
		var id = ReadText( dialog, "id" );
		var data = StartFormData( ReadForm( dialog ) );
		data[field.Name] = HtmlForm.ComputeChoiceValue( field, new HashSet<int> { index }, null );
		UpdateDialogAnswer( id, new { type = "extension_ui_response", id, data } );
	}

	/// <summary>Answers a form whose one field is a single choice with an answer of one's own; empty text answers nothing.</summary>
	private void UpdateFormOther( JsonElement dialog, FormChoiceField field, string text )
	{
		if ( text.Trim().Length == 0 )
			return;

		var id = ReadText( dialog, "id" );
		var data = StartFormData( ReadForm( dialog ) );
		data[field.Name] = HtmlForm.ComputeChoiceValue( field, new HashSet<int>(), text );
		UpdateDialogAnswer( id, new { type = "extension_ui_response", id, data } );
	}

	/// <summary>Submits a form's data once every field is valid, as Omp checks it again; otherwise shows each field's error and sends nothing.</summary>
	private void UpdateFormSubmit( JsonElement dialog )
	{
		var id = ReadText( dialog, "id" );
		var form = ReadForm( dialog );
		ReleaseFormErrors( id );
		var data = StartFormData( form );
		foreach ( var field in form.Fields )
		{
			// Omp leaves fields without a name out of the answer.
			if ( field.Name.Length == 0 )
				continue;

			var key = id + "." + field.Name;
			var value = ReadFormValue( id, field );
			if ( HtmlForm.Validate( field, value ) is { } error )
				_formErrors[key] = error;
			else if ( value is not null )
				data[field.Name] = value;
		}
		if ( _formErrors.Keys.Any( key => key.StartsWith( id + ".", StringComparison.Ordinal ) ) )
		{
			MarkChanged();
			return;
		}

		UpdateDialogAnswer( id, new { type = "extension_ui_response", id, data } );
	}

	/// <summary>Gets the value a form field would send now: its picked options and answer of one's own, or its typed text.</summary>
	private JsonNode? ReadFormValue( string id, FormField field )
	{
		var key = id + "." + field.Name;
		return field switch
		{
			FormChoiceField choice => HtmlForm.ComputeChoiceValue( choice, ReadFormPicks( key ),
				choice.HasOther ? EnsureDialogEditor( key + ".other", "" ).Document.GetText() : null ),
			FormTextField text => HtmlForm.ComputeTextValue( text, EnsureDialogEditor( key, text.Value ).Document.GetText() ),
			_ => null,
		};
	}

	/// <summary>Gets the message of each named field that is not valid now, by field name, as a submit would check it.</summary>
	private Dictionary<string, string> ComputeFormErrors( string id, HtmlForm form )
	{
		var errors = new Dictionary<string, string>();
		foreach ( var field in form.Fields )
		{
			// Omp leaves fields without a name out of the answer, so it does not check them.
			if ( field.Name.Length > 0 && HtmlForm.Validate( field, ReadFormValue( id, field ) ) is { } error )
				errors[field.Name] = error;
		}
		return errors;
	}

	/// <summary>Forgets the errors the last submit gave a form's fields.</summary>
	private void ReleaseFormErrors( string id )
	{
		foreach ( var key in _formErrors.Keys.Where( key => key.StartsWith( id + ".", StringComparison.Ordinal ) ).ToArray() )
			_formErrors.Remove( key );
	}

	/// <summary>Forgets a finished form: its parse, its errors and its pictures.</summary>
	private void ReleaseForm( string id )
	{
		_forms.Remove( id );
		ReleaseFormErrors( id );
		foreach ( var key in _formPictures.Keys.Where( key => key.StartsWith( id + ":", StringComparison.Ordinal ) ).ToArray() )
		{
			(_formPictures[key] as ImageSource)?.Dispose();
			_formPictures.Remove( key );
		}
	}

	/// <summary>Releases every form picture as the panel closes.</summary>
	private void ReleaseFormPictures()
	{
		foreach ( var picture in _formPictures.Values )
			(picture as ImageSource)?.Dispose();
		_formPictures.Clear();
	}
}
