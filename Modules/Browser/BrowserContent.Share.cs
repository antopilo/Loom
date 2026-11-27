using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Holds the Share button a page made by Loom shows in the address bar, such as a report: it saves the page as a PDF in Downloads and shows it
/// in Explorer, or copies the whole page as an image.</summary>
internal sealed partial class BrowserContent
{
	/// <summary>Defines the largest image, in pixels, a page is copied as at twice its size; a longer page is copied at its own size.</summary>
	private const long SharpImageLimit = 24_000_000;

	/// <summary>Defines how long the Share button shows how an export went, in milliseconds.</summary>
	private const int ShareStatusTime = 2500;

	/// <summary>Retains the Share button the menu opens under.</summary>
	private readonly ElementHandle _shareButton = new();

	/// <summary>Tracks whether the Share menu is open.</summary>
	private readonly State<bool> _isShareOpen = new( false );

	/// <summary>Tracks how the latest export went, shown on the Share button for a moment, or an empty string.</summary>
	private readonly State<string> _shareStatus = new( "" );

	/// <summary>Counts exports, so only the latest clears the status it showed.</summary>
	private int _shareCount;

	/// <summary>Builds the Share button and, while it is open, its menu: Export as PDF and Copy as image.</summary>
	private Button BuildShare( Theme theme, BrowserTab tab )
	{
		var status = _shareStatus.Value;
		var button = BuildButton( theme, status.Length > 0 ? "\uE73E" : "\uE72D", status.Length > 0 ? status : "Share", tab.Controller is not null,
			() => UpdateShareMenu( !_isShareOpen.Value ) );
		button.Handle = _shareButton;
		if ( !_isShareOpen.Value )
			return button;

		List<MenuEntry> entries =
		[
			new MenuAction( "Export as PDF", () => _context.Run( () => Share( tab, ExportPdfAsync ) ) ),
			new MenuAction( "Copy as image", () => _context.Run( () => Share( tab, CopyImageAsync ) ) ),
		];
		button.Children =
		[
			.. button.Children,
			Cell.Mount<ContextMenuInput, ContextMenu>( "share", new( _shareButton, "Share", entries, () => _context.Run( () => UpdateShareMenu( false ) ) )
			{
				Width = 200,
			} ),
		];
		return button;
	}

	/// <summary>Opens or closes the Share menu, hiding the pages meanwhile, since a native page would cover it.</summary>
	private void UpdateShareMenu( bool isOpen )
	{
		if ( _isShareOpen.Value == isOpen )
			return;

		_isShareOpen.Value = isOpen;
		CoverPages( isOpen );
		ApplyPlacements();
	}

	/// <summary>Closes the menu and runs one export of the tab's page, then shows how it went on the Share button for a moment.</summary>
	private async void Share( BrowserTab tab, Func<CoreWebView2, string, Task<string>> export )
	{
		UpdateShareMenu( false );
		if ( tab.Controller?.CoreWebView2 is not { } web )
			return;

		var count = ++_shareCount;
		string status;
		try
		{
			status = await export( web, tab.LoomName ?? tab.Title );
		}
		catch ( Exception error ) when ( error is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or FormatException )
		{
			status = $"Could not export: {error.Message}";
		}

		_context.Run( () => _shareStatus.Value = status );
		await Task.Delay( ShareStatusTime );
		_context.Run( () =>
		{
			if ( count == _shareCount )
				_shareStatus.Value = "";
		} );
	}

	/// <summary>Saves the page as a PDF in Downloads, with its backgrounds and without the browser's header and footer, and shows it in Explorer.</summary>
	private static async Task<string> ExportPdfAsync( CoreWebView2 web, string name )
	{
		var path = ComputeExportPath( ReadDownloadsPath(), name, ".pdf" );
		var settings = web.Environment.CreatePrintSettings();
		settings.ShouldPrintBackgrounds = true;
		settings.ShouldPrintHeaderAndFooter = false;
		settings.MarginTop = 0;
		settings.MarginBottom = 0;
		settings.MarginLeft = 0;
		settings.MarginRight = 0;
		if ( !await web.PrintToPdfAsync( path, settings ) )
			return "Could not save the PDF";

		Process.Start( new ProcessStartInfo( "explorer.exe", $"/select,\"{path}\"" ) { UseShellExecute = true } );
		return "Saved to Downloads";
	}

	/// <summary>Copies the whole page as a PNG image, sharp at twice its size unless it is very long, without Loom's own controls on it; the image is
	/// also kept in Loom's temporary folder, so apps that paste files get it too.</summary>
	private async Task<string> CopyImageAsync( CoreWebView2 web, string name )
	{
		if ( _context.App.Window?.Clipboard is not { } clipboard )
			return "Could not copy: no clipboard";

		using var metrics = JsonDocument.Parse( await web.CallDevToolsProtocolMethodAsync( "Page.getLayoutMetrics", "{}" ) );
		var size = metrics.RootElement.GetProperty( "cssContentSize" );
		var width = Math.Ceiling( size.GetProperty( "width" ).GetDouble() );
		var height = Math.Ceiling( size.GetProperty( "height" ).GetDouble() );
		var scale = width * height * 4 <= SharpImageLimit ? 2 : 1;
		string shot;
		await web.ExecuteScriptAsync( "document.documentElement.classList.add( 'loom-capturing' )" );
		try
		{
			shot = await web.CallDevToolsProtocolMethodAsync( "Page.captureScreenshot", JsonSerializer.Serialize( new
			{
				format = "png",
				captureBeyondViewport = true,
				clip = new { x = 0, y = 0, width, height, scale },
			} ) );
		}
		finally
		{
			await web.ExecuteScriptAsync( "document.documentElement.classList.remove( 'loom-capturing' )" );
		}

		using var result = JsonDocument.Parse( shot );
		var bytes = Convert.FromBase64String( result.RootElement.GetProperty( "data" ).GetString() ?? "" );
		var folder = Path.Combine( LoknPaths.TempRoot, "exports" );
		Directory.CreateDirectory( folder );
		var path = ComputeExportPath( folder, name, ".png" );
		await File.WriteAllBytesAsync( path, bytes );
		var written = await clipboard.WriteAsync( new TransferData( [new TransferRepresentation( "image/png", bytes )], [new Uri( path )] ), ClipboardIntent.Copy,
			_context.Lifetime );
		return written.Status == DataTransferStatus.Success ? "Copied as image" : $"Could not copy: {written.Error}";
	}

	/// <summary>Computes a file path in a folder named after the page, adding a number when that name is taken.</summary>
	private static string ComputeExportPath( string folder, string name, string extension )
	{
		var invalid = Path.GetInvalidFileNameChars();
		var safe = new string( [.. name.Select( letter => invalid.Contains( letter ) ? '-' : letter )] ).Trim().TrimEnd( '.' );
		if ( safe.Length == 0 )
			safe = "Loom page";

		var path = Path.Combine( folder, safe + extension );
		for ( var copy = 2; File.Exists( path ); copy++ )
			path = Path.Combine( folder, $"{safe} ({copy}){extension}" );
		return path;
	}

	/// <summary>Reads the user's Downloads folder, wherever they moved it.</summary>
	private static string ReadDownloadsPath()
	{
		var downloads = new Guid( "374DE290-123F-4565-9164-39C4925E467B" );
		if ( SHGetKnownFolderPath( downloads, 0, 0, out var path ) == 0 )
			return path;

		var fallback = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "Downloads" );
		Directory.CreateDirectory( fallback );
		return fallback;
	}

	/// <summary>Reads the path of a known folder, such as Downloads.</summary>
	[LibraryImport( "shell32.dll", StringMarshalling = StringMarshalling.Utf16 )]
	private static partial int SHGetKnownFolderPath( in Guid id, uint flags, nint token, out string path );
}
