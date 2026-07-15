using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace OfflineMapsTest.Converters
{
	/// <summary>
	/// Maps <see cref="ViewModels.RadarSiteRow.IsOffline"/> (bool) to a status-dot brush: offline =
	/// critical (red), online = success (green). Resolves the system theme brushes at convert time so it
	/// follows the Win11 light/dark setting. Used by the Radar Site Explorer's list + detail status dots.
	/// </summary>
	public sealed class OfflineToBrushConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			bool isOffline = value is bool b && b;
			var key = isOffline ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush";
			if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush resolved)
			{
				return resolved;
			}

			return new SolidColorBrush(isOffline ? Microsoft.UI.Colors.OrangeRed : Microsoft.UI.Colors.LimeGreen);
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
