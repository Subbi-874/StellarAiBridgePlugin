import ipaddress
import json
import os
from pathlib import Path
import sys
import tempfile
import time
from urllib.parse import urlparse

MAX_WEB_SEARCHES_PER_TURN = int(os.getenv("BPSR_MAX_WEB_SEARCHES", "2"))

ALLOWED_FINISH_TOOLS = {"finish"}
ALLOWED_WEB_SEARCH_TOOLS = {
    "search_web",
    "web_search",
    "google_search",
}
ALLOWED_URL_FETCH_TOOLS = {
    "read_url_content",
    "fetch_url",
}


def _check_and_increment_search_count(conv_id: str) -> tuple[bool, int]:
    if not conv_id:
        return True, 1
    safe_id = "".join(ch for ch in conv_id if ch.isalnum() or ch in ("-", "_"))[:64]
    if not safe_id:
        return True, 1
    state_dir = Path(tempfile.gettempdir()) / "stellar_agy_search_caps"
    try:
        state_dir.mkdir(parents=True, exist_ok=True)
        # Opportunistically sweep old counter files (>10 min)
        now = time.time()
        for p in state_dir.glob("cnt_*.txt"):
            try:
                if now - p.stat().st_mtime > 600:
                    p.unlink(missing_ok=True)
            except OSError:
                pass
        cnt_file = state_dir / f"cnt_{safe_id}.txt"
        cur = int(cnt_file.read_text(encoding="utf-8").strip()) if cnt_file.exists() else 0
        if cur >= MAX_WEB_SEARCHES_PER_TURN:
            return False, cur
        cur += 1
        cnt_file.write_text(str(cur), encoding="utf-8")
        return True, cur
    except Exception:
        return True, 1


def _is_safe_public_http_url(url: str) -> bool:
    if not url:
        return False
    try:
        parsed = urlparse(url.strip())
    except Exception:
        return False
    if parsed.scheme.lower() not in ("http", "https"):
        return False
    host = (parsed.hostname or "").strip().lower()
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


def main() -> None:
    try:
        raw = sys.stdin.read()
        payload = json.loads(raw) if raw.strip() else {}
    except Exception:
        print(json.dumps({"decision": "deny", "reason": "Invalid hook payload"}))
        return

    conv_id = str(payload.get("conversationId") or "").strip()
    tool_call = payload.get("toolCall") or {}
    tool_name = str(tool_call.get("name") or "").strip().lower()
    args = tool_call.get("args") or {}

    if tool_name in ALLOWED_FINISH_TOOLS:
        print(json.dumps({"decision": "allow"}))
        return

    if tool_name in ALLOWED_WEB_SEARCH_TOOLS:
        allowed, count = _check_and_increment_search_count(conv_id)
        if allowed:
            print(json.dumps({"decision": "allow"}))
        else:
            print(
                json.dumps(
                    {
                        "decision": "deny",
                        "reason": (
                            f"웹 검색 횟수 상한({MAX_WEB_SEARCHES_PER_TURN}회)에 도달했습니다. "
                            "억지로 반복 검색하지 말고 지금까지 찾은 내용만 존댓말로 요약하거나, "
                            "못 찾았다면 '찾아봤는데 관련 정보를 못 찾았어요.'라고 즉시 답변하세요."
                        ),
                    },
                    ensure_ascii=False,
                )
            )
        return

    if tool_name in ALLOWED_URL_FETCH_TOOLS:
        url = str(args.get("Url") or args.get("url") or "")
        if _is_safe_public_http_url(url):
            print(json.dumps({"decision": "allow"}))
            return
        print(
            json.dumps(
                {
                    "decision": "deny",
                    "reason": "Only public http/https URLs are permitted in in-game chat bot mode.",
                }
            )
        )
        return

    print(
        json.dumps(
            {
                "decision": "deny",
                "reason": f"Tool '{tool_name}' is blocked in in-game chat bot mode. Only internet search (search_web) is permitted.",
            }
        )
    )


if __name__ == "__main__":
    main()
