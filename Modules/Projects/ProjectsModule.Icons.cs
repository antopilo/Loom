using Goo;
using Lokn.Core;

namespace Lokn.Modules.Projects;

/// <summary>Finds and asynchronously loads project icons from working directories and recognized development environments.</summary>
public sealed partial class ProjectsModule
{
	/// <summary>Defines the decoded icon size in physical pixels, enough for Home and the sidebar at high display densities.</summary>
	private const int IconPixels = 64;

	/// <summary>Caches one lazy image provider per project directory; null records a directory with no resolved icon.</summary>
	private readonly Dictionary<string, AsyncImageSource?> _icons = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Gets the shared lazy icon source for a project, or null when its directory has no supported icon.</summary>
	/// <remarks>The source is released when this module stops, so a view resolves it again on each build.</remarks>
	public AsyncImageSource? GetIcon( ProjectInfo project )
	{
		if ( _icons.TryGetValue( project.Directory, out var known ) )
			return known;

		var path = ProjectIcons.FindPath( project.Directory );
		var source = path is null ? null : new AsyncImageSource( cancellation => Task.Run(
			() => ImageSource.LoadThumbnail( path, IconPixels, IconPixels, cancellation ), cancellation ) );
		_icons.Add( project.Directory, source );
		return source;
	}

	/// <summary>Builds a square project icon from its asynchronously loaded source.</summary>
	/// <param name="source">The icon from <see cref="GetIcon"/>.</param>
	/// <param name="projectName">The project's name, which names the icon for accessibility.</param>
	/// <param name="size">The icon's width and height in logical pixels.</param>
	public static Image BuildIcon( ImageSourceProvider source, string projectName, double size ) => ProjectIcons.Build( source, projectName, size );

	/// <summary>Releases every decoded or pending project icon when the module stops.</summary>
	private void ReleaseIcons()
	{
		foreach ( var source in _icons.Values )
			source?.Dispose();
		_icons.Clear();
	}
}

/// <summary>Defines project icon discovery and rendering shared by Home and the project sidebar.</summary>
internal static class ProjectIcons
{
	/// <summary>Lists supported encoded image extensions in preference order.</summary>
	private static readonly string[] _extensions = [".png", ".jpg", ".jpeg", ".gif"];

	/// <summary>Defines the path to the PNG used as the s&box program icon, relative to its game directory.</summary>
	private static readonly string _sboxProgramIcon = Path.Combine( "addons", "tools", "assets", "window", "icon.png" );

	/// <summary>Lists explicit project icon paths without extensions.</summary>
	private static readonly string[] _explicitStems = [Path.Combine( ".lokn", "icon" )];

	/// <summary>Lists conventional fallback paths without extensions.</summary>
	private static readonly string[] _conventionalStems =
	[
		"icon",
		"logo",
		"favicon",
		Path.Combine( "assets", "icon" ),
		Path.Combine( "assets", "logo" ),
		Path.Combine( "public", "icon" ),
		Path.Combine( "public", "logo" ),
		Path.Combine( "public", "favicon" ),
		Path.Combine( "docs", "icon" ),
		Path.Combine( "docs", "logo" ),
	];

	/// <summary>Finds the first icon for a project using the installed development environments.</summary>
	internal static string? FindPath( string directory ) =>
		FindPath( directory, Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE" ) );

	/// <summary>Finds the first icon for a project: its explicit override, a recognized environment's icon, or a conventional image.</summary>
	internal static string? FindPath( string directory, string? sboxGameDirectory ) =>
		FindSupportedImage( directory, _explicitStems )
		?? FindSboxProgramIcon( directory, sboxGameDirectory )
		?? FindSupportedImage( directory, _conventionalStems );

	/// <summary>Finds the s&box program icon for an engine checkout or a folder containing an s&box project manifest.</summary>
	private static string? FindSboxProgramIcon( string directory, string? configured )
	{
		if ( FindProgramIcon( directory ) is { } direct )
			return direct;
		if ( FindProgramIcon( Path.Combine( directory, "game" ) ) is { } nested )
			return nested;
		if ( !ContainsSboxProject( directory ) )
			return null;

		if ( FindProgramIcon( configured ) is { } installed )
			return installed;
		if ( !string.IsNullOrWhiteSpace( configured ) && FindProgramIcon( Path.Combine( configured, "game" ) ) is { } configuredNested )
			return configuredNested;

		var programFiles = Environment.GetFolderPath( Environment.SpecialFolder.ProgramFilesX86 );
		return programFiles.Length == 0 ? null : FindProgramIcon( Path.Combine( programFiles, "Steam", "steamapps", "common", "sbox", "game" ) );
	}

	/// <summary>Gets the program icon under an s&box game directory, or null when it is not installed there.</summary>
	private static string? FindProgramIcon( string? gameDirectory )
	{
		if ( string.IsNullOrWhiteSpace( gameDirectory ) )
			return null;
		var path = Path.Combine( gameDirectory, _sboxProgramIcon );
		return File.Exists( path ) ? path : null;
	}

	/// <summary>Reports whether a directory contains a top-level s&box project manifest.</summary>
	private static bool ContainsSboxProject( string directory )
	{
		try
		{
			return Directory.EnumerateFiles( directory, "*.sbproj", SearchOption.TopDirectoryOnly ).Any();
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			return false;
		}
	}

	/// <summary>Finds the first supported image made from the ordered stems and extensions.</summary>
	private static string? FindSupportedImage( string directory, IReadOnlyList<string> stems )
	{
		foreach ( var stem in stems )
		{
			foreach ( var extension in _extensions )
			{
				var path = Path.Combine( directory, stem + extension );
				if ( File.Exists( path ) )
					return path;
			}
		}
		return null;
	}

	/// <summary>Builds a square project icon from its asynchronously loaded source.</summary>
	internal static Image Build( ImageSourceProvider source, string projectName, double size ) => new()
	{
		Source = source,
		Fit = ImageFit.Contain,
		Width = size,
		Height = size,
		FlexShrink = 0,
		BorderRadius = Theme.Current.Radius,
		AccessibilityName = $"{projectName} icon",
	};
}
