using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Keeps one <see cref="IChatSession"/> per conversation for as long as the Conversations module runs, whether or not a panel shows it.</summary>
/// <remarks>
/// Sessions are local <see cref="ChatSession"/>s that run the agent in this process unless <see cref="Source"/> supplies them, as the Remote
/// module does with <see cref="RemoteChatSession"/>s in its client roles. The service follows every session's questions and requests for the user,
/// so they reach the user while no panel shows the chat.
/// </remarks>
/// <param name="app">The application the sessions use.</param>
/// <param name="store">The history the sessions record in.</param>
/// <param name="backend">Gets the Omp service that starts the sessions' processes, read when a session is created.</param>
/// <param name="post">Posts an action to the UI thread while the module runs and reports whether it was accepted.</param>
public sealed class ChatSessionService( LoknApplication app, ConversationStore store, Func<OmpService> backend, Func<Action, bool> post ) : IDisposable
{
	/// <summary>Maps each conversation to its session and the handlers that follow it.</summary>
	private readonly Dictionary<Guid, Followed> _sessions = [];

	/// <summary>Changes when a session's pending questions change, or a session is created or forgotten, so readers of <see cref="Waiting"/> rebuild.</summary>
	private readonly State<int> _dialogVersion = new( 0 );

	/// <summary>Stores what creates a conversation's session instead of a local one, or null.</summary>
	private Func<Guid, IChatSession>? _source;

	/// <summary>Raised on the UI thread when a session needs the user, with the session, the conversation's title and what it needs.</summary>
	public event Action<IChatSession, string, string>? AttentionRequested;

	/// <summary>Raised on the UI thread when a session works on alone and withdraws its request for the user.</summary>
	public event Action<IChatSession>? AttentionCleared;

	/// <summary>Raised on the UI thread after <see cref="Source"/> changed and every session was stopped, so views showing an old session take a new one.</summary>
	public event Action? SourceChanged;

	/// <summary>Raised on the UI thread when a session's pending questions change, or a session is created or forgotten, so <see cref="Waiting"/> may differ.</summary>
	public event Action? WaitingChanged;

	/// <summary>Gets or sets what creates a conversation's session instead of a local <see cref="ChatSession"/>, or null for local sessions.</summary>
	/// <remarks>Changing it stops and forgets every session, then raises <see cref="SourceChanged"/>; the Conversations module reopens its chat panels on it.</remarks>
	public Func<Guid, IChatSession>? Source
	{
		get => _source;
		set
		{
			if ( _source == value )
				return;

			_source = value;
			Dispose();
			SourceChanged?.Invoke();
		}
	}

	/// <summary>Gets the sessions created so far.</summary>
	public IReadOnlyCollection<IChatSession> Sessions => [.. _sessions.Values.Select( followed => followed.Session )];

	/// <summary>Gets the sessions whose agent waits on a question; reading it during a build rebuilds the reader when any session's questions change.</summary>
	public IReadOnlyList<IChatSession> Waiting
	{
		get
		{
			_ = _dialogVersion.Value;
			return [.. _sessions.Values.Select( followed => followed.Session ).Where( session => session.PendingDialog is not null )];
		}
	}

	/// <summary>Gets a conversation's session, creating it on first use.</summary>
	public IChatSession GetOrCreate( Guid conversationId )
	{
		if ( _sessions.TryGetValue( conversationId, out var followed ) )
			return followed.Session;

		var session = _source?.Invoke( conversationId ) ?? new ChatSession( app, store, conversationId, backend(), post );
		followed = new( session, MarkDialogsChanged, ( title, message ) => AttentionRequested?.Invoke( session, title, message ), () => AttentionCleared?.Invoke( session ) );
		session.DialogsChanged += followed.Dialogs;
		session.AttentionRequested += followed.Requested;
		session.AttentionCleared += followed.Cleared;
		_sessions.Add( conversationId, followed );
		MarkDialogsChanged();
		return session;
	}

	/// <summary>Gets a conversation's session, or null when none was created.</summary>
	public IChatSession? Find( Guid conversationId ) => _sessions.GetValueOrDefault( conversationId )?.Session;

	/// <summary>Stops and forgets a conversation's session, such as when the conversation is deleted.</summary>
	public void Remove( Guid conversationId )
	{
		if ( !_sessions.Remove( conversationId, out var followed ) )
			return;

		Release( followed );
		MarkDialogsChanged();
	}

	/// <summary>Stops every session, when the module stops or the source changes.</summary>
	public void Dispose()
	{
		if ( _sessions.Count == 0 )
			return;

		foreach ( var followed in _sessions.Values )
			Release( followed );
		_sessions.Clear();
		MarkDialogsChanged();
	}

	/// <summary>Stops following a session and stops it.</summary>
	private static void Release( Followed followed )
	{
		followed.Session.DialogsChanged -= followed.Dialogs;
		followed.Session.AttentionRequested -= followed.Requested;
		followed.Session.AttentionCleared -= followed.Cleared;
		followed.Session.Dispose();
	}

	/// <summary>Rebuilds the readers of <see cref="Waiting"/> and tells <see cref="WaitingChanged"/>.</summary>
	private void MarkDialogsChanged()
	{
		_dialogVersion.Value++;
		WaitingChanged?.Invoke();
	}

	/// <summary>One session and the handlers the service follows it with.</summary>
	/// <param name="Session">The session.</param>
	/// <param name="Dialogs">Handles <see cref="IChatSession.DialogsChanged"/>.</param>
	/// <param name="Requested">Handles <see cref="IChatSession.AttentionRequested"/>.</param>
	/// <param name="Cleared">Handles <see cref="IChatSession.AttentionCleared"/>.</param>
	private sealed record Followed( IChatSession Session, Action Dialogs, Action<string, string> Requested, Action Cleared );
}
