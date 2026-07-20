using System;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Anvil
{
	/// <summary>
	/// DEV-ONLY site-sweep results pop-up (see the XAML header). Shows a <see cref="SweepReport"/> and,
	/// on Save…, writes its markdown to a file the user picks. Save is deferred so a failed/cancelled
	/// pick keeps the dialog open. Built as a ContentDialog, mirroring <see cref="SettingsDialog"/>.
	/// </summary>
	public sealed partial class SweepReportDialog : ContentDialog
	{
		private readonly SweepReport _report;
		private readonly nint _ownerHwnd;

		public SweepReportDialog(SweepReport report, nint ownerHwnd)
		{
			_report = report;
			_ownerHwnd = ownerHwnd;
			InitializeComponent();
			SummaryText.Text = report.Summary;
			ReportText.Text = report.ToMarkdown();
		}

		private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			// Hold the dialog open across the async pick; without the deferral it closes immediately.
			var deferral = args.GetDeferral();
			args.Cancel = true; // don't dismiss on Save — let the user keep the results up
			try
			{
				var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Desktop };
				picker.FileTypeChoices.Add("Markdown", new[] { ".md" });
				picker.SuggestedFileName = _report.SuggestedFileName;
				WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);

				var file = await picker.PickSaveFileAsync();
				if (file is not null)
				{
					await FileIO.WriteTextAsync(file, _report.ToMarkdown());
				}
			}
			catch
			{
				// A failed save must not take down the dev tool; the report stays on screen to retry.
			}
			finally
			{
				deferral.Complete();
			}
		}
	}
}
