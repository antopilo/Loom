using System.Text.Json;
using Goo;

namespace Lokn.Core;

/// <summary>Keeps the recent notifications shown under the titlebar bell, shared by every profile and saved across restarts.</summary>
/// <remarks>
/// Items are newest first and capped at <see cref="Capacity"/>. Posting only records a notification; the desktop chimes and
/// shows Windows notifications for panels that request attention, and records those requests here. Mutate on the UI thread.
/// </remarks>
public sealed class NotificationFeed
{
	/// <summary>Defines how many notifications are kept; older ones are dropped when a new one arrives.</summary>
	public const int Capacity = 200;

	/// <summary>Defines the supported feed file format version.</summary>
	private const int FileVersion = 1;

	/// <summary>Defines readable JSON for the feed file.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
	};

	/// <summary>Stores the notifications, newest first.</summary>
	private readonly List<LoknNotification> _items = [];

	/// <summary>Tracks changes read by retained Goo views.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the optional feed file path; null keeps tests and headless apps in memory.</summary>
	private readonly string? _path;

	/// <summary>Tracks whether the loaded file can safely be replaced, false after it failed to parse.</summary>
	private bool _canSave = true;

	/// <summary>Gets the per-user feed file path.</summary>
	public static string DefaultPath => Path.Combine(
		Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ),
		"Lokn",
		"notifications.json" );

	/// <summary>Gets the notifications, newest first, and subscribes the building view to changes.</summary>
	public IReadOnlyList<LoknNotification> Items
	{
		get
		{
			_ = _version.Value;
			return _items.ToArray();
		}
	}

	/// <summary>Gets how many notifications are unread, and subscribes the building view to changes.</summary>
	public int UnreadCount
	{
		get
		{
			_ = _version.Value;
			return _items.Count( item => !item.IsRead );
		}
	}

	/// <summary>Reports a load or save failure to the owning application.</summary>
	internal event Action<string>? Failed;

	/// <summary>Reports each notification after it is recorded, so the desktop can announce it.</summary>
	public event Action<LoknNotification>? Posted;

	/// <summary>Gets the load failure, or null when the feed loaded or had no file.</summary>
	internal string? LoadError
	{
		get; private set;
	}

	/// <summary>Creates an in-memory feed or loads the explicitly named local file.</summary>
	public NotificationFeed( string? path = null )
	{
		_path = path is null ? null : Path.GetFullPath( path );
		EnsureLoaded();
	}

	/// <summary>Records an unread notification at the top of the feed.</summary>
	/// <param name="request">What to show and what opening it does.</param>
	public LoknNotification Post( NotificationRequest request )
	{
		ArgumentNullException.ThrowIfNull( request );
		ArgumentNullException.ThrowIfNull( request.Title );
		ArgumentNullException.ThrowIfNull( request.Message );
		var notification = new LoknNotification( Guid.NewGuid(), DateTimeOffset.Now, request.Title, request.Message, request.PanelId, IsRead: false, request.Link?.AbsoluteUri, request.Source )
		{
			Kind = request.Kind,
			Actions = request.Actions.Count == 0 ? null : [.. request.Actions],
		};
		_items.Insert( 0, notification );
		if ( _items.Count > Capacity )
			_items.RemoveRange( Capacity, _items.Count - Capacity );

		UpdateChanged();
		Posted?.Invoke( notification );
		return notification;
	}

	/// <summary>Marks one notification read.</summary>
	/// <returns>False when it does not exist or was already read.</returns>
	public bool MarkRead( Guid id ) => UpdateRead( item => item.Id == id ) > 0;

	/// <summary>Marks every unread notification of a panel read, such as when the user looks at the panel.</summary>
	/// <returns>How many notifications changed.</returns>
	public int MarkPanelRead( Guid panelId ) => UpdateRead( item => item.PanelId == panelId );

	/// <summary>Marks every notification read.</summary>
	/// <returns>How many notifications changed.</returns>
	public int MarkAllRead() => UpdateRead( _ => true );

	/// <summary>Removes one notification.</summary>
	/// <returns>False when it does not exist.</returns>
	public bool Remove( Guid id )
	{
		if ( _items.RemoveAll( item => item.Id == id ) == 0 )
			return false;

		UpdateChanged();
		return true;
	}

	/// <summary>Removes every notification.</summary>
	public void Clear()
	{
		if ( _items.Count == 0 )
			return;

		_items.Clear();
		UpdateChanged();
	}

	/// <summary>Marks the unread notifications that match read, saving once when any changed.</summary>
	private int UpdateRead( Func<LoknNotification, bool> isMatch )
	{
		var changed = 0;
		for ( var index = 0; index < _items.Count; index++ )
		{
			if ( _items[index].IsRead || !isMatch( _items[index] ) )
				continue;

			_items[index] = _items[index] with { IsRead = true };
			changed++;
		}

		if ( changed > 0 )
			UpdateChanged();
		return changed;
	}

	/// <summary>Saves the feed and refreshes the views that read it.</summary>
	private void UpdateChanged()
	{
		EnsureSaved();
		_version.Value++;
	}

	/// <summary>Loads the saved feed; a file that fails to parse is left untouched and never overwritten this session.</summary>
	private void EnsureLoaded()
	{
		if ( _path is null || !File.Exists( _path ) )
			return;

		try
		{
			var document = JsonSerializer.Deserialize<FeedDocument>( File.ReadAllText( _path ), JsonOptions )
				?? throw new JsonException( "The notification feed is empty." );
			if ( document.Version != FileVersion || document.Items is null )
				throw new JsonException( "The notification feed has an unsupported version or no items." );

			_items.AddRange( document.Items.OrderByDescending( item => item.Time ).Take( Capacity ) );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			_canSave = false;
			// The application subscribes after construction, so it reads a load failure from here.
			LoadError = $"Could not load {_path}: {error.Message} The original file is unchanged; notifications stay in memory until it is corrected and Lokn restarts.";
		}
	}

	/// <summary>Writes through a sibling temporary file so a failed write keeps the previous feed.</summary>
	private void EnsureSaved()
	{
		if ( _path is null || !_canSave )
			return;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( _path )! );
			var temporary = _path + ".tmp";
			File.WriteAllText( temporary, JsonSerializer.Serialize( new FeedDocument { Items = _items }, JsonOptions ) );
			File.Move( temporary, _path, overwrite: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			Failed?.Invoke( $"Notifications could not be saved to {_path}: {error.Message}" );
		}
	}

	/// <summary>Stores the notifications in the versioned file format.</summary>
	private sealed record FeedDocument
	{
		/// <summary>Gets the feed file format version.</summary>
		public int Version { get; init; } = FileVersion;

		/// <summary>Gets the notifications, newest first.</summary>
		public List<LoknNotification>? Items { get; init; } = [];
	}
}

/// <summary>Describes one notification in the feed.</summary>
/// <param name="Id">Identifies the notification.</param>
/// <param name="Time">When it was posted, in local time.</param>
/// <param name="Title">Who or what it comes from, such as a conversation title or GitHub.</param>
/// <param name="Message">What happened or what is needed.</param>
/// <param name="PanelId">The panel opening it shows, or null.</param>
/// <param name="IsRead">Whether the user opened it, looked at its panel, or marked it read.</param>
/// <param name="Url">The link opening it follows when it has no open panel, or null.</param>
/// <param name="Source">What posted it: a panel type such as <c>conversations.chat</c>, or a module identifier such as <c>github</c>; null for notifications saved before sources were kept.</param>
public sealed record LoknNotification( Guid Id, DateTimeOffset Time, string Title, string Message, Guid? PanelId, bool IsRead, string? Url = null, string? Source = null )
{
	/// <summary>Gets what kind of event it is, such as <c>github.checks-failed</c>, which views use to pick its icon; null when the poster gave none.</summary>
	public string? Kind { get; init; }

	/// <summary>Gets the buttons shown with it, or null when it has none.</summary>
	public IReadOnlyList<NotificationAction>? Actions { get; init; }
}

/// <summary>Describes a notification to post with <see cref="NotificationFeed.Post"/>.</summary>
/// <param name="Title">Who or what it comes from, such as a conversation title or GitHub; the feed groups consecutive notifications by it.</param>
/// <param name="Message">What happened or what is needed.</param>
public sealed record NotificationRequest( string Title, string Message )
{
	/// <summary>Gets the panel opening it shows, or null.</summary>
	public Guid? PanelId { get; init; }

	/// <summary>Gets the link opening it follows when it has no open panel: a web page, or a module's route made with <see cref="LoknLinks.Create"/>.</summary>
	public Uri? Link { get; init; }

	/// <summary>Gets what posted it: a panel type such as <c>conversations.chat</c>, or a module identifier such as <c>github</c>.</summary>
	public string? Source { get; init; }

	/// <summary>Gets what kind of event it is, <c>module.name</c>; a module contributes the kind's icon to <see cref="NotificationKind.Point"/>.</summary>
	public string? Kind { get; init; }

	/// <summary>Gets the buttons shown with it, each following its link.</summary>
	public IReadOnlyList<NotificationAction> Actions { get; init; } = [];
}

/// <summary>A button on a notification.</summary>
/// <param name="Label">The button text.</param>
/// <param name="Link">The link it follows: a web page, or a module's route made with <see cref="LoknLinks.Create"/>.</param>
public sealed record NotificationAction( string Label, Uri Link );

/// <summary>Describes a kind of notification for the notification views: its icon, whether it is a short update, and how a run of them reads; contribute it to <see cref="Point"/>.</summary>
/// <remarks>A module ships its own icon and wording this way, so the views know nothing about it.</remarks>
/// <param name="Kind">The kind, as posted in <see cref="NotificationRequest.Kind"/>, or a source such as <c>github</c> or a panel type's module such as <c>terminal</c> to cover every notification it posts.</param>
/// <param name="Glyph">A Segoe Fluent Icons character drawn as the icon when <see cref="Icon"/> is null.</param>
public sealed record NotificationKind( string Kind, string Glyph )
{
	/// <summary>The extension point the Notifications module reads kinds from.</summary>
	public const string Point = "notifications.kinds";

	/// <summary>Gets a vector icon, such as a logo, filled with the text color; null draws <see cref="Glyph"/>.</summary>
	public VectorPath? Icon { get; init; }

	/// <summary>Gets whether notifications of this kind are short updates, which fold with their neighbors into one summary line instead of standing alone.</summary>
	public bool IsUpdate { get; init; }

	/// <summary>Gets how a run of this kind reads in a summary line, given how many there are and the first, such as <c>3 checks passed</c>; null reads the message, or "N updates from …".</summary>
	public Func<int, LoknNotification, string>? Summarize { get; init; }
}

/// <summary>Opens the notifications a module knows how to show better than activating their panel, such as a chat in its own workspace; contribute it to <see cref="Point"/>.</summary>
/// <param name="Open">Shows what the notification is about and returns true, or returns false to leave it to the next opener; the notification is already marked read.</param>
public sealed record NotificationOpener( Func<LoknApplication, LoknNotification, bool> Open )
{
	/// <summary>The extension point the Notifications module reads openers from.</summary>
	public const string Point = "notifications.openers";
}
