namespace Lokn.Modules.Terminal;

/// <summary>Keeps the newest lines scrolled off the top of the main screen, up to a capacity.</summary>
internal sealed class TerminalHistory
{
	/// <summary>Defines the first allocation; the ring grows on demand up to its capacity.</summary>
	private const int InitialSize = 256;

	/// <summary>Stores lines in a ring starting at <see cref="_start"/>.</summary>
	private TerminalLine[] _lines = [];

	/// <summary>Stores the ring index of the oldest line.</summary>
	private int _start;

	/// <summary>Gets the most lines kept.</summary>
	internal int Capacity
	{
		get; private set;
	}

	/// <summary>Gets the number of kept lines.</summary>
	internal int Count
	{
		get; private set;
	}

	/// <summary>Gets how many lines were ever dropped or cleared, so line numbers stay stable as old lines go.</summary>
	internal long Dropped
	{
		get; private set;
	}

	/// <summary>Creates an empty history.</summary>
	internal TerminalHistory( int capacity ) => Capacity = Math.Max( 0, capacity );

	/// <summary>Gets a kept line, oldest first.</summary>
	internal TerminalLine this[int index] => _lines[(_start + index) % _lines.Length];

	/// <summary>Adds the newest line, dropping the oldest when full.</summary>
	internal void Add( TerminalLine line )
	{
		if ( Capacity == 0 )
		{
			Dropped++;
			return;
		}

		line.Trim();
		if ( Count == _lines.Length && _lines.Length < Capacity )
			Grow( Math.Min( Capacity, Math.Max( InitialSize, _lines.Length * 2 ) ) );

		if ( Count < _lines.Length )
		{
			_lines[(_start + Count) % _lines.Length] = line;
			Count++;
			return;
		}

		_lines[_start] = line;
		_start = (_start + 1) % _lines.Length;
		Dropped++;
	}

	/// <summary>Removes and returns the newest line, or null when empty.</summary>
	internal TerminalLine? RemoveNewest()
	{
		if ( Count == 0 )
			return null;

		Count--;
		return _lines[(_start + Count) % _lines.Length];
	}

	/// <summary>Forgets every line.</summary>
	internal void Clear()
	{
		Dropped += Count;
		Count = 0;
		_start = 0;
		_lines = [];
	}

	/// <summary>Changes the capacity, keeping the newest lines.</summary>
	internal void UpdateCapacity( int capacity )
	{
		capacity = Math.Max( 0, capacity );
		if ( capacity == Capacity )
			return;

		var excess = Math.Max( 0, Count - capacity );
		var kept = new TerminalLine[Math.Min( capacity, Math.Max( Count - excess, InitialSize ) )];
		for ( var index = 0; index < Count - excess; index++ )
			kept[index] = this[excess + index];

		Dropped += excess;
		Count -= excess;
		Capacity = capacity;
		_lines = kept;
		_start = 0;
	}

	/// <summary>Reallocates the ring in order with room for more lines.</summary>
	private void Grow( int size )
	{
		var lines = new TerminalLine[size];
		for ( var index = 0; index < Count; index++ )
			lines[index] = this[index];

		_lines = lines;
		_start = 0;
	}
}
