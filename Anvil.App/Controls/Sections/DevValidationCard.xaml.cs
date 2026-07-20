using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// DEV-ONLY dealias-validation card (see the XAML header). Hosts the run controls for the fixed-corpus
	/// regression harness, bound to a <see cref="RadarValidationViewModel"/>. Its own open/close is a plain
	/// view-state bool (<see cref="IsCardOpen"/>) toggled by the bar's dev "Validate" button, mirroring
	/// <see cref="DevSweepCard"/>. When a run finishes it raises <see cref="ReportRequested"/> so the host
	/// can open <see cref="Anvil.ValidationReportDialog"/>.
	/// </summary>
	public sealed partial class DevValidationCard : UserControl
	{
		public DevValidationCard()
		{
			InitializeComponent();
			Loaded += OnLoaded;
		}

		/// <summary>The validation engine; bound from the host.</summary>
		public RadarValidationViewModel? ViewModel
		{
			get => (RadarValidationViewModel?)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarValidationViewModel), typeof(DevValidationCard), new PropertyMetadata(null));

		/// <summary>Whether the card is open (bar button ↔ this). View state on the control, like
		/// <see cref="DevSweepCard.IsCardOpen"/> — a dev tool has no app-wide VM flag.</summary>
		public bool IsCardOpen
		{
			get => (bool)GetValue(IsCardOpenProperty);
			set => SetValue(IsCardOpenProperty, value);
		}

		public static readonly DependencyProperty IsCardOpenProperty =
			DependencyProperty.Register(nameof(IsCardOpen), typeof(bool), typeof(DevValidationCard), new PropertyMetadata(false));

		/// <summary>Raised when the user asks to see the finished report (run completion or the Report button).</summary>
		public event EventHandler<RadarValidationReport>? ReportRequested;

		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null)
			{
				ViewModel.PropertyChanged += OnViewModelPropertyChanged;
			}
		}

		// Auto-open the report the moment a run produces one.
		private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(RadarValidationViewModel.HasReport) && ViewModel?.LastReport is { } report)
			{
				ReportRequested?.Invoke(this, report);
			}
		}

		private async void OnRunClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) await ViewModel.StartAsync();
		}

		private void OnStopClick(object sender, RoutedEventArgs e) => ViewModel?.Stop();

		private void OnReportClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.LastReport is { } report) ReportRequested?.Invoke(this, report);
		}

		private void OnCardCloseRequested(object sender, RoutedEventArgs e) => IsCardOpen = false;
	}
}
