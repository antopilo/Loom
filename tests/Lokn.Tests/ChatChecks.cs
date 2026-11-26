using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Markdown;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Tests;

/// <summary>Checks rich Markdown parsing and authoritative streaming snapshots without a model request.</summary>
internal static class ChatChecks
{
	/// <summary>Runs focused Markdown, message identity, profile, and transport failure checks.</summary>
	internal static void Run()
	{
		CoreChecks.Check( TerminalText.BuildPlain( "\u001b[32mBuild — café 👋\u001b[0m" ) == "Build — café 👋", "Tool output removes ANSI controls without changing Unicode text." );
		var blocks = MarkdownView.Parse( "## Plan\n\n- [x] Inspect\n- [ ] Build\n\n1. First\n2. Second\n\n```diff\n+added\n-removed\n```\n\n| A | B |\n| --- | --- |\n| One | Two |" );
		CoreChecks.Check( blocks[0] is { Kind: "heading", Text: "Plan", Marker: "##" }, "Markdown preserves heading levels." );
		CoreChecks.Check( blocks.Count( block => block.Kind == "list" ) == 4 && blocks[1].Marker == "☑" && blocks[2].Marker == "☐", "Markdown preserves checked and pending tasks and ordered steps." );
		CoreChecks.Check( blocks.Single( block => block.Kind == "code" ) is { Text: "+added\n-removed", Marker: "diff" }, "Fenced code and diff content survives parsing verbatim." );
		CoreChecks.Check( blocks.Any( block => block.Kind == "table" && block.Text.Contains( "One" ) ), "Markdown tables retain headers and cells." );
		CoreChecks.Check( MarkdownView.Parse( "```csharp\nConsole.WriteLine(\"Lokn\");" ).Single().Kind == "code", "Streaming incomplete fences remain readable code." );
		static string Visible( string text ) => string.Concat( MarkdownView.ParseInline( text ).Select( span => span.Text ) );
		CoreChecks.Check( Visible( "Open **`ChatContent.cs`** and [`Build`](https://x.dev)." ) == "Open ChatContent.cs and Build.", "Code inside bold and link labels shows no backticks." );
		CoreChecks.Check( Visible( "Use ``a`b`` and `*.cs` or `*.md`, then `__init__`." ) == "Use a`b and *.cs or *.md, then __init__.", "Double-backtick spans work, and code keeps its stars and underscores." );
		var diagram = MarkdownView.Parse( "Plan:\n\nstart ──► check\n   │\n   ▼ apply\n\nThen → done." );
		CoreChecks.Check( diagram[1] is { Kind: "code", Text: "start ──► check\n   │\n   ▼ apply" } && diagram[2] is { Kind: "paragraph" }, "An unfenced diagram keeps its lines and spacing, and prose with an arrow still wraps." );
		CoreChecks.Check( Visible( "Still streaming `Console.Write" ) == "Still streaming `Console.Write", "An unclosed backtick, such as one still streaming, stays literal." );
		var nested = MarkdownView.Parse( "Soft\nwrapped line\n\n- Parent\n  - Child\n    continued\n- Sibling" );
		CoreChecks.Check( nested[0] is { Kind: "paragraph", Text: "Soft wrapped line" }, "Soft line breaks join into one wrapping paragraph." );
		CoreChecks.Check( nested.Skip( 1 ).Select( block => block.Depth ).SequenceEqual( [0, 1, 0] ) && nested[2].Text == "Child continued", "Nested list items keep their depth and continuation lines." );
		var oversized = (Container)MarkdownView.BuildCode( new string( 'x', 80_000 ) + "\n" + string.Join( "\n", Enumerable.Repeat( "line", MarkdownView.MaxCodeLines + 100 ) ) );
		var texts = oversized.Children.OfType<Text>().ToArray();
		var codeLines = texts[0].Content.Split( '\n' );
		CoreChecks.Check( codeLines[0].Length < ViewStyle.MaxCodeLineLength + 64 && codeLines[0].Contains( "more characters not shown" ), "A code line too long to lay out is cut with a note." );
		CoreChecks.Check( codeLines.Length == MarkdownView.MaxCodeLines && texts.Length == 2 && texts[1].Content.Contains( "101 more lines not shown" ), "A code block with too many lines is cut with a note." );
		var failedAt = new DateTimeOffset( 2026, 11, 17, 15, 0, 0, TimeSpan.Zero );
		var failed = ChatContent.BuildStoredMessage( Guid.NewGuid(), Parse( """{"role":"assistant","content":[],"stopReason":"error","timestamp":1794927600000,"errorMessage":"429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"Rate limit reached.\"},\"request_id\":\"req_1\"} retry-after-ms=1800000"}""" ) );
		CoreChecks.Check( ChatContent.ReadFailure( failed ) == $"The model provider did not answer: Rate limit reached. Try again after {failed.CreatedAt.AddMinutes( 30 ).ToLocalTime():t}.", "A reply the provider failed says why and when to try again." );
		CoreChecks.Check( ChatContent.DescribeProviderError( "connection reset", failedAt ) == "The model provider did not answer: connection reset", "A provider error without a JSON body shows as it is." );
		CoreChecks.Check( ChatContent.ReadFailure( ChatContent.BuildStoredMessage( Guid.NewGuid(), Parse( """{"role":"assistant","content":[],"stopReason":"aborted","errorMessage":"Interrupted by user"}""" ) ) ) is null, "A reply the user stopped is not a provider failure." );
		var inline = MarkdownView.ParseInline( "Plain **bold** and *italic* with `code` and [link](https://example.com)." );
		CoreChecks.Check( string.Concat( inline.Select( span => span.Text ) ) == "Plain bold and italic with code and link.", "Inline rendering removes syntax without losing visible text." );
		CoreChecks.Check( ChatContent.ComposePrompt( "Review these ", [@"C:\My Files\shot.png", @"C:\src\a.cs"] ) == "Review these\n\n@\"C:\\My Files\\shot.png\" @\"C:\\src\\a.cs\"", "Attachments become quoted Omp file mentions, including paths with spaces." );
		CoreChecks.Check( ChatContent.ComposePrompt( " ", [@"C:\shot.png"] ) == "@\"C:\\shot.png\"" && ChatContent.ComposePrompt( "Hi", [] ) == "Hi", "An attachment-only prompt sends just its mentions, and plain prompts are unchanged." );
		CheckMarkdownSyntax( Visible );
		CheckDiffs();
		CheckRichDisplay();

		var app = new LoknApplication();
		var module = new OmpModule();
		CoreChecks.Check( app.Load( module ), "Omp settings register for the rich-chat fixture." );
		var store = new ConversationStore();
		var chat = store.CreateConversation( SettingsProfile.Personal );
		var context = new PanelContext( app, Guid.NewGuid(), CancellationToken.None, input: null );
		var panel = BuildChat( context, store, chat.Id, module.Service );
		panel.Session.UpdateFrame( Parse( """{"type":"message_start","messageId":"m1","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}]}}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"message_update","messageId":"m1","message":{"role":"assistant","content":[{"type":"text","text":"Hello Lokn"},{"type":"thinking","thinking":"Checking."}]}}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"message_end","messageId":"m1","message":{"role":"assistant","content":[{"type":"text","text":"Hello Lokn!"},{"type":"toolCall","id":"t1","name":"bash","arguments":{"command":"dotnet build"}}]}}""" ) );
		var messages = store.GetMessages( chat.Id );
		CoreChecks.Check( messages.Count == 1 && messages[0].Text == "Hello Lokn!", "Full streaming snapshots replace one stable message rather than duplicating deltas." );
		CoreChecks.Check( messages[0].RichContent!.Value.GetProperty( "content" )[1].GetProperty( "type" ).GetString() == "toolCall", "Structured tool calls persist with the message." );
		panel.Session.UpdateFrame( Parse( """{"type":"message_end","messageId":"m2","message":{"role":"user","content":"First line\nSecond line"}}""" ) );
		CoreChecks.Check( store.Get( chat.Id ).Title == "First line", "The first user message gives an untitled chat a concise title." );
		app.Settings.SetActiveProfile( SettingsProfile.Work );
		var restored = panel.CaptureState()!.Value.Deserialize<ConversationPanelInput>();
		CoreChecks.Check( restored.Profile == SettingsProfile.Personal && restored.ConversationId == chat.Id, "Restored conversation identity remains pinned after the titlebar profile changes." );
		panel.Session.UpdateFrame( Parse( """{"type":"notice","source":"omp-transport","level":"error","message":"Backend exited"}""" ) );
		CoreChecks.Check( store.GetMessages( chat.Id ).Count == 2, "Transport failures retain already recorded history." );
		CheckSlashCommands( panel );
		CheckCommandOutput( panel, store, chat.Id );
		CheckModes( panel );
		CheckActivity( app, module );
		CheckToolImages( app, module );
		CheckAttention( app, module );
		CheckFolder( app, module );
		CheckCompactionTitle( panel, store, chat.Id );
		CheckForm( panel );
		panel.Closed();
		app.Shutdown();
		CheckReloadContinue();
		CheckHeadlessTurn();
		Console.WriteLine( "PASS rich Markdown, code/diffs, checklists/tables, streaming identity, rich payload persistence, pinned chat profiles, chat attention, chat folders, continuing after a reload, and a turn with no panel open." );
	}

	/// <summary>Checks the Markdown agents write beyond the basics: math, colors, other emphasis marks, escapes, entities, inline HTML, autolinks,
	/// reference links, footnotes, underlined headings, indented code, long fences, code inside list items and HTML blocks.</summary>
	private static void CheckMarkdownSyntax( Func<string, string> visible )
	{
		CoreChecks.Check( visible( @"Area $\pi r^2$, ratio $\frac{a}{b}$, sum $\sum_{i=1}^{n} x_i \leq \infty$." ) == "Area πr², ratio a/b, sum ∑ᵢ₌₁ⁿxᵢ≤∞.",
			"Inline math turns Greek letters, scripts, fractions and operators into Unicode." );
		CoreChecks.Check( visible( "Costs $5 and $6 today." ) == "Costs $5 and $6 today.", "Dollar amounts are not math." );
		var colored = MarkdownView.ParseInline( @"A \textcolor{red}{warning} here" );
		CoreChecks.Check( string.Concat( colored.Select( span => span.Text ) ) == "A warning here" && colored.Any( span => span.Text == "warning" && span.Style is not null ),
			"A LaTeX color command outside math colors its text." );
		CoreChecks.Check( visible( "***both*** and _under_ but snake_case_name" ) == "both and under but snake_case_name", "Bold italic and underscore italic work; underscores inside words stay." );
		CoreChecks.Check( visible( @"\*not italic\* and \`tick\`" ) == "*not italic* and `tick`", "Backslash escapes show the character and drop the backslash." );
		CoreChecks.Check( visible( "a&nbsp;b &lt;tag&gt; &amp; &#169;" ) == "a\u00A0b <tag> & ©", "HTML entities decode, and a decoded tag stays text." );
		CoreChecks.Check( visible( "Press <kbd>Ctrl</kbd>+<b>S</b>, H<sub>2</sub>O, x<sup>2</sup>, List<T> and a<br>b" ) == "Press Ctrl+S, H₂O, x², List<T> and a\nb",
			"Inline HTML becomes code, bold, scripts and line breaks, and generics in prose stay text." );
		CoreChecks.Check( visible( "See <https://example.com> or <me@example.com>." ) == "See https://example.com or me@example.com.", "Autolinks drop their brackets." );
		var document = MarkdownView.Parse( "Title\n=====\n\nSub\n---\n\n    indented code\n    line two\n\n````md\n```\ninner\n```\n````\n\n- item\n  ```sh\n  echo hi\n  ```\n\n<details>\n<summary>More</summary>\n\nText[^1].\n\n</details>\n\n[^1]: The note.\n[docs]: https://docs.example\n\n$$\n\\frac{1}{2}\n$$" );
		CoreChecks.Check( document[0] is { Kind: "heading", Text: "Title", Marker: "#" } && document[1] is { Kind: "heading", Text: "Sub", Marker: "##" }, "Underlined headings read as levels one and two." );
		CoreChecks.Check( document[2] is { Kind: "code", Text: "indented code\nline two" }, "Code indented by four spaces is a code block." );
		CoreChecks.Check( document[3] is { Kind: "code", Text: "```\ninner\n```", Marker: "md" }, "A longer fence holds a shorter one." );
		CoreChecks.Check( document[4] is { Kind: "list" } && document[5] is { Kind: "code", Text: "echo hi", Marker: "sh", Depth: 1 }, "A fence under a list item stays in the item." );
		CoreChecks.Check( document[6] is { Kind: "paragraph", Text: "**More**" } && document[7] is { Kind: "paragraph", Text: "Text[^1]." }, "A details block shows its summary in bold and its content." );
		CoreChecks.Check( document.Any( block => block is { Kind: "footnote", Marker: "1", Text: "The note." } ) && document.Any( block => block is { Kind: "reference", Marker: "docs" } ),
			"Footnote and link reference definitions are read." );
		CoreChecks.Check( document[^1] is { Kind: "math", Text: "\\frac{1}{2}" }, "Display math is its own block." );
	}

	/// <summary>Builds a chat view over a session of its own, as a panel hosted outside the Conversations module does in focused checks.</summary>
	internal static ChatContent BuildChat( PanelContext context, ConversationStore store, Guid conversationId, OmpService backend ) =>
		new( context, store, new ChatSession( context.App, store, conversationId, backend, context.Post ) );

	/// <summary>Checks that the ask tool's HTML form becomes a pending question whose fields, defaults and checks follow Omp's own form reader, so
	/// the data Loom sends is data Omp accepts.</summary>
	private static void CheckForm( ChatContent panel )
	{
		const string html = """
			<form>
			  <p>Clean <b>the cache</b>?</p>
			  <fieldset><legend>Scope</legend>
			    <label><input type="radio" name="scope" value="all" checked> Everything</label>
			    <label><input type="radio" name="scope" value="temp"> Temporary files</label>
			  </fieldset>
			  <label><input type="checkbox" name="parts" value="a" checked> A</label>
			  <label><input type="checkbox" name="parts" value="b"> B</label>
			  <label><input type="checkbox" name="parts" value="other"> Other</label>
			  <label><input type="checkbox" name="dry"> Dry run</label>
			  <label>Level <select name="level"><option value="1">Low</option><option value="2" selected>High</option></select></label>
			  <label>Days <input type="number" name="days" value="3" min="1" max="30" required></label>
			  <label>Notes <textarea name="notes" placeholder="Why?"></textarea></label>
			  <input type="hidden" name="token" value="t1">
			  <script>alert(1)</script>
			</form>
			""";
		panel.Session.UpdateFrame( JsonSerializer.SerializeToElement( new { type = "extension_ui_request", id = "f1", method = "form", html } ) );
		CoreChecks.Check( panel.Session.PendingDialog is { } dialog && dialog.GetProperty( "method" ).GetString() == "form", "The ask tool's form waits for an answer." );
		CoreChecks.Check( panel.Session.ReadSelection( "f1.scope" ).SetEquals( ["0"] ) && panel.Session.ReadSelection( "f1.level" ).SetEquals( ["1"] ),
			"A form's checked radio and selected option start picked." );

		var form = HtmlForm.Parse( html );
		var fields = form.Fields.ToDictionary( field => field.Name );
		CoreChecks.Check( form.Sections[0].Blocks[0] is FormMarkdown { Markdown: "Clean **the cache**?" } && form.Sections.Any( section => section.Title == "Scope" ),
			"Form prose keeps its emphasis, and a fieldset's legend titles its section." );
		CoreChecks.Check( fields["scope"] is FormChoiceField { HasOther: true, ReturnsArray: false } && fields["parts"] is FormChoiceField { HasOther: false, ReturnsArray: true }
			&& fields["dry"] is FormChoiceField { ReturnsArray: false, HasOther: false } && fields["notes"] is FormTextField { IsMultiline: true, Placeholder: "Why?" },
			"Radio and checkbox groups offer an answer of one's own unless they have an Other option; a lone checkbox gives one value." );
		CoreChecks.Check( form.ComputeDefaults().ToJsonString() == """{"token":"t1","scope":"all","parts":["a"],"level":"2","days":3,"notes":""}""",
			"An untouched form sends its hidden values, checked options, and typed values, with numbers as numbers." );
		var scope = (FormChoiceField)fields["scope"];
		CoreChecks.Check( HtmlForm.ComputeChoiceValue( scope, new HashSet<int> { 0 }, "Only logs" )!.GetValue<string>() == "Only logs"
			&& HtmlForm.ComputeChoiceValue( (FormChoiceField)fields["dry"], new HashSet<int>(), null ) is null, "An answer of one's own replaces a single choice, and an unchecked lone checkbox is left out." );
		var days = (FormTextField)fields["days"];
		CoreChecks.Check( HtmlForm.Validate( days, HtmlForm.ComputeTextValue( days, "" ) ) == "Required" && HtmlForm.Validate( days, HtmlForm.ComputeTextValue( days, "40" ) ) == "Must be at most 30"
			&& HtmlForm.Validate( days, HtmlForm.ComputeTextValue( days, "7" ) ) is null, "Form checks use Omp's messages, so an answer Omp would refuse is never sent." );
		panel.Session.UpdateFrame( Parse( """{"type":"extension_ui_request","method":"cancel","targetId":"f1"}""" ) );
	}

	/// <summary>Checks how <c>/plan</c> and <c>/goal</c> are read, that a plan prompt shows only its task, and that a finished plan waits for Implement until the user writes again.</summary>
	private static void CheckModes( ChatContent panel )
	{
		CoreChecks.Check( ChatModes.ReadPlanTask( "/plan Add a theme " ) == "Add a theme" && ChatModes.ReadPlanTask( "/plan" ) == "" && ChatModes.ReadPlanTask( "/planner x" ) is null && ChatModes.ReadPlanTask( "plan x" ) is null,
			"Only /plan itself, alone or followed by a task, is the plan command." );
		CoreChecks.Check( ChatModes.ReadGoal( "/goal" ) == new GoalCommand( "get", "" ) && ChatModes.ReadGoal( "/goal show" ) == new GoalCommand( "get", "" )
			&& ChatModes.ReadGoal( "/goal Pause" ) == new GoalCommand( "pause", "" ) && ChatModes.ReadGoal( "/goal set Ship it" ) == new GoalCommand( "create", "Ship it" )
			&& ChatModes.ReadGoal( "/goal pause the build when red" ) == new GoalCommand( "create", "pause the build when red" ) && ChatModes.ReadGoal( "/goals" ) is null,
			"/goal reads or acts on the goal by its one-word actions, and any other text is a new objective." );
		var sent = ChatModes.ComposePlan( "Add a theme" );
		CoreChecks.Check( ChatModes.IsPlanRequest( sent ) && ChatModes.StripPlanRequest( sent ) == "Add a theme" && ChatModes.StripPlanRequest( sent + "\n\n@\"C:\\a.png\"" ) == "Add a theme\n\n@\"C:\\a.png\"",
			"A plan prompt's instructions come out again whole, keeping the task and the attachments after them." );
		CoreChecks.Check( ChatModes.SplitPlan( "Intro\n<lokn-plan>\n1. Step\n</lokn-plan>\nDone" ) is { Before: "Intro\n", Plan: "\n1. Step\n", After: "\nDone", IsClosed: true }
			&& ChatModes.SplitPlan( "Intro <lokn-plan>1. Ste" ) is { Plan: "1. Ste", IsClosed: false } && ChatModes.SplitPlan( "No plan" ) is null,
			"A reply splits at its plan, also while the plan still streams." );

		var pending = typeof( ChatContent ).GetMethod( "HasPendingPlan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!;
		var prompt = JsonSerializer.SerializeToElement( new { role = "user", content = new[] { new { type = "text", text = sent } } } );
		CoreChecks.Check( ChatContent.BuildStoredMessage( Guid.NewGuid(), prompt ).Text == "Add a theme", "A plan prompt is stored with its task as its text, which titles and copies use." );
		panel.Session.UpdateFrame( JsonSerializer.SerializeToElement( new { type = "message_end", messageId = "plan-user", message = prompt } ) );
		panel.Session.UpdateFrame( Parse( """{"type":"message_end","messageId":"plan-reply","message":{"role":"assistant","content":[{"type":"text","text":"Looked around.\n<lokn-plan>\n1. Add it\n</lokn-plan>"}]}}""" ) );
		CoreChecks.Check( pending.Invoke( panel, null ) is true, "A finished plan waits for Implement or Revise." );
		panel.Session.UpdateFrame( Parse( """{"type":"message_end","messageId":"plan-next","message":{"role":"user","content":"Something else"}}""" ) );
		CoreChecks.Check( pending.Invoke( panel, null ) is false, "Once the user writes again, the plan no longer waits." );
	}

	/// <summary>Checks that a chat session connects to a real (fixture) Omp child and runs a whole turn with no chat panel open.</summary>
	private static void CheckHeadlessTurn()
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
		CoreChecks.Check( app.Load( omp ), "Omp loads for the headless turn check." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		app.Settings.Set( OmpModule.Model, SettingsScope.Global, "fixture/one" );
		var store = new ConversationStore( new ProjectStore() );
		var chat = store.CreateConversation( SettingsProfile.Personal );
		store.SaveBackendSession( chat.Id, Path.Combine( Path.GetTempPath(), "lokn-headless-" + Guid.NewGuid().ToString( "N" ) ) );
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

		var session = new ChatSession( app, store, chat.Id, omp.Service, app.Post );
		CoreChecks.Check( session.PanelId is null, "A session created without a panel has no panel attached." );
		// A view closed before Omp admitted its prompt saved the prompt as the draft.
		store.SaveDraft( chat.Id, "Run headless" );
		session.Send( "Run headless", [] );
		CoreChecks.Check( session.IsBusy, "Sending makes the session work at once." );
		CoreChecks.Check( PumpUntil( () => session.PendingDialog is not null ), "With no panel open, the session connects, sends the prompt and receives the agent's question." );
		CoreChecks.Check( store.Get( chat.Id ).Draft is null, "A prompt Omp admitted with no view open no longer comes back as the chat's draft." );
		session.AnswerDialog( "approval", new { type = "extension_ui_response", id = "approval", confirmed = true } );
		CoreChecks.Check( PumpUntil( () => !session.IsBusy ), "Answering the question lets the turn settle without a panel." );
		var messages = store.GetMessages( chat.Id );
		CoreChecks.Check( messages.Any( message => message.Role == "user" && message.Text == "Run headless" )
			&& messages.Any( message => message.Role == "assistant" && message.Text.StartsWith( "Streaming complete.", StringComparison.Ordinal ) )
			&& !store.IsWorking( chat.Id ),
			"A turn run with no panel open records the user and assistant messages and leaves the chat idle." );
		session.Dispose();

		// The last panel leaves while an idle connection starts, and a prompt follows at once: the ended startup must not touch the new connection.
		var racingChat = store.CreateConversation( SettingsProfile.Personal );
		store.SaveBackendSession( racingChat.Id, Path.Combine( Path.GetTempPath(), "lokn-racing-" + Guid.NewGuid().ToString( "N" ) ) );
		var racing = new ChatSession( app, store, racingChat.Id, omp.Service, app.Post );
		var viewer = Guid.NewGuid();
		racing.Attach( viewer );
		racing.UpdateConnect();
		racing.Detach( viewer );
		CoreChecks.Check( !racing.IsConnectionStarted && racing.Status == "Ready", "Leaving a chat while its idle connection starts ends that connection." );
		racing.Send( "Run headless", [] );
		CoreChecks.Check( PumpUntil( () => racing.PendingDialog is not null ) && racing.IsConnected, "A prompt sent right after a connection ended connects afresh and reaches the agent." );
		racing.AnswerDialog( "approval", new { type = "extension_ui_response", id = "approval", confirmed = true } );
		CoreChecks.Check( PumpUntil( () => !racing.IsBusy ) && racing.Status == "Ready"
			&& store.GetMessages( racingChat.Id ).Any( message => message.Role == "assistant" && message.Text.StartsWith( "Streaming complete.", StringComparison.Ordinal ) ),
			"The ended connection's startup finishing late changes nothing: the new turn completes." );
		racing.Dispose();

		// /goal and /plan run through Omp's goal command and a plan prompt, which Omp's RPC mode does not run as slash commands.
		var modesChat = store.CreateConversation( SettingsProfile.Personal );
		store.SaveBackendSession( modesChat.Id, Path.Combine( Path.GetTempPath(), "lokn-modes-" + Guid.NewGuid().ToString( "N" ) ) );
		var modes = new ChatSession( app, store, modesChat.Id, omp.Service, app.Post );
		string Output() => store.GetMessages( modesChat.Id ).LastOrDefault( message => message.Role == ChatContent.CommandRole )?.Text ?? "";
		string? GoalField( string name ) => modes.Goal is { } goal ? ChatContent.ReadText( goal, name ) : null;
		modes.Send( "/goal Ship the fixture", [] );
		CoreChecks.Check( PumpUntil( () => Output().Length > 0 ) && GoalField( "status" ) == "active" && Output() == $"Goal (active): Ship the fixture\n{ChatModes.DescribeGoalTokens( modes.Goal!.Value )}"
			&& !modes.IsBusy && modes.Status == "Ready", "/goal with an objective creates the goal through Omp's goal command and shows it under the command." );
		modes.Send( "/goal pause", [] );
		CoreChecks.Check( PumpUntil( () => GoalField( "status" ) == "paused" ), "/goal pause pauses the goal." );
		modes.Send( "/goal Replace the fixture", [] );
		CoreChecks.Check( PumpUntil( () => GoalField( "objective" ) == "Replace the fixture" ) && GoalField( "status" ) == "active",
			"A new objective replaces the goal, which Omp creates only once the old one is dropped." );
		modes.Send( "/goal drop", [] );
		CoreChecks.Check( PumpUntil( () => Output() == "Goal dropped." ) && modes.Goal is null, "/goal drop removes the goal." );
		modes.Send( "/plan Add a theme", [] );
		CoreChecks.Check( PumpUntil( () => !modes.IsBusy && store.GetMessages( modesChat.Id ).Any( message => message.Role == "assistant" && message.Text.Contains( "<lokn-plan>", StringComparison.Ordinal ) ) )
			&& store.GetMessages( modesChat.Id ).Single( message => message.Role == "user" ) is { Text: "Add a theme" } sentPlan
			&& ChatModes.IsPlanRequest( ChatContent.ComputeMessageText( sentPlan.RichContent!.Value ) ),
			"/plan sends its task with the plan instructions, and the chat keeps the task as the prompt's text." );
		modes.Dispose();
		CheckDisconnectReset( app, store, omp.Service );
		app.Shutdown();
	}

	/// <summary>Checks that a session no panel shows any more forgets what its stopped process reported, so a reopened chat starts clean.</summary>
	private static void CheckDisconnectReset( LoknApplication app, ConversationStore store, OmpService backend )
	{
		var chat = store.CreateConversation( SettingsProfile.Personal );
		var session = new ChatSession( app, store, chat.Id, backend, app.Post );
		var first = Guid.NewGuid();
		session.Attach( first );
		session.UpdateFrame( Parse( """{"type":"tool_execution_start","toolCallId":"t1","toolName":"bash","intent":"Building"}""" ) );
		session.UpdateFrame( Parse( """{"type":"auto_compaction_start"}""" ) );
		session.UpdateFrame( Parse( """{"type":"extension_ui_request","id":"q1","method":"confirm","title":"Proceed?"}""" ) );
		var changes = 0;
		session.DialogsChanged += () => changes++;
		var second = Guid.NewGuid();
		session.Attach( second );
		CoreChecks.Check( changes == 1, "A panel attaching to a session with a pending question tells the feed, which answers it in place from then on." );
		session.Detach( second );
		session.Detach( first );
		CoreChecks.Check( session.Status == "Ready" && session.PendingDialog is null && session.Tools.Count == 0 && session.Intents.Count == 0 && !session.IsAutoCompacting
			&& session.QueuedSteering.Length == 0 && changes >= 3,
			"Once no panel shows an idle session, its process stops and its status, question, live tools and compaction are forgotten." );
		session.Dispose();
	}

	/// <summary>Checks bounded title material, generated title cleanup, and preservation of a name the user chose.</summary>
	private static void CheckCompactionTitle( ChatContent panel, ConversationStore store, Guid chatId )
	{
		CoreChecks.Check( ConversationStore.ComputeGeneratedTitle( "\n**Title:** \"Workspace sessions per chat\".\nMore text" ) == "Workspace sessions per chat",
			"A title answer keeps its first line, without Markdown, a Title label, quotes or a trailing period." );
		CoreChecks.Check( ConversationStore.ComputeGeneratedTitle( "💬 FEED: Redesign notifications as chat feed with GitHub updates" ) == "Redesign notifications as chat",
			"A generated title drops a leading icon and category code, and keeps at most four words." );

		var longPrompt = new string( 'x', 1999 ) + "👋" + new string( 'y', 100 );
		var material = ChatContent.BuildTitleMaterial(
		[
			new( Guid.NewGuid(), "user", longPrompt, DateTimeOffset.UtcNow ),
			new( Guid.NewGuid(), "assistant", "Ignored", DateTimeOffset.UtcNow ),
			new( Guid.NewGuid(), "user", "Latest request", DateTimeOffset.UtcNow ),
		] );
		CoreChecks.Check( material.StartsWith( "First prompt:\n" + new string( 'x', 1999 ), StringComparison.Ordinal )
			&& !material.Contains( "👋", StringComparison.Ordinal ) && material.EndsWith( "Latest prompt:\nLatest request", StringComparison.Ordinal ),
			"Automatic titles receive only bounded first and latest user prompts without splitting a surrogate pair." );

		var taskMaterial = ChatContent.BuildTaskCheckMaterial(
		[
			new( Guid.NewGuid(), "user", "Fix the login bug", DateTimeOffset.UtcNow ),
			new( Guid.NewGuid(), "user", "Also cover the logout path", DateTimeOffset.UtcNow ),
		], longPrompt );
		CoreChecks.Check( taskMaterial.Contains( "Latest prompt:\nAlso cover the logout path", StringComparison.Ordinal )
			&& taskMaterial.EndsWith( "New prompt:\n" + new string( 'x', 1999 ), StringComparison.Ordinal ),
			"The new-task check reads the first and latest prompts, then the bounded new prompt." );
		CoreChecks.Check( ChatContent.ReadNewTaskAnswer( "**NEW**" ) && ChatContent.ReadNewTaskAnswer( " new." )
			&& !ChatContent.ReadNewTaskAnswer( "SAME" ) && !ChatContent.ReadNewTaskAnswer( "" ) && !ChatContent.ReadNewTaskAnswer( "Not sure, maybe NEW" ),
			"Only an answer that starts with NEW suggests a new chat; anything unclear sends the prompt here." );

		var apply = typeof( ChatSession ).GetMethod( "UpdateTitleFromAnswer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!;
		apply.Invoke( panel.Session, ["Compacted chat topic"] );
		CoreChecks.Check( store.Get( chatId ).Title == "Compacted chat topic" && store.Get( chatId ).IsTitleGenerated, "A generated answer renames the chat." );

		store.UpdateConversation( chatId, "My own name", store.Get( chatId ).ProjectId );
		apply.Invoke( panel.Session, ["Another generated title"] );
		CoreChecks.Check( store.Get( chatId ).Title == "My own name", "A name the user chose survives later generated titles." );
	}

	/// <summary>Checks that a module reload during a turn reopens the chat and asks a real (fixture) Omp child to continue, and that other captures do not.</summary>
	private static void CheckReloadContinue()
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
		CoreChecks.Check( app.Load( omp ), "Omp loads for the reload check." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		app.Settings.Set( OmpModule.Model, SettingsScope.Global, "fixture/one" );
		app.Settings.Set( OmpModule.Economy, SettingsScope.Personal, true );
		var store = new ConversationStore( new ProjectStore() );
		var chat = store.CreateConversation( SettingsProfile.Personal );
		store.SaveBackendSession( chat.Id, Path.Combine( Path.GetTempPath(), "lokn-continue-" + Guid.NewGuid().ToString( "N" ) ) );
		ConversationsModule Build() => new( store, ConversationsModule.BuildChat );
		CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( Build() ), "Conversations loads for the reload check." );
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

		var panelId = app.Panels.Open( ConversationsModule.ChatPanelType, input: new ConversationPanelInput( chat.Id, chat.Profile ) );
		var panel = ((ConversationPanel)app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!).View!;
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		panel.Session.UpdateConnect();
		var modelField = typeof( ChatSession ).GetField( "_connectedModel", flags )!;
		CoreChecks.Check( PumpUntil( () => modelField.GetValue( panel.Session ) is "fixture/one" ), "The first chat connects to its Omp child before the reload." );
		CoreChecks.Check( typeof( ChatSession ).GetField( "_effort", flags )!.GetValue( panel.Session ) is "low", "Economy mode starts a new chat at low reasoning effort." );

		// The meter reads the context's use from get_state and the compaction point from the profile's settings.
		var meter = typeof( ChatContent ).GetMethod( "BuildContextMeter", flags )!;
		CoreChecks.Check( PumpUntil( () => typeof( ChatSession ).GetField( "_compaction", flags )!.GetValue( panel.Session ) is not null )
			&& typeof( ChatSession ).GetField( "_contextTokens", flags )!.GetValue( panel.Session ) is 84000L && typeof( ChatSession ).GetField( "_contextWindow", flags )!.GetValue( panel.Session ) is 200000L
			&& meter.Invoke( panel, null ) is not null,
			"A connected chat shows its context use against the model's window." );
		var settings = (OmpCompaction)typeof( ChatSession ).GetField( "_compaction", flags )!.GetValue( panel.Session )!;
		CoreChecks.Check( settings.ComputeThreshold( 200_000, "fixture/one" ) == 150_000 && settings.ComputeThreshold( 200_000, "fixture/two" ) == 80_000
			&& settings.ComputeThreshold( 200_000, "other/model" ) == 160_000,
			"The compaction mark follows the profile's threshold and its per-model fixed triggers and window caps." );
		var defaults = new OmpCompaction( true, -1, -1, null, new Dictionary<string, string>() );
		CoreChecks.Check( defaults.ComputeThreshold( 200_000, "a/b" ) == 170_000 && defaults.ComputeThreshold( 500_000, "a/b" ) == 425_000
			&& defaults with { ReserveTokens = 50_000 } is var reserved && reserved.ComputeThreshold( 200_000, "a/b" ) == 150_000
			&& defaults with { IsEnabled = false } is var off && off.ComputeThreshold( 200_000, "a/b" ) is null,
			"Omp's default compaction point leaves the larger of 15% and the reply reserve, and none when compaction is off." );
		CoreChecks.Check( ContextMeter.FormatTokens( 500_000 ) == "500K" && ContextMeter.FormatTokens( 1_200_000 ) == "1.2M" && ContextMeter.FormatTokens( 84_300 ) == "84.3K",
			"Token counts read like Omp's: 500K, 1.2M." );
		var costDetails = ContextMeter.Describe( new( 150_000, 200_000, 170_000, false, "Fixture One", "low", 3, 15, 1, 2, true ) );
		CoreChecks.Check( costDetails.Contains( new( "List price", "Input", "$3 / M" ) )
			&& costDetails.Contains( new( "Session", "Auxiliary calls (titles, task checks)", "2" ) )
			&& costDetails.Any( row => row.Label.Length == 0 && row.Value.Contains( "Start a new chat", StringComparison.Ordinal ) ),
			"The token and cost panel reports model rates, auxiliary calls, and economy guidance." );
		panel.Session.UpdateFrame( Parse( """{"type":"agent_start"}""" ) );
		CoreChecks.Check( !panel.CaptureState()!.Value.Deserialize<ConversationPanelInput>().ContinueTurn, "Saving the layout outside a reload never asks a working chat to continue." );

		CoreChecks.Check( app.Reload( Build() ), "Conversations reloads while its chat is working." );
		CoreChecks.Check( PumpUntil( () => store.GetMessages( chat.Id ).Any( message => message.Role == "user" && message.Text == ChatContent.ContinuePrompt ) ),
			"The reopened chat waits for the old child to exit, reconnects, and asks the agent to continue the turn the reload stopped." );

		// The model picker's main action asks the connected child for the model and shows it once Omp accepts.
		var reopened = app.Panels.Sessions.Select( session => session.Content ).OfType<ConversationPanel>().Single().View!;
		typeof( ChatContent ).GetMethod( "UpdateModel", flags )!.Invoke( reopened, [new OmpModelInfo( "fixture", "two", "Fixture Two" )] );
		CoreChecks.Check( PumpUntil( () => modelField.GetValue( reopened.Session ) is "fixture/two" ), "Choosing a model for a chat switches its session and shows the new model." );

		// A panel whose agent is working keeps running hidden; another conversation opens in a new panel in its pane.
		var conversations = app.GetModule<ConversationsModule>();
		var working = app.Panels.ActivePanelId!.Value;
		reopened.Session.UpdateFrame( Parse( """{"type":"agent_start"}""" ) );
		var other = store.CreateConversation( SettingsProfile.Personal );
		var otherPanel = conversations.OpenConversation( app, ChatRef.Local( other.Id ) );
		CoreChecks.Check( otherPanel != working && app.Panels.Panes.Count == 1 && !app.Panels.IsShown( working ) && app.Panels.Panels.Any( panel => panel.Id == working ),
			"Opening a conversation while the shown chat works opens it in a new panel in the same pane and keeps the working one, hidden." );
		CoreChecks.Check( conversations.OpenConversation( app, ChatRef.Local( chat.Id ) ) == working && app.Panels.IsShown( working ) && app.Panels.Panes.Count == 1,
			"Opening the working conversation again brings its panel back into the pane." );

		// Clearing is refused mid-turn, then empties the chat in place once the turn ends, from the palette or /clear.
		var statusField = typeof( ChatSession ).GetField( "_status", flags )!;
		reopened.Session.UpdateFrame( Parse( """{"type":"agent_start"}""" ) );
		CoreChecks.Check( !app.Commands.CanExecute( "conversations.clear" ), "A working chat cannot be cleared from the palette." );
		reopened.Session.UpdateFrame( Parse( """{"type":"agent_end"}""" ) );
		CoreChecks.Check( store.GetMessages( chat.Id ).Count > 0 && app.Commands.CanExecute( "conversations.clear" ), "An idle chat with history can be cleared." );
		_ = app.Commands.ExecuteAsync( "conversations.clear" );
		CoreChecks.Check( PumpUntil( () => statusField.GetValue( reopened.Session ) is "Cleared" ) && store.GetMessages( chat.Id ).Count == 0, "Clearing starts a new Omp session and empties the transcript." );
		CoreChecks.Check( app.Panels.Sessions.Any( session => session.Content is ConversationPanel { View: var shown } && shown == reopened ) && store.Get( chat.Id ).Id == chat.Id, "The chat panel and its conversation stay." );
		CoreChecks.Check( PumpUntil( () => store.Get( chat.Id ).Title == "Fixture small title" ) && store.Get( chat.Id ).IsTitleGenerated,
			"After its first turn, the chat asks Omp's small-model role for a title from bounded prompts and applies it." );
		// Every few prompts the chat asks again what it works on now, unless renaming is off.
		var calls = typeof( ChatSession ).GetField( "_auxiliaryCallCount", flags )!;
		int Calls() => (int)calls.GetValue( reopened.Session )!;
		void Prompt( string text )
		{
			store.AppendMessage( chat.Id, new ConversationMessage( Guid.NewGuid(), "user", text, DateTimeOffset.UtcNow ) );
			reopened.Session.UpdateFrame( Parse( """{"type":"agent_end"}""" ) );
		}
		var before = Calls();
		Prompt( "Now make the export async" );
		Prompt( "Add a progress bar to it" );
		CoreChecks.Check( Calls() == before, "A chat with a generated title is not named again before a few new prompts." );
		Prompt( "And cancel it on close" );
		CoreChecks.Check( PumpUntil( () => Calls() == before + 1 ) && store.Get( chat.Id ).IsTitleGenerated,
			"After a few new prompts, the chat asks the small model again for a title for what it works on now." );
		app.Settings.Set( ChatContent.AutoRename, SettingsScope.Global, false );
		Prompt( "One" );
		Prompt( "Two" );
		Prompt( "Three" );
		CoreChecks.Check( Calls() == before + 1, "With Rename chats as their task changes off, the chat keeps its title." );
		app.Settings.Set( ChatContent.AutoRename, SettingsScope.Global, true );
		var sentField = typeof( ChatSession ).GetField( "_sentCommand", flags )!;
		sentField.SetValue( reopened.Session, "/rename 🪟 PANEL: Mine" );
		reopened.Session.UpdateFrame( Parse( """{"type":"session_info_update","title":"🪟 PANEL: Mine","sessionId":"fixture"}""" ) );
		CoreChecks.Check( store.Get( chat.Id ) is { Title: "🪟 PANEL: Mine", IsTitleChosen: true }, "A title the user gives with /rename <title> counts as chosen." );
		sentField.SetValue( reopened.Session, "/model fixture/one" );
		reopened.Session.UpdateFrame( Parse( """{"type":"session_info_update","title":"Stale name","sessionId":"fixture"}""" ) );
		reopened.Session.UpdateFrame( Parse( """{"type":"auto_compaction_end","result":{}}""" ) );
		CoreChecks.Check( store.Get( chat.Id ).Title == "🪟 PANEL: Mine" && !store.SetAutomaticTitle( chat.Id, "Compacted summary", isGenerated: true ),
			"Neither the name Omp echoes after another command nor a compaction's summary replaces a title the user chose." );
		CoreChecks.Check( ChatContent.ComputeCommandMatches( [ChatContent.ClearCommand], "cl" ).Single().Name == "clear", "/clear is offered among the slash commands." );

		// While the agent works, Enter queues for after the turn and Ctrl+Enter steers; the switch beside Send swaps them. Each message
		// shows in the prompt box at once, Omp's queue_update keeps the list, and a queued message can be steered now or taken back.
		var composer = (TextEditorController)typeof( ChatContent ).GetField( "_composer", flags )!.GetValue( reopened )!;
		var steeringField = typeof( ChatSession ).GetField( "_queuedSteering", flags )!;
		var followUpsField = typeof( ChatSession ).GetField( "_queuedFollowUps", flags )!;
		string[] Steering() => (string[])steeringField.GetValue( reopened.Session )!;
		string[] FollowUps() => (string[])followUpsField.GetValue( reopened.Session )!;
		void Type( string text ) => composer.Document.Apply( new TextChange( new TextRange( 0, composer.Document.Length ), text ) );
		reopened.Session.UpdateFrame( Parse( """{"type":"agent_start"}""" ) );
		Type( "Run the tests after this" );
		reopened.UpdateSend();
		CoreChecks.Check( FollowUps() is ["Run the tests after this"] && composer.Document.Length == 0
			&& typeof( ChatContent ).GetMethod( "BuildQueue", flags )!.Invoke( reopened, null ) is not null,
			"A message sent while the agent works leaves the prompt and shows in the prompt box as queued at once." );
		Type( "Use the new API" );
		reopened.UpdateSteer();
		CoreChecks.Check( PumpUntil( () => Steering() is ["Use the new API"] && FollowUps() is ["Run the tests after this"] ),
			"Ctrl+Enter steers the running turn instead, and Omp's queue report keeps both." );
		typeof( ChatContent ).GetField( "_isSteerMode", flags )!.SetValue( reopened, true );
		Type( "Also check Linux" );
		reopened.UpdateSend();
		CoreChecks.Check( PumpUntil( () => Steering() is [_, "Also check Linux"] ), "With the switch on Steer, Enter steers." );
		reopened.Session.UpdatePromote( "Run the tests after this" );
		CoreChecks.Check( PumpUntil( () => FollowUps() is [] && Steering() is [_, _, "Run the tests after this"] ),
			"Steer now moves a queued message to the steering queue." );
		reopened.Session.UpdateUnqueue( "Use the new API", true );
		CoreChecks.Check( PumpUntil( () => Steering() is { Length: 2 } && composer.Document.GetText() == "Use the new API" ),
			"Taking a message back removes it from Omp's queue and returns it to the empty prompt." );
		reopened.Session.UpdateFrame( Parse( """{"type":"agent_end"}""" ) );
		CoreChecks.Check( Steering() is [] && typeof( ChatContent ).GetMethod( "BuildQueue", flags )!.Invoke( reopened, null ) is null,
			"The waiting list empties when the turn ends." );
		Type( "" );
		typeof( ChatContent ).GetField( "_isSteerMode", flags )!.SetValue( reopened, false );

		// A question the agent waits on is offered to the notification feed and to its notification's banner, which answer it in place, and leaves once answered.
		reopened.Session.UpdateFrame( Parse( """{"type":"extension_ui_request","id":"pick","method":"select","title":"Which branch?","options":["main","dev"]}""" ) );
		CoreChecks.Check( NotificationPromptSource.ListAll( app ) is [{ PanelId: var waiting } prompt] && waiting == working
			&& prompt.Build( "feed" ) is not null && prompt.Build( "banner" ) is not null,
			"A chat waiting on a question offers it through the notification prompts, for the feed and its panel's banner." );
		reopened.Session.UpdateFrame( Parse( """{"type":"extension_ui_request","id":"cancel-pick","method":"cancel","targetId":"pick"}""" ) );
		CoreChecks.Check( NotificationPromptSource.ListAll( app ).Count == 0,
			"A question the agent withdraws, or the user answers, leaves the feed and the banner." );
		app.Shutdown();
	}

	/// <summary>Checks that a chat outside projects moves to a chosen folder only while its agent is idle.</summary>
	private static void CheckFolder( LoknApplication app, OmpModule module )
	{
		var store = new ConversationStore();
		var chat = store.CreateConversation( SettingsProfile.Personal );
		var panel = BuildChat( new PanelContext( app, Guid.NewGuid(), CancellationToken.None, input: null ), store, chat.Id, module.Service );
		var folder = Path.Combine( Path.GetTempPath(), "lokn-chat-folder" );
		panel.Session.UpdateFrame( Parse( """{"type":"agent_start"}""" ) );
		CoreChecks.Check( !panel.UpdateDirectory( folder ) && store.Get( chat.Id ).Directory is null, "A working chat keeps its folder." );
		panel.Session.UpdateFrame( Parse( """{"type":"agent_end"}""" ) );
		CoreChecks.Check( panel.UpdateDirectory( folder ) && store.GetDirectory( chat.Id ) == folder, "An idle chat outside projects moves to the chosen folder." );
		panel.Closed();
	}

	/// <summary>Checks that a chat asks for the user when a question arrives or its turn ends, and withdraws once the agent works on.</summary>
	private static void CheckAttention( LoknApplication app, OmpModule module )
	{
		var store = new ConversationStore();
		var chat = store.CreateConversation( SettingsProfile.Personal );
		store.UpdateConversation( chat.Id, "Fix the build", null );
		CoreChecks.Check( app.Load( new FixtureModule( "attention", builder => builder.Panel( "chat", "Chat",
			context => BuildChat( context, store, chat.Id, module.Service ) ) ) ), "A workspace-hosted chat registers for attention checks." );
		var panelId = app.Panels.Open( "attention.chat" );
		app.Panels.SplitActive( SplitAxis.Horizontal );
		var panel = (ChatContent)app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!;
		panel.Session.UpdateFrame( Parse( """{"type":"agent_start"}""" ) );
		CoreChecks.Check( app.Panels.GetAttention( panelId ) is null, "A working chat does not ask for the user." );
		panel.Session.UpdateFrame( Parse( """{"type":"extension_ui_request","method":"select","id":"d1","title":"Pick a branch","options":["main"]}""" ) );
		CoreChecks.Check( app.Panels.GetAttention( panelId ) is { Title: "Fix the build", Message: "Pick a branch", Tone: NotificationTone.Attention },
			"A tool question asks for the user with its title, marked as waiting." );
		app.Panels.Activate( panelId );
		panel.Session.UpdateFrame( Parse( """{"type":"agent_end"}""" ) );
		CoreChecks.Check( app.Panels.GetAttention( panelId )?.Message == "Finished and waiting for you", "A finished turn asks for the user again." );
		panel.Session.UpdateFrame( Parse( """{"type":"extension_ui_request","method":"cancel","targetId":"d1"}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"agent_start"}""" ) );
		CoreChecks.Check( app.Panels.GetAttention( panelId ) is null, "A chat working on alone withdraws its request." );

		CoreChecks.Check( panel.ComputeWorkingLabel() == "Working", "A turn with no assistant output yet is working." );
		panel.Session.UpdateFrame( Parse( """{"type":"message_update","messageId":"w1","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Hmm"}]}}""" ) );
		CoreChecks.Check( panel.ComputeWorkingLabel() == "Thinking", "Streaming reasoning shows as thinking." );
		panel.Session.UpdateFrame( Parse( """{"type":"message_update","messageId":"w1","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Hmm"},{"type":"toolCall","id":"w-t1","name":"read","arguments":{"path":"a.cs"}},{"type":"toolCall","id":"w-t2","name":"bash","arguments":{"command":"dotnet build"}}]}}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"tool_execution_start","toolCallId":"w-t1","toolName":"read","args":{},"intent":"Reading the build script"}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"tool_execution_update","toolCallId":"w-t1","toolName":"read","args":{},"partialResult":{}}""" ) );
		CoreChecks.Check( panel.ComputeWorkingLabel() == "Reading the build script", "A running tool shows the intent it started with, after progress frames." );
		panel.Session.UpdateFrame( Parse( """{"type":"tool_execution_end","toolCallId":"w-t1","toolName":"read","result":{"content":[]}}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"tool_execution_start","toolCallId":"w-t2","toolName":"bash","args":{}}""" ) );
		CoreChecks.Check( panel.ComputeWorkingLabel() == "Running bash", "A running tool without an intent names itself; finished tools no longer show." );
		panel.Session.UpdateFrame( Parse( """{"type":"tool_execution_end","toolCallId":"w-t2","toolName":"bash","result":{"content":[]}}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"message_update","messageId":"w2","message":{"role":"assistant","content":[{"type":"text","text":"Done"}]}}""" ) );
		CoreChecks.Check( panel.ComputeWorkingLabel() == "Writing", "Streaming prose shows as writing." );
		CoreChecks.Check( WorkingIndicator.FormatElapsed( TimeSpan.FromSeconds( 9.7 ) ) == "9s" && WorkingIndicator.FormatElapsed( TimeSpan.FromSeconds( 125 ) ) == "2m 05s"
			&& WorkingIndicator.FormatElapsed( TimeSpan.FromSeconds( 4580 ) ) == "1h 16m 20s", "Elapsed time reads in seconds, then minutes, then hours." );
		app.Unload( "attention" );
	}

	/// <summary>Checks that reasoning and tool calls across messages fold into one summary row, which the agent's prose ends.</summary>
	private static void CheckActivity( LoknApplication app, OmpModule module )
	{
		var store = new ConversationStore();
		var chat = store.CreateConversation( SettingsProfile.Personal );
		void Add( string role, string json ) => store.AppendMessage( chat.Id, new( Guid.NewGuid(), role, "", DateTimeOffset.UtcNow, Parse( json ) ), persist: false );
		Add( "user", """{"content":[{"type":"text","text":"Fix it"}]}""" );
		Add( "assistant", """{"content":[{"type":"thinking","thinking":"Look first."},{"type":"toolCall","id":"r1","name":"read","arguments":{"path":"A.cs"}}]}""" );
		Add( "toolResult", """{"toolCallId":"r1","toolName":"read","content":[{"type":"text","text":"code"}]}""" );
		Add( "assistant", """{"content":[{"type":"text","text":" "},{"type":"toolCall","id":"b1","name":"bash","arguments":{"command":"build"}},{"type":"toolCall","id":"b2","name":"bash","arguments":{"command":"test"}}]}""" );
		Add( "toolResult", """{"toolCallId":"b1","toolName":"bash","content":[{"type":"text","text":"ok"}]}""" );
		Add( "toolResult", """{"toolCallId":"b2","toolName":"bash","isError":true,"content":[{"type":"text","text":"failed"}]}""" );
		Add( "assistant", """{"content":[{"type":"text","text":"Found it."},{"type":"toolCall","id":"r2","name":"read","arguments":{"path":"B.cs"}}]}""" );
		Add( "assistant", """{"content":[{"type":"toolCall","id":"t1","name":"todo","arguments":{"op":"done","task":["Inspect"]}},{"type":"toolCall","id":"r3","name":"read","arguments":{"path":"C.cs"}}]}""" );
		var panel = BuildChat( new PanelContext( app, Guid.NewGuid(), CancellationToken.None, input: null ), store, chat.Id, module.Service );
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		var rows = (List<TranscriptRow>)typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [store.GetMessages( chat.Id )] )!;
		CoreChecks.Check( rows.Select( row => row.IsActivity ).SequenceEqual( [false, true, false, true, false, true] ) && rows[1].Parts.Length == 2 && rows[3].Parts.Length == 1,
			"Reasoning and tool calls across messages fold into one activity row; the agent's prose and a todo list each start a new row." );
		var tools = rows[1].Parts.SelectMany( part => part.Message.RichContent!.Value.GetProperty( "content" ).EnumerateArray().Skip( part.Start ) )
			.Where( block => block.GetProperty( "type" ).GetString() == "toolCall" ).ToArray();
		var summary = (string)typeof( ChatContent ).GetMethod( "ComputeActivitySummary", flags )!.Invoke( panel, [tools] )!;
		CoreChecks.Check( summary == "Read 1 file, ran 2 commands", $"An activity row summarizes its tools by kind ({summary})." );

		// The pinned list is the newest one with work left; a call that only marks a task while running defers to the list before it.
		CoreChecks.Check( panel.ComputeCurrentTodo( store.GetMessages( chat.Id ) ).Count == 0, "A todo call without a list or result pins nothing." );
		Add( "assistant", """{"content":[{"type":"toolCall","id":"t2","name":"todo","arguments":{"op":"init","list":[{"phase":"Fix","items":["Inspect","Patch"]}]}}]}""" );
		Add( "toolResult", """{"toolCallId":"t2","toolName":"todo","details":{"phases":[{"name":"Fix","tasks":[{"content":"Inspect","status":"in_progress"},{"content":"Patch","status":"pending"}]}]},"content":[]}""" );
		Add( "assistant", """{"content":[{"type":"toolCall","id":"t3","name":"todo","arguments":{"op":"done","task":["Inspect"]}}]}""" );
		var messages = store.GetMessages( chat.Id );
		typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [messages] );
		CoreChecks.Check( panel.ComputeCurrentTodo( messages ) is [{ Name: "Fix", Tasks: [{ State: TaskState.Active }, { State: TaskState.Pending }] }], "The newest list with work left is pinned, even while a later update is still running." );
		Add( "toolResult", """{"toolCallId":"t3","toolName":"todo","details":{"phases":[{"name":"Fix","tasks":[{"content":"Inspect","status":"completed"},{"content":"Patch","status":"completed"}]}]},"content":[]}""" );
		messages = store.GetMessages( chat.Id );
		typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [messages] );
		CoreChecks.Check( panel.ComputeCurrentTodo( messages ).Count == 0, "A finished list unpins." );

		// Omp's notices to the model stay out of the transcript; a custom message it displays shows.
		var before = ((List<TranscriptRow>)typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [messages] )!).Count;
		Add( "custom", """{"role":"custom","customType":"xdev-mount-notice","content":"<system-notice>\nxd:// device inventory changed.\n</system-notice>","display":false}""" );
		Add( "custom", """{"role":"custom","customType":"eval-state-context","content":"<system-notice>\nstate\n</system-notice>"}""" );
		Add( "custom", """{"role":"custom","customType":"advisor","content":"Consider the cache.","display":true}""" );
		rows = (List<TranscriptRow>)typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [store.GetMessages( chat.Id )] )!;
		CoreChecks.Check( rows.Count == before + 1 && rows[^1].Parts[0].Message.RichContent!.Value.GetProperty( "customType" ).GetString() == "advisor",
			"Custom messages Omp does not display stay hidden; displayed ones show." );

		// Displayed notices show labels from their details and the text a person needs, never Omp's tags or its directions to the model.
		var agentJob = ChatContent.ComputeNotice( Parse( """{"customType":"async-result","content":"<system-notice>\nBackground job Hunks has completed.\nResume your work using the result below.\n<task-result id=\"Hunks\" status=\"completed\">\n\n<output>\n**Done.**\n</output>\n</task-result>\n</system-notice>","details":{"jobs":[{"label":"Hunks","type":"task","status":"completed","durationMs":369570}]},"display":true}""" ) );
		var bashJob = ChatContent.ComputeNotice( Parse( """{"customType":"async-result","content":"<system-notice>\nBackground job bg_1 (make) has completed.\nResume your work using the result below.\nBUILD OK\n</system-notice>","details":{"jobs":[{"label":"make","type":"bash","status":"failed","durationMs":7000}]},"display":true}""" ) );
		var irc = ChatContent.ComputeNotice( Parse( """{"customType":"irc:incoming","content":"<irc>\nIncoming IRC message\n\nReply via write\n</irc>","details":{"from":"Labels","message":"Labels: which Reveal?"},"display":true}""" ) );
		var other = ChatContent.ComputeNotice( Parse( """{"customType":"advisor","content":"<system-notice>\nConsider the cache.\n</system-notice>","display":true}""" ) );
		CoreChecks.Check( agentJob is { Title: "Background job completed · 6m 09s · Hunks", Body: "**Done.**", IsPlain: false }
			&& bashJob is { Title: "Background job failed · 7s · make", Body: "BUILD OK", IsPlain: true }
			&& irc is { Title: "Message from Labels", Body: "which Reveal?" }
			&& other is { Title: "advisor", Body: "Consider the cache.", IsKnown: false },
			"Displayed notices show their job, sender or type with the text inside Omp's tags, without the tags or directions to the model." );

		// A finished turn that changed files ends with one card summing them per file; failed edits and turns without edits add none.
		Add( "user", """{"content":[{"type":"text","text":"Fix the build"}]}""" );
		Add( "assistant", """{"content":[{"type":"toolCall","id":"e1","name":"edit","arguments":{}},{"type":"toolCall","id":"w1","name":"write","arguments":{"path":"B.cs","content":"one\ntwo\n"}},{"type":"toolCall","id":"e2","name":"edit","arguments":{}},{"type":"toolCall","id":"e3","name":"edit","arguments":{}}]}""" );
		Add( "toolResult", """{"toolCallId":"e1","toolName":"edit","content":[],"details":{"path":"A.cs","diff":" 1|a\n-2|old\n+2|new"}}""" );
		Add( "toolResult", """{"toolCallId":"w1","toolName":"write","content":[]}""" );
		Add( "toolResult", """{"toolCallId":"e2","toolName":"edit","content":[],"details":{"path":"a.cs","diff":"+5|x"}}""" );
		Add( "toolResult", """{"toolCallId":"e3","toolName":"edit","isError":true,"content":[],"details":{"path":"C.cs","diff":"+1|y"}}""" );
		Add( "assistant", """{"content":[{"type":"text","text":"Done."}]}""" );
		rows = (List<TranscriptRow>)typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [store.GetMessages( chat.Id )] )!;
		var changes = rows.Where( row => row.Changes is not null ).ToArray();
		CoreChecks.Check( changes.Length == 1 && rows[^1] == changes[0]
			&& changes[0].Changes!.SequenceEqual( [new ChangedFile( "A.cs", 2, 1 ), new ChangedFile( "B.cs", 2, 0 )] ),
			"A finished turn's last row lists the files it changed with lines added and removed; failed edits and earlier turns without edits add none." );
		// The row wraps its card in an entrance cell, which the element tree does not open, so the card is built directly.
		var card = (Blob)typeof( ChatContent ).GetMethod( "BuildChanges", flags )!.Invoke( panel, [changes[0].Key, changes[0].Changes!] )!;
		CoreChecks.Check( Descendants( card ).OfType<Button>().Select( button => button.AccessibilityName ).Order()
			.SequenceEqual( ["Open diff of A.cs, 2 lines added, 1 removed", "Open diff of B.cs, 2 lines added, 0 removed", "View changes"] ),
			"The card offers the folder's diff and each file's." );

		// Each prompt gets a line along the scrollbar at its row's share of the transcript height; agent rows and cards get none.
		var marks = new TranscriptMarks( action =>
		{
			action();
			return true;
		} );
		var tops = new double[rows.Count + 1];
		for ( var i = 0; i < rows.Count; i++ )
			tops[i + 1] = tops[i] + (i == 0 ? 300 : 100);
		marks.Update( rows, tops );
		var promptRow = rows.FindIndex( row => row.Parts is [{ Message.Role: "user", Message.RichContent: { } prompt }] && ChatContent.ComputeMessageText( prompt ) == "Fix the build" );
		CoreChecks.Check( marks.Items is [{ Fraction: 0 }, var last] && last.Key == rows[promptRow].Key
			&& Math.Abs( last.Fraction - tops[promptRow] / tops[^1] ) < 1e-9,
			"The scrollbar marks each prompt at its row's top as a share of the transcript height." );

		// A Lokn tool the agent ran by writing to its xd:// device draws as that tool's card, from the arguments and details Omp keeps, outside activity rows.
		app.Load( new FixtureModule( "sketchy", builder => builder
			.Tool( "draw", "Draws.", """{ "type": "object" }""", ( _, arguments ) => new( "Drew.", Details: arguments.Clone() ) )
			.ToolCard( "draw", card => new Text { Content = $"{card.Status} {card.Arguments.GetProperty( "shape" ).GetString()} {card.Details.GetProperty( "shape" ).GetString()}" } ) ) );
		Add( "assistant", """{"content":[{"type":"toolCall","id":"x1","name":"write","arguments":{"path":"xd://sketchy_draw","content":"{\"shape\":\"box\"}"}}]}""" );
		Add( "toolResult", """{"toolCallId":"x1","toolName":"write","content":[{"type":"text","text":"Drew."}],"details":{"xdev":{"tool":"sketchy_draw","mode":"execute","args":{"shape":"box"},"inner":{"shape":"drawn box"}}}}""" );
		messages = store.GetMessages( chat.Id );
		typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [messages] );
		var call = messages[^2].RichContent!.Value.GetProperty( "content" )[0];
		var drawn = (Blob)typeof( ChatContent ).GetMethod( "BuildToolBody", flags )!.Invoke( panel, ["x1-key", "x1", call] )!;
		CoreChecks.Check( Descendants( drawn ).OfType<Text>().Any( text => text.Content == "Done box drawn box" )
			&& !(bool)typeof( ChatContent ).GetMethod( "IsActivityBlock", flags )!.Invoke( panel, [call] )!,
			"A tool run through its xd:// device draws as its module's card from the arguments and details Omp keeps, outside activity rows." );
		app.Unload( "sketchy" );
		panel.Closed();
	}

	/// <summary>Checks that an image a tool returns shows under its closed summary line and opens full size when clicked.</summary>
	private static void CheckToolImages( LoknApplication app, OmpModule module )
	{
		const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
		var store = new ConversationStore();
		var chat = store.CreateConversation( SettingsProfile.Personal );
		void Add( string role, string json ) => store.AppendMessage( chat.Id, new( Guid.NewGuid(), role, "", DateTimeOffset.UtcNow, Parse( json ) ), persist: false );
		Add( "user", """{"content":[{"type":"text","text":"Show me"}]}""" );
		Add( "assistant", """{"content":[{"type":"toolCall","id":"s1","name":"screenshot","arguments":{}}]}""" );
		Add( "toolResult", $$"""{"toolCallId":"s1","toolName":"screenshot","content":[{"type":"text","text":"Captured"},{"type":"image","mimeType":"image/png","data":"{{Pixel}}"}]}""" );
		using var posted = new System.Collections.Concurrent.BlockingCollection<Action>();
		app.BindDispatcher( action =>
		{
			posted.Add( action );
			return true;
		} );
		void PumpUntil( Func<bool> condition )
		{
			while ( !condition() && posted.TryTake( out var action, TimeSpan.FromSeconds( 30 ) ) )
				action();
		}

		var panel = BuildChat( new PanelContext( app, Guid.NewGuid(), CancellationToken.None, input: null ), store, chat.Id, module.Service );
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		var rows = (List<TranscriptRow>)typeof( ChatContent ).GetMethod( "ComputeRows", flags )!.Invoke( panel, [store.GetMessages( chat.Id )] )!;
		Button? FindThumbnail() => Descendants( (Blob)typeof( ChatContent ).GetMethod( "BuildActivity", flags )!.Invoke( panel, [rows.Single( row => row.IsActivity )] )! )
			.OfType<Button>().SingleOrDefault( button => button.AccessibilityName == "Open image" );
		PumpUntil( () => FindThumbnail() is not null );
		var thumbnail = FindThumbnail();
		CoreChecks.Check( thumbnail is not null && Descendants( thumbnail ).OfType<Image>().Any(), "A tool's image shows under its closed summary line." );
		thumbnail!.OnClick!();
		Blob? BuildViewer() => (Blob?)typeof( ChatContent ).GetMethod( "BuildViewer", flags )!.Invoke( panel, null );
		PumpUntil( () => BuildViewer() is not null );
		CoreChecks.Check( BuildViewer() is Blob viewer && Descendants( viewer ).OfType<Button>().Any( button => button.AccessibilityName == "Close image" ),
			"Clicking an image opens it full size over the chat." );
		panel.Closed();
		app.BindDispatcher( null );
	}

	/// <summary>Lists a declaration and every blob below it.</summary>
	private static IEnumerable<Blob> Descendants( Blob blob )
	{
		yield return blob;
		if ( blob.GetType().GetProperty( "Children" )?.GetValue( blob ) is IEnumerable<Blob> children )
		{
			foreach ( var child in children.SelectMany( Descendants ) )
				yield return child;
		}

		// An entrance cell's mount holds its content in the input snapshot; look through it as the rendered tree would.
		var fields = blob.GetType().GetFields( System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public );
		foreach ( var field in fields )
		{
			if ( field.GetValue( blob ) is RevealInput reveal )
			{
				foreach ( var child in Descendants( reveal.Content ) )
					yield return child;
			}
		}
	}

	/// <summary>Checks todo lists read from Omp's results and init calls, and the code colors a reader sees.</summary>
	private static void CheckRichDisplay()
	{
		var details = Parse( """{"phases":[{"name":"Core","tasks":[{"content":"A","status":"completed"},{"content":"B","status":"in_progress"}]},{"name":"Docs","tasks":[{"content":"C","status":"pending"},{"content":"D","status":"abandoned"}]}]}""" );
		var phases = ChatContent.ComputeTodo( details );
		CoreChecks.Check( phases.Select( phase => phase.Name ).SequenceEqual( ["Core", "Docs"] )
			&& phases.SelectMany( phase => phase.Tasks ).Select( task => task.State ).SequenceEqual( [TaskState.Done, TaskState.Active, TaskState.Pending, TaskState.Dropped] ),
			"A todo result's phases keep their order and each task's state." );
		var planned = ChatContent.ComputeTodo( Parse( """{"op":"init","list":[{"phase":"Plan","items":["One","Two"]}]}""" ) );
		CoreChecks.Check( planned is [{ Name: "Plan", Tasks: [{ Content: "One", State: TaskState.Pending }, { Content: "Two" }] }], "An init call shows its planned tasks before a result reports their state." );

		string Classify( string line, string language )
		{
			var isInComment = false;
			return string.Join( " ", CodeHighlighter.Scan( line, CodeHighlighter.Find( language )!, ref isInComment )
				.Where( run => run.Token != CodeToken.Plain ).Select( run => $"{run.Token}:{line.Substring( run.Start, run.Length )}" ) );
		}
		var csharp = Classify( "var total = await Sum( \"a://b\", 42 ); // done", "csharp" );
		CoreChecks.Check( csharp == "Keyword:var Keyword:await Function:Sum String:\"a://b\" Number:42 Comment:// done", $"C# code colors keywords, calls, strings, numbers and comments ({csharp})." );
		var shell = Classify( "Get-ChildItem $env:TEMP | sort # list a#b", "pwsh" );
		CoreChecks.Check( shell == "Function:Get-ChildItem Type:$env Function:sort Comment:# list a#b", $"Shell commands and variables color, and a # only starts a comment after a space ({shell})." );
		var json = Classify( "{ \"name\": \"Lokn\", \"ok\": true }", "json" );
		CoreChecks.Check( json == "Type:\"name\" String:\"Lokn\" Type:\"ok\" Keyword:true", $"JSON keys color apart from string values ({json})." );
	}

	/// <summary>Checks that Omp's numbered edit diffs and write calls become numbered, hunked diff files.</summary>
	private static void CheckDiffs()
	{
		var lines = DiffParser.Parse( " 42|a\n 43|b\n-44|old\n+44|n1\n+45|n2\n+46|n3\n 45|c\n 46|d\n 105|e\n-106|x\n+108|y" );
		CoreChecks.Check( lines.Select( line => (line.Kind, line.Number) ).SequenceEqual( [
			(DiffLineKind.Context, 42), (DiffLineKind.Context, 43), (DiffLineKind.Removed, 44),
			(DiffLineKind.Added, 44), (DiffLineKind.Added, 45), (DiffLineKind.Added, 46),
			(DiffLineKind.Context, 47), (DiffLineKind.Context, 48), (DiffLineKind.Gap, 58),
			(DiffLineKind.Context, 107), (DiffLineKind.Removed, 106), (DiffLineKind.Added, 108)] ),
			"Edit diffs number context lines in the new file and mark the unchanged lines between hunks." );

		var root = Path.Combine( Path.GetTempPath(), "lokn-diff" );
		var edit = JsonDocument.Parse( JsonSerializer.Serialize( new
		{
			details = new
			{
				diff = "ignored",
				perFileResults = new object[]
				{
					new { path = Path.Combine( root, "src", "A.cs" ), op = "update", diff = "-1|a\n+1|b" },
					new { path = Path.Combine( root, "B.cs" ), op = "create", diff = "+1|x" },
					new { path = Path.Combine( root, "C.cs" ), op = "update", diff = "" },
				},
			},
		} ) ).RootElement;
		var files = DiffParser.ParseTool( "edit", default, edit, root );
		CoreChecks.Check( files.Count == 2 && files[0] is { Path: var first, Added: 1, Removed: 1 } && first == Path.Combine( "src", "A.cs" ) && files[1].Operation == "create",
			"Multi-file edits give one project-relative diff per changed file." );

		var write = JsonDocument.Parse( "{\"path\":\"Notes.md\",\"content\":\"one\\r\\n\\ttwo\\r\\n\"}" ).RootElement;
		var written = DiffParser.ParseTool( "write", write, null, root ).Single();
		CoreChecks.Check( written.Lines.Select( line => (line.Kind, line.Number, line.Text) ).SequenceEqual( [(DiffLineKind.Added, 1, "one"), (DiffLineKind.Added, 2, "\ttwo")] ),
			"A write shows its content as added lines without an empty line for the final newline." );
		var failed = JsonDocument.Parse( "{\"isError\":true}" ).RootElement;
		CoreChecks.Check( DiffParser.ParseTool( "write", write, failed, root ).Count == 0, "A failed write shows no diff." );

		var patch = "diff --git a/src/A.cs b/src/A.cs\nindex 1..2 100644\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -3,3 +3,3 @@ class A\n one\n-two\n+TWO\n three\n@@ -20,2 +20,3 @@\n x\n+y\n z\n"
			+ "diff --git a/new.txt b/new.txt\nnew file mode 100644\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+hello\n"
			+ "diff --git a/img.png b/img.png\nBinary files a/img.png and b/img.png differ\n";
		var parsed = GitDiff.Parse( patch );
		CoreChecks.Check( parsed.Count == 3 && parsed[0].Path == Path.Combine( "src", "A.cs" ) && parsed[0].Lines.SequenceEqual( [
			new( DiffLineKind.Gap, 2, "" ), new( DiffLineKind.Context, 3, "one" ), new( DiffLineKind.Removed, 4, "two" ), new( DiffLineKind.Added, 4, "TWO" ),
			new( DiffLineKind.Context, 5, "three" ), new( DiffLineKind.Gap, 14, "" ), new( DiffLineKind.Context, 20, "x" ), new( DiffLineKind.Added, 21, "y" ), new( DiffLineKind.Context, 22, "z" ),
		] ), "A git diff keeps new line numbers for kept and added lines, old ones for removed lines, and counts the unchanged lines between hunks." );
		CoreChecks.Check( parsed[1] is { Path: "new.txt", Operation: "create", Added: 1 } && parsed[1].Lines[0] == new DiffLine( DiffLineKind.Added, 1, "hello" ) && parsed[2] is { Note: "Binary file", Lines.Count: 0 },
			"A new file has no leading gap, and a binary file is noted rather than shown." );
	}

	/// <summary>Checks Omp's command catalog parsing, prefix-then-substring filtering, and that Enter completes a command needing an argument instead of sending it.</summary>
	private static void CheckSlashCommands( ChatContent panel )
	{
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		panel.Session.UpdateFrame( Parse( """{"type":"available_commands_update","commands":[{"name":"model","source":"builtin","aliases":["models"],"description":"Show current model selection"},{"name":"mcp","source":"builtin","description":"Manage MCP servers","input":{"hint":"<subcommand>"}},{"name":"compact","source":"builtin","description":"Compact the conversation\nKeeps recent turns.","input":{"hint":"[soft|remote] [focus]"}},{"source":"builtin"}]}""" ) );
		var commands = (SlashCommand[])typeof( ChatSession ).GetField( "_commands", flags )!.GetValue( panel.Session )!;
		CoreChecks.Check( commands.Select( command => command.Name ).SequenceEqual( ["model", "mcp", "compact"] ) && commands[0].Aliases.Single() == "models" && commands[1] is { NeedsArgument: true, Hint: "<subcommand>" } && !commands[2].NeedsArgument,
			"Omp's command catalog keeps its order, aliases and argument hints, and skips nameless entries." );
		CoreChecks.Check( ChatContent.ComputeCommandQuery( "/mo" ) == "mo" && ChatContent.ComputeCommandQuery( "/" ) == "" && ChatContent.ComputeCommandQuery( "/compact now" ) is null && ChatContent.ComputeCommandQuery( "see /model" ) is null,
			"Only a prompt that is a leading slash and a name being typed opens the command list." );
		CoreChecks.Check( ChatContent.ComputeCommandMatches( commands, "M" ).Select( command => command.Name ).SequenceEqual( ["model", "mcp", "compact"] )
			&& ChatContent.ComputeCommandMatches( commands, "models" ).Single().Name == "model" && ChatContent.ComputeCommandMatches( commands, "" ).Count == 3,
			"Commands starting with the query, by name or alias, list before those only containing it, and an empty query lists them all." );
		var composer = (TextEditorController)typeof( ChatContent ).GetField( "_composer", flags )!.GetValue( panel )!;
		composer.Document.Apply( new TextChange( new TextRange( 0, composer.Document.Length ), "/mc" ) );
		typeof( ChatContent ).GetMethod( "UpdatePromptChange", flags )!.Invoke( panel, null );
		typeof( ChatContent ).GetMethod( "UpdateEnter", flags )!.Invoke( panel, null );
		CoreChecks.Check( composer.Document.GetText() == "/mcp " && typeof( ChatSession ).GetField( "_isBusy", flags )!.GetValue( panel.Session ) is false,
			"Enter completes a command that needs an argument, leaving room to type it, instead of sending it." );
		composer.Document.Apply( new TextChange( new TextRange( 0, composer.Document.Length ), "" ) );
	}

	/// <summary>Checks that a local command's output shows in the transcript under the command, that unrequested output stays hidden, and that widget frames keep the status.</summary>
	private static void CheckCommandOutput( ChatContent panel, ConversationStore store, Guid conversationId )
	{
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		var sent = typeof( ChatSession ).GetField( "_sentCommand", flags )!;
		var before = store.GetMessages( conversationId ).Count;
		panel.Session.UpdateFrame( Parse( """{"type":"command_output","text":"Title: Lokn"}""" ) );
		CoreChecks.Check( store.GetMessages( conversationId ).Count == before, "Command output with no command sent stays out of the transcript." );
		sent.SetValue( panel.Session, "/usage" );
		panel.Session.UpdateFrame( Parse( """{"type":"command_output","text":"Usage\n  [\u001b[1m\u001b[38;5;39m·\u001b[22m\u001b[39m]\n  resets in 14d\n"}""" ) );
		var output = store.GetMessages( conversationId )[^1];
		CoreChecks.Check( output is { Role: ChatContent.CommandRole, Text: "Usage\n  [·]\n  resets in 14d" } && output.RichContent!.Value.GetProperty( "command" ).GetString() == "/usage",
			"A command's output joins the transcript under the command, without terminal colors, and keeps its lines." );
		var status = typeof( ChatSession ).GetField( "_status", flags )!;
		panel.Session.UpdateFrame( Parse( """{"type":"extension_ui_request","method":"notify","id":"n1","message":"Model set to sol"}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"extension_ui_request","method":"setWidget","id":"w1","widgetKey":"autoresearch"}""" ) );
		CoreChecks.Check( status.GetValue( panel.Session ) is "Model set to sol", "A command's notice stays in the status line when a widget update follows it." );
		sent.SetValue( panel.Session, "/compact" );
		panel.Session.UpdateFrame( Parse( """{"type":"manual_compaction_start"}""" ) );
		CoreChecks.Check( panel.IsBusy && status.GetValue( panel.Session ) is "Compacting…" && store.IsWorking( conversationId ), "A manual compaction shows the chat as working, in the panel and the sidebar, until it ends." );
		panel.Session.UpdateFrame( Parse( """{"type":"manual_compaction_end","aborted":false,"skipped":true,"errorMessage":"Nothing to compact (session too small)"}""" ) );
		panel.Session.UpdateFrame( Parse( """{"type":"command_output","text":"Compaction failed: Nothing to compact (session too small)"}""" ) );
		CoreChecks.Check( !panel.IsBusy && !store.IsWorking( conversationId ) && status.GetValue( panel.Session ) is "Nothing to compact (session too small)"
			&& store.GetMessages( conversationId )[^1] is { Role: ChatContent.CommandRole, Text: "Compaction failed: Nothing to compact (session too small)" },
			"Output a command prints after Omp admits it, as /compact does, still shows." );
		sent.SetValue( panel.Session, null );
	}

	/// <summary>Runs git in a test repository with a fixed author, failing the check when git fails.</summary>
	internal static void RunGit( string root, params string[] arguments )
	{
		var start = new System.Diagnostics.ProcessStartInfo( "git" ) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach ( var argument in (string[])["-c", "user.name=Lokn", "-c", "user.email=lokn@example.com", .. arguments] )
			start.ArgumentList.Add( argument );
		using var process = System.Diagnostics.Process.Start( start )!;
		process.WaitForExit();
		CoreChecks.Check( process.ExitCode == 0, $"git {string.Join( ' ', arguments )} succeeds." );
	}

	/// <summary>Deletes a test repository, clearing the read-only flag git puts on its objects.</summary>
	internal static void DeleteRepository( string root )
	{
		foreach ( var file in Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories ) )
			File.SetAttributes( file, FileAttributes.Normal );
		Directory.Delete( root, recursive: true );
	}

	/// <summary>Clones one fixture frame independently of its JSON parser lifetime.</summary>
	private static JsonElement Parse( string json )
	{
		using var document = JsonDocument.Parse( json );
		return document.RootElement.Clone();
	}
}

/// <summary>Reaches the local engine behind a chat view's session in focused checks.</summary>
internal static class ChatSessionCheckExtensions
{
	/// <summary>Feeds one Omp frame to the local session behind a view, as its process would.</summary>
	internal static void UpdateFrame( this IChatSession session, JsonElement frame ) => ((ChatSession)session).UpdateFrame( frame );
}
