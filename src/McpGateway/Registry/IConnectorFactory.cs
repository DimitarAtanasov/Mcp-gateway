using McpGateway.Connectors;

namespace McpGateway.Registry;

/// <summary>
/// Instantiates connectors from registry entries. Abstracted so the registry can be built in
/// tests without reaching an Azure identity or a live backend.
/// </summary>
public interface IConnectorFactory
{
    /// <summary>Connector kinds this factory knows how to build.</summary>
    IReadOnlyCollection<string> SupportedNames { get; }

    /// <summary>Builds the connector described by <paramref name="entry"/>.</summary>
    /// <exception cref="RegistryValidationException">The entry is not valid for this connector kind.</exception>
    Connector Create(ConnectorEntry entry);
}
