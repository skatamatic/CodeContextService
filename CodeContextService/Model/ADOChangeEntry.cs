using System.Text.Json.Serialization;

namespace CodeContextService.Model
{

    public class AdoChangeList
    {
        [JsonPropertyName("changeEntries")]
        public List<ChangeEntry> ChangeEntries { get; set; } = new();
    }

    public class ChangeEntry
    {
        [JsonPropertyName("changeTrackingId")]
        public int ChangeTrackingId { get; set; }

        [JsonPropertyName("changeId")]
        public int ChangeId { get; set; }

        [JsonPropertyName("item")]
        public ChangedItem Item { get; set; }

        [JsonPropertyName("changeType")]
        public string ChangeType { get; set; }
    }

    public class ChangedItem
    {
        [JsonPropertyName("objectId")]
        public string ObjectId { get; set; }

        [JsonPropertyName("originalObjectId")]
        public string? OriginalObjectId { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; }
    }
}
