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

CREATE TABLE IF NOT EXISTS recipe_cache_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS recipes_cache (
    recipe_id INTEGER PRIMARY KEY,
    game_version TEXT NOT NULL,
    recipe_kind TEXT NOT NULL,
    output_item_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    output_name TEXT NOT NULL,
    yield_per_craft INTEGER NOT NULL,
    focus_cost INTEGER NOT NULL,
    data_json TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_recipes_cache_output
    ON recipes_cache (output_item_id);

CREATE INDEX IF NOT EXISTS idx_recipes_cache_name
    ON recipes_cache (name, output_name);

CREATE TABLE IF NOT EXISTS item_obtain_ways_cache (
    item_id INTEGER PRIMARY KEY,
    game_version TEXT NOT NULL,
    item_name TEXT NOT NULL,
    data_json TEXT NOT NULL
);
