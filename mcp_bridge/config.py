import os
from pathlib import Path
import shutil

BRIDGE_HOST = os.getenv("BRIDGE_HOST", "0.0.0.0")
BRIDGE_PORT = int(os.getenv("BRIDGE_PORT", "18765"))
BRIDGE_TOKEN = os.getenv("BRIDGE_TOKEN", "")
_LOCAL_CONNECT_HOST = "127.0.0.1" if BRIDGE_HOST in ("0.0.0.0", "::", "") else BRIDGE_HOST
BRIDGE_HTTP_BASE = os.getenv("BRIDGE_HTTP_BASE", f"http://{_LOCAL_CONNECT_HOST}:{BRIDGE_PORT}")
DEFAULT_RPC_TIMEOUT = float(os.getenv("BRIDGE_RPC_TIMEOUT", "12.0"))

# LLM backend selection: 'local_agent' (blank URL -> local CLI, non-empty URL -> remote HTTP /bpsr/chat),
# 'openai_api', or 'gemini_api' (legacy aliases 'remote_bpsr' / 'local_cli' auto-migrate to 'local_agent').
LLM_BACKEND = os.getenv("BPSR_LLM_BACKEND", "remote_bpsr").strip().lower()

REMOTE_BPSR_BASE_URL = os.getenv("REMOTE_BPSR_BASE_URL", "http://192.168.2.20:28000").rstrip("/")
REMOTE_BPSR_CHAT_URL = os.getenv("REMOTE_BPSR_CHAT_URL", f"{REMOTE_BPSR_BASE_URL}/bpsr/chat")
REMOTE_BPSR_HEALTH_URL = os.getenv("REMOTE_BPSR_HEALTH_URL", f"{REMOTE_BPSR_BASE_URL}/bpsr/health")
REMOTE_BPSR_MODEL = os.getenv("REMOTE_BPSR_MODEL", "gemini-4-hydrogen-low")
REMOTE_BPSR_TIMEOUT = float(os.getenv("REMOTE_BPSR_TIMEOUT", "75.0"))

AGY_CLI_PATH = (
    os.getenv("AGY_CLI_PATH")
    or shutil.which("agy")
    or r"C:\Users\cjs_9\AppData\Local\agy\bin\agy.EXE"
)
AGY_CLI_MODEL = os.getenv("AGY_CLI_MODEL", "gemini-4-hydrogen-low")
AGY_CLI_EFFORT = os.getenv("AGY_CLI_EFFORT", "low")
AGY_CLI_TIMEOUT = float(os.getenv("AGY_CLI_TIMEOUT", "75.0"))

MAX_CONCURRENT_LLM_CALLS = max(1, int(os.getenv("MAX_CONCURRENT_LLM_CALLS", "3")))
LLM_BUDGET_PER_MINUTE = max(1, int(os.getenv("LLM_BUDGET_PER_MINUTE", "20")))
LLM_BUDGET_PER_GOAL = max(1, int(os.getenv("LLM_BUDGET_PER_GOAL", "8")))
API_TOOL_LOOP_MAX_ROUNDS = max(1, int(os.getenv("API_TOOL_LOOP_MAX_ROUNDS", "5")))
API_TOOL_LOOP_MAX_CALLS = max(1, int(os.getenv("API_TOOL_LOOP_MAX_CALLS", "10")))


def _default_memory_db_path() -> str:
    local_app = os.getenv("LOCALAPPDATA", "").strip()
    if local_app:
        return str(Path(local_app) / "StellarAgyBridge" / "memory.db")
    return str(Path.home() / ".local" / "share" / "StellarAgyBridge" / "memory.db")


MEMORY_DB_PATH = os.getenv("BRIDGE_MEMORY_DB_PATH", _default_memory_db_path())
MEMORY_MAX_PER_BOT = max(20, int(os.getenv("MEMORY_MAX_PER_BOT", "300")))
MEMORY_EVENTS_MAX_PER_BOT = max(20, int(os.getenv("MEMORY_EVENTS_MAX_PER_BOT", "500")))
