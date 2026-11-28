using Goo;
using Lokn.Core;

namespace Lokn.Modules.Overview;

/// <summary>Holds the look of a project's brief: its serif faces, and the paper and ink it is printed in; the day's painting is <see cref="DailyPainting"/>.</summary>
/// <remarks>The faces are Fraunces, roman and italic, under the SIL Open Font License (<c>Fonts\OFL-Fraunces.txt</c>), set soft and without its wonky
/// letters as in the sidebar and in Loom's report kit.</remarks>
internal static class BriefArt
{
	/// <summary>Defines the registered family of the hero title's face: Fraunces at its largest optical size.</summary>
	private const string DisplayFamily = "Lokn Brief Display";

	/// <summary>Defines the registered family of the page's other serif lines: Fraunces at a text optical size.</summary>
	private const string SerifFamily = "Lokn Brief Serif";

	/// <summary>Retains the registered faces for as long as the module is loaded: the display face upright and italic, the text face upright and italic.</summary>
	private static readonly FontSource[] Fonts =
	[
		Register( DisplayFamily, 500, false, "Fraunces.ttf", 144 ),
		Register( DisplayFamily, 500, true, "FrauncesItalic.ttf", 144 ),
		Register( SerifFamily, 400, false, "Fraunces.ttf", 24 ),
		Register( SerifFamily, 400, true, "FrauncesItalic.ttf", 24 ),
	];

	/// <summary>Gets the hero title's face, weight 500, with the app's face and its symbol and emoji fallbacks after it.</summary>
	public static string DisplayFontFamily => $"{Fonts[0].Family}, {ViewStyle.FontFamily}";

	/// <summary>Gets the face of section titles, the summary and the margins, weight 400, with the app's face after it.</summary>
	public static string SerifFontFamily => $"{Fonts[2].Family}, {ViewStyle.FontFamily}";

	/// <summary>Registers one fixed instance of a Fraunces face from the module's resources.</summary>
	private static FontSource Register( string family, int weight, bool italic, string file, float opticalSize )
	{
		using var stream = typeof( BriefArt ).Assembly.GetManifestResourceStream( $"Fonts.{file}" )
			?? throw new InvalidOperationException( $"The Overview module lacks its font {file}." );
		var bytes = new byte[stream.Length];
		stream.ReadExactly( bytes );
		var font = new FontSource( family, weight, italic, bytes, 0, [new( "SOFT", 100 ), new( "WONK", 0 ), new( "opsz", opticalSize ), new( "wght", weight )] );
		font.Register();
		return font;
	}
}

/// <summary>Holds the paper and ink of the brief, the tokens of Loom's report kit (<c>Modules\Browser\Reports\loom-report.css</c>): its dark "ink" paper
/// under a dark theme, its white "paper" under a light one.</summary>
/// <param name="Paper">The page.</param>
/// <param name="Ink">Titles and main text.</param>
/// <param name="Ink2">Secondary text.</param>
/// <param name="Ink3">Faint text: dates, credits.</param>
/// <param name="Rule">Hairlines and outlines.</param>
/// <param name="Card">The tint of a section's card on the page.</param>
/// <param name="Hover">The fill of a row under the pointer.</param>
/// <param name="Accent">The theme's accent, for the few marks that stand out, such as the sticker.</param>
/// <param name="AccentInk">Text on <paramref name="Accent"/>: the darker or lighter of the paper and the ink, whichever reads better on it.</param>
internal sealed record BriefPaper( Color Paper, Color Ink, Color Ink2, Color Ink3, Color Rule, Color Card, Color Hover, Color Accent, Color AccentInk )
{
	/// <summary>Gets the paper of the applied theme: its canvas as the page, its text, muted and inactive colors as the three inks, and its accent;
	/// hairlines, cards and hover derive from the ink. Building views read it, so they follow theme changes.</summary>
	public static BriefPaper Current
	{
		get
		{
			var theme = Theme.Current;
			var ink = theme.Text;
			var accentInk = Luminance( theme.Accent ) > 0.5 == Luminance( theme.Canvas ) > 0.5 ? ink : theme.Canvas;
			return new( theme.Canvas, ink, theme.Muted, theme.Inactive, ink.WithAlpha( 0.22 ), ink.WithAlpha( 0.045 ), ink.WithAlpha( 0.08 ), theme.Accent, accentInk );
		}
	}

	/// <summary>Gets a color's relative brightness, 0 for black to 1 for white.</summary>
	private static double Luminance( Color color ) => 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
}
