using System.Globalization;
using Microsoft.Data.Sqlite;

namespace McpGateway.Connectors.Fhir.Indexing;

/// <summary>
/// SQLite-backed document index using FTS5 for keyword search.
///
/// SQLite is chosen over a search server because the whole store is one file with no operational
/// surface, and FTS5 supplies BM25 ranking and match snippets natively. The metadata table is the
/// record of what exists; the FTS table carries only documents whose text could be extracted, so
/// an unreadable scan stays visible in the statistics without polluting search.
/// </summary>
public sealed class SqliteDocumentIndex : IDocumentIndex
{
    private const string WatermarkKey = "sync_watermark";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    // An in-memory database lives only as long as a connection is open to it, so one connection
    // is held for the index's lifetime. A file-backed index reuses it simply to serialize writes.
    private SqliteConnection? _connection;

    /// <summary>Creates an index over the database at <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">A file path, or <c>:memory:</c> to keep PHI out of storage.</param>
    public SqliteDocumentIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        IsInMemory = string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase);

        // SQLite treats every shared-cache ":memory:" connection as the SAME database for the
        // whole process, so two in-memory indexes would silently share one store. Naming the
        // database per instance keeps them isolated.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = IsInMemory
                ? $"mcp-gateway-{Guid.NewGuid():N}"
                : databasePath,
            Mode = IsInMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = IsInMemory ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();
    }

    /// <summary>Whether this index keeps document text in memory only.</summary>
    public bool IsInMemory { get; }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                return;

            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA journal_mode = WAL;

                    CREATE TABLE IF NOT EXISTS documents (
                        id                TEXT PRIMARY KEY,
                        version_id        TEXT,
                        last_updated      TEXT,
                        title             TEXT,
                        subject_reference TEXT,
                        type_code         TEXT,
                        type_display      TEXT,
                        created           TEXT,
                        content_type      TEXT,
                        status            TEXT NOT NULL,
                        status_detail     TEXT,
                        text_length       INTEGER NOT NULL DEFAULT 0,
                        indexed_at        TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_documents_subject ON documents(subject_reference);
                    CREATE INDEX IF NOT EXISTS idx_documents_status  ON documents(status);

                    CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
                        id UNINDEXED,
                        title,
                        body,
                        tokenize = 'porter unicode61'
                    );

                    CREATE TABLE IF NOT EXISTS sync_state (
                        key   TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    """;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _connection = connection;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public Task UpsertAsync(FhirDocument document, CancellationToken cancellationToken) =>
        UpsertManyAsync([document], cancellationToken);

    /// <inheritdoc />
    public async Task UpsertManyAsync(IReadOnlyCollection<FhirDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
            return;

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = RequireConnection();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var document in documents)
            {
                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = (SqliteTransaction)transaction;
                    delete.CommandText = "DELETE FROM documents_fts WHERE id = $id; DELETE FROM documents WHERE id = $id;";
                    delete.Parameters.AddWithValue("$id", document.Id);
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = (SqliteTransaction)transaction;
                    insert.CommandText = """
                        INSERT INTO documents (
                            id, version_id, last_updated, title, subject_reference,
                            type_code, type_display, created, content_type,
                            status, status_detail, text_length, indexed_at)
                        VALUES (
                            $id, $version, $lastUpdated, $title, $subject,
                            $typeCode, $typeDisplay, $created, $contentType,
                            $status, $statusDetail, $textLength, $indexedAt);
                        """;

                    insert.Parameters.AddWithValue("$id", document.Id);
                    insert.Parameters.AddWithValue("$version", (object?)document.VersionId ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$lastUpdated", ToStorage(document.LastUpdated));
                    insert.Parameters.AddWithValue("$title", (object?)document.Title ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$subject", (object?)document.SubjectReference ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$typeCode", (object?)document.TypeCode ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$typeDisplay", (object?)document.TypeDisplay ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$created", ToStorage(document.Created));
                    insert.Parameters.AddWithValue("$contentType", (object?)document.ContentType ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$status", document.Status.ToString());
                    insert.Parameters.AddWithValue("$statusDetail", (object?)document.StatusDetail ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$textLength", document.Text.Length);
                    insert.Parameters.AddWithValue("$indexedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Only searchable text enters the FTS table, so an unreadable scan cannot
                // masquerade as an empty but present document in search results.
                if (document.Status == ExtractionStatus.Extracted && document.Text.Length > 0)
                {
                    await using var indexText = connection.CreateCommand();
                    indexText.Transaction = (SqliteTransaction)transaction;
                    indexText.CommandText = "INSERT INTO documents_fts (id, title, body) VALUES ($id, $title, $body);";
                    indexText.Parameters.AddWithValue("$id", document.Id);
                    indexText.Parameters.AddWithValue("$title", (object?)document.Title ?? string.Empty);
                    indexText.Parameters.AddWithValue("$body", document.Text);
                    await indexText.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSearchHit>> SearchAsync(
        string query,
        int limit,
        string? subjectReference,
        CancellationToken cancellationToken)
    {
        var match = FtsQueryBuilder.Build(query);
        if (match is null)
            return [];

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = RequireConnection();
            await using var command = connection.CreateCommand();

            command.CommandText = """
                SELECT d.id,
                       d.title,
                       bm25(documents_fts) AS rank,
                       snippet(documents_fts, 2, '[', ']', ' ... ', 18) AS snippet,
                       d.subject_reference,
                       d.type_display,
                       d.created
                FROM documents_fts
                JOIN documents d ON d.id = documents_fts.id
                WHERE documents_fts MATCH $match
                  AND ($subject IS NULL OR d.subject_reference = $subject)
                ORDER BY rank
                LIMIT $limit;
                """;

            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$subject", (object?)subjectReference ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);

            var hits = new List<DocumentSearchHit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(new DocumentSearchHit
                {
                    Id = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? null : reader.GetString(1),

                    // BM25 returns lower-is-better negatives; flip it so "higher is better" holds
                    // for the model reading the result.
                    Score = Math.Round(-reader.GetDouble(2), 4),
                    Snippet = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SubjectReference = reader.IsDBNull(4) ? null : reader.GetString(4),
                    TypeDisplay = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Created = reader.IsDBNull(6) ? null : ParseStorage(reader.GetString(6)),
                });
            }

            return hits;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<FhirDocument?> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = RequireConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.id, d.version_id, d.last_updated, d.title, d.subject_reference,
                       d.type_code, d.type_display, d.created, d.content_type,
                       d.status, d.status_detail,
                       COALESCE((SELECT body FROM documents_fts WHERE documents_fts.id = d.id), '')
                FROM documents d
                WHERE d.id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            return new FhirDocument
            {
                Id = reader.GetString(0),
                VersionId = reader.IsDBNull(1) ? null : reader.GetString(1),
                LastUpdated = reader.IsDBNull(2) ? null : ParseStorage(reader.GetString(2)),
                Title = reader.IsDBNull(3) ? null : reader.GetString(3),
                SubjectReference = reader.IsDBNull(4) ? null : reader.GetString(4),
                TypeCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                TypeDisplay = reader.IsDBNull(6) ? null : reader.GetString(6),
                Created = reader.IsDBNull(7) ? null : ParseStorage(reader.GetString(7)),
                ContentType = reader.IsDBNull(8) ? null : reader.GetString(8),
                Status = Enum.Parse<ExtractionStatus>(reader.GetString(9)),
                StatusDetail = reader.IsDBNull(10) ? null : reader.GetString(10),
                Text = reader.GetString(11),
            };
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetVersionAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = RequireConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version_id FROM documents WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);

            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is DBNull or null ? null : (string)value;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = RequireConnection();

            var byStatus = new Dictionary<string, int>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT status, COUNT(*) FROM documents GROUP BY status;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    byStatus[reader.GetString(0)] = reader.GetInt32(1);
            }

            var extractedKey = ExtractionStatus.Extracted.ToString();
            var searchable = byStatus.GetValueOrDefault(extractedKey);

            return new IndexStatistics
            {
                TotalDocuments = byStatus.Values.Sum(),
                SearchableDocuments = searchable,
                UnsearchableByReason = byStatus
                    .Where(entry => !string.Equals(entry.Key, extractedKey, StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                Watermark = await ReadWatermarkAsync(connection, cancellationToken).ConfigureAwait(false),
            };
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadWatermarkAsync(RequireConnection(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetWatermarkAsync(DateTimeOffset watermark, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = RequireConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sync_state (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", WatermarkKey);
            command.Parameters.AddWithValue("$value", watermark.ToString("o", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _mutex.Dispose();
    }

    private static async Task<DateTimeOffset?> ReadWatermarkAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sync_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", WatermarkKey);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull or null ? null : ParseStorage((string)value);
    }

    private SqliteConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException(
            $"{nameof(SqliteDocumentIndex)} was used before {nameof(InitializeAsync)} completed.");

    private static object ToStorage(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseStorage(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
