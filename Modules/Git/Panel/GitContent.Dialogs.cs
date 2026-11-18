using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>An open in-panel dialog: a question with an optional text field, and the action its accept button runs.</summary>
/// <param name="Title">The question.</param>
/// <param name="Message">What accepting does.</param>
/// <param name="Value">The field's initial text, or null for a confirmation without a field.</param>
/// <param name="Accept">The accept button's label.</param>
/// <param name="IsDestructive">Whether accepting deletes or discards something, which draws the button in the error color.</param>
/// <param name="AllowEmpty">Whether an empty field may be accepted.</param>
/// <param name="Run">Runs with the trimmed field text, or an empty string for a confirmation.</param>
internal sealed record GitDialog( string Title, string Message, string? Value, string Accept, bool IsDestructive, bool AllowEmpty, Action<string> Run );

/// <summary>Builds the in-panel dialog that asks for a name or confirms a destructive command.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines the dialog card width in logical pixels.</summary>
	private const int DialogWidth = 380;

	/// <summary>Defines the opacity of the panel color behind the dialog.</summary>
	private const double ScrimOpacity = 0.7;

	/// <summary>Targets the dialog's field, to select its initial text.</summary>
	private readonly ElementHandle _dialogEntry = new();

	/// <summary>Stores the open dialog, or null.</summary>
	private GitDialog? _dialog;

	/// <summary>Stores the dialog field's current text.</summary>
	private string _dialogText = "";

	/// <summary>Asks for text, such as a branch name, then runs <paramref name="run"/> with it trimmed.</summary>
	private void Ask( string title, string message, string initial, string accept, Action<string> run, bool allowEmpty = false )
	{
		_dialog = new( title, message, initial, accept, false, allowEmpty, run );
		_dialogText = initial;
		_dialogEntry.SelectText( 0, initial.Length );
		MarkChanged();
	}

	/// <summary>Asks before a destructive command, then runs it.</summary>
	private void Confirm( string title, string message, string accept, Action run )
	{
		_dialog = new( title, message, null, accept, true, true, _ => run() );
		_dialogText = "";
		MarkChanged();
	}

	/// <summary>Closes the dialog and runs its action, unless its field is empty and must not be.</summary>
	private void AcceptDialog()
	{
		if ( _dialog is not { } dialog )
			return;

		var text = _dialogText.Trim();
		if ( dialog.Value is not null && text.Length == 0 && !dialog.AllowEmpty )
			return;

		_dialog = null;
		MarkChanged();
		dialog.Run( text );
	}

	/// <summary>Closes the dialog without running its action.</summary>
	private void CancelDialog()
	{
		_dialog = null;
		MarkChanged();
	}

	/// <summary>Builds the open dialog over the panel, or null.</summary>
	private Container? BuildDialog()
	{
		if ( _dialog is not { } dialog )
			return null;

		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Text { Content = dialog.Title, FontWeight = EmphasisWeight, FontSize = theme.FontSize + 1, },
			new Text { Content = dialog.Message, Color = theme.Muted, FontSize = ViewStyle.SmallSize, },
		};
		if ( dialog.Value is not null )
			children.Add( new TextEntry
			{
				Key = "git-dialog-entry",
				Handle = _dialogEntry,
				AutoFocus = true,
				Value = _dialogText,
				Controlled = true,
				AccessibilityName = dialog.Title,
				Height = theme.ControlHeight,
				Padding = new Edges( 0, theme.Spacing ),
				BackgroundColor = theme.Panel,
				BorderColor = theme.Accent,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				OnChange = value => _context.Run( () => _dialogText = value ),
				OnSubmit = value => _context.Run( () =>
				{
					_dialogText = value;
					AcceptDialog();
				} ),
			} );

		var cancel = theme.BuildButton( "Cancel", () => _context.Run( CancelDialog ) );
		// A confirmation starts on Cancel, so Enter never deletes by accident.
		cancel.AutoFocus = dialog.Value is null;
		var accept = theme.BuildButton( dialog.Accept, () => _context.Run( AcceptDialog ) );
		accept.BackgroundColor = (dialog.IsDestructive ? theme.Error : theme.Accent).WithAlpha( 0.22 );
		accept.Color = dialog.IsDestructive ? theme.Error : theme.Text;
		children.Add( new Row
		{
			Gap = theme.Spacing,
			JustifyContent = JustifyContent.FlexEnd,
			Children = [cancel, accept],
		} );

		// The scrim covers the panel so nothing behind the card takes input; Escape or Cancel closes it.
		return new Container
		{
			Key = "git-dialog",
			Position = PositionType.Absolute,
			Left = 0,
			Top = 0,
			Right = 0,
			Bottom = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BackgroundColor = theme.Panel.WithAlpha( ScrimOpacity ),
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = dialog.Title,
			KeyBindings = [new( Key.Escape, KeyModifiers.None, () => _context.Run( CancelDialog ) )],
			Children =
			[
				new Container
				{
					Width = DialogWidth,
					MaxWidth = Length.Percent( 90 ),
					Padding = theme.Spacing * 2,
					Gap = theme.Spacing,
					BackgroundColor = theme.Raised,
					BorderColor = theme.Border,
					BorderWidth = theme.BorderWidth,
					BorderRadius = theme.Radius + 3,
					BoxShadow = new BoxShadow { OffsetY = 6, Blur = 18, Color = Color.Black.WithAlpha( 0.35 ) },
					AccessibilityRole = AccessibilityRole.Group,
					AccessibilityName = dialog.Title,
					Children = children,
				},
			],
		};
	}
}
