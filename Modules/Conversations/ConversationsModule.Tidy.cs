using System.Text;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Describes what tidying a project's chats would do, shown to the user before it runs.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="Stale">The chats it removes: never started, or untouched for <see cref="ConversationsModule.StaleAge"/>.</param>
/// <param name="Renamed">The chats it names afresh from what they are about now: every other started chat the user did not name.</param>
internal sealed record TidyPlan( Guid ProjectId, IReadOnlyList<ConversationInfo> Stale, IReadOnlyList<ConversationInfo> Renamed )
{
	/// <summary>Gets the plan of no project, for when no tidy is being confirmed.</summary>
	public static TidyPlan None { get; } = new( Guid.Empty, [], [] );
}

/// <summary>Tidies a project's chats: removes the stale ones and gives the rest short titles that say what they are about now.</summary>
public sealed partial class ConversationsModule
{
	/// <summary>Defines how long a chat may go without a message before tidying removes it.</summary>
	internal static readonly TimeSpan StaleAge = TimeSpan.FromDays( 14 );

	/// <summary>Defines how many of a chat's latest prompts the title question shows, beside its first.</summary>
	private const int TidyPromptCount = 3;

	/// <summary>Defines how much of each prompt the title question shows, in characters.</summary>
	private const int TidyPromptLength = 300;

	/// <summary>Defines the question that names a project's chats at once, one numbered line each.</summary>
	internal const string TidyQuestion = "The attached file lists numbered chats from one project, each with its current title, the first request and the latest requests. "
		+ "Give each chat a new title for what it is about now, weighing its latest requests most: at most 4 plain words, no emoji, no category prefix, "
		+ "no quotes, no trailing punctuation, and different chats should get distinct titles. Reply with only one line per chat, as <number>: <title>.";

	/// <summary>Holds the projects whose chats are being named, so the sidebar shows it and a second tidy waits.</summary>
	private readonly State<IReadOnlySet<Guid>> _tidying = new( new HashSet<Guid>() );

	/// <summary>Reports whether a project's chats are being named now.</summary>
	internal bool IsTidying( Guid projectId ) => _tidying.Value.Contains( projectId );

	/// <summary>Reports whether tidying may remove a chat: one the user neither pinned, named nor left a draft in, that was never started or saw no message for <see cref="StaleAge"/>.</summary>
	/// <remarks>Tasks without a chat are layouts the user arranged, and are never stale.</remarks>
	internal static bool IsStale( ConversationInfo conversation, DateTimeOffset now ) =>
		conversation is { HasChat: true, IsPinned: false, IsTitleChosen: false, Draft: null }
		&& (IsUnstarted( conversation ) || now - conversation.UpdatedAt >= StaleAge);

	/// <summary>Reports whether a chat never got a prompt: it still has the title it was created with.</summary>
	private static bool IsUnstarted( ConversationInfo conversation ) => conversation is { Title: "New chat", IsTitleChosen: false };

	/// <summary>Works out what tidying a project of the current host would do now; chats open in a panel, working, or selected are never removed.</summary>
	internal TidyPlan ComputeTidy( LoknApplication app, Guid projectId, DateTimeOffset now )
	{
		var host = Hosts.Current;
		var history = host.Store;
		var project = host.Projects.Get( projectId );
		var open = FindPanels( app ).Select( item => item.Panel.Chat ).OfType<ChatRef>().Where( chat => chat.HostId == host.Id ).Select( chat => chat.ConversationId ).ToHashSet();
		var selected = GetSelected( app );
		var stale = new List<ConversationInfo>();
		var renamed = new List<ConversationInfo>();
		foreach ( var conversation in history.Conversations( project.Profile, projectId ) )
		{
			var isInUse = open.Contains( conversation.Id ) || selected == host.Ref( conversation.Id ) || history.IsWorking( conversation.Id );
			if ( !isInUse && IsStale( conversation, now ) )
				stale.Add( conversation );
			else if ( conversation is { HasChat: true, IsTitleChosen: false } && !IsUnstarted( conversation ) )
				renamed.Add( conversation );
		}

		return new( projectId, stale, renamed );
	}

	/// <summary>Tidies a project of the current host as the user confirmed: removes the stale chats they were shown that are still stale, then names the rest afresh in the background.</summary>
	/// <param name="app">The application.</param>
	/// <param name="confirmed">The plan the user confirmed; what changed since is worked out again, so nothing is removed that was not shown.</param>
	internal void Tidy( LoknApplication app, TidyPlan confirmed )
	{
		if ( IsTidying( confirmed.ProjectId ) )
			return;

		var host = Hosts.Current;
		var plan = ComputeTidy( app, confirmed.ProjectId, DateTimeOffset.UtcNow );
		var shown = confirmed.Stale.Select( conversation => conversation.Id ).ToHashSet();
		foreach ( var conversation in plan.Stale.Where( conversation => shown.Contains( conversation.Id ) ) )
			DeleteConversation( app, host.Ref( conversation.Id ) );

		if ( plan.Renamed.Count == 0 || _context is not { } context )
			return;

		_tidying.Value = new HashSet<Guid>( _tidying.Value ) { plan.ProjectId };
		_ = UpdateTitlesAsync( context, plan.ProjectId, host.Store.IsMirror
			? lifetime => RenameOnHostAsync( host, plan.ProjectId, [.. plan.Renamed.Select( conversation => conversation.Id )], lifetime )
			: lifetime => RenameAsync( context, host.Store, host.Projects.Get( plan.ProjectId ), plan.Renamed, lifetime ) );
	}

	/// <summary>Runs a project's renaming, reports a failure in the shell notice and ends the project's tidying state when it ends.</summary>
	/// <remarks>The chats keep their titles on a failure.</remarks>
	private async Task UpdateTitlesAsync( ModuleContext context, Guid projectId, Func<CancellationToken, Task> rename )
	{
		try
		{
			await rename( context.Lifetime ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException ) when ( context.Lifetime.IsCancellationRequested )
		{
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			context.Post( () => context.App.ReportError( "Tidy chats", error ) );
		}
		finally
		{
			context.Post( () =>
			{
				var tidying = new HashSet<Guid>( _tidying.Value );
				tidying.Remove( projectId );
				_tidying.Value = tidying;
			} );
		}
	}

	/// <summary>Has a mirrored host name the chats itself, from its own history files and with its own model; the titles arrive with its changes.</summary>
	private static Task RenameOnHostAsync( ChatHost host, Guid projectId, IReadOnlyList<Guid> conversationIds, CancellationToken cancellationToken ) =>
		(host.RenameChats ?? throw new IOException( $"Not connected to {host.Name}, so its chats were not named." ))( projectId, conversationIds, cancellationToken );

	/// <summary>Names chats of a project of This PC afresh, as a client that mirrors this PC asked: each listed chat of the project the user did not
	/// name; call on the UI thread.</summary>
	/// <param name="projectId">The project.</param>
	/// <param name="conversationIds">The chats to name.</param>
	/// <param name="cancellationToken">Cancels the naming.</param>
	/// <exception cref="InvalidOperationException">This PC's chats mirror another process's, or the module is not running.</exception>
	/// <exception cref="KeyNotFoundException">This PC has no such project.</exception>
	public Task RenameChatsAsync( Guid projectId, IReadOnlyList<Guid> conversationIds, CancellationToken cancellationToken )
	{
		var host = Hosts.Local;
		if ( host.Store.IsMirror || _context is not { } context )
			throw new InvalidOperationException( "This Loom does not hold its chats now, so it cannot name them." );

		var project = host.Projects.Get( projectId );
		var conversations = conversationIds.Select( host.Store.Find ).OfType<ConversationInfo>()
			.Where( conversation => conversation is { HasChat: true, IsTitleChosen: false } && conversation.ProjectId == projectId ).ToArray();
		return conversations.Length == 0 ? Task.CompletedTask : RenameLinkedAsync();

		async Task RenameLinkedAsync()
		{
			using var lifetime = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken, context.Lifetime );
			await RenameAsync( context, host.Store, project, conversations, lifetime.Token ).ConfigureAwait( false );
		}
	}

	/// <summary>Asks the project's model for a title per chat in one question, and applies each to a chat the user still did not name.</summary>
	private static async Task RenameAsync( ModuleContext context, ConversationStore history, ProjectInfo project, IReadOnlyList<ConversationInfo> conversations,
		CancellationToken cancellationToken )
	{
		var prompts = await history.ReadPromptsAsync( [.. conversations.Select( conversation => conversation.Id )], cancellationToken ).ConfigureAwait( false );
		var listed = conversations.Where( conversation => prompts.GetValueOrDefault( conversation.Id ) is { Count: > 0 } ).ToArray();
		if ( listed.Length == 0 )
			return;

		var material = BuildTidyMaterial( project, listed, prompts );
		var agent = context.App.Services.Get<IAgentService>() ?? throw new InvalidOperationException( "Tidying chat titles needs the Omp module." );
		var answer = await agent.AskAsync( project.Profile, TidyQuestion, material, cancellationToken ).ConfigureAwait( false );
		var titles = ReadTidyTitles( answer, listed.Length );
		var applied = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		if ( !context.Post( () =>
		{
			foreach ( var (number, title) in titles )
			{
				if ( history.Find( listed[number - 1].Id ) is { IsTitleChosen: false } current )
					history.SetAutomaticTitle( current.Id, title, isGenerated: true );
			}
			applied.SetResult();
		} ) )
			throw new OperationCanceledException( "Loom is stopping." );
		await applied.Task.ConfigureAwait( false );
	}

	/// <summary>Writes the numbered list the title question reads: each chat's title, its first prompt and its latest ones, each cut short on one line.</summary>
	internal static string BuildTidyMaterial( ProjectInfo project, IReadOnlyList<ConversationInfo> conversations, IReadOnlyDictionary<Guid, IReadOnlyList<string>> prompts )
	{
		var text = new StringBuilder();
		text.Append( "Project: " ).AppendLine( project.Name ).AppendLine();
		for ( var index = 0; index < conversations.Count; index++ )
		{
			var asked = prompts[conversations[index].Id];
			text.Append( index + 1 ).Append( ". Current title: " ).AppendLine( conversations[index].Title );
			text.Append( "   First request: " ).AppendLine( Shorten( asked[0] ) );
			var latest = asked.Skip( 1 ).TakeLast( TidyPromptCount ).ToArray();
			if ( latest.Length > 0 )
			{
				text.AppendLine( "   Latest requests, oldest first:" );
				foreach ( var prompt in latest )
					text.Append( "   - " ).AppendLine( Shorten( prompt ) );
			}
			text.AppendLine();
		}
		return text.ToString();

		// One line, so a prompt cannot pass itself off as another chat's entry.
		static string Shorten( string prompt )
		{
			var line = string.Join( ' ', prompt.Split( (char[]?)null, StringSplitOptions.RemoveEmptyEntries ) );
			return line.Length <= TidyPromptLength ? line : line[..TidyPromptLength] + "…";
		}
	}

	/// <summary>Reads the answer's <c>number: title</c> lines, keeping the first title for each number from 1 to <paramref name="count"/>, cleaned as a generated title.</summary>
	internal static IReadOnlyDictionary<int, string> ReadTidyTitles( string answer, int count )
	{
		var titles = new Dictionary<int, string>();
		foreach ( var line in answer.Split( '\n' ) )
		{
			if ( TidyLine().Match( line ) is { Success: true } match && int.TryParse( match.Groups[1].Value, out var number ) && number >= 1 && number <= count
				&& ConversationStore.ComputeGeneratedTitle( match.Groups[2].Value ) is { Length: > 0 } title )
				titles.TryAdd( number, title );
		}
		return titles;
	}

	/// <summary>Matches one answer line: a chat's number, optionally bulleted or bold, then a colon, dot or parenthesis and its title.</summary>
	[GeneratedRegex( @"^\W*?(\d+)\W*?[:.)]\s*(.+)$" )]
	private static partial Regex TidyLine();
}
