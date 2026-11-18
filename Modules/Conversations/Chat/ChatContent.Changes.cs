using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Chat;

/// <summary>Describes one file a turn changed, with the lines its edits added and removed.</summary>
/// <param name="Path">The file path, relative to the chat folder when it lies inside it.</param>
/// <param name="Added">The lines added over the turn.</param>
/// <param name="Removed">The lines removed over the turn.</param>
internal readonly record struct ChangedFile( string Path, int Added, int Removed );

/// <summary>Ends each finished turn that edited or wrote files with a card listing them, which opens the folder's diff or one file's.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines how many changed files a card lists before the rest fold away.</summary>
	private const int ChangedFilesShown = 3;

	/// <summary>Defines the size of the card's change icon in logical pixels.</summary>
	private const int ChangesIconSize = 32;

	/// <summary>Caches the files of each finished file-changing tool call by its result, so streaming rebuilds do not parse every diff again.</summary>
	private readonly Dictionary<string, (ConversationMessage Result, ChangedFile[] Files)> _toolChanges = new( StringComparer.Ordinal );

	/// <summary>Adds the files a message's finished edit and write calls changed to its turn's totals; failed calls change nothing.</summary>
	private void AddTurnChanges( ConversationMessage message, Dictionary<string, ChangedFile> changes )
	{
		if ( message.Role != "assistant" )
			return;

		foreach ( var block in ReadBlocks( TranscriptPart.Whole( message ) ) )
		{
			var name = ReadText( block, "name" );
			if ( ReadText( block, "type" ) != "toolCall" || name is not ("edit" or "write" or "ast_edit") )
				continue;

			var id = ReadText( block, "id" );
			if ( !_toolResults.TryGetValue( id, out var result ) || result.RichContent is not { } body )
				continue;
			if ( !_toolChanges.TryGetValue( id, out var parsed ) || !ReferenceEquals( parsed.Result, result ) )
			{
				var arguments = block.TryGetProperty( "arguments", out var args ) ? args : default;
				ChangedFile[] files = [.. DiffParser.ParseTool( name, arguments, body, store.GetDirectory( conversationId ) ).Select( file => new ChangedFile( file.Path, file.Added, file.Removed ) )];
				_toolChanges[id] = parsed = (result, files);
			}

			foreach ( var file in parsed.Files )
				changes[file.Path] = changes.TryGetValue( file.Path, out var total ) ? total with { Added = total.Added + file.Added, Removed = total.Removed + file.Removed } : file;
		}
	}

	/// <summary>Builds a turn's change card: how many files it edited and the lines changed, its first files, and the rest on demand.</summary>
	private Container BuildChanges( string key, ChangedFile[] files )
	{
		var theme = Theme.Current;
		var expanded = _expanded.Contains( key );
		var added = files.Sum( file => file.Added );
		var removed = files.Sum( file => file.Removed );
		var title = "Edited " + Count( files.Length, "file", "files" );
		// Without the Git module there is no diff to open. The Git panel opens on the turn's first file, in the working tree's changes, not on the graph.
		var view = theme.BuildButton( "View changes", () => context.Run( () => OpenDiff( files[0].Path ) ), files.Length > 0 && context.App.Modules.Any( module => module.Id == "git" ) );
		view.Key = "changes-view";
		view.FlexShrink = 0;
		view.BorderWidth = theme.BorderWidth;
		view.BorderColor = theme.Border;
		var children = new List<Blob>
		{
			new Row
			{
				Key = "changes-head",
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing * 1.5,
				Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 1.5 ),
				Children =
				[
					BuildChangesIcon(),
					new Container
					{
						FlexGrow = 1,
						FlexShrink = 1,
						MinWidth = 0,
						Gap = 2,
						Children =
						[
							new Text { Content = title, FontWeight = AuthorWeight, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, },
							new Row { Gap = theme.Spacing / 2, Children = BuildLineCounts( added, removed, ViewStyle.SmallSize ), },
						],
					},
					view,
				],
			},
		};
		foreach ( var file in expanded ? files : files.Take( ChangedFilesShown ) )
			children.Add( BuildChangedFile( file ) );
		if ( files.Length > ChangedFilesShown )
		{
			var more = BuildDisclosure( expanded ? "Show fewer files" : $"Show {Count( files.Length - ChangedFilesShown, "more file", "more files" )}", expanded, () => context.Run( () => UpdateExpanded( key ) ) );
			more.Key = "changes-more";
			more.Margin = new Edges( 0 );
			children.Add( new Container
			{
				Key = "changes-more-row",
				Padding = new Edges( theme.Spacing, theme.Spacing ),
				BorderColor = theme.Border,
				BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
				// Opening or closing the list moves the disclosure, which glides to its new place.
				LayoutTransition = new( ChatMotion.SlowMs, ChatMotion.Ease ),
				Children = [more],
			} );
		}

		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{title}, {added:N0} lines added, {removed:N0} removed",
			Overflow = Overflow.Hidden,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Children = children,
		};
	}

	/// <summary>Builds one changed file's line: its folder, name, and lines changed; clicking it opens the diff on that file.</summary>
	private Button BuildChangedFile( ChangedFile file )
	{
		var theme = Theme.Current;
		var name = Path.GetFileName( file.Path );
		var folder = file.Path[..^name.Length];
		void Open() => context.Run( () => OpenDiff( file.Path ) );
		return new Button
		{
			Key = "changes-file:" + file.Path,
			AccessibilityName = $"Open diff of {file.Path}, {file.Added:N0} lines added, {file.Removed:N0} removed",
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing, theme.Spacing * 1.5 ),
			BorderColor = theme.Border,
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			Cursor = Cursor.Pointer,
			Focusable = true,
			OnClick = Open,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Open ),
				new( Key.Space, KeyModifiers.None, Open ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				new Text( [new TextSpan( folder, new Style { Color = theme.Muted } ), new TextSpan( name, null! )] )
				{
					Color = theme.Text,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				new Spacer(),
				.. BuildLineCounts( file.Added, file.Removed, ViewStyle.SmallSize ),
			],
		};
	}

	/// <summary>Builds the lines added and removed, <c>+12 −3</c>, in their change colors.</summary>
	private static List<Blob> BuildLineCounts( int added, int removed, int size ) =>
	[
		new Text { Content = $"+{added:N0}", Color = Theme.Current.Success, FontSize = size, FlexShrink = 0, TextWrap = TextWrap.NoWrap, },
		new Text { Content = $"−{removed:N0}", Color = Theme.Current.Error, FontSize = size, FlexShrink = 0, TextWrap = TextWrap.NoWrap, },
	];

	/// <summary>Builds the card's icon: a plus-minus sign in a rounded square.</summary>
	private static Container BuildChangesIcon() => new()
	{
		Width = ChangesIconSize,
		Height = ChangesIconSize,
		FlexShrink = 0,
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		BorderRadius = Theme.Current.Radius * 1.5,
		BackgroundColor = Theme.Current.Hover,
		Children = [new Text { Content = "±", FontSize = ChatStyle.BodySize + 2, Color = Theme.Current.Text, }],
	};
}
