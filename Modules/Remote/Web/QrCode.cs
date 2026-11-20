using System.Text;

namespace Lokn.Modules.Remote;

/// <summary>Encodes text as a QR code symbol (ISO/IEC 18004): byte mode, error correction level M, the smallest version from 1 to 25 that holds it,
/// and the mask with the lowest penalty score.</summary>
public static class QrCode
{
	/// <summary>The largest version encoded; a pairing link fits well below it.</summary>
	private const int MaxVersion = 25;

	/// <summary>The format information's error correction bits for level M.</summary>
	private const int LevelBits = 0;

	/// <summary>The penalty for each run of five same-colored modules, plus one per module beyond five.</summary>
	private const int RunPenalty = 3;

	/// <summary>The penalty for each 2×2 block of one color.</summary>
	private const int BlockPenalty = 3;

	/// <summary>The penalty for each finder-like 1:1:3:1:1 pattern with four light modules on a side.</summary>
	private const int FinderPenalty = 40;

	/// <summary>The penalty for each 5% the dark share strays from half.</summary>
	private const int BalancePenalty = 10;

	/// <summary>Holds the error correction codewords per block at level M, by version; index 0 is unused.</summary>
	private static readonly int[] EccPerBlock = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28];

	/// <summary>Holds the error correction blocks at level M, by version; index 0 is unused.</summary>
	private static readonly int[] BlockCount = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21];

	/// <summary>Encodes the UTF-8 bytes of a text.</summary>
	/// <param name="text">The text, such as a pairing link.</param>
	/// <returns>The modules as <c>[y, x]</c>, true for dark, without the quiet zone the caller draws around them.</returns>
	/// <exception cref="ArgumentException">The text is too long for version 25.</exception>
	public static bool[,] Encode( string text )
	{
		var data = Encoding.UTF8.GetBytes( text );
		var version = ComputeVersion( data.Length );
		var symbol = new Symbol( version );
		symbol.DrawFunctionPatterns();
		symbol.DrawCodewords( BuildCodewords( BuildDataCodewords( data, version ), version ) );

		var best = 0;
		var lowest = int.MaxValue;
		for ( var mask = 0; mask < 8; mask++ )
		{
			symbol.ApplyMask( mask );
			symbol.DrawFormat( mask );
			var penalty = symbol.ComputePenalty();
			if ( penalty < lowest )
			{
				best = mask;
				lowest = penalty;
			}
			// Applying a mask twice undoes it.
			symbol.ApplyMask( mask );
		}
		symbol.ApplyMask( best );
		symbol.DrawFormat( best );
		return symbol.Modules;
	}

	/// <summary>Computes the side of a version's symbol in modules.</summary>
	public static int ComputeSize( int version ) => version * 4 + 17;

	/// <summary>Picks the smallest version whose data codewords hold the mode, the count and the bytes.</summary>
	/// <exception cref="ArgumentException">No version up to 25 holds them.</exception>
	private static int ComputeVersion( int length )
	{
		for ( var version = 1; version <= MaxVersion; version++ )
		{
			if ( 4 + ComputeCountBits( version ) + length * 8 <= ComputeDataCodewords( version ) * 8 )
				return version;
		}
		throw new ArgumentException( $"The text is too long for a QR code: {length} bytes." );
	}

	/// <summary>Computes the bits of the byte mode's character count: 8 up to version 9, else 16.</summary>
	private static int ComputeCountBits( int version ) => version <= 9 ? 8 : 16;

	/// <summary>Computes the data codewords of a version at level M.</summary>
	private static int ComputeDataCodewords( int version ) => ComputeRawModules( version ) / 8 - EccPerBlock[version] * BlockCount[version];

	/// <summary>Computes the modules of a version that carry codewords and remainder bits: all but the function patterns.</summary>
	private static int ComputeRawModules( int version )
	{
		var result = (16 * version + 128) * version + 64;
		if ( version >= 2 )
		{
			var alignments = version / 7 + 2;
			result -= (25 * alignments - 10) * alignments - 55;
			if ( version >= 7 )
				result -= 36;
		}
		return result;
	}

	/// <summary>Builds the data codewords: mode 0100, the count, the bytes, up to four terminator bits, zero bits to a byte, then pad bytes 0xEC and 0x11.</summary>
	private static byte[] BuildDataCodewords( byte[] data, int version )
	{
		var capacity = ComputeDataCodewords( version );
		var bits = new BitWriter( capacity );
		bits.Write( 0b0100, 4 );
		bits.Write( data.Length, ComputeCountBits( version ) );
		foreach ( var value in data )
			bits.Write( value, 8 );
		bits.Write( 0, Math.Min( 4, capacity * 8 - bits.Length ) );
		bits.Write( 0, (8 - bits.Length % 8) % 8 );
		for ( var pad = 0xEC; bits.Length < capacity * 8; pad ^= 0xEC ^ 0x11 )
			bits.Write( pad, 8 );
		return bits.Bytes;
	}

	/// <summary>Splits the data codewords into blocks, adds each block's Reed-Solomon codewords and interleaves them, as the symbol carries them.</summary>
	private static byte[] BuildCodewords( byte[] data, int version )
	{
		var blocks = BlockCount[version];
		var ecc = EccPerBlock[version];
		var raw = ComputeRawModules( version ) / 8;
		var shortBlocks = blocks - raw % blocks;
		var shortLength = raw / blocks;
		var divisor = ComputeDivisor( ecc );
		var dataParts = new byte[blocks][];
		var eccParts = new byte[blocks][];
		var offset = 0;
		for ( var block = 0; block < blocks; block++ )
		{
			var length = shortLength - ecc + (block < shortBlocks ? 0 : 1);
			dataParts[block] = data[offset..(offset + length)];
			eccParts[block] = ComputeRemainder( dataParts[block], divisor );
			offset += length;
		}

		var result = new byte[raw];
		var index = 0;
		for ( var column = 0; column <= shortLength - ecc; column++ )
		{
			foreach ( var part in dataParts )
			{
				if ( column < part.Length )
					result[index++] = part[column];
			}
		}
		for ( var column = 0; column < ecc; column++ )
		{
			foreach ( var part in eccParts )
				result[index++] = part[column];
		}
		return result;
	}

	/// <summary>Computes the Reed-Solomon generator polynomial of a degree over GF(256) with polynomial 0x11D, highest coefficient first, leading 1 left out.</summary>
	private static byte[] ComputeDivisor( int degree )
	{
		var result = new byte[degree];
		result[degree - 1] = 1;
		var root = 1;
		for ( var step = 0; step < degree; step++ )
		{
			for ( var index = 0; index < degree; index++ )
			{
				result[index] = Multiply( result[index], root );
				if ( index + 1 < degree )
					result[index] ^= result[index + 1];
			}
			root = Multiply( root, 0x02 );
		}
		return result;
	}

	/// <summary>Computes the remainder of the data polynomial divided by the generator, which is the block's error correction codewords.</summary>
	private static byte[] ComputeRemainder( byte[] data, byte[] divisor )
	{
		var result = new byte[divisor.Length];
		foreach ( var value in data )
		{
			var factor = value ^ result[0];
			Array.Copy( result, 1, result, 0, result.Length - 1 );
			result[^1] = 0;
			for ( var index = 0; index < result.Length; index++ )
				result[index] ^= Multiply( divisor[index], factor );
		}
		return result;
	}

	/// <summary>Multiplies two elements of GF(256) with the reducing polynomial 0x11D.</summary>
	private static byte Multiply( int left, int right )
	{
		var result = 0;
		for ( var bit = 7; bit >= 0; bit-- )
		{
			result = (result << 1) ^ ((result >> 7) * 0x11D);
			result ^= ((right >> bit) & 1) * left;
		}
		return (byte)result;
	}

	/// <summary>Appends bits, most significant first, into a byte array of fixed size.</summary>
	/// <param name="capacity">The bytes the writer holds.</param>
	private sealed class BitWriter( int capacity )
	{
		/// <summary>Gets the bytes written, zero past <see cref="Length"/>.</summary>
		public byte[] Bytes
		{
			get;
		} = new byte[capacity];

		/// <summary>Gets the bits written.</summary>
		public int Length
		{
			get; private set;
		}

		/// <summary>Appends the low <paramref name="count"/> bits of a value, highest first.</summary>
		public void Write( int value, int count )
		{
			for ( var bit = count - 1; bit >= 0; bit-- )
			{
				if ( ((value >> bit) & 1) != 0 )
					Bytes[Length >> 3] |= (byte)(0x80 >> (Length & 7));
				Length++;
			}
		}
	}

	/// <summary>The modules of one symbol and which of them belong to function patterns.</summary>
	/// <param name="version">The symbol's version.</param>
	private sealed class Symbol( int version )
	{
		/// <summary>Stores the side in modules.</summary>
		private readonly int _size = ComputeSize( version );

		/// <summary>Marks the modules of function patterns, which carry no data and no mask.</summary>
		private readonly bool[,] _isFunction = new bool[ComputeSize( version ), ComputeSize( version )];

		/// <summary>Gets the modules as <c>[y, x]</c>, true for dark.</summary>
		public bool[,] Modules
		{
			get;
		} = new bool[ComputeSize( version ), ComputeSize( version )];

		/// <summary>Draws the timing, finder and alignment patterns, reserves the format area and the dark module, and draws the version information.</summary>
		public void DrawFunctionPatterns()
		{
			for ( var index = 0; index < _size; index++ )
			{
				UpdateFunction( 6, index, index % 2 == 0 );
				UpdateFunction( index, 6, index % 2 == 0 );
			}

			DrawFinder( 3, 3 );
			DrawFinder( _size - 4, 3 );
			DrawFinder( 3, _size - 4 );

			var positions = ComputeAlignmentPositions();
			var last = positions.Length - 1;
			for ( var row = 0; row <= last; row++ )
			{
				for ( var column = 0; column <= last; column++ )
				{
					// The finder corners have no alignment pattern.
					if ( (row == 0 && column == 0) || (row == 0 && column == last) || (row == last && column == 0) )
						continue;
					DrawAlignment( positions[column], positions[row] );
				}
			}

			// Mask 0 reserves the format area; the real format is drawn once the mask is chosen.
			DrawFormat( 0 );
			DrawVersion();
		}

		/// <summary>Places the codewords' bits in the two-column zigzag from the bottom right, skipping function modules and the vertical timing column.</summary>
		public void DrawCodewords( byte[] codewords )
		{
			var bit = 0;
			for ( var right = _size - 1; right >= 1; right -= 2 )
			{
				if ( right == 6 )
					right = 5;
				var isUpward = ((right + 1) & 2) == 0;
				for ( var step = 0; step < _size; step++ )
				{
					var y = isUpward ? _size - 1 - step : step;
					for ( var offset = 0; offset < 2; offset++ )
					{
						var x = right - offset;
						if ( _isFunction[y, x] || bit >= codewords.Length * 8 )
							continue;
						Modules[y, x] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
						bit++;
					}
				}
			}
		}

		/// <summary>Inverts the data modules a mask pattern selects; applying it again undoes it.</summary>
		public void ApplyMask( int mask )
		{
			for ( var y = 0; y < _size; y++ )
			{
				for ( var x = 0; x < _size; x++ )
				{
					var invert = mask switch
					{
						0 => (x + y) % 2 == 0,
						1 => y % 2 == 0,
						2 => x % 3 == 0,
						3 => (x + y) % 3 == 0,
						4 => (x / 3 + y / 2) % 2 == 0,
						5 => x * y % 2 + x * y % 3 == 0,
						6 => (x * y % 2 + x * y % 3) % 2 == 0,
						_ => ((x + y) % 2 + x * y % 3) % 2 == 0,
					};
					if ( invert && !_isFunction[y, x] )
						Modules[y, x] = !Modules[y, x];
				}
			}
		}

		/// <summary>Draws both copies of the format information for level M and a mask, and the dark module.</summary>
		public void DrawFormat( int mask )
		{
			var data = (LevelBits << 3) | mask;
			var remainder = data;
			for ( var step = 0; step < 10; step++ )
				remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
			var bits = ((data << 10) | remainder) ^ 0x5412;

			for ( var index = 0; index <= 5; index++ )
				UpdateFunction( 8, index, ReadBit( bits, index ) );
			UpdateFunction( 8, 7, ReadBit( bits, 6 ) );
			UpdateFunction( 8, 8, ReadBit( bits, 7 ) );
			UpdateFunction( 7, 8, ReadBit( bits, 8 ) );
			for ( var index = 9; index < 15; index++ )
				UpdateFunction( 14 - index, 8, ReadBit( bits, index ) );

			for ( var index = 0; index < 8; index++ )
				UpdateFunction( _size - 1 - index, 8, ReadBit( bits, index ) );
			for ( var index = 8; index < 15; index++ )
				UpdateFunction( 8, _size - 15 + index, ReadBit( bits, index ) );
			UpdateFunction( 8, _size - 8, true );
		}

		/// <summary>Scores the symbol by the standard's four penalty rules: runs, 2×2 blocks, finder-like patterns and dark balance.</summary>
		public int ComputePenalty()
		{
			var result = 0;
			Span<int> history = stackalloc int[7];
			for ( var vertical = 0; vertical < 2; vertical++ )
			{
				for ( var line = 0; line < _size; line++ )
				{
					var color = false;
					var run = 0;
					history.Clear();
					for ( var along = 0; along < _size; along++ )
					{
						var module = vertical == 0 ? Modules[line, along] : Modules[along, line];
						if ( module == color )
						{
							run++;
							if ( run == 5 )
								result += RunPenalty;
							else if ( run > 5 )
								result++;
						}
						else
						{
							UpdateHistory( run, history );
							if ( !color )
								result += CountFinderPatterns( history ) * FinderPenalty;
							color = module;
							run = 1;
						}
					}
					// The light quiet zone ends every line: a dark run ends there, and the light border joins the last light run.
					if ( color )
					{
						UpdateHistory( run, history );
						run = 0;
					}
					UpdateHistory( run + _size, history );
					result += CountFinderPatterns( history ) * FinderPenalty;
				}
			}

			var dark = 0;
			for ( var y = 0; y < _size; y++ )
			{
				for ( var x = 0; x < _size; x++ )
				{
					if ( Modules[y, x] )
						dark++;
					if ( y + 1 < _size && x + 1 < _size && Modules[y, x] == Modules[y, x + 1] && Modules[y, x] == Modules[y + 1, x] && Modules[y, x] == Modules[y + 1, x + 1] )
						result += BlockPenalty;
				}
			}
			var total = _size * _size;
			result += ((Math.Abs( dark * 20 - total * 10 ) + total - 1) / total - 1) * BalancePenalty;
			return result;
		}

		/// <summary>Pushes a run length onto the newest end of the run history; the first run of a line includes the light border before it.</summary>
		private void UpdateHistory( int run, Span<int> history )
		{
			if ( history[0] == 0 )
				run += _size;
			history[..6].CopyTo( history[1..] );
			history[0] = run;
		}

		/// <summary>Counts the 1:1:3:1:1 dark-light patterns at the newest end of the history with at least four light modules before or after them.</summary>
		private static int CountFinderPatterns( Span<int> history )
		{
			var unit = history[1];
			var isCore = unit > 0 && history[2] == unit && history[3] == unit * 3 && history[4] == unit && history[5] == unit;
			return (isCore && history[0] >= unit * 4 && history[6] >= unit ? 1 : 0)
				+ (isCore && history[6] >= unit * 4 && history[0] >= unit ? 1 : 0);
		}

		/// <summary>Computes the alignment patterns' centers on each axis, ascending.</summary>
		private int[] ComputeAlignmentPositions()
		{
			if ( version == 1 )
				return [];
			var count = version / 7 + 2;
			var step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
			var result = new int[count];
			result[0] = 6;
			for ( var index = 1; index < count; index++ )
				result[index] = _size - 7 - (count - 1 - index) * step;
			return result;
		}

		/// <summary>Draws a finder pattern and its light separator around a center, clipped to the symbol.</summary>
		private void DrawFinder( int x, int y )
		{
			for ( var dy = -4; dy <= 4; dy++ )
			{
				for ( var dx = -4; dx <= 4; dx++ )
				{
					var distance = Math.Max( Math.Abs( dx ), Math.Abs( dy ) );
					if ( x + dx >= 0 && x + dx < _size && y + dy >= 0 && y + dy < _size )
						UpdateFunction( x + dx, y + dy, distance is not (2 or 4) );
				}
			}
		}

		/// <summary>Draws a 5×5 alignment pattern around a center.</summary>
		private void DrawAlignment( int x, int y )
		{
			for ( var dy = -2; dy <= 2; dy++ )
			{
				for ( var dx = -2; dx <= 2; dx++ )
					UpdateFunction( x + dx, y + dy, Math.Max( Math.Abs( dx ), Math.Abs( dy ) ) != 1 );
			}
		}

		/// <summary>Draws both copies of the 18-bit version information, from version 7 on.</summary>
		private void DrawVersion()
		{
			if ( version < 7 )
				return;
			var remainder = version;
			for ( var step = 0; step < 12; step++ )
				remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
			var bits = (version << 12) | remainder;
			for ( var index = 0; index < 18; index++ )
			{
				var near = _size - 11 + index % 3;
				var far = index / 3;
				UpdateFunction( near, far, ReadBit( bits, index ) );
				UpdateFunction( far, near, ReadBit( bits, index ) );
			}
		}

		/// <summary>Sets a module and marks it as part of a function pattern.</summary>
		private void UpdateFunction( int x, int y, bool isDark )
		{
			Modules[y, x] = isDark;
			_isFunction[y, x] = true;
		}

		/// <summary>Reads one bit of a value, counting from the lowest.</summary>
		private static bool ReadBit( int value, int index ) => ((value >> index) & 1) != 0;
	}
}
