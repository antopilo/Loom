using Lokn.Modules.Conversations;

namespace Lokn.Modules.Remote;

/// <summary>Shows the control bar (<see cref="ControlBar"/>) over this PC's screen while an agent of a chat on this PC uses its windows, and stops that
/// chat's turn when the user presses Stop or Esc.</summary>
/// <remarks>Loom on this PC shows it, in the <c>local</c> role for its own agents and in the <c>daemon</c> role for the daemon's, which tells it of each
/// action (<c>cap.agent</c>). The bar leaves once the chat's turn ends, or after a minute without an action.</remarks>
public sealed partial class RemoteModule
{
	/// <summary>Defines how long the bar stays after the agent's last action while its turn still runs.</summary>
	private static readonly TimeSpan ControlIdle = TimeSpan.FromMinutes( 1 );

	/// <summary>Stores the bar while this Loom shows one, on Windows; null otherwise.</summary>
	private IDisposable? _controlBar;

	/// <summary>Stores the timer that hides the bar once the agent is done.</summary>
	private Timer? _controlTimer;

	/// <summary>Stores the chat whose agent uses the windows, or null when no chat's agent does.</summary>
	private Guid? _controlChat;

	/// <summary>Stores when the agent last acted.</summary>
	private DateTime _controlAt;

	/// <summary>Stores whether the bar shows.</summary>
	private bool _isControlShown;

	/// <summary>Starts following agents' actions in this PC's windows, on Windows, outside checks.</summary>
	private void StartControlBar()
	{
		if ( !OperatingSystem.IsWindows() || _hooks is not null || _controlBar is not null )
			return;

		_controlBar = new ControlBar( () => _context?.Post( StopControl ) );
		AgentWindowActivity.Used += FollowControl;
		_controlTimer = new Timer( _ => _context?.Post( CheckControl ), null, TimeSpan.FromSeconds( 1 ), TimeSpan.FromSeconds( 1 ) );
	}

	/// <summary>Stops following agents and closes the bar.</summary>
	private void StopControlBar()
	{
		AgentWindowActivity.Used -= FollowControl;
		_controlTimer?.Dispose();
		_controlTimer = null;
		_controlBar?.Dispose();
		_controlBar = null;
		_isControlShown = false;
	}

	/// <summary>Shows the bar for an action an agent took, naming its window; any thread.</summary>
	private void FollowControl( AgentWindowUse use ) => _context?.Post( () =>
	{
		if ( !OperatingSystem.IsWindows() || _controlBar is not ControlBar bar )
			return;

		_controlChat = use.Chat;
		_controlAt = DateTime.UtcNow;
		_isControlShown = true;
		bar.Show( use.Title, use.Hwnd );
	} );

	/// <summary>Hides the bar once the agent's chat no longer works, or after <see cref="ControlIdle"/> without an action.</summary>
	private void CheckControl()
	{
		if ( !_isControlShown || !OperatingSystem.IsWindows() || _controlBar is not ControlBar bar )
			return;

		var isWorking = _controlChat is { } chat && FindChatSession( chat ) is { IsBusy: true };
		if ( isWorking && DateTime.UtcNow - _controlAt < ControlIdle )
			return;

		_isControlShown = false;
		bar.Hide();
	}

	/// <summary>Stops the turn of the chat whose agent uses the windows, as the user asked with Stop or Esc.</summary>
	private void StopControl()
	{
		_isControlShown = false;
		if ( _controlChat is { } chat && FindChatSession( chat ) is { } session )
			session.Stop();
	}

	/// <summary>Finds the session of a chat on this PC, or null when it has none.</summary>
	private Chat.IChatSession? FindChatSession( Guid chat ) =>
		_context?.App.GetModule<ConversationsModule>().Hosts.Local.Sessions.Find( chat );
}
