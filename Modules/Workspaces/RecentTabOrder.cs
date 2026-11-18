namespace Lokn.Modules.Workspaces;

/// <summary>Keeps the tab strip's order stable, so tabs do not move under the pointer.</summary>
/// <remarks>
/// The strip takes the recency order from <see cref="RecentTabs.ComputeShown"/> on its first build and whenever the current tab
/// changes by something other than the strip itself, such as the sidebar, the Search palette or a command. Otherwise, such as after
/// a click on one of its tabs or +, or while chats work in the background, groups and tabs keep their places, those that leave are
/// dropped, and those newly shown are added at the end, of the strip or of their group.
/// </remarks>
public sealed class RecentTabOrder
{
	/// <summary>Stores the group keys in their last shown order, or null before the first arrangement.</summary>
	private List<string>? _groups;

	/// <summary>Stores each shown group's tabs in their last shown order.</summary>
	private readonly Dictionary<string, List<Guid>> _tabs = new( StringComparer.Ordinal );

	/// <summary>Stores the current tab at the last arrangement.</summary>
	private Guid? _current;

	/// <summary>Tracks whether the strip's own action ran since the last arrangement.</summary>
	private bool _isOwnAction;

	/// <summary>Records that the strip itself is changing the current tab, so the next arrangement keeps the order.</summary>
	public void MarkOwnAction() => _isOwnAction = true;

	/// <summary>Orders the shown groups and tabs: by recency when the current tab changed elsewhere, otherwise in their last places.</summary>
	/// <param name="shown">The groups and tabs to show, in recency order, from <see cref="RecentTabs.ComputeShown"/>.</param>
	/// <param name="currentTab">The current tab, or null.</param>
	public IReadOnlyList<RecentTabGroup> Arrange( IReadOnlyList<RecentTabGroup> shown, Guid? currentTab )
	{
		var isStable = _groups is not null && (currentTab == _current || _isOwnAction);
		_isOwnAction = false;
		_current = currentTab;
		var arranged = shown;
		if ( isStable )
		{
			// OrderBy is stable, so items not shown before keep their recency order after the known ones.
			arranged = [.. shown
				.OrderBy( group => Rank( _groups!, group.Key ) )
				.Select( group => group with
				{
					Tabs = _tabs.TryGetValue( group.Key, out var known ) ? [.. group.Tabs.OrderBy( tab => Rank( known, tab.Id ) )] : group.Tabs,
				} )];
		}

		_groups = [.. arranged.Select( group => group.Key )];
		_tabs.Clear();
		foreach ( var group in arranged )
			_tabs[group.Key] = [.. group.Tabs.Select( tab => tab.Id )];
		return arranged;
	}

	/// <summary>Gets an item's place in a remembered order, or past the end when it is new.</summary>
	private static int Rank<T>( List<T> order, T item )
	{
		var index = order.IndexOf( item );
		return index < 0 ? int.MaxValue : index;
	}
}
