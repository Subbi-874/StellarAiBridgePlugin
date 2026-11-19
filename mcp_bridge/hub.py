import asyncio
from collections import deque
from dataclasses import dataclass, field
import json
import logging
import time
import uuid
from typing import Any
from starlette.websockets import WebSocket, WebSocketDisconnect

logger = logging.getLogger("agy-bridge.hub")


@dataclass
class ClientConnection:
    conn_id: str
    ws: WebSocket
    connected_at: float
    hello_info: dict[str, Any] = field(default_factory=dict)


class GameBridgeHub:
    """
    Manages one or more simultaneous WebSocket connections from in-game Stellar.AgyBridge instances
    (e.g. E:\\ StarLauncher client and G:\\ Steam client running side-by-side without disconnecting each other).
    """

    def __init__(self, expected_token: str = "", event_capacity: int = 200) -> None:
        self._expected_token = expected_token
        self._clients: dict[str, ClientConnection] = {}
        self._active_conn_id: str | None = None
        self._seq: int = 0
        self._pending: dict[str, tuple[str, asyncio.Future[dict[str, Any]]]] = {}
        self._events: deque[dict[str, Any]] = deque(maxlen=event_capacity)
        self._event_listeners: list[Any] = []
        self._lock = asyncio.Lock()

    def add_event_listener(self, callback: Any) -> None:
        self._event_listeners.append(callback)

    @property
    def is_connected(self) -> bool:
        return len(self._clients) > 0

    def _get_active_client(self, preferred_conn_id: str | None = None) -> ClientConnection | None:
        if preferred_conn_id and preferred_conn_id in self._clients:
            return self._clients[preferred_conn_id]
        if self._active_conn_id and self._active_conn_id in self._clients:
            return self._clients[self._active_conn_id]
        if self._clients:
            first_id = next(iter(self._clients))
            self._active_conn_id = first_id
            return self._clients[first_id]
        return None

    def list_clients(self) -> list[dict[str, Any]]:
        out: list[dict[str, Any]] = []
        for cid, c in self._clients.items():
            out.append(
                {
                    "conn_id": cid,
                    "is_active": cid == self._active_conn_id,
                    "connected_at": c.connected_at,
                    "version": c.hello_info.get("version"),
                    "pid": c.hello_info.get("pid"),
                    "game_dir": c.hello_info.get("game_dir"),
                    "methods_count": len(c.hello_info.get("methods") or []),
                }
            )
        return out

    def select_active_client(self, selector: str) -> dict[str, Any]:
        q = (selector or "").strip().lower()
        if not q:
            return {"ok": False, "error": "Empty client selector"}
        for cid, c in self._clients.items():
            gdir = str(c.hello_info.get("game_dir") or "").lower()
            pid = str(c.hello_info.get("pid") or "")
            if cid.lower() == q or pid == q or (q in gdir and gdir):
                self._active_conn_id = cid
                return {"ok": True, "active_conn_id": cid, "client": c.hello_info}
        return {"ok": False, "error": f"No connected client matched '{selector}'", "clients": self.list_clients()}

    def client_supports(self, method: str, conn_id: str | None = None) -> bool:
        target = self._get_active_client(conn_id)
        if not target:
            return False
        methods = target.hello_info.get("methods") or []
        return method in methods

    def client_version_at_least(self, min_ver: tuple[int, ...], conn_id: str | None = None) -> bool:
        target = self._get_active_client(conn_id)
        if not target:
            return False
        raw = str(target.hello_info.get("version") or "").strip()
        if not raw:
            return False
        parts: list[int] = []
        for seg in raw.split("."):
            digits = "".join(ch for ch in seg if ch.isdigit())
            parts.append(int(digits) if digits else 0)
        return tuple(parts) >= min_ver

    def status_summary(self) -> dict[str, Any]:
        active = self._get_active_client()
        return {
            "connected": self.is_connected,
            "active_conn_id": active.conn_id if active else None,
            "connected_at": active.connected_at if active else None,
            "plugin_hello": active.hello_info if active else {},
            "clients": self.list_clients(),
            "buffered_events": len(self._events),
        }

    def get_events(self, limit: int = 50) -> list[dict[str, Any]]:
        items = list(self._events)
        return items[-max(1, limit) :]

    async def handle_websocket(self, websocket: WebSocket) -> None:
        if self._expected_token:
            auth = websocket.headers.get("authorization", "")
            if auth != f"Bearer {self._expected_token}":
                await websocket.close(code=4401, reason="Unauthorized")
                return

        await websocket.accept()
        async with self._lock:
            self._seq += 1
            conn_id = f"client-{self._seq}"
            conn = ClientConnection(conn_id=conn_id, ws=websocket, connected_at=time.time())
            self._clients[conn_id] = conn
            if self._active_conn_id is None or self._active_conn_id not in self._clients:
                self._active_conn_id = conn_id

        logger.info("In-game plugin connected [%s] from %s (total=%d)", conn_id, websocket.client, len(self._clients))

        try:
            while True:
                raw = await websocket.receive_text()
                try:
                    msg = json.loads(raw)
                except json.JSONDecodeError:
                    continue

                msg_type = msg.get("type")
                if msg_type == "hello":
                    conn.hello_info = msg
                    new_pid = msg.get("pid")
                    # Close stale duplicate socket only if it belongs to the exact same OS PID
                    if new_pid:
                        for other_id, other_c in list(self._clients.items()):
                            if other_id != conn_id and other_c.hello_info.get("pid") == new_pid:
                                try:
                                    await other_c.ws.close(code=1012, reason="Same PID reconnected")
                                except Exception:
                                    pass
                                self._clients.pop(other_id, None)
                                if self._active_conn_id == other_id:
                                    self._active_conn_id = conn_id

                    logger.info("Handshake received [%s]: %s", conn_id, msg)
                    if isinstance(msg.get("chat_settings"), dict):
                        synth_evt = {
                            "type": "event",
                            "conn_id": conn_id,
                            "event": "ui_chat_settings_changed",
                            "data": msg["chat_settings"],
                            "received_at": time.time(),
                        }
                        for listener in list(self._event_listeners):
                            try:
                                res = listener(synth_evt)
                                if asyncio.iscoroutine(res):
                                    asyncio.create_task(res)
                            except Exception as cb_exc:
                                logger.warning("Hello listener error: %s", cb_exc)

                    if isinstance(msg.get("capabilities"), dict):
                        cap_evt = {
                            "type": "event",
                            "conn_id": conn_id,
                            "event": "capabilities_updated",
                            "data": msg["capabilities"],
                            "received_at": time.time(),
                        }
                        for listener in list(self._event_listeners):
                            try:
                                res = listener(cap_evt)
                                if asyncio.iscoroutine(res):
                                    asyncio.create_task(res)
                            except Exception as cb_exc:
                                logger.warning("Hello capabilities listener error: %s", cb_exc)

                elif msg_type == "response":
                    req_id = str(msg.get("id", ""))
                    entry = self._pending.pop(req_id, None)
                    if entry is not None:
                        _, fut = entry
                        if not fut.done():
                            fut.set_result(msg.get("result", {"ok": False, "error": "Empty result"}))

                elif msg_type == "event":
                    msg["conn_id"] = conn_id
                    msg["received_at"] = time.time()
                    self._events.append(msg)
                    for listener in list(self._event_listeners):
                        try:
                            res = listener(msg)
                            if asyncio.iscoroutine(res):
                                asyncio.create_task(res)
                        except Exception as cb_exc:
                            logger.warning("Event listener error: %s", cb_exc)
        except WebSocketDisconnect:
            logger.info("In-game plugin disconnected [%s].", conn_id)
        except Exception as exc:
            logger.warning("WebSocket error [%s]: %s", conn_id, exc)
        finally:
            async with self._lock:
                self._clients.pop(conn_id, None)
                if self._active_conn_id == conn_id:
                    self._active_conn_id = next(iter(self._clients), None)
                for rid, (owner_cid, fut) in list(self._pending.items()):
                    if owner_cid == conn_id:
                        if not fut.done():
                            fut.set_exception(ConnectionError(f"Game [{conn_id}] disconnected during RPC"))
                        self._pending.pop(rid, None)

    async def call(
        self,
        method: str,
        params: dict[str, Any] | None = None,
        timeout: float = 12.0,
        conn_id: str | None = None,
    ) -> dict[str, Any]:
        target = self._get_active_client(conn_id)
        if target is None:
            return {
                "ok": False,
                "error": "Game plugin is not connected to bridge. Ensure StarASIA.exe / StarASIA_STEAM.exe is running with Stellar.AgyBridge.",
            }

        req_id = uuid.uuid4().hex[:12]
        loop = asyncio.get_running_loop()
        fut: asyncio.Future[dict[str, Any]] = loop.create_future()
        self._pending[req_id] = (target.conn_id, fut)

        payload = {
            "id": req_id,
            "method": method,
            "params": params or {},
        }

        try:
            await target.ws.send_text(json.dumps(payload, ensure_ascii=False))
            return await asyncio.wait_for(fut, timeout=timeout)
        except asyncio.TimeoutError:
            self._pending.pop(req_id, None)
            return {"ok": False, "error": f"RPC '{method}' timed out after {timeout:.1f}s"}
        except Exception as exc:
            self._pending.pop(req_id, None)
            return {"ok": False, "error": f"RPC '{method}' failed: {exc}"}
