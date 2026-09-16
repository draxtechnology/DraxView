using System.Text.Json;

namespace DraxView.Services;

// One normalised event as the service publishes it on drax/<panel>/event
// (MqttTransfer.PublishEvent). The decoded block is the AMX reference:
// node / loop / input is what a floor plan would key on.
public sealed record DraxEvent(
    long Seq,
    DateTime ReceivedLocal,
    string Panel,
    string Ext,
    string Type,
    int TypeId,
    bool On,
    int Value,
    int Node,
    int Loop,
    int Input,
    int InputType,
    string Text,
    string Text2,
    string Text3,
    string Raw)
{
    public string AmxRef => $"N{Node} L{Loop} D{Input}";

    // One condition on one point: an ON event raises it, the matching OFF clears it.
    // Same identity AMX keys on - the event number less its on/off bit - so a fire
    // and a fault on the same device are two conditions, and a panel reset that
    // sends the OFFs takes them off the active list one by one.
    public string ConditionKey => $"{Panel}|{InputType}|{Node}|{Loop}|{Input}";

    // Same families MqttMonitor colours; the CSS class carries it to the row.
    public string Family
    {
        get
        {
            var t = Type.ToLowerInvariant();
            if (t.Contains("alarm")) return On ? "alarm" : "cleared";
            if (t.Contains("fault") || t.Contains("error")) return On ? "fault" : "cleared";
            if (t.Contains("isolation") || t.Contains("disable") || t.Contains("isolate")) return "isolation";
            if (t.Contains("reset") || t.Contains("silence")) return "control";
            return "info";
        }
    }

    public static DraxEvent? Parse(long seq, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int node = 0, loop = 0, input = 0, inputType = 0;
            if (r.TryGetProperty("decoded", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                node = Int(d, "node");
                loop = Int(d, "loop");
                input = Int(d, "input");
                inputType = Int(d, "inputType");
            }
            return new DraxEvent(
                seq,
                DateTime.Now,
                Str(r, "panel"),
                Str(r, "ext"),
                Str(r, "type"),
                Int(r, "typeId"),
                r.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
                Int(r, "value"),
                node, loop, input, inputType,
                Str(r, "text"), Str(r, "text2"), Str(r, "text3"),
                json);
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
