using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using UnityEngine.UI;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Keeps the details modal's Join button clickable when the player count says the server is full.
    ///
    /// That count is what the server last registered with Steam's master list, not what it has now, so a server
    /// reading 32/32 is often joinable and one reading 20/32 is often not. The modal disables the button on that
    /// number alone, and a disabled uGUI button cannot be clicked at all, so the count is the only thing standing
    /// between the player and a join attempt. The join path itself checks the version and the password and nothing
    /// else: a server that really is full refuses the join, and the join overlay says so.
    ///
    /// The decision is made inside the modal's per-second async refresh, which compiles to a struct state machine
    /// whose MoveNext takes its receiver by reference. Harmony cannot wrap that method on Mono (it emits invalid
    /// IL), so this re-enables the button from the lobby list's own Update instead, which runs while the browser is
    /// open. Vanilla gets one frame of a disabled button per second, which the eye does not catch, and the button is
    /// only touched when vanilla has just turned it off.
    /// </summary>
    [HarmonyPatch(typeof(LobbyList), "Update")]
    internal static class FullServerJoinPatch
    {
        private static readonly FieldInfo PopupField = AccessTools.Field(typeof(LobbyList), "lobbyPopup");

        private static readonly FieldInfo LobbyField = AccessTools.Field(typeof(LobbyDetailsModal), "lobby");

        private static readonly FieldInfo JoinButtonField = AccessTools.Field(typeof(LobbyDetailsModal), "joinButton");

        [HarmonyPostfix]
        private static void Postfix(LobbyList __instance)
        {
            if (!Plugin.JoinFullServers.Value
                || PopupField == null
                || LobbyField == null
                || JoinButtonField == null)
            {
                return;
            }

            var modal = PopupField.GetValue(__instance) as LobbyDetailsModal;
            if (modal == null)
            {
                return;
            }

            // The modal clears its lobby when it hides, so a lobby means it is on screen.
            if (LobbyField.GetValue(modal) == null)
            {
                return;
            }

            var button = JoinButtonField.GetValue(modal) as Button;
            if (button != null && !button.interactable)
            {
                button.interactable = true;
            }
        }
    }
}
