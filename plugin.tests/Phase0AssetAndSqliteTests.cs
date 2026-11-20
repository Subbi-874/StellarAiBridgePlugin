using System;
using System.IO;
using System.Text.Json;
using Stellar.AgyBridge.Bridge.Storage;
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase0AssetAndSqliteTests
{
    [Fact]
    public void EmbeddedPromptsAndAssetsLoadCleanly()
    {
        string guidelines = EmbeddedAssets.ReadPrompt("common_guidelines.md");
        string rules = EmbeddedAssets.ReadPrompt("gemini_control_rules.md");
        string responseSchema = EmbeddedAssets.ReadPrompt("response_schema.json");
        Assert.False(string.IsNullOrWhiteSpace(guidelines));
        Assert.False(string.IsNullOrWhiteSpace(rules));
        Assert.Contains("reply_text", responseSchema);

        string toolsJson = EmbeddedAssets.ReadAsset("bot_tools.json");
        using JsonDocument toolsDoc = JsonDocument.Parse(toolsJson);
        Assert.Equal(JsonValueKind.Array, toolsDoc.RootElement.ValueKind);
        Assert.Equal(9, toolsDoc.RootElement.GetArrayLength());

        string schemaSql = EmbeddedAssets.ReadAsset("memory_schema.sql");
        string fts5Sql = EmbeddedAssets.ReadAsset("memory_fts5.sql");
        Assert.Contains("CREATE TABLE IF NOT EXISTS bot_memories", schemaSql);
        Assert.Contains("bot_memories_fts USING fts5", fts5Sql);
    }

    [Fact]
    public void NativeSqliteSupportsInMemoryAndWalWithFts5()
    {
        string schemaSql = EmbeddedAssets.ReadAsset("memory_schema.sql");
        string fts5Sql = EmbeddedAssets.ReadAsset("memory_fts5.sql");

        string version = NativeSqliteDb.LibVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(File.Exists(NativeSqliteDb.LoadedLibraryPath));

        using (var memDb = new NativeSqliteDb(":memory:"))
        {
            memDb.ExecuteScript(schemaSql);
            memDb.ExecuteScript(fts5Sql);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"agy_sqlite_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "memory.db");
        try
        {
            using (var fileDb = new NativeSqliteDb(dbPath))
            {
                fileDb.ExecuteScript("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
                fileDb.ExecuteScript(schemaSql);
                fileDb.ExecuteScript(fts5Sql);

                using (var ins = fileDb.Prepare(
                    "INSERT INTO bot_memories (bot_char_id, subject_uid, category, mem_key, content, authority_tier, source_uid, created_at, updated_at) " +
                    "VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9);"))
                {
                    ins.BindInt64(1, 1001);
                    ins.BindInt64(2, 2002);
                    ins.BindText(3, "preference");
                    ins.BindText(4, "favorite_fish");
                    ins.BindText(5, "유저는 별빛연어를 좋아하고 빈 문자열 처리도 확인한다");
                    ins.BindText(6, "commander");
                    ins.BindInt64(7, 2002);
                    ins.BindText(8, "2026-11-20T04:00:00Z");
                    ins.BindText(9, "2026-11-20T04:00:00Z");
                    ins.StepDone();
                }

                using (var q = fileDb.Prepare(
                    "SELECT m.mem_key, m.content FROM bot_memories_fts f " +
                    "JOIN bot_memories m ON m.id = f.rowid " +
                    "WHERE bot_memories_fts MATCH ?1 AND m.bot_char_id = ?2;"))
                {
                    q.BindText(1, "\"별빛연어\"*");
                    q.BindInt64(2, 1001);
                    Assert.True(q.StepRow());
                    Assert.Equal("favorite_fish", q.GetText(0));
                    Assert.Contains("별빛연어", q.GetText(1));
                    Assert.False(q.StepRow());
                }

                // Verify empty string binds cleanly as '' rather than NULL
                using (var empIns = fileDb.Prepare("INSERT INTO schema_meta (key, value) VALUES (?1, ?2);"))
                {
                    empIns.BindText(1, "empty_test");
                    empIns.BindText(2, "");
                    empIns.StepDone();
                }
                using (var empQ = fileDb.Prepare("SELECT value FROM schema_meta WHERE key = ?1;"))
                {
                    empQ.BindText(1, "empty_test");
                    Assert.True(empQ.StepRow());
                    Assert.False(empQ.IsNull(0));
                    Assert.Equal("", empQ.GetText(0));
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup
            }
        }
    }
}
