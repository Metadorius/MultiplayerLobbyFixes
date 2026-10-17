using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using UnityEngine;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Holds the game's per-server queries in a queue and starts them one at a time, instead of letting a
    /// refresh start them all in one frame.
    ///
    /// The queue holds the game's own query objects, not the Steam calls: `SteamLobby.SearchQuery` and
    /// `PingQuery` are deferred at their `Run`, which is the method that registers the query with its request and
    /// stores the handle Steam hands back. Because the handle is stored when the query finally runs at its turn,
    /// everything the game does with that handle keeps working, and no query is left without an owner.
    ///
    /// Two guards keep the queue honest. A `Cancel` on a query that is still waiting drops it, so a refresh that
    /// replaces its request never starts a query nobody wants. And a request that is cancelled drops everything
    /// still waiting for it, which the request's own cancel loop cannot reach, because a waiting query is not in
    /// the request's list of running queries yet.
    ///
    /// A query the queue takes is skipped at its call site and started again by the pump, spending the shared
    /// `Rate` like the mod's own asks do. A query that arrives while nothing is waiting starts on the spot, so the
    /// queue costs nothing in the common single-query case.
    /// </summary>
    internal static class QueryThrottle
    {
        /// <summary>The queries waiting for their turn, oldest first.</summary>
        private static readonly Queue<object> Pending = new Queue<object>();

        /// <summary>The queries still waiting, for membership and for the drop when one is cancelled.</summary>
        private static readonly HashSet<object> Waiting = new HashSet<object>();

        private static readonly Dictionary<Type, MethodInfo> RunMethods = new Dictionary<Type, MethodInfo>();

        /// <summary>The request a rules query belongs to, for the drop when that request is cancelled.</summary>
        private static readonly FieldInfo RequestField = AccessTools.Field(
            AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "listRequest");

        /// <summary>The ping list a ping query belongs to, which is the list its request holds.</summary>
        private static readonly FieldInfo QueriesField = AccessTools.Field(
            AccessTools.Inner(typeof(SteamLobby), "PingQuery"), "runningQueries");

        /// <summary>The game's own rate: its queries spend the shared budget like everything else.</summary>
        private static readonly Rate Game = Plugin.Queries;

        /// <summary>Set while a waiting query is being started, so it is not queued again on its way out.</summary>
        private static bool _issuing;

        private static GameObject _pump;

        /// <summary>
        /// Whether a query may start now. False means it waits in the queue, and the caller's Run is skipped.
        /// </summary>
        internal static bool TryDefer(object query, string what)
        {
            if (_issuing || !Plugin.ThrottleQueries.Value)
            {
                return true;
            }

            if (Waiting.Count == 0 && Game.Take(Time.unscaledTime))
            {
                return true;
            }

            if (!PumpExists())
            {
                // A query the game asked for is never dropped: with no pump to start it, it starts now.
                Plugin.Verbose("Starting a " + what + " query unspaced.");
                return true;
            }

            Pending.Enqueue(query);
            Waiting.Add(query);
            return false;
        }

        /// <summary>
        /// Whether the game may cancel this query. False means it was still waiting, so it had no handle and
        /// there is nothing to cancel: it is dropped from the queue instead.
        /// </summary>
        internal static bool TryCancel(object query)
        {
            return !Waiting.Remove(query);
        }

        /// <summary>
        /// Drops everything still waiting for a request that was just cancelled. The request's own cancel loop
        /// cannot reach a waiting query, because a query registers itself with its request only when it runs.
        /// </summary>
        internal static void DropFor(object listRequest, object runningQueries)
        {
            if (Waiting.Count == 0)
            {
                return;
            }

            List<object> dropped = null;
            foreach (var query in Waiting)
            {
                var mine = RequestField != null && ReferenceEquals(RequestField.GetValue(query), listRequest);
                if (!mine && QueriesField != null)
                {
                    mine = ReferenceEquals(QueriesField.GetValue(query), runningQueries);
                }

                if (mine)
                {
                    dropped = dropped ?? new List<object>();
                    dropped.Add(query);
                }
            }

            if (dropped == null)
            {
                return;
            }

            foreach (var query in dropped)
            {
                Waiting.Remove(query);
            }

            Plugin.Verbose("Dropped " + dropped.Count + " queries still waiting for a cancelled request.");
        }

        /// <summary>Starts a frame's share of the waiting queries; the pump calls it every frame.</summary>
        internal static void Drain()
        {
            if (Pending.Count == 0)
            {
                return;
            }

            if (!Plugin.ThrottleQueries.Value)
            {
                // The toggle went off with queries still waiting: none of them should sit in here.
                while (Pending.Count > 0)
                {
                    var flushed = Pending.Dequeue();
                    if (Waiting.Remove(flushed))
                    {
                        Start(flushed);
                    }
                }

                return;
            }

            while (Pending.Count > 0)
            {
                // A query the game cancelled while it waited is gone from Waiting: dropping it costs neither a call
                // nor a slot, so it is cleared before the rate is asked, and the next one may still go out now.
                if (!Waiting.Contains(Pending.Peek()))
                {
                    Pending.Dequeue();
                    continue;
                }

                if (!Game.Take(Time.unscaledTime))
                {
                    return;
                }

                Start(Pending.Dequeue());
            }
        }

        /// <summary>Runs the query object's own Run, which is what registers it and stores its handle.</summary>
        private static void Start(object query)
        {
            var type = query.GetType();

            MethodInfo run;
            if (!RunMethods.TryGetValue(type, out run))
            {
                run = AccessTools.Method(type, "Run");
                RunMethods[type] = run;
            }

            if (run == null)
            {
                Plugin.Log.LogError("A queued query has no Run: " + type.FullName);
                return;
            }

            _issuing = true;
            try
            {
                run.Invoke(query, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not start a queued server query: " + e);
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
                _pump = new GameObject("MultiplayerLobbyFixesQueryThrottle");
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

    /// <summary>Holds the game's rules queries in the queue, at the point their own object starts them.</summary>
    [HarmonyPatch]
    internal static class SearchQueryRunPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "Run");
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            return QueryThrottle.TryDefer(__instance, "rules");
        }
    }

    /// <summary>The same for the queries the game's ping loop starts.</summary>
    [HarmonyPatch]
    internal static class PingQueryRunPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(AccessTools.Inner(typeof(SteamLobby), "PingQuery"), "Run");
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            return QueryThrottle.TryDefer(__instance, "ping");
        }
    }

    /// <summary>
    /// Drops a query the game cancels while it is still waiting, and lets one that has started be cancelled as
    /// usual, which is what releases its handle and its socket.
    /// </summary>
    [HarmonyPatch]
    internal static class QueryCancelPatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            var rules = AccessTools.Method(AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "Cancel");
            if (rules != null)
            {
                yield return rules;
            }

            var ping = AccessTools.Method(AccessTools.Inner(typeof(SteamLobby), "PingQuery"), "Cancel");
            if (ping != null)
            {
                yield return ping;
            }
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            return QueryThrottle.TryCancel(__instance);
        }
    }

    /// <summary>
    /// Drops everything still waiting for a request that is being cancelled. The request's own cancel loop only
    /// sees the queries that already ran, so the waiting ones need this to not start after their request is gone.
    /// </summary>
    [HarmonyPatch]
    internal static class RequestCancelPatch
    {
        private static readonly FieldInfo QueriesField = AccessTools.Field(
            AccessTools.Inner(typeof(SteamLobby), "ServerListRequest"), "RunningQueries");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(AccessTools.Inner(typeof(SteamLobby), "ServerListRequest"), "Cancel");
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance)
        {
            QueryThrottle.DropFor(__instance, QueriesField != null ? QueriesField.GetValue(__instance) : null);
        }
    }
}
