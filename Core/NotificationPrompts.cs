using Goo;

namespace Lokn.Core;

/// <summary>Something a panel waits on the user for, such as a question an agent asked, that notification views can show and answer in place.</summary>
/// <param name="Key">A stable identifier, unique among the prompts.</param>
/// <param name="Title">What waits, such as the chat's title.</param>
/// <param name="PanelId">The panel that waits, whose notifications show this prompt in their banner; null when it has no panel.</param>
/// <param name="Profile">The profile the prompt belongs to; it opens only from that profile.</param>
/// <param name="Build">Builds the answering controls for a surface, such as <c>feed</c> or <c>banner</c>, during the view's build; null when there is nothing to show.</param>
/// <param name="Open">Shows where the prompt comes from, such as the chat, for answers that need its context.</param>
public sealed record NotificationPrompt( string Key, string Title, Guid? PanelId, SettingsProfile? Profile, Func<string, Blob?> Build, Action Open );

/// <summary>Lists the prompts a module has waiting; contribute it to <see cref="Point"/>.</summary>
/// <remarks>The views read it on the UI thread during their build, so reading state the list depends on rebuilds them when it changes.</remarks>
/// <param name="List">Gets the prompts waiting now.</param>
public sealed record NotificationPromptSource( Func<LoknApplication, IReadOnlyList<NotificationPrompt>> List )
{
	/// <summary>The extension point the Notifications module reads prompts from.</summary>
	public const string Point = "notifications.prompts";

	/// <summary>Gets every prompt waiting now, from every source.</summary>
	public static IReadOnlyList<NotificationPrompt> ListAll( LoknApplication app ) =>
		[.. app.Extensions.Get<NotificationPromptSource>( Point ).SelectMany( source => source.List( app ) )];
}
