using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SaveEditor.Core.Formats;

/// <summary>
/// SQLite databases (used by many HTML5/Electron and mobile game saves).
/// Tables are exposed as JSON; on save, rows are rewritten into a copy of the
/// original file so schema, indexes and settings are preserved.
/// </summary>
public sealed class SqliteFormat : ISaveFormat
{
    public string Id => "sqlite";
    public string Name => "SQLite Veritabanı";
    public int Priority => 40;

    private const int MaxRowsPerTable = 100_000;

    public bool CanRead(byte[] data, string fileName)
        => data.Length >= 16 && Encoding.ASCII.GetString(data, 0, 15) == "SQLite format 3" && data[15] == 0;

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        string tempPath = WriteTemp(data);
        try
        {
            var tables = new JsonObject();
            var warnings = new List<string>();
            bool editable = true;

            using (var conn = Open(tempPath, readOnly: true))
            {
                var tableNames = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) tableNames.Add(reader.GetString(0));
                }

                foreach (string table in tableNames)
                {
                    var columns = new JsonArray();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read()) columns.Add(reader.GetString(1));
                    }

                    var rows = new JsonArray();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT * FROM {Quote(table)}";
                        using var reader = cmd.ExecuteReader();
                        int count = 0;
                        while (reader.Read())
                        {
                            if (++count > MaxRowsPerTable)
                            {
                                warnings.Add($"'{table}' tablosu {MaxRowsPerTable} satırdan büyük; dosya salt okunur açıldı.");
                                editable = false;
                                break;
                            }
                            var row = new JsonArray();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                row.Add(ReadValue(reader, i));
                            }
                            rows.Add(row);
                        }
                    }

                    tables[table] = new JsonObject { ["columns"] = columns, ["rows"] = rows };
                }
            }

            var doc = new SaveDocument
            {
                FormatId = Id,
                FormatName = Name,
                FileName = fileName,
                Root = new JsonObject { ["tables"] = tables },
                Editable = editable,
            };
            doc.State["original"] = Convert.ToBase64String(data);
            doc.Warnings.AddRange(warnings);
            return doc;
        }
        catch (SqliteException ex)
        {
            // CanRead only checks the 16-byte header; the body can still be
            // corrupt (truncated, overwritten, not actually a database).
            throw new SaveFormatException($"SQLite dosyası okunamadı: {ex.Message}", ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempPath);
        }
    }

    public byte[] Write(SaveDocument doc)
    {
        if (doc.State.TryGetValue("original", out object? o) is false || o is not string b64)
            throw new SaveFormatException("Orijinal SQLite verisi bulunamadı.");
        if (doc.Root?["tables"] is not JsonObject tables)
            throw new SaveFormatException("Geçersiz SQLite düzenleme verisi: 'tables' yok.");

        string tempPath = WriteTemp(Convert.FromBase64String(b64));
        try
        {
            using (var conn = Open(tempPath, readOnly: false))
            {
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA journal_mode=DELETE;";
                    pragma.ExecuteNonQuery();
                }
                using var tx = conn.BeginTransaction();
                foreach (var (table, node) in tables)
                {
                    if (node is not JsonObject tableObj) continue;
                    var columns = (tableObj["columns"] as JsonArray)?.Select(c => c!.GetValue<string>()).ToList()
                        ?? throw new SaveFormatException($"'{table}' tablosunda sütun listesi yok.");
                    var rows = tableObj["rows"] as JsonArray ?? [];

                    using (var del = conn.CreateCommand())
                    {
                        del.Transaction = tx;
                        del.CommandText = $"DELETE FROM {Quote(table)}";
                        del.ExecuteNonQuery();
                    }

                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(",", columns.Select(Quote))}) " +
                        $"VALUES ({string.Join(",", columns.Select((_, i) => "$p" + i))})";
                    var parameters = columns.Select((_, i) =>
                    {
                        var p = insert.CreateParameter();
                        p.ParameterName = "$p" + i;
                        insert.Parameters.Add(p);
                        return p;
                    }).ToArray();

                    foreach (var rowNode in rows)
                    {
                        if (rowNode is not JsonArray row || row.Count != columns.Count)
                            throw new SaveFormatException($"'{table}' tablosunda satır uzunluğu sütun sayısıyla eşleşmiyor.");
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            parameters[i].Value = ToSqliteValue(row[i]);
                        }
                        insert.ExecuteNonQuery();
                    }
                }
                tx.Commit();
                using (var vacuum = conn.CreateCommand())
                {
                    vacuum.CommandText = "VACUUM";
                    vacuum.ExecuteNonQuery();
                }
            }
            SqliteConnection.ClearAllPools();
            return File.ReadAllBytes(tempPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempPath);
        }
    }

    private static JsonNode? ReadValue(SqliteDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return null;
        return reader.GetFieldType(i) switch
        {
            var t when t == typeof(long) => JsonValue.Create(reader.GetInt64(i)),
            var t when t == typeof(double) => JsonValue.Create(reader.GetDouble(i)),
            var t when t == typeof(byte[]) => new JsonObject
            {
                ["__type"] = "bytes",
                ["b64"] = Convert.ToBase64String((byte[])reader.GetValue(i)),
            },
            _ => JsonValue.Create(reader.GetString(i)),
        };
    }

    private static object ToSqliteValue(JsonNode? node)
    {
        if (node is null) return DBNull.Value;
        if (node is JsonObject obj && obj["__type"]?.GetValue<string>() == "bytes")
            return Convert.FromBase64String(obj["b64"]?.GetValue<string>() ?? "");
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out long l)) return l;
            if (value.TryGetValue(out double d)) return d;
            if (value.TryGetValue(out bool b)) return b ? 1L : 0L;
            if (value.TryGetValue(out string? s)) return s!;
        }
        return node.ToJsonString();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static string WriteTemp(byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), "saveeditor-" + Guid.NewGuid().ToString("N") + ".sqlite");
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp cleanup is best effort */ }
    }
}
