"""Pure deterministic multi-stage crafting dependency planner (zero network I/O, C#-portable)."""

from dataclasses import dataclass
import math
from typing import Any

from goal_manager import GoalStep, infer_behavior_kind


@dataclass(frozen=True)
class MonsterFarmPrompt:
    item_id: int
    item_name: str
    deficit: int
    monster_id: int
    monster_name: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "item_id": self.item_id,
            "item_name": self.item_name,
            "deficit": self.deficit,
            "monster_id": self.monster_id,
            "monster_name": self.monster_name,
        }


@dataclass(frozen=True)
class BlockedMaterial:
    item_id: int
    item_name: str
    needed: int
    owned: int
    deficit: int
    reason: str
    obtain_hints: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        return {
            "item_id": self.item_id,
            "item_name": self.item_name,
            "needed": self.needed,
            "owned": self.owned,
            "deficit": self.deficit,
            "reason": self.reason,
            "obtain_hints": list(self.obtain_hints),
        }


@dataclass
class CraftPlanResult:
    """Deterministic crafting plan output (`ready`, `already_satisfied`, `needs_monster_confirm`, `blocked`, `not_found`)."""

    status: str
    target_recipe: dict[str, Any] | None = None
    requested_count: int = 0
    crafts_needed: int = 0
    total_focus_needed: int = 0
    steps: tuple[GoalStep, ...] = ()
    monster_prompts: tuple[MonsterFarmPrompt, ...] = ()
    blocked_materials: tuple[BlockedMaterial, ...] = ()
    message: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "status": self.status,
            "message": self.message,
            "requested_count": self.requested_count,
            "crafts_needed": self.crafts_needed,
            "total_focus_needed": self.total_focus_needed,
            "target_recipe": (
                {
                    "recipe_id": self.target_recipe.get("recipe_id"),
                    "name": self.target_recipe.get("name"),
                    "recipe_kind": self.target_recipe.get("recipe_kind"),
                    "output_item_id": self.target_recipe.get("output_item_id"),
                    "output_name": self.target_recipe.get("output_name"),
                    "yield_per_craft": self.target_recipe.get("yield_per_craft", 1),
                    "focus_cost": self.target_recipe.get("focus_cost", 0),
                }
                if self.target_recipe
                else None
            ),
            "steps": [s.to_dict() for s in self.steps],
            "monster_prompts": [m.to_dict() for m in self.monster_prompts],
            "blocked_materials": [b.to_dict() for b in self.blocked_materials],
        }


def select_target_recipe(
    recipes: list[dict[str, Any]],
    *,
    recipe_id: int = 0,
    query: str = "",
    unlock_map: dict[int, bool] | None = None,
) -> dict[str, Any] | None:
    """Selects the best matching recipe across all crafting domains (`life_build`, `cooking`, `alchemy`, `house_furniture`, `equip_create`)."""
    if recipe_id > 0:
        for r in recipes:
            if int(r.get("recipe_id") or 0) == recipe_id:
                return r
        return None

    q_clean = (query or "").strip().lower()
    if not q_clean:
        return None

    cands: list[tuple[tuple[int, int, int, int], dict[str, Any]]] = []
    for r in recipes:
        rid = int(r.get("recipe_id") or 0)
        name = str(r.get("name") or "").strip()
        out_name = str(r.get("output_name") or "").strip()
        n_low = name.lower()
        o_low = out_name.lower()
        exact = (n_low == q_clean) or (o_low == q_clean)
        partial = (q_clean in n_low) or (q_clean in o_low)
        if not exact and not partial:
            continue
        unlocked_rank = 0
        if unlock_map is not None and rid in unlock_map:
            unlocked_rank = 0 if unlock_map[rid] else 1
        cands.append(((unlocked_rank, 0 if exact else 1, len(name), rid), r))

    if not cands:
        return None
    cands.sort(key=lambda pair: pair[0])
    return cands[0][1]


def collect_candidate_item_ids(
    recipes: list[dict[str, Any]],
    root_recipe: dict[str, Any],
    max_depth: int = 5,
) -> list[int]:
    """Collects all item IDs referenced anywhere under `root_recipe`'s craft tree for bulk bag-count RPC."""
    by_output: dict[int, list[dict[str, Any]]] = {}
    for r in recipes:
        oid = int(r.get("output_item_id") or 0)
        if oid > 0:
            by_output.setdefault(oid, []).append(r)

    ids: set[int] = set()
    visited_recipes: set[int] = set()

    def _walk(rec: dict[str, Any], depth: int) -> None:
        rid = int(rec.get("recipe_id") or 0)
        if rid in visited_recipes or depth > max_depth:
            return
        visited_recipes.add(rid)
        oid = int(rec.get("output_item_id") or 0)
        if oid > 0:
            ids.add(oid)
        for m in rec.get("materials") or []:
            if not isinstance(m, dict):
                continue
            mid = int(m.get("item_id") or 0)
            if mid > 0:
                ids.add(mid)
                for sub_r in by_output.get(mid, ()):
                    _walk(sub_r, depth + 1)
            for cid in m.get("candidate_item_ids") or []:
                c_int = int(cid or 0)
                if c_int > 0:
                    ids.add(c_int)
        for cid in rec.get("base_equip_candidates") or []:
            c_int = int(cid or 0)
            if c_int > 0:
                ids.add(c_int)

    _walk(root_recipe, 0)
    return sorted(ids)


class CraftPlanner:
    """
    Expands recipe dependency trees deterministically against cached recipes, obtain ways,
    and live bag inventory counts to produce an ordered `GoalStep` execution list.
    """

    @staticmethod
    def _coalesce_steps(steps: list[GoalStep]) -> list[GoalStep]:
        merged: list[GoalStep] = []
        gather_idx: dict[str, int] = {}
        fish_idx: dict[str, int] = {}
        farm_idx: dict[tuple[int, str], int] = {}

        for st in steps:
            if st.action_type == "gather_resource":
                res = str(st.params.get("resource") or "")
                if res in gather_idx:
                    prev = merged[gather_idx[res]]
                    prev.target_count += st.target_count
                    prev.params["count"] = prev.target_count
                    continue
                gather_idx[res] = len(merged)
                merged.append(st)
            elif st.action_type == "fish":
                spot = str(st.params.get("spot") or "")
                if spot in fish_idx:
                    prev = merged[fish_idx[spot]]
                    prev.target_count += st.target_count
                    prev.params["count"] = prev.target_count
                    continue
                fish_idx[spot] = len(merged)
                merged.append(st)
            elif st.action_type == "farm_monster_item":
                key = (int(st.params.get("item_id") or 0), str(st.params.get("monster") or ""))
                if key in farm_idx:
                    prev = merged[farm_idx[key]]
                    prev.target_count = max(prev.target_count, st.target_count)
                    prev.params["count"] = prev.target_count
                    prev.params["target_count"] = prev.target_count
                    continue
                farm_idx[key] = len(merged)
                merged.append(st)
            else:
                merged.append(st)
        return merged

    def plan_craft(
        self,
        recipes: list[dict[str, Any]],
        obtain_ways: dict[int, dict[str, Any]],
        bag_counts: dict[int, int],
        *,
        recipe_id: int = 0,
        query: str = "",
        requested_count: int = 1,
        count_mode: str = "additional",
        allow_monster_farm: bool = False,
        available_focus: int = -1,
        unlock_map: dict[int, bool] | None = None,
        unequipped_base_equip_counts: dict[int, int] | None = None,
    ) -> CraftPlanResult:
        req_cnt = max(1, int(requested_count or 1))
        root = select_target_recipe(
            recipes,
            recipe_id=recipe_id,
            query=query,
            unlock_map=unlock_map,
        )
        if root is None:
            label = query.strip() or f"#{recipe_id}"
            return CraftPlanResult(
                status="not_found",
                requested_count=req_cnt,
                message=f"'{label}' 제작 레시피를 찾지 못했어요.",
            )

        root_id = int(root["recipe_id"])
        root_out_id = int(root.get("output_item_id") or 0)
        root_yield = max(1, int(root.get("yield_per_craft") or 1))
        initial_bag: dict[int, int] = {int(k): max(0, int(v)) for k, v in bag_counts.items()}
        virtual_bag: dict[int, int] = dict(initial_bag)

        if count_mode == "total_target" and root_out_id > 0:
            root_owned = virtual_bag.get(root_out_id, 0)
            root_deficit = max(0, req_cnt - root_owned)
            if root_deficit == 0:
                return CraftPlanResult(
                    status="already_satisfied",
                    target_recipe=root,
                    requested_count=req_cnt,
                    crafts_needed=0,
                    message=f"'{root.get('output_name') or root.get('name')}'은(는) 이미 {root_owned}개 보유 중이에요.",
                )
            root_crafts = math.ceil(root_deficit / root_yield)
        else:
            root_crafts = math.ceil(req_cnt / root_yield)

        by_output: dict[int, list[dict[str, Any]]] = {}
        for r in recipes:
            oid = int(r.get("output_item_id") or 0)
            if oid > 0:
                by_output.setdefault(oid, []).append(r)
        for oid in by_output:
            by_output[oid].sort(
                key=lambda r: (
                    0 if (unlock_map is None or unlock_map.get(int(r["recipe_id"]), True)) else 1,
                    int(r["recipe_id"]),
                )
            )

        steps: list[GoalStep] = []
        monster_deficits: dict[int, int] = {}
        monster_meta: dict[int, tuple[str, int, str]] = {}
        blocked: list[BlockedMaterial] = []
        focus_total = [0]

        def _consume_from_bag(item_id: int, need_qty: int) -> tuple[int, int]:
            have = virtual_bag.get(item_id, 0)
            take = min(have, need_qty)
            virtual_bag[item_id] = have - take
            return take, need_qty - take

        def _pick_sub_recipe(item_id: int, visiting_items: set[int]) -> dict[str, Any] | None:
            if item_id in visiting_items:
                return None
            cands = by_output.get(item_id) or []
            for cand in cands:
                cid = int(cand["recipe_id"])
                if unlock_map is not None and unlock_map.get(cid) is False:
                    continue
                return cand
            return None

        def _expand_recipe(rec: dict[str, Any], crafts_cnt: int, visiting_items: set[int]) -> None:
            rid = int(rec["recipe_id"])
            rname = str(rec.get("name") or f"Recipe#{rid}")
            rkind = str(rec.get("recipe_kind") or "life_build")
            oid = int(rec.get("output_item_id") or 0)
            ryield = max(1, int(rec.get("yield_per_craft") or 1))
            fcost = max(0, int(rec.get("focus_cost") or 0))
            focus_total[0] += fcost * crafts_cnt

            if unlock_map is not None and unlock_map.get(rid) is False:
                blocked.append(
                    BlockedMaterial(
                        item_id=oid,
                        item_name=rname,
                        needed=crafts_cnt * ryield,
                        owned=initial_bag.get(oid, 0),
                        deficit=crafts_cnt * ryield,
                        reason="recipe_locked",
                        obtain_hints=("제작 레시피가 아직 해금되지 않았어요",),
                    )
                )
                return

            if rkind == "equip_create" and int(rec.get("equip_name_group_id") or 0) > 0:
                if unequipped_base_equip_counts is not None:
                    base_have = int(unequipped_base_equip_counts.get(rid, 0))
                    if base_have < crafts_cnt:
                        blocked.append(
                            BlockedMaterial(
                                item_id=oid,
                                item_name=rname,
                                needed=crafts_cnt,
                                owned=base_have,
                                deficit=crafts_cnt - base_have,
                                reason="missing_base_equip",
                                obtain_hints=("장비 단조에 소모할 미착용 하위 장비가 부족해요",),
                            )
                        )
                        return

            next_visiting = set(visiting_items)
            if oid > 0:
                next_visiting.add(oid)

            for mat in rec.get("materials") or []:
                if not isinstance(mat, dict):
                    continue
                per_need = max(1, int(mat.get("count") or 1))
                total_need = per_need * crafts_cnt
                mid = int(mat.get("item_id") or 0)
                mname = str(mat.get("item_name") or f"Item#{mid}")

                # Special Case: generic fish category (NeedMaterialType == 2)
                fish_tb = int(mat.get("fish_type_b") or 0)
                if mid == 0 and fish_tb > 0:
                    cands = [int(x) for x in (mat.get("candidate_item_ids") or []) if int(x or 0) > 0]
                    rem = total_need
                    for cid in cands:
                        _, rem = _consume_from_bag(cid, rem)
                        if rem <= 0:
                            break
                    if rem > 0 and cands:
                        pick_id = cands[0]
                        ow = obtain_ways.get(pick_id) or {}
                        fish_info = ow.get("fish") or {}
                        spot_name = str(fish_info.get("name") or ow.get("item_name") or f"어류#{fish_tb}")
                        steps.append(
                            GoalStep(
                                action_type="fish",
                                params={"spot": spot_name, "count": rem},
                                target_count=rem,
                                behavior_kind=infer_behavior_kind("fish"),
                            )
                        )
                    elif rem > 0:
                        blocked.append(
                            BlockedMaterial(
                                item_id=0,
                                item_name=mname,
                                needed=total_need,
                                owned=total_need - rem,
                                deficit=rem,
                                reason="unknown_fish_category",
                                obtain_hints=("해당 등급 물고기 정보를 찾지 못했어요",),
                            )
                        )
                    continue

                if mid <= 0:
                    continue

                used_from_bag, deficit = _consume_from_bag(mid, total_need)
                if deficit <= 0:
                    continue

                # 1. Sub-recipe craftable
                sub_rec = _pick_sub_recipe(mid, next_visiting)
                if sub_rec is not None:
                    sub_yield = max(1, int(sub_rec.get("yield_per_craft") or 1))
                    sub_crafts = math.ceil(deficit / sub_yield)
                    _expand_recipe(sub_rec, sub_crafts, next_visiting)
                    surplus = sub_crafts * sub_yield - deficit
                    if surplus > 0:
                        virtual_bag[mid] = virtual_bag.get(mid, 0) + surplus
                    continue

                # 2. Obtain way resolution (gather > fish > monster > blocked)
                ow = obtain_ways.get(mid) or {}
                item_label = str(ow.get("item_name") or mname)
                gather = ow.get("gather") if isinstance(ow.get("gather"), dict) else None
                fish = ow.get("fish") if isinstance(ow.get("fish"), dict) else None
                monster = ow.get("monster") if isinstance(ow.get("monster"), dict) else None
                other_ways = tuple(str(x) for x in (ow.get("other_ways") or ()) if str(x))

                if gather and str(gather.get("resource_name") or "").strip():
                    res_name = str(gather["resource_name"]).strip()
                    steps.append(
                        GoalStep(
                            action_type="gather_resource",
                            params={"resource": res_name, "count": deficit},
                            target_count=deficit,
                            behavior_kind=infer_behavior_kind("gather_resource"),
                        )
                    )
                    continue

                if fish and str(fish.get("name") or "").strip():
                    fish_name = str(fish["name"]).strip()
                    steps.append(
                        GoalStep(
                            action_type="fish",
                            params={"spot": fish_name, "count": deficit},
                            target_count=deficit,
                            behavior_kind=infer_behavior_kind("fish"),
                        )
                    )
                    continue

                if monster and str(monster.get("monster_name") or "").strip():
                    mon_id = int(monster.get("monster_id") or 0)
                    mon_name = str(monster["monster_name"]).strip()
                    monster_deficits[mid] = monster_deficits.get(mid, 0) + deficit
                    monster_meta[mid] = (item_label, mon_id, mon_name)
                    if allow_monster_farm:
                        target_total = initial_bag.get(mid, 0) + monster_deficits[mid]
                        steps.append(
                            GoalStep(
                                action_type="farm_monster_item",
                                params={
                                    "monster": mon_name,
                                    "item_id": mid,
                                    "count": target_total,
                                    "target_count": target_total,
                                },
                                target_count=target_total,
                                behavior_kind=infer_behavior_kind("farm_monster_item"),
                            )
                        )
                    continue

                # Non-automatable source (exchange, NPC vendor, chest, cycle, locked sub-recipe, etc.)
                locked_cands = [
                    c for c in by_output.get(mid, ())
                    if unlock_map is not None and unlock_map.get(int(c["recipe_id"])) is False
                ]
                reason = "sub_recipe_locked" if locked_cands else "non_automatable_source"
                hints = other_ways or (("하위 제작 레시피가 미해금 상태예요",) if locked_cands else ("거래소·상점·직접 획득 필요",))
                blocked.append(
                    BlockedMaterial(
                        item_id=mid,
                        item_name=item_label,
                        needed=total_need,
                        owned=used_from_bag,
                        deficit=deficit,
                        reason=reason,
                        obtain_hints=hints,
                    )
                )

            steps.append(
                GoalStep(
                    action_type="craft_item",
                    params={
                        "recipe_id": rid,
                        "query": rname,
                        "recipe_kind": rkind,
                        "count": crafts_cnt,
                    },
                    target_count=crafts_cnt,
                    behavior_kind=infer_behavior_kind("craft_item"),
                )
            )

        _expand_recipe(root, root_crafts, set())

        if available_focus >= 0 and focus_total[0] > available_focus:
            blocked.append(
                BlockedMaterial(
                    item_id=20003,
                    item_name="집중력",
                    needed=focus_total[0],
                    owned=available_focus,
                    deficit=focus_total[0] - available_focus,
                    reason="insufficient_focus",
                    obtain_hints=(f"집중력이 부족해요 (필요 {focus_total[0]}, 보유 {available_focus})",),
                )
            )

        if blocked:
            first_b = blocked[0]
            return CraftPlanResult(
                status="blocked",
                target_recipe=root,
                requested_count=req_cnt,
                crafts_needed=root_crafts,
                total_focus_needed=focus_total[0],
                blocked_materials=tuple(blocked),
                message=f"'{first_b.item_name}' {first_b.deficit}개 부족으로 자동 제작이 막혔어요 ({', '.join(first_b.obtain_hints)}).",
            )

        if monster_deficits and not allow_monster_farm:
            prompts = tuple(
                MonsterFarmPrompt(
                    item_id=mid,
                    item_name=monster_meta[mid][0],
                    deficit=tot_def,
                    monster_id=monster_meta[mid][1],
                    monster_name=monster_meta[mid][2],
                )
                for mid, tot_def in monster_deficits.items()
            )
            desc = ", ".join(f"{p.item_name} {p.deficit}개({p.monster_name})" for p in prompts)
            return CraftPlanResult(
                status="needs_monster_confirm",
                target_recipe=root,
                requested_count=req_cnt,
                crafts_needed=root_crafts,
                total_focus_needed=focus_total[0],
                monster_prompts=prompts,
                message=f"부족한 몬스터 드랍 재료가 있어요: {desc}. 몬스터 사냥을 시작할까요?",
            )

        final_steps = tuple(self._coalesce_steps(steps))
        return CraftPlanResult(
            status="ready",
            target_recipe=root,
            requested_count=req_cnt,
            crafts_needed=root_crafts,
            total_focus_needed=focus_total[0],
            steps=final_steps,
            message=f"'{root.get('name')}' x{root_crafts} 제작 계획 {len(final_steps)}단계 준비 완료",
        )
