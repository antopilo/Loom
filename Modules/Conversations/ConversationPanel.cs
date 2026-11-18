using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;

namespace Lokn.Modules.Conversations;

/// <summary>Shows one conversation, or Home while it has none, and can switch to another conversation in place.</summary>
/// <remarks>
/// Each conversation's view gets its own lifetime, linked to the panel's and cancelled when the panel switches away, so a
/// chat's background work ends with it while the panel, its pane and its place in layouts stay the same.
/// </remarks>
internal sealed class ConversationPanel( PanelContext context, ConversationsModule module, ConversationStore store, Func<PanelContext, ConversationStore, Guid, PanelContent> create ) : PanelContent
{
	/// <summary>Rebuilds the panel when it switches conversation.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Owns the shown conversation's lifetime, or null while Home shows.</summary>
	private CancellationTokenSource? _lifetime;

	/// <summary>Retains Home, created the first time the panel is empty.</summary>
	private HomeContent? _home;

	/// <summary>Gets the shown conversation, or null while Home shows.</summary>
	internal Guid? ConversationId
	{
		get; private set;
	}

	/// <summary>Gets the shown conversation's view, or null while Home shows.</summary>
	internal PanelContent? Content
	{
		get; private set;
	}

	/// <summary>Gets the shown chat, or null while Home or another kind of conversation view shows.</summary>
	internal ChatContent? Chat => Content as ChatContent;

	/// <summary>Gets whether Home shows.</summary>
	internal bool IsEmpty => Content is null;

	/// <summary>Gets whether the shown chat's agent is working, which switching away would stop.</summary>
	internal bool IsBusy => Chat?.IsBusy ?? false;

	/// <summary>Builds the shown conversation, or Home; each is keyed apart so nothing carries over when the panel switches.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		Blob view;
		string key;
		if ( Content is { } content )
			(view, key) = (content.Build(), $"conversation:{ConversationId:N}");
		else
		{
			_home ??= new HomeContent( context, module );
			(view, key) = (_home.Build(), "home");
		}

		return new Container
		{
			Key = key,
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Children = [view],
		};
	}

	/// <summary>Captures the shown conversation for layout restoration; Home captures nothing and restores as Home.</summary>
	public override JsonElement? CaptureState() => Content?.CaptureState();

	/// <summary>Gets the shown conversation's account profile, so a report or page its agent opens uses it too; Home follows the active profile.</summary>
	public override SettingsProfile? Profile => ConversationId is { } id ? store.Find( id )?.Profile : null;

	/// <summary>Releases the shown conversation with the panel.</summary>
	public override void Closed() => ReleaseContent();

	/// <summary>Shows a conversation in place of what the panel showed, which stops a chat that was shown there.</summary>
	/// <param name="conversation">The conversation to show.</param>
	/// <param name="isContinued">Whether a module reload stopped the conversation's agent mid-turn, so it is asked to carry on.</param>
	internal void Load( ConversationInfo conversation, bool isContinued = false )
	{
		ReleaseContent();
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource( context.Lifetime );
		// The view shares the panel's identity, so attention, diffs and layouts still find the panel, with a lifetime of its own.
		var view = new PanelContext( context.App, context.Id, _lifetime.Token, new ConversationPanelInput( conversation.Id, conversation.Profile ) );
		try
		{
			Content = create( view, store, conversation.Id );
		}
		catch
		{
			ReleaseContent();
			throw;
		}

		ConversationId = conversation.Id;
		_version.Value++;
		if ( isContinued && Chat is { } chat )
			view.Post( chat.UpdateContinue );
	}

	/// <summary>Shows Home in place of the shown conversation.</summary>
	internal void Unload()
	{
		ReleaseContent();
		_version.Value++;
	}

	/// <summary>Closes the shown conversation's view and ends its lifetime, withdrawing any request it made for the user.</summary>
	private void ReleaseContent()
	{
		if ( _lifetime is null )
			return;

		var content = Content;
		Content = null;
		ConversationId = null;
		context.ClearAttention();
		try
		{
			_lifetime.Cancel();
			content?.Closed();
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			context.App.ReportError( "Conversation", error );
		}
		finally
		{
			_lifetime.Dispose();
			_lifetime = null;
		}
	}
}
