using System;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Stops this Edgegap deployment once nobody is playing on it, so an empty server does not keep running
    /// (and billing) until its maximum duration. Only active inside an Edgegap deployment.
    /// Timeouts can be changed per app version with the EDGEGAP_FIRST_PLAYER_TIMEOUT_S and
    /// EDGEGAP_EMPTY_TIMEOUT_S environment variables.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class EdgegapServerLifecycleSystem : SystemBase
    {
        private const float k_DefaultFirstPlayerTimeoutSeconds = 300f;
        private const float k_DefaultEmptyTimeoutSeconds = 60f;

        private EntityQuery m_ConnectedPlayers;
        private float m_FirstPlayerTimeout;
        private float m_EmptyTimeout;
        private float m_StartedAt;
        private float m_EmptySince = -1f;
        private int m_LastPlayerCount = -1;
        private bool m_AnyPlayerJoined;
        private UnityWebRequest m_SelfStopRequest;

        protected override void OnCreate()
        {
            if (!EdgegapEnvironment.IsOnEdgegap)
            {
                Enabled = false;
                return;
            }

            m_ConnectedPlayers = GetEntityQuery(ComponentType.ReadOnly<NetworkId>());
            m_FirstPlayerTimeout = ReadSeconds("EDGEGAP_FIRST_PLAYER_TIMEOUT_S", k_DefaultFirstPlayerTimeoutSeconds);
            m_EmptyTimeout = ReadSeconds("EDGEGAP_EMPTY_TIMEOUT_S", k_DefaultEmptyTimeoutSeconds);
            m_StartedAt = UnityEngine.Time.realtimeSinceStartup;

            Debug.Log($"[Edgegap] Deployment '{EdgegapEnvironment.RequestId}' started. Self-stop after " +
                      $"{m_FirstPlayerTimeout}s without a first player, or {m_EmptyTimeout}s empty.");
        }

        protected override void OnUpdate()
        {
            if (m_SelfStopRequest != null)
            {
                if (m_SelfStopRequest.isDone)
                {
                    if (m_SelfStopRequest.result == UnityWebRequest.Result.Success)
                        Debug.Log("[Edgegap] Self-stop requested.");
                    else
                    {
                        Debug.LogError($"[Edgegap] Self-stop failed ({m_SelfStopRequest.error}), quitting process.");
                        Application.Quit();
                    }

                    m_SelfStopRequest.Dispose();
                    m_SelfStopRequest = null;
                    Enabled = false;
                }

                return;
            }

            var now = UnityEngine.Time.realtimeSinceStartup;
            var playerCount = m_ConnectedPlayers.CalculateEntityCount();
            if (playerCount != m_LastPlayerCount)
            {
                Debug.Log($"[Edgegap] Connected players: {playerCount}");
                m_LastPlayerCount = playerCount;
            }

            if (playerCount > 0)
            {
                m_AnyPlayerJoined = true;
                m_EmptySince = -1f;
                return;
            }

            if (!m_AnyPlayerJoined)
            {
                if (now - m_StartedAt > m_FirstPlayerTimeout)
                    SelfStop("no player connected in time");
                return;
            }

            if (m_EmptySince < 0f)
                m_EmptySince = now;
            else if (now - m_EmptySince > m_EmptyTimeout)
                SelfStop("all players left");
        }

        private void SelfStop(string reason)
        {
            Debug.Log($"[Edgegap] Stopping deployment: {reason}.");

            var url = EdgegapEnvironment.SelfStopUrl;
            var token = EdgegapEnvironment.SelfStopToken;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
            {
                Debug.LogError("[Edgegap] Self-stop URL or token missing, quitting process instead.");
                Application.Quit();
                Enabled = false;
                return;
            }

            // URL and one-time token are injected by Edgegap into this container only.
            m_SelfStopRequest = UnityWebRequest.Delete(url);
            m_SelfStopRequest.SetRequestHeader("authorization", token);
            m_SelfStopRequest.SendWebRequest();
        }

        private static float ReadSeconds(string variable, float fallback)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            return float.TryParse(value, out var seconds) && seconds > 0f ? seconds : fallback;
        }
    }
}
