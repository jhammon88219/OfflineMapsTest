using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// The SPC outlook controls (Day / Product selectors + fill opacity), shown as the body of the
	/// ForeCast settings card while ForeCast (Outlook.IsOutlookVisible) is on — the ForeCast counterpart
	/// to <see cref="PastEventInput"/>. Bound to the <see cref="OutlookViewModel"/>; the whole outlook
	/// subsystem (fetch/cache/refresh, times, narrative) already lives there, so this is a thin selector
	/// surface. (Watches moved to the NowCast card / <see cref="WatchesViewModel"/>.)
	/// </summary>
	public sealed partial class OutlookInput : UserControl
	{
		public OutlookInput()
		{
			InitializeComponent();
		}

		/// <summary>The outlook view model; bound from the host (ContextBar → ViewModel.Outlook).</summary>
		public OutlookViewModel ViewModel
		{
			get => (OutlookViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(OutlookViewModel), typeof(OutlookInput), new PropertyMetadata(null));
	}
}
