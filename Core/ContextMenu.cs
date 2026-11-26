using Goo;

namespace Lokn.Core;

/// <summary>Describes one line of a <see cref="ContextMenu"/>.</summary>
public abstract record MenuEntry;

/// <summary>Describes a menu action: its label, an optional key on the right, and what it does.</summary>
/// <param name="Label">The text shown and read to assistive technology.</param>
/// <param name="Run">Runs the action after the menu closes.</param>
public sealed record MenuAction( string Label, Action Run ) : MenuEntry
{
	/// <summary>Gets the letter shown on the right that chooses the action while the menu is open, or null for none.</summary>
	public Key? AccessKey { get; init; }

	/// <summary>Gets a global shortcut shown on the right instead of the access key, such as "Ctrl+W"; the menu itself does not handle it.</summary>
	public string? Shortcut { get; init; }

	/// <summary>Gets whether the action deletes or removes something, which draws it in the error color.</summary>
	public bool IsDestructive { get; init; }

	/// <summary>Gets whether the action can run now; a disabled one is shown dimmed and cannot be chosen.</summary>
	public bool IsEnabled { get; init; } = true;
}

/// <summary>Describes a thin rule between groups of actions.</summary>
public sealed record MenuSeparator : MenuEntry;

/// <summary>Describes a muted line of text that cannot be chosen, such as a hint.</summary>
/// <param name="Text">The text shown.</param>
public sealed record MenuNote( string Text ) : MenuEntry;

/// <summary>Supplies a context menu with its anchor, entries, and the way to close it.</summary>
/// <param name="Anchor">The element the menu opens beside.</param>
/// <param name="Name">The menu's accessible name, such as "Project overview options".</param>
/// <param name="Entries">The actions, separators and notes, top to bottom.</param>
/// <param name="Dismiss">Closes the menu; called before a chosen action runs and on Escape or a press outside.</param>
public sealed record ContextMenuInput( ElementHandle Anchor, string Name, IReadOnlyList<MenuEntry> Entries, Action Dismiss )
{
	/// <summary>Gets a rectangle in the anchor's local coordinates to open beside, such as the pointer of a right-click; null uses the whole anchor.</summary>
	public ElementRect? AnchorRect { get; init; }

	/// <summary>Gets the preferred side; the menu flips to stay in the window.</summary>
	public PortalPlacement Placement { get; init; } = PortalPlacement.BottomStart;

	/// <summary>Gets the menu width in logical pixels.</summary>
	public int Width { get; init; } = 200;
}

/// <summary>The app's one context menu: a floating rounded card of actions with their keys on the right, separators between groups, and destructive actions in the error color.</summary>
/// <remarks>
/// Mount it with <c>Cell.Mount&lt;ContextMenuInput, ContextMenu&gt;</c> while the menu is open and leave it out to close it.
/// Up and Down move the highlight, Enter or Space choose it, an action's letter chooses that action, and Escape or a press outside closes the menu.
/// </remarks>
public sealed class ContextMenu : GuardedCell<ContextMenuInput>
{
	/// <summary>Defines one action row's height in logical pixels.</summary>
	private const int RowHeight = 26;

	/// <summary>Defines a note row's height in logical pixels.</summary>
	private const int NoteHeight = 24;

	/// <summary>Defines the height a separator takes, its rule centered, in logical pixels.</summary>
	private const int SeparatorHeight = 9;

	/// <summary>Defines the inset between the card's edge and its rows in logical pixels.</summary>
	private const int Inset = 4;

	/// <summary>Defines how much rounder the card is than the theme's controls, in logical pixels.</summary>
	private const int ExtraRadius = 3;

	/// <summary>Defines the opacity of a disabled action.</summary>
	private const double DisabledOpacity = 0.45;

	/// <summary>Targets the card's content, which holds keyboard focus while the menu is open.</summary>
	private readonly ElementHandle _content = new();

	/// <summary>Stores the highlighted entry's index, or -1 when none is.</summary>
	private readonly State<int> _highlight = new( -1 );

	/// <summary>Builds the card at the anchor with one row per entry.</summary>
	protected override Blob BuildView( ContextMenuInput input )
	{
		var theme = Theme.Current;
		var rows = new List<Blob>();
		var height = (Inset + theme.BorderWidth) * 2;
		for ( var index = 0; index < input.Entries.Count; index++ )
		{
			switch ( input.Entries[index] )
			{
				case MenuAction action:
					rows.Add( BuildAction( index, action, input ) );
					height += RowHeight;
					break;
				case MenuSeparator:
					rows.Add( new Container
					{
						Key = "separator:" + index,
						Height = SeparatorHeight,
						FlexShrink = 0,
						JustifyContent = JustifyContent.Center,
						Padding = new Edges( 0, theme.Spacing / 2 ),
						Children = [new Container { Height = theme.BorderWidth, BackgroundColor = theme.Divider }],
					} );
					height += SeparatorHeight;
					break;
				case MenuNote note:
					rows.Add( new Text
					{
						Key = "note:" + index,
						Content = note.Text,
						MinHeight = NoteHeight,
						FlexShrink = 0,
						Padding = new Edges( 0, theme.Spacing + 2 ),
						FontSize = theme.FontSize - 1,
						Color = theme.Muted,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					} );
					height += NoteHeight;
					break;
			}
		}

		// A native popup draws over native child surfaces, such as browser pages, which a portal cannot.
		var card = new Container
		{
			Handle = _content,
			Focusable = true,
			FlexGrow = 1,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.GetScrollbar(),
			Padding = Inset,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + ExtraRadius,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			OnKeyDown = UpdateKey,
			Children = rows,
		};
		return Cell.Mount<NativePopupInput, NativePopup>( "context-menu", new( input.Anchor, card, input.Dismiss )
		{
			AnchorRect = input.AnchorRect,
			Placement = input.Placement,
			Size = new LayoutSize( input.Width, height ),
			InitialFocus = _content,
			Role = AccessibilityRole.Menu,
			Name = input.Name,
		} );
	}

	/// <summary>Builds one action row: its label, then its key or shortcut on the right.</summary>
	private Button BuildAction( int index, MenuAction action, ContextMenuInput input )
	{
		var theme = Theme.Current;
		var isHighlighted = action.IsEnabled && _highlight.Value == index;
		var hint = action.Shortcut ?? action.AccessKey?.ToString() ?? "";
		return new Button
		{
			Key = "action:" + index,
			MinHeight = RowHeight,
			FlexShrink = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 0, theme.Spacing + 2 ),
			BorderRadius = theme.Radius,
			BackgroundColor = isHighlighted ? theme.Hover : Color.Transparent,
			Opacity = action.IsEnabled ? 1 : DisabledOpacity,
			Focusable = false,
			Disabled = !action.IsEnabled,
			Cursor = action.IsEnabled ? Cursor.Pointer : Cursor.Default,
			AccessibilityRole = AccessibilityRole.MenuItem,
			AccessibilityName = action.Label,
			OnPointerEnter = _ => _highlight.Value = action.IsEnabled ? index : -1,
			OnPointerLeave = _ =>
			{
				if ( _highlight.Value == index )
					_highlight.Value = -1;
			},
			OnClick = () => Choose( action ),
			Children =
			[
				new Text
				{
					Content = action.Label,
					Color = action.IsDestructive ? theme.Error : theme.Text,
					FlexGrow = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				new Text
				{
					Content = hint,
					Color = theme.Muted,
					FontSize = theme.FontSize - 1,
					FlexShrink = 0,
				},
			],
		};
	}

	/// <summary>Moves the highlight with the arrows, chooses with Enter, Space or an action's letter.</summary>
	private void UpdateKey( KeyEvent e )
	{
		if ( e.Modifiers != KeyModifiers.None )
			return;

		var entries = Input.Entries;
		if ( e.Key is Key.Up or Key.Down )
		{
			var step = e.Key == Key.Down ? 1 : -1;
			var index = _highlight.Value;
			for ( var tried = 0; tried < entries.Count; tried++ )
			{
				index = ((index + step) % entries.Count + entries.Count) % entries.Count;
				if ( entries[index] is MenuAction { IsEnabled: true } )
				{
					_highlight.Value = index;
					break;
				}
			}

			e.PreventDefault();
			return;
		}

		var chosen = e.Key is Key.Enter or Key.Space
			? _highlight.Value >= 0 && _highlight.Value < entries.Count ? entries[_highlight.Value] as MenuAction : null
			: entries.OfType<MenuAction>().FirstOrDefault( action => action.AccessKey == e.Key );
		if ( chosen is not { IsEnabled: true } )
			return;

		e.PreventDefault();
		Choose( chosen );
	}

	/// <summary>Closes the menu, then runs the action.</summary>
	private void Choose( MenuAction action )
	{
		if ( !action.IsEnabled )
			return;

		InvokeInputCallback( Input.Dismiss );
		InvokeInputCallback( action.Run );
	}
}
