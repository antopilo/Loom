using Goo;

namespace Lokn.Core;

/// <summary>Sets how a popup window sits in the shell.</summary>
public sealed record PopupOptions
{
	/// <summary>Gets the window's width in logical pixels; it never grows past 95% of the shell.</summary>
	public double Width { get; init; } = 960;

	/// <summary>Gets the window's height in logical pixels; it never grows past 90% of the shell.</summary>
	public double Height { get; init; } = 680;
}

/// <summary>One open popup window: a panel shown over the shell in a <see cref="ModalWindow"/> instead of in a pane.</summary>
public sealed class ShellPopup
{
	/// <summary>Creates a popup window around a detached panel.</summary>
	internal ShellPopup( PanelSession session, PopupOptions options )
	{
		Session = session;
		Options = options;
	}

	/// <summary>Gets the detached panel this window shows.</summary>
	internal PanelSession Session
	{
		get;
	}

	/// <summary>Gets the panel type, such as <c>shell.settings</c>.</summary>
	public string TypeId => Session.Info.TypeId;

	/// <summary>Gets the window's title, the panel type's title.</summary>
	public string Title => Session.Info.Title;

	/// <summary>Gets the panel shown in the window, or null when its factory failed.</summary>
	public PanelContent? Content => Session.Content;

	/// <summary>Gets why the panel could not be created, or null.</summary>
	public string? Failure => Session.Failure;

	/// <summary>Gets how the window sits in the shell.</summary>
	public PopupOptions Options
	{
		get;
	}
}

/// <summary>Opens registered panel types as popup windows over the shell, such as Settings; the shell draws the newest on top.</summary>
/// <remarks>
/// A popup holds a panel created by its type's factory, as a pane would, but outside the dock tree: it is not saved with the layout and
/// closes when its module unloads. Opening a type that is already open brings that window to the front instead of opening a second one.
/// </remarks>
public sealed class PopupWindows
{
	/// <summary>Stores the owning application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the open windows, oldest first.</summary>
	private readonly List<ShellPopup> _windows = [];

	/// <summary>Creates the popup list for an application.</summary>
	internal PopupWindows( LoknApplication app ) => _app = app;

	/// <summary>Gets the open windows, oldest first; the last one is on top.</summary>
	public IReadOnlyList<ShellPopup> Windows => _windows;

	/// <summary>Gets the window on top, or null when none is open.</summary>
	public ShellPopup? Top => _windows.Count > 0 ? _windows[^1] : null;

	/// <summary>Opens a panel type in a popup window, or brings its open window to the front; on the UI thread.</summary>
	/// <param name="typeId">The registered panel type.</param>
	/// <param name="input">The new panel's input; ignored when the type is already open.</param>
	/// <param name="options">How the window sits, or null for the default size.</param>
	/// <returns>The window showing the type.</returns>
	public ShellPopup Open( string typeId, object? input = null, PopupOptions? options = null )
	{
		ArgumentNullException.ThrowIfNull( typeId );
		if ( _windows.FirstOrDefault( window => window.TypeId == typeId ) is { } open )
		{
			_windows.Remove( open );
			_windows.Add( open );
			_app.MarkChanged();
			return open;
		}

		var created = new ShellPopup( _app.Panels.CreateDetached( typeId, input ), options ?? new() );
		_windows.Add( created );
		// The panel's lifetime ends with its module; the window closes with it rather than showing a stale panel.
		created.Session.Lifetime.Token.Register( () => _app.Post( () => Close( created ) ) );
		_app.MarkChanged();
		return created;
	}

	/// <summary>Finds the panel of an open popup by its type.</summary>
	/// <typeparam name="T">The panel class, such as the shell's settings.</typeparam>
	public T? FindContent<T>() where T : PanelContent => _windows.Select( window => window.Content ).OfType<T>().FirstOrDefault();

	/// <summary>Closes a popup window and releases its panel; closing one that is no longer open does nothing.</summary>
	public void Close( ShellPopup window )
	{
		if ( !_windows.Remove( window ) )
			return;

		_app.Panels.ReleaseDetached( window.Session );
		_app.MarkChanged();
	}

	/// <summary>Closes every popup window, such as at shutdown.</summary>
	internal void CloseAll()
	{
		foreach ( var window in _windows.ToArray() )
			Close( window );
	}

	/// <summary>Reports whether the top window's panel handles a key itself instead of a global shortcut.</summary>
	internal bool ClaimsKey( Key key, KeyModifiers modifiers )
	{
		try
		{
			return Top?.Content?.ClaimsKey( key, modifiers ) == true;
		}
		catch ( Exception )
		{
			// Queried for every key press; a failing panel must not block the shell's keys.
			return false;
		}
	}
}
