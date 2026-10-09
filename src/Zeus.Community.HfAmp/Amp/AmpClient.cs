// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zeus.Community.HfAmp.Amp;

internal enum LinkState { Unconfigured, Connecting, Connected, Disconnected }

/// <summary>Outcome of a command. Status is the HTTP status the API layer should use.</summary>
internal sealed record CommandResult(bool Ok, string Message, int Status)
{
    public static CommandResult Success(string message) => new(true, message, 200);
    /// <summary>Refused because of the amplifier's state (not connected, transmitting, ...).</summary>
    public static CommandResult Blocked(string message) => new(false, message, 409);
    /// <summary>Refused because the request itself was malformed.</summary>
    public static CommandResult Invalid(string message) => new(false, message, 400);
}

internal sealed record MeterView(bool Fresh, Readings Readings, int[] Raw);

internal sealed record AmpSnapshot(
    LinkState Link, string? Host, string? Error,
    AmpStatus? Status, MeterView? Meter,
    bool FansForced, string? BandSelect, string? Message, DateTime? SampledUtc);

internal sealed class AmpClientOptions
{
    public int TcpPort { get; init; } = AmpProtocol.TcpPort;
    public int UdpPort { get; init; } = AmpProtocol.UdpPort;

    /// <summary>How long to wait for the amp to report a requested band before sending the command again.</summary>
    public TimeSpan BandVerifyDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Attempts before giving up on a band change. 0 retries until accepted or cancelled.</summary>
    public int BandMaxAttempts { get; init; } = 5;

    /// <summary>Permit band/antenna/voltage changes while the amp reports TX. Off by default.</summary>
    public bool AllowChangesDuringTx { get; init; }

    public TimeSpan MeterFreshFor { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMin { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Connection to one amplifier: status push over TCP, meters over UDP,
/// commands with transmit interlocks, and a band-select loop that verifies the
/// amp really changed band. Reconnects by itself and is safe to call from any thread.
/// </summary>
internal sealed class AmpClient : IAsyncDisposable
{
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromSeconds(8);

    private readonly AmpClientOptions _opt;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly MeterSmoother _smoother = new();

    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private NetworkStream? _stream;
    private UdpClient? _udp;

    private LinkState _link = LinkState.Unconfigured;
    private string? _host, _error, _bandSelect, _message;
    private DateTime _messageUtc;
    private AmpStatus? _status;
    private MeterSample? _meter;
    private (double Forward, double Reflected, double Current, double Input) _smoothed;
    private DateTime _meterUtc = DateTime.MinValue;
    private DateTime _helloUtc = DateTime.MinValue;
    private bool _fansForced;

    private CancellationTokenSource? _bandCts;
    private Task? _bandTask;

    public AmpClient(ILogger log, AmpClientOptions? options = null)
    {
        _log = log;
        _opt = options ?? new AmpClientOptions();
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Connect to a host (replacing any current connection, which is released first). Null or empty disconnects.</summary>
    public async Task StartAsync(string? host)
    {
        await StopAsync(release: true).ConfigureAwait(false);

        host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        lock (_gate)
        {
            _host = host;
            _error = _message = _bandSelect = null;
            _status = null;
            _meter = null;
            _fansForced = false;
            _smoother.Reset();
            _link = host is null ? LinkState.Unconfigured : LinkState.Connecting;
        }
        if (host is null) return;

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _supervisor = Task.Run(() => SuperviseAsync(host, cts.Token));
        }
    }

    /// <summary>
    /// Stop talking to the amp. With <paramref name="release"/> the front panel
    /// regains control first (command 99, which the vendor application also
    /// sends when it closes).
    /// </summary>
    public async Task StopAsync(bool release)
    {
        CancellationTokenSource? cts;
        Task? supervisor;
        lock (_gate)
        {
            cts = _cts;
            supervisor = _supervisor;
            _cts = null;
            _supervisor = null;
        }

        await CancelBandSelectAsync().ConfigureAwait(false);

        if (release) await TrySendQuietlyAsync(AmpProtocol.Release).ConfigureAwait(false);

        cts?.Cancel();
        if (supervisor is not null)
        {
            try { await supervisor.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        cts?.Dispose();

        lock (_gate)
        {
            _link = _host is null ? LinkState.Unconfigured : LinkState.Disconnected;
            _status = null;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(release: true));

    // ------------------------------------------------------------------ state

    public AmpSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            MeterView? meter = null;
            if (_meter is { } raw)
            {
                var fresh = DateTime.UtcNow - _meterUtc <= _opt.MeterFreshFor;
                var readings = Calibration.Compute(
                    _smoothed.Forward, _smoothed.Reflected, _smoothed.Current, _smoothed.Input,
                    _status?.Volts ?? 0);
                meter = new MeterView(fresh, readings, [raw.Forward, raw.Reflected, raw.Current, raw.Input]);
            }

            var message = DateTime.UtcNow - _messageUtc <= MessageLifetime ? _message : null;
            // When did the numbers describe the amp? While connected and idle the last status push is still
            // current (the amp only pushes on change), so that is "now". While transmitting the meter stream
            // is the source, so a stalled stream shows up as an old timestamp and Zeus can mark it stale.
            DateTime? sampled = null;
            if (_link == LinkState.Connected && _status is { } st)
                sampled = st.Tx ? (_meterUtc == DateTime.MinValue ? null : _meterUtc) : DateTime.UtcNow;
            return new AmpSnapshot(_link, _host, _error, _status, meter, _fansForced, _bandSelect, message, sampled);
        }
    }

    private void SetMessage(string text)
    {
        lock (_gate) { _message = text; _messageUtc = DateTime.UtcNow; }
    }

    // ------------------------------------------------------------------ connection

    private async Task SuperviseAsync(string host, CancellationToken ct)
    {
        var delay = _opt.ReconnectMin;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                lock (_gate) { _link = LinkState.Connecting; }
                await RunSessionAsync(host, ct).ConfigureAwait(false);
                delay = _opt.ReconnectMin;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Amplifier session ended");
                lock (_gate) { _error = ex.Message; }
            }

            lock (_gate) { _link = LinkState.Disconnected; _status = null; }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _opt.ReconnectMax.TotalMilliseconds));
        }
    }

    private async Task RunSessionAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? throw new InvalidOperationException($"No IPv4 address found for {host}");

        using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await tcp.ConnectAsync(ip, _opt.TcpPort, connectCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Connection to the amplifier timed out");
            }
        }
        ConfigureKeepAlive(tcp.Client);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(ip, _opt.UdpPort);

        var stream = tcp.GetStream();
        lock (_gate)
        {
            _stream = stream;
            _udp = udp;
            _link = LinkState.Connected;
            _error = null;
            _status = null;
        }

        try
        {
            // Same order as the vendor application: UDP hello, then TCP hello.
            await SendUdpHelloAsync(udp).ConfigureAwait(false);
            await SendTcpAsync(AmpProtocol.Hello).ConfigureAwait(false);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var side = Task.WhenAll(ReadUdpAsync(udp, session.Token), WatchdogAsync(udp, session.Token));
            try
            {
                await ReadTcpAsync(stream, udp, session.Token).ConfigureAwait(false);
            }
            finally
            {
                session.Cancel();
                try { await side.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            lock (_gate) { _stream = null; _udp = null; }
        }
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception)
        {
            // Keep-alive tuning is best effort; some platforms reject individual options.
        }
    }

    private async Task ReadTcpAsync(NetworkStream stream, UdpClient udp, CancellationToken ct)
    {
        var parser = new StatusStream();
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("The amplifier closed the connection");

            foreach (var status in parser.Feed(buffer.AsSpan(0, n)))
                OnStatus(status, udp);
        }
    }

    private void OnStatus(AmpStatus status, UdpClient udp)
    {
        bool txStarted;
        lock (_gate)
        {
            txStarted = status.Tx && !(_status?.Tx ?? false);
            _status = status;
            if (txStarted) _smoother.Reset();
        }

        // The meter stream only runs while transmitting; ask for it again each time TX starts.
        if (txStarted) _ = SendUdpHelloAsync(udp);
    }

    private async Task ReadUdpAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (MeterSample.TryParse(result.Buffer, out var sample)) OnMeter(sample);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                // Typically ICMP "port unreachable" while the amp is not streaming.
                _log.LogDebug(ex, "UDP receive error");
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void OnMeter(MeterSample sample)
    {
        lock (_gate)
        {
            _meter = sample;
            _meterUtc = DateTime.UtcNow;
            _smoothed = _smoother.Add(sample);
        }
    }

    /// <summary>If the amp is transmitting but no meters arrive, register for the stream again.</summary>
    private async Task WatchdogAsync(UdpClient udp, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                bool resend;
                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    resend = (_status?.Tx ?? false)
                             && now - _meterUtc > TimeSpan.FromSeconds(2)
                             && now - _helloUtc > TimeSpan.FromSeconds(2);
                }
                if (resend) await SendUdpHelloAsync(udp).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendUdpHelloAsync(UdpClient udp)
    {
        lock (_gate) { _helloUtc = DateTime.UtcNow; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await udp.SendAsync(Encoding.ASCII.GetBytes(AmpProtocol.Hello), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            _log.LogDebug(ex, "UDP hello failed");
        }
    }

    private async Task SendTcpAsync(string command)
    {
        NetworkStream? stream;
        lock (_gate) { stream = _stream; }
        if (stream is null) throw new InvalidOperationException("Not connected to the amplifier");

        var bytes = Encoding.ASCII.GetBytes(command);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try { await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
        _log.LogDebug("Sent command {Command}", command);
    }

    private async Task TrySendQuietlyAsync(string command)
    {
        bool connected;
        lock (_gate) { connected = _stream is not null; }
        if (!connected) return;
        try
        {
            await SendTcpAsync(command).ConfigureAwait(false);
            await Task.Delay(150).ConfigureAwait(false); // let the amp act before the socket closes
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "Could not send {Command}", command);
        }
    }

    // ------------------------------------------------------------------ commands

    private CommandResult? Precheck(bool refuseWhileTx, string what)
    {
        lock (_gate)
        {
            if (_link != LinkState.Connected) return CommandResult.Blocked("Amplifier is not connected");
            if (_status is not { } status) return CommandResult.Blocked("Waiting for the amplifier's status");
            if (refuseWhileTx && status.Tx && !_opt.AllowChangesDuringTx)
                return CommandResult.Blocked($"{what} is locked while transmitting");
        }
        return null;
    }

    private async Task<CommandResult> SendAsync(string command, string okMessage)
    {
        try
        {
            await SendTcpAsync(command).ConfigureAwait(false);
            SetMessage(okMessage);
            return CommandResult.Success(okMessage);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            return CommandResult.Blocked("Command failed: " + ex.Message);
        }
    }

    public async Task<CommandResult> ResetAsync() =>
        Precheck(false, "Reset") ?? await SendAsync(AmpProtocol.Reset, "Reset sent").ConfigureAwait(false);

    public async Task<CommandResult> SetAntennaAsync(int antenna)
    {
        if (antenna < 1 || antenna > AmpProtocol.AntennaCount)
            return CommandResult.Invalid($"Antenna must be 1 to {AmpProtocol.AntennaCount}");
        return Precheck(true, "Antenna change")
               ?? await SendAsync(AmpProtocol.Antenna(antenna), $"Antenna {antenna}").ConfigureAwait(false);
    }

    public async Task<CommandResult> SetVoltageAsync(bool high) =>
        Precheck(true, "Voltage change")
        ?? await SendAsync(high ? AmpProtocol.VoltageHigh : AmpProtocol.VoltageLow,
            high ? "Supply set to high" : "Supply set to low").ConfigureAwait(false);

    public async Task<CommandResult> SetFansAsync(bool forced)
    {
        var blocked = Precheck(false, "Cooling");
        if (blocked is not null) return blocked;

        var result = await SendAsync(forced ? AmpProtocol.FansForced : AmpProtocol.FansAuto,
            forced ? "Fans forced to 100%" : "Fans set to auto").ConfigureAwait(false);
        // The amp raises fan speed by itself when hot, so its status flag cannot tell us
        // whether 100 % is forced. Remember what we asked for.
        if (result.Ok) lock (_gate) { _fansForced = forced; }
        return result;
    }

    /// <summary>Cutting the LDMOS supply is always allowed; restoring it under RF drive is not.</summary>
    public async Task<CommandResult> SetBypassAsync(bool on) =>
        Precheck(!on, "Restoring the LDMOS supply")
        ?? await SendAsync(on ? AmpProtocol.BypassOn : AmpProtocol.BypassOff,
            on ? "Bypass on: LDMOS supply cut" : "Bypass off: LDMOS supply restoring").ConfigureAwait(false);

    // ------------------------------------------------------------------ band select

    /// <summary>
    /// Ask for a band and keep asking until the amp reports it, because the
    /// firmware does not always act on the first command. Returns immediately;
    /// progress is in <see cref="AmpSnapshot.BandSelect"/>. A newer request replaces an older one.
    /// </summary>
    public CommandResult StartBandSelect(int band)
    {
        if (band < 1 || band > AmpProtocol.BandCount)
            return CommandResult.Invalid($"Band must be 1 to {AmpProtocol.BandCount}");
        var blocked = Precheck(true, "Band change");
        if (blocked is not null) return blocked;

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _bandCts;
            _bandCts = cts;
            _bandSelect = $"selecting {AmpProtocol.BandName(band)}";
            _bandTask = Task.Run(() => BandLoopAsync(band, cts.Token));
        }
        previous?.Cancel();
        return CommandResult.Success($"Selecting {AmpProtocol.BandName(band)}");
    }

    public async Task CancelBandSelectAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _bandCts;
            task = _bandTask;
            _bandCts = null;
            _bandTask = null;
        }
        cts?.Cancel();
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
    }

    private void SetBandSelect(string text, CancellationToken ct)
    {
        // A superseded loop must not overwrite the newer request's progress text.
        if (ct.IsCancellationRequested) return;
        lock (_gate) { _bandSelect = text; }
    }

    private async Task BandLoopAsync(int band, CancellationToken ct)
    {
        var name = AmpProtocol.BandName(band);
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                LinkState link;
                AmpStatus? status;
                lock (_gate) { link = _link; status = _status; }

                if (link != LinkState.Connected) { SetBandSelect($"{name}: stopped, amplifier disconnected", ct); return; }
                if (status is { Band: var current } && current == band)
                {
                    SetBandSelect(attempt == 0
                        ? $"{name} already selected"
                        : $"{name} confirmed after {attempt} attempt{(attempt == 1 ? "" : "s")}", ct);
                    return;
                }
                if (status is { Tx: true } && !_opt.AllowChangesDuringTx)
                {
                    SetBandSelect($"{name}: stopped, amplifier is transmitting", ct);
                    return;
                }
                if (_opt.BandMaxAttempts > 0 && attempt >= _opt.BandMaxAttempts)
                {
                    SetBandSelect($"{name}: amplifier ignored {attempt} attempts", ct);
                    return;
                }

                attempt++;
                SetBandSelect($"selecting {name}: attempt {attempt}", ct);
                if (ct.IsCancellationRequested) return;
                await SendTcpAsync(AmpProtocol.Band(band)).ConfigureAwait(false);

                var deadline = DateTime.UtcNow + _opt.BandVerifyDelay;
                while (DateTime.UtcNow < deadline)
                {
                    lock (_gate) { if (_status is { } s && s.Band == band) break; }
                    await Task.Delay(40, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            SetBandSelect($"{name}: {ex.Message}", ct);
        }
    }
}
