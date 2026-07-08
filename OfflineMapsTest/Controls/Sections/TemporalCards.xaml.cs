using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// Hosts the floating temporal settings cards (Past / Now / Fore) above the OverlayBar. Which one is
	/// shown is driven by <see cref="MapViewModel.OpenCard"/>; each card's down-triangle routes here to
	/// clear the open-card state (without disabling the feature). Bound to the coordinator
	/// <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class TemporalCards : UserControl
	{
		public TemporalCards()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalCards), new PropertyMetadata(null));

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed on a UserControl).
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// Any card's down-triangle hides the shown card. Only one is ever open, so clearing to None
		// is enough; the feature itself stays active.
		private void OnCardCloseRequested(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null)
			{
				ViewModel.OpenCard = TemporalCard.None;
			}
		}
	}
}
