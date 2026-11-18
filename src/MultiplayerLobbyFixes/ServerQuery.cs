using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using Steamworks;
using UnityEngine;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Asks Steam's master list for one server, by its Steam ID, and hands the fresh copy back.
    ///
    /// This is the same request the game's own join lookup makes: `RequestInternetServerList` with a single
    /// `steamid` filter. Steam probes that one server and answers through the same callbacks a full refresh uses,
    /// so a card can be brought up to date without re-asking every other server. The copy it answers with is a
    /// whole `gameserveritem_t`: the name, the player counts, the ping, the mission and the game tags that carry
    /// the map, the start time and the password flag.
    ///
    /// A server whose query port does not answer fails the probe here the same way it fails it in a refresh. That
    /// is not an error to report: the caller is told, and the row keeps what it already says.
    ///
    /// One ask per server at a time. The handle is released once the request stops refreshing, which is what the
    /// game's own request does after its own callback.
    /// </summary>
    internal static class ServerQuery
    {
        /// <summary>How long an ask that never completes is written off, so its handle cannot leak.</summary>
        private const float Timeout = 10f;

        private sealed class AskState
        {
            internal ServerLobbyInstance Lobby;

            internal Action<gameserveritem_t> Answered;

            internal HServerListRequest Request;

            internal SteamLobby Manager;

            internal float When;

            internal bool Responded;

            internal bool Completed;

            internal bool Done;

            internal ISteamMatchmakingServerListResponse Response;
        }

        /// <summary>The asks that are out, by server, which is also what the markers read.</summary>
        private static readonly Dictionary<ulong, AskState> Asking = new Dictionary<ulong, AskState>();

        /// <summary>Whether a single-server ask is out for this server, which is what its row shows.</summary>
        internal static bool IsAsking(ServerLobbyInstance lobby)
        {
            var id = ServerBrowser.IdOf(lobby);
            return id.IsValid() && Asking.ContainsKey(id.m_SteamID);
        }

        /// <summary>
        /// Asks the master list about one server. The answer, or null when the server did not answer, arrives once
        /// the request has finished. Returns false when the ask could not be made at all.
        /// </summary>
        internal static bool Ask(ServerLobbyInstance lobby, Action<gameserveritem_t> answered)
        {
            var details = lobby != null ? lobby.details : null;
            var manager = SteamLobby.instance;
            if (details == null || !details.m_steamID.IsValid() || manager == null)
            {
                return false;
            }

            var id = details.m_steamID;
            AskState previous;
            if (Asking.TryGetValue(id.m_SteamID, out previous))
            {
                // A second press while one is out adds itself to the answer rather than spending a second request.
                previous.Answered += answered;
                return true;
            }

            var ask = new AskState
            {
                Lobby = lobby,
                Answered = answered,
                Manager = manager,
                When = Time.time,
            };

            var response = new ISteamMatchmakingServerListResponse(
                (request, index) => Responded(ask, request, index),
                (request, index) => { },
                (request, result) => RefreshComplete(ask, request));

            ask.Response = response;

            try
            {
                var filter = new MatchMakingKeyValuePair_t[1];
                filter[0].m_szKey = "steamid";
                filter[0].m_szValue = id.m_SteamID.ToString();

                ask.Request = SteamMatchmakingServers.RequestInternetServerList(
                    SteamUtils.GetAppID(), filter, (uint)filter.Length, response);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not ask the master list about a server: " + e);
                return false;
            }

            Asking[id.m_SteamID] = ask;
            Plugin.Verbose("Asked the master list about " + lobby.LobbyNameSanitized + " (" + id.m_SteamID + ").");
            return true;
        }

        private static void Responded(AskState ask, HServerListRequest request, int index)
        {
            gameserveritem_t copy;
            try
            {
                copy = SteamMatchmakingServers.GetServerDetails(request, index);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not read the master list's answer for a server: " + e);
                return;
            }

            if (copy == null || !copy.m_steamID.IsValid())
            {
                return;
            }

            // The copy is repaired on its way in by ServerDetailsPatch, so a field the server did not report keeps
            // what the last copy said.
            ask.Lobby.SetDetails(copy);
            ask.Responded = true;

            ServerBrowser.Unreachable.Remove(copy.m_steamID.m_SteamID);
        }

        private static void RefreshComplete(AskState ask, HServerListRequest request)
        {
            ask.Completed = true;
            End(ask, ask.Responded);
        }

        /// <summary>
        /// Finishes an ask: releases the handle, takes it out of the table, and hands the answer on. A server that
        /// did not answer is reported as one this machine cannot reach, which is what its row then says.
        /// </summary>
        private static void End(AskState ask, bool answered)
        {
            if (ask.Done)
            {
                return;
            }

            ask.Done = true;

            var id = ServerBrowser.IdOf(ask.Lobby);
            if (id.IsValid())
            {
                Asking.Remove(id.m_SteamID);
            }

            try
            {
                if (!ask.Completed)
                {
                    // The refresh never finished: the query is still live on Steam's side, so it stops here.
                    SteamMatchmakingServers.CancelQuery(ask.Request);
                }

                SteamMatchmakingServers.ReleaseRequest(ask.Request);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not release a server request: " + e);
            }

            if (!answered && id.IsValid())
            {
                ServerBrowser.Unreachable.Add(id.m_SteamID);
                Plugin.Verbose(
                    "The master list could not reach " + ask.Lobby.LobbyNameSanitized + " (" + id.m_SteamID
                    + "), so its row keeps what it already says.");
            }

            var callback = ask.Answered;
            ask.Answered = null;
            if (callback != null)
            {
                try
                {
                    callback(answered ? ask.Lobby.details : null);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Could not take a server's answer: " + e);
                }
            }
        }

        /// <summary>Ends asks that never completed, from the mod's own per-frame host.</summary>
        internal static void Tick()
        {
            if (Asking.Count == 0)
            {
                return;
            }

            List<AskState> stuck = null;
            foreach (var ask in Asking.Values)
            {
                if (Time.time - ask.When > Timeout)
                {
                    stuck = stuck ?? new List<AskState>();
                    stuck.Add(ask);
                }
            }

            if (stuck == null)
            {
                return;
            }

            foreach (var ask in stuck)
            {
                // A response that did arrive still counts: only the request's own completion was lost.
                End(ask, ask.Responded);
            }
        }
    }
}



