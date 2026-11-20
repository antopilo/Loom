using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokn.Core;

/// <summary>Persists docking topology and feature-owned panel state between application sessions.</summary>
public sealed partial class PanelWorkspace
{
	/// <summary>Defines the supported workspace document version.</summary>
	private const int LayoutVersion = 1;

	/// <summary>Stores the malformed workspace path that must remain available for recovery.</summary>
	private string? _invalidLayoutPath;

	/// <summary>Gets the per-user workspace document path, in the data folder.</summary>
	public static string DefaultLayoutPath => LoknPaths.Combine( "workspace.json" );

	/// <summary>Saves the current tree and its shown panels atomically without unloading modules; hidden panels are not kept.</summary>
	public bool SaveLayout( string path )
	{
		try
		{
			var fullPath = Path.GetFullPath( path );
			if ( string.Equals( fullPath, _invalidLayoutPath, StringComparison.OrdinalIgnoreCase ) )
				return false;

			var document = BuildLayout();
			var json = JsonSerializer.Serialize( document, new JsonSerializerOptions { WriteIndented = true } );
			Directory.CreateDirectory( Path.GetDirectoryName( fullPath )! );
			var temporary = fullPath + ".tmp";
			File.WriteAllText( temporary, json );
			File.Move( temporary, fullPath, overwrite: true );
			return true;
		}
		catch ( Exception error )
		{
			_app.ReportError( "Save workspace", error );
			return false;
		}
	}

	/// <summary>Restores a validated workspace after module registration, omitting unavailable panel types.</summary>
	public bool RestoreLayout( string path )
	{
		if ( !File.Exists( path ) )
			return false;

		SavedLayout saved;
		try
		{
			saved = JsonSerializer.Deserialize<SavedLayout>( File.ReadAllText( path ) )
				?? throw new InvalidDataException( "The workspace document is empty." );
			EnsureValidLayout( saved );
			saved = ResolveFormerTypes( saved );
		}
		catch ( Exception error )
		{
			_invalidLayoutPath = Path.GetFullPath( path );
			_app.ReportError( "Restore workspace; the original file is preserved", error );
			return false;
		}

		var available = saved.Panels.Where( panel => _types.TryGetValue( panel.TypeId, out var type ) && !type.Lifetime.IsCancellationRequested ).ToArray();
		var identifiers = available.Select( panel => panel.Id ).ToHashSet();
		var root = BuildRestoredNode( saved.Root, identifiers ) ?? new DockPane( Guid.NewGuid(), null );

		// Capture and validate first: malformed documents never close the currently usable workspace.
		foreach ( var session in _sessions.Values.ToArray() )
			ReleasePanel( session );
		_sessions.Clear();
		Root = root;
		ActivePaneId = saved.ActivePaneId;
		EnsureActivePane();
		foreach ( var panel in available )
		{
			var type = _types[panel.TypeId];
			_sessions.Add( panel.Id, BuildSession( type, panel.Id, input: null, panel.State, panel.Title, RestoreHost( type, panel.Host ) ) );
		}

		_invalidLayoutPath = null;
		_app.MarkChanged();
		return true;
	}

	/// <summary>Creates one owned panel model from its factory and optional restored state, on a host already resolved for its type.</summary>
	private PanelSession BuildSession( RegisteredPanel type, Guid id, object? input, JsonElement? state = null, string? title = null, string? hostId = null )
	{
		var lifetime = CancellationTokenSource.CreateLinkedTokenSource( type.Lifetime );
		var context = new PanelContext( _app, id, lifetime.Token, input, state, hostId );
		PanelContent? content = null;
		string? failure = null;
		try
		{
			content = type.Definition.Create( context ) ?? throw new InvalidOperationException( "The panel factory returned null." );
		}
		catch ( Exception error )
		{
			failure = error.Message;
			_app.ReportError( type.Definition.Info.Title, error );
		}

		return new( new( id, type.Definition.Info.Id, title ?? type.Definition.Info.Title ), content, failure, lifetime, hostId );
	}

	/// <summary>Reads panels saved under a former panel type identifier as the type that took it over.</summary>
	private SavedLayout ResolveFormerTypes( SavedLayout layout ) =>
		layout with { Panels = [.. layout.Panels.Select( panel => panel with { TypeId = ResolveTypeId( panel.TypeId ) } )] };

	/// <summary>Checks topology and ownership before replacing live panel instances.</summary>
	private static void EnsureValidLayout( SavedLayout layout )
	{
		if ( layout.Version != LayoutVersion || layout.Root is null || layout.Panels is null )
			throw new InvalidDataException( "The workspace document version or structure is unsupported." );

		var panels = new HashSet<Guid>();
		foreach ( var panel in layout.Panels )
		{
			if ( panel is null || panel.Id == Guid.Empty || string.IsNullOrWhiteSpace( panel.TypeId ) || !panels.Add( panel.Id ) )
				throw new InvalidDataException( "The workspace contains an invalid or duplicate panel." );
		}

		var nodes = new HashSet<Guid>();
		var references = new HashSet<Guid>();
		EnsureValidNode( layout.Root, nodes, references, panels );
		if ( !references.SetEquals( panels ) )
			throw new InvalidDataException( "The workspace contains panels outside its pane tree." );
	}

	/// <summary>Checks unique node identifiers, split fractions, and one pane per panel.</summary>
	private static void EnsureValidNode( SavedNode node, HashSet<Guid> nodes, HashSet<Guid> references, HashSet<Guid> panels )
	{
		if ( node.Id == Guid.Empty || !nodes.Add( node.Id ) )
			throw new InvalidDataException( "The workspace contains an invalid or duplicate node." );

		if ( node.Kind == "pane" )
		{
			if ( node.First is not null || node.Second is not null || node.PanelId is { } panel && (!panels.Contains( panel ) || !references.Add( panel )) )
				throw new InvalidDataException( "The workspace pane references an invalid panel." );
			return;
		}

		if ( node.Kind != "split" || node.First is null || node.Second is null || node.PanelId is not null
			|| !Enum.IsDefined( node.Axis ) || !double.IsFinite( node.Ratio ) || node.Ratio < MinimumRatio || node.Ratio > 1.0 - MinimumRatio )
			throw new InvalidDataException( "The workspace split is invalid." );

		EnsureValidNode( node.First, nodes, references, panels );
		EnsureValidNode( node.Second, nodes, references, panels );
	}

	/// <summary>Builds a serializable tree without coupling JSON to runtime record inheritance.</summary>
	private static SavedNode BuildSavedNode( DockNode node ) => node switch
	{
		DockPane pane => new( "pane", pane.Id, pane.PanelId, default, default, null, null ),
		DockSplit split => new( "split", split.Id, null, split.Axis, split.Ratio, BuildSavedNode( split.First ), BuildSavedNode( split.Second ) ),
		_ => throw new InvalidDataException( "The workspace contains an unknown node." ),
	};

	/// <summary>Rebuilds the tree while collapsing panes whose panel is not among <paramref name="panels"/>; null keeps every pane.</summary>
	private static DockNode? BuildRestoredNode( SavedNode node, HashSet<Guid>? panels )
	{
		if ( node.Kind == "pane" )
			return node.PanelId is { } panel && panels is not null && !panels.Contains( panel ) ? null : new DockPane( node.Id, node.PanelId );

		var first = BuildRestoredNode( node.First!, panels );
		var second = BuildRestoredNode( node.Second!, panels );
		if ( first is null )
			return second;
		if ( second is null )
			return first;

		return new DockSplit( node.Id, node.Axis, node.Ratio, first, second );
	}

	/// <summary>Stores the versioned layout document and selected pane.</summary>
	private sealed record SavedLayout( int Version, Guid ActivePaneId, SavedNode Root, SavedPanel[] Panels );

	/// <summary>Stores one panel factory identifier, its feature-owned JSON state, and the paired host it works on, omitted for This PC and for types
	/// that are not host-bound, so a layout without paired hosts reads as before.</summary>
	private sealed record SavedPanel( Guid Id, string TypeId, string Title, JsonElement? State, [property: JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )] string? Host = null );

	/// <summary>Builds the saved form of an open panel.</summary>
	private SavedPanel BuildSavedPanel( PanelSession session ) =>
		new( session.Info.Id, session.Info.TypeId, session.Info.Title, CaptureState( session ), session.HostId is null or LoknHost.LocalId ? null : session.HostId );

	/// <summary>Stores a pane or split node using an explicit JSON discriminator.</summary>
	private sealed record SavedNode( string Kind, Guid Id, Guid? PanelId, SplitAxis Axis, double Ratio, SavedNode? First, SavedNode? Second );
}
