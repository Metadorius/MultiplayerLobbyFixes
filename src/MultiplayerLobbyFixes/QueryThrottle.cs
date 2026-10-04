using System;
using System.Collections.Generic;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace MapMouseSelect
{
    /// <summary>
    /// Spaces the browser's per-server queries out instead of letting a refresh fire them in one burst.
    ///
    /// Every rules query and every ping in the game goes through SteamMatchmakingServers, so both calls are
    /// fenced here: a query that arrives sooner than the configured spacing after the last one waits in a queue
    /// instead of being dropped, and a pump owned by this mod issues what is waiting, one query per interval.
    /// The callers get nothing back for a queued query and live with it: the game stores the handle only to
    /// cancel it later, and the answers, which is all anyone reads, arrive normally.
    ///
    /// The details window's own Refresh press goes out on the spot, past the queue, because a player is waiting
    /// on it; everything else, the game's refreshes and its ping loop alike, takes its turn.
    /// </summary>
    internal static class QueryThrottle
    {
        private static readonly Queue<KeyValuePair<string, Action>> Pending =
            new Queue<KeyValuePair<string, Action>>();

        private static float _nextAt;

        /// <summary>Set while a queued query is being issued, so it is not queued again on its way out.</summary>
        private static bool _issuing;

        private static GameObject _pump;

        /// <summary>
        /// Whether a query may go out now. False means it was queued, and the caller's call must be skipped.
        /// </summary>
        internal static bool TryIssue(string what, Action call)
        {
            if (_issuing || Plugin.QuerySpacingMs.Value <= 0f)
            {
                return true;
            }

            var now = Time.time;
            if (Pending.Count == 0 && now >= _nextAt)
            {
                _nextAt = now + Plugin.QuerySpacingMs.Value / 1000f;
                return true;
            }

            if (!PumpExists())
            {
                // Without the pump the queue would never move, so this query goes out unspaced instead.
                Plugin.Verbose("Sending a " + what + " query unspaced, no pump.");
                return true;
            }

            Pending.Enqueue(new KeyValuePair<string, Action>(what, call));
            Plugin.Verbose("Queued a " + what + " query, " + Pending.Count + " waiting.");
            return false;
        }

        /// <summary>Issues one queued query once the spacing has passed, which the pump calls every frame.</summary>
        internal static void Drain()
        {
            if (Pending.Count == 0)
            {
                return;
            }

            var now = Time.time;
            if (now < _nextAt)
            {
                return;
            }

            var next = Pending.Dequeue();
            _nextAt = now + Plugin.QuerySpacingMs.Value / 1000f;

            _issuing = true;
            try
            {
                next.Value();
                Plugin.Verbose("Issued a queued " + next.Key + " query, " + Pending.Count + " left.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not issue a queued server query: " + e);
            }
            finally
            {
                _issuing = false;
            }
        }

        /// <summary>Sends a call past the queue, for the queries a player is actively waiting on.</summary>
        internal static void Now(Action call)
        {
            _issuing = true;
            try
            {
                call();
            }
            finally
            {
                _issuing = false;
            }
        }

        private static bool PumpExists()
        {
            if (_pump != null)
            {
                return true;
            }

            try
            {
                _pump = new GameObject("MapMouseSelectQueryThrottle");
                UnityEngine.Object.DontDestroyOnLoad(_pump);
                _pump.AddComponent<Pump>();
                Plugin.Verbose("Query pump started.");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not start the query pump: " + e);
                return false;
            }
        }

        /// <summary>The per-frame hook that issues what is waiting, owned by a GameObject of this mod's own.</summary>
        private class Pump : MonoBehaviour
        {
            private void Update()
            {
                Drain();
            }
        }
    }

    /// <summary>Sends the game's rules queries, and this mod's, through the throttle.</summary>
    [HarmonyPatch(typeof(SteamMatchmakingServers), nameof(SteamMatchmakingServers.ServerRules))]
    internal static class ServerRulesQueuePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            uint unIP, ushort usPort, ISteamMatchmakingRulesResponse pRequestServersResponse)
        {
            return QueryThrottle.TryIssue(
                "rules", () => SteamMatchmakingServers.ServerRules(unIP, usPort, pRequestServersResponse));
        }
    }

    /// <summary>Sends the game's pings, and this mod's, through the throttle.</summary>
    [HarmonyPatch(typeof(SteamMatchmakingServers), nameof(SteamMatchmakingServers.PingServer))]
    internal static class ServerPingQueuePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            uint unIP, ushort usPort, ISteamMatchmakingPingResponse pRequestServersResponse)
        {
            return QueryThrottle.TryIssue(
                "ping", () => SteamMatchmakingServers.PingServer(unIP, usPort, pRequestServersResponse));
        }
    }
}
