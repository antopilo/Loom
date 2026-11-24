using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Shows the host's windows and screens as tiles with a live preview and the app's icon, applications and screens on separate pages.</summary>
public sealed partial class StreamContent
{
	/// <summary>Defines the narrowest tile in logical pixels; tiles grow to fill each row.</summary>
	private const double TileWidth = 180;

	/// <summary>Defines how many invisible tiles end the grid, so a short last row keeps the width of the rows above.</summary>
	private const int TileFillers = 8;

	/// <summary>Defines the width-to-height ratio of a tile's preview.</summary>
	private const double PreviewRatio = 16.0 / 10;

	/// <summary>Defines how many previews are read at once.</summary>
	private const int PreviewWorkers = 3;

	/// <summary>Defines the side of a tile's app icon in logical pixels.</summary>
	private const double IconSide = 20;

	/// <summary>Holds what the picker knows of one window's preview; touched on the UI thread only.</summary>
	private sealed class Preview
	{
		/// <summary>Gets or sets the capture, owned by this preview, or null.</summary>
		public ImageSource? Picture
		{
			get; set;
		}

		/// <summary>Gets or sets whether the window is minimized, so it has no capture.</summary>
		public bool IsMinimized
		{
			get; set;
		}

		/// <summary>Gets or sets whether the host answered, with a capture or without.</summary>
		public bool IsLoaded
		{
			get; set;
		}
	}

	/// <summary>Stores each listed window's preview by handle.</summary>
	private readonly Dictionary<long, Preview> _previews = [];

	/// <summary>Stores each process's icon, owned here; null while it is read or when it has none.</summary>
	private readonly Dictionary<string, ImageSource?> _icons = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Cancels the previews being read, or null.</summary>
	private CancellationTokenSource? _previewing;

	/// <summary>Tracks whether the picker shows screens rather than applications.</summary>
	private bool _isShowingScreens;

	/// <summary>Stores the text the shown windows must contain, in their title or process.</summary>
	private string _filter = "";

	/// <summary>Builds the heading with the filter and refresh, the tiles of the page shown, and the switch between applications and screens.</summary>
	private Container BuildPicker( Theme theme )
	{
		var windows = _windows ?? [];
		var screens = windows.Count( window => ScreenCatalog.IsScreen( window.Hwnd ) );
		var isShowingScreens = _isShowingScreens;
		var shown = windows.Where( window => ScreenCatalog.IsScreen( window.Hwnd ) == isShowingScreens && Matches( window ) ).ToList();

		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = "Stream a window", FontSize = HeadingSize, FlexGrow = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, },
					new TextEntry
					{
						Key = "filter",
						Value = _filter,
						Controlled = true,
						Placeholder = "Filter…",
						AccessibilityName = "Filter windows",
						Width = 200,
						MinWidth = 0,
						FlexShrink = 1,
						Height = theme.ControlHeight,
						Padding = new Edges( 0, theme.Spacing ),
						BackgroundColor = theme.Raised,
						BorderColor = theme.Border,
						BorderWidth = theme.BorderWidth,
						BorderRadius = theme.Radius,
						FocusStyle = new Style { BorderColor = theme.Accent },
						OnChange = text => _context.Run( () =>
						{
							_filter = text;
							UpdateVersion();
						} ),
					},
					theme.BuildButton( _isListing ? "Refreshing…" : "Refresh", () => _context.Run( UpdateList ), isEnabled: !_isListing ),
				],
			},
		};

		if ( _status.Length > 0 )
			children.Add( new Text { Content = _status, Color = theme.Error, Selectable = true, } );

		var empty = _windows is null ? (_isListing ? "Looking for windows…" : "")
			: shown.Count > 0 ? ""
			: _filter.Length > 0 ? "Nothing matches the filter."
			: isShowingScreens ? (_source is LocalStreamSource ? "Loom cannot stream the screen of the PC it runs on. Connect to another PC in Remote access to stream its screens." : "The host shows no screens.")
			: "The host shows no windows.";
		if ( empty.Length > 0 )
			children.Add( new Text { Content = empty, Color = theme.Muted, } );

		var tiles = new List<Blob>( shown.Count + TileFillers );
		foreach ( var window in shown )
			tiles.Add( BuildTile( window, theme ) );
		for ( var i = 0; i < TileFillers; i++ )
			tiles.Add( new Container { Key = "filler:" + i, FlexGrow = 1, FlexBasis = TileWidth, Height = 0, } );

		children.Add( new Container
		{
			FlexGrow = 1,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			Children = [new Row { FlexWrap = FlexWrap.Wrap, Gap = theme.Spacing * 1.5, Children = tiles, }],
		} );

		// Always shown, so the screens page is found even when the host offers none; that page then says why.
		children.Add( new Container { Height = theme.BorderWidth, BackgroundColor = theme.Divider, } );
		children.Add( new Row
		{
			JustifyContent = JustifyContent.Center,
			Children =
			[
				theme.BuildSegments( "Sources", [(false, $"Applications · {windows.Count - screens}"), (true, $"Screens · {screens}")], isShowingScreens, show => _context.Run( () =>
				{
					_isShowingScreens = show;
					UpdateVersion();
				} ) ),
			],
		} );

		return new Container
		{
			FlexGrow = 1,
			MinHeight = 0,
			Padding = ContentPadding,
			Gap = theme.Spacing * 1.5,
			Children = children,
		};
	}

	/// <summary>Reports whether a window's title or process contains the filter.</summary>
	private bool Matches( CaptureWindow window ) =>
		_filter.Length == 0 || window.Title.Contains( _filter, StringComparison.OrdinalIgnoreCase ) || window.Process.Contains( _filter, StringComparison.OrdinalIgnoreCase );

	/// <summary>Builds one window as a tile: its preview, then its icon, title, process and size.</summary>
	private Button BuildTile( CaptureWindow window, Theme theme )
	{
		void Start() => _context.Run( () => UpdateStart( window, isChosen: true ) );
		var isScreen = ScreenCatalog.IsScreen( window.Hwnd );
		var preview = _previews.GetValueOrDefault( window.Hwnd );
		var details = isScreen ? $"Whole screen · {window.Width}×{window.Height}"
			: window.Process.Length > 0 ? $"{window.Process} · {window.Width}×{window.Height}" : $"{window.Width}×{window.Height}";
		return new Button
		{
			Key = "window:" + window.Hwnd,
			FlexGrow = 1,
			FlexBasis = TileWidth,
			MinWidth = 0,
			Padding = theme.Spacing / 2,
			Gap = theme.Spacing * 0.75,
			BorderRadius = theme.Radius + 2,
			BorderWidth = 2,
			BorderColor = Color.Transparent,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"Stream {window.Title}, {details}",
			OnClick = Start,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Start ),
				new( Key.Space, KeyModifiers.None, Start ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover, },
			FocusStyle = new Style { BorderColor = theme.Accent, },
			Children =
			[
				BuildPreview( preview, theme ),
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Children =
					[
						BuildIcon( window, isScreen, theme ),
						new Container
						{
							FlexGrow = 1,
							MinWidth = 0,
							Children =
							[
								new Text { Content = window.Title, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, },
								new Text { Content = details, Color = theme.Muted, FontSize = ViewStyle.SmallSize, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, },
							],
						},
					],
				},
			],
		};
	}

	/// <summary>Builds a tile's preview: the capture fitted in a 16:10 box, else why there is none.</summary>
	private static Container BuildPreview( Preview? preview, Theme theme ) => new()
	{
		AspectRatio = PreviewRatio,
		Width = Length.Percent( 100 ),
		BorderRadius = theme.Radius,
		BackgroundColor = theme.Canvas,
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		Overflow = Overflow.Hidden,
		Children =
		[
			preview?.Picture is { } picture
				? new Image { Source = picture, Fit = ImageFit.Contain, Width = Length.Percent( 100 ), Height = Length.Percent( 100 ), }
				: new Text
				{
					Content = preview is null || !preview.IsLoaded ? "Loading…" : preview.IsMinimized ? "Minimized" : "No preview",
					Color = theme.Muted,
					FontSize = ViewStyle.SmallSize,
				},
		],
	};

	/// <summary>Builds a tile's icon: the app's own, a screen's number, or the process's first letter while none is known.</summary>
	private Blob BuildIcon( CaptureWindow window, bool isScreen, Theme theme )
	{
		if ( !isScreen && _icons.GetValueOrDefault( window.Process ) is { } icon )
			return new Image { Source = icon, Fit = ImageFit.Contain, Width = IconSide, Height = IconSide, FlexShrink = 0, };

		var name = window.Process.Length > 0 ? window.Process : window.Title;
		return new Container
		{
			Width = IconSide,
			Height = IconSide,
			FlexShrink = 0,
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Hover,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children =
			[
				new Text
				{
					Content = isScreen ? (-window.Hwnd).ToString( System.Globalization.CultureInfo.InvariantCulture ) : name.Length > 0 ? char.ToUpperInvariant( name[0] ).ToString() : "?",
					FontSize = ViewStyle.SmallSize,
					FontWeight = 600,
					Color = theme.Accent,
				},
			],
		};
	}

	/// <summary>Reads fresh previews of a new list, those of the page shown first, and forgets the windows that are gone.</summary>
	private void UpdatePreviews( IReadOnlyList<CaptureWindow> windows )
	{
		_previewing?.Cancel();
		_previewing?.Dispose();
		var previewing = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_previewing = previewing;

		var listed = windows.Select( window => window.Hwnd ).ToHashSet();
		foreach ( var hwnd in _previews.Keys.Where( hwnd => !listed.Contains( hwnd ) ).ToList() )
		{
			_previews[hwnd].Picture?.Dispose();
			_ = _previews.Remove( hwnd );
		}

		// A process without an icon, or whose read was stopped, is asked again.
		foreach ( var process in _icons.Where( pair => pair.Value is null ).Select( pair => pair.Key ).ToList() )
			_ = _icons.Remove( process );

		var queue = new Queue<(long Hwnd, bool WithIcon)>();
		foreach ( var window in windows.OrderBy( window => ScreenCatalog.IsScreen( window.Hwnd ) != _isShowingScreens ) )
		{
			_ = _previews.TryAdd( window.Hwnd, new Preview() );
			queue.Enqueue( (window.Hwnd, window.Process.Length > 0 && _icons.TryAdd( window.Process, null )) );
		}

		for ( var i = 0; i < PreviewWorkers; i++ )
			UpdateNextPreview( queue, previewing.Token );
	}

	/// <summary>Asks the source for the next preview in the queue; on the UI thread, as every source call is.</summary>
	private void UpdateNextPreview( Queue<(long Hwnd, bool WithIcon)> queue, CancellationToken cancellation )
	{
		if ( cancellation.IsCancellationRequested || !queue.TryDequeue( out var next ) )
			return;

		Task<WindowPreview> request;
		try
		{
			request = _source.PreviewAsync( next.Hwnd, next.WithIcon, cancellation );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			request = Task.FromException<WindowPreview>( error );
		}

		_ = ReadPreviewAsync( request, next.Hwnd, queue, cancellation );
	}

	/// <summary>Waits for a preview, decodes it off the UI thread and applies it on the UI thread; a host without previews, such as one before Remote 2.13.0, leaves the tile without one.</summary>
	private async Task ReadPreviewAsync( Task<WindowPreview> request, long hwnd, Queue<(long Hwnd, bool WithIcon)> queue, CancellationToken cancellation )
	{
		WindowPreview? preview;
		try
		{
			preview = await request.ConfigureAwait( false );
		}
		catch ( OperationCanceledException )
		{
			return;
		}
		catch
		{
			preview = null;
		}

		ImageSource? picture = null;
		ImageSource? icon = null;
		try
		{
			if ( preview?.Jpeg is { Length: > 0 } jpeg )
				picture = ImageSource.Decode( jpeg );
			if ( preview?.Icon is { } pixels && pixels.Length == WindowPreviews.IconSize * WindowPreviews.IconSize * 4 )
				icon = new ImageSource( WindowPreviews.IconSize, WindowPreviews.IconSize, pixels );
		}
		catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException or ImageLimitException )
		{
		}

		if ( !_context.Post( () => ApplyPreview( hwnd, preview, picture, icon, queue, cancellation ) ) )
		{
			picture?.Dispose();
			icon?.Dispose();
		}
	}

	/// <summary>Shows a preview on its tile and its icon on every tile of its process, then asks for the next.</summary>
	private void ApplyPreview( long hwnd, WindowPreview? preview, ImageSource? picture, ImageSource? icon, Queue<(long Hwnd, bool WithIcon)> queue, CancellationToken cancellation )
	{
		if ( cancellation.IsCancellationRequested || !_previews.TryGetValue( hwnd, out var entry ) )
		{
			picture?.Dispose();
			icon?.Dispose();
			return;
		}

		entry.Picture?.Dispose();
		entry.Picture = picture;
		entry.IsMinimized = preview?.IsMinimized == true;
		entry.IsLoaded = true;
		if ( icon is not null )
		{
			if ( _windows?.FirstOrDefault( window => window.Hwnd == hwnd ) is { Process.Length: > 0 } window && _icons.TryGetValue( window.Process, out var known ) && known is null )
				_icons[window.Process] = icon;
			else
				icon.Dispose();
		}

		UpdateVersion();
		UpdateNextPreview( queue, cancellation );
	}

	/// <summary>Stops reading previews.</summary>
	private void StopPreviews()
	{
		_previewing?.Cancel();
		_previewing?.Dispose();
		_previewing = null;
	}

	/// <summary>Stops reading previews and lets go of every preview and icon.</summary>
	private void ClearPreviews()
	{
		StopPreviews();
		foreach ( var preview in _previews.Values )
			preview.Picture?.Dispose();
		_previews.Clear();
		foreach ( var icon in _icons.Values )
			icon?.Dispose();
		_icons.Clear();
	}
}
