using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

namespace Lokn.Modules.Notifications;

/// <summary>Shows and removes silent Windows toasts through the WinRT notification API.</summary>
/// <remarks>
/// Goo's notification service cannot silence a toast, and Windows would play its own sound over the attention chime.
/// Clicks use Goo's notification activation URI, so <see cref="Goo.Application.Activated"/> still delivers them.
/// Interface slots count the six IInspectable methods first.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal static class WindowsToast
{
	/// <summary>Defines the HRESULT returned when the thread already uses another COM apartment, which WinRT still serves.</summary>
	private const int ChangedMode = unchecked((int)0x80010106);

	/// <summary>Defines the HRESULT Windows returns for an app's notification setting before its first toast creates the entry.</summary>
	private const int ElementNotFound = unchecked((int)0x80070490);

	/// <summary>Defines RoInitialize's multithreaded apartment.</summary>
	private const uint Multithreaded = 1;

	/// <summary>Defines the group every Lokn toast belongs to.</summary>
	private const string Group = "attention";

	/// <summary>Identifies IToastNotificationManagerStatics.</summary>
	private static readonly Guid _managerStatics = new( "50ac103f-d235-4598-bbef-98fe4d1a3ad4" );

	/// <summary>Identifies IToastNotificationManagerStatics2.</summary>
	private static readonly Guid _managerHistoryStatics = new( "7ab93c52-0e48-4750-ba9d-1a4113981847" );

	/// <summary>Identifies IToastNotificationFactory.</summary>
	private static readonly Guid _toastFactory = new( "04124b20-82c6-4229-b109-fd9ed4662b53" );

	/// <summary>Identifies IToastNotification2, which carries the replacement tag.</summary>
	private static readonly Guid _toastTag = new( "9dfb9fd1-143a-490e-90bf-b9fba7132de7" );

	/// <summary>Identifies IXmlDocument.</summary>
	private static readonly Guid _xmlDocument = new( "f7f3a506-1e87-42d6-bcfb-b8c809fa5494" );

	/// <summary>Identifies IXmlDocumentIO.</summary>
	private static readonly Guid _xmlDocumentIo = new( "6cd0e74e-ee65-4489-9ebf-ca43e87ba637" );

	/// <summary>Builds silent toast XML whose click opens Goo's notification activation URI for this app's protocol.</summary>
	internal static string BuildContent( string scheme, string tag, string title, string body, string payload )
	{
		var launch = $"{scheme}://goo-notification/activate?id={Uri.EscapeDataString( tag )}&action=&data={Uri.EscapeDataString( payload )}";
		return $"<toast activationType=\"protocol\" launch=\"{SecurityElement.Escape( launch )}\"><visual><binding template=\"ToastGeneric\">"
			+ $"<text>{SecurityElement.Escape( title )}</text><text>{SecurityElement.Escape( body )}</text>"
			+ "</binding></visual><audio silent=\"true\"/></toast>";
	}

	/// <summary>Shows or replaces the toast with this tag, unless the user turned Lokn's notifications off in Windows.</summary>
	internal static void Show( string application, string tag, string content ) => Run( () =>
	{
		var manager = GetFactory( "Windows.UI.Notifications.ToastNotificationManager", _managerStatics );
		try
		{
			using var id = new HString( application );
			Check( Slot<Create>( manager, 7 )( manager, id.Handle, out var notifier ) );
			try
			{
				// Any setting other than Enabled means the user or policy disabled Lokn's notifications.
				var read = Slot<Read>( notifier, 8 )( notifier, out var setting );
				if ( read != ElementNotFound )
					Check( read );
				if ( read >= 0 && setting != 0 )
					return;

				var toast = BuildToast( tag, content );
				try
				{
					Check( Slot<Call>( notifier, 6 )( notifier, toast ) );
				}
				finally
				{
					Marshal.Release( toast );
				}
			}
			finally
			{
				Marshal.Release( notifier );
			}
		}
		finally
		{
			Marshal.Release( manager );
		}
	} );

	/// <summary>Removes the toast with this tag from the notification center.</summary>
	internal static void Remove( string application, string tag ) => Run( () =>
	{
		var manager = GetFactory( "Windows.UI.Notifications.ToastNotificationManager", _managerHistoryStatics );
		try
		{
			Check( Slot<Read>( manager, 6 )( manager, out var history ) );
			try
			{
				using var id = new HString( application );
				using var name = new HString( tag );
				using var group = new HString( Group );
				Check( Slot<RemoveTag>( history, 8 )( history, name.Handle, group.Handle, id.Handle ) );
			}
			finally
			{
				Marshal.Release( history );
			}
		}
		finally
		{
			Marshal.Release( manager );
		}
	} );

	/// <summary>Creates a tagged toast from its XML content.</summary>
	private static nint BuildToast( string tag, string content )
	{
		var document = Activate( "Windows.Data.Xml.Dom.XmlDocument", _xmlDocument );
		try
		{
			var io = Query( document, _xmlDocumentIo );
			try
			{
				using var xml = new HString( content );
				Check( Slot<Call>( io, 6 )( io, xml.Handle ) );
			}
			finally
			{
				Marshal.Release( io );
			}

			var factory = GetFactory( "Windows.UI.Notifications.ToastNotification", _toastFactory );
			try
			{
				Check( Slot<Create>( factory, 6 )( factory, document, out var toast ) );
				try
				{
					var tagged = Query( toast, _toastTag );
					try
					{
						using var name = new HString( tag );
						Check( Slot<Call>( tagged, 6 )( tagged, name.Handle ) );
						// Windows removes a toast by tag only together with a group.
						using var group = new HString( Group );
						Check( Slot<Call>( tagged, 8 )( tagged, group.Handle ) );
					}
					finally
					{
						Marshal.Release( tagged );
					}
				}
				catch
				{
					Marshal.Release( toast );
					throw;
				}

				return toast;
			}
			finally
			{
				Marshal.Release( factory );
			}
		}
		finally
		{
			Marshal.Release( document );
		}
	}

	/// <summary>Runs WinRT work with the runtime initialized on the calling thread.</summary>
	private static void Run( Action action )
	{
		var initialized = RoInitialize( Multithreaded );
		if ( initialized < 0 && initialized != ChangedMode )
			Check( initialized );

		try
		{
			action();
		}
		finally
		{
			if ( initialized >= 0 )
				RoUninitialize();
		}
	}

	/// <summary>Gets an activation factory interface for a runtime class.</summary>
	private static nint GetFactory( string name, Guid identity )
	{
		using var className = new HString( name );
		Check( RoGetActivationFactory( className.Handle, ref identity, out var factory ) );
		return factory;
	}

	/// <summary>Creates a runtime class instance and returns the requested interface.</summary>
	private static nint Activate( string name, Guid identity )
	{
		using var className = new HString( name );
		Check( RoActivateInstance( className.Handle, out var instance ) );
		try
		{
			return Query( instance, identity );
		}
		finally
		{
			Marshal.Release( instance );
		}
	}

	/// <summary>Gets another interface of a COM object.</summary>
	private static nint Query( nint instance, Guid identity )
	{
		Check( Marshal.QueryInterface( instance, in identity, out var result ) );
		return result;
	}

	/// <summary>Reads a method from a COM object's vtable.</summary>
	private static T Slot<T>( nint instance, int slot ) where T : Delegate =>
		Marshal.GetDelegateForFunctionPointer<T>( Marshal.ReadIntPtr( Marshal.ReadIntPtr( instance ), slot * IntPtr.Size ) );

	/// <summary>Throws for a failed HRESULT.</summary>
	private static void Check( int result )
	{
		if ( result < 0 )
			throw new COMException( "A Windows notification call failed.", result );
	}

	/// <summary>Calls a method taking one string or object argument.</summary>
	[UnmanagedFunctionPointer( CallingConvention.StdCall )]
	private delegate int Call( nint instance, nint argument );

	/// <summary>Calls a method taking one argument and returning an object.</summary>
	[UnmanagedFunctionPointer( CallingConvention.StdCall )]
	private delegate int Create( nint instance, nint argument, out nint result );

	/// <summary>Calls a property getter returning an object or enumeration.</summary>
	[UnmanagedFunctionPointer( CallingConvention.StdCall )]
	private delegate int Read( nint instance, out nint result );

	/// <summary>Calls IToastNotificationHistory.RemoveGroupedTagWithId.</summary>
	[UnmanagedFunctionPointer( CallingConvention.StdCall )]
	private delegate int RemoveTag( nint instance, nint tag, nint group, nint application );

	/// <summary>Owns one WinRT string handle.</summary>
	private readonly struct HString : IDisposable
	{
		/// <summary>Gets the native handle.</summary>
		internal nint Handle
		{
			get;
		}

		/// <summary>Creates a WinRT string copy of a managed string.</summary>
		internal HString( string value )
		{
			Check( WindowsCreateString( value, (uint)value.Length, out var handle ) );
			Handle = handle;
		}

		/// <summary>Releases the native string.</summary>
		public void Dispose() => _ = WindowsDeleteString( Handle );
	}

	/// <summary>Initializes the Windows Runtime on the current thread.</summary>
	[DllImport( "combase.dll" )]
	private static extern int RoInitialize( uint kind );

	/// <summary>Releases this thread's Windows Runtime initialization.</summary>
	[DllImport( "combase.dll" )]
	private static extern void RoUninitialize();

	/// <summary>Gets a runtime class activation factory.</summary>
	[DllImport( "combase.dll" )]
	private static extern int RoGetActivationFactory( nint name, ref Guid identity, out nint factory );

	/// <summary>Creates a runtime class with its default constructor.</summary>
	[DllImport( "combase.dll" )]
	private static extern int RoActivateInstance( nint name, out nint instance );

	/// <summary>Creates a WinRT string.</summary>
	[DllImport( "combase.dll", CharSet = CharSet.Unicode )]
	private static extern int WindowsCreateString( string value, uint length, out nint handle );

	/// <summary>Releases a WinRT string.</summary>
	[DllImport( "combase.dll" )]
	private static extern int WindowsDeleteString( nint handle );
}
