using McpGateway.Connectors;
using McpGateway.Connectors.Fhir;
using Microsoft.Extensions.Logging;

namespace McpGateway.Registry;

/// <summary>
/// Maps registry entries to connector implementations.
///
/// Adding a backend means one entry in the factory map plus a class implementing
/// <see cref="Connector"/>. Nothing in the registry, dispatcher or MCP wiring changes.
/// </summary>
public sealed class ConnectorFactory : IConnectorFactory
{
    private readonly FhirRuntimeSettings _fhirSettings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpMessageHandler? _transport;

    private readonly Dictionary<string, Func<ConnectorEntry, Connector>> _factories;

    /// <summary>Creates the factory.</summary>
    /// <param name="fhirSettings">Credential and index settings for the FHIR connector.</param>
    /// <param name="loggerFactory">Logger factory handed to connectors.</param>
    /// <param name="transport">HTTP transport override; tests use it to supply a fake.</param>
    public ConnectorFactory(
        FhirRuntimeSettings fhirSettings,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? transport = null)
    {
        _fhirSettings = fhirSettings ?? throw new ArgumentNullException(nameof(fhirSettings));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _transport = transport;

        _factories = new Dictionary<string, Func<ConnectorEntry, Connector>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fhir"] = CreateFhirConnector,
        };
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedNames => _factories.Keys;

    /// <inheritdoc />
    public Connector Create(ConnectorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_factories.TryGetValue(entry.Name, out var factory))
        {
            throw new RegistryValidationException(
                $"Unknown connector '{entry.Name}'. Known connectors: {string.Join(", ", _factories.Keys)}.");
        }

        return factory(entry);
    }

    private FhirConnector CreateFhirConnector(ConnectorEntry entry) =>
        new(FhirConnectorOptions.FromEntry(entry, _fhirSettings), _loggerFactory, _transport);
}
