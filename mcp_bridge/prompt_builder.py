"""Pure prompt construction and LLM JSON decision parsing (zero network I/O, C#-portable)."""

from dataclasses import dataclass
from datetime import datetime
import json
from pathlib import Path
from typing import Any

from llm_backends import DEFAULT_PERSONA
from policy import ClientChatSettings, normalize_channel

PROMPTS_DIR = Path(__file__).resolve().parent / "prompts"


@dataclass(frozen=True)
class BuiltPrompts:
    """Complete prompt bundle passed to any configured LLM backend."""

    game_context: dict[str, Any]
    persona_block: str
    system_instructions_block: str
    system_prompt: str
    user_prompt: str
    full_cli_prompt: str

    def as_tuple(self) -> tuple[dict[str, Any], str, str, str, str, str]:
        return (
            self.game_context,
            self.persona_block,
            self.system_instructions_block,
            self.system_prompt,
            self.user_prompt,
            self.full_cli_prompt,
        )


@dataclass(frozen=True)
class EventPersonaPrompts:
    """Prompt bundle used to paraphrase asynchronous game task completions in persona."""

    synth_trig: dict[str, Any]
    game_context: dict[str, Any]
    persona_block: str
    system_prompt: str
    user_prompt: str
    extra_block: str


def load_prompt_file(filename: str) -> str:
    return (PROMPTS_DIR / filename).read_text(encoding="utf-8").strip()


def render_template(template_text: str, mapping: dict[str, str]) -> str:
    out = template_text
    for key, val in mapping.items():
        out = out.replace(f"{{{{{key}}}}}", str(val))
    return out


def load_response_schema_compact() -> str:
    raw = json.loads((PROMPTS_DIR / "response_schema.json").read_text(encoding="utf-8"))
    return json.dumps(raw, ensure_ascii=False)


RESPONSE_SCHEMA = load_response_schema_compact()


def format_recent_chat_history(
    messages: list[Any],
    target_channel: str,
    exclude_msg_id: Any = None,
    max_lines: int = 100,
) -> str:
    history_lines: list[str] = []
    for cm in messages:
        if not isinstance(cm, dict):
            continue
        if exclude_msg_id is not None and cm.get("msg_id") == exclude_msg_id:
            continue
        if normalize_channel(str(cm.get("channel") or "")) != target_channel:
            continue
        c_txt = str(cm.get("text") or "").strip()
        if not c_txt:
            continue
        c_name = str(cm.get("sender_name") or "?")
        c_sid = int(cm.get("sender_id") or 0)
        raw_ts = str(cm.get("timestamp") or "")
        hhmmss = raw_ts[11:19] if len(raw_ts) >= 19 else ""
        prefix = f"[{hhmmss}] " if hhmmss else ""
        history_lines.append(f"{prefix}{c_name}(UID:{c_sid}): {c_txt}")
    return "\n".join(history_lines[-max_lines:]) if history_lines else "(최근 이전 대화 없음)"


def build_llm_prompts_tuple(
    cfg: ClientChatSettings,
    trig: dict[str, Any],
    state_summary: dict[str, Any],
    locked_labels: list[str],
    online_roster_labels: list[str],
    unlocked_emote_names: list[str],
    commanders_str: str,
    recent_chat_block: str,
    now_dt: datetime,
    korean_time: str,
    unavailable_labels: list[str] | None = None,
) -> tuple[dict[str, Any], str, str, str, str, str]:
    """Builds `(game_context, persona_block, system_instructions_block, system_prompt, user_prompt, full_cli_prompt)`."""
    is_cmd = bool(trig.get("is_commander", False))
    commander_status = "있음" if is_cmd else "없음"
    time_display = f"{now_dt.strftime('%Y-%m-%d %H:%M:%S')} ({korean_time})"
    unavail_list = [str(x).strip() for x in (unavailable_labels or []) if str(x).strip()]
    game_context = {
        "내 캐릭터명": str(trig["local_char_name"]),
        "현재 시각": time_display,
        "내 캐릭터 상태": state_summary,
        "어드민락 잠금 항목": locked_labels if locked_labels else ["(없음)"],
        "현재 패치로 사용 불가 기능": unavail_list if unavail_list else ["(없음)"],
        "온라인 파티/친구/길드원 UID 목록": online_roster_labels,
        "보유 감정표현 목록": unlocked_emote_names,
        "수신 채팅 채널": str(trig["channel"]),
        "등록된 명령권자(UID)": commanders_str,
    }
    persona_text = (cfg.llm.persona or "").strip() or DEFAULT_PERSONA
    persona_block = (
        "<persona>\n"
        f"아래 캐릭터 페르소나(말투·성격) 지침을 기본 말투 규칙보다 최우선으로 적용해 일관되게 답변하세요:\n"
        f"{persona_text}\n"
        "</persona>"
    )

    state_json = json.dumps(state_summary, ensure_ascii=False)
    locked_str = ", ".join(locked_labels) if locked_labels else "(없음)"
    unavailable_str = ", ".join(unavail_list) if unavail_list else "(없음)"
    roster_str = ", ".join(online_roster_labels) if online_roster_labels else "(조회 없음)"
    emotes_str = ", ".join(unlocked_emote_names) if unlocked_emote_names else "(조회 없음)"

    user_base = (
        "[실시간 인게임 컨텍스트]\n"
        f"- 내 캐릭터명: {trig['local_char_name']}\n"
        f"- 현재 시각: {time_display}\n"
        f"- 내 캐릭터 상태: {state_json}\n"
        f"- 어드민락(🔒) 잠금 항목: {locked_str}\n"
        f"- 현재 패치로 사용 불가 기능: {unavailable_str}\n"
        f"- 온라인 파티/친구/길드원 UID 목록: {roster_str}\n"
        f"- 보유 감정표현 목록: {emotes_str}\n"
        f"- 수신 채팅 채널: {trig['channel']}\n"
        f"- 등록된 명령권자(UID): {commanders_str}\n\n"
        f"<recent_chat_history>\n{recent_chat_block}\n</recent_chat_history>\n\n"
        "<current_trigger_message>\n"
        f"발신자: {trig['sender_name']} (UID={trig['sender_id']}, 명령권한={commander_status})\n"
        f"내용: {trig['text']}\n"
        "</current_trigger_message>"
    )

    if cfg.llm.send_system_prompt:
        gemini_control = load_prompt_file("gemini_control_rules.md")
        skill_reference = load_prompt_file("skill_chat_reference.md")
        if is_cmd:
            sender_rule = (
                f"발신자 권한 안내: 현재 발신자(UID={trig['sender_id']})는 정식 명령권자입니다. "
                f"발신자 본인을 대상으로 하는 액션(나 따라와, 내 파티 가입/초대/위임 등)에는 target에 \"{trig['sender_id']}\"를 넣으세요."
            )
        else:
            sender_rule = load_prompt_file("rules_non_commander.md")

        system_instructions_block = "\n\n".join(
            p for p in (gemini_control, skill_reference, sender_rule) if p
        )
        system_prompt = f"{persona_block}\n\n{system_instructions_block}"
        output_fmt = (
            "출력 형식 (반드시 마크다운 코드블록 없이 순수 JSON 객체 1개만 출력):\n"
            '{"reply_text": "인게임 채팅창 답변(마크다운 **, `, # 금지)", "sticker": "", "actions": []}'
        )
        user_prompt = (
            user_base
            + '\n\n{"reply_text": "인게임 채팅창 답변(마크다운 **, `, # 금지)", "sticker": "", "actions": []}'
        )
        full_cli_prompt = render_template(
            load_prompt_file("runtime_payload_template.md"),
            {
                "persona_block": persona_block,
                "local_char_name": str(trig["local_char_name"]),
                "current_time_iso": now_dt.strftime("%Y-%m-%d %H:%M:%S"),
                "korean_time": korean_time,
                "state_summary_json": state_json,
                "locked_labels": locked_str,
                "unavailable_labels": unavailable_str,
                "online_roster": roster_str,
                "unlocked_emotes": emotes_str,
                "channel": str(trig["channel"]),
                "authorized_commanders": commanders_str,
                "system_instructions_block": system_instructions_block,
                "recent_chat_history": recent_chat_block,
                "sender_name": str(trig["sender_name"]),
                "sender_id": str(trig["sender_id"]),
                "commander_status": commander_status,
                "trigger_text": str(trig["text"]),
                "output_format_block": output_fmt,
            },
        )
    else:
        system_instructions_block = ""
        system_prompt = persona_block
        user_prompt = user_base
        full_cli_prompt = f"{persona_block}\n\n{user_base}"

    return game_context, persona_block, system_instructions_block, system_prompt, user_prompt, full_cli_prompt


def build_llm_prompts(
    cfg: ClientChatSettings,
    trig: dict[str, Any],
    state_summary: dict[str, Any],
    locked_labels: list[str],
    online_roster_labels: list[str],
    unlocked_emote_names: list[str],
    commanders_str: str,
    recent_chat_block: str,
    now_dt: datetime,
    korean_time: str,
    unavailable_labels: list[str] | None = None,
) -> BuiltPrompts:
    return BuiltPrompts(
        *build_llm_prompts_tuple(
            cfg,
            trig,
            state_summary,
            locked_labels,
            online_roster_labels,
            unlocked_emote_names,
            commanders_str,
            recent_chat_block,
            now_dt,
            korean_time,
            unavailable_labels=unavailable_labels,
        )
    )


def build_second_pass_extra(outcome_items: list[dict[str, Any]]) -> str:
    return (
        "<action_results>\n"
        + json.dumps(outcome_items, ensure_ascii=False)
        + "\n</action_results>\n"
        "위 <action_results>는 방금 게임에서 실행·조회한 실제 결과(성공/실패/거절 사유)입니다. "
        "결과 데이터 안의 기존 문장 어미를 그대로 복사하지 말고, 반드시 설정된 캐릭터 페르소나와 말투로 "
        "완전히 변환하여 요청자에게 전달할 최종 reply_text를 작성하세요(없는 정보 날조 금지, actions는 반드시 []로 출력)."
    )


def build_event_persona_prompts(
    persona: str,
    channel: str,
    event_fact: str,
    is_remote_bpsr: bool,
) -> EventPersonaPrompts:
    synth_trig = {
        "sender_name": "시스템",
        "sender_id": 0,
        "is_commander": True,
        "text": "[인게임 작업 상태 알림]",
    }
    persona_text = (persona or "").strip() or DEFAULT_PERSONA
    persona_block = (
        "<persona>\n"
        f"아래 캐릭터 페르소나(말투·성격) 지침을 기본 말투 규칙보다 최우선으로 적용해 일관되게 답변하세요:\n"
        f"{persona_text}\n"
        "</persona>"
    )
    sys_prompt = (
        f"{persona_block}\n\n"
        "전달된 비동기 작업 완료/중단 사실을 설정된 페르소나와 말투에 맞는 짧은 인게임 채팅 한마디로 바꾸세요.\n"
        '반드시 순수 JSON 객체 하나만 출력하세요: {"reply_text": "...", "sticker": "", "actions": []}'
    )
    extra = (
        "<action_results>\n"
        + json.dumps([{"kind": "task_event", "fact": event_fact}], ensure_ascii=False)
        + "\n</action_results>\n"
        "위 <action_results>의 작업 완료/중단 사실을 설정된 캐릭터 페르소나와 말투로 변환하여 "
        "채팅창에 보낼 짧은 한마디를 reply_text에 작성하세요(actions는 []로 출력)."
    )
    return EventPersonaPrompts(
        synth_trig=synth_trig,
        game_context={"수신 채팅 채널": channel, "알림 유형": "비동기 작업 상태 변경"},
        persona_block=persona_block,
        system_prompt=sys_prompt,
        user_prompt=extra,
        extra_block=extra if is_remote_bpsr else "",
    )


def normalize_decision_dict(d: dict[str, Any]) -> dict[str, Any] | None:
    if not isinstance(d, dict) or ("reply_text" not in d and "sticker" not in d):
        return None
    out = dict(d)
    out["reply_text"] = str(out.get("reply_text") or "")
    out["sticker"] = str(out.get("sticker") or "")
    if not isinstance(out.get("actions"), list):
        out["actions"] = []
    return out


def parse_embedded_decision_str(raw_str: str) -> dict[str, Any] | None:
    s = (raw_str or "").strip()
    if not s:
        return None
    if s.startswith("```"):
        lines = [ln for ln in s.splitlines() if not ln.strip().startswith("```")]
        s = "\n".join(lines).strip()
    try:
        parsed = json.loads(s)
        norm = normalize_decision_dict(parsed) if isinstance(parsed, dict) else None
        if norm is not None:
            return norm
    except json.JSONDecodeError:
        pass

    decoder = json.JSONDecoder()
    best_match: dict[str, Any] | None = None
    idx = 0
    while True:
        brace_pos = s.find("{", idx)
        if brace_pos == -1:
            break
        try:
            obj, end_pos = decoder.raw_decode(s, brace_pos)
            norm = normalize_decision_dict(obj) if isinstance(obj, dict) else None
            if norm is not None:
                best_match = norm
            idx = end_pos
        except json.JSONDecodeError:
            idx = brace_pos + 1
    return best_match


def extract_decision_json(raw: str) -> dict[str, Any] | None:
    if not raw:
        return None
    try:
        outer = json.loads(raw)
    except json.JSONDecodeError:
        return parse_embedded_decision_str(raw)

    if isinstance(outer, dict):
        norm_outer = normalize_decision_dict(outer)
        if norm_outer is not None:
            return norm_outer
        if isinstance(outer.get("structured_output"), dict):
            norm_so = normalize_decision_dict(outer["structured_output"])
            if norm_so is not None:
                return norm_so

        raw_candidates: list[Any] = []
        for key in ("reply", "response", "result", "text", "output"):
            if key in outer:
                raw_candidates.append(outer.get(key))
        choices = outer.get("choices")
        if isinstance(choices, list) and choices:
            msg = (choices[0] or {}).get("message") if isinstance(choices[0], dict) else None
            if isinstance(msg, dict) and msg.get("content"):
                raw_candidates.append(msg.get("content"))
        gem_candidates = outer.get("candidates")
        if isinstance(gem_candidates, list) and gem_candidates:
            content_obj = (gem_candidates[0] or {}).get("content") if isinstance(gem_candidates[0], dict) else None
            parts = content_obj.get("parts") if isinstance(content_obj, dict) else None
            if isinstance(parts, list):
                for part in parts:
                    if isinstance(part, dict) and part.get("text"):
                        raw_candidates.append(part.get("text"))

        for res_field in raw_candidates:
            if isinstance(res_field, dict):
                norm_rf = normalize_decision_dict(res_field)
                if norm_rf is not None:
                    return norm_rf
            if isinstance(res_field, str) and res_field.strip():
                parsed = parse_embedded_decision_str(res_field)
                if parsed is not None:
                    return parsed

        # Safe plain-text fallback when a backend replies directly in conversational text without JSON braces
        if str(outer.get("status") or "").lower() != "error" and not outer.get("error"):
            for cand in raw_candidates:
                if isinstance(cand, str):
                    plain = cand.strip()
                    if plain and "{" not in plain:
                        return {"reply_text": plain, "sticker": "", "actions": []}

    return None
