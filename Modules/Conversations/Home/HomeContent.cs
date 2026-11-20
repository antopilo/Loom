using Goo;
using Lokn.Core;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Shows Home, the empty state of a conversation panel: the wordmark, cards for the current host's recent projects, and a prompt bar that starts a chat in this panel.</summary>
internal sealed class HomeContent( PanelContext context, ConversationsModule module ) : PanelContent
{
	/// <summary>Defines how many recent cards Home shows.</summary>
	private const int CardLimit = 6;

	/// <summary>Defines a card's width in logical pixels; four cards and their gaps fit the column.</summary>
	private const int CardWidth = 158;

	/// <summary>Defines a card's height in logical pixels.</summary>
	private const int CardHeight = 92;

	/// <summary>Defines the widest the cards and the prompt bar grow, in logical pixels.</summary>
	private const int ColumnWidth = 680;

	/// <summary>Defines the prompt bar's height in logical pixels.</summary>
	private const int PromptHeight = 46;

	/// <summary>Defines the space between the wordmark and the cards, in logical pixels.</summary>
	private const int LogoGap = 44;

	/// <summary>Defines the outer inset in logical pixels.</summary>
	private const int Inset = 32;

	/// <summary>Defines a card name's size in logical pixels.</summary>
	private const int NameSize = 14;

	/// <summary>Defines small detail text's size in logical pixels.</summary>
	private const int DetailSize = 11;

	/// <summary>Defines a project icon's width and height in logical pixels.</summary>
	private const int ProjectIconSize = 22;

	/// <summary>Retains the prompt being typed.</summary>
	private readonly State<string> _prompt = new( "" );

	/// <summary>Reports a failure, such as a folder chooser that could not open, or empty.</summary>
	private readonly State<string> _status = new( "" );

	/// <summary>Builds the centered wordmark and cards with the prompt bar along the bottom.</summary>
	public override Blob Build()
	{
		var host = module.Hosts.Current;
		var children = new List<Blob>
		{
			new Spacer(),
			Cell.Mount( "home-logo", () => new HomeLogo() ),
			BuildCards( context, module, host, error => _status.Value = error ),
		};
		// Project-trusted modules load on This PC alone.
		if ( host.IsLocal && module.Projects.BuildModulePrompt( context ) is { } modules )
			children.Add( modules );
		if ( host.IsLocal && module.IsWhatsNewShown )
			children.Add( BuildWhatsNew() );
		if ( _status.Value.Length > 0 )
			children.Add( BuildDetail( _status.Value, "home-status" ) );

		children.Add( new Spacer() );
		children.Add( BuildPrompt() );
		children.Add( BuildDetail( "Enter starts a chat  ·  Ctrl+Shift+H comes back here", "home-hint" ) );

		return new Column
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Home",
			FlexGrow = 1,
			MinHeight = 0,
			AlignItems = AlignItems.Center,
			Padding = Inset,
			Gap = Theme.Current.Spacing * 2,
			Color = Theme.Current.Text,
			OverflowY = Overflow.Hidden,
			Children = children,
		};
	}

	/// <summary>Formats how long ago something happened, in the largest whole unit.</summary>
	internal static string FormatAge( DateTimeOffset date, DateTimeOffset now )
	{
		var age = now - date;
		return age.TotalMinutes < 1 ? "just now"
			: age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
			: age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
			: age.TotalDays < 2 ? "yesterday"
			: age.TotalDays < 60 ? $"{(int)age.TotalDays} days ago"
			: date.ToLocalTime().ToString( "MMM d, yyyy" );
	}

	/// <summary>Shortens a directory under the user's profile to start with ~.</summary>
	internal static string FormatDirectory( string directory )
	{
		var profile = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		return profile.Length > 0 && directory.StartsWith( profile, StringComparison.OrdinalIgnoreCase )
			? "~" + directory[profile.Length..]
			: directory;
	}

	/// <summary>Builds a host's recent cards and the card that opens one of its folders as a project; an empty chat shows the same cards.</summary>
	/// <param name="context">The panel the cards are in.</param>
	/// <param name="module">The module whose projects the cards open.</param>
	/// <param name="host">The host whose projects and chats the cards open.</param>
	/// <param name="report">Shows a failure of the folder chooser in the panel.</param>
	internal static Blob BuildCards( PanelContext context, ConversationsModule module, ChatHost host, Action<string> report )
	{
		var now = DateTimeOffset.UtcNow;
		var cards = module.ComputeEntries( context.App, host )
			.Take( CardLimit )
			.Select( entry => (Blob)BuildCard( entry.Key, entry.Title, entry.Project is null ? entry.Detail : FormatDirectory( entry.Detail ),
				FormatUse( entry, now ), $"Open {entry.Title}", () => context.Run( () => module.Open( context.App, entry ) ),
				entry.Project is { } project ? module.Projects.GetIcon( project, host.Id ) : null ) )
			.Append( BuildCard( "open-folder", "Open folder…", host.IsLocal ? "Add a project" : $"Add a project on {host.Name}", "", "Open a folder as a project",
				() => context.Run( () => ChooseFolder( context, module, host, report ) ), icon: null, isQuiet: true ) )
			.ToList();

		return new Row
		{
			Key = "home-cards",
			Margin = new Edges( LogoGap, 0, 0, 0 ),
			Width = Length.Percent( 100 ),
			MaxWidth = ColumnWidth,
			FlexWrap = FlexWrap.Wrap,
			JustifyContent = JustifyContent.Center,
			Gap = Theme.Current.Spacing * 1.5,
			Children = cards,
		};
	}

	/// <summary>Describes when a card was last used and how many chats it holds.</summary>
	private static string FormatUse( HomeEntry entry, DateTimeOffset now )
	{
		var chats = entry.Conversations == 1 ? "1 chat" : $"{entry.Conversations} chats";
		return entry.LastUsed is { } used ? $"{FormatAge( used, now )}  ·  {chats}" : chats;
	}

	/// <summary>Builds one quiet card with an optional project icon, a name, a detail line, and a footnote.</summary>
	private static Button BuildCard( string key, string title, string detail, string footnote, string name, Action open, ImageSourceProvider? icon, bool isQuiet = false )
	{
		var theme = Theme.Current;
		var heading = new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinWidth = 0,
			Children =
			[
				.. icon is null ? Array.Empty<Blob>() : [ProjectsModule.BuildIcon( icon, title, ProjectIconSize )],
				new Text
				{
					Content = title,
					FontSize = NameSize,
					FontWeight = 600,
					Color = isQuiet ? theme.Muted : theme.Text,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
					MinWidth = 0,
					FlexShrink = 1,
				},
			],
		};
		var lines = new List<Blob>
		{
			heading,
			new Text
			{
				Content = detail,
				FontSize = DetailSize,
				Color = theme.Muted,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
				MinWidth = 0,
			},
			new Spacer(),
		};
		if ( footnote.Length > 0 )
			lines.Add( new Text
			{
				Content = footnote,
				FontSize = DetailSize,
				Color = theme.Inactive,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
				MinWidth = 0,
			} );

		return new Button
		{
			Key = key,
			Width = CardWidth,
			Height = CardHeight,
			Padding = new Edges( 12, 14 ),
			Gap = 3,
			FlexDirection = FlexDirection.Column,
			AlignItems = AlignItems.Stretch,
			BorderRadius = theme.Radius + 3,
			BorderWidth = theme.BorderWidth,
			BorderColor = isQuiet ? theme.Border : Color.Transparent,
			BackgroundColor = isQuiet ? Color.Transparent : theme.Raised,
			TransitionMs = 150,
			TransitionProperties = [TransitionProperty.BackgroundColor, TransitionProperty.BorderColor],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BorderColor = theme.Accent },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = name,
			OnClick = open,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, open ),
				new( Key.Space, KeyModifiers.None, open ),
			],
			Children = lines,
		};
	}

	/// <summary>Builds the rounded prompt bar; Enter starts a chat outside projects with what was typed.</summary>
	private Blob BuildPrompt()
	{
		var theme = Theme.Current;
		return new TextEntry
		{
			Key = "home-prompt",
			AutoFocus = true,
			Value = _prompt.Value,
			Controlled = true,
			Placeholder = "Ask anything…",
			AccessibilityName = "Start a chat",
			Width = Length.Percent( 100 ),
			MaxWidth = ColumnWidth,
			Height = PromptHeight,
			FlexShrink = 0,
			Padding = new Edges( 0, PromptHeight / 2 - 4 ),
			FontSize = theme.FontSize + 1,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = PromptHeight / 2.0,
			FocusStyle = new Style { BorderColor = theme.Accent },
			OnChange = value => context.Run( () => _prompt.Value = value ),
			OnSubmit = value => context.Run( () => UpdateSubmit( value ) ),
		};
	}

	/// <summary>Builds a small muted line.</summary>
	private static Text BuildDetail( string text, string key ) => new()
	{
		Key = key,
		Content = text,
		FontSize = DetailSize,
		Color = Theme.Current.Inactive,
		TextAlign = TextAlign.Center,
	};

	/// <summary>Builds the one-time What's new card after an update to 0.3.0: chats that keep running in the background, paired devices in preview, and
	/// that nothing changed unless the user turns it on; <b>Turn on</b> runs This PC's chats in the daemon, <b>Not now</b> hides the card for good.</summary>
	private Blob BuildWhatsNew()
	{
		var theme = Theme.Current;
		Text Line( string key, string text ) => new() { Key = key, Content = text, Color = theme.Muted, TextWrap = TextWrap.Wrap, MinWidth = 0 };
		var buttons = new List<Blob>();
		if ( module.CanRunInBackground )
			buttons.Add( theme.BuildButton( "Turn on", () => context.Run( () => module.TurnOnBackground( context.App ) ) ) );
		buttons.Add( theme.BuildButton( "Not now", () => context.Run( module.DismissWhatsNew ) ) );
		return new Container
		{
			Key = "home-whats-new",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"What's new in Loom {ConversationsModule.WhatsNewVersion}",
			Width = Length.Percent( 100 ),
			MaxWidth = ColumnWidth,
			Padding = new Edges( 12, 14 ),
			Gap = theme.Spacing,
			BorderRadius = theme.Radius + 3,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BackgroundColor = theme.Raised,
			Children =
			[
				new Text { Key = "title", Content = $"What's new in Loom {ConversationsModule.WhatsNewVersion}", FontSize = NameSize, FontWeight = 600 },
				Line( "background", "Chats can keep running in the background. Turn it on and your agents keep working after you close Loom." ),
				Line( "devices", "You can also use this PC's chats from your laptop or phone. Paired devices are a preview; find them in Remote access." ),
				Line( "unchanged", "Nothing changed unless you turn it on. Your chats, projects and settings are as you left them, and Loom kept a backup in Settings." ),
				new Row { Key = "buttons", Gap = theme.Spacing, JustifyContent = JustifyContent.FlexEnd, Children = buttons },
			],
		};
	}

	/// <summary>Starts a chat with the typed prompt in this panel on the current host, in the project of the current session when there is one.</summary>
	private void UpdateSubmit( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return;

		_prompt.Value = "";
		_status.Value = "";
		module.StartConversation( context.App, module.Hosts.Current, value.Trim(), module.ComputeSessionProject( context.App ), context.Id );
	}

	/// <summary>Asks for a folder of the host, then opens its project, creating it when it is new: the native folder chooser over the shell window on This PC,
	/// a typed path checked on the host for a paired one.</summary>
	private static void ChooseFolder( PanelContext context, ConversationsModule module, ChatHost host, Action<string> report )
	{
		if ( !host.IsLocal )
			HostFolderPrompt.Show( context, host.Host, "Open a project folder", null, directory => module.OpenFolder( context.App, host, directory ) );
		else if ( context.App.Window is { } window )
			_ = ChooseFolderAsync( window, context, module, report );
	}

	/// <summary>Opens the chosen folder's project on This PC once the owner-modal chooser closes.</summary>
	private static async Task ChooseFolderAsync( Window window, PanelContext context, ConversationsModule module, Action<string> report )
	{
		var result = await window.ShowFileDialogAsync( FileDialogKind.Folder, new FileDialogOptions
		{
			Title = "Open a project folder",
		} );
		context.Post( () =>
		{
			if ( result.Status == FileDialogStatus.Success && result.Paths.FirstOrDefault() is { } directory )
				module.OpenFolder( context.App, module.Hosts.Local, directory );
			else if ( result.Status != FileDialogStatus.Cancelled )
				report( $"Could not open the folder chooser: {result.Error}" );
		} );
	}
}
