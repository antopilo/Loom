using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lokn.Modules.Chat;

/// <summary>A piece of an Omp form's content, in document order.</summary>
internal abstract record FormBlock;

/// <summary>A paragraph, heading, list, table or preformatted block written as CommonMark.</summary>
/// <param name="Markdown">The block's Markdown.</param>
/// <param name="IsHeading">Whether the block is a heading.</param>
internal sealed record FormMarkdown( string Markdown, bool IsHeading ) : FormBlock;

/// <summary>An image the form shows.</summary>
/// <param name="Source">An http(s) URL, a data URI, or a file path relative to the agent's working directory.</param>
/// <param name="Alt">The image's alternative text, or empty.</param>
internal sealed record FormImage( string Source, string Alt ) : FormBlock;

/// <summary>An inline SVG drawing the form shows.</summary>
/// <param name="Markup">The drawing's outer markup, from <c>&lt;svg</c> to its matching end tag.</param>
/// <param name="Alt">The drawing's title or aria-label, or empty.</param>
internal sealed record FormSvg( string Markup, string Alt ) : FormBlock;

/// <summary>A field shown where its first control appears.</summary>
/// <param name="Field">The field.</param>
internal sealed record FormFieldBlock( FormField Field ) : FormBlock;

/// <summary>A run of blocks: the form's top level or one top-level fieldset.</summary>
/// <param name="Title">The fieldset's legend as Markdown, or empty.</param>
/// <param name="IsFieldset">Whether the section comes from a fieldset.</param>
/// <param name="Blocks">The section's blocks in document order.</param>
internal sealed record FormSection( string Title, bool IsFieldset, IReadOnlyList<FormBlock> Blocks );

/// <summary>A value the form asks for, sent under its name.</summary>
/// <param name="Name">The name the value is sent under.</param>
/// <param name="Label">The field's label as Markdown, or empty.</param>
/// <param name="IsRequired">Whether the field must have a value.</param>
internal abstract record FormField( string Name, string Label, bool IsRequired );

/// <summary>One option of a choice field.</summary>
/// <param name="Value">The value sent when the option is picked.</param>
/// <param name="Label">The option's plain-text label.</param>
/// <param name="IsChecked">Whether the option starts picked.</param>
/// <param name="IsDisabled">Whether the option cannot be picked.</param>
internal sealed record FormOption( string Value, string Label, bool IsChecked, bool IsDisabled );

/// <summary>A radio group, checkbox group or select.</summary>
/// <param name="Name">The name the value is sent under.</param>
/// <param name="Label">The select's label as Markdown; empty for radio and checkbox groups.</param>
/// <param name="IsRequired">Whether an option must be picked.</param>
/// <param name="IsMulti">Whether several options can be picked.</param>
/// <param name="IsSelect">Whether the field comes from a <c>&lt;select&gt;</c>.</param>
/// <param name="HasOther">Whether the user may type a free-text answer.</param>
/// <param name="Options">The options in document order.</param>
internal sealed record FormChoiceField( string Name, string Label, bool IsRequired, bool IsMulti, bool IsSelect, bool HasOther, IReadOnlyList<FormOption> Options ) : FormField( Name, Label, IsRequired )
{
	/// <summary>Whether the value is sent as an array of strings rather than one string.</summary>
	public bool ReturnsArray => IsMulti && Options.Count > 1;
}

/// <summary>A text input or textarea.</summary>
/// <param name="Name">The name the value is sent under.</param>
/// <param name="Label">The field's label as Markdown.</param>
/// <param name="IsRequired">Whether the field must be filled.</param>
/// <param name="Type">The input type, such as <c>text</c>, <c>number</c> or <c>email</c>, or <c>textarea</c>.</param>
/// <param name="IsMultiline">Whether the field is a textarea.</param>
/// <param name="Value">The starting text.</param>
/// <param name="Placeholder">The placeholder text, or empty.</param>
/// <param name="Min">The smallest number allowed, when set.</param>
/// <param name="Max">The largest number allowed, when set.</param>
/// <param name="Pattern">The regular expression the whole text must match, when set.</param>
/// <param name="MinLength">The fewest characters allowed, when set.</param>
/// <param name="MaxLength">The most characters allowed, when set.</param>
/// <param name="Rows">The visible line count.</param>
internal sealed record FormTextField( string Name, string Label, bool IsRequired, string Type, bool IsMultiline, string Value, string Placeholder, double? Min, double? Max, string? Pattern, int? MinLength, int? MaxLength, int Rows ) : FormField( Name, Label, IsRequired );

/// <summary>Reads the HTML form Omp's <c>ask</c> tool sends, following Omp's own rules for fields, values and validation.</summary>
internal sealed partial class HtmlForm
{
	/// <summary>The deepest element nesting the parser keeps; deeper elements become siblings.</summary>
	private const int MaxDepth = 200;

	/// <summary>Elements that never have content.</summary>
	private static readonly HashSet<string> VoidTags = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];

	/// <summary>Elements whose content is text up to their end tag.</summary>
	private static readonly HashSet<string> RawTextTags = ["script", "style", "textarea", "title"];

	/// <summary>Elements skipped with their content.</summary>
	private static readonly HashSet<string> IgnoredTags = ["button", "head", "iframe", "link", "meta", "noscript", "script", "style", "template", "title"];

	/// <summary>Input types that are not fields.</summary>
	private static readonly HashSet<string> IgnoredInputTypes = ["button", "file", "image", "reset", "submit"];

	/// <summary>Elements that break text into separate paragraphs.</summary>
	private static readonly HashSet<string> BlockTags = ["address", "article", "aside", "blockquote", "dd", "details", "div", "dl", "dt", "figure", "figcaption", "footer", "header", "li", "main", "nav", "p", "section"];

	/// <summary>Elements shown as headings.</summary>
	private static readonly HashSet<string> HeadingTags = ["h1", "h2", "h3", "h4", "h5", "h6", "summary"];

	/// <summary>Elements that cannot sit inside a paragraph, so they close an open <c>&lt;p&gt;</c>.</summary>
	private static readonly HashSet<string> ParagraphClosers = ["address", "article", "aside", "blockquote", "dd", "details", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "menu", "nav", "ol", "p", "pre", "section", "summary", "table", "ul"];

	/// <summary>Elements an implied end tag does not reach past.</summary>
	private static readonly HashSet<string> ScopeTags = ["applet", "button", "caption", "html", "marquee", "object", "select", "svg", "table", "td", "template", "th"];

	/// <summary>Elements an implied <c>&lt;li&gt;</c> end does not reach past.</summary>
	private static readonly HashSet<string> ListScopeTags = [.. ScopeTags, "ol", "ul"];

	/// <summary>Elements an implied <c>&lt;dt&gt;</c> or <c>&lt;dd&gt;</c> end does not reach past.</summary>
	private static readonly HashSet<string> DefinitionScopeTags = [.. ScopeTags, "dl"];

	/// <summary>Elements an end tag outside a table does not reach past.</summary>
	private static readonly HashSet<string> EndScopeTags = ["button", "caption", "select", "svg", "table", "td", "template", "th"];

	/// <summary>Table parts whose end tags reach up to the table.</summary>
	private static readonly HashSet<string> TableTags = ["caption", "table", "tbody", "td", "tfoot", "th", "thead", "tr"];

	/// <summary>Elements that hold content not shown inline, so a styled span containing them is walked as blocks.</summary>
	private static readonly HashSet<string> NonInlineTags = ["fieldset", "hr", "img", "ol", "pre", "svg", "table", "ul"];

	/// <summary>Characters escaped so source text renders literally in Markdown.</summary>
	private static readonly HashSet<char> EscapedChars = ['\\', '`', '*', '_', '[', ']', '<', '>', '#', '|', '&', '~'];

	/// <summary>Creates a parsed form.</summary>
	/// <param name="sections">The non-empty sections.</param>
	/// <param name="fields">The fields in document order.</param>
	/// <param name="hidden">The hidden inputs' names and values.</param>
	private HtmlForm( IReadOnlyList<FormSection> sections, IReadOnlyList<FormField> fields, IReadOnlyList<KeyValuePair<string, string>> hidden )
	{
		Sections = sections;
		Fields = fields;
		Hidden = hidden;
	}

	/// <summary>The form's sections; sections with no blocks and no title are dropped.</summary>
	public IReadOnlyList<FormSection> Sections { get; }

	/// <summary>The form's fields in document order.</summary>
	public IReadOnlyList<FormField> Fields { get; }

	/// <summary>The hidden inputs' names and values, always sent.</summary>
	public IReadOnlyList<KeyValuePair<string, string>> Hidden { get; }

	/// <summary>Parses a form's HTML. Malformed markup is read as leniently as a browser would, never thrown on.</summary>
	/// <param name="html">The form's HTML.</param>
	public static HtmlForm Parse( string html )
	{
		var document = BuildTree( html ?? "" );
		return new Builder( FindElement( document, "form" ) ?? document ).Build();
	}

	/// <summary>Computes a choice field's value from the picked options and the typed "other" answer.</summary>
	/// <param name="field">The field.</param>
	/// <param name="picked">The indices of the picked options; disabled options are ignored.</param>
	/// <param name="other">The typed "other" answer, or null.</param>
	/// <returns>An array of strings when the field returns an array, else a string, or null when there is no value.</returns>
	public static JsonNode? ComputeChoiceValue( FormChoiceField field, IReadOnlySet<int> picked, string? other )
	{
		var values = new List<string>();
		for ( var index = 0; index < field.Options.Count; index++ )
		{
			if ( picked.Contains( index ) && !field.Options[index].IsDisabled )
				values.Add( field.Options[index].Value );
		}

		var typed = other?.Trim() ?? "";
		if ( field.ReturnsArray )
		{
			var array = new JsonArray();
			foreach ( var value in values )
				array.Add( JsonValue.Create( value ) );
			if ( typed.Length > 0 )
				array.Add( JsonValue.Create( typed ) );
			return array;
		}

		if ( typed.Length > 0 )
			return JsonValue.Create( typed );
		return values.Count > 0 ? JsonValue.Create( values[0] ) : null;
	}

	/// <summary>Computes a text field's value: a number for number and range fields whose text parses to a finite number, else the text.</summary>
	/// <param name="field">The field.</param>
	/// <param name="text">The field's text.</param>
	public static JsonNode? ComputeTextValue( FormTextField field, string text )
	{
		if ( field.Type is "number" or "range" && double.TryParse( text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number ) && double.IsFinite( number ) )
			return JsonValue.Create( number );
		return JsonValue.Create( text );
	}

	/// <summary>Checks a field's value the way Omp does.</summary>
	/// <param name="field">The field.</param>
	/// <param name="value">The value from <see cref="ComputeChoiceValue"/> or <see cref="ComputeTextValue"/>.</param>
	/// <returns>Null when the value is valid, else Omp's message.</returns>
	public static string? Validate( FormField field, JsonNode? value )
	{
		if ( field is FormChoiceField choice )
		{
			if ( choice.IsRequired && value is null or JsonArray { Count: 0 } )
				return choice.ReturnsArray ? "Select at least one" : "Required";
			return null;
		}

		return field is FormTextField text ? ValidateText( text, value ) : null;
	}

	/// <summary>Computes what the untouched form submits: hidden values, each text field's value and each choice field's checked options.</summary>
	/// <remarks>Fields with an empty name and fields without a value are left out.</remarks>
	public JsonObject ComputeDefaults()
	{
		var data = new JsonObject();
		foreach ( var (name, value) in Hidden )
		{
			if ( name.Length > 0 )
				data[name] = value;
		}

		foreach ( var field in Fields )
		{
			if ( field.Name.Length == 0 )
				continue;

			var value = field switch
			{
				FormTextField text => ComputeTextValue( text, text.Value ),
				FormChoiceField choice => ComputeChoiceValue( choice, Enumerable.Range( 0, choice.Options.Count ).Where( index => choice.Options[index].IsChecked ).ToHashSet(), null ),
				_ => null,
			};
			if ( value is not null )
				data[field.Name] = value;
		}

		return data;
	}

	/// <summary>Checks a text field's value.</summary>
	/// <param name="field">The field.</param>
	/// <param name="value">The value: a string, a number, or null.</param>
	private static string? ValidateText( FormTextField field, JsonNode? value )
	{
		var raw = "";
		double? number = null;
		if ( value is JsonValue scalar )
		{
			switch ( scalar.GetValueKind() )
			{
				case JsonValueKind.Number:
					raw = scalar.ToJsonString();
					if ( double.TryParse( raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed ) )
						number = parsed;
					break;
				case JsonValueKind.String:
					raw = scalar.GetValue<string>();
					break;
				default:
					raw = scalar.ToJsonString();
					break;
			}
		}

		if ( number is null && raw.Trim().Length == 0 )
			return field.IsRequired ? "Required" : null;

		if ( field.Type is "number" or "range" )
		{
			var amount = number ?? ( double.TryParse( raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed ) ? typed : double.NaN );
			if ( !double.IsFinite( amount ) )
				return "Enter a number";
			if ( field.Min is { } min && amount < min )
				return $"Must be at least {min.ToString( CultureInfo.InvariantCulture )}";
			if ( field.Max is { } max && amount > max )
				return $"Must be at most {max.ToString( CultureInfo.InvariantCulture )}";
		}

		if ( field.MinLength is { } minLength && raw.Length < minLength )
			return $"Needs at least {minLength} characters";
		if ( field.MaxLength is { } maxLength && raw.Length > maxLength )
			return $"At most {maxLength} characters";
		if ( field.Type == "email" && !EmailPattern().IsMatch( raw ) )
			return "Enter an email address";
		if ( field.Type == "url" && !Uri.TryCreate( raw, UriKind.Absolute, out _ ) )
			return "Enter a full URL";
		if ( !string.IsNullOrEmpty( field.Pattern ) && !MatchesPattern( field.Pattern, raw ) )
			return $"Must match {( field.Pattern.Length > 40 ? field.Pattern[..39] + "…" : field.Pattern )}";
		return null;
	}

	/// <summary>Whether text matches a field's whole pattern; an invalid or runaway pattern counts as a match.</summary>
	/// <param name="pattern">The field's pattern.</param>
	/// <param name="text">The field's text.</param>
	private static bool MatchesPattern( string pattern, string text )
	{
		try
		{
			return Regex.IsMatch( text, $"^(?:{pattern})\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 250 ) );
		}
		catch ( ArgumentException )
		{
			return true;
		}
		catch ( RegexMatchTimeoutException )
		{
			return true;
		}
	}

	/// <summary>Builds the element tree of a document.</summary>
	/// <param name="html">The document's HTML.</param>
	private static Node BuildTree( string html )
	{
		var document = new Node( "#document", null );
		var stack = new List<Node> { document };
		var text = new StringBuilder();
		var index = 0;
		while ( index < html.Length )
		{
			var current = html[index];
			var next = index + 1 < html.Length ? html[index + 1] : '\0';
			if ( current != '<' || !( char.IsAsciiLetter( next ) || next is '/' or '!' or '?' ) )
			{
				text.Append( current );
				index++;
				continue;
			}

			AddText( stack, text );
			if ( html.AsSpan( index ).StartsWith( "<!--", StringComparison.Ordinal ) )
			{
				var end = html.IndexOf( "-->", index + 4, StringComparison.Ordinal );
				index = end < 0 ? html.Length : end + 3;
			}
			else if ( next is '!' or '?' )
				index = SkipPast( html, index, '>' );
			else if ( next == '/' )
			{
				var start = index + 2;
				var end = start;
				while ( end < html.Length && IsNameChar( html[end] ) )
					end++;
				if ( end > start && char.IsAsciiLetter( html[start] ) )
					CloseTag( stack, html[start..end].ToLowerInvariant() );
				index = SkipPast( html, end, '>' );
			}
			else
				index = ReadStartTag( html, index, stack );
		}

		AddText( stack, text );
		return document;
	}

	/// <summary>Reads one start tag and adds its element, returning the index after it.</summary>
	/// <param name="html">The document's HTML.</param>
	/// <param name="index">The index of the tag's <c>&lt;</c>.</param>
	/// <param name="stack">The open elements, innermost last.</param>
	private static int ReadStartTag( string html, int index, List<Node> stack )
	{
		var position = index + 1;
		while ( position < html.Length && IsNameChar( html[position] ) )
			position++;
		var tag = html[( index + 1 )..position].ToLowerInvariant();

		var attributes = new List<KeyValuePair<string, string>>();
		var isSelfClosing = false;
		while ( position < html.Length )
		{
			var current = html[position];
			if ( current == '>' )
			{
				position++;
				break;
			}

			if ( IsSpace( current ) )
			{
				position++;
				continue;
			}

			if ( current == '/' )
			{
				position++;
				if ( position < html.Length && html[position] == '>' )
				{
					isSelfClosing = true;
					position++;
					break;
				}

				continue;
			}

			var nameStart = position;
			position++;
			while ( position < html.Length && !IsSpace( html[position] ) && html[position] is not ( '=' or '>' or '/' ) )
				position++;
			var name = html[nameStart..position].ToLowerInvariant();

			var probe = position;
			while ( probe < html.Length && IsSpace( html[probe] ) )
				probe++;
			var value = "";
			if ( probe < html.Length && html[probe] == '=' )
			{
				position = probe + 1;
				while ( position < html.Length && IsSpace( html[position] ) )
					position++;
				if ( position < html.Length && html[position] is '"' or '\'' )
				{
					var close = html.IndexOf( html[position], position + 1 );
					if ( close < 0 )
						close = html.Length;
					value = html[( position + 1 )..close];
					position = Math.Min( close + 1, html.Length );
				}
				else
				{
					var start = position;
					while ( position < html.Length && !IsSpace( html[position] ) && html[position] != '>' )
						position++;
					value = html[start..position];
				}
			}

			attributes.Add( new( name, WebUtility.HtmlDecode( value ) ) );
		}

		CloseImplied( stack, tag );
		var parent = stack[^1];
		var node = new Node( tag, parent );
		foreach ( var (name, value) in attributes )
			node.Attributes.TryAdd( name, value );
		parent.Children.Add( node );

		if ( tag == "svg" )
			node.Markup = html[index..FindSvgEnd( html, position, isSelfClosing )];
		if ( isSelfClosing || VoidTags.Contains( tag ) )
			return position;

		if ( RawTextTags.Contains( tag ) )
		{
			var end = html.IndexOf( "</" + tag, position, StringComparison.OrdinalIgnoreCase );
			if ( end < 0 )
				end = html.Length;
			var raw = html[position..end];
			node.Children.Add( new Node( "#text", node ) { Text = tag is "script" or "style" ? raw : WebUtility.HtmlDecode( raw ) } );
			return end < html.Length ? SkipPast( html, end, '>' ) : end;
		}

		if ( stack.Count < MaxDepth )
			stack.Add( node );
		return position;
	}

	/// <summary>Finds the end of an SVG element's outer markup.</summary>
	/// <param name="html">The document's HTML.</param>
	/// <param name="start">The index just after the SVG's start tag.</param>
	/// <param name="isSelfClosing">Whether the start tag closed itself.</param>
	private static int FindSvgEnd( string html, int start, bool isSelfClosing )
	{
		if ( isSelfClosing )
			return start;

		var depth = 1;
		for ( var match = SvgTagPattern().Match( html, start ); match.Success; match = match.NextMatch() )
		{
			if ( match.Groups[1].Length > 0 )
			{
				if ( --depth == 0 )
					return match.Index + match.Length;
			}
			else if ( !match.Value.EndsWith( "/>", StringComparison.Ordinal ) )
				depth++;
		}

		return html.Length;
	}

	/// <summary>Adds pending text to the innermost open element.</summary>
	/// <param name="stack">The open elements, innermost last.</param>
	/// <param name="text">The pending source text, cleared afterwards.</param>
	private static void AddText( List<Node> stack, StringBuilder text )
	{
		if ( text.Length == 0 )
			return;

		var parent = stack[^1];
		parent.Children.Add( new Node( "#text", parent ) { Text = WebUtility.HtmlDecode( text.ToString() ) } );
		text.Clear();
	}

	/// <summary>Closes the elements a start tag ends implicitly, such as an open paragraph before a block.</summary>
	/// <param name="stack">The open elements, innermost last.</param>
	/// <param name="tag">The starting element's name.</param>
	private static void CloseImplied( List<Node> stack, string tag )
	{
		if ( ParagraphClosers.Contains( tag ) )
			CloseOpen( stack, ["p"], ScopeTags );

		switch ( tag )
		{
			case "li":
				CloseOpen( stack, ["li"], ListScopeTags );
				break;
			case "dt" or "dd":
				CloseOpen( stack, ["dt", "dd"], DefinitionScopeTags );
				break;
			case "option":
				CloseOpen( stack, ["option"], ["select", "optgroup", "datalist"] );
				break;
			case "optgroup":
				CloseOpen( stack, ["option", "optgroup"], ["select", "datalist"] );
				break;
			case "tr":
				CloseOpen( stack, ["tr", "td", "th"], ["table", "thead", "tbody", "tfoot"] );
				break;
			case "td" or "th":
				CloseOpen( stack, ["td", "th"], ["tr", "table"] );
				break;
			case "thead" or "tbody" or "tfoot":
				CloseOpen( stack, ["td", "th", "tr", "thead", "tbody", "tfoot"], ["table"] );
				break;
		}
	}

	/// <summary>Closes the outermost open target element found before a boundary, with everything inside it.</summary>
	/// <param name="stack">The open elements, innermost last.</param>
	/// <param name="targets">The element names to close.</param>
	/// <param name="boundaries">The element names the search stops at.</param>
	private static void CloseOpen( List<Node> stack, string[] targets, HashSet<string> boundaries )
	{
		var found = -1;
		for ( var index = stack.Count - 1; index > 0; index-- )
		{
			var open = stack[index].Tag;
			if ( targets.Contains( open ) )
				found = index;
			else if ( boundaries.Contains( open ) )
				break;
		}

		if ( found > 0 )
			stack.RemoveRange( found, stack.Count - found );
	}

	/// <summary>Closes the innermost open element an end tag names; an end tag with no open element is ignored.</summary>
	/// <param name="stack">The open elements, innermost last.</param>
	/// <param name="tag">The end tag's name.</param>
	private static void CloseTag( List<Node> stack, string tag )
	{
		var isTablePart = TableTags.Contains( tag );
		for ( var index = stack.Count - 1; index > 0; index-- )
		{
			var open = stack[index].Tag;
			if ( open == tag )
			{
				stack.RemoveRange( index, stack.Count - index );
				return;
			}

			if ( isTablePart ? open == "table" : EndScopeTags.Contains( open ) )
				return;
		}
	}

	/// <summary>Returns the index after the next occurrence of a character, or the end of the text.</summary>
	/// <param name="html">The document's HTML.</param>
	/// <param name="start">Where the search starts.</param>
	/// <param name="character">The character to pass.</param>
	private static int SkipPast( string html, int start, char character )
	{
		var index = html.IndexOf( character, Math.Min( start, html.Length ) );
		return index < 0 ? html.Length : index + 1;
	}

	/// <summary>Whether a character can be part of a tag name.</summary>
	/// <param name="character">The character.</param>
	private static bool IsNameChar( char character ) => char.IsAsciiLetterOrDigit( character ) || character is '-' or ':' or '_' or '.';

	/// <summary>Whether a character is HTML whitespace.</summary>
	/// <param name="character">The character.</param>
	private static bool IsSpace( char character ) => character is ' ' or '\t' or '\n' or '\r' or '\f';

	/// <summary>Finds the first element with a name, depth first.</summary>
	/// <param name="node">The element to search inside.</param>
	/// <param name="tag">The element name.</param>
	private static Node? FindElement( Node node, string tag )
	{
		foreach ( var child in node.Children )
		{
			if ( child.Tag == tag )
				return child;
			if ( FindElement( child, tag ) is { } found )
				return found;
		}

		return null;
	}

	/// <summary>Collapses whitespace runs to one space and trims the ends.</summary>
	/// <param name="text">The text, or null.</param>
	private static string Collapse( string? text )
	{
		if ( string.IsNullOrEmpty( text ) )
			return "";

		var builder = new StringBuilder( text.Length );
		var isPending = false;
		foreach ( var character in text )
		{
			if ( IsSpace( character ) )
				isPending = builder.Length > 0;
			else
			{
				if ( isPending )
					builder.Append( ' ' );
				isPending = false;
				builder.Append( character );
			}
		}

		return builder.ToString();
	}

	/// <summary>Escapes list and heading markers that would start a Markdown block at the start of each line.</summary>
	/// <param name="markdown">The inline Markdown.</param>
	private static string EscapeLineStarts( string markdown )
	{
		if ( markdown.Length == 0 )
			return markdown;

		var lines = markdown.Split( '\n' );
		for ( var index = 0; index < lines.Length; index++ )
		{
			var line = lines[index];
			if ( line.Length == 0 )
				continue;

			if ( line[0] is '-' or '+' or '=' )
			{
				lines[index] = "\\" + line;
				continue;
			}

			var digits = 0;
			while ( digits < line.Length && digits < 10 && char.IsAsciiDigit( line[digits] ) )
				digits++;
			if ( digits > 0 && digits < line.Length && line[digits] is '.' or ')' )
				lines[index] = line.Insert( digits, "\\" );
		}

		return string.Join( '\n', lines );
	}

	/// <summary>Returns a fence of backticks longer than any backtick run in the text.</summary>
	/// <param name="text">The fenced text.</param>
	/// <param name="minimum">The shortest fence.</param>
	private static string ComputeFence( string text, int minimum )
	{
		var longest = 0;
		var run = 0;
		foreach ( var character in text )
		{
			run = character == '`' ? run + 1 : 0;
			longest = Math.Max( longest, run );
		}

		return new string( '`', Math.Max( minimum, longest + 1 ) );
	}

	/// <summary>Reads an attribute as a finite number.</summary>
	/// <param name="text">The attribute's value, or null.</param>
	private static double? ParseNumber( string? text ) =>
		double.TryParse( text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value ) && double.IsFinite( value ) ? value : null;

	/// <summary>Reads an attribute as a non-negative whole number.</summary>
	/// <param name="text">The attribute's value, or null.</param>
	private static int? ParseCount( string? text ) =>
		int.TryParse( text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value ) && value >= 0 ? value : null;

	/// <summary>Matches an SVG start or end tag; the first group holds the end tag's slash.</summary>
	[GeneratedRegex( @"<(/?)svg\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant )]
	private static partial Regex SvgTagPattern();

	/// <summary>Matches an option label that already offers a free-text answer.</summary>
	[GeneratedRegex( @"^\s*other\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant )]
	private static partial Regex OtherPattern();

	/// <summary>Matches what Omp accepts as an email address.</summary>
	[GeneratedRegex( @"^[^\s@]+@[^\s@]+\z" )]
	private static partial Regex EmailPattern();

	/// <summary>One element or text run of a parsed document.</summary>
	/// <param name="tag">The lowercase element name, <c>#text</c> for text, or <c>#document</c> for the root.</param>
	/// <param name="parent">The containing element, or null for the root.</param>
	private sealed class Node( string tag, Node? parent )
	{
		/// <summary>The lowercase element name, <c>#text</c> for text, or <c>#document</c> for the root.</summary>
		public string Tag { get; } = tag;

		/// <summary>The containing element, or null for the root.</summary>
		public Node? Parent { get; } = parent;

		/// <summary>The child elements and text runs in document order.</summary>
		public List<Node> Children { get; } = [];

		/// <summary>The attributes by lowercase name; the first of a repeated name wins.</summary>
		public Dictionary<string, string> Attributes { get; } = new( StringComparer.Ordinal );

		/// <summary>A text run's decoded text.</summary>
		public string Text { get; init; } = "";

		/// <summary>An SVG element's outer source markup.</summary>
		public string Markup { get; set; } = "";

		/// <summary>Whether this is a text run.</summary>
		public bool IsText => Tag == "#text";

		/// <summary>Reads an attribute, or null when it is missing.</summary>
		/// <param name="name">The lowercase attribute name.</param>
		public string? Get( string name ) => Attributes.TryGetValue( name, out var value ) ? value : null;

		/// <summary>Whether an attribute is present.</summary>
		/// <param name="name">The lowercase attribute name.</param>
		public bool Has( string name ) => Attributes.ContainsKey( name );
	}

	/// <summary>A label's text, as Markdown and as plain text.</summary>
	/// <param name="Markdown">The label as Markdown.</param>
	/// <param name="Plain">The label as plain text with whitespace collapsed.</param>
	private sealed record LabelText( string Markdown, string Plain );

	/// <summary>One table row.</summary>
	/// <param name="Cells">The row's <c>td</c> and <c>th</c> cells.</param>
	/// <param name="IsHead">Whether the row is inside <c>thead</c>.</param>
	private sealed record TableRow( List<Node> Cells, bool IsHead );

	/// <summary>A section being built.</summary>
	/// <param name="title">The section's title as Markdown, or empty.</param>
	/// <param name="isFieldset">Whether the section comes from a fieldset.</param>
	private sealed class SectionDraft( string title, bool isFieldset )
	{
		/// <summary>The section's title as Markdown, or empty.</summary>
		public string Title { get; } = title;

		/// <summary>Whether the section comes from a fieldset.</summary>
		public bool IsFieldset { get; } = isFieldset;

		/// <summary>The section's blocks: finished blocks, or choice drafts still gathering options.</summary>
		public List<object> Blocks { get; } = [];
	}

	/// <summary>A choice field still gathering its options.</summary>
	/// <param name="name">The name the value is sent under.</param>
	/// <param name="label">The field's label as Markdown, or empty.</param>
	/// <param name="isMulti">Whether several options can be picked.</param>
	/// <param name="isSelect">Whether the field comes from a select.</param>
	private sealed class ChoiceDraft( string name, string label, bool isMulti, bool isSelect )
	{
		/// <summary>The field built once every option is gathered.</summary>
		private FormChoiceField? field;

		/// <summary>Whether an option must be picked.</summary>
		public bool IsRequired { get; set; }

		/// <summary>The options in document order.</summary>
		public List<FormOption> Options { get; } = [];

		/// <summary>Builds the field once and returns the same instance afterwards.</summary>
		public FormChoiceField Finish()
		{
			var hasOther = !isSelect && Options.Count >= 2 && !Options.Any( option => OtherPattern().IsMatch( option.Label ) );
			return field ??= new( name, label, IsRequired, isMulti, isSelect, hasOther, [.. Options] );
		}
	}

	/// <summary>Writes inline content as Markdown, collapsing whitespace and escaping source text.</summary>
	private sealed class InlineWriter
	{
		/// <summary>The Markdown written so far.</summary>
		private readonly StringBuilder builder = new();

		/// <summary>Whether a space is owed before the next content.</summary>
		private bool isPending;

		/// <summary>Whether whitespace came before any content.</summary>
		public bool LeadingSpace { get; private set; }

		/// <summary>Whether whitespace came after the last content.</summary>
		public bool TrailingSpace => isPending;

		/// <summary>Owes one space before the next content.</summary>
		public void Space()
		{
			if ( builder.Length == 0 )
				LeadingSpace = true;
			else if ( builder[^1] != '\n' )
				isPending = true;
		}

		/// <summary>Writes source text, escaped so it renders literally.</summary>
		/// <param name="text">The decoded source text.</param>
		public void Text( string text )
		{
			foreach ( var character in text )
			{
				if ( IsSpace( character ) )
				{
					Space();
					continue;
				}

				Settle();
				if ( EscapedChars.Contains( character ) )
					builder.Append( '\\' );
				builder.Append( character );
			}
		}

		/// <summary>Writes Markdown as is.</summary>
		/// <param name="markdown">The Markdown.</param>
		public void Markdown( string markdown )
		{
			Settle();
			builder.Append( markdown );
		}

		/// <summary>Writes a hard line break.</summary>
		public void Break()
		{
			if ( builder.Length == 0 )
				return;

			builder.Append( "  \n" );
			isPending = false;
		}

		/// <summary>Returns the Markdown without trailing breaks.</summary>
		public string Build() => builder.ToString().TrimEnd( ' ', '\n' );

		/// <summary>Writes an owed space.</summary>
		private void Settle()
		{
			if ( isPending )
				builder.Append( ' ' );
			isPending = false;
		}
	}

	/// <summary>Walks a form's elements into sections, blocks and fields.</summary>
	private sealed class Builder
	{
		/// <summary>The form element, or the whole document.</summary>
		private readonly Node root;

		/// <summary>The first <c>&lt;label for&gt;</c> naming each id.</summary>
		private readonly Dictionary<string, Node> labelsById = new( StringComparer.Ordinal );

		/// <summary>The ids of the form's controls.</summary>
		private readonly HashSet<string> controlIds = new( StringComparer.Ordinal );

		/// <summary>The sections in document order.</summary>
		private readonly List<SectionDraft> sections = [];

		/// <summary>The fields in document order: text fields and choice drafts.</summary>
		private readonly List<object> fields = [];

		/// <summary>The radio and checkbox groups by type and name.</summary>
		private readonly Dictionary<string, ChoiceDraft> groups = new( StringComparer.Ordinal );

		/// <summary>The hidden inputs' names and values.</summary>
		private readonly List<KeyValuePair<string, string>> hidden = [];

		/// <summary>The paragraph being written.</summary>
		private InlineWriter paragraph = new();

		/// <summary>Whether the paragraph being written is a heading.</summary>
		private bool isHeading;

		/// <summary>How many fieldsets the walk is inside.</summary>
		private int fieldsetDepth;

		/// <summary>The section blocks are added to.</summary>
		private SectionDraft section = new( "", false );

		/// <summary>Creates a builder for a form element.</summary>
		/// <param name="root">The form element, or the whole document.</param>
		public Builder( Node root )
		{
			this.root = root;
			sections.Add( section );
			Index( root );
		}

		/// <summary>Walks the form and returns the result.</summary>
		public HtmlForm Build()
		{
			foreach ( var child in root.Children )
				Walk( child );
			Flush();

			var built = new List<FormSection>();
			foreach ( var draft in sections )
			{
				if ( draft.Blocks.Count == 0 && draft.Title.Length == 0 )
					continue;

				var blocks = draft.Blocks.Select( block => block is ChoiceDraft choice ? new FormFieldBlock( choice.Finish() ) : (FormBlock) block ).ToArray();
				built.Add( new( draft.Title, draft.IsFieldset, blocks ) );
			}

			var finished = fields.Select( field => field is ChoiceDraft choice ? choice.Finish() : (FormField) field ).ToArray();
			return new( built, finished, hidden );
		}

		/// <summary>Records labels with a target, control ids and hidden inputs.</summary>
		/// <param name="node">The element to index inside.</param>
		private void Index( Node node )
		{
			foreach ( var child in node.Children )
			{
				if ( child.IsText || IgnoredTags.Contains( child.Tag ) )
					continue;

				if ( child.Tag == "label" && child.Get( "for" ) is { Length: > 0 } target )
					labelsById.TryAdd( target, child );
				if ( child.Tag == "input" && InputType( child ) == "hidden" )
					hidden.Add( new( child.Get( "name" ) ?? "", child.Get( "value" ) ?? "" ) );
				if ( IsControl( child ) && child.Get( "id" ) is { Length: > 0 } id )
					controlIds.Add( id );
				Index( child );
			}
		}

		/// <summary>Walks one node of the form's content.</summary>
		/// <param name="node">The node.</param>
		private void Walk( Node node )
		{
			if ( node.IsText )
			{
				paragraph.Text( node.Text );
				return;
			}

			var tag = node.Tag;
			if ( IgnoredTags.Contains( tag ) )
				return;

			switch ( tag )
			{
				case "input" or "select" or "textarea":
					AddControl( node, null );
					return;
				case "label":
					WalkLabel( node );
					return;
				case "br":
					paragraph.Break();
					return;
				case "hr":
					Flush();
					return;
				case "img":
					Flush();
					if ( node.Get( "src" )?.Trim() is { Length: > 0 } source )
						Add( new FormImage( source, Collapse( node.Get( "alt" ) ) ) );
					return;
				case "svg":
					Flush();
					Add( new FormSvg( node.Markup, ComputeSvgAlt( node ) ) );
					return;
				case "pre":
					Flush();
					AddPre( node );
					return;
				case "table":
					Flush();
					if ( HasControls( node ) )
						WalkTable( node );
					else
						AddTable( node );
					return;
				case "fieldset":
					WalkFieldset( node );
					return;
				case "legend":
					WalkHeading( node );
					return;
				case "ul" or "ol" when !HasControls( node ):
					Flush();
					AddMarkdown( RenderList( node, 0 ), false );
					return;
			}

			if ( HeadingTags.Contains( tag ) )
			{
				WalkHeading( node );
				return;
			}

			if ( tag is "b" or "strong" or "em" or "i" or "code" or "kbd" or "samp" or "tt" && IsPlainInline( node ) )
			{
				RenderInlineNode( node, paragraph );
				return;
			}

			var isBlock = BlockTags.Contains( tag ) || tag is "ul" or "ol" or "form";
			if ( isBlock )
				Flush();
			WalkChildren( node );
			if ( isBlock )
				Flush();
		}

		/// <summary>Walks an element's children.</summary>
		/// <param name="node">The element.</param>
		private void WalkChildren( Node node )
		{
			foreach ( var child in node.Children )
				Walk( child );
		}

		/// <summary>Walks a heading's content as a heading paragraph.</summary>
		/// <param name="node">The heading element.</param>
		private void WalkHeading( Node node )
		{
			Flush();
			isHeading = true;
			WalkChildren( node );
			Flush();
		}

		/// <summary>Walks a label: its controls when it wraps any, nothing when it names a control elsewhere, else its text.</summary>
		/// <param name="label">The label element.</param>
		private void WalkLabel( Node label )
		{
			if ( HasControls( label ) )
			{
				foreach ( var control in Controls( label ) )
					AddControl( control, null );
				return;
			}

			if ( label.Get( "for" ) is { Length: > 0 } target && controlIds.Contains( target ) )
				return;
			WalkChildren( label );
		}

		/// <summary>Walks a fieldset: a top-level one starts a section titled by its legend, a nested one shows its legend as a heading.</summary>
		/// <param name="node">The fieldset element.</param>
		private void WalkFieldset( Node node )
		{
			Flush();
			var legend = node.Children.FirstOrDefault( child => child.Tag == "legend" );
			var title = legend is null ? "" : InlineMarkdown( legend );
			var isTop = fieldsetDepth == 0;
			if ( isTop )
				StartSection( title, true );
			else if ( title.Length > 0 )
				Add( new FormMarkdown( title, true ) );

			fieldsetDepth++;
			foreach ( var child in node.Children )
			{
				if ( child != legend )
					Walk( child );
			}

			Flush();
			fieldsetDepth--;
			if ( isTop )
				StartSection( "", false );
		}

		/// <summary>Walks a table that holds controls: header rows name columns, other cells become a paragraph and the row's fields.</summary>
		/// <param name="table">The table element.</param>
		private void WalkTable( Node table )
		{
			AddCaption( table );
			string[] headers = [];
			foreach ( var row in CollectRows( table ) )
			{
				if ( row.Cells.Count > 0 && row.Cells.All( cell => cell.Tag == "th" ) && !row.Cells.Any( HasControls ) )
				{
					headers = [.. row.Cells.Select( PlainText )];
					continue;
				}

				var parts = new List<string>();
				var controlled = new List<(Node Cell, int Column)>();
				for ( var column = 0; column < row.Cells.Count; column++ )
				{
					var cell = row.Cells[column];
					if ( HasControls( cell ) )
						controlled.Add( (cell, column) );
					else if ( InlineMarkdown( cell ) is { Length: > 0 } text )
						parts.Add( text );
				}

				Flush();
				if ( parts.Count > 0 )
					Add( new FormMarkdown( string.Join( " · ", parts ), false ) );
				foreach ( var (cell, column) in controlled )
				{
					var fallback = LabelOf( cell ) ?? ( column < headers.Length ? FromText( headers[column] ) : null );
					foreach ( var control in Controls( cell ) )
						AddControl( control, fallback );
				}
			}
		}

		/// <summary>Adds a table without controls as a GFM pipe table.</summary>
		/// <param name="table">The table element.</param>
		private void AddTable( Node table )
		{
			AddCaption( table );
			var rows = CollectRows( table );
			if ( rows.Count == 0 )
				return;

			var hasHeader = rows[0].IsHead || rows[0].Cells.Count > 0 && rows[0].Cells.All( cell => cell.Tag == "th" );
			var columns = Math.Max( 1, rows.Max( row => row.Cells.Count ) );
			var builder = new StringBuilder();
			AppendTableRow( builder, hasHeader ? rows[0].Cells : [], columns );
			builder.Append( '|' );
			for ( var column = 0; column < columns; column++ )
				builder.Append( " --- |" );
			builder.Append( '\n' );
			foreach ( var row in rows.Skip( hasHeader ? 1 : 0 ) )
				AppendTableRow( builder, row.Cells, columns );
			AddMarkdown( builder.ToString().TrimEnd( '\n' ), false );
		}

		/// <summary>Adds a table's caption as a paragraph.</summary>
		/// <param name="table">The table element.</param>
		private void AddCaption( Node table )
		{
			if ( table.Children.FirstOrDefault( child => child.Tag == "caption" ) is { } caption )
				AddMarkdown( InlineMarkdown( caption ), false );
		}

		/// <summary>Appends one pipe-table row, padded to the column count.</summary>
		/// <param name="builder">The table being written.</param>
		/// <param name="cells">The row's cells.</param>
		/// <param name="columns">The column count.</param>
		private static void AppendTableRow( StringBuilder builder, List<Node> cells, int columns )
		{
			builder.Append( '|' );
			for ( var column = 0; column < columns; column++ )
			{
				var text = column < cells.Count ? InlineMarkdown( cells[column] ).Replace( "  \n", " " ).Replace( '\n', ' ' ) : "";
				builder.Append( ' ' ).Append( text ).Append( " |" );
			}

			builder.Append( '\n' );
		}

		/// <summary>Collects a table's rows, without descending into nested tables.</summary>
		/// <param name="table">The table element.</param>
		private static List<TableRow> CollectRows( Node table )
		{
			var rows = new List<TableRow>();
			CollectRows( table, rows, false );
			return rows;
		}

		/// <summary>Collects the rows inside a table or table section.</summary>
		/// <param name="node">The table or table section.</param>
		/// <param name="rows">The rows collected so far.</param>
		/// <param name="isHead">Whether the rows are inside <c>thead</c>.</param>
		private static void CollectRows( Node node, List<TableRow> rows, bool isHead )
		{
			foreach ( var child in node.Children )
			{
				switch ( child.Tag )
				{
					case "tr":
						rows.Add( new( [.. child.Children.Where( cell => cell.Tag is "td" or "th" )], isHead ) );
						break;
					case "thead":
						CollectRows( child, rows, true );
						break;
					case "tbody" or "tfoot" or "form":
						CollectRows( child, rows, isHead );
						break;
				}
			}
		}

		/// <summary>Adds a preformatted block as a fenced code block.</summary>
		/// <param name="node">The pre element.</param>
		private void AddPre( Node node )
		{
			var builder = new StringBuilder();
			AppendRawText( node, builder );
			var text = builder.ToString().Replace( "\r\n", "\n" );
			if ( text.StartsWith( '\n' ) )
				text = text[1..];
			text = text.TrimEnd();
			if ( text.Length == 0 )
				return;

			var fence = ComputeFence( text, 3 );
			Add( new FormMarkdown( $"{fence}\n{text}\n{fence}", false ) );
		}

		/// <summary>Appends an element's text without collapsing whitespace.</summary>
		/// <param name="node">The element.</param>
		/// <param name="builder">The text so far.</param>
		private static void AppendRawText( Node node, StringBuilder builder )
		{
			foreach ( var child in node.Children )
			{
				if ( child.IsText )
					builder.Append( child.Text );
				else if ( child.Tag == "br" )
					builder.Append( '\n' );
				else if ( !IgnoredTags.Contains( child.Tag ) && !IsControl( child ) )
					AppendRawText( child, builder );
			}
		}

		/// <summary>Renders a list without controls as Markdown list items, nesting inner lists.</summary>
		/// <param name="list">The ul or ol element.</param>
		/// <param name="indent">The indent of this list's markers.</param>
		private static string RenderList( Node list, int indent )
		{
			var builder = new StringBuilder();
			var isOrdered = list.Tag == "ol";
			var number = ParseCount( list.Get( "start" ) ) ?? 1;
			var padding = new string( ' ', indent );
			foreach ( var item in list.Children.Where( child => child.Tag == "li" ) )
			{
				var marker = isOrdered ? $"{number++}. " : "- ";
				var writer = new InlineWriter();
				var nested = new List<Node>();
				foreach ( var child in item.Children )
				{
					if ( child.Tag is "ul" or "ol" )
						nested.Add( child );
					else
						RenderInlineNode( child, writer );
				}

				var text = EscapeLineStarts( writer.Build() ).Replace( "\n", "\n" + new string( ' ', indent + marker.Length ) );
				builder.Append( padding ).Append( marker ).Append( text ).Append( '\n' );
				foreach ( var inner in nested )
				{
					var rendered = RenderList( inner, indent + marker.Length );
					if ( rendered.Length > 0 )
						builder.Append( rendered ).Append( '\n' );
				}
			}

			return builder.ToString().TrimEnd( '\n' );
		}

		/// <summary>Adds a control as a field, using a fallback label when it has none of its own.</summary>
		/// <param name="node">The input, select or textarea element.</param>
		/// <param name="fallback">The label to use when the control has no label, aria-label or title.</param>
		private void AddControl( Node node, LabelText? fallback )
		{
			switch ( node.Tag )
			{
				case "select":
					AddSelect( node, fallback );
					break;
				case "textarea":
					AddTextField( node, fallback, "textarea", true );
					break;
				case "input":
					var type = InputType( node );
					if ( type == "hidden" || IgnoredInputTypes.Contains( type ) )
						return;
					if ( type is "radio" or "checkbox" )
						AddOption( node, type, fallback );
					else
						AddTextField( node, fallback, type, false );
					break;
			}
		}

		/// <summary>Adds a radio or checkbox to the group of its type and name, starting the group at its first input.</summary>
		/// <param name="node">The input element.</param>
		/// <param name="type">The input type, <c>radio</c> or <c>checkbox</c>.</param>
		/// <param name="fallback">The label to use when the input has none of its own.</param>
		private void AddOption( Node node, string type, LabelText? fallback )
		{
			Flush();
			var name = node.Get( "name" ) ?? "";
			var key = type + "\n" + name;
			if ( !groups.TryGetValue( key, out var draft ) )
			{
				draft = new( name, "", type == "checkbox", false );
				groups.Add( key, draft );
				AddField( draft );
			}

			var label = ( ResolveLabel( node ) ?? fallback )?.Plain;
			if ( string.IsNullOrEmpty( label ) )
				label = node.Get( "value" ) ?? name;
			draft.Options.Add( new( node.Get( "value" ) ?? "on", label, node.Has( "checked" ), node.Has( "disabled" ) ) );
			draft.IsRequired |= node.Has( "required" );
		}

		/// <summary>Adds a select as a choice field.</summary>
		/// <param name="node">The select element.</param>
		/// <param name="fallback">The label to use when the select has none of its own.</param>
		private void AddSelect( Node node, LabelText? fallback )
		{
			Flush();
			var isMulti = node.Has( "multiple" );
			var draft = new ChoiceDraft( node.Get( "name" ) ?? "", ( ResolveLabel( node ) ?? fallback )?.Markdown ?? "", isMulti, true ) { IsRequired = node.Has( "required" ) };
			foreach ( var option in Descendants( node, "option" ) )
			{
				var text = PlainText( option );
				var value = option.Get( "value" ) ?? text;
				var label = text.Length > 0 ? text : Collapse( option.Get( "label" ) ) is { Length: > 0 } named ? named : value;
				draft.Options.Add( new( value, label, option.Has( "selected" ), option.Has( "disabled" ) ) );
			}

			if ( !isMulti && !draft.Options.Any( option => option.IsChecked ) )
			{
				var first = draft.Options.FindIndex( option => !option.IsDisabled );
				if ( first >= 0 )
					draft.Options[first] = draft.Options[first] with { IsChecked = true };
			}

			AddField( draft );
		}

		/// <summary>Adds a text input or textarea as a text field.</summary>
		/// <param name="node">The input or textarea element.</param>
		/// <param name="fallback">The label to use when the control has none of its own.</param>
		/// <param name="type">The input type, or <c>textarea</c>.</param>
		/// <param name="isMultiline">Whether the control is a textarea.</param>
		private void AddTextField( Node node, LabelText? fallback, string type, bool isMultiline )
		{
			Flush();
			var name = node.Get( "name" ) ?? "";
			var label = ( ResolveLabel( node ) ?? fallback )?.Markdown;
			if ( string.IsNullOrEmpty( label ) )
				label = EscapeText( name.Replace( '_', ' ' ).Replace( '-', ' ' ) );

			string value;
			int rows;
			if ( isMultiline )
			{
				var builder = new StringBuilder();
				foreach ( var child in node.Children.Where( child => child.IsText ) )
					builder.Append( child.Text );
				value = builder.ToString();
				if ( value.StartsWith( "\r\n", StringComparison.Ordinal ) )
					value = value[2..];
				else if ( value.StartsWith( '\n' ) )
					value = value[1..];
				rows = ParseCount( node.Get( "rows" ) ) is int count && count > 0 ? count : 4;
			}
			else
			{
				value = node.Get( "value" ) ?? "";
				rows = 1;
			}

			var pattern = node.Get( "pattern" );
			AddField( new FormTextField(
				name,
				label,
				node.Has( "required" ),
				type,
				isMultiline,
				value,
				node.Get( "placeholder" ) ?? "",
				ParseNumber( node.Get( "min" ) ),
				ParseNumber( node.Get( "max" ) ),
				string.IsNullOrEmpty( pattern ) ? null : pattern,
				ParseCount( node.Get( "minlength" ) ),
				ParseCount( node.Get( "maxlength" ) ),
				rows ) );
		}

		/// <summary>Adds a field in document order and shows it in the current section.</summary>
		/// <param name="field">A text field or a choice draft.</param>
		private void AddField( object field )
		{
			fields.Add( field );
			section.Blocks.Add( field is FormField finished ? new FormFieldBlock( finished ) : field );
		}

		/// <summary>Finds a control's own label: a wrapping label, a label naming its id, its aria-label, or its title.</summary>
		/// <param name="node">The control element.</param>
		private LabelText? ResolveLabel( Node node )
		{
			for ( var parent = node.Parent; parent is not null; parent = parent.Parent )
			{
				if ( parent.Tag != "label" )
					continue;
				if ( LabelOf( parent ) is { } wrapping )
					return wrapping;
				break;
			}

			if ( node.Get( "id" ) is { Length: > 0 } id && labelsById.TryGetValue( id, out var label ) && LabelOf( label ) is { } named )
				return named;
			return FromText( node.Get( "aria-label" ) ) ?? FromText( node.Get( "title" ) );
		}

		/// <summary>Reads an element's text, without its controls, as a label; null when it has no text.</summary>
		/// <param name="node">The label or cell element.</param>
		private static LabelText? LabelOf( Node node )
		{
			var plain = PlainText( node );
			return plain.Length == 0 ? null : new( InlineMarkdown( node ), plain );
		}

		/// <summary>Reads attribute or header text as a label; null when it is empty.</summary>
		/// <param name="text">The text, or null.</param>
		private static LabelText? FromText( string? text )
		{
			var plain = Collapse( text );
			return plain.Length == 0 ? null : new( EscapeText( plain ), plain );
		}

		/// <summary>Escapes plain text as Markdown.</summary>
		/// <param name="text">The text.</param>
		private static string EscapeText( string text )
		{
			var writer = new InlineWriter();
			writer.Text( text );
			return EscapeLineStarts( writer.Build() );
		}

		/// <summary>Renders an element's content as inline Markdown, skipping controls.</summary>
		/// <param name="node">The element.</param>
		private static string InlineMarkdown( Node node )
		{
			var writer = new InlineWriter();
			RenderInline( node, writer );
			return EscapeLineStarts( writer.Build() );
		}

		/// <summary>Renders an element's children inline.</summary>
		/// <param name="node">The element.</param>
		/// <param name="writer">The Markdown being written.</param>
		private static void RenderInline( Node node, InlineWriter writer )
		{
			foreach ( var child in node.Children )
				RenderInlineNode( child, writer );
		}

		/// <summary>Renders one node inline: text, line breaks and strong, emphasis and code spans; blocks become spaces.</summary>
		/// <param name="node">The node.</param>
		/// <param name="writer">The Markdown being written.</param>
		private static void RenderInlineNode( Node node, InlineWriter writer )
		{
			if ( node.IsText )
			{
				writer.Text( node.Text );
				return;
			}

			if ( IgnoredTags.Contains( node.Tag ) )
				return;

			switch ( node.Tag )
			{
				case "input" or "select" or "textarea" or "svg" or "img":
					return;
				case "br":
					writer.Break();
					return;
				case "b" or "strong":
					RenderWrapped( node, writer, "**" );
					return;
				case "em" or "i":
					RenderWrapped( node, writer, "*" );
					return;
				case "code" or "kbd" or "samp" or "tt":
					var code = PlainText( node );
					if ( code.Length > 0 )
					{
						var fence = ComputeFence( code, 1 );
						var pad = code.StartsWith( '`' ) || code.EndsWith( '`' ) ? " " : "";
						writer.Markdown( fence + pad + code + pad + fence );
					}

					return;
			}

			if ( IsBreaking( node.Tag ) )
			{
				writer.Space();
				RenderInline( node, writer );
				writer.Space();
			}
			else
				RenderInline( node, writer );
		}

		/// <summary>Renders an element's content between emphasis markers, keeping surrounding spaces outside them.</summary>
		/// <param name="node">The element.</param>
		/// <param name="writer">The Markdown being written.</param>
		/// <param name="marker">The marker, <c>**</c> or <c>*</c>.</param>
		private static void RenderWrapped( Node node, InlineWriter writer, string marker )
		{
			var inner = new InlineWriter();
			RenderInline( node, inner );
			var content = inner.Build();
			if ( inner.LeadingSpace )
				writer.Space();
			if ( content.Length > 0 )
				writer.Markdown( marker + content + marker );
			if ( inner.TrailingSpace )
				writer.Space();
		}

		/// <summary>Reads an element's text, without controls, with whitespace collapsed.</summary>
		/// <param name="node">The element.</param>
		private static string PlainText( Node node )
		{
			var builder = new StringBuilder();
			AppendPlainText( node, builder );
			return Collapse( builder.ToString() );
		}

		/// <summary>Appends an element's text, separating blocks and line breaks with spaces.</summary>
		/// <param name="node">The element.</param>
		/// <param name="builder">The text so far.</param>
		private static void AppendPlainText( Node node, StringBuilder builder )
		{
			foreach ( var child in node.Children )
			{
				if ( child.IsText )
					builder.Append( child.Text );
				else if ( child.Tag == "br" )
					builder.Append( ' ' );
				else if ( !IgnoredTags.Contains( child.Tag ) && child.Tag is not ( "input" or "select" or "textarea" or "svg" ) )
				{
					var isBreaking = IsBreaking( child.Tag );
					if ( isBreaking )
						builder.Append( ' ' );
					AppendPlainText( child, builder );
					if ( isBreaking )
						builder.Append( ' ' );
				}
			}
		}

		/// <summary>Whether an element separates the text around it.</summary>
		/// <param name="tag">The element name.</param>
		private static bool IsBreaking( string tag ) =>
			BlockTags.Contains( tag ) || HeadingTags.Contains( tag ) || NonInlineTags.Contains( tag ) || tag is "legend" or "tr" or "td" or "th" or "caption" or "option";

		/// <summary>Whether a styled span holds only inline content, so it can be written as one span.</summary>
		/// <param name="node">The element.</param>
		private static bool IsPlainInline( Node node ) =>
			node.Children.All( child => child.IsText || IgnoredTags.Contains( child.Tag ) || !IsControl( child ) && !IsBreaking( child.Tag ) && child.Tag is not ( "label" or "input" ) && IsPlainInline( child ) );

		/// <summary>Reads an SVG's alternative text: its title, else its aria-label.</summary>
		/// <param name="node">The svg element.</param>
		private static string ComputeSvgAlt( Node node )
		{
			var title = FindElement( node, "title" );
			var text = title is null ? "" : PlainText( title );
			return text.Length > 0 ? text : Collapse( node.Get( "aria-label" ) );
		}

		/// <summary>Adds a Markdown block when it is not empty.</summary>
		/// <param name="markdown">The Markdown.</param>
		/// <param name="isHeading">Whether the block is a heading.</param>
		private void AddMarkdown( string markdown, bool isHeading )
		{
			if ( markdown.Length > 0 )
				Add( new FormMarkdown( markdown, isHeading ) );
		}

		/// <summary>Adds a finished block to the current section.</summary>
		/// <param name="block">The block.</param>
		private void Add( FormBlock block ) => section.Blocks.Add( block );

		/// <summary>Ends the paragraph being written, adding it when it has text.</summary>
		private void Flush()
		{
			var text = paragraph.Build();
			var heading = isHeading;
			paragraph = new();
			isHeading = false;
			AddMarkdown( EscapeLineStarts( text ), heading );
		}

		/// <summary>Starts a new section.</summary>
		/// <param name="title">The section's title as Markdown, or empty.</param>
		/// <param name="isFieldset">Whether the section comes from a fieldset.</param>
		private void StartSection( string title, bool isFieldset )
		{
			section = new( title, isFieldset );
			sections.Add( section );
		}

		/// <summary>Whether an element is a field control: a select, a textarea, or an input that is not hidden or a button.</summary>
		/// <param name="node">The element.</param>
		private static bool IsControl( Node node ) => node.Tag switch
		{
			"select" or "textarea" => true,
			"input" => InputType( node ) is var type && type != "hidden" && !IgnoredInputTypes.Contains( type ),
			_ => false,
		};

		/// <summary>Reads an input's lowercase type, <c>text</c> when missing.</summary>
		/// <param name="node">The input element.</param>
		private static string InputType( Node node ) => ( node.Get( "type" )?.Trim().ToLowerInvariant() ) is { Length: > 0 } type ? type : "text";

		/// <summary>Whether an element holds a field control outside ignored elements.</summary>
		/// <param name="node">The element.</param>
		private static bool HasControls( Node node ) => Controls( node ).Any();

		/// <summary>Lists the field controls inside an element in document order, outside ignored elements.</summary>
		/// <param name="node">The element.</param>
		private static IEnumerable<Node> Controls( Node node )
		{
			foreach ( var child in node.Children )
			{
				if ( child.IsText || IgnoredTags.Contains( child.Tag ) )
					continue;
				if ( IsControl( child ) )
					yield return child;
				else
				{
					foreach ( var inner in Controls( child ) )
						yield return inner;
				}
			}
		}

		/// <summary>Lists the elements with a name inside an element in document order.</summary>
		/// <param name="node">The element.</param>
		/// <param name="tag">The element name.</param>
		private static IEnumerable<Node> Descendants( Node node, string tag )
		{
			foreach ( var child in node.Children )
			{
				if ( child.Tag == tag )
					yield return child;
				foreach ( var inner in Descendants( child, tag ) )
					yield return inner;
			}
		}
	}
}
