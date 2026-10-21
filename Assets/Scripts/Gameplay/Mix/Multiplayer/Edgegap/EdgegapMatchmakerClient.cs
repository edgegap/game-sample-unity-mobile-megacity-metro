using System;
using System.Threading.Tasks;
using Edgegap;
using Edgegap.Matchmaking;
using UnityEngine;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Finds a match with the Edgegap matchmaker (Group Up flow, solo group) and returns the address of the
    /// dedicated server Edgegap started, or the running server a backfill seated the player on.
    /// Flow: ticket -> SEARCHING -> MATCH_FOUND (server starting) -> HOST_ASSIGNED {public_ip, ports}.
    /// </summary>
    public class EdgegapMatchmakerClient
    {
        public struct Assignment
        {
            public string IP;
            public ushort Port;
        }

        /// <summary>Ticket of the last match found. The client sends it to the server after connecting.</summary>
        public static string CurrentTicketId { get; private set; }

        private readonly EdgegapMatchmakerSettings m_Settings;
        private readonly GroupClient<MetroGroupUpRequest, MetroTicketAttributes> m_Client;
        private TaskCompletionSource<bool> m_Health;
        private TaskCompletionSource<Assignment> m_Match;
        private Action<string> m_OnStatus;

        public EdgegapMatchmakerClient(MonoBehaviour handler, EdgegapMatchmakerSettings settings)
        {
            m_Settings = settings;
            m_Client = new GroupClient<MetroGroupUpRequest, MetroTicketAttributes>(
                handler,
                settings.BaseUrl,
                settings.AuthToken, // sent raw as the Authorization header, no "Bearer"
                requestTimeoutSeconds: 5,
                pollingBackoffSeconds: 1f,
                maxConsecutivePollingErrors: 10,
                removeGroupSeconds: 30f,
                logGroupUpdates: true,
                logPollingUpdates: false);

            m_Health = new TaskCompletionSource<bool>();
            m_Client.Initialize(OnMonitorUpdate, OnGroupUpdate);
        }

        public bool IsSearching => m_Match != null && !m_Match.Task.IsCompleted;

        /// <summary>Searches until a server is assigned. Throws with a player-readable message on failure.</summary>
        public async Task<Assignment> FindMatchAsync(Action<string> onStatus)
        {
            if (IsSearching)
                throw new InvalidOperationException("Already searching for a match.");

            m_OnStatus = onStatus;
            CurrentTicketId = null;

            if (!m_Health.Task.IsCompleted || !m_Health.Task.Result)
            {
                // First call, or the last check failed: ask again (e.g. free-tier matchmaker restarted).
                m_Health = new TaskCompletionSource<bool>();
                m_OnStatus?.Invoke("[Matchmaker] Checking Edgegap matchmaker...");
                m_Client.Status();
            }

            if (!await m_Health.Task)
                throw new Exception("Edgegap matchmaker unavailable.");

            m_Match = new TaskCompletionSource<Assignment>();
            m_OnStatus?.Invoke("[Matchmaker] Searching...");

            // abandon: true is required: the SDK's Observable starts with an empty (non-null) group,
            // so the first CreateGroup without it fails with "conflict, abandon and restart".
            m_Client.CreateGroup(new MetroGroupUpRequest(m_Settings.Profile), abandon: true);
            return await m_Match.Task;
        }

        public void Cancel()
        {
            if (!IsSearching)
                return;

            m_Client.StopMatchmaking(() =>
            {
                var current = m_Client.Group.Current;
                if (current?.GroupID != null)
                {
                    // DELETE was refused (409) because a match was already found: keep polling to get it.
                    m_Client.ResumeMatchmaking(current, abandon: true);
                    return;
                }

                m_Match?.TrySetException(new OperationCanceledException("Matchmaking cancelled."));
            });
        }

        private void OnMonitorUpdate(Observable<MonitorResponseDTO> monitor, ObservableActionType action, string message)
        {
            if (action == ObservableActionType.Update && message == "healthy")
                m_Health.TrySetResult(true);
            else if (action == ObservableActionType.Update || action == ObservableActionType.Error)
                m_Health.TrySetResult(false);
        }

        private void OnGroupUpdate(Observable<GroupUpResponseDTO> group, ObservableActionType action, string message)
        {
            if (m_Match == null || m_Match.Task.IsCompleted)
                return;

            if (action == ObservableActionType.Error)
            {
                m_Match.TrySetException(new Exception($"Matchmaking failed: {group.Error ?? message}"));
                return;
            }

            if (action != ObservableActionType.Update || group.Current?.GroupID == null)
                return;

            var current = group.Current;
            switch (current.Status)
            {
                case "SEARCHING":
                    m_OnStatus?.Invoke("[Matchmaker] Searching for players...");
                    break;
                case "MATCH_FOUND":
                    m_OnStatus?.Invoke("[Matchmaker] Match found, starting server...");
                    break;
                case "HOST_ASSIGNED":
                    // Read the assignment now: the SDK clears the group 30 s later.
                    CompleteWithAssignment(current);
                    break;
                case "CANCELLED":
                    m_Match.TrySetException(new Exception("No match found in time, try again."));
                    break;
            }
        }

        private void CompleteWithAssignment(GroupUpResponseDTO group)
        {
            var assignment = group.Assignment;
            if (assignment?.Ports == null || !assignment.Ports.TryGetValue(m_Settings.PortName, out var port)
                || !ushort.TryParse(port.External, out var externalPort))
            {
                Debug.LogError($"[Edgegap] Assignment has no port named '{m_Settings.PortName}': {assignment}");
                m_Match.TrySetException(new Exception("Server assignment has no game port."));
                return;
            }

            CurrentTicketId = group.TicketID;
            Debug.Log($"[Edgegap] Ticket {group.TicketID} assigned to {assignment.Fqdn} " +
                      $"({assignment.PublicIP}:{externalPort}).");

            // UDP: connect to the IP, no DNS lookup needed.
            m_Match.TrySetResult(new Assignment { IP = assignment.PublicIP, Port = externalPort });
        }
    }
}
