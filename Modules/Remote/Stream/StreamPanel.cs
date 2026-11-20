using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Names the window a stream panel streams as soon as it opens, skipping the window list; also the panel's saved state.</summary>
/// <param name="Hwnd">The window's handle on the panel's host.</param>
/// <param name="Title">The window's title, shown until the stream says more.</param>
public sealed record StreamPanelInput( long Hwnd, string Title );

/// <summary>Registers the window stream panel, <c>stream</c>, over whichever <see cref="IStreamSource"/> the caller supplies.</summary>
public static class StreamPanel
{
	/// <summary>Defines the panel type id within the module.</summary>
	public const string Id = "stream";

	/// <summary>Defines the panel type's full id.</summary>
	public const string TypeId = "remote." + Id;

	/// <summary>Defines the panel title.</summary>
	public const string Title = "Window stream";

	/// <summary>Registers the panel, bound to the host it opens on, and its palette command (<c>Open Window stream</c>).</summary>
	/// <param name="builder">The registering module's builder.</param>
	/// <param name="source">Returns the source for a new panel, its host's windows, or null when that host is not connected, which shows a notice instead.</param>
	public static void Register( ModuleBuilder builder, Func<PanelContext, IStreamSource?> source ) =>
		builder.Panel( Id, Title, context => source( context ) is { } found ? new StreamContent( context, found, initial: ReadInput( context ) ) : new NotConnectedContent(), "\uE7F4", onHost: true );

	/// <summary>Shows a host's window streamed for an agent that works with it: brings the stream panel of that window on that host forward, or opens one
	/// beside the agent's panel that streams it at once. The active pane, and so the chat composer's focus, stays where it was.</summary>
	/// <param name="app">The application, on the UI thread.</param>
	/// <param name="hostId">The host whose window it is.</param>
	/// <param name="beside">The panel to open beside, such as the agent's chat; null opens in the active pane.</param>
	/// <param name="hwnd">The window's handle on the host.</param>
	/// <param name="title">The window's title.</param>
	/// <returns>The stream panel's identifier.</returns>
	public static Guid Show( LoknApplication app, string hostId, Guid? beside, long hwnd, string title )
	{
		var panels = app.Panels;
		var active = panels.ActivePaneId;
		Guid id;
		if ( StreamContent.Find( hostId, hwnd ) is { } open )
		{
			id = open.PanelId;
			open.Watch( hwnd, title );
			// A panel waiting beside a chat that is off screen stays hidden with it; otherwise it comes back into view.
			if ( !panels.IsShown( id ) && (beside is not { } chat || panels.IsShown( panels.ResolvePanelId( chat ) )) )
				panels.Activate( id );
		}
		else
		{
			var input = new StreamPanelInput( hwnd, title );
			id = beside is { } source ? panels.OpenBeside( TypeId, source, input, hostId ) : panels.Open( TypeId, null, input, hostId );
		}

		if ( panels.ActivePaneId != active && panels.Panes.Any( pane => pane.Id == active ) )
			panels.ActivatePane( active );
		return id;
	}

	/// <summary>Reads the window a panel streams from its input, else from the state its last session saved; null shows the window list.</summary>
	private static StreamPanelInput? ReadInput( PanelContext context )
	{
		if ( context.Input is StreamPanelInput input )
			return input;

		return context.RestoredState is { ValueKind: JsonValueKind.Object } state ? state.Deserialize<StreamPanelInput>( StreamService.Wire ) : null;
	}

	/// <summary>Tells the user the stream needs a connected host.</summary>
	private sealed class NotConnectedContent : PanelContent
	{
		/// <summary>Builds the notice.</summary>
		public override Blob Build()
		{
			var theme = Theme.Current;
			return new Container
			{
				AccessibilityRole = AccessibilityRole.Group,
				AccessibilityName = Title,
				FlexGrow = 1,
				Padding = 16,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = "Not connected", FontSize = 18, },
					new Text { Content = "Connect to a host in Remote access, then open the window stream again.", Color = theme.Muted, },
				],
			};
		}
	}
}
