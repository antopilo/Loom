namespace Lokn.Relay;

/// <summary>Starts the relay on the URLs and TLS endpoints of its configuration.</summary>
internal static class Program
{
	/// <summary>Runs the relay until the service manager or the console stops it.</summary>
	private static void Main( string[] args ) => RelayApp.Build( [], args ).Run();
}
