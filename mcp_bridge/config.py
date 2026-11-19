import os
import shutil

BRIDGE_HOST = os.getenv("BRIDGE_HOST", "0.0.0.0")
BRIDGE_PORT = int(os.getenv("BRIDGE_PORT", "18765"))
BRIDGE_TOKEN = os.getenv("BRIDGE_TOKEN", "")
_LOCAL_CONNECT_HOST = "127.0.0.1" if BRIDGE_HOST in ("0.0.0.0", "::", "") else BRIDGE_HOST
BRIDGE_HTTP_BASE = os.getenv("BRIDGE_HTTP_BASE", f"http://{_LOCAL_CONNECT_HOST}:{BRIDGE_PORT}")
DEFAULT_RPC_TIMEOUT = float(os.getenv("BRIDGE_RPC_TIMEOUT", "12.0"))

# LLM backend selection: 'remote_bpsr' (Linux /bpsr/chat server) or 'local_agy' (Windows agy.exe CLI)
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
