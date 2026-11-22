using System.Text.Json;

namespace Lokn.Tests;

/// <summary>Runs a deterministic local Omp peer for native chat checks without provider access.</summary>
internal static class OmpUiFixture
{
	/// <summary>Lists deterministic account-dependent models before RPC startup or model selection.</summary>
	internal static void BuildModelList( string[] args )
	{
		// With LOKN_FIXTURE_SIGNIN set, every profile is signed in only once a fixture login wrote that file.
		var marker = Environment.GetEnvironmentVariable( SignInVariable );
		var isSignedOut = marker is { Length: > 0 } ? !File.Exists( marker ) : args.Contains( "personal" );
		var models = isSignedOut ? [] : new[]
		{
			new { provider = "fixture", id = "one", name = "Fixture One — café 👋" },
			new { provider = "fixture", id = "two", name = "Fixture Two" },
		};
		Console.OutputEncoding = new System.Text.UTF8Encoding( encoderShouldEmitUTF8Identifier: false );
		Console.WriteLine( JsonSerializer.Serialize( new
		{
			models
		}, JsonOptions ) );
	}

	/// <summary>Prints deterministic provider limits for usage-panel checks without contacting a provider.</summary>
	internal static void BuildUsage()
	{
		var now = DateTimeOffset.UtcNow;
		Console.OutputEncoding = new System.Text.UTF8Encoding( encoderShouldEmitUTF8Identifier: false );
		Console.WriteLine( JsonSerializer.Serialize( new
		{
			generatedAt = now.ToUnixTimeMilliseconds(),
			reports = new[]
			{
				new
				{
					provider = "fixture",
					limits = new object[]
					{
						new { label = "Fixture 5 Hour", window = new { resetsAt = now.AddHours( 2 ).ToUnixTimeMilliseconds() }, amount = new { usedFraction = 0.37 }, status = "ok" },
						new { label = "Fixture 7 Day", window = new { resetsAt = now.AddDays( 5 ).ToUnixTimeMilliseconds() }, amount = new { usedFraction = 0.12 }, status = "ok" },
					},
					metadata = new { email = "person@example.com" },
					resetCredits = new { availableCount = 2, redeemableCount = 1 },
				},
			},
		}, JsonOptions ) );
	}

	/// <summary>Handles the minimal real JSONL exchange used by the composer and confirmation UI.</summary>
	internal static async Task RunAsync()
	{
		Console.OutputEncoding = new System.Text.UTF8Encoding( encoderShouldEmitUTF8Identifier: false );
		await SendAsync( new
		{
			type = "ready",
			supportedProtocolVersions = new[] { 1, 2 }
		} );
		var steering = new List<string>();
		var followUps = new List<string>();
		static bool Add( List<string> queue, string message )
		{
			queue.Add( message );
			return true;
		}

		// The long turn scripts/bench.ps1 asks for runs until the chat stops it.
		CancellationTokenSource? longTurn = null;
		// Omp's goal command keeps one goal, which create refuses to replace, and reports each change in goal_updated.
		Dictionary<string, object?>? goal = null;
		while ( await Console.In.ReadLineAsync() is { } line )
		{
			using var document = JsonDocument.Parse( line );
			var request = document.RootElement;
			var type = request.GetProperty( "type" ).GetString();
			if ( type is "get_login_providers" or "login" )
			{
				await AnswerLoginAsync( request, type );
				continue;
			}

			// A form answer is echoed as it arrived, so checks can read the data Loom sent.
			if ( type == "extension_ui_response" && request.TryGetProperty( "data", out var formData ) )
			{
				await SendMessageAsync( "fixture-form", new
				{
					role = "assistant",
					content = new[] { new { type = "text", text = "Fixture form data: " + formData.GetRawText() } },
				} );
				continue;
			}

			if ( type == "extension_ui_response" )
			{
				var accepted = request.TryGetProperty( "confirmed", out var confirmed ) && confirmed.ValueKind == JsonValueKind.True;
				await SendMessageAsync( "fixture-result", new
				{
					role = "toolResult",
					toolCallId = "fixture-tool",
					toolName = "bash",
					content = new[] { new { type = "text", text = accepted ? "Fixture approval received." : "Fixture approval cancelled." } },
					isError = !accepted,
				} );
				await SendAsync( new
				{
					type = "agent_end",
					isTerminal = true
				} );
				continue;
			}

			// Omp's btw admits a side question at once, then reports its answer as a finished record.
			if ( type == "btw" )
			{
				var question = request.GetProperty( "question" ).GetString();
				await SendAsync( new
				{
					type = "response",
					id = request.GetProperty( "id" ).GetString(),
					command = type,
					success = true,
					data = new { record = new { id = "fixture-title", question, status = "running" } },
				} );
				await SendAsync( new
				{
					type = "btw_record",
					record = new { id = "fixture-title", question, answer = "💬 FEED: Fixture summary title here.", status = "complete" },
				} );
				continue;
			}

			// Omp's queue: steering and follow-up messages wait, a follow-up can be promoted to steering, and either can be taken back.
			if ( type is "steer" or "follow_up" or "remove_queued_message" or "promote_queued_message" )
			{
				var message = request.GetProperty( "message" ).GetString() ?? "";
				var taken = type switch
				{
					"steer" => Add( steering, message ),
					"follow_up" => Add( followUps, message ),
					"promote_queued_message" => followUps.Remove( message ) && Add( steering, message ),
					_ => (request.GetProperty( "queue" ).GetString() == "steering" ? steering : followUps).Remove( message ),
				};
				await SendAsync( new { type = "queue_update", steering, followUp = followUps, liveSteered = 0 } );
				await SendAsync( new { type = "response", id = request.GetProperty( "id" ).GetString(), command = type, success = true, data = new { removed = taken, promoted = taken } } );
				continue;
			}

			if ( type == "goal" )
			{
				var op = request.GetProperty( "op" ).GetString();
				var goalId = request.GetProperty( "id" ).GetString();
				if ( op == "create" && goal is not null )
				{
					await SendAsync( new { type = "response", id = goalId, command = type, success = false, error = "A goal is already active." } );
					continue;
				}

				if ( op == "create" )
					goal = new() { ["id"] = "fixture-goal", ["objective"] = request.GetProperty( "objective" ).GetString(), ["status"] = "active", ["tokensUsed"] = 1200, ["tokenBudget"] = 50000, ["timeUsedSeconds"] = 3 };
				else if ( op is "pause" or "resume" && goal is not null )
					goal["status"] = op == "pause" ? "paused" : "active";
				else if ( op == "drop" )
					goal = null;
				if ( op != "get" )
					await SendAsync( new { type = "goal_updated", goal } );
				await SendAsync( new { type = "response", id = goalId, command = type, success = true, data = new { goal, state = (object?)null } } );
				continue;
			}

			// Frames without an id, such as set_session_name, expect no answer.
			if ( !request.TryGetProperty( "id", out var requestId ) )
				continue;

			var id = requestId.GetString();
			object data = type == "get_state" ? new
			{
				model = new
				{
					provider = "fixture",
					id = "one"
				},
				autoCompactionEnabled = true,
				contextUsage = new
				{
					tokens = 84000,
					contextWindow = 200000,
					percent = 42,
				},
			} : type == "get_messages" ? new
			{
				messages = Array.Empty<object>()
			} : type == "get_available_thinking_levels" ? new
			{
				levels = new[] { "off", "minimal", "low", "high" }
			} : type == "get_subagents" ? new
			{
				subagents = new[]
				{
					new
					{
						id = "fixture-agent",
						status = "running",
						sessionFile = "fixture-agent.jsonl",
						progress = new
						{
							id = "fixture-agent",
							agent = "reviewer",
							status = "running",
							task = "Index fixture repository",
							stage = "Inspecting changed files",
							currentTool = "read",
							currentToolArgs = "src/Fixture.cs",
							resolvedModel = "fixture/worker:low",
							tokens = 1200,
							contextTokens = 2400,
							contextWindow = 32000,
							cost = 0.0012,
							durationMs = 45000,
						},
					},
				},
			} : type == "get_subagent_messages" ? new
			{
				messages = new object[]
				{
					new { role = "user", content = new[] { new { type = "text", text = "Index the repository." } } },
					new { role = "assistant", content = new[] { new { type = "text", text = "Fixture subagent transcript." } } },
				},
			} : new
			{
			};
			await SendAsync( new
			{
				type = "response",
				id,
				command = type,
				success = true,
				data
			} );
			if ( type == "abort" && longTurn is not null )
			{
				await longTurn.CancelAsync();
				longTurn = null;
				await SendAsync( new
				{
					type = "agent_end",
					isTerminal = true
				} );
				continue;
			}

			if ( type != "prompt" )
				continue;

			var prompt = request.GetProperty( "message" ).GetString() ?? "";
			if ( Environment.GetEnvironmentVariable( TurnVariable ) is "busy" or "stream" )
			{
				await SendAsync( new
				{
					type = "agent_start"
				} );
				await SendMessageAsync( "fixture-bench-user", new
				{
					role = "user",
					content = new[] { new { type = "text", text = prompt } },
				} );
				longTurn = new CancellationTokenSource();
				if ( Environment.GetEnvironmentVariable( TurnVariable ) == "stream" )
					_ = StreamAsync( longTurn.Token );
				continue;
			}

			if ( prompt.StartsWith( "/review ", StringComparison.Ordinal ) )
			{
				await SendAsync( new
				{
					type = "agent_start"
				} );
				await SendMessageAsync( "fixture-review-user", new
				{
					role = "user",
					content = new[] { new { type = "text", text = prompt } },
				} );
				await SendMessageAsync( "fixture-review-assistant", new
				{
					role = "assistant",
					content = new[] { new { type = "text", text = """
						Review complete.
						<lokn-review>{"overall_correctness":"incorrect","explanation":"Review fixture found one issue.","confidence":0.98,"findings":[{"title":"Preserve the fixture value","body":"The changed line drops the expected value.","priority":1,"confidence":0.96,"file_path":"review.txt","line_start":2,"line_end":2}]}</lokn-review>
						""" } },
				} );
				await SendAsync( new
				{
					type = "agent_end",
					isTerminal = true
				} );
				continue;
			}

			// A /plan prompt carries Lokn's plan instructions; the plan comes back between its tags.
			if ( prompt.Contains( "<lokn-plan-mode>", StringComparison.Ordinal ) )
			{
				await SendAsync( new { type = "agent_start" } );
				await SendMessageAsync( "fixture-plan-user", new
				{
					role = "user",
					content = new[] { new { type = "text", text = prompt } },
				} );
				await SendMessageAsync( "fixture-plan-assistant", new
				{
					role = "assistant",
					content = new[] { new { type = "text", text = "Explored the fixture.\n\n<lokn-plan>\n1. Add the theme.\n2. Verify it.\n</lokn-plan>" } },
				} );
				await SendAsync( new { type = "agent_end", isTerminal = true } );
				continue;
			}

			await SendAsync( new
			{
				type = "agent_start"
			} );
			await SendMessageAsync( "fixture-user", new
			{
				role = "user",
				content = new[] { new { type = "text", text = request.GetProperty( "message" ).GetString() } },
			} );
			var partial = new
			{
				role = "assistant",
				content = new[] { new { type = "text", text = "Streaming" } }
			};
			await SendAsync( new
			{
				type = "message_start",
				messageId = "fixture-assistant",
				message = partial
			} );
			var complete = new
			{
				role = "assistant",
				content = new object[]
				{
					new { type = "text", text = "Streaming complete.\n\n- [x] Native composer\n- [x] JSONL stream" },
					new { type = "toolCall", id = "fixture-tool", name = "bash", arguments = new { command = "fixture only" } },
				},
			};
			await SendAsync( new
			{
				type = "message_update",
				messageId = "fixture-assistant",
				message = complete
			} );
			await SendAsync( new
			{
				type = "message_end",
				messageId = "fixture-assistant",
				message = complete
			} );
			await SendAsync( new
			{
				type = "extension_ui_request",
				method = "confirm",
				id = "approval",
				title = "Run fixture tool?",
				message = "Allow local fixture?"
			} );
		}
	}

	/// <summary>Names the variable holding the file a fixture login writes; while it is missing every profile reads as signed out.</summary>
	internal const string SignInVariable = "LOKN_FIXTURE_SIGNIN";

	/// <summary>Names the variable scripts/bench.ps1 sets for a turn that runs until the chat stops it: <c>busy</c> works silently, <c>stream</c> writes
	/// a Markdown answer a little at a time.</summary>
	internal const string TurnVariable = "LOKN_FIXTURE_TURN";

	/// <summary>Names the variable holding the milliseconds between a streaming turn's updates; 500 without it.</summary>
	internal const string StreamIntervalVariable = "LOKN_FIXTURE_STREAM_MS";

	/// <summary>Holds the pieces a streaming turn writes in turn: prose, a list, a table and code, as an agent's answers have.</summary>
	private static readonly string[] StreamPieces =
	[
		"The working indicator stays on while the answer grows, one piece at a time. ",
		"Each update carries the whole message so far, as Omp's do.\n\n",
		"- **Build** the declaration\n- *Diff* it against the last one\n- Lay out and paint what changed\n\n",
		"| Phase | Work |\n|---|---|\n| build | cells |\n| layout | Yoga |\n\n",
		"```csharp\nvar total = items.Sum( item => item.Count );\nConsole.WriteLine( total );\n```\n\n",
	];

	/// <summary>Writes a growing assistant message every interval until the turn is stopped.</summary>
	private static async Task StreamAsync( CancellationToken cancellation )
	{
		var interval = int.TryParse( Environment.GetEnvironmentVariable( StreamIntervalVariable ), out var ms ) && ms > 0 ? ms : 500;
		var text = new System.Text.StringBuilder();
		try
		{
			for ( var piece = 0; !cancellation.IsCancellationRequested; piece++ )
			{
				text.Append( StreamPieces[piece % StreamPieces.Length] );
				var message = new
				{
					role = "assistant",
					content = new[] { new { type = "text", text = text.ToString() } }
				};
				await SendAsync( new
				{
					type = piece == 0 ? "message_start" : "message_update",
					messageId = "fixture-bench-assistant",
					message
				} );
				await Task.Delay( interval, cancellation );
			}
		}
		catch ( OperationCanceledException )
		{
			// The chat stopped the turn.
		}
	}

	/// <summary>Answers sign-in requests: a browser provider that succeeds after naming its page, and a key provider that fails as Omp does for secret prompts.</summary>
	private static async Task AnswerLoginAsync( JsonElement request, string type )
	{
		var id = request.GetProperty( "id" ).GetString();
		if ( type == "get_login_providers" )
		{
			var isSignedIn = Environment.GetEnvironmentVariable( SignInVariable ) is { Length: > 0 } marker && File.Exists( marker );
			await SendAsync( new { type = "response", id, command = type, success = true, data = new { providers = new[] { new { id = "fixture", name = "Fixture", authenticated = isSignedIn }, new { id = "keyed", name = "Keyed", authenticated = false } } } } );
			return;
		}

		if ( request.GetProperty( "providerId" ).GetString() == "keyed" )
		{
			await SendAsync( new { type = "response", id, command = type, success = false, error = "This provider asks for a secret. Use omp login in a terminal." } );
			return;
		}

		// Not an http address, so the page reports it instead of opening a real browser.
		await SendAsync( new { type = "extension_ui_request", id = "fixture-url", method = "open_url", url = "fixture://sign-in" } );
		if ( Environment.GetEnvironmentVariable( SignInVariable ) is { Length: > 0 } file )
			await File.WriteAllTextAsync( file, "signed in" );
		await SendAsync( new { type = "response", id, command = type, success = true, data = new { } } );
	}

	/// <summary>Sends one complete message using stable streaming identity.</summary>
	private static async Task SendMessageAsync( string id, object message )
	{
		await SendAsync( new
		{
			type = "message_start",
			messageId = id,
			message
		} );
		await SendAsync( new
		{
			type = "message_end",
			messageId = id,
			message
		} );
	}

	/// <summary>Writes and flushes one complete JSONL frame; a streaming turn writes from its own task, so frames never interleave.</summary>
	private static async Task SendAsync( object frame )
	{
		await Output.WaitAsync();
		try
		{
			await Console.Out.WriteLineAsync( JsonSerializer.Serialize( frame, JsonOptions ) );
			await Console.Out.FlushAsync();
		}
		finally
		{
			Output.Release();
		}
	}

	/// <summary>Lets one frame at a time reach the output.</summary>
	private static readonly SemaphoreSlim Output = new( 1, 1 );

	/// <summary>Emits actual UTF-8 characters so transport checks cannot pass using ASCII JSON escapes alone.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};
}
