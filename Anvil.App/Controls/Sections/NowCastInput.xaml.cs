using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The NowCast (live radar) settings-card body. Surfaces the "now"-oriented live-conditions options —
	/// today just the SPC Watch boxes (which are active alerts, hence NowCast not ForeCast). Bound to the
	/// coordinator <see cref="MapViewModel"/> so it can reach the relevant subsystems (watches on
	/// <c>Watches</c>, and future live-radar settings on <c>Radar</c>).
	/// </summary>
	public sealed partial class NowCastInput : UserControl
	{
		public NowCastInput()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the per-type active-warning counts (int → display string). Used by
		/// the "Active" readout; re-evaluates when the bound count property raises PropertyChanged.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(NowCastInput), new PropertyMetadata(null));
	}
}
