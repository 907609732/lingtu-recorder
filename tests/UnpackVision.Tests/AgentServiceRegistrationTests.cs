using Microsoft.Extensions.DependencyInjection;
using UnpackVision.Core;
using UnpackVision.Infrastructure;
using UnpackVision.Service;

namespace UnpackVision.Tests;

public sealed class AgentServiceRegistrationTests
{
    [Fact]
    public void SyncDispatcherServices_ResolveRequiredMutationGate()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScanRecordRepository>(new InMemoryRepository());
        services.AddSingleton<ISyncConnector>(new NoopConnector());
        services.AddSingleton<IClock>(new FakeClock(DateTimeOffset.UtcNow));
        services.AddSyncDispatcherServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider.GetRequiredService<IRecordMutationGate>());
        Assert.NotNull(provider.GetRequiredService<SyncDispatcher>());
    }

    private sealed class NoopConnector : ISyncConnector
    {
        public string Id => "excel";
        public IReadOnlyList<string> ValidateConfiguration() => [];
        public Task<ConnectorHealth> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectorHealth(true, "ok"));
        public Task<ConnectorHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectorHealth(true, "ok"));
        public Task<SyncPushResult> PushRecordAsync(
            ScanRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SyncPushResult(null, "ok"));
    }
}
