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
    /// Players the matchmaker seated here but who are still connecting count as present, and open backfills are
    /// closed before the deployment stops so the matchmaker never sends a player to a server that is going away.
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
        private int m_LastPendingCount;
        private bool m_AnyPlayerJoined;
        private UnityWebRequest m_SelfStopRequest;
        private string m_StopReason;
        private float m_StopRequestedAt;

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
            if (m_StopReason != null)
            {
                // Wait for EdgegapBackfillSystem to close its backfills, then stop.
                if (EdgegapBackfillState.IsClosed || now - m_StopRequestedAt > 10f)
                    SelfStop();
                return;
            }

            var playerCount = m_ConnectedPlayers.CalculateEntityCount();
            var pendingCount = EdgegapBackfillState.PendingPlayers;
            if (playerCount != m_LastPlayerCount || pendingCount != m_LastPendingCount)
            {
                Debug.Log($"[Edgegap] Connected players: {playerCount}" +
                          (pendingCount > 0 ? $" (+{pendingCount} matched, connecting)" : ""));
                m_LastPlayerCount = playerCount;
                m_LastPendingCount = pendingCount;
            }

            if (playerCount > 0)
            {
                m_AnyPlayerJoined = true;
                m_EmptySince = -1f;
                return;
            }

            // A player the matchmaker just seated here is on the way: keep the server up for them.
            if (pendingCount > 0)
            {
                m_EmptySince = -1f;
                return;
            }

            if (!m_AnyPlayerJoined)
            {
                if (now - m_StartedAt > m_FirstPlayerTimeout)
                    RequestStop("no player connected in time", now);
                return;
            }

            if (m_EmptySince < 0f)
                m_EmptySince = now;
            else if (now - m_EmptySince > m_EmptyTimeout)
                RequestStop("all players left", now);
        }

        private void RequestStop(string reason, float now)
        {
            Debug.Log($"[Edgegap] Stopping deployment: {reason}.");
            m_StopReason = reason;
            m_StopRequestedAt = now;
            EdgegapBackfillState.StopRequested = true;
        }

        private void SelfStop()
        {
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
