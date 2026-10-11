using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using NuclearOption.UI;
using Steamworks;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Applies the browser's filters to what has already arrived, instead of letting the game ask Steam for the
    /// whole list again whenever a toggle moves.
    ///
    /// Every filter control and the browser's own Refresh button call GetListOfLobbies, so this is the one door
    /// they share: the call is left alone when the toggles match the filter the current list was built with (the
    /// Refresh button, and the first call each screen makes), and taken over when they differ, which is a toggle
    /// having moved. The taken-over call clears the rows locally and shows every kept server that passes, with
    /// the queries they are waiting on left alone, and the filter also goes to the request that may still be
    /// answering, so a server arriving after the toggle is judged by the toggle and not by what the request was
    /// asked with. The checks mirror the game's own: the rules answer path does
    /// the password, the count and the ping ones, and the master request carries the mission type, the server
    /// type and the distance.
    ///
    /// The same checks gate the admit pass, so a server the filter hides is not handed to the ping loop either
    /// while it is out of sight.
    /// </summary>
    internal static class LocalFilters
    {
        private static readonly FieldInfo FullField = AccessTools.Field(typeof(LobbyList), "hideFullServersToggle");

        private static readonly FieldInfo EmptyField = AccessTools.Field(typeof(LobbyList), "hideEmptyServersToggle");

        private static readonly FieldInfo PasswordField = AccessTools.Field(
            typeof(LobbyList), "hidePasswordProtectedToggle");

        private static readonly FieldInfo MissionField = AccessTools.Field(typeof(LobbyList), "missionTypeToggleGroup");

        private static readonly FieldInfo ServerField = AccessTools.Field(typeof(LobbyList), "serverTypeToggleGroup");

        private static readonly FieldInfo DistanceField = AccessTools.Field(
            typeof(LobbyList), "distanceFilterToggleGroup");

        private static readonly FieldInfo ActiveField = AccessTools.Field(typeof(LobbyList), "activeFilter");

        private static readonly MethodInfo ClearMethod = AccessTools.Method(typeof(LobbyList), "ClearLobbies");

        private static readonly FieldInfo LobbiesField = AccessTools.Field(typeof(SteamLobby), "_lobbyData");

        private static readonly Type RequestType = AccessTools.Inner(typeof(SteamLobby), "ServerListRequest");

        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "SearchQuery");

        private static readonly FieldInfo RequestField = AccessTools.Field(typeof(SteamLobby), "serverListRequest");

        private static readonly FieldInfo RequestFilterField = RequestType != null
            ? AccessTools.Field(RequestType, "filter")
            : null;

        private static readonly FieldInfo RunningField = RequestType != null
            ? AccessTools.Field(RequestType, "RunningQueries")
            : null;

        private static readonly FieldInfo QueryFilterField = QueryType != null
            ? AccessTools.Field(QueryType, "filter")
            : null;

        /// <summary>The browser seen so far, so each screen's first request is always a real one.</summary>
        private static LobbyList _seen;

        /// <summary>The browser in use, for the readers that do not have it at hand.</summary>
        private static LobbyList _list;

        /// <summary>What a GetListOfLobbies call is: the screen opening, a moved toggle, or the Refresh button.</summary>
        internal enum FilterCall
        {
            /// <summary>The list's own Start asks first, and that call is always a real request.</summary>
            First,

            /// <summary>A toggle moved, so the list is filtered from what is in hand instead of requeried.</summary>
            Toggle,

            /// <summary>The Refresh button, the one call the mod's own refresh pass rides along with.</summary>
            Refresh,
        }

        /// <summary>
        /// Sorts this call into the three kinds. The Refresh button and the screen opening both build the filter
        /// the list already uses, while a moved toggle builds a different one.
        /// </summary>
        internal static FilterCall Classify(LobbyList list)
        {
            if (list == null || ActiveField == null)
            {
                return FilterCall.First;
            }

            _list = list;

            if (_seen != list)
            {
                // The list's own Start asks first, and that call is always a real one.
                _seen = list;
                return FilterCall.First;
            }

            var value = ActiveField.GetValue(list);
            var active = value is LobbySearchFilter filter ? filter : default(LobbySearchFilter);
            return Same(FilterOf(list), active) ? FilterCall.Refresh : FilterCall.Toggle;
        }

        /// <summary>
        /// Puts the filter on the rows in hand: they are cleared, and every kept lobby the filter keeps is shown
        /// again, with nothing asked of Steam.
        /// </summary>
        internal static void Apply(LobbyList list)
        {
            var filter = FilterOf(list);
            if (ActiveField != null)
            {
                ActiveField.SetValue(list, filter);
            }

            Push(filter);

            ClearMethod?.Invoke(list, null);

            var manager = SteamLobby.instance;
            var lobbies = manager != null && LobbiesField != null
                ? LobbiesField.GetValue(manager) as Dictionary<CSteamID, LobbyInstance>
                : null;
            if (lobbies == null)
            {
                return;
            }

            var shown = 0;
            foreach (var lobby in lobbies.Values)
            {
                if (Passes(lobby, filter))
                {
                    lobby.InList = true;
                    ServerBrowser.Announce(manager, lobby);
                    shown++;
                }
                else
                {
                    lobby.InList = false;
                }
            }

            Plugin.Verbose(
                "Filtered the servers already in hand: " + shown + " of " + lobbies.Count
                + " shown, no queries spent.");
        }

        /// <summary>
        /// Hands the filter to the request that may still be answering: its own copy is what it judges arriving
        /// servers with and what the rules queries it starts are built from, and the queries already out carry a
        /// copy of their own, so a server that answers after a toggle answers to the toggle.
        /// </summary>
        private static void Push(LobbySearchFilter filter)
        {
            try
            {
                var manager = SteamLobby.instance;
                var request = manager != null && RequestField != null ? RequestField.GetValue(manager) : null;
                if (request == null)
                {
                    return;
                }

                if (RequestFilterField != null)
                {
                    RequestFilterField.SetValue(request, filter);
                }

                var running = RunningField != null
                    ? RunningField.GetValue(request) as System.Collections.IEnumerable
                    : null;
                if (running == null || QueryFilterField == null)
                {
                    return;
                }

                foreach (var query in running)
                {
                    if (query != null && QueryFilterField.DeclaringType.IsInstanceOfType(query))
                    {
                        QueryFilterField.SetValue(query, filter);
                    }
                }
            }
            catch (Exception e)
            {
                // Arrivals keep the request's own filter in this case; the next real refresh corrects the list.
                Plugin.Verbose("Could not hand the current filter to the running request: " + e.Message);
            }
        }

        /// <summary>The filter the toggles say, built the way the game builds it before a request.</summary>
        internal static LobbySearchFilter FilterOf(LobbyList list)
        {
            return new LobbySearchFilter
            {
                HideFull = IsOn(list, FullField),
                HideEmpty = IsOn(list, EmptyField),
                HidePasswordProtected = IsOn(list, PasswordField),
                MissionPvpType = (MissionPvpType)Index(list, MissionField),
                ServerType = (FilterServerType)Index(list, ServerField),
                distanceFilter = Distance(Index(list, DistanceField)),
            };
        }

        /// <summary>The filter the current list was last built with, which is what the admissions read.</summary>
        internal static LobbySearchFilter Current(LobbyList list = null)
        {
            if (list != null)
            {
                _list = list;
            }

            var source = _list;
            if (ActiveField != null && source != null)
            {
                var value = ActiveField.GetValue(source);
                if (value is LobbySearchFilter filter)
                {
                    return filter;
                }
            }

            return source != null ? FilterOf(source) : default(LobbySearchFilter);
        }

        /// <summary>
        /// Whether a lobby passes the filter, mirroring the game's own checks: the password, the count and the
        /// ping ones from the rules answer path, and the mission type, the server type and the distance the
        /// master request carries.
        /// </summary>
        internal static bool Passes(LobbyInstance lobby, LobbySearchFilter filter)
        {
            if (lobby == null)
            {
                return false;
            }

            var server = lobby as ServerLobbyInstance;
            if (server != null && server.details == null)
            {
                // Nothing has arrived for this entry yet, so there is nothing to check it against.
                return true;
            }

            if (filter.ServerType == FilterServerType.DedicatedServerOnly && server == null)
            {
                return false;
            }

            if (filter.ServerType == FilterServerType.PlayerHostedOnly && server != null)
            {
                return false;
            }

            if (filter.MissionPvpType != MissionPvpType.All && lobby.MissionPvpType != filter.MissionPvpType)
            {
                return false;
            }

            string password;
            if (filter.HidePasswordProtected && lobby.IsPasswordProtected(out password))
            {
                return false;
            }

            if (filter.HideFull || filter.HideEmpty)
            {
                int current;
                int max;
                if (lobby.GetPlayerCounts(out current, out max))
                {
                    if (filter.HideFull && current >= max)
                    {
                        return false;
                    }

                    if (filter.HideEmpty && current == 0)
                    {
                        return false;
                    }
                }
            }

            if (server != null && !filter.PingDistanceAllowed(server.details.m_nPing))
            {
                return false;
            }

            return true;
        }

        private static bool Same(LobbySearchFilter a, LobbySearchFilter b)
        {
            return a.HideFull == b.HideFull
                && a.HideEmpty == b.HideEmpty
                && a.HidePasswordProtected == b.HidePasswordProtected
                && a.MissionPvpType == b.MissionPvpType
                && a.ServerType == b.ServerType
                && a.distanceFilter == b.distanceFilter;
        }

        private static bool IsOn(LobbyList list, FieldInfo field)
        {
            var toggle = field != null ? field.GetValue(list) as BaseToggle : null;
            return toggle != null && toggle.isOn;
        }

        private static int Index(LobbyList list, FieldInfo field)
        {
            var group = field != null ? field.GetValue(list) as BetterToggleGroup : null;
            return group != null ? group.GetIndex() : 0;
        }

        /// <summary>The game's own mapping from the distance toggles to the filter value.</summary>
        private static ELobbyDistanceFilter? Distance(int index)
        {
            if (index == 0)
            {
                return ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault;
            }

            if (index == 1)
            {
                return ELobbyDistanceFilter.k_ELobbyDistanceFilterFar;
            }

            return null;
        }
    }

    /// <summary>
    /// Takes over the browser's filter changes: when the toggles moved, the list is filtered from what is already
    /// in hand instead of the game clearing it and asking Steam for everything again.
    /// </summary>
    [HarmonyPatch(typeof(LobbyList), nameof(LobbyList.GetListOfLobbies))]
    internal static class LocalFilterPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(LobbyList __instance)
        {
            try
            {
                var call = LocalFilters.Classify(__instance);
                if (call == LocalFilters.FilterCall.Toggle)
                {
                    LocalFilters.Apply(__instance);
                    return false;
                }

                if (call == LocalFilters.FilterCall.Refresh)
                {
                    // A press: the game's own request runs below, and the pass fills the gaps it leaves. A
                    // screen opening is not a press, and picks nothing.
                    ServerBrowser.RefreshListed();
                }
            }
            catch (Exception e)
            {
                // The vanilla request still runs below, which is the recovery: it rebuilds the whole list.
                Plugin.Log.LogError("Could not filter the list without a refresh: " + e);
            }

            return true;
        }
    }
}
