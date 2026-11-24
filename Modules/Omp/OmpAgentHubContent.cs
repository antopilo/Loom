using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Opens a native Agent Hub on one live Omp RPC session.</summary>
/// <param name="Session">The session whose subagents are observed and controlled.</param>
public sealed record OmpAgentHubInput( OmpSession Session );

/// <summary>Describes one Omp subagent snapshot for the native Agent Hub.</summary>
internal sealed record OmpSubagentInfo(
	string Id,
	string Agent,
	string Status,
	string Task,
	string Activity,
	string CurrentTool,
	string Model,
	long Tokens,
	long ContextTokens,
	long ContextWindow,
	double Cost,
	long DurationMilliseconds,
	string SessionFile );

/// <summary>Shows a live Omp subagent roster, transcript and RPC controls.</summary>
internal sealed class OmpAgentHubContent : PanelContent
{
	/// <summary>Defines the widest the roster grows, in logical pixels.</summary>
	private const int RosterWidth = 320;

	/// <summary>Defines the roster's share of the panel width below <see cref="RosterWidth"/>.</summary>
	private const double RosterPercent = 40;

	/// <summary>Defines the height assumed for a roster row until it is measured, in logical pixels.</summary>
	private const double RosterRowEstimate = 96;

	/// <summary>Defines the height assumed for a transcript message until it is measured, in logical pixels.</summary>
	private const double MessageRowEstimate = 64;

	/// <summary>Stores the owning panel's dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Rebuilds after roster, transcript and input changes.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores subagents by stable Omp identifier.</summary>
	private readonly Dictionary<string, OmpSubagentInfo> _agents = new( StringComparer.Ordinal );

	/// <summary>Stores the selected agent's plain transcript.</summary>
	private IReadOnlyList<AgentMessageRow> _messages = [];

	/// <summary>Builds one roster row; the same delegate on every build lets unchanged rows be reused.</summary>
	private readonly Func<AgentRosterRow, Blob> _buildRosterRow;

	/// <summary>Builds one transcript message; the same delegate on every build lets unchanged rows be reused.</summary>
	private readonly Func<AgentMessageRow, Blob> _buildMessageRow;

	/// <summary>Stores the observed live session, or null for a restored panel that needs a chat.</summary>
	private OmpSession? _session;

	/// <summary>Stores the selected subagent identifier.</summary>
	private string _selected = "";

	/// <summary>Stores the pending steering message.</summary>
	private string _steer = "";

	/// <summary>Stores the visible operation state.</summary>
	private string _status = "Open Agent Hub from a connected chat.";

	/// <summary>Reports whether an RPC control action is running.</summary>
	private bool _isBusy;

	/// <summary>Reports whether a transcript read is in progress.</summary>
	private bool _transcriptLoading;

	/// <summary>Reports whether another transcript read became necessary while one was in progress.</summary>
	private bool _transcriptDirty;

	/// <summary>Creates a native Agent Hub, optionally attached to a live chat session.</summary>
	internal OmpAgentHubContent( PanelContext context, OmpSession? session )
	{
		_context = context;
		_buildRosterRow = BuildRosterRow;
		_buildMessageRow = BuildMessageRow;
		if ( session is not null )
			UpdateSession( session );
	}

	/// <summary>Builds the roster and selected agent inspector.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var agents = _agents.Values.OrderBy( agent => StatusOrder( agent.Status ) ).ThenBy( agent => agent.Id, StringComparer.OrdinalIgnoreCase ).ToArray();
		var selected = _agents.GetValueOrDefault( _selected );
		return new Container
		{
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Color = theme.Text,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			Children =
			[
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Padding = theme.Spacing,
					BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
					BorderColor = theme.Border,
					Children =
					[
						new Text { Content = "Agent Hub", FontWeight = 600, FontSize = theme.FontSize + 2, FlexGrow = 1 },
						new Text { Content = $"{agents.Count( agent => agent.Status == "running" )} running · {agents.Length} total", Color = theme.Muted },
						theme.BuildButton( _isBusy ? "Working…" : "Refresh", () => _context.Run( StartRefresh ), _session is not null && !_isBusy ),
					],
				},
				new Row
				{
					FlexGrow = 1,
					MinWidth = 0,
					MinHeight = 0,
					Children =
					[
						BuildRoster( agents ),
						BuildInspector( selected ),
					],
				},
				new Text
				{
					Content = _status,
					Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
					Color = _status.StartsWith( "Could not", StringComparison.Ordinal ) ? theme.Error : theme.Muted,
					Selectable = true,
				},
			],
		};
	}

	/// <summary>Stops observing the chat session when the panel closes.</summary>
	public override void Closed()
	{
		if ( _session is { } session )
		{
			session.FrameReceived -= FrameReceived;
			_ = IgnoreFailureAsync( session.RequestAsync( "set_subagent_subscription", new { level = "off" } ) );
		}
	}

	/// <summary>Gets whether this hub currently observes a given session.</summary>
	internal bool Shows( OmpSession session ) => ReferenceEquals( _session, session );

	/// <summary>Moves the hub to another live chat session and starts its event subscription.</summary>
	internal void UpdateSession( OmpSession session )
	{
		if ( ReferenceEquals( _session, session ) )
			return;
		if ( _session is { } previous )
			previous.FrameReceived -= FrameReceived;
		_session = session;
		_session.FrameReceived += FrameReceived;
		_agents.Clear();
		_messages = [];
		_selected = "";
		_status = "Loading subagents…";
		_isBusy = true;
		MarkChanged();
		_ = SubscribeAsync( session );
	}

	/// <summary>Builds the status-ordered subagent roster.</summary>
	private Blob BuildRoster( IReadOnlyList<OmpSubagentInfo> agents )
	{
		var theme = Theme.Current;
		if ( agents.Count == 0 )
		{
			return new Container
			{
				Width = Length.Percent( RosterPercent ),
				MaxWidth = RosterWidth,
				FlexShrink = 0,
				Padding = theme.Spacing,
				BorderWidth = new Edges( 0, theme.BorderWidth, 0, 0 ),
				BorderColor = theme.Border,
				Children = [new Text { Content = "No subagents in this session.", Padding = theme.Spacing * 2, Color = theme.Muted }],
			};
		}

		return new Virtual
		{
			Key = "agent-roster",
			Source = new MeasuredRowsSource<AgentRosterRow>( [.. agents.Select( agent => new AgentRosterRow( agent.Id, agent, agent.Id == _selected ) )], _buildRosterRow, static _ => RosterRowEstimate, FollowEnd: false ),
			AccessibilityName = "Subagents",
			Width = Length.Percent( RosterPercent ),
			MaxWidth = RosterWidth,
			FlexShrink = 0,
			MinHeight = 0,
			Padding = theme.Spacing,
			OverflowX = Overflow.Hidden,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			BorderWidth = new Edges( 0, theme.BorderWidth, 0, 0 ),
			BorderColor = theme.Border,
		};
	}

	/// <summary>Builds one roster row: status, identifier, task and metrics, selecting the agent on click.</summary>
	private Blob BuildRosterRow( AgentRosterRow row )
	{
		var theme = Theme.Current;
		var item = row.Agent;
		return new Container
		{
			Padding = new Edges( 0, 0, theme.Spacing / 2, 0 ),
			Children =
			[
				new Button
				{
					AccessibilityName = $"Open {item.Id}",
					Focusable = true,
					Cursor = Cursor.Pointer,
					Padding = theme.Spacing,
					Gap = theme.Spacing / 2,
					BorderRadius = theme.Radius,
					BackgroundColor = row.IsSelected ? theme.Accent.WithAlpha( 0.18 ) : Color.Transparent,
					OnClick = () => _context.Run( () => SelectAgent( item.Id ) ),
					Children =
					[
						new Row
						{
							AlignItems = AlignItems.Center,
							Gap = theme.Spacing / 2,
							Children =
							[
								BuildStatusDot( item.Status ),
								new Text { Content = item.Id, FontWeight = 600, FlexGrow = 1, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
								new Text { Content = item.Status, Color = theme.Muted, FontSize = ViewStyle.SmallSize },
							],
						},
						new Text { Content = item.Task.Length > 0 ? item.Task : item.Activity, Color = theme.Muted, TextMaxLines = 2, TextTrimming = TextTrimming.Ellipsis },
						new Text { Content = BuildMetrics( item ), Color = theme.Inactive, FontSize = ViewStyle.SmallSize, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
					],
				},
			],
		};
	}

	/// <summary>Builds one transcript message: its role and its selectable text.</summary>
	private Blob BuildMessageRow( AgentMessageRow row )
	{
		var theme = Theme.Current;
		return new Container
		{
			Padding = new Edges( 0, 0, theme.Spacing, 0 ),
			Children =
			[
				new Container
				{
					Gap = theme.Spacing / 2,
					Padding = theme.Spacing,
					BorderRadius = theme.Radius,
					BackgroundColor = row.Role == "user" ? theme.Accent.WithAlpha( 0.08 ) : theme.Raised,
					Children =
					[
						new Text { Content = row.Role, FontWeight = 600, Color = theme.Muted, FontSize = ViewStyle.SmallSize },
						new Text { Content = row.Text, Selectable = true },
					],
				},
			],
		};
	}

	/// <summary>Builds the selected agent's controls, metadata and transcript.</summary>
	private Blob BuildInspector( OmpSubagentInfo? agent )
	{
		var theme = Theme.Current;
		if ( agent is null )
			return new Text { Content = "Select a subagent to inspect its work and transcript.", Padding = theme.Spacing * 2, Color = theme.Muted, FlexGrow = 1 };

		var canControl = agent.Status is "running" or "idle";
		Blob transcript = _messages.Count == 0
			? new Container
			{
				FlexGrow = 1,
				MinHeight = 0,
				Padding = theme.Spacing,
				BorderWidth = theme.BorderWidth,
				BorderColor = theme.Border,
				BorderRadius = theme.Radius,
				Children = [new Text { Content = _transcriptLoading ? "Reading transcript…" : "No transcript messages yet.", Color = theme.Muted }],
			}
			: new Virtual
			{
				Key = "agent-transcript:" + agent.Id,
				Source = new MeasuredRowsSource<AgentMessageRow>( _messages, _buildMessageRow, static _ => MessageRowEstimate, FollowEnd: false ),
				AccessibilityName = "Transcript",
				FlexGrow = 1,
				MinHeight = 0,
				Padding = theme.Spacing,
				OverflowX = Overflow.Hidden,
				ScrollbarY = ViewStyle.BuildScrollbar(),
				BorderWidth = theme.BorderWidth,
				BorderColor = theme.Border,
				BorderRadius = theme.Radius,
			};
		Blob steering = canControl
			? new TextEntry
			{
				Value = _steer,
				Controlled = true,
				Placeholder = "Steer this subagent",
				AccessibilityName = "Subagent message",
				FlexGrow = 1,
				OnChange = value =>
				{
					_steer = value;
					MarkChanged();
				},
				OnSubmit = _ => _context.Run( () => StartSteer( agent.Id ) ),
			}
			: new Text
			{
				Content = "Revive is not exposed by Omp RPC. Use Omp's Agent Hub or agent:// messaging.",
				Color = theme.Muted,
				FlexGrow = 1,
			};

		return new Container
		{
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Padding = theme.Spacing * 1.5,
			Gap = theme.Spacing,
			Children =
			[
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Children =
					[
						new Text { Content = agent.Id, FontWeight = 600, FontSize = theme.FontSize + 2, FlexGrow = 1, MinWidth = 0 },
						theme.BuildButton( "Stop", () => _context.Run( () => StartCancel( agent.Id ) ), !_isBusy && agent.Status == "running" ),
					],
				},
				new Text { Content = BuildInspectorMetadata( agent ), Color = theme.Muted, Selectable = true },
				new Text { Content = agent.Activity, Color = theme.Text, TextMaxLines = 3 },
				transcript,
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Children =
					[
						steering,
						theme.BuildButton( "Send", () => _context.Run( () => StartSteer( agent.Id ) ), canControl && !_isBusy && _steer.Trim().Length > 0 ),
					],
				},
			],
		};
	}

	/// <summary>Subscribes to full subagent events, then reads the initial roster.</summary>
	private async Task SubscribeAsync( OmpSession session )
	{
		try
		{
			await session.RequestAsync( "set_subagent_subscription", new { level = "events" }, _context.Lifetime ).ConfigureAwait( false );
			await RefreshAsync( session ).ConfigureAwait( false );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => FinishError( "Could not open Agent Hub: " + error.Message ) );
		}
	}

	/// <summary>Starts an explicit roster refresh.</summary>
	private void StartRefresh()
	{
		if ( _session is not { } session || _isBusy )
			return;
		_isBusy = true;
		_status = "Refreshing subagents…";
		MarkChanged();
		_ = RefreshAsync( session );
	}

	/// <summary>Reads and applies the authoritative Omp subagent snapshot.</summary>
	private async Task RefreshAsync( OmpSession session, bool completesBusyOperation = true )
	{
		try
		{
			var result = await session.RequestAsync( "get_subagents", cancellationToken: _context.Lifetime ).ConfigureAwait( false );
			var agents = BuildSubagents( result );
			_context.Post( () =>
			{
				if ( !ReferenceEquals( _session, session ) )
					return;
				_agents.Clear();
				foreach ( var agent in agents )
					_agents[agent.Id] = agent;
				if ( _selected.Length == 0 || !_agents.ContainsKey( _selected ) )
					_selected = agents.FirstOrDefault()?.Id ?? "";
				if ( completesBusyOperation )
					_isBusy = false;
				_status = "Live subagent events connected.";
				MarkChanged();
				StartTranscriptRead();
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () =>
			{
				if ( completesBusyOperation )
					FinishError( "Could not refresh subagents: " + error.Message );
				else
				{
					_status = "Could not refresh subagents: " + error.Message;
					MarkChanged();
				}
			} );
		}
	}

	/// <summary>Marshals subagent frames from the transport reader to the panel thread.</summary>
	private void FrameReceived( JsonElement frame )
	{
		var type = ReadText( frame, "type" );
		if ( type.StartsWith( "subagent_", StringComparison.Ordinal ) )
			_context.Post( () => ApplySubagentFrame( frame ) );
	}

	/// <summary>Applies live progress immediately and refreshes terminal lifecycle state from the registry.</summary>
	private void ApplySubagentFrame( JsonElement frame )
	{
		var type = ReadText( frame, "type" );
		if ( type == "subagent_progress" && frame.TryGetProperty( "payload", out var payload ) && payload.TryGetProperty( "progress", out var progress ) )
		{
			var agent = BuildSubagent( progress, payload );
			_agents[agent.Id] = agent;
			if ( _selected.Length == 0 )
				_selected = agent.Id;
			MarkChanged();
			if ( agent.Id == _selected )
				StartTranscriptRead();
			return;
		}
		if ( type == "subagent_event" )
		{
			StartTranscriptRead();
			return;
		}
		if ( _session is { } session )
			_ = RefreshAsync( session, completesBusyOperation: false );
	}

	/// <summary>Selects an agent and reads its persisted transcript.</summary>
	private void SelectAgent( string id )
	{
		if ( _selected == id )
			return;
		_selected = id;
		_messages = [];
		_steer = "";
		MarkChanged();
		StartTranscriptRead();
	}

	/// <summary>Coalesces transcript reads while subagent events stream quickly.</summary>
	private void StartTranscriptRead()
	{
		if ( _session is not { } session || _selected.Length == 0 )
			return;
		if ( _transcriptLoading )
		{
			_transcriptDirty = true;
			return;
		}
		_transcriptLoading = true;
		_transcriptDirty = false;
		MarkChanged();
		_ = ReadTranscriptAsync( session, _selected );
	}

	/// <summary>Reads the selected agent's complete message transcript.</summary>
	private async Task ReadTranscriptAsync( OmpSession session, string id )
	{
		try
		{
			var result = await session.RequestAsync( "get_subagent_messages", new { subagentId = id }, _context.Lifetime ).ConfigureAwait( false );
			var messages = BuildMessages( result );
			_context.Post( () =>
			{
				if ( ReferenceEquals( _session, session ) && _selected == id )
					_messages = messages;
				_transcriptLoading = false;
				MarkChanged();
				if ( _transcriptDirty )
					StartTranscriptRead();
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () =>
			{
				_transcriptLoading = false;
				_status = "Could not read transcript: " + error.Message;
				MarkChanged();
			} );
		}
	}

	/// <summary>Starts a hard stop of one running subagent.</summary>
	private void StartCancel( string id )
	{
		if ( _session is not { } session || _isBusy )
			return;
		_isBusy = true;
		_status = $"Stopping {id}…";
		MarkChanged();
		_ = ControlAsync( session, "cancel_subagent", new { subagentId = id }, $"Stopped {id}." );
	}

	/// <summary>Starts a steering message to one running or idle subagent.</summary>
	private void StartSteer( string id )
	{
		var message = _steer.Trim();
		if ( _session is not { } session || _isBusy || message.Length == 0 )
			return;
		_isBusy = true;
		_status = $"Steering {id}…";
		_steer = "";
		MarkChanged();
		_ = ControlAsync( session, "steer_subagent", new { subagentId = id, message }, $"Message sent to {id}." );
	}

	/// <summary>Runs a subagent control command and refreshes the roster after it succeeds.</summary>
	private async Task ControlAsync( OmpSession session, string command, object fields, string success )
	{
		try
		{
			await session.RequestAsync( command, fields, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				_isBusy = false;
				_status = success;
				MarkChanged();
				_ = RefreshAsync( session );
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => FinishError( "Could not control subagent: " + error.Message ) );
		}
	}

	/// <summary>Parses an Omp <c>get_subagents</c> response.</summary>
	internal static IReadOnlyList<OmpSubagentInfo> BuildSubagents( JsonElement result )
	{
		if ( result.ValueKind != JsonValueKind.Object || !result.TryGetProperty( "subagents", out var items ) || items.ValueKind != JsonValueKind.Array )
			return [];
		return [.. items.EnumerateArray().Select( item => BuildSubagent( item.TryGetProperty( "progress", out var progress ) ? progress : item, item ) )];
	}

	/// <summary>Builds one normalized subagent from a snapshot or progress payload.</summary>
	private static OmpSubagentInfo BuildSubagent( JsonElement progress, JsonElement fallback )
	{
		var id = ReadText( progress, "id", ReadText( fallback, "id" ) );
		var activity = ReadText( progress, "stage", ReadText( progress, "lastIntent", ReadText( fallback, "description" ) ) );
		var tool = ReadText( progress, "currentTool" );
		if ( tool.Length > 0 && ReadText( progress, "currentToolArgs" ) is { Length: > 0 } arguments )
			tool += " " + arguments;
		return new(
			id,
			ReadText( progress, "agent", ReadText( fallback, "agent" ) ),
			ReadText( progress, "status", ReadText( fallback, "status" ) ),
			ReadText( progress, "task", ReadText( fallback, "task", ReadText( fallback, "assignment" ) ) ),
			activity,
			tool,
			ReadText( progress, "resolvedModel", ReadText( progress, "modelRole" ) ),
			ReadInteger( progress, "tokens" ),
			ReadInteger( progress, "contextTokens" ),
			ReadInteger( progress, "contextWindow" ),
			ReadDouble( progress, "cost" ),
			ReadInteger( progress, "durationMs" ),
			ReadText( fallback, "sessionFile" ) );
	}

	/// <summary>Parses plain user and assistant text from a subagent message response, keyed by each message's position in the append-only transcript.</summary>
	private static IReadOnlyList<AgentMessageRow> BuildMessages( JsonElement result )
	{
		if ( result.ValueKind != JsonValueKind.Object || !result.TryGetProperty( "messages", out var messages ) || messages.ValueKind != JsonValueKind.Array )
			return [];
		var rows = new List<AgentMessageRow>();
		foreach ( var (position, message) in messages.EnumerateArray().Index() )
		{
			var role = ReadText( message, "role" );
			if ( role is not ("user" or "assistant") )
				continue;
			var text = ReadMessageText( message );
			if ( text.Length > 0 )
				rows.Add( new( position.ToString(), role, text ) );
		}
		return rows;
	}

	/// <summary>Reads text blocks from one Omp message.</summary>
	private static string ReadMessageText( JsonElement message )
	{
		if ( !message.TryGetProperty( "content", out var content ) )
			return ReadText( message, "text" );
		if ( content.ValueKind == JsonValueKind.String )
			return content.GetString() ?? "";
		if ( content.ValueKind != JsonValueKind.Array )
			return "";
		return string.Join( "\n", content.EnumerateArray()
			.Where( block => ReadText( block, "type" ) is "text" or "thinking" )
			.Select( block => ReadText( block, "text", ReadText( block, "thinking" ) ) )
			.Where( text => text.Length > 0 ) );
	}

	/// <summary>Builds a compact roster metrics line.</summary>
	private static string BuildMetrics( OmpSubagentInfo agent )
	{
		var parts = new List<string>();
		if ( agent.Model.Length > 0 )
			parts.Add( agent.Model );
		if ( agent.Tokens > 0 )
			parts.Add( $"{OmpModelPicker.FormatTokens( agent.Tokens )} tok" );
		if ( agent.Cost > 0 )
			parts.Add( $"${agent.Cost:0.####}" );
		if ( agent.DurationMilliseconds > 0 )
			parts.Add( TimeSpan.FromMilliseconds( agent.DurationMilliseconds ).ToString( @"m\:ss" ) );
		return string.Join( " · ", parts );
	}

	/// <summary>Builds detailed inspector metadata.</summary>
	private static string BuildInspectorMetadata( OmpSubagentInfo agent )
	{
		var parts = new List<string> { agent.Agent, agent.Status };
		if ( agent.Model.Length > 0 )
			parts.Add( agent.Model );
		if ( agent.ContextWindow > 0 )
			parts.Add( $"context {OmpModelPicker.FormatTokens( agent.ContextTokens )}/{OmpModelPicker.FormatTokens( agent.ContextWindow )}" );
		if ( agent.CurrentTool.Length > 0 )
			parts.Add( "tool " + agent.CurrentTool );
		if ( agent.SessionFile.Length > 0 )
			parts.Add( agent.SessionFile );
		return string.Join( "  ·  ", parts.Where( part => part.Length > 0 ) );
	}

	/// <summary>Builds the status color marker for one roster row.</summary>
	private static Container BuildStatusDot( string status ) => new()
	{
		Width = 8,
		Height = 8,
		FlexShrink = 0,
		BorderRadius = 4,
		BackgroundColor = status switch
		{
			"running" => Theme.Current.Success,
			"failed" or "aborted" => Theme.Current.Error,
			"pending" => Theme.Current.AnsiYellow,
			_ => Theme.Current.Muted,
		},
	};

	/// <summary>Orders active work before idle and terminal agents.</summary>
	private static int StatusOrder( string status ) => status switch
	{
		"running" => 0,
		"pending" => 1,
		"idle" => 2,
		"parked" => 3,
		_ => 4,
	};

	/// <summary>Reads a string property or a fallback.</summary>
	private static string ReadText( JsonElement value, string property, string fallback = "" ) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty( property, out var item ) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;

	/// <summary>Reads an integer-compatible number property.</summary>
	private static long ReadInteger( JsonElement value, string property ) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty( property, out var item ) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64( out var number ) ? number : 0;

	/// <summary>Reads a floating-point number property.</summary>
	private static double ReadDouble( JsonElement value, string property ) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty( property, out var item ) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0;

	/// <summary>Ends a failed operation and exposes its diagnostic.</summary>
	private void FinishError( string message )
	{
		_isBusy = false;
		_status = message;
		MarkChanged();
	}

	/// <summary>Ignores cleanup failures after the panel has detached.</summary>
	private static async Task IgnoreFailureAsync( Task task )
	{
		try
		{
			await task.ConfigureAwait( false );
		}
		catch
		{
		}
	}

	/// <summary>Rebuilds after mutable panel state changes.</summary>
	private void MarkChanged() => _version.Value++;

	/// <summary>Places one subagent in the roster.</summary>
	/// <param name="Key">The subagent's Omp identifier.</param>
	/// <param name="Agent">The subagent shown.</param>
	/// <param name="IsSelected">Whether the inspector shows this subagent.</param>
	private sealed record AgentRosterRow( string Key, OmpSubagentInfo Agent, bool IsSelected ) : IMeasuredRow;

	/// <summary>Places one transcript message in the inspector.</summary>
	/// <param name="Key">The message's position in Omp's transcript.</param>
	/// <param name="Role">The author role, "user" or "assistant".</param>
	/// <param name="Text">The message's plain text.</param>
	private sealed record AgentMessageRow( string Key, string Role, string Text ) : IMeasuredRow;
}
