using System.Text.Json.Nodes;

namespace AlexaWidget;

/// <summary>
/// The Alexa cloud endpoints used by the widget. Paths, GraphQL documents and the text-command
/// sequence shape were taken from the Android app's Hermes bundle (assets/index.bundle.hbc).
/// </summary>
sealed class AlexaApi(AlexaSession session)
{
    const string EndpointsQuery = """
        query AlexaWidgetEndpoints {
          listEndpoints(listEndpointsInput: {}) {
            endpoints {
              id
              friendlyNameObject { value { text } }
              displayCategories { primary { value } }
              enablement
              features {
                name
                instance
                operations { name }
                properties {
                  name
                  ... on Power { powerStateValue }
                  ... on Reachability { reachabilityStatusValue }
                  ... on Brightness { brightnessStateValue }
                  ... on ColorTemperature { colorTemperatureInKelvinStateValue }
                  ... on Mode { modeValue { value } }
                  ... on RangeValue { rangeValue { value } }
                }
              }
            }
          }
        }
        """;

    // Same document as the app's togglePowerFeatureForEndpoint mutation.
    const string PowerMutation = """
        mutation togglePowerFeatureForEndpoint($endpointId: String, $featureOperationName: FeatureOperationName!) {
          setEndpointFeatures(
            setEndpointFeaturesInput: {
              featureControlRequests: [
                { endpointId: $endpointId, featureName: power, featureOperationName: $featureOperationName }
              ]
            }
          ) {
            featureControlResponses { endpointId }
            errors { endpointId code }
          }
        }
        """;

    // Generic form of the app's per-feature mutations (e.g. SmartHomeFdalFujiCommonsSetBrightness).
    const string FeatureMutation = """
        mutation setFeature($request: FeatureControlRequest!) {
          setEndpointFeatures(setEndpointFeaturesInput: { featureControlRequests: [$request] }) {
            featureControlResponses { endpointId }
            errors { endpointId code }
          }
        }
        """;

    /// <summary>Runs one feature operation (setBrightness, setMode…) with its JSON payload.</summary>
    public async Task SetFeatureAsync(string endpointId, string feature, string operation, string? instance, JsonNode? payload)
    {
        var request = new JsonObject
        {
            ["endpointId"] = endpointId,
            ["featureName"] = feature,
            ["featureOperationName"] = operation,
        };
        if (instance is not null) request["instance"] = instance;
        if (payload is not null) request["payload"] = payload.DeepClone();
        var result = await GraphqlAsync(FeatureMutation, new JsonObject { ["request"] = request });
        if (result?["setEndpointFeatures"]?["errors"] is JsonArray { Count: > 0 } errors)
            throw new AlexaException("O dispositivo recusou: " + (string?)errors[0]?["code"]);
    }

    /// <summary>Default music/podcast/station providers per content type, as the app's music settings show them.</summary>
    public Task<JsonNode?> GetMusicDefaultsAsync() => session.SendAsync(HttpMethod.Get, "/api/music/defaults");

    public Task<JsonNode?> GetDevicesAsync() => session.SendAsync(HttpMethod.Get, "/api/devices-v2/device?cached=true");

    /// <summary>cached=true can lag behind alarms created by voice; the app asks for fresh data when the user refreshes.</summary>
    public Task<JsonNode?> GetNotificationsAsync(bool fresh) =>
        session.SendAsync(HttpMethod.Get, $"/api/notifications?cached={(fresh ? "false" : "true")}");

    /// <summary>Runs typed text on an Echo as if it had been spoken to it (the app's "Alexa.TextCommand").</summary>
    public Task TextCommandAsync(string serial, string deviceType, string? customerId, string text) =>
        RunOperationAsync("Alexa.TextCommand", "amzn1.ask.1p.tellalexa", serial, deviceType, customerId, "text", text);

    /// <summary>Makes an Echo say the text verbatim (the routine action "Alexa diz → Personalizar").</summary>
    public Task SpeakAsync(string serial, string deviceType, string? customerId, string text) =>
        RunOperationAsync("Alexa.Speak", "amzn1.ask.1p.saysomething", serial, deviceType, customerId, "textToSpeak", text);

    /// <summary>One-step routine run immediately through /api/behaviors/preview, as the app runs routines.</summary>
    async Task RunOperationAsync(string type, string skillId, string serial, string deviceType, string? customerId, string field, string value)
    {
        var sequence = new JsonObject
        {
            ["@type"] = "com.amazon.alexa.behaviors.model.Sequence",
            ["startNode"] = new JsonObject
            {
                ["@type"] = "com.amazon.alexa.behaviors.model.OpaquePayloadOperationNode",
                ["type"] = type,
                ["skillId"] = skillId,
                ["operationPayload"] = new JsonObject
                {
                    ["deviceType"] = deviceType,
                    ["deviceSerialNumber"] = serial,
                    ["locale"] = session.Region.Locale,
                    ["customerId"] = customerId ?? session.CustomerId,
                    [field] = value,
                },
            },
        };
        var body = new JsonObject
        {
            ["behaviorId"] = "PREVIEW",
            ["sequenceJson"] = sequence.ToJsonString(),
            ["status"] = "ENABLED",
        };
        await session.SendAsync(HttpMethod.Post, "/api/behaviors/preview", body);
    }

    /// <summary>Creates an alarm or reminder ("Alarm" | "Reminder") on an Echo at a local date and time.</summary>
    public Task<JsonNode?> CreateNotificationAsync(string type, string serial, string deviceType, DateTime when, string? label, string? recurringPattern)
    {
        var notification = new JsonObject
        {
            ["type"] = type,
            ["status"] = "ON",
            ["alarmTime"] = new DateTimeOffset(when).ToUnixTimeMilliseconds(),
            ["originalDate"] = when.ToString("yyyy-MM-dd"),
            ["originalTime"] = when.ToString("HH:mm:00") + ".000",
            ["createdDate"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["deviceSerialNumber"] = serial,
            ["deviceType"] = deviceType,
            ["reminderLabel"] = type == "Reminder" ? label : null,
            ["recurringPattern"] = recurringPattern,
            ["isRecurring"] = recurringPattern is not null,
            ["id"] = "create" + type,
            ["isSaveInFlight"] = true,
            ["remainingTime"] = 0,
            ["timeZoneId"] = null,
            ["reminderIndex"] = null,
            ["sound"] = null,
        };
        return session.SendAsync(HttpMethod.Put, $"/api/notifications/create{type}", notification);
    }

    /// <summary>Deletes an alarm/reminder/timer; the API wants the notification object itself as the body.</summary>
    public Task DeleteNotificationAsync(JsonObject notification) =>
        session.SendAsync(HttpMethod.Delete, $"/api/notifications/{Uri.EscapeDataString((string?)notification["id"] ?? "")}", notification);

    /// <summary>Puts an Echo in Bluetooth pairing mode; falls back to the spoken command if the endpoint refuses.</summary>
    public async Task EnablePairingModeAsync(string serial, string deviceType, string softwareVersion, string? customerId)
    {
        try
        {
            await session.SendAsync(HttpMethod.Post,
                $"/api/bluetooth/enable-pairing-mode/{deviceType}/{softwareVersion}/{serial}", new JsonObject());
        }
        catch (AlexaException)
        {
            await TextCommandAsync(serial, deviceType, customerId, "parear");
        }
    }

    public Task CancelPairingModeAsync(string serial, string deviceType) =>
        session.SendAsync(HttpMethod.Post, $"/api/bluetooth/cancel-pairing-mode/{deviceType}/{serial}", new JsonObject());

    public async Task<JsonNode?> GetEndpointsAsync()
    {
        var result = await GraphqlAsync(EndpointsQuery, null);
        return result?["listEndpoints"]?["endpoints"];
    }

    public async Task SetPowerAsync(string endpointId, bool on)
    {
        var variables = new JsonObject { ["endpointId"] = endpointId, ["featureOperationName"] = on ? "turnOn" : "turnOff" };
        var result = await GraphqlAsync(PowerMutation, variables);
        if (result?["setEndpointFeatures"]?["errors"] is JsonArray { Count: > 0 } errors)
            throw new AlexaException("O dispositivo não respondeu: " + (string?)errors[0]?["code"]);
    }

    async Task<JsonNode?> GraphqlAsync(string query, JsonObject? variables)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables ?? new JsonObject() };
        var response = await session.SendAsync(HttpMethod.Post, "/nexus/v1/graphql", body);
        if (response?["data"] is null && response?["errors"] is JsonArray { Count: > 0 } errors)
            throw new AlexaException("Casa inteligente: " + (string?)errors[0]?["message"]);
        return response?["data"];
    }
}
