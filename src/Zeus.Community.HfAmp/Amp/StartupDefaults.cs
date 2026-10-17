// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;

namespace Zeus.Community.HfAmp.Amp;

internal sealed class StartupDefaultsOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Minimum time between two sends of the same command while waiting for the amp to confirm.</summary>
    public TimeSpan Resend { get; init; } = TimeSpan.FromSeconds(1.5);

    public int MaxAttempts { get; init; } = 5;
}

/// <summary>
/// Puts the amplifier on the operator's chosen antenna and supply voltage once per Zeus session,
/// as soon as the amp is connected, receiving and not faulted. After that it leaves the amp alone,
/// so later manual choices (and reconnects after a network blip) are never overridden. It changes
/// the same two things as the panel's buttons, with the same transmit interlock, and never keys anything.
///
/// The values in force at that moment are used: changing the setting later takes effect at the next
/// start, or when the amplifier address changes.
/// </summary>
internal sealed class StartupDefaults
{
    private readonly AmpClient _amp;
    private readonly ILogger _log;
    private readonly StartupDefaultsOptions _opt;
    private readonly object _gate = new();

    private int _antenna;                    // 0 = leave as is
    private double _volts;                   // 0 = leave as is
    private bool _evaluated;
    private bool _antennaDone, _voltsDone;
    private int _antennaTries, _voltsTries;
    private DateTime _antennaSent, _voltsSent;
    private string _state = "off";

    public StartupDefaults(AmpClient amp, ILogger log, StartupDefaultsOptions? options = null)
    {
        _amp = amp;
        _log = log;
        _opt = options ?? new StartupDefaultsOptions();
    }

    public void Configure(int antenna, double volts)
    {
        lock (_gate)
        {
            _antenna = antenna;
            _volts = volts;
            if (antenna == 0 && volts == 0 && !_evaluated) _state = "off";
        }
    }

    /// <summary>Apply the defaults again (a different amplifier was selected).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _evaluated = false;
            _antennaDone = _voltsDone = false;
            _antennaTries = _voltsTries = 0;
            _state = "waiting for the amplifier";
        }
    }

    public string State { get { lock (_gate) return _state; } }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_opt.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await TickAsync(DateTime.UtcNow).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Startup defaults tick failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task TickAsync(DateTime nowUtc)
    {
        int antenna; double volts;
        lock (_gate)
        {
            if (_evaluated) return;
            antenna = _antenna; volts = _volts;
        }

        var snapshot = _amp.GetSnapshot();
        if (snapshot.Link != LinkState.Connected || snapshot.Status is not { } amp)
        {
            SetState("waiting for the amplifier");
            return;
        }
        if (antenna == 0 && volts == 0) { Finish("off"); return; }
        if (amp.Tx) { SetState("waiting: the amplifier is transmitting"); return; }
        if (amp.Flag4 != 0) { SetState("waiting: the amplifier reports a fault"); return; }

        string? gaveUp = null;
        bool antennaDone, voltsDone;
        lock (_gate) { antennaDone = _antennaDone; voltsDone = _voltsDone; }

        if (antenna != 0 && !antennaDone)
        {
            if (amp.Antenna == antenna) lock (_gate) _antennaDone = true;
            else if (nowUtc - _antennaSent >= _opt.Resend)
            {
                if (_antennaTries >= _opt.MaxAttempts) { lock (_gate) _antennaDone = true; gaveUp = $"antenna {antenna} not accepted"; }
                else
                {
                    _antennaTries++;
                    _antennaSent = nowUtc;
                    var r = await _amp.SetAntennaAsync(antenna).ConfigureAwait(false);
                    if (!r.Ok) _log.LogInformation("Startup antenna {Antenna}: {Message}", antenna, r.Message);
                }
            }
        }

        if (volts != 0 && !voltsDone)
        {
            // The amp does not report its preset; at idle the measured voltage identifies it (see AmpClient).
            if (snapshot.SupplyPreset is { } current && Math.Abs(current - volts) <= 0.1) lock (_gate) _voltsDone = true;
            else if (nowUtc - _voltsSent >= _opt.Resend)
            {
                if (_voltsTries >= _opt.MaxAttempts) { lock (_gate) _voltsDone = true; gaveUp ??= $"{volts:0.##} V not accepted"; }
                else
                {
                    _voltsTries++;
                    _voltsSent = nowUtc;
                    var r = await _amp.SetSupplyAsync(volts).ConfigureAwait(false);
                    if (r.Ok) lock (_gate) _voltsDone = true;   // the supply ramps slowly, so the command being accepted is the confirmation
                    else _log.LogInformation("Startup supply {Volts} V: {Message}", volts, r.Message);
                }
            }
        }

        lock (_gate) { antennaDone = antenna == 0 || _antennaDone; voltsDone = volts == 0 || _voltsDone; }
        if (antennaDone && voltsDone)
        {
            var parts = new List<string>();
            if (antenna != 0) parts.Add($"antenna {antenna}");
            if (volts != 0) parts.Add($"{volts:0.##} V");
            Finish(gaveUp is null ? $"applied {string.Join(", ", parts)}" : $"stopped: {gaveUp}");
            _log.LogInformation("Startup defaults: {State}", State);
        }
        else SetState("applying");
    }

    private void SetState(string s) { lock (_gate) _state = s; }

    private void Finish(string s) { lock (_gate) { _evaluated = true; _state = s; } }
}
