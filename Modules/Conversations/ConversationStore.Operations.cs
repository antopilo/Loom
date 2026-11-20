using Lokn.Core;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Publishes each change as a <see cref="StoreOp"/>, applies operations and message pushes, and mirrors another process's history.</summary>
/// <remarks>
/// A mirror shows a host's conversations (see <c>docs/remote.md</c>): <see cref="Load"/> replaces them from the host's snapshot, and every
/// public mutator a client may call changes the mirror at once and sends its operation through <see cref="Forward"/>, so a caller reads its own
/// change straight back. The host applies it and sends it back to every client through <see cref="Apply"/>, where the mirror that made it finds
/// it applied already, since every mutator returns early on a value it already holds; operations come back in the host's order, so the mirror
/// ends as the host does. <see cref="MoveConversation"/> is the exception, as moving twice is not a no-op: it only sends the move. Messages are
/// the host's alone: <see cref="AppendMessage"/>, <see cref="UpdateMessage"/>, <see cref="ReplaceMessages"/> and <see cref="SetWorking"/> are
/// never forwarded, and a mirror receives messages through <see cref="ApplyMessage"/> and <see cref="ApplyMessages"/>. A mirror never reads or
/// writes history files.
/// </remarks>
public sealed partial class ConversationStore
{
	/// <summary>Stores where a mirror sends its changes, or null when the store is the authority.</summary>
	private Func<StoreOp, bool>? _forward;

	/// <summary>Reports whether the store mirrors another process's, so it never touches the history files again.</summary>
	private bool _isMirror;

	/// <summary>Reports whether an operation is being applied, which changes the store even in a mirror.</summary>
	private bool _isApplying;

	/// <summary>Raised on the UI thread after a public mutator or <see cref="Apply"/> changed the conversations, with the operation that repeats the change.</summary>
	public event Action<StoreOp>? Mutated;

	/// <summary>Raised on the UI thread after a message was appended or replaced, with its conversation, the message and whether it was saved.</summary>
	public event Action<Guid, ConversationMessage, bool>? MessageApplied;

	/// <summary>Raised on the UI thread after a conversation's messages were all replaced, with the conversation and its new messages.</summary>
	public event Action<Guid, IReadOnlyList<ConversationMessage>>? MessagesReplaced;

	/// <summary>Raised on the UI thread after <see cref="SetWorking"/> changed whether a conversation's agent works, with the conversation and its new state.</summary>
	public event Action<Guid, bool>? WorkingChanged;

	/// <summary>Raised on the UI thread after a conversation was deleted, by a mutator, an applied operation or a <see cref="Load"/> without it, with its identifier.</summary>
	public event Action<Guid>? Deleted;

	/// <summary>Gets or sets where a mirror sends each change it applied, returning whether it was sent; null for a store that is the authority.</summary>
	/// <remarks>
	/// Setting it makes the store a mirror until <see cref="EndMirror"/>: pending changes are written first, and history files are not read or written
	/// meanwhile. The store holds the host's conversations once <see cref="Load"/> gives them.
	/// </remarks>
	public Func<StoreOp, bool>? Forward
	{
		get => _forward;
		set
		{
			if ( value is not null && !_isMirror )
			{
				Flush();
				_isLoaded = true;
				_isMirror = true;
			}
			_forward = value;
		}
	}

	/// <summary>Gets whether the store mirrors another process's history.</summary>
	public bool IsMirror => _isMirror;

	/// <summary>Makes a mirror the authority over its history files again, such as after this PC's daemon stopped and this Loom took its chats back: it
	/// sends nothing any more, no conversation works, and the files the other process wrote are read in place of what it mirrored (<see cref="Reload"/>).</summary>
	/// <remarks>Call it on the UI thread, after <c>ProjectStore.EndMirror</c>. A store that mirrors nothing is left alone; one without files keeps what it mirrored.</remarks>
	public void EndMirror()
	{
		if ( !_isMirror )
			return;

		_forward = null;
		_isMirror = false;
		HostHome = null;
		foreach ( var id in _working.ToArray() )
			SetWorking( id, false );
		Reload();
	}

	/// <summary>Replaces every conversation with a host's snapshot, such as a <c>sync</c> answer; messages already received for conversations that remain are kept,
	/// and <see cref="Deleted"/> is raised for each conversation the snapshot no longer has.</summary>
	public void Load( IEnumerable<ConversationInfo> conversations )
	{
		_isLoaded = true;
		var previous = _conversations.Keys.ToArray();
		_conversations.Clear();
		foreach ( var conversation in conversations )
			_conversations[conversation.Id] = conversation;
		foreach ( var id in _messages.Keys.Where( id => !_conversations.ContainsKey( id ) ).ToArray() )
		{
			_messages.Remove( id );
			_snapshots.Remove( id );
			if ( _messageVersions.Remove( id, out var version ) )
				version.Value++;
		}
		MarkChanged( persist: false );
		foreach ( var id in previous.Where( id => !_conversations.ContainsKey( id ) ) )
			Deleted?.Invoke( id );
	}

	/// <summary>Applies one operation as its mutator would, even in a mirror, and raises <see cref="Mutated"/> when it changed the conversations.</summary>
	/// <exception cref="ArgumentException">The operation is for another store, unknown, host-only, or misses an argument.</exception>
	public void Apply( StoreOp op )
	{
		if ( op.S != StoreOp.Conversations )
			throw new ArgumentException( $"The conversation store cannot apply a {op.S} operation." );

		_isApplying = true;
		try
		{
			switch ( op.Op )
			{
				case nameof( CreateConversation ):
					CreateConversation( op.Require<Guid>( "id" ), op.Read<DateTimeOffset?>( "createdAt" ) ?? DateTimeOffset.UtcNow, op.Require<SettingsProfile>( "profile" ),
						op.Read<Guid?>( "projectId" ), op.Read<string>( "title" ) ?? "New chat", op.Read<bool?>( "hasChat" ) ?? true );
					break;
				case nameof( AddChat ):
					AddChat( op.Require<Guid>( "id" ) );
					break;
				case nameof( RenameConversation ):
					RenameConversation( op.Require<Guid>( "id" ), op.Require<string>( "title" ) );
					break;
				case nameof( UpdateConversation ):
					UpdateConversation( op.Require<Guid>( "id" ), op.Require<string>( "title" ), op.Read<Guid?>( "projectId" ) );
					break;
				case nameof( SetAutomaticTitle ):
					SetAutomaticTitle( op.Require<Guid>( "id" ), op.Require<string>( "title" ), op.Require<bool>( "isGenerated" ) );
					break;
				case nameof( SetPinned ):
					SetPinned( op.Require<Guid>( "id" ), op.Require<bool>( "isPinned" ) );
					break;
				case nameof( SetArchived ):
					SetArchived( op.Require<Guid>( "id" ), op.Require<bool>( "isArchived" ) );
					break;
				case nameof( MoveConversation ):
					MoveConversation( op.Require<Guid>( "id" ), op.Require<Guid>( "target" ) );
					break;
				case nameof( SetTaskCheckOff ):
					SetTaskCheckOff( op.Require<Guid>( "id" ) );
					break;
				case nameof( SaveBackendSession ):
					SaveBackendSession( op.Require<Guid>( "id" ), op.Read<string>( "sessionDirectory" ) );
					break;
				case nameof( SetDirectory ):
					SetDirectory( op.Require<Guid>( "id" ), op.Read<string>( "directory" ) );
					break;
				case nameof( SetWorktree ):
					SetWorktree( op.Require<Guid>( "id" ), op.Require<string>( "worktree" ) );
					break;
				case nameof( ReleaseWorktree ):
					ReleaseWorktree( op.Require<string>( "worktree" ), op.Require<string>( "mainPath" ) );
					break;
				case nameof( SaveDraft ):
					SaveDraft( op.Require<Guid>( "id" ), op.Read<string>( "draft" ) ?? "" );
					break;
				case nameof( DeleteConversation ):
					DeleteConversation( op.Require<Guid>( "id" ) );
					break;
				default:
					throw new ArgumentException( $"The conversation store has no client operation {op.Op}." );
			}
		}
		finally
		{
			_isApplying = false;
		}
	}

	/// <summary>Applies a message a host pushed: appends it, or replaces the message with its identifier; a conversation no longer here ignores it.</summary>
	public void ApplyMessage( Guid conversationId, ConversationMessage message, bool persist )
	{
		if ( Find( conversationId ) is null )
			return;

		if ( EnsureMessages( conversationId ).Any( item => item.Id == message.Id ) )
			UpdateMessage( conversationId, message, persist );
		else
			AppendMessage( conversationId, message, persist );
	}

	/// <summary>Replaces a conversation's messages with the ones a host pushed; a conversation no longer here ignores them.</summary>
	public void ApplyMessages( Guid conversationId, IReadOnlyList<ConversationMessage> messages )
	{
		if ( Find( conversationId ) is not null )
			ReplaceMessages( conversationId, messages );
	}

	/// <summary>Sends a change to the host, while the store mirrors one and is not applying an operation.</summary>
	/// <returns>Whether the change was forwarded.</returns>
	private bool TryForward( string op, object args )
	{
		if ( _forward is not { } forward || _isApplying )
			return false;

		forward( StoreOp.Build( StoreOp.Conversations, op, args ) );
		return true;
	}

	/// <summary>Publishes a change just applied: a mirror sends it to the host, otherwise <see cref="Mutated"/> is raised with it.</summary>
	private void MarkMutated( string op, object args )
	{
		if ( !TryForward( op, args ) )
			Mutated?.Invoke( StoreOp.Build( StoreOp.Conversations, op, args ) );
	}
}
