using Windows.Devices.Enumeration;

namespace AlexaWidget;

sealed record BluetoothEcho(string Id, string Name, bool Paired);

/// <summary>Pairs this PC (as audio source) with an Echo in Bluetooth pairing mode, using Windows' own pairing.</summary>
static class BluetoothSpeaker
{
    // Classic Bluetooth association endpoints (paired state is added per query).
    const string ClassicProtocol = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
    static readonly string[] Properties = ["System.Devices.Aep.IsPaired", "System.Devices.Aep.IsPresent"];

    /// <summary>Echos Windows already has a pairing for (possibly a stale key from before a reset).</summary>
    public static async Task<List<BluetoothEcho>> FindPairedAsync()
    {
        var selector = $"{ClassicProtocol} AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
        var found = await DeviceInformation.FindAllAsync(selector, Properties, DeviceInformationKind.AssociationEndpoint);
        return found.Where(d => IsEcho(d.Name)).Select(d => new BluetoothEcho(d.Id, d.Name, true)).ToList();
    }

    /// <summary>Runs a Bluetooth inquiry and returns the unpaired Echos in range.</summary>
    public static async Task<List<BluetoothEcho>> DiscoverAsync(TimeSpan timeout)
    {
        var selector = $"{ClassicProtocol} AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#False";
        var watcher = DeviceInformation.CreateWatcher(selector, Properties, DeviceInformationKind.AssociationEndpoint);
        var found = new Dictionary<string, BluetoothEcho>();
        var firstHit = new TaskCompletionSource();
        watcher.Added += (_, d) =>
        {
            if (!IsEcho(d.Name)) return;
            lock (found) found[d.Id] = new BluetoothEcho(d.Id, d.Name, false);
            firstHit.TrySetResult();
        };
        // A watcher only raises Added reliably when Updated and Removed have handlers too.
        watcher.Updated += (_, _) => { };
        watcher.Removed += (_, _) => { };
        watcher.Start();
        try
        {
            // Stop a few seconds after the first Echo shows up, so a second one in range can still appear.
            if (await Task.WhenAny(firstHit.Task, Task.Delay(timeout)) == firstHit.Task)
                await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            watcher.Stop();
        }
        lock (found) return [.. found.Values];
    }

    public static async Task UnpairAsync(string id)
    {
        var device = await DeviceInformation.CreateFromIdAsync(id);
        var result = await device.Pairing.UnpairAsync();
        if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
            throw new AlexaException($"O Windows não conseguiu remover \"{device.Name}\" ({result.Status}).");
    }

    public static async Task PairAsync(string id)
    {
        var device = await DeviceInformation.CreateFromIdAsync(id);
        var custom = device.Pairing.Custom;
        // Echos use Secure Simple Pairing ("just works"); accept whatever confirmation Windows asks for.
        custom.PairingRequested += (_, args) =>
        {
            if (args.PairingKind == DevicePairingKinds.ProvidePin) args.Accept("0000");
            else args.Accept();
        };
        var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch
                    | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin;
        var result = await custom.PairAsync(kinds);
        if (result.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
            throw new AlexaException($"O pareamento com \"{device.Name}\" falhou ({result.Status}). Diga \"Alexa, parear\" e tente de novo.");
    }

    static bool IsEcho(string? name) => name is not null && name.StartsWith("Echo", StringComparison.OrdinalIgnoreCase);
}
