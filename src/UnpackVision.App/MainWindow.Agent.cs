using UnpackVision.Application.Agent;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class MainWindow
{
    private readonly IRecordMutationGate _recordMutationGate = new RecordMutationGate();
    private AgentApiHost? _agentApiHost;
    private HistoryWindow? _historyWindow;
    private volatile RecordAutomationRuntime _agentRuntime = RecordAutomationRuntime.NotReady;
    private volatile IReadOnlyList<IssueTagDefinition> _agentTagCatalog = [];
    private DateTimeOffset? _agentInitializedAt;

    private async Task StartAgentApiAsync()
    {
        if (_repository is null || _agentApiHost is not null)
        {
            return;
        }

        _agentInitializedAt = DateTimeOffset.Now;
        UpdateAgentRuntime();
        UpdateAgentTagCatalog();
        var service = new RecordAutomationService(
            _repository,
            () => _agentTagCatalog,
            new SystemClock(),
            _recordMutationGate,
            () => _agentRuntime.CurrentRecordId);
        AgentApiHost? host = null;
        try
        {
            host = new AgentApiHost(
                service,
                () => _agentRuntime,
                recordChanged: NotifyAgentRecordChanged);
            await host.StartAsync(_lifetime.Token);
            _agentApiHost = host;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }
        catch (Exception)
        {
            // The agent boundary is optional. Camera, recording and Excel must
            // continue even when loopback binding or app-data writes fail.
            if (host is not null)
            {
                await host.DisposeAsync();
            }
            FooterText.Text = "智能体接口未启动，录像功能不受影响";
        }
    }

    private void UpdateAgentRuntime()
    {
        if (_repository is null || _agentInitializedAt is null)
        {
            return;
        }

        // This method only runs on the WPF thread. Publishing one immutable
        // snapshot avoids a background request observing a half-updated mix of
        // coordinator state and current-record fields.
        var current = _coordinator?.CurrentRecord;
        _agentRuntime = new RecordAutomationRuntime(
            DesktopReady: true,
            DatabaseReady: true,
            RecordingState: _coordinator?.State ?? RecordingState.Idle,
            CurrentRecordId: current?.Id,
            CurrentTrackingNo: current?.TrackingNo,
            AppVersion: ProductInfo.Version,
            InitializedAt: _agentInitializedAt);
    }

    private void UpdateAgentTagCatalog()
    {
        _agentTagCatalog = _settings.IssueTags
            .Select(tag => tag with { })
            .ToArray();
    }

    private void NotifyAgentRecordChanged()
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RefreshRecentAsync();
                if (_historyWindow is { IsLoaded: true } history)
                {
                    await history.RefreshFromExternalChangeAsync();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // The committed agent write already succeeded. A refresh
                // failure must not surface as an unhandled UI exception.
            }
        }));
    }

    private async Task StopAgentApiAsync()
    {
        var host = _agentApiHost;
        _agentApiHost = null;
        if (host is not null)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception)
            {
                // Agent shutdown is optional and bounded. A stale lock or
                // transport failure must never prevent the user from exiting.
            }
        }
    }
}
