using Goo;

namespace Lokn.Core;

/// <summary>Supplies a <see cref="NativePopup"/> with its anchor, content and the way to close it.</summary>
/// <param name="Anchor">The element the popup opens beside.</param>
/// <param name="Content">The popup's content, with its own background, border and padding; rebuilt by the owner like any child.</param>
/// <param name="Dismiss">Closes the popup on Escape, a press outside or lost focus; null for a tooltip that only its owner closes.</param>
public sealed record NativePopupInput( ElementHandle Anchor, Blob Content, Action? Dismiss )
{
	/// <summary>Gets a rectangle in the anchor's local coordinates to open beside, such as the pointer of a right-click; null uses the whole anchor.</summary>
	public ElementRect? AnchorRect { get; init; }

	/// <summary>Gets the preferred side; the popup flips to stay on screen.</summary>
	public PortalPlacement Placement { get; init; } = PortalPlacement.BottomStart;

	/// <summary>Gets whether the popup takes input; a tooltip lets every press and key through.</summary>
	public PopupKind Kind { get; init; } = PopupKind.Menu;

	/// <summary>Gets whether a menu blocks input outside it and closes on Escape or a press outside; false lets the pointer and presses
	/// reach the window under it, for a menu that opens on hover and that its owner closes.</summary>
	public bool IsModal { get; init; } = true;

	/// <summary>Gets the content size in logical pixels, or null to measure the content.</summary>
	public LayoutSize? Size { get; init; }

	/// <summary>Gets the largest size the content is measured to, or null for the screen's work area.</summary>
	public LayoutSize? Maximum { get; init; }

	/// <summary>Gets the element that takes the keyboard when the popup opens, or null.</summary>
	public ElementHandle? InitialFocus { get; init; }

	/// <summary>Gets the accessible role of the popup.</summary>
	public AccessibilityRole Role { get; init; } = AccessibilityRole.Group;

	/// <summary>Gets the popup's accessible name, or null.</summary>
	public string? Name { get; init; }
}

/// <summary>The app's one floating surface for menus, tooltips and popovers: a native popup window, so it shows over native child surfaces such as browser pages.</summary>
/// <remarks>
/// Mount it with <c>Cell.Mount&lt;NativePopupInput, NativePopup&gt;</c> while the popup is open and leave it out to close it.
/// When the platform cannot open a native popup, such as with no window or while another popup holds the owner, the content shows
/// as an ordinary <see cref="Portal"/> and hides native surfaces through <see cref="LoknApplication.CoverNativeSurfaces"/> while it does.
/// </remarks>
public sealed class NativePopup : GuardedCell<NativePopupInput>
{
	/// <summary>Holds the native popup while it is open.</summary>
	private PopupWindow? _window;

	/// <summary>Holds the root the native popup shows, which receives each new content.</summary>
	private PopupRoot? _root;

	/// <summary>Tracks whether the content shows as a portal because the native popup could not open.</summary>
	private readonly State<bool> _isPortal = new( false );

	/// <summary>Stores the anchor rectangle the popup was last placed at, to move it only when the anchor moves.</summary>
	private ElementRect _placed;

	/// <summary>Tracks whether the popup is being closed by its owner, so the dismissal is not reported back.</summary>
	private bool _isClosing;

	/// <summary>Stores the application whose native surfaces a portal fallback covers, or null when no shell hosts the window.</summary>
	private LoknApplication? _coveredApp;

	/// <summary>Builds nothing in the owner's tree while the native popup is open, or the content in a portal when it could not open.</summary>
	protected override Blob BuildView( NativePopupInput input )
	{
		if ( !_isPortal.Value )
			return new Container { Width = 0, Height = 0 };

		var portal = new Portal
		{
			Key = "popup",
			Anchor = input.Anchor,
			AnchorRect = input.AnchorRect,
			Placement = input.Placement,
			AccessibilityRole = input.Role,
			AccessibilityName = input.Name ?? "",
		};
		if ( input.Size is { } size )
		{
			portal.Width = size.Width;
			portal.Height = size.Height;
		}

		if ( input.Kind == PopupKind.Menu && input.IsModal )
		{
			portal.FocusScope = new FocusScopeOptions { Modal = true, InitialFocus = input.InitialFocus };
			portal.OnDismiss = _ => Dismiss();
		}

		var surface = new Container { Children = [input.Content] };
		PopupMotion.Apply( surface, _appear?.Value ?? 1, Rise );
		portal.Add( surface );
		return portal;
	}

	/// <summary>Opens the native popup once the anchor has geometry.</summary>
	protected override void Mounted()
	{
		Input.Anchor.MetricsChanged += UpdateAnchor;
		Open();
	}

	/// <summary>Tracks the popup's entrance from 0, faded out and offset, to 1 at rest.</summary>
	private Anim<double>? _appear;

	/// <summary>Gets how far the popup starts from its place: from above when it opens above its anchor, else from below.</summary>
	private double Rise => Input.Placement is PortalPlacement.TopStart or PortalPlacement.TopEnd or PortalPlacement.Top ? PopupMotion.PopoverRise : -PopupMotion.PopoverRise;

	/// <summary>Starts the entrance on the first input; sends later content to the open popup and re-measures it when it has no fixed size.</summary>
	protected override void InputChanged( NativePopupInput input )
	{
		_appear ??= PopupMotion.Start( Animate( 0.0 ) );
		if ( _window is null || _root is null )
			return;

		_root.Update( input.Content );
		if ( input.Size is null )
			Place( force: true );
	}

	/// <summary>Closes the native popup and releases the cover of a portal fallback.</summary>
	protected override void Unmounting()
	{
		Input.Anchor.MetricsChanged -= UpdateAnchor;
		_isClosing = true;
		_window?.Dispose();
		_window = null;
		_root = null;
		_coveredApp?.CoverNativeSurfaces( this, false );
		_coveredApp = null;
	}

	/// <summary>Opens a tooltip in a native popup at the anchor; shows a menu, or a tooltip the platform cannot open, in a portal that covers native surfaces.</summary>
	/// <remarks>
	/// Menus stay in the window: a native menu popup takes Windows' focus and pointer grab from Lokn's window, and after it closed Lokn often
	/// ignored clicks and keys, and an editor opened from a menu choice, such as Rename, could not be typed in. Browser pages hide while a menu shows.
	/// </remarks>
	private void Open()
	{
		var input = Input;
		if ( input.Kind == PopupKind.Menu )
		{
			ShowPortal();
			return;
		}

		var root = new PopupRoot( input.Content );
		var window = new PopupWindow( Host, root, input.Kind );
		window.Dismissed += UpdateDismissed;
		_root = root;
		_window = window;
		_placed = ComputeAnchor( input );
		var result = window.Open( _placed, input.Size, input.Maximum, input.Placement );
		if ( result.Status == PopupStatus.Opened )
		{
			// The popup's root lays out on its first frame; focus waits for it.
			if ( input.InitialFocus is { } focus )
				After( 0, () => focus.Focus() );
			return;
		}

		_isClosing = true;
		window.Dismissed -= UpdateDismissed;
		window.Dispose();
		_window = null;
		_root = null;
		ShowPortal();
	}

	/// <summary>Shows the content in a portal in Lokn's window, hiding native surfaces such as browser pages while it shows.</summary>
	private void ShowPortal()
	{
		_isPortal.Value = true;
		_coveredApp = LoknApplication.Find( Host );
		_coveredApp?.CoverNativeSurfaces( this, true );
	}

	/// <summary>Moves the popup with its anchor.</summary>
	private void UpdateAnchor( ElementMetrics metrics ) => Place( force: false );

	/// <summary>Places the popup at the anchor when the anchor moved, or always when <paramref name="force"/> is set.</summary>
	private void Place( bool force )
	{
		if ( _window is null )
			return;

		var anchor = ComputeAnchor( Input );
		if ( !force && anchor == _placed )
			return;

		_placed = anchor;
		_window.UpdatePlacement( anchor, Input.Size, Input.Maximum, Input.Placement );
	}

	/// <summary>Reports a dismissal the user caused to the owner.</summary>
	private void UpdateDismissed( PopupDismissReason reason )
	{
		_window = null;
		_root = null;
		if ( !_isClosing && reason is not (PopupDismissReason.Programmatic or PopupDismissReason.OwnerClosed) )
			Dismiss();
	}

	/// <summary>Runs the owner's dismiss callback, when it has one.</summary>
	private void Dismiss()
	{
		if ( Input.Dismiss is { } dismiss )
			InvokeInputCallback( dismiss );
	}

	/// <summary>Computes the anchor in window logical coordinates.</summary>
	private static ElementRect ComputeAnchor( NativePopupInput input )
	{
		var box = input.Anchor.BorderBox;
		return input.AnchorRect is { } local
			? new ElementRect { X = box.X + local.X, Y = box.Y + local.Y, Width = local.Width, Height = local.Height }
			: new ElementRect { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height };
	}

	/// <summary>Shows the latest content inside the native popup, at rest from the first frame.</summary>
	/// <remarks>
	/// No entrance plays here: the popup window has no transparency behind its content, so a fade or shrink shows black, and the
	/// owner's animation rebuilds the owner, not this root, which kept the first frame, fully faded, on screen.
	/// </remarks>
	/// <param name="content">The first content.</param>
	private sealed class PopupRoot( Blob content ) : Cell
	{
		/// <summary>Stores the content shown.</summary>
		private Blob _content = content;

		/// <summary>Shows new content.</summary>
		internal void Update( Blob content )
		{
			_content = content;
			Rebuild();
		}

		/// <summary>Builds the content filling the popup.</summary>
		public override Blob Build() => new Container { FlexGrow = 1, Children = [_content] };
	}
}
