using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Lists tasks in the Search palette: those outside projects here, a project's through <see cref="BuildTaskItem"/> from the Projects module.</summary>
public sealed partial class WorkspacesModule
{
	/// <summary>Defines the place of Tasks among the Search palette's types: first after All.</summary>
	public const int TasksSearchOrder = 10;

	/// <summary>Defines the module whose chats are the tasks while it is loaded.</summary>
	private const string ConversationsModuleId = "conversations";

	/// <summary>Gets the task a session shows when it opens, or empty when it was never opened; subscribes the building view.</summary>
	public Guid CurrentTaskOf( string session ) => Store.CurrentOf( session );

	/// <summary>Builds a task's Search palette item: running it shows the task; its actions pin or unpin it and delete it.</summary>
	/// <param name="app">The application.</param>
	/// <param name="session">The task's session key.</param>
	/// <param name="task">The task.</param>
	/// <param name="detail">Where the task belongs, such as its project's name, or null.</param>
	/// <param name="open">Opens the task's session and shows the task.</param>
	/// <remarks>The task a session shows when it opens counts as used when the session was last opened.</remarks>
	public SearchItem BuildTaskItem( LoknApplication app, string session, WorkspaceTask task, string? detail, Action open ) => new( task.Id.ToString(), task.Name, open )
	{
		Detail = detail,
		Glyph = SidebarRows.TaskGlyph,
		LastUsed = CurrentTaskOf( session ) == task.Id ? Sessions.FirstOrDefault( item => item.Key == session )?.LastOpened : null,
		Actions =
		[
			new MenuAction( task.IsPinned ? "Unpin" : "Pin", () => SetPinned( session, task.Id, !task.IsPinned ) ) { AccessKey = Key.P },
			// A session keeps its last task, so that one cannot be deleted.
			new MenuAction( "Delete task", () => DeleteTask( app, session, task.Id ) )
			{
				AccessKey = Key.D,
				IsDestructive = true,
				IsEnabled = Tasks( session ).Count > 1,
			},
		],
	};

	/// <summary>Lists the tasks outside projects; nothing while the Conversations module is loaded, whose chats are the tasks then.</summary>
	private IEnumerable<SearchItem> ListTasks( LoknApplication app )
	{
		if ( app.Modules.Any( module => module.Id == ConversationsModuleId ) )
			return [];

		return Tasks( HomeSession ).Select( task => BuildTaskItem( app, HomeSession, task, null,
			() => OpenTask( app, HomeSession, HomeTitle, task.Id, app.Panels.HideAll ) ) );
	}
}
