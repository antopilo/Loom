using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Supplies the profile switcher with the active profile, each profile's icon, and the actions that change them.</summary>
/// <param name="Active">The active settings profile.</param>
/// <param name="WorkIcon">Work's emoji, or empty for its initial.</param>
/// <param name="PersonalIcon">Personal's emoji, or empty for its initial.</param>
/// <param name="Switch">Makes a profile active.</param>
/// <param name="SetIcon">Saves a profile's emoji; empty restores its initial.</param>
/// <param name="IsInline">Whether it sits in the titlebar, so it adds only side padding.</param>
internal sealed record ProfileSwitcherInput(
	SettingsProfile Active,
	string WorkIcon,
	string PersonalIcon,
	Action<SettingsProfile> Switch,
	Action<SettingsProfile, string> SetIcon,
	bool IsInline = false );

/// <summary>Switches profiles from the bottom of the sidebar, like spaces in Arc: one round button per profile showing its emoji.</summary>
/// <remarks>Clicking another profile switches to it; clicking the active one, or right-clicking any, opens its icon picker.</remarks>
internal sealed class ProfileSwitcher : GuardedCell<ProfileSwitcherInput>
{
	/// <summary>Defines each profile button's diameter in logical pixels.</summary>
	private const int ButtonSize = 30;

	/// <summary>Defines the emoji size in logical pixels.</summary>
	private const int IconSize = 16;

	/// <summary>Defines each suggested emoji's button size in logical pixels.</summary>
	private const int SuggestionSize = 30;

	/// <summary>Defines the opacity of profiles that are not active.</summary>
	private const double InactiveOpacity = 0.55;

	/// <summary>Defines the initial's emphasis weight.</summary>
	private const int InitialWeight = 600;

	/// <summary>Defines how far, as a fraction of the font size, an emoji's ink sits above the center of its line box.</summary>
	/// <remarks>Segoe UI Emoji draws its glyphs about 758 units above the baseline at their center, its line box centers at 530 of 2048.</remarks>
	private const double EmojiRise = 0.11;

	/// <summary>Defines how far, as a fraction of the font size, a capital's ink sits above the center of its line box.</summary>
	/// <remarks>Segoe UI's capitals center at 717 units above the baseline, its line box at 530 of 2048.</remarks>
	private const double CapitalRise = 0.09;

	/// <summary>Lists the emoji offered in the icon picker; any other can be typed or pasted.</summary>
	private static readonly string[] Suggestions =
	[
		"💼", "🏢", "💻", "🛠️", "📈", "🧪", "🚀", "⚙️",
		"🏠", "🌿", "🎨", "🎮", "🎧", "📚", "☕", "🦊",
	];

	/// <summary>Retains the installed emoji face, registered once for the shell.</summary>
	private static readonly FontSource EmojiFont = SystemFonts.Register( "Lokn Shell Emoji", 400, italic: false, "seguiemj.ttf" );

	/// <summary>Retains the profile whose icon picker is open, or empty when none is.</summary>
	private readonly State<string> _editing = new( "" );

	/// <summary>Builds the icon picker when open, then the row of profile buttons.</summary>
	protected override Blob BuildView( ProfileSwitcherInput input )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( Enum.TryParse<SettingsProfile>( _editing.Value, out var editing ) )
			children.Add( BuildPicker( editing, input ) );

		children.Add( new Row
		{
			Key = "profile-buttons",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Profiles",
			JustifyContent = JustifyContent.Center,
			Gap = theme.Spacing,
			Children = [BuildButton( SettingsProfile.Work, input.WorkIcon, input ), BuildButton( SettingsProfile.Personal, input.PersonalIcon, input )],
		} );
		return new Container
		{
			Key = "profile-switcher",
			FlexShrink = 0,
			Padding = input.IsInline ? new Edges( 0, theme.Spacing ) : new Edges( theme.Spacing, theme.Spacing, theme.Spacing * 1.5, theme.Spacing ),
			Children = children,
		};
	}

	/// <summary>Builds one profile's round button: its emoji or initial, filled while active.</summary>
	private Button BuildButton( SettingsProfile profile, string icon, ProfileSwitcherInput input )
	{
		var theme = Theme.Current;
		var isActive = profile == input.Active;
		void Activate()
		{
			if ( isActive )
				UpdateEditing( profile );
			else
				InvokeInputCallback( input.Switch, profile );
		}

		return new Button
		{
			Key = "profile:" + profile,
			Width = ButtonSize,
			Height = ButtonSize,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = ButtonSize / 2.0,
			BackgroundColor = isActive ? theme.Hover : Color.Transparent,
			Opacity = isActive ? 1 : InactiveOpacity,
			TransitionMs = 120,
			TransitionProperties = [TransitionProperty.Opacity, TransitionProperty.BackgroundColor],
			Focusable = true,
			Cursor = Cursor.Pointer,
			Accessibility = new Accessibility
			{
				Role = AccessibilityRole.Radio,
				Name = isActive ? $"{profile} profile, active. Choose its icon." : $"Switch to {profile}",
				Checked = isActive ? AccessibilityChecked.True : AccessibilityChecked.False,
			},
			HoverStyle = new Style { Opacity = 1, BackgroundColor = theme.Hover },
			FocusStyle = new Style { Opacity = 1, OutlineWidth = theme.BorderWidth, OutlineColor = theme.Accent },
			OnClick = Activate,
			OnPointerDown = item =>
			{
				if ( item.Button != PointerButton.Secondary )
					return;

				item.PreventDefault();
				UpdateEditing( profile );
			},
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Activate ),
				new( Key.Space, KeyModifiers.None, Activate ),
			],
			Children = [BuildIcon( profile, icon )],
		};
	}

	/// <summary>Builds the icon picker for a profile: suggested emoji, a field for any other, and a way back to its initial.</summary>
	private Container BuildPicker( SettingsProfile profile, ProfileSwitcherInput input )
	{
		var theme = Theme.Current;
		void Choose( string icon )
		{
			InvokeInputCallback( input.SetIcon, profile, icon );
			_editing.Value = "";
		}

		var suggestions = Suggestions.Select( emoji => (Blob)new Button
		{
			Width = SuggestionSize,
			Height = SuggestionSize,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = "Use " + emoji,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			OnClick = () => Choose( emoji ),
			KeyBindings = [new( Key.Enter, KeyModifiers.None, () => Choose( emoji ) )],
			Children = [BuildEmoji( emoji )],
		} ).ToList();
		return new Container
		{
			Key = "profile-picker",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{profile} icon",
			Gap = theme.Spacing,
			Padding = theme.Spacing,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Children =
			[
				new Text { Content = $"{profile} icon", FontSize = 12, Color = theme.Muted },
				new Row { FlexWrap = FlexWrap.Wrap, Gap = 2, Children = suggestions },
				new TextEntry
				{
					Key = "profile-icon-entry",
					Placeholder = "Type or paste any emoji (Win + .)",
					AccessibilityName = $"{profile} emoji",
					Height = theme.ControlHeight,
					Padding = new Edges( 0, theme.Spacing ),
					BackgroundColor = theme.Panel,
					BorderRadius = theme.Radius,
					// The first character is the icon, so a pasted phrase or an emoji with a trailing space still works.
					OnChange = value =>
					{
						if ( ComputeIcon( value ) is { Length: > 0 } icon )
							Choose( icon );
					},
				},
				new Row
				{
					Gap = theme.Spacing,
					Children =
					[
						BuildAction( "Use initial", () => Choose( "" ) ),
						new Spacer(),
						BuildAction( "Done", () => _editing.Value = "" ),
					],
				},
			],
		};
	}

	/// <summary>Opens a profile's icon picker, or closes it when it is already open.</summary>
	private void UpdateEditing( SettingsProfile profile ) => _editing.Value = _editing.Value == profile.ToString() ? "" : profile.ToString();

	/// <summary>Gets the first user-perceived character of typed text, so one emoji with its modifiers stays whole.</summary>
	internal static string ComputeIcon( string text )
	{
		var trimmed = text.Trim();
		return trimmed.Length == 0 ? "" : StringInfo.GetNextTextElement( trimmed );
	}

	/// <summary>Builds a profile's emoji, or its initial when it has none, lowered so its ink, not its line box, is centered.</summary>
	private static Text BuildIcon( SettingsProfile profile, string icon ) => icon.Length > 0
		? BuildEmoji( icon )
		: new Text
		{
			Content = profile.ToString()[..1],
			Top = Theme.Current.FontSize * CapitalRise,
			FontWeight = InitialWeight,
			FontSize = Theme.Current.FontSize,
			Color = Theme.Current.Text,
		};

	/// <summary>Builds an emoji, lowered so its ink, not its line box, is centered in its button.</summary>
	private static Text BuildEmoji( string emoji ) => new() { Content = emoji, Top = IconSize * EmojiRise, FontFamily = EmojiFont.Family, FontSize = IconSize };

	/// <summary>Builds a compact text action.</summary>
	private static Button BuildAction( string label, Action action )
	{
		var button = Theme.Current.BuildButton( label, action );
		button.FlexShrink = 0;
		return button;
	}
}
