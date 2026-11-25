#if LOKN_WINDOWS
using System.Runtime.Versioning;
using Windows.Foundation;
using Windows.Graphics.Imaging;
#endif

namespace Lokn.Modules.Remote;

/// <summary>Encodes BGRA frames to JPEG and back with the Windows imaging component, for <c>cap.frame</c>.</summary>
/// <remarks>Safe on any thread; the Windows Runtime calls run on the caller's thread pool.</remarks>
public static class JpegEncoder
{
#if LOKN_WINDOWS
	/// <summary>Defines the oldest Windows the imaging API is annotated for.</summary>
	private const int ImagingBuild = 10240;
#endif

	/// <summary>Encodes a frame as a baseline JPEG.</summary>
	/// <param name="frame">The BGRA pixels; alpha is ignored.</param>
	/// <param name="quality">The JPEG quality from 0 (smallest) to 1 (best); values outside are clamped.</param>
	/// <param name="cancellation">Cancels the encoding.</param>
	/// <returns>The encoded JPEG bytes.</returns>
	/// <exception cref="PlatformNotSupportedException">Not running on Windows 10 or later.</exception>
	public static Task<byte[]> EncodeAsync( BgraFrame frame, double quality, CancellationToken cancellation = default )
	{
#if LOKN_WINDOWS
		if ( OperatingSystem.IsWindowsVersionAtLeast( 10, 0, ImagingBuild ) )
			return EncodeCoreAsync( frame, (float)Math.Clamp( quality, 0, 1 ), cancellation );
#endif
		throw new PlatformNotSupportedException( "JPEG encoding needs Windows 10 or later." );
	}

	/// <summary>Decodes a JPEG, or any format Windows decodes, to BGRA pixels.</summary>
	/// <param name="data">The encoded image.</param>
	/// <param name="cancellation">Cancels the decoding.</param>
	/// <returns>The decoded pixels, rows top first.</returns>
	/// <exception cref="PlatformNotSupportedException">Not running on Windows 10 or later.</exception>
	public static Task<BgraFrame> DecodeAsync( byte[] data, CancellationToken cancellation = default )
	{
#if LOKN_WINDOWS
		if ( OperatingSystem.IsWindowsVersionAtLeast( 10, 0, ImagingBuild ) )
			return DecodeCoreAsync( data, cancellation );
#endif
		throw new PlatformNotSupportedException( "JPEG decoding needs Windows 10 or later." );
	}

#if LOKN_WINDOWS
	/// <summary>Encodes through <see cref="BitmapEncoder"/> into memory.</summary>
	[SupportedOSPlatform( "windows10.0.10240.0" )]
	private static async Task<byte[]> EncodeCoreAsync( BgraFrame frame, float quality, CancellationToken cancellation )
	{
		using var output = new MemoryStream();
		using var stream = output.AsRandomAccessStream();
		var options = new BitmapPropertySet
		{
			["ImageQuality"] = new BitmapTypedValue( quality, PropertyType.Single ),
		};
		var encoder = await BitmapEncoder.CreateAsync( BitmapEncoder.JpegEncoderId, stream, options ).AsTask( cancellation );
		encoder.SetPixelData( BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels );
		await encoder.FlushAsync().AsTask( cancellation );
		return output.ToArray();
	}

	/// <summary>Decodes through <see cref="BitmapDecoder"/> from memory.</summary>
	[SupportedOSPlatform( "windows10.0.10240.0" )]
	private static async Task<BgraFrame> DecodeCoreAsync( byte[] data, CancellationToken cancellation )
	{
		using var input = new MemoryStream( data, writable: false );
		using var stream = input.AsRandomAccessStream();
		var decoder = await BitmapDecoder.CreateAsync( stream ).AsTask( cancellation );
		var pixels = await decoder.GetPixelDataAsync( BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage ).AsTask( cancellation );
		return new( (int)decoder.PixelWidth, (int)decoder.PixelHeight, pixels.DetachPixelData() );
	}
#endif
}
