using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SceneLoading;
using NuclearOption.UI;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Keeps the browser usable when Steam stops answering its per-server queries.
    ///
    /// The list is built from Steam's master server list, and then every entry is queried from this machine: a rules
    /// query that fills in the mission, map, password flag, player count and start time, and a ping query for the
    /// ping column. The game drops every server whose queries do not come back, and only logs the failure.
    ///
    /// Two things go wrong with that, and both show in the logs. A dedicated server this machine cannot query at
    /// all, usually one behind a relay or a firewall where the game's own connection goes through Steam while a
    /// direct query to its port never comes back, is missing from the list entirely. And Steam stops answering these
    /// queries after a couple of refreshes: on 2026-09-28 the per-server results went 9 failures out of 109, then 9,
    /// then 105 with every rules query after that failing, so the third refresh replaced every mission, map and
    /// player count with nothing. The second one happens without this mod as well, and the empty fourth refresh it
    /// came with is Steam refusing the master list itself. Refreshing again is what invites both.
    ///
    /// So a server that did not answer is listed anyway, from the master list's own copy of it, and with whatever
    /// the last refresh that did answer learned about it: the mission, the map and the start time come from a rules
    /// answer, and a failed refresh would otherwise replace them with nothing. Steam also hands a server it could
    /// not reach back with the address it holds where the name was, which is how a server turns into
    /// "192.169.81.58:38012" a couple of refreshes in, so the last real name is kept too. Listing a server costs no
    /// queries:
    /// nothing is asked of it beyond the master list request that was already made, and it is kept out of the ping
    /// loop. That matters, because these queries are metered, and queries spent on servers that cannot answer are
    /// queries the reachable ones do not get.
    ///
    /// The ping column is blank until a ping query has been answered for that server, rather than showing the master
    /// list's number from whenever the server last registered itself. Joining goes through Steam rather than the
    /// query port, so a listed server can still be reachable.
    /// </summary>
    internal static class ServerBrowser
    {
        private static readonly HashSet<ulong> PingsMeasured = new HashSet<ulong>();

        /// <summary>The last ping this machine measured, by server.</summary>
        private static readonly Dictionary<ulong, int> LastPings = new Dictionary<ulong, int>();

        /// <summary>Fields read through reflection, resolved once. Traverse handed back a null lookup here, which
        /// threw inside Steam's callback loop and took the listing with it.</summary>
        private static readonly FieldInfo RulesField = AccessTools.Field(typeof(ServerLobbyInstance), "rules");

        private static readonly FieldInfo AnnounceField = AccessTools.Field(typeof(SteamLobby), "OnLobbyDataUpdated");

        private static readonly FieldInfo RowsField = AccessTools.Field(typeof(LobbyList), "allLobbies");

        /// <summary>The browser's own dictionary of known lobbies, which is the merged set the whole-list refresh walks.</summary>
        private static readonly FieldInfo LobbiesField = AccessTools.Field(typeof(SteamLobby), "_lobbyData");

        /// <summary>
        /// The rules a server last answered with. Only the dictionary itself is kept: the details the game holds
        /// are Steam's own copy of the server, which is refreshed with every master list request and carries no
        /// strings of its own for anyone else to hold on to.
        /// </summary>
        private static readonly Dictionary<ulong, Dictionary<string, string>> LastRules =
            new Dictionary<ulong, Dictionary<string, string>>();

        /// <summary>The last name seen for a server, replaced only by another real name.</summary>
        private static readonly Dictionary<ulong, string> LastNames = new Dictionary<ulong, string>();

        /// <summary>Whether there is a number this machine measured behind the ping column.</summary>
        internal static bool HasPing(CSteamID id)
        {
            return PingsMeasured.Contains(id.m_SteamID);
        }

        /// <summary>The last ping this machine measured for a server, which is the number the column shows.</summary>
        internal static bool LastPing(CSteamID id, out int ping)
        {
            return LastPings.TryGetValue(id.m_SteamID, out ping);
        }

        /// <summary>Records a ping this machine measured, which stands in for a copy of the server that reports none.</summary>
        internal static void PingMeasured(CSteamID id, int ping)
        {
            if (!id.IsValid())
            {
                return;
            }

            if (!PingLooksMeasured(ping))
            {
                // A number Steam uses for no ping is no measurement: it is kept out of the column and out of
                // the marks both, so a response that carries one reads as no answer everywhere.
                return;
            }

            PingsMeasured.Add(id.m_SteamID);
            LastPings[id.m_SteamID] = ping;
        }

        /// <summary>How a query cycle for a server stands, which is what the two markers read.</summary>
        internal enum QueryState
        {
            /// <summary>Nothing has come back for a query yet, which is also the state of the servers that query is never asked for.</summary>
            Unresolved,

            /// <summary>A query is out, waiting for an answer or a failure.</summary>
            Asking,

            /// <summary>A press has this server down for a refresh that has not gone out yet.</summary>
            Queued,

            /// <summary>The last query was answered.</summary>
            Answered,

            /// <summary>The last query came back with nothing.</summary>
            Failed,
        }

        /// <summary>A query that is out, and the row it belongs to.</summary>
        internal struct Ask
        {
            internal ServerLobbyInstance Lobby;

            internal float When;
        }

        /// <summary>The ping queries with no answer yet, by server. Read by the marker tick every frame.</summary>
        internal static readonly Dictionary<ulong, Ask> Asking = new Dictionary<ulong, Ask>();

        /// <summary>How each server's last settled ping query ended: true answered, false nothing came back.</summary>
        private static readonly Dictionary<ulong, bool> Settled = new Dictionary<ulong, bool>();

        /// <summary>The rules queries with no answer yet, by server, read the same way.</summary>
        internal static readonly Dictionary<ulong, Ask> RulesAsking = new Dictionary<ulong, Ask>();

        /// <summary>How each server's last settled rules query ended: true answered, false nothing came back.</summary>
        private static readonly Dictionary<ulong, bool> RulesSettled = new Dictionary<ulong, bool>();

        /// <summary>
        /// The servers listed because Steam's master list could not reach them, by server.
        ///
        /// The ping loop is held off these: a host that never answers never grows its ping count, so its
        /// countdown stays due and the loop asks it again on every pass, at the frame rate, each attempt holding
        /// a socket of the Steam client's until its own timeout runs out. A refresh that lists a hundred such
        /// hosts keeps the client's small socket set full, and the queries that matter then have nowhere to go.
        /// Answering anything, or turning up in a master list that reached it, takes the mark off.
        /// </summary>
        internal static readonly HashSet<ulong> Unreachable = new HashSet<ulong>();

        /// <summary>The last player counts a server gave, by server.</summary>
        private struct Counts
        {
            internal int Current;

            internal int Max;
        }

        private static readonly Dictionary<ulong, Counts> LastCounts = new Dictionary<ulong, Counts>();

        /// <summary>A ping query went out. The marker shows work in progress until it settles.</summary>
        internal static void PingStarted(ServerLobbyInstance lobby)
        {
            var id = IdOf(lobby);
            if (!id.IsValid())
            {
                return;
            }

            Asking[id.m_SteamID] = new Ask { Lobby = lobby, When = Time.time };
        }

        /// <summary>A ping query came back.</summary>
        internal static void PingAnswered(CSteamID id)
        {
            if (!id.IsValid())
            {
                return;
            }

            Asking.Remove(id.m_SteamID);
            Settled[id.m_SteamID] = true;
        }

        /// <summary>A ping query came back with nothing, or was written off when it never called back.</summary>
        internal static void PingFailed(CSteamID id)
        {
            if (!id.IsValid())
            {
                return;
            }

            Asking.Remove(id.m_SteamID);
            Settled[id.m_SteamID] = false;
        }

        internal static QueryState PingStateOf(CSteamID id)
        {
            if (!id.IsValid())
            {
                return QueryState.Unresolved;
            }

            if (Asking.ContainsKey(id.m_SteamID))
            {
                return QueryState.Asking;
            }

            if (Waiting.Contains(id.m_SteamID))
            {
                return QueryState.Queued;
            }

            bool answered;
            if (Settled.TryGetValue(id.m_SteamID, out answered))
            {
                return answered ? QueryState.Answered : QueryState.Failed;
            }

            return QueryState.Unresolved;
        }

        /// <summary>A rules query went out. The mission cell's marker shows work in progress until it settles.</summary>
        internal static void RulesStarted(ServerLobbyInstance lobby)
        {
            var id = IdOf(lobby);
            if (!id.IsValid())
            {
                return;
            }

            RulesAsking[id.m_SteamID] = new Ask { Lobby = lobby, When = Time.time };
        }

        /// <summary>A rules query came back.</summary>
        internal static void RulesAnswered(CSteamID id)
        {
            if (!id.IsValid())
            {
                return;
            }

            RulesAsking.Remove(id.m_SteamID);
            RulesSettled[id.m_SteamID] = true;
        }

        /// <summary>A rules query came back with nothing, or was written off when it never called back.</summary>
        internal static void RulesFailed(CSteamID id)
        {
            if (!id.IsValid())
            {
                return;
            }

            RulesAsking.Remove(id.m_SteamID);
            RulesSettled[id.m_SteamID] = false;
        }

        internal static QueryState RulesStateOf(CSteamID id)
        {
            if (!id.IsValid())
            {
                return QueryState.Unresolved;
            }

            if (RulesAsking.ContainsKey(id.m_SteamID))
            {
                return QueryState.Asking;
            }

            if (Waiting.Contains(id.m_SteamID))
            {
                return QueryState.Queued;
            }

            bool answered;
            if (RulesSettled.TryGetValue(id.m_SteamID, out answered))
            {
                return answered ? QueryState.Answered : QueryState.Failed;
            }

            return QueryState.Unresolved;
        }

        /// <summary>Steam's way of saying it got no ping from a server, seen on every copy that came with a failure.</summary>
        private const int PingPlaceholder = 2000;

        /// <summary>
        /// Whether a ping number is a measurement rather than one of the shapes Steam uses for no ping: nought,
        /// a negative, or the 2000 placeholder a copy that failed to answer carries. Everything else is read as
        /// no answer, the same way the copy repair reads it.
        /// </summary>
        internal static bool PingLooksMeasured(int ping)
        {
            return ping > 0 && ping < PingPlaceholder;
        }

        /// <summary>Whether each server's last good copy asked for a password, by server.</summary>
        private static readonly Dictionary<ulong, bool> LastLocked = new Dictionary<ulong, bool>();

        /// <summary>
        /// Repairs a copy of a server Steam handed over with nothing in it, before vanilla reads it.
        ///
        /// The master list answers for a server it could not reach with zeroes in both count fields, no password
        /// flag, an address where the name was and no ping, and SetDetails writes every one of those straight into
        /// the entry, over whatever the last good answer said. Repairing them there keeps the entry honest for
        /// everything downstream: the row, the filters, the sort, and the ping sample SetDetails itself takes.
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
                RememberName(id, name);
            }

            int ping;
            if (!PingLooksMeasured(copy.m_nPing) && LastPings.TryGetValue(id.m_SteamID, out ping))
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

        /// <summary>
        /// Writes the copies' tally once the hand-over has stopped, which is a line per refresh rather than a line
        /// per copy. A copy that failed against one that answered is what the repair's test is argued from, so the
        /// counts stay; the per-copy fields were only ever read while that test was being written.
        /// </summary>
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

        /// <summary>Remembers what a server that answered said about itself, for the refreshes that follow.</summary>
        internal static void Remember(ServerLobbyInstance lobby)
        {
            try
            {
                var id = IdOf(lobby);
                if (!id.IsValid())
                {
                    return;
                }

                Unreachable.Remove(id.m_SteamID);
                RememberName(id, lobby.LobbyNameSanitized);

                var rules = RulesOf(lobby);
                if (rules != null && rules.Count > 0)
                {
                    LastRules[id.m_SteamID] = new Dictionary<string, string>(rules);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not remember what a server answered: " + e);
            }
        }

        /// <summary>
        /// The server's Steam ID, or nil when it has none yet.
        ///
        /// Steam's server details are a class rather than a struct, so an entry this mod creates before the master
        /// list hands over its copy has none, and reading through it throws. Every read of it goes through here.
        /// </summary>
        internal static CSteamID IdOf(ServerLobbyInstance lobby)
        {
            var details = lobby != null ? lobby.details : null;
            return details != null ? details.m_steamID : CSteamID.Nil;
        }

        /// <summary>
        /// Remembers a server's name. The name a server answers with is the real one; a query it did not answer
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
            if (LooksLikeAddress(name) && LastNames.TryGetValue(id.m_SteamID, out known))
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
        /// Puts the rows the mod has a real answer from back into the game's list, which is what its ping loop
        /// reads: a server that has answered its rules or a ping once is worth asking again, even when the refresh
        /// that just ran got no answers.
        ///
        /// Every refresh clears that flag on every entry, and only a rules answer sets it again, so a degraded
        /// refresh stops the ping loop for the whole list and the ping column freezes. A server nothing was ever
        /// heard from is deliberately left out: asking one of those costs a query off the same budget that is
        /// failing first. A server the browser's filter hides is left out too, so it costs no pings while it is
        /// out of sight, and the toggle that shows it again hands it back the same frame.
        /// </summary>
        internal static void Admit(LobbyList list)
        {
            if (!Plugin.KeepServersListed.Value || RowsField == null)
            {
                return;
            }

            var rows = RowsField.GetValue(list) as Dictionary<LobbyInstance, LobbyListItem>;
            if (rows == null)
            {
                return;
            }

            var filter = LocalFilters.Current(list);

            foreach (var server in rows.Keys)
            {
                var lobby = server as ServerLobbyInstance;
                if (lobby == null || lobby.InList)
                {
                    continue;
                }

                var id = IdOf(lobby);
                if (id.IsValid()
                    && (LastRules.ContainsKey(id.m_SteamID) || PingsMeasured.Contains(id.m_SteamID))
                    && LocalFilters.Passes(lobby, filter))
                {
                    lobby.InList = true;
                }
            }
        }

        /// <summary>
        /// Whether anything is known about a server from an earlier answer: its rules, its name, or its player count.
        ///
        /// A server the master list could not reach this time is not the same as one nothing is known about, and
        /// the difference decides whether its row keeps what it has. Restore cannot answer this question: it fills
        /// gaps, and a row that was never wiped has none to fill.
        /// </summary>
        internal static bool Knows(CSteamID id)
        {
            return id.IsValid()
                && (LastRules.ContainsKey(id.m_SteamID)
                    || LastNames.ContainsKey(id.m_SteamID)
                    || LastCounts.ContainsKey(id.m_SteamID));
        }

        /// <summary>
        /// Fills in the rules a server last answered with, for the keys this refresh brought nothing for. The
        /// mission, the map and the start time are in there, and a refresh whose rules query failed otherwise
        /// leaves them empty.
        ///
        /// Returns whether anything was filled in, which is not the same as knowing nothing about a server: a row
        /// whose rules were never cleared has no gaps, and Knows is the question to ask there.
        /// </summary>
        internal static bool Restore(ServerLobbyInstance lobby)
        {
            try
            {
                var id = IdOf(lobby);
                if (!id.IsValid())
                {
                    return false;
                }

                Dictionary<string, string> rules;
                var live = RulesOf(lobby);
                if (live == null || !LastRules.TryGetValue(id.m_SteamID, out rules))
                {
                    return false;
                }

                var restored = false;
                foreach (var pair in rules)
                {
                    // Fills the gaps rather than replacing, so a rules answer that arrived before a failure keeps
                    // whatever it did say.
                    if (!live.ContainsKey(pair.Key))
                    {
                        live[pair.Key] = pair.Value;
                        restored = true;
                    }
                }

                return restored;
            }
            catch (Exception e)
            {
                // A server with nothing restored is still worth listing, so this reports and gets out of the way.
                Plugin.Log.LogError("Could not restore what is known about a server: " + e);
                return false;
            }
        }

        /// <summary>
        /// Fires the event the browser builds its rows from, which is all the game does for a server it lists.
        /// InList is deliberately left alone: that field is what hands a server to the ping loop, and pinging a
        /// server this machine cannot reach is the cost this is here to avoid. The row stays as it is until an
        /// answer or a later refresh changes it, and a server that comes back is picked up by the ordinary path.
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

        /// <summary>A refresh asked for from the details window, waiting for the rules answer and the server's own
        /// details, which arrive as two separate queries.</summary>
        private sealed class Question
        {
            internal ServerLobbyInstance Lobby;

            internal Action Updated;

            internal float When;

            internal bool RulesSettled;

            internal bool RulesAnswered;

            /// <summary>Whether the rules response itself carried a key only a rules answer carries.</summary>
            internal bool RulesCarried;

            internal bool DetailsSettled;

            internal ISteamMatchmakingRulesResponse Rules;

            internal ISteamMatchmakingPingResponse Details;

            /// <summary>The two handles this question's queries came back with, so both can be cancelled.</summary>
            internal HServerQuery RulesHandle;

            internal HServerQuery PingHandle;

            internal bool RulesAsked;

            internal bool PingAsked;
        }

        private static readonly Dictionary<ulong, Question> Questioned = new Dictionary<ulong, Question>();

        private static readonly Dictionary<ulong, float> RequestedAt = new Dictionary<ulong, float>();

        /// <summary>How long a refresh of one server is left alone before another is allowed.</summary>
        private const float RequestInterval = 3f;

        /// <summary>When a refresh whose answers never came is written off, so it cannot hold the window back.</summary>
        private const float QuestionTimeout = 10f;

        /// <summary>
        /// Asks one server for its rules and for its own details, and keeps both handles so the pair can be
        /// cancelled. The details window's button passes an update callback, which reads that window again once the
        /// answers land; the refresh pass passes none, because its row repaints on its own.
        ///
        /// The rules answer carries the mission, the map, the description and the duration. The details answer, the
        /// one a ping query returns, carries the name, the player counts and the ping, which is why both are asked:
        /// a name is not something a rules answer contains, and the copy the master list hands over for a server it
        /// could not reach has an address where the name should be.
        /// </summary>
        internal static bool RefreshServer(ServerLobbyInstance lobby, Action updated)
        {
            if (lobby == null)
            {
                return false;
            }

            var details = lobby.details;
            if (details == null || !details.m_steamID.IsValid())
            {
                return false;
            }

            var id = details.m_steamID.m_SteamID;

            float askedAt;
            if (RequestedAt.TryGetValue(id, out askedAt) && Time.time - askedAt < RequestInterval)
            {
                Plugin.Verbose("A refresh of this server is already running.");
                return false;
            }

            var question = new Question
            {
                Lobby = lobby,
                Updated = updated,
                When = Time.time,
            };

            var address = details.m_NetAdr;

            question.Rules = new ISteamMatchmakingRulesResponse(
                new ISteamMatchmakingRulesResponse.RulesResponded((key, value) =>
                {
                    lobby.SetRule(key, value);

                    // Watched as the keys arrive: only this answer's own keys can be told apart from what the
                    // dictionary already held.
                    question.RulesCarried = question.RulesCarried || IsRulesContentKey(key);
                }),
                new ISteamMatchmakingRulesResponse.RulesFailedToRespond(() => Outcome(question, false, true)),
                new ISteamMatchmakingRulesResponse.RulesRefreshComplete(() => Outcome(question, true, true)));

            question.Details = new ISteamMatchmakingPingResponse(
                new ISteamMatchmakingPingResponse.ServerResponded(server => Details(question, server)),
                new ISteamMatchmakingPingResponse.ServerFailedToRespond(() => PingFailure(question)));

            RequestedAt[id] = Time.time;

            Question previous;
            if (Questioned.TryGetValue(id, out previous))
            {
                // This ask replaces one that is still out: the older pair of queries stops here, and it counts as
                // settled, because no answer of its own is coming to close it.
                CancelQueries(previous);

                if (previous.Updated == null)
                {
                    _passSettled++;
                }
            }

            Questioned[id] = question;

            // Both markers follow this query as well: the asks go out now and settle in Details, PingFailure or
            // Outcome, which is what puts the in-progress glyphs up and takes them back down again.
            PingStarted(lobby);
            RulesStarted(lobby);
            StaleRowPatch.Repaint(lobby);

            // The asks go out on the spot: the queue holds the game's own queries, and the refresh pass paces
            // itself, so nothing here waits for a turn. Both handles are kept, because nothing else holds them. A
            // refresh that is replaced or given up on cancels its own two queries; one whose answers land after the
            // details window is closed still updates the row, and the window reads itself again only if it is still
            // open on that server.
            question.RulesHandle = SteamMatchmakingServers.ServerRules(
                address.GetIP(), address.GetQueryPort(), question.Rules);
            question.RulesAsked = true;
            question.PingHandle = SteamMatchmakingServers.PingServer(
                address.GetIP(), address.GetQueryPort(), question.Details);
            question.PingAsked = true;

            if (updated == null)
            {
                _passAsked++;
            }

            return true;
        }

        /// <summary>The server's details query came back with nothing, which the marker shows as a warning.</summary>
        private static void PingFailure(Question question)
        {
            PingFailed(IdOf(question.Lobby));

            if (question.Updated == null)
            {
                _passEmpty++;
            }

            Outcome(question, false, false);
        }

        /// <summary>The server said what it is, straight from the source: its name, its counts and its ping.</summary>
        private static void Details(Question question, gameserveritem_t server)
        {
            var mark = Cost.Mark();
            var lobby = question.Lobby;
            var id = IdOf(lobby);

            if (id.IsValid())
            {
                var ping = Cost.Mark();

                // The game's own way of taking a ping sample, so its state stays consistent with a vanilla ping.
                lobby.SetPingResult(server.m_nPing);

                PingMeasured(id, server.m_nPing);

                // A response with no usable number in it got the query nowhere, for the marker and for the
                // column both.
                if (PingLooksMeasured(server.m_nPing))
                {
                    PingAnswered(id);
                }
                else
                {
                    PingFailed(id);
                }

                Cost.Add("ping", ping, lobby);

                var keep = Cost.Mark();

                // An answer here is proof the server is alive, so it goes back into the game's ping loop, the way
                // the admit pass treats a rules answer; a server the filter hides stays out either way.
                if (Plugin.KeepServersListed.Value && LocalFilters.Passes(lobby, LocalFilters.Current()))
                {
                    lobby.InList = true;
                }

                Cost.Add("keep", keep, lobby);

                var save = Cost.Mark();
                Answered(lobby, server);
                Cost.Add("save", save, lobby);

                if (question.Updated == null)
                {
                    // A pass answers for every row it asked, and a line each is a hundred lines per press, each
                    // of them through the console and the log file. The tally in RefreshTick says the same thing
                    // once the pass is over.
                    _passAnswered++;
                }
                else
                {
                    // The details window's own refresh is one server, so its answer may be a line of its own.
                    Plugin.Verbose(
                        "The refresh of " + server.GetServerName() + " answered: players=" + server.m_nPlayers + "/"
                        + server.m_nMaxPlayers + " ping=" + server.m_nPing + " password=" + server.m_bPassword);
                }
            }

            var settle = Cost.Mark();
            Outcome(question, true, false);
            Cost.Add("settle", settle, lobby);
            Cost.Add("answer", mark, lobby);
        }

        /// <summary>
        /// Takes what a status answer says about a server, from a manual refresh or from the ping loop's own
        /// query, which carries the same copy: its name, its counts and its password flag.
        ///
        /// Vanilla keeps only the ping from a loop answer, so without this a row's counts stay as old as the last
        /// master list copy, and a copy that could not be fetched reads 0/0. The two memories the repairs fall
        /// back on are refreshed with it, so a repair restores what the server said rather than what a failed
        /// copy carried.
        /// </summary>
        internal static void Answered(ServerLobbyInstance lobby, gameserveritem_t server)
        {
            var details = lobby != null ? lobby.details : null;
            var id = IdOf(lobby);
            if (details == null || !id.IsValid())
            {
                return;
            }

            // It answered, so the ping loop may ask it again.
            Unreachable.Remove(id.m_SteamID);

            var name = server.GetServerName();
            RememberName(id, name);

            var changed = false;
            if (!LooksLikeAddress(name) && name != details.GetServerName())
            {
                details.SetServerName(name);
                changed = true;
            }

            // A copy with no room in it is the same failure wearing a server's shape the copy repair knows, so
            // its counts and its password flag are read as missing rather than written over what is known.
            if (server.m_nMaxPlayers > 0)
            {
                changed = changed
                    || details.m_nPlayers != server.m_nPlayers
                    || details.m_nMaxPlayers != server.m_nMaxPlayers
                    || details.m_bPassword != server.m_bPassword;

                details.m_nPlayers = server.m_nPlayers;
                details.m_nMaxPlayers = server.m_nMaxPlayers;
                details.m_bPassword = server.m_bPassword;
                LastCounts[id.m_SteamID] = new Counts { Current = server.m_nPlayers, Max = server.m_nMaxPlayers };
                LastLocked[id.m_SteamID] = server.m_bPassword;
            }

            // A row keeps the values it was built with, so it is shown again to take the new ones, and only when
            // an answer actually changed one: most answers repeat what the row already says.
            if (changed)
            {
                Announce(SteamLobby.instance, lobby);
            }
        }

        private static void Outcome(Question question, bool answered, bool rules)
        {
            if (rules)
            {
                if (question.RulesSettled)
                {
                    return;
                }

                question.RulesSettled = true;
                question.RulesAnswered = answered;

                // The mission cell's marker follows the rules half the way the ping column's follows the other.
                // A response that carried nothing real is an answer where nothing useful came back, so the
                // marker reads it as no answer.
                var id = IdOf(question.Lobby);
                if (answered && question.RulesCarried)
                {
                    RulesAnswered(id);
                }
                else
                {
                    RulesFailed(id);
                }

                StaleRowPatch.Repaint(question.Lobby);
            }
            else
            {
                if (question.DetailsSettled)
                {
                    return;
                }

                question.DetailsSettled = true;

                // Whether the ping came back or not, the marker follows it, and nothing else repaints for it.
                StaleRowPatch.Repaint(question.Lobby);
            }

            if (question.RulesSettled && question.DetailsSettled)
            {
                Finish(question);
            }
        }

        /// <summary>
        /// Adds to the refresh pass the servers a press should ask: the ones with nothing of their own on the way.
        ///
        /// A server falls into one of three groups. It is finished when no question of the mod's own is pending
        /// for it, and a press enqueues it. It is waiting when the pass has picked it but has not asked yet, and in
        /// flight when its two queries are out; both are left alone, because that ask is already on its way. A
        /// press that lands while a pass is running therefore tops the pass up instead of restarting it, and one
        /// that lands after the pass is done starts a fresh pick of every finished row.
        /// </summary>
        internal static void RefreshListed()
        {
            if (_askAt >= ToAsk.Count)
            {
                // Nothing is waiting: this press starts the pass over.
                ToAsk.Clear();
                Picked.Clear();
                Waiting.Clear();
                _askAt = 0;
            }

            var manager = SteamLobby.instance;
            var lobbies = manager != null && LobbiesField != null
                ? LobbiesField.GetValue(manager) as Dictionary<CSteamID, LobbyInstance>
                : null;
            if (lobbies == null)
            {
                return;
            }

            var filter = LocalFilters.Current();

            var asking = new HashSet<ulong>();
            CollectAsking(asking);

            var added = 0;
            foreach (var entry in lobbies.Values)
            {
                var lobby = entry as ServerLobbyInstance;
                if (lobby == null)
                {
                    continue;
                }

                var id = IdOf(lobby);
                if (!id.IsValid() || asking.Contains(id.m_SteamID) || Picked.Contains(id.m_SteamID))
                {
                    continue;
                }

                if (Questioned.ContainsKey(id.m_SteamID) || !LocalFilters.Passes(lobby, filter))
                {
                    continue;
                }

                ToAsk.Add(lobby);
                Picked.Add(id.m_SteamID);
                Waiting.Add(id.m_SteamID);
                added++;
            }

            if (added > 0)
            {
                // Everything the pass has down for a refresh shows a still icon from here, so a press reads as
                // work queued rather than as nothing at all until each row's turn comes.
                for (var i = 0; i < ToAsk.Count; i++)
                {
                    StaleRowPatch.Repaint(ToAsk[i]);
                }

                Plugin.Verbose(
                    "A refresh press added " + added + " finished servers to the pass, " + (ToAsk.Count - _askAt)
                    + " still to ask of " + lobbies.Count + " in hand.");
            }
        }

        /// <summary>Asks the servers the press picked, a few a frame, and clears the pick once it is done.</summary>
        internal static void RefreshListedTick()
        {
            if (_askAt >= ToAsk.Count)
            {
                return;
            }

            var mark = Cost.Mark();
            for (var i = 0; i < AskPerFrame && _askAt < ToAsk.Count; i++, _askAt++)
            {
                // Its turn has come, so it is not queued any more, whatever the ask itself does with it.
                Waiting.Remove(IdOf(ToAsk[_askAt]).m_SteamID);
                RefreshServer(ToAsk[_askAt], null);
            }

            Cost.Add("asks", mark);

            if (_askAt >= ToAsk.Count)
            {
                ToAsk.Clear();
                Picked.Clear();
                Waiting.Clear();
                _askAt = 0;
                Plugin.Verbose("The refresh pass asked every server it picked.");
            }
        }

        /// <summary>How many of the picked servers a refresh pass asks per frame.</summary>
        private const int AskPerFrame = 2;

        /// <summary>The servers the pass is working through, and the ids it has already picked.</summary>
        private static readonly List<ServerLobbyInstance> ToAsk = new List<ServerLobbyInstance>();

        private static readonly HashSet<ulong> Picked = new HashSet<ulong>();

        /// <summary>
        /// The picked servers the pass has not asked yet, by id, which is what the markers read as queued. It is
        /// not the same as Picked: that one keeps a row for the whole pass, answers included.
        /// </summary>
        private static readonly HashSet<ulong> Waiting = new HashSet<ulong>();

        private static int _askAt;

        /// <summary>The pass's tally, written by the asks, the answers and the settles, and cleared by the line
        /// that reports it.</summary>
        private static int _passAsked;

        private static int _passSettled;

        private static int _passAnswered;

        private static int _passEmpty;

        private static readonly FieldInfo RequestField = AccessTools.Field(typeof(SteamLobby), "serverListRequest");

        private static readonly FieldInfo RunningField = AccessTools.Field(
            AccessTools.Inner(typeof(SteamLobby), "ServerListRequest"), "RunningQueries");

        private static readonly FieldInfo SearchLobbyField = AccessTools.Field(
            AccessTools.Inner(typeof(SteamLobby), "SearchQuery"), "lobby");

        private static readonly FieldInfo PingLobbyField = AccessTools.Field(
            AccessTools.Inner(typeof(SteamLobby), "PingQuery"), "lobby");

        /// <summary>Collects the ids the game's request is asking right now, its rules queries and pings alike.</summary>
        private static void CollectAsking(HashSet<ulong> into)
        {
            try
            {
                var manager = SteamLobby.instance;
                var request = manager != null && RequestField != null ? RequestField.GetValue(manager) : null;
                var running = request != null && RunningField != null
                    ? RunningField.GetValue(request) as System.Collections.IEnumerable
                    : null;
                if (running == null)
                {
                    return;
                }

                foreach (var query in running)
                {
                    if (query == null)
                    {
                        continue;
                    }

                    var field = SearchLobbyField != null && SearchLobbyField.DeclaringType.IsInstanceOfType(query)
                        ? SearchLobbyField
                        : PingLobbyField != null && PingLobbyField.DeclaringType.IsInstanceOfType(query)
                            ? PingLobbyField
                            : null;
                    if (field == null)
                    {
                        continue;
                    }

                    var lobby = field.GetValue(query) as ServerLobbyInstance;
                    var id = lobby != null ? IdOf(lobby) : default(CSteamID);
                    if (id.IsValid())
                    {
                        into.Add(id.m_SteamID);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Verbose("Could not read the request's running queries: " + e.Message);
            }
        }

        /// <summary>Finishes a refresh that is over, either because both answers came or because it was given up on.</summary>
        internal static void RefreshTick()
        {
            if (_passAsked > 0 && _passSettled >= _passAsked)
            {
                // One line for a whole pass. A line per answer is what makes a press of a hundred rows cost a
                // hundred writes to the console and the log file.
                Plugin.Verbose(
                    "The refresh pass asked " + _passAsked + " servers: " + _passAnswered
                    + " answered, " + _passEmpty + " said nothing.");
                _passAsked = 0;
                _passSettled = 0;
                _passAnswered = 0;
                _passEmpty = 0;
            }

            if (Questioned.Count == 0)
            {
                return;
            }

            List<Question> stuck = null;
            foreach (var question in Questioned.Values)
            {
                if (Time.time - question.When > QuestionTimeout)
                {
                    stuck = stuck ?? new List<Question>();
                    stuck.Add(question);
                }
            }

            if (stuck == null)
            {
                return;
            }

            foreach (var question in stuck)
            {
                Completion(question, false);
            }

            Plugin.Verbose(
                stuck.Count == 1
                    ? "Gave up on one refresh that never answered."
                    : "Gave up on " + stuck.Count + " refreshes that never answered.");
        }

        private static void Finish(Question question)
        {
            Completion(question, true);
        }

        private static void Completion(Question question, bool settled)
        {
            var lobby = question.Lobby;
            var id = IdOf(lobby);

            Question current;
            if (!id.IsValid() || !Questioned.TryGetValue(id.m_SteamID, out current)
                || !ReferenceEquals(current, question))
            {
                // A newer ask replaced this one and already took its place in the map: a late answer of this
                // question must not take the replacement out with it.
                return;
            }

            Questioned.Remove(id.m_SteamID);

            if (question.Updated == null)
            {
                _passSettled++;
            }

            // Both answers are in, or the refresh was given up on: either way, its two queries are done with.
            var closes = Cost.Mark();
            CancelQueries(question);
            Cost.Add("closes", closes, lobby);

            // Give-ups are reported in one line by RefreshTick, where a press can make several at once; only
            // the details window's own ask keeps a line per refresh.
            if (settled && !question.RulesAnswered && question.Updated != null)
            {
                Plugin.Verbose("The refresh of " + lobby.LobbyNameSanitized + " got no rules answer.");
            }

            // Only a rules answer is remembered. A server that answered nothing still holds a rules dictionary,
            // the password flag and the master list's tags, written when its entry was built, and remembering
            // that as an answer would mark it answered for the admit pass and hand a dead entry to the ping loop.
            if (question.RulesAnswered)
            {
                var remembered = Cost.Mark();
                Remember(lobby);
                Cost.Add("remember", remembered, lobby);
            }

            var shown = Cost.Mark();
            Announce(SteamLobby.instance, lobby);
            Cost.Add("show", shown, lobby);

            if (question.Updated != null)
            {
                var window = Cost.Mark();
                question.Updated();
                Cost.Add("window", window, lobby);
            }
        }

        /// <summary>
        /// Cancels the two queries a question asked. Nothing else holds them, so without this a refresh that is
        /// replaced or given up on leaves its rules and ping queries running on Steam's side.
        /// </summary>
        private static void CancelQueries(Question question)
        {
            // A half that already answered is done with on Steam's side, and cancelling it would be a Steam call
            // for nothing. A refresh of a hundred rows ends two hundred of these, and a call into Steam from
            // inside its own callback pump is what blocks a frame for most of a second.
            if (question.RulesAsked)
            {
                question.RulesAsked = false;

                if (!question.RulesSettled)
                {
                    CancelQuery(question.RulesHandle);
                }
            }

            if (question.PingAsked)
            {
                question.PingAsked = false;

                if (!question.DetailsSettled)
                {
                    CancelQuery(question.PingHandle);
                }
            }
        }

        private static void CancelQuery(HServerQuery handle)
        {
            try
            {
                SteamMatchmakingServers.CancelServerQuery(handle);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not cancel a server query: " + e);
            }
        }

        internal static void Report(ServerLobbyInstance lobby, string why)
        {
            Plugin.Verbose(
                "Listing " + lobby.LobbyNameSanitized + " (" + IdOf(lobby).m_SteamID + ") " + why);
        }

        private static Dictionary<string, string> RulesOf(ServerLobbyInstance lobby)
        {
            return RulesField != null ? RulesField.GetValue(lobby) as Dictionary<string, string> : null;
        }

        /// <summary>
        /// Whether a rules answer said anything only a rules answer says: the mission, the map, the start time,
        /// the description or the workshop id. Every refresh seeds a server's rules through SetDetails with the
        /// password flag and the tags, so a response that brought none of these keys is one to read as no answer.
        /// </summary>
        internal static bool HasRulesContent(ServerLobbyInstance lobby)
        {
            var rules = RulesOf(lobby);
            return rules != null
                && (rules.ContainsKey("mission_name")
                    || rules.ContainsKey("map_name")
                    || rules.ContainsKey("start_time")
                    || rules.ContainsKey("mission_workshop_id")
                    || rules.ContainsKey("d0")
                    || rules.ContainsKey("d1")
                    || rules.ContainsKey("d2")
                    || rules.ContainsKey("d3"));
        }

        /// <summary>The same question for a key as a rules response carries it, which is the short form: the
        /// long form is what the client stores the key under once it reads it.</summary>
        internal static bool IsRulesContentKey(string key)
        {
            return key == "mi"
                || key == "ma"
                || key == "s"
                || key == "id"
                || key == "d0"
                || key == "d1"
                || key == "d2"
                || key == "d3";
        }
    }

    /// <summary>
    /// Gives a server that failed the master list's per-server query an entry and a row, the way a server that
    /// answered gets one.
    ///
    /// Vanilla would run a rules query here through ServerListRequest.OnServerUpdated and let that decide, which
    /// would spend a query on a server that has just shown it does not answer, so this builds the entry itself
    /// instead: the master list's copy of the server already carries its name, player count and password flag.
    ///
    /// That copy is repaired on the way in by ServerDetailsPatch, so a server already known keeps its name, counts
    /// and password, and the rules the previous refresh answered are filled back in below.
    ///
    /// Both nested types are private, so the target is resolved by name rather than by an attribute argument.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerListFailurePatch
    {
        private static readonly Type RequestType = AccessTools.Inner(typeof(SteamLobby), "ServerListRequest");

        private static readonly FieldInfo ManagerField = AccessTools.Field(RequestType, "manager");

        private static readonly FieldInfo CliJoinField = AccessTools.Field(RequestType, "_cliJoin");

        private static readonly FieldInfo LobbiesField = AccessTools.Field(typeof(SteamLobby), "_lobbyData");

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
        /// list for one server by its ID and lets Steam probe it, the same probe every other refresh rests on, and
        /// the one that stops answering after the first refresh of a session. A probe that fails turns into "server
        /// could not be found" over a server that is very much up.
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
            var lobbies = manager != null && LobbiesField != null
                ? LobbiesField.GetValue(manager) as Dictionary<CSteamID, LobbyInstance>
                : null;
            if (lobbies == null)
            {
                return false;
            }

            LobbyInstance cached;
            if (!lobbies.TryGetValue(id, out cached))
            {
                cached = new ServerLobbyInstance();
                lobbies.Add(id, cached);
            }

            var lobby = cached as ServerLobbyInstance;
            if (lobby == null)
            {
                return false;
            }

            lobby.SetDetails(details);
            ServerBrowser.Restore(lobby);

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
                new ISteamMatchmakingRulesResponse.RulesRefreshComplete(() => RescuedRules(rescue)));

            // A join is a player waiting on the answer, so the query goes out on the spot.
            SteamMatchmakingServers.ServerRules(address.GetIP(), address.GetQueryPort(), rescue.Rules);

            return true;
        }

        private static void RescuedRules(JoinRescue rescue)
        {
            ServerBrowser.Remember(rescue.Lobby);
            AnswerRescue(rescue, "answered its rules");
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
            if (details == null || !details.m_steamID.IsValid() || ManagerField == null || LobbiesField == null)
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
            var lobbies = manager != null
                ? LobbiesField.GetValue(manager) as Dictionary<CSteamID, LobbyInstance>
                : null;
            if (lobbies == null)
            {
                return;
            }

            LobbyInstance cached;
            if (!lobbies.TryGetValue(details.m_steamID, out cached))
            {
                cached = new ServerLobbyInstance();
                lobbies.Add(details.m_steamID, cached);
            }

            var lobby = cached as ServerLobbyInstance;
            if (lobby == null)
            {
                return;
            }

            var known = ServerBrowser.Knows(ServerBrowser.IdOf(lobby));

            // The copy is repaired on its way in, so a server that is already known keeps its counts, name and
            // password while still taking the fresh address, and the rules gap-fill below fills in the rest.
            lobby.SetDetails(details);
            ServerBrowser.Restore(lobby);

            if (!known)
            {
                ServerBrowser.Report(
                    lobby,
                    "did not answer this machine, and nothing is known about it beyond what it registered with "
                    + "Steam. Its mission, map and ping stay empty.");
            }

            ServerBrowser.Unreachable.Add(ServerBrowser.IdOf(lobby).m_SteamID);
            ServerBrowser.Announce(manager, lobby);
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

        /// <summary>Starts listening for connects, which is what tells a refused join from a dropped session.</summary>
        internal static void Ensure()
        {
            if (_listening)
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

    /// <summary>
    /// Holds the ping loop off the servers the master list could not reach.
    ///
    /// The loop asks every listed server whose countdown is due, and a server that never answers never counts
    /// its pings up, so its countdown stays due and it is asked again on every pass, at the frame rate. Each of
    /// those attempts holds a socket of the Steam client's while its own timeout runs out; a refresh lists a
    /// hundred silent hosts and the client's socket set fills up, after which the queries that matter find no
    /// room and refreshes come back nearly empty. Their countdown is kept pushed forward instead, which the
    /// mark coming off lifts the moment one of them answers anything.
    /// </summary>
    [HarmonyPatch(typeof(SteamLobby), "CheckPingServers")]
    internal static class PingHoldPatch
    {
        private const float Hold = 30f;

        private static readonly FieldInfo LobbiesField = AccessTools.Field(typeof(SteamLobby), "_lobbyData");

        [HarmonyPrefix]
        private static void Prefix(SteamLobby __instance)
        {
            if (!Plugin.ListUnreachableServers.Value || LobbiesField == null || ServerBrowser.Unreachable.Count == 0)
            {
                return;
            }

            try
            {
                var lobbies = LobbiesField.GetValue(__instance) as Dictionary<CSteamID, LobbyInstance>;
                if (lobbies == null)
                {
                    return;
                }

                var until = Time.time + Hold;
                foreach (var id in ServerBrowser.Unreachable)
                {
                    LobbyInstance entry;
                    if (!lobbies.TryGetValue(new CSteamID(id), out entry))
                    {
                        continue;
                    }

                    var server = entry as ServerLobbyInstance;
                    if (server != null && server.InList && server.NextPingTime < until)
                    {
                        server.NextPingTime = until;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not hold the ping loop off the servers it cannot reach: " + e);
            }
        }
    }

    /// <summary>
    /// Lists a server whose rules query never came back, because Steam's master list is proof enough that it
    /// exists.
    ///
    /// The filter checks are not repeated: the player count and the ping a rules query would have supplied never
    /// arrived, and the values the master list holds for a server it cannot reach either read as nought, so asking
    /// the "hide full" and "hide empty" toggles about them would hide exactly the servers this is meant to show.
    /// The password flag the master list does carry is still honoured.
    /// </summary>
    [HarmonyPatch]
    internal static class SearchQueryFailurePatch
    {
        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "SearchQuery");

        private static readonly FieldInfo LobbyField = AccessTools.Field(QueryType, "lobby");

        private static readonly FieldInfo FilterField = AccessTools.Field(QueryType, "filter");

        private static readonly FieldInfo CliJoinField = AccessTools.Field(QueryType, "cliJoin");

        private static readonly FieldInfo ManagerField = AccessTools.Field(QueryType, "manager");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(QueryType, "RulesFailedToRespond");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (Plugin.ShowLatestQueryStatus.Value)
            {
                try
                {
                    var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
                    ServerBrowser.RulesFailed(ServerBrowser.IdOf(lobby));
                    StaleRowPatch.Repaint(lobby);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Could not note a rules query with no answer: " + e);
                }
            }

            if (!Plugin.ListUnreachableServers.Value)
            {
                return;
            }

            try
            {
                List(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not list a server whose rules query failed: " + e);
            }
        }

        private static void List(object instance)
        {
            if (LobbyField == null)
            {
                return;
            }

            // A direct join by Steam ID is told the server is missing, which is the useful answer there.
            if (CliJoinField != null && CliJoinField.GetValue(instance) != null)
            {
                return;
            }

            var lobby = LobbyField.GetValue(instance) as ServerLobbyInstance;
            if (lobby == null || !ServerBrowser.IdOf(lobby).IsValid())
            {
                return;
            }

            var filter = FilterField != null
                ? (LobbySearchFilter)FilterField.GetValue(instance)
                : default(LobbySearchFilter);
            string password;
            if (filter.HidePasswordProtected && lobby.IsPasswordProtected(out password))
            {
                return;
            }

            if (ServerBrowser.Knows(ServerBrowser.IdOf(lobby)))
            {
                ServerBrowser.Restore(lobby);
            }
            else
            {
                ServerBrowser.Report(
                    lobby,
                    "did not answer this machine's rules query, and nothing is known about it, so its mission, "
                    + "map and player count stay empty.");
            }

            var manager = ManagerField != null ? ManagerField.GetValue(instance) as SteamLobby : null;
            ServerBrowser.Announce(manager, lobby);
        }
    }

    /// <summary>
    /// Keeps a rule answer from leaving a server worse off than the refresh before it, and remembers what it said
    /// for the refreshes that follow.
    ///
    /// Every refresh clears a server's rules through SetDetails before the new answer arrives, so keys this answer
    /// does not carry would read as empty columns: the mission, the map and the duration vanish from a row that had
    /// them a moment ago. The gaps are filled from the last answer before vanilla reads the rules back for its
    /// filters and for the row, so a partial answer changes only the columns it really changed.
    /// </summary>
    [HarmonyPatch]
    internal static class SearchQueryAnswerPatch
    {
        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "SearchQuery");

        private static readonly FieldInfo LobbyField = AccessTools.Field(QueryType, "lobby");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(QueryType, "RulesRefreshComplete");
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            try
            {
                var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
                if (lobby != null)
                {
                    // Read the answer's own keys before Restore fills the gaps from the last good answer, so
                    // an answer that carried nothing reads as nothing here too.
                    __state = ServerBrowser.HasRulesContent(lobby);
                    ServerBrowser.Restore(lobby);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not fill in a rules answer: " + e);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance, bool __state)
        {
            try
            {
                var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
                ServerBrowser.Remember(lobby);

                // The mission cell's marker reads this query's cycle too. A response that carried nothing
                // real is an answer where nothing useful came back, so the marker reads it as no answer.
                if (Plugin.ShowLatestQueryStatus.Value)
                {
                    var id = ServerBrowser.IdOf(lobby);
                    if (__state)
                    {
                        ServerBrowser.RulesAnswered(id);
                    }
                    else
                    {
                        ServerBrowser.RulesFailed(id);
                    }

                    StaleRowPatch.Repaint(lobby);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not remember a server that answered: " + e);
            }
        }
    }

    /// <summary>A blank ping column until a ping query has been answered for that server, instead of the master
    /// list's number from whenever the server last registered itself. Once one has, the column shows that last
    /// measured number rather than the game's average of samples: the average also takes a sample from every copy
    /// the master list hands over, and a copy it could not fetch carries 2000 where the ping should be.</summary>
    [HarmonyPatch(typeof(ServerLobbyInstance), nameof(ServerLobbyInstance.CalculatePing))]
    internal static class ServerPingPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ServerLobbyInstance __instance, ref int? __result)
        {
            var id = ServerBrowser.IdOf(__instance);
            if (!ServerBrowser.HasPing(id))
            {
                __result = null;
                return;
            }

            int ping;
            if (ServerBrowser.LastPing(id, out ping))
            {
                __result = ping;
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
    /// Records a ping answer before vanilla reads it, both halves of it: the ping, which has to land before the
    /// browser's ping handler calls CalculatePing().Value on a column the mod has blanked, and the copy's name,
    /// counts and password, which vanilla drops. The row is repainted as the query settles, which is what turns
    /// the marker's in-progress glyph back off.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerPingAnswerPatch
    {
        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "PingQuery");

        private static readonly FieldInfo LobbyField = AccessTools.Field(QueryType, "lobby");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(QueryType, "ServerResponded");
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance, gameserveritem_t server)
        {
            try
            {
                var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;

                // Keyed by the entry's own id, the way the marker and the admit pass key this server, so they all
                // read the same server the ask went out for.
                var id = ServerBrowser.IdOf(lobby);
                if (!id.IsValid())
                {
                    // The entry could not be resolved; the ping is still measured under the id the answer carries,
                    // which is what keeps the browser's own handler off a blanked ping column.
                    ServerBrowser.PingMeasured(server.m_steamID, server.m_nPing);
                    return;
                }

                ServerBrowser.PingMeasured(id, server.m_nPing);
                ServerBrowser.Answered(lobby, server);

                if (!Plugin.ShowLatestQueryStatus.Value)
                {
                    return;
                }

                // A response with no usable number in it got the query nowhere, so the marker reads it the
                // same as a query that came back with nothing.
                if (ServerBrowser.PingLooksMeasured(server.m_nPing))
                {
                    ServerBrowser.PingAnswered(id);
                }
                else
                {
                    ServerBrowser.PingFailed(id);
                }

                StaleRowPatch.Repaint(lobby);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not record a ping answer: " + e);
            }
        }
    }

    /// <summary>
    /// Notes a ping query going out, which is what puts the marker's in-progress glyph on the ping column: the
    /// wait is visible from the moment the question is asked.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerPingAskPatch
    {
        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "PingQuery");

        private static readonly FieldInfo LobbyField = AccessTools.Field(QueryType, "lobby");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(QueryType, "Run");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Plugin.ShowLatestQueryStatus.Value)
            {
                return;
            }

            try
            {
                var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
                if (lobby == null)
                {
                    return;
                }

                ServerBrowser.PingStarted(lobby);
                StaleRowPatch.Repaint(lobby);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not note a ping query going out: " + e);
            }
        }
    }

    /// <summary>
    /// Notes a rules query going out, which is what puts the mission cell's in-progress glyph on the row, the way
    /// its ping twin does on the ping column.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerRulesAskPatch
    {
        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "SearchQuery");

        private static readonly FieldInfo LobbyField = AccessTools.Field(QueryType, "lobby");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(QueryType, "Run");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Plugin.ShowLatestQueryStatus.Value)
            {
                return;
            }

            try
            {
                var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
                if (lobby == null)
                {
                    return;
                }

                ServerBrowser.RulesStarted(lobby);
                StaleRowPatch.Repaint(lobby);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not note a rules query going out: " + e);
            }
        }
    }

    /// <summary>
    /// Notes a ping query that came back with nothing, the case the marker's warning triangle is for. Vanilla does
    /// nothing with this callback at all, so without the repaint here the row would keep whatever glyph it had.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerPingFailurePatch
    {
        private static readonly Type QueryType = AccessTools.Inner(typeof(SteamLobby), "PingQuery");

        private static readonly FieldInfo LobbyField = AccessTools.Field(QueryType, "lobby");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(QueryType, "ServerFailedToRespond");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Plugin.ShowLatestQueryStatus.Value)
            {
                return;
            }

            try
            {
                var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
                if (lobby == null)
                {
                    return;
                }

                ServerBrowser.PingFailed(ServerBrowser.IdOf(lobby));
                StaleRowPatch.Repaint(lobby);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not note a ping query with no answer: " + e);
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
    /// Lists a server as soon as Steam's master list hands it over, rather than waiting for its rules query to come
    /// back.
    ///
    /// The row starts with what the master list knows (name, player count, password flag), and the columns only a
    /// rules answer fills in are taken from the last answer if there was one. Vanilla's own path updates the row in
    /// place when the rules do arrive.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerListAnswerPatch
    {
        private static readonly Type RequestType = AccessTools.Inner(typeof(SteamLobby), "ServerListRequest");

        private static readonly FieldInfo ManagerField = AccessTools.Field(RequestType, "manager");

        private static readonly FieldInfo CliJoinField = AccessTools.Field(RequestType, "_cliJoin");

        private static readonly FieldInfo LobbiesField = AccessTools.Field(typeof(SteamLobby), "_lobbyData");

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(RequestType, "OnServerResponded");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance, HServerListRequest hRequest, int iServer)
        {
            try
            {
                List(__instance, hRequest, iServer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not list a server that answered: " + e);
            }
        }

        private static void List(object instance, HServerListRequest hRequest, int iServer)
        {
            var details = SteamMatchmakingServers.GetServerDetails(hRequest, iServer);
            if (details == null || !details.m_steamID.IsValid())
            {
                return;
            }

            // The master list reached this one, so the ping loop may ask it again.
            ServerBrowser.Unreachable.Remove(details.m_steamID.m_SteamID);

            if (!Plugin.ListBeforeAnswering.Value || ManagerField == null || LobbiesField == null)
            {
                return;
            }

            // A direct join by Steam ID goes through vanilla's own path, which wants no row for it.
            if (CliJoinField != null && CliJoinField.GetValue(instance) != null)
            {
                return;
            }

            var manager = ManagerField.GetValue(instance) as SteamLobby;
            var lobbies = manager != null
                ? LobbiesField.GetValue(manager) as Dictionary<CSteamID, LobbyInstance>
                : null;
            if (lobbies == null)
            {
                return;
            }

            LobbyInstance cached;
            if (!lobbies.TryGetValue(details.m_steamID, out cached))
            {
                cached = new ServerLobbyInstance();
                lobbies.Add(details.m_steamID, cached);
            }

            var lobby = cached as ServerLobbyInstance;
            if (lobby == null)
            {
                return;
            }

            ServerBrowser.Restore(lobby);
            ServerBrowser.Announce(manager, lobby);
        }
    }

    /// <summary>
    /// Stops the browser from emptying itself at the start of every refresh.
    ///
    /// Vanilla hides every row and then re-adds each server as it answers, so a refresh Steam declines to answer
    /// leaves an empty list and a slow one leaves a half-empty list. With this the rows stay on screen and the
    /// refresh updates them in place, including the servers Steam says nothing about.
    ///
    /// A changed filter still clears the list, because rows the new filter excludes would otherwise stay on screen.
    /// Every filter toggle calls GetListOfLobbies, which is what puts the new filter in place before the clear.
    ///
    /// The refreshing overlay comes down with the clear it belongs to: the list raises it on every visit, and only
    /// a marked-pending refresh lowers it, so leaving it up would leave the browser stuck behind it.
    /// </summary>
    [HarmonyPatch(typeof(LobbyList), "RefreshLobbyListCleared")]
    internal static class LobbyListKeepRowsPatch
    {
        private static readonly FieldInfo FilterField = AccessTools.Field(typeof(LobbyList), "activeFilter");

        private static readonly FieldInfo OverlayField = AccessTools.Field(typeof(LobbyList), "refreshOverlay");

        private static LobbySearchFilter _lastFilter;

        private static bool _seen;

        /// <summary>
        /// Records a filter the mod put on the rows itself. Those rows match it, so the clear that follows is a
        /// press and not a moved toggle; without this the local apply's own filter write reads as a change, and the
        /// press's clear drops every row, which is the vanilla emptying the patch exists to stop.
        /// </summary>
        internal static void Remember(LobbySearchFilter filter)
        {
            _lastFilter = filter;
            _seen = true;
        }

        [HarmonyPrefix]
        private static bool Prefix(LobbyList __instance)
        {
            if (!Plugin.KeepServersListed.Value || FilterField == null || OverlayField == null)
            {
                return true;
            }

            var filter = (LobbySearchFilter)FilterField.GetValue(__instance);
            var unchanged = _seen && Same(_lastFilter, filter);
            _lastFilter = filter;
            _seen = true;

            if (!unchanged)
            {
                return true;
            }

            // Vanilla raises this overlay at the same time as it clears the rows, and nothing hides it again unless
            // a refresh is marked pending, which the clear is what does. With the rows kept there is nothing to
            // cover, and the list shows the overlay on every visit, so take it back down here.
            var overlay = OverlayField.GetValue(__instance) as LobbyListFadeOverlay;
            if (overlay != null)
            {
                overlay.Hide();
            }

            // False skips vanilla, which is what hides every row and hands it back to the pool.
            return false;
        }

        private static bool Same(LobbySearchFilter a, LobbySearchFilter b)
        {
            return a.HideFull == b.HideFull
                && a.HideEmpty == b.HideEmpty
                && a.HidePasswordProtected == b.HidePasswordProtected
                && a.MissionPvpType == b.MissionPvpType
                && a.ServerType == b.ServerType
                && a.distanceFilter == b.distanceFilter
                && a.ignoreVersionFilter == b.ignoreVersionFilter;
        }
    }

    /// <summary>
    /// Re-sorts the list when a row is updated in place, which is what every update looks like while the rows are
    /// kept through a refresh.
    ///
    /// The game sorts when it places a fresh row into the list and when it rebuilds the whole list at the start of
    /// a refresh, so with the rows kept, a server that answers updates its row where it stands: the player count,
    /// the mission and the ping change, and the position, which is what the active sort key reads, does not. The
    /// placement path also inserts a row it already holds a second time rather than moving it, leaving another copy
    /// behind in the list the order is kept in, one more each refresh.
    ///
    /// The two update callbacks mark the list instead of sorting it where they stand, and the sort runs once per
    /// frame from the list's own update, so a refresh answered in a burst costs one re-sort rather than one per
    /// answer.
    ///
    /// That per-frame hook is also where the ping marker's animation and timeouts are ticked, so the list has one
    /// prefix from this mod rather than two.
    /// </summary>
    [HarmonyPatch]
    internal static class LobbyListResortPatch
    {
        private static readonly MethodInfo SortMethod = AccessTools.Method(typeof(LobbyList), "UpdateLobbyList");

        private static Action<LobbyList> _sort;

        /// <summary>The list an update has landed on and a re-sort is owed to.</summary>
        private static LobbyList _waiting;

        private static void Request(LobbyList list)
        {
            if (list != null)
            {
                _waiting = list;
            }
        }

        [HarmonyPatch(typeof(LobbyList), "OnLobbyDataUpdated")]
        [HarmonyPostfix]
        private static void AfterDataUpdated(LobbyList __instance)
        {
            Request(__instance);
        }

        [HarmonyPatch(typeof(LobbyList), "OnLobbyPingUpdated")]
        [HarmonyPostfix]
        private static void AfterPingUpdated(LobbyList __instance)
        {
            Request(__instance);
        }

        [HarmonyPatch(typeof(LobbyList), "Update")]
        [HarmonyPrefix]
        private static void BeforeUpdate(LobbyList __instance)
        {
            var mark = Cost.Mark();
            try
            {
                TickAndSort(__instance);
            }
            finally
            {
                Cost.Add("frame", mark);
                Cost.Frame();
                ServerBrowser.ReportCopies();
            }
        }

        /// <summary>The per-frame work itself, apart from the timing wrapped around it.</summary>
        private static void TickAndSort(LobbyList __instance)
        {
            StaleRowPatch.Tick(__instance);
            ServerBrowser.RefreshTick();
            ServerBrowser.RefreshListedTick();
            ServerBrowser.Admit(__instance);

            var waiting = _waiting;
            if (waiting == null)
            {
                // Covers a browser that was destroyed with a re-sort still owed.
                _waiting = null;
                return;
            }

            if (!ReferenceEquals(waiting, __instance))
            {
                return;
            }

            _waiting = null;

            if (SortMethod == null)
            {
                return;
            }

            try
            {
                if (_sort == null)
                {
                    _sort = (Action<LobbyList>)SortMethod.CreateDelegate(typeof(Action<LobbyList>));
                }

                _sort(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not re-sort the server list: " + e);
            }
        }
    }

    /// <summary>
    /// Keeps the servers themselves when the browser closes, not just the rows while it is open.
    ///
    /// Leaving the multiplayer screen destroys the browser, and its OnDestroy drops the whole lobby cache with it,
    /// so the next visit starts from nothing and shows an empty list until Steam answers again. The lobby object
    /// outlives the screen (it is marked DontDestroyOnLoad), and this call is its only cache clear, so the cache
    /// survives with it.
    /// </summary>
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.ClearLobbyCache))]
    internal static class LobbyCacheKeepPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !Plugin.KeepServersListed.Value;
        }
    }

    /// <summary>
    /// Puts the servers kept from the last visit back on screen when the browser opens.
    ///
    /// A new browser object starts with no rows of its own, so the kept entries have to be announced again, exactly
    /// as the game announces an entry it has just heard about. They are announced after the first refresh request,
    /// which is where every entry's InList flag is cleared, so nothing here re-enters the ping loop, and the refresh
    /// itself updates each row in place as answers arrive.
    /// </summary>
    [HarmonyPatch(typeof(LobbyList), "Start")]
    internal static class LobbyListStartPatch
    {
        private static readonly FieldInfo LobbiesField = AccessTools.Field(typeof(SteamLobby), "_lobbyData");

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!Plugin.KeepServersListed.Value || LobbiesField == null)
            {
                return;
            }

            var manager = SteamLobby.instance;
            var lobbies = manager != null
                ? LobbiesField.GetValue(manager) as Dictionary<CSteamID, LobbyInstance>
                : null;
            if (lobbies == null)
            {
                return;
            }

            var shown = 0;
            foreach (var lobby in lobbies.Values)
            {
                if (lobby is ServerLobbyInstance || lobby is PlayerLobbyInstance)
                {
                    ServerBrowser.Announce(manager, lobby);
                    shown++;
                }
            }

            if (shown > 0)
            {
                Plugin.Verbose("Put " + shown + " servers from the last visit back in the list.");
            }
        }
    }
}
