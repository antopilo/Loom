using System.Globalization;
using Goo;
using Lokn.Core;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds the front of Home and of an empty chat, as a project's overview sets its page: a kicker line in small capitals, the day's painting
/// with Loom's mark over it, and the painting's credit; Home also sets the date and the time along its margins.</summary>
/// <remarks>Each panel owns one front: it decodes the painting off the UI thread, ticks on the minute until the panel closes, and disposes the painting then.</remarks>
internal sealed class HomeFront
{
	/// <summary>Defines the widest the front grows, in logical pixels.</summary>
	public const int ColumnWidth = 680;

	/// <summary>Defines a margin's width, in logical pixels.</summary>
	public const int RailWidth = 56;

	/// <summary>Defines the room the date and the time take along a margin, in logical pixels.</summary>
	private const int RailTextLength = 320;

	/// <summary>Defines the mark's width and height over the painting, in logical pixels.</summary>
	private const int MarkSize = 84;

	/// <summary>Defines how dark the shade behind the mark is at its middle, as black's opacity.</summary>
	private const double MarkShade = 0.3;

	/// <summary>Defines Loom's mark, <c>assets\brand\loom-mark.svg</c>, in light ink for the painting: the ring is drawn as two arcs that stop short
	/// of the weft, as the brand file's mask cuts it.</summary>
	private const string MarkMarkup = """
		<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" fill="none" stroke="#f6f1e8" stroke-linecap="round" stroke-width="5">
		<path d="M49.58 22.46A20 20 0 0 0 12.59 27.16"/>
		<path d="M14.02 40.77A20 20 0 0 0 51.6 35.99"/>
		<path d="M8 36C16 22 24 46 32 32S48 18 56 28"/>
		</svg>
		""";

	/// <summary>Holds Loom's mark, parsed once.</summary>
	private static readonly VectorAsset Mark = Goo.Svg.Svg.Parse( MarkMarkup );

	/// <summary>Holds the panel the front is in.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the time the front shows, set on each minute.</summary>
	private readonly State<DateTimeOffset> _now = new( DateTimeOffset.Now );

	/// <summary>Stores the day's painting once decoded, or null before; the front owns and disposes it.</summary>
	private readonly State<ImageSource?> _painting = new( null );

	/// <summary>Stores the day whose painting is shown or decoding.</summary>
	private DayOfWeek? _paintingDay;

	/// <summary>Creates the front of a panel: decodes the day's painting and starts the clock.</summary>
	public HomeFront( PanelContext context )
	{
		_context = context;
		UpdatePainting();
		_ = TickAsync();
		context.Lifetime.Register( () => context.Post( () => _painting.Value?.Dispose() ) );
	}

	/// <summary>Builds the kicker line, the painting with the mark over it, and its credit.</summary>
	/// <param name="paintingHeight">The painting's height, in logical pixels.</param>
	public Blob Build( double paintingHeight )
	{
		var theme = Theme.Current;
		var now = _now.Value;
		var painting = DailyPainting.ForDay( now.DayOfWeek );
		Text Kicker( string key, string text ) => new()
		{
			Key = key,
			Content = text,
			FontSize = 10,
			FontWeight = 600,
			LetterSpacing = 1.6,
			Color = theme.Inactive,
			TextMaxLines = 1,
		};
		Blob[] picture = _painting.Value is { } source
			?
			[
				new Image { Key = "painting", Source = source, Fit = ImageFit.Cover, Position = PositionType.Absolute, Left = 0, Top = 0, Right = 0, Bottom = 0 },
				new Container
				{
					Key = "shade",
					Position = PositionType.Absolute,
					Left = 0,
					Top = 0,
					Right = 0,
					Bottom = 0,
					BackgroundGradient = new RadialGradient( Color.Black.WithAlpha( MarkShade ), Color.Black.WithAlpha( MarkShade / 2 ), Color.Black.WithAlpha( 0 ) ),
				},
			]
			: [];

		return new Column
		{
			Key = "home-front",
			Width = Length.Percent( 100 ),
			MaxWidth = ColumnWidth,
			FlexShrink = 0,
			Children =
			[
				new Row
				{
					Key = "kicker",
					JustifyContent = JustifyContent.SpaceBetween,
					Padding = new Edges( 0, 0, 8, 0 ),
					Children =
					[
						Kicker( "brand", "LOOM" ),
						Kicker( "edition", $"{now.ToString( "dddd", CultureInfo.CurrentCulture ).ToUpper( CultureInfo.CurrentCulture )} EDITION" ),
						Kicker( "number", $"NO. {now.DayOfYear}" ),
					],
				},
				new Container { Key = "rule", Height = 1, BackgroundColor = theme.Text.WithAlpha( 0.22 ) },
				new Container
				{
					Key = "art",
					Height = paintingHeight,
					Margin = new Edges( 14, 0, 0, 0 ),
					BorderRadius = 3,
					Overflow = Overflow.Hidden,
					BackgroundColor = theme.Text.WithAlpha( 0.045 ),
					AlignItems = AlignItems.Center,
					JustifyContent = JustifyContent.Center,
					AccessibilityRole = AccessibilityRole.Group,
					AccessibilityName = $"Loom. {painting.Credit}",
					Children =
					[
						.. picture,
						new Container { Key = "mark", Width = MarkSize, Height = MarkSize, Children = [Mark.Render( ShapeFit.Contain )] },
					],
				},
				new Text
				{
					Key = "credit",
					Content = painting.Credit,
					FontFamily = ViewStyle.CodeFontFamily,
					FontSize = 10,
					LineHeight = 1.5,
					Color = theme.Inactive,
					TextAlign = TextAlign.Right,
					TextWrap = TextWrap.Wrap,
					Padding = new Edges( 6, 0, 0, 0 ),
				},
			],
		};
	}

	/// <summary>Builds one margin along the panel's side, centered on its height: the date on the left, turned to read upward, or the time on the right.</summary>
	/// <param name="isLeft">Whether the margin is the left one, with the date.</param>
	public Blob BuildRail( bool isLeft )
	{
		var now = _now.Value;
		var rail = new Container
		{
			Key = isLeft ? "rail-date" : "rail-time",
			Position = PositionType.Absolute,
			Top = 0,
			Bottom = 0,
			Width = RailWidth,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children =
			[
				new Text
				{
					Content = isLeft ? now.ToString( "d MMM yyyy", CultureInfo.CurrentCulture ).ToUpper( CultureInfo.CurrentCulture ) : now.ToString( "t", CultureInfo.CurrentCulture ),
					Width = RailTextLength,
					FlexShrink = 0,
					TextAlign = TextAlign.Center,
					TextMaxLines = 1,
					FontFamily = SidebarRows.SerifFontFamily,
					FontStyle = FontStyle.Italic,
					FontSize = 26,
					Color = Theme.Current.Inactive,
					Transform = new PanelTransform { Rotate = isLeft ? -90 : 90 },
				},
			],
		};
		if ( isLeft )
			rail.Left = 0;
		else
			rail.Right = 0;
		return rail;
	}

	/// <summary>Sets the time on each minute until the panel closes, changing the painting at midnight.</summary>
	private async Task TickAsync()
	{
		try
		{
			while ( true )
			{
				var now = DateTimeOffset.Now;
				await Task.Delay( TimeSpan.FromMilliseconds( 60_050 - now.Second * 1000 - now.Millisecond ), _context.Lifetime );
				_context.Post( () =>
				{
					_now.Value = DateTimeOffset.Now;
					UpdatePainting();
				} );
			}
		}
		catch ( OperationCanceledException )
		{
			// The panel closed.
		}
	}

	/// <summary>Decodes the painting of the shown day off the UI thread when the day changed.</summary>
	private void UpdatePainting()
	{
		var day = _now.Value.DayOfWeek;
		if ( _paintingDay == day )
			return;
		_paintingDay = day;
		_ = LoadPaintingAsync( DailyPainting.ForDay( day ), day );
	}

	/// <summary>Decodes a painting and shows it, unless the day or the panel moved on meanwhile; the painting it replaces is released after the panel
	/// rebuilt without it.</summary>
	private async Task LoadPaintingAsync( Painting painting, DayOfWeek day )
	{
		ImageSource source;
		try
		{
			source = await Task.Run( () => DailyPainting.Decode( painting ), _context.Lifetime );
		}
		// A painting that cannot show leaves the plain card under the mark.
		catch ( Exception error ) when ( error is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException )
		{
			return;
		}

		_context.Post( () =>
		{
			if ( _context.Lifetime.IsCancellationRequested || _paintingDay != day )
			{
				source.Dispose();
				return;
			}
			var previous = _painting.Value;
			_painting.Value = source;
			if ( previous is not null )
				_context.Post( previous.Dispose );
		} );
	}
}
