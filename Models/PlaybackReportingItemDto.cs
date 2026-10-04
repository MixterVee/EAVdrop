using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EAVdrop.Models;

public sealed class PlaybackReportingItemDto
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("time")]
    public string Time { get; set; } = "";

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("item_name")]
    public string ItemName { get; set; } = "";

    [JsonPropertyName("item_id")]
    public JsonElement ItemIdValue { get; set; }

    [JsonPropertyName("item_type")]
    public string ItemType { get; set; } = "";

    [JsonPropertyName("duration")]
    public JsonElement DurationValue { get; set; }

    [JsonPropertyName("remote_address")]
    public string RemoteAddress { get; set; } = "";

    public string ItemId =>
        ItemIdValue.ValueKind switch
        {
            JsonValueKind.String => ItemIdValue.GetString() ?? "",
            JsonValueKind.Number => ItemIdValue.GetRawText(),
            _ => ""
        };

    public int DurationSeconds
    {
        get
        {
            if (DurationValue.ValueKind == JsonValueKind.Number &&
                DurationValue.TryGetInt32(out var number))
                return Math.Max(0, number);

            if (DurationValue.ValueKind == JsonValueKind.String &&
                int.TryParse(
                    DurationValue.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var textNumber))
                return Math.Max(0, textNumber);

            return 0;
        }
    }

    public DateTimeOffset PlayedDate
    {
        get
        {
            var combined = $"{Date} {Time}".Trim();

            if (DateTime.TryParseExact(
                    combined,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var exact))
                return new DateTimeOffset(exact);

            if (DateTime.TryParse(
                    combined,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsed))
                return new DateTimeOffset(parsed);

            return DateTimeOffset.MinValue;
        }
    }
}
