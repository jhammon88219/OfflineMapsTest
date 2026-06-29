using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OfflineMapsTest.Services;

namespace OfflineMapsTest
{
	/// <summary>
	/// App settings dialog. Currently lets the user point the app at the folder holding the offline
	/// basemap PMTiles file; built as a ContentDialog so future settings drop in as more rows. The
	/// dialog only edits <see cref="SelectedFolder"/> — the caller persists it (and prompts a restart).
	/// </summary>
	public sealed partial class SettingsDialog : ContentDialog
	{
		private readonly ISettingsService _settings;
		private readonly nint _ownerHwnd;

		public SettingsDialog(ISettingsService settings, nint ownerHwnd)
		{
			_settings = settings;
			_ownerHwnd = ownerHwnd;
			InitializeComponent();
			FolderBox.Text = settings.MapDataFolder;
			RefreshStatus();
		}

		/// <summary>The folder currently chosen in the dialog (not yet persisted).</summary>
		public string SelectedFolder => FolderBox.Text;

		private async void OnBrowseClick(object sender, RoutedEventArgs e)
		{
			var picker = new Windows.Storage.Pickers.FolderPicker();
			picker.FileTypeFilter.Add("*"); // FolderPicker requires at least one filter entry.
			// WinUI 3 desktop has no CoreWindow, so the picker must be tied to the window handle.
			WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);

			var folder = await picker.PickSingleFolderAsync();
			if (folder is not null)
			{
				FolderBox.Text = folder.Path;
				RefreshStatus();
			}
		}

		// Shows whether the basemap file is present in the chosen folder (green check / amber warning).
		private void RefreshStatus()
		{
			var present = _settings.MapDataFilePresent(FolderBox.Text);
			StatusIcon.Glyph = present ? "" : ""; // checkmark / warning
			StatusIcon.Foreground = new SolidColorBrush(present
				? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50)   // green
				: Windows.UI.Color.FromArgb(0xFF, 0xE3, 0xB3, 0x41)); // amber
			StatusText.Text = present
				? $"{_settings.MapDataFileName} found here."
				: $"{_settings.MapDataFileName} isn't in this folder — the basemap won't draw until it is (overlays still work).";
		}
	}
}
