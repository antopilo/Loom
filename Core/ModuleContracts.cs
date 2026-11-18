using System.Text.Json;
using Goo;

namespace Lokn.Core;

/// <summary>Contributes an independently registered feature to the application.</summary>
public interface ILoknModule
{
	/// <summary>Gets the stable module identifier used to namespace contributions.</summary>
	string Id
	{
		get;
	}

	/// <summary>Gets the human-readable module name.</summary>
	string Title
	{
		get;
	}

	/// <summary>Gets whether the module can be disabled while the application runs.</summary>
	bool CanUnload => true;

	/// <summary>Gets module identifiers that must remain loaded while this module runs.</summary>
	IReadOnlyList<string> Dependencies => [];

	/// <summary>Declares panels and commands before any contributions become visible.</summary>
	void Register( ModuleBuilder builder );

	/// <summary>Starts the feature after its contributions are registered.</summary>
	void Start( ModuleContext context )
	{
	}

	/// <summary>Stops feature-owned work after its lifetime is cancelled.</summary>
	void Stop()
	{
	}
}

/// <summary>Provides application services and a lifetime cancelled at module unload.</summary>
/// <param name="App">The owning application on its UI thread.</param>
/// <param name="Lifetime">The module cancellation token.</param>
public sealed record ModuleContext( LoknApplication App, CancellationToken Lifetime )
{
	/// <summary>Posts guarded module work to the UI thread while its lifetime remains active.</summary>
	public bool Post( Action action )
	{
		if ( Lifetime.IsCancellationRequested )
			return false;

		return App.Post( () =>
		{
			if ( Lifetime.IsCancellationRequested )
				return;

			try
			{
				action();
			}
			catch ( Exception error )
			{
				App.ReportError( "Module action", error );
			}
		} );
	}

	/// <summary>Runs an action once the application finished starting and restored its workspace, such as opening the module's startup view.</summary>
	/// <remarks>Does nothing for a module loaded or reloaded after startup, so a reload never changes what the user sees.</remarks>
	public void WhenStarted( Action action )
	{
		if ( App.IsStarted )
			return;

		App.Started += () =>
		{
			if ( Lifetime.IsCancellationRequested )
				return;

			try
			{
				action();
			}
			catch ( Exception error )
			{
				App.ReportError( "Module startup", error );
			}
		};
	}
}

/// <summary>Provides application services and the executing command's module lifetime.</summary>
/// <param name="App">The owning application on its UI thread.</param>
/// <param name="Lifetime">The module cancellation token.</param>
public sealed record CommandContext( LoknApplication App, CancellationToken Lifetime );

/// <summary>Describes an exact global physical key and modifier combination.</summary>
/// <param name="Key">The physical key.</param>
/// <param name="Modifiers">The required modifiers.</param>
public readonly record struct CommandShortcut( Key Key, KeyModifiers Modifiers );

/// <summary>Provides a panel with guarded UI dispatch and its owned lifetime.</summary>
public sealed class PanelContext
{
	/// <summary>Gets the application services on the UI thread.</summary>
	public LoknApplication App
	{
		get;
	}

	/// <summary>Gets the stable panel instance identifier.</summary>
	public Guid Id
	{
		get;
	}

	/// <summary>Gets the feature-defined input supplied when opening this panel.</summary>
	public object? Input
	{
		get;
	}

	/// <summary>Gets feature-owned JSON restored from the previous workspace session.</summary>
	public JsonElement? RestoredState
	{
		get;
	}

	/// <summary>Gets the token cancelled when this panel closes or its module unloads.</summary>
	public CancellationToken Lifetime
	{
		get;
	}

	/// <summary>Creates the context for one owned panel instance.</summary>
	internal PanelContext( LoknApplication app, Guid id, CancellationToken lifetime, object? input, JsonElement? restoredState = null )
	{
		App = app;
		Id = id;
		Lifetime = lifetime;
		Input = input;
		RestoredState = restoredState;
	}

	/// <summary>Runs a panel callback on the UI thread and contains feature exceptions.</summary>
	public void Run( Action action )
	{
		if ( Lifetime.IsCancellationRequested )
			return;

		try
		{
			action();
		}
		catch ( Exception error ) { App.ReportError( "Panel action", error ); }
	}

	/// <summary>Posts guarded work from a background task to the owning UI thread.</summary>
	public bool Post( Action action )
	{
		if ( Lifetime.IsCancellationRequested )
			return false;

		return App.Post( () => Run( action ) );
	}

	/// <summary>Creates a context for content this content shows inside itself, such as a popover, that lives until the returned source is cancelled or this context ends.</summary>
	/// <returns>The child context and the source the caller cancels and disposes when the child closes.</returns>
	public (PanelContext Context, CancellationTokenSource Lifetime) CreateChild()
	{
		var lifetime = CancellationTokenSource.CreateLinkedTokenSource( Lifetime );
		return (new PanelContext( App, Guid.NewGuid(), lifetime.Token, input: null ), lifetime);
	}

	/// <summary>Highlights this panel and tells the user it is waiting for them, until they activate it or the panel clears the request.</summary>
	/// <param name="title">What is waiting, such as a document or conversation title.</param>
	/// <param name="message">What the panel needs from the user.</param>
	public bool RequestAttention( string title, string message ) => App.Panels.RequestAttention( Id, new( title, message ) );

	/// <summary>Withdraws this panel's request for the user once it no longer needs them.</summary>
	public bool ClearAttention() => App.Panels.ClearAttention( Id );
}

/// <summary>Owns panel state independently of its position in the workspace.</summary>
public abstract class PanelContent
{
	/// <summary>Builds a fresh Goo declaration while retaining state on this instance.</summary>
	public abstract Blob Build();

	/// <summary>Captures feature-owned JSON used to reopen this panel after restart.</summary>
	public virtual JsonElement? CaptureState() => null;

	/// <summary>Gets the settings profile this panel works in, such as a chat's account, so what it opens uses the same one; null follows the active profile.</summary>
	public virtual SettingsProfile? Profile => null;

	/// <summary>Reports whether this panel handles a key itself instead of the global shortcut or palette key bound to it.</summary>
	/// <remarks>Queried on the UI thread for every key press, before focused controls receive it. Claim only while the panel's own input element has focus; Ctrl+Shift+P always opens the palette.</remarks>
	public virtual bool ClaimsKey( Key key, KeyModifiers modifiers ) => false;

	/// <summary>Releases feature-owned resources after the panel lifetime is cancelled.</summary>
	public virtual void Closed()
	{
	}
}

/// <summary>Describes a registered panel type offered by the command palette.</summary>
/// <param name="Id">The fully qualified panel type identifier.</param>
/// <param name="Title">The default pane title.</param>
/// <param name="ModuleId">The owning module identifier.</param>
public sealed record PanelTypeInfo( string Id, string Title, string ModuleId )
{
	/// <summary>Gets the type's tiny icon, a Segoe Fluent Icons code point, such as on the tasks that hold it; null has none.</summary>
	public string? Glyph
	{
		get; init;
	}
}

/// <summary>Describes an available global command.</summary>
/// <param name="Id">The fully qualified command identifier.</param>
/// <param name="Title">The palette label.</param>
/// <param name="ModuleId">The owning module identifier.</param>
/// <param name="Shortcut">The optional exact keyboard shortcut.</param>
public sealed record CommandInfo( string Id, string Title, string ModuleId, CommandShortcut? Shortcut );

/// <summary>Describes a loaded module and its required modules.</summary>
/// <param name="Id">The stable module identifier.</param>
/// <param name="Title">The human-readable module name.</param>
/// <param name="Dependencies">The required loaded module identifiers.</param>
/// <param name="CanUnload">Whether the module can be disabled during application execution.</param>
public sealed record ModuleInfo( string Id, string Title, IReadOnlyList<string> Dependencies, bool CanUnload = true );
