# Stellar.AgyBridge

Blue Protocol: Star Resonance용 **[스텔라 레조넌스(StellarResonance)](https://github.com/StellarProtocol/StellarResonanceModSystem)** 프레임워크 플러그인입니다. **단일 DLL(`Stellar.AgyBridge.dll`)** 기반 AI 브리지 · MCP 서버 · 인게임 자동화 기능을 제공합니다.

> 이 플러그인은 [스텔라 레조넌스 (StellarResonanceModSystem)](https://github.com/StellarProtocol/StellarResonanceModSystem) 위에서 동작합니다. ([런처](https://github.com/StellarProtocol/StellarResonance) · [공식 문서](https://docs.stellarresonance.app))

별도의 Python 런타임이나 외부 스크립트 프로세스 없이, 게임 플러그인 DLL 하나만으로 **내장 MCP Streamable HTTP 서버**, **WebSocket 멀티클라이언트 허브**, **REST API**, 그리고 **SQLite(WAL + FTS5) 장기 기억 저장소**까지 모두 인프로세스로 구동됩니다.

---

## 1. 핵심 특징

- **단일 DLL 배포**: `stellar/plugins/agybridge/Stellar.AgyBridge.dll` 파일 하나만 배치하면 동작합니다. (Python·uvicorn·별도 `.dll` 파일 불필요)
- **내장 네이티브 SQLite(`e_sqlite3.dll`)**: 공식 `SQLitePCLRaw.lib.e_sqlite3` 바이너리가 DLL 리소스로 내장되어 있으며, 실행 시 `%LOCALAPPDATA%\StellarAgyBridge\native\`에 자동 추출되어 WAL + FTS5 장기 기억 DB(`%LOCALAPPDATA%\StellarAgyBridge\memory.db`)를 사용합니다.
- **리더-팔로워(Leader-Follower) 멀티 클라이언트 아키텍처**:
  - 첫 번째로 실행된 게임 클라이언트가 기본 포트 **`18765`** 를 점유하여 **리더(Leader)** 로서 HTTP/MCP/WebSocket 허브와 SQLite 기억 저장소·대화 감시기를 호스팅합니다.
  - 이후 실행된 게임 클라이언트들은 자동으로 **팔로워(Follower)** 모드로 전환되어 리더의 `ws://127.0.0.1:18765/ws`에 합류합니다.
  - **리더 종료 시 자동 페일오버(Failover)**: 리더 게임 클라이언트가 종료되면 포트가 해제되고, 대기 중이던 팔로워 중 하나가 다음 재연결 주기에 포트 `18765`를 인계받아 새 리더로 자동 승격됩니다(동일한 SQLite DB 계속 사용).

---

## 2. 설치 방법

1. 프로젝트를 빌드합니다.
   ```powershell
   dotnet build plugin/Stellar.AgyBridge.csproj -c Release
   ```
2. 빌드 결과물 `plugin/bin/Release/Stellar.AgyBridge.dll` 파일 **만** 게임 폴더의 플러그인 디렉터리에 복사합니다.
   - `<게임_루트>/stellar/plugins/agybridge/Stellar.AgyBridge.dll`
   - 참고: `stellar/plugins/` 하위에는 `Stellar.AgyBridge.dll` 외의 다른 `.dll` 파일(예: `e_sqlite3.dll`)을 두지 마세요.

---

## 3. 외부 LLM / MCP 연결 엔드포인트 (기본 포트: `18765`)

게임 클라이언트가 하나 이상 실행 중이면(리더 활성화 상태), 외부 LLM 클라이언트나 자동화 도구는 고정 엔드포인트 **`127.0.0.1:18765`** 로 바로 접근할 수 있습니다.

| 프로토콜 | 기본 URL | 용도 |
| --- | --- | --- |
| **MCP (Streamable HTTP)** | `http://127.0.0.1:18765/mcp` | Antigravity, Claude Code, Cursor 등 외부 MCP 클라이언트 연결 (63개 게임 제어/기억 도구 제공) |
| **WebSocket** | `ws://127.0.0.1:18765/ws` | 인게임 클라이언트(리더 자신 및 팔로워) 연결 채널 |
| **REST 상태 조회** | `GET http://127.0.0.1:18765/api/status` | 현재 브리지 역할(`leader`), 연결된 클라이언트 목록, 이벤트 버퍼 상태 조회 |
| **REST 이벤트 조회** | `GET http://127.0.0.1:18765/api/events?limit=50` | 최근 게임 이벤트 버퍼 조회 |
| **REST RPC 호출** | `POST http://127.0.0.1:18765/api/call` | JSON 바디 `{"method": "...", "params": {...}, "conn_id": "(선택)"}`로 직접 RPC 실행 |

### MCP 클라이언트 설정 예시 (Streamable HTTP)

Antigravity / Claude Code / Cursor 등의 MCP 설정(`mcp_config.json`, `.mcp.json` 등)에 아래와 같이 등록하세요.

```json
{
  "mcpServers": {
    "stellar-agy-bridge": {
      "type": "http",
      "url": "http://127.0.0.1:18765/mcp"
    }
  }
}
```

인증 토큰(`auth_token` 또는 `BRIDGE_TOKEN`)을 설정한 경우에는 `Authorization: Bearer <YOUR_TOKEN>` 헤더를 함께 지정하세요.

```json
{
  "mcpServers": {
    "stellar-agy-bridge": {
      "type": "http",
      "url": "http://127.0.0.1:18765/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN"
      }
    }
  }
}
```

### 멀티 클라이언트 전환

여러 게임 클라이언트를 동시에 실행한 경우 MCP 도구나 `/api/call`을 통해 대상 캐릭터를 확인/전환할 수 있습니다.

- `list_game_clients` (REST RPC: `list_clients`): 연결된 모든 클라이언트와 캐릭터명(`char_name`), `char_id`, `scene_name`, `conn_id` 조회
- `select_game_client` (REST RPC: `select_client`, `{"selector": "캐릭터명 또는 conn_id 또는 PID"}`): 제어 대상 활성 클라이언트 전환
- 또는 `POST /api/call` 요청 본문에 `"conn_id": "client-2"`를 지정하면 전역 활성 클라이언트를 바꾸지 않고 특정 클라이언트만 호출할 수 있습니다.

---

## 4. 포트 · 인증 · 데이터 경로 설정

### 플러그인 설정 키 (`BridgeConfig`)

- `bridge_url` (기본값: `ws://127.0.0.1:18765/ws`): 접속/호스팅 대상 브리지 URL. 포트를 바꾸면(예: `ws://127.0.0.1:19000/ws`) 리더가 해당 포트로 열립니다.
- `auto_start_local_bridge` (기본값: `true`): 로컬 주소(`127.0.0.1`, `localhost`, `::1`, `0.0.0.0`)일 때 리더 자동 기동 여부. 외부 브리지만 사용하려면 `false`로 설정하세요.
- `auth_token` (기본값: `""`): 비어 있지 않으면 `/mcp`, `/api/call`, `/ws` 요청에 `Authorization: Bearer <token>` 검증을 적용합니다.

### 환경 변수 (선택)

- `BRIDGE_HOST`: 리더 HTTP/WS 소켓 바인드 IP 재정의 (기본은 URL 호스트 기준 루프백 `127.0.0.1`)
- `BRIDGE_PORT`: URL에 명시적 포트가 없을 때 사용할 기본 포트 (기본값 `18765`)
- `BRIDGE_TOKEN`: 플러그인 설정 `auth_token`이 비어 있을 때 사용하는 Bearer 인증 토큰
- `BRIDGE_MEMORY_DB_PATH`: SQLite 기억 DB 경로 재정의 (기본값 `%LOCALAPPDATA%\StellarAgyBridge\memory.db`)
- `BPSR_LLM_BACKEND`, `REMOTE_BPSR_CHAT_URL`, `AGY_CLI_MODEL` 등: 내장 채팅 감시기 LLM 백엔드 설정

---

## 5. 빌드

```powershell
# 플러그인 Release 빌드 (단일 DLL 생성)
dotnet build plugin/Stellar.AgyBridge.csproj -c Release --nologo -v minimal
```

---

## 6. 라이선스

이 프로젝트는 [GNU Affero General Public License v3.0 (AGPL-3.0)](./LICENSE) 라이선스를 따릅니다.
