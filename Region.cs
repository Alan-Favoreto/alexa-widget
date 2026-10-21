using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlexaWidget;

/// <summary>Amazon marketplace the account belongs to. Editable in %LOCALAPPDATA%\AlexaWidget\settings.json.</summary>
sealed record Region(string AmazonDomain, string Locale)
{
    [JsonIgnore] public string AlexaHost => "alexa." + AmazonDomain;
    [JsonIgnore] public string WwwHost => "www." + AmazonDomain;
    [JsonIgnore] public string ApiHost => "api." + AmazonDomain;
    [JsonIgnore] public string CookieDomain => "." + AmazonDomain;

    // The sign-in page handle carries the marketplace suffix (amazon.com.br -> _br); amazon.com has none.
    [JsonIgnore] public string PageHandle => AmazonDomain == "amazon.com" ? "" : "_" + AmazonDomain.Split('.')[^1];

    // Marketplace IDs as embedded in the app (needed by routine triggers).
    [JsonIgnore]
    public string MarketplaceId => AmazonDomain switch
    {
        "amazon.com.br" => "A2Q3Y263D00KWC",
        "amazon.com" => "ATVPDKIKX0DER",
        "amazon.com.mx" => "A1AM78C64UM0Y8",
        "amazon.ca" => "A2EUQ1WTGCTBG2",
        "amazon.co.uk" => "A1F83G8C2ARO7P",
        "amazon.de" => "A1PA6795UKMFR9",
        "amazon.fr" => "A13V1IB3VIYZZH",
        "amazon.es" => "A1RKKUPIHCS9HS",
        "amazon.it" => "APJ6JRA9NG5V4",
        _ => "",
    };

    public static Region Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "settings.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Region>(File.ReadAllText(path)) is { AmazonDomain.Length: > 0 } saved)
                return saved;
        }
        catch (JsonException) { }

        var fallback = new Region("amazon.com.br", "pt-BR");
        File.WriteAllText(path, JsonSerializer.Serialize(fallback, new JsonSerializerOptions { WriteIndented = true }));
        return fallback;
    }
}
