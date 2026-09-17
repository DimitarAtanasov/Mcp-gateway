using McpGateway.Connectors.Fhir;
using McpGateway.Diagnostics;
using McpGateway.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpGateway.Hosting;

/// <summary>
/// Keeps the document index current by running incremental syncs on a timer.
///
/// Sync is deliberately not exposed as an MCP tool: pulling an archive is an operator concern,
/// and a model able to trigger it could hammer the FHIR server between turns.
/// </summary>
public sealed class DocumentSyncService : BackgroundService
{
    private readonly ToolRegistry _registry;
    private readonly ILogger<DocumentSyncService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the sync service.</summary>
    public DocumentSyncService(
        ToolRegistry registry,
        ILogger<DocumentSyncService> logger,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectors = _registry.Connectors.Values.OfType<FhirConnector>().ToArray();
        if (connectors.Length == 0)
            return;

        foreach (var connector in connectors.Where(connector => connector.SyncOnStartup))
            await RunSyncAsync(connector, stoppingToken).ConfigureAwait(false);

        var interval = connectors.Min(connector => connector.SyncInterval);
        using var timer = new PeriodicTimer(interval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var connector in connectors)
                    await RunSyncAsync(connector, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    private async Task RunSyncAsync(FhirConnector connector, CancellationToken cancellationToken)
    {
        try
        {
            await connector.Ingestor.SyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed sync must not take the gateway down: search keeps serving whatever is
            // already indexed, and the next tick retries.
            _logger.DocumentSyncFailed(ex, connector.Name);
        }
    }
}
