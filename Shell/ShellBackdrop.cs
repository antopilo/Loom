using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Maps the shell backdrop setting to Goo's native material and translucent shell surfaces.</summary>
internal static class ShellBackdrop
{
	/// <summary>Reports whether a setting value enables a native material.</summary>
	internal static bool IsEnabled( string value ) => value is ShellSettings.MicaBackdrop or ShellSettings.AcrylicBackdrop;

	/// <summary>Returns the surface alpha from zero to one: opaque without a backdrop, else the percent setting.</summary>
	internal static double Alpha( string backdrop, int percent ) => IsEnabled( backdrop ) ? Math.Clamp( percent, 0, 100 ) / 100.0 : 1;

	/// <summary>Applies one backdrop choice to a window whose native surface has transparency enabled.</summary>
	internal static void Apply( Window window, string value )
	{
		var isEnabled = IsEnabled( value );
		window.Background = isEnabled ? Color.Transparent : Theme.Current.Canvas;
		window.BackdropFallbackColor = Theme.Current.Canvas;
		window.WindowsBackdrop = new WindowsBackdropOptions
		{
			Material = value == ShellSettings.AcrylicBackdrop ? WindowsBackdropMaterial.Acrylic : WindowsBackdropMaterial.Mica,
		};
		window.Backdrop = isEnabled ? WindowBackdrop.Blur : WindowBackdrop.None;
	}
}
