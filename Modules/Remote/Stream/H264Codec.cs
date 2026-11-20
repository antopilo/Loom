using System.Diagnostics;

namespace Lokn.Modules.Remote;

/// <summary>Encodes BGRA frames to H.264 with Media Foundation: a hardware encoder when the GPU has one, else Windows' software encoder.</summary>
/// <remarks>
/// Baseline profile with low-latency mode, so there are no B-frames and each input gives its output at once; the first output is a key frame with
/// its parameter sets. Output is an Annex B byte stream. Frames must keep one size: <see cref="CaptureSession"/> makes a new encoder when the size changes.
/// Uses one thread at a time.
/// </remarks>
internal sealed class H264Encoder : IDisposable
{
	/// <summary>Defines eAVEncH264VProfile_Base.</summary>
	private const uint BaselineProfile = 66;

	/// <summary>Defines how long an asynchronous encoder may take to ask for input or give output, in milliseconds.</summary>
	private const int EventWait = 40;

	/// <summary>Stores the transform.</summary>
	private readonly nint _transform;

	/// <summary>Stores the event generator of an asynchronous (hardware) encoder, or 0.</summary>
	private readonly nint _events;

	/// <summary>Stores the NV12 input buffer.</summary>
	private readonly byte[] _nv12;

	/// <summary>Stores the frame duration in 100 ns units.</summary>
	private readonly long _duration;

	/// <summary>Counts input requests an asynchronous encoder made that were not answered yet.</summary>
	private int _wanted;

	/// <summary>Stores the next sample time.</summary>
	private long _time;

	/// <summary>Gets the encoded width, the frame width rounded down to even.</summary>
	public int Width
	{
		get;
	}

	/// <summary>Gets the encoded height, the frame height rounded down to even.</summary>
	public int Height
	{
		get;
	}

	/// <summary>Gets whether a hardware encoder runs.</summary>
	public bool IsHardware => _events != 0;

	/// <summary>Creates an encoder for frames of one size.</summary>
	/// <exception cref="NotSupportedException">No H.264 encoder accepts these settings.</exception>
	public H264Encoder( int width, int height, int fps, int bitrate )
	{
		if ( !MediaFoundation.Start() )
			throw new NotSupportedException( "Media Foundation is not available." );

		Width = width & ~1;
		Height = height & ~1;
		if ( Width < 2 || Height < 2 )
			throw new NotSupportedException( "The frame is too small to encode." );

		_nv12 = new byte[Width * Height * 3 / 2];
		_duration = 10_000_000L / Math.Max( fps, 1 );
		foreach ( var flags in new[] { MediaFoundation.HardwareFlag | MediaFoundation.AsyncFlag | MediaFoundation.SortFlag, MediaFoundation.SyncFlag | MediaFoundation.SortFlag } )
		{
			var transform = MediaFoundation.CreateTransform( MediaFoundation.VideoEncoderCategory, flags, null, MediaFoundation.H264 );
			if ( transform == 0 )
				continue;

			var events = (flags & MediaFoundation.AsyncFlag) != 0 ? MediaFoundation.GetEventGenerator( transform ) : 0;
			if ( Configure( transform, events != 0, fps, bitrate ) )
			{
				_transform = transform;
				_events = events;
				return;
			}

			MediaFoundation.Release( events );
			MediaFoundation.Release( transform );
		}

		throw new NotSupportedException( "No H.264 encoder accepts this stream." );
	}

	/// <summary>Encodes one frame; returns the Annex B bytes it produced, which may be empty while a hardware encoder still works on it.</summary>
	public byte[] Encode( BgraFrame frame )
	{
		ConvertToNv12( frame );
		var sample = MediaFoundation.CreateSample( _nv12, _time, _duration );
		_time += _duration;
		try
		{
			return _events == 0 ? EncodeSync( sample ) : EncodeAsync( sample );
		}
		finally
		{
			MediaFoundation.Release( sample );
		}
	}

	/// <summary>Releases the encoder.</summary>
	public void Dispose()
	{
		MediaFoundation.Release( _events );
		MediaFoundation.Release( _transform );
	}

	/// <summary>Sets the output then the input type, low-latency mode, and starts streaming; false when the encoder refuses them.</summary>
	private bool Configure( nint transform, bool isAsync, int fps, int bitrate )
	{
		var attributes = MediaFoundation.GetAttributes( transform );
		if ( attributes != 0 )
		{
			if ( isAsync )
				_ = MediaFoundation.SetUInt32( attributes, MediaFoundation.AsyncUnlockKey, 1 );
			_ = MediaFoundation.SetUInt32( attributes, MediaFoundation.LowLatencyKey, 1 );
			MediaFoundation.Release( attributes );
		}

		var output = BuildType( MediaFoundation.H264, fps );
		var input = BuildType( MediaFoundation.Nv12, fps );
		try
		{
			_ = MediaFoundation.SetUInt32( output, MediaFoundation.BitrateKey, (uint)bitrate );
			_ = MediaFoundation.SetUInt32( output, MediaFoundation.ProfileKey, BaselineProfile );
			if ( MediaFoundation.SetOutputType( transform, output ) < 0 || MediaFoundation.SetInputType( transform, input ) < 0 )
				return false;
		}
		finally
		{
			MediaFoundation.Release( output );
			MediaFoundation.Release( input );
		}

		return MediaFoundation.ProcessMessage( transform, MediaFoundation.BeginStreaming ) >= 0
			&& MediaFoundation.ProcessMessage( transform, MediaFoundation.StartOfStream ) >= 0;
	}

	/// <summary>Builds a progressive video type of the encoded size.</summary>
	private nint BuildType( Guid subtype, int fps )
	{
		var type = MediaFoundation.CreateType();
		_ = MediaFoundation.SetGuid( type, MediaFoundation.MajorTypeKey, MediaFoundation.VideoMajor );
		_ = MediaFoundation.SetGuid( type, MediaFoundation.SubtypeKey, subtype );
		_ = MediaFoundation.SetUInt64( type, MediaFoundation.FrameSizeKey, ((ulong)Width << 32) | (uint)Height );
		_ = MediaFoundation.SetUInt64( type, MediaFoundation.FrameRateKey, ((ulong)fps << 32) | 1 );
		_ = MediaFoundation.SetUInt64( type, MediaFoundation.PixelAspectKey, (1UL << 32) | 1 );
		_ = MediaFoundation.SetUInt32( type, MediaFoundation.InterlaceKey, 2 );
		return type;
	}

	/// <summary>Gives the sample to a synchronous encoder and takes every output it has.</summary>
	private byte[] EncodeSync( nint sample )
	{
		MediaFoundation.Check( MediaFoundation.ProcessInput( _transform, sample ) );
		using var output = new MemoryStream();
		while ( true )
		{
			var (result, produced) = MediaFoundation.ProcessOutput( _transform );
			if ( result == MediaFoundation.NeedMoreInput )
				break;

			MediaFoundation.Check( result );
			output.Write( MediaFoundation.ReadSample( produced ) );
			MediaFoundation.Release( produced );
		}

		return output.ToArray();
	}

	/// <summary>Waits for an asynchronous encoder to ask for input, gives it the sample, then takes the output it announces within <see cref="EventWait"/>.</summary>
	private byte[] EncodeAsync( nint sample )
	{
		using var output = new MemoryStream();
		var clock = Stopwatch.StartNew();
		while ( _wanted == 0 && clock.ElapsedMilliseconds < EventWait * 3 )
		{
			if ( !Poll( output ) )
				Thread.Yield();
		}
		if ( _wanted == 0 )
			return output.ToArray();

		MediaFoundation.Check( MediaFoundation.ProcessInput( _transform, sample ) );
		_wanted--;
		clock.Restart();
		// A hardware encoder answers within a few milliseconds; Thread.Sleep( 1 ) would wait a whole timer tick, up to 15 ms.
		while ( output.Length == 0 && clock.ElapsedMilliseconds < EventWait )
		{
			if ( !Poll( output ) )
				Thread.Yield();
		}

		while ( Poll( output ) )
		{
		}

		return output.ToArray();
	}

	/// <summary>Handles one queued event of an asynchronous encoder; false when none was queued.</summary>
	private bool Poll( MemoryStream output )
	{
		switch ( MediaFoundation.TakeEvent( _events ) )
		{
			case 0:
				return false;
			case MediaFoundation.NeedInputEvent:
				_wanted++;
				return true;
			case MediaFoundation.HaveOutputEvent:
				var (result, produced) = MediaFoundation.ProcessOutput( _transform );
				MediaFoundation.Check( result );
				output.Write( MediaFoundation.ReadSample( produced ) );
				MediaFoundation.Release( produced );
				return true;
			default:
				return true;
		}
	}

	/// <summary>Converts the frame's BGRA pixels to NV12 with BT.601 limited-range colors, averaging each 2×2 block for the chroma.</summary>
	private void ConvertToNv12( BgraFrame frame )
	{
		var source = frame.Pixels;
		var stride = frame.Width * 4;
		var chroma = Width * Height;
		for ( var y = 0; y < Height; y += 2 )
		{
			for ( var x = 0; x < Width; x += 2 )
			{
				int sumR = 0, sumG = 0, sumB = 0;
				for ( var dy = 0; dy < 2; dy++ )
				{
					for ( var dx = 0; dx < 2; dx++ )
					{
						var index = (y + dy) * stride + (x + dx) * 4;
						int b = source[index], g = source[index + 1], r = source[index + 2];
						_nv12[(y + dy) * Width + x + dx] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
						sumR += r;
						sumG += g;
						sumB += b;
					}
				}

				int red = sumR >> 2, green = sumG >> 2, blue = sumB >> 2;
				var target = chroma + (y >> 1) * Width + x;
				_nv12[target] = (byte)(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
				_nv12[target + 1] = (byte)(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
			}
		}
	}
}

/// <summary>Decodes an H.264 stream from <see cref="H264Encoder"/> to RGBA images with Windows' Media Foundation decoder, for <see cref="StreamContent"/>.</summary>
/// <remarks>Feed every frame of a stream in order; a new stream needs a new decoder. Uses one thread at a time.</remarks>
internal sealed class H264Decoder : IDisposable
{
	/// <summary>Stores the transform.</summary>
	private readonly nint _transform;

	/// <summary>Stores the next sample time.</summary>
	private long _time;

	/// <summary>Creates a decoder.</summary>
	/// <exception cref="NotSupportedException">This Windows has no H.264 decoder.</exception>
	public H264Decoder()
	{
		if ( !MediaFoundation.Start() )
			throw new NotSupportedException( "Media Foundation is not available." );

		_transform = MediaFoundation.CreateTransform( MediaFoundation.VideoDecoderCategory, MediaFoundation.SyncFlag | MediaFoundation.SortFlag, MediaFoundation.H264, null );
		if ( _transform == 0 )
			throw new NotSupportedException( "No H.264 decoder is installed." );

		var attributes = MediaFoundation.GetAttributes( _transform );
		if ( attributes != 0 )
		{
			_ = MediaFoundation.SetUInt32( attributes, MediaFoundation.LowLatencyKey, 1 );
			MediaFoundation.Release( attributes );
		}

		var input = MediaFoundation.CreateType();
		try
		{
			_ = MediaFoundation.SetGuid( input, MediaFoundation.MajorTypeKey, MediaFoundation.VideoMajor );
			_ = MediaFoundation.SetGuid( input, MediaFoundation.SubtypeKey, MediaFoundation.H264 );
			MediaFoundation.Check( MediaFoundation.SetInputType( _transform, input ) );
		}
		finally
		{
			MediaFoundation.Release( input );
		}

		ChooseOutput();
		_ = MediaFoundation.ProcessMessage( _transform, MediaFoundation.BeginStreaming );
	}

	/// <summary>Reports whether this PC can decode the stream, so a client asks for H.264 only then.</summary>
	public static bool IsAvailable()
	{
		try
		{
			new H264Decoder().Dispose();
			return true;
		}
		catch ( Exception error ) when ( error is NotSupportedException or System.Runtime.InteropServices.COMException or DllNotFoundException )
		{
			return false;
		}
	}

	/// <summary>Decodes one encoded frame to an RGBA picture (in <see cref="BgraFrame.Pixels"/>, red first) of at most the frame's size, or returns null when the decoder gave no picture for it yet.</summary>
	public BgraFrame? Decode( byte[] data, int width, int height )
	{
		var sample = MediaFoundation.CreateSample( data, _time, 0 );
		_time += 333_333;
		try
		{
			var result = MediaFoundation.ProcessInput( _transform, sample );
			BgraFrame? picture = null;
			if ( result == MediaFoundation.NotAccepting )
			{
				picture = Drain( width, height );
				result = MediaFoundation.ProcessInput( _transform, sample );
			}
			MediaFoundation.Check( result );
			return Drain( width, height ) ?? picture;
		}
		finally
		{
			MediaFoundation.Release( sample );
		}
	}

	/// <summary>Releases the decoder.</summary>
	public void Dispose() => MediaFoundation.Release( _transform );

	/// <summary>Takes every picture the decoder has and returns the last as RGBA, following a format change on the way.</summary>
	private BgraFrame? Drain( int width, int height )
	{
		BgraFrame? picture = null;
		while ( true )
		{
			var (result, produced) = MediaFoundation.ProcessOutput( _transform );
			if ( result == MediaFoundation.NeedMoreInput )
				return picture;

			if ( result == MediaFoundation.StreamChange )
			{
				ChooseOutput();
				continue;
			}

			MediaFoundation.Check( result );
			var nv12 = MediaFoundation.ReadSample( produced );
			MediaFoundation.Release( produced );
			picture = ConvertToRgba( nv12, width, height );
		}
	}

	/// <summary>Picks the NV12 output type the decoder offers.</summary>
	private void ChooseOutput()
	{
		for ( uint index = 0; ; index++ )
		{
			var type = MediaFoundation.GetOutputAvailableType( _transform, index );
			if ( type == 0 )
				throw new NotSupportedException( "The H.264 decoder offers no NV12 output." );

			try
			{
				if ( MediaFoundation.GetGuid( type, MediaFoundation.SubtypeKey ) == MediaFoundation.Nv12 )
				{
					MediaFoundation.Check( MediaFoundation.SetOutputType( _transform, type ) );
					return;
				}
			}
			finally
			{
				MediaFoundation.Release( type );
			}
		}
	}

	/// <summary>Converts the decoder's NV12 picture, whose rows and planes may be padded to its own size, to RGBA of the frame's size.</summary>
	private BgraFrame ConvertToRgba( byte[] nv12, int width, int height )
	{
		var type = MediaFoundation.GetOutputCurrentType( _transform );
		var size = type == 0 ? 0 : MediaFoundation.GetUInt64( type, MediaFoundation.FrameSizeKey );
		MediaFoundation.Release( type );
		var stride = size == 0 ? width : (int)(size >> 32);
		var planeHeight = size == 0 ? height : (int)(uint)size;
		// A buffer with more rows than the type says has its height padded further, as the 16-line alignment of H.264 asks.
		if ( nv12.Length >= stride * planeHeight * 3 / 2 && nv12.Length / (stride * 3 / 2) > planeHeight )
			planeHeight = nv12.Length * 2 / 3 / stride;
		width = Math.Min( width, stride );
		height = Math.Min( height, planeHeight );

		var rgba = new byte[width * height * 4];
		var chroma = stride * planeHeight;
		for ( var y = 0; y < height; y++ )
		{
			var row = y * stride;
			var chromaRow = chroma + (y >> 1) * stride;
			for ( var x = 0; x < width; x++ )
			{
				var c = 298 * (nv12[row + x] - 16);
				var uv = chromaRow + (x & ~1);
				var d = nv12[uv] - 128;
				var e = nv12[uv + 1] - 128;
				var target = (y * width + x) * 4;
				rgba[target] = Clamp( (c + 409 * e + 128) >> 8 );
				rgba[target + 1] = Clamp( (c - 100 * d - 208 * e + 128) >> 8 );
				rgba[target + 2] = Clamp( (c + 516 * d + 128) >> 8 );
				rgba[target + 3] = 255;
			}
		}

		return new( width, height, rgba );
	}

	/// <summary>Clamps a color channel to a byte.</summary>
	private static byte Clamp( int value ) => (byte)Math.Clamp( value, 0, 255 );
}
