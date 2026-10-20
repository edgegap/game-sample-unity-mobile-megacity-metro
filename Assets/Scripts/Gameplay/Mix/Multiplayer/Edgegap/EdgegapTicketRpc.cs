using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Sent once by a client that joined through the Edgegap matchmaker, so the server can tie the connection to
    /// the matchmaker ticket: a seat is released when that player leaves, or when they never arrive.
    /// </summary>
    public struct EdgegapTicketRpc : IRpcCommand
    {
        public FixedString64Bytes TicketId;
    }

    /// <summary>Marks a client connection that already sent its <see cref="EdgegapTicketRpc"/>.</summary>
    public struct EdgegapTicketSent : IComponentData
    {
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class EdgegapSendTicketSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
        }

        protected override void OnUpdate()
        {
            var ticketId = EdgegapMatchmakerClient.CurrentTicketId;
            if (string.IsNullOrEmpty(ticketId))
                return;

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkStreamInGame>>()
                         .WithNone<EdgegapTicketSent>().WithEntityAccess())
            {
                var request = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(request, new EdgegapTicketRpc { TicketId = ticketId });
                commandBuffer.AddComponent<SendRpcCommandRequest>(request);
                commandBuffer.AddComponent<EdgegapTicketSent>(connection);
            }

            commandBuffer.Playback(EntityManager);
        }
    }
}
