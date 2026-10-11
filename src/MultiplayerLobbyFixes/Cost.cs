using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NuclearOption.Networking.Lobbies;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Times this mod's own work, so a hitch says whether it was spent here and in which part of it: the frame's
    /// own hook, a press, the asks a press sends, or the answers coming back.
    ///
    /// A mark costs nothing while verbose logging is off, and a line is written only for a frame that went over the
    /// limit, so a run that does not hitch pays one branch a frame and writes nothing.
    /// </summary>
    internal static class Cost
    {
        /// <summary>A frame this mod spent more than this many milliseconds in is worth a line.</summary>
        private const double Limit = 15;

        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        private static readonly List<string> Names = new List<string>();

        private static readonly List<double> Millis = new List<double>();

        private static readonly List<double> Peaks = new List<double>();

        /// <summary>The server the worst call of each kind was about, which is what a slow call needs naming.</summary>
        private static readonly List<ServerLobbyInstance> Slowest = new List<ServerLobbyInstance>();

        private static double _frame;

        /// <summary>Starts a measurement. A mark of zero means nothing is being measured.</summary>
        internal static long Mark()
        {
            return Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value ? Watch.ElapsedTicks : 0;
        }

        internal static void Add(string what, long mark, ServerLobbyInstance lobby = null)
        {
            if (mark == 0)
            {
                return;
            }

            var ms = (Watch.ElapsedTicks - mark) * 1000.0 / Stopwatch.Frequency;
            _frame += ms;

            for (var i = 0; i < Names.Count; i++)
            {
                if (Names[i] == what)
                {
                    Millis[i] += ms;
                    if (ms > Peaks[i])
                    {
                        Peaks[i] = ms;
                        Slowest[i] = lobby;
                    }

                    return;
                }
            }

            Names.Add(what);
            Millis.Add(ms);
            Peaks.Add(ms);
            Slowest.Add(lobby);
        }

        /// <summary>Reports the frame if it went over the limit, and clears the tally for the next one.</summary>
        internal static void Frame()
        {
            if (_frame >= Limit)
            {
                // The total and the worst single call of each kind, so one row drawn for a whole frame reads
                // differently from a hundred rows drawn for a slice each.
                var line = new StringBuilder("This mod spent " + _frame.ToString("F1") + " ms in one frame:");
                for (var i = 0; i < Names.Count; i++)
                {
                    line.Append(' ').Append(Names[i]).Append('=').Append(Millis[i].ToString("F1"))
                        .Append('/').Append(Peaks[i].ToString("F1"));

                    var slowest = Slowest[i];
                    if (slowest != null)
                    {
                        line.Append('(').Append(slowest.LobbyNameSanitized).Append(')');
                    }
                }

                Plugin.Verbose(line.ToString());
            }

            _frame = 0;
            Names.Clear();
            Millis.Clear();
            Peaks.Clear();
            Slowest.Clear();
        }
    }
}
