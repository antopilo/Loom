using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokn.Core;

namespace Lokn.Modules.GitHub;

/// <summary>Reports that GitHub has no usable credential, so the user has to sign in.</summary>
internal sealed class GitHubSignInException( string message ) : Exception( message );

/// <summary>Reads the user's pull requests and workflow runs from GitHub with the credential Git already has.</summary>
/// <remarks>
/// The token comes from <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>, or Git's credential helper (Git Credential Manager on Windows),
/// so Lokn never stores one. One instance belongs to one panel and runs one request at a time.
/// </remarks>
internal sealed partial class GitHubClient : IDisposable
{
	/// <summary>Caches git's resolved path; null until git is found, so a git installed after Lokn started is found by the next command.</summary>
	private static string? _git;

	/// <summary>Gets the git executable: Git for Windows' machine or per-user install, else git on PATH, else the bare name.</summary>
	private static string Git => (_git ??= ToolLocator.Find( "git",
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "Git", "cmd", "git.exe" ),
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Programs", "Git", "cmd", "git.exe" ) )) ?? "git";

	/// <summary>Defines the API root.</summary>
	private const string ApiRoot = "https://api.github.com/";

	/// <summary>Defines how many repositories' workflow runs one refresh reads.</summary>
	private const int MaxRunRepositories = 8;

	/// <summary>Defines how many runs one repository contributes.</summary>
	private const int RunsPerRepository = 10;

	/// <summary>Defines how many runs the dashboard keeps across repositories.</summary>
	private const int MaxRuns = 20;

	/// <summary>Defines the GraphQL query: the viewer, their open and recently closed pull requests, and requests for their review.</summary>
	private const string Query = """
		query {
			viewer { login }
			mine: search(query: "is:pr is:open author:@me archived:false sort:updated-desc", type: ISSUE, first: 30) { nodes { ...pr } }
			reviews: search(query: "is:pr is:open review-requested:@me archived:false sort:updated-desc", type: ISSUE, first: 30) { nodes { ...pr } }
			closed: search(query: "is:pr is:closed author:@me archived:false sort:updated-desc", type: ISSUE, first: 10) { nodes { ...pr } }
		}
		fragment pr on PullRequest {
			number title url isDraft state reviewDecision updatedAt headRefName
			author { login }
			repository { nameWithOwner }
			comments { totalCount }
			reviews { totalCount }
			commits(last: 1) { nodes { commit { statusCheckRollup { state contexts(first: 50) { nodes {
				__typename
				... on CheckRun { name status conclusion detailsUrl }
				... on StatusContext { context state targetUrl }
			} } } } } }
		}
		""";

	/// <summary>Sends the API requests.</summary>
	private readonly HttpClient _http = new()
	{
		BaseAddress = new( ApiRoot ),
		Timeout = TimeSpan.FromSeconds( 30 ),
	};

	/// <summary>Caches the resolved token until GitHub rejects it.</summary>
	private string? _token;

	/// <summary>Sets the headers every GitHub request needs.</summary>
	internal GitHubClient()
	{
		_http.DefaultRequestHeaders.UserAgent.Add( new( "Lokn", "1.0" ) );
		_http.DefaultRequestHeaders.Accept.Add( new( "application/vnd.github+json" ) );
		_http.DefaultRequestHeaders.Add( "X-GitHub-Api-Version", "2022-11-28" );
	}

	/// <summary>Reads the dashboard: pull requests through one GraphQL query, then workflow runs of the watched repositories and those of the user's open pull requests.</summary>
	internal async Task<GitHubSnapshot> LoadAsync( IReadOnlyList<string> watched, CancellationToken cancellationToken )
	{
		var token = _token ??= await ResolveTokenAsync( cancellationToken ).ConfigureAwait( false )
			?? throw new GitHubSignInException( "Sign in to GitHub to see your pull requests and workflow runs." );

		using var document = await SendAsync( HttpMethod.Post, "graphql", token, JsonSerializer.Serialize( new { query = Query } ), cancellationToken ).ConfigureAwait( false );
		var root = document.RootElement;
		if ( root.TryGetProperty( "errors", out var errors ) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 )
			throw new InvalidOperationException( "GitHub: " + (errors[0].TryGetProperty( "message", out var message ) ? message.GetString() : "the query failed.") );

		var data = root.GetProperty( "data" );
		var login = data.GetProperty( "viewer" ).GetProperty( "login" ).GetString() ?? "";
		var mine = GitHubModel.ReadPullRequests( data.GetProperty( "mine" ) );
		var reviews = GitHubModel.ReadPullRequests( data.GetProperty( "reviews" ) );
		var closed = GitHubModel.ReadPullRequests( data.GetProperty( "closed" ) );

		// Watched repositories show everyone's runs; the repositories of the user's pull requests show only theirs.
		var sources = watched.Select( repository => (Repository: repository, Actor: "") )
			.Concat( mine.Select( pull => (pull.Repository, Actor: login) ) )
			.DistinctBy( source => source.Repository, StringComparer.OrdinalIgnoreCase )
			.Take( MaxRunRepositories )
			.ToArray();
		var runs = await Task.WhenAll( sources.Select( source => LoadRunsAsync( source.Repository, source.Actor, token, cancellationToken ) ) ).ConfigureAwait( false );
		return new( login, mine, reviews, closed, [.. runs.SelectMany( list => list ).OrderByDescending( run => run.UpdatedAt ).Take( MaxRuns )] );
	}

	/// <summary>Opens Git Credential Manager's sign-in for github.com and stores the credential it returns.</summary>
	internal async Task SignInAsync( CancellationToken cancellationToken )
	{
		var credential = await RunGitAsync( "fill", "protocol=https\nhost=github.com\n\n", interactive: true, cancellationToken ).ConfigureAwait( false );
		if ( ReadCredentialPassword( credential ) is null )
			throw new InvalidOperationException( "Git returned no GitHub credential. Install Git Credential Manager, or set GH_TOKEN, then try again." );

		await RunGitAsync( "approve", credential, interactive: false, cancellationToken ).ConfigureAwait( false );
		_token = null;
	}

	/// <summary>Releases the HTTP connection pool.</summary>
	public void Dispose() => _http.Dispose();

	/// <summary>Reads the extra watched repositories from the setting, or null when an entry is not owner/name.</summary>
	internal static string[]? ParseRepositories( string value )
	{
		var entries = value.Split( [',', ' ', '\t', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries );
		return entries.All( entry => RepositoryPattern().IsMatch( entry ) ) ? entries : null;
	}

	/// <summary>Reads the password field of git credential output.</summary>
	internal static string? ReadCredentialPassword( string credential ) => credential
		.Split( '\n' )
		.Select( line => line.TrimEnd( '\r' ) )
		.FirstOrDefault( line => line.StartsWith( "password=", StringComparison.Ordinal ) )?["password=".Length..] is { Length: > 0 } password ? password : null;

	/// <summary>Reads one repository's latest workflow runs, optionally only those one user started.</summary>
	private async Task<List<WorkflowRunInfo>> LoadRunsAsync( string repository, string actor, string token, CancellationToken cancellationToken )
	{
		var query = $"repos/{repository}/actions/runs?per_page={RunsPerRepository}" + (actor.Length > 0 ? "&actor=" + Uri.EscapeDataString( actor ) : "");
		try
		{
			using var document = await SendAsync( HttpMethod.Get, query, token, null, cancellationToken ).ConfigureAwait( false );
			return GitHubModel.ReadRuns( document.RootElement );
		}
		catch ( HttpRequestException error ) when ( error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden )
		{
			// A repository without Actions, or one the token may not read, simply has no runs to show.
			return [];
		}
	}

	/// <summary>Sends one request and parses its JSON, forgetting the token when GitHub rejects it.</summary>
	private async Task<JsonDocument> SendAsync( HttpMethod method, string path, string token, string? body, CancellationToken cancellationToken )
	{
		using var request = new HttpRequestMessage( method, path );
		request.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", token );
		if ( body is not null )
			request.Content = new StringContent( body, Encoding.UTF8, "application/json" );

		using var response = await _http.SendAsync( request, cancellationToken ).ConfigureAwait( false );
		if ( response.StatusCode == HttpStatusCode.Unauthorized )
		{
			_token = null;
			throw new GitHubSignInException( "GitHub rejected the saved credential. Sign in again." );
		}
		if ( !response.IsSuccessStatusCode )
			throw new HttpRequestException( $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {path.Split( '?' )[0]}.", null, response.StatusCode );

		await using var stream = await response.Content.ReadAsStreamAsync( cancellationToken ).ConfigureAwait( false );
		return await JsonDocument.ParseAsync( stream, cancellationToken: cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Finds a token in the environment, else asks Git's credential helper without letting it prompt.</summary>
	private static async Task<string?> ResolveTokenAsync( CancellationToken cancellationToken )
	{
		foreach ( var name in (string[])["GH_TOKEN", "GITHUB_TOKEN"] )
		{
			if ( Environment.GetEnvironmentVariable( name ) is { Length: > 0 } token )
				return token;
		}

		try
		{
			return ReadCredentialPassword( await RunGitAsync( "fill", "protocol=https\nhost=github.com\n\n", interactive: false, cancellationToken ).ConfigureAwait( false ) );
		}
		catch ( Exception error ) when ( error is InvalidOperationException or System.ComponentModel.Win32Exception )
		{
			// No stored credential, or no git at all: the panel offers sign-in.
			return null;
		}
	}

	/// <summary>Runs <c>git credential</c> with input, returning its output; non-interactive runs never show a prompt or window.</summary>
	private static async Task<string> RunGitAsync( string action, string input, bool interactive, CancellationToken cancellationToken )
	{
		var start = new ProcessStartInfo( Git )
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		start.ArgumentList.Add( "credential" );
		start.ArgumentList.Add( action );
		// Without a terminal, git's own username prompt could only fail; Git Credential Manager shows its window when interactive.
		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		start.Environment["GCM_INTERACTIVE"] = interactive ? "auto" : "never";

		using var process = new Process { StartInfo = start };
		process.Start();
		var output = process.StandardOutput.ReadToEndAsync( cancellationToken );
		var error = process.StandardError.ReadToEndAsync( cancellationToken );
		try
		{
			await process.StandardInput.WriteAsync( input.AsMemory(), cancellationToken ).ConfigureAwait( false );
			process.StandardInput.Close();
			await process.WaitForExitAsync( cancellationToken ).ConfigureAwait( false );
			var text = await output.ConfigureAwait( false );
			var diagnostic = (await error.ConfigureAwait( false )).Trim();
			if ( process.ExitCode != 0 )
				throw new InvalidOperationException( diagnostic.Length > 0 ? diagnostic : $"git credential {action} exited with code {process.ExitCode}." );

			return text;
		}
		catch ( OperationCanceledException )
		{
			// A closed panel must not leave a sign-in window or helper running.
			if ( !process.HasExited )
				process.Kill( entireProcessTree: true );

			throw;
		}
	}

	/// <summary>Matches one owner/name repository.</summary>
	[GeneratedRegex( @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$" )]
	private static partial Regex RepositoryPattern();
}
