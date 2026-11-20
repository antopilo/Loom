using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Opens a stream for an agent that works with a window, saves the streamed window with the layout, and shows what agents do in it: a header label
/// while an agent acts and a short ripple where it clicked or scrolled (<c>cap.agent</c>).</summary>
public sealed partial class StreamContent
{
	/// <summary>Defines how long the header says an agent uses the window after its last action.</summary>
	private static readonly TimeSpan AgentLabelTime = TimeSpan.FromSeconds( 3 );

	/// <summary>Defines how long the ripple at an agent's click lasts.</summary>
	private static readonly TimeSpan MarkerTime = TimeSpan.FromSeconds( 1 );

	/// <summary>Defines how often the ripple redraws while it grows.</summary>
	private static readonly TimeSpan MarkerStep = TimeSpan.FromMilliseconds( 80 );

	/// <summary>Defines the ripple's radius when it starts, in logical pixels.</summary>
	private const double MarkerStartRadius = 6;

	/// <summary>Defines how far the ripple grows, in logical pixels.</summary>
	private const double MarkerGrowth = 18;

	/// <summary>Defines the radius of the dot at the ripple's center, in logical pixels.</summary>
	private const double MarkerDotRadius = 4;

	/// <summary>Holds the open stream panels of this process, on the UI thread, so an agent's window is shown in the panel that already streams it.</summary>
	private static readonly List<StreamContent> _open = [];

	/// <summary>Stores whether the user chose the streamed window in the list, which focuses the stream; false when it was opened for an agent or restored.</summary>
	private bool _isChosen;

	/// <summary>Stores when an agent last acted in the window, as a <see cref="Stopwatch"/> timestamp, or 0.</summary>
	private long _agentAt;

	/// <summary>Stores where an agent last clicked or scrolled, as fractions of the window, and when; null before the first.</summary>
	private (double X, double Y, long At)? _mark;

	/// <summary>Gets the panel's identifier.</summary>
	internal Guid PanelId => _context.Id;

	/// <summary>Gets the handle of the streamed window, or null while the list shows.</summary>
	internal long? Window => _streaming?.Hwnd;

	/// <summary>Gets whether the header says an agent uses the window.</summary>
	internal bool IsAgentActive => _agentAt != 0 && Stopwatch.GetElapsedTime( _agentAt ) < AgentLabelTime;

	/// <summary>Finds the open panel that streams a window of a host; on the UI thread.</summary>
	/// <param name="hostId">The host's identifier.</param>
	/// <param name="hwnd">The window's handle.</param>
	internal static StreamContent? Find( string hostId, long hwnd ) =>
		_open.FirstOrDefault( content => content._context.HostId == hostId && content._streaming?.Hwnd == hwnd && !content._context.Lifetime.IsCancellationRequested );

	/// <summary>Streams a window unless it already streams, without taking the focus, as for an agent that works with it.</summary>
	/// <param name="hwnd">The window's handle.</param>
	/// <param name="title">The window's title.</param>
	internal void Watch( long hwnd, string title )
	{
		if ( _streaming?.Hwnd != hwnd )
			UpdateStart( new( hwnd, title, "", 0, 0 ), isChosen: false );
	}

	/// <summary>Saves the streamed window, so a restored layout streams it again; null while the list shows.</summary>
	public override JsonElement? CaptureState() =>
		_streaming is { } window ? JsonSerializer.SerializeToElement( new StreamPanelInput( window.Hwnd, window.Title ), StreamService.Wire ) : null;

	/// <summary>Takes an agent's action in the streamed window to the UI thread.</summary>
	private void QueueAgent( AgentWindowUse use ) => _context.Post( () => ApplyAgent( use ) );

	/// <summary>Shows an agent's action: the header label, and a ripple where it has a point; both fade by themselves.</summary>
	private void ApplyAgent( AgentWindowUse use )
	{
		if ( _streaming?.Hwnd != use.Hwnd )
			return;

		var now = Stopwatch.GetTimestamp();
		_agentAt = now;
		if ( use.HasPoint )
		{
			_mark = (Math.Clamp( use.X, 0, 1 ), Math.Clamp( use.Y, 0, 1 ), now);
			_ = AnimateMarkerAsync();
		}
		UpdateVersion();
		_ = RefreshAfterAsync( AgentLabelTime );
	}

	/// <summary>Redraws the ripple while it grows, and once more after it ends.</summary>
	private async Task AnimateMarkerAsync()
	{
		var steps = (int)Math.Ceiling( MarkerTime / MarkerStep );
		for ( var step = 0; step <= steps; step++ )
		{
			if ( !await DelayAsync( MarkerStep ) )
				return;

			_context.Post( UpdateVersion );
		}
	}

	/// <summary>Redraws once a time has passed, so the label fades.</summary>
	private async Task RefreshAfterAsync( TimeSpan delay )
	{
		if ( await DelayAsync( delay + MarkerStep ) )
			_context.Post( UpdateVersion );
	}

	/// <summary>Waits while the panel is open; false when it closed meanwhile.</summary>
	private async Task<bool> DelayAsync( TimeSpan delay )
	{
		try
		{
			await Task.Delay( delay, _context.Lifetime ).ConfigureAwait( false );
			return true;
		}
		catch ( OperationCanceledException )
		{
			return false;
		}
	}

	/// <summary>Builds the header label that says an agent uses the window, while it does.</summary>
	private IEnumerable<Blob> BuildAgentLabel( Theme theme )
	{
		if ( IsAgentActive )
			yield return new Text { Content = "Agent is using this window", Color = theme.Attention, FontSize = ViewStyle.SmallSize, FlexShrink = 0, TextWrap = TextWrap.NoWrap, };
	}

	/// <summary>Builds the ripple at an agent's last click or scroll over the drawn frame, while it lasts: a ring that grows and a dot at its center.</summary>
	private IEnumerable<Blob> BuildAgentMarker( FrameFit fit, int frameWidth, int frameHeight )
	{
		if ( _mark is not { } mark )
			yield break;

		var elapsed = Stopwatch.GetElapsedTime( mark.At );
		if ( elapsed >= MarkerTime )
			yield break;

		var color = Theme.Current.Attention;
		var x = fit.Left + mark.X * frameWidth * fit.Scale;
		var y = fit.Top + mark.Y * frameHeight * fit.Scale;
		var radius = MarkerStartRadius + MarkerGrowth * (elapsed / MarkerTime);
		yield return new Container
		{
			Position = PositionType.Absolute,
			Left = x - radius,
			Top = y - radius,
			Width = radius * 2,
			Height = radius * 2,
			BorderRadius = radius,
			BorderWidth = 2,
			BorderColor = color,
		};
		yield return new Container
		{
			Position = PositionType.Absolute,
			Left = x - MarkerDotRadius,
			Top = y - MarkerDotRadius,
			Width = MarkerDotRadius * 2,
			Height = MarkerDotRadius * 2,
			BorderRadius = MarkerDotRadius,
			BackgroundColor = color,
		};
	}
}
