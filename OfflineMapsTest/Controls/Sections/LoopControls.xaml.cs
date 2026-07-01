using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// Transport-bar section: the radar loop's play / stop / scrubber / frame-time controls, bound to
	/// <see cref="RadarViewModel"/>. Composed into the TransportBar content slot by the host.
	/// </summary>
	public sealed partial class LoopControls : UserControl
	{
		public LoopControls()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(LoopControls), new PropertyMetadata(null));

		private void OnTogglePlayClick(object sender, RoutedEventArgs e) =>
			ViewModel?.ToggleRadarPlay();

		private void OnStopLoopClick(object sender, RoutedEventArgs e) =>
			ViewModel?.StopRadarLoop();
	}
}
