using Lokn.Core;

namespace Lokn.Modules.GitHub;

/// <summary>Contributes a GitHub panel with the user's pull requests, checks, review requests and workflow runs, posts what changed to the notification
/// feed, and adds a project's pull requests to its brief.</summary>
public sealed class GitHubModule : ILoknModule
{
	/// <summary>Stores the poller while the module runs.</summary>
	private GitHubWatcher? _watcher;

	/// <summary>Defines the registered dashboard panel identifier.</summary>
	public const string DashboardPanelType = "github.dashboard";

	/// <summary>Defines extra repositories whose workflow runs are shown for everyone.</summary>
	public static readonly Setting<string> Repositories = new(
		"github.repositories", "Watched repositories",
		"owner/name entries, separated by spaces or commas.",
		"", "GitHub", value => GitHubClient.ParseRepositories( value ) is not null );

	/// <summary>Defines how often the panel asks GitHub for changes, in seconds.</summary>
	public static readonly Setting<int> RefreshSeconds = new(
		"github.refresh-seconds", "Refresh interval",
		"How often the GitHub panel checks for changes.",
		60, "GitHub", value => value is >= 15 and <= 3600 ) { Unit = "s", Step = 15, Minimum = 15, Maximum = 3600 };

	/// <summary>Gets the module identifier.</summary>
	public string Id => "github";

	/// <summary>Gets the display name.</summary>
	public string Title => "GitHub";

	/// <summary>Registers the settings, the dashboard panel with its automatic palette command, the kinds of notification it posts, and the Pull
	/// requests card of a project's brief.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Repositories )
			.Setting( RefreshSeconds )
			.Panel( "dashboard", "Pull Requests", context => new GitHubContent( context,
				_watcher ?? throw new InvalidOperationException( "The GitHub module is not running." ) ) )
			.Contribute( BriefSection.Point, GitHubBrief.Create( () => _watcher ) );
		// The notification feed draws GitHub's updates with its mark and summarizes them, such as "3 checks passed", without knowing GitHub.
		foreach ( var kind in GitHubKinds.All )
			builder.Contribute( NotificationKind.Point, kind );
	}

	/// <summary>Starts polling GitHub, so its updates reach the notification feed whether or not the panel is open.</summary>
	public void Start( ModuleContext context ) => _watcher = new GitHubWatcher( context );

	/// <summary>Stops polling GitHub.</summary>
	public void Stop()
	{
		_watcher?.Dispose();
		_watcher = null;
	}
}
