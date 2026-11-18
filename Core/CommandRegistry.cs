using Goo;

namespace Lokn.Core;

/// <summary>Routes palette and keyboard execution through the same guarded command registry.</summary>
public sealed class CommandRegistry
{
	/// <summary>Stores the owning application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores registrations by globally namespaced command identifier.</summary>
	private readonly Dictionary<string, RegisteredCommand> _commands = new( StringComparer.Ordinal );

	/// <summary>Stores the native window's asynchronous UI dispatcher while attached.</summary>
	private Func<Func<Task<bool>>, Task<bool>>? _dispatch;

	/// <summary>Gets the registered command metadata.</summary>
	public IReadOnlyList<CommandInfo> Items => _commands.Values.Select( value => value.Definition.Info ).ToArray();

	/// <summary>Creates the application command registry.</summary>
	internal CommandRegistry( LoknApplication app ) => _app = app;

	/// <summary>Checks a command's current availability and execution state.</summary>
	public bool CanExecute( string id )
	{
		if ( !_commands.TryGetValue( id, out var command ) || command.IsRunning || command.Lifetime.IsCancellationRequested )
			return false;

		try
		{
			return command.Definition.CanExecute?.Invoke( new( _app, command.Lifetime ) ) ?? true;
		}
		catch ( Exception )
		{
			// Availability is queried during rendering; a failing predicate only disables its command.
			return false;
		}
	}

	/// <summary>Executes an available command and contains exceptions from feature code.</summary>
	public async Task<bool> ExecuteAsync( string id )
	{
		if ( _dispatch is null )
			return await ExecuteCoreAsync( id );

		try
		{
			return await _dispatch( () => ExecuteCoreAsync( id ) );
		}
		catch ( OperationCanceledException )
		{
			return false;
		}
	}

	/// <summary>Attaches or detaches the native command execution dispatcher.</summary>
	internal void BindDispatcher( Func<Func<Task<bool>>, Task<bool>>? dispatch ) => _dispatch = dispatch;

	/// <summary>Executes a command inside the native async dispatcher or a headless caller.</summary>
	private async Task<bool> ExecuteCoreAsync( string id )
	{
		if ( !CanExecute( id ) )
			return false;

		var command = _commands[id];
		command.IsRunning = true;
		_app.MarkChanged();

		try
		{
			await command.Definition.Execute( new( _app, command.Lifetime ) );
			return !command.Lifetime.IsCancellationRequested;
		}
		catch ( OperationCanceledException ) when ( command.Lifetime.IsCancellationRequested )
		{
			return false;
		}
		catch ( Exception error )
		{
			_app.ReportError( command.Definition.Info.Title, error );
			return false;
		}
		finally
		{
			command.IsRunning = false;
			_app.MarkChanged();
		}
	}

	/// <summary>Finds the available command matching an exact global shortcut.</summary>
	public string? FindShortcut( Key key, KeyModifiers modifiers )
	{
		return _commands.Values
			.Where( value => value.Definition.Info.Shortcut == new CommandShortcut( key, modifiers ) )
			.Select( value => value.Definition.Info.Id )
			.FirstOrDefault( CanExecute );
	}

	/// <summary>Rejects duplicate identifiers and ambiguous global shortcuts before registration.</summary>
	internal void EnsureAvailable( IReadOnlyList<CommandDefinition> definitions )
	{
		var others = _commands.Values.ToArray();
		var identifiers = others.Select( value => value.Definition.Info.Id ).ToHashSet( StringComparer.Ordinal );
		var shortcuts = others
			.Select( value => value.Definition.Info.Shortcut )
			.OfType<CommandShortcut>()
			.ToHashSet();

		// Palette entry shortcuts belong to the shell and cannot be shadowed by feature modules.
		shortcuts.Add( new( Key.P, KeyModifiers.PrimaryShift ) );
		shortcuts.Add( new( Key.K, KeyModifiers.Primary ) );

		foreach ( var definition in definitions )
		{
			if ( !identifiers.Add( definition.Info.Id ) )
				throw new InvalidOperationException( $"Duplicate command: {definition.Info.Id}" );

			if ( definition.Info.Shortcut is { } shortcut && !shortcuts.Add( shortcut ) )
				throw new InvalidOperationException( $"Shortcut already registered: {shortcut}" );
		}
	}

	/// <summary>Commits contributions after all module registrations pass validation.</summary>
	internal void Register( IEnumerable<CommandDefinition> definitions, CancellationToken lifetime )
	{
		foreach ( var definition in definitions )
			_commands.Add( definition.Info.Id, new( definition, lifetime ) );
	}

	/// <summary>Removes every command owned by a module, including running commands from lookup.</summary>
	internal void RemoveModule( string moduleId )
	{
		foreach ( var id in Items.Where( item => item.ModuleId == moduleId ).Select( item => item.Id ) )
			_commands.Remove( id );
	}

	/// <summary>Stores one registration and its in-flight execution state.</summary>
	private sealed class RegisteredCommand( CommandDefinition definition, CancellationToken lifetime )
	{
		/// <summary>Gets the immutable command definition.</summary>
		internal CommandDefinition Definition { get; } = definition;

		/// <summary>Gets the owning module cancellation token.</summary>
		internal CancellationToken Lifetime { get; } = lifetime;

		/// <summary>Gets or sets whether this registration is already executing.</summary>
		internal bool IsRunning
		{
			get; set;
		}
	}
}
