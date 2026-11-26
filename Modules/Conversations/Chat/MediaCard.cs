using System.Buffers.Binary;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Builds an image in the transcript as a card, as Tern does: a header with its name, the scale it shows at and an expand button, over a
/// dark stage that holds the image centered.</summary>
internal static class MediaCard
{
	/// <summary>Defines the stage's height in logical pixels; the image fits inside it without growing past its own pixels.</summary>
	private const int StageHeight = 260;

	/// <summary>Defines the space around the image on the stage, in logical pixels.</summary>
	private const int StageInset = 12;

	/// <summary>Defines the header's height in logical pixels.</summary>
	private const int HeaderHeight = 34;

	/// <summary>Builds the card.</summary>
	/// <param name="key">The card's stable key.</param>
	/// <param name="name">The image's name, such as its file name.</param>
	/// <param name="source">The decoded thumbnail.</param>
	/// <param name="original">The image's own size in pixels, which the scale counts against.</param>
	/// <param name="open">Opens the image full size.</param>
	internal static Container Build( string key, string name, ImageSource source, (int Width, int Height) original, Action open )
	{
		var theme = Theme.Current;
		// The image never shows larger than its own pixels, and fits the stage's height.
		var height = Math.Min( StageHeight - 2 * StageInset, original.Height );
		var scale = original.Height > 0 ? (double)height / original.Height : 1;
		var percent = (int)Math.Round( scale * 100 );
		var expand = theme.BuildButton( "\uE740", open );
		expand.AccessibilityName = "Open image full size";
		expand.FontFamily = ViewStyle.IconFontFamily;
		expand.FontSize = ViewStyle.SmallSize + 1;
		expand.Color = theme.Muted;
		expand.Width = 26;
		expand.Height = 24;
		expand.Padding = 0;
		expand.FlexShrink = 0;

		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"Image {name}",
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			BackgroundColor = theme.Panel,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Children =
			[
				new Row
				{
					Key = "media-header",
					Height = HeaderHeight,
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing * 1.5 ),
					BorderColor = theme.Border,
					BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
					Children =
					[
						new Text
						{
							Content = name,
							FontFamily = ViewStyle.CodeFontFamily,
							FontSize = ViewStyle.SmallSize + 1,
							Color = theme.Text,
							FlexShrink = 1,
							MinWidth = 0,
							TextWrap = TextWrap.NoWrap,
							TextTrimming = TextTrimming.Ellipsis,
						},
						new Spacer(),
						new Text
						{
							Content = $"{percent}%",
							FontSize = ViewStyle.SmallSize,
							Color = theme.Muted,
							FlexShrink = 0,
							AccessibilityName = $"Shown at {percent}% of {original.Width} by {original.Height} pixels",
						},
						expand,
					],
				},
				new Button
				{
					Key = "media-stage",
					AccessibilityName = "Open image full size",
					Height = StageHeight,
					Padding = StageInset,
					AlignItems = AlignItems.Center,
					JustifyContent = JustifyContent.Center,
					BackgroundColor = theme.Canvas,
					Cursor = Cursor.Pointer,
					Focusable = true,
					OnClick = open,
					KeyBindings = [new( Key.Enter, KeyModifiers.None, open ), new( Key.Space, KeyModifiers.None, open )],
					Children =
					[
						new Image
						{
							Source = source,
							Fit = ImageFit.Contain,
							Height = height,
							AspectRatio = (double)source.Width / source.Height,
							MaxWidth = Length.Percent( 100 ),
							BorderRadius = theme.Radius / 2,
						},
					],
				},
			],
		};
	}

	/// <summary>Reads an image's size in pixels from its header: PNG, GIF, JPEG, WebP or BMP, or null for any other format.</summary>
	/// <param name="data">The encoded image.</param>
	internal static (int Width, int Height)? ReadSize( ReadOnlySpan<byte> data )
	{
		if ( data.Length >= 24 && data[..8].SequenceEqual( (ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] ) )
			return (BinaryPrimitives.ReadInt32BigEndian( data[16..] ), BinaryPrimitives.ReadInt32BigEndian( data[20..] ));
		if ( data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' )
			return (BinaryPrimitives.ReadUInt16LittleEndian( data[6..] ), BinaryPrimitives.ReadUInt16LittleEndian( data[8..] ));
		if ( data.Length >= 26 && data[0] == 'B' && data[1] == 'M' )
			return (BinaryPrimitives.ReadInt32LittleEndian( data[18..] ), Math.Abs( BinaryPrimitives.ReadInt32LittleEndian( data[22..] ) ));
		if ( data.Length >= 30 && data[..4].SequenceEqual( "RIFF"u8 ) && data[8..12].SequenceEqual( "WEBP"u8 ) )
		{
			var chunk = data[12..16];
			if ( chunk.SequenceEqual( "VP8X"u8 ) )
				return (1 + (data[24] | data[25] << 8 | data[26] << 16), 1 + (data[27] | data[28] << 8 | data[29] << 16));
			if ( chunk.SequenceEqual( "VP8L"u8 ) && data.Length >= 25 )
			{
				var bits = BinaryPrimitives.ReadUInt32LittleEndian( data[21..] );
				return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
			}
			if ( chunk.SequenceEqual( "VP8 "u8 ) )
				return (BinaryPrimitives.ReadUInt16LittleEndian( data[26..] ) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian( data[28..] ) & 0x3FFF);
			return null;
		}
		if ( data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 )
		{
			// JPEG: walk the markers to the first start-of-frame, which holds the size.
			var at = 2;
			while ( at + 9 < data.Length && data[at] == 0xFF )
			{
				var marker = data[at + 1];
				var length = BinaryPrimitives.ReadUInt16BigEndian( data[(at + 2)..] );
				if ( marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC )
					return (BinaryPrimitives.ReadUInt16BigEndian( data[(at + 7)..] ), BinaryPrimitives.ReadUInt16BigEndian( data[(at + 5)..] ));
				at += 2 + length;
			}
		}
		return null;
	}
}
