using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
#if PATHOFIDLE_DIAGNOSTICS
using BepInEx.Logging;
#endif
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PathOfIdleInGameSearch;

public sealed class InGameSearchOverlay : MonoBehaviour
{
    private const float DefaultWindowWidth = 720f;
    private const float DefaultWindowHeight = 780f;
    private const float MinWindowWidth = 560f;
    private const float MinWindowHeight = 610f;
    private const float MaxWindowWidth = 1100f;
    private const float MaxWindowHeight = 1000f;
    private const float ScreenMargin = 10f;
    private const float MinUiScale = 0.78f;
    private const float MaxUiScale = 1.30f;
    private const float InventoryIdleRefreshSeconds = 15f;
    private const string SearchControlName = "PathOfIdleInGameSearch.Query";
    private static readonly int DragControlHint = "PathOfIdleInGameSearch.Drag".GetHashCode();
    private static readonly int ResizeControlHint = "PathOfIdleInGameSearch.Resize".GetHashCode();
    private static readonly float[] SpeedSteps = { 0.5f, 1f, 2f, 3f, 5f, 10f, 20f, 50f, 100f };
    private readonly List<ItemSearchRecord> allItems = new();
    private readonly List<ItemSearchRecord> matches = new();
    private ItemSearchRecord? hoveredItem;
    private Rect windowRect;
    private StorageKind selectedStorage = StorageKind.Inventory;
    private OverlayPage selectedPage = OverlayPage.Search;
    private int currentPage;
    private int searchCaret;
    private float currentSpeed = 1f;
    private bool visible;
    private bool focusSearch;
    private bool focusSpeedInput;
    private bool focusPlanningTimeInput;
    private bool focusThemePriorityInput;
    private bool dragging;
    private Vector2 dragOffset;
    private bool resizing;
    private Vector2 resizeStartMouse;
    private Vector2 resizeStartSize;
    private float uiScale = 1f;
    private float appliedStyleScale = -1f;
    private bool includeWarehouse = true;
    private int selectedQualityMask;
    private bool searchOptionsOnly;
    private string languageMode = "auto";
    private string query = string.Empty;
    private string speedInput = "1";
    private string planningTimeInput = "60";
    private string themePriorityInput = "50";
    private string status = string.Empty;
    private float nextRefreshAt;
    private float transferCooldownUntil;
    private BulkToolKind armedBulkOpen;
    private float bulkConfirmUntil;
    private BulkOpenSession? bulkSession;
    private bool warehouseSaleArmed;
    private float warehouseSaleConfirmUntil;
    private WarehouseEquipmentSaleSession? warehouseSaleSession;
    private AutoBuildAction armedAutoBuild;
    private float autoBuildConfirmUntil;
    private AutoBuildOperation? autoBuildOperation;
    private Task<AutoBuildWorkerResult>? autoBuildTask;
    private bool forgeOptimizationArmed;
    private bool forgeMaxAllArmed;
    private float forgeOptimizationConfirmUntil;
    private AutoBuildOperation? forgeOptimizationOperation;
    private Task<EquipmentForgeOptimizationResult>? forgeOptimizationTask;
    private const float MythicRefineConfirmSeconds = 12f;
    private bool mythicRefineArmed;
    private float mythicRefineConfirmUntil;
    private bool mythicRefinePreviewOnly;
    private string mythicRefineStatus = string.Empty;
    private string mythicRefineConfirmedItemKey = string.Empty;
    private int mythicRefineConfirmedCandidateCount;
    private string mythicRefineConfirmedBatchToken = string.Empty;
    private AutoBuildOperation? mythicRefineOperation;
    private Task<MythicRefineResult>? mythicRefineTask;
    private int equipmentBoxCount;
    private int runeBoxCount;
    private bool bulkCountsAvailable = true;
    private bool bulkCountFailedLastRefresh;
    private IMECompositionMode previousImeMode;
    private bool imeModeSaved;
    private GameObject? inputBlockerCanvasObject;
    private GameObject? inputBlockerRegionObject;
    private RectTransform? inputBlockerRect;
    private GameObject? inputBlockerTooltipObject;
    private RectTransform? inputBlockerTooltipRect;
    private bool keyboardBlockCaptured;
    private bool previousGameKeyboardBlocked;
    private bool navigationBlockCaptured;
    private bool previousNavigationEventsEnabled;
    private bool keyboardReleasePending;
    private bool keyboardBlockUnavailableLogged;
    private bool overlayInputFocused;
    private Rect activeTooltipRect;
    private Rect tooltipAnchorRect;
    private readonly List<Rect> visibleTransferRects = new();
    private Vector2 searchBodyScroll;
    private Vector2 bulkBodyScroll;
    private Vector2 autoBuildBodyScroll;
    private Vector2 workshopBodyScroll;
    private Vector2 tooltipScroll;
    private Vector2 bulkStatusScroll;
    private Vector2 autoBuildStatusScroll;
    private string tooltipItemKey = string.Empty;
    private string bulkStatusScrollKey = string.Empty;
    private string autoBuildStatusScrollKey = string.Empty;
    private string selectedHeroSummary = string.Empty;
    private string selectedHeroProfile = string.Empty;
    private string selectedAutoBuildTheme = "auto";
#if PATHOFIDLE_RUNTIME_TEST
    private static readonly object RuntimeTestResultSync = new();
    private static AutoBuildWorkerResult? runtimeTestResult;
    private static long runtimeTestElapsedMilliseconds;
    private static string runtimeTestPostState = string.Empty;
    private bool runtimeTestQueued;
    private Stopwatch? runtimeTestStopwatch;
    private string runtimeUiStage = "not started";
#endif

    private GUIStyle? panelStyle;
    private GUIStyle? solidFillStyle;
    private GUIStyle? titleStyle;
    private GUIStyle? hintStyle;
    private GUIStyle? searchStyle;
    private GUIStyle? searchTextStyle;
    private GUIStyle? resultNameStyle;
    private GUIStyle? resultMetaStyle;
    private GUIStyle? resultAffixStyle;
    private GUIStyle? badgeStyle;
    private GUIStyle? closeStyle;
    private GUIStyle? tooltipTitleStyle;
    private GUIStyle? tooltipBodyStyle;
    private GUIStyle? utilityTitleStyle;
    private GUIStyle? pageStyle;
    private GUIStyle? buttonStyle;
    private GUIStyle? compactButtonStyle;
    private GUIStyle? toggleStyle;
    private Font? uiFont;

    private static InGameSearchOverlay? activeOverlay;
    internal static bool ShouldBlockWheel => activeOverlay?.ShouldBlockPointerInput() == true;
    private bool HasActiveAutoBuildWorker
        => autoBuildTask is not null || autoBuildOperation is { IsCompleted: false };
    private bool HasActiveForgeOptimizationWorker
        => forgeOptimizationTask is not null
           || forgeOptimizationOperation is { IsCompleted: false };
    private bool HasActiveMythicRefineWorker
        => mythicRefineTask is not null
           || mythicRefineOperation is { IsCompleted: false };
    private bool IsAutoBuildRunning
        => HasActiveAutoBuildWorker
           || HasActiveForgeOptimizationWorker
           || HasActiveMythicRefineWorker
           || GameInventoryReader.RuntimeTestSelectionBlocksUserOperations
           || GameInventoryReader.WarehouseEquipmentSaleActive;

#if PATHOFIDLE_RUNTIME_TEST
    internal static bool TryGetRuntimeJointBuildTestQueueAvailability(out string reason)
    {
        var overlay = activeOverlay;
        if (overlay is null)
        {
            reason = "in-game overlay is not loaded";
            return false;
        }
        if (overlay.HasActiveAutoBuildWorker)
        {
            reason = "another Auto Build operation is already running";
            return false;
        }
        if (overlay.HasActiveForgeOptimizationWorker)
        {
            reason = "a Forge optimization is already running";
            return false;
        }
        if (overlay.HasActiveMythicRefineWorker)
        {
            reason = "a Mythic refinement operation is already running";
            return false;
        }
        if (GameInventoryReader.WarehouseEquipmentSaleActive)
        {
            reason = "a Warehouse equipment sale is running";
            return false;
        }
        if (overlay.bulkSession is not null)
        {
            reason = "a bulk-open operation is running";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    internal static bool TryQueueRuntimeJointBuildTest(out string reason)
    {
        var overlay = activeOverlay;
        if (!TryGetRuntimeJointBuildTestQueueAvailability(out reason) || overlay is null)
            return false;

        // This bounded diagnostic is specifically verifying missing-skill
        // transformation as part of the real combined Auto Build path.
        Plugin.AutoTransformSkills.Value = true;
        lock (RuntimeTestResultSync)
        {
            runtimeTestResult = null;
            runtimeTestElapsedMilliseconds = 0;
            runtimeTestPostState = string.Empty;
        }
        overlay.runtimeTestQueued = true;
        overlay.runtimeTestStopwatch = Stopwatch.StartNew();
        overlay.QueueAutoBuild(
            AutoBuildAction.Combined,
            UiText.L("진단용 공동 자동 빌드", "Diagnostic joint Auto Build", "诊断联合自动构筑", "診斷聯合自動配置"),
            Plugin.AutoBuildIncludeStorage.Value,
            allowRuntimeTestSelectionLease: true);
        if (overlay.autoBuildTask is not null)
        {
            reason = string.Empty;
            return true;
        }

        overlay.runtimeTestQueued = false;
        overlay.runtimeTestStopwatch = null;
        reason = "the Auto Build worker could not be queued";
        return false;
    }

    internal static bool TryCancelRuntimeJointBuildTestBeforeMutation(out string reason)
    {
        var overlay = activeOverlay;
        if (overlay is null || !overlay.runtimeTestQueued)
        {
            reason = "the diagnostic Auto Build is no longer queued";
            return false;
        }
        var operation = overlay.autoBuildOperation;
        if (operation is null)
        {
            reason = "the diagnostic Auto Build operation is unavailable";
            return false;
        }
        if (operation.MutationStarted)
        {
            reason = "the diagnostic Auto Build already started mutation and must finish verification/rollback";
            return false;
        }
        operation.RequestCancel();
        var progress = operation.Progress;
        reason = "diagnostic planning exceeded 90 seconds and was cancelled before mutation"
                 + $" (step={progress.Step}, index={progress.Index}, total={progress.Total}, detail={progress.Detail})";
        return true;
    }

    internal static bool TryTakeRuntimeJointBuildTestResult(
        out AutoBuildWorkerResult result,
        out long elapsedMilliseconds,
        out string postState)
    {
        lock (RuntimeTestResultSync)
        {
            if (runtimeTestResult is not { } completed)
            {
                result = default;
                elapsedMilliseconds = 0;
                postState = string.Empty;
                return false;
            }
            result = completed;
            elapsedMilliseconds = runtimeTestElapsedMilliseconds;
            postState = runtimeTestPostState;
            runtimeTestResult = null;
            runtimeTestElapsedMilliseconds = 0;
            runtimeTestPostState = string.Empty;
            return true;
        }
    }
#endif

    public InGameSearchOverlay(IntPtr pointer) : base(pointer) { }

    public void Start()
    {
        MainThreadNativeGateway.InitializeMainThread();
        activeOverlay = this;
        StartupAutoController.Initialize(Plugin.AutoStart.Value);
        query = Plugin.SavedQuery.Value ?? string.Empty;
        searchCaret = query.Length;
        includeWarehouse = Plugin.IncludeWarehouse.Value;
        selectedQualityMask = Plugin.QualityMask.Value;
        if (selectedQualityMask == 0 && Plugin.QualityFilter.Value != 0)
        {
            selectedQualityMask = QualityFilterLogic.BitFor(Plugin.QualityFilter.Value);
            Plugin.QualityMask.Value = selectedQualityMask;
            Plugin.QualityFilter.Value = 0;
        }
        searchOptionsOnly = Plugin.SearchOptionsOnly.Value;
        languageMode = NormalizeLanguageMode(Plugin.UiLanguage.Value);
        UpdateUiLanguage();
        status = UiText.L("게임에 접속한 뒤 F3을 누르세요.", "Enter the game, then press F3.");
        var configuredWidth = Plugin.WindowWidth.Value;
        var configuredHeight = Plugin.WindowHeight.Value;
        if (!float.IsFinite(configuredWidth) || configuredWidth <= 0f)
        {
            configuredWidth = DefaultWindowWidth;
            Plugin.WindowWidth.Value = configuredWidth;
        }
        if (!float.IsFinite(configuredHeight) || configuredHeight <= 0f)
        {
            configuredHeight = DefaultWindowHeight;
            Plugin.WindowHeight.Value = configuredHeight;
        }
        windowRect = new Rect(Plugin.WindowX.Value, Plugin.WindowY.Value, configuredWidth, configuredHeight);
        var configuredSpeed = Plugin.GameSpeed.Value;
        currentSpeed = float.IsFinite(configuredSpeed) ? Mathf.Clamp(configuredSpeed, 0.1f, 100f) : 1f;
        speedInput = currentSpeed.ToString("0.##", CultureInfo.InvariantCulture);
        ApplyGameSpeed();
        var configuredPlanningSeconds = Plugin.NormalizeAutoBuildPlanningSeconds(
            Plugin.AutoBuildPlanningSeconds.Value);
        Plugin.AutoBuildPlanningSeconds.Value = configuredPlanningSeconds;
        planningTimeInput = configuredPlanningSeconds <= 0
            ? "∞"
            : configuredPlanningSeconds.ToString(CultureInfo.InvariantCulture);
        var configuredThemePriority = Plugin.NormalizeAutoBuildThemePriority(
            Plugin.AutoBuildThemePriority.Value);
        Plugin.AutoBuildThemePriority.Value = configuredThemePriority;
        themePriorityInput = configuredThemePriority.ToString(CultureInfo.InvariantCulture);
        ClampWindowToScreen();
        UpdateUiScale();
        Plugin.DiagInfo("In-game search overlay started.");
        if (Plugin.OpenOnStart.Value) SetVisible(true);
    }

    public void Update()
    {
        if (Plugin.TryConsumeBackgroundActivationToggle())
        {
            Plugin.DiagInfo("System-wide search hotkey received.");
            SetVisible(!visible);
            return;
        }
#if PATHOFIDLE_RUNTIME_TEST
        if (GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
            Plugin.MaintainRuntimeNoActivateWindowScope();
#endif
        // Planning is read-only until the final transaction. Give its queued
        // native snapshots up to half of a 60 Hz frame instead of four ms; this
        // reduces frame-by-frame hand-off overhead while preserving UI updates.
        MainThreadNativeGateway.Drain(8d);
        StartupAutoController.Tick(Plugin.AutoStart.Value);
        CompleteAutoBuildIfReady();
        CompleteForgeOptimizationIfReady();
        CompleteMythicRefineIfReady();
        if (warehouseSaleSession is not null) AdvanceWarehouseEquipmentSaleSession();
        // Never freeze the whole game while the worker plans. Path of Idle keeps
        // creating Spine mixing entries in some zero-delta update paths; a long
        // freeze can therefore build a recursive mixingFrom chain that overflows
        // the main-thread stack as soon as the window renders or becomes visible
        // again. Native reads and the final mutation are already serialized by
        // MainThreadNativeGateway, so global time does not need to stop here.
        var targetTimeScale = currentSpeed;
        if (Math.Abs(Time.timeScale - targetTimeScale) > 0.001f) Time.timeScale = targetTimeScale;
        var controlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        Plugin.GetBackgroundActivationHotkeyRegistration(
            out var globalF3Registered,
            out var globalCtrlFRegistered);
        // A registered system-wide key is delivered through WM_HOTKEY. Do not
        // also consume Unity's local edge for that same key: depending on the
        // Windows message timing, doing both could toggle the panel twice. A key
        // that failed global registration remains available while the game is
        // active, so a conflict does not disable the original local shortcut.
        var localF3Pressed = !globalF3Registered && Input.GetKeyDown(KeyCode.F3);
        var localCtrlFPressed = !globalCtrlFRegistered
                                && controlDown
                                && Input.GetKeyDown(KeyCode.F);
        if (localF3Pressed || localCtrlFPressed)
        {
            Plugin.DiagInfo("Search hotkey received.");
            SetVisible(!visible);
            return;
        }

        if (!visible)
        {
            if (keyboardReleasePending) TryReleaseGameInputGuards();
            return;
        }
        // hotControl normally delivers MouseUp even outside the window, but a
        // focus loss or IMGUI Ignore event can skip it. Never leave the temporary
        // full-screen drag/resize blocker latched after the button is released.
        if ((resizing || dragging) && !Input.GetMouseButton(0))
        {
            resizing = false;
            dragging = false;
            GUIUtility.hotControl = 0;
            ClampWindowToScreen();
            UpdateUiScale();
            SaveWindowPosition();
            UpdateInputBlocker();
        }
        // The game's key manager is process-wide rather than spatial. Release it
        // as soon as the pointer leaves the owned UI, before an outside click or
        // wheel can be interpreted by the game. Re-entering/clicking the panel
        // reacquires it on the next update without extending the raycast surface.
        var pointerInsideOverlay = IsPointerOverOverlay();
        if (!pointerInsideOverlay) overlayInputFocused = false;
        if (overlayInputFocused && pointerInsideOverlay) MaintainGameKeyboardBlock();
        else if (keyboardBlockCaptured || navigationBlockCaptured)
        {
            keyboardReleasePending = true;
            TryReleaseGameInputGuards();
        }
        UpdateInputBlocker();
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            SetVisible(false);
            return;
        }
        if (bulkSession is not null && !IsAutoBuildRunning) AdvanceBulkOpenSession();

        if (!IsAutoBuildRunning && Time.unscaledTime >= nextRefreshAt)
        {
            // Full inventory/vault discovery uses game reflection and can be fairly
            // expensive on large saves. Transfers and user actions already refresh
            // immediately, so a slower idle poll avoids needless frame-time spikes.
            nextRefreshAt = Time.unscaledTime + InventoryIdleRefreshSeconds;
            RefreshCurrentPage(resetStatus: false);
        }
    }

    public void OnGUI()
    {
        try
        {
            DrawResponsiveOverlay();
        }
        catch (Exception error)
        {
            _ = error;
#if PATHOFIDLE_RUNTIME_TEST
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        "PathOfIdleInGameSearch-v1.1.8-ui-error.txt"),
                    $"stage={runtimeUiStage}{Environment.NewLine}{error}");
            }
            catch
            {
                // The fail-open input release below must not depend on diagnostics.
            }
#endif
            // A rendering failure must never leave an invisible modal input layer.
            // Fail open: close the overlay and release every game input latch.
            visible = false;
            overlayInputFocused = false;
            dragging = false;
            resizing = false;
            GUIUtility.hotControl = 0;
            RestoreImeMode();
            SetInputBlockerActive(false);
            TryReleaseGameInputGuards(force: true);
        }
    }

    [HideFromIl2Cpp]
    private void DrawResponsiveOverlay()
    {
        MarkRuntimeUiStage("entry");
        if (!visible)
        {
            SetInputBlockerActive(false);
            return;
        }

        var current = Event.current;
        if (current is null) return;
        if (current.type == EventType.MouseDown)
        {
            // Do not consume clicks outside these two owned surfaces. The click
            // still clears the overlay's keyboard focus, then continues to the game.
            overlayInputFocused = windowRect.Contains(current.mousePosition)
                                  || activeTooltipRect.Contains(current.mousePosition);
            if (!overlayInputFocused)
            {
                focusSearch = false;
                focusSpeedInput = false;
                focusPlanningTimeInput = false;
                focusThemePriorityInput = false;
                RestoreImeMode();
            }
        }

        MarkRuntimeUiStage("clamp-window");
        ClampWindowToScreen();
        UpdateUiScale();
        MarkRuntimeUiStage("ensure-styles");
        EnsureStyles();

        // Item details used to open as a second panel beside Search on hover.
        // That made the window look twice as wide and the dense diagnostic-like
        // text obscured the game. Search results already show the useful summary,
        // so the automatic side panel is intentionally disabled.
        hoveredItem = null;
        tooltipItemKey = string.Empty;
        tooltipAnchorRect = default;
        activeTooltipRect = default;
        var tooltipOwnsPointer = false;
        var pointerOverTransfer = !tooltipOwnsPointer
                                  && visibleTransferRects.Any(rect => rect.Contains(current.mousePosition));
        if (pointerOverTransfer)
        {
            hoveredItem = null;
            tooltipItemKey = string.Empty;
            activeTooltipRect = default;
        }
        visibleTransferRects.Clear();

        MarkRuntimeUiStage("window-input");
        if (resizing || !tooltipOwnsPointer) HandleWindowResize();
        if (!resizing && !tooltipOwnsPointer) HandleWindowDrag();
        UpdateUiScale();
        EnsureStyles();

        if (!tooltipOwnsPointer) hoveredItem = null;
        activeTooltipRect = default;
        MarkRuntimeUiStage("update-input-blocker");
        UpdateInputBlocker();

        var previousEnabled = GUI.enabled;
        if (tooltipOwnsPointer) GUI.enabled = false;
        GUI.depth = -10000;
        MarkRuntimeUiStage("draw-background");
        DrawOpaquePanel(windowRect, new Color(0.035f, 0.035f, 0.045f, 1f));

        var padding = S(16f);
        var content = new Rect(
            windowRect.x + padding,
            windowRect.y + S(10f),
            Math.Max(S(240f), windowRect.width - padding * 2f),
            Math.Max(S(120f), windowRect.height - S(26f)));
        var headerRect = new Rect(content.x, content.y, content.width, S(32f));
        MarkRuntimeUiStage("draw-header");
        DrawResponsiveHeader(headerRect, tooltipOwnsPointer);
        if (!visible)
        {
            GUI.enabled = previousEnabled;
            return;
        }

        var subRowTop = headerRect.yMax + S(4f);
        var tabWidth = Mathf.Clamp(content.width * 0.72f, S(360f), content.width);
        var hintWidth = Math.Max(0f, content.width - tabWidth - S(10f));
        if (hintWidth >= S(110f))
        {
            var hint = UiText.L(
                "F3 / Ctrl+F · 공백 AND · | OR · - 제외",
                "F3 / Ctrl+F · space AND · | OR · - exclude",
                "F3 / Ctrl+F · 空格 AND · | OR · - 排除",
                "F3 / Ctrl+F · 空格 AND · | OR · - 排除");
            GUI.Label(
                new Rect(content.x, subRowTop, hintWidth, S(28f)),
                EllipsizeToWidth(hint, hintStyle!, hintWidth),
                hintStyle!);
        }
        MarkRuntimeUiStage("draw-tabs");
        DrawResponsivePageTabs(new Rect(content.xMax - tabWidth, subRowTop, tabWidth, S(28f)));

        var bodyTop = subRowTop + S(34f);
        var body = new Rect(
            content.x,
            bodyTop,
            content.width,
            Math.Max(S(80f), windowRect.yMax - S(18f) - bodyTop));
        switch (selectedPage)
        {
            case OverlayPage.BulkOpen:
                MarkRuntimeUiStage("draw-bulk-body");
                DrawResponsiveBulkOpenPanel(body);
                break;
            case OverlayPage.AutoBuild:
                MarkRuntimeUiStage("draw-auto-build-body");
                DrawResponsiveAutoBuildPanel(body);
                break;
            case OverlayPage.Workshop:
                MarkRuntimeUiStage("draw-workshop-body");
                DrawResponsiveWorkshopPanel(body);
                break;
            default:
                MarkRuntimeUiStage("draw-search-body");
                DrawResponsiveSearchPanel(body, tooltipOwnsPointer);
                break;
        }

        GUI.enabled = previousEnabled;
        MarkRuntimeUiStage("draw-resize-grip");
        DrawResizeGrip();
        tooltipItemKey = string.Empty;
        tooltipAnchorRect = default;
        tooltipScroll = Vector2.zero;
        UpdateInputBlocker();
        ConsumeRemainingKeyboardEvent();
        MarkRuntimeUiStage("complete");
    }

    [Conditional("PATHOFIDLE_RUNTIME_TEST")]
    private void MarkRuntimeUiStage(string stage)
    {
#if PATHOFIDLE_RUNTIME_TEST
        runtimeUiStage = stage;
#endif
    }

    [HideFromIl2Cpp]
    private void DrawResponsiveHeader(Rect rect, bool tooltipOwnsPointer)
    {
        var gap = S(6f);
        var languageWidth = ResponsiveLanguageButtonWidth();
        var minusWidth = S(32f);
        var speedWidth = S(62f);
        var plusWidth = S(32f);
        var applyWidth = S(52f);
        var resetWidth = S(42f);
        var closeWidth = S(34f);
        var controlsWidth = ResponsiveHeaderControlsWidth();
        var controlX = rect.xMax - controlsWidth;
        var titleWidth = Math.Max(S(70f), controlX - rect.x - gap);
        var pageTitle = selectedPage switch
        {
            OverlayPage.BulkOpen => UiText.L("Path of Idle · 일괄 개봉", "Path of Idle · Bulk Open", "Path of Idle · 批量开启", "Path of Idle · 批次開啟"),
            OverlayPage.AutoBuild => UiText.L("Path of Idle · 자동 빌드", "Path of Idle · Auto Build", "Path of Idle · 自动配装", "Path of Idle · 自動配裝"),
            OverlayPage.Workshop => UiText.L("Path of Idle · 장비 강화", "Path of Idle · Forge", "Path of Idle · 装备强化", "Path of Idle · 裝備強化"),
            _ => UiText.L("Path of Idle · 아이템 검색", "Path of Idle · Item Search", "Path of Idle · 物品搜索", "Path of Idle · 物品搜尋")
        };
        GUI.Label(new Rect(rect.x, rect.y, titleWidth, rect.height), EllipsizeToWidth(pageTitle, titleStyle!, titleWidth), titleStyle!);

        var headerEnabled = GUI.enabled;
        GUI.enabled = headerEnabled && !IsAutoBuildRunning;
        if (GUI.Button(new Rect(controlX, rect.y, languageWidth, rect.height), LanguageButtonLabel(), compactButtonStyle!)) CycleUiLanguage();
        controlX += languageWidth + gap;
        GUI.enabled = headerEnabled;
        if (GUI.Button(new Rect(controlX, rect.y, minusWidth, rect.height), "−", buttonStyle!)) ChangeGameSpeed(-1);
        controlX += minusWidth + gap;
        var speedRect = new Rect(controlX, rect.y, speedWidth, rect.height);
        if (!tooltipOwnsPointer) HandleSpeedInput(speedRect);
        GUI.Box(speedRect, GUIContent.none, searchStyle!);
        GUI.Label(new Rect(speedRect.x + S(4f), speedRect.y + S(3f), speedRect.width - S(8f), speedRect.height - S(6f)), speedInput + (focusSpeedInput ? "|" : string.Empty) + "×", badgeStyle!);
        controlX += speedWidth + gap;
        if (GUI.Button(new Rect(controlX, rect.y, plusWidth, rect.height), "+", buttonStyle!)) ChangeGameSpeed(1);
        controlX += plusWidth + gap;
        if (GUI.Button(new Rect(controlX, rect.y, applyWidth, rect.height), UiText.L("적용", "Apply", "应用", "套用"), compactButtonStyle!)) ApplyCustomSpeed();
        controlX += applyWidth + gap;
        if (GUI.Button(new Rect(controlX, rect.y, resetWidth, rect.height), "1×", buttonStyle!)) SetGameSpeed(1f);
        controlX += resetWidth + gap;
        if (GUI.Button(new Rect(controlX, rect.y, closeWidth, rect.height), "×", closeStyle!)) SetVisible(false);
        GUI.enabled = headerEnabled;
    }

    private float ResponsiveLanguageButtonWidth()
        => Math.Max(S(62f), compactButtonStyle!.CalcSize(new GUIContent(LanguageButtonLabel())).x + S(16f));

    private float ResponsiveHeaderControlsWidth()
        => ResponsiveLanguageButtonWidth() + S(32f) + S(62f) + S(32f) + S(52f) + S(42f) + S(34f) + S(6f) * 6f;

    private string DetailedStatusButtonLabel()
        => Plugin.ShowDetailedStatus.Value
            ? UiText.L("로그 숨김", "Hide log", "隐藏日志", "隱藏記錄")
            : UiText.L("로그 보기", "Show log", "显示日志", "顯示記錄");

    private static void ToggleDetailedStatus()
        => Plugin.ShowDetailedStatus.Value = !Plugin.ShowDetailedStatus.Value;

    [HideFromIl2Cpp]
    private void DrawResponsivePageTabs(Rect rect)
    {
        var gap = S(6f);
        var width = Math.Max(S(62f), (rect.width - gap * 3f) / 4f);
        DrawPageTab(new Rect(rect.x, rect.y, width, rect.height), OverlayPage.Search, UiText.L("아이템 검색", "Item Search", "物品搜索", "物品搜尋"));
        DrawPageTab(new Rect(rect.x + width + gap, rect.y, width, rect.height), OverlayPage.BulkOpen, UiText.L("일괄 개봉", "Bulk Open", "批量开启", "批次開啟"));
        DrawPageTab(new Rect(rect.x + (width + gap) * 2f, rect.y, width, rect.height), OverlayPage.AutoBuild, UiText.L("자동 빌드", "Auto Build", "自动配装", "自動配裝"));
        DrawPageTab(new Rect(rect.x + (width + gap) * 3f, rect.y, width, rect.height), OverlayPage.Workshop, UiText.L("장비 강화", "Forge", "装备强化", "裝備強化"));
    }

    [HideFromIl2Cpp]
    private void UpdateManualScroll(Rect viewport, float contentHeight, ref Vector2 scroll)
    {
        scroll.x = 0f;
        var maximum = Math.Max(0f, contentHeight - viewport.height);
        scroll.y = Mathf.Clamp(scroll.y, 0f, maximum);
        var current = Event.current;
        if (current is null) return;
        if (current.type == EventType.ScrollWheel && viewport.Contains(current.mousePosition))
        {
            overlayInputFocused = true;
            scroll.y = Mathf.Clamp(scroll.y + current.delta.y * S(24f), 0f, maximum);
            current.Use();
            return;
        }

        if (maximum <= 0f || current.type != EventType.MouseDown || current.button != 0) return;
        var track = ManualScrollbarTrack(viewport);
        if (!track.Contains(current.mousePosition)) return;
        var thumbHeight = Math.Max(S(24f), track.height * viewport.height / contentHeight);
        var travel = Math.Max(1f, track.height - thumbHeight);
        var ratio = Mathf.Clamp01((current.mousePosition.y - track.y - thumbHeight * 0.5f) / travel);
        scroll.y = ratio * maximum;
        overlayInputFocused = true;
        current.Use();
    }

    [HideFromIl2Cpp]
    private Rect ManualContentRect(Rect viewport, Rect contentRect, Vector2 scroll)
        => new(
            viewport.x + contentRect.x - scroll.x,
            viewport.y + contentRect.y - scroll.y,
            contentRect.width,
            contentRect.height);

    [HideFromIl2Cpp]
    private static bool RectFullyInside(Rect outer, Rect inner)
        => inner.xMin >= outer.xMin && inner.xMax <= outer.xMax
           && inner.yMin >= outer.yMin && inner.yMax <= outer.yMax;

    [HideFromIl2Cpp]
    private static bool RectIntersects(Rect left, Rect right)
        => left.xMin < right.xMax && left.xMax > right.xMin
           && left.yMin < right.yMax && left.yMax > right.yMin;

    [HideFromIl2Cpp]
    private static Rect IntersectRects(Rect left, Rect right)
    {
        var xMin = Math.Max(left.xMin, right.xMin);
        var yMin = Math.Max(left.yMin, right.yMin);
        var xMax = Math.Min(left.xMax, right.xMax);
        var yMax = Math.Min(left.yMax, right.yMax);
        return xMax > xMin && yMax > yMin
            ? new Rect(xMin, yMin, xMax - xMin, yMax - yMin)
            : default;
    }

    [HideFromIl2Cpp]
    private bool TryManualControlRect(Rect viewport, Vector2 scroll, Rect contentRect, out Rect screenRect)
    {
        screenRect = ManualContentRect(viewport, contentRect, scroll);
        return RectFullyInside(viewport, screenRect);
    }

    [HideFromIl2Cpp]
    private void DrawManualLabel(
        Rect viewport,
        Vector2 scroll,
        Rect contentRect,
        string text,
        GUIStyle style)
    {
        var screenRect = ManualContentRect(viewport, contentRect, scroll);
        if (!RectIntersects(viewport, screenRect)) return;
        var visibleRect = IntersectRects(viewport, screenRect);
        if (visibleRect.width <= 0f || visibleRect.height <= 0f) return;

        // Labels are the only partially visible element. Preserve the original
        // wrapping width, shift its contents by the hidden amount, and clip to the
        // intersection. Interactive controls are drawn only when fully visible.
        var previousOffset = style.contentOffset;
        var previousClipping = style.clipping;
        style.contentOffset = new Vector2(
            previousOffset.x - (visibleRect.x - screenRect.x),
            previousOffset.y - (visibleRect.y - screenRect.y));
        style.clipping = TextClipping.Clip;
        GUI.Label(visibleRect, text, style);
        style.contentOffset = previousOffset;
        style.clipping = previousClipping;
    }

    [HideFromIl2Cpp]
    private bool DrawManualButton(
        Rect viewport,
        Vector2 scroll,
        Rect contentRect,
        string text,
        GUIStyle style)
        => TryManualControlRect(viewport, scroll, contentRect, out var screenRect)
           && GUI.Button(screenRect, text, style);

    [HideFromIl2Cpp]
    private bool DrawManualToggle(
        Rect viewport,
        Vector2 scroll,
        Rect contentRect,
        bool value,
        string text,
        GUIStyle style)
        => TryManualControlRect(viewport, scroll, contentRect, out var screenRect)
            ? GUI.Toggle(screenRect, value, text, style)
            : value;

    [HideFromIl2Cpp]
    private void DrawManualPanel(Rect viewport, Vector2 scroll, Rect contentRect, Color fill)
    {
        var screenRect = ManualContentRect(viewport, contentRect, scroll);
        var visibleRect = IntersectRects(viewport, screenRect);
        if (visibleRect.width > 0f && visibleRect.height > 0f) DrawOpaquePanel(visibleRect, fill);
    }

    [HideFromIl2Cpp]
    private Rect ManualScrollbarTrack(Rect viewport)
    {
        var width = Math.Max(S(8f), 8f);
        return new Rect(viewport.xMax - width, viewport.y + S(2f), width, Math.Max(1f, viewport.height - S(4f)));
    }

    [HideFromIl2Cpp]
    private void DrawManualScrollbar(Rect viewport, float contentHeight, Vector2 scroll)
    {
        if (contentHeight <= viewport.height + 0.5f) return;
        var track = ManualScrollbarTrack(viewport);
        DrawSolidBox(track, new Color(0.10f, 0.11f, 0.14f, 0.96f));
        var thumbHeight = Math.Max(S(24f), track.height * viewport.height / contentHeight);
        var maximum = Math.Max(1f, contentHeight - viewport.height);
        var travel = Math.Max(0f, track.height - thumbHeight);
        var thumb = new Rect(track.x, track.y + travel * Mathf.Clamp01(scroll.y / maximum), track.width, thumbHeight);
        DrawSolidBox(thumb, new Color(0.42f, 0.58f, 0.76f, 1f));
    }

    [HideFromIl2Cpp]
    private void DrawSolidBox(Rect rect, Color fill)
    {
        var previousColor = GUI.color;
        var previousBackground = GUI.backgroundColor;
        GUI.color = Color.white;
        GUI.backgroundColor = fill;
        GUI.Box(rect, GUIContent.none, solidFillStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.color = previousColor;
    }

    [HideFromIl2Cpp]
    private void DrawResponsiveSearchPanel(Rect viewport, bool tooltipOwnsPointer)
    {
        var contentWidth = Math.Max(S(300f), viewport.width - S(18f));
        var gap = S(7f);
        var y = S(2f);
        var searchRect = new Rect(0f, y, contentWidth, S(38f));
        y = searchRect.yMax + gap;

        var warehouseWidth = Mathf.Clamp(contentWidth * 0.32f, S(158f), S(232f));
        var refreshWidth = S(92f);
        var clearWidth = S(112f);
        var inventoryCount = 0;
        var warehouseCount = 0;
        var selectedMatches = new List<ItemSearchRecord>();
        foreach (var item in matches)
        {
            if (item.StorageKind == StorageKind.Inventory) inventoryCount++; else warehouseCount++;
            if (item.StorageKind == selectedStorage) selectedMatches.Add(item);
        }
        // Search owns a compact count, not the verbose result text from Auto
        // Build or Bulk Open. Reusing the shared status string here created a
        // narrow, log-like column on the right side of the Search page.
        var searchScope = searchOptionsOnly
            ? UiText.L(" · 옵션", " · affixes", " · 词缀", " · 詞綴")
            : string.Empty;
        var searchStatus = UiText.L(
            $"일치 {matches.Count}개 · I {inventoryCount} / W {warehouseCount}{searchScope}",
            $"Matches {matches.Count} · I {inventoryCount} / W {warehouseCount}{searchScope}",
            $"匹配 {matches.Count} · I {inventoryCount} / W {warehouseCount}{searchScope}",
            $"相符 {matches.Count} · I {inventoryCount} / W {warehouseCount}{searchScope}");
        var toolbarFixedWidth = warehouseWidth + refreshWidth + clearWidth + gap * 3f;
        var statusInline = contentWidth - toolbarFixedWidth >= S(112f);
        var statusWidth = statusInline ? contentWidth - toolbarFixedWidth : contentWidth;
        var statusHeight = Math.Max(S(30f), WrappedHeight(searchStatus, hintStyle!, statusWidth, S(30f)));
        var toolbarHeight = statusInline ? Math.Max(S(32f), statusHeight) : S(32f) + gap + statusHeight;
        var toolbarTop = y;
        y += toolbarHeight + gap;

        var qualityHeight = ResponsiveQualityFiltersHeight(contentWidth);
        var qualityRect = new Rect(0f, y, contentWidth, qualityHeight);
        y += qualityHeight + gap;

        var storageTop = y;
        y += S(34f) + gap;
        var resultTop = y;
        var rowStep = S(88f);
        var visibleResultSpace = Math.Max(rowStep, viewport.height - resultTop - S(42f));
        var resultsPerPage = Math.Clamp((int)Math.Floor(visibleResultSpace / rowStep), 1, 12);
        var pageCount = Math.Max(1, (int)Math.Ceiling(selectedMatches.Count / (double)resultsPerPage));
        currentPage = Math.Clamp(currentPage, 0, pageCount - 1);
        // The responsive renderer uses a manually clipped list rather than a
        // Unity ScrollView. Route wheel input over the visible result rows to
        // pagination before the enclosing body scroller sees the event. The
        // focused Harmony guards independently keep this same wheel tick from
        // reaching the game's character selector.
        var wheelEvent = Event.current;
        var resultViewport = IntersectRects(
            viewport,
            new Rect(viewport.x, viewport.y + resultTop - searchBodyScroll.y,
                viewport.width, visibleResultSpace));
        if (pageCount > 1
            && wheelEvent is not null
            && wheelEvent.type == EventType.ScrollWheel
            && resultViewport.Contains(wheelEvent.mousePosition))
        {
            var direction = wheelEvent.delta.y > 0f ? 1 : wheelEvent.delta.y < 0f ? -1 : 0;
            if (direction != 0)
            {
                currentPage = Math.Clamp(currentPage + direction, 0, pageCount - 1);
                overlayInputFocused = true;
                wheelEvent.Use();
            }
        }
        var pageStart = currentPage * resultsPerPage;
        var pageItemCount = Math.Min(resultsPerPage, Math.Max(0, selectedMatches.Count - pageStart));
        var resultHeight = pageItemCount > 0 ? pageItemCount * rowStep : S(92f);
        var navTop = resultTop + resultHeight + gap;
        var contentHeight = Math.Max(viewport.height, navTop + S(34f) + S(6f));

        UpdateManualScroll(viewport, contentHeight, ref searchBodyScroll);
        DrawOpaquePanel(viewport, new Color(0.075f, 0.08f, 0.105f, 1f));

        if (TryManualControlRect(viewport, searchBodyScroll, searchRect, out var searchScreenRect))
        {
            HandleSearchInput(searchScreenRect);
            GUI.Box(searchScreenRect, GUIContent.none, searchStyle!);
            if (focusSearch)
            {
                EnableImeMode();
                Input.compositionCursorPos = new Vector2(
                    searchScreenRect.x + S(12f),
                    searchScreenRect.yMax + S(4f));
            }
            if (!string.IsNullOrEmpty(query) || focusSearch)
            {
                var textRect = new Rect(
                    searchScreenRect.x + S(10f),
                    searchScreenRect.y + S(6f),
                    Math.Max(S(20f), searchScreenRect.width - S(20f)),
                    Math.Max(S(20f), searchScreenRect.height - S(12f)));
                GUI.Label(textRect, BuildVisibleSearchText(textRect.width), searchTextStyle!);
            }
        }
        else if (focusSearch)
        {
            focusSearch = false;
            RestoreImeMode();
        }
        if (string.IsNullOrEmpty(query) && !focusSearch)
        {
            var placeholder = UiText.L(
                "아이템 이름, 등급, 부위, 옵션 검색…",
                "Search name, quality, slot, or affix…",
                "搜索名称、品质、部位或词缀…",
                "搜尋名稱、品質、部位或詞綴…");
            var previousTextColor = searchTextStyle!.normal.textColor;
            searchTextStyle.normal.textColor = new Color(0.52f, 0.52f, 0.56f);
            DrawManualLabel(
                viewport,
                searchBodyScroll,
                new Rect(searchRect.x + S(10f), searchRect.y + S(7f), searchRect.width - S(20f), S(24f)),
                placeholder,
                searchTextStyle);
            searchTextStyle.normal.textColor = previousTextColor;
        }

        var nextWarehouse = DrawManualToggle(
            viewport,
            searchBodyScroll,
            new Rect(0f, toolbarTop + S(1f), warehouseWidth, S(30f)),
            includeWarehouse,
            UiText.L(" 창고·Vault 포함", " Include warehouse and Vault", " 包含仓库与宝库", " 包含倉庫與寶庫"),
            toggleStyle!);
        if (nextWarehouse != includeWarehouse)
        {
            includeWarehouse = nextWarehouse;
            Plugin.IncludeWarehouse.Value = includeWarehouse;
            if (!includeWarehouse) selectedStorage = StorageKind.Inventory;
            currentPage = 0;
            searchBodyScroll.y = 0f;
            RefreshItems();
        }
        var refreshX = warehouseWidth + gap;
        var clearX = refreshX + refreshWidth + gap;
        if (DrawManualButton(viewport, searchBodyScroll, new Rect(refreshX, toolbarTop, refreshWidth, S(30f)), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshItems();
        if (DrawManualButton(viewport, searchBodyScroll, new Rect(clearX, toolbarTop, clearWidth, S(30f)), UiText.L("검색어 지우기", "Clear search", "清除搜索", "清除搜尋"), compactButtonStyle!))
        {
            query = string.Empty;
            searchCaret = 0;
            Plugin.SavedQuery.Value = query;
            focusSearch = true;
            currentPage = 0;
            searchBodyScroll.y = 0f;
            ApplyFilter();
        }
        var statusRect = statusInline
            ? new Rect(clearX + clearWidth + gap, toolbarTop, statusWidth, statusHeight)
            : new Rect(0f, toolbarTop + S(32f) + gap, statusWidth, statusHeight);
        DrawManualLabel(viewport, searchBodyScroll, statusRect, searchStatus, hintStyle!);

        DrawResponsiveQualityFilters(viewport, searchBodyScroll, qualityRect);

        var storageGap = S(8f);
        var storageWidth = (contentWidth - storageGap) / 2f;
        var inventoryTabRect = new Rect(0f, storageTop, storageWidth, S(34f));
        if (TryManualControlRect(viewport, searchBodyScroll, inventoryTabRect, out var inventoryTabScreenRect))
            DrawStorageTab(inventoryTabScreenRect, StorageKind.Inventory,
                $"{UiText.L("인벤토리", "INVENTORY", "背包", "背包")}  {inventoryCount}", new Color(0.32f, 0.86f, 0.46f));
        var warehouseTabRect = new Rect(storageWidth + storageGap, storageTop, storageWidth, S(34f));
        if (TryManualControlRect(viewport, searchBodyScroll, warehouseTabRect, out var warehouseTabScreenRect))
            DrawStorageTab(warehouseTabScreenRect, StorageKind.Warehouse,
                $"{UiText.L("보관함", "WAREHOUSE / VAULT", "仓库 / 宝库", "倉庫 / 寶庫")}  {warehouseCount}", new Color(0.25f, 0.78f, 0.92f));

        if (selectedMatches.Count == 0)
        {
            var emptyText = allItems.Count == 0
                ? UiText.L("인벤토리 데이터를 기다리는 중입니다.", "Waiting for inventory data.", "正在等待背包数据。", "正在等待背包資料。")
                : UiText.L("이 구역에는 검색 조건에 맞는 아이템이 없습니다.", "No matching items in this section.", "此区域没有匹配的物品。", "此區域沒有相符的物品。");
            DrawManualLabel(viewport, searchBodyScroll, new Rect(S(12f), resultTop + S(24f), contentWidth - S(24f), S(48f)), emptyText, hintStyle!);
        }
        else
        {
            for (var index = 0; index < pageItemCount; index++)
            {
                var item = selectedMatches[pageStart + index];
                var rowRect = new Rect(0f, resultTop + index * rowStep, contentWidth, S(82f));
                if (TryManualControlRect(viewport, searchBodyScroll, rowRect, out var rowScreenRect))
                    DrawResult(item, rowScreenRect, null, viewport, tooltipOwnsPointer);
            }
        }

        var previousWidth = S(104f);
        var pageNumberWidth = S(96f);
        var nextWidth = S(104f);
        var navGap = S(8f);
        var navWidth = previousWidth + pageNumberWidth + nextWidth + navGap * 2f;
        var navX = Math.Max(0f, (contentWidth - navWidth) / 2f);
        var navEnabled = GUI.enabled;
        GUI.enabled = navEnabled && currentPage > 0;
        if (DrawManualButton(viewport, searchBodyScroll, new Rect(navX, navTop, previousWidth, S(30f)), UiText.L("◀ 이전", "◀ Previous", "◀ 上一页", "◀ 上一頁"), compactButtonStyle!))
        {
            currentPage--;
            searchBodyScroll.y = 0f;
        }
        GUI.enabled = navEnabled;
        DrawManualLabel(viewport, searchBodyScroll, new Rect(navX + previousWidth + navGap, navTop, pageNumberWidth, S(30f)), $"{currentPage + 1} / {pageCount}", pageStyle!);
        GUI.enabled = navEnabled && currentPage + 1 < pageCount;
        if (DrawManualButton(viewport, searchBodyScroll, new Rect(navX + previousWidth + pageNumberWidth + navGap * 2f, navTop, nextWidth, S(30f)), UiText.L("다음 ▶", "Next ▶", "下一页 ▶", "下一頁 ▶"), compactButtonStyle!))
        {
            currentPage++;
            searchBodyScroll.y = 0f;
        }
        GUI.enabled = navEnabled;
        DrawManualScrollbar(viewport, contentHeight, searchBodyScroll);
    }

    private float ResponsiveQualityFiltersHeight(float width)
    {
        var gap = S(6f);
        var columns = Math.Clamp((int)Math.Floor((width + gap) / Math.Max(S(72f), S(104f))), 2, 5);
        var rows = (int)Math.Ceiling(10d / columns);
        return S(26f) + rows * S(34f) + S(30f) + gap * 2f;
    }

    [HideFromIl2Cpp]
    private void DrawResponsiveQualityFilters(Rect viewport, Vector2 scroll, Rect rect)
    {
        var entries = new (int Quality, string Label)[]
        {
            (0, UiText.L("전체", "All", "全部", "全部")),
            (1, UiText.L("일반", "Common", "普通", "普通")),
            (2, UiText.L("고급", "Fine", "精良", "精良")),
            (3, UiText.L("희귀", "Rare", "稀有", "稀有")),
            (4, UiText.L("전설", "Legend", "传奇", "傳奇")),
            (5, UiText.L("신화", "Mythic", "神话", "神話")),
            (6, UiText.L("세트", "Set", "套装", "套裝")),
            (7, UiText.L("마법", "Magic", "魔法", "魔法")),
            (8, UiText.L("고유", "Unique", "独特", "獨特")),
            (-1, UiText.L("기타", "Other", "其他", "其他"))
        };
        var gap = S(6f);
        var columns = Math.Clamp((int)Math.Floor((rect.width + gap) / Math.Max(S(72f), S(104f))), 2, 5);
        var buttonWidth = (rect.width - gap * (columns - 1)) / columns;
        DrawManualLabel(viewport, scroll, new Rect(0f, rect.y, rect.width, S(24f)), UiText.L("등급", "Quality", "品质", "品質"), utilityTitleStyle!);
        var buttonsTop = rect.y + S(26f);
        for (var index = 0; index < entries.Length; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var buttonRect = new Rect(column * (buttonWidth + gap), buttonsTop + row * S(34f), buttonWidth, S(30f));
            if (TryManualControlRect(viewport, scroll, buttonRect, out var buttonScreenRect))
                DrawQualityButton(buttonScreenRect, entries[index].Quality, entries[index].Label);
        }
        var rows = (int)Math.Ceiling(entries.Length / (double)columns);
        var toggleTop = buttonsTop + rows * S(34f) + gap;
        var nextOptionsOnly = DrawManualToggle(viewport, scroll, new Rect(0f, toggleTop, rect.width, S(28f)), searchOptionsOnly, UiText.L(" 옵션만 검색", " Affixes only", " 仅搜索词缀", " 僅搜尋詞綴"), toggleStyle!);
        if (nextOptionsOnly != searchOptionsOnly)
        {
            searchOptionsOnly = nextOptionsOnly;
            Plugin.SearchOptionsOnly.Value = searchOptionsOnly;
            currentPage = 0;
            searchBodyScroll.y = 0f;
            ApplyFilter();
        }
    }

    private float WrappedHeight(string? text, GUIStyle style, float width, float minimum)
    {
        return Math.Max(minimum, style.CalcHeight(new GUIContent(text ?? string.Empty), Math.Max(S(40f), width)));
    }

    [HideFromIl2Cpp]
    private void DrawResponsiveBulkOpenPanel(Rect viewport)
    {
        if (armedBulkOpen != BulkToolKind.None && Time.unscaledTime > bulkConfirmUntil)
            armedBulkOpen = BulkToolKind.None;
        if (warehouseSaleArmed && Time.unscaledTime > warehouseSaleConfirmUntil)
            warehouseSaleArmed = false;

        var contentWidth = Math.Max(S(300f), viewport.width - S(18f));
        var padding = S(18f);
        var innerWidth = Math.Max(S(220f), contentWidth - padding * 2f);
        var gap = S(8f);
        var title = UiText.L("보유 상자 일괄 개봉", "Open Owned Boxes in Bulk", "批量开启持有的箱子", "批次開啟持有的箱子");
        var description = UiText.L(
            "아래 등급은 상자 자체를 거르는 조건입니다. 상자에서 나올 보상 등급을 정하는 기능은 아닙니다.",
            "The quality filter selects the boxes themselves; it does not control reward quality.",
            "下方品质筛选的是箱子本身，并不会决定开出的奖励品质。",
            "下方品質篩選的是箱子本身，並不會決定開出的獎勵品質。");
        var skipLabel = UiText.L(" 2단계 확인 생략 (판매 제외)", " Skip second confirmation (except Sell)", " 跳过二次确认（出售除外）", " 略過二次確認（出售除外）");
        var destinationTitle = UiText.L("장비 상자 보상 처리", "Gear-box reward destination", "装备箱奖励处理", "裝備箱獎勵處理");
        var destinationInfo = BulkDestinationDescription(Plugin.OpenedEquipmentDestination.Value);
        var detailButtonWidth = S(94f);
        var titleWidth = Math.Max(S(90f), innerWidth - detailButtonWidth - gap);
        var titleHeight = WrappedHeight(title, titleStyle!, titleWidth, S(28f));
        var descriptionHeight = WrappedHeight(description, hintStyle!, innerWidth, S(38f));
        var skipHeight = WrappedHeight(skipLabel, toggleStyle!, innerWidth, S(28f));
        var destinationTitleHeight = WrappedHeight(destinationTitle, utilityTitleStyle!, innerWidth, S(24f));
        var destinationButtonHeight = S(38f);
        var destinationInfoHeight = WrappedHeight(destinationInfo, hintStyle!, innerWidth, S(34f));
        var warehouseSaleDescription = UiText.L(
            "일반 Warehouse에 있는 판매 가능 장비를 모두 판매합니다. Vault·인벤토리·착용 장비는 대상이 아니며, 항목마다 즉시 판매하고 검증합니다.",
            "Sell every sellable gear item in ordinary Warehouse. Vault, inventory, and equipped gear are excluded; each item is sold and verified immediately.",
            "出售普通仓库中的所有可出售装备。宝库、背包和已装备物品不在范围内；每件物品都会立即出售并验证。",
            "出售普通倉庫中的所有可出售裝備。寶庫、背包和已裝備物品不在範圍內；每件物品都會立即出售並驗證。");
        var warehouseSaleDescriptionHeight = WrappedHeight(
            warehouseSaleDescription, hintStyle!, innerWidth, S(42f));
        var warehouseSaleBlockHeight = S(48f) + S(4f) + warehouseSaleDescriptionHeight;
        var twoActionColumns = innerWidth >= S(420f);
        var actionBlockHeight = twoActionColumns ? S(56f) : S(120f);
        var qualityEntries = new (int Quality, string Label)[]
        {
            (0, UiText.L("전체", "All", "全部", "全部")),
            (1, UiText.L("일반", "Common", "普通", "普通")),
            (2, UiText.L("고급", "Fine", "精良", "精良")),
            (3, UiText.L("희귀", "Rare", "稀有", "稀有")),
            (4, UiText.L("전설", "Legend", "传奇", "傳奇")),
            (5, UiText.L("신화", "Mythic", "神话", "神話")),
            (6, UiText.L("세트", "Set", "套装", "套裝")),
            (7, UiText.L("마법", "Magic", "魔法", "魔法")),
            (8, UiText.L("고유", "Unique", "独特", "獨特")),
            (-1, UiText.L("기타", "Other", "其他", "其他"))
        };
        var qualityGap = S(6f);
        var qualityColumns = Math.Clamp((int)Math.Floor((innerWidth + qualityGap) / Math.Max(S(72f), S(104f))), 2, 5);
        var qualityRows = (int)Math.Ceiling(qualityEntries.Length / (double)qualityColumns);
        var qualityBlockHeight = S(26f) + qualityRows * S(38f);
        var atLeastLabel = UiText.L(" 선택한 최저 등급 이상 모두", " Open the selected minimum quality or higher", " 开启所选最低品质及以上", " 開啟所選最低品質以上");
        var atLeastHeight = WrappedHeight(atLeastLabel, toggleStyle!, innerWidth, S(30f));
        var hasRunningBulkOperation = warehouseSaleSession is not null || bulkSession is not null;
        var runningText = warehouseSaleSession is not null
            ? WarehouseEquipmentSaleProgressText(warehouseSaleSession)
            : bulkSession is null ? status : BulkOpenProgressText(bulkSession);
        var cancelWidth = bulkSession is null ? 0f : Math.Min(S(112f), innerWidth * 0.28f);
        var statusWidth = Math.Max(S(80f), innerWidth - (cancelWidth > 0f ? cancelWidth + gap : 0f));
        var showStatusBody = hasRunningBulkOperation || Plugin.ShowDetailedStatus.Value;
        var statusHeight = showStatusBody
            ? WrappedHeight(runningText, tooltipBodyStyle!, statusWidth, S(56f))
            : 0f;

        var contentHeight = padding + titleHeight + gap + descriptionHeight + gap
                            + skipHeight + gap + destinationTitleHeight + S(4f)
                            + destinationButtonHeight + S(4f) + destinationInfoHeight + gap
                            + warehouseSaleBlockHeight + gap
                            + actionBlockHeight + gap * 2f + qualityBlockHeight + gap
                            + atLeastHeight + gap * 2f + statusHeight + padding;
        contentHeight = Math.Max(viewport.height, contentHeight);
        UpdateManualScroll(viewport, contentHeight, ref bulkBodyScroll);
        DrawOpaquePanel(viewport, new Color(0.10f, 0.105f, 0.13f, 1f));

        var y = padding;
        DrawManualLabel(viewport, bulkBodyScroll, new Rect(padding, y, titleWidth, titleHeight), title, titleStyle!);
        if (DrawManualButton(
                viewport,
                bulkBodyScroll,
                new Rect(padding + innerWidth - detailButtonWidth, y, detailButtonWidth, S(32f)),
                DetailedStatusButtonLabel(),
                compactButtonStyle!))
            ToggleDetailedStatus();
        y += titleHeight + gap;
        DrawManualLabel(viewport, bulkBodyScroll, new Rect(padding, y, innerWidth, descriptionHeight), description, hintStyle!);
        y += descriptionHeight + gap;

        var panelEnabled = GUI.enabled;
        GUI.enabled = panelEnabled && bulkSession is null && warehouseSaleSession is null;
        var nextSkip = DrawManualToggle(viewport, bulkBodyScroll, new Rect(padding, y, innerWidth, skipHeight), Plugin.SkipBulkConfirmation.Value, skipLabel, toggleStyle!);
        if (nextSkip != Plugin.SkipBulkConfirmation.Value)
        {
            Plugin.SkipBulkConfirmation.Value = nextSkip;
            armedBulkOpen = BulkToolKind.None;
            status = nextSkip
                ? UiText.L("일괄 개봉 확인을 생략합니다.", "Bulk opening will run with one click.", "批量开启将单击执行。", "批次開啟將單擊執行。")
                : UiText.L("일괄 개봉은 두 번 눌러야 실행됩니다.", "Bulk opening requires two clicks.", "批量开启需要点击两次。", "批次開啟需要點擊兩次。");
        }
        y += skipHeight + gap;
        DrawManualLabel(viewport, bulkBodyScroll, new Rect(padding, y, innerWidth, destinationTitleHeight), destinationTitle, utilityTitleStyle!);
        y += destinationTitleHeight + S(4f);
        var destinationGap = S(6f);
        var destinationWidth = (innerWidth - destinationGap * 2f) / 3f;
        var destinations = new (BulkEquipmentDestination Destination, string Label)[]
        {
            (BulkEquipmentDestination.Inventory, UiText.L("인벤토리", "Inventory", "背包", "背包")),
            (BulkEquipmentDestination.Warehouse, UiText.L("창고·Vault", "Warehouse", "仓库", "倉庫")),
            (BulkEquipmentDestination.Sell, UiText.L("Vault+판매", "Vault + Sell", "宝库+出售", "寶庫+出售"))
        };
        for (var index = 0; index < destinations.Length; index++)
        {
            var destinationRect = new Rect(padding + index * (destinationWidth + destinationGap), y, destinationWidth, destinationButtonHeight);
            if (TryManualControlRect(viewport, bulkBodyScroll, destinationRect, out var destinationScreenRect))
                DrawBulkDestinationButton(destinationScreenRect, destinations[index].Destination, destinations[index].Label);
        }
        y += destinationButtonHeight + S(4f);
        DrawManualLabel(viewport, bulkBodyScroll, new Rect(padding, y, innerWidth, destinationInfoHeight), destinationInfo, hintStyle!);
        y += destinationInfoHeight + gap;

        GUI.enabled = panelEnabled;
        var warehouseSaleRect = new Rect(padding, y, innerWidth, S(48f));
        if (TryManualControlRect(viewport, bulkBodyScroll, warehouseSaleRect, out var warehouseSaleScreenRect))
            DrawWarehouseEquipmentSaleButton(warehouseSaleScreenRect);
        GUI.enabled = panelEnabled && bulkSession is null && warehouseSaleSession is null;
        y += S(48f) + S(4f);
        DrawManualLabel(
            viewport,
            bulkBodyScroll,
            new Rect(padding, y, innerWidth, warehouseSaleDescriptionHeight),
            warehouseSaleDescription,
            hintStyle!);
        y += warehouseSaleDescriptionHeight + gap;

        if (twoActionColumns)
        {
            var actionWidth = (innerWidth - gap) / 2f;
            var equipmentRect = new Rect(padding, y, actionWidth, S(56f));
            if (TryManualControlRect(viewport, bulkBodyScroll, equipmentRect, out var equipmentScreenRect))
                DrawBulkOpenButton(equipmentScreenRect, BulkToolKind.EquipmentBox, equipmentBoxCount, UiText.L("장비 상자", "Gear boxes", "装备箱", "裝備箱"));
            var runeRect = new Rect(padding + actionWidth + gap, y, actionWidth, S(56f));
            if (TryManualControlRect(viewport, bulkBodyScroll, runeRect, out var runeScreenRect))
                DrawBulkOpenButton(runeScreenRect, BulkToolKind.RuneBox, runeBoxCount, UiText.L("룬 상자", "Rune boxes", "符文箱", "符文箱"));
        }
        else
        {
            var equipmentRect = new Rect(padding, y, innerWidth, S(56f));
            if (TryManualControlRect(viewport, bulkBodyScroll, equipmentRect, out var equipmentScreenRect))
                DrawBulkOpenButton(equipmentScreenRect, BulkToolKind.EquipmentBox, equipmentBoxCount, UiText.L("장비 상자", "Gear boxes", "装备箱", "裝備箱"));
            var runeRect = new Rect(padding, y + S(64f), innerWidth, S(56f));
            if (TryManualControlRect(viewport, bulkBodyScroll, runeRect, out var runeScreenRect))
                DrawBulkOpenButton(runeScreenRect, BulkToolKind.RuneBox, runeBoxCount, UiText.L("룬 상자", "Rune boxes", "符文箱", "符文箱"));
        }
        y += actionBlockHeight + gap * 2f;

        DrawManualLabel(viewport, bulkBodyScroll, new Rect(padding, y, innerWidth, S(24f)), UiText.L("상자 자체 등급", "Box quality", "箱子自身品质", "箱子本身品質"), utilityTitleStyle!);
        y += S(26f);
        var qualityWidth = (innerWidth - qualityGap * (qualityColumns - 1)) / qualityColumns;
        for (var index = 0; index < qualityEntries.Length; index++)
        {
            var row = index / qualityColumns;
            var column = index % qualityColumns;
            var qualityRect = new Rect(padding + column * (qualityWidth + qualityGap), y + row * S(38f), qualityWidth, S(34f));
            if (TryManualControlRect(viewport, bulkBodyScroll, qualityRect, out var qualityScreenRect))
                DrawBulkQualityButton(qualityScreenRect, qualityEntries[index].Quality, qualityEntries[index].Label);
        }
        y += qualityRows * S(38f) + gap;
        var nextAtLeast = DrawManualToggle(viewport, bulkBodyScroll, new Rect(padding, y, innerWidth, atLeastHeight), Plugin.BulkQualityAtLeast.Value, atLeastLabel, toggleStyle!);
        if (nextAtLeast != Plugin.BulkQualityAtLeast.Value)
        {
            Plugin.BulkQualityAtLeast.Value = nextAtLeast;
            if (nextAtLeast && Plugin.BulkQualityMask.Value != 0)
            {
                var lowestSelected = Enumerable.Range(1, 8).FirstOrDefault(quality =>
                    QualityFilterLogic.IsSelected(Plugin.BulkQualityMask.Value, quality));
                if (lowestSelected > 0) Plugin.BulkQualityMask.Value = QualityFilterLogic.BitFor(lowestSelected);
            }
            armedBulkOpen = BulkToolKind.None;
            RefreshBulkCounts();
        }
        GUI.enabled = panelEnabled;
        y += atLeastHeight + gap * 2f;
        if (showStatusBody)
            DrawManualLabel(viewport, bulkBodyScroll, new Rect(padding, y, statusWidth, statusHeight), runningText, tooltipBodyStyle!);
        if (bulkSession is not null && DrawManualButton(viewport, bulkBodyScroll, new Rect(padding + statusWidth + gap, y, cancelWidth, S(34f)), UiText.L("중단", "Cancel", "取消", "取消"), buttonStyle!))
            bulkSession.CancelRequested = true;
        DrawManualScrollbar(viewport, contentHeight, bulkBodyScroll);
    }

    [HideFromIl2Cpp]
    private void DrawResponsiveAutoBuildPanel(Rect viewport)
    {
        if (armedAutoBuild != AutoBuildAction.None && Time.unscaledTime > autoBuildConfirmUntil)
            armedAutoBuild = AutoBuildAction.None;

        var contentWidth = Math.Max(S(300f), viewport.width - S(18f));
        var padding = S(20f);
        var innerWidth = Math.Max(S(220f), contentWidth - padding * 2f);
        var gap = S(8f);
        var title = UiText.L("현재 선택 영웅 자동 최적화", "Optimize the Selected Hero", "自动优化当前英雄", "自動最佳化目前英雄");
        var intro = UiText.L(
            "게임에서 영웅을 먼저 선택하세요. 공격 테마는 피해 근사치, 방어 테마는 생존 능력치를 우선하며 공식 추천 장비에는 가산점을 주지 않습니다.",
            "Select a hero first. Damage themes prioritize the damage proxy; Defense prioritizes survival stats. Official guide gear gets no score bonus.",
            "请先选择英雄。伤害主题优先伤害近似值，防御主题优先生存属性；官方指南装备不获得评分加成。",
            "請先選擇英雄。傷害主題優先傷害近似值，防禦主題優先生存屬性；官方指南裝備不獲得評分加成。");
        var refreshWidth = S(96f);
        var detailButtonWidth = S(94f);
        var titleWidth = Math.Max(
            S(80f),
            innerWidth - refreshWidth - detailButtonWidth - gap * 2f);
        var titleHeight = WrappedHeight(title, titleStyle!, titleWidth, S(32f));
        var introHeight = WrappedHeight(intro, hintStyle!, innerWidth, S(42f));
        var autoStartLabel = UiText.L(
            " 시작할 때 Continue Game 실행 후 활동 중인 파티 Auto 켜기",
            " Continue Game and enable Auto for active parties on startup",
            " 启动时继续游戏并为活动队伍启用 Auto",
            " 啟動時繼續遊戲並為活動隊伍啟用 Auto");
        var autoStartHeight = WrappedHeight(autoStartLabel, toggleStyle!, innerWidth, S(34f));
        var autoStartStatus = StartupAutoController.GetStatusLabel();
        var autoStartStatusHeight = WrappedHeight(autoStartStatus, hintStyle!, innerWidth, S(28f));
        var startInBackgroundLabel = UiText.L(
            " 다음 실행부터 플러그인 로드 후 게임 창을 다른 창 뒤로 보내기",
            " On the next launch, place the game behind other windows after the plugin loads",
            " 下次启动时，在插件加载后将游戏窗口置于其他窗口之后",
            " 下次啟動時，在外掛載入後將遊戲視窗置於其他視窗之後");
        var startInBackgroundHeight = WrappedHeight(
            startInBackgroundLabel, toggleStyle!, innerWidth, S(34f));
        var backgroundHotkeyLabel = UiText.L(
            " 백그라운드 F3 / Ctrl+F 활성화 (시스템 전역 단축키)",
            " Activate with background F3 / Ctrl+F (system-wide shortcuts)",
            " 使用后台 F3 / Ctrl+F 激活（系统全局快捷键）",
            " 使用背景 F3 / Ctrl+F 啟用（系統全域快速鍵）");
        var backgroundHotkeyHeight = WrappedHeight(
            backgroundHotkeyLabel, toggleStyle!, innerWidth, S(34f));
        Plugin.GetBackgroundActivationHotkeyRegistration(
            out var backgroundF3Registered,
            out var backgroundCtrlFRegistered);
        var backgroundHotkeyStatus = Plugin.ActivateFromBackgroundHotkeys.Value
            ? backgroundF3Registered && backgroundCtrlFRegistered
                ? UiText.L(
                    "F3와 Ctrl+F가 등록됐습니다. 다른 앱에서 누르면 게임이 앞으로 옵니다.",
                    "F3 and Ctrl+F are registered. Pressing either in another app brings the game forward.",
                    "F3 和 Ctrl+F 已注册。在其他应用中按下任一快捷键会将游戏置于前台。",
                    "F3 與 Ctrl+F 已註冊。在其他應用程式中按下任一快速鍵會將遊戲移到前景。")
                : UiText.L(
                    $"단축키 등록 상태: F3 {(backgroundF3Registered ? "사용 가능" : "충돌")} · Ctrl+F {(backgroundCtrlFRegistered ? "사용 가능" : "충돌")}",
                    $"Shortcut registration: F3 {(backgroundF3Registered ? "ready" : "conflict")} · Ctrl+F {(backgroundCtrlFRegistered ? "ready" : "conflict")}",
                    $"快捷键注册：F3 {(backgroundF3Registered ? "可用" : "冲突")} · Ctrl+F {(backgroundCtrlFRegistered ? "可用" : "冲突")}",
                    $"快速鍵註冊：F3 {(backgroundF3Registered ? "可用" : "衝突")} · Ctrl+F {(backgroundCtrlFRegistered ? "可用" : "衝突")}")
            : UiText.L(
                "꺼져 있으면 F3/Ctrl+F는 게임이 활성화된 동안에만 작동합니다.",
                "When off, F3/Ctrl+F work only while the game is active.",
                "关闭时，F3/Ctrl+F 仅在游戏处于活动状态时生效。",
                "關閉時，F3/Ctrl+F 僅在遊戲處於作用中狀態時生效。");
        var backgroundHotkeyStatusHeight = WrappedHeight(
            backgroundHotkeyStatus, hintStyle!, innerWidth, S(36f));
        var heroName = string.IsNullOrWhiteSpace(selectedHeroSummary)
            ? UiText.L("선택된 영웅 없음", "No hero selected", "未选择英雄", "未選擇英雄")
            : selectedHeroSummary;
        var heroTitleHeight = WrappedHeight(heroName, utilityTitleStyle!, innerWidth - S(28f), S(26f));
        var heroProfileHeight = WrappedHeight(selectedHeroProfile, hintStyle!, innerWidth - S(28f), S(26f));
        var heroCardHeight = S(14f) + heroTitleHeight + S(4f) + heroProfileHeight + S(14f);

        var themes = new (string Key, string Label)[]
        {
            ("auto", UiText.L("자동", "Auto", "自动", "自動")),
            ("physical", UiText.L("물리", "Physical", "物理", "物理")),
            ("elemental", UiText.L("원소", "Elemental", "元素", "元素")),
            ("fire", UiText.L("화염", "Fire", "火焰", "火焰")),
            ("ice", UiText.L("냉기", "Ice", "冰霜", "冰霜")),
            ("lightning", UiText.L("번개", "Lightning", "闪电", "閃電")),
            ("minion", UiText.L("소환수", "Minion", "召唤", "召喚")),
            ("bleed", UiText.L("출혈", "Bleed", "流血", "流血")),
            ("corrosion", UiText.L("부식", "Corrosion", "腐蚀", "腐蝕")),
            ("crit", UiText.L("치명타", "Critical", "暴击", "暴擊")),
            ("support", UiText.L("지원", "Support", "辅助", "輔助")),
            ("defense", UiText.L("방어", "Defense", "防御", "防禦"))
        };
        var themeGap = S(6f);
        var themeColumns = Math.Clamp((int)Math.Floor((innerWidth + themeGap) / Math.Max(S(74f), S(104f))), 3, 6);
        var themeRows = (int)Math.Ceiling(themes.Length / (double)themeColumns);
        var themeBlockHeight = S(26f) + themeRows * S(36f);
        var themePriority = Plugin.NormalizeAutoBuildThemePriority(
            Plugin.AutoBuildThemePriority.Value);
        var themePriorityLabel = UiText.L(
            "테마 우선 비율 (0 일반 성능 · 100 선택 테마)",
            "Theme priority (0 general output · 100 selected theme)",
            "主题优先比例（0 通用性能 · 100 所选主题）",
            "主題優先比例（0 通用效能 · 100 所選主題）");
        var themePriorityBlockHeight = S(64f);
        var planningSeconds = Plugin.NormalizeAutoBuildPlanningSeconds(
            Plugin.AutoBuildPlanningSeconds.Value);
        var planningTimeLabel = UiText.L(
            "최고안 탐색 시간 (초 · 10 이상)",
            "Best-candidate search time (seconds · 10 minimum)",
            "最佳候选搜索时间（秒 · 最少 10）",
            "最佳候選搜尋時間（秒 · 最少 10）");
        var planningTimeBlockHeight = S(64f);
        var storageLabel = UiText.L(
            " 착용 중인 8부위와 창고·Vault 장비를 모두 후보에 포함",
            " Include equipped 8-slot gear, Warehouse, and Vault",
            " 将已装备的 8 个部位、仓库及宝库装备全部纳入候选",
            " 將已裝備的 8 個部位、倉庫及寶庫裝備全部納入候選");
        var forgeLabel = UiText.L(
            " 강화 수치 제외 (모든 장비를 0강 기준으로 비교)",
            " Ignore forge bonuses (compare every item at forge level 0)",
            " 排除强化数值（按全部装备 0 强比较）",
            " 排除強化數值（按全部裝備 0 強比較）");
        var transformLabel = UiText.L(
            $" 최종 조합에 필요한 미보유 스킬도 변환 시도 (최대 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)}회)",
            $" Transform missing skills required by the final build (max {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)})",
            $" 尝试转换最终方案所需的未拥有技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）",
            $" 嘗試轉換最終配置所需的未擁有技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）");
        var storageHeight = WrappedHeight(storageLabel, toggleStyle!, innerWidth, S(30f));
        var forgeHeight = WrappedHeight(forgeLabel, toggleStyle!, innerWidth, S(30f));
        var transformHeight = WrappedHeight(transformLabel, toggleStyle!, innerWidth, S(32f));
        var twoAdvancedColumns = innerWidth >= S(420f);
        var advancedHeight = twoAdvancedColumns ? S(48f) : S(104f);
        var explanation = AutoBuildPlanningExplanation(planningSeconds);
        var explanationHeight = WrappedHeight(explanation, tooltipBodyStyle!, innerWidth, S(48f));
        var caution = UiText.L(
            "주의: 스킬 변환·특성 초기화에는 게임의 정상 비용이 듭니다. 초기화 비용은 남겨 두며, 실행 버튼은 두 번 눌러야 합니다.",
            "Caution: skill transformation and talent reset use normal game costs. Reset cost is reserved; each action requires a second click.",
            "注意：技能转换与天赋重置会消耗游戏正常费用。系统会预留重置费用；操作需点击两次。",
            "注意：技能轉換與天賦重設會消耗遊戲正常費用。系統會保留重設費用；操作需點擊兩次。");

        var running = IsAutoBuildRunning ? autoBuildOperation : null;
        var tailHeight = 0f;
        var phase = string.Empty;
        var meta = string.Empty;
        var phaseWidth = innerWidth;
        var phaseHeight = 0f;
        var metaHeight = 0f;
        var cancelWidth = 0f;
        var cautionHeight = 0f;
        var statusHeight = 0f;
        if (running is not null)
        {
            cancelWidth = Math.Min(S(118f), Math.Max(S(88f), innerWidth * 0.25f));
            phaseWidth = Math.Max(S(80f), innerWidth - cancelWidth - gap);
            phase = AutoBuildPhaseLabel(running);
            meta = AutoBuildProgressMetaLabel(running);
            phaseHeight = WrappedHeight(phase, tooltipBodyStyle!, phaseWidth, S(30f));
            metaHeight = WrappedHeight(meta, hintStyle!, innerWidth, S(28f));
            tailHeight = phaseHeight + gap + S(10f) + gap + metaHeight;
        }
        else
        {
            cautionHeight = WrappedHeight(caution, hintStyle!, innerWidth, S(46f));
            if (Plugin.ShowDetailedStatus.Value)
                statusHeight = WrappedHeight(status, tooltipBodyStyle!, innerWidth, S(60f));
            tailHeight = cautionHeight
                         + (statusHeight > 0f ? gap + statusHeight : 0f);
        }

        var contentHeight = padding + Math.Max(titleHeight, S(32f)) + gap + introHeight + gap
                            + autoStartHeight + gap + autoStartStatusHeight + gap
                            + startInBackgroundHeight + gap
                            + backgroundHotkeyHeight + gap + backgroundHotkeyStatusHeight + gap * 2f
                            + heroCardHeight + gap * 2f + themeBlockHeight + gap
                            + themePriorityBlockHeight + gap
                            + planningTimeBlockHeight + gap
                            + storageHeight + gap
                            + forgeHeight + gap
                            + transformHeight + gap
                            + S(58f) + gap + advancedHeight + gap
                            + explanationHeight + gap * 2f + tailHeight + padding;
        contentHeight = Math.Max(viewport.height, contentHeight);
        UpdateManualScroll(viewport, contentHeight, ref autoBuildBodyScroll);
        DrawOpaquePanel(viewport, new Color(0.085f, 0.09f, 0.12f, 1f));

        var y = padding;
        var panelEnabled = GUI.enabled;
        DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, titleWidth, titleHeight), title, titleStyle!);
        if (DrawManualButton(
                viewport,
                autoBuildBodyScroll,
                new Rect(
                    padding + innerWidth - refreshWidth - gap - detailButtonWidth,
                    y,
                    detailButtonWidth,
                    S(32f)),
                DetailedStatusButtonLabel(),
                compactButtonStyle!))
            ToggleDetailedStatus();
        GUI.enabled = panelEnabled && !IsAutoBuildRunning;
        if (DrawManualButton(viewport, autoBuildBodyScroll, new Rect(padding + innerWidth - refreshWidth, y, refreshWidth, S(32f)), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshCurrentPage();
        GUI.enabled = panelEnabled;
        y += Math.Max(titleHeight, S(32f)) + gap;
        DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, introHeight), intro, hintStyle!);
        y += introHeight + gap;

        GUI.enabled = panelEnabled && !IsAutoBuildRunning;
        var nextAutoStart = DrawManualToggle(
            viewport, autoBuildBodyScroll,
            new Rect(padding, y, innerWidth, autoStartHeight),
            Plugin.AutoStart.Value, autoStartLabel, toggleStyle!);
        if (nextAutoStart != Plugin.AutoStart.Value)
        {
            Plugin.AutoStart.Value = nextAutoStart;
            StartupAutoController.OnSettingChanged(nextAutoStart);
        }
        y += autoStartHeight + gap;
        DrawManualLabel(viewport, autoBuildBodyScroll,
            new Rect(padding, y, innerWidth, autoStartStatusHeight), autoStartStatus, hintStyle!);
        y += autoStartStatusHeight + gap;

        var nextStartInBackground = DrawManualToggle(
            viewport, autoBuildBodyScroll,
            new Rect(padding, y, innerWidth, startInBackgroundHeight),
            Plugin.StartInBackground.Value,
            startInBackgroundLabel,
            toggleStyle!);
        if (nextStartInBackground != Plugin.StartInBackground.Value)
        {
            Plugin.StartInBackground.Value = nextStartInBackground;
            status = nextStartInBackground
                ? UiText.L(
                    "다음 실행부터 플러그인이 로드되면 게임 창을 뒤로 보냅니다.",
                    "On the next launch, the game will be placed behind other windows after the plugin loads.",
                    "下次启动时，插件加载后会将游戏窗口置于其他窗口之后。",
                    "下次啟動時，外掛載入後會將遊戲視窗置於其他視窗之後。")
                : UiText.L(
                    "다음 실행부터 시작 창 위치를 강제로 바꾸지 않습니다.",
                    "Starting with the next launch, the plugin will not force the startup window position.",
                    "从下次启动起，插件不会强制更改启动窗口位置。",
                    "從下次啟動起，外掛不會強制變更啟動視窗位置。");
        }
        y += startInBackgroundHeight + gap;

        var nextBackgroundHotkeys = DrawManualToggle(
            viewport, autoBuildBodyScroll,
            new Rect(padding, y, innerWidth, backgroundHotkeyHeight),
            Plugin.ActivateFromBackgroundHotkeys.Value,
            backgroundHotkeyLabel,
            toggleStyle!);
        if (nextBackgroundHotkeys != Plugin.ActivateFromBackgroundHotkeys.Value)
        {
            var available = Plugin.ConfigureBackgroundActivationHotkeys(
                nextBackgroundHotkeys,
                out var f3Registered,
                out var ctrlFRegistered);
            status = !nextBackgroundHotkeys
                ? UiText.L(
                    "시스템 전역 단축키를 해제했습니다.",
                    "System-wide shortcuts were disabled.",
                    "已停用系统全局快捷键。",
                    "已停用系統全域快速鍵。")
                : available
                    ? UiText.L(
                        $"백그라운드 단축키를 켰습니다: F3={f3Registered}, Ctrl+F={ctrlFRegistered}.",
                        $"Background shortcuts enabled: F3={f3Registered}, Ctrl+F={ctrlFRegistered}.",
                        $"后台快捷键已启用：F3={f3Registered}，Ctrl+F={ctrlFRegistered}。",
                        $"背景快速鍵已啟用：F3={f3Registered}，Ctrl+F={ctrlFRegistered}。")
                    : UiText.L(
                        "F3와 Ctrl+F를 다른 프로그램이 사용 중이라 등록하지 못했습니다.",
                        "F3 and Ctrl+F could not be registered because another program is using them.",
                        "F3 和 Ctrl+F 正被其他程序使用，无法注册。",
                        "F3 與 Ctrl+F 正由其他程式使用，無法註冊。");
        }
        y += backgroundHotkeyHeight + gap;
        DrawManualLabel(viewport, autoBuildBodyScroll,
            new Rect(padding, y, innerWidth, backgroundHotkeyStatusHeight),
            backgroundHotkeyStatus,
            hintStyle!);
        y += backgroundHotkeyStatusHeight + gap * 2f;

        DrawManualPanel(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, heroCardHeight), new Color(0.24f, 0.42f, 0.62f, 0.90f));
        DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding + S(14f), y + S(10f), innerWidth - S(28f), heroTitleHeight), heroName, utilityTitleStyle!);
        DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding + S(14f), y + S(10f) + heroTitleHeight + S(4f), innerWidth - S(28f), heroProfileHeight), selectedHeroProfile, hintStyle!);
        y += heroCardHeight + gap * 2f;

        DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, S(24f)), UiText.L("빌드 테마", "Build theme", "构筑主题", "流派主題"), utilityTitleStyle!);
        y += S(26f);
        var themeWidth = (innerWidth - themeGap * (themeColumns - 1)) / themeColumns;
        for (var index = 0; index < themes.Length; index++)
        {
            var row = index / themeColumns;
            var column = index % themeColumns;
            var themeRect = new Rect(padding + column * (themeWidth + themeGap), y + row * S(36f), themeWidth, S(32f));
            if (TryManualControlRect(viewport, autoBuildBodyScroll, themeRect, out var themeScreenRect))
                DrawAutoBuildThemeButton(themeScreenRect, themes[index].Key, themes[index].Label);
        }
        y += themeRows * S(36f) + gap;

        GUI.enabled = panelEnabled && !IsAutoBuildRunning;
        DrawManualLabel(viewport, autoBuildBodyScroll,
            new Rect(padding, y, innerWidth, S(24f)), themePriorityLabel, utilityTitleStyle!);
        y += S(28f);
        var priorityApplyWidth = S(68f);
        var priorityBalancedWidth = S(94f);
        var priorityThemeWidth = S(86f);
        var priorityInputWidth = Math.Max(
            S(70f),
            innerWidth - priorityApplyWidth - priorityBalancedWidth
                       - priorityThemeWidth - gap * 3f);
        var priorityInputRect = new Rect(padding, y, priorityInputWidth, S(34f));
        if (TryManualControlRect(viewport, autoBuildBodyScroll, priorityInputRect,
                out var priorityInputScreenRect))
        {
            HandleThemePriorityInput(priorityInputScreenRect);
            GUI.Box(priorityInputScreenRect, GUIContent.none, searchStyle!);
            GUI.Label(
                new Rect(
                    priorityInputScreenRect.x + S(5f),
                    priorityInputScreenRect.y + S(3f),
                    priorityInputScreenRect.width - S(10f),
                    priorityInputScreenRect.height - S(6f)),
                themePriorityInput + (focusThemePriorityInput ? "|" : string.Empty) + "%",
                badgeStyle!);
        }
        else focusThemePriorityInput = false;
        var priorityButtonX = padding + priorityInputWidth + gap;
        if (DrawManualButton(viewport, autoBuildBodyScroll,
                new Rect(priorityButtonX, y, priorityApplyWidth, S(34f)),
                UiText.L("적용", "Apply", "应用", "套用"), compactButtonStyle!))
            ApplyCustomThemePriority();
        priorityButtonX += priorityApplyWidth + gap;
        var previousPriorityBackground = GUI.backgroundColor;
        if (themePriority == 50) GUI.backgroundColor = new Color(0.34f, 0.78f, 0.58f);
        if (DrawManualButton(viewport, autoBuildBodyScroll,
                new Rect(priorityButtonX, y, priorityBalancedWidth, S(34f)),
                UiText.L("균형 50", "Balanced 50", "均衡 50", "均衡 50"), compactButtonStyle!))
            SetAutoBuildThemePriority(50);
        GUI.backgroundColor = previousPriorityBackground;
        priorityButtonX += priorityBalancedWidth + gap;
        if (themePriority == 100) GUI.backgroundColor = new Color(0.34f, 0.78f, 0.58f);
        if (DrawManualButton(viewport, autoBuildBodyScroll,
                new Rect(priorityButtonX, y, priorityThemeWidth, S(34f)),
                UiText.L("테마 100", "Theme 100", "主题 100", "主題 100"), compactButtonStyle!))
            SetAutoBuildThemePriority(100);
        GUI.backgroundColor = previousPriorityBackground;
        y += S(36f) + gap;

        GUI.enabled = panelEnabled && !IsAutoBuildRunning;
        DrawManualLabel(viewport, autoBuildBodyScroll,
            new Rect(padding, y, innerWidth, S(24f)), planningTimeLabel, utilityTitleStyle!);
        y += S(28f);
        var planningApplyWidth = S(68f);
        var planningUnlimitedWidth = S(102f);
        var planningDefaultWidth = S(74f);
        var planningInputWidth = Math.Max(
            S(88f),
            innerWidth - planningApplyWidth - planningUnlimitedWidth
                       - planningDefaultWidth - gap * 3f);
        var planningInputRect = new Rect(padding, y, planningInputWidth, S(34f));
        if (TryManualControlRect(viewport, autoBuildBodyScroll, planningInputRect,
                out var planningInputScreenRect))
        {
            HandlePlanningTimeInput(planningInputScreenRect);
            GUI.Box(planningInputScreenRect, GUIContent.none, searchStyle!);
            var inputSuffix = planningTimeInput == "∞" ? string.Empty : UiText.L(" 초", " s", " 秒", " 秒");
            GUI.Label(
                new Rect(
                    planningInputScreenRect.x + S(5f),
                    planningInputScreenRect.y + S(3f),
                    planningInputScreenRect.width - S(10f),
                    planningInputScreenRect.height - S(6f)),
                planningTimeInput + (focusPlanningTimeInput ? "|" : string.Empty) + inputSuffix,
                badgeStyle!);
        }
        else
        {
            focusPlanningTimeInput = false;
        }
        var planningButtonX = padding + planningInputWidth + gap;
        if (DrawManualButton(viewport, autoBuildBodyScroll,
                new Rect(planningButtonX, y, planningApplyWidth, S(34f)),
                UiText.L("적용", "Apply", "应用", "套用"), compactButtonStyle!))
            ApplyCustomPlanningTime();
        planningButtonX += planningApplyWidth + gap;
        var previousPlanningBackground = GUI.backgroundColor;
        if (planningSeconds == 0) GUI.backgroundColor = new Color(0.34f, 0.78f, 0.58f);
        if (DrawManualButton(viewport, autoBuildBodyScroll,
                new Rect(planningButtonX, y, planningUnlimitedWidth, S(34f)),
                UiText.L("무제한", "Unlimited", "无限", "無限制"), compactButtonStyle!))
            SetAutoBuildPlanningSeconds(0);
        GUI.backgroundColor = previousPlanningBackground;
        planningButtonX += planningUnlimitedWidth + gap;
        if (DrawManualButton(viewport, autoBuildBodyScroll,
                new Rect(planningButtonX, y, planningDefaultWidth, S(34f)),
                UiText.L("기본 60", "Default 60", "默认 60", "預設 60"), compactButtonStyle!))
            SetAutoBuildPlanningSeconds(60);
        y += S(36f) + gap;

        var nextStorage = DrawManualToggle(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, storageHeight), Plugin.AutoBuildIncludeStorage.Value, storageLabel, toggleStyle!);
        if (nextStorage != Plugin.AutoBuildIncludeStorage.Value)
        {
            Plugin.AutoBuildIncludeStorage.Value = nextStorage;
            armedAutoBuild = AutoBuildAction.None;
        }
        y += storageHeight + gap;
        var forgeExcluded = !Plugin.AutoBuildIncludeForgeBonuses.Value;
        var nextForgeExcluded = DrawManualToggle(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, forgeHeight), forgeExcluded, forgeLabel, toggleStyle!);
        if (nextForgeExcluded != forgeExcluded)
        {
            Plugin.AutoBuildIncludeForgeBonuses.Value = !nextForgeExcluded;
            armedAutoBuild = AutoBuildAction.None;
        }
        y += forgeHeight + gap;
        var nextTransform = DrawManualToggle(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, transformHeight), Plugin.AutoTransformSkills.Value, transformLabel, toggleStyle!);
        if (nextTransform != Plugin.AutoTransformSkills.Value)
        {
            Plugin.AutoTransformSkills.Value = nextTransform;
            armedAutoBuild = AutoBuildAction.None;
        }
        GUI.enabled = panelEnabled;
        y += transformHeight + gap;

        var combinedRect = new Rect(padding, y, innerWidth, S(58f));
        if (TryManualControlRect(viewport, autoBuildBodyScroll, combinedRect, out var combinedScreenRect))
            DrawAutoBuildButton(combinedScreenRect, AutoBuildAction.Combined,
                UiText.L("장비+스킬 자동 빌드", "Auto-build Gear + Skills", "自动构筑装备和技能", "自動配置裝備與技能"));
        y += S(58f) + gap;
        if (twoAdvancedColumns)
        {
            var advancedWidth = (innerWidth - gap) / 2f;
            var gearRect = new Rect(padding, y, advancedWidth, S(48f));
            if (TryManualControlRect(viewport, autoBuildBodyScroll, gearRect, out var gearScreenRect))
                DrawAutoBuildButton(gearScreenRect, AutoBuildAction.Gear,
                    UiText.L("고급: 장비만 적용", "Advanced: apply gear only", "高级：仅应用装备", "進階：僅套用裝備"));
            var skillsRect = new Rect(padding + advancedWidth + gap, y, advancedWidth, S(48f));
            if (TryManualControlRect(viewport, autoBuildBodyScroll, skillsRect, out var skillsScreenRect))
                DrawAutoBuildButton(skillsScreenRect, AutoBuildAction.Skills,
                    UiText.L("고급: 현재 장비로 스킬만", "Advanced: skills for current gear", "高级：按当前装备仅调整技能", "進階：依目前裝備僅調整技能"));
        }
        else
        {
            var gearRect = new Rect(padding, y, innerWidth, S(48f));
            if (TryManualControlRect(viewport, autoBuildBodyScroll, gearRect, out var gearScreenRect))
                DrawAutoBuildButton(gearScreenRect, AutoBuildAction.Gear,
                    UiText.L("고급: 장비만 적용", "Advanced: apply gear only", "高级：仅应用装备", "進階：僅套用裝備"));
            var skillsRect = new Rect(padding, y + S(56f), innerWidth, S(48f));
            if (TryManualControlRect(viewport, autoBuildBodyScroll, skillsRect, out var skillsScreenRect))
                DrawAutoBuildButton(skillsScreenRect, AutoBuildAction.Skills,
                    UiText.L("고급: 현재 장비로 스킬만", "Advanced: skills for current gear", "高级：按当前装备仅调整技能", "進階：依目前裝備僅調整技能"));
        }
        y += advancedHeight + gap;
        DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, explanationHeight), explanation, tooltipBodyStyle!);
        y += explanationHeight + gap * 2f;

        if (running is not null)
        {
            DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, phaseWidth, phaseHeight), phase, tooltipBodyStyle!);
            var canCancel = running.CanCancelPlanning;
            GUI.enabled = panelEnabled && canCancel;
            if (DrawManualButton(viewport, autoBuildBodyScroll, new Rect(padding + phaseWidth + gap, y, cancelWidth, S(32f)), UiText.L("계산 취소", "Cancel", "取消计算", "取消計算"), buttonStyle!))
                running.RequestCancel();
            GUI.enabled = panelEnabled;
            y += phaseHeight + gap;
            var barRect = new Rect(padding, y, innerWidth, S(10f));
            if (TryManualControlRect(viewport, autoBuildBodyScroll, barRect, out var barScreenRect))
                DrawAutoBuildProgressBar(barScreenRect, running);
            y += S(10f) + gap;
            DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, metaHeight), meta, hintStyle!);
        }
        else
        {
            DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, cautionHeight), caution, hintStyle!);
            y += cautionHeight + gap;
            if (statusHeight > 0f)
                DrawManualLabel(viewport, autoBuildBodyScroll, new Rect(padding, y, innerWidth, statusHeight), status, tooltipBodyStyle!);
        }
        DrawManualScrollbar(viewport, contentHeight, autoBuildBodyScroll);
    }

    [HideFromIl2Cpp]
    private void DrawResponsiveWorkshopPanel(Rect viewport)
    {
        if (forgeOptimizationArmed && Time.unscaledTime > forgeOptimizationConfirmUntil)
            forgeOptimizationArmed = false;
        if (forgeMaxAllArmed && Time.unscaledTime > forgeOptimizationConfirmUntil)
            forgeMaxAllArmed = false;
        if (mythicRefineArmed && Time.unscaledTime > mythicRefineConfirmUntil)
        {
            mythicRefineArmed = false;
            mythicRefineConfirmedItemKey = string.Empty;
            mythicRefineConfirmedCandidateCount = 0;
            mythicRefineConfirmedBatchToken = string.Empty;
            mythicRefineStatus = UiText.L(
                "Mythic 정련 확인 시간이 지나 취소됐습니다. 아이템과 재료는 소비하지 않았습니다.",
                "Mythic confirmation expired. No item or resource was consumed.",
                "Mythic 精炼确认已超时取消。未消耗任何物品或材料。",
                "Mythic 精煉確認已逾時取消。未消耗任何物品或材料。");
            status = mythicRefineStatus;
        }

        var contentWidth = Math.Max(S(300f), viewport.width - S(18f));
        var padding = S(20f);
        var innerWidth = Math.Max(S(220f), contentWidth - padding * 2f);
        var gap = S(8f);
        var title = UiText.L(
            "별도 장비 강화 최적화 (+1)",
            "Separate Gear Forge Optimizer (+1)",
            "独立装备强化优化（+1）",
            "獨立裝備強化最佳化（+1）");
        var detailButtonWidth = S(94f);
        var titleWidth = Math.Max(S(90f), innerWidth - detailButtonWidth - gap);
        var intro = UiText.L(
            "자동 빌드나 Mythic 정련과 별개입니다. 선택 영웅이 현재 착용한 각 장비를 정확히 복제해 +1 강화한 뒤, 30초 빌드 성능과 실제 게임 비용을 비교합니다.",
            "Separate from Auto Build and Mythic refinement. Each equipped item is cloned at +1, then compared using the 30-second build model and native game cost.",
            "此功能独立于自动配装与 Mythic 精炼。系统会将每件已装备物品精确复制并提升 +1，再以 30 秒构筑性能和原生游戏费用进行比较。",
            "此功能獨立於自動配裝與 Mythic 精煉。系統會將每件已裝備物品精確複製並提升 +1，再以 30 秒配置效能和原生遊戲費用進行比較。");
        var comparison = UiText.L(
            "비교값: 무강화 원본 → 현재 누적 강화값 → 다음 +1 예상값. 순위는 현재 상태에서 얻는 성능 증가량/비용으로 정하며 한 번에 정확히 1단계만 강화합니다.",
            "Comparison: un-forged base → current cumulative value → predicted next +1. Ranking uses gain from the current state per native cost; exactly one level is applied per run.",
            "比较值：无强化原值 → 当前累计强化值 → 下一次 +1 预测值。按当前状态的性能增益/原生费用排序；每次只强化 1 级。",
            "比較值：無強化原值 → 目前累計強化值 → 下一次 +1 預測值。依目前狀態的效能增益/原生費用排序；每次只強化 1 級。");
        var warning = UiText.L(
            "실행하면 게임의 실제 강화 재료가 소모됩니다. 계산 단계에서는 장비와 자원을 바꾸지 않으며, 적용 직전에 영웅·장비·스킬과 빈 작업대를 다시 확인합니다.",
            "Running this spends real Forge resources. Planning changes nothing; hero, gear, skills, and the empty Workshop field are revalidated immediately before application.",
            "执行会消耗真实强化材料。计算阶段不会更改装备或资源；应用前会再次验证英雄、装备、技能及空工坊槽位。",
            "執行會消耗真實強化材料。計算階段不會變更裝備或資源；套用前會再次驗證英雄、裝備、技能及空工坊欄位。");
        var mythicTitle = UiText.L(
            "Mythic 일괄 정련 · 가능한 후보 전부",
            "Mythic Batch Refinement · all possible candidates",
            "Mythic 批量精炼 · 所有可执行候选",
            "Mythic 批次精煉 · 所有可執行候選");
        var mythicIntro = UiText.L(
            "캐릭터 선택 없이 Inventory·Warehouse·Vault 전체에서 정련 가능한 Legendary를 찾습니다. 같은 원형이 여러 개인 장비, 강화 투자가 적고 레벨·주 능력치가 낮은 장비를 먼저 고르며 잠금·룬 장착·착용 장비는 제외합니다.",
            "No character selection is required. It scans Inventory, Warehouse, and Vault, preferring duplicate, less-invested, lower-level/attribute Legendary gear while excluding locked, socketed, or equipped items.",
            "无需选择角色。系统会扫描背包、仓库和宝库，优先选择同基础重复、强化投入较少且等级/主属性较低的 Legendary，并排除锁定、镶嵌或已装备物品。",
            "無需選擇角色。系統會掃描背包、倉庫與寶庫，優先選擇同基礎重複、強化投入較少且等級/主屬性較低的 Legendary，並排除鎖定、鑲嵌或已裝備物品。");
        var mythicWarning = UiText.L(
            "위험: 첫 클릭은 소모 없이 전체 후보와 Vault 공간을 검사합니다. 12초 안에 다시 누르면 확인된 후보를 가능한 만큼 모두 시도하며, 실패한 Legendary는 각각 영구 파괴됩니다. 후보마다 자원과 공간을 다시 검사하고 성공·실패·건너뜀 횟수를 보고합니다.",
            "Risk: the first click checks the full candidate set and Vault capacity without consuming anything. Confirm within 12 seconds to attempt every confirmed candidate that remains possible; every failed Legendary is permanently destroyed. Resources and capacity are rechecked per item, and success/failure/skip counts are reported.",
            "风险：第一次点击只检查全部候选与宝库空间，不会消耗物品。请在 12 秒内再次确认，系统会尽可能尝试所有已确认候选；每件失败的 Legendary 都会永久摧毁。每件物品都会重新检查材料与空间，并报告成功、失败和跳过次数。",
            "風險：第一次點擊只檢查全部候選與寶庫空間，不會消耗物品。請在 12 秒內再次確認，系統會盡可能嘗試所有已確認候選；每件失敗的 Legendary 都會永久摧毀。每件物品都會重新檢查材料與空間，並報告成功、失敗和略過次數。");
        var heroName = string.IsNullOrWhiteSpace(selectedHeroSummary)
            ? UiText.L("선택된 영웅 없음", "No hero selected", "未选择英雄", "未選擇英雄")
            : selectedHeroSummary;
        var heroProfile = string.IsNullOrWhiteSpace(selectedHeroProfile)
            ? UiText.L("게임에서 영웅을 먼저 선택하세요.", "Select a hero in the game first.", "请先在游戏中选择英雄。", "請先在遊戲中選擇英雄。")
            : selectedHeroProfile;
        var titleHeight = WrappedHeight(title, titleStyle!, titleWidth, S(34f));
        var introHeight = WrappedHeight(intro, hintStyle!, innerWidth, S(62f));
        var comparisonHeight = WrappedHeight(comparison, tooltipBodyStyle!, innerWidth, S(62f));
        var warningHeight = WrappedHeight(warning, hintStyle!, innerWidth, S(58f));
        var mythicTitleHeight = WrappedHeight(mythicTitle, utilityTitleStyle!, innerWidth, S(30f));
        var mythicIntroHeight = WrappedHeight(mythicIntro, tooltipBodyStyle!, innerWidth, S(72f));
        var mythicWarningHeight = WrappedHeight(mythicWarning, hintStyle!, innerWidth, S(72f));
        var mythicInlineStatusHeight = string.IsNullOrWhiteSpace(mythicRefineStatus)
            ? 0f
            : WrappedHeight(mythicRefineStatus, tooltipBodyStyle!, innerWidth, S(42f));
        var heroTitleHeight = WrappedHeight(heroName, utilityTitleStyle!, innerWidth - S(28f), S(26f));
        var heroProfileHeight = WrappedHeight(heroProfile, hintStyle!, innerWidth - S(28f), S(26f));
        var heroCardHeight = S(14f) + heroTitleHeight + S(4f) + heroProfileHeight + S(14f);
        var mythicRunning = mythicRefineOperation is { IsCompleted: false };
        var running = forgeOptimizationOperation is { IsCompleted: false }
            ? forgeOptimizationOperation
            : mythicRunning ? mythicRefineOperation : null;
        var runningDetail = running?.Progress.Detail ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runningDetail))
            runningDetail = mythicRunning
                ? UiText.L("Mythic 정련 후보 확인 중", "Checking Mythic candidates", "正在检查 Mythic 候选", "正在檢查 Mythic 候選")
                : UiText.L("강화 후보 계산 중", "Calculating Forge candidates", "正在计算强化候选", "正在計算強化候選");
        var runningHeight = running is null
            ? 0f
            : WrappedHeight(runningDetail, tooltipBodyStyle!, innerWidth - S(118f), S(34f));
        var statusHeight = running is null
            ? Plugin.ShowDetailedStatus.Value
                ? WrappedHeight(status, tooltipBodyStyle!, innerWidth, S(82f))
                : 0f
            : WrappedHeight(WorkshopProgressMeta(running, mythicRunning), hintStyle!, innerWidth, S(28f));
        var contentHeight = padding + titleHeight + gap + introHeight + gap
                            + heroCardHeight + gap * 2f + comparisonHeight + gap
                            + S(58f) + gap + S(58f) + gap + warningHeight + gap * 2f
                            + mythicTitleHeight + gap + mythicIntroHeight + gap
                            + S(58f) + gap
                            + (mythicInlineStatusHeight > 0f ? mythicInlineStatusHeight + gap : 0f)
                            + mythicWarningHeight + gap * 2f
                            + (running is null ? statusHeight : runningHeight + gap + S(10f) + gap + statusHeight)
                            + padding;
        contentHeight = Math.Max(viewport.height, contentHeight);
        UpdateManualScroll(viewport, contentHeight, ref workshopBodyScroll);
        DrawOpaquePanel(viewport, new Color(0.085f, 0.09f, 0.12f, 1f));

        var y = padding;
        var panelEnabled = GUI.enabled;
        DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding, y, titleWidth, titleHeight), title, titleStyle!);
        if (DrawManualButton(
                viewport,
                workshopBodyScroll,
                new Rect(padding + innerWidth - detailButtonWidth, y, detailButtonWidth, S(32f)),
                DetailedStatusButtonLabel(),
                compactButtonStyle!))
            ToggleDetailedStatus();
        y += titleHeight + gap;
        DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding, y, innerWidth, introHeight), intro, hintStyle!);
        y += introHeight + gap;
        DrawManualPanel(viewport, workshopBodyScroll, new Rect(padding, y, innerWidth, heroCardHeight), new Color(0.24f, 0.42f, 0.62f, 0.90f));
        DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding + S(14f), y + S(10f), innerWidth - S(28f), heroTitleHeight), heroName, utilityTitleStyle!);
        DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding + S(14f), y + S(10f) + heroTitleHeight + S(4f), innerWidth - S(28f), heroProfileHeight), heroProfile, hintStyle!);
        y += heroCardHeight + gap * 2f;
        DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding, y, innerWidth, comparisonHeight), comparison, tooltipBodyStyle!);
        y += comparisonHeight + gap;

        var actionRect = new Rect(padding, y, innerWidth, S(58f));
        if (TryManualControlRect(viewport, workshopBodyScroll, actionRect, out var actionScreenRect))
        {
            var armed = forgeOptimizationArmed && Time.unscaledTime <= forgeOptimizationConfirmUntil;
            var previousBackground = GUI.backgroundColor;
            GUI.enabled = panelEnabled && !IsAutoBuildRunning && bulkSession is null
                          && !string.IsNullOrWhiteSpace(selectedHeroSummary);
            GUI.backgroundColor = armed
                ? new Color(1f, 0.48f, 0.18f)
                : new Color(0.32f, 0.68f, 0.94f);
            var label = armed
                ? UiText.L("확인: 최적 장비를 찾아 +1 강화", "Confirm: find best and Forge +1", "确认：寻找最佳装备并强化 +1", "確認：尋找最佳裝備並強化 +1")
                : UiText.L("최적 강화 대상 찾기 + 1단계 적용", "Find best target + apply one level", "寻找最佳强化目标并应用 1 级", "尋找最佳強化目標並套用 1 級");
            if (GUI.Button(actionScreenRect, label, buttonStyle!))
                BeginOrConfirmForgeOptimization();
            GUI.backgroundColor = previousBackground;
            GUI.enabled = panelEnabled;
        }
        y += S(58f) + gap;

        var maxForgeRect = new Rect(padding, y, innerWidth, S(58f));
        if (TryManualControlRect(viewport, workshopBodyScroll,
                maxForgeRect, out var maxForgeScreenRect))
        {
            var armed = forgeMaxAllArmed
                        && Time.unscaledTime <= forgeOptimizationConfirmUntil;
            var previousBackground = GUI.backgroundColor;
            GUI.enabled = panelEnabled && !IsAutoBuildRunning && bulkSession is null
                          && !string.IsNullOrWhiteSpace(selectedHeroSummary);
            GUI.backgroundColor = armed
                ? new Color(1f, 0.48f, 0.18f)
                : new Color(0.24f, 0.76f, 0.52f);
            var label = armed
                ? UiText.L(
                    "확인: 착용 장비 전부 최대 강화",
                    "Confirm: max all equipped gear",
                    "确认：将所有已装备物品强化至最高",
                    "確認：將所有已裝備物品強化至最高")
                : UiText.L(
                    "착용 장비 전부 최대 강화 (+5 우선)",
                    "Max all equipped gear (+5 first)",
                    "所有已装备物品强化至最高（优先 +5）",
                    "所有已裝備物品強化至最高（優先 +5）");
            if (GUI.Button(maxForgeScreenRect, label, buttonStyle!))
                BeginOrConfirmForgeMaximum();
            GUI.backgroundColor = previousBackground;
            GUI.enabled = panelEnabled;
        }
        y += S(58f) + gap;
        DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding, y, innerWidth, warningHeight), warning, hintStyle!);
        y += warningHeight + gap * 2f;

        DrawManualLabel(viewport, workshopBodyScroll,
            new Rect(padding, y, innerWidth, mythicTitleHeight),
            mythicTitle, utilityTitleStyle!);
        y += mythicTitleHeight + gap;
        DrawManualLabel(viewport, workshopBodyScroll,
            new Rect(padding, y, innerWidth, mythicIntroHeight),
            mythicIntro, tooltipBodyStyle!);
        y += mythicIntroHeight + gap;
        var mythicActionRect = new Rect(padding, y, innerWidth, S(58f));
        if (TryManualControlRect(viewport, workshopBodyScroll,
                mythicActionRect, out var mythicActionScreenRect))
        {
            var armed = mythicRefineArmed
                        && Time.unscaledTime <= mythicRefineConfirmUntil;
            var previousBackground = GUI.backgroundColor;
            GUI.enabled = panelEnabled && !IsAutoBuildRunning
                          && bulkSession is null
                          && warehouseSaleSession is null;
            GUI.backgroundColor = armed
                ? new Color(0.96f, 0.22f, 0.16f)
                : new Color(0.72f, 0.34f, 0.82f);
            var secondsRemaining = Math.Max(1,
                (int)Math.Ceiling(mythicRefineConfirmUntil - Time.unscaledTime));
            var label = armed
                ? UiText.L(
                    $"확인 ({secondsRemaining}초): 후보 {mythicRefineConfirmedCandidateCount}개 전부 시도",
                    $"Confirm ({secondsRemaining}s): attempt all {mythicRefineConfirmedCandidateCount} candidates",
                    $"确认（{secondsRemaining} 秒）：尝试全部 {mythicRefineConfirmedCandidateCount} 件候选",
                    $"確認（{secondsRemaining} 秒）：嘗試全部 {mythicRefineConfirmedCandidateCount} 件候選")
                : UiText.L("Mythic 전체 후보 검사 (소모 없음)", "Check all Mythic candidates (no consumption)", "检查全部 Mythic 候选（无消耗）", "檢查全部 Mythic 候選（無消耗）");
            if (GUI.Button(mythicActionScreenRect, label, buttonStyle!))
                BeginOrConfirmMythicRefine();
            GUI.backgroundColor = previousBackground;
            GUI.enabled = panelEnabled;
        }
        y += S(58f) + gap;
        if (mythicInlineStatusHeight > 0f)
        {
            DrawManualLabel(viewport, workshopBodyScroll,
                new Rect(padding, y, innerWidth, mythicInlineStatusHeight),
                mythicRefineStatus, tooltipBodyStyle!);
            y += mythicInlineStatusHeight + gap;
        }
        DrawManualLabel(viewport, workshopBodyScroll,
            new Rect(padding, y, innerWidth, mythicWarningHeight),
            mythicWarning, hintStyle!);
        y += mythicWarningHeight + gap * 2f;

        if (running is not null)
        {
            var cancelWidth = S(110f);
            var detailWidth = Math.Max(S(90f), innerWidth - cancelWidth - gap);
            DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding, y, detailWidth, runningHeight), runningDetail, tooltipBodyStyle!);
            GUI.enabled = panelEnabled && running.CanCancelPlanning;
            if (DrawManualButton(viewport, workshopBodyScroll,
                    new Rect(padding + detailWidth + gap, y, cancelWidth, S(34f)),
                    UiText.L("계산 취소", "Cancel", "取消计算", "取消計算"), buttonStyle!))
                running.RequestCancel();
            GUI.enabled = panelEnabled;
            y += runningHeight + gap;
            var progressRect = new Rect(padding, y, innerWidth, S(10f));
            if (TryManualControlRect(viewport, workshopBodyScroll, progressRect, out var progressScreenRect))
                DrawAutoBuildProgressBar(progressScreenRect, running);
            y += S(10f) + gap;
            DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding, y, innerWidth, statusHeight), WorkshopProgressMeta(running, mythicRunning), hintStyle!);
        }
        else if (statusHeight > 0f)
        {
            DrawManualLabel(viewport, workshopBodyScroll, new Rect(padding, y, innerWidth, statusHeight), status, tooltipBodyStyle!);
        }
        DrawManualScrollbar(viewport, contentHeight, workshopBodyScroll);
    }

    // Kept as a short-term fallback while the responsive renderer is exercised
    // against live game data. Unity invokes only OnGUI above.
    [HideFromIl2Cpp]
    private void DrawLegacyOverlay()
    {
        if (!visible) return;
        var focusEvent = Event.current;
        if (focusEvent is not null && focusEvent.type == EventType.MouseDown)
        {
            overlayInputFocused = windowRect.Contains(focusEvent.mousePosition)
                                  || activeTooltipRect.Contains(focusEvent.mousePosition);
            if (!overlayInputFocused)
            {
                focusSearch = false;
                focusSpeedInput = false;
                focusPlanningTimeInput = false;
                GUI.FocusControl(string.Empty);
                RestoreImeMode();
            }
        }
        ClampWindowToScreen();
        UpdateUiScale();
        EnsureStyles();
        // The opaque tooltip owns overlapping pixels before any hidden result
        // control. Otherwise a click could dismiss it and activate a covered
        // transfer button or resize grip in the same IMGUI event.
        var tooltipOwnsPointer = hoveredItem is not null && activeTooltipRect.Contains(Event.current.mousePosition);
        var pointerOverTransfer = !tooltipOwnsPointer
                                  && visibleTransferRects.Any(rect => rect.Contains(Event.current.mousePosition));
        if (pointerOverTransfer)
        {
            hoveredItem = null;
            tooltipItemKey = string.Empty;
            activeTooltipRect = default;
        }
        visibleTransferRects.Clear();
        if (resizing || !tooltipOwnsPointer) HandleWindowResize();
        if (!resizing && !tooltipOwnsPointer) HandleWindowDrag();
        UpdateUiScale();
        EnsureStyles();
        var keepTooltipOpen = tooltipOwnsPointer;
        if (!keepTooltipOpen) hoveredItem = null;
        activeTooltipRect = default;
        UpdateInputBlocker();

        // The tooltip is drawn after the main panel. Disable every underlying
        // IMGUI control while the pointer is inside its previous-frame bounds;
        // otherwise an opaque tooltip can still click hidden tabs/filters/drag.
        var mainPanelEnabled = GUI.enabled;
        if (tooltipOwnsPointer) GUI.enabled = false;

        GUI.depth = -10000;
        DrawOpaquePanel(windowRect, new Color(0.035f, 0.035f, 0.045f, 1f));
        var left = windowRect.x + S(18f);
        var width = windowRect.width - S(36f);
        var pageTitle = selectedPage switch
        {
            OverlayPage.BulkOpen => UiText.L("Path of Idle · 일괄 개봉", "Path of Idle · Bulk Open", "Path of Idle · 批量开启", "Path of Idle · 批次開啟"),
            OverlayPage.AutoBuild => UiText.L("Path of Idle · 자동 빌드", "Path of Idle · Auto Build", "Path of Idle · 自动配装", "Path of Idle · 自動配裝"),
            OverlayPage.Workshop => UiText.L("Path of Idle · 장비 강화", "Path of Idle · Forge", "Path of Idle · 装备强化", "Path of Idle · 裝備強化"),
            _ => UiText.L("Path of Idle · 아이템 검색", "Path of Idle · Item Search", "Path of Idle · 物品搜索", "Path of Idle · 物品搜尋")
        };
        GUI.Label(new Rect(left, windowRect.y + S(12f), Math.Max(S(80f), width - S(382f)), S(30f)), pageTitle, titleStyle!);
        var headerEnabled = GUI.enabled;
        GUI.enabled = headerEnabled && !IsAutoBuildRunning;
        if (GUI.Button(new Rect(windowRect.xMax - S(368f), windowRect.y + S(11f), S(62f), S(29f)), LanguageButtonLabel(), compactButtonStyle!)) CycleUiLanguage();
        GUI.enabled = headerEnabled;
        if (GUI.Button(new Rect(windowRect.xMax - S(300f), windowRect.y + S(11f), S(32f), S(29f)), "−", buttonStyle!)) ChangeGameSpeed(-1);
        var speedRect = new Rect(windowRect.xMax - S(262f), windowRect.y + S(11f), S(62f), S(29f));
        if (!tooltipOwnsPointer) HandleSpeedInput(speedRect);
        GUI.Box(speedRect, GUIContent.none, searchStyle!);
        GUI.Label(new Rect(speedRect.x + S(5f), speedRect.y + S(4f), speedRect.width - S(10f), S(22f)), speedInput + (focusSpeedInput ? "|" : string.Empty) + "×", badgeStyle!);
        if (GUI.Button(new Rect(windowRect.xMax - S(194f), windowRect.y + S(11f), S(32f), S(29f)), "+", buttonStyle!)) ChangeGameSpeed(1);
        if (GUI.Button(new Rect(windowRect.xMax - S(156f), windowRect.y + S(11f), S(50f), S(29f)), UiText.L("적용", "Apply", "应用", "套用"), compactButtonStyle!)) ApplyCustomSpeed();
        if (GUI.Button(new Rect(windowRect.xMax - S(100f), windowRect.y + S(11f), S(42f), S(29f)), "1×", buttonStyle!)) SetGameSpeed(1f);
        if (GUI.Button(new Rect(windowRect.xMax - S(50f), windowRect.y + S(10f), S(34f), S(30f)), "×", closeStyle!)) SetVisible(false);
        GUI.Label(new Rect(left, windowRect.y + S(43f), S(280f), S(22f)), UiText.L(
            "F3 / Ctrl+F · 공백 AND · | OR · - 제외",
            "F3 / Ctrl+F · space AND · | OR · - exclude",
            "F3 / Ctrl+F · 空格 AND · | OR · - 排除",
            "F3 / Ctrl+F · 空格 AND · | OR · - 排除"), hintStyle!);
        DrawPageTabs();

        if (selectedPage == OverlayPage.BulkOpen)
        {
            var panelTop = windowRect.y + S(78f);
            DrawBulkOpenPanel(new Rect(left, panelTop, width, Math.Max(S(1f), windowRect.yMax - panelTop - S(18f))));
            DrawResizeGrip();
            GUI.enabled = mainPanelEnabled;
            ConsumeRemainingKeyboardEvent();
            return;
        }

        if (selectedPage == OverlayPage.AutoBuild)
        {
            var panelTop = windowRect.y + S(78f);
            DrawAutoBuildPanel(new Rect(left, panelTop, width, Math.Max(S(1f), windowRect.yMax - panelTop - S(18f))));
            DrawResizeGrip();
            GUI.enabled = mainPanelEnabled;
            ConsumeRemainingKeyboardEvent();
            return;
        }

        if (selectedPage == OverlayPage.Workshop)
        {
            var panelTop = windowRect.y + S(78f);
            DrawResponsiveWorkshopPanel(new Rect(left, panelTop, width, Math.Max(S(1f), windowRect.yMax - panelTop - S(18f))));
            DrawResizeGrip();
            GUI.enabled = mainPanelEnabled;
            ConsumeRemainingKeyboardEvent();
            return;
        }

        var searchRect = new Rect(left, windowRect.y + S(70f), width, S(38f));
        GUI.SetNextControlName(SearchControlName);
        var nextQuery = GUI.TextField(searchRect, query, 200, searchStyle!);
        if (focusSearch && GUI.GetNameOfFocusedControl() != SearchControlName)
            GUI.FocusControl(SearchControlName);
        focusSearch = GUI.GetNameOfFocusedControl() == SearchControlName;
        if (focusSearch)
        {
            EnableImeMode();
            Input.compositionCursorPos = new Vector2(searchRect.x + S(12f), searchRect.yMax + S(4f));
        }
        if (!string.Equals(nextQuery, query, StringComparison.Ordinal))
        {
            query = nextQuery;
            searchCaret = query.Length;
            Plugin.SavedQuery.Value = query;
            currentPage = 0;
            ApplyFilter();
        }
        if (string.IsNullOrEmpty(query) && !focusSearch)
        {
            var placeholder = UiText.L(
                "아이템 이름, 등급, 부위, 옵션 검색…",
                "Search name, quality, slot, or affix…",
                "搜索名称、品质、部位或词缀…",
                "搜尋名稱、品質、部位或詞綴…");
            var previousTextColor = searchTextStyle!.normal.textColor;
            searchTextStyle.normal.textColor = new Color(0.52f, 0.52f, 0.56f);
            GUI.Label(new Rect(searchRect.x + S(10f), searchRect.y + S(7f), searchRect.width - S(20f), S(24f)), placeholder, searchTextStyle);
            searchTextStyle.normal.textColor = previousTextColor;
        }

        var toolbarY = windowRect.y + S(114f);
        var toolbarGap = S(6f);
        var warehouseToggleWidth = Mathf.Clamp(width * 0.32f, S(180f), S(230f));
        var refreshWidth = S(92f);
        var clearWidth = S(112f);
        var nextWarehouse = GUI.Toggle(new Rect(left, toolbarY + S(2f), warehouseToggleWidth, S(30f)), includeWarehouse, UiText.L(" 창고·Vault 포함", " Include warehouse and Vault", " 包含仓库与宝库", " 包含倉庫與寶庫"), toggleStyle!);
        if (nextWarehouse != includeWarehouse)
        {
            includeWarehouse = nextWarehouse;
            Plugin.IncludeWarehouse.Value = includeWarehouse;
            if (!includeWarehouse) selectedStorage = StorageKind.Inventory;
            currentPage = 0;
            RefreshItems();
        }
        var refreshX = left + warehouseToggleWidth + toolbarGap;
        var clearX = refreshX + refreshWidth + toolbarGap;
        if (GUI.Button(new Rect(refreshX, toolbarY, refreshWidth, S(30f)), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshItems();
        if (GUI.Button(new Rect(clearX, toolbarY, clearWidth, S(30f)), UiText.L("검색어 지우기", "Clear search", "清除搜索", "清除搜尋"), compactButtonStyle!))
        {
            query = string.Empty;
            searchCaret = 0;
            Plugin.SavedQuery.Value = query;
            focusSearch = selectedPage == OverlayPage.Search;
            currentPage = 0;
            ApplyFilter();
        }
        var statusX = clearX + clearWidth + toolbarGap;
        GUI.Label(new Rect(statusX, toolbarY, Math.Max(S(80f), left + width - statusX), S(36f)), status, hintStyle!);

        DrawQualityFilters(new Rect(left, windowRect.y + S(154f), width, S(100f)));
        var inventoryCount = 0;
        var warehouseCount = 0;
        var selectedMatches = new List<ItemSearchRecord>();
        foreach (var item in matches)
        {
            if (item.StorageKind == StorageKind.Inventory) inventoryCount++; else warehouseCount++;
            if (item.StorageKind == selectedStorage) selectedMatches.Add(item);
        }
        var storageGap = S(8f);
        var storageWidth = (width - storageGap) / 2f;
        DrawStorageTab(new Rect(left, windowRect.y + S(264f), storageWidth, S(34f)), StorageKind.Inventory, $"{UiText.L("인벤토리", "INVENTORY", "背包", "背包")}  {inventoryCount}", new Color(0.32f, 0.86f, 0.46f));
        DrawStorageTab(new Rect(left + storageWidth + storageGap, windowRect.y + S(264f), storageWidth, S(34f)), StorageKind.Warehouse, $"{UiText.L("보관함", "WAREHOUSE / VAULT", "仓库 / 宝库", "倉庫 / 寶庫")}  {warehouseCount}", new Color(0.25f, 0.78f, 0.92f));

        var resultTop = windowRect.y + S(306f);
        var resultRowStep = S(88f);
        var resultAreaHeight = Math.Max(resultRowStep, windowRect.yMax - S(52f) - resultTop);
        var resultsPerPage = Math.Max(1, Math.Min(12, (int)Math.Floor(resultAreaHeight / resultRowStep)));
        var pageCount = Math.Max(1, (int)Math.Ceiling(selectedMatches.Count / (double)resultsPerPage));
        currentPage = Math.Max(0, Math.Min(currentPage, pageCount - 1));
        var resultArea = new Rect(left, resultTop, width, resultAreaHeight);
        var currentEvent = Event.current;
        var pointerOverTooltip = hoveredItem is not null && tooltipAnchorRect.Contains(currentEvent.mousePosition);
        if (currentEvent.type == EventType.ScrollWheel && resultArea.Contains(currentEvent.mousePosition) && !pointerOverTooltip && !tooltipOwnsPointer)
        {
            overlayInputFocused = true;
            currentPage = Math.Max(0, Math.Min(pageCount - 1, currentPage + (currentEvent.delta.y > 0f ? 1 : -1)));
            currentEvent.Use();
        }

        if (selectedMatches.Count == 0)
        {
            GUI.Label(new Rect(left + S(12f), resultTop + S(32f), width - S(24f), S(40f)), allItems.Count == 0
                ? UiText.L("인벤토리 데이터를 기다리는 중입니다.", "Waiting for inventory data.", "正在等待背包数据。", "正在等待背包資料。")
                : UiText.L("이 구역에는 검색 조건에 맞는 아이템이 없습니다.", "No matching items in this section.", "此区域没有匹配的物品。", "此區域沒有相符的物品。"), hintStyle!);
        }
        else
        {
            var pageItems = selectedMatches.Skip(currentPage * resultsPerPage).Take(resultsPerPage).ToList();
            for (var index = 0; index < pageItems.Count; index++)
                DrawResult(pageItems[index], new Rect(left, resultTop + index * resultRowStep, width, S(82f)));
        }

        var previousWidth = S(104f);
        var pageNumberWidth = S(96f);
        var nextWidth = S(104f);
        var navGap = S(8f);
        var navWidth = previousWidth + pageNumberWidth + nextWidth + navGap * 2f;
        var navX = left + Math.Max(0f, (width - navWidth) / 2f);
        if (GUI.Button(new Rect(navX, windowRect.yMax - S(43f), previousWidth, S(30f)), UiText.L("◀ 이전", "◀ Previous", "◀ 上一页", "◀ 上一頁"), compactButtonStyle!) && currentPage > 0) currentPage--;
        GUI.Label(new Rect(navX + previousWidth + navGap, windowRect.yMax - S(43f), pageNumberWidth, S(30f)), $"{currentPage + 1} / {pageCount}", pageStyle!);
        if (GUI.Button(new Rect(navX + previousWidth + pageNumberWidth + navGap * 2f, windowRect.yMax - S(43f), nextWidth, S(30f)), UiText.L("다음 ▶", "Next ▶", "下一页 ▶", "下一頁 ▶"), compactButtonStyle!) && currentPage + 1 < pageCount) currentPage++;

        GUI.enabled = mainPanelEnabled;
        DrawResizeGrip();
        if (hoveredItem is not null) DrawItemTooltip(hoveredItem);
        else
        {
            tooltipItemKey = string.Empty;
            tooltipAnchorRect = default;
            tooltipScroll = Vector2.zero;
        }

        ConsumeRemainingKeyboardEvent();

    }

    public void OnDestroy()
    {
        StartupAutoController.CancelForShutdown();
        Plugin.StopBackgroundActivationHotkeys();
        var operation = autoBuildOperation;
        var task = autoBuildTask;
        if (operation is { MutationStarted: false }) operation.RequestCancel();
        if (task is not null && !task.IsCompleted)
        {
            var shutdownWait = Stopwatch.StartNew();
            while (!task.IsCompleted
                   && (operation?.MutationStarted == true || shutdownWait.ElapsedMilliseconds < 2000))
            {
                MainThreadNativeGateway.Drain(operation?.MutationStarted == true ? double.MaxValue : 4d);
                Thread.Yield();
            }
        }
        var forgeOperation = forgeOptimizationOperation;
        var forgeTask = forgeOptimizationTask;
        if (forgeOperation is { MutationStarted: false }) forgeOperation.RequestCancel();
        if (forgeTask is not null && !forgeTask.IsCompleted)
        {
            var shutdownWait = Stopwatch.StartNew();
            while (!forgeTask.IsCompleted
                   && (forgeOperation?.MutationStarted == true || shutdownWait.ElapsedMilliseconds < 2000))
            {
                MainThreadNativeGateway.Drain(
                    forgeOperation?.MutationStarted == true ? double.MaxValue : 4d);
                Thread.Yield();
            }
        }
        var mythicOperation = mythicRefineOperation;
        var mythicTask = mythicRefineTask;
        if (mythicOperation is { MutationStarted: false }) mythicOperation.RequestCancel();
        if (mythicTask is not null && !mythicTask.IsCompleted)
        {
            var shutdownWait = Stopwatch.StartNew();
            while (!mythicTask.IsCompleted
                   && (mythicOperation?.MutationStarted == true || shutdownWait.ElapsedMilliseconds < 2000))
            {
                MainThreadNativeGateway.Drain(
                    mythicOperation?.MutationStarted == true ? double.MaxValue : 4d);
                Thread.Yield();
            }
        }
#if PATHOFIDLE_RUNTIME_TEST
        // A diagnostic request owns this process-wide switch only while its
        // overlay operation is alive. Do not let a scene/plugin teardown leave
        // a later ordinary Auto Build in forced-rollback mode.
        runtimeTestQueued = false;
        runtimeTestStopwatch = null;
        lock (RuntimeTestResultSync)
        {
            runtimeTestResult = null;
            runtimeTestElapsedMilliseconds = 0;
            runtimeTestPostState = string.Empty;
        }
        GameInventoryReader.ReleaseRuntimeTestHeroSelection(out _);
#endif
        if (warehouseSaleSession is not null)
        {
            GameInventoryReader.ReleaseWarehouseEquipmentSale(warehouseSaleSession);
            warehouseSaleSession = null;
        }
        MainThreadNativeGateway.Shutdown();
        SaveWindowPosition();
        RestoreImeMode();
        DestroyInputBlocker();
        TryReleaseGameInputGuards(force: true);
        if (ReferenceEquals(activeOverlay, this)) activeOverlay = null;
        Time.timeScale = 1f;
    }

    private void SetVisible(bool value)
    {
        if (visible == value) return;
        visible = value;
        overlayInputFocused = visible;
        Plugin.DiagInfo($"Search panel visible={visible}.");
        dragging = false;
        if (resizing) GUIUtility.hotControl = 0;
        resizing = false;
        if (visible)
        {
            keyboardReleasePending = false;
            EnableImeMode();
            focusSearch = true;
            nextRefreshAt = 0f;
            MaintainGameKeyboardBlock();
            UpdateInputBlocker();
            RefreshCurrentPage();
        }
        else
        {
            if (bulkSession is not null)
            {
                bulkSession.CancelRequested = true;
                AdvanceBulkOpenSession();
            }
            overlayInputFocused = false;
            SaveWindowPosition();
            RestoreImeMode();
            SetInputBlockerActive(false);
            // Keep the game's key manager blocked until the key that closed the
            // overlay is released, so F3/Escape/Ctrl+F cannot leak into gameplay.
            keyboardReleasePending = keyboardBlockCaptured || navigationBlockCaptured;
        }
    }

    [HideFromIl2Cpp]
    private void UpdateInputBlocker()
    {
        if (!visible)
        {
            SetInputBlockerActive(false);
            return;
        }
        EnsureInputBlocker();
        if (inputBlockerCanvasObject is null || inputBlockerRegionObject is null || inputBlockerRect is null) return;
        if (!inputBlockerCanvasObject.activeSelf) inputBlockerCanvasObject.SetActive(true);
        inputBlockerRegionObject.SetActive(true);
        inputBlockerRect.anchorMin = new Vector2(0f, 1f);
        inputBlockerRect.anchorMax = new Vector2(0f, 1f);
        inputBlockerRect.pivot = new Vector2(0f, 1f);
        // IMGUI hotControl already owns an active drag/resize. Keep the Unity UI
        // raycast blocker local to the visible window so the rest of the game is
        // never covered by an invisible full-screen rectangle.
        inputBlockerRect.anchoredPosition = new Vector2(windowRect.x, -windowRect.y);
        inputBlockerRect.sizeDelta = new Vector2(windowRect.width, windowRect.height);
        if (inputBlockerTooltipObject is not null && inputBlockerTooltipRect is not null)
        {
            var hasTooltip = activeTooltipRect.width > 0f && activeTooltipRect.height > 0f;
            inputBlockerTooltipObject.SetActive(hasTooltip);
            if (hasTooltip)
            {
                inputBlockerTooltipRect.anchorMin = new Vector2(0f, 1f);
                inputBlockerTooltipRect.anchorMax = new Vector2(0f, 1f);
                inputBlockerTooltipRect.pivot = new Vector2(0f, 1f);
                inputBlockerTooltipRect.anchoredPosition = new Vector2(activeTooltipRect.x, -activeTooltipRect.y);
                inputBlockerTooltipRect.sizeDelta = new Vector2(activeTooltipRect.width, activeTooltipRect.height);
            }
        }
    }

    [HideFromIl2Cpp]
    private void EnsureInputBlocker()
    {
        if (inputBlockerCanvasObject is not null) return;
        try
        {
            inputBlockerCanvasObject = new GameObject("PathOfIdleSearchInputBlockerCanvas");
            var canvas = inputBlockerCanvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            inputBlockerCanvasObject.AddComponent<GraphicRaycaster>();
            UnityEngine.Object.DontDestroyOnLoad(inputBlockerCanvasObject);

            inputBlockerRegionObject = new GameObject(
                "PathOfIdleSearchInputBlockerRegion",
                Il2CppType.Of<RectTransform>(),
                Il2CppType.Of<CanvasRenderer>(),
                Il2CppType.Of<Image>());
            inputBlockerRegionObject.transform.SetParent(inputBlockerCanvasObject.transform, false);
            inputBlockerRect = inputBlockerRegionObject.GetComponent<RectTransform>();
            var image = inputBlockerRegionObject.GetComponent<Image>();
            image.color = Color.clear;
            image.raycastTarget = true;

            inputBlockerTooltipObject = new GameObject(
                "PathOfIdleSearchTooltipInputBlocker",
                Il2CppType.Of<RectTransform>(),
                Il2CppType.Of<CanvasRenderer>(),
                Il2CppType.Of<Image>());
            inputBlockerTooltipObject.transform.SetParent(inputBlockerCanvasObject.transform, false);
            inputBlockerTooltipRect = inputBlockerTooltipObject.GetComponent<RectTransform>();
            var tooltipImage = inputBlockerTooltipObject.GetComponent<Image>();
            tooltipImage.color = Color.clear;
            tooltipImage.raycastTarget = true;
            inputBlockerTooltipObject.SetActive(false);
        }
        catch (Exception error)
        {
            Plugin.DiagWarning($"Localized input blocker unavailable: {error.Message}");
            DestroyInputBlocker();
        }
    }

    [HideFromIl2Cpp]
    private void SetInputBlockerActive(bool active)
    {
        // Disabling only the transparent child images leaves an active overlay
        // Canvas/GraphicRaycaster in the scene. Hide the root as the invariant:
        // a closed overlay owns no UI input surface at all.
        if (inputBlockerCanvasObject is not null) inputBlockerCanvasObject.SetActive(active);
        if (inputBlockerRegionObject is not null) inputBlockerRegionObject.SetActive(active);
        if (inputBlockerTooltipObject is not null) inputBlockerTooltipObject.SetActive(active && activeTooltipRect.width > 0f && activeTooltipRect.height > 0f);
    }

    [HideFromIl2Cpp]
    private void DestroyInputBlocker()
    {
        if (inputBlockerCanvasObject is not null) UnityEngine.Object.Destroy(inputBlockerCanvasObject);
        inputBlockerCanvasObject = null;
        inputBlockerRegionObject = null;
        inputBlockerRect = null;
        inputBlockerTooltipObject = null;
        inputBlockerTooltipRect = null;
    }

    private void RestoreImeMode()
    {
        if (!imeModeSaved) return;
        Input.imeCompositionMode = previousImeMode;
        imeModeSaved = false;
    }

    private void EnableImeMode()
    {
        if (!imeModeSaved)
        {
            previousImeMode = Input.imeCompositionMode;
            imeModeSaved = true;
        }
        Input.imeCompositionMode = IMECompositionMode.On;
    }

    private void MaintainGameKeyboardBlock()
    {
        if (!GameInputGuard.TrySetKeyboardBlocked(true, out var previous))
        {
            if (!keyboardBlockUnavailableLogged)
            {
                keyboardBlockUnavailableLogged = true;
                Plugin.DiagWarning("Game keyboard input guard is waiting for Game.keyMgr.");
            }
            return;
        }

        keyboardBlockUnavailableLogged = false;
        if (!keyboardBlockCaptured)
        {
            previousGameKeyboardBlocked = previous;
            keyboardBlockCaptured = true;
            Plugin.DiagInfo($"Game keyboard input blocked (previous={previousGameKeyboardBlocked}).");
        }

        if (!GameInputGuard.TrySetUiNavigationBlocked(true, out var previousNavigation) || navigationBlockCaptured) return;
        previousNavigationEventsEnabled = previousNavigation;
        navigationBlockCaptured = true;
        Plugin.DiagInfo($"Game UI keyboard navigation blocked (previous={previousNavigationEventsEnabled}).");
    }

    private void TryReleaseGameInputGuards(bool force = false)
    {
        if (!keyboardBlockCaptured && !navigationBlockCaptured)
        {
            keyboardReleasePending = false;
            return;
        }
        if (!force && IsOverlayCloseKeyHeld()) return;

        if (keyboardBlockCaptured && (GameInputGuard.TrySetKeyboardBlocked(previousGameKeyboardBlocked, out _) || force))
        {
            Plugin.DiagInfo($"Game keyboard input restored to {previousGameKeyboardBlocked}.");
            keyboardBlockCaptured = false;
        }
        if (navigationBlockCaptured && (GameInputGuard.TrySetUiNavigationBlocked(!previousNavigationEventsEnabled, out _) || force))
        {
            Plugin.DiagInfo($"Game UI keyboard navigation restored to {previousNavigationEventsEnabled}.");
            navigationBlockCaptured = false;
        }
        keyboardReleasePending = keyboardBlockCaptured || navigationBlockCaptured;
    }

    private static bool IsOverlayCloseKeyHeld()
    {
        var controlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        return Input.GetKey(KeyCode.F3) || Input.GetKey(KeyCode.Escape) || (controlDown && Input.GetKey(KeyCode.F));
    }

    private static void ConsumeRemainingKeyboardEvent()
    {
        var overlay = activeOverlay;
        if (overlay?.overlayInputFocused != true || !overlay.IsPointerOverOverlay()) return;
        var current = Event.current;
        if (current is not null && current.type is EventType.KeyDown or EventType.KeyUp) current.Use();
    }

    private bool ShouldBlockPointerInput()
    {
        return visible && (resizing || dragging || IsPointerOverOverlay());
    }

    private bool IsPointerOverOverlay()
    {
        if (!visible) return false;
        var mouse = Input.mousePosition;
        var guiMouse = new Vector2(mouse.x, Screen.height - mouse.y);
        return windowRect.Contains(guiMouse) || activeTooltipRect.Contains(guiMouse);
    }

    private static string NormalizeLanguageMode(string? value)
    {
        var normalized = (value ?? "auto").Trim().ToLowerInvariant();
        return normalized is "auto" or "ko" or "en" or "zh-cn" or "zh-tw" ? normalized : "auto";
    }

    private void UpdateUiLanguage()
    {
        var code = languageMode == "auto" ? GameInventoryReader.GetGameLanguageCode() : languageMode;
        if (UiText.LanguageCode == code) return;
        UiText.SetLanguage(code);
        panelStyle = null;
        appliedStyleScale = -1f;
        Plugin.DiagInfo($"Mod UI language: {code} (mode={languageMode}).");
    }

    private string LanguageButtonLabel() => languageMode switch
    {
        "ko" => "한국어",
        "en" => "EN",
        "zh-cn" => "简中",
        "zh-tw" => "繁中",
        _ => UiText.LanguageCode switch
        {
            "ko" => "AUTO·KO",
            "zh-cn" => "AUTO·简",
            "zh-tw" => "AUTO·繁",
            _ => "AUTO·EN"
        }
    };

    private void CycleUiLanguage()
    {
        languageMode = languageMode switch
        {
            "auto" => "ko",
            "ko" => "en",
            "en" => "zh-cn",
            "zh-cn" => "zh-tw",
            _ => "auto"
        };
        Plugin.UiLanguage.Value = languageMode;
        UpdateUiLanguage();
        status = UiText.L("모드 언어가 변경되었습니다.", "Mod language changed.", "模组语言已更改。", "模組語言已變更。");
        currentPage = 0;
        RefreshCurrentPage(resetStatus: false);
    }

    [HideFromIl2Cpp]
    private void DrawPageTabs()
    {
        var y = windowRect.y + S(42f);
        var x = windowRect.xMax - S(394f);
        DrawPageTab(new Rect(x, y, S(94f), S(27f)), OverlayPage.Search, UiText.L("검색", "Search", "搜索", "搜尋"));
        DrawPageTab(new Rect(x + S(100f), y, S(94f), S(27f)), OverlayPage.BulkOpen, UiText.L("일괄 개봉", "Bulk Open", "批量开启", "批次開啟"));
        DrawPageTab(new Rect(x + S(200f), y, S(94f), S(27f)), OverlayPage.AutoBuild, UiText.L("자동 빌드", "Auto Build", "自动配装", "自動配裝"));
        DrawPageTab(new Rect(x + S(300f), y, S(94f), S(27f)), OverlayPage.Workshop, UiText.L("장비 강화", "Forge", "装备强化", "裝備強化"));
    }

    [HideFromIl2Cpp]
    private void DrawPageTab(Rect rect, OverlayPage page, string label)
    {
        var previousBackground = GUI.backgroundColor;
        var previousEnabled = GUI.enabled;
        if (IsAutoBuildRunning || (bulkSession is not null && page != OverlayPage.BulkOpen)) GUI.enabled = false;
        if (selectedPage == page) GUI.backgroundColor = new Color(0.30f, 0.70f, 0.96f);
        if (GUI.Button(rect, label, compactButtonStyle!) && selectedPage != page)
        {
            selectedPage = page;
            focusSearch = page == OverlayPage.Search;
            focusSpeedInput = false;
            focusPlanningTimeInput = false;
            if (page != OverlayPage.Search)
            {
                RestoreImeMode();
            }
            armedBulkOpen = BulkToolKind.None;
            armedAutoBuild = AutoBuildAction.None;
            forgeOptimizationArmed = false;
            forgeMaxAllArmed = false;
            mythicRefineArmed = false;
            mythicRefineConfirmedItemKey = string.Empty;
            mythicRefineConfirmedCandidateCount = 0;
            mythicRefineConfirmedBatchToken = string.Empty;
            nextRefreshAt = 0f;
            RefreshCurrentPage();
        }
        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    private void RefreshCurrentPage(bool resetStatus = true)
    {
        if (IsAutoBuildRunning) return;
        if (languageMode == "auto") UpdateUiLanguage();
        if (selectedPage == OverlayPage.AutoBuild)
        {
            var summary = GameInventoryReader.GetSelectedHeroBuildSummary();
            selectedHeroSummary = summary.Hero;
            selectedHeroProfile = summary.Profile;
            selectedAutoBuildTheme = GameInventoryReader.GetSelectedHeroBuildTheme();
            if (resetStatus) status = summary.Status;
            return;
        }
        if (selectedPage == OverlayPage.Workshop)
        {
            var summary = GameInventoryReader.GetSelectedHeroBuildSummary();
            selectedHeroSummary = summary.Hero;
            selectedHeroProfile = summary.Profile;
            selectedAutoBuildTheme = GameInventoryReader.GetSelectedHeroBuildTheme();
            if (resetStatus)
                status = string.IsNullOrWhiteSpace(summary.Hero)
                    ? UiText.L(
                        "선택된 캐릭터가 없어 장착 장비 강화는 사용할 수 없습니다. Mythic 정련은 캐릭터 선택 없이 사용할 수 있습니다.",
                        "Equipped-gear Forge is unavailable without a selected character. Mythic refinement remains available without one.",
                        "未选择角色，无法强化已装备物品；Mythic 精炼无需选择角色即可使用。",
                        "未選擇角色，無法強化已裝備物品；Mythic 精煉無需選擇角色即可使用。")
                    : UiText.L(
                        "현재 장비의 최적 +1 강화 또는 확인 후 가능한 Legendary 전체 Mythic 일괄 정련을 실행할 수 있습니다.",
                        "Ready for equipped-gear +1 optimization or an explicitly confirmed batch of every possible Legendary-to-Mythic refinement.",
                        "可优化当前已装备物品的 +1 强化，或在明确确认后批量精炼所有可执行的 Legendary。",
                        "可最佳化目前已裝備物品的 +1 強化，或在明確確認後批次精煉所有可執行的 Legendary。");
            return;
        }
        if (selectedPage == OverlayPage.BulkOpen)
        {
            if (bulkSession is not null) return;
            var countsAvailable = RefreshBulkCounts();
            if (resetStatus && countsAvailable) status = UiText.L("개봉할 상자 종류와 등급을 선택하세요.", "Choose a box type and quality.", "请选择箱子类型和品质。", "請選擇箱子類型和品質。");
            return;
        }
        RefreshItems(resetStatus);
    }

    private bool RefreshBulkCounts()
    {
        if (IsAutoBuildRunning) return bulkCountsAvailable;
        var counts = GameInventoryReader.GetBulkToolCounts(Plugin.BulkQualityMask.Value, Plugin.BulkQualityAtLeast.Value);
        var recovered = bulkCountFailedLastRefresh && counts.Success;
        bulkCountsAvailable = counts.Success;
        equipmentBoxCount = counts.EquipmentBoxes;
        runeBoxCount = counts.RuneBoxes;
        if (!counts.Success)
        {
            bulkCountFailedLastRefresh = true;
            status = UiText.L(
                "상자 데이터를 읽지 못했습니다. 잠시 뒤 다시 시도하세요.",
                "Box data could not be read. Try again in a moment.",
                "无法读取箱子数据，请稍后重试。",
                "無法讀取箱子資料，請稍後再試。");
        }
        else
        {
            bulkCountFailedLastRefresh = false;
            if (recovered)
                status = UiText.L(
                    $"상자 데이터를 다시 읽었습니다 · 장비 {equipmentBoxCount:N0} / 룬 {runeBoxCount:N0}",
                    $"Box data recovered · gear {equipmentBoxCount:N0} / runes {runeBoxCount:N0}",
                    $"箱子数据已恢复 · 装备 {equipmentBoxCount:N0} / 符文 {runeBoxCount:N0}",
                    $"箱子資料已恢復 · 裝備 {equipmentBoxCount:N0} / 符文 {runeBoxCount:N0}");
        }
        return counts.Success;
    }

    private void SaveWindowPosition()
    {
        Plugin.WindowX.Value = windowRect.x;
        Plugin.WindowY.Value = windowRect.y;
        Plugin.WindowWidth.Value = windowRect.width;
        Plugin.WindowHeight.Value = windowRect.height;
        Plugin.SavedQuery.Value = query;
        Plugin.IncludeWarehouse.Value = includeWarehouse;
    }

    private void ChangeGameSpeed(int direction)
    {
        if (direction > 0)
        {
            foreach (var step in SpeedSteps)
                if (step > currentSpeed + 0.001f) { SetGameSpeed(step); return; }
            SetGameSpeed(SpeedSteps[^1]);
            return;
        }

        for (var index = SpeedSteps.Length - 1; index >= 0; index--)
            if (SpeedSteps[index] < currentSpeed - 0.001f) { SetGameSpeed(SpeedSteps[index]); return; }
        SetGameSpeed(SpeedSteps[0]);
    }

    private void SetGameSpeed(float speed)
    {
        if (!float.IsFinite(speed)) speed = 1f;
        currentSpeed = Mathf.Clamp(speed, 0.1f, 100f);
        speedInput = currentSpeed.ToString("0.##", CultureInfo.InvariantCulture);
        ApplyGameSpeed();
    }

    private void ApplyCustomSpeed()
    {
        var normalized = speedInput.Trim().Replace(',', '.');
        if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !float.IsFinite(parsed))
        {
            speedInput = currentSpeed.ToString("0.##", CultureInfo.InvariantCulture);
            status = UiText.L("배속은 0.1~100 사이 숫자로 입력하세요.", "Enter a speed from 0.1 to 100.", "请输入 0.1 到 100 的速度。", "請輸入 0.1 到 100 的速度。");
            return;
        }
        SetGameSpeed(parsed);
        focusSpeedInput = false;
        status = UiText.L($"배속 {currentSpeed:0.##}× 적용", $"Speed {currentSpeed:0.##}× applied", $"已应用 {currentSpeed:0.##}× 速度", $"已套用 {currentSpeed:0.##}× 速度");
    }

    private void ApplyGameSpeed()
    {
        Time.timeScale = currentSpeed;
        Plugin.GameSpeed.Value = currentSpeed;
        Plugin.DiagInfo($"Game speed set to {currentSpeed:0.##}x.");
    }

    private static string AutoBuildPlanningExplanation(int seconds)
    {
        if (seconds <= 0)
            return UiText.L(
                "장비와 스킬은 시간 제한 없이 최고안을 탐색하고, 후보별 성능은 30초 전투 미리보기로 비교합니다. 이는 전체 전투·조건부 효과의 정확한 DPS가 아닙니다.",
                "Gear and skills search for the best candidate without a time limit; each candidate uses a 30-second combat preview. This is not exact full-combat or conditional-effect DPS.",
                "装备与技能会不限时搜索最佳候选，每个候选使用 30 秒战斗预览；这并非完整战斗或条件效果的精确 DPS。",
                "裝備與技能會不限時搜尋最佳候選，每個候選使用 30 秒戰鬥預覽；這並非完整戰鬥或條件效果的精確 DPS。");

        return UiText.L(
            $"장비와 스킬은 {seconds:N0}초 동안 최고안을 탐색하고, 후보별 성능은 30초 전투 미리보기로 비교합니다. 이는 전체 전투·조건부 효과의 정확한 DPS가 아닙니다.",
            $"Gear and skills search for the best candidate for {seconds:N0} seconds; each candidate uses a 30-second combat preview. This is not exact full-combat or conditional-effect DPS.",
            $"装备与技能会搜索最佳候选 {seconds:N0} 秒，每个候选使用 30 秒战斗预览；这并非完整战斗或条件效果的精确 DPS。",
            $"裝備與技能會搜尋最佳候選 {seconds:N0} 秒，每個候選使用 30 秒戰鬥預覽；這並非完整戰鬥或條件效果的精確 DPS。");
    }

    private void SetAutoBuildThemePriority(int priority)
    {
        var normalized = Plugin.NormalizeAutoBuildThemePriority(priority);
        Plugin.AutoBuildThemePriority.Value = normalized;
        themePriorityInput = normalized.ToString(CultureInfo.InvariantCulture);
        focusThemePriorityInput = false;
        armedAutoBuild = AutoBuildAction.None;
        status = UiText.L(
            $"테마 우선 비율을 {normalized:N0}%로 설정했습니다.",
            $"Theme priority set to {normalized:N0}%.",
            $"主题优先比例已设为 {normalized:N0}% 。",
            $"主題優先比例已設為 {normalized:N0}% 。");
    }

    private void ApplyCustomThemePriority()
    {
        var normalized = themePriorityInput.Trim();
        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var parsed)
            || parsed is < 0 or > 100)
        {
            var configured = Plugin.NormalizeAutoBuildThemePriority(
                Plugin.AutoBuildThemePriority.Value);
            themePriorityInput = configured.ToString(CultureInfo.InvariantCulture);
            status = UiText.L(
                "테마 우선 비율은 0부터 100 사이의 숫자로 입력하세요.",
                "Enter a theme priority from 0 to 100.",
                "请输入 0 到 100 之间的主题优先比例。",
                "請輸入 0 到 100 之間的主題優先比例。");
            return;
        }

        SetAutoBuildThemePriority(parsed);
    }

    private void HandleThemePriorityInput(Rect inputRect)
    {
        var current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0)
        {
            focusThemePriorityInput = inputRect.Contains(current.mousePosition);
            if (focusThemePriorityInput)
            {
                focusSearch = false;
                focusSpeedInput = false;
                focusPlanningTimeInput = false;
                current.Use();
            }
        }
        if (!focusThemePriorityInput || current.type != EventType.KeyDown) return;

        var controlDown = current.control || current.command;
        if (current.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
        {
            ApplyCustomThemePriority();
        }
        else if (current.keyCode == KeyCode.Backspace)
        {
            if (themePriorityInput.Length > 0)
                themePriorityInput = themePriorityInput[..^1];
        }
        else if (controlDown && current.keyCode == KeyCode.A)
        {
            themePriorityInput = string.Empty;
        }
        else if (controlDown && current.keyCode == KeyCode.V)
        {
            var pasted = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
            if (pasted.Length <= 3 && pasted.All(char.IsDigit))
                themePriorityInput = pasted;
        }
        else if (!controlDown && char.IsDigit(current.character)
                              && themePriorityInput.Length < 3)
        {
            themePriorityInput += current.character;
        }
        current.Use();
    }

    private void SetAutoBuildPlanningSeconds(int seconds)
    {
        var normalized = Plugin.NormalizeAutoBuildPlanningSeconds(seconds);
        Plugin.AutoBuildPlanningSeconds.Value = normalized;
        planningTimeInput = normalized <= 0
            ? "∞"
            : normalized.ToString(CultureInfo.InvariantCulture);
        focusPlanningTimeInput = false;
        armedAutoBuild = AutoBuildAction.None;
        status = normalized <= 0
            ? UiText.L(
                "최고안 탐색 시간을 무제한으로 설정했습니다.",
                "Best-candidate search time set to unlimited.",
                "最佳候选搜索时间已设为无限。",
                "最佳候選搜尋時間已設為無限制。")
            : UiText.L(
                $"최고안 탐색 시간을 {normalized:N0}초로 설정했습니다.",
                $"Best-candidate search time set to {normalized:N0} seconds.",
                $"最佳候选搜索时间已设为 {normalized:N0} 秒。",
                $"最佳候選搜尋時間已設為 {normalized:N0} 秒。");
    }

    private void ApplyCustomPlanningTime()
    {
        var normalized = planningTimeInput.Trim();
        if (normalized is "∞"
            || string.Equals(normalized, "unlimited", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "무제한", StringComparison.Ordinal))
        {
            SetAutoBuildPlanningSeconds(0);
            return;
        }

        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0)
        {
            var configured = Plugin.NormalizeAutoBuildPlanningSeconds(
                Plugin.AutoBuildPlanningSeconds.Value);
            planningTimeInput = configured <= 0
                ? "∞"
                : configured.ToString(CultureInfo.InvariantCulture);
            status = UiText.L(
                "탐색 시간은 10 이상의 초 단위 숫자나 0(무제한)으로 입력하세요.",
                "Enter at least 10 seconds, or 0 for unlimited.",
                "请输入不少于 10 的秒数，或输入 0 表示无限。",
                "請輸入不少於 10 的秒數，或輸入 0 表示無限制。");
            return;
        }

        var raisedToMinimum = parsed is > 0 and < 10;
        SetAutoBuildPlanningSeconds(parsed);
        if (raisedToMinimum)
            status = UiText.L(
                "최소값이 10초이므로 탐색 시간을 10초로 설정했습니다.",
                "The minimum is 10 seconds, so the search time was set to 10 seconds.",
                "最小值为 10 秒，搜索时间已设为 10 秒。",
                "最小值為 10 秒，搜尋時間已設為 10 秒。");
    }

    private void HandlePlanningTimeInput(Rect inputRect)
    {
        var current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0)
        {
            focusPlanningTimeInput = inputRect.Contains(current.mousePosition);
            if (focusPlanningTimeInput)
            {
                focusSearch = false;
                focusSpeedInput = false;
                if (planningTimeInput == "∞") planningTimeInput = string.Empty;
                current.Use();
            }
        }
        if (!focusPlanningTimeInput || current.type != EventType.KeyDown) return;

        var controlDown = current.control || current.command;
        if (current.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
        {
            ApplyCustomPlanningTime();
        }
        else if (current.keyCode == KeyCode.Backspace)
        {
            if (planningTimeInput.Length > 0) planningTimeInput = planningTimeInput[..^1];
        }
        else if (controlDown && current.keyCode == KeyCode.A)
        {
            planningTimeInput = string.Empty;
        }
        else if (controlDown && current.keyCode == KeyCode.V)
        {
            var pasted = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
            if (pasted.Length <= 10) planningTimeInput = pasted;
        }
        else if (!controlDown && char.IsDigit(current.character)
                              && planningTimeInput.Length < 10)
        {
            if (planningTimeInput == "∞") planningTimeInput = string.Empty;
            planningTimeInput += current.character;
        }
        current.Use();
    }

    private void HandleSpeedInput(Rect speedRect)
    {
        var current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0)
        {
            focusSpeedInput = speedRect.Contains(current.mousePosition);
            if (focusSpeedInput)
            {
                focusSearch = false;
                current.Use();
            }
        }
        if (!focusSpeedInput || current.type != EventType.KeyDown) return;

        var controlDown = current.control || current.command;
        if (current.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
        {
            ApplyCustomSpeed();
        }
        else if (current.keyCode == KeyCode.Backspace)
        {
            if (speedInput.Length > 0) speedInput = speedInput[..^1];
        }
        else if (controlDown && current.keyCode == KeyCode.A)
        {
            speedInput = string.Empty;
        }
        else if (controlDown && current.keyCode == KeyCode.V)
        {
            var pasted = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
            if (pasted.Length <= 8) speedInput = pasted;
        }
        else if (!controlDown && (char.IsDigit(current.character) || current.character is '.' or ',') && speedInput.Length < 8)
        {
            speedInput += current.character;
        }
        current.Use();
    }

    private void HandleWindowDrag()
    {
        var current = Event.current;
        if (current is null) return;
        // Keep every title-bar control out of the draggable region. Previously the
        // language button overlapped this rectangle, so its left click started a drag.
        var header = new Rect(
            windowRect.x,
            windowRect.y,
            Math.Max(S(70f), windowRect.width - S(16f) - ResponsiveHeaderControlsWidth() - S(6f)),
            S(42f));
        var controlId = GUIUtility.GetControlID(DragControlHint, FocusType.Passive);
        switch (current.GetTypeForControl(controlId))
        {
            case EventType.MouseDown when current.button == 0 && header.Contains(current.mousePosition):
                GUIUtility.hotControl = controlId;
                dragging = true;
                dragOffset = current.mousePosition - new Vector2(windowRect.x, windowRect.y);
                current.Use();
                break;
            case EventType.MouseDrag when dragging && GUIUtility.hotControl == controlId:
                windowRect.x = current.mousePosition.x - dragOffset.x;
                windowRect.y = current.mousePosition.y - dragOffset.y;
                ClampWindowToScreen();
                UpdateInputBlocker();
                current.Use();
                break;
            case EventType.MouseUp when dragging && GUIUtility.hotControl == controlId:
                dragging = false;
                GUIUtility.hotControl = 0;
                ClampWindowToScreen();
                SaveWindowPosition();
                UpdateInputBlocker();
                current.Use();
                break;
        }
    }

    private void HandleWindowResize()
    {
        var current = Event.current;
        if (current is null) return;
        var controlId = GUIUtility.GetControlID(ResizeControlHint, FocusType.Passive);
        var handle = ResizeHandleRect();
        switch (current.GetTypeForControl(controlId))
        {
            case EventType.MouseDown when current.button == 0 && handle.Contains(current.mousePosition):
                GUIUtility.hotControl = controlId;
                resizing = true;
                dragging = false;
                resizeStartMouse = current.mousePosition;
                resizeStartSize = new Vector2(windowRect.width, windowRect.height);
                focusSearch = false;
                focusSpeedInput = false;
                focusPlanningTimeInput = false;
                hoveredItem = null;
                tooltipItemKey = string.Empty;
                tooltipAnchorRect = default;
                activeTooltipRect = default;
                tooltipScroll = Vector2.zero;
                overlayInputFocused = true;
                UpdateInputBlocker();
                current.Use();
                break;
            case EventType.MouseDrag when resizing && GUIUtility.hotControl == controlId:
                var delta = current.mousePosition - resizeStartMouse;
                var screenMaxWidth = Math.Max(1f, Screen.width - ScreenMargin - windowRect.x);
                var screenMaxHeight = Math.Max(1f, Screen.height - ScreenMargin - windowRect.y);
                var maxWidth = Math.Min(MaxWindowWidth, screenMaxWidth);
                var maxHeight = Math.Min(MaxWindowHeight, screenMaxHeight);
                var minWidth = Math.Min(MinWindowWidth, maxWidth);
                var minHeight = Math.Min(MinWindowHeight, maxHeight);
                windowRect.width = Mathf.Clamp(resizeStartSize.x + delta.x, minWidth, maxWidth);
                windowRect.height = Mathf.Clamp(resizeStartSize.y + delta.y, minHeight, maxHeight);
                ClampWindowToScreen();
                UpdateUiScale();
                appliedStyleScale = -1f;
                tooltipItemKey = string.Empty;
                tooltipAnchorRect = default;
                activeTooltipRect = default;
                UpdateInputBlocker();
                current.Use();
                break;
            case EventType.MouseUp when resizing && GUIUtility.hotControl == controlId:
                resizing = false;
                GUIUtility.hotControl = 0;
                ClampWindowToScreen();
                UpdateUiScale();
                SaveWindowPosition();
                UpdateInputBlocker();
                current.Use();
                break;
        }
    }

    private Rect ResizeHandleRect()
    {
        // Keep this comfortably larger than a normal button edge.  The old
        // 18-26 px corner was effectively impossible to acquire on a scaled or
        // high-DPI window, even though the resize event path itself worked.
        var size = Math.Max(38f, S(42f));
        return new Rect(windowRect.xMax - size, windowRect.yMax - size, size, size);
    }

    private void DrawResizeGrip()
    {
        var handle = ResizeHandleRect();
        var previousColor = GUI.color;
        var gripColor = resizing
            ? new Color(1f, 0.78f, 0.32f, 1f)
            : handle.Contains(Event.current?.mousePosition ?? new Vector2(-1f, -1f))
                ? new Color(0.92f, 0.94f, 1f, 1f)
                : new Color(0.74f, 0.76f, 0.82f, 0.95f);

        // Draw three nested right-angle marks instead of relying on a Unicode
        // triangle glyph that is absent from several of the game's fonts.
        GUI.color = new Color(0.08f, 0.085f, 0.11f, 0.92f);
        GUI.Box(handle, GUIContent.none, solidFillStyle!);
        GUI.color = gripColor;
        for (var index = 0; index < 3; index++)
        {
            var inset = S(7f + index * 7f);
            var thickness = Math.Max(2f, S(2f));
            var length = Math.Max(S(8f), handle.width - inset - S(5f));
            GUI.Box(new Rect(handle.xMax - length - S(4f), handle.yMax - inset, length, thickness), GUIContent.none, solidFillStyle!);
            GUI.Box(new Rect(handle.xMax - inset, handle.yMax - length - S(4f), thickness, length), GUIContent.none, solidFillStyle!);
        }
        GUI.color = previousColor;
    }

    private void DrawOpaquePanel(Rect rect, Color fill)
    {
        var previousColor = GUI.color;
        var previousBackground = GUI.backgroundColor;
        GUI.color = Color.white;
        GUI.backgroundColor = fill;
        GUI.Box(rect, GUIContent.none, solidFillStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.color = previousColor;
        GUI.Box(rect, GUIContent.none, panelStyle!);
    }

    private void DrawScrollableWrappedText(
        Rect rect,
        string text,
        GUIStyle style,
        ref Vector2 scroll,
        ref string scrollKey)
    {
        text ??= string.Empty;
        var nextKey = $"{UiText.LanguageCode}|{rect.width:0}|{rect.height:0}|{style.fontSize}|{text}";
        if (!string.Equals(scrollKey, nextKey, StringComparison.Ordinal))
        {
            scrollKey = nextKey;
            scroll = Vector2.zero;
        }

        var fullWidth = Math.Max(S(40f), rect.width - S(8f));
        var fullHeight = style.CalcHeight(new GUIContent(text), fullWidth);
        if (fullHeight <= rect.height)
        {
            scroll = Vector2.zero;
            GUI.Label(rect, text, style);
            return;
        }

        var contentWidth = Math.Max(S(40f), rect.width - S(24f));
        fullHeight = style.CalcHeight(new GUIContent(text), contentWidth);
        var contentHeight = fullHeight + S(6f);
        UpdateManualScroll(rect, contentHeight, ref scroll);
        DrawManualLabel(rect, scroll, new Rect(0f, 0f, contentWidth, fullHeight), text, style);
        DrawManualScrollbar(rect, contentHeight, scroll);
    }

    private float S(float value) => Mathf.Round(value * uiScale);

    private void UpdateUiScale()
    {
        var widthScale = windowRect.width / DefaultWindowWidth;
        var heightScale = windowRect.height / DefaultWindowHeight;
        var nextScale = Mathf.Clamp(Math.Min(widthScale, heightScale), MinUiScale, MaxUiScale);
        if (Math.Abs(nextScale - uiScale) < 0.001f) return;
        uiScale = nextScale;
        appliedStyleScale = -1f;
    }

    private void ClampWindowToScreen()
    {
        var availableWidth = Math.Max(1f, Screen.width - ScreenMargin * 2f);
        var availableHeight = Math.Max(1f, Screen.height - ScreenMargin * 2f);
        var maxWidth = Math.Min(MaxWindowWidth, availableWidth);
        var maxHeight = Math.Min(MaxWindowHeight, availableHeight);
        var minWidth = Math.Min(MinWindowWidth, maxWidth);
        var minHeight = Math.Min(MinWindowHeight, maxHeight);
        var repairedWidth = !float.IsFinite(windowRect.width) || windowRect.width <= 0f;
        var repairedHeight = !float.IsFinite(windowRect.height) || windowRect.height <= 0f;
        if (repairedWidth) windowRect.width = DefaultWindowWidth;
        if (repairedHeight) windowRect.height = DefaultWindowHeight;
        if (repairedWidth) Plugin.WindowWidth.Value = DefaultWindowWidth;
        if (repairedHeight) Plugin.WindowHeight.Value = DefaultWindowHeight;
        windowRect.width = Mathf.Clamp(windowRect.width, minWidth, maxWidth);
        windowRect.height = Mathf.Clamp(windowRect.height, minHeight, maxHeight);
        var maxX = Math.Max(ScreenMargin, Screen.width - ScreenMargin - windowRect.width);
        var maxY = Math.Max(ScreenMargin, Screen.height - ScreenMargin - windowRect.height);
        if (!float.IsFinite(windowRect.x)) windowRect.x = ScreenMargin;
        if (!float.IsFinite(windowRect.y)) windowRect.y = ScreenMargin;
        windowRect.x = Mathf.Clamp(windowRect.x, ScreenMargin, maxX);
        windowRect.y = Mathf.Clamp(windowRect.y, ScreenMargin, maxY);
    }

    [HideFromIl2Cpp]
    private void DrawResult(
        ItemSearchRecord item,
        Rect rect,
        Vector2? screenOffset = null,
        Rect? screenClip = null,
        bool pointerOwnedByTooltip = false)
    {
        var tooltipOwnsPointer = pointerOwnedByTooltip
                                 || (hoveredItem is not null && tooltipAnchorRect.Contains(Event.current.mousePosition));
        var previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = item.StorageKind == StorageKind.Inventory
            ? new Color(0.30f, 0.76f, 0.42f, 0.72f)
            : new Color(0.20f, 0.68f, 0.82f, 0.72f);
        GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;
        var nameWidth = Math.Max(S(80f), rect.width - S(290f));
        GUI.Label(new Rect(rect.x + S(10f), rect.y + S(6f), nameWidth, S(24f)),
            HighlightMatches(EllipsizeToWidth(item.Name, resultNameStyle!, nameWidth), item.StorageKind), resultNameStyle!);
        GUI.Label(new Rect(rect.xMax - S(272f), rect.y + S(6f), S(106f), S(24f)), item.QualityLabel, badgeStyle!);
        var transferLabel = item.StorageKind == StorageKind.Inventory
            ? UiText.L("창고로 이동", "Move to storage", "移至仓库", "移至倉庫")
            : UiText.L("인벤토리로 이동", "Move to inventory", "移至背包", "移至背包");
        var transferRect = new Rect(rect.xMax - S(156f), rect.y + S(4f), S(146f), S(32f));
        var transferScreenRect = screenOffset is { } offset
            ? new Rect(transferRect.x + offset.x, transferRect.y + offset.y, transferRect.width, transferRect.height)
            : transferRect;
        if (screenClip is not { } clip || clip.Overlaps(transferScreenRect))
            visibleTransferRects.Add(transferScreenRect);
        var previousEnabled = GUI.enabled;
        if (tooltipOwnsPointer) GUI.enabled = false;
        if (GUI.Button(transferRect, transferLabel, compactButtonStyle!)) TransferItem(item);
        GUI.enabled = previousEnabled;
        var level = item.Level is > 0 ? $"Lv.{item.Level}" : UiText.L("레벨 미상", "Unknown level", "等级未知", "等級未知");
        var textWidth = Math.Max(S(80f), rect.width - S(20f));
        var metaText = $"{item.StorageLabel}  ·  {item.PartName}  ·  {level}";
        GUI.Label(new Rect(rect.x + S(10f), rect.y + S(35f), textWidth, S(19f)),
            HighlightMatches(EllipsizeToWidth(metaText, resultMetaStyle!, textWidth), item.StorageKind), resultMetaStyle!);
        var optionPreview = string.IsNullOrWhiteSpace(item.SetName)
            ? item.AffixSummary
            : $"{UiText.L("세트", "Set", "套装", "套裝")} {item.SetName}  ·  {item.AffixSummary}".TrimEnd(' ', '·');
        if (!string.IsNullOrWhiteSpace(optionPreview))
            GUI.Label(new Rect(rect.x + S(10f), rect.y + S(58f), textWidth, S(19f)),
                HighlightMatches(EllipsizeToWidth(optionPreview, resultAffixStyle!, textWidth), item.StorageKind), resultAffixStyle!);
        // Deliberately do not open a second hover panel beside the Search window.
    }

    [HideFromIl2Cpp]
    private void DrawItemTooltip(ItemSearchRecord item)
    {
        var tooltipWidth = Math.Min(S(620f), Screen.width - ScreenMargin * 2f);
        var optionText = string.IsNullOrWhiteSpace(item.AffixSummary)
            ? UiText.L("옵션 없음", "No affixes", "无词缀", "無詞綴")
            : "• " + item.AffixSummary.Replace("  ·  ", "\n• ", StringComparison.Ordinal);
        var description = string.IsNullOrWhiteSpace(item.Description) ? UiText.L("별도 아이템 설명 없음", "No item description", "无物品说明", "無物品說明") : item.Description;
        var meta = $"{item.QualityLabel}  ·  {item.StorageLabel}  ·  {item.PartName}  ·  {(item.Level is > 0 ? $"Lv.{item.Level}" : UiText.L("레벨 미상", "Unknown level", "等级未知", "等級未知"))}";
        var setSection = string.IsNullOrWhiteSpace(item.SetName)
            ? string.Empty
            : $"\n\n{UiText.L("세트", "Set", "套装", "套裝")} · {item.SetName}\n{UiText.L("적용 직업", "Class", "适用职业", "適用職業")} · {item.SetJob}\n\n{UiText.L("구성 장비", "Set pieces", "套装部件", "套裝部件")}\n{item.SetMembers}\n\n{UiText.L("세트 효과", "Set bonuses", "套装效果", "套裝效果")}\n{item.SetBonuses}";
        var body = $"{meta}\n\n{UiText.L("설명", "Description", "说明", "說明")}\n{description}\n\n{UiText.L("전체 옵션", "All affixes", "全部词缀", "全部詞綴")}\n{optionText}{setSection}";
        var contentWidth = Math.Max(S(80f), tooltipWidth - S(48f));
        var titleWidth = Math.Max(S(80f), tooltipWidth - S(28f));
        var titleHeight = Math.Max(S(26f), tooltipTitleStyle!.CalcHeight(new GUIContent(item.Name), titleWidth));
        var bodyHeight = tooltipBodyStyle!.CalcHeight(new GUIContent(body), contentWidth);
        var tooltipHeight = Mathf.Clamp(bodyHeight + titleHeight + S(42f), S(190f), Screen.height - ScreenMargin * 2f);
        var itemKey = $"{UiText.LanguageCode}|{uiScale:0.###}|{windowRect.width:0}|{windowRect.height:0}|{item.StorageLabel}|{item.Name}|{item.Level}|{item.AffixSummary}|{item.Description}|{item.SetName}|{item.SetJob}|{item.SetMembers}|{item.SetBonuses}";
        if (!string.Equals(tooltipItemKey, itemKey, StringComparison.Ordinal))
        {
            tooltipItemKey = itemKey;
            tooltipScroll = Vector2.zero;
            var rightX = windowRect.xMax;
            var leftX = windowRect.x - tooltipWidth;
            var rightSpace = Screen.width - windowRect.xMax;
            var leftSpace = windowRect.x;
            var initialX = rightX + tooltipWidth <= Screen.width - ScreenMargin
                ? rightX
                : leftX >= ScreenMargin
                    ? leftX
                    : rightSpace >= leftSpace
                        ? Mathf.Clamp(rightX, ScreenMargin, Math.Max(ScreenMargin, Screen.width - tooltipWidth - ScreenMargin))
                        : Mathf.Clamp(leftX, ScreenMargin, Math.Max(ScreenMargin, Screen.width - tooltipWidth - ScreenMargin));
            var initialY = Mathf.Clamp(Event.current.mousePosition.y - S(24f), ScreenMargin, Math.Max(ScreenMargin, Screen.height - tooltipHeight - ScreenMargin));
            tooltipAnchorRect = new Rect(initialX, initialY, tooltipWidth, tooltipHeight);
        }
        // Keep the panel fixed after it opens. A mouse-following tooltip moves
        // its viewport away from the cursor, making its scrollbar impossible to
        // reach. Touch the main window edge so there is no disappearing gap.
        var x = Mathf.Clamp(tooltipAnchorRect.x, 0f, Math.Max(0f, Screen.width - tooltipWidth));
        var y = Mathf.Clamp(tooltipAnchorRect.y, 0f, Math.Max(0f, Screen.height - tooltipHeight));
        var rect = new Rect(x, y, tooltipWidth, tooltipHeight);
        tooltipAnchorRect = rect;
        activeTooltipRect = rect;
        DrawOpaquePanel(rect, new Color(0.025f, 0.025f, 0.035f, 1f));
        GUI.Label(new Rect(rect.x + S(14f), rect.y + S(10f), titleWidth, titleHeight), item.Name, tooltipTitleStyle!);
        var viewportTop = rect.y + S(16f) + titleHeight;
        var viewport = new Rect(rect.x + S(10f), viewportTop, rect.width - S(20f), Math.Max(S(70f), rect.yMax - viewportTop - S(10f)));
        var scrollHeight = Math.Max(viewport.height, bodyHeight + S(8f));
        UpdateManualScroll(viewport, scrollHeight, ref tooltipScroll);
        DrawManualLabel(viewport, tooltipScroll, new Rect(0f, 0f, contentWidth, bodyHeight), body, tooltipBodyStyle!);
        DrawManualScrollbar(viewport, scrollHeight, tooltipScroll);
        UpdateInputBlocker();
    }

    [HideFromIl2Cpp]
    private void TransferItem(ItemSearchRecord item)
    {
        if (IsAutoBuildRunning)
        {
            status = UiText.L("자동 빌드가 끝난 뒤 아이템을 이동하세요.", "Move items after Auto Build finishes.", "请在自动构筑完成后移动物品。", "請在自動配置完成後移動物品。");
            return;
        }
        if (Time.unscaledTime < transferCooldownUntil) return;
        transferCooldownUntil = Time.unscaledTime + 0.25f;
#if PATHOFIDLE_RUNTIME_TEST
        var transferStopwatch = Stopwatch.StartNew();
#endif
        if (!GameInventoryReader.TryTransfer(item, out var message))
        {
            status = message;
            Plugin.DiagWarning($"Item transfer failed: {message}");
#if PATHOFIDLE_RUNTIME_TEST
            Plugin.DiagInfo(
                $"ITEM TRANSFER PERFORMANCE|success=false|move={transferStopwatch.Elapsed.TotalMilliseconds:0.###}|items={allItems.Count}");
#endif
            return;
        }

        status = message;
        Plugin.DiagInfo(message);
        currentPage = 0;
#if PATHOFIDLE_RUNTIME_TEST
        var nativeMoveMilliseconds = transferStopwatch.Elapsed.TotalMilliseconds;
#endif
        // TryTransfer already verified and updated this one cached record. Avoid
        // rebuilding localized affix/set text for the entire Warehouse/Vault on
        // the click path; the periodic/manual full refresh remains authoritative.
        ApplyFilter(updateStatus: false);
        nextRefreshAt = Time.unscaledTime + InventoryIdleRefreshSeconds;
        status = message;
#if PATHOFIDLE_RUNTIME_TEST
        Plugin.DiagInfo(
            $"ITEM TRANSFER PERFORMANCE|success=true|move={nativeMoveMilliseconds:0.###}|total={transferStopwatch.Elapsed.TotalMilliseconds:0.###}|items={allItems.Count}|matches={matches.Count}");
#endif
    }

    [HideFromIl2Cpp]
    private void DrawQualityFilters(Rect rect)
    {
        var label = UiText.L("등급", "Quality", "品质", "品質");
        var labelWidth = Math.Max(S(72f), utilityTitleStyle!.CalcSize(new GUIContent(label)).x + S(12f));
        GUI.Label(new Rect(rect.x + S(4f), rect.y + S(7f), labelWidth - S(8f), S(24f)), label, utilityTitleStyle);
        var entries = new (int Quality, string Label)[]
        {
            (0, UiText.L("전체", "All", "全部", "全部")),
            (1, UiText.L("일반", "Common", "普通", "普通")),
            (2, UiText.L("고급", "Fine", "精良", "精良")),
            (3, UiText.L("희귀", "Rare", "稀有", "稀有")),
            (4, UiText.L("전설", "Legend", "传奇", "傳奇")),
            (5, UiText.L("신화", "Mythic", "神话", "神話")),
            (6, UiText.L("세트", "Set", "套装", "套裝")),
            (7, UiText.L("마법", "Magic", "魔法", "魔法")),
            (8, UiText.L("고유", "Unique", "独特", "獨特")),
            (-1, UiText.L("기타", "Other", "其他", "其他"))
        };
        var buttonGap = S(6f);
        var buttonStartX = rect.x + labelWidth;
        var buttonWidth = Math.Max(S(54f), (rect.width - labelWidth - buttonGap * 4f) / 5f);
        for (var index = 0; index < entries.Length; index++)
        {
            var row = index / 5;
            var column = index % 5;
            var entry = entries[index];
            DrawQualityButton(new Rect(buttonStartX + column * (buttonWidth + buttonGap), rect.y + S(3f) + row * S(34f), buttonWidth, S(30f)), entry.Quality, entry.Label);
        }

        var nextOptionsOnly = GUI.Toggle(new Rect(rect.x + S(4f), rect.y + S(72f), Math.Min(rect.width - S(8f), S(180f)), S(24f)), searchOptionsOnly, UiText.L(" 옵션만 검색", " Affixes only", " 仅搜索词缀", " 僅搜尋詞綴"), toggleStyle!);
        if (nextOptionsOnly != searchOptionsOnly)
        {
            searchOptionsOnly = nextOptionsOnly;
            Plugin.SearchOptionsOnly.Value = searchOptionsOnly;
            currentPage = 0;
            ApplyFilter();
        }
    }

    [HideFromIl2Cpp]
    private void DrawQualityButton(Rect rect, int quality, string label)
    {
        var previousBackground = GUI.backgroundColor;
        var selected = quality == 0 ? selectedQualityMask == 0 : QualityFilterLogic.IsSelected(selectedQualityMask, quality);
        if (selected) GUI.backgroundColor = quality == 6
            ? new Color(0.32f, 0.86f, 0.46f)
            : new Color(0.28f, 0.68f, 0.94f);
        if (GUI.Button(rect, selected ? $"✓ {label}" : label, compactButtonStyle!))
        {
            selectedQualityMask = quality == 0
                ? 0
                : selectedQualityMask ^ QualityFilterLogic.BitFor(quality);
            Plugin.QualityMask.Value = selectedQualityMask;
            Plugin.QualityFilter.Value = 0;
            currentPage = 0;
            ApplyFilter();
        }
        GUI.backgroundColor = previousBackground;
    }

    [HideFromIl2Cpp]
    private void DrawBulkOpenPanel(Rect rect)
    {
        if (armedBulkOpen != BulkToolKind.None && Time.unscaledTime > bulkConfirmUntil)
            armedBulkOpen = BulkToolKind.None;

        var previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.20f, 0.20f, 0.24f, 0.94f);
        GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;

        GUI.Label(new Rect(rect.x + S(18f), rect.y + S(14f), rect.width - S(36f), S(26f)), UiText.L("보유 상자 일괄 개봉", "Open Owned Boxes in Bulk", "批量开启持有的箱子", "批次開啟持有的箱子"), titleStyle!);
        GUI.Label(new Rect(rect.x + S(18f), rect.y + S(44f), rect.width - S(36f), S(46f)), UiText.L(
            "아래 등급은 상자 자체를 거르는 조건입니다. 상자에서 나올 보상 등급을 정하는 기능은 아닙니다.",
            "The quality filter selects the boxes themselves; it does not control reward quality.",
            "下方品质筛选的是箱子本身，并不会决定开出的奖励品质。",
            "下方品質篩選的是箱子本身，並不會決定開出的獎勵品質。"), hintStyle!);

        var panelEnabled = GUI.enabled;
        if (bulkSession is not null) GUI.enabled = false;
        var nextSkip = GUI.Toggle(new Rect(rect.x + S(18f), rect.y + S(96f), rect.width - S(36f), S(26f)), Plugin.SkipBulkConfirmation.Value, UiText.L(" 2단계 확인 생략 (판매 제외)", " Skip second confirmation (except Sell)", " 跳过二次确认（出售除外）", " 略過二次確認（出售除外）"), toggleStyle!);
        if (nextSkip != Plugin.SkipBulkConfirmation.Value)
        {
            Plugin.SkipBulkConfirmation.Value = nextSkip;
            armedBulkOpen = BulkToolKind.None;
            status = nextSkip
                ? UiText.L("일괄 개봉 확인을 생략합니다.", "Bulk opening will run with one click.", "批量开启将单击执行。", "批次開啟將單擊執行。")
                : UiText.L("일괄 개봉은 두 번 눌러야 실행됩니다.", "Bulk opening requires two clicks.", "批量开启需要点击两次。", "批次開啟需要點擊兩次。");
        }
        GUI.Label(new Rect(rect.x + S(18f), rect.y + S(124f), rect.width - S(36f), S(24f)), UiText.L("장비 상자 보상 처리", "Gear-box reward destination", "装备箱奖励处理", "裝備箱獎勵處理"), utilityTitleStyle!);
        var destinationGap = S(6f);
        var destinationWidth = (rect.width - S(36f) - destinationGap * 2f) / 3f;
        DrawBulkDestinationButton(new Rect(rect.x + S(18f), rect.y + S(150f), destinationWidth, S(32f)), BulkEquipmentDestination.Inventory, UiText.L("인벤토리", "Inventory", "背包", "背包"));
        DrawBulkDestinationButton(new Rect(rect.x + S(18f) + destinationWidth + destinationGap, rect.y + S(150f), destinationWidth, S(32f)), BulkEquipmentDestination.Warehouse, UiText.L("창고·Vault", "Warehouse", "仓库", "倉庫"));
        DrawBulkDestinationButton(new Rect(rect.x + S(18f) + (destinationWidth + destinationGap) * 2f, rect.y + S(150f), destinationWidth, S(32f)), BulkEquipmentDestination.Sell, UiText.L("Vault+판매", "Vault + Sell", "宝库+出售", "寶庫+出售"));
        GUI.Label(new Rect(rect.x + S(18f), rect.y + S(186f), rect.width - S(36f), S(38f)), BulkDestinationDescription(Plugin.OpenedEquipmentDestination.Value), hintStyle!);
        DrawBulkOpenButton(new Rect(rect.x + S(18f), rect.y + S(230f), (rect.width - S(42f)) / 2f, S(56f)), BulkToolKind.EquipmentBox, equipmentBoxCount, UiText.L("장비 상자", "Gear boxes", "装备箱", "裝備箱"));
        DrawBulkOpenButton(new Rect(rect.x + S(24f) + (rect.width - S(42f)) / 2f, rect.y + S(230f), (rect.width - S(42f)) / 2f, S(56f)), BulkToolKind.RuneBox, runeBoxCount, UiText.L("룬 상자", "Rune boxes", "符文箱", "符文箱"));

        GUI.Label(new Rect(rect.x + S(18f), rect.y + S(298f), rect.width - S(36f), S(26f)), UiText.L("상자 자체 등급", "Box quality", "箱子自身品质", "箱子本身品質"), utilityTitleStyle!);
        var qualityEntries = new (int Quality, string Label)[]
        {
            (0, UiText.L("전체", "All", "全部", "全部")),
            (1, UiText.L("일반", "Common", "普通", "普通")),
            (2, UiText.L("고급", "Fine", "精良", "精良")),
            (3, UiText.L("희귀", "Rare", "稀有", "稀有")),
            (4, UiText.L("전설", "Legend", "传奇", "傳奇")),
            (5, UiText.L("신화", "Mythic", "神话", "神話")),
            (6, UiText.L("세트", "Set", "套装", "套裝")),
            (7, UiText.L("마법", "Magic", "魔法", "魔法")),
            (8, UiText.L("고유", "Unique", "独特", "獨特")),
            (-1, UiText.L("기타", "Other", "其他", "其他"))
        };
        var qualityGap = S(6f);
        var qualityWidth = (rect.width - S(36f) - qualityGap * 4f) / 5f;
        for (var index = 0; index < qualityEntries.Length; index++)
        {
            var row = index / 5;
            var column = index % 5;
            var entry = qualityEntries[index];
            DrawBulkQualityButton(new Rect(rect.x + S(18f) + column * (qualityWidth + qualityGap), rect.y + S(328f) + row * S(42f), qualityWidth, S(36f)), entry.Quality, entry.Label);
        }
        var nextAtLeast = GUI.Toggle(new Rect(rect.x + S(18f), rect.y + S(416f), rect.width - S(36f), S(28f)), Plugin.BulkQualityAtLeast.Value, UiText.L(" 선택한 최저 등급 이상 모두", " Open the selected minimum quality or higher", " 开启所选最低品质及以上", " 開啟所選最低品質以上"), toggleStyle!);
        if (nextAtLeast != Plugin.BulkQualityAtLeast.Value)
        {
            Plugin.BulkQualityAtLeast.Value = nextAtLeast;
            if (nextAtLeast && Plugin.BulkQualityMask.Value != 0)
            {
                var lowestSelected = Enumerable.Range(1, 8)
                    .FirstOrDefault(quality => QualityFilterLogic.IsSelected(
                        Plugin.BulkQualityMask.Value, quality));
                if (lowestSelected > 0)
                    Plugin.BulkQualityMask.Value = QualityFilterLogic.BitFor(lowestSelected);
            }
            armedBulkOpen = BulkToolKind.None;
            RefreshBulkCounts();
        }
        GUI.enabled = panelEnabled;
        if (bulkSession is not null)
        {
            var runningText = BulkOpenProgressText(bulkSession);
            DrawScrollableWrappedText(
                new Rect(rect.x + S(18f), rect.y + S(454f), rect.width - S(150f), Math.Max(S(40f), rect.yMax - rect.y - S(472f))),
                runningText, hintStyle!, ref bulkStatusScroll, ref bulkStatusScrollKey);
            if (GUI.Button(new Rect(rect.xMax - S(122f), rect.y + S(454f), S(104f), S(34f)), UiText.L("중단", "Cancel", "取消", "取消"), buttonStyle!))
                bulkSession.CancelRequested = true;
        }
        else
        {
            DrawScrollableWrappedText(
                new Rect(rect.x + S(18f), rect.y + S(454f), rect.width - S(36f), Math.Max(S(44f), rect.yMax - rect.y - S(472f))),
                status, tooltipBodyStyle!, ref bulkStatusScroll, ref bulkStatusScrollKey);
        }
    }

    [HideFromIl2Cpp]
    private void DrawAutoBuildPanel(Rect rect)
    {
        if (armedAutoBuild != AutoBuildAction.None && Time.unscaledTime > autoBuildConfirmUntil)
            armedAutoBuild = AutoBuildAction.None;

        var previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.15f, 0.16f, 0.20f, 0.98f);
        GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;

        GUI.Label(new Rect(rect.x + S(20f), rect.y + S(18f), rect.width - S(140f), S(30f)), UiText.L("현재 선택 영웅 자동 최적화", "Optimize the Selected Hero", "自动优化当前英雄", "自動最佳化目前英雄"), titleStyle!);
        var panelEnabled = GUI.enabled;
        GUI.enabled = panelEnabled && !IsAutoBuildRunning;
        if (GUI.Button(new Rect(rect.xMax - S(112f), rect.y + S(16f), S(92f), S(30f)), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshCurrentPage();
        GUI.enabled = panelEnabled;
        GUI.Label(new Rect(rect.x + S(20f), rect.y + S(54f), rect.width - S(40f), S(48f)), UiText.L(
            "게임에서 영웅을 먼저 선택하세요. 공격 테마는 피해 근사치, 방어 테마는 생존 능력치를 우선하며 공식 추천 장비에는 가산점을 주지 않습니다.",
            "Select a hero first. Damage themes prioritize the damage proxy; Defense prioritizes survival stats. Official guide gear gets no score bonus.",
            "请先选择英雄。伤害主题优先伤害近似值，防御主题优先生存属性；官方指南装备不获得评分加成。",
            "請先選擇英雄。傷害主題優先傷害近似值，防禦主題優先生存屬性；官方指南裝備不獲得評分加成。"), hintStyle!);

        GUI.backgroundColor = new Color(0.24f, 0.42f, 0.62f, 0.90f);
        GUI.Box(new Rect(rect.x + S(20f), rect.y + S(108f), rect.width - S(40f), S(90f)), GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.Label(new Rect(rect.x + S(36f), rect.y + S(118f), rect.width - S(72f), S(28f)), string.IsNullOrWhiteSpace(selectedHeroSummary)
            ? UiText.L("선택된 영웅 없음", "No hero selected", "未选择英雄", "未選擇英雄")
            : selectedHeroSummary, utilityTitleStyle!);
        GUI.Label(new Rect(rect.x + S(36f), rect.y + S(150f), rect.width - S(72f), S(40f)), selectedHeroProfile, hintStyle!);

        GUI.Label(new Rect(rect.x + S(24f), rect.y + S(208f), rect.width - S(48f), S(24f)), UiText.L("빌드 테마", "Build theme", "构筑主题", "流派主題"), utilityTitleStyle!);
        var themes = new (string Key, string Label)[]
        {
            ("auto", UiText.L("자동", "Auto", "自动", "自動")),
            ("physical", UiText.L("물리", "Physical", "物理", "物理")),
            ("elemental", UiText.L("원소", "Elemental", "元素", "元素")),
            ("fire", UiText.L("화염", "Fire", "火焰", "火焰")),
            ("ice", UiText.L("냉기", "Ice", "冰霜", "冰霜")),
            ("lightning", UiText.L("번개", "Lightning", "闪电", "閃電")),
            ("minion", UiText.L("소환수", "Minion", "召唤", "召喚")),
            ("bleed", UiText.L("출혈", "Bleed", "流血", "流血")),
            ("corrosion", UiText.L("부식", "Corrosion", "腐蚀", "腐蝕")),
            ("crit", UiText.L("치명타", "Critical", "暴击", "暴擊")),
            ("support", UiText.L("지원", "Support", "辅助", "輔助")),
            ("defense", UiText.L("방어", "Defense", "防御", "防禦"))
        };
        var themeGap = S(6f);
        var themeWidth = (rect.width - S(48f) - themeGap * 5f) / 6f;
        for (var index = 0; index < themes.Length; index++)
        {
            var row = index / 6;
            var column = index % 6;
            DrawAutoBuildThemeButton(new Rect(rect.x + S(24f) + column * (themeWidth + themeGap), rect.y + S(234f) + row * S(36f), themeWidth, S(32f)), themes[index].Key, themes[index].Label);
        }

        GUI.enabled = panelEnabled && !IsAutoBuildRunning;
        var nextStorage = GUI.Toggle(new Rect(rect.x + S(24f), rect.y + S(310f), rect.width - S(48f), S(28f)), Plugin.AutoBuildIncludeStorage.Value, UiText.L(" 착용 중인 8부위와 창고·Vault 장비를 모두 후보에 포함", " Include equipped 8-slot gear, Warehouse, and Vault", " 将已装备的 8 个部位、仓库及宝库装备全部纳入候选", " 將已裝備的 8 個部位、倉庫及寶庫裝備全部納入候選"), toggleStyle!);
        if (nextStorage != Plugin.AutoBuildIncludeStorage.Value)
        {
            Plugin.AutoBuildIncludeStorage.Value = nextStorage;
            armedAutoBuild = AutoBuildAction.None;
        }
        var forgeExcluded = !Plugin.AutoBuildIncludeForgeBonuses.Value;
        var nextForgeExcluded = GUI.Toggle(new Rect(rect.x + S(24f), rect.y + S(340f), rect.width - S(48f), S(28f)), forgeExcluded, UiText.L(" 강화 수치 제외 (모든 장비를 0강 기준으로 비교)", " Ignore forge bonuses (compare every item at forge level 0)", " 排除强化数值（按全部装备 0 强比较）", " 排除強化數值（按全部裝備 0 強比較）"), toggleStyle!);
        if (nextForgeExcluded != forgeExcluded)
        {
            Plugin.AutoBuildIncludeForgeBonuses.Value = !nextForgeExcluded;
            armedAutoBuild = AutoBuildAction.None;
        }
        var nextTransform = GUI.Toggle(new Rect(rect.x + S(24f), rect.y + S(370f), rect.width - S(48f), S(38f)), Plugin.AutoTransformSkills.Value, UiText.L($" 최종 조합에 필요한 미보유 스킬도 변환 시도 (최대 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)}회)", $" Transform missing skills required by the final build (max {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)})", $" 尝试转换最终方案所需的未拥有技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）", $" 嘗試轉換最終配置所需的未擁有技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）"), toggleStyle!);
        if (nextTransform != Plugin.AutoTransformSkills.Value)
        {
            Plugin.AutoTransformSkills.Value = nextTransform;
            armedAutoBuild = AutoBuildAction.None;
        }
        GUI.enabled = panelEnabled;

        DrawAutoBuildButton(new Rect(rect.x + S(24f), rect.y + S(418f), rect.width - S(48f), S(58f)), AutoBuildAction.Combined,
            UiText.L("장비+스킬 자동 빌드", "Auto-build Gear + Skills", "自动构筑装备和技能", "自動配置裝備與技能"));
        var advancedWidth = (rect.width - S(60f)) / 2f;
        DrawAutoBuildButton(new Rect(rect.x + S(24f), rect.y + S(484f), advancedWidth, S(46f)), AutoBuildAction.Gear,
            UiText.L("고급: 장비만 적용", "Advanced: apply gear only", "高级：仅应用装备", "進階：僅套用裝備"));
        DrawAutoBuildButton(new Rect(rect.x + S(36f) + advancedWidth, rect.y + S(484f), advancedWidth, S(46f)), AutoBuildAction.Skills,
            UiText.L("고급: 현재 장비로 스킬만", "Advanced: skills for current gear", "高级：按当前装备仅调整技能", "進階：依目前裝備僅調整技能"));

        GUI.Label(
            new Rect(rect.x + S(24f), rect.y + S(540f), rect.width - S(48f), S(56f)),
            AutoBuildPlanningExplanation(Plugin.NormalizeAutoBuildPlanningSeconds(
                Plugin.AutoBuildPlanningSeconds.Value)),
            tooltipBodyStyle!);
        if (IsAutoBuildRunning && autoBuildOperation is { } running)
        {
            var progressHeight = S(76f);
            var progressTop = Math.Max(rect.y + S(602f), rect.yMax - progressHeight - S(10f));
            var cancelWidth = Math.Min(S(118f), Math.Max(S(88f), rect.width * 0.25f));
            var cancelRect = new Rect(
                rect.xMax - S(24f) - cancelWidth,
                progressTop,
                cancelWidth,
                S(28f));
            var phase = AutoBuildPhaseLabel(running);
            GUI.Label(new Rect(
                rect.x + S(24f),
                progressTop,
                Math.Max(S(72f), cancelRect.x - rect.x - S(34f)),
                S(28f)), phase, tooltipBodyStyle!);
            var canCancel = running.CanCancelPlanning;
            GUI.enabled = panelEnabled && canCancel;
            if (GUI.Button(cancelRect, UiText.L("계산 취소", "Cancel", "取消计算", "取消計算"), buttonStyle!))
                running.RequestCancel();
            GUI.enabled = panelEnabled;
            var barRect = new Rect(
                rect.x + S(24f),
                progressTop + S(34f),
                Math.Max(S(80f), rect.width - S(48f)),
                S(10f));
            DrawAutoBuildProgressBar(barRect, running);
            GUI.Label(new Rect(
                    rect.x + S(24f),
                    progressTop + S(48f),
                    rect.width - S(48f),
                    S(24f)),
                AutoBuildProgressMetaLabel(running), hintStyle!);
        }
        else
        {
            GUI.Label(new Rect(rect.x + S(24f), rect.y + S(602f), rect.width - S(48f), S(46f)), UiText.L(
                "주의: 스킬 변환·특성 초기화에는 게임의 정상 비용이 듭니다. 초기화 비용은 남겨 두며, 실행 버튼은 두 번 눌러야 합니다.",
                "Caution: skill transformation and talent reset use normal game costs. Reset cost is reserved; each action requires a second click.",
                "注意：技能转换与天赋重置会消耗游戏正常费用。系统会预留重置费用；操作需点击两次。",
                "注意：技能轉換與天賦重設會消耗遊戲正常費用。系統會保留重設費用；操作需點擊兩次。"), hintStyle!);
            var statusTop = Math.Min(rect.y + S(654f), rect.yMax - S(48f));
            DrawScrollableWrappedText(
                new Rect(rect.x + S(24f), statusTop, rect.width - S(48f), Math.Max(S(40f), rect.yMax - statusTop - S(10f))),
                status, tooltipBodyStyle!, ref autoBuildStatusScroll, ref autoBuildStatusScrollKey);
        }
    }

    [HideFromIl2Cpp]
    private void DrawAutoBuildThemeButton(Rect rect, string key, string label)
    {
        var selected = string.Equals(selectedAutoBuildTheme, key, StringComparison.OrdinalIgnoreCase);
        var previousBackground = GUI.backgroundColor;
        var previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !IsAutoBuildRunning;
        if (selected) GUI.backgroundColor = new Color(0.34f, 0.78f, 0.58f);
        if (GUI.Button(rect, label, compactButtonStyle!))
        {
            GameInventoryReader.SetSelectedHeroBuildTheme(key);
            selectedAutoBuildTheme = GameInventoryReader.NormalizeBuildTheme(key);
            armedAutoBuild = AutoBuildAction.None;
            RefreshCurrentPage();
        }
        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    [HideFromIl2Cpp]
    private void DrawAutoBuildButton(Rect rect, AutoBuildAction action, string label)
    {
        var armed = armedAutoBuild == action && Time.unscaledTime <= autoBuildConfirmUntil;
        var previousEnabled = GUI.enabled;
        var previousBackground = GUI.backgroundColor;
        GUI.enabled = previousEnabled && !IsAutoBuildRunning && bulkSession is null && !string.IsNullOrWhiteSpace(selectedHeroSummary);
        GUI.backgroundColor = armed ? new Color(1f, 0.48f, 0.18f) : new Color(0.32f, 0.68f, 0.94f);
        var text = armed ? UiText.L($"확인: {label}", $"Confirm: {label}", $"确认：{label}", $"確認：{label}") : label;
        if (GUI.Button(rect, text, buttonStyle!)) BeginOrConfirmAutoBuild(action, label);
        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    [HideFromIl2Cpp]
    private void BeginOrConfirmAutoBuild(AutoBuildAction action, string label)
    {
        if (IsAutoBuildRunning || bulkSession is not null) return;
        if (armedAutoBuild != action || Time.unscaledTime > autoBuildConfirmUntil)
        {
            armedAutoBuild = action;
            autoBuildConfirmUntil = Time.unscaledTime + 5f;
            status = UiText.L($"{label}: 같은 버튼을 한 번 더 누르면 실행합니다.", $"{label}: click again to run.", $"{label}：再次点击即可执行。", $"{label}：再次點擊即可執行。");
            return;
        }

        armedAutoBuild = AutoBuildAction.None;
        QueueAutoBuild(action, label, Plugin.AutoBuildIncludeStorage.Value);
    }

    [HideFromIl2Cpp]
    private void QueueAutoBuild(
        AutoBuildAction action,
        string label,
        bool includeStorage,
        bool allowRuntimeTestSelectionLease = false)
    {
        if (HasActiveAutoBuildWorker
            || GameInventoryReader.WarehouseEquipmentSaleActive
            || (!allowRuntimeTestSelectionLease
                && GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)) return;
        if (!GameInventoryReader.TryCaptureAutoBuildTarget(out var targetHeroKey, out var targetReason))
        {
            status = targetReason;
            nextRefreshAt = Time.unscaledTime + 0.5f;
            return;
        }
        var operation = new AutoBuildOperation(
            label,
            Plugin.AutoTransformSkills.Value,
            Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50),
            Plugin.NormalizeAutoBuildPlanningSeconds(Plugin.AutoBuildPlanningSeconds.Value),
            Plugin.NormalizeAutoBuildThemePriority(Plugin.AutoBuildThemePriority.Value),
            Plugin.AutoBuildTheme.Value,
            Plugin.AutoBuildHeroThemes.Value,
            targetHeroKey,
            Plugin.AutoBuildIncludeForgeBonuses.Value);
        autoBuildOperation = operation;
        status = UiText.L(
            $"{label} 작업을 준비하는 중입니다.",
            $"Preparing {label}.",
            $"正在准备{label}。",
            $"正在準備{label}。");
        nextRefreshAt = float.PositiveInfinity;
        autoBuildTask = Task.Run(() => RunAutoBuildWorker(operation, action, includeStorage));
    }

    private static AutoBuildWorkerResult RunAutoBuildWorker(
        AutoBuildOperation operation,
        AutoBuildAction action,
        bool includeStorage)
    {
        var result = new AutoBuildWorkerResult(false, false, string.Empty);
        using var scope = MainThreadNativeGateway.AttachWorker(operation);
        try
        {
#if PATHOFIDLE_RUNTIME_TEST
            if (action == AutoBuildAction.Combined)
                GameInventoryReader.BeginRuntimeJointBuildProgress();
#endif
            GameInventoryReader.WaitForAutoBuildReadiness();
            var message = UiText.L(
                $"알 수 없는 자동 빌드 작업입니다 ({action}). 아무 작업도 실행하지 않았습니다.",
                $"Unknown auto-build action ({action}). Nothing was changed.",
                $"未知的自动构筑操作（{action}）。未执行任何操作。",
                $"未知的自動配置操作（{action}）。未執行任何操作。");
            var succeeded = action switch
            {
                AutoBuildAction.Combined => GameInventoryReader.TryOptimizeSelectedHeroBuild(includeStorage, out message),
                AutoBuildAction.Gear => GameInventoryReader.TryOptimizeSelectedHeroGear(includeStorage, out message),
                AutoBuildAction.Skills => GameInventoryReader.TryOptimizeSelectedHeroSkills(out message),
                _ => false
            };
            result = operation.CancelRequested && !operation.MutationStarted
                ? new AutoBuildWorkerResult(false, true, UiText.L("자동 빌드 계산을 취소했습니다.", "Auto Build calculation cancelled.", "已取消自动构筑计算。", "已取消自動配置計算。"))
                : new AutoBuildWorkerResult(succeeded, false, message);
        }
        catch (OperationCanceledException)
        {
            result = new AutoBuildWorkerResult(false, true,
                UiText.L("자동 빌드 계산을 취소했습니다.", "Auto Build calculation cancelled.", "已取消自动构筑计算。", "已取消自動配置計算。"));
        }
        catch (Exception error)
        {
            result = new AutoBuildWorkerResult(false, false, UiText.L(
                $"자동 빌드 실패: {error.GetBaseException().Message}",
                $"Auto Build failed: {error.GetBaseException().Message}",
                $"自动构筑失败：{error.GetBaseException().Message}",
                $"自動配置失敗：{error.GetBaseException().Message}"));
        }
        finally
        {
            operation.MarkCompleted(result.Cancelled);
            if (!operation.MutationStarted && operation.CancelRequested && !result.Cancelled)
                result = new AutoBuildWorkerResult(false, true,
                    UiText.L("자동 빌드 계산을 취소했습니다.", "Auto Build calculation cancelled.", "已取消自动构筑计算。", "已取消自動配置計算。"));
#if PATHOFIDLE_RUNTIME_TEST
            if (action == AutoBuildAction.Combined)
                GameInventoryReader.EndRuntimeJointBuildProgress();
#endif
        }
        return result;
    }

    private void CompleteAutoBuildIfReady()
    {
        var task = autoBuildTask;
        if (task is null || !task.IsCompleted) return;
        AutoBuildWorkerResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            result = new AutoBuildWorkerResult(false, false, UiText.L(
                $"자동 빌드 실패: {error.GetBaseException().Message}",
                $"Auto Build failed: {error.GetBaseException().Message}",
                $"自动构筑失败：{error.GetBaseException().Message}",
                $"自動配置失敗：{error.GetBaseException().Message}"));
        }

        autoBuildTask = null;
        autoBuildOperation = null;
        status = result.Message;
#if PATHOFIDLE_RUNTIME_TEST
        if (runtimeTestQueued)
        {
            runtimeTestStopwatch?.Stop();
            lock (RuntimeTestResultSync)
            {
                runtimeTestResult = result;
                runtimeTestElapsedMilliseconds = runtimeTestStopwatch?.ElapsedMilliseconds ?? 0;
                runtimeTestPostState = GameInventoryReader.CaptureRuntimeJointBuildPostState();
            }
            runtimeTestQueued = false;
            runtimeTestStopwatch = null;
        }
#endif
        if (!result.Cancelled)
        {
            if (result.Succeeded) Plugin.DiagInfo(result.Message); else Plugin.DiagWarning(result.Message);
            var summary = GameInventoryReader.GetSelectedHeroBuildSummary();
            selectedHeroSummary = summary.Hero;
            selectedHeroProfile = summary.Profile;
            selectedAutoBuildTheme = GameInventoryReader.GetSelectedHeroBuildTheme();
        }
        nextRefreshAt = Time.unscaledTime + 0.5f;
    }

    private void BeginOrConfirmForgeOptimization()
    {
        if (IsAutoBuildRunning || bulkSession is not null || warehouseSaleSession is not null) return;
        if (!forgeOptimizationArmed || Time.unscaledTime > forgeOptimizationConfirmUntil)
        {
            forgeOptimizationArmed = true;
            forgeMaxAllArmed = false;
            forgeOptimizationConfirmUntil = Time.unscaledTime + 6f;
            armedAutoBuild = AutoBuildAction.None;
            armedBulkOpen = BulkToolKind.None;
            status = UiText.L(
                "실제 강화 재료를 사용해 최적 장비 한 개를 +1 강화합니다. 같은 버튼을 다시 누르세요.",
                "This spends real Forge resources and applies +1 to one optimal item. Click the same button again.",
                "这会消耗真实强化材料，并将一件最佳装备强化 +1。请再次点击同一按钮。",
                "這會消耗真實強化材料，並將一件最佳裝備強化 +1。請再次點擊同一按鈕。");
            return;
        }

        forgeOptimizationArmed = false;
        if (!GameInventoryReader.TryCaptureAutoBuildTarget(
                out var targetHeroKey, out var targetReason))
        {
            status = targetReason;
            nextRefreshAt = Time.unscaledTime + 0.5f;
            return;
        }

        var label = UiText.L(
            "장비 강화 최적화",
            "Gear Forge optimization",
            "装备强化优化",
            "裝備強化最佳化");
        var operation = new AutoBuildOperation(
            label,
            autoTransformSkills: false,
            autoTransformMaxAttempts: 0,
            planningLimitSeconds: 0,
            Plugin.NormalizeAutoBuildThemePriority(Plugin.AutoBuildThemePriority.Value),
            Plugin.AutoBuildTheme.Value,
            Plugin.AutoBuildHeroThemes.Value,
            targetHeroKey,
            includeForgeBonuses: true);
        forgeOptimizationOperation = operation;
        status = UiText.L(
            "현재 장비의 강화 전·현재·+1 수치와 30초 성능을 비교하는 중입니다.",
            "Comparing un-forged, current, and +1 values with 30-second performance.",
            "正在比较无强化、当前及 +1 数值与 30 秒性能。",
            "正在比較無強化、目前及 +1 數值與 30 秒效能。");
        nextRefreshAt = float.PositiveInfinity;
        forgeOptimizationTask = Task.Run(() => RunForgeOptimizationWorker(operation));
    }

    private void BeginOrConfirmForgeMaximum()
    {
        if (IsAutoBuildRunning || bulkSession is not null || warehouseSaleSession is not null) return;
        if (!forgeMaxAllArmed || Time.unscaledTime > forgeOptimizationConfirmUntil)
        {
            forgeMaxAllArmed = true;
            forgeOptimizationArmed = false;
            forgeOptimizationConfirmUntil = Time.unscaledTime + 6f;
            armedAutoBuild = AutoBuildAction.None;
            armedBulkOpen = BulkToolKind.None;
            mythicRefineArmed = false;
            mythicRefineConfirmedItemKey = string.Empty;
            mythicRefineConfirmedCandidateCount = 0;
            mythicRefineConfirmedBatchToken = string.Empty;
            status = UiText.L(
                "실제 강화 재료를 사용해 현재 착용 장비를 모두 작업장 상한까지 강화합니다. 같은 버튼을 다시 누르세요.",
                "This spends real Forge resources to raise all equipped gear to the Workshop limit. Click the same button again.",
                "这会消耗真实强化材料，将所有已装备物品强化至工坊上限。请再次点击同一按钮。",
                "這會消耗真實強化材料，將所有已裝備物品強化至工坊上限。請再次點擊同一按鈕。");
            return;
        }

        forgeMaxAllArmed = false;
        if (!GameInventoryReader.TryCaptureAutoBuildTarget(
                out var targetHeroKey, out var targetReason))
        {
            status = targetReason;
            nextRefreshAt = Time.unscaledTime + 0.5f;
            return;
        }

        var operation = new AutoBuildOperation(
            UiText.L(
                "착용 장비 최대 강화",
                "Maximum equipped-gear Forge",
                "已装备物品最大强化",
                "已裝備物品最大強化"),
            autoTransformSkills: false,
            autoTransformMaxAttempts: 0,
            planningLimitSeconds: 0,
            Plugin.NormalizeAutoBuildThemePriority(Plugin.AutoBuildThemePriority.Value),
            Plugin.AutoBuildTheme.Value,
            Plugin.AutoBuildHeroThemes.Value,
            targetHeroKey,
            includeForgeBonuses: true);
        forgeOptimizationOperation = operation;
        status = UiText.L(
            "현재 착용 장비를 낮은 강화 단계부터 +5 묶음으로 강화하는 중입니다.",
            "Raising currently equipped gear in +5 batches, starting with the lowest Forge level.",
            "正在从最低强化等级开始，以 +5 批次强化已装备物品。",
            "正在從最低強化等級開始，以 +5 批次強化已裝備物品。");
        nextRefreshAt = float.PositiveInfinity;
        forgeOptimizationTask = Task.Run(() => RunForgeMaximumWorker(operation));
    }

    private static EquipmentForgeOptimizationResult RunForgeOptimizationWorker(
        AutoBuildOperation operation)
    {
        var result = new EquipmentForgeOptimizationResult(
            false, false, false, string.Empty, null);
        using var scope = MainThreadNativeGateway.AttachWorker(operation);
        try
        {
            result = GameInventoryReader.OptimizeSelectedHeroForgeOneLevel();
        }
        catch (OperationCanceledException)
        {
            result = new EquipmentForgeOptimizationResult(
                false, true, false,
                UiText.L(
                    "장비 강화 최적화 계산을 취소했습니다. 자원은 사용하지 않았습니다.",
                    "Forge optimization was cancelled. No resources were spent.",
                    "已取消装备强化优化。未消耗资源。",
                    "已取消裝備強化最佳化。未消耗資源。"),
                null);
        }
        catch (Exception error)
        {
            result = new EquipmentForgeOptimizationResult(
                false, false, operation.MutationStarted,
                UiText.L(
                    $"장비 강화 최적화 실패: {error.GetBaseException().Message}",
                    $"Forge optimization failed: {error.GetBaseException().Message}",
                    $"装备强化优化失败：{error.GetBaseException().Message}",
                    $"裝備強化最佳化失敗：{error.GetBaseException().Message}"),
                null);
        }
        finally
        {
            operation.MarkCompleted(result.Cancelled);
        }
        return result;
    }

    private static EquipmentForgeOptimizationResult RunForgeMaximumWorker(
        AutoBuildOperation operation)
    {
        var result = new EquipmentForgeOptimizationResult(
            false, false, false, string.Empty, null);
        using var scope = MainThreadNativeGateway.AttachWorker(operation);
        try
        {
            result = GameInventoryReader.MaxForgeSelectedHeroEquipment();
        }
        catch (Exception error)
        {
            result = new EquipmentForgeOptimizationResult(
                false, false, operation.MutationStarted,
                UiText.L(
                    $"착용 장비 최대 강화 실패: {error.GetBaseException().Message}",
                    $"Maximum equipped-gear Forge failed: {error.GetBaseException().Message}",
                    $"已装备物品最大强化失败：{error.GetBaseException().Message}",
                    $"已裝備物品最大強化失敗：{error.GetBaseException().Message}"),
                null);
        }
        finally
        {
            operation.MarkCompleted(result.Cancelled);
        }
        return result;
    }

    private void CompleteForgeOptimizationIfReady()
    {
        var task = forgeOptimizationTask;
        if (task is null || !task.IsCompleted) return;
        EquipmentForgeOptimizationResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            result = new EquipmentForgeOptimizationResult(
                false, false, forgeOptimizationOperation?.MutationStarted == true,
                UiText.L(
                    $"장비 강화 최적화 실패: {error.GetBaseException().Message}",
                    $"Forge optimization failed: {error.GetBaseException().Message}",
                    $"装备强化优化失败：{error.GetBaseException().Message}",
                    $"裝備強化最佳化失敗：{error.GetBaseException().Message}"),
                null);
        }

        forgeOptimizationTask = null;
        forgeOptimizationOperation = null;
        forgeOptimizationArmed = false;
        forgeMaxAllArmed = false;
        status = result.Message;
        if (result.Succeeded) Plugin.DiagInfo(result.Message);
        else if (!result.Cancelled) Plugin.DiagWarning(result.Message);
        var summary = GameInventoryReader.GetSelectedHeroBuildSummary();
        selectedHeroSummary = summary.Hero;
        selectedHeroProfile = summary.Profile;
        selectedAutoBuildTheme = GameInventoryReader.GetSelectedHeroBuildTheme();
        nextRefreshAt = Time.unscaledTime + 0.5f;
    }

    private void BeginOrConfirmMythicRefine()
    {
        if (IsAutoBuildRunning || bulkSession is not null || warehouseSaleSession is not null) return;
        var applyMutation = mythicRefineArmed
                            && Time.unscaledTime <= mythicRefineConfirmUntil;
        var expectedItemKey = applyMutation
            ? mythicRefineConfirmedItemKey
            : string.Empty;
        var expectedCandidateCount = applyMutation
            ? mythicRefineConfirmedCandidateCount
            : 0;
        var expectedCandidateSetToken = applyMutation
            ? mythicRefineConfirmedBatchToken
            : string.Empty;
        mythicRefineArmed = false;
        mythicRefineConfirmedItemKey = string.Empty;
        mythicRefineConfirmedCandidateCount = 0;
        mythicRefineConfirmedBatchToken = string.Empty;
        StartMythicRefine(
            applyMutation,
            expectedItemKey,
            expectedCandidateCount,
            expectedCandidateSetToken);
    }

    private void StartMythicRefine(
        bool applyMutation,
        string expectedItemKey,
        int expectedCandidateCount,
        string expectedCandidateSetToken)
    {
        var label = applyMutation
            ? UiText.L(
                "Mythic 전체 일괄 정련",
                "Refine all possible Mythic candidates",
                "批量精炼所有 Mythic 候选",
                "批次精煉所有 Mythic 候選")
            : UiText.L(
                "Mythic 후보 안전 검사",
                "Mythic candidate safety check",
                "Mythic 候选安全检查",
                "Mythic 候選安全檢查");
        var operation = new AutoBuildOperation(
            label,
            autoTransformSkills: false,
            autoTransformMaxAttempts: 0,
            planningLimitSeconds: 0,
            Plugin.NormalizeAutoBuildThemePriority(Plugin.AutoBuildThemePriority.Value),
            Plugin.AutoBuildTheme.Value,
            Plugin.AutoBuildHeroThemes.Value,
            targetHeroKey: string.Empty,
            includeForgeBonuses: true);
        mythicRefineOperation = operation;
        mythicRefinePreviewOnly = !applyMutation;
        mythicRefineStatus = applyMutation
            ? UiText.L(
                $"확인한 후보 {expectedCandidateCount}개를 순서대로 다시 검증하며 가능한 만큼 전부 Mythic 정련합니다.",
                $"Revalidating all {expectedCandidateCount} confirmed candidates and refining every one that remains possible.",
                $"正在逐一重新验证 {expectedCandidateCount} 件已确认候选，并精炼所有仍可执行的候选。",
                $"正在逐一重新驗證 {expectedCandidateCount} 件已確認候選，並精煉所有仍可執行的候選。")
            : UiText.L(
                "잠금·룬·착용 장비를 제외하고 실행 가능한 후보와 Vault 공간을 검사하는 중입니다. 아이템과 재료는 소비하지 않습니다.",
                "Checking an actionable candidate and Vault capacity while excluding locked, socketed, and equipped gear. No item or resource is consumed.",
                "正在检查可执行候选与宝库空间，并排除锁定、镶嵌及已装备物品。不会消耗物品或材料。",
                "正在檢查可執行候選與寶庫空間，並排除鎖定、鑲嵌及已裝備物品。不會消耗物品或材料。");
        status = mythicRefineStatus;
        nextRefreshAt = float.PositiveInfinity;
        mythicRefineTask = Task.Run(() =>
            RunMythicRefineWorker(
                operation, applyMutation, expectedItemKey, expectedCandidateCount,
                expectedCandidateSetToken));
    }

    private static MythicRefineResult RunMythicRefineWorker(
        AutoBuildOperation operation,
        bool applyMutation,
        string expectedItemKey,
        int expectedCandidateCount,
        string expectedCandidateSetToken)
    {
        var result = new MythicRefineResult(
            false, false, false, false, false, false, string.Empty, null);
        using var scope = MainThreadNativeGateway.AttachWorker(operation);
        try
        {
            result = applyMutation
                ? GameInventoryReader.RefineAllEligibleLegendsToMythic(
                    expectedItemKey, expectedCandidateCount,
                    expectedCandidateSetToken)
                : GameInventoryReader.PreviewOneEligibleLegendToMythic();
        }
        catch (OperationCanceledException)
        {
            result = new MythicRefineResult(
                false, true, false, false, false, false,
                UiText.L(
                    "Mythic 정련 후보 계산을 취소했습니다. 아이템과 자원은 변경하지 않았습니다.",
                    "Mythic candidate planning was cancelled. No item or resource was changed.",
                    "已取消 Mythic 候选计算。物品和资源均未更改。",
                    "已取消 Mythic 候選計算。物品與資源均未變更。"),
                null);
        }
        catch (Exception error)
        {
            result = new MythicRefineResult(
                false, false, operation.MutationStarted,
                false, false, operation.MutationStarted,
                UiText.L(
                    $"Mythic 정련 실패: {error.GetBaseException().Message}",
                    $"Mythic refinement failed: {error.GetBaseException().Message}",
                    $"Mythic 精炼失败：{error.GetBaseException().Message}",
                    $"Mythic 精煉失敗：{error.GetBaseException().Message}"),
                null);
        }
        finally
        {
            operation.MarkCompleted(result.Cancelled);
        }
        return result;
    }

    private void CompleteMythicRefineIfReady()
    {
        var task = mythicRefineTask;
        if (task is null || !task.IsCompleted) return;
        var previewOnly = mythicRefinePreviewOnly;
        MythicRefineResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            result = new MythicRefineResult(
                false, false, mythicRefineOperation?.MutationStarted == true,
                false, false, mythicRefineOperation?.MutationStarted == true,
                UiText.L(
                    $"Mythic 정련 실패: {error.GetBaseException().Message}",
                    $"Mythic refinement failed: {error.GetBaseException().Message}",
                    $"Mythic 精炼失败：{error.GetBaseException().Message}",
                    $"Mythic 精煉失敗：{error.GetBaseException().Message}"),
                null);
        }

        mythicRefineTask = null;
        mythicRefineOperation = null;
        mythicRefinePreviewOnly = false;
        if (previewOnly && result.Succeeded && result.Candidate is { } candidate)
        {
            mythicRefineArmed = true;
            mythicRefineConfirmedItemKey = candidate.ItemKey;
            mythicRefineConfirmedCandidateCount = candidate.EligibleCount;
            mythicRefineConfirmedBatchToken = candidate.CandidateSetToken;
            mythicRefineConfirmUntil = Time.unscaledTime + MythicRefineConfirmSeconds;
            mythicRefineStatus = UiText.L(
                $"일괄 실행 가능: 전체 후보 {candidate.EligibleCount}개 · 첫 순서 {candidate.ItemName} ({candidate.StorageLabel}) · 성공률 {candidate.SuccessRatePercent:0.##}%. 12초 안에 빨간 버튼을 누르면 목록 전체를 다시 확인한 뒤 가능한 후보를 모두 시도합니다. 실패한 장비는 각각 파괴됩니다.",
                $"Batch ready: {candidate.EligibleCount} candidates · first {candidate.ItemName} ({candidate.StorageLabel}) · {candidate.SuccessRatePercent:0.##}% success. Press the red button within 12 seconds to recheck the full set and attempt every possible candidate. Each failed item is destroyed.",
                $"批量执行就绪：共 {candidate.EligibleCount} 件候选 · 首件 {candidate.ItemName}（{candidate.StorageLabel}）· 成功率 {candidate.SuccessRatePercent:0.##}%。请在 12 秒内点击红色按钮，系统会重新检查完整列表并尝试所有可执行候选。每件失败物品都会被摧毁。",
                $"批次執行就緒：共 {candidate.EligibleCount} 件候選 · 首件 {candidate.ItemName}（{candidate.StorageLabel}）· 成功率 {candidate.SuccessRatePercent:0.##}%。請在 12 秒內點擊紅色按鈕，系統會重新檢查完整清單並嘗試所有可執行候選。每件失敗物品都會被摧毀。");
            Plugin.DiagInfo(
                $"MYTHIC UI PREFLIGHT|eligible={candidate.EligibleCount}|firstItem={candidate.DefinitionId}|source={candidate.Source}|copies={candidate.DuplicateCount}|rate={candidate.SuccessRatePercent:0.##}");
        }
        else
        {
            mythicRefineArmed = false;
            mythicRefineConfirmedItemKey = string.Empty;
            mythicRefineConfirmedCandidateCount = 0;
            mythicRefineConfirmedBatchToken = string.Empty;
            mythicRefineStatus = result.Message;
        }
        status = mythicRefineStatus;
        if (result.Succeeded) Plugin.DiagInfo(result.Message);
        else if (!result.Cancelled) Plugin.DiagWarning(result.Message);
        var summary = GameInventoryReader.GetSelectedHeroBuildSummary();
        selectedHeroSummary = summary.Hero;
        selectedHeroProfile = summary.Profile;
        selectedAutoBuildTheme = GameInventoryReader.GetSelectedHeroBuildTheme();
        nextRefreshAt = Time.unscaledTime + 0.5f;
    }

    private static string WorkshopProgressMeta(
        AutoBuildOperation operation,
        bool mythicRefinement)
    {
        if (!mythicRefinement) return ForgeProgressMeta(operation);
        var progress = operation.Progress;
        if (operation.MutationStarted)
        {
            var liveCounts = string.IsNullOrWhiteSpace(progress.Detail)
                ? UiText.L(
                    "실제 일괄 정련 준비 중",
                    "Preparing the real batch",
                    "正在准备实际批量精炼",
                    "正在準備實際批次精煉")
                : progress.Detail;
            return liveCounts + UiText.L(
                " · 각 결과와 아이템 위치 검증 중 · 취소 불가",
                " · verifying every result and item location · cannot cancel",
                " · 正在验证每项结果与物品位置 · 无法取消",
                " · 正在驗證每項結果與物品位置 · 無法取消");
        }
        if (progress.Total <= 0)
            return UiText.L(
                "읽기 전용 후보 확인 중 · 아직 아이템/자원 변경 없음",
                "Read-only candidate check · no item or resource changed yet",
                "只读候选检查中 · 尚未更改物品或资源",
                "唯讀候選檢查中 · 尚未變更物品或資源");
        var index = Math.Clamp(progress.Index, 0, progress.Total);
        return UiText.L(
            $"후보 확인 {index:N0}/{progress.Total:N0} · 아직 실제 정련 전",
            $"candidate check {index:N0}/{progress.Total:N0} · no real attempt yet",
            $"候选检查 {index:N0}/{progress.Total:N0} · 尚未实际精炼",
            $"候選檢查 {index:N0}/{progress.Total:N0} · 尚未實際精煉");
    }

    private static string ForgeProgressMeta(AutoBuildOperation operation)
    {
        var progress = operation.Progress;
        if (operation.MutationStarted)
            return UiText.L(
                "실제 +1 강화 적용 및 원래 슬롯 재착용을 검증하는 중입니다.",
                "Applying the real +1 Forge and verifying the original equipment slot.",
                "正在应用真实 +1 强化并验证原装备槽位。",
                "正在套用真實 +1 強化並驗證原裝備欄位。");
        if (progress.Total <= 0)
            return UiText.L(
                "읽기 전용 계산 중 · 아직 자원 사용 없음",
                "Read-only calculation · no resources spent yet",
                "只读计算中 · 尚未消耗资源",
                "唯讀計算中 · 尚未消耗資源");
        var index = Math.Clamp(progress.Index, 0, progress.Total);
        return UiText.L(
            $"후보 {index:N0}/{progress.Total:N0} · 읽기 전용 계산 · 아직 자원 사용 없음",
            $"candidate {index:N0}/{progress.Total:N0} · read-only · no resources spent yet",
            $"候选 {index:N0}/{progress.Total:N0} · 只读计算 · 尚未消耗资源",
            $"候選 {index:N0}/{progress.Total:N0} · 唯讀計算 · 尚未消耗資源");
    }

    private static string AutoBuildPhaseLabel(AutoBuildOperation operation)
    {
        var progress = operation.Progress;
        var step = progress.Step switch
        {
            AutoBuildStep.Preparing => UiText.L("계산 준비", "Preparing", "准备计算", "準備計算"),
            AutoBuildStep.WaitingForSafeState => UiText.L("안전 상태 대기", "Waiting for a safe state", "等待安全状态", "等待安全狀態"),
            AutoBuildStep.CollectingCandidates => UiText.L("후보 수집", "Collecting candidates", "收集候选装备", "收集候選裝備"),
            AutoBuildStep.SearchingLoadouts => UiText.L("장비 조합 탐색", "Searching loadouts", "搜索装备组合", "搜尋裝備組合"),
            AutoBuildStep.CoarseEvaluation => UiText.L("빠른 예비 평가", "Preliminary evaluation", "初步评估", "初步評估"),
            AutoBuildStep.StructuralScreen => UiText.L("시너지 후보 선별", "Screening synergy candidates", "筛选协同候选", "篩選協同候選"),
            AutoBuildStep.FinalRefinement => UiText.L("최종 정밀 평가", "Final refinement", "最终精算", "最終精算"),
            AutoBuildStep.ApplyingGear => UiText.L("장비 적용", "Applying gear", "应用装备", "套用裝備"),
            AutoBuildStep.ApplyingSkills => UiText.L("스킬 적용", "Applying skills", "应用技能", "套用技能"),
            AutoBuildStep.Verifying => UiText.L("최종 검증", "Final verification", "最终验证", "最終驗證"),
            AutoBuildStep.RollingBack => UiText.L("이전 상태 복구", "Restoring previous state", "恢复先前状态", "復原先前狀態"),
            AutoBuildStep.FinalizingStorage => UiText.L("보관 정리", "Finalizing storage", "整理存储", "整理儲存"),
            AutoBuildStep.Finished => UiText.L("완료", "Finished", "完成", "完成"),
            AutoBuildStep.Cancelled => UiText.L("취소 처리 중", "Cancelling", "正在取消", "正在取消"),
            AutoBuildStep.TimedOut => UiText.L("계산 시간 초과", "Planning timed out", "计算超时", "計算逾時"),
            _ => UiText.L("계산 중", "Calculating", "正在计算", "正在計算")
        };
        var detail = Regex.Replace(progress.Detail ?? string.Empty, "\\s+", " ").Trim();
        if (detail.Length > 72) detail = detail[..69] + "...";
        return string.IsNullOrWhiteSpace(detail) ? step : $"{step} · {detail}";
    }

    private static string AutoBuildProgressMetaLabel(AutoBuildOperation operation)
    {
        var progress = operation.Progress;
        var countKo = "— / — · —%";
        var countEn = "— / — · —%";
        var countZh = "— / — · —%";
        if (progress.Total > 0)
        {
            var index = Math.Clamp(progress.Index, 0, progress.Total);
            var percent = Math.Clamp(index * 100d / progress.Total, 0d, 100d);
            countKo = $"{index:N0} / {progress.Total:N0} · {percent:0}%";
            countEn = $"{index:N0} / {progress.Total:N0} · {percent:0}%";
            countZh = $"{index:N0} / {progress.Total:N0} · {percent:0}%";
        }
        countKo += operation.IncludeForgeBonuses ? " · 현재 강화 포함" : " · 0강 비교";
        countEn += operation.IncludeForgeBonuses ? " · current Forge included" : " · Forge +0 comparison";
        countZh += operation.IncludeForgeBonuses ? " · 包含当前强化" : " · 0 强比较";

        if (!operation.PlanningClockStarted)
        {
            if (!operation.HasPlanningLimit)
                return UiText.L(
                    $"{countKo} · 준비 후 무제한 최고안 탐색 시작",
                    $"{countEn} · unlimited best-candidate search starts after preparation",
                    $"{countZh} · 准备后开始无限最佳候选搜索",
                    $"{countZh} · 準備後開始無限制最佳候選搜尋");
            return UiText.L(
                $"{countKo} · 준비 후 {operation.PlanningLimitSeconds:N0}초 최고안 탐색 시작",
                $"{countEn} · the {operation.PlanningLimitSeconds:N0}s best-candidate search starts after preparation",
                $"{countZh} · 准备后开始 {operation.PlanningLimitSeconds:N0} 秒最佳候选搜索",
                $"{countZh} · 準備後開始 {operation.PlanningLimitSeconds:N0} 秒最佳候選搜尋");
        }

        var elapsed = operation.HasPlanningLimit
            ? Math.Min(operation.PlanningElapsedSeconds, operation.PlanningLimitSeconds)
            : operation.PlanningElapsedSeconds;
        if (operation.MutationStarted)
            return UiText.L(
                $"{countKo} · 계획 {elapsed:0.0}초 완료 · 적용·검증은 중단하지 않습니다",
                $"{countEn} · planning completed in {elapsed:0.0}s · apply/verify will not be interrupted",
                $"{countZh} · 规划用时 {elapsed:0.0} 秒 · 应用与验证不会中断",
                $"{countZh} · 規劃用時 {elapsed:0.0} 秒 · 套用與驗證不會中斷");
        if (operation.PlanningDeadlineReached)
            return UiText.L(
                $"{countKo} · {operation.PlanningLimitSeconds:N0}초 도달 · 현재 안전 비교를 마무리하는 중",
                $"{countEn} · {operation.PlanningLimitSeconds:N0}s reached · finishing the current safe comparison",
                $"{countZh} · 已达 {operation.PlanningLimitSeconds:N0} 秒 · 正在完成当前安全比较",
                $"{countZh} · 已達 {operation.PlanningLimitSeconds:N0} 秒 · 正在完成目前安全比較");
        if (!operation.HasPlanningLimit)
            return UiText.L(
                $"{countKo} · 계획 경과 {elapsed:0.0}초 / 무제한",
                $"{countEn} · planning elapsed {elapsed:0.0}s / unlimited",
                $"{countZh} · 规划用时 {elapsed:0.0} 秒 / 无限",
                $"{countZh} · 規劃用時 {elapsed:0.0} 秒 / 無限制");
        return UiText.L(
            $"{countKo} · 계획 경과 {elapsed:0.0}초 / {operation.PlanningLimitSeconds:N0}초",
            $"{countEn} · planning elapsed {elapsed:0.0}s / {operation.PlanningLimitSeconds:N0}s",
            $"{countZh} · 规划用时 {elapsed:0.0} 秒 / {operation.PlanningLimitSeconds:N0} 秒",
            $"{countZh} · 規劃用時 {elapsed:0.0} 秒 / {operation.PlanningLimitSeconds:N0} 秒");
    }

    [HideFromIl2Cpp]
    private void DrawAutoBuildProgressBar(Rect rect, AutoBuildOperation operation)
    {
        DrawSolidBox(rect, new Color(0.12f, 0.13f, 0.16f, 1f));
        var progress = operation.Progress;
        Rect fill;
        if (progress.Total > 0)
        {
            var ratio = Mathf.Clamp01((float)progress.Index / progress.Total);
            fill = new Rect(rect.x, rect.y, rect.width * ratio, rect.height);
        }
        else
        {
            var segment = Math.Max(S(34f), rect.width * 0.24f);
            var travel = rect.width + segment;
            var offset = Mathf.Repeat(Time.unscaledTime * S(92f), travel) - segment;
            fill = new Rect(
                Math.Max(rect.x, rect.x + offset),
                rect.y,
                Math.Min(segment, rect.xMax - Math.Max(rect.x, rect.x + offset)),
                rect.height);
        }
        if (fill.width > 0f)
        {
            var fillColor = operation.MutationStarted
                ? new Color(0.95f, 0.62f, 0.22f, 1f)
                : new Color(0.28f, 0.72f, 0.96f, 1f);
            DrawSolidBox(fill, fillColor);
        }
    }

    [HideFromIl2Cpp]
    private void DrawBulkQualityButton(Rect rect, int quality, string label)
    {
        var mask = Plugin.BulkQualityMask.Value;
        var selected = quality == 0 ? mask == 0 : QualityFilterLogic.IsSelected(mask, quality);
        var previousBackground = GUI.backgroundColor;
        if (selected) GUI.backgroundColor = quality == 6
            ? new Color(0.32f, 0.86f, 0.46f)
            : new Color(0.28f, 0.68f, 0.94f);
        var displayLabel = selected ? $"✓ {label}" : label;
        if (GUI.Button(rect, displayLabel, compactButtonStyle!))
        {
            Plugin.BulkQualityMask.Value = quality == 0
                ? 0
                : Plugin.BulkQualityAtLeast.Value
                    ? QualityFilterLogic.BitFor(quality)
                    : mask ^ QualityFilterLogic.BitFor(quality);
            armedBulkOpen = BulkToolKind.None;
            RefreshBulkCounts();
        }
        GUI.backgroundColor = previousBackground;
    }

    private static string BulkDestinationDescription(BulkEquipmentDestination destination) => destination switch
    {
        BulkEquipmentDestination.Inventory => UiText.L(
            "새 장비를 인벤토리에 남깁니다.",
            "New gear stays in the inventory.",
            "新装备将留在背包。",
            "新裝備將留在背包。"),
        BulkEquipmentDestination.Warehouse => UiText.L(
            "새 장비를 게임 규칙에 따라 창고·Vault로 보냅니다.",
            "New gear follows the game's Warehouse/Vault routing.",
            "新装备将按游戏规则送入仓库/宝库。",
            "新裝備將依遊戲規則送入倉庫/寶庫。"),
        BulkEquipmentDestination.Sell => UiText.L(
            "게임 필터가 먼저 자동판매한 보상은 건너뜁니다. 실제 Vault에 들어간 장비만 보관하고, Vault에 들어가지 않은 장비는 일반 창고에 남기지 않고 판매칸에 1개씩 넣어 즉시 판매합니다. 판매는 항상 두 번 눌러야 합니다.",
            "Drops already auto-sold by the game filter are skipped. Only gear actually placed in the Vault is kept; gear that does not enter the Vault is not left in Warehouse and is sold one item at a time. Selling always requires two clicks.",
            "游戏筛选器已自动出售的奖励会跳过。仅保留实际进入宝库的装备；未进入宝库的装备不会留在普通仓库，而会逐件立即出售。出售始终需要点击两次。",
            "遊戲篩選器已自動出售的獎勵會略過。僅保留實際進入寶庫的裝備；未進入寶庫的裝備不會留在普通倉庫，而會逐件立即出售。出售始終需要點擊兩次。"),
        _ => string.Empty
    };

    private static string BulkOpenProgressText(BulkOpenSession session) => UiText.L(
        $"진행 중 · {session.Opened:N0}/{session.Initial:N0}개 확인 · 보관 {session.Stored:N0} · 판매 {session.Sold:N0} · 실패 {session.FailedRouting.Count:N0}",
        $"Opening · {session.Opened:N0}/{session.Initial:N0} confirmed · stored {session.Stored:N0} · sold {session.Sold:N0} · failed {session.FailedRouting.Count:N0}",
        $"进行中 · 已确认 {session.Opened:N0}/{session.Initial:N0} · 入库 {session.Stored:N0} · 出售 {session.Sold:N0} · 失败 {session.FailedRouting.Count:N0}",
        $"進行中 · 已確認 {session.Opened:N0}/{session.Initial:N0} · 入庫 {session.Stored:N0} · 出售 {session.Sold:N0} · 失敗 {session.FailedRouting.Count:N0}");

    private static string WarehouseEquipmentSaleProgressText(
        WarehouseEquipmentSaleSession session) => UiText.L(
        $"Warehouse 판매 중 · {session.Sold:N0}/{session.InitialSellable:N0}개 확인 · 판매 불가 제외 {session.UnsellableSkipped:N0}",
        $"Selling Warehouse gear · {session.Sold:N0}/{session.InitialSellable:N0} verified · unsellable skipped {session.UnsellableSkipped:N0}",
        $"正在出售仓库装备 · 已确认 {session.Sold:N0}/{session.InitialSellable:N0} · 跳过不可出售 {session.UnsellableSkipped:N0}",
        $"正在出售倉庫裝備 · 已確認 {session.Sold:N0}/{session.InitialSellable:N0} · 跳過不可出售 {session.UnsellableSkipped:N0}");

    [HideFromIl2Cpp]
    private void DrawWarehouseEquipmentSaleButton(Rect rect)
    {
        var previousEnabled = GUI.enabled;
        var previousBackground = GUI.backgroundColor;
        var active = warehouseSaleSession is not null;
        var gap = S(6f);
        var cancelWidth = active ? Math.Min(S(112f), Math.Max(S(88f), rect.width * 0.24f)) : 0f;
        var actionWidth = active ? Math.Max(S(120f), rect.width - cancelWidth - gap) : rect.width;

        GUI.backgroundColor = warehouseSaleArmed
            ? new Color(1f, 0.42f, 0.16f)
            : new Color(0.88f, 0.27f, 0.20f);
        GUI.enabled = previousEnabled && !active && !IsAutoBuildRunning && bulkSession is null;
        var label = warehouseSaleArmed && Time.unscaledTime <= warehouseSaleConfirmUntil
            ? UiText.L(
                "확인: Warehouse 장비 전체 판매 (Vault 제외)",
                "Confirm: sell all Warehouse gear (Vault excluded)",
                "确认：出售全部仓库装备（不含宝库）",
                "確認：出售全部倉庫裝備（不含寶庫）")
            : active
                ? WarehouseEquipmentSaleProgressText(warehouseSaleSession!)
                : UiText.L(
                    "Warehouse 장비 전체 판매 (Vault 제외)",
                    "Sell all Warehouse gear (Vault excluded)",
                    "出售全部仓库装备（不含宝库）",
                    "出售全部倉庫裝備（不含寶庫）");
        if (GUI.Button(new Rect(rect.x, rect.y, actionWidth, rect.height), label, buttonStyle!))
            BeginOrConfirmWarehouseEquipmentSale();

        if (active)
        {
            GUI.enabled = previousEnabled && warehouseSaleSession is { CancelRequested: false };
            GUI.backgroundColor = new Color(0.48f, 0.48f, 0.52f);
            if (GUI.Button(
                    new Rect(rect.xMax - cancelWidth, rect.y, cancelWidth, rect.height),
                    UiText.L("중단", "Cancel", "取消", "取消"),
                    buttonStyle!))
            {
                warehouseSaleSession!.CancelRequested = true;
                status = UiText.L(
                    "현재 항목 처리 후 Warehouse 판매를 중단합니다.",
                    "Warehouse selling will stop before the next item.",
                    "仓库出售将在下一件物品前停止。",
                    "倉庫出售將在下一件物品前停止。");
            }
        }

        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    private void BeginOrConfirmWarehouseEquipmentSale()
    {
        if (warehouseSaleSession is not null || IsAutoBuildRunning || bulkSession is not null) return;
        if (!warehouseSaleArmed || Time.unscaledTime > warehouseSaleConfirmUntil)
        {
            warehouseSaleArmed = true;
            warehouseSaleConfirmUntil = Time.unscaledTime + 5f;
            armedBulkOpen = BulkToolKind.None;
            status = UiText.L(
                "주의: 일반 Warehouse의 판매 가능한 장비를 모두 즉시 판매합니다. Vault·인벤토리·착용 장비는 제외되며 판매는 복구할 수 없습니다. 같은 버튼을 다시 누르세요.",
                "Warning: every sellable gear item in ordinary Warehouse will be sold immediately. Vault, inventory, and equipped gear are excluded. Sales cannot be recovered. Click the same button again.",
                "警告：普通仓库中的所有可出售装备都将立即出售。宝库、背包和已装备物品不受影响；出售无法撤销。请再次点击同一按钮。",
                "警告：普通倉庫中的所有可出售裝備都將立即出售。寶庫、背包和已裝備物品不受影響；出售無法復原。請再次點擊同一按鈕。");
            return;
        }

        warehouseSaleArmed = false;
        if (!GameInventoryReader.TryBeginWarehouseEquipmentSale(
                out var session, out var message))
        {
            status = message;
            return;
        }
        warehouseSaleSession = session;
        status = message;
        nextRefreshAt = float.PositiveInfinity;
    }

    private void AdvanceWarehouseEquipmentSaleSession()
    {
        if (warehouseSaleSession is null) return;
        // Keep the native queue at one exact item. The bounds prevent large
        // Warehouses from monopolizing a frame while still selling promptly.
        const int maxSalesPerFrame = 8;
        const double maxWorkMilliseconds = 8d;
        var slice = Stopwatch.StartNew();
        for (var index = 0; index < maxSalesPerFrame && warehouseSaleSession is not null; index++)
        {
            var outcome = GameInventoryReader.AdvanceWarehouseEquipmentSale(
                warehouseSaleSession);
            status = outcome.Message;
            if (outcome.Finished)
            {
                var finalMessage = outcome.Message;
                warehouseSaleSession = null;
                warehouseSaleArmed = false;
                currentPage = 0;
                nextRefreshAt = Time.unscaledTime + 0.25f;
                RefreshBulkCounts();
                status = finalMessage;
                return;
            }
            if (slice.Elapsed.TotalMilliseconds >= maxWorkMilliseconds) return;
        }
    }

    [HideFromIl2Cpp]
    private void DrawBulkDestinationButton(Rect rect, BulkEquipmentDestination destination, string label)
    {
        var previousBackground = GUI.backgroundColor;
        if (Plugin.OpenedEquipmentDestination.Value == destination)
            GUI.backgroundColor = destination == BulkEquipmentDestination.Sell
                ? new Color(0.92f, 0.35f, 0.20f)
                : new Color(0.28f, 0.68f, 0.94f);
        var selected = Plugin.OpenedEquipmentDestination.Value == destination;
        if (GUI.Button(rect, selected ? $"✓ {label}" : label, compactButtonStyle!) && !selected)
        {
            Plugin.OpenedEquipmentDestination.Value = destination;
            Plugin.AutoStoreOpenedEquipment.Value = destination == BulkEquipmentDestination.Warehouse;
            armedBulkOpen = BulkToolKind.None;
            status = BulkDestinationDescription(destination);
        }
        GUI.backgroundColor = previousBackground;
    }

    [HideFromIl2Cpp]
    private void DrawBulkOpenButton(Rect rect, BulkToolKind kind, int count, string label)
    {
        var armed = armedBulkOpen == kind && Time.unscaledTime <= bulkConfirmUntil;
        var destructiveSell = kind == BulkToolKind.EquipmentBox
                              && Plugin.OpenedEquipmentDestination.Value == BulkEquipmentDestination.Sell;
        var previousEnabled = GUI.enabled;
        var previousBackground = GUI.backgroundColor;
        GUI.enabled = previousEnabled && !IsAutoBuildRunning && bulkSession is null && bulkCountsAvailable && count > 0;
        GUI.backgroundColor = armed ? new Color(1f, 0.48f, 0.18f) : new Color(0.34f, 0.62f, 0.92f);
        var text = !bulkCountsAvailable
            ? UiText.L("상자 데이터 확인 불가", "Box data unavailable", "箱子数据不可用", "箱子資料不可用")
            : count <= 0
            ? UiText.L($"{label} 없음", $"No {label}", $"没有{label}", $"沒有{label}")
            : armed && destructiveSell
                ? UiText.L($"확인: {count:N0}개 · Vault 보관/나머지 판매", $"Confirm: {count:N0} · keep Vault/sell rest", $"确认：{count:N0} 个 · 宝库保留/其余出售", $"確認：{count:N0} 個 · 寶庫保留/其餘出售")
            : armed
                ? UiText.L($"확인: {label} {count:N0}개 모두 열기", $"Confirm: open all {count:N0} {label}", $"确认：开启全部 {count:N0} 个{label}", $"確認：開啟全部 {count:N0} 個{label}")
                : destructiveSell
                    ? UiText.L($"Vault 보관·나머지 판매 · {count:N0}개", $"Keep Vault gear, sell rest · {count:N0}", $"宝库保留、其余出售 · {count:N0}", $"寶庫保留、其餘出售 · {count:N0}")
                : UiText.L($"{label} 모두 열기  ·  {count:N0}개", $"Open all {label}  ·  {count:N0}", $"开启全部{label}  ·  {count:N0}", $"開啟全部{label}  ·  {count:N0}");
        if (GUI.Button(rect, text, buttonStyle!)) BeginOrConfirmBulkOpen(kind, count, label);
        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    [HideFromIl2Cpp]
    private void BeginOrConfirmBulkOpen(BulkToolKind kind, int count, string label)
    {
        if (IsAutoBuildRunning || bulkSession is not null) return;
        var destructiveSell = kind == BulkToolKind.EquipmentBox
                              && Plugin.OpenedEquipmentDestination.Value == BulkEquipmentDestination.Sell;
        var requiresConfirmation = destructiveSell || !Plugin.SkipBulkConfirmation.Value;
        if (requiresConfirmation && (armedBulkOpen != kind || Time.unscaledTime > bulkConfirmUntil))
        {
            armedBulkOpen = kind;
            bulkConfirmUntil = Time.unscaledTime + 4f;
            status = destructiveSell
                ? UiText.L(
                    $"주의: {label} {count:N0}개를 열어 Vault 대상은 보관하고 나머지는 1개씩 즉시 판매합니다. 판매는 되돌릴 수 없습니다. 같은 버튼을 다시 누르세요.",
                    $"Warning: rewards from {count:N0} {label} will keep Vault-eligible gear and immediately sell the rest one item at a time. Sales cannot be recovered. Click again.",
                    $"警告：将开启 {count:N0} 个{label}，保留宝库目标装备，其余逐件立即出售。出售无法恢复。请再次点击。",
                    $"警告：將開啟 {count:N0} 個{label}，保留寶庫目標裝備，其餘逐件立即出售。出售無法復原。請再次點擊。")
                : UiText.L(
                    $"{label} {count:N0}개: 같은 버튼을 한 번 더 누르면 모두 엽니다.",
                    $"{count:N0} {label}: click the same button again to open all.",
                    $"{count:N0} 个{label}：再次点击同一按钮即可全部开启。",
                    $"{count:N0} 個{label}：再次點擊同一按鈕即可全部開啟。");
            return;
        }

        armedBulkOpen = BulkToolKind.None;
        if (!GameInventoryReader.TryBeginBulkOpen(kind, Plugin.BulkQualityMask.Value, Plugin.BulkQualityAtLeast.Value,
                Plugin.OpenedEquipmentDestination.Value, label, out var session, out var message))
        {
            status = message;
            return;
        }
        bulkSession = session;
        status = message;
        nextRefreshAt = float.PositiveInfinity;
    }

    private void AdvanceBulkOpenSession()
    {
        if (bulkSession is null) return;
        // Each step still opens exactly one box and completely routes/sells its
        // rewards before the next step. Run several cheap steps in one frame so
        // large stacks are not artificially limited to one box per rendered
        // frame, while an 8 ms / 12-step cap keeps input and rendering responsive.
        const int maxStepsPerFrame = 12;
        const double maxWorkMilliseconds = 8d;
        var slice = Stopwatch.StartNew();
        for (var stepIndex = 0; stepIndex < maxStepsPerFrame && bulkSession is not null; stepIndex++)
        {
            var step = GameInventoryReader.AdvanceBulkOpen(bulkSession);
            status = step.Message;
            if (step.Finished)
            {
                var finalMessage = step.Message;
                bulkSession = null;
                currentPage = 0;
                nextRefreshAt = Time.unscaledTime + 0.25f;
                RefreshBulkCounts();
                // Count refresh is useful, but the terminal action result must
                // remain visible even if that refresh itself fails or recovers.
                status = finalMessage;
                return;
            }
            if (slice.Elapsed.TotalMilliseconds >= maxWorkMilliseconds) return;
        }
    }

    [HideFromIl2Cpp]
    private void DrawStorageTab(Rect rect, StorageKind kind, string label, Color selectedColor)
    {
        var previousBackground = GUI.backgroundColor;
        if (selectedStorage == kind) GUI.backgroundColor = selectedColor;
        if (GUI.Button(rect, label, buttonStyle!))
        {
            selectedStorage = kind;
            currentPage = 0;
        }
        GUI.backgroundColor = previousBackground;
    }

    private void HandleSearchInput(Rect searchRect)
    {
        var current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0)
        {
            var wasFocused = focusSearch;
            focusSearch = searchRect.Contains(current.mousePosition);
            if (focusSearch)
            {
                focusSpeedInput = false;
                focusPlanningTimeInput = false;
                searchCaret = query.Length;
                EnableImeMode();
                Input.compositionCursorPos = new Vector2(searchRect.x + S(12f), searchRect.yMax + S(4f));
                current.Use();
            }
            else if (wasFocused)
            {
                RestoreImeMode();
            }
        }
        if (!focusSearch || current.type != EventType.KeyDown) return;

        var controlDown = current.control || current.command;
        var changed = false;
        if (current.keyCode == KeyCode.Backspace)
        {
            if (searchCaret > 0)
            {
                query = query.Remove(searchCaret - 1, 1);
                searchCaret--;
                changed = true;
            }
        }
        else if (current.keyCode == KeyCode.Delete)
        {
            if (searchCaret < query.Length)
            {
                query = query.Remove(searchCaret, 1);
                changed = true;
            }
        }
        else if (current.keyCode == KeyCode.LeftArrow)
        {
            searchCaret = Math.Max(0, searchCaret - 1);
        }
        else if (current.keyCode == KeyCode.RightArrow)
        {
            searchCaret = Math.Min(query.Length, searchCaret + 1);
        }
        else if (current.keyCode == KeyCode.Home)
        {
            searchCaret = 0;
        }
        else if (current.keyCode == KeyCode.End)
        {
            searchCaret = query.Length;
        }
        else if (controlDown && current.keyCode == KeyCode.V)
        {
            var clipboard = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(clipboard))
            {
                var room = Math.Max(0, 200 - query.Length);
                var insert = clipboard.Length > room ? clipboard[..room] : clipboard;
                query = query.Insert(searchCaret, insert);
                searchCaret += insert.Length;
                changed = true;
            }
        }
        else if (controlDown && current.keyCode == KeyCode.A)
        {
            query = string.Empty;
            searchCaret = 0;
            changed = true;
        }
        else if (!controlDown && current.character >= ' ' && current.character != '\u007f' && query.Length < 200)
        {
            query = query.Insert(searchCaret, current.character.ToString());
            searchCaret++;
            changed = true;
        }

        if (changed)
        {
            Plugin.SavedQuery.Value = query;
            currentPage = 0;
            ApplyFilter();
        }
        current.Use();
    }

    [HideFromIl2Cpp]
    private string BuildVisibleSearchText(float availableWidth)
    {
        searchCaret = Math.Clamp(searchCaret, 0, query.Length);
        var caret = focusSearch ? "|" : string.Empty;
        var full = ComposeVisibleSearchText(0, query.Length, caret);
        if (searchTextStyle!.CalcSize(new GUIContent(full)).x <= availableWidth) return full;

        var left = searchCaret;
        var right = searchCaret;
        var preferRight = true;
        while (left > 0 || right < query.Length)
        {
            var nextLeft = left;
            var nextRight = right;
            if ((preferRight && right < query.Length) || left == 0) nextRight++;
            else nextLeft--;
            var candidate = ComposeVisibleSearchText(nextLeft, nextRight, caret);
            if (searchTextStyle.CalcSize(new GUIContent(candidate)).x > availableWidth) break;
            left = nextLeft;
            right = nextRight;
            preferRight = !preferRight;
        }
        return ComposeVisibleSearchText(left, right, caret);
    }

    [HideFromIl2Cpp]
    private string ComposeVisibleSearchText(int start, int end, string caret)
    {
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < query.Length ? "…" : string.Empty;
        return prefix
               + query[start..searchCaret]
               + caret
               + query[searchCaret..end]
               + suffix;
    }

    [HideFromIl2Cpp]
    private string HighlightMatches(string value, StorageKind storageKind)
    {
        var escaped = EscapeRichText(value);
        var terms = SearchQuery.HighlightTerms(query);
        if (terms.Count == 0) return escaped;
        var color = storageKind == StorageKind.Inventory ? "#86EFAC" : "#67E8F9";
        var pattern = string.Join("|", terms.OrderByDescending(term => term.Length).Select(Regex.Escape));
        return Regex.Replace(escaped, pattern, match => $"<color={color}><b>{match.Value}</b></color>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string EscapeRichText(string value) => (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EllipsizeToWidth(string value, GUIStyle style, float maxWidth)
    {
        value ??= string.Empty;
        if (value.Length == 0 || maxWidth <= 0f
            || style.CalcSize(new GUIContent(value)).x <= maxWidth) return value;
        const string suffix = "…";
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var candidate = value[..middle].TrimEnd() + suffix;
            if (style.CalcSize(new GUIContent(candidate)).x <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return value[..low].TrimEnd() + suffix;
    }

    private void RefreshItems(bool resetStatus = true)
    {
        if (IsAutoBuildRunning) return;
        try
        {
            if (languageMode == "auto") UpdateUiLanguage();
            var next = GameInventoryReader.ReadAll(includeWarehouse);
            var toolCounts = GameInventoryReader.GetBulkToolCounts(Plugin.BulkQualityMask.Value, Plugin.BulkQualityAtLeast.Value);
            allItems.Clear();
            allItems.AddRange(next);
            bulkCountsAvailable = toolCounts.Success;
            equipmentBoxCount = toolCounts.EquipmentBoxes;
            runeBoxCount = toolCounts.RuneBoxes;
            ApplyFilter(resetStatus);
        }
        catch (Exception error)
        {
            allItems.Clear();
            matches.Clear();
            equipmentBoxCount = 0;
            runeBoxCount = 0;
            status = UiText.L("데이터 대기 중", "Waiting for data", "等待数据", "等待資料");
            Plugin.DiagDebug($"Inventory refresh deferred: {error.Message}");
        }
    }

    private void ApplyFilter(bool updateStatus = true)
    {
        matches.Clear();
        var parsed = SearchQuery.Parse(query);
        foreach (var item in allItems)
        {
            var qualityMatches = QualityFilterLogic.Matches(item.Quality, selectedQualityMask, false);
            if (!qualityMatches) continue;
            if (searchOptionsOnly && string.IsNullOrWhiteSpace(item.AffixSearchText)) continue;
            if (parsed.Matches(searchOptionsOnly ? item.AffixSearchText : item.SearchText)) matches.Add(item);
        }

        matches.Sort(static (left, right) =>
        {
            var quality = right.Quality.CompareTo(left.Quality);
            if (quality != 0) return quality;
            var name = string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
            return name != 0 ? name : string.Compare(left.StorageLabel, right.StorageLabel, StringComparison.CurrentCultureIgnoreCase);
        });
        if (updateStatus) UpdateStatus();
    }

    private void UpdateStatus()
    {
        var inventoryCount = 0;
        foreach (var item in matches) if (item.StorageKind == StorageKind.Inventory) inventoryCount++;
        var warehouseCount = matches.Count - inventoryCount;
        var scope = searchOptionsOnly ? UiText.L(" · 옵션", " · affixes", " · 词缀", " · 詞綴") : string.Empty;
        status = UiText.L(
            $"일치 {matches.Count}개 · I {inventoryCount} / W {warehouseCount}{scope}",
            $"Matches {matches.Count} · I {inventoryCount} / W {warehouseCount}{scope}",
            $"匹配 {matches.Count} · I {inventoryCount} / W {warehouseCount}{scope}",
            $"相符 {matches.Count} · I {inventoryCount} / W {warehouseCount}{scope}");
    }

    private void EnsureStyles()
    {
        if (panelStyle is not null)
        {
            ApplyStyleScale();
            return;
        }
        try
        {
            var fontName = UiText.LanguageCode switch
            {
                "ko" => "Malgun Gothic",
                "zh-cn" => "Microsoft YaHei UI",
                "zh-tw" => "Microsoft JhengHei UI",
                _ => "Segoe UI Semibold"
            };
            uiFont = Font.CreateDynamicFontFromOSFont(fontName, 15);
        }
        catch (Exception error)
        {
            uiFont = null;
            Plugin.DiagDebug($"Preferred UI font unavailable: {error.Message}");
        }
        panelStyle = CopyStyle(GUI.skin.box);
        panelStyle.font = uiFont ?? panelStyle.font;
        panelStyle.padding = new RectOffset(14, 14, 12, 12);
        panelStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
        solidFillStyle = CopyStyle(GUI.skin.box);
        solidFillStyle.border = new RectOffset(0, 0, 0, 0);
        solidFillStyle.padding = new RectOffset(0, 0, 0, 0);
        solidFillStyle.normal.background = Texture2D.whiteTexture;
        solidFillStyle.hover.background = Texture2D.whiteTexture;
        solidFillStyle.active.background = Texture2D.whiteTexture;
        solidFillStyle.focused.background = Texture2D.whiteTexture;
        solidFillStyle.onNormal.background = Texture2D.whiteTexture;
        solidFillStyle.onHover.background = Texture2D.whiteTexture;
        solidFillStyle.onActive.background = Texture2D.whiteTexture;
        solidFillStyle.onFocused.background = Texture2D.whiteTexture;
        titleStyle = CopyStyle(GUI.skin.label);
        titleStyle.font = uiFont ?? titleStyle.font;
        titleStyle.fontSize = 20;
        titleStyle.fontStyle = FontStyle.Bold;
        titleStyle.normal.textColor = new Color(1f, 0.78f, 0.32f);
        hintStyle = CopyStyle(GUI.skin.label);
        hintStyle.font = uiFont ?? hintStyle.font;
        hintStyle.fontSize = 12;
        hintStyle.fontStyle = FontStyle.Bold;
        hintStyle.wordWrap = true;
        hintStyle.normal.textColor = new Color(0.68f, 0.68f, 0.72f);
        searchStyle = CopyStyle(GUI.skin.textField);
        searchStyle.font = uiFont ?? searchStyle.font;
        searchStyle.fontSize = 17;
        searchStyle.fontStyle = FontStyle.Bold;
        searchStyle.clipping = TextClipping.Clip;
        searchStyle.padding = new RectOffset(12, 12, 8, 7);
        searchStyle.normal.textColor = Color.white;
        searchTextStyle = CopyStyle(GUI.skin.label);
        searchTextStyle.font = uiFont ?? searchTextStyle.font;
        searchTextStyle.fontSize = 17;
        searchTextStyle.fontStyle = FontStyle.Bold;
        searchTextStyle.clipping = TextClipping.Clip;
        resultNameStyle = CopyStyle(GUI.skin.label);
        resultNameStyle.font = uiFont ?? resultNameStyle.font;
        resultNameStyle.fontSize = 15;
        resultNameStyle.fontStyle = FontStyle.Bold;
        resultNameStyle.wordWrap = false;
        resultNameStyle.clipping = TextClipping.Clip;
        resultNameStyle.richText = true;
        resultNameStyle.normal.textColor = Color.white;
        resultMetaStyle = CopyStyle(GUI.skin.label);
        resultMetaStyle.font = uiFont ?? resultMetaStyle.font;
        resultMetaStyle.fontSize = 12;
        resultMetaStyle.fontStyle = FontStyle.Bold;
        resultMetaStyle.richText = true;
        resultMetaStyle.normal.textColor = new Color(0.42f, 0.82f, 0.94f);
        resultAffixStyle = CopyStyle(GUI.skin.label);
        resultAffixStyle.font = uiFont ?? resultAffixStyle.font;
        resultAffixStyle.fontSize = 12;
        resultAffixStyle.fontStyle = FontStyle.Bold;
        resultAffixStyle.wordWrap = false;
        resultAffixStyle.clipping = TextClipping.Clip;
        resultAffixStyle.richText = true;
        resultAffixStyle.normal.textColor = new Color(0.82f, 0.82f, 0.84f);
        badgeStyle = CopyStyle(GUI.skin.box);
        badgeStyle.font = uiFont ?? badgeStyle.font;
        badgeStyle.alignment = TextAnchor.MiddleCenter;
        badgeStyle.fontSize = 11;
        badgeStyle.fontStyle = FontStyle.Bold;
        badgeStyle.normal.textColor = new Color(1f, 0.82f, 0.38f);
        closeStyle = CopyStyle(GUI.skin.button);
        closeStyle.font = uiFont ?? closeStyle.font;
        closeStyle.fontSize = 20;
        closeStyle.fontStyle = FontStyle.Bold;
        closeStyle.alignment = TextAnchor.MiddleCenter;
        closeStyle.normal.textColor = Color.white;
        tooltipTitleStyle = CopyStyle(GUI.skin.label);
        tooltipTitleStyle.font = uiFont ?? tooltipTitleStyle.font;
        tooltipTitleStyle.fontSize = 17;
        tooltipTitleStyle.fontStyle = FontStyle.Bold;
        tooltipTitleStyle.wordWrap = true;
        tooltipTitleStyle.normal.textColor = new Color(1f, 0.82f, 0.38f);
        tooltipBodyStyle = CopyStyle(GUI.skin.label);
        tooltipBodyStyle.font = uiFont ?? tooltipBodyStyle.font;
        tooltipBodyStyle.fontSize = 13;
        tooltipBodyStyle.fontStyle = FontStyle.Bold;
        tooltipBodyStyle.wordWrap = true;
        tooltipBodyStyle.richText = true;
        tooltipBodyStyle.normal.textColor = new Color(0.92f, 0.92f, 0.94f);
        utilityTitleStyle = CopyStyle(GUI.skin.label);
        utilityTitleStyle.font = uiFont ?? utilityTitleStyle.font;
        utilityTitleStyle.fontSize = 14;
        utilityTitleStyle.fontStyle = FontStyle.Bold;
        utilityTitleStyle.normal.textColor = new Color(0.98f, 0.78f, 0.34f);
        pageStyle = CopyStyle(GUI.skin.label);
        pageStyle.font = uiFont ?? pageStyle.font;
        pageStyle.fontSize = 12;
        pageStyle.fontStyle = FontStyle.Bold;
        pageStyle.alignment = TextAnchor.MiddleCenter;
        pageStyle.normal.textColor = new Color(0.78f, 0.80f, 0.84f);
        buttonStyle = CopyStyle(GUI.skin.button);
        buttonStyle.font = uiFont ?? buttonStyle.font;
        buttonStyle.fontSize = 12;
        buttonStyle.fontStyle = FontStyle.Bold;
        buttonStyle.alignment = TextAnchor.MiddleCenter;
        buttonStyle.wordWrap = true;
        compactButtonStyle = CopyStyle(buttonStyle);
        compactButtonStyle.fontSize = 11;
        compactButtonStyle.wordWrap = true;
        toggleStyle = CopyStyle(GUI.skin.toggle);
        toggleStyle.font = uiFont ?? toggleStyle.font;
        toggleStyle.fontSize = 11;
        toggleStyle.fontStyle = FontStyle.Bold;
        toggleStyle.wordWrap = true;
        appliedStyleScale = -1f;
        ApplyStyleScale();
    }

    private void ApplyStyleScale()
    {
        if (panelStyle is null || Math.Abs(appliedStyleScale - uiScale) < 0.001f) return;
        appliedStyleScale = uiScale;
        static int FontSize(float scale, int baseline, int minimum, int maximum) =>
            Math.Clamp(Mathf.RoundToInt(baseline * scale), minimum, maximum);
        int Pad(float baseline) => Math.Max(1, Mathf.RoundToInt(baseline * uiScale));

        panelStyle.padding = new RectOffset(Pad(14f), Pad(14f), Pad(12f), Pad(12f));
        searchStyle!.padding = new RectOffset(Pad(12f), Pad(12f), Pad(8f), Pad(7f));
        buttonStyle!.padding = new RectOffset(Pad(7f), Pad(7f), Pad(4f), Pad(4f));
        compactButtonStyle!.padding = new RectOffset(Pad(5f), Pad(5f), Pad(3f), Pad(3f));
        toggleStyle!.padding = new RectOffset(Pad(14f), Pad(3f), Pad(2f), Pad(2f));
        titleStyle!.fontSize = FontSize(uiScale, 20, 16, 26);
        hintStyle!.fontSize = FontSize(uiScale, 12, 10, 16);
        searchTextStyle!.fontSize = FontSize(uiScale, 17, 13, 22);
        searchStyle.fontSize = FontSize(uiScale, 17, 13, 22);
        resultNameStyle!.fontSize = FontSize(uiScale, 15, 12, 20);
        resultMetaStyle!.fontSize = FontSize(uiScale, 12, 10, 16);
        resultAffixStyle!.fontSize = FontSize(uiScale, 12, 10, 16);
        badgeStyle!.fontSize = FontSize(uiScale, 11, 9, 15);
        closeStyle!.fontSize = FontSize(uiScale, 20, 16, 26);
        tooltipTitleStyle!.fontSize = FontSize(uiScale, 17, 14, 23);
        tooltipBodyStyle!.fontSize = FontSize(uiScale, 13, 10, 18);
        utilityTitleStyle!.fontSize = FontSize(uiScale, 14, 11, 19);
        pageStyle!.fontSize = FontSize(uiScale, 12, 10, 16);
        buttonStyle!.fontSize = FontSize(uiScale, 12, 10, 16);
        compactButtonStyle!.fontSize = FontSize(uiScale, 11, 9, 15);
        toggleStyle!.fontSize = FontSize(uiScale, 11, 9, 15);
    }

    private static GUIStyle CopyStyle(GUIStyle source)
    {
        var target = new GUIStyle
        {
            alignment = source.alignment,
            clipping = source.clipping,
            contentOffset = source.contentOffset,
            fixedHeight = source.fixedHeight,
            fixedWidth = source.fixedWidth,
            font = source.font,
            fontSize = source.fontSize,
            fontStyle = source.fontStyle,
            imagePosition = source.imagePosition,
            richText = source.richText,
            stretchHeight = source.stretchHeight,
            stretchWidth = source.stretchWidth,
            wordWrap = source.wordWrap,
            border = new RectOffset(source.border.left, source.border.right, source.border.top, source.border.bottom),
            margin = new RectOffset(source.margin.left, source.margin.right, source.margin.top, source.margin.bottom),
            overflow = new RectOffset(source.overflow.left, source.overflow.right, source.overflow.top, source.overflow.bottom),
            padding = new RectOffset(source.padding.left, source.padding.right, source.padding.top, source.padding.bottom)
        };
        CopyStyleState(source.normal, target.normal);
        CopyStyleState(source.hover, target.hover);
        CopyStyleState(source.active, target.active);
        CopyStyleState(source.focused, target.focused);
        CopyStyleState(source.onNormal, target.onNormal);
        CopyStyleState(source.onHover, target.onHover);
        CopyStyleState(source.onActive, target.onActive);
        CopyStyleState(source.onFocused, target.onFocused);
        return target;
    }

    private static void CopyStyleState(GUIStyleState source, GUIStyleState target)
    {
        target.background = source.background;
        target.textColor = source.textColor;
    }
}
