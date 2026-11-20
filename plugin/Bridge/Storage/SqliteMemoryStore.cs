using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;
using NativeSqliteStmt = Stellar.AgyBridge.Bridge.Storage.NativeSqliteDb.NativeSqliteStmt;

namespace Stellar.AgyBridge.Bridge.Storage;

/// <summary>
/// Storage contract for cross-bot user identities, per-bot durable memories/events,
/// and versioned game crafting catalogs (1:1 port of <c>mcp_bridge/memory_store.py</c>).
/// </summary>
public interface IMemoryStore : IDisposable
{
    UserRecord? ObserveUser(long uid, string name, string? observedAt = null);
    UserRecord? GetUser(long uid);
    List<UserRecord> ListUsers(string query = "", int limit = 50);

    (MemoryEntry? Entry, string Error) UpsertMemory(
        long botCharId,
        string category,
        string memKey,
        string content,
        string authorityTier,
        long sourceUid = 0,
        long subjectUid = 0);

    int DeleteMemory(
        long botCharId,
        long memoryId = 0,
        string memKey = "",
        string category = "",
        long? subjectUid = null);

    List<MemoryEntry> QueryMemories(
        long botCharId,
        string query = "",
        long? subjectUid = null,
        string category = "",
        bool includeNonCommander = true,
        int limit = 12);

    BotEventRecord? RecordEvent(
        long botCharId,
        string eventType,
        string summary,
        string outcome = "info",
        string authorityTier = "system",
        long relatedUid = 0);

    List<BotEventRecord> ListRecentEvents(long botCharId, int limit = 10);

    MemoryContextBundle BuildPromptContext(
        long botCharId,
        long senderUid = 0,
        string senderName = "",
        string triggerText = "",
        int maxMemories = 8,
        int maxEvents = 5);

    string GetCachedRecipeVersion();

    JsonObject ReplaceRecipeCatalog(
        string gameVersion,
        IReadOnlyList<JsonObject> recipes,
        IReadOnlyList<JsonObject> items);

    List<JsonObject> FindCachedRecipes(
        string query = "",
        int outputItemId = 0,
        int recipeId = 0,
        string recipeKind = "",
        int limit = 20);

    JsonObject? GetCachedItemObtainWay(int itemId);
    List<JsonObject> LoadAllCachedRecipes();
    Dictionary<int, JsonObject> LoadAllCachedObtainWays();
}

/// <summary>
/// Validation, sanitization, and keyword extraction helpers shared across storage and tools.
/// </summary>
public static class MemoryStoreHelpers
{
    private static readonly Regex SearchTokenRegex = new(@"[0-9A-Za-z가-힣]{2,}", RegexOptions.Compiled);
    private static readonly Regex ControlCharsRegex = new(@"[\x00-\x1f\x7f]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex RecipeNormRegex = new(@"[\W_\u200b\ufeff]+", RegexOptions.Compiled);

    public static string UtcNowIso()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00", CultureInfo.InvariantCulture);

    public static string SanitizeText(string? raw, int maxLen)
    {
        string cleaned = ControlCharsRegex.Replace(raw ?? "", " ");
        string collapsed = WhitespaceRegex.Replace(cleaned, " ").Trim();
        if (maxLen > 0 && collapsed.Length > maxLen)
            return collapsed[..maxLen].TrimEnd();
        return collapsed;
    }

    public static bool CanWriteMemory(string? authorityTier)
    {
        string norm = (authorityTier ?? "").Trim().ToLowerInvariant();
        return BridgeConstants.WritableAuthorityTiers.Contains(norm);
    }

    public static (bool Ok, string Error, string Category, string MemKey, string Content, string AuthorityTier)
        ValidateMemoryInput(string? category, string? memKey, string? content, string? authorityTier)
    {
        string normTier = (authorityTier ?? "").Trim().ToLowerInvariant();
        if (!BridgeConstants.AuthorityTiers.Contains(normTier) || !CanWriteMemory(normTier))
            return (false, "허용되지 않은 기억 권한 출처예요.", "", "", "", normTier);

        string normCat = (category ?? "").Trim().ToLowerInvariant();
        if (!BridgeConstants.AllowedMemoryCategories.Contains(normCat))
        {
            string allowed = string.Join(", ", BridgeConstants.AllowedMemoryCategories.OrderBy(x => x, StringComparer.Ordinal));
            return (false, $"허용되지 않은 기억 분류 '{category}'예요. (허용: {allowed})", "", "", "", normTier);
        }

        string normKey = SanitizeText(memKey, BridgeConstants.MaxMemKeyLen);
        string normContent = SanitizeText(content, BridgeConstants.MaxMemContentLen);
        if (string.IsNullOrEmpty(normKey))
            return (false, "기억 키(mem_key)가 비어 있어요.", "", "", "", normTier);
        if (string.IsNullOrEmpty(normContent))
            return (false, "기억 내용(content)이 비어 있어요.", "", "", "", normTier);

        string combinedLow = $"{normKey} {normContent}".ToLowerInvariant();
        foreach (string pat in BridgeConstants.ForbiddenContentPatterns)
        {
            if (combinedLow.Contains(pat, StringComparison.Ordinal))
                return (false, "보안 정책상 권한 변경이나 프롬프트 제어 문구는 기억에 저장할 수 없어요.", "", "", "", normTier);
        }

        return (true, "", normCat, normKey, normContent, normTier);
    }

    public static List<string> ExtractSearchKeywords(string? query, int maxTokens = 6)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (Match match in SearchTokenRegex.Matches(query ?? ""))
        {
            string tok = match.Value;
            if (!seen.Add(tok))
                continue;
            outList.Add(tok);
            if (outList.Count >= maxTokens)
                break;
        }
        return outList;
    }

    public static string EscapeLikePattern(string token)
        => token.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);

    internal static string NormalizeRecipeTokenString(string? text)
        => RecipeNormRegex.Replace((text ?? "").ToLowerInvariant(), "");

    internal static string[] SplitRecipeTokens(string? text)
        => RecipeNormRegex.Split((text ?? "").ToLowerInvariant())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();
}

/// <summary>
/// Thread-safe WAL SQLite implementation of <see cref="IMemoryStore"/> backed by <see cref="NativeSqliteDb"/>.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly object _lock = new();
    private readonly bool _isFileDb;
    private readonly Func<string> _clock;
    private NativeSqliteDb? _db;

    public string DbPath { get; }
    public int MaxMemoriesPerBot { get; }
    public int MaxEventsPerBot { get; }
    public bool Fts5Enabled { get; private set; }

    public SqliteMemoryStore(
        string dbPath = ":memory:",
        int maxMemoriesPerBot = 300,
        int maxEventsPerBot = 500,
        Func<string>? clock = null)
    {
        DbPath = string.IsNullOrWhiteSpace(dbPath) ? ":memory:" : dbPath;
        _isFileDb = !string.Equals(DbPath, ":memory:", StringComparison.Ordinal)
            && !DbPath.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase);
        MaxMemoriesPerBot = Math.Max(1, maxMemoriesPerBot);
        MaxEventsPerBot = Math.Max(1, maxEventsPerBot);
        _clock = clock ?? MemoryStoreHelpers.UtcNowIso;

        _db = new NativeSqliteDb(DbPath);
        InitDb();
    }

    private void InitDb()
    {
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            db.ExecuteScript("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
            if (_isFileDb)
                db.ExecuteScript("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");

            db.ExecuteScript(EmbeddedAssets.ReadAsset("memory_schema.sql"));

            using (NativeSqliteStmt stmt = db.Prepare(
                "INSERT INTO schema_meta(key, value) VALUES('schema_version', ?1) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value;"))
            {
                stmt.BindText(1, BridgeConstants.SchemaVersion.ToString(CultureInfo.InvariantCulture));
                stmt.StepDone();
            }

            try
            {
                db.ExecuteScript(EmbeddedAssets.ReadAsset("memory_fts5.sql"));
                Fts5Enabled = true;
            }
            catch
            {
                Fts5Enabled = false;
            }
        }
    }

    public UserRecord? ObserveUser(long uid, string name, string? observedAt = null)
    {
        string cleanName = MemoryStoreHelpers.SanitizeText(name, 64);
        if (uid <= 0 || string.IsNullOrEmpty(cleanName))
            return null;

        string nowTs = !string.IsNullOrEmpty(observedAt) ? observedAt : _clock();
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            db.RunInTransaction(() =>
            {
                using (NativeSqliteStmt s1 = db.Prepare(
                    "INSERT INTO users(uid, current_name, first_seen_at, last_seen_at) " +
                    "VALUES (?1, ?2, ?3, ?4) " +
                    "ON CONFLICT(uid) DO UPDATE SET " +
                    "    current_name = excluded.current_name, " +
                    "    last_seen_at = excluded.last_seen_at;"))
                {
                    s1.BindInt64(1, uid);
                    s1.BindText(2, cleanName);
                    s1.BindText(3, nowTs);
                    s1.BindText(4, nowTs);
                    s1.StepDone();
                }

                using (NativeSqliteStmt s2 = db.Prepare(
                    "INSERT INTO user_nicknames(uid, nickname, first_seen_at, last_seen_at) " +
                    "VALUES (?1, ?2, ?3, ?4) " +
                    "ON CONFLICT(uid, nickname) DO UPDATE SET " +
                    "    last_seen_at = excluded.last_seen_at;"))
                {
                    s2.BindInt64(1, uid);
                    s2.BindText(2, cleanName);
                    s2.BindText(3, nowTs);
                    s2.BindText(4, nowTs);
                    s2.StepDone();
                }
            });

            return GetUserLocked(db, uid);
        }
    }

    public UserRecord? GetUser(long uid)
    {
        if (uid <= 0)
            return null;
        lock (_lock)
        {
            return GetUserLocked(EnsureOpenDb(), uid);
        }
    }

    public List<UserRecord> ListUsers(string query = "", int limit = 50)
    {
        int lim = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        string qClean = MemoryStoreHelpers.SanitizeText(query, 64);
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            var rows = new List<(long Uid, string CurrentName, string FirstSeenAt, string LastSeenAt)>();

            if (string.IsNullOrEmpty(qClean))
            {
                using NativeSqliteStmt stmt = db.Prepare(
                    "SELECT uid, current_name, first_seen_at, last_seen_at " +
                    "FROM users ORDER BY last_seen_at DESC LIMIT ?1;");
                stmt.BindInt64(1, lim);
                while (stmt.StepRow())
                    rows.Add((stmt.GetInt64(0), stmt.GetText(1), stmt.GetText(2), stmt.GetText(3)));
            }
            else if (IsAsciiDigits(qClean) && long.TryParse(qClean, NumberStyles.None, CultureInfo.InvariantCulture, out long uidVal))
            {
                using NativeSqliteStmt stmt = db.Prepare(
                    "SELECT uid, current_name, first_seen_at, last_seen_at " +
                    "FROM users WHERE uid = ?1 LIMIT ?2;");
                stmt.BindInt64(1, uidVal);
                stmt.BindInt64(2, lim);
                while (stmt.StepRow())
                    rows.Add((stmt.GetInt64(0), stmt.GetText(1), stmt.GetText(2), stmt.GetText(3)));
            }
            else
            {
                string pat = $"%{MemoryStoreHelpers.EscapeLikePattern(qClean)}%";
                using NativeSqliteStmt stmt = db.Prepare(
                    "SELECT DISTINCT u.uid, u.current_name, u.first_seen_at, u.last_seen_at " +
                    "FROM users u " +
                    "LEFT JOIN user_nicknames n ON u.uid = n.uid " +
                    "WHERE u.current_name LIKE ?1 ESCAPE '\\' " +
                    "   OR n.nickname LIKE ?2 ESCAPE '\\' " +
                    "ORDER BY u.last_seen_at DESC LIMIT ?3;");
                stmt.BindText(1, pat);
                stmt.BindText(2, pat);
                stmt.BindInt64(3, lim);
                while (stmt.StepRow())
                    rows.Add((stmt.GetInt64(0), stmt.GetText(1), stmt.GetText(2), stmt.GetText(3)));
            }

            var result = new List<UserRecord>(rows.Count);
            foreach ((long rUid, string rName, string rFirst, string rLast) in rows)
            {
                result.Add(new UserRecord
                {
                    Uid = rUid,
                    CurrentName = rName,
                    FirstSeenAt = rFirst,
                    LastSeenAt = rLast,
                    NicknameHistory = LoadNicknamesLocked(db, rUid),
                });
            }
            return result;
        }
    }

    public (MemoryEntry? Entry, string Error) UpsertMemory(
        long botCharId,
        string category,
        string memKey,
        string content,
        string authorityTier,
        long sourceUid = 0,
        long subjectUid = 0)
    {
        if (botCharId <= 0)
            return (null, "봇 캐릭터 UID가 아직 확인되지 않아 기억을 저장할 수 없어요.");

        var (ok, errMsg, normCat, normKey, normContent, normTier) =
            MemoryStoreHelpers.ValidateMemoryInput(category, memKey, content, authorityTier);
        if (!ok)
            return (null, errMsg);

        long subj = Math.Max(0L, subjectUid);
        long src = Math.Max(0L, sourceUid);
        string nowTs = _clock();

        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            MemoryEntry? entry = null;

            db.RunInTransaction(() =>
            {
                using (NativeSqliteStmt ins = db.Prepare(
                    "INSERT INTO bot_memories (" +
                    "    bot_char_id, subject_uid, category, mem_key, content," +
                    "    authority_tier, source_uid, created_at, updated_at" +
                    ") VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9) " +
                    "ON CONFLICT(bot_char_id, subject_uid, category, mem_key) DO UPDATE SET " +
                    "    content = excluded.content, " +
                    "    authority_tier = excluded.authority_tier, " +
                    "    source_uid = excluded.source_uid, " +
                    "    updated_at = excluded.updated_at;"))
                {
                    ins.BindInt64(1, botCharId);
                    ins.BindInt64(2, subj);
                    ins.BindText(3, normCat);
                    ins.BindText(4, normKey);
                    ins.BindText(5, normContent);
                    ins.BindText(6, normTier);
                    ins.BindInt64(7, src);
                    ins.BindText(8, nowTs);
                    ins.BindText(9, nowTs);
                    ins.StepDone();
                }

                PruneBotMemoriesLocked(db, botCharId);

                using NativeSqliteStmt sel = db.Prepare(
                    "SELECT id, bot_char_id, subject_uid, category, mem_key, content, " +
                    "       authority_tier, source_uid, created_at, updated_at " +
                    "FROM bot_memories " +
                    "WHERE bot_char_id = ?1 AND subject_uid = ?2 AND category = ?3 AND mem_key = ?4;");
                sel.BindInt64(1, botCharId);
                sel.BindInt64(2, subj);
                sel.BindText(3, normCat);
                sel.BindText(4, normKey);
                if (sel.StepRow())
                    entry = ReadMemoryRow(sel);
            });

            return (entry, "");
        }
    }

    public int DeleteMemory(
        long botCharId,
        long memoryId = 0,
        string memKey = "",
        string category = "",
        long? subjectUid = null)
    {
        if (botCharId <= 0)
            return 0;

        string normKey = MemoryStoreHelpers.SanitizeText(memKey, BridgeConstants.MaxMemKeyLen);
        if (memoryId <= 0 && string.IsNullOrEmpty(normKey))
            return 0;

        var clauses = new List<string> { "bot_char_id = ?" };
        var args = new List<object?> { botCharId };

        if (memoryId > 0)
        {
            clauses.Add("id = ?");
            args.Add(memoryId);
        }
        if (!string.IsNullOrEmpty(normKey))
        {
            clauses.Add("mem_key = ?");
            args.Add(normKey);
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            clauses.Add("category = ?");
            args.Add(category.Trim().ToLowerInvariant());
        }
        if (subjectUid.HasValue)
        {
            clauses.Add("subject_uid = ?");
            args.Add(Math.Max(0L, subjectUid.Value));
        }

        string sql = $"DELETE FROM bot_memories WHERE {string.Join(" AND ", clauses)};";
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            int changes = 0;
            db.RunInTransaction(() =>
            {
                using NativeSqliteStmt stmt = db.Prepare(sql);
                BindParameters(stmt, args);
                stmt.StepDone();
                changes = db.Changes();
            });
            return changes;
        }
    }

    public List<MemoryEntry> QueryMemories(
        long botCharId,
        string query = "",
        long? subjectUid = null,
        string category = "",
        bool includeNonCommander = true,
        int limit = 12)
    {
        if (botCharId <= 0)
            return new List<MemoryEntry>();

        int lim = Math.Clamp(limit <= 0 ? 12 : limit, 1, 100);
        var clauses = new List<string> { "m.bot_char_id = ?" };
        var baseArgs = new List<object?> { botCharId };

        if (!includeNonCommander)
            clauses.Add("m.authority_tier IN ('commander', 'system')");
        if (subjectUid.HasValue)
        {
            clauses.Add("m.subject_uid = ?");
            baseArgs.Add(Math.Max(0L, subjectUid.Value));
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            clauses.Add("m.category = ?");
            baseArgs.Add(category.Trim().ToLowerInvariant());
        }

        string baseWhere = string.Join(" AND ", clauses);
        List<string> keywords = MemoryStoreHelpers.ExtractSearchKeywords(query);

        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            if (keywords.Count == 0)
            {
                var plainArgs = new List<object?>(baseArgs) { (long)lim };
                using NativeSqliteStmt stmt = db.Prepare(
                    $"SELECT {MemoryColumnsSql("m")} FROM bot_memories m WHERE {baseWhere} " +
                    "ORDER BY m.updated_at DESC, m.id DESC LIMIT ?;");
                BindParameters(stmt, plainArgs);
                var plainList = new List<MemoryEntry>();
                while (stmt.StepRow())
                    plainList.Add(ReadMemoryRow(stmt));
                return plainList;
            }

            var matchedById = new Dictionary<long, (int Score, MemoryEntry Entry)>();

            if (Fts5Enabled)
            {
                string ftsExpr = string.Join(" OR ", keywords.Select(tok => $"\"{tok}\"*"));
                var ftsArgs = new List<object?> { ftsExpr };
                ftsArgs.AddRange(baseArgs);
                ftsArgs.Add((long)(lim * 2));
                try
                {
                    using NativeSqliteStmt ftsStmt = db.Prepare(
                        $"SELECT {MemoryColumnsSql("m")} " +
                        "FROM bot_memories_fts f " +
                        "JOIN bot_memories m ON m.id = f.rowid " +
                        $"WHERE bot_memories_fts MATCH ? AND {baseWhere} " +
                        "ORDER BY bm25(bot_memories_fts), m.updated_at DESC " +
                        "LIMIT ?;");
                    BindParameters(ftsStmt, ftsArgs);
                    while (ftsStmt.StepRow())
                    {
                        MemoryEntry entry = ReadMemoryRow(ftsStmt);
                        matchedById[entry.MemoryId] = (2, entry);
                    }
                }
                catch
                {
                    // Fall back cleanly to LIKE matching when FTS5 query cannot execute
                }
            }

            var likeParts = new List<string>(keywords.Count);
            var likeArgs = new List<object?>(baseArgs);
            foreach (string tok in keywords)
            {
                string pat = $"%{MemoryStoreHelpers.EscapeLikePattern(tok)}%";
                likeParts.Add("(m.mem_key LIKE ? ESCAPE '\\' OR m.content LIKE ? ESCAPE '\\')");
                likeArgs.Add(pat);
                likeArgs.Add(pat);
            }
            likeArgs.Add((long)(lim * 2));

            string likeSql = $"SELECT {MemoryColumnsSql("m")} FROM bot_memories m " +
                $"WHERE {baseWhere} AND ({string.Join(" OR ", likeParts)}) " +
                "ORDER BY m.updated_at DESC, m.id DESC LIMIT ?;";
            using (NativeSqliteStmt likeStmt = db.Prepare(likeSql))
            {
                BindParameters(likeStmt, likeArgs);
                while (likeStmt.StepRow())
                {
                    MemoryEntry entry = ReadMemoryRow(likeStmt);
                    int nextScore = (matchedById.TryGetValue(entry.MemoryId, out var prev) ? prev.Score : 0) + 1;
                    matchedById[entry.MemoryId] = (nextScore, entry);
                }
            }

            return matchedById.Values
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.UpdatedAt, StringComparer.Ordinal)
                .ThenByDescending(x => x.Entry.MemoryId)
                .Take(lim)
                .Select(x => x.Entry)
                .ToList();
        }
    }

    public BotEventRecord? RecordEvent(
        long botCharId,
        string eventType,
        string summary,
        string outcome = "info",
        string authorityTier = "system",
        long relatedUid = 0)
    {
        string evType = MemoryStoreHelpers.SanitizeText(eventType, 48);
        string cleanSummary = MemoryStoreHelpers.SanitizeText(summary, BridgeConstants.MaxEventSummaryLen);
        if (botCharId <= 0 || string.IsNullOrEmpty(evType) || string.IsNullOrEmpty(cleanSummary))
            return null;

        string tier = (authorityTier ?? "").Trim().ToLowerInvariant();
        if (!BridgeConstants.AuthorityTiers.Contains(tier))
            tier = "system";
        string cleanOutcome = MemoryStoreHelpers.SanitizeText(string.IsNullOrEmpty(outcome) ? "info" : outcome, 24);
        if (string.IsNullOrEmpty(cleanOutcome))
            cleanOutcome = "info";
        string nowTs = _clock();
        long relUid = Math.Max(0L, relatedUid);

        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            BotEventRecord? record = null;
            db.RunInTransaction(() =>
            {
                using (NativeSqliteStmt ins = db.Prepare(
                    "INSERT INTO bot_events (" +
                    "    bot_char_id, event_type, summary, outcome, authority_tier, related_uid, created_at" +
                    ") VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7);"))
                {
                    ins.BindInt64(1, botCharId);
                    ins.BindText(2, evType);
                    ins.BindText(3, cleanSummary);
                    ins.BindText(4, cleanOutcome);
                    ins.BindText(5, tier);
                    ins.BindInt64(6, relUid);
                    ins.BindText(7, nowTs);
                    ins.StepDone();
                }

                long newId = db.LastInsertRowId();
                PruneBotEventsLocked(db, botCharId);

                using NativeSqliteStmt sel = db.Prepare(
                    "SELECT id, bot_char_id, event_type, summary, outcome, authority_tier, related_uid, created_at " +
                    "FROM bot_events WHERE id = ?1;");
                sel.BindInt64(1, newId);
                if (sel.StepRow())
                    record = ReadEventRow(sel);
            });
            return record;
        }
    }

    public List<BotEventRecord> ListRecentEvents(long botCharId, int limit = 10)
    {
        if (botCharId <= 0)
            return new List<BotEventRecord>();
        int lim = Math.Clamp(limit <= 0 ? 10 : limit, 1, 100);

        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            using NativeSqliteStmt stmt = db.Prepare(
                "SELECT id, bot_char_id, event_type, summary, outcome, authority_tier, related_uid, created_at " +
                "FROM bot_events WHERE bot_char_id = ?1 ORDER BY id DESC LIMIT ?2;");
            stmt.BindInt64(1, botCharId);
            stmt.BindInt64(2, lim);
            var list = new List<BotEventRecord>();
            while (stmt.StepRow())
                list.Add(ReadEventRow(stmt));
            return list;
        }
    }

    public MemoryContextBundle BuildPromptContext(
        long botCharId,
        long senderUid = 0,
        string senderName = "",
        string triggerText = "",
        int maxMemories = 8,
        int maxEvents = 5)
    {
        UserRecord? senderUser = senderUid > 0 ? GetUser(senderUid) : null;
        if (botCharId <= 0)
            return new MemoryContextBundle { SenderUser = senderUser };

        var picked = new List<MemoryEntry>();
        var seenIds = new HashSet<long>();

        void ExtendUnique(List<MemoryEntry> entries)
        {
            foreach (MemoryEntry item in entries)
            {
                if (seenIds.Contains(item.MemoryId))
                    continue;
                seenIds.Add(item.MemoryId);
                picked.Add(item);
                if (picked.Count >= maxMemories)
                    break;
            }
        }

        if (senderUid > 0 && picked.Count < maxMemories)
            ExtendUnique(QueryMemories(botCharId, subjectUid: senderUid, limit: Math.Min(4, maxMemories)));

        if (!string.IsNullOrEmpty(triggerText) && picked.Count < maxMemories)
        {
            string searchQ = $"{senderName} {triggerText}".Trim();
            ExtendUnique(QueryMemories(botCharId, query: searchQ, limit: Math.Min(4, maxMemories)));
        }

        if (picked.Count < maxMemories)
            ExtendUnique(QueryMemories(botCharId, subjectUid: 0, limit: maxMemories));

        List<BotEventRecord> recentEvents = maxEvents > 0
            ? ListRecentEvents(botCharId, limit: maxEvents)
            : new List<BotEventRecord>();

        return new MemoryContextBundle
        {
            SenderUser = senderUser,
            Memories = picked.Take(Math.Max(0, maxMemories)).ToList(),
            RecentEvents = recentEvents,
        };
    }

    public string GetCachedRecipeVersion()
    {
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            using NativeSqliteStmt stmt = db.Prepare(
                "SELECT value FROM recipe_cache_meta WHERE key = 'game_version';");
            return stmt.StepRow() ? stmt.GetText(0) : "";
        }
    }

    public JsonObject ReplaceRecipeCatalog(
        string gameVersion,
        IReadOnlyList<JsonObject> recipes,
        IReadOnlyList<JsonObject> items)
    {
        string ver = MemoryStoreHelpers.SanitizeText(gameVersion, 64);
        if (string.IsNullOrEmpty(ver))
            ver = "unknown";
        string nowTs = _clock();
        int recipesCached = 0;
        int itemsCached = 0;

        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            db.RunInTransaction(() =>
            {
                db.ExecuteScript("DELETE FROM recipes_cache; DELETE FROM item_obtain_ways_cache;");

                using (NativeSqliteStmt recStmt = db.Prepare(
                    "INSERT INTO recipes_cache (" +
                    "    recipe_id, game_version, recipe_kind, output_item_id," +
                    "    name, output_name, yield_per_craft, focus_cost, data_json" +
                    ") VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9);"))
                {
                    foreach (JsonObject rec in recipes)
                    {
                        long rid = JsonNodeHelpers.GetLong(rec, "recipe_id", 0);
                        if (rid <= 0)
                            continue;
                        string rKind = JsonNodeHelpers.GetString(rec, "recipe_kind", "");
                        if (string.IsNullOrEmpty(rKind))
                            rKind = "life_build";
                        long outId = JsonNodeHelpers.GetLong(rec, "output_item_id", 0);
                        string name = JsonNodeHelpers.GetString(rec, "name", "");
                        if (string.IsNullOrEmpty(name))
                            name = $"Recipe#{rid}";
                        string outName = JsonNodeHelpers.GetString(rec, "output_name", "");
                        if (string.IsNullOrEmpty(outName))
                            outName = JsonNodeHelpers.GetString(rec, "name", "");
                        long yieldVal = Math.Max(1L, JsonNodeHelpers.GetLong(rec, "yield_per_craft", 1));
                        long focusVal = Math.Max(0L, JsonNodeHelpers.GetLong(rec, "focus_cost", 0));

                        recStmt.Reset();
                        recStmt.BindInt64(1, rid);
                        recStmt.BindText(2, ver);
                        recStmt.BindText(3, rKind);
                        recStmt.BindInt64(4, outId);
                        recStmt.BindText(5, name);
                        recStmt.BindText(6, outName);
                        recStmt.BindInt64(7, yieldVal);
                        recStmt.BindInt64(8, focusVal);
                        recStmt.BindText(9, JsonNodeHelpers.ToCompactJson(rec));
                        recStmt.StepDone();
                        recipesCached++;
                    }
                }

                var seenItems = new HashSet<long>();
                using (NativeSqliteStmt itemStmt = db.Prepare(
                    "INSERT INTO item_obtain_ways_cache (" +
                    "    item_id, game_version, item_name, data_json" +
                    ") VALUES (?1, ?2, ?3, ?4);"))
                {
                    foreach (JsonObject it in items)
                    {
                        long iid = JsonNodeHelpers.GetLong(it, "item_id", 0);
                        if (iid <= 0 || !seenItems.Add(iid))
                            continue;
                        string itemName = JsonNodeHelpers.GetString(it, "item_name", "");
                        if (string.IsNullOrEmpty(itemName))
                            itemName = $"Item#{iid}";

                        itemStmt.Reset();
                        itemStmt.BindInt64(1, iid);
                        itemStmt.BindText(2, ver);
                        itemStmt.BindText(3, itemName);
                        itemStmt.BindText(4, JsonNodeHelpers.ToCompactJson(it));
                        itemStmt.StepDone();
                        itemsCached++;
                    }
                }

                UpsertRecipeMetaLocked(db, "game_version", ver);
                UpsertRecipeMetaLocked(db, "updated_at", nowTs);
            });
        }

        return new JsonObject
        {
            ["game_version"] = ver,
            ["recipes_cached"] = recipesCached,
            ["items_cached"] = itemsCached,
            ["updated_at"] = nowTs,
        };
    }

    public List<JsonObject> FindCachedRecipes(
        string query = "",
        int outputItemId = 0,
        int recipeId = 0,
        string recipeKind = "",
        int limit = 20)
    {
        int lim = Math.Clamp(limit <= 0 ? 20 : limit, 1, 200);
        var clauses = new List<string> { "1=1" };
        var args = new List<object?>();

        if (recipeId > 0)
        {
            clauses.Add("recipe_id = ?");
            args.Add((long)recipeId);
        }
        if (outputItemId > 0)
        {
            clauses.Add("output_item_id = ?");
            args.Add((long)outputItemId);
        }
        if (!string.IsNullOrWhiteSpace(recipeKind))
        {
            clauses.Add("recipe_kind = ?");
            args.Add(recipeKind.Trim());
        }

        string qClean = MemoryStoreHelpers.SanitizeText(query, 64);
        var baseClauses = new List<string>(clauses);
        var baseArgs = new List<object?>(args);

        if (!string.IsNullOrEmpty(qClean))
        {
            string pat = $"%{MemoryStoreHelpers.EscapeLikePattern(qClean)}%";
            clauses.Add("(name LIKE ? ESCAPE '\\' OR output_name LIKE ? ESCAPE '\\')");
            args.Add(pat);
            args.Add(pat);
        }
        args.Add((long)lim);

        string sql = $"SELECT data_json FROM recipes_cache WHERE {string.Join(" AND ", clauses)} " +
            "ORDER BY LENGTH(name) ASC, recipe_id ASC LIMIT ?;";

        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            var outList = new List<JsonObject>();
            var seenRids = new HashSet<int>();

            using (NativeSqliteStmt stmt = db.Prepare(sql))
            {
                BindParameters(stmt, args);
                while (stmt.StepRow())
                {
                    JsonObject? parsed = JsonNodeHelpers.ParseObject(stmt.GetText(0));
                    if (parsed == null)
                        continue;
                    int ridVal = JsonNodeHelpers.GetInt(parsed, "recipe_id", 0);
                    if (ridVal > 0)
                        seenRids.Add(ridVal);
                    outList.Add(parsed);
                }
            }

            if (!string.IsNullOrEmpty(qClean) && outList.Count < lim && recipeId <= 0)
            {
                string qNorm = MemoryStoreHelpers.NormalizeRecipeTokenString(qClean);
                string[] qTokens = MemoryStoreHelpers.SplitRecipeTokens(qClean);
                if (!string.IsNullOrEmpty(qNorm))
                {
                    string fbSql = "SELECT recipe_id, name, output_name, data_json FROM recipes_cache " +
                        $"WHERE {string.Join(" AND ", baseClauses)} ORDER BY LENGTH(name) ASC, recipe_id ASC;";
                    var scored = new List<(int Rank, int NormLen, int RecipeId, JsonObject Parsed)>();

                    using (NativeSqliteStmt fbStmt = db.Prepare(fbSql))
                    {
                        BindParameters(fbStmt, baseArgs);
                        while (fbStmt.StepRow())
                        {
                            int ridVal = (int)fbStmt.GetInt64(0);
                            if (seenRids.Contains(ridVal))
                                continue;
                            string nRaw = fbStmt.GetText(1);
                            string oRaw = fbStmt.GetText(2);
                            string nNorm = MemoryStoreHelpers.NormalizeRecipeTokenString(nRaw);
                            string oNorm = MemoryStoreHelpers.NormalizeRecipeTokenString(oRaw);

                            int rank;
                            if (nNorm == qNorm || oNorm == qNorm)
                                rank = 0;
                            else if (nNorm.Contains(qNorm, StringComparison.Ordinal)
                                || oNorm.Contains(qNorm, StringComparison.Ordinal))
                                rank = 1;
                            else if (qTokens.Length >= 2 && (qTokens.All(t => nNorm.Contains(t, StringComparison.Ordinal))
                                || qTokens.All(t => oNorm.Contains(t, StringComparison.Ordinal))))
                                rank = 2;
                            else
                                continue;

                            JsonObject? parsed = JsonNodeHelpers.ParseObject(fbStmt.GetText(3));
                            if (parsed != null)
                                scored.Add((rank, nNorm.Length > 0 ? nNorm.Length : nRaw.Length, ridVal, parsed));
                        }
                    }

                    foreach (var item in scored.OrderBy(x => x.Rank).ThenBy(x => x.NormLen).ThenBy(x => x.RecipeId))
                    {
                        if (outList.Count >= lim)
                            break;
                        outList.Add(item.Parsed);
                    }
                }
            }

            return outList;
        }
    }

    public JsonObject? GetCachedItemObtainWay(int itemId)
    {
        if (itemId <= 0)
            return null;
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            using NativeSqliteStmt stmt = db.Prepare(
                "SELECT data_json FROM item_obtain_ways_cache WHERE item_id = ?1;");
            stmt.BindInt64(1, itemId);
            return stmt.StepRow() ? JsonNodeHelpers.ParseObject(stmt.GetText(0)) : null;
        }
    }

    public List<JsonObject> LoadAllCachedRecipes()
    {
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            using NativeSqliteStmt stmt = db.Prepare(
                "SELECT data_json FROM recipes_cache ORDER BY recipe_id ASC;");
            var list = new List<JsonObject>();
            while (stmt.StepRow())
            {
                JsonObject? parsed = JsonNodeHelpers.ParseObject(stmt.GetText(0));
                if (parsed != null)
                    list.Add(parsed);
            }
            return list;
        }
    }

    public Dictionary<int, JsonObject> LoadAllCachedObtainWays()
    {
        lock (_lock)
        {
            NativeSqliteDb db = EnsureOpenDb();
            using NativeSqliteStmt stmt = db.Prepare(
                "SELECT item_id, data_json FROM item_obtain_ways_cache;");
            var map = new Dictionary<int, JsonObject>();
            while (stmt.StepRow())
            {
                int iid = (int)stmt.GetInt64(0);
                JsonObject? parsed = JsonNodeHelpers.ParseObject(stmt.GetText(1));
                if (parsed != null)
                    map[iid] = parsed;
            }
            return map;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_db != null)
            {
                _db.Dispose();
                _db = null;
            }
        }
    }

    private NativeSqliteDb EnsureOpenDb()
    {
        if (_db == null)
            throw new ObjectDisposedException(nameof(SqliteMemoryStore));
        return _db;
    }

    private UserRecord? GetUserLocked(NativeSqliteDb db, long uid)
    {
        using NativeSqliteStmt stmt = db.Prepare(
            "SELECT uid, current_name, first_seen_at, last_seen_at FROM users WHERE uid = ?1;");
        stmt.BindInt64(1, uid);
        if (!stmt.StepRow())
            return null;

        long rowUid = stmt.GetInt64(0);
        string curName = stmt.GetText(1);
        string firstSeen = stmt.GetText(2);
        string lastSeen = stmt.GetText(3);
        return new UserRecord
        {
            Uid = rowUid,
            CurrentName = curName,
            FirstSeenAt = firstSeen,
            LastSeenAt = lastSeen,
            NicknameHistory = LoadNicknamesLocked(db, rowUid),
        };
    }

    private List<string> LoadNicknamesLocked(NativeSqliteDb db, long uid)
    {
        using NativeSqliteStmt stmt = db.Prepare(
            "SELECT nickname FROM user_nicknames WHERE uid = ?1 ORDER BY first_seen_at ASC, last_seen_at ASC;");
        stmt.BindInt64(1, uid);
        var list = new List<string>();
        while (stmt.StepRow())
            list.Add(stmt.GetText(0));
        return list;
    }

    private void PruneBotMemoriesLocked(NativeSqliteDb db, long botCharId)
    {
        using NativeSqliteStmt stmt = db.Prepare(
            "DELETE FROM bot_memories " +
            "WHERE bot_char_id = ?1 " +
            "  AND id NOT IN (" +
            "      SELECT id FROM bot_memories " +
            "      WHERE bot_char_id = ?2 " +
            "      ORDER BY updated_at DESC, id DESC " +
            "      LIMIT ?3" +
            "  );");
        stmt.BindInt64(1, botCharId);
        stmt.BindInt64(2, botCharId);
        stmt.BindInt64(3, MaxMemoriesPerBot);
        stmt.StepDone();
    }

    private void PruneBotEventsLocked(NativeSqliteDb db, long botCharId)
    {
        using NativeSqliteStmt stmt = db.Prepare(
            "DELETE FROM bot_events " +
            "WHERE bot_char_id = ?1 " +
            "  AND id NOT IN (" +
            "      SELECT id FROM bot_events " +
            "      WHERE bot_char_id = ?2 " +
            "      ORDER BY id DESC " +
            "      LIMIT ?3" +
            "  );");
        stmt.BindInt64(1, botCharId);
        stmt.BindInt64(2, botCharId);
        stmt.BindInt64(3, MaxEventsPerBot);
        stmt.StepDone();
    }

    private static void UpsertRecipeMetaLocked(NativeSqliteDb db, string key, string value)
    {
        using NativeSqliteStmt stmt = db.Prepare(
            "INSERT INTO recipe_cache_meta(key, value) VALUES(?1, ?2) " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value;");
        stmt.BindText(1, key);
        stmt.BindText(2, value);
        stmt.StepDone();
    }

    private static string MemoryColumnsSql(string alias) =>
        $"{alias}.id, {alias}.bot_char_id, {alias}.subject_uid, {alias}.category, {alias}.mem_key, " +
        $"{alias}.content, {alias}.authority_tier, {alias}.source_uid, {alias}.created_at, {alias}.updated_at";

    private static MemoryEntry ReadMemoryRow(NativeSqliteStmt stmt) => new()
    {
        MemoryId = stmt.GetInt64(0),
        BotCharId = stmt.GetInt64(1),
        SubjectUid = stmt.GetInt64(2),
        Category = stmt.GetText(3),
        MemKey = stmt.GetText(4),
        Content = stmt.GetText(5),
        AuthorityTier = stmt.GetText(6),
        SourceUid = stmt.GetInt64(7),
        CreatedAt = stmt.GetText(8),
        UpdatedAt = stmt.GetText(9),
    };

    private static BotEventRecord ReadEventRow(NativeSqliteStmt stmt) => new()
    {
        EventId = stmt.GetInt64(0),
        BotCharId = stmt.GetInt64(1),
        EventType = stmt.GetText(2),
        Summary = stmt.GetText(3),
        Outcome = stmt.GetText(4),
        AuthorityTier = stmt.GetText(5),
        RelatedUid = stmt.GetInt64(6),
        CreatedAt = stmt.GetText(7),
    };

    private static void BindParameters(NativeSqliteStmt stmt, IReadOnlyList<object?> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            int paramIdx = i + 1;
            switch (args[i])
            {
                case null:
                    stmt.BindNull(paramIdx);
                    break;
                case long l:
                    stmt.BindInt64(paramIdx, l);
                    break;
                case int n:
                    stmt.BindInt64(paramIdx, n);
                    break;
                case string s:
                    stmt.BindText(paramIdx, s);
                    break;
                default:
                    stmt.BindText(paramIdx, Convert.ToString(args[i], CultureInfo.InvariantCulture));
                    break;
            }
        }
    }

    private static bool IsAsciiDigits(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }
}
