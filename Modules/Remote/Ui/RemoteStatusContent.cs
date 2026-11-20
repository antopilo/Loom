using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The status bar's daemon dot in the <c>daemon</c> role: its color and the daemon's state, opening Remote access when clicked, and the
/// dialog that asks whether a device that just paired may control this PC; empty in the other roles, where paired hosts show in the sidebar's
/// host switcher instead.</summary>
/// <param name="context">The slot's context.</param>
/// <param name="module">The module.</param>
public sealed class RemoteStatusContent( PanelContext context, RemoteModule module ) : PanelContent
{
	/// <summary>Picks the dot's color for a client state.</summary>
	public static Color ComputeColor( ClientState state, Theme theme ) => state switch
	{
		ClientState.Connected => theme.Success,
		ClientState.Failed => theme.Error,
		ClientState.Stopped => theme.Inactive,
		_ => theme.Attention,
	};

	/// <summary>Builds the dot and a short label with the pairing dialog while one waits, or nothing without the daemon link.</summary>
	public override Blob Build()
	{
		if ( module.Daemon is not { } client )
			return new Container();

		var theme = Theme.Current;
		var label = "Daemon: " + client.Status switch
		{
			ClientState.Connected => client.HostName.Length > 0 ? client.HostName : "connected",
			ClientState.Failed => "not reachable",
			ClientState.Stopped => "stopped",
			_ => "connecting",
		};
		void Open() => context.Run( () => context.App.OpenLink( LoknLinks.Create( "remote", RemoteModule.RoleRoute ) ) );
		var button = new Button
		{
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"{label}. {client.StatusText} Open Remote access.",
			OnClick = Open,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Open ), new( Key.Space, KeyModifiers.None, Open )],
			HoverStyle = new Style { BackgroundColor = theme.Hover, },
			Children =
			[
				AccessContent.BuildDot( ComputeColor( client.Status, theme ) ),
				new Text { Content = label, TextWrap = TextWrap.NoWrap, },
			],
		};
		if ( module.Prompts?.BuildDialog() is not { } dialog )
			return button;

		return new Row
		{
			AlignItems = AlignItems.Center,
			Children = [button, Cell.Mount<ConfirmDialogInput, ConfirmDialog>( "pairing-confirm", dialog )],
		};
	}
}
