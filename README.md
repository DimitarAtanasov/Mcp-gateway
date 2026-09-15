# MCP Gateway ("Wire Server")

[![CI](https://github.com/DimitarAtanasov/Mcp-gateway/actions/workflows/ci.yml/badge.svg)](https://github.com/DimitarAtanasov/Mcp-gateway/actions/workflows/ci.yml)

A single MCP server that Azure AI calls for data access. Internally it routes tool calls to
backend connectors (OpenSearch today; Redis/SQL later) so the AI never holds credentials or
connection strings to any backend directly.

- **Runtime:** .NET 8, official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- **Connectors:** OpenSearch
- **Tools:** `vector_search` (k-NN similarity search)
- **Auth:** Managed Identity on both legs (Azure AI → Gateway, Gateway → OpenSearch)

## How a call flows

```
Azure AI ──(1) MCP over HTTP, AAD bearer token──▶ Gateway
                                                   │ (2) JWT validated: signature, iss, aud, exp
                                                   │ (3) caller identity → tool allowlist
                                                   │ (4) arguments validated against JSON Schema
                                                   ▼
                                                 Connector ──(5) MI token──▶ OpenSearch
```

Every step fails closed. An unauthenticated caller resolves to the identity `unknown`, which
appears in no allowlist and therefore reaches no tool.

## Layout

```
src/McpGateway/
  Program.cs                          Host bootstrap and transport selection
  Configuration/                      Env-var mapping, options, startup validation
  Auth/
    ManagedIdentityTokenProvider.cs   Token cache, renewed ahead of expiry
    AadAuthConnection.cs              Per-request auth header for OpenSearch
    CallerIdentity.cs                 Caller identity resolved from AAD claims
  Connectors/
    Connector.cs                      Connector base type
    OpenSearch/                       Client lifecycle, options, tools
  Registry/                           registry.yaml loading, validation, connector factory
  Server/                             Tool dispatch and JSON Schema argument validation
  Hosting/                            Connector lifecycle and health checks
  Diagnostics/Log.cs                  Source-generated log messages
tests/McpGateway.Tests/               137 unit tests
```

## Configuration

| Env var | Required when | Purpose |
|---|---|---|
| `GATEWAY_TRANSPORT` | always | `stdio` (default, local dev) or `streamable-http` (production) |
| `GATEWAY_REGISTRY_PATH` | always | Path to `registry.yaml` (default: `registry.yaml`) |
| `AZURE_TENANT_ID` | http transport | AAD tenant that issues caller tokens |
| `GATEWAY_EXPECTED_AUDIENCE` | http transport | This gateway's app registration client id |
| `AZURE_AD_INSTANCE` | optional | Override for sovereign clouds |
| `GATEWAY_HOST` / `GATEWAY_PORT` | optional, http only | Bind address (default `0.0.0.0:8000`) |
| `GATEWAY_MCP_PATH` | optional, http only | MCP endpoint path (default `/mcp`) |
| `GATEWAY_OPENSEARCH_SCOPE` | optional | AAD scope for the OpenSearch backend |
| `AZURE_CLIENT_ID` | optional | Selects a user-assigned managed identity |

Startup validation refuses to run the HTTP transport without `AZURE_TENANT_ID` and
`GATEWAY_EXPECTED_AUDIENCE`, rather than silently serving an unauthenticated gateway.

### `registry.yaml`

```yaml
connectors:
  - name: opensearch
    enabled: true
    endpoint: "https://<your-opensearch-endpoint>"   # https is enforced
    allowed_indices: [product-docs-v1, kb-articles-v1]
    tools: [vector_search]

authz:
  - identity: "<caller's AAD app id or object id>"
    allowed_tools: [vector_search]
```

The registry is validated at startup: an unknown connector, a tool no connector provides, a
duplicate tool name, or an authz entry naming a tool nobody serves all fail the boot. A typo
never degrades into a silently missing tool.

## Endpoints (HTTP transport)

| Path | Auth | Purpose |
|---|---|---|
| `/mcp` | Bearer token required | MCP streamable HTTP endpoint |
| `/health/live` | anonymous | Liveness: the process is up |
| `/health/ready` | anonymous | Readiness: every connector's backend answers |

## Running locally

```bash
dotnet run --project src/McpGateway          # stdio, no auth to configure
```

Exercising the real auth path needs a tenant and an app registration:

```bash
export GATEWAY_TRANSPORT=streamable-http
export AZURE_TENANT_ID=<tenant-guid>
export GATEWAY_EXPECTED_AUDIENCE=<gateway-app-registration-client-id>
dotnet run --project src/McpGateway
```

> Under `stdio`, stdout carries the JSON-RPC stream, so all logging is routed to stderr.
> Writing anything else to stdout corrupts the protocol.

## Testing

```bash
dotnet test                      # 137 unit tests, no network required
dotnet format --verify-no-changes
```

Tests cover the parts that decide whether the gateway is safe: identity resolution from AAD
claims, the fail-closed authorization map, JSON Schema argument validation, index-allowlist
enforcement, query construction, result truncation, token renewal, and the error paths that
decide what reaches the model versus what stays in the logs.

## Deploying on Azure

```bash
docker build -t mcp-gateway .
```

- Run as an **Azure Container App** (or AKS pod) with a **system-assigned Managed Identity**.
- Grant that identity access to the OpenSearch endpoint, and to Key Vault if later connectors
  need secrets.
- Put the app in a **VNet** with a **Private Endpoint** to OpenSearch — no public IP on the
  backend, ever.
- Point the Container Apps health probes at `/health/live` and `/health/ready`.
- Expose the gateway to Azure AI over a private DNS name, or Private Link across VNets.

## Extending

**A new tool on an existing connector:**

1. Add `Connectors/OpenSearch/Tools/KeywordSearchTool.cs`, following `VectorSearchTool` —
   input schema, output schema, handler.
2. Return it from `OpenSearchConnector.Tools()`.
3. List its name under that connector's `tools:` in `registry.yaml`.

**A new connector:**

1. Add `Connectors/Redis/RedisConnector.cs` deriving from `Connector`.
2. Add its tools under `Connectors/Redis/Tools/`.
3. Register it in `ConnectorFactory`'s factory map.
4. Add its entry to `registry.yaml`.

`Program.cs`, the dispatcher and the registry are untouched in both cases — that is the point
of the plugin structure.

## Security notes

- Caller tokens are validated by `Microsoft.Identity.Web`: signature against the tenant's JWKS,
  issuer, audience and expiry. No hand-rolled JWT parsing.
- Authorization is fail-closed and matches the caller's `appid`, `azp`, `oid` or `sub`, so the
  allowlist can be written against whichever identifier the operator has.
- The index allowlist is enforced inside the tool, not just in the schema: the model cannot
  reach an index the connector was not configured for.
- Filter values go into structured `term` clauses and are schema-restricted to scalars; nothing
  is concatenated into a query string.
- Results are capped (20 hits, 2000 chars per string field) so one oversized document cannot
  flood the model's context.
- Backend diagnostics stay server-side. Unexpected failures return a correlation id to the
  caller and the detail goes to the logs.
- `http://` endpoints are rejected at startup; bearer tokens must never travel in clear text.

## Not yet done

- Rate limiting per caller identity.
- Redis and SQL connectors.
- Metrics and tracing export (OpenTelemetry).

## License

MIT — see [LICENSE](LICENSE).
