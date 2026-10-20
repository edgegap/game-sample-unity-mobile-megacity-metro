using System;
using System.Collections.Generic;
using System.Linq;
using Edgegap;
using Edgegap.Matchmaking;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Shared between <see cref="EdgegapBackfillSystem"/> and <see cref="EdgegapServerLifecycleSystem"/>.
    /// </summary>
    public static class EdgegapBackfillState
    {
        /// <summary>Players the matchmaker seated on this server who have not connected yet.</summary>
        public static int PendingPlayers;

        /// <summary>Set by the lifecycle before it stops the deployment: close all backfills first.</summary>
        public static bool StopRequested;

        /// <summary>True when no backfill is open (or backfill never ran), so stopping cannot strand a player.</summary>
        public static bool IsClosed = true;
    }

    /// <summary>
    /// Join-in-progress for matchmaker deployments: reads the match the Edgegap matchmaker injected (MM_* variables),
    /// then keeps a few backfills open while the server has free seats. A backfill is a server-owned ticket carrying
    /// this deployment's address; the matchmaker pairs it with one newly queued player and hands that player this
    /// server, so 150-player sessions keep filling after the first match. Seats are tracked per matchmaker ticket
    /// (clients send theirs in <see cref="EdgegapTicketRpc"/>): they free up when a player leaves, or when a matched
    /// player does not connect within the grace period.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class EdgegapBackfillSystem : SystemBase
    {
        private const float k_PollSeconds = 2f;
        private const float k_CreateIntervalSeconds = 0.5f;
        private const float k_ErrorBackoffSeconds = 5f;
        private const float k_StopTimeoutSeconds = 5f;

        private class Seat
        {
            public BackfillAssignedTicket<JObject> Ticket;
            public int NetworkId = -1;
            public float AssignedAt;
        }

        private class OpenBackfill
        {
            public float NextPollAt;
            public bool RequestInFlight;
        }

        private EdgegapMatchmakerSettings m_Settings;
        private Api m_Api;
        private string m_Profile;
        private BackfillAttributes m_Assignment;

        private readonly Dictionary<string, Seat> m_Seats = new();
        private readonly Dictionary<int, string> m_TicketByConnection = new();
        private readonly HashSet<int> m_Connections = new();
        private readonly Dictionary<string, OpenBackfill> m_Backfills = new();
        private int m_CreatesInFlight;
        private int m_DeletesInFlight;
        private float m_NextCreateAt;
        private float m_StopRequestedAt = -1f;
        private string m_LastSummary;

        private EntityQuery m_ConnectionQuery;

        protected override void OnCreate()
        {
            m_ConnectionQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkId>());

            var match = EdgegapEnvironment.IsOnEdgegap
                ? new MatchEnvironmentDTO<JObject>(Environment.GetEnvironmentVariables())
                : null;
            if (string.IsNullOrEmpty(match?.MatchProfile))
            {
                // Not started by the matchmaker (direct deploy, local or Docker run): nothing to backfill.
                Enabled = false;
                return;
            }

            m_Settings = EdgegapMatchmakerSettings.Instance;
            if (m_Settings == null || !m_Settings.IsConfigured)
            {
                Debug.LogError("[Edgegap] Matchmaker deployment but no matchmaker URL/token in " +
                               "Resources/EdgegapMatchmakerSettings: backfill is off, only the matched players can join.");
                Enabled = false;
                return;
            }

            var now = UnityEngine.Time.realtimeSinceStartup;
            m_Profile = match.MatchProfile;
            foreach (var ticket in match.Tickets.Values)
                m_Seats[ticket.ID] = new Seat { Ticket = ToBackfillTicket(ticket), AssignedAt = now };

            var deployment = new DeploymentEnvironmentDTO(Environment.GetEnvironmentVariables());
            m_Assignment = new BackfillAttributes(new BackfillAssignment
            {
                RequestID = deployment.RequestID,
                Fqdn = deployment.Fqdn,
                PublicIP = deployment.PublicIP,
                Ports = deployment.PortMapping,
                Location = deployment.Location,
            });

            m_Api = new Api(EdgegapRequestRunner.Create("Edgegap Backfill"), m_Settings.AuthToken, m_Settings.BaseUrl);
            EdgegapBackfillState.IsClosed = false;

            Debug.Log($"[Edgegap] Match '{match.MatchId}' (profile '{m_Profile}') with {m_Seats.Count} matched " +
                      $"player(s): {string.Join(", ", m_Seats.Keys)}. Backfill on, up to {m_Settings.MaxPlayers} players.");
        }

        protected override void OnUpdate()
        {
            var now = UnityEngine.Time.realtimeSinceStartup;

            ReadTicketMessages();
            TrackConnections();
            ExpireNoShows(now);
            EdgegapBackfillState.PendingPlayers = m_Seats.Values.Count(s => s.NetworkId < 0);

            if (EdgegapBackfillState.StopRequested)
            {
                CloseAllBackfills(now);
                return;
            }

            var freeSeats = m_Settings.MaxPlayers - m_Connections.Count - EdgegapBackfillState.PendingPlayers;
            // A backfill must carry at least one ticket of a player on this server.
            var target = m_Seats.Count == 0 ? 0 : Math.Clamp(freeSeats, 0, m_Settings.MaxOpenBackfills);

            if (m_Backfills.Count + m_CreatesInFlight < target && now >= m_NextCreateAt)
            {
                m_NextCreateAt = now + k_CreateIntervalSeconds;
                CreateBackfill();
            }
            else if (m_Backfills.Count > target && m_DeletesInFlight == 0)
            {
                DeleteBackfill(m_Backfills.Keys.First());
            }

            foreach (var (id, backfill) in m_Backfills.ToList())
            {
                if (!backfill.RequestInFlight && now >= backfill.NextPollAt)
                    PollBackfill(id, backfill);
            }

            LogSummary();
        }

        private void ReadTicketMessages()
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, request, entity) in SystemAPI
                         .Query<RefRO<EdgegapTicketRpc>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                commandBuffer.DestroyEntity(entity);
                if (!SystemAPI.HasComponent<NetworkId>(request.ValueRO.SourceConnection))
                    continue;

                var networkId = SystemAPI.GetComponent<NetworkId>(request.ValueRO.SourceConnection).Value;
                var ticketId = rpc.ValueRO.TicketId.ToString();
                if (m_Seats.TryGetValue(ticketId, out var seat))
                {
                    seat.NetworkId = networkId;
                    m_TicketByConnection[networkId] = ticketId;
                    Debug.Log($"[Edgegap] Matched player connected: ticket {ticketId} (connection {networkId}).");
                }
                else
                {
                    // Possible to reject such players instead; Megacity lets them in (e.g. a seat that expired).
                    Debug.LogWarning($"[Edgegap] Connection {networkId} sent ticket {ticketId}, which this server was not given.");
                }
            }

            commandBuffer.Playback(EntityManager);
        }

        private void TrackConnections()
        {
            using var ids = m_ConnectionQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            var current = new HashSet<int>();
            foreach (var id in ids)
                current.Add(id.Value);

            foreach (var left in m_Connections.Where(id => !current.Contains(id)).ToList())
            {
                if (m_TicketByConnection.Remove(left, out var ticketId) && m_Seats.Remove(ticketId))
                    Debug.Log($"[Edgegap] Player with ticket {ticketId} left, seat reopened.");
            }

            m_Connections.Clear();
            m_Connections.UnionWith(current);
        }

        private void ExpireNoShows(float now)
        {
            foreach (var (ticketId, seat) in m_Seats.ToList())
            {
                if (seat.NetworkId < 0 && now - seat.AssignedAt > m_Settings.ConnectionGraceSeconds)
                {
                    m_Seats.Remove(ticketId);
                    Debug.LogWarning($"[Edgegap] Ticket {ticketId} did not connect within " +
                                     $"{m_Settings.ConnectionGraceSeconds}s, seat reopened.");
                }
            }
        }

        private void CreateBackfill()
        {
            // Every ticket offers exactly one seat ("1"): Megacity players queue solo.
            var tickets = m_Seats.ToDictionary(s => s.Key, s => s.Value.Ticket);
            m_CreatesInFlight++;
            m_Api.CreateBackfill(
                new BackfillRequestDTO<JObject>(m_Profile, m_Assignment, tickets),
                (response, _) =>
                {
                    m_CreatesInFlight--;
                    m_Backfills[response.ID] = new OpenBackfill { NextPollAt = UnityEngine.Time.realtimeSinceStartup + k_PollSeconds };
                    Debug.Log($"[Edgegap] Backfill {response.ID} open.");
                },
                (error, request) =>
                {
                    m_CreatesInFlight--;
                    m_NextCreateAt = UnityEngine.Time.realtimeSinceStartup + k_ErrorBackoffSeconds;
                    Debug.LogError($"[Edgegap] Backfill create failed ({request.responseCode}): {error}");
                });
        }

        private void PollBackfill(string id, OpenBackfill backfill)
        {
            backfill.RequestInFlight = true;
            m_Api.GetBackfill<JObject>(
                id,
                (response, _) =>
                {
                    backfill.RequestInFlight = false;
                    backfill.NextPollAt = UnityEngine.Time.realtimeSinceStartup + k_PollSeconds;

                    if (response.Status == "ASSIGNED" && response.AssignedTicket != null)
                    {
                        m_Backfills.Remove(id);
                        var ticket = response.AssignedTicket;
                        if (EdgegapBackfillState.StopRequested)
                        {
                            Debug.LogWarning($"[Edgegap] Backfill {id} seated ticket {ticket.ID} while stopping.");
                            return;
                        }

                        m_Seats[ticket.ID] = new Seat
                        {
                            Ticket = ToBackfillTicket(ticket),
                            AssignedAt = UnityEngine.Time.realtimeSinceStartup,
                        };
                        Debug.Log($"[Edgegap] Backfill {id} seated ticket {ticket.ID} (player {ticket.PlayerIP}).");
                    }
                    else if (response.Status != null && (response.Status.Contains("CANCEL") || response.Status.Contains("EXPIRE")))
                    {
                        m_Backfills.Remove(id);
                        Debug.Log($"[Edgegap] Backfill {id} {response.Status}, opening a new one.");
                    }
                },
                (error, request) =>
                {
                    backfill.RequestInFlight = false;
                    backfill.NextPollAt = UnityEngine.Time.realtimeSinceStartup + k_ErrorBackoffSeconds;

                    // 404: expired and removed by the matchmaker. Other client errors: drop it and open a fresh one.
                    if (request.responseCode == 404 || (request.responseCode >= 400 && request.responseCode < 500 && request.responseCode != 429))
                    {
                        m_Backfills.Remove(id);
                        Debug.Log($"[Edgegap] Backfill {id} gone ({request.responseCode}), opening a new one.");
                    }
                    else
                    {
                        Debug.LogWarning($"[Edgegap] Backfill {id} poll failed ({request.responseCode}): {error}");
                    }
                });
        }

        private void DeleteBackfill(string id)
        {
            m_Backfills.Remove(id);
            m_DeletesInFlight++;
            m_Api.DeleteBackfill(
                id,
                _ =>
                {
                    m_DeletesInFlight--;
                    Debug.Log($"[Edgegap] Backfill {id} closed.");
                },
                (error, request) =>
                {
                    m_DeletesInFlight--;
                    if (request.responseCode != 404)
                        Debug.LogWarning($"[Edgegap] Backfill {id} delete failed ({request.responseCode}): {error}");
                });
        }

        private void CloseAllBackfills(float now)
        {
            if (m_StopRequestedAt < 0f)
            {
                m_StopRequestedAt = now;
                Debug.Log($"[Edgegap] Server stopping, closing {m_Backfills.Count} backfill(s).");
            }

            foreach (var id in m_Backfills.Keys.ToList())
                DeleteBackfill(id);

            var done = m_DeletesInFlight == 0 && m_CreatesInFlight == 0;
            if (done || now - m_StopRequestedAt > k_StopTimeoutSeconds)
            {
                EdgegapBackfillState.IsClosed = true;
                Enabled = false;
            }
        }

        protected override void OnDestroy()
        {
            // The process is quitting without the lifecycle's stop (Application.Quit fallback): a backfill left open
            // would send players to this dead server until it expires, so delete them now. Coroutines no longer
            // run here, so send plain requests and wait briefly for them. Not reached when the container is stopped
            // from outside (SIGTERM on Edgegap stop or max duration): the Unity player exits without running
            // OnDestroy, and those backfills expire after the profile's ticket_expiration_period.
            if (m_Api == null || m_Backfills.Count == 0)
                return;

            var requests = m_Backfills.Keys.Select(id =>
            {
                var request = UnityWebRequest.Delete($"{m_Settings.BaseUrl}/backfills/{id}");
                request.SetRequestHeader("Authorization", m_Settings.AuthToken);
                request.SendWebRequest();
                return request;
            }).ToList();

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (requests.Any(r => !r.isDone) && DateTime.UtcNow < deadline)
                System.Threading.Thread.Sleep(50);

            Debug.Log($"[Edgegap] Quitting: closed {requests.Count(r => r.isDone && r.result == UnityWebRequest.Result.Success)}" +
                      $"/{requests.Count} open backfill(s).");
            requests.ForEach(r => r.Dispose());
            m_Backfills.Clear();
        }

        private void LogSummary()
        {
            var summary = $"{m_Connections.Count} connected, {EdgegapBackfillState.PendingPlayers} on the way, " +
                          $"{m_Backfills.Count} backfill(s) open";
            if (summary == m_LastSummary)
                return;
            m_LastSummary = summary;
            Debug.Log($"[Edgegap] Backfill: {summary}.");
        }

        private static BackfillAssignedTicket<JObject> ToBackfillTicket(InjectedTicketDTO<JObject> ticket)
        {
            var attributes = ticket.Attributes != null ? (JObject)ticket.Attributes.DeepClone() : new JObject();
            attributes["backfill_group_size"] = new JArray("1");
            return new BackfillAssignedTicket<JObject>
            {
                ID = ticket.ID,
                CreatedAt = ticket.CreatedAt,
                PlayerIP = ticket.PlayerIP,
                GroupID = ticket.GroupID,
                TeamID = ticket.TeamID,
                Attributes = attributes,
            };
        }
    }
}
