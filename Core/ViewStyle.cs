using Goo;

namespace Lokn.Core;

/// <summary>Defines the fonts, small text, scrollbar and code-line limit the built-in views share, such as the chat, the Git panel, Markdown and the notification feed.</summary>
/// <remarks>Goo draws only registered fonts, so the faces are registered once here for every module that uses them. The fonts, small size and code-line
/// cutting are public for modules that keep to the public API, such as Markdown; the scrollbar and icon face are for built-in modules.</remarks>
public static class ViewStyle
{
	/// <summary>Defines metadata text size in logical pixels.</summary>
	public const int SmallSize = 12;

	/// <summary>Defines the scrollbar track width in logical pixels.</summary>
	internal const int TrackWidth = 10;

	/// <summary>Defines the longest code line rendered, in UTF-16 code units. Shaping and wrapping one line of tens of thousands of characters, such as an embedded file, stalls layout for minutes.</summary>
	internal const int MaxCodeLineLength = 2000;

	/// <summary>Defines the scrollbar thumb width at rest in logical pixels; it widens to the track on hover.</summary>
	private const int ThumbWidth = 6;

	/// <summary>Defines regular font weight on the OpenType scale.</summary>
	private const int RegularWeight = 400;

	/// <summary>Defines the registered semibold font weight on the OpenType scale.</summary>
	private const int SemiboldWeight = 600;

	/// <summary>Defines the registered bold font weight on the OpenType scale.</summary>
	private const int BoldWeight = 700;

	/// <summary>Retains the UI faces registered off Windows in regular, semibold and bold, upright and italic; Windows uses its installed Segoe UI family.</summary>
	private static readonly FontSource[] UiFonts = OperatingSystem.IsWindows() ? [] :
	[
		BuildFont( "Lokn UI", "segoeui.ttf", RegularWeight, italic: false ),
		BuildFont( "Lokn UI", "seguisb.ttf", SemiboldWeight, italic: false ),
		BuildFont( "Lokn UI", "seguisbi.ttf", SemiboldWeight, italic: true ),
		BuildFont( "Lokn UI", "segoeuib.ttf", BoldWeight, italic: false ),
		BuildFont( "Lokn UI", "segoeuii.ttf", RegularWeight, italic: true ),
		BuildFont( "Lokn UI", "segoeuiz.ttf", BoldWeight, italic: true ),
	];

	/// <summary>Retains the registered monospace face used by code and inline code.</summary>
	private static readonly FontSource CodeFont = BuildFont( "Lokn Code", "consola.ttf", RegularWeight, italic: false );

	/// <summary>Retains the registered bold monospace face for headings and emphasis in code.</summary>
	private static readonly FontSource CodeBoldFont = BuildFont( "Lokn Code", "consolab.ttf", BoldWeight, italic: false );

	/// <summary>Retains the registered italic monospace face for emphasis in code.</summary>
	private static readonly FontSource CodeItalicFont = BuildFont( "Lokn Code", "consolai.ttf", RegularWeight, italic: true );

	/// <summary>Retains the registered bold italic monospace face for combined emphasis.</summary>
	private static readonly FontSource CodeBoldItalicFont = BuildFont( "Lokn Code", "consolaz.ttf", BoldWeight, italic: true );

	/// <summary>Retains the registered symbol face used by checklist and folding markers.</summary>
	private static readonly FontSource SymbolFont = BuildFont( "Lokn Symbols", "seguisym.ttf", RegularWeight, italic: false );

	/// <summary>Retains the installed emoji face used by UTF-8 text.</summary>
	private static readonly FontSource EmojiFont = BuildFont( "Lokn Chat Emoji", "seguiemj.ttf", RegularWeight, italic: false );

	/// <summary>Retains the installed icon face used by icon buttons.</summary>
	private static readonly FontSource IconFont = BuildFont( "Lokn Icons", "SegoeIcons.ttf", RegularWeight, italic: false );

	/// <summary>Gets the app's UI face with registered symbol and emoji fallback families.</summary>
	public static string FontFamily => $"{(OperatingSystem.IsWindows() ? "Segoe UI" : UiFonts[0].Family)}, {SymbolFont.Family}, {EmojiFont.Family}";

	/// <summary>Gets registered monospace and Unicode fallback families for code and commands, in every weight and slant.</summary>
	public static string CodeFontFamily => $"{CodeFont.Family}, {SymbolFont.Family}, {EmojiFont.Family}";

	/// <summary>Gets the registered symbol face's family, for markers that must not fall back to an unregistered font.</summary>
	public static string SymbolFontFamily => SymbolFont.Family;

	/// <summary>Gets the installed Segoe Fluent Icons family, for the glyphs of icon buttons.</summary>
	internal static string IconFontFamily => IconFont.Family;

	/// <summary>Registers an installed font file, named as in the Windows Fonts folder, under a family name.</summary>
	private static FontSource BuildFont( string family, string filename, int weight, bool italic ) => SystemFonts.Register( family, weight, italic, filename );

	/// <summary>Builds a visible vertical scrollbar in the current theme; call it per build because Goo owns a mounted descriptor.</summary>
	internal static Scrollbar BuildScrollbar()
	{
		var theme = Theme.Current;
		return new Scrollbar
		{
			Thickness = TrackWidth,
			HitThickness = TrackWidth + 4,
			Thumb = new Container
			{
				Width = ThumbWidth,
				BorderRadius = ThumbWidth / 2.0,
				BackgroundColor = theme.Muted,
				Opacity = 0.5,
				HoverStyle = new Style
				{
					Width = TrackWidth,
					BorderRadius = TrackWidth / 2.0,
					Opacity = 0.8,
				},
			},
		};
	}

	/// <summary>Cuts a line that is too long to lay out and says how much was left out.</summary>
	public static string CutCodeLine( string line )
	{
		var length = ComputeCodeLineLength( line );
		return length == line.Length ? line : $"{line[..length]} … {line.Length - length:N0} more characters not shown";
	}

	/// <summary>Computes how much of a line is laid out: all of it, or the most that fits the limit without splitting a surrogate pair.</summary>
	public static int ComputeCodeLineLength( string line )
	{
		if ( line.Length <= MaxCodeLineLength )
			return line.Length;

		// Cutting between the halves of a surrogate pair would leave an unpaired, unrenderable code unit.
		return char.IsHighSurrogate( line[MaxCodeLineLength - 1] ) ? MaxCodeLineLength - 1 : MaxCodeLineLength;
	}
}
