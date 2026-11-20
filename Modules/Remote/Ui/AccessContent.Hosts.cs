using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The Hosts section of the Remote access panel: every paired host with its state, name, scope and build, Reconnect and Remove, the switch
/// that lends this PC's browser to their agents, and the form that adds a host from its pairing link.</summary>
public sealed partial class AccessContent
{
	/// <summary>Stores whether the pairing link field takes focus when it first shows, as the <see cref="AddHostInput"/> input asks.</summary>
	private bool _isAddFocused;

	/// <summary>Stores the pairing link pasted in the Add host form.</summary>
	private string _hostLink = "";

	/// <summary>Stores the host the Remove dialog asks about, or null.</summary>
	private string? _removing;

	/// <summary>Holds the names being edited, by host id.</summary>
	private readonly Dictionary<string, string> _nameDrafts = new( StringComparer.Ordinal );

	/// <summary>Builds the Hosts section: the lend-browser switch, a card per paired host, the Add host form, and the Remove dialog while open.</summary>
	private IEnumerable<Blob> BuildHosts( Theme theme )
	{
		yield return BuildSection( "Hosts", theme, isPreview: true );
		yield return BuildParagraph( "Other PCs this one paired with. Their chats show in the sidebar's host switcher next to This PC's.", theme.Muted );
		var isLending = _context.App.Settings.Get( RemoteModule.LendBrowser, SettingsScope.Global );
		yield return BuildSwitch( RemoteModule.LendBrowser.Title, isLending, true,
			() => _context.App.Settings.Set( RemoteModule.LendBrowser, SettingsScope.Global, !isLending ) );

		var links = _module.Links;
		if ( links.Count == 0 )
			yield return BuildParagraph( "No host is paired yet.", theme.Muted );
		foreach ( var link in links )
			yield return BuildHost( link, theme );

		yield return BuildAddHost( theme );
		if ( _removing is { } removing && _module.FindLink( removing ) is { } removed )
			yield return BuildRemoveDialog( removed );
	}

	/// <summary>Builds one paired host's card: its dot and editable name, status line, scope and build, Reconnect and Remove.</summary>
	private Container BuildHost( RemoteClient link, Theme theme )
	{
		var hostId = link.Host.Id;
		var name = link.Host.Name;
		var draft = _nameDrafts.GetValueOrDefault( hostId ) ?? name;
		var isConnected = link.Status == ClientState.Connected;
		var scope = !isConnected ? "" : link.Scopes.Contains( DeviceScopes.Control ) ? "View and control" : "View only until its user allows control";
		var details = string.Join( " · ", new[] { scope, link.HostVersion is { } version ? "build " + version : "", "host id " + hostId }.Where( part => part.Length > 0 ) );
		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					BuildDot( RemoteStatusContent.ComputeColor( link.Status, theme ) ),
					BuildEntry( "host-name:" + hostId, "Name", draft, link.HostName.Length > 0 ? link.HostName : name, text => _nameDrafts[hostId] = text,
						text => UpdateName( hostId, text ) ),
				],
			},
			new Text { Content = link.StatusText, Selectable = true, TextWrap = TextWrap.Wrap, },
			BuildParagraph( details, theme.Muted, selectable: true ),
		};
		var buttons = new List<Blob>();
		if ( _nameDrafts.TryGetValue( hostId, out var edited ) && edited != name )
			buttons.Add( theme.BuildButton( "Save name", () => _context.Run( () => UpdateName( hostId, edited ) ) ) );
		buttons.Add( theme.BuildButton( "Reconnect", () => _context.Run( () => _module.ReconnectHost( hostId ) ) ) );
		buttons.Add( theme.BuildButton( "Remove\u2026", () => _context.Run( () =>
		{
			_removing = hostId;
			_version.Value++;
		} ) ) );
		children.Add( new Row { Gap = theme.Spacing, Children = buttons } );
		return new Container
		{
			Key = "host:" + hostId,
			Padding = theme.Spacing,
			Gap = theme.Spacing / 2,
			BorderRadius = theme.Radius,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			Children = children,
		};
	}

	/// <summary>Builds the Add host form: paste a host's pairing link and add it; the field takes focus once when the panel opened to add a host.</summary>
	private Container BuildAddHost( Theme theme )
	{
		var isFocused = _isAddFocused;
		_isAddFocused = false;
		var entry = BuildEntry( "add-host", "Link", _hostLink, "loom://pair#…", text => _hostLink = text, _ => UpdateAddHost(), autoFocus: isFocused );
		return new Container
		{
			Gap = theme.Spacing,
			Children =
			[
				BuildSection( "Add host", theme, isPreview: true ),
				BuildParagraph( "On the other PC, open Remote access, choose Pair device, and paste the link it shows here. It shows here at once, with view only "
					+ "until its user allows control.", theme.Muted ),
				entry,
				theme.BuildButton( _isBusy ? "Adding\u2026" : "Add", UpdateAddHost, isEnabled: !_isBusy && _hostLink.Trim().Length > 0 ),
			],
		};
	}

	/// <summary>Builds the dialog that asks before a host is removed from this Loom.</summary>
	private Blob BuildRemoveDialog( RemoteClient link )
	{
		var name = link.Host.Name;
		var hostId = link.Host.Id;
		return Cell.Mount<ConfirmDialogInput, ConfirmDialog>( "remove-host", new ConfirmDialogInput( _context.App, $"Remove {name}?",
			$"Its chats and panels close in this Loom. {name} keeps this device until you revoke it there.", "Remove",
			() => _context.Run( () =>
			{
				_removing = null;
				_nameDrafts.Remove( hostId );
				_module.RemoveHost( hostId );
				ShowMessage( $"{name} is removed from this Loom.", false );
			} ),
			() => _context.Run( () =>
			{
				_removing = null;
				_version.Value++;
			} ) ) );
	}

	/// <summary>Pairs with the host whose link was pasted and adds it, showing what failed, such as a used or expired link.</summary>
	private void UpdateAddHost()
	{
		var text = _hostLink;
		RunRequest( async () =>
		{
			var host = await _module.AddHostAsync( text, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				_hostLink = "";
				var name = _context.App.Hosts.Find( host.HostId )?.Name ?? "the host";
				// A session of this PC stays on this PC; the new host is a switch away.
				ShowMessage( _context.App.Hosts.Current.Id == host.HostId ? $"Paired with {name}. Its chats show in the sidebar."
					: $"Paired with {name}. Switch to it in the host switcher above the sidebar to see its chats.", false );
			} );
		} );
	}

	/// <summary>Saves a host's name, or shows why it could not be saved.</summary>
	private void UpdateName( string hostId, string name )
	{
		try
		{
			_module.RenameHost( hostId, name );
			_nameDrafts.Remove( hostId );
			_version.Value++;
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or InvalidDataException )
		{
			ShowMessage( exception.Message, true );
		}
	}
}
