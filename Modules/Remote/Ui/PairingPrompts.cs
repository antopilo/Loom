using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Asks the user of this PC whether a device that just paired with the daemon may control this PC: a notification with <b>Allow control</b>
/// and <b>View only</b>, and a dialog the status bar mounts, while This PC's chats run in the daemon (docs/remote.md, Pairing confirm).</summary>
/// <remarks>
/// It follows the daemon link's <c>admin.pairing</c>, <c>admin.pairing.done</c> and <c>admin.paired</c> pushes and, on each connection,
/// <c>admin.status</c>'s <c>pending</c> list. Either answer sends <c>admin.confirm</c>; the daemon's <c>admin.pairing.done</c> closes the dialog and
/// marks the notification read in every Loom window. Everything runs on the UI thread.
/// </remarks>
internal sealed class PairingPrompts : IDisposable
{
	/// <summary>Defines the notifications' source, the module's id.</summary>
	private const string Source = "remote";

	/// <summary>Stores the application, whose notification feed and errors the prompts use.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the link with this PC's daemon.</summary>
	private readonly RemoteClient _daemon;

	/// <summary>Posts to the UI thread.</summary>
	private readonly Func<Action, bool> _post;

	/// <summary>Holds the devices waiting for an answer, oldest first.</summary>
	private readonly List<PendingDevice> _pending = [];

	/// <summary>Holds the notification posted for each waiting device, by device id.</summary>
	private readonly Dictionary<string, Guid> _notifications = new( StringComparer.Ordinal );

	/// <summary>Holds the devices whose answer is on its way to the daemon.</summary>
	private readonly HashSet<string> _answering = new( StringComparer.Ordinal );

	/// <summary>Counts changes, so views reading the prompts rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the daemon link's state last seen, so each new connection reads the waiting devices once.</summary>
	private LoknHostState? _lastState;

	/// <summary>Follows the daemon link; create it before the link starts so no push is missed.</summary>
	/// <param name="app">The application.</param>
	/// <param name="daemon">The link with this PC's daemon.</param>
	/// <param name="post">Posts to the UI thread.</param>
	internal PairingPrompts( LoknApplication app, RemoteClient daemon, Func<Action, bool> post )
	{
		_app = app;
		_daemon = daemon;
		_post = post;
		_daemon.AdminPushed += Apply;
		_daemon.Host.Host.Changed += FollowHost;
	}

	/// <summary>Gets the devices waiting for the user's answer, oldest first; reading it rebuilds the reader when it changes.</summary>
	internal IReadOnlyList<PendingDevice> Pending
	{
		get
		{
			_ = _version.Value;
			return [.. _pending];
		}
	}

	/// <summary>Gets whether an answer for a device is on its way; reading it rebuilds the reader when it changes.</summary>
	internal bool IsAnswering( string deviceId )
	{
		_ = _version.Value;
		return _answering.Contains( deviceId );
	}

	/// <summary>Stops following the daemon link.</summary>
	public void Dispose()
	{
		_daemon.AdminPushed -= Apply;
		_daemon.Host.Host.Changed -= FollowHost;
	}

	/// <summary>Builds the dialog for the oldest device whose answer is not on its way, or null when none waits; reading it rebuilds the reader.</summary>
	internal ConfirmDialogInput? BuildDialog()
	{
		_ = _version.Value;
		if ( _pending.FirstOrDefault( device => !_answering.Contains( device.DeviceId ) ) is not { } device )
			return null;

		return new ConfirmDialogInput( _app, $"Allow {device.Name} to control this PC?", AccessContent.ControlText, "Allow control",
			() => Answer( device.DeviceId, control: true ), () => Answer( device.DeviceId, control: false ) )
		{
			IsDestructive = true,
			CancelLabel = "View only",
		};
	}

	/// <summary>Follows a pairing notification's link: <c>allow/&lt;deviceId&gt;</c> or <c>view/&lt;deviceId&gt;</c> answers, <c>devices</c> opens Remote
	/// access on its devices.</summary>
	internal void AnswerLink( string path )
	{
		var parts = path.Split( '/', 2 );
		if ( parts.Length == 2 && parts[1].Length > 0 && parts[0] is "allow" or "view" )
			Answer( Uri.UnescapeDataString( parts[1] ), parts[0] == "allow" );
		else if ( path == "devices" )
			_app.Panels.Open( AccessContent.TypeId );
	}

	/// <summary>Sends the user's answer for a waiting device with <c>admin.confirm</c>, once; the daemon's <c>admin.pairing.done</c> then dismisses it.</summary>
	/// <param name="deviceId">The device.</param>
	/// <param name="control">Whether it may control this PC; false keeps it view only.</param>
	internal void Answer( string deviceId, bool control )
	{
		if ( _pending.All( device => device.DeviceId != deviceId ) || !_answering.Add( deviceId ) )
			return;

		_version.Value++;
		_ = SendAnswerAsync( deviceId, control );
	}

	/// <summary>Sends <c>admin.confirm</c> and dismisses the device once the daemon took the answer or no longer waits for it; a lost connection lets
	/// the user answer again.</summary>
	private async Task SendAnswerAsync( string deviceId, bool control )
	{
		var frame = Frame.Create( "admin.confirm" );
		frame["deviceId"] = deviceId;
		frame["control"] = control;
		try
		{
			await _daemon.RequestAsync( frame, CancellationToken.None ).ConfigureAwait( false );
			_post( () => Remove( deviceId ) );
		}
		catch ( RemoteException exception ) when ( exception.Code == RemoteErrors.BadRequest )
		{
			// The device was answered elsewhere, revoked or rescoped meanwhile.
			_post( () => Remove( deviceId ) );
		}
		catch ( Exception exception ) when ( exception is RemoteException or IOException or OperationCanceledException )
		{
			_post( () =>
			{
				_answering.Remove( deviceId );
				_version.Value++;
				_app.ReportError( "Remote", exception );
			} );
		}
	}

	/// <summary>Applies one of the daemon's <c>admin.*</c> pushes.</summary>
	private void Apply( JsonObject frame )
	{
		var deviceId = Frame.ReadString( frame, "deviceId" );
		if ( deviceId is null )
			return;

		var name = Frame.ReadString( frame, "name" ) is { Length: > 0 } text ? text : "A device";
		switch ( Frame.ReadType( frame ) )
		{
			case "admin.pairing":
				Add( new( deviceId, name, DateTimeOffset.FromUnixTimeSeconds( Frame.ReadLong( frame, "paired" ) ?? 0 ) ) );
				break;
			case "admin.pairing.done":
				Remove( deviceId );
				break;
			case "admin.paired":
				_app.Notifications.Post( new( $"{name} paired with control",
					"It can run programs and use this PC's files and windows as you. Revoke it in Remote access if you did not pair it." )
				{
					Source = Source,
					Link = LoknLinks.Create( "remote", "pairing", "devices" ),
				} );
				break;
		}
	}

	/// <summary>Reads the waiting devices once each time the daemon link connects, so pairings made while this Loom was away are asked about too.</summary>
	private void FollowHost()
	{
		var state = _daemon.Host.Host.State;
		var previous = _lastState;
		_lastState = state;
		if ( state == LoknHostState.Connected && previous != LoknHostState.Connected )
			_ = RefreshAsync();
	}

	/// <summary>Asks the daemon for <c>admin.status</c> and takes its <c>pending</c> list: new devices are asked about, gone ones dismissed.</summary>
	private async Task RefreshAsync()
	{
		JsonObject status;
		try
		{
			status = await _daemon.RequestAsync( Frame.Create( "admin.status" ), CancellationToken.None ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is RemoteException or IOException or OperationCanceledException )
		{
			return;
		}

		// An older daemon has no pending list; its pairings are view only and need no answer.
		if ( status["pending"] is not JsonArray list )
			return;

		var pending = list.OfType<JsonObject>()
			.Select( entry => Frame.ReadString( entry, "deviceId" ) is { } id
				? new PendingDevice( id, Frame.ReadString( entry, "name" ) is { Length: > 0 } name ? name : "A device", DateTimeOffset.FromUnixTimeSeconds( Frame.ReadLong( entry, "paired" ) ?? 0 ) )
				: null )
			.OfType<PendingDevice>()
			.ToArray();
		_post( () =>
		{
			foreach ( var gone in _pending.Where( device => pending.All( waiting => waiting.DeviceId != device.DeviceId ) ).ToArray() )
				Remove( gone.DeviceId );
			foreach ( var device in pending )
				Add( device );
		} );
	}

	/// <summary>Asks about a device that waits for an answer: a notification with both answers, and the dialog; nothing for one already asked about.</summary>
	private void Add( PendingDevice device )
	{
		if ( _pending.Any( pending => pending.DeviceId == device.DeviceId ) )
			return;

		_pending.Add( device );
		var notification = _app.Notifications.Post( new( $"{device.Name} paired with this PC", "It can view your chats. Allow it to control this PC?" )
		{
			Source = Source,
			Link = LoknLinks.Create( "remote", "pairing", "devices" ),
			Actions =
			[
				new( "Allow control", LoknLinks.Create( "remote", "pairing", "allow", device.DeviceId ) ),
				new( "View only", LoknLinks.Create( "remote", "pairing", "view", device.DeviceId ) ),
			],
		} );
		_notifications[device.DeviceId] = notification.Id;
		_version.Value++;
	}

	/// <summary>Dismisses a device that no longer waits: its dialog closes and its notification is marked read.</summary>
	private void Remove( string deviceId )
	{
		var removed = _pending.RemoveAll( device => device.DeviceId == deviceId ) > 0;
		removed |= _answering.Remove( deviceId );
		if ( _notifications.Remove( deviceId, out var notification ) )
			_app.Notifications.MarkRead( notification );
		if ( removed )
			_version.Value++;
	}
}
