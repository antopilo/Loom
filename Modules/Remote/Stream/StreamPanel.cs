using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Registers the window stream panel, <c>stream</c>, over whichever <see cref="IStreamSource"/> the caller supplies.</summary>
public static class StreamPanel
{
	/// <summary>Defines the panel type id within the module.</summary>
	public const string Id = "stream";

	/// <summary>Defines the panel title.</summary>
	public const string Title = "Window stream";

	/// <summary>Registers the panel, bound to the host it opens on, and its palette command (<c>Open Window stream</c>).</summary>
	/// <param name="builder">The registering module's builder.</param>
	/// <param name="source">Returns the source for a new panel, its host's windows, or null when that host is not connected, which shows a notice instead.</param>
	public static void Register( ModuleBuilder builder, Func<PanelContext, IStreamSource?> source ) =>
		builder.Panel( Id, Title, context => source( context ) is { } found ? new StreamContent( context, found ) : new NotConnectedContent(), "\uE7F4", onHost: true );

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
