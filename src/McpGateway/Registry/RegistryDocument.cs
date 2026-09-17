namespace McpGateway.Registry;

/// <summary>The parsed contents of <c>registry.yaml</c>.</summary>
public sealed class RegistryDocument
{
    /// <summary>Backend connectors, enabled or otherwise.</summary>
    public List<ConnectorEntry> Connectors { get; set; } = [];

    /// <summary>Which caller identity may invoke which tools. Fail-closed: absent means no tools.</summary>
    public List<AuthzEntry> Authz { get; set; } = [];
}

/// <summary>One connector's configuration.</summary>
public sealed class ConnectorEntry
{
    /// <summary>Connector kind, e.g. <c>opensearch</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the gateway should instantiate and connect this connector.</summary>
    public bool Enabled { get; set; }

    /// <summary>Backend endpoint. Required by the FHIR connector.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Names of the connector's tools to expose. Tools not listed here stay unregistered.</summary>
    public List<string> Tools { get; set; } = [];
}

/// <summary>One caller's tool allowlist.</summary>
public sealed class AuthzEntry
{
    /// <summary>The caller's AAD application id or service principal object id.</summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>Tools this identity may invoke.</summary>
    public List<string> AllowedTools { get; set; } = [];
}
