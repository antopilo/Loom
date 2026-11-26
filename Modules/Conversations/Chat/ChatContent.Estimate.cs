using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Lokn.Modules.Chat;

/// <summary>Estimates a transcript row's height from its content until layout measures it.</summary>
/// <remarks>
/// A row scrolled into view is first placed at its estimate and measured a frame later; the list then moves the offset by the difference.
/// A fixed 80 px put answers that are 400 px tall a few hundred pixels off, which showed as a one-frame jump while scrolling up.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines the height of one line of prose in logical pixels.</summary>
	private const double EstimatedLineHeight = 22;

	/// <summary>Defines how many characters of prose fit on one line of a transcript at its usual width.</summary>
	private const int EstimatedLineCharacters = 100;

	/// <summary>Defines the height of a folded activity line or a tool line in logical pixels.</summary>
	private const double EstimatedToolHeight = 30;

	/// <summary>Defines the height of an inline image in logical pixels: its thumbnail height.</summary>
	private const double EstimatedImageHeight = ImageHeight + 8;

	/// <summary>Defines the space a message row takes beyond its lines, such as its actions and padding, in logical pixels.</summary>
	private const double EstimatedRowChrome = 28;

	/// <summary>Holds each row's estimate, so the list's every placement does not read its content again.</summary>
	private static readonly ConditionalWeakTable<TranscriptRow, StrongBox<double>> _estimates = [];

	/// <summary>Gets a row's estimated height: a folded line for activity, else its text's lines, tool lines and images.</summary>
	private static double EstimateRowHeight( TranscriptRow row ) => _estimates.GetValue( row, static row => new( row.Gap + ComputeEstimate( row ) ) ).Value;

	/// <summary>Computes a row's estimated content height without its gap.</summary>
	private static double ComputeEstimate( TranscriptRow row )
	{
		if ( row.IsActivity )
			return EstimatedToolHeight;

		var height = EstimatedRowChrome;
		foreach ( var part in row.Parts )
		{
			var hasBlocks = false;
			foreach ( var block in ReadBlocks( part ) )
			{
				hasBlocks = true;
				height += ReadText( block, "type" ) switch
				{
					"text" => EstimateText( ReadText( block, "text" ) ),
					"image" => EstimatedImageHeight,
					"thinking" => 0,
					_ => EstimatedToolHeight,
				};
			}
			if ( !hasBlocks )
				height += EstimateText( part.Message.Text );
		}
		return height;
	}

	/// <summary>Estimates the height of text as its lines, each wrapped at the usual line length.</summary>
	private static double EstimateText( string text )
	{
		if ( text.Length == 0 )
			return 0;

		var lines = 0;
		foreach ( var line in text.AsSpan().EnumerateLines() )
			lines += Math.Max( 1, (line.Length + EstimatedLineCharacters - 1) / EstimatedLineCharacters );
		return lines * EstimatedLineHeight;
	}
}
