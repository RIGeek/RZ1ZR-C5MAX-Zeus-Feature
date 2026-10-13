// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;
using Zeus.Community.HfAmp.Amp;

namespace Zeus.Community.HfAmp;

// ---- JSON shapes returned to the panel (serialized camelCase) ----------------

internal sealed record ApiStatus(
    ApiConnection Connection, ApiAmp? Amp, ApiMeters? Meters,
    ApiControls Controls, ApiFollow Follow, ApiLimits Limits, string? SampledUtc);

internal sealed record ApiConnection(string State, string? Host, string? Error);

internal sealed record ApiAmp(
    int TempC, double Volts, int Band, string BandName, int Antenna, int Flag4,
    bool Fault, string? FaultText, bool Tx, bool FanHigh, bool Bypass, bool HighVoltage);

internal sealed record ApiMeters(
    bool Fresh, double ForwardW, double ReflectedW, double InputW, double CurrentA,
    double? Swr, double? EfficiencyPercent, int[] Raw);

internal sealed record ApiControls(
    bool FansForced, string? BandSelect, string? Message, string[] Bands, int Antennas,
    int PowerClass, int[] PowerClasses, double HighSupplyVolts, double[] HighSupplyChoices);

internal sealed record ApiFollow(bool Available, bool Enabled, long? RadioHz, int? RadioBand, string State);

/// <summary>Full-scale values of the vendor application's gauges, for drawing bars.</summary>
internal sealed record ApiLimits(double ForwardW, double ReflectedW, double InputW, double CurrentA);

/// <summary>
/// Everything the feature does, with no dependency on the Zeus SDK so that it
/// can be exercised directly by tests: the amp connection, band following, and settings.
/// </summary>
internal sealed class HfAmpService : IAsyncDisposable
{
    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private AmpSettings _settings = new(null, false);
    private Task? _followTask;

    public AmpClient Amp { get; }
    public BandFollower Follower { get; }

    public HfAmpService(
        ILogger log, ISettingsStore store, IRadioStateSource radio,
        AmpClientOptions? ampOptions = null, BandFollowOptions? followOptions = null)
    {
        _store = store;
        Amp = new AmpClient(log, ampOptions);
        Follower = new BandFollower(Amp, radio, log, followOptions);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _settings = await _store.LoadAsync(ct).ConfigureAwait(false);
        Follower.Enabled = _settings.FollowBand;
        Amp.PowerClass = _settings.PowerClass;
        await Amp.StartAsync(_settings.Host).ConfigureAwait(false);
        _followTask = Task.Run(() => Follower.RunAsync(_cts.Token));
    }

    public AmpSettings GetSettings() => _settings;

    /// <summary>Accepts an IPv4 address or a DNS name. Nothing else is ever dialled.</summary>
    internal static bool IsValidHost(string host) =>
        host.Length is > 0 and <= 253
        && Uri.CheckHostName(host) is UriHostNameType.IPv4 or UriHostNameType.Dns;

    public async Task<CommandResult> ApplySettingsAsync(AmpSettings next, CancellationToken ct)
    {
        var host = string.IsNullOrWhiteSpace(next.Host) ? null : next.Host.Trim();
        if (host is not null && !IsValidHost(host))
            return CommandResult.Invalid("Enter an IPv4 address or host name");
        if (!Calibration.IsPowerClass(next.PowerClass))
            return CommandResult.Invalid("Power class must be 600, 1200, 2400 or 4800 W");
        if (!Calibration.IsHighSupplyChoice(next.HighSupplyVolts))
            return CommandResult.Invalid("High supply must be 48.0, 50.0, 53.5 or 58.3 V");

        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var normalized = new AmpSettings(host, next.FollowBand, _settings.UiPrefs,
                next.PowerClass, Calibration.HighSupplyChoices.First(v => Math.Abs(v - next.HighSupplyVolts) < 0.05));
            var hostChanged = !string.Equals(_settings.Host, host, StringComparison.OrdinalIgnoreCase);

            // Changing the address hands the amp back to its front panel, so not in the middle of a transmission.
            if (hostChanged && Amp.GetSnapshot().Status is { Tx: true })
                return CommandResult.Blocked("The amplifier address cannot change while transmitting");

            await _store.SaveAsync(normalized, ct).ConfigureAwait(false);
            _settings = normalized;
            Follower.Enabled = normalized.FollowBand;
            Amp.PowerClass = normalized.PowerClass;
            if (hostChanged) await Amp.StartAsync(host).ConfigureAwait(false);
            return CommandResult.Success("Settings saved");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>Panel appearance (meter ranges, colour zones, chime). Opaque JSON object owned by the panel.</summary>
    public string? GetPrefs() => _settings.UiPrefs;

    public async Task<CommandResult> SetPrefsAsync(string? prefsJson, CancellationToken ct)
    {
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = _settings with { UiPrefs = prefsJson };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _settings = next;
            return CommandResult.Success("Display settings saved");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public ApiStatus BuildStatus()
    {
        var s = Amp.GetSnapshot();

        ApiAmp? amp = s.Status is { } st
            ? new ApiAmp(st.TempC, st.Volts, st.Band, AmpProtocol.BandName(st.Band), st.Antenna, st.Flag4,
                st.Flag4 != 0, AmpProtocol.FaultText(st.Flag4), st.Tx, st.FanHigh, st.Bypass,
                st.IsHighSupply(_settings.HighSupplyVolts))
            : null;

        ApiMeters? meters = s.Meter is { } m
            ? new ApiMeters(m.Fresh, m.Readings.ForwardW, m.Readings.ReflectedW, m.Readings.InputW,
                m.Readings.CurrentA, m.Readings.Swr, m.Readings.EfficiencyPercent, m.Raw)
            : null;

        var (followState, radio) = Follower.Describe();
        var follow = new ApiFollow(
            Follower.Available, Follower.Enabled,
            radio?.VfoHz, radio is { } r ? BandPlan.FromHz(r.VfoHz) : null,
            followState);

        return new ApiStatus(
            new ApiConnection(s.Link.ToString().ToLowerInvariant(), s.Host, s.Error),
            amp, meters,
            new ApiControls(s.FansForced, s.BandSelect, s.Message, AmpProtocol.BandNames, AmpProtocol.AntennaCount,
                _settings.PowerClass, Calibration.PowerClasses, _settings.HighSupplyVolts, Calibration.HighSupplyChoices),
            follow,
            Limits(_settings.PowerClass),
            s.SampledUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Helios DX gauge full scales for the 1200 W version, scaled by version (input is not scaled).</summary>
    private static ApiLimits Limits(int powerClass)
    {
        var k = powerClass / (double)Calibration.DefaultPowerClass;
        return new ApiLimits(1300 * k, 100 * k, 15, 40 * k);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_followTask is not null)
        {
            try { await _followTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        await Amp.DisposeAsync().ConfigureAwait(false);   // sends 99 so the front panel regains control
        _cts.Dispose();
    }
}
