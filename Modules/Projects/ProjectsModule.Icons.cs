using System.Runtime.Versioning;
using Goo;
using Lokn.Core;
using Windows.Graphics.Imaging;

namespace Lokn.Modules.Projects;

/// <summary>Finds and asynchronously loads project icons from working directories and recognized development environments.</summary>
public sealed partial class ProjectsModule
{
	/// <summary>Defines the decoded icon size in physical pixels, enough for Home and the sidebar at high display densities.</summary>
	private const int IconPixels = 64;

	/// <summary>Caches one lazy image provider per project directory; null records a directory with no resolved icon.</summary>
	private readonly Dictionary<string, AsyncImageSource?> _icons = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Gets the shared lazy icon source for a project, or null when its directory has no supported icon or the project is on a paired host.</summary>
	/// <param name="project">The project.</param>
	/// <param name="hostId">The host the project is on, or null or <see cref="LoknHost.LocalId"/> for This PC.</param>
	/// <remarks>
	/// A paired host's directory names a folder on that host, never on This PC, so its projects show their letter icon. The source is released when
	/// this module stops, so a view resolves it again on each build.
	/// </remarks>
	public AsyncImageSource? GetIcon( ProjectInfo project, string? hostId )
	{
		if ( hostId is not null && hostId != LoknHost.LocalId )
			return null;
		if ( _icons.TryGetValue( project.Directory, out var known ) )
			return known;

		var path = ProjectIcons.FindPath( project.Directory );
		var source = path is null ? null : new AsyncImageSource( cancellation => ProjectIcons.LoadAsync( path, IconPixels, cancellation ) );
		_icons.Add( project.Directory, source );
		return source;
	}

	/// <summary>Builds a square project icon from its asynchronously loaded source.</summary>
	/// <param name="source">The icon from <see cref="GetIcon"/>.</param>
	/// <param name="projectName">The project's name, which names the icon for accessibility.</param>
	/// <param name="size">The icon's width and height in logical pixels.</param>
	public static Image BuildIcon( ImageSourceProvider source, string projectName, double size ) => ProjectIcons.Build( source, projectName, size );

	/// <summary>Builds a project's icon: its folder's image, else the emoji the user chose for it; null when it has neither.</summary>
	/// <param name="project">The project.</param>
	/// <param name="hostId">The host the project is on, or null or <see cref="LoknHost.LocalId"/> for This PC.</param>
	/// <param name="size">The icon's width and height in logical pixels.</param>
	public Blob? BuildProjectIcon( ProjectInfo project, string? hostId, double size ) =>
		GetIcon( project, hostId ) is { } source ? BuildIcon( source, project.Name, size )
		: project.Icon is { Length: > 0 } emoji ? ProjectIcons.BuildEmoji( emoji, project.Name, size )
		: null;

	/// <summary>Reports whether a project's folder gives it an icon image, which takes the place of a chosen emoji.</summary>
	public bool HasImageIcon( ProjectInfo project, string? hostId ) => GetIcon( project, hostId ) is not null;

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
	private static readonly string[] _extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

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

	/// <summary>Loads a bounded project icon, using Windows' image decoder for WebP and Goo for its native formats.</summary>
	internal static Task<ImageSource> LoadAsync( string path, int maximumPixels, CancellationToken cancellation )
	{
		if ( !path.EndsWith( ".webp", StringComparison.OrdinalIgnoreCase ) )
		{
			return Task.Run( () => ImageSource.LoadThumbnail( path, maximumPixels, maximumPixels, cancellation ), cancellation );
		}
		if ( !OperatingSystem.IsWindowsVersionAtLeast( 10, 0, 10240 ) )
			throw new PlatformNotSupportedException( "WebP project icons need Windows 10 or later." );
		return LoadWebpAsync( path, maximumPixels, cancellation );
	}

	/// <summary>Decodes a WebP image to bounded premultiplied RGBA pixels through Windows Imaging Component.</summary>
	[SupportedOSPlatform( "windows10.0.10240.0" )]
	private static async Task<ImageSource> LoadWebpAsync( string path, int maximumPixels, CancellationToken cancellation )
	{
		using var input = File.OpenRead( path );
		using var stream = input.AsRandomAccessStream();
		var decoder = await BitmapDecoder.CreateAsync( stream ).AsTask( cancellation );
		if ( decoder.PixelWidth == 0 || decoder.PixelHeight == 0 )
			throw new InvalidDataException( "The WebP icon has no pixels." );

		var scale = Math.Min( 1, Math.Min( maximumPixels / (double)decoder.PixelWidth, maximumPixels / (double)decoder.PixelHeight ) );
		var width = Math.Max( 1, (uint)Math.Round( decoder.PixelWidth * scale ) );
		var height = Math.Max( 1, (uint)Math.Round( decoder.PixelHeight * scale ) );
		var transform = new BitmapTransform
		{
			ScaledWidth = width,
			ScaledHeight = height,
			InterpolationMode = BitmapInterpolationMode.Fant,
		};
		var data = await decoder.GetPixelDataAsync( BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied, transform,
			ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage ).AsTask( cancellation );
		return ImageSource.Transfer( (int)width, (int)height, data.DetachPixelData(), static () => { } );
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

	/// <summary>Defines a chosen emoji's font size as a share of the icon's size, so its ink fills about as much as an image does.</summary>
	private const double EmojiScale = 0.8;

	/// <summary>Builds a project's chosen emoji centered in a square of the icon's size.</summary>
	internal static Container BuildEmoji( string emoji, string projectName, double size ) => new()
	{
		Width = size,
		Height = size,
		FlexShrink = 0,
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		AccessibilityName = $"{projectName} icon",
		Children =
		[
			new Text
			{
				Content = emoji,
				FontFamily = ViewStyle.FontFamily,
				FontSize = size * EmojiScale,
				TextWrap = TextWrap.NoWrap,
			},
		],
	};
}
