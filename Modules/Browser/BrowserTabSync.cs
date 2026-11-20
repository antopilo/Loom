using Lokn.Core;

namespace Lokn.Modules.Browser;

/// <summary>What a view's page shows, as a view reports it to the session.</summary>
/// <param name="Address">The page's address.</param>
/// <param name="Title">The page's title.</param>
/// <param name="IsLoading">Whether the page loads.</param>
/// <param name="Progress">The loading progress, from 0 to 100.</param>
/// <param name="Failed">How the page's latest navigation failed, or null.</param>
/// <param name="CanGoBack">Whether the page's history can go back.</param>
/// <param name="CanGoForward">Whether the page's history can go forward.</param>
internal readonly record struct BrowserPageState( string Address, string Title, bool IsLoading, int Progress, string? Failed, bool CanGoBack, bool CanGoForward );

/// <summary>Says what made a view report its page.</summary>
internal enum BrowserPageEvent
{
	/// <summary>The page's state changed, such as its title or progress; its address moves nothing.</summary>
	State,

	/// <summary>The page's address changed, such as a navigation committing.</summary>
	Commit,

	/// <summary>The page's current navigation ended, which may have left the page where it was, such as a download.</summary>
	Complete,
}

/// <summary>Keeps one view's page and its session tab in step, apart from WebView2 so checks can drive it: which address the page loads to show the tab,
/// which of the page's navigations are the view's own and so move the tab, and what the view reports (docs/remote.md, Browser frames).</summary>
/// <remarks>
/// A view owns the tab's address when it set it last, or when no view did yet (<see cref="BrowserTabInfo.By"/>). A navigation its page starts is its own
/// when the user, an agent or the page started it, unless it goes to the address the view loaded to follow another view: such a followed load, with its
/// redirects, leaves the tab where it is, so two windows whose cookies redirect differently never pull each other back and forth. A view follows each
/// address another view sets once, so a page that was redirected away from it is not loaded again at every change of the tab's title or progress.
/// Within a followed page, an address change that keeps the document, such as a script's history entry, is the view's own only once the user activated
/// the page. A view reports its page's state only while the page shows the tab's address, and a new address only from its own navigation.
/// </remarks>
/// <param name="tabs">The session's tabs.</param>
/// <param name="id">The session tab the view shows.</param>
internal sealed class BrowserTabSync( BrowserTabs tabs, Guid id )
{
	/// <summary>Stores the address another view set that this view loaded last to follow it, or null after a navigation of its own.</summary>
	private string? _followed;

	/// <summary>Gets this view's identity in the session, so the session tells its own address changes from other windows'.</summary>
	internal string ViewId { get; } = Guid.NewGuid().ToString( "N" );

	/// <summary>Gets whether the page's current navigation is the view's own, so its address may move the tab.</summary>
	internal bool IsOwnLoad { get; private set; }

	/// <summary>Gets the session tab, or null while a mirror has not received it yet.</summary>
	internal BrowserTabInfo? Info => tabs.Find( id );

	/// <summary>Returns the address the page loads to show the tab: always when it starts, and later when another view set an address the page does not
	/// show and this view has not followed yet; null when it needs no load, or this PC may not load the address (<see cref="BrowserTabs.CanShow"/>).</summary>
	/// <param name="pageAddress">The address the page shows now.</param>
	/// <param name="isStarting">Whether the page is starting and shows nothing yet.</param>
	internal string? ComputeLoad( string pageAddress, bool isStarting )
	{
		if ( Info is not { Url.Length: > 0 } info || !tabs.CanShow( info.Url ) )
			return null;
		if ( !isStarting && (info.By == ViewId || pageAddress == info.Url || info.Url == _followed) )
			return null;

		IsOwnLoad = false;
		_followed = info.By is not null && info.By != ViewId ? info.Url : null;
		return info.Url;
	}

	/// <summary>Sends the tab to an address as this view's own navigation, such as one typed or an agent's.</summary>
	internal void Navigate( string address )
	{
		_followed = null;
		tabs.Navigate( id, address, ViewId );
	}

	/// <summary>Records a navigation the page starts and returns whether it is the view's own; a redirect continues its navigation.</summary>
	/// <param name="address">Where the navigation goes.</param>
	/// <param name="isRedirected">Whether the navigation is a redirect of the one before.</param>
	/// <param name="isUserInitiated">Whether a user gesture or this window's own call started it, as WebView2 counts it.</param>
	internal bool BeginNavigation( string address, bool isRedirected, bool isUserInitiated )
	{
		if ( isRedirected )
			return IsOwnLoad;

		if ( _followed is { } followed && Normalize( address ) == Normalize( followed ) )
		{
			IsOwnLoad = false;
			return false;
		}

		_followed = null;
		IsOwnLoad = isUserInitiated || IsOwner;
		return IsOwnLoad;
	}

	/// <summary>Makes the page's address changes the view's own after the user activated the page, such as clicking a link a script handles.</summary>
	internal void TakeOver()
	{
		_followed = null;
		IsOwnLoad = true;
	}

	/// <summary>Reports the page's state: with its address after a commit of the view's own navigation, or after its own navigation to an address it set
	/// itself ended without replacing the page; otherwise only while the page shows the tab's address.</summary>
	/// <param name="page">What the page shows.</param>
	/// <param name="happened">What made the view report.</param>
	internal void Report( BrowserPageState page, BrowserPageEvent happened )
	{
		if ( Info is not { } info )
			return;

		var isMoved = page.Address != info.Url;
		var canMove = happened switch
		{
			BrowserPageEvent.Commit => IsOwnLoad,
			BrowserPageEvent.Complete => IsOwnLoad && info.By == ViewId,
			_ => false,
		};
		if ( isMoved && (!canMove || !tabs.CanShow( page.Address )) )
			return;

		tabs.Report( id, ViewId, page.Address, page.Title, page.IsLoading, page.Progress, page.Failed, page.CanGoBack, page.CanGoForward, isMoved );
	}

	/// <summary>Gets whether this view owns the tab's address: it set it last, or no view did yet.</summary>
	private bool IsOwner => Info is { } info && (info.By is null || info.By == ViewId);

	/// <summary>Returns an address in the form a page names it, so a load can be told by its address.</summary>
	private static string Normalize( string address ) => Uri.TryCreate( address, UriKind.Absolute, out var uri ) ? uri.AbsoluteUri : address;
}
