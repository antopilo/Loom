using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Builds Omp-style tool heads with an icon, the agent's stated intent and status, and a highlighted code and output body.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines how many lines of a tool's code show before the rest folds away.</summary>
	private const int PreviewLines = 8;

	/// <summary>Defines how many lines a code preview may run over before it folds; folding fewer would hide almost nothing.</summary>
	private const int PreviewSlack = 3;

	/// <summary>Defines how many of a cut preview's last lines fade out.</summary>
	private const int PreviewFadeLines = 2;

	/// <summary>Holds the tool calls already shown or present when their row was first built; a call not in it enters with motion once.</summary>
	private readonly HashSet<string> _shownTools = new( StringComparer.Ordinal );

	/// <summary>Stores the status mark each tool call showed last, so a change of mark fades the new one in.</summary>
	private readonly Dictionary<string, ToolMark> _toolMarks = new( StringComparer.Ordinal );

	/// <summary>Names the mark at the head of a tool line.</summary>
	private enum ToolMark
	{
		/// <summary>The tool's own icon, while it waits or once it is done.</summary>
		Icon,

		/// <summary>The spinner of a running tool.</summary>
		Spinner,

		/// <summary>The icon in the error color of a failed tool.</summary>
		Failure,
	}

	/// <summary>Gets tool-call identities so paired results render inside their tool rather than twice.</summary>
	private static IEnumerable<string> ComputeToolCallIds( ConversationMessage message )
	{
		if ( message.RichContent is not { ValueKind: JsonValueKind.Object } rich || !rich.TryGetProperty( "content", out var content ) || content.ValueKind != JsonValueKind.Array )
			yield break;
		foreach ( var block in content.EnumerateArray() )
		{
			if ( ReadText( block, "type" ) == "toolCall" )
				yield return ReadText( block, "id" );
		}
	}

	/// <summary>Builds one tool as its module's card, or as an icon and the agent's intent with its command or path; its code and output open on demand.</summary>
	/// <remarks>A tool call that joins a row already on screen fades and rises in once.</remarks>
	private Blob BuildTool( string key, JsonElement block )
	{
		var id = ReadText( block, "id" );
		var isNew = _shownTools.Add( id ) && _isRowLive;
		return Cell.Mount<RevealInput, Reveal>( key, new( BuildToolBody( key, id, block ), isNew ) );
	}

	/// <summary>Builds the tool's card, todo list, or line with its status mark, diffs, images and opened details.</summary>
	private Blob BuildToolBody( string key, string id, JsonElement block )
	{
		var name = ReadText( block, "name", "Tool" );
		var status = ComputeToolStatus( id, out var result );
		var arguments = block.TryGetProperty( "arguments", out var args ) ? args : default;
		if ( name is "todo" or "todo_write" )
			return BuildTodo( key, status, arguments, result );
		if ( BuildToolCard( key, id, name, arguments, status, result ) is { } card )
			return card;

		var expanded = _expanded.Contains( key );
		var diffs = DiffParser.ParseTool( name, arguments, result, store.GetDirectory( conversationId ) );
		var intent = session.Intents.GetValueOrDefault( id ) is { Length: > 0 } started ? started : ReadText( block, "intent", ReadText( arguments, "i" ) );
		if ( name == "bash" )
			return BuildBash( key, id, arguments, status, result, intent );
		if ( name == "grep" )
			return BuildGrep( key, arguments, status, result );
		// A tool on one file reads as its verb and path, the file name bold, as "Edit ~/src/view.cs:104"; others show the agent's intent.
		var path = ComputeToolPath( arguments );
		var toggle = path.Length > 0 && diffs.Count <= 1
			? BuildPathDisclosure( name, path, expanded, () => context.Run( () => UpdateExpanded( key ) ) )
			: BuildDisclosure( intent.Length > 0 ? intent : name, expanded, () => context.Run( () => UpdateExpanded( key ) ),
				diffs.Count > 0 ? string.Join( ", ", diffs.Select( file => Path.GetFileName( file.Path ) ) ) : ComputeToolTarget( arguments ) );
		toggle.AccessibilityName = intent.Length > 0 ? $"{name} · {intent} · {status}" : $"{name} · {status}";
		toggle.Key = "tool-toggle:" + key;
		toggle.FlexShrink = 1;
		var glyph = ChatIcons.ComputeToolGlyph( name );
		var mark = status switch
		{
			"Running" => ToolMark.Spinner,
			"Failed" => ToolMark.Failure,
			_ => ToolMark.Icon,
		};
		// A mark that changed while the line was shown, such as the spinner turning into the icon, fades in over the old one's place.
		var isMarkChanged = _toolMarks.TryGetValue( id, out var previous ) && previous != mark;
		_toolMarks[id] = mark;
		var (markKey, markContent) = mark switch
		{
			ToolMark.Spinner => ("tool-mark-spinner", ChatIcons.BuildSpinner( "tool-spinner" )),
			ToolMark.Failure => ("tool-mark-failure", ChatIcons.BuildIcon( glyph, Theme.Current.Error )),
			_ => ("tool-mark-icon", ChatIcons.BuildIcon( glyph )),
		};
		var head = new List<Blob>
		{
			Cell.Mount<RevealInput, Reveal>( markKey, new( markContent, isMarkChanged ) ),
			toggle,
		};
		if ( name == "eval" && ReadText( arguments, "language" ) is { Length: > 0 } language )
			head.Add( ChatIcons.BuildBadge( language ) );
		// Finished tools stay quiet and running ones spin; only failures need words.
		if ( status == "Failed" )
			head.Add( Cell.Mount<RevealInput, Reveal>( "tool-failed", new( new Text { Content = status, Color = Theme.Current.Error, FontSize = ViewStyle.SmallSize, FlexShrink = 0, }, isMarkChanged ) ) );
		// An edit of one file names it on this line, so its diff card below shows only the code, and its counts sit at this line's end.
		var isOneFile = path.Length > 0 && diffs.Count == 1;
		if ( isOneFile )
		{
			head.Add( new Spacer() );
			if ( diffs[0].Added > 0 )
				head.Add( new Text { Content = $"+{diffs[0].Added:N0}", Color = Theme.Current.Success, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
			if ( diffs[0].Removed > 0 )
				head.Add( new Text { Content = $"−{diffs[0].Removed:N0}", Color = Theme.Current.Error, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		}
		var children = new List<Blob>
		{
			new Row { Key = "tool-head", AlignItems = AlignItems.Center, Gap = Theme.Current.Spacing, Children = head, },
		};
		foreach ( var (index, file) in diffs.Index() )
		{
			var cardKey = key + ":diff:" + index;
			// Several files each get a quiet line naming the file over a card that shows only the code, like the one-file edit above.
			if ( !isOneFile )
				children.Add( BuildDiffTitle( cardKey, file ) );
			children.Add( DiffView.BuildCard( cardKey, file, () => context.Run( () => OpenDiff( file.Path ) ), _expanded.Contains( cardKey ),
				() => context.Run( () => UpdateExpanded( cardKey ) ), header: false ) );
		}
		// A written file shows its content at once, like an edit's diff, rather than behind the fold.
		var (code, codeLanguage) = ComputeToolCode( name, arguments );
		if ( name == "write" && code.Length > 0 )
			children.Add( BuildCodePreview( key + ":code", code, codeLanguage, lineNumbers: true ) );
		// Images a tool returned, such as screenshots, stay visible like diff cards rather than waiting behind the fold.
		children.AddRange( BuildToolImages( id, result ) );
		// An edit's diff already shows what it did, so its text output and raw details would only repeat it.
		if ( expanded && diffs.Count == 0 )
		{
			var details = new List<Blob>();
			// A written file's content already shows above.
			if ( code.Length > 0 && name != "write" )
				details.Add( BuildCodePreview( key + ":code", code, codeLanguage ) );
			else if ( code.Length == 0 && arguments.ValueKind == JsonValueKind.Object && arguments.EnumerateObject().Any( property => property.Name != "i" ) )
				details.Add( MarkdownView.BuildCode( BuildJson( arguments ), "json", showLanguage: false ) );
			if ( result is { } body )
			{
				var text = TerminalText.BuildPlain( ComputeResultText( body ) );
				if ( text.Length > 0 )
					details.Add( BuildCodePreview( key + ":output", text, "text" ) );
				if ( body.ValueKind == JsonValueKind.Object )
				{
					if ( body.TryGetProperty( "content", out var content ) && content.ValueKind == JsonValueKind.Array )
					{
						var index = 0;
						foreach ( var attachment in content.EnumerateArray().Where( item => ReadText( item, "type" ) is not ("text" or "image") ) )
							details.AddRange( BuildRichBlock( key + ":attachment:" + index++, attachment, "toolResult" ) );
					}
					if ( body.TryGetProperty( "details", out var extra ) )
						details.AddRange( BuildDetails( key, extra, includeDiff: diffs.Count == 0 ) );
				}
			}
			children.Add( BuildIndent( details ) );
		}
		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{name} tool {status}",
			Gap = Theme.Current.Spacing,
			Children = children,
		};
	}

	/// <summary>Gets the code a tool runs or writes and its language: a script, a shell command, or a new file's content.</summary>
	private static (string Code, string Language) ComputeToolCode( string name, JsonElement arguments ) => name switch
	{
		"eval" => (ReadText( arguments, "code" ), ReadText( arguments, "language" )),
		"bash" => (ReadText( arguments, "command" ), "bash"),
		"write" => (ReadText( arguments, "content" ), Path.GetExtension( ReadText( arguments, "path" ) ).TrimStart( '.' )),
		_ => ("", ""),
	};

	/// <summary>Builds highlighted code that shows its first lines, fading out, until the reader opens the rest.</summary>
	private Blob BuildCodePreview( string key, string code, string language, bool lineNumbers = false )
	{
		var lines = code.Replace( "\r\n", "\n" ).TrimEnd( '\n' ).Split( '\n' );
		if ( lines.Length <= PreviewLines + PreviewSlack )
			return MarkdownView.BuildCode( code, language, showLanguage: false, lineNumbers: lineNumbers );

		var expanded = _expanded.Contains( key );
		var toggle = BuildDisclosure( expanded ? "Show less" : $"{lines.Length - PreviewLines:N0} more lines · Show all", expanded, () => context.Run( () => UpdateExpanded( key ) ) );
		toggle.Key = "preview-toggle";
		return new Container
		{
			Key = key,
			Gap = Theme.Current.Spacing / 2,
			Children =
			[
				expanded
					? MarkdownView.BuildCode( code, language, showLanguage: false, lineNumbers: lineNumbers )
					: MarkdownView.BuildCode( string.Join( '\n', lines.Take( PreviewLines ) ), language, showLanguage: false, fadeLines: PreviewFadeLines, lineNumbers: lineNumbers ),
				toggle,
			],
		};
	}

	/// <summary>Opens the Git panel beside this chat on a changed file's diff.</summary>
	/// <param name="path">The file's path, relative to the chat folder or full.</param>
	private void OpenDiff( string path )
	{
		var directory = store.GetDirectory( conversationId );
		var focus = Path.GetFullPath( Path.Combine( directory, path ) );
		if ( context.App.Modules.Any( module => module.Id == "git" ) )
			context.App.GetModule<GitModule>().OpenPanel( context.App, context.Id, directory, focus );
	}

	/// <summary>Gets the command, file, query, or description that identifies a tool's work.</summary>
	private static string ComputeToolTarget( JsonElement arguments )
	{
		foreach ( var property in new[] { "command", "path", "file_path", "filePath", "query", "pattern", "description" } )
		{
			var value = ReadText( arguments, property );
			if ( value.Length > 0 )
				return value.Split( '\n' )[0];
		}
		return "";
	}

	/// <summary>Gets the file path a tool works on, with any line selector, or empty when it has none.</summary>
	private static string ComputeToolPath( JsonElement arguments )
	{
		foreach ( var property in new[] { "path", "file_path", "filePath" } )
		{
			var value = ReadText( arguments, property );
			if ( value.Length > 0 && !value.Contains( '\n' ) )
				return value;
		}
		return "";
	}

	/// <summary>Builds a tool head as its verb, then the path's folder, its file name in bold, and any line selector after it.</summary>
	private static Button BuildPathDisclosure( string name, string path, bool expanded, Action action )
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		if ( home.Length > 0 && path.StartsWith( home, StringComparison.OrdinalIgnoreCase ) )
			path = "~" + path[home.Length..];
		var slash = path.LastIndexOfAny( ['/', '\\'] ) + 1;
		var file = path[slash..];
		var colon = file.IndexOf( ':' );
		var (fileName, selector) = colon > 0 ? (file[..colon], file[colon..]) : (file, "");
		var verb = name.Length > 0 ? char.ToUpperInvariant( name[0] ) + name[1..] : name;
		return BuildDisclosure( verb, expanded, action, path[..slash], fileName, selector );
	}
}
