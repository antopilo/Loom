using System.Globalization;
using System.Text.Json;
using static Lokn.Modules.Chat.ChatContent;

namespace Lokn.Modules.Chat;

/// <summary>Keeps the questions Omp's tools ask, their questionnaire selections, and sends the answers.</summary>
public sealed partial class ChatSession
{
	/// <summary>Retains pending native tool dialogs in wire order.</summary>
	private readonly List<JsonElement> _dialogs = [];

	/// <summary>Retains selected labels for each opt-in multi-question dialog, by dialog and question key.</summary>
	private readonly Dictionary<string, HashSet<string>> _answers = [];

	/// <summary>Stores what the pending questions looked like at the last change, to tell when <see cref="DialogsChanged"/> must be raised.</summary>
	private string _dialogSignature = "";

	/// <summary>Counts selection changes in questionnaires, which the signature includes.</summary>
	private int _answerRevision;

	/// <summary>Gets the questions waiting for an answer, in wire order.</summary>
	public IReadOnlyList<JsonElement> Dialogs => _dialogs;

	/// <summary>Gets the first question still waiting for an answer, or null when none is.</summary>
	public JsonElement? PendingDialog => _dialogs.Count > 0 ? _dialogs[0] : null;

	/// <summary>Gets the labels selected for one questionnaire question, by dialog and question key; none before the first pick.</summary>
	public IReadOnlySet<string> ReadSelection( string key ) => _answers.TryGetValue( key, out var selected ) ? selected : new HashSet<string>();

	/// <summary>Picks or unpicks one option of a questionnaire question; a single-choice question keeps only the last pick.</summary>
	public void UpdateSelection( string key, string label, bool isMulti )
	{
		if ( !_answers.TryGetValue( key, out var selected ) )
			_answers.Add( key, selected = [] );
		if ( selected.Contains( label ) )
			selected.Remove( label );
		else
		{
			if ( !isMulti )
				selected.Clear();
			selected.Add( label );
		}
		_answerRevision++;
		MarkChanged();
	}

	/// <summary>Raises <see cref="DialogsChanged"/> when the pending questions changed since the last change.</summary>
	private void UpdateDialogSignature()
	{
		var signature = PendingDialog is { } dialog ? $"{_dialogs.Count}:{ReadText( dialog, "id" )}:{_answerRevision}" : "";
		if ( signature == _dialogSignature )
			return;

		_dialogSignature = signature;
		DialogsChanged?.Invoke();
	}

	/// <summary>Stops a question being pending: it leaves the list, and views play its exit; with no view, its selections are released at once.</summary>
	private void UpdateDialogLeave( string id )
	{
		var index = _dialogs.FindIndex( dialog => ReadText( dialog, "id" ) == id );
		if ( index < 0 )
			return;

		var dialog = _dialogs[index];
		_dialogs.RemoveAt( index );
		if ( PanelId is null )
			ReleaseSelections( id );
		DialogLeft?.Invoke( dialog );
	}

	/// <summary>Releases a finished question's selections, once its exit played, so old questions do not accumulate.</summary>
	public void ReleaseSelections( string id )
	{
		foreach ( var key in _answers.Keys.Where( key => key.StartsWith( id + ".", StringComparison.Ordinal ) ).ToArray() )
			_answers.Remove( key );
	}

	/// <summary>Updates ordered dialog state or applies a backend presentation notification.</summary>
	private void UpdateDialog( JsonElement frame )
	{
		var method = ReadText( frame, "method" );
		var id = ReadText( frame, "id" );
		if ( method == "cancel" )
			UpdateDialogLeave( ReadText( frame, "targetId" ) );
		else if ( method is "select" or "confirm" or "input" or "editor" or "ask" or "form" or "open_url" )
		{
			_dialogs.RemoveAll( dialog => ReadText( dialog, "id" ) == id );
			_dialogs.Add( frame.Clone() );
			if ( method == "form" )
				UpdateFormDefaults( id, ReadText( frame, "html" ) );
			_status = "Waiting for your answer";
		}
		else if ( method == "set_editor_text" )
			EditorTextRequested?.Invoke( ReadText( frame, "text" ) );
		else if ( method is "notify" or "setStatus" && ReadText( frame, "message", ReadText( frame, "text", ReadText( frame, "content" ) ) ) is { Length: > 0 } notice )
			_status = notice;
	}

	/// <summary>Picks a form's checked options, by index, as its choice fields' first selections; a form sent again keeps the picks already made.</summary>
	private void UpdateFormDefaults( string id, string html )
	{
		foreach ( var field in HtmlForm.Parse( html ).Fields.OfType<FormChoiceField>() )
		{
			var key = id + "." + field.Name;
			if ( !_answers.ContainsKey( key ) )
				_answers.Add( key, [.. field.Options.Index().Where( entry => entry.Item.IsChecked ).Select( entry => entry.Index.ToString( CultureInfo.InvariantCulture ) )] );
		}
	}

	/// <summary>Dismisses a question that takes no answer, such as an authorization page to open.</summary>
	public void DismissDialog( string id )
	{
		UpdateDialogLeave( id );
		MarkChanged();
	}

	/// <summary>Sends one tool answer and removes its question only after the pipe accepts it; a question no longer pending takes no second answer.</summary>
	public void AnswerDialog( string id, object response )
	{
		if ( _session is not { } session || !_dialogs.Any( dialog => ReadText( dialog, "id" ) == id ) )
			return;
		_ = UpdateDialogAnswerAsync( session, id, response );
	}

	/// <summary>Contains side-channel write failures and retains unanswered dialogs for retry.</summary>
	private async Task UpdateDialogAnswerAsync( Omp.OmpSession session, string id, object response )
	{
		try
		{
			await session.SendAsync( response, Lifetime );
			Post( () =>
			{
				UpdateDialogLeave( id );
				_status = "Working…";
				// Answering is dealing with it, wherever the answer came from, such as the notification feed.
				DialogAnswered?.Invoke();
				if ( PendingDialog is null )
					AttentionCleared?.Invoke();
				MarkChanged();
			} );
		}
		catch ( Exception error )
		{
			Post( () =>
			{
				_status = error.Message;
				MarkChanged();
			} );
		}
	}
}
