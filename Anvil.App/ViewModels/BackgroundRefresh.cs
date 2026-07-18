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
	}
}
