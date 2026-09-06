#if PATHOFIDLE_RUNTIME_TEST
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace PathOfIdleInGameSearch;

// Request-file-driven and strictly read-only. This exercises the same selected-
// hero, native 30-second evaluator and +1 clone ranking as the public Forge UI,
// then proves the real equipped item identities and Forge levels stayed exact.
public sealed class RuntimeForgeOptimizationPreviewHook : MonoBehaviour
{
    private const string RequestPrefix = "PREVIEW OPTIMAL FORGE +1 v1.1.8";
    private const int DefaultTargetHeroUniqueId = 28130;
    private const float ReadinessTimeoutSeconds = 120f;
    private const float OperationTimeoutSeconds = 180f;
    private static readonly string RequestPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-forge-preview.request");
    private static readonly string ResultPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-forge-preview.result");
    private static readonly string ProgressPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-forge-preview.progress");

    private bool armed;
    private float armedAt;
    private float operationStartedAt;
    private float nextProbeAt;
    private float nextProgressAt;
    private int requestedHeroUniqueId = DefaultTargetHeroUniqueId;
    private AutoBuildOperation? operation;
    private Task<EquipmentForgeOptimizationResult>? task;
    private string beforeState = string.Empty;
    private Stopwatch? elapsed;
    private bool selectionLeaseOwned;

    public RuntimeForgeOptimizationPreviewHook(IntPtr pointer) : base(pointer) { }

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
        if (!armed)
        {
            try
            {
                if (!File.Exists(RequestPath)) return;
                var request = File.ReadAllText(RequestPath, Encoding.UTF8).Trim();
                if (!request.StartsWith(RequestPrefix, StringComparison.Ordinal)) return;
                var heroMarker = "|hero=";
                var heroMarkerIndex = request.IndexOf(heroMarker, StringComparison.Ordinal);
                requestedHeroUniqueId = heroMarkerIndex >= 0
                    && int.TryParse(
                        request[(heroMarkerIndex + heroMarker.Length)..].Split('|')[0],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsedHeroId)
                    && parsedHeroId > 0
                        ? parsedHeroId
                        : DefaultTargetHeroUniqueId;
                File.Delete(RequestPath);
                try { File.Delete(ResultPath); } catch { }
                try { File.Delete(ProgressPath); } catch { }
                armed = true;
                armedAt = Time.unscaledTime;
            }
            catch
            {
                return;
            }
        }

        if (!InGameSearchOverlay.TryGetRuntimeJointBuildTestQueueAvailability(out var readiness))
        {
            if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
            WriteFailure($"readiness timeout: {readiness}");
            return;
        }
        if (!GameInventoryReader.TryAcquireRuntimeTestHeroSelection(
                requestedHeroUniqueId, out readiness, out var initialLoadPending))
        {
            _ = initialLoadPending;
            if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
            WriteFailure($"could not select hero {requestedHeroUniqueId}: {readiness}");
            return;
        }
        selectionLeaseOwned = true;
        if (!GameInventoryReader.TryCaptureAutoBuildTarget(out var targetHeroKey, out readiness))
        {
            if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
            WriteFailure($"could not capture hero: {readiness}");
            return;
        }

        beforeState = GameInventoryReader.CaptureSelectedHeroForgeState();
        operation = new AutoBuildOperation(
            "read-only Forge optimization preview",
            false,
            0,
            0,
            Plugin.NormalizeAutoBuildThemePriority(Plugin.AutoBuildThemePriority.Value),
            Plugin.AutoBuildTheme.Value,
            Plugin.AutoBuildHeroThemes.Value,
            targetHeroKey,
            includeForgeBonuses: true);
        operationStartedAt = Time.unscaledTime;
        nextProgressAt = Time.unscaledTime;
        elapsed = Stopwatch.StartNew();
        var workerOperation = operation;
        task = Task.Run(() =>
        {
            EquipmentForgeOptimizationResult result;
            var cancelled = false;
            using var scope = MainThreadNativeGateway.AttachWorker(workerOperation);
            try
            {
                result = GameInventoryReader.PreviewSelectedHeroForgeOneLevel();
                cancelled = result.Cancelled;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                result = new EquipmentForgeOptimizationResult(
                    false, true, false, "read-only Forge preview cancelled", null);
            }
            catch (Exception error)
            {
                result = new EquipmentForgeOptimizationResult(
                    false, false, false, error.GetBaseException().Message, null);
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
        EquipmentForgeOptimizationResult result;
        try
        {
            result = task!.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            result = new EquipmentForgeOptimizationResult(
                false, false, false, error.GetBaseException().Message, null);
        }
        var afterState = GameInventoryReader.CaptureSelectedHeroForgeState();
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
        var best = result.BestCandidate;
        var attributes = best is null
            ? string.Empty
            : string.Join(";", best.Attributes.Select(attribute =>
                $"{attribute.BattleAttributeType}:{attribute.PreForge.ToString("R", CultureInfo.InvariantCulture)}>{attribute.Current.ToString("R", CultureInfo.InvariantCulture)}>{attribute.Next.ToString("R", CultureInfo.InvariantCulture)}"));
        try
        {
            File.WriteAllText(
                ResultPath,
                $"success={(result.Succeeded && unchanged && !result.MutationApplied && selectionRestored).ToString().ToLowerInvariant()}\n" +
                $"cancelled={result.Cancelled.ToString().ToLowerInvariant()}\n" +
                $"mutation_applied={result.MutationApplied.ToString().ToLowerInvariant()}\n" +
                $"state_unchanged={unchanged.ToString().ToLowerInvariant()}\n" +
                $"selection_restored={selectionRestored.ToString().ToLowerInvariant()}\n" +
                $"selection_cleanup={Sanitize(selectionCleanup)}\n" +
                $"hero={requestedHeroUniqueId.ToString(CultureInfo.InvariantCulture)}\n" +
                $"elapsed_ms={(elapsed?.ElapsedMilliseconds ?? 0L).ToString(CultureInfo.InvariantCulture)}\n" +
                $"item={Sanitize(best?.ItemName)}\n" +
                $"slot={Sanitize(best?.Slot)}\n" +
                $"forge_level_before={(best?.CurrentLevel ?? -1).ToString(CultureInfo.InvariantCulture)}\n" +
                $"forge_level_after={(best?.NextLevel ?? -1).ToString(CultureInfo.InvariantCulture)}\n" +
                $"price={(best?.Price ?? -1).ToString(CultureInfo.InvariantCulture)}\n" +
                $"current_performance={(best?.CurrentPerformance ?? double.NaN).ToString("R", CultureInfo.InvariantCulture)}\n" +
                $"next_performance={(best?.NextPerformance ?? double.NaN).ToString("R", CultureInfo.InvariantCulture)}\n" +
                $"performance_gain={(best?.PerformanceGain ?? double.NaN).ToString("R", CultureInfo.InvariantCulture)}\n" +
                $"attributes={attributes}\n" +
                $"message={Sanitize(result.Message)}\n" +
                $"before_state={Sanitize(beforeState)}\n" +
                $"after_state={Sanitize(afterState)}\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // Never repeat an already completed diagnostic due to receipt I/O.
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
                $"hero={requestedHeroUniqueId.ToString(CultureInfo.InvariantCulture)}\n" +
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
        armed = false;
        operation = null;
        task = null;
        beforeState = string.Empty;
        elapsed = null;
        operationStartedAt = 0f;
        nextProgressAt = 0f;
        requestedHeroUniqueId = DefaultTargetHeroUniqueId;
    }

    private static string Sanitize(string? value) => (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
#endif
