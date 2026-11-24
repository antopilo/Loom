using System.Runtime.InteropServices;
using Goo;

namespace Lokn.Core;

/// <summary>Registers installed fonts under Loom's family names on every platform.</summary>
/// <remarks>Windows loads the named file from its Fonts folder. Other platforms load the file fontconfig matches for the same Windows family,
/// with a generic fallback, so the original font is used when it is installed and the user's configured font otherwise.</remarks>
public static partial class SystemFonts
{
	/// <summary>Names the fontconfig library that matches installed fonts off Windows.</summary>
	private const string Fontconfig = "libfontconfig.so.1";

	/// <summary>Maps each Windows font file to the fontconfig families matched in its place.</summary>
	private static readonly Dictionary<string, string> Families = new( StringComparer.OrdinalIgnoreCase )
	{
		["segoeui.ttf"] = "Segoe UI,sans-serif",
		["seguisb.ttf"] = "Segoe UI,sans-serif",
		["segoeuib.ttf"] = "Segoe UI,sans-serif",
		["segoeuii.ttf"] = "Segoe UI,sans-serif",
		["seguisbi.ttf"] = "Segoe UI,sans-serif",
		["segoeuiz.ttf"] = "Segoe UI,sans-serif",
		["seguisym.ttf"] = "Segoe UI Symbol,sans-serif",
		["seguiemj.ttf"] = "Segoe UI Emoji,emoji",
		["SegoeIcons.ttf"] = "Segoe Fluent Icons",
		["consola.ttf"] = "Consolas,monospace",
		["consolab.ttf"] = "Consolas,monospace",
		["consolai.ttf"] = "Consolas,monospace",
		["consolaz.ttf"] = "Consolas,monospace",
	};

	/// <summary>Registers an installed font face under a family name.</summary>
	/// <param name="family">The family name views use.</param>
	/// <param name="weight">The weight on the OpenType scale.</param>
	/// <param name="italic">Whether the face is italic.</param>
	/// <param name="windowsFile">The file name of the face in the Windows Fonts folder. Other systems support only segoeui.ttf, seguisb.ttf, segoeuib.ttf,
	/// segoeuii.ttf, seguisbi.ttf, segoeuiz.ttf, seguisym.ttf, seguiemj.ttf, SegoeIcons.ttf, consola.ttf, consolab.ttf, consolai.ttf and consolaz.ttf.</param>
	/// <returns>The registered font source.</returns>
	public static FontSource Register( string family, int weight, bool italic, string windowsFile )
	{
		var font = OperatingSystem.IsWindows()
			? FontSource.FromPath( family, weight, italic, Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Fonts ), windowsFile ) )
			: Match( family, weight, italic, Families[windowsFile] );
		font.Register();
		return font;
	}

	/// <summary>Loads the face fontconfig matches for a family list, weight and slant.</summary>
	private static FontSource Match( string family, int weight, bool italic, string families )
	{
		var pattern = FcNameParse( $"{families}:weight={FcWeightFromOpenType( weight )}:slant={( italic ? 100 : 0 )}" );
		FcConfigSubstitute( 0, pattern, 0 );
		FcDefaultSubstitute( pattern );
		var match = FcFontMatch( 0, pattern, out _ );
		FcPatternDestroy( pattern );
		if ( match == 0 )
			throw new FileNotFoundException( $"fontconfig matched no font for {families}." );
		try
		{
			FcPatternGetString( match, "file", 0, out var file );
			FcPatternGetInteger( match, "index", 0, out var index );
			// The upper 16 bits of a fontconfig index select a named instance of a variable font, drawn here at its weight; the face index is the lower 16.
			return FontSource.FromPath( family, weight, italic, Marshal.PtrToStringUTF8( file )!, (uint)index & 0xFFFF, index >> 16 == 0 ? [] : [new( "wght", weight )] );
		}
		finally
		{
			FcPatternDestroy( match );
		}
	}

	/// <summary>Parses a fontconfig pattern string.</summary>
	[LibraryImport( Fontconfig, StringMarshalling = StringMarshalling.Utf8 )]
	private static partial nint FcNameParse( string name );

	/// <summary>Applies the user's fontconfig substitutions to a pattern.</summary>
	[LibraryImport( Fontconfig )]
	private static partial int FcConfigSubstitute( nint config, nint pattern, int kind );

	/// <summary>Fills unset pattern values with fontconfig defaults.</summary>
	[LibraryImport( Fontconfig )]
	private static partial void FcDefaultSubstitute( nint pattern );

	/// <summary>Finds the installed font that best matches a pattern.</summary>
	[LibraryImport( Fontconfig )]
	private static partial nint FcFontMatch( nint config, nint pattern, out int result );

	/// <summary>Reads a string value from a pattern.</summary>
	[LibraryImport( Fontconfig, StringMarshalling = StringMarshalling.Utf8 )]
	private static partial int FcPatternGetString( nint pattern, string name, int index, out nint value );

	/// <summary>Reads an integer value from a pattern.</summary>
	[LibraryImport( Fontconfig, StringMarshalling = StringMarshalling.Utf8 )]
	private static partial int FcPatternGetInteger( nint pattern, string name, int index, out int value );

	/// <summary>Frees a pattern.</summary>
	[LibraryImport( Fontconfig )]
	private static partial void FcPatternDestroy( nint pattern );

	/// <summary>Converts an OpenType weight to the fontconfig weight scale.</summary>
	[LibraryImport( Fontconfig )]
	private static partial int FcWeightFromOpenType( int weight );
}
