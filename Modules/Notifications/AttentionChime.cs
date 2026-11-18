using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Lokn.Modules.Notifications;

/// <summary>Plays the soft two-note chime that tells the user a panel is waiting for them.</summary>
/// <remarks>
/// The chime is synthesized rather than shipped: two rounded, quickly fading bell tones a fourth apart (E5 then A5),
/// well below full scale, with no sharp attack. It reads as a gentle "ready" cue that stays pleasant when heard often.
/// </remarks>
internal static class AttentionChime
{
	/// <summary>Defines the synthesized sample rate in hertz.</summary>
	private const int SampleRate = 44100;

	/// <summary>Defines the chime length in seconds, long enough for both tones to ring out.</summary>
	private const double Duration = 1.1;

	/// <summary>Defines the loudest sample as a fraction of full scale, keeping the chime quieter than system alerts.</summary>
	private const double Peak = 0.25;

	/// <summary>Defines each tone's fade-in in seconds; a rounded start avoids a click.</summary>
	private const double Attack = 0.008;

	/// <summary>Defines each tone's decay time constant in seconds.</summary>
	private const double Decay = 0.3;

	/// <summary>Defines the closing fade in seconds, so the sound ends on silence.</summary>
	private const double Release = 0.05;

	/// <summary>Defines winmm's flag to return while the sound plays.</summary>
	private const uint SoundAsync = 0x1;

	/// <summary>Defines winmm's flag to stay silent instead of playing the default sound on failure.</summary>
	private const uint SoundNoDefault = 0x2;

	/// <summary>Defines winmm's flag that the sound argument points at an in-memory WAV image.</summary>
	private const uint SoundMemory = 0x4;

	/// <summary>Lists each tone's frequency in hertz, start in seconds, and relative level.</summary>
	private static readonly (double Frequency, double Start, double Level)[] _tones = [(659.25, 0, 1), (880, 0.13, 0.8)];

	/// <summary>Holds the WAV image on the pinned heap, because asynchronous playback reads it after the call returns.</summary>
	private static readonly Lazy<byte[]> _wave = new( BuildWave );

	/// <summary>Starts the chime without blocking, replacing one that is still playing.</summary>
	internal static void Play()
	{
		if ( !OperatingSystem.IsWindows() )
			return;

		// Without an audio device winmm returns false; the chime is optional, so silence is the right outcome.
		_ = PlaySound( Marshal.UnsafeAddrOfPinnedArrayElement( _wave.Value, 0 ), 0, SoundAsync | SoundNoDefault | SoundMemory );
	}

	/// <summary>Synthesizes the chime as a mono 16-bit PCM WAV image.</summary>
	internal static byte[] BuildWave()
	{
		const int HeaderSize = 44;
		var count = (int)(SampleRate * Duration);
		var samples = new double[count];
		var loudest = 0.0;
		for ( var index = 0; index < count; index++ )
		{
			var time = (double)index / SampleRate;
			var sample = 0.0;
			foreach ( var (frequency, start, level) in _tones )
			{
				var age = time - start;
				if ( age < 0 )
					continue;

				var envelope = level * Math.Exp( -age / Decay ) * (age < Attack ? 0.5 - 0.5 * Math.Cos( Math.PI * age / Attack ) : 1);
				// Faint, faster-fading overtones give a soft bell color; a pure sine alone sounds like a test tone.
				sample += envelope * (Math.Sin( 2 * Math.PI * frequency * age )
					+ 0.2 * Math.Exp( -age / 0.12 ) * Math.Sin( 4 * Math.PI * frequency * age )
					+ 0.05 * Math.Exp( -age / 0.06 ) * Math.Sin( 6 * Math.PI * frequency * age ));
			}

			var remaining = Duration - time;
			samples[index] = remaining < Release ? sample * remaining / Release : sample;
			loudest = Math.Max( loudest, Math.Abs( samples[index] ) );
		}

		var wave = GC.AllocateUninitializedArray<byte>( HeaderSize + count * sizeof( short ), pinned: true );
		var span = wave.AsSpan();
		"RIFF"u8.CopyTo( span );
		BinaryPrimitives.WriteInt32LittleEndian( span[4..], wave.Length - 8 );
		"WAVEfmt "u8.CopyTo( span[8..] );
		BinaryPrimitives.WriteInt32LittleEndian( span[16..], 16 );
		BinaryPrimitives.WriteInt16LittleEndian( span[20..], 1 );
		BinaryPrimitives.WriteInt16LittleEndian( span[22..], 1 );
		BinaryPrimitives.WriteInt32LittleEndian( span[24..], SampleRate );
		BinaryPrimitives.WriteInt32LittleEndian( span[28..], SampleRate * sizeof( short ) );
		BinaryPrimitives.WriteInt16LittleEndian( span[32..], sizeof( short ) );
		BinaryPrimitives.WriteInt16LittleEndian( span[34..], 16 );
		"data"u8.CopyTo( span[36..] );
		BinaryPrimitives.WriteInt32LittleEndian( span[40..], count * sizeof( short ) );
		var scale = Peak * short.MaxValue / loudest;
		for ( var index = 0; index < count; index++ )
			BinaryPrimitives.WriteInt16LittleEndian( span[(HeaderSize + index * sizeof( short ))..], (short)Math.Round( samples[index] * scale ) );
		return wave;
	}

	/// <summary>Plays a WAV sound through the Windows multimedia API.</summary>
	[DllImport( "winmm.dll", EntryPoint = "PlaySoundW" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool PlaySound( nint sound, nint module, uint flags );
}
