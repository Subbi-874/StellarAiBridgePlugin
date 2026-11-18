#!/usr/bin/env python3
"""
PreToolUse safety & search-cap guard for /home/subbi/bpsr.
1. Caps search_web calls to MAX_WEB_SEARCHES (default 2) per conversationId so unsearchable topics never loop.
2. Blocks all non-BPSR Linux system/file tools (blocks arbitrary shell commands, local file reads outside /home/subbi/bpsr/.agents/skills, file writes/edits, directory scans).
3. Allows bridge_cli.py for authorized commanders (명령권한=있음) while blocking non-commanders (명령권한=없음) and direct send-chat/send-sticker calls during <current_trigger_message>.
"""
import ipaddress
import json
import os
from pathlib import Path
import re
import shlex
import sys
import time
from urllib.parse import urlparse

WORKSPACE_ROOT = Path("/home/subbi/bpsr").resolve()
SKILLS_ROOT = (WORKSPACE_ROOT / ".agents" / "skills").resolve()
BRIDGE_CLI_PATH = (SKILLS_ROOT / "stellar-bridge" / "scripts" / "bridge_cli.py").resolve()
MAX_WEB_SEARCHES = int(os.getenv("BPSR_MAX_WEB_SEARCHES", "2"))

WEB_SEARCH_TOOLS = {"search_web", "web_search", "google_search"}
URL_FETCH_TOOLS = {"read_url_content", "fetch_url"}
READ_FILE_TOOLS = {"read_file", "view_file"}
PASSTHROUGH_TOOLS = {"finish"}

FORBIDDEN_SHELL_METACHARS = (";", "&&", "||", "|", "`", "$(", "\n", "\r", ">", "<")


def _deny(reason: str) -> None:
    print(json.dumps({"decision": "deny", "reason": reason}, ensure_ascii=False))


def _allow() -> None:
    print(json.dumps({"decision": "allow"}))


def _check_search_cap(conv_id: str) -> bool:
    if not conv_id:
        return True
    safe_id = "".join(c for c in conv_id if c.isalnum() or c in ("-", "_"))[:64]
    if not safe_id:
        return True
    cap_dir = Path("/dev/shm/bpsr_search_caps")
    try:
        cap_dir.mkdir(parents=True, exist_ok=True)
        now = time.time()
        for f in cap_dir.glob("cnt_*.txt"):
            try:
                if now - f.stat().st_mtime > 600:
                    f.unlink(missing_ok=True)
            except OSError:
                pass
        cf = cap_dir / f"cnt_{safe_id}.txt"
        cur = int(cf.read_text(encoding="utf-8").strip()) if cf.exists() else 0
        if cur >= MAX_WEB_SEARCHES:
            return False
        cf.write_text(str(cur + 1), encoding="utf-8")
        return True
    except Exception:
        return True


def _is_safe_public_url(url: str) -> bool:
    if not url:
        return False
    try:
        p = urlparse(url.strip())
    except Exception:
        return False
    if p.scheme.lower() not in ("http", "https"):
        return False
    host = (p.hostname or "").strip().lower()
    if not host or host in ("localhost", "localhost.localdomain"):
        return False
    try:
        ip = ipaddress.ip_address(host)
        if ip.is_loopback or ip.is_private or ip.is_link_local or ip.is_reserved or ip.is_multicast:
            return False
    except ValueError:
        if host.endswith(".local") or host.endswith(".internal"):
            return False
    return True


def _is_allowed_bridge_command(cmd_line: str) -> bool:
    s = (cmd_line or "").strip()
    if not s or any(mc in s for mc in FORBIDDEN_SHELL_METACHARS):
        return False
    try:
        tokens = shlex.split(s)
    except ValueError:
        return False
    if len(tokens) < 2:
        return False
    exe = Path(tokens[0]).name.lower()
    if exe not in ("python3", "python"):
        return False
    try:
        script_path = Path(tokens[1])
        if not script_path.is_absolute():
            script_path = (WORKSPACE_ROOT / script_path).resolve()
        else:
            script_path = script_path.resolve()
        return script_path == BRIDGE_CLI_PATH
    except Exception:
        return False


def _is_non_commander_trigger(last_input: str) -> bool:
    if "<current_trigger_message>" not in last_input:
        return False
    tail = last_input.split("</recent_chat_history>", 1)[-1]
    m = re.search(
        r"<current_trigger_message>\s*\r?\n발신자:[^\r\n]*,\s*명령권한=(있음|없음)\)",
        tail,
    )
    return m is None or m.group(1) != "있음"


def _is_direct_chat_send_subcommand(cmd_line: str) -> bool:
    try:
        tokens = shlex.split((cmd_line or "").strip())
    except ValueError:
        return True
    if len(tokens) < 3:
        return False
    pos: list[str] = []
    i = 2
    while i < len(tokens):
        tok = tokens[i]
        if tok == "--client":
            i += 2
            continue
        if tok.startswith("--client="):
            i += 1
            continue
        if tok.startswith("-"):
            i += 1
            continue
        pos.append(tok.lower())
        i += 1
    if not pos:
        return False
    if pos[0] in ("send-chat", "send-sticker"):
        return True
    if pos[0] == "call" and len(pos) >= 2 and pos[1] in ("send_chat", "send_sticker"):
        return True
    return False


def main() -> None:
    try:
        raw = sys.stdin.read()
        payload = json.loads(raw) if raw.strip() else {}
    except Exception:
        _deny("유효하지 않은 훅 입력입니다.")
        return

    conv_id = str(payload.get("conversationId") or "").strip()
    last_input = str(payload.get("lastUserInput") or "")
    is_ingame_chat_trigger = "<current_trigger_message>" in last_input

    tc = payload.get("toolCall") or {}
    tname = str(tc.get("name") or "").strip().lower()
    args = tc.get("args") or {}

    if tname in PASSTHROUGH_TOOLS:
        _allow()
        return

    if tname in WEB_SEARCH_TOOLS:
        if _check_search_cap(conv_id):
            _allow()
        else:
            _deny(
                f"웹 검색 횟수 상한({MAX_WEB_SEARCHES}회)에 도달했습니다. "
                "반복 검색을 중단하고 확인된 정보만 존댓말로 요약하거나, "
                "못 찾았다면 '찾아봤는데 관련 정보를 못 찾았어요.'라고 답변하세요."
            )
        return

    if tname in URL_FETCH_TOOLS:
        url = str(args.get("Url") or args.get("url") or "")
        if _is_safe_public_url(url):
            _allow()
        else:
            _deny("공개된 외부 http/https URL만 열람할 수 있습니다.")
        return

    if tname in READ_FILE_TOOLS:
        raw_path = str(args.get("Path") or args.get("path") or args.get("AbsolutePath") or "").strip()
        if not raw_path:
            _deny("파일 경로가 비어 있습니다.")
            return
        try:
            rp = Path(raw_path)
            resolved = (rp if rp.is_absolute() else (WORKSPACE_ROOT / rp)).resolve()
            if resolved == (WORKSPACE_ROOT / "GEMINI.md").resolve() or SKILLS_ROOT in resolved.parents:
                _allow()
                return
        except Exception:
            pass
        _deny("보안 정책상 브릿지 스킬 문서(.agents/skills/stellar-bridge) 외의 서버 로컬 파일 열람은 금지되어 있습니다.")
        return

    if tname == "run_command":
        cmd_line = str(args.get("CommandLine") or args.get("command") or "")
        if not _is_allowed_bridge_command(cmd_line):
            _deny(
                "보안 정책상 블루 프로토콜 브릿지 스크립트(python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py ...) "
                "외의 일반 리눅스 시스템·파일 명령어는 실행할 수 없습니다."
            )
            return
        if is_ingame_chat_trigger:
            if _is_non_commander_trigger(last_input):
                _deny(
                    "권한 없는 발신자(명령권한=없음)의 인게임 채팅 요청에서는 bridge_cli.py를 실행할 수 없습니다. "
                    "actions는 []로 두고 거절 답변만 출력하세요."
                )
                return
            if _is_direct_chat_send_subcommand(cmd_line):
                _deny(
                    "인게임 채팅 자동응답 모드에서는 send-chat/send-sticker를 CLI로 직접 호출하지 마세요. "
                    "최종 JSON의 reply_text와 sticker 필드로 자동 전송됩니다."
                )
                return
        _allow()
        return

    _deny(f"도구 '{tname}'는 블루 프로토콜 전용 에이전트 보안 정책에 의해 차단되어 있습니다.")


if __name__ == "__main__":
    main()
