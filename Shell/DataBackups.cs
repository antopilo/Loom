using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Lists the data backups Loom made before an upgrade in Settings and restores one: Loom closes, stops the daemon, copies the backup back
/// before anything reads the data, and starts again (docs/architecture.md, Data and upgrades).</summary>
internal static class DataBackups
{
	/// <summary>Gets or sets the data folder this window's backups are in: the user's, or the bare folder.</summary>
	internal static string DataFolder { get; set; } = LoknPaths.DataRoot;

	/// <summary>Defines the Settings entry that lists the backups; its value is never set, only its editor is used.</summary>
	internal static readonly Setting<string> Backups = new(
		"shell.backups",
		"Backups",
		"Before a new version of Loom first changes your chats, projects, workspaces and settings, it copies them here. It keeps the three newest.",
		defaultValue: "",
		category: "Data" )
	{
		Editor = Build,
	};

	/// <summary>Builds the list of backups, each with its Restore button.</summary>
	private static Blob Build( LoknApplication app, SettingsScope scope ) => Cell.Mount<LoknApplication, BackupsView>( "backups", app );
}

/// <summary>The Settings list of backups, newest first, and the dialog that confirms a restore.</summary>
internal sealed class BackupsView : GuardedCell<LoknApplication>
{
	/// <summary>Holds the backup whose restore is being confirmed, or empty.</summary>
	private readonly State<string> _confirming = new( "" );

	/// <summary>Holds why the last restore request failed, or empty.</summary>
	private readonly State<string> _error = new( "" );

	/// <summary>Stores the backups, read from the disk the first time the list shows.</summary>
	private IReadOnlyList<DataBackup>? _backups;

	/// <summary>Builds one row per backup with its version and time, or a line saying there are none, and the confirm dialog while one is open.</summary>
	protected override Blob BuildView( LoknApplication app )
	{
		var theme = Theme.Current;
		var backups = _backups ??= DataUpgrade.ListBackups( DataBackups.DataFolder );
		var children = new List<Blob>();
		if ( backups.Count == 0 )
			children.Add( new Text { Key = "none", Content = "No backups yet. Loom makes one the first time a newer version starts.", Color = theme.Muted, TextWrap = TextWrap.Wrap } );
		foreach ( var backup in backups )
		{
			var folder = backup.Folder;
			children.Add( new Row
			{
				Key = "backup:" + Path.GetFileName( folder ),
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					new Text
					{
						Content = $"{DescribeVersion( backup.Version )} · {backup.Created.ToString( "g", CultureInfo.CurrentCulture )}",
						FlexGrow = 1,
						FlexShrink = 1,
						MinWidth = 0,
						TextWrap = TextWrap.Wrap,
					},
					theme.BuildButton( "Restore\u2026", () => _confirming.Value = folder ),
				],
			} );
		}
		if ( _error.Value.Length > 0 )
			children.Add( new Text { Key = "error", Content = _error.Value, Color = theme.Error, Selectable = true, TextWrap = TextWrap.Wrap } );
		if ( backups.FirstOrDefault( backup => backup.Folder == _confirming.Value ) is { } chosen )
			children.Add( BuildConfirm( app, chosen ) );

		return new Container { Gap = theme.Spacing, Children = children };
	}

	/// <summary>Builds the dialog that asks before Loom restarts to restore a backup.</summary>
	private Blob BuildConfirm( LoknApplication app, DataBackup backup )
	{
		var when = backup.Created.ToString( "g", CultureInfo.CurrentCulture );
		return Cell.Mount<ConfirmDialogInput, ConfirmDialog>( "restore", new ConfirmDialogInput( app, $"Restore the backup from {when}?",
			"Loom closes, stops the daemon on this PC if it runs, puts back the chats, projects, workspaces and settings of "
			+ $"{DescribeVersion( backup.Version )}, and starts again. Changes made since then are replaced.", "Restart and restore",
			() =>
			{
				_confirming.Value = "";
				try
				{
					DataUpgrade.RequestRestore( DataBackups.DataFolder, backup.Folder );
					_error.Value = "";
					ShellRestart.Request( app );
				}
				catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
				{
					_error.Value = $"Loom could not ask for the restore: {exception.Message}";
				}
			},
			() => _confirming.Value = "" ) );
	}

	/// <summary>Names a backup's version for the user: <c>Loom 0.2.0</c>, or a Loom before 0.3.0 for data without <c>version.json</c>.</summary>
	private static string DescribeVersion( string version ) => version == DataUpgrade.EarlierVersion ? "Loom before 0.3.0" : $"Loom {version}";
}
