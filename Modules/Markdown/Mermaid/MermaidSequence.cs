using System.Text.RegularExpressions;

namespace Lokn.Modules.Markdown;

/// <summary>Parses a Mermaid sequence diagram and lays it out: one column per participant, one row per message, note or frame line.</summary>
internal sealed partial class MermaidSequence
{
	/// <summary>Defines the space around the drawing, in logical pixels.</summary>
	private const double Margin = 16;

	/// <summary>Defines the smallest space between two participants' centers, in logical pixels.</summary>
	private const double MinColumnGap = 120;

	/// <summary>Defines the space inside a participant's box around its name, in logical pixels.</summary>
	private const double BoxPadding = 12;

	/// <summary>Defines the space between rows, in logical pixels.</summary>
	private const double RowGap = 14;

	/// <summary>Holds one participant: its id, its shown name, whether it is an actor, and after layout its column's center.</summary>
	internal sealed class Participant( string id, string name, bool isActor )
	{
		/// <summary>Gets the id messages name.</summary>
		public string Id { get; } = id;

		/// <summary>Gets or sets the shown name.</summary>
		public string Name { get; set; } = name;

		/// <summary>Gets or sets whether it is drawn as an actor.</summary>
		public bool IsActor { get; set; } = isActor;

		/// <summary>Gets or sets the box width, after layout.</summary>
		public double Width { get; set; }

		/// <summary>Gets or sets the box height, after layout.</summary>
		public double Height { get; set; }

		/// <summary>Gets or sets the column's center, after layout.</summary>
		public double X { get; set; }
	}

	/// <summary>Names what one row shows.</summary>
	internal enum RowKind
	{
		/// <summary>A message between participants.</summary>
		Message,

		/// <summary>A note beside or over participants.</summary>
		Note,

		/// <summary>The start of a frame such as <c>loop</c>.</summary>
		FrameStart,

		/// <summary>A divider in a frame such as <c>else</c>.</summary>
		FrameDivider,

		/// <summary>The end of a frame.</summary>
		FrameEnd,
	}

	/// <summary>Holds one row: what it shows, its participants and text, and after layout its vertical place and height.</summary>
	internal sealed class Row( RowKind kind, string text )
	{
		/// <summary>Gets what the row shows.</summary>
		public RowKind Kind { get; } = kind;

		/// <summary>Gets the row's text: the message, the note, or the frame's kind and title.</summary>
		public string Text { get; } = text;

		/// <summary>Gets or sets the participant a message leaves, or a note's first participant.</summary>
		public Participant? From { get; set; }

		/// <summary>Gets or sets the participant a message reaches, or a note's last participant.</summary>
		public Participant? To { get; set; }

		/// <summary>Gets or sets whether a message's line is dashed.</summary>
		public bool IsDashed { get; set; }

		/// <summary>Gets or sets a message's end mark: <c>&gt;</c> filled arrow, <c>)</c> open arrow, <c>x</c> cross, or a space for none.</summary>
		public char Head { get; set; } = '>';

		/// <summary>Gets or sets where a note sits: left of, right of, or over.</summary>
		public string Placement { get; set; } = "";

		/// <summary>Gets or sets the row's top, after layout.</summary>
		public double Top { get; set; }

		/// <summary>Gets or sets the row's height, after layout.</summary>
		public double Height { get; set; }

		/// <summary>Gets or sets the row's number when autonumber is on, else zero.</summary>
		public int Number { get; set; }

		/// <summary>Gets or sets the frame row that starts this one's frame, for dividers and ends.</summary>
		public Row? Frame { get; set; }

		/// <summary>Gets or sets a frame's left and right edges, after layout.</summary>
		public (double Left, double Right) Span { get; set; }
	}

	/// <summary>Gets the participants, left to right.</summary>
	public List<Participant> Participants { get; } = [];

	/// <summary>Gets the rows, top to bottom.</summary>
	public List<Row> Rows { get; } = [];

	/// <summary>Gets the drawing's width, after layout.</summary>
	public double Width { get; private set; }

	/// <summary>Gets the drawing's height, after layout.</summary>
	public double Height { get; private set; }

	/// <summary>Gets the top of the participant boxes at the bottom, after layout.</summary>
	public double FooterTop { get; private set; }

	/// <summary>Gets the height of the participant boxes, after layout.</summary>
	public double HeaderHeight { get; private set; }

	/// <summary>Parses a sequence diagram and lays it out with text measured by <paramref name="measure"/>; null when the source is not one it reads.</summary>
	/// <param name="source">The Mermaid source.</param>
	/// <param name="measure">Measures a label's width and height in logical pixels.</param>
	public static MermaidSequence? Build( string source, Func<string, (double Width, double Height)> measure )
	{
		var diagram = new MermaidSequence();
		if ( !diagram.Parse( source ) || diagram.Participants.Count == 0 )
			return null;
		diagram.Layout( measure );
		return diagram;
	}

	/// <summary>Reads participants, messages, notes and frames; activation and styling lines are skipped.</summary>
	private bool Parse( string source )
	{
		var lines = source.Replace( "\r\n", "\n" ).Split( '\n' ).Select( line => line.Trim() ).Where( line => line.Length > 0 && !line.StartsWith( "%%", StringComparison.Ordinal ) ).ToList();
		if ( lines.Count == 0 || !lines[0].Equals( "sequenceDiagram", StringComparison.OrdinalIgnoreCase ) )
			return false;
		var frames = new Stack<Row>();
		var number = 0;
		foreach ( var line in lines.Skip( 1 ) )
		{
			if ( line.Equals( "autonumber", StringComparison.OrdinalIgnoreCase ) || line.StartsWith( "autonumber ", StringComparison.OrdinalIgnoreCase ) )
			{
				number = 1;
				continue;
			}
			if ( ParticipantPattern().Match( line ) is { Success: true } declared )
			{
				var participant = Find( declared.Groups[2].Value );
				participant.IsActor = declared.Groups[1].Value.Equals( "actor", StringComparison.OrdinalIgnoreCase );
				if ( declared.Groups[3].Success )
					participant.Name = Clean( declared.Groups[3].Value );
				continue;
			}
			if ( MessagePattern().Match( line ) is { Success: true } message )
			{
				var arrow = message.Groups["arrow"].Value;
				Rows.Add( new Row( RowKind.Message, Clean( message.Groups["text"].Value ) )
				{
					From = Find( message.Groups["from"].Value ),
					To = Find( message.Groups["to"].Value ),
					IsDashed = arrow.StartsWith( "--", StringComparison.Ordinal ),
					Head = arrow.EndsWith( ">>", StringComparison.Ordinal ) ? '>' : arrow.EndsWith( 'x' ) ? 'x' : arrow.EndsWith( ')' ) ? ')' : arrow.EndsWith( '>' ) ? ' ' : '>',
					Number = number > 0 ? number++ : 0,
				} );
				continue;
			}
			if ( NotePattern().Match( line ) is { Success: true } note )
			{
				var names = note.Groups[2].Value.Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
				Rows.Add( new Row( RowKind.Note, Clean( note.Groups[3].Value ) )
				{
					Placement = note.Groups[1].Value.ToLowerInvariant().Replace( " of", "", StringComparison.Ordinal ),
					From = Find( names[0] ),
					To = Find( names[^1] ),
				} );
				continue;
			}
			if ( FramePattern().Match( line ) is { Success: true } frame )
			{
				var row = new Row( RowKind.FrameStart, $"{frame.Groups[1].Value.ToLowerInvariant()}{(frame.Groups[2].Value.Trim() is { Length: > 0 } title ? " " + Clean( title ) : "")}" );
				Rows.Add( row );
				frames.Push( row );
				continue;
			}
			if ( DividerPattern().Match( line ) is { Success: true } divider && frames.Count > 0 )
			{
				Rows.Add( new Row( RowKind.FrameDivider, $"{divider.Groups[1].Value.ToLowerInvariant()}{(divider.Groups[2].Value.Trim() is { Length: > 0 } title ? " " + Clean( title ) : "")}" ) { Frame = frames.Peek() } );
				continue;
			}
			if ( line.Equals( "end", StringComparison.OrdinalIgnoreCase ) && frames.Count > 0 )
				Rows.Add( new Row( RowKind.FrameEnd, "" ) { Frame = frames.Pop() } );
		}
		return true;
	}

	/// <summary>Finds a participant by id, adding it the first time it is named.</summary>
	private Participant Find( string id )
	{
		var key = id.Trim();
		if ( Participants.FirstOrDefault( participant => participant.Id == key ) is { } found )
			return found;
		var participant = new Participant( key, key, false );
		Participants.Add( participant );
		return participant;
	}

	/// <summary>Turns <c>&lt;br&gt;</c> into line breaks and removes quotes around text.</summary>
	private static string Clean( string text )
	{
		var value = text.Trim();
		if ( value.Length >= 2 && value[0] == '"' && value[^1] == '"' )
			value = value[1..^1];
		return BreakPattern().Replace( value, "\n" );
	}

	/// <summary>Sizes the participant boxes, spaces the columns so every label fits, and stacks the rows.</summary>
	private void Layout( Func<string, (double Width, double Height)> measure )
	{
		foreach ( var participant in Participants )
		{
			var (width, height) = measure( participant.Name );
			participant.Width = width + BoxPadding * 2;
			participant.Height = height + BoxPadding + (participant.IsActor ? 18 : 0);
		}
		HeaderHeight = Participants.Max( participant => participant.Height );

		// Each pair of neighboring columns is as far apart as the widest label between them needs.
		var gaps = new double[Participants.Count];
		for ( var index = 1; index < Participants.Count; index++ )
			gaps[index] = Math.Max( MinColumnGap, (Participants[index - 1].Width + Participants[index].Width) / 2 + 24 );
		foreach ( var row in Rows )
		{
			if ( row is not { Kind: RowKind.Message or RowKind.Note, From: { } from, To: { } to } )
				continue;
			var a = Participants.IndexOf( from );
			var b = Participants.IndexOf( to );
			var (low, high) = (Math.Min( a, b ), Math.Max( a, b ));
			var labelWidth = measure( row.Number > 0 ? $"{row.Number} {row.Text}" : row.Text ).Width + 32;
			if ( row.Kind == RowKind.Note && row.Placement != "over" )
			{
				// A note beside a participant needs room on its side.
				var side = row.Placement == "left" ? low : high + 1;
				if ( side > 0 && side < gaps.Length )
					gaps[side] = Math.Max( gaps[side], labelWidth + Participants[side - 1].Width / 2 );
				continue;
			}
			if ( high == low )
			{
				// A message to itself loops out to the right.
				if ( high + 1 < gaps.Length )
					gaps[high + 1] = Math.Max( gaps[high + 1], labelWidth + 24 );
				continue;
			}
			var span = 0.0;
			for ( var index = low + 1; index <= high; index++ )
				span += gaps[index];
			if ( span < labelWidth )
			{
				var extra = (labelWidth - span) / (high - low);
				for ( var index = low + 1; index <= high; index++ )
					gaps[index] += extra;
			}
		}

		var leftNote = Rows.Where( row => row is { Kind: RowKind.Note, Placement: "left" } && row.From == Participants[0] ).Select( row => measure( row.Text ).Width + 32 ).DefaultIfEmpty( 0 ).Max();
		var x = Margin + Math.Max( Participants[0].Width / 2, leftNote );
		for ( var index = 0; index < Participants.Count; index++ )
		{
			x += gaps[index];
			Participants[index].X = x;
		}

		var y = Margin + HeaderHeight + RowGap * 2;
		var depth = 0;
		foreach ( var row in Rows )
		{
			var (_, textHeight) = measure( row.Text.Length == 0 ? " " : row.Text );
			row.Top = y;
			row.Height = row.Kind switch
			{
				RowKind.Message when row.From == row.To => textHeight + 30,
				RowKind.Message => textHeight + 14,
				RowKind.Note => textHeight + 14,
				RowKind.FrameEnd => 6,
				_ => textHeight + 10,
			};
			if ( row.Kind == RowKind.FrameStart )
				depth++;
			else if ( row.Kind == RowKind.FrameEnd )
				depth--;
			y += row.Height + RowGap;
		}

		// A frame spans every participant its rows use, a little wider for each frame it is inside.
		var starts = new Stack<(Row Row, int Index)>();
		for ( var index = 0; index < Rows.Count; index++ )
		{
			var row = Rows[index];
			if ( row.Kind == RowKind.FrameStart )
				starts.Push( (row, index) );
			else if ( row.Kind == RowKind.FrameEnd && starts.Count > 0 )
			{
				var (start, first) = starts.Pop();
				var used = Rows.Skip( first ).Take( index - first ).Where( inner => inner.From is not null ).SelectMany( inner => new[] { inner.From!, inner.To! } ).ToList();
				var inset = 12 + 8 * starts.Count;
				var left = used.Count == 0 ? Participants[0].X : used.Min( participant => participant.X );
				var right = used.Count == 0 ? Participants[^1].X : used.Max( participant => participant.X );
				var titleWidth = measure( start.Text ).Width + 40;
				start.Span = (left - Math.Max( inset, 30 ), Math.Max( right + Math.Max( inset, 30 ), left - inset + titleWidth ));
				row.Span = start.Span;
				foreach ( var inner in Rows.Skip( first ).Take( index - first ).Where( inner => inner.Kind == RowKind.FrameDivider && inner.Frame == start ) )
					inner.Span = start.Span;
			}
		}

		FooterTop = y + RowGap;
		Height = FooterTop + HeaderHeight + Margin;
		var rightmost = Participants.Max( participant => participant.X + participant.Width / 2 );
		rightmost = Math.Max( rightmost, Rows.Select( row => row.Span.Right ).DefaultIfEmpty( 0 ).Max() );
		foreach ( var row in Rows.Where( row => row.Kind is RowKind.Note or RowKind.Message ) )
		{
			var text = measure( row.Text ).Width + 32;
			if ( row.Kind == RowKind.Note && row.Placement == "right" )
				rightmost = Math.Max( rightmost, row.To!.X + 10 + text );
			if ( row.Kind == RowKind.Message && row.From == row.To )
				rightmost = Math.Max( rightmost, row.From!.X + 40 + text );
		}
		Width = rightmost + Margin;
	}

	/// <summary>Matches a participant or actor declaration with an optional alias.</summary>
	[GeneratedRegex( @"^(participant|actor)\s+(.+?)(?:\s+as\s+(.+))?$", RegexOptions.IgnoreCase )]
	private static partial Regex ParticipantPattern();

	/// <summary>Matches a message: sender, arrow, receiver and text; activation marks are dropped.</summary>
	[GeneratedRegex( @"^(?<from>[^\-<>:+]+?)\s*(?<arrow><<-->>|<<->>|-->>|->>|-->|->|--x|-x|--\)|-\))\s*[+-]?(?<to>[^:]+?)\s*:\s*(?<text>.*)$" )]
	private static partial Regex MessagePattern();

	/// <summary>Matches a note: where it sits, its participants and its text.</summary>
	[GeneratedRegex( @"^note\s+(left of|right of|over)\s+([^:]+?)\s*:\s*(.*)$", RegexOptions.IgnoreCase )]
	private static partial Regex NotePattern();

	/// <summary>Matches the start of a frame and its title.</summary>
	[GeneratedRegex( @"^(loop|alt|opt|par|critical|break|rect|box)\b(.*)$", RegexOptions.IgnoreCase )]
	private static partial Regex FramePattern();

	/// <summary>Matches a divider inside a frame and its title.</summary>
	[GeneratedRegex( @"^(else|and|option)\b(.*)$", RegexOptions.IgnoreCase )]
	private static partial Regex DividerPattern();

	/// <summary>Matches a line break tag in a label.</summary>
	[GeneratedRegex( @"<br\s*/?>", RegexOptions.IgnoreCase )]
	private static partial Regex BreakPattern();
}
