using System.Text;

namespace Lokn.Core;

/// <summary>Keeps emoji that Goo cannot draw out of text, since drawing one throws and ends the process.</summary>
/// <remarks>
/// Goo encodes a color glyph's paint into a fixed 256 KB text workspace, and an emoji whose paint is larger throws
/// <c>Vulkan text color glyph exceeds workspace capacity</c> from the render pass, which nothing can catch. Segoe UI Emoji's paint is
/// heavy for some glyphs, such as the horse, the knot and the family emoji. <see cref="Guard"/> replaces those before the text reaches a
/// <c>Text</c>. The set is measured, not probed in a window: <see cref="Blocked"/> holds every glyph of the font whose COLR paint graph has
/// more than <see cref="MaximumPaintNodes"/> nodes, a limit between the largest glyph known to draw (273) and the smallest known to crash (457).
/// </remarks>
public static class EmojiGuard
{
	/// <summary>Defines the paint graph size, in COLR paint nodes, above which an emoji is replaced; Goo draws 273 nodes and fails at 457.</summary>
	private const int MaximumPaintNodes = 300;

	/// <summary>Defines the first code point of the Fitzpatrick skin tone modifiers.</summary>
	private const int FirstSkinTone = 0x1F3FB;

	/// <summary>Defines the last code point of the Fitzpatrick skin tone modifiers.</summary>
	private const int LastSkinTone = 0x1F3FF;

	/// <summary>Defines the zero width joiner that binds several emoji into one glyph.</summary>
	private const int Joiner = 0x200D;

	/// <summary>Defines the empty box drawn in place of a blocked emoji.</summary>
	private const string Placeholder = "\u25A1";

	/// <summary>Defines the first UTF-16 unit that can start a blocked emoji or a joined sequence; text below it is returned as is.</summary>
	private const char FirstRelevantUnit = '\u200D';

	/// <summary>Lists, in ascending order, the code points of Segoe UI Emoji whose paint graph has more than <see cref="MaximumPaintNodes"/> nodes.</summary>
	private static readonly int[] Blocked =
	[
		0x26F9, 0x1F33E, 0x1F37F, 0x1F3A1, 0x1F3D3, 0x1F3E8,
		0x1F3E9, 0x1F3F5, 0x1F40A, 0x1F40E, 0x1F40F, 0x1F429,
		0x1F46A, 0x1F46B, 0x1F46C, 0x1F46D, 0x1F46F, 0x1F470,
		0x1F478, 0x1F48F, 0x1F490, 0x1F491, 0x1F492, 0x1F4BB,
		0x1F4E0, 0x1F574, 0x1F575, 0x1F587, 0x1F5A5, 0x1F5C4,
		0x1F5D3, 0x1F5FD, 0x1F934, 0x1F936, 0x1F939, 0x1F93C,
		0x1F96F, 0x1F982, 0x1F987, 0x1F994, 0x1F998, 0x1F9A0,
		0x1F9A7, 0x1F9B1, 0x1F9DD, 0x1F9DE, 0x1F9DF, 0x1F9EE,
		0x1FA8E, 0x1FA97, 0x1FAA2, 0x1FABB, 0x1FAC5, 0x1FAC8,
	];

	/// <summary>Replaces the emoji Goo cannot draw in a text, and returns the same instance when there is nothing to replace.</summary>
	/// <param name="text">The text about to be drawn.</param>
	/// <remarks>
	/// A joined sequence, such as a family or a person with a profession, draws as one glyph that is usually too large, and so does a skin
	/// toned person. Both are drawn as their parts instead: the joiners and skin tones are removed, and each part is checked on its own.
	/// </remarks>
	public static string Guard( string text )
	{
		if ( text.AsSpan().IndexOfAnyExceptInRange( '\0', (char)( FirstRelevantUnit - 1 ) ) < 0 )
			return text;

		var guarded = new StringBuilder( text.Length );
		var isChanged = false;
		foreach ( var rune in text.EnumerateRunes() )
		{
			if ( rune.Value is Joiner or >= FirstSkinTone and <= LastSkinTone )
			{
				isChanged = true;
				continue;
			}

			if ( Array.BinarySearch( Blocked, rune.Value ) >= 0 )
			{
				isChanged = true;
				guarded.Append( Placeholder );
				continue;
			}

			guarded.Append( rune.ToString() );
		}

		return isChanged ? guarded.ToString() : text;
	}
}
