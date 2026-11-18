using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// Joins a server the moment the player asks, instead of waiting on a rules query first.
    ///
    /// The details window's own join path asks the server for its rules when it does not already have them, waits
    /// for that answer, and only then connects. The rules answer carries one thing the join needs, the
    /// `short_password` hash that the client's typo pre-check compares against, and nothing else: the version check
    /// reads the master list's tags, and the password itself is validated by the server over a nonce challenge. So
    /// for a server that takes no password the wait buys nothing, and for a password-protected one the prompt can
    /// go up right away.
    ///
    /// What is left is the pre-check refusing a join it cannot make: with no hash in hand there is nothing to
    /// compare a typed password against, and vanilla reads that as a mismatch. PasswordPrecheckPatch treats a
    /// missing hash as "cannot check" rather than "wrong", which is what the server's own check is for.
    /// </summary>
    [HarmonyPatch(typeof(LobbyDetailsModal), "Join")]
    internal static class InstantJoinPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(LobbyDetailsModal __instance)
        {
            try
            {
                return !InstantJoin.Try(__instance);
            }
            catch (Exception e)
            {
                // Vanilla's own path stands, which only costs the wait this was here to skip.
                Plugin.Log.LogError("Could not join a server straight away: " + e);
                return true;
            }
        }
    }

    internal static class InstantJoin
    {
        private static readonly FieldInfo LobbyField = AccessTools.Field(typeof(LobbyDetailsModal), "lobby");

        /// <summary>
        /// Starts the join without waiting for rules. Returns whether vanilla's own path should be skipped.
        /// </summary>
        internal static bool Try(LobbyDetailsModal modal)
        {
            var manager = SteamLobby.instance;
            var lobby = modal != null && LobbyField != null ? LobbyField.GetValue(modal) as LobbyInstance : null;
            if (manager == null || lobby == null)
            {
                return false;
            }

            string password;
            if (!lobby.IsPasswordProtected(out password))
            {
                Plugin.Verbose("Joining " + lobby.LobbyNameSanitized + " straight away: it takes no password.");
                manager.TryJoinLobby(lobby, null, true);
                return true;
            }

            var server = lobby as ServerLobbyInstance;
            if (server != null && server.RulesLoaded)
            {
                // The hash is in hand, so vanilla's own path already goes straight through.
                return false;
            }

            // The prompt's own submit goes to TryJoinLobby, which the pre-check patch lets through when there is
            // no hash to compare against, and the server decides there.
            Plugin.Verbose("Asking for the password of " + lobby.LobbyNameSanitized + " straight away.");
            LobbyPasswordPromptSingleton.ShowPrompt(lobby);
            return true;
        }
    }

    /// <summary>
    /// Treats the client's short-password pre-check as passed when there is no hash to check against.
    ///
    /// The hash is `SHA256(password)[0] &amp; 0x3F`, six bits, and it only arrives with a server's rules answer.
    /// Vanilla compares a typed password against it and refuses the join on a mismatch, which means a join started
    /// before the answer lands is refused locally over a server that would have accepted. A missing hash is read as
    /// "cannot check" here, and the server's own nonce challenge decides.
    ///
    /// Only dedicated servers are affected. A player hosted lobby keeps its hash in Steam's lobby data, so its
    /// check stays exactly as it was.
    /// </summary>
    [HarmonyPatch(typeof(LobbyPassword), nameof(LobbyPassword.TestShortPassword))]
    internal static class PasswordPrecheckPatch
    {
        private static readonly FieldInfo RulesField = AccessTools.Field(typeof(ServerLobbyInstance), "rules");

        [HarmonyPrefix]
        private static bool Prefix(LobbyInstance lobby, ref bool __result)
        {
            var server = lobby as ServerLobbyInstance;
            if (server == null || RulesField == null)
            {
                return true;
            }

            var rules = RulesField.GetValue(server) as Dictionary<string, string>;

            string hash;
            if (rules != null && rules.TryGetValue("short_password", out hash) && !string.IsNullOrEmpty(hash))
            {
                return true;
            }

            __result = true;
            Plugin.Verbose(
                "Joining " + lobby.LobbyNameSanitized + " without the password pre-check: its hash has not "
                + "arrived, so the server decides.");
            return false;
        }
    }
}
