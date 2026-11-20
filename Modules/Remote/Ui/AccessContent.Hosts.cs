using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The PCs part of Remote access: every PC this one paired with, with its state, Reconnect and Remove, and the field that adds one from its
/// pairing link.</summary>
internal sealed partial class AccessContent
{
	/// <summary>Stores whether the pairing link field takes focus when it next shows, as an <see cref="AddHostRequest"/> asks.</summary>
	private bool _isAddFocused;

	/// <summary>Stores the pairing link pasted in the Add host form.</summary>
	private string _hostLink = "";

	/// <summary>Stores the host the Remove dialog asks about, or null.</summary>
	private string? _removing;

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

	/// <summary>Builds the Add field: paste another PC's pairing link and add it; the field takes focus once when the panel opened to add a host.</summary>
	private Row BuildAddHost( Theme theme )
	{
		var isFocused = _isAddFocused;
		_isAddFocused = false;
		var entry = BuildEntry( "add-host", "Add a PC", _hostLink, "Paste the link from Pair on the other PC", text => _hostLink = text,
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
}
