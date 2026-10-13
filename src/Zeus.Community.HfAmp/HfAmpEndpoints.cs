// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Zeus.Community.HfAmp.Amp;

namespace Zeus.Community.HfAmp;

internal sealed record CommandRequest(string? Action, int? Band, int? Antenna, string? Mode);

internal sealed record SettingsRequest(string? Host, bool? FollowBand, int? PowerClass, double? HighSupplyVolts);

/// <summary>
/// HTTP surface, mounted by Zeus under /api/plugins/&lt;id&gt;/. It can switch relays
/// and cut the PA supply, so state-changing routes refuse cross-site browser
/// requests and anything that is not JSON.
/// </summary>
internal static class HfAmpEndpoints
{
    private const int MaxBodyBytes = 4096;
    private const int MaxPrefsBytes = 16384;

    public static void Map(IEndpointRouteBuilder endpoints, Func<HfAmpService?> service)
    {
        endpoints.MapGet("status", (HttpContext ctx) =>
            With(ctx, service, s => Results.Json(s.BuildStatus())));

        endpoints.MapGet("settings", (HttpContext ctx) =>
            With(ctx, service, s =>
            {
                var c = s.GetSettings();
                return Results.Json(new { host = c.Host, followBand = c.FollowBand, powerClass = c.PowerClass, highSupplyVolts = c.HighSupplyVolts });
            }));

        endpoints.MapGet("prefs", (HttpContext ctx) =>
            With(ctx, service, s => Results.Text(s.GetPrefs() ?? "{}", "application/json")));

        endpoints.MapPost("prefs", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();

            var (body, error) = await ReadJsonAsync<JsonElement?>(ctx, MaxPrefsBytes);
            if (error is not null) return error;
            if (body is not { ValueKind: JsonValueKind.Object } prefs)
                return Refuse(400, "Display settings must be a JSON object");

            return Reply(ctx, await s.SetPrefsAsync(prefs.GetRawText(), ctx.RequestAborted));
        });

        endpoints.MapPost("settings", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();

            var (body, error) = await ReadJsonAsync<SettingsRequest>(ctx);
            if (error is not null) return error;

            var current = s.GetSettings();
            var result = await s.ApplySettingsAsync(
                current with
                {
                    Host = body!.Host ?? current.Host,
                    FollowBand = body.FollowBand ?? current.FollowBand,
                    PowerClass = body.PowerClass ?? current.PowerClass,
                    HighSupplyVolts = body.HighSupplyVolts ?? current.HighSupplyVolts,
                },
                ctx.RequestAborted);
            return Reply(ctx, result);
        });

        endpoints.MapPost("command", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();

            var (body, error) = await ReadJsonAsync<CommandRequest>(ctx);
            if (error is not null) return error;

            var mode = body!.Mode?.ToLowerInvariant();
            CommandResult result = body.Action?.ToLowerInvariant() switch
            {
                "reset" => await s.Amp.ResetAsync(),
                "band" when body.Band is int band => s.Amp.StartBandSelect(band),
                "antenna" when body.Antenna is int antenna => await s.Amp.SetAntennaAsync(antenna),
                "voltage" when mode is "low" or "high" => await s.Amp.SetVoltageAsync(mode == "high"),
                "fans" when mode is "auto" or "force" => await s.Amp.SetFansAsync(mode == "force"),
                "bypass" when mode is "on" or "off" => await s.Amp.SetBypassAsync(mode == "on"),
                _ => CommandResult.Invalid("Unknown or incomplete command"),
            };
            return Reply(ctx, result);
        });
    }

    private static IResult With(HttpContext ctx, Func<HfAmpService?> service, Func<HfAmpService, IResult> action)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var s = service();
        return s is null ? Unavailable() : action(s);
    }

    private static IResult Unavailable() =>
        Results.Json(new { ok = false, message = "The amplifier feature is not running" }, statusCode: 503);

    private static IResult Reply(HttpContext ctx, CommandResult result)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { ok = result.Ok, message = result.Message }, statusCode: result.Status);
    }

    private static IResult Refuse(int status, string message) =>
        Results.Json(new { ok = false, message }, statusCode: status);

    private static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext ctx, int maxBytes = MaxBodyBytes)
    {
        // Browsers label every request with Sec-Fetch-Site. The Zeus page may be served from a different
        // port than the API (same-site, not same-origin), so that is accepted; only a request that
        // originates from another site is refused. JSON-only bodies add a second layer: a cross-origin
        // JSON POST needs a CORS preflight, which this server never grants.
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin" && site != "same-site" && site != "none")
            return (default, Refuse(403, "Cross-site requests are refused"));

        if (!ctx.Request.HasJsonContentType())
            return (default, Refuse(415, "Content-Type must be application/json"));

        if (ctx.Request.ContentLength > maxBytes)
            return (default, Refuse(413, "Request body too large"));
        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = maxBytes;

        try
        {
            var value = await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted);
            return value is null ? (default, Refuse(400, "Empty request")) : (value, null);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException)
        {
            return (default, Refuse(400, "Malformed request"));
        }
    }
}
