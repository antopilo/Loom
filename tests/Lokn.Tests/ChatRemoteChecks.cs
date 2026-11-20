using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Tests;

/// <summary>Checks a chat shown in another process: store operations between a host and its mirror, and a proxy session over a real (fixture) Omp turn.</summary>
/// <remarks>Every value crosses the in-memory link as protocol JSON, as it would over the Remote module's connection.</remarks>
internal static class ChatRemoteChecks
{
	/// <summary>Runs the store operation and proxy session checks.</summary>
	internal static void Run()
	{
		CheckStoreOps();
		CheckProxyTurn();
		CheckProxyResync();
		Console.WriteLine( "PASS store operations between a host and its mirror, mirror creation identifiers, a proxy chat session over a real Omp turn, and a proxy's sequence numbers, attach answers after a reconnect or host restart, sent prompts and disposal." );
	}

	/// <summary>Checks that mirror stores show their own changes at once and send them to the host, whose operations coming back change nothing again, and agree on created identifiers.</summary>
	private static void CheckStoreOps()
	{
		var hostProjects = new ProjectStore();
		var host = new ConversationStore( hostProjects );
		var project = hostProjects.Create( SettingsProfile.Personal, "Alpha", Path.GetTempPath() );
		var existing = host.CreateConversation( SettingsProfile.Personal, project.Id, "Existing" );

		var mirrorProjects = new ProjectStore();
		var mirror = new ConversationStore( mirrorProjects );
		var sent = new List<StoreOp>();
		mirrorProjects.Forward = op =>
		{
			sent.Add( op );
			hostProjects.Apply( Wire( op ) );
			return true;
		};
		mirror.Forward = op =>
		{
			sent.Add( op );
			host.Apply( Wire( op ) );
			return true;
		};
		hostProjects.Mutated += op => mirrorProjects.Apply( Wire( op ) );
		host.Mutated += op => mirror.Apply( Wire( op ) );
		mirrorProjects.Load( Wire( hostProjects.Projects( SettingsProfile.Personal ).ToArray() ) );
		mirror.Load( Wire( host.Conversations( SettingsProfile.Personal, project.Id ).ToArray() ) );
		CoreChecks.Check( mirror.IsMirror && mirrorProjects.IsMirror && mirror.Get( existing.Id ) == existing && mirrorProjects.Get( project.Id ) == project,
			"A mirror loads the host's projects and conversations from a snapshot." );

		var created = mirror.CreateConversation( SettingsProfile.Personal, project.Id, "From the mirror" );
		CoreChecks.Check( sent[^1] is { S: StoreOp.Conversations, Op: nameof( ConversationStore.CreateConversation ) } && host.Find( created.Id ) == created && mirror.Find( created.Id ) == created,
			"A mirror's new conversation is created on the host under the identifier and time the mirror returned." );

		mirror.RenameConversation( created.Id, "Renamed remotely" );
		mirror.SetPinned( existing.Id, true );
		mirror.SaveDraft( created.Id, "Half a thought" );
		CoreChecks.Check( host.Get( created.Id ) is { Title: "Renamed remotely", IsTitleChosen: true, Draft: "Half a thought" } && host.Get( existing.Id ).IsPinned
			&& mirror.Get( created.Id ) == host.Get( created.Id ) && mirror.Get( existing.Id ) == host.Get( existing.Id ),
			"A mirror's changes apply on the host and come back to the mirror as the host's operations." );

		host.SetAutomaticTitle( existing.Id, "**Title:** Host side name.", isGenerated: true );
		CoreChecks.Check( mirror.Get( existing.Id ) == host.Get( existing.Id ) && host.Get( existing.Id ).Title == "Host side name",
			"A host's own change reaches the mirror with the arguments it was called with." );

		var made = mirrorProjects.Create( SettingsProfile.Personal, "Beta", Path.GetTempPath() );
		CoreChecks.Check( hostProjects.Find( made.Id ) == made && mirrorProjects.Find( made.Id ) == made, "A mirror's new project is created on the host under the same identifier." );
		mirrorProjects.Update( made.Id, "Beta renamed", Path.GetTempPath() );
		CoreChecks.Check( hostProjects.Get( made.Id ).Name == "Beta renamed" && mirrorProjects.Get( made.Id ) == hostProjects.Get( made.Id ), "A mirror's project change applies on the host and comes back." );
		CoreChecks.Check( mirrorProjects.Delete( project.Id ) && hostProjects.Find( project.Id ) is null && mirrorProjects.Find( project.Id ) is null
			&& host.Get( created.Id ).ProjectId is null && mirror.Get( created.Id ).ProjectId is null,
			"Deleting a project from a mirror deletes it on the host, and both take its chats out of it." );
		CoreChecks.Check( mirror.DeleteConversation( created.Id ) && host.Find( created.Id ) is null && mirror.Find( created.Id ) is null, "A mirror deletes a conversation through the host." );

		// A host that has not answered yet: the mirror shows its own change at once, so its caller reads it back.
		var silent = new ConversationStore( new ProjectStore() );
		silent.Load( Wire( new[] { host.Get( existing.Id ) } ) );
		var unanswered = new List<StoreOp>();
		silent.Forward = op =>
		{
			unanswered.Add( op );
			return true;
		};
		var raised = new List<StoreOp>();
		silent.Mutated += raised.Add;
		silent.RenameConversation( existing.Id, "Not yet" );
		silent.SaveDraft( existing.Id, "Typed on Home" );
		CoreChecks.Check( silent.Get( existing.Id ) is { Title: "Not yet", IsTitleChosen: true, Draft: "Typed on Home" }
			&& unanswered is [{ Op: nameof( ConversationStore.RenameConversation ) } rename, { Op: nameof( ConversationStore.SaveDraft ) }]
			&& rename.Read<string>( "title" ) == "Not yet" && rename.Read<Guid>( "id" ) == existing.Id,
			"A mirror shows its own change at once, so its caller reads it back, and sends it with its named arguments." );
		foreach ( var op in unanswered.ToArray() )
			silent.Apply( Wire( op ) );
		CoreChecks.Check( raised.Count == 0 && unanswered.Count == 2 && silent.Get( existing.Id ) is { Title: "Not yet", Draft: "Typed on Home" },
			"The host's operation for a mirror's own change finds it applied: nothing changes, and nothing is sent again." );

		silent.SaveDraft( existing.Id, "a" );
		silent.SaveDraft( existing.Id, "ab" );
		foreach ( var op in unanswered.Skip( 2 ).ToArray() )
			silent.Apply( Wire( op ) );
		CoreChecks.Check( silent.Get( existing.Id ).Draft == "ab", "Operations for quick successive changes come back in order and leave the mirror at the last one." );

		var task = silent.CreateConversation( SettingsProfile.Personal, title: "Task", hasChat: false );
		silent.AddChat( task.Id );
		CoreChecks.Check( silent.Get( task.Id ).HasChat, "A mirror's task gains its chat at once, so the chat starter opens it." );

		var sentBefore = unanswered.Count;
		silent.MoveConversation( existing.Id, task.Id );
		CoreChecks.Check( unanswered.Count == sentBefore + 1 && unanswered[^1].Op == nameof( ConversationStore.MoveConversation ) && silent.Get( existing.Id ).Position is null,
			"A mirror only sends a move, since moving twice is not a no-op." );
		silent.Apply( Wire( unanswered[^1] ) );
		CoreChecks.Check( silent.Get( existing.Id ).Position is not null, "A move shows once the host's operation comes back." );

		var message = new ConversationMessage( Guid.NewGuid(), "user", "Hello", DateTimeOffset.UtcNow );
		var isRejected = false;
		try
		{
			host.Apply( StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.AppendMessage ), new { conversationId = existing.Id, message } ) );
		}
		catch ( ArgumentException )
		{
			isRejected = true;
		}
		CoreChecks.Check( isRejected && host.GetMessages( existing.Id ).Count == 0, "Messages are the host's: a client cannot append one through a store operation." );
		silent.ApplyMessage( existing.Id, message, persist: true );
		silent.ApplyMessage( existing.Id, message with { Text = "Hello again" }, persist: true );
		CoreChecks.Check( silent.GetMessages( existing.Id ) is [{ Text: "Hello again" }], "A pushed message is appended, and a push with its identifier replaces it." );

		var deleted = new List<Guid>();
		silent.Deleted += deleted.Add;
		silent.DeleteConversation( task.Id );
		silent.Apply( Wire( unanswered[^1] ) );
		CoreChecks.Check( silent.Find( task.Id ) is null && deleted.SequenceEqual( [task.Id] ), "A mirror's delete raises Deleted at once, and its operation coming back raises it no more." );
		silent.Load( [] );
		CoreChecks.Check( deleted.SequenceEqual( [task.Id, existing.Id] ), "A snapshot without a conversation raises Deleted for it." );
	}

	/// <summary>Checks a proxy session in a 'client' over a local session in a 'host' that runs a real (fixture) Omp turn, with the client's history fed only by pushes.</summary>
	private static void CheckProxyTurn()
	{
		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		var omp = new OmpModule();
		CoreChecks.Check( app.Load( omp ), "Omp loads for the proxy session check." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		app.Settings.Set( OmpModule.Model, SettingsScope.Global, "fixture/one" );
		var host = new ConversationStore( new ProjectStore() );
		var chat = host.CreateConversation( SettingsProfile.Personal );
		host.SaveBackendSession( chat.Id, Path.Combine( Path.GetTempPath(), "lokn-remote-" + Guid.NewGuid().ToString( "N" ) ) );
		bool PumpUntil( Func<bool> condition )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while ( !condition() && clock.Elapsed.TotalSeconds < 30 )
			{
				Action[] actions;
				lock ( queued )
				{
					actions = [.. queued];
					queued.Clear();
				}
				foreach ( var action in actions )
					action();
				Thread.Sleep( 20 );
			}
			return condition();
		}

		// The client's history mirrors the host's through store operations and message pushes only.
		var mirror = new ConversationStore( new ProjectStore() );
		mirror.Forward = op => app.Post( () => host.Apply( Wire( op ) ) );
		mirror.Load( Wire( host.Conversations( SettingsProfile.Personal ).ToArray() ) );
		host.Mutated += op =>
		{
			var wired = Wire( op );
			app.Post( () => mirror.Apply( wired ) );
		};

		var session = new ChatSession( app, host, chat.Id, omp.Service, app.Post );
		var channel = new LoopbackChannel( session, app.Post );
		var proxy = new RemoteChatSession( app, mirror, chat.Id, omp.Service, channel, app.Post );
		long seq = 0;
		void Push( Action<long> apply )
		{
			var number = ++seq;
			app.Post( () => apply( number ) );
		}
		host.MessageApplied += ( id, pushed, persist ) =>
		{
			var wired = Wire( pushed );
			if ( id == chat.Id )
				Push( number => proxy.ApplyMessage( number, wired, persist ) );
		};
		host.MessagesReplaced += ( id, replaced ) =>
		{
			var wired = Wire( replaced.ToArray() );
			if ( id == chat.Id )
				Push( number => proxy.ApplyMessages( number, wired ) );
		};
		using var feed = new ChatSessionFeed( session,
			state =>
			{
				var wired = Wire( state );
				Push( number => proxy.ApplyState( number, wired ) );
			},
			( id, frame ) =>
			{
				var wired = Wire( frame );
				Push( number => proxy.ApplyTool( number, id, wired ) );
			},
			sessionEvent =>
			{
				var wired = Wire( sessionEvent );
				Push( number => proxy.ApplyEvent( number, wired ) );
			} );
		proxy.ApplyAttach( seq, Wire( feed.State ), session.Tools, session.Intents, Wire( host.GetMessages( chat.Id ).ToArray() ) );

		var sawBusy = false;
		var dialogs = 0;
		var left = new List<string>();
		proxy.Changed += () => sawBusy |= proxy.IsBusy;
		proxy.DialogsChanged += () => dialogs++;
		proxy.DialogLeft += dialog => left.Add( dialog.GetProperty( "id" ).GetString() ?? "" );
		proxy.Attach( Guid.NewGuid() );
		CoreChecks.Check( proxy.IsRemote && !proxy.IsBusy && proxy.Connected is null && proxy.ComputeFolderRefusal( mirror.Get( chat.Id ) ) is null,
			"A proxy starts idle from the host's state, with no Omp process of its own." );

		proxy.Send( "Run remote", [] );
		CoreChecks.Check( PumpUntil( () => proxy.PendingDialog is not null ) && sawBusy && dialogs > 0 && proxy.ConnectedModel == "fixture/one"
			&& proxy.PendingDialog!.Value.GetProperty( "id" ).GetString() == "approval",
			"A prompt sent from the proxy runs in the host, and the proxy shows the agent working, its model and its question." );
		CoreChecks.Check( proxy.ComputeFolderRefusal( mirror.Get( chat.Id ) ) is not null && !proxy.Interject( "", [], steer: false ),
			"While the host's agent works, the proxy refuses a folder change and an empty interjection from the host's last state." );

		proxy.AnswerDialog( "approval", new { type = "extension_ui_response", id = "approval", confirmed = true } );
		CoreChecks.Check( PumpUntil( () => !session.IsBusy && !proxy.IsBusy && proxy.PendingDialog is null && proxy.Status == session.Status ),
			"Answering the question from the proxy lets the host's turn settle, and the proxy follows it to idle." );
		CoreChecks.Check( left.Contains( "approval" ) && channel.Methods.Contains( ChatSessionCall.Send ) && channel.Methods.Contains( ChatSessionCall.AnswerDialog ),
			"The proxy sends its prompt and answer as whitelisted calls and sees the question leave." );

		CoreChecks.Check( PumpUntil( () => mirror.Get( chat.Id ).Title == host.Get( chat.Id ).Title && mirror.GetMessages( chat.Id ).Count == host.GetMessages( chat.Id ).Count ),
			"The mirror history catches up with the host's title and messages." );
		var messages = mirror.GetMessages( chat.Id );
		CoreChecks.Check( messages.Any( message => message.Role == "user" && message.Text == "Run remote" )
			&& messages.Any( message => message.Role == "assistant" && message.Text.StartsWith( "Streaming complete.", StringComparison.Ordinal ) )
			&& !mirror.IsWorking( chat.Id ) && mirror.Get( chat.Id ).Title != "New chat",
			"The mirror, fed only by store operations and message pushes, holds the turn's messages and title and shows the chat idle." );

		var isRejected = ChatSessionCall.InvokeAsync( session, nameof( IChatSession.Dispose ), [] ).Exception?.InnerException is ArgumentException;
		CoreChecks.Check( isRejected && !ChatSessionCall.Methods.Contains( nameof( IChatSession.EnsureConnection ) ), "Only whitelisted session methods can be called by name." );

		proxy.Dispose();
		session.Dispose();
		app.Shutdown();
	}

	/// <summary>Checks a proxy without a host: stale pushes are ignored, an attach answer replaces what it showed (a host restart's lower sequence number
	/// included) and tells the tool rows and questions that changed, a sent prompt shows working until the host answers or refuses it, and a disposed
	/// proxy takes nothing more.</summary>
	private static void CheckProxyResync()
	{
		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		bool PumpUntil( Func<bool> condition )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while ( !condition() && clock.Elapsed.TotalSeconds < 10 )
			{
				Action[] actions;
				lock ( queued )
				{
					actions = [.. queued];
					queued.Clear();
				}
				foreach ( var action in actions )
					action();
				Thread.Sleep( 5 );
			}
			return condition();
		}

		var omp = new OmpModule();
		CoreChecks.Check( app.Load( omp ), "Omp loads for the proxy resync check." );
		var store = new ConversationStore( new ProjectStore() );
		var chat = store.CreateConversation( SettingsProfile.Personal );
		var channel = new ScriptedChannel();
		var proxy = new RemoteChatSession( app, store, chat.Id, omp.Service, channel, app.Post );
		var changedTools = new List<string>();
		var dialogsChanged = 0;
		var refused = 0;
		proxy.ToolChanged += changedTools.Add;
		proxy.DialogsChanged += () => dialogsChanged++;
		proxy.PromptRefused += () => refused++;

		JsonElement Json( string text ) => JsonDocument.Parse( text ).RootElement.Clone();
		var question = Json( """{"id":"q1","method":"confirm","title":"Go?"}""" );
		var running = Json( """{"type":"tool_execution_start","intent":"Reading"}""" );
		var message = new ConversationMessage( Guid.NewGuid(), "assistant", "First", DateTimeOffset.UtcNow );
		proxy.ApplyAttach( 10, new ChatSessionState { IsBusy = true, IsConnected = true, Status = "Working", Dialogs = [question] },
			new Dictionary<string, JsonElement> { ["kept"] = running, ["gone"] = running }, new Dictionary<string, string> { ["kept"] = "Reading" }, [message] );
		CoreChecks.Check( proxy.Seq == 10 && proxy.IsBusy && proxy.PendingDialog is not null && store.GetMessages( chat.Id ).Single().Text == "First"
			&& changedTools.Order().SequenceEqual( ["gone", "kept"] ) && dialogsChanged == 1,
			"A first attach answer sets the sequence number, state, messages and tools, and tells the new tool rows and questions." );

		proxy.ApplyState( 10, new ChatSessionState { Status = "Stale" } );
		proxy.ApplyState( 9, new ChatSessionState { Status = "Older" } );
		CoreChecks.Check( proxy.Status == "Working" && proxy.Seq == 10, "A push at or below the last sequence number is ignored." );
		proxy.ApplyState( 11, new ChatSessionState { IsBusy = true, IsConnected = true, Status = "Next", Dialogs = [question] } );
		CoreChecks.Check( proxy.Status == "Next" && proxy.Seq == 11, "The next push applies." );

		// A reconnect to a restarted host: its sequence numbers start over, its question was answered and one tool finished meanwhile.
		changedTools.Clear();
		dialogsChanged = 0;
		proxy.ApplyAttach( 2, new ChatSessionState { IsConnected = true, Status = "Ready" },
			new Dictionary<string, JsonElement> { ["kept"] = running }, new Dictionary<string, string>(), null );
		CoreChecks.Check( proxy.Seq == 2 && !proxy.IsBusy && proxy.PendingDialog is null && changedTools.SequenceEqual( ["gone"] ) && dialogsChanged == 1
			&& proxy.Intents.Count == 0 && store.GetMessages( chat.Id ).Single().Text == "First",
			"An attach answer after a host restart takes its lower sequence number, keeps the messages it did not resend, and tells the tool row and question that changed." );
		proxy.ApplyState( 3, new ChatSessionState { IsConnected = true, Status = "After restart" } );
		CoreChecks.Check( proxy.Status == "After restart", "Pushes after a restart's attach apply from its sequence number." );

		// A sent prompt shows working at once; the host's refusal brings it back to the composer and idle.
		channel.Fail = true;
		proxy.Send( "Hello", [] );
		CoreChecks.Check( proxy.IsBusy && proxy.Status == "Sending…" && proxy.ComputeFolderRefusal( store.Get( chat.Id ) ) is not null,
			"A sent prompt shows the chat working before the host's state comes, as a local session does, and refuses a folder change meanwhile." );
		CoreChecks.Check( PumpUntil( () => refused == 1 ) && !proxy.IsBusy && proxy.Status.StartsWith( "The host did not take it", StringComparison.Ordinal ),
			"A prompt the host did not take is refused, and the proxy is idle again with the reason." );
		channel.Fail = false;
		proxy.Send( "Again", [] );
		proxy.ApplyState( 4, new ChatSessionState { IsBusy = true, IsConnected = true, Status = "Thinking" } );
		CoreChecks.Check( PumpUntil( () => channel.Methods.Count( method => method == ChatSessionCall.Send ) == 2 ) && proxy.Status == "Thinking" && refused == 1,
			"The host's next state replaces a sent prompt's status." );

		proxy.Dispose();
		var check = proxy.CheckNewTaskAsync( "Next task", CancellationToken.None );
		proxy.ApplyState( 5, new ChatSessionState { Status = "After dispose" } );
		CoreChecks.Check( check.IsCompletedSuccessfully && !check.Result && proxy.Status == "Thinking" && proxy.Seq == 4,
			"A disposed proxy answers the task check as the same task without throwing, and takes no more pushes." );
		app.Shutdown();
	}

	/// <summary>Round-trips a value through the protocol's JSON, as it crosses between processes.</summary>
	private static T Wire<T>( T value ) =>
		JsonSerializer.Deserialize<T>( JsonSerializer.Serialize( value, StoreOp.JsonOptions ), StoreOp.JsonOptions )!;

	/// <summary>Carries a proxy's calls to a host's session on the UI thread, round-tripping arguments and answers through JSON.</summary>
	/// <param name="host">The host's session.</param>
	/// <param name="post">Posts to the UI thread.</param>
	private sealed class LoopbackChannel( IChatSession host, Func<Action, bool> post ) : IChatChannel
	{
		/// <summary>Gets the methods called, in order.</summary>
		internal List<string> Methods { get; } = [];

		/// <summary>Invokes a call on the host's session on the UI thread and answers its JSON result.</summary>
		public Task<JsonElement> CallAsync( string method, JsonElement[] args, CancellationToken cancellationToken )
		{
			var wired = Wire( args );
			var answer = new TaskCompletionSource<JsonElement>( TaskCreationOptions.RunContinuationsAsynchronously );
			if ( !post( () =>
			{
				Methods.Add( method );
				_ = UpdateCallAsync( method, wired, answer, cancellationToken );
			} ) )
				answer.SetException( new InvalidOperationException( "The host is gone." ) );
			return answer.Task;
		}

		/// <summary>Runs one call and completes its answer.</summary>
		private async Task UpdateCallAsync( string method, JsonElement[] args, TaskCompletionSource<JsonElement> answer, CancellationToken cancellationToken )
		{
			try
			{
				answer.SetResult( Wire( await ChatSessionCall.InvokeAsync( host, method, args, cancellationToken ) ) );
			}
			catch ( Exception error )
			{
				answer.SetException( error );
			}
		}
	}

	/// <summary>Answers a proxy's calls at once on the calling thread: JSON null, or an error while <see cref="Fail"/> is set.</summary>
	private sealed class ScriptedChannel : IChatChannel
	{
		/// <summary>Gets or sets whether calls fail, as when the host is unreachable.</summary>
		internal bool Fail { get; set; }

		/// <summary>Gets the methods called, in order.</summary>
		internal List<string> Methods { get; } = [];

		/// <summary>Records the call and answers it.</summary>
		public Task<JsonElement> CallAsync( string method, JsonElement[] args, CancellationToken cancellationToken )
		{
			lock ( Methods )
				Methods.Add( method );
			return Fail ? Task.FromException<JsonElement>( new IOException( "Not connected to the host." ) ) : Task.FromResult( JsonSerializer.SerializeToElement<object?>( null ) );
		}
	}
}
