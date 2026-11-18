using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Holds the commit message, the amend checkbox and committing, which the side view's commit box shows.</summary>
internal sealed partial class GitChangesPane
{
	/// <summary>Defines the amend checkbox's size in logical pixels.</summary>
	private const int CheckSize = 14;

	/// <summary>Defines the check mark's stroke width in logical pixels.</summary>
	private const double CheckStroke = 1.75;

	/// <summary>Holds the check mark drawn in a checked box.</summary>
	private static readonly VectorPath CheckMark = VectorPath.Parse( "M3.5 7.4 L6 9.9 L10.6 4.6", new ElementRect { Width = CheckSize, Height = CheckSize } );

	/// <summary>Holds the commit description's text.</summary>
	private readonly TextEditorController _description;

	/// <summary>Stores the commit subject as typed.</summary>
	private string _subject = "";

	/// <summary>Reports whether the next commit amends the last one.</summary>
	private bool _amend;

	/// <summary>Stores the message filled in from the last commit when amend was checked, so unchecking clears it only while it is unchanged; null when none was filled in.</summary>
	private string? _prefill;

	/// <summary>Cancels reading the last commit's message when amend is unchecked or the repository changes.</summary>
	private CancellationTokenSource? _amendLoad;

	/// <summary>Stores why the last commit's message could not be read, or null.</summary>
	private string? _amendError;

	/// <summary>Builds the amend checkbox: a box that fills with the accent and a check while checked, and its label.</summary>
	private Button BuildAmendBox()
	{
		var theme = Theme.Current;
		var button = Theme.Current.BuildButton( "Amend", () => _context.Run( UpdateAmend ) );
		button.AccessibilityRole = AccessibilityRole.Checkbox;
		button.AccessibilityName = _amend ? "Amend last commit, checked" : "Amend last commit, not checked";
		button.FlexDirection = FlexDirection.Row;
		button.Gap = theme.Spacing / 2;
		button.Padding = new Edges( 0, theme.Spacing / 2 );
		button.FlexShrink = 0;
		button.Children = [BuildBox( _amend ), new Text { Content = "Amend", }];
		return button;
	}

	/// <summary>Builds a checkbox's box: filled with the accent and a check mark while checked, an outline otherwise.</summary>
	private static Container BuildBox( bool isChecked )
	{
		var theme = Theme.Current;
		var mark = new List<Blob>();
		if ( isChecked )
			mark.Add( new Shape
			{
				Path = CheckMark,
				Fit = ShapeFit.Contain,
				Width = CheckSize - theme.BorderWidth * 2,
				Height = CheckSize - theme.BorderWidth * 2,
				Stroke = new VectorStroke( CheckStroke, theme.Panel )
				{
					Cap = StrokeCap.Round,
					Join = StrokeJoin.Round,
				},
			} );
		return new Container
		{
			Width = CheckSize,
			Height = CheckSize,
			FlexShrink = 0,
			BorderRadius = theme.Radius - 2,
			BorderWidth = theme.BorderWidth,
			BorderColor = isChecked ? theme.Accent : theme.Muted,
			BackgroundColor = isChecked ? theme.Accent : Color.Transparent,
			Children = mark,
		};
	}

	/// <summary>Keeps the typed subject.</summary>
	private void UpdateSubject( string value )
	{
		if ( value == _subject )
			return;

		_subject = value;
		MarkChanged();
	}

	/// <summary>Checks or unchecks amend; checking it with both boxes empty fills them from the last commit's message, and unchecking clears that message if it is unchanged.</summary>
	private void UpdateAmend()
	{
		_amendLoad?.Cancel();
		_amendLoad = null;
		_amendError = null;
		_amend = !_amend;
		if ( !_amend )
		{
			if ( _prefill is { } prefill && ComposeMessage() == prefill )
			{
				_subject = "";
				UpdateDescription( "" );
			}
			_prefill = null;
			MarkChanged();
			return;
		}

		MarkChanged();
		if ( _subject.Length > 0 || _description.Document.Length > 0 || _loadedRoot is not { } root )
			return;

		var load = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_amendLoad = load;
		_ = Task.Run( async () =>
		{
			try
			{
				var message = await GitRepository.ReadHeadMessageAsync( root, load.Token );
				_context.Post( () => ApplyHeadMessage( load, message, null ) );
			}
			catch ( OperationCanceledException )
			{
			}
			catch ( Exception error )
			{
				_context.Post( () => ApplyHeadMessage( load, null, error.Message ) );
			}
		} );
	}

	/// <summary>Fills the subject with the last commit message's first line and the description with the rest, unless amend was unchecked or the user typed meanwhile.</summary>
	private void ApplyHeadMessage( CancellationTokenSource load, string? message, string? error )
	{
		if ( _amendLoad != load || !_amend )
			return;

		_amendLoad = null;
		_amendError = error;
		if ( message is not null && _subject.Length == 0 && _description.Document.Length == 0 )
		{
			var text = message.Replace( "\r\n", "\n" ).Trim();
			var newline = text.IndexOf( '\n' );
			_subject = newline < 0 ? text : text[..newline].TrimEnd();
			UpdateDescription( newline < 0 ? "" : text[(newline + 1)..].Trim() );
			_prefill = ComposeMessage();
		}
		MarkChanged();
	}

	/// <summary>Reports whether the commit button works: a subject is typed, and files are staged or the last commit is amended.</summary>
	private bool CanCommit() => _loadedRoot is not null && _subject.Trim().Length > 0 && (_amend || ListOf( true ).Count > 0);

	/// <summary>Builds the commit message: the subject, then the description after a blank line when there is one.</summary>
	private string ComposeMessage()
	{
		var subject = _subject.Trim();
		var description = _description.Document.GetText().Trim();
		return description.Length > 0 ? subject + "\n\n" + description : subject;
	}

	/// <summary>Commits the staged files, or amends the last commit, through the owner; a successful commit clears the message and unchecks amend.</summary>
	private void Commit()
	{
		if ( !CanCommit() )
			return;

		var message = ComposeMessage();
		var amend = _amend;
		RunGit( amend ? "Amending last commit" : "Committing", async ( root, cancellationToken ) =>
		{
			await GitRepository.CommitAsync( root, message, amend, cancellationToken );
			_context.Post( ClearMessage );
		} );
	}

	/// <summary>Empties both boxes and unchecks amend after a successful commit.</summary>
	private void ClearMessage()
	{
		_amendLoad?.Cancel();
		_amendLoad = null;
		_amendError = null;
		_subject = "";
		UpdateDescription( "" );
		_amend = false;
		_prefill = null;
		MarkChanged();
	}

	/// <summary>Replaces the description's whole text.</summary>
	private void UpdateDescription( string text ) => _description.Document.Apply( new TextChange( new TextRange( 0, _description.Document.Length ), text ) );
}
