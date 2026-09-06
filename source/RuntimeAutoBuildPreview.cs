#if PATHOFIDLE_RUNTIME_TEST
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace PathOfIdleInGameSearch;

// Request-file-driven, read-only broad-regression hook. It runs the production
// candidate discovery and native 30-second comparison, stops before the first
// live equipment write, and proves gear plus skills stayed byte-for-byte stable
// at the plugin's authoritative identity/level snapshot boundary.
public sealed class RuntimeAutoBuildPreviewHook : MonoBehaviour
{
    private const string RequestPrefix = "PREVIEW AUTO BUILD v1.1.8";
    private const int DefaultTargetHeroUniqueId = 28130;
    private const float ReadinessTimeoutSeconds = 120f;
    private const float OperationTimeoutSeconds = 180f;
    private const float AdventureStableSeconds = 1f;
    private static readonly string RequestPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-auto-preview.request");
    private static readonly string ResultPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-auto-preview.result");
    private static readonly string ProgressPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-auto-preview.progress");

    private bool armed;
    private bool selectionLeaseOwned;
    private float armedAt;
    private float operationStartedAt;
    private float nextProbeAt;
    private float nextProgressAt;
    private float adventureStableAt = -1f;
    private int requestedHeroUniqueId = DefaultTargetHeroUniqueId;
    private int requestedPlanningSeconds = 60;
    private int requestedThemePriority = 50;
    private string requestedTheme = "auto";
    private bool requestedIncludeForgeBonuses = true;
    private AutoBuildOperation? operation;
    private Task<AutoBuildPlanningPreviewResult>? task;
    private Stopwatch? elapsed;
    private string beforeState = string.Empty;

    public RuntimeAutoBuildPreviewHook(IntPtr pointer) : base(pointer) { }

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
                requestedHeroUniqueId = ParsePositiveInt(request, "|hero=", DefaultTargetHeroUniqueId);
                requestedPlanningSeconds = Plugin.NormalizeAutoBuildPlanningSeconds(
                    ParseNonNegativeInt(request, "|seconds=", 60));
                requestedThemePriority = Plugin.NormalizeAutoBuildThemePriority(
                    ParseNonNegativeInt(request, "|priority=", 50));
                requestedTheme = ParseText(request, "|theme=", "auto");
                requestedTheme = GameInventoryReader.NormalizeBuildTheme(requestedTheme);
                requestedIncludeForgeBonuses = ParseBoolean(
                    request, "|forge=", true);
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
        if (!GameInventoryReader.TryGetRuntimeTestHeroAdventureBusy(
                out var adventureBusy, out readiness))
        {
            if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
            WriteFailure($"could not isolate hero adventure state: {readiness}");
            return;
        }
        // A background Unity window can suspend battle progression indefinitely.
        // Once Auto is paused, however, that combat instance cannot start a new
        // persistent adventure transition and the planner reads HeroData rather
        // than CombatData. Record the observation without waiting for a battle
        // animation that may never advance while the window stays unfocused.
        _ = adventureBusy;
        if (adventureStableAt < 0f)
        {
            adventureStableAt = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - adventureStableAt < AdventureStableSeconds) return;
        if (!GameInventoryReader.TryCaptureAutoBuildTarget(out var targetHeroKey, out readiness))
        {
            if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
            WriteFailure($"could not capture hero: {readiness}");
            return;
        }

        beforeState = GameInventoryReader.CaptureRuntimeJointBuildPostState();
        operation = new AutoBuildOperation(
            "read-only Auto Build planning preview",
            false,
            0,
            requestedPlanningSeconds,
            requestedThemePriority,
            requestedTheme,
            string.Empty,
            targetHeroKey,
            requestedIncludeForgeBonuses);
        operationStartedAt = Time.unscaledTime;
        nextProgressAt = Time.unscaledTime;
        elapsed = Stopwatch.StartNew();
        var workerOperation = operation;
        task = Task.Run(() =>
        {
            var result = new AutoBuildPlanningPreviewResult(false, false, string.Empty);
            using var scope = MainThreadNativeGateway.AttachWorker(workerOperation);
            try
            {
                result = GameInventoryReader.PreviewSelectedHeroAutoBuild(includeStorage: true);
            }
            catch (OperationCanceledException)
            {
                result = new AutoBuildPlanningPreviewResult(
                    false, true, "read-only Auto Build preview cancelled");
            }
            catch (Exception error)
            {
                result = new AutoBuildPlanningPreviewResult(
                    false, false, error.GetBaseException().Message);
            }
            finally
            {
                workerOperation.MarkCompleted(result.Cancelled);
            }
            return result;
        });
    }

    public void OnDestroy()
    {
        if (operation is { MutationStarted: false }) operation.RequestCancel();
        if (selectionLeaseOwned && (task is null || task.IsCompleted)) ReleaseSelection(out _);
    }

    private void Complete()
    {
        elapsed?.Stop();
        AutoBuildPlanningPreviewResult result;
        try
        {
            result = task!.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            result = new AutoBuildPlanningPreviewResult(
                false, false, error.GetBaseException().Message);
        }
        var afterState = GameInventoryReader.CaptureRuntimeJointBuildPostState();
        var unchanged = string.Equals(beforeState, afterState, StringComparison.Ordinal);
        ReleaseSelection(out var selectionCleanup);
        var selectionRestored = !GameInventoryReader.RuntimeTestSelectionBlocksUserOperations
                                && string.IsNullOrWhiteSpace(selectionCleanup);
        try
        {
            File.WriteAllText(
                ResultPath,
                $"success={(result.Succeeded && unchanged && selectionRestored).ToString().ToLowerInvariant()}\n" +
                $"planning_succeeded={result.Succeeded.ToString().ToLowerInvariant()}\n" +
                $"cancelled={result.Cancelled.ToString().ToLowerInvariant()}\n" +
                $"state_unchanged={unchanged.ToString().ToLowerInvariant()}\n" +
                $"selection_restored={selectionRestored.ToString().ToLowerInvariant()}\n" +
                $"selection_cleanup={Sanitize(selectionCleanup)}\n" +
                $"hero={requestedHeroUniqueId.ToString(CultureInfo.InvariantCulture)}\n" +
                $"theme={Sanitize(requestedTheme)}\n" +
                $"planning_seconds={requestedPlanningSeconds.ToString(CultureInfo.InvariantCulture)}\n" +
                $"theme_priority={requestedThemePriority.ToString(CultureInfo.InvariantCulture)}\n" +
                $"include_forge_bonuses={requestedIncludeForgeBonuses.ToString().ToLowerInvariant()}\n" +
                $"elapsed_ms={(elapsed?.ElapsedMilliseconds ?? 0L).ToString(CultureInfo.InvariantCulture)}\n" +
                $"message={Sanitize(result.Message)}\n" +
                $"before_state={Sanitize(beforeState)}\n" +
                $"after_state={Sanitize(afterState)}\n",
                new UTF8Encoding(false));
        }
        catch { }
        try { File.Delete(ProgressPath); } catch { }
        Reset();
    }

    private void WriteFailure(string message)
    {
        ReleaseSelection(out var selectionCleanup);
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
                $"theme={Sanitize(requestedTheme)}\n" +
                $"include_forge_bonuses={requestedIncludeForgeBonuses.ToString().ToLowerInvariant()}\n" +
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

    private void ReleaseSelection(out string reason)
    {
        reason = string.Empty;
        if (!selectionLeaseOwned) return;
        GameInventoryReader.ReleaseRuntimeTestHeroSelection(out reason);
        if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
            selectionLeaseOwned = false;
    }

    private void Reset()
    {
        armed = false;
        operation = null;
        task = null;
        elapsed = null;
        beforeState = string.Empty;
        adventureStableAt = -1f;
        armedAt = 0f;
        operationStartedAt = 0f;
        nextProgressAt = 0f;
        requestedHeroUniqueId = DefaultTargetHeroUniqueId;
        requestedPlanningSeconds = 60;
        requestedThemePriority = 50;
        requestedTheme = "auto";
        requestedIncludeForgeBonuses = true;
    }

    private static int ParsePositiveInt(string request, string marker, int fallback)
    {
        var parsed = ParseNonNegativeInt(request, marker, fallback);
        return parsed > 0 ? parsed : fallback;
    }

    private static int ParseNonNegativeInt(string request, string marker, int fallback)
    {
        var markerIndex = request.IndexOf(marker, StringComparison.Ordinal);
        return markerIndex >= 0
               && int.TryParse(
                   request[(markerIndex + marker.Length)..].Split('|')[0],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var parsed)
               && parsed >= 0
            ? parsed
            : fallback;
    }

    private static string ParseText(string request, string marker, string fallback)
    {
        var markerIndex = request.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return fallback;
        var value = request[(markerIndex + marker.Length)..].Split('|')[0].Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool ParseBoolean(string request, string marker, bool fallback)
    {
        var value = ParseText(request, marker, fallback.ToString());
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string Sanitize(string? value) => (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
#endif
