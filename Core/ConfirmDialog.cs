using Goo;

namespace Lokn.Core;

/// <summary>Supplies a <see cref="ConfirmDialog"/> with its question, what accepting does, and the way to close it.</summary>
/// <param name="App">The application whose native surfaces, such as browser pages, the dialog hides while it is open, since they draw over its scrim.</param>
/// <param name="Title">The question, such as "Remove the worktree feature?".</param>
/// <param name="Message">What accepting does and keeps.</param>
/// <param name="Accept">The accept button's label, or null for a notice with only a Close button.</param>
/// <param name="Confirm">Runs when the user accepts; the owner closes the dialog, at once or when its work ends.</param>
/// <param name="Cancel">Closes the dialog; runs on Cancel, Close and Escape.</param>
public sealed record ConfirmDialogInput( LoknApplication App, string Title, string Message, string? Accept, Action Confirm, Action Cancel )
{
	/// <summary>Gets the lines listed under the message, such as the chats a command removes.</summary>
	public IReadOnlyList<string> Items { get; init; } = [];

	/// <summary>Gets whether accepting deletes or removes something, which draws the accept button in the error color.</summary>
	public bool IsDestructive { get; init; } = true;

	/// <summary>Gets whether the accepted work runs now, which disables both buttons and Escape.</summary>
	public bool IsBusy { get; init; }

	/// <summary>Gets why the accepted work failed, shown above the buttons, or null.</summary>
	public string? Error { get; init; }
}

/// <summary>The app's one confirmation dialog: a card in the middle of the window over a scrim, which takes all input until it closes.</summary>
/// <remarks>
/// Mount it with <c>Cell.Mount&lt;ConfirmDialogInput, ConfirmDialog&gt;</c> while the question is open and leave it out to close it.
/// Focus starts on Cancel, so Enter never accepts a destructive command by accident; Tab moves to the accept button and Escape cancels.
/// </remarks>
public sealed class ConfirmDialog : GuardedCell<ConfirmDialogInput>
{
	/// <summary>Defines the card's width in logical pixels.</summary>
	private const int CardWidth = 400;

	/// <summary>Defines the opacity of the scrim over the window.</summary>
	private const double ScrimOpacity = 0.45;

	/// <summary>Defines how much rounder the card is than the theme's controls, in logical pixels.</summary>
	private const int ExtraRadius = 3;

	/// <summary>Targets the Cancel button, which holds focus when the dialog opens.</summary>
	private readonly ElementHandle _cancel = new();

	/// <summary>Hides native surfaces, such as browser pages, which would draw over the scrim and card.</summary>
	protected override void Mounted() => Input.App.CoverNativeSurfaces( this, true );

	/// <summary>Shows native surfaces again.</summary>
	protected override void Unmounting() => Input.App.CoverNativeSurfaces( this, false );

	/// <summary>Builds the scrim and the centered card: the title, the message, the listed items, the error, then Cancel and the accept button.</summary>
	protected override Blob BuildView( ConfirmDialogInput input )
	{
		var theme = Theme.Current;
		var card = new List<Blob>
		{
			new Text { Key = "title", Content = input.Title, FontWeight = 600, FontSize = theme.FontSize + 1, Color = theme.Text },
			new Text { Key = "message", Content = input.Message, Color = theme.Muted },
		};
		for ( var index = 0; index < input.Items.Count; index++ )
		{
			card.Add( new Text
			{
				Key = "item:" + index,
				Content = "· " + input.Items[index],
				Color = theme.Muted,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );
		}
		if ( input.Error is { Length: > 0 } error )
			card.Add( new Text { Key = "error", Content = error, Color = theme.Error, Selectable = true } );

		var cancel = theme.BuildButton( input.Accept is null ? "Close" : "Cancel", () => InvokeInputCallback( input.Cancel ), !input.IsBusy );
		cancel.Key = "cancel";
		cancel.Handle = _cancel;
		var buttons = new List<Blob> { cancel };
		if ( input.Accept is { } label )
		{
			var accept = theme.BuildButton( label, () => InvokeInputCallback( input.Confirm ), !input.IsBusy );
			accept.Key = "accept";
			accept.BackgroundColor = (input.IsDestructive ? theme.Error : theme.Accent).WithAlpha( 0.22 );
			accept.Color = input.IsBusy ? theme.Muted : input.IsDestructive ? theme.Error : theme.Text;
			buttons.Add( accept );
		}
		card.Add( new Row
		{
			Key = "buttons",
			Margin = new Edges( theme.Spacing, 0, 0, 0 ),
			Gap = theme.Spacing,
			JustifyContent = JustifyContent.FlexEnd,
			Children = buttons,
		} );

		// The portal fills the window and centers the card; the modal scope keeps focus and input inside it.
		var portal = new Portal
		{
			Key = "confirm-dialog",
			Left = 0,
			Top = 0,
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Scrim = Color.Black.WithAlpha( ScrimOpacity ),
			FocusScope = new FocusScopeOptions
			{
				Modal = true,
				InitialFocus = _cancel,
			},
			AccessibilityRole = AccessibilityRole.Dialog,
			AccessibilityName = input.Title,
			OnDismiss = _ =>
			{
				if ( !input.IsBusy )
					InvokeInputCallback( input.Cancel );
			},
		};
		portal.Add( new Container
		{
			Key = "card",
			Width = CardWidth,
			MaxWidth = Length.Percent( 90 ),
			Padding = theme.Spacing * 2,
			Gap = theme.Spacing,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + ExtraRadius,
			BoxShadow = new BoxShadow { OffsetY = 6, Blur = 18, Color = Color.Black.WithAlpha( 0.35 ) },
			Children = card,
		} );
		return portal;
	}
}
