using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Loads commit authors' pictures: a GitHub account's for its no-reply address, else the Gravatar of the email, kept on disk between runs.</summary>
/// <remarks>
/// <see cref="Get"/> answers from memory on the UI thread and starts a load the first time it sees an address; a finished load posts back and
/// bumps <see cref="_version"/>, which every view that asked observes, so its avatars redraw. An address without a picture is remembered too,
/// so it is asked for once a day at most. Nothing is fetched while <see cref="GitModule.Avatars"/> is off.
/// </remarks>
internal sealed class GitAvatars
{
	/// <summary>Defines the picture size asked for, in pixels: twice the largest avatar, for sharp high-density displays.</summary>
	private const int PictureSize = 80;

	/// <summary>Defines how long a stored picture is used before it is asked for again.</summary>
	private static readonly TimeSpan PictureAge = TimeSpan.FromDays( 7 );

	/// <summary>Defines how long an address without a picture waits before it is asked for again.</summary>
	private static readonly TimeSpan MissingAge = TimeSpan.FromDays( 1 );

	/// <summary>Matches a GitHub no-reply address, with the account number when it has one.</summary>
	private static readonly Regex NoReply = new( @"^(?:(?<id>\d+)\+)?(?<login>[^@]+)@users\.noreply\.github\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

	/// <summary>Fetches the pictures; one client for every load.</summary>
	private static readonly HttpClient Http = BuildClient();

	/// <summary>Bumps when a picture arrives, so the views that asked redraw.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores loaded pictures by address; null marks an address without one.</summary>
	private readonly Dictionary<string, ImageSource?> _pictures = new( StringComparer.Ordinal );

	/// <summary>Stores the addresses whose load runs.</summary>
	private readonly HashSet<string> _loading = new( StringComparer.Ordinal );

	/// <summary>Limits how many pictures load at once.</summary>
	private readonly SemaphoreSlim _gate = new( 4 );

	/// <summary>Stores the module's context, which posts results back and cancels loads when the module stops.</summary>
	private readonly ModuleContext _context;

	/// <summary>Stores the folder the pictures are kept in, in the data folder.</summary>
	private readonly string _folder = LoknPaths.Combine( "avatars" );

	/// <summary>Creates the loader for a running module.</summary>
	internal GitAvatars( ModuleContext context ) => _context = context;

	/// <summary>Gets an address's picture, or null while it loads, when it has none, or while avatars are off; the calling view redraws when it arrives.</summary>
	internal ImageSource? Get( string email )
	{
		_ = _version.Value;
		if ( !_context.App.Settings.Get( GitModule.Avatars ) )
			return null;

		var key = email.Trim().ToLowerInvariant();
		if ( key.Length == 0 || !key.Contains( '@' ) )
			return null;
		if ( _pictures.TryGetValue( key, out var picture ) )
			return picture;
		if ( _loading.Add( key ) )
			_ = Task.Run( () => LoadAsync( key ) );
		return null;
	}

	/// <summary>Releases every picture when the module stops.</summary>
	internal void Dispose()
	{
		foreach ( var picture in _pictures.Values )
			picture?.Dispose();
		_pictures.Clear();
	}

	/// <summary>Reads an address's picture from disk while it is fresh, else from the web, then decodes it on the UI thread.</summary>
	private async Task LoadAsync( string key )
	{
		byte[]? bytes = null;
		var token = _context.Lifetime;
		try
		{
			await _gate.WaitAsync( token ).ConfigureAwait( false );
			try
			{
				bytes = await ReadAsync( key, token ).ConfigureAwait( false );
			}
			finally
			{
				_gate.Release();
			}
		}
		catch ( OperationCanceledException )
		{
			return;
		}
		catch ( Exception error ) when ( error is HttpRequestException or IOException or UnauthorizedAccessException )
		{
			// Offline or blocked: the initials stay, and the next run asks again.
		}

		_context.Post( () => Apply( key, bytes ) );
	}

	/// <summary>Keeps a loaded picture, or marks the address as having none, and redraws the views that asked.</summary>
	private void Apply( string key, byte[]? bytes )
	{
		_loading.Remove( key );
		ImageSource? picture = null;
		if ( bytes is { Length: > 0 } )
		{
			try
			{
				picture = ImageSource.Decode( bytes );
			}
			catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException )
			{
			}
		}

		_pictures[key] = picture;
		_version.Value++;
	}

	/// <summary>Reads a stored picture while it is fresh, or fetches and stores it; null when the address has none.</summary>
	private async Task<byte[]?> ReadAsync( string key, CancellationToken token )
	{
		var name = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( key ) ) ).ToLowerInvariant();
		var picturePath = Path.Combine( _folder, name + ".img" );
		var missingPath = Path.Combine( _folder, name + ".none" );
		if ( File.Exists( picturePath ) && DateTime.UtcNow - File.GetLastWriteTimeUtc( picturePath ) < PictureAge )
			return await File.ReadAllBytesAsync( picturePath, token ).ConfigureAwait( false );
		if ( File.Exists( missingPath ) && DateTime.UtcNow - File.GetLastWriteTimeUtc( missingPath ) < MissingAge )
			return null;

		using var response = await Http.GetAsync( ComputeUrl( key ), token ).ConfigureAwait( false );
		Directory.CreateDirectory( _folder );
		if ( !response.IsSuccessStatusCode )
		{
			await File.WriteAllBytesAsync( missingPath, [], token ).ConfigureAwait( false );
			return null;
		}

		var bytes = await response.Content.ReadAsByteArrayAsync( token ).ConfigureAwait( false );
		await File.WriteAllBytesAsync( picturePath, bytes, token ).ConfigureAwait( false );
		File.Delete( missingPath );
		return bytes;
	}

	/// <summary>Gets where an address's picture is: its GitHub account's for a no-reply address, else its Gravatar, which answers 404 when there is none.</summary>
	internal static string ComputeUrl( string key )
	{
		if ( NoReply.Match( key ) is { Success: true } match )
			return match.Groups["id"].Success
				? $"https://avatars.githubusercontent.com/u/{match.Groups["id"].Value}?s={PictureSize}&v=4"
				: $"https://github.com/{Uri.EscapeDataString( match.Groups["login"].Value )}.png?size={PictureSize}";

		var hash = Convert.ToHexString( MD5.HashData( Encoding.UTF8.GetBytes( key ) ) ).ToLowerInvariant();
		return $"https://www.gravatar.com/avatar/{hash}?s={PictureSize}&d=404";
	}

	/// <summary>Builds the client, named after the app as GitHub asks.</summary>
	private static HttpClient BuildClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds( 15 ) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd( "Loom/1.0" );
		return client;
	}
}

/// <summary>Describes one author's avatar.</summary>
/// <param name="Avatars">The loader that finds the picture.</param>
/// <param name="Name">The author's name, whose initials show until or unless a picture does.</param>
/// <param name="Email">The author's address, which finds the picture.</param>
/// <param name="Size">The avatar's diameter in logical pixels.</param>
/// <param name="Ring">The ring around it and the fill behind the initials, such as the branch's lane color.</param>
internal sealed record GitAvatarInput( GitAvatars Avatars, string Name, string Email, double Size, Color Ring );

/// <summary>Draws a round avatar: the author's initials on a colored disc, and their picture fading in over them once it loads.</summary>
internal sealed class GitAvatar : GuardedCell<GitAvatarInput>
{
	/// <summary>Defines how long a picture takes to fade in, in milliseconds.</summary>
	private const double FadeMs = 220;

	/// <summary>Tracks the picture's opacity.</summary>
	private Anim<double>? _fade;

	/// <summary>Reports whether the avatar was built without a picture, so one that arrives later fades in rather than popping in.</summary>
	private bool _wasEmpty;

	/// <summary>Reports whether the picture has shown.</summary>
	private bool _hasPicture;

	/// <summary>Prepares the picture's fade.</summary>
	protected override void InputChanged( GitAvatarInput input ) => _fade ??= AnimateValue( 1.0 );

	/// <summary>Builds the disc with the initials, and the picture over it when it has loaded.</summary>
	protected override Blob BuildView( GitAvatarInput input )
	{
		var size = input.Size;
		var picture = input.Avatars.Get( input.Email );
		var children = new List<Blob>
		{
			new Text
			{
				Content = ComputeInitials( input.Name ),
				FontSize = Math.Max( 7, size * 0.42 ),
				FontWeight = 700,
				LineHeight = 1,
				Color = Theme.Current.Panel,
				TextWrap = TextWrap.NoWrap,
			},
		};
		if ( picture is null )
			_wasEmpty = true;
		else
		{
			if ( !_hasPicture && _wasEmpty && _fade is { } fade )
			{
				fade.Set( 0 );
				// Goo refuses Cell.Post during Build and ends the process; the window's queue runs the fade after this frame's build.
				var lifetime = Lifetime;
				Host.Post( () =>
				{
					if ( !lifetime.IsCancellationRequested )
						fade.To( 1.0, Motion.Tween( FadeMs, Easing.EaseOut ) );
				} );
			}
			_hasPicture = true;
			children.Add( new Container
			{
				Position = PositionType.Absolute,
				Left = 0,
				Top = 0,
				Right = 0,
				Bottom = 0,
				OpacityAnim = _fade,
				Children = [new Image { Source = picture, Fit = ImageFit.Cover, Width = Length.Percent( 100 ), Height = Length.Percent( 100 ) }],
			} );
		}

		return new Container
		{
			Width = size,
			Height = size,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = size / 2,
			Overflow = Overflow.Hidden,
			BackgroundColor = input.Ring,
			BorderWidth = size >= 24 ? 0 : 1.5,
			BorderColor = input.Ring,
			AccessibilityRole = AccessibilityRole.Image,
			AccessibilityName = input.Name,
			Children = children,
		};
	}

	/// <summary>Gets up to two initials: the first letters of the first and last words, or the first two letters of a single word.</summary>
	internal static string ComputeInitials( string name )
	{
		var words = name.Split( [' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries );
		if ( words.Length == 0 )
			return "?";
		if ( words.Length == 1 )
			return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();
		return string.Concat( char.ToUpperInvariant( words[0][0] ), char.ToUpperInvariant( words[^1][0] ) );
	}
}

/// <summary>Describes content that fades and rises in once when it mounts.</summary>
/// <param name="Content">The content.</param>
/// <param name="Grow">Whether the wrapper fills its parent's remaining height, as a list does.</param>
internal sealed record GitRevealInput( Blob Content, bool Grow = true );

/// <summary>Shows content that enters once, fading in while it rises a few pixels into place, such as a newly selected commit or file.</summary>
/// <remarks>Mount it under a key that changes with what it shows; a new key plays the entrance again.</remarks>
internal sealed class GitReveal : GuardedCell<GitRevealInput>
{
	/// <summary>Defines the entrance's length in milliseconds.</summary>
	private const double EnterMs = 200;

	/// <summary>Defines how far the content rises, in logical pixels.</summary>
	private const double Rise = 6;

	/// <summary>Tracks the entrance's opacity.</summary>
	private Anim<double>? _opacity;

	/// <summary>Tracks the entrance's offset.</summary>
	private Anim<Point>? _offset;

	/// <summary>Starts the content hidden and lowered.</summary>
	protected override void InputChanged( GitRevealInput input )
	{
		_opacity ??= AnimateValue( 0.0 );
		_offset ??= AnimateValue( new Point { X = 0, Y = Rise } );
	}

	/// <summary>Plays the entrance once the content has its place.</summary>
	protected override void Mounted()
	{
		var spec = Motion.Tween( EnterMs, Easing.EaseOut );
		_opacity?.To( 1.0, spec );
		_offset?.To( new Point { X = 0, Y = 0 }, spec );
	}

	/// <summary>Builds a wrapper carrying the fade and offset around the content.</summary>
	protected override Blob BuildView( GitRevealInput input ) => new Container
	{
		FlexGrow = input.Grow ? 1 : 0,
		MinWidth = 0,
		MinHeight = 0,
		OpacityAnim = _opacity,
		TranslateAnim = _offset,
		Children = [input.Content],
	};
}
