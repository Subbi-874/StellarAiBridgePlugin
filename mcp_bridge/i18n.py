"""Internationalization (KO / EN / JA) catalogs and helpers for the Python bridge (stdlib-only, C#-portable)."""

from typing import Any


SUPPORTED_LANGUAGES: tuple[str, ...] = ("ko", "en", "ja")

DEFAULT_PERSONAS: dict[str, str] = {
    "ko": "당신은 블루 프로토콜 스타 레조넌스를 플레이 하는 유저입니다.",
    "en": "You are a player playing Blue Protocol: Star Resonance.",
    "ja": "あなたはブループロトコル：スターレゾナンスをプレイしているユーザーです。",
}


def normalize_ui_language(raw: str | None) -> str:
    """Normalizes UI/client language codes into `'ko'`, `'ja'`, or `'en'` (blank defaults to `'ko'`)."""
    s = (raw or "").strip().lower().replace("_", "-")
    if not s:
        return "ko"
    base = s.split("-", 1)[0]
    if base == "ko":
        return "ko"
    if base in ("ja", "jp"):
        return "ja"
    return "en"


def default_persona_for_lang(lang: str | None) -> str:
    """Returns the localized simple default persona string for the given language."""
    return DEFAULT_PERSONAS.get(normalize_ui_language(lang), DEFAULT_PERSONAS["ko"])


POLICY_LABELS_BY_LANG: dict[str, dict[str, str]] = {
    "ko": {
        "send_world": "월드(전체) 채팅 발신",
        "send_say": "일반(주변) 채팅 발신",
        "send_guild": "길드 채팅 발신",
        "send_party": "파티 채팅 발신",
        "send_whisper": "귓속말 발신",
        "chat_monitor": "대화 모니터링 전체",
        "recv_guild": "길드 채팅 감지",
        "recv_party": "파티 채팅 감지",
        "recv_whisper": "귓속말 감지",
        "recv_say": "일반 채팅 감지",
        "follow_move": "대상 추적·이동",
        "teleport_channel": "맵 포탈·채널 이동",
        "auto_combat": "자동전투",
        "boost_move": "3단 가속 이동",
        "closest_portal": "최단거리 포탈 이동",
        "emote": "감정표현",
        "party_manage": "파티 생성·가입·탈퇴",
        "auto_gather": "자동 채집",
        "lua_eval": "외부 Lua 스크립트",
        "auto_revive": "사망 시 자동 부활 (무료)",
        "ride": "탈것 탑승·하차",
        "interaction_accept": "상호작용·동승·파티초대 요청 (승인/발신)",
        "player_locate": "유저 위치 조회",
        "loadout_switch": "직업·스킬 프리셋 변경",
        "outfit_switch": "의상 프리셋 변경",
        "auto_ready": "던전·레이드·월드보스 자동 준비",
        "dungeon_enter": "던전·레이드·불안정공간 입장 시작",
        "auto_fish": "자동 낚시 제어",
        "auto_craft": "아이템·요리·가구·장비 제작",
    },
    "en": {
        "send_world": "World chat send",
        "send_say": "Local (Nearby) chat send",
        "send_guild": "Guild chat send",
        "send_party": "Party chat send",
        "send_whisper": "Whisper send",
        "chat_monitor": "Chat monitoring",
        "recv_guild": "Guild chat detection",
        "recv_party": "Party chat detection",
        "recv_whisper": "Whisper detection",
        "recv_say": "Local chat detection",
        "follow_move": "Follow & move to target",
        "teleport_channel": "Map portal & channel switch",
        "auto_combat": "Auto combat",
        "boost_move": "Triple-dash movement",
        "closest_portal": "Closest portal teleport",
        "emote": "Emote",
        "party_manage": "Party create, join & leave",
        "auto_gather": "Auto gather",
        "lua_eval": "External Lua script",
        "auto_revive": "Auto revive on death (free)",
        "ride": "Mount ride & dismount",
        "interaction_accept": "Interaction, co-ride & party invite requests",
        "player_locate": "Query player location",
        "loadout_switch": "Class & skill loadout switch",
        "outfit_switch": "Outfit preset switch",
        "auto_ready": "Dungeon, raid & world boss auto-ready",
        "dungeon_enter": "Dungeon, raid & unstable space enter",
        "auto_fish": "Auto fishing control",
        "auto_craft": "Item, cooking, housing & gear crafting",
    },
    "ja": {
        "send_world": "ワールドチャット送信",
        "send_say": "周囲(一般)チャット送信",
        "send_guild": "ギルドチャット送信",
        "send_party": "パーティチャット送信",
        "send_whisper": "ささやき送信",
        "chat_monitor": "チャット監視全体",
        "recv_guild": "ギルドチャット検知",
        "recv_party": "パーティチャット検知",
        "recv_whisper": "ささやき検知",
        "recv_say": "周囲チャット検知",
        "follow_move": "対象追尾・移動",
        "teleport_channel": "マップ移動・チャンネル変更",
        "auto_combat": "自動戦闘",
        "boost_move": "3段ダッシュ移動",
        "closest_portal": "最寄りポータル移動",
        "emote": "エモート",
        "party_manage": "パーティ作成・加入・脱退",
        "auto_gather": "自動採集",
        "lua_eval": "外部Luaスクリプト",
        "auto_revive": "死亡時自動蘇生 (無料)",
        "ride": "マウント騎乗・降車",
        "interaction_accept": "インタラクト・二人乗り・パーティ招待要請",
        "player_locate": "プレイヤー位置照会",
        "loadout_switch": "クラス・スキルプリセット変更",
        "outfit_switch": "衣装プリセット変更",
        "auto_ready": "ダンジョン・レイド・ワールドボス自動準備",
        "dungeon_enter": "ダンジョン・レイド・不安定空間入場",
        "auto_fish": "自動釣り制御",
        "auto_craft": "アイテム・料理・ハウジング・装備製作",
    },
}


FOLLOW_CANCEL_MESSAGES_BY_LANG: dict[str, dict[str, str]] = {
    "ko": {
        "cancelled_unenterable_scene": "{name}님이 있는 '{scene}' 지역은 입장 불가 구역이어서 따라가기 중단됨",
        "cancelled_target_offline": "{name}님이 오프라인 상태여서 따라가기 중단됨",
        "cancelled_target_not_found": "대상 '{query}'을(를) 찾지 못해 따라가기 실패함",
        "cancelled_locked_channel": "{name}님 채널이 잠겨 있어 채널 이동 불가로 따라가기 중단됨",
        "cancelled_teleport_unavailable": "{name}님 위치로 순간이동할 수 없어 따라가기 중단됨",
        "cancelled_unreachable_scene": "{name}님이 있는 맵으로 이동할 수 없어 따라가기 중단됨",
        "cancelled_different_scene": "맵 이동 기능이 꺼져 있어 다른 맵에 있는 {name}님에게 이동 불가함",
        "cancelled_transfer_rejected": "{name}님 위치로의 이동이 서버에서 거부되어 따라가기 중단됨",
        "cancelled_admin_lock": "따라가기 기능이 어드민락 잠금 상태여서 중단됨",
    },
    "en": {
        "cancelled_unenterable_scene": "Stopped following because '{scene}' where {name} is located cannot be entered.",
        "cancelled_target_offline": "Stopped following because {name} is offline.",
        "cancelled_target_not_found": "Follow failed because target '{query}' could not be found.",
        "cancelled_locked_channel": "Stopped following because {name}'s channel is locked and cannot be switched to.",
        "cancelled_teleport_unavailable": "Stopped following because teleporting to {name}'s location is unavailable.",
        "cancelled_unreachable_scene": "Stopped following because the map where {name} is located is unreachable.",
        "cancelled_different_scene": "Cannot move to {name} on another map because cross-map movement is disabled.",
        "cancelled_transfer_rejected": "Stopped following because the server rejected moving to {name}'s location.",
        "cancelled_admin_lock": "Stopped following because Follow Move is locked by Admin Lock.",
    },
    "ja": {
        "cancelled_unenterable_scene": "{name}さんがいる「{scene}」エリアは入場不可区域のため追尾を中断しました。",
        "cancelled_target_offline": "{name}さんがオフラインのため追尾を中断しました。",
        "cancelled_target_not_found": "対象「{query}」が見つからず追尾に失敗しました。",
        "cancelled_locked_channel": "{name}さんのチャンネルがロックされており移動できないため追尾を中断しました。",
        "cancelled_teleport_unavailable": "{name}さんの場所へテレポートできないため追尾を中断しました。",
        "cancelled_unreachable_scene": "{name}さんがいるマップへ移動できないため追尾を中断しました。",
        "cancelled_different_scene": "マップ移動機能がオフのため、別マップにいる{name}さんの元へ移動できません。",
        "cancelled_transfer_rejected": "{name}さんの場所への移動がサーバーに拒否されたため追尾を中断しました。",
        "cancelled_admin_lock": "追尾機能が管理者ロックされているため中断しました。",
    },
}


MESSAGES: dict[str, dict[str, str]] = {
    "ko": {
        "admin_lock_error": "'{label}' 기능은 어드민락(🔒) 상태라 대화로 실행하거나 변경할 수 없어요.",
        "no_recent_chat": "(최근 이전 대화 없음)",
        "no_active_goals": "(활성 목표 없음)",
        "no_memories": "(저장된 기억 없음)",
        "none_item": "(없음)",
        "not_queried": "(조회 없음)",
        "reply_language_rule": (
            "언어 규칙: 기본 응답 언어는 한국어입니다. "
            "단, 설정된 페르소나 지침에 특정 언어가 지정되었거나 상대방이 다른 언어로 말할 경우 "
            "그 언어에 맞춰 마크다운 기호 없는 깔끔한 평문으로 답변하세요."
        ),
        "tool_calling_addendum": (
            "[Function Calling 도구 사용 규칙]\n"
            "- 현재 연결은 표준 Function Calling(tools) 모드입니다.\n"
            "- 상태·위치·레시피·기억 조회나 인게임 조작/목표/기억 변경이 필요하면 JSON의 actions 배열을 쓰는 대신 제공된 함수(tools)를 직접 호출하세요.\n"
            "- 도구 호출 결과를 확인한 뒤, 설정된 페르소나와 말투 및 발신자·클라이언트 언어에 맞춰 사용자에게 보낼 자연스러운 평문 답변을 작성하세요.\n"
            "- 이미 도구로 실행 완료한 행동을 최종 JSON의 actions 배열에 중복 기재하지 마세요(단순 평문으로 답변해도 됩니다)."
        ),
        "task_follow_default": "따라가기 중단됨 (사유: {phase})",
        "target_default": "대상",
        "area_default": "해당 구역",
        "task_fish_spot_default": "낚시터",
        "task_fish_done": "{spot}에서 낚시 {caught}마리 완료",
        "task_fish_stopped": "{spot} 낚시 종료 (총 {caught}마리)",
        "task_gimmick_failed": "이동 기믹 사용 실패",
        "task_craft_done": "제작 완료",
        "task_craft_failed": "제작 실패",
        "task_farm_done": "몬스터 드랍 재료 확보 완료",
        "task_farm_failed": "몬스터 재료 파밍 실패",
        "task_monster_arrived": "목표 몬스터 출현 위치 도착 완료",
        "task_monster_failed": "목표 몬스터 위치 이동 실패",
        "craft_not_found": "'{label}' 제작 레시피를 찾지 못했어요.",
        "craft_already_owned": "'{name}'은(는) 이미 {owned}개 보유 중이에요.",
        "craft_recipe_locked": "제작 레시피가 아직 해금되지 않았어요",
        "craft_missing_base_equip": "장비 단조에 소모할 미착용 하위 장비가 부족해요",
        "craft_fish_grade_missing": "해당 등급 물고기 정보를 찾지 못했어요",
        "craft_sub_recipe_locked": "하위 제작 레시피가 미해금 상태예요",
        "craft_external_source_hint": "거래소·상점·직접 획득 필요",
        "craft_focus_name": "집중력",
        "craft_focus_shortage": "집중력이 부족해요 (필요 {needed}, 보유 {owned})",
        "craft_blocked": "'{item_name}' {deficit}개 부족으로 자동 제작이 막혔어요 ({hints}).",
        "craft_monster_item_fmt": "{item_name} {deficit}개({monster_name})",
        "craft_needs_monster_confirm": "부족한 몬스터 드랍 재료가 있어요: {desc}. 몬스터 사냥을 시작할까요?",
        "craft_ready": "'{name}' x{crafts} 제작 계획 {steps}단계 준비 완료",
    },
    "en": {
        "admin_lock_error": "'{label}' is locked by Admin Lock and cannot be run or changed via chat.",
        "no_recent_chat": "(No recent prior chat)",
        "no_active_goals": "(No active goals)",
        "no_memories": "(No saved memories)",
        "none_item": "(None)",
        "not_queried": "(Not queried)",
        "reply_language_rule": (
            "Language rule: Default reply language is English. "
            "Follow the language and tone specified in <persona> or spoken by the sender, "
            "and reply in clean plain text without Markdown symbols."
        ),
        "tool_calling_addendum": (
            "[Function Calling Tool Rules]\n"
            "- This connection uses standard Function Calling (tools) mode.\n"
            "- Whenever you need to inspect status, locations, recipes, or memories, or run in-game actions, goals, or memory updates, call the provided functions (tools) directly instead of writing JSON actions.\n"
            "- After checking tool results, write a natural plain-text reply matching your persona and the sender/client language (no Markdown; plain text without JSON is also accepted).\n"
            "- Do not duplicate actions already executed via tools in any final JSON actions array."
        ),
        "task_follow_default": "Stopped following (reason: {phase})",
        "target_default": "target",
        "area_default": "that area",
        "task_fish_spot_default": "fishing spot",
        "task_fish_done": "Finished fishing {caught} fish at {spot}",
        "task_fish_stopped": "Stopped fishing at {spot} ({caught} caught total)",
        "task_gimmick_failed": "Failed to use traversal gimmick",
        "task_craft_done": "Crafting completed",
        "task_craft_failed": "Crafting failed",
        "task_farm_done": "Finished farming monster drop materials",
        "task_farm_failed": "Failed to farm monster drop materials",
        "task_monster_arrived": "Arrived at target monster spawn location",
        "task_monster_failed": "Failed to move to target monster location",
        "craft_not_found": "Could not find a crafting recipe for '{label}'.",
        "craft_already_owned": "Already own {owned} of '{name}'.",
        "craft_recipe_locked": "Crafting recipe is not unlocked yet",
        "craft_missing_base_equip": "Not enough unequipped base gear required for forging",
        "craft_fish_grade_missing": "Could not find fish data for that grade",
        "craft_sub_recipe_locked": "Sub-material crafting recipe is not unlocked yet",
        "craft_external_source_hint": "Requires Exchange, NPC Shop, or manual acquisition",
        "craft_focus_name": "Focus",
        "craft_focus_shortage": "Not enough Focus (needed {needed}, owned {owned})",
        "craft_blocked": "Auto-crafting is blocked: missing {deficit}x '{item_name}' ({hints}).",
        "craft_monster_item_fmt": "{deficit}x {item_name} ({monster_name})",
        "craft_needs_monster_confirm": "Missing monster drop materials: {desc}. Should I start hunting monsters for them?",
        "craft_ready": "Crafting plan ready for '{name}' x{crafts} ({steps} steps)",
    },
    "ja": {
        "admin_lock_error": "「{label}」機能は管理者ロック中のためチャットから実行・変更できません。",
        "no_recent_chat": "(直近の会話履歴なし)",
        "no_active_goals": "(実行中の目標なし)",
        "no_memories": "(保存された記憶なし)",
        "none_item": "(なし)",
        "not_queried": "(未照会)",
        "reply_language_rule": (
            "言語ルール: 基本の返答言語は日本語です。"
            "<persona>での言語指定や送信者の言語に合わせて、Markdown記号なしの自然な平文で返答してください。"
        ),
        "tool_calling_addendum": (
            "[Function Calling ツール使用ルール]\n"
            "- 現在の接続は標準のFunction Calling(tools)モードです。\n"
            "- ステータス・位置・レシピ・記憶の照会やゲーム内操作・目標・記憶の変更が必要な場合は、JSONのactions配列ではなく提供された関数(tools)を直接呼び出してください。\n"
            "- ツール実行結果を確認したあと、設定されたペルソナ・口調と送信者・クライアントの言語に合わせて自然な平文で返答してください(Markdown不可)。\n"
            "- すでにツールで実行済みの操作を最終JSONのactions配列に重複記載しないでください。"
        ),
        "task_follow_default": "追尾を中断しました (理由: {phase})",
        "target_default": "対象",
        "area_default": "該当エリア",
        "task_fish_spot_default": "釣り場",
        "task_fish_done": "{spot}で釣り{caught}匹完了",
        "task_fish_stopped": "{spot}での釣りを終了しました (合計{caught}匹)",
        "task_gimmick_failed": "移動ギミックの使用に失敗しました",
        "task_craft_done": "製作が完了しました",
        "task_craft_failed": "製作に失敗しました",
        "task_farm_done": "モンスタードロップ素材の確保が完了しました",
        "task_farm_failed": "モンスター素材のファーミングに失敗しました",
        "task_monster_arrived": "目標モンスターの出現地点に到着しました",
        "task_monster_failed": "目標モンスターの地点への移動に失敗しました",
        "craft_not_found": "「{label}」の製作レシピが見つかりませんでした。",
        "craft_already_owned": "「{name}」はすでに{owned}個所持しています。",
        "craft_recipe_locked": "製作レシピがまだ解放されていません",
        "craft_missing_base_equip": "装備鍛造に消費する未装備の下位装備が不足しています",
        "craft_fish_grade_missing": "該当グレードの魚情報が見つかりませんでした",
        "craft_sub_recipe_locked": "下位製作レシピが未解放です",
        "craft_external_source_hint": "取引所・ショップ・直接入手が必要です",
        "craft_focus_name": "スタミナ(集中力)",
        "craft_focus_shortage": "スタミナ(集中力)が不足しています (必要 {needed}、所持 {owned})",
        "craft_blocked": "「{item_name}」が{deficit}個不足しているため自動製作を開始できません ({hints})。",
        "craft_monster_item_fmt": "{item_name} {deficit}個({monster_name})",
        "craft_needs_monster_confirm": "不足しているモンスタードロップ素材があります: {desc}。モンスター討伐を開始しますか？",
        "craft_ready": "「{name}」x{crafts}の製作計画({steps}ステップ)の準備が完了しました",
    },
}


def policy_label(policy_key: str, lang: str = "ko") -> str:
    """Returns a localized feature policy label, falling back to Korean then raw key."""
    lang_code = normalize_ui_language(lang)
    table = POLICY_LABELS_BY_LANG.get(lang_code) or POLICY_LABELS_BY_LANG["ko"]
    if policy_key in table:
        return table[policy_key]
    return POLICY_LABELS_BY_LANG["ko"].get(policy_key, policy_key)


def t(key: str, lang: str = "ko", **kwargs: Any) -> str:
    """Looks up a localized bridge message string and applies optional `.format(**kwargs)`."""
    lang_code = normalize_ui_language(lang)
    table = MESSAGES.get(lang_code) or MESSAGES["ko"]
    tmpl = table.get(key) if key in table else MESSAGES["ko"].get(key, key)
    if kwargs:
        try:
            return tmpl.format(**kwargs)
        except Exception:  # noqa: BLE001
            return tmpl
    return tmpl
