using Goo;
using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Builds the host switcher over the chat sidebar and before the tab strip: the host the user is on with its status dot, and a menu of every host
/// plus Add host… and Manage hosts…; under the sidebar's, a banner while a paired host is offline or lets this PC only view.</summary>
/// <remarks>
/// Choosing a host navigates there, to its last session or Home (<see cref="ConversationsModule.SwitchHost"/>). The switcher is left out while only This PC
/// exists and the Remote module is not loaded. One instance belongs to one view and holds whether its menu is open.
/// </remarks>
/// <param name="context">The view's dispatch, which every action runs through.</param>
/// <param name="module">The module that switches hosts.</param>
internal sealed class HostSwitcher( PanelContext context, ConversationsModule module )
{
	/// <summary>Defines the Remote module, which pairs hosts.</summary>
	private const string RemoteModule = "remote";

	/// <summary>Defines the Remote access panel, where hosts are added and managed.</summary>
	private const string RemoteAccessPanel = "remote.access";

	/// <summary>Defines a status dot's diameter in logical pixels.</summary>
	private const int DotSize = HistoryContent.ActivityOrb.Size;

	/// <summary>Defines the width of the hosts menu in logical pixels.</summary>
	private const int MenuWidth = 260;

	/// <summary>Holds whether the hosts menu is open.</summary>
	private readonly State<bool> _isOpen = new( false );

	/// <summary>Targets the switcher, which the menu opens below.</summary>
	private readonly ElementHandle _anchor = new();

	/// <summary>Reports whether the switcher shows: more than This PC exists, or the Remote module that adds hosts is loaded.</summary>
	internal bool IsShown => context.App.Hosts.All.Count > 1 || IsRemoteLoaded;

	/// <summary>Reports whether the Remote module is loaded.</summary>
	private bool IsRemoteLoaded => context.App.Modules.Any( item => item.Id == RemoteModule );

	/// <summary>Builds the sidebar's row: the current host's dot, its name and a chevron, with the hosts menu below while open; null while hidden.</summary>
	internal Blob? BuildRow()
	{
		if ( !IsShown )
			return null;

		var theme = Theme.Current;
		var host = context.App.Hosts.Current;
		var button = theme.BuildButton( host.Name, () => context.Run( Toggle ) );
		button.Key = "host-switcher";
		button.Handle = _anchor;
		button.FlexDirection = FlexDirection.Row;
		button.JustifyContent = JustifyContent.FlexStart;
		button.AlignItems = AlignItems.Center;
		button.Gap = theme.Spacing;
		button.AccessibilityName = $"Host: {host.Name}, {FormatState( host )}. Switch host";
		button.Children =
		[
			BuildDot( host ),
			new Text
			{
				Content = host.Name,
				FontWeight = 600,
				MinWidth = 0,
				FlexShrink = 1,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			theme.BuildChevron( _isOpen.Value ),
		];
		return BuildWithMenu( "host-switcher-row", button );
	}

	/// <summary>Builds the tab strip's leading chip: the current host's dot and name, opening the same menu; null unless more than one host exists.</summary>
	internal Blob? BuildChip()
	{
		if ( context.App.Hosts.All.Count < 2 )
			return null;

		var theme = Theme.Current;
		var host = context.App.Hosts.Current;
		var button = theme.BuildButton( host.Name, () => context.Run( Toggle ) );
		button.Key = "host-chip";
		button.Handle = _anchor;
		button.FlexDirection = FlexDirection.Row;
		button.AlignItems = AlignItems.Center;
		button.FlexShrink = 0;
		button.Gap = theme.Spacing / 2;
		button.AccessibilityName = $"Host: {host.Name}, {FormatState( host )}. Switch host";
		button.Children =
		[
			BuildDot( host ),
			new Text { Content = host.Name, TextWrap = TextWrap.NoWrap },
		];
		return BuildWithMenu( "host-chip-group", button );
	}

	/// <summary>Builds the line under the sidebar's switcher while the current host is a paired one that is offline, with Reconnect, or that lets this PC
	/// only view; null otherwise.</summary>
	internal Blob? BuildBanner()
	{
		var host = context.App.Hosts.Current;
		if ( host.IsLocal )
			return null;

		var theme = Theme.Current;
		if ( !host.IsOnline )
		{
			var text = host.State == LoknHostState.Removed ? $"{host.Name} was removed"
				: host.StatusText.Length > 0 ? $"{host.Name} is offline · {host.StatusText}"
				: $"{host.Name} is offline";
			var children = new List<Blob> { BuildBannerText( text, theme.Muted ) };
			if ( host.State != LoknHostState.Removed && IsRemoteLoaded )
			{
				var reconnect = theme.BuildButton( "Reconnect", () => context.Run( () => context.App.OpenLink( LoknLinks.Create( RemoteModule, "reconnect", host.Id ) ) ) );
				reconnect.Key = "host-reconnect";
				reconnect.AlignSelf = AlignSelf.FlexStart;
				reconnect.AccessibilityName = $"Reconnect to {host.Name}";
				children.Add( reconnect );
			}
			return BuildBannerBox( "host-offline", children );
		}

		return host.CanControl ? null
			: BuildBannerBox( "host-view-only", [BuildBannerText( $"View only: ask {host.Name}'s user to allow control", theme.Muted )] );
	}

	/// <summary>Builds a host's status dot: green while it is This PC or connected (a ring while this PC may only view), a pulsing muted dot while connecting,
	/// the error color after a failure, and a muted ring while stopped or removed.</summary>
	internal static Blob BuildDot( LoknHost host )
	{
		var theme = Theme.Current;
		return host.State switch
		{
			LoknHostState.Connecting => Cell.Mount<Color, HistoryContent.ActivityOrb>( "host-dot", theme.Muted ),
			LoknHostState.Local => BuildDisc( theme.Success, isHollow: false ),
			LoknHostState.Connected => BuildDisc( theme.Success, isHollow: !host.CanControl ),
			LoknHostState.Failed => BuildDisc( theme.Error, isHollow: false ),
			_ => BuildDisc( theme.Muted, isHollow: true ),
		};
	}

	/// <summary>Describes a host's link in a word or two, as the menu and accessible names show it.</summary>
	internal static string FormatState( LoknHost host ) => host.State switch
	{
		LoknHostState.Local => "This PC",
		LoknHostState.Connecting => "Connecting",
		LoknHostState.Connected => host.CanControl ? "Connected" : "View only",
		LoknHostState.Failed => "Offline",
		LoknHostState.Stopped => "Stopped",
		_ => "Removed",
	};

	/// <summary>Opens or closes the hosts menu.</summary>
	private void Toggle() => _isOpen.Value = !_isOpen.Value;

	/// <summary>Wraps the switcher with its menu while the menu is open.</summary>
	private Container BuildWithMenu( string key, Button button ) => new()
	{
		Key = key,
		FlexShrink = 0,
		Children = _isOpen.Value
			? [button, Cell.Mount<ContextMenuInput, ContextMenu>( "host-menu", new( _anchor, "Hosts", BuildEntries(), () => context.Run( () => _isOpen.Value = false ) ) { Width = MenuWidth } )]
			: [button],
	};

	/// <summary>Lists the menu: every host, the current one checked, with its state on the right, then Add host… and Manage hosts… while Remote access is there.</summary>
	private List<MenuEntry> BuildEntries()
	{
		var app = context.App;
		var current = app.Hosts.Current;
		var entries = new List<MenuEntry>();
		foreach ( var host in app.Hosts.All )
		{
			var id = host.Id;
			entries.Add( new MenuAction( host == current ? $"✓  {host.Name}" : host.Name, () => context.Run( () => module.SwitchHost( app, id ) ) )
			{
				Shortcut = FormatState( host ),
			} );
		}

		if ( !IsRemoteLoaded && app.Hosts.All.Count > 1 )
			entries.Add( new MenuNote( "Remote is off" ) );
		if ( app.Panels.Types.Any( type => type.Id == RemoteAccessPanel ) )
		{
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Add host…", () => context.Run( () => app.Panels.Open( RemoteAccessPanel, input: "add-host" ) ) ) { AccessKey = Key.A } );
			entries.Add( new MenuAction( "Manage hosts…", () => context.Run( () => app.Panels.Open( RemoteAccessPanel, input: "hosts" ) ) ) { AccessKey = Key.M } );
		}
		return entries;
	}

	/// <summary>Builds a filled or hollow dot.</summary>
	private static Container BuildDisc( Color color, bool isHollow ) => new()
	{
		Key = "host-dot",
		Width = DotSize,
		Height = DotSize,
		FlexShrink = 0,
		BorderRadius = DotSize / 2.0,
		BackgroundColor = isHollow ? Color.Transparent : color,
		BorderColor = color,
		BorderWidth = isHollow ? 2 : 0,
	};

	/// <summary>Builds the banner's wrapped line.</summary>
	private static Text BuildBannerText( string text, Color color ) => new()
	{
		Key = "host-banner-text",
		Content = text,
		Color = color,
		TextWrap = TextWrap.Wrap,
	};

	/// <summary>Builds the banner's box, inset like the sidebar's rows.</summary>
	private static Container BuildBannerBox( string key, List<Blob> children ) => new()
	{
		Key = key,
		AccessibilityRole = AccessibilityRole.Group,
		AccessibilityName = "Host status",
		Gap = Theme.Current.Spacing / 2,
		Padding = new Edges( Theme.Current.Spacing / 2, Theme.Current.Spacing ),
		BorderRadius = Theme.Current.Radius,
		BackgroundColor = Theme.Current.Raised,
		Children = children,
	};
}
