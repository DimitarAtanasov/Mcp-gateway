# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed — backend is now a FHIR server

The OpenSearch connector was removed; the gateway now serves keyword search over clinical
documents held in a FHIR server (HAPI or any R4/R4B/R5 server), authenticating with a static
bearer token or API key.

- `FhirConnector` walks DocumentReference pages, fetches each attachment, extracts its text and
  indexes it. Sync is incremental by `meta.lastUpdated`, and unchanged `versionId`s are skipped
  before the attachment is fetched.
- Text extraction for PDF (text layer), plain text, HTML/XHTML and RTF. **No OCR**: a scanned
  document is recorded as `NoTextLayer` rather than indexed as empty, so the gap is countable.
- SQLite FTS5 index with BM25 ranking and highlighted snippets. `GATEWAY_INDEX_PATH=:memory:`
  keeps extracted PHI out of storage.
- `search_documents` and `get_document` replace `vector_search`. Every search response carries a
  `coverage` block so "no matches" can be told apart from "most of the archive is unreadable".
- Caller keywords are sanitized into an FTS5 expression — every term quoted — so FTS operators and
  column redirects in user input cannot reshape the query.
- Background `DocumentSyncService` on a timer; sync is deliberately not exposed as an MCP tool.

### Fixed

- `SqliteDocumentIndex` gave every `:memory:` index a private database. SQLite's shared-cache
  `:memory:` is process-global, so two in-memory indexes would have silently shared one store.
- Connector tools are now describable before `ConnectAsync`, because the registry validates tool
  names at startup; binding them to a live index crashed the gateway on boot.

### Added

- JSON Schema validation of every tool's arguments before the handler runs.
- Registry validation at startup: unknown connectors, tools no connector provides, duplicate
  tool names and authz entries naming unserved tools now fail the boot.
- Authorization matches any of the caller's AAD identifiers (`appid`, `azp`, `oid`, `sub`), so
  the allowlist can be written against whichever one the operator has.
- Liveness (`/health/live`) and readiness (`/health/ready`) endpoints, served anonymously.
- Connector lifecycle hosted service: connectors connect before traffic is served and are shut
  down cleanly on stop.
- Unit test suite (137 tests) with fakes for the token provider, credential, OpenSearch
  transport and backend.
- CI (build, format check, tests with coverage, container build), CodeQL and Dependabot.
- Dockerfile: multi-stage, non-root, runtime image without the SDK.
- Source-generated logging, central package management, `.editorconfig`, analyzers with
  warnings as errors.

### Fixed

- **stdio transport corrupted its own protocol stream.** Logs were written to stdout, which is
  the JSON-RPC channel; they now go to stderr.
- **Connectors were never shut down.** `CloseAsync` had no caller, so the token refresh loop
  and HTTP handlers outlived the process's intent to exit.
- **A 404 from OpenSearch was treated as success**, turning a missing index into an empty
  result set instead of an error.
- Startup no longer proceeds when the HTTP transport is configured without a tenant id or
  audience.
- `http://` backend endpoints are rejected; bearer tokens must not travel in clear text.

### Changed

- Rewritten from Python to .NET 8 on the official C# MCP SDK.
- Caller token validation moved to `Microsoft.Identity.Web` instead of hand-rolled JWT parsing.
- OpenSearch requests are stamped with a token refreshed on a timer, rather than a header
  captured once at connect time.
- Unexpected tool failures return a correlation id; the detail stays in the logs.
