using Microsoft.Extensions.DependencyInjection;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Service;

/// <summary>
/// Composition-root fragment shared by the compatibility service and its DI
/// resolve test. The service is a separate process from the desktop, so it has
/// its own mutation gate; the gate is still required in the container so that
/// SyncDispatcher's claim/read/push/complete sequence never runs unguarded.
/// </summary>
public static class SyncServiceRegistration
{
    public static IServiceCollection AddSyncDispatcherServices(this IServiceCollection services)
    {
        services.AddSingleton<IRecordMutationGate, RecordMutationGate>();
        services.AddSingleton<SyncDispatcher>();
        return services;
    }
}
