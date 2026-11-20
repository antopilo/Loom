using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Core;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Remote;

/// <summary>Serves the session's browser tabs to every connection: the <c>browser.sync</c>, <c>browser.open</c>, <c>browser.navigate</c>,
/// <c>browser.report</c> and <c>browser.close</c> requests and the <c>browser.tab</c> and <c>browser.closed</c> pushes (docs/remote.md, Browser frames).</summary>
/// <remarks>
/// Create, register and dispose it on the host's UI thread; it applies changes there. Reading the tabs needs <c>view</c>, changing them <c>control</c>.
/// Every change the host's tabs make, whoever made it, goes to every connection as the whole tab with its revision; a newer push of a tab replaces
/// one not sent yet, so a page's loading progress never piles up behind a slow connection.
/// </remarks>
/// <param name="tabs">The host's browser tabs.</param>
/// <param name="post">Posts to the host's UI thread and reports whether it was accepted.</param>
internal sealed class BrowserService( BrowserTabs tabs, Func<Action, bool> post ) : IHostService, IDisposable
{
	/// <summary>Defines the most pushes waiting for one connection before it counts as too slow and is closed.</summary>
	private const int MaxPendingPushes = 4096;

	/// <summary>Holds each open connection's push queue; on the UI thread.</summary>
	private readonly List<PushQueue> _queues = [];

	/// <summary>Stores whether the service follows the tabs.</summary>
	private bool _isFollowing;

	/// <summary>Registers the browser handlers for a connection; on the UI thread, before the peer starts.</summary>
	public void Register( RemoteConnection connection )
	{
		if ( !_isFollowing )
		{
			tabs.TabChanged += Broadcast;
			_isFollowing = true;
		}

		var queue = new PushQueue( connection.Peer, MaxPendingPushes );
		_queues.Add( queue );
		connection.Peer
			.On( "browser.sync", _ => UiInvoke.RunAsync<JsonObject?>( post, BuildSync ) )
			.On( "browser.open", frame => ApplyAsync( connection, frame, BrowserOp.OpenKind ) )
			.On( "browser.navigate", frame => ApplyAsync( connection, frame, BrowserOp.NavigateKind ) )
			.On( "browser.report", frame => ApplyAsync( connection, frame, BrowserOp.ReportKind ) )
			.On( "browser.close", frame => ApplyAsync( connection, frame, BrowserOp.CloseKind ) );
		connection.Peer.Closed += _ => post( () => _queues.Remove( queue ) );
	}

	/// <summary>Stops following the tabs; on the UI thread.</summary>
	public void Dispose()
	{
		if ( _isFollowing )
			tabs.TabChanged -= Broadcast;
		_isFollowing = false;
		_queues.Clear();
	}

	/// <summary>Builds a frame's fields for a change, without its type: the tab id as <c>tab</c> and the change's own fields.</summary>
	public static JsonObject BuildFields( BrowserOp op )
	{
		var fields = WireJson.BuildNode( op ) as JsonObject ?? [];
		fields.Remove( "op" );
		foreach ( var name in fields.Where( pair => pair.Value is null ).Select( pair => pair.Key ).ToArray() )
			fields.Remove( name );
		return fields;
	}

	/// <summary>Answers <c>browser.sync</c>: every tab with its revision.</summary>
	private JsonObject? BuildSync() => new() { ["tabs"] = WireJson.BuildNode( tabs.Tabs ) };

	/// <summary>Applies a client's change on the UI thread and answers the tab's revision after it, none once the tab is closed; the host's push of the
	/// changed tab follows to every connection, the sender's too, in either order with the answer.</summary>
	private Task<JsonObject?> ApplyAsync( RemoteConnection connection, JsonObject frame, string kind )
	{
		connection.RequireControl();
		BrowserOp op;
		try
		{
			op = (frame.Deserialize<BrowserOp>( StoreOp.JsonOptions ) ?? throw new JsonException( "The change is empty." )) with { Op = kind };
		}
		catch ( JsonException exception )
		{
			throw new RemoteException( RemoteErrors.BadRequest, $"The browser change is malformed: {exception.Message}" );
		}

		return UiInvoke.RunAsync( post, () =>
		{
			long? rev;
			try
			{
				rev = tabs.Apply( op );
			}
			catch ( ArgumentException exception )
			{
				throw new RemoteException( RemoteErrors.BadRequest, exception.Message );
			}
			var answer = new JsonObject();
			if ( rev is { } value )
				answer["rev"] = value;
			return (JsonObject?)answer;
		} );
	}

	/// <summary>Pushes a changed tab, or its closing, to every connection.</summary>
	private void Broadcast( Guid id, BrowserTabInfo? tab )
	{
		var key = id.ToString( "N" );
		foreach ( var queue in _queues )
		{
			JsonObject push;
			if ( tab is null )
			{
				push = Frame.Create( "browser.closed" );
				push["tab"] = WireJson.BuildNode( id );
			}
			else
			{
				push = Frame.Create( "browser.tab" );
				push["tab"] = WireJson.BuildNode( tab );
			}
			queue.Send( push, key );
		}
	}
}
