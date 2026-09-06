using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PathOfIdleInGameSearch;

// Actual Workshop operations live outside Auto Build. Auto Build compares and
// equips owned gear; this service compares the selected hero's already-equipped
// items and spends resources on exactly one native +1 Forge action.
internal static partial class GameInventoryReader
{
    private static IReadOnlyDictionary<int, string>? forgeBattleAttributeLabels;

    private sealed record ForgeSlotSnapshot(
        GearSlot Slot,
        object Item,
        GearCandidate CurrentCandidate);

    private sealed record ForgeApplyOutcome(
        bool Applied,
        bool Reequipped,
        int ActualLevel,
        IReadOnlyList<ForgeAttributeComparison> ActualAttributes,
        bool ResourceInsufficient,
        string Error);

    private sealed record EquippedForgeTarget(
        GearSlot Slot,
        object Item,
        string ItemKey,
        string ItemName,
        int InitialLevel);

    internal static EquipmentForgeOptimizationResult OptimizeSelectedHeroForgeOneLevel()
        => EvaluateSelectedHeroForgeOneLevel(applyMutation: true);

    internal static EquipmentForgeOptimizationResult MaxForgeSelectedHeroEquipment()
    {
        EquipmentForgeCandidatePreview? lastPreview = null;
        var mutationApplied = false;
        try
        {
            WaitForAutoBuildReadiness();
            var operation = MainThreadNativeGateway.CurrentWorkerOperation
                            ?? throw new InvalidOperationException(
                                "Maximum Forge requires a managed operation.");
            var hero = GetSelectedHeroAfterAutoBuildWait();
            if (hero is null)
                return ForgeFailure(UiText.L(
                    "선택된 영웅이 없습니다.",
                    "No hero is selected.",
                    "未选择英雄。",
                    "未選擇英雄。"));

            var workshop = MainThreadNativeGateway.Invoke(ReadWorkshopState);
            if (!workshop.Available) return ForgeFailure(workshop.Error);
            var snapshot = MainThreadNativeGateway.Invoke(() =>
            {
                var targets = GetGearSlots().Select(slot =>
                    {
                        var item = GetEquippedItem(hero, slot.Part, slot.MainWeapon);
                        if (item is null) return null;
                        var field = GetEquipDestinationField(hero, slot);
                        if (field is null || !IsVerifiedHeroEquipField(hero, item, field))
                            throw new InvalidOperationException(
                                $"The equipped item in {slot.Label} could not be verified.");
                        var level = Math.Max(0,
                            ReadNullableInt(Read(item, "saveItemData"), "forgeLevel") ?? 0);
                        return new EquippedForgeTarget(
                            slot,
                            item,
                            NativeObjectKey(item, item),
                            Clean(InvokeString(item, "GetName") ?? slot.Label),
                            level);
                    })
                    .Where(target => target is not null)
                    .Cast<EquippedForgeTarget>()
                    .ToList();
                return (
                    Targets: targets,
                    GearFingerprint: GetCurrentGearFingerprint(hero),
                    SkillFingerprint: GetSkillStateFingerprint(hero),
                    HeroKey: GetJointSkillPlanHeroKey(hero));
            });
            if (!string.Equals(snapshot.HeroKey, operation.TargetHeroKey, StringComparison.Ordinal))
                return ForgeFailure("The selected hero changed after maximum Forge started.");
            if (snapshot.Targets.Count == 0)
                return ForgeFailure(UiText.L(
                    "선택한 영웅이 장비를 착용하지 않았습니다.",
                    "The selected hero has no equipped gear.",
                    "所选英雄未装备物品。",
                    "所選英雄未裝備物品。"));

            var levels = snapshot.Targets.ToDictionary(
                target => target.ItemKey,
                target => target.InitialLevel,
                StringComparer.Ordinal);
            var remainingLevels = levels.Values.Sum(level =>
                Math.Max(0, workshop.LevelMax - level));
            if (remainingLevels == 0)
                return new EquipmentForgeOptimizationResult(
                    true, false, false,
                    UiText.L(
                        $"착용 장비 {snapshot.Targets.Count}개가 이미 최대 +{workshop.LevelMax}입니다.",
                        $"All {snapshot.Targets.Count} equipped items are already at +{workshop.LevelMax}.",
                        $"{snapshot.Targets.Count} 件已装备物品均已达到 +{workshop.LevelMax}。",
                        $"{snapshot.Targets.Count} 件已裝備物品均已達到 +{workshop.LevelMax}。"),
                    null);

            BeginLiveMutation(hero, snapshot.GearFingerprint, snapshot.SkillFingerprint);
            var failures = new List<string>();
            var failedItems = new HashSet<string>(StringComparer.Ordinal);
            var resourceExhausted = false;
            var completedLevels = 0;
            var guard = 0;
            var guardLimit = Math.Max(16,
                snapshot.Targets.Count * ((workshop.LevelMax + 4) / 5 + 2));
            while (guard++ < guardLimit)
            {
                var target = snapshot.Targets
                    .Where(entry => !failedItems.Contains(entry.ItemKey)
                                    && levels[entry.ItemKey] < workshop.LevelMax)
                    .OrderBy(entry => levels[entry.ItemKey])
                    .ThenBy(entry => entry.Slot.Part)
                    .ThenBy(entry => entry.Slot.MainWeapon ? 0 : 1)
                    .FirstOrDefault();
                if (target is null) break;

                var before = levels[target.ItemKey];
                var batch = Math.Min(5, workshop.LevelMax - before);
                (EquipmentForgeCandidatePreview Preview, ForgeApplyOutcome Outcome) ApplyBatch(int count)
                    => MainThreadNativeGateway.Invoke(() =>
                    {
                        var currentFingerprint = GetCurrentGearFingerprint(hero);
                        var preview = CreateDirectForgePreview(
                            target.Slot, target.Item, before + count);
                        var outcome = ApplyForgeCandidateCore(
                            hero,
                            target.Slot,
                            target.Item,
                            preview,
                            currentFingerprint,
                            snapshot.SkillFingerprint);
                        return (preview, outcome);
                    });

                var applied = ApplyBatch(batch);
                lastPreview = applied.Preview;
                if (!applied.Outcome.Applied
                    && applied.Outcome.ResourceInsufficient
                    && batch > 1)
                {
                    batch = 1;
                    applied = ApplyBatch(batch);
                    lastPreview = applied.Preview;
                }

                if (!applied.Outcome.Applied)
                {
                    if (applied.Outcome.ResourceInsufficient)
                    {
                        resourceExhausted = true;
                        failures.Add("Forge resources are insufficient for another +1 level.");
                        break;
                    }
                    failures.Add($"{target.Slot.Label}/{target.ItemName}: {applied.Outcome.Error}");
                    failedItems.Add(target.ItemKey);
                    continue;
                }

                mutationApplied = true;
                levels[target.ItemKey] = applied.Outcome.ActualLevel;
                completedLevels += Math.Max(0, applied.Outcome.ActualLevel - before);
                Plugin.DiagInfo(
                    $"FORGE MAX EQUIPPED|slot={target.Slot.Label}|item={target.ItemName}|before={before}|after={applied.Outcome.ActualLevel}|limit={workshop.LevelMax}");
                operation.SetProgress(
                    AutoBuildStep.ApplyingGear,
                    Math.Min(remainingLevels, completedLevels),
                    remainingLevels,
                    $"{target.Slot.Label}: +{before} -> +{applied.Outcome.ActualLevel}");
                if (!applied.Outcome.Reequipped || !string.IsNullOrWhiteSpace(applied.Outcome.Error))
                {
                    failures.Add($"{target.Slot.Label}/{target.ItemName}: {applied.Outcome.Error}");
                    break;
                }
            }

            if (guard > guardLimit)
                failures.Add("Maximum Forge stopped at its bounded operation limit.");

            var finalState = MainThreadNativeGateway.Invoke(() => snapshot.Targets.Select(target =>
                {
                    var field = GetEquipDestinationField(hero, target.Slot);
                    var exact = field is not null
                                && NativeEquals(Read(field, "itemData"), target.Item)
                                && IsVerifiedHeroEquipField(hero, target.Item, field);
                    var level = Math.Max(0,
                        ReadNullableInt(Read(target.Item, "saveItemData"), "forgeLevel") ?? -1);
                    return (Target: target, Exact: exact, Level: level);
                }).ToList());
            foreach (var state in finalState)
            {
                levels[state.Target.ItemKey] = state.Level;
                if (!state.Exact)
                    failures.Add($"{state.Target.Slot.Label}: original equipped item was not restored.");
            }

            var summaries = finalState.Select(state =>
                    $"{state.Target.Slot.Label} +{state.Target.InitialLevel}->+{state.Level}")
                .ToList();
            var allMax = finalState.All(state => state.Exact && state.Level >= workshop.LevelMax);
            if (allMax && failures.Count == 0)
                return new EquipmentForgeOptimizationResult(
                    true, false, mutationApplied,
                    UiText.L(
                        $"착용 장비 최대 강화 완료 · 상한 +{workshop.LevelMax} · {string.Join(" · ", summaries)}",
                        $"All equipped gear reached +{workshop.LevelMax} · {string.Join(" · ", summaries)}",
                        $"所有已装备物品已达到 +{workshop.LevelMax} · {string.Join(" · ", summaries)}",
                        $"所有已裝備物品已達到 +{workshop.LevelMax} · {string.Join(" · ", summaries)}"),
                    lastPreview);

            var reason = string.Join("; ", failures.Distinct(StringComparer.Ordinal));
            if (string.IsNullOrWhiteSpace(reason))
                reason = resourceExhausted
                    ? "Forge resources were exhausted."
                    : "One or more equipped items did not reach the native maximum.";
            return new EquipmentForgeOptimizationResult(
                false, false, mutationApplied,
                UiText.L(
                    $"착용 장비 최대 강화가 일부만 적용되었습니다 · {string.Join(" · ", summaries)} · {reason}",
                    $"Maximum Forge was only partially applied · {string.Join(" · ", summaries)} · {reason}",
                    $"装备最大强化仅部分完成 · {string.Join(" · ", summaries)} · {reason}",
                    $"裝備最大強化僅部分完成 · {string.Join(" · ", summaries)} · {reason}"),
                lastPreview);
        }
        catch (Exception error)
        {
            return new EquipmentForgeOptimizationResult(
                false, false, mutationApplied
                              || MainThreadNativeGateway.CurrentWorkerOperation?.MutationStarted == true,
                UiText.L(
                    $"착용 장비 최대 강화 실패: {error.GetBaseException().Message}",
                    $"Maximum equipped-gear Forge failed: {error.GetBaseException().Message}",
                    $"已装备物品最大强化失败：{error.GetBaseException().Message}",
                    $"已裝備物品最大強化失敗：{error.GetBaseException().Message}"),
                lastPreview);
        }
    }

#if PATHOFIDLE_RUNTIME_TEST
    internal static EquipmentForgeOptimizationResult PreviewSelectedHeroForgeOneLevel()
        => EvaluateSelectedHeroForgeOneLevel(applyMutation: false);

    internal static string CaptureSelectedHeroForgeState()
    {
        var hero = GetSelectedHero();
        if (hero is null) return "unavailable:selected hero is not loaded";
        return string.Join("|", GetGearSlots().Select(slot =>
        {
            var item = GetEquippedItem(hero, slot.Part, slot.MainWeapon);
            if (item is null) return $"{slot.Label}=empty";
            var level = Math.Max(0,
                ReadNullableInt(Read(item, "saveItemData"), "forgeLevel") ?? 0);
            return $"{slot.Label}={NativeObjectKey(item, item)}@+{level}";
        }));
    }
#endif

    private static EquipmentForgeOptimizationResult EvaluateSelectedHeroForgeOneLevel(bool applyMutation)
    {
        EquipmentForgeCandidatePreview? best = null;
        try
        {
            WaitForAutoBuildReadiness();
            var operation = MainThreadNativeGateway.CurrentWorkerOperation
                            ?? throw new InvalidOperationException("Forge optimization requires a managed operation.");
            var hero = GetSelectedHeroAfterAutoBuildWait();
            if (hero is null)
                return ForgeFailure(UiText.L(
                    "선택된 영웅이 없습니다.",
                    "No hero is selected.",
                    "未选择英雄。",
                    "未選擇英雄。"));

            operation.SetProgress(
                AutoBuildStep.CollectingCandidates,
                0,
                8,
                UiText.L(
                    "현재 장비와 작업대 상태 확인",
                    "Checking current equipment and Workshop state",
                    "正在检查当前装备与工坊状态",
                    "正在檢查目前裝備與工坊狀態"));

            var workshop = MainThreadNativeGateway.Invoke(ReadWorkshopState);
            if (!workshop.Available)
                return ForgeFailure(workshop.Error);

            var profileSnapshot = MainThreadNativeGateway.Invoke(() =>
            {
                var focus = ResolveHeroFocus(hero, GetHeroBuildTheme(hero));
                return (
                    Profile: BuildHeroEffectProfile(hero, focus),
                    GearFingerprint: GetCurrentGearFingerprint(hero),
                    SkillFingerprint: GetSkillStateFingerprint(hero),
                    HeroKey: GetJointSkillPlanHeroKey(hero));
            });
            var profile = profileSnapshot.Profile;
            var gearFingerprint = profileSnapshot.GearFingerprint;
            var skillFingerprint = profileSnapshot.SkillFingerprint;
            var heroKey = profileSnapshot.HeroKey;
            if (!string.Equals(heroKey, operation.TargetHeroKey, StringComparison.Ordinal))
                return ForgeFailure(UiText.L(
                    "강화 계산을 시작한 뒤 선택 영웅이 바뀌었습니다.",
                    "The selected hero changed after Forge optimization started.",
                    "强化计算开始后所选英雄已更改。",
                    "強化計算開始後所選英雄已變更。"));

            operation.ThrowIfPlanningStopped();
            var snapshots = MainThreadNativeGateway.Invoke(() =>
            {
                var captured = new List<ForgeSlotSnapshot>();
                foreach (var slot in GetGearSlots())
                {
                    var item = GetEquippedItem(hero, slot.Part, slot.MainWeapon);
                    if (item is null) continue;
                    var record = DescribeEquipmentSource(
                        item,
                        UiText.L("현재 착용", "Equipped", "当前装备", "目前裝備"),
                        StorageKind.Inventory,
                        StorageSource.Equipped,
                        GetEquipDestinationField(hero, slot));
                    var candidate = CreateGearCandidate(
                        record, hero, profile, allowExistingEquippedItem: true)
                        ?? throw new InvalidOperationException(
                            $"The {slot.Label} item could not be converted for performance comparison.");
                    captured.Add(new ForgeSlotSnapshot(slot, item, candidate));
                }
                return captured;
            });

            if (snapshots.Count == 0)
                return ForgeFailure(UiText.L(
                    "선택 영웅이 장비를 착용하지 않았습니다.",
                    "The selected hero has no equipped gear.",
                    "所选英雄未装备任何物品。",
                    "所選英雄未裝備任何物品。"));

            var currentItems = snapshots.Select(snapshot => snapshot.Item).ToList();
            var currentCandidates = snapshots.Select(snapshot => snapshot.CurrentCandidate).ToList();
            var baseline = MainThreadNativeGateway.Invoke(() =>
            {
                var valid = TryEvaluateForgePerformance(
                    currentCandidates, hero, profile, currentItems, out var value);
                return (Valid: valid, Value: value);
            });
            var currentPerformance = baseline.Value;
            if (!baseline.Valid)
                return ForgeFailure(UiText.L(
                    "현재 장비의 30초 성능을 네이티브 규칙으로 계산하지 못해 강화하지 않았습니다.",
                    "The current gear's 30-second performance could not be evaluated with native rules, so nothing was forged.",
                    "无法按原生规则计算当前装备的 30 秒性能，因此未进行强化。",
                    "無法依原生規則計算目前裝備的 30 秒效能，因此未進行強化。"));

            var previews = new List<EquipmentForgeCandidatePreview>();
            for (var index = 0; index < snapshots.Count; index++)
            {
                operation.ThrowIfPlanningStopped();
                var snapshot = snapshots[index];
                operation.SetProgress(
                    AutoBuildStep.FinalRefinement,
                    index,
                    snapshots.Count,
                    UiText.L(
                        $"{snapshot.Slot.Label}: 현재값과 +1 강화값 비교",
                        $"{snapshot.Slot.Label}: comparing current values with +1 Forge",
                        $"{snapshot.Slot.Label}：比较当前值与 +1 强化值",
                        $"{snapshot.Slot.Label}：比較目前值與 +1 強化值"));

                var preview = MainThreadNativeGateway.Invoke(() =>
                {
                    var source = GetEquipDestinationField(hero, snapshot.Slot);
                    if (source is null
                        || !NativeEquals(Read(source, "itemData"), snapshot.Item)
                        || !IsVerifiedHeroEquipField(hero, snapshot.Item, source)) return null;
                    var workshopValidation = ToInt(InvokeRequiredInstance(
                        workshop.HouseWork, "CheckAddItem", source, workshop.WorkField));
                    if (workshopValidation != 0) return null;
                    var save = Read(snapshot.Item, "saveItemData");
                    var currentLevel = Math.Max(0, ReadNullableInt(save, "forgeLevel") ?? 0);
                    if (currentLevel >= workshop.LevelMax) return null;
                    var currentEquip = Read(snapshot.Item, "itemEquipData");
                    if (currentEquip is null) return null;
                    var nextItem = CloneEquipmentAtForgeLevel(snapshot.Item, currentLevel + 1);
                    var nextRecord = DescribeEquipmentSource(
                        nextItem,
                        UiText.L("+1 강화 예상", "+1 Forge preview", "+1 强化预览", "+1 強化預覽"),
                        StorageKind.Inventory,
                        StorageSource.Equipped);
                    var nextCandidate = CreateGearCandidate(
                        nextRecord, hero, profile, allowExistingEquippedItem: true);
                    if (nextCandidate is null) return null;
                    var testCandidates = currentCandidates.ToList();
                    testCandidates[index] = nextCandidate;
                    if (!TryEvaluateForgePerformance(
                            testCandidates, hero, profile, currentItems, out var nextPerformance))
                        return null;
                    var price = Math.Max(0,
                        ToInt(InvokeRequiredInstance(currentEquip, "GetForgePrice")));
                    var gain = nextPerformance - currentPerformance;
                    return new EquipmentForgeCandidatePreview(
                        NativeObjectKey(snapshot.Item, snapshot.Item),
                        Clean(InvokeString(snapshot.Item, "GetName")
                              ?? snapshot.CurrentCandidate.Record.Name),
                        snapshot.Slot.Label,
                        currentLevel,
                        currentLevel + 1,
                        price,
                        currentPerformance,
                        nextPerformance,
                        gain,
                        gain / Math.Max(1d, price),
                        ReadForgeAttributeComparison(
                            currentEquip,
                            Read(nextItem, "itemEquipData")
                            ?? throw new InvalidOperationException(
                                "The cloned equipment has no ItemEquipData.")));
                });
                if (preview is not null) previews.Add(preview);
            }

            operation.SetProgress(
                AutoBuildStep.FinalRefinement,
                snapshots.Count,
                snapshots.Count,
                UiText.L(
                    "강화 후보 순위 확정",
                    "Ranking Forge candidates",
                    "正在确定强化候选排名",
                    "正在確定強化候選排名"));
            best = previews
                .Where(candidate => candidate.PerformanceGain > 0d
                                    && double.IsFinite(candidate.PerformanceGain)
                                    && double.IsFinite(candidate.Efficiency))
                .OrderByDescending(candidate => candidate.Efficiency)
                .ThenByDescending(candidate => candidate.PerformanceGain)
                .ThenBy(candidate => candidate.Price)
                .ThenBy(candidate => candidate.Slot, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best is null)
                return ForgeFailure(UiText.L(
                    $"강화 가능한 {previews.Count:N0}개 장비를 비교했지만 현재 30초 빌드 성능을 높이는 +1 후보가 없었습니다.",
                    $"Compared {previews.Count:N0} forgeable items, but no +1 candidate improved the current 30-second build performance.",
                    $"已比较 {previews.Count:N0} 件可强化装备，但没有 +1 候选能提升当前 30 秒构筑性能。",
                    $"已比較 {previews.Count:N0} 件可強化裝備，但沒有 +1 候選能提升目前 30 秒配置效能。"));

            if (!applyMutation)
            {
                var previewAttributes = FormatForgeAttributeSummary(best.Attributes);
                return new EquipmentForgeOptimizationResult(
                    true, false, false,
                    UiText.L(
                        $"읽기 전용 결과: {best.Slot}의 {best.ItemName} +{best.CurrentLevel}→+{best.NextLevel} · 비용 {best.Price:N0} · 30초 성능 +{best.PerformanceGain:0.###} · {previewAttributes}",
                        $"Read-only result: {best.ItemName} in {best.Slot} +{best.CurrentLevel}→+{best.NextLevel} · cost {best.Price:N0} · 30s performance +{best.PerformanceGain:0.###} · {previewAttributes}",
                        $"只读结果：{best.Slot} 的 {best.ItemName} +{best.CurrentLevel}→+{best.NextLevel} · 费用 {best.Price:N0} · 30 秒性能 +{best.PerformanceGain:0.###} · {previewAttributes}",
                        $"唯讀結果：{best.Slot} 的 {best.ItemName} +{best.CurrentLevel}→+{best.NextLevel} · 費用 {best.Price:N0} · 30 秒效能 +{best.PerformanceGain:0.###} · {previewAttributes}"),
                    best);
            }

            operation.ThrowIfPlanningStopped();
            var selectedSnapshot = snapshots.First(snapshot =>
                string.Equals(
                    NativeObjectKey(snapshot.Item, snapshot.Item),
                    best.ItemKey,
                    StringComparison.Ordinal));
            BeginLiveMutation(hero, gearFingerprint, skillFingerprint);
            operation.SetProgress(
                AutoBuildStep.ApplyingGear,
                0,
                1,
                UiText.L(
                    $"{best.ItemName} +1 강화 적용",
                    $"Applying +1 Forge to {best.ItemName}",
                    $"正在为 {best.ItemName} 应用 +1 强化",
                    $"正在為 {best.ItemName} 套用 +1 強化"));

            var applyOutcome = MainThreadNativeGateway.Invoke(() => ApplyForgeCandidateCore(
                hero,
                selectedSnapshot.Slot,
                selectedSnapshot.Item,
                best,
                gearFingerprint,
                skillFingerprint));
            operation.SetProgress(
                AutoBuildStep.Verifying,
                1,
                1,
                UiText.L(
                    "강화 결과와 재착용 검증",
                    "Verifying Forge result and re-equipment",
                    "正在验证强化结果与重新装备",
                    "正在驗證強化結果與重新裝備"));

            if (!applyOutcome.Applied)
                return new EquipmentForgeOptimizationResult(
                    false, false, false,
                    UiText.L(
                        $"강화하지 않았습니다: {applyOutcome.Error}",
                        $"Nothing was forged: {applyOutcome.Error}",
                        $"未进行强化：{applyOutcome.Error}",
                        $"未進行強化：{applyOutcome.Error}"),
                    best);
            if (!applyOutcome.Reequipped)
                return new EquipmentForgeOptimizationResult(
                    false, false, true,
                    UiText.L(
                        $"{best.ItemName}은 +{applyOutcome.ActualLevel} 강화됐지만 원래 슬롯 재착용을 확인하지 못했습니다: {applyOutcome.Error}",
                        $"{best.ItemName} reached +{applyOutcome.ActualLevel}, but re-equipping it to the original slot was not verified: {applyOutcome.Error}",
                        $"{best.ItemName} 已强化至 +{applyOutcome.ActualLevel}，但无法确认重新装备到原槽位：{applyOutcome.Error}",
                        $"{best.ItemName} 已強化至 +{applyOutcome.ActualLevel}，但無法確認重新裝備到原欄位：{applyOutcome.Error}"),
                    best);
            if (!string.IsNullOrWhiteSpace(applyOutcome.Error))
                return new EquipmentForgeOptimizationResult(
                    false, false, true,
                    UiText.L(
                        $"{best.ItemName}은 +{applyOutcome.ActualLevel} 강화 및 재착용됐지만 예상 수치 검증이 일치하지 않았습니다: {applyOutcome.Error}",
                        $"{best.ItemName} reached +{applyOutcome.ActualLevel} and was re-equipped, but predicted-value verification did not match: {applyOutcome.Error}",
                        $"{best.ItemName} 已强化至 +{applyOutcome.ActualLevel} 并重新装备，但预测数值验证不一致：{applyOutcome.Error}",
                        $"{best.ItemName} 已強化至 +{applyOutcome.ActualLevel} 並重新裝備，但預測數值驗證不一致：{applyOutcome.Error}"),
                    best);

            var attrSummary = FormatForgeAttributeSummary(applyOutcome.ActualAttributes);
            return new EquipmentForgeOptimizationResult(
                true, false, true,
                UiText.L(
                    $"완료: {best.Slot}의 {best.ItemName} +{best.CurrentLevel}→+{applyOutcome.ActualLevel} · 비용 {best.Price:N0} · 30초 성능 +{best.PerformanceGain:0.###} · {attrSummary}",
                    $"Done: {best.ItemName} in {best.Slot} +{best.CurrentLevel}→+{applyOutcome.ActualLevel} · cost {best.Price:N0} · 30s performance +{best.PerformanceGain:0.###} · {attrSummary}",
                    $"完成：{best.Slot} 的 {best.ItemName} +{best.CurrentLevel}→+{applyOutcome.ActualLevel} · 费用 {best.Price:N0} · 30 秒性能 +{best.PerformanceGain:0.###} · {attrSummary}",
                    $"完成：{best.Slot} 的 {best.ItemName} +{best.CurrentLevel}→+{applyOutcome.ActualLevel} · 費用 {best.Price:N0} · 30 秒效能 +{best.PerformanceGain:0.###} · {attrSummary}"),
                best);
        }
        catch (OperationCanceledException)
        {
            return new EquipmentForgeOptimizationResult(
                false, true, false,
                UiText.L(
                    "장비 강화 최적화 계산을 취소했습니다. 자원은 사용하지 않았습니다.",
                    "Forge optimization was cancelled. No resources were spent.",
                    "已取消装备强化优化。未消耗资源。",
                    "已取消裝備強化最佳化。未消耗資源。"),
                best);
        }
        catch (Exception error)
        {
            return new EquipmentForgeOptimizationResult(
                false, false,
                MainThreadNativeGateway.CurrentWorkerOperation?.MutationStarted == true,
                UiText.L(
                    $"장비 강화 최적화 실패: {error.GetBaseException().Message}",
                    $"Forge optimization failed: {error.GetBaseException().Message}",
                    $"装备强化优化失败：{error.GetBaseException().Message}",
                    $"裝備強化最佳化失敗：{error.GetBaseException().Message}"),
                best);
        }
    }

    private static EquipmentForgeOptimizationResult ForgeFailure(string message)
        => new(false, false, false, message, null);

    private static bool TryEvaluateForgePerformance(
        List<GearCandidate> candidates,
        object hero,
        HeroEffectProfile profile,
        List<object> currentItems,
        out double score)
    {
        var simulated = CreateJointLoadoutAttr(
            hero,
            candidates,
            currentItems,
            profile.PlannedSkills,
            includePlannedMasteries: true,
            neutralObjectiveAttr: profile.PlannedSkills.NeutralObjectiveAttr);
        var abilities = EvaluateNativeAbilityPackage(candidates, hero, profile, simulated);
        return TryEvaluateFinalPerformance(
            candidates,
            hero,
            profile,
            currentItems,
            out score,
            out _,
            simulated,
            abilities,
            baseSkillOnly: false);
    }

    private static EquipmentForgeCandidatePreview CreateDirectForgePreview(
        GearSlot slot,
        object item,
        int targetForgeLevel)
    {
        var save = Read(item, "saveItemData")
                   ?? throw new InvalidOperationException("SaveItemData is unavailable.");
        var currentLevel = Math.Max(0, ReadNullableInt(save, "forgeLevel") ?? 0);
        if (targetForgeLevel <= currentLevel || targetForgeLevel - currentLevel > 5)
            throw new InvalidOperationException(
                $"Direct Forge preview requires a 1-5 level increase ({currentLevel}->{targetForgeLevel}).");
        var currentEquip = Read(item, "itemEquipData")
                           ?? throw new InvalidOperationException(
                               "Current ItemEquipData is unavailable.");
        var nextItem = CloneEquipmentAtForgeLevel(item, targetForgeLevel);
        var nextEquip = Read(nextItem, "itemEquipData")
                        ?? throw new InvalidOperationException(
                            "The cloned equipment has no ItemEquipData.");
        return new EquipmentForgeCandidatePreview(
            NativeObjectKey(item, item),
            Clean(InvokeString(item, "GetName") ?? slot.Label),
            slot.Label,
            currentLevel,
            targetForgeLevel,
            Math.Max(0, ToInt(InvokeRequiredInstance(currentEquip, "GetForgePrice"))),
            0d,
            0d,
            0d,
            0d,
            ReadForgeAttributeComparison(currentEquip, nextEquip));
    }

    private static object CloneEquipmentAtForgeLevel(object item, int targetForgeLevel)
    {
        var save = Read(item, "saveItemData")
                   ?? throw new InvalidOperationException("SaveItemData is unavailable.");
        var clonedSave = InvokeRequiredInstance(save, "Clone")
                         ?? throw new InvalidOperationException("SaveItemData.Clone returned null.");
        var currentLevel = Math.Max(0, ReadNullableInt(clonedSave, "forgeLevel") ?? 0);
        var posType = Read(item, "posType") ?? CreateEnum("EItemPosType", 0)
                      ?? throw new InvalidOperationException("EItemPosType.none is unavailable.");
        var clone = InvokeRequiredStaticMany("ItemData", "Create", clonedSave, posType, -1)
                    ?? throw new InvalidOperationException("ItemData.Create returned null.");
        var clonedEquip = Read(clone, "itemEquipData")
                          ?? throw new InvalidOperationException("Cloned ItemEquipData is unavailable.");
        var delta = targetForgeLevel - currentLevel;
        if (delta != 0) InvokeRequiredInstance(clonedEquip, "AddForgeLevel", delta);
        var actualLevel = Math.Max(0, ReadNullableInt(Read(clone, "saveItemData"), "forgeLevel") ?? 0);
        if (actualLevel != targetForgeLevel)
            throw new InvalidOperationException(
                $"Forge clone level mismatch (expected {targetForgeLevel}, got {actualLevel}).");
        return clone;
    }

    private static IReadOnlyList<ForgeAttributeComparison> ReadForgeAttributeComparison(
        object currentEquip,
        object nextEquip)
    {
        var currentAttr = Read(currentEquip, "equipAttrData")
                          ?? throw new InvalidOperationException("Current EquipAttrData is unavailable.");
        var nextAttr = Read(nextEquip, "equipAttrData")
                       ?? throw new InvalidOperationException("Next EquipAttrData is unavailable.");
        var result = new List<ForgeAttributeComparison>();
        foreach (var mapping in GetEquipAttrMappings())
        {
            var equipType = CreateEnum("EEquipAttrType", mapping.EquipType)
                            ?? throw new InvalidOperationException(
                                $"Unknown equipment attribute type {mapping.EquipType}.");
            var current = Convert.ToDouble(
                InvokeRequiredInstance(currentAttr, "GetAttrValue", equipType, null!) ?? 0d,
                CultureInfo.InvariantCulture);
            var next = Convert.ToDouble(
                InvokeRequiredInstance(nextAttr, "GetAttrValue", equipType, null!) ?? 0d,
                CultureInfo.InvariantCulture);
            var preForge = Convert.ToDouble(
                InvokeRequiredInstance(currentEquip, "GetCoreAttrValueWithoutForge", equipType) ?? 0d,
                CultureInfo.InvariantCulture);
            if (Math.Abs(current) <= double.Epsilon
                && Math.Abs(next) <= double.Epsilon
                && Math.Abs(preForge) <= double.Epsilon) continue;
            result.Add(new ForgeAttributeComparison(
                mapping.EquipType,
                mapping.BattleAttrType,
                GetBattleAttributeLabel(mapping.BattleAttrType),
                preForge,
                current,
                next));
        }
        return result;
    }

    private static string GetBattleAttributeLabel(int battleAttrType)
    {
        forgeBattleAttributeLabels ??= ReadValues(ReadStatic("TableData", "TAttrDict"))
            .Select(row => new
            {
                Id = ReadNullableInt(row, "id") ?? 0,
                Name = Clean(ReadString(row, "name") ?? string.Empty)
            })
            .Where(row => row.Id > 0 && !string.IsNullOrWhiteSpace(row.Name))
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);
        return forgeBattleAttributeLabels.TryGetValue(battleAttrType, out var label)
            ? label
            : $"Attr {battleAttrType}";
    }

    private static string FormatForgeAttributeSummary(
        IReadOnlyList<ForgeAttributeComparison> attributes)
    {
        var rows = attributes
            .Where(attribute => Math.Abs(attribute.Next - attribute.Current) > 0.0000001d)
            .OrderByDescending(attribute =>
                Math.Abs(attribute.Next - attribute.Current)
                / Math.Max(1d, Math.Abs(attribute.Current)))
            .Take(3)
            .Select(attribute =>
                $"{attribute.Label} {attribute.PreForge:0.###}→{attribute.Current:0.###}→{attribute.Next:0.###}")
            .ToList();
        return rows.Count == 0
            ? UiText.L("수치 변화 없음", "no readable stat change", "无可读数值变化", "無可讀數值變化")
            : string.Join(" · ", rows);
    }

    private static (bool Available, int LevelMax, object SeasonData, object House,
        object HouseWork, object WorkForge, object WorkField, string Error) ReadWorkshopState()
    {
        try
        {
            var seasonData = Read(ReadStatic("Game", "dataMgr"), "nowSeasonData")
                             ?? throw new InvalidOperationException("Season data is unavailable.");
            var townData = Read(seasonData, "townData")
                           ?? throw new InvalidOperationException("Town data is unavailable.");
            var workType = CreateEnum("EHouseType", 103)
                           ?? throw new InvalidOperationException("Workshop house type is unavailable.");
            var house = InvokeRequiredInstance(townData, "GetHouse", workType)
                        ?? throw new InvalidOperationException("Workshop is unavailable.");
            if (ReadBool(InvokeRequiredInstance(house, "IsLock")))
                throw new InvalidOperationException(
                    UiText.L("작업장이 아직 잠겨 있습니다.", "The Workshop is locked.", "工坊尚未解锁。", "工坊尚未解鎖。"));
            var attrData = Read(house, "houseAttrData")
                           ?? throw new InvalidOperationException("Workshop AttrData is unavailable.");
            double HouseAttr(int id)
            {
                var type = CreateEnum("EHouseAttrType", id)
                           ?? throw new InvalidOperationException($"Workshop attribute {id} is unavailable.");
                return Convert.ToDouble(
                    InvokeRequiredInstance(attrData, "GetAttrValue", type, null!) ?? 0d,
                    CultureInfo.InvariantCulture);
            }
            if (HouseAttr(10300) <= 0d)
                throw new InvalidOperationException(
                    UiText.L("장비 강화 기능이 아직 열리지 않았습니다.", "Forge is not unlocked.", "装备强化尚未解锁。", "裝備強化尚未解鎖。"));
            var levelMax = Math.Max(0, (int)Math.Floor(HouseAttr(10313)));
            if (levelMax <= 0)
                throw new InvalidOperationException("The native Forge level limit is unavailable.");
            var houseWork = Read(house, "houseWorkData")
                            ?? throw new InvalidOperationException("Workshop runtime data is unavailable.");
            var fields = ReadList(Read(houseWork, "fieldList"));
            if (fields.Count != 1)
                throw new InvalidOperationException(
                    $"Expected one native Workshop item field, found {fields.Count}.");
            var field = fields[0];
            if (Read(field, "itemData") is not null)
                throw new InvalidOperationException(
                    UiText.L("작업대에 다른 아이템이 있어 강화하지 않았습니다.", "The Workshop field is occupied.", "工坊槽位中已有其他物品。", "工坊欄位中已有其他物品。"));
            var forge = Read(houseWork, "workForgeData")
                        ?? throw new InvalidOperationException("WorkForgeData is unavailable.");
            return (true, levelMax, seasonData, house, houseWork, forge, field, string.Empty);
        }
        catch (Exception error)
        {
            return (false, 0, null!, null!, null!, null!, null!, error.GetBaseException().Message);
        }
    }

    private static ForgeApplyOutcome ApplyForgeCandidateCore(
        object hero,
        GearSlot slot,
        object item,
        EquipmentForgeCandidatePreview preview,
        string expectedGearFingerprint,
        string expectedSkillFingerprint)
    {
        var source = GetEquipDestinationField(hero, slot);
        var applied = false;
        var reequipped = false;
        var resourceInsufficient = false;
        var actualLevel = Math.Max(0,
            ReadNullableInt(Read(item, "saveItemData"), "forgeLevel") ?? 0);
        IReadOnlyList<ForgeAttributeComparison> actualAttributes = Array.Empty<ForgeAttributeComparison>();
        var error = string.Empty;
        object? workField = null;
        object? houseWorkData = null;
        try
        {
            if (!string.Equals(GetCurrentGearFingerprint(hero), expectedGearFingerprint, StringComparison.Ordinal)
                || !string.Equals(GetSkillStateFingerprint(hero), expectedSkillFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected hero changed before Forge could be applied.");
            if (source is null || !NativeEquals(Read(source, "itemData"), item)
                               || !IsVerifiedHeroEquipField(hero, item, source))
                throw new InvalidOperationException("The selected item is no longer in its original equipment slot.");
            actualLevel = Math.Max(0,
                ReadNullableInt(Read(item, "saveItemData"), "forgeLevel") ?? 0);
            if (actualLevel != preview.CurrentLevel)
                throw new InvalidOperationException(
                    $"The Forge level changed before application (expected {preview.CurrentLevel}, got {actualLevel}).");
            var levelCount = preview.NextLevel - preview.CurrentLevel;
            if (levelCount is < 1 or > 5)
                throw new InvalidOperationException(
                    $"The native Forge batch must contain 1-5 levels (requested {levelCount}).");

            var workshop = ReadWorkshopState();
            if (!workshop.Available) throw new InvalidOperationException(workshop.Error);
            workField = workshop.WorkField;
            houseWorkData = workshop.HouseWork;
            var validation = ToInt(InvokeRequiredInstance(
                workshop.HouseWork, "CheckAddItem", source, workField));
            if (validation != 0)
                throw new InvalidOperationException(
                    $"The Workshop rejected the equipped item (code {validation}).");

            var moveCode = ToInt(InvokeRequiredStaticMany("ItemSys", "MoveItem", source, workField));
            if (!NativeEquals(Read(workField, "itemData"), item)
                || Read(source, "itemData") is not null)
                throw new InvalidOperationException(
                    $"The item did not enter the Workshop exactly once (move code {moveCode}).");

            InvokeRequiredInstance(workshop.WorkForge, "CreateForgePrice", levelCount);
            InvokeRequiredInstance(workshop.WorkForge, "CreateForgeAttrUpList", levelCount);
            var prices = ReadList(Read(workshop.WorkForge, "forgePriceList"));
            if (prices.Count == 0)
                throw new InvalidOperationException("The native Forge produced no price entries.");
            var unavailable = prices
                .Select(price => ToInt(InvokeRequiredInstance(price, "CheckEnough")))
                .Where(code => code != 0)
                .ToList();
            if (unavailable.Count > 0)
            {
                resourceInsufficient = true;
                throw new InvalidOperationException(
                    UiText.L(
                        $"강화 재료가 부족합니다 (코드 {string.Join(',', unavailable)}).",
                        $"Forge resources are insufficient (code {string.Join(',', unavailable)}).",
                        $"强化材料不足（代码 {string.Join(',', unavailable)}）。",
                        $"強化材料不足（代碼 {string.Join(',', unavailable)}）。"));
            }

            _ = ToInt(InvokeRequiredInstance(workshop.WorkForge, "ForgeEquip", levelCount));
            actualLevel = Math.Max(0,
                ReadNullableInt(Read(item, "saveItemData"), "forgeLevel") ?? 0);
            applied = actualLevel == preview.NextLevel;
            if (!applied)
                throw new InvalidOperationException(
                    $"The native Forge did not reach the predicted level (expected {preview.NextLevel}, got {actualLevel}).");

            var actualEquip = Read(item, "itemEquipData")
                              ?? throw new InvalidOperationException("Forged ItemEquipData is unavailable.");
            actualAttributes = ReadForgeAttributeComparison(actualEquip, actualEquip)
                .Select(actual =>
                {
                    var predicted = preview.Attributes.FirstOrDefault(entry =>
                        entry.EquipmentAttributeType == actual.EquipmentAttributeType);
                    return predicted is null
                        ? actual
                        : actual with { PreForge = predicted.PreForge, Current = predicted.Current };
                }).ToList();
        }
        catch (Exception caught)
        {
            error = caught.GetBaseException().Message;
            actualLevel = Math.Max(0,
                ReadNullableInt(Read(item, "saveItemData"), "forgeLevel") ?? actualLevel);
            applied = actualLevel == preview.NextLevel;
        }
        finally
        {
            if (workField is not null && NativeEquals(Read(workField, "itemData"), item))
            {
                try
                {
                    if (source is not null)
                        _ = InvokeRequiredStaticMany("ItemSys", "MoveItem", workField, source);
                    if (source is null || !IsVerifiedHeroEquipField(hero, item, source))
                    {
                        if (houseWorkData is not null)
                            _ = InvokeRequiredInstance(houseWorkData, "TryReturnItemFromWork", workField);
                    }
                }
                catch (Exception returnError)
                {
                    error = string.IsNullOrWhiteSpace(error)
                        ? returnError.GetBaseException().Message
                        : $"{error}; return failed: {returnError.GetBaseException().Message}";
                }
            }
            reequipped = source is not null && IsVerifiedHeroEquipField(hero, item, source);
            if (!reequipped && string.IsNullOrWhiteSpace(error))
                error = "The exact item was not verified in its original equipment slot.";
        }

        if (applied && actualAttributes.Count > 0)
        {
            var mismatches = preview.Attributes.Where(predicted =>
            {
                var actual = actualAttributes.FirstOrDefault(entry =>
                    entry.EquipmentAttributeType == predicted.EquipmentAttributeType);
                return actual is null
                       || Math.Abs(actual.Next - predicted.Next)
                       > Math.Max(0.001d, Math.Abs(predicted.Next) * 0.00001d);
            }).ToList();
            if (mismatches.Count > 0)
                error = string.IsNullOrWhiteSpace(error)
                    ? $"{mismatches.Count} predicted attribute value(s) did not match the actual Forge result."
                    : $"{error}; {mismatches.Count} predicted attribute value(s) did not match.";
        }
        return new ForgeApplyOutcome(
            applied, reequipped, actualLevel, actualAttributes,
            resourceInsufficient, error);
    }
}
