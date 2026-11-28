namespace Lokn.Core;

/// <summary>Items another module adds to a project's brief, the Overview module's page of a project, such as its pull requests or its tickets;
/// contribute it to <see cref="Point"/>.</summary>
/// <remarks>
/// <para>The page calls <see cref="Read"/> on the UI thread every time it builds, so it must return at once with what the module already holds; fetch
/// anything else in the background and keep it in a Goo <c>State</c> that <see cref="Read"/> reads, so the page builds again when it changes.</para>
/// <para>The brief mixes the items of every source: items that need the user go under Top to-dos, the others under New
/// updates by their <see cref="BriefItem.Time"/>, each with the section's title in its source line. Items also reach the small model that writes the
/// line under the painting. A section's <see cref="BriefContent.Empty"/> note and its links show under New updates; an exception from
/// <see cref="Read"/> shows there as a note.</para>
/// </remarks>
/// <param name="Id">Identifies the section among all sections, such as <c>github.pulls</c>.</param>
/// <param name="Title">Names the items' source, such as <c>Pull requests</c>.</param>
/// <param name="Order">The section's place among sections, lowest first, for items with the same time and for notes and links.</param>
/// <param name="Read">Reads the section's items for a project, or returns null to add nothing, such as for a project with no GitHub remote.</param>
public sealed record BriefSection( string Id, string Title, int Order, Func<BriefProject, BriefContent?> Read )
{
	/// <summary>Defines the extension point the Overview module reads sections from.</summary>
	public const string Point = "overview.sections";
}

/// <summary>Describes the project a <see cref="BriefSection"/> is read for.</summary>
/// <param name="App">The application.</param>
/// <param name="Id">The project's id.</param>
/// <param name="Name">The project's name.</param>
/// <param name="Directory">The project's folder, on its host.</param>
/// <param name="HostId">The host the project is on: <see cref="LoknHost.LocalId"/> for This PC.</param>
/// <param name="Profile">The settings profile the project belongs to.</param>
/// <param name="Branch">The folder's checked-out git branch; empty until known, or when the folder is no repository.</param>
/// <param name="Remote">The address of the repository's <c>origin</c> remote; empty until known, or when there is none.</param>
/// <param name="PanelId">The brief's panel, to open a panel beside it with <see cref="PanelWorkspace.OpenBeside"/> or a link from it with
/// <see cref="LoknApplication.OpenLink"/>.</param>
public sealed record BriefProject( LoknApplication App, Guid Id, string Name, string Directory, string HostId, SettingsProfile Profile, string Branch,
	string Remote, Guid PanelId );

/// <summary>Holds what a <see cref="BriefSection"/> adds for one project.</summary>
/// <param name="Items">The items, in the order they show when they share a place.</param>
public sealed record BriefContent( IReadOnlyList<BriefItem> Items )
{
	/// <summary>Gets a note shown while <see cref="Items"/> is empty, such as "Sign in to GitHub"; empty adds none.</summary>
	public string Empty { get; init; } = "";

	/// <summary>Gets links shown under New updates, such as one that opens the module's own panel.</summary>
	public IReadOnlyList<BriefLink> Links { get; init; } = [];
}

/// <summary>One item of a brief section, such as a pull request or a ticket. Items that need the user go under the brief's Top to-dos with a check
/// circle; the others, when they have a <see cref="Time"/>, go under New updates by how new they are.</summary>
/// <param name="Key">Identifies the item within its section across reads, such as the item's address.</param>
/// <param name="Title">The item's headline, such as a pull request's number and title.</param>
/// <param name="Detail">The short state shown after the section's title in the item's source line, such as "Changes requested" or "In review".</param>
public sealed record BriefItem( string Key, string Title, string Detail )
{
	/// <summary>Gets one or two sentences under the headline that say what to do or what changed, such as which checks fail; empty for none.</summary>
	public string Description { get; init; } = "";

	/// <summary>Gets when the item last changed, which places it among New updates; an item without one shows only when it needs the user.</summary>
	public DateTimeOffset? Time { get; init; }

	/// <summary>Gets the item's icon, a Segoe Fluent Icons code point shown after its headline; empty shows a page.</summary>
	public string Glyph { get; init; } = "";

	/// <summary>Gets whether the item waits on the user, such as a review request or failing checks: it goes under Top to-dos.</summary>
	public bool NeedsYou { get; init; }

	/// <summary>Gets what clicking the item does, such as opening its page; null for an item that only informs.</summary>
	public Action? Open { get; init; }
}

/// <summary>A link a brief section shows under New updates.</summary>
/// <param name="Label">The link's text, such as "Open pull requests".</param>
/// <param name="Open">What clicking it does.</param>
public sealed record BriefLink( string Label, Action Open );
