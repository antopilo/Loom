using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Completes <c>@file</c> mentions in the prompt from the chat folder's files, and colors mentions and <c>#N</c> image references in it.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the most files the mention list reads from a folder, so a huge tree cannot stall it.</summary>
	private const int MaxMentionFiles = 20000;

	/// <summary>Defines the most matches the mention list shows.</summary>
	private const int MaxMentionMatches = 50;

	/// <summary>Lists folder names the mention list skips: build output, dependencies and version control.</summary>
	private static readonly HashSet<string> _skippedFolders = new( [".git", ".vs", ".idea", "bin", "obj", "node_modules", "artifacts", "dist", "target", "__pycache__"], StringComparer.OrdinalIgnoreCase );

	/// <summary>Lists the file types an attachment counts as an image, numbered <c>#1</c>, <c>#2</c> in the prompt.</summary>
	private static readonly string[] _imageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

	/// <summary>Holds the chat folder's files as paths relative to it with forward slashes, or null before they are read.</summary>
	private string[]? _mentionFiles;

	/// <summary>Stores the folder <see cref="_mentionFiles"/> was read from.</summary>
	private string? _mentionRoot;

	/// <summary>Stores the mention being typed: its text after the <c>@</c>, or null when none is.</summary>
	private string? _mentionQuery;

	/// <summary>Stores the offset of the typed mention's <c>@</c>.</summary>
	private int _mentionStart;

	/// <summary>Stores the selected match in the mention list.</summary>
	private int _selectedMention;

	/// <summary>Stores the prompt text whose mention list Escape closed, so it stays closed until the prompt changes.</summary>
	private string? _dismissedMentionText;

	/// <summary>Scrolls the mention list.</summary>
	private readonly ElementHandle _mentionList = new();

	/// <summary>Colors mentions and image references in the prompt; created with the prompt editor.</summary>
	private TextPresentationLayer? _promptLayer;

	/// <summary>Stores the prompt text and image count the layer was colored for.</summary>
	private (string Text, int Images)? _promptLayerState;

	/// <summary>Reports whether a path is an image, which the prompt refers to by number.</summary>
	internal static bool IsImagePath( string path ) => _imageExtensions.Contains( Path.GetExtension( path ), StringComparer.OrdinalIgnoreCase );

	/// <summary>Gets an attachment's image number, <c>#1</c> for the first image in the list, or 0 for a file that is not an attached image.</summary>
	internal static int ComputeImageNumber( IReadOnlyList<string> attachments, string path )
	{
		var number = 0;
		foreach ( var attachment in attachments )
		{
			if ( !IsImagePath( attachment ) )
				continue;
			number++;
			if ( string.Equals( attachment, path, StringComparison.OrdinalIgnoreCase ) )
				return number;
		}
		return 0;
	}

	/// <summary>Returns the mention typed before the caret: its <c>@</c> offset and the text after it, when the <c>@</c> starts a word and is not a quoted mention.</summary>
	internal static (int Start, string Query)? ComputeMentionQuery( string text, int caret )
	{
		caret = Math.Clamp( caret, 0, text.Length );
		var start = caret;
		while ( start > 0 && !char.IsWhiteSpace( text[start - 1] ) )
			start--;
		if ( start >= caret || text[start] != '@' || (start + 1 < text.Length && text[start + 1] == '"') )
			return null;
		return (start, text[(start + 1)..caret]);
	}

	/// <summary>Lists the files matching a query: names that start with it, then names that contain it, then paths that contain it, shorter first.</summary>
	internal static List<string> ComputeMentionMatches( IReadOnlyList<string> files, string query )
	{
		if ( query.Length == 0 )
			return [.. files.OrderBy( file => file.Count( c => c == '/' ) ).ThenBy( file => file, StringComparer.OrdinalIgnoreCase ).Take( MaxMentionMatches )];

		var ranked = new List<(int Rank, string File)>();
		foreach ( var file in files )
		{
			var name = file[(file.LastIndexOf( '/' ) + 1)..];
			var rank = name.StartsWith( query, StringComparison.OrdinalIgnoreCase ) ? 0
				: name.Contains( query, StringComparison.OrdinalIgnoreCase ) ? 1
				: file.Contains( query, StringComparison.OrdinalIgnoreCase ) ? 2 : -1;
			if ( rank >= 0 )
				ranked.Add( (rank, file) );
		}
		return [.. ranked.OrderBy( item => item.Rank ).ThenBy( item => item.File.Length ).ThenBy( item => item.File, StringComparer.OrdinalIgnoreCase )
			.Take( MaxMentionMatches ).Select( item => item.File )];
	}

	/// <summary>Finds the prompt's <c>@file</c> mentions and its <c>#N</c> references to attached images, by offset and length.</summary>
	internal static List<(int Start, int Length, bool IsImage)> ComputePromptMarks( string text, int images )
	{
		var marks = new List<(int Start, int Length, bool IsImage)>();
		foreach ( Match match in PromptMark().Matches( text ) )
		{
			if ( match.Groups["file"].Success )
				marks.Add( (match.Index, match.Length, false) );
			else if ( int.TryParse( match.Groups["image"].Value, out var number ) && number >= 1 && number <= images )
				marks.Add( (match.Index, match.Length, true) );
		}
		return marks;
	}

	/// <summary>Matches an <c>@file</c> mention, quoted or not, or a <c>#N</c> reference, each starting a word.</summary>
	[GeneratedRegex( "(?<=^|\\s)(?:(?<file>@(?:\"[^\"\\n]+\"|[^\\s\"]+))|#(?<image>\\d+)\\b)" )]
	private static partial Regex PromptMark();

	/// <summary>Colors the prompt's mentions and image references again when its text or the attached images changed, and gets the layer.</summary>
	private TextPresentationLayer UpdatePromptLayer()
	{
		_promptLayer ??= new TextPresentationLayer( _composer.Document );
		var text = _composer.Document.GetText();
		var images = _attachments.Count( IsImagePath );
		if ( _promptLayerState == (text, images) )
			return _promptLayer;

		_promptLayerState = (text, images);
		var theme = Theme.Current;
		var spans = new List<TextStyleSpan>();
		foreach ( var (start, length, isImage) in ComputePromptMarks( text, images ) )
			spans.Add( new( $"mark:{start}", new( start, length ), new Style { Color = isImage ? theme.AnsiMagenta : theme.AnsiBlue } ) );
		_promptLayer.ReplaceStyles( spans );
		return _promptLayer;
	}

	/// <summary>Reads the mention typed before the caret, resetting the selection when it changes and reading the folder's files when the list opens;
	/// returns whether it changed.</summary>
	private bool UpdateMentionQuery()
	{
		var text = _composer.Document.GetText();
		if ( text != _dismissedMentionText )
			_dismissedMentionText = null;
		var found = _dismissedMentionText is null ? ComputeMentionQuery( text, _composer.Selection.Active.Offset ) : null;
		var query = found?.Query;
		if ( query == _mentionQuery && (found is null || found.Value.Start == _mentionStart) )
			return false;

		if ( _mentionQuery is null && query is not null )
			UpdateMentionFiles();
		_mentionQuery = query;
		_mentionStart = found?.Start ?? 0;
		_selectedMention = 0;
		_mentionList.ScrollTo( null, 0 );
		return true;
	}

	/// <summary>Reads the chat folder's files in the background, skipping build output and dependencies, and rebuilds once they are in.</summary>
	private void UpdateMentionFiles()
	{
		var root = store.GetDirectory( conversationId );
		if ( string.IsNullOrEmpty( root ) || !Directory.Exists( root ) )
		{
			(_mentionRoot, _mentionFiles) = (root, []);
			return;
		}
		_ = Task.Run( () =>
		{
			var files = ReadMentionFiles( root );
			context.Post( () =>
			{
				(_mentionRoot, _mentionFiles) = (root, files);
				if ( _mentionQuery is not null )
					MarkChanged();
			} );
		} );
	}

	/// <summary>Lists a folder's files as relative paths with forward slashes, up to <see cref="MaxMentionFiles"/>; unreadable folders are skipped.</summary>
	private static string[] ReadMentionFiles( string root )
	{
		var files = new List<string>();
		var folders = new Stack<string>();
		folders.Push( root );
		while ( folders.Count > 0 && files.Count < MaxMentionFiles )
		{
			var folder = folders.Pop();
			try
			{
				foreach ( var file in Directory.EnumerateFiles( folder ) )
				{
					files.Add( Path.GetRelativePath( root, file ).Replace( '\\', '/' ) );
					if ( files.Count >= MaxMentionFiles )
						break;
				}
				foreach ( var child in Directory.EnumerateDirectories( folder ) )
				{
					if ( !_skippedFolders.Contains( Path.GetFileName( child ) ) )
						folders.Push( child );
				}
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
			{
			}
		}
		return [.. files];
	}

	/// <summary>Gets the matches for the open mention list, or none while the folder is read.</summary>
	private List<string> ReadMentionMatches() => _mentionQuery is { } query && _mentionFiles is { } files ? ComputeMentionMatches( files, query ) : [];

	/// <summary>Builds the card of matching files above the prompt, scrolling past <see cref="VisibleCommands"/> rows, with a key legend or loading state.</summary>
	private Blob BuildMentions( string query )
	{
		var theme = Theme.Current;
		var matches = ReadMentionMatches();
		_selectedMention = Math.Clamp( _selectedMention, 0, Math.Max( 0, matches.Count - 1 ) );
		var children = new List<Blob>();
		if ( matches.Count > 0 )
		{
			children.Add( new Container
			{
				Key = "conversation-mentions-list",
				Handle = _mentionList,
				Height = Math.Min( matches.Count, VisibleCommands ) * CommandRowHeight,
				FlexShrink = 0,
				OverflowY = Overflow.Scroll,
				ScrollbarY = ViewStyle.GetScrollbar(),
				Children = [.. matches.Select( ( file, index ) => BuildMentionRow( file, index == _selectedMention ) )],
			} );
		}
		var status = _mentionFiles is null || _mentionRoot != store.GetDirectory( conversationId ) ? "Reading the folder's files…"
			: matches.Count == 0 ? $"No file matches @{query}."
			: "↑ ↓ select   ·   Tab or Enter insert   ·   Esc close";
		children.Add( new Text
		{
			Key = "conversation-mentions-status",
			Content = status,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			FontSize = ViewStyle.SmallSize,
			Color = theme.Muted,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
			MinWidth = 0,
		} );
		var card = new Container
		{
			Key = "conversation-mentions",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Files",
			Padding = theme.Spacing / 2,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = ChatStyle.BubbleRadius,
			BackgroundColor = theme.Raised,
			Children = children,
		};
		return Cell.Mount<RevealInput, Reveal>( "conversation-mentions-reveal", new( card, true ) );
	}

	/// <summary>Builds one file row: its name, then its folder muted, highlighted when selected.</summary>
	private Button BuildMentionRow( string file, bool isSelected )
	{
		var theme = Theme.Current;
		var slash = file.LastIndexOf( '/' ) + 1;
		return new Button
		{
			Key = "mention:" + file,
			Height = CommandRowHeight,
			FlexShrink = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 0, theme.Spacing ),
			BorderRadius = theme.Radius,
			BackgroundColor = isSelected ? theme.Hover : theme.Raised,
			TransitionMs = ChatMotion.FastMs,
			TransitionEasing = ChatMotion.Ease,
			TransitionProperties = [TransitionProperty.BackgroundColor],
			Color = theme.Text,
			AccessibilityName = file,
			OnClick = () => context.Run( () => UpdateMentionAccept( file ) ),
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				new Text { Content = file[slash..], FlexShrink = 0, TextWrap = TextWrap.NoWrap },
				new Text
				{
					Content = file[..slash],
					FlexShrink = 1,
					MinWidth = 0,
					FontSize = ViewStyle.SmallSize,
					Color = theme.Muted,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
			],
		};
	}

	/// <summary>Moves the keyboard selection through the file matches, wrapping at either end, and scrolls it into view.</summary>
	private void UpdateMentionSelection( int step )
	{
		var count = ReadMentionMatches().Count;
		if ( count == 0 )
			return;

		_selectedMention = (Math.Clamp( _selectedMention, 0, count - 1 ) + step + count) % count;
		var top = _selectedMention * CommandRowHeight;
		var viewport = Math.Min( count, VisibleCommands ) * CommandRowHeight;
		var offset = _mentionList.ScrollOffset.Y;
		if ( top < offset )
			_mentionList.ScrollTo( null, top );
		else if ( top + CommandRowHeight > offset + viewport )
			_mentionList.ScrollTo( null, top + CommandRowHeight - viewport );
		MarkChanged();
	}

	/// <summary>Inserts the selected file; returns false when nothing matches, so Enter sends the prompt as typed.</summary>
	private bool UpdateMentionSelected()
	{
		var matches = ReadMentionMatches();
		if ( matches.Count == 0 )
			return false;

		UpdateMentionAccept( matches[Math.Clamp( _selectedMention, 0, matches.Count - 1 )] );
		return true;
	}

	/// <summary>Replaces the typed mention with the file's path, quoted when it has spaces, followed by a space.</summary>
	private void UpdateMentionAccept( string file )
	{
		var caret = Math.Max( _mentionStart, _composer.Selection.Active.Offset );
		var mention = file.Contains( ' ' ) ? $"@\"{file}\" " : $"@{file} ";
		_composer.Selection = new( new( _mentionStart, TextAffinity.Downstream ), new( caret, TextAffinity.Downstream ) );
		_composer.Execute( new TextCommand( TextCommandKind.Insert, mention, false ) );
		_composer.Focus();
		UpdateMentionQuery();
		UpdatePromptLayer();
		MarkChanged();
	}

	/// <summary>Closes the file list until the prompt changes.</summary>
	private void UpdateMentionDismiss()
	{
		_dismissedMentionText = _composer.Document.GetText();
		UpdateMentionQuery();
		MarkChanged();
	}
}
