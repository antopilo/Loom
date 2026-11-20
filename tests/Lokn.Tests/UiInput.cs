using System.Diagnostics;
using Goo;

namespace Lokn.Tests;

/// <summary>Native pointer and keyboard input for the window checks that holds when their window lost the system focus.</summary>
/// <remarks>
/// Goo discards presses and keys once its window lost the system focus, such as to a window another program or another check opened on the same desktop,
/// until focus returns or one of its elements is focused (its focus manager re-allows native input on an element focus). Each helper therefore focuses its
/// target in code first, as a click on an inactive window would activate it, and reads a target's bounds only once they held still across two pumps, so
/// the press lands on it. Callers then wait on the state the input changes, never on a fixed number of frames.
/// </remarks>
internal static class UiInput
{
	/// <summary>Defines how long a target may take to appear, become enabled and stop moving.</summary>
	private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds( 20 );

	/// <summary>Clicks a node's center through native pointer input: waits until it is enabled and its bounds hold still after scrolling it into view,
	/// focuses it, and presses and releases the primary button.</summary>
	/// <param name="window">The window.</param>
	/// <param name="find">Finds the node in the current accessibility tree; called again after each pump, as the tree is rebuilt.</param>
	/// <param name="pump">Settles layout and accessibility delivery.</param>
	/// <param name="what">Names the node in failures.</param>
	internal static void Click( Window window, Func<AccessibilityNode?> find, Action pump, string what )
	{
		var (node, bounds) = Settle( window, find, pump, what );
		Focus( window, node );
		Press( window, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, PointerButton.Primary );
		pump();
	}

	/// <summary>Clicks a node found before (<see cref="Click(Window, Func{AccessibilityNode?}, Action, string)"/>), following it by its identifier through
	/// the window's <see cref="SemanticAdapter"/>, which holds the same element across rebuilds of the tree.</summary>
	/// <param name="window">The window, whose accessibility adapter is a <see cref="SemanticAdapter"/>.</param>
	/// <param name="node">The node.</param>
	/// <param name="pump">Settles layout and accessibility delivery.</param>
	internal static void Click( Window window, AccessibilityNode node, Action pump ) =>
		Click( window, () => FindById( window, node.Id ), pump, node.Name ?? node.Role.ToString() );

	/// <summary>Finds the node with an identifier in the window's current accessibility tree, or null.</summary>
	private static AccessibilityNode? FindById( Window window, AccessibilityId id ) =>
		window.AccessibilityAdapter is SemanticAdapter { Tree.Root: { } root } ? Flatten( root ).FirstOrDefault( node => node.Id == id ) : null;

	/// <summary>Enumerates a semantic subtree in source order.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Lets the window take native input again after it lost the system focus (<see cref="Activate(Window, IEnumerable{AccessibilityNode})"/>),
	/// reading the nodes from the window's <see cref="SemanticAdapter"/>.</summary>
	internal static void Activate( Window window )
	{
		if ( window.AccessibilityAdapter is SemanticAdapter { Tree.Root: { } root } )
			Activate( window, Flatten( root ) );
	}

	/// <summary>Replaces the text of an entry or editor through native keyboard input after clicking it: selects all and commits the text.</summary>
	/// <param name="window">The window.</param>
	/// <param name="find">Finds the entry in the current accessibility tree.</param>
	/// <param name="pump">Settles layout and accessibility delivery.</param>
	/// <param name="what">Names the entry in failures.</param>
	/// <param name="text">The new text.</param>
	internal static void Type( Window window, Func<AccessibilityNode?> find, Action pump, string what, string text )
	{
		Click( window, find, pump, what );
		// The click may have left another element focused, such as when it landed while the entry moved; the keys go to the entry.
		if ( find() is { Focused: false } entry )
			Focus( window, entry );
		window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
		window.PlatformInput.KeyRelease( Key.A );
		window.PlatformInput.CommitText( text );
		pump();
	}

	/// <summary>Presses and releases a key on a node, focusing it first so the window accepts the key even when it lost the system focus.</summary>
	/// <param name="window">The window.</param>
	/// <param name="node">The node that takes the key.</param>
	/// <param name="key">The key.</param>
	/// <param name="modifiers">The modifiers held.</param>
	internal static void PressKey( Window window, AccessibilityNode node, Key key, KeyModifiers modifiers )
	{
		Focus( window, node );
		window.PlatformInput.KeyPress( key, modifiers );
		window.PlatformInput.KeyRelease( key );
	}

	/// <summary>Focuses a node in code, which also lets the window take native input again after it lost the system focus.</summary>
	/// <returns>Whether the node took the focus; a node that takes none leaves the focus as it was.</returns>
	internal static bool Focus( Window window, AccessibilityNode node ) =>
		window.PerformAccessibilityAction( node.Id, new AccessibilityActionRequest( AccessibilityAction.Focus ) );

	/// <summary>Lets the window take native input again after it lost the system focus, by focusing a node in code, for input that targets no focusable
	/// node itself, such as a drag of a divider or a window shortcut: the node that has the focus keeps it, and otherwise the first enabled button takes it.</summary>
	/// <param name="window">The window.</param>
	/// <param name="nodes">The nodes of the current accessibility tree.</param>
	internal static void Activate( Window window, IEnumerable<AccessibilityNode> nodes )
	{
		if ( window.IsFocused )
			return;

		var candidates = nodes.ToList();
		if ( candidates.FirstOrDefault( node => node.Focused ) is { } focused && Focus( window, focused ) )
			return;

		foreach ( var node in candidates.Where( node => !node.Disabled && node.Role == AccessibilityRole.Button ) )
		{
			if ( Focus( window, node ) )
				return;
		}
	}

	/// <summary>Moves the pointer to a point in layout coordinates and presses and releases a button there.</summary>
	internal static void Press( Window window, double x, double y, PointerButton button )
	{
		var (pixelX, pixelY) = (x * window.UiScale, y * window.UiScale);
		window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, pixelX, pixelY, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerPress( 1, PointerDevice.Mouse, pixelX, pixelY, button, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerRelease( 1, PointerDevice.Mouse, pixelX, pixelY, button, KeyModifiers.None, 0.0f );
	}

	/// <summary>Waits until a node shows enabled, scrolls it into view, and waits until its visible bounds are the same across two pumps.</summary>
	/// <returns>The node and its settled bounds in layout coordinates.</returns>
	internal static (AccessibilityNode Node, ElementRect Bounds) Settle( Window window, Func<AccessibilityNode?> find, Action pump, string what )
	{
		var clock = Stopwatch.StartNew();
		AccessibilityNode? node;
		while ( (node = find()) is not { Disabled: false } )
		{
			CoreChecks.Check( clock.Elapsed < SettleTimeout, $"{what} shows and is enabled ({(node is null ? "not shown" : "disabled")})." );
			pump();
		}

		_ = window.PerformAccessibilityAction( node.Id, new AccessibilityActionRequest( AccessibilityAction.ScrollIntoView ) );
		ElementRect? previous = null;
		while ( true )
		{
			pump();
			node = find();
			CoreChecks.Check( node is not null, $"{what} still shows while it settles." );
			var bounds = node!.VisibleBounds ?? node.Bounds;
			if ( bounds.Width > 0 && bounds.Height > 0 && previous == bounds )
				return (node, bounds);

			CoreChecks.Check( clock.Elapsed < SettleTimeout, $"{what} is visible and holds still ({bounds})." );
			previous = bounds;
		}
	}
}
