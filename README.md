# MCP Gateway ("Wire Server")

A single MCP server that Azure AI calls for data access. Internally it
routes tool calls to backend connectors (OpenSearch today; Redis/SQL
later) so the AI never holds credentials or connection strings to any
backend directly.

## Status: v1
- One connector: **OpenSearch**
- One tool: **`vector_search`** (k-NN similarity search)
- Auth: Managed Identity on both legs (Azure AI → Gateway, Gateway → OpenSearch)
- Runtime: .NET 8, official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)

## Layout
```
Program.cs                          # Host bootstrap, transport selection, tool dispatch
Registry.cs                         # Loads registry.yaml, instantiates connectors, authz map
registry.yaml                       # Which connectors/tools are enabled + who may call them
Connectors/
  Connector.cs                      # Connector + ToolDefinition base types
  OpenSearch/
    OpenSearchConnector.cs          # OpenSearch client lifecycle, auth, health check
    Tools/
      VectorSearchTool.cs           # The one tool exposed today
Auth/
  ManagedIdentityTokenProvider.cs   # Shared Managed Identity token cache
  AadAuthConnection.cs              # Per-request OpenSearch auth header, refreshed on a timer
```

## Auth: wired
1. **Caller token validation** is handled by ASP.NET Core's JWT bearer
   authentication via `Microsoft.Identity.Web` (`AddMicrosoftIdentityWebApi`,
   wired in `Program.cs`). It fetches your AAD tenant's JWKS, and checks the
   token's signature, issuer, audience and expiry — no hand-rolled JWT
   parsing. Configure it with `AZURE_TENANT_ID` and `GATEWAY_EXPECTED_AUDIENCE`.
2. **Identity extraction** (`ExtractIdentity` in `Program.cs`) reads the
   already-validated `ClaimsPrincipal` off the MCP request context
   (`RequestContext<T>.User`, populated by the auth middleware above) and
   returns the caller's `appid`/`azp` claim. This only applies to the
   streamable-http transport; over stdio there's no bearer token to check,
   so identity stays `"unknown"` and every call fails the authz check in
   `Registry.cs`, by design.
3. **`registry.yaml`** still has a placeholder OpenSearch endpoint and a
   placeholder AAD app id under `authz` — replace both with your own.
4. **OpenSearch auth header refresh**: `AadAuthConnection` (a custom
   `OpenSearch.Net.HttpConnection`) stamps every outgoing request with the
   current cached token and a background loop refreshes that cache every 5
   minutes — well under an AAD access token's lifetime — so no request
   blocks on a token fetch.

## Configuration
| Env var | Required when | Purpose |
|---|---|---|
| `GATEWAY_REGISTRY_PATH` | always | Path to `registry.yaml` (default: `registry.yaml`) |
| `GATEWAY_TRANSPORT` | always | `stdio` (default, local dev) or `streamable-http` (production, behind APIM/Container Apps) |
| `AZURE_TENANT_ID` | `GATEWAY_TRANSPORT=streamable-http` | AAD tenant that issues caller tokens |
| `GATEWAY_EXPECTED_AUDIENCE` | `GATEWAY_TRANSPORT=streamable-http` | This gateway's own AAD app registration's Application (client) ID |
| `GATEWAY_HOST` / `GATEWAY_PORT` | optional, http only | Bind address (default `0.0.0.0:8000`) |

The HTTP transport serves the MCP endpoint at `/mcp`.

## Local run
```bash
dotnet run       # stdio transport, no auth to configure
```

To exercise the real auth path locally, run the http transport with a
real tenant/app registration:
```bash
export GATEWAY_TRANSPORT=streamable-http
export AZURE_TENANT_ID=<your-tenant-guid>
export GATEWAY_EXPECTED_AUDIENCE=<this-gateway's-app-registration-client-id>
dotnet run
```

## Deploying on Azure
- Run as an **Azure Container App** (or AKS pod) with a **system-assigned
  Managed Identity**.
- Grant that identity access to your OpenSearch endpoint (AAD-based
  access control) and, if you add connectors later, to Key Vault for
  any secrets those backends need.
- Put the Container App in a **VNet** with a **Private Endpoint** to
  OpenSearch — no public IP on the backend, ever.
- Expose the gateway to Azure AI via an internal/private DNS name
  (or Private Link if Azure AI lives in a different VNet).

## Extending

**Add a tool to an existing connector** (e.g. `keyword_search` to OpenSearch):
1. New file under `Connectors/OpenSearch/Tools/KeywordSearchTool.cs`
   following the `VectorSearchTool.cs` pattern (input/output schema + handler).
2. Register it in `OpenSearchConnector.Tools()`.
3. Add its name to `registry.yaml`'s `tools:` list.

**Add a new connector** (e.g. Redis):
1. New folder `Connectors/Redis/` implementing the `Connector` base class
   from `Connectors/Connector.cs` (`ConnectAsync`, `HealthCheckAsync`, `Tools`).
2. One or more `ToolDefinition`s under `Connectors/Redis/Tools/`.
3. Add `["redis"] = (config, tokenProvider) => new RedisConnector(config, tokenProvider)`
   to `ConnectorFactories` in `Registry.cs`.
4. Uncomment/add its entry in `registry.yaml`.

Nothing in `Program.cs` changes for either case — that's the point of
the plugin structure.
