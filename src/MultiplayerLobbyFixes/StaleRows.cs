using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.MissionEditorScripts.Buttons;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Marks a server that is not answering, the way the game marks a server with too many players: a glyph in the
    /// cell, with a tooltip that explains it on hover.
    ///
    /// Two marks, for the two things that can be wrong with a listed server. The ping column carries a triangle
    /// when this machine's master list could not reach the server at all, which is also the row the unreachable
    /// listing added. The mission cell carries one when the server's rules query failed, and a sync icon while it
    /// is still out; neither happens to a row on its own, since a listing asks nothing per server, so the mission
    /// mark shows up mostly in the details window, where a rules answer is asked for.
    ///
    /// The ping mark rides the game's own repaint of the ping column, which happens when a row is shown and when a
    /// ping arrives. The details window's cells are rewritten by its own loop every second, so they are composed
    /// again from their own sources each frame and only written when they differ.
    ///
    /// The hover catcher is the game's own ShowHoverText, added to the cell's text and pointed at a clone of the
    /// tooltip the player-count warning uses, so it looks and behaves like vanilla's; one clone serves both
    /// markers, since what a hovered cell says is its own catcher's text. A disabled ShowHoverText receives no
    /// pointer events, which is how the game hides its own, so the same trick hides ours, at the cost of the exit
    /// event never arriving to close a tooltip that is already open.
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

        /// <summary>The game's two-arrows sync icon, the closest thing to a spinner in the icon set.</summary>
        private const string LoadingIcon =
            "<color=#AEB8C8FF><size=80%><font=MaterialSymbolsSharp>\ue86a</font></size></color> ";

        private const string UnreachableText = "May be stale, no response to Steam's master list query";

        private const string RulesWarningText = "May be stale, no response to the latest rules query";

        private static readonly FieldInfo LobbyField = AccessTools.Field(typeof(LobbyListItem), "<lobby>k__BackingField");

        private static readonly FieldInfo PingTextField = AccessTools.Field(typeof(LobbyListItem), "pingText");

        private static readonly FieldInfo MissionField = AccessTools.Field(typeof(LobbyListItem), "missionNameText");

        private static readonly FieldInfo DetailsPingField = AccessTools.Field(typeof(LobbyDetailsModal), "pingText");

        private static readonly FieldInfo DetailsMissionField =
            AccessTools.Field(typeof(LobbyDetailsModal), "missionNameText");

        private static readonly FieldInfo ListField = AccessTools.Field(typeof(LobbyListItem), "multiplayerLobbyList");

        private static readonly FieldInfo AllLobbiesField = AccessTools.Field(typeof(LobbyList), "allLobbies");

        private static readonly FieldInfo HoverSourceField = AccessTools.Field(typeof(LobbyList), "TooManyPlayerHover");

        private static readonly FieldInfo HoverField = AccessTools.Field(typeof(ShowHoverText), "hover");

        /// <summary>
        /// The mod's own copy of the list's warning tooltip. Shares the look, not the object: the game's warning
        /// hands its tooltip a null text and expects whatever is already in it to stay, so borrowing it would mean
        /// the player-count warning showed this marker's text.
        /// </summary>
        private static HoverText _tooltip;

        /// <summary>The browser the rows belong to, remembered while one is open.</summary>
        private static LobbyList _list;

        /// <summary>The details window while one is open, and the server it shows.</summary>
        private static LobbyDetailsModal _details;

        private static ServerLobbyInstance _detailsLobby;

        [HarmonyPostfix]
        private static void Postfix(LobbyListItem __instance)
        {
            DrawPing(__instance);
        }

        /// <summary>Remembers the browser in use, for the readers that do not have it at hand.</summary>
        internal static void Focus(LobbyList list)
        {
            _list = list;
        }

        /// <summary>
        /// Draws the ping marker on a row, which the game has just written the ping of, or nothing when it has
        /// none. A single-server refresh that is out shows the sync icon; otherwise a server the master list could
        /// not reach keeps the warning triangle over whatever the row already says.
        /// </summary>
        internal static void DrawPing(LobbyListItem row)
        {
            if (!Plugin.ShowLatestQueryStatus.Value || row == null)
            {
                return;
            }

            var lobby = LobbyField != null ? LobbyField.GetValue(row) as ServerLobbyInstance : null;
            var ping = PingTextField != null ? PingTextField.GetValue(row) as TextMeshProUGUI : null;
            if (lobby == null || ping == null)
            {
                SetHover(ping, null, string.Empty);
                return;
            }

            if (ServerQuery.IsAsking(lobby))
            {
                if (!HasGlyph(ping.text))
                {
                    ping.text = LoadingIcon + ping.text;
                }

                SetHover(ping, ListOf(row), string.Empty);
                return;
            }

            var unreachable = ServerBrowser.IsUnreachable(lobby);

            if (unreachable && !HasGlyph(ping.text))
            {
                ping.text = WarningIcon + ping.text;
            }

            SetHover(ping, ListOf(row), unreachable ? UnreachableText : string.Empty);
        }

        /// <summary>
        /// Re-reads both markers on a row: the ping column through vanilla's own update, which is what draws it, and
        /// the mission cell through the same drawing it gets when it is shown. Called when a state the markers read
        /// has just changed.
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
            if (!Plugin.ShowLatestQueryStatus.Value || row == null)
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

            string glyph;
            string reason;
            if (lobby.RulesFailed)
            {
                glyph = WarningIcon;
                reason = RulesWarningText;
            }
            else if (lobby.RulesLoading)
            {
                // A query that is out says nothing on hover, the way the old in-progress glyph did.
                glyph = LoadingIcon;
                reason = string.Empty;
            }
            else
            {
                glyph = null;
                reason = string.Empty;
            }

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

        private static bool HasGlyph(string text)
        {
            return text != null && (text.IndexOf('\ue002') >= 0 || text.IndexOf('\ue86a') >= 0);
        }

        /// <summary>The list a row belongs to, which is where the hover tooltip is cloned from.</summary>
        private static LobbyList ListOf(LobbyListItem item)
        {
            return ListField != null ? ListField.GetValue(item) as LobbyList : null;
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
            clone.name = "MultiplayerLobbyFixesStaleTooltip";
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
        /// Draws the window's two markers on the cells the two queries fill there, the ping cell and the mission
        /// cell. The window's own loop rewrites the ping cell from CalculatePing every second, so the cell is
        /// composed from the same source with the marker in front, and only written when it actually differs.
        /// </summary>
        internal static void TickDetails()
        {
            if (!Plugin.ShowLatestQueryStatus.Value)
            {
                return;
            }

            var modal = _details;
            var lobby = _detailsLobby;
            if (modal == null || lobby == null)
            {
                return;
            }

            var ping = DetailsPingField != null ? DetailsPingField.GetValue(modal) as TextMeshProUGUI : null;
            if (ping != null)
            {
                var asking = ServerQuery.IsAsking(lobby);
                var unreachable = ServerBrowser.IsUnreachable(lobby);
                var value = lobby.CalculatePing();
                var body = value != null ? value.Value + "ms" : string.Empty;
                var text = asking
                    ? LoadingIcon + body
                    : unreachable
                        ? WarningIcon + body
                        : body;
                if (ping.text != text)
                {
                    ping.text = text;
                }

                SetHover(ping, _list, asking ? string.Empty : unreachable ? UnreachableText : string.Empty);
            }

            var mission = DetailsMissionField != null
                ? DetailsMissionField.GetValue(modal) as TextMeshProUGUI
                : null;
            if (mission != null)
            {
                string glyph;
                string reason;
                if (lobby.RulesFailed)
                {
                    glyph = WarningIcon;
                    reason = RulesWarningText;
                }
                else if (lobby.RulesLoading)
                {
                    glyph = LoadingIcon;
                    reason = string.Empty;
                }
                else
                {
                    glyph = null;
                    reason = string.Empty;
                }

                var body = lobby.MissionNameSanitized ?? string.Empty;
                var text = glyph != null ? glyph + body : body;
                if (mission.text != text)
                {
                    mission.text = text;
                }

                SetHover(mission, _list, reason);
            }
        }
    }

    /// <summary>
    /// Draws the rules marker in the mission cell when a row is shown, which is the only time the game writes that
    /// cell.
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
}
