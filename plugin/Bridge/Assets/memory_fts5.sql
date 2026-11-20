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
