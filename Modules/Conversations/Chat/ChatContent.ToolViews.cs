using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Git;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Draws file reads as one group of colored file chips and shell commands as a box with the command, its output and its outcome, as Tern does.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the corner radius of a file chip, in logical pixels: fully round at its height.</summary>
	private const int ChipRadius = 11;

	/// <summary>Reports whether a block is a call of the read tool, which joins its neighbours in one group.</summary>
	private static bool IsReadCall( JsonElement block ) => ReadText( block, "type" ) == "toolCall" && ReadText( block, "name" ) == "read";

	/// <summary>Builds the blocks of an activity run, with each run of consecutive reads drawn as one group.</summary>
	/// <param name="parts">The run's parts, in order.</param>
	/// <param name="body">The list the built blocks are added to.</param>
	private void AddActivityBlocks( IReadOnlyList<TranscriptPart> parts, List<Blob> body )
	{
		var reads = new List<(string Key, JsonElement Block)>();
		void Flush()
		{
			if ( reads.Count > 0 )
				body.Add( BuildReadGroup( reads ) );
			reads = [];
		}

		foreach ( var part in parts )
		{
			var index = part.Start;
			foreach ( var block in ReadBlocks( part ) )
			{
				var key = $"{part.Message.Id:N}:{index++}";
				if ( IsReadCall( block ) )
				{
					reads.Add( (key, block) );
					continue;
				}

				// Blank text between two reads shows nothing, so it does not split their group.
				if ( reads.Count > 0 && ReadText( block, "type" ) == "text" && string.IsNullOrWhiteSpace( ReadText( block, "text" ) ) )
					continue;
				Flush();
				body.AddRange( BuildRichBlock( key, block, "assistant" ) );
			}
		}
		Flush();
	}

	/// <summary>Builds consecutive reads as a "Read N files" head over one chip per file; a chip opens its file, and its output once clicked again.</summary>
	private Container BuildReadGroup( List<(string Key, JsonElement Block)> reads )
	{
		var groupKey = reads[0].Key + ":reads";
		var chips = new List<Blob>();
		var outputs = new List<Blob>();
		var running = false;
		foreach ( var (key, block) in reads )
		{
			var id = ReadText( block, "id" );
			_shownTools.Add( id );
			var status = ComputeToolStatus( id, out var result );
			running |= status == "Running";
			var arguments = block.TryGetProperty( "arguments", out var args ) ? args : default;
			chips.Add( BuildFileChip( key, ComputeToolPath( arguments ), status ) );
			if ( _expanded.Contains( key ) && result is { } body && TerminalText.BuildPlain( ComputeResultText( body ) ) is { Length: > 0 } text )
				outputs.Add( BuildCodePreview( key + ":output", text, "text" ) );
		}

		var head = new Row
		{
			Key = "reads-head",
			AlignItems = AlignItems.Center,
			Gap = Theme.Current.Spacing,
			Children =
			[
				running ? ChatIcons.BuildSpinner( "reads-spinner" ) : ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "read" ) ),
				BuildToolTitle( "Read", Count( reads.Count, "file", "files" ) ),
			],
		};
		var children = new List<Blob>
		{
			head,
			new Row { Key = "reads-chips", FlexWrap = FlexWrap.Wrap, Gap = Theme.Current.Spacing / 2, Children = chips },
		};
		children.AddRange( outputs );
		return new Container
		{
			Key = groupKey,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"Read {Count( reads.Count, "file", "files" )}",
			Gap = Theme.Current.Spacing / 2,
			Children = children,
		};
	}

	/// <summary>Builds a tool's title: its name in bold, then what it did in the body color, such as "Read 3 files" or "Bash Checking versions".</summary>
	private static Text BuildToolTitle( string name, string detail ) => new( [
		new( name, new Style { FontWeight = 600, Color = Theme.Current.Text } ),
		new( detail.Length > 0 ? "  " + detail : "", new Style { Color = Theme.Current.Text } ),
	] )
	{
		FlexShrink = 1,
		MinWidth = 0,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Builds one file a read opened as a rounded chip: a file icon, the path in green code font and any line range muted after it.</summary>
	/// <param name="key">The read's key; clicking the chip opens the file and shows or hides the read's output under the group.</param>
	/// <param name="path">The path or address read, with any line selector.</param>
	/// <param name="status">The read's status; a failed read shows its path in the error color.</param>
	private Button BuildFileChip( string key, string path, string status )
	{
		var theme = Theme.Current;
		var (target, selector) = SplitSelector( path );
		var shown = target;
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		if ( home.Length > 0 && shown.StartsWith( home, StringComparison.OrdinalIgnoreCase ) )
			shown = "~" + shown[home.Length..];
		var color = status == "Failed" ? theme.Error : theme.AnsiGreen;
		var button = theme.BuildButton( shown, () => context.Run( () =>
		{
			UpdateExpanded( key );
			if ( _expanded.Contains( key ) && ComputeFileLink( target ) is { } link )
				OpenLink( link );
		} ) );
		button.Key = "chip:" + key;
		button.AccessibilityName = $"{shown}{selector}, {status}";
		button.FlexDirection = FlexDirection.Row;
		button.AlignItems = AlignItems.Center;
		button.Gap = theme.Spacing / 2;
		button.Height = ChipRadius * 2;
		button.Padding = new Edges( 0, theme.Spacing );
		button.BorderRadius = ChipRadius;
		button.BorderWidth = 0;
		button.BackgroundColor = theme.Raised;
		button.MinWidth = 0;
		button.MaxWidth = Length.Percent( 100 );
		button.Children =
		[
			status == "Running" ? ChatIcons.BuildSpinner( "chip-spinner" ) : ChatIcons.BuildIcon( "\uE7C3", theme.Muted ),
			new Text( [
				new( shown, new Style { FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = color } ),
				new( selector, new Style { FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted } ),
			] )
			{
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		];
		return button;
	}

	/// <summary>Splits a read's target from its line selector, such as <c>view.cs:10-40</c>; a web address keeps its own colon.</summary>
	private static (string Target, string Selector) SplitSelector( string path )
	{
		var colon = path.LastIndexOf( ':' );
		return colon > 0 && colon < path.Length - 1 && path[(colon + 1)..].All( character => char.IsDigit( character ) || character is '-' or '+' or ',' )
			? (path[..colon], path[colon..])
			: (path, "");
	}

	/// <summary>Gets the link that opens what a read read: a web address as it is, a file in the editor, or null when there is nothing to open.</summary>
	private static Uri? ComputeFileLink( string target )
	{
		if ( target.Length == 0 )
			return null;
		if ( Uri.TryCreate( target, UriKind.Absolute, out var web ) && web.Scheme is "http" or "https" )
			return web;
		return target.Contains( "://", StringComparison.Ordinal ) ? null : new Uri( $"{LoknLinks.Create( "editor", "open" )}?path={Uri.EscapeDataString( target )}" );
	}

	/// <summary>Builds a shell command as a bold "Bash" with its intent, over a box with the highlighted command, its output and how it ended.</summary>
	private Container BuildBash( string key, string id, JsonElement arguments, string status, JsonElement? result, string intent )
	{
		var theme = Theme.Current;
		var command = ReadText( arguments, "command" );
		var mark = status switch
		{
			"Running" => ChatIcons.BuildSpinner( "bash-spinner" ),
			"Failed" => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "bash" ), theme.Error ),
			_ => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "bash" ) ),
		};
		var box = new List<Blob> { new Container { Key = "command", Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2, 0, theme.Spacing * 2 ), Children = [MarkdownView.BuildCode( command, "bash", framed: false, showLanguage: false )] } };
		// A backgrounded command's output is Omp's note to the agent, not the command's output, so only its job shows.
		var job = result is { ValueKind: JsonValueKind.Object } done && done.TryGetProperty( "details", out var details )
			&& details.ValueKind == JsonValueKind.Object && details.TryGetProperty( "async", out var background ) ? ReadText( background, "jobId" ) : "";
		if ( job.Length == 0 && result is { } body && TerminalText.BuildPlain( ComputeResultText( body ) ).TrimEnd() is { Length: > 0 } output )
		{
			// Long output shows its last lines, fading in at the top, with Expand on the rule above them; expanded, all of it and Collapse.
			var outputLines = output.Split( '\n' );
			var isLong = outputLines.Length > PreviewLines + PreviewSlack;
			var outputKey = key + ":output";
			var isOpen = _expanded.Contains( outputKey );
			var rule = new Container { Key = "output-rule", Height = theme.BorderWidth, BackgroundColor = theme.Divider };
			if ( isLong )
				rule.Children = [BuildExpandPill( isOpen, () => context.Run( () => UpdateExpanded( outputKey ) ) )];
			box.Add( rule );
			var shownOutput = isLong && !isOpen ? string.Join( '\n', outputLines[^PreviewLines..] ) : output;
			// Output that arrives while the card shows fades and rises in, as do the other parts below.
			box.Add( BuildEntering( key + ":output", new Container
			{
				Key = "output",
				Padding = new Edges( isLong ? theme.Spacing : 0, theme.Spacing * 2, 0, theme.Spacing * 2 ),
				Children = [MarkdownView.BuildCode( shownOutput, "text", framed: false, showLanguage: false, fadeHeadLines: isLong && !isOpen ? PreviewFadeLines : 0 )],
			} ) );
		}
		var (word, color) = status switch
		{
			_ when job.Length > 0 => ("● In background", theme.AnsiMagenta),
			"Running" => ("Running", theme.Muted),
			"Failed" => ("✗ Failed", theme.Error),
			"Requested" => ("Waiting", theme.Muted),
			_ => ("✓ Done", theme.AnsiGreen),
		};
		// The footer shows how long the command ran, counting up while it runs.
		var footer = new List<Blob>
		{
			new Text( [
				new( word, new Style { Color = color } ),
				new( job.Length > 0 ? "   Job " + job : "", new Style { Color = theme.Muted } ),
			] ) { FontSize = ViewStyle.SmallSize },
		};
		if ( job.Length == 0 && ComputeToolTimes( id ) is var (started, ended) && status != "Requested" )
			footer.Add( Cell.Mount<ToolClockInput, ToolClock>( "clock", new( started, ended ) ) );
		// A new outcome, such as Running turning into Done, enters again.
		box.Add( BuildEntering( key + ":outcome:" + word, new Row
		{
			Key = "outcome",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing * 2, theme.Spacing * 1.5, theme.Spacing * 2 ),
			Children = footer,
		} ) );

		var children = new List<Blob>
		{
			new Row { Key = "tool-head", AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = [mark, BuildToolTitle( "Bash", intent )] },
			new Container
			{
				Key = "bash-box:" + id,
				Gap = theme.Spacing,
				BackgroundColor = theme.Text.WithAlpha( 0.035 ),
				BorderWidth = theme.BorderWidth,
				BorderColor = theme.Border.WithAlpha( 0.45 ),
				BorderRadius = theme.Radius * 2,
				Overflow = Overflow.Hidden,
				Children = box,
			},
		};
		// Each commit the command made gets a card under it that opens the commit in the Git panel.
		if ( result is { } finished && command.Contains( "commit", StringComparison.Ordinal )
			&& ParseCommits( TerminalText.BuildPlain( ComputeResultText( finished ) ) ) is { Count: > 0 } commits )
		{
			var folder = ComputeCommandFolder( arguments, command );
			foreach ( var (index, commit) in commits.Index() )
				children.Add( BuildCommitCard( "commit:" + index, commit, folder ) );
		}

		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"bash tool {status}",
			Gap = theme.Spacing * 1.25,
			Children = children,
		};
	}

	/// <summary>Builds a wait: one line with a spinner while it waits; once jobs settled, a count over a card per job with its mark, intent, id, time and output.</summary>
	private Container BuildWait( string key, string status, JsonElement? result )
	{
		var theme = Theme.Current;
		var jobs = result is { ValueKind: JsonValueKind.Object } done && done.TryGetProperty( "details", out var details ) && details.ValueKind == JsonValueKind.Object
			&& details.TryGetProperty( "jobs", out var list ) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
		if ( jobs.Count > 0 && status != "Running" )
		{
			var completed = jobs.Count( job => ReadText( job, "status" ) == "completed" );
			// A job the wait stopped waiting for still runs in the background; only one that ended otherwise failed.
			var running = jobs.Count( job => IsJobRunning( ReadText( job, "status" ) ) );
			var failed = jobs.Count - completed - running;
			var cards = new List<Blob>();
			foreach ( var (index, job) in jobs.Index() )
			{
				var id = ReadText( job, "jobId", ReadText( job, "id" ) );
				var ok = ReadText( job, "status" ) == "completed";
				var isRunning = IsJobRunning( ReadText( job, "status" ) );
				var seconds = job.TryGetProperty( "durationMs", out var ms ) && ms.TryGetDouble( out var value ) ? $"  {value / 1000:0.0}s" : "";
				var rows = new List<Blob>
				{
					new Row
					{
						Key = "job-head",
						AlignItems = AlignItems.Center,
						Gap = theme.Spacing,
						Children =
						[
							new Text { Content = ok ? "✓" : isRunning ? "●" : "✗", Color = ok ? theme.AnsiGreen : isRunning ? theme.AnsiMagenta : theme.Error, FontSize = ViewStyle.SmallSize, FlexShrink = 0 },
							new Text { Content = ReadText( job, "intent", ReadText( job, "label", id ) ), Color = theme.Text, FontWeight = 600, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
							new Spacer(),
							new Text { Content = id + seconds, Color = theme.Muted, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, FlexShrink = 0 },
						],
					},
				};
				if ( TerminalText.BuildPlain( ReadText( job, "resultText", ReadText( job, "output" ) ) ).Trim() is { Length: > 0 } output )
					rows.Add( BuildText( output, muted: true ) );
				cards.Add( new Container
				{
					Key = key + ":job:" + index,
					Gap = theme.Spacing,
					Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2 ),
					BackgroundColor = theme.Text.WithAlpha( 0.035 ),
					BorderWidth = theme.BorderWidth,
					BorderColor = theme.Border.WithAlpha( 0.45 ),
					BorderRadius = theme.Radius * 2,
					Children = rows,
				} );
			}
			var head = new Row
			{
				Key = "tool-head",
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "wait" ) ),
					new Text( [
						new( Count( jobs.Count - running, "job", "jobs" ) + " settled", new Style { FontWeight = 600, Color = theme.Text } ),
						new( completed > 0 ? $"  {completed} done" : "", new Style { Color = theme.AnsiYellow } ),
						new( running > 0 ? $"  {running} in background" : "", new Style { Color = theme.AnsiMagenta } ),
						new( failed > 0 ? $"  {failed} failed" : "", new Style { Color = theme.Error } ),
					] ),
				],
			};
			return new Container { Key = key, AccessibilityRole = AccessibilityRole.Group, AccessibilityName = "wait tool " + status, Gap = theme.Spacing, Children = [head, .. cards] };
		}
		var outcome = result is { } body && status != "Running" ? TerminalText.BuildPlain( ComputeResultText( body ) ).Trim().TrimEnd( '.' ) : "";
		if ( outcome.StartsWith( "Wait ", StringComparison.Ordinal ) )
			outcome = outcome[5..];
		var children = new List<Blob>
		{
			ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "wait" ), status == "Failed" ? theme.Error : theme.AnsiMagenta ),
			new Text( [
				new( "Wait", new Style { FontWeight = 600, Color = theme.Text } ),
				new( outcome.Length > 0 ? "  " + outcome : "", new Style { Color = theme.Muted } ),
			] )
			{
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( status == "Running" )
		{
			children.Add( new Spacer() );
			children.Add( ChatIcons.BuildSpinner( "wait-spinner" ) );
		}
		return new Container { Key = key, Children = [new Row { AccessibilityRole = AccessibilityRole.Group, AccessibilityName = $"wait tool {status}", AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = children }] };
	}

	/// <summary>Builds the line over one file's diff in an edit of several files: its folder muted, its name bold, and its counts at the end.</summary>
	private static Row BuildDiffTitle( string key, DiffFile file )
	{
		var theme = Theme.Current;
		var name = Path.GetFileName( file.Path );
		var folder = file.Path[..^name.Length];
		var children = new List<Blob>
		{
			new Text( [
				new( folder, new Style { Color = theme.Muted } ),
				new( name, new Style { Color = theme.Text, FontWeight = 600 } ),
			] )
			{
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize,
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			new Spacer(),
		};
		if ( file.Added > 0 )
			children.Add( new Text { Content = $"+{file.Added:N0}", Color = theme.Success, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		if ( file.Removed > 0 )
			children.Add( new Text { Content = $"−{file.Removed:N0}", Color = theme.Error, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		return new Row { Key = "title:" + key, AlignItems = AlignItems.Center, Gap = theme.Spacing, Margin = new Edges( theme.Spacing / 2, 0, 0, 0 ), Children = children };
	}

	/// <summary>Gets when a tool started, its call's message time, and when it ended, its result's time, or null while it runs; null when its call is unknown.</summary>
	private (DateTime Started, DateTime? Ended)? ComputeToolTimes( string id )
	{
		if ( !_toolOwners.TryGetValue( id, out var owner ) || store.GetMessages( conversationId ).FirstOrDefault( message => message.Id == owner ) is not { } call )
			return null;
		return (call.CreatedAt.UtcDateTime, _toolResults.TryGetValue( id, out var result ) ? result.CreatedAt.UtcDateTime : null);
	}

	/// <summary>Builds the small Expand or Collapse pill centered on the rule between a command and its output.</summary>
	private static Container BuildExpandPill( bool isOpen, Action action )
	{
		var theme = Theme.Current;
		var pill = BuildMoreToggle( isOpen ? "Collapse" : "Expand", isOpen, action );
		pill.Padding = new Edges( 1, theme.Spacing * 1.25 );
		pill.Margin = 0;
		// The disclosure keeps itself to the start of its row; the pill sits in the middle of the rule.
		pill.AlignSelf = AlignSelf.Center;
		pill.BackgroundColor = theme.Panel;
		pill.BorderColor = theme.Border.WithAlpha( 0.6 );
		pill.BorderWidth = theme.BorderWidth;
		// Fully round ends make it read as a pill on the rule, not a box.
		pill.BorderRadius = 999;
		return new Container
		{
			Key = "expand",
			Position = PositionType.Absolute,
			Left = 0,
			Right = 0,
			Top = -11,
			AlignItems = AlignItems.Center,
			Children = [pill],
		};
	}

	/// <summary>Reports whether a job status means it still runs: running, pending or queued.</summary>
	private static bool IsJobRunning( string status ) => status is "running" or "pending" or "queued" or "started";
}
