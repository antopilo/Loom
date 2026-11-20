using System.Text.Json;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using static Lokn.Modules.Chat.ChatContent;

namespace Lokn.Modules.Chat;

/// <summary>Keeps the Omp process in the chat's folder, moves a chat outside projects to another folder, and follows the agent into a worktree.</summary>
public sealed partial class ChatSession
{
	/// <summary>Stores the folder the connected Omp process was started in, or null before a connection.</summary>
	private string? _connectionDirectory;

	/// <summary>Counts the folders the agent's bash commands ended in during the running turn, as Omp's results report them in <c>details.cwd</c>.</summary>
	private readonly Dictionary<string, int> _shellFolders = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Moves a chat outside projects to another folder. A connected Omp stops, so the next prompt starts Omp in the new folder and continues the same session.</summary>
	/// <returns>Whether the folder was set; a project chat, or one whose agent is working or connecting, keeps its folder and says why.</returns>
	public bool UpdateDirectory( string directory )
	{
		if ( _isDisposed )
			return false;

		if ( ComputeFolderRefusal( store.Get( conversationId ) ) is { } refusal )
		{
			_status = refusal;
			MarkChanged();
			return false;
		}

		store.SetDirectory( conversationId, directory );
		UpdateFollowFolder();
		_status = $"Folder set to {FolderName.Compute( store.GetDirectory( conversationId ) )}";
		MarkChanged();
		return true;
	}

	/// <summary>Gets why the chat cannot change its folder now, or null when it can.</summary>
	public string? ComputeFolderRefusal( ConversationInfo conversation ) => ComputeFolderRefusal( conversation, _isBusy, _connection is { IsCompleted: false } );

	/// <summary>Gets why a chat cannot change its folder now, or null when it can; shared by a session and its proxy in another process.</summary>
	/// <param name="conversation">The chat.</param>
	/// <param name="isBusy">Whether its agent works.</param>
	/// <param name="isConnecting">Whether its process is starting.</param>
	internal static string? ComputeFolderRefusal( ConversationInfo conversation, bool isBusy, bool isConnecting ) =>
		conversation.ProjectId is not null ? "A project chat works in its project's folder"
		: isBusy || isConnecting ? "The folder can change once the agent is idle"
		: null;

	/// <summary>Stops a connected Omp that runs in another folder than the chat's, such as after its agent moved the chat into a worktree,
	/// so the next turn starts Omp in the chat's folder and continues the same session; call only between turns.</summary>
	private void UpdateFollowFolder()
	{
		if ( _connection is not { IsCompleted: true } || _connectionDirectory is not { } connected || ConversationStore.IsSameFolder( connected, store.GetDirectory( conversationId ) ) )
			return;

		// Forgetting the stopped process lets the next connection start Omp in the new folder; the session directory keeps the conversation.
		UpdateDisconnect();
	}

	/// <summary>Counts the folder a finished bash command ended in, from its result's <c>details.cwd</c>; other messages are ignored.</summary>
	private void UpdateShellFolder( JsonElement message )
	{
		if ( ReadText( message, "role" ) != "toolResult" || ReadText( message, "toolName" ) != "bash" || !message.TryGetProperty( "details", out var details ) )
			return;

		var folder = ReadText( details, "cwd" );
		if ( folder.Length > 0 )
			_shellFolders[folder] = _shellFolders.GetValueOrDefault( folder ) + 1;
	}

	/// <summary>Moves a project chat, once its turn ends, to the worktree of its project's repository where most of the turn's bash commands ran,
	/// so the sidebar lists it there and Omp starts there from the next turn on; a tie, or folders outside that repository, keep it where it is.</summary>
	private void UpdateFollowShell()
	{
		if ( _shellFolders.Count == 0 )
			return;

		KeyValuePair<string, int>[] counts = [.. _shellFolders];
		_shellFolders.Clear();
		if ( store.Find( conversationId )?.ProjectId is not { } projectId || store.ProjectStore.Find( projectId ) is not { } project )
			return;

		_ = UpdateFollowShellAsync( counts, project.Directory, store.GetDirectory( conversationId ) );
	}

	/// <summary>Finds the worktree most of the turn's commands ran in off the UI thread, then moves the chat there unless its folder changed meanwhile.</summary>
	private async Task UpdateFollowShellAsync( KeyValuePair<string, int>[] counts, string projectDirectory, string current )
	{
		var votes = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		try
		{
			if ( await ReadWorktreeAsync( projectDirectory ).ConfigureAwait( false ) is not { } repository )
				return;

			foreach ( var (folder, count) in counts )
			{
				if ( await ReadWorktreeAsync( folder ).ConfigureAwait( false ) is { } tree && string.Equals( tree.Common, repository.Common, StringComparison.OrdinalIgnoreCase ) )
					votes[tree.Root] = votes.GetValueOrDefault( tree.Root ) + count;
			}
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// Without git, or once the session ended, the chat stays where it is.
			return;
		}

		if ( votes.Count == 0 )
			return;

		var most = votes.Values.Max();
		var target = votes.First( vote => vote.Value == most ).Key;
		if ( votes.Count( vote => vote.Value == most ) > 1 || ConversationStore.IsSameFolder( target, current ) )
			return;

		Post( () =>
		{
			if ( store.Find( conversationId ) is { ProjectId: not null } && ConversationStore.IsSameFolder( store.GetDirectory( conversationId ), current ) )
				store.SetWorktree( conversationId, target );
		} );
	}

	/// <summary>Reads the top folder of the working tree holding a folder and its repository's shared git folder, or null outside a repository.</summary>
	private async Task<(string Root, string Common)?> ReadWorktreeAsync( string folder )
	{
		if ( !System.IO.Directory.Exists( folder ) )
			return null;

		// A local session only runs for This PC's chats.
		var output = await GitProcess.TryRunAsync( app.Hosts.Local.Workspace, folder, Lifetime, "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir" ).ConfigureAwait( false );
		return output?.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) is [var root, var common]
			? (Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) ), Path.TrimEndingDirectorySeparator( Path.GetFullPath( common ) ))
			: null;
	}
}
