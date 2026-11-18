namespace Lokn.Core;

/// <summary>Describes a link the user followed, for the module link handlers that may open it.</summary>
/// <param name="App">The application on its UI thread.</param>
/// <param name="Uri">The absolute link.</param>
/// <param name="SourcePanelId">The panel the link was followed in, or null.</param>
public sealed record LinkRequest( LoknApplication App, Uri Uri, Guid? SourcePanelId );
