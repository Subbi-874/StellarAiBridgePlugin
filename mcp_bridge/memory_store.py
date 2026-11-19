"""SQLite persistent memory store with common users, per-bot partitions, and authority-tier guards (C#-portable)."""

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import re
import sqlite3
import threading
from typing import Any, Callable, Protocol


SCHEMA_VERSION = 1

AUTHORITY_TIERS: frozenset[str] = frozenset({"system", "commander", "non_commander"})
WRITABLE_AUTHORITY_TIERS: frozenset[str] = frozenset({"system", "commander"})
ALLOWED_MEMORY_CATEGORIES: frozenset[str] = frozenset(
    {"preference", "relationship", "nickname_alias", "habit", "note"}
)

MAX_MEM_KEY_LEN = 64
MAX_MEM_CONTENT_LEN = 240
MAX_EVENT_SUMMARY_LEN = 240

FORBIDDEN_CONTENT_PATTERNS: tuple[str, ...] = (
    "<persona",
    "</persona",
    "<system",
    "</system",
    "<action_results",
    "<current_trigger",
    "어드민락 해제",
    "admin_lock",
    "ignore previous",
    "system prompt",
    "명령권자 추가",
    "권한 부여",
)

_TIER_LABELS: dict[str, str] = {
    "commander": "명령권자",
    "system": "시스템",
    "non_commander": "비명령권자",
}

_SEARCH_TOKEN_RE = re.compile(r"[0-9A-Za-z가-힣]{2,}")
_CONTROL_CHARS_RE = re.compile(r"[\x00-\x1f\x7f]+")
_WHITESPACE_RE = re.compile(r"\s+")


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _local_hhmm(iso_ts: str) -> str:
    if not iso_ts:
        return ""
    try:
        dt = datetime.fromisoformat(iso_ts)
        if dt.tzinfo is not None:
            dt = dt.astimezone()
        return dt.strftime("%H:%M")
    except ValueError:
        return iso_ts[11:16] if len(iso_ts) >= 16 else ""


def sanitize_text(raw: str, max_len: int) -> str:
    """Strips control chars, collapses whitespace, and truncates safely."""
    cleaned = _CONTROL_CHARS_RE.sub(" ", str(raw or ""))
    collapsed = _WHITESPACE_RE.sub(" ", cleaned).strip()
    if max_len > 0 and len(collapsed) > max_len:
        return collapsed[:max_len].rstrip()
    return collapsed


def can_write_memory(authority_tier: str) -> bool:
    """Returns True only for privileged tiers allowed to persist durable memories."""
    return (authority_tier or "").strip().lower() in WRITABLE_AUTHORITY_TIERS


def validate_memory_input(
    category: str,
    mem_key: str,
    content: str,
    authority_tier: str,
) -> tuple[bool, str, tuple[str, str, str, str]]:
    """Validates category whitelist, authority tier, lengths, and prompt-injection patterns."""
    norm_tier = (authority_tier or "").strip().lower()
    if norm_tier not in AUTHORITY_TIERS or not can_write_memory(norm_tier):
        return (
            False,
            "명령권자 또는 시스템 출처만 기억을 저장할 수 있어요.",
            ("", "", "", norm_tier),
        )

    norm_cat = (category or "").strip().lower()
    if norm_cat not in ALLOWED_MEMORY_CATEGORIES:
        allowed = ", ".join(sorted(ALLOWED_MEMORY_CATEGORIES))
        return (
            False,
            f"허용되지 않은 기억 분류 '{category}'예요. (허용: {allowed})",
            ("", "", "", norm_tier),
        )

    norm_key = sanitize_text(mem_key, MAX_MEM_KEY_LEN)
    norm_content = sanitize_text(content, MAX_MEM_CONTENT_LEN)
    if not norm_key:
        return False, "기억 키(mem_key)가 비어 있어요.", ("", "", "", norm_tier)
    if not norm_content:
        return False, "기억 내용(content)이 비어 있어요.", ("", "", "", norm_tier)

    combined_low = f"{norm_key} {norm_content}".lower()
    for pat in FORBIDDEN_CONTENT_PATTERNS:
        if pat in combined_low:
            return (
                False,
                "보안 정책상 권한 변경이나 프롬프트 제어 문구는 기억에 저장할 수 없어요.",
                ("", "", "", norm_tier),
            )

    return True, "", (norm_cat, norm_key, norm_content, norm_tier)


def extract_search_keywords(query: str, max_tokens: int = 6) -> list[str]:
    """Extracts unique alphanumeric/Hangul search tokens from free-form text."""
    seen: set[str] = set()
    out: list[str] = []
    for tok in _SEARCH_TOKEN_RE.findall(query or ""):
        low = tok.lower()
        if low in seen:
            continue
        seen.add(low)
        out.append(tok)
        if len(out) >= max_tokens:
            break
    return out


def escape_like_pattern(token: str) -> str:
    return token.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


@dataclass(frozen=True)
class UserRecord:
    """Shared cross-bot identity record for a game player UID."""

    uid: int
    current_name: str
    first_seen_at: str
    last_seen_at: str
    nickname_history: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        return {
            "uid": self.uid,
            "current_name": self.current_name,
            "first_seen_at": self.first_seen_at,
            "last_seen_at": self.last_seen_at,
            "nickname_history": list(self.nickname_history),
        }


@dataclass(frozen=True)
class MemoryEntry:
    """Per-bot durable memory partitioned by `bot_char_id` with source privilege tier."""

    memory_id: int
    bot_char_id: int
    subject_uid: int
    category: str
    mem_key: str
    content: str
    authority_tier: str
    source_uid: int
    created_at: str
    updated_at: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "memory_id": self.memory_id,
            "bot_char_id": self.bot_char_id,
            "subject_uid": self.subject_uid,
            "category": self.category,
            "mem_key": self.mem_key,
            "content": self.content,
            "authority_tier": self.authority_tier,
            "source_uid": self.source_uid,
            "created_at": self.created_at,
            "updated_at": self.updated_at,
        }


@dataclass(frozen=True)
class BotEventRecord:
    """Per-bot recent action/goal outcome record partitioned by `bot_char_id`."""

    event_id: int
    bot_char_id: int
    event_type: str
    summary: str
    outcome: str
    authority_tier: str
    related_uid: int
    created_at: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "event_id": self.event_id,
            "bot_char_id": self.bot_char_id,
            "event_type": self.event_type,
            "summary": self.summary,
            "outcome": self.outcome,
            "authority_tier": self.authority_tier,
            "related_uid": self.related_uid,
            "created_at": self.created_at,
        }


@dataclass(frozen=True)
class MemoryContextBundle:
    """Prompt-ready memory and recent-event bundle scoped to a bot and trigger sender."""

    sender_user: UserRecord | None
    memories: tuple[MemoryEntry, ...]
    recent_events: tuple[BotEventRecord, ...]

    def to_prompt_text(self) -> str:
        lines: list[str] = []
        if self.sender_user is not None:
            prev_names = [
                n for n in self.sender_user.nickname_history if n and n != self.sender_user.current_name
            ]
            if prev_names:
                lines.append(
                    f"- 발신자 닉변 이력: 현재 {self.sender_user.current_name} "
                    f"(이전: {', '.join(prev_names[-5:])})"
                )

        for mem in self.memories:
            if mem.authority_tier == "non_commander":
                continue
            tier_label = _TIER_LABELS.get(mem.authority_tier, mem.authority_tier)
            subj = f" (대상UID:{mem.subject_uid})" if mem.subject_uid > 0 else ""
            lines.append(f"- [기억|{tier_label}|{mem.category}] {mem.mem_key}: {mem.content}{subj}")

        for ev in self.recent_events:
            hhmm = _local_hhmm(ev.created_at)
            time_part = f"{hhmm}|" if hhmm else ""
            lines.append(f"- [최근사건|{time_part}{ev.outcome}] {ev.summary}")

        return "\n".join(lines) if lines else "(저장된 기억 없음)"


class MemoryStore(Protocol):
    """Storage abstraction mirroring the future C# `IMemoryStore` interface."""

    def observe_user(self, uid: int, name: str, observed_at: str | None = None) -> UserRecord | None: ...

    def get_user(self, uid: int) -> UserRecord | None: ...

    def list_users(self, query: str = "", limit: int = 50) -> list[UserRecord]: ...

    def upsert_memory(
        self,
        bot_char_id: int,
        category: str,
        mem_key: str,
        content: str,
        *,
        authority_tier: str,
        source_uid: int = 0,
        subject_uid: int = 0,
    ) -> tuple[MemoryEntry | None, str]: ...

    def delete_memory(
        self,
        bot_char_id: int,
        *,
        memory_id: int = 0,
        mem_key: str = "",
        category: str = "",
        subject_uid: int | None = None,
    ) -> int: ...

    def query_memories(
        self,
        bot_char_id: int,
        *,
        query: str = "",
        subject_uid: int | None = None,
        category: str = "",
        include_non_commander: bool = False,
        limit: int = 12,
    ) -> list[MemoryEntry]: ...

    def record_event(
        self,
        bot_char_id: int,
        event_type: str,
        summary: str,
        *,
        outcome: str = "info",
        authority_tier: str = "system",
        related_uid: int = 0,
    ) -> BotEventRecord | None: ...

    def list_recent_events(self, bot_char_id: int, limit: int = 10) -> list[BotEventRecord]: ...

    def build_prompt_context(
        self,
        bot_char_id: int,
        *,
        sender_uid: int = 0,
        sender_name: str = "",
        trigger_text: str = "",
        max_memories: int = 8,
        max_events: int = 5,
    ) -> MemoryContextBundle: ...

    def close(self) -> None: ...


class SqliteMemoryStore:
    """
    Thread-safe WAL SQLite implementation of `MemoryStore` using stdlib `sqlite3` only.
    Stores common `users`/`user_nicknames`, per-bot `bot_memories`, and per-bot `bot_events`
    in a single SQLite database partitioned by `bot_char_id`.
    """

    def __init__(
        self,
        db_path: str | Path = ":memory:",
        *,
        max_memories_per_bot: int = 300,
        max_events_per_bot: int = 500,
        clock: Callable[[], str] | None = None,
    ) -> None:
        self.db_path = str(db_path)
        self._is_file_db = self.db_path != ":memory:" and not self.db_path.startswith("file::memory:")
        self.max_memories_per_bot = max(1, int(max_memories_per_bot))
        self.max_events_per_bot = max(1, int(max_events_per_bot))
        self._clock = clock or _utc_now_iso
        self._lock = threading.RLock()
        self.fts5_enabled = False

        if self._is_file_db:
            Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)

        self._conn = sqlite3.connect(
            self.db_path,
            timeout=5.0,
            check_same_thread=False,
        )
        self._conn.row_factory = sqlite3.Row
        self._init_db()

    def __enter__(self) -> "SqliteMemoryStore":
        return self

    def __exit__(self, _exc_type: Any, _exc: Any, _tb: Any) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:  # noqa: BLE001
            pass

    def close(self) -> None:
        with self._lock:
            if getattr(self, "_conn", None) is not None:
                self._conn.close()
                self._conn = None  # type: ignore[assignment]

    def _init_db(self) -> None:
        with self._lock:
            cur = self._conn.cursor()
            cur.execute("PRAGMA busy_timeout=5000;")
            cur.execute("PRAGMA foreign_keys=ON;")
            if self._is_file_db:
                cur.execute("PRAGMA journal_mode=WAL;")
                cur.execute("PRAGMA synchronous=NORMAL;")

            cur.executescript(
                """
                CREATE TABLE IF NOT EXISTS schema_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS users (
                    uid INTEGER PRIMARY KEY,
                    current_name TEXT NOT NULL,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS user_nicknames (
                    uid INTEGER NOT NULL,
                    nickname TEXT NOT NULL,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    PRIMARY KEY (uid, nickname),
                    FOREIGN KEY (uid) REFERENCES users(uid) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS bot_memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bot_char_id INTEGER NOT NULL,
                    subject_uid INTEGER NOT NULL DEFAULT 0,
                    category TEXT NOT NULL,
                    mem_key TEXT NOT NULL,
                    content TEXT NOT NULL,
                    authority_tier TEXT NOT NULL,
                    source_uid INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE (bot_char_id, subject_uid, category, mem_key)
                );

                CREATE INDEX IF NOT EXISTS idx_bot_memories_scope
                    ON bot_memories (bot_char_id, authority_tier, updated_at DESC);

                CREATE INDEX IF NOT EXISTS idx_bot_memories_subject
                    ON bot_memories (bot_char_id, subject_uid, updated_at DESC);

                CREATE TABLE IF NOT EXISTS bot_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bot_char_id INTEGER NOT NULL,
                    event_type TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    authority_tier TEXT NOT NULL,
                    related_uid INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_bot_events_scope
                    ON bot_events (bot_char_id, id DESC);
                """
            )
            cur.execute(
                "INSERT INTO schema_meta(key, value) VALUES('schema_version', ?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (str(SCHEMA_VERSION),),
            )
            self._init_fts5(cur)
            self._conn.commit()

    def _init_fts5(self, cur: sqlite3.Cursor) -> None:
        try:
            cur.executescript(
                """
                CREATE VIRTUAL TABLE IF NOT EXISTS bot_memories_fts USING fts5(
                    mem_key,
                    content,
                    content='bot_memories',
                    content_rowid='id',
                    tokenize='unicode61'
                );

                CREATE TRIGGER IF NOT EXISTS bot_memories_ai AFTER INSERT ON bot_memories BEGIN
                    INSERT INTO bot_memories_fts(rowid, mem_key, content)
                    VALUES (new.id, new.mem_key, new.content);
                END;

                CREATE TRIGGER IF NOT EXISTS bot_memories_ad AFTER DELETE ON bot_memories BEGIN
                    INSERT INTO bot_memories_fts(bot_memories_fts, rowid, mem_key, content)
                    VALUES ('delete', old.id, old.mem_key, old.content);
                END;

                CREATE TRIGGER IF NOT EXISTS bot_memories_au AFTER UPDATE ON bot_memories BEGIN
                    INSERT INTO bot_memories_fts(bot_memories_fts, rowid, mem_key, content)
                    VALUES ('delete', old.id, old.mem_key, old.content);
                    INSERT INTO bot_memories_fts(rowid, mem_key, content)
                    VALUES (new.id, new.mem_key, new.content);
                END;
                """
            )
            self.fts5_enabled = True
        except sqlite3.OperationalError:
            self.fts5_enabled = False

    @staticmethod
    def _row_to_memory(row: sqlite3.Row) -> MemoryEntry:
        return MemoryEntry(
            memory_id=int(row["id"]),
            bot_char_id=int(row["bot_char_id"]),
            subject_uid=int(row["subject_uid"]),
            category=str(row["category"]),
            mem_key=str(row["mem_key"]),
            content=str(row["content"]),
            authority_tier=str(row["authority_tier"]),
            source_uid=int(row["source_uid"]),
            created_at=str(row["created_at"]),
            updated_at=str(row["updated_at"]),
        )

    @staticmethod
    def _row_to_event(row: sqlite3.Row) -> BotEventRecord:
        return BotEventRecord(
            event_id=int(row["id"]),
            bot_char_id=int(row["bot_char_id"]),
            event_type=str(row["event_type"]),
            summary=str(row["summary"]),
            outcome=str(row["outcome"]),
            authority_tier=str(row["authority_tier"]),
            related_uid=int(row["related_uid"]),
            created_at=str(row["created_at"]),
        )

    def _load_nicknames_locked(self, uid: int) -> tuple[str, ...]:
        rows = self._conn.execute(
            "SELECT nickname FROM user_nicknames WHERE uid = ? ORDER BY first_seen_at ASC, last_seen_at ASC",
            (uid,),
        ).fetchall()
        return tuple(str(r["nickname"]) for r in rows)

    def observe_user(self, uid: int, name: str, observed_at: str | None = None) -> UserRecord | None:
        uid_int = int(uid or 0)
        clean_name = sanitize_text(name, 64)
        if uid_int <= 0 or not clean_name:
            return None

        now_ts = observed_at or self._clock()
        with self._lock:
            self._conn.execute(
                """
                INSERT INTO users(uid, current_name, first_seen_at, last_seen_at)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(uid) DO UPDATE SET
                    current_name = excluded.current_name,
                    last_seen_at = excluded.last_seen_at
                """,
                (uid_int, clean_name, now_ts, now_ts),
            )
            self._conn.execute(
                """
                INSERT INTO user_nicknames(uid, nickname, first_seen_at, last_seen_at)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(uid, nickname) DO UPDATE SET
                    last_seen_at = excluded.last_seen_at
                """,
                (uid_int, clean_name, now_ts, now_ts),
            )
            self._conn.commit()
            return self.get_user(uid_int)

    def get_user(self, uid: int) -> UserRecord | None:
        uid_int = int(uid or 0)
        if uid_int <= 0:
            return None
        with self._lock:
            row = self._conn.execute(
                "SELECT uid, current_name, first_seen_at, last_seen_at FROM users WHERE uid = ?",
                (uid_int,),
            ).fetchone()
            if row is None:
                return None
            history = self._load_nicknames_locked(uid_int)
            return UserRecord(
                uid=int(row["uid"]),
                current_name=str(row["current_name"]),
                first_seen_at=str(row["first_seen_at"]),
                last_seen_at=str(row["last_seen_at"]),
                nickname_history=history,
            )

    def list_users(self, query: str = "", limit: int = 50) -> list[UserRecord]:
        lim = max(1, min(200, int(limit or 50)))
        q_clean = sanitize_text(query, 64)
        with self._lock:
            if not q_clean:
                rows = self._conn.execute(
                    "SELECT uid, current_name, first_seen_at, last_seen_at "
                    "FROM users ORDER BY last_seen_at DESC LIMIT ?",
                    (lim,),
                ).fetchall()
            elif q_clean.isdigit():
                rows = self._conn.execute(
                    "SELECT uid, current_name, first_seen_at, last_seen_at "
                    "FROM users WHERE uid = ? LIMIT ?",
                    (int(q_clean), lim),
                ).fetchall()
            else:
                pat = f"%{escape_like_pattern(q_clean)}%"
                rows = self._conn.execute(
                    """
                    SELECT DISTINCT u.uid, u.current_name, u.first_seen_at, u.last_seen_at
                    FROM users u
                    LEFT JOIN user_nicknames n ON u.uid = n.uid
                    WHERE u.current_name LIKE ? ESCAPE '\\'
                       OR n.nickname LIKE ? ESCAPE '\\'
                    ORDER BY u.last_seen_at DESC
                    LIMIT ?
                    """,
                    (pat, pat, lim),
                ).fetchall()

            return [
                UserRecord(
                    uid=int(r["uid"]),
                    current_name=str(r["current_name"]),
                    first_seen_at=str(r["first_seen_at"]),
                    last_seen_at=str(r["last_seen_at"]),
                    nickname_history=self._load_nicknames_locked(int(r["uid"])),
                )
                for r in rows
            ]

    def _prune_bot_memories_locked(self, bot_char_id: int) -> None:
        self._conn.execute(
            """
            DELETE FROM bot_memories
            WHERE bot_char_id = ?
              AND id NOT IN (
                  SELECT id FROM bot_memories
                  WHERE bot_char_id = ?
                  ORDER BY updated_at DESC, id DESC
                  LIMIT ?
              )
            """,
            (bot_char_id, bot_char_id, self.max_memories_per_bot),
        )

    def _prune_bot_events_locked(self, bot_char_id: int) -> None:
        self._conn.execute(
            """
            DELETE FROM bot_events
            WHERE bot_char_id = ?
              AND id NOT IN (
                  SELECT id FROM bot_events
                  WHERE bot_char_id = ?
                  ORDER BY id DESC
                  LIMIT ?
              )
            """,
            (bot_char_id, bot_char_id, self.max_events_per_bot),
        )

    def upsert_memory(
        self,
        bot_char_id: int,
        category: str,
        mem_key: str,
        content: str,
        *,
        authority_tier: str,
        source_uid: int = 0,
        subject_uid: int = 0,
    ) -> tuple[MemoryEntry | None, str]:
        bot_cid = int(bot_char_id or 0)
        if bot_cid <= 0:
            return None, "봇 캐릭터 UID가 아직 확인되지 않아 기억을 저장할 수 없어요."

        ok, err_msg, (norm_cat, norm_key, norm_content, norm_tier) = validate_memory_input(
            category, mem_key, content, authority_tier
        )
        if not ok:
            return None, err_msg

        subj = max(0, int(subject_uid or 0))
        src = max(0, int(source_uid or 0))
        now_ts = self._clock()

        with self._lock:
            self._conn.execute(
                """
                INSERT INTO bot_memories (
                    bot_char_id, subject_uid, category, mem_key, content,
                    authority_tier, source_uid, created_at, updated_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(bot_char_id, subject_uid, category, mem_key) DO UPDATE SET
                    content = excluded.content,
                    authority_tier = excluded.authority_tier,
                    source_uid = excluded.source_uid,
                    updated_at = excluded.updated_at
                """,
                (bot_cid, subj, norm_cat, norm_key, norm_content, norm_tier, src, now_ts, now_ts),
            )
            self._prune_bot_memories_locked(bot_cid)
            row = self._conn.execute(
                """
                SELECT * FROM bot_memories
                WHERE bot_char_id = ? AND subject_uid = ? AND category = ? AND mem_key = ?
                """,
                (bot_cid, subj, norm_cat, norm_key),
            ).fetchone()
            self._conn.commit()
            return (self._row_to_memory(row) if row is not None else None), ""

    def delete_memory(
        self,
        bot_char_id: int,
        *,
        memory_id: int = 0,
        mem_key: str = "",
        category: str = "",
        subject_uid: int | None = None,
    ) -> int:
        bot_cid = int(bot_char_id or 0)
        if bot_cid <= 0:
            return 0

        mid = int(memory_id or 0)
        norm_key = sanitize_text(mem_key, MAX_MEM_KEY_LEN)
        if mid <= 0 and not norm_key:
            return 0

        clauses = ["bot_char_id = ?"]
        params: list[Any] = [bot_cid]
        if mid > 0:
            clauses.append("id = ?")
            params.append(mid)
        if norm_key:
            clauses.append("mem_key = ?")
            params.append(norm_key)
        if category and category.strip():
            clauses.append("category = ?")
            params.append(category.strip().lower())
        if subject_uid is not None:
            clauses.append("subject_uid = ?")
            params.append(max(0, int(subject_uid)))

        with self._lock:
            cur = self._conn.execute(f"DELETE FROM bot_memories WHERE {' AND '.join(clauses)}", params)
            self._conn.commit()
            return int(cur.rowcount or 0)

    def query_memories(
        self,
        bot_char_id: int,
        *,
        query: str = "",
        subject_uid: int | None = None,
        category: str = "",
        include_non_commander: bool = False,
        limit: int = 12,
    ) -> list[MemoryEntry]:
        bot_cid = int(bot_char_id or 0)
        if bot_cid <= 0:
            return []

        lim = max(1, min(100, int(limit or 12)))
        clauses = ["m.bot_char_id = ?"]
        params: list[Any] = [bot_cid]

        if not include_non_commander:
            clauses.append("m.authority_tier IN ('commander', 'system')")
        if subject_uid is not None:
            clauses.append("m.subject_uid = ?")
            params.append(max(0, int(subject_uid)))
        if category and category.strip():
            clauses.append("m.category = ?")
            params.append(category.strip().lower())

        base_where = " AND ".join(clauses)
        keywords = extract_search_keywords(query)

        with self._lock:
            if not keywords:
                rows = self._conn.execute(
                    f"SELECT m.* FROM bot_memories m WHERE {base_where} "
                    "ORDER BY m.updated_at DESC, m.id DESC LIMIT ?",
                    [*params, lim],
                ).fetchall()
                return [self._row_to_memory(r) for r in rows]

            matched_by_id: dict[int, tuple[int, MemoryEntry]] = {}

            if self.fts5_enabled:
                fts_expr = " OR ".join(f'"{tok}"*' for tok in keywords)
                try:
                    fts_rows = self._conn.execute(
                        f"""
                        SELECT m.*
                        FROM bot_memories_fts f
                        JOIN bot_memories m ON m.id = f.rowid
                        WHERE bot_memories_fts MATCH ? AND {base_where}
                        ORDER BY bm25(bot_memories_fts), m.updated_at DESC
                        LIMIT ?
                        """,
                        [fts_expr, *params, lim * 2],
                    ).fetchall()
                    for r in fts_rows:
                        entry = self._row_to_memory(r)
                        matched_by_id[entry.memory_id] = (2, entry)
                except sqlite3.OperationalError:
                    pass

            like_parts: list[str] = []
            like_params: list[Any] = []
            for tok in keywords:
                pat = f"%{escape_like_pattern(tok)}%"
                like_parts.append("(m.mem_key LIKE ? ESCAPE '\\' OR m.content LIKE ? ESCAPE '\\')")
                like_params.extend([pat, pat])
            like_sql = f"({' OR '.join(like_parts)})"
            like_rows = self._conn.execute(
                f"SELECT m.* FROM bot_memories m WHERE {base_where} AND {like_sql} "
                "ORDER BY m.updated_at DESC, m.id DESC LIMIT ?",
                [*params, *like_params, lim * 2],
            ).fetchall()
            for r in like_rows:
                entry = self._row_to_memory(r)
                prev = matched_by_id.get(entry.memory_id)
                score = (prev[0] if prev else 0) + 1
                matched_by_id[entry.memory_id] = (score, entry)

            ranked = sorted(
                matched_by_id.values(),
                key=lambda item: (item[0], item[1].updated_at, item[1].memory_id),
                reverse=True,
            )
            return [entry for _, entry in ranked[:lim]]

    def record_event(
        self,
        bot_char_id: int,
        event_type: str,
        summary: str,
        *,
        outcome: str = "info",
        authority_tier: str = "system",
        related_uid: int = 0,
    ) -> BotEventRecord | None:
        bot_cid = int(bot_char_id or 0)
        ev_type = sanitize_text(event_type, 48)
        clean_summary = sanitize_text(summary, MAX_EVENT_SUMMARY_LEN)
        if bot_cid <= 0 or not ev_type or not clean_summary:
            return None

        tier = authority_tier.strip().lower()
        if tier not in AUTHORITY_TIERS:
            tier = "system"
        clean_outcome = sanitize_text(outcome or "info", 24) or "info"
        now_ts = self._clock()

        with self._lock:
            cur = self._conn.execute(
                """
                INSERT INTO bot_events (
                    bot_char_id, event_type, summary, outcome, authority_tier, related_uid, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                (bot_cid, ev_type, clean_summary, clean_outcome, tier, max(0, int(related_uid or 0)), now_ts),
            )
            new_id = int(cur.lastrowid or 0)
            self._prune_bot_events_locked(bot_cid)
            row = self._conn.execute("SELECT * FROM bot_events WHERE id = ?", (new_id,)).fetchone()
            self._conn.commit()
            return self._row_to_event(row) if row is not None else None

    def list_recent_events(self, bot_char_id: int, limit: int = 10) -> list[BotEventRecord]:
        bot_cid = int(bot_char_id or 0)
        if bot_cid <= 0:
            return []
        lim = max(1, min(100, int(limit or 10)))
        with self._lock:
            rows = self._conn.execute(
                "SELECT * FROM bot_events WHERE bot_char_id = ? ORDER BY id DESC LIMIT ?",
                (bot_cid, lim),
            ).fetchall()
            return [self._row_to_event(r) for r in rows]

    def build_prompt_context(
        self,
        bot_char_id: int,
        *,
        sender_uid: int = 0,
        sender_name: str = "",
        trigger_text: str = "",
        max_memories: int = 8,
        max_events: int = 5,
    ) -> MemoryContextBundle:
        bot_cid = int(bot_char_id or 0)
        sender_user = self.get_user(sender_uid) if sender_uid > 0 else None
        if bot_cid <= 0:
            return MemoryContextBundle(sender_user=sender_user, memories=(), recent_events=())

        picked: list[MemoryEntry] = []
        seen_ids: set[int] = set()

        def _extend_unique(entries: list[MemoryEntry]) -> None:
            for item in entries:
                if item.memory_id in seen_ids or item.authority_tier == "non_commander":
                    continue
                seen_ids.add(item.memory_id)
                picked.append(item)
                if len(picked) >= max_memories:
                    break

        if sender_uid > 0 and len(picked) < max_memories:
            _extend_unique(
                self.query_memories(
                    bot_cid,
                    subject_uid=sender_uid,
                    limit=min(4, max_memories),
                )
            )

        if trigger_text and len(picked) < max_memories:
            search_q = f"{sender_name} {trigger_text}".strip()
            _extend_unique(
                self.query_memories(
                    bot_cid,
                    query=search_q,
                    limit=min(4, max_memories),
                )
            )

        if len(picked) < max_memories:
            _extend_unique(
                self.query_memories(
                    bot_cid,
                    subject_uid=0,
                    limit=max_memories,
                )
            )

        recent_events = tuple(self.list_recent_events(bot_cid, limit=max_events)) if max_events > 0 else ()
        return MemoryContextBundle(
            sender_user=sender_user,
            memories=tuple(picked[:max_memories]),
            recent_events=recent_events,
        )
