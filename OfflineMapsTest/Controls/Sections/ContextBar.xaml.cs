using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest.Controls.Sections
{
	/// <summary>
	/// The transport bar's right section: each enabled temporal feature's options shown inline. Cross-
	/// category, so it binds the coordinator <see cref="MapViewModel"/>. Today it hosts the inline Past
	/// Event input form (shown while PastCast is on); the form owns its own load/focus behavior.
	/// </summary>
	public sealed partial class ContextBar : UserControl
	{
		public ContextBar()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(ContextBar), new PropertyMetadata(null));

		// x:Bind function: bool -> Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;
	}
}
