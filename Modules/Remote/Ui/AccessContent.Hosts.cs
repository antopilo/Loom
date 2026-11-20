using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The Connected part of Remote access: This PC, every host this one paired with, with its state, Reconnect and Remove, and Add a host,
/// which opens the field that adds one from its pairing link.</summary>
internal sealed partial class AccessContent
{
	/// <summary>Stores whether the pairing link field takes focus when it next shows, as an <see cref="AddHostRequest"/> asks.</summary>
	private bool _isAddFocused;

	/// <summary>Stores whether the pairing link field shows, after Add remote host or an <see cref="AddHostRequest"/>.</summary>
	private bool _isAdding;

	/// <summary>Stores the pairing link pasted in the Add host form.</summary>
	private string _hostLink = "";

	/// <summary>Stores the host the Remove dialog asks about, or null.</summary>
	private string? _removing;

	/// <summary>Builds Connected: This PC, a row per paired host and Add a host in one card, and the Remove dialog while open.</summary>
	private IEnumerable<Blob> BuildHosts( Theme theme )
	{
		yield return BuildSection( "Connected", theme );
		var rows = new List<Blob> { BuildThisPc( theme ) };
		rows.AddRange( _module.Links.Select( link => (Blob)BuildHost( link, theme ) ) );
		rows.Add( BuildAddHost( theme ) );
		yield return BuildGroup( theme, rows );
		if ( _removing is { } removing && _module.FindLink( removing ) is { } removed )
			yield return BuildRemoveDialog( removed );
	}

	/// <summary>Builds This PC's row: its name with a This PC tag, and where its chats run.</summary>
	private Row BuildThisPc( Theme theme ) => BuildGroupRow( "host:local", theme,
		BuildDot( theme.Accent ),
		new Container
		{
			FlexGrow = 1,
			FlexShrink = 1,
			Children =
			[
				new Row
				{
					Gap = theme.Spacing,
					AlignItems = AlignItems.Center,
					Children =
					[
						new Text { Content = _context.App.Hosts.Local.Name, FontWeight = 600 },
						new Text { Content = "This PC", FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted },
					],
				},
				new Text
				{
					Content = _module.RunningRole == RemoteModule.DaemonRole ? "Chats run in the daemon on this PC" : "Chats run in this Loom",
					FontSize = ViewStyle.SmallSize,
					Color = theme.Muted,
				},
			],
		} );

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
		return BuildGroupRow( "host:" + hostId, theme, [.. children] );
	}

	/// <summary>Builds Add a host: what it needs and Add remote host, or once chosen the pairing link field, which takes focus once.</summary>
	private Blob BuildAddHost( Theme theme )
	{
		if ( !_isAdding )
		{
			return BuildSettingRow( "add-host", "Add a host", "Choose Pair a device on the other PC, then paste the link it shows here.",
				theme.BuildButton( "+ Add remote host\u2026", () => _context.Run( () =>
				{
					_isAdding = true;
					_isAddFocused = true;
					_version.Value++;
				} ) ), theme );
		}

		var isFocused = _isAddFocused;
		_isAddFocused = false;
		var entry = BuildEntry( "add-host", "Pairing link", _hostLink, "Paste the link from Pair a device on the other PC", text => _hostLink = text,
			_ => UpdateAddHost(), autoFocus: isFocused );
		return BuildGroupRow( "add-host", theme,
			entry,
			theme.BuildButton( _isBusy ? "Adding\u2026" : "Add", UpdateAddHost, isEnabled: !_isBusy && _hostLink.Trim().Length > 0 ),
			theme.BuildButton( "Cancel", () => _context.Run( () =>
			{
				_isAdding = false;
				_hostLink = "";
				_version.Value++;
			} ) ) );
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
				_isAdding = false;
				var name = _context.App.Hosts.Find( host.HostId )?.Name ?? "the host";
				// A session of this PC stays on this PC; the new host is a switch away.
				ShowMessage( _context.App.Hosts.Current.Id == host.HostId ? $"Paired with {name}. Its chats show in the sidebar."
					: $"Paired with {name}. Switch to it in the host switcher above the sidebar to see its chats.", false );
			} );
		} );
	}
}
