// SPDX-License-Identifier: GPL-2.0-or-later
//
// Self-contained test runner (a .NET 10 file-based app, so no test project or
// test-framework packages are needed):
//
//     dotnet run tests/HfAmp.Tests.cs
//
// It talks to a mock amplifier that implements the protocol decoded from the
// packet captures, including coalesced status pushes and a firmware that
// ignores band commands. Nothing here touches real hardware.
#:sdk Microsoft.NET.Sdk.Web
#:property AssemblyName=Zeus.Community.HfAmp.Tests
#:property PublishAot=false
#:project ../src/Zeus.Community.HfAmp/Zeus.Community.HfAmp.csproj

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Community.HfAmp;
using Zeus.Community.HfAmp.Amp;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
void Check(bool condition, string message) { if (!condition) throw new Exception("FAILED: " + message); }
async Task<bool> Until(Func<bool> condition, int timeoutMs = 4000)
{
    var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(20); }
    return condition();
}
AmpClientOptions Fast(MockAmp m, int maxAttempts = 5, bool allowTx = false) => new()
{
    TcpPort = m.TcpPort, UdpPort = m.UdpPort,
    BandVerifyDelay = TimeSpan.FromMilliseconds(150), BandMaxAttempts = maxAttempts,
    MeterFreshFor = TimeSpan.FromMilliseconds(600),
    ReconnectMin = TimeSpan.FromMilliseconds(100), ReconnectMax = TimeSpan.FromMilliseconds(300),
    AllowChangesDuringTx = allowTx,
};
async Task<AmpClient> Connected(MockAmp m, AmpClientOptions? o = null)
{
    var c = new AmpClient(NullLogger.Instance, o ?? Fast(m));
    await c.StartAsync("127.0.0.1");
    Check(await Until(() => c.GetSnapshot().Status is not null), "client never received a status push");
    return c;
}

// ------------------------------------------------------------------ parsing

Test("status: fields decode", () =>
{
    var s = new StatusStream().Feed("35,419,7,1,0,1,1,0"u8);
    Check(s.Count == 1, "one message");
    var m = s[0];
    Check(m.TempC == 35 && Math.Abs(m.Volts - 41.9) < 1e-9 && m.Band == 7 && m.Antenna == 1, "numeric fields");
    Check(m.Tx && m.FanHigh && !m.Bypass && m.Flag4 == 0, "flags");
    return Task.CompletedTask;
});

Test("status: two messages in one read, and a message split across reads", () =>
{
    var p = new StatusStream();
    var both = p.Feed("28,419,6,1,0,0,0,028,422,6,1,0,0,0,0"u8);
    Check(both.Count == 2 && both[0].Volts == 41.9 && both[1].Volts == 42.2, "coalesced pair");

    var a = p.Feed("28,419,6,"u8);
    Check(a.Count == 0, "partial yields nothing");
    var b = p.Feed("1,0,0,0,0"u8);
    Check(b.Count == 1 && b[0].Band == 6, "split message completes");
    return Task.CompletedTask;
});

Test("status: garbage is skipped", () =>
{
    var s = new StatusStream().Feed("xx??\0\0 28,419,6,1,0,0,0,0 zz"u8);
    Check(s.Count == 1, "one message found in noise");
    return Task.CompletedTask;
});

Test("meter: parse, padding, and rejects", () =>
{
    Check(MeterSample.TryParse("454,97,111,30"u8, out var m) && m.Forward == 454 && m.Input == 30, "plain");
    Check(MeterSample.TryParse("454,97,111,30\0\0\0\0\0"u8, out _), "NUL padding");
    Check(!MeterSample.TryParse("1,2,3"u8, out _), "too few");
    Check(!MeterSample.TryParse("a,b,c,d"u8, out _), "non numeric");
    Check(!MeterSample.TryParse("-1,2,3,4"u8, out _), "negative");
    return Task.CompletedTask;
});

Test("calibration reproduces the reference screenshot", () =>
{
    // Screenshot: 458 W forward, 21 W reflected, 2 W input, 15.2 A, SWR 1.55, 41.8 V
    var r = Calibration.Compute(454, 97, 111, 30, 41.8);
    Check(Math.Abs(r.ForwardW - 458) < 3, $"forward {r.ForwardW}");
    Check(Math.Abs(r.ReflectedW - 21) < 1.5, $"reflected {r.ReflectedW}");
    Check(Math.Abs(r.InputW - 2) < 0.3, $"input {r.InputW}");
    Check(Math.Abs(r.CurrentA - 15.2) < 0.5, $"current {r.CurrentA}");
    Check(r.Swr is { } swr && Math.Abs(swr - 1.55) < 0.05, $"swr {r.Swr}");
    Check(r.EfficiencyPercent is { } e && e > 70 && e < 76, $"efficiency {r.EfficiencyPercent}");
    return Task.CompletedTask;
});

Test("calibration: idle shows no SWR or efficiency", () =>
{
    var r = Calibration.Compute(0, 0, 5, 0, 41.9);
    Check(r.Swr is null && r.EfficiencyPercent is null, "nothing meaningful at idle");
    Check(Calibration.Compute(454, 97, 111, 30, 0).EfficiencyPercent is null, "no volts, no efficiency");
    Check(Calibration.Compute(454, 454, 111, 30, 41.9).Swr is null, "reflected == forward has no SWR");
    return Task.CompletedTask;
});

Test("band plan", () =>
{
    (long Hz, int? Band)[] cases =
    [
        (1_840_000, 1), (3_750_000, 2), (5_357_000, 3), (7_074_000, 3), (10_136_000, 4),
        (14_200_000, 5), (18_100_000, 6), (21_300_000, 6), (24_930_000, 7), (28_400_000, 7), (50_313_000, 8),
        (11_000_000, null), (0, null), (144_200_000, null), (7_300_001, null), (6_000_000, null),
    ];
    foreach (var (hz, band) in cases)
        Check(BandPlan.FromHz(hz) == band, $"{hz} Hz -> {BandPlan.FromHz(hz)?.ToString() ?? "none"}, expected {band?.ToString() ?? "none"}");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ client against the mock amp

Test("client: connects, says hello on both channels, reads status", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    Check(await Until(() => amp.UdpHellos >= 1 && amp.Count("11") >= 1), "UDP and TCP hello");
    var s = c.GetSnapshot();
    Check(s.Link == LinkState.Connected && s.Status is { Band: 6, Antenna: 1 }, "status mirrored");
});

Test("client: antenna command and range check", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    Check((await c.SetAntennaAsync(3)).Ok, "antenna 3 accepted");
    Check(await Until(() => c.GetSnapshot().Status is { Antenna: 3 }), "antenna reported");
    Check(amp.Count("33") == 1, "33 sent exactly once");
    var bad = await c.SetAntennaAsync(5);
    Check(!bad.Ok && bad.Status == 400 && amp.Count("35") == 0, "antenna 5 rejected locally");
});

Test("band select: retries until the flaky firmware accepts", async () =>
{
    await using var amp = new MockAmp { IgnoreBandCommands = 2 };
    await using var c = await Connected(amp);
    Check(c.StartBandSelect(3).Ok, "started");
    Check(await Until(() => c.GetSnapshot().Status is { Band: 3 }), "band 3 reported");
    Check(await Until(() => c.GetSnapshot().BandSelect?.Contains("confirmed after 3 attempts") == true),
        "message: " + c.GetSnapshot().BandSelect);
    Check(amp.Count("73") == 3, $"73 sent {amp.Count("73")} times, expected 3");
});

Test("band select: gives up after the attempt limit", async () =>
{
    await using var amp = new MockAmp { IgnoreBandCommands = 1000 };
    await using var c = await Connected(amp, Fast(amp, maxAttempts: 3));
    c.StartBandSelect(4);
    Check(await Until(() => c.GetSnapshot().BandSelect?.Contains("ignored 3 attempts") == true, 5000),
        "message: " + c.GetSnapshot().BandSelect);
    await Task.Delay(400);
    Check(amp.Count("74") == 3, $"74 sent {amp.Count("74")} times, expected 3");
});

Test("band select: a newer request replaces an older one", async () =>
{
    await using var amp = new MockAmp { IgnoreBandCommands = 1000 };
    await using var c = await Connected(amp);
    c.StartBandSelect(4);
    Check(await Until(() => amp.Count("74") >= 1), "first request sent");
    amp.IgnoreBandCommands = 0;
    c.StartBandSelect(5);
    Check(await Until(() => c.GetSnapshot().Status is { Band: 5 }), "second request honoured");
    var sent = amp.Count("74");
    await Task.Delay(500);
    Check(amp.Count("74") == sent, "the superseded loop stopped sending");
    Check(c.GetSnapshot().BandSelect?.Contains("20m confirmed") == true, "message: " + c.GetSnapshot().BandSelect);
});

Test("band select: stops when the amp starts transmitting", async () =>
{
    await using var amp = new MockAmp { IgnoreBandCommands = 1000 };
    await using var c = await Connected(amp);
    c.StartBandSelect(3);
    Check(await Until(() => amp.Count("73") >= 1), "first attempt sent");
    amp.SetTx(true);
    Check(await Until(() => c.GetSnapshot().BandSelect?.Contains("transmitting") == true),
        "message: " + c.GetSnapshot().BandSelect);
    var sent = amp.Count("73");
    await Task.Delay(400);
    Check(amp.Count("73") == sent, "no band commands while transmitting");
});

Test("interlocks: transmit blocks relays and supply, allows safety actions", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    amp.SetTx(true);
    Check(await Until(() => c.GetSnapshot().Status is { Tx: true }), "tx seen");

    Check((await c.SetAntennaAsync(2)) is { Ok: false, Status: 409 }, "antenna blocked");
    Check(c.StartBandSelect(2) is { Ok: false, Status: 409 }, "band blocked");
    Check((await c.SetVoltageAsync(true)) is { Ok: false, Status: 409 }, "voltage blocked");
    Check((await c.SetBypassAsync(false)) is { Ok: false, Status: 409 }, "restoring LDMOS supply blocked");

    Check((await c.SetBypassAsync(true)).Ok, "cutting the supply is always allowed");
    Check((await c.ResetAsync()).Ok, "reset allowed");
    Check((await c.SetFansAsync(true)).Ok, "fans allowed");
    await Task.Delay(100);
    Check(amp.Count("32") == 0 && amp.Count("72") == 0 && amp.Count("42") == 0 && amp.Count("22") == 0,
        "blocked commands never reached the amp");
    Check(amp.Count("21") == 1 && amp.Count("23") == 1 && amp.Count("45") == 1, "allowed commands did");
});

Test("interlocks can be relaxed by option", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp, Fast(amp, allowTx: true));
    amp.SetTx(true);
    Check(await Until(() => c.GetSnapshot().Status is { Tx: true }), "tx seen");
    Check((await c.SetAntennaAsync(2)).Ok, "allowed when explicitly enabled");
});

Test("cooling: tracks what we asked for, not the amp's own fan flag", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    Check(!c.GetSnapshot().FansForced, "starts auto");
    await c.SetFansAsync(true);
    Check(c.GetSnapshot().FansForced, "forced");
    Check(await Until(() => amp.Count("45") == 1), "amp received 45");
    await c.SetFansAsync(false);
    Check(!c.GetSnapshot().FansForced, "back to auto");
    Check(await Until(() => amp.Count("46") == 1), "amp received 46");
});

Test("meters: derived readings and efficiency while transmitting, stale afterwards", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    Check(c.GetSnapshot().Meter is null, "no meters at idle");

    amp.SetTx(true);
    Check(await Until(() => c.GetSnapshot().Meter is { Fresh: true }), "meter stream arrived");
    await Task.Delay(300);
    var m = c.GetSnapshot().Meter!;
    Check(Math.Abs(m.Readings.ForwardW - 458) < 6, $"forward {m.Readings.ForwardW}");
    Check(m.Readings.Swr is > 1.45 and < 1.65, $"swr {m.Readings.Swr}");
    Check(m.Readings.EfficiencyPercent is > 70 and < 76, $"efficiency {m.Readings.EfficiencyPercent}");

    amp.SetTx(false);
    Check(await Until(() => c.GetSnapshot().Meter is { Fresh: false }, 3000), "meter goes stale after TX");
});

Test("meters: stream is requested again on every TX start", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    await Until(() => amp.UdpHellos >= 1);
    var before = amp.UdpHellos;
    amp.SetTx(true);
    Check(await Until(() => amp.UdpHellos > before), "hello on TX start");
});

Test("lifecycle: disposing releases the amp (command 99)", async () =>
{
    await using var amp = new MockAmp();
    var c = await Connected(amp);
    await c.DisposeAsync();
    Check(await Until(() => amp.Count("99") == 1), "99 sent exactly once");
});

Test("lifecycle: changing host releases the old amp first", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    await c.StartAsync(null);
    Check(amp.Count("99") == 1 && c.GetSnapshot().Link == LinkState.Unconfigured, "released and unconfigured");
    Check((await c.ResetAsync()) is { Ok: false, Status: 409 }, "commands refused while unconfigured");
});

Test("lifecycle: reconnects after the amp drops the connection", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    var hellos = amp.Count("11");
    amp.DropConnection();
    Check(await Until(() => amp.Count("11") > hellos, 6000), "client reconnected and said hello again");
    Check(await Until(() => c.GetSnapshot() is { Link: LinkState.Connected, Status: not null }), "status flowing again");
});

Test("lifecycle: unreachable host reports an error and keeps trying", async () =>
{
    var dead = new TcpListener(IPAddress.Loopback, 0); dead.Start();
    var port = ((IPEndPoint)dead.LocalEndpoint).Port; dead.Stop();   // nothing listens here now
    await using var c = new AmpClient(NullLogger.Instance, new AmpClientOptions
    { TcpPort = port, UdpPort = port, ReconnectMin = TimeSpan.FromMilliseconds(100), ReconnectMax = TimeSpan.FromMilliseconds(200) });
    await c.StartAsync("127.0.0.1");
    Check(await Until(() => c.GetSnapshot() is { Link: LinkState.Disconnected, Error: not null }), "disconnected with an error");
});

// ------------------------------------------------------------------ band following

Test("follow: moves the amp when the radio settles in a new band", async () =>
{
    await using var amp = new MockAmp();                       // amp starts on band 6
    await using var c = await Connected(amp);
    var radio = new FakeRadio { Hz = 7_074_000 };              // band 3
    var f = new BandFollower(c, radio, NullLogger.Instance, new BandFollowOptions { Settle = TimeSpan.FromSeconds(1) }) { Enabled = true };
    var t = DateTime.UtcNow;

    f.Tick(t);
    Check(f.Describe().State.Contains("settling"), "settling first: " + f.Describe().State);
    Check(amp.Count("73") == 0, "no command before the radio has settled");

    f.Tick(t.AddSeconds(1.1));
    Check(await Until(() => c.GetSnapshot().Status is { Band: 3 }), "amp moved to band 3");
    Check(amp.Count("73") == 1, "exactly one command");
    f.Tick(t.AddSeconds(2));
    Check(f.Describe().State.Contains("following"), "state: " + f.Describe().State);
});

Test("follow: a manual band choice sticks until the radio changes band", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    var radio = new FakeRadio { Hz = 7_074_000 };
    var f = new BandFollower(c, radio, NullLogger.Instance, new BandFollowOptions { Settle = TimeSpan.FromSeconds(1) }) { Enabled = true };
    var t = DateTime.UtcNow;
    f.Tick(t); f.Tick(t.AddSeconds(1.1));
    Check(await Until(() => c.GetSnapshot().Status is { Band: 3 }), "followed to 3");

    c.StartBandSelect(2);                                      // operator overrides by hand
    Check(await Until(() => c.GetSnapshot().Status is { Band: 2 }), "manual band 2");
    for (var i = 0; i < 6; i++) f.Tick(t.AddSeconds(2 + i));
    await Task.Delay(300);
    Check(amp.Count("73") == 1, "follower did not fight the manual choice");

    radio.Hz = 21_300_000;                                     // radio moves to band 6
    f.Tick(t.AddSeconds(10)); f.Tick(t.AddSeconds(11.2));
    Check(await Until(() => c.GetSnapshot().Status is { Band: 6 }), "followed the radio to band 6");
});

Test("follow: never switches while the radio or the amp transmits; catches up afterwards", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    var radio = new FakeRadio { Hz = 14_200_000 };             // band 5
    var f = new BandFollower(c, radio, NullLogger.Instance, new BandFollowOptions { Settle = TimeSpan.FromSeconds(1) }) { Enabled = true };
    var t = DateTime.UtcNow;

    radio.Transmitting = true;
    f.Tick(t); f.Tick(t.AddSeconds(2));
    Check(amp.Count("75") == 0 && f.Describe().State.Contains("transmitting"), "radio TX holds it back: " + f.Describe().State);
    radio.Transmitting = false;
    amp.SetTx(true);
    Check(await Until(() => c.GetSnapshot().Status is { Tx: true }), "amp tx seen");
    f.Tick(t.AddSeconds(3));
    Check(amp.Count("75") == 0, "amp TX holds it back");
    amp.SetTx(false);
    Check(await Until(() => c.GetSnapshot().Status is { Tx: false }), "amp rx again");
    f.Tick(t.AddSeconds(4));
    Check(await Until(() => c.GetSnapshot().Status is { Band: 5 }), "switched once transmit ended");
});

Test("follow: ignores frequencies outside the amp's bands", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    var radio = new FakeRadio { Hz = 11_000_000 };
    var f = new BandFollower(c, radio, NullLogger.Instance, new BandFollowOptions { Settle = TimeSpan.Zero }) { Enabled = true };
    var t = DateTime.UtcNow;
    f.Tick(t); f.Tick(t.AddSeconds(5));
    Check(amp.Commands.All(x => x == "11") && f.Describe().State.Contains("outside"), "state: " + f.Describe().State);
});

Test("follow: off, unavailable, and re-enable behaviour", async () =>
{
    await using var amp = new MockAmp();
    await using var c = await Connected(amp);
    var radio = new FakeRadio { Hz = 3_700_000 };              // band 2
    var f = new BandFollower(c, radio, NullLogger.Instance, new BandFollowOptions { Settle = TimeSpan.Zero });
    var t = DateTime.UtcNow;

    f.Tick(t); f.Tick(t.AddSeconds(1));
    Check(amp.Count("72") == 0 && f.Describe().State == "off", "disabled does nothing");

    radio.Available = false; f.Enabled = true;
    f.Tick(t.AddSeconds(2));
    Check(f.Describe().State.Contains("not available") && amp.Count("72") == 0, "unavailable source");

    radio.Available = true;
    f.Tick(t.AddSeconds(3)); f.Tick(t.AddSeconds(4));
    Check(await Until(() => c.GetSnapshot().Status is { Band: 2 }), "enabling syncs to the radio");
});

// ------------------------------------------------------------------ HTTP layer

Test("http: status, commands, validation and cross-site protection", async () =>
{
    await using var amp = new MockAmp();
    var service = new HfAmpService(NullLogger.Instance, new MemorySettingsStore(), new FakeRadio { Available = false }, Fast(amp));
    await service.StartAsync(CancellationToken.None);

    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    HfAmpEndpoints.Map(app.MapGroup("/api/plugins/test"), () => service);
    await app.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/api/plugins/test/") };

    async Task<(int Code, JsonElement Body)> Call(HttpMethod method, string path, string? json = null, string mediaType = "application/json",
        string? fetchSite = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, mediaType);
        if (fetchSite is not null) req.Headers.Add("Sec-Fetch-Site", fetchSite);
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        Check(res.Headers.CacheControl?.NoStore == true || (int)res.StatusCode >= 400, "responses are not cacheable");
        return ((int)res.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    var (code, st) = await Call(HttpMethod.Get, "status");
    Check(code == 200 && st.GetProperty("connection").GetProperty("state").GetString() == "unconfigured", "starts unconfigured");
    Check(!st.GetProperty("follow").GetProperty("available").GetBoolean(), "follow reports unavailable");

    (code, _) = await Call(HttpMethod.Post, "settings", "{\"host\":\"not a host!\"}");
    Check(code == 400, "bad host rejected");
    (code, _) = await Call(HttpMethod.Post, "settings", "{\"host\":\"http://evil/\"}");
    Check(code == 400, "URL rejected as host");
    (code, _) = await Call(HttpMethod.Post, "settings", "{\"host\":\"127.0.0.1\"}");
    Check(code == 200, "good host accepted");
    Check(await Until(() => service.Amp.GetSnapshot().Status is not null), "connected after saving the host");

    (code, st) = await Call(HttpMethod.Get, "status");
    Check(st.GetProperty("amp").GetProperty("bandName").GetString() == "17-15m", "band name served");
    Check(st.GetProperty("controls").GetProperty("bands").GetArrayLength() == 8, "band list served");

    (code, var r) = await Call(HttpMethod.Post, "command", "{\"action\":\"band\",\"band\":3}");
    Check(code == 200 && r.GetProperty("ok").GetBoolean(), "band command accepted");
    Check(await Until(() => amp.Band == 3), "amp changed band");

    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"antenna\",\"antenna\":9}");
    Check(code == 400, "antenna 9 -> 400");
    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"explode\"}");
    Check(code == 400, "unknown action -> 400");
    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"voltage\"}");
    Check(code == 400, "missing mode -> 400");
    (code, _) = await Call(HttpMethod.Post, "command", "{not json");
    Check(code == 400, "malformed JSON -> 400");
    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"reset\"}", "text/plain");
    Check(code == 415, "non-JSON content type -> 415");
    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"reset\"}", fetchSite: "cross-site");
    Check(code == 403, "cross-site browser request -> 403");
    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"reset\"}", fetchSite: "same-site");
    Check(code == 200, "same-site (Zeus UI on another port) is accepted");
    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"reset\"}", fetchSite: "same-origin");
    Check(code == 200, "same-origin browser request allowed");
    (code, _) = await Call(HttpMethod.Post, "command", "{\"action\":\"note\",\"mode\":\"" + new string('x', 6000) + "\"}");
    Check(code == 413, "oversized body -> 413");
    Check(await Until(() => amp.Count("23") == 2), "only the same-site and same-origin resets reached the amp (cross-site was blocked)");

    amp.SetTx(true);
    Check(await Until(() => service.Amp.GetSnapshot().Status is { Tx: true }), "tx seen");
    (code, r) = await Call(HttpMethod.Post, "command", "{\"action\":\"antenna\",\"antenna\":2}");
    Check(code == 409 && !r.GetProperty("ok").GetBoolean(), "interlock surfaces as 409");

    await app.StopAsync();
    await service.DisposeAsync();
    Check(amp.Count("99") == 1, "service shutdown released the amp");
});

Test("http: routes answer 503 before the feature is running", async () =>
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    HfAmpEndpoints.Map(app.MapGroup("/p"), () => null);
    await app.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/p/") };
    Check((int)(await http.GetAsync("status")).StatusCode == 503, "status 503");
    await app.StopAsync();
});

Test("service: settings persist through the store and host validation", async () =>
{
    var store = new MemorySettingsStore();
    await using var amp = new MockAmp();
    var svc = new HfAmpService(NullLogger.Instance, store, new FakeRadio(), Fast(amp));
    await svc.StartAsync(CancellationToken.None);
    Check((await svc.ApplySettingsAsync(new AmpSettings("127.0.0.1", true), CancellationToken.None)).Ok, "saved");
    Check((await store.LoadAsync(CancellationToken.None)) is { Host: "127.0.0.1", FollowBand: true }, "store updated");
    Check(svc.Follower.Enabled, "follow enabled");
    Check(HfAmpService.IsValidHost("amp.local") && HfAmpService.IsValidHost("10.10.10.77"), "valid hosts");
    Check(!HfAmpService.IsValidHost("a/b") && !HfAmpService.IsValidHost("a b") && !HfAmpService.IsValidHost("[::1]:80"), "invalid hosts");
    await svc.DisposeAsync();
});

// ------------------------------------------------------------------ run

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
    }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

// ------------------------------------------------------------------ test doubles

sealed class FakeRadio : IRadioStateSource
{
    public bool Available { get; set; } = true;
    public long Hz { get; set; } = 14_200_000;
    public bool Transmitting { get; set; }
    public RadioState? Read() => Available ? new RadioState(Hz, Transmitting) : null;
}

/// <summary>Implements the decoded protocol closely enough to drive the client.</summary>
sealed class MockAmp : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<string> _commands = [];
    private readonly Random _jitter = new(1);
    private NetworkStream? _stream;
    private TcpClient? _client;
    private IPEndPoint? _meterTarget;
    private int _udpHellos;

    public int Temp = 28, Volts10 = 419, Band = 6, Ant = 1, Flag4, Tx, Fan, Bypass;
    public int IgnoreBandCommands;                 // swallow this many band commands (the firmware bug)
    public int[] Counts = [454, 97, 111, 30];      // the reference screenshot's operating point

    public int TcpPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int UdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int UdpHellos => Volatile.Read(ref _udpHellos);
    public string[] Commands { get { lock (_lock) return [.. _commands]; } }
    public int Count(string command) => Commands.Count(c => c == command);

    public MockAmp()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(UdpLoop);
        _ = Task.Run(MeterLoop);
    }

    public void SetTx(bool on) { Tx = on ? 1 : 0; Push(); }

    public void DropConnection() { try { _client?.Close(); } catch { } }

    private void Push()
    {
        var s = _stream;
        if (s is null) return;
        var text = $"{Temp},{Volts10},{Band},{Ant},{Flag4},{Tx},{Fan},{Bypass}";
        try { lock (_lock) s.Write(Encoding.ASCII.GetBytes(text)); } catch { }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;
                _client = client;
                _stream = client.GetStream();
                _ = Task.Run(() => ReadLoop(client));
            }
        }
        catch { }
    }

    private async Task ReadLoop(TcpClient client)
    {
        var stream = client.GetStream();
        var buffer = new byte[64];
        var pending = "";
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, _cts.Token);
                if (n == 0) return;
                pending += Encoding.ASCII.GetString(buffer, 0, n);     // commands may arrive coalesced
                while (pending.Length >= 2)
                {
                    Handle(pending[..2]);
                    pending = pending[2..];
                }
            }
        }
        catch { }
    }

    private void Handle(string cmd)
    {
        lock (_lock) _commands.Add(cmd);
        switch (cmd)
        {
            case "11": break;
            case "99": return;
            case var c when c[0] == '3': Ant = c[1] - '0'; break;
            case var c when c[0] == '7':
                if (IgnoreBandCommands > 0) { IgnoreBandCommands--; return; }
                Band = c[1] - '0'; break;
            case "41": Volts10 = 420; break;
            case "42": Volts10 = 480; break;
            case "45": Fan = 1; break;
            case "46": Fan = 0; break;
            case "21": Bypass = 1; Volts10 = 0; break;
            case "22": Bypass = 0; Volts10 = 420; break;
        }
        Push();
    }

    private async Task UdpLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                if (Encoding.ASCII.GetString(r.Buffer) == "11")
                {
                    _meterTarget = r.RemoteEndPoint;
                    Interlocked.Increment(ref _udpHellos);
                }
            }
        }
        catch { }
    }

    private async Task MeterLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(50, _cts.Token);
                var target = _meterTarget;
                if (Tx == 0 || target is null) continue;
                var c = Counts.Select(v => v + _jitter.Next(-1, 2)).ToArray();
                var payload = Encoding.ASCII.GetBytes(string.Join(",", c));
                await _udp.SendAsync(payload, target, _cts.Token);
            }
        }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _client?.Close(); } catch { }
        _listener.Stop();
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
