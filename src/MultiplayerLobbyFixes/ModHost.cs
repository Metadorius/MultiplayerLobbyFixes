using UnityEngine;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// The mod's own per-frame component. BepInEx destroys its plugin host object in this game right after
    /// "Chainloader startup complete", so a component added in Plugin.Awake does not survive. The host is created
    /// lazily and rebuilt if it goes missing. Work that must preempt vanilla input stays in Harmony patches, which
    /// run before the game acts on the same input in the same frame.
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

            if (!_survivalReported && Time.timeSinceLevelLoad > 60f)
            {
                _survivalReported = true;
                Plugin.Verbose("Mod host is still ticking after 60s of level time.");
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
}
