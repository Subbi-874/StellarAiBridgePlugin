import argparse
import asyncio
import ctypes
from ctypes import wintypes
from datetime import datetime
import json
import logging
from logging.handlers import RotatingFileHandler
import math
import os
from pathlib import Path
import random
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

# Fallback Python-side player tracker used only when an older plugin DLL (pre-v0.3.0) is still loaded in memory
_py_follow_task: asyncio.Task[None] | None = None
_py_follow_snapshot: dict[str, Any] | None = None
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
        Path(r"E:\StarLauncher\game\release_3.7\game_mini\stellar\plugins\agybridge"),
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
                (slot_dir / ".plugin-version").write_text("0.9.14", encoding="utf-8")
                logger.info("Auto-deployed latest Stellar.AgyBridge.dll (v0.9.14) to %s", dest)
        except OSError as exc:
            logger.debug("Auto-deploy DLL skipped for %s: %s", slot_dir, exc)


def _lua_quote(value: str) -> str:
    escaped = (
        value.replace("\\", "\\\\")
        .replace("'", "\\'")
        .replace("\n", "\\n")
        .replace("\r", "\\r")
    )
    return f"'{escaped}'"


def _plugin_supports(method: str, conn_id: str | None = None) -> bool:
    return hub.client_supports(method, conn_id=conn_id)


def _cancel_py_follow(reason: str = "stopped") -> dict[str, Any] | None:
    global _py_follow_task, _py_follow_snapshot
    snap = None
    if _py_follow_task and not _py_follow_task.done():
        _py_follow_task.cancel()
    _py_follow_task = None
    if _py_follow_snapshot and not _py_follow_snapshot.get("finished"):
        _py_follow_snapshot["phase"] = reason
        _py_follow_snapshot["finished"] = True
        snap = dict(_py_follow_snapshot)
    return snap


async def _raw_hub_call(
    method: str,
    params: dict[str, Any] | None = None,
    timeout: float | None = None,
    conn_id: str | None = None,
) -> dict[str, Any]:
    return await hub.call(method, params or {}, timeout=timeout or config.DEFAULT_RPC_TIMEOUT, conn_id=conn_id)


async def _fallback_get_guild_members(online_only: bool = True) -> dict[str, Any]:
    code = f"""
local onlineOnly = {'true' if online_only else 'false'}
local uvm = Z.VMMgr.GetVM('union')
local udata = Z.DataMgr.Get('union_data')
if not uvm or not udata then
  return {{ in_guild = false, union_id = 0, members = {{}} }}
end
local uid = uvm.GetPlayerUnionId and uvm:GetPlayerUnionId() or 0
if uid <= 0 then
  return {{ in_guild = false, union_id = 0, members = {{}} }}
end
local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
local list = {{}}
if type(udata.MemberDict) == 'table' then
  for k, m in pairs(udata.MemberDict) do
    local bd = m.socialData and m.socialData.basicData or m.basicData
    if bd then
      local isOnline = ((tonumber(bd.offlineTime or 0) or 0) == 0)
      if (not onlineOnly) or isOnline then
        local cid = tonumber(bd.charID or k) or 0
        local sid = tonumber(bd.sceneId or 0) or 0
        local sName = ''
        if stm and sid > 0 then
          pcall(function()
            local r = stm.GetRow(sid)
            if r and r.Name then sName = tostring(r.Name) end
          end)
        end
        list[#list + 1] = {{
          char_id = cid,
          name = tostring(bd.name or ''),
          level = tonumber(bd.level or 0) or 0,
          is_online = isOnline,
          scene_id = sid,
          scene_name = sName
        }}
      end
    end
  end
end
table.sort(list, function(a, b)
  if a.is_online ~= b.is_online then return a.is_online end
  return a.name < b.name
end)
return {{
  in_guild = true,
  union_id = uid,
  union_name = uvm.GetPlayerUnionName and uvm:GetPlayerUnionName() or '',
  members = list
}}
"""
    ev = await _raw_hub_call("lua_eval", {"code": code})
    if ev.get("ok") and isinstance(ev.get("result"), dict):
        return {"ok": True, **ev["result"]}
    return ev


async def _fallback_get_social_roster(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    """Unified Party + Friends + Guild UID roster via Lua for in-memory pre-v0.6.6 game clients."""
    category = str(params.get("category") or "all").strip().lower()
    query = str(params.get("query") or "").strip().lower()
    online_only = bool(params.get("online_only", False))

    st_resp = await _raw_hub_call("get_status", {}, conn_id=conn_id)
    self_cid = int(((st_resp.get("state") or {}).get("player") or {}).get("char_id") or 0)
    party_resp = await _raw_hub_call("get_party", {"refresh_line": False}, conn_id=conn_id)

    merged: dict[int, dict[str, Any]] = {}

    def _merge(uid: int, name: str, online: bool, in_p: bool, is_f: bool, in_g: bool, sid: int, sname: str) -> None:
        if uid <= 0 or (self_cid > 0 and uid == self_cid):
            return
        clean_nm = (name or "").strip()
        prev = merged.get(uid)
        if prev:
            if (not prev["name"] or prev["name"].startswith("#")) and clean_nm:
                prev["name"] = clean_nm
            prev["is_online"] = prev["is_online"] or online
            prev["in_party"] = prev["in_party"] or in_p
            prev["is_friend"] = prev["is_friend"] or is_f
            prev["in_guild"] = prev["in_guild"] or in_g
            if prev["scene_id"] <= 0 and sid > 0:
                prev["scene_id"] = sid
            if not prev["scene_name"] and sname:
                prev["scene_name"] = sname
        else:
            merged[uid] = {
                "uid": uid,
                "char_id": uid,
                "name": clean_nm or f"#{uid}",
                "is_online": online,
                "in_party": in_p,
                "is_friend": is_f,
                "in_guild": in_g,
                "scene_id": sid,
                "scene_name": sname,
            }

    for pm in party_resp.get("members") or []:
        if pm.get("is_self"):
            continue
        _merge(
            int(pm.get("char_id") or 0),
            str(pm.get("name") or ""),
            bool(pm.get("is_online", True)),
            True,
            False,
            False,
            int(pm.get("scene_id") or 0),
            str(pm.get("scene_name") or ""),
        )

    code = f"""
local selfCid = {self_cid}
local friends, guild = {{}}, {{}}
local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
local function sceneName(sid)
  if stm and sid > 0 then
    local okR, r = pcall(function() return stm.GetRow(sid) end)
    if okR and r and r.Name then return tostring(r.Name) end
  end
  return ''
end

local okF, fdata = pcall(function() return Z.DataMgr.Get('friend_main_data') end)
local fvm = Z.VMMgr and Z.VMMgr.GetVM and Z.VMMgr.GetVM('friends_main')
if okF and fdata then
  if (not fdata.IsCache) and fvm and fvm.AsyncGetFriendBaseData then
    Z.CoroUtil.create_coro_xpcall(function() pcall(function() fvm.AsyncGetFriendBaseData() end) end)()
  end
  if fdata.GetFriendCharList and fdata.GetFriendDataByCharId then
    local okL, cids = pcall(function() return fdata:GetFriendCharList() end)
    if okL and type(cids) == 'table' then
      for _, rawCid in pairs(cids) do
        local cidNum = tonumber(rawCid) or 0
        if cidNum > 0 and cidNum ~= selfCid then
          local okD, fd = pcall(function() return fdata:GetFriendDataByCharId(cidNum) end)
          if okD and fd and not fd.isBlack_ then
            local uid = tonumber(fd.charId_ or (fd.GetCharId and fd:GetCharId()) or cidNum) or cidNum
            local nm = tostring(fd.playerName_ or (fd.GetPlayerName and fd:GetPlayerName()) or '')
            local offT = tonumber(fd.offLineTime_ or (fd.GetPlayerOffLineTime and fd:GetPlayerOffLineTime()) or 1) or 1
            local sid = tonumber(fd.sceneId_ or (fd.GetPlayerSceneId and fd:GetPlayerSceneId()) or 0) or 0
            if uid > 0 and uid ~= selfCid then
              friends[#friends + 1] = {{ uid = uid, name = nm, online = (offT == 0), scene_id = sid, scene_name = sceneName(sid) }}
            end
          end
        end
      end
    end
  end
end

local uvm = Z.VMMgr and Z.VMMgr.GetVM and Z.VMMgr.GetVM('union')
local udata = Z.DataMgr and Z.DataMgr.Get and Z.DataMgr.Get('union_data')
if uvm and udata then
  local unionId = uvm.GetPlayerUnionId and uvm:GetPlayerUnionId() or 0
  if unionId > 0 and type(udata.MemberDict) == 'table' then
    local gCount = 0
    for _ in pairs(udata.MemberDict) do gCount = gCount + 1 break end
    if gCount == 0 and uvm.AsyncReqUnionMemsList then
      Z.CoroUtil.create_coro_xpcall(function()
        local tok = (udata.CancelSource and udata.CancelSource:CreateToken()) or Z.CancelSource.Rent():CreateToken()
        uvm:AsyncReqUnionMemsList(unionId, tok)
      end)()
    end
    for k, m in pairs(udata.MemberDict) do
      local bd = m.socialData and m.socialData.basicData or m.basicData
      if bd then
        local uid = tonumber(bd.charID or k) or 0
        if uid > 0 and uid ~= selfCid then
          local nm = tostring(bd.name or '')
          local offT = tonumber(bd.offlineTime or 0) or 0
          local sid = tonumber(bd.sceneId or 0) or 0
          guild[#guild + 1] = {{ uid = uid, name = nm, online = (offT == 0), scene_id = sid, scene_name = sceneName(sid) }}
        end
      end
    end
  end
end
return {{ friends = friends, guild = guild }}
"""
    ev = await _raw_hub_call("lua_eval", {"code": code}, conn_id=conn_id)
    r_obj = ev.get("result") if ev.get("ok") and isinstance(ev.get("result"), dict) else {}
    for fo in r_obj.get("friends") or []:
        if isinstance(fo, dict):
            _merge(
                int(fo.get("uid") or 0),
                str(fo.get("name") or ""),
                bool(fo.get("online", False)),
                False,
                True,
                False,
                int(fo.get("scene_id") or 0),
                str(fo.get("scene_name") or ""),
            )
    for go in r_obj.get("guild") or []:
        if isinstance(go, dict):
            _merge(
                int(go.get("uid") or 0),
                str(go.get("name") or ""),
                bool(go.get("online", False)),
                False,
                False,
                True,
                int(go.get("scene_id") or 0),
                str(go.get("scene_name") or ""),
            )

    def _badge(e: dict[str, Any]) -> str:
        p, f, g = e["in_party"], e["is_friend"], e["in_guild"]
        if p and f and g:
            return "[파티·친구·길드]"
        if p and f:
            return "[파티·친구]"
        if p and g:
            return "[파티·길드]"
        if f and g:
            return "[친구·길드]"
        if p:
            return "[파티]"
        if f:
            return "[친구]"
        if g:
            return "[길드]"
        return "[기타]"

    out: list[dict[str, Any]] = []
    for item in merged.values():
        if online_only and not item["is_online"]:
            continue
        if category in ("party", "파티") and not item["in_party"]:
            continue
        if category in ("friend", "friends", "친구") and not item["is_friend"]:
            continue
        if category in ("guild", "union", "길드") and not item["in_guild"]:
            continue
        if query and query not in item["name"].lower() and query not in str(item["uid"]):
            continue
        item["source_badge"] = _badge(item)
        out.append(item)

    out.sort(
        key=lambda x: (
            not x["in_party"],
            not x["is_online"],
            not x["is_friend"],
            str(x["name"]).lower(),
            int(x["uid"]),
        )
    )
    return {"ok": True, "category": category, "count": len(out), "members": out}


async def _resolve_any_player_via_lua(query: str, self_cid: int) -> dict[str, Any] | None:
    # Check party first
    party = await _raw_hub_call("get_party", {"refresh_line": False})
    q_trim = query.strip()
    if "#" in q_trim:
        nm_part, uid_part = q_trim.rsplit("#", 1)
        if uid_part.strip().isdigit() and int(uid_part.strip()) > 0:
            q_trim = uid_part.strip()
    q_low = q_trim.lower()
    for m in party.get("members") or []:
        if m.get("is_self"):
            continue
        if q_low in ("leader", "파티장", "리더") and m.get("is_leader"):
            return {"cid": int(m["char_id"]), "name": m.get("name", ""), "source": "party"}
        if q_trim.isdigit() and int(m["char_id"]) == int(q_trim):
            return {"cid": int(m["char_id"]), "name": m.get("name", ""), "source": "party"}
        if str(m.get("name", "")).lower() == q_low or q_low in str(m.get("name", "")).lower():
            return {"cid": int(m["char_id"]), "name": m.get("name", ""), "source": "party"}

    if q_trim.isdigit() and int(q_trim) != self_cid:
        return {"cid": int(q_trim), "name": f"#{q_trim}", "source": "char_id"}

    q_lua = _lua_quote(q_trim)
    code = f"""
local q = tostring({q_lua}):lower()
local selfCid = {self_cid}

local okF, fdata = pcall(function() return Z.DataMgr.Get('friend_main_data') end)
if okF and fdata and fdata.GetFriendCharList and fdata.GetFriendDataByCharId then
  local fExact, fPartial = nil, nil
  local okL, cids = pcall(function() return fdata:GetFriendCharList() end)
  if okL and type(cids) == 'table' then
    for _, rawCid in pairs(cids) do
      local cidNum = tonumber(rawCid) or 0
      if cidNum > 0 and cidNum ~= selfCid then
        local okD, fd = pcall(function() return fdata:GetFriendDataByCharId(cidNum) end)
        if okD and fd and not fd.isBlack_ then
          local cid = tonumber(fd.charId_ or (fd.GetCharId and fd:GetCharId()) or cidNum) or cidNum
          local nm = tostring(fd.playerName_ or (fd.GetPlayerName and fd:GetPlayerName()) or '')
          local on = ((tonumber(fd.offLineTime_ or (fd.GetPlayerOffLineTime and fd:GetPlayerOffLineTime()) or 1) or 1) == 0)
          if cid > 0 and cid ~= selfCid and nm ~= '' then
            local nml = nm:lower()
            if nml == q then
              if not fExact or (on and not fExact.online) then fExact = {{ cid = cid, name = nm, online = on, source = 'friend' }} end
            elseif nml:find(q, 1, true) then
              if not fPartial or (on and not fPartial.online) then fPartial = {{ cid = cid, name = nm, online = on, source = 'friend' }} end
            end
          end
        end
      end
    end
  end
  if fExact and fExact.online then return fExact end
  if fPartial and fPartial.online then return fPartial end
end

local udata = Z.DataMgr.Get('union_data')
if udata and type(udata.MemberDict) == 'table' then
  local exact, partial = nil, nil
  for k, m in pairs(udata.MemberDict) do
    local bd = m.socialData and m.socialData.basicData or m.basicData
    if bd then
      local cid = tonumber(bd.charID or k) or 0
      local nm = tostring(bd.name or '')
      local on = ((tonumber(bd.offlineTime or 0) or 0) == 0)
      if cid > 0 and cid ~= selfCid and nm ~= '' then
        local nml = nm:lower()
        if nml == q then
          if not exact or (on and not exact.online) then
            exact = {{ cid = cid, name = nm, online = on, source = 'guild' }}
          end
        elseif nml:find(q, 1, true) then
          if not partial or (on and not partial.online) then
            partial = {{ cid = cid, name = nm, online = on, source = 'guild' }}
          end
        end
      end
    end
  end
  if exact then return exact end
  if partial then return partial end
end
return nil
"""
    ev = await _raw_hub_call("lua_eval", {"code": code})
    res = ev.get("result") if ev.get("ok") else None
    if isinstance(res, dict) and res.get("cid"):
        return res

    chats = await _raw_hub_call("get_chats", {"limit": 30, "include_system": False})
    for msg in reversed(chats.get("messages") or []):
        sid = int(msg.get("sender_id") or 0)
        sname = str(msg.get("sender_name") or "")
        if sid > 0 and sid != self_cid and q_low in sname.lower():
            return {"cid": sid, "name": sname, "source": "chat"}
    return None


async def _query_social_scene(cid: int) -> dict[str, Any] | None:
    key = f"__agyPySd_{cid}"
    start_code = f"""
rawset(_G, '{key}', nil)
Z.CoroUtil.create_coro_xpcall(function()
  local svm = Z.VMMgr.GetVM('social')
  local mask = svm.GetSocialDataTypeMask(Z.ConstValue.SocialDataType.SocialDataTypeBase, 0)
  mask = svm.GetSocialDataTypeMask(Z.ConstValue.SocialDataType.SocialDataTypeScene, mask)
  local tok = Z.CancelSource.Rent():CreateToken()
  local sd = svm.AsyncGetSocialData(mask, {cid}, tok)
  if not sd or not sd.basicData then
    rawset(_G, '{key}', {{ ok = false }})
    return
  end
  local b = sd.basicData
  local sc = sd.sceneData
  local sid = tonumber(b.sceneId or 0) or 0
  local lmap = sc and tonumber(sc.levelMapId or 0) or 0
  local mapId = sc and tonumber(sc.mapId or 0) or 0
  local p = (sid > 0 and sid == lmap and sc and sc.levelPos) and sc.levelPos or (sc and sc.pos or nil)
  rawset(_G, '{key}', {{
    ok = true,
    name = tostring(b.name or ''),
    online = ((tonumber(b.offlineTime or 0) or 0) == 0),
    scene_id = sid > 0 and sid or (lmap > 0 and lmap or mapId),
    line_id = sc and tonumber(sc.lineId or 0) or 0,
    x = p and tonumber(p.x or 0) or 0,
    y = p and tonumber(p.y or 0) or 0,
    z = p and tonumber(p.z or 0) or 0
  }})
end)()
return true
"""
    await _raw_hub_call("lua_eval", {"code": start_code})
    for _ in range(12):
        await asyncio.sleep(0.25)
        poll = await _raw_hub_call(
            "lua_eval",
            {"code": f"local v = rawget(_G, '{key}') if v then rawset(_G, '{key}', nil) end return v"},
        )
        val = poll.get("result") if poll.get("ok") else None
        if isinstance(val, dict):
            return val if val.get("ok") else None
    return None


async def _query_aoi_pos(cid: int) -> dict[str, float] | None:
    code = f"""
local uuid = {cid} * 65536 + 640
if Z.EntityMgr then
  local okE, ent = pcall(function() return Z.EntityMgr:GetEntity(uuid) end)
  if okE and ent then
    local okP, vp = pcall(function() return ent:GetLocalAttrVirtualPos() end)
    if okP and vp then
      return {{ x = tonumber(vp.x or 0) or 0, y = tonumber(vp.y or 0) or 0, z = tonumber(vp.z or 0) or 0 }}
    end
  end
end
return nil
"""
    ev = await _raw_hub_call("lua_eval", {"code": code})
    res = ev.get("result") if ev.get("ok") else None
    return res if isinstance(res, dict) and "x" in res else None


async def _fallback_follow_loop(
    target_query: str,
    stop_distance: float,
    continuous: bool,
    auto_teleport: bool,
    auto_switch_channel: bool,
) -> None:
    global _py_follow_snapshot
    try:
        st_resp = await _raw_hub_call("get_status")
        self_cid = int(st_resp.get("state", {}).get("player", {}).get("char_id") or 0)
        resolved = await _resolve_any_player_via_lua(target_query, self_cid)
        if not resolved:
            if _py_follow_snapshot:
                _py_follow_snapshot.update({"phase": "target_not_found", "finished": True})
            return

        cid = int(resolved["cid"])
        name = str(resolved.get("name") or f"#{cid}")
        source = str(resolved.get("source") or "guild")
        if _py_follow_snapshot:
            _py_follow_snapshot.update({"target_char_id": cid, "target_name": name, "target_source": source})

        last_social_ts = 0.0
        social_info: dict[str, Any] | None = None
        last_goal: tuple[float, float] | None = None
        holding = False

        while True:
            st_resp = await _raw_hub_call("get_status")
            st = st_resp.get("state") or {}
            if st.get("phase") != "World" or st.get("is_loading") or not st.get("player", {}).get("is_available"):
                holding = False
                last_goal = None
                if _py_follow_snapshot:
                    _py_follow_snapshot["phase"] = "loading_transition"
                await asyncio.sleep(0.8)
                continue

            my_scene = int(st.get("scene_id") or 0)
            my_line = int(st.get("line_id") or 0)
            my_pos = st.get("player", {}).get("pos") or {"x": 0.0, "y": 0.0, "z": 0.0}

            aoi = await _query_aoi_pos(cid)
            now = time.monotonic()
            if aoi is None and (social_info is None or now - last_social_ts >= 4.5):
                fetched = await _query_social_scene(cid)
                last_social_ts = now
                if fetched:
                    social_info = fetched
                    if fetched.get("name"):
                        name = str(fetched["name"])

            if social_info and not social_info.get("online", True):
                await _raw_hub_call("stop", {"reason": "target_offline"})
                if _py_follow_snapshot:
                    _py_follow_snapshot.update({"phase": "target_offline", "finished": not continuous})
                if not continuous:
                    return
                await asyncio.sleep(2.0)
                continue

            target_scene = my_scene if aoi else int((social_info or {}).get("scene_id") or 0)
            target_line = my_line if aoi else int((social_info or {}).get("line_id") or 0)
            if _py_follow_snapshot:
                _py_follow_snapshot.update(
                    {"target_name": name, "target_scene_id": target_scene, "target_line_id": target_line}
                )

            # 1. Cross-map / hub transition
            if target_scene > 0 and my_scene > 0 and target_scene != my_scene:
                holding = False
                last_goal = None
                if not auto_teleport:
                    if _py_follow_snapshot:
                        _py_follow_snapshot["phase"] = "waiting_same_scene"
                    await asyncio.sleep(1.0)
                    continue

                if _py_follow_snapshot:
                    _py_follow_snapshot["phase"] = "requesting_map_teleport"
                tp = await _raw_hub_call("teleport_scene", {"scene_id": target_scene, "char_id": cid})
                if tp.get("async_id"):
                    await await_async_rpc(tp["async_id"])
                await wait_for_scene_ready(my_scene, expected_scene_id=target_scene, timeout=25.0)
                await asyncio.sleep(1.5)
                last_social_ts = 0.0
                continue

            # 2. Channel mismatch on channeled maps
            if aoi is None and target_line > 0 and my_line > 0 and target_line != my_line:
                holding = False
                last_goal = None
                if auto_switch_channel:
                    if _py_follow_snapshot:
                        _py_follow_snapshot["phase"] = "requesting_channel_switch"
                    ch = await _raw_hub_call("switch_channel", {"char_id": cid, "line_id": target_line})
                    if ch.get("async_id"):
                        await await_async_rpc(ch["async_id"])
                    await asyncio.sleep(4.0)
                    last_social_ts = 0.0
                    continue

            # 3. Same scene pursuit
            tx = float(aoi["x"]) if aoi else float((social_info or {}).get("x") or 0.0)
            ty = float(aoi["y"]) if aoi else float((social_info or {}).get("y") or 0.0)
            tz = float(aoi["z"]) if aoi else float((social_info or {}).get("z") or 0.0)
            if tx == 0.0 and tz == 0.0 and aoi is None:
                await asyncio.sleep(0.5)
                continue

            dist = math.hypot(tx - float(my_pos.get("x") or 0.0), tz - float(my_pos.get("z") or 0.0))
            if _py_follow_snapshot:
                _py_follow_snapshot["distance"] = round(dist, 2)

            resume_dist = stop_distance + 1.0
            if holding:
                if dist <= resume_dist:
                    if _py_follow_snapshot:
                        _py_follow_snapshot["phase"] = "holding_in_range"
                    await asyncio.sleep(0.35)
                    continue
                holding = False

            if dist <= stop_distance:
                await _raw_hub_call("stop", {"reason": "reached_target"})
                last_goal = None
                if not continuous:
                    if _py_follow_snapshot:
                        _py_follow_snapshot.update(
                            {
                                "phase": "arrived_target",
                                "detail": f"Arrived at {name} ({dist:.2f}m, one-shot)",
                                "finished": True,
                            }
                        )
                    return
                holding = True
                if _py_follow_snapshot:
                    _py_follow_snapshot.update(
                        {"phase": "holding_in_range", "detail": f"Reached {name} ({dist:.2f}m)"}
                    )
                await asyncio.sleep(0.35)
                continue

            beh_active = bool(st_resp.get("behavior", {}).get("active"))
            goal_shifted = last_goal is None or math.hypot(tx - last_goal[0], tz - last_goal[1]) > 0.65
            if goal_shifted or not beh_active:
                await _raw_hub_call(
                    "move_to",
                    {"x": tx, "y": ty, "z": tz, "stop_distance": stop_distance, "timeout_sec": 90.0},
                )
                last_goal = (tx, tz)

            if _py_follow_snapshot:
                _py_follow_snapshot.update(
                    {
                        "phase": "following_live_aoi" if aoi else "approaching_far_same_scene",
                        "detail": f"Moving toward {name} ({dist:.1f}m)",
                    }
                )
            await asyncio.sleep(0.3)
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        logger.warning("Fallback follow loop error: %s", exc)
        if _py_follow_snapshot:
            _py_follow_snapshot.update({"phase": "error", "detail": str(exc), "finished": True})


async def dispatch_game_rpc(
    method: str,
    params: dict[str, Any] | None = None,
    timeout: float | None = None,
    conn_id: str | None = None,
) -> dict[str, Any]:
    """Unified dispatcher handling native C# RPCs plus transparent compatibility for pre-v0.3.0 in-memory plugins."""
    global _py_follow_task, _py_follow_snapshot
    params = params or {}
    active_cid = conn_id or hub.status_summary().get("active_conn_id")

    if method == "send_chat":
        ch_raw = str(params.get("channel") or "party").strip().lower()
        ch_pol_map = {
            "world": "send_world",
            "월드": "send_world",
            "전체": "send_world",
            "say": "send_say",
            "local": "send_say",
            "current": "send_say",
            "일반": "send_say",
            "guild": "send_guild",
            "union": "send_guild",
            "길드": "send_guild",
            "party": "send_party",
            "team": "send_party",
            "파티": "send_party",
            "whisper": "send_whisper",
            "private": "send_whisper",
            "귓속말": "send_whisper",
        }
        pol_key = ch_pol_map.get(ch_raw, "send_party")
        # Defense-in-depth: also enforce send_world/send_say AdminLock(2) & profanity guard on Python bridge side
        if not _plugin_supports("get_feature_policies", active_cid) and chat_monitor is not None:
            fmodes = chat_monitor.status(conn_id=active_cid).get("feature_modes") or {}
            mode_val = int(fmodes.get(pol_key, 1))
            if mode_val != 1:
                return {
                    "ok": False,
                    "channel": ch_raw,
                    "admin_locked": mode_val == 2,
                    "error": f"'{pol_key}' 발신 채널이 {'어드민락(2)' if mode_val == 2 else '비활성(0)'} 상태라 차단되었습니다.",
                }
            msg_txt = str(params.get("text") or "")
            gdir = str((hub.status_summary().get("plugin_hello") or {}).get("game_dir") or "")
            bad_words: list[str] = []
            if gdir:
                cfg_file = Path(gdir) / "stellar" / "plugins" / "stellar.agybridge.config.json"
                if cfg_file.exists():
                    try:
                        raw_bw = (json.loads(cfg_file.read_text(encoding="utf-8")).get("bridge") or {}).get("blocked_chat_words") or ""
                        bad_words = [w.strip() for w in str(raw_bw).split(",") if w.strip()]
                    except Exception:
                        pass
            if not bad_words and os.getenv("BRIDGE_BLOCKED_WORDS"):
                bad_words = [w.strip() for w in os.getenv("BRIDGE_BLOCKED_WORDS", "").split(",") if w.strip()]
            compact = "".join(ch.lower() for ch in msg_txt if ch.isalnum())
            for bad in bad_words:
                bl = bad.lower()
                if bl in msg_txt.lower() or bl in compact:
                    return {
                        "ok": False,
                        "channel": ch_raw,
                        "error": f"금칙어('{bad}')가 포함되어 전송이 차단되었습니다.",
                    }

    if method in ("stop", "move_to", "teleport_place", "teleport_scene", "leave_hub", "follow_party", "follow_player"):
        if method == "stop":
            stopped_py = _cancel_py_follow(str(params.get("reason") or "user_stop"))
            res = await _raw_hub_call("stop", params, timeout, conn_id=conn_id)
            if stopped_py:
                res["stopped_follow_player"] = stopped_py
            return res
        _cancel_py_follow(f"preempted_by_{method}")

    if method == "get_status":
        res = await _raw_hub_call("get_status", params, timeout, conn_id=conn_id)
        if _py_follow_snapshot and not _py_follow_snapshot.get("finished"):
            res["behavior"] = {"active": True, "kind": "follow_player", "current": dict(_py_follow_snapshot)}
        elif _py_follow_snapshot and res.get("behavior", {}).get("kind") == "idle":
            res["behavior"]["last_follow_player"] = dict(_py_follow_snapshot)
        return res

    if method == "get_guild_members" and not _plugin_supports("get_guild_members", conn_id):
        return await _fallback_get_guild_members(bool(params.get("online_only", True)))

    if method == "get_social_roster" and not _plugin_supports("get_social_roster", conn_id):
        return await _fallback_get_social_roster(params, conn_id=conn_id)

    if method == "follow_player" and not _plugin_supports("follow_player"):
        target = str(params.get("target") or "leader")
        stop_dist = float(params.get("stop_distance") or 2.5)
        continuous = bool(params.get("continuous", True))
        auto_tp = bool(params.get("auto_teleport", True))
        auto_ch = bool(params.get("auto_switch_channel", True))
        _py_follow_snapshot = {
            "kind": "follow_player",
            "phase": "resolving_target",
            "detail": f"Resolving '{target}'",
            "finished": False,
            "continuous": continuous,
            "target_query": target,
            "stop_distance": stop_dist,
            "auto_teleport": auto_tp,
            "auto_switch_channel": auto_ch,
        }
        _py_follow_task = asyncio.create_task(
            _fallback_follow_loop(target, stop_dist, continuous, auto_tp, auto_ch)
        )
        await asyncio.sleep(0.4)
        return {"ok": True, "behavior": dict(_py_follow_snapshot)}

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

    if method == "party_action" and not _plugin_supports("party_action", conn_id):
        return await _fallback_party_action(params)

    # Pre-v0.9.0 in-memory compatibility for transfer_leader pre-checks, dungeons, ready_check, and stickers
    if (
        method == "party_action"
        and str(params.get("action") or "").strip().lower() in ("transfer_leader", "leader", "delegate")
        and not _plugin_supports("enter_dungeon", conn_id)
    ):
        return await _compat_transfer_leader(params, conn_id=conn_id)

    if method == "list_dungeons" and not hub.client_version_at_least((0, 9, 2), conn_id):
        return await _fallback_list_dungeons(params, conn_id=conn_id)

    if method == "enter_dungeon" and not hub.client_version_at_least((0, 9, 2), conn_id):
        gate_err = _check_feature_gate_py("dungeon_enter", active_cid)
        if gate_err is not None:
            return gate_err
        return await _fallback_enter_dungeon(params, conn_id=conn_id)

    if method == "get_ready_status" and not _plugin_supports("get_ready_status", conn_id):
        return _fallback_get_ready_status(active_cid)

    if method == "ready_check" and not _plugin_supports("ready_check", conn_id):
        fmodes = (chat_monitor.status(conn_id=active_cid).get("feature_modes") or {}) if chat_monitor else {}
        if int(fmodes.get("auto_ready", 0)) == 2:
            return {
                "ok": False,
                "policy_key": "auto_ready",
                "mode": 2,
                "admin_locked": True,
                "error": "'던전·레이드·월드보스 자동 준비' 기능이 어드민락(🔒) 상태라 실행할 수 없음",
            }
        return await _fallback_ready_check(conn_id=conn_id)

    if method == "list_channels" and not hub.client_version_at_least((0, 9, 3), conn_id):
        return await _fallback_list_channels(conn_id=conn_id)

    if method == "switch_channel" and not hub.client_version_at_least((0, 9, 3), conn_id):
        return await _fallback_switch_channel(params, conn_id=conn_id, active_cid=active_cid)

    if method == "list_fishing_spots" and not hub.client_version_at_least((0, 9, 3), conn_id):
        return await _fallback_list_fishing_spots(params, conn_id=conn_id)

    if method == "fish" and not hub.client_version_at_least((0, 9, 3), conn_id):
        gate_err = _check_feature_gate_py("auto_fish", active_cid)
        if gate_err is not None:
            return gate_err
        await _raw_hub_call("stop", {"reason": "fish"}, conn_id=conn_id)
        return {
            "ok": False,
            "error": "자동 낚시 연동(v0.9.3) 업데이트가 빌드 완료되어 대기 중입니다. 게임을 한 번 재실행해 v0.9.3 플러그인이 적용되면 바로 사용할 수 있어요.",
        }

    if method in ("leave_dungeon", "leave_hub") and not hub.client_version_at_least((0, 9, 5), conn_id):
        policy_key = "dungeon_enter" if method == "leave_dungeon" else "teleport_channel"
        gate_err = _check_feature_gate_py(policy_key, active_cid)
        if gate_err is not None:
            return gate_err
        await _raw_hub_call("stop", {"reason": method}, conn_id=conn_id)
        return await _fallback_leave_dungeon(conn_id=conn_id)

    if method == "list_gimmicks" and not hub.client_version_at_least((0, 9, 5), conn_id):
        return await _fallback_list_gimmicks(params, conn_id=conn_id)

    if method == "use_gimmick" and not hub.client_version_at_least((0, 9, 5), conn_id):
        gate_err = _check_feature_gate_py("follow_move", active_cid)
        if gate_err is not None:
            return gate_err
        await _raw_hub_call("stop", {"reason": "use_gimmick"}, conn_id=conn_id)
        return await _fallback_use_gimmick(params, conn_id=conn_id)

    if method == "list_stickers" and not hub.client_version_at_least((0, 9, 2), conn_id):
        return await _fallback_list_stickers(params, conn_id=conn_id)

    if method == "send_sticker" and not hub.client_version_at_least((0, 9, 2), conn_id):
        return await _fallback_send_sticker(params, conn_id=conn_id, active_cid=active_cid)

    if method == "list_emotes" and not _plugin_supports("list_emotes", conn_id):
        return await _fallback_list_emotes(params, conn_id=conn_id)

    if method == "play_emote":
        res = (
            await _raw_hub_call(method, params, timeout, conn_id=conn_id)
            if _plugin_supports("play_emote", conn_id)
            else await _fallback_play_emote(params, conn_id=conn_id)
        )
        # Retry once after 0.35s if character was just finishing a step/deceleration
        if not res.get("ok") and not res.get("skipped_unowned") and "상태" in str(res.get("error") or ""):
            await asyncio.sleep(0.35)
            res = (
                await _raw_hub_call(method, params, timeout, conn_id=conn_id)
                if _plugin_supports("play_emote", conn_id)
                else await _fallback_play_emote(params, conn_id=conn_id)
            )
        return res

    return await _raw_hub_call(method, params, timeout, conn_id=conn_id)


async def _fallback_list_emotes(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    q_lua = _lua_quote(str(params.get("query") or "").strip())
    code = f"""
local rawQuery = {q_lua}
local function norm(s)
  return (tostring(s or ''):lower():gsub('[%s%._%-]', ''):gsub('・', ''):gsub('·', ''))
end
local qn = norm(rawQuery)
local evm = Z.VMMgr.GetVM('expression')
if not evm or not evm.GetExpressionShowDataByType then
  return {{ emotes = {{}}, count = 0 }}
end
local list, seen = {{}}, {{}}
for _, dispType in ipairs({{ 1, 2 }}) do
  local okL, items = pcall(function() return evm.GetExpressionShowDataByType(dispType, false) end)
  if okL and type(items) == 'table' then
    for _, it in ipairs(items) do
      local tr = it and it.tableData
      if tr and tr.Id and not seen[tr.Id] and (it.activeType == nil or it.activeType == 1) then
        local nm = tostring(tr.Name or '')
        local et = tonumber(tr.EmoteType or dispType) or dispType
        if qn == '' or norm(nm):find(qn, 1, true) or tostring(tr.Id) == qn then
          seen[tr.Id] = true
          list[#list + 1] = {{ id = tr.Id, name = nm, emote_type = et, is_loop = (et == 2) }}
        end
      end
    end
  end
end
table.sort(list, function(a, b) return a.id < b.id end)
return {{ emotes = list, count = #list }}
"""
    ev = await _raw_hub_call("lua_eval", {"code": code}, conn_id=conn_id)
    if ev.get("ok") and isinstance(ev.get("result"), dict):
        return {"ok": True, **ev["result"]}
    return ev


async def _fallback_play_emote(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    q_trim = str(params.get("emote") or params.get("query") or "").strip()
    explicit_id = int(params.get("emote_id") or params.get("id") or 0)
    stop_flag = bool(params.get("stop", False))

    if stop_flag or q_trim in ("중지", "정지", "그만", "취소", "일어나", "일어서", "stop", "cancel"):
        stop_code = """
local pe = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if pe and pe.Model and pe.Model.GetLuaAttrActionInfoActionId then
  local myAid = math.floor((tonumber(pe.Model:GetLuaAttrActionInfoActionId() or 0) or 0) + 0.5)
  if myAid > 0 then
    pcall(function()
      local apm = Panda and Panda.ZAnim and Panda.ZAnim.ZAnimActionPlayMgr and Panda.ZAnim.ZAnimActionPlayMgr.Instance
      if apm then apm:ResetAction() end
    end)
  end
end
return { ok = true, stopped = true }
"""
        ev = await _raw_hub_call("lua_eval", {"code": stop_code}, conn_id=conn_id)
        if ev.get("ok") and isinstance(ev.get("result"), dict):
            return ev["result"]
        return ev

    q_lua = _lua_quote(q_trim)
    code = f"""
local rawQuery = {q_lua}
local explicitId = {explicit_id}
local function norm(s)
  return (tostring(s or ''):lower():gsub('[%s%._%-]', ''):gsub('・', ''):gsub('·', ''))
end
local function stripNumSuffix(s)
  return (norm(s):gsub('%d+$', ''))
end
local pe = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if not pe or not pe.Model then
  return {{ ok = false, error = 'Character entity not ready in world' }}
end
local evm = Z.VMMgr.GetVM('expression')
if not evm or not evm.GetExpressionShowDataByType or not evm.PlayAction then
  return {{ ok = false, error = 'expression_vm unavailable' }}
end
local unlocked, lockedExact, byId = {{}}, {{}}, {{}}
for _, dispType in ipairs({{ 1, 2 }}) do
  local okL, items = pcall(function() return evm.GetExpressionShowDataByType(dispType, true) end)
  if okL and type(items) == 'table' then
    for _, it in ipairs(items) do
      local tr = it and it.tableData
      if tr and tr.Id then
        local nm = tostring(tr.Name or '')
        local nn = norm(nm)
        local bn = stripNumSuffix(nm)
        if it.activeType == nil or it.activeType == 1 then
          if not byId[tr.Id] then
            local et = tonumber(tr.EmoteType or dispType) or dispType
            local entry = {{ id = tr.Id, name = nm, normName = nn, baseName = bn, emoteType = et }}
            byId[tr.Id] = entry
            unlocked[#unlocked + 1] = entry
          end
        else
          if nn ~= '' then lockedExact[nn] = nm end
          if bn ~= '' then lockedExact[bn] = nm end
        end
      end
    end
  end
end
local target = nil
local qNum = explicitId > 0 and explicitId or tonumber(rawQuery)
if qNum and qNum > 0 then
  target = byId[math.floor(qNum + 0.5)]
end
local qn = norm(rawQuery)
if not target and qn ~= '' then
  for _, e in ipairs(unlocked) do
    if e.normName == qn then target = e break end
  end
  if not target then
    for _, e in ipairs(unlocked) do
      if e.baseName == qn then target = e break end
    end
  end
  if not target and lockedExact[qn] then
    return {{ ok = false, skipped_unowned = true, query = rawQuery, locked_name = lockedExact[qn], error = '미보유(미해금) 감정표현이므로 스킵했습니다: ' .. tostring(lockedExact[qn]) }}
  end
  if not target then
    local bestLen = 999999
    for _, e in ipairs(unlocked) do
      if e.normName ~= '' and e.normName:find(qn, 1, true) then
        if #e.normName < bestLen then
          bestLen = #e.normName
          target = e
        end
      end
    end
  end
end
if not target then
  return {{ ok = false, skipped_unowned = true, query = rawQuery, error = '보유(해금)한 감정표현 중 일치하는 항목이 없어 스킵했습니다.' }}
end
if evm.CanPlayActionCheck and pe.GetLuaAttrState then
  local okC, c = pcall(function() return evm.CanPlayActionCheck(pe:GetLuaAttrState()) end)
  if okC and c == false then
    return {{ ok = false, error = '현재 캐릭터 상태에서는 감정표현을 사용할 수 없습니다.', id = target.id, name = target.name }}
  end
end
if evm.CheckEmoteCondition then
  local okCond, cond = pcall(function() return evm.CheckEmoteCondition(target.id, false) end)
  if okCond and cond == false then
    return {{ ok = false, error = '현재 위치/상태에서는 해당 감정표현 조건이 충족되지 않습니다.', id = target.id, name = target.name }}
  end
end
local okPlay, errPlay = pcall(function() evm.PlayAction(target.id, true, false) end)
return {{
  ok = okPlay,
  id = target.id,
  name = target.name,
  emote_type = target.emoteType,
  is_loop = (target.emoteType == 2),
  error = okPlay and '' or tostring(errPlay)
}}
"""
    ev = await _raw_hub_call("lua_eval", {"code": code}, conn_id=conn_id)
    if ev.get("ok") and isinstance(ev.get("result"), dict):
        return ev["result"]
    return ev


async def _fallback_party_action(params: dict[str, Any]) -> dict[str, Any]:
    """Lua coroutine fallback for party management when a pre-v0.6.0 plugin DLL is still loaded in memory."""
    act = str(params.get("action") or "").strip().lower()
    auto_accept = bool(params.get("auto_accept", False))
    leave_first = bool(params.get("leave_first", True))
    target_q = str(params.get("target") or "").strip()
    explicit_tid = int(params.get("team_id") or 0)

    st = await _raw_hub_call("get_status", {})
    self_cid = int(((st.get("state") or {}).get("player") or {}).get("char_id") or 0)
    resolved_cid = 0
    if target_q:
        if target_q.isdigit():
            resolved_cid = int(target_q)
        else:
            found = await _resolve_any_player_via_lua(target_q, self_cid)
            if found:
                resolved_cid = int(found["cid"])

    act_lua = _lua_quote(act)
    code = f"""
rawset(_G, '__agyPartyRes', nil)
Z.CoroUtil.create_coro_xpcall(function()
  local act = {act_lua}
  local autoAccept = {'true' if auto_accept else 'false'}
  local leaveFirst = {'true' if leave_first else 'false'}
  local enableOpt = {'1' if params.get('enable', auto_accept) else '0'}
  local targetCid = {resolved_cid}
  local explicitTid = {explicit_tid}

  local tvm = Z.VMMgr.GetVM('team')
  local svm = Z.VMMgr.GetVM('social')
  local tdata = Z.DataMgr.Get('team_data')
  if not tvm or not tdata then
    rawset(_G, '__agyPartyRes', {{ ok = false, error = 'team_vm unavailable' }})
    return
  end

  local cs = Z.CancelSource.Rent()
  local inTeam = tvm.CheckIsInTeam and tvm.CheckIsInTeam() or false

  if act == 'leave' or act == 'quit' or act == 'leave_party' then
    if not inTeam then
      cs:Recycle()
      rawset(_G, '__agyPartyRes', {{ ok = true, already_solo = true }})
      return
    end
    local okQ, errQ = pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
    cs:Recycle()
    rawset(_G, '__agyPartyRes', {{ ok = okQ, action = 'leave', error = okQ and '' or tostring(errQ) }})
    return
  end

  if act == 'create' or act == 'create_party' then
    if inTeam then
      if leaveFirst then
        pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
      else
        if tvm.GetYouIsLeader and tvm.GetYouIsLeader() then
          pcall(function() tvm.AsyncSetTeamOption(1, autoAccept and 1 or 0, cs:CreateToken()) end)
          cs:Recycle()
          rawset(_G, '__agyPartyRes', {{ ok = true, already_in_party = true, auto_accept = autoAccept }})
          return
        end
        cs:Recycle()
        rawset(_G, '__agyPartyRes', {{ ok = false, error = 'Already in a party' }})
        return
      end
    end
    local opts = {{ [1] = autoAccept and 1 or 0 }}
    local okC, errC = pcall(function() tvm.AsyncCreatTeam(1000, cs:CreateToken(), nil, '', opts) end)
    if okC and autoAccept and tvm.AsyncSetTeamOption then
      pcall(function() tvm.AsyncSetTeamOption(1, 1, cs:CreateToken()) end)
    end
    local tid = tdata.TeamInfo and tdata.TeamInfo.baseInfo and tdata.TeamInfo.baseInfo.teamId or 0
    cs:Recycle()
    rawset(_G, '__agyPartyRes', {{ ok = okC, action = 'create', team_id = tid, auto_accept = autoAccept, error = okC and '' or tostring(errC) }})
    return
  end

  if act == 'set_auto_accept' or act == 'auto_accept' then
    if not inTeam then
      cs:Recycle()
      rawset(_G, '__agyPartyRes', {{ ok = false, error = 'Not in a party' }})
      return
    end
    local okS, errS = pcall(function() tvm.AsyncSetTeamOption(1, enableOpt, cs:CreateToken()) end)
    cs:Recycle()
    rawset(_G, '__agyPartyRes', {{ ok = okS, auto_accept = (enableOpt == 1), error = okS and '' or tostring(errS) }})
    return
  end

  if act == 'join' or act == 'join_party' or act == 'apply' then
    local teamId = explicitTid
    if teamId <= 0 and targetCid > 0 and svm and svm.AsyncGetSocialData then
      local okSd, sd = pcall(function() return svm.AsyncGetSocialData(1026, targetCid, cs:CreateToken()) end)
      if okSd and sd and sd.teamData then
        teamId = tonumber(sd.teamData.teamId or 0) or 0
      end
    end
    if teamId <= 0 then
      cs:Recycle()
      rawset(_G, '__agyPartyRes', {{ ok = false, error = 'Target player is not in any party', target_char_id = targetCid }})
      return
    end
    local myTid = tdata.TeamInfo and tdata.TeamInfo.baseInfo and tonumber(tdata.TeamInfo.baseInfo.teamId or 0) or 0
    if myTid == teamId and myTid > 0 then
      cs:Recycle()
      rawset(_G, '__agyPartyRes', {{ ok = true, already_in_target_party = true, team_id = teamId }})
      return
    end
    if myTid > 0 and leaveFirst then
      pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
    end
    local okA, errA = pcall(function() tvm.AsyncApplyJoinTeam(teamId, cs:CreateToken()) end)
    cs:Recycle()
    rawset(_G, '__agyPartyRes', {{ ok = okA, applied_team_id = teamId, target_char_id = targetCid, error = okA and '' or tostring(errA) }})
    return
  end

  if act == 'invite' or act == 'invite_player' then
    if targetCid <= 0 then
      cs:Recycle()
      rawset(_G, '__agyPartyRes', {{ ok = false, error = 'Could not resolve target player' }})
      return
    end
    if not inTeam then
      pcall(function() tvm.AsyncCreatTeam(1000, cs:CreateToken(), nil, '', {{ [1] = 1 }}) end)
    end
    local okI, errI = pcall(function() tvm.AsyncInviteToTeam(targetCid, cs:CreateToken()) end)
    cs:Recycle()
    rawset(_G, '__agyPartyRes', {{ ok = okI, invited_char_id = targetCid, error = okI and '' or tostring(errI) }})
    return
  end

  cs:Recycle()
  rawset(_G, '__agyPartyRes', {{ ok = false, error = 'Unsupported fallback party action: ' .. tostring(act) }})
end)()
return {{ started = true }}
"""
    await _raw_hub_call("lua_eval", {"code": code})
    for _ in range(20):
        await asyncio.sleep(0.25)
        poll = await _raw_hub_call("lua_eval", {"code": "local r = rawget(_G, '__agyPartyRes') if r then rawset(_G, '__agyPartyRes', nil) end return r"})
        if isinstance(poll.get("result"), dict):
            return poll["result"]
    return {"ok": True, "pending": True, "action": act}


# ---------------------------------------------------------------------------
# Pre-v0.9.0 in-memory compatibility layer (auto-disabled once v0.9.0 DLL loads)
# ---------------------------------------------------------------------------

_SEND_CHANNEL_MAP: dict[str, tuple[str, int]] = {
    "world": ("send_world", 1),
    "월드": ("send_world", 1),
    "전체": ("send_world", 1),
    "say": ("send_say", 2),
    "local": ("send_say", 2),
    "current": ("send_say", 2),
    "일반": ("send_say", 2),
    "party": ("send_party", 3),
    "team": ("send_party", 3),
    "파티": ("send_party", 3),
    "guild": ("send_guild", 4),
    "union": ("send_guild", 4),
    "길드": ("send_guild", 4),
    "whisper": ("send_whisper", 5),
    "private": ("send_whisper", 5),
    "귓속말": ("send_whisper", 5),
}

_STICKER_MOOD_SLOTS: dict[str, list[int]] = {
    "greet": [1, 2],
    "hello": [1, 2],
    "인사": [1, 2],
    "반가움": [1, 2],
    "thanks": [2, 3, 4],
    "감사": [2, 3, 4],
    "고마움": [2, 3, 4],
    "thumbsup": [3, 4, 1],
    "good": [3, 4, 1],
    "ok": [1, 3, 4],
    "확인": [1, 3, 4],
    "따봉": [3, 4],
    "최고": [3, 4],
    "cheer": [3, 4, 5],
    "응원": [3, 4, 5],
    "축하": [3, 4, 5],
    "heart": [2, 4, 5],
    "love": [2, 4, 5],
    "하트": [2, 4, 5],
    "좋아": [2, 4, 5],
    "laugh": [5, 3, 2],
    "happy": [2, 3, 5],
    "웃음": [5, 3, 2],
    "기쁨": [2, 3, 5],
    "cute": [1, 2, 4],
    "귀여움": [1, 2, 4],
    "think": [6, 7],
    "고민": [6, 7],
    "궁금": [6, 7],
    "sorry": [7, 6, 8],
    "sweat": [7, 6],
    "사과": [7, 6, 8],
    "미안": [7, 6, 8],
    "당황": [6, 7],
    "sad": [7, 8],
    "cry": [7, 8],
    "슬픔": [7, 8],
    "울음": [7, 8],
    "angry": [8, 6],
    "화남": [8, 6],
}

_py_ready_stats: dict[str, dict[str, Any]] = {}


def _check_feature_gate_py(policy_key: str, active_cid: str | None) -> dict[str, Any] | None:
    if chat_monitor is None:
        return None
    fmodes = chat_monitor.status(conn_id=active_cid).get("feature_modes") or {}
    mode_val = int(fmodes.get(policy_key, 1))
    if mode_val != 1:
        return {
            "ok": False,
            "policy_key": policy_key,
            "mode": mode_val,
            "admin_locked": mode_val == 2,
            "error": f"'{policy_key}' 기능이 {'어드민락(🔒)' if mode_val == 2 else '비활성'} 상태라 실행할 수 없어요.",
        }
    return None


async def _run_lua_coro(key: str, body_lua: str, conn_id: str | None = None, timeout_sec: float = 7.0) -> dict[str, Any]:
    wrapped = (
        f"rawset(_G, '{key}', nil)\n"
        f"Z.CoroUtil.create_coro_xpcall(function()\n"
        f"  local ok, res = pcall(function()\n{body_lua}\n  end)\n"
        f"  if not ok then rawset(_G, '{key}', {{ ok = false, error = tostring(res) }}) else rawset(_G, '{key}', res or {{ ok = true }}) end\n"
        f"end)()\n"
        f"return true"
    )
    ev = await _raw_hub_call("lua_eval", {"code": wrapped}, conn_id=conn_id)
    if not ev.get("ok"):
        return ev
    steps = max(4, int(timeout_sec / 0.25))
    poll_code = f"local r = rawget(_G, '{key}') if r ~= nil then rawset(_G, '{key}', nil) end return r"
    for _ in range(steps):
        await asyncio.sleep(0.25)
        poll = await _raw_hub_call("lua_eval", {"code": poll_code}, conn_id=conn_id)
        if poll.get("ok") and isinstance(poll.get("result"), dict):
            return poll["result"]
    return {"ok": True, "pending": True}


async def _compat_transfer_leader(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    active_cid = conn_id or hub.status_summary().get("active_conn_id")
    gate_err = _check_feature_gate_py("party_manage", active_cid)
    if gate_err is not None:
        return gate_err

    st = await _raw_hub_call("get_status", {}, conn_id=conn_id)
    self_cid = int(((st.get("state") or {}).get("player") or {}).get("char_id") or 0)
    party = await _raw_hub_call("get_party", {"refresh_line": False}, conn_id=conn_id)
    members = party.get("members") or []
    in_party = bool(party.get("in_party")) or len(members) > 0
    if not in_party:
        return {"ok": False, "reason_code": "not_in_party", "error": "파티에 속해 있지 않아서 파티장을 위임할 수 없어요"}

    leader_cid = int(party.get("leader_char_id") or 0)
    if leader_cid <= 0:
        for m in members:
            if m.get("is_leader"):
                leader_cid = int(m.get("char_id") or 0)
                break
    if self_cid > 0 and leader_cid != self_cid:
        return {"ok": False, "reason_code": "not_leader", "error": "제가 파티장이 아니라서 파티장을 위임할 수 없어요"}

    raw_target = str(params.get("target") or "").strip()
    if not raw_target:
        return {"ok": False, "error": "파티장을 위임할 대상(이름 또는 UID)을 알려주세요"}

    q_trim = raw_target
    if "#" in q_trim:
        _, uid_part = q_trim.rsplit("#", 1)
        if uid_part.strip().isdigit():
            q_trim = uid_part.strip()
    q_low = q_trim.lower()

    matched_member: dict[str, Any] | None = None
    for m in members:
        mcid = int(m.get("char_id") or 0)
        mnm = str(m.get("name") or "")
        if q_trim.isdigit() and mcid == int(q_trim):
            matched_member = m
            break
        if mnm and (mnm.lower() == q_low or q_low in mnm.lower()):
            matched_member = m
            break

    if matched_member is None:
        return {
            "ok": False,
            "reason_code": "target_not_in_party",
            "target": raw_target,
            "error": f"'{raw_target}'님이 현재 같은 파티에 없어서 파티장을 위임할 수 없어요",
        }

    target_cid = int(matched_member.get("char_id") or 0)
    if target_cid == self_cid:
        return {"ok": True, "action": "transfer_leader", "already_leader": True, "target_char_id": target_cid}

    if _plugin_supports("party_action", conn_id):
        return await _raw_hub_call(
            "party_action",
            {"action": "transfer_leader", "target": str(target_cid)},
            conn_id=conn_id,
        )

    lua_body = f"""
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncTransferLeader then
  return {{ ok = false, error = 'AsyncTransferLeader unavailable' }}
end
local cs = Z.CancelSource.Rent()
local okT, errT = pcall(function() tvm.AsyncTransferLeader({target_cid}, cs:CreateToken()) end)
cs:Recycle()
return {{ ok = okT, action = 'transfer_leader', target_char_id = {target_cid}, error = okT and '' or tostring(errT) }}
"""
    return await _run_lua_coro("__agyPyTransferLeader", lua_body, conn_id=conn_id)


_OPEN_DUNGEON_POOL_HELPER_LUA = """
local function norm(s)
  s = tostring(s or ''):lower()
  s = s:gsub('[%s%p·・!~<>%(%)]+', '')
  return s
end

local function buildOpenDungeonPool()
  local dtm = Z.TableMgr.GetTable('DungeonsTableMgr')
  local mcdt = Z.TableMgr.GetTable('MasterChallengeDungeonTableMgr')
  local sat = Z.TableMgr.GetTable('SeasonActTableMgr')
  local rpd = Z.DataMgr.Get('recommendedplay_data')
  local rpvm = Z.VMMgr.GetVM('recommendedplay')
  local svm = Z.VMMgr.GetVM('season')

  local curSeason = 0
  if svm and svm.GetCurrentSeasonId then
    local okS, sVal = pcall(svm.GetCurrentSeasonId)
    if okS and tonumber(sVal or 0) > 0 then curSeason = tonumber(sVal) end
  end
  if curSeason <= 0 and rpd and tonumber(rpd.seasonId_ or 0) > 0 then
    curSeason = tonumber(rpd.seasonId_)
  end

  local openUnstableFuncs = {}
  local openUnstableDids = {}
  local openUnstableNames = {}
  local openExtremityFuncs = {}
  local openDirectDids = {}
  local openActNames = {}
  local openActFuncs = {}

  local function isActOpen(actRow)
    if type(actRow) ~= 'table' then return false end
    local aid = tonumber(actRow.Id or 0) or 0
    if rpvm and rpvm.GetActivityState and aid > 0 then
      local okSt, st = pcall(function() return rpvm.GetActivityState(aid) end)
      if okSt and tonumber(st or -1) ~= 1 then return false end
    end
    local tid = tonumber(actRow.OpenTimerId or 0) or 0
    if tid > 0 and Z.TimeTools and Z.TimeTools.CheckIsInTimeByTimeId then
      local okTm, inTm = pcall(function() return Z.TimeTools.CheckIsInTimeByTimeId(tid) end)
      if okTm and inTm == false then return false end
    end
    return true
  end

  if rpd and rpd.GetSecondTagsByType then
    for _, firstType in ipairs({ 1, 2, 3, 4, 5 }) do
      local okSec, secs = pcall(function() return rpd:GetSecondTagsByType(firstType) end)
      if okSec and type(secs) == 'table' then
        for _, s in pairs(secs) do
          if type(s) == 'table' then
            local sName = tostring(s.Name or '')
            local sFunc = tonumber(s.FunctionId or 0) or 0
            local isUnstableGroup = (sFunc == 300200) or (sName:find('불안정', 1, true) ~= nil)
            local isExtremityGroup = (sName:find('극한', 1, true) ~= nil)

            local okTh, thirds = pcall(function() return rpd:GetThirdTagsById(s.Id) end)
            if okTh and type(thirds) == 'table' and next(thirds) ~= nil then
              for _, t in pairs(thirds) do
                if type(t) == 'table' and isActOpen(t) then
                  local tfid = tonumber(t.FunctionId or 0) or 0
                  local relDid = tonumber(t.RelatedDungeonId or 0) or 0
                  local tName = tostring(t.Name or '')
                  if isUnstableGroup then
                    local okCdo, cdo = pcall(function() return rpd:checkDungeonOpen(t) end)
                    if (not okCdo) or cdo == true then
                      if tfid > 0 then openUnstableFuncs[tfid] = true end
                      if relDid > 0 then openUnstableDids[relDid] = true end
                      if tName ~= '' then openUnstableNames[#openUnstableNames + 1] = tName end
                    end
                  elseif isExtremityGroup then
                    if tfid > 0 then openExtremityFuncs[tfid] = true end
                    if relDid > 0 then openDirectDids[relDid] = true end
                  else
                    if relDid > 0 then openDirectDids[relDid] = true end
                    if tfid > 0 then openActFuncs[tfid] = true end
                    if tName ~= '' then openActNames[norm(tName)] = tName end
                  end
                end
              end
            else
              if isActOpen(s) then
                local relDid = tonumber(s.RelatedDungeonId or 0) or 0
                if relDid > 0 then openDirectDids[relDid] = true end
                if sFunc > 0 then openActFuncs[sFunc] = true end
                if sName ~= '' then openActNames[norm(sName)] = sName end
              end
            end
          end
        end
      end
    end
  end

  local masterMaxByDid = {}
  local masterCurSeasonDids = {}
  if mcdt and mcdt.GetDatas then
    for _, mr in pairs(mcdt.GetDatas()) do
      local did = tonumber(mr.DungeonId or 0) or 0
      local df = tonumber(mr.Difficulty or 0) or 0
      local mSeason = tonumber(mr.SeasonId or 0) or 0
      if did > 0 and (curSeason <= 0 or mSeason == curSeason) then
        masterCurSeasonDids[did] = true
        if df > (masterMaxByDid[did] or 0) then masterMaxByDid[did] = df end
      end
    end
  end

  local function getCondFuncId(r)
    if type(r.Condition) == 'table' then
      for _, c in ipairs(r.Condition) do
        if type(c) == 'table' and tonumber(c[1] or 0) == 14 then
          return tonumber(c[2] or 0) or 0
        end
      end
    end
    return 0
  end

  local function isDungeonOpenNow(did, r)
    local pt = tonumber(r.PlayType or -1) or -1
    local condFunc = getCondFuncId(r)
    local nm = tostring(r.Name or '')
    local nmNorm = norm(nm)

    if pt == 8 then
      return (condFunc > 0 and openUnstableFuncs[condFunc] == true) or (openUnstableDids[did] == true)
    elseif pt == 17 then
      if not masterCurSeasonDids[did] then return false end
      if next(openExtremityFuncs) ~= nil and condFunc > 0 and not openExtremityFuncs[condFunc] then
        return false
      end
      return true
    elseif pt == 9 then
      return (condFunc > 0 and openExtremityFuncs[condFunc] == true) or (openDirectDids[did] == true)
    elseif pt == 2 then
      return openDirectDids[did] == true
    elseif pt == 18 or pt == 19 or pt == 21 then
      for actNorm, _ in pairs(openActNames) do
        if #actNorm >= 3 and nmNorm:find(actNorm, 1, true) then
          return true
        end
      end
      return openDirectDids[did] == true
    elseif pt == 16 then
      if not (openActFuncs[500888] or openActNames[norm('시디문 유적')]) then return false end
      if Z.ConditionHelper and Z.ConditionHelper.CheckCondition and r.Condition then
        local okC, cPass = pcall(function() return Z.ConditionHelper.CheckCondition(r.Condition, false) end)
        if okC and cPass == false then return false end
      end
      return true
    elseif pt == 13 then
      return (openActFuncs[500151] == true) or (openActNames[norm('길드 사냥')] ~= nil)
    end
    return false
  end

  local wbActId = rpd and tonumber(rpd.worldBossConfigID_ or 0) or 0
  if wbActId <= 0 and sat and sat.GetDatas then
    for id, sr in pairs(sat.GetDatas()) do
      if tonumber(sr.FunctionId or 0) == 800902 then wbActId = tonumber(id) or 0 break end
    end
  end
  if wbActId <= 0 then wbActId = 18 end
  local wbRow = sat and sat.GetRow(wbActId, true) or nil
  local wbOpen = wbRow ~= nil and isActOpen(wbRow)
  if wbOpen and Z.VMMgr then
    local swvm = Z.VMMgr.GetVM('switch')
    if swvm and swvm.CheckFuncSwitch and wbRow.FunctionId and tonumber(wbRow.FunctionId) > 0 then
      local okSw, swPass = pcall(function() return swvm.CheckFuncSwitch(wbRow.FunctionId, false) end)
      if okSw and swPass == false then wbOpen = false end
    end
  end

  return {
    dtm = dtm,
    curSeason = curSeason,
    masterMaxByDid = masterMaxByDid,
    openUnstableNames = openUnstableNames,
    isDungeonOpenNow = isDungeonOpenNow,
    wbActId = wbActId,
    wbOpen = wbOpen
  }
end
"""

_LIST_DUNGEONS_LUA_BODY = _OPEN_DUNGEON_POOL_HELPER_LUA + """
local pool = buildOpenDungeonPool()
if not pool.dtm or not pool.dtm.GetDatas then
  return { season_id = pool.curSeason, dungeons = {}, count = 0 }
end
local q = norm(rawQ)
local list = {}
for id, r in pairs(pool.dtm.GetDatas()) do
  local did = tonumber(id) or 0
  local pt = tonumber(r.PlayType or -1) or -1
  if pool.isDungeonOpenNow(did, r) then
    local nm = tostring(r.Name or '')
    local tn = tostring(r.DungeonTypeName or '')
    local full = tn ~= '' and (nm .. ' (' .. tn .. ')') or nm
    local catOk = true
    if rawCat == 'unstable' then catOk = (pt == 8)
    elseif rawCat == 'raid' then catOk = (pt == 18 or pt == 19 or pt == 21)
    elseif rawCat == 'master' then catOk = (pt == 17)
    end
    if catOk and (q == '' or norm(full):find(q, 1, true)) then
      local lim = type(r.LimitedNum) == 'table' and { r.LimitedNum[1] or 1, r.LimitedNum[2] or 5 } or { 1, 5 }
      list[#list + 1] = {
        id = did,
        name = nm,
        difficulty = tn,
        play_type = pt,
        min_players = lim[1],
        max_players = lim[2],
        max_master_diff = pool.masterMaxByDid[did] or 0
      }
    end
  end
end
table.sort(list, function(a, b) return a.id < b.id end)
if #list > 60 then
  local trimmed = {}
  for i = 1, 60 do trimmed[i] = list[i] end
  list = trimmed
end
return {
  season_id = pool.curSeason,
  today_unstable = pool.openUnstableNames,
  world_boss = { activity_id = pool.wbActId, is_open = pool.wbOpen },
  dungeons = list,
  count = #list
}
"""

_ENTER_DUNGEON_CORO_BODY = _OPEN_DUNGEON_POOL_HELPER_LUA + """
local pool = buildOpenDungeonPool()
if not pool.dtm or not pool.dtm.GetDatas then return { ok = false, error = 'DungeonsTableMgr unavailable' } end

local combined = (rawQuery .. ' ' .. rawDiff)
local allNorm = norm(combined)

if explicitId <= 0 and (allNorm:find('월드보스', 1, true) or allNorm:find('세계의지배자', 1, true)) then
  if not pool.wbOpen then
    return {
      ok = false,
      reason_code = 'world_boss_not_open',
      activity_id = pool.wbActId,
      error = '현재 월드 보스 토벌 오픈 시간이 아니라서 입장할 수 없음'
    }
  end
  local mavm = Z.VMMgr.GetVM('match_activity')
  if mavm and mavm.AsyncBeginMatch then
    local okW, errW = pcall(function() mavm.AsyncBeginMatch(pool.wbActId) end)
    if not okW then return { ok = false, error = '월드 보스 매칭 시작 실패: ' .. tostring(errW) } end
    return { ok = true, mode = 'world_boss_match', activity_id = pool.wbActId, name = '월드 보스 토벌' }
  end
  local wp = require('zproxy.world_proxy')
  local cs = Z.CancelSource.Rent()
  local okB, retB = pcall(function() return wp.BeginWorldBossMatch({}, cs:CreateToken()) end)
  cs:Recycle()
  if not okB then return { ok = false, error = '월드 보스 입장 요청 실패: ' .. tostring(retB) } end
  local ec = type(retB) == 'table' and tonumber(retB.errCode or 0) or 0
  if ec ~= 0 then return { ok = false, err_code = ec, error = '월드 보스 입장 조건 미충족 (errCode=' .. tostring(ec) .. ')' } end
  return { ok = true, mode = 'world_boss_match', activity_id = pool.wbActId, name = '월드 보스 토벌' }
end

local wantUnstable = allNorm:find('불안정', 1, true) ~= nil
local wantMaster = (allNorm:find('마스터', 1, true) ~= nil) or (allNorm:find('master', 1, true) ~= nil)
local masterDiff = 0
if wantMaster then
  local numStr = combined:match('마스터%s*난?이?도?%s*(%d+)')
    or combined:match('[Mm]aster%s*(%d+)')
    or rawDiff:match('(%d+)')
    or combined:match('(%d+)%s*단')
  masterDiff = tonumber(numStr or 1) or 1
  if masterDiff < 1 then masterDiff = 1 end
end
local wantHard = (not wantMaster) and (allNorm:find('하드', 1, true) ~= nil or allNorm:find('어려움', 1, true) ~= nil or allNorm:find('hard', 1, true) ~= nil)
local wantNormal = (not wantMaster) and (allNorm:find('노멀', 1, true) ~= nil or allNorm:find('일반', 1, true) ~= nil or allNorm:find('보통', 1, true) ~= nil or allNorm:find('normal', 1, true) ~= nil)
local wantEasy = allNorm:find('이지', 1, true) ~= nil or allNorm:find('쉬움', 1, true) ~= nil or allNorm:find('easy', 1, true) ~= nil
local wantNightmare = allNorm:find('나이트메어', 1, true) ~= nil or allNorm:find('악몽', 1, true) ~= nil
local wantRaidTier = nil
if allNorm:find('결전', 1, true) then wantRaidTier = '결전'
elseif allNorm:find('혈전', 1, true) then wantRaidTier = '혈전'
elseif allNorm:find('회귀', 1, true) then wantRaidTier = '회귀'
elseif allNorm:find('격투', 1, true) then wantRaidTier = '격투' end
local wantErosion = allNorm:find('침식', 1, true) ~= nil

local cleanSearch = rawQuery
for _, tok in ipairs({
  '불안정 공간', '불안정공간', '불안정',
  '마스터 난이도', '마스터난이도', '마스터', 'master',
  '나이트메어', '노멀', '일반', '하드', '어려움', '이지', '쉬움',
  '회귀', '혈전', '결전', '격투', '레이드'
}) do
  cleanSearch = cleanSearch:gsub(tok, ' ')
end
cleanSearch = cleanSearch:gsub('%d+%s*단', ' ')
local searchNorm = norm(cleanSearch)
local searchNoUi = norm(cleanSearch:gsub('의%s+', ' '):gsub('의', ''))

local chosen = nil
if explicitId > 0 then
  local rowById = pool.dtm.GetRow(explicitId, true)
  if not rowById then return { ok = false, error = '지정한 던전 ID(' .. tostring(explicitId) .. ')를 찾을 수 없음' } end
  if not pool.isDungeonOpenNow(explicitId, rowById) then
    return {
      ok = false,
      reason_code = 'dungeon_not_open',
      dungeon_id = explicitId,
      error = "'" .. tostring(rowById.Name or explicitId) .. "' 던전은 현재 시즌 또는 오늘 오픈된 목록에 없어 입장할 수 없음"
    }
  end
  chosen = rowById
else
  local bestScore = -999999
  for id, r in pairs(pool.dtm.GetDatas()) do
    local did = tonumber(id) or 0
    if pool.isDungeonOpenNow(did, r) then
      local pt = tonumber(r.PlayType or -1) or -1
      local nm = tostring(r.Name or '')
      local tn = tostring(r.DungeonTypeName or '')
      local nmNorm = norm(nm)
      local nmNoUi = norm(nm:gsub('의', ''))
      local tnNorm = norm(tn)
      local score = -999999
      local typeAllowed = true
      if wantUnstable and pt ~= 8 then typeAllowed = false end
      if (not wantUnstable) and pt == 8 and searchNorm ~= '' then typeAllowed = false end
      if wantMaster and pt ~= 17 then typeAllowed = false end
      if (not wantMaster) and pt == 17 then typeAllowed = false end
      if typeAllowed then
        local nameMatched = false
        if wantUnstable and searchNorm == '' then
          nameMatched = true
          score = 100
        elseif searchNorm ~= '' then
          if nmNorm == searchNorm or nmNoUi == searchNoUi then nameMatched, score = true, 500
          elseif nmNorm:find(searchNorm, 1, true) or (searchNoUi ~= '' and nmNoUi:find(searchNoUi, 1, true)) then nameMatched, score = true, 300
          elseif #nmNorm >= 3 and (searchNorm:find(nmNorm, 1, true) or allNorm:find(nmNorm, 1, true)) then nameMatched, score = true, 220 end
        end
        if nameMatched then
          local isErosionRow = nm:find('침식', 1, true) ~= nil
          if wantErosion and isErosionRow then score = score + 80
          elseif (not wantErosion) and isErosionRow then score = score - 25 end

          local diffOk = true
          if wantMaster then
            if (pool.masterMaxByDid[did] or 0) < masterDiff then diffOk = false else score = score + 200 end
          elseif wantRaidTier then
            if nm:find(wantRaidTier, 1, true) then score = score + 200 else diffOk = false end
          elseif wantNightmare then
            if nmNorm:find('나이트메어', 1, true) or tnNorm:find('나이트메어', 1, true) or nm:find('결전', 1, true) then score = score + 200 else diffOk = false end
          elseif wantHard then
            if tnNorm:find('하드', 1, true) or nmNorm:find('하드', 1, true) or nm:find('혈전', 1, true) then score = score + 200 else diffOk = false end
          elseif wantEasy then
            if tnNorm:find('이지', 1, true) or nmNorm:find('이지', 1, true) or nm:find('회귀', 1, true) or nm:find('격투', 1, true) then score = score + 200 else diffOk = false end
          elseif wantNormal then
            if tnNorm:find('노멀', 1, true) or nmNorm:find('노멀', 1, true) or (tn == '' and pt == 2) then score = score + 200 else diffOk = false end
          else
            if tnNorm:find('노멀', 1, true) or nm:find('회귀', 1, true) or nm:find('격투', 1, true) or nmNorm:find('이지', 1, true) then score = score + 45 end
          end

          if diffOk then
            local lim = type(r.LimitedNum) == 'table' and r.LimitedNum or nil
            local minP = lim and tonumber(lim[1] or 1) or 1
            local maxP = lim and tonumber(lim[2] or 5) or 5
            if partyCount >= minP and partyCount <= maxP then score = score + 160
            elseif wantSingleAi and minP <= 5 then score = score + 40
            else score = score - 80 end
            if score > bestScore then bestScore, chosen = score, r end
          end
        end
      end
    end
  end
end

if not chosen then
  if wantUnstable and #pool.openUnstableNames > 0 then
    return {
      ok = false,
      reason_code = 'unstable_not_open_today',
      today_unstable = pool.openUnstableNames,
      error = "'" .. rawQuery .. "' 불안정 공간은 오늘 열려 있지 않음 (오늘 오픈된 불안정 공간: " .. table.concat(pool.openUnstableNames, ', ') .. ")"
    }
  end
  return {
    ok = false,
    reason_code = 'dungeon_not_open',
    season_id = pool.curSeason,
    error = "'" .. combined:gsub('^%s+', ''):gsub('%s+$', '') .. "' 던전/레이드는 현재 시즌(S" .. tostring(pool.curSeason) .. ") 또는 오늘 오픈된 목록에 없어 입장할 수 없음"
  }
end

local targetDid = tonumber(chosen.Id or 0) or 0
local targetFuncId = tonumber(chosen.FunctionId or 0) or 0
local targetPt = tonumber(chosen.PlayType or 0) or 0
local targetName = tostring(chosen.Name or '')
local targetTypeName = tostring(chosen.DungeonTypeName or '')
local displayLabel = targetTypeName ~= '' and (targetName .. ' (' .. targetTypeName .. ')') or targetName
if wantMaster and masterDiff > 0 then
  displayLabel = targetName .. ' (마스터 ' .. tostring(masterDiff) .. '단)'
end

if targetPt == 17 and masterDiff > 0 then
  local maxCfg = pool.masterMaxByDid[targetDid] or 20
  if masterDiff > maxCfg then
    return { ok = false, error = targetName .. ' 최대 마스터 단수는 ' .. tostring(maxCfg) .. '단임' }
  end
  local hdvm = Z.VMMgr.GetVM('hero_dungeon_main')
  if hdvm and hdvm.GetMasterDungeonMaxDiff then
    local okU, unlockedMax = pcall(function() return hdvm.GetMasterDungeonMaxDiff(targetDid) end)
    if okU and type(unlockedMax) == 'number' and unlockedMax >= 0 and masterDiff > (unlockedMax + 1) then
      return { ok = false, error = displayLabel .. ' 단수가 아직 캐릭터에 해금되지 않음' }
    end
  end
end

local lim = type(chosen.LimitedNum) == 'table' and chosen.LimitedNum or nil
local minPlayers = lim and tonumber(lim[1] or 1) or 1
local maxPlayers = lim and tonumber(lim[2] or 5) or 5

if maxPlayers > 5 and inParty and (not isRaid20) then
  local tvm = Z.VMMgr.GetVM('team')
  if tvm and tvm.AsyncChangeTeamMemberType then
    local csRaid = Z.CancelSource.Rent()
    pcall(function() tvm.AsyncChangeTeamMemberType(1, csRaid:CreateToken()) end)
    csRaid:Recycle()
  end
end

local selectType = 0
if wantSingleAi and partyCount == 1 then
  selectType = 1
elseif partyCount < minPlayers then
  return {
    ok = false,
    reason_code = 'not_enough_players',
    dungeon_id = targetDid,
    min_players = minPlayers,
    party_count = partyCount,
    error = "'" .. displayLabel .. "' 입장은 최소 " .. tostring(minPlayers) .. '명이 필요함 (현재 ' .. tostring(partyCount) .. '명)'
  }
elseif partyCount > maxPlayers then
  return {
    ok = false,
    reason_code = 'too_many_players',
    dungeon_id = targetDid,
    max_players = maxPlayers,
    party_count = partyCount,
    error = "'" .. displayLabel .. "' 최대 입장 인원(" .. tostring(maxPlayers) .. '명) 초과 (현재 ' .. tostring(partyCount) .. '명)'
  }
end

if Z.ConditionHelper and Z.ConditionHelper.CheckCondition and chosen.Condition then
  local okCond, condPass = pcall(function() return Z.ConditionHelper.CheckCondition(chosen.Condition, false) end)
  if okCond and condPass == false then
    return {
      ok = false,
      reason_code = 'condition_not_met',
      dungeon_id = targetDid,
      error = "'" .. displayLabel .. "' 입장 조건(장비 점수·선행 퀘스트 등) 미충족"
    }
  end
end

local affix = nil
if targetPt == 8 or targetPt == 17 then
  local hdvm = Z.VMMgr.GetVM('hero_dungeon_main')
  if hdvm and hdvm.GetAffix then
    local okA, af = pcall(function() return hdvm.GetAffix(targetDid) end)
    if okA and type(af) == 'table' then affix = af end
  end
end

local cs = Z.CancelSource.Rent()
local edvm = Z.VMMgr.GetVM('ui_enterdungeonscene')
if edvm and edvm.AsyncCreateLevel then
  local okE, retE = pcall(function()
    return edvm.AsyncCreateLevel(targetFuncId, targetDid, cs:CreateToken(), affix, 0, selectType, 0, masterDiff)
  end)
  cs:Recycle()
  if not okE then return { ok = false, dungeon_id = targetDid, error = "'" .. displayLabel .. "' 입장 호출 오류: " .. tostring(retE) } end
  if type(retE) == 'table' and tonumber(retE.errCode or 0) ~= 0 then
    return { ok = false, dungeon_id = targetDid, err_code = tonumber(retE.errCode), error = "'" .. displayLabel .. "' 입장 서버 거부 (errCode=" .. tostring(retE.errCode) .. ')' }
  end
  if retE == false then return { ok = false, dungeon_id = targetDid, error = "'" .. displayLabel .. "' 입장 조건 미충족" } end
  return { ok = true, mode = 'dungeon_enter', dungeon_id = targetDid, function_id = targetFuncId, play_type = targetPt, master_diff = masterDiff, name = displayLabel }
end
cs:Recycle()
return { ok = false, error = 'ui_enterdungeonscene VM unavailable' }
"""

_DETECT_READY_LUA = """
local myCid = (Z.ContainerMgr and Z.ContainerMgr.CharSerialize and Z.ContainerMgr.CharSerialize.charBase and Z.ContainerMgr.CharSerialize.charBase.charId) or 0
if Z.UIMgr and Z.UIMgr.IsActive then
  local okTe, activeTe = pcall(function() return Z.UIMgr:IsActive('team_enter') end)
  if okTe and activeTe then
    local tvm = Z.VMMgr.GetVM('team')
    local isLeader = tvm and tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
    if not isLeader then return { kind = 'team_enter' } end
  end
end
local okM, mdata = pcall(function() return Z.DataMgr.Get('match_data') end)
if okM and mdata and tonumber(mdata.matchState_ or 0) == 2 then
  local alreadyReady = false
  pcall(function()
    local pInfo = mdata.GetMatchPlayerInfo and mdata:GetMatchPlayerInfo() or mdata.matchPlayerInfo_
    if type(pInfo) == 'table' and myCid > 0 then
      for k, v in pairs(pInfo) do
        local cid = tonumber((type(v) == 'table' and (v.charId or v.playerId)) or k) or 0
        if cid == myCid and type(v) == 'table' and (v.isReady == true or tonumber(v.readyStatus or v.isReady or 0) == 1) then
          alreadyReady = true
        end
      end
    end
  end)
  if not alreadyReady then return { kind = 'match_ready' } end
end
local okT, tdata = pcall(function() return Z.DataMgr.Get('team_data') end)
if okT and tdata and tdata.IsDungeonPrepareIng == true then
  local tvm = Z.VMMgr.GetVM('team')
  local isLeader = tvm and tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
  if not isLeader then
    local alreadyPrepare = false
    pcall(function()
      if type(tdata.DungeonPrepareCheckInfo) == 'table' and myCid > 0 then
        local mine = tdata.DungeonPrepareCheckInfo[myCid]
        if mine and (mine.isReady == true or tonumber(mine.readyStatus or 0) == 1) then alreadyPrepare = true end
      end
    end)
    if not alreadyPrepare then return { kind = 'dungeon_prepare' } end
  end
end
return { kind = '' }
"""

_CONFIRM_READY_CORO_BODY = """
local handled = {}
local cs = Z.CancelSource.Rent()
pcall(function()
  if Z.UIMgr and Z.UIMgr:IsActive('team_enter') then
    local tevm = Z.VMMgr.GetVM('team_enter')
    if tevm and tevm.AsyncReplyJoinActivity then
      tevm.AsyncReplyJoinActivity(true, cs:CreateToken())
      handled[#handled + 1] = 'team_enter'
    end
  end
end)
pcall(function()
  local mdata = Z.DataMgr.Get('match_data')
  if mdata and tonumber(mdata.matchState_ or 0) == 2 then
    local mvm = Z.VMMgr.GetVM('match')
    if mvm and mvm.AsyncMatchReady then
      mvm.AsyncMatchReady(true)
      handled[#handled + 1] = 'match_ready'
    end
  end
end)
pcall(function()
  local tdata = Z.DataMgr.Get('team_data')
  if tdata and tdata.IsDungeonPrepareIng == true then
    local tvm = Z.VMMgr.GetVM('team')
    if tvm and tvm.AsyncTeamMemCallOperator then
      tvm.AsyncTeamMemCallOperator(true, tdata.CancelSource or cs)
      handled[#handled + 1] = 'dungeon_prepare'
    end
  end
end)
cs:Recycle()
if #handled == 0 then
  return { ok = false, error = '현재 진행 중인 준비 확인창이 없어요' }
end
return { ok = true, confirmed = handled }
"""


async def _fallback_list_dungeons(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    q_lua = _lua_quote(str(params.get("query") or params.get("name") or "").strip())
    cat_lua = _lua_quote(str(params.get("category") or "").strip().lower())
    code = f"local rawQ = {q_lua}\nlocal rawCat = {cat_lua}\n" + _LIST_DUNGEONS_LUA_BODY
    ev = await _raw_hub_call("lua_eval", {"code": code}, conn_id=conn_id)
    if ev.get("ok") and isinstance(ev.get("result"), dict):
        return {"ok": True, **ev["result"]}
    return ev


async def _fallback_enter_dungeon(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    st_resp = await _raw_hub_call("get_status", {}, conn_id=conn_id)
    st = st_resp.get("state") or {}
    if st.get("phase") != "World" or st.get("is_loading"):
        return {"ok": False, "error": "현재 로딩 중이거나 월드 상태가 아니라서 입장할 수 없어요"}

    self_cid = int((st.get("player") or {}).get("char_id") or 0)
    party = await _raw_hub_call("get_party", {"refresh_line": False}, conn_id=conn_id)
    members = party.get("members") or []
    in_party = bool(party.get("in_party")) or len(members) > 0
    leader_cid = int(party.get("leader_char_id") or 0)
    if leader_cid <= 0:
        for m in members:
            if m.get("is_leader"):
                leader_cid = int(m.get("char_id") or 0)
                break

    if in_party and self_cid > 0 and leader_cid != self_cid:
        return {
            "ok": False,
            "reason_code": "not_leader",
            "error": "현재 내 캐릭터가 파티장이 아니라서 입장을 시작할 수 없음 (파티장 위임 필요)",
        }

    raw_query = str(params.get("dungeon") or params.get("name") or params.get("query") or "").strip()
    raw_diff = str(params.get("difficulty") or "").strip()
    explicit_id = int(params.get("dungeon_id") or 0)
    single_ai = bool(params.get("single_ai") or params.get("use_ai") or False)

    if explicit_id <= 0 and not raw_query:
        return {"ok": False, "error": "입장할 던전이나 레이드, 불안정 공간, 월드 보스 이름을 알려주세요"}

    party_count = max(1, len(members)) if in_party else 1
    party_type_str = str((st.get("party") or {}).get("party_type") or party.get("party_type") or "")
    is_raid20 = in_party and ("20" in party_type_str or "raid" in party_type_str.lower())

    header = (
        f"local rawQuery = {_lua_quote(raw_query)}\n"
        f"local rawDiff = {_lua_quote(raw_diff)}\n"
        f"local explicitId = {explicit_id}\n"
        f"local partyCount = {party_count}\n"
        f"local inParty = {'true' if in_party else 'false'}\n"
        f"local isRaid20 = {'true' if is_raid20 else 'false'}\n"
        f"local wantSingleAi = {'true' if single_ai else 'false'}\n"
    )
    return await _run_lua_coro("__agyPyEnterDungeon", header + _ENTER_DUNGEON_CORO_BODY, conn_id=conn_id, timeout_sec=8.0)


def _fallback_get_ready_status(active_cid: str | None) -> dict[str, Any]:
    fmodes = (chat_monitor.status(conn_id=active_cid).get("feature_modes") or {}) if chat_monitor else {}
    mode_val = int(fmodes.get("auto_ready", 0))
    stat = _py_ready_stats.get(active_cid or "", {})
    return {
        "ok": True,
        "enabled": mode_val == 1,
        "admin_locked": mode_val == 2,
        "accepted_total": int(stat.get("accepted_total") or 0),
        "last_kind": str(stat.get("last_kind") or ""),
        "last_accepted_at": stat.get("last_accepted_at"),
    }


async def _fallback_ready_check(conn_id: str | None = None) -> dict[str, Any]:
    res = await _run_lua_coro("__agyPyReadyNow", _CONFIRM_READY_CORO_BODY, conn_id=conn_id, timeout_sec=5.0)
    if res.get("ok") and res.get("confirmed"):
        cid_key = conn_id or hub.status_summary().get("active_conn_id") or ""
        stat = _py_ready_stats.setdefault(cid_key, {"accepted_total": 0, "last_kind": "", "last_accepted_at": None, "cooldown_until": 0.0})
        stat["accepted_total"] = int(stat.get("accepted_total") or 0) + 1
        stat["last_kind"] = ",".join(str(x) for x in (res.get("confirmed") or []))
        stat["last_accepted_at"] = datetime.now().astimezone().isoformat()
        stat["cooldown_until"] = time.monotonic() + 2.8
    return res


async def _fetch_owned_stickers_lua(conn_id: str | None = None) -> list[dict[str, Any]]:
    code = """
local stm = Z.TableMgr.GetTable('ChatStickersTableMgr')
local cvm = Z.VMMgr.GetVM('chat_main')
if not stm or not stm.GetDatas then return {} end
local activeGroups = {}
local hasSortFilter = false
if Z.Global and type(Z.Global.ChatStickersSort) == 'table' then
  for _, entry in ipairs(Z.Global.ChatStickersSort) do
    local gid = tonumber(tostring(entry):match('^(%d+)='))
    if gid and gid > 0 then
      activeGroups[gid] = true
      hasSortFilter = true
    end
  end
end
local list = {}
for id, r in pairs(stm.GetDatas()) do
  local t = tonumber(r.Type or 0) or 0
  local gid = tonumber(r.GroupId or 0) or 0
  local resStr = tostring(r.Res or '')
  if (t == 3 or t == 4) and resStr ~= '' and ((not hasSortFilter) or activeGroups[gid]) then
    local unlocked = false
    if cvm and cvm.GetChatEmojiUnlockByTableRow then
      local okU, u = pcall(function() return cvm.GetChatEmojiUnlockByTableRow(r) end)
      if okU and u == true then unlocked = true end
    else
      unlocked = (tonumber(r.UnlockItem or 0) == 0)
    end
    if unlocked then
      list[#list + 1] = {
        id = tonumber(id) or 0,
        type = t,
        group_id = gid,
        res = resStr,
        default_unlocked = (tonumber(r.UnlockItem or 0) == 0)
      }
    end
  end
end
table.sort(list, function(a, b) return a.id < b.id end)
return list
"""
    ev = await _raw_hub_call("lua_eval", {"code": code}, conn_id=conn_id)
    res = ev.get("result") if ev.get("ok") else None
    if isinstance(res, list):
        return [x for x in res if isinstance(x, dict) and int(x.get("id") or 0) > 0]
    return []


async def _fallback_list_stickers(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    q = str(params.get("query") or "").strip().lower()
    grp_filter = int(params.get("group_id") or 0)
    owned = await _fetch_owned_stickers_lua(conn_id=conn_id)
    filtered: list[dict[str, Any]] = []
    for s in owned:
        if grp_filter > 0 and int(s.get("group_id") or 0) != grp_filter:
            continue
        if q and q not in str(s.get("res") or "").lower() and q not in str(s.get("id") or ""):
            continue
        filtered.append(s)
        if len(filtered) >= 120:
            break
    return {
        "ok": True,
        "owned_count": len(owned),
        "supported_moods": ["greet", "thanks", "thumbsup", "cheer", "heart", "laugh", "cute", "ok", "think", "sorry", "sad", "random"],
        "stickers": filtered,
    }


def _resolve_owned_sticker_py(owned: list[dict[str, Any]], explicit_id: int, query: str) -> dict[str, Any] | None:
    if not owned:
        return None
    if explicit_id > 0:
        for s in owned:
            if int(s.get("id") or 0) == explicit_id:
                return s
    q = (query or "").strip()
    if q.isdigit() and int(q) > 0:
        qid = int(q)
        for s in owned:
            if int(s.get("id") or 0) == qid:
                return s
    q_low = q.lower()
    if q_low and q_low not in ("random", "아무거나", "랜덤"):
        res_matches = [s for s in owned if q_low in str(s.get("res") or "").lower()]
        if res_matches:
            return random.choice(res_matches)
        offsets = _STICKER_MOOD_SLOTS.get(q_low)
        if offsets is None:
            for k, v in _STICKER_MOOD_SLOTS.items():
                if k in q_low:
                    offsets = v
                    break
        if offsets:
            by_group: dict[int, list[dict[str, Any]]] = {}
            for s in owned:
                by_group.setdefault(int(s.get("group_id") or 0), []).append(s)
            candidates: list[dict[str, Any]] = []
            for grp_items in by_group.values():
                grp_items.sort(key=lambda x: int(x.get("id") or 0))
                for one_based in offsets:
                    idx = one_based - 1
                    if 0 <= idx < len(grp_items):
                        candidates.append(grp_items[idx])
            if candidates:
                return random.choice(candidates)
    return random.choice(owned)


async def _fallback_send_sticker(
    params: dict[str, Any],
    conn_id: str | None = None,
    active_cid: str | None = None,
) -> dict[str, Any]:
    ch_raw = str(params.get("channel") or "party").strip().lower()
    pol_key, lua_ch_id = _SEND_CHANNEL_MAP.get(ch_raw, ("send_party", 3))
    gate_err = _check_feature_gate_py(pol_key, active_cid)
    if gate_err is not None:
        gate_err["channel"] = ch_raw
        return gate_err

    target_cid = int(params.get("target_char_id") or 0)
    if lua_ch_id == 5 and target_cid <= 0:
        return {"ok": False, "channel": ch_raw, "error": "귓속말로 이모티콘을 보내려면 target_char_id가 필요해요"}

    explicit_id = int(params.get("sticker_id") or params.get("id") or 0)
    query = str(params.get("sticker") or params.get("mood") or params.get("query") or "random").strip()
    owned = await _fetch_owned_stickers_lua(conn_id=conn_id)
    if not owned:
        return {"ok": False, "channel": ch_raw, "error": "현재 사용할 수 있는 보유 이모티콘(스티커)이 없어요"}

    picked = _resolve_owned_sticker_py(owned, explicit_id, query)
    if picked is None:
        return {"ok": False, "channel": ch_raw, "error": f"요청한 이모티콘('{query}')을 보유 목록에서 찾지 못했어요"}

    picked_id = int(picked["id"])
    if _plugin_supports("send_sticker", conn_id):
        return await _raw_hub_call(
            "send_sticker",
            {"channel": ch_raw, "target_char_id": target_cid, "sticker_id": picked_id},
            conn_id=conn_id,
        )

    target_lua = str(target_cid) if lua_ch_id == 5 else "nil"
    send_body = f"""
local cvm = Z.VMMgr.GetVM('chat_main')
if not cvm or not cvm.AsyncSendMessage then
  return {{ ok = false, error = 'chat_main.AsyncSendMessage unavailable' }}
end
local msgType = (E and E.ChitChatMsgType and E.ChitChatMsgType.EChatMsgPictureEmoji) or 3
local cs = Z.CancelSource.Rent()
local okS, errS = pcall(function()
  cvm.AsyncSendMessage({lua_ch_id}, {target_lua}, '', msgType, {picked_id}, cs:CreateToken())
end)
cs:Recycle()
if not okS then
  return {{ ok = false, error = tostring(errS) }}
end
return {{ ok = true }}
"""
    res = await _run_lua_coro("__agyPySendSticker", send_body, conn_id=conn_id, timeout_sec=5.0)
    return {
        "ok": bool(res.get("ok")),
        "channel": ch_raw,
        "sticker_id": picked_id,
        "res": picked.get("res"),
        "group_id": picked.get("group_id"),
        "remaining_cooldown_sec": 1.25,
        **({"error": res.get("error")} if not res.get("ok") else {}),
    }


async def _fallback_list_channels(conn_id: str | None = None) -> dict[str, Any]:
    body = """
local vm = Z.VMMgr.GetVM('sceneline')
local sld = Z.DataMgr.Get('sceneline_data')
if not vm or not sld then
  return { ok = false, error = 'sceneline VM/data unavailable' }
end
if vm.AsyncReqSceneLineInfo then
  pcall(function() vm.AsyncReqSceneLineInfo() end)
end
local list = (sld.GetLineInfoDataList and sld:GetLineInfoDataList()) or sld.LineDataList or {}
local lines = {}
if type(list) == 'table' then
  for _, it in pairs(list) do
    local lid = tonumber(it.lineId or 0) or 0
    if lid > 0 then
      lines[#lines + 1] = {
        line_id = lid,
        status = tonumber(it.status or 0) or 0,
        scene_guid = tostring(it.sceneGuid or '')
      }
    end
  end
end
table.sort(lines, function(a, b) return a.line_id < b.line_id end)
return {
  ok = true,
  current_line = tonumber(sld.PlayerLineId or 0) or 0,
  recycle_end_time = tonumber(sld.RecycleEndTime or 0) or 0,
  lines = lines,
  count = #lines
}
"""
    return await _run_lua_coro("__agyPyListCh", body, conn_id=conn_id, timeout_sec=6.0)


async def _fallback_switch_channel(
    params: dict[str, Any],
    conn_id: str | None = None,
    active_cid: str | None = None,
) -> dict[str, Any]:
    gate_err = _check_feature_gate_py("teleport_channel", active_cid)
    if gate_err is not None:
        return gate_err

    char_id = int(params.get("char_id") or 0)
    line_id = int(params.get("line_id") or 0)
    raw_target = str(params.get("target") or "").strip()

    if char_id <= 0 and line_id <= 0 and raw_target:
        if raw_target.isdigit() and 1 <= int(raw_target) <= 999:
            line_id = int(raw_target)
        else:
            loc = await _raw_hub_call("query_player_location", {"target": raw_target}, conn_id=conn_id)
            for _ in range(2):
                if not (isinstance(loc, dict) and loc.get("pending")):
                    break
                await asyncio.sleep(0.9)
                loc = await _raw_hub_call("query_player_location", {"target": raw_target}, conn_id=conn_id)
            if isinstance(loc, dict) and loc.get("found"):
                char_id = int(loc.get("char_id") or 0)
                if int(loc.get("line_id") or 0) > 0:
                    line_id = int(loc["line_id"])
            else:
                return {"ok": False, "error": f"'{raw_target}' 대상 또는 유효한 채널 번호를 찾지 못했어요."}

    if char_id <= 0 and line_id <= 0:
        return {"ok": False, "error": "이동할 채널 번호(line_id) 또는 대상(target/char_id)을 지정해주세요."}

    await _raw_hub_call("stop", {"reason": "switch_channel"}, conn_id=conn_id)

    body = f"""
local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
local curSid = (Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId()) or 0
local srow = stm and stm.GetRow and curSid > 0 and stm.GetRow(curSid, true) or nil
local st = srow and tonumber(srow.SceneSubType or 0) or 0
if st == 5 or st == 7 or st == 8 then
  return {{ ok = false, reason_code = 'single_channel_scene', error = '현재 구역은 채널 변경을 지원하지 않음' }}
end
local vm = Z.VMMgr.GetVM('sceneline')
local sld = Z.DataMgr.Get('sceneline_data')
if not vm then return {{ ok = false, error = 'sceneline VM not found' }} end
local curLine = sld and tonumber(sld.PlayerLineId or 0) or 0
local targetCharId = {char_id}
local targetLine = {line_id}
if targetLine > 0 and curLine == targetLine then
  return {{ ok = true, already_on_channel = true, line_id = targetLine, method = 'already_on_channel' }}
end
if targetCharId > 0 and vm.AsyncReqSwitchSceneLineByCharId then
  local okCall, ret = pcall(function() return vm.AsyncReqSwitchSceneLineByCharId(targetCharId) end)
  if not okCall then return {{ ok = false, error = tostring(ret) }} end
  if ret == false or (type(ret) == 'table' and tonumber(ret.errCode or 0) ~= 0) then
    return {{ ok = false, error = '해당 유저 채널로 이동할 수 없음 (혼잡·쿨타임·구역 상이)' }}
  end
  return {{ ok = true, method = 'AsyncReqSwitchSceneLineByCharId', char_id = targetCharId, target_line = targetLine, previous_line = curLine }}
end
if vm.AsyncReqSceneLineInfo then
  pcall(function() vm.AsyncReqSceneLineInfo() end)
end
local list = sld and ((sld.GetLineInfoDataList and sld:GetLineInfoDataList()) or sld.LineDataList) or nil
if type(list) == 'table' then
  local count, foundRow = 0, nil
  for _, it in pairs(list) do
    local lid = tonumber(it.lineId or 0) or 0
    if lid > 0 then
      count = count + 1
      if lid == targetLine then foundRow = it end
    end
  end
  if count > 0 and not foundRow then
    return {{ ok = false, reason_code = 'channel_not_found', line_id = targetLine, error = tostring(targetLine) .. '번 채널은 현재 맵에 존재하지 않음 (현재 ' .. tostring(count) .. '개 채널 존재)' }}
  end
end
if not vm.AsyncReqSwitchSceneLineByLineId then
  return {{ ok = false, error = 'sceneline.AsyncReqSwitchSceneLineByLineId unavailable' }}
end
local okCall, ret = pcall(function() return vm.AsyncReqSwitchSceneLineByLineId(targetLine) end)
if not okCall then return {{ ok = false, error = tostring(ret) }} end
if ret == false or (type(ret) == 'table' and tonumber(ret.errCode or 0) ~= 0) then
  return {{ ok = false, error = tostring(targetLine) .. '번 채널로 이동할 수 없음 (혼잡·쿨타임 상태)' }}
end
return {{ ok = true, method = 'AsyncReqSwitchSceneLineByLineId', line_id = targetLine, previous_line = curLine }}
"""
    return await _run_lua_coro("__agyPySwitchCh", body, conn_id=conn_id, timeout_sec=7.0)


async def _fallback_list_fishing_spots(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    q_lua = _lua_quote(str(params.get("query") or "").strip())
    code = f"""
local rawQ = {q_lua}
local function norm(s)
  s = tostring(s or ''):lower():gsub('아스테리즈', '아스텔리즈'):gsub('순간%s*이동%s*[-–]?%s*', '')
  return (s:gsub('[%s%p·・]+', ''))
end
local q = norm(rawQ)
local fat = Z.TableMgr.GetTable('FishingAreaTableMgr')
local ft = Z.TableMgr.GetTable('FishingTableMgr')
local itm = Z.TableMgr.GetTable('ItemTableMgr')
local soeg = Z.TableMgr.GetTable('SceneObjectEntityGlobalTableMgr')
local sot = Z.TableMgr.GetTable('SceneObjectTableMgr')
local stm = Z.TableMgr.GetTable('SceneTableMgr')
local ttm = Z.TableMgr.GetTable('TransferTableMgr')
local mapVm = Z.VMMgr.GetVM('map')
local curSid = (Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId()) or 0

local areaNameById, sobjToArea, fishByArea, fishSobjSet = {{}}, {{}}, {{}}, {{}}
if fat and fat.GetDatas then
  for id, ar in pairs(fat.GetDatas()) do
    local aid = tonumber(ar.Id or id) or 0
    local sobjId = tonumber(ar.SceneObjectId or 0) or 0
    areaNameById[aid] = tostring(ar.AreaName or '')
    if sobjId > 0 then sobjToArea[sobjId] = aid; fishSobjSet[sobjId] = true end
  end
end
if sot and sot.GetDatas then
  for id, r in pairs(sot.GetDatas()) do
    if tostring(r.Name or '') == '낚시 포인트' then fishSobjSet[tonumber(id)] = true end
  end
end
if ft and ft.GetDatas then
  for id, fr in pairs(ft.GetDatas()) do
    local aid = tonumber(fr.FishingAreaId or 0) or 0
    local fid = tonumber(fr.FishId or id) or 0
    local ir = itm and itm.GetRow(fid, true) or nil
    local nm = ir and tostring(ir.Name or '') or tostring(fid)
    if aid > 0 then
      if not fishByArea[aid] then fishByArea[aid] = {{}} end
      table.insert(fishByArea[aid], nm)
    end
    if type(fr.QuickJumpParam) == 'table' and #fr.QuickJumpParam >= 3 and aid > 0 and soeg then
      local gr = soeg.GetRow(tonumber(fr.QuickJumpParam[1] or 0) * 10000000 + tonumber(fr.QuickJumpParam[3] or 0), true)
      if gr and tonumber(gr.Id or 0) > 0 and not sobjToArea[tonumber(gr.Id)] then sobjToArea[tonumber(gr.Id)] = aid end
    end
  end
end
local groups, order = {{}}, {{}}
if soeg and soeg.GetDatas then
  for k, r in pairs(soeg.GetDatas()) do
    local sobj = tonumber(r.Id or 0) or 0
    if fishSobjSet[sobj] and r.Position then
      local sid = math.floor((tonumber(k) or 0) / 10000000)
      local srow = stm and stm.GetRow(sid, true) or nil
      if srow and (srow.SceneSubType == 3 or srow.SceneSubType == 4) then
        local aid = sobjToArea[sobj] or 0
        local sname = tostring(srow.Name or sid)
        local aname = areaNameById[aid] or ''
        local hay = norm(sname) .. norm(aname) .. norm(table.concat(fishByArea[aid] or {{}}, ','))
        if q == '' or hay:find(q, 1, true) then
          local gk = tostring(sid) .. ':' .. tostring(aid)
          if not groups[gk] then
            local g = {{ scene_id = sid, scene_name = sname, area_id = aid, area_name = aname, spot_count = 1, fish_sample = fishByArea[aid] or {{}} }}
            groups[gk] = g
            order[#order + 1] = g
          else
            groups[gk].spot_count = groups[gk].spot_count + 1
          end
        end
      end
    end
  end
end
return {{ current_scene_id = curSid, spots = order }}
"""
    ev = await _raw_hub_call("lua_eval", {"code": code}, conn_id=conn_id)
    if ev.get("ok") and isinstance(ev.get("result"), dict):
        return {"ok": True, **ev["result"]}
    return ev


async def _fallback_leave_dungeon(conn_id: str | None = None) -> dict[str, Any]:
    body_lua = """
local curSceneId = (Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId()) or 0
local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
local curRow = (stm and curSceneId > 0) and stm.GetRow(curSceneId, true) or nil
local subType = curRow and tonumber(curRow.SceneSubType or 0) or 0
local curName = curRow and tostring(curRow.Name or '') or ''

local wpOk, wp = pcall(require, 'zproxy.world_proxy')
local ent = Z.EntityMgr and Z.EntityMgr.PlayerEnt
local vLayer = 0
pcall(function()
  if ent and ent.GetLuaAttr and Z.PbAttrEnum then
    local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrVisualLayerUid'))
    vLayer = tonumber(a and a.Value or 0) or 0
  end
end)
if vLayer > 0 and wpOk and wp and wp.ExitVisualLayer then
  wp.ExitVisualLayer()
  return { ok = true, method = 'world_proxy.ExitVisualLayer', fromSceneId = curSceneId, sceneName = curName, visualLayerId = vLayer }
end

local inDungeon = (subType == 5)
pcall(function()
  if not inDungeon and Z.StageMgr and Z.StageMgr.GetIsInDungeon then
    inDungeon = (Z.StageMgr.GetIsInDungeon() == true)
  end
end)
if inDungeon then
  if not wpOk or not wp or not wp.LeaveScene then
    return { ok = false, error = 'world_proxy.LeaveScene unavailable' }
  end
  local cs = Z.CancelSource.Rent()
  local okL, ret = pcall(function() return wp.LeaveScene(cs:CreateToken()) end)
  cs:Recycle()
  if not okL then
    return { ok = false, error = tostring(ret), fromSceneId = curSceneId, sceneName = curName }
  end
  return { ok = true, method = 'world_proxy.LeaveScene', fromSceneId = curSceneId, sceneName = curName, ret = tostring(ret) }
end

if subType == 9 then
  local unionVm = Z.VMMgr and Z.VMMgr.GetVM('union')
  if not unionVm or not unionVm.LeaveUnionScene then
    return { ok = false, error = 'union_vm.LeaveUnionScene unavailable' }
  end
  local udata = Z.DataMgr and Z.DataMgr.Get('union_data')
  local tok = (udata and udata.CancelSource and udata.CancelSource:CreateToken()) or Z.CancelSource.Rent():CreateToken()
  local ret = unionVm.LeaveUnionScene(0, tok)
  return { ok = true, method = 'union_vm.LeaveUnionScene', fromSceneId = curSceneId, sceneName = curName, ret = tostring(ret) }
end

if subType == 7 or subType == 8 then
  local houseVm = Z.VMMgr and Z.VMMgr.GetVM('house')
  if not houseVm or not houseVm.ExitHome then
    return { ok = false, error = 'house_vm.ExitHome unavailable' }
  end
  houseVm.ExitHome()
  return { ok = true, method = 'house_vm.ExitHome', fromSceneId = curSceneId, sceneName = curName }
end

return { ok = false, reason_code = 'not_in_dungeon_or_hub', error = '현재 던전이나 길드홈/마이홈 안에 있지 않아요.' }
"""
    return await _run_lua_coro("__agyPyLeaveDungeon", body_lua, conn_id=conn_id, timeout_sec=6.0)


_FALLBACK_GIMMICK_SCAN_LUA = """
local entSelf = Z.EntityMgr and Z.EntityMgr.PlayerEnt
local vpSelf = entSelf and entSelf.GetLocalAttrVirtualPos and entSelf:GetLocalAttrVirtualPos()
local lx = vpSelf and tonumber(vpSelf.x or 0) or 0
local ly = vpSelf and tonumber(vpSelf.y or 0) or 0
local lz = vpSelf and tonumber(vpSelf.z or 0) or 0
local itbl = Z.TableMgr and Z.TableMgr.GetTable('InteractiveTableMgr')
local iov = Z.TableMgr and Z.TableMgr.GetTable('InteractionOverrideTableMgr')
local btm = Z.TableMgr and Z.TableMgr.GetTable('InteractBtnTableMgr')
local sot = Z.TableMgr and Z.TableMgr.GetTable('SceneObjectTableMgr')
local soeg = Z.TableMgr and Z.TableMgr.GetTable('SceneObjectEntityGlobalTableMgr')
local cache = rawget(_G, '__agyTravCfgCache')
if not cache then cache = {}; rawset(_G, '__agyTravCfgCache', cache) end

local function norm(s)
  return tostring(s or ''):lower():gsub('[%s%p·・]+', '')
end
local qNorm = norm(qRaw)
local allowUnlock = qNorm:find('활성', 1, true) or qNorm:find('지점', 1, true)

local function hasAny(txt, arr)
  for _, pat in ipairs(arr) do
    if txt:find(pat, 1, true) then return true end
  end
  return false
end

local EXCLUDE_LABELS = {
  '나가기', '꿈 끝내기', '포기', '퇴장', '드림스톤', '상자', '상점', '교환',
  '길드 이벤트 센터', '아스텔리즈로', '유성도로', '퍼레이드', '변신', '획득',
  '줍기', '낚시', '대화', '열기', '엿듣기', '조사', '질문', '채집'
}
local INCLUDE_LABELS = {
  '바람의 통로', '바람 통로', '바람 장치', '기류', '비행하기', '활공',
  '순간 이동', '전송실', '전장으로 이동', '구역으로 이동', '중심으로 이동',
  '상단 이동', '하단 이동', '공간 균열 진입', '안전 구역', '돌아가기',
  '산 정상', '섬으로', '동굴로'
}

local function inspectActionStage(tbl)
  local hitCode = nil
  local isUnlockOnly = false
  if type(tbl) ~= 'table' then return nil, false end
  for _, sub in pairs(tbl) do
    if type(sub) == 'table' then
      for _, entry in pairs(sub) do
        if type(entry) == 'table' then
          local c = tostring(entry[1] or '')
          local p = tostring(entry[2] or '')
          if c == '1003' then isUnlockOnly = true end
          if c == '1000' or p:find('WindTunnel', 1, true) or p:find('Fly', 1, true) or p:find('Glide', 1, true) then
            hitCode = 'wind_tunnel'
          elseif c == '1013' or c == '1014' or p:find('transfer', 1, true) or p:find('Trans', 1, true) or p:find('gaotai', 1, true) then
            hitCode = 'portal_jump'
          elseif p:find('Magnetic', 1, true) then
            hitCode = 'magnetic_rail'
          end
        end
      end
    end
  end
  return hitCode, isUnlockOnly
end

local function classifyCfg(cfgId, tplId, replaceBtnId, contentStr)
  local label = tostring(contentStr or '')
  local cId = tonumber(cfgId or 0) or 0
  if cId == 2200 or tonumber(tplId or 0) == 2200 or tonumber(replaceBtnId or 0) == 5006 then
    return nil, ''
  end

  if cId > 0 and cache[cId] ~= nil then
    local cached = cache[cId]
    if cached == false then return nil, '' end
    local bestLbl = (label ~= '' and label) or cached.lbl
    if bestLbl == '' then bestLbl = cached.cat == 'wind_tunnel' and '바람의 통로' or '이동 장치' end
    if cached.unlockOnly and not allowUnlock then return nil, '' end
    return cached.cat, bestLbl
  end

  local row = itbl and cId > 0 and itbl.GetRow(cId, true) or nil
  local ov = iov and cId > 0 and iov.GetRow(cId, true) or nil
  if (not row) and ov and tonumber(ov.Id or 0) > 0 and itbl then
    row = itbl.GetRow(tonumber(ov.Id), true)
  end

  local btnId = tonumber(replaceBtnId or 0) or (row and tonumber(row.TriggerBtnId or 0) or 0)
  if btnId == 1169 or btnId == 1170 or btnId == 1171 or btnId == 5006 then
    if cId > 0 then cache[cId] = false end
    return nil, ''
  end

  local tblLbl = ''
  if btnId > 0 and btm then
    local br = btm.GetRow(btnId, true)
    if br and br.Name then tblLbl = tostring(br.Name) end
  end
  local effLbl = (label ~= '' and label) or tblLbl
  if hasAny(effLbl, EXCLUDE_LABELS) then
    if cId > 0 then cache[cId] = false end
    return nil, ''
  end

  local cat, isUnlockOnly = inspectActionStage(ov and ov.ActionStage or (row and row.ActionStage))
  if not cat and ov and row then
    cat, isUnlockOnly = inspectActionStage(row.ActionStage)
  end
  if not cat and hasAny(effLbl, INCLUDE_LABELS) then
    cat = (effLbl:find('바람') or effLbl:find('비행') or effLbl:find('활공')) and 'wind_tunnel' or 'portal_jump'
  end

  if not cat then
    if cId > 0 then cache[cId] = false end
    return nil, ''
  end

  if cId > 0 then
    cache[cId] = { cat = cat, lbl = tblLbl, unlockOnly = isUnlockOnly }
  end
  if isUnlockOnly and not allowUnlock then return nil, '' end
  if effLbl == '' then effLbl = (cat == 'wind_tunnel') and '바람의 통로' or '이동 장치' end
  return cat, effLbl
end

local items = {}
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
local list = idata and idata.GetData and idata:GetData()
if type(list) == 'table' then
  for _, e in pairs(list) do
    local uuid = tonumber(e.GetUuid and e:GetUuid() or 0) or 0
    local cfgId = tonumber(e.GetInteractionCfgId and e:GetInteractionCfgId() or 0) or 0
    local tplId = tonumber(e.GetTemplateId and e:GetTemplateId() or 0) or 0
    local btnId = tonumber(e.GetReplaceBtnId and e:GetReplaceBtnId() or 0) or 0
    local seat = tonumber(e.GetSeatGroupIndex and e:GetSeatGroupIndex() or 0) or 0
    local txt = ''
    pcall(function() txt = tostring(e:GetContentStr() or '') end)

    local cat, lbl = classifyCfg(cfgId, tplId, btnId, txt)
    if cat then
      local condOk = true
      pcall(function() condOk = Z.InteractionMgr:CheckCondition(uuid, cfgId, seat, false) end)
      if condOk ~= false then
        local px, py, pz, hasPos, dist = 0, 0, 0, false, 0
        pcall(function()
          local ent = Z.EntityMgr and Z.EntityMgr:GetEntity(uuid)
          local vpos = ent and ent.GetLocalAttrVirtualPos and ent:GetLocalAttrVirtualPos()
          if vpos then
            px, py, pz = vpos.x, vpos.y, vpos.z
            hasPos = true
            dist = math.sqrt((px - lx)^2 + (py - ly)^2 + (pz - lz)^2)
          end
        end)
        if qNorm == '' or norm(lbl):find(qNorm, 1, true) then
          items[#items + 1] = {
            uuid = uuid, cfg_id = cfgId, tpl_id = tplId, seat = seat,
            label = lbl, category = cat, in_range = true,
            has_pos = hasPos, x = px, y = py, z = pz, distance = math.floor(dist * 10 + 0.5) / 10
          }
        end
      end
    end
  end
end

local curSid = (Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId()) or 0
if curSid > 0 and soeg and soeg.GetDatas and sot then
  local prefixMin = curSid * 10000000
  local prefixMax = prefixMin + 9999999
  for gKey, gr in pairs(soeg.GetDatas()) do
    local gkNum = tonumber(gKey) or 0
    if gkNum >= prefixMin and gkNum <= prefixMax and gr.Position then
      local dx = (tonumber(gr.Position.X) or 0) - lx
      local dz = (tonumber(gr.Position.Z) or 0) - lz
      if math.abs(dx) <= maxR and math.abs(dz) <= maxR then
        local d3 = math.sqrt(dx^2 + (((tonumber(gr.Position.Y) or 0) - ly)^2) + dz^2)
        if d3 <= maxR then
          local sobjId = tonumber(gr.Id or 0) or 0
          local sr = sobjId > 0 and sot.GetRow(sobjId, true) or nil
          if sr and type(sr.InteractionTemplate) == 'table' then
            for _, tpl in ipairs(sr.InteractionTemplate) do
              local iid = type(tpl) == 'table' and tonumber(tpl[2] or tpl[1] or 0) or tonumber(tpl or 0)
              local cat, lbl = classifyCfg(iid, 0, 0, sr.Name)
              if cat and (qNorm == '' or norm(lbl):find(qNorm, 1, true)) then
                items[#items + 1] = {
                  uuid = 0, cfg_id = iid or 0, tpl_id = 0, seat = 0,
                  label = lbl, category = cat, in_range = false,
                  has_pos = true, x = gr.Position.X, y = gr.Position.Y, z = gr.Position.Z,
                  distance = math.floor(d3 * 10 + 0.5) / 10
                }
                break
              end
            end
          end
        end
      end
    end
  end
end

table.sort(items, function(a, b)
  if a.in_range ~= b.in_range then return a.in_range end
  return a.distance < b.distance
end)
return { ok = true, count = #items, gimmicks = items }
"""


async def _fallback_list_gimmicks(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    q = str(params.get("query") or "").strip()
    radius = float(params.get("radius") or 45.0)
    code = f"local qRaw = {_lua_quote(q)}\nlocal maxR = {radius:.1f}\n" + _FALLBACK_GIMMICK_SCAN_LUA
    ev = await _raw_hub_call("lua_eval", {"code": code}, conn_id=conn_id)
    if ev.get("ok") and isinstance(ev.get("result"), dict):
        return {"ok": True, **ev["result"]}
    return ev


async def _fallback_use_gimmick(params: dict[str, Any], conn_id: str | None = None) -> dict[str, Any]:
    q = str(params.get("gimmick") or params.get("query") or params.get("destination") or "").strip()
    scan = await _fallback_list_gimmicks({"query": q, "radius": 45.0}, conn_id=conn_id)
    if not scan.get("ok"):
        return scan
    gimmicks = scan.get("gimmicks") or []
    pick = next((g for g in gimmicks if g.get("in_range")), None)
    if not pick:
        if gimmicks:
            near = gimmicks[0]
            return {
                "ok": False,
                "error": f"주변 {near.get('distance', '?')}m에 '{near.get('label', '이동 기믹')}' 기믹이 있으나 상호작용 범위 밖이에요. 조금 더 다가가거나 게임을 한 번 재시작해 v0.9.5 자동 접근 이동을 적용해 주세요.",
                "nearest": near,
            }
        return {"ok": False, "error": "주변 45m 이내에서 탈 수 있는 이동 기믹을 찾지 못했어요."}

    uuid = int(pick.get("uuid") or 0)
    cfg_id = int(pick.get("cfg_id") or 0)
    seat = int(pick.get("seat") or 0)
    lbl = str(pick.get("label") or "이동 기믹")
    trig_code = f"""
local wantUuid = {uuid}
local wantCfg = {cfg_id}
local wantSeat = {seat}
if not Z.InteractionMgr then return {{ ok = false, error = 'InteractionMgr unavailable' }} end
local entSelf = Z.EntityMgr and Z.EntityMgr.PlayerEnt
local vvm = Z.VMMgr and Z.VMMgr.GetVM('vehicle')
if entSelf and vvm and vvm.StopRide and (entSelf:GetLuaRidingId() or 0) ~= 0 then
  pcall(function() vvm.StopRide() end)
end
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
local list = idata and idata.GetData and idata:GetData()
if type(list) ~= 'table' then return {{ ok = false, error = 'interaction_data empty' }} end
for _, e in pairs(list) do
  local u = tonumber(e.GetUuid and e:GetUuid() or 0) or 0
  local c = tonumber(e.GetInteractionCfgId and e:GetInteractionCfgId() or 0) or 0
  local s = tonumber(e.GetSeatGroupIndex and e:GetSeatGroupIndex() or 0) or 0
  local t = tonumber(e.GetTemplateId and e:GetTemplateId() or 0) or 0
  if u == wantUuid and (c == wantCfg or wantCfg == 0) and (s == wantSeat or wantSeat == 0) then
    local condOk = true
    pcall(function() condOk = Z.InteractionMgr:CheckCondition(u, c, s, false) end)
    if condOk == false then
      return {{ ok = false, error = 'CheckCondition returned false' }}
    end
    Z.InteractionMgr:BeginInteraction(u, c, t, s)
    return {{ ok = true, uuid = u, cfg_id = c }}
  end
end
return {{ ok = false, error = 'Prompt left range before activation' }}
"""
    ev = await _raw_hub_call("lua_eval", {"code": trig_code}, conn_id=conn_id)
    inner = ev.get("result") if ev.get("ok") and isinstance(ev.get("result"), dict) else ev
    if isinstance(inner, dict) and inner.get("ok"):
        return {
            "ok": True,
            "uuid": uuid,
            "cfg_id": cfg_id,
            "label": lbl,
            "behavior": {
                "kind": "use_gimmick",
                "phase": "completed",
                "gimmick_label": lbl,
                "detail": f"'{lbl}' 이동 기믹을 작동했습니다.",
                "finished": True,
            },
        }
    return {"ok": False, "error": str((inner or {}).get("error") or "이동 기믹 작동에 실패했어요.")}


chat_monitor = ChatMonitorService(lambda m, p, conn_id=None: dispatch_game_rpc(m, p, conn_id=conn_id))
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

    async def _pre_v090_poller() -> None:
        """
        Lightweight background poller ONLY for connected game clients running a pre-v0.9.0 DLL in memory:
          1) Auto-confirms active dungeon/raid/unstable/world-boss ready checks when `auto_ready` is enabled.
          2) Auto-accepts incoming party leader transfer offers (`InvitationTipsType.TeamTransfer == 4`) from authorized commanders.
        Automatically skips any client whose in-memory DLL already natively supports `ready_check` (v0.9.0+).
        """
        hook_lua = """
if not rawget(_G, '__agyPyLeaderOffers') then rawset(_G, '__agyPyLeaderOffers', {}) end
if not rawget(_G, '__agyPyLeaderHookInstalled') and Z and Z.EventMgr and Z.ConstValue then
  local T = (E and E.InvitationTipsType) or {}
  local wantType = T.TeamTransfer or 4
  local owner = {}
  local fn = function(a, b)
    pcall(function()
      local info = (type(a) == 'table' and a.tipsType ~= nil) and a or b
      if type(info) == 'table' and tonumber(info.tipsType or 0) == wantType then
        local q = rawget(_G, '__agyPyLeaderOffers')
        if type(q) == 'table' then
          q[#q + 1] = tonumber(info.charId or 0) or 0
          if #q > 15 then table.remove(q, 1) end
        end
      end
    end)
  end
  pcall(function()
    Z.EventMgr:Add(Z.ConstValue.InvitationRefreshTips, fn, owner)
    rawset(_G, '__agyPyLeaderHookOwner', owner)
    rawset(_G, '__agyPyLeaderHookInstalled', true)
  end)
end
local offers = rawget(_G, '__agyPyLeaderOffers') or {}
if #offers > 0 then rawset(_G, '__agyPyLeaderOffers', {}) end
""" + "\nlocal readyInfo = (function()\n" + _DETECT_READY_LUA + "\nend)()\nreturn { ready = readyInfo.kind or '', leader_offers = offers }"

        while not server.should_exit:
            await asyncio.sleep(0.8)
            for c in list(hub.list_clients()):
                cid = str(c.get("conn_id") or "")
                if not cid or _plugin_supports("ready_check", cid):
                    continue
                try:
                    fmodes = (chat_monitor.status(conn_id=cid).get("feature_modes") or {}) if chat_monitor else {}
                    auto_ready_on = int(fmodes.get("auto_ready", 0)) == 1
                    party_manage_on = int(fmodes.get("party_manage", 1)) == 1
                    interact_on = int(fmodes.get("interaction_accept", 1)) == 1
                    if not auto_ready_on and not (party_manage_on and interact_on):
                        continue

                    probe = await _raw_hub_call("lua_eval", {"code": hook_lua}, timeout=3.0, conn_id=cid)
                    p_res = probe.get("result") if probe.get("ok") and isinstance(probe.get("result"), dict) else None
                    if not p_res:
                        continue

                    # 1. Auto-confirm ready popup
                    r_kind = str(p_res.get("ready") or "")
                    stat = _py_ready_stats.setdefault(
                        cid,
                        {"accepted_total": 0, "last_kind": "", "last_accepted_at": None, "cooldown_until": 0.0},
                    )
                    if auto_ready_on and r_kind and time.monotonic() >= float(stat.get("cooldown_until") or 0.0):
                        c_res = await _fallback_ready_check(conn_id=cid)
                        if c_res.get("ok"):
                            logger.info("[AutoReady-Compat] Auto-confirmed ready check (%s) on [%s]: %s", r_kind, cid, c_res)

                    # 2. Auto-accept commander party leader transfer offer
                    offers = p_res.get("leader_offers")
                    if party_manage_on and interact_on and isinstance(offers, list) and offers and chat_monitor is not None:
                        cfg_obj = chat_monitor._get_cfg(cid)
                        for raw_sender in offers:
                            sender_uid = int(raw_sender or 0)
                            if sender_uid > 0 and await chat_monitor._is_sender_authorized(cfg_obj, cid, sender_uid, ""):
                                accept_body = """
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncAcceptTransferBeLeader then return { ok = false } end
local cs = Z.CancelSource.Rent()
local okA, errA = pcall(function() tvm.AsyncAcceptTransferBeLeader(true, cs:CreateToken()) end)
cs:Recycle()
pcall(function() Z.DataMgr.Get('team_tip_data'):ClearCacheDataByType(4) end)
return { ok = okA, error = okA and '' or tostring(errA) }
"""
                                a_res = await _run_lua_coro("__agyPyAcceptLeader", accept_body, conn_id=cid, timeout_sec=4.0)
                                logger.info("[LeaderOffer-Compat] Auto-accepted leader transfer from %d on [%s]: %s", sender_uid, cid, a_res)
                                break
                except Exception as poll_exc:
                    logger.debug("Pre-v0.9.0 compat poller skipped [%s]: %s", cid, poll_exc)

    wd_task = asyncio.create_task(_watchdog())
    compat_task = asyncio.create_task(_pre_v090_poller())
    try:
        await server.serve()
    finally:
        wd_task.cancel()
        compat_task.cancel()


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
