namespace Lokn.Modules.Git;

/// <summary>One entry of a folder tree built from paths: a folder, or an item in one.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Label">The folder's name, with folders that hold only one folder joined by <c>/</c>, or the item's file name.</param>
/// <param name="Path">The folder's path in the tree, such as <c>/src/Core</c>, which keys its folded state; the item's own path for an item.</param>
/// <param name="Depth">The nesting level.</param>
/// <param name="Item">The item, or null for a folder.</param>
/// <param name="Count">How many items the folder holds at any depth; one for an item.</param>
/// <param name="IsOpen">Whether a folder shows its contents.</param>
internal readonly record struct PathTreeEntry<T>( string Label, string Path, int Depth, T? Item, int Count, bool IsOpen ) where T : class;

/// <summary>Lays out items with relative paths as a folder tree, as the diff panel and the side view's changes show them.</summary>
internal static class PathTree
{
	/// <summary>Lists folders first, by name, then items in the order given, joining folders that hold only one folder and leaving out folded folders' contents.</summary>
	/// <param name="items">The items.</param>
	/// <param name="pathOf">Gives an item's relative path, separated by <c>/</c> or <c>\</c>.</param>
	/// <param name="isFolded">Reports whether the folder at a tree path is folded.</param>
	internal static List<PathTreeEntry<T>> Build<T>( IEnumerable<T> items, Func<T, string> pathOf, Func<string, bool> isFolded ) where T : class
	{
		var root = new Folder<T>();
		foreach ( var item in items )
		{
			var folder = root;
			var parts = pathOf( item ).Split( ['/', '\\'], StringSplitOptions.RemoveEmptyEntries );
			folder.Count++;
			foreach ( var part in parts.Length > 0 ? parts[..^1] : parts )
			{
				if ( !folder.Folders.TryGetValue( part, out var child ) )
					folder.Folders[part] = child = new Folder<T>();
				folder = child;
				folder.Count++;
			}
			folder.Items.Add( (parts.Length > 0 ? parts[^1] : pathOf( item ), item) );
		}

		var entries = new List<PathTreeEntry<T>>();
		Add( entries, root, "", 0, pathOf, isFolded );
		return entries;
	}

	/// <summary>Adds one folder's subfolders, then its items, at one nesting level.</summary>
	private static void Add<T>( List<PathTreeEntry<T>> entries, Folder<T> folder, string path, int depth, Func<T, string> pathOf, Func<string, bool> isFolded ) where T : class
	{
		foreach ( var (name, child) in folder.Folders )
		{
			var label = name;
			var node = child;
			while ( node.Items.Count == 0 && node.Folders.Count == 1 )
			{
				var (next, only) = node.Folders.First();
				label += "/" + next;
				node = only;
			}
			var folderPath = path + "/" + label;
			var isOpen = !isFolded( folderPath );
			entries.Add( new( label, folderPath, depth, null, node.Count, isOpen ) );
			if ( isOpen )
				Add( entries, node, folderPath, depth + 1, pathOf, isFolded );
		}
		foreach ( var (name, item) in folder.Items )
			entries.Add( new( name, pathOf( item ), depth, item, 1, true ) );
	}

	/// <summary>Groups items by folder while building the tree.</summary>
	private sealed class Folder<T>
	{
		/// <summary>Gets the subfolders by name, in name order.</summary>
		public SortedDictionary<string, Folder<T>> Folders { get; } = new( StringComparer.OrdinalIgnoreCase );

		/// <summary>Gets the items directly in this folder with their file names, in the order given.</summary>
		public List<(string Name, T Item)> Items { get; } = [];

		/// <summary>Gets or sets how many items the folder holds at any depth.</summary>
		public int Count
		{
			get; set;
		}
	}
}
