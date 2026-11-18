# Stellar AGY Bridge — 리눅스 LLM 서버(`/home/subbi/bpsr`) 연동 & 보안 명세서

인게임 채팅 자동응답 및 외부 직접 제어 두뇌(LLM)를 리눅스 서버(`http://192.168.2.20:28000/bpsr/chat`)의 격리 워크스페이스(`/home/subbi/bpsr`)로 이관한 구성 문서입니다.

---

## 1. 리눅스 서버 배치 구조 (`/home/subbi/bpsr`)

로컬 미러 원본은 `mcp_bridge/prompts/linux_bpsr_workspace/`에 보관되어 있으며, 리눅스 서버에는 아래 경로로 배포됩니다:

| 리눅스 경로 | 로컬 미러 원본 | 역할 |
| :--- | :--- | :--- |
| `/home/subbi/bpsr/GEMINI.md` (`.gemini.md` 심볼릭 링크 포함) | [GEMINI.md](file:///E:/StarLauncher/StellarAgyBridge/mcp_bridge/prompts/linux_bpsr_workspace/GEMINI.md) | **핵심 페르소나 일관성(`<persona>`) · 검색 상한(1~2회) · 보안 지침 · 동작 모드 A/B 분기 규칙** (수정 즉시 mtime 핫리로드) |
| `/home/subbi/bpsr/.agents/skills/stellar-bridge/SKILL.md` | [SKILL.md](file:///E:/StarLauncher/StellarAgyBridge/mcp_bridge/prompts/linux_bpsr_workspace/SKILL.md) | **비서앱 및 직접 제어 공용 브릿지 스킬 명세** |
| `/home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py` | [bridge_cli.py](file:///E:/StarLauncher/StellarAgyBridge/mcp_bridge/prompts/linux_bpsr_workspace/bridge_cli.py) | 윈도우 브릿지(`http://192.168.2.21:18765/api/call`) 호출 전용 CLI 헬퍼 |
| `/home/subbi/bpsr/.agents/hooks.json` | [hooks.json](file:///E:/StarLauncher/StellarAgyBridge/mcp_bridge/prompts/linux_bpsr_workspace/hooks.json) | `PreToolUse` 보안 훅 설정 |
| `/home/subbi/bpsr/.agents/bpsr_guard.py` | [bpsr_guard.py](file:///E:/StarLauncher/StellarAgyBridge/mcp_bridge/prompts/linux_bpsr_workspace/bpsr_guard.py) | ① 턴당 `search_web` 최대 2회 하드캡 ② 브릿지 외 리눅스 파일/시스템 명령 차단 ③ 인게임 채팅 트리거 모드 시 `run_command` 전면 차단 |

---

## 2. 윈도우 브릿지 설정 (`mcp_bridge/config.py` & `chat_monitor.py` & `llm_backends.py`)

- **포트 바인딩**: `BRIDGE_HOST=0.0.0.0`, `BRIDGE_PORT=18765` (리눅스 `192.168.2.20` → 윈도우 `192.168.2.21:18765` 통신 개방)
- **LLM 백엔드 / 시스템 프롬프트 전송 토글**: UI에서 `remote_bpsr`, `local_cli`(`agy`/`claude`/`codex`/`gemini`/`custom`), `openai_api`, `gemini_api` 선택 및 편집 가능한 `<persona>`, `send_system_prompt` 토글 지원
- **재발 방지 처리**:
  1. `search_web` 최대 2회 제한 + 안 나오는 정보는 반복 검색 없이 `<persona>` 말투에 맞춰 못 찾았다고 응답
  2. 말투는 `<persona>` 지침을 일관되게 따르며 반말·존댓말 혼용 금지(고정 말투 강제 없음)
  3. 타임아웃(`REMOTE_BPSR_TIMEOUT=75.0s`) 발생 시 소켓 즉시 정리(로컬 폴백 시 `proc.kill()`) 및 인게임 지연 안내 메시지 전송

---

## 3. 동작 모드 2가지 요약

1. **[모드 A] 인게임 채팅 자동응답 (`<current_trigger_message>` 포함 요청)**
   - 윈도우 [chat_monitor.py](file:///E:/StarLauncher/StellarAgyBridge/mcp_bridge/chat_monitor.py)가 인게임 채팅을 감지하면 `context`(캐릭터 상태, 어드민락 목록, 온라인 파티/친구/길드 UID, 보유 이모트)와 `<recent_chat_history>`, `<current_trigger_message>`를 묶어 `POST /bpsr/chat`으로 전송합니다.
   - 리눅스 AGY는 `run_command` 없이(필요 시 `search_web` 최대 2회만 수행) `{"reply_text": "...", "actions": [...]}` JSON만 반환하며, 윈도우 브릿지가 명령권자 UID 및 인게임 어드민락(🔒)을 최종 검증해 실행합니다.
2. **[모드 B] 외부 직접 제어 (`<current_trigger_message>` 미포함 일반 지시 / 비서앱 공용)**
   - `/bpsr/chat` 또는 비서앱에서 직접 게임 상태 조회·추적·이동·채집·파티·이모트·채팅을 명령하면 `stellar-bridge` 스킬(`bridge_cli.py`)을 통해 윈도우 브릿지 API(`http://192.168.2.21:18765/api/call`)를 직접 호출하여 수행합니다.
