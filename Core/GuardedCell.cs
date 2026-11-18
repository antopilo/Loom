using Goo;

namespace Lokn.Core;

/// <summary>Reports views whose build failed, so the shell can show the error instead of Goo ending the process.</summary>
/// <remarks>Goo calls a nested <see cref="Cell"/>'s build from its frame loop, outside any panel's guard; an exception there would close Lokn.</remarks>
public static class CellFaults
{
	/// <summary>Holds the failures already reported, so a view that fails on every frame reports once.</summary>
	private static readonly HashSet<string> Reported = [];

	/// <summary>Occurs on the UI thread the first time a view type fails with a given message.</summary>
	public static event Action<Type, Exception>? Faulted;

	/// <summary>Reports a failed build once and returns the small error mark drawn in the view's place.</summary>
	/// <param name="view">The view type whose build failed.</param>
	/// <param name="error">The failure.</param>
	internal static Blob Show( Type view, Exception error )
	{
		if ( Reported.Add( view.FullName + "\n" + error.Message ) )
			Faulted?.Invoke( view, error );

		return new Text
		{
			Content = "⚠",
			Color = Theme.Current.Error,
			AccessibilityName = $"{view.Name} failed: {error.Message}",
		};
	}
}

/// <summary>A <see cref="Cell"/> whose build failure shows an error mark and reaches <see cref="CellFaults"/> instead of closing Lokn; use it for every view.</summary>
public abstract class GuardedCell : Cell
{
	/// <summary>Builds the view through <see cref="BuildView"/>, catching its failure.</summary>
	public sealed override Blob Build()
	{
		try
		{
			return BuildView();
		}
		catch ( Exception error )
		{
			return CellFaults.Show( GetType(), error );
		}
	}

	/// <summary>Builds the view's tree, as <see cref="Cell.Build"/> would.</summary>
	protected abstract Blob BuildView();
}

/// <summary>A <see cref="Cell{TInput}"/> whose build failure shows an error mark and reaches <see cref="CellFaults"/> instead of closing Lokn; use it for every view.</summary>
/// <typeparam name="TInput">The immutable input snapshot.</typeparam>
public abstract class GuardedCell<TInput> : Cell<TInput> where TInput : notnull
{
	/// <summary>Builds the view through <see cref="BuildView"/>, catching its failure.</summary>
	protected sealed override Blob Build( TInput input )
	{
		try
		{
			return BuildView( input );
		}
		catch ( Exception error )
		{
			return CellFaults.Show( GetType(), error );
		}
	}

	/// <summary>Builds the view's tree from its input, as <see cref="Cell{TInput}.Build(TInput)"/> would.</summary>
	/// <param name="input">The current input snapshot.</param>
	protected abstract Blob BuildView( TInput input );
}
