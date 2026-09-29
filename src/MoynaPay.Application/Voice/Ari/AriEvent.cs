using System.Text.Json;

namespace MoynaPay.Application.Voice.Ari;

/// <summary>
/// One event from the ARI WebSocket, parsed only as far as routing needs.
///
/// The raw JSON is kept because Asterisk sends far more than this models, and the next phase
/// always wants a field this one did not think of. Parsing everything up front would mean
/// touching this type every time a handler needs one more value.
/// </summary>
public sealed record AriEvent(
    string Type,
    string? ChannelId,
    string? PlaybackId,
    string? RecordingName,
    string Json)
{
    public const string StasisStart = "StasisStart";
    public const string StasisEnd = "StasisEnd";
    public const string ChannelDestroyed = "ChannelDestroyed";
    public const string ChannelHangupRequest = "ChannelHangupRequest";
    public const string ChannelStateChange = "ChannelStateChange";
    public const string ChannelDtmfReceived = "ChannelDtmfReceived";
    public const string PlaybackFinished = "PlaybackFinished";
    public const string RecordingFinished = "RecordingFinished";

    /// <summary>
    /// Returns null for malformed JSON or an event with no type, and never throws.
    ///
    /// A single bad frame must not end the stream. Asterisk is a C program on the other end
    /// of a socket; one truncated message should cost one message, not every call in
    /// progress.
    /// </summary>
    public static AriEvent? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;

            var type = Text(root, "type");
            if (type is null) return null;

            return new AriEvent(
                type,
                Nested(root, "channel", "id"),
                Nested(root, "playback", "id"),
                Nested(root, "recording", "name"),
                json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a field this type does not model, straight from the raw JSON - the digit on a
    /// ChannelDtmfReceived, say, or a channel variable on a StasisStart.
    /// </summary>
    public string? Read(params string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            using var document = JsonDocument.Parse(Json);
            var element = document.RootElement;

            foreach (var segment in path)
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty(segment, out element))
                {
                    return null;
                }
            }

            return element.ValueKind == JsonValueKind.Null ? null : element.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static string? Nested(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object
            ? Text(child, property)
            : null;
}
