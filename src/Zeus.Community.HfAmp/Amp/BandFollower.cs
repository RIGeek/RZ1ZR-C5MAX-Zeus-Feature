// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;

namespace Zeus.Community.HfAmp.Amp;

/// <summary>Current state of the radio, as far as band following needs it.</summary>
internal readonly record struct RadioState(long VfoHz, bool Transmitting);

/// <summary>Where band following reads the radio's frequency from. Implemented by <see cref="HostBridge"/>.</summary>
internal interface IRadioStateSource
{
    /// <summary>False when this build cannot read the radio at all (the panel then disables the option).</summary>
    bool Available { get; }

    RadioState? Read();
}

internal sealed class BandFollowOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The radio must sit in a band this long before the amp is moved, so tuning across bands does not make the amp chase.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Moves the amplifier to the band the radio is tuned to.
///
/// Edge triggered: it acts when the radio settles in a band it has not already
/// acted on, then leaves the amp alone, so a manual band choice sticks until the
/// radio moves to a different band. It never acts while either side is
/// transmitting and never keys anything.
/// </summary>
internal sealed class BandFollower
{
    private readonly AmpClient _amp;
    private readonly IRadioStateSource _radio;
    private readonly BandFollowOptions _opt;
    private readonly ILogger _log;
    private readonly object _gate = new();

    private bool _enabled;
    private int _lastActed;                 // band last acted on, 0 = none
    private int _pending;                   // band the radio is currently in, 0 = none
    private DateTime _pendingSince;
    private string _state = "off";
    private RadioState? _lastRadio;

    public BandFollower(AmpClient amp, IRadioStateSource radio, ILogger log, BandFollowOptions? options = null)
    {
        _amp = amp;
        _radio = radio;
        _log = log;
        _opt = options ?? new BandFollowOptions();
    }

    public bool Available => _radio.Available;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate)
            {
                // Turning it on syncs the amp to wherever the radio is right now.
                if (value && !_enabled) { _lastActed = 0; _pending = 0; }
                _enabled = value;
                if (!value) _state = "off";
            }
        }
    }

    public (string State, RadioState? Radio) Describe()
    {
        lock (_gate) return (_state, _lastRadio);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_opt.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Tick(DateTime.UtcNow); }
                catch (Exception ex) { _log.LogWarning(ex, "Band follow tick failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Tick(DateTime nowUtc)
    {
        var snapshot = _amp.GetSnapshot();
        lock (_gate)
        {
            if (!_enabled) { _state = "off"; return; }
            if (!_radio.Available) { _state = "radio state not available"; return; }

            if (snapshot.Link != LinkState.Connected || snapshot.Status is not { } amp)
            {
                // After a reconnect, sync again.
                _lastActed = 0;
                _pending = 0;
                _state = "waiting for the amplifier";
                return;
            }

            var radio = _radio.Read();
            _lastRadio = radio;
            if (radio is not { } r) { _state = "no radio frequency"; return; }

            if (BandPlan.FromHz(r.VfoHz) is not { } target)
            {
                _pending = 0;
                _state = $"radio at {r.VfoHz / 1e6:0.000} MHz is outside the amp's bands";
                return;
            }

            var name = AmpProtocol.BandName(target);
            if (target != _pending) { _pending = target; _pendingSince = nowUtc; }

            if (target == _lastActed) { _state = $"following ({name})"; return; }
            if (nowUtc - _pendingSince < _opt.Settle) { _state = $"radio moved to {name}, settling"; return; }
            if (r.Transmitting || amp.Tx) { _state = $"waiting to switch to {name}: transmitting"; return; }
            if (amp.Band == target) { _lastActed = target; _state = $"following ({name})"; return; }

            var result = _amp.StartBandSelect(target);
            if (result.Ok)
            {
                _lastActed = target;
                _state = $"switching amp to {name}";
                _log.LogInformation("Band follow: radio at {Mhz:0.000} MHz, selecting {Band}", r.VfoHz / 1e6, name);
            }
            else
            {
                _state = result.Message;   // try again on the next tick
            }
        }
    }
}
