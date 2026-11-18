namespace Lokn.Core;

/// <summary>Collects folders of source modules that loaded modules contribute, such as each trusted project's <c>.lokn\modules</c>.</summary>
/// <remarks>
/// Each folder holds one module per subfolder. The source module compiler, when it runs, loads every module these folders
/// hold and follows their changes; a folder that leaves the registry has its modules removed. Use it on the UI thread.
/// </remarks>
public sealed class ModuleFolderRegistry
{
	/// <summary>Stores each owner's folders.</summary>
	private readonly Dictionary<string, string[]> _owners = new( StringComparer.Ordinal );

	/// <summary>Gets every contributed folder as a full path, once each, in contribution order.</summary>
	public IReadOnlyList<string> Folders => [.. _owners.Values.SelectMany( folders => folders ).Distinct( StringComparer.OrdinalIgnoreCase )];

	/// <summary>Raised on the UI thread after the folders change.</summary>
	public event Action? Changed;

	/// <summary>Replaces the folders one owner contributes; an empty list withdraws them.</summary>
	/// <param name="owner">The contributing module's identifier.</param>
	/// <param name="folders">The folders, each holding one module per subfolder.</param>
	public void Set( string owner, IEnumerable<string> folders )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( owner );
		string[] full = [.. folders.Select( folder => Path.TrimEndingDirectorySeparator( Path.GetFullPath( folder ) ) ).Distinct( StringComparer.OrdinalIgnoreCase )];
		var previous = _owners.GetValueOrDefault( owner ) ?? [];
		if ( previous.SequenceEqual( full, StringComparer.OrdinalIgnoreCase ) )
			return;

		if ( full.Length == 0 )
			_owners.Remove( owner );
		else
			_owners[owner] = full;
		Changed?.Invoke();
	}
}
