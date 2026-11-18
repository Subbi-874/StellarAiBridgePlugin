#!/usr/bin/env python3
"""
Stellar AGY Bridge CLI helper for Linux BPSR workspace (/home/subbi/bpsr).
Communicates strictly with the Windows Stellar.AgyBridge HTTP RPC endpoint (/api/call, /api/status).
"""
import argparse
import json
import os
from pathlib import Path
import sys
import time
from typing import Any
import urllib.error
import urllib.request


def _load_bridge_env() -> dict[str, str]:
    env_map: dict[str, str] = {}
    candidates = [
        Path(__file__).resolve().parents[4] / ".bridge.env",
        Path("/home/subbi/bpsr/.bridge.env"),
    ]
    for p in candidates:
        if p.is_file():
            try:
                for raw in p.read_text(encoding="utf-8").splitlines():
                    ln = raw.strip()
                    if not ln or ln.startswith("#") or "=" not in ln:
                        continue
                    k, v = ln.split("=", 1)
                    env_map[k.strip()] = v.strip().strip("'\"")
            except OSError:
                pass
    return env_map


_FILE_ENV = _load_bridge_env()
BRIDGE_URL = (
    os.getenv("STELLAR_BRIDGE_URL")
    or _FILE_ENV.get("STELLAR_BRIDGE_URL")
    or "http://192.168.2.21:18765"
).rstrip("/")
BRIDGE_TOKEN = os.getenv("STELLAR_BRIDGE_TOKEN") or _FILE_ENV.get("STELLAR_BRIDGE_TOKEN") or ""


_DEFAULT_CONN_ID = ""
_RESOLVED_CONN_CACHE: dict[str, str] = {}


def _raw_post_call(
    method: str,
    params: dict[str, Any] | None = None,
    timeout: float = 15.0,
    conn_id: str = "",
) -> dict[str, Any]:
    body: dict[str, Any] = {"method": method, "params": params or {}, "timeout": timeout}
    if conn_id:
        body["conn_id"] = conn_id
    payload = json.dumps(body).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if BRIDGE_TOKEN:
        headers["Authorization"] = f"Bearer {BRIDGE_TOKEN}"
    req = urllib.request.Request(f"{BRIDGE_URL}/api/call", data=payload, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout + 3.0) as resp:
            return json.loads(resp.read().decode("utf-8", errors="replace"))
    except urllib.error.HTTPError as he:
        body_err = he.read().decode("utf-8", errors="replace")
        return {"ok": False, "http_status": he.code, "error": body_err or str(he)}
    except Exception as exc:
        return {"ok": False, "bridge_url": BRIDGE_URL, "error": f"Bridge unreachable: {exc}"}


def _resolve_conn_selector(sel: str) -> str:
    q = (sel or "").strip()
    if not q or q.lower().startswith("client-"):
        return q
    if q in _RESOLVED_CONN_CACHE:
        return _RESOLVED_CONN_CACHE[q]

    res = _raw_post_call("list_clients", {}, timeout=5.0)
    clients = res.get("clients") if isinstance(res, dict) else None
    if isinstance(clients, list):
        q_low = q.lower()
        for c in clients:
            if not isinstance(c, dict):
                continue
            cid = str(c.get("conn_id") or "").strip()
            if not cid:
                continue
            char_id = str(c.get("char_id") or "").strip()
            char_name = str(c.get("char_name") or "").strip().lower()
            if (char_id and char_id == q) or (char_name and char_name == q_low):
                _RESOLVED_CONN_CACHE[q] = cid
                return cid
    return ""


def bridge_call(
    method: str,
    params: dict[str, Any] | None = None,
    timeout: float = 15.0,
    conn_id: str = "",
) -> dict[str, Any]:
    target_conn = _resolve_conn_selector((conn_id or _DEFAULT_CONN_ID).strip())
    return _raw_post_call(method, params, timeout=timeout, conn_id=target_conn)


def bridge_call_wait(
    method: str,
    params: dict[str, Any] | None = None,
    timeout: float = 15.0,
    wait: float = 12.0,
    conn_id: str = "",
) -> dict[str, Any]:
    """Calls a bridge command; if it started an async operation (async_id), polls until it finishes and attaches `final`."""
    res = bridge_call(method, params, timeout, conn_id=conn_id)
    if isinstance(res, dict) and res.get("ok") and res.get("async_id"):
        deadline = time.time() + wait
        while time.time() < deadline:
            time.sleep(0.4)
            poll = bridge_call("poll_async", {"async_id": res["async_id"]}, conn_id=conn_id)
            entry = poll.get("result") if isinstance(poll, dict) else None
            if not isinstance(entry, dict):
                continue
            state = entry.get("state")
            if state == "done":
                res["final"] = entry.get("result")
                if isinstance(res["final"], dict) and not res["final"].get("ok", True):
                    res["ok"] = False
                    res["error"] = res["final"].get("error")
                break
            if state in ("error", "missing"):
                res["final"] = {"ok": False, "error": entry.get("error") or state}
                res["ok"] = False
                res["error"] = res["final"]["error"]
                break
    return res


def main() -> None:
    global _DEFAULT_CONN_ID
    client_parent = argparse.ArgumentParser(add_help=False)
    client_parent.add_argument(
        "--client",
        dest="conn_id",
        default=argparse.SUPPRESS,
        help="Optional target client conn_id (e.g. client-1) or character name/UID without switching active client",
    )

    parser = argparse.ArgumentParser(description="Stellar AGY Bridge remote helper")
    parser.add_argument(
        "--client",
        dest="conn_id",
        default="",
        help="Optional target client conn_id (e.g. client-1) or character name/UID without switching active client",
    )
    _raw_sub = parser.add_subparsers(dest="cmd", required=True)

    def add_cmd(name: str, **kwargs: Any) -> argparse.ArgumentParser:
        parents = list(kwargs.pop("parents", ()))
        if client_parent not in parents:
            parents.append(client_parent)
        return _raw_sub.add_parser(name, parents=parents, **kwargs)

    add_cmd("status", help="Get active character status, location, party, and feature locks")
    add_cmd("chat-settings", help="Get UI chat monitor, commander, AdminLock, and LLM/persona settings")
    add_cmd("clients", help="List connected game clients")

    p_sel = add_cmd("select", help="Select active game client")
    p_sel.add_argument("selector")

    add_cmd("party", help="Get current party info")

    p_ros = add_cmd("roster", help="Get unified Party/Friend/Guild UID roster")
    p_ros.add_argument("--category", default="all")
    p_ros.add_argument("--query", default="")
    p_ros.add_argument("--online-only", action="store_true")

    p_ent = add_cmd("entities", help="Get nearby entities")
    p_ent.add_argument("--kind", default="")
    p_ent.add_argument("--limit", type=int, default=40)

    p_chats = add_cmd("chats", help="Get recent in-game chat messages")
    p_chats.add_argument("--channel", default="")
    p_chats.add_argument("--limit", type=int, default=30)

    p_send = add_cmd("send-chat", help="Send an in-game chat message")
    p_send.add_argument("channel")
    p_send.add_argument("text")
    p_send.add_argument("--target-uid", type=int, default=0)

    p_stls = add_cmd("stickers", help="List owned in-game chat stickers")
    p_stls.add_argument("--query", default="")
    p_stls.add_argument("--group", type=int, default=0)

    p_st = add_cmd("send-sticker", help="Send a built-in game picture sticker")
    p_st.add_argument("channel")
    p_st.add_argument("sticker", help="Mood tag (greet, thanks, thumbsup, cheer, heart, laugh, cute, ok, think, sorry, sad, random) or numeric ID")
    p_st.add_argument("--target-uid", type=int, default=0)

    p_fol = add_cmd("follow", help="Follow a player by UID or name")
    p_fol.add_argument("target")
    p_fol.add_argument("--once", action="store_true")
    p_fol.add_argument("--distance", type=float, default=2.5)

    add_cmd("stop", help="Stop current movement/follow/gather")

    p_mv = add_cmd("move", help="Move to coordinates in current scene")
    p_mv.add_argument("x", type=float)
    p_mv.add_argument("y", type=float)
    p_mv.add_argument("z", type=float)

    p_pl = add_cmd("places", help="List teleportable places/warps")
    p_pl.add_argument("--query", default="")

    p_tp = add_cmd("teleport", help="Teleport to named place/home/hub")
    p_tp.add_argument("destination")

    add_cmd("channels", help="List current and available channels on the active map")

    p_ch = add_cmd("channel", help="Switch channel/line number or jump to a player's channel")
    p_ch.add_argument("line_id", nargs="?", type=int, default=0)
    p_ch.add_argument("--target", default="", help="Player UID or name whose channel to join")

    p_cb = add_cmd("combat", help="Toggle auto-combat on/off")
    p_cb.add_argument("state", choices=["on", "off", "true", "false", "1", "0"])

    p_gl = add_cmd("gatherables", help="Search gatherable life-skill resources")
    p_gl.add_argument("--query", default="")

    p_ga = add_cmd("gather", help="Start auto-gathering a resource")
    p_ga.add_argument("resource")
    p_ga.add_argument("--count", type=int, default=20)
    p_ga.add_argument("--focus", choices=["true", "false"], default=None)

    p_fs = add_cmd("fish-spots", help="List dynamic fishing spots, landmarks, and catchable fish")
    p_fs.add_argument("--query", default="")

    p_fi = add_cmd("fish", help="Travel to a fishing spot and auto-fish via Stellar.AutoFishing")
    p_fi.add_argument("spot", nargs="?", default="", help="Landmark, sea/lake/map, fish name, or empty for nearest")
    p_fi.add_argument("--count", type=int, default=0, help="Target fish count (0 = continuous until stop)")

    p_po = add_cmd("party-op", help="Manage party")
    p_po.add_argument(
        "op",
        choices=[
            "create",
            "join",
            "merge",
            "leave",
            "invite",
            "accept_apply",
            "set_auto_accept",
            "set_member_type",
            "kick",
            "transfer_leader",
        ],
    )
    p_po.add_argument("--target", default="")
    p_po.add_argument("--auto-accept", action="store_true")
    p_po.add_argument("--size", type=int, default=0, help="5 or 20 (set_member_type)")

    p_dls = add_cmd("dungeons", help="List matching dungeons / unstable spaces / raids / world boss")
    p_dls.add_argument("--query", default="")
    p_dls.add_argument("--category", default="", choices=["", "unstable", "raid", "master", "boss"])

    p_ent_d = add_cmd("enter-dungeon", help="Start entering a dungeon, unstable space, raid, or world boss (party leader or solo)")
    p_ent_d.add_argument("dungeon")
    p_ent_d.add_argument("--difficulty", default="")
    p_ent_d.add_argument("--single-ai", action="store_true")

    add_cmd("ready", help="Confirm any currently active dungeon/raid/match ready check")

    add_cmd("revive", help="Revive if dead (free options only)")

    p_ri = add_cmd("ride", help="Mount / dismount the equipped Riding Imagine mount")
    p_ri.add_argument("action", nargs="?", default="mount", choices=["mount", "dismount", "toggle"])
    p_ri.add_argument("--type", default="auto", choices=["auto", "land", "water"])

    p_ai = add_cmd("accept-interaction", help="Approve 2-person interaction / ride / party / leader requests from a player")
    p_ai.add_argument("target")
    p_ai.add_argument("--window", type=int, default=60)
    p_ai.add_argument("--kinds", default="", help="comma list: interaction, ride, party, leader (empty = all)")

    p_rr = add_cmd("request-ride", help="Send a ride-together request to a nearby player")
    p_rr.add_argument("target")
    p_rr.add_argument("--mode", default="apply", choices=["apply", "invite"])

    p_ri2 = add_cmd("request-interaction", help="Send a 2-person action request to a nearby player")
    p_ri2.add_argument("target")
    p_ri2.add_argument("--action", default="손잡기")

    p_lc = add_cmd("locate", help="Where is a player (map, channel, coordinates, distance)")
    p_lc.add_argument("target")

    add_cmd("loadouts", help="List job/skill presets")
    p_lo = add_cmd("loadout", help="Switch job/skill preset by number, name or profession")
    p_lo.add_argument("--number", type=int, default=0)
    p_lo.add_argument("--name", default="")
    p_lo.add_argument("--profession", default="")

    add_cmd("outfits", help="List outfit presets")
    p_ou = add_cmd("outfit", help="Switch outfit preset (no args = random)")
    p_ou.add_argument("--number", type=int, default=0)
    p_ou.add_argument("--name", default="")

    p_sm = add_cmd("seek-monster", help="Travel to the nearest spawn of a monster")
    p_sm.add_argument("monster")
    p_sm.add_argument("--engage", action="store_true")

    add_cmd("leave-dungeon", help="Exit current dungeon, raid, unstable space, world boss, guild hall, or home")

    p_gm = add_cmd("gimmicks", help="List nearby traversal field/dungeon gimmicks")
    p_gm.add_argument("--query", default="")
    p_gm.add_argument("--radius", type=float, default=45.0)

    p_ug = add_cmd("use-gimmick", help="Approach and activate a nearby traversal gimmick")
    p_ug.add_argument("query", nargs="?", default="")

    p_em = add_cmd("emotes", help="List unlocked emotes")
    p_em.add_argument("--query", default="")

    p_pe = add_cmd("emote", help="Play or stop an emote")
    p_pe.add_argument("name")

    p_raw = add_cmd("call", help="Raw RPC call")
    p_raw.add_argument("method")
    p_raw.add_argument("params_json", nargs="?", default="{}")

    args = parser.parse_args()
    _DEFAULT_CONN_ID = str(getattr(args, "conn_id", "") or "").strip()
    c = args.cmd

    if c == "status":
        res = bridge_call("get_status", {})
    elif c == "chat-settings":
        res = bridge_call("get_ui_chat_settings", {})
    elif c == "clients":
        res = bridge_call("list_clients", {})
    elif c == "select":
        res = bridge_call("select_client", {"selector": args.selector})
    elif c == "party":
        res = bridge_call("get_party", {"refresh_line": True})
    elif c == "roster":
        res = bridge_call(
            "get_social_roster",
            {"category": args.category, "query": args.query, "online_only": args.online_only},
        )
    elif c == "entities":
        p: dict[str, Any] = {"limit": args.limit}
        if args.kind:
            p["kind"] = args.kind
        res = bridge_call("get_entities", p)
    elif c == "chats":
        p = {"limit": args.limit, "include_system": False}
        if args.channel:
            p["channel"] = args.channel
        res = bridge_call("get_chats", p)
    elif c == "send-chat":
        res = bridge_call(
            "send_chat",
            {"channel": args.channel, "text": args.text, "target_char_id": args.target_uid},
        )
    elif c == "stickers":
        res = bridge_call("list_stickers", {"query": args.query, "group_id": args.group})
    elif c == "send-sticker":
        res = bridge_call(
            "send_sticker",
            {"channel": args.channel, "sticker": args.sticker, "target_char_id": args.target_uid},
        )
    elif c == "follow":
        res = bridge_call(
            "follow_player",
            {"target": args.target, "continuous": not args.once, "stop_distance": args.distance},
        )
    elif c == "stop":
        res = bridge_call("stop", {"reason": "remote_agent"})
    elif c == "move":
        res = bridge_call("move_to", {"x": args.x, "y": args.y, "z": args.z})
    elif c == "places":
        res = bridge_call("list_places", {"query": args.query})
    elif c == "teleport":
        res = bridge_call("teleport_place", {"destination": args.destination}, timeout=20.0)
    elif c == "channels":
        res = bridge_call_wait("list_channels", {}, timeout=12.0)
    elif c == "channel":
        chp: dict[str, Any] = {}
        if args.line_id > 0:
            chp["line_id"] = args.line_id
        if args.target:
            chp["target"] = args.target
        res = bridge_call_wait("switch_channel", chp, timeout=20.0)
    elif c == "combat":
        enable = args.state in ("on", "true", "1")
        res = bridge_call("set_auto_combat", {"enable": enable})
    elif c == "gatherables":
        res = bridge_call("list_gatherables", {"query": args.query})
    elif c == "gather":
        gp: dict[str, Any] = {"resource": args.resource, "count": args.count}
        if args.focus is not None:
            gp["use_focus"] = args.focus == "true"
        res = bridge_call("gather_resource", gp, timeout=20.0)
    elif c == "fish-spots":
        res = bridge_call("list_fishing_spots", {"query": args.query})
    elif c == "fish":
        res = bridge_call("fish", {"spot": args.spot, "count": args.count}, timeout=20.0)
    elif c == "party-op":
        pp: dict[str, Any] = {
            "action": args.op,
            "auto_accept": args.auto_accept,
            "enable": args.auto_accept,
            "leave_first": True,
        }
        if args.target:
            pp["target"] = args.target
        if args.size:
            pp["size"] = args.size
        res = bridge_call_wait("party_action", pp, timeout=15.0)
    elif c == "dungeons":
        res = bridge_call("list_dungeons", {"query": args.query, "category": args.category})
    elif c == "enter-dungeon":
        res = bridge_call_wait(
            "enter_dungeon",
            {"dungeon": args.dungeon, "difficulty": args.difficulty, "single_ai": args.single_ai},
            timeout=20.0,
            wait=15.0,
        )
    elif c == "ready":
        res = bridge_call_wait("ready_check", {})
    elif c == "revive":
        res = bridge_call_wait("revive", {})
    elif c == "ride":
        res = bridge_call_wait("ride", {"action": args.action, "type": args.type})
    elif c == "accept-interaction":
        ap = {"target": args.target, "window_sec": args.window}
        if args.kinds:
            ap["kinds"] = args.kinds
        res = bridge_call("accept_interaction", ap)
    elif c == "request-ride":
        res = bridge_call_wait("request_ride", {"target": args.target, "mode": args.mode})
    elif c == "request-interaction":
        res = bridge_call_wait("request_interaction", {"target": args.target, "action": args.action})
    elif c == "locate":
        res = bridge_call("query_player_location", {"target": args.target})
        for _ in range(3):
            if not (res.get("ok") and res.get("pending")):
                break
            time.sleep(1.0)
            res = bridge_call("query_player_location", {"target": args.target})
    elif c == "loadouts":
        res = bridge_call("list_loadouts", {})
    elif c == "loadout":
        lp = {k: v for k, v in (("number", args.number), ("name", args.name), ("profession", args.profession)) if v}
        res = bridge_call_wait("switch_loadout", lp)
    elif c == "outfits":
        res = bridge_call("list_outfits", {})
    elif c == "outfit":
        op = {k: v for k, v in (("number", args.number), ("name", args.name)) if v}
        res = bridge_call_wait("switch_outfit", op)
    elif c == "seek-monster":
        res = bridge_call("seek_monster", {"monster": args.monster, "engage": args.engage}, timeout=20.0)
    elif c == "leave-dungeon":
        res = bridge_call_wait("leave_dungeon", {}, timeout=15.0)
    elif c == "gimmicks":
        res = bridge_call("list_gimmicks", {"query": args.query, "radius": args.radius})
    elif c == "use-gimmick":
        res = bridge_call("use_gimmick", {"gimmick": args.query}, timeout=15.0)
    elif c == "emotes":
        res = bridge_call("list_emotes", {"query": args.query})
    elif c == "emote":
        if args.name.strip().lower() in ("stop", "중지", "정지", "그만"):
            res = bridge_call("play_emote", {"stop": True})
        else:
            res = bridge_call("play_emote", {"emote": args.name})
    elif c == "call":
        try:
            parsed_p = json.loads(args.params_json)
        except json.JSONDecodeError as je:
            print(json.dumps({"ok": False, "error": f"Invalid JSON params: {je}"}, ensure_ascii=False))
            sys.exit(1)
        res = bridge_call(args.method, parsed_p)
    else:
        res = {"ok": False, "error": f"Unknown command: {c}"}

    print(json.dumps(res, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
