using System;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Small shared helper for the app's launch-then-interval background refresh loops (SPC outlooks,
	/// SPC watches). Kept in one place so each subsystem VM runs the exact same loop shape rather than
	/// duplicating the timer plumbing.
	/// </summary>
	internal static class BackgroundRefresh
	{
		/// <summary>
		/// Runs <paramref name="work"/> once immediately, then every <paramref name="interval"/> for the
		/// app's life. <c>first</c> is true only on the launch cycle. The caller owns its try/catch so one
		/// bad cycle can't kill the loop.
		/// </summary>
		public static async Task RunPeriodicAsync(TimeSpan interval, Func<bool, Task> work)
		{
			var first = true;
			using var timer = new PeriodicTimer(interval);
			do
			{
				await work(first);
				first = false;
			}
			while (await timer.WaitForNextTickAsync());
		}

		/// <summary>
		/// Like <see cref="RunPeriodicAsync"/>, but the cadence is DYNAMIC: <paramref name="work"/> returns
		/// the delay to wait before the next cycle, so it can speed up or slow down based on what it just
		/// found (e.g. poll faster while warnings are active). Runs once immediately (<c>first</c> = true),
		/// then waits the returned delay and repeats for the app's life. The caller owns its try/catch so
		/// one bad cycle can't kill the loop; a returned delay is clamped to a small floor so a bug can't
		/// spin the loop hot.
		/// </summary>
		public static async Task RunAdaptiveAsync(Func<bool, Task<TimeSpan>> work)
		{
			var first = true;
			while (true)
			{
				var next = await work(first);
				first = false;
				if (next < TimeSpan.FromSeconds(1)) { next = TimeSpan.FromSeconds(1); }
				await Task.Delay(next);
			}
		}
	}
}
