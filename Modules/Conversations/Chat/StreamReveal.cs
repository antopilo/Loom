using System.Diagnostics;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Remembers how far one streamed text block is revealed, so a remounted reveal resumes where it was instead of replaying.</summary>
internal sealed class StreamProgress
{
	/// <summary>Gets or sets how many characters of the block are shown.</summary>
	internal int Revealed { get; set; }
}

/// <summary>Describes one streamed Markdown block to reveal.</summary>
/// <param name="Text">Everything received for the block so far.</param>
/// <param name="IsLive">Whether more text may still arrive, so the reveal stops at whole words; once it ends the rest is revealed too.</param>
/// <param name="Progress">The block's reveal position, kept by the transcript across remounts.</param>
/// <param name="OpenLink">Opens a web link the reader follows.</param>
internal sealed record StreamRevealInput( string Text, bool IsLive, StreamProgress Progress, Action<Uri> OpenLink );

/// <summary>Reveals streamed Markdown at a pace that follows its backlog, whole words at a time, with the newest words fading in. It owns its frame timer, so only this block rebuilds per frame.</summary>
internal sealed class StreamReveal : GuardedCell<StreamRevealInput>
{
	/// <summary>Defines the reveal frame interval in milliseconds.</summary>
	private const double FrameMs = 1000.0 / 60;

	/// <summary>Defines how long the reveal takes to work off a backlog, in milliseconds: it moves at the backlog divided by this, so a burst is caught up quickly and a trickle reads steadily.</summary>
	private const double CatchUpMs = 350;

	/// <summary>Defines the slowest reveal pace in characters per second, so the last of a backlog does not crawl.</summary>
	private const double MinCharsPerSecond = 40;

	/// <summary>Defines how long a newly revealed word takes to fade in, in milliseconds.</summary>
	private const double FadeMs = ChatMotion.SlowMs;

	/// <summary>Counts reveal frames; reading it rebuilds the block on every frame.</summary>
	private readonly State<int> _frame = new( 0 );

	/// <summary>Holds the reveal steps still fading in, oldest first.</summary>
	private readonly List<RevealStep> _steps = [];

	/// <summary>Stores the unrounded reveal position in characters; the shown text ends at the last word boundary before it.</summary>
	private double _position;

	/// <summary>Stores the previous frame's <see cref="Stopwatch"/> timestamp.</summary>
	private long _lastTick;

	/// <summary>Holds the frame timer while the reveal moves or fades, or null at rest.</summary>
	private WindowTimer? _timer;

	/// <summary>Takes up new text: snaps when motion is reduced, otherwise starts revealing what is not shown yet.</summary>
	protected override void InputChanged( StreamRevealInput input )
	{
		var progress = input.Progress;
		if ( progress.Revealed > input.Text.Length )
		{
			// A replaced snapshot can be shorter than what was shown; show it as it is.
			progress.Revealed = input.Text.Length;
			_steps.Clear();
		}
		_position = Math.Min( Math.Max( _position, progress.Revealed ), input.Text.Length );
		if ( IsReducedMotion() )
		{
			progress.Revealed = input.Text.Length;
			_steps.Clear();
			return;
		}
		if ( progress.Revealed < input.Text.Length && _timer is not { IsActive: true } )
		{
			_lastTick = Stopwatch.GetTimestamp();
			_timer = Every( FrameMs, Advance );
		}
	}

	/// <summary>Shows everything received when the block leaves the view, so scrolling back finds it at rest.</summary>
	protected override void Unmounting() => Input.Progress.Revealed = Input.Text.Length;

	/// <summary>Builds the revealed prefix, fading its newest words by how long ago each was revealed.</summary>
	protected override Blob BuildView( StreamRevealInput input )
	{
		_ = _frame.Value;
		var shown = Math.Min( input.Progress.Revealed, input.Text.Length );
		var text = input.Text[..shown];
		if ( _steps.Count == 0 )
			return MarkdownView.Build( text, input.OpenLink );

		var now = Stopwatch.GetTimestamp();
		return MarkdownView.Build( text, input.OpenLink, distance => ComputeOpacity( shown - 1L - distance, now ) );
	}

	/// <summary>Moves the reveal one frame toward the received text, records the words it reveals, and stops once nothing moves or fades.</summary>
	private void Advance()
	{
		var now = Stopwatch.GetTimestamp();
		var elapsed = Stopwatch.GetElapsedTime( _lastTick, now ).TotalSeconds;
		_lastTick = now;
		var input = Input;
		var text = input.Text;
		var progress = input.Progress;
		if ( IsReducedMotion() )
		{
			progress.Revealed = text.Length;
			_steps.Clear();
		}
		else
		{
			var backlog = text.Length - _position;
			if ( backlog > 0 )
				_position = Math.Min( text.Length, _position + Math.Max( MinCharsPerSecond, backlog * 1000 / CatchUpMs ) * elapsed );
			var cut = ComputeCut( text, (int)_position, progress.Revealed, input.IsLive );
			if ( cut > progress.Revealed )
			{
				_steps.Add( new( progress.Revealed, cut, now ) );
				progress.Revealed = cut;
			}
			while ( _steps.Count > 0 && Stopwatch.GetElapsedTime( _steps[0].Time, now ).TotalMilliseconds >= FadeMs )
				_steps.RemoveAt( 0 );
		}

		// A live block waits at its last whole word for more text; new input restarts the timer.
		if ( _steps.Count == 0 && (progress.Revealed >= text.Length || _position >= text.Length) )
		{
			_timer?.Dispose();
			_timer = null;
		}
		_frame.Value++;
	}

	/// <summary>Computes the opacity of the character at a source offset from when its reveal step began, easing out.</summary>
	private double ComputeOpacity( long offset, long now )
	{
		foreach ( var step in _steps )
		{
			if ( offset >= step.End )
				continue;
			if ( offset < step.Start )
				return 1;

			var progress = Math.Clamp( Stopwatch.GetElapsedTime( step.Time, now ).TotalMilliseconds / FadeMs, 0, 1 );
			return 1 - (1 - progress) * (1 - progress);
		}
		return 1;
	}

	/// <summary>Computes where the shown text ends: the last word end at or before <paramref name="limit"/> and past <paramref name="revealed"/>, or the whole text once it is complete.</summary>
	private static int ComputeCut( string text, int limit, int revealed, bool isLive )
	{
		if ( limit >= text.Length && !isLive )
			return text.Length;

		for ( var index = Math.Min( limit, text.Length - 1 ); index > revealed; index-- )
		{
			if ( char.IsWhiteSpace( text[index] ) && !char.IsWhiteSpace( text[index - 1] ) )
				return index;
		}
		return revealed;
	}

	/// <summary>Reports whether motion is off: the system asks for reduced motion or Goo's time scale stops animations.</summary>
	private bool IsReducedMotion() => Motion.TimeScale <= 0 || Host.Preferences.ReducedMotion == true;

	/// <summary>Records one reveal step: the characters it showed and when.</summary>
	/// <param name="Start">The first revealed character's offset.</param>
	/// <param name="End">The offset just past the last revealed character.</param>
	/// <param name="Time">The <see cref="Stopwatch"/> timestamp of the reveal.</param>
	private readonly record struct RevealStep( int Start, int End, long Time );
}
