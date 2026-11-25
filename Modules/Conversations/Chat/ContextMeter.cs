using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes context use, model cost metadata, and token-saving activity shown by the meter and its detail panel.</summary>
/// <param name="Tokens">The tokens the session's context holds, or null when Omp does not know yet.</param>
/// <param name="Window">The model's context window in tokens.</param>
/// <param name="CompactAt">The context size at which Omp compacts automatically, or null when it does not.</param>
/// <param name="IsCompacting">Whether a compaction runs now.</param>
/// <param name="Model">The connected model's display name.</param>
/// <param name="Effort">The connected session's reasoning effort.</param>
/// <param name="InputCost">The model's list price for one million input tokens, or null when unavailable.</param>
/// <param name="OutputCost">The model's list price for one million output tokens, or null when unavailable.</param>
/// <param name="Compactions">The compactions observed while the panel is open.</param>
/// <param name="AuxiliaryCalls">The isolated auxiliary model calls started while the panel is open.</param>
/// <param name="Economy">Whether economy mode is active for this chat.</param>
internal sealed record ContextMeterInput(
	long? Tokens,
	long Window,
	long? CompactAt,
	bool IsCompacting,
	string Model = "",
	string Effort = "",
	double? InputCost = null,
	double? OutputCost = null,
	int Compactions = 0,
	int AuxiliaryCalls = 0,
	bool Economy = false );

/// <summary>Describes one row of the token and cost details.</summary>
/// <param name="Section">The group the row belongs to, such as Context or Model.</param>
/// <param name="Label">The row's name, or empty for a note that spans the row.</param>
/// <param name="Value">The row's value, or the note's text.</param>
internal readonly record struct ContextDetail( string Section, string Label, string Value );

/// <summary>Draws the session's context use as the prompt's top border, like Tern's composer.</summary>
/// <remarks>
/// The meter lies over the prompt frame's top edge, between its rounded corners, and takes no room of its own: the filled
/// part of the border is the context used, a tick crosses the border where Omp compacts automatically, and a small legend
/// sits on the border at the right with the share used and the window's size. Pointing at it lists the numbers.
/// </remarks>
internal sealed class ContextMeter : GuardedCell<ContextMeterInput>
{
	/// <summary>Defines the meter's height, centered on the border, in logical pixels; tall enough to point at and to hold the legend.</summary>
	private const int MeterHeight = 14;

	/// <summary>Defines the filled line's thickness in logical pixels; it covers the frame's thinner border.</summary>
	private const int LineThickness = 2;

	/// <summary>Defines the compaction tick's height in logical pixels.</summary>
	private const int TickHeight = 8;

	/// <summary>Defines the legend's text size in logical pixels.</summary>
	private const int LabelSize = 10;

	/// <summary>Defines the details popup's width in logical pixels; its content has a fixed width, so wrapping never depends on the popup's measured size.</summary>
	private const int DetailsWidth = 320;

	/// <summary>Defines the details' context bar thickness in logical pixels.</summary>
	private const int BarThickness = 4;

	/// <summary>Anchors the details shown while pointing at the meter.</summary>
	private readonly ElementHandle _handle = new();

	/// <summary>Tracks whether the pointer is on the meter.</summary>
	private readonly State<bool> _isHovered = new( false );

	/// <summary>Tracks whether the keyboard opened the token and cost panel; pointing away or Escape closes it.</summary>
	private readonly State<bool> _isOpen = new( false );

	/// <summary>Builds the fill over the border, the compaction tick, the legend, and the details while hovered.</summary>
	protected override Blob BuildView( ContextMeterInput input )
	{
		var theme = Theme.Current;
		var used = input.Tokens is { } tokens ? Math.Clamp( (double)tokens / input.Window, 0, 1 ) : 0;
		var compactAt = input.CompactAt is { } at ? Math.Clamp( (double)at / input.Window, 0, 1 ) : (double?)null;
		var isDue = compactAt is { } due && used >= due;
		var fill = input.IsCompacting || isDue ? theme.Attention : theme.Muted;
		const double lineTop = (MeterHeight - LineThickness) / 2.0;
		// The unfilled part is the frame's own border showing through, so it keeps the frame's color, such as on a file drag.
		var children = new List<Blob>
		{
			new Container
			{
				Key = "fill",
				Position = PositionType.Absolute,
				Left = 0,
				Top = lineTop,
				Width = Length.Percent( used * 100 ),
				Height = LineThickness,
				BorderRadius = LineThickness / 2.0,
				BackgroundColor = fill,
				TransitionMs = ChatMotion.SlowMs,
				TransitionEasing = ChatMotion.Ease,
				TransitionProperties = [TransitionProperty.Width, TransitionProperty.BackgroundColor],
			},
		};

		if ( compactAt is { } mark )
		{
			children.Add( new Row
			{
				Key = "compact",
				Position = PositionType.Absolute,
				Left = 0,
				Top = (MeterHeight - TickHeight) / 2.0,
				Width = Length.Percent( mark * 100 ),
				JustifyContent = JustifyContent.FlexEnd,
				Children = [new Container { Width = 1, Height = TickHeight, BackgroundColor = input.IsCompacting ? theme.Attention : theme.Muted }],
			} );
		}

		// The legend sits on the border like a fieldset's, its background breaking the line under it.
		var legend = new List<Blob>();
		if ( input.Tokens is not null )
			legend.Add( Cell.Mount<RollingTextInput, RollingText>( "used-share", new( $"{Math.Round( used * 100 )}%", LabelSize, isDue ? theme.Attention : theme.Text ) { FontWeight = 600, FontFamily = ViewStyle.FontFamily, Shrink = false } ) );
		legend.Add( Cell.Mount<RollingTextInput, RollingText>( "window-size", new( FormatTokens( input.Window ), LabelSize, theme.Muted ) { FontFamily = ViewStyle.FontFamily, Shrink = false } ) );
		// Only the legend reacts to the pointer: the line spans the prompt's whole top edge, where pointing at the prompt would open the details.
		children.Add( new Button
		{
			Key = "legend",
			Handle = _handle,
			Position = PositionType.Absolute,
			Right = 0,
			Top = 0,
			MinHeight = MeterHeight,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = 4,
			Padding = new Edges( 0, 5 ),
			BorderRadius = MeterHeight / 2.0,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			AccessibilityName = _isOpen.Value ? "Close token and cost details" : "Open token and cost details",
			Focusable = true,
			// Enter opens the details for the keyboard; pointing away or Escape closes them, so a click never leaves them stuck open.
			OnClick = () => _isOpen.Value = !_isOpen.Value,
			KeyBindings = [new( Key.Escape, KeyModifiers.None, () => _isOpen.Value = false )],
			OnPointerEnter = _ => _isHovered.Value = true,
			OnPointerLeave = _ => _isHovered.Value = _isOpen.Value = false,
			Children = legend,
		} );

		if ( _isHovered.Value || _isOpen.Value )
		{
			// The bar repeats the meter at a readable size: the used share filled, the auto-compact point marked.
			var bar = new List<Blob>
			{
				new Container
				{
					Height = BarThickness,
					BorderRadius = BarThickness / 2.0,
					BackgroundColor = theme.Border,
					Overflow = Overflow.Hidden,
					Children = [new Container { Width = Length.Percent( used * 100 ), Height = Length.Percent( 100 ), BackgroundColor = input.IsCompacting || isDue ? theme.Attention : theme.Accent }],
				},
			};
			if ( compactAt is { } point )
			{
				bar.Add( new Row
				{
					Position = PositionType.Absolute,
					Left = 0,
					Top = 0,
					Width = Length.Percent( point * 100 ),
					JustifyContent = JustifyContent.FlexEnd,
					Children = [new Container { Width = LineThickness, Height = TickHeight, BorderRadius = LineThickness / 2.0, BackgroundColor = theme.Text }],
				} );
			}

			var lines = new List<Blob> { new Text { Content = "Token and cost", FontSize = theme.FontSize, FontWeight = 600 } };
			var section = "";
			foreach ( var row in Describe( input ) )
			{
				// Each group opens with its name; the bar sits under the first, the context.
				if ( row.Section != section )
				{
					lines.Add( new Text { Content = row.Section, Color = theme.Muted, FontWeight = 600, Margin = new Edges( theme.Spacing, 0, 0, 0 ) } );
					if ( section.Length == 0 )
						lines.Add( new Container { Height = TickHeight, JustifyContent = JustifyContent.Center, Margin = new Edges( 2, 0 ), Accessibility = new Accessibility { Hidden = true }, Children = bar } );
					section = row.Section;
				}

				lines.Add( row.Label.Length == 0
					? new Row
					{
						Gap = theme.Spacing,
						Margin = new Edges( theme.Spacing, 0, 0, 0 ),
						Children = [new Container { Width = LineThickness, BorderRadius = LineThickness / 2.0, BackgroundColor = theme.Attention }, new Text { Content = row.Value, FlexShrink = 1, MinWidth = 0 }],
					}
					: new Row
					{
						Gap = theme.Spacing,
						Children = [new Text { Content = row.Label, Color = theme.Muted, FlexShrink = 0 }, new Text { Content = row.Value, FlexGrow = 1, FlexShrink = 1, MinWidth = 0, TextAlign = TextAlign.End }],
					} );
			}

			// The shared tooltip card pads by the spacing and draws a border on each side; the column fills the rest of the details' width.
			var column = new Container { Width = DetailsWidth - 2 * (theme.Spacing + theme.BorderWidth), Gap = 2, Children = lines };
			children.Add( NativePopup.BuildTooltip( _handle, "Token and cost details", [column], PortalPlacement.TopEnd, new LayoutSize( DetailsWidth, Host.Size.Height ),
				_isOpen.Value ? AccessibilityRole.Dialog : AccessibilityRole.Tooltip ) );
		}

		return new Container
		{
			Key = "context-meter",
			// Laid over the prompt frame's top border, centered on it, between the frame's rounded corners; its parent box is the frame's own size with no padding.
			// The strip itself takes no pointer, so clicks on the rest of the border reach the prompt.
			Position = PositionType.Absolute,
			Left = ChatStyle.BubbleRadius,
			Right = ChatStyle.BubbleRadius,
			Top = (Theme.Current.BorderWidth - MeterHeight) / 2.0,
			Height = MeterHeight,
			HitTestSelf = false,
			Children = children,
		};
	}

	/// <summary>Lists context use, compaction, the model, list prices, observed auxiliary work, and economy mode as rows grouped by section, context first;
	/// economy guidance follows as a note when the context runs full.</summary>
	internal static IReadOnlyList<ContextDetail> Describe( ContextMeterInput input )
	{
		var rows = new List<ContextDetail>
		{
			new( "Context", "Used", input.Tokens is { } tokens
				? $"{FormatTokens( tokens )} of {FormatTokens( input.Window )} · {Math.Round( (double)tokens / input.Window * 100 )}%"
				: "Unknown until the next reply" ),
			new( "Context", "Auto-compact", input.IsCompacting ? "Compacting now"
				: input.CompactAt is { } at ? $"{FormatTokens( at )} · {Math.Round( (double)at / input.Window * 100 )}%"
				: "Off" ),
			new( "Context", "Compactions", input.Compactions.ToString( CultureInfo.InvariantCulture ) ),
		};
		if ( input.Model.Length > 0 )
			rows.Add( new( "Model", "Name", input.Model ) );
		if ( input.Effort.Length > 0 )
			rows.Add( new( "Model", "Effort", input.Effort ) );
		if ( input.InputCost is not null || input.OutputCost is not null )
		{
			rows.Add( new( "List price", "Input", FormatRate( input.InputCost ) + " / M" ) );
			rows.Add( new( "List price", "Output", FormatRate( input.OutputCost ) + " / M" ) );
		}
		else
			rows.Add( new( "List price", "Rates", "Unavailable" ) );
		if ( input.Tokens is { } pricedTokens && input.InputCost is { } rate )
			rows.Add( new( "List price", "Next input", $"≈ ${(pricedTokens / 1_000_000.0 * rate).ToString( "0.####", CultureInfo.InvariantCulture )}" ) );
		rows.Add( new( "Session", "Auxiliary calls (titles, task checks)", input.AuxiliaryCalls.ToString( CultureInfo.InvariantCulture ) ) );
		rows.Add( new( "Session", "Economy mode", input.Economy ? "On · extra Lokn tools on demand" : "Off" ) );
		if ( input.Economy && input.Tokens is { } used && used >= input.Window * 0.7 )
			rows.Add( new( "Session", "", "Different task? Start a new chat.\nSame task? Use /compact." ) );
		return rows;
	}

	/// <summary>Formats one per-million token rate, or a question mark when the model catalog omits it.</summary>
	private static string FormatRate( double? rate ) => rate is { } known ? "$" + known.ToString( "0.##", CultureInfo.InvariantCulture ) : "?";

	/// <summary>Formats a token count the way Omp does: 500K, 1.2M.</summary>
	internal static string FormatTokens( long tokens ) => tokens switch
	{
		>= 1_000_000 => (tokens / 1_000_000.0).ToString( "0.#", CultureInfo.InvariantCulture ) + "M",
		>= 1_000 => (tokens / 1_000.0).ToString( tokens >= 100_000 ? "0" : "0.#", CultureInfo.InvariantCulture ) + "K",
		_ => tokens.ToString( CultureInfo.InvariantCulture ),
	};
}
