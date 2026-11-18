using Goo;
using Lokn.Core;

namespace Lokn.Modules.Projects;

/// <summary>Describes a project whose repository holds Lokn modules the user has not yet trusted.</summary>
/// <param name="Project">The project.</param>
/// <param name="Modules">The module folder names under its <c>.lokn\modules</c>.</param>
internal sealed record PendingModules( ProjectInfo Project, IReadOnlyList<string> Modules );

/// <summary>Loads the Lokn modules projects keep in <c>.lokn\modules</c>, once the user trusts each project, for every project in the active profile.</summary>
/// <remarks>
/// Each subfolder of a project's <c>.lokn\modules</c> is a source module, compiled and hot-reloaded by Hotload like the ones in the
/// per-user folder. Their code runs with the user's rights, so a project's modules load only after the user chooses Load on Home,
/// or in the project list while Conversations, which draws Home, is not loaded.
/// </remarks>
public sealed partial class ProjectsModule
{
	/// <summary>Defines the folder, relative to a project's directory, that holds its modules.</summary>
	internal const string ProjectModulesFolder = ".lokn\\modules";

	/// <summary>Defines the identifier the trusted directories were saved under while Conversations owned projects; <see cref="Start"/> moves them to <see cref="TrustedModuleProjects"/>.</summary>
	internal const string LegacyTrustedModuleProjects = "conversations.module-projects";

	/// <summary>Defines the project directories whose modules the user chose to load.</summary>
	internal static readonly Setting<string> TrustedModuleProjects = new(
		"projects.module-projects",
		"Trusted project modules",
		"Projects whose .lokn\\modules load. Separate them with semicolons.",
		defaultValue: "",
		category: "Modules" );

	/// <summary>Tracks the active profile's projects that hold modules the user has not trusted yet; Home asks about the first.</summary>
	private readonly State<IReadOnlyList<PendingModules>> _pendingModules = new( [] );

	/// <summary>Stores the projects the user chose Not now for, asked again at the next start.</summary>
	private readonly HashSet<Guid> _declinedModules = [];

	/// <summary>Follows a profile switch or an edit of the trusted directories.</summary>
	private void UpdateProjectModulesOnChange()
	{
		if ( _context is { Lifetime.IsCancellationRequested: false } context )
			UpdateProjectModules( context.App );
	}

	/// <summary>Contributes the trusted projects' module folders to Hotload, and lists the projects still to ask about.</summary>
	/// <remarks>Runs at start, when settings or the profile change, and when a project opens, so a project added meanwhile is asked about too.</remarks>
	internal void UpdateProjectModules( LoknApplication app )
	{
		var trusted = ReadTrusted( app );
		var folders = new List<string>();
		var pending = new List<PendingModules>();
		foreach ( var project in _store.Projects( app.Settings.ActiveProfile ) )
		{
			var modules = FindProjectModules( project.Directory );
			if ( modules.Count == 0 )
				continue;

			if ( trusted.Contains( Normalize( project.Directory ) ) )
				folders.Add( Path.Combine( project.Directory, ProjectModulesFolder ) );
			else if ( !_declinedModules.Contains( project.Id ) )
				pending.Add( new( project, modules ) );
		}

		app.ModuleFolders.Set( Id, folders );
		if ( !pending.Select( item => (item.Project.Id, string.Join( ';', item.Modules )) ).SequenceEqual( _pendingModules.Value.Select( item => (item.Project.Id, string.Join( ';', item.Modules )) ) ) )
			_pendingModules.Value = pending;
	}

	/// <summary>Builds the question about the first of the active profile's untrusted projects with modules, or null when there is none.</summary>
	/// <param name="context">The panel or slot that shows it.</param>
	/// <param name="isNarrow">Whether it goes in the narrow sidebar, with its buttons under the text, rather than in one line on Home.</param>
	public Blob? BuildModulePrompt( PanelContext context, bool isNarrow = false )
	{
		if ( _pendingModules.Value is not [var first, ..] )
			return null;

		var theme = Theme.Current;
		var names = string.Join( ", ", first.Modules );
		var load = theme.BuildButton( "Load", () => context.Run( () => UpdateTrust( context.App, first.Project ) ) );
		load.Color = theme.Accent;
		var text = new Text
		{
			Content = $"{first.Project.Name} has Lokn modules: {names}. Loading runs their code with your rights.",
			FlexGrow = 1,
			FlexShrink = 1,
			MinWidth = 0,
			FontSize = theme.FontSize - 1,
		};
		var later = theme.BuildButton( "Not now", () => context.Run( () =>
		{
			_declinedModules.Add( first.Project.Id );
			UpdateProjectModules( context.App );
		} ) );
		return new Container
		{
			Key = "home-modules",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{first.Project.Name} has Lokn modules",
			FlexDirection = isNarrow ? FlexDirection.Column : FlexDirection.Row,
			AlignItems = isNarrow ? AlignItems.Stretch : AlignItems.Center,
			Gap = theme.Spacing,
			Padding = isNarrow ? theme.Spacing : new Edges( theme.Spacing, theme.Spacing * 2 ),
			MaxWidth = 680,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			Children = isNarrow
				? [text, new Row { Gap = theme.Spacing, JustifyContent = JustifyContent.FlexEnd, Children = [load, later] }]
				: [text, load, later],
		};
	}

	/// <summary>Trusts a project's modules for good; saving the setting loads them through <see cref="UpdateProjectModules"/>.</summary>
	internal void UpdateTrust( LoknApplication app, ProjectInfo project )
	{
		var trusted = ReadTrusted( app );
		if ( trusted.Add( Normalize( project.Directory ) ) )
			app.Settings.Set( TrustedModuleProjects, SettingsScope.Global, string.Join( ';', trusted ) );

		UpdateProjectModules( app );
	}

	/// <summary>Lists the module folders a project keeps: the subfolders of its <c>.lokn\modules</c> that hold C# files.</summary>
	internal static IReadOnlyList<string> FindProjectModules( string directory )
	{
		var root = Path.Combine( directory, ProjectModulesFolder );
		try
		{
			return Directory.Exists( root )
				? [.. Directory.GetDirectories( root ).Where( folder => Directory.EnumerateFiles( folder, "*.cs", SearchOption.AllDirectories ).Any() ).Select( Path.GetFileName ).OfType<string>().Order( StringComparer.OrdinalIgnoreCase )]
				: [];
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// A project on a drive that is gone, or a folder the user cannot read, offers no modules.
			return [];
		}
	}

	/// <summary>Reads the trusted directories, normalized.</summary>
	private static HashSet<string> ReadTrusted( LoknApplication app ) =>
		[.. app.Settings.Get( TrustedModuleProjects ).Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).Select( Normalize )];

	/// <summary>Normalizes a directory for comparison: full path, no trailing separator, case-insensitive through the set's comparer.</summary>
	private static string Normalize( string directory ) => Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) ).ToLowerInvariant();
}
