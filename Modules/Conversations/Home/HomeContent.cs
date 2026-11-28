using Goo;
using Lokn.Core;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Shows Home, the empty state of a conversation panel: the day's painting under Loom's mark, the date and time along the margins, and a prompt bar
/// that starts a chat in this panel.</summary>
internal sealed class HomeContent( PanelContext context, ConversationsModule module ) : PanelContent
{
	/// <summary>Defines the widest the prompt bar and the notes grow, in logical pixels.</summary>
	private const int ColumnWidth = HomeFront.ColumnWidth;

	/// <summary>Defines the prompt bar's least height in logical pixels.</summary>
	private const int PromptHeight = 46;

	/// <summary>Defines the painting's height on Home, in logical pixels.</summary>
	private const int PaintingHeight = 330;

	/// <summary>Defines the outer inset in logical pixels.</summary>
	private const int Inset = 32;

	/// <summary>Defines small detail text's size in logical pixels.</summary>
	private const int DetailSize = 11;

	/// <summary>Retains the prompt being typed.</summary>
	private readonly State<string> _prompt = new( "" );

	/// <summary>Holds the painting, the kicker line and the margins.</summary>
	private readonly HomeFront _front = new( context );

	/// <summary>Builds the painting centered, the date and time along the margins, and the prompt bar along the bottom.</summary>
	public override Blob Build()
	{
		var host = module.Hosts.Current;
		var content = new List<Blob> { new Spacer(), _front.Build( PaintingHeight ) };
		// Project-trusted modules load on This PC alone.
		if ( host.IsLocal && module.Projects.BuildModulePrompt( context ) is { } modules )
			content.Add( modules );
		if ( host.IsLocal && module.IsWhatsNewShown )
			content.Add( BuildWhatsNew() );
		content.Add( new Spacer() );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Home",
			FlexGrow = 1,
			MinHeight = 0,
			Color = Theme.Current.Text,
			Children =
			[
				_front.BuildRail( isLeft: true ),
				_front.BuildRail( isLeft: false ),
				new Column
				{
					Key = "home-page",
					FlexGrow = 1,
					MinHeight = 0,
					AlignItems = AlignItems.Center,
					Padding = new Edges( Inset, HomeFront.RailWidth + Inset / 2 ),
					Gap = Theme.Current.Spacing * 2,
					Children =
					[
						new Column
						{
							Key = "home-content",
							FlexGrow = 1,
							MinWidth = 0,
							MinHeight = 0,
							AlignSelf = AlignSelf.Stretch,
							OverflowY = Overflow.Scroll,
							ScrollbarY = ViewStyle.GetScrollbar(),
							Children =
							[
								new Column
								{
									MinHeight = Length.Percent( 100 ),
									FlexShrink = 0,
									AlignItems = AlignItems.Center,
									Gap = Theme.Current.Spacing * 2,
									Children = content,
								},
							],
						},
						BuildPrompt(),
						BuildDetail( "Enter starts a chat  ·  Ctrl+Shift+H comes back here", "home-hint" ),
					],
				},
			],
		};
	}

	/// <summary>Builds the prompt bar as a project's overview draws it: paper, a hairline outline and soft corners; Enter starts a chat outside projects with what was typed.</summary>
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
			PlaceholderColor = theme.Inactive,
			AccessibilityName = "Start a chat",
			Width = Length.Percent( 100 ),
			MaxWidth = ColumnWidth,
			MinHeight = PromptHeight,
			FlexShrink = 0,
			Padding = new Edges( 0, 16 ),
			FontSize = theme.FontSize + 1,
			Color = theme.Text,
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Text.WithAlpha( 0.22 ),
			BorderWidth = 1,
			BorderRadius = 12,
			FocusStyle = new Style { BorderColor = theme.Muted },
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
			Padding = new Edges( 14, 16 ),
			Gap = theme.Spacing,
			BorderRadius = 12,
			BackgroundColor = theme.Text.WithAlpha( 0.045 ),
			Children =
			[
				new Text { Key = "title", Content = $"What's new in Loom {ConversationsModule.WhatsNewVersion}", FontFamily = SidebarRows.SerifFontFamily, FontStyle = FontStyle.Italic, FontSize = 18 },
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
		module.StartConversation( context.App, module.Hosts.Current, value.Trim(), module.ComputeSessionProject( context.App ), context.Id );
	}
}
