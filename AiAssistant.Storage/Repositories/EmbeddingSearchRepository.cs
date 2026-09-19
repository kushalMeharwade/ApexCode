using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using System.Data.SQLite;
using System.Data;

namespace AiAssistant.Storage.Repositories;

public interface IEmbeddingSearchRepository
{
    Task InsertChunksAsync(IEnumerable<EmbeddingChunk> chunks, IEnumerable<float[]> embeddings, CancellationToken ct = default);
    Task DeleteChunksForFileAsync(string filePath, CancellationToken ct = default);
    Task<IEnumerable<EmbeddingSearchResult>> SearchAsync(float[] queryEmbedding, int limit = 20, CancellationToken ct = default);
    Task<int> GetTotalChunksAsync(CancellationToken ct = default);
    Task<int> GetTotalFilesAsync(CancellationToken ct = default);
    Task ClearIndexAsync(CancellationToken ct = default);
    Task<long?> GetLastIndexedTimestampAsync(string filePath, CancellationToken ct = default);
}

public class EmbeddingSearchRepository : IEmbeddingSearchRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    // One writer at a time. WAL allows concurrent reads during a held write lock, so
    // read methods (SearchAsync, GetTotal*, GetLastIndexed*) bypass this semaphore.
    // The semaphore is static so it applies across all instances: if DI ever creates
    // a second instance it still serialises through the same write gate.
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public EmbeddingSearchRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InsertChunksAsync(IEnumerable<EmbeddingChunk> chunks, IEnumerable<float[]> embeddings, CancellationToken ct = default)
    {
        await _writeSemaphore.WaitAsync(ct);
        try
        {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            var chunkInsertCmd = connection.CreateCommand();
            chunkInsertCmd.Transaction = transaction;
            chunkInsertCmd.CommandText = @"
                INSERT OR REPLACE INTO embedding_chunks (id, file_path, chunk_text, chunk_type, start_line, end_line, symbol_name, last_indexed)
                VALUES (@id, @file_path, @chunk_text, @chunk_type, @start_line, @end_line, @symbol_name, @last_indexed)";

            var idParam = chunkInsertCmd.Parameters.Add("@id", DbType.String);
            var filePathParam = chunkInsertCmd.Parameters.Add("@file_path", DbType.String);
            var chunkTextParam = chunkInsertCmd.Parameters.Add("@chunk_text", DbType.String);
            var chunkTypeParam = chunkInsertCmd.Parameters.Add("@chunk_type", DbType.String);
            var startLineParam = chunkInsertCmd.Parameters.Add("@start_line", DbType.Int64);
            var endLineParam = chunkInsertCmd.Parameters.Add("@end_line", DbType.Int64);
            var symbolNameParam = chunkInsertCmd.Parameters.Add("@symbol_name", DbType.String);
            var lastIndexedParam = chunkInsertCmd.Parameters.Add("@last_indexed", DbType.Int64);

            var vecInsertCmd = connection.CreateCommand();
            vecInsertCmd.Transaction = transaction;
            vecInsertCmd.CommandText = "INSERT INTO code_embeddings(rowid, embedding) VALUES (@rowid, @embedding)";
            var rowidParam = vecInsertCmd.Parameters.Add("@rowid", DbType.Int64);
            var embeddingParam = vecInsertCmd.Parameters.Add("@embedding", DbType.Binary);

            using var chunkEnum = chunks.GetEnumerator();
            using var embEnum = embeddings.GetEnumerator();

            while (chunkEnum.MoveNext() && embEnum.MoveNext())
            {
                var chunk = chunkEnum.Current;
                var emb = embEnum.Current;

                idParam.Value = chunk.Id;
                filePathParam.Value = chunk.FilePath;
                chunkTextParam.Value = chunk.ChunkText;
                chunkTypeParam.Value = chunk.ChunkType;
                startLineParam.Value = chunk.StartLine;
                endLineParam.Value = chunk.EndLine;
                symbolNameParam.Value = chunk.SymbolName ?? (object)DBNull.Value;
                lastIndexedParam.Value = chunk.LastIndexed;

                await chunkInsertCmd.ExecuteNonQueryAsync(ct);

                // Get rowid
                using var idCmd = connection.CreateCommand();
                idCmd.Transaction = transaction;
                idCmd.CommandText = "SELECT last_insert_rowid()";
                var rowid = (long)await idCmd.ExecuteScalarAsync(ct);

                // Serialize float[] to byte[] for sqlite-vec
                var byteSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<float, byte>(emb);
                rowidParam.Value = rowid;
                embeddingParam.Value = byteSpan.ToArray();

                await vecInsertCmd.ExecuteNonQueryAsync(ct);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        } // end using connection
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task DeleteChunksForFileAsync(string filePath, CancellationToken ct = default)
    {
        await _writeSemaphore.WaitAsync(ct);
        try
        {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            // First get the rowids
            var getIdsCmd = connection.CreateCommand();
            getIdsCmd.Transaction = transaction;
            getIdsCmd.CommandText = "SELECT rowid FROM embedding_chunks WHERE file_path = @file_path";
            getIdsCmd.Parameters.AddWithValue("@file_path", filePath);

            var rowids = new List<long>();
            using (var reader = await getIdsCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rowids.Add(reader.GetInt64(0));
                }
            }

            if (rowids.Count == 0) return;

            // Delete from chunks
            var delChunksCmd = connection.CreateCommand();
            delChunksCmd.Transaction = transaction;
            delChunksCmd.CommandText = "DELETE FROM embedding_chunks WHERE file_path = @file_path";
            delChunksCmd.Parameters.AddWithValue("@file_path", filePath);
            await delChunksCmd.ExecuteNonQueryAsync(ct);

            // Delete from vec (using parameterized query to prevent SQL injection)
            var delVecCmd = connection.CreateCommand();
            delVecCmd.Transaction = transaction;
            
            // Build parameterized query: DELETE FROM code_embeddings WHERE rowid IN (@p0, @p1, @p2, ...)
            var parameters = new List<string>();
            for (int i = 0; i < rowids.Count; i++)
            {
                var paramName = $"@p{i}";
                parameters.Add(paramName);
                delVecCmd.Parameters.AddWithValue(paramName, rowids[i]);
            }
            
            delVecCmd.CommandText = $"DELETE FROM code_embeddings WHERE rowid IN ({string.Join(",", parameters)})";
            await delVecCmd.ExecuteNonQueryAsync(ct);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        } // end using connection
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<IEnumerable<EmbeddingSearchResult>> SearchAsync(float[] queryEmbedding, int limit = 20, CancellationToken ct = default)
    {
        var results = new List<EmbeddingSearchResult>();
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync(ct);

        var byteSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<float, byte>(queryEmbedding);

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                c.id, c.file_path, c.chunk_text, c.chunk_type, c.start_line, c.end_line, c.symbol_name, c.last_indexed,
                v.distance
            FROM code_embeddings v
            JOIN embedding_chunks c ON c.rowid = v.rowid
            WHERE v.embedding MATCH @query
            AND k = @limit
            ORDER BY v.distance ASC";
            
        cmd.Parameters.AddWithValue("@query", byteSpan.ToArray());
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var distance = reader.GetFloat(8);
            if (distance > 0.7f) continue;

            results.Add(new EmbeddingSearchResult
            {
                Chunk = new EmbeddingChunk
                {
                    Id = reader.GetString(0),
                    FilePath = reader.GetString(1),
                    ChunkText = reader.GetString(2),
                    ChunkType = reader.GetString(3),
                    StartLine = reader.GetInt32(4),
                    EndLine = reader.GetInt32(5),
                    SymbolName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LastIndexed = reader.GetInt64(7)
                },
                Distance = distance
            });
        }

        return results;
    }

    public async Task<int> GetTotalChunksAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM embedding_chunks";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<int> GetTotalFilesAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT file_path) FROM embedding_chunks";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task ClearIndexAsync(CancellationToken ct = default)
    {
        await _writeSemaphore.WaitAsync(ct);
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM embedding_chunks; DELETE FROM code_embeddings;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<long?> GetLastIndexedTimestampAsync(string filePath, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(last_indexed) FROM embedding_chunks WHERE file_path = @file_path";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result == DBNull.Value || result == null) return null;
        return Convert.ToInt64(result);
    }
}
