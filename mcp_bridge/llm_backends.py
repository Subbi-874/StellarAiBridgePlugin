import asyncio
from dataclasses import dataclass, field
import json
import logging
import os
from pathlib import Path
import shlex
import shutil
import socket
from typing import Any
import urllib.error
import urllib.parse
import urllib.request

import config

logger = logging.getLogger("agy-bridge.llm")

PROMPTS_DIR = Path(__file__).resolve().parent / "prompts"
BOT_SANDBOX_DIR = Path(__file__).resolve().parent / "bot_sandbox"

DEFAULT_PERSONA = (
    "존댓말을 써서 과하지 않은 젊은 영애 아가씨처럼 친절하고 자연스럽게 대화하세요. "
    "반말과 존댓말을 섞어 쓰지 마세요."
)

VALID_BACKENDS = ("remote_bpsr", "local_cli", "openai_api", "gemini_api")
VALID_CLI_FLAVORS = ("agy", "claude", "codex", "gemini", "custom")


@dataclass
class LlmClientConfig:
    backend: str = config.LLM_BACKEND if config.LLM_BACKEND in VALID_BACKENDS else "remote_bpsr"
    cli_flavor: str = "agy"
    endpoint_url: str = config.REMOTE_BPSR_CHAT_URL
    api_key: str = ""
    model: str = ""
    cli_command: str = ""
    cli_effort: str = config.AGY_CLI_EFFORT
    persona: str = DEFAULT_PERSONA
    send_system_prompt: bool = field(default_factory=lambda: config.LLM_BACKEND != "remote_bpsr")

    @classmethod
    def from_ui_dict(cls, data: dict[str, Any] | None) -> "LlmClientConfig":
        cfg = cls()
        if not isinstance(data, dict):
            return cfg

        backend = str(data.get("backend") or "").strip().lower()
        if backend in ("local_agy", "cli"):
            backend = "local_cli"
        if backend in VALID_BACKENDS:
            cfg.backend = backend

        flavor = str(data.get("cli_flavor") or "").strip().lower()
        if flavor in VALID_CLI_FLAVORS:
            cfg.cli_flavor = flavor

        ep = str(data.get("endpoint_url") or "").strip()
        if ep:
            cfg.endpoint_url = ep

        key = str(data.get("api_key") or "").strip()
        if key and key != "***":
            cfg.api_key = key

        cfg.model = str(data.get("model") or "").strip()
        cfg.cli_command = str(data.get("cli_command") or "").strip()

        effort = str(data.get("cli_effort") or "").strip().lower()
        if effort in ("low", "medium", "high"):
            cfg.cli_effort = effort

        persona = str(data.get("persona") or "").strip()
        if persona:
            cfg.persona = persona

        if "send_system_prompt" in data:
            cfg.send_system_prompt = bool(data["send_system_prompt"])
        else:
            cfg.send_system_prompt = cfg.backend != "remote_bpsr"
        return cfg

    def masked_dict(self) -> dict[str, Any]:
        return {
            "backend": self.backend,
            "cli_flavor": self.cli_flavor,
            "endpoint_url": self.endpoint_url,
            "api_key": "***" if self.api_key else "",
            "has_api_key": bool(self.api_key),
            "model": self.model,
            "cli_command": self.cli_command,
            "cli_effort": self.cli_effort,
            "persona": self.persona,
            "send_system_prompt": self.send_system_prompt,
        }


def normalize_remote_bpsr_url(raw_url: str) -> str:
    url = (raw_url or "").strip() or config.REMOTE_BPSR_CHAT_URL
    parsed = urllib.parse.urlparse(url)
    if parsed.path in ("", "/"):
        return url.rstrip("/") + "/bpsr/chat"
    return url


def normalize_openai_chat_url(raw_url: str) -> str:
    base = (raw_url or "").strip() or "https://api.openai.com/v1"
    base = base.rstrip("/")
    if base.endswith("/chat/completions"):
        return base
    return f"{base}/chat/completions"


def resolve_cli_executable(flavor: str, custom_cmd: str) -> list[str]:
    custom = (custom_cmd or "").strip()
    if custom:
        # Handle raw Windows executable paths cleanly even if they contain spaces without quotes
        if Path(custom).exists() or custom.lower().endswith((".exe", ".cmd", ".bat", ".ps1")):
            return [custom]
        parts = shlex.split(custom, posix=False)
        if parts:
            parts[0] = shutil.which(parts[0]) or parts[0]
            return parts

    candidates_map: dict[str, list[str]] = {
        "agy": ["agy", config.AGY_CLI_PATH, r"C:\Users\cjs_9\AppData\Local\agy\bin\agy.EXE"],
        "claude": ["claude", str(Path.home() / "AppData/Roaming/npm/claude.cmd")],
        "codex": ["codex", str(Path.home() / "AppData/Roaming/npm/codex.cmd")],
        "gemini": ["gemini", str(Path.home() / "AppData/Roaming/npm/gemini.cmd")],
    }
    for cand in candidates_map.get(flavor, []):
        if not cand:
            continue
        found = shutil.which(cand)
        if found:
            return [found]
        if Path(cand).exists():
            return [cand]
    return [flavor] if flavor != "custom" else []


def build_cli_invocation(
    llm: LlmClientConfig,
    full_prompt: str,
    schema_path: str,
    schema_compact: str,
) -> tuple[list[str], bytes | None]:
    flavor = llm.cli_flavor if llm.cli_flavor in VALID_CLI_FLAVORS else "agy"
    parts = resolve_cli_executable(flavor, llm.cli_command)
    if not parts:
        raise RuntimeError("No local CLI executable configured.")

    exe = parts[0]
    extra = parts[1:]
    model = llm.model.strip()

    if flavor == "agy":
        argv_prompt = full_prompt if len(full_prompt) <= 28000 else full_prompt[-28000:]
        cmd = [
            exe,
            *extra,
            "-p",
            argv_prompt,
            "--model",
            model or config.AGY_CLI_MODEL,
            "--effort",
            llm.cli_effort or config.AGY_CLI_EFFORT,
            "--output-format",
            "json",
        ]
        if llm.send_system_prompt:
            cmd.extend(["--json-schema", schema_path])
        cmd.extend(["--disable-slash-commands", "--dangerously-skip-permissions"])
        stdin_bytes: bytes | None = None
    elif flavor == "claude":
        cmd = [
            exe,
            *extra,
            "-p",
            "--output-format",
            "json",
            "--permission-mode",
            "bypassPermissions",
        ]
        if llm.send_system_prompt:
            cmd.extend(["--json-schema", schema_compact])
        if model:
            cmd.extend(["--model", model])
        stdin_bytes = full_prompt.encode("utf-8")
    elif flavor == "codex":
        cmd = [exe, *extra, "exec", "-"]
        if llm.send_system_prompt:
            cmd.extend(["--output-schema", schema_path])
        if model:
            cmd.extend(["-m", model])
        stdin_bytes = full_prompt.encode("utf-8")
    elif flavor == "gemini":
        cmd = [exe, *extra, "-p", "", "-o", "json"]
        if model:
            cmd.extend(["-m", model])
        stdin_bytes = full_prompt.encode("utf-8")
    else:  # custom CLI
        if "{prompt}" in extra:
            cmd = [exe, *[full_prompt if a == "{prompt}" else a for a in extra]]
            stdin_bytes = None
        else:
            cmd = [exe, *extra]
            stdin_bytes = full_prompt.encode("utf-8")

    if os.name == "nt" and exe.lower().endswith((".cmd", ".bat")):
        cmd = ["cmd.exe", "/d", "/s", "/c", *cmd]

    return cmd, stdin_bytes


def _is_timeout_error(exc: BaseException) -> bool:
    if isinstance(exc, (TimeoutError, socket.timeout)):
        return True
    if isinstance(exc, urllib.error.URLError) and isinstance(getattr(exc, "reason", None), (TimeoutError, socket.timeout)):
        return True
    return False


async def call_remote_bpsr(
    llm: LlmClientConfig,
    trig: dict[str, Any],
    game_context: dict[str, Any],
    recent_chat_block: str,
    persona_block: str = "",
    system_instructions_block: str = "",
    extra_block: str = "",
) -> tuple[str, bool]:
    is_cmd = bool(trig.get("is_commander", False))
    commander_status = "있음" if is_cmd else "없음"
    parts: list[str] = []
    if persona_block.strip():
        parts.append(persona_block.strip())
    if llm.send_system_prompt and system_instructions_block.strip():
        parts.append(
            "<system_instructions>\n"
            + system_instructions_block.strip()
            + "\n</system_instructions>"
        )
    parts.append(f"<recent_chat_history>\n{recent_chat_block}\n</recent_chat_history>")
    parts.append(
        "<current_trigger_message>\n"
        f"발신자: {trig['sender_name']} (UID={trig['sender_id']}, 명령권한={commander_status})\n"
        f"내용: {trig['text']}\n"
        "</current_trigger_message>"
    )
    if extra_block:
        parts.append(extra_block)

    url = normalize_remote_bpsr_url(llm.endpoint_url)
    req_payload: dict[str, Any] = {
        "message": "\n\n".join(parts),
        "context": game_context,
        "model": llm.model or config.REMOTE_BPSR_MODEL,
        "stream": False,
    }
    if llm.send_system_prompt:
        sys_joined = "\n\n".join(
            p for p in (persona_block.strip(), system_instructions_block.strip()) if p
        )
        if sys_joined:
            req_payload["system_prompt"] = sys_joined
    headers = {"Content-Type": "application/json"}
    if llm.api_key:
        headers["Authorization"] = f"Bearer {llm.api_key}"

    def _post() -> str:
        data = json.dumps(req_payload, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(url, data=data, headers=headers, method="POST")
        with urllib.request.urlopen(req, timeout=config.REMOTE_BPSR_TIMEOUT) as resp:
            return resp.read().decode("utf-8", errors="replace").strip()

    try:
        return await asyncio.to_thread(_post), False
    except Exception as exc:
        if _is_timeout_error(exc):
            logger.warning("Remote BPSR LLM timed out after %.1fs: %r", config.REMOTE_BPSR_TIMEOUT, exc)
            return "", True
        raise


async def call_openai_compatible(
    llm: LlmClientConfig,
    system_prompt: str,
    user_prompt: str,
) -> tuple[str, bool]:
    url = normalize_openai_chat_url(llm.endpoint_url)
    model = llm.model.strip() or "gpt-4.1-mini"
    headers = {"Content-Type": "application/json"}
    if llm.api_key:
        headers["Authorization"] = f"Bearer {llm.api_key}"

    def _post(use_json_mode: bool) -> str:
        payload: dict[str, Any] = {
            "model": model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "temperature": 0.6,
        }
        if use_json_mode:
            payload["response_format"] = {"type": "json_object"}
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(url, data=data, headers=headers, method="POST")
        with urllib.request.urlopen(req, timeout=config.REMOTE_BPSR_TIMEOUT) as resp:
            return resp.read().decode("utf-8", errors="replace").strip()

    want_json_mode = bool(llm.send_system_prompt)
    try:
        try:
            return await asyncio.to_thread(_post, want_json_mode), False
        except urllib.error.HTTPError as he:
            if want_json_mode and he.code in (400, 404, 422):
                # Some custom/local OpenAI-compatible servers reject response_format
                return await asyncio.to_thread(_post, False), False
            raise
    except Exception as exc:
        if _is_timeout_error(exc):
            logger.warning("OpenAI-compatible API timed out after %.1fs: %r", config.REMOTE_BPSR_TIMEOUT, exc)
            return "", True
        raise


async def call_gemini_api(
    llm: LlmClientConfig,
    system_prompt: str,
    user_prompt: str,
) -> tuple[str, bool]:
    api_key = llm.api_key.strip()
    if not api_key:
        raise RuntimeError("Google Gemini API Key가 설정되지 않았습니다.")

    model = llm.model.strip() or "gemini-2.5-flash"
    if not model.startswith("models/"):
        model_path = f"models/{model}"
    else:
        model_path = model

    base = (llm.endpoint_url or "").strip().rstrip("/")
    if not base or "192.168." in base or base.endswith("/bpsr/chat"):
        base = "https://generativelanguage.googleapis.com/v1beta"
    url = f"{base}/{model_path}:generateContent"

    gen_cfg: dict[str, Any] = {"temperature": 0.6}
    if llm.send_system_prompt:
        gen_cfg["responseMimeType"] = "application/json"

    payload = {
        "systemInstruction": {"parts": [{"text": system_prompt}]},
        "contents": [{"role": "user", "parts": [{"text": user_prompt}]}],
        "generationConfig": gen_cfg,
    }

    def _post() -> str:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(
            url,
            data=data,
            headers={
                "Content-Type": "application/json",
                "x-goog-api-key": api_key,
            },
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=config.REMOTE_BPSR_TIMEOUT) as resp:
            return resp.read().decode("utf-8", errors="replace").strip()

    try:
        return await asyncio.to_thread(_post), False
    except Exception as exc:
        if _is_timeout_error(exc):
            logger.warning("Gemini API timed out after %.1fs: %r", config.REMOTE_BPSR_TIMEOUT, exc)
            return "", True
        raise


async def call_local_cli(
    llm: LlmClientConfig,
    full_prompt: str,
    schema_compact: str,
) -> tuple[str, bool]:
    schema_path = str((PROMPTS_DIR / "response_schema.json").resolve())
    BOT_SANDBOX_DIR.mkdir(parents=True, exist_ok=True)
    cmd, stdin_bytes = build_cli_invocation(llm, full_prompt, schema_path, schema_compact)

    async def _run_once(run_cmd: list[str]) -> tuple[str, str, int | None, bool]:
        proc = await asyncio.create_subprocess_exec(
            *run_cmd,
            cwd=str(BOT_SANDBOX_DIR),
            stdin=asyncio.subprocess.PIPE if stdin_bytes is not None else asyncio.subprocess.DEVNULL,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        try:
            stdout_b, stderr_b = await asyncio.wait_for(
                proc.communicate(input=stdin_bytes),
                timeout=config.AGY_CLI_TIMEOUT,
            )
            raw_out = stdout_b.decode("utf-8", errors="replace").strip()
            err_txt = stderr_b.decode("utf-8", errors="replace").strip()
            return raw_out, err_txt, proc.returncode, False
        except asyncio.TimeoutError:
            logger.warning(
                "Local CLI (%s) timed out after %.1fs; killing pid=%s",
                llm.cli_flavor,
                config.AGY_CLI_TIMEOUT,
                proc.pid,
            )
            try:
                proc.kill()
            except ProcessLookupError:
                pass
            try:
                await asyncio.wait_for(proc.wait(), timeout=3.0)
            except Exception:
                pass
            return "", "", None, True

    raw_out, err_txt, rc, timed_out = await _run_once(cmd)
    if timed_out:
        return "", True

    if (
        llm.cli_flavor == "agy"
        and "--effort" in cmd
        and ("--effort is not supported" in raw_out or "--effort is not supported" in err_txt)
    ):
        ef_idx = cmd.index("--effort")
        retry_cmd = cmd[:ef_idx] + cmd[ef_idx + 2 :]
        logger.info("Local AGY CLI rejected --effort for selected model; retrying once without --effort")
        raw_out, err_txt, rc, timed_out = await _run_once(retry_cmd)
        if timed_out:
            return "", True

    if not raw_out and err_txt:
        logger.warning("Local CLI (%s) empty stdout (rc=%s): err=%s", llm.cli_flavor, rc, err_txt[:300])
    return raw_out, False
