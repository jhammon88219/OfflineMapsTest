using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using OfflineMapsTest.ViewModels;

namespace OfflineMapsTest
{
	/// <summary>
	/// Routes JS→C# WebView2 messages (posted by map.js / radar.js) to the view models and the
	/// radar diagnostics. Extracted from MainWindow so the window isn't a giant message switch.
	/// Owns the one-shot map-ready latch.
	/// </summary>
	internal sealed class WebMessageRouter
	{
		private readonly MapViewModel _viewModel;
		private bool _mapReady;

		/// <summary>
		/// Raised once, right after the first map-ready is handled, so the host can run WebView setup
		/// that isn't a view-model concern (e.g. pushing the OS theme accent to the page).
		/// </summary>
		public event System.Func<System.Threading.Tasks.Task>? MapReady;

		public WebMessageRouter(MapViewModel viewModel)
		{
			_viewModel = viewModel;
		}

		public async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
		{
			var message = args.TryGetWebMessageAsString();
			if (string.IsNullOrEmpty(message))
			{
				return;
			}

			try
			{
				using var doc = JsonDocument.Parse(message);
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object ||
					!root.TryGetProperty("type", out var typeEl))
				{
					return;
				}
				var type = typeEl.GetString();

				// Free-form diagnostics line from radar.js (also echoed to the VS Output window).
				if (type == "radarLog")
				{
					if (root.TryGetProperty("msg", out var msgEl))
					{
						var msg = msgEl.GetString();
						System.Diagnostics.Debug.WriteLine($"[radar-js] {msg}");
						if (msg is not null) Services.RadarDiagnostics.JsLog(msg);
					}
					return;
				}

				// Structured per-frame decode metrics from radar.js — recorded with suspect-frame
				// evaluation + .V06 quarantine in the diagnostics service.
				if (type == "radarFrame")
				{
					Services.RadarDiagnostics.JsFrame(root);
					return;
				}

					// Velocity build progress: how many loaded frames have their (lazily-built, dealiased)
					// velocity geometry ready. Drives the "Building velocity N/M" readout and lets playback
					// hold at the built frontier instead of stuttering into a still-decoding frame.
					if (type == "radarBuildProgress")
					{
						var built = root.TryGetProperty("built", out var bEl) ? bEl.GetInt32() : 0;
						var total = root.TryGetProperty("total", out var tEl) ? tEl.GetInt32() : 0;
						bool[]? ready = null;
						if (root.TryGetProperty("ready", out var rdEl) && rdEl.ValueKind == JsonValueKind.Array)
						{
							ready = new bool[rdEl.GetArrayLength()];
							var ri = 0;
							foreach (var e in rdEl.EnumerateArray())
							{
								ready[ri++] = e.ValueKind == JsonValueKind.True;
							}
						}
						_viewModel.Radar.SetBuildProgress(built, total, ready);
						return;
					}

					// Render-health signal from radar.js (blank / error / recovered / context lost).
					if (type == "radarRender")
				{
					Services.RadarDiagnostics.JsRender(root);
					return;
				}

				// The active product's color ramp (pushed from radar-ramps.js) — feeds the legend.
				if (type == "radarRamp")
				{
					if (root.TryGetProperty("ramp", out var rampEl))
					{
						var ramp = JsonSerializer.Deserialize<Models.RadarRampInfo>(
							rampEl.GetRawText(),
							new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
						_viewModel.Radar.SetColorScale(ramp);
					}
					return;
				}

				// Inspect-mode value under the cursor (pushed from radar.js as the pointer moves) —
				// drives the live marker on the color-scale bar. has=false clears it.
				if (type == "radarInspect")
				{
					var has = root.TryGetProperty("has", out var hasEl) && hasEl.GetBoolean();
					double? val = has && root.TryGetProperty("value", out var valEl) ? valEl.GetDouble() : null;
					_viewModel.Radar.SetInspectValue(val);
					return;
				}

				// A radar site marker was clicked.
				if (type == "radarSiteClick")
				{
					if (root.TryGetProperty("id", out var siteEl))
					{
						_viewModel.Radar.OnRadarSiteClicked(siteEl.GetString());
					}
					return;
				}

				// A map marker (e.g. the user-location marker) was clicked — select it for editing.
				if (type == "markerClick")
				{
					if (root.TryGetProperty("id", out var mIdEl))
					{
						_viewModel.Markers.OnMarkerClicked(mIdEl.GetString());
					}
					return;
				}

				// A draggable marker was moved — record the refined position.
				if (type == "markerMoved")
				{
					if (root.TryGetProperty("id", out var mvIdEl) &&
						root.TryGetProperty("lng", out var lngEl) &&
						root.TryGetProperty("lat", out var latEl))
					{
						_viewModel.Markers.OnMarkerMoved(mvIdEl.GetString(), lngEl.GetDouble(), latEl.GetDouble());
					}
					return;
				}

				// A radar loop frame finished decoding in the WebView.
				if (type == "radarFrameReady")
				{
					if (root.TryGetProperty("index", out var idxEl))
					{
						var hasData = root.TryGetProperty("hasData", out var hd) && hd.GetBoolean();
						_viewModel.Radar.OnRadarFrameReady(idxEl.GetInt32(), hasData);
					}
					return;
				}

				// The page posts {type:"mapReady"} once its map's 'load' fires. Enable
				// style / outlook commands the first time we see it.
				if (type == "mapReady" && !_mapReady)
				{
					_mapReady = true;
					// Push the theme accent (MapReady) BEFORE the VM's map-ready work, which creates the
					// radar-site markers: their halo reads a CSS var, so setting it first means the markers
					// render in the accent from the start instead of flashing the green fallback for the
					// ~second OnMapsReadyAsync takes.
					if (MapReady is not null)
					{
						await MapReady.Invoke();
					}
					await _viewModel.OnMapsReadyAsync();
				}
			}
			catch (JsonException)
			{
				// Ignore malformed messages from the page.
			}
		}
	}
}
