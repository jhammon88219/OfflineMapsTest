using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The SPC storm-reports verification controls (per-type Tornado / Wind / Hail toggles, a count readout,
	/// and fill opacity), shown in both the Past and Now cards. Bound to the <see cref="StormReportsViewModel"/>,
	/// which keys the overlay to the active convective day (replay day in PastCast, today in NowCast).
	/// </summary>
	public sealed partial class StormReportsInput : UserControl
	{
		public StormReportsInput()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the per-type report counts (int → display string). Re-evaluates when
		/// the bound count property raises PropertyChanged.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>The storm-reports view model; bound from the host (MainWindow → ViewModel.StormReports).</summary>
		public StormReportsViewModel ViewModel
		{
			get => (StormReportsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StormReportsViewModel), typeof(StormReportsInput), new PropertyMetadata(null));
	}
}
