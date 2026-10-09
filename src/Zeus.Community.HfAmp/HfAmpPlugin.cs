// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.AspNetCore.Routing;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Community.HfAmp;

/// <summary>
/// Thin shell between Zeus and <see cref="HfAmpService"/>: lifecycle and route
/// registration only. Everything else is SDK-free and unit tested.
/// </summary>
public sealed class HfAmpPlugin : IZeusPlugin, IBackendPlugin
{
    private HfAmpService? _service;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        var service = new HfAmpService(
            context.Logger,
            HostBridge.CreateSettingsStore(context),
            HostBridge.CreateRadioStateSource(context));
        await service.StartAsync(ct);
        _service = service;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var service = _service;
        _service = null;
        if (service is not null) await service.DisposeAsync();   // releases the amp to local control
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        HfAmpEndpoints.Map(endpoints, () => _service);
}
