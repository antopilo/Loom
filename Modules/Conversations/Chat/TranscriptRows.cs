using Lokn.Core;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Chat;

/// <summary>Selects a run of one message's content blocks.</summary>
/// <param name="Message">The message the blocks belong to.</param>
/// <param name="Start">The first block index.</param>
/// <param name="End">The block index after the last, or <see cref="int.MaxValue"/> for every block and the whole message.</param>
internal readonly record struct TranscriptPart( ConversationMessage Message, int Start, int End )
{
	/// <summary>Selects every block of a message, and its non-block content such as tool details.</summary>
	internal static TranscriptPart Whole( ConversationMessage message ) => new( message, 0, int.MaxValue );
}

/// <summary>Describes one transcript row; a row equal to the one already mounted under its key is not built again.</summary>
/// <param name="Key">The stable row key: its first message's identifier and first block index.</param>
/// <param name="Parts">The message blocks the row shows, in order; an activity row's parts can span several messages.</param>
/// <param name="IsActivity">Whether the row is a run of reasoning and tool calls shown as one summary line.</param>
/// <param name="Results">The tool results paired with the parts' tool calls, in call order.</param>
/// <param name="Revision">Counts view-state changes, such as an opened fold or live tool progress, that the messages alone do not show.</param>
/// <param name="Gap">The space above the row in logical pixels.</param>
/// <param name="Changes">The files a finished turn changed, for the card at its end; null for every other row.</param>
/// <param name="IsRunning">Whether the row is the reasoning or tool run the agent works on now, which shows open until the agent writes or stops.</param>
internal sealed record TranscriptRow( string Key, TranscriptPart[] Parts, bool IsActivity, ConversationMessage[] Results, int Revision, double Gap, ChangedFile[]? Changes = null,
	bool IsRunning = false ) : IMeasuredRow
{
	/// <summary>Compares rows by their parts' messages and ranges, by the identity of each paired result, by their changed files, and by view state.</summary>
	public bool Equals( TranscriptRow? other )
	{
		if ( ReferenceEquals( this, other ) )
			return true;
		if ( other is null || Key != other.Key || IsActivity != other.IsActivity || IsRunning != other.IsRunning || Revision != other.Revision || Gap != other.Gap
			|| Parts.Length != other.Parts.Length || Results.Length != other.Results.Length
			|| (Changes is null ? other.Changes is not null : other.Changes is null || !Changes.SequenceEqual( other.Changes )) )
			return false;

		for ( var i = 0; i < Parts.Length; i++ )
		{
			if ( Parts[i].Start != other.Parts[i].Start || Parts[i].End != other.Parts[i].End || !Parts[i].Message.Equals( other.Parts[i].Message ) )
				return false;
		}
		for ( var i = 0; i < Results.Length; i++ )
		{
			if ( !ReferenceEquals( Results[i], other.Results[i] ) )
				return false;
		}
		return true;
	}

	/// <summary>Hashes the row identity and view state, consistent with <see cref="Equals(TranscriptRow?)"/>.</summary>
	public override int GetHashCode() => HashCode.Combine( Key, Parts.Length, Revision, Gap );
}
