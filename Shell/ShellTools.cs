using System.Text.Json;
using Lokn.Core;

namespace Lokn;

/// <summary>Registers the tools that let an agent see and arrange the workspace and run palette commands.</summary>
internal static class ShellTools
{
	/// <summary>Defines the shortest panel identifier prefix accepted in place of a full identifier.</summary>
	private const int MinimumPrefix = 4;

	/// <summary>Describes the panel argument shared by tools that act on one panel.</summary>
	private const string PanelArgument = """
		"panel": { "type": "string", "description": "A panel id from shell_state, a unique prefix of one, or \"self\" for the panel the agent is running in." }
		""";

	/// <summary>Adds the workspace tools to the shell's module.</summary>
	internal static ModuleBuilder AddShellTools( this ModuleBuilder builder ) => builder
		.Tool( "state",
			"Describes the Lokn window: every open panel (id, type, title), the split layout tree, the active pane, the panel types that can be opened, and which panel is \"self\" (the one you are running in). Call this before arranging panels.",
			"""{ "type": "object", "properties": {}, "additionalProperties": false }""",
			( context, _ ) => BuildState( context ) )
		.Tool( "open_panel",
			"Opens a new panel of a registered type. By default it splits the target panel's pane and opens to its right, or fills the target pane when it is empty. Returns the new panel's id.",
			$$"""
			{
				"type": "object",
				"properties": {
					"type": { "type": "string", "description": "A panel type id from shell_state, such as conversations.chat." },
					"beside": { "type": "string", "description": "The panel to open next to: an id, a unique id prefix, or \"self\". Defaults to your own panel. Beside a panel that is not on screen, the new panel waits hidden and appears with it." },
					"placement": { "type": "string", "enum": ["right", "left", "top", "bottom"], "description": "Which side of the target panel the new one takes. Defaults to right." }
				},
				"required": ["type"],
				"additionalProperties": false
			}
			""",
			OpenPanel )
		.Tool( "focus_panel",
			"Makes a panel the active one, highlighting it for the user. New panels and commands such as split act on the active panel.",
			$$"""{ "type": "object", "properties": { {{PanelArgument}} }, "required": ["panel"], "additionalProperties": false }""",
			( context, arguments ) =>
			{
				var panel = ResolvePanel( context, arguments, "panel" );
				context.App.Panels.Activate( panel.Id );
				return new( $"Focused {panel.Title} ({panel.Id})." );
			} )
		.Tool( "move_panel",
			"Moves a panel next to another one, or swaps the two. Panels keep their state when they move.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{PanelArgument}},
					"target": { "type": "string", "description": "The panel to move next to: an id, a unique id prefix, or \"self\"." },
					"placement": { "type": "string", "enum": ["swap", "right", "left", "top", "bottom"], "description": "Which side of the target to dock on, or swap places with it. Defaults to swap." }
				},
				"required": ["panel", "target"],
				"additionalProperties": false
			}
			""",
			MovePanel )
		.Tool( "close_panel",
			"Closes a panel, stopping its work. It cannot close the panel you are running in.",
			$$"""{ "type": "object", "properties": { {{PanelArgument}} }, "required": ["panel"], "additionalProperties": false }""",
			( context, arguments ) =>
			{
				var panel = ResolvePanel( context, arguments, "panel" );
				if ( panel.Id == context.Caller )
					throw new InvalidOperationException( "You cannot close the panel you are running in; ask the user to close it." );

				context.App.Panels.Close( panel.Id );
				return new( $"Closed {panel.Title}." );
			} )
		.Tool( "list_commands",
			"Lists the commands in Lokn's command palette, with ids, titles, shortcuts and whether they can run now. Every query word must match the title or id.",
			"""
			{
				"type": "object",
				"properties": { "query": { "type": "string", "description": "Words to filter by, such as \"split\" or \"open\"." } },
				"additionalProperties": false
			}
			""",
			( context, arguments ) =>
			{
				var words = ReadString( arguments, "query" )?.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) ?? [];
				return AgentToolResult.Json( context.App.Commands.Items
					.Where( command => words.All( word => $"{command.Title} {command.Id}".Contains( word, StringComparison.OrdinalIgnoreCase ) ) )
					.OrderBy( command => command.Id, StringComparer.Ordinal )
					.Select( command => new
					{
						command.Id,
						command.Title,
						Shortcut = command.Shortcut is { } shortcut ? $"{shortcut.Modifiers}+{shortcut.Key}" : null,
						CanRun = context.App.Commands.CanExecute( command.Id ),
					} ) );
			} )
		.ToolAsync( "run_command",
			"Runs a command from Lokn's command palette by id, exactly as if the user picked it. Commands act on the active panel, so focus a panel first when it matters.",
			"""
			{
				"type": "object",
				"properties": { "id": { "type": "string", "description": "A command id from shell_list_commands." } },
				"required": ["id"],
				"additionalProperties": false
			}
			""",
			async ( context, arguments ) =>
			{
				var id = ReadString( arguments, "id" ) ?? throw new ArgumentException( "Give the command id." );
				if ( context.App.Commands.Items.All( command => command.Id != id ) )
					throw new ArgumentException( $"There is no command {id}; call shell_list_commands." );
				if ( !context.App.Commands.CanExecute( id ) )
					throw new InvalidOperationException( $"{id} cannot run right now." );

				return await context.App.Commands.ExecuteAsync( id ) ? new( $"Ran {id}." ) : AgentToolResult.Error( $"{id} did not complete." );
			} );

	/// <summary>Describes the panels, the layout tree, the active pane and the openable panel types.</summary>
	private static AgentToolResult BuildState( AgentToolContext context )
	{
		var panels = context.App.Panels;
		var open = panels.Panels.ToDictionary( panel => panel.Id );
		object BuildNode( DockNode node ) => node switch
		{
			DockSplit split => new
			{
				Split = split.Axis == SplitAxis.Horizontal ? "columns" : "rows",
				Ratio = Math.Round( split.Ratio, 3 ),
				First = BuildNode( split.First ),
				Second = BuildNode( split.Second ),
			},
			DockPane { PanelId: { } id } pane when open.TryGetValue( id, out var panel ) => new
			{
				Pane = pane.Id,
				Panel = panel.Id,
				Type = panel.TypeId,
				panel.Title,
				Active = pane.Id == panels.ActivePaneId ? true : (bool?)null,
				Self = panel.Id == context.Caller ? true : (bool?)null,
			},
			_ => new
			{
				Pane = node.Id,
				Empty = true,
			},
		};

		return AgentToolResult.Json( new
		{
			Self = context.Caller,
			ActivePanel = panels.ActivePanelId,
			Layout = BuildNode( panels.Root ),
			// Panels another saved workspace shows: still open and working, out of this layout until focused or switched to.
			Hidden = panels.Panels.Where( panel => !panels.IsShown( panel.Id ) ).Select( panel => new
			{
				Panel = panel.Id,
				Type = panel.TypeId,
				panel.Title,
			} ),
			PanelTypes = panels.Types.Select( type => new
			{
				Type = type.Id,
				type.Title,
			} ),
		} );
	}

	/// <summary>Opens a panel beside a target panel, on the requested side: by default beside the caller, hidden with it while the user is not looking at it.</summary>
	private static AgentToolResult OpenPanel( AgentToolContext context, JsonElement arguments )
	{
		var panels = context.App.Panels;
		var type = ReadString( arguments, "type" ) ?? throw new ArgumentException( "Give the panel type." );
		if ( panels.Types.All( item => item.Id != type ) )
			throw new ArgumentException( $"There is no panel type {type}; shell_state lists them." );

		var placement = ReadPlacement( arguments, PanelPlacement.Right );
		if ( placement == PanelPlacement.Swap )
			throw new ArgumentException( "Open a panel to the right, left, top or bottom." );

		// A chat working in the background must not drop its panel into the layout the user is looking at.
		var target = ReadString( arguments, "beside" ) is not null ? ResolvePanel( context, arguments, "beside" ).Id : context.Caller ?? panels.ActivePanelId;
		if ( target is { } hidden && !panels.IsShown( hidden ) )
		{
			var waiting = panels.OpenBeside( type, hidden );
			return AgentToolResult.Json( new
			{
				Panel = waiting,
				Title = panels.Panels.First( panel => panel.Id == waiting ).Title,
				Note = "The panel it opens beside is not on screen, so the new panel waits hidden and appears beside it when the user opens it.",
			} );
		}

		var targetPane = panels.Panes.FirstOrDefault( pane => pane.PanelId == target )?.Id ?? panels.ActivePaneId;
		var opened = panels.Open( type, targetPane );
		// Open fills an empty pane or splits to the right; any other side is a move from there.
		if ( placement != PanelPlacement.Right && target is not null )
			panels.Move( opened, targetPane, placement );

		return AgentToolResult.Json( new
		{
			Panel = opened,
			Title = panels.Panels.First( panel => panel.Id == opened ).Title,
		} );
	}

	/// <summary>Docks a panel beside another, or swaps them.</summary>
	private static AgentToolResult MovePanel( AgentToolContext context, JsonElement arguments )
	{
		var panel = ResolvePanel( context, arguments, "panel" );
		var target = ResolvePanel( context, arguments, "target" );
		if ( panel.Id == target.Id )
			throw new ArgumentException( "A panel cannot move next to itself." );

		var pane = context.App.Panels.Panes.FirstOrDefault( item => item.PanelId == target.Id )
			?? throw new InvalidOperationException( $"{target.Title} is hidden by this workspace's layout; focus it first to bring it back." );
		// A hidden panel comes back into the layout before it docks.
		if ( !context.App.Panels.IsShown( panel.Id ) )
			context.App.Panels.Activate( panel.Id );
		var placement = ReadPlacement( arguments, PanelPlacement.Swap );
		if ( !context.App.Panels.Move( panel.Id, pane.Id, placement ) )
			throw new InvalidOperationException( $"{panel.Title} could not move." );

		return new( placement == PanelPlacement.Swap ? $"Swapped {panel.Title} and {target.Title}." : $"Moved {panel.Title} {placement.ToString().ToLowerInvariant()} of {target.Title}." );
	}

	/// <summary>Finds an open panel from an id, a unique id prefix, or <c>self</c>.</summary>
	private static PanelInfo ResolvePanel( AgentToolContext context, JsonElement arguments, string property )
	{
		var value = ReadString( arguments, property )?.Trim() ?? throw new ArgumentException( $"Give {property}." );
		var panels = context.App.Panels.Panels;
		if ( value.Equals( "self", StringComparison.OrdinalIgnoreCase ) )
			return panels.FirstOrDefault( panel => panel.Id == context.Caller ) ?? throw new InvalidOperationException( "You are not running in a panel." );
		if ( Guid.TryParse( value, out var id ) )
			return panels.FirstOrDefault( panel => panel.Id == id ) ?? throw new ArgumentException( $"No panel {value} is open; call shell_state." );

		var matches = value.Length >= MinimumPrefix
			? panels.Where( panel => panel.Id.ToString().StartsWith( value, StringComparison.OrdinalIgnoreCase ) ).ToArray()
			: [];
		return matches.Length == 1 ? matches[0] : throw new ArgumentException( matches.Length == 0
			? $"No panel matches {value}; call shell_state."
			: $"{value} matches several panels; give more of the id." );
	}

	/// <summary>Reads the optional placement argument.</summary>
	private static PanelPlacement ReadPlacement( JsonElement arguments, PanelPlacement fallback ) => ReadString( arguments, "placement" ) switch
	{
		null => fallback,
		var value when Enum.TryParse<PanelPlacement>( value, ignoreCase: true, out var placement ) => placement,
		var value => throw new ArgumentException( $"Unknown placement {value}." ),
	};

	/// <summary>Reads an optional string argument.</summary>
	private static string? ReadString( JsonElement arguments, string property ) =>
		arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
