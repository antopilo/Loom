using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Lokn.Core;
using Microsoft.Win32;

namespace Lokn.Modules.Notifications;

/// <summary>Registers what Windows requires before an unpackaged app may show notifications.</summary>
/// <remarks>
/// Windows only shows toasts for an app ID that a Start menu shortcut declares, and keeps them in the notification center
/// only when that shortcut also names a toast activator. A clicked toast opens a per-user protocol that relaunches this
/// executable with Goo's notification arguments. Both are written for the running executable, so they follow the build the
/// user last ran. A bare instance never writes them, since they belong to the user's own Loom, and shows no Windows notifications.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal static class NotificationIdentity
{
	/// <summary>Defines the private protocol a clicked notification launches.</summary>
	internal const string Scheme = "lokn-notification";

	/// <summary>Defines the Start menu shortcut name.</summary>
	private const string ShortcutName = "Loom.lnk";

	/// <summary>Defines the property ID of System.AppUserModel.ID.</summary>
	private const uint AppUserModelIdProperty = 5;

	/// <summary>Defines the property ID of System.AppUserModel.ToastActivatorCLSID.</summary>
	private const uint ToastActivatorProperty = 26;

	/// <summary>Defines the PROPVARIANT type of a UTF-16 string pointer.</summary>
	private const ushort StringVariant = 31;

	/// <summary>Defines the PROPVARIANT type of a CLSID pointer.</summary>
	private const ushort ClsidVariant = 72;

	/// <summary>Identifies the System.AppUserModel property set.</summary>
	private static readonly Guid _appUserModelProperties = new( "9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3" );

	/// <summary>Identifies Lokn's toast activator; clicks arrive through the protocol, so no COM server stands behind it.</summary>
	private static readonly Guid _toastActivator = new( "2f283938-de6c-43fa-895d-668f4e0de39f" );

	/// <summary>Gets whether this process may write the shortcut and protocol: never in a bare instance, whose data folder is custom.</summary>
	internal static bool CanInstall => !LoknPaths.IsCustom;

	/// <summary>Writes the Start menu shortcut and notification protocol for the running executable, unless they are current or
	/// <see cref="CanInstall"/> is false.</summary>
	internal static void Install( string identifier )
	{
		if ( !CanInstall )
			return;

		var executable = Environment.ProcessPath ?? throw new InvalidOperationException( "The Lokn executable path is unknown." );
		// Goo reads a toast click from exactly these arguments and forwards it to the running instance.
		var command = $"\"{executable}\" --goo-notification \"%1\"";
		var shortcut = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Programs ), ShortcutName );
		using var protocol = Registry.CurrentUser.CreateSubKey( $@"Software\Classes\{Scheme}" );
		using var open = protocol.CreateSubKey( @"shell\open\command" );
		// The command is written last, so a matching command means this executable's shortcut was saved too.
		if ( open.GetValue( "" ) as string == command && File.Exists( shortcut ) )
			return;

		SaveShortcut( shortcut, executable, identifier );
		protocol.SetValue( "", "URL:Lokn notification" );
		protocol.SetValue( "URL Protocol", "" );
		open.SetValue( "", command );
	}

	/// <summary>Saves a shortcut to the executable that declares Lokn's app ID and toast activator.</summary>
	private static void SaveShortcut( string path, string executable, string identifier )
	{
		var link = (IShellLinkW)new ShellLink();
		try
		{
			link.SetPath( executable );
			link.SetWorkingDirectory( Path.GetDirectoryName( executable )! );
			link.SetDescription( "Loom" );
			var properties = (IPropertyStore)link;
			var name = Marshal.StringToCoTaskMemUni( identifier );
			var activator = Marshal.AllocCoTaskMem( Marshal.SizeOf<Guid>() );
			try
			{
				Marshal.StructureToPtr( _toastActivator, activator, false );
				SetProperty( properties, AppUserModelIdProperty, StringVariant, name );
				SetProperty( properties, ToastActivatorProperty, ClsidVariant, activator );
				properties.Commit();
			}
			finally
			{
				Marshal.FreeCoTaskMem( name );
				Marshal.FreeCoTaskMem( activator );
			}

			((IPersistFile)link).Save( path, true );
		}
		finally
		{
			Marshal.FinalReleaseComObject( link );
		}
	}

	/// <summary>Sets one System.AppUserModel property; the store copies the pointed-to value.</summary>
	private static void SetProperty( IPropertyStore properties, uint id, ushort type, nint value )
	{
		var key = new PropertyKey( _appUserModelProperties, id );
		var variant = new PropVariant
		{
			Type = type,
			Value = value,
		};
		properties.SetValue( ref key, ref variant );
	}

	/// <summary>Creates the shell's shortcut object.</summary>
	[ComImport]
	[Guid( "00021401-0000-0000-c000-000000000046" )]
	// Not sealed: the runtime casts a COM coclass to interfaces it does not declare.
	private class ShellLink;

	/// <summary>Edits a shell shortcut; members are declared in vtable order, and only the setters are used.</summary>
	[ComImport]
	[Guid( "000214f9-0000-0000-c000-000000000046" )]
	[InterfaceType( ComInterfaceType.InterfaceIsIUnknown )]
	private interface IShellLinkW
	{
		/// <summary>Reads the target path.</summary>
		void GetPath( nint file, int length, nint data, uint flags );

		/// <summary>Reads the target item ID list.</summary>
		void GetIDList( out nint list );

		/// <summary>Sets the target item ID list.</summary>
		void SetIDList( nint list );

		/// <summary>Reads the description.</summary>
		void GetDescription( nint name, int length );

		/// <summary>Sets the description.</summary>
		void SetDescription( [MarshalAs( UnmanagedType.LPWStr )] string name );

		/// <summary>Reads the working directory.</summary>
		void GetWorkingDirectory( nint directory, int length );

		/// <summary>Sets the working directory.</summary>
		void SetWorkingDirectory( [MarshalAs( UnmanagedType.LPWStr )] string directory );

		/// <summary>Reads the command-line arguments.</summary>
		void GetArguments( nint arguments, int length );

		/// <summary>Sets the command-line arguments.</summary>
		void SetArguments( [MarshalAs( UnmanagedType.LPWStr )] string arguments );

		/// <summary>Reads the hotkey.</summary>
		void GetHotkey( out short hotkey );

		/// <summary>Sets the hotkey.</summary>
		void SetHotkey( short hotkey );

		/// <summary>Reads the show command.</summary>
		void GetShowCmd( out int command );

		/// <summary>Sets the show command.</summary>
		void SetShowCmd( int command );

		/// <summary>Reads the icon location.</summary>
		void GetIconLocation( nint path, int length, out int index );

		/// <summary>Sets the icon location.</summary>
		void SetIconLocation( [MarshalAs( UnmanagedType.LPWStr )] string path, int index );

		/// <summary>Sets the relative target path.</summary>
		void SetRelativePath( [MarshalAs( UnmanagedType.LPWStr )] string path, uint reserved );

		/// <summary>Resolves a moved target.</summary>
		void Resolve( nint window, uint flags );

		/// <summary>Sets the target path.</summary>
		void SetPath( [MarshalAs( UnmanagedType.LPWStr )] string file );
	}

	/// <summary>Edits shell item properties; members are declared in vtable order.</summary>
	[ComImport]
	[Guid( "886d8eeb-8cf2-4446-8d02-cdba1dbdcf99" )]
	[InterfaceType( ComInterfaceType.InterfaceIsIUnknown )]
	private interface IPropertyStore
	{
		/// <summary>Counts the stored properties.</summary>
		void GetCount( out uint count );

		/// <summary>Reads a property key by index.</summary>
		void GetAt( uint index, out PropertyKey key );

		/// <summary>Reads a property value.</summary>
		void GetValue( ref PropertyKey key, out PropVariant value );

		/// <summary>Writes a property value.</summary>
		void SetValue( ref PropertyKey key, ref PropVariant value );

		/// <summary>Saves the written values.</summary>
		void Commit();
	}

	/// <summary>Identifies one shell property.</summary>
	/// <param name="Format">The property set.</param>
	/// <param name="Id">The property within the set.</param>
	[StructLayout( LayoutKind.Sequential )]
	private readonly record struct PropertyKey( Guid Format, uint Id );

	/// <summary>Holds a pointer-valued PROPVARIANT: the type, three reserved words, then a 16-byte value union.</summary>
	[StructLayout( LayoutKind.Explicit, Size = 24 )]
	private struct PropVariant
	{
		/// <summary>Gets or sets the variant type.</summary>
		[FieldOffset( 0 )]
		public ushort Type;

		/// <summary>Gets or sets the pointer value.</summary>
		[FieldOffset( 8 )]
		public nint Value;
	}
}
