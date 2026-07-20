using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The HISTORICAL SPC outlook controls (Day / Product / issuance Cycle + fill opacity + status/times),
	/// shown in the Past Event card beneath the replay date/time controls (<see cref="PastEventInput"/>).
	/// Bound to the <see cref="PastOutlookViewModel"/>, which fetches the archived issuance for the replay
	/// date and drives the shared map outlook layer while in PastCast. Mirrors <see cref="OutlookInput"/>.
	/// </summary>
	public sealed partial class PastOutlookInput : UserControl
	{
		public PastOutlookInput()
		{
			InitializeComponent();
		}

		/// <summary>The past-outlook view model; bound from the host (MainWindow → ViewModel.PastOutlook).</summary>
		public PastOutlookViewModel ViewModel
		{
			get => (PastOutlookViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(PastOutlookViewModel), typeof(PastOutlookInput), new PropertyMetadata(null));

		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
	}
}
