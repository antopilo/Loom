using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Builds the sidebar's native icons and icon actions without depending on fallback glyph fonts.</summary>
public sealed partial class SidebarRows
{
	/// <summary>Defines sidebar icon size in logical pixels.</summary>
	public const int IconSize = 12;

	/// <summary>Defines sidebar icon weight on the OpenType scale.</summary>
	private const int IconWeight = 400;

	/// <summary>Defines the search glyph from Segoe Fluent Icons.</summary>
	public const string SearchGlyph = "\uE721";

	/// <summary>Defines the new-folder glyph from Segoe Fluent Icons.</summary>
	public const string NewFolderGlyph = "\uE8F4";

	/// <summary>Defines the add glyph from Segoe Fluent Icons.</summary>
	public const string AddGlyph = "\uE710";

	/// <summary>Defines the more-actions glyph from Segoe Fluent Icons.</summary>
	public const string MoreGlyph = "\uE712";

	/// <summary>Defines the pin glyph from Segoe Fluent Icons, shown on pinned rows and as the pin action.</summary>
	public const string PinGlyph = "\uE718";

	/// <summary>Defines the unpin glyph from Segoe Fluent Icons.</summary>
	public const string UnpinGlyph = "\uE77A";

	/// <summary>Defines the tiles glyph from Segoe Fluent Icons, on actions that create a task.</summary>
	public const string TaskGlyph = "\uECA5";

	/// <summary>Defines how long a row's hidden actions take to fade in or out, in milliseconds.</summary>
	private const int RevealMs = 120;

	/// <summary>Defines how strongly a row action lightens the row highlight it sits on.</summary>
	private const double ActionHoverAlpha = 0.08;

	/// <summary>Retains the registered system icon font for the sidebar and the views that draw its glyphs.</summary>
	public static readonly FontSource IconFont = SystemFonts.Register( "Lokn History", IconWeight, italic: false, "SegoeIcons.ttf" );

	/// <summary>Builds a left-aligned icon action, such as New chat or New project.</summary>
	public Button BuildIconAction( string title, string icon, Action action )
	{
		var button = BuildAction( title, action );
		button.FlexDirection = FlexDirection.Row;
		button.JustifyContent = JustifyContent.FlexStart;
		button.AlignItems = AlignItems.Center;
		button.Gap = Theme.Current.Spacing;
		button.Children =
		[
			BuildIcon( icon ),
			new Text
			{
				Content = title,
				MinWidth = 0,
				TextMaxLines = 1,
			},
		];
		return button;
	}

	/// <summary>Adds the actions other modules contribute to <see cref="SidebarAction.Point"/>, such as Files, drawn like the sidebar's own; an active one is highlighted.</summary>
	public void AddContributedActions( List<Blob> children )
	{
		foreach ( var item in context.App.Extensions.Get<SidebarAction>( SidebarAction.Point ) )
		{
			var button = BuildIconAction( item.Title, item.Glyph, () => item.Run( context.App ) );
			button.Key = $"action:{item.Title}";
			if ( item.IsActive?.Invoke( context.App ) == true )
				button.BackgroundColor = Theme.Current.Hover;
			children.Add( button );
		}
	}

	/// <summary>Builds a square icon-only action; a hidden one stays in place and focusable, and appears on hover or focus.</summary>
	public Button BuildIconButton( string glyph, string accessibilityName, Action action, bool isVisible = true )
	{
		var button = BuildAction( accessibilityName, action );
		button.Width = Theme.Current.ControlHeight;
		button.Padding = 0;
		button.FlexShrink = 0;
		button.AlignItems = AlignItems.Center;
		button.JustifyContent = JustifyContent.Center;
		button.Opacity = isVisible ? 1 : 0;
		button.TransitionMs = RevealMs;
		button.TransitionProperties = [TransitionProperty.Opacity];
		// The action sits inside its row's highlight, so it lightens that highlight rather than repeating it.
		var hover = Theme.Current.Text.WithAlpha( ActionHoverAlpha );
		button.HoverStyle = new Style { Opacity = 1, BackgroundColor = hover };
		button.FocusStyle = new Style { Opacity = 1, BackgroundColor = hover };
		button.Children = [BuildIcon( glyph )];
		return button;
	}

	/// <summary>Builds one native glyph at the sidebar icon size.</summary>
	public static Text BuildIcon( string glyph ) => new()
	{
		Content = glyph,
		FontFamily = IconFont.Family,
		FontSize = IconSize,
		Color = Theme.Current.Muted,
	};
}
