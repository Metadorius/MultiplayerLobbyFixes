using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;

namespace MultiplayerLobbyFixes
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kerbiter.multiplayerlobbyfixes";
        public const string PluginName = "Multiplayer Lobby Fixes";

        /// <summary>Lives in three places: this constant, &lt;Version&gt; in the csproj, and version in meta.json.</summary>
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log { get; private set; }

        /// <summary>Whether dedicated servers this machine cannot query are listed in the server browser anyway.</summary>
        internal static ConfigEntry<bool> ListUnreachableServers { get; private set; }

        /// <summary>Whether a server is listed as soon as Steam's master list returns it, before it answers.</summary>
        internal static ConfigEntry<bool> ListBeforeAnswering { get; private set; }

        /// <summary>Whether a refresh keeps the rows already on screen instead of clearing the list first.</summary>
        internal static ConfigEntry<bool> KeepServersListed { get; private set; }

        /// <summary>Whether the details window's Join button stays clickable when the player count says full.</summary>
        internal static ConfigEntry<bool> JoinFullServers { get; private set; }

        /// <summary>Whether the state of each server's latest query is shown on its row.</summary>
        internal static ConfigEntry<bool> ShowLatestQueryStatus { get; private set; }

        /// <summary>Whether the details window gets a button that asks that one server for its rules and its status again.</summary>
        internal static ConfigEntry<bool> RefreshServerButton { get; private set; }

        /// <summary>Whether the per-server queries go through the spacing queue instead of out at once.</summary>
        internal static ConfigEntry<bool> ThrottleQueries { get; private set; }

        /// <summary>Milliseconds between the browser's per-server queries, which spreads a refresh's burst out.</summary>
        internal static ConfigEntry<float> QuerySpacingMs { get; private set; }

        internal static ConfigEntry<bool> VerboseLogging { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            ListUnreachableServers = Config.Bind("Server browser", "ListUnreachableServers", true,
                "Steam's master list is asked for servers, and each one is then queried from this machine for its "
                + "rules and its ping. A dedicated server that answers neither is dropped from the browser; with "
                + "this on it is listed anyway, with a blank ping and nothing in the mission, map, duration and "
                + "player columns, and can still be joined if it is reachable through Steam. Player hosted games "
                + "are listed from Steam's lobby data and are unaffected by this setting.");

            ListBeforeAnswering = Config.Bind("Server browser", "ListBeforeAnswering", true,
                "List a server as soon as Steam's master list returns it, instead of waiting for the rules query "
                + "that fills in its mission, map and player count. The row starts with the name, player count and "
                + "password flag the master list holds, and fills in as answers arrive, or keeps the last mission, "
                + "map and start time it answered with.");

            KeepServersListed = Config.Bind("Server browser", "KeepServersListed", true,
                "Keep the rows already on screen when you refresh, instead of clearing the list and adding back only "
                + "the servers that answer, and keep the servers themselves when you leave the multiplayer screen "
                + "and come back to it. A refresh that returns nothing then leaves the list as it was, rather than "
                + "emptying it. Changing a filter still clears the list.");

            JoinFullServers = Config.Bind("Server browser", "JoinFullServers", true,
                "Leave the Join button in the server details window clickable when the player count says the server "
                + "is full. That count is what the server last registered with Steam, not what it has now, so a "
                + "server reading 32/32 often has room. A server that really is full refuses the join itself.");

            ShowLatestQueryStatus = Config.Bind("Server browser", "ShowLatestQueryStatus", true,
                "Show the ping query's state in the ping column: a pulsing icon while a query is waiting for an "
                + "answer, and a warning triangle with a tooltip once the last query came back empty. Servers the "
                + "ping loop never asks, like the unreachable ones listed from the master list, keep the triangle.");

            RefreshServerButton = Config.Bind("Server browser", "RefreshServerButton", true,
                "Add a Refresh button to the server details window, left of Join, which asks that one server for its "
                + "rules and its status again: the mission, the map, the description, the duration, the player count "
                + "and the ping, without touching the rest of the list. Two queries per press.");

            ThrottleQueries = Config.Bind("Server browser", "ThrottleQueries", true,
                "Hold the game's per-server queries in a queue and start them one at a time, instead of letting a "
                + "refresh start them all at once; the game's own ping loop waits in the queue too. A query the "
                + "game cancels while it waits is dropped. With this off every query starts at once.");

            QuerySpacingMs = Config.Bind("Server browser", "QuerySpacingMs", 5f,
                "Milliseconds between the queries the queue starts, which spreads a refresh's burst out. A query "
                + "that arrives sooner waits its turn rather than being dropped. The queue starts at most one "
                + "query a frame however small this is, so 0 means one query a frame. The ThrottleQueries toggle "
                + "turns the queue off altogether.");

            VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", true,
                "Log what the browser and the Steam joins are doing.");

            _harmony = new Harmony(PluginGuid);
            ApplyPatches();
            ModHost.Ensure();
            ReportPatchState();

            try
            {
                var location = typeof(Plugin).Assembly.Location;
                Log.LogInfo("Loaded build: " + location + " (mtime "
                    + System.IO.File.GetLastWriteTime(location).ToString("HH:mm:ss") + ")");
            }
            catch
            {
                // Location is unavailable for in-memory assemblies.
            }
        }

        /// <summary>
        /// Patches every class in this assembly, one at a time. PatchAll stops at the first class it cannot patch,
        /// which takes every class after it down too, so a bad postfix argument would leave the mod half applied.
        /// </summary>
        private void ApplyPatches()
        {
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                {
                    continue;
                }

                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    Log.LogError("Could not apply the patches in " + type.Name + ": " + e);
                }
            }
        }

        /// <summary>Startup check so a silent no-op is distinguishable from a broken patch.</summary>
        private static void ReportPatchState()
        {
            // The patches are listed by the method they land on, because the point is to tell a silent no-op from
            // a patch that never applied. HarmonyX resolves arguments by name, so a game-side rename loses one.
            var listFailureInfo = PatchInfo(AccessTools.Method(
                AccessTools.Inner(typeof(SteamLobby), "ServerListRequest"), "OnServerFailedToRespond"));
            var rulesFailureInfo = PatchInfo(AccessTools.Method(
                AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "RulesFailedToRespond"));
            var rulesAnswerTarget = AccessTools.Method(
                AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "RulesRefreshComplete");
            var rulesAnswerInfo = rulesAnswerTarget != null ? PatchProcessor.GetPatchInfo(rulesAnswerTarget) : null;
            var pingInfo = PatchInfo(AccessTools.Method(
                typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.CalculatePing)));
            var detailsInfo = PatchInfo(AccessTools.Method(
                typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.SetDetails)));
            var refreshInfo = PatchInfo(AccessTools.Method(typeof(LobbyDetailsModal), "Show"));
            var keepRowsInfo = PatchInfo(AccessTools.Method(typeof(LobbyList), "RefreshLobbyListCleared"));
            var lobbyUpdateTarget = AccessTools.Method(typeof(LobbyList), "Update");
            var lobbyUpdateInfo = lobbyUpdateTarget != null ? PatchProcessor.GetPatchInfo(lobbyUpdateTarget) : null;
            var resortDataInfo = PatchInfo(AccessTools.Method(typeof(LobbyList), "OnLobbyDataUpdated"));
            var resortPingInfo = PatchInfo(AccessTools.Method(typeof(LobbyList), "OnLobbyPingUpdated"));
            var keepCacheInfo = PatchInfo(AccessTools.Method(typeof(SteamLobby), nameof(SteamLobby.ClearLobbyCache)));
            var reopenInfo = PatchInfo(AccessTools.Method(typeof(LobbyList), "Start"));
            var filterInfo = PatchInfo(AccessTools.Method(typeof(LobbyList), nameof(LobbyList.GetListOfLobbies)));
            var staleInfo = PatchInfo(AccessTools.Method(typeof(LobbyListItem), "SetPingTextAndColor"));
            var pingQueryType = AccessTools.Inner(typeof(SteamLobby), "PingQuery");
            var pingAskInfo = PatchInfo(AccessTools.Method(pingQueryType, "Run"));
            var pingFailureInfo = PatchInfo(AccessTools.Method(pingQueryType, "ServerFailedToRespond"));
            var rulesCancelInfo = PatchInfo(AccessTools.Method(
                AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "Cancel"));
            var queryCancelInfo = PatchInfo(AccessTools.Method(pingQueryType, "Cancel"));
            var requestCancelInfo = PatchInfo(AccessTools.Method(
                AccessTools.Inner(typeof(SteamLobby), "ServerListRequest"), "Cancel"));
            var pingHoldInfo = PatchInfo(AccessTools.Method(typeof(SteamLobby), "CheckPingServers"));
            var rulesAskInfo = PatchInfo(AccessTools.Method(
                AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "Run"));
            var missionMarkerInfo = PatchInfo(AccessTools.Method(
                typeof(LobbyListItem), nameof(LobbyListItem.Show)));
            var joinAskInfo = PatchInfo(AccessTools.Method(typeof(SteamLobby), nameof(SteamLobby.CliJoinBySteamId)));
            var joinAttemptInfo = PatchInfo(AccessTools.Method(typeof(SteamLobby), nameof(SteamLobby.TryJoinLobby)));
            var joinRequestInfo = PatchInfo(AccessTools.Method(typeof(SteamLobby), "OnJoinRequest"));
            var joinRetryInfo = PatchInfo(AccessTools.Method(
                AccessTools.Inner(typeof(SteamLobby), "ServerListRequest"), "OnRefreshComplete"));
            var joinFailureInfo = PatchInfo(AccessTools.Method(typeof(NetworkManagerNuclearOption), "ClientDisconnected"));

            Log.LogInfo(
                "Patches: server list failure=" + listFailureInfo
                + ", rules failure=" + rulesFailureInfo
                + ", rules answer hooks="
                + (rulesAnswerInfo != null ? rulesAnswerInfo.Prefixes.Count + rulesAnswerInfo.Postfixes.Count : -1)
                + ", ping postfixes=" + pingInfo
                + ", details repairs=" + detailsInfo
                + ", detail refreshes=" + refreshInfo
                + ", keep rows prefixes=" + keepRowsInfo
                + ", join full postfixes=" + (lobbyUpdateInfo != null ? lobbyUpdateInfo.Postfixes.Count : -1)
                + ", resort prefixes=" + (lobbyUpdateInfo != null ? lobbyUpdateInfo.Prefixes.Count : -1)
                + ", resort triggers=" + resortDataInfo + "/" + resortPingInfo
                + ", keep cache prefixes=" + keepCacheInfo
                + ", reopen postfixes=" + reopenInfo
                + ", filter prefixes=" + filterInfo
                + ", stale markers=" + staleInfo
                + ", rules markers=" + missionMarkerInfo
                + ", ping asks=" + pingAskInfo
                + ", ping failures=" + pingFailureInfo
                + ", rules asks=" + rulesAskInfo
                + ", queued queries=" + rulesAskInfo + "/" + pingAskInfo
                + ", query cancels=" + rulesCancelInfo + "/" + queryCancelInfo
                + ", request cancels=" + requestCancelInfo
                + ", ping holds=" + pingHoldInfo
                + ", join asks=" + joinAskInfo
                + ", join attempts=" + joinAttemptInfo
                + ", join requests=" + joinRequestInfo
                + ", join retries=" + joinRetryInfo
                + ", join failures=" + joinFailureInfo
                + " | listUnreachableServers=" + ListUnreachableServers.Value
                + ", listBeforeAnswering=" + ListBeforeAnswering.Value
                + ", keepServersListed=" + KeepServersListed.Value
                + ", joinFullServers=" + JoinFullServers.Value
                + ", showLatestQueryStatus=" + ShowLatestQueryStatus.Value
                + ", refreshServerButton=" + RefreshServerButton.Value
                + ", throttleQueries=" + ThrottleQueries.Value
                + ", querySpacingMs=" + QuerySpacingMs.Value);
        }

        /// <summary>How many prefixes and postfixes landed on a method, or -1 when the method is not there.</summary>
        private static int PatchInfo(System.Reflection.MethodBase target)
        {
            var info = target != null ? PatchProcessor.GetPatchInfo(target) : null;
            return info != null ? info.Prefixes.Count + info.Postfixes.Count : -1;
        }

        internal static void Verbose(string message)
        {
            if (VerboseLogging != null && VerboseLogging.Value)
            {
                Log.LogInfo(message);
            }
        }

        /// <summary>Fires before the main menu appears. Unpatching here would strip the patches before they ran.</summary>
        private void OnDestroy()
        {
            Log.LogInfo("Plugin host torn down by the loader. Patches left in place.");
        }
    }
}
