using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using UnityEngine;

namespace MapMouseSelect
{
    /// <summary>
    /// Temporary: measures the work behind the refresh stutter and logs one line a second while anything is
    /// running. Remove once the cost is known. Buckets are milliseconds per second, with the worst single call.
    /// </summary>
    internal static class PerfProbe
    {
        internal const int Tick = 0;
        internal const int List = 1;
        internal const int Sort = 2;
        internal const int Show = 3;
        internal const int Paint = 4;
        internal const int Press = 5;
        internal const int Count = 6;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly double[] Total = new double[Count];
        private static readonly double[] Max = new double[Count];
        private static readonly int[] Calls = new int[Count];
        private static float _at;
        private static int _frames;

        internal static long Now
        {
            get { return Clock.ElapsedTicks; }
        }

        internal static void Add(int bucket, long since)
        {
            var ms = (Clock.ElapsedTicks - since) * 1000.0 / Stopwatch.Frequency;
            Total[bucket] += ms;
            Calls[bucket]++;
            if (ms > Max[bucket])
            {
                Max[bucket] = ms;
            }
        }

        internal static void Frame()
        {
            _frames++;
            if (Time.unscaledTime - _at < 1f)
            {
                return;
            }

            Report();
        }

        private static void Report()
        {
            var any = false;
            for (var i = 0; i < Count; i++)
            {
                if (Calls[i] > 0)
                {
                    any = true;
                    break;
                }
            }

            var burst = ServerBrowser.OutstandingQuestions;
            if (!any && burst == 0 && _frames == 0)
            {
                return;
            }

            var text = new StringBuilder("Perf(refresh): frames=").Append(_frames).Append(" burst=").Append(burst);
            Append(text, "tick", Tick);
            Append(text, "list", List);
            Append(text, "sort", Sort);
            Append(text, "show", Show);
            Append(text, "paint", Paint);
            Append(text, "press", Press);
            Plugin.Verbose(text.ToString());

            _frames = 0;
            for (var i = 0; i < Count; i++)
            {
                Total[i] = 0;
                Max[i] = 0;
                Calls[i] = 0;
            }

            _at = Time.unscaledTime;
        }

        private static void Append(StringBuilder text, string name, int bucket)
        {
            if (Calls[bucket] == 0)
            {
                return;
            }

            text.Append(' ').Append(name).Append('=')
                .Append(Total[bucket].ToString("F1")).Append("ms/max")
                .Append(Max[bucket].ToString("F1")).Append(" x").Append(Calls[bucket]);
        }
    }

    /// <summary>Temporary: times the game's row redraw, which every answer runs.</summary>
    [HarmonyPatch(typeof(LobbyListItem), nameof(LobbyListItem.Show))]
    internal static class PerfShowPatch
    {
        [HarmonyPrefix]
        private static void Prefix(out long __state)
        {
            __state = PerfProbe.Now;
        }

        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            PerfProbe.Add(PerfProbe.Show, __state);
        }
    }
}
