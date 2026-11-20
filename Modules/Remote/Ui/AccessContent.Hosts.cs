using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The hosts part of Remote access: every paired host with its state, Reconnect and Remove, and the field that adds one from its pairing link;
/// in Advanced, their names.</summary>
internal sealed partial class AccessContent
{
	/// <summary>Stores whether the pairing link field takes focus when it next shows, as an <see cref="AddHostRequest"/> asks.</summary>
	private bool _isAddFocused;

	/// <summary>Stores the pairing link pasted in the Add host form.</summary>
	private string _hostLink = "";

	/// <summary>Stores the host the Remove dialog asks about, or null.</summary>
	private string? _removing;

	/// <summary>Holds the names being edited, by host id.</summary>
	private readonly Dictionary<string, string> _nameDrafts = new( StringComparer.Ordinal );

	/// <summary>Builds the hosts part: a row per paired host, the Add field, and the Remove dialog while open.</summary>
	private IEnumerable<Blob> BuildHosts( Theme theme )
	{
		yield return BuildSection( "PCs you connect to", theme );
		foreach ( var link in _module.Links )
			yield return BuildHost( link, theme );

		yield return BuildAddHost( theme );
		if ( _removing is { } removing && _module.FindLink( removing ) is { } removed )
			yield return BuildRemoveDialog( removed );
	}

	/// <summary>Builds one paired host's row: its dot, name and status, Reconnect while it is not connected, and Remove.</summary>
	private Row BuildHost( RemoteClient link, Theme theme )
	{
		var hostId = link.Host.Id;
		var children = new List<Blob>
		{
			BuildDot( RemoteStatusContent.ComputeColor( link.Status, theme ) ),
			new Container
			{
				FlexGrow = 1,
				FlexShrink = 1,
				Children =
				[
					new Text { Content = link.Host.Name, },
					BuildParagraph( link.StatusText, theme.Muted, selectable: true ),
				],
			},
		};
		if ( link.Status != ClientState.Connected )
			children.Add( theme.BuildButton( "Reconnect", () => _context.Run( () => _module.ReconnectHost( hostId ) ) ) );
		children.Add( theme.BuildButton( "Remove\u2026", () => _context.Run( () =>
		{
			_removing = hostId;
			_version.Value++;
		} ) ) );
		return new Row
		{
			Key = "host:" + hostId,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Children = children,
		};
	}

	/// <summary>Builds the hosts part of Advanced: each paired host's name, what it allows, build and id.</summary>
	private IEnumerable<Blob> BuildHostSettings( Theme theme )
	{
		if ( _module.Links is not { Count: > 0 } links )
			yield break;

		yield return BuildSection( "PCs you connect to", theme );
		foreach ( var link in links )
		{
			var hostId = link.Host.Id;
			var name = link.Host.Name;
			var draft = _nameDrafts.GetValueOrDefault( hostId ) ?? name;
			var scope = link.Status != ClientState.Connected ? "" : link.Scopes.Contains( DeviceScopes.Control ) ? "View and control" : "View only until its user allows control";
			var details = string.Join( " · ", new[] { scope, link.HostVersion is { } version ? "build " + version : "", "host id " + hostId }.Where( part => part.Length > 0 ) );
			yield return BuildEntry( "host-name:" + hostId, "Name", draft, link.HostName.Length > 0 ? link.HostName : name, text => _nameDrafts[hostId] = text,
				text => UpdateName( hostId, text ) );
			if ( _nameDrafts.TryGetValue( hostId, out var edited ) && edited != name )
				yield return theme.BuildButton( "Save name", () => _context.Run( () => UpdateName( hostId, edited ) ) );
			yield return BuildParagraph( details, theme.Muted, selectable: true );
		}
	}

	/// <summary>Builds the Add field: paste another PC's pairing link and add it; the field takes focus once when the panel opened to add a host.</summary>
	private Row BuildAddHost( Theme theme )
	{
		var isFocused = _isAddFocused;
		_isAddFocused = false;
		var entry = BuildEntry( "add-host", "Add a PC", _hostLink, "Paste the link from Connect a device on the other PC", text => _hostLink = text,
			_ => UpdateAddHost(), autoFocus: isFocused );
		return new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Children =
			[
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
