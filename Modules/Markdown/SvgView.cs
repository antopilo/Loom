using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Draws an <c>svg</c> code block as the vector drawing it describes, in a card with its kind and a Copy button, as Tern does.</summary>
/// <remarks>
/// Agents draw layouts and diagrams this way rather than in ASCII art. Goo 0.32.9 draws SVG shapes but not <c>&lt;text&gt;</c>, so the labels are
/// taken out of the source, Goo draws the rest, and each label is laid over it as a Goo text at its place. The drawing shows at its view box's
/// size, one unit to one logical pixel, and scrolls sideways where the chat is narrower. A block that cannot be read shows its source with a note.
/// </remarks>
internal static partial class SvgView
{
	/// <summary>Defines how many parsed drawings stay cached, so a rebuilt message does not parse its drawings again.</summary>
	private const int CacheSize = 32;

	/// <summary>Defines the font size a label without one uses, in SVG units: SVG's own default.</summary>
	private const double DefaultFontSize = 16;

	/// <summary>Defines how far above its baseline a label's box starts, as a share of its font size.</summary>
	private const double Ascent = 0.8;

	/// <summary>Defines the width given to a centered or end-anchored label's box, as a multiple of its font size per character.</summary>
	private const double AnchorWidthPerCharacter = 0.8;

	/// <summary>Describes one label of a drawing, in view-box units.</summary>
	/// <param name="X">The anchor's x.</param>
	/// <param name="Y">The baseline's y.</param>
	/// <param name="Content">The label's text.</param>
	/// <param name="Size">The font size.</param>
	/// <param name="Weight">The font weight.</param>
	/// <param name="Fill">The color, or null for the theme's text color.</param>
	/// <param name="Anchor">Where the anchor sits on the label: <c>start</c>, <c>middle</c> or <c>end</c>.</param>
	private readonly record struct Label( double X, double Y, string Content, double Size, int Weight, Color? Fill, string Anchor );

	/// <summary>Holds a parsed drawing: Goo's shapes, the labels laid over them, and the view box.</summary>
	/// <param name="Shapes">The shapes without labels, or null when there are none.</param>
	/// <param name="Labels">The labels.</param>
	/// <param name="X">The view box's left.</param>
	/// <param name="Y">The view box's top.</param>
	/// <param name="Width">The view box's width.</param>
	/// <param name="Height">The view box's height.</param>
	private sealed record Drawing( VectorAsset? Shapes, Label[] Labels, double X, double Y, double Width, double Height );

	/// <summary>Holds parsed drawings by their source, newest last; null marks a source that could not be read.</summary>
	private static readonly List<(string Source, Drawing? Drawing)> _cache = [];

	/// <summary>Guards the cache, which views on several panels share.</summary>
	private static readonly Lock _gate = new();

	/// <summary>Reports whether a code block is SVG: marked <c>svg</c>, or unmarked or marked as XML or HTML with an <c>&lt;svg</c> root.</summary>
	/// <param name="language">The block's language marker.</param>
	/// <param name="code">The block's text.</param>
	internal static bool IsSvg( string language, string code ) =>
		language.Equals( "svg", StringComparison.OrdinalIgnoreCase )
		|| (language is "" or "xml" or "html" && code.TrimStart().StartsWith( "<svg", StringComparison.OrdinalIgnoreCase ));

	/// <summary>Builds the drawing in a card, or null when it cannot be read.</summary>
	/// <param name="source">The SVG text.</param>
	/// <param name="header">The card's header row: its kind and its buttons.</param>
	/// <param name="inset">The space around the drawing inside the card, in logical pixels.</param>
	internal static Container? Build( string source, Row header, double inset )
	{
		if ( Parse( source ) is not { } drawing )
			return null;

		var theme = Theme.Current;
		var layers = new List<Blob>();
		if ( drawing.Shapes is { } shapes )
		{
			var art = shapes.Render( ShapeFit.Fill, "shapes" );
			art.Position = PositionType.Absolute;
			art.Left = 0;
			art.Top = 0;
			art.Width = drawing.Width;
			art.Height = drawing.Height;
			layers.Add( art );
		}
		foreach ( var (index, label) in drawing.Labels.Index() )
			layers.Add( BuildLabel( "label:" + index, label, drawing, theme ) );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Drawing",
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			BackgroundColor = theme.Panel,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Children =
			[
				header,
				// Wider than the chat, the drawing scrolls sideways rather than shrinking its labels out of place.
				new Row
				{
					MinWidth = 0,
					Padding = inset,
					JustifyContent = JustifyContent.Center,
					Overflow = Overflow.Scroll,
					Children = [new Container { Key = "drawing", Width = drawing.Width, Height = drawing.Height, FlexShrink = 0, Children = layers }],
				},
			],
		};
	}

	/// <summary>Builds one label as a text at its place in the drawing: its box starts at the anchor, ends at it, or centers on it.</summary>
	private static Text BuildLabel( string key, Label label, Drawing drawing, Theme theme )
	{
		var x = label.X - drawing.X;
		var width = Math.Max( label.Size, label.Content.Length * label.Size * AnchorWidthPerCharacter );
		var (left, align) = label.Anchor switch
		{
			"middle" => (x - width / 2, TextAlign.Center),
			"end" => (x - width, TextAlign.Right),
			_ => (x, TextAlign.Left),
		};
		return new Text
		{
			Key = key,
			Content = label.Content,
			Position = PositionType.Absolute,
			Left = left,
			Top = label.Y - drawing.Y - label.Size * Ascent,
			Width = label.Anchor is "middle" or "end" ? width : Length.Auto,
			TextAlign = align,
			TextWrap = TextWrap.NoWrap,
			FontFamily = ViewStyle.FontFamily,
			FontSize = label.Size,
			FontWeight = label.Weight,
			LineHeight = 1,
			Color = label.Fill ?? theme.Text,
		};
	}

	/// <summary>Parses SVG text once and keeps the result, or null when it cannot be read.</summary>
	private static Drawing? Parse( string source )
	{
		lock ( _gate )
		{
			var index = _cache.FindIndex( entry => entry.Source == source );
			if ( index >= 0 )
			{
				var found = _cache[index];
				_cache.RemoveAt( index );
				_cache.Add( found );
				return found.Drawing;
			}
		}

		var drawing = Read( source );
		lock ( _gate )
		{
			_cache.Add( (source, drawing) );
			if ( _cache.Count > CacheSize )
				_cache.RemoveAt( 0 );
		}
		return drawing;
	}

	/// <summary>Reads a drawing: its view box, its labels taken out of it, and the rest as Goo shapes; null when it is not SVG Goo can draw.</summary>
	private static Drawing? Read( string source )
	{
		try
		{
			var document = XDocument.Parse( source );
			if ( document.Root is not { } root || root.Name.LocalName != "svg" )
				return null;

			var (x, y, width, height) = ReadViewBox( root );
			var labels = new List<Label>();
			foreach ( var text in root.Descendants().Where( element => element.Name.LocalName == "text" ).ToArray() )
			{
				if ( ReadLabel( text ) is { } label )
					labels.Add( label );
				text.Remove();
			}

			var hasShapes = root.Descendants().Any( element => element.Name.LocalName is not ("g" or "defs" or "title" or "desc" or "style") );
			var shapes = hasShapes ? Goo.Svg.Svg.Parse( document.ToString( SaveOptions.DisableFormatting ) ) : null;
			return new Drawing( shapes, [.. labels], x, y, width, height );
		}
		catch ( Exception error ) when ( error is XmlException or Goo.Svg.SvgParseException or FormatException )
		{
			return null;
		}
	}

	/// <summary>Reads the drawing's view box, or its width and height when it has none.</summary>
	private static (double X, double Y, double Width, double Height) ReadViewBox( XElement root )
	{
		if ( root.Attribute( "viewBox" )?.Value.Split( [' ', ','], StringSplitOptions.RemoveEmptyEntries ) is [var x, var y, var w, var h] )
			return (ParseNumber( x ), ParseNumber( y ), ParseNumber( w ), ParseNumber( h ));
		return (0, 0, ReadLength( root, "width", 640 ), ReadLength( root, "height", 320 ));
	}

	/// <summary>Reads one label with the font, color and anchor it or its groups set, moved by its groups' translations.</summary>
	private static Label? ReadLabel( XElement text )
	{
		var content = string.Concat( text.DescendantNodes().OfType<XText>().Select( node => node.Value ) ).Trim();
		if ( content.Length == 0 )
			return null;

		double x = ReadLength( text, "x", 0 ), y = ReadLength( text, "y", 0 );
		// The first tspan's own place counts when the text sets none.
		if ( text.Element( text.Name.Namespace + "tspan" ) is { } span )
		{
			x = span.Attribute( "x" ) is not null ? ReadLength( span, "x", x ) : x;
			y = span.Attribute( "y" ) is not null ? ReadLength( span, "y", y ) : y;
		}
		foreach ( var group in text.Ancestors() )
		{
			if ( group.Attribute( "transform" )?.Value is { } transform && TranslatePattern().Match( transform ) is { Success: true } move )
			{
				x += ParseNumber( move.Groups["x"].Value );
				y += move.Groups["y"].Success ? ParseNumber( move.Groups["y"].Value ) : 0;
			}
		}

		var size = Inherited( text, "font-size" ) is { } fontSize ? ParseNumber( fontSize.Replace( "px", "" ) ) : DefaultFontSize;
		var weight = Inherited( text, "font-weight" ) switch
		{
			"bold" or "bolder" => 700,
			{ } value when int.TryParse( value, out var number ) => number,
			_ => 400,
		};
		var fill = Inherited( text, "fill" ) is { } color && color is not ("currentColor" or "none") ? Color.TryParse( color ) : null;
		return new Label( x, y, content, size, weight, fill, Inherited( text, "text-anchor" ) ?? "start" );
	}

	/// <summary>Gets an attribute from an element or its nearest group that sets it, or from a <c>style</c> declaration on them.</summary>
	private static string? Inherited( XElement element, string name )
	{
		foreach ( var node in element.AncestorsAndSelf() )
		{
			if ( node.Attribute( name )?.Value is { } value )
				return value.Trim();
			if ( node.Attribute( "style" )?.Value is { } style && Regex.Match( style, $@"(?:^|;)\s*{Regex.Escape( name )}\s*:\s*([^;]+)" ) is { Success: true } declared )
				return declared.Groups[1].Value.Trim();
		}
		return null;
	}

	/// <summary>Reads a length attribute in SVG units, or a fallback when it is missing or a percentage.</summary>
	private static double ReadLength( XElement element, string name, double fallback ) =>
		element.Attribute( name )?.Value is { } value && !value.EndsWith( '%' ) ? ParseNumber( value.Replace( "px", "" ) ) : fallback;

	/// <summary>Parses a number written the way SVG writes them.</summary>
	private static double ParseNumber( string value ) => double.Parse( value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture );

	/// <summary>Matches a <c>translate(x y)</c> or <c>translate(x, y)</c> transform.</summary>
	[GeneratedRegex( @"translate\(\s*(?<x>-?[\d.]+)(?:[\s,]+(?<y>-?[\d.]+))?\s*\)" )]
	private static partial Regex TranslatePattern();
}
