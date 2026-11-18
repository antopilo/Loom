using Lokn.Core;

namespace Lokn.Modules.Setup;

/// <summary>Shows Get started, which checks what Loom needs from the machine, such as Git and a signed-in Omp, and installs each in one click.</summary>
/// <remarks>
/// It depends on no other module and knows none of the items: modules contribute a <see cref="SetupItem"/> to <see cref="SetupItem.Point"/>.
/// At launch it checks the required items off the UI thread and opens Get started while one is not ready. <c>lokn://setup/open</c> opens it.
/// </remarks>
public sealed class SetupModule : ILoknModule
{
	/// <summary>Defines the Get started panel type.</summary>
	internal const string PanelType = "setup.get-started";

	/// <summary>Gets the module identifier.</summary>
	public string Id => "setup";

	/// <summary>Gets the display name.</summary>
	public string Title => "Setup";

	/// <summary>Gets no dependencies: other modules contribute their items instead.</summary>
	public IReadOnlyList<string> Dependencies => [];

	/// <summary>Declares the items point and registers the Get started panel, its open command and its route.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.ExtensionPoint<SetupItem>( "items" )
			.Panel( "get-started", "Get started", context => new GetStartedContent( context ) )
			.Command( "open", "Get started", context => Open( context.App ) )
			.Route( "open", request => Open( request.App ) );
	}

	/// <summary>Opens Get started once the application has started when a required item is not ready; a reload never opens it.</summary>
	public void Start( ModuleContext context ) => context.WhenStarted( () => _ = OpenWhenNeededAsync( context ) );

	/// <summary>Gets the contributed items in the order the page lists them.</summary>
	internal static IReadOnlyList<SetupItem> GetItems( LoknApplication app ) =>
		app.Extensions.Get<SetupItem>( SetupItem.Point ).OrderBy( item => item.Order ).ToArray();

	/// <summary>Runs an item's check off the UI thread; a check that throws reports the item missing with the error's message.</summary>
	/// <exception cref="OperationCanceledException">The check was cancelled.</exception>
	internal static async Task<SetupStatus> CheckAsync( SetupItem item, CancellationToken cancellation )
	{
		try
		{
			return await Task.Run( () => item.CheckAsync( cancellation ), cancellation );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			return new( SetupState.Missing, error.Message );
		}
	}

	/// <summary>Shows Get started: its open panel, shown or hidden, else a new one beside the active pane.</summary>
	/// <param name="app">The application.</param>
	/// <param name="statuses">Statuses already checked, which a new page shows instead of checking those items again; null checks every item.</param>
	internal static void Open( LoknApplication app, IReadOnlyDictionary<SetupItem, SetupStatus>? statuses = null )
	{
		if ( app.Panels.FindContent<GetStartedContent>() is (var id, _) )
			app.Panels.Activate( id );
		else
			app.Panels.Open( PanelType, input: statuses );
	}

	/// <summary>Checks the required items and opens Get started when any is not ready.</summary>
	private static async Task OpenWhenNeededAsync( ModuleContext context )
	{
		var required = GetItems( context.App ).Where( item => item.IsRequired ).ToArray();
		if ( required.Length == 0 )
			return;

		try
		{
			var statuses = await Task.WhenAll( required.Select( item => CheckAsync( item, context.Lifetime ) ) );
			if ( statuses.Any( status => status.State != SetupState.Ready ) )
				context.Post( () => Open( context.App, required.Zip( statuses ).ToDictionary( pair => pair.First, pair => pair.Second ) ) );
		}
		catch ( OperationCanceledException )
		{
			// The module unloaded during the checks.
		}
	}
}
