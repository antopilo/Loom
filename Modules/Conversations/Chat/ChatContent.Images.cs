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

	/// <summary>Retains each inline image's reduced pixels while its conversation panel remains open; null marks an image that could not be decoded.</summary>
	private readonly Dictionary<string, ImageSource?> _images = [];

	/// <summary>Maps each inline image still decoding to the messages whose rows show it, so each one is rebuilt when it arrives.</summary>
	private readonly Dictionary<string, HashSet<Guid>> _imageLoads = new( StringComparer.Ordinal );

	/// <summary>Holds the inline images decoded since a row last showed them, which fade in once.</summary>
	private readonly HashSet<string> _loadedImages = new( StringComparer.Ordinal );

	/// <summary>Retains each inline image's block, whose encoded data the viewer decodes full size.</summary>
	private readonly Dictionary<string, JsonElement> _imageBlocks = [];

	/// <summary>Stores the key of the image shown full size, or null when none is.</summary>
	private string? _viewing;

	/// <summary>Stores the full-size image the viewer shows, or null while it is closed.</summary>
	private ImageSource? _fullImage;

	/// <summary>Holds each decoded image's own size in pixels, read from its header, so its card names the scale it shows at.</summary>
	private readonly Dictionary<string, (int Width, int Height)> _imageSizes = new( StringComparer.Ordinal );

	/// <summary>Builds an inline image as a thumbnail that opens full size when clicked, or null while it decodes off the UI thread.</summary>
	/// <remarks>The thumbnail fades in once when its decode arrives.</remarks>
	/// <param name="key">The image's stable key.</param>
	/// <param name="messageId">The message whose row shows the image, rebuilt when its decode arrives.</param>
	/// <param name="block">The image block with its encoded data.</param>
	private Blob? BuildImage( string key, Guid messageId, JsonElement block )
	{
		if ( !_images.TryGetValue( key, out var source ) )
		{
			var data = ReadText( block, "data" );
			if ( data.Length > 0 )
			{
				_imageBlocks[key] = block;
				UpdateImageLoad( key, messageId, data );
			}
			return null;
		}
		if ( source is null )
			return BuildText( "Image could not be decoded", muted: true );
		var isNew = _loadedImages.Remove( key );
		// The image sits in a card like Tern's: its name, the scale it shows at and an expand button over a dark stage; clicking opens it full size.
		var original = _imageSizes.TryGetValue( key, out var known ) ? known : (source.Width, source.Height);
		var card = MediaCard.Build( "image:" + key, ComputeImageName( block ), source, original, () => context.Run( () => UpdateViewing( key ) ) );
		return Cell.Mount<RevealInput, Reveal>( "image:" + key, new( card, isNew ) );
	}

	/// <summary>Gets the name an image shows under: its own file name when the block has one, else "image" with its format.</summary>
	private static string ComputeImageName( JsonElement block )
	{
		foreach ( var property in new[] { "name", "fileName", "filename", "path" } )
		{
			if ( ReadText( block, property ) is { Length: > 0 } name )
				return Path.GetFileName( name );
		}
		return ReadText( block, "mimeType" ) is { Length: > 0 } mime && mime.StartsWith( "image/", StringComparison.Ordinal )
			? "image." + mime[6..].Replace( "jpeg", "jpg" ).Replace( "svg+xml", "svg" )
			: "image";
	}

	/// <summary>Decodes an inline image's thumbnail off the UI thread, once per key, and rebuilds every row waiting for it.</summary>
	private void UpdateImageLoad( string key, Guid messageId, string data )
	{
		if ( _imageLoads.TryGetValue( key, out var waiting ) )
		{
			waiting.Add( messageId );
			return;
		}

		_imageLoads[key] = [messageId];
		_ = Task.Run( () =>
		{
			ImageSource? source = null;
			(int Width, int Height)? size = null;
			try
			{
				var bytes = Convert.FromBase64String( data );
				size = MediaCard.ReadSize( bytes );
				source = DecodeThumbnail( bytes );
			}
			catch ( Exception )
			{
				// Goo rejects data it cannot decode; the row says the image could not be decoded.
			}

			var isPosted = context.Post( () =>
			{
				if ( !_imageLoads.Remove( key, out var messages ) )
				{
					source?.Dispose();
					return;
				}

				_images[key] = source;
				if ( size is { } known )
					_imageSizes[key] = known;
				if ( source is not null )
					_loadedImages.Add( key );
				foreach ( var message in messages )
					MarkRowChanged( message );
			} );
			if ( !isPosted )
				source?.Dispose();
		} );
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
			if ( BuildImage( $"{toolId}:image:{index++}", _toolOwners.GetValueOrDefault( toolId ), block ) is { } image )
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

	/// <summary>Opens an image full size once its decode off the UI thread arrives, or closes the viewer when the key is null; the full image is released on close.</summary>
	private void UpdateViewing( string? key )
	{
		_fullImage?.Dispose();
		_fullImage = null;
		_viewing = null;
		if ( key is not null && _imageBlocks.TryGetValue( key, out var block ) )
		{
			_viewing = key;
			var data = ReadText( block, "data" );
			_ = Task.Run( () =>
			{
				ImageSource? source = null;
				try
				{
					source = ImageSource.Decode( Convert.FromBase64String( data ) );
				}
				catch ( Exception )
				{
					// The thumbnail decoded, so this only fails for an image too large to keep full size; it stays inline.
				}

				if ( !context.Post( () => UpdateFullImage( key, source ) ) )
					source?.Dispose();
			} );
		}
		MarkChanged();
	}

	/// <summary>Shows a decoded full-size image while the viewer still waits for it, and releases it otherwise.</summary>
	private void UpdateFullImage( string key, ImageSource? source )
	{
		if ( _viewing != key || _fullImage is not null )
		{
			source?.Dispose();
			return;
		}

		_fullImage = source;
		if ( source is null )
			_viewing = null;
		MarkChanged();
	}

	/// <summary>Decodes an image's reduced pixels through a temporary file, which Goo's thumbnail decoder reads, and deletes the file.</summary>
	private static ImageSource DecodeThumbnail( byte[] encoded )
	{
		var folder = Path.Combine( LoknPaths.TempRoot, "images" );
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
			image?.Dispose();
		_images.Clear();
		_imageLoads.Clear();
		_loadedImages.Clear();
		_imageBlocks.Clear();
		_fullImage?.Dispose();
		_fullImage = null;
		_viewing = null;
	}
}
