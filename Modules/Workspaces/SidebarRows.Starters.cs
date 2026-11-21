using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Builds a group's + and the grid of what a new task can start with, which opens while the pointer rests on the +.</summary>
/// <remarks>
/// The grid shows one icon per <see cref="TaskStarter"/> loaded modules contribute, then one for an empty task. It stays open while the
/// pointer moves from the + into it, and closes shortly after the pointer leaves both, on Escape, or on a press outside.
/// </remarks>
public sealed partial class SidebarRows
{
	/// <summary>Defines a grid icon's width and height in logical pixels.</summary>
	private const int StarterSize = 32;

	/// <summary>Defines a grid icon's glyph size in logical pixels.</summary>
	private const int StarterGlyphSize = 16;

	/// <summary>Defines how many icons a grid row holds.</summary>
	private const int StarterColumns = 4;

	/// <summary>Defines the space between grid icons and around them, in logical pixels.</summary>
	private const int StarterGap = 4;

	/// <summary>Defines how long the grid stays open after the pointer leaves it or the +, in milliseconds, so the pointer can cross between them.</summary>
	private const int StarterCloseMs = 250;

	/// <summary>Defines how long the pointer rests on the + before the grid opens, in milliseconds, so a pointer that only crosses it opens nothing.</summary>
	private const int StarterOpenMs = 150;

	/// <summary>Tracks the group whose grid is open, or an empty group when none is.</summary>
	private readonly State<Guid> _starters = new( Guid.Empty );

	/// <summary>Tracks the title of the grid icon under the pointer, shown under the grid, or empty when none is.</summary>
	private readonly State<string> _starterHint = new( "" );

	/// <summary>Targets each group's +, which its grid opens below.</summary>
	private readonly Dictionary<Guid, ElementHandle> _addButtons = [];

	/// <summary>Reports whether the pointer is on the open grid or its +.</summary>
	private bool _isOverStarters;

	/// <summary>Reports whether a group's grid is open, subscribing the building view.</summary>
	public bool IsStartersOpen( Guid id ) => _starters.Value == id;

	/// <summary>Builds a group's +: a click runs <paramref name="add"/>; resting the pointer on it opens the grid <see cref="BuildStarters"/> draws.</summary>
	/// <param name="id">The group, such as a project.</param>
	/// <param name="accessibilityName">What a click does, such as "New chat in Lokn".</param>
	/// <param name="add">Runs on a click.</param>
	/// <param name="isVisible">Whether the + shows; a hidden one appears on hover or focus.</param>
	public Button BuildAddButton( Guid id, string accessibilityName, Action add, bool isVisible )
	{
		var button = BuildIconButton( AddGlyph, accessibilityName, () =>
		{
			CloseStarters();
			add();
		}, isVisible );
		button.Handle = _addButtons.TryGetValue( id, out var known ) ? known : _addButtons[id] = new ElementHandle();
		button.OnPointerEnter = _ => context.Run( () => EnterStarters( id ) );
		button.OnPointerLeave = _ => context.Run( () => LeaveStarters( id ) );
		return button;
	}

	/// <summary>Builds a group's grid while it is open: one icon per contributed <see cref="TaskStarter"/>, then an empty task; null while closed.</summary>
	/// <param name="id">The group, whose + the grid opens below.</param>
	/// <param name="name">The group's name, such as a project's.</param>
	/// <param name="newTask">Creates an empty task with the given name in the group and opens its workspace, which the chosen starter then fills;
	/// a starter's task is named after it, such as Terminal, and an empty task <see cref="WorkspacesModule.NewTaskName"/>.</param>
	public Blob? BuildStarters( Guid id, string name, Action<string> newTask )
	{
		if ( _starters.Value != id || !_addButtons.TryGetValue( id, out var anchor ) )
			return null;

		var theme = Theme.Current;
		var tiles = new List<Blob>();
		foreach ( var starter in context.App.Extensions.Get<TaskStarter>( TaskStarter.Point ).OrderBy( item => item.Order ) )
		{
			tiles.Add( BuildStarterTile( starter.Title, starter.Glyph, $"{name}, new task with {starter.Title}", () =>
			{
				newTask( starter.Title );
				starter.Open( context.App );
			} ) );
		}
		tiles.Add( BuildStarterTile( "Empty task", TaskGlyph, $"{name}, new empty task", () => newTask( WorkspacesModule.NewTaskName ) ) );

		var columns = Math.Min( tiles.Count, StarterColumns );
		var border = theme.BorderWidth;
		var hint = _starterHint.Value is { Length: > 0 } title ? title : $"New task in {name}";

		var grid = new Container
		{
			Width = columns * StarterSize + (columns - 1) * StarterGap + (StarterGap + border) * 2,
			Padding = StarterGap,
			Gap = StarterGap,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = border,
			BorderRadius = theme.Radius + 3,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize - 1,
			OnPointerEnter = _ => context.Run( () => EnterStarters( id ) ),
			OnPointerLeave = _ => context.Run( () => LeaveStarters( id ) ),
			Children =
			[
				new Row
				{
					Key = "tiles",
					FlexWrap = FlexWrap.Wrap,
					Gap = StarterGap,
					Children = tiles,
				},
				new Text
				{
					Key = "hint",
					Content = hint,
					Color = theme.Muted,
					Padding = new Edges( 0, 2 ),
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
			],
		};
		// Not modal: a modal menu blocks the + under it, whose leave then closes the grid and whose enter reopens it, which flickers,
		// and whose press it takes, so a click there made no chat. The grid closes when the pointer leaves it, and its tiles close it on Escape.
		return Cell.Mount<NativePopupInput, NativePopup>( "starters", new( anchor, grid, () => context.Run( CloseStarters ) )
		{
			Placement = PortalPlacement.BottomEnd,
			Kind = PopupKind.Menu,
			IsModal = false,
			Role = AccessibilityRole.Group,
			Name = $"Start a task in {name}",
		} );
	}

	/// <summary>Builds one grid icon, whose title shows under the grid while the pointer is on it; choosing it closes the grid, then runs <paramref name="start"/>.</summary>
	private Button BuildStarterTile( string title, string glyph, string accessibilityName, Action start )
	{
		var theme = Theme.Current;
		void Choose() => context.Run( () =>
		{
			CloseStarters();
			start();
		} );

		return new Button
		{
			Key = $"starter:{title}",
			Width = StarterSize,
			Height = StarterSize,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = accessibilityName,
			OnClick = Choose,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Choose ),
				new( Key.Space, KeyModifiers.None, Choose ),
				new( Key.Escape, KeyModifiers.None, () => context.Run( CloseStarters ) ),
			],
			// The grid's own leave fires as the pointer moves onto a tile, so a tile counts as being over the grid, and the gap between tiles stays inside it.
			OnPointerEnter = _ => context.Run( () =>
			{
				_isOverStarters = true;
				_starterHint.Value = title;
			} ),
			OnPointerLeave = _ => context.Run( () =>
			{
				if ( _starterHint.Value == title )
					_starterHint.Value = "";
			} ),
			Children =
			[
				new Text
				{
					Content = glyph,
					FontFamily = IconFont.Family,
					FontSize = StarterGlyphSize,
					Color = theme.Text,
				},
			],
		};
	}

	/// <summary>Opens a group's grid once the pointer has rested on its + or reached the grid, closing any other group's.</summary>
	private void EnterStarters( Guid id )
	{
		_isOverStarters = true;
		if ( _starters.Value == id )
			return;

		_ = Task.Delay( StarterOpenMs, context.Lifetime ).ContinueWith( _ => context.Post( () =>
		{
			if ( !_isOverStarters || _starters.Value == id )
				return;

			_starterHint.Value = "";
			_starters.Value = id;
		} ), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default );
	}

	/// <summary>Closes a group's grid shortly after the pointer leaves its + or the grid, unless the pointer reached the other by then.</summary>
	private void LeaveStarters( Guid id )
	{
		_isOverStarters = false;
		_ = Task.Delay( StarterCloseMs, context.Lifetime ).ContinueWith( _ => context.Post( () =>
		{
			if ( !_isOverStarters && _starters.Value == id )
				CloseStarters();
		} ), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default );
	}

	/// <summary>Closes the open grid.</summary>
	private void CloseStarters()
	{
		_isOverStarters = false;
		_starterHint.Value = "";
		_starters.Value = Guid.Empty;
	}
}
