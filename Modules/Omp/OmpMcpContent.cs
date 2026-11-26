using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Lists, adds, edits, removes and turns on or off the MCP servers in Omp's own config files.</summary>
/// <remarks>Omp owns the files; this panel edits them as <c>/mcp add</c>, <c>enable</c> and <c>disable</c> do, and keeps no copy.</remarks>
internal sealed class OmpMcpContent : PanelContent
{
	/// <summary>Holds the panel's context.</summary>
	private readonly PanelContext _context;

	/// <summary>Holds the service that maps a Lokn profile to its Omp profile.</summary>
	private readonly OmpService _service;

	/// <summary>Holds the Lokn profile whose user-level file is shown.</summary>
	private SettingsProfile _profile;

	/// <summary>Holds whether the project file is shown instead of the user file.</summary>
	private bool _isProject;

	/// <summary>Holds the project folder whose <c>.omp/mcp.json</c> is shown.</summary>
	private string _projectFolder = "";

	/// <summary>Holds the servers last read from the shown file.</summary>
	private IReadOnlyList<OmpMcpServer> _servers = [];

	/// <summary>Holds the latest result or error.</summary>
	private string _status = "";

	/// <summary>Holds whether the status reports an error.</summary>
	private bool _isError;

	/// <summary>Holds whether the editor is open.</summary>
	private bool _isEditing;

	/// <summary>Holds the name of the server being edited, or null for a new server.</summary>
	private string? _editedName;

	/// <summary>Holds the name typed in the editor.</summary>
	private string _name = "";

	/// <summary>Holds the definition being typed, while the editor is open.</summary>
	private TextEditorController? _definition;

	/// <summary>Counts changes so a build that read it runs again after one.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Rebuilds the panel after its plain fields changed.</summary>
	private void MarkChanged() => _version.Value++;

	/// <summary>Creates the panel for a profile and reads its servers.</summary>
	internal OmpMcpContent( PanelContext context, OmpService service, SettingsProfile profile )
	{
		_context = context;
		_service = service;
		_profile = profile;
		UpdateServers();
	}

	/// <summary>Gets the file being shown.</summary>
	private string? ComputePath() =>
		!_isProject ? _service.GetMcpConfigPath( _profile )
		: _projectFolder.Trim().Length > 0 ? Path.Combine( Environment.ExpandEnvironmentVariables( _projectFolder.Trim() ), ".omp", "mcp.json" )
		: null;

	/// <summary>Builds the scope controls, the server list and the editor.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var path = ComputePath();
		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				FlexWrap = FlexWrap.Wrap,
				Children =
				[
					new Text { Content = "MCP servers", FontSize = theme.FontSize + 4, FontWeight = 600, FlexGrow = 1 },
					BuildTab( SettingsProfile.Work.ToString(), !_isProject && _profile == SettingsProfile.Work, () => UpdateScope( SettingsProfile.Work, false ) ),
					BuildTab( SettingsProfile.Personal.ToString(), !_isProject && _profile == SettingsProfile.Personal, () => UpdateScope( SettingsProfile.Personal, false ) ),
					BuildTab( "Project", _isProject, () => UpdateScope( _profile, true ) ),
					theme.BuildButton( "Refresh", () => _context.Run( UpdateServers ) ),
				],
			},
			new Text
			{
				Content = _isProject
					? "Project servers apply to every profile in that folder. Commit the file to share it; keep secrets in environment variables."
					: $"User servers of Omp profile \"{_service.GetProfileName( _profile )}\". Omp also loads servers from Claude Code, Cursor and other tools; run /mcp list in a chat to see all.",
				Color = theme.Muted,
			},
		};
		if ( _isProject )
		{
			children.Add( new TextEntry
			{
				Value = _projectFolder,
				Controlled = true,
				Placeholder = @"Project folder, for example F:\Code\MyRepo",
				AccessibilityName = "Project folder",
				OnChange = value =>
				{
					_projectFolder = value;
					MarkChanged();
				},
				OnSubmit = _ => _context.Run( UpdateServers ),
			} );
		}

		if ( path is not null )
			children.Add( new Text { Content = path, Color = theme.Muted, Selectable = true } );

		children.Add( BuildList() );
		if ( _isEditing )
			children.Add( BuildEditor() );
		else if ( path is not null )
			children.Add( theme.BuildButton( "Add server", () => _context.Run( () => StartEdit( null ) ) ) );

		if ( _status.Length > 0 )
			children.Add( new Text { Content = _status, Color = _isError ? theme.Error : theme.Muted, Selectable = true } );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "MCP servers",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Padding = theme.Spacing * 2,
			Gap = theme.Spacing * 1.5,
			Color = theme.Text,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.GetScrollbar(),
			Children = children,
		};
	}

	/// <summary>Builds a scope tab, highlighted when selected.</summary>
	private Button BuildTab( string label, bool isSelected, Action select )
	{
		var button = Theme.Current.BuildButton( label, () => _context.Run( select ) );
		if ( isSelected )
			button.BackgroundColor = Theme.Current.Accent.WithAlpha( 0.2 );
		return button;
	}

	/// <summary>Builds one row per server with its transport, target and actions.</summary>
	private Container BuildList()
	{
		var theme = Theme.Current;
		var rows = new List<Blob>();
		if ( _servers.Count == 0 )
			rows.Add( new Text { Content = "No servers in this file.", Color = theme.Muted } );

		foreach ( var server in _servers )
		{
			rows.Add( new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = server.Name, FontWeight = 600, Width = 160, FlexShrink = 0, Color = server.IsEnabled ? theme.Text : theme.Muted },
					new Text { Content = server.Transport, Width = 50, FlexShrink = 0, Color = theme.Muted },
					new Text { Content = server.Target, FlexGrow = 1, MinWidth = 0, Color = theme.Muted, Selectable = true },
					theme.BuildButton( server.IsEnabled ? "Disable" : "Enable", () => _context.Run( () => Apply( path => OmpMcpConfig.SetEnabled( path, server.Name, !server.IsEnabled ), $"{(server.IsEnabled ? "Disabled" : "Enabled")} {server.Name}." ) ) ),
					theme.BuildButton( "Edit", () => _context.Run( () => StartEdit( server ) ) ),
					theme.BuildButton( "Remove", () => _context.Run( () => Apply( path => OmpMcpConfig.Remove( path, server.Name ), $"Removed {server.Name}." ) ) ),
				],
			} );
		}

		return new Container
		{
			Padding = theme.Spacing * 1.5,
			Gap = theme.Spacing,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Raised,
			Children = rows,
		};
	}

	/// <summary>Builds the name entry, the JSON definition editor and its actions.</summary>
	private Container BuildEditor()
	{
		var theme = Theme.Current;
		return new Container
		{
			Padding = theme.Spacing * 1.5,
			Gap = theme.Spacing,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Raised,
			Children =
			[
				new Text { Content = _editedName is null ? "New server" : $"Edit {_editedName}", FontWeight = 600 },
				new TextEntry
				{
					Value = _name,
					Controlled = true,
					Placeholder = "Server name",
					AccessibilityName = "Server name",
					OnChange = value =>
					{
						_name = value;
						MarkChanged();
					},
				},
				new Text { Content = "Definition: stdio uses \"command\", \"args\", \"env\"; http and sse use \"url\", \"headers\". An env or header value that names an environment variable reads it.", Color = theme.Muted },
				new TextEditor
				{
					Controller = _definition!,
					AccessibilityName = "Server definition",
					AccessibilityRole = AccessibilityRole.TextEditor,
					Height = theme.FontSize * 1.5 * 10 + theme.Spacing * 2,
					Padding = theme.Spacing,
					BackgroundColor = theme.Panel,
					BorderColor = theme.Border,
					BorderWidth = theme.BorderWidth,
					BorderRadius = theme.Radius,
				},
				new Row
				{
					Gap = theme.Spacing,
					Children =
					[
						theme.BuildButton( "Save", () => _context.Run( SaveEdit ) ),
						theme.BuildButton( "Cancel", () => _context.Run( StopEdit ) ),
					],
				},
			],
		};
	}

	/// <summary>Shows another profile's user file or the project file.</summary>
	private void UpdateScope( SettingsProfile profile, bool isProject )
	{
		_profile = profile;
		_isProject = isProject;
		StopEdit();
		UpdateServers();
	}

	/// <summary>Reads the shown file again.</summary>
	private void UpdateServers()
	{
		_status = "";
		_isError = false;
		try
		{
			_servers = ComputePath() is { } path ? OmpMcpConfig.Read( path ) : [];
		}
		catch ( Exception exception ) when ( exception is InvalidDataException or IOException or UnauthorizedAccessException )
		{
			_servers = [];
			SetStatus( $"Could not read the file: {exception.Message}", true );
		}

		MarkChanged();
	}

	/// <summary>Opens the editor on a server, or on the template for a new one.</summary>
	private void StartEdit( OmpMcpServer? server )
	{
		_definition?.Dispose();
		_definition = new( new TextDocument( server?.Json ?? OmpMcpConfig.Template ) );
		_editedName = server?.Name;
		_name = server?.Name ?? "";
		_isEditing = true;
		MarkChanged();
	}

	/// <summary>Closes the editor without saving.</summary>
	private void StopEdit()
	{
		_definition?.Dispose();
		_definition = null;
		_editedName = null;
		_name = "";
		_isEditing = false;
		MarkChanged();
	}

	/// <summary>Saves the edited server and closes the editor, or shows why it cannot.</summary>
	private void SaveEdit()
	{
		var name = _name.Trim();
		var definition = _definition?.Document.GetText() ?? "";
		if ( Apply( path => OmpMcpConfig.Save( path, name, definition, _editedName ), $"Saved {name}." ) )
			StopEdit();
	}

	/// <summary>Changes the shown file, reads it again and reports the result; returns whether the change was made.</summary>
	private bool Apply( Action<string> change, string done )
	{
		if ( ComputePath() is not { } path )
			return false;
		try
		{
			change( path );
		}
		catch ( Exception exception ) when ( exception is FormatException or InvalidOperationException or KeyNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException )
		{
			SetStatus( exception.Message, true );
			return false;
		}

		UpdateServers();
		SetStatus( $"{done} New chats load it at start; in an open chat, run /mcp reload.", false );
		return true;
	}

	/// <summary>Sets the status line.</summary>
	private void SetStatus( string text, bool isError )
	{
		_status = text;
		_isError = isError;
		MarkChanged();
	}

	/// <summary>Keeps the profile when the workspace layout is restored.</summary>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( _profile );

	/// <summary>Releases the definition editor.</summary>
	public override void Closed() => _definition?.Dispose();
}
