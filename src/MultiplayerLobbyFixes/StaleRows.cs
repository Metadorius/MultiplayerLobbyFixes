using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.MissionEditorScripts.Buttons;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;

namespace MapMouseSelect
{
    /// <summary>
    /// Draws the two query markers, the ping column's and the mission cell's, the way the game marks a server
    /// with too many players: a glyph in the cell, with a tooltip that explains it on hover. The ping marker rides
    /// the game's own repaint of the ping column, and the rules marker sits on the map and mission text, the two
    /// things a rules answer carries.
    ///
    /// The glyph follows the query cycle. While a query is out the row shows a pulsing sync icon, so a run of them
    /// reads as the browser working through the list. When a query comes back with nothing useful, the row shows
    /// the warning triangle until one is answered; the servers the ping loop never asks, the unreachable ones
    /// listed from the master list and the ones whose rules never answered, keep the triangle too. An answered
    /// query leaves the row bare.
    ///
    /// Vanilla never repaints a row when a ping fails, so the state is pushed through the query callbacks, which
    /// repaint the row through Repaint, and the pulse is stepped from the list's per-frame hook over in
    /// LobbyListResortPatch.
    ///
    /// The same two markers also sit in the details window, on the rows the two queries fill there: the window's
    /// ping row and its mission row. The window rewrites its ping cell from its own loop every second, and
    /// that loop is an async method, so the markers are drawn again from the MoveNext of the loop right after each
    /// write, and from the list's own per-frame hook while the window is up.
    ///
    /// The hover catcher is the game's own ShowHoverText, added to the cell's text and pointed at a clone of the
    /// tooltip the player-count warning uses, so it looks and behaves like vanilla's; one clone serves both
    /// markers, since what a hovered cell says is its own catcher's text. A disabled ShowHoverText
    /// receives no pointer events, which is how the game hides its own, so the same trick hides ours, at the cost
    /// of the exit event never arriving to close a tooltip that is already open.
    /// </summary>
    [HarmonyPatch(typeof(LobbyListItem), "SetPingTextAndColor")]
    internal static class StaleRowPatch
    {
        /// <summary>
        /// The triangle the player-count warning uses, in that warning's own yellow, so the marker reads as the
        /// same warning the game already shows. The font tag is how the game asks for an icon glyph: the cells'
        /// own font has none of them, which is why the glyph shows as a box without it. The size tag is the game's
        /// own treatment for an icon inline with text (the sort headers): at full size its metrics lift the line,
        /// which pushes the cell's text down a little and leaves the glyph sitting high.
        /// </summary>
        private const string WarningIcon =
            "<color=#FFFF00FF><size=80%><font=MaterialSymbolsSharp>\ue002</font></size></color> ";

        private const string WarningText = "May be stale, no response to the latest status query";

        private const string RulesWarningText = "May be stale, no response to the latest rules query";

        /// <summary>The game's two-arrows sync icon, the closest thing to a spinner in the icon set.</summary>
        private const string SpinnerIcon = "\ue86a";

        /// <summary>The greys the sync icon pulses between while a query is out, and the pulse timing.</summary>
        private static readonly Color SpinnerDim = new Color32(0x53, 0x5b, 0x69, 0xff);

        private static readonly Color SpinnerBright = new Color32(0xae, 0xb8, 0xc8, 0xff);

        /// <summary>How many steps one pulse runs through, and how long a step takes.</summary>
        private const int PulseSteps = 7;

        private const float PulsePeriod = 1.2f;

        /// <summary>When a query that never called back is written off as failed, so a row cannot pulse forever.</summary>
        private const float AskTimeout = 10f;

        private static readonly FieldInfo LobbyField = AccessTools.Field(typeof(LobbyListItem), "<lobby>k__BackingField");

        private static readonly FieldInfo PingTextField = AccessTools.Field(typeof(LobbyListItem), "pingText");

        private static readonly FieldInfo MissionField = AccessTools.Field(typeof(LobbyListItem), "missionNameText");

        private static readonly FieldInfo DetailsPingField = AccessTools.Field(typeof(LobbyDetailsModal), "pingText");

        private static readonly FieldInfo DetailsMissionField =
            AccessTools.Field(typeof(LobbyDetailsModal), "missionNameText");

        private static readonly FieldInfo ListField = AccessTools.Field(typeof(LobbyListItem), "multiplayerLobbyList");

        private static readonly FieldInfo HoverSourceField = AccessTools.Field(typeof(LobbyList), "TooManyPlayerHover");

        private static readonly FieldInfo HoverField = AccessTools.Field(typeof(ShowHoverText), "hover");

        /// <summary>
        /// The mod's own copy of the list's warning tooltip. Shares the look, not the object: the game's warning
        /// hands its tooltip a null text and expects whatever is already in it to stay, so borrowing it would mean
        /// the player-count warning showed this marker's text.
        /// </summary>
        private static HoverText _tooltip;

        [HarmonyPostfix]
        private static void Postfix(LobbyListItem __instance)
        {
            if (!Plugin.MarkStaleServers.Value)
            {
                return;
            }

            var lobby = LobbyField != null ? LobbyField.GetValue(__instance) as ServerLobbyInstance : null;
            var ping = PingTextField != null ? PingTextField.GetValue(__instance) as TextMeshProUGUI : null;
            if (lobby == null || ping == null)
            {
                return;
            }

            GlyphFor(ServerBrowser.PingStateOf(ServerBrowser.IdOf(lobby)), WarningText, out var glyph, out var reason);

            if (glyph != null && !HasGlyph(ping.text))
            {
                // Vanilla has just written the ping, or nothing when it has none, and this is added on top of it.
                ping.text = glyph + ping.text;
            }

            SetHover(ping, ListOf(__instance), reason);
        }

        private static bool HasGlyph(string text)
        {
            return text.IndexOf('\ue002') >= 0 || text.IndexOf('\ue86a') >= 0;
        }

        /// <summary>
        /// The list's per-frame housekeeping: remembers the browser in use, pulses any query still out, and writes
        /// off a query that never called back, so a row cannot pulse forever over one Steam forgot.
        /// </summary>
        internal static void Tick(LobbyList list)
        {
            _list = list;

            if (!Plugin.MarkStaleServers.Value)
            {
                return;
            }

            TickDetails();

            if (ServerBrowser.Asking.Count == 0 && ServerBrowser.RulesAsking.Count == 0)
            {
                return;
            }

            var now = Time.time;
            List<ServerLobbyInstance> stuck = null;
            foreach (var ask in ServerBrowser.Asking.Values)
            {
                if (now - ask.When > AskTimeout)
                {
                    stuck = stuck ?? new List<ServerLobbyInstance>();
                    stuck.Add(ask.Lobby);
                }
            }

            if (stuck != null)
            {
                foreach (var lobby in stuck)
                {
                    ServerBrowser.PingFailed(ServerBrowser.IdOf(lobby));
                    Repaint(lobby);
                }
            }

            stuck = null;
            foreach (var ask in ServerBrowser.RulesAsking.Values)
            {
                if (now - ask.When > AskTimeout)
                {
                    stuck = stuck ?? new List<ServerLobbyInstance>();
                    stuck.Add(ask.Lobby);
                }
            }

            if (stuck != null)
            {
                foreach (var lobby in stuck)
                {
                    ServerBrowser.RulesFailed(ServerBrowser.IdOf(lobby));
                    Repaint(lobby);
                }
            }

            var step = PulseStep();
            if (step == _step)
            {
                return;
            }

            _step = step;
            foreach (var ask in ServerBrowser.Asking.Values)
            {
                Repaint(ask.Lobby);
            }

            foreach (var ask in ServerBrowser.RulesAsking.Values)
            {
                Repaint(ask.Lobby);
            }
        }

        /// <summary>
        /// Re-reads both markers on a row: the ping column through vanilla's own update, and the mission cell
        /// through the same drawing it gets when it is shown. The query callbacks call this because vanilla
        /// repaints nothing itself when a ping fails or when a query settles.
        /// </summary>
        internal static void Repaint(ServerLobbyInstance lobby)
        {
            try
            {
                var list = _list;
                if (list == null || lobby == null || AllLobbiesField == null)
                {
                    return;
                }

                var rows = AllLobbiesField.GetValue(list) as Dictionary<LobbyInstance, LobbyListItem>;
                LobbyListItem row;
                if (rows != null && rows.TryGetValue(lobby, out row) && row != null)
                {
                    row.UpdatePing();
                    DrawMission(row);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not repaint a server row: " + e);
            }
        }

        /// <summary>
        /// Draws the rules marker in the mission cell, which the game writes only when a row is shown: the cell is
        /// composed here the way Show composes it, so the marker is the only difference.
        /// </summary>
        internal static void DrawMission(LobbyListItem row)
        {
            if (!Plugin.MarkStaleServers.Value)
            {
                return;
            }

            var mission = MissionField != null ? MissionField.GetValue(row) as TextMeshProUGUI : null;
            if (mission == null)
            {
                return;
            }

            var lobby = LobbyField != null ? LobbyField.GetValue(row) as ServerLobbyInstance : null;
            if (lobby == null)
            {
                // Not a dedicated server's row, so nothing here has a rules query; a marker the row still carries
                // from an earlier use is taken back down.
                SetHover(mission, null, string.Empty);
                return;
            }

            GlyphFor(ServerBrowser.RulesStateOf(ServerBrowser.IdOf(lobby)), RulesWarningText, out var glyph, out var reason);

            var text = row.MissionName ?? string.Empty;
            if (!string.IsNullOrEmpty(row.MapName))
            {
                text = string.IsNullOrEmpty(row.MissionName)
                    ? row.MapName
                    : row.MapName + " | " + row.MissionName;
            }

            mission.text = glyph != null ? glyph + text : text;

            SetHover(mission, ListOf(row), reason);
        }

        private static string Spinner()
        {
            var shade = Color.Lerp(SpinnerDim, SpinnerBright, PulseStep() / (float)PulseSteps);
            return "<color=#" + ColorUtility.ToHtmlStringRGB(shade)
                + "><size=80%><font=MaterialSymbolsSharp>" + SpinnerIcon + "</font></size></color> ";
        }

        /// <summary>The pulse as a handful of steps rather than per frame, so a repaint lands on each step only.</summary>
        private static int PulseStep()
        {
            var t = Mathf.Repeat(Time.time / PulsePeriod, 1f);
            var up = t < 0.5f ? t * 2f : (1f - t) * 2f;
            return Mathf.RoundToInt(up * PulseSteps);
        }

        /// <summary>The list a row belongs to, which is where the hover tooltip is cloned from.</summary>
        private static LobbyList ListOf(LobbyListItem item)
        {
            return ListField != null ? ListField.GetValue(item) as LobbyList : null;
        }

        /// <summary>The glyph and the hover message a marker shows for a query cycle.</summary>
        private static void GlyphFor(
            ServerBrowser.QueryState state, string warning, out string glyph, out string reason)
        {
            if (state == ServerBrowser.QueryState.Asking)
            {
                glyph = Spinner();
                reason = string.Empty;
            }
            else if (state == ServerBrowser.QueryState.Answered)
            {
                glyph = null;
                reason = string.Empty;
            }
            else
            {
                glyph = WarningIcon;
                reason = warning;
            }
        }

        /// <summary>
        /// Listens for the pointer while there is something to say, and stops while there is not. A component
        /// that is switched off is never sent an OnPointerExit, so a tooltip it opened is closed by hand first;
        /// otherwise the message stays on screen after the row stops being stale.
        /// </summary>
        private static void SetHover(TextMeshProUGUI target, LobbyList list, string reason)
        {
            if (target == null)
            {
                return;
            }

            var hover = target.GetComponent<ShowHoverText>();
            var wanted = reason.Length > 0;

            if (hover == null)
            {
                if (!wanted)
                {
                    return;
                }

                hover = target.gameObject.AddComponent<ShowHoverText>();
            }

            var tooltip = HoverField != null ? HoverField.GetValue(hover) as HoverText : null;
            if (wanted && tooltip == null)
            {
                tooltip = list != null ? Tooltip(list) : null;
                if (tooltip != null)
                {
                    hover.SetHover(tooltip);
                }
            }

            if (wanted && tooltip != null)
            {
                hover.SetText(reason);
                hover.enabled = true;
                return;
            }

            // The game hides its own hover catchers the same way, by disabling them, which also means the
            // tooltip has to be closed here rather than waiting for a pointer exit that will never arrive.
            if (tooltip != null)
            {
                tooltip.Hide(hover);
            }

            hover.enabled = false;
        }

        private static HoverText Tooltip(LobbyList list)
        {
            if (_tooltip != null)
            {
                return _tooltip;
            }

            var source = HoverSourceField != null ? HoverSourceField.GetValue(list) as HoverText : null;
            if (source == null)
            {
                return null;
            }

            // Cloned alongside the original so it sits on the same canvas and moves the same way.
            var clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            clone.name = "MapMouseSelectStaleTooltip";
            _tooltip = clone.GetComponent<HoverText>();
            return _tooltip;
        }

        /// <summary>Remembers the details window and the server it was opened for, from the window's own Show.</summary>
        internal static void WatchDetails(LobbyDetailsModal modal, LobbyInstance lobby)
        {
            _details = modal;
            _detailsLobby = lobby as ServerLobbyInstance;
        }

        /// <summary>Takes the window's hovers down when it closes, since a hidden cell gets no pointer exit.</summary>
        internal static void CloseDetails()
        {
            var modal = _details;
            if (modal != null)
            {
                SetHover(
                    DetailsPingField != null ? DetailsPingField.GetValue(modal) as TextMeshProUGUI : null,
                    null,
                    string.Empty);
                SetHover(
                    DetailsMissionField != null ? DetailsMissionField.GetValue(modal) as TextMeshProUGUI : null,
                    null,
                    string.Empty);
            }

            _details = null;
            _detailsLobby = null;
        }

        /// <summary>
        /// Draws the window's two markers on the rows the two queries fill there: the ping row and the mission
        /// row. The window's own loop rewrites the ping cell from CalculatePing every second, so the cell is
        /// composed from the same source with the marker in front, and only written when it actually differs.
        /// </summary>
        internal static void TickDetails()
        {
            if (!Plugin.MarkStaleServers.Value)
            {
                return;
            }

            var modal = _details;
            var lobby = _detailsLobby;
            if (modal == null || lobby == null)
            {
                return;
            }

            var id = ServerBrowser.IdOf(lobby);

            var ping = DetailsPingField != null ? DetailsPingField.GetValue(modal) as TextMeshProUGUI : null;
            if (ping != null)
            {
                GlyphFor(ServerBrowser.PingStateOf(id), WarningText, out var glyph, out var reason);

                var value = lobby.CalculatePing();
                var body = value != null ? value.Value + "ms" : string.Empty;
                var text = glyph != null ? glyph + body : body;
                if (ping.text != text)
                {
                    ping.text = text;
                }

                SetHover(ping, _list, reason);
            }

            var mission = DetailsMissionField != null
                ? DetailsMissionField.GetValue(modal) as TextMeshProUGUI
                : null;
            if (mission != null)
            {
                GlyphFor(ServerBrowser.RulesStateOf(id), RulesWarningText, out var glyph, out var reason);

                var body = lobby.MissionNameSanitized ?? string.Empty;
                var text = glyph != null ? glyph + body : body;
                if (mission.text != text)
                {
                    mission.text = text;
                }

                SetHover(mission, _list, reason);
            }
        }

        private static readonly FieldInfo AllLobbiesField = AccessTools.Field(typeof(LobbyList), "allLobbies");

        /// <summary>The browser the rows belong to, set by Tick while one is open.</summary>
        private static LobbyList _list;

        /// <summary>The details window while one is open, and the server it shows.</summary>
        private static LobbyDetailsModal _details;

        private static ServerLobbyInstance _detailsLobby;

        private static int _step = -1;
    }

    /// <summary>
    /// Puts the rules marker on the mission cell when a row is shown, which is the only time the game writes that
    /// cell: the server may already have a rules query out, or a settled one from an earlier refresh.
    /// </summary>
    [HarmonyPatch(typeof(LobbyListItem), nameof(LobbyListItem.Show))]
    internal static class RulesRowPatch
    {
        [HarmonyPostfix]
        private static void Postfix(LobbyListItem __instance)
        {
            StaleRowPatch.DrawMission(__instance);
        }
    }

    /// <summary>
    /// Draws the player-count warning at the size the game gives an icon that sits inline with text, since the
    /// game draws that one at full size, which lifts the line and pushes the row's text down the same way the
    /// markers' glyphs did before they were sized down. The warning's glyph is the only icon the player cell
    /// holds, so the font tag finds it without touching the counts.
    /// </summary>
    [HarmonyPatch(typeof(LobbyListItem), nameof(LobbyListItem.Show))]
    internal static class PlayerWarningPatch
    {
        private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(LobbyListItem), "playerText");

        [HarmonyPostfix]
        private static void Postfix(LobbyListItem __instance)
        {
            try
            {
                var players = PlayerField != null ? PlayerField.GetValue(__instance) as TextMeshProUGUI : null;
                if (players == null)
                {
                    return;
                }

                players.text = players.text.Replace(
                    "<font=MaterialSymbolsSharp>\ue002</font>",
                    "<size=80%><font=MaterialSymbolsSharp>\ue002</font></size>");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not size down the player-count warning: " + e);
            }
        }
    }

    /// <summary>
    /// Follows the details window: its markers are drawn while it is open, and its cells' hovers are taken down
    /// when it closes, since a hidden cell gets no pointer exit.
    /// </summary>
    [HarmonyPatch(typeof(LobbyDetailsModal))]
    internal static class DetailsMarkerPatch
    {
        [HarmonyPatch(nameof(LobbyDetailsModal.Show))]
        [HarmonyPostfix]
        private static void AfterShow(LobbyDetailsModal __instance, LobbyInstance lobby)
        {
            StaleRowPatch.WatchDetails(__instance, lobby);
        }

        [HarmonyPatch("Hide")]
        [HarmonyPostfix]
        private static void AfterHide()
        {
            StaleRowPatch.CloseDetails();
        }
    }

    /// <summary>
    /// Draws the window's markers again right after its own loop runs, which is the moment the loop has just
    /// rewritten the ping cell: the loop is an async method, so its body lives in a generated state machine, and
    /// the MoveNext of that state machine is the only hook its writer has.
    /// </summary>
    [HarmonyPatch]
    internal static class DetailsLoopPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            foreach (var type in typeof(LobbyDetailsModal).GetNestedTypes(
                BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (type.Name.StartsWith("<UpdateLoop>", StringComparison.Ordinal))
                {
                    return AccessTools.Method(type, "MoveNext");
                }
            }

            return null;
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            StaleRowPatch.TickDetails();
        }
    }
}
