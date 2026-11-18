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
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log { get; private set; }

        /// <summary>Whether dedicated servers this machine cannot query are listed in the server browser anyway.</summary>
        internal static ConfigEntry<bool> ListUnreachableServers { get; private set; }

        /// <summary>Whether the details window's Join button stays clickable when the player count says full.</summary>
        internal static ConfigEntry<bool> JoinFullServers { get; private set; }

        /// <summary>Whether the state of each server's rules query is shown on its row.</summary>
        internal static ConfigEntry<bool> ShowLatestQueryStatus { get; private set; }

        /// <summary>Whether the details window gets a button that asks that one server for its rules again.</summary>
        internal static ConfigEntry<bool> RefreshServerButton { get; private set; }

        /// <summary>How long one server's rules are left alone before the card's button may ask again.</summary>
        internal static ConfigEntry<float> RefreshCooldownSeconds { get; private set; }

        internal static ConfigEntry<bool> VerboseLogging { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            ListUnreachableServers = Config.Bind("Server browser", "ListUnreachableServers", true,
                "Steam's master list returns a server, but its own query port does not answer from this machine, "
                + "so the game drops it and only writes a line in the log. With this on, such a server is listed "
                + "anyway, built from the copy Steam handed over: its name, player count and password flag, plus "
                + "the map and mission its Steam tags carry. Its ping stays blank and its row keeps a warning "
                + "triangle, and joining it works whenever Steam can reach it. Player hosted games are listed from "
                + "Steam's lobby data and are not affected.");

            JoinFullServers = Config.Bind("Server browser", "JoinFullServers", true,
                "Leave the Join button in the server details window clickable when the player count says the server "
                + "is full. That count is what the server last registered with Steam, not what it has now, so a "
                + "server reading 32/32 often has room. A server that really is full refuses the join itself.");

            ShowLatestQueryStatus = Config.Bind("Server browser", "ShowLatestQueryStatus", true,
                "Mark a row whose server is not answering: a warning triangle in the ping column when the master "
                + "list could not reach it, and one in the mission cell when its rules query failed or is still "
                + "out. The same marks appear in the details window. A server with nothing wrong shows nothing.");

            RefreshServerButton = Config.Bind("Server browser", "RefreshServerButton", true,
                "Add a Refresh button to the server details window, left of Join, which asks that one server for "
                + "its rules again: the mission, the map, the description and the duration. It reads the card "
                + "again when the answer lands, and does nothing to the rest of the list.");

            RefreshCooldownSeconds = Config.Bind("Server browser", "RefreshCooldownSeconds", 3f,
                new ConfigDescription(
                    "How long one server's rules are left alone after the card's Refresh button asked for them, so "
                    + "a held down button cannot spend a query a frame on the same server.",
                    new AcceptableValueRange<float>(0f, 60f)));

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

        /// <summary>
        /// Startup check so a silent no-op is distinguishable from a broken patch. The patches are listed by the
        /// method they land on, because HarmonyX resolves arguments by name and a game-side rename loses one.
        /// </summary>
        private static void ReportPatchState()
        {
            var requestType = AccessTools.Inner(typeof(SteamLobby), "ServerListRequest");
            var queryType = AccessTools.Inner(typeof(SteamLobby), "SearchQuery");

            Log.LogInfo(
                "Patches: list failure=" + PatchInfo(AccessTools.Method(requestType, "OnServerFailedToRespond"))
                + ", list answer=" + PatchInfo(AccessTools.Method(requestType, "OnServerResponded"))
                + ", refresh complete=" + PatchInfo(AccessTools.Method(requestType, "OnRefreshComplete"))
                + ", details=" + PatchInfo(AccessTools.Method(
                    typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.SetDetails)))
                + ", ping cell=" + PatchInfo(AccessTools.Method(
                    typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.CalculatePing)))
                + ", ping answer=" + PatchInfo(AccessTools.Method(
                    typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.SetPingResult)))
                + ", name=" + PatchInfo(AccessTools.Method(typeof(ServerLobbyInstance), "get_LobbyName"))
                + ", row ping=" + PatchInfo(AccessTools.Method(typeof(LobbyListItem), "SetPingTextAndColor"))
                + ", row show=" + PatchInfo(AccessTools.Method(typeof(LobbyListItem), nameof(LobbyListItem.Show)))
                + ", card show=" + PatchInfo(AccessTools.Method(typeof(LobbyDetailsModal), "Show"))
                + ", card hide=" + PatchInfo(AccessTools.Method(typeof(LobbyDetailsModal), "Hide"))
                + ", list awake=" + PatchInfo(AccessTools.Method(typeof(LobbyList), "Awake"))
                + ", list destroy=" + PatchInfo(AccessTools.Method(typeof(LobbyList), "OnDestroy"))
                + ", join ask=" + PatchInfo(AccessTools.Method(typeof(SteamLobby), nameof(SteamLobby.CliJoinBySteamId)))
                + ", join attempt=" + PatchInfo(AccessTools.Method(typeof(SteamLobby), nameof(SteamLobby.TryJoinLobby)))
                + ", join request=" + PatchInfo(AccessTools.Method(typeof(SteamLobby), "OnJoinRequest"))
                + ", join rules=" + PatchInfo(AccessTools.Method(queryType, "RulesFailedToRespond"))
                + ", join failure=" + PatchInfo(AccessTools.Method(
                    typeof(NetworkManagerNuclearOption), "ClientDisconnected"))
                + " | listUnreachableServers=" + ListUnreachableServers.Value
                + ", joinFullServers=" + JoinFullServers.Value
                + ", showLatestQueryStatus=" + ShowLatestQueryStatus.Value
                + ", refreshServerButton=" + RefreshServerButton.Value
                + ", refreshCooldownSeconds=" + RefreshCooldownSeconds.Value);
        }

        /// <summary>How many prefixes and postfixes landed on a method, or -1 when the method is not there.</summary>
        private static int PatchInfo(MethodBase target)
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
