using System.Runtime.InteropServices;

namespace Lokn.Modules.Remote;

/// <summary>Calls the few Media Foundation functions and COM methods the H.264 stream needs, through vtable slots, so no interop library ships with the module.</summary>
/// <remarks>Every <c>nint</c> is a COM pointer the caller owns and releases with <see cref="Release"/>.</remarks>
internal static unsafe class MediaFoundation
{
	/// <summary>Defines MF_E_TRANSFORM_NEED_MORE_INPUT.</summary>
	internal const int NeedMoreInput = unchecked((int)0xC00D6D72);

	/// <summary>Defines MF_E_TRANSFORM_STREAM_CHANGE.</summary>
	internal const int StreamChange = unchecked((int)0xC00D6D61);

	/// <summary>Defines MF_E_NOTACCEPTING.</summary>
	internal const int NotAccepting = unchecked((int)0xC00D36B5);

	/// <summary>Defines MF_E_NO_EVENTS_AVAILABLE.</summary>
	internal const int NoEvents = unchecked((int)0xC00D3E80);

	/// <summary>Defines METransformNeedInput.</summary>
	internal const uint NeedInputEvent = 601;

	/// <summary>Defines METransformHaveOutput.</summary>
	internal const uint HaveOutputEvent = 602;

	/// <summary>Defines MFT_ENUM_FLAG_SYNCMFT.</summary>
	internal const uint SyncFlag = 0x1;

	/// <summary>Defines MFT_ENUM_FLAG_ASYNCMFT.</summary>
	internal const uint AsyncFlag = 0x2;

	/// <summary>Defines MFT_ENUM_FLAG_HARDWARE.</summary>
	internal const uint HardwareFlag = 0x4;

	/// <summary>Defines MFT_ENUM_FLAG_SORTANDFILTER.</summary>
	internal const uint SortFlag = 0x40;

	/// <summary>Defines MFT_OUTPUT_STREAM_PROVIDES_SAMPLES and MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.</summary>
	internal const uint ProvidesSamples = 0x100 | 0x200;

	/// <summary>Defines MFT_MESSAGE_NOTIFY_BEGIN_STREAMING.</summary>
	internal const uint BeginStreaming = 0x10000000;

	/// <summary>Defines MFT_MESSAGE_NOTIFY_START_OF_STREAM.</summary>
	internal const uint StartOfStream = 0x10000003;

	internal static readonly Guid VideoEncoderCategory = new( "f79eac7d-e545-4387-bdee-d647d7bde42a" );
	internal static readonly Guid VideoDecoderCategory = new( "d6c02d4b-6833-45b4-971a-05a4b04bab91" );
	internal static readonly Guid VideoMajor = new( "73646976-0000-0010-8000-00AA00389B71" );
	internal static readonly Guid H264 = new( "34363248-0000-0010-8000-00AA00389B71" );
	internal static readonly Guid Nv12 = new( "3231564E-0000-0010-8000-00AA00389B71" );
	internal static readonly Guid MajorTypeKey = new( "48eba18e-f8c9-4687-bf11-0a74c9f96a8f" );
	internal static readonly Guid SubtypeKey = new( "f7e34c9a-42e8-4714-b74b-cb29d72c35e5" );
	internal static readonly Guid FrameSizeKey = new( "1652c33d-d6b2-4012-b834-72030849a37d" );
	internal static readonly Guid FrameRateKey = new( "c459a2e8-3d2c-4e44-b132-fee5156c7bb0" );
	internal static readonly Guid PixelAspectKey = new( "c6376a1e-8d0a-4027-be45-6d9a0ad39bb6" );
	internal static readonly Guid BitrateKey = new( "20332624-fb0d-4d9e-bd0d-cbf6786c102e" );
	internal static readonly Guid InterlaceKey = new( "e2724bb8-e676-4806-b4b2-a8d6efb44ccd" );
	internal static readonly Guid ProfileKey = new( "ad76a80b-2d5c-4e0b-b375-64e520137036" );
	internal static readonly Guid LowLatencyKey = new( "9c27891a-ed7a-40e1-88e8-b22727a024ee" );
	internal static readonly Guid AsyncUnlockKey = new( "e5666d6b-3422-4eb6-a421-da7db1f8e207" );
	private static readonly Guid TransformId = new( "bf94c121-5b05-4e6f-8000-ba598961414d" );
	private static readonly Guid EventGeneratorId = new( "2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D" );

	/// <summary>Stores whether Media Foundation started, or null before the first try.</summary>
	private static bool? _isStarted;

	/// <summary>Starts Media Foundation once for the process; false when this Windows has none, such as an N edition without the media pack.</summary>
	internal static bool Start()
	{
		lock ( TransformId.GetType() )
		{
			if ( _isStarted is { } started )
				return started;

			try
			{
				_isStarted = MFStartup( 0x20070, 0 ) >= 0;
			}
			catch ( DllNotFoundException )
			{
				_isStarted = false;
			}

			return _isStarted.Value;
		}
	}

	/// <summary>Creates the first transform of a category that turns <paramref name="input"/> into <paramref name="output"/>, or 0 when none exists.</summary>
	/// <param name="isAsync">Whether the created transform is asynchronous and needs its event loop.</param>
	internal static nint CreateTransform( Guid category, uint flags, Guid? input, Guid? output )
	{
		var inputInfo = new TypeInfo { Major = VideoMajor, Subtype = input ?? Guid.Empty };
		var outputInfo = new TypeInfo { Major = VideoMajor, Subtype = output ?? Guid.Empty };
		if ( MFTEnumEx( category, flags, input is null ? null : &inputInfo, output is null ? null : &outputInfo, out var activates, out var count ) < 0 || count == 0 )
			return 0;

		nint transform = 0;
		try
		{
			for ( var index = 0; index < count && transform == 0; index++ )
			{
				var id = TransformId;
				nint created;
				if ( ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot( activates[index], 33 ))( activates[index], &id, &created ) >= 0 )
					transform = created;
			}
		}
		finally
		{
			for ( var index = 0; index < count; index++ )
				Release( activates[index] );
			Marshal.FreeCoTaskMem( (nint)activates );
		}

		return transform;
	}

	/// <summary>Gets a transform's attribute store, or 0 when it has none.</summary>
	internal static nint GetAttributes( nint transform )
	{
		nint attributes;
		return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot( transform, 8 ))( transform, &attributes ) >= 0 ? attributes : 0;
	}

	/// <summary>Gets a transform's event generator, which an asynchronous transform has.</summary>
	internal static nint GetEventGenerator( nint transform )
	{
		var id = EventGeneratorId;
		return Marshal.QueryInterface( transform, in id, out var generator ) >= 0 ? generator : 0;
	}

	/// <summary>Takes the next event of an asynchronous transform without waiting, or returns 0 when none is queued.</summary>
	internal static uint TakeEvent( nint generator )
	{
		nint received;
		var result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot( generator, 3 ))( generator, 1, &received );
		if ( result == NoEvents || result < 0 )
			return 0;

		uint type;
		_ = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot( received, 33 ))( received, &type );
		Release( received );
		return type;
	}

	/// <summary>Creates an empty media type.</summary>
	internal static nint CreateType()
	{
		Check( MFCreateMediaType( out var type ) );
		return type;
	}

	/// <summary>Sets a 32-bit attribute.</summary>
	internal static int SetUInt32( nint attributes, Guid key, uint value ) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot( attributes, 21 ))( attributes, &key, value );

	/// <summary>Sets a 64-bit attribute, such as a size packed as high and low halves.</summary>
	internal static int SetUInt64( nint attributes, Guid key, ulong value ) => ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot( attributes, 22 ))( attributes, &key, value );

	/// <summary>Sets a GUID attribute.</summary>
	internal static int SetGuid( nint attributes, Guid key, Guid value ) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot( attributes, 24 ))( attributes, &key, &value );

	/// <summary>Reads a 64-bit attribute, or 0.</summary>
	internal static ulong GetUInt64( nint attributes, Guid key )
	{
		ulong value;
		return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot( attributes, 8 ))( attributes, &key, &value ) >= 0 ? value : 0;
	}

	/// <summary>Reads a GUID attribute, or <see cref="Guid.Empty"/>.</summary>
	internal static Guid GetGuid( nint attributes, Guid key )
	{
		Guid value;
		return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot( attributes, 10 ))( attributes, &key, &value ) >= 0 ? value : Guid.Empty;
	}

	/// <summary>Sets a transform's input type.</summary>
	internal static int SetInputType( nint transform, nint type ) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot( transform, 15 ))( transform, 0, type, 0 );

	/// <summary>Sets a transform's output type.</summary>
	internal static int SetOutputType( nint transform, nint type ) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot( transform, 16 ))( transform, 0, type, 0 );

	/// <summary>Gets one of the output types a transform offers, or 0 past the last.</summary>
	internal static nint GetOutputAvailableType( nint transform, uint index )
	{
		nint type;
		return ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot( transform, 14 ))( transform, 0, index, &type ) >= 0 ? type : 0;
	}

	/// <summary>Gets a transform's current output type, or 0.</summary>
	internal static nint GetOutputCurrentType( nint transform )
	{
		nint type;
		return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot( transform, 18 ))( transform, 0, &type ) >= 0 ? type : 0;
	}

	/// <summary>Sends a message, such as <see cref="BeginStreaming"/>.</summary>
	internal static int ProcessMessage( nint transform, uint message ) => ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot( transform, 23 ))( transform, message, 0 );

	/// <summary>Gives a transform one input sample.</summary>
	internal static int ProcessInput( nint transform, nint sample ) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot( transform, 24 ))( transform, 0, sample, 0 );

	/// <summary>Takes one output sample: the transform's own, or one made here when the transform needs the caller to provide it.</summary>
	/// <returns>The result and the sample, which the caller releases; 0 on failure.</returns>
	internal static (int Result, nint Sample) ProcessOutput( nint transform )
	{
		StreamInfo info;
		_ = ((delegate* unmanaged[Stdcall]<nint, uint, StreamInfo*, int>)Slot( transform, 7 ))( transform, 0, &info );
		var buffer = new OutputBuffer();
		if ( (info.Flags & ProvidesSamples) == 0 )
			buffer.Sample = CreateSample( Math.Max( (int)info.Size, 1 << 20 ) );

		uint status;
		var result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, OutputBuffer*, uint*, int>)Slot( transform, 25 ))( transform, 0, 1, &buffer, &status );
		if ( buffer.Events != 0 )
			Release( buffer.Events );
		if ( result < 0 )
		{
			Release( buffer.Sample );
			return (result, 0);
		}

		return (result, buffer.Sample);
	}

	/// <summary>Creates a sample over a new memory buffer of a capacity, empty.</summary>
	internal static nint CreateSample( int capacity )
	{
		Check( MFCreateMemoryBuffer( (uint)capacity, out var buffer ) );
		try
		{
			Check( MFCreateSample( out var sample ) );
			Check( ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot( sample, 42 ))( sample, buffer ) );
			return sample;
		}
		finally
		{
			Release( buffer );
		}
	}

	/// <summary>Creates a sample holding a copy of bytes, with its time and duration in 100 ns units.</summary>
	internal static nint CreateSample( ReadOnlySpan<byte> data, long time, long duration )
	{
		var sample = CreateSample( data.Length );
		var buffer = GetBuffer( sample );
		try
		{
			byte* target;
			uint max, current;
			Check( ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot( buffer, 3 ))( buffer, &target, &max, &current ) );
			data.CopyTo( new Span<byte>( target, data.Length ) );
			_ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot( buffer, 4 ))( buffer );
			_ = ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot( buffer, 6 ))( buffer, (uint)data.Length );
		}
		finally
		{
			Release( buffer );
		}

		_ = ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot( sample, 36 ))( sample, time );
		_ = ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot( sample, 38 ))( sample, duration );
		return sample;
	}

	/// <summary>Copies a sample's bytes out.</summary>
	internal static byte[] ReadSample( nint sample )
	{
		var buffer = GetBuffer( sample );
		try
		{
			byte* source;
			uint max, current;
			Check( ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot( buffer, 3 ))( buffer, &source, &max, &current ) );
			var bytes = new ReadOnlySpan<byte>( source, (int)current ).ToArray();
			_ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot( buffer, 4 ))( buffer );
			return bytes;
		}
		finally
		{
			Release( buffer );
		}
	}

	/// <summary>Releases a COM pointer; 0 is ignored.</summary>
	internal static void Release( nint pointer )
	{
		if ( pointer != 0 )
			Marshal.Release( pointer );
	}

	/// <summary>Throws for a failed result.</summary>
	internal static void Check( int result ) => Marshal.ThrowExceptionForHR( result );

	/// <summary>Gets a sample's single contiguous buffer.</summary>
	private static nint GetBuffer( nint sample )
	{
		nint buffer;
		Check( ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot( sample, 41 ))( sample, &buffer ) );
		return buffer;
	}

	/// <summary>Reads a COM object's vtable slot, for the caller to cast to the method's function pointer.</summary>
	private static nint Slot( nint obj, int slot ) => (*(nint**)obj)[slot];

	/// <summary>Mirrors MFT_REGISTER_TYPE_INFO.</summary>
	private struct TypeInfo
	{
		public Guid Major;
		public Guid Subtype;
	}

	/// <summary>Mirrors MFT_OUTPUT_STREAM_INFO.</summary>
	private struct StreamInfo
	{
		public uint Flags;
		public uint Size;
		public uint Alignment;
	}

	/// <summary>Mirrors MFT_OUTPUT_DATA_BUFFER.</summary>
	private struct OutputBuffer
	{
		public uint StreamId;
		public nint Sample;
		public uint Status;
		public nint Events;
	}

	[DllImport( "mfplat.dll" )]
	private static extern int MFStartup( uint version, uint flags );

	[DllImport( "mfplat.dll" )]
	private static extern int MFTEnumEx( Guid category, uint flags, TypeInfo* input, TypeInfo* output, out nint* activates, out int count );

	[DllImport( "mfplat.dll" )]
	private static extern int MFCreateMediaType( out nint type );

	[DllImport( "mfplat.dll" )]
	private static extern int MFCreateSample( out nint sample );

	[DllImport( "mfplat.dll" )]
	private static extern int MFCreateMemoryBuffer( uint capacity, out nint buffer );
}
