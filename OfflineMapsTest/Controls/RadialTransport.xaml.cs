using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OfflineMapsTest.ViewModels;
using Windows.Foundation;

namespace OfflineMapsTest.Controls
{
	/// <summary>
	/// Round transport dial: the outer ring shows loop position and is a scrubber (drag to seek); the
	/// center button plays (▶ when stopped) / stops (■ when playing — stop returns to the newest frame).
	/// Reads <see cref="RadarViewModel"/> frame state; writes the scrub position back via CurrentFrameIndex.
	/// </summary>
	public sealed partial class RadialTransport : UserControl
	{
		private const double Diameter = 150;
		private const double StrokeThickness = 10;
		private const double Center = Diameter / 2;                  // 75
		private const double Radius = (Diameter - StrokeThickness) / 2; // 70, to the stroke centerline
		private const double ButtonRadius = 46;                     // center hit-radius (the play/stop button)

		private bool _scrubbing;

		public RadialTransport()
		{
			InitializeComponent();
			Loaded += (_, _) => UpdateVisual();
		}

		public RadarViewModel ViewModel
		{
			get => (RadarViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(RadialTransport),
				new PropertyMetadata(null, OnViewModelChanged));

		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (RadialTransport)d;
			if (e.OldValue is RadarViewModel oldVm) oldVm.PropertyChanged -= self.OnVmPropertyChanged;
			if (e.NewValue is RadarViewModel newVm) newVm.PropertyChanged += self.OnVmPropertyChanged;
			self.UpdateVisual();
		}

		private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(RadarViewModel.CurrentFrameIndex):
				case nameof(RadarViewModel.MaxFrameIndex):
				case nameof(RadarViewModel.IsPlaying):
				case nameof(RadarViewModel.IsLoopReady):
					DispatcherQueue?.TryEnqueue(UpdateVisual);
					break;
			}
		}

		// Refresh the icon, dim state, and ring arc/thumb from the VM.
		private void UpdateVisual()
		{
			if (ViewModel is null)
			{
				return;
			}

			bool playing = ViewModel.IsPlaying;
			PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
			StopIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
			Dial.Opacity = ViewModel.IsLoopReady ? 1.0 : 0.5;

			int max = ViewModel.MaxFrameIndex;
			double frac = max > 0 ? Math.Clamp(ViewModel.CurrentFrameIndex / max, 0, 1) : 0;
			SetArc(frac);
		}

		// Draws the progress arc from 12 o'clock clockwise to the fraction, and places the thumb there.
		private void SetArc(double frac)
		{
			if (frac <= 0.0001)
			{
				ProgressArc.Data = null;
				PlaceThumb(-90);
				return;
			}

			double f = Math.Min(frac, 0.9999); // a true full circle degenerates to a point
			double endDeg = -90 + f * 360;
			var figure = new PathFigure { StartPoint = new Point(Center, Center - Radius), IsClosed = false };
			figure.Segments.Add(new ArcSegment
			{
				Point = PointAt(endDeg),
				Size = new Size(Radius, Radius),
				SweepDirection = SweepDirection.Clockwise,
				IsLargeArc = f > 0.5,
			});
			var geometry = new PathGeometry();
			geometry.Figures.Add(figure);
			ProgressArc.Data = geometry;
			PlaceThumb(endDeg);
		}

		private static Point PointAt(double degrees)
		{
			double rad = degrees * Math.PI / 180.0;
			return new Point(Center + Radius * Math.Cos(rad), Center + Radius * Math.Sin(rad));
		}

		private void PlaceThumb(double degrees)
		{
			var p = PointAt(degrees);
			Thumb.Margin = new Thickness(p.X - Thumb.Width / 2, p.Y - Thumb.Height / 2, 0, 0);
		}

		private void OnCenterClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null)
			{
				return;
			}
			if (ViewModel.IsPlaying)
			{
				ViewModel.StopRadarLoop();   // stop = halt + return to newest
			}
			else
			{
				ViewModel.ToggleRadarPlay(); // play / resume
			}
		}

		private void OnDialPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (ViewModel is null || !ViewModel.IsLoopReady)
			{
				return;
			}
			var p = e.GetCurrentPoint(Dial).Position;
			if (Distance(p) < ButtonRadius)
			{
				return; // center button area — let the button handle it
			}
			_scrubbing = true;
			if (ViewModel.IsPlaying)
			{
				ViewModel.ToggleRadarPlay(); // pause in place so scrubbing sticks
			}
			Dial.CapturePointer(e.Pointer);
			SeekTo(p);
		}

		private void OnDialPointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (_scrubbing && ViewModel is not null)
			{
				SeekTo(e.GetCurrentPoint(Dial).Position);
			}
		}

		private void OnDialPointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!_scrubbing)
			{
				return;
			}
			_scrubbing = false;
			Dial.ReleasePointerCapture(e.Pointer);
		}

		// Pointer position -> nearest frame index (angle from 12 o'clock, clockwise).
		private void SeekTo(Point p)
		{
			int max = ViewModel.MaxFrameIndex;
			if (max <= 0)
			{
				return;
			}
			double deg = Math.Atan2(p.Y - Center, p.X - Center) * 180.0 / Math.PI; // 0 at 3 o'clock
			double fromTop = deg + 90.0;                                           // 0 at 12 o'clock
			if (fromTop < 0) fromTop += 360.0;
			int idx = Math.Clamp((int)Math.Round(fromTop / 360.0 * max), 0, max);
			ViewModel.CurrentFrameIndex = idx; // clamps + pushes to JS + raises (UpdateVisual repaints)
			SetArc(idx / (double)max);         // immediate feedback while dragging
		}

		private static double Distance(Point p) =>
			Math.Sqrt((p.X - Center) * (p.X - Center) + (p.Y - Center) * (p.Y - Center));
	}
}
