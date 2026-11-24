using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Worktrees;

/// <summary>Shows one repository's working trees and branches, with what each working tree shares, and creates or removes them.</summary>
internal sealed partial class WorktreesContent : PanelContent
{
	/// <summary>Defines the panel content inset in logical pixels.</summary>
	private const int ContentPadding = 24;

	/// <summary>Defines the panel heading size in logical pixels.</summary>
	private const int HeadingSize = 22;

	/// <summary>Defines the height assumed for a page row until layout measures it, in logical pixels.</summary>
	private const double EstimatedRowHeight = 48;

	/// <summary>Defines the section heading size in logical pixels.</summary>
	private const int SectionSize = 16;

	/// <summary>Defines the text entry height in logical pixels.</summary>
	private const int FieldHeight = 34;

	/// <summary>Defines the path and detail text size in logical pixels.</summary>
	private const int DetailSize = 12;

	/// <summary>Defines how many commit hash characters identify a detached working tree.</summary>
	private const int HashLength = 7;

	/// <summary>Defines the number of bytes in one step between displayed size units.</summary>
	private const double BytesPerUnit = 1024.0;

	/// <summary>Defines the label of the repository choice that uses the application's working directory.</summary>
	private const string WorkspaceName = "Workspace";

	/// <summary>Stores the panel's guarded dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the projects whose directories are offered as repositories.</summary>
	private readonly ConversationStore _store;

	/// <summary>Stores repository setups and native build records.</summary>
	private readonly WorktreeSetupStore _setup;

	/// <summary>Reports that this panel created or removed a working tree, so the chat sidebar lists the repository again.</summary>
	private readonly Action _changed;

	/// <summary>Tracks the directory whose repository is shown.</summary>
	private readonly State<string> _repository;

	/// <summary>Rebuilds the panel when <see cref="View"/> changes.</summary>
	private readonly State<int> _viewVersion = new( 0 );

	/// <summary>Stores the latest listing of the repository, or null before the first one or after a failure.</summary>
	private RepositoryView? _currentView;

	/// <summary>Tracks measured sizes in bytes by working tree directory.</summary>
	private readonly State<Dictionary<string, long>> _sizes = new( [] );

	/// <summary>Retains the branch name typed for a new working tree.</summary>
	private readonly State<string> _branch = new( "" );

	/// <summary>Retains the start point typed for a new branch.</summary>
	private readonly State<string> _startPoint = new( "" );

	/// <summary>Retains the folders a new working tree checks out; empty checks out everything.</summary>
	private readonly State<string> _folders = new( "" );

	/// <summary>Retains the branch list filter.</summary>
	private readonly State<string> _branchFilter = new( "" );

	/// <summary>Tracks whether the repository setup is open for editing.</summary>
	private readonly State<bool> _isSetupOpen = new( false );

	/// <summary>Retains the edited default folders.</summary>
	private readonly State<string> _setupFolders = new( "" );

	/// <summary>Retains the edited shared folders.</summary>
	private readonly State<string> _setupShared = new( "" );

	/// <summary>Retains the edited native input paths.</summary>
	private readonly State<string> _setupInputs = new( "" );

	/// <summary>Retains the edited native output folder.</summary>
	private readonly State<string> _setupOutput = new( "" );

	/// <summary>Reports the last result or git error.</summary>
	private readonly State<string> _status = new( "" );

	/// <summary>Tracks whether a git command is running, which disables further actions.</summary>
	private readonly State<bool> _isBusy = new( false );

	/// <summary>Tracks the working tree whose removal or the branch whose deletion awaits a confirming second click, or empty.</summary>
	private readonly State<string> _confirming = new( "" );

	/// <summary>Creates the panel and starts listing the requested, restored or default repository; on a paired host it lists nothing.</summary>
	internal WorktreesContent( PanelContext context, ConversationStore store, WorktreeSetupStore setup, Action changed )
	{
		_context = context;
		_store = store;
		_setup = setup;
		_changed = changed;
		_repository = new( ComputeInitialRepository( context, store ) );
		if ( context.Host.IsLocal )
			_ = UpdateViewAsync( resetForm: true );
	}

	/// <summary>Shows the repository holding a folder, unless it is already shown; call on the UI thread.</summary>
	internal void ShowRepository( string directory )
	{
		if ( !IsSamePath( _repository.Value, directory ) )
			UpdateRepository( directory );
	}

	/// <summary>Gets or sets the latest listing, subscribing the building view.</summary>
	private RepositoryView? View
	{
		get
		{
			_ = _viewVersion.Value;
			return _currentView;
		}
		set
		{
			_currentView = value;
			_viewVersion.Value++;
		}
	}

	/// <summary>Builds the repository choices, setup, create form, working trees and branches; on a paired host, only the notice that worktrees work on this PC.</summary>
	public override Blob Build()
	{
		if ( !_context.Host.IsLocal )
		{
			return new Container
			{
				AccessibilityRole = AccessibilityRole.Group,
				AccessibilityName = "Worktrees",
				FlexGrow = 1,
				Padding = ContentPadding,
				Gap = Theme.Current.Spacing,
				Children =
				[
					new Text { Content = "Worktrees", FontSize = HeadingSize },
					new Text { Content = "Worktrees work on this PC only.", Color = Theme.Current.Muted },
				],
			};
		}

		var view = View;
		var rows = new List<BuiltRow>
		{
			new( "title", () => new Text { Content = "Worktrees", FontSize = HeadingSize } ),
			new( "repositories", BuildRepositories ),
			new( "repository", () => BuildDetail( _repository.Value ) ),
		};

		if ( _status.Value is { Length: > 0 } status )
			rows.Add( new( "status", () => new Text { Content = status, Selectable = true } ) );

		if ( view is not null )
		{
			if ( _isSetupOpen.Value )
				rows.Add( new( "setup", BuildSetup ) );

			rows.Add( new( "create", () => BuildCreate( view ) ) );
			rows.AddRange( view.Worktrees.Select( worktree => new BuiltRow( "worktree:" + worktree.Path, () => BuildWorktree( view, worktree ) ) ) );
			rows.Add( new( "summary", () => BuildSummary( view ) ) );
			rows.AddRange( BuildBranches( view ) );
		}

		return new Virtual
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Worktrees",
			Source = new MeasuredRowsSource<BuiltRow>( rows, BuildRow, static _ => EstimatedRowHeight, FollowEnd: false ),
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Padding = ContentPadding,
			OverflowX = Overflow.Hidden,
			ScrollbarY = ViewStyle.BuildScrollbar(),
		};
	}

	/// <summary>Builds one row of the page with the gap below it.</summary>
	private static Container BuildRow( BuiltRow row ) => new()
	{
		Padding = new Edges( 0, 0, Theme.Current.Spacing, 0 ),
		Children = [row.Build()],
	};

	/// <summary>Captures the shown repository directory for layout restoration and module reloads.</summary>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( _repository.Value );

	/// <summary>Formats a byte count with the largest binary unit that keeps it above one.</summary>
	internal static string FormatSize( long bytes )
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var value = (double)bytes;
		var unit = 0;
		while ( value >= BytesPerUnit && unit < units.Length - 1 )
		{
			value /= BytesPerUnit;
			unit++;
		}

		return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
	}

	/// <summary>Formats how long ago a commit was made, in the largest whole unit.</summary>
	internal static string FormatAge( DateTimeOffset date, DateTimeOffset now )
	{
		var age = now - date;
		return age.TotalMinutes < 1 ? "just now"
			: age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
			: age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
			: age.TotalDays < 60 ? $"{(int)age.TotalDays} days ago"
			: date.ToLocalTime().ToString( "yyyy-MM-dd" );
	}

	/// <summary>Builds a choice for each of the active profile's projects and the working directory, with the repository actions.</summary>
	private Blob BuildRepositories()
	{
		var choices = _store.ProjectStore
			.Projects( _context.App.Settings.ActiveProfile )
			.Select( project => (project.Name, project.Directory) )
			.Append( (Name: WorkspaceName, Directory: Environment.CurrentDirectory) );

		var isIdle = !_isBusy.Value;
		var buttons = new List<Blob>();
		foreach ( var (name, directory) in choices )
		{
			var button = Theme.Current.BuildButton( name, () => _context.Run( () => UpdateRepository( directory ) ), isIdle );
			button.BackgroundColor = IsSamePath( directory, _repository.Value ) ? Theme.Current.Hover : Theme.Current.Raised;
			buttons.Add( button );
		}

		buttons.Add( Theme.Current.BuildButton( "Refresh", () => _context.Run( () => _ = UpdateViewAsync( resetForm: false ) ), isIdle ) );
		buttons.Add( Theme.Current.BuildButton( "Fetch", () => _context.Run( UpdateFetch ), isIdle && View is not null ) );
		buttons.Add( Theme.Current.BuildButton( _isSetupOpen.Value ? "Close setup" : "Setup", () => _context.Run( UpdateSetupOpen ), View is not null ) );
		return new Row
		{
			Gap = Theme.Current.Spacing,
			FlexWrap = FlexWrap.Wrap,
			Children = buttons,
		};
	}

	/// <summary>Builds the repository setup form: default folders, shared folders and native build sharing.</summary>
	private Blob BuildSetup()
	{
		return new Container
		{
			Key = "worktree-setup",
			Gap = Theme.Current.Spacing,
			Padding = Theme.Current.Spacing,
			BackgroundColor = Theme.Current.Raised,
			BorderRadius = Theme.Current.Radius,
			Children =
			[
				BuildSection( "Repository setup" ),
				BuildDetail( "Folders, comma-separated and relative to the repository root. Saved per repository in Lokn's worktrees.json." ),
				BuildField( "setup-folders", _setupFolders, "Default folders for new worktrees, such as engine, docs, game/addons (empty: everything)", "Default worktree folders" ),
				BuildField( "setup-shared", _setupShared, "Ignored folders every worktree shares with the main checkout, such as game/download, game/.source2", "Shared folders" ),
				BuildField( "setup-inputs", _setupInputs, "Native input paths, such as src, engine/Definitions, engine/Tools/SboxBuild, engine/Tools/InteropGen, engine/manifest.def", "Native input paths" ),
				BuildField( "setup-output", _setupOutput, "Native output folder, such as game/bin/win64", "Native output folder" ),
				BuildDetail( "Worktrees whose native input paths are identical share one recorded native build through hard links, and build managed only. Stop sharing before building native in one." ),
				new Row
				{
					Gap = Theme.Current.Spacing,
					Children = [Theme.Current.BuildButton( "Save setup", () => _context.Run( UpdateSaveSetup ), !_isBusy.Value )],
				},
			],
		};
	}

	/// <summary>Builds the new worktree form: branch, start point and folders.</summary>
	private Blob BuildCreate( RepositoryView view )
	{
		return new Container
		{
			Gap = Theme.Current.Spacing,
			Children =
			[
				new Row
				{
					Gap = Theme.Current.Spacing,
					AlignItems = AlignItems.Center,
					Children =
					[
						BuildField( "worktree-branch", _branch, "Branch: an existing local or remote branch, or a new name", "Worktree branch", UpdateCreateTyped ),
						BuildField( "worktree-start", _startPoint, "Start point for a new branch (default: current commit)", "New branch start point", UpdateCreateTyped ),
						Theme.Current.BuildButton( "Create worktree", () => _context.Run( UpdateCreateTyped ), !_isBusy.Value ),
					],
				},
				BuildField( "worktree-folders", _folders, "Folders to check out, comma-separated (empty: everything)", "Worktree folders", UpdateCreateTyped ),
				BuildDetail( view.Setup.SharedFolders.Length > 0 || view.Setup.SharesNative
					? $"New worktrees share {string.Join( ", ", [.. view.Setup.SharedFolders, .. view.Setup.SharesNative ? [$"{view.Setup.NativeOutput} when native sources match"] : Array.Empty<string>()] )}."
					: "New worktrees share nothing but git's history. Open Setup to share caches and native builds." ),
			],
		};
	}

	/// <summary>Builds one working tree's branch, directory, folders, native build, size and actions.</summary>
	private Blob BuildWorktree( RepositoryView view, WorktreeInfo worktree )
	{
		var title = worktree.Branch
			?? (worktree.Head.Length >= HashLength ? $"detached {worktree.Head[..HashLength]}" : "bare");
		if ( worktree.IsMain )
			title += " · main working tree";
		if ( worktree.IsLocked )
			title += " · locked";
		if ( worktree.IsPrunable )
			title += " · directory missing";

		var details = view.Details.GetValueOrDefault( worktree.Path );
		var lines = new List<Blob>
		{
			new Text
			{
				Content = title,
			},
			new Text
			{
				Content = worktree.Path,
				Color = Theme.Current.Muted,
				FontSize = DetailSize,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
				Selectable = true,
			},
		};
		if ( details is not null )
			lines.Add( BuildDetail( details.Folders.Count == 0 ? "Full checkout" : $"Folders: {string.Join( ", ", details.Folders )}" ) );
		if ( details?.Native is { } native )
		{
			var text = BuildDetail( native.Text );
			if ( native.IsWarning )
				text.Color = Theme.Current.Accent;
			lines.Add( text );
		}

		var sizes = _sizes.Value;
		var size = sizes.TryGetValue( worktree.Path, out var bytes ) ? FormatSize( bytes ) : "measuring…";
		if ( details?.SharedNative is not null )
			size += " + shared native";

		var actions = new List<Blob>
		{
			new Text
			{
				Content = size,
				Color = Theme.Current.Muted,
			},
		};
		if ( details?.Native?.Action is { } action and not NativeAction.None )
		{
			var label = action switch
			{
				NativeAction.Record => "Record native build",
				NativeAction.Share => "Share native build",
				_ => "Stop sharing native",
			};
			var button = Theme.Current.BuildButton( label, () => _context.Run( () => UpdateNative( worktree.Path, action ) ), !_isBusy.Value );
			button.AccessibilityName = $"{label} {title}";
			actions.Add( button );
		}

		actions.Add( Theme.Current.BuildButton( "Open folder", () => _context.Run( () => UpdateOpen( worktree.Path ) ), !worktree.IsPrunable ) );
		if ( !worktree.IsMain )
			actions.Add( BuildConfirming( worktree.Path, "Remove", title, () => UpdateRemove( worktree.Path ) ) );

		return new Row
		{
			Key = worktree.Path,
			Padding = Theme.Current.Spacing,
			Gap = Theme.Current.Spacing,
			AlignItems = AlignItems.Center,
			BackgroundColor = Theme.Current.Raised,
			BorderRadius = Theme.Current.Radius,
			Children =
			[
				new Container
				{
					FlexGrow = 1,
					MinWidth = 0,
					Children = lines,
				},
				.. actions,
			],
		};
	}

	/// <summary>Builds the working tree count and total size.</summary>
	private Text BuildSummary( RepositoryView view )
	{
		var sizes = _sizes.Value;
		var count = view.Worktrees.Count;
		return BuildDetail( sizes.Count < count
			? $"{count} working trees. Measuring disk usage…"
			: $"{count} working trees using {FormatSize( sizes.Values.Sum() )}, not counting shared folders and shared native builds. The main working tree includes the shared .git directory." );
	}

	/// <summary>Lists the branch rows: the heading, the filter, then each matching branch.</summary>
	private List<BuiltRow> BuildBranches( RepositoryView view )
	{
		var filter = _branchFilter.Value.Trim();
		var now = DateTimeOffset.UtcNow;
		var rows = new List<BuiltRow>
		{
			new( "branches", () => BuildSection( "Branches" ) ),
			new( "branch-filter", () => BuildField( "branch-filter", _branchFilter, "Filter branches", "Filter branches" ) ),
		};
		rows.AddRange( view.Branches
			.Where( branch => filter.Length == 0 || branch.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
			.Select( branch => new BuiltRow( "branch:" + branch.Reference, () => BuildBranch( view, branch, now ) ) ) );
		if ( rows.Count == 2 )
			rows.Add( new( "no-branches", () => BuildDetail( "No branches match." ) ) );
		return rows;
	}

	/// <summary>Builds one branch with its upstream, latest commit, native match and actions.</summary>
	private Row BuildBranch( RepositoryView view, BranchInfo branch, DateTimeOffset now )
	{
		var heading = branch.Name;
		if ( branch.Upstream.Length > 0 )
			heading += branch.Track.Length > 0 ? $" · {branch.Upstream} {branch.Track}" : $" · {branch.Upstream}";
		else if ( branch.IsRemote )
			heading += " · remote only";

		var detail = $"{FormatAge( branch.Date, now )} · {branch.Subject}";
		if ( view.BranchNative.TryGetValue( branch.Reference, out var holder ) )
			detail += $" · native sources match the build in {holder}";

		var actions = new List<Blob>();
		if ( branch.WorktreePath is { } path )
			actions.Add( BuildDetail( $"in {Path.GetFileName( path )}" ) );
		else
		{
			var create = Theme.Current.BuildButton( "Create worktree", () => _context.Run( () => UpdateCreate( branch.LocalName, "" ) ), !_isBusy.Value );
			create.AccessibilityName = $"Create worktree {branch.Name}";
			actions.Add( create );
			if ( !branch.IsRemote )
				actions.Add( BuildConfirming( branch.Reference, "Delete", branch.Name, () => UpdateDeleteBranch( branch ) ) );
		}

		return new Row
		{
			Key = branch.Reference,
			Padding = Theme.Current.Spacing,
			Gap = Theme.Current.Spacing,
			AlignItems = AlignItems.Center,
			BackgroundColor = Theme.Current.Raised,
			BorderRadius = Theme.Current.Radius,
			Children =
			[
				new Container
				{
					FlexGrow = 1,
					MinWidth = 0,
					Children =
					[
						new Text
						{
							Content = heading,
							TextWrap = TextWrap.NoWrap,
							TextTrimming = TextTrimming.Ellipsis,
						},
						new Text
						{
							Content = detail,
							Color = Theme.Current.Muted,
							FontSize = DetailSize,
							TextWrap = TextWrap.NoWrap,
							TextTrimming = TextTrimming.Ellipsis,
						},
					],
				},
				.. actions,
			],
		};
	}

	/// <summary>Builds a destructive action that asks for a confirming second click.</summary>
	private Button BuildConfirming( string key, string label, string subject, Action action )
	{
		var isConfirming = _confirming.Value == key;
		var text = isConfirming ? $"Confirm {label.ToLowerInvariant()}" : label;
		var button = Theme.Current.BuildButton( text, () => _context.Run( action ), !_isBusy.Value );
		button.AccessibilityName = $"{text} {subject}";
		if ( isConfirming )
			button.Color = Theme.Current.Accent;

		return button;
	}

	/// <summary>Builds a single-line entry bound to a retained value.</summary>
	private TextEntry BuildField( string key, State<string> value, string placeholder, string name, Action? onSubmit = null )
	{
		return new TextEntry
		{
			Key = key,
			Value = value.Value,
			Controlled = true,
			Disabled = _isBusy.Value,
			Placeholder = placeholder,
			AccessibilityName = name,
			Height = FieldHeight,
			FlexGrow = 1,
			MinWidth = 0,
			Padding = new Edges( 0, Theme.Current.Spacing ),
			BackgroundColor = Theme.Current.Raised,
			BorderColor = Theme.Current.Border,
			BorderWidth = Theme.Current.BorderWidth,
			BorderRadius = Theme.Current.Radius,
			OnChange = text => _context.Run( () => value.Value = text ),
			OnSubmit = onSubmit is null ? null : _ => _context.Run( onSubmit ),
		};
	}

	/// <summary>Builds a section heading.</summary>
	private static Text BuildSection( string text ) => new()
	{
		Content = text,
		FontSize = SectionSize,
	};

	/// <summary>Builds small muted selectable text.</summary>
	private static Text BuildDetail( string text ) => new()
	{
		Content = text,
		Color = Theme.Current.Muted,
		FontSize = DetailSize,
		Selectable = true,
	};

	/// <summary>Starts with the requested repository, else restores the saved one, else the first project of the active profile.</summary>
	private static string ComputeInitialRepository( PanelContext context, ConversationStore store )
	{
		if ( context.Input is string { Length: > 0 } requested )
			return requested;
		if ( context.RestoredState is { ValueKind: JsonValueKind.String } saved && saved.GetString() is { Length: > 0 } directory )
			return directory;

		var project = store.ProjectStore
			.Projects( context.App.Settings.ActiveProfile )
			.FirstOrDefault();
		return project?.Directory ?? Environment.CurrentDirectory;
	}

	/// <summary>Compares two directories ignoring case and a trailing separator.</summary>
	private static bool IsSamePath( string first, string second )
	{
		return string.Equals(
			Path.TrimEndingDirectorySeparator( first ),
			Path.TrimEndingDirectorySeparator( second ),
			StringComparison.OrdinalIgnoreCase );
	}
}
