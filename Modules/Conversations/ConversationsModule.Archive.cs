using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Archives the chats nobody uses any more: each list, a project's or the chats outside projects, keeps its most recent chats, and a chat
/// untouched for long leaves too. An archived chat leaves the sidebar's list and memory, and comes back from its project's archive or when opened.</summary>
public sealed partial class ConversationsModule
{
	/// <summary>Defines after how many days without a message a chat is archived; 0 never archives by age.</summary>
	internal static readonly Setting<int> ArchiveAfterDays = new(
		"conversations.archive-after-days", "Archive chats after",
		"Chats nobody wrote in for this long leave the sidebar and memory. Bring one back from the archive beside its project. 0 never archives by age.",
		defaultValue: 14, category: "Conversations", isValid: value => value is >= 0 and <= 365 ) { Unit = "days", Minimum = 0, Maximum = 365 };

	/// <summary>Defines how many chats each project, and the chats outside projects, keeps in the sidebar before older ones are archived; 0 keeps all.</summary>
	internal static readonly Setting<int> KeepChats = new(
		"conversations.keep-chats", "Chats kept per project",
		"Older chats beyond this many in a project, or outside projects, are archived. Pinned chats, chats open in a panel and working chats always stay. 0 keeps all.",
		defaultValue: 12, category: "Conversations", isValid: value => value is >= 0 and <= 500 ) { Unit = "chats", Minimum = 0, Maximum = 500 };

	/// <summary>Defines how often the chats are checked for archiving while Loom runs.</summary>
	private static readonly TimeSpan ArchiveInterval = TimeSpan.FromMinutes( 30 );

	/// <summary>Lists the chats to archive now in the stores this Loom writes: This PC's, unless it mirrors the daemon, which archives its own.</summary>
	/// <remarks>
	/// Per profile and list, from the most recent: pinned chats, chats open in a panel, the selected chat, working chats, chats with a draft and tasks
	/// without a chat always stay and do not count; of the rest, the first <see cref="KeepChats"/> stay unless older than <see cref="ArchiveAfterDays"/>.
	/// </remarks>
	internal IReadOnlyList<ConversationInfo> ComputeArchive( LoknApplication app, DateTimeOffset now )
	{
		var host = Hosts.Local;
		var history = host.Store;
		if ( history.IsMirror )
			return [];

		var settings = app.Settings;
		var days = settings.Get( ArchiveAfterDays, SettingsScope.Global );
		var keep = settings.Get( KeepChats, SettingsScope.Global );
		if ( days == 0 && keep == 0 )
			return [];

		var open = FindPanels( app ).Select( item => item.Panel.Chat ).OfType<ChatRef>().Where( chat => chat.HostId == host.Id ).Select( chat => chat.ConversationId ).ToHashSet();
		var selected = GetSelected( app );
		var archive = new List<ConversationInfo>();
		foreach ( var profile in Enum.GetValues<SettingsProfile>() )
		{
			Guid?[] lists = [null, .. host.Projects.Projects( profile ).Select( project => (Guid?)project.Id )];
			foreach ( var list in lists )
			{
				var kept = 0;
				foreach ( var conversation in history.Conversations( profile, list ).Where( item => !item.IsArchived ).OrderByDescending( item => item.UpdatedAt ) )
				{
					var isInUse = open.Contains( conversation.Id ) || selected == host.Ref( conversation.Id ) || history.IsWorking( conversation.Id );
					if ( isInUse || conversation is { IsPinned: true } or { HasChat: false } || conversation.Draft is not null )
						continue;

					var isOld = days > 0 && now - conversation.UpdatedAt >= TimeSpan.FromDays( days );
					if ( isOld || (keep > 0 && kept >= keep) )
						archive.Add( conversation );
					else
						kept++;
				}
			}
		}
		return archive;
	}

	/// <summary>Archives the chats <see cref="ComputeArchive"/> lists now.</summary>
	internal void ArchiveIdle( LoknApplication app )
	{
		var history = Hosts.Local.Store;
		foreach ( var conversation in ComputeArchive( app, DateTimeOffset.UtcNow ) )
			history.SetArchived( conversation.Id, true );
	}

	/// <summary>Archives a chat now, as its menu's Archive does: its panels close first, so its messages can leave memory.</summary>
	internal void ArchiveConversation( LoknApplication app, ChatRef chat )
	{
		if ( Hosts.Find( chat ) is not { } found )
			return;

		foreach ( var (id, panel) in FindPanels( app ).Where( item => item.Panel.Chat == chat ) )
			app.Panels.Close( id );
		found.Host.Store.SetArchived( chat.ConversationId, true );
	}

	/// <summary>Brings an archived chat back to its list and opens it.</summary>
	internal void RestoreConversation( LoknApplication app, ChatRef chat )
	{
		if ( Hosts.Find( chat ) is not { } found )
			return;

		found.Host.Store.SetArchived( chat.ConversationId, false );
		SelectConversation( app, chat );
	}

	/// <summary>Archives idle chats once Loom has started, then every <see cref="ArchiveInterval"/> while the module runs.</summary>
	private async Task RunArchiveAsync( ModuleContext context )
	{
		try
		{
			while ( !context.Lifetime.IsCancellationRequested )
			{
				context.Post( () => ArchiveIdle( context.App ) );
				await Task.Delay( ArchiveInterval, context.Lifetime ).ConfigureAwait( false );
			}
		}
		catch ( OperationCanceledException )
		{
			// The module stopped.
		}
	}
}
