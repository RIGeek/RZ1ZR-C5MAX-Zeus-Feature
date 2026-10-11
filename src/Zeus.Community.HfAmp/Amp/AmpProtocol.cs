// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zeus.Community.HfAmp.Amp;

/// <summary>
/// Wire protocol of the amplifier, decoded from packet captures of its own
/// control software. This is NOT vendor documentation; fields that were never
/// observed to change are marked as unverified.
///
///   TCP 5005  host -> amp   two ASCII digits per command, no terminator
///             amp  -> host  status push, sent whenever any field changes:
///                           "temp,volts*10,band,antenna,flag4,tx,fanHigh,bypass"
///   UDP 5010  host sends "11"; while the amp transmits it streams
///             "forward,reflected,current,input" raw detector counts (~20 Hz)
/// </summary>
internal static class AmpProtocol
{
    public const int TcpPort = 5005;
    public const int UdpPort = 5010;
    public const int AntennaCount = 4;
    public const int BandCount = 8;

    /// <summary>Filter bands in command order: command "7n" selects BandNames[n-1].</summary>
    public static readonly string[] BandNames =
        ["160m", "80m", "60-40m", "30m", "20m", "17-15m", "12-10m", "6m"];

    public const string Hello = "11";
    public const string Release = "99";      // hand control back to the front panel
    public const string Reset = "23";
    public const string VoltageLow = "41";
    public const string VoltageHigh = "42";
    public const string FansForced = "45";   // fans forced to 100 %
    public const string FansAuto = "46";
    public const string BypassOn = "21";     // cuts the LDMOS supply
    public const string BypassOff = "22";

    public static string Antenna(int n)
    {
        if (n < 1 || n > AntennaCount) throw new ArgumentOutOfRangeException(nameof(n));
        return "3" + n.ToString(CultureInfo.InvariantCulture);
    }

    public static string Band(int n)
    {
        if (n < 1 || n > BandCount) throw new ArgumentOutOfRangeException(nameof(n));
        return "7" + n.ToString(CultureInfo.InvariantCulture);
    }

    public static string BandName(int band) =>
        band >= 1 && band <= BandCount ? BandNames[band - 1] : "?";
}

/// <summary>One decoded status push from the amplifier.</summary>
internal readonly record struct AmpStatus(
    int TempC, double Volts, int Band, int Antenna, int Flag4,
    bool Tx, bool FanHigh, bool Bypass)
{
    /// <summary>
    /// The supply setting is user adjustable (about 42 V to 56 V), so "high"
    /// is inferred from the measured voltage and is only a heuristic.
    /// </summary>
    public bool HighVoltage => Volts >= Calibration.HighVoltageThreshold;
}

/// <summary>
/// Splits the status byte stream into messages. Messages have no delimiter, so
/// two pushes can arrive in one read or one push can be split across reads.
/// Fields after the second are single digits, which makes the format
/// unambiguous even when messages run together.
/// </summary>
internal sealed class StatusStream
{
    private static readonly Regex Message = new(
        @"(\d+),(\d+),(\d),(\d),(\d),(\d),(\d),(\d)",
        RegexOptions.CultureInvariant);

    private const int MaxTail = 64;
    private string _buffer = string.Empty;

    public List<AmpStatus> Feed(ReadOnlySpan<byte> data)
    {
        var results = new List<AmpStatus>();
        _buffer += Encoding.ASCII.GetString(data);

        var consumed = 0;
        for (var m = Message.Match(_buffer); m.Success; m = m.NextMatch())
        {
            if (TryConvert(m, out var status)) results.Add(status);
            consumed = m.Index + m.Length;
        }

        _buffer = _buffer[consumed..];
        if (_buffer.Length > MaxTail) _buffer = _buffer[^MaxTail..];
        return results;
    }

    private static bool TryConvert(Match m, out AmpStatus status)
    {
        status = default;
        var v = new int[8];
        for (var i = 0; i < 8; i++)
        {
            if (!int.TryParse(m.Groups[i + 1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out v[i]))
                return false;
        }
        status = new AmpStatus(v[0], v[1] / 10.0, v[2], v[3], v[4], v[5] != 0, v[6] != 0, v[7] != 0);
        return true;
    }
}

/// <summary>One UDP meter datagram: raw detector counts.</summary>
internal readonly record struct MeterSample(int Forward, int Reflected, int Current, int Input)
{
    public static bool TryParse(ReadOnlySpan<byte> payload, out MeterSample sample)
    {
        sample = default;
        // The amp pads datagrams; trim NULs and whitespace defensively.
        var text = Encoding.ASCII.GetString(payload).Trim('\0', ' ', '\r', '\n');
        var parts = text.Split(',');
        if (parts.Length != 4) return false;

        var v = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out v[i]))
                return false;
        }
        sample = new MeterSample(v[0], v[1], v[2], v[3]);
        return true;
    }
}

/// <summary>Engineering-unit readings derived from raw counts. All approximate.</summary>
internal readonly record struct Readings(
    double ForwardW, double ReflectedW, double InputW, double CurrentA,
    double? Swr, double? EfficiencyPercent);

/// <summary>
/// Meter calibration, fitted to the amplifier's own front-panel readings. Forward power was compared
/// at five points (42-388 W), reflected power at five (1-29 W) and current at six (0.9-20.5 A); the
/// ratio was constant across each range (power within about 1 W, current within 0.1 A), so one scale
/// factor per quantity is enough. The same power scale fits forward and reflected. Input power has
/// not been compared and uses the same scale. Treat it as roughly +/-3 % and possibly band dependent.
///
/// The front panel current is about 1.5 times what the vendor application shows, so it may include
/// stages other than the final transistors; efficiency below is a drain estimate on that current.
/// </summary>
internal static class Calibration
{
    /// <summary>Power in watts = counts squared / PowerDivisor (square-law detectors).</summary>
    public const double PowerDivisor = 475.3;

    /// <summary>Drain current in amps = counts * AmpsPerCount (about 1 A of idle bias at 5 counts).</summary>
    public const double AmpsPerCount = 0.2051;

    /// <summary>Below this forward count the reflected/forward ratio is noise, so no SWR is shown.</summary>
    public const double MinSwrCounts = 30;

    /// <summary>Efficiency is meaningless at idle: require real output and real current.</summary>
    public const double MinEfficiencyForwardW = 10;
    public const double MinEfficiencyCurrentA = 1;

    /// <summary>Supply voltage at or above which the "high" supply setting is assumed.</summary>
    public const double HighVoltageThreshold = 45.0;

    public static Readings Compute(double forward, double reflected, double current, double input, double volts)
    {
        var fwdW = forward * forward / PowerDivisor;
        var rflW = reflected * reflected / PowerDivisor;
        var inpW = input * input / PowerDivisor;
        var amps = current * AmpsPerCount;

        // Same ratio the vendor application displays: (f + r) / (f - r) on the raw counts.
        double? swr = null;
        if (forward >= MinSwrCounts && forward > reflected)
            swr = Math.Min(99.0, (forward + reflected) / (forward - reflected));

        // Drain efficiency estimate: RF output power / (supply volts x front-panel current).
        // The vendor application showed 82 % for its own screenshot; its formula is unknown and
        // cannot be reproduced from these readings, so treat this as an estimate.
        double? eff = null;
        if (fwdW >= MinEfficiencyForwardW && amps >= MinEfficiencyCurrentA && volts > 0)
            eff = Math.Clamp(100.0 * fwdW / (volts * amps), 0.0, 100.0);

        return new Readings(fwdW, rflW, inpW, amps, swr, eff);
    }
}

/// <summary>Exponential smoothing of the raw counts, which jitter heavily under RF.</summary>
internal sealed class MeterSmoother
{
    private readonly double _alpha;
    private bool _primed;
    private double _fwd, _rfl, _cur, _inp;

    public MeterSmoother(double alpha = 0.35) => _alpha = alpha;

    public (double Forward, double Reflected, double Current, double Input) Add(MeterSample s)
    {
        if (!_primed)
        {
            (_fwd, _rfl, _cur, _inp) = (s.Forward, s.Reflected, s.Current, s.Input);
            _primed = true;
        }
        else
        {
            _fwd += _alpha * (s.Forward - _fwd);
            _rfl += _alpha * (s.Reflected - _rfl);
            _cur += _alpha * (s.Current - _cur);
            _inp += _alpha * (s.Input - _inp);
        }
        return (_fwd, _rfl, _cur, _inp);
    }

    public void Reset() => _primed = false;
}
