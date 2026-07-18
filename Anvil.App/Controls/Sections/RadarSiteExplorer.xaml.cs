using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Converters;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The Radar Site Explorer panel — a non-modal, searchable master–detail browser over the radar
	/// network, floating above the OverlayBar. Bound to the coordinator <see cref="MapViewModel"/> (like
	/// <see cref="AppSettingsCard"/>): its visibility follows <see cref="MapViewModel.IsSiteExplorerOpen"/>
	/// and it reaches into <see cref="MapViewModel.SiteExplorer"/> for the list/detail. The close triangle
	/// and Load button are handled here in code-behind.
	/// </summary>
	public sealed partial class RadarSiteExplorer : UserControl
	{
		public RadarSiteExplorer()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSiteExplorer), new PropertyMetadata(null));

		// x:Bind helpers (bool → Visibility) — no value-converter lookup needed on a UserControl.
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
		public Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

		// The selected row's status-dot brush, for the detail pane (safe when nothing is selected).
		public Microsoft.UI.Xaml.Media.Brush SelectedStatusBrush(RadarSiteRow? row) =>
			(Microsoft.UI.Xaml.Media.Brush)_offlineToBrush.Convert(row?.IsOffline ?? false, typeof(Microsoft.UI.Xaml.Media.Brush), null!, null!);

		private readonly OfflineToBrushConverter _offlineToBrush = new();

		// Close triangle: hide the explorer (app-wide open state on the coordinator).
		private void OnCloseClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null)
			{
				ViewModel.IsSiteExplorerOpen = false;
			}
		}

		// Load the selected site's radar loop on the map, then close the panel.
		private void OnLoadClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null)
			{
				return;
			}

			ViewModel.SiteExplorer.LoadOnMap();
			ViewModel.IsSiteExplorerOpen = false;
		}
	}
}
