using Goo;
using Lokn.Core;

namespace Lokn.Modules;

/// <summary>Provides the optional welcome panel as a minimal example feature module.</summary>
public sealed class WelcomeModule : ILoknModule
{
	/// <summary>Defines the welcome panel heading displayed by the active profile.</summary>
	internal static readonly Setting<string> Greeting = new(
		"welcome.greeting",
		"Heading",
		"The heading in Welcome panels.",
		defaultValue: "Your workspace, your way.",
		category: "Welcome",
		isValid: value => !string.IsNullOrWhiteSpace( value ) );

	/// <summary>Gets the feature's stable module identifier.</summary>
	public string Id => "welcome";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "Welcome";

	/// <summary>Registers the feature's panel type and automatic palette command.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder.Setting( Greeting ).Panel( "home", "Welcome", context => new WelcomeContent( context ) );
	}

	/// <summary>Opens an initial panel when the feature is first enabled.</summary>
	public void Start( ModuleContext context ) => context.App.Panels.Open( "welcome.home" );
}

/// <summary>Owns welcome content and demonstrates state retained through docking.</summary>
internal sealed class WelcomeContent( PanelContext context ) : PanelContent
{
	/// <summary>Defines the welcome content inset in logical pixels.</summary>
	private const int ContentPadding = 32;

	/// <summary>Defines the welcome heading size in logical pixels.</summary>
	private const int HeadingSize = 28;

	/// <summary>Retains a scratch value independently of pane focus and docking.</summary>
	private readonly State<string> _scratch = new( "" );

	/// <summary>Builds workspace guidance and a small state-retention example.</summary>
	public override Blob Build()
	{
		return new Container
		{
			Padding = ContentPadding,
			Gap = Theme.Current.Spacing,
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			Children =
			[
				new Text
				{
					Content = context.App.Settings.Get( WelcomeModule.Greeting ),
					FontSize = HeadingSize,
				},
				new Text
				{
					Content = "A small foundation for the tools you want to build.",
					Color = Theme.Current.Muted,
				},
				new Text
				{
					Content = "Open panels with the titlebar + icon. Split the workspace, drag a pane's top grip to an edge to dock, or drop it in the center to swap. Click the grip for pane actions.",
				},
				new Text
				{
					Content = "Ctrl+Shift+P opens every command. Open Modules to enable or disable optional features.",
					Color = Theme.Current.Muted,
				},
				new TextEntry
				{
					Value = _scratch.Value,
					Controlled = true,
					Placeholder = "Type something — it stays when you move this panel",
					AccessibilityName = "Workspace scratch text",
					Padding = new Edges( 0, Theme.Current.Spacing ),
					Height = Theme.Current.ControlHeight + Theme.Current.Spacing,
					BackgroundColor = Theme.Current.Raised,
					BorderColor = Theme.Current.Border,
					BorderWidth = Theme.Current.BorderWidth,
					BorderRadius = Theme.Current.Radius,
					OnChange = value => context.Run( () => _scratch.Value = value ),
				},
			],
		};
	}
}
