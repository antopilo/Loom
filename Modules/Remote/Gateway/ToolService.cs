using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Publishes the tools Loom on this PC offers in the host's tool registry and forwards their calls to it: the <c>tools.offer</c> and
/// <c>tool.result</c> requests and the <c>tool.call</c> and <c>tool.cancel</c> pushes (docs/remote.md, Host tools). It also runs the host's browser
/// tools in an attached window (<see cref="IBrowserWindows"/>).</summary>
/// <remarks>
/// Create, register and dispose it on the host's UI thread. Only the <see cref="HostGateway.LocalDevice"/> connection may offer tools to the registry;
/// a paired device's would act on another PC. A paired device with <c>control</c> may offer its browser tools alone, which only lends its windows'
/// browser: the host's own browser tools run there when Loom on this PC does not offer them. A call waits until the client answers, the agent cancels
/// it or the connection closes; there is no timeout of its own, as the registry has none.
/// </remarks>
internal sealed class ToolService( LoknApplication app, Func<Action, bool> post ) : IHostService, IBrowserWindows, IDisposable
{
	/// <summary>Names the module whose tools a window lends for the session's browser tabs.</summary>
	public const string BrowserModuleId = "browser";

	/// <summary>Holds the connections that offered tools.</summary>
	private readonly List<Link> _links = [];

	/// <summary>Counts browser offers, so the latest lender of each kind is preferred.</summary>
	private long _lendings;

	/// <summary>Registers the tool handlers for a connection; on the UI thread, before the peer starts.</summary>
	public void Register( RemoteConnection connection )
	{
		var link = new Link( connection );
		connection.Peer
			.On( "tools.offer", frame => UiInvoke.RunAsync<JsonObject?>( post, () => UpdateOffer( link, frame ) ) )
			.On( "tool.result", frame => Task.FromResult<JsonObject?>( UpdateResult( link, frame ) ) );
		connection.Peer.Closed += _ =>
		{
			link.FailCalls();
			post( () => RemoveLink( link ) );
		};
	}

	/// <summary>Removes every offered tool and stops lending windows' browsers; on the UI thread.</summary>
	public void Dispose()
	{
		foreach ( var link in _links.ToArray() )
			RemoveLink( link );
		if ( app.Hosts.Local.BrowserTabs.Windows == this )
			app.Hosts.Local.BrowserTabs.Windows = null;
	}

	/// <summary>Runs a browser tool call in the window that lends its browser: Loom on this PC first, else a paired PC whose user lets the host's agents
	/// use its browser, the latest offer first; null when none does.</summary>
	public Task<AgentToolResult>? TryRun( AgentToolContext context, string tool, JsonElement arguments )
	{
		var lender = _links.Where( link => link.LentAt > 0 && !link.Connection.Peer.IsClosed )
			.OrderBy( link => link.Connection.DeviceId == HostGateway.LocalDevice ? 0 : 1 )
			.ThenByDescending( link => link.LentAt )
			.FirstOrDefault();
		return lender is null ? null : CallAsync( lender, context, tool, arguments );
	}

	/// <summary>Answers <c>tools.offer</c>: brings the tools the connection offers in the host's registry in line with the new list, and records whether
	/// the connection lends its browser.</summary>
	/// <remarks>
	/// Tools are offered per module. A module whose tools are the same as before, by name, description, schema and module, keeps its offer and
	/// its running calls; only a module whose tools changed or went away is offered again, which cancels its own running calls. A paired device
	/// may offer only browser tools, which only lend its browser and never enter the registry.
	/// </remarks>
	private JsonObject? UpdateOffer( Link link, JsonObject frame )
	{
		var tools = WireJson.Read<AgentToolInfo[]>( frame, "tools" ) ?? [];
		var isLocal = link.Connection.DeviceId == HostGateway.LocalDevice;
		if ( !isLocal )
		{
			if ( tools.Any( tool => tool.ModuleId != BrowserModuleId ) )
				throw new RemoteException( RemoteErrors.Scope, "Only Loom on this PC may offer tools; a paired device may offer its browser tools alone." );
			link.Connection.RequireControl();
		}

		if ( link.Connection.Peer.IsClosed )
			return new JsonObject();

		var isLending = tools.Any( tool => tool.ModuleId == BrowserModuleId );
		if ( isLending != link.LentAt > 0 )
			link.LentAt = isLending ? ++_lendings : 0;
		if ( !_links.Contains( link ) )
			_links.Add( link );
		if ( !isLocal )
			return new JsonObject();

		var modules = tools.GroupBy( tool => tool.ModuleId, StringComparer.Ordinal )
			.ToDictionary( group => group.Key, group => (Tools: group.ToArray(), Key: BuildKey( group )), StringComparer.Ordinal );
		foreach ( var (moduleId, offer) in link.Offers.ToArray() )
		{
			if ( modules.TryGetValue( moduleId, out var next ) && next.Key == offer.Key )
				continue;

			offer.Offer.Dispose();
			link.Offers.Remove( moduleId );
		}
		foreach ( var (moduleId, next) in modules )
		{
			if ( !link.Offers.ContainsKey( moduleId ) )
				link.Offers[moduleId] = (next.Key, app.Tools.Offer( next.Tools, ( context, name, arguments ) => CallAsync( link, context, name, arguments ) ));
		}
		return new JsonObject();
	}

	/// <summary>Builds the text that tells whether a module's offered tools changed: each tool's name, description, schema and module, in name order.</summary>
	private static string BuildKey( IEnumerable<AgentToolInfo> tools ) =>
		string.Join( "\n", tools.OrderBy( tool => tool.Name, StringComparer.Ordinal )
			.Select( tool => JsonSerializer.Serialize( new { tool.Name, tool.Description, Parameters = tool.Parameters.GetRawText(), tool.ModuleId } ) ) );

	/// <summary>Answers <c>tool.result</c>: completes the call it answers; one no longer waited for is ignored.</summary>
	private static JsonObject? UpdateResult( Link link, JsonObject frame )
	{
		var call = Frame.ReadString( frame, "call" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "The result names no call." );
		if ( !link.Calls.TryGetValue( call, out var pending ) )
			return new JsonObject();

		if ( Frame.ReadString( frame, "error" ) is { } error )
			pending.TrySetResult( AgentToolResult.Error( error ) );
		else if ( frame["result"] is JsonObject result )
		{
			var details = result["details"] is { } node ? WireJson.BuildElement( node ) : (JsonElement?)null;
			pending.TrySetResult( new AgentToolResult( Frame.ReadString( result, "text" ) ?? "", Frame.ReadBool( result, "isError" ) == true, details ) );
		}
		else
			throw new RemoteException( RemoteErrors.BadRequest, "The result holds neither result nor error." );
		return new JsonObject();
	}

	/// <summary>Forwards one call of an offered tool as a <c>tool.call</c> push, with the calling panel and the agent's conversation, and waits for its
	/// <c>tool.result</c>, sending <c>tool.cancel</c> when the agent abandons it.</summary>
	private static async Task<AgentToolResult> CallAsync( Link link, AgentToolContext context, string name, JsonElement arguments )
	{
		var peer = link.Connection.Peer;
		var call = Guid.NewGuid().ToString( "N" );
		var pending = new TaskCompletionSource<AgentToolResult>( TaskCreationOptions.RunContinuationsAsynchronously );
		link.Calls[call] = pending;
		try
		{
			var push = Frame.Create( "tool.call" );
			push["call"] = call;
			push["name"] = name;
			push["args"] = JsonSerializer.SerializeToNode( arguments );
			if ( context.Caller is { } caller )
				push["caller"] = caller.ToString( "N" );
			if ( context.Conversation is { } conversation )
				push["conversation"] = conversation.ToString( "N" );
			if ( peer.IsClosed )
				return AgentToolResult.Error( "Loom that offered the tool disconnected." );

			await peer.PushAsync( push ).ConfigureAwait( false );
			using ( context.Cancellation.Register( () =>
			{
				if ( pending.TrySetCanceled( context.Cancellation ) )
					_ = SendCancelAsync( peer, call );
			} ) )
				return await pending.Task.ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or ObjectDisposedException )
		{
			return AgentToolResult.Error( "Loom that offered the tool disconnected." );
		}
		finally
		{
			link.Calls.TryRemove( call, out _ );
		}
	}

	/// <summary>Sends <c>tool.cancel</c>, ignoring a connection that closed.</summary>
	private static async Task SendCancelAsync( RpcPeer peer, string call )
	{
		var push = Frame.Create( "tool.cancel" );
		push["call"] = call;
		try
		{
			await peer.PushAsync( push ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( peer.IsClosed || exception is IOException or ObjectDisposedException )
		{
		}
	}

	/// <summary>Forgets a connection and removes its tools.</summary>
	private void RemoveLink( Link link )
	{
		_links.Remove( link );
		foreach ( var offer in link.Offers.Values )
			offer.Offer.Dispose();
		link.Offers.Clear();
	}

	/// <summary>One connection's offered tools and the calls waiting for its results.</summary>
	/// <param name="connection">The connection.</param>
	private sealed class Link( RemoteConnection connection )
	{
		/// <summary>Gets the connection.</summary>
		public RemoteConnection Connection => connection;

		/// <summary>Gets the registry offer of each module's tools, with the key it was offered under; on the UI thread.</summary>
		public Dictionary<string, (string Key, IDisposable Offer)> Offers { get; } = new( StringComparer.Ordinal );

		/// <summary>Gets the calls waiting for a result, by call id; any thread.</summary>
		public ConcurrentDictionary<string, TaskCompletionSource<AgentToolResult>> Calls { get; } = new( StringComparer.Ordinal );

		/// <summary>Gets or sets when the connection started lending its browser, by the service's count of offers; 0 while it does not.</summary>
		public long LentAt { get; set; }

		/// <summary>Ends every waiting call with an error, as the connection closed.</summary>
		public void FailCalls()
		{
			foreach ( var pending in Calls.Values )
				pending.TrySetResult( AgentToolResult.Error( "Loom that offered the tool disconnected." ) );
		}
	}
}
