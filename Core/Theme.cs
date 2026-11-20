using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Goo;

namespace Lokn.Core;

/// <summary>Defines the application's shared colors, typography and control geometry.</summary>
/// <remarks>Read <see cref="Current"/> inside <c>Build()</c> so the view rebuilds when the theme changes.</remarks>
public sealed record Theme
{
	/// <summary>Gets the built-in dark theme used for any token a theme file leaves out.</summary>
	public static Theme Default { get; } = new();

	/// <summary>Tracks the applied theme so every view that reads it rebuilds on change.</summary>
	private static readonly State<Theme> _current = new( Default );

	/// <summary>Gets the applied theme and subscribes the building view to theme changes.</summary>
	public static Theme Current
	{
		get => _current.Value;
		internal set => _current.Value = value;
	}

	/// <summary>Gets the application background color.</summary>
	public Color Canvas { get; init; } = Color.Parse( "#262624" );

	/// <summary>Gets the panel background color.</summary>
	public Color Panel { get; init; } = Color.Parse( "#222220" );

	/// <summary>Gets the raised surface color for cards, fields and menus.</summary>
	public Color Raised { get; init; } = Color.Parse( "#2d2d29" );

	/// <summary>Gets the hover and selection fill.</summary>
	public Color Hover { get; init; } = Color.Parse( "#3a3a34" );

	/// <summary>Gets the primary foreground color.</summary>
	public Color Text { get; init; } = Color.Parse( "#e8e6df" );

	/// <summary>Gets the secondary foreground color.</summary>
	public Color Muted { get; init; } = Color.Parse( "#a09f96" );

	/// <summary>Gets the separator and outline color.</summary>
	public Color Border { get; init; } = Color.Parse( "#3b3b35" );

	/// <summary>Gets the accent color for the active panel, focus and highlights.</summary>
	public Color Accent { get; init; } = Color.Parse( "#d5a583" );

	/// <summary>Gets the docking drop preview fill.</summary>
	public Color Drop { get; init; } = Color.Parse( "#655343" );

	/// <summary>Gets the outline of a panel waiting for the user.</summary>
	public Color Attention { get; init; } = Color.Parse( "#8fb4d9" );

	/// <summary>Gets the color for successful status.</summary>
	public Color Success { get; init; } = Color.Parse( "#9bcb9a" );

	/// <summary>Gets the color for failed status.</summary>
	public Color Error { get; init; } = Color.Parse( "#e6a09a" );

	/// <summary>Gets the title bar divider color.</summary>
	public Color Divider { get; init; } = Color.Parse( "#353531" );

	/// <summary>Gets the title bar foreground while the window is inactive.</summary>
	public Color Inactive { get; init; } = Color.Parse( "#85857d" );

	/// <summary>Gets the hover fill of the title bar caption buttons.</summary>
	public Color CaptionHover { get; init; } = Color.Parse( "#393934" );

	/// <summary>Gets terminal ANSI color 0, black.</summary>
	public Color AnsiBlack { get; init; } = Color.Parse( "#3b3b35" );

	/// <summary>Gets terminal ANSI color 1, red.</summary>
	public Color AnsiRed { get; init; } = Color.Parse( "#e0857c" );

	/// <summary>Gets terminal ANSI color 2, green.</summary>
	public Color AnsiGreen { get; init; } = Color.Parse( "#9bcb9a" );

	/// <summary>Gets terminal ANSI color 3, yellow.</summary>
	public Color AnsiYellow { get; init; } = Color.Parse( "#dfc07f" );

	/// <summary>Gets terminal ANSI color 4, blue.</summary>
	public Color AnsiBlue { get; init; } = Color.Parse( "#83a8d4" );

	/// <summary>Gets terminal ANSI color 5, magenta.</summary>
	public Color AnsiMagenta { get; init; } = Color.Parse( "#c59bd0" );

	/// <summary>Gets terminal ANSI color 6, cyan.</summary>
	public Color AnsiCyan { get; init; } = Color.Parse( "#80c2bd" );

	/// <summary>Gets terminal ANSI color 7, white.</summary>
	public Color AnsiWhite { get; init; } = Color.Parse( "#cfcdc5" );

	/// <summary>Gets terminal ANSI color 8, bright black.</summary>
	public Color AnsiBrightBlack { get; init; } = Color.Parse( "#75746c" );

	/// <summary>Gets terminal ANSI color 9, bright red.</summary>
	public Color AnsiBrightRed { get; init; } = Color.Parse( "#f0a59d" );

	/// <summary>Gets terminal ANSI color 10, bright green.</summary>
	public Color AnsiBrightGreen { get; init; } = Color.Parse( "#b8deb7" );

	/// <summary>Gets terminal ANSI color 11, bright yellow.</summary>
	public Color AnsiBrightYellow { get; init; } = Color.Parse( "#f0d69e" );

	/// <summary>Gets terminal ANSI color 12, bright blue.</summary>
	public Color AnsiBrightBlue { get; init; } = Color.Parse( "#a6c3e6" );

	/// <summary>Gets terminal ANSI color 13, bright magenta.</summary>
	public Color AnsiBrightMagenta { get; init; } = Color.Parse( "#dab9e3" );

	/// <summary>Gets terminal ANSI color 14, bright cyan.</summary>
	public Color AnsiBrightCyan { get; init; } = Color.Parse( "#a3d9d5" );

	/// <summary>Gets terminal ANSI color 15, bright white.</summary>
	public Color AnsiBrightWhite { get; init; } = Color.Parse( "#f4f2ec" );

	/// <summary>Gets the user interface font family.</summary>
	public string FontFamily { get; init; } = "Segoe UI";

	/// <summary>Gets the standard text size in logical pixels.</summary>
	public int FontSize { get; init; } = 13;

	/// <summary>Gets the standard control height in logical pixels.</summary>
	public int ControlHeight { get; init; } = 28;

	/// <summary>Gets the standard content spacing in logical pixels.</summary>
	public int Spacing { get; init; } = 8;

	/// <summary>Gets the separator thickness in logical pixels.</summary>
	public int BorderWidth { get; init; } = 1;

	/// <summary>Gets the shared control corner radius in logical pixels.</summary>
	public int Radius { get; init; } = 5;

	/// <summary>Builds a keyboard-accessible button in this theme.</summary>
	public Button BuildButton( string label, Action action, bool isEnabled = true )
	{
		return new Button
		{
			Height = ControlHeight,
			Padding = new Edges( 0, Spacing ),
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = Radius,
			Focusable = true,
			Disabled = !isEnabled,
			AccessibilityName = label,
			Color = isEnabled ? Text : Muted,
			Cursor = Cursor.Pointer,
			OnClick = action,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, action ),
				new( Key.Space, KeyModifiers.None, action ),
			],
			HoverStyle = new Style
			{
				BackgroundColor = Hover,
			},
			FocusStyle = new Style
			{
				BackgroundColor = Hover,
			},
			Children =
			[
				new Text
				{
					Content = label,
					FontSize = FontSize,
				},
			],
		};
	}

	/// <summary>Defines the switch's width in logical pixels.</summary>
	private const int SwitchWidth = 40;

	/// <summary>Defines the switch's height in logical pixels.</summary>
	private const int SwitchHeight = 22;

	/// <summary>Defines the gap between the switch's track and knob in logical pixels.</summary>
	private const int KnobInset = 3;

	/// <summary>Builds the app's one on and off switch: a track in the accent color with the knob at the end when on; a disabled one is dimmed and
	/// ignores clicks.</summary>
	/// <param name="name">What the switch turns on, for screen readers.</param>
	/// <param name="isOn">Whether it is on.</param>
	/// <param name="toggle">Flips it.</param>
	/// <param name="isEnabled">Whether it takes clicks.</param>
	public Button BuildSwitch( string name, bool isOn, Action toggle, bool isEnabled = true )
	{
		const int knob = SwitchHeight - KnobInset * 2;
		return new Button
		{
			Key = "switch",
			Width = SwitchWidth,
			Height = SwitchHeight,
			FlexShrink = 0,
			Padding = KnobInset,
			FlexDirection = FlexDirection.Row,
			JustifyContent = isOn ? JustifyContent.FlexEnd : JustifyContent.FlexStart,
			BorderRadius = SwitchHeight / 2.0,
			BackgroundColor = isOn ? Accent : Hover,
			Opacity = isEnabled ? 1 : 0.4,
			Focusable = isEnabled,
			Cursor = isEnabled ? Cursor.Pointer : Cursor.Default,
			AccessibilityName = $"{name}: {(isOn ? "on" : "off")}",
			OnClick = isEnabled ? toggle : null,
			KeyBindings = isEnabled ? [new( Key.Enter, KeyModifiers.None, toggle ), new( Key.Space, KeyModifiers.None, toggle )] : [],
			FocusStyle = new Style { BorderColor = Text, BorderWidth = BorderWidth },
			Children = [new Container { Width = knob, Height = knob, BorderRadius = knob / 2.0, BackgroundColor = isOn ? Canvas : Muted }],
		};
	}

	/// <summary>Builds the app's one segmented control, with the picked choice raised.</summary>
	/// <typeparam name="T">The choices' value type.</typeparam>
	/// <param name="name">What the control picks, for screen readers.</param>
	/// <param name="choices">Each choice's value and label, in order.</param>
	/// <param name="picked">The picked value.</param>
	/// <param name="pick">Picks a value.</param>
	public Row BuildSegments<T>( string name, IReadOnlyList<(T Value, string Label)> choices, T picked, Action<T> pick )
	{
		var segments = new List<Blob>();
		foreach ( var (value, label) in choices )
		{
			var isPicked = EqualityComparer<T>.Default.Equals( value, picked );
			void Pick() => pick( value );
			segments.Add( new Button
			{
				Key = "segment:" + label,
				Padding = new Edges( Spacing * 0.75, Spacing * 1.75 ),
				BorderRadius = Radius,
				BackgroundColor = isPicked ? Panel : Color.Transparent,
				BorderColor = isPicked ? Border : Color.Transparent,
				BorderWidth = BorderWidth,
				Color = isPicked ? Text : Muted,
				Focusable = true,
				Cursor = Cursor.Pointer,
				AccessibilityName = isPicked ? $"{label}, selected" : label,
				OnClick = Pick,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, Pick ), new( Key.Space, KeyModifiers.None, Pick )],
				HoverStyle = new Style { Color = Text },
				FocusStyle = new Style { BorderColor = Accent },
				Children = [new Text { Content = label, FontWeight = isPicked ? 600 : 400 }],
			} );
		}

		return new Row
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = name,
			AlignSelf = AlignSelf.FlexStart,
			FlexWrap = FlexWrap.Wrap,
			Padding = 2,
			Gap = 2,
			BackgroundColor = Canvas,
			BorderColor = Border,
			BorderWidth = BorderWidth,
			BorderRadius = Radius + 2,
			Children = segments,
		};
	}

	/// <summary>Defines the chevron view box and default drawn size, in logical pixels.</summary>
	private const double ChevronBox = 12;

	/// <summary>Defines the chevron stroke width in logical pixels at its default size.</summary>
	private const double ChevronStroke = 1.5;

	/// <summary>Holds the closed chevron, pointing right; the open one is the same stroke turned a quarter clockwise.</summary>
	private static readonly VectorPath ClosedChevron = VectorPath.Parse( "M4.25 2.5 L7.75 6 L4.25 9.5", new ElementRect { Width = ChevronBox, Height = ChevronBox } );

	/// <summary>Holds the open chevron, pointing down.</summary>
	private static readonly VectorPath OpenChevron = VectorPath.Parse( "M2.5 4.25 L6 7.75 L9.5 4.25", new ElementRect { Width = ChevronBox, Height = ChevronBox } );

	/// <summary>Builds the app's one disclosure chevron: › when closed, ⌄ when open, drawn so both share one size and center.</summary>
	/// <param name="isOpen">Whether the content it discloses is shown.</param>
	/// <param name="color">The stroke color; <see cref="Muted"/> when null.</param>
	/// <param name="size">The square size in logical pixels; 12 when null.</param>
	public Shape BuildChevron( bool isOpen, Color? color = null, double? size = null ) => new()
	{
		Path = isOpen ? OpenChevron : ClosedChevron,
		Fit = ShapeFit.Contain,
		Width = size ?? ChevronBox,
		Height = size ?? ChevronBox,
		FlexShrink = 0,
		Stroke = new VectorStroke( ChevronStroke, color ?? Muted )
		{
			Cap = StrokeCap.Round,
			Join = StrokeJoin.Round,
		},
	};

	/// <summary>Reads theme file JSON; tokens it leaves out keep their values in <paramref name="baseTheme"/>, or <see cref="Default"/>.</summary>
	/// <exception cref="JsonException">The JSON is malformed, names an unknown token, or holds an invalid value.</exception>
	public static Theme Parse( string json, Theme? baseTheme = null )
	{
		var overrides = JsonNode.Parse( json, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip } ) as JsonObject
			?? throw new JsonException( "The theme file must hold a JSON object." );
		var merged = JsonSerializer.SerializeToNode( baseTheme ?? Default, _fileOptions )!.AsObject();
		foreach ( var (name, value) in overrides )
		{
			// Token names match case-insensitively; an unknown name stays so deserializing rejects it.
			var token = merged.Select( pair => pair.Key ).FirstOrDefault( key => string.Equals( key, name, StringComparison.OrdinalIgnoreCase ) ) ?? name;
			merged[token] = value?.DeepClone();
		}

		var theme = merged.Deserialize<Theme>( _fileOptions ) ?? throw new JsonException( "The theme file must hold a JSON object." );
		if ( string.IsNullOrWhiteSpace( theme.FontFamily ) )
			throw new JsonException( "FontFamily must not be empty." );
		if ( theme.FontSize <= 0 || theme.ControlHeight <= 0 )
			throw new JsonException( "FontSize and ControlHeight must be positive." );
		if ( theme.Spacing < 0 || theme.BorderWidth < 0 || theme.Radius < 0 )
			throw new JsonException( "Spacing, BorderWidth and Radius must not be negative." );

		return theme;
	}

	/// <summary>Writes every token as theme file JSON that <see cref="Parse"/> reads back unchanged.</summary>
	public string Format() => JsonSerializer.Serialize( this, _fileOptions );

	/// <summary>Reads and writes theme files: indented, commented, unknown tokens rejected, colors as CSS strings.</summary>
	private static readonly JsonSerializerOptions _fileOptions = new()
	{
		WriteIndented = true,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		PropertyNameCaseInsensitive = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Converters = { new ColorConverter() },
	};

	/// <summary>Converts colors to and from CSS hexadecimal or named color strings.</summary>
	private sealed class ColorConverter : JsonConverter<Color>
	{
		/// <summary>Parses a CSS color string.</summary>
		public override Color Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
		{
			var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
			return (text is null ? null : Color.TryParse( text )) ?? throw new JsonException( $"Expected a CSS color string such as \"#d5a583\", found {(text is null ? reader.TokenType.ToString() : $"\"{text}\"")}." );
		}

		/// <summary>Writes a color as #rrggbb, or #rrggbbaa when it is translucent.</summary>
		public override void Write( Utf8JsonWriter writer, Color value, JsonSerializerOptions options )
		{
			static string ComputeChannel( double channel ) => ((int)Math.Round( channel * 255 )).ToString( "x2" );

			var alpha = value.A < 1 ? ComputeChannel( value.A ) : "";
			writer.WriteStringValue( $"#{ComputeChannel( value.R )}{ComputeChannel( value.G )}{ComputeChannel( value.B )}{alpha}" );
		}
	}
}
