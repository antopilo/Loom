using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Describes one tab of the tab strip, such as a task or a chat.</summary>
/// <param name="Id">The stable identifier, unique across groups.</param>
/// <param name="Title">The tab's text.</param>
/// <param name="IsPinned">Whether the user pinned it, which keeps it shown however old.</param>
/// <param name="LastUsed">When the user last used it; the current tab counts as used now.</param>
public sealed record RecentTab( Guid Id, string Title, bool IsPinned, DateTimeOffset LastUsed )
{
	/// <summary>Gets marks drawn after the title, such as an activity orb.</summary>
	public IReadOnlyList<Blob> Marks { get; init; } = [];

	/// <summary>Gets the accessible name, or null for the title.</summary>
	public string? AccessibilityName { get; init; }
}

/// <summary>Holds one group of the tab strip, such as a project's tasks or chats.</summary>
/// <param name="Key">The stable group key, such as a session key or a project's identifier.</param>
/// <param name="Tabs">The group's tabs; after <see cref="RecentTabs.ComputeShown"/>, the ones it shows, most recently used first.</param>
public sealed record RecentTabGroup( string Key, IReadOnlyList<RecentTab> Tabs );

/// <summary>Picks, orders and builds the tab strip's groups: each group's label, its tabs inside a shared tinted background, and its +.</summary>
/// <remarks>
/// The strip shows what the user actually uses: the current tab, pinned tabs and tabs used within <see cref="RecentWindow"/>, at
/// most <see cref="GroupLimit"/> per group and <see cref="TotalLimit"/> in all, the current tab always. The Search palette still
/// lists everything. The order is kept stable by <see cref="RecentTabOrder"/>. Tasks (Workspaces and Projects) and chats
/// (Conversations) share it.
/// </remarks>
/// <param name="context">The view's dispatch, which every action runs through.</param>
public sealed class RecentTabs( PanelContext context )
{
	/// <summary>Defines the most tabs a group shows; the current tab counts toward it and shows past it.</summary>
	public const int GroupLimit = 3;

	/// <summary>Defines the most tabs the strip shows; the current tab counts toward it and shows past it.</summary>
	public const int TotalLimit = 8;

	/// <summary>Defines how long after its last use a tab that is neither current nor pinned stays shown.</summary>
	public static readonly TimeSpan RecentWindow = TimeSpan.FromDays( 7 );

	/// <summary>Defines the widest a tab grows before its title ends with an ellipsis, in logical pixels.</summary>
	private const int TabMaxWidth = 200;

	/// <summary>Defines the widest a group's name grows before it ends with an ellipsis, in logical pixels.</summary>
	private const int GroupNameMaxWidth = 160;

	/// <summary>Defines the inset between a group's tinted background and its buttons, in logical pixels; the group keeps the height of one control.</summary>
	private const int GroupInset = 2;

	/// <summary>Keeps the strip's order between rebuilds.</summary>
	private readonly RecentTabOrder _order = new();

	/// <summary>Picks the groups and tabs the strip shows, groups ordered by their most recently used tab and tabs by last use.</summary>
	/// <remarks>
	/// A tab shows when it is current, pinned, or used within <see cref="RecentWindow"/>; current first, then pinned, then the most
	/// recent fill each group up to <see cref="GroupLimit"/> and the strip up to <see cref="TotalLimit"/>, and the current tab shows
	/// past both. The current tab counts as used now. A group with no tab shown is left out unless it is the current group.
	/// </remarks>
	/// <param name="groups">Every group with all its tabs, in any order.</param>
	/// <param name="currentGroup">The group the user is in, kept even when it shows nothing, or null.</param>
	/// <param name="currentTab">The current tab, or null.</param>
	/// <param name="now">The time the recent window ends.</param>
	public static IReadOnlyList<RecentTabGroup> ComputeShown( IEnumerable<RecentTabGroup> groups, string? currentGroup, Guid? currentTab, DateTimeOffset now )
	{
		var cutoff = now - RecentWindow;
		var ordered = groups
			.Select( group =>
			{
				var tabs = group.Tabs.Select( tab => (Tab: tab, IsCurrent: tab.Id == currentTab) ).ToArray();
				var recency = tabs.Length == 0 ? DateTimeOffset.MinValue : tabs.Max( item => item.IsCurrent ? DateTimeOffset.MaxValue : item.Tab.LastUsed );
				return (Group: group, Tabs: tabs, Recency: recency);
			} )
			.OrderByDescending( item => item.Recency )
			.ToArray();

		// The current group comes first, so the current tab always finds room and spends one of the total.
		var budget = TotalLimit;
		var shown = new List<RecentTabGroup>();
		foreach ( var (group, tabs, _) in ordered )
		{
			var picked = new List<(RecentTab Tab, bool IsCurrent)>();
			var candidates = tabs
				.Where( item => item.IsCurrent || item.Tab.IsPinned || item.Tab.LastUsed >= cutoff )
				.OrderByDescending( item => item.IsCurrent )
				.ThenByDescending( item => item.Tab.IsPinned )
				.ThenByDescending( item => item.Tab.LastUsed );
			foreach ( var item in candidates )
			{
				if ( item.IsCurrent )
				{
					picked.Add( item );
					budget = Math.Max( 0, budget - 1 );
					continue;
				}

				if ( budget == 0 || picked.Count >= GroupLimit )
					continue;

				picked.Add( item );
				budget--;
			}

			if ( picked.Count > 0 || group.Key == currentGroup )
			{
				shown.Add( group with
				{
					Tabs = [.. picked.OrderByDescending( item => item.IsCurrent ? DateTimeOffset.MaxValue : item.Tab.LastUsed ).Select( item => item.Tab )],
				} );
			}
		}
		return shown;
	}

	/// <summary>Picks the groups and tabs to show with <see cref="ComputeShown"/>, then orders them with this strip's <see cref="RecentTabOrder"/>.</summary>
	/// <param name="groups">Every group with all its tabs, in any order.</param>
	/// <param name="currentGroup">The group the user is in, or null.</param>
	/// <param name="currentTab">The current tab, or null.</param>
	public IReadOnlyList<RecentTabGroup> Arrange( IEnumerable<RecentTabGroup> groups, string? currentGroup, Guid? currentTab ) =>
		_order.Arrange( ComputeShown( groups, currentGroup, currentTab, DateTimeOffset.UtcNow ), currentTab );

	/// <summary>Builds the strip's row of groups and actions, scrolling sideways when they do not fit.</summary>
	/// <param name="accessibilityName">The tab list's accessible name.</param>
	/// <param name="children">The groups, then the actions after them.</param>
	public static Row BuildStrip( string accessibilityName, List<Blob> children )
	{
		var theme = Theme.Current;
		return new Row
		{
			AccessibilityRole = AccessibilityRole.TabList,
			AccessibilityName = accessibilityName,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			MinWidth = 0,
			OverflowX = Overflow.Scroll,
			Children = children,
		};
	}

	/// <summary>Builds one group: its label, then its tabs with the current one highlighted, then a small + that adds a tab to it.</summary>
	/// <param name="group">The group with its shown tabs.</param>
	/// <param name="title">The group's name, such as a project's.</param>
	/// <param name="icon">The icon before the name, or null for none.</param>
	/// <param name="isCurrentGroup">Whether the user is in this group, which brightens its name.</param>
	/// <param name="currentTab">The current tab, highlighted, or null.</param>
	/// <param name="open">Opens the group, such as its project.</param>
	/// <param name="openTab">Opens one of its tabs.</param>
	/// <param name="add">Adds a tab to the group, such as a new task or chat.</param>
	/// <param name="addName">The + button's accessible name, such as "New task in Lokn".</param>
	public Row BuildGroup( RecentTabGroup group, string title, Blob? icon, bool isCurrentGroup, Guid? currentTab, Action open, Action<Guid> openTab, Action add, string addName )
	{
		var theme = Theme.Current;
		var height = theme.ControlHeight - GroupInset * 2;

		var label = theme.BuildButton( title, Run( open ) );
		label.Key = "label";
		label.Height = height;
		label.FlexDirection = FlexDirection.Row;
		label.AlignItems = AlignItems.Center;
		label.Gap = theme.Spacing / 2;
		label.FlexShrink = 0;
		label.AccessibilityName = isCurrentGroup ? $"{title}, open" : title;
		label.Children =
		[
			.. icon is null ? Array.Empty<Blob>() : [icon],
			new Text
			{
				Content = title,
				FontWeight = SidebarRows.GroupWeight,
				Color = isCurrentGroup ? theme.Text : theme.Muted,
				MinWidth = 0,
				MaxWidth = GroupNameMaxWidth,
				FlexShrink = 1,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		];

		var children = new List<Blob> { label };
		foreach ( var tab in group.Tabs )
			children.Add( BuildTab( tab, tab.Id == currentTab, height, () => openTab( tab.Id ) ) );

		var plus = theme.BuildButton( addName, Run( add ) );
		plus.Key = "new";
		plus.Width = height;
		plus.Height = height;
		plus.Padding = 0;
		plus.FlexShrink = 0;
		plus.Children = [SidebarRows.BuildIcon( SidebarRows.AddGlyph )];
		children.Add( plus );

		return new Row
		{
			Key = group.Key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = title,
			AlignItems = AlignItems.Center,
			Gap = 2,
			Height = theme.ControlHeight,
			Padding = GroupInset,
			FlexShrink = 0,
			BorderRadius = theme.Radius + GroupInset,
			// The shared tint binds the label and its tabs together; the current tab stands out on it.
			BackgroundColor = theme.Raised,
			Children = children,
		};
	}

	/// <summary>Builds the strip's trailing +, such as the one that starts a task or chat outside projects.</summary>
	/// <param name="key">The button's key.</param>
	/// <param name="accessibilityName">The button's accessible name.</param>
	/// <param name="add">Adds the tab.</param>
	public Button BuildAdd( string key, string accessibilityName, Action add )
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( accessibilityName, Run( add ) );
		button.Key = key;
		button.Width = theme.ControlHeight;
		button.Padding = 0;
		button.FlexShrink = 0;
		button.Children = [SidebarRows.BuildIcon( SidebarRows.AddGlyph )];
		return button;
	}

	/// <summary>Turns a session's tasks into a group keyed by the session, for <see cref="ComputeShown"/>.</summary>
	public static RecentTabGroup BuildTaskGroup( string session, IEnumerable<WorkspaceTask> tasks ) =>
		new( session, [.. tasks.Select( task => new RecentTab( task.Id, task.Name, task.IsPinned, task.LastUsed ) )] );

	/// <summary>Gets the tasks outside projects as a group, for <see cref="ComputeShown"/>; subscribes the building view.</summary>
	public static RecentTabGroup BuildHomeGroup( WorkspacesModule workspaces ) => BuildTaskGroup( WorkspacesModule.HomeSession, workspaces.Tasks( WorkspacesModule.HomeSession ) );

	/// <summary>Builds a session's task group: its label opens the session, its tabs show its tasks, its + starts a task there.</summary>
	/// <param name="workspaces">The module that keeps the tasks.</param>
	/// <param name="group">The group with its shown tasks, keyed by the session.</param>
	/// <param name="title">The group's name.</param>
	/// <param name="icon">The icon before the name, or null for none.</param>
	/// <param name="open">Opens the session at the task it shows.</param>
	/// <param name="openTask">Opens the session and shows one of its tasks.</param>
	/// <param name="newTask">Starts a new, empty task in the session.</param>
	public Row BuildTasks( WorkspacesModule workspaces, RecentTabGroup group, string title, Blob? icon, Action open, Action<Guid> openTask, Action newTask )
	{
		var isCurrentGroup = workspaces.CurrentSession == group.Key;
		return BuildGroup( group, title, icon, isCurrentGroup, isCurrentGroup ? workspaces.CurrentTask : null, open, openTask, newTask, $"New task in {title}" );
	}

	/// <summary>Builds the Tasks group, the tasks outside projects; its label opens their session, its + starts one there.</summary>
	public Row BuildHome( WorkspacesModule workspaces, RecentTabGroup group ) => BuildTasks( workspaces, group, "Tasks", null,
		() => workspaces.OpenSession( context.App, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, context.App.Panels.HideAll ),
		task => workspaces.OpenTask( context.App, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, task, context.App.Panels.HideAll ),
		() => workspaces.NewTask( context.App, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, context.App.Panels.HideAll ) );

	/// <summary>Builds the strip's trailing + that starts a new, empty task outside projects.</summary>
	public Button BuildNewTask( WorkspacesModule workspaces ) => BuildAdd( "new-home-task", "New task",
		() => workspaces.NewTask( context.App, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, context.App.Panels.HideAll ) );

	/// <summary>Wraps a strip action so the order it leaves stays as it is, then runs it through the view's dispatch.</summary>
	private Action Run( Action action ) => () => context.Run( () =>
	{
		_order.MarkOwnAction();
		action();
	} );

	/// <summary>Builds one tab: its title and marks; clicking it opens it.</summary>
	private Button BuildTab( RecentTab tab, bool isCurrent, int height, Action open )
	{
		var theme = Theme.Current;
		var name = tab.AccessibilityName ?? tab.Title;
		var button = theme.BuildButton( tab.Title, Run( open ) );
		button.Key = tab.Id.ToString();
		button.Height = height;
		button.FlexShrink = 0;
		button.MaxWidth = TabMaxWidth;
		button.FlexDirection = FlexDirection.Row;
		button.Gap = theme.Spacing;
		button.BackgroundColor = isCurrent ? theme.Hover : Color.Transparent;
		button.AccessibilityRole = AccessibilityRole.Tab;
		button.AccessibilityName = isCurrent ? $"{name}, current" : name;
		button.Children =
		[
			new Text
			{
				Content = tab.Title,
				Color = isCurrent ? theme.Text : theme.Muted,
				MinWidth = 0,
				FlexShrink = 1,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			.. tab.Marks,
		];
		return button;
	}
}
