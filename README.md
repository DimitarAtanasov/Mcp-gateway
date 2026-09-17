# MCP Gateway (FHIR clinical documents)

[![CI](https://github.com/DimitarAtanasov/Mcp-gateway/actions/workflows/ci.yml/badge.svg)](https://github.com/DimitarAtanasov/Mcp-gateway/actions/workflows/ci.yml)

An MCP server that gives Azure AI keyword search over the clinical documents in a FHIR server,
without ever handing the model a credential for that server.

- **Runtime:** .NET 8, official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- **Backend:** any FHIR R4/R4B/R5 server (HAPI and friends), static bearer or API-key auth
- **Tools:** `search_documents`, `get_document`
- **Search:** SQLite FTS5, BM25 ranking with highlighted snippets

## Why there is a local index

A FHIR server cannot full-text search inside an attachment. `DocumentReference.content.attachment`
is a base64 PDF or a `Binary` reference — bytes the server treats as opaque. Asking it for
"patients with elevated troponin" finds nothing, because the words are inside the document, not in
any indexed field.

So the gateway walks the DocumentReferences page by page, fetches each attachment, extracts its
text, and keeps that text in a local FTS5 index. Search runs against the index; the FHIR server
stays the system of record.

```
FHIR server ──DocumentReference pages──▶ Ingestor ──extract text──▶ SQLite FTS5 index
   (source of truth)                         │                            ▲
                                             └── incremental: only        │
                                                 _lastUpdated > watermark │
Azure AI ──MCP + AAD token──▶ Gateway ──authorize──▶ search_documents ────┘
```

## What it can and cannot read

| Attachment | Result |
|---|---|
| `application/pdf` with a text layer | Indexed |
| `text/plain`, `text/html`, `application/xhtml+xml` | Indexed |
| `application/rtf`, `text/rtf` | Indexed |
| **Scanned PDF (no text layer)** | **Not searchable** — recorded as `NoTextLayer` |
| `image/tiff`, `image/jpeg`, other binaries | **Not searchable** — recorded as `UnsupportedMediaType` |

**There is no OCR.** A scanned document is pixels; no amount of parsing recovers words from it.
Rather than indexing such a document as empty text — which would make it silently invisible to
search — the gateway records *why* it is unreadable and reports the totals. Every
`search_documents` response carries a `coverage` block:

```json
{
  "results": [ ... ],
  "coverage": { "searchable_documents": 8431, "unsearchable_documents": 2190 }
}
```

That distinction matters clinically: "no documents mention penicillin allergy" and "2,190 of your
documents are scans nobody can search" must not look the same to the model. If a large share of
your archive is scans, OCR is the next piece of work, not a tweak to this gateway.

## PHI, plainly

The index holds text extracted from clinical documents. **That is PHI, stored outside the FHIR
server**, and it is the main thing to get right when deploying this:

- Mount the index on an **encrypted volume**, or set `GATEWAY_INDEX_PATH=:memory:` to keep it out
  of storage entirely — the cost is a full re-sync on every restart.
- Search snippets and `get_document` return document text to the caller. That is the feature, but
  it means callers see PHI.
- **Authorization is per tool, not per patient.** Any identity allowed `search_documents` can
  search every indexed document. There is no row-level or compartment-level scoping. If different
  callers must see different patients, this gateway does not provide that today.
- Backend errors, credentials and document content never appear in responses to the caller;
  unexpected failures return a correlation id and the detail stays in the logs.

## Configuration

| Env var | Required when | Purpose |
|---|---|---|
| `GATEWAY_TRANSPORT` | always | `stdio` (local dev) or `streamable-http` (production) |
| `GATEWAY_REGISTRY_PATH` | always | Path to `registry.yaml` |
| `FHIR_CREDENTIAL` | unless auth scheme is `None` | Bearer token or API key for the FHIR server |
| `FHIR_AUTH_SCHEME` | optional | `Bearer` (default), `ApiKeyHeader`, or `None` |
| `FHIR_API_KEY_HEADER` | optional | Header for `ApiKeyHeader` (default `X-API-Key`) |
| `GATEWAY_INDEX_PATH` | optional | Index file, or `:memory:` (default `index.db`) |
| `GATEWAY_SYNC_INTERVAL` | optional | Sync cadence, e.g. `00:15:00` |
| `GATEWAY_SYNC_ON_STARTUP` | optional | Sync right after boot (default true) |
| `FHIR_PAGE_SIZE` | optional | DocumentReferences per page (default 50) |
| `FHIR_MAX_ATTACHMENT_BYTES` | optional | Attachment size cap (default 25 MB) |
| `AZURE_TENANT_ID` | http transport | AAD tenant validating *inbound* caller tokens |
| `GATEWAY_EXPECTED_AUDIENCE` | http transport | This gateway's app registration client id |

Two different credentials are in play: **inbound**, Azure AI proves who it is with an AAD token
this gateway validates; **outbound**, this gateway proves itself to the FHIR server with
`FHIR_CREDENTIAL`. The model never sees the second one.

### `registry.yaml`

```yaml
connectors:
  - name: fhir
    enabled: true
    endpoint: "https://your-fhir-server/fhir"   # https enforced unless localhost
    tools: [search_documents, get_document]

authz:
  - identity: "<caller's AAD app id or object id>"
    allowed_tools: [search_documents, get_document]
```

Validated at startup: an unknown connector, a tool no connector provides, a duplicate tool name or
an authz entry naming an unserved tool all fail the boot rather than degrading into a silently
missing tool.

## Sync

A background service syncs on a timer. It is incremental: the highest `meta.lastUpdated` seen
becomes the watermark, and the next run asks only for `_lastUpdated=gt{watermark}`. Documents whose
`versionId` is unchanged are skipped *before* their attachment is fetched, which is the expensive
part. A failed sync is logged and retried on the next tick; search keeps serving whatever is
already indexed.

Sync is deliberately **not** an MCP tool. Pulling an archive is an operator concern, and a model
able to trigger it could hammer the FHIR server between turns.

## Tools

`search_documents` — keywords, optional `top_k` (max 25) and `subject` filter. Returns ranked hits
with highlighted snippets, plus the `coverage` block. Caller keywords are sanitized into an FTS5
expression: every term is quoted, so `title:` column redirects and FTS operators in user input
cannot change the query's shape.

`get_document` — full extracted text for one id from a search hit, truncated at 20,000 characters
with `truncated: true` when it is longer.

## Endpoints (HTTP transport)

| Path | Auth | Purpose |
|---|---|---|
| `/mcp` | Bearer token required | MCP streamable HTTP endpoint |
| `/health/live` | anonymous | Liveness |
| `/health/ready` | anonymous | Readiness: the FHIR server answers |

## Running locally

```bash
dotnet run --project src/McpGateway        # stdio
```

Over stdio there is no caller identity, so every tool call is denied by design — stdio is useful
for checking that the gateway boots, connects and syncs, not for exercising the tools.

```bash
dotnet test                                # 178 tests, no network, no PHI
dotnet format --verify-no-changes
```

## Deploying

```bash
docker build -t mcp-gateway .
```

- Azure Container App or AKS pod, with a system-assigned Managed Identity for inbound token
  validation.
- **Mount an encrypted volume at `/var/lib/mcp-gateway`** for the index, or run with
  `GATEWAY_INDEX_PATH=:memory:`.
- Supply `FHIR_CREDENTIAL` from Key Vault, not from an image or a plain env var in source control.
- Reach the FHIR server over a private endpoint; probes go to `/health/live` and `/health/ready`.

## Extending

A new tool: add it under `Connectors/Fhir/Tools/`, return it from `FhirConnector.Tools()`, and list
its name in `registry.yaml`. A new backend: implement `Connector`, register it in
`ConnectorFactory`. `Program.cs`, the dispatcher and the registry are untouched either way.

Note that `Tools()` must be describable *before* `ConnectAsync` runs, because the registry
validates tool names at startup — bind backend resources lazily, as the FHIR tools do.

## Not yet done

- OCR for scanned documents (the largest functional gap).
- Per-patient authorization scoping.
- Deletion propagation: documents removed from the FHIR server stay in the index.
- Rate limiting per caller identity.

## License

MIT — see [LICENSE](LICENSE).
