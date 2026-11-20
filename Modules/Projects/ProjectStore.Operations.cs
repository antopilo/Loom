using System.Text.Json;
using System.Text.Json.Serialization;
using Lokn.Core;

namespace Lokn.Modules.Projects;

/// <summary>Describes one change to a store, which another store applies the same way: a host's change to its mirrors, or a mirror's request to its host.</summary>
/// <remarks>See <c>docs/remote.md</c>: one operation per public mutator, with its named arguments.</remarks>
/// <param name="S">The store: <see cref="Conversations"/> or <see cref="Projects"/>.</param>
/// <param name="Op">The mutator's method name, such as <c>Create</c>.</param>
/// <param name="A">The mutator's arguments as one object with a camelCase property per parameter.</param>
public sealed record StoreOp( string S, string Op, JsonElement A )
{
	/// <summary>Names the conversation store.</summary>
	public const string Conversations = "conversations";

	/// <summary>Names the project store.</summary>
	public const string Projects = "projects";

	/// <summary>Gets the JSON options of the remote protocol: camelCase names and enums as their names.</summary>
	public static JsonSerializerOptions JsonOptions { get; } = BuildJsonOptions();

	/// <summary>Builds the protocol's JSON options once.</summary>
	private static JsonSerializerOptions BuildJsonOptions()
	{
		var options = new JsonSerializerOptions( JsonSerializerDefaults.Web );
		options.Converters.Add( new JsonStringEnumConverter() );
		options.MakeReadOnly( populateMissingResolver: true );
		return options;
	}

	/// <summary>Builds an operation from a mutator's arguments.</summary>
	public static StoreOp Build( string store, string op, object args ) => new( store, op, JsonSerializer.SerializeToElement( args, JsonOptions ) );

	/// <summary>Reads one named argument, or the type's default when it is missing or null.</summary>
	public T? Read<T>( string name ) => TryGet( name, out var value ) ? value.Deserialize<T>( JsonOptions ) : default;

	/// <summary>Reads one named argument that must be present.</summary>
	/// <exception cref="ArgumentException">The argument is missing or null.</exception>
	public T Require<T>( string name ) => TryGet( name, out var value ) && value.Deserialize<T>( JsonOptions ) is { } read ? read
		: throw new ArgumentException( $"The {S} operation {Op} needs the argument {name}." );

	/// <summary>Finds one named argument that is not null.</summary>
	private bool TryGet( string name, out JsonElement value )
	{
		value = default;
		return A.ValueKind == JsonValueKind.Object && A.TryGetProperty( name, out value ) && value.ValueKind != JsonValueKind.Null;
	}
}

/// <summary>Publishes each change as a <see cref="StoreOp"/>, applies operations, and mirrors another process's projects.</summary>
/// <remarks>
/// A mirror shows a host's projects: <see cref="Load"/> replaces them from the host's snapshot, and every public mutator changes the mirror at
/// once and sends its operation through <see cref="Forward"/>, so a caller reads its own change straight back. The host's operation coming back
/// through <see cref="Apply"/> finds it applied already, since every mutator returns early on a value it already holds. A mirror never reads or
/// writes the projects file.
/// </remarks>
public sealed partial class ProjectStore
{
	/// <summary>Stores where a mirror sends its changes, or null when the store is the authority.</summary>
	private Func<StoreOp, bool>? _forward;

	/// <summary>Reports whether the store mirrors another process's, so it never touches the projects file again.</summary>
	private bool _isMirror;

	/// <summary>Reports whether an operation is being applied, which changes the store even in a mirror.</summary>
	private bool _isApplying;

	/// <summary>Raised on the UI thread after a public mutator or <see cref="Apply"/> changed the projects, with the operation that repeats the change.</summary>
	public event Action<StoreOp>? Mutated;

	/// <summary>Gets or sets where a mirror sends each change it applied, returning whether it was sent; null for a store that is the authority.</summary>
	/// <remarks>Setting it makes the store a mirror until <see cref="EndMirror"/>: it does not read or write the projects file meanwhile, and holds the host's projects once <see cref="Load"/> gives them.</remarks>
	public Func<StoreOp, bool>? Forward
	{
		get => _forward;
		set
		{
			if ( value is not null )
			{
				_isLoaded = true;
				_isMirror = true;
			}
			_forward = value;
		}
	}

	/// <summary>Gets whether the store mirrors another process's projects.</summary>
	public bool IsMirror => _isMirror;

	/// <summary>Makes a mirror the authority over the projects file again, such as after this PC's daemon stopped and this Loom took its chats back: it
	/// sends nothing any more and reads the file the other process wrote in place of what it mirrored (<see cref="Reload"/>).</summary>
	/// <remarks>Call it on the UI thread, before <c>ConversationStore.EndMirror</c>. A store that mirrors nothing is left alone; one without a file keeps what it mirrored.</remarks>
	public void EndMirror()
	{
		if ( !_isMirror )
			return;

		_forward = null;
		_isMirror = false;
		Reload();
	}

	/// <summary>Replaces every project with a host's snapshot, such as a <c>sync</c> answer.</summary>
	public void Load( IEnumerable<ProjectInfo> projects )
	{
		_isLoaded = true;
		_canSave = true;
		_projects.Clear();
		foreach ( var project in projects )
			_projects[project.Id] = project;
		_version.Value++;
	}

	/// <summary>Applies one operation as its mutator would, even in a mirror, and raises <see cref="Mutated"/> when it changed the projects.</summary>
	/// <exception cref="ArgumentException">The operation is for another store, unknown, or misses an argument.</exception>
	public void Apply( StoreOp op )
	{
		if ( op.S != StoreOp.Projects )
			throw new ArgumentException( $"The project store cannot apply a {op.S} operation." );

		_isApplying = true;
		try
		{
			switch ( op.Op )
			{
				case nameof( Create ):
					EnsureLoaded();
					Create( new ProjectInfo( op.Require<Guid>( "id" ), op.Require<SettingsProfile>( "profile" ), op.Require<string>( "name" ).Trim(),
						Path.GetFullPath( op.Require<string>( "directory" ) ) ) );
					break;
				case nameof( Update ):
					Update( op.Require<Guid>( "id" ), op.Require<string>( "name" ), op.Require<string>( "directory" ) );
					break;
				case nameof( Delete ):
					Delete( op.Require<Guid>( "id" ) );
					break;
				default:
					throw new ArgumentException( $"The project store has no operation {op.Op}." );
			}
		}
		finally
		{
			_isApplying = false;
		}
	}

	/// <summary>Sends a change to the host, while the store mirrors one and is not applying an operation.</summary>
	/// <returns>Whether the change was forwarded.</returns>
	private bool TryForward( string op, object args )
	{
		if ( _forward is not { } forward || _isApplying )
			return false;

		forward( StoreOp.Build( StoreOp.Projects, op, args ) );
		return true;
	}

	/// <summary>Publishes a change just applied: a mirror sends it to the host, otherwise <see cref="Mutated"/> is raised with it.</summary>
	private void MarkMutated( string op, object args )
	{
		if ( !TryForward( op, args ) )
			Mutated?.Invoke( StoreOp.Build( StoreOp.Projects, op, args ) );
	}
}
