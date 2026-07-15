using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Converters
{
	/// <summary>
	/// Turns a <see cref="RadarRampInfo"/> (pushed from the WebView's <c>radar-ramps.js</c> — the SAME
	/// stops that color the gates) into a horizontal <see cref="LinearGradientBrush"/>, so any ramp bar
	/// is generated from the renderer's own data and can never drift from the pixels.
	///
	/// Honors the ramp's <see cref="RadarRampInfo.Interpolate"/> flag:
	/// <list type="bullet">
	/// <item><b>true</b> (velocity / CC / KDP / ZDR / SW) — one gradient stop per ramp stop, so WinUI
	/// blends between them into a smooth bar.</item>
	/// <item><b>false</b> (reflectivity) — DISCRETE NWS bands: each stop's color is emitted twice (at its
	/// own offset and again at the next stop's offset), which pins the color flat across the band and
	/// produces the hard edges the gates are actually painted with.</item>
	/// </list>
	/// A null/empty ramp yields an empty brush (the caller shows a ghost placeholder instead).
	/// </summary>
	public sealed class RampToBrushConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language) =>
			ToBrush(value as RadarRampInfo);

		/// <summary>The conversion itself, callable directly (e.g. from an x:Bind function).</summary>
		public static Brush ToBrush(RadarRampInfo? ramp)
		{
			var brush = new LinearGradientBrush
			{
				StartPoint = new Windows.Foundation.Point(0, 0.5),
				EndPoint = new Windows.Foundation.Point(1, 0.5),
			};

			if (ramp?.Stops is not { Count: > 0 } stops)
			{
				return brush;
			}

			double span = ramp.Max - ramp.Min;
			if (span <= 0)
			{
				span = 1;
			}

			double OffsetOf(double v) => Math.Clamp((v - ramp.Min) / span, 0, 1);

			for (int i = 0; i < stops.Count; i++)
			{
				var color = ColorOf(stops[i].Color);
				double start = OffsetOf(stops[i].V);
				brush.GradientStops.Add(new GradientStop { Offset = start, Color = color });

				// Discrete ramps: repeat this stop's color at the NEXT stop's offset so the band stays flat
				// and the next stop's color starts with a hard edge (matching the banded gate rendering).
				if (!ramp.Interpolate && i + 1 < stops.Count)
				{
					brush.GradientStops.Add(new GradientStop { Offset = OffsetOf(stops[i + 1].V), Color = color });
				}
			}

			return brush;
		}

		private static Windows.UI.Color ColorOf(int[]? rgb) =>
			rgb is { Length: >= 3 }
				? Windows.UI.Color.FromArgb(255, (byte)rgb[0], (byte)rgb[1], (byte)rgb[2])
				: Windows.UI.Color.FromArgb(0, 0, 0, 0);

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
