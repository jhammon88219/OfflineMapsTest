using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
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

		// The Scan readout: the VOLUME's scan strategy — "VCP 215 · precip · SAILS/MRLE ×2".
		//
		// RadarViewModel.RadarModeText is one formatted string, "VCP 215 · precip · SAILS/MRLE ×2 ·
		// 0.5°×3" (or "VCP ? · 0.5°×3" when the VCP couldn't be read, or "VCP 212 · precip" with no
		// sweep segment at all on the archive path). Everything before the "0.5°" sweep token describes
		// the volume, so that's this row; the token itself is dropped, because "0.5°×3" and
		// "SAILS/MRLE ×2" state the same fact twice (3 sweeps = 1 + 2 extra) and the Tilt row now shows
		// the rendered elevation instead.
		//
		// SAILS belongs HERE, not on the Tilt row: it counts re-scans of the BASE tilt, a property of
		// the volume that holds whichever tilt is on screen. On the Tilt row it could only ever be true
		// for 0.5° and disappeared as soon as a higher tilt was selected.
		public string RadarVcpText(string mode) =>
			string.IsNullOrEmpty(mode) ? string.Empty : mode[..SweepIndex(mode)].TrimEnd(' ', '·');

		private static int SweepIndex(string mode)
		{
			var idx = mode.IndexOf("0.5°", StringComparison.Ordinal);
			return idx > 0 ? idx : mode.Length; // no sweep segment (e.g. "—" / "loading…") — keep it all
		}

		// Generic bool → Visibility for x:Bind (color-scale bar, inspect tick, numerical scale row).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

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
