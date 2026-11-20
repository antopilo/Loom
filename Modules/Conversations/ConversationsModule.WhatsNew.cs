using Goo;
using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Home's one-time What's new card after an update to 0.3.0: what the update brings, in plain words, with a button that turns the daemon on.</summary>
/// <remarks>The card shows while the data folder's <c>version.json</c> says it was upgraded from a version older than <see cref="WhatsNewVersion"/>
/// (<see cref="DataUpgrade.ReadUpgradedFrom"/>); either button forgets that, so the card never shows again. A fresh install never shows it.</remarks>
public sealed partial class ConversationsModule
{
	/// <summary>Defines the version the What's new card describes.</summary>
	internal const string WhatsNewVersion = "0.3.0";

	/// <summary>Holds whether Home shows the What's new card.</summary>
	private readonly State<bool> _isWhatsNewShown = new( false );

	/// <summary>Stores the data folder whose <c>version.json</c> records the upgrade, read when the module starts.</summary>
	private string _dataFolder = LoknPaths.DataRoot;

	/// <summary>Gets whether Home shows the What's new card; reading it rebuilds the reader when it is dismissed.</summary>
	internal bool IsWhatsNewShown => _isWhatsNewShown.Value;

	/// <summary>Gets whether the card offers to turn the daemon on: the Remote module, which runs it, is loaded.</summary>
	internal bool CanRunInBackground => _context?.App.Modules.Any( module => module.Id == RemoteModuleId ) == true;

	/// <summary>Defines the Remote module's identifier, whose <c>role</c> link turns the daemon on.</summary>
	private const string RemoteModuleId = "remote";

	/// <summary>Reads whether this data folder was upgraded from a version older than <see cref="WhatsNewVersion"/> and the card was not dismissed.</summary>
	private void LoadWhatsNew( LoknApplication app )
	{
		_dataFolder = LoknPaths.DataRoot;
		_isWhatsNewShown.Value = !app.IsHost && DataUpgrade.IsOlder( DataUpgrade.ReadUpgradedFrom( _dataFolder ), WhatsNewVersion );
	}

	/// <summary>Hides the card for good: <b>Not now</b>, and after <b>Turn on</b>.</summary>
	internal void DismissWhatsNew()
	{
		_isWhatsNewShown.Value = false;
		DataUpgrade.DismissUpgradedFrom( _dataFolder );
	}

	/// <summary>Runs This PC's chats in the daemon from now on, through the Remote module's <c>role</c> link, and hides the card for good.</summary>
	internal void TurnOnBackground( LoknApplication app )
	{
		DismissWhatsNew();
		app.OpenLink( LoknLinks.Create( RemoteModuleId, "role", "daemon" ) );
	}
}
