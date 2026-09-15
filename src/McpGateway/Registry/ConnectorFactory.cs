using McpGateway.Auth;
using McpGateway.Connectors;
using McpGateway.Connectors.OpenSearch;
using Microsoft.Extensions.Logging;
using OpenSearch.Net;

namespace McpGateway.Registry;

/// <summary>
/// Maps registry entries to connector implementations.
///
/// Adding a backend means one entry in the factory map plus a class implementing
/// <see cref="Connector"/>. Nothing in the registry, dispatcher or MCP wiring changes.
/// </summary>
public sealed class ConnectorFactory : IConnectorFactory
{
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _openSearchScope;
    private readonly Func<IConnection?>? _innerConnectionFactory;

    private readonly Dictionary<string, Func<ConnectorEntry, Connector>> _factories;

    /// <summary>Creates the factory.</summary>
    /// <param name="tokenProvider">Source of AAD tokens for backends.</param>
    /// <param name="loggerFactory">Logger factory handed to connectors.</param>
    /// <param name="openSearchScope">AAD scope for the OpenSearch backend.</param>
    /// <param name="innerConnectionFactory">Transport override; tests use it to supply a fake.</param>
    public ConnectorFactory(
        IAccessTokenProvider tokenProvider,
        ILoggerFactory loggerFactory,
        string? openSearchScope = null,
        Func<IConnection?>? innerConnectionFactory = null)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _openSearchScope = string.IsNullOrWhiteSpace(openSearchScope)
            ? OpenSearchConnectorOptions.DefaultAadScope
            : openSearchScope;
        _innerConnectionFactory = innerConnectionFactory;

        _factories = new Dictionary<string, Func<ConnectorEntry, Connector>>(StringComparer.OrdinalIgnoreCase)
        {
            ["opensearch"] = CreateOpenSearchConnector,

            // ["redis"] = CreateRedisConnector,
            // ["sql"] = CreateSqlConnector,
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

    private OpenSearchConnector CreateOpenSearchConnector(ConnectorEntry entry) =>
        new OpenSearchConnector(
            OpenSearchConnectorOptions.FromEntry(entry, _openSearchScope),
            _tokenProvider,
            _loggerFactory,
            _innerConnectionFactory?.Invoke());
}
