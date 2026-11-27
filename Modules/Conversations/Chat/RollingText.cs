using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes a label whose changes animate.</summary>
/// <param name="Text">The text shown.</param>
/// <param name="FontSize">The text size in logical pixels.</param>
/// <param name="Color">The text color.</param>
internal sealed record RollingTextInput( string Text, double FontSize, Color Color )
{
	/// <summary>Gets the font weight, or null to inherit it.</summary>
	public double? FontWeight { get; init; }

	/// <summary>Gets the font family list, or null to inherit it; a changed run is measured in it, or in the theme's font when null.</summary>
	public string? FontFamily { get; init; }

	/// <summary>Gets how many lines the label wraps to: one rolls the changed run in place, more cross-fade the whole text.</summary>
	public int MaxLines { get; init; } = 1;

	/// <summary>Gets whether the label takes its row's free space.</summary>
	public bool Grow { get; init; }

	/// <summary>Gets whether the label may shrink, ending in an ellipsis, when its row runs out of room.</summary>
	public bool Shrink { get; init; } = true;

	/// <summary>Gets whether every change rolls up, as a clock's does, rather than numbers that fell rolling down.</summary>
	public bool Rising { get; init; }

	/// <summary>Gets whether a settled one-line label that runs out of room fades out at its end, as the sidebar's titles do, rather than ending in an
	/// ellipsis.</summary>
	public bool Fade { get; init; }
}

/// <summary>Shows a label whose changes move instead of popping: only the changed run moves, the rest of the text stays put.</summary>
/// <remarks>
/// On a change the text keeps its shared start and end, and the run between them sits in a line-high clipped box: the old run
/// slides up and fades out while the new one slides in from below. A number that fell rolls the other way. The box's width
/// eases between the two runs, so whatever follows the label glides. A change during a change continues from what shows.
/// The first text shows at rest, and a settled label is one plain text again. A label of several lines cross-fades instead.
/// </remarks>
internal sealed class RollingText : GuardedCell<RollingTextInput>
{
	/// <summary>Defines how far a rolling run travels, as a multiple of the font size, so it clears its clipped line.</summary>
	private const double Travel = 1.2;

	/// <summary>Defines the opacity under which an outgoing run is dropped when another change arrives.</summary>
	private const double MinOpacity = 0.02;

	/// <summary>Stores the text shown at rest, or the text a running change moves to; null before the first input.</summary>
	private string? _text;

	/// <summary>Holds the runs of a running change, outgoing first and incoming last; empty at rest.</summary>
	private Layer[] _layers = [];

	/// <summary>Stores how many leading characters every run shares and shows still.</summary>
	private int _prefix;

	/// <summary>Stores how many trailing characters every run shares and shows still.</summary>
	private int _suffix;

	/// <summary>Stores the changed box's width when the change started, in logical pixels.</summary>
	private double _fromWidth;

	/// <summary>Stores the changed box's width once the change settles, in logical pixels.</summary>
	private double _toWidth;

	/// <summary>Tracks a change from 0, as it starts, to 1, settled; reading it rebuilds only this label.</summary>
	private Anim<double>? _progress;

	/// <summary>Starts a change when the text differs from what shows; the first text shows at rest.</summary>
	protected override void InputChanged( RollingTextInput input )
	{
		if ( _progress is null )
		{
			_progress = Animate( 1.0 );
			_progress.Completed += _ => UpdateSettled();
		}

		if ( _text is null )
		{
			_text = input.Text;
			return;
		}
		if ( input.Text == _text )
			return;

		var progress = _layers.Length > 0 ? _progress.Value : 1;
		var isRoll = input.MaxLines == 1;
		var direction = input.Rising || !isRoll ? 1 : ComputeDirection( _text, input.Text );
		var distance = isRoll ? input.FontSize * Travel : ChatMotion.Rise;

		// What shows now becomes the outgoing runs, each leaving from where it is; a run returning to the new text turns back instead.
		// The text shown so far always stays, since the box's starting width is measured from it.
		Layer[] shown = _layers.Length > 0 ? _layers : [new( _text, 1, 0, 1, 0 )];
		var layers = new List<Layer>();
		var incoming = new Layer( input.Text, 0, direction * distance, 1, 0 );
		foreach ( var layer in shown )
		{
			var opacity = Lerp( layer.FromOpacity, layer.ToOpacity, progress );
			var offset = Lerp( layer.FromOffset, layer.ToOffset, progress );
			if ( layer.Text == input.Text )
				incoming = incoming with { FromOpacity = opacity, FromOffset = offset };
			else if ( opacity > MinOpacity || layer.Text == _text )
				layers.Add( new( layer.Text, opacity, offset, 0, -direction * distance ) );
		}
		layers.Add( incoming );

		if ( isRoll )
		{
			var (prefix, suffix) = ComputeShared( [.. layers.Select( layer => layer.Text )] );
			// The box starts as wide as it shows now, plus what leaves the still text for it, or less what joins the still text.
			var width = _layers.Length == 0
				? Measure( input, _text[prefix..(_text.Length - suffix)] )
				: Lerp( _fromWidth, _toWidth, progress ) + MeasureBetween( input, _text, prefix, _prefix ) + MeasureBetween( input, _text, _text.Length - _suffix, _text.Length - suffix );
			_prefix = prefix;
			_suffix = suffix;
			_fromWidth = Math.Max( 0, width );
			_toWidth = Measure( input, ComputeRun( input.Text ) );
		}
		else
		{
			_prefix = 0;
			_suffix = 0;
		}

		_layers = [.. layers];
		_text = input.Text;
		_progress.Play( 0, 1, Motion.Tween( ChatMotion.BaseMs, ChatMotion.Ease ) );
	}

	/// <summary>Builds the label at rest, or its change at the current progress.</summary>
	protected override Blob BuildView( RollingTextInput input )
	{
		if ( _layers.Length == 0 || _progress is null )
			return BuildRest( input );

		var progress = _progress.Value;
		return input.MaxLines == 1 ? BuildRoll( input, progress ) : BuildFade( input, progress );
	}

	/// <summary>Builds the settled label: one text that ends in an ellipsis when it runs out of room, or fades out there when asked to.</summary>
	private static Text BuildRest( RollingTextInput input )
	{
		var text = BuildText( input, input.Text );
		text.FlexGrow = input.Grow ? 1 : 0;
		text.FlexShrink = input.Shrink ? 1 : 0;
		text.MinWidth = 0;
		text.TextMaxLines = Math.Max( 1, input.MaxLines );
		text.TextTrimming = TextTrimming.Ellipsis;
		return input.Fade && input.MaxLines == 1 ? Workspaces.SidebarFade.Apply( text ) : text;
	}

	/// <summary>Builds a single-line change: the still start, the clipped box where the runs roll, and the still end.</summary>
	private Row BuildRoll( RollingTextInput input, double progress )
	{
		// An invisible, zero-width line gives the box its height even when no run is in it.
		var strut = BuildText( input, "0" );
		strut.Width = 0;
		strut.Visibility = Visibility.Hidden;
		var runs = new List<Blob> { strut };
		foreach ( var layer in _layers )
		{
			var content = ComputeRun( layer.Text );
			if ( content.Length == 0 )
				continue;

			var run = BuildText( input, content );
			run.Position = PositionType.Absolute;
			run.Left = 0;
			run.Top = 0;
			run.Opacity = Lerp( layer.FromOpacity, layer.ToOpacity, progress );
			run.Transform = new PanelTransform { TranslateY = Lerp( layer.FromOffset, layer.ToOffset, progress ) };
			runs.Add( run );
		}

		var children = new List<Blob>();
		if ( _prefix > 0 )
			children.Add( BuildStill( input, _text![.._prefix] ) );
		children.Add( new Container
		{
			Width = Lerp( _fromWidth, _toWidth, progress ),
			FlexShrink = 0,
			Overflow = Overflow.Hidden,
			Children = runs,
		} );
		if ( _suffix > 0 )
			children.Add( BuildStill( input, _text![^_suffix..] ) );
		return new Row
		{
			AlignItems = AlignItems.FlexStart,
			FlexGrow = input.Grow ? 1 : 0,
			FlexShrink = input.Shrink ? 1 : 0,
			MinWidth = 0,
			Overflow = input.Shrink ? Overflow.Hidden : Overflow.Visible,
			Children = children,
		};
	}

	/// <summary>Builds a multi-line change: the new text rises into place while the old fades out over it.</summary>
	private Container BuildFade( RollingTextInput input, double progress )
	{
		var children = new List<Blob>();
		foreach ( var (index, layer) in _layers.Index() )
		{
			var text = BuildText( input, layer.Text );
			text.TextMaxLines = Math.Max( 1, input.MaxLines );
			text.TextTrimming = TextTrimming.Ellipsis;
			text.Opacity = Lerp( layer.FromOpacity, layer.ToOpacity, progress );
			text.Transform = new PanelTransform { TranslateY = Lerp( layer.FromOffset, layer.ToOffset, progress ) };
			// The incoming text, last, sizes the label; outgoing ones lie over it.
			if ( index < _layers.Length - 1 )
			{
				text.Position = PositionType.Absolute;
				text.Left = 0;
				text.Right = 0;
				text.Top = 0;
			}
			children.Add( text );
		}

		return new Container
		{
			FlexGrow = input.Grow ? 1 : 0,
			FlexShrink = input.Shrink ? 1 : 0,
			MinWidth = 0,
			Children = children,
		};
	}

	/// <summary>Builds a still part of a rolling label, which keeps its place while the run beside it changes.</summary>
	private static Text BuildStill( RollingTextInput input, string content )
	{
		var text = BuildText( input, content );
		text.FlexShrink = 0;
		return text;
	}

	/// <summary>Builds one unwrapped text in the label's font and color; a multi-line label wraps its own.</summary>
	private static Text BuildText( RollingTextInput input, string content )
	{
		var text = new Text
		{
			Content = content,
			FontSize = input.FontSize,
			Color = input.Color,
		};
		if ( input.MaxLines == 1 )
			text.TextWrap = TextWrap.NoWrap;
		if ( input.FontFamily is { } family )
			text.FontFamily = family;
		if ( input.FontWeight is { } weight )
			text.FontWeight = weight;
		return text;
	}

	/// <summary>Ends a settled change, so the label is one plain text again.</summary>
	private void UpdateSettled()
	{
		if ( _layers.Length == 0 )
			return;

		_layers = [];
		Rebuild();
	}

	/// <summary>Gets the changing run of a text: what lies between the shared start and end.</summary>
	private string ComputeRun( string text ) => text[_prefix..(text.Length - _suffix)];

	/// <summary>Measures a text's single-line width in the label's font, in logical pixels.</summary>
	private double Measure( RollingTextInput input, string text ) => text.Length == 0 ? 0 : Host.MeasureText( text, BuildMeasureStyle( input ) );

	/// <summary>Measures the characters of a text between two positions, negative when the end comes first.</summary>
	private double MeasureBetween( RollingTextInput input, string text, int start, int end ) => start <= end
		? Measure( input, text[start..end] )
		: -Measure( input, text[end..start] );

	/// <summary>Builds the font declarations a run is measured with; an inherited family is measured in the theme's font.</summary>
	private static Text BuildMeasureStyle( RollingTextInput input )
	{
		var style = BuildText( input, "" );
		style.FontFamily = input.FontFamily ?? Theme.Current.FontFamily;
		return style;
	}

	/// <summary>Finds how many leading and trailing characters all texts share, widened so a change inside a word replaces the whole word.</summary>
	/// <remarks>"Thinking" to "Writing" changes the whole word rather than "Think" to "Writ"; digits roll alone, so 12 to 13 rolls only its last digit.</remarks>
	private static (int Prefix, int Suffix) ComputeShared( IReadOnlyList<string> texts )
	{
		var first = texts[0];
		var shortest = texts.Min( text => text.Length );
		var prefix = 0;
		while ( prefix < shortest && texts.All( text => text[prefix] == first[prefix] ) )
			prefix++;
		var suffix = 0;
		while ( suffix < shortest - prefix && texts.All( text => text[^(suffix + 1)] == first[^(suffix + 1)] ) )
			suffix++;

		while ( prefix > 0 && char.IsLetter( first[prefix - 1] ) && texts.Any( text => prefix < text.Length && char.IsLetter( text[prefix] ) ) )
			prefix--;
		while ( suffix > 0 && char.IsLetter( first[^suffix] ) && texts.Any( text => text.Length > suffix && char.IsLetter( text[^(suffix + 1)] ) ) )
			suffix--;
		// A surrogate pair stays whole on one side of the change.
		if ( prefix > 0 && char.IsHighSurrogate( first[prefix - 1] ) )
			prefix--;
		if ( suffix > 0 && char.IsLowSurrogate( first[^suffix] ) )
			suffix--;
		return (prefix, suffix);
	}

	/// <summary>Rolls down when the number at the change fell, and up otherwise.</summary>
	private static int ComputeDirection( string from, string to )
	{
		var (prefix, _) = ComputeShared( [from, to] );
		// The number is read whole, so 19 to 20 compares 19 with 20, not 9 with 0.
		var start = prefix;
		while ( start > 0 && char.IsAsciiDigit( from[start - 1] ) )
			start--;
		return ReadNumber( from, start ) is { } before && ReadNumber( to, start ) is { } after && after < before ? -1 : 1;
	}

	/// <summary>Reads the number, with optional thousands separators, starting at a position, or null when none starts there.</summary>
	private static decimal? ReadNumber( string text, int start )
	{
		var end = start;
		while ( end < text.Length && (char.IsAsciiDigit( text[end] ) || text[end] == ',' && end > start && end + 1 < text.Length && char.IsAsciiDigit( text[end + 1] )) )
			end++;
		return end > start && decimal.TryParse( text.AsSpan( start, end - start ), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value ) ? value : null;
	}

	/// <summary>Interpolates linearly between two values.</summary>
	private static double Lerp( double from, double to, double progress ) => from + (to - from) * progress;

	/// <summary>Describes one run of a change: its text, and its opacity and vertical offset at the change's start and end.</summary>
	/// <param name="Text">The run's whole text, of which the part between the shared start and end moves.</param>
	/// <param name="FromOpacity">The opacity as the change starts.</param>
	/// <param name="FromOffset">The vertical offset as the change starts, in logical pixels; positive is down.</param>
	/// <param name="ToOpacity">The opacity once the change settles.</param>
	/// <param name="ToOffset">The vertical offset once the change settles, in logical pixels.</param>
	private readonly record struct Layer( string Text, double FromOpacity, double FromOffset, double ToOpacity, double ToOffset );
}
