// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Community.HfAmp.Amp;
using Zeus.Plugins.Contracts;

namespace Zeus.Community.HfAmp;

/// <summary>Settings that survive a restart of Zeus.</summary>
internal sealed record AmpSettings(
    string? Host, bool FollowBand, string? UiPrefs = null,
    int PowerClass = Calibration.DefaultPowerClass,
    int StartupAntenna = 0, double StartupVoltage = 0);   // 0 = leave the amplifier as it is

/// <summary>Shown on the panel's About page.</summary>
internal sealed record AboutInfo(string Name, string Version, string Author, string License, string? Homepage);

internal interface ISettingsStore
{
    ValueTask<AmpSettings> LoadAsync(CancellationToken ct);
    ValueTask SaveAsync(AmpSettings settings, CancellationToken ct);
}

/// <summary>
/// The ONLY place that touches Zeus-provided services beyond logging.
/// Settings use the plugin-scoped store (IPluginSettings, granted by default);
/// band following reads the VFO and MOX from IRadioStateReader (ReadRadioState).
/// The plugin never calls IRadioController, so it can never retune or key the radio.
/// </summary>
internal static class HostBridge
{
    /// <summary>Name, version, author, licence and homepage come from plugin.json, so the About page can never disagree with the package.</summary>
    public static AboutInfo CreateAbout(IPluginContext context) =>
        new(context.Manifest.Name, context.Manifest.Version, context.Manifest.Author,
            context.Manifest.License, context.Manifest.Homepage);

    public static ISettingsStore CreateSettingsStore(IPluginContext context) =>
        new PluginSettingsStore(context.Settings);

    public static IRadioStateSource CreateRadioStateSource(IPluginContext context) =>
        context.Radio is { } radio ? new ZeusRadioStateSource(radio) : new UnavailableRadioStateSource();
}

internal sealed class PluginSettingsStore(IPluginSettings settings) : ISettingsStore
{
    private const string HostKey = "host";
    private const string FollowKey = "followBand";
    private const string PrefsKey = "uiPrefs";
    private const string PowerClassKey = "powerClass";
    private const string StartupAntennaKey = "startupAntenna";
    private const string StartupVoltageKey = "startupVoltage";

    public async ValueTask<AmpSettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            var host = await settings.GetAsync<string>(HostKey, ct).ConfigureAwait(false);
            var follow = await settings.GetAsync<bool?>(FollowKey, ct).ConfigureAwait(false);
            var prefs = await settings.GetAsync<string>(PrefsKey, ct).ConfigureAwait(false);
            var power = await settings.GetAsync<int?>(PowerClassKey, ct).ConfigureAwait(false);
            var antenna = await settings.GetAsync<int?>(StartupAntennaKey, ct).ConfigureAwait(false);
            var volts = await settings.GetAsync<double?>(StartupVoltageKey, ct).ConfigureAwait(false);
            return new AmpSettings(string.IsNullOrWhiteSpace(host) ? null : host, follow ?? false,
                string.IsNullOrWhiteSpace(prefs) ? null : prefs,
                power is int p && Calibration.IsPowerClass(p) ? p : Calibration.DefaultPowerClass,
                antenna is int a && a is >= 1 and <= AmpProtocol.AntennaCount ? a : 0,
                volts is double v && AmpProtocol.SupplyCommand(v) is not null ? v : 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AmpSettings(null, false);   // unreadable settings must not stop the feature loading
        }
    }

    public async ValueTask SaveAsync(AmpSettings s, CancellationToken ct)
    {
        await settings.SetAsync(HostKey, s.Host ?? string.Empty, ct).ConfigureAwait(false);
        await settings.SetAsync(FollowKey, s.FollowBand, ct).ConfigureAwait(false);
        await settings.SetAsync(PrefsKey, s.UiPrefs ?? string.Empty, ct).ConfigureAwait(false);
        await settings.SetAsync(PowerClassKey, s.PowerClass, ct).ConfigureAwait(false);
        await settings.SetAsync(StartupAntennaKey, s.StartupAntenna, ct).ConfigureAwait(false);
        await settings.SetAsync(StartupVoltageKey, s.StartupVoltage, ct).ConfigureAwait(false);
    }
}

internal sealed class ZeusRadioStateSource(IRadioStateReader reader) : IRadioStateSource
{
    public bool Available => true;

    public RadioState? Read()
    {
        try { return new RadioState(reader.FrequencyHz, reader.Mox); }
        catch (Exception) { return null; }
    }
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    private AmpSettings _current = new(null, false);

    public ValueTask<AmpSettings> LoadAsync(CancellationToken ct) => new(_current);

    public ValueTask SaveAsync(AmpSettings settings, CancellationToken ct)
    {
        _current = settings;
        return ValueTask.CompletedTask;
    }
}

internal sealed class UnavailableRadioStateSource : IRadioStateSource
{
    public bool Available => false;
    public RadioState? Read() => null;
}
