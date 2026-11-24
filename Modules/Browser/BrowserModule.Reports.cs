using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Browser;

/// <summary>Lists the html reports agents showed, with the chat that showed each, so other features can find a chat's or a project's reports again.</summary>
/// <remarks>The list is kept in <c>reports\reports.json</c> beside the report files. Reports saved before the list existed are not in it.</remarks>
public sealed partial class BrowserModule
{
	/// <summary>Defines how many reports the list keeps; older entries are dropped from the list, not deleted from disk.</summary>
	private const int MaxReports = 500;

	/// <summary>Serializes the list readably, with camel-case names.</summary>
	private static readonly JsonSerializerOptions ReportsJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	/// <summary>Stores the listed reports, oldest first, once read.</summary>
	private List<BrowserReport>? _reports;

	/// <summary>Counts changes to the list, so readers rebuild when a report is added.</summary>
	private readonly State<int> _reportsVersion = new( 0 );

	/// <summary>Gets the file the list is kept in.</summary>
	private string ReportsIndexPath => Path.Combine( ReportsPath, "reports.json" );

	/// <summary>Gets the listed reports whose file still exists, newest first; reading it during a build rebuilds the reader when a report is added.</summary>
	public IReadOnlyList<BrowserReport> Reports
	{
		get
		{
			_ = _reportsVersion.Value;
			return [.. ReadReports().Where( report => File.Exists( report.Path ) ).Reverse()];
		}
	}

	/// <summary>Opens a listed report in Loom's browser on Windows or in the system browser elsewhere.</summary>
	/// <param name="app">The application.</param>
	/// <param name="report">The report.</param>
	/// <param name="sourcePanelId">The panel to show it beside, or null to show it in view.</param>
	public void ShowReport( LoknApplication app, BrowserReport report, Guid? sourcePanelId = null )
	{
		if ( OperatingSystem.IsWindows() )
			ShowBeside( app, app.Hosts.Local, new Uri( report.Path ).AbsoluteUri, sourcePanelId, replace: null );
		else
			Process.Start( new ProcessStartInfo( new Uri( report.Path ).AbsoluteUri ) { UseShellExecute = true } )?.Dispose();
	}

	/// <summary>Adds a report an agent's tool call saved to the list, with the chat that made the call.</summary>
	internal void RecordReport( string path, string? title, AgentToolContext context )
	{
		var reports = ReadReports();
		reports.Add( new BrowserReport( path, title ?? Path.GetFileNameWithoutExtension( path ), DateTimeOffset.UtcNow, context.Conversation, context.Host ?? LoknHost.LocalId ) );
		if ( reports.Count > MaxReports )
			reports.RemoveRange( 0, reports.Count - MaxReports );
		try
		{
			Directory.CreateDirectory( ReportsPath );
			var temporary = ReportsIndexPath + ".tmp";
			File.WriteAllText( temporary, JsonSerializer.Serialize( reports, ReportsJson ) );
			File.Move( temporary, ReportsIndexPath, overwrite: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			context.App.ReportError( Title, new IOException( $"Could not save the list of reports: {error.Message}", error ) );
		}
		_reportsVersion.Value++;
	}

	/// <summary>Reads the list once; an empty list when the file is missing or does not parse.</summary>
	private List<BrowserReport> ReadReports()
	{
		if ( _reports is not null )
			return _reports;
		try
		{
			_reports = File.Exists( ReportsIndexPath ) ? JsonSerializer.Deserialize<List<BrowserReport>>( File.ReadAllText( ReportsIndexPath ), ReportsJson ) ?? [] : [];
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			_reports = [];
		}
		return _reports;
	}
}

/// <summary>Describes an html report an agent showed with <c>browser_show</c>.</summary>
/// <param name="Path">The saved report file.</param>
/// <param name="Title">The title the agent gave it, else its file name.</param>
/// <param name="Created">When it was saved.</param>
/// <param name="Conversation">The chat whose agent showed it, or null when no chat's agent did.</param>
/// <param name="Host">The id of the host that chat is on.</param>
public sealed record BrowserReport( string Path, string Title, DateTimeOffset Created, Guid? Conversation, string Host );
