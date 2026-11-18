using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using UnityEngine;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// The mod's own per-frame component. BepInEx destroys its plugin host object in this game right after
    /// "Chainloader startup complete", so a component added in Plugin.Awake does not survive. The host is created
    /// lazily and rebuilt if it goes missing. Work that must preempt vanilla input stays in Harmony patches, which
    /// run before the game acts on the same input in the same frame.
    ///
    /// The browser's own per-frame work lives here too, since 0.34.6 took the list's Update away: the browser's
    /// rows and the details window are painted from here while either is on screen.
    /// </summary>
    internal sealed class ModHost : MonoBehaviour
    {
        private const string HostName = "MultiplayerLobbyFixes.Host";

        private static ModHost _instance;

        private static bool _survivalReported;

        internal static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }

            var host = new GameObject(HostName);
            Object.DontDestroyOnLoad(host);

            // HideAndDontSave keeps the host out of Resources.UnloadUnusedAssets, which the game calls between scenes.
            host.hideFlags = HideFlags.HideAndDontSave;

            _instance = host.AddComponent<ModHost>();
        }

        private void Update()
        {
            ServerListFailurePatch.TickRescue();
            SteamJoinWatchPatch.Tick();
            LeaveThenJoinPatch.Tick();
            RichPresenceJoin.Ensure();
            LaunchJoin.Tick();
            JoinFailurePatch.Ensure();
            Browser();

            if (!_survivalReported && Time.timeSinceLevelLoad > 60f)
            {
                _survivalReported = true;
                Plugin.Verbose("Mod host is still ticking after 60s of level time.");
            }
        }

        /// <summary>The markers and the Join button, which are only worth drawing while a browser is up.</summary>
        private static void Browser()
        {
            var mark = Cost.Mark();
            try
            {
                StaleRowPatch.TickDetails();

                var list = LobbyListFocus.Current;
                if (list != null)
                {
                    FullServerJoin.Tick(list);
                }
            }
            finally
            {
                Cost.Add("frame", mark);
                Cost.Frame();
                ServerBrowser.ReportCopies();
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }

    /// <summary>
    /// Remembers the browser while one is open, which is what the per-frame work needs and what the list's own
    /// update used to provide. The list is destroyed when the player leaves the multiplayer screen.
    /// </summary>
    internal static class LobbyListFocus
    {
        internal static LobbyList Current { get; private set; }

        internal static void Take(LobbyList list)
        {
            Current = list;
            StaleRowPatch.Focus(list);
        }

        internal static void Forget(LobbyList list)
        {
            if (ReferenceEquals(Current, list))
            {
                Current = null;
            }
        }
    }

    [HarmonyPatch(typeof(LobbyList), "Awake")]
    internal static class LobbyListFocusPatch
    {
        [HarmonyPostfix]
        private static void Postfix(LobbyList __instance)
        {
            LobbyListFocus.Take(__instance);
        }
    }

    [HarmonyPatch(typeof(LobbyList), "OnDestroy")]
    internal static class LobbyListFocusEndPatch
    {
        [HarmonyPostfix]
        private static void Postfix(LobbyList __instance)
        {
            LobbyListFocus.Forget(__instance);
        }
    }
}
