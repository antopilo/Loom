using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Shows one conversation of any host, or Home while it has none, and can switch to another conversation in place.</summary>
/// <remarks>
/// Each conversation's view gets its own lifetime, linked to the panel's and cancelled when the panel switches away, so the
/// view's background work ends with it while the panel, its pane and its place in layouts stay the same. The chat's agent runs
/// in its host's <see cref="ChatSession"/>, which stops once no panel shows it and it is idle. A paired host's chat whose host has not
/// sent its chats yet waits in the panel, which loads it once they arrive.
/// </remarks>
internal sealed class ConversationPanel( PanelContext context, ConversationsModule module, Func<PanelContext, ChatHost, Guid, PanelContent> create ) : PanelContent
{
	/// <summary>Rebuilds the panel when it switches conversation.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Owns the shown conversation's lifetime, or null while Home shows.</summary>
	private CancellationTokenSource? _lifetime;

	/// <summary>Retains Home, created the first time the panel is empty.</summary>
	private HomeContent? _home;

	/// <summary>Gets the shown chat, or null while Home shows or the panel waits for its chat.</summary>
	internal ChatRef? Chat
	{
		get; private set;
	}

	/// <summary>Gets the shown conversation, or null while Home shows or the panel waits for its chat.</summary>
	internal Guid? ConversationId => Chat?.ConversationId;

	/// <summary>Gets the input of the chat the panel waits for, its host's chats not here yet, or null.</summary>
	internal ConversationPanelInput? Waiting
	{
		get; private set;
	}

	/// <summary>Gets the shown conversation's view, or null while Home shows.</summary>
	internal PanelContent? Content
	{
		get; private set;
	}

	/// <summary>Gets the shown chat's view, or null while Home or another kind of conversation view shows.</summary>
	internal ChatContent? View => Content as ChatContent;

	/// <summary>Gets whether Home shows.</summary>
	internal bool IsEmpty => Content is null && Waiting is null;

	/// <summary>Gets whether the shown chat's agent is working, which switching away would stop.</summary>
	internal bool IsBusy => View?.IsBusy ?? false;

	/// <summary>Gets the host the panel works on: the shown or awaited chat's, else, for Home, the host the user is on.</summary>
	public override string? HostId => Chat?.HostId ?? Waiting?.Host ?? context.App.Hosts.Current.Id;

	/// <summary>Builds the shown conversation, the wait for it, or Home; each is keyed apart so nothing carries over when the panel switches.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		Blob view;
		string key;
		if ( Content is { } content )
			(view, key) = (content.Build(), $"conversation:{Chat}");
		else if ( Waiting is { } waiting )
			(view, key) = (BuildWaiting( waiting ), "waiting");
		else
		{
			_home ??= new HomeContent( context, module );
			(view, key) = (_home.Build(), "home");
		}

		var children = new List<Blob> { view };
		if ( HostFolderPrompt.Build( context ) is { } prompt )
			children.Add( prompt );
		return new Container
		{
			Key = key,
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Children = children,
		};
	}

	/// <summary>Captures the shown conversation for layout restoration, or the awaited one as it was restored; Home captures nothing and restores as Home.</summary>
	public override JsonElement? CaptureState() =>
		Waiting is { } waiting ? JsonSerializer.SerializeToElement( waiting ) : Content?.CaptureState();

	/// <summary>Gets the shown conversation's account profile, from its host's history, so a report or page its agent opens uses it too; Home follows the active profile.</summary>
	public override SettingsProfile? Profile => Chat is { } chat ? module.Hosts.Find( chat )?.Conversation.Profile : Waiting?.Profile;

	/// <summary>Releases the shown conversation with the panel.</summary>
	public override void Closed() => ReleaseContent();

	/// <summary>Shows a conversation in place of what the panel showed, which stops a chat that was shown there.</summary>
	/// <param name="chat">The conversation to show and its host.</param>
	/// <param name="isContinued">Whether a module reload stopped the conversation's agent mid-turn, so it is asked to carry on.</param>
	internal void Load( HostedChat chat, bool isContinued = false )
	{
		ReleaseContent();
		Waiting = null;
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource( context.Lifetime );
		// The view shares the panel's identity, so attention, diffs and layouts still find the panel, with a lifetime of its own and its chat's host.
		var view = new PanelContext( context.App, context.Id, _lifetime.Token, ConversationsModule.BuildInput( chat.Ref, chat.Conversation.Profile ), hostId: chat.Host.Id );
		try
		{
			Content = create( view, chat.Host, chat.Conversation.Id );
		}
		catch
		{
			ReleaseContent();
			throw;
		}

		Chat = chat.Ref;
		_version.Value++;
		if ( isContinued && View is { } shown )
			view.Post( shown.UpdateContinue );
	}

	/// <summary>Waits for a paired host's chat whose host has not sent its chats yet, or is not paired yet; the Conversations module loads it once they arrive,
	/// or shows Home when the host does not have it.</summary>
	/// <param name="chat">The awaited chat.</param>
	/// <param name="input">The input the panel was restored or opened with, which it saves while it waits.</param>
	internal void Wait( ChatRef chat, ConversationPanelInput input )
	{
		ReleaseContent();
		Waiting = input with { Host = chat.IsLocal ? null : chat.HostId };
		_version.Value++;
	}

	/// <summary>Shows Home in place of the shown or awaited conversation.</summary>
	internal void Unload()
	{
		ReleaseContent();
		Waiting = null;
		_version.Value++;
	}

	/// <summary>Builds the line shown while the panel waits for its chat: the host is being reached, or is offline.</summary>
	private Blob BuildWaiting( ConversationPanelInput waiting )
	{
		var host = context.App.Hosts.Get( waiting.Host );
		var theme = Theme.Current;
		var lines = new List<Blob>
		{
			new Text
			{
				Key = "waiting-title",
				Content = host.State is LoknHostState.Connecting ? $"Waiting for {host.Name}…" : $"{host.Name} is offline",
				FontWeight = 600,
				Color = theme.Text,
				TextAlign = TextAlign.Center,
			},
		};
		if ( host.StatusText.Length > 0 )
			lines.Add( new Text { Key = "waiting-status", Content = host.StatusText, Color = theme.Muted, TextAlign = TextAlign.Center } );

		return new Column
		{
			Key = "conversation-waiting",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"Waiting for {host.Name}",
			FlexGrow = 1,
			MinHeight = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Gap = theme.Spacing,
			Padding = theme.Spacing * 2,
			Children = lines,
		};
	}

	/// <summary>Closes the shown conversation's view and ends its lifetime, withdrawing any request it made for the user.</summary>
	private void ReleaseContent()
	{
		if ( _lifetime is null )
			return;

		var content = Content;
		Content = null;
		Chat = null;
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
