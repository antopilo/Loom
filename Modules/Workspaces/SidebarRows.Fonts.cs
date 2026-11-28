using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Holds the sidebar's heading face: Fraunces for group headings such as a project's name; every other line keeps the app's face.</summary>
/// <remarks>It ships with the module under the SIL Open Font License (<c>Fonts/OFL-Fraunces.txt</c>) and is registered once, from the module's resources.</remarks>
public sealed partial class SidebarRows
{
	/// <summary>Defines the registered family of the sidebar's heading face.</summary>
	private const string HeadingFamily = "Lokn Sidebar Heading";

	/// <summary>Defines how much larger a group heading is than the sidebar's text, in logical pixels.</summary>
	public const int HeadingSizeStep = 2;

	/// <summary>Retains the registered heading face for as long as the module is loaded: Fraunces's soft, large-title cut without its wonky letters, as one
	/// fixed instance of the variable font.</summary>
	private static readonly FontSource HeadingFont =
		Register( HeadingFamily, GroupWeight, false, "Fraunces.ttf", [new( "SOFT", 100 ), new( "WONK", 0 ), new( "opsz", 48 ), new( "wght", GroupWeight )] );

	/// <summary>Retains the italic display face, Fraunces at its largest optical size, for big editorial lines such as Home's greeting.</summary>
	private static readonly FontSource DisplayItalicFont =
		Register( "Lokn Display", 500, true, "FrauncesItalic.ttf", [new( "SOFT", 100 ), new( "WONK", 0 ), new( "opsz", 144 ), new( "wght", 500 )] );

	/// <summary>Retains the italic text face, Fraunces at a text optical size, for section titles such as Home's.</summary>
	private static readonly FontSource SerifItalicFont =
		Register( "Lokn Serif", 400, true, "FrauncesItalic.ttf", [new( "SOFT", 100 ), new( "WONK", 0 ), new( "opsz", 24 ), new( "wght", 400 )] );

	/// <summary>Gets the face of group headings, such as a project's name, with the app's face and its symbol and emoji fallbacks after it.</summary>
	public static string HeadingFontFamily => $"{HeadingFont.Family}, {ViewStyle.FontFamily}";

	/// <summary>Gets the italic display face, set with <see cref="FontStyle.Italic"/> and weight 500, with the app's face after it.</summary>
	public static string DisplayFontFamily => $"{DisplayItalicFont.Family}, {ViewStyle.FontFamily}";

	/// <summary>Gets the italic text face, set with <see cref="FontStyle.Italic"/> and weight 400, with the app's face after it.</summary>
	public static string SerifFontFamily => $"{SerifItalicFont.Family}, {ViewStyle.FontFamily}";

	/// <summary>Registers one face from the module's resources.</summary>
	private static FontSource Register( string family, int weight, bool italic, string file, FontVariation[] variations )
	{
		using var stream = typeof( SidebarRows ).Assembly.GetManifestResourceStream( $"Fonts.{file}" )
			?? throw new InvalidOperationException( $"The Workspaces module lacks its font {file}." );
		var bytes = new byte[stream.Length];
		stream.ReadExactly( bytes );
		var font = new FontSource( family, weight, italic, bytes, 0, variations );
		font.Register();
		return font;
	}
}
