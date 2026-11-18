using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Names a built-in theme.</summary>
/// <param name="Name">The name stored in the theme setting.</param>
/// <param name="Theme">The theme's tokens.</param>
/// <param name="IsLight">Whether its panels are light, which groups it in the picker.</param>
internal sealed record ThemeEntry( string Name, Theme Theme, bool IsLight );

/// <summary>Holds Lokn's own theme and every Tern theme, converted to Lokn's tokens.</summary>
/// <remarks>
/// TernThemes.json is the theme catalog embedded in tern.exe, copied unchanged so it can be refreshed from a newer
/// Tern. Each theme names colors directly or through its "vars"; an empty value means the terminal default.
/// </remarks>
internal static class ThemeCatalog
{
	/// <summary>Defines the name of Lokn's built-in theme.</summary>
	internal const string DefaultName = "lokn";

	/// <summary>Defines how far the docking drop preview moves from the panel color toward the accent.</summary>
	private const float DropBlend = 0.4f;

	/// <summary>Defines the relative luminance above which a panel color counts as light.</summary>
	private const double LightLuminance = 0.5;

	/// <summary>Gets Lokn's theme first, then the light and the dark themes, each alphabetical.</summary>
	internal static IReadOnlyList<ThemeEntry> Entries { get; } = Load();

	/// <summary>Finds a theme by name, ignoring case.</summary>
	internal static ThemeEntry? Find( string name ) => Entries.FirstOrDefault( entry => string.Equals( entry.Name, name, StringComparison.OrdinalIgnoreCase ) );

	/// <summary>Reads and converts the embedded Tern catalog.</summary>
	private static IReadOnlyList<ThemeEntry> Load()
	{
		using var stream = typeof( ThemeCatalog ).Assembly.GetManifestResourceStream( "TernThemes.json" )
			?? throw new InvalidOperationException( "The Tern theme catalog is not embedded." );
		using var document = JsonDocument.Parse( stream );
		var themes = document.RootElement.GetProperty( "themes" ).EnumerateObject()
			.Select( property => Convert( property.Name, property.Value ) )
			.OrderBy( entry => !entry.IsLight )
			.ThenBy( entry => entry.Name, StringComparer.Ordinal );
		return [new ThemeEntry( DefaultName, Theme.Default, IsLight: false ), .. themes];
	}

	/// <summary>Maps one Tern theme onto Lokn's tokens: terminal chrome and background become the canvas and panels, its UI colors the rest.</summary>
	internal static ThemeEntry Convert( string name, JsonElement theme )
	{
		var vars = theme.TryGetProperty( "vars", out var values ) ? values : default;
		var colors = theme.GetProperty( "colors" );
		var terminal = theme.GetProperty( "terminal" );
		Color Read( JsonElement section, string key ) => Resolve( vars, section.GetProperty( key ).GetString() ?? "" )
			?? throw new JsonException( $"Theme {name} leaves {key} empty." );

		var panel = Read( terminal, "background" );
		var accent = Read( colors, "accent" );
		var selected = Read( colors, "selectedBg" );
		var line = Read( colors, "borderMuted" );
		var ansi = terminal.GetProperty( "ansi" ).EnumerateArray()
			.Select( color => Resolve( vars, color.GetString() ?? "" ) ?? throw new JsonException( $"Theme {name} leaves an ANSI color empty." ) )
			.ToArray();
		if ( ansi.Length != 16 )
			throw new JsonException( $"Theme {name} has {ansi.Length} ANSI colors instead of 16." );

		var converted = Theme.Default with
		{
			Canvas = Read( terminal, "chrome" ),
			Panel = panel,
			Raised = Read( terminal, "widget" ),
			Hover = selected,
			Text = Read( terminal, "foreground" ),
			Muted = Read( colors, "muted" ),
			Border = line,
			Accent = accent,
			Drop = Blend( panel, accent, DropBlend ),
			Attention = Read( colors, "warning" ),
			Success = Read( colors, "success" ),
			Error = Read( colors, "error" ),
			Divider = line,
			Inactive = Read( colors, "dim" ),
			CaptionHover = selected,
			AnsiBlack = ansi[0],
			AnsiRed = ansi[1],
			AnsiGreen = ansi[2],
			AnsiYellow = ansi[3],
			AnsiBlue = ansi[4],
			AnsiMagenta = ansi[5],
			AnsiCyan = ansi[6],
			AnsiWhite = ansi[7],
			AnsiBrightBlack = ansi[8],
			AnsiBrightRed = ansi[9],
			AnsiBrightGreen = ansi[10],
			AnsiBrightYellow = ansi[11],
			AnsiBrightBlue = ansi[12],
			AnsiBrightMagenta = ansi[13],
			AnsiBrightCyan = ansi[14],
			AnsiBrightWhite = ansi[15],
		};
		converted = EnsureLegible( converted );
		var luminance = 0.2126 * panel.R + 0.7152 * panel.G + 0.0722 * panel.B;
		return new( name, converted, luminance > LightLuminance );
	}

	/// <summary>Defines the contrast body text keeps on every surface: WCAG AA for normal text.</summary>
	private const double TextContrast = 4.5;

	/// <summary>Defines the contrast for colors that also draw text, such as links, inline code, and secondary lines.</summary>
	private const double ColoredTextContrast = 4.5;

	/// <summary>Defines the contrast for status marks and dots: WCAG's minimum for non-text graphics.</summary>
	private const double MarkContrast = 3;

	/// <summary>Raises each foreground color that some Tern themes leave too faint, keeping its hue, so text stays readable on every surface it draws on.</summary>
	/// <remarks>Terminal ANSI colors stay as the theme defines them; programs choose those.</remarks>
	internal static Theme EnsureLegible( Theme theme )
	{
		// Colored text draws on the canvas, panels and cards; only body text also draws on hovered rows, whose color may be translucent.
		Color[] surfaces = [theme.Canvas, theme.Panel, theme.Raised];
		var text = Raise( theme.Text, [.. surfaces, Composite( theme.Hover, theme.Panel )], TextContrast );
		// A hover so bright that no text color reads on it, such as a saturated selection blue, moves toward the panel instead.
		var hover = Composite( theme.Hover, theme.Panel );
		for ( var step = 1; step <= 20 && ComputeContrast( text, hover ) < TextContrast; step++ )
			hover = Blend( Composite( theme.Hover, theme.Panel ), theme.Panel, step / 20f );
		var isHoverDimmed = hover != Composite( theme.Hover, theme.Panel );
		return theme with
		{
			Text = text,
			Hover = isHoverDimmed ? hover : theme.Hover,
			CaptionHover = isHoverDimmed ? hover : theme.CaptionHover,
			Muted = Raise( theme.Muted, surfaces, ColoredTextContrast ),
			Accent = Raise( theme.Accent, surfaces, ColoredTextContrast ),
			Attention = Raise( theme.Attention, surfaces, ColoredTextContrast ),
			Success = Raise( theme.Success, surfaces, ColoredTextContrast ),
			Error = Raise( theme.Error, surfaces, ColoredTextContrast ),
			Inactive = Raise( theme.Inactive, surfaces, MarkContrast ),
		};
	}

	/// <summary>Moves a color toward white on dark surfaces, or black on light ones, in small steps until it reaches a contrast on all of them.</summary>
	private static Color Raise( Color color, Color[] surfaces, double target )
	{
		var average = surfaces.Average( ComputeLuminance );
		var toward = average < 0.18 ? Color.Rgb( 255, 255, 255 ) : Color.Rgb( 0, 0, 0 );
		var raised = color;
		for ( var step = 1; step <= 20 && surfaces.Min( surface => ComputeContrast( raised, surface ) ) < target; step++ )
			raised = Blend( color, toward, step / 20f );
		return raised;
	}

	/// <summary>Draws a translucent color over an opaque one, giving the color the eye sees.</summary>
	internal static Color Composite( Color top, Color under )
	{
		static int Channel( float top, float under, float alpha ) => (int)Math.Round( (top * alpha + under * (1 - alpha)) * 255 );

		return Color.Rgb( Channel( top.R, under.R, top.A ), Channel( top.G, under.G, top.A ), Channel( top.B, under.B, top.A ) );
	}

	/// <summary>Computes the WCAG contrast ratio of two colors, from 1 to 21.</summary>
	internal static double ComputeContrast( Color first, Color second )
	{
		var (light, dark) = (ComputeLuminance( first ), ComputeLuminance( second ));
		if ( light < dark )
			(light, dark) = (dark, light);
		return (light + 0.05) / (dark + 0.05);
	}

	/// <summary>Computes a color's WCAG relative luminance.</summary>
	private static double ComputeLuminance( Color color )
	{
		static double Linear( float channel ) => channel <= 0.03928 ? channel / 12.92 : Math.Pow( (channel + 0.055) / 1.055, 2.4 );

		return 0.2126 * Linear( color.R ) + 0.7152 * Linear( color.G ) + 0.0722 * Linear( color.B );
	}

	/// <summary>Resolves a color value, following variable names, or returns null for an empty value.</summary>
	private static Color? Resolve( JsonElement vars, string value )
	{
		// Variables may name other variables; the depth bound stops a cycle.
		for ( var depth = 0; depth < 8; depth++ )
		{
			if ( value.Length == 0 )
				return null;
			if ( value.StartsWith( '#' ) )
				return Color.TryParse( value ) ?? throw new JsonException( $"Invalid color {value}." );
			if ( vars.ValueKind != JsonValueKind.Object || !vars.TryGetProperty( value, out var next ) )
				throw new JsonException( $"Unknown theme variable {value}." );

			value = next.GetString() ?? "";
		}

		throw new JsonException( $"Theme variable {value} refers to itself." );
	}

	/// <summary>Moves one color toward another by an amount from 0 to 1, rounded to 8-bit channels so it survives a theme file round trip.</summary>
	private static Color Blend( Color from, Color to, float amount )
	{
		static int Channel( float from, float to, float amount ) => (int)Math.Round( (from + (to - from) * amount) * 255 );

		return Color.Rgb( Channel( from.R, to.R, amount ), Channel( from.G, to.G, amount ), Channel( from.B, to.B, amount ) );
	}
}
