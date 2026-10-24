using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlexaWidget;

/// <summary>Display summary of a routine created in the Alexa phone app.</summary>
sealed record AlexaRoutine(string Id, string Name, bool Enabled, string[] Phrases, string? Time, string[] Days, string[] Actions);

/// <summary>
/// Routines made in the Alexa app ("behaviors/automations"): listed, run, enabled/disabled and deleted.
/// Creating them is not supported: /api/behaviors/automations rejects every body shape tried
/// (object → 500, triggerJson/sequenceJson strings → 400 "Input failed to validate").
/// </summary>
sealed class RoutinesApi(AlexaSession session)
{
    const string Serial = "com.amazon.alexa.behaviors.model.SerialNode";

    public async Task<List<AlexaRoutine>> ListAsync() =>
        (await ListRawAsync()).Select(Summarize).ToList();

    public async Task RunAsync(string id)
    {
        var raw = await FindAsync(id);
        var body = new JsonObject
        {
            ["behaviorId"] = id,
            ["sequenceJson"] = raw["sequence"]?.ToJsonString(),
            ["status"] = "ENABLED",
        };
        await session.SendAsync(HttpMethod.Post, "/api/behaviors/preview", body);
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        var raw = await FindAsync(id);
        raw["status"] = enabled ? "ENABLED" : "DISABLED";
        await session.SendAsync(HttpMethod.Put, $"/api/behaviors/automations/{Uri.EscapeDataString(id)}", raw);
    }

    public Task DeleteAsync(string id) =>
        session.SendAsync(HttpMethod.Delete, $"/api/behaviors/automations/{Uri.EscapeDataString(id)}");

    async Task<List<JsonObject>> ListRawAsync() =>
        (await session.SendAsync(HttpMethod.Get, "/api/behaviors/v2/automations?limit=2000") as JsonArray ?? []).OfType<JsonObject>().ToList();

    async Task<JsonObject> FindAsync(string id) =>
        (await ListRawAsync()).FirstOrDefault(r => (string?)r["automationId"] == id)?.DeepClone() as JsonObject
        ?? throw new AlexaException("Essa rotina não existe mais. Atualize a lista.");

    internal static AlexaRoutine Summarize(JsonObject raw)
    {
        var phrases = new List<string>();
        string? time = null;
        string[] days = [];
        foreach (var trigger in raw["triggers"]?.AsArray() ?? [])
        {
            var payload = trigger?["payload"];
            if (payload?["utterances"] is JsonArray all) phrases.AddRange(all.Select(u => (string?)u).OfType<string>());
            else if ((string?)payload?["utterance"] is { } one) phrases.Add(one);
            else if (TryParseSchedule(payload?["schedule"], out var t, out var d)) (time, days) = (t, d);
        }

        var start = raw["sequence"]?["startNode"];
        var nodes = (string?)start?["@type"] == Serial ? start!["nodesToExecute"]?.AsArray().ToList() ?? [] : [start];
        var actions = nodes.Select(node =>
        {
            var payload = node?["operationPayload"];
            return (string?)node?["type"] switch
            {
                "Alexa.Speak" => $"Alexa diz: {(string?)payload?["textToSpeak"]}",
                "Alexa.TextCommand" => $"Comando: {(string?)payload?["text"]}",
                "Alexa.System.Wait" => $"Esperar {(int?)payload?["waitTimeInSeconds"]} s",
                { } type => type.Replace("Alexa.", ""),
                null => "Ação",
            };
        }).ToArray();

        var id = (string?)raw["automationId"] ?? "";
        var name = (string?)raw["name"] ?? (phrases.Count > 0 ? $"\"{phrases[0]}\"" : "Rotina sem nome");
        return new AlexaRoutine(id, name, (string?)raw["status"] != "DISABLED", [.. phrases], time, days, actions);
    }

    static bool TryParseSchedule(JsonNode? schedule, out string? time, out string[] days)
    {
        time = null;
        days = [];
        // The schedule is either an object or a JSON string holding one.
        if (schedule is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try { schedule = JsonNode.Parse(text); }
            catch (JsonException) { return false; }
        }
        if ((string?)schedule?["triggerTime"] is not { Length: >= 4 } t) return false;
        time = $"{t[..2]}:{t[2..4]}";
        var byDay = ((string?)schedule!["recurrence"] ?? "").Split(';').FirstOrDefault(p => p.StartsWith("BYDAY="))?[6..];
        days = byDay?.Split(',') ?? [];
        return true;
    }
}
