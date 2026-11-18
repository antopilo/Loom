using Goo;
using Lokn.Core;

namespace Lokn.Modules.Editor;

/// <summary>A plain text editor: opens a file in a panel, edits it, and saves it.</summary>
/// <remarks>It uses only the public API of Lokn.Core and Goo and depends on no other module.</remarks>
public sealed class EditorModule : ILoknModule
{
	/// <summary>Defines the editor's panel type.</summary>
	internal const string PanelType = "editor.file";

	/// <summary>Defines the editor text size.</summary>
	internal static readonly Setting<int> FontSize = new(
		"editor.font-size",
		"Font size",
		"Text size in the editor.",
		defaultValue: 13,
		category: "Editor",
		isValid: value => value is >= 8 and <= 32 ) { Unit = "px", Minimum = 8, Maximum = 32 };

	/// <summary>Defines whether line numbers show.</summary>
	internal static readonly Setting<bool> LineNumbers = new(
		"editor.line-numbers",
		"Line numbers",
		"Show line numbers beside the text.",
		defaultValue: true,
		category: "Editor" );

	/// <summary>Defines how many spaces a tab is wide.</summary>
	internal static readonly Setting<int> TabWidth = new(
		"editor.tab-width",
		"Tab width",
		"How many spaces a tab takes.",
		defaultValue: 4,
		category: "Editor",
		isValid: value => value is >= 1 and <= 8 ) { Unit = "spaces", Minimum = 1, Maximum = 8 };

	/// <summary>Gets the module identifier.</summary>
	public string Id => "editor";

	/// <summary>Gets the display name.</summary>
	public string Title => "Editor";

	/// <summary>Defines whether long lines wrap to the panel's width.</summary>
	internal static readonly Setting<bool> WrapLines = new(
		"editor.wrap-lines",
		"Wrap lines",
		"Wrap long lines to the panel's width.",
		defaultValue: false,
		category: "Editor" );

	/// <summary>Stores the open editor panels, the most recently used last.</summary>
	private readonly List<EditorContent> _editors = [];

	/// <summary>Registers the settings, the editor panel, the open command and the route that opens a file.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( FontSize )
			.Setting( LineNumbers )
			.Setting( TabWidth )
			.Setting( WrapLines )
			// Modules such as Git mark changed lines in the gutter; the editor asks them when a file opens or is saved.
			.ExtensionPoint<LineMarkSource>( "marks" )
			.Panel( "file", "Editor", context => new EditorContent( context, this ), "\uE8A5" )
			.CommandAsync( "open-file", "Editor: open file", context => OpenFile( context.App ), new( Key.O, KeyModifiers.Primary ) )
			// lokn://editor/open?path=C%3A%5Cnotes.txt opens a file, so a file tree, chats and other modules can link to it.
			.Route( "open", request =>
			{
				if ( request.Query( "path" ) is { Length: > 0 } path )
					Open( request.App, Path.GetFullPath( path ), request.SourcePanelId );
			} );
	}

	/// <summary>Opens a file where the user expects it: its editor when one shows it already; else the last used editor on screen, which swaps
	/// to it when it has no unsaved changes; else a new editor beside that one, or beside the panel the link came from.</summary>
	internal void Open( LoknApplication app, string path, Guid? sourcePanelId )
	{
		if ( _editors.LastOrDefault( editor => editor.Shows( path ) && app.Panels.IsShown( editor.Id ) ) is { } showing )
		{
			app.Panels.Activate( showing.Id );
			return;
		}

		if ( _editors.LastOrDefault( editor => app.Panels.IsShown( editor.Id ) ) is { } last )
		{
			if ( last.IsDirty )
				app.Panels.Activate( app.Panels.OpenBeside( PanelType, last.Id, path ) );
			else
			{
				last.Navigate( path );
				app.Panels.Activate( last.Id );
			}
			return;
		}

		app.Panels.Activate( sourcePanelId is { } source ? app.Panels.OpenBeside( PanelType, source, path ) : app.Panels.Open( PanelType, input: path ) );
	}

	/// <summary>Records an editor as the most recently used.</summary>
	internal void MarkUsed( EditorContent editor )
	{
		_editors.Remove( editor );
		_editors.Add( editor );
	}

	/// <summary>Forgets a closed editor.</summary>
	internal void Forget( EditorContent editor ) => _editors.Remove( editor );

	/// <summary>Asks for a file and opens it in a new editor panel.</summary>
	private static async Task OpenFile( LoknApplication app )
	{
		if ( app.Window is not { } window )
			return;

		var result = await window.ShowFileDialogAsync( FileDialogKind.OpenFile, new FileDialogOptions { Title = "Open file" } );
		if ( result.Status == FileDialogStatus.Success && result.Paths is [var path, ..] )
			app.Panels.Open( PanelType, input: path );
		else if ( result.Status != FileDialogStatus.Cancelled )
			app.ReportError( "Editor", new IOException( $"Could not open the file chooser: {result.Error}" ) );
	}
}
