using Edgegap.Matchmaking;
using Newtonsoft.Json;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Ticket attributes for the "metro" profile. Every rule in the profile except player_count needs an
    /// attribute of the same name, and unknown attributes are rejected (HTTP 400).
    /// </summary>
    public class MetroTicketAttributes
    {
        /// <summary>
        /// Intersection rule that pairs players with server backfills: "1" accepts a seat on a running server,
        /// "new" accepts starting a new one with other queued players.
        /// </summary>
        [JsonProperty("backfill_group_size")]
        public string[] BackfillGroupSize;

        [JsonConstructor]
        public MetroTicketAttributes(string[] backfillGroupSize)
        {
            BackfillGroupSize = backfillGroupSize;
        }
    }

    /// <summary>A solo player is a group of one that is ready at once.</summary>
    public class MetroGroupUpRequest : GroupUpRequestDTO<MetroTicketAttributes>
    {
        public static readonly string[] SoloPlayer = { "new", "1" };

        public MetroGroupUpRequest(string profile)
            : base(profile, isReady: true)
        {
            Attributes = new MetroTicketAttributes(SoloPlayer);
        }
    }
}
