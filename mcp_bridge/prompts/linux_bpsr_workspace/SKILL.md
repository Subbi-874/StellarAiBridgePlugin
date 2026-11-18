---
name: stellar-bridge
description: >-
  블루 프로토콜: 스타 레조넌스(Blue Protocol: Star Resonance) 인게임 캐릭터 제어·조회 통합 매뉴얼.
  명령권자(UID) 권한 규칙, 어드민락 보안 정책, 채널 이동·자동 낚시(Stellar.AutoFishing 연동)·자동 부활·자동 준비·던전/레이드/불안정공간/월드보스 입장·퇴장·자동 장판회피·이동형 필드기믹·파티 위임·프리셋·감정표현·내장 이모티콘 규칙,
  인게임 채팅 응답용 JSON actions 스키마(<current_trigger_message> + 명령권한=있음 제어 요청 시 열람) 및
  외부 직접 제어용 bridge_cli.py 사용법(<current_trigger_message> 미포함 직접 제어 시 사용)을 모두 담고 있다.
---

# Stellar AGY Bridge 통합 제어 매뉴얼 (`stellar-bridge`)

윈도우 게이밍 PC(`192.168.2.21:18765`, 환경변수 `STELLAR_BRIDGE_URL`로 변경 가능)에서 실행 중인 `Stellar.AgyBridge` 플러그인·데몬과 연동하여 인게임 캐릭터를 조회·제어하기 위한 단일 기준 문서(Single Source of Truth)입니다.

---

## 0. 보안 및 실행 대원칙 (필수 준수)

1. **입력 경로에 따른 도구 사용 구분 (엄격 분리)**:
   - **인게임 채팅 응답 (`<current_trigger_message>` 포함 입력)**:
     - 이 문서를 `read_file`로 열람해 아래 **3장(인게임 채팅 응답용 JSON `actions` 스키마)** 규격만 확인한 뒤, **절대 `run_command`나 `bridge_cli.py`를 직접 실행하지 말고** `{"reply_text": "...", "sticker": "...", "actions": [...]}` 순수 JSON 객체만 출력하세요(실행과 권한·어드민락 최종 검증은 윈도우 브릿지가 수행함).
   - **외부 직접 제어 (`<current_trigger_message>` 미포함 일반 지시)**:
     - 아래 **4장(`bridge_cli.py` 전용 CLI 사용법)** 에 명시된 `python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py ...` 단일 명령만 `run_command`로 실행하고, 결과를 한국어로 보고하세요.
2. **블루 프로토콜 브릿지 외 리눅스 시스템·파일 명령 절대 금지**:
   - 서버 로컬 파일 탐색/열람/수정(`ls`, `cat`, `rm`, `find`, `env`, `ps` 등) 및 임의 셸 파이프/체이닝(`;`, `&&`, `||`, `|`, `` ` ``, `$()`)은 어떤 경우에도 실행하지 마세요.

---

## 1. 명령권자(Commander) 권한 체계 및 어드민락(🔒) 정책

### 1-1. 명령권자 미지정 시 전원 허용 원칙 (매우 중요)
- 등록된 명령권자 목록(`authorized_commanders` / `authorized_commander_uids`)이 **비어 있거나 미지정(`[]`, `"(미지정 — 모든 유저 허용)"`)인 경우에는 `permission_mode` 값(`whitelist_only`, `party_or_whitelist` 등)과 무관하게 모든 유저에게 명령 권한이 허용(`명령권한=있음`)** 됩니다.
- 따라서 상태 조회 시 `authorized_commanders: []`로 보이더라도 "아무도 명령할 수 없는 상태"가 아니라 **"누구나 명령 가능한 전체 허용 상태"** 임을 정확히 인지하세요(단, 아래 1-3의 어드민락 `mode=2` 잠금 기능은 전원 허용 상태에서도 여전히 차단됨).

### 1-2. 명령권자가 1명 이상 등록되어 있을 때의 권한 판정
- 동일 닉네임 유저 간 혼동·사칭을 막기 위해 `닉네임#숫자UID`(예: `패랭이#15977376`) 형태의 **고유 숫자 UID(`char_id`)** 를 최우선으로 판별합니다.
- `permission_mode` 종류:
  - `whitelist_only`: 등록된 명령권자 UID(또는 레거시 지정 닉네임)만 캐릭터 제어 가능.
  - `party_or_whitelist` (기본값): 등록된 명령권자 UID **또는 현재 내 캐릭터와 같은 파티에 속한 파티원**이면 제어 가능.
  - `everyone`: 모든 유저 제어 가능.
- 비권한자(`명령권한=없음`) 처리:
  - `allow_non_commander_reply = true`일 때 비권한자의 일상 대화·질문·웹검색·이모티콘 응답은 페르소나와 말투대로 수행하되, **모든 캐릭터 조작 지시는 `actions: []`로 두고 명령권자가 아니라서 도울 수 없다고 거절**합니다(`false`일 때는 브릿지가 아예 응답하지 않음).

### 1-3. 어드민락(`mode=2`, 🔒) 및 기능별 정책(`feature_modes`)
- 모든 세부 기능과 채팅 발신/수신 채널은 세 가지 상태 중 하나를 가집니다:
  - `0`: 꺼짐(비활성)
  - `1`: 켜짐(활성)
  - `2`: **어드민락(🔒 AdminLock)** — 기본값으로 `send_world`(월드 채팅 발신)와 `send_say`(일반 채팅 발신)가 `2`로 잠겨 있음.
- **절대 우선 원칙**: 현재 `[어드민락 잠금 항목]`에 포함되었거나 브릿지 응답에 `"admin_locked": true`가 반환된 기능은, **정식 명령권자이거나 전원 허용 상태라 하더라도 대화나 원격 명령으로 절대 실행하거나 잠금을 해제할 수 없습니다.** 요청 시 `actions`에 넣지 말고(또는 재시도하지 말고) 인게임 스텔라 어드민락 보안 설정창에서만 직접 해제할 수 있다고 안내하세요.

### 1-4. 멀티 LLM 백엔드 · 페르소나(`<persona>`) · 시스템 프롬프트 전송 토글
- 윈도우 `Stellar.AgyBridge` 플러그인 설정창(`get_ui_chat_settings`)에서는 사용할 LLM 백엔드(`remote_bpsr` 리눅스 서버, `local_cli` [`agy`/`claude`/`codex`/`gemini`/`custom`], `openai_api` OpenAI 호환 엔드포인트, `gemini_api` Gemini REST API)와 모델·CLI 옵션을 선택할 수 있습니다.
- 채팅 트리거마다 UI에 입력된 페르소나가 `<persona>` 태그로 함께 전달되므로, 모든 답변(`reply_text`)은 `<persona>`의 성격·말투를 최우선으로 일관되게 따릅니다.
- **시스템 프롬프트 전송 토글 (`send_system_prompt`)**:
  - `OFF`일 때는 브릿지가 추가 제어문·스킬 요약·출력 JSON 스키마를 주입하지 않고 `<persona>` + 실시간 컨텍스트/채팅만 전달합니다(리눅스 `GEMINI.md`/`SKILL.md`나 자체 워크스페이스 규칙을 사용하는 경우 중복 방지용).
  - `ON`일 때는 외부 API/RAW CLI에서도 동일하게 캐릭터 제어가 가능하도록 페르소나 + 제어 규칙(`gemini_control_rules`) + 액션 명세(`skill_chat_reference`) + JSON 스키마를 함께 전송합니다.

---

## 2. 플러그인 자율 동작 및 게임 도메인 규칙

1. **사망 시 무료 자동 부활 (`auto_revive`)**:
   - `auto_revive`가 켜져 있을 때(설정 기본값 OFF) 캐릭터가 사망하면 유료 재화를 쓰지 않는 무료 부활 옵션만 골라 **`근처 부활 → 부활 지점 → 메인 도시`** 우선순위로 자동 부활합니다(`revive` 수동 명령도 동일 규칙 적용).
2. **던전 · 레이드 · 불안정 공간 · 월드보스 자동 준비 (`auto_ready`)**:
   - `auto_ready`가 켜져 있을 때(설정 기본값 OFF) 아래 3종류의 진입/전투 준비 확인창이 뜨면 백그라운드에서 자동으로 준비 완료를 누릅니다:
     1. 파티 직접 입장 확인 투표창 (`team_enter`)
     2. 월드 보스 / 매칭 큐 성사 준비 확인창 (`match_ready`)
     3. 던전·레이드 내부 파티장 전투 준비 카운트다운 (`dungeon_prepare`)
   - 유저가 `"준비 눌러 / 레디해"`라고 직접 지시하면 수동 확인(`ready_check` / `ready`)도 실행할 수 있습니다(어드민락=2일 때만 차단).
3. **파티장 전용 기능 및 파티장 위임 자동 수락 (`party_manage` / `dungeon_enter`)**:
   - **파티장만 실행 가능한 작업**:
     - 파티장 위임(`transfer_leader`), 파티원 추방(`kick`), 다른 파티와 합병 신청(`merge`), 파티 인원 5인/20인 전환(`set_member_type`)
     - 파티에 속한 상태에서 던전·레이드·불안정 공간·월드보스 입장 시작(`enter_dungeon`) *(솔로 상태일 때는 1인 입장 허용 콘텐츠나 월드보스 매칭, 싱글 AI 모드 입장 가능)*
   - 내 캐릭터가 파티장이 아닐 때 위 명령을 실행하면 브릿지가 사전 검사에서 걸러 실패 사유(`not_leader`)를 반환합니다.
   - **파티장 양도 자동 수락**: 명령 권한이 있는 유저(전원 허용 상태 포함)가 인게임에서 내 캐릭터에게 파티장 위임(`TeamTransfer`)을 걸면 별도 사전 명령 없이 자동으로 수락합니다.
4. **던전 · 마스터 · 불안정 공간 · 레이드 · 월드보스 세부 매칭 동작**:
   - **일반 / 하드 / 마스터 1~20단**: 인게임 `DungeonsTableMgr` 및 `MasterChallengeDungeonTableMgr`를 실시간 조회하여 매칭하며, 해금되지 않았거나 최대 단수를 초과한 마스터 단수는 사유와 함께 거절합니다.
   - **불안정 공간 (`PlayType=8`)**: 같은 던전이라도 파티 인원에 따라 테이블 ID가 나뉘므로 현재 인원수(`1~2인` vs `3~5인`)와 당일 개방 조건을 자동 판별해 입장합니다.
   - **20인 레이드 자동 공격대 전환**: `드래곤 프리즌`, `환상꽃` 등 최대 인원이 5명을 초과하는 레이드(`회귀`/`혈전`/`결전`, `이지`/`하드`/`나이트메어`) 입장 시 현재 5인 파티면 자동으로 **20인 공격대(`Raid20`)로 전환**한 뒤 입장합니다.
   - **싱글 AI 서포트**: 솔로 상태에서 싱글/AI 동료 입장을 지정(`single_ai: true` / `--single-ai`)하면 AI 서포트 모드로 진입합니다.
5. **따라가기 실시간 추적 · 전투 중 추적 최우선 · 단독 이동 시 자동 해제 (`follow_move`)**:
   - 따라가기 시작 전 대상의 접속 여부와 맵 종류를 사전 검사하여, 오프라인이거나 진입 불가 인스턴스(던전·레이드·타인 하우징 등)에 있으면 즉시 거절 안내를 보냅니다. 이동 중 실패하거나 몬스터 추적에 도착/실패했을 때도 요청했던 채팅 채널로 자동 안내합니다.
   - **실시간 `levelPos` 추적 & 무한 워프 방지**: 원거리 파티 추적 시 정적 스폰 좌표가 아닌 실시간 보행 좌표(`levelPos`)를 우선 갱신하며, 20초 이내 최근 사용한 맵 내 포탈·전송 장치는 재진입 후보에서 제외해 두 지점 왕복 루프를 방지합니다.
   - **전투 중에도 원거리·타채널 추적 최우선**: 따라갈 대상이 `15m` 이상 멀어졌거나 다른 씬/다른 채널에 있으면 전투 중이더라도 자동전투를 즉시 일시 중지(`SuppressForCatchup`)해 합류·채널 전환을 최우선으로 수행합니다(실전투 여부는 `GetLuaIsInCombat`으로 판별하며 채널 변경 전 교전 해제 대기는 최대 `6초` 상한).
   - **스태미나 절약 · 익사 방지 이동**: 스태미나가 `50%` 이하(`<= 50%`)로 떨어지면 대시·점프·이동 스킬 입력을 자동 중단해 일반 이동만으로 회복을 기다리며, 수영 중에도 대시·점프 연타를 억제해 익사를 방지합니다.
   - **벽/모서리 우회 커밋(`detour`)**: 장애물/모서리 회피 중에는 코너를 완전히 빠져나올 때까지 각도 유지를 우선하고 벽 쪽 점프·대시를 억제하며, 막힌 포켓에서는 반대편/최대 개방 방향으로 탈출합니다.
   - **전투·워프 후 시점 자동 복구 (`enable_auto_camera_guard` / `reset_camera`)**: 자동전투 또는 추적 중 궁극기 연출 종료·워프·채널 변경·로딩 직후 카메라가 기본값으로 리셋되면 자동으로 최대 줌아웃 및 상단 부감 각도로 복구하며, 유저 마우스 조작 시에는 일시 양보합니다(`bridge_cli.py call reset_camera "{}"`로 수동 호출 가능).
   - **이동 중 자동 기믹 연계**: `follow_player`로 대상을 추적하다가 수평 `14m+` 또는 높이차 `6m+` 이상 벌어진 지형에서 주변에 바람의 통로·활공 발판·맵 내 전송 장치 등 이동 기믹이 있으면 자동으로 접근·작동해 따라갑니다.
   - **중요**: 포탈/장소 이동(`teleport_to_place`), 던전·특수 공간 나가기(`leave_dungeon`), 보스/몬스터 위치 이동(`seek_monster`), 채널 이동(`switch_channel`), 이동 기믹 탑승(`use_gimmick`), 자동 낚시(`fish`), 생활 채집(`gather_resource`)을 실행하면 브릿지가 **진행 중이던 `follow_player`를 자동으로 중지**하고 해당 동작을 수행합니다. 따라서 이런 단독 이동 지시에는 `follow_player`를 절대 함께 넣지 마세요.
6. **유저 위치 조회 2단계 응답 (`player_locate`)**:
   - 인게임 채팅에서 `query_player_location` 액션을 반환하면 브릿지가 맵 이름·종류·채널·좌표·거리·입장 가능 여부를 조회해 `<action_results>` 태그로 한 번 더 전달하므로, 1차 `reply_text`는 페르소나 말투에 맞춰 확인 중이라는 짧은 한마디만 쓰고 2차에서 실제 조회 사실만으로 답변합니다.
7. **직업/스킬 프리셋 · 의상 프리셋 · 몬스터 추적 · 감정표현 · 내장 스티커**:
   - **직업/스킬 프리셋 (`loadout_switch`)**: 1번부터 시작하는 번호, 프리셋 이름, 또는 직업명으로 전환.
   - **의상 프리셋 (`outfit_switch`)**: `WardrobeLoadout` 파일을 읽기 전용으로 참조하며, 번호/이름 미지정 시 보유 프리셋 중 **랜덤**으로 갈아입음.
   - **몬스터 추적 (`seek_monster`)**: 몬스터 이름으로 가장 가까운 출현 좌표를 계산해 다른 맵이면 워프 후 이동(`engage: true` 시 도착 후 자동전투 활성화).
   - **감정표현 (`emote`) vs 채팅 내장 이모티콘 (`sticker`)**:
     - 감정표현(`play_emote`)은 3D 캐릭터 모션(춤, 인사, 앉기 등)이며 `[보유 감정표현 목록]`에 있는 해금 항목만 사용 가능(미보유 시 스킵·안내).
     - 내장 이모티콘(`sticker` / `send-sticker`)은 채팅창에 전송하는 2D 그림 스티커(`Type=3,4`)로, 계정 보유 목록에서 감정 태그에 맞는 스티커를 골라 보내며 발신 채널 어드민락(`send_world=2`, `send_say=2`)과 채팅 쿨타임을 동일하게 적용받음.
8. **채널 이동 (`switch_channel` / `teleport_channel`) 및 자동 낚시 (`fish` / `auto_fish`)**:
   - **채널 이동 (`switch_channel`)**: 현재 맵에서 지정한 채널 번호(`line_id`) 또는 특정 유저가 있는 채널(`target` UID/닉네임)로 이동합니다. 따라가기 중이었다면 따라가기를 먼저 멈춘 뒤 채널만 전환합니다. 이미 같은 채널이면 불필요한 이동 없이 알립니다.
   - **자동 낚시 (`fish`)**: `Stellar.AutoFishing` 플러그인과 연동해 현재 위치 근처(`spot: ""`), 지역/해변/호수/워프명(`"아스텔리즈 해변"`, `"민다로 호수"`, `"평원 바다"` 등), 또는 물고기 이름(`"원조산갈치"` 등)으로 가장 알맞은 낚시 포인트를 찾아 포탈·주행 이동한 뒤 낚싯대와 미끼를 자동 장착하고 낚시를 시작합니다. `count > 0`이면 목표 마릿수 달성 시 낚시를 종료·해제하고 보고하며, `count: 0`(미지정)이면 중지(`stop`)나 다른 이동 명령 또는 미끼/낚싯대 소진 전까지 계속 낚시합니다.
9. **던전/특수 공간 퇴장 (`leave_dungeon`) · 저스트 회피 우선 모드 · 이동형 필드 기믹 (`use_gimmick`)**:
   - **던전·특수 공간 나가기 (`leave_dungeon`)**: 일반/하드/마스터 던전, 불안정 공간, 20인 레이드, 시디문 유적, 월드 보스, 가상 레이어, 길드 홈, 마이홈에서 즉시 밖으로 퇴장합니다.
   - **저스트 회피 우선 & 보스 붉은 섬광 회피 (`auto_evade`)**:
     - 일반 몬스터 평타 및 일반 투사체(`BulletStage`)에는 불필요하게 반응하지 않으며, 장판을 걸어서 빠져나가기 위해 이동키를 남발하지 않습니다.
     - 위험 바닥 장판 위에서는 폭발 직전(`~74%`)까지 공격을 유지하다가, 폭발 임박 시점(`~82%` 이상 또는 잔여 `0.19초` 이하)에 안전 방향으로 회피키 대시(`Rush`)를 짧게 눌러 **저스트 회피**를 수행합니다.
     - 보스 눈이 붉게 빛나는 강타 전조(**붉은 섬광**, `ZUISkillWarning` 및 붉은 섬광 이펙트) 감지 시 강타 타이밍에 맞춰 회피키(`Rush`)로 회피합니다.
     - 아군 회복/버프용 녹색 장판과 파티 **분담/집합 장판(`"분담"`, `"[집합]"`, 아군 화살표 추적)** 은 회피 대상에서 자동 제외해 뭉침을 유지합니다.
   - **이동형 필드/던전 기믹 (`use_gimmick` / `list_gimmicks`)**: 바람의 통로·기류·활공 발판·맵 내 순간 이동 장치 등 이동 기믹을 찾아 접근·작동합니다(던전 출구, 상자, 상점, 의자 앉기, 낚시터는 자동 제외).

---

## 3. [인게임 채팅 응답용] JSON `actions` 스키마 명세
*(입력에 `<current_trigger_message>`가 있고 `명령권한=있음`일 때만 아래 객체를 `actions` 배열에 추가 — `run_command` 호출 금지!)*

대상 유저 지정 시 동일닉 방지를 위해 `[온라인 파티/친구/길드원 UID 목록]` 또는 발신자의 **숫자 UID 문자열**(예: `"15977376"`)을 최우선으로 넣고, 목록에 없을 때만 닉네임을 넣으세요. `[어드민락 잠금 항목]`에 해당하는 액션은 절대 넣지 마세요.

1. **유저 따라가기 (`follow_player`)**:
   - `{"type": "follow_player", "target": "<발신자 또는 지정 대상 숫자 UID 우선>", "continuous": true}` (1회 접근 후 정지는 `"continuous": false`)
2. **자동전투 켜기/끄기 (`set_auto_combat`)**:
   - `{"type": "set_auto_combat", "enable": true}` 또는 `false`
3. **이동/추적/채집/낚시 즉시 정지 (`stop`)**:
   - `{"type": "stop"}`
4. **장소/워프 포탈 이동 (`teleport_to_place`)** *(기존 따라가기 자동 중지)*:
   - `{"type": "teleport_to_place", "destination": "길드 홈 | 마이홈 | 아스텔리즈 | 워프명 등"}`
   - 채널 번호 변경(`"3채널로 가"`)이나 유저 채널 합류는 `teleport_to_place`가 아니라 아래 **19번 `switch_channel`**을 사용하세요.
5. **대화 모니터링 종료 (`disable_chat_monitor`)**:
   - `{"type": "disable_chat_monitor"}` ('대화 모니터링 꺼 / 조용히 해')
6. **생활 자원 자동 채집 (`gather_resource`)**:
   - `{"type": "gather_resource", "resource": "사향 분말", "count": 20}` (집중력/활력 사용 여부 언급 시 `"use_focus": true/false` 추가)
7. **파티 관리 (`party_action`)**:
   - 생성: `{"type": "party_action", "party_op": "create", "auto_accept": true, "leave_first": true}`
   - 자동 수락 토글(파티장만): `{"type": "party_action", "party_op": "set_auto_accept", "auto_accept": true}` (또는 `false`)
   - 가입 신청: `{"type": "party_action", "party_op": "join", "target": "<숫자 UID 우선>", "leave_first": true}`
   - 탈퇴: `{"type": "party_action", "party_op": "leave"}`
   - 초대: `{"type": "party_action", "party_op": "invite", "target": "<숫자 UID 우선>"}`
   - 가입 신청 일괄 수락: `{"type": "party_action", "party_op": "accept_apply"}`
   - **파티장 위임(파티장만)**: `{"type": "party_action", "party_op": "transfer_leader", "target": "<발신자 또는 지정 파티원 UID>"}`
   - 파티원 추방(파티장만): `{"type": "party_action", "party_op": "kick", "target": "<파티원 UID>"}`
   - 파티 합병 신청(파티장만): `{"type": "party_action", "party_op": "merge", "target": "<상대 파티 유저 UID>"}`
   - 5인/20인 공격대 전환(파티장만): `{"type": "party_action", "party_op": "set_member_type", "size": 5}` 또는 `20`
8. **3D 감정표현 실행 / 중지 (`play_emote` / `stop_emote`)**:
   - 실행: `{"type": "play_emote", "emote": "<보유 감정표현 목록의 정확한 이름>"}` (보유 목록에 없으면 `actions`에 넣지 말고 미보유 안내)
   - 중지('일어나 / 춤 그만 / 박수 그만'): `{"type": "stop_emote"}`
9. **부활 (`revive`)**:
   - `{"type": "revive"}`
10. **탈것 탑승/하차 (`ride`)**:
    - `{"type": "ride", "ride_op": "mount" | "dismount" | "toggle"}` (수상 탈것 명시 시 `"ride_type": "water"` 추가)
11. **2인 상호작용 · 동승 · 파티 초대 승인 및 발신**:
    - 상대가 보낼 요청 60초간 승인 대기: `{"type": "accept_interaction", "target": "<발신자 UID>", "window_sec": 60}` (특정 종류만 한정 시 `"kinds": ["interaction" | "ride" | "party" | "leader"]`)
    - 내가 먼저 동승 요청 보내기: `{"type": "request_ride", "target": "<UID>", "ride_mode": "apply"}` (내 탈것에 태우기는 `"ride_mode": "invite"`)
    - 내가 먼저 2인 상호작용 신청 보내기: `{"type": "request_interaction", "target": "<UID>", "action": "손잡기"}`
12. **유저 위치 조회 (`query_player_location`)**:
    - `{"type": "query_player_location", "target": "<UID 우선>"}` (1차 `reply_text`는 페르소나 말투에 맞춰 확인 중이라고만 짧게 작성)
13. **직업/스킬 프리셋 변경 (`switch_loadout`)**:
    - `{"type": "switch_loadout", "number": 2}` (또는 `"name": "프리셋명"`, `"profession": "스톰 블레이드"`)
14. **의상 프리셋 변경 (`switch_outfit`)**:
    - 번호/이름 지정 시 `{"type": "switch_outfit", "number": 3}` (또는 `"name"`), 미지정 랜덤 변경 시 `{"type": "switch_outfit"}`
15. **몬스터 찾아가기 (`seek_monster`)**:
    - `{"type": "seek_monster", "monster": "몬스터 이름", "engage": false}` ('잡아/사냥' 언급 시 `"engage": true`)
16. **던전 · 불안정 공간 · 레이드 · 월드보스 입장 시작 (`enter_dungeon`)**:
    - 일반/하드/마스터: `{"type": "enter_dungeon", "dungeon": "티나의 정신 세계", "difficulty": "하드"}` / `{"type": "enter_dungeon", "dungeon": "거탑 유적", "difficulty": "마스터 5"}`
    - 불안정 공간: `{"type": "enter_dungeon", "dungeon": "불안정 공간"}` 또는 `{"type": "enter_dungeon", "dungeon": "불안정 공간 쇠락의 심연"}`
    - 레이드: `{"type": "enter_dungeon", "dungeon": "드래곤 프리즌", "difficulty": "혈전"}` (`회귀`/`혈전`/`결전`, `이지`/`하드`/`나이트메어`)
    - 월드 보스 / 시디문 유적: `{"type": "enter_dungeon", "dungeon": "월드 보스"}` / `{"type": "enter_dungeon", "dungeon": "시디문 유적 50층"}`
    - 싱글 AI 서포트 요청 시 `"single_ai": true` 추가.
17. **진입 준비 확인 버튼 수동 누르기 (`ready_check`)**:
    - `{"type": "ready_check"}`
18. **내장 그림 이모티콘(스티커) 전송**:
    - 별도 액션 객체 대신 최상위 JSON의 `"sticker"` 필드에 `"greet"`, `"thanks"`, `"thumbsup"`, `"cheer"`, `"heart"`, `"laugh"`, `"cute"`, `"ok"`, `"think"`, `"sorry"`, `"sad"`, `"random"` 중 하나를 기입합니다.
19. **채널(라인) 이동 (`switch_channel`)** *(기존 따라가기 자동 중지)*:
    - 특정 채널 번호로 이동(`"3채널로 가 / 5채널 옮겨"`): `{"type": "switch_channel", "line_id": 3}`
    - 특정 유저가 있는 채널로 이동(`"쵸로기 채널로 와"` — 따라가지 않고 채널만 맞춤): `{"type": "switch_channel", "target": "<숫자 UID 우선>"}`
20. **자동 낚시 (`fish`)** *(Stellar.AutoFishing 연동 · 기존 따라가기 자동 중지)*:
    - 특정 해변/호수/지역/어종 지정(`"아스텔리즈 해변 가서 낚시 10마리 해"`, `"원조산갈치 낚아와"`): `{"type": "fish", "spot": "아스텔리즈 해변", "count": 10}`
    - 현재 맵 근처 낚시 또는 마릿수 미지정(`"여기서 낚시해"`, `"바닷가에서 낚시해"`): `{"type": "fish", "spot": "" | "바닷가", "count": 0}` (`count: 0`이면 멈추라고 할 때까지 지속)
21. **던전·특수 공간 나가기 (`leave_dungeon`)** *(기존 따라가기·낚시 자동 중지)*:
    - `{"type": "leave_dungeon"}` (일반/하드/마스터 던전, 불안정 공간, 20인 레이드, 시디문 유적, 월드 보스, 가상 레이어, 길드 홈, 마이홈에서 즉시 밖으로 퇴장)
22. **이동형 필드·던전 기믹 사용 (`use_gimmick`)** *(기존 따라가기·낚시 자동 중지)*:
    - `{"type": "use_gimmick", "gimmick": "바람의 통로"}` (기믹명 미지정 시 `""`로 두면 주변에서 가장 가까운 바람의 통로·활공 발판·맵 내 순간 이동 장치 작동)

---

## 4. [외부 직접 제어용] 전용 CLI 사용법 (`bridge_cli.py`)
*(입력에 `<current_trigger_message>`가 없는 일반 직접 제어 요청일 때만 `run_command`로 실행)*

```bash
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py [--client <conn_id>] <명령> [옵션]
```

### A. 상태 · 멀티클라이언트 · 인원(UID) · 명령권자/정책 조회
```bash
# 내 캐릭터 상태, 맵/채널, 파티 요약, feature_modes(어드민락 포함) 조회
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py status

# 연결된 멀티 클라이언트 목록 조회, 제어 대상 전환, 또는 특정 클라이언트 1회성 지정 조회 (--client)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py clients
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py select 패랭이
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py status --client client-2

# 통합 인원(파티·친구·길드) 고유 숫자 UID 조회 (동일닉 방지용 필수 확인)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py roster --online-only
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py roster --query 쵸로기

# 현재 파티원 목록 / 주변 엔티티 조회
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py entities --limit 40

# 대화 모니터링 상태 · 명령권자(비어 있으면 전원 허용) · 어드민락 · LLM 백엔드/페르소나/시스템 프롬프트 설정 조회
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py call get_chat_monitor_status "{}"
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py chat-settings
```

### B. 이동 · 유저 추적 · 포탈 · 채널 전환 · 몬스터 추적
```bash
# 유저 지속 따라가기 / 1회 접근 후 정지 / 모든 행동 즉시 정지
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py follow 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py follow 15977376 --once
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py stop

# 현재 맵 좌표 이동 / 장소·워프 목록 검색 및 순간이동 / 채널 목록 조회·이동
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py move 120.5 32.0 -85.2
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py places --query 아스텔
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py teleport "길드 홈"
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py channels
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py channel 3
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py channel --target 15977376

# 특정 유저 위치 상세 조회 / 몬스터 출현 위치 찾아가기 (--engage 시 도착 후 자동전투)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py locate 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py seek-monster "고블린 전사" --engage

# 주변 이동형 필드·던전 기믹(바람 통로·활공·전송) 조회 및 접근·작동
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py gimmicks
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py use-gimmick "바람의 통로"
```

### C. 파티 제어 · 던전/레이드/불안정공간/월드보스 입장 · 준비 확인
```bash
# 파티 생성 / 가입 / 초대 / 탈퇴 / 가입신청 수락
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op create --auto-accept
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op join --target 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op invite --target 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op leave
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op accept_apply

# 파티장 전용 제어: 파티장 위임 / 추방 / 합병 신청 / 5인·20인 공격대 전환
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op transfer_leader --target 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op kick --target 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op merge --target 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py party-op set_member_type --size 20

# 던전·불안정 공간·레이드·월드보스 목록 검색 및 입장 시작 (파티 시 파티장 전용)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py dungeons --query 티나
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py dungeons --category unstable
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py enter-dungeon "티나의 정신 세계" --difficulty 하드
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py enter-dungeon "거탑 유적" --difficulty "마스터 5"
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py enter-dungeon "불안정 공간"
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py enter-dungeon "드래곤 프리즌" --difficulty 혈전
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py enter-dungeon "월드 보스"

# 현재 던전·마스터·불안정 공간·레이드·월드보스·길드홈·마이홈에서 즉시 퇴장
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py leave-dungeon

# 준비 확인 상태 조회 및 수동 준비 완료 누르기 (평소에는 자동 수락됨)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py call get_ready_status "{}"
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py ready
```

### D. 자동전투 · 채집 · 부활 · 탈것 · 상호작용 · 프리셋 · 감정표현 · 채팅/스티커
```bash
# 자동전투 / 생활 채집
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py combat on
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py combat off
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py gatherables --query 사향
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py gather "사향 분말" --count 20

# 낚시터·어종 조회 및 자동 낚시 (Stellar.AutoFishing 연동)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py fish-spots --query 해변
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py fish "아스텔리즈 해변" --count 10
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py fish

# 부활 / 탈것 / 2인 상호작용·동승 승인 및 발신
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py revive
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py ride mount
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py ride dismount
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py accept-interaction 15977376 --window 60
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py request-ride 15977376
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py request-interaction 15977376 --action 손잡기

# 직업/스킬 프리셋 & 의상 프리셋 (outfit 인자 생략 시 랜덤 변경)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py loadouts
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py loadout --number 2
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py outfits
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py outfit --number 3
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py outfit

# 3D 감정표현 조회·실행·중지
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py emotes
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py emote "손 흔들기"
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py emote stop

# 채팅 조회 · 텍스트 발신 · 게임 내장 이모티콘(스티커) 조회 및 발신 (world/say 기본 어드민락 잠금)
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py chats --channel guild --limit 30
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py send-chat party "준비 완료"
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py stickers
python3 /home/subbi/bpsr/.agents/skills/stellar-bridge/scripts/bridge_cli.py send-sticker party greet
```
