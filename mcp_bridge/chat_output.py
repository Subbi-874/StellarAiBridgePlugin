"""Chat chunking, echo loop guard, and throttled outbound delivery (C#-portable)."""

import asyncio
from collections import deque
import logging
import re
import time
from typing import Any, Awaitable, Callable

logger = logging.getLogger("agy-bridge.chat-output")

MAX_CHAT_CHUNK_CHARS = 140
MAX_CHUNKS_PER_REPLY = 8


def split_chat_chunks(text: str, max_chars: int = MAX_CHAT_CHUNK_CHARS, max_chunks: int = MAX_CHUNKS_PER_REPLY) -> list[str]:
    """
    Cleans markdown artifacts for plain in-game chat and splits arbitrarily long Korean answers
    into natural <=140-character chunks along sentence/line/word boundaries.
    """
    if not text:
        return []

    cleaned = text.replace("\r\n", "\n").replace("\r", "\n")
    cleaned = re.sub(r"```[a-zA-Z0-9_-]*", "", cleaned)
    cleaned = cleaned.replace("`", "").replace("**", "").replace("__", "")
    lines = []
    for raw_ln in cleaned.split("\n"):
        ln = re.sub(r"^\s*#{1,6}\s+", "", raw_ln).strip()
        ln = re.sub(r"^\s*[-*•]\s+", "• ", ln)
        ln = re.sub(r"\s+", " ", ln).strip()
        if ln:
            lines.append(ln)

    if not lines:
        return []

    joined = " / ".join(lines)
    if len(joined) <= max_chars:
        return [joined]

    atoms: list[str] = []
    for ln in lines:
        parts = re.split(r"(?<=[.!?。])\s+", ln)
        for p in parts:
            p = p.strip()
            if not p:
                continue
            if len(p) <= max_chars:
                atoms.append(p)
            else:
                words = p.split(" ")
                cur_w = ""
                for w in words:
                    if not w:
                        continue
                    while len(w) > max_chars:
                        if cur_w:
                            atoms.append(cur_w)
                            cur_w = ""
                        atoms.append(w[:max_chars])
                        w = w[max_chars:]
                    cand = f"{cur_w} {w}".strip() if cur_w else w
                    if len(cand) <= max_chars:
                        cur_w = cand
                    else:
                        if cur_w:
                            atoms.append(cur_w)
                        cur_w = w
                if cur_w:
                    atoms.append(cur_w)

    chunks: list[str] = []
    current = ""
    for atom in atoms:
        if not current:
            current = atom
            continue
        sep = " " if current[-1] in ".!?。" else " / "
        candidate = f"{current}{sep}{atom}"
        if len(candidate) <= max_chars:
            current = candidate
        else:
            chunks.append(current)
            current = atom
            if len(chunks) >= max_chunks:
                break

    if current and len(chunks) < max_chunks:
        chunks.append(current[:max_chars])

    return chunks


class EchoGuard:
    """Tracks recent bot-sent lines so the bot does not reply to its own messages."""

    def __init__(self, maxlen: int = 80, clock: Callable[[], float] | None = None) -> None:
        self._recent: deque[tuple[float, str]] = deque(maxlen=maxlen)
        self._clock = clock

    def _now(self) -> float:
        return (self._clock or time.monotonic)()

    def record(self, text: str) -> None:
        norm = re.sub(r"\s+", " ", (text or "").strip())
        if norm:
            self._recent.append((self._now(), norm))

    def is_recent(self, text: str, ttl_sec: float = 20.0) -> bool:
        norm = re.sub(r"\s+", " ", (text or "").strip())
        if not norm:
            return False
        now = self._now()
        for ts, sent_norm in reversed(self._recent):
            if now - ts > ttl_sec:
                break
            if sent_norm == norm:
                return True
        return False


class ChatOutputSender:
    """Splits long replies, enforces send cooldown spacing, records echo guard, and delivers stickers."""

    def __init__(
        self,
        rpc_call: Callable[..., Awaitable[dict[str, Any]]],
        echo_guard: EchoGuard,
        sleep_fn: Callable[[float], Awaitable[None]] | None = None,
    ) -> None:
        self._dispatch = rpc_call
        self._echo = echo_guard
        self._sleep = sleep_fn

    async def _sleep_sec(self, sec: float) -> None:
        await (self._sleep or asyncio.sleep)(sec)

    async def send_notice(
        self,
        channel: str,
        text: str,
        target_char_id: int,
        conn_id: str | None = None,
    ) -> dict[str, Any]:
        self._echo.record(text)
        return await self._dispatch(
            "send_chat",
            {
                "channel": channel,
                "text": text,
                "target_char_id": int(target_char_id or 0),
            },
            conn_id=conn_id,
        )

    async def send_reply_and_sticker(
        self,
        channel: str,
        sender_id: int,
        reply_text: str,
        sticker_req: dict[str, Any] | None,
        conn_id: str | None = None,
    ) -> tuple[list[str], dict[str, Any] | None]:
        chunks = split_chat_chunks(reply_text, max_chars=MAX_CHAT_CHUNK_CHARS, max_chunks=MAX_CHUNKS_PER_REPLY)
        prev_cooldown = 0.0
        any_text_sent = False
        for idx, chunk in enumerate(chunks):
            if idx > 0:
                await self._sleep_sec(max(1.25, prev_cooldown + 0.25))
            send_res = await self.send_notice(channel, chunk, sender_id, conn_id=conn_id)
            if not send_res.get("ok"):
                logger.warning("Chunk %d/%d send_chat blocked/failed: %s", idx + 1, len(chunks), send_res)
                break
            any_text_sent = True
            prev_cooldown = float(send_res.get("remaining_cooldown_sec") or 0.0)

        sticker_sent_res: dict[str, Any] | None = None
        if sticker_req and (sticker_req.get("sticker") or sticker_req.get("sticker_id")):
            if any_text_sent:
                await self._sleep_sec(max(1.25, prev_cooldown + 0.25))
            s_params: dict[str, Any] = {
                "channel": channel,
                "sticker": str(sticker_req.get("sticker") or "random"),
                "target_char_id": int(sender_id or 0),
            }
            if int(sticker_req.get("sticker_id") or 0) > 0:
                s_params["sticker_id"] = int(sticker_req["sticker_id"])
            sticker_sent_res = await self._dispatch("send_sticker", s_params, conn_id=conn_id)
            if not sticker_sent_res.get("ok"):
                logger.warning("send_sticker blocked/failed: %s", sticker_sent_res)

        return chunks, sticker_sent_res
