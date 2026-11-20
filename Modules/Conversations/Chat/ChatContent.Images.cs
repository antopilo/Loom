using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Shows images inline as clickable thumbnails, keeps tool images in sight, and opens one full size over the chat.</summary>
/// <remarks>Inline images keep only reduced pixels, at most twice their shown height so they stay sharp on high-DPI screens; the full image is decoded
/// only while the viewer shows it, so a chat full of screenshots does not hold every one at full size.</remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines the maximum inline image height in logical pixels.</summary>
	private const int ImageHeight = 220;

	/// <summary>Defines the opacity of the panel color behind a full-size image.</summary>
	private const double ViewerBackdropOpacity = 0.94;

	/// <summary>Defines how many pixels tall an inline image keeps at most: twice its shown height, for high-DPI screens.</summary>
	private const int ThumbnailPixels = ImageHeight * 2;

	/// <summary>Defines how many pixels wide an inline image keeps at most.</summary>
	private const int ThumbnailMaxWidth = 1600;

	/// <summary>Retains each inline image's reduced pixels while its conversation panel remains open.</summary>
	private readonly Dictionary<string, ImageSource> _images = [];

	/// <summary>Retains each inline image's block, whose encoded data the viewer decodes full size.</summary>
	private readonly Dictionary<string, JsonElement> _imageBlocks = [];

	/// <summary>Stores the key of the image shown full size, or null when none is.</summary>
	private string? _viewing;

	/// <summary>Stores the full-size image the viewer shows, or null while it is closed.</summary>
	private ImageSource? _fullImage;

	/// <summary>Decodes an inline image once and builds it as a thumbnail that opens full size when clicked.</summary>
	/// <remarks>An image that arrives in a row already on screen, such as a tool's screenshot, fades in as it is decoded.</remarks>
	private Blob? BuildImage( string key, JsonElement block )
	{
		ImageSource source;
		var isNew = false;
		try
		{
			if ( !_images.TryGetValue( key, out source! ) )
			{
				var data = ReadText( block, "data" );
				if ( data.Length == 0 )
					return null;
				_images.Add( key, source = DecodeThumbnail( Convert.FromBase64String( data ) ) );
				_imageBlocks[key] = block;
				isNew = _isRowLive;
			}
		}
		// Goo rejects formats it cannot decode, such as WebP, with NotSupportedException.
		catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException or IOException )
		{
			return BuildText( "Image could not be decoded", muted: true );
		}

		// The thumbnail keeps the image's shape and never grows past its own pixels, so clicks land on the picture, not beside it.
		var height = Math.Min( ImageHeight, source.Height );
		var image = new Image
		{
			Source = source,
			Fit = ImageFit.Contain,
			Width = height * (double)source.Width / source.Height,
			AspectRatio = (double)source.Width / source.Height,
			MaxWidth = Length.Percent( 100 ),
		};
		var thumbnail = new Button
		{
			Key = "image:" + key,
			AlignSelf = AlignSelf.FlexStart,
			MaxWidth = Length.Percent( 100 ),
			Padding = 0,
			Focusable = true,
			Cursor = Cursor.Pointer,
			BorderRadius = Theme.Current.Radius,
			Overflow = Overflow.Hidden,
			AccessibilityName = "Open image",
			OnClick = () => context.Run( () => UpdateViewing( key ) ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, () => context.Run( () => UpdateViewing( key ) ) ),
				new( Key.Space, KeyModifiers.None, () => context.Run( () => UpdateViewing( key ) ) ),
			],
			Children = [image],
		};
		return Cell.Mount<RevealInput, Reveal>( "image:" + key, new( thumbnail, isNew ) );
	}

	/// <summary>Builds the images a tool returned, which stay visible whether or not its details are open.</summary>
	private List<Blob> BuildToolImages( string toolId, JsonElement? result )
	{
		var images = new List<Blob>();
		if ( result is not { ValueKind: JsonValueKind.Object } body || !body.TryGetProperty( "content", out var content ) || content.ValueKind != JsonValueKind.Array )
			return images;

		var index = 0;
		foreach ( var block in content.EnumerateArray().Where( block => ReadText( block, "type" ) == "image" ) )
		{
			// Keyed by the tool call, so the summary line and the opened tool share one decoded image.
			if ( BuildImage( $"{toolId}:image:{index++}", block ) is { } image )
				images.Add( image );
		}

		return images;
	}

	/// <summary>Builds the image being viewed full size over the whole chat, fading in as it opens; a click or Escape closes it.</summary>
	private Blob? BuildViewer()
	{
		if ( _viewing is not { } key || _fullImage is not { } source )
			return null;

		void Close() => context.Run( () => UpdateViewing( null ) );
		var viewer = new Button
		{
			Key = "image-viewer",
			Position = PositionType.Absolute,
			Left = 0,
			Top = 0,
			Right = 0,
			Bottom = 0,
			Padding = ContentPadding * 2,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Focusable = true,
			Cursor = Cursor.Pointer,
			BackgroundColor = Theme.Current.Panel.WithAlpha( ViewerBackdropOpacity ),
			AccessibilityName = "Close image",
			OnClick = Close,
			KeyBindings =
			[
				new( Key.Escape, KeyModifiers.None, Close ),
				new( Key.Enter, KeyModifiers.None, Close ),
			],
			Children =
			[
				new Image
				{
					Source = source,
					Fit = ImageFit.Contain,
					Width = Length.Percent( 100 ),
					Height = Length.Percent( 100 ),
					// A small image stays sharp at its own size rather than being blown up.
					MaxWidth = source.Width,
					MaxHeight = source.Height,
				},
			],
		};
		// The viewer mounts only while open, so each image opened enters once.
		return Cell.Mount<RevealInput, Reveal>( "image-viewer:" + key, new( viewer, true, Cover: true ) );
	}

	/// <summary>Shows an image full size, decoding it from its block, or closes the viewer when the key is null; the full image is released on close.</summary>
	private void UpdateViewing( string? key )
	{
		_fullImage?.Dispose();
		_fullImage = null;
		_viewing = null;
		if ( key is not null && _imageBlocks.TryGetValue( key, out var block ) )
		{
			try
			{
				_fullImage = ImageSource.Decode( Convert.FromBase64String( ReadText( block, "data" ) ) );
				_viewing = key;
			}
			catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException )
			{
				// The thumbnail decoded, so this only fails for an image too large to keep full size; it stays inline.
			}
		}
		MarkChanged();
	}

	/// <summary>Decodes an image's reduced pixels through a temporary file, which Goo's thumbnail decoder reads, and deletes the file.</summary>
	private static ImageSource DecodeThumbnail( byte[] encoded )
	{
		var folder = Path.Combine( Path.GetTempPath(), "Lokn", "images" );
		Directory.CreateDirectory( folder );
		var path = Path.Combine( folder, Guid.NewGuid().ToString( "N" ) );
		File.WriteAllBytes( path, encoded );
		try
		{
			return ImageSource.LoadThumbnail( path, ThumbnailMaxWidth, ThumbnailPixels );
		}
		finally
		{
			File.Delete( path );
		}
	}

	/// <summary>Releases every decoded image as the panel closes.</summary>
	private void ReleaseImages()
	{
		foreach ( var image in _images.Values )
			image.Dispose();
		_images.Clear();
		_imageBlocks.Clear();
		_fullImage?.Dispose();
		_fullImage = null;
	}
}
