using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// The overlay bar's left section: three temporal features (PastCast / NowCast / ForeCast), each a
	/// two-part split button — a feature toggle (left) + a settings cog (right) that opens that feature's
	/// floating card (see <see cref="TemporalCards"/>). A 3-way radio routed through
	/// <see cref="MapViewModel.TemporalMode"/> (Past/Fore mutually exclusive; Now = live). The cog is
	/// disabled until its feature is on. Binds the coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class TemporalToggles : UserControl
	{
		public TemporalToggles()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalToggles), new PropertyMetadata(null));
	}
}
