#if PATHOFIDLE_RUNTIME_TEST
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace PathOfIdleInGameSearch;

// Request-file driven and strictly read-only. It exercises the exact public
// candidate discovery/unlock path while proving every equipment identity,
// location, level, quality, Forge level, and lock state remained unchanged.
public sealed class RuntimeMythicRefinePreviewHook : MonoBehaviour
{
    private const string RequestPrefix = "PREVIEW MYTHIC REFINE BATCH v1.1.8";
    private const float OperationTimeoutSeconds = 120f;
    private static readonly string RequestPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-mythic-preview.request");
    private static readonly string ResultPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-mythic-preview.result");
    private static readonly string ProgressPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-mythic-preview.progress");

    private float nextProbeAt;
    private float operationStartedAt;
    private float nextProgressAt;
    private AutoBuildOperation? operation;
    private Task<MythicRefineResult>? task;
    private Stopwatch? elapsed;
    private string beforeState = string.Empty;
    private bool selectionLeaseOwned;
    private bool verifyChangedCandidateGuard;

    public RuntimeMythicRefinePreviewHook(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (task is not null)
        {
            if (Time.unscaledTime >= nextProgressAt)
            {
                nextProgressAt = Time.unscaledTime + 0.5f;
                WriteProgress();
            }
            if (task.IsCompleted)
            {
                Complete();
                return;
            }
            if (Time.unscaledTime - operationStartedAt >= OperationTimeoutSeconds
                && operation is { MutationStarted: false })
                operation.RequestCancel();
            return;
        }

        if (Time.unscaledTime < nextProbeAt) return;
        nextProbeAt = Time.unscaledTime + 0.5f;
        string request;
        try
        {
            if (!File.Exists(RequestPath)) return;
            request = File.ReadAllText(RequestPath, Encoding.UTF8).Trim();
            if (!request.StartsWith(RequestPrefix, StringComparison.Ordinal)) return;
            verifyChangedCandidateGuard = request.Contains(
                "|guard=changed-candidate-set",
                StringComparison.Ordinal);
        }
        catch
        {
            return;
        }

        if (!InGameSearchOverlay.TryGetRuntimeJointBuildTestQueueAvailability(out var readiness))
            return;
        if (!GameInventoryReader.TryAcquireRuntimeTestNoHeroSelection(out readiness))
            return;
        selectionLeaseOwned = true;

        try
        {
            // A request can arrive while the title/menu is still constructing
            // season inventory. Keep it pending and retry on a later frame
            // instead of throwing from the IL2CPP Update trampoline.
            beforeState = GameInventoryReader.CaptureMythicRefineInventoryState();
        }
        catch
        {
            GameInventoryReader.ReleaseRuntimeTestHeroSelection(out _);
            if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
                selectionLeaseOwned = false;
            return;
        }

        try
        {
            File.Delete(RequestPath);
            try { File.Delete(ResultPath); } catch { }
            try { File.Delete(ProgressPath); } catch { }
        }
        catch
        {
            GameInventoryReader.ReleaseRuntimeTestHeroSelection(out _);
            if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
                selectionLeaseOwned = false;
            return;
        }

        operation = new AutoBuildOperation(
            "read-only Mythic batch refinement preview",
            false,
            0,
            0,
            Plugin.NormalizeAutoBuildThemePriority(Plugin.AutoBuildThemePriority.Value),
            Plugin.AutoBuildTheme.Value,
            Plugin.AutoBuildHeroThemes.Value,
            targetHeroKey: string.Empty,
            includeForgeBonuses: true);
        operationStartedAt = Time.unscaledTime;
        nextProgressAt = Time.unscaledTime;
        elapsed = Stopwatch.StartNew();
        var workerOperation = operation;
        var verifyGuard = verifyChangedCandidateGuard;
        task = Task.Run(() =>
        {
            MythicRefineResult result;
            var cancelled = false;
            using var scope = MainThreadNativeGateway.AttachWorker(workerOperation);
            try
            {
                result = GameInventoryReader.PreviewOneEligibleLegendToMythic();
                if (verifyGuard && result.Succeeded && result.Candidate is { } candidate)
                {
                    result = GameInventoryReader.RefineAllEligibleLegendsToMythic(
                        candidate.ItemKey,
                        candidate.EligibleCount,
                        candidate.CandidateSetToken + "-changed-by-runtime-test");
                }
                cancelled = result.Cancelled;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                result = new MythicRefineResult(
                    false, true, false, false, false, false,
                    "read-only Mythic preview cancelled", null);
            }
            catch (Exception error)
            {
                result = new MythicRefineResult(
                    false, false, false, false, false, false,
                    error.GetBaseException().Message, null);
            }
            finally
            {
                workerOperation.MarkCompleted(cancelled);
            }
            return result;
        });
    }

    public void OnDestroy()
    {
        if (operation is { MutationStarted: false }) operation.RequestCancel();
        if (selectionLeaseOwned && (task is null || task.IsCompleted))
        {
            GameInventoryReader.ReleaseRuntimeTestHeroSelection(out _);
            if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
                selectionLeaseOwned = false;
        }
    }

    private void Complete()
    {
        elapsed?.Stop();
        MythicRefineResult result;
        try
        {
            result = task!.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            result = new MythicRefineResult(
                false, false, false, false, false, false,
                error.GetBaseException().Message, null);
        }
        var afterState = GameInventoryReader.CaptureMythicRefineInventoryState();
        var unchanged = string.Equals(beforeState, afterState, StringComparison.Ordinal);
        var selectionCleanup = string.Empty;
        if (selectionLeaseOwned)
        {
            GameInventoryReader.ReleaseRuntimeTestHeroSelection(out selectionCleanup);
            if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
                selectionLeaseOwned = false;
        }
        var selectionRestored = !GameInventoryReader.RuntimeTestSelectionBlocksUserOperations
                                && string.IsNullOrWhiteSpace(selectionCleanup);
        var candidate = result.Candidate;
        var expectedResultObserved = verifyChangedCandidateGuard
            ? !result.Succeeded && !result.Cancelled && !result.MutationAttempted
            : result.Succeeded;
        try
        {
            File.WriteAllText(
                ResultPath,
                $"success={(expectedResultObserved && unchanged && !result.MutationAttempted && selectionRestored).ToString().ToLowerInvariant()}\n" +
                $"guard_changed_candidate_set={verifyChangedCandidateGuard.ToString().ToLowerInvariant()}\n" +
                $"guard_rejection_verified={(verifyChangedCandidateGuard && expectedResultObserved).ToString().ToLowerInvariant()}\n" +
                $"cancelled={result.Cancelled.ToString().ToLowerInvariant()}\n" +
                $"mutation_attempted={result.MutationAttempted.ToString().ToLowerInvariant()}\n" +
                $"attempt_count={result.AttemptCount.ToString(CultureInfo.InvariantCulture)}\n" +
                $"success_count={result.SuccessCount.ToString(CultureInfo.InvariantCulture)}\n" +
                $"failure_count={result.FailureCount.ToString(CultureInfo.InvariantCulture)}\n" +
                $"skipped_count={result.SkippedCount.ToString(CultureInfo.InvariantCulture)}\n" +
                $"state_unchanged={unchanged.ToString().ToLowerInvariant()}\n" +
                $"selection_restored={selectionRestored.ToString().ToLowerInvariant()}\n" +
                $"selection_cleanup={Sanitize(selectionCleanup)}\n" +
                "hero_selected_during_test=false\n" +
                $"elapsed_ms={(elapsed?.ElapsedMilliseconds ?? 0L).ToString(CultureInfo.InvariantCulture)}\n" +
                $"eligible_count={(candidate?.EligibleCount ?? 0).ToString(CultureInfo.InvariantCulture)}\n" +
                $"candidate_set_token={Sanitize(candidate?.CandidateSetToken)}\n" +
                $"item={Sanitize(candidate?.ItemName)}\n" +
                $"source={candidate?.Source.ToString() ?? string.Empty}\n" +
                $"storage={Sanitize(candidate?.StorageLabel)}\n" +
                $"definition_id={(candidate?.DefinitionId ?? 0).ToString(CultureInfo.InvariantCulture)}\n" +
                $"mythic_definition_id={(candidate?.MythicDefinitionId ?? 0).ToString(CultureInfo.InvariantCulture)}\n" +
                $"level={(candidate?.Level ?? 0).ToString(CultureInfo.InvariantCulture)}\n" +
                $"forge_level={(candidate?.ForgeLevel ?? 0).ToString(CultureInfo.InvariantCulture)}\n" +
                $"main_attribute={(candidate?.MainAttributeValue ?? 0).ToString(CultureInfo.InvariantCulture)}\n" +
                $"duplicate_count={(candidate?.DuplicateCount ?? 0).ToString(CultureInfo.InvariantCulture)}\n" +
                $"success_rate_percent={(candidate?.SuccessRatePercent ?? 0d).ToString("R", CultureInfo.InvariantCulture)}\n" +
                $"message={Sanitize(result.Message)}\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // Never repeat a completed read-only diagnostic due to receipt I/O.
        }
        try { File.Delete(ProgressPath); } catch { }
        Reset();
    }

    private void WriteFailure(string message)
    {
        var selectionCleanup = string.Empty;
        if (selectionLeaseOwned)
        {
            GameInventoryReader.ReleaseRuntimeTestHeroSelection(out selectionCleanup);
            if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
                selectionLeaseOwned = false;
        }
        if (!string.IsNullOrWhiteSpace(selectionCleanup))
            message += $"; selection cleanup: {selectionCleanup}";
        try
        {
            File.WriteAllText(
                ResultPath,
                $"success=false\nmessage={Sanitize(message)}\n",
                new UTF8Encoding(false));
        }
        catch { }
        try { File.Delete(ProgressPath); } catch { }
        Reset();
    }

    private void WriteProgress()
    {
        if (operation is null) return;
        var progress = operation.Progress;
        try
        {
            File.WriteAllText(
                ProgressPath,
                $"phase={progress.Phase}\n" +
                $"step={progress.Step}\n" +
                $"index={progress.Index.ToString(CultureInfo.InvariantCulture)}\n" +
                $"total={progress.Total.ToString(CultureInfo.InvariantCulture)}\n" +
                $"detail={Sanitize(progress.Detail)}\n" +
                $"elapsed_ms={(elapsed?.ElapsedMilliseconds ?? 0L).ToString(CultureInfo.InvariantCulture)}\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void Reset()
    {
        operation = null;
        task = null;
        elapsed = null;
        beforeState = string.Empty;
        verifyChangedCandidateGuard = false;
        operationStartedAt = 0f;
        nextProgressAt = 0f;
    }

    private static string Sanitize(string? value) => (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
#endif
