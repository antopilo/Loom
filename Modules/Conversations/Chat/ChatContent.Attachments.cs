using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Collects files and pasted images for the next prompt and sends them as Omp file mentions.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the widest attachment chip label in logical pixels before it is trimmed.</summary>
	private const int AttachmentLabelWidth = 220;

	/// <summary>Defines the largest image thumbnail in a sent message, in logical pixels.</summary>
	private const int ThumbnailWidth = 320;

	/// <summary>Defines the tallest image thumbnail in a sent message, in logical pixels.</summary>
	private const int ThumbnailHeight = 180;

	/// <summary>Defines how many thumbnail pixels are decoded per logical pixel of the size limit, so large images stay sharp on high-density displays.</summary>
	private const int ThumbnailDensity = 2;

	/// <summary>Lists the file types a sent message shows as thumbnails; the thumbnail loader reads PNG, JPEG and a GIF's first frame.</summary>
	private static readonly string[] _thumbnailExtensions = [".png", ".jpg", ".jpeg", ".gif"];

	/// <summary>Retains attached absolute paths in the order they were added, until a prompt carrying them is accepted.</summary>
	private readonly List<string> _attachments = [];

	/// <summary>Retains sent-image thumbnails by path; null marks an image that could not be read.</summary>
	private readonly Dictionary<string, ImageSource?> _thumbnails = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Maps each thumbnail still loading to the messages that show it, so each one is rebuilt when it arrives.</summary>
	private readonly Dictionary<string, HashSet<Guid>> _thumbnailLoads = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Holds the thumbnails loaded since a message last showed them, which fade in over their file chips once.</summary>
	private readonly HashSet<string> _loadedThumbnails = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Lists the attachments removed from the next prompt whose chips are still leaving, with the place each held.</summary>
	private readonly List<(string Path, int Index)> _detaching = [];

	/// <summary>Reports whether Explorer files are being dragged over the composer.</summary>
	private bool _isFileDragOver;

	/// <summary>Appends each attachment as a quoted Omp <c>@path</c> mention, which Omp reads, resizes or converts before the model sees it.</summary>
	internal static string ComposePrompt( string text, IReadOnlyList<string> attachments )
	{
		if ( attachments.Count == 0 )
			return text;

		var mentions = string.Join( " ", attachments.Select( path => $"@\"{path}\"" ) );
		return string.IsNullOrWhiteSpace( text ) ? mentions : $"{text.TrimEnd()}\n\n{mentions}";
	}

	/// <summary>Builds a sent prompt's text without its <c>@path</c> mentions, then shows the mentioned files: images as thumbnails, other files as chips.</summary>
	private IEnumerable<Blob> BuildSentText( Guid messageId, string text )
	{
		var paths = Mention().Matches( text ).Select( match => match.Groups["path"].Value ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
		var prose = paths.Length == 0 ? text : Mention().Replace( text, "" ).Trim();
		if ( !string.IsNullOrWhiteSpace( prose ) )
			yield return BuildText( prose );
		if ( paths.Length > 0 )
		{
			yield return new Row
			{
				Key = "sent-attachments",
				FlexWrap = FlexWrap.Wrap,
				Gap = Theme.Current.Spacing / 2,
				Children = [.. paths.Select( path => BuildSentAttachment( messageId, path ) )],
			};
		}
	}

	/// <summary>Shows a mentioned image once its thumbnail is loaded, fading in over its file chip, and a file chip until then or for any other file.</summary>
	private Blob BuildSentAttachment( Guid messageId, string path )
	{
		if ( !_thumbnailExtensions.Contains( Path.GetExtension( path ), StringComparer.OrdinalIgnoreCase ) )
			return BuildFileChip( path );
		if ( !_thumbnails.TryGetValue( path, out var source ) )
		{
			UpdateThumbnailLoad( messageId, path );
			return BuildFileChip( path );
		}
		if ( source is null )
			return BuildFileChip( path );

		// A small image shows at its own size; a larger one shrinks into the limits.
		var scale = Math.Min( 1.0, Math.Min( ( double )ThumbnailWidth / source.Width, ( double )ThumbnailHeight / source.Height ) );
		var key = $"sent-image:{path}";
		var image = new Image
		{
			Key = key,
			Source = source,
			Fit = ImageFit.Contain,
			Width = Math.Max( 1, Math.Round( source.Width * scale ) ),
			Height = Math.Max( 1, Math.Round( source.Height * scale ) ),
			MaxWidth = Length.Percent( 100 ),
			BorderRadius = Theme.Current.Radius,
			AccessibilityName = Path.GetFileName( path ),
		};
		return Cell.Mount<RevealInput, Reveal>( key, new( image, _loadedThumbnails.Remove( path ) ) );
	}

	/// <summary>Reads a thumbnail off the UI thread, once per path, and rebuilds every message waiting for it.</summary>
	private void UpdateThumbnailLoad( Guid messageId, string path )
	{
		if ( _thumbnailLoads.TryGetValue( path, out var waiting ) )
		{
			waiting.Add( messageId );
			return;
		}

		_thumbnailLoads[path] = [messageId];
		var lifetime = context.Lifetime;
		_ = Task.Run( () =>
		{
			ImageSource? source = null;
			try
			{
				source = ImageSource.LoadThumbnail( path, ThumbnailWidth * ThumbnailDensity, ThumbnailHeight * ThumbnailDensity, lifetime );
			}
			catch ( Exception )
			{
				// A moved, deleted or unreadable file keeps its chip.
			}

			var isPosted = context.Post( () =>
			{
				_thumbnails[path] = source;
				if ( source is not null )
					_loadedThumbnails.Add( path );
				if ( _thumbnailLoads.Remove( path, out var messages ) )
				{
					foreach ( var message in messages )
						MarkRowChanged( message );
				}
			} );
			if ( !isPosted )
				source?.Dispose();
		} );
	}

	/// <summary>Builds a bordered chip naming a file, with optional trailing controls.</summary>
	private static Row BuildFileChip( string path, Blob? trailing = null ) => new()
	{
		Key = $"attachment:{path}",
		AlignItems = AlignItems.Center,
		Gap = Theme.Current.Spacing / 2,
		Height = Theme.Current.ControlHeight - Theme.Current.Spacing,
		Padding = trailing is null ? new Edges( 0, Theme.Current.Spacing ) : new Edges( 0, 0, 0, Theme.Current.Spacing ),
		BorderRadius = Theme.Current.Radius,
		BorderWidth = Theme.Current.BorderWidth,
		BorderColor = Theme.Current.Border,
		BackgroundColor = Theme.Current.Panel,
		Children =
		[
			new Text
			{
				Content = Path.GetFileName( path ),
				FontSize = ViewStyle.SmallSize,
				MaxWidth = AttachmentLabelWidth,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			.. trailing is null ? Array.Empty<Blob>() : [trailing],
		],
	};

	/// <summary>Matches a quoted <c>@"path"</c> mention as <see cref="ComposePrompt"/> writes it.</summary>
	[GeneratedRegex( "@\"(?<path>[^\"]+)\"" )]
	private static partial Regex Mention();

	/// <summary>Adds existing files once each, keeping their order.</summary>
	private void UpdateAttach( IEnumerable<string> paths )
	{
		var added = false;
		foreach ( var path in paths )
		{
			if ( !File.Exists( path ) && !Directory.Exists( path ) )
				continue;
			if ( _attachments.Contains( path, StringComparer.OrdinalIgnoreCase ) )
				continue;

			_attachments.Add( path );
			added = true;
		}
		if ( added )
			MarkChanged();
	}

	/// <summary>Removes one attachment from the next prompt without touching the file; its chip fades out while the others close the gap.</summary>
	private void UpdateDetach( string path )
	{
		var index = _attachments.IndexOf( path );
		if ( index < 0 )
			return;

		// The prompt loses the file at once; only its chip lingers for the exit.
		_attachments.RemoveAt( index );
		_detaching.RemoveAll( item => item.Path == path );
		_detaching.Add( (path, index) );
		UpdateAfterExit( () =>
		{
			_detaching.RemoveAll( item => item.Path == path );
			MarkChanged();
		} );
		MarkChanged();
	}

	/// <summary>Opens the native file chooser over the shell window.</summary>
	private void UpdateBrowse()
	{
		if ( context.App.Window is { } window )
			_ = UpdateBrowseAsync( window );
	}

	/// <summary>Attaches the chosen files once the owner-modal chooser closes.</summary>
	private async Task UpdateBrowseAsync( Window window )
	{
		var result = await window.ShowFileDialogAsync( FileDialogKind.OpenFile, new FileDialogOptions
		{
			Title = "Attach files",
			Multiple = true,
		} );
		context.Post( () =>
		{
			if ( result.Status == FileDialogStatus.Success )
			{
				UpdateAttach( result.Paths );
			}
			else if ( result.Status != FileDialogStatus.Cancelled )
			{
				session.UpdateStatus( $"Could not open the file chooser: {result.Error}" );
			}
		} );
	}

	/// <summary>Attaches copied files or a copied image, and otherwise pastes clipboard text into the prompt.</summary>
	private void UpdatePaste()
	{
		if ( context.App.Window?.Clipboard is not { } clipboard )
			return;

		var formats = clipboard.GetFormats();
		if ( formats.HasFiles && clipboard.ReadFiles() is { Status: ClipboardReadStatus.Success } files )
		{
			UpdateAttach( files.Paths );
			return;
		}
		if ( formats.HasImage )
		{
			var image = clipboard.ReadImage();
			if ( image.Status == ClipboardReadStatus.Success )
			{
				UpdateAttach( [SaveClipboardImage( image )] );
			}
			else
			{
				session.UpdateStatus( $"Could not paste the image: {image.Error}" );
			}
			return;
		}

		var text = clipboard.ReadText();
		if ( text.Status == ClipboardReadStatus.Success && text.Text.Length > 0 )
			_composer.Execute( new TextCommand( TextCommandKind.Paste, text.Text.ReplaceLineEndings( "\n" ), false ) );
	}

	/// <summary>Writes a pasted image beside this conversation's other local data so Omp can read it by path.</summary>
	private string SaveClipboardImage( ClipboardImage image )
	{
		var extension = image.ContentType switch
		{
			"image/jpeg" => ".jpg",
			"image/gif" => ".gif",
			"image/webp" => ".webp",
			"image/bmp" => ".bmp",
			_ => ".png",
		};
		var directory = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ),
			"Lokn", "attachments", store.Get( conversationId ).Profile.ToString(), conversationId.ToString( "N" ) );
		Directory.CreateDirectory( directory );
		var path = Path.Combine( directory, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}" );
		File.WriteAllBytes( path, image.Bytes.Span );
		return path;
	}

	/// <summary>Accepts files dragged from outside the window.</summary>
	private static DragEffect QueryFileDrop( DragEvent item ) => item.Data.Value is NativeFileDrop ? DragEffect.Copy : DragEffect.None;

	/// <summary>Highlights the composer while files hover over it and attaches them on drop.</summary>
	private void UpdateFileDrop( DragEvent item )
	{
		var isOver = item.Kind is DragEventKind.Enter or DragEventKind.Move;
		if ( item.Kind == DragEventKind.Drop && item.Data.Value is NativeFileDrop { IsPreview: false } drop )
			UpdateAttach( drop.Paths );
		if ( isOver == _isFileDragOver )
			return;

		_isFileDragOver = isOver;
		MarkChanged();
	}

	/// <summary>Builds one removable chip per attachment above the prompt; a new chip rises in and a removed one fades out where it was.</summary>
	/// <remarks>The composer shows this row only while attachments remain, so removing the last one closes the row at once.</remarks>
	private Row BuildAttachments()
	{
		var chips = new List<Blob>();
		foreach ( var path in _attachments )
			chips.Add( BuildAttachmentChip( path, isLeaving: false ) );
		foreach ( var (path, index) in _detaching )
		{
			// A file attached again while its old chip leaves shows only its new chip.
			if ( !_attachments.Contains( path ) )
				chips.Insert( Math.Min( index, chips.Count ), BuildAttachmentChip( path, isLeaving: true ) );
		}
		return new Row
		{
			Key = "conversation-attachments",
			FlexWrap = FlexWrap.Wrap,
			Gap = Theme.Current.Spacing / 2,
			Children = chips,
		};
	}

	/// <summary>Builds one attachment's removable chip, which rises in when added, fades out when removed, and glides when a chip before it comes or goes.</summary>
	private Blob BuildAttachmentChip( string path, bool isLeaving )
	{
		var remove = Theme.Current.BuildButton( "×", () => context.Run( () => UpdateDetach( path ) ) );
		remove.AccessibilityName = $"Remove {Path.GetFileName( path )}";
		remove.Height = Theme.Current.ControlHeight - Theme.Current.Spacing;
		remove.Padding = new Edges( 0, Theme.Current.Spacing / 2 );
		remove.Color = Theme.Current.Muted;
		var chip = BuildFileChip( path, remove );
		// A chip mounts when its file is attached, so the entrance plays then and never again while it stays. The exit wraps the
		// entrance so they move different elements, and the wrapper, the row's child, is what glides when a chip before it comes or goes.
		var exit = Reveal.BuildExit( "chip:" + path, Cell.Mount<RevealInput, Reveal>( "chip-reveal", new( chip, true ) ), isLeaving );
		exit.LayoutTransition = new( ChatMotion.SlowMs, ChatMotion.Ease );
		return exit;
	}
}
