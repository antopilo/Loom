using Goo;
using Lokn.Core;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Projects;

/// <summary>Builds the sidebar's project rows: the New project action, a project's heading, which renames in place like a task, and its folder chooser.</summary>
/// <remarks>
/// New project opens the native folder chooser, adds the folder as a project named after it, and starts renaming its heading so the name can be
/// typed at once. Double-click a heading, press F2 on it, or choose Rename in its menu to rename it; Change folder… picks its folder again.
/// A paired host's folders are typed in a <see cref="HostFolderPrompt"/> instead, which the view mounts with <see cref="HostFolderPrompt.Build"/>.
/// One instance belongs to one sidebar view, beside its <see cref="SidebarRows"/>.
/// </remarks>
/// <param name="context">The view's dispatch, which every action runs through.</param>
/// <param name="sidebar">The view's rows, which hold the rename state.</param>
/// <param name="store">The projects the actions create and change.</param>
/// <param name="host">The host the projects' folders are on, or null for This PC.</param>
public sealed class ProjectRows( PanelContext context, SidebarRows sidebar, ProjectStore store, LoknHost? host = null )
{
	/// <summary>Builds the New project action, which opens the folder chooser and adds the chosen folder as a project.</summary>
	/// <param name="created">Runs with the project once its folder is chosen, the new one or the one that already had the folder, or null.</param>
	public Button BuildNewProjectAction( Action<ProjectInfo>? created = null ) =>
		sidebar.BuildIconAction( "New project", SidebarRows.NewFolderGlyph, () => BeginCreate( created ) );

	/// <summary>Asks for a folder, then adds it as a project: the native folder chooser over the shell window on This PC, a typed path on a paired host;
	/// nothing happens on This PC without a window.</summary>
	/// <param name="created">Runs with the project once its folder is chosen, or null.</param>
	public void BeginCreate( Action<ProjectInfo>? created = null ) =>
		ChooseFolder( "Choose the project's folder", null, directory => created?.Invoke( Create( directory ) ) );

	/// <summary>Adds a folder as a project of the active profile, named after the folder, and starts renaming its heading; a folder that already is a project returns that project.</summary>
	/// <param name="directory">The project's folder.</param>
	public ProjectInfo Create( string directory )
	{
		directory = Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );
		var profile = context.App.Settings.ActiveProfile;
		if ( store.Projects( profile ).FirstOrDefault( item => string.Equals( Path.TrimEndingDirectorySeparator( Path.GetFullPath( item.Directory ) ), directory, StringComparison.OrdinalIgnoreCase ) ) is { } existing )
			return existing;

		var project = store.Create( profile, Path.GetFileName( directory ) is { Length: > 0 } name ? name : directory, directory );
		sidebar.BeginRename( project.Id, project.Name );
		return project;
	}

	/// <summary>Starts renaming a project's heading in place.</summary>
	public void BeginRename( ProjectInfo project ) => sidebar.BeginRename( project.Id, project.Name );

	/// <summary>Asks for a project's folder again, starting at its current one, then moves the project to the chosen one.</summary>
	public void BeginChangeFolder( ProjectInfo project ) =>
		ChooseFolder( $"Choose the folder of {project.Name}", project.Directory, directory =>
		{
			if ( store.Find( project.Id ) is { } current )
				store.Update( current.Id, current.Name, directory );
		} );

	/// <summary>Builds a project's bold heading with its icon and disclosure chevron, or its name editor while it is renamed; clicking the heading folds or unfolds its rows.</summary>
	/// <param name="project">The project.</param>
	/// <param name="icon">The project's icon from <see cref="ProjectsModule.GetIcon"/>, or null for none.</param>
	/// <param name="isCollapsed">Whether its rows are folded.</param>
	/// <param name="accessibilityName">The heading's accessible name.</param>
	/// <param name="trailing">Marks after the chevron, such as an activity dot.</param>
	public Blob BuildHeading( ProjectInfo project, AsyncImageSource? icon, bool isCollapsed, string accessibilityName, IEnumerable<Blob>? trailing = null )
	{
		if ( sidebar.IsRenaming( project.Id ) )
		{
			return sidebar.BuildRenameEntry( project.Id, project.Name, "Rename project", name =>
			{
				if ( store.Find( project.Id ) is { } current )
					store.Update( current.Id, name, current.Directory );
			} );
		}

		var heading = sidebar.BuildGroupHeading( project.Id, project.Name, icon is null ? null : ProjectsModule.BuildIcon( icon, project.Name, SidebarRows.GroupIconSize ),
			isCollapsed, accessibilityName, trailing );
		heading.KeyBindings = [new( Key.F2, KeyModifiers.None, () => context.Run( () => BeginRename( project ) ) )];
		// The double-click's two clicks fold and unfold the project, which leaves it as it was.
		heading.OnPointerUp = pointer => context.Run( () =>
		{
			if ( pointer.Button == PointerButton.Primary && pointer.ClickCount == 2 )
				BeginRename( project );
		} );
		return heading;
	}

	/// <summary>Asks for a folder of the projects' host and passes a chosen one on the UI thread: a typed path checked on a paired host, else the native
	/// folder chooser over the shell window.</summary>
	private void ChooseFolder( string title, string? initial, Action<string> chosen )
	{
		if ( host is { IsLocal: false } paired )
			HostFolderPrompt.Show( context, paired, title, initial, chosen );
		else if ( context.App.Window is { } window )
			_ = ChooseFolderAsync( window, initial is null ? new FileDialogOptions { Title = title } : new FileDialogOptions { Title = title, InitialPath = initial }, chosen );
	}

	/// <summary>Shows the folder chooser and passes a chosen folder on the UI thread; a chooser that fails to open reports why.</summary>
	private async Task ChooseFolderAsync( Window window, FileDialogOptions options, Action<string> chosen )
	{
		var result = await window.ShowFileDialogAsync( FileDialogKind.Folder, options );
		context.Post( () =>
		{
			if ( result.Status == FileDialogStatus.Success && result.Paths.FirstOrDefault() is { } directory )
				chosen( directory );
			else if ( result.Status != FileDialogStatus.Cancelled )
				context.App.ReportError( "Projects", new IOException( $"Could not open the folder chooser: {result.Error}" ) );
		} );
	}
}
