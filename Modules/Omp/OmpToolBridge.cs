using System.Collections.Concurrent;
using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Serves Lokn's agent tools to one Omp session over Omp's host-tool sub-protocol.</summary>
/// <remarks>
/// <see cref="Publish"/> sends <c>set_host_tools</c>; Omp then sends <c>host_tool_call</c> for each use, which this bridge runs
/// through <see cref="AgentToolRegistry.InvokeAsync"/> and answers with <c>host_tool_result</c>. The tool list is sent
/// again when modules add or remove tools, or when <see cref="OmpModule.HostTools"/> changes.
/// Create, publish and dispose it on the UI thread; calls arrive on the session's reader thread.
/// </remarks>
public sealed class OmpToolBridge : IDisposable
{
	/// <summary>Stores the session the tools are offered to.</summary>
	private readonly OmpSession _session;

	/// <summary>Stores the application whose tools are offered.</summary>
	private readonly LoknApplication _app;

	/// <summary>Gets the panel showing the agent, read for every call as its caller; null while no panel shows it. Called on the session's reader thread.</summary>
	private readonly Func<Guid?> _caller;

	/// <summary>Stores the conversation whose agent the session runs, which every call names whether or not a panel shows it, or null.</summary>
	private readonly Guid? _conversation;

	/// <summary>Defines the synthetic tool that reveals hidden Lokn tool groups for this chat.</summary>
	private const string LoaderName = "lokn_load_tools";

	/// <summary>Stores the profile whose economy setting controls this chat.</summary>
	private readonly SettingsScope _scope;

	/// <summary>Posts metadata reads to the panel's UI thread.</summary>
	private readonly Func<Action, bool> _post;

	/// <summary>Protects the groups and names shared by UI publishing and reader-thread calls.</summary>
	private readonly Lock _gate = new();

	/// <summary>Stores tool groups revealed through <see cref="LoaderName"/> for this bridge only.</summary>
	private readonly HashSet<string> _loadedGroups = new( StringComparer.Ordinal );

	/// <summary>Stores the exact tool names Omp may call after the last publish.</summary>
	private readonly HashSet<string> _publishedNames = new( StringComparer.Ordinal );

	/// <summary>Cancels in-flight calls when the bridge is disposed.</summary>
	private readonly CancellationTokenSource _lifetime = new();

	/// <summary>Stores the cancellation of each in-flight call by its host request identifier.</summary>
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _calls = new( StringComparer.Ordinal );

	/// <summary>Stores whether tools were offered by the last publish, read on the reader thread.</summary>
	private volatile bool _isEnabled;

	/// <summary>Stores whether economy loading was used by the last publish, read on the reader thread.</summary>
	private volatile bool _isEconomy;

	/// <summary>Reports a publish that Omp refused, such as an older Omp without host tools; raised on the UI thread or a worker.</summary>
	public event Action<string>? Failed;

	/// <summary>Attaches to a session's frames and to tool and setting changes, using the application's active profile and immediate metadata reads.</summary>
	/// <param name="session">The connected session.</param>
	/// <param name="app">The application whose tools are offered.</param>
	/// <param name="caller">Gets the panel showing the agent, such as its chat, or null; read on the session's reader thread for every call.</param>
	public OmpToolBridge( OmpSession session, LoknApplication app, Func<Guid?> caller )
		: this( session, app, caller, app.Settings.ActiveProfile, action =>
		{
			action();
			return true;
		} )
	{
	}

	/// <summary>Attaches to one profile's session and uses the panel dispatcher for reader-thread tool-group loads.</summary>
	/// <param name="session">The connected session.</param>
	/// <param name="app">The application whose tools are offered.</param>
	/// <param name="caller">Gets the panel showing the agent, such as its chat, or null; read on the session's reader thread for every call.</param>
	/// <param name="profile">The account profile captured by the chat.</param>
	/// <param name="post">Posts an action to the chat's UI thread and reports whether it was accepted.</param>
	/// <param name="conversation">The conversation whose agent the session runs, which tools find the chat by while no panel shows it; null for an agent of no chat.</param>
	public OmpToolBridge( OmpSession session, LoknApplication app, Func<Guid?> caller, SettingsProfile profile, Func<Action, bool> post, Guid? conversation = null )
	{
		_session = session;
		_app = app;
		_caller = caller;
		_conversation = conversation;
		_scope = profile == SettingsProfile.Work ? SettingsScope.Work : SettingsScope.Personal;
		_post = post;
		_session.FrameReceived += UpdateFrame;
		_app.Tools.Changed += Publish;
		_app.Settings.Changed += UpdateConfiguration;
	}

	/// <summary>Sends all tools, or only the baseline and on-demand loader in economy mode, or none when agent control is off.</summary>
	public void Publish()
	{
		var enabled = _app.Settings.Get( OmpModule.HostTools );
		var economy = _app.Settings.Get( OmpModule.Economy, _scope );
		if ( economy && !_isEconomy )
		{
			lock ( _gate )
				_loadedGroups.Clear();
		}
		_isEnabled = enabled;
		_isEconomy = economy;
		_ = PublishAsync( BuildTools() );
	}

	/// <summary>Builds provider tool definitions and remembers exactly what the provider may call.</summary>
	private object[] BuildTools()
	{
		var registered = _isEnabled ? _app.Tools.Items : [];
		var tools = !_isEconomy
			? registered
			: registered.Where( tool => IsBaseline( tool ) || IsLoaded( tool.ModuleId ) ).ToArray();
		var payload = tools.Select( BuildTool ).ToList();
		var hiddenGroups = _isEnabled && _isEconomy
			? registered.Where( tool => !IsBaseline( tool ) && !IsLoaded( tool.ModuleId ) ).Select( tool => tool.ModuleId ).Distinct( StringComparer.Ordinal ).Order( StringComparer.Ordinal ).ToArray()
			: [];
		if ( hiddenGroups.Length > 0 )
		{
			payload.Add( new
			{
				name = LoaderName,
				label = "Load Lokn tools",
				description = $"Loads hidden Lokn tool groups for this chat. Load a group before its feature is needed. Available groups: {string.Join( ", ", hiddenGroups )}.",
				parameters = JsonSerializer.SerializeToElement( new
				{
					type = "object",
					properties = new
					{
						groups = new
						{
							type = "array",
							items = new
							{
								type = "string",
								@enum = hiddenGroups,
							},
							minItems = 1,
						},
					},
					required = new[] { "groups" },
					additionalProperties = false,
				} ),
			} );
		}

		lock ( _gate )
		{
			_publishedNames.Clear();
			foreach ( var tool in tools )
				_publishedNames.Add( tool.Name );
			if ( hiddenGroups.Length > 0 )
				_publishedNames.Add( LoaderName );
		}
		return [.. payload];
	}

	/// <summary>Builds one provider host-tool definition.</summary>
	private static object BuildTool( AgentToolInfo tool ) => new
	{
		name = tool.Name,
		label = tool.Name,
		description = tool.Description,
		parameters = tool.Parameters,
	};

	/// <summary>Keeps the shell's panel tools and worktree entry available without loading a feature group.</summary>
	private static bool IsBaseline( AgentToolInfo tool ) => tool.ModuleId == "shell" || tool.Name == "worktrees_enter";

	/// <summary>Reports whether a group has been revealed for this chat.</summary>
	private bool IsLoaded( string group )
	{
		lock ( _gate )
			return _loadedGroups.Contains( group );
	}

	/// <summary>Reports whether a call still names a tool in the current provider list.</summary>
	private bool IsPublished( string name )
	{
		lock ( _gate )
			return _publishedNames.Contains( name );
	}

	/// <summary>Detaches from the session and cancels in-flight calls.</summary>
	public void Dispose()
	{
		_session.FrameReceived -= UpdateFrame;
		_app.Tools.Changed -= Publish;
		_app.Settings.Changed -= UpdateConfiguration;
		_lifetime.Cancel();
	}

	/// <summary>Sends a tool list and reports Omp's refusal instead of throwing into the UI.</summary>
	private async Task PublishAsync( object[] tools )
	{
		try
		{
			await _session.RequestAsync( "set_host_tools", new
			{
				tools
			}, _lifetime.Token ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException ) when ( _lifetime.IsCancellationRequested )
		{
		}
		catch ( Exception error )
		{
			Failed?.Invoke( $"Omp did not accept Lokn's tools: {error.Message}" );
		}
	}

	/// <summary>Publishes again when agent control or this chat's economy mode changes.</summary>
	private void UpdateConfiguration()
	{
		if ( _app.Settings.Get( OmpModule.HostTools ) != _isEnabled || _app.Settings.Get( OmpModule.Economy, _scope ) != _isEconomy )
			Publish();
	}

	/// <summary>Starts or cancels host tool calls; runs on the session's reader thread.</summary>
	private void UpdateFrame( JsonElement frame )
	{
		var type = Read( frame, "type" );
		if ( type == "host_tool_call" )
			_ = RunAsync( frame );
		else if ( type == "host_tool_cancel" && _calls.TryGetValue( Read( frame, "targetId" ), out var call ) )
			call.Cancel();
	}

	/// <summary>Runs one call on the UI thread and always answers it, even when it fails; progress the tool reports streams ahead of the answer.</summary>
	private async Task RunAsync( JsonElement frame )
	{
		var id = Read( frame, "id" );
		if ( id.Length == 0 )
			return;

		using var call = CancellationTokenSource.CreateLinkedTokenSource( _lifetime.Token );
		_calls[id] = call;
		var progress = new ProgressStream( this, id );
		AgentToolResult result;
		try
		{
			var arguments = frame.TryGetProperty( "arguments", out var value ) ? value : default;
			var name = Read( frame, "toolName" );
			if ( !_isEnabled )
				result = AgentToolResult.Error( "The user has turned off agent control of Lokn in Settings." );
			else if ( !IsPublished( name ) )
				result = AgentToolResult.Error( $"The Lokn tool {name} is not loaded for this chat." );
			else if ( name == LoaderName )
				result = await LoadGroupsAsync( arguments, call.Token ).ConfigureAwait( false );
			else
				result = await _app.Tools.InvokeAsync( name, arguments, _caller(), call.Token, Read( frame, "toolCallId" ), progress.Report, _conversation ).ConfigureAwait( false );
		}
		catch ( Exception error )
		{
			result = AgentToolResult.Error( error.Message );
		}
		finally
		{
			progress.Close();
			_calls.TryRemove( id, out _ );
		}

		if ( _lifetime.IsCancellationRequested )
			return;

		await SendAsync( new
		{
			type = "host_tool_result",
			id,
			result = BuildPayload( result ),
			isError = result.IsError,
		} ).ConfigureAwait( false );
	}

	/// <summary>Reveals requested registered module groups on the UI thread, publishes them, and keeps them for this bridge's lifetime.</summary>
	private async Task<AgentToolResult> LoadGroupsAsync( JsonElement arguments, CancellationToken cancellationToken )
	{
		if ( arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty( "groups", out var groups ) || groups.ValueKind != JsonValueKind.Array )
			return AgentToolResult.Error( "groups must be a non-empty array." );
		var requested = groups.EnumerateArray().Where( group => group.ValueKind == JsonValueKind.String ).Select( group => group.GetString()! )
			.Distinct( StringComparer.Ordinal ).ToArray();
		if ( requested.Length == 0 )
			return AgentToolResult.Error( "groups must be a non-empty array." );

		var completion = new TaskCompletionSource<ToolSelection>( TaskCreationOptions.RunContinuationsAsynchronously );
		if ( !_post( () =>
		{
			try
			{
				var known = _app.Tools.Items.Select( tool => tool.ModuleId ).ToHashSet( StringComparer.Ordinal );
				var unknown = requested.Where( group => !known.Contains( group ) ).ToArray();
				if ( unknown.Length == 0 )
				{
					lock ( _gate )
						_loadedGroups.UnionWith( requested );
				}
				completion.TrySetResult( new( unknown, unknown.Length == 0 ? BuildTools() : [] ) );
			}
			catch ( Exception error )
			{
				completion.TrySetException( error );
			}
		} ) )
			return AgentToolResult.Error( "The chat closed before Lokn could load those tools." );

		var selection = await completion.Task.WaitAsync( cancellationToken ).ConfigureAwait( false );
		if ( selection.Unknown.Length > 0 )
			return AgentToolResult.Error( "Unknown Lokn tool groups: " + string.Join( ", ", selection.Unknown ) + "." );
		await PublishAsync( selection.Tools ).ConfigureAwait( false );
		return new( "Loaded Lokn tool groups: " + string.Join( ", ", requested ) + ". Their tools are available now." );
	}

	/// <summary>Holds a UI-thread group selection and the provider list produced from it.</summary>
	private sealed record ToolSelection( string[] Unknown, object[] Tools );

	/// <summary>Sends a frame, ignoring a session that ended while the tool ran; Omp has already rejected its pending call.</summary>
	private async Task SendAsync( object frame )
	{
		try
		{
			await _session.SendAsync( frame, _lifetime.Token ).ConfigureAwait( false );
		}
		catch ( Exception error ) when ( error is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException )
		{
		}
	}

	/// <summary>Builds Omp's tool result shape: the text the agent reads, and the details a tool card draws when there are any.</summary>
	private static object BuildPayload( AgentToolResult result )
	{
		var content = new[] { new { type = "text", text = result.Text } };
		return result.SerializeDetails() is { } details ? new { content, details } : new { content };
	}

	/// <summary>Sends a running call's progress as <c>host_tool_update</c> frames, at most one per interval with the latest report.</summary>
	/// <param name="bridge">The bridge whose session receives the updates.</param>
	/// <param name="id">The host request identifier of the call.</param>
	private sealed class ProgressStream( OmpToolBridge bridge, string id )
	{
		/// <summary>Defines the shortest time between two updates, so a chatty tool does not flood the agent or redraw the chat per line.</summary>
		private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds( 100 );

		/// <summary>Guards the pending report and the stream's state across the tool's threads.</summary>
		private readonly Lock _lock = new();

		/// <summary>Stores the newest report not yet sent.</summary>
		private AgentToolResult? _pending;

		/// <summary>Reports whether a send is scheduled.</summary>
		private bool _isScheduled;

		/// <summary>Reports whether the call has finished, after which reports are dropped.</summary>
		private bool _isClosed;

		/// <summary>Keeps the newest report and schedules a send when none is waiting; callable from any thread.</summary>
		public void Report( AgentToolResult progress )
		{
			lock ( _lock )
			{
				if ( _isClosed )
					return;

				_pending = progress;
				if ( _isScheduled )
					return;

				_isScheduled = true;
			}
			_ = SendAsync();
		}

		/// <summary>Drops reports that arrive after the call has its answer.</summary>
		public void Close()
		{
			lock ( _lock )
				_isClosed = true;
		}

		/// <summary>Waits out the interval, then sends the newest report unless the call has finished.</summary>
		private async Task SendAsync()
		{
			await Task.Delay( Interval ).ConfigureAwait( false );
			AgentToolResult? progress;
			lock ( _lock )
			{
				progress = _isClosed ? null : _pending;
				_pending = null;
				_isScheduled = false;
			}
			if ( progress is null )
				return;

			await bridge.SendAsync( new
			{
				type = "host_tool_update",
				id,
				partialResult = BuildPayload( progress ),
			} ).ConfigureAwait( false );
		}
	}

	/// <summary>Reads a string property, or an empty string.</summary>
	private static string Read( JsonElement element, string property ) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? ""
			: "";
}
