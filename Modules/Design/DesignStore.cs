using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lokn.Modules.Design;

/// <summary>Reads and writes one design's folder: its variants, its comments and its exports.</summary>
/// <remarks>
/// A design is <c>&lt;project&gt;/.lokn/design/&lt;name&gt;/</c>. A variant is a folder in it with an <c>index.html</c>; an <c>index.html</c> in the design folder
/// itself is the variant with the empty name. Comments are kept in <c>comments.json</c>, exports in <c>exports/</c>.
/// </remarks>
internal sealed partial class DesignStore
{
	/// <summary>Defines the file a variant starts at.</summary>
	internal const string IndexFile = "index.html";

	/// <summary>Defines the file comments are kept in.</summary>
	internal const string CommentsFile = "comments.json";

	/// <summary>Defines the folder exports are written to.</summary>
	internal const string ExportsFolder = "exports";

	/// <summary>Defines the most comments a design keeps; adding more drops the oldest resolved ones first.</summary>
	private const int MaxComments = 500;

	/// <summary>Defines the longest comment text kept, in characters.</summary>
	private const int MaxCommentLength = 4000;

	/// <summary>Defines the longest selector kept, in characters.</summary>
	private const int MaxSelectorLength = 1000;

	/// <summary>Defines the longest element description kept with a comment, in characters.</summary>
	private const int MaxContextLength = 1200;

	/// <summary>Serializes comments readably, with camel-case names.</summary>
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	/// <summary>Serializes comment changes one at a time, across every design.</summary>
	private static readonly Lock CommentsLock = new();

	/// <summary>Creates the store over a design folder.</summary>
	/// <param name="name">The design's name, its folder name.</param>
	/// <param name="folder">The design folder's full path.</param>
	internal DesignStore( string name, string folder )
	{
		Name = name;
		Folder = Path.GetFullPath( folder );
		Token = ComputeToken( Folder );
	}

	/// <summary>Gets the design's name.</summary>
	internal string Name { get; }

	/// <summary>Gets the design folder's full path.</summary>
	internal string Folder { get; }

	/// <summary>Gets the design's address segment, stable for its folder.</summary>
	internal string Token { get; }

	/// <summary>Gets the design folder of a design in a project.</summary>
	/// <param name="project">The project or working folder.</param>
	/// <param name="name">The design's name.</param>
	/// <exception cref="ArgumentException">The name is not a valid design name.</exception>
	internal static string GetFolder( string project, string name )
	{
		if ( !IsValidName( name ) )
			throw new ArgumentException( $"\"{name}\" is not a valid design name; use 1 to 64 lowercase letters, digits, - and _." );
		return Path.Combine( Path.GetFullPath( project ), ".lokn", "design", name );
	}

	/// <summary>Reports whether a name is a valid design or variant name.</summary>
	internal static bool IsValidName( string name ) => NamePattern().IsMatch( name );

	/// <summary>Lists the design's variants by name, the empty name first, then in ordinal order.</summary>
	internal IReadOnlyList<string> ListVariants()
	{
		if ( !Directory.Exists( Folder ) )
			return [];

		var variants = new List<string>();
		if ( File.Exists( Path.Combine( Folder, IndexFile ) ) )
			variants.Add( "" );
		variants.AddRange( Directory.EnumerateDirectories( Folder )
			.Select( Path.GetFileName )
			.OfType<string>()
			.Where( name => name != ExportsFolder && IsValidName( name ) && File.Exists( Path.Combine( Folder, name, IndexFile ) ) )
			.Order( StringComparer.Ordinal ) );
		return variants;
	}

	/// <summary>Gets a variant's folder.</summary>
	/// <exception cref="ArgumentException">The variant name is not valid.</exception>
	internal string GetVariantFolder( string variant )
	{
		if ( variant.Length > 0 && !IsValidName( variant ) )
			throw new ArgumentException( $"\"{variant}\" is not a valid variant name." );
		return variant.Length == 0 ? Folder : Path.Combine( Folder, variant );
	}

	/// <summary>Resolves a path the studio asks for to a file in the design folder, or null when it is outside it or missing.</summary>
	/// <param name="relative">The path below the design folder, with forward slashes.</param>
	internal string? ResolveFile( string relative )
	{
		var decoded = Uri.UnescapeDataString( relative ).Replace( '/', Path.DirectorySeparatorChar );
		if ( decoded.Length == 0 || decoded.EndsWith( Path.DirectorySeparatorChar ) )
			decoded += IndexFile;

		var full = Path.GetFullPath( Path.Combine( Folder, decoded ) );
		var root = Folder.EndsWith( Path.DirectorySeparatorChar ) ? Folder : Folder + Path.DirectorySeparatorChar;
		return full.StartsWith( root, StringComparison.OrdinalIgnoreCase ) && File.Exists( full ) ? full : null;
	}

	/// <summary>Reads the design's comments, oldest first; none when the file is missing or does not parse.</summary>
	internal IReadOnlyList<DesignComment> ReadComments()
	{
		lock ( CommentsLock )
			return ReadCommentsCore();
	}

	/// <summary>Adds a comment and returns it.</summary>
	/// <param name="context">What the studio saw of the element: its markup and key styles, so the agent edits it without reading the page first; empty for none.</param>
	/// <exception cref="ArgumentException">The text is blank or the variant name is not valid.</exception>
	internal DesignComment AddComment( string variant, string selector, string text, string context = "" )
	{
		text = text.Trim();
		if ( text.Length == 0 )
			throw new ArgumentException( "The comment is blank." );
		_ = GetVariantFolder( variant );

		var comment = new DesignComment( Convert.ToHexStringLower( RandomNumberGenerator.GetBytes( 4 ) ), variant, Truncate( selector.Trim(), MaxSelectorLength ),
			Truncate( text, MaxCommentLength ), DateTimeOffset.UtcNow, false ) { Context = context.Trim().Length > 0 ? Truncate( context.Trim(), MaxContextLength ) : null };
		lock ( CommentsLock )
		{
			var comments = ReadCommentsCore().ToList();
			comments.Add( comment );
			while ( comments.Count > MaxComments )
				comments.RemoveAt( Math.Max( 0, comments.FindIndex( item => item.Resolved ) ) );
			WriteComments( comments );
		}
		return comment;
	}

	/// <summary>Sets whether comments are resolved and returns how many it changed.</summary>
	/// <param name="ids">The comments' identifiers.</param>
	/// <param name="resolved">Whether they are done.</param>
	internal int SetResolved( IReadOnlyCollection<string> ids, bool resolved )
	{
		lock ( CommentsLock )
		{
			var comments = ReadCommentsCore().ToList();
			var changed = 0;
			for ( var index = 0; index < comments.Count; index++ )
			{
				if ( comments[index].Resolved != resolved && ids.Contains( comments[index].Id ) )
				{
					comments[index] = comments[index] with { Resolved = resolved };
					changed++;
				}
			}
			if ( changed > 0 )
				WriteComments( comments );
			return changed;
		}
	}

	/// <summary>Writes a variant's files as a zip archive to a stream, leaving out comments, exports and other variants.</summary>
	/// <exception cref="InvalidOperationException">The variant has no index.html.</exception>
	internal void WriteArchive( string variant, Stream destination )
	{
		var folder = GetVariantFolder( variant );
		if ( !File.Exists( Path.Combine( folder, IndexFile ) ) )
			throw new InvalidOperationException( $"Variant \"{variant}\" of design {Name} has no {IndexFile}." );

		var others = ListVariants().Where( name => name.Length > 0 && name != variant ).Select( name => Path.Combine( Folder, name ) + Path.DirectorySeparatorChar ).ToArray();
		var exports = Path.Combine( Folder, ExportsFolder ) + Path.DirectorySeparatorChar;
		using var archive = new ZipArchive( destination, ZipArchiveMode.Create, leaveOpen: true );
		foreach ( var file in Directory.EnumerateFiles( folder, "*", SearchOption.AllDirectories ) )
		{
			if ( file.StartsWith( exports, StringComparison.OrdinalIgnoreCase ) || others.Any( other => file.StartsWith( other, StringComparison.OrdinalIgnoreCase ) )
				|| string.Equals( file, Path.Combine( Folder, CommentsFile ), StringComparison.OrdinalIgnoreCase ) )
				continue;
			archive.CreateEntryFromFile( file, Path.GetRelativePath( folder, file ).Replace( Path.DirectorySeparatorChar, '/' ) );
		}
	}

	/// <summary>Exports a variant as a zip archive in the design's exports folder and returns its path.</summary>
	internal string Export( string variant )
	{
		var exports = Path.Combine( Folder, ExportsFolder );
		Directory.CreateDirectory( exports );
		var path = Path.Combine( exports, $"{Name}{(variant.Length > 0 ? "-" + variant : "")}-{DateTime.Now:yyyyMMdd-HHmmss}.zip" );
		using ( var file = File.Create( path ) )
			WriteArchive( variant, file );
		return path;
	}

	/// <summary>Writes the message that asks a chat's agent to apply comments: each one's variant file, element and text, and how to mark it done.</summary>
	internal string ComposeRequest( IReadOnlyList<DesignComment> comments )
	{
		var text = new StringBuilder();
		text.Append( "Apply my comment" ).Append( comments.Count == 1 ? "" : "s" ).Append( " on design " ).Append( Name ).AppendLine( ":" );
		foreach ( var comment in comments )
		{
			var file = Path.Combine( GetVariantFolder( comment.Variant ), IndexFile );
			var element = comment.Selector.Length > 0 ? $"`{comment.Selector}`" : "the whole page";
			text.Append( "- [" ).Append( comment.Id ).Append( "] " ).Append( file ).Append( ", " ).Append( element ).Append( ": " ).AppendLine( comment.Text.ReplaceLineEndings( " " ) );
			if ( comment.Context is { } seen )
				text.Append( "  Element: " ).AppendLine( seen.ReplaceLineEndings( " " ) );
		}
		text.Append( "Then call design_resolve with name " ).Append( Name ).Append( " and the ids of the comments you applied." );
		return text.ToString();
	}

	/// <summary>Computes a folder's address segment: a hash of its path, so addresses stay the same across restarts.</summary>
	private static string ComputeToken( string folder ) =>
		Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( folder.ToLowerInvariant() ) ) )[..16];

	/// <summary>Reads the comments without taking the lock.</summary>
	private List<DesignComment> ReadCommentsCore()
	{
		var path = Path.Combine( Folder, CommentsFile );
		if ( !File.Exists( path ) )
			return [];
		try
		{
			return JsonSerializer.Deserialize<List<DesignComment>>( File.ReadAllText( path ), Json ) ?? [];
		}
		catch ( JsonException )
		{
			return [];
		}
	}

	/// <summary>Writes the comments through a temporary file, so a reader never sees half of them.</summary>
	private void WriteComments( List<DesignComment> comments )
	{
		Directory.CreateDirectory( Folder );
		var path = Path.Combine( Folder, CommentsFile );
		var temporary = path + ".tmp";
		File.WriteAllText( temporary, JsonSerializer.Serialize( comments, Json ) );
		File.Move( temporary, path, overwrite: true );
	}

	/// <summary>Cuts text to a length.</summary>
	private static string Truncate( string text, int length ) => text.Length > length ? text[..length] : text;

	/// <summary>Matches design and variant names.</summary>
	[GeneratedRegex( "^[a-z0-9][a-z0-9_-]{0,63}$" )]
	private static partial Regex NamePattern();
}

/// <summary>Holds a note the user pinned to an element of a design.</summary>
/// <param name="Id">The comment's identifier, eight hex digits.</param>
/// <param name="Variant">The variant it is on; empty for the design's own index.html.</param>
/// <param name="Selector">A CSS selector for the element, or empty for the whole page.</param>
/// <param name="Text">What the user wrote.</param>
/// <param name="Created">When the user wrote it.</param>
/// <param name="Resolved">Whether it was done.</param>
internal sealed record DesignComment( string Id, string Variant, string Selector, string Text, DateTimeOffset Created, bool Resolved )
{
	/// <summary>Gets what the studio saw of the element when the user commented: its start tag, text and key computed styles; null for none.</summary>
	public string? Context { get; init; }
}
