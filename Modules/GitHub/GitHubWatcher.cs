using Goo;
using Lokn.Core;

namespace Lokn.Modules.GitHub;

/// <summary>Polls GitHub while the module is loaded, keeps the latest snapshot for the dashboard, and posts what changed to the notification feed.</summary>
/// <remarks>
/// Polling never prompts: without a credential it only records that sign-in is needed. The first refresh sets the baseline,
/// so the feed only receives changes noticed after Lokn started. Mutate on the UI thread; loads run in the background.
/// </remarks>
internal sealed class GitHubWatcher : IDisposable
{
	/// <summary>Defines the title GitHub notifications are posted under, which groups them in the feed.</summary>
	internal const string NotificationTitle = "GitHub";

	/// <summary>Defines how many noticed changes the dashboard keeps.</summary>
	private const int MaxUpdates = 50;

	/// <summary>Stores the module's dispatch and lifetime.</summary>
	private readonly ModuleContext _context;

	/// <summary>Reads GitHub.</summary>
	private readonly GitHubClient _client = new();

	/// <summary>Schedules the next refresh after the previous one finishes.</summary>
	private readonly Timer _timer;

	/// <summary>Counts applied refreshes and state changes, so views that read the watcher redraw.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the last successful refresh, or null before the first.</summary>
	private GitHubSnapshot? _snapshot;

	/// <summary>Stores the changes noticed since Lokn started, newest first.</summary>
	private IReadOnlyList<GitHubUpdate> _updates = [];

	/// <summary>Stores the last error or sign-in hint, or empty.</summary>
	private string _status = "";

	/// <summary>Tracks whether a refresh or sign-in is running.</summary>
	private bool _isBusy;

	/// <summary>Tracks whether GitHub needs a credential before anything can show.</summary>
	private bool _needsSignIn;

	/// <summary>Creates the watcher and starts its first refresh.</summary>
	internal GitHubWatcher( ModuleContext context )
	{
		_context = context;
		_timer = new( _ => _context.Post( Refresh ), null, Timeout.Infinite, Timeout.Infinite );
		Refresh();
	}

	/// <summary>Gets the last successful refresh, or null before the first, subscribing the building view.</summary>
	internal GitHubSnapshot? Snapshot => Read( _snapshot );

	/// <summary>Gets the changes noticed since Lokn started, newest first, subscribing the building view.</summary>
	internal IReadOnlyList<GitHubUpdate> Updates => Read( _updates );

	/// <summary>Gets the last error or sign-in hint, or empty, subscribing the building view.</summary>
	internal string Status => Read( _status );

	/// <summary>Gets whether a refresh or sign-in is running, subscribing the building view.</summary>
	internal bool IsBusy => Read( _isBusy );

	/// <summary>Gets whether GitHub needs a credential, subscribing the building view.</summary>
	internal bool NeedsSignIn => Read( _needsSignIn );

	/// <summary>Stops polling and releases the HTTP client.</summary>
	public void Dispose()
	{
		_timer.Dispose();
		_client.Dispose();
	}

	/// <summary>Starts a refresh unless one is running; the next one is scheduled when it finishes.</summary>
	internal void Refresh()
	{
		if ( _isBusy )
			return;

		_isBusy = true;
		MarkChanged();
		var watched = GitHubClient.ParseRepositories( _context.App.Settings.Get( GitHubModule.Repositories ) ) ?? [];
		_ = RefreshAsync( watched );
	}

	/// <summary>Forgets the noticed changes shown on the dashboard; the notification feed keeps its own.</summary>
	internal void ClearUpdates()
	{
		_updates = [];
		MarkChanged();
	}

	/// <summary>Opens Git Credential Manager's sign-in, then refreshes with the new credential.</summary>
	internal void SignIn()
	{
		if ( _isBusy )
			return;

		_isBusy = true;
		_status = "Finish signing in in the window Git Credential Manager opened.";
		MarkChanged();
		_ = SignInAsync();
	}

	/// <summary>Loads GitHub off the UI thread and applies the result, the sign-in need, or the error on the UI thread.</summary>
	private async Task RefreshAsync( string[] watched )
	{
		try
		{
			var snapshot = await _client.LoadAsync( watched, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () => Apply( snapshot ) );
		}
		catch ( Exception error )
		{
			_context.Post( () =>
			{
				_needsSignIn = error is GitHubSignInException;
				_status = error is TaskCanceledException ? "GitHub did not answer in time." : error.Message;
				Finish();
			} );
		}
	}

	/// <summary>Applies a refresh, recording what changed since the previous one and posting each change to the feed.</summary>
	private void Apply( GitHubSnapshot snapshot )
	{
		if ( _snapshot is { } previous && previous.Login == snapshot.Login )
		{
			var updates = GitHubUpdates.Compute( previous, snapshot, DateTimeOffset.Now );
			if ( updates.Count > 0 )
			{
				_updates = [.. updates.Concat( _updates ).Take( MaxUpdates )];
				// The feed shows newest last, so post in reverse to keep this refresh's reading order.
				for ( var index = updates.Count - 1; index >= 0; index-- )
					_context.App.Notifications.Post( new( NotificationTitle, updates[index].Text )
					{
						Link = Uri.TryCreate( updates[index].Url, UriKind.Absolute, out var link ) ? link : null,
						Source = "github",
						Kind = updates[index].Kind,
						Tone = updates[index].Tone switch
						{
							CheckState.Success => NotificationTone.Success,
							CheckState.Failure => NotificationTone.Failure,
							CheckState.Pending => NotificationTone.Attention,
							_ => NotificationTone.None,
						},
						Project = updates[index].Repository.Length > 0 ? updates[index].Repository : null,
					} );
			}
		}

		_snapshot = snapshot;
		_needsSignIn = false;
		_status = "";
		Finish();
	}

	/// <summary>Ends a refresh and schedules the next.</summary>
	private void Finish()
	{
		_isBusy = false;
		MarkChanged();
		_timer.Change( TimeSpan.FromSeconds( _context.App.Settings.Get( GitHubModule.RefreshSeconds ) ), Timeout.InfiniteTimeSpan );
	}

	/// <summary>Runs the sign-in off the UI thread.</summary>
	private async Task SignInAsync()
	{
		string? failure = null;
		try
		{
			await _client.SignInAsync( _context.Lifetime ).ConfigureAwait( false );
		}
		catch ( Exception error )
		{
			failure = error.Message;
		}

		_context.Post( () =>
		{
			_isBusy = false;
			_status = failure ?? "";
			MarkChanged();
			if ( failure is null )
				Refresh();
		} );
	}

	/// <summary>Returns a value after subscribing the building view to changes.</summary>
	private T Read<T>( T value )
	{
		_ = _version.Value;
		return value;
	}

	/// <summary>Redraws the views that read the watcher.</summary>
	private void MarkChanged() => _version.Value++;
}
