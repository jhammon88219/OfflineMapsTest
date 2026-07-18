using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls
{
	/// <summary>
	/// Fixed-size, theme-aware chrome for a temporal feature's settings card floating above the OverlayBar.
	/// Carries a <see cref="Title"/> and a <see cref="CardBody"/> content slot; its down-triangle raises
	/// <see cref="CloseRequested"/> so the host can hide it (hiding does NOT disable the feature).
	/// </summary>
	public sealed partial class ControlCard : UserControl
	{
		public ControlCard()
		{
			InitializeComponent();
		}

		/// <summary>Header title (e.g. "Past Event", "SPC Outlooks").</summary>
		public string Title
		{
			get => (string)GetValue(TitleProperty);
			set => SetValue(TitleProperty, value);
		}

		public static readonly DependencyProperty TitleProperty =
			DependencyProperty.Register(nameof(Title), typeof(string), typeof(ControlCard), new PropertyMetadata(string.Empty));

		/// <summary>The feature controls shown in the card body (filled by the host).</summary>
		public object? CardBody
		{
			get => GetValue(CardBodyProperty);
			set => SetValue(CardBodyProperty, value);
		}

		public static readonly DependencyProperty CardBodyProperty =
			DependencyProperty.Register(nameof(CardBody), typeof(object), typeof(ControlCard), new PropertyMetadata(null));

		/// <summary>Raised when the user clicks the down-triangle to hide the card.</summary>
		public event RoutedEventHandler? CloseRequested;

		private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, e);
	}
}
