using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SceneLoading;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Keeps the browser usable when Steam's master list knows about a server this machine cannot query, and keeps
    /// the copies it hands over honest.
    ///
    /// Since 0.34.6 a refresh is one master list request: the servers' mission, map, player count, password flag
    /// and start time arrive inside the game tags of each server's entry, and the entry's own details carry the
    /// ping. Nothing else is asked of a listed server while the browser is open. What is left is the server whose
    /// query port does not answer from this machine at all, usually one behind a relay or a firewall, where the
    /// game's own connection goes through Steam while a direct query never comes back: vanilla drops it, and only
    /// writes "Request Server List, fail index" in the log. Such a server is listed here anyway, off the copy
    /// Steam handed over, and marked so the row can say so.
    ///
    /// Steam also answers for a server it could not reach with zeroes in both count fields, no password flag and
    /// an address where the name was, and SetDetails writes every one of those over whatever the last good answer
    /// said. Repair puts those fields back from what the server said before, for every copy that passes through.
    /// </summary>
    internal static class ServerBrowser
    {
        /// <summary>Steam's way of saying it got no ping from a server, seen on every copy that came with a failure.</summary>
        private const int PingPlaceholder = 2000;

        /// <summary>Fields read through reflection, resolved once.</summary>
        private static readonly FieldInfo AnnounceField = AccessTools.Field(typeof(SteamLobby), "OnLobbyDataUpdated");

        /// <summary>The browser's own dictionary of known lobbies, keyed by the server's Steam ID.</summary>
        private static readonly FieldInfo LobbiesField = AccessTools.Field(typeof(SteamLobby), "_lobbyData");

        /// <summary>The map list a server entry is built with, which is how its tags become a map name.</summary>
        internal static readonly FieldInfo MapLoaderField = AccessTools.Field(typeof(SteamLobby), "mapLoader");

        /// <summary>The last player counts a server gave, by server.</summary>
        private static readonly Dictionary<ulong, Counts> LastCounts = new Dictionary<ulong, Counts>();

        /// <summary>Whether each server's last good copy asked for a password, by server.</summary>
        private static readonly Dictionary<ulong, bool> LastLocked = new Dictionary<ulong, bool>();

        /// <summary>The last name seen for a server, replaced only by another real name.</summary>
        private static readonly Dictionary<ulong, string> LastNames = new Dictionary<ulong, string>();

        /// <summary>The last ping a copy carried, by server, for the copies that come with none.</summary>
        private static readonly Dictionary<ulong, int> LastPings = new Dictionary<ulong, int>();

        /// <summary>
        /// The servers listed because Steam's master list could not reach them, by server. The row says so, its
        /// ping column stays blank, and the mark comes off as soon as the server answers anything.
        /// </summary>
        internal static readonly HashSet<ulong> Unreachable = new HashSet<ulong>();

        private struct Counts
        {
            internal int Current;

            internal int Max;
        }

        /// <summary>
        /// The server's Steam ID, or nil when it has none yet.
        ///
        /// Steam's server details are a class rather than a struct, so an entry built before Steam handed over its
        /// copy has none, and reading through it throws. Every read of it goes through here.
        /// </summary>
        internal static CSteamID IdOf(ServerLobbyInstance lobby)
        {
            var details = lobby != null ? lobby.details : null;
            return details != null ? details.m_steamID : CSteamID.Nil;
        }

        /// <summary>Whether this machine's master list could not reach a server, which is what its row says.</summary>
        internal static bool IsUnreachable(ServerLobbyInstance lobby)
        {
            var id = IdOf(lobby);
            return id.IsValid() && Unreachable.Contains(id.m_SteamID);
        }

        /// <summary>
        /// Whether a ping number is a measurement rather than one of the shapes Steam uses for no ping: nought,
        /// a negative, or the 2000 placeholder a copy that failed to answer carries. Everything else is read as
        /// no answer, the same way the copy repair reads it.
        /// </summary>
        internal static bool PingLooksMeasured(int ping)
        {
            return ping > 0 && ping < PingPlaceholder;
        }

        /// <summary>
        /// Repairs a copy of a server Steam handed over with nothing in it, before vanilla reads it.
        ///
        /// A copy that carries values is believed as it is, so a server that really emptied out, changed its name
        /// or dropped its password still shows that. Only fields with nothing usable in them fall back.
        /// </summary>
        internal static void Repair(gameserveritem_t copy)
        {
            if (copy == null)
            {
                return;
            }

            var id = copy.m_steamID;
            if (!id.IsValid())
            {
                return;
            }

            var replaced = new List<string>();

            if (copy.m_nMaxPlayers <= 0)
            {
                // No real server is set up for nought players, so this is a failure wearing a server's shape.
                Counts counts;
                if (LastCounts.TryGetValue(id.m_SteamID, out counts))
                {
                    copy.m_nPlayers = counts.Current;
                    copy.m_nMaxPlayers = counts.Max;
                    replaced.Add("counts");
                }

                bool locked;
                if (LastLocked.TryGetValue(id.m_SteamID, out locked))
                {
                    copy.m_bPassword = locked;
                    replaced.Add("password");
                }
            }
            else
            {
                LastCounts[id.m_SteamID] = new Counts { Current = copy.m_nPlayers, Max = copy.m_nMaxPlayers };
                LastLocked[id.m_SteamID] = copy.m_bPassword;
            }

            var name = copy.GetServerName();
            if (LooksLikeAddress(name))
            {
                string known;
                if (LastNames.TryGetValue(id.m_SteamID, out known))
                {
                    copy.SetServerName(known);
                    replaced.Add("name");
                }
            }
            else
            {
                LastNames[id.m_SteamID] = name;
            }

            int ping;
            if (PingLooksMeasured(copy.m_nPing))
            {
                LastPings[id.m_SteamID] = copy.m_nPing;
            }
            else if (LastPings.TryGetValue(id.m_SteamID, out ping))
            {
                copy.m_nPing = ping;
                replaced.Add("ping");
            }

            CountCopy(copy, replaced);
        }

        /// <summary>How many copies the master list handed over since the last line, and what they said.</summary>
        private static int _copies;

        private static int _copiesCold;

        private static int _copiesRepaired;

        private static float _lastCopy;

        /// <summary>
        /// Counts one copy Steam handed over, instead of a line of its own: a refresh hands over a copy per server,
        /// and a line each marshals the name, the tags, the map and the description off every one of them, which is
        /// work no log is worth.
        /// </summary>
        private static void CountCopy(gameserveritem_t copy, List<string> replaced)
        {
            _copies++;
            _lastCopy = Time.time;

            if (!copy.m_bHadSuccessfulResponse)
            {
                _copiesCold++;
            }

            if (replaced.Count > 0)
            {
                _copiesRepaired++;
            }
        }

        /// <summary>Writes the copies' tally once the hand-over has stopped, which is a line per refresh.</summary>
        internal static void ReportCopies()
        {
            if (_copies == 0 || Time.time - _lastCopy < 1f)
            {
                return;
            }

            Plugin.Verbose(
                "The master list handed over " + _copies + " entries: " + _copiesCold
                + " of them have never answered this machine, " + _copiesRepaired
                + " needed a field put back from what the server said before.");

            _copies = 0;
            _copiesCold = 0;
            _copiesRepaired = 0;
        }

        /// <summary>
        /// Remembers a server's name. The name a server answers with is the real one; a copy that failed to answer
        /// comes back with its address in that field instead.
        /// </summary>
        internal static void RememberName(CSteamID id, string name)
        {
            if (id.IsValid() && !LooksLikeAddress(name))
            {
                LastNames[id.m_SteamID] = name;
            }
        }

        /// <summary>The name to show for a server, which is its last real one when Steam no longer has it.</summary>
        internal static string NameFor(CSteamID id, string name)
        {
            string known;
            if (LooksLikeAddress(name) && id.IsValid() && LastNames.TryGetValue(id.m_SteamID, out known))
            {
                return known;
            }

            return name;
        }

        /// <summary>
        /// Whether a name is really an endpoint. Steam answers for a server it could not reach with the address it
        /// holds, and the browser shows that the same way it shows a name.
        /// </summary>
        private static bool LooksLikeAddress(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }

            var colon = name.LastIndexOf(':');
            if (colon <= 0 || colon == name.Length - 1)
            {
                return false;
            }

            int port;
            if (!int.TryParse(name.Substring(colon + 1), out port))
            {
                return false;
            }

            foreach (var part in name.Substring(0, colon).Split('.'))
            {
                int octet;
                if (!int.TryParse(part, out octet))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Fires the event the browser builds its rows from, which is all the game does for a server it lists.
        /// </summary>
        internal static void Announce(SteamLobby manager, LobbyInstance lobby)
        {
            if (manager == null || lobby == null || AnnounceField == null)
            {
                return;
            }

            var updated = AnnounceField.GetValue(manager) as Action<LobbyInstance>;
            if (updated != null)
            {
                updated(lobby);
            }
        }

        internal static void Report(ServerLobbyInstance lobby, string why)
        {
            Plugin.Verbose(
                "Listing " + lobby.LobbyNameSanitized + " (" + IdOf(lobby).m_SteamID + ") " + why);
        }

        /// <summary>The browser's dictionary of known entries, which is where a new row's entry has to go.</summary>
        internal static Dictionary<CSteamID, LobbyInstance> LobbiesOf(SteamLobby manager)
        {
            return manager != null && LobbiesField != null
                ? LobbiesField.GetValue(manager) as Dictionary<CSteamID, LobbyInstance>
                : null;
        }

        /// <summary>
        /// A fresh entry for a server the game has no entry for, built the way the game builds one: with the map
        /// list, which is what turns the map id in the server's tags into a map name.
        /// </summary>
        internal static ServerLobbyInstance NewEntry(SteamLobby manager)
        {
            var loader = manager != null && MapLoaderField != null
                ? MapLoaderField.GetValue(manager) as MapLoader
                : null;
            return new ServerLobbyInstance(loader);
        }
    }

    /// <summary>
    /// Gives a server that failed the master list's per-server query an entry and a row, the way a server that
    /// answered gets one, and rescues a direct join whose server failed that same probe.
    ///
    /// Vanilla drops a failed entry and only logs it, so a server this machine cannot query is missing from the
    /// browser entirely. The master list's copy of it already carries its name, player count, password flag and
    /// Steam tags, which is everything a row needs except the ping.
    ///
    /// Both nested types are private, so the target is resolved by name rather than by an attribute argument.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerListFailurePatch
    {
        private static readonly Type RequestType = AccessTools.Inner(typeof(SteamLobby), "ServerListRequest");

        private static readonly FieldInfo ManagerField = AccessTools.Field(RequestType, "manager");

        private static readonly FieldInfo CliJoinField = AccessTools.Field(RequestType, "_cliJoin");

        private static readonly Type JoinType = AccessTools.Inner(typeof(SteamLobby), "CliJoinHandler");

        private static readonly FieldInfo PendingIdField =
            JoinType != null ? AccessTools.Field(JoinType, "pendingId") : null;

        private static readonly MethodInfo FoundMethod =
            JoinType != null ? AccessTools.Method(JoinType, "OnServerFound") : null;

        private static readonly MethodInfo FailedMethod =
            JoinType != null ? AccessTools.Method(JoinType, "OnDiscoveryFailed") : null;

        /// <summary>How long a direct join's server is given to answer this mod before the join is given up on.</summary>
        private const float RescueTimeout = 10f;

        /// <summary>The direct join being re-asked right now; the game runs one of them at a time.</summary>
        private static JoinRescue _rescue;

        /// <summary>A direct join whose server Steam's probe could not reach, joined from its copy instead.</summary>
        private sealed class JoinRescue
        {
            internal ServerLobbyInstance Lobby;

            internal object Join;

            internal float When;

            internal bool Done;

            internal ISteamMatchmakingRulesResponse Rules;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(RequestType, "OnServerFailedToRespond");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance, HServerListRequest hRequest, int iServer)
        {
            try
            {
                if (Rescue(__instance, hRequest, iServer))
                {
                    // The join this request exists for is being decided here now, so it is no browser entry.
                    return;
                }
            }
            catch (Exception e)
            {
                // Nothing this mod does is worth taking a game callback down with it.
                Plugin.Log.LogError("Could not re-ask a server a direct join was looking for: " + e);
            }

            if (!Plugin.ListUnreachableServers.Value)
            {
                return;
            }

            try
            {
                List(__instance, hRequest, iServer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not list a server that Steam reported as unreachable: " + e);
            }
        }

        /// <summary>
        /// The join-by-Steam-ID flow, the one a friend list or the overlay's Join Game starts, asks Steam's master
        /// list for one server by its ID and lets Steam probe it. A probe that fails turns into "server could not be
        /// found" over a server that is very much up.
        ///
        /// The probe is not needed: the master list's copy of the server carries the Steam ID the join connects to,
        /// and the join's own checks, the version and the password flag, read the copy's tags, which SetDetails has
        /// just parsed in. So the join starts right here, off the copy; only a password-protected server still asks
        /// its rules, the one thing the copy does not carry, because that is where its password check lives. The
        /// request's own failure path is stood down first, so it cannot race this one to the verdict.
        /// </summary>
        private static bool Rescue(object instance, HServerListRequest hRequest, int iServer)
        {
            var join = CliJoinField != null ? CliJoinField.GetValue(instance) : null;
            if (join == null || PendingIdField == null || FoundMethod == null || FailedMethod == null)
            {
                return false;
            }

            var details = SteamMatchmakingServers.GetServerDetails(hRequest, iServer);
            if (details == null || !details.m_steamID.IsValid())
            {
                return false;
            }

            var id = details.m_steamID;
            var pending = (CSteamID)PendingIdField.GetValue(join);
            if (pending != id)
            {
                return false;
            }

            var manager = ManagerField != null ? ManagerField.GetValue(instance) as SteamLobby : null;
            var lobbies = ServerBrowser.LobbiesOf(manager);
            if (lobbies == null)
            {
                return false;
            }

            LobbyInstance cached;
            if (!lobbies.TryGetValue(id, out cached))
            {
                cached = ServerBrowser.NewEntry(manager);
                lobbies.Add(id, cached);
            }

            var lobby = cached as ServerLobbyInstance;
            if (lobby == null)
            {
                return false;
            }

            lobby.SetDetails(details);

            // A newer join takes over the slot; the older rescue has nothing left to answer for.
            if (_rescue != null)
            {
                _rescue.Done = true;
            }

            CliJoinField.SetValue(instance, null);

            var rescue = new JoinRescue
            {
                Lobby = lobby,
                Join = join,
                When = Time.time,
            };
            _rescue = rescue;

            if (!details.m_bPassword)
            {
                AnswerRescue(rescue, "is joined directly, off the master list's own copy");
                return true;
            }

            Plugin.Verbose(
                "The join's server failed Steam's probe and asks for a password, so its rules are asked: "
                + lobby.LobbyNameSanitized + " (" + id.m_SteamID + ").");

            var address = details.m_NetAdr;

            rescue.Rules = new ISteamMatchmakingRulesResponse(
                new ISteamMatchmakingRulesResponse.RulesResponded((key, value) => lobby.SetRule(key, value)),
                new ISteamMatchmakingRulesResponse.RulesFailedToRespond(() => GiveUpRescue(rescue)),
                new ISteamMatchmakingRulesResponse.RulesRefreshComplete(() => AnswerRescue(rescue, "answered its rules")));

            // A join is a player waiting on the answer, so the query goes out on the spot.
            SteamMatchmakingServers.ServerRules(address.GetIP(), address.GetQueryPort(), rescue.Rules);

            return true;
        }

        private static void AnswerRescue(JoinRescue rescue, string why)
        {
            if (rescue.Done)
            {
                return;
            }

            rescue.Done = true;
            if (ReferenceEquals(_rescue, rescue))
            {
                _rescue = null;
            }

            Plugin.Verbose(
                "The join's server " + why + ": " + rescue.Lobby.LobbyNameSanitized + " ("
                + ServerBrowser.IdOf(rescue.Lobby).m_SteamID + ").");

            try
            {
                FoundMethod.Invoke(rescue.Join, new object[] { rescue.Lobby });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not hand a found server to the join: " + e);
            }
        }

        private static void GiveUpRescue(JoinRescue rescue)
        {
            if (rescue.Done)
            {
                return;
            }

            rescue.Done = true;
            if (ReferenceEquals(_rescue, rescue))
            {
                _rescue = null;
            }

            Plugin.Verbose(
                "The join's server did not answer its rules, so the join fails the way it would have: "
                + ServerBrowser.IdOf(rescue.Lobby).m_SteamID);

            try
            {
                FailedMethod.Invoke(rescue.Join, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not tell a join its server was not found: " + e);
            }
        }

        /// <summary>Ends a direct join's rescue that ran out of time, from the mod's per-frame host.</summary>
        internal static void TickRescue()
        {
            var rescue = _rescue;
            if (rescue == null || rescue.Done || Time.time - rescue.When <= RescueTimeout)
            {
                return;
            }

            GiveUpRescue(rescue);
        }

        private static void List(object instance, HServerListRequest hRequest, int iServer)
        {
            var details = SteamMatchmakingServers.GetServerDetails(hRequest, iServer);
            if (details == null || !details.m_steamID.IsValid() || ManagerField == null)
            {
                return;
            }

            // A direct join by Steam ID: vanilla tells the player the server is missing once the request ends,
            // so a row for it would only fight that message.
            if (CliJoinField != null && CliJoinField.GetValue(instance) != null)
            {
                return;
            }

            var manager = ManagerField.GetValue(instance) as SteamLobby;
            var lobbies = ServerBrowser.LobbiesOf(manager);
            if (lobbies == null)
            {
                return;
            }

            LobbyInstance cached;
            if (!lobbies.TryGetValue(details.m_steamID, out cached))
            {
                cached = ServerBrowser.NewEntry(manager);
                lobbies.Add(details.m_steamID, cached);
            }

            var lobby = cached as ServerLobbyInstance;
            if (lobby == null)
            {
                return;
            }

            // The copy is repaired on its way in, so a server that is already known keeps its counts, name and
            // password while still taking the fresh address.
            lobby.SetDetails(details);

            // InList is what the prune at the end of the refresh reads: a row left out of it is removed when the
            // request completes, which is exactly the flicker this listing exists to avoid.
            lobby.InList = true;

            var id = ServerBrowser.IdOf(lobby);
            if (ServerBrowser.Unreachable.Add(id.m_SteamID))
            {
                ServerBrowser.Report(
                    lobby,
                    "did not answer this machine, so it is listed from the master list's copy. Its ping stays "
                    + "blank.");
            }

            ServerBrowser.Announce(manager, lobby);
        }
    }

    /// <summary>
    /// Takes the mark off a server the master list did reach this time, so a server that comes back stops being
    /// shown as unreachable. It runs before vanilla builds the row, which is what keeps the marker off the row
    /// this answer is about to paint.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerListAnswerPatch
    {
        private static readonly Type RequestType = AccessTools.Inner(typeof(SteamLobby), "ServerListRequest");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(RequestType, "OnServerResponded");
        }

        [HarmonyPrefix]
        private static void Prefix(HServerListRequest hRequest, int iServer)
        {
            try
            {
                var details = SteamMatchmakingServers.GetServerDetails(hRequest, iServer);
                if (details != null && details.m_steamID.IsValid())
                {
                    ServerBrowser.Unreachable.Remove(details.m_steamID.m_SteamID);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not note a server that answered: " + e);
            }
        }
    }

    /// <summary>
    /// Repairs the copy of a server Steam hands over before vanilla reads it, which is the one place every copy
    /// passes through: the master list's own entries and the ones the mod builds for a failed entry alike.
    /// </summary>
    [HarmonyPatch(typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.SetDetails))]
    internal static class ServerDetailsPatch
    {
        [HarmonyPrefix]
        private static void Prefix(gameserveritem_t details)
        {
            try
            {
                ServerBrowser.Repair(details);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not repair a server's details: " + e);
            }
        }
    }

    /// <summary>
    /// Keeps a server's name through the refreshes that cannot reach it. Steam answers for a server it could not
    /// reach with the address it holds in the name field, so without this a server turns into
    /// "192.169.81.58:38012" a couple of refreshes in.
    /// </summary>
    [HarmonyPatch(typeof(ServerLobbyInstance), "get_LobbyName")]
    internal static class ServerNamePatch
    {
        [HarmonyPostfix]
        private static void Postfix(ServerLobbyInstance __instance, ref string __result)
        {
            __result = ServerBrowser.NameFor(ServerBrowser.IdOf(__instance), __result);
        }
    }

    /// <summary>
    /// Blanks the ping column of a server this machine could not query, instead of showing the copy's placeholder
    /// or a number from whenever the server last registered itself. A ping the game measured itself, which the
    /// details window asks for every ten seconds, is left alone: it proves the server is up.
    /// </summary>
    [HarmonyPatch(typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.CalculatePing))]
    internal static class ServerPingPatch
    {
        private static readonly FieldInfo RealPingField = AccessTools.Field(typeof(ServerLobbyInstance), "hasRealPing");

        [HarmonyPostfix]
        private static void Postfix(ServerLobbyInstance __instance, ref int? __result)
        {
            if (!ServerBrowser.IsUnreachable(__instance))
            {
                return;
            }

            if (RealPingField != null && (bool)RealPingField.GetValue(__instance))
            {
                return;
            }

            __result = null;
        }
    }

    /// <summary>
    /// Takes the mark off a server the game measured a ping for. The details window asks one every ten seconds,
    /// and an answer means the server is up whatever the master list's probe said.
    /// </summary>
    [HarmonyPatch(typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.SetPingResult))]
    internal static class ServerPingResultPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ServerLobbyInstance __instance, int ping)
        {
            if (!ServerBrowser.PingLooksMeasured(ping))
            {
                return;
            }

            var id = ServerBrowser.IdOf(__instance);
            if (id.IsValid() && ServerBrowser.Unreachable.Remove(id.m_SteamID))
            {
                Plugin.Verbose("A server this machine could not reach answered a ping, so its mark is off.");
            }
        }
    }

    /// <summary>Says which way a Steam join went, so a join that stops silently can be traced in the log.</summary>
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.CliJoinBySteamId))]
    internal static class CliJoinTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(CSteamID lobbyId)
        {
            Plugin.Verbose(
                "A Steam join was asked for " + lobbyId.m_SteamID + ": lobby=" + lobbyId.IsLobby()
                + ", server=" + lobbyId.BGameServerAccount() + ".");
        }
    }

    /// <summary>Says what a join is about to connect to, including the checks that can turn it away.</summary>
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.TryJoinLobby))]
    internal static class JoinTracePatch
    {
        /// <summary>When the last join was asked for, so a failed connect can be told from a mid-session drop.</summary>
        internal static float AttemptedAt = -1f;

        [HarmonyPrefix]
        private static void Prefix(LobbyInstance lobby)
        {
            AttemptedAt = Time.time;

            if (lobby == null)
            {
                return;
            }

            try
            {
                string password;
                lobby.IsPasswordProtected(out password);
                Plugin.Verbose(
                    "Joining " + lobby.LobbyNameSanitized + ": dedicated=" + lobby.DedicatedServer
                    + ", version=" + lobby.HostVersion + " of " + Application.version
                    + ", password=" + password + ", host=" + lobby.HostAddress + ".");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not note a join: " + e);
            }
        }
    }

    /// <summary>
    /// Leaves the game in progress before a join starts, and puts the join through again once the menu is up.
    ///
    /// The network runs one client, and StartClient throws while this machine is already in a session ("Can't
    /// start client because it is already running", which TryJoinLobby reports as "Client Error"). Vanilla never
    /// hit that: its joins all start from the menus. A Steam join does not, and the click should do what it says,
    /// so the session is left the way the quit button leaves it, and the same join is tried again from the menu.
    ///
    /// The waiting join is dropped when the player starts a game of their own or another join goes through, so
    /// an old click cannot pull them out of a session they chose; nothing about the wait is timed, it watches
    /// the network and the scene.
    ///
    /// A join the game would turn away on its own is not intercepted: a version mismatch fails before the
    /// network is touched either way, and the password prompt goes up first, because leaving the game for a
    /// prompt the player may cancel would be worse than the prompt waiting.
    /// </summary>
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.TryJoinLobby))]
    internal static class LeaveThenJoinPatch
    {
        private static LobbyInstance _lobby;

        private static string _password;

        private static bool _promptIfPasswordNeeded;

        private static bool _leaving;

        private static bool _replaying;

        [HarmonyPrefix]
        private static bool Prefix(LobbyInstance lobby, string password, bool promptIfPasswordNeeded)
        {
            try
            {
                if (_replaying)
                {
                    _replaying = false;
                    return true;
                }

                if (lobby == null)
                {
                    return true;
                }

                // A join vanilla will turn away anyway must not cost the game in progress: the version check
                // fails before the network is touched, and the password prompt belongs to the player first.
                if (lobby.HostVersion != Application.version)
                {
                    return true;
                }

                string ignored;
                if (promptIfPasswordNeeded && string.IsNullOrEmpty(password)
                    && lobby.IsPasswordProtected(out ignored))
                {
                    return true;
                }

                var network = NetworkManagerNuclearOption.i;
                if (network == null || (!network.Server.Active && !network.Client.Active))
                {
                    // Nothing in the way; a join still waiting for a leave is superseded by this one.
                    Cancel("another join went through");
                    return true;
                }

                _lobby = lobby;
                _password = password;
                _promptIfPasswordNeeded = promptIfPasswordNeeded;

                if (!_leaving)
                {
                    _leaving = true;
                    Plugin.Verbose("A join came while a game is in progress, so that game is left first.");
                    LeaveAsync(network).Forget();
                }

                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not leave the game in progress for a join: " + e);
                Cancel("the leave failed");
                return true;
            }
        }

        /// <summary>Runs the waiting join once the leave is done and a menu is up.</summary>
        internal static void Tick()
        {
            if (!_leaving)
            {
                return;
            }

            var network = NetworkManagerNuclearOption.i;
            if (network != null && (network.Server.Active || network.Client.Active))
            {
                return;
            }

            var scene = SceneManager.GetActiveScene().path;
            if (SteamLobby.instance == null || (scene != MapLoader.MainMenu && scene != MapLoader.MultiplayerMenu))
            {
                return;
            }

            var lobby = _lobby;
            var password = _password;
            var prompt = _promptIfPasswordNeeded;
            _lobby = null;
            _leaving = false;
            _replaying = true;

            Plugin.Verbose("The game is left, so the join goes on.");
            SteamLobby.instance.TryJoinLobby(lobby, password, prompt);
        }

        /// <summary>
        /// Asks the game to leave through the same task the quit button runs, and catches what the fire-and-forget
        /// Stop cannot: a leave that fails drops the waiting join instead of staying armed, and a leave that
        /// finishes leaves the replay to Tick's state check.
        /// </summary>
        private static async UniTaskVoid LeaveAsync(NetworkManagerNuclearOption network)
        {
            try
            {
                await network.StopAsync(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not leave the game in progress for a join: " + e);
                Cancel("the leave failed");
            }
        }

        /// <summary>Drops a join that is still waiting for a leave, for a newer choice the player made.</summary>
        internal static void Cancel(string why)
        {
            if (!_leaving)
            {
                return;
            }

            Plugin.Verbose("A join waiting for the game in progress to be left is dropped: " + why + ".");
            Clear();
        }

        private static void Clear()
        {
            _lobby = null;
            _password = null;
            _leaving = false;
        }
    }

    /// <summary>
    /// Drops a join that was waiting for a game in progress to be left when the player starts a game of their
    /// own instead: holding the click's intent past a new choice would join them out of a session they picked.
    /// </summary>
    [HarmonyPatch]
    internal static class PendingJoinCancelPatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            var start = AccessTools.Method(typeof(NetworkManagerNuclearOption), "StartHost");
            if (start != null)
            {
                yield return start;
            }

            var startAsync = AccessTools.Method(typeof(NetworkManagerNuclearOption), "StartHostAsync");
            if (startAsync != null)
            {
                yield return startAsync;
            }
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            LeaveThenJoinPatch.Cancel("a game of their own was started");
        }
    }

    /// <summary>
    /// Answers the Steam join the game never registered a handler for.
    ///
    /// The game publishes its join string as Steam's "connect" rich presence, "+join_lobby &lt;id&gt;", which is
    /// what makes Join Game work while the game is not running: Steam starts it with that string and the command
    /// line parser joins. With the game already running, Steam sends the same string through
    /// GameRichPresenceJoinRequested instead, and the game never registered that callback, so the click lands
    /// nowhere and nothing happens at all. The string is taken here and handed to the same join the friends-list
    /// invites use, which asks before leaving a game in progress and stops the network before the join.
    /// </summary>
    internal static class RichPresenceJoin
    {
        private const string JoinPrefix = "+join_lobby ";

        private static Callback<GameRichPresenceJoinRequested_t> _callback;

        /// <summary>Starts listening, once Steam is up; safe to call every frame until it takes.</summary>
        internal static void Ensure()
        {
            if (_callback != null || !SteamAPI.IsSteamRunning())
            {
                return;
            }

            try
            {
                _callback = Callback<GameRichPresenceJoinRequested_t>.Create(OnRequest);
                Plugin.Verbose("Listening for Steam's join requests.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not listen for Steam's join requests: " + e);
            }
        }

        private static void OnRequest(GameRichPresenceJoinRequested_t callback)
        {
            try
            {
                var connect = callback.m_rgchConnect ?? string.Empty;
                Plugin.Verbose("Steam sent a join: \"" + connect + "\".");

                if (!connect.StartsWith(JoinPrefix, StringComparison.Ordinal))
                {
                    // Some other connect string, the kind the game only knows how to take on its command line.
                    return;
                }

                ulong id;
                if (!ulong.TryParse(connect.Substring(JoinPrefix.Length).Trim(), out id))
                {
                    Plugin.Log.LogError("Could not read the lobby id out of \"" + connect + "\".");
                    return;
                }

                var manager = SteamLobby.instance;
                if (manager == null)
                {
                    Plugin.Log.LogError("A Steam join came before the lobby system was up.");
                    return;
                }

                // Through the game's own invite flow: the confirmation and the leave a join from inside a game
                // needs come with it.
                InviteJoinModal.TryJoin(() => manager.CliJoinBySteamId(new CSteamID(id)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not answer a Steam join: " + e);
            }
        }
    }

    /// <summary>
    /// Shows why a join failed when the join was not started from the server browser.
    ///
    /// The game's own handler shows the reason only while the multiplayer menu scene is up; a join started from
    /// the main menu, which is what a Steam invite, a friends-list Join Game and the launch argument are, falls
    /// into the branch that only reloads the main menu, and the join overlay keeps saying "Connecting via Steam"
    /// or "Finding server..." over a server that has already refused. The reason is shown here for those, in the
    /// browser's own words, and only while the join never connected: a disconnect from a session that did connect
    /// is a game dropping its players, not a join refusal, and it keeps the game's own handling. The reason this
    /// same disconnect armed for the game's main menu is consumed once it has been shown here, so one failure
    /// cannot be announced twice.
    /// </summary>
    [HarmonyPatch(typeof(NetworkManagerNuclearOption), "ClientDisconnected")]
    internal static class JoinFailurePatch
    {
        private static bool _listening;

        private static bool _warned;

        private static float _nextTry;

        /// <summary>
        /// Starts listening for connects, which is what tells a refused join from a dropped session.
        ///
        /// The manager is reached through the game's own async resource loader, and asking for it before the main
        /// menu has preloaded it logs an error every time. The lobby object comes up with the manager and is a
        /// plain static, so it is the gate: until it exists, this asks nothing, and it asks at most once a second.
        /// </summary>
        internal static void Ensure()
        {
            if (_listening || Time.time < _nextTry)
            {
                return;
            }

            _nextTry = Time.time + 1f;

            if (SteamLobby.instance == null)
            {
                return;
            }

            try
            {
                var manager = NetworkManagerNuclearOption.i;
                if (manager == null)
                {
                    return;
                }

                manager.Client.Connected.AddListener(OnConnected);
                _listening = true;
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogError("Could not listen for successful joins: " + e);
                }
            }
        }

        private static void OnConnected(INetworkPlayer _)
        {
            // The join worked, so nothing that follows is a join refusal.
            JoinTracePatch.AttemptedAt = -1f;
        }

        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            // Whether this disconnect is the one that will arm the game's disconnect reason: the game only sets
            // it when none is armed, and the main menu consumes it the next time it loads fresh.
            __state = GameManager.disconnectInfo == null;
        }

        [HarmonyPostfix]
        private static void Postfix(ClientStoppedReason disconnectReason, bool __state)
        {
            try
            {
                if (disconnectReason == ClientStoppedReason.LocalConnectionClosed
                    || disconnectReason == ClientStoppedReason.ConnectingCancel)
                {
                    // The local side stopped: a quit, a leave for another join, or a cancel. Nothing was
                    // turned away, so there is nothing to show, and any attempt still watched is over.
                    JoinTracePatch.AttemptedAt = -1f;
                    return;
                }

                if (JoinTracePatch.AttemptedAt < 0f)
                {
                    // No join was watched go out that never connected; the game's own handling stands.
                    return;
                }

                JoinTracePatch.AttemptedAt = -1f;

                if (SceneManager.GetActiveScene().path != MapLoader.MultiplayerMenu)
                {
                    // A join from anywhere but the browser itself, where the game shows the reason already.
                    JoinLobbyOverlay.Open(JoinProgress.GetFailReason(disconnectReason));
                }

                if (__state && GameManager.disconnectInfo != null)
                {
                    // The reason this refused join armed is shown now, so consume it: the main menu would
                    // otherwise show the same failure again the next time it loads. A reason that was
                    // already pending is not this disconnect's to consume, so it stays for the menu.
                    GameManager.ClearDisconnectReason();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not show why a join failed: " + e);
            }
        }
    }

    /// <summary>
    /// Joins the lobby Steam was told to join when it started the game.
    ///
    /// The game publishes "+join_lobby &lt;id&gt;" as its join string, which is what Steam launches the game with
    /// when someone accepts a Join Game while the game is closed, but its own command line parser only knows
    /// "-joinLobby", so that argument is dropped on the floor and the launch does nothing at all. The id is taken
    /// here instead, once the main menu is up, and handed to the same join the in-game requests use.
    /// </summary>
    internal static class LaunchJoin
    {
        private const string JoinArgument = "+join_lobby";

        private static bool _parsed;

        private static bool _warned;

        private static CSteamID _wanted = CSteamID.Nil;

        /// <summary>Parses the launch once, then joins when the game is ready for it.</summary>
        internal static void Tick()
        {
            if (!_parsed)
            {
                _parsed = true;
                _wanted = Wanted(Environment.GetCommandLineArgs());
                if (_wanted.IsValid())
                {
                    Plugin.Verbose("The launch asked to join " + _wanted.m_SteamID + ".");
                }
            }

            if (!_wanted.IsValid())
            {
                return;
            }

            // The game's own launch joins wait for the menu, and Steam has to be up before a request can go out:
            // one asked for any earlier throws out of the request and leaves the overlay on "Finding server...".
            if (SteamLobby.instance == null || !SteamManager.ClientInitialized
                || SceneManager.GetActiveScene().path != MapLoader.MainMenu)
            {
                return;
            }

            var wanted = _wanted;
            Plugin.Verbose("Joining what the launch asked for.");

            try
            {
                SteamLobby.instance.CliJoinBySteamId(wanted);
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogError("Could not join what the launch asked for, another try follows: " + e);
                }

                return;
            }

            _wanted = CSteamID.Nil;
        }

        private static CSteamID Wanted(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], JoinArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ulong id;
                if (ulong.TryParse(args[i + 1], out id))
                {
                    return new CSteamID(id);
                }

                Plugin.Log.LogError("Could not read the lobby id of the launch out of \"" + args[i + 1] + "\".");
            }

            return CSteamID.Nil;
        }
    }

    /// <summary>
    /// Joins a server the master list found whose rules query failed.
    ///
    /// The join flow proves a server is there with its rules query and lets that answer decide, so a server whose
    /// rules endpoint does not answer is declared "could not be found" over a server that is up and joinable,
    /// which is what some dedicated servers look like. The master list's copy of it is in hand by then, and the
    /// join reads that copy: its Steam ID is what the join connects to, and its tags carry the version and the
    /// password flag the join's own checks take. The vanilla failure is skipped and the join goes on.
    /// </summary>
    [HarmonyPatch]
    internal static class RulesFailureJoinRescuePatch
    {
        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "SearchQuery");

        private static readonly FieldInfo LobbyField = AccessTools.Field(QueryType, "lobby");

        private static readonly FieldInfo CliJoinField = AccessTools.Field(QueryType, "cliJoin");

        private static readonly Type JoinType = AccessTools.Inner(typeof(SteamLobby), "CliJoinHandler");

        private static readonly MethodInfo FoundMethod =
            JoinType != null ? AccessTools.Method(JoinType, "OnServerFound") : null;

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(QueryType, "RulesFailedToRespond");
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            if (CliJoinField == null || FoundMethod == null)
            {
                return true;
            }

            try
            {
                var join = CliJoinField.GetValue(__instance);
                if (join == null)
                {
                    return true;
                }

                var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
                var id = ServerBrowser.IdOf(lobby);
                if (lobby == null || !id.IsValid())
                {
                    return true;
                }

                Plugin.Verbose(
                    "The join's server did not answer its rules, so it is joined from the master list's own copy: "
                    + lobby.LobbyNameSanitized + " (" + id.m_SteamID + ").");
                FoundMethod.Invoke(join, new object[] { lobby });
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not carry a join past a failed rules query: " + e);
                return true;
            }
        }
    }

    /// <summary>
    /// Asks the master list for a join's server a second time when the first refresh found nothing.
    ///
    /// The game's not-found verdict comes off one refresh, and Steam's master list hands a single-server query
    /// back empty now and then, answering on the next ask; asking twice is what a player retrying the join does
    /// by hand. Only the first empty refresh is retried, and the game's own failure stands after the second.
    /// </summary>
    [HarmonyPatch]
    internal static class JoinRetryPatch
    {
        private static readonly Type RequestType = AccessTools.Inner(typeof(SteamLobby), "ServerListRequest");

        private static readonly FieldInfo CliJoinField = RequestType != null
            ? AccessTools.Field(RequestType, "_cliJoin")
            : null;

        private static readonly Type JoinType = AccessTools.Inner(typeof(SteamLobby), "CliJoinHandler");

        private static readonly FieldInfo PendingIdField =
            JoinType != null ? AccessTools.Field(JoinType, "pendingId") : null;

        private static readonly MethodInfo JoinMethod =
            JoinType != null ? AccessTools.Method(JoinType, "JoinBySteamId") : null;

        /// <summary>The join server the attempts below belong to, and how many refreshes have looked for it.</summary>
        private static ulong _pending;

        private static int _attempts;

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(RequestType, "OnRefreshComplete");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            try
            {
                var join = CliJoinField != null ? CliJoinField.GetValue(__instance) : null;
                if (join == null || PendingIdField == null || JoinMethod == null)
                {
                    // Found and handed over, not a direct join, or already left to fail.
                    _pending = 0;
                    _attempts = 0;
                    return;
                }

                var pendingId = (CSteamID)PendingIdField.GetValue(join);
                if (!pendingId.IsValid() || join == null)
                {
                    return;
                }

                if (_pending != pendingId.m_SteamID)
                {
                    _pending = pendingId.m_SteamID;
                    _attempts = 1;
                }

                if (_attempts >= 2)
                {
                    // The second look found nothing either; the game's own verdict stands.
                    _pending = 0;
                    _attempts = 0;
                    return;
                }

                _attempts++;
                CliJoinField.SetValue(__instance, null);
                Plugin.Verbose(
                    "The refresh found nothing for the join, so it is asked for again (" + _attempts + " of 2).");
                JoinMethod.Invoke(join, new object[] { pendingId });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not ask again for a join's server: " + e);
            }
        }
    }

    /// <summary>
    /// Watches the Steam join that is waiting for a lobby's data, whichever way the join was started.
    ///
    /// Steam sends lobby data back only while the lobby exists, and it drops the first request for a lobby's data
    /// often, handing it over on the next ask; the game waits for one callback with no timeout and no retry at
    /// all, so either of those leaves the join waiting forever. The data is asked for again a few times here, and
    /// then, if it never comes, the join is given up on with the game's own words for a lobby that is gone.
    /// </summary>
    internal static class SteamJoinWatchPatch
    {
        private static readonly FieldInfo JoinPendingField =
            AccessTools.Field(typeof(SteamLobby), "joinPendingLobby");

        /// <summary>How long Steam is given to send a lobby's data before it is asked again.</summary>
        private const float LobbyDataTimeout = 8f;

        /// <summary>How many times the lobby's data is asked for before the join is given up on.</summary>
        private const int LobbyDataAttempts = 2;

        private static CSteamID _waiting = CSteamID.Nil;

        private static float _waitingSince;

        private static int _attempt;

        /// <summary>Follows the pending join from the mod's per-frame host, and ends one whose data never came.</summary>
        internal static void Tick()
        {
            var manager = SteamLobby.instance;
            var lobby = Pending(manager);
            if (lobby == null)
            {
                // Nothing is pending: it joined, it failed, or another join took its place.
                _waiting = CSteamID.Nil;
                return;
            }

            if (lobby.LobbyId != _waiting)
            {
                _waiting = lobby.LobbyId;
                _waitingSince = Time.time;
                _attempt = 1;
                Plugin.Verbose(
                    "Waiting for Steam to send the lobby data of " + _waiting.m_SteamID
                    + ", which is what the join starts on.");
                return;
            }

            if (Time.time - _waitingSince <= LobbyDataTimeout)
            {
                return;
            }

            if (_attempt < LobbyDataAttempts)
            {
                _attempt++;
                _waitingSince = Time.time;
                Plugin.Verbose(
                    "Steam has not sent the lobby data yet, so it is asked again (" + _attempt + " of "
                    + LobbyDataAttempts + ").");
                SteamMatchmaking.RequestLobbyData(_waiting);
                return;
            }

            _waiting = CSteamID.Nil;
            if (JoinPendingField != null && manager != null)
            {
                JoinPendingField.SetValue(manager, null);
            }

            Plugin.Verbose("Steam never sent the lobby data of this join, so it is given up on.");
            JoinLobbyOverlay.Open(JoinProgress.Fail("Lobby no longer exists or Steam is unavailable"));
        }

        private static PlayerLobbyInstance Pending(SteamLobby manager)
        {
            if (manager == null || JoinPendingField == null)
            {
                return null;
            }

            return JoinPendingField.GetValue(manager) as PlayerLobbyInstance;
        }
    }

    /// <summary>
    /// Says when Steam hands the game a join request, which is what a friends-list Join Game does while the game
    /// is already running. Nothing else in the flow says anything until the lobby's data arrives, so a request
    /// that stops silently is otherwise invisible.
    /// </summary>
    [HarmonyPatch(typeof(SteamLobby), "OnJoinRequest")]
    internal static class JoinRequestTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(GameLobbyJoinRequested_t callback)
        {
            Plugin.Verbose(
                "Steam asked to join " + callback.m_steamIDLobby.m_SteamID + ": lobby="
                + callback.m_steamIDLobby.IsLobby() + ", server=" + callback.m_steamIDLobby.BGameServerAccount()
                + ".");
        }
    }
}
