using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// DEV-ONLY site-sweep card (see the XAML header). Hosts the sweep params + run controls, bound to a
	/// <see cref="SiteSweepViewModel"/>. Its own open/close is a plain view-state bool (<see cref="IsCardOpen"/>)
	/// toggled by the bar's dev "Sweep" button, mirroring how the app settings card is driven — except this
	/// state lives on the control (there is no app-wide VM flag for a dev tool). When a run finishes it
	/// raises <see cref="ReportRequested"/> so the host can open <see cref="SweepReportDialog"/>.
	/// </summary>
	public sealed partial class DevSweepCard : UserControl
	{
		public DevSweepCard()
		{
			InitializeComponent();
			Loaded += OnLoaded;
		}

		/// <summary>The sweep engine; bound from the host.</summary>
		public SiteSweepViewModel? SweepVm
		{
			get => (SiteSweepViewModel?)GetValue(SweepVmProperty);
			set => SetValue(SweepVmProperty, value);
		}

		public static readonly DependencyProperty SweepVmProperty =
			DependencyProperty.Register(nameof(SweepVm), typeof(SiteSweepViewModel), typeof(DevSweepCard), new PropertyMetadata(null));

		/// <summary>Whether the card is open (bar button ↔ this). View state on the control, like the
		/// OverlayBar's own pull-tab visibility — a dev tool has no app-wide VM flag.</summary>
		public bool IsCardOpen
		{
			get => (bool)GetValue(IsCardOpenProperty);
			set => SetValue(IsCardOpenProperty, value);
		}

		public static readonly DependencyProperty IsCardOpenProperty =
			DependencyProperty.Register(nameof(IsCardOpen), typeof(bool), typeof(DevSweepCard), new PropertyMetadata(false));

		/// <summary>Raised when the user asks to see the finished report (run completion or the Report button).</summary>
		public event EventHandler<SweepReport>? ReportRequested;

		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// Seed the NumberBoxes from the VM once it's bound (they're double-valued; the VM props are int).
		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (SweepVm is null) return;
			DwellBox.Value = SweepVm.DwellSeconds;
			TimeoutBox.Value = SweepVm.PerSiteTimeoutSeconds;
			FramesBox.Value = SweepVm.FramesPerSite;
			SweepVm.PropertyChanged += OnSweepPropertyChanged;
		}

		// Auto-open the report the moment a run produces one.
		private void OnSweepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(SiteSweepViewModel.HasReport) && SweepVm?.LastReport is { } report)
			{
				ReportRequested?.Invoke(this, report);
			}
		}

		private void OnDwellChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
		{
			if (SweepVm is not null && !double.IsNaN(args.NewValue)) SweepVm.DwellSeconds = (int)args.NewValue;
		}

		private void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
		{
			if (SweepVm is not null && !double.IsNaN(args.NewValue)) SweepVm.PerSiteTimeoutSeconds = (int)args.NewValue;
		}

		private void OnFramesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
		{
			if (SweepVm is not null && !double.IsNaN(args.NewValue)) SweepVm.FramesPerSite = (int)args.NewValue;
		}

		private async void OnStartClick(object sender, RoutedEventArgs e)
		{
			if (SweepVm is not null) await SweepVm.StartAsync();
		}

		private void OnStopClick(object sender, RoutedEventArgs e) => SweepVm?.Stop();

		private void OnReportClick(object sender, RoutedEventArgs e)
		{
			if (SweepVm?.LastReport is { } report) ReportRequested?.Invoke(this, report);
		}

		private void OnCardCloseRequested(object sender, RoutedEventArgs e) => IsCardOpen = false;
	}
}
