using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// The transport bar's left section: three temporal-category checkboxes (PastCast / NowCast /
	/// ForeCast) that enable each time-frame's features on the map. Cross-category, so it binds the
	/// coordinator <see cref="MapViewModel"/>. PastCast ↔ <c>Radar.IsPastEventMode</c>; the others are
	/// placeholders until their features are re-added.
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
