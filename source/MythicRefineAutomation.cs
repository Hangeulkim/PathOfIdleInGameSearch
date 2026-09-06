using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PathOfIdleInGameSearch;

// Mythic refinement is intentionally separate from both Auto Build and the
// deterministic +1 Forge optimizer. One confirmed activation consumes the
// fixed preflight candidate set in its safe ordering. Every item still receives
// an independent native eligibility/resource/capacity check immediately before
// its irreversible roll.
internal static partial class GameInventoryReader
{
    private sealed record MythicRefineRuntimeState(
        object SeasonData,
        object House,
        object HouseWork,
        object WorkRefine,
        object WorkField,
        double SuccessRatePercent);

    private sealed record MythicRefineCandidate(
        ItemSearchRecord Record,
        MythicRefineCandidatePreview Preview);

    private sealed record MythicVaultCapacity(
        bool CanAccept,
        int StoredCount,
        int MaximumCount,
        string Detail);

    private sealed record MythicRefineApplyOutcome(
        bool Attempted,
        bool Upgraded,
        bool Destroyed,
        bool Uncertain,
        bool StopBatch,
        int NativeResultCode,
        int DisplacedItemsSold,
        StorageSource? FinalSource,
        string Error);

    internal static MythicRefineResult RefineAllEligibleLegendsToMythic(
        string expectedFirstItemKey = "",
        int expectedCandidateCount = 0,
        string expectedCandidateSetToken = "")
        => EvaluateMythicRefineCandidate(
            applyMutation: true, expectedFirstItemKey, expectedCandidateCount,
            expectedCandidateSetToken);

    internal static MythicRefineResult PreviewOneEligibleLegendToMythic()
        => EvaluateMythicRefineCandidate(
            applyMutation: false, expectedFirstItemKey: string.Empty,
            expectedCandidateCount: 0, expectedCandidateSetToken: string.Empty);

#if PATHOFIDLE_RUNTIME_TEST
    internal static string CaptureMythicRefineInventoryState()
    {
        return MainThreadNativeGateway.Invoke(() => string.Join("|",
            ReadAllEquipmentSources(true)
                .Select(record =>
                {
                    var save = Read(record.ItemData, "saveItemData");
                    var definition = Read(Read(record.ItemData, "itemEquipData"), "tEquipData");
                    return string.Join(":",
                        NativeObjectKey(record.ItemData, record.SourceField ?? record.ItemData),
                        record.StorageSource,
                        ReadNullableInt(definition, "id") ?? 0,
                        ReadNullableInt(save, "quality") ?? 0,
                        ReadNullableInt(save, "level") ?? 0,
                        ReadNullableInt(save, "forgeLevel") ?? 0,
                        ReadBool(save is null ? null : Read(save, "isLock")) ? 1 : 0);
                })
                .OrderBy(row => row, StringComparer.Ordinal)));
    }
#endif

    private static MythicRefineResult EvaluateMythicRefineCandidate(
        bool applyMutation,
        string expectedFirstItemKey,
        int expectedCandidateCount,
        string expectedCandidateSetToken)
    {
        MythicRefineCandidate? selected = null;
        try
        {
            var operation = MainThreadNativeGateway.CurrentWorkerOperation
                            ?? throw new InvalidOperationException(
                                "Mythic refinement requires a managed operation.");
            operation.BeginPlanning();

            operation.SetProgress(
                AutoBuildStep.CollectingCandidates,
                0,
                1,
                UiText.L(
                    "Inventory · Warehouse · Vault의 안전한 Mythic 정련 후보 확인",
                    "Checking safe Mythic candidates in Inventory, Warehouse, and Vault",
                    "正在检查背包、仓库和宝库中的安全 Mythic 候选",
                    "正在檢查背包、倉庫與寶庫中的安全 Mythic 候選"));

            var snapshot = MainThreadNativeGateway.Invoke(() =>
            {
                var workshop = ReadMythicRefineState();
                var candidates = SelectMythicRefineCandidates(
                    ReadAllEquipmentSources(includeWarehouse: true),
                    workshop.SuccessRatePercent);
                var confirmedBatchMatches = !applyMutation
                                            || (!string.IsNullOrWhiteSpace(expectedFirstItemKey)
                                                && expectedCandidateCount > 0
                                                && candidates.Count == expectedCandidateCount
                                                && string.Equals(
                                                    candidates[0].Preview.CandidateSetToken,
                                                    expectedCandidateSetToken,
                                                    StringComparison.Ordinal)
                                                && candidates.Count(candidate => string.Equals(
                                                    candidate.Preview.ItemKey,
                                                    expectedFirstItemKey,
                                                    StringComparison.Ordinal)) == 1);
                var needsVaultBridge = candidates.Any(candidate =>
                    candidate.Preview.Source == StorageSource.Treasure);
                var vaultBridgeAvailable = !needsVaultBridge
                                           || FindEmptyEquipmentBagField(
                                               workshop.SeasonData) is not null;
                var capacities = new Dictionary<int, MythicVaultCapacity>();
                MythicRefineCandidate? actionable = null;
                MythicVaultCapacity? actionableCapacity = null;
                var bridgeBlocked = 0;
                var actionableCount = 0;
                foreach (var candidate in candidates)
                {
                    if (candidate.Preview.Source == StorageSource.Treasure
                        && !vaultBridgeAvailable)
                    {
                        bridgeBlocked++;
                        continue;
                    }

                    if (!capacities.TryGetValue(
                            candidate.Preview.MythicDefinitionId,
                            out var capacity))
                    {
                        capacity = ReadMythicVaultCapacity(
                            workshop.SeasonData,
                            candidate.Preview.MythicDefinitionId);
                        capacities[candidate.Preview.MythicDefinitionId] = capacity;
                    }
                    if (!capacity.CanAccept) continue;
                    actionableCount++;
                    if (actionable is not null) continue;
                    actionable = candidate;
                    actionableCapacity = capacity;
                }
                var fullTargetCount = capacities.Values.Count(capacity =>
                    !capacity.CanAccept);
                return (Workshop: workshop, Candidates: candidates, Selected: actionable,
                    SelectedCapacity: actionableCapacity,
                    ConfirmedBatchMatches: confirmedBatchMatches,
                    VaultBridgeAvailable: vaultBridgeAvailable,
                    BridgeBlocked: bridgeBlocked,
                    FullTargetCount: fullTargetCount,
                    ActionableCount: actionableCount);
            });

            if (applyMutation && !snapshot.ConfirmedBatchMatches)
                return MythicRefineFailure(UiText.L(
                    "확인했던 Legendary 후보 목록이 이동·잠금·변경되어 실제 정련을 실행하지 않았습니다. 다시 후보 검사부터 진행하세요.",
                    "The confirmed Legendary candidate set moved, was locked, or changed, so no refinement was attempted. Run the candidate check again.",
                    "已确认的 Legendary 候选列表已移动、锁定或发生变化，因此未执行精炼。请重新检查候选。",
                    "已確認的 Legendary 候選清單已移動、鎖定或發生變更，因此未執行精煉。請重新檢查候選。"));

            selected = snapshot.Selected;
            if (selected is null && snapshot.Candidates.Count == 0)
                return MythicRefineFailure(UiText.L(
                    "정련 가능한 잠금 해제·룬 미장착 Legendary 장비가 없습니다.",
                    "No unlocked, rune-free Legendary equipment can be refined to Mythic.",
                    "没有可精炼为 Mythic 的未锁定、未镶嵌 Legendary 装备。",
                    "沒有可精煉為 Mythic 的未鎖定、未鑲嵌 Legendary 裝備。"));
            if (selected is null)
                return MythicRefineFailure(UiText.L(
                    $"후보 {snapshot.Candidates.Count}개를 찾았지만 지금 안전하게 실행할 수 있는 후보가 없습니다. Vault 이동용 인벤토리 빈칸 부족 {snapshot.BridgeBlocked}개 · 가득 찼거나 확인 불가한 Mythic Vault 대상 {snapshot.FullTargetCount}개.",
                    $"Found {snapshot.Candidates.Count} eligible item(s), but none can be staged safely now. {snapshot.BridgeBlocked} Vault item(s) are blocked by a full equipment bag; {snapshot.FullTargetCount} Mythic Vault target group(s) are full or unverifiable.",
                    $"找到 {snapshot.Candidates.Count} 件候选，但目前没有可安全执行的候选。{snapshot.BridgeBlocked} 件宝库物品因装备背包已满而受阻；{snapshot.FullTargetCount} 个 Mythic 宝库目标组已满或无法确认。",
                    $"找到 {snapshot.Candidates.Count} 件候選，但目前沒有可安全執行的候選。{snapshot.BridgeBlocked} 件寶庫物品因裝備背包已滿而受阻；{snapshot.FullTargetCount} 個 Mythic 寶庫目標群組已滿或無法確認。"));

            var vaultCapacity = snapshot.SelectedCapacity
                                ?? throw new InvalidOperationException(
                                    "The selected Mythic candidate has no verified Vault capacity.");
            Plugin.DiagInfo(
                $"MYTHIC CANDIDATE FILTER|eligible={snapshot.Candidates.Count}|actionable={snapshot.ActionableCount}|selectedSource={selected.Preview.Source}|bridgeAvailable={snapshot.VaultBridgeAvailable}|bridgeBlocked={snapshot.BridgeBlocked}|fullTargets={snapshot.FullTargetCount}");
            Plugin.DiagInfo(
                $"MYTHIC VAULT CAPACITY|phase=preview|target={selected.Preview.MythicDefinitionId}|stored={vaultCapacity.StoredCount}|maximum={vaultCapacity.MaximumCount}|accept={vaultCapacity.CanAccept}|{vaultCapacity.Detail}");

            operation.SetProgress(
                AutoBuildStep.FinalRefinement,
                snapshot.ActionableCount,
                snapshot.Candidates.Count,
                UiText.L(
                    $"일괄 후보 확인: 전체 {snapshot.Candidates.Count}개 · 현재 실행 가능 {snapshot.ActionableCount}개",
                    $"Batch checked: {snapshot.Candidates.Count} total · {snapshot.ActionableCount} currently actionable",
                    $"批量候选已检查：共 {snapshot.Candidates.Count} 件 · 当前可执行 {snapshot.ActionableCount} 件",
                    $"批次候選已檢查：共 {snapshot.Candidates.Count} 件 · 目前可執行 {snapshot.ActionableCount} 件"));

            if (!applyMutation)
            {
                return new MythicRefineResult(
                    true, false, false, false, false, false,
                    UiText.L(
                        $"읽기 전용 후보: {selected.Preview.ItemName} · {selected.Preview.StorageLabel} · 같은 원형 {selected.Preview.DuplicateCount}개 · 성공률 {selected.Preview.SuccessRatePercent:0.##}% · 전체 후보 {selected.Preview.EligibleCount}개",
                        $"Read-only candidate: {selected.Preview.ItemName} · {selected.Preview.StorageLabel} · {selected.Preview.DuplicateCount} same-base copies · {selected.Preview.SuccessRatePercent:0.##}% success · {selected.Preview.EligibleCount} eligible total",
                        $"只读候选：{selected.Preview.ItemName} · {selected.Preview.StorageLabel} · 同基础 {selected.Preview.DuplicateCount} 件 · 成功率 {selected.Preview.SuccessRatePercent:0.##}% · 共 {selected.Preview.EligibleCount} 件候选",
                        $"唯讀候選：{selected.Preview.ItemName} · {selected.Preview.StorageLabel} · 同基礎 {selected.Preview.DuplicateCount} 件 · 成功率 {selected.Preview.SuccessRatePercent:0.##}% · 共 {selected.Preview.EligibleCount} 件候選"),
                    selected.Preview);
            }

            operation.ThrowIfPlanningStopped();
            BeginMythicRefineMutation();
            var attemptCount = 0;
            var successCount = 0;
            var failureCount = 0;
            var skippedCount = 0;
            var displacedItemsSold = 0;
            var processedCount = 0;
            var uncertain = false;
            var stoppedEarly = false;
            var stopReason = string.Empty;
            var batchNotes = new List<string>();
            var totalCandidates = snapshot.Candidates.Count;

            foreach (var candidate in snapshot.Candidates)
            {
                operation.SetProgress(
                    AutoBuildStep.ApplyingGear,
                    processedCount,
                    totalCandidates,
                    UiText.L(
                        $"Mythic 일괄 정련 {processedCount + 1}/{totalCandidates} · 시도 {attemptCount} · 성공 {successCount} · 실패(파괴) {failureCount} · 건너뜀 {skippedCount}",
                        $"Mythic batch {processedCount + 1}/{totalCandidates} · attempted {attemptCount} · succeeded {successCount} · failed (destroyed) {failureCount} · skipped {skippedCount}",
                        $"Mythic 批量精炼 {processedCount + 1}/{totalCandidates} · 尝试 {attemptCount} · 成功 {successCount} · 失败（销毁）{failureCount} · 跳过 {skippedCount}",
                        $"Mythic 批次精煉 {processedCount + 1}/{totalCandidates} · 嘗試 {attemptCount} · 成功 {successCount} · 失敗（銷毀）{failureCount} · 略過 {skippedCount}"));

                // The candidate list is fixed by the confirmed preflight, but
                // each object is independently revalidated against live native
                // storage, Workshop, resource, and Vault state here. The loop
                // never admits a newly appeared item and is therefore finite.
                var outcome = MainThreadNativeGateway.Invoke(() =>
                    ApplyMythicRefineCandidateCore(candidate, snapshot.Workshop));
                processedCount++;
                displacedItemsSold += Math.Max(0, outcome.DisplacedItemsSold);
                if (outcome.Attempted) attemptCount++;
                else skippedCount++;
                if (outcome.Upgraded) successCount++;
                if (outcome.Destroyed) failureCount++;
                uncertain |= outcome.Uncertain;
                if (!string.IsNullOrWhiteSpace(outcome.Error)
                    && batchNotes.Count < 3
                    && !batchNotes.Contains(outcome.Error, StringComparer.Ordinal))
                    batchNotes.Add(outcome.Error);

                Plugin.DiagInfo(
                    $"MYTHIC BATCH OUTCOME|index={processedCount}|total={totalCandidates}"
                    + $"|item={candidate.Preview.DefinitionId}|source={candidate.Preview.Source}"
                    + $"|attempted={outcome.Attempted}|upgraded={outcome.Upgraded}"
                    + $"|destroyed={outcome.Destroyed}|uncertain={outcome.Uncertain}"
                    + $"|stop={outcome.StopBatch}|native={outcome.NativeResultCode}"
                    + $"|final={outcome.FinalSource?.ToString() ?? string.Empty}"
                    + $"|sold={outcome.DisplacedItemsSold}|error={outcome.Error.Replace('|', '/')}");

                operation.SetProgress(
                    AutoBuildStep.ApplyingGear,
                    processedCount,
                    totalCandidates,
                    UiText.L(
                        $"Mythic 일괄 정련 {processedCount}/{totalCandidates} · 시도 {attemptCount} · 성공 {successCount} · 실패(파괴) {failureCount} · 건너뜀 {skippedCount}",
                        $"Mythic batch {processedCount}/{totalCandidates} · attempted {attemptCount} · succeeded {successCount} · failed (destroyed) {failureCount} · skipped {skippedCount}",
                        $"Mythic 批量精炼 {processedCount}/{totalCandidates} · 尝试 {attemptCount} · 成功 {successCount} · 失败（销毁）{failureCount} · 跳过 {skippedCount}",
                        $"Mythic 批次精煉 {processedCount}/{totalCandidates} · 嘗試 {attemptCount} · 成功 {successCount} · 失敗（銷毀）{failureCount} · 略過 {skippedCount}"));

                // An unverified native result can hide either a live item or a
                // destroyed item, so continuing would compound an unknown
                // state. A global Workshop/resource block also makes every
                // remaining candidate impossible in this activation.
                if (!outcome.Uncertain && !outcome.StopBatch) continue;
                stoppedEarly = true;
                stopReason = outcome.Error;
                skippedCount += Math.Max(0, totalCandidates - processedCount);
                break;
            }

            var displaced = displacedItemsSold > 0
                ? UiText.L(
                    $" · Vault 교체로 밀려난 장비 {displacedItemsSold}개 즉시 판매",
                    $" · sold {displacedItemsSold} item(s) displaced by Vault upgrades",
                    $" · 已立即出售 {displacedItemsSold} 件因宝库替换而移出的装备",
                    $" · 已立即出售 {displacedItemsSold} 件因寶庫替換而移出的裝備")
                : string.Empty;
            var earlyStop = stoppedEarly
                ? UiText.L(
                    $" · 안전 중단: {stopReason}",
                    $" · stopped safely: {stopReason}",
                    $" · 已安全停止：{stopReason}",
                    $" · 已安全停止：{stopReason}")
                : string.Empty;
            var batchDetail = batchNotes.Count > 0
                ? UiText.L(
                    $" · 상세: {string.Join(" / ", batchNotes)}",
                    $" · details: {string.Join(" / ", batchNotes)}",
                    $" · 详情：{string.Join(" / ", batchNotes)}",
                    $" · 詳情：{string.Join(" / ", batchNotes)}")
                : string.Empty;
            var verified = !uncertain && (attemptCount > 0 || !stoppedEarly);
            return new MythicRefineResult(
                verified, false, attemptCount > 0, successCount > 0,
                failureCount > 0, uncertain,
                UiText.L(
                    $"Mythic 일괄 정련 완료: 전체 {totalCandidates}개 · 시도 {attemptCount}회 · 성공 {successCount}회 · 실패(파괴) {failureCount}회 · 건너뜀 {skippedCount}개{displaced}{earlyStop}{batchDetail}",
                    $"Mythic batch complete: {totalCandidates} total · {attemptCount} attempted · {successCount} succeeded · {failureCount} failed (destroyed) · {skippedCount} skipped{displaced}{earlyStop}{batchDetail}",
                    $"Mythic 批量精炼完成：共 {totalCandidates} 件 · 尝试 {attemptCount} 次 · 成功 {successCount} 次 · 失败（摧毁）{failureCount} 次 · 跳过 {skippedCount} 件{displaced}{earlyStop}{batchDetail}",
                    $"Mythic 批次精煉完成：共 {totalCandidates} 件 · 嘗試 {attemptCount} 次 · 成功 {successCount} 次 · 失敗（摧毀）{failureCount} 次 · 略過 {skippedCount} 件{displaced}{earlyStop}{batchDetail}"),
                selected.Preview,
                attemptCount,
                successCount,
                failureCount,
                skippedCount);
        }
        catch (OperationCanceledException)
        {
            return new MythicRefineResult(
                false, true, false, false, false, false,
                UiText.L(
                    "Mythic 정련 후보 계산을 취소했습니다. 아이템과 자원은 변경하지 않았습니다.",
                    "Mythic candidate planning was cancelled. No item or resource was changed.",
                    "已取消 Mythic 候选计算。物品和资源均未更改。",
                    "已取消 Mythic 候選計算。物品與資源均未變更。"),
                selected?.Preview);
        }
        catch (Exception error)
        {
            var operation = MainThreadNativeGateway.CurrentWorkerOperation;
            return new MythicRefineResult(
                false, false, operation?.MutationStarted == true, false, false,
                operation?.MutationStarted == true,
                UiText.L(
                    $"Mythic 정련 실패: {error.GetBaseException().Message}",
                    $"Mythic refinement failed: {error.GetBaseException().Message}",
                    $"Mythic 精炼失败：{error.GetBaseException().Message}",
                    $"Mythic 精煉失敗：{error.GetBaseException().Message}"),
                selected?.Preview);
        }
    }

    private static void BeginMythicRefineMutation()
    {
        var operation = MainThreadNativeGateway.CurrentWorkerOperation
                        ?? throw new InvalidOperationException(
                            "Mythic refinement requires a managed operation.");
        operation.ThrowIfPlanningStopped();
        MainThreadNativeGateway.Invoke(() =>
        {
            if (!operation.TryBeginMutation())
                throw new OperationCanceledException(
                    "Mythic refinement was cancelled before mutation.");
            MainThreadNativeGateway.EnterExclusiveMutation(operation);
        });
    }

    private static List<MythicRefineCandidate> SelectMythicRefineCandidates(
        IReadOnlyCollection<ItemSearchRecord> records,
        double successRatePercent)
    {
        var eligible = records
            .Where(record => record.StorageSource != StorageSource.Equipped)
            .Select(record =>
            {
                var item = record.ItemData;
                var save = Read(item, "saveItemData");
                var equip = Read(item, "itemEquipData");
                var definition = Read(equip, "tEquipData");
                var definitionId = ReadNullableInt(definition, "id") ?? 0;
                var mythicDefinitionId = ReadNullableInt(definition, "upMyth") ?? 0;
                var setId = ReadNullableInt(definition, "setsId") ?? 0;
                var hasRune = ReadList(Read(equip, "slotRuneList")).Count > 0
                              || ReadList(Read(save, "slotRuneList")).Count > 0
                              || ReadBool(Read(save, "isInlay"));
                return new
                {
                    Record = record,
                    Save = save,
                    DefinitionId = definitionId,
                    MythicDefinitionId = mythicDefinitionId,
                    SetId = setId,
                    HasRune = hasRune
                };
            })
            .Where(entry => entry.Record.Quality == 4
                            && entry.DefinitionId > 0
                            && entry.MythicDefinitionId > 0
                            && entry.MythicDefinitionId != entry.DefinitionId
                            && entry.SetId <= 0
                            && !entry.HasRune
                            && !ReadBool(Read(entry.Save, "isLock")))
            .ToList();
        var duplicateCounts = eligible
            .GroupBy(entry => entry.DefinitionId)
            .ToDictionary(group => group.Key, group => group.Count());
        var total = eligible.Count;
        var candidates = eligible.Select(entry =>
            {
                var save = entry.Save;
                var preview = new MythicRefineCandidatePreview(
                    NativeObjectKey(entry.Record.ItemData,
                        entry.Record.SourceField ?? entry.Record.ItemData),
                    entry.Record.Name,
                    entry.Record.StorageLabel,
                    entry.Record.StorageSource,
                    entry.DefinitionId,
                    entry.MythicDefinitionId,
                    Math.Max(0, ReadNullableInt(save, "level") ?? entry.Record.Level ?? 0),
                    Math.Max(0, ReadNullableInt(save, "forgeLevel") ?? 0),
                    Math.Max(0, ReadNullableInt(save, "mainAttrValue") ?? 0),
                    duplicateCounts[entry.DefinitionId],
                    total,
                    successRatePercent);
                return new MythicRefineCandidate(entry.Record, preview);
            })
            // Preserve unique or invested equipment when a lower-risk copy exists.
            .OrderByDescending(candidate => candidate.Preview.DuplicateCount > 1)
            .ThenByDescending(candidate => candidate.Preview.DuplicateCount)
            .ThenBy(candidate => candidate.Preview.ForgeLevel)
            .ThenBy(candidate => candidate.Preview.Level)
            .ThenBy(candidate => candidate.Preview.MainAttributeValue)
            .ThenBy(candidate => candidate.Preview.Source switch
            {
                StorageSource.Inventory => 0,
                StorageSource.Warehouse => 1,
                StorageSource.Treasure => 2,
                _ => 3
            })
            .ThenBy(candidate => candidate.Preview.ItemKey, StringComparer.Ordinal)
            .ToList();
        var candidateSetToken = BuildMythicCandidateSetToken(candidates);
        return candidates
            .Select(candidate => candidate with
            {
                Preview = candidate.Preview with
                {
                    CandidateSetToken = candidateSetToken
                }
            })
            .ToList();
    }

    private static string BuildMythicCandidateSetToken(
        IReadOnlyCollection<MythicRefineCandidate> candidates)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        var rows = candidates
            .Select(candidate => string.Join(":",
                candidate.Preview.ItemKey,
                (int)candidate.Preview.Source,
                candidate.Preview.DefinitionId,
                candidate.Preview.MythicDefinitionId,
                candidate.Preview.Level,
                candidate.Preview.ForgeLevel,
                candidate.Preview.MainAttributeValue))
            .OrderBy(row => row, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var character in row)
            {
                hash ^= character;
                hash *= prime;
            }
            hash ^= '\n';
            hash *= prime;
        }

        return $"{candidates.Count.ToString(CultureInfo.InvariantCulture)}-{hash:x16}";
    }

    private static MythicRefineRuntimeState ReadMythicRefineState()
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
            throw new InvalidOperationException(UiText.L(
                "작업장이 아직 잠겨 있습니다.",
                "The Workshop is locked.",
                "工坊尚未解锁。",
                "工坊尚未解鎖。"));
        var attrData = Read(house, "houseAttrData")
                       ?? throw new InvalidOperationException("Workshop attributes are unavailable.");

        double HouseAttr(int id)
        {
            var type = CreateEnum("EHouseAttrType", id)
                       ?? throw new InvalidOperationException(
                           $"Workshop attribute {id} is unavailable.");
            return Convert.ToDouble(
                InvokeRequiredInstance(attrData, "GetAttrValue", type, null!) ?? 0d,
                CultureInfo.InvariantCulture);
        }

        if (HouseAttr(10302) <= 0d || HouseAttr(10340) <= 0d
            || !ReadBool(InvokeRequiredStaticMany("EquipSys", "IsMythEquipOpen")))
            throw new InvalidOperationException(UiText.L(
                "Mythic 장비 정련 기능이 아직 열리지 않았습니다.",
                "Mythic equipment refinement is not unlocked.",
                "Mythic 装备精炼尚未解锁。",
                "Mythic 裝備精煉尚未解鎖。"));
        var rawRate = HouseAttr(10341);
        var successRatePercent = Math.Clamp(rawRate <= 1d ? rawRate * 100d : rawRate, 0d, 100d);
        if (successRatePercent <= 0d)
            throw new InvalidOperationException("The native Mythic refinement rate is unavailable.");

        var houseWork = Read(house, "houseWorkData")
                        ?? throw new InvalidOperationException("Workshop runtime data is unavailable.");
        var fields = ReadList(Read(houseWork, "fieldList"));
        if (fields.Count != 1)
            throw new InvalidOperationException(
                $"Expected one native Workshop item field, found {fields.Count}.");
        var workField = fields[0];
        if (Read(workField, "itemData") is not null)
            throw new InvalidOperationException(UiText.L(
                "작업대에 다른 아이템이 있어 정련하지 않았습니다.",
                "The Workshop field is occupied, so nothing was refined.",
                "工坊槽位中已有其他物品，因此未精炼。",
                "工坊欄位中已有其他物品，因此未精煉。"));
        var workRefine = Read(houseWork, "workRefineData")
                         ?? throw new InvalidOperationException("WorkRefineData is unavailable.");
        return new MythicRefineRuntimeState(
            seasonData, house, houseWork, workRefine, workField,
            successRatePercent);
    }

    private static int ReadNativeMythicEquipmentQuality()
    {
        var enumType = GameType("EItemQualityType")
                       ?? throw new InvalidOperationException(
                           "The native equipment-quality enum is unavailable.");
        var name = Enum.GetNames(enumType).FirstOrDefault(candidate =>
            candidate.Contains("myth", StringComparison.OrdinalIgnoreCase));
        if (name is null)
            throw new InvalidOperationException(
                "The native Mythic equipment-quality value is unavailable.");
        return Convert.ToInt32(Enum.Parse(enumType, name), CultureInfo.InvariantCulture);
    }

    private static bool IsExpectedVaultGroup(object? group, int definitionId, int quality)
    {
        if (group is null) return false;
        var save = Read(group, "saveEquipGroupData");
        return (ReadNullableInt(save, "id") ?? 0) == definitionId
               && (ReadNullableInt(save, "quality") ?? 0) == quality;
    }

    private static object? FindExpectedVaultGroup(
        object treasure,
        int definitionId,
        int quality)
    {
        // Resolve through the native index first, then validate its identity.
        // Enumeration is a compatibility fallback for game versions that build
        // the lookup lazily; neither path creates or changes a group.
        try
        {
            var direct = InvokeRequiredInstance(
                treasure, "GetEquipGroupData", definitionId, quality);
            if (IsExpectedVaultGroup(direct, definitionId, quality)) return direct;
        }
        catch
        {
            // The read-only dictionary fallback below remains authoritative.
        }

        foreach (var groupList in ReadValues(Read(treasure, "equipGroupDic")))
        foreach (var group in ReadList(groupList))
            if (IsExpectedVaultGroup(group, definitionId, quality))
                return group;
        return null;
    }

    private static MythicVaultCapacity ReadMythicVaultCapacity(
        object seasonData,
        int mythicDefinitionId)
    {
        try
        {
            if (mythicDefinitionId <= 0)
                return new MythicVaultCapacity(false, 0, 0,
                    "invalid Mythic target definition");
            var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
                .Select(house => Read(house, "houseStoreData"))
                .FirstOrDefault(store => Read(store, "storeTreaData") is not null);
            var treasure = Read(houseStoreData, "storeTreaData");
            if (treasure is null)
                return new MythicVaultCapacity(false, 0, 0,
                    "Vault runtime data is unavailable");

            var quality = ReadNativeMythicEquipmentQuality();
            if (!ReadBool(InvokeRequiredInstance(treasure, "IsQualityUnlocked", quality)))
                return new MythicVaultCapacity(false, 0, 0,
                    "the native Mythic Vault quality is locked");

            var maximum = Math.Max(0,
                ToInt(InvokeRequiredInstance(treasure, "GetMaxCount")));
            var group = FindExpectedVaultGroup(treasure, mythicDefinitionId, quality);
            if (group is null)
                return new MythicVaultCapacity(true, 0, maximum,
                    $"target={mythicDefinitionId}, stored=0/{maximum}");

            var stored = ReadList(Read(group, "equipList")).Count;
            var full = ReadBool(InvokeRequiredInstance(treasure, "IsGroupFull", group));
            return new MythicVaultCapacity(!full, stored, maximum,
                $"target={mythicDefinitionId}, stored={stored}/{maximum}, nativeFull={full}");
        }
        catch (Exception error)
        {
            // Capacity is a destructive-operation prerequisite. An unreadable
            // state must fail closed rather than risk displacing another item.
            return new MythicVaultCapacity(false, 0, 0,
                $"Vault capacity could not be verified: {error.GetBaseException().Message}");
        }
    }

    private static MythicRefineApplyOutcome ApplyMythicRefineCandidateCore(
        MythicRefineCandidate candidate,
        MythicRefineRuntimeState expectedWorkshop)
    {
        var item = candidate.Record.ItemData;
        var preview = candidate.Preview;
        var attempted = false;
        var upgraded = false;
        var destroyed = false;
        var uncertain = false;
        var stopBatch = false;
        var nativeCode = int.MinValue;
        var displacedItemsSold = 0;
        StorageSource? finalSource = null;
        var errors = new List<string>();
        object? stagingField = null;
        object? outputItem = null;
        var extractedFromVault = false;
        var enteredWorkshop = false;

        try
        {
            MythicRefineRuntimeState workshop;
            try
            {
                workshop = ReadMythicRefineState();
            }
            catch
            {
                stopBatch = true;
                throw;
            }
            if (!NativeEquals(workshop.House, expectedWorkshop.House)
                || !NativeEquals(workshop.WorkField, expectedWorkshop.WorkField))
            {
                stopBatch = true;
                throw new InvalidOperationException(
                    "The Workshop changed after Mythic candidate selection.");
            }
            var vaultCapacity = ReadMythicVaultCapacity(
                workshop.SeasonData, preview.MythicDefinitionId);
            if (!vaultCapacity.CanAccept)
                throw new InvalidOperationException(
                    $"The Mythic target Vault group has no verified free capacity; "
                    + $"nothing was refined ({vaultCapacity.Detail}).");
            if (!TryFindEquipmentStorageLocationCore(
                    item, workshop.SeasonData, out var actualSource,
                    out var actualField, out var actualGroup)
                || actualSource != preview.Source)
                throw new InvalidOperationException(
                    "The selected Legendary is no longer in its original storage source.");
            if (actualSource is StorageSource.Inventory or StorageSource.Warehouse)
            {
                if (actualField is null
                    || candidate.Record.SourceField is null
                    || !NativeEquals(actualField, candidate.Record.SourceField)
                    || !NativeEquals(Read(actualField, "itemData"), item))
                    throw new InvalidOperationException(
                        "The selected Legendary is no longer in its exact original field.");
                stagingField = actualField;
            }
            else if (actualSource == StorageSource.Treasure)
            {
                if (actualGroup is null
                    || candidate.Record.GroupData is null
                    || !NativeEquals(actualGroup, candidate.Record.GroupData))
                    throw new InvalidOperationException(
                        "The selected Legendary is no longer in its exact Vault group.");
                var moveJournal = new List<MoveReceipt>();
                if (!TryMoveCandidateToBag(candidate.Record, workshop.SeasonData, moveJournal))
                    throw new InvalidOperationException(
                        "The selected Vault item could not be moved to an empty equipment bag field.");
                stagingField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item);
                extractedFromVault = true;
            }
            else
            {
                throw new InvalidOperationException("Equipped items cannot be Mythic-refined automatically.");
            }

            if (stagingField is null || !NativeEquals(Read(stagingField, "itemData"), item))
                throw new InvalidOperationException("The exact refinement staging field could not be verified.");
            var validation = ToInt(InvokeRequiredInstance(
                workshop.HouseWork, "CheckAddItem", stagingField, workshop.WorkField));
            if (validation != 0)
                throw new InvalidOperationException(
                    $"The Workshop rejected the selected Legendary (code {validation}).");
            var moveCode = ToInt(InvokeRequiredStaticMany(
                "ItemSys", "MoveItem", stagingField, workshop.WorkField));
            if (!NativeEquals(Read(workshop.WorkField, "itemData"), item)
                || Read(stagingField, "itemData") is not null)
                throw new InvalidOperationException(
                    $"The selected Legendary did not enter the Workshop exactly once (code {moveCode}).");
            enteredWorkshop = true;

            // Moving an item directly through ItemSys does not run the Workshop
            // screen's refresh callback. Rebuild WorkRefineData before consulting
            // its cached quality predicates; otherwise CheckEquipCanRefine can
            // return success while IsEquipHasLegend still describes the previous
            // empty field.
            InvokeRequiredInstance(workshop.WorkRefine, "Init");
            var nativeEligibility = ToInt(InvokeRequiredInstance(
                workshop.WorkRefine, "CheckEquipCanRefine"));
            var hasLegend = ReadBool(InvokeRequiredInstance(
                workshop.WorkRefine, "IsEquipHasLegend"));
            var hasMyth = ReadBool(InvokeRequiredInstance(
                workshop.WorkRefine, "IsEquipHasMyth"));
            var hasSets = ReadBool(InvokeRequiredInstance(
                workshop.WorkRefine, "IsEquipHasSets"));
            Plugin.DiagInfo(
                $"MYTHIC NATIVE CONTRACT|item={preview.DefinitionId}|target={preview.MythicDefinitionId}|code={nativeEligibility}|legend={hasLegend}|myth={hasMyth}|sets={hasSets}");
            // CheckEquipCanRefine is the native authorization contract. The
            // three IsEquipHas* helpers back the Workshop UI badges and have
            // been observed lagging even after Init() while the authoritative
            // check returns success. Candidate selection has already proven an
            // unlocked, rune-free, non-set Legendary with a distinct upMyth
            // target, so keep those helpers diagnostic-only.
            if (nativeEligibility != 0)
                throw new InvalidOperationException(
                    $"The native refinement contract rejected this Legendary "
                    + $"(code {nativeEligibility}, legend={hasLegend}, myth={hasMyth}, sets={hasSets}).");

            InvokeRequiredInstance(workshop.WorkRefine, "CreateRefinePrice");
            var prices = ReadList(Read(workshop.WorkRefine, "refinePriceList"));
            if (prices.Count == 0)
            {
                stopBatch = true;
                throw new InvalidOperationException("The native refinement produced no price entries.");
            }
            var unavailable = prices
                .Select(price => ToInt(InvokeRequiredInstance(price, "CheckEnough")))
                .Where(code => code != 0)
                .ToList();
            if (unavailable.Count > 0)
            {
                stopBatch = true;
                throw new InvalidOperationException(UiText.L(
                    $"Mythic 정련 재료가 부족합니다 (코드 {string.Join(',', unavailable)}).",
                    $"Mythic refinement resources are insufficient (code {string.Join(',', unavailable)}).",
                    $"Mythic 精炼材料不足（代码 {string.Join(',', unavailable)}）。",
                    $"Mythic 精煉材料不足（代碼 {string.Join(',', unavailable)}）。"));
            }

            // Re-read the native group immediately before the irreversible
            // roll. If anything changed since planning/staging, restore the
            // Legendary through the existing finally path without consuming it.
            vaultCapacity = ReadMythicVaultCapacity(
                workshop.SeasonData, preview.MythicDefinitionId);
            if (!vaultCapacity.CanAccept)
                throw new InvalidOperationException(
                    $"The Mythic target Vault group became full before refinement; "
                    + $"nothing was refined ({vaultCapacity.Detail}).");

            attempted = true;
            nativeCode = ToInt(InvokeRequiredInstance(workshop.WorkRefine, "RefineEquip"));
            outputItem = Read(workshop.WorkField, "itemData");
            if (outputItem is not null)
            {
                var outputDefinition = Read(Read(outputItem, "itemEquipData"), "tEquipData");
                upgraded = (ReadNullableInt(outputDefinition, "id") ?? 0)
                           == preview.MythicDefinitionId;
                if (!upgraded)
                {
                    uncertain = true;
                    errors.Add($"native result {nativeCode} left a non-Mythic item in the Workshop");
                }
            }
            else
            {
                // Refinement normally leaves success in the work field and
                // removes a failed item. Check the exact original object before
                // classifying an empty field as destruction.
                if (TryFindEquipmentStorageLocationCore(
                        item, workshop.SeasonData, out var returnedSource,
                        out _, out _))
                {
                    finalSource = returnedSource;
                    var returnedDefinition = Read(Read(item, "itemEquipData"), "tEquipData");
                    upgraded = (ReadNullableInt(returnedDefinition, "id") ?? 0)
                               == preview.MythicDefinitionId;
                    if (!upgraded)
                    {
                        uncertain = true;
                        errors.Add($"native result {nativeCode} returned the original non-Mythic item");
                    }
                }
                else
                {
                    destroyed = true;
                }
            }
        }
        catch (Exception error)
        {
            errors.Add(error.GetBaseException().Message);
            uncertain |= attempted;
            // Never treat a pre-existing/foreign Workshop item as this
            // candidate's output. It is safe to recover from the work field
            // only after the exact candidate was proven to have entered it.
            outputItem = enteredWorkshop
                ? Read(expectedWorkshop.WorkField, "itemData")
                : null;
            if (attempted && outputItem is null
                && !TryFindEquipmentStorageLocationCore(
                    item, expectedWorkshop.SeasonData, out _, out _, out _))
                destroyed = true;
        }
        finally
        {
            if (enteredWorkshop)
                outputItem ??= Read(expectedWorkshop.WorkField, "itemData");
            if (outputItem is null && extractedFromVault && stagingField is not null)
            {
                // A preflight rejection can happen after the exact Vault item
                // was extracted but before it entered the work field. Treat the
                // bridge field as an authoritative preservation path as well.
                outputItem = Read(stagingField, "itemData");
                if (outputItem is not null)
                {
                    var bridgeDefinition = Read(Read(outputItem, "itemEquipData"), "tEquipData");
                    upgraded = (ReadNullableInt(bridgeDefinition, "id") ?? 0)
                               == preview.MythicDefinitionId;
                    destroyed = false;
                }
            }
            if (outputItem is not null)
            {
                try
                {
                    if (extractedFromVault)
                    {
                        if (stagingField is null)
                            throw new InvalidOperationException(
                                "The Vault bridge bag field is unavailable.");
                        var workItem = Read(expectedWorkshop.WorkField, "itemData");
                        var bridgeItem = Read(stagingField, "itemData");
                        if (NativeEquals(workItem, outputItem))
                        {
                            if (bridgeItem is not null)
                                throw new InvalidOperationException(
                                    "The Vault bridge bag field is no longer empty.");
                            _ = InvokeRequiredStaticMany(
                                "ItemSys", "MoveItem", expectedWorkshop.WorkField, stagingField);
                        }
                        if (!NativeEquals(Read(stagingField, "itemData"), outputItem)
                            || Read(expectedWorkshop.WorkField, "itemData") is not null)
                            throw new InvalidOperationException(
                                "The refined item could not leave the Workshop safely.");

                        if (upgraded)
                        {
                            var routed = RouteRefinedVaultItem(
                                outputItem, stagingField, expectedWorkshop.SeasonData);
                            finalSource = routed.FinalSource;
                            displacedItemsSold = routed.DisplacedItemsSold;
                            if (!string.IsNullOrWhiteSpace(routed.Error))
                            {
                                errors.Add(routed.Error);
                                uncertain |= routed.Uncertain;
                            }
                        }
                        else
                        {
                            InvokeRequiredStaticMany(
                                "ItemSys", "QuickMoveItemFromBagToStore", outputItem);
                            if (TryFindEquipmentStorageLocationCore(
                                    outputItem, expectedWorkshop.SeasonData,
                                    out var restoredSource, out _, out _))
                                finalSource = restoredSource;
                            else
                                throw new InvalidOperationException(
                                    "The unrefined Legendary could not be located after Vault restoration.");
                        }
                    }
                    else if (candidate.Record.SourceField is { } originalField
                             && Read(originalField, "itemData") is null)
                    {
                        _ = InvokeRequiredStaticMany(
                            "ItemSys", "MoveItem", expectedWorkshop.WorkField, originalField);
                        if (!NativeEquals(Read(originalField, "itemData"), outputItem)
                            || Read(expectedWorkshop.WorkField, "itemData") is not null)
                            throw new InvalidOperationException(
                                "The refined item was not restored to its exact original field.");
                        finalSource = candidate.Record.StorageSource;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "The original equipment field was occupied before the refined item could be returned.");
                    }
                }
                catch (Exception returnError)
                {
                    errors.Add($"item return failed: {returnError.GetBaseException().Message}");
                    uncertain = true;
                    try
                    {
                        if (Read(expectedWorkshop.WorkField, "itemData") is not null)
                            _ = InvokeRequiredInstance(
                                expectedWorkshop.HouseWork,
                                "TryReturnItemFromWork",
                                expectedWorkshop.WorkField);
                    }
                    catch (Exception fallbackError)
                    {
                        errors.Add($"native return fallback failed: {fallbackError.GetBaseException().Message}");
                    }
                }
            }
            if (Read(expectedWorkshop.WorkField, "itemData") is not null)
            {
                uncertain = true;
                errors.Add("the Workshop field is still occupied; the item was not discarded");
            }
        }

        return new MythicRefineApplyOutcome(
            attempted, upgraded, destroyed, uncertain, stopBatch, nativeCode,
            displacedItemsSold, finalSource,
            string.Join("; ", errors.Where(error => !string.IsNullOrWhiteSpace(error))));
    }

    private static (StorageSource? FinalSource, int DisplacedItemsSold,
        bool Uncertain, string Error) RouteRefinedVaultItem(
        object refinedItem,
        object bagField,
        object seasonData)
    {
        var warehouseBefore = ReadOrdinaryWarehouseEquipment()
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var bagBefore = ReadInventoryEquipment()
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        Exception? moveError = null;
        try
        {
            InvokeRequiredStaticMany("ItemSys", "QuickMoveItemFromBagToStore", refinedItem);
        }
        catch (Exception error)
        {
            moveError = error.GetBaseException();
        }

        if (!TryFindEquipmentStorageLocationCore(
                refinedItem, seasonData, out var finalSource,
                out _, out _))
            return (null, 0, true,
                moveError?.Message ?? "the refined Mythic item could not be located after storage routing");
        if (finalSource != StorageSource.Treasure)
            return (finalSource, 0, moveError is not null,
                UiText.L(
                    "Mythic 장비는 보존됐지만 Vault가 아닌 Inventory/Warehouse에 남았습니다.",
                    "The Mythic item was preserved but remained in Inventory/Warehouse instead of Vault.",
                    "Mythic 装备已保留，但仍在背包/仓库而非宝库中。",
                    "Mythic 裝備已保留，但仍在背包/倉庫而非寶庫中。"));

        var displaced = ReadOrdinaryWarehouseEquipment()
            .Where(entry => !warehouseBefore.Contains(entry.Key)
                            && !NativeEquals(entry.ItemData, refinedItem))
            .Concat(ReadInventoryEquipment()
                .Where(entry => !bagBefore.Contains(entry.Key)
                                && !NativeEquals(entry.ItemData, refinedItem)))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var sold = 0;
        var warnings = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var displacedItem in displaced)
        {
            if (!ReadBool(Read(Read(displacedItem.ItemData, "tItemData"), "isSell")))
            {
                warnings.Add("a Vault-displaced item was not sellable and was preserved");
                continue;
            }
            var sale = SellExactEquipmentFromSource(known, failed, displacedItem);
            sold += sale.Sold;
            if (!string.IsNullOrWhiteSpace(sale.Error)) warnings.Add(sale.Error);
            if (sale.Uncertain) warnings.Add("a displaced-item sale could not be fully verified");
        }
        if (moveError is not null) warnings.Add(moveError.Message);
        return (finalSource, sold, warnings.Count > 0, string.Join("; ", warnings));
    }

    private static MythicRefineResult MythicRefineFailure(string message)
        => new(false, false, false, false, false, false, message, null);

}
