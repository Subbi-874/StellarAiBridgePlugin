import argparse
import asyncio
import ctypes
from ctypes import wintypes
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import shutil
import sys
import time
from typing import Any
import urllib.request
import uvicorn
from mcp.server.mcpserver import MCPServer
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route, WebSocketRoute
from starlette.websockets import WebSocket

import config
from chat_monitor import ChatMonitorService
from hub import GameBridgeHub
import legacy_compat
from memory_store import SqliteMemoryStore


def _setup_logging() -> None:
    log_path = Path(__file__).resolve().parent / "bridge.log"
    handlers: list[logging.Handler] = [
        RotatingFileHandler(log_path, maxBytes=2 * 1024 * 1024, backupCount=1, encoding="utf-8")
    ]
    if sys.stdout is not None and sys.stderr is not None:
        handlers.append(logging.StreamHandler())
    else:
        # Running windowless under pythonw.exe -> attach safe file sink so uvicorn never hits NoneType.isatty
        sink = open(os.devnull, "w", encoding="utf-8")
        if sys.stdout is None:
            sys.stdout = sink
        if sys.stderr is None:
            sys.stderr = sink

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        handlers=handlers,
        force=True,
    )


_setup_logging()
logger = logging.getLogger("agy-bridge.server")

hub = GameBridgeHub(expected_token=config.BRIDGE_TOKEN)
_stdio_mode = False

chat_monitor: ChatMonitorService | None = None


def _any_game_process_running() -> bool:
    """Checks via in-process Win32 Toolhelp32 (<0.3ms, zero console flash) whether StarASIA.exe or StarASIA_STEAM.exe exists."""
    if os.name != "nt":
        return True
    try:
        TH32CS_SNAPPROCESS = 0x00000002

        class PROCESSENTRY32W(ctypes.Structure):
            _fields_ = [
                ("dwSize", wintypes.DWORD),
                ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wintypes.DWORD),
                ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD),
                ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wintypes.DWORD),
                ("szExeFile", wintypes.WCHAR * 260),
            ]

        k32 = ctypes.windll.kernel32
        snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if snap == ctypes.c_void_p(-1).value or snap == -1:
            return True
        try:
            entry = PROCESSENTRY32W()
            entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
            ok = k32.Process32FirstW(snap, ctypes.byref(entry))
            while ok:
                exe_low = entry.szExeFile.lower()
                if exe_low in ("starasia.exe", "starasia_steam.exe"):
                    return True
                ok = k32.Process32NextW(snap, ctypes.byref(entry))
            return False
        finally:
            k32.CloseHandle(snap)
    except Exception:
        return True


def _try_stage_latest_dll() -> None:
    """Copies any newly compiled Stellar.AgyBridge.dll into the game slots, renaming locked DLLs aside if running."""
    root = Path(__file__).resolve().parent.parent
    built = root / "plugin" / "bin" / "Release" / "Stellar.AgyBridge.dll"
    if not built.exists():
        return
    built_stat = built.stat()
    slot_dirs = [
        *sorted(Path(r"E:\StarLauncher\game").glob(r"release_*\game_mini\stellar\plugins\agybridge")),
        Path(r"G:\SteamLibrary\steamapps\common\星痕\stellar\plugins\agybridge"),
    ]
    for slot_dir in slot_dirs:
        try:
            if not slot_dir.exists():
                continue
            for old_f in slot_dir.glob("Stellar.AgyBridge.old_*"):
                try:
                    old_f.unlink()
                except OSError:
                    pass

            dest = slot_dir / "Stellar.AgyBridge.dll"
            needs_copy = not dest.exists()
            if not needs_copy:
                dest_stat = dest.stat()
                needs_copy = (
                    built_stat.st_mtime > dest_stat.st_mtime + 0.5
                    or built_stat.st_size != dest_stat.st_size
                )

            if needs_copy:
                try:
                    shutil.copy2(built, dest)
                except OSError:
                    aside = slot_dir / f"Stellar.AgyBridge.old_{int(time.time() * 1000)}"
                    dest.rename(aside)
                    shutil.copy2(built, dest)
                (slot_dir / ".plugin-version").write_text("0.9.22", encoding="utf-8")
                logger.info("Auto-deployed latest Stellar.AgyBridge.dll (v0.9.22) to %s", dest)
        except OSError as exc:
            logger.debug("Auto-deploy DLL skipped for %s: %s", slot_dir, exc)


def _plugin_supports(method: str, conn_id: str | None = None) -> bool:
    return hub.client_supports(method, conn_id=conn_id)


async def _raw_hub_call(
    method: str,
    params: dict[str, Any] | None = None,
    timeout: float | None = None,
    conn_id: str | None = None,
) -> dict[str, Any]:
    return await hub.call(method, params or {}, timeout=timeout or config.DEFAULT_RPC_TIMEOUT, conn_id=conn_id)


async def dispatch_game_rpc(
    method: str,
    params: dict[str, Any] | None = None,
    timeout: float | None = None,
    conn_id: str | None = None,
) -> dict[str, Any]:
    """Unified dispatcher handling native C# RPCs plus legacy fallback delegation for pre-v0.9.5 plugins."""
    params = params or {}
    active_cid = conn_id or hub.status_summary().get("active_conn_id")

    if method in ("stop", "move_to", "teleport_place", "teleport_scene", "leave_hub", "follow_party", "follow_player"):
        if method == "stop":
            stopped_py = legacy_compat.cancel_py_follow(str(params.get("reason") or "user_stop"))
            res = await _raw_hub_call("stop", params, timeout, conn_id=conn_id)
            if stopped_py:
                res["stopped_follow_player"] = stopped_py
            return res
        legacy_compat.cancel_py_follow(f"preempted_by_{method}")

    if method == "get_status":
        res = await _raw_hub_call("get_status", params, timeout, conn_id=conn_id)
        return legacy_compat.augment_status_with_py_follow(res)

    if method == "configure_chat_monitor" and chat_monitor is not None:
        st = chat_monitor.configure(
            enabled=bool(params.get("enabled", False)),
            channels=params.get("channels"),
            use_local_agy=params.get("use_local_agy"),
            extra_triggers=params.get("extra_triggers"),
            permission_mode=params.get("permission_mode"),
            allow_non_commander_reply=params.get("allow_non_commander_reply"),
            authorized_commanders=params.get("authorized_commanders"),
            conn_id=active_cid,
        )
        if _plugin_supports("sync_ui_chat_settings", active_cid):
            await _raw_hub_call(
                "sync_ui_chat_settings",
                {
                    "enabled": st["enabled"],
                    "channels": st["channels"],
                    "permission_mode": st["permission_mode"],
                    "allow_non_commander_reply": st["allow_non_commander_reply"],
                    "authorized_commanders": st["authorized_commanders"],
                    "authorized_commander_uids": st["authorized_commander_uids"],
                },
                conn_id=active_cid,
            )
        return {"ok": True, "chat_monitor": st}

    if method == "get_chat_monitor_status" and chat_monitor is not None:
        return {"ok": True, "chat_monitor": chat_monitor.status(conn_id=active_cid)}

    if method == "get_bot_goals" and chat_monitor is not None:
        return chat_monitor.get_bot_goals_summary(
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
        )

    if method == "cancel_bot_goal" and chat_monitor is not None:
        return await chat_monitor.cancel_bot_goal(
            goal_id=str(params.get("goal_id") or ""),
            cancel_all=bool(params.get("cancel_all", False)),
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
        )

    if method == "set_autonomous_mode" and chat_monitor is not None:
        return chat_monitor.configure_autonomous_mode(
            enabled=bool(params.get("enabled", False)),
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
            manual_input_grace_sec=(
                float(params["manual_input_grace_sec"])
                if params.get("manual_input_grace_sec") is not None
                else None
            ),
        )

    if method == "get_rule_promotion_candidates" and chat_monitor is not None:
        return {"ok": True, "candidates": chat_monitor.get_rule_promotion_candidates()}

    if method == "get_bot_memories" and chat_monitor is not None:
        return chat_monitor.get_bot_memories(
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
            query=str(params.get("query") or ""),
            category=str(params.get("category") or ""),
            include_events=bool(params.get("include_events", True)),
            limit=int(params.get("limit") or 30),
        )

    if method == "save_bot_memory" and chat_monitor is not None:
        return chat_monitor.save_bot_memory(
            category=str(params.get("category") or ""),
            mem_key=str(params.get("mem_key") or ""),
            content=str(params.get("content") or ""),
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
            subject_uid=int(params.get("subject_uid") or 0),
        )

    if method == "delete_bot_memory" and chat_monitor is not None:
        return chat_monitor.delete_bot_memory(
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
            memory_id=int(params.get("memory_id") or 0),
            mem_key=str(params.get("mem_key") or ""),
            category=str(params.get("category") or ""),
        )

    if method == "get_known_users" and chat_monitor is not None:
        return chat_monitor.get_known_users(
            query=str(params.get("query") or ""),
            limit=int(params.get("limit") or 50),
        )

    if method == "plan_craft" and chat_monitor is not None:
        return await chat_monitor.plan_bot_craft(
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
            recipe_id=int(params.get("recipe_id") or 0),
            query=str(params.get("query") or params.get("item") or ""),
            count=max(1, int(params.get("count") or 1)),
            count_mode=str(params.get("count_mode") or "additional"),
            allow_monster_farm=bool(params.get("allow_monster_farm", False)),
        )

    if method == "start_craft_goal" and chat_monitor is not None:
        return await chat_monitor.start_bot_craft_goal(
            conn_id=active_cid,
            char_id=int(params.get("char_id") or 0),
            recipe_id=int(params.get("recipe_id") or 0),
            query=str(params.get("query") or params.get("item") or ""),
            count=max(1, int(params.get("count") or 1)),
            count_mode=str(params.get("count_mode") or "additional"),
            allow_monster_farm=bool(params.get("allow_monster_farm", False)),
            priority=str(params.get("priority") or "command"),
            preempt=bool(params.get("preempt", False)),
        )

    if method == "wait_for_chat_trigger" and chat_monitor is not None:
        return await chat_monitor.wait_for_next_trigger(float(params.get("timeout_sec") or 60.0))

    if method == "list_clients":
        enriched: list[dict[str, Any]] = []
        for c in hub.list_clients():
            cid = c["conn_id"]
            st = await hub.call("get_status", {}, timeout=3.0, conn_id=cid)
            player = ((st.get("state") or {}).get("player") or {}) if isinstance(st, dict) else {}
            enriched.append(
                {
                    **c,
                    "char_id": player.get("char_id"),
                    "char_name": player.get("name"),
                    "scene_name": (st.get("state") or {}).get("scene_name") if isinstance(st, dict) else None,
                }
            )
        return {"ok": True, "clients": enriched}

    if method == "select_client":
        sel = str(params.get("selector") or "").strip()
        direct = hub.select_active_client(sel)
        if direct.get("ok"):
            return direct
        # Fallback: match by character name or char_id across connected clients!
        sel_low = sel.lower()
        for c in hub.list_clients():
            cid = c["conn_id"]
            st = await hub.call("get_status", {}, timeout=3.0, conn_id=cid)
            player = ((st.get("state") or {}).get("player") or {}) if isinstance(st, dict) else {}
            cname = str(player.get("name") or "")
            ccid = str(player.get("char_id") or "")
            if (cname and sel_low in cname.lower()) or (ccid and sel == ccid):
                return hub.select_active_client(cid)
        return direct

    handled, legacy_res = await legacy_compat.try_dispatch_legacy(
        method, params, timeout=timeout, conn_id=conn_id, active_cid=active_cid
    )
    if handled:
        assert legacy_res is not None
        return legacy_res

    if method == "play_emote":
        res = (
            await _raw_hub_call(method, params, timeout, conn_id=conn_id)
            if _plugin_supports("play_emote", conn_id)
            else await legacy_compat.fallback_play_emote(params, conn_id=conn_id)
        )
        # Retry once after 0.35s if character was just finishing a step/deceleration
        if not res.get("ok") and not res.get("skipped_unowned") and "상태" in str(res.get("error") or ""):
            await asyncio.sleep(0.35)
            res = (
                await _raw_hub_call(method, params, timeout, conn_id=conn_id)
                if _plugin_supports("play_emote", conn_id)
                else await legacy_compat.fallback_play_emote(params, conn_id=conn_id)
            )
        return res

    return await _raw_hub_call(method, params, timeout, conn_id=conn_id)


# ---------------------------------------------------------------------------
# Pre-v0.9.0 in-memory compatibility layer (auto-disabled once v0.9.0 DLL loads)
# ---------------------------------------------------------------------------


memory_store = SqliteMemoryStore(
    config.MEMORY_DB_PATH,
    max_memories_per_bot=config.MEMORY_MAX_PER_BOT,
    max_events_per_bot=config.MEMORY_EVENTS_MAX_PER_BOT,
)
chat_monitor = ChatMonitorService(
    lambda m, p, conn_id=None: dispatch_game_rpc(m, p, conn_id=conn_id),
    memory_store=memory_store,
)
hub.add_event_listener(chat_monitor.on_bridge_event)


async def invoke_game(method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
    """Routes RPC either directly to the in-process WebSocket hub or over HTTP when running in --stdio proxy mode."""
    if not _stdio_mode:
        return await dispatch_game_rpc(method, params, timeout=config.DEFAULT_RPC_TIMEOUT)

    def _post_sync() -> dict[str, Any]:
        data = json.dumps({"method": method, "params": params or {}}).encode("utf-8")
        headers = {"Content-Type": "application/json"}
        if config.BRIDGE_TOKEN:
            headers["Authorization"] = f"Bearer {config.BRIDGE_TOKEN}"
        req = urllib.request.Request(
            f"{config.BRIDGE_HTTP_BASE}/api/call",
            data=data,
            headers=headers,
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=config.DEFAULT_RPC_TIMEOUT + 2.0) as resp:
            return json.loads(resp.read().decode("utf-8"))

    return await asyncio.to_thread(_post_sync)


async def await_async_rpc(async_id: str, timeout: float = 6.0) -> dict[str, Any]:
    """Polls a Lua coroutine RPC started on the game main thread until completion."""
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout
    last: dict[str, Any] = {}
    while loop.time() < deadline:
        await asyncio.sleep(0.3)
        poll = await invoke_game("poll_async", {"async_id": async_id})
        last = poll
        entry = poll.get("result") if isinstance(poll, dict) else None
        if isinstance(entry, dict):
            state = entry.get("state")
            if state == "done":
                inner = entry.get("result")
                return inner if isinstance(inner, dict) else {"ok": True, "result": inner}
            if state in ("error", "missing"):
                return {"ok": False, "state": state, "error": entry.get("error", state)}
    return {"ok": True, "pending_async_id": async_id, "last_poll": last}


async def wait_for_scene_ready(
    prev_scene_id: int,
    expected_scene_id: int = 0,
    timeout: float = 25.0,
) -> dict[str, Any]:
    """Waits for any loading transition to settle and returns the latest player state."""
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout
    saw_loading_or_change = False
    latest_state: dict[str, Any] = {}

    await asyncio.sleep(0.9)
    while loop.time() < deadline:
        status = await invoke_game("get_status")
        st = status.get("state") if isinstance(status, dict) else None
        if isinstance(st, dict):
            latest_state = st
            phase = st.get("phase")
            loading = bool(st.get("is_loading", False))
            world_active = bool(st.get("is_world_active", False))
            player_obj = st.get("player")
            player_avail = bool(player_obj.get("is_available", False)) if isinstance(player_obj, dict) else False
            cur_scene = int(st.get("scene_id") or 0)

            if loading or phase != "World" or (cur_scene > 0 and cur_scene != prev_scene_id):
                saw_loading_or_change = True

            if phase == "World" and world_active and not loading and player_avail:
                if expected_scene_id > 0 and cur_scene == expected_scene_id:
                    if expected_scene_id != prev_scene_id or saw_loading_or_change:
                        return latest_state
                elif expected_scene_id == 0 and cur_scene > 0 and cur_scene != prev_scene_id:
                    return latest_state
        await asyncio.sleep(0.8)

    return latest_state


legacy_compat.bind_runtime(hub, chat_monitor, _raw_hub_call, await_async_rpc, wait_for_scene_ready)


mcp = MCPServer(
    name="stellar-agy-bridge",
    instructions=(
        "Controls and observes a Blue Protocol: Star Resonance character via the Stellar.AgyBridge plugin. "
        "Use get_status / get_party / get_guild_members to inspect state, teleport_to_place / list_places to move "
        "to Guild Home, Personal Home, cities, or warp points, follow_player / follow_party_member to follow any "
        "party or guild member across maps/hubs/channels (continuously or one-shot), and stop to halt movement."
    ),
)


@mcp.tool()
async def get_status() -> str:
    """Returns local player state (HP, scene, channel line_id, 3D coordinates, party summary) and active behavior."""
    res = await invoke_game("get_status")
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_party(refresh_line: bool = True) -> str:
    """Lists all party members with their name, char_id, leader status, scene, channel (line_id), AOI status, and position."""
    res = await invoke_game("get_party", {"refresh_line": refresh_line})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_guild_members(online_only: bool = True) -> str:
    """Lists guild members (name, char_id, level, online status, current scene_id and scene_name)."""
    res = await invoke_game("get_guild_members", {"online_only": online_only})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_social_roster(
    category: str = "all",
    query: str = "",
    online_only: bool = False,
) -> str:
    """
    Lists a unified, deduplicated UID roster across Party ('party'), Friends ('friend'), and Guild ('guild'), or 'all'.
    Returns each player's numeric uid (char_id), name, online status, source_badge ([파티], [친구], [길드]), and scene.
    Use this to disambiguate duplicate nicknames and select exact UIDs for follow_player or configure_chat_monitor.
    """
    res = await invoke_game(
        "get_social_roster",
        {"category": category, "query": query, "online_only": online_only},
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def follow_player(
    target: str,
    stop_distance: float = 2.5,
    continuous: bool = True,
    auto_teleport: bool = True,
    auto_switch_channel: bool = True,
) -> str:
    """
    Follows ANY player (party member, guild member, friend, chat sender, or numeric char_id) across maps, Guild Hall, and channels.
    - target: Character name (e.g. '안도라'), numeric char_id, or 'leader' / '파티장'.
    - stop_distance: Distance in meters to maintain from the target (default 2.5).
    - continuous: If True (default), continuously follows the target as they walk or change maps/channels until stop() is called.
                  If False, approaches the target once and stops automatically upon arrival.
    - auto_teleport: Automatically enter Guild Hall / teleport to the target's map if on a different scene.
    - auto_switch_channel: Automatically switch channel (line) to match the target.
    """
    res = await invoke_game(
        "follow_player",
        {
            "target": target,
            "stop_distance": stop_distance,
            "continuous": continuous,
            "auto_teleport": auto_teleport,
            "auto_switch_channel": auto_switch_channel,
        },
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def follow_party_member(
    target: str = "leader",
    stop_distance: float = 2.5,
    continuous: bool = True,
    auto_teleport: bool = True,
    auto_switch_channel: bool = True,
) -> str:
    """
    Follows a party member (or 'leader' / '파티장').
    - continuous: True (default) keeps following until stop() is called; False moves to them once and stops on arrival.
    """
    res = await invoke_game(
        "follow_player",
        {
            "target": target,
            "stop_distance": stop_distance,
            "continuous": continuous,
            "auto_teleport": auto_teleport,
            "auto_switch_channel": auto_switch_channel,
        },
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def stop(reason: str = "user_stop") -> str:
    """Immediately stops any active movement or player-following behavior."""
    res = await invoke_game("stop", {"reason": reason})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def move_to(x: float, z: float, y: float | None = None, stop_distance: float = 1.8) -> str:
    """Moves the character once to the specified world coordinates (x, y, z) on the current map."""
    payload: dict[str, Any] = {"x": x, "z": z, "stop_distance": stop_distance}
    if y is not None:
        payload["y"] = y
    res = await invoke_game("move_to", payload)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_nearby_entities(kind: str = "", limit: int = 40) -> str:
    """Lists nearby in-AOI entities ('player', 'monster', or '' for all) sorted by distance."""
    params: dict[str, Any] = {"limit": limit}
    if kind:
        params["kind"] = kind
    res = await invoke_game("get_entities", params)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_recent_chats(limit: int = 20) -> str:
    """Returns recent in-game chat messages (excluding noisy system notices)."""
    res = await invoke_game("get_chats", {"limit": limit, "include_system": False})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def send_chat(text: str, channel: str = "party", target_char_id: int = 0) -> str:
    """Sends an in-game chat message. channel can be 'party', 'say', 'world', 'guild', or 'whisper'."""
    res = await invoke_game(
        "send_chat",
        {"channel": channel, "text": text, "target_char_id": target_char_id},
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def set_auto_combat(enable: bool | None = None) -> str:
    """Toggles (when enable is None) or sets built-in in-game Auto-Combat ('G' key feature)."""
    params: dict[str, Any] = {}
    if enable is not None:
        params["enable"] = enable
    res = await invoke_game("set_auto_combat", params)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_channels() -> str:
    """Lists current channel (line_id) and available channels on the active map."""
    return await _invoke_and_wait("list_channels", {}, wait_sec=8.0)


@mcp.tool()
async def switch_channel(line_id: int = 0, char_id: int = 0, target: str = "") -> str:
    """Switches channel (scene line) to an explicit line_id or to a player's channel via char_id/target. Automatically stops any active player-following."""
    params: dict[str, Any] = {"line_id": line_id, "char_id": char_id}
    if target:
        params["target"] = target
    return await _invoke_and_wait("switch_channel", params, wait_sec=10.0)


@mcp.tool()
async def list_places(query: str = "") -> str:
    """
    Lists special hubs ('길드 이벤트 센터', '가든 주택' 마이홈) and open-world maps / warp points.
    Provide an optional query (e.g. '길드', '아스텔리즈', '개척국', '바하마르') to filter destinations.
    """
    res = await invoke_game("list_places", {"query": query})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def teleport_to_place(destination: str, wait_until_loaded: bool = True) -> str:
    """
    Immediately enters or teleports to a destination by name, alias, or ID:
    - Guild Hall: '길드 홈', '길드홀', '길드 이벤트 센터'
    - Personal Home: '마이홈', '내 집', '가든 주택'
    - Exit current hub: '나가기', '퇴장'
    - Any map or warp point name: '아스텔리즈', '개척국', '타륜 주점', '아스테리아 평원', '바하마르 고원', etc.
    """
    pre = await invoke_game("get_status")
    pre_state = pre.get("state", {}) if isinstance(pre, dict) else {}
    prev_scene_id = int(pre_state.get("scene_id") or 0)

    start_res = await invoke_game("teleport_place", {"destination": destination, "leave_special_first": False})
    if not start_res.get("ok"):
        return json.dumps(start_res, ensure_ascii=False, indent=2)

    async_id = start_res.get("async_id")
    rpc_res = await await_async_rpc(async_id) if async_id else start_res

    if not rpc_res.get("ok") or rpc_res.get("already_in_scene"):
        return json.dumps({"ok": bool(rpc_res.get("ok")), "rpc": rpc_res, "state": pre_state}, ensure_ascii=False, indent=2)

    if not wait_until_loaded:
        return json.dumps({"ok": True, "rpc": rpc_res}, ensure_ascii=False, indent=2)

    expected_scene = int(rpc_res.get("sceneId") or 0)
    post_state = await wait_for_scene_ready(prev_scene_id, expected_scene_id=expected_scene, timeout=25.0)

    cur_after = int(post_state.get("scene_id") or 0)
    if (
        expected_scene > 0
        and expected_scene != prev_scene_id
        and cur_after == prev_scene_id
        and prev_scene_id >= 12000
    ):
        leave_start = await invoke_game("leave_hub", {})
        if leave_start.get("async_id"):
            await await_async_rpc(leave_start["async_id"])
        mid_state = await wait_for_scene_ready(prev_scene_id, expected_scene_id=0, timeout=25.0)
        mid_scene = int(mid_state.get("scene_id") or 0)
        if mid_scene > 0 and mid_scene != expected_scene:
            await asyncio.sleep(1.5)
            retry_start = await invoke_game("teleport_place", {"destination": destination, "leave_special_first": False})
            if retry_start.get("async_id"):
                rpc_res = await await_async_rpc(retry_start["async_id"])
            post_state = await wait_for_scene_ready(mid_scene, expected_scene_id=expected_scene, timeout=25.0)
        else:
            post_state = mid_state

    return json.dumps(
        {"ok": True, "destination": destination, "rpc": rpc_res, "state": post_state},
        ensure_ascii=False,
        indent=2,
    )


@mcp.tool()
async def leave_special_scene(wait_until_loaded: bool = True) -> str:
    """Exits Guild Hall ('길드 이벤트 센터') or Personal Home ('가든 주택') back to the open world."""
    pre = await invoke_game("get_status")
    pre_state = pre.get("state", {}) if isinstance(pre, dict) else {}
    prev_scene_id = int(pre_state.get("scene_id") or 0)

    start_res = await invoke_game("leave_hub", {})
    if not start_res.get("ok"):
        return json.dumps(start_res, ensure_ascii=False, indent=2)

    async_id = start_res.get("async_id")
    rpc_res = await await_async_rpc(async_id) if async_id else start_res
    if not rpc_res.get("ok") or not wait_until_loaded:
        return json.dumps({"ok": bool(rpc_res.get("ok")), "rpc": rpc_res}, ensure_ascii=False, indent=2)

    post_state = await wait_for_scene_ready(prev_scene_id, expected_scene_id=0, timeout=25.0)
    return json.dumps({"ok": True, "rpc": rpc_res, "state": post_state}, ensure_ascii=False, indent=2)


@mcp.tool()
async def inspect_skill_slots() -> str:
    """Inspects currently equipped skill slots, names, and cooldown states on the character."""
    res = await invoke_game("inspect_skills", {})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def configure_locomotion(
    enable_mobility_skill: bool | None = None,
    mobility_skill_slot: int | None = None,
    mobility_skill_name_hint: str | None = None,
    mobility_skill_min_distance: float | None = None,
    enable_bunnyhop: bool | None = None,
    bunnyhop_min_distance: float | None = None,
    combat_tether_distance: float | None = None,
) -> str:
    """Updates 3-tier locomotion ('E' mobility skill, bunnyhop, combat tether) thresholds dynamically."""
    payload: dict[str, Any] = {}
    if enable_mobility_skill is not None:
        payload["enable_mobility_skill"] = enable_mobility_skill
    if mobility_skill_slot is not None:
        payload["mobility_skill_slot"] = mobility_skill_slot
    if mobility_skill_name_hint is not None:
        payload["mobility_skill_name_hint"] = mobility_skill_name_hint
    if mobility_skill_min_distance is not None:
        payload["mobility_skill_min_distance"] = mobility_skill_min_distance
    if enable_bunnyhop is not None:
        payload["enable_bunnyhop"] = enable_bunnyhop
    if bunnyhop_min_distance is not None:
        payload["bunnyhop_min_distance"] = bunnyhop_min_distance
    if combat_tether_distance is not None:
        payload["combat_tether_distance"] = combat_tether_distance
    res = await invoke_game("set_locomotion_config", payload)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def manage_party(
    action: str,
    target: str = "",
    auto_accept: bool = False,
    leave_first: bool = True,
    team_id: int = 0,
    size: int = 0,
) -> str:
    """
    Manages the character's party:
    - action='create': Creates a party (set auto_accept=True to automatically accept incoming join requests).
    - action='set_auto_accept': Enables or disables auto-accepting join requests on the current party (auto_accept=True/False).
    - action='set_member_type': Switches the party between 5-player and 20-player (raid) mode (size=5 or 20; leader only).
    - action='join': Applies to join another player's party by player name/char_id ('target') or 'team_id'.
    - action='merge': Party LEADER applies to merge into another player's party ('target' name/char_id or 'team_id'), keeping own party.
    - action='leave': Leaves the current party.
    - action='invite': Invites 'target' player to the party (creates a party first if solo).
    - action='accept_apply': Approves all pending incoming party join applications.
    - action='transfer_leader': Transfers party leadership to 'target' party member (name or UID; leader only).
    - action='kick': Kicks 'target' from the party (leader only).
    """
    params: dict[str, Any] = {
        "action": action,
        "target": target,
        "auto_accept": auto_accept,
        "enable": auto_accept,
        "leave_first": leave_first,
        "team_id": team_id,
    }
    if size:
        params["size"] = size
    res = await invoke_game("party_action", params)
    if res.get("async_id"):
        rpc_res = await await_async_rpc(res["async_id"])
        return json.dumps({"ok": bool(rpc_res.get("ok", True)), "rpc": rpc_res}, ensure_ascii=False, indent=2)
    return json.dumps(res, ensure_ascii=False, indent=2)


async def _invoke_and_wait(method: str, params: dict[str, Any], wait_sec: float = 12.0) -> str:
    """Invokes a plugin command and, if it started an async operation, waits for its final result."""
    res = await invoke_game(method, params)
    if isinstance(res, dict) and res.get("ok") and res.get("async_id"):
        rpc_res = await await_async_rpc(res["async_id"], timeout=wait_sec)
        return json.dumps({"ok": bool(rpc_res.get("ok", True)), "start": res, "result": rpc_res}, ensure_ascii=False, indent=2)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def revive(revive_type: int = 0, dry_run: bool = False) -> str:
    """Revives the character if dead (free options only: nearby → revive point → main city). dry_run=True only reports the chosen option."""
    params: dict[str, Any] = {"dry_run": dry_run}
    if revive_type:
        params["type"] = revive_type
    return await _invoke_and_wait("revive", params)


@mcp.tool()
async def ride(action: str = "mount", ride_type: str = "auto") -> str:
    """Mounts/dismounts the equipped Riding Imagine mount. action: 'mount' | 'dismount' | 'toggle'; ride_type: 'auto' | 'land' | 'water'."""
    return await _invoke_and_wait("ride", {"action": action, "type": ride_type})


@mcp.tool()
async def accept_interaction(target: str, window_sec: int = 60, kinds: str = "") -> str:
    """
    Approves requests sent by 'target' (name/UID) within window_sec seconds.
    kinds (comma list, empty = all): interaction (2-person action), ride (ride-together apply/invite), party (party invite).
    """
    params: dict[str, Any] = {"target": target, "window_sec": window_sec}
    if kinds:
        params["kinds"] = kinds
    return await _invoke_and_wait("accept_interaction", params)


@mcp.tool()
async def request_ride(target: str, mode: str = "apply") -> str:
    """Sends a ride-together request to a NEARBY player. mode='apply': ride THEIR mount; mode='invite': let them ride OUR mount."""
    return await _invoke_and_wait("request_ride", {"target": target, "mode": mode})


@mcp.tool()
async def request_interaction(target: str, action: str = "손잡기") -> str:
    """Sends a 2-person action request (default '손잡기') to a NEARBY player; they must accept it."""
    return await _invoke_and_wait("request_interaction", {"target": target, "action": action})


@mcp.tool()
async def query_player_location(target: str) -> str:
    """Looks up where a player is: map name/type, channel, coordinates, distance, and whether the map is enterable."""
    res = await invoke_game("query_player_location", {"target": target})
    for _ in range(3):
        if not (isinstance(res, dict) and res.get("pending")):
            break
        await asyncio.sleep(1.0)
        res = await invoke_game("query_player_location", {"target": target})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_presets() -> str:
    """Lists job/skill presets (loadouts) and outfit presets available to the current character."""
    loadouts = await invoke_game("list_loadouts", {})
    outfits = await invoke_game("list_outfits", {})
    return json.dumps({"loadouts": loadouts, "outfits": outfits}, ensure_ascii=False, indent=2)


@mcp.tool()
async def switch_loadout(number: int = 0, name: str = "", profession: str = "") -> str:
    """Switches the job/skill/talent/gear preset by 1-based number, preset name, or profession (id or Korean name)."""
    params: dict[str, Any] = {}
    if number:
        params["number"] = number
    if name:
        params["name"] = name
    if profession:
        params["profession"] = profession
    return await _invoke_and_wait("switch_loadout", params)


@mcp.tool()
async def switch_outfit(number: int = 0, name: str = "") -> str:
    """Switches to an outfit preset (from Stellar.WardrobeLoadout) by 1-based number or name; with neither, picks a random preset."""
    params: dict[str, Any] = {}
    if number:
        params["number"] = number
    if name:
        params["name"] = name
    return await _invoke_and_wait("switch_outfit", params)


@mcp.tool()
async def seek_monster(monster: str, engage: bool = False) -> str:
    """Travels (across maps if needed) to the nearest spawn point of a monster by name or id. engage=True turns on auto-combat on arrival."""
    return json.dumps(await invoke_game("seek_monster", {"monster": monster, "engage": engage}), ensure_ascii=False, indent=2)


@mcp.tool()
async def gather_resource(
    resource: str,
    count: int = 20,
    use_focus: bool | None = None,
) -> str:
    """
    Integrates with Stellar.AutoGather to automatically teleport near the resource cluster, gather 'resource'
    (e.g. '사향 분말', '부채조개', '속삭임 난초', or '주변' for any nearby node) 'count' times, and stop automatically.
    """
    params: dict[str, Any] = {"resource": resource, "count": count}
    if use_focus is not None:
        params["use_focus"] = use_focus
    res = await invoke_game("gather_resource", params)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_gatherable_resources(query: str = "") -> str:
    """Lists gatherable life-skill resources (name, ID, map area, focus cost) matching an optional Korean query."""
    res = await invoke_game("list_gatherables", {"query": query})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_fishing_spots(query: str = "") -> str:
    """Lists fishing areas, landmarks, and catchable fish species dynamically from game tables (optionally filtered by Korean query)."""
    res = await invoke_game("list_fishing_spots", {"query": query})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def fish(spot: str = "", count: int = 0) -> str:
    """
    Integrates with Stellar.AutoFishing to travel to a fishing spot (or nearest spot when spot=''),
    equip rod & compatible bait, enter fishing stance, and auto-fish ('count' fish or continuously when count=0).
    """
    res = await invoke_game("fish", {"spot": spot, "count": count})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def configure_chat_monitor(
    enabled: bool,
    channels: list[str] | None = None,
    use_local_agy: bool = True,
    extra_triggers: list[str] | None = None,
    permission_mode: str | None = None,
    allow_non_commander_reply: bool | None = None,
    authorized_commanders: list[str] | None = None,
) -> str:
    """
    Configures real-time in-game chat monitoring and commander permissions.
    - enabled: True to activate chat monitoring, False to stop.
    - channels: Channels to watch, e.g. ['guild', 'party'] (also supports 'say', 'world', 'whisper').
    - permission_mode: 'party_or_whitelist' (default), 'whitelist_only', or 'everyone'.
    - allow_non_commander_reply: If True, non-commanders get conversational replies while control actions are blocked.
    - authorized_commanders: List of character names or char_ids allowed to issue control commands.
    """
    params: dict[str, Any] = {"enabled": enabled, "use_local_agy": use_local_agy}
    if channels is not None:
        params["channels"] = channels
    if extra_triggers is not None:
        params["extra_triggers"] = extra_triggers
    if permission_mode is not None:
        params["permission_mode"] = permission_mode
    if allow_non_commander_reply is not None:
        params["allow_non_commander_reply"] = allow_non_commander_reply
    if authorized_commanders is not None:
        params["authorized_commanders"] = authorized_commanders
    res = await invoke_game("configure_chat_monitor", params)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_chat_monitor_status() -> str:
    """Returns current chat monitor configuration, local AGY CLI readiness, and recent handled chat triggers."""
    res = await invoke_game("get_chat_monitor_status", {})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_bot_goals(char_id: int = 0) -> str:
    """Returns the per-bot GoalManager state (active goal, paused resume stack, queued goals, autonomous config)."""
    res = await invoke_game("get_bot_goals", {"char_id": char_id})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def cancel_bot_goal(goal_id: str = "", cancel_all: bool = False, char_id: int = 0) -> str:
    """Cancels an active, paused, or queued goal on the selected bot (or all goals when cancel_all=True)."""
    res = await invoke_game(
        "cancel_bot_goal",
        {"goal_id": goal_id, "cancel_all": cancel_all, "char_id": char_id},
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def set_autonomous_mode(
    enabled: bool,
    char_id: int = 0,
    manual_input_grace_sec: float | None = None,
) -> str:
    """Toggles per-bot autonomous idle mode (default OFF) and optional manual-input grace window in seconds."""
    params: dict[str, Any] = {"enabled": enabled, "char_id": char_id}
    if manual_input_grace_sec is not None:
        params["manual_input_grace_sec"] = manual_input_grace_sec
    res = await invoke_game("set_autonomous_mode", params)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_rule_promotion_candidates() -> str:
    """Returns repeated LLM exception-consultation patterns eligible for promotion into deterministic rules."""
    res = await invoke_game("get_rule_promotion_candidates", {})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_bot_memories(
    char_id: int = 0,
    query: str = "",
    category: str = "",
    include_events: bool = True,
    limit: int = 30,
) -> str:
    """Queries persistent SQLite memories and recent events partitioned by `char_id` (or the active client's character)."""
    res = await invoke_game(
        "get_bot_memories",
        {
            "char_id": char_id,
            "query": query,
            "category": category,
            "include_events": include_events,
            "limit": limit,
        },
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def save_bot_memory(
    category: str,
    mem_key: str,
    content: str,
    char_id: int = 0,
    subject_uid: int = 0,
) -> str:
    """Saves or updates a persistent memory entry (`preference`, `relationship`, `nickname_alias`, `habit`, `note`) for the selected bot character."""
    res = await invoke_game(
        "save_bot_memory",
        {
            "category": category,
            "mem_key": mem_key,
            "content": content,
            "char_id": char_id,
            "subject_uid": subject_uid,
        },
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def delete_bot_memory(
    memory_id: int = 0,
    mem_key: str = "",
    category: str = "",
    char_id: int = 0,
) -> str:
    """Deletes stored memories matching `memory_id` or `mem_key` for the selected bot character."""
    res = await invoke_game(
        "delete_bot_memory",
        {
            "memory_id": memory_id,
            "mem_key": mem_key,
            "category": category,
            "char_id": char_id,
        },
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_known_users(query: str = "", limit: int = 50) -> str:
    """Lists shared player UID records and observed nickname change history across all bots."""
    res = await invoke_game("get_known_users", {"query": query, "limit": limit})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_item_count(query: str = "", item_id: int = 0) -> str:
    """Queries live bag inventory counts (materials, consumables, currency, vitality) by Korean item name or item_id."""
    res = await invoke_game("get_item_count", {"query": query, "item_id": item_id})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def get_recipe(query: str = "", recipe_id: int = 0) -> str:
    """Queries live recipe details across life crafting, cooking, alchemy, housing furniture, and equip forge."""
    res = await invoke_game("get_recipe", {"query": query, "recipe_id": recipe_id})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def plan_craft(
    item: str = "",
    count: int = 1,
    recipe_id: int = 0,
    allow_monster_farm: bool = False,
    count_mode: str = "additional",
    char_id: int = 0,
) -> str:
    """Deterministically expands a crafting dependency tree against live inventory and SQLite cached recipes without executing."""
    res = await invoke_game(
        "plan_craft",
        {
            "item": item,
            "count": count,
            "recipe_id": recipe_id,
            "allow_monster_farm": allow_monster_farm,
            "count_mode": count_mode,
            "char_id": char_id,
        },
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def start_craft_goal(
    item: str = "",
    count: int = 1,
    recipe_id: int = 0,
    allow_monster_farm: bool = False,
    count_mode: str = "additional",
    priority: str = "command",
    preempt: bool = False,
    char_id: int = 0,
) -> str:
    """Plans and enqueues/starts a multi-stage crafting Goal (gather -> fish -> sub-craft -> final craft; asks before monster hunting unless allow_monster_farm=True)."""
    res = await invoke_game(
        "start_craft_goal",
        {
            "item": item,
            "count": count,
            "recipe_id": recipe_id,
            "allow_monster_farm": allow_monster_farm,
            "count_mode": count_mode,
            "priority": priority,
            "preempt": preempt,
            "char_id": char_id,
        },
    )
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def wait_for_chat_trigger(timeout_sec: float = 60.0) -> str:
    """Waits for the next triggered chat message when chat monitor is running with use_local_agy=False."""
    res = await invoke_game("wait_for_chat_trigger", {"timeout_sec": timeout_sec})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def play_emote(emote: str = "", emote_id: int = 0, stop: bool = False) -> str:
    """
    Plays a standalone emote/action on the active character (e.g. emote='박수', '댄스', '앉기', '인사', '죽은 척하기'),
    or stops any ongoing continuous emote when stop=True (or emote='중지'). Silently skips if unowned.
    """
    res = await invoke_game("play_emote", {"emote": emote, "emote_id": emote_id, "stop": stop})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_emotes(query: str = "") -> str:
    """Lists all unlocked emotes (ID, Korean name, looping flag) available on the active character."""
    res = await invoke_game("list_emotes", {"query": query})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_dungeons(query: str = "", category: str = "") -> str:
    """
    Lists enterable dungeons, Unstable Spaces ('unstable'), Master Mode dungeons ('master'), Raids ('raid'),
    and Boss activities matching an optional Korean query.
    """
    res = await invoke_game("list_dungeons", {"query": query, "category": category})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def enter_dungeon(
    dungeon: str = "",
    difficulty: str = "",
    dungeon_id: int = 0,
    single_ai: bool = False,
) -> str:
    """
    Starts entry into a dungeon, Master Mode tier (e.g. difficulty='마스터 5'), Unstable Space ('불안정 공간'),
    Raid ('회귀'/'혈전'/'결전' or '이지'/'하드'/'나이트메어'; auto-converts 5-man party to 20-man raid when needed),
    Sidimoon Tower, or World Boss ('월드 보스') when the character is solo or the party leader.
    """
    params: dict[str, Any] = {
        "dungeon": dungeon,
        "difficulty": difficulty,
        "single_ai": single_ai,
    }
    if dungeon_id > 0:
        params["dungeon_id"] = dungeon_id
    return await _invoke_and_wait("enter_dungeon", params, wait_sec=12.0)


@mcp.tool()
async def ready_check() -> str:
    """Manually confirms any active dungeon / raid / Unstable Space / World Boss ready-check popup immediately."""
    return await _invoke_and_wait("ready_check", {}, wait_sec=8.0)


@mcp.tool()
async def list_stickers(query: str = "", group_id: int = 0) -> str:
    """Lists owned in-game chat picture stickers and supported semantic mood tags ('greet', 'thanks', 'thumbsup', 'cheer', 'heart', 'laugh', 'cute', 'ok', 'think', 'sorry', 'sad', 'random')."""
    params: dict[str, Any] = {"query": query}
    if group_id > 0:
        params["group_id"] = group_id
    res = await invoke_game("list_stickers", params)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def send_sticker(
    sticker: str = "random",
    channel: str = "party",
    sticker_id: int = 0,
    target_char_id: int = 0,
) -> str:
    """
    Sends an owned built-in picture sticker to an in-game chat channel ('party', 'guild', 'whisper', etc.).
    - sticker: Semantic mood tag ('greet', 'thanks', 'thumbsup', 'cheer', 'heart', 'laugh', 'cute', 'ok', 'think', 'sorry', 'sad', 'random'), resource substring, or ID string.
    """
    params: dict[str, Any] = {
        "channel": channel,
        "sticker": sticker,
        "target_char_id": target_char_id,
    }
    if sticker_id > 0:
        params["sticker_id"] = sticker_id
    res = await invoke_game("send_sticker", params)
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_game_clients() -> str:
    """Lists all simultaneously connected game clients (e.g. E:\\ StarASIA and G:\\ StarASIA_STEAM) with character names."""
    res = await invoke_game("list_clients", {})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def select_game_client(selector: str) -> str:
    """Selects which connected game client AGY controls (by character name e.g. '쵸로기', drive/path 'E:' or 'G:', PID, or conn_id)."""
    res = await invoke_game("select_client", {"selector": selector})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def leave_dungeon() -> str:
    """Immediately exits the current Dungeon, Master Mode, Unstable Space, 20-Man Raid, Sidimoon Tower, World Boss, Visual Layer, Guild Hall, or Personal Home."""
    res = await invoke_game("leave_dungeon", {})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def list_gimmicks(query: str = "", radius: float = 45.0) -> str:
    """Lists nearby interactive traversal field/dungeon gimmicks (Wind Tunnels, Glider Launches, Intra-map Teleporters) excluding dungeon exits, chests, seats, and fishing spots."""
    res = await invoke_game("list_gimmicks", {"query": query, "radius": radius})
    return json.dumps(res, ensure_ascii=False, indent=2)


@mcp.tool()
async def use_gimmick(query: str = "") -> str:
    """Approaches and activates a nearby traversal field/dungeon gimmick (e.g. '바람의 통로', '순간 이동', or nearest available if query is empty)."""
    res = await invoke_game("use_gimmick", {"gimmick": query})
    return json.dumps(res, ensure_ascii=False, indent=2)


def build_http_app():
    app = mcp.streamable_http_app(host=config.BRIDGE_HOST)

    async def ws_endpoint(websocket: WebSocket):
        await hub.handle_websocket(websocket)

    async def api_status(_request: Request):
        return JSONResponse(hub.status_summary())

    async def api_events(request: Request):
        limit = int(request.query_params.get("limit", "50"))
        return JSONResponse({"events": hub.get_events(limit)})

    async def api_call(request: Request):
        if config.BRIDGE_TOKEN:
            auth = request.headers.get("authorization", "")
            if auth != f"Bearer {config.BRIDGE_TOKEN}":
                return JSONResponse({"ok": False, "error": "Unauthorized"}, status_code=401)
        body = await request.json()
        method = body.get("method", "")
        params = body.get("params") or {}
        timeout = float(body.get("timeout", config.DEFAULT_RPC_TIMEOUT))
        # Optional: target one specific game client (conn_id) without switching the globally active client.
        result = await dispatch_game_rpc(method, params, timeout=timeout, conn_id=body.get("conn_id") or None)
        return JSONResponse(result)

    app.routes.insert(0, WebSocketRoute("/ws", ws_endpoint))
    app.routes.insert(0, Route("/api/status", api_status, methods=["GET"]))
    app.routes.insert(0, Route("/api/events", api_events, methods=["GET"]))
    app.routes.insert(0, Route("/api/call", api_call, methods=["POST"]))
    return app


async def _run_daemon_with_watchdog(app: Any, host: str, port: int, keep_alive: bool) -> None:
    cfg = uvicorn.Config(app, host=host, port=port, log_level="info", log_config=None)
    server = uvicorn.Server(cfg)

    async def _watchdog() -> None:
        no_game_since: float | None = None
        _try_stage_latest_dll()
        tick_count = 0
        while not server.should_exit:
            await asyncio.sleep(1.0)
            _try_stage_latest_dll()
            tick_count = (tick_count + 1) % 4
            if tick_count != 0:
                continue
            try:
                await chat_monitor.tick_autonomous_agents()
            except Exception as auto_exc:  # noqa: BLE001
                logger.debug("Autonomous tick skipped: %r", auto_exc)
            if keep_alive:
                continue
            if hub.is_connected or _any_game_process_running():
                no_game_since = None
                continue
            now = time.monotonic()
            if no_game_since is None:
                no_game_since = now
            elif now - no_game_since >= 12.0:
                logger.info(
                    "Neither StarASIA.exe (E:) nor StarASIA_STEAM.exe (G:) is running; shutting down bridge daemon automatically."
                )
                server.should_exit = True
                return


    wd_task = asyncio.create_task(_watchdog())
    compat_task = asyncio.create_task(legacy_compat.run_pre_v090_poller(lambda: server.should_exit))
    try:
        await server.serve()
    finally:
        wd_task.cancel()
        compat_task.cancel()
        memory_store.close()


def main() -> None:
    global _stdio_mode
    parser = argparse.ArgumentParser(description="Stellar AGY Bridge & MCP Server")
    parser.add_argument("--stdio", action="store_true", help="Run as MCP stdio client proxying to running bridge daemon")
    parser.add_argument("--host", default=config.BRIDGE_HOST, help="Bind host for HTTP/WS daemon")
    parser.add_argument("--port", type=int, default=config.BRIDGE_PORT, help="Bind port for HTTP/WS daemon")
    parser.add_argument("--keep-alive", action="store_true", help="Do not auto-exit when all game processes close")
    args = parser.parse_args()

    if args.stdio:
        _stdio_mode = True
        mcp.run(transport="stdio")
    else:
        _try_stage_latest_dll()
        config.BRIDGE_HOST = args.host
        config.BRIDGE_PORT = args.port
        app = build_http_app()
        logger.info(
            "Starting Bridge Hub on http://%s:%d (WS: /ws, MCP: /mcp, REST: /api/status, auto_exit=%s)",
            args.host,
            args.port,
            not args.keep_alive,
        )
        asyncio.run(_run_daemon_with_watchdog(app, args.host, args.port, args.keep_alive))


if __name__ == "__main__":
    main()
