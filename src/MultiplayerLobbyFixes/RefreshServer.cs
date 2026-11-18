using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Puts a Refresh button in the server details window, immediately left of Join, which asks that one server for
    /// its rules again: the mission, the map, the description and the duration.
    ///
    /// Since 0.34.6 a server's rules are only asked for when the window opens, and only if it does not already have
    /// an answer. That answer is cached on the entry for as long as the entry lives, so a server whose mission
    /// changed, or whose rules failed the first time, has no way to be asked again. This button is that way: it
    /// forgets the cached answer, asks again, and reads the window again when it lands, through the same method the
    /// game's own open uses.
    ///
    /// The button is a copy of the Join button, so it lays out like the window's own buttons, dressed in the colours
    /// and the icon of the browser's refresh button, with the game's listeners taken off it.
    /// </summary>
    [HarmonyPatch(typeof(LobbyDetailsModal), "Show")]
    internal static class LobbyRefreshButtonPatch
    {
        private static readonly FieldInfo JoinField = AccessTools.Field(typeof(LobbyDetailsModal), "joinButton");

        private static readonly FieldInfo LobbyField = AccessTools.Field(typeof(LobbyDetailsModal), "lobby");

        private static readonly FieldInfo RefreshField = AccessTools.Field(typeof(LobbyList), "refreshButton");

        private static readonly MethodInfo MissionDetailsMethod =
            AccessTools.Method(typeof(LobbyDetailsModal), "UpdateMissionDetails");

        private static readonly FieldInfo CancelSourceField =
            AccessTools.Field(typeof(LobbyDetailsModal), "detailsOpenCancelSource");

        private static Button _button;

        private static LobbyDetailsModal _modal;

        private static LobbyList _list;

        /// <summary>When the card's button takes clicks again, which is its own cooldown.</summary>
        private static float _readyAt;

        [HarmonyPostfix]
        private static void Postfix(LobbyDetailsModal __instance, LobbyList lobbyList)
        {
            if (!Plugin.RefreshServerButton.Value)
            {
                return;
            }

            try
            {
                _list = lobbyList;
                Layout(__instance, lobbyList);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not put the refresh button up: " + e);
            }
        }

        /// <summary>
        /// Keeps the button's clickability with the browser's own refresh button: it is grey while that one is
        /// grey, which is the game's cooldown after a refresh, and while this server's own cooldown runs.
        /// </summary>
        internal static void Tick(LobbyList list)
        {
            if (!Plugin.RefreshServerButton.Value || _button == null || list == null)
            {
                return;
            }

            var model = RefreshField != null ? RefreshField.GetValue(list) as Button : null;
            var ready = Time.time >= _readyAt && (model == null || model.interactable);
            if (_button.interactable != ready)
            {
                _button.interactable = ready;
            }
        }

        private static void Layout(LobbyDetailsModal modal, LobbyList list)
        {
            var join = JoinField != null ? JoinField.GetValue(modal) as Button : null;
            if (join == null)
            {
                return;
            }

            _modal = modal;

            if (_button == null)
            {
                var copy = UnityEngine.Object.Instantiate(join.gameObject, join.transform.parent);
                copy.name = "MultiplayerLobbyFixesRefreshServer";

                _button = copy.GetComponent<Button>();
                if (_button == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    return;
                }

                // The copy carries the Join button's serialized setup, not the listener Awake added, but there is no
                // reason to leave that to chance.
                _button.onClick.RemoveAllListeners();

                // Immediately left of Join, which is where the button was asked for.
                copy.transform.SetSiblingIndex(join.transform.GetSiblingIndex());

                Dress(_button, list);

                _button.onClick.AddListener(new UnityAction(Clicked));
                Plugin.Verbose("Added the refresh button to the server details window.");
            }

            // Player-hosted lobbies have no rules to ask for.
            var lobby = LobbyField != null ? LobbyField.GetValue(modal) as LobbyInstance : null;
            _button.gameObject.SetActive(lobby is ServerLobbyInstance);
        }

        /// <summary>
        /// Takes the browser's own refresh button as the model for everything that is drawn: the colour block, the
        /// sprite and colour of each part including the border, and the icon. The label's font stays the one the
        /// details window's own buttons use, with the icon wrapped in the game's icon font tag.
        /// </summary>
        private static void Dress(Button button, LobbyList list)
        {
            var model = RefreshField != null && list != null ? RefreshField.GetValue(list) as Button : null;
            if (model == null)
            {
                return;
            }

            button.interactable = true;
            button.transition = model.transition;
            button.spriteState = model.spriteState;
            button.colors = model.colors;

            var mine = Parts(button);
            var theirs = Parts(model);
            if (mine.Count == theirs.Count)
            {
                for (var i = 0; i < mine.Count; i++)
                {
                    mine[i].color = theirs[i].color;
                    mine[i].sprite = theirs[i].sprite;
                    mine[i].type = theirs[i].type;
                }
            }

            // The border is an effect on the button's own image rather than a part of its own.
            var mineEdge = button.GetComponent<Shadow>();
            var theirsEdge = model.GetComponent<Shadow>();
            if (mineEdge != null && theirsEdge != null)
            {
                mineEdge.effectColor = theirsEdge.effectColor;
                mineEdge.effectDistance = theirsEdge.effectDistance;
                mineEdge.useGraphicAlpha = theirsEdge.useGraphicAlpha;
            }

            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                // The icon and the word, as the browser's own button carries them, with the icon at the size the
                // game gives an icon that sits inline with text. This label does not parse escapes, so the
                // character goes in directly instead of the escape the game's own text holds.
                label.text = "<size=80%>" + GoogleIconFont.FontString(GoogleIconFont.REFRESH_TWO_ARROWS_ICON)
                    + "</size> Refresh";

                var modelLabel = model.GetComponentInChildren<TextMeshProUGUI>();
                if (modelLabel != null)
                {
                    // The font stays the details window's own; only the colour follows the model.
                    label.color = modelLabel.color;
                }
            }

            Describe(button, mine, "ours");
            Describe(model, theirs, "theirs");
        }

        /// <summary>The drawn pieces of a button, its own background last, so two buttons' pieces can be matched up.</summary>
        private static List<Image> Parts(Button button)
        {
            var parts = new List<Image>();
            foreach (var image in button.GetComponentsInChildren<Image>(true))
            {
                if (image.gameObject != button.gameObject)
                {
                    parts.Add(image);
                }
            }

            var background = button.targetGraphic as Image;
            if (background != null)
            {
                parts.Add(background);
            }

            return parts;
        }

        /// <summary>One line of what a button is made of, for when the two do not match up.</summary>
        private static void Describe(Button button, List<Image> parts, string which)
        {
            var edge = button.GetComponent<Shadow>();
            var said = "Refresh button, " + which + ", " + parts.Count + " parts"
                + ", edge=" + (edge != null ? "#" + ColorUtility.ToHtmlStringRGB(edge.effectColor) : "none")
                + ", label=\"" + Label(button) + "\"";
            foreach (var part in parts)
            {
                said += " [" + part.name
                    + " " + (part == button.targetGraphic ? "background" : "part")
                    + " colour=#" + ColorUtility.ToHtmlStringRGB(part.color)
                    + " sprite=" + (part.sprite != null ? part.sprite.name : "none") + "]";
            }

            // The frame around it is drawn above the button, so the chain is worth seeing too.
            var above = button.transform.parent;
            for (var i = 0; i < 3 && above != null; i++)
            {
                var image = above.GetComponent<Image>();
                said += " <" + above.name
                    + (image != null
                        ? " #" + ColorUtility.ToHtmlStringRGB(image.color)
                            + (image.sprite != null ? " " + image.sprite.name : " no-sprite")
                        : " no-image")
                    + ">";
                above = above.parent;
            }

            var kinds = "";
            foreach (var component in button.GetComponentsInChildren<Component>(true))
            {
                if (!(component is Transform))
                {
                    kinds += " " + component.GetType().Name;
                }
            }

            Plugin.Verbose(said + " kinds:" + kinds);
        }

        private static string Label(Button button)
        {
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            return label != null ? label.text : "none";
        }

        private static void Clicked()
        {
            try
            {
                var modal = _modal;
                var lobby = modal != null && LobbyField != null
                    ? LobbyField.GetValue(modal) as ServerLobbyInstance
                    : null;
                if (lobby == null || !ServerBrowser.IdOf(lobby).IsValid())
                {
                    return;
                }

                if (Time.time < _readyAt)
                {
                    return;
                }

                var accepted = ServerQuery.Ask(lobby, copy => Fetched(modal, lobby, copy != null));
                if (!accepted)
                {
                    return;
                }

                _readyAt = Time.time + Plugin.RefreshCooldownSeconds.Value;

                // The row and the card both show the ask through the markers, which read the query's own state.
                StaleRowPatch.Repaint(lobby);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not refresh a server: " + e);
            }
        }

        /// <summary>
        /// Reads the window again once the master list has answered, and asks the server for its rules too.
        ///
        /// The master copy carries the tags, and nothing more: the description chunks and the workshop id never fit
        /// in them, so those only ever arrive from a rules query. The entry keeps the last ones it reported, which
        /// SetDetails puts back, so nothing on the card degrades while the answer is out; the rules query is what
        /// freshens them. A server that did not answer the master list keeps its card as it is.
        /// </summary>
        private static void Fetched(LobbyDetailsModal modal, ServerLobbyInstance lobby, bool answered)
        {
            StaleRowPatch.Repaint(lobby);

            if (!answered)
            {
                return;
            }

            var manager = SteamLobby.instance;
            if (manager != null && !lobby.RulesLoading)
            {
                try
                {
                    manager.QueryServerRules(lobby, TokenOf(modal), () => RulesFetched(modal, lobby));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Could not ask a server for its rules: " + e);
                }
            }

            Read(modal, lobby);
        }

        /// <summary>The rules answer landed, or did not: either way the card is read from what the entry now holds.</summary>
        private static void RulesFetched(LobbyDetailsModal modal, ServerLobbyInstance lobby)
        {
            StaleRowPatch.Repaint(lobby);
            Read(modal, lobby);
        }

        /// <summary>
        /// Reads the window again through the method its own open calls: the mission, the map, the description and
        /// the preview image are all written there.
        /// </summary>
        private static void Read(LobbyDetailsModal modal, ServerLobbyInstance lobby)
        {
            try
            {
                // It checks that the window still shows this server before writing anything.
                MissionDetailsMethod?.Invoke(modal, new object[] { lobby });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not read the details window again: " + e);
            }
        }

        /// <summary>
        /// The window's own token, which cancels its queries when the window closes, so a press that is walked away
        /// from does not leave a query running on Steam's side.
        /// </summary>
        private static CancellationToken TokenOf(LobbyDetailsModal modal)
        {
            var source = modal != null && CancelSourceField != null
                ? CancelSourceField.GetValue(modal) as CancellationTokenSource
                : null;
            return source != null ? source.Token : CancellationToken.None;
        }
    }
}
