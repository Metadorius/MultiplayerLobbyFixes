using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MapMouseSelect
{
    /// <summary>
    /// Puts a Refresh button in the server details window, immediately left of Join, which asks that one server for
    /// its rules and its own details again: the mission, the map, the description, the duration, the name, the player
    /// counts and the ping. Two queries, for the server the player is looking at, instead of a full refresh that
    /// re-asks every server and still spends a query on each.
    ///
    /// The button is a copy of the Join button, so it lays out like the window's own buttons, dressed in the colours
    /// and the icon of the browser's refresh button, with the game's listeners taken off it. When the answers land,
    /// the window is reopened through the game's own Show, which is what makes it read the texts, the uptime and the
    /// preview image again.
    /// </summary>
    [HarmonyPatch(typeof(LobbyDetailsModal), "Show")]
    internal static class LobbyRefreshButtonPatch
    {
        private static readonly FieldInfo JoinField = AccessTools.Field(typeof(LobbyDetailsModal), "joinButton");

        private static readonly FieldInfo LobbyField = AccessTools.Field(typeof(LobbyDetailsModal), "lobby");

        private static readonly FieldInfo RefreshField = AccessTools.Field(typeof(LobbyList), "refreshButton");

        private static readonly MethodInfo HideMethod = AccessTools.Method(typeof(LobbyDetailsModal), "Hide");

        private static Button _button;

        private static LobbyDetailsModal _modal;

        private static LobbyList _list;

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

        private static void Layout(LobbyDetailsModal modal, LobbyList list)
        {
            var join = JoinField != null ? JoinField.GetValue(modal) as Button : null;
            if (join == null)
            {
                return;
            }

            if (_button == null)
            {
                var copy = UnityEngine.Object.Instantiate(join.gameObject, join.transform.parent);
                copy.name = "MapMouseSelectRefreshServer";

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

                _modal = modal;
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
                var lobby = _modal != null && LobbyField != null
                    ? LobbyField.GetValue(_modal) as ServerLobbyInstance
                    : null;
                if (lobby == null)
                {
                    return;
                }

                ServerBrowser.RefreshServer(lobby, () => Reopen(lobby));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not refresh a server: " + e);
            }
        }

        /// <summary>
        /// Reopens the window through the game's own Show, which is what re-reads the texts, the uptime and the
        /// preview image; the loop behind it captured the uptime when it started, so it cannot be corrected in place.
        /// </summary>
        private static void Reopen(ServerLobbyInstance lobby)
        {
            if (_modal == null || _list == null || LobbyField == null || HideMethod == null)
            {
                return;
            }

            if (!ReferenceEquals(LobbyField.GetValue(_modal), lobby))
            {
                return;
            }

            try
            {
                HideMethod.Invoke(_modal, null);
                _modal.Show(_list, lobby);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not reopen the server details window: " + e);
            }
        }
    }
}
