using System;
using UnityEngine;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Edgegap matchmaker connection, shared by the game client (Find Match) and the dedicated server (backfill).
    /// Lives in Resources/EdgegapMatchmakerSettings. The auth token is the matchmaker's own token: it is meant to
    /// ship in game clients and grants no access to the Edgegap account.
    /// URL, token and profile can be overridden at launch (-edgegapMatchmakerUrl or EDGEGAP_MM_URL, and so on)
    /// to point a build at another matchmaker without rebuilding.
    /// </summary>
    [CreateAssetMenu(fileName = "EdgegapMatchmakerSettings", menuName = "Gameplay/Settings/EdgegapMatchmakerSettings")]
    public class EdgegapMatchmakerSettings : ScriptableObject
    {
        private const string k_ResourceName = "EdgegapMatchmakerSettings";

        [Tooltip("Matchmaker API URL from the Edgegap dashboard. Empty disables the Edgegap matchmaker.")]
        [SerializeField] private string m_BaseUrl;

        [Tooltip("Matchmaker Auth Token from the Edgegap dashboard (sent raw as the Authorization header).")]
        [SerializeField] private string m_AuthToken;

        [Tooltip("Profile name in the matchmaker configuration.")]
        [SerializeField] private string m_Profile = "metro";

        [Tooltip("Port name on the Edgegap app version.")]
        [SerializeField] private string m_PortName = "gameport";

        [Header("Server backfill")]
        [Tooltip("Players a server accepts. Backfill stops when it is full.")]
        [SerializeField] private int m_MaxPlayers = 150;

        [Tooltip("Backfills a server keeps open at once. Each one admits one player; more fill a server faster.")]
        [SerializeField] private int m_MaxOpenBackfills = 3;

        [Tooltip("Seconds a matched player has to connect before their seat is given to someone else.")]
        [SerializeField] private float m_ConnectionGraceSeconds = 60f;

        public string BaseUrl => Override("-edgegapMatchmakerUrl", "EDGEGAP_MM_URL", m_BaseUrl)?.Trim().TrimEnd('/');
        public string AuthToken => Override("-edgegapMatchmakerToken", "EDGEGAP_MM_TOKEN", m_AuthToken)?.Trim();
        public string Profile => Override("-edgegapMatchmakerProfile", "EDGEGAP_MM_PROFILE", m_Profile);
        public string PortName => m_PortName;
        public int MaxPlayers => OverrideInt("EDGEGAP_MAX_PLAYERS", m_MaxPlayers);
        public int MaxOpenBackfills => OverrideInt("EDGEGAP_MAX_OPEN_BACKFILLS", m_MaxOpenBackfills);
        public float ConnectionGraceSeconds => m_ConnectionGraceSeconds;

        public bool IsConfigured => !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(AuthToken);

        private static EdgegapMatchmakerSettings s_Instance;

        /// <summary>The settings asset, or null when the project has none.</summary>
        public static EdgegapMatchmakerSettings Instance
        {
            get
            {
                if (s_Instance == null)
                    s_Instance = Resources.Load<EdgegapMatchmakerSettings>(k_ResourceName);
                return s_Instance;
            }
        }

        /// <summary>True when Find Match should use the Edgegap matchmaker instead of Unity Gaming Services.</summary>
        public static bool IsEnabled => Instance != null && Instance.IsConfigured;

        private static string Override(string argument, string variable, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, argument);
            if (index >= 0 && index + 1 < args.Length)
                return args[index + 1];

            var value = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static int OverrideInt(string variable, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value > 0
                ? value
                : fallback;
        }
    }
}
