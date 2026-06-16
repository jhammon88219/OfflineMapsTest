using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// A process-wide, thread-safe ring buffer of timestamped radar diagnostic events, written
	/// from the VM, the Level II service, and (routed through MainWindow) the WebView's radar JS.
	/// It exists to debug intermittent issues — e.g. "the loop runs a couple cycles then the
	/// tiles vanish" — that a point-in-time snapshot can't capture: let the app run, reproduce,
	/// then dump the whole timeline (Copy Debug button) and hand it back.
	///
	/// Deliberately static + lockless-to-callers so any layer can log without plumbing a
	/// dependency through. Keep messages compact and prefixed by subsystem (vm/svc/js).
	/// </summary>
	public static class RadarDebugLog
	{
		private const int Capacity = 4000;
		private static readonly object Gate = new();
		private static readonly Queue<string> Entries = new(Capacity);
		private static long _seq;

		/// <summary>Appends one event (timestamped, sequence-numbered). Safe from any thread.</summary>
		public static void Log(string message)
		{
			var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} #{System.Threading.Interlocked.Increment(ref _seq)} {message}";
			lock (Gate)
			{
				Entries.Enqueue(line);
				while (Entries.Count > Capacity)
				{
					Entries.Dequeue();
				}
			}
		}

		/// <summary>Total events logged since launch (including any aged out of the buffer).</summary>
		public static long TotalCount => System.Threading.Interlocked.Read(ref _seq);

		/// <summary>The most recent <paramref name="count"/> events, oldest-first.</summary>
		public static IReadOnlyList<string> Tail(int count)
		{
			lock (Gate)
			{
				return Entries.Count <= count
					? Entries.ToList()
					: Entries.Skip(Entries.Count - count).ToList();
			}
		}

		/// <summary>The full buffer as one newline-joined string (for clipboard / file export).</summary>
		public static string Dump()
		{
			lock (Gate)
			{
				var sb = new StringBuilder(Entries.Count * 48);
				foreach (var e in Entries)
				{
					sb.AppendLine(e);
				}
				return sb.ToString();
			}
		}

		/// <summary>Clears the buffer (used when starting a fresh capture).</summary>
		public static void Clear()
		{
			lock (Gate)
			{
				Entries.Clear();
			}
		}
	}
}
