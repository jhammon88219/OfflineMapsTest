using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// Transport-bar section: the radar Product selector (and a disabled Tilt stub), bound to
	/// <see cref="RadarViewModel"/>. Composed into the TransportBar content slot by the host.
	/// </summary>
	public sealed partial class RadarProductControls : UserControl
	{
		public RadarProductControls()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(RadarProductControls), new PropertyMetadata(null));
	}
}
