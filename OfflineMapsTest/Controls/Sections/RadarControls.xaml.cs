using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OfflineMapsTest.Models;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// The whole radar console in one control: the center transport cluster (prev · play/stop · next +
	/// a linear scrubber + frame N/M and time), the left display controls (color scale, product, tilt,
	/// show/hide sites, inspect), and the right selected-site readout. All bound to one
	/// <see cref="RadarViewModel"/>.
	/// </summary>
	public sealed partial class RadarControls : UserControl
	{
		public RadarControls()
		{
			InitializeComponent();
		}

		/// <summary>The radar view model driving these controls; bound from the host.</summary>
		public RadarViewModel ViewModel
		{
			get => (RadarViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(RadarControls),
				new PropertyMetadata(null, OnViewModelChanged));

		// Track the VM's frame index / count so the scrubber playhead can follow (there's no thumb control
		// to two-way-bind now — the segmented scrubber is drawn, and the playhead is positioned by code).
		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (RadarControls)d;
			if (e.OldValue is RadarViewModel oldVm)
			{
				oldVm.PropertyChanged -= self.OnViewModelPropertyChanged;
				oldVm.Segments.CollectionChanged -= self.OnSegmentsChanged;
			}
			if (e.NewValue is RadarViewModel newVm)
			{
				newVm.PropertyChanged += self.OnViewModelPropertyChanged;
				newVm.Segments.CollectionChanged += self.OnSegmentsChanged; // count changes -> reposition playhead
			}
			self.UpdatePlayhead();
		}

		private void OnSegmentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
			UpdatePlayhead();

		private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(RadarViewModel.CurrentFrameIndex) or nameof(RadarViewModel.MaxFrameIndex))
			{
				UpdatePlayhead();
			}
		}

		private void OnToggleSitesClick(object sender, RoutedEventArgs e) =>
			ViewModel?.ToggleRadarSitesVisible();

		// Play/stop button: while playing, Stop (halt + return to newest); otherwise Play/resume. Mirrors
		// the old dial's center button so the single-button "play + stop in one" behavior is unchanged.
		private void OnPlayStopClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null)
			{
				return;
			}
			if (ViewModel.IsPlaying)
			{
				ViewModel.StopRadarLoop();
			}
			else
			{
				ViewModel.ToggleRadarPlay();
			}
		}

		private void OnPrevClick(object sender, RoutedEventArgs e) => ViewModel?.StepFrame(-1);

		private void OnNextClick(object sender, RoutedEventArgs e) => ViewModel?.StepFrame(+1);

		// ---- Segmented scrubber interaction ----
		// The scrubber is drawn (cells + playhead), not a Slider, so seeking is handled here: press/drag on
		// the strip maps the pointer x to a frame index. Playback pauses on grab so the drag isn't fought by
		// the advancing loop, and the playhead follows via UpdatePlayhead (VM PropertyChanged / SizeChanged).
		private bool _scrubbing;

		private void OnScrubberPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (ViewModel is null || ViewModel.Segments.Count == 0) return;
			_scrubbing = true;
			ScrubberHost.CapturePointer(e.Pointer);
			if (ViewModel.IsPlaying) ViewModel.ToggleRadarPlay(); // pause in place; stays engaged so Stop remains available
			SeekToPointer(e);
		}

		private void OnScrubberPointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (_scrubbing) SeekToPointer(e);
		}

		private void OnScrubberPointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!_scrubbing) return;
			_scrubbing = false;
			ScrubberHost.ReleasePointerCapture(e.Pointer);
		}

		private void OnScrubberSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayhead();

		private void SeekToPointer(PointerRoutedEventArgs e)
		{
			if (ViewModel is null) return;
			// Count from the RENDERED cells (Segments), so seek + playhead + cells always agree.
			var count = ViewModel.Segments.Count;
			var width = ScrubberHost.ActualWidth;
			if (count <= 0 || width <= 0) return;
			var x = e.GetCurrentPoint(ScrubberHost).Position.X;
			var idx = Math.Clamp((int)Math.Floor(x / (width / count)), 0, count - 1);
			if (idx != ViewModel.CurrentFrameIndex) ViewModel.CurrentFrameIndex = idx;
		}

		// Positions the playhead over the current segment's centre. Uses Segments.Count and the SAME
		// per-cell rounding as UniformHorizontalPanel, so the playhead lands exactly on each cell's midpoint
		// (no cumulative drift). Called when the frame index / count changes (VM) or the strip resizes.
		private void UpdatePlayhead()
		{
			if (ViewModel is null || ScrubberHost is null || Playhead is null || PlayheadTransform is null) return;
			var count = ViewModel.Segments.Count;
			var width = ScrubberHost.ActualWidth;
			if (count <= 0 || width <= 0)
			{
				Playhead.Visibility = Visibility.Collapsed;
				return;
			}
			Playhead.Visibility = Visibility.Visible;
			var idx = Math.Clamp(ViewModel.CurrentFrameIndex, 0, count - 1);
			var cell = width / count;
			// Cell idx spans [round(idx*cell), round((idx+1)*cell)] in the panel — centre on that span.
			var centre = (Math.Round(idx * cell) + Math.Round((idx + 1) * cell)) / 2;
			PlayheadTransform.X = Math.Clamp(centre - Playhead.Width / 2, 0, width - Playhead.Width);
		}

		// Segoe Fluent glyph for the center button: Stop while playing, Play otherwise.
		public string PlayStopGlyph(bool isPlaying) => isPlaying ? "" : "";

		// "N / M" frame counter (1-based) shown under the scrubber. Empty until there's a real multi-frame
		// loop (max 0 = 0 or 1 frames, where the scrubber is disabled anyway) so it isn't a misleading "1 / 1".
		public string FrameCountText(double current, int max) =>
			max <= 0 ? string.Empty : $"{(int)Math.Round(current) + 1} / {max + 1}";

		// RadarViewModel.RadarModeText is one formatted string, e.g. "VCP 215 · precip · 0.5°×3 ·
		// SAILS/MRLE ×2" (or "VCP ? · 0.5°×3" when the VCP couldn't be read). These x:Bind functions
		// split it across the two lower right-side rows at the "0.5°" sweep segment, which always
		// starts the second half regardless of which format applies.
		public string RadarVcpText(string mode) =>
			string.IsNullOrEmpty(mode) ? string.Empty : mode[..SweepIndex(mode)].TrimEnd(' ', '·');

		public string RadarSweepText(string mode)
		{
			if (string.IsNullOrEmpty(mode))
			{
				return string.Empty;
			}

			var sweep = mode[SweepIndex(mode)..];
			// "0.5°×N" is the low-tilt sweep count; ×1 is the normal single scan (every clear-air /
			// non-SAILS volume), so it's constant noise — show it only when SAILS/MRLE adds extra
			// rescans (×2, ×3…). Guard against a future ×10+ by requiring the "1" isn't part of a
			// longer number.
			if (sweep.StartsWith("0.5°×1", StringComparison.Ordinal) &&
				(sweep.Length == 6 || !char.IsDigit(sweep[6])))
			{
				sweep = "0.5°" + sweep[6..];
			}

			return sweep;
		}

		private static int SweepIndex(string mode)
		{
			var idx = mode.IndexOf("0.5°", StringComparison.Ordinal);
			return idx > 0 ? idx : mode.Length; // no sweep segment (e.g. "—" / "loading…") — keep it all on the top line
		}

		// Generic bool → Visibility for x:Bind (color-scale bar, inspect tick, numerical scale row).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		// Inverse of VisibleWhen: shows the ghost placeholder bar only while no real ramp is known yet.
		public Visibility GhostVisibility(bool hasColorScale) =>
			hasColorScale ? Visibility.Collapsed : Visibility.Visible;

		// Positions the inspect tick by splitting the bar into two proportional (star) columns at
		// InspectFraction (0-1); the tick sits on the boundary. Width-independent — works at any bar size.
		public GridLength InspectLeftStar(double fraction) =>
			new GridLength(Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		public GridLength InspectRightStar(double fraction) =>
			new GridLength(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		// Builds the color-scale legend brush from the active product's ramp — the SAME stops
		// radar-ramps.js uses to color the gates. Always rendered as a SMOOTH gradient (one gradient stop
		// per ramp stop, WinUI interpolates between them), even for discrete ramps like reflectivity whose
		// gates are painted in hard NWS dBZ bands — this is a legend-display choice and doesn't affect the
		// gate coloring (that stays banded in the JS renderer).
		public Brush RampBrush(RadarRampInfo ramp)
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

			foreach (var s in stops)
			{
				double offset = Math.Clamp((s.V - ramp.Min) / span, 0, 1);
				brush.GradientStops.Add(new GradientStop { Offset = offset, Color = ColorOf(s.Color) });
			}

			return brush;
		}

		// Legend scale ticks: value at 0 / 0.5 / 1 along the ramp. Velocity is shown in mph (converted
		// from the ramp's native m/s, to match the inspector); other products keep their native unit
		// (dBZ, ρHV). Only the MAX tick carries the unit label, so the reader sees what the numbers are.
		public string RampScaleMin(RadarRampInfo ramp) => RampScaleValue(ramp, 0, withUnit: false);
		public string RampScaleMid(RadarRampInfo ramp) => RampScaleValue(ramp, 0.5, withUnit: false);
		public string RampScaleMax(RadarRampInfo ramp) => RampScaleValue(ramp, 1, withUnit: true);

		private static string RampScaleValue(RadarRampInfo ramp, double fraction, bool withUnit)
		{
			if (ramp is null)
			{
				return string.Empty;
			}

			double v = ramp.Min + (ramp.Max - ramp.Min) * fraction;
			string unit = ramp.Unit;
			if (unit == "m/s")
			{
				v *= 2.23694;
				unit = "mph";
			}

			string num = v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
			return withUnit && !string.IsNullOrEmpty(unit) ? $"{num} {unit}" : num;
		}

		private static Windows.UI.Color ColorOf(int[] rgb) =>
			rgb is { Length: >= 3 }
				? Windows.UI.Color.FromArgb(255, (byte)rgb[0], (byte)rgb[1], (byte)rgb[2])
				: Windows.UI.Color.FromArgb(0, 0, 0, 0);

		// Foreground for the staleness readout, ramped continuously by the newest frame's age in minutes:
		// green while fresh → amber at ~12 min (the Live→Recent boundary) → red at ~30 min (Recent→Stale),
		// clamped red beyond. The two knees match RadarStatus's freshness thresholds so the color and the
		// status dot agree. Null age (no frame yet, "—") reads as a muted gray.
		public Brush AgeBrush(double? minutes)
		{
			if (minutes is not double m)
			{
				return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A));
			}

			var fresh = Windows.UI.Color.FromArgb(255, 0x3F, 0xB9, 0x50); // green
			var mid = Windows.UI.Color.FromArgb(255, 0xE3, 0xB3, 0x41);   // amber
			var stale = Windows.UI.Color.FromArgb(255, 0xF8, 0x51, 0x49); // red

			Windows.UI.Color c = m <= 12 ? Lerp(fresh, mid, m / 12.0)
				: m <= 30 ? Lerp(mid, stale, (m - 12) / 18.0)
				: stale;
			return new SolidColorBrush(c);
		}

		private static Windows.UI.Color Lerp(Windows.UI.Color a, Windows.UI.Color b, double t)
		{
			t = Math.Clamp(t, 0, 1);
			return Windows.UI.Color.FromArgb(255,
				(byte)(a.R + (b.R - a.R) * t),
				(byte)(a.G + (b.G - a.G) * t),
				(byte)(a.B + (b.B - a.B) * t));
		}
	}
}
