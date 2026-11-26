using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Goo;

namespace Lokn.Core;

/// <summary>Registers an installed font family the user picks by name, in every weight and slant it has, under a Loom alias.</summary>
public static partial class SystemFonts
{
	/// <summary>Defines the weights and slants asked of fontconfig off Windows.</summary>
	private static readonly (int Weight, bool Italic)[] Styles = [(400, false), (600, false), (700, false), (400, true), (700, true)];

	/// <summary>Holds each registered family by the name it was picked by, so a family is found and registered once.</summary>
	private static readonly Dictionary<string, string?> Picked = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Guards the picked families, which the UI thread and startup share.</summary>
	private static readonly Lock PickedGate = new();

	/// <summary>Registers every face of an installed family under an alias views can use, or returns null when no font of that family is installed.</summary>
	/// <remarks>Goo draws only registered faces, so an installed family typed in Settings is found here: on Windows by the family name in each
	/// font file of the system and per-user Fonts folders, elsewhere through fontconfig. Its regular, bold and italic faces all register.</remarks>
	/// <param name="family">The family name, such as Inter or Cascadia Code.</param>
	/// <returns>The alias to use in a font family list, or null.</returns>
	public static string? RegisterFamily( string family )
	{
		family = family.Trim();
		if ( family.Length == 0 )
			return null;

		lock ( PickedGate )
		{
			if ( Picked.TryGetValue( family, out var known ) )
				return known;

			var alias = $"Lokn Picked {family}";
			var count = 0;
			try
			{
				if ( OperatingSystem.IsWindows() )
				{
					foreach ( var (path, index, weight, italic, isVariable) in FindWindowsFaces( family ) )
					{
						// A variable face holds every weight on its wght axis, so it registers once per weight Loom draws, each at that weight;
						// left at its default instance it drew every weight alike.
						foreach ( var drawn in isVariable ? Styles.Where( style => !style.Italic ).Select( style => style.Weight ) : [weight] )
						{
							FontSource.FromPath( alias, drawn, italic, path, index, isVariable ? [new( "wght", drawn )] : [] ).Register();
							count++;
						}
					}
				}
				else
				{
					foreach ( var (weight, italic) in Styles )
					{
						if ( MatchExact( alias, family, weight, italic ) is { } font )
						{
							font.Register();
							count++;
						}
					}
				}
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or DllNotFoundException )
			{
				count = 0;
			}

			var result = count > 0 ? alias : null;
			Picked[family] = result;
			return result;
		}
	}

	/// <summary>Finds the faces of a family in the Windows font folders: each file's path, face index, weight and slant.</summary>
	private static IEnumerable<(string Path, uint Index, int Weight, bool Italic, bool IsVariable)> FindWindowsFaces( string family )
	{
		string[] folders =
		[
			Environment.GetFolderPath( Environment.SpecialFolder.Fonts ),
			Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Microsoft", "Windows", "Fonts" ),
		];
		foreach ( var folder in folders.Where( Directory.Exists ) )
		{
			foreach ( var path in Directory.EnumerateFiles( folder ) )
			{
				var extension = Path.GetExtension( path );
				if ( !extension.Equals( ".ttf", StringComparison.OrdinalIgnoreCase ) && !extension.Equals( ".otf", StringComparison.OrdinalIgnoreCase )
					&& !extension.Equals( ".ttc", StringComparison.OrdinalIgnoreCase ) )
					continue;

				foreach ( var face in ReadFaces( path ) )
				{
					if ( face.Families.Any( name => name.Equals( family, StringComparison.OrdinalIgnoreCase ) ) )
						yield return (path, face.Index, face.Weight, face.Italic, face.IsVariable);
				}
			}
		}
	}

	/// <summary>Reads each face of a font file: its family names, weight and slant; nothing when the file cannot be read.</summary>
	private static List<(uint Index, string[] Families, int Weight, bool Italic, bool IsVariable)> ReadFaces( string path )
	{
		var faces = new List<(uint, string[], int, bool, bool)>();
		byte[] data;
		try
		{
			data = File.ReadAllBytes( path );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			return faces;
		}

		uint[] offsets = data.Length >= 12 && data.AsSpan( 0, 4 ).SequenceEqual( "ttcf"u8 )
			? [.. Enumerable.Range( 0, (int)BinaryPrimitives.ReadUInt32BigEndian( data.AsSpan( 8 ) ) ).Select( i => BinaryPrimitives.ReadUInt32BigEndian( data.AsSpan( 12 + 4 * i ) ) )]
			: [0];
		for ( var index = 0; index < offsets.Length; index++ )
		{
			try
			{
				if ( ReadFace( data, (int)offsets[index] ) is { } face )
					faces.Add( ((uint)index, face.Families, face.Weight, face.Italic, face.IsVariable) );
			}
			catch ( ArgumentOutOfRangeException )
			{
				// A truncated or unusual file is skipped.
			}
		}
		return faces;
	}

	/// <summary>Reads one face's family names from its name table, and its weight and slant from its OS/2 table.</summary>
	private static (string[] Families, int Weight, bool Italic, bool IsVariable)? ReadFace( byte[] data, int offset )
	{
		var span = data.AsSpan();
		var tables = BinaryPrimitives.ReadUInt16BigEndian( span[(offset + 4)..] );
		int name = -1, os2 = -1;
		var isVariable = false;
		for ( var i = 0; i < tables; i++ )
		{
			var record = offset + 12 + i * 16;
			var tag = span.Slice( record, 4 );
			var at = (int)BinaryPrimitives.ReadUInt32BigEndian( span[(record + 8)..] );
			if ( tag.SequenceEqual( "name"u8 ) )
				name = at;
			else if ( tag.SequenceEqual( "OS/2"u8 ) )
				os2 = at;
			else if ( tag.SequenceEqual( "fvar"u8 ) )
				isVariable = true;
		}
		if ( name < 0 )
			return null;

		var families = new List<string>();
		var count = BinaryPrimitives.ReadUInt16BigEndian( span[(name + 2)..] );
		var strings = name + BinaryPrimitives.ReadUInt16BigEndian( span[(name + 4)..] );
		for ( var i = 0; i < count; i++ )
		{
			var record = name + 6 + i * 12;
			var platform = BinaryPrimitives.ReadUInt16BigEndian( span[record..] );
			var id = BinaryPrimitives.ReadUInt16BigEndian( span[(record + 6)..] );
			// Family (1) and typographic family (16), in the Windows Unicode encoding.
			if ( platform != 3 || id is not (1 or 16) )
				continue;
			var length = BinaryPrimitives.ReadUInt16BigEndian( span[(record + 8)..] );
			var start = strings + BinaryPrimitives.ReadUInt16BigEndian( span[(record + 10)..] );
			families.Add( Encoding.BigEndianUnicode.GetString( span.Slice( start, length ) ) );
		}

		var weight = 400;
		var italic = false;
		if ( os2 >= 0 )
		{
			weight = BinaryPrimitives.ReadUInt16BigEndian( span[(os2 + 4)..] );
			italic = (BinaryPrimitives.ReadUInt16BigEndian( span[(os2 + 62)..] ) & 1) != 0;
		}
		return families.Count == 0 ? null : ([.. families.Distinct()], weight, italic, isVariable);
	}

	/// <summary>Loads the face fontconfig matches for a family, weight and slant, only when it is that family rather than a substitute.</summary>
	private static FontSource? MatchExact( string alias, string family, int weight, bool italic )
	{
		var pattern = FcNameParse( $"{family}:weight={FcWeightFromOpenType( weight )}:slant={( italic ? 100 : 0 )}" );
		FcConfigSubstitute( 0, pattern, 0 );
		FcDefaultSubstitute( pattern );
		var match = FcFontMatch( 0, pattern, out _ );
		FcPatternDestroy( pattern );
		if ( match == 0 )
			return null;
		try
		{
			if ( FcPatternGetString( match, "family", 0, out var matched ) != 0
				|| !string.Equals( Marshal.PtrToStringUTF8( matched ), family, StringComparison.OrdinalIgnoreCase ) )
				return null;
			FcPatternGetString( match, "file", 0, out var file );
			FcPatternGetInteger( match, "index", 0, out var index );
			return FontSource.FromPath( alias, weight, italic, Marshal.PtrToStringUTF8( file )!, (uint)index & 0xFFFF, index >> 16 == 0 ? [] : [new( "wght", weight )] );
		}
		finally
		{
			FcPatternDestroy( match );
		}
	}
}
