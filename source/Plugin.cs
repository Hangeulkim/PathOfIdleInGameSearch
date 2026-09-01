#if PATHOFIDLE_DIAGNOSTICS
#define POI_DEV_FEATURE
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "local.pathofidle.ingame-search";
    public const string PluginName = "Path of Idle In-Game Search";
    public const string PluginVersion = "1.1.5";

#if PATHOFIDLE_DIAGNOSTICS
    private static ManualLogSource DiagnosticsLogger { get; set; } = null!;
#endif
    internal static ConfigEntry<string> SavedQuery { get; private set; } = null!;
    internal static ConfigEntry<bool> IncludeWarehouse { get; private set; } = null!;
    internal static ConfigEntry<float> WindowX { get; private set; } = null!;
    internal static ConfigEntry<float> WindowY { get; private set; } = null!;
    internal static ConfigEntry<float> WindowWidth { get; private set; } = null!;
    internal static ConfigEntry<float> WindowHeight { get; private set; } = null!;
    internal static ConfigEntry<bool> OpenOnStart { get; private set; } = null!;
    internal static ConfigEntry<float> GameSpeed { get; private set; } = null!;
    internal static ConfigEntry<bool> SkipBulkConfirmation { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoStoreOpenedEquipment { get; private set; } = null!;
    internal static ConfigEntry<int> BulkQualityMask { get; private set; } = null!;
    internal static ConfigEntry<bool> BulkQualityAtLeast { get; private set; } = null!;
    internal static ConfigEntry<int> QualityFilter { get; private set; } = null!;
    internal static ConfigEntry<int> QualityMask { get; private set; } = null!;
    internal static ConfigEntry<bool> SearchOptionsOnly { get; private set; } = null!;
    internal static ConfigEntry<string> UiLanguage { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoBuildIncludeStorage { get; private set; } = null!;
    internal static ConfigEntry<string> AutoBuildTheme { get; private set; } = null!;
    internal static ConfigEntry<string> AutoBuildHeroThemes { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoTransformSkills { get; private set; } = null!;
    internal static ConfigEntry<int> AutoTransformMaxAttempts { get; private set; } = null!;

    public override void Load()
    {
#if PATHOFIDLE_DIAGNOSTICS
        DiagnosticsLogger = Log;
#endif
        SavedQuery = Config.Bind("Search", "LastQuery", string.Empty, "Last in-game item search query.");
        IncludeWarehouse = Config.Bind("Search", "IncludeWarehouse", true, "Include warehouse and vault items.");
        WindowX = Config.Bind("Window", "X", 48f, "Search panel X position.");
        WindowY = Config.Bind("Window", "Y", 72f, "Search panel Y position.");
        WindowWidth = Config.Bind("Window", "Width", 720f, "Search panel width. Drag the lower-right grip to resize it.");
        WindowHeight = Config.Bind("Window", "Height", 780f, "Search panel height. Drag the lower-right grip to resize it.");
        OpenOnStart = Config.Bind("Window", "OpenOnStart", false, "Open the search panel when the game starts.");
        GameSpeed = Config.Bind("Speed", "Multiplier", 1f, "Runtime speed: 0.1 through 100.");
        SkipBulkConfirmation = Config.Bind("BulkOpen", "SkipConfirmation", false, "Open all boxes immediately without a second confirmation click.");
        AutoStoreOpenedEquipment = Config.Bind("BulkOpen", "AutoStoreEquipment", true, "Move equipment received from bulk-opened boxes through the game's automatic warehouse routing.");
        BulkQualityMask = Config.Bind("BulkOpen", "QualityMask", 0, "Bit mask of box and rune-box qualities. Zero means all.");
        BulkQualityAtLeast = Config.Bind("BulkOpen", "QualityAtLeast", false, "Open the selected quality and every higher quality.");
        QualityFilter = Config.Bind("Search", "QualityFilter", 0, "Equipment quality filter: 0 all, -1 other, or a game quality id.");
        QualityMask = Config.Bind("Search", "QualityMask", 0, "Bit mask of equipment qualities. Zero means all.");
        SearchOptionsOnly = Config.Bind("Search", "OptionsOnly", false, "Search affixes and set bonuses only.");
        UiLanguage = Config.Bind("Window", "Language", "auto", "UI language: auto, ko, en, zh-cn, or zh-tw.");
        AutoBuildIncludeStorage = Config.Bind("AutoBuild", "IncludeStorage", true, "Allow automatic gear selection to use warehouse and Vault equipment.");
        AutoBuildTheme = Config.Bind("AutoBuild", "Theme", "auto", "Build theme used by automatic gear and skill optimization.");
        AutoBuildHeroThemes = Config.Bind("AutoBuild", "HeroThemes", string.Empty, "Per-hero build themes keyed by the save hero unique ID.");
        AutoTransformSkills = Config.Bind("AutoBuild", "TransformMissingSkills", true, "Use the shrine's normal paid skill transformation to seek missing performance-plan skills before allocating points.");
        AutoTransformMaxAttempts = Config.Bind("AutoBuild", "MaxSkillTransformAttempts", 12, "Maximum paid skill transformations per automatic skill run.");
        InstallWheelPatch();
        AddComponent<InGameSearchOverlay>();
#if PATHOFIDLE_RUNTIME_TEST
        AddComponent<RuntimeJointBuildTestHook>();
#endif
        DiagInfo($"{PluginName} {PluginVersion} loaded. Press F3 or Ctrl+F to open it.");
    }

    // Diagnostic calls are removed completely from public builds, including
    // argument evaluation and interpolated-string construction. Developers can
    // opt in locally by defining PATHOFIDLE_DIAGNOSTICS at compile time.
    [Conditional("POI_DEV_FEATURE")]
    internal static void DiagInfo(string message)
    {
#if PATHOFIDLE_DIAGNOSTICS
        DiagnosticsLogger.LogInfo(message);
#endif
    }

    [Conditional("POI_DEV_FEATURE")]
    internal static void DiagWarning(string message)
    {
#if PATHOFIDLE_DIAGNOSTICS
        DiagnosticsLogger.LogWarning(message);
#endif
    }

    [Conditional("POI_DEV_FEATURE")]
    internal static void DiagDebug(string message)
    {
#if PATHOFIDLE_DIAGNOSTICS
        DiagnosticsLogger.LogDebug(message);
#endif
    }

    private static void InstallWheelPatch()
    {
        try
        {
            var gameAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
            var target = gameAssembly?.GetType("ScrollRectCustomWheel")?.GetMethod("ReadNormalizedWheelDelta", BindingFlags.Public | BindingFlags.Static);
            var prefix = typeof(WheelInputPatch).GetMethod(nameof(WheelInputPatch.Prefix), BindingFlags.Public | BindingFlags.Static);
            if (target is null || prefix is null) throw new MissingMethodException("ScrollRectCustomWheel.ReadNormalizedWheelDelta");
            var harmony = new Harmony(PluginGuid);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            var heroWheelUpdate = gameAssembly?.GetType("LordMod")?.GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var heroWheelPrefix = typeof(WheelInputPatch).GetMethod(nameof(WheelInputPatch.HeroSelectorPrefix), BindingFlags.Public | BindingFlags.Static);
            if (heroWheelUpdate is null || heroWheelPrefix is null) throw new MissingMethodException("LordMod.Update");
            harmony.Patch(heroWheelUpdate, prefix: new HarmonyMethod(heroWheelPrefix));

            var axisPrefix = typeof(WheelInputPatch).GetMethod(nameof(WheelInputPatch.AxisPrefix), BindingFlags.Public | BindingFlags.Static);
            var getAxis = typeof(Input).GetMethod(nameof(Input.GetAxis), BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            var getAxisRaw = typeof(Input).GetMethod(nameof(Input.GetAxisRaw), BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (axisPrefix is null || getAxis is null || getAxisRaw is null) throw new MissingMethodException("UnityEngine.Input.GetAxis");
            harmony.Patch(getAxis, prefix: new HarmonyMethod(axisPrefix));
            harmony.Patch(getAxisRaw, prefix: new HarmonyMethod(axisPrefix));

            var scrollGetter = typeof(Input).GetProperty(nameof(Input.mouseScrollDelta), BindingFlags.Public | BindingFlags.Static)?.GetMethod;
            var scrollPrefix = typeof(WheelInputPatch).GetMethod(nameof(WheelInputPatch.MouseScrollDeltaPrefix), BindingFlags.Public | BindingFlags.Static);
            if (scrollGetter is not null && scrollPrefix is not null)
                harmony.Patch(scrollGetter, prefix: new HarmonyMethod(scrollPrefix));

            DiagInfo("Focused overlay mouse-wheel input guard installed.");
        }
        catch (Exception error)
        {
            DiagWarning($"Mouse-wheel input guard unavailable: {error.Message}");
        }
    }
}

#if PATHOFIDLE_RUNTIME_TEST
// Opt-in runtime test harness. This type and all of its file-system strings are
// absent from normal Release builds. It does not bind or modify any setting and
// emits exactly one small, overwrite-only result file instead of enabling logs.
public sealed class RuntimeJointBuildTestHook : MonoBehaviour
{
    private const string RequestContents = "APPLY JOINT BUILD v1.1.5";
    private const string ForceRollbackRequestContents = "APPLY JOINT BUILD FORCE ROLLBACK v1.1.5";
    private static readonly string RequestPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.5-runtime-test.request");
    private static readonly string ResultPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.5-runtime-test.result");

    private bool armed;
    private bool forceRollbackRequested;
    private bool continueInvocationAttempted;
    private float armedAt;
    private float nextProbeAt;
    private string lastReadiness = "request not armed";

    public RuntimeJointBuildTestHook(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (Time.unscaledTime < nextProbeAt) return;
        nextProbeAt = Time.unscaledTime + 0.5f;

        if (!armed)
        {
            string request;
            try
            {
                if (!System.IO.File.Exists(RequestPath)) return;
                request = System.IO.File.ReadAllText(RequestPath, Encoding.UTF8).Trim();
            }
            catch
            {
                return;
            }
            var ordinaryRequest = string.Equals(request, RequestContents, StringComparison.Ordinal);
            var rollbackRequest = string.Equals(request, ForceRollbackRequestContents, StringComparison.Ordinal);
            if (!ordinaryRequest && !rollbackRequest) return;

            armed = true;
            forceRollbackRequested = rollbackRequest;
            armedAt = Time.unscaledTime;
            continueInvocationAttempted = false;
            try { System.IO.File.Delete(RequestPath); } catch { }
            try { System.IO.File.Delete(ResultPath); } catch { }
        }

        if (!GameInventoryReader.TryGetRuntimeJointBuildTestReadiness(out lastReadiness))
        {
            if (!continueInvocationAttempted)
            {
                GameInventoryReader.TryInvokeRuntimeTestContinue(
                    out var invocationAttempted, out var continueReason);
                if (invocationAttempted) continueInvocationAttempted = true;
                if (!string.IsNullOrWhiteSpace(continueReason))
                    lastReadiness = $"{lastReadiness}; continue helper: {continueReason}";
            }
            if (Time.unscaledTime - armedAt < 12f) return;
            WriteResult(false, 0, $"readiness timeout: {lastReadiness}");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        bool success;
        string message;
        try
        {
            GameInventoryReader.RuntimeForceJointRollback = forceRollbackRequested;
            success = GameInventoryReader.TryOptimizeSelectedHeroBuild(
                Plugin.AutoBuildIncludeStorage.Value, out message);
        }
        catch (Exception error)
        {
            success = false;
            message = $"unhandled exception: {error.GetBaseException().Message}";
        }
        finally
        {
            GameInventoryReader.RuntimeForceJointRollback = false;
            forceRollbackRequested = false;
        }
        stopwatch.Stop();
        WriteResult(success, stopwatch.ElapsedMilliseconds, message);
    }

    private void WriteResult(bool success, long elapsedMilliseconds, string message)
    {
        armed = false;
        continueInvocationAttempted = false;
        var sanitized = (message ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        try
        {
            System.IO.File.WriteAllText(
                ResultPath,
                $"success={success.ToString().ToLowerInvariant()}\nelapsed_ms={elapsedMilliseconds}\nmessage={sanitized}\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // Broad logging is intentionally disabled for this bounded test hook.
        }
    }
}
#endif

internal static class WheelInputPatch
{
    private static bool blockReported;

    public static bool Prefix(ref float __result)
    {
        if (!InGameSearchOverlay.ShouldBlockWheel) return true;
        __result = 0f;
        return false;
    }

    public static bool HeroSelectorPrefix()
    {
        if (!InGameSearchOverlay.ShouldBlockWheel) return true;
        if (!blockReported)
        {
            blockReported = true;
            Plugin.DiagInfo("Character wheel selection blocked while the pointer is over the focused overlay.");
        }
        return false;
    }

    public static bool AxisPrefix(string axisName, ref float __result)
    {
        if (!InGameSearchOverlay.ShouldBlockWheel || !axisName.Contains("Scroll", StringComparison.OrdinalIgnoreCase)) return true;
        __result = 0f;
        return false;
    }

    public static bool MouseScrollDeltaPrefix(ref Vector2 __result)
    {
        if (!InGameSearchOverlay.ShouldBlockWheel) return true;
        __result = Vector2.zero;
        return false;
    }
}

internal static class GameInputGuard
{
    private static Assembly? gameAssembly;
    private static Type? eventSystemType;

    public static bool TrySetKeyboardBlocked(bool blocked, out bool previous)
    {
        previous = false;
        try
        {
            gameAssembly ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
            var gameType = gameAssembly?.GetType("Game", false, false);
            var keyManager = gameType?.GetProperty("keyMgr", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var property = keyManager?.GetType().GetProperty("isBlockKeys", BindingFlags.Instance | BindingFlags.Public);
            if (keyManager is null || property?.CanRead != true || property.CanWrite != true) return false;
            previous = Convert.ToBoolean(property.GetValue(keyManager), CultureInfo.InvariantCulture);
            if (previous != blocked) property.SetValue(keyManager, blocked);
            return Convert.ToBoolean(property.GetValue(keyManager), CultureInfo.InvariantCulture) == blocked;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetUiNavigationBlocked(bool blocked, out bool previousEnabled)
    {
        previousEnabled = true;
        try
        {
            eventSystemType ??= AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEngine.EventSystems.EventSystem", false, false))
                .FirstOrDefault(type => type is not null);
            var current = eventSystemType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var property = current?.GetType().GetProperty("sendNavigationEvents", BindingFlags.Instance | BindingFlags.Public);
            if (current is null || property?.CanRead != true || property.CanWrite != true) return false;
            previousEnabled = Convert.ToBoolean(property.GetValue(current), CultureInfo.InvariantCulture);
            var desired = !blocked;
            if (previousEnabled != desired) property.SetValue(current, desired);
            return Convert.ToBoolean(property.GetValue(current), CultureInfo.InvariantCulture) == desired;
        }
        catch
        {
            return false;
        }
    }
}

internal static class UiText
{
    public static string LanguageCode { get; private set; } = "en";
    public static bool IsKorean => LanguageCode == "ko";

    public static void SetLanguage(string code) => LanguageCode = code is "ko" or "en" or "zh-cn" or "zh-tw" ? code : "en";

    public static string L(string ko, string en, string? zhCn = null, string? zhTw = null) => LanguageCode switch
    {
        "ko" => ko,
        "zh-cn" => zhCn ?? en,
        "zh-tw" => zhTw ?? zhCn ?? en,
        _ => en
    };
}

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
    private float searchTextScrollX;
    private float currentSpeed = 1f;
    private bool visible;
    private bool focusSearch;
    private bool focusSpeedInput;
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
    private string status = string.Empty;
    private float nextRefreshAt;
    private float transferCooldownUntil;
    private BulkToolKind armedBulkOpen;
    private float bulkConfirmUntil;
    private BulkOpenSession? bulkSession;
    private AutoBuildAction armedAutoBuild;
    private float autoBuildConfirmUntil;
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
    private Vector2 tooltipScroll;
    private string tooltipItemKey = string.Empty;
    private string selectedHeroSummary = string.Empty;
    private string selectedHeroProfile = string.Empty;

    private GUIStyle? panelStyle;
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

    public InGameSearchOverlay(IntPtr pointer) : base(pointer) { }

    public void Start()
    {
        activeOverlay = this;
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
        if (!float.IsFinite(configuredWidth)) configuredWidth = DefaultWindowWidth;
        if (!float.IsFinite(configuredHeight)) configuredHeight = DefaultWindowHeight;
        windowRect = new Rect(Plugin.WindowX.Value, Plugin.WindowY.Value, configuredWidth, configuredHeight);
        var configuredSpeed = Plugin.GameSpeed.Value;
        currentSpeed = float.IsFinite(configuredSpeed) ? Mathf.Clamp(configuredSpeed, 0.1f, 100f) : 1f;
        speedInput = currentSpeed.ToString("0.##", CultureInfo.InvariantCulture);
        ApplyGameSpeed();
        ClampWindowToScreen();
        UpdateUiScale();
        Plugin.DiagInfo("In-game search overlay started.");
        if (Plugin.OpenOnStart.Value) SetVisible(true);
    }

    public void Update()
    {
        if (Math.Abs(Time.timeScale - currentSpeed) > 0.001f) Time.timeScale = currentSpeed;
        var controlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (Input.GetKeyDown(KeyCode.F3) || (controlDown && Input.GetKeyDown(KeyCode.F)))
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
        if (overlayInputFocused) MaintainGameKeyboardBlock();
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
        if (bulkSession is not null) AdvanceBulkOpenSession();

        if (Time.unscaledTime >= nextRefreshAt)
        {
            // Full inventory/vault discovery uses game reflection and can be fairly
            // expensive on large saves. Transfers and user actions already refresh
            // immediately, so a slower idle poll avoids needless frame-time spikes.
            nextRefreshAt = Time.unscaledTime + 3f;
            RefreshCurrentPage(resetStatus: false);
        }
    }

    public void OnGUI()
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
        var windowBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.035f, 0.035f, 0.045f, 1f);
        for (var layer = 0; layer < 9; layer++) GUI.Box(windowRect, GUIContent.none, panelStyle!);
        GUI.backgroundColor = windowBackground;
        GUI.Box(windowRect, GUIContent.none, panelStyle!);
        var left = windowRect.x + S(18f);
        var width = windowRect.width - S(36f);
        var pageTitle = selectedPage switch
        {
            OverlayPage.BulkOpen => UiText.L("Path of Idle · 일괄 개봉", "Path of Idle · Bulk Open", "Path of Idle · 批量开启", "Path of Idle · 批次開啟"),
            OverlayPage.AutoBuild => UiText.L("Path of Idle · 자동 빌드", "Path of Idle · Auto Build", "Path of Idle · 自动配装", "Path of Idle · 自動配裝"),
            _ => UiText.L("Path of Idle · 아이템 검색", "Path of Idle · Item Search", "Path of Idle · 物品搜索", "Path of Idle · 物品搜尋")
        };
        GUI.Label(new Rect(left, windowRect.y + S(12f), Math.Max(S(80f), width - S(382f)), S(30f)), pageTitle, titleStyle!);
        if (GUI.Button(new Rect(windowRect.xMax - S(368f), windowRect.y + S(11f), S(62f), S(29f)), LanguageButtonLabel(), compactButtonStyle!)) CycleUiLanguage();
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

        var searchRect = new Rect(left, windowRect.y + S(70f), width, S(38f));
        if (!tooltipOwnsPointer) HandleSearchInput(searchRect);
        GUI.Box(searchRect, GUIContent.none, searchStyle!);
        var composition = Input.compositionString ?? string.Empty;
        searchCaret = Math.Max(0, Math.Min(searchCaret, query.Length));
        var searchDisplay = string.IsNullOrEmpty(query) && string.IsNullOrEmpty(composition)
            ? UiText.L("아이템 이름, 등급, 부위, 옵션 검색…", "Search name, quality, slot, or affix…", "搜索名称、品质、部位或词缀…", "搜尋名稱、品質、部位或詞綴…")
            : query[..searchCaret] + (focusSearch ? "|" : string.Empty) + composition + query[searchCaret..];
        searchTextStyle!.normal.textColor = string.IsNullOrEmpty(query) && string.IsNullOrEmpty(composition)
            ? new Color(0.52f, 0.52f, 0.56f)
            : Color.white;
        var searchContentRect = new Rect(searchRect.x + S(10f), searchRect.y + S(7f), searchRect.width - S(20f), S(24f));
        if (focusSearch && (!string.IsNullOrEmpty(query) || !string.IsNullOrEmpty(composition)))
        {
            var caretText = query[..searchCaret] + composition + "|";
            var caretWidth = searchTextStyle.CalcSize(new GUIContent(caretText)).x;
            var edgePadding = S(8f);
            if (caretWidth - searchTextScrollX > searchContentRect.width - edgePadding)
                searchTextScrollX = Math.Max(0f, caretWidth - searchContentRect.width + edgePadding);
            else if (caretWidth - searchTextScrollX < edgePadding)
                searchTextScrollX = Math.Max(0f, caretWidth - edgePadding);
            Input.compositionCursorPos = new Vector2(searchContentRect.x + Mathf.Clamp(caretWidth - searchTextScrollX, 0f, searchContentRect.width), searchContentRect.yMax + S(4f));
        }
        else
        {
            searchTextScrollX = 0f;
        }
        GUI.BeginGroup(searchContentRect);
        var searchDisplayWidth = Math.Max(searchContentRect.width + searchTextScrollX, searchTextStyle.CalcSize(new GUIContent(searchDisplay)).x + S(8f));
        GUI.Label(new Rect(-searchTextScrollX, 0f, searchDisplayWidth, searchContentRect.height), searchDisplay, searchTextStyle!);
        GUI.EndGroup();

        var nextWarehouse = GUI.Toggle(new Rect(left, windowRect.y + S(116f), S(158f), S(26f)), includeWarehouse, UiText.L(" 창고·보관함 포함", " Include warehouse/vault", " 包含仓库/宝库", " 包含倉庫/寶庫"), toggleStyle!);
        if (nextWarehouse != includeWarehouse)
        {
            includeWarehouse = nextWarehouse;
            Plugin.IncludeWarehouse.Value = includeWarehouse;
            if (!includeWarehouse) selectedStorage = StorageKind.Inventory;
            currentPage = 0;
            RefreshItems();
        }
        if (GUI.Button(new Rect(left + S(164f), windowRect.y + S(114f), S(88f), S(28f)), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshItems();
        if (GUI.Button(new Rect(left + S(258f), windowRect.y + S(114f), S(105f), S(28f)), UiText.L("검색어 지우기", "Clear search", "清除搜索", "清除搜尋"), compactButtonStyle!))
        {
            query = string.Empty;
            searchCaret = 0;
            Plugin.SavedQuery.Value = query;
            focusSearch = selectedPage == OverlayPage.Search;
            currentPage = 0;
            ApplyFilter();
        }
        GUI.Label(new Rect(left + S(372f), windowRect.y + S(114f), Math.Max(S(80f), width - S(372f)), S(34f)), status, hintStyle!);

        DrawQualityFilters(new Rect(left, windowRect.y + S(150f), width, S(100f)));
        var inventoryCount = 0;
        var warehouseCount = 0;
        var selectedMatches = new List<ItemSearchRecord>();
        foreach (var item in matches)
        {
            if (item.StorageKind == StorageKind.Inventory) inventoryCount++; else warehouseCount++;
            if (item.StorageKind == selectedStorage) selectedMatches.Add(item);
        }
        DrawStorageTab(new Rect(left, windowRect.y + S(260f), S(190f), S(32f)), StorageKind.Inventory, $"{UiText.L("인벤토리", "INVENTORY", "背包", "背包")}  {inventoryCount}", new Color(0.32f, 0.86f, 0.46f));
        DrawStorageTab(new Rect(left + S(198f), windowRect.y + S(260f), S(190f), S(32f)), StorageKind.Warehouse, $"{UiText.L("창고", "WAREHOUSE", "仓库", "倉庫")}  {warehouseCount}", new Color(0.25f, 0.78f, 0.92f));

        var resultTop = windowRect.y + S(302f);
        var resultRowStep = S(76f);
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
                DrawResult(pageItems[index], new Rect(left, resultTop + index * resultRowStep, width, S(70f)));
        }

        if (GUI.Button(new Rect(left, windowRect.yMax - S(43f), S(88f), S(28f)), UiText.L("◀ 이전", "◀ Previous", "◀ 上一页", "◀ 上一頁"), compactButtonStyle!) && currentPage > 0) currentPage--;
        GUI.Label(new Rect(left + S(94f), windowRect.yMax - S(43f), S(90f), S(28f)), $"{currentPage + 1} / {pageCount}", pageStyle!);
        if (GUI.Button(new Rect(left + S(190f), windowRect.yMax - S(43f), S(88f), S(28f)), UiText.L("다음 ▶", "Next ▶", "下一页 ▶", "下一頁 ▶"), compactButtonStyle!) && currentPage + 1 < pageCount) currentPage++;
        GUI.Label(new Rect(left + S(294f), windowRect.yMax - S(40f), Math.Max(S(80f), width - S(294f)), S(24f)), UiText.L("검색어가 있으면 일치한 아이템만 표시됩니다.", "A search shows matching items only.", "输入搜索词后仅显示匹配物品。", "輸入搜尋詞後僅顯示相符物品。"), hintStyle!);

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
            if (!imeModeSaved)
            {
                previousImeMode = Input.imeCompositionMode;
                imeModeSaved = true;
            }
            Input.imeCompositionMode = IMECompositionMode.On;
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
            keyboardReleasePending = keyboardBlockCaptured;
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
        if (inputBlockerRegionObject is null || inputBlockerRect is null) return;
        inputBlockerRegionObject.SetActive(true);
        inputBlockerRect.anchorMin = new Vector2(0f, 1f);
        inputBlockerRect.anchorMax = new Vector2(0f, 1f);
        inputBlockerRect.pivot = new Vector2(0f, 1f);
        if (resizing || dragging)
        {
            inputBlockerRect.anchoredPosition = Vector2.zero;
            inputBlockerRect.sizeDelta = new Vector2(Screen.width, Screen.height);
        }
        else
        {
            inputBlockerRect.anchoredPosition = new Vector2(windowRect.x, -windowRect.y);
            inputBlockerRect.sizeDelta = new Vector2(windowRect.width, windowRect.height);
        }
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
        if (activeOverlay?.overlayInputFocused != true) return;
        var current = Event.current;
        if (current is not null && current.type is EventType.KeyDown or EventType.KeyUp) current.Use();
    }

    private bool ShouldBlockPointerInput()
    {
        if (!visible) return false;
        var mouse = Input.mousePosition;
        var guiMouse = new Vector2(mouse.x, Screen.height - mouse.y);
        // Pointer input is local to the visible panel/tooltip. Clicking elsewhere
        // still releases keyboard focus, but a wheel over the panel must never also
        // reach the game's character selector.
        return resizing || dragging || windowRect.Contains(guiMouse) || activeTooltipRect.Contains(guiMouse);
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
        DrawPageTab(new Rect(x, y, S(122f), S(27f)), OverlayPage.Search, UiText.L("아이템 검색", "Item Search", "物品搜索", "物品搜尋"));
        DrawPageTab(new Rect(x + S(128f), y, S(122f), S(27f)), OverlayPage.BulkOpen, UiText.L("일괄 개봉", "Bulk Open", "批量开启", "批次開啟"));
        DrawPageTab(new Rect(x + S(256f), y, S(122f), S(27f)), OverlayPage.AutoBuild, UiText.L("자동 빌드", "Auto Build", "自动配装", "自動配裝"));
    }

    [HideFromIl2Cpp]
    private void DrawPageTab(Rect rect, OverlayPage page, string label)
    {
        var previousBackground = GUI.backgroundColor;
        var previousEnabled = GUI.enabled;
        if (bulkSession is not null && page != OverlayPage.BulkOpen) GUI.enabled = false;
        if (selectedPage == page) GUI.backgroundColor = new Color(0.30f, 0.70f, 0.96f);
        if (GUI.Button(rect, label, compactButtonStyle!) && selectedPage != page)
        {
            selectedPage = page;
            focusSearch = page == OverlayPage.Search;
            focusSpeedInput = false;
            armedBulkOpen = BulkToolKind.None;
            armedAutoBuild = AutoBuildAction.None;
            nextRefreshAt = 0f;
            RefreshCurrentPage();
        }
        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    private void RefreshCurrentPage(bool resetStatus = true)
    {
        if (languageMode == "auto") UpdateUiLanguage();
        if (selectedPage == OverlayPage.AutoBuild)
        {
            var summary = GameInventoryReader.GetSelectedHeroBuildSummary();
            selectedHeroSummary = summary.Hero;
            selectedHeroProfile = summary.Profile;
            if (resetStatus) status = summary.Status;
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
        var header = new Rect(windowRect.x, windowRect.y, Math.Max(S(80f), windowRect.width - S(382f)), S(40f));
        if (current.type == EventType.MouseDown && current.button == 0 && header.Contains(current.mousePosition))
        {
            dragging = true;
            dragOffset = current.mousePosition - new Vector2(windowRect.x, windowRect.y);
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && dragging)
        {
            windowRect.x = current.mousePosition.x - dragOffset.x;
            windowRect.y = current.mousePosition.y - dragOffset.y;
            ClampWindowToScreen();
            current.Use();
        }
        else if (current.type == EventType.MouseUp && current.button == 0 && dragging)
        {
            dragging = false;
            SaveWindowPosition();
            current.Use();
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
        var size = Math.Max(18f, S(20f));
        return new Rect(windowRect.xMax - size, windowRect.yMax - size, size, size);
    }

    private void DrawResizeGrip()
    {
        var previousColor = GUI.color;
        GUI.color = resizing ? new Color(1f, 0.78f, 0.32f, 1f) : new Color(0.74f, 0.76f, 0.82f, 0.95f);
        GUI.Label(ResizeHandleRect(), "◢", closeStyle!);
        GUI.color = previousColor;
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
        if (!float.IsFinite(windowRect.width)) windowRect.width = DefaultWindowWidth;
        if (!float.IsFinite(windowRect.height)) windowRect.height = DefaultWindowHeight;
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
    private void DrawResult(ItemSearchRecord item, Rect rect)
    {
        var tooltipOwnsPointer = hoveredItem is not null && tooltipAnchorRect.Contains(Event.current.mousePosition);
        var previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = item.StorageKind == StorageKind.Inventory
            ? new Color(0.30f, 0.76f, 0.42f, 0.72f)
            : new Color(0.20f, 0.68f, 0.82f, 0.72f);
        GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.Label(new Rect(rect.x + S(10f), rect.y + S(5f), rect.width - S(260f), S(22f)), HighlightMatches(item.Name, item.StorageKind), resultNameStyle!);
        GUI.Label(new Rect(rect.xMax - S(244f), rect.y + S(5f), S(98f), S(22f)), item.QualityLabel, badgeStyle!);
        var transferLabel = item.StorageKind == StorageKind.Inventory
            ? UiText.L("창고로 이동", "Move to storage", "移至仓库", "移至倉庫")
            : UiText.L("인벤토리로 이동", "Move to inventory", "移至背包", "移至背包");
        var transferRect = new Rect(rect.xMax - S(140f), rect.y + S(4f), S(130f), S(28f));
        visibleTransferRects.Add(transferRect);
        var previousEnabled = GUI.enabled;
        if (tooltipOwnsPointer) GUI.enabled = false;
        if (GUI.Button(transferRect, transferLabel, compactButtonStyle!)) TransferItem(item);
        GUI.enabled = previousEnabled;
        var level = item.Level is > 0 ? $"Lv.{item.Level}" : UiText.L("레벨 미상", "Unknown level", "等级未知", "等級未知");
        GUI.Label(new Rect(rect.x + S(10f), rect.y + S(27f), rect.width - S(150f), S(19f)), HighlightMatches($"{item.StorageLabel}  ·  {item.PartName}  ·  {level}", item.StorageKind), resultMetaStyle!);
        var optionPreview = string.IsNullOrWhiteSpace(item.SetName)
            ? item.AffixSummary
            : $"{UiText.L("세트", "Set", "套装", "套裝")} {item.SetName}  ·  {item.AffixSummary}".TrimEnd(' ', '·');
        if (!string.IsNullOrWhiteSpace(optionPreview))
            GUI.Label(new Rect(rect.x + S(10f), rect.y + S(47f), rect.width - S(150f), S(18f)), HighlightMatches(optionPreview, item.StorageKind), resultAffixStyle!);
        if (!tooltipOwnsPointer && rect.Contains(Event.current.mousePosition) && !transferRect.Contains(Event.current.mousePosition)) hoveredItem = item;
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
        var bodyHeight = tooltipBodyStyle!.CalcHeight(new GUIContent(body), contentWidth);
        var tooltipHeight = Mathf.Clamp(bodyHeight + S(58f), S(190f), Screen.height - ScreenMargin * 2f);
        var itemKey = $"{UiText.LanguageCode}|{uiScale:0.###}|{windowRect.width:0}|{windowRect.height:0}|{item.StorageLabel}|{item.Name}|{item.Level}|{item.AffixSummary}";
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
        var previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.025f, 0.025f, 0.035f, 1f);
        for (var layer = 0; layer < 9; layer++) GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.Label(new Rect(rect.x + S(14f), rect.y + S(10f), rect.width - S(28f), S(26f)), item.Name, tooltipTitleStyle!);
        var viewport = new Rect(rect.x + S(10f), rect.y + S(40f), rect.width - S(20f), rect.height - S(50f));
        var scrollHeight = Math.Max(viewport.height, bodyHeight + S(8f));
        tooltipScroll = GUI.BeginScrollView(viewport, tooltipScroll, new Rect(0f, 0f, contentWidth, scrollHeight));
        GUI.Label(new Rect(0f, 0f, contentWidth, bodyHeight), body, tooltipBodyStyle!);
        GUI.EndScrollView();
        UpdateInputBlocker();
    }

    [HideFromIl2Cpp]
    private void TransferItem(ItemSearchRecord item)
    {
        if (Time.unscaledTime < transferCooldownUntil) return;
        transferCooldownUntil = Time.unscaledTime + 0.75f;
        if (!GameInventoryReader.TryTransfer(item, out var message))
        {
            status = message;
            Plugin.DiagWarning($"Item transfer failed: {message}");
            return;
        }

        status = message;
        Plugin.DiagInfo(message);
        currentPage = 0;
        nextRefreshAt = Time.unscaledTime + 0.25f;
        RefreshItems();
        // RefreshItems normally replaces status with the match count. Preserve the
        // verified destination so the user can actually see where the item went.
        status = message;
    }

    [HideFromIl2Cpp]
    private void DrawQualityFilters(Rect rect)
    {
        GUI.Label(new Rect(rect.x + S(4f), rect.y + S(7f), S(54f), S(20f)), UiText.L("등급", "Quality", "品质", "品質"), utilityTitleStyle!);
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
        var buttonWidth = Math.Max(S(54f), Math.Min(S(88f), (rect.width - S(76f)) / 5f - S(6f)));
        for (var index = 0; index < entries.Length; index++)
        {
            var row = index / 5;
            var column = index % 5;
            var entry = entries[index];
            DrawQualityButton(new Rect(rect.x + S(60f) + column * (buttonWidth + S(6f)), rect.y + S(3f) + row * S(34f), buttonWidth, S(28f)), entry.Quality, entry.Label);
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
        if (GUI.Button(rect, label, compactButtonStyle!))
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
        GUI.Label(new Rect(rect.x + S(18f), rect.y + S(44f), rect.width - S(36f), S(40f)), UiText.L(
            "아래 등급은 상자 자체를 거르는 조건입니다. 상자에서 나올 보상 등급을 정하는 기능은 아닙니다.",
            "The quality filter selects the boxes themselves; it does not control reward quality.",
            "下方品质筛选的是箱子本身，并不会决定开出的奖励品质。",
            "下方品質篩選的是箱子本身，並不會決定開出的獎勵品質。"), hintStyle!);

        var panelEnabled = GUI.enabled;
        if (bulkSession is not null) GUI.enabled = false;
        var nextSkip = GUI.Toggle(new Rect(rect.x + S(18f), rect.y + S(91f), S(145f), S(24f)), Plugin.SkipBulkConfirmation.Value, UiText.L(" 2단계 확인 생략", " Skip second confirm", " 跳过二次确认", " 略過二次確認"), toggleStyle!);
        if (nextSkip != Plugin.SkipBulkConfirmation.Value)
        {
            Plugin.SkipBulkConfirmation.Value = nextSkip;
            armedBulkOpen = BulkToolKind.None;
            status = nextSkip
                ? UiText.L("일괄 개봉 확인을 생략합니다.", "Bulk opening will run with one click.", "批量开启将单击执行。", "批次開啟將單擊執行。")
                : UiText.L("일괄 개봉은 두 번 눌러야 실행됩니다.", "Bulk opening requires two clicks.", "批量开启需要点击两次。", "批次開啟需要點擊兩次。");
        }
        var nextAutoStore = GUI.Toggle(new Rect(rect.x + S(180f), rect.y + S(91f), Math.Max(S(160f), rect.width - S(198f)), S(24f)), Plugin.AutoStoreOpenedEquipment.Value, UiText.L(" 개봉 장비 자동 창고·Vault 이동", " Auto-store opened gear", " 开出的装备自动入库", " 開出的裝備自動入庫"), toggleStyle!);
        if (nextAutoStore != Plugin.AutoStoreOpenedEquipment.Value)
        {
            Plugin.AutoStoreOpenedEquipment.Value = nextAutoStore;
            armedBulkOpen = BulkToolKind.None;
            status = nextAutoStore
                ? UiText.L("개봉 장비를 게임 규칙에 따라 자동 보관합니다.", "Opened gear follows the game's automatic storage rules.", "开启的装备将按游戏规则自动入库。", "開啟的裝備將按遊戲規則自動入庫。")
                : UiText.L("개봉 장비를 인벤토리에 남깁니다.", "Opened gear stays in the inventory.", "开启的装备将留在背包。", "開啟的裝備將留在背包。");
        }
        DrawBulkOpenButton(new Rect(rect.x + S(18f), rect.y + S(130f), (rect.width - S(42f)) / 2f, S(52f)), BulkToolKind.EquipmentBox, equipmentBoxCount, UiText.L("장비 상자", "Gear boxes", "装备箱", "裝備箱"));
        DrawBulkOpenButton(new Rect(rect.x + S(24f) + (rect.width - S(42f)) / 2f, rect.y + S(130f), (rect.width - S(42f)) / 2f, S(52f)), BulkToolKind.RuneBox, runeBoxCount, UiText.L("룬 상자", "Rune boxes", "符文箱", "符文箱"));

        GUI.Label(new Rect(rect.x + S(18f), rect.y + S(202f), S(110f), S(24f)), UiText.L("상자 등급", "Box Quality", "箱子品质", "箱子品質"), utilityTitleStyle!);
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
        var qualityWidth = (rect.width - S(60f)) / 5f;
        for (var index = 0; index < qualityEntries.Length; index++)
        {
            var row = index / 5;
            var column = index % 5;
            var entry = qualityEntries[index];
            DrawBulkQualityButton(new Rect(rect.x + S(18f) + column * (qualityWidth + S(6f)), rect.y + S(232f) + row * S(40f), qualityWidth, S(34f)), entry.Quality, entry.Label);
        }
        var nextAtLeast = GUI.Toggle(new Rect(rect.x + S(18f), rect.y + S(318f), S(190f), S(24f)), Plugin.BulkQualityAtLeast.Value, UiText.L(" 선택 등급 이상 모두", " Selected or higher", " 所选品质及以上", " 所選品質以上"), toggleStyle!);
        if (nextAtLeast != Plugin.BulkQualityAtLeast.Value)
        {
            Plugin.BulkQualityAtLeast.Value = nextAtLeast;
            armedBulkOpen = BulkToolKind.None;
            RefreshBulkCounts();
        }
        GUI.enabled = panelEnabled;
        if (bulkSession is not null)
        {
            GUI.Label(new Rect(rect.x + S(18f), rect.y + S(348f), rect.width - S(150f), S(42f)),
                UiText.L(
                    $"진행 중 · {bulkSession.Opened:N0}/{bulkSession.Initial:N0}개 확인",
                    $"Opening · {bulkSession.Opened:N0}/{bulkSession.Initial:N0} confirmed",
                    $"进行中 · 已确认 {bulkSession.Opened:N0}/{bulkSession.Initial:N0}",
                    $"進行中 · 已確認 {bulkSession.Opened:N0}/{bulkSession.Initial:N0}"), hintStyle!);
            if (GUI.Button(new Rect(rect.xMax - S(122f), rect.y + S(349f), S(104f), S(32f)), UiText.L("중단", "Cancel", "取消", "取消"), buttonStyle!))
                bulkSession.CancelRequested = true;
        }
        else
        {
            GUI.Label(new Rect(rect.x + S(18f), rect.y + S(352f), rect.width - S(36f), S(38f)), status, hintStyle!);
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
        if (GUI.Button(new Rect(rect.xMax - S(112f), rect.y + S(16f), S(92f), S(30f)), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshCurrentPage();
        GUI.Label(new Rect(rect.x + S(20f), rect.y + S(54f), rect.width - S(40f), S(42f)), UiText.L(
            "게임에서 영웅을 먼저 선택하세요. 공격 테마는 피해 근사치, 방어 테마는 생존 능력치를 우선하며 공식 추천 장비에는 가산점을 주지 않습니다.",
            "Select a hero first. Damage themes prioritize the damage proxy; Defense prioritizes survival stats. Official guide gear gets no score bonus.",
            "请先选择英雄。伤害主题优先伤害近似值，防御主题优先生存属性；官方指南装备不获得评分加成。",
            "請先選擇英雄。傷害主題優先傷害近似值，防禦主題優先生存屬性；官方指南裝備不獲得評分加成。"), hintStyle!);

        GUI.backgroundColor = new Color(0.24f, 0.42f, 0.62f, 0.90f);
        GUI.Box(new Rect(rect.x + S(20f), rect.y + S(105f), rect.width - S(40f), S(92f)), GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.Label(new Rect(rect.x + S(36f), rect.y + S(118f), rect.width - S(72f), S(28f)), string.IsNullOrWhiteSpace(selectedHeroSummary)
            ? UiText.L("선택된 영웅 없음", "No hero selected", "未选择英雄", "未選擇英雄")
            : selectedHeroSummary, utilityTitleStyle!);
        GUI.Label(new Rect(rect.x + S(36f), rect.y + S(151f), rect.width - S(72f), S(34f)), selectedHeroProfile, hintStyle!);

        GUI.Label(new Rect(rect.x + S(24f), rect.y + S(207f), S(150f), S(24f)), UiText.L("빌드 테마", "Build Theme", "构筑主题", "流派主題"), utilityTitleStyle!);
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
            DrawAutoBuildThemeButton(new Rect(rect.x + S(24f) + column * (themeWidth + themeGap), rect.y + S(234f) + row * S(36f), themeWidth, S(30f)), themes[index].Key, themes[index].Label);
        }

        var nextStorage = GUI.Toggle(new Rect(rect.x + S(24f), rect.y + S(316f), S(300f), S(26f)), Plugin.AutoBuildIncludeStorage.Value, UiText.L(" 창고·Vault 장비도 후보에 포함", " Include warehouse and Vault gear", " 包含仓库和宝库装备", " 包含倉庫和寶庫裝備"), toggleStyle!);
        if (nextStorage != Plugin.AutoBuildIncludeStorage.Value)
        {
            Plugin.AutoBuildIncludeStorage.Value = nextStorage;
            armedAutoBuild = AutoBuildAction.None;
        }
        var nextTransform = GUI.Toggle(new Rect(rect.x + S(340f), rect.y + S(316f), Math.Max(S(180f), rect.width - S(364f)), S(26f)), Plugin.AutoTransformSkills.Value, UiText.L($" 부족한 성능 계획 스킬 변환 (최대 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)}회)", $" Transform missing plan skills (max {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)})", $" 转换缺少的性能方案技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）", $" 轉換缺少的效能方案技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）"), toggleStyle!);
        if (nextTransform != Plugin.AutoTransformSkills.Value)
        {
            Plugin.AutoTransformSkills.Value = nextTransform;
            armedAutoBuild = AutoBuildAction.None;
        }

        DrawAutoBuildButton(new Rect(rect.x + S(24f), rect.y + S(350f), rect.width - S(48f), S(58f)), AutoBuildAction.Combined,
            UiText.L("장비+스킬 자동 빌드", "Auto-build Gear + Skills", "自动构筑装备和技能", "自動配置裝備與技能"));
        var advancedWidth = (rect.width - S(60f)) / 2f;
        DrawAutoBuildButton(new Rect(rect.x + S(24f), rect.y + S(416f), advancedWidth, S(44f)), AutoBuildAction.Gear,
            UiText.L("고급: 장비만 적용", "Advanced: apply gear only", "高级：仅应用装备", "進階：僅套用裝備"));
        DrawAutoBuildButton(new Rect(rect.x + S(36f) + advancedWidth, rect.y + S(416f), advancedWidth, S(44f)), AutoBuildAction.Skills,
            UiText.L("고급: 현재 장비로 스킬만", "Advanced: skills for current gear", "高级：按当前装备仅调整技能", "進階：依目前裝備僅調整技能"));

        GUI.Label(new Rect(rect.x + S(24f), rect.y + S(468f), rect.width - S(48f), S(58f)), UiText.L(
            "장비와 스킬은 동일한 공동 계획과 60초 성능 목표로 연속 최적화합니다. 이는 전체 전투·조건부 효과의 정확한 DPS가 아닙니다.",
            "Gear and skills are optimized in sequence from the same joint plan and 60-second objective. This is not exact full-combat or conditional-effect DPS.",
            "装备与技能会依据同一联合方案和 60 秒性能目标连续优化；这并非完整战斗或条件效果的精确 DPS。",
            "裝備與技能會依據同一聯合方案和 60 秒效能目標連續最佳化；這並非完整戰鬥或條件效果的精確 DPS。"), tooltipBodyStyle!);
        GUI.Label(new Rect(rect.x + S(24f), rect.y + S(528f), rect.width - S(48f), S(38f)), UiText.L(
            "주의: 스킬 변환·특성 초기화에는 게임의 정상 비용이 듭니다. 초기화 비용은 남겨 두며, 실행 버튼은 두 번 눌러야 합니다.",
            "Caution: transformation and talent reset use normal game costs. Reset cost is reserved; each action requires a second click.",
            "注意：技能转换与天赋重置会消耗游戏正常费用。系统会预留重置费用；操作需点击两次。",
            "注意：技能轉換與天賦重設會消耗遊戲正常費用。系統會保留重設費用；操作需點擊兩次。"), hintStyle!);
        GUI.Label(new Rect(rect.x + S(24f), rect.y + S(570f), rect.width - S(48f), S(44f)), status, tooltipBodyStyle!);
    }

    [HideFromIl2Cpp]
    private void DrawAutoBuildThemeButton(Rect rect, string key, string label)
    {
        var selected = string.Equals(GameInventoryReader.GetSelectedHeroBuildTheme(), key, StringComparison.OrdinalIgnoreCase);
        var previousBackground = GUI.backgroundColor;
        if (selected) GUI.backgroundColor = new Color(0.34f, 0.78f, 0.58f);
        if (GUI.Button(rect, label, compactButtonStyle!))
        {
            GameInventoryReader.SetSelectedHeroBuildTheme(key);
            armedAutoBuild = AutoBuildAction.None;
            RefreshCurrentPage();
        }
        GUI.backgroundColor = previousBackground;
    }

    [HideFromIl2Cpp]
    private void DrawAutoBuildButton(Rect rect, AutoBuildAction action, string label)
    {
        var armed = armedAutoBuild == action && Time.unscaledTime <= autoBuildConfirmUntil;
        var previousEnabled = GUI.enabled;
        var previousBackground = GUI.backgroundColor;
        GUI.enabled = !string.IsNullOrWhiteSpace(selectedHeroSummary);
        GUI.backgroundColor = armed ? new Color(1f, 0.48f, 0.18f) : new Color(0.32f, 0.68f, 0.94f);
        var text = armed ? UiText.L($"확인: {label}", $"Confirm: {label}", $"确认：{label}", $"確認：{label}") : label;
        if (GUI.Button(rect, text, buttonStyle!)) BeginOrConfirmAutoBuild(action, label);
        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    private void BeginOrConfirmAutoBuild(AutoBuildAction action, string label)
    {
        if (armedAutoBuild != action || Time.unscaledTime > autoBuildConfirmUntil)
        {
            armedAutoBuild = action;
            autoBuildConfirmUntil = Time.unscaledTime + 5f;
            status = UiText.L($"{label}: 같은 버튼을 한 번 더 누르면 실행합니다.", $"{label}: click again to run.", $"{label}：再次点击即可执行。", $"{label}：再次點擊即可執行。");
            return;
        }

        armedAutoBuild = AutoBuildAction.None;
        var message = UiText.L(
            $"알 수 없는 자동 빌드 작업입니다 ({action}). 아무 작업도 실행하지 않았습니다.",
            $"Unknown auto-build action ({action}). Nothing was changed.",
            $"未知的自动构筑操作（{action}）。未执行任何操作。",
            $"未知的自動配置操作（{action}）。未執行任何操作。");
        var succeeded = action switch
        {
            AutoBuildAction.Combined => GameInventoryReader.TryOptimizeSelectedHeroBuild(Plugin.AutoBuildIncludeStorage.Value, out message),
            AutoBuildAction.Gear => GameInventoryReader.TryOptimizeSelectedHeroGear(Plugin.AutoBuildIncludeStorage.Value, out message),
            AutoBuildAction.Skills => GameInventoryReader.TryOptimizeSelectedHeroSkills(out message),
            _ => false
        };
        status = message;
        if (succeeded) Plugin.DiagInfo(message); else Plugin.DiagWarning(message);
        var summary = GameInventoryReader.GetSelectedHeroBuildSummary();
        selectedHeroSummary = summary.Hero;
        selectedHeroProfile = summary.Profile;
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
        if (GUI.Button(rect, label, compactButtonStyle!))
        {
            Plugin.BulkQualityMask.Value = quality == 0 ? 0 : mask ^ QualityFilterLogic.BitFor(quality);
            armedBulkOpen = BulkToolKind.None;
            RefreshBulkCounts();
        }
        GUI.backgroundColor = previousBackground;
    }

    [HideFromIl2Cpp]
    private void DrawBulkOpenButton(Rect rect, BulkToolKind kind, int count, string label)
    {
        var armed = armedBulkOpen == kind && Time.unscaledTime <= bulkConfirmUntil;
        var previousEnabled = GUI.enabled;
        var previousBackground = GUI.backgroundColor;
        GUI.enabled = previousEnabled && bulkSession is null && bulkCountsAvailable && count > 0;
        GUI.backgroundColor = armed ? new Color(1f, 0.48f, 0.18f) : new Color(0.34f, 0.62f, 0.92f);
        var text = !bulkCountsAvailable
            ? UiText.L("상자 데이터 확인 불가", "Box data unavailable", "箱子数据不可用", "箱子資料不可用")
            : count <= 0
            ? UiText.L($"{label} 없음", $"No {label}", $"没有{label}", $"沒有{label}")
            : armed
                ? UiText.L($"확인: {label} {count:N0}개 모두 열기", $"Confirm: open all {count:N0} {label}", $"确认：开启全部 {count:N0} 个{label}", $"確認：開啟全部 {count:N0} 個{label}")
                : UiText.L($"{label} 모두 열기  ·  {count:N0}개", $"Open all {label}  ·  {count:N0}", $"开启全部{label}  ·  {count:N0}", $"開啟全部{label}  ·  {count:N0}");
        if (GUI.Button(rect, text, buttonStyle!)) BeginOrConfirmBulkOpen(kind, count, label);
        GUI.backgroundColor = previousBackground;
        GUI.enabled = previousEnabled;
    }

    [HideFromIl2Cpp]
    private void BeginOrConfirmBulkOpen(BulkToolKind kind, int count, string label)
    {
        if (bulkSession is not null) return;
        if (!Plugin.SkipBulkConfirmation.Value && (armedBulkOpen != kind || Time.unscaledTime > bulkConfirmUntil))
        {
            armedBulkOpen = kind;
            bulkConfirmUntil = Time.unscaledTime + 4f;
            status = UiText.L(
                $"{label} {count:N0}개: 같은 버튼을 한 번 더 누르면 모두 엽니다.",
                $"{count:N0} {label}: click the same button again to open all.",
                $"{count:N0} 个{label}：再次点击同一按钮即可全部开启。",
                $"{count:N0} 個{label}：再次點擊同一按鈕即可全部開啟。");
            return;
        }

        armedBulkOpen = BulkToolKind.None;
        if (!GameInventoryReader.TryBeginBulkOpen(kind, Plugin.BulkQualityMask.Value, Plugin.BulkQualityAtLeast.Value,
                Plugin.AutoStoreOpenedEquipment.Value, label, out var session, out var message))
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
        var step = GameInventoryReader.AdvanceBulkOpen(bulkSession);
        status = step.Message;
        if (!step.Finished) return;
        var finalMessage = step.Message;
        bulkSession = null;
        currentPage = 0;
        nextRefreshAt = Time.unscaledTime + 0.25f;
        RefreshBulkCounts();
        // Count refresh is useful, but the terminal action result must remain
        // visible even if that refresh itself fails or recovers.
        status = finalMessage;
    }

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
            focusSearch = searchRect.Contains(current.mousePosition);
            if (focusSearch)
            {
                focusSpeedInput = false;
                searchCaret = query.Length;
                Input.imeCompositionMode = IMECompositionMode.On;
                Input.compositionCursorPos = new Vector2(searchRect.x + S(12f), searchRect.yMax + S(4f));
                current.Use();
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

    private void RefreshItems(bool resetStatus = true)
    {
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
        resultNameStyle.wordWrap = true;
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
        resultAffixStyle.wordWrap = true;
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
        compactButtonStyle = CopyStyle(buttonStyle);
        compactButtonStyle.fontSize = 11;
        toggleStyle = CopyStyle(GUI.skin.toggle);
        toggleStyle.font = uiFont ?? toggleStyle.font;
        toggleStyle.fontSize = 11;
        toggleStyle.fontStyle = FontStyle.Bold;
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
        titleStyle!.fontSize = FontSize(uiScale, 20, 16, 26);
        hintStyle!.fontSize = FontSize(uiScale, 12, 10, 16);
        searchTextStyle!.fontSize = FontSize(uiScale, 17, 13, 22);
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

internal sealed class SearchQuery
{
    private readonly List<string[]> requiredGroups;
    private readonly List<string[]> excludedGroups;

    private SearchQuery(List<string[]> requiredGroups, List<string[]> excludedGroups)
    {
        this.requiredGroups = requiredGroups;
        this.excludedGroups = excludedGroups;
    }

    public static SearchQuery Parse(string value)
    {
        var required = new List<string[]>();
        var excluded = new List<string[]>();
        var pending = new List<string>();
        var pendingExcluded = false;
        var joinNext = false;

        void Flush()
        {
            if (pending.Count == 0) return;
            var alternatives = pending.Distinct(StringComparer.Ordinal).ToArray();
            (pendingExcluded ? excluded : required).Add(alternatives);
            pending.Clear();
        }

        // Pipe is a real token, so `fire|cold`, `fire | cold`, quoted phrases,
        // and negative quoted phrases all share the same parser.
        foreach (Match match in Regex.Matches(value ?? string.Empty,
                     @"(?<or>\|)|(?<exclude>-)?(?:""(?<quoted>[^""]+)""|(?<word>[^\s|]+))"))
        {
            if (match.Groups["or"].Success)
            {
                if (pending.Count > 0) joinNext = true;
                continue;
            }
            var token = Normalize(match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["word"].Value);
            if (token.Length == 0) continue;
            var isExcluded = match.Groups["exclude"].Success;
            if (pending.Count == 0)
            {
                pendingExcluded = isExcluded;
                pending.Add(token);
            }
            else if (joinNext && pendingExcluded == isExcluded)
            {
                pending.Add(token);
            }
            else
            {
                Flush();
                pendingExcluded = isExcluded;
                pending.Add(token);
            }
            joinNext = false;
        }
        Flush();
        return new SearchQuery(required, excluded);
    }

    public static List<string> HighlightTerms(string value)
    {
        return Parse(value).requiredGroups.SelectMany(group => group)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public bool Matches(string searchableText)
    {
        var normalized = Normalize(searchableText);
        if (excludedGroups.Any(group => group.Any(normalized.Contains))) return false;
        return requiredGroups.All(group => group.Any(normalized.Contains));
    }

    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}

internal sealed class ItemSearchRecord
{
    public string Name { get; init; } = string.Empty;
    public string QualityLabel { get; init; } = string.Empty;
    public int Quality { get; init; }
    public int? Level { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string StorageLabel { get; init; } = string.Empty;
    public StorageKind StorageKind { get; init; }
    public string AffixSummary { get; init; } = string.Empty;
    public string AffixSearchText { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SetName { get; init; } = string.Empty;
    public string SetJob { get; init; } = string.Empty;
    public string SetMembers { get; init; } = string.Empty;
    public string SetBonuses { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    public object ItemData { get; init; } = null!;
    public object? SourceField { get; init; }
    public object? GroupData { get; init; }
    public StorageSource StorageSource { get; init; }
}

internal enum StorageKind
{
    Inventory,
    Warehouse
}

internal enum StorageSource
{
    Inventory,
    Warehouse,
    Treasure,
    Equipped
}

internal enum OverlayPage
{
    Search,
    BulkOpen,
    AutoBuild
}

internal enum AutoBuildAction
{
    None,
    Combined,
    Gear,
    Skills
}

internal enum BulkToolKind
{
    None,
    EquipmentBox,
    RuneBox
}

internal static class QualityFilterLogic
{
    private static readonly int[] RankedQualities = { 1, 2, 3, 4, 5, 6, 7, 8 };

    public static int BitFor(int quality) => quality switch
    {
        // Preserve every bit used by earlier releases, then append Common/Fine.
        1 => 1 << 7,
        2 => 1 << 8,
        3 => 1 << 0,
        4 => 1 << 1,
        5 => 1 << 2,
        6 => 1 << 3,
        8 => 1 << 4,
        // Keep the existing Unique/Other/Magic bits stable for saved configs.
        7 => 1 << 6,
        _ => 1 << 5
    };

    public static bool IsSelected(int mask, int quality) => (mask & BitFor(quality)) != 0;

    public static bool Matches(int quality, int mask, bool atLeast)
    {
        if (mask == 0) return true;
        if (!atLeast) return IsSelected(mask, quality);

        var qualityRank = Array.IndexOf(RankedQualities, quality);
        if (qualityRank < 0) return IsSelected(mask, -1);
        var minimumRank = int.MaxValue;
        for (var rank = 0; rank < RankedQualities.Length; rank++)
            if (IsSelected(mask, RankedQualities[rank])) minimumRank = Math.Min(minimumRank, rank);
        return minimumRank != int.MaxValue && qualityRank >= minimumRank;
    }
}

internal sealed record BulkToolStack(string Key, BulkToolKind Kind, object ItemData, int Count, int Quality);
internal sealed class BulkOpenSession
{
    public BulkToolKind Kind { get; init; }
    public int QualityMask { get; init; }
    public bool AtLeast { get; init; }
    public bool AutoStoreEquipment { get; init; }
    public string Label { get; init; } = string.Empty;
    public string SaveIdentity { get; init; } = string.Empty;
    public int Initial { get; init; }
    public int Opened { get; set; }
    public int AutoStored { get; set; }
    public bool CancelRequested { get; set; }
    public bool MutationAttempted { get; set; }
    public HashSet<string> KnownEquipment { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> FailedAutoStore { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> BlockedStacks { get; init; } = new(StringComparer.Ordinal);
}
internal sealed record InventoryEquipment(string Key, object ItemData, object SourceField);
internal sealed record AutoBuildSummary(string Hero, string Profile, string Status);
internal sealed record GearCandidate(ItemSearchRecord Record, string Key, int Part, int SetId, int DefinitionId, int WeaponType, double Score, double NumericScore, int DirectMatches, int ThemeMatches, HashSet<string> NonStackingEffectKeys);
internal sealed record TeamCandidate(string Key, string Name, string Job, double Offense, double Defense, double Support, double Control, double Power, HashSet<string> Themes, string BuildHint);
internal sealed record TeamSuggestion(TeamCandidate A, TeamCandidate B, TeamCandidate C, double Score, string Reason);

internal static class GameInventoryReader
{
    private static readonly Regex RichText = new("<[^>]+>", RegexOptions.Compiled);
    private static Assembly? gameAssembly;
    private static bool languageTableLogged;
    private static readonly HashSet<string> loggedBulkToolQualities = new(StringComparer.Ordinal);
    private static bool bulkCountFailureLogged;
#if PATHOFIDLE_RUNTIME_TEST
    internal static bool RuntimeForceJointRollback { get; set; }
#endif

    private sealed record HeroEffectProfile(
        HeroFocus Focus,
        int JobId,
        HashSet<int> AllowedWeaponTypes,
        HashSet<int> BaseWeaponRequirement,
        List<HashSet<int>> SkillWeaponPreferences,
        int ActiveSkillMainType,
        HashSet<int> ActiveSkillTags,
        int PreviewBaseSkillId,
        int PreviewBaseSkillLevel,
        HashSet<int> SkillIds,
        HashSet<int> SkillInfoIds,
        HashSet<int> TalentIds,
        HashSet<int> MasteryIds,
        HashSet<int> PreferredTalentIds,
        HashSet<int> PreferredSkillIds,
        HashSet<int> PreferredMasteryIds,
        HashSet<int> AbilityIds,
        HashSet<int> RecommendedEquipmentIds,
        HashSet<string> RecommendedRunewordKeys,
        string[] SkillTerms,
        PlannedSkillTarget PlannedSkills,
        string JointSkillReason,
        double JointSkillObjective);

    private sealed record EquipmentScore(double Total, int DirectMatches, int ThemeMatches);
    private sealed record EquipAttrMapping(int EquipType, int BattleAttrType);
    private sealed record SetEffectScoreRow(
        int EffectId,
        int Pieces,
        string Text,
        int AbilityId,
        IReadOnlyList<int> AbilityParameters);
    private sealed record LoadoutEvaluation(
        double Score,
        HeroEffectProfile Profile,
        bool IsValid = true,
        string Failure = "");
    private sealed record RememberedJointSkillPlan(
        string FocusKey,
        int BaseSkillId,
        HashSet<int> ActiveSkillIds,
        Dictionary<int, int> GrantedSkillLevels,
        HashSet<int> MasteryTalentIds,
        Dictionary<int, int> TargetSavedLevels,
        int TotalTalentPointBudget,
        string PlanToken,
        string GearFingerprint,
        string Reason);
    private sealed record GearSlot(int Part, bool MainWeapon, int WeaponSlotIndex, string Label);
    private enum MoveReceiptKind { FieldMove, BagToVault, VaultToBag }
    private sealed record MoveReceipt(MoveReceiptKind Kind, object? FromField, object? ToField, object BeforeFromItem, object? BeforeToItem, object? TreasureData = null, object? GroupData = null);
    private sealed record PendingGearCommit(
        object Hero,
        string HeroKey,
        string OriginalGearFingerprint,
        List<MoveReceipt> MoveJournal,
        object? SeasonData,
        List<object> TargetItems);
    private sealed record SaveTalentRollbackState(
        int DictionaryKey,
        object SaveTalent,
        int Id,
        int Level,
        int PosId,
        bool IsFixed,
        bool IsInspired,
        bool IsInspiredLocked,
        int InspireBaseLevel,
        int InspireRow,
        int InspireCol,
        int InspireMaxLevel,
        bool IsAlien);
    private sealed record JointSkillRollbackSnapshot(
        object Hero,
        string HeroKey,
        object SaveHero,
        object TalentData,
        object TownData,
        object SaveStatData,
        object BloodType,
        int Blood,
        int BaseSkillId,
        int TalentRemainPoint,
        int TalentStickPoint,
        int BlessTalentPoint,
        int ChangeBaseSkillCount,
        int WashHeroCount,
        int LearnNewSkillCount,
        List<int> LikeTalentIds,
        List<SaveTalentRollbackState> Talents);
    private sealed record JointBuildRollbackResult(
        int GearMoveFailures,
        bool GearFingerprintRestored,
        bool SkillStateRestored,
        bool ProgressStateRestored,
        bool BloodRestored,
        List<string> Failures)
    {
        public bool IsExact => GearMoveFailures == 0 && GearFingerprintRestored
                               && SkillStateRestored && ProgressStateRestored
                               && BloodRestored && Failures.Count == 0;
    }
    private sealed record LoadoutState(List<GearCandidate> Items, HashSet<string> UsedKeys, HashSet<string> NonStackingEffectKeys, double HeuristicScore);
    private sealed record PreferredTalentPlan(
        object? Build,
        List<int> SkillTalentIds,
        List<int> MasteryTalentIds,
        HashSet<int> PreferredSkillIds,
        string BuildName,
        Dictionary<int, double>? ObjectiveScores = null,
        Dictionary<int, int>? TargetSavedLevels = null,
        string PlanToken = "");
    private sealed record PlannedSkillTarget(
        int BaseTalentId,
        int BaseSkillId,
        int BaseSkillLevel,
        HashSet<int> ActiveSkillIds,
        HashSet<int> TalentIds,
        HashSet<int> MasteryTalentIds,
        HashSet<int> BaseCandidateSkillIds,
        HashSet<int> ActiveCandidateSkillIds,
        int ActiveSlotCount,
        Dictionary<int, double> ObjectiveBySkillId,
        int TotalTalentPointBudget,
        Dictionary<int, int> TargetSavedLevels,
        string PlanToken);
    private sealed record TalentLevelPlan(
        Dictionary<int, int> SavedLevels,
        Dictionary<int, int> EffectiveSkillLevels,
        string Token);
    private sealed record PreferredActiveSkill(int GuideTalentId, int TalentId, int SkillId, object Talent);
    private sealed record NativeSkillRoleProfile(
        Dictionary<int, double> DamageByType,
        double Heal,
        double Shield,
        bool Summon,
        double SummonDamage,
        double SummonSurvival,
        double AbilitySupport,
        double AbilityDefense,
        double AbilityMinion,
        double CastOpportunities,
        double ActionSecondsPerCast,
        double HpCostPerCast = 0d,
        double MpCostPerCast = 0d,
        double HpBudget60 = double.MaxValue,
        double MpBudget60 = double.MaxValue,
        bool IsComplete = true,
        string Failure = "",
        double Confidence = 1d);
    private sealed record NativeSkillExecutionGraph(
        List<NativePowerInvocation> PowerInvocations,
        List<NativeAbilityInvocation> AbilityInvocations,
        List<NativeSummonInvocation> SummonInvocations,
        bool IsComplete,
        string Failure);
    private sealed record NativePowerInvocation(
        int PowerId,
        int Level,
        double EventsPerCast,
        object SourceAttr,
        object TriggerData,
        object PowerPreview,
        IReadOnlyList<NativeTimedEvent>? EventTimeline = null);
    private sealed record NativeAbilityInvocation(
        int AbilityId,
        int Level,
        double EventsPerCast,
        object SourceAttr,
        object TriggerData,
        IReadOnlyList<NativeTimedEvent>? EventTimeline = null);
    private sealed record NativeTimedEvent(double OffsetSeconds, double Count);
    private sealed record NativeSummonInvocation(int SummonId, int Level, int CountPerCast, object ProduceAttr);
    private sealed record NativeSummonEvaluation(
        double Damage,
        double Survival,
        double Support,
        double Defense,
        double Minion,
        bool IsComplete,
        string Failure);
    private sealed record NativeStrictAbilityContribution(
        int AbilityId,
        int StackLimit,
        Dictionary<int, double> AttrDeltas);
    private sealed record NativeSkillAlwaysOnPreview(
        List<NativeStrictAbilityContribution> Contributions,
        bool IsComplete,
        string Failure);
    private sealed record NativeAbilityPackage(
        double StructuralScore,
        double OffensiveAttrValue,
        double Support,
        double Defense,
        double Minion,
        Dictionary<int, double> AlwaysOnAttrDeltas,
        Dictionary<int, int> AppliedAbilityCounts,
        bool IsComplete,
        HashSet<int> FailedAbilityIds,
        HashSet<int> UnmodeledAbilityIds);
    private sealed record SkillTransformResult(int Attempts, int Matched, int Target, int SpentBlood, string Note, bool CleanupSucceeded, bool ExecutionSucceeded = true);
    private sealed record SkillVariantVerification(List<int> Expected, List<int> Actual, List<int> Missing, List<int> Unexpected)
    {
        public bool IsExact => Missing.Count == 0 && Unexpected.Count == 0;
    }
    private sealed record DeduplicatedEffectContribution(string Key, int Level, double Value, int DirectMatches);
    private sealed record GearAbilityBaseline(Dictionary<int, int> NonGearCounts);
    private sealed record GearEffectVerification(
        List<string> MissingAbilities,
        List<string> UnexpectedAbilities,
        List<string> MissingExtraTalents,
        List<string> UnexpectedExtraTalents,
        List<int> MissingSetEffects,
        List<int> UnexpectedSetEffects)
    {
        public bool IsVerified => MissingAbilities.Count == 0 && UnexpectedAbilities.Count == 0
                                  && MissingExtraTalents.Count == 0 && UnexpectedExtraTalents.Count == 0
                                  && MissingSetEffects.Count == 0 && UnexpectedSetEffects.Count == 0;
    }
    private static List<EquipAttrMapping>? equipAttrMappings;
    private static Dictionary<int, List<SetEffectScoreRow>>? setEffectScoreRows;
    private static readonly Dictionary<int, string> SetThemeTextCache = new();
    private static readonly Dictionary<string, RememberedJointSkillPlan> JointSkillPlansByHero = new(StringComparer.Ordinal);
    private static bool performanceSimulationFailureLogged;
    private static bool numericPreScoreFailureLogged;
    private static bool nativeSkillPreviewFailureLogged;
    private static bool sustainedDamageModelLogged;

    public static string GetGameLanguageCode()
    {
        try
        {
            LogSupportedLanguagesOnce();
            var dataManager = ReadStatic("Game", "dataMgr");
            var index = ReadNullableInt(Read(dataManager, "nativeData"), "l10nIndex") ?? 0;
            object? row = null;
            foreach (var candidate in new[] { index, index + 1 })
            {
                row = InvokeStatic("TableData", "getTLanguageData", candidate);
                if (row is not null) break;
            }
            var value = ((ReadString(row, "shortName") ?? ReadString(row, "name") ?? string.Empty).Trim()).ToLowerInvariant();
            if (value.Contains("ko") || value.Contains("kr") || value.Contains("korean") || value.Contains("한국")) return "ko";
            if (value.Contains("tchinese") || value == "tc" || value.Contains("traditional") || value.Contains("繁")) return "zh-tw";
            if (value.Contains("schinese") || value == "sc" || value.Contains("cn") || value.Contains("simplified") || value.Contains("简")) return "zh-cn";
            return "en";
        }
        catch
        {
            return "en";
        }
    }

    public static AutoBuildSummary GetSelectedHeroBuildSummary()
    {
        try
        {
            var hero = GetSelectedHero();
            if (hero is null)
                return new AutoBuildSummary(string.Empty, string.Empty, UiText.L("게임에서 최적화할 영웅을 선택하세요.", "Select a hero in the game to optimize.", "请在游戏中选择要优化的英雄。", "請在遊戲中選擇要最佳化的英雄。"));

            var save = Read(hero, "saveHeroData");
            var job = Read(hero, "tHeroJobData");
            var name = Clean(ReadString(save, "name") ?? InvokeString(save ?? hero, "GetL10nName") ?? UiText.L("이름 없는 영웅", "Unnamed hero", "未命名英雄", "未命名英雄"));
            var jobName = Clean(ReadString(hero, "jobName") ?? ReadString(job, "name") ?? UiText.L("직업 미상", "Unknown job", "未知职业", "未知職業"));
            var level = ReadNullableInt(save, "level") ?? 0;
            var quality = ReadNullableInt(save, "quality") ?? 0;
            var requestedTheme = GetHeroBuildTheme(hero);
            var focus = ResolveHeroFocus(hero, requestedTheme);
            var heroLine = $"{name}  ·  {jobName}  ·  Lv.{level}  ·  Q{quality}";
            var profile = requestedTheme == "auto"
                ? UiText.L($"자동 목표: {focus.Localized}  ·  공식 장비 추천 보너스 없음", $"Auto objective: {focus.English}  ·  no official-gear bonus", $"自动目标：{focus.Localized} · 无官方装备加成", $"自動目標：{focus.Localized} · 無官方裝備加成")
                : UiText.L($"선택 목표: {focus.Localized}  ·  공식 장비 추천 보너스 없음", $"Selected objective: {focus.English}  ·  no official-gear bonus", $"所选目标：{focus.Localized} · 无官方装备加成", $"所選目標：{focus.Localized} · 無官方裝備加成");
            return new AutoBuildSummary(heroLine, profile, UiText.L("자동 장착할 기능을 선택하세요.", "Choose an optimization action.", "请选择自动优化功能。", "請選擇自動最佳化功能。"));
        }
        catch (Exception error)
        {
            return new AutoBuildSummary(string.Empty, string.Empty, UiText.L($"영웅 데이터 대기 중: {error.Message}", $"Waiting for hero data: {error.Message}", $"正在等待英雄数据：{error.Message}", $"正在等待英雄資料：{error.Message}"));
        }
    }

    public static bool TryLogTeamRecommendations()
    {
        try
        {
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData");
            var lordData = Read(seasonData, "lordData");
            var owned = new List<TeamCandidate>();
            foreach (var field in ReadList(Read(lordData, "heroFieldList")))
            {
                var hero = Read(field, "heroData");
                if (hero is null) continue;
                var save = Read(hero, "saveHeroData");
                var key = $"hero:{ReadNullableInt(save, "uniqueId") ?? owned.Count}";
                var name = Clean(ReadString(save, "name") ?? InvokeString(save ?? hero, "GetL10nName") ?? key);
                var job = Clean(ReadString(hero, "jobName") ?? ReadString(Read(hero, "tHeroJobData"), "name") ?? "?");
                var textParts = new List<string>
                {
                    EnglishName(Read(hero, "tHeroJobData"), job) ?? job,
                    EnglishText(Read(hero, "tHeroJobData"), "_des", string.Empty) ?? string.Empty
                };
                var buildHints = new List<string>();
                foreach (var talent in ReadValues(Read(Read(hero, "heroTalentData"), "talentDic")))
                {
                    var level = Convert.ToInt32(InvokeInstance(talent, "GetLevel") ?? 0, CultureInfo.InvariantCulture);
                    if (level <= 0) continue;
                    var def = Read(talent, "tTalentData");
                    var skill = Read(talent, "skillData");
                    var skillRow = Read(skill, "tSkillData");
                    var info = Read(skill, "tSkillInfoData");
                    var talentName = Clean(EnglishName(skillRow, EnglishName(def, string.Empty)) ?? string.Empty);
                    if (talentName.Length > 0 && buildHints.Count < 2) buildHints.Add(talentName);
                    textParts.Add(talentName);
                    textParts.Add(EnglishText(info, "_des", string.Empty) ?? string.Empty);
                }
                owned.Add(CreateTeamCandidate(key, name, job, string.Join(" ", textParts), hero, string.Join(" / ", buildHints)));
            }
            if (owned.Count < 3) return false;

            var endgame = new List<TeamCandidate>();
            var talentRows = ReadValues(ReadStatic("TableData", "TTalentDict")).ToList();
            foreach (var jobRow in ReadValues(ReadStatic("TableData", "THeroJobDict")))
            {
                var jobId = ReadNullableInt(jobRow, "id") ?? 0;
                if (jobId <= 0) continue;
                var jobName = Clean(ReadString(jobRow, "name") ?? EnglishName(jobRow, $"Job {jobId}") ?? $"Job {jobId}");
                var textParts = new List<string> { EnglishName(jobRow, jobName) ?? jobName, EnglishText(jobRow, "_des", string.Empty) ?? string.Empty };
                var hints = new List<string>();
                foreach (var talent in talentRows.Where(row => ReadNullableInt(row, "jobId") == jobId))
                {
                    var skillId = ReadNullableInt(talent, "skillId") ?? 0;
                    var masteryId = ReadNullableInt(talent, "masteryId") ?? 0;
                    var skill = skillId > 0 ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
                    var infoId = ReadNullableInt(skill, "infoId") ?? 0;
                    var info = infoId > 0 ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null;
                    var mastery = masteryId > 0 ? InvokeStatic("TableData", "getTMasteryData", masteryId) : null;
                    var hint = Clean(EnglishName(skill, EnglishName(mastery, EnglishName(talent, string.Empty))) ?? string.Empty);
                    if (hint.Length > 0 && !hints.Contains(hint, StringComparer.OrdinalIgnoreCase) && hints.Count < 3) hints.Add(hint);
                    textParts.Add(hint);
                    textParts.Add(EnglishText(info, "_des", string.Empty) ?? string.Empty);
                }
                endgame.Add(CreateTeamCandidate($"job:{jobId}", jobName, jobName, string.Join(" ", textParts), null, string.Join(" / ", hints.Take(2))));
            }
            if (endgame.Count < 3) return false;

            LogTeamSet("OWNED", FindBestTeams(owned));
            LogTeamSet("ENDGAME", FindBestTeams(endgame));
            return true;
        }
        catch (Exception error)
        {
            Plugin.DiagDebug($"Team recommendation report deferred: {error.Message}");
            return false;
        }
    }

    private static TeamCandidate CreateTeamCandidate(string key, string name, string job, string rawText, object? hero, string buildHint)
    {
        var text = Clean(rawText).ToLowerInvariant();
        var offense = 30d + KeywordScore(text, PhysicalWords.Concat(ElementalWords)) * 8d;
        var defense = 15d + KeywordScore(text, TankWords) * 12d;
        var support = 10d + KeywordScore(text, SupportWords) * 13d;
        var control = 8d + KeywordScore(text, new[] { "debuff", "weaken", "slow", "stun", "bleed", "corrosion", "ailment", "freeze", "armor break", "shatter" }) * 11d;
        var power = 40d;
        if (hero is not null)
        {
            offense += Math.Log10(1d + Math.Max(0d, ReadHeroAttr(hero, 1) + ReadHeroAttr(hero, 2))) * 14d;
            defense += Math.Log10(1d + Math.Max(0d, ReadHeroAttr(hero, 3) + ReadHeroAttr(hero, 4) + ReadHeroAttr(hero, 5))) * 12d;
            power = Math.Log10(1d + Math.Max(0d, ReadHeroAttr(hero, 1) + ReadHeroAttr(hero, 2) + ReadHeroAttr(hero, 3) + ReadHeroAttr(hero, 4) + ReadHeroAttr(hero, 5))) * 18d;
            power += (ReadNullableInt(Read(hero, "saveHeroData"), "level") ?? 0) * 0.35d + (ReadNullableInt(Read(hero, "saveHeroData"), "quality") ?? 0) * 4d;
        }
        var themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in new Dictionary<string, string[]>
        {
            ["fire"] = new[] { "fire", "burn" }, ["ice"] = new[] { "ice", "frost", "freeze" }, ["lightning"] = new[] { "lightning", "shock" },
            ["physical"] = new[] { "physical", "martial", "blunt", "slash", "pierce" }, ["crit"] = new[] { "crit", "critical" },
            ["bleed"] = new[] { "bleed" }, ["corrosion"] = new[] { "corrosion", "poison" }, ["summon"] = new[] { "summon", "minion" },
            ["aura"] = new[] { "aura", "ally", "buff" }, ["debuff"] = new[] { "debuff", "weaken", "ailment" }
        }) if (theme.Value.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))) themes.Add(theme.Key);
        return new TeamCandidate(key, name, job, offense, defense, support, control, power, themes, buildHint);
    }

    private static List<TeamSuggestion> FindBestTeams(List<TeamCandidate> candidates)
    {
        var results = new List<TeamSuggestion>();
        for (var a = 0; a < candidates.Count - 2; a++)
        for (var b = a + 1; b < candidates.Count - 1; b++)
        for (var c = b + 1; c < candidates.Count; c++)
        {
            var team = new[] { candidates[a], candidates[b], candidates[c] };
            var offense = team.Sum(hero => hero.Offense);
            var defense = team.Sum(hero => hero.Defense);
            var support = team.Sum(hero => hero.Support);
            var control = team.Sum(hero => hero.Control);
            var score = team.Sum(hero => hero.Power) * 0.65d + offense * 0.35d + defense * 0.30d + support * 0.36d + control * 0.18d;
            var reasons = new List<string>();
            if (team.Any(hero => hero.Defense >= 45d) && team.Any(hero => hero.Support >= 38d)) { score += 42d; reasons.Add("frontline+support"); }
            if (team.Any(hero => hero.Offense >= 65d) && team.Any(hero => hero.Support >= 38d)) { score += 28d; reasons.Add("carry+buff"); }
            var shared = team.SelectMany(hero => hero.Themes).GroupBy(theme => theme, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() >= 2).OrderByDescending(group => group.Count()).Select(group => group.Key).Take(2).ToList();
            score += shared.Count * 24d;
            if (shared.Count > 0) reasons.Add(string.Join("+", shared));
            if (team.Select(hero => hero.Job).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3) score += 12d;
            results.Add(new TeamSuggestion(team[0], team[1], team[2], score, reasons.Count == 0 ? "balanced roles" : string.Join(", ", reasons)));
        }
        return results.OrderByDescending(result => result.Score).Take(3).ToList();
    }

    [Conditional("POI_DEV_FEATURE")]
    private static void LogTeamSet(string scope, List<TeamSuggestion> teams)
    {
        for (var index = 0; index < teams.Count; index++)
        {
            var team = teams[index];
            Plugin.DiagInfo($"TEAM-{scope}|{index + 1}|{team.Score:0.0}|{team.A.Name} [{team.A.Job}] + {team.B.Name} [{team.B.Job}] + {team.C.Name} [{team.C.Job}]|{team.Reason}|{team.A.BuildHint} ; {team.B.BuildHint} ; {team.C.BuildHint}");
        }
    }

    public static bool TryOptimizeSelectedHeroGear(bool includeStorage, out string message)
        => TryOptimizeSelectedHeroGear(includeStorage, false, out _, out _, out message);

    private static bool TryOptimizeSelectedHeroGear(
        bool includeStorage,
        bool combinedFlow,
        out bool gearCommitted,
        out PendingGearCommit? pendingGearCommit,
        out string message)
    {
        gearCommitted = false;
        pendingGearCommit = null;
        var moveJournal = new List<MoveReceipt>();
        object? gearTalentData = null;
        try
        {
            var hero = GetSelectedHero();
            if (hero is null)
            {
                message = UiText.L("선택된 영웅이 없습니다.", "No hero is selected.", "未选择英雄。", "未選擇英雄。");
                return false;
            }
            if (ReadBool(InvokeRequiredInstance(hero, "IsAdventureBusy")))
            {
                message = UiText.L(
                    "모험 중에는 현재 전투 데이터와 장비 상태가 달라질 수 있어 자동 장착을 실행하지 않습니다. 모험을 끝낸 후 다시 실행하세요.",
                    "Auto Gear is disabled while the hero is adventuring because the live combat state can differ from saved equipment. Try again after the adventure ends.",
                    "英雄冒险时，实时战斗状态可能与已保存装备不一致，因此未执行自动装备。请在冒险结束后重试。",
                    "英雄冒險時，即時戰鬥狀態可能與已儲存裝備不一致，因此未執行自動裝備。請在冒險結束後重試。");
                return false;
            }
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData");
            var lordData = Read(seasonData, "lordData");
            if (lordData is null) throw new InvalidOperationException("Lord data is unavailable.");
            var focus = ResolveHeroFocus(hero, GetHeroBuildTheme(hero));
            var profile = BuildHeroEffectProfile(hero, focus);
            gearTalentData = Read(hero, "heroTalentData");
            var slots = GetGearSlots();
            var currentBySlot = slots.ToDictionary(slot => slot, slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon));
            var originalGearFingerprint = GetCurrentGearFingerprint(hero);
            // Capture non-gear ability counts before the first move. Post-equip
            // verification can then prove the exact count delta instead of a
            // same-ID class/skill ability masking a missing gear ability.
            var abilityBaseline = CaptureGearAbilityBaseline(hero,
                currentBySlot.Values.Where(item => item is not null).Cast<object>());
            var records = ReadAll(includeStorage);
            foreach (var current in currentBySlot.Values.Where(item => item is not null).DistinctBy(item => NativeObjectKey(item!, item!)))
                records.Add(DescribeItem(current!, UiText.L("현재 착용", "Equipped", "当前装备", "目前裝備"), StorageKind.Inventory, StorageSource.Equipped));

            var candidates = records.Select(record => CreateGearCandidate(record, hero, profile)).Where(candidate => candidate is not null).Cast<GearCandidate>().ToList();
            if (candidates.Count == 0)
            {
                message = UiText.L("착용 가능한 장비 후보가 없습니다.", "No wearable gear candidates were found.", "没有可穿戴的装备候选。", "沒有可穿戴的裝備候選。");
                return false;
            }

            // Search complete eight-slot loadouts (two weapon slots plus six other
            // parts). Keeping equipped pieces in the pool prevents a good existing
            // set from disappearing just because it is no longer in the bag.
            var maxMythic = Convert.ToInt32(
                InvokeRequiredStaticMany("HeroEquipData", "GetMaxMythEquipCount", hero)
                ?? throw new InvalidOperationException("The native Mythic equipment limit is unavailable."),
                CultureInfo.InvariantCulture);
            if (maxMythic is < 0 or > 8)
                throw new InvalidOperationException($"The native Mythic equipment limit is invalid ({maxMythic}).");
            Plugin.DiagInfo($"AUTO-GEAR MYTHIC LIMIT|heroLevel={ReadNullableInt(Read(hero, "saveHeroData"), "level") ?? 0}|limit={maxMythic}");
            var beam = new List<LoadoutState>
            {
                new(new List<GearCandidate>(), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 0d)
            };
            foreach (var slot in slots)
            {
                var current = currentBySlot[slot];
                var currentKey = current is null ? string.Empty : NativeObjectKey(current, current);
                var slotCandidates = candidates.Where(candidate => candidate.Part == slot.Part).ToList();
                var options = slotCandidates.OrderByDescending(candidate => candidate.Score).Take(16)
                    .Concat(slotCandidates.OrderByDescending(candidate => candidate.NumericScore).Take(10))
                    .Concat(slotCandidates.OrderByDescending(GetRawCandidatePower).Take(8))
                    .Concat(slotCandidates.Where(candidate => candidate.SetId > 0)
                        .GroupBy(candidate => candidate.SetId)
                        .SelectMany(group => group.OrderByDescending(candidate => candidate.Score).Take(3)))
                    .Concat(slotCandidates.Where(candidate => candidate.Record.Quality == 5)
                        .OrderByDescending(candidate => candidate.Score + candidate.NumericScore * 0.1d).Take(12))
                    .Concat(slotCandidates.Where(candidate => profile.RecommendedEquipmentIds.Contains(candidate.DefinitionId)))
                    .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                    .OrderByDescending(candidate => candidate.Score)
                    .Take(36).ToList();
                // High-value duplicate Uniques can otherwise fill the whole
                // per-slot shortlist. Keep one representative for every narrow
                // exclusive-effect policy signature (and one ordinary
                // alternative), so the beam can compare a different effect.
                foreach (var alternative in slotCandidates
                             .GroupBy(candidate => GetNonStackingEffectSignature(candidate.NonStackingEffectKeys.Where(IsHardExclusiveEffectKey)), StringComparer.Ordinal)
                             .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                             .OrderByDescending(candidate => candidate.Score))
                {
                    if (options.All(candidate => candidate.Key != alternative.Key)) options.Add(alternative);
                }
                // Native keeps the highest-level source when several equipment
                // affixes grant the same extra skill. Preserve a max-level
                // representative even when a lower-level, high-stat copy won
                // the ordinary shortlist.
                foreach (var alternative in slotCandidates
                             .SelectMany(candidate => GetGrantedExtraSkillLevels(candidate.Record.ItemData)
                                 .Select(entry => (Candidate: candidate, entry.Key, entry.Value)))
                             .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                             .Select(group => group.OrderByDescending(entry => entry.Value)
                                 .ThenByDescending(entry => entry.Candidate.Score).First().Candidate))
                {
                    if (options.All(candidate => candidate.Key != alternative.Key)) options.Add(alternative);
                }
                // A variant item can be worthless for the baseline skill yet be
                // the key to the best jointly selected skill package. Preserve a
                // representative for every exact native variant target ID.
                foreach (var alternative in slotCandidates
                             .SelectMany(candidate => GetItemSkillVariantIds(candidate.Record.ItemData)
                                 .Select(skillId => (Candidate: candidate, SkillId: skillId)))
                             .GroupBy(entry => entry.SkillId)
                             .Select(group => group.OrderByDescending(entry => GetRawCandidatePower(entry.Candidate))
                                 .ThenByDescending(entry => entry.Candidate.Score).First().Candidate))
                {
                    if (options.All(candidate => candidate.Key != alternative.Key)) options.Add(alternative);
                }
                // Conditional/special abilities receive a conservative zero when
                // their battle event cannot be simulated. Preserve one candidate
                // for every exact native ability ID so that a zero lower bound
                // does not prune the entire effect family before finalist review.
                foreach (var alternative in slotCandidates
                             .SelectMany(candidate => GetItemNativeAbilityIds(candidate.Record.ItemData)
                                 .Select(abilityId => (Candidate: candidate, AbilityId: abilityId)))
                             .GroupBy(entry => entry.AbilityId)
                             .Select(group => group.OrderByDescending(entry => GetRawCandidatePower(entry.Candidate))
                                 .ThenByDescending(entry => entry.Candidate.Score).First().Candidate))
                {
                    if (options.All(candidate => candidate.Key != alternative.Key)) options.Add(alternative);
                }
                if (current is not null && options.All(candidate => candidate.Key != currentKey))
                {
                    var currentCandidate = candidates.FirstOrDefault(candidate => candidate.Key == currentKey);
                    if (currentCandidate is not null) options.Add(currentCandidate);
                }

                var expandedBeam = beam.SelectMany(state => options
                        .Where(candidate => !state.UsedKeys.Contains(candidate.Key))
                        // Only the user's duplicate-Unique policy rejects a
                        // whole item. Boolean variants and highest-level extra
                        // skills are allowed here; their duplicated score is
                        // removed by ScoreItemsWithDeduplicatedEffects so valid
                        // numeric affixes and set pieces are not discarded.
                        .Where(candidate => !candidate.NonStackingEffectKeys
                            .Where(IsHardExclusiveEffectKey)
                            .Any(state.NonStackingEffectKeys.Contains))
                        .Where(candidate => state.Items.Count(item => item.Record.Quality == 5) + (candidate.Record.Quality == 5 ? 1 : 0) <= maxMythic)
                        .Where(candidate => !HasLegendMythWeaponConflict(state.Items.Append(candidate)))
                        .Select(candidate =>
                        {
                            var combinedItems = state.Items.Append(candidate).ToList();
                            return new LoadoutState(
                                combinedItems,
                                new HashSet<string>(state.UsedKeys, StringComparer.Ordinal) { candidate.Key },
                                UnionEffectKeys(state.NonStackingEffectKeys, candidate.NonStackingEffectKeys),
                                ScoreItemsWithDeduplicatedEffects(combinedItems, profile));
                        }));
                // Weapon requirements belong to the jointly selected skill
                // package, not the baseline plan. Pruning here would discard a
                // valid weapon + alternate-skill + Unique-variant combination
                // before finalist evaluation can compare it.
                beam = expandedBeam
                    .OrderByDescending(state => state.HeuristicScore + EstimatePartialSetSynergy(state.Items, profile))
                    .Take(360).ToList();
                if (beam.Count == 0) throw new InvalidOperationException($"No valid loadout remains for {slot.Label}.");
            }

            var currentItems = currentBySlot.Values.Where(item => item is not null).Cast<object>().ToList();
            string LoadoutDiversityKey(LoadoutState state)
            {
                var sets = string.Join(",", state.Items.Where(item => item.SetId > 0)
                    .GroupBy(item => item.SetId).OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}:{group.Count()}"));
                var granted = string.Join(",", state.Items
                    .SelectMany(item => GetGrantedExtraSkillLevels(item.Record.ItemData))
                    // Native GetSkillList keeps the highest level when several
                    // equipped affixes grant the same skill. Redundant lower
                    // copies therefore must not manufacture fake diversity and
                    // displace a real set/variant/weapon representative.
                    .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Key}:{group.Max(entry => entry.Value)}"));
                var variants = string.Join(",", state.Items
                    .SelectMany(item => GetItemSkillVariantIds(item.Record.ItemData))
                    .Distinct().OrderBy(id => id));
                var abilities = string.Join(",", state.Items
                    .SelectMany(item => GetItemNativeAbilityIds(item.Record.ItemData))
                    .Distinct().OrderBy(id => id));
                var weapons = string.Join(",", state.Items.Where(item => item.Part == 1)
                    .Select(item => item.WeaponType).OrderBy(id => id));
                return $"w={weapons}|s={sets}|g={granted}|v={variants}|a={abilities}";
            }

            var finalistStates = beam
                .OrderByDescending(state => state.HeuristicScore + EstimatePartialSetSynergy(state.Items, profile))
                // Joint skill/variant/set evaluation is intentionally delayed
                // until a complete 8-slot loadout exists. Retain the full beam so
                // an alternate-skill enabler is not discarded by the baseline
                // profile immediately before that exact comparison.
                .Take(360)
                .ToList();
            if (finalistStates.Count == 0)
            {
                message = UiText.L(
                    "현재 후보에서는 추천 스킬을 모두 사용할 수 있는 8부위 장비 조합을 찾지 못했습니다.",
                    "No evaluated 8-slot loadout can use every recommended skill with its selected weapons.",
                    "在已评估候选中找不到可用所选武器使用全部推荐技能的 8 部位装备组合。",
                    "在已評估候選中找不到可用所選武器使用全部推薦技能的 8 部位裝備組合。");
                return false;
            }
            // Native skill, mastery, trigger and summon previews are reflection-
            // heavy. Preserve both score leaders and one representative of each
            // structural set/variant/granted-skill/weapon family before invoking
            // that joint engine. This avoids freezing the Unity UI for minutes
            // while retaining the combinations that can materially change the
            // selected skill package.
            const int coarseLeaderCount = 12;
            const int coarseStateLimit = 24;
            var coarseCandidateStates = finalistStates.Take(coarseLeaderCount)
                .Concat(finalistStates.GroupBy(LoadoutDiversityKey, StringComparer.Ordinal)
                    .Select(group => group.First()))
                .DistinctBy(state => string.Join("|", state.Items.Select(item => item.Key)))
                .Take(coarseStateLimit)
                .ToList();
            var coarseEvaluations = coarseCandidateStates
                .Select(state => new
                {
                    State = state,
                    Evaluation = EvaluateCompleteLoadout(
                        state.Items, hero, profile, currentItems, false)
                })
                .ToList();
            var coarseFinalists = coarseEvaluations
                .Where(entry => entry.Evaluation.IsValid && double.IsFinite(entry.Evaluation.Score))
                .OrderByDescending(entry => entry.Evaluation.Score)
                .ToList();
            if (coarseFinalists.Count == 0)
            {
                message = UiText.L(
                    "장비·스킬 공동 예비 계산을 완료하지 못해 장비를 변경하지 않았습니다.",
                    "The joint gear/skill pre-evaluation could not be completed, so no gear was changed.",
                    "无法完成装备与技能联合预评估，因此未更换装备。",
                    "無法完成裝備與技能聯合預評估，因此未更換裝備。");
#if PATHOFIDLE_RUNTIME_TEST
                var runtimeFailure = coarseEvaluations.Select(entry => entry.Evaluation.Failure)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "native preview failed";
                message += $" [runtime: {runtimeFailure}]";
#endif
                return false;
            }

            const int exactLeaderCount = 2;
            const int exactStateLimit = 3;
            var exactCandidateStates = coarseFinalists.Take(exactLeaderCount)
                .Concat(coarseFinalists.GroupBy(entry => LoadoutDiversityKey(entry.State), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderByDescending(entry => entry.Evaluation.Score))
                .DistinctBy(entry => string.Join("|", entry.State.Items.Select(item => item.Key)))
                .Take(exactStateLimit)
                .Select(entry => entry.State)
                .ToList();
            // Rebuild native mastery choices for every retained structural
            // finalist, but use one deterministic exact-budget vector for this
            // screening pass. Running the point-by-point nonlinear allocator for
            // every loadout × skill-package pair was the dominant UI stall.
            var screenedFinalists = exactCandidateStates
                .Select(state => new
                {
                    State = state,
                    Evaluation = EvaluateCompleteLoadout(
                        state.Items, hero, profile, currentItems, true, false)
                })
                .ToList();
            var validScreenedFinalists = screenedFinalists
                .Where(entry => entry.Evaluation.IsValid && double.IsFinite(entry.Evaluation.Score))
                .OrderByDescending(entry => entry.Evaluation.Score)
                .ToList();
            if (validScreenedFinalists.Count == 0)
            {
                var failure = screenedFinalists.Select(entry => entry.Evaluation.Failure)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "native preview failed";
                message = UiText.L(
                    $"장비·스킬 공동 계산을 완료하지 못해 장비를 변경하지 않았습니다: {failure}",
                    $"The joint gear/skill evaluation could not be completed, so no gear was changed: {failure}",
                    $"无法完成装备与技能联合计算，因此未更换装备：{failure}",
                    $"無法完成裝備與技能聯合計算，因此未更換裝備：{failure}");
                return false;
            }

            // Fully optimize only the best screened package. If its nonlinear
            // path proves invalid, fall through to the next already-screened
            // structural finalist; successful paths therefore pay for one full
            // allocator while retaining deterministic failure recovery.
            var refinedFinalists = new List<(LoadoutState State, LoadoutEvaluation Evaluation)>();
            foreach (var screened in validScreenedFinalists)
            {
                var refined = EvaluateCompleteLoadout(
                    screened.State.Items,
                    hero,
                    screened.Evaluation.Profile,
                    currentItems,
                    true,
                    true);
                refinedFinalists.Add((screened.State, refined));
                if (refined.IsValid && double.IsFinite(refined.Score)) break;
            }
            var winnerIndex = refinedFinalists.FindIndex(entry =>
                entry.Evaluation.IsValid && double.IsFinite(entry.Evaluation.Score));
            if (winnerIndex < 0)
            {
                var failure = refinedFinalists.Select(entry => entry.Evaluation.Failure)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "native refinement failed";
                message = UiText.L(
                    $"최종 장비·스킬 포인트 계산을 완료하지 못해 장비를 변경하지 않았습니다: {failure}",
                    $"The final joint gear/skill point allocation could not be completed, so no gear was changed: {failure}",
                    $"无法完成最终装备与技能点联合分配，因此未更换装备：{failure}",
                    $"無法完成最終裝備與技能點聯合分配，因此未更換裝備：{failure}");
                return false;
            }
            var winner = refinedFinalists[winnerIndex];
            var winnerProfile = winner.Evaluation.Profile;

            var selectedMythics = winner.State.Items.Count(candidate => candidate.Record.Quality == 5);
            Plugin.DiagInfo($"AUTO-GEAR MYTHIC RESULT|limit={maxMythic}|selected={selectedMythics}|available={candidates.Count(candidate => candidate.Record.Quality == 5)}");
            foreach (var slot in slots)
            {
                var topMythics = candidates.Where(candidate => candidate.Part == slot.Part && candidate.Record.Quality == 5)
                    .OrderByDescending(candidate => candidate.Score + candidate.NumericScore * 0.1d)
                    .Take(3)
                    .Select(candidate => $"{candidate.Record.Name}:{candidate.Score:0.0}/{candidate.NumericScore:0.0}");
                var selected = winner.State.Items[slots.IndexOf(slot)];
                Plugin.DiagInfo($"AUTO-GEAR MYTHIC SLOT|slot={slot.Label}|selected={selected.Record.Name}:Q{selected.Record.Quality}|top={string.Join(',', topMythics)}");
            }

            Plugin.DiagInfo($"AUTO-GEAR PLAN|focus={focus.English}|score={winner.Evaluation.Score:0.0}|skills={winnerProfile.JointSkillReason}|" +
                                  string.Join(" ; ", slots.Select((slot, index) =>
                                  {
                                      var choice = winner.State.Items[index];
                                       return $"{slot.Label}={choice.Record.Name} Q{choice.Record.Quality} Lv{choice.Record.Level ?? 0} itemScore={choice.Score:0.0} numeric={choice.NumericScore:0.0} direct={choice.DirectMatches} theme={choice.ThemeMatches} set={choice.SetId} effectPolicy={GetNonStackingEffectSignature(choice.NonStackingEffectKeys)}";
                                  })));
            Plugin.DiagInfo($"AUTO-GEAR SET PLAN|focus={focus.English}|{DescribeSetPlan(winner.State.Items, winnerProfile)}");

            // Apply ordinary replacements first so an outgoing Mythic or a
            // conflicting weapon can be removed before a restricted item is
            // inserted. Failed moves are retried after the other slots settle.
            var pending = slots.Select((slot, index) => (Slot: slot, Candidate: winner.State.Items[index]))
                .Where(entry => !NativeEquals(GetEquippedItem(hero, entry.Slot.Part, entry.Slot.MainWeapon), entry.Candidate.Record.ItemData))
                .OrderBy(entry => entry.Candidate.Record.Quality == 5 ? 1 : 0)
                .ThenBy(entry => entry.Slot.Part == 1 ? 1 : 0)
                .ToList();
            var targetItems = winner.State.Items.Select(candidate => candidate.Record.ItemData).ToList();
            var treasureTargetItems = winner.State.Items.Where(candidate => candidate.Record.StorageSource == StorageSource.Treasure)
                .Select(candidate => candidate.Record.ItemData).ToList();
            pending = pending
                .OrderByDescending(entry => InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", entry.Candidate.Record.ItemData) is not null)
                .ThenBy(entry => entry.Candidate.Record.StorageSource == StorageSource.Treasure ? 1 : 0)
                .ToList();
            var lastFailures = new Dictionary<string, (int Code, string Reason)>(StringComparer.Ordinal);
            var stagedConflictingWeapon = false;
            if (pending.Any(entry => entry.Candidate.Record.StorageSource == StorageSource.Treasure)
                && InvokeStaticMany("ItemSys", "FindEmptyLordInventoryField") is null)
            {
                var bridgeRecord = ReadAll(false).FirstOrDefault(record => record.StorageSource == StorageSource.Inventory
                    && record.SourceField is not null && !targetItems.Any(target => NativeEquals(target, record.ItemData)));
                var bridgeFailure = "The bag is full and no non-target equipment can be staged for Vault access.";
                if (bridgeRecord?.SourceField is null
                    || !TryClearBridgeBagField(bridgeRecord.SourceField, bridgeRecord.ItemData, seasonData, moveJournal, out bridgeFailure))
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(bridgeFailure)
                        ? "The bag is full and no non-target equipment can be staged for Vault access."
                        : bridgeFailure);
                }
                Plugin.DiagInfo("AUTO-GEAR BRIDGE|staged one non-target bag equipment item to storage");
            }
            for (var pass = 0; pass < 3 && pending.Count > 0; pass++)
            {
                var retry = new List<(GearSlot Slot, GearCandidate Candidate)>();
                foreach (var entry in pending)
                {
                    if (TryEquipCandidate(hero, entry.Candidate, entry.Slot, seasonData, moveJournal, targetItems, treasureTargetItems, out var moveCode, out var failure))
                    {
                        lastFailures.Remove(entry.Slot.Label);
                        continue;
                    }
                    lastFailures[entry.Slot.Label] = (moveCode, failure);
                    retry.Add(entry);
                }
                if (retry.Count == pending.Count && pass > 0)
                {
                    var conflict = retry.FirstOrDefault(entry => entry.Slot.Part == 1
                        && lastFailures.TryGetValue(entry.Slot.Label, out var detail) && detail.Code == 7);
                    var stageFailure = string.Empty;
                    var oppositeSlot = conflict.Slot is null
                        ? null
                        : slots.FirstOrDefault(slot => slot.Part == 1 && slot.MainWeapon != conflict.Slot.MainWeapon);
                    if (!stagedConflictingWeapon && oppositeSlot is not null
                        && TryStageEquippedWeapon(hero, oppositeSlot, seasonData, moveJournal, out stageFailure))
                    {
                        stagedConflictingWeapon = true;
                        Plugin.DiagInfo($"AUTO-GEAR STAGE|slot={oppositeSlot.Label}|moved the opposite conflicting weapon to bag");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(stageFailure))
                            Plugin.DiagWarning($"AUTO-GEAR STAGE FAILED|reason={stageFailure}");
                        break;
                    }
                }
                pending = retry
                    .OrderByDescending(entry => InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", entry.Candidate.Record.ItemData) is not null)
                    .ThenBy(entry => entry.Candidate.Record.StorageSource == StorageSource.Treasure ? 1 : 0)
                    .ToList();
            }

            var attemptFailures = lastFailures.Select(entry => $"{entry.Key}: {entry.Value.Reason}").ToList();
            foreach (var entry in lastFailures)
                Plugin.DiagWarning($"AUTO-GEAR MOVE FAILED|slot={entry.Key}|code={entry.Value.Code}|reason={entry.Value.Reason}");

            var finalMatches = 0;
            var changed = 0;
            var unchanged = 0;
            var directMatches = 0;
            var themeMatches = 0;
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                var candidate = winner.State.Items[index];
                var destination = GetEquipDestinationField(hero, slot);
                if (destination is null
                    || !IsVerifiedHeroEquipField(hero, candidate.Record.ItemData, destination)) continue;
                var actual = Read(destination, "itemData");
                finalMatches++;
                if (NativeEquals(currentBySlot[slot], actual)) unchanged++;
                else
                {
                    changed++;
                    themeMatches += candidate.ThemeMatches;
                }
            }
            directMatches = CountEffectiveDirectMatches(winner.State.Items, winnerProfile);

            var failed = slots.Count - finalMatches;
            if (failed > 0)
            {
                var rollbackFailures = RollbackMoveJournal(moveJournal);
                if (gearTalentData is not null)
                {
                    try { InvokeRequiredInstance(gearTalentData, "ReapplySkillVariantsFromEquippedItems"); }
                    catch (Exception refreshError) { Plugin.DiagWarning($"AUTO-GEAR VARIANT REFRESH FAILED|{refreshError.GetBaseException().Message}"); }
                }
                var firstFailure = attemptFailures.FirstOrDefault() ?? UiText.L("일부 목표 슬롯이 최종 장비와 일치하지 않음", "some target slots did not match the final loadout", "部分目标栏位与最终装备不一致", "部分目標欄位與最終裝備不一致");
                message = UiText.L(
                    $"장비 적용 실패 · 확인 {finalMatches}/8 · 이전 장비 복구 {(rollbackFailures == 0 ? "완료" : $"실패 {rollbackFailures}건")} · {firstFailure}",
                    $"Gear apply failed · verified {finalMatches}/8 · previous loadout rollback {(rollbackFailures == 0 ? "complete" : $"failed for {rollbackFailures} move(s)")} · {firstFailure}",
                    $"装备应用失败 · 验证 {finalMatches}/8 · 旧装备回滚{(rollbackFailures == 0 ? "完成" : $"失败 {rollbackFailures} 项")} · {firstFailure}",
                    $"裝備套用失敗 · 驗證 {finalMatches}/8 · 舊裝備復原{(rollbackFailures == 0 ? "完成" : $"失敗 {rollbackFailures} 項")} · {firstFailure}");
                return false;
            }

            // Equipment affixes are the game's source of truth for skill
            // transformations. Verify them while the move journal can still
            // restore the previous loadout. Storage normalization can move
            // outgoing items again, so it deliberately runs only after this
            // rollback-capable postcondition.
            var variantRefreshFailed = false;
            var gearEffectVerificationFailed = false;
            var unusableSkillFailed = false;
            try
            {
                InvokeRequiredInstance(gearTalentData ?? throw new InvalidOperationException("Talent data is unavailable."), "ReapplySkillVariantsFromEquippedItems");
                var learnedPlannedSkillIds = winnerProfile.PlannedSkills.ActiveSkillIds
                    .Append(winnerProfile.PlannedSkills.BaseSkillId)
                    .Where(id => id > 0).ToHashSet();
                var variantVerification = VerifyEquippedSkillVariants(
                    hero, gearTalentData, "AUTO-GEAR", learnedPlannedSkillIds);
                variantRefreshFailed = !variantVerification.IsExact;
                gearEffectVerificationFailed = !VerifyCommittedGearEffects(hero, gearTalentData, abilityBaseline, "AUTO-GEAR").IsVerified;
                unusableSkillFailed = ReadBool(InvokeRequiredInstance(gearTalentData, "IsHasUnusableSkill"));
                if (unusableSkillFailed)
                    Plugin.DiagWarning("AUTO-GEAR SKILL USABILITY FAILED|at least one learned skill cannot be used by the committed weapons");
            }
            catch (Exception refreshError)
            {
                variantRefreshFailed = true;
                gearEffectVerificationFailed = true;
                Plugin.DiagWarning($"AUTO-GEAR VARIANT REFRESH FAILED|{refreshError.GetBaseException().Message}");
            }
            if (variantRefreshFailed || gearEffectVerificationFailed)
            {
                var rollbackFailures = RollbackMoveJournal(moveJournal);
                try
                {
                    InvokeRequiredInstance(gearTalentData ?? throw new InvalidOperationException("Talent data is unavailable."), "ReapplySkillVariantsFromEquippedItems");
                }
                catch (Exception refreshError)
                {
                    Plugin.DiagWarning($"AUTO-GEAR VARIANT ROLLBACK REFRESH FAILED|{refreshError.GetBaseException().Message}");
                }
                message = UiText.L(
                    $"장비 특수 효과 검증 실패 · 이전 장비 복구 {(rollbackFailures == 0 ? "완료" : $"실패 {rollbackFailures}건")}",
                    $"Gear special-effect verification failed · previous loadout rollback {(rollbackFailures == 0 ? "complete" : $"failed for {rollbackFailures} move(s)")}",
                    $"装备特殊效果验证失败 · 旧装备回滚{(rollbackFailures == 0 ? "完成" : $"失败 {rollbackFailures} 项")}",
                    $"裝備特殊效果驗證失敗 · 舊裝備復原{(rollbackFailures == 0 ? "完成" : $"失敗 {rollbackFailures} 項")}");
                return false;
            }

            // Standalone Auto Gear keeps its existing immediate commit behavior.
            // Combined Auto Build retains every reversible move until the skill
            // mutation and final joint verifier both succeed; only then may
            // storage normalization make the transaction irreversible.
            var storageNormalizationFailures = 0;
            if (combinedFlow)
            {
                pendingGearCommit = new PendingGearCommit(
                    hero,
                    GetJointSkillPlanHeroKey(hero),
                    originalGearFingerprint,
                    moveJournal,
                    seasonData,
                    targetItems);
            }
            else
            {
                storageNormalizationFailures = NormalizeCommittedStorage(moveJournal, seasonData, targetItems);
                moveJournal.Clear();
            }
            RememberJointSkillPlan(hero, winnerProfile);
            gearCommitted = true;
            var commitWarningKo = (storageNormalizationFailures, unusableSkillFailed) switch
            {
                (> 0, true) => " · 장비는 적용됐지만 보관 정리 실패 및 기존 학습 스킬 무기 불일치(스킬 자동 배분 실행)",
                (> 0, false) => " · 장비는 적용됐지만 보관 정리 일부 실패(수동 확인 필요)",
                (0, true) => " · 장비는 적용됐지만 기존 학습 스킬이 현재 무기와 불일치(스킬 자동 배분 실행)",
                _ => string.Empty
            };
            var commitWarningEn = (storageNormalizationFailures, unusableSkillFailed) switch
            {
                (> 0, true) => " · gear applied, but storage cleanup failed and old learned skills mismatch the weapons (run Auto Skills)",
                (> 0, false) => " · gear applied, but some storage cleanup failed (manual review needed)",
                (0, true) => " · gear applied, but old learned skills mismatch the weapons (run Auto Skills)",
                _ => string.Empty
            };
            var commitWarningZhCn = (storageNormalizationFailures, unusableSkillFailed) switch
            {
                (> 0, true) => " · 装备已应用，但存储整理失败且旧技能与武器不匹配（请运行自动技能）",
                (> 0, false) => " · 装备已应用，但部分存储整理失败（需要手动检查）",
                (0, true) => " · 装备已应用，但旧技能与武器不匹配（请运行自动技能）",
                _ => string.Empty
            };
            var commitWarningZhTw = (storageNormalizationFailures, unusableSkillFailed) switch
            {
                (> 0, true) => " · 裝備已套用，但儲存整理失敗且舊技能與武器不符（請執行自動技能）",
                (> 0, false) => " · 裝備已套用，但部分儲存整理失敗（需要手動檢查）",
                (0, true) => " · 裝備已套用，但舊技能與武器不符（請執行自動技能）",
                _ => string.Empty
            };

            var skillReason = winnerProfile.JointSkillReason;
            message = changed > 0
                ? UiText.L($"8부위 실제 장착 완료 · {focus.Localized} · 교체 {changed}개 · 계획 스킬 효과 일치 {directMatches} · 테마 키워드 일치 {themeMatches} · Mythic {selectedMythics}/{maxMythic} · 유지 {unchanged}개 · 공동 평가: {skillReason}{commitWarningKo}", $"All 8 slots equipped · {focus.English} · changed {changed} · planned-skill effect matches {directMatches} · theme-keyword matches {themeMatches} · Mythic {selectedMythics}/{maxMythic} · kept {unchanged} · joint evaluation: {skillReason}{commitWarningEn}", $"8部位已实际装备 · {focus.Localized} · 更换 {changed} · 计划技能效果匹配 {directMatches} · 主题关键词匹配 {themeMatches} · 神话 {selectedMythics}/{maxMythic} · 保留 {unchanged} · 联合评估：{skillReason}{commitWarningZhCn}", $"8部位已實際裝備 · {focus.Localized} · 更換 {changed} · 計畫技能效果相符 {directMatches} · 主題關鍵字符合 {themeMatches} · 神話 {selectedMythics}/{maxMythic} · 保留 {unchanged} · 聯合評估：{skillReason}{commitWarningZhTw}")
                : UiText.L($"평가한 후보 중 현재 8부위의 {focus.Localized} 추천 점수가 가장 높습니다. · 공동 평가: {skillReason}{commitWarningKo}", $"The current 8-slot loadout has the highest {focus.English} recommendation score among the evaluated candidates. · joint evaluation: {skillReason}{commitWarningEn}", $"在已评估候选中，当前 8 部位的 {focus.Localized} 推荐评分最高。 · 联合评估：{skillReason}{commitWarningZhCn}", $"在已評估候選中，目前 8 部位的 {focus.Localized} 推薦評分最高。 · 聯合評估：{skillReason}{commitWarningZhTw}");
            // Storage tidy-up and the old learned-skill compatibility check run
            // after the target gear is committed. A compatibility warning is
            // actionable by Auto Skills and may be expected during a theme
            // switch, but it must not be reported as a verified success.
            return storageNormalizationFailures == 0 && (!unusableSkillFailed || combinedFlow);
        }
        catch (Exception error)
        {
            var hadMoves = moveJournal.Count > 0;
            var rollbackFailures = RollbackMoveJournal(moveJournal);
            if (gearTalentData is not null)
            {
                try { InvokeRequiredInstance(gearTalentData, "ReapplySkillVariantsFromEquippedItems"); }
                catch (Exception refreshError) { Plugin.DiagWarning($"AUTO-GEAR VARIANT REFRESH FAILED|{refreshError.GetBaseException().Message}"); }
            }
            var rollbackNote = !hadMoves ? string.Empty : rollbackFailures == 0 ? " · previous loadout restored" : $" · rollback failed for {rollbackFailures} move(s)";
            message = UiText.L($"장비 자동 장착 실패: {error.GetBaseException().Message}{(!hadMoves ? string.Empty : rollbackFailures == 0 ? " · 이전 장비 복구 완료" : $" · 복구 실패 {rollbackFailures}건")}", $"Auto-equip failed: {error.GetBaseException().Message}{rollbackNote}", $"自动装备失败：{error.GetBaseException().Message}{(!hadMoves ? string.Empty : rollbackFailures == 0 ? " · 已恢复旧装备" : $" · 回滚失败 {rollbackFailures} 项")}", $"自動裝備失敗：{error.GetBaseException().Message}{(!hadMoves ? string.Empty : rollbackFailures == 0 ? " · 已復原舊裝備" : $" · 復原失敗 {rollbackFailures} 項")}");
            return false;
        }
    }

    public static bool TryOptimizeSelectedHeroBuild(bool includeStorage, out string message)
    {
        JointSkillRollbackSnapshot rollbackSnapshot;
        try
        {
            rollbackSnapshot = CaptureJointSkillRollbackSnapshot();
        }
        catch (Exception error)
        {
            message = UiText.L(
                $"공동 자동 빌드 시작 상태를 안전하게 저장하지 못했습니다. 아무것도 변경하지 않았습니다: {error.GetBaseException().Message}",
                $"The joint auto-build could not capture a safe starting snapshot. Nothing was changed: {error.GetBaseException().Message}",
                $"无法安全保存联合自动构筑的初始状态，未进行任何更改：{error.GetBaseException().Message}",
                $"無法安全保存聯合自動配置的初始狀態，未進行任何變更：{error.GetBaseException().Message}");
            return false;
        }

        var gearSucceeded = TryOptimizeSelectedHeroGear(
            includeStorage, true, out var gearCommitted, out var pendingGearCommit, out var gearMessage);
        if (!gearCommitted)
        {
            message = gearMessage;
            return false;
        }
        if (pendingGearCommit is null)
        {
            message = UiText.L(
                "공동 자동 빌드 장비 트랜잭션을 찾지 못했습니다. 안전을 위해 스킬을 변경하지 않았습니다.",
                "The joint auto-build gear transaction is unavailable. Skills were not changed for safety.",
                "找不到联合自动构筑的装备事务。为安全起见未更改技能。",
                "找不到聯合自動配置的裝備交易。為安全起見未變更技能。");
            return false;
        }

        if (!TryOptimizeSelectedHeroSkills(true, out var skillMessage))
        {
            var rollback = RollbackJointBuild(pendingGearCommit, rollbackSnapshot);
            message = UiText.L(
                $"공동 자동 빌드 스킬 단계 실패{DescribeJointRollback(rollback, 0)} · {skillMessage}",
                $"Joint auto-build skill step failed{DescribeJointRollback(rollback, 1)} · {skillMessage}",
                $"联合自动构筑的技能步骤失败{DescribeJointRollback(rollback, 2)} · {skillMessage}",
                $"聯合自動配置的技能步驟失敗{DescribeJointRollback(rollback, 3)} · {skillMessage}");
            return false;
        }

        bool jointVerified;
        string verification;
        try
        {
            jointVerified = VerifyRememberedJointBuild(out verification);
        }
        catch (Exception error)
        {
            jointVerified = false;
            verification = $"final joint verification could not complete: {error.GetBaseException().Message}";
        }
#if PATHOFIDLE_RUNTIME_TEST
        if (jointVerified && RuntimeForceJointRollback)
        {
            jointVerified = false;
            verification = "forced runtime rollback after successful joint verification";
        }
#endif
        if (!jointVerified)
        {
            var rollback = RollbackJointBuild(pendingGearCommit, rollbackSnapshot);
            message = UiText.L(
                $"공동 자동 빌드 검증 실패{DescribeJointRollback(rollback, 0)} · {verification}",
                $"Joint auto-build verification failed{DescribeJointRollback(rollback, 1)} · {verification}",
                $"联合自动构筑验证失败{DescribeJointRollback(rollback, 2)} · {verification}",
                $"聯合自動配置驗證失敗{DescribeJointRollback(rollback, 3)} · {verification}");
            return false;
        }

        if (!gearSucceeded)
        {
            var rollback = RollbackJointBuild(pendingGearCommit, rollbackSnapshot);
            message = UiText.L(
                $"공동 자동 빌드 장비 단계 경고로 적용을 취소했습니다{DescribeJointRollback(rollback, 0)} · {gearMessage}",
                $"The joint auto-build was cancelled after a gear-step warning{DescribeJointRollback(rollback, 1)} · {gearMessage}",
                $"联合自动构筑因装备步骤警告而取消{DescribeJointRollback(rollback, 2)} · {gearMessage}",
                $"聯合自動配置因裝備步驟警告而取消{DescribeJointRollback(rollback, 3)} · {gearMessage}");
            return false;
        }

        // The equipment journal remains reversible through both skill mutation
        // and the exact joint verifier. Storage tidy-up is the commit boundary.
        int storageNormalizationFailures;
        try
        {
            storageNormalizationFailures = NormalizeCommittedStorage(
                pendingGearCommit.MoveJournal,
                pendingGearCommit.SeasonData,
                pendingGearCommit.TargetItems);
        }
        catch (Exception error)
        {
            // Normalization is the irreversible commit boundary: one or more
            // staged items may already have reached their native destination.
            // Do not attempt a journal rollback or discard the receipts when
            // the cleanup itself throws. Report the already-applied build and
            // leave the journal intact for the rest of this call's diagnostics.
            var detail = error.GetBaseException().Message;
            Plugin.DiagWarning($"AUTO-BUILD STORAGE COMMIT FAILED|{detail}");
            message = UiText.L(
                $"장비+스킬 공동 계획은 검증·적용됐지만 보관 정리가 예기치 않게 중단됐습니다. 적용 상태는 유지되며 보관함을 수동으로 확인해야 합니다: {detail} · {skillMessage}",
                $"The joint gear + skill plan was verified and applied, but storage cleanup stopped unexpectedly. The applied build remains in place; manually review storage: {detail} · {skillMessage}",
                $"装备与技能联合方案已验证并应用，但存储整理意外中断。已应用的方案会保留，请手动检查存储：{detail} · {skillMessage}",
                $"裝備與技能聯合方案已驗證並套用，但儲存整理意外中斷。已套用的方案會保留，請手動檢查儲存：{detail} · {skillMessage}");
            return false;
        }
        pendingGearCommit.MoveJournal.Clear();
        if (storageNormalizationFailures > 0)
        {
            message = UiText.L(
                $"장비+스킬 공동 계획은 검증·적용됐지만 보관 정리 {storageNormalizationFailures}건이 실패했습니다. 수동 확인이 필요합니다 · {skillMessage}",
                $"The joint gear + skill plan was verified and applied, but {storageNormalizationFailures} storage cleanup operation(s) failed. Manual review is required · {skillMessage}",
                $"装备与技能联合方案已验证并应用，但有 {storageNormalizationFailures} 项存储整理失败，需要手动检查 · {skillMessage}",
                $"裝備與技能聯合方案已驗證並套用，但有 {storageNormalizationFailures} 項儲存整理失敗，需要手動檢查 · {skillMessage}");
            return false;
        }

        message = UiText.L(
            $"장비+스킬 공동 자동 빌드 검증 완료 · 동일한 공동 계획 적용 · {skillMessage}",
            $"Joint gear + skill auto-build verified · the same joint plan was applied · {skillMessage}",
            $"装备与技能联合自动构筑验证完成 · 已应用同一联合方案 · {skillMessage}",
            $"裝備與技能聯合自動配置驗證完成 · 已套用同一聯合方案 · {skillMessage}");
        return true;
    }

    private static JointSkillRollbackSnapshot CaptureJointSkillRollbackSnapshot()
    {
        var hero = GetSelectedHero()
                   ?? throw new InvalidOperationException("No hero is selected.");
        var saveHero = Read(hero, "saveHeroData")
                       ?? throw new InvalidOperationException("SaveHeroData is unavailable.");
        var talentData = Read(hero, "heroTalentData")
                         ?? throw new InvalidOperationException("HeroTalentData is unavailable.");
        var seasonData = Read(ReadStatic("Game", "dataMgr"), "nowSeasonData")
                         ?? throw new InvalidOperationException("SeasonData is unavailable.");
        var townData = Read(seasonData, "townData")
                       ?? throw new InvalidOperationException("TownData is unavailable.");
        var statData = Read(seasonData, "statData")
                       ?? throw new InvalidOperationException("StatData is unavailable.");
        var saveStatData = Read(statData, "saveStatData")
                           ?? throw new InvalidOperationException("SaveStatData is unavailable.");
        var bloodType = CreateEnum("EResType", 2)
                        ?? throw new InvalidOperationException("Blood resource type is unavailable.");
        var blood = Convert.ToInt32(
            InvokeRequiredInstance(townData, "GetRes", bloodType)
            ?? throw new InvalidOperationException("Blood amount is unavailable."),
            CultureInfo.InvariantCulture);
        var talents = CaptureSaveTalentRollbackStates(saveHero);
        var baseSkillId = ReadRequiredIntProperty(saveHero, "baseSkillId");
        if (baseSkillId <= 0)
            throw new InvalidOperationException(
                "The starting hero has no selected base skill, so an exact native rollback cannot be guaranteed.");
        var baseRow = talents.SingleOrDefault(state =>
            state.DictionaryKey == baseSkillId && state.Id == baseSkillId);
        var baseDefinition = InvokeStatic("TableData", "getTTalentData", baseSkillId);
        if (baseRow is null || baseRow.Level <= 0
            || baseDefinition is null || !IsBaseSkillDefinition(baseDefinition)
            || (ReadNullableInt(baseDefinition, "skillId") ?? 0) <= 0)
            throw new InvalidOperationException(
                $"The starting base-skill row {baseSkillId} is not present as an active native talent row.");
        var likeTalentIds = ReadTalentLikesRequired(townData);
        return new JointSkillRollbackSnapshot(
            hero,
            GetJointSkillPlanHeroKey(hero),
            saveHero,
            talentData,
            townData,
            saveStatData,
            bloodType,
            blood,
            baseSkillId,
            ReadRequiredIntProperty(saveHero, "talentRemainPoint"),
            ReadRequiredIntProperty(saveHero, "talentStickPoint"),
            ReadRequiredIntProperty(saveHero, "blessTalentPoint"),
            ReadRequiredIntProperty(saveStatData, "changeBaseSkillCount"),
            ReadRequiredIntProperty(saveStatData, "washHeroCount"),
            ReadRequiredIntProperty(saveStatData, "learnNewSkillCount"),
            likeTalentIds,
            talents);
    }

    private static List<SaveTalentRollbackState> CaptureSaveTalentRollbackStates(object saveHero)
    {
        var talentDictionary = ReadRequiredProperty(saveHero, "talentDic")
                               ?? throw new InvalidOperationException("SaveHeroData.talentDic is unavailable.");
        var result = new List<SaveTalentRollbackState>();
        foreach (var entry in ReadEntries(talentDictionary))
        {
            var dictionaryKey = ReadRequiredIntProperty(entry, "Key");
            var saveTalent = ReadRequiredProperty(entry, "Value")
                             ?? throw new InvalidOperationException($"SaveTalentData for key {dictionaryKey} is unavailable.");
            result.Add(CaptureSaveTalentRollbackState(dictionaryKey, saveTalent));
        }
        if (result.Count == 0)
            throw new InvalidOperationException("SaveHeroData.talentDic is empty.");
        if (result.Select(entry => entry.DictionaryKey).Distinct().Count() != result.Count)
            throw new InvalidOperationException("SaveHeroData.talentDic contains duplicate keys.");
        for (var index = 0; index < result.Count; index++)
        for (var other = index + 1; other < result.Count; other++)
        {
            if (NativeEquals(result[index].SaveTalent, result[other].SaveTalent))
                throw new InvalidOperationException(
                    $"SaveHeroData.talentDic binds the same SaveTalentData object to keys {result[index].DictionaryKey} and {result[other].DictionaryKey}.");
        }
        return result.OrderBy(entry => entry.DictionaryKey).ToList();
    }

    private static SaveTalentRollbackState CaptureSaveTalentRollbackState(int dictionaryKey, object saveTalent)
        => new(
            dictionaryKey,
            saveTalent,
            ReadRequiredIntProperty(saveTalent, "id"),
            ReadRequiredIntProperty(saveTalent, "level"),
            ReadRequiredIntProperty(saveTalent, "posId"),
            ReadRequiredBoolProperty(saveTalent, "isFixed"),
            ReadRequiredBoolProperty(saveTalent, "isInspired"),
            ReadRequiredBoolProperty(saveTalent, "isInspiredLocked"),
            ReadRequiredIntProperty(saveTalent, "inspireBaseLevel"),
            ReadRequiredIntProperty(saveTalent, "inspireRow"),
            ReadRequiredIntProperty(saveTalent, "inspireCol"),
            ReadRequiredIntProperty(saveTalent, "inspireMaxLevel"),
            ReadRequiredBoolProperty(saveTalent, "isAlien"));

    private static JointBuildRollbackResult RollbackJointBuild(
        PendingGearCommit pendingGearCommit,
        JointSkillRollbackSnapshot snapshot)
    {
        var failures = new List<string>();
        var gearMoveFailures = RollbackMoveJournal(pendingGearCommit.MoveJournal);
        if (gearMoveFailures > 0)
            failures.Add($"equipment reverse moves failed {gearMoveFailures}");

        var gearFingerprintRestored = false;
        try
        {
            gearFingerprintRestored = string.Equals(
                GetCurrentGearFingerprint(pendingGearCommit.Hero),
                pendingGearCommit.OriginalGearFingerprint,
                StringComparison.Ordinal);
            if (!gearFingerprintRestored)
                failures.Add("original equipment fingerprint was not restored");
        }
        catch (Exception error)
        {
            failures.Add($"equipment rollback verification failed: {error.GetBaseException().Message}");
        }

        var skillStateRestored = false;
        var progressStateRestored = false;
        var bloodRestored = false;
        var selectedHero = GetSelectedHero();
        var sameHero = selectedHero is not null
                       && string.Equals(GetJointSkillPlanHeroKey(selectedHero), snapshot.HeroKey, StringComparison.Ordinal)
                       && NativeEquals(selectedHero, snapshot.Hero)
                       && NativeEquals(Read(selectedHero, "saveHeroData"), snapshot.SaveHero)
                       && NativeEquals(Read(selectedHero, "heroTalentData"), snapshot.TalentData)
                       && string.Equals(pendingGearCommit.HeroKey, snapshot.HeroKey, StringComparison.Ordinal);
        if (!sameHero)
        {
            failures.Add("selected hero changed before the skill rollback");
        }
        else
        {
            try
            {
                var saveHero = Read(selectedHero, "saveHeroData")
                               ?? throw new InvalidOperationException("SaveHeroData is unavailable during rollback.");
                var talentData = Read(selectedHero, "heroTalentData")
                                 ?? throw new InvalidOperationException("HeroTalentData is unavailable during rollback.");
                RestoreSaveTalentRollbackStates(saveHero, snapshot.Talents);
                Write(saveHero, "baseSkillId", snapshot.BaseSkillId);
                Write(saveHero, "talentRemainPoint", snapshot.TalentRemainPoint);
                Write(saveHero, "talentStickPoint", snapshot.TalentStickPoint);
                Write(saveHero, "blessTalentPoint", snapshot.BlessTalentPoint);

                // Rehydrate the live TalentData graph from the exact restored
                // save rows. ReCreateTalent is intentionally forbidden here:
                // the native method rerolls replaceable shrine rows instead of
                // merely rebuilding their runtime wrappers.
                // Native CreateTalentDic starts by clearing the old talent
                // effects and live dictionary itself. Calling either operation
                // ahead of it would risk subtracting an effect twice.
                InvokeRequiredInstance(talentData, "CreateTalentDic");
                // FloorData caches references to TalentData nodes, so it must
                // follow the dictionary rebuild before effects are reapplied.
                InvokeRequiredInstance(talentData, "CreateTalentFloorDic");
                // CreateTalentDic restores the chosen SkillData flag, but the
                // native base-skill setter also refreshes the hero's derived
                // base-attack attribute. Its statistic increment is restored
                // from the snapshot below before Stele progress is rebuilt.
                InvokeRequiredInstance(talentData, "ChangeBaseSkill", snapshot.BaseSkillId);
                InvokeRequiredInstance(talentData, "ReapplySkillVariantsFromEquippedItems");
                VerifyJointSkillRollbackState(selectedHero!, talentData, saveHero, snapshot);
                skillStateRestored = true;
            }
            catch (Exception error)
            {
                failures.Add($"skill rollback failed: {error.GetBaseException().Message}");
            }

        }

        // Base-skill selection, shrine washing, and first-time skill learning
        // each update a persistent SaveStatData counter. They also recalculate
        // Stele claimability from those counters. Restore the counters and the
        // temporary Codex preference list, then run that same native derivation
        // once so the live Stele UI cannot retain rolled-back progress.
        try
        {
            var currentSeason = Read(ReadStatic("Game", "dataMgr"), "nowSeasonData")
                                ?? throw new InvalidOperationException("SeasonData is unavailable during progress rollback.");
            var currentTownData = Read(currentSeason, "townData")
                                  ?? throw new InvalidOperationException("TownData is unavailable during progress rollback.");
            if (!NativeEquals(currentTownData, snapshot.TownData))
                throw new InvalidOperationException("the active TownData changed before progress rollback");
            var currentStatData = Read(currentSeason, "statData")
                                  ?? throw new InvalidOperationException("StatData is unavailable during progress rollback.");
            var currentSaveStatData = Read(currentStatData, "saveStatData")
                                      ?? throw new InvalidOperationException("SaveStatData is unavailable during progress rollback.");
            if (!NativeEquals(currentSaveStatData, snapshot.SaveStatData))
                throw new InvalidOperationException("the active SaveStatData changed before progress rollback");

            RestoreTalentLikes(snapshot.TownData, snapshot.LikeTalentIds);
            Write(currentSaveStatData, "changeBaseSkillCount", snapshot.ChangeBaseSkillCount);
            Write(currentSaveStatData, "washHeroCount", snapshot.WashHeroCount);
            Write(currentSaveStatData, "learnNewSkillCount", snapshot.LearnNewSkillCount);
            InvokeRequiredInstance(snapshot.TownData, "UpdateSteleProgress");
            VerifyJointProgressRollbackState(currentSaveStatData, snapshot);
            progressStateRestored = true;
        }
        catch (Exception error)
        {
            failures.Add($"progress rollback failed: {error.GetBaseException().Message}");
        }

        // Blood is season-global, so refund it even if the selected hero was
        // changed unexpectedly and the hero-specific rollback could not run.
        try
        {
            var bloodNow = Convert.ToInt32(
                InvokeRequiredInstance(snapshot.TownData, "GetRes", snapshot.BloodType)
                ?? throw new InvalidOperationException("Blood amount is unavailable during rollback."),
                CultureInfo.InvariantCulture);
            var consumedByThisRun = Math.Max(0, snapshot.Blood - bloodNow);
            if (consumedByThisRun > 0)
                InvokeRequiredInstance(snapshot.TownData, "AddRes", snapshot.BloodType, consumedByThisRun);
            var bloodAfter = Convert.ToInt32(
                InvokeRequiredInstance(snapshot.TownData, "GetRes", snapshot.BloodType)
                ?? throw new InvalidOperationException("Blood amount is unavailable after refund."),
                CultureInfo.InvariantCulture);
            if (bloodAfter != snapshot.Blood)
                throw new InvalidOperationException($"Blood mismatch after refund ({bloodAfter}/{snapshot.Blood}).");
            bloodRestored = true;
        }
        catch (Exception error)
        {
            failures.Add($"Blood refund failed: {error.GetBaseException().Message}");
        }

        JointSkillPlansByHero.Remove(snapshot.HeroKey);
        return new JointBuildRollbackResult(
            gearMoveFailures,
            gearFingerprintRestored,
            skillStateRestored,
            progressStateRestored,
            bloodRestored,
            failures);
    }

    private static void RestoreSaveTalentRollbackStates(
        object saveHero,
        IReadOnlyCollection<SaveTalentRollbackState> expected)
    {
        var talentDictionary = ReadRequiredProperty(saveHero, "talentDic")
                               ?? throw new InvalidOperationException("SaveHeroData.talentDic is unavailable during rollback.");
        if (expected.Count == 0)
            throw new InvalidOperationException("the starting talent dictionary snapshot is empty");
        if (expected.Select(state => state.DictionaryKey).Distinct().Count() != expected.Count)
            throw new InvalidOperationException("the starting talent dictionary snapshot contains duplicate keys");
        var expectedList = expected.ToList();
        for (var index = 0; index < expectedList.Count; index++)
        for (var other = index + 1; other < expectedList.Count; other++)
        {
            if (NativeEquals(expectedList[index].SaveTalent, expectedList[other].SaveTalent))
                throw new InvalidOperationException(
                    $"the starting talent dictionary snapshot binds one SaveTalentData object to keys {expectedList[index].DictionaryKey} and {expectedList[other].DictionaryKey}");
        }

        // Shrine washing may replace both the keys and the SaveTalentData
        // values in the save dictionary. Restore the original objects while
        // the snapshot still owns strong references to them, then rebuild the
        // exact key/value topology before the native runtime graph is created.
        foreach (var state in expectedList)
        {
            var saveTalent = state.SaveTalent;
            Write(saveTalent, "id", state.Id);
            Write(saveTalent, "level", state.Level);
            Write(saveTalent, "posId", state.PosId);
            Write(saveTalent, "isFixed", state.IsFixed);
            Write(saveTalent, "isInspired", state.IsInspired);
            Write(saveTalent, "isInspiredLocked", state.IsInspiredLocked);
            Write(saveTalent, "inspireBaseLevel", state.InspireBaseLevel);
            Write(saveTalent, "inspireRow", state.InspireRow);
            Write(saveTalent, "inspireCol", state.InspireCol);
            Write(saveTalent, "inspireMaxLevel", state.InspireMaxLevel);
            Write(saveTalent, "isAlien", state.IsAlien);
        }

        InvokeRequiredInstance(talentDictionary, "Clear");
        foreach (var state in expectedList)
            InvokeRequiredInstance(talentDictionary, "Add", state.DictionaryKey, state.SaveTalent);

        VerifySaveTalentRollbackStates(
            CaptureSaveTalentRollbackStates(saveHero),
            expectedList,
            "restored saved talent dictionary");
    }

    private static bool SaveTalentRollbackValuesEqual(
        SaveTalentRollbackState actual,
        SaveTalentRollbackState expected)
        => actual.DictionaryKey == expected.DictionaryKey
           && actual.Id == expected.Id
           && actual.Level == expected.Level
           && actual.PosId == expected.PosId
           && actual.IsFixed == expected.IsFixed
           && actual.IsInspired == expected.IsInspired
           && actual.IsInspiredLocked == expected.IsInspiredLocked
           && actual.InspireBaseLevel == expected.InspireBaseLevel
           && actual.InspireRow == expected.InspireRow
           && actual.InspireCol == expected.InspireCol
           && actual.InspireMaxLevel == expected.InspireMaxLevel
           && actual.IsAlien == expected.IsAlien;

    private static void VerifySaveTalentRollbackStates(
        IReadOnlyCollection<SaveTalentRollbackState> actual,
        IReadOnlyCollection<SaveTalentRollbackState> expected,
        string scope)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException(
                $"{scope} entry count differs from the starting snapshot ({actual.Count}/{expected.Count})");

        var actualByKey = actual.ToDictionary(state => state.DictionaryKey);
        foreach (var expectedState in expected)
        {
            if (!actualByKey.TryGetValue(expectedState.DictionaryKey, out var actualState))
                throw new InvalidOperationException(
                    $"{scope} is missing starting key {expectedState.DictionaryKey}");
            if (!NativeEquals(actualState.SaveTalent, expectedState.SaveTalent))
                throw new InvalidOperationException(
                    $"{scope} key {expectedState.DictionaryKey} is not bound to its original SaveTalentData object");
            if (!SaveTalentRollbackValuesEqual(actualState, expectedState))
                throw new InvalidOperationException(
                    $"{scope} fields differ for starting key {expectedState.DictionaryKey}");
        }
    }

    private static void VerifyJointSkillRollbackState(
        object hero,
        object talentData,
        object saveHero,
        JointSkillRollbackSnapshot expected)
    {
        var actualTalents = CaptureSaveTalentRollbackStates(saveHero);
        VerifySaveTalentRollbackStates(actualTalents, expected.Talents, "saved talent dictionary");
        if (ReadRequiredIntProperty(saveHero, "baseSkillId") != expected.BaseSkillId
            || ReadRequiredIntProperty(saveHero, "talentRemainPoint") != expected.TalentRemainPoint
            || ReadRequiredIntProperty(saveHero, "talentStickPoint") != expected.TalentStickPoint
            || ReadRequiredIntProperty(saveHero, "blessTalentPoint") != expected.BlessTalentPoint)
            throw new InvalidOperationException("saved base-skill or talent-point fields do not match the starting snapshot");

        var expectedBaseDefinition = expected.BaseSkillId > 0
            ? InvokeStatic("TableData", "getTTalentData", expected.BaseSkillId)
            : null;
        var expectedBaseSkillId = ReadNullableInt(expectedBaseDefinition, "skillId") ?? 0;
        if (expected.BaseSkillId > 0 && expectedBaseSkillId <= 0)
            throw new InvalidOperationException($"starting base talent {expected.BaseSkillId} no longer resolves to a skill");
        var liveBaseSkill = InvokeRequiredInstance(hero, "GetNowBaseSkillData");
        var liveBaseSkillId = ReadNullableInt(Read(liveBaseSkill, "tSkillData"), "id") ?? 0;
        if (liveBaseSkillId != expectedBaseSkillId)
            throw new InvalidOperationException(
                $"live base skill does not match the starting snapshot ({liveBaseSkillId}/{expectedBaseSkillId})");

        var runtimeDictionary = ReadRequiredProperty(talentData, "talentDic")
                                ?? throw new InvalidOperationException("HeroTalentData.talentDic is unavailable after rollback.");
        var runtimeByKey = ReadEntries(runtimeDictionary).ToDictionary(
            entry => ReadRequiredIntProperty(entry, "Key"),
            entry => ReadRequiredProperty(entry, "Value")
                     ?? throw new InvalidOperationException("TalentData is unavailable after rollback."));
        if (runtimeByKey.Count != expected.Talents.Count
            || expected.Talents.Any(state => !runtimeByKey.ContainsKey(state.DictionaryKey)))
            throw new InvalidOperationException("live talent dictionary keys do not match the starting snapshot");

        var restoredSkillIds = new HashSet<int>();
        foreach (var state in expected.Talents)
        {
            var runtimeTalent = runtimeByKey[state.DictionaryKey];
            var runtimeSave = ReadRequiredProperty(runtimeTalent, "saveTalentData")
                              ?? throw new InvalidOperationException(
                                  $"live talent {state.DictionaryKey} has no SaveTalentData after rollback");
            if (!NativeEquals(runtimeSave, state.SaveTalent))
                throw new InvalidOperationException(
                    $"live talent {state.DictionaryKey} is not bound to its original SaveTalentData object");
            var runtimeState = CaptureSaveTalentRollbackState(state.DictionaryKey, runtimeSave);
            if (!SaveTalentRollbackValuesEqual(runtimeState, state))
                throw new InvalidOperationException(
                    $"live talent {state.DictionaryKey} is not bound to the restored SaveTalentData fields");

            var definition = ReadRequiredProperty(runtimeTalent, "tTalentData")
                             ?? throw new InvalidOperationException(
                                 $"live talent {state.DictionaryKey} has no talent definition after rollback");
            var definitionId = ReadRequiredIntProperty(definition, "id");
            if (definitionId != state.Id)
                throw new InvalidOperationException(
                    $"live talent definition differs after rollback ({definitionId}/{state.Id})");

            var savedLevel = GetSavedTalentLevel(runtimeTalent);
            if (savedLevel != Math.Max(0, state.Level))
                throw new InvalidOperationException(
                    $"live saved talent level differs after rollback for {state.Id} ({savedLevel}/{state.Level})");
            var cap = GetTalentLevelCap(runtimeTalent);
            var expectedEffective = Math.Min(cap,
                checked(GetTalentBaseLevelRequired(runtimeTalent) + savedLevel));
            var effectiveLevel = GetTalentLevel(runtimeTalent);
            if (effectiveLevel != expectedEffective)
                throw new InvalidOperationException(
                    $"live effective talent level differs after rollback for {state.Id} ({effectiveLevel}/{expectedEffective})");

            var skillId = ReadNullableInt(definition, "skillId") ?? 0;
            if (skillId > 0 && (savedLevel > 0 || state.Id == expected.BaseSkillId))
                restoredSkillIds.Add(skillId);
        }

        foreach (var grantedSkillId in GetCurrentGrantedSkillLevels(hero).Keys)
            restoredSkillIds.Add(grantedSkillId);
        var variants = VerifyEquippedSkillVariants(
            hero, talentData, "AUTO-BUILD ROLLBACK", restoredSkillIds);
        if (!variants.IsExact)
            throw new InvalidOperationException(
                $"equipped skill variants differ after rollback (-{string.Join(',', variants.Missing)} +{string.Join(',', variants.Unexpected)})");
    }

    private static void VerifyJointProgressRollbackState(
        object saveStatData,
        JointSkillRollbackSnapshot expected)
    {
        if (ReadRequiredIntProperty(saveStatData, "changeBaseSkillCount") != expected.ChangeBaseSkillCount
            || ReadRequiredIntProperty(saveStatData, "washHeroCount") != expected.WashHeroCount
            || ReadRequiredIntProperty(saveStatData, "learnNewSkillCount") != expected.LearnNewSkillCount)
            throw new InvalidOperationException(
                "saved base-skill, shrine-wash, or learned-skill progress counters do not match the starting snapshot");

        var actualLikes = ReadTalentLikesRequired(expected.TownData);
        if (!actualLikes.SequenceEqual(expected.LikeTalentIds))
            throw new InvalidOperationException(
                $"saved talent preference order differs after rollback (expected {string.Join(',', expected.LikeTalentIds)}, got {string.Join(',', actualLikes)})");
    }

    private static string DescribeJointRollback(JointBuildRollbackResult rollback, int language)
    {
        if (rollback.IsExact)
        {
            return language switch
            {
                0 => " · 변경 전 장비·스킬·피·진행도·스킬 선호 복구 검증 완료",
                1 => " · original gear, skills, Blood, progress, and skill preferences restored and verified",
                2 => " · 已恢复并验证原装备、技能、鲜血、进度与技能偏好",
                _ => " · 已復原並驗證原裝備、技能、鮮血、進度與技能偏好"
            };
        }

        var detail = rollback.Failures.Count == 0
            ? "unknown rollback mismatch"
            : string.Join("; ", rollback.Failures);
        return language switch
        {
            0 => $" · 복구 실패: {detail}",
            1 => $" · rollback failed: {detail}",
            2 => $" · 回滚失败：{detail}",
            _ => $" · 復原失敗：{detail}"
        };
    }

    private static bool VerifyRememberedJointBuild(out string failure)
    {
        var hero = GetSelectedHero();
        if (hero is null)
        {
            failure = "selected hero is unavailable";
            return false;
        }
        var key = GetJointSkillPlanHeroKey(hero);
        if (!JointSkillPlansByHero.TryGetValue(key, out var remembered))
        {
            failure = "the remembered joint plan is unavailable";
            return false;
        }
        if (string.IsNullOrWhiteSpace(remembered.PlanToken))
        {
            failure = "the remembered joint plan has an empty plan token";
            return false;
        }
        if (!string.Equals(remembered.GearFingerprint, GetCurrentGearFingerprint(hero), StringComparison.Ordinal))
        {
            failure = "the equipped items no longer match the remembered joint plan";
            return false;
        }

        var talentData = Read(hero, "heroTalentData");
        var saveHero = Read(hero, "saveHeroData");
        if (talentData is null || saveHero is null)
        {
            failure = "hero talent data is unavailable";
            return false;
        }
        var talents = ReadValues(Read(talentData, "talentDic"))
            .DistinctBy(value => NativeObjectKey(value, value)).ToList();
        var jobId = ReadNullableInt(saveHero, "jobId") ?? 0;
        var jobRows = ReadValues(ReadStatic("TableData", "TTalentDict"))
            .Where(row => (ReadNullableInt(row, "jobId") ?? 0) == jobId).ToList();
        var expectedActive = remembered.ActiveSkillIds
            .Where(skillId => jobRows.Any(row => IsTransformableSkillDefinition(row)
                                                  && (ReadNullableInt(row, "skillId") ?? 0) == skillId))
            .ToHashSet();
        var learnedActive = talents
            .Where(talent => IsTransformableSkillDefinition(Read(talent, "tTalentData")))
            .Where(talent => GetSavedTalentLevel(talent) > 0)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
        if (!learnedActive.SetEquals(expectedActive))
        {
            failure = $"active skills differ ({string.Join(',', learnedActive.OrderBy(id => id))}/{string.Join(',', expectedActive.OrderBy(id => id))})";
            return false;
        }

        var actualBase = InvokeRequiredInstance(hero, "GetNowBaseSkillData");
        var actualBaseId = ReadNullableInt(Read(actualBase, "tSkillData"), "id") ?? 0;
        if (actualBaseId != remembered.BaseSkillId)
        {
            failure = $"base skill differs ({actualBaseId}/{remembered.BaseSkillId})";
            return false;
        }
        // Native GetSkillList enumerates learned active and equipment-granted
        // skills, but the chosen base attack is exposed separately through
        // GetNowBaseSkillData. Recheck its weapon contract here instead of
        // incorrectly requiring it to appear in GetSkillList.
        if (!IsSkillCompatibleWithEquippedWeapons(hero, remembered.BaseSkillId))
        {
            failure = $"base skill {remembered.BaseSkillId} is incompatible with the committed weapons";
            return false;
        }
        var usableSkillRows = ReadList(InvokeRequiredInstance(talentData, "GetSkillList"));
        var usableSkills = usableSkillRows
            .Select(skill => ReadNullableInt(Read(skill, "tSkillData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var currentGrantedSkillLevels = GetCurrentGrantedSkillLevels(hero);
        if (currentGrantedSkillLevels.Count != remembered.GrantedSkillLevels.Count
            || remembered.GrantedSkillLevels.Any(entry =>
                currentGrantedSkillLevels.GetValueOrDefault(entry.Key) != entry.Value))
        {
            failure = "equipment-granted skills differ from the remembered combat package";
            return false;
        }
        var expectedUsableSkills = remembered.ActiveSkillIds
            .Concat(remembered.GrantedSkillLevels.Keys)
            .Where(id => id > 0).ToHashSet();
        var expectedPackageSkills = expectedUsableSkills
            .Append(remembered.BaseSkillId).Where(id => id > 0).ToHashSet();
        var missingSkills = expectedUsableSkills
            .Where(id => id > 0 && !usableSkills.Contains(id)).Distinct().OrderBy(id => id).ToList();
        var underleveledGrantedSkills = remembered.GrantedSkillLevels.Where(entry =>
            usableSkillRows.Where(skill =>
                    (ReadNullableInt(Read(skill, "tSkillData"), "id") ?? 0) == entry.Key)
                .Select(skill => ReadNullableInt(skill, "level") ?? 0)
                .DefaultIfEmpty(0).Max() < entry.Value)
            .Select(entry => $"{entry.Key}:{entry.Value}").OrderBy(value => value).ToList();
        if (missingSkills.Count > 0 || underleveledGrantedSkills.Count > 0
                                    || ReadBool(InvokeRequiredInstance(talentData, "IsHasUnusableSkill")))
        {
            failure = $"planned skills are unavailable with the committed gear (missing {string.Join(',', missingSkills)}; granted levels {string.Join(',', underleveledGrantedSkills)})";
            return false;
        }

        var talentById = BuildTalentGridById(talents);
        var resolvedTargets = new Dictionary<int, (object Talent, int Target)>();
        var levelFailures = new List<string>();

        // TargetSavedLevels is the exact materialized save vector. Native
        // ChangeBaseSkill contributes one saved level without reducing
        // talentRemainPoint, while TotalTalentPointBudget contains only normal
        // paid points. Keep that distinction explicit so a 54-point plan with a
        // free base level is verified as 55 saved levels, not mistaken for an
        // over-allocation (or silently accepted as a 53-point plan).
        var rememberedBaseTargets = remembered.TargetSavedLevels
            .Where(entry => IsBaseSkillDefinition(InvokeStatic("TableData", "getTTalentData", entry.Key)))
            .ToList();
        if (rememberedBaseTargets.Count != 1 || rememberedBaseTargets[0].Value < 1)
        {
            failure = "the remembered joint plan has no single materialized base-skill level";
            return false;
        }
        var materializedTargetTotal = remembered.TargetSavedLevels.Values.Sum();
        var rememberedPaidPointTotal = checked(materializedTargetTotal - 1);
        if (rememberedPaidPointTotal != remembered.TotalTalentPointBudget)
        {
            failure = $"joint target budget differs (paid {rememberedPaidPointTotal}/{remembered.TotalTalentPointBudget}; materialized {materializedTargetTotal})";
            return false;
        }
        var rememberedRemainingPoints = ReadNullableInt(saveHero, "talentRemainPoint") ?? -1;
        if (rememberedRemainingPoints != 0)
        {
            failure = $"joint target left unspent talent points ({rememberedRemainingPoints})";
            return false;
        }

        string DescribeTalentLevel(object talent, int target, string? prefix = null)
        {
            var definition = Read(talent, "tTalentData");
            var talentId = ReadNullableInt(definition, "id") ?? 0;
            var skillId = ReadNullableInt(definition, "skillId") ?? 0;
            var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
            var skillRow = skillId > 0 ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
            var masteryRow = masteryId > 0 ? InvokeStatic("TableData", "getTMasteryData", masteryId) : null;
            var name = FirstNonEmpty(
                Clean(ReadString(skillRow, "name") ?? EnglishName(skillRow, string.Empty) ?? string.Empty),
                Clean(ReadString(masteryRow, "name") ?? EnglishName(masteryRow, string.Empty) ?? string.Empty),
                Clean(ReadString(definition, "name") ?? EnglishName(definition, string.Empty) ?? string.Empty),
                "talent");
            var nativeId = skillId > 0 ? skillId : talentId;
            var saved = GetSavedTalentLevel(talent);
            var effective = GetTalentLevel(talent);
            var cap = GetTalentLevelCap(talent);
            return $"{prefix}{name}#{nativeId} talent={talentId} saved={saved}/{target} effective={effective}/{Math.Min(cap, GetTalentBaseLevelRequired(talent) + target)} cap={cap} plan={remembered.PlanToken}";
        }

        foreach (var targetEntry in remembered.TargetSavedLevels.OrderBy(entry => entry.Key))
        {
            var plannedDefinition = InvokeStatic("TableData", "getTTalentData", targetEntry.Key);
            object? actualTalent = null;
            if (IsTransformableSkillDefinition(plannedDefinition))
            {
                // Shrine transformation replaces the talent row ID. The native
                // skill ID is the stable identity for active-skill targets.
                var skillId = ReadNullableInt(plannedDefinition, "skillId") ?? 0;
                actualTalent = talentById.Values
                    .Where(talent => IsTransformableSkillDefinition(Read(talent, "tTalentData")))
                    .Where(talent => (ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0) == skillId)
                    .OrderByDescending(talent => GetSavedTalentLevel(talent))
                    .ThenBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? int.MaxValue)
                    .FirstOrDefault();
            }
            else
            {
                // Base-skill and mastery nodes are not shrine-row aliases and
                // must retain the exact planned talent ID.
                talentById.TryGetValue(targetEntry.Key, out actualTalent);
            }
            if (actualTalent is null)
            {
                var skillId = ReadNullableInt(plannedDefinition, "skillId") ?? 0;
                var skillRow = skillId > 0 ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
                var name = FirstNonEmpty(Clean(ReadString(skillRow, "name") ?? EnglishName(skillRow, string.Empty) ?? string.Empty), "talent");
                levelFailures.Add($"missing {name}#{(skillId > 0 ? skillId : targetEntry.Key)} talent={targetEntry.Key} saved=?/{targetEntry.Value} effective=? cap=? plan={remembered.PlanToken}");
                continue;
            }
            var actualTalentId = ReadNullableInt(Read(actualTalent, "tTalentData"), "id") ?? 0;
            if (!resolvedTargets.TryAdd(actualTalentId, (actualTalent, targetEntry.Value)))
            {
                levelFailures.Add(DescribeTalentLevel(actualTalent, targetEntry.Value, "duplicate target "));
                continue;
            }
            var cap = GetTalentLevelCap(actualTalent);
            var expectedEffective = Math.Min(cap, GetTalentBaseLevelRequired(actualTalent) + targetEntry.Value);
            if (GetSavedTalentLevel(actualTalent) != targetEntry.Value
                || GetTalentLevel(actualTalent) != expectedEffective)
                levelFailures.Add(DescribeTalentLevel(actualTalent, targetEntry.Value));
        }

        // ResetTalentPoint clears every ordinary resettable node. Verify that no
        // stale point survived outside the exact deterministic target. Fixed and
        // alien nodes are intentionally excluded because the native reset keeps
        // them outside the spendable ledger.
        foreach (var talent in talentById.Values
                     .Where(IsNormalTalentNode)
                     .Where(talent => !IsTalentUnreplaceable(talent)))
        {
            var talentId = ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0;
            if (resolvedTargets.ContainsKey(talentId) || GetSavedTalentLevel(talent) == 0) continue;
            levelFailures.Add(DescribeTalentLevel(talent, 0, "unexpected point "));
        }
        if (levelFailures.Count > 0)
        {
            failure = $"joint target levels differ: {string.Join("; ", levelFailures)}";
            return false;
        }
        var variants = VerifyEquippedSkillVariants(
            hero, talentData, "AUTO-BUILD", expectedPackageSkills);
        if (!variants.IsExact)
        {
            failure = $"gear skill variants differ (-{string.Join(',', variants.Missing)} +{string.Join(',', variants.Unexpected)})";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    public static bool TryOptimizeSelectedHeroSkills(out string message)
        => TryOptimizeSelectedHeroSkills(false, out message);

    private static bool TryOptimizeSelectedHeroSkills(bool requireRememberedJointPlan, out string message)
    {
        var journalTransformAttempts = 0;
        var journalTransformBlood = 0;
        var journalResetInvoked = false;
        var journalResetSucceeded = false;
        var journalResetPrice = 0;
        var journalBaseChanged = false;
        var journalAllocationStart = -1;
        object? journalSaveHero = null;
        try
        {
            var hero = GetSelectedHero();
            if (hero is null)
            {
                message = UiText.L("선택된 영웅이 없습니다.", "No hero is selected.", "未选择英雄。", "未選擇英雄。");
                return false;
            }
            if (ReadBool(InvokeRequiredInstance(hero, "IsAdventureBusy")))
            {
                message = UiText.L(
                    "모험 중에는 현재 전투 스킬과 저장 스킬 상태가 달라질 수 있어 자동 스킬을 실행하지 않습니다. 모험을 끝낸 후 다시 실행하세요.",
                    "Auto Skills is disabled while the hero is adventuring because the live combat skills can differ from saved talents. Try again after the adventure ends.",
                    "英雄冒险时，实时战斗技能可能与已保存天赋不一致，因此未执行自动技能。请在冒险结束后重试。",
                    "英雄冒險時，即時戰鬥技能可能與已儲存天賦不一致，因此未執行自動技能。請在冒險結束後重試。");
                return false;
            }
            var talentData = Read(hero, "heroTalentData");
            var saveHero = Read(hero, "saveHeroData");
            journalSaveHero = saveHero;
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData");
            var townData = Read(seasonData, "townData");
            if (talentData is null || saveHero is null || townData is null) throw new InvalidOperationException("Talent data is unavailable.");

            // Only the native talent grid spends normal talent points. Inspired,
            // alien and runeword entries in extraTalentList have separate rules.
            var talents = ReadValues(Read(talentData, "talentDic"))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            if (talents.Count == 0)
            {
                message = UiText.L("현재 영웅의 스킬·특성 표를 찾지 못했습니다.", "The selected hero's skill grid is unavailable.", "找不到当前英雄的技能天赋表。", "找不到目前英雄的技能天賦表。");
                return false;
            }

            // Resolve the build before resetting. Resetting removes the active
            // skill evidence that Auto uses to identify the intended archetype.
            var focus = ResolveHeroFocus(hero, GetHeroBuildTheme(hero));
            var spentBefore = GetResettableTalentPointCount(talentData, talents);
            var totalTalentPoints = PreviewExactTalentPointBudget(talentData, saveHero, talents, spentBefore);
            PreferredTalentPlan preferred;
            RememberedJointSkillPlan? requiredJointPlan = null;
            if (requireRememberedJointPlan)
            {
                var planKey = GetJointSkillPlanHeroKey(hero);
                if (!JointSkillPlansByHero.TryGetValue(planKey, out requiredJointPlan))
                    throw new InvalidOperationException("The gear step did not leave an exact joint skill plan.");
                if (!string.Equals(requiredJointPlan.FocusKey, focus.Key, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The remembered joint focus changed ({requiredJointPlan.FocusKey}/{focus.Key}).");
                if (!string.Equals(requiredJointPlan.GearFingerprint, GetCurrentGearFingerprint(hero), StringComparison.Ordinal))
                    throw new InvalidOperationException("The equipped items changed after the joint gear plan was committed.");
                var currentGrantedSkillLevels = GetCurrentGrantedSkillLevels(hero);
                if (currentGrantedSkillLevels.Count != requiredJointPlan.GrantedSkillLevels.Count
                    || requiredJointPlan.GrantedSkillLevels.Any(entry =>
                        currentGrantedSkillLevels.GetValueOrDefault(entry.Key) != entry.Value))
                    throw new InvalidOperationException(
                        "The equipment-granted skill package changed after the joint gear plan was committed.");
                if (string.IsNullOrWhiteSpace(requiredJointPlan.PlanToken))
                    throw new InvalidOperationException("The exact joint plan token is empty.");

                // Combined Auto Build must apply the immutable plan selected while
                // comparing gear. Re-evaluating here could overwrite plan A with
                // plan B and then verify only B, which would make a sequential
                // gear/skill result look joint even when the two optimizers drifted.
                preferred = new PreferredTalentPlan(
                    null, new List<int>(), new List<int>(), new HashSet<int>(),
                    UiText.L("장비 공동 계획", "Joint gear plan", "装备联合方案", "裝備聯合方案"));
                preferred = ApplyRememberedJointSkillPlan(hero, focus, preferred);
                if (!string.Equals(preferred.PlanToken, requiredJointPlan.PlanToken, StringComparison.Ordinal))
                    throw new InvalidOperationException("The remembered joint plan could not be materialized exactly.");
            }
            else
            {
                // Standalone Auto Skills has no preceding gear transaction. Build
                // a fresh joint plan from the equipment that is actually worn.
                preferred = GetPerformanceTalentPlan(hero, focus, totalTalentPoints);
                preferred = ApplyCurrentGearJointSkillPlan(hero, focus, preferred);
            }

            // Validate the computed performance plan before choosing the subset
            // that fits the hero's currently unlocked rows. Invalid rows must
            // not be silently discarded and reported as a successful plan.
            var invalidGuideSkillTalentIds = preferred.SkillTalentIds.Where(id =>
            {
                var definition = InvokeStatic("TableData", "getTTalentData", id);
                if (definition is null || (ReadNullableInt(definition, "type") ?? 0) != 1) return true;
                return (ReadNullableInt(definition, "skillId") ?? 0) <= 0;
            }).ToList();
            var invalidGuideMasteryTalentIds = preferred.MasteryTalentIds.Where(id =>
            {
                var definition = InvokeStatic("TableData", "getTTalentData", id);
                return definition is null || (ReadNullableInt(definition, "type") ?? 0) != 2
                       || (ReadNullableInt(definition, "masteryId") ?? 0) <= 0;
            }).ToList();
            if (invalidGuideSkillTalentIds.Count > 0 || invalidGuideMasteryTalentIds.Count > 0)
            {
                message = UiText.L(
                    $"성능 계획 데이터가 불완전합니다 · 스킬 {string.Join(',', invalidGuideSkillTalentIds)} · 마스터리 {string.Join(',', invalidGuideMasteryTalentIds)}. 초기화하지 않았습니다.",
                    $"The performance plan is incomplete · skills {string.Join(',', invalidGuideSkillTalentIds)} · masteries {string.Join(',', invalidGuideMasteryTalentIds)}. No reset was performed.",
                    $"性能方案数据不完整 · 技能 {string.Join(',', invalidGuideSkillTalentIds)} · 专精 {string.Join(',', invalidGuideMasteryTalentIds)}。未执行重置。",
                    $"效能方案資料不完整 · 技能 {string.Join(',', invalidGuideSkillTalentIds)} · 專精 {string.Join(',', invalidGuideMasteryTalentIds)}。未執行重設。");
                return false;
            }
            // A guide can list more active skills than the hero currently has
            // unlocked shrine rows. Pick the actual row-sized loadout before
            // washing; otherwise any arbitrary N matching skills make the wash
            // look complete and a build-defining skill can never be rolled.
            preferred = SelectPreferredActiveSkillTargets(hero, talentData, preferred, focus);
            if (requiredJointPlan is not null)
            {
                var selectedBaseSkillId = preferred.SkillTalentIds
                    .Select(id => InvokeStatic("TableData", "getTTalentData", id))
                    .Where(IsBaseSkillDefinition)
                    .Select(row => ReadNullableInt(row, "skillId") ?? 0)
                    .FirstOrDefault(id => id > 0);
                var selectedJointActiveSkillIds = preferred.SkillTalentIds
                    .Select(id => InvokeStatic("TableData", "getTTalentData", id))
                    .Where(IsTransformableSkillDefinition)
                    .Select(row => ReadNullableInt(row, "skillId") ?? 0)
                    .Where(id => id > 0).ToHashSet();
                if (selectedBaseSkillId != requiredJointPlan.BaseSkillId
                    || !selectedJointActiveSkillIds.SetEquals(requiredJointPlan.ActiveSkillIds)
                    || !string.Equals(preferred.PlanToken, requiredJointPlan.PlanToken, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The unlocked talent rows cannot materialize the exact gear-selected skill package.");
            }
            // ApplyCurrentGearJointSkillPlan already rebuilt masteries against the
            // exact committed gear, skill package and per-hero theme. Rebuilding
            // again on a gear-neutral AttrData would silently replace that joint
            // winner with a different mastery branch.
            var gridById = BuildTalentGridById(talents);
            if (gridById.Count == 0)
            {
                message = UiText.L("배분 가능한 스킬·특성이 없습니다.", "No skills or talents can receive points.", "没有可分配点数的技能或天赋。", "沒有可分配點數的技能或天賦。");
                return false;
            }
            var selectedActiveDefinitions = preferred.SkillTalentIds
                .Select(id => InvokeStatic("TableData", "getTTalentData", id))
                .Where(IsTransformableSkillDefinition).ToList();
            // A native joint package may legitimately be base-only. Do not force
            // every unlocked shrine row into the plan when all optional active
            // skills reduce the shared-time objective.
            var incompatibleSkillIds = selectedActiveDefinitions
                .Select(definition => ReadNullableInt(definition, "skillId") ?? 0)
                .Where(id => id > 0 && !IsSkillCompatibleWithEquippedWeapons(hero, id))
                .Distinct().ToList();
            var desiredBaseTalentForPreflight = preferred.SkillTalentIds
                .FirstOrDefault(id => IsBaseSkillDefinition(InvokeStatic("TableData", "getTTalentData", id)));
            if (desiredBaseTalentForPreflight > 0)
            {
                if (!gridById.ContainsKey(desiredBaseTalentForPreflight))
                {
                    message = UiText.L(
                        $"추천 기본 스킬 {desiredBaseTalentForPreflight}을 현재 특성표에서 찾지 못했습니다. 초기화하지 않았습니다.",
                        $"Recommended base skill {desiredBaseTalentForPreflight} is absent from the current talent grid. No reset was performed.",
                        $"当前天赋表中没有推荐基础技能 {desiredBaseTalentForPreflight}，未执行重置。",
                        $"目前天賦表中沒有推薦基礎技能 {desiredBaseTalentForPreflight}，未執行重設。");
                    return false;
                }
                var desiredBaseDefinitionForPreflight = InvokeStatic("TableData", "getTTalentData", desiredBaseTalentForPreflight);
                var desiredBaseSkillForPreflight = ReadNullableInt(desiredBaseDefinitionForPreflight, "skillId") ?? 0;
                if (desiredBaseSkillForPreflight > 0 && !IsSkillCompatibleWithEquippedWeapons(hero, desiredBaseSkillForPreflight))
                    incompatibleSkillIds.Add(desiredBaseSkillForPreflight);
            }
            incompatibleSkillIds = incompatibleSkillIds.Distinct().ToList();
            if (incompatibleSkillIds.Count > 0)
            {
                message = UiText.L(
                    $"현재 무기로 추천 스킬을 사용할 수 없습니다 ({string.Join(',', incompatibleSkillIds)}). 먼저 장비 자동 장착을 실행하세요. 초기화하지 않았습니다.",
                    $"The current weapons cannot use the recommended skills ({string.Join(',', incompatibleSkillIds)}). Run Auto Gear first. No reset was performed.",
                    $"当前武器无法使用推荐技能（{string.Join(',', incompatibleSkillIds)}）。请先运行自动装备，未执行重置。",
                    $"目前武器無法使用推薦技能（{string.Join(',', incompatibleSkillIds)}）。請先執行自動裝備，未執行重設。");
                return false;
            }

            var selectedActiveSkillIds = selectedActiveDefinitions
                .Select(definition => ReadNullableInt(definition, "skillId") ?? 0)
                .Where(id => id > 0).Distinct().ToHashSet();
            var currentUnlockedActiveSkillIds = GetTransformableTalents(talentData)
                .Where(talent => !IsTalentLockedRequired(talent))
                .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
                .Where(id => id > 0).ToHashSet();
            var missingBeforeTransform = selectedActiveSkillIds
                .Where(id => !currentUnlockedActiveSkillIds.Contains(id)).ToList();
            if (missingBeforeTransform.Count > 0
                && (!Plugin.AutoTransformSkills.Value || Plugin.AutoTransformMaxAttempts.Value <= 0))
            {
                message = UiText.L(
                    $"추천 스킬 변환이 필요합니다 ({string.Join(',', missingBeforeTransform)}). 스킬 변환을 켜거나 직접 변환하세요. 초기화하지 않았습니다.",
                    $"Recommended skills require transformation ({string.Join(',', missingBeforeTransform)}). Enable skill transformation or transform them manually. No reset was performed.",
                    $"推荐技能需要转换（{string.Join(',', missingBeforeTransform)}）。请启用技能转换或手动转换，未执行重置。",
                    $"推薦技能需要轉換（{string.Join(',', missingBeforeTransform)}）。請啟用技能轉換或手動轉換，未執行重設。");
                return false;
            }

            var investedFixedOutsidePlan = GetTransformableTalents(talentData)
                .Where(IsTalentFixedRequired)
                .Where(talent => GetSavedTalentLevel(talent) > 0)
                .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
                .Where(id => id > 0 && !selectedActiveSkillIds.Contains(id))
                .Distinct().ToList();
            if (investedFixedOutsidePlan.Count > 0)
            {
                message = UiText.L(
                    $"추천 빌드 밖의 고정 스킬에 포인트가 있습니다 ({string.Join(',', investedFixedOutsidePlan)}). 고정을 해제하거나 포인트를 정리한 뒤 다시 실행하세요. 변환·초기화하지 않았습니다.",
                    $"Fixed skills outside the selected guide have invested points ({string.Join(',', investedFixedOutsidePlan)}). Unfix or respec them first. No transformation or reset was performed.",
                    $"所选流派之外的固定技能已有加点（{string.Join(',', investedFixedOutsidePlan)}）。请先取消固定或整理点数。未执行转换或重置。",
                    $"所選流派之外的固定技能已有加點（{string.Join(',', investedFixedOutsidePlan)}）。請先取消固定或整理點數。未執行轉換或重設。");
                return false;
            }

            var desiredGuideMasteryIds = preferred.MasteryTalentIds.Distinct().ToList();
            var missingGuideMasteryIds = desiredGuideMasteryIds.Where(id => !gridById.ContainsKey(id)).ToList();
            if (missingGuideMasteryIds.Count > 0)
            {
                message = UiText.L(
                    $"추천 마스터리 노드를 현재 특성표에서 찾지 못했습니다 ({string.Join(',', missingGuideMasteryIds)}). 변환·초기화하지 않았습니다.",
                    $"Guide mastery nodes are missing from the current talent grid ({string.Join(',', missingGuideMasteryIds)}). No transformation or reset was performed.",
                    $"当前天赋树中缺少推荐专精节点（{string.Join(',', missingGuideMasteryIds)}）。未执行转换或重置。",
                    $"目前天賦樹中缺少推薦專精節點（{string.Join(',', missingGuideMasteryIds)}）。未執行轉換或重設。");
                return false;
            }

            // The exact native point budget was previewed before the performance
            // plan, so mastery selection and the mutation preflight use the same
            // milestone-adjusted total.
            var preResetGuideMasteryIds = desiredGuideMasteryIds
                .Where(id => !IsTalentLockedRequired(gridById[id]))
                .ToList();
            // Every selected active skill and mastery is part of the committed
            // plan. Reserve one real saved point for each before any destructive
            // reset; an effective/base bonus alone is not proof that the plan was
            // learned by this hero.
            var minimumRequiredPoints = selectedActiveSkillIds.Count + preResetGuideMasteryIds.Count;
            if (totalTalentPoints < minimumRequiredPoints)
            {
                message = UiText.L(
                    $"추천 스킬·마스터리 최소 배분 포인트가 부족합니다 · 필요 {minimumRequiredPoints:N0} / 보유 {totalTalentPoints:N0}. 변환·초기화하지 않았습니다.",
                    $"Not enough talent points for the minimum guide package · need {minimumRequiredPoints:N0} / have {totalTalentPoints:N0}. No transformation or reset was performed.",
                    $"推荐技能与专精的最低分配点数不足 · 需要 {minimumRequiredPoints:N0} / 拥有 {totalTalentPoints:N0}。未执行转换或重置。",
                    $"推薦技能與專精的最低分配點數不足 · 需要 {minimumRequiredPoints:N0} / 擁有 {totalTalentPoints:N0}。未執行轉換或重設。");
                return false;
            }

            var resetPrice = Convert.ToInt32(InvokeRequiredInstance(talentData, "GetResetTalentPrice") ?? throw new InvalidOperationException("Talent reset price is unavailable."), CultureInfo.InvariantCulture);
            journalResetPrice = resetPrice;
            var bloodType = CreateEnum("EResType", 2) ?? throw new InvalidOperationException("Blood resource type is unavailable.");
            var bloodBefore = Convert.ToInt32(InvokeInstance(townData, "GetRes", bloodType) ?? 0, CultureInfo.InvariantCulture);
            if (spentBefore > 0 && bloodBefore < resetPrice)
            {
                message = UiText.L($"초기화 재화 부족 · 필요 피 {resetPrice:N0} / 보유 {bloodBefore:N0}", $"Not enough Blood to reset · need {resetPrice:N0} / have {bloodBefore:N0}", $"重置资源不足 · 需要鲜血 {resetPrice:N0} / 持有 {bloodBefore:N0}", $"重設資源不足 · 需要鮮血 {resetPrice:N0} / 持有 {bloodBefore:N0}");
                return false;
            }

            var transform = Plugin.AutoTransformSkills.Value && Plugin.AutoTransformMaxAttempts.Value > 0
                ? TransformMissingPreferredSkills(hero, talentData, townData, preferred, spentBefore > 0 ? resetPrice : 0, Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50))
                : new SkillTransformResult(0, 0, 0, 0, string.Empty, true);
            journalTransformAttempts = transform.Attempts;
            journalTransformBlood = transform.SpentBlood;
            var transformBloodText = transform.SpentBlood >= 0
                ? transform.SpentBlood.ToString("N0", CultureInfo.CurrentCulture)
                : UiText.L("확인 불가", "unknown", "未知", "未知");
            talents = ReadValues(Read(talentData, "talentDic"))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            if (!transform.ExecutionSucceeded)
            {
                Plugin.DiagWarning($"AUTO-SKILLS TRANSFORM ERROR|attempts={transform.Attempts}|matched={transform.Matched}|target={transform.Target}|spentBlood={transform.SpentBlood}|cleanup={transform.CleanupSucceeded}|reason={transform.Note}");
                message = UiText.L(
                    $"스킬 변환 중 오류가 발생해 특성 초기화를 중단했습니다 · 성공 변환 {transform.Attempts}회 · 변환 피 {transformBloodText} · 현재 스킬 행은 이미 바뀌었을 수 있습니다{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}",
                    $"Talent reset was stopped after a skill-transformation error · {transform.Attempts} successful transform(s) · transform Blood {transformBloodText} · current skill rows may already have changed{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}",
                    $"技能转换出错，已停止天赋重置 · 成功转换 {transform.Attempts} 次 · 转换鲜血 {transformBloodText} · 当前技能行可能已发生变化{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}",
                    $"技能轉換發生錯誤，已停止天賦重設 · 成功轉換 {transform.Attempts} 次 · 轉換鮮血 {transformBloodText} · 目前技能列可能已變更{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}");
                return false;
            }
            if (Plugin.AutoTransformSkills.Value && transform.Target > 0 && transform.Matched < transform.Target)
            {
                Plugin.DiagWarning($"AUTO-SKILLS TRANSFORM INCOMPLETE|attempts={transform.Attempts}|matched={transform.Matched}|target={transform.Target}|spentBlood={transform.SpentBlood}|reason={transform.Note}");
                message = UiText.L(
                    $"추천 스킬 변환이 완료되지 않아 특성 초기화를 중단했습니다 · {transform.Matched}/{transform.Target} · 변환 피 {transform.SpentBlood:N0}{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}",
                    $"Talent reset was stopped because skill transformation was incomplete · {transform.Matched}/{transform.Target} · transform Blood {transform.SpentBlood:N0}{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}",
                    $"技能转换未完成，已停止天赋重置 · {transform.Matched}/{transform.Target} · 转换鲜血 {transform.SpentBlood:N0}{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}",
                    $"技能轉換未完成，已停止天賦重設 · {transform.Matched}/{transform.Target} · 轉換鮮血 {transform.SpentBlood:N0}{(string.IsNullOrWhiteSpace(transform.Note) ? string.Empty : $" · {transform.Note}")}");
                return false;
            }
            if (!transform.CleanupSucceeded)
            {
                message = UiText.L(
                    $"스킬 변환 임시 설정을 완전히 복구하지 못해 특성 초기화를 중단했습니다 · 성공 변환 {transform.Attempts}회 · 변환 피 {transformBloodText} · 현재 스킬 행은 이미 바뀌었을 수 있습니다.",
                    $"Talent reset was stopped because temporary transformation settings were not restored · {transform.Attempts} successful transform(s) · transform Blood {transformBloodText} · current skill rows may already have changed.",
                    $"未能完整恢复技能转换临时设置，已停止天赋重置 · 成功转换 {transform.Attempts} 次 · 转换鲜血 {transformBloodText} · 当前技能行可能已发生变化。",
                    $"未能完整復原技能轉換暫存設定，已停止天賦重設 · 成功轉換 {transform.Attempts} 次 · 轉換鮮血 {transformBloodText} · 目前技能列可能已變更。");
                return false;
            }

            // Transformation changes the active rows. Resolve the real post-wash
            // nodes and ensure each required node can accept at least one saved
            // point before consuming the reset cost.
            gridById = BuildTalentGridById(talents);
            var postTransformActive = ResolvePreferredActiveSkills(preferred, gridById);
            var postTransformActiveSkillIds = postTransformActive
                .Select(entry => entry.SkillId).Where(id => id > 0).ToHashSet();
            var unresolvedAfterTransform = selectedActiveSkillIds
                .Where(id => !postTransformActiveSkillIds.Contains(id)).ToList();
            var postTransformMasteryIds = preResetGuideMasteryIds
                .Where(gridById.ContainsKey)
                .Where(id => !IsTalentLockedRequired(gridById[id]))
                .ToList();
            var noPointCapacityIds = postTransformActive.Select(entry => entry.Talent)
                .Concat(postTransformMasteryIds.Select(id => gridById[id]))
                .Where(talent => GetTalentLevelCap(talent) - GetTalentBaseLevelRequired(talent) < 1)
                .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
                .Where(id => id > 0).Distinct().ToList();
            var transformPostconditionExact = postTransformActiveSkillIds.SetEquals(selectedActiveSkillIds)
                                              && (transform.Target <= 0
                                                  || (transform.Matched == postTransformActiveSkillIds.Count
                                                      && transform.Target == selectedActiveSkillIds.Count));
            if (!transformPostconditionExact || postTransformMasteryIds.Count != preResetGuideMasteryIds.Count || noPointCapacityIds.Count > 0)
            {
                message = UiText.L(
                    $"추천 노드 사전 검증 실패 · 미확인 스킬 {string.Join(',', unresolvedAfterTransform)} · 포인트 불가 {string.Join(',', noPointCapacityIds)}. 특성 초기화하지 않았습니다. 변환 피 {transform.SpentBlood:N0}",
                    $"Guide-node preflight failed · unresolved skills {string.Join(',', unresolvedAfterTransform)} · cannot receive points {string.Join(',', noPointCapacityIds)}. Talent reset was not performed. Transform Blood {transform.SpentBlood:N0}",
                    $"推荐节点预检失败 · 未确认技能 {string.Join(',', unresolvedAfterTransform)} · 无法加点 {string.Join(',', noPointCapacityIds)}。未重置天赋。转换鲜血 {transform.SpentBlood:N0}",
                    $"推薦節點預檢失敗 · 未確認技能 {string.Join(',', unresolvedAfterTransform)} · 無法加點 {string.Join(',', noPointCapacityIds)}。未重設天賦。轉換鮮血 {transform.SpentBlood:N0}");
                return false;
            }

            journalResetInvoked = spentBefore > 0;
            var resetResult = journalResetInvoked
                ? Convert.ToInt32(InvokeRequiredInstance(talentData, "ResetTalentPoint") ?? 1, CultureInfo.InvariantCulture)
                : 0;
            if (resetResult != 0)
            {
                message = UiText.L($"특성 초기화 실패 · 필요 피 {resetPrice:N0} / 보유 {bloodBefore:N0}", $"Talent reset failed · need {resetPrice:N0} Blood / have {bloodBefore:N0}", $"天赋重置失败 · 需要鲜血 {resetPrice:N0} / 持有 {bloodBefore:N0}", $"天賦重設失敗 · 需要鮮血 {resetPrice:N0} / 持有 {bloodBefore:N0}")
                          + DescribeSkillMutationJournal(journalTransformAttempts, journalTransformBlood, journalResetInvoked, false, journalResetPrice, false, -1, journalSaveHero);
                return false;
            }
            journalResetSucceeded = journalResetInvoked;
            var remainAfterReset = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
            journalAllocationStart = remainAfterReset;
            talents = ReadValues(Read(talentData, "talentDic"))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            gridById = BuildTalentGridById(talents);
            var spentAfter = GetResettableTalentPointCount(talentData, talents);
            if (spentBefore > 0 && spentAfter != 0)
            {
                message = UiText.L("초기화 함수는 성공을 반환했지만 일부 포인트가 남았습니다.", "The reset function returned success, but invested points remain.", "重置函数返回成功，但仍有已投入点数。", "重設函式回傳成功，但仍有已投入點數。")
                          + DescribeSkillMutationJournal(journalTransformAttempts, journalTransformBlood, journalResetInvoked, journalResetSucceeded, journalResetPrice, journalBaseChanged, journalAllocationStart, journalSaveHero);
                return false;
            }
            // Native ResetTalentPoint recalculates blessTalentPoint from the
            // current milestone state, then SaveHeroData.ResetTalentPoint sets
            // talentRemainPoint to GetTotalTalentPoint().  The old
            // remainBefore + spentBefore check could therefore reject a valid
            // reset after it had already consumed Blood.  Verify against the
            // same native total that the reset itself uses.
            var expectedRemainAfterReset = Convert.ToInt32(
                InvokeRequiredInstance(saveHero, "GetTotalTalentPoint")
                ?? throw new InvalidOperationException("The native total talent point value is unavailable."),
                CultureInfo.InvariantCulture);
            if (remainAfterReset != expectedRemainAfterReset)
            {
                message = UiText.L(
                    $"특성 초기화 포인트 검증 실패 · 예상 {expectedRemainAfterReset:N0} / 실제 {remainAfterReset:N0}",
                    $"Talent reset point verification failed · expected {expectedRemainAfterReset:N0} / actual {remainAfterReset:N0}",
                    $"天赋重置点数验证失败 · 预期 {expectedRemainAfterReset:N0} / 实际 {remainAfterReset:N0}",
                    $"天賦重設點數驗證失敗 · 預期 {expectedRemainAfterReset:N0} / 實際 {remainAfterReset:N0}")
                          + DescribeSkillMutationJournal(journalTransformAttempts, journalTransformBlood, journalResetInvoked, journalResetSucceeded, journalResetPrice, journalBaseChanged, journalAllocationStart, journalSaveHero);
                return false;
            }

            var baseSkillChanged = ApplyPreferredBaseSkill(talentData, saveHero, preferred, out var baseSkillName);
            journalBaseChanged = baseSkillChanged;
            talents = ReadValues(Read(talentData, "talentDic"))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            gridById = BuildTalentGridById(talents);

            var beforeAllocation = remainAfterReset;
            var allocatedByPlan = 0;
            var failedNodes = new HashSet<int>();
            var previewFailedNodes = new HashSet<int>();
            var desiredActiveSkillIds = preferred.SkillTalentIds
                .Select(id => InvokeStatic("TableData", "getTTalentData", id))
                .Where(IsTransformableSkillDefinition)
                .Select(definition => ReadNullableInt(definition, "skillId") ?? 0)
                .Where(id => id > 0).Distinct().ToList();
            var activeSkills = ResolvePreferredActiveSkills(preferred, gridById);
            var activeSkillTalentIds = activeSkills.Select(entry => entry.TalentId).ToList();
            var availableActiveSkillIds = activeSkills.Select(entry => entry.SkillId).ToHashSet();
            var availableBaseSkillTalentIds = preferred.SkillTalentIds
                .Where(id => IsBaseSkillDefinition(InvokeStatic("TableData", "getTTalentData", id)))
                .Where(gridById.ContainsKey).ToList();
            var availableBaseSkillIds = availableBaseSkillTalentIds
                .Select(id => ReadNullableInt(InvokeStatic("TableData", "getTTalentData", id), "skillId") ?? 0)
                .Where(id => id > 0);
            var availablePreferredSkillIds = availableActiveSkillIds.Concat(availableBaseSkillIds).ToHashSet();
            var unresolvedPreferredSkillIds = desiredActiveSkillIds.Where(id => !availableActiveSkillIds.Contains(id)).ToList();
            LogPreferredActiveSkillState("RESOLVED", activeSkills, unresolvedPreferredSkillIds);
            var relevantMasteryTalentIds = preferred.MasteryTalentIds
                .Where(id => (ReadNullableInt(InvokeStatic("TableData", "getTTalentData", id), "type") ?? 0) == 2)
                .Where(gridById.ContainsKey)
                .Where(id => !IsTalentLockedRequired(gridById[id]))
                .ToList();
            var effectivePreferred = preferred with
            {
                SkillTalentIds = availableBaseSkillTalentIds.Concat(activeSkillTalentIds).Distinct().ToList(),
                MasteryTalentIds = relevantMasteryTalentIds,
                PreferredSkillIds = availablePreferredSkillIds
            };

            // Snapshot every saved/effective level after reset and base-skill
            // selection. Each successful native AddTalentPoint call advances this
            // ledger; the final state must match it node-for-node.
            var expectedSavedLevels = gridById.ToDictionary(
                entry => entry.Key, entry => GetSavedTalentLevel(entry.Value));
            void RecordExpectedSpend(int talentId, int spent)
            {
                if (spent <= 0) return;
                if (!expectedSavedLevels.TryGetValue(talentId, out var before))
                    throw new InvalidOperationException($"Allocated talent {talentId} is absent from the post-reset ledger.");
                expectedSavedLevels[talentId] = checked(before + spent);
            }

            var requiredTalentIds = activeSkillTalentIds
                .Concat(relevantMasteryTalentIds).Distinct().ToList();
            var exactTargetLevels = new Dictionary<int, int>();
            var rawTargetLevels = effectivePreferred.TargetSavedLevels;
            if (rawTargetLevels is not null)
            {
                foreach (var talentId in availableBaseSkillTalentIds.Concat(relevantMasteryTalentIds).Distinct())
                    if (gridById.ContainsKey(talentId) && rawTargetLevels.TryGetValue(talentId, out var target))
                        exactTargetLevels[talentId] = target;
                // Shrine transformation can materialize the requested skill in
                // a position-specific TTalent row with a different talent ID.
                // The shared plan is keyed by its guide row before washing, so
                // transfer that exact saved-level target to the resolved native
                // row by the already-verified skill identity.
                foreach (var active in activeSkills)
                {
                    if (rawTargetLevels.TryGetValue(active.TalentId, out var target)
                        || rawTargetLevels.TryGetValue(active.GuideTalentId, out target))
                        exactTargetLevels[active.TalentId] = target;
                }
            }
            if (exactTargetLevels.Count > 0)
            {
                var missingRequiredTargets = requiredTalentIds.Where(id =>
                    !exactTargetLevels.TryGetValue(id, out var target) || target <= 0).ToList();
                if (missingRequiredTargets.Count > 0)
                    throw new InvalidOperationException(
                        $"The shared gear/skill plan has no learnable target for required talents {string.Join(',', missingRequiredTargets)}.");
                var availablePointTotal = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
                // The selected base skill is materialized by ChangeBaseSkill with
                // one free saved level. Compare only the additional native point
                // calls still required from the post-change state; summing raw
                // targets would count that free level against the spendable pool.
                var materializedBaseTargets = availableBaseSkillTalentIds
                    .Where(exactTargetLevels.ContainsKey)
                    .Select(id => exactTargetLevels[id])
                    .ToList();
                if (materializedBaseTargets.Count != 1 || materializedBaseTargets[0] < 1)
                    throw new InvalidOperationException(
                        "The shared gear/skill plan has no single materialized base-skill level.");
                var materializedTargetTotal = exactTargetLevels.Values.Sum();
                var plannedPaidPointTotal = checked(materializedTargetTotal - 1);
                if (plannedPaidPointTotal != availablePointTotal)
                    throw new InvalidOperationException(
                        $"The shared gear/skill plan paid-point budget differs ({plannedPaidPointTotal}/{availablePointTotal}; materialized {materializedTargetTotal}).");
                var requiredAdditionalPoints = exactTargetLevels.Sum(entry =>
                    Math.Max(0, entry.Value - GetSavedTalentLevel(gridById[entry.Key])));
                if (requiredAdditionalPoints > availablePointTotal)
                    throw new InvalidOperationException(
                        $"The shared gear/skill plan requires {requiredAdditionalPoints} additional points, but only {availablePointTotal} are available after reset.");

                foreach (var entry in exactTargetLevels.OrderBy(entry =>
                             ReadNullableInt(Read(gridById[entry.Key], "tTalentData"), "floor") ?? int.MaxValue)
                         .ThenBy(entry => entry.Key))
                {
                    var talent = gridById[entry.Key];
                    var cap = GetTalentLevelCap(talent);
                    var baseLevel = Math.Min(GetTalentBaseLevelRequired(talent), cap);
                    var maxSaved = Math.Max(0, cap - baseLevel);
                    if (entry.Value < 0 || entry.Value > maxSaved)
                        throw new InvalidOperationException(
                            $"Talent {entry.Key} target {entry.Value} exceeds native cap {cap} (base {baseLevel}).");
                    var currentSaved = GetSavedTalentLevel(talent);
                    var requested = entry.Value - currentSaved;
                    if (requested < 0)
                        throw new InvalidOperationException(
                            $"Talent {entry.Key} is already above the exact target ({currentSaved}/{entry.Value}).");
                    if (requested == 0) continue;
                    if (!TrySpendTalentPoints(talentData, saveHero, talent, requested, out var spent)
                        || spent != requested)
                    {
                        failedNodes.Add(entry.Key);
                        continue;
                    }
                    RecordExpectedSpend(entry.Key, spent);
                    allocatedByPlan += spent;
                }
            }
            else
            {
                // Compatibility fallback for a plan created before exact target
                // vectors were introduced. New joint plans always use the branch
                // above, so Auto Gear and Auto Skills cannot independently drift.
                foreach (var talentId in requiredTalentIds)
                {
                    if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                    var talent = gridById[talentId];
                    if (GetSavedTalentLevel(talent) > 0) continue;
                    if (!TrySpendTalentPoints(talentData, saveHero, talent, 1, out var spent)) failedNodes.Add(talentId);
                    else RecordExpectedSpend(talentId, spent);
                    allocatedByPlan += spent;
                }

                var marginalCandidateIds = availableBaseSkillTalentIds
                    .Concat(activeSkillTalentIds)
                    .Concat(relevantMasteryTalentIds)
                    .Where(gridById.ContainsKey)
                    .Distinct().ToList();
                var guard = 0;
                while ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) > 0 && guard++ < 2048)
                {
                    var best = marginalCandidateIds
                        .Where(id => !failedNodes.Contains(id))
                        .Select(id => gridById[id])
                        .Where(talent => CanAutoAllocateTalent(talent, hero, effectivePreferred))
                        .Select(talent =>
                        {
                            try
                            {
                                return (Talent: talent, Gain: ScoreTalentPointMarginalGain(
                                    hero, talentData, talent, focus, effectivePreferred));
                            }
                            catch (Exception error)
                            {
                                var id = ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0;
                                if (id > 0) previewFailedNodes.Add(id);
                                Plugin.DiagDebug($"AUTO-SKILLS MARGINAL PREVIEW FAILED|talent={id}|{error.GetBaseException().Message}");
                                return (Talent: talent, Gain: double.NegativeInfinity);
                            }
                        })
                        .Where(entry => double.IsFinite(entry.Gain) && entry.Gain > 0.000001d)
                        .OrderByDescending(entry => entry.Gain)
                        .ThenBy(entry => ReadNullableInt(Read(entry.Talent, "tTalentData"), "floor") ?? int.MaxValue)
                        .ThenBy(entry => ReadNullableInt(Read(entry.Talent, "tTalentData"), "id") ?? int.MaxValue)
                        .Select(entry => entry.Talent)
                        .FirstOrDefault();
                    if (best is null) break;
                    var talentId = ReadNullableInt(Read(best, "tTalentData"), "id") ?? 0;
                    if (!TrySpendTalentPoints(talentData, saveHero, best, 1, out var spent))
                        failedNodes.Add(talentId);
                    else RecordExpectedSpend(talentId, spent);
                    allocatedByPlan += spent;
                }
            }

            // A native rebuild or an effective-level bonus must never make a
            // recommended skill look learned while its saved point remains zero.
            // Retry with any remaining point, then record exact state for support.
            foreach (var entry in activeSkills.Where(entry => GetSavedTalentLevel(entry.Talent) <= 0))
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                if (!TrySpendTalentPoints(talentData, saveHero, entry.Talent, 1, out var spent)) failedNodes.Add(entry.TalentId);
                else RecordExpectedSpend(entry.TalentId, spent);
                allocatedByPlan += spent;
            }
            var unlearnedPreferredSkillIds = activeSkills
                .Where(entry => GetSavedTalentLevel(entry.Talent) <= 0)
                .Select(entry => entry.SkillId).ToList();
            var missingPreferredSkillIds = unresolvedPreferredSkillIds
                .Concat(unlearnedPreferredSkillIds).Where(id => id > 0).Distinct().ToList();
            LogPreferredActiveSkillState("ALLOCATED", activeSkills, unresolvedPreferredSkillIds);
            if (unlearnedPreferredSkillIds.Count > 0)
                Plugin.DiagWarning($"AUTO-SKILLS ACTIVE UNLEARNED|skills={string.Join(',', unlearnedPreferredSkillIds)}|remaining={ReadNullableInt(saveHero, "talentRemainPoint") ?? 0}");

            var remaining = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
            var allocated = Math.Max(0, beforeAllocation - remaining);
            if (allocated <= 0 && beforeAllocation > 0)
            {
                message = UiText.L("초기화는 완료됐지만 현재 레벨에서 배분 가능한 특성이 없습니다.", "Reset completed, but no talent can receive points at the current level.", "重置已完成，但当前等级没有可分配的天赋。", "重設已完成，但目前等級沒有可分配的天賦。")
                          + DescribeSkillMutationJournal(journalTransformAttempts, journalTransformBlood, journalResetInvoked, journalResetSucceeded, journalResetPrice, journalBaseChanged, journalAllocationStart, journalSaveHero);
                return false;
            }

            InvokeRequiredInstance(talentData, "ReapplySkillVariantsFromEquippedItems");
            var usableSkillIds = ReadList(InvokeRequiredInstance(talentData, "GetSkillList"))
                .Select(skill => ReadNullableInt(Read(skill, "tSkillData"), "id") ?? 0)
                .Where(id => id > 0).ToHashSet();
            var learnedActiveSkillIds = activeSkills
                .Where(entry => GetSavedTalentLevel(entry.Talent) > 0)
                .Select(entry => entry.SkillId).Where(id => id > 0).ToHashSet();
            var activeSkillPostconditionExact = learnedActiveSkillIds.SetEquals(desiredActiveSkillIds);
            var learnedButUnavailableSkillIds = activeSkills
                .Where(entry => GetSavedTalentLevel(entry.Talent) > 0 && !usableSkillIds.Contains(entry.SkillId))
                .Select(entry => entry.SkillId).Distinct().ToList();
            if (learnedButUnavailableSkillIds.Count > 0)
            {
                missingPreferredSkillIds = missingPreferredSkillIds.Concat(learnedButUnavailableSkillIds).Distinct().ToList();
                Plugin.DiagWarning($"AUTO-SKILLS ACTIVE UNAVAILABLE|skills={string.Join(',', learnedButUnavailableSkillIds)}");
            }
            var hasUnusableSkill = ReadBool(InvokeRequiredInstance(talentData, "IsHasUnusableSkill"));
            var variantVerification = VerifyEquippedSkillVariants(
                hero, talentData, "AUTO-SKILLS", effectivePreferred.PreferredSkillIds);
            var variantSkillIds = variantVerification.Actual;
            var desiredBaseTalentId = preferred.SkillTalentIds
                .FirstOrDefault(id => IsBaseSkillDefinition(InvokeStatic("TableData", "getTTalentData", id)));
            var actualBaseTalentId = ReadNullableInt(saveHero, "baseSkillId") ?? 0;
            var desiredBaseDefinition = desiredBaseTalentId > 0
                ? InvokeStatic("TableData", "getTTalentData", desiredBaseTalentId)
                : null;
            var desiredBaseSkillId = ReadNullableInt(desiredBaseDefinition, "skillId") ?? 0;
            var actualBaseSkill = InvokeRequiredInstance(hero, "GetNowBaseSkillData");
            var actualBaseSkillId = ReadNullableInt(Read(actualBaseSkill, "tSkillData"), "id") ?? 0;
            var desiredBaseSavedLevel = desiredBaseTalentId > 0 && gridById.TryGetValue(desiredBaseTalentId, out var desiredBaseTalent)
                ? GetSavedTalentLevel(desiredBaseTalent)
                : 0;
            var baseSkillVerified = desiredBaseTalentId <= 0
                                    || (actualBaseTalentId == desiredBaseTalentId
                                        && desiredBaseSkillId > 0
                                        && actualBaseSkillId == desiredBaseSkillId);
            if (!baseSkillVerified)
                Plugin.DiagWarning($"AUTO-SKILLS BASE MISMATCH|talent={actualBaseTalentId}/{desiredBaseTalentId}|skill={actualBaseSkillId}/{desiredBaseSkillId}|savedLevel={desiredBaseSavedLevel}");
            var unlearnedMasteryTalentIds = relevantMasteryTalentIds
                .Where(id => gridById.TryGetValue(id, out var talent) && GetSavedTalentLevel(talent) <= 0)
                .Distinct().ToList();
            if (unlearnedMasteryTalentIds.Count > 0)
                Plugin.DiagWarning($"AUTO-SKILLS MASTERY UNLEARNED|talents={string.Join(',', unlearnedMasteryTalentIds)}|remaining={remaining}");
            var talentLevelMismatches = expectedSavedLevels.Select(entry =>
                {
                    if (!gridById.TryGetValue(entry.Key, out var talent))
                        return $"{entry.Key}:missing";
                    var actualSaved = GetSavedTalentLevel(talent);
                    var expectedEffective = Math.Min(GetTalentLevelCap(talent),
                        GetTalentBaseLevelRequired(talent) + entry.Value);
                    var actualEffective = GetTalentLevel(talent);
                    return actualSaved == entry.Value && actualEffective == expectedEffective
                        ? string.Empty
                        : $"{entry.Key}:{actualSaved}/{entry.Value}@{actualEffective}/{expectedEffective}";
                })
                .Where(value => value.Length > 0).ToList();
            if (talentLevelMismatches.Count > 0)
                Plugin.DiagWarning($"AUTO-SKILLS LEVEL MISMATCH|{string.Join(',', talentLevelMismatches)}");
            var exactTargetMismatches = exactTargetLevels.Select(entry =>
                {
                    if (!gridById.TryGetValue(entry.Key, out var talent)) return $"{entry.Key}:missing";
                    var actualSaved = GetSavedTalentLevel(talent);
                    return actualSaved == entry.Value ? string.Empty : $"{entry.Key}:{actualSaved}/{entry.Value}";
                })
                .Where(value => value.Length > 0).ToList();
            var skillLevelStates = availableBaseSkillTalentIds.Concat(activeSkillTalentIds)
                .Where(gridById.ContainsKey).Distinct()
                .Select(talentId =>
                {
                    var talent = gridById[talentId];
                    var definition = Read(talent, "tTalentData");
                    var skillId = ReadNullableInt(definition, "skillId") ?? 0;
                    var skillRow = skillId > 0 ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
                    var name = Clean(ReadString(skillRow, "name")
                                     ?? EnglishName(skillRow, string.Empty)
                                     ?? $"skill#{skillId}");
                    return $"{name}#{skillId}:saved={GetSavedTalentLevel(talent)}/effective={GetTalentLevel(talent)}/cap={GetTalentLevelCap(talent)}";
                }).ToList();
            if (exactTargetMismatches.Count > 0)
                Plugin.DiagWarning($"AUTO-SKILLS TARGET LEVEL MISMATCH|plan={effectivePreferred.PlanToken}|{string.Join(',', exactTargetMismatches)}");
            var transformComplete = transform.Matched >= transform.Target;
            // The action is advertised as automatic allocation, so any unspent
            // point makes the result partial—even if the only remaining native
            // targets are active skills deliberately excluded from this guide.
            // Do not report a focused subset as a fully completed allocation.
            var unspentPointsRemain = remaining > 0;
            var allocationLedgerExact = allocated == allocatedByPlan;
            var totalSpentBlood = Math.Max(0, bloodBefore - Convert.ToInt32(InvokeRequiredInstance(townData, "GetRes", bloodType) ?? bloodBefore, CultureInfo.InvariantCulture));
            var resetSpentBlood = Math.Max(0, totalSpentBlood - transform.SpentBlood);
            var resetBloodVerified = spentBefore > 0 ? resetSpentBlood == resetPrice : resetSpentBlood == 0;
            Plugin.DiagInfo($"AUTO-SKILLS PLAN|token={effectivePreferred.PlanToken}|focus={focus.English}|build={preferred.BuildName}|transform={transform.Attempts}:{transform.Matched}/{transform.Target}|transformNote={transform.Note}|baseSkillChanged={baseSkillChanged}:{baseSkillName}|baseTalent={actualBaseTalentId}/{desiredBaseTalentId}|baseSkill={actualBaseSkillId}/{desiredBaseSkillId}|baseSaved={desiredBaseSavedLevel}|skills={string.Join(';', skillLevelStates)}|learnedActive={string.Join(',', learnedActiveSkillIds.OrderBy(id => id))}|activeExact={activeSkillPostconditionExact}|usableSkills={string.Join(',', usableSkillIds.OrderBy(id => id))}|hasUnusableSkill={hasUnusableSkill}|variants={string.Join(',', variantSkillIds)}|variantExact={variantVerification.IsExact}|allocated={allocated}|planned={allocatedByPlan}|ledgerExact={allocationLedgerExact}|levelMismatches={string.Join(',', talentLevelMismatches)}|targetMismatches={string.Join(',', exactTargetMismatches)}|remaining={remaining}|unspent={unspentPointsRemain}|resetBlood={resetSpentBlood}/{(spentBefore > 0 ? resetPrice : 0)}|unlearnedMasteries={string.Join(',', unlearnedMasteryTalentIds)}|failedNodes={string.Join(',', failedNodes)}");
            var transformKo = transform.Target > 0 ? $" · 스킬 변환{(transform.Matched < transform.Target ? " 일부" : string.Empty)} {transform.Attempts}회({transform.Matched}/{transform.Target})" : string.Empty;
            var transformEn = transform.Target > 0 ? $" · {(transform.Matched < transform.Target ? "partially " : string.Empty)}transformed {transform.Attempts}× ({transform.Matched}/{transform.Target})" : string.Empty;
            var transformZh = transform.Target > 0 ? $" · 技能{(transform.Matched < transform.Target ? "部分" : string.Empty)}转换 {transform.Attempts} 次（{transform.Matched}/{transform.Target}）" : string.Empty;
            var variantKo = variantVerification.Expected.Count > 0 || variantSkillIds.Count > 0 ? $" · 장비 변환형 {variantSkillIds.Count}/{variantVerification.Expected.Count} 확인" : string.Empty;
            var variantEn = variantVerification.Expected.Count > 0 || variantSkillIds.Count > 0 ? $" · gear variants verified {variantSkillIds.Count}/{variantVerification.Expected.Count}" : string.Empty;
            var variantZh = variantVerification.Expected.Count > 0 || variantSkillIds.Count > 0 ? $" · 装备变体验证 {variantSkillIds.Count}/{variantVerification.Expected.Count}" : string.Empty;
            var transformNote = LocalizeTransformNote(transform.Note);
            var transformNoteSuffix = string.IsNullOrWhiteSpace(transformNote) ? string.Empty : $" · {transformNote}";
            var incompleteKo = new List<string>();
            var incompleteEn = new List<string>();
            var incompleteZhCn = new List<string>();
            var incompleteZhTw = new List<string>();
            if (missingPreferredSkillIds.Count > 0)
            {
                incompleteKo.Add($"추천 액티브 미학습 {string.Join(',', missingPreferredSkillIds)}");
                incompleteEn.Add($"active skills not learned {string.Join(',', missingPreferredSkillIds)}");
                incompleteZhCn.Add($"主动技能未学习 {string.Join(',', missingPreferredSkillIds)}");
                incompleteZhTw.Add($"主動技能未學習 {string.Join(',', missingPreferredSkillIds)}");
            }
            if (!activeSkillPostconditionExact)
            {
                incompleteKo.Add($"액티브 스킬 최종 검증 불일치 {string.Join(',', learnedActiveSkillIds.OrderBy(id => id))}/{string.Join(',', desiredActiveSkillIds.OrderBy(id => id))}");
                incompleteEn.Add($"active-skill final state mismatch {string.Join(',', learnedActiveSkillIds.OrderBy(id => id))}/{string.Join(',', desiredActiveSkillIds.OrderBy(id => id))}");
                incompleteZhCn.Add($"主动技能最终状态不匹配 {string.Join(',', learnedActiveSkillIds.OrderBy(id => id))}/{string.Join(',', desiredActiveSkillIds.OrderBy(id => id))}");
                incompleteZhTw.Add($"主動技能最終狀態不符 {string.Join(',', learnedActiveSkillIds.OrderBy(id => id))}/{string.Join(',', desiredActiveSkillIds.OrderBy(id => id))}");
            }
            if (!transformComplete)
            {
                incompleteKo.Add($"변환 목표 {transform.Matched}/{transform.Target}");
                incompleteEn.Add($"transform target {transform.Matched}/{transform.Target}");
                incompleteZhCn.Add($"转换目标 {transform.Matched}/{transform.Target}");
                incompleteZhTw.Add($"轉換目標 {transform.Matched}/{transform.Target}");
            }
            if (!baseSkillVerified)
            {
                incompleteKo.Add($"기본 스킬 {actualBaseTalentId}/{desiredBaseTalentId}");
                incompleteEn.Add($"base skill {actualBaseTalentId}/{desiredBaseTalentId}");
                incompleteZhCn.Add($"基础技能 {actualBaseTalentId}/{desiredBaseTalentId}");
                incompleteZhTw.Add($"基礎技能 {actualBaseTalentId}/{desiredBaseTalentId}");
            }
            if (unlearnedMasteryTalentIds.Count > 0)
            {
                incompleteKo.Add($"추천 마스터리 미학습 {string.Join(',', unlearnedMasteryTalentIds)}");
                incompleteEn.Add($"masteries not learned {string.Join(',', unlearnedMasteryTalentIds)}");
                incompleteZhCn.Add($"专精未学习 {string.Join(',', unlearnedMasteryTalentIds)}");
                incompleteZhTw.Add($"專精未學習 {string.Join(',', unlearnedMasteryTalentIds)}");
            }
            if (!variantVerification.IsExact)
            {
                incompleteKo.Add($"장비 변환형 불일치 -{string.Join(',', variantVerification.Missing)} +{string.Join(',', variantVerification.Unexpected)}");
                incompleteEn.Add($"gear variant mismatch -{string.Join(',', variantVerification.Missing)} +{string.Join(',', variantVerification.Unexpected)}");
                incompleteZhCn.Add($"装备变体不匹配 -{string.Join(',', variantVerification.Missing)} +{string.Join(',', variantVerification.Unexpected)}");
                incompleteZhTw.Add($"裝備變體不符 -{string.Join(',', variantVerification.Missing)} +{string.Join(',', variantVerification.Unexpected)}");
            }
            if (hasUnusableSkill)
            {
                incompleteKo.Add("현재 무기로 사용할 수 없는 학습 스킬 있음");
                incompleteEn.Add("a learned skill is unusable with the current weapons");
                incompleteZhCn.Add("当前武器无法使用某个已学习技能");
                incompleteZhTw.Add("目前武器無法使用某個已學習技能");
            }
            if (failedNodes.Count > 0)
            {
                incompleteKo.Add($"포인트 적용 실패 {string.Join(',', failedNodes)}");
                incompleteEn.Add($"point application failed {string.Join(',', failedNodes)}");
                incompleteZhCn.Add($"点数应用失败 {string.Join(',', failedNodes)}");
                incompleteZhTw.Add($"點數套用失敗 {string.Join(',', failedNodes)}");
            }
            if (previewFailedNodes.Count > 0)
            {
                incompleteKo.Add($"성능 미리보기 실패 {string.Join(',', previewFailedNodes)}");
                incompleteEn.Add($"performance preview failed {string.Join(',', previewFailedNodes)}");
                incompleteZhCn.Add($"性能预览失败 {string.Join(',', previewFailedNodes)}");
                incompleteZhTw.Add($"效能預覽失敗 {string.Join(',', previewFailedNodes)}");
            }
            if (!allocationLedgerExact)
            {
                incompleteKo.Add($"포인트 원장 불일치 {allocated}/{allocatedByPlan}");
                incompleteEn.Add($"point ledger mismatch {allocated}/{allocatedByPlan}");
                incompleteZhCn.Add($"点数记录不匹配 {allocated}/{allocatedByPlan}");
                incompleteZhTw.Add($"點數記錄不符 {allocated}/{allocatedByPlan}");
            }
            if (talentLevelMismatches.Count > 0)
            {
                incompleteKo.Add($"특성 레벨 검증 불일치 {string.Join(',', talentLevelMismatches)}");
                incompleteEn.Add($"talent-level verification mismatch {string.Join(',', talentLevelMismatches)}");
                incompleteZhCn.Add($"天赋等级验证不匹配 {string.Join(',', talentLevelMismatches)}");
                incompleteZhTw.Add($"天賦等級驗證不符 {string.Join(',', talentLevelMismatches)}");
            }
            if (exactTargetMismatches.Count > 0)
            {
                incompleteKo.Add($"공동 계획 목표 레벨 불일치 {string.Join(',', exactTargetMismatches)}");
                incompleteEn.Add($"shared-plan target-level mismatch {string.Join(',', exactTargetMismatches)}");
                incompleteZhCn.Add($"联合方案目标等级不匹配 {string.Join(',', exactTargetMismatches)}");
                incompleteZhTw.Add($"聯合方案目標等級不符 {string.Join(',', exactTargetMismatches)}");
            }
            if (!resetBloodVerified)
            {
                incompleteKo.Add($"초기화 피 검증 실패 {resetSpentBlood}/{(spentBefore > 0 ? resetPrice : 0)}");
                incompleteEn.Add($"reset Blood mismatch {resetSpentBlood}/{(spentBefore > 0 ? resetPrice : 0)}");
                incompleteZhCn.Add($"重置鲜血不匹配 {resetSpentBlood}/{(spentBefore > 0 ? resetPrice : 0)}");
                incompleteZhTw.Add($"重設鮮血不符 {resetSpentBlood}/{(spentBefore > 0 ? resetPrice : 0)}");
            }
            if (unspentPointsRemain)
            {
                incompleteKo.Add($"미사용 포인트 {remaining}");
                incompleteEn.Add($"{remaining} points remain unspent");
                incompleteZhCn.Add($"仍有 {remaining} 点未分配");
                incompleteZhTw.Add($"仍有 {remaining} 點未分配");
            }
            if (!transform.CleanupSucceeded)
            {
                incompleteKo.Add("임시 신전 설정 복구 실패");
                incompleteEn.Add("temporary shrine settings were not restored");
                incompleteZhCn.Add("临时神殿设置未恢复");
                incompleteZhTw.Add("臨時神殿設定未復原");
            }
            var verifiedComplete = incompleteEn.Count == 0;
            var completionKo = verifiedComplete ? "스킬 집중 분배 검증 완료" : $"일부 적용 · {string.Join("; ", incompleteKo)}";
            var completionEn = verifiedComplete ? "Focused skill allocation verified" : $"Partially applied · {string.Join("; ", incompleteEn)}";
            var completionZhCn = verifiedComplete ? "技能集中分配验证完成" : $"部分应用 · {string.Join("; ", incompleteZhCn)}";
            var completionZhTw = verifiedComplete ? "技能集中分配驗證完成" : $"部分套用 · {string.Join("; ", incompleteZhTw)}";
            var skillLevelSummary = string.Join(" · ", skillLevelStates);
            var planTokenSuffix = string.IsNullOrWhiteSpace(effectivePreferred.PlanToken)
                ? string.Empty
                : $" · plan {effectivePreferred.PlanToken}";
            message = UiText.L(
                $"{completionKo} · {focus.Localized} · {preferred.BuildName}{transformKo}{variantKo} · {allocated:N0}포인트 · {skillLevelSummary}{planTokenSuffix} · 변환 피 {transform.SpentBlood:N0} / 초기화 피 {resetSpentBlood:N0}{(remaining > 0 ? $" · 미사용 {remaining:N0}" : string.Empty)}{transformNoteSuffix}",
                $"{completionEn} · {focus.English} · {preferred.BuildName}{transformEn}{variantEn} · {allocated:N0} points · {skillLevelSummary}{planTokenSuffix} · transform Blood {transform.SpentBlood:N0} / reset Blood {resetSpentBlood:N0}{(remaining > 0 ? $" · {remaining:N0} unspent" : string.Empty)}{transformNoteSuffix}",
                $"{completionZhCn} · {focus.Localized} · {preferred.BuildName}{transformZh}{variantZh} · {allocated:N0} 点 · {skillLevelSummary}{planTokenSuffix} · 转换鲜血 {transform.SpentBlood:N0} / 重置鲜血 {resetSpentBlood:N0}{(remaining > 0 ? $" · 剩余 {remaining:N0}" : string.Empty)}{transformNoteSuffix}",
                $"{completionZhTw} · {focus.Localized} · {preferred.BuildName}{transformZh}{variantZh} · {allocated:N0} 點 · {skillLevelSummary}{planTokenSuffix} · 轉換鮮血 {transform.SpentBlood:N0} / 重設鮮血 {resetSpentBlood:N0}{(remaining > 0 ? $" · 剩餘 {remaining:N0}" : string.Empty)}{transformNoteSuffix}");
            return verifiedComplete;
        }
        catch (Exception error)
        {
            message = UiText.L($"스킬 자동 분배 실패: {error.GetBaseException().Message}", $"Skill allocation failed: {error.GetBaseException().Message}", $"技能自动分配失败：{error.GetBaseException().Message}", $"技能自動分配失敗：{error.GetBaseException().Message}")
                      + DescribeSkillMutationJournal(journalTransformAttempts, journalTransformBlood, journalResetInvoked, journalResetSucceeded, journalResetPrice, journalBaseChanged, journalAllocationStart, journalSaveHero);
            return false;
        }
    }

    private static string DescribeSkillMutationJournal(int transformAttempts, int transformBlood, bool resetInvoked,
        bool resetSucceeded, int resetPrice, bool baseChanged, int allocationStart, object? saveHero)
    {
        var allocated = allocationStart >= 0 && saveHero is not null
            ? Math.Max(0, allocationStart - (ReadNullableInt(saveHero, "talentRemainPoint") ?? allocationStart))
            : 0;
        if (transformAttempts <= 0 && !resetInvoked && !baseChanged && allocated <= 0) return string.Empty;
        var transformText = transformBlood >= 0 ? transformBlood.ToString("N0", CultureInfo.CurrentCulture) : UiText.L("확인 불가", "unknown", "未知", "未知");
        return UiText.L(
            $" · 변경 기록: 스킬 변환 {transformAttempts}회/피 {transformText}, 초기화 {(resetInvoked ? resetSucceeded ? $"성공(예상 피 {resetPrice:N0})" : "호출됨·성공 미확인" : "안 함")}, 기본 스킬 {(baseChanged ? "변경" : "유지")}, 확인된 배분 {allocated:N0}포인트",
            $" · mutation record: transforms {transformAttempts}/Blood {transformText}, reset {(resetInvoked ? resetSucceeded ? $"succeeded (expected Blood {resetPrice:N0})" : "invoked; success unverified" : "not run")}, base skill {(baseChanged ? "changed" : "unchanged")}, verified allocation {allocated:N0} point(s)",
            $" · 变更记录：技能转换 {transformAttempts} 次/鲜血 {transformText}，重置 {(resetInvoked ? resetSucceeded ? $"成功（预计鲜血 {resetPrice:N0}）" : "已调用·成功未确认" : "未执行")}，基础技能{(baseChanged ? "已更改" : "未更改")}，已确认分配 {allocated:N0} 点",
            $" · 變更記錄：技能轉換 {transformAttempts} 次/鮮血 {transformText}，重設 {(resetInvoked ? resetSucceeded ? $"成功（預計鮮血 {resetPrice:N0}）" : "已呼叫·成功未確認" : "未執行")}，基礎技能{(baseChanged ? "已變更" : "未變更")}，已確認分配 {allocated:N0} 點");
    }

#if PATHOFIDLE_RUNTIME_TEST
    internal static bool TryGetRuntimeJointBuildTestReadiness(out string reason)
    {
        try
        {
            var hero = GetSelectedHero();
            if (hero is null)
            {
                reason = "selected hero is not loaded";
                return false;
            }
            if (ReadBool(InvokeRequiredInstance(hero, "IsAdventureBusy")))
            {
                reason = "selected hero is adventuring";
                return false;
            }
            if (Read(hero, "saveHeroData") is null || Read(hero, "heroTalentData") is null)
            {
                reason = "selected hero save/talent data is not loaded";
                return false;
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            reason = error.GetBaseException().Message;
            return false;
        }
    }

    internal static bool TryInvokeRuntimeTestContinue(out bool invocationAttempted, out string reason)
    {
        invocationAttempted = false;
        try
        {
            var mainSceneType = GameType("MainScene");
            if (mainSceneType is null)
            {
                reason = "MainScene type is not loaded";
                return false;
            }
            var continueMethod = mainSceneType.GetMethod(
                "OnContinueBtnClick", BindingFlags.Public | BindingFlags.Instance);
            if (continueMethod is null)
            {
                reason = "MainScene.OnContinueBtnClick is unavailable";
                return false;
            }
            var findObjects = typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "FindObjectsOfTypeAll"
                    && method.IsGenericMethodDefinition
                    && method.GetParameters().Length == 0);
            if (findObjects is null)
            {
                reason = "Resources.FindObjectsOfTypeAll is unavailable";
                return false;
            }
            var objects = findObjects.MakeGenericMethod(mainSceneType).Invoke(null, null);
            foreach (var mainScene in ReadSequence(objects))
            {
                var gameObject = Read(mainScene, "gameObject");
                if (!ReadBool(Read(gameObject, "activeInHierarchy"))) continue;
                invocationAttempted = true;
                continueMethod.Invoke(mainScene, null);
                reason = string.Empty;
                return true;
            }
            reason = "active MainScene is not ready";
            return false;
        }
        catch (Exception error)
        {
            reason = $"continue invocation failed: {error.GetBaseException().Message}";
            return false;
        }
    }
#endif

    private static object? GetSelectedHero()
    {
        var dataManager = ReadStatic("Game", "dataMgr");
        var seasonData = Read(dataManager, "nowSeasonData");
        return Read(Read(seasonData, "lordData"), "nowHeroData");
    }

    private sealed record HeroFocus(string Key, string Localized, string English, string[] Keywords, bool IsManual = false);

    public static string NormalizeBuildTheme(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "physical" or "elemental" or "fire" or "ice" or "lightning" or "minion" or "bleed" or "corrosion" or "crit" or "support" or "defense"
            ? normalized
            : "auto";
    }

    public static string GetSelectedHeroBuildTheme()
    {
        try
        {
            var hero = GetSelectedHero();
            return hero is null ? NormalizeBuildTheme(Plugin.AutoBuildTheme.Value) : GetHeroBuildTheme(hero);
        }
        catch
        {
            return NormalizeBuildTheme(Plugin.AutoBuildTheme.Value);
        }
    }

    public static void SetSelectedHeroBuildTheme(string? value)
    {
        var normalized = NormalizeBuildTheme(value);
        var hero = GetSelectedHero();
        if (hero is null)
        {
            Plugin.AutoBuildTheme.Value = normalized;
            return;
        }
        var uniqueId = ReadNullableInt(Read(hero, "saveHeroData"), "uniqueId") ?? 0;
        JointSkillPlansByHero.Remove(GetJointSkillPlanHeroKey(hero));
        if (uniqueId <= 0)
        {
            Plugin.AutoBuildTheme.Value = normalized;
            return;
        }

        var themes = ParseHeroBuildThemes(Plugin.AutoBuildHeroThemes.Value);
        themes[uniqueId] = normalized;
        Plugin.AutoBuildHeroThemes.Value = string.Join(";", themes.OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key.ToString(CultureInfo.InvariantCulture)}={entry.Value}"));
    }

    private static string GetHeroBuildTheme(object hero)
    {
        var uniqueId = ReadNullableInt(Read(hero, "saveHeroData"), "uniqueId") ?? 0;
        if (uniqueId > 0 && ParseHeroBuildThemes(Plugin.AutoBuildHeroThemes.Value).TryGetValue(uniqueId, out var saved))
            return NormalizeBuildTheme(saved);
        return NormalizeBuildTheme(Plugin.AutoBuildTheme.Value);
    }

    private static Dictionary<int, string> ParseHeroBuildThemes(string? encoded)
    {
        var result = new Dictionary<int, string>();
        foreach (var entry in (encoded ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator >= entry.Length - 1) continue;
            if (!int.TryParse(entry[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uniqueId) || uniqueId <= 0) continue;
            result[uniqueId] = NormalizeBuildTheme(entry[(separator + 1)..]);
        }
        return result;
    }

    private static HeroFocus ResolveHeroFocus(object hero, string? requestedTheme)
    {
        var normalized = NormalizeBuildTheme(requestedTheme);
        if (normalized == "auto"
            && JointSkillPlansByHero.TryGetValue(GetJointSkillPlanHeroKey(hero), out var remembered)
            && string.Equals(remembered.GearFingerprint, GetCurrentGearFingerprint(hero), StringComparison.Ordinal))
            return remembered.FocusKey == "hybrid"
                ? new HeroFocus("hybrid", UiText.L("균형·혼합", "Balanced / Hybrid", "均衡/混合", "均衡/混合"),
                    "Balanced / Hybrid", PhysicalWords.Concat(ElementalWords).ToArray())
                : ResolveHeroFocus(hero, remembered.FocusKey) with { IsManual = false };
        return normalized switch
        {
            "physical" => new HeroFocus("physical", UiText.L("물리·무예", "Physical / Martial", "物理/武技", "物理/武技"), "Physical / Martial", PhysicalWords, true),
            "elemental" => new HeroFocus("elemental", UiText.L("원소·주문", "Elemental / Spell", "元素/法术", "元素/法術"), "Elemental / Spell", ElementalWords, true),
            "fire" => new HeroFocus("fire", UiText.L("화염·연소", "Fire / Burn", "火焰/燃烧", "火焰/燃燒"), "Fire / Burn", FireWords, true),
            "ice" => new HeroFocus("ice", UiText.L("냉기·빙결", "Ice / Freeze", "冰霜/冻结", "冰霜/凍結"), "Ice / Freeze", IceWords, true),
            "lightning" => new HeroFocus("lightning", UiText.L("번개·감전", "Lightning / Shock", "闪电/感电", "閃電/感電"), "Lightning / Shock", LightningWords, true),
            "minion" => new HeroFocus("minion", UiText.L("소환수·하수인", "Minion / Summon", "召唤物/仆从", "召喚物/僕從"), "Minion / Summon", MinionWords, true),
            "bleed" => new HeroFocus("bleed", UiText.L("출혈·상처", "Bleed / Wound", "流血/创伤", "流血/創傷"), "Bleed / Wound", BleedWords, true),
            "corrosion" => new HeroFocus("corrosion", UiText.L("부식·중독", "Corrosion / Poison", "腐蚀/中毒", "腐蝕/中毒"), "Corrosion / Poison", CorrosionWords, true),
            "crit" => new HeroFocus("crit", UiText.L("치명타", "Critical Strike", "暴击", "暴擊"), "Critical Strike", CriticalWords, true),
            "support" => new HeroFocus("support", UiText.L("지원·오라", "Support / Aura", "辅助/光环", "輔助/光環"), "Support / Aura", SupportWords, true),
            "defense" => new HeroFocus("defense", UiText.L("생존·방어", "Defense / Survival", "防御/生存", "防禦/生存"), "Defense / Survival", TankWords, true),
            _ => DetermineHeroFocus(hero)
        };
    }

    private static HeroFocus DetermineHeroFocus(object hero)
    {
        var job = Read(hero, "tHeroJobData");
        var roleTextParts = new List<string>
        {
            ReadString(job, "name") ?? string.Empty,
            ReadString(job, "des") ?? string.Empty,
            EnglishName(job, string.Empty) ?? string.Empty,
            EnglishText(job, "_des", string.Empty) ?? string.Empty
        };
        var nativeRoles = new List<(int SkillId, bool IsBase, NativeSkillRoleProfile Role)>();
        var seenSkillIds = new HashSet<int>();
        void AddNativeRole(object? skill, int fallbackLevel, bool isBase)
        {
            if (skill is null) return;
            var row = Read(skill, "tSkillData") ?? skill;
            var skillId = ReadNullableInt(row, "id") ?? 0;
            if (skillId <= 0 || !seenSkillIds.Add(skillId)) return;
            var level = Math.Max(1, ReadNullableInt(skill, "level") ?? fallbackLevel);
            try
            {
                var role = ReadNativeSkillRoleProfile(hero, skillId, level);
                nativeRoles.Add((skillId, isBase, role));
            }
            catch (Exception error)
            {
                // Auto inference is read-only. An unavailable native preview is
                // absence of evidence, never permission to infer an element from
                // a translated verb such as "fire".
                Plugin.DiagDebug($"AUTO-FOCUS SKILL PREVIEW SKIPPED|skill={skillId}|{error.GetBaseException().Message}");
            }
        }

        var currentBaseSkill = InvokeInstance(hero, "GetNowBaseSkillData");
        AddNativeRole(currentBaseSkill, 1, true);
        try
        {
            var talentData = Read(hero, "heroTalentData");
            var activeTalents = ReadValues(Read(talentData, "talentDic")).Concat(
                    ReadList(Read(talentData, "extraTalentList")).Where(talent => !ReadBool(Read(talent, "isRuneWords"))))
                .DistinctBy(value => NativeObjectKey(value, value));
            foreach (var talent in activeTalents)
            {
                if (Convert.ToInt32(InvokeInstance(talent, "GetLevel") ?? 0, CultureInfo.InvariantCulture) <= 0) continue;
                var definition = Read(talent, "tTalentData");
                var skill = Read(talent, "skillData");
                var skillRow = Read(skill, "tSkillData");
                var info = Read(skill, "tSkillInfoData");
                var masteryData = Read(talent, "masteryData");
                var mastery = Read(masteryData, "tMasteryData");
                AddNativeRole(skill, GetTalentLevel(talent), false);
                // Text is retained only as secondary evidence for role themes.
                // Elemental themes are derived exclusively from native damage
                // types below, so ordinary UI verbs cannot become Fire/Ice/etc.
                roleTextParts.Add(EnglishName(definition, string.Empty) ?? string.Empty);
                roleTextParts.Add(EnglishName(skillRow, string.Empty) ?? string.Empty);
                roleTextParts.Add(EnglishText(info, "_des", string.Empty) ?? string.Empty);
                roleTextParts.Add(EnglishName(mastery, string.Empty) ?? string.Empty);
                foreach (var affix in ReadList(Read(masteryData, "affixList")))
                {
                    var effectType = ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0;
                    if (effectType is 1 or 3) roleTextParts.Add(GetAffixSearchText(affix));
                }
            }
        }
        catch { }

        HeroFocus AutoFocus(string key) => ResolveHeroFocus(hero, key) with { IsManual = false };
        var baseRole = nativeRoles.FirstOrDefault(entry => entry.IsBase).Role ?? EmptyNativeSkillRole();
        var activeRoles = nativeRoles.Where(entry => !entry.IsBase).Select(entry => entry.Role).ToList();
        var package = BuildSharedSkillPackage(baseRole, activeRoles);
        var roleText = Clean(string.Join(" ", roleTextParts)).ToLowerInvariant();
        var directDamage = package.DamageByType.Values.Where(value => value > 0d).Sum();
        var damageSignal = Math.Log10(1d + directDamage);
        var minionNative = Math.Max(0d,
            package.SummonDamage + package.SummonSurvival * 0.35d + package.AbilityMinion);
        var supportNative = Math.Max(0d,
            package.Heal * 1.4d + package.Shield + package.AbilitySupport);
        var defenseNative = Math.Max(0d,
            package.Shield * 1.5d + package.Heal + package.AbilityDefense);
        var minionSignal = Math.Log10(1d + minionNative);
        minionSignal += Math.Min(1.5d, KeywordScore(roleText, MinionWords) * 0.18d);
        var supportSignal = Math.Log10(1d + supportNative)
                            + Math.Min(1.25d, KeywordScore(roleText, SupportWords) * 0.15d);
        var defenseSignal = Math.Log10(1d + defenseNative)
                            + Math.Min(1.25d, KeywordScore(roleText, TankWords) * 0.15d);
        var bestRole = new[]
        {
            (Key: "minion", Score: minionSignal, Native: minionNative > 0d),
            (Key: "support", Score: supportSignal, Native: supportNative > 0d),
            (Key: "defense", Score: defenseSignal, Native: defenseNative > 0d)
        }.Where(entry => entry.Native)
            .OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Key).FirstOrDefault();
        if (bestRole.Score > 0.75d && bestRole.Score >= damageSignal + 0.45d)
            return AutoFocus(bestRole.Key);

        var nativeDamage = new Dictionary<string, double>
        {
            ["physical"] = Enumerable.Range(1, 3).Sum(id => package.DamageByType.GetValueOrDefault(id)),
            ["fire"] = package.DamageByType.GetValueOrDefault(4),
            ["ice"] = package.DamageByType.GetValueOrDefault(5),
            ["lightning"] = package.DamageByType.GetValueOrDefault(6),
            ["bleed"] = package.DamageByType.GetValueOrDefault(7),
            ["corrosion"] = package.DamageByType.GetValueOrDefault(8)
        };
        var rankedDamage = nativeDamage.Where(entry => entry.Value > 0d)
            .OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key).ToList();
        if (rankedDamage.Count > 0)
        {
            var best = rankedDamage[0];
            var runnerUp = rankedDamage.Skip(1).Select(entry => entry.Value).DefaultIfEmpty(0d).First();
            if (best.Value >= runnerUp * 1.10d) return AutoFocus(best.Key);
            var elementalDamage = nativeDamage["fire"] + nativeDamage["ice"] + nativeDamage["lightning"];
            if (elementalDamage > nativeDamage["physical"])
                return AutoFocus("elemental");
            if (nativeDamage["physical"] > 0d) return AutoFocus("physical");
        }

        // No native damage row means no element claim. In particular, generic
        // English verbs are deliberately not an elemental fallback.
        return new HeroFocus("hybrid", UiText.L("균형·혼합", "Balanced / Hybrid", "均衡/混合", "均衡/混合"), "Balanced / Hybrid", PhysicalWords.Concat(ElementalWords).ToArray());
    }

    private static void AddSkillText(object? skill, List<string> parts)
    {
        if (skill is null) return;
        var row = Read(skill, "tSkillData") ?? skill;
        var info = Read(skill, "tSkillInfoData")
                   ?? (ReadNullableInt(row, "infoId") is > 0 and var infoId ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null);
        parts.Add(ReadString(row, "name") ?? string.Empty);
        parts.Add(EnglishName(row, string.Empty) ?? string.Empty);
        parts.Add(ReadString(info, "des") ?? string.Empty);
        parts.Add(EnglishText(info, "_des", string.Empty) ?? string.Empty);
    }

    private static object? GetPreferredBuild(object hero, HeroFocus? focus = null)
    {
        var jobId = ReadNullableInt(Read(hero, "saveHeroData"), "jobId") ?? ReadNullableInt(Read(hero, "tHeroJobData"), "id") ?? 0;
        focus ??= DetermineHeroFocus(hero);
        var builds = ReadValues(ReadStatic("TableData", "TBuildsDict"))
            .Where(build => ReadNullableInt(build, "jobId") == jobId).ToList();
        if (builds.Count == 0) return null;
        var unlocked = builds.Where(build => !ReadRequiredBoolProperty(build, "isLock")).ToList();
        // A locked guide can describe skills, rows or equipment the current
        // hero cannot legally use. Never select one merely because no guide is
        // unlocked yet.
        builds = unlocked;
        if (builds.Count == 0) return null;

        var saveHero = Read(hero, "saveHeroData");
        var baseTalentId = ReadNullableInt(saveHero, "baseSkillId") ?? 0;
        var invested = ReadValues(Read(Read(hero, "heroTalentData"), "talentDic"))
            .Where(talent => GetSpentTalentPoints(talent) > 0 || GetTalentLevel(talent) > 0)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();

        var ranked = builds.Select(build =>
            {
                var skillIds = ReadRequiredSequenceProperty(build, "skillArr").Select(ToInt).Where(id => id > 0).ToList();
                var masteryIds = ReadRequiredSequenceProperty(build, "masteryArr").Select(ToInt).Where(id => id > 0).ToList();
                var guideIds = skillIds.Concat(masteryIds).ToHashSet();
                var textParts = new List<string>
                {
                    ReadString(build, "name") ?? string.Empty,
                    ReadString(build, "des") ?? string.Empty,
                    EnglishName(build, string.Empty) ?? string.Empty,
                    EnglishText(build, "_des", string.Empty) ?? string.Empty
                };
                foreach (var talentId in guideIds)
                    textParts.Add(GetTalentDefinitionText(InvokeStatic("TableData", "getTTalentData", talentId)));
                var text = Clean(string.Join(" ", textParts)).ToLowerInvariant();
                var themeEvidence = KeywordScore(text, focus.Keywords);
                var score = themeEvidence * (focus.IsManual ? 1500d : 520d);
                // A manually selected theme is an instruction to change builds.
                // Letting the currently invested nodes dominate here creates a
                // circular choice (for example, an old Lightning setup keeps
                // winning after the user explicitly selects Fire). In Auto mode
                // the existing build remains useful evidence for choosing among
                // otherwise similar guides.
                if (!focus.IsManual) score += guideIds.Count(invested.Contains) * 1150d;
                var buildBaseIds = skillIds.Where(id => IsBaseSkillDefinition(InvokeStatic("TableData", "getTTalentData", id))).ToHashSet();
                if (!focus.IsManual && baseTalentId > 0 && buildBaseIds.Contains(baseTalentId)) score += 3600d;
                else if (!focus.IsManual && baseTalentId > 0 && buildBaseIds.Count > 0) score -= 2600d;
                score -= (ReadNullableInt(build, "index") ?? 0) * 0.01d;
                return (Build: build, Score: score, ThemeEvidence: themeEvidence);
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => ReadNullableInt(entry.Build, "index") ?? int.MaxValue)
            .ToList();
        var winner = ranked.FirstOrDefault();
        // A manual theme is an explicit constraint. Returning the first guide
        // on a zero-keyword tie used to label unrelated guides as Fire/Defense
        // and then reset the hero around a build that did not support the
        // selected theme.
        if (winner.Build is null || (focus.IsManual && winner.ThemeEvidence <= 0)) return null;
        return winner.Build;
    }

    private static PreferredTalentPlan GetPreferredTalentPlan(object hero, HeroFocus focus)
    {
        var build = GetPreferredBuild(hero, focus);
        var skillTalentIds = build is null
            ? new List<int>()
            : ReadRequiredSequenceProperty(build, "skillArr").Select(ToInt).Where(id => id > 0).Distinct().ToList();
        var masteryTalentIds = build is null
            ? new List<int>()
            : ReadRequiredSequenceProperty(build, "masteryArr").Select(ToInt).Where(id => id > 0).Distinct().ToList();
        var preferredSkillIds = skillTalentIds
            .Select(id => InvokeStatic("TableData", "getTTalentData", id))
            .Select(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var buildName = build is null
            ? UiText.L("사용자 빌드", "Custom build", "自定义流派", "自訂流派")
            : Clean(ReadString(build, "name") ?? EnglishName(build, UiText.L("추천 빌드", "Recommended build", "推荐流派", "推薦流派")) ?? UiText.L("추천 빌드", "Recommended build", "推荐流派", "推薦流派"));
        return new PreferredTalentPlan(build, skillTalentIds, masteryTalentIds, preferredSkillIds, buildName);
    }

    private static PreferredTalentPlan GetPerformanceTalentPlan(object hero, HeroFocus focus, int totalTalentPoints)
    {
        var saveHero = ReadRequiredProperty(hero, "saveHeroData")
                       ?? throw new InvalidOperationException("SaveHeroData is unavailable.");
        var jobId = ReadRequiredIntProperty(saveHero, "jobId");
        if (jobId <= 0) throw new InvalidOperationException("The hero job ID is unavailable.");

        var talentData = ReadRequiredProperty(hero, "heroTalentData")
                         ?? throw new InvalidOperationException("HeroTalentData is unavailable.");
        var gridTalents = ReadValues(ReadRequiredProperty(talentData, "talentDic"))
            .DistinctBy(talent => NativeObjectKey(talent, talent)).ToList();
        var gridById = BuildTalentGridById(gridTalents);

        // The native shrine pool is every same-job, type-1, non-base TTalent.
        // Do not narrow that pool with the current loadout, current skills, names,
        // or official build rows before all candidates have received the same
        // side-effect-free native 60-second preview.
        var baseRows = gridTalents
            .Where(talent => !IsTalentLockedRequired(talent))
            .Select(talent => Read(talent, "tTalentData"))
            .Where(IsBaseSkillDefinition).Cast<object>()
            .DistinctBy(row => ReadNullableInt(row, "id") ?? 0)
            .ToList();

        var currentActiveTalents = GetTransformableTalents(talentData)
            .Where(talent => !IsTalentLockedRequired(talent)).ToList();
        var currentActiveTalentIds = currentActiveTalents
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var preservedTalentIds = GetTransformableTalents(talentData)
            .Where(IsTalentUnreplaceable)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var heroLevel = ReadRequiredIntProperty(saveHero, "level");
        var activeRows = ReadValues(ReadStatic("TableData", "TTalentDict"))
            .Where(IsTransformableSkillDefinition).Cast<object>()
            .Where(row => (ReadNullableInt(row, "jobId") ?? 0) == jobId)
            .Where(row =>
            {
                var id = ReadNullableInt(row, "id") ?? 0;
                if (id <= 0 || (preservedTalentIds.Contains(id) && !currentActiveTalentIds.Contains(id))) return false;
                // Native FillActiveSkillTalentDicGaps applies this one special
                // restriction to the first slot of a level-one hero.
                return heroLevel > 1 || currentActiveTalentIds.Contains(id) || (ReadNullableInt(row, "floor") ?? 0) == 1;
            })
            .GroupBy(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(group => group.Key > 0)
            .Select(group => group.OrderBy(row => ReadNullableInt(row, "index") ?? int.MaxValue)
                .ThenBy(row => ReadNullableInt(row, "id") ?? int.MaxValue).First())
            .ToList();
        if (baseRows.Count == 0 || activeRows.Count == 0)
            throw new InvalidOperationException($"No compatible unlocked performance skill pool is available (base={baseRows.Count}, active={activeRows.Count}).");

        var objectiveAttr = CreateTalentNeutralObjectiveAttr(hero, gridTalents);
        const int defaultSkillLevel = 1;
        var objectiveScores = new Dictionary<int, double>();
        var rankableRows = new List<object>();
#if PATHOFIDLE_RUNTIME_TEST
        const int runtimeRejectionDetailLimit = 2200;
        var runtimeBaseTalentIds = baseRows
            .Select(row => ReadNullableInt(row, "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var runtimeRejectionDetails = new StringBuilder();
        var runtimeOmittedRejections = 0;
#endif
        void RecordCandidateRejection(int talentId, int skillId, string reason, bool? previewComplete = null)
        {
#if PATHOFIDLE_RUNTIME_TEST
            var compactFailure = Regex.Replace(reason, @"\s+", " ").Trim();
            if (compactFailure.Length > 180) compactFailure = compactFailure[..177] + "...";
            var rejection = $"{(runtimeBaseTalentIds.Contains(talentId) ? "base" : "active")}:talent={talentId},skill={skillId},reason={compactFailure}";
            var separatorLength = runtimeRejectionDetails.Length == 0 ? 0 : 3;
            if (runtimeRejectionDetails.Length + separatorLength + rejection.Length <= runtimeRejectionDetailLimit)
            {
                if (separatorLength > 0) runtimeRejectionDetails.Append(" | ");
                runtimeRejectionDetails.Append(rejection);
            }
            else
            {
                runtimeOmittedRejections++;
            }
#endif
            Plugin.DiagDebug(
                $"AUTO-SKILLS PERFORMANCE CANDIDATE REJECTED|talent={talentId}|skill={skillId}|rankable=false|previewComplete={previewComplete?.ToString() ?? "unavailable"}|{reason}");
        }

        foreach (var row in baseRows.Concat(activeRows))
        {
            var talentId = 0;
            var skillId = 0;
            try
            {
                talentId = ReadNullableInt(row, "id") ?? 0;
                skillId = ReadNullableInt(row, "skillId") ?? 0;
                if (talentId <= 0 || skillId <= 0) continue;
                var role = ReadNativeSkillRoleProfile(hero, skillId, defaultSkillLevel, objectiveAttr, false);
                if (!IsNativeRoleRankable(role, focus))
                {
                    RecordCandidateRejection(
                        talentId, skillId, role.Failure ?? "no proven native output", role.IsComplete);
                    continue;
                }
                var score = ScoreNativeSkillRoleObjective(role, focus) * 10000d;
                if (!double.IsFinite(score))
                {
                    RecordCandidateRejection(talentId, skillId, "native objective is not finite", role.IsComplete);
                    continue;
                }
                objectiveScores[talentId] = score;
                rankableRows.Add(row);
            }
            catch (Exception error)
            {
                // A strict native preview can fail for one skill while other
                // candidates remain independently rankable. Reject only this row;
                // the pool-level check below still fails when no usable base or
                // active candidate survives.
                RecordCandidateRejection(
                    talentId, skillId, error.GetBaseException().Message, null);
            }
        }
        baseRows = baseRows.Where(row => rankableRows.Contains(row)).ToList();
        activeRows = activeRows.Where(row => rankableRows.Contains(row)).ToList();
        if (baseRows.Count == 0 || activeRows.Count == 0)
#if PATHOFIDLE_RUNTIME_TEST
        {
            if (runtimeOmittedRejections > 0)
                runtimeRejectionDetails.Append($" | +{runtimeOmittedRejections} omitted");
            throw new InvalidOperationException(
                $"No rankable native performance preview remains (base={baseRows.Count}, active={activeRows.Count}); rejected=[{runtimeRejectionDetails}].");
        }
#else
            throw new InvalidOperationException(
                $"No rankable native performance preview remains (base={baseRows.Count}, active={activeRows.Count}).");
#endif
        var selectedBase = baseRows
            .OrderByDescending(row => objectiveScores[ReadNullableInt(row, "id") ?? 0])
            .ThenBy(row => ReadNullableInt(row, "index") ?? int.MaxValue)
            .First();
        var selectedBaseId = ReadNullableInt(selectedBase, "id") ?? 0;
        var skillTalentIds = new[] { selectedBaseId }
            .Concat(activeRows.OrderByDescending(row => objectiveScores[ReadNullableInt(row, "id") ?? 0])
                .Select(row => ReadNullableInt(row, "id") ?? 0))
            .Where(id => id > 0).Distinct().ToList();
        var skillIds = skillTalentIds.Select(id => InvokeStatic("TableData", "getTTalentData", id))
            .Select(row => ReadNullableInt(row, "skillId") ?? 0).Where(id => id > 0).ToHashSet();
        var objectiveSkillIds = new[] { ReadNullableInt(selectedBase, "skillId") ?? 0 }
            .Concat(activeRows.OrderByDescending(row => objectiveScores[ReadNullableInt(row, "id") ?? 0])
                .Take(Math.Max(1, currentActiveTalents.Count))
                .Select(row => ReadNullableInt(row, "skillId") ?? 0))
            .Where(id => id > 0).Distinct().ToList();

        var masteryRows = gridTalents
            .Where(talent => !IsTalentLockedRequired(talent))
            .Where(talent => (ReadNullableInt(Read(talent, "tTalentData"), "type") ?? 0) == 2)
            .Where(talent => (ReadNullableInt(Read(talent, "tTalentData"), "masteryId") ?? 0) > 0)
            .Where(talent => GetTalentLevelCap(talent) - GetTalentBaseLevelRequired(talent) > 0)
            .ToList();
        var masteryTalentIds = masteryRows
            .Select(talent =>
            {
                var id = ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0;
                var score = ScoreMasteryTalentCandidateForObjective(
                    hero, talent, focus, objectiveAttr, objectiveSkillIds);
                objectiveScores[id] = score;
                return (Id: id, Score: score, Floor: ReadNullableInt(Read(talent, "tTalentData"), "floor") ?? int.MaxValue);
            })
            // A zero/negative mastery is not merely a low priority: it does not
            // help this hero's selected objective. Leaving it in the required
            // plan made the allocator spread points across unrelated branches.
            .Where(entry => entry.Id > 0 && double.IsFinite(entry.Score) && entry.Score > 0.000001d)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Floor)
            .Select(entry => entry.Id)
            // Every listed mastery is later treated as required. Keep the plan
            // feasible by reserving the minimum one point for each selected
            // skill and never listing more mastery nodes than the point budget
            // can actually activate.
            .Take(Math.Max(0, totalTalentPoints - objectiveSkillIds.Count))
            .ToList();
        var label = UiText.L("성능 목표", "Performance objective", "性能目标", "效能目標");
        Plugin.DiagInfo($"AUTO-SKILLS PERFORMANCE POOL|job={jobId}|focus={focus.English}|base={selectedBaseId}|activeCandidates={activeRows.Count}|scores={string.Join(',', skillTalentIds.Take(12).Select(id => $"{id}:{objectiveScores[id]:0.0}"))}");
        return new PreferredTalentPlan(null, skillTalentIds, masteryTalentIds, skillIds, label, objectiveScores);
    }

    private static PreferredTalentPlan RebuildMasteryPlanForSelectedSkills(
        object hero,
        HeroFocus focus,
        int totalTalentPoints,
        PreferredTalentPlan preferred,
        object? objectiveAttrOverride = null,
        IReadOnlyCollection<GearCandidate>? candidateItems = null)
    {
        var talentData = ReadRequiredProperty(hero, "heroTalentData")
                         ?? throw new InvalidOperationException("HeroTalentData is unavailable.");
        var gridTalents = ReadValues(ReadRequiredProperty(talentData, "talentDic"))
            .DistinctBy(talent => NativeObjectKey(talent, talent)).ToList();
        var selectedSkillIds = preferred.PreferredSkillIds.Where(id => id > 0).Distinct().ToList();
        if (selectedSkillIds.Count == 0) return preferred with { MasteryTalentIds = new List<int>() };
        var mandatoryActivePointCount = preferred.SkillTalentIds
            .Select(id => InvokeStatic("TableData", "getTTalentData", id))
            .Where(IsTransformableSkillDefinition)
            .Select(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(id => id > 0).Distinct().Count();

        var objectiveAttr = objectiveAttrOverride ?? CreateTalentNeutralObjectiveAttr(hero, gridTalents);
        var objectiveScores = preferred.ObjectiveScores is null
            ? new Dictionary<int, double>()
            : new Dictionary<int, double>(preferred.ObjectiveScores);
        var rankedRows = gridTalents
            .Where(talent => !IsTalentLockedRequired(talent))
            .Where(talent => (ReadNullableInt(Read(talent, "tTalentData"), "type") ?? 0) == 2)
            .Where(talent => (ReadNullableInt(Read(talent, "tTalentData"), "masteryId") ?? 0) > 0)
            .Where(talent => GetTalentLevelCap(talent) - GetTalentBaseLevelRequired(talent) > 0)
            .Select(talent =>
            {
                var definition = Read(talent, "tTalentData");
                var id = ReadNullableInt(definition, "id") ?? 0;
                var score = ScoreMasteryTalentCandidateForObjective(
                    hero, talent, focus, objectiveAttr, selectedSkillIds, candidateItems);
                objectiveScores[id] = score;
                return (Id: id, Score: score, Floor: ReadNullableInt(definition, "floor") ?? int.MaxValue);
            })
            .Where(entry => entry.Id > 0 && double.IsFinite(entry.Score) && entry.Score > 0.000001d)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Floor)
            .ToList();
        // Native cap previews rank the mastery pool for this exact gear/skill
        // package. Bound the expensive joint level search while retaining floor
        // diversity so one branch cannot occupy every slot in the shortlist.
        // Base and equipment-granted skills participate in scoring but do not
        // consume a mandatory saved point; reserve only real shrine actives.
        var masteryLimit = Math.Min(8, Math.Max(0, totalTalentPoints - mandatoryActivePointCount));
        var ranked = rankedRows.Take(Math.Max(0, masteryLimit - 2))
            .Concat(rankedRows.GroupBy(entry => entry.Floor)
                .Select(group => group.OrderByDescending(entry => entry.Score)
                    .ThenBy(entry => entry.Id).First()))
            .Concat(rankedRows)
            .DistinctBy(entry => entry.Id)
            .Take(masteryLimit)
            .Select(entry => entry.Id)
            .ToList();
        return preferred with { MasteryTalentIds = ranked, ObjectiveScores = objectiveScores };
    }

    private static TalentLevelPlan BuildDeterministicTalentLevelPlan(
        object hero,
        HeroFocus focus,
        IReadOnlyCollection<int> skillTalentIds,
        IReadOnlyCollection<int> masteryTalentIds,
        int totalTalentPoints,
        object objectiveAttr,
        IReadOnlyCollection<GearCandidate>? candidateItems = null,
        int milestoneSeedLimit = -1,
        IReadOnlyDictionary<int, int>? preferredSavedLevels = null,
        bool fastDeterministicCompletion = false)
    {
        var talentData = ReadRequiredProperty(hero, "heroTalentData")
                         ?? throw new InvalidOperationException("HeroTalentData is unavailable.");
        var gridTalents = ReadValues(ReadRequiredProperty(talentData, "talentDic"))
            .DistinctBy(talent => NativeObjectKey(talent, talent)).ToList();
        var gridById = BuildTalentGridById(gridTalents);
        var activeSlots = GetTransformableTalents(talentData)
            .Where(talent => !IsTalentLockedRequired(talent))
            .OrderBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "floor") ?? int.MaxValue)
            .ThenBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "index") ?? int.MaxValue)
            .ThenBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? int.MaxValue)
            .ToList();
        var usedActiveSlots = new HashSet<string>(StringComparer.Ordinal);

        object ResolvePlanningTalent(object definition, int talentId)
        {
            if (gridById.TryGetValue(talentId, out var exact))
            {
                if (IsTransformableSkillDefinition(definition))
                {
                    var exactKey = NativeObjectKey(exact, exact);
                    if (!usedActiveSlots.Add(exactKey))
                        throw new InvalidOperationException(
                            $"Several planned active skills resolve to the same native slot ({talentId}).");
                }
                return exact;
            }
            if (!IsTransformableSkillDefinition(definition))
                throw new InvalidOperationException($"Planned talent {talentId} is absent from the native talent grid.");

            var skillId = ReadNullableInt(definition, "skillId") ?? 0;
            var floor = ReadNullableInt(definition, "floor") ?? int.MinValue;
            var index = ReadNullableInt(definition, "index") ?? int.MinValue;
            var slot = activeSlots.FirstOrDefault(candidate =>
            {
                var key = NativeObjectKey(candidate, candidate);
                return !usedActiveSlots.Contains(key)
                       && (ReadNullableInt(Read(candidate, "tTalentData"), "skillId") ?? 0) == skillId;
            }) ?? activeSlots.FirstOrDefault(candidate =>
            {
                var row = Read(candidate, "tTalentData");
                var key = NativeObjectKey(candidate, candidate);
                return !usedActiveSlots.Contains(key)
                       && (ReadNullableInt(row, "floor") ?? int.MinValue) == floor
                       && (ReadNullableInt(row, "index") ?? int.MinValue) == index;
            }) ?? activeSlots.FirstOrDefault(candidate =>
                !usedActiveSlots.Contains(NativeObjectKey(candidate, candidate)));
            if (slot is null)
                throw new InvalidOperationException($"No unlocked native active-skill slot can host talent {talentId}.");
            usedActiveSlots.Add(NativeObjectKey(slot, slot));
            return slot;
        }

        var definitions = skillTalentIds.Concat(masteryTalentIds).Distinct()
            .Select(id => (Id: id, Definition: InvokeStatic("TableData", "getTTalentData", id)))
            .Where(entry => entry.Id > 0 && entry.Definition is not null)
            // Claim rows that already exist in the hero grid first. Otherwise a
            // transformed candidate processed earlier can borrow that physical
            // slot as a fallback and collide when its real fixed/current talent
            // is resolved later in the same plan.
            .OrderByDescending(entry => gridById.ContainsKey(entry.Id))
            .ThenBy(entry => entry.Id)
            .Select(entry => (entry.Id, Definition: entry.Definition!, Talent: ResolvePlanningTalent(entry.Definition!, entry.Id)))
            .ToList();
        var missing = skillTalentIds.Concat(masteryTalentIds).Where(id => id > 0)
            .Except(definitions.Select(entry => entry.Id)).Distinct().ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Planned talent definitions are unavailable ({string.Join(',', missing)}).");

        var bindings = definitions.Select(entry =>
        {
            var type = ReadNullableInt(entry.Definition, "type") ?? 0;
            var baseLevel = Math.Min(GetTalentBaseLevelRequired(entry.Talent), GetTalentLevelCap(entry.Talent));
            var cap = GetTalentLevelCap(entry.Talent);
            // ChangeBaseSkill materializes the selected base-skill row with one
            // saved level without consuming a normal talent point. Keep the
            // optimizer's vector in paid-point space, then add this native free
            // level only when producing the exact save target.
            var implicitSaved = IsBaseSkillDefinition(entry.Definition)
                ? Math.Min(1, Math.Max(0, cap - baseLevel))
                : 0;
            return (entry.Id, entry.Definition, entry.Talent, Type: type, Base: baseLevel,
                Cap: cap, ImplicitSaved: implicitSaved,
                MaxSaved: Math.Max(0, cap - baseLevel - implicitSaved),
                SkillId: ReadNullableInt(entry.Definition, "skillId") ?? 0,
                MasteryId: ReadNullableInt(entry.Definition, "masteryId") ?? 0,
                Floor: ReadNullableInt(entry.Definition, "floor") ?? int.MaxValue);
        }).Where(entry => entry.Type is 1 or 2).ToList();
        var skillBindings = bindings.Where(entry => entry.Type == 1 && entry.SkillId > 0).ToList();
        var masteryBindings = bindings.Where(entry => entry.Type == 2 && entry.MasteryId > 0).ToList();
        if (skillBindings.Count == 0)
            throw new InvalidOperationException("The performance plan contains no native skill talent.");
        var baseSkillBindings = skillBindings
            .Where(entry => IsBaseSkillDefinition(entry.Definition)).ToList();
        if (baseSkillBindings.Count != 1)
            throw new InvalidOperationException("The performance plan must contain exactly one native base-skill talent.");
        if (baseSkillBindings[0].ImplicitSaved != 1)
            throw new InvalidOperationException("The native base-skill talent cannot materialize its free saved level.");

        var levels = bindings.ToDictionary(entry => entry.Id, _ => 0);
        foreach (var entry in skillBindings.Where(entry => IsTransformableSkillDefinition(entry.Definition)))
        {
            if (entry.MaxSaved <= 0)
                throw new InvalidOperationException($"Active skill talent {entry.Id} has no investable level.");
            levels[entry.Id] = 1;
        }
        var committed = levels.Values.Sum();
        if (committed > totalTalentPoints)
            throw new InvalidOperationException($"The exact talent budget cannot learn the selected package ({committed}/{totalTalentPoints}).");

        Dictionary<int, int> grantedLevels = candidateItems is null
            ? new Dictionary<int, int>()
            : skillBindings.Select(entry => entry.SkillId).Distinct()
                .ToDictionary(id => id, id => GetGrantedSkillLevel(candidateItems, id));
        var learnedSkillIds = skillBindings.Select(entry => entry.SkillId).ToHashSet();
        var grantedOnlySkillIds = candidateItems is null
            ? new List<int>()
            : candidateItems.SelectMany(candidate => GetGrantedExtraSkillLevels(candidate.Record.ItemData))
                .Select(entry =>
                {
                    const string prefix = "extra-skill:";
                    return entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                           && int.TryParse(entry.Key.Substring(prefix.Length), NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out var id)
                        ? id
                        : 0;
                })
                .Where(id => id > 0 && !learnedSkillIds.Contains(id))
                .Distinct().OrderBy(id => id).ToList();
        NativeSkillRoleProfile RoleFor((int Id, object Definition, object Talent, int Type, int Base, int Cap,
            int ImplicitSaved, int MaxSaved, int SkillId, int MasteryId, int Floor) entry, int saved, object attr)
        {
            var grantedLevel = grantedLevels.TryGetValue(entry.SkillId, out var value) ? value : 0;
            var effective = Math.Max(1, Math.Max(entry.Base + entry.ImplicitSaved + saved, grantedLevel));
            var role = ReadNativeSkillRoleProfile(
                hero, entry.SkillId, effective, attr, false, candidateItems, true, false);
            if (!IsNativeRoleRankable(role, focus) && !IsTalentUnreplaceable(entry.Talent))
                throw new InvalidOperationException(
                    $"Skill {entry.SkillId} level {effective} has no rankable native output: {role.Failure}");
            return role;
        }

        var packageScoreCache = new Dictionary<string, double>(StringComparer.Ordinal);
#if PATHOFIDLE_RUNTIME_TEST
        const int runtimeTalentFailureLimit = 2200;
        var runtimeTalentFailures = new StringBuilder();
        void RecordRuntimeTalentFailure(string context, Exception error)
        {
            var detail = Regex.Replace(error.GetBaseException().Message, @"\s+", " ").Trim();
            if (detail.Length > 220) detail = detail[..217] + "...";
            var entry = $"{context}:{detail}";
            if (runtimeTalentFailures.Length + (runtimeTalentFailures.Length == 0 ? 0 : 3) + entry.Length
                > runtimeTalentFailureLimit) return;
            if (runtimeTalentFailures.Length > 0) runtimeTalentFailures.Append(" | ");
            runtimeTalentFailures.Append(entry);
        }
#endif
        string VectorKey(IReadOnlyDictionary<int, int> savedLevels) => string.Join(",",
            savedLevels.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}"));

        double PackageScore(IReadOnlyDictionary<int, int> savedLevels)
        {
            var vectorKey = VectorKey(savedLevels);
            if (packageScoreCache.TryGetValue(vectorKey, out var cached)) return cached;

            // Every trial must use one coherent AttrData snapshot. Applying all
            // mastery levels before rebuilding the skill package preserves both
            // mastery/mastery and mastery/skill-level interactions.
            var attr = InvokeRequiredStaticMany("AttrData", "copyCreate", objectiveAttr)
                       ?? throw new InvalidOperationException("Talent level-plan AttrData copy failed.");
            foreach (var mastery in masteryBindings)
            {
                if (mastery.Base > 0)
                    ApplyMasteryAttributePreview(attr, mastery.MasteryId, mastery.Base, mastery.Cap, false);
                var effectiveMastery = mastery.Base + savedLevels.GetValueOrDefault(mastery.Id);
                if (effectiveMastery > 0)
                    ApplyMasteryAttributePreview(attr, mastery.MasteryId, effectiveMastery, mastery.Cap, true);
            }

            var baseBinding = baseSkillBindings[0];
            var packageAttr = CreateStrictSkillPackageAdjustedAttr(
                hero,
                skillBindings.Select(entry => (
                    SkillId: entry.SkillId,
                    Level: Math.Max(1, Math.Max(
                        entry.Base + entry.ImplicitSaved + savedLevels.GetValueOrDefault(entry.Id),
                        grantedLevels.GetValueOrDefault(entry.SkillId)))))
                    .Concat(grantedOnlySkillIds.Select(skillId => (
                        SkillId: skillId,
                        Level: Math.Max(1, GetGrantedSkillLevel(candidateItems!, skillId))))),
                attr,
                false,
                candidateItems,
                out _);
            var baseRole = RoleFor(baseBinding, savedLevels.GetValueOrDefault(baseBinding.Id), packageAttr);
            var activeRoles = skillBindings.Where(entry => entry.Id != baseBinding.Id)
                .Select(entry => RoleFor(entry, savedLevels.GetValueOrDefault(entry.Id), packageAttr)).ToList();
            foreach (var grantedSkillId in grantedOnlySkillIds)
            {
                var grantedLevel = Math.Max(1, GetGrantedSkillLevel(candidateItems!, grantedSkillId));
                var grantedRole = ReadNativeSkillRoleProfile(
                    hero, grantedSkillId, grantedLevel, packageAttr, false, candidateItems, true, false);
                // Gear-granted skills cannot be removed from this loadout. A
                // returned partial preview contributes only its proven lower
                // bound and does not invalidate independent package output. A
                // hard strict-preview failure still rejects this trial upstream.
                activeRoles.Add(grantedRole);
            }
            var package = BuildSharedSkillPackage(baseRole, activeRoles);
            if (!IsNativeRoleRankable(package, focus))
                throw new InvalidOperationException($"The shared skill package has no rankable native output: {package.Failure}");
            var score = ScoreHeroAttrObjective(packageAttr, focus) + ScoreNativeSkillRoleObjective(package, focus);
            if (!double.IsFinite(score))
                throw new InvalidOperationException($"Talent vector {vectorKey} returned a non-finite objective.");
            packageScoreCache[vectorKey] = score;
            return score;
        }

        TalentLevelPlan FinalizePlan(Dictionary<int, int> finalLevels)
        {
            var paidPointTotal = finalLevels.Values.Sum();
            if (paidPointTotal != totalTalentPoints)
                throw new InvalidOperationException(
                    $"The finalized paid talent budget differs ({paidPointTotal}/{totalTalentPoints}).");
            foreach (var entry in bindings)
            {
                var paid = finalLevels.GetValueOrDefault(entry.Id, -1);
                if (paid < 0 || paid > entry.MaxSaved)
                    throw new InvalidOperationException(
                        $"The finalized paid target for talent {entry.Id} is outside its native range ({paid}/{entry.MaxSaved}).");
            }
            var materializedSavedLevels = bindings.ToDictionary(
                entry => entry.Id,
                entry => checked(finalLevels[entry.Id] + entry.ImplicitSaved));
            var implicitSavedTotal = bindings.Sum(entry => entry.ImplicitSaved);
            if (materializedSavedLevels.Values.Sum() != checked(paidPointTotal + implicitSavedTotal))
                throw new InvalidOperationException("The finalized materialized talent vector lost an implicit base-skill level.");
            var effectiveSkillLevels = skillBindings.ToDictionary(
                entry => entry.SkillId,
                entry => Math.Max(1, Math.Max(
                    entry.Base + materializedSavedLevels[entry.Id],
                    grantedLevels.GetValueOrDefault(entry.SkillId))));
            var tokenBody = $"{focus.Key}|{totalTalentPoints}|" + string.Join(",",
                materializedSavedLevels.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}"));
            ulong tokenHash = 1469598103934665603UL;
            foreach (var character in tokenBody)
            {
                tokenHash ^= character;
                tokenHash *= 1099511628211UL;
            }
            return new TalentLevelPlan(
                materializedSavedLevels,
                effectiveSkillLevels,
                $"{focus.Key}-{totalTalentPoints}-{tokenHash:x16}");
        }

        Dictionary<int, int> BuildProjectedPreferredVector()
        {
            var projected = new Dictionary<int, int>(levels);
            var projectedCommitted = projected.Values.Sum();
            foreach (var entry in bindings.OrderBy(entry => entry.Floor).ThenBy(entry => entry.Id))
            {
                if (projectedCommitted >= totalTalentPoints) break;
                var materializedPreferred = preferredSavedLevels?.GetValueOrDefault(entry.Id)
                                            ?? checked(projected[entry.Id] + entry.ImplicitSaved);
                var preferred = Math.Max(0, materializedPreferred - entry.ImplicitSaved);
                var target = Math.Clamp(preferred, projected[entry.Id], entry.MaxSaved);
                var add = Math.Min(target - projected[entry.Id], totalTalentPoints - projectedCommitted);
                if (add <= 0) continue;
                projected[entry.Id] += add;
                projectedCommitted += add;
            }
            var completionOrder = bindings
                .OrderBy(entry => entry.Type == 1 ? 0 : 1)
                .ThenBy(entry => entry.Floor)
                .ThenBy(entry => entry.Id)
                .ToList();
            while (projectedCommitted < totalTalentPoints)
            {
                var progressed = false;
                foreach (var entry in completionOrder)
                {
                    if (projectedCommitted >= totalTalentPoints) break;
                    if (projected[entry.Id] >= entry.MaxSaved) continue;
                    projected[entry.Id]++;
                    projectedCommitted++;
                    progressed = true;
                }
                if (!progressed)
                    throw new InvalidOperationException(
                        $"The projected talent vector cannot spend the exact point budget ({projectedCommitted}/{totalTalentPoints}).");
            }
            return projected;
        }

        if (fastDeterministicCompletion)
        {
            // Coarse gear screening must never run the point-by-point native
            // optimizer hundreds of times. Project the already verified target
            // vector onto this skill combination, then spend any points whose
            // original node was replaced in a deterministic round-robin. This
            // vector is evaluated once and is never applied to the save; every
            // surviving finalist is rebuilt by the full nonlinear allocator.
            var coarse = BuildProjectedPreferredVector();
            _ = PackageScore(coarse);
            return FinalizePlan(coarse);
        }

        // Immediate one-point greedy can strand a skill at level one when its
        // intermediate gains are modest but a later native level/cap interaction
        // is decisive. Keep a small, deterministic set of milestone starts and
        // greedily complete each one. This crosses those valleys without running
        // an exponential level-vector search for every one of the 360 gear
        // finalists. PackageScore's shared vector cache keeps repeated prefixes
        // cheap and every accepted path still requires rankable native output.
        var seedLimit = milestoneSeedLimit > 0
            ? milestoneSeedLimit
            : candidateItems is null ? 12 : 6;
        var seeds = new List<Dictionary<int, int>> { new(levels) };
        var seedKeys = new HashSet<string>(StringComparer.Ordinal) { VectorKey(levels) };
        void AddMilestoneSeed(int talentId, int target)
        {
            if (seeds.Count >= seedLimit || !levels.TryGetValue(talentId, out var initial)) return;
            var binding = bindings.First(entry => entry.Id == talentId);
            target = Math.Clamp(target, initial, binding.MaxSaved);
            var extra = target - initial;
            if (extra <= 0 || committed + extra > totalTalentPoints) return;
            var seed = new Dictionary<int, int>(levels) { [talentId] = target };
            var key = VectorKey(seed);
            if (seedKeys.Add(key)) seeds.Add(seed);
        }

        var orderedSkillBindings = skillBindings
            .OrderBy(entry => IsTransformableSkillDefinition(entry.Definition) ? 0 : 1)
            .ThenBy(entry => entry.Floor).ThenBy(entry => entry.Id).ToList();
        foreach (var entry in orderedSkillBindings)
        {
            var reachable = Math.Min(entry.MaxSaved,
                levels[entry.Id] + Math.Max(0, totalTalentPoints - committed));
            AddMilestoneSeed(entry.Id, reachable);
        }
        // If the bound has room, retain the strongest pre-ranked mastery cap path
        // before adding mid-level skill paths as further nonlinear probes.
        foreach (var entry in masteryBindings)
        {
            var reachable = Math.Min(entry.MaxSaved,
                Math.Max(0, totalTalentPoints - committed));
            AddMilestoneSeed(entry.Id, reachable);
        }
        foreach (var entry in orderedSkillBindings)
        {
            var reachable = Math.Min(entry.MaxSaved,
                levels[entry.Id] + Math.Max(0, totalTalentPoints - committed));
            AddMilestoneSeed(entry.Id, levels[entry.Id] + (reachable - levels[entry.Id] + 1) / 2);
        }

        (Dictionary<int, int> Levels, double Score)? CompleteSeed(Dictionary<int, int> seed)
        {
            var trial = new Dictionary<int, int>(seed);
            var trialCommitted = trial.Values.Sum();
            try
            {
                while (trialCommitted < totalTalentPoints)
                {
                    var currentPackage = PackageScore(trial);
                    var candidates = new List<(int Id, double Gain, int Floor)>();
                    var legalCandidates = bindings.Where(entry => trial[entry.Id] < entry.MaxSaved)
                        .OrderBy(entry => entry.Floor).ThenBy(entry => entry.Id).ToList();
                    foreach (var entry in legalCandidates)
                    {
                        trial[entry.Id]++;
                        try
                        {
                            var next = PackageScore(trial);
                            candidates.Add((entry.Id, next - currentPackage, entry.Floor));
                        }
                        catch (InvalidOperationException error)
                        {
                            _ = error;
                            // An unrankable or otherwise invalid native package is
                            // an invalid search edge, never a heuristic fallback.
#if PATHOFIDLE_RUNTIME_TEST
                            RecordRuntimeTalentFailure($"seed={VectorKey(trial)},raise={entry.Id}", error);
#endif
                        }
                        finally
                        {
                            trial[entry.Id]--;
                        }
                    }
                    var best = candidates.Where(entry => double.IsFinite(entry.Gain))
                        .OrderByDescending(entry => entry.Gain)
                        .ThenBy(entry => entry.Floor)
                        .ThenBy(entry => entry.Id)
                        .FirstOrDefault();
                    if (best.Id <= 0) return null;
                    trial[best.Id]++;
                    trialCommitted++;
                }
                return trialCommitted == totalTalentPoints
                    ? (trial, PackageScore(trial))
                    : null;
            }
            catch (InvalidOperationException error)
            {
                _ = error;
#if PATHOFIDLE_RUNTIME_TEST
                RecordRuntimeTalentFailure($"seed={VectorKey(seed)}", error);
#endif
                return null;
            }
        }

        var completedSeeds = seeds.Select(CompleteSeed).Where(result => result.HasValue)
            .Select(result => result!.Value)
            .ToList();
        // The fast exact-screening vector is a valid complete allocation. Keep
        // it as a one-evaluation floor during nonlinear refinement so staging can
        // never make the chosen package worse merely because its vector was not
        // one of the limited milestone seeds.
        if (preferredSavedLevels is not null)
        {
            try
            {
                var preferredVector = BuildProjectedPreferredVector();
                var preferredKey = VectorKey(preferredVector);
                if (completedSeeds.All(result => VectorKey(result.Levels) != preferredKey))
                    completedSeeds.Add((preferredVector, PackageScore(preferredVector)));
            }
            catch (InvalidOperationException error)
            {
#if PATHOFIDLE_RUNTIME_TEST
                RecordRuntimeTalentFailure("preferred-screen-vector", error);
#else
                _ = error;
#endif
            }
        }
        completedSeeds = completedSeeds
            .OrderByDescending(result => result.Score)
            .ThenBy(result => VectorKey(result.Levels), StringComparer.Ordinal)
            .ToList();
        if (completedSeeds.Count == 0)
            throw new InvalidOperationException(
                $"No rankable native talent package can spend the exact point budget ({committed}/{totalTalentPoints})"
#if PATHOFIDLE_RUNTIME_TEST
                + $"; rejected=[{runtimeTalentFailures}]"
#endif
                + ".");
        levels = completedSeeds[0].Levels;
        committed = levels.Values.Sum();

        return FinalizePlan(levels);
    }

    private static PlannedSkillTarget BuildPlannedSkillTarget(object hero, HeroFocus focus)
    {
        var talentData = ReadRequiredProperty(hero, "heroTalentData")
                         ?? throw new InvalidOperationException("HeroTalentData is unavailable.");
        var saveHero = ReadRequiredProperty(hero, "saveHeroData")
                       ?? throw new InvalidOperationException("SaveHeroData is unavailable.");
        var talents = ReadValues(ReadRequiredProperty(talentData, "talentDic"))
            .DistinctBy(talent => NativeObjectKey(talent, talent)).ToList();
        var spent = GetResettableTalentPointCount(talentData, talents);
        var total = PreviewExactTalentPointBudget(talentData, saveHero, talents, spent);
        var plan = SelectPreferredActiveSkillTargets(hero, talentData,
            GetPerformanceTalentPlan(hero, focus, total), focus);
        plan = RebuildMasteryPlanForSelectedSkills(hero, focus, total, plan);
        var planRows = plan.SkillTalentIds
            .Select(id => InvokeStatic("TableData", "getTTalentData", id))
            .Where(row => row is not null).Cast<object>().ToList();
        var baseRow = planRows.FirstOrDefault(IsBaseSkillDefinition);
        var baseTalentId = ReadNullableInt(baseRow, "id") ?? 0;
        var baseSkillId = ReadNullableInt(baseRow, "skillId") ?? 0;
        var activeSkillIds = planRows.Where(IsTransformableSkillDefinition)
            .Select(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var jobId = ReadRequiredIntProperty(saveHero, "jobId");
        var baseCandidateSkillIds = talents
            .Where(talent => !IsTalentLockedRequired(talent))
            .Select(talent => Read(talent, "tTalentData"))
            .Where(IsBaseSkillDefinition)
            .Select(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var currentActiveTalents = GetTransformableTalents(talentData)
            .Where(talent => !IsTalentLockedRequired(talent)).ToList();
        var currentActiveTalentIds = currentActiveTalents
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var preservedTalentIds = GetTransformableTalents(talentData)
            .Where(IsTalentUnreplaceable)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var heroLevel = ReadRequiredIntProperty(saveHero, "level");
        var activeCandidateSkillIds = ReadValues(ReadStatic("TableData", "TTalentDict"))
            .Where(IsTransformableSkillDefinition)
            .Where(row => (ReadNullableInt(row, "jobId") ?? 0) == jobId)
            .Where(row =>
            {
                var id = ReadNullableInt(row, "id") ?? 0;
                if (id <= 0 || (preservedTalentIds.Contains(id) && !currentActiveTalentIds.Contains(id))) return false;
                return heroLevel > 1 || currentActiveTalentIds.Contains(id) || (ReadNullableInt(row, "floor") ?? 0) == 1;
            })
            .Select(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var objectiveBySkillId = new Dictionary<int, double>();
        foreach (var row in baseCandidateSkillIds.Concat(activeCandidateSkillIds)
                     .Select(skillId => (SkillId: skillId, Rows: ReadValues(ReadStatic("TableData", "TTalentDict"))
                         .Where(candidate => (ReadNullableInt(candidate, "skillId") ?? 0) == skillId))))
        {
            var value = row.Rows.Select(candidate => plan.ObjectiveScores?.GetValueOrDefault(ReadNullableInt(candidate, "id") ?? 0) ?? double.NegativeInfinity)
                .DefaultIfEmpty(double.NegativeInfinity).Max();
            if (double.IsFinite(value)) objectiveBySkillId[row.SkillId] = value;
        }
        var objectiveAttr = CreateTalentNeutralObjectiveAttr(hero, talents);
        var levelPlan = BuildDeterministicTalentLevelPlan(
            hero, focus, plan.SkillTalentIds, plan.MasteryTalentIds, total, objectiveAttr);
        var selectedMasteryTalentIds = plan.MasteryTalentIds
            .Where(id => levelPlan.SavedLevels.GetValueOrDefault(id) > 0)
            .Distinct().ToList();
        plan = plan with
        {
            MasteryTalentIds = selectedMasteryTalentIds,
            TargetSavedLevels = new Dictionary<int, int>(levelPlan.SavedLevels),
            PlanToken = levelPlan.Token
        };
        var baseSavedTarget = baseTalentId > 0
            ? levelPlan.SavedLevels.GetValueOrDefault(baseTalentId)
            : 0;
        var baseTalent = baseTalentId > 0 && BuildTalentGridById(talents).TryGetValue(baseTalentId, out var resolvedBaseTalent)
            ? resolvedBaseTalent
            : null;
        var baseEffectiveTarget = baseTalent is null
            ? levelPlan.EffectiveSkillLevels.GetValueOrDefault(baseSkillId, 1)
            : Math.Max(1, GetTalentBaseLevelRequired(baseTalent) + baseSavedTarget);
        return new PlannedSkillTarget(
            baseTalentId,
            baseSkillId,
            baseEffectiveTarget,
            activeSkillIds,
            plan.SkillTalentIds.ToHashSet(),
            selectedMasteryTalentIds.ToHashSet(),
            baseCandidateSkillIds,
            activeCandidateSkillIds,
            currentActiveTalents.Count,
            objectiveBySkillId,
            total,
            new Dictionary<int, int>(levelPlan.SavedLevels),
            levelPlan.Token);
    }

    private static double ScoreSkillDefinitionForObjective(object hero, object definition, HeroFocus focus,
        int previewLevel, object objectiveAttr)
    {
        var talentId = ReadNullableInt(definition, "id") ?? 0;
        var skillId = ReadNullableInt(definition, "skillId") ?? 0;
        if (talentId <= 0 || skillId <= 0)
            throw new InvalidOperationException($"Invalid skill objective row (talent={talentId}, skill={skillId}).");
        var nativeRole = ReadNativeSkillRoleProfile(hero, skillId, Math.Max(1, previewLevel), objectiveAttr, false);
        // Preview failures must abort before shrine transformation/reset, not
        // become a zero that silently selects a different hero personality.
        return ScoreNativeSkillRoleObjective(nativeRole, focus) * 10000d;
    }

    private static bool MatchesManualSkillTheme(object hero, object definition, HeroFocus focus)
    {
        if (!focus.IsManual) return true;
        var skillId = ReadNullableInt(definition, "skillId") ?? 0;
        if (skillId <= 0) return false;
        var text = GetTalentDefinitionText(definition).ToLowerInvariant();
        var role = ReadNativeSkillRoleProfile(hero, skillId, 1);
        var positive = role.DamageByType.Where(entry => entry.Value > 0d).ToList();
        HashSet<int>? allowed = focus.Key switch
        {
            "physical" => new HashSet<int> { 1, 2, 3 },
            "elemental" => new HashSet<int> { 4, 5, 6 },
            "fire" => new HashSet<int> { 4 },
            "ice" => new HashSet<int> { 5 },
            "lightning" => new HashSet<int> { 6 },
            "bleed" => new HashSet<int> { 7 },
            "corrosion" => new HashSet<int> { 8 },
            _ => null
        };
        if (allowed is not null && positive.Count > 0)
        {
            return positive.Any(entry => allowed.Contains(entry.Key) && entry.Value > 0d);
        }

        return focus.Key switch
        {
            "minion" => role.Summon || KeywordScore(text, focus.Keywords) > 0,
            "crit" => positive.Count > 0 || KeywordScore(text, CriticalWords) > 0,
            "defense" => role.Heal > 0d || role.Shield > 0d || KeywordScore(text, TankWords) > 0,
            "support" => role.Heal > 0d || role.Shield > 0d || KeywordScore(text, SupportWords) > 0,
            _ => KeywordScore(text, focus.Keywords) > 0
        };
    }

    private static double GetManualDamageAmount(IReadOnlyDictionary<int, double> damageByType, HeroFocus focus)
    {
        var total = damageByType.Values.Where(value => value > 0d).Sum();
        HashSet<int>? allowed = focus.Key switch
        {
            "physical" => new HashSet<int> { 1, 2, 3 },
            "elemental" => new HashSet<int> { 4, 5, 6 },
            "fire" => new HashSet<int> { 4 },
            "ice" => new HashSet<int> { 5 },
            "lightning" => new HashSet<int> { 6 },
            "bleed" => new HashSet<int> { 7 },
            "corrosion" => new HashSet<int> { 8 },
            _ => null
        };
        if (allowed is null) return total;
        var aligned = damageByType.Where(entry => allowed.Contains(entry.Key) && entry.Value > 0d).Sum(entry => entry.Value);
        // A manual element is an objective weight, not an eligibility rule. A
        // substantially stronger off-element secondary skill may still improve
        // the combined 60-second package; aligned damage receives full value and
        // all other native damage remains visible at a reduced weight.
        return aligned + Math.Max(0d, total - aligned) * 0.18d;
    }

    private static NativeSkillExecutionGraph ResolveNativeSkillExecutionGraph(
        object skillPreview,
        object skillInfo,
        int level,
        IReadOnlyCollection<int> displayedPowerIds)
    {
        var powers = new List<NativePowerInvocation>();
        var abilities = new List<NativeAbilityInvocation>();
        var summons = new List<NativeSummonInvocation>();
        var failures = new HashSet<string>(StringComparer.Ordinal);
        var emitterStack = new HashSet<int>();
        var expandedEdges = 0;
        object? rootActionAttr = null;
        object? rootActionData = null;

        void Fail(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason)) failures!.Add(reason);
        }

        double BoundEvents(double events, string edge)
        {
            if (!double.IsFinite(events) || events <= 0d)
            {
                Fail($"invalid {edge} event count {events:0.###}");
                return 0d;
            }
            if (events <= 6000d) return events;
            Fail($"{edge} event count exceeded the safe preview bound");
            return 6000d;
        }

        List<NativeTimedEvent> BoundTimeline(IEnumerable<NativeTimedEvent> rawEvents, string edge)
        {
            const double previewWindowSeconds = 60d;
            const double maximumTimelineEvents = 6000d;
            var ordered = new List<NativeTimedEvent>();
            foreach (var entry in rawEvents)
            {
                if (!double.IsFinite(entry.OffsetSeconds) || entry.OffsetSeconds < 0d
                    || !double.IsFinite(entry.Count) || entry.Count <= 0d)
                {
                    Fail($"invalid {edge} timed event {entry.OffsetSeconds:0.###}:{entry.Count:0.###}");
                    continue;
                }
                // Per-cast offsets at 60 seconds or later can never fall inside
                // the global half-open [0, 60) objective, even for the first cast.
                if (entry.OffsetSeconds >= previewWindowSeconds) continue;
                ordered.Add(entry);
            }

            var result = new List<NativeTimedEvent>();
            var acceptedEvents = 0d;
            foreach (var entry in ordered.OrderBy(value => value.OffsetSeconds))
            {
                var remaining = maximumTimelineEvents - acceptedEvents;
                if (remaining <= 0d)
                {
                    Fail($"{edge} timed event count exceeded the safe preview bound");
                    break;
                }
                var accepted = Math.Min(entry.Count, remaining);
                if (accepted < entry.Count)
                    Fail($"{edge} timed event count exceeded the safe preview bound");
                if (result.Count > 0
                    && Math.Abs(result[^1].OffsetSeconds - entry.OffsetSeconds) <= 0.000000001d)
                {
                    var previous = result[^1];
                    result[^1] = previous with { Count = previous.Count + accepted };
                }
                else result.Add(new NativeTimedEvent(entry.OffsetSeconds, accepted));
                acceptedEvents += accepted;
                if (accepted < entry.Count) break;
            }
            return result;
        }

        static double TimelineEventCount(IEnumerable<NativeTimedEvent> timeline)
            => timeline.Sum(entry => entry.Count);

        List<NativeTimedEvent> ScaleTimeline(
            IReadOnlyList<NativeTimedEvent> timeline, double multiplier, string edge)
            => BoundTimeline(
                timeline.Select(entry => new NativeTimedEvent(entry.OffsetSeconds, entry.Count * multiplier)), edge);

        List<NativeTimedEvent> ExpandEmitterTimeline(
            IReadOnlyList<NativeTimedEvent> starts,
            IReadOnlyList<double> shotOffsets,
            int bulletCount,
            string edge)
        {
            const double previewWindowSeconds = 60d;
            const double maximumTimelineEvents = 6000d;
            var queue = new PriorityQueue<(int StartIndex, int ShotIndex), double>();
            for (var startIndex = 0; startIndex < starts.Count; startIndex++)
            {
                if (shotOffsets.Count == 0) break;
                var firstOffset = starts[startIndex].OffsetSeconds + shotOffsets[0];
                if (firstOffset < previewWindowSeconds)
                    queue.Enqueue((startIndex, 0), firstOffset);
            }

            var result = new List<NativeTimedEvent>();
            var acceptedEvents = 0d;
            while (queue.TryDequeue(out var node, out var eventOffset))
            {
                var start = starts[node.StartIndex];
                var eventCount = start.Count * bulletCount;
                if (!double.IsFinite(eventCount) || eventCount <= 0d)
                {
                    Fail($"invalid {edge} expanded event count {eventCount:0.###}");
                    continue;
                }
                var remaining = maximumTimelineEvents - acceptedEvents;
                var accepted = Math.Min(eventCount, Math.Max(0d, remaining));
                if (accepted > 0d)
                {
                    if (result.Count > 0
                        && Math.Abs(result[^1].OffsetSeconds - eventOffset) <= 0.000000001d)
                    {
                        var previous = result[^1];
                        result[^1] = previous with { Count = previous.Count + accepted };
                    }
                    else result.Add(new NativeTimedEvent(eventOffset, accepted));
                    acceptedEvents += accepted;
                }

                var nextShotIndex = node.ShotIndex + 1;
                if (nextShotIndex < shotOffsets.Count)
                {
                    var nextOffset = start.OffsetSeconds + shotOffsets[nextShotIndex];
                    if (nextOffset < previewWindowSeconds)
                        queue.Enqueue((node.StartIndex, nextShotIndex), nextOffset);
                }
                if (accepted < eventCount || (acceptedEvents >= maximumTimelineEvents && queue.Count > 0))
                {
                    Fail($"{edge} timed event count exceeded the safe preview bound");
                    break;
                }
            }
            return result;
        }

        void AddPower(int powerId, double events, object triggerData, object sourceAttr,
            IReadOnlyList<NativeTimedEvent>? eventTimeline = null)
        {
            if (eventTimeline is not null)
            {
                eventTimeline = BoundTimeline(eventTimeline, $"power {powerId}");
                events = TimelineEventCount(eventTimeline);
                if (events <= 0d) return;
            }
            events = BoundEvents(events, $"power {powerId}");
            if (powerId <= 0 || events <= 0d)
            {
                Fail($"invalid power edge {powerId}:{events:0.###}");
                return;
            }
            try
            {
                var power = InvokeRequiredStaticMany("PowerData", "CreateByTrigger", powerId, level, triggerData);
                if (power is null)
                    throw new InvalidOperationException($"PowerData.CreateByTrigger({powerId}) returned no power.");
                powers.Add(new NativePowerInvocation(
                    powerId, level, events, sourceAttr, triggerData, power, eventTimeline));
            }
            catch (Exception error)
            {
                // CreateByShow would silently replace the action/bullet source
                // with the root SkillData attributes, so an unavailable trigger
                // preview is an unknown zero lower bound rather than a fallback.
                Fail($"power {powerId} trigger preview failed: {error.GetBaseException().Message}");
            }
        }

        void AddAbility(int abilityId, double events, object triggerData, object sourceAttr,
            IReadOnlyList<NativeTimedEvent>? eventTimeline = null)
        {
            if (eventTimeline is not null)
            {
                eventTimeline = BoundTimeline(eventTimeline, $"ability {abilityId}");
                events = TimelineEventCount(eventTimeline);
                if (events <= 0d) return;
            }
            events = BoundEvents(events, $"ability {abilityId}");
            if (abilityId <= 0 || events <= 0d)
            {
                Fail($"invalid ability edge {abilityId}:{events:0.###}");
                return;
            }
            abilities.Add(new NativeAbilityInvocation(
                abilityId, level, events, sourceAttr, triggerData, eventTimeline));
        }

        void AddSummon(int summonId, int count, object produceAttr)
        {
            if (summonId <= 0 || count <= 0)
            {
                Fail($"invalid summon edge {summonId}:{count}");
                return;
            }
            if (count > 6000)
            {
                Fail($"summon {summonId} count exceeded the safe preview bound");
                count = 6000;
            }
            summons.Add(new NativeSummonInvocation(summonId, level, count, produceAttr));
        }

        object? CreateTriggerPreview(int triggerId, string source, object sourceData)
        {
            try
            {
                var createType = CreateEnum("ETriggerCreateType", source == "action" ? 3 : 2)
                                 ?? throw new InvalidOperationException($"{source} trigger enum is unavailable.");
                var triggerData = InvokeRequiredStaticMany(
                    "TriggerData", "Create", triggerId, level, createType, sourceData);
                if (triggerData is null)
                    throw new InvalidOperationException($"TriggerData.Create({triggerId}) returned no trigger.");
                if (Read(triggerData, "inAttrData") is null || Read(triggerData, "skillData") is null)
                    throw new InvalidOperationException($"TriggerData.Create({triggerId}) lost its source attributes or skill.");
                return triggerData;
            }
            catch (Exception error)
            {
                Fail($"{source} trigger {triggerId} preview failed: {error.GetBaseException().Message}");
                return null;
            }
        }

        void VisitEmitter(int emitterId, double emitterEvents, int depth, object parentAttr, object? sourceBullet,
            IReadOnlyList<NativeTimedEvent>? emitterStartTimeline = null)
        {
            if (depth > 16 || ++expandedEdges > 2048)
            {
                Fail("skill trigger graph exceeded its safe expansion limit");
                return;
            }
            if (emitterId <= 0 || emitterEvents <= 0d)
            {
                Fail($"invalid emitter edge {emitterId}:{emitterEvents:0.###}");
                return;
            }
            // A shared emitter reached from two independent trigger branches must
            // contribute twice. Only an emitter already on the current recursion
            // path is a cycle.
            if (!emitterStack.Add(emitterId))
            {
                Fail($"cyclic emitter {emitterId}");
                return;
            }
            try
            {
                var emitterRow = InvokeStatic("TableData", "getTEmitterData", emitterId);
                var emitterAttrRows = Read(emitterRow, "attrArr");
                var emitterOwner = CreateEnum("EAttrOwnType", 4);
                if (emitterRow is null || emitterAttrRows is null || emitterOwner is null)
                {
                    Fail($"emitter table/attribute input {emitterId} unavailable");
                    return;
                }
                object? emitterData;
                object? emitterAttr;
                if (sourceBullet is null)
                {
                    emitterAttr = InvokeRequiredStaticMany(
                        "AttrData", "Create", emitterOwner, parentAttr, emitterAttrRows);
                    emitterData = InvokeRequiredStaticMany(
                        "EmitterData", "CreateByShow", emitterId, level, skillPreview);
                    if (emitterData is not null && emitterAttr is not null)
                    {
                        Write(emitterData, "attrData", emitterAttr);
                        if (rootActionData is not null) Write(emitterData, "ownActionData", rootActionData);
                    }
                    // Native InitByAction runs a small battle-state extra-effect
                    // hook for these table rows. Calling it without CombatData can
                    // crash, so retain only the raw lower bound and fail closed.
                    if (emitterId is 35051 or 36091 or 36111)
                        Fail($"emitter {emitterId} requires a live extra-effect context");
                }
                else
                {
                    emitterData = InvokeRequiredStaticMany(
                        "EmitterData", "CreateByBullet", emitterId, level, sourceBullet);
                    emitterAttr = Read(emitterData, "attrData");
                }
                if (emitterData is null || emitterAttr is null)
                {
                    Fail($"emitter {emitterId} native preview failed");
                    return;
                }
                InvokeRequiredInstance(emitterData, "createShotData");
                var shotAttr = Read(emitterData, "shotData");
                if (shotAttr is null)
                {
                    Fail($"emitter {emitterId} shot attributes unavailable");
                    return;
                }
                var bulletId = ReadIntAttrRequired(shotAttr, 4001);
                var bullet = bulletId > 0
                    ? InvokeRequiredStaticMany("BulletData", "CreateByShow", bulletId, Math.Max(1, level), shotAttr)
                    : null;
                var bulletRow = Read(bullet, "tBulletData");
                if (bullet is null || bulletRow is null)
                {
                    Fail($"emitter {emitterId} has no preview bullet {bulletId}");
                    return;
                }

                var bulletAttr = Read(bullet, "attrData");
                if (bulletAttr is null)
                {
                    Fail($"emitter {emitterId} attributes unavailable");
                    return;
                }
                Write(bullet, "ownEmitterData", emitterData);
                var bulletCount = ReadIntAttrRequired(shotAttr, 4002);
                var emitterLifeTime = (float)ReadAttrRequired(shotAttr, 4006);
                var shotInterval = (float)ReadAttrRequired(shotAttr, 4007);
                var shotLimit = (float)ReadAttrRequired(shotAttr, 4008);
                if (bulletCount <= 0)
                {
                    Fail($"emitter {emitterId} has no proven bullet count ({bulletCount})");
                    return;
                }
                if (!float.IsFinite(emitterLifeTime) || emitterLifeTime < 0f
                    || !float.IsFinite(shotInterval) || shotInterval < 0f
                    || !float.IsFinite(shotLimit))
                {
                    Fail($"emitter {emitterId} has an invalid native shot schedule "
                         + $"(life={emitterLifeTime:0.###}, interval={shotInterval:0.###}, max={shotLimit:0.###})");
                    return;
                }
                var shotSchedule = BuildNativeEmitterShotOffsets(emitterLifeTime, shotInterval, shotLimit);
                if (!shotSchedule.IsComplete && !string.IsNullOrWhiteSpace(shotSchedule.Failure))
                    Fail($"emitter {emitterId} {shotSchedule.Failure}");
                if (shotSchedule.Offsets.Count == 0) return;
                var hitTimes = ReadIntAttrRequired(bulletAttr, 5002);
                var starts = emitterStartTimeline is null
                    ? BoundTimeline(new[] { new NativeTimedEvent(0d, emitterEvents) }, $"emitter {emitterId} start")
                    : BoundTimeline(emitterStartTimeline, $"emitter {emitterId} start");
                var createdTimeline = ExpandEmitterTimeline(
                    starts, shotSchedule.Offsets, bulletCount, $"emitter {emitterId}");
                var createdBullets = TimelineEventCount(createdTimeline);
                if (createdBullets <= 0d) return;
                VisitTriggers(
                    ReadSequence(Read(bulletRow, "triggerArr")).Select(ToInt).Where(id => id > 0),
                    "bullet", bullet, createdBullets, hitTimes, depth + 1, createdTimeline);
            }
            catch (Exception error)
            {
                Fail($"emitter {emitterId} preview failed: {error.GetBaseException().Message}");
            }
            finally
            {
                emitterStack.Remove(emitterId);
            }
        }

        void VisitTriggers(IEnumerable<int> triggerIds, string source, object sourceData,
            double sourceEvents, int hitTimes = 0, int depth = 0,
            IReadOnlyList<NativeTimedEvent>? sourceTimeline = null)
        {
            foreach (var triggerId in triggerIds)
            {
                if (depth > 16 || ++expandedEdges > 2048)
                {
                    Fail("skill trigger graph exceeded its safe expansion limit");
                    return;
                }
                var trigger = InvokeStatic("TableData", "getTTriggerData", triggerId);
                if (trigger is null)
                {
                    Fail($"trigger {triggerId} unavailable");
                    continue;
                }
                if (Clean(ReadString(trigger, "condition") ?? string.Empty).Length > 0)
                {
                    Fail($"trigger {triggerId} has a battle condition");
                    continue;
                }
                var moment = ReadNullableInt(trigger, "moment") ?? 0;
                double resultEvents;
                IReadOnlyList<NativeTimedEvent>? resultTimeline = null;
                if (source == "action")
                {
                    if (moment != 2)
                    {
                        Fail($"action trigger {triggerId} uses moment {moment}");
                        continue;
                    }
                    resultEvents = sourceEvents;
                }
                else
                {
                    if (sourceTimeline is null)
                    {
                        Fail($"bullet trigger {triggerId} has no proven event timeline");
                        continue;
                    }
                    if (moment == 1 && hitTimes <= 0)
                    {
                        Fail($"bullet trigger {triggerId} has no proven hit count ({hitTimes})");
                        continue;
                    }
                    resultTimeline = moment switch
                    {
                        1 => ScaleTimeline(sourceTimeline, hitTimes, $"trigger {triggerId}"),
                        4 or 6 => BoundTimeline(sourceTimeline, $"trigger {triggerId}"),
                        _ => Array.Empty<NativeTimedEvent>()
                    };
                    resultEvents = TimelineEventCount(resultTimeline);
                    if (resultEvents <= 0d)
                    {
                        Fail($"bullet trigger {triggerId} uses conditional moment {moment}");
                        continue;
                    }
                }
                resultEvents = BoundEvents(resultEvents, $"trigger {triggerId}");
                if (resultEvents <= 0d) continue;

                List<int[]> rows;
                try { rows = ReadIntMatrixRows(Read(trigger, "resultArrArr"), 2); }
                catch (Exception error)
                {
                    Fail($"trigger {triggerId} result matrix failed: {error.GetBaseException().Message}");
                    continue;
                }
                if (rows.Count == 0)
                {
                    Fail($"trigger {triggerId} has no results");
                    continue;
                }
                var triggerData = rows.Any(row => row[0] is 1 or 2)
                    ? CreateTriggerPreview(triggerId, source, sourceData)
                    : null;
                foreach (var row in rows)
                {
                    var resultType = row[0];
                    var payloadId = row[1];
                    switch (resultType)
                    {
                        case 1:
                            if (triggerData is null) break;
                            AddPower(payloadId, resultEvents, triggerData,
                                Read(triggerData, "inAttrData")!, resultTimeline);
                            break;
                        case 2:
                            if (triggerData is null) break;
                            AddAbility(payloadId, resultEvents, triggerData,
                                Read(triggerData, "inAttrData")!, resultTimeline);
                            break;
                        case 3:
                            if (source != "bullet")
                                Fail($"trigger {triggerId} creates emitter {payloadId} outside bullet context");
                            else
                                VisitEmitter(payloadId, resultEvents, depth + 1,
                                    Read(sourceData, "attrData")!, sourceData, resultTimeline);
                            break;
                        default:
                            Fail($"trigger {triggerId} has unknown result type {resultType}");
                            break;
                    }
                }
            }
        }

        var actionId = ReadNullableInt(skillInfo, "actionId") ?? 0;
        var action = actionId > 0 ? InvokeStatic("TableData", "getTActionData", actionId) : null;
        if (action is null || (ReadNullableInt(action, "type") ?? 0) != 3)
        {
            Fail($"skill action {actionId} unavailable or not a skill action");
        }
        else
        {
            var skillAttr = Read(skillPreview, "attrData");
            var actionOwner = CreateEnum("EAttrOwnType", 3);
            if (skillAttr is null || actionOwner is null)
            {
                Fail($"skill action {actionId} attributes unavailable");
            }
            else
            {
                rootActionAttr = InvokeRequiredStaticMany("AttrData", "Create", actionOwner, skillAttr, (object)null!);
                if (rootActionAttr is null) Fail($"skill action {actionId} attribute preview failed");
            }
            if (rootActionAttr is not null)
            {
                try
                {
                    var actionType = GameType("ActionData")
                                     ?? throw new InvalidOperationException("ActionData type is unavailable.");
                    rootActionData = Activator.CreateInstance(actionType)
                                     ?? throw new InvalidOperationException("ActionData preview allocation failed.");
                    Write(rootActionData, "fieldIndex", 0);
                    Write(rootActionData, "level", level);
                    Write(rootActionData, "tActionData", action);
                    Write(rootActionData, "ownSkillData", skillPreview);
                    Write(rootActionData, "attrData", rootActionAttr);
                }
                catch (Exception error)
                {
                    rootActionData = null;
                    Fail($"skill action {actionId} source preview failed: {error.GetBaseException().Message}");
                }
                if (rootActionData is not null)
                    VisitTriggers(ReadSequence(Read(action, "triggerArr")).Select(ToInt).Where(id => id > 0),
                        "action", rootActionData, 1d);
                var emitterId = ReadNullableInt(action, "emitterId") ?? 0;
                if (emitterId > 0) VisitEmitter(emitterId, 1d, 0, rootActionAttr, null);

                var produceId = ReadNullableInt(action, "produceId") ?? 0;
                if (produceId > 0)
                {
                    var produceRow = InvokeStatic("TableData", "getTProduceData", produceId);
                    var produceOwner = CreateEnum("EAttrOwnType", 6);
                    var produceAttrRows = Read(produceRow, "attrArr");
                    if (produceRow is null || produceOwner is null || produceAttrRows is null)
                        Fail($"produce {produceId} table/attributes unavailable");
                    else
                    {
                        var produceAttr = InvokeRequiredStaticMany(
                            "AttrData", "Create", produceOwner, rootActionAttr, produceAttrRows);
                        if (produceAttr is null) Fail($"produce {produceId} attribute preview failed");
                        else
                        {
                            var summonId = ReadIntAttrRequired(produceAttr, 6001);
                            var summonCount = ReadIntAttrRequired(produceAttr, 6002);
                            if (summonId > 0 || summonCount > 0)
                            {
                                if (summonId <= 0 || summonCount <= 0)
                                    Fail($"produce {produceId} has an incomplete summon tuple {summonId}:{summonCount}");
                                else
                                    AddSummon(summonId, summonCount, produceAttr);
                            }
                        }
                    }
                }
            }
        }

        var reachedPowerIds = powers.Select(invocation => invocation.PowerId).ToHashSet();
        var unreachableDisplayed = displayedPowerIds.Where(id => id > 0 && !reachedPowerIds.Contains(id)).Distinct().ToList();
        if (unreachableDisplayed.Count > 0)
            Fail($"display powers are unreachable: {string.Join(',', unreachableDisplayed)}");
        return new NativeSkillExecutionGraph(
            powers,
            abilities,
            summons,
            failures.Count == 0,
            string.Join("; ", failures.OrderBy(value => value)));
    }

    private static (List<double> Offsets, bool IsComplete, string Failure)
        BuildNativeEmitterShotOffsets(float lifeTime, float interval, float maximumShots)
    {
        const double previewWindowSeconds = 60d;
        const int maximumPreviewEvents = 6000;
        if (!float.IsFinite(lifeTime) || lifeTime < 0f
            || !float.IsFinite(interval) || interval < 0f
            || !float.IsFinite(maximumShots))
            return (new List<double>(), false, "has an invalid native shot schedule");

        // Native OnInit calls shot() immediately for a zero interval. It does
        // not enter Update's attr4008 comparison on that path, so even a zero or
        // negative maximum still produces this one native initialization shot.
        if (interval == 0f) return (new List<double> { 0d }, true, string.Empty);

        // In Update, zero means unlimited. A negative maximum rejects the first
        // scheduled update shot. A positive fractional maximum is compared with
        // the integer completed-shot count and therefore permits ceil(maximum).
        if (maximumShots < 0f) return (new List<double>(), true, string.Empty);

        // The native lifetime comparison is `nowLifeTime > lifeTime`, so an
        // event exactly on the lifetime boundary is still allowed. The 60 s
        // objective is half-open: an event at exactly 60 s belongs outside it.
        var duration = Math.Min((double)lifeTime, Math.BitDecrement(previewWindowSeconds));
        var initialAccumulator = interval * 0.6f;
        var firstDelay = (double)interval - initialAccumulator;
        if (duration < firstDelay) return (new List<double>(), true, string.Empty);

        var maximumByAttr = maximumShots > 0f
            ? Math.Ceiling((double)maximumShots)
            : double.PositiveInfinity;
        var offsets = new List<double>(Math.Min(maximumPreviewEvents,
            maximumByAttr >= maximumPreviewEvents ? maximumPreviewEvents : (int)maximumByAttr));
        var wasCapped = false;
        for (var index = 0; index <= maximumPreviewEvents; index++)
        {
            if (index >= maximumByAttr) break;
            var offset = firstDelay + index * (double)interval;
            if (offset > duration) break;
            if (offsets.Count >= maximumPreviewEvents)
            {
                wasCapped = true;
                break;
            }
            offsets.Add(offset);
        }
        return (offsets, !wasCapped,
            wasCapped ? "shot schedule exceeded the safe preview bound" : string.Empty);
    }

    private static double CountNativeTimedEvents60(
        IReadOnlyList<NativeTimedEvent> rawTimeline,
        double castOpportunities,
        double cycleSeconds,
        double firstCastOffsetSeconds)
    {
        const double previewWindowSeconds = 60d;
        if (!double.IsFinite(castOpportunities) || castOpportunities <= 0d
            || !double.IsFinite(cycleSeconds) || cycleSeconds <= 0d
            || !double.IsFinite(firstCastOffsetSeconds) || firstCastOffsetSeconds < 0d)
            return 0d;

        var timeline = rawTimeline
            .Where(entry => double.IsFinite(entry.OffsetSeconds) && entry.OffsetSeconds >= 0d
                            && entry.OffsetSeconds < previewWindowSeconds
                            && double.IsFinite(entry.Count) && entry.Count > 0d)
            .OrderBy(entry => entry.OffsetSeconds)
            .ToList();
        if (timeline.Count == 0) return 0d;

        var prefixCounts = new double[timeline.Count + 1];
        for (var index = 0; index < timeline.Count; index++)
            prefixCounts[index + 1] = prefixCounts[index] + timeline[index].Count;
        var castCount = Math.Min(6000, Math.Max(0,
            Convert.ToInt32(Math.Floor(castOpportunities + 0.0000001d), CultureInfo.InvariantCulture)));
        var total = 0d;
        for (var castIndex = 0; castIndex < castCount; castIndex++)
        {
            var castTime = firstCastOffsetSeconds + castIndex * cycleSeconds;
            if (castTime >= previewWindowSeconds) break;
            var remaining = previewWindowSeconds - castTime;
            // Upper-bound search for offsets strictly below the remaining time;
            // an event exactly at t=60 is outside the half-open objective.
            var low = 0;
            var high = timeline.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (timeline[middle].OffsetSeconds < remaining) low = middle + 1;
                else high = middle;
            }
            total += prefixCounts[low];
        }
        return double.IsFinite(total) ? total : 0d;
    }

    private static NativeSkillAlwaysOnPreview ReadNativeSkillAlwaysOnPreview(
        object skillPreview,
        object skillInfo,
        int level,
        NativeSkillExecutionGraph executionGraph)
    {
        var failures = new HashSet<string>(StringComparer.Ordinal);
        var contributions = new List<NativeStrictAbilityContribution>();
        var previewAttr = Read(skillPreview, "attrData");
        if (previewAttr is null)
            return new NativeSkillAlwaysOnPreview(contributions, false, "skill ability attributes are unavailable");

        // Preserve the native TSkillInfo.infoArr order. A static result301 is
        // acquired once and its signed delta is applied before the package is
        // re-previewed; it is not iterated to a fabricated fixed point.
        var displayedLevels = new Dictionary<int, int>();
        var displayedOrder = new List<int>();
        foreach (var explainId in ReadSequence(Read(skillInfo, "infoArr")).Select(ToInt).Where(id => id > 0))
        {
            var explain = InvokeStatic("TableData", "getTSkillExplainData", explainId);
            if ((ReadNullableInt(explain, "type") ?? -1) is not (3 or 4 or 103)) continue;
            var parameters = ReadSequence(Read(explain, "typeParam")).Select(ToInt).Where(value => value > 0).ToList();
            if (parameters.Count == 0) continue;
            var abilityId = parameters[0];
            var abilityLevel = parameters.Count > 1 ? Math.Max(1, parameters[1]) : Math.Max(1, level);
            if (!displayedLevels.ContainsKey(abilityId)) displayedOrder.Add(abilityId);
            displayedLevels[abilityId] = Math.Max(displayedLevels.GetValueOrDefault(abilityId), abilityLevel);
        }

        var triggeredIds = executionGraph.AbilityInvocations.Select(invocation => invocation.AbilityId).ToHashSet();
        foreach (var abilityId in displayedOrder)
        {
            if (triggeredIds.Contains(abilityId)) continue;
            var abilityLevel = displayedLevels[abilityId];
            var preview = InvokeStaticMany("AbilityData", "CreateByShow", abilityId, abilityLevel, previewAttr);
            var row = Read(preview, "tAbilityData") ?? ResolveAbilityTableRow(abilityId, abilityLevel);
            if (row is null)
            {
                failures.Add($"display ability {abilityId} is unavailable");
                continue;
            }
            var candidate = preview ?? row;
            if (!IsStrictAlwaysOnSelfAbility(candidate)) continue;
            if (preview is null)
            {
                failures.Add($"strict result301 ability {abilityId} has no side-effect-free preview");
                continue;
            }
            var deltas = ReadNativeAbilityResultAttrDeltas(preview);
            if (deltas.Count == 0)
            {
                failures.Add($"strict result301 ability {abilityId} produced no readable signed deltas");
                continue;
            }
            var resolvedId = ReadNullableInt(row, "id") ?? ReadNullableInt(preview, "id") ?? abilityId;
            var stackLimit = Math.Max(1, ReadNullableInt(row, "stack") ?? 1);
            contributions.Add(new NativeStrictAbilityContribution(resolvedId, stackLimit, deltas));
        }
        return new NativeSkillAlwaysOnPreview(
            contributions,
            failures.Count == 0,
            string.Join("; ", failures.OrderBy(value => value)));
    }

    private static object CreateStrictSkillPackageAdjustedAttr(
        object hero,
        IEnumerable<(int SkillId, int Level)> packageSkills,
        object attrData,
        bool applyCurrentEquipmentVariant,
        IEnumerable<GearCandidate>? candidateItems,
        out bool applied,
        IReadOnlyDictionary<int, int>? preAppliedAbilityCounts = null,
        IDictionary<(int SkillId, int Level), NativeSkillAlwaysOnPreview>? previewCache = null)
    {
        const int maxAbilityApplications = 256;
        var contributions = new List<NativeStrictAbilityContribution>();
        foreach (var entry in packageSkills
                     .Where(entry => entry.SkillId > 0)
                     .Select(entry => (entry.SkillId, Level: Math.Max(1, entry.Level)))
                     .Distinct()
                     .Take(maxAbilityApplications + 1))
        {
            if (contributions.Count >= maxAbilityApplications)
                throw new InvalidOperationException("The selected skill package exceeded the strict ability preview bound.");
            NativeSkillAlwaysOnPreview abilityPreview;
            if (previewCache is not null
                && previewCache.TryGetValue((entry.SkillId, entry.Level), out var cachedPreview))
            {
                abilityPreview = cachedPreview;
            }
            else
            {
                var preview = InvokeRequiredStaticMany("SkillData", "CreatePreview", entry.SkillId, entry.Level, attrData)
                              ?? throw new InvalidOperationException($"SkillData.CreatePreview({entry.SkillId}) returned no skill.");
                var variantEnabled = (candidateItems is not null && CandidateEnablesSkillVariant(candidateItems, entry.SkillId))
                                     || (applyCurrentEquipmentVariant && CurrentEquipmentEnablesSkillVariant(hero, entry.SkillId));
                if (variantEnabled) InvokeRequiredInstance(preview, "SetVariant", true);
                var info = Read(preview, "tSkillInfoData")
                           ?? throw new InvalidOperationException($"Skill {entry.SkillId} info is unavailable.");
                var displayedPowerIds = ReadSequence(Read(info, "infoArr")).Select(ToInt).Where(id => id > 0)
                    .Select(id => InvokeStatic("TableData", "getTSkillExplainData", id))
                    .Where(row => (ReadNullableInt(row, "type") ?? -1) == 2)
                    .Select(row => ReadSequence(Read(row, "typeParam")).Select(ToInt).FirstOrDefault())
                    .Where(id => id > 0).Distinct().ToList();
                var graph = ResolveNativeSkillExecutionGraph(preview, info, entry.Level, displayedPowerIds);
                abilityPreview = ReadNativeSkillAlwaysOnPreview(preview, info, entry.Level, graph);
                if (previewCache is not null)
                    previewCache[(entry.SkillId, entry.Level)] = abilityPreview;
            }
            if (!abilityPreview.IsComplete)
                throw new InvalidOperationException(
                    $"Skill {entry.SkillId} strict ability preview is incomplete: {abilityPreview.Failure}");
            contributions.AddRange(abilityPreview.Contributions);
        }

        return ApplyStrictAbilityContributions(
            attrData, contributions, out applied, preAppliedAbilityCounts);
    }

    private static object ApplyStrictAbilityContributions(
        object attrData,
        IEnumerable<NativeStrictAbilityContribution> rawContributions,
        out bool applied,
        IReadOnlyDictionary<int, int>? preAppliedAbilityCounts = null)
    {
        const int maxAbilityApplications = 256;
        var contributions = rawContributions.ToList();
        applied = false;
        if (contributions.Count == 0) return attrData;
        var adjusted = InvokeRequiredStaticMany("AttrData", "copyCreate", attrData)
                       ?? throw new InvalidOperationException("Skill ability-adjusted AttrData copy failed.");
        var appliedCounts = preAppliedAbilityCounts is null
            ? new Dictionary<int, int>()
            : new Dictionary<int, int>(preAppliedAbilityCounts);
        var applicationCount = 0;
        foreach (var contribution in contributions)
        {
            if (appliedCounts.GetValueOrDefault(contribution.AbilityId) >= contribution.StackLimit) continue;
            if (++applicationCount > maxAbilityApplications)
                throw new InvalidOperationException("The selected skill package exceeded the strict ability application bound.");
            foreach (var delta in contribution.AttrDeltas.OrderBy(entry => entry.Key))
            {
                var attrType = CreateEnum("EAttrType", delta.Key)
                               ?? throw new InvalidOperationException($"Unknown skill ability result attribute {delta.Key}.");
                InvokeRequiredInstance(adjusted, "ChangeAttr", attrType,
                    Convert.ToSingle(delta.Value, CultureInfo.InvariantCulture));
            }
            appliedCounts[contribution.AbilityId] = appliedCounts.GetValueOrDefault(contribution.AbilityId) + 1;
            applied = true;
        }
        return applied ? adjusted : attrData;
    }

    private static int GetPlannedSkillEffectiveLevel(
        object hero,
        PlannedSkillTarget planned,
        int skillId,
        IEnumerable<GearCandidate> candidateItems)
    {
        var granted = Math.Max(0, GetGrantedSkillLevel(candidateItems, skillId));
        if (skillId == planned.BaseSkillId)
            return Math.Max(1, Math.Max(planned.BaseSkillLevel, granted));
        var talentId = planned.TalentIds
            .Where(id => (ReadNullableInt(InvokeStatic("TableData", "getTTalentData", id), "skillId") ?? 0) == skillId)
            .OrderBy(id => id)
            .FirstOrDefault();
        var savedTarget = talentId > 0 ? planned.TargetSavedLevels.GetValueOrDefault(talentId) : 0;
        var runtimeTalent = ReadValues(Read(Read(hero, "heroTalentData"), "talentDic"))
            .FirstOrDefault(talent => (ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0) == talentId);
        var baseLevel = runtimeTalent is null ? 0 : GetTalentBaseLevelRequired(runtimeTalent);
        return Math.Max(1, Math.Max(baseLevel + savedTarget, granted));
    }

    private static object CreateNativeSummonAttr(object summonRow, int summonLevel, object produceAttr)
    {
        var inheritParameters = ReadSequence(Read(summonRow, "attrInheritParam"))
            .Select(value => Convert.ToDouble(value, CultureInfo.InvariantCulture)).ToList();
        if (inheritParameters.Count < 3 || inheritParameters.Take(3).Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("TSummon.attrInheritParam does not contain three finite coefficients.");

        var extraRates = ReadFloatMatrixRows(Read(summonRow, "extraAttrInheritRateArr"), 2);
        var summonType = ReadNullableInt(summonRow, "type") ?? 0;
        var baseRate = Convert.ToDouble(Read(summonRow, "attrInheritRate") ?? 0d, CultureInfo.InvariantCulture);
        if (!double.IsFinite(baseRate))
            throw new InvalidOperationException("TSummon.attrInheritRate is not finite.");
        var levelValue = summonLevel * (double)summonLevel * inheritParameters[0]
                         + summonLevel * inheritParameters[1]
                         + inheritParameters[2];
        if (!double.IsFinite(levelValue))
            throw new InvalidOperationException("TSummon inheritance level polynomial is not finite.");

        var rates = new Dictionary<int, float>();
        foreach (var attrRow in ReadValues(ReadStatic("TableData", "TAttrDict")))
        {
            var attrId = ReadNullableInt(attrRow, "id") ?? 0;
            var inheritType = ReadNullableInt(attrRow, "inheritType") ?? 0;
            if (attrId <= 0 || inheritType <= 0 || inheritType > summonType) continue;
            var extra = extraRates
                .Where(row => row.Length >= 2 && Convert.ToInt32(row[0], CultureInfo.InvariantCulture) == attrId)
                .Select(row => row[1]).FirstOrDefault();
            var rate = (baseRate + extra) * levelValue / 100d;
            if (!double.IsFinite(rate) || rate < float.MinValue || rate > float.MaxValue)
                throw new InvalidOperationException($"Summon inheritance rate for attribute {attrId} is invalid.");
            rates[attrId] = (float)rate;
        }

        var attrDataType = GameType("AttrData")
                           ?? throw new InvalidOperationException("AttrData type is unavailable.");
        var createPercent = attrDataType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(method => method.Name == "CreatePercent" && method.GetParameters().Length == 4)
                            ?? throw new MissingMethodException("AttrData", "CreatePercent");
        var dictionaryType = createPercent.GetParameters()[2].ParameterType;
        var nativeRates = Activator.CreateInstance(dictionaryType)
                          ?? throw new InvalidOperationException("Native summon inheritance dictionary allocation failed.");
        var addMethod = dictionaryType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "Add" && method.GetParameters().Length == 2
                                      && method.GetParameters()[1].ParameterType == typeof(float))
                        ?? throw new MissingMethodException(dictionaryType.FullName, "Add");
        foreach (var entry in rates.OrderBy(entry => entry.Key))
        {
            var attrType = CreateEnum("EAttrType", entry.Key)
                           ?? throw new InvalidOperationException($"Unknown inherited attribute {entry.Key}.");
            addMethod.Invoke(nativeRates, new object[] { attrType, entry.Value });
        }

        var summonAttrRows = Read(summonRow, "attrArr")
                             ?? throw new InvalidOperationException("TSummon.attrArr is unavailable.");
        var ownerType = CreateEnum("EAttrOwnType", 1)
                        ?? throw new InvalidOperationException("Summon body owner type is unavailable.");
        object summonAttr;
        try
        {
            summonAttr = createPercent.Invoke(null, new[] { ownerType, produceAttr, nativeRates, summonAttrRows })
                         ?? throw new InvalidOperationException("AttrData.CreatePercent returned no summon attributes.");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException($"AttrData.CreatePercent failed: {error.InnerException.Message}", error.InnerException);
        }

        foreach (var row in ReadFloatMatrixRows(Read(summonRow, "levelAttrArr"), 2))
        {
            if (row.Length < 2 || !double.IsFinite(row[0]) || !double.IsFinite(row[1]))
                throw new InvalidOperationException("TSummon.levelAttrArr contains an invalid row.");
            var attrId = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
            if (attrId <= 0) throw new InvalidOperationException($"TSummon.levelAttrArr contains attribute {attrId}.");
            var attrType = CreateEnum("EAttrType", attrId)
                           ?? throw new InvalidOperationException($"Unknown summon level attribute {attrId}.");
            var delta = summonLevel * row[1];
            if (!double.IsFinite(delta) || delta < float.MinValue || delta > float.MaxValue)
                throw new InvalidOperationException($"Summon level attribute {attrId} is invalid.");
            InvokeRequiredInstance(summonAttr, "ChangeAttr", attrType, (float)delta);
        }
        return summonAttr;
    }

    private static (List<double> Durations, bool IsComplete, string Failure) BuildSummonActiveDurations(
        object parentSkillPreview,
        NativeSummonInvocation invocation,
        object summonRow,
        double castOpportunities,
        double cycleSeconds,
        double spawnOffsetSeconds)
    {
        var failures = new HashSet<string>(StringComparer.Ordinal);
        var hasCountGate = false;
        foreach (var rawCondition in ReadList(Read(parentSkillPreview, "conditionList")))
        {
            var condition = ReadSequence(rawCondition).Select(ToInt).ToList();
            if (condition.Count == 0) continue;
            if (condition.Count >= 2 && condition[0] == 2 && condition[1] == invocation.SummonId)
            {
                hasCountGate = true;
                continue;
            }
            failures.Add($"summon {invocation.SummonId} has an unsupported cast condition");
        }
        if (castOpportunities <= 0d)
            failures.Add($"summon {invocation.SummonId} has no proven parent cast opportunity");
        if (!double.IsFinite(cycleSeconds) || cycleSeconds <= 0d)
            failures.Add($"summon {invocation.SummonId} has no proven parent cast cycle");
        if (!double.IsFinite(spawnOffsetSeconds) || spawnOffsetSeconds < 0d)
            failures.Add($"summon {invocation.SummonId} has no proven spawn offset");
        if (failures.Count > 0)
            return (new List<double>(), false,
                string.Join("; ", failures.OrderBy(value => value)));

        var rawLifeTime = Convert.ToDouble(Read(summonRow, "lifeTime") ?? 0d, CultureInfo.InvariantCulture);
        if (!double.IsFinite(rawLifeTime))
            return (new List<double>(), false, $"summon {invocation.SummonId} lifetime is invalid");
        var lifeTime = double.PositiveInfinity;
        if (rawLifeTime > 0d)
        {
            var lifeAttr = CreateEnum("EAttrType", 190)
                           ?? throw new InvalidOperationException("minionTimeUp attribute is unavailable.");
            var lifeRate = Convert.ToDouble(
                InvokeRequiredInstance(invocation.ProduceAttr, "GetAttrUpRate", lifeAttr) ?? 0d,
                CultureInfo.InvariantCulture);
            if (!double.IsFinite(lifeRate) || lifeRate < 0d)
                return (new List<double>(), false, $"summon {invocation.SummonId} lifetime rate is invalid");
            lifeTime = rawLifeTime * lifeRate;
        }

        var castCount = Math.Min(6000, Math.Max(0,
            Convert.ToInt32(Math.Floor(castOpportunities + 0.0000001d), CultureInfo.InvariantCulture)));
        var maxCount = ReadNullableInt(summonRow, "maxCount") ?? 0;
        if (hasCountGate && maxCount <= 0)
            return (new List<double>(), false,
                $"summon {invocation.SummonId} has a count gate without a positive native maxCount");
        var expiries = new List<double>();
        var durations = new List<double>();
        for (var castIndex = 0; castIndex < castCount; castIndex++)
        {
            var spawnTime = castIndex * cycleSeconds + Math.Max(0d, spawnOffsetSeconds);
            if (spawnTime >= 60d) break;
            expiries.RemoveAll(expiry => expiry <= spawnTime + 0.0000001d);
            if (hasCountGate && expiries.Count >= maxCount) continue;
            for (var index = 0; index < invocation.CountPerCast; index++)
            {
                if (durations.Count >= 6000)
                {
                    failures.Add($"summon {invocation.SummonId} preview exceeded the instance bound");
                    break;
                }
                var expiry = double.IsPositiveInfinity(lifeTime) ? double.PositiveInfinity : spawnTime + lifeTime;
                expiries.Add(expiry);
                durations.Add(Math.Max(0d, Math.Min(60d, expiry) - spawnTime));
            }
            if (durations.Count >= 6000) break;
        }
        return (durations, failures.Count == 0,
            string.Join("; ", failures.OrderBy(value => value)));
    }

    private static NativeSummonEvaluation EvaluateNativeSummonInvocation(
        object hero,
        object parentSkillPreview,
        NativeSummonInvocation invocation,
        double parentCastOpportunities,
        double parentCycleSeconds,
        double spawnOffsetSeconds)
    {
        var failures = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var summonRow = InvokeStatic("TableData", "getTSummonData", invocation.SummonId)
                            ?? throw new InvalidOperationException($"TSummon {invocation.SummonId} is unavailable.");
            var summonAttr = CreateNativeSummonAttr(summonRow, invocation.Level, invocation.ProduceAttr);
            var active = BuildSummonActiveDurations(
                parentSkillPreview, invocation, summonRow,
                parentCastOpportunities, parentCycleSeconds, spawnOffsetSeconds);
            if (!active.IsComplete && !string.IsNullOrWhiteSpace(active.Failure)) failures.Add(active.Failure);
            if (active.Durations.Count == 0)
                return new NativeSummonEvaluation(0d, 0d, 0d, 0d, 0d,
                    failures.Count == 0, string.Join("; ", failures));

            var directActions = ReadSequence(Read(summonRow, "actionArr")).Select(ToInt).Where(id => id > 0).ToList();
            foreach (var actionId in directActions)
            {
                var action = InvokeStatic("TableData", "getTActionData", actionId);
                var hasCombatEffect = (ReadNullableInt(action, "emitterId") ?? 0) > 0
                                      || (ReadNullableInt(action, "produceId") ?? 0) > 0
                                      || ReadSequence(Read(action, "triggerArr")).Select(ToInt).Any(id => id > 0);
                if (hasCombatEffect)
                    failures.Add($"summon {invocation.SummonId} direct action {actionId} requires combat AI");
            }
            if (ReadSequence(Read(summonRow, "abilityArr")).Select(ToInt).Any(id => id > 0))
                failures.Add($"summon {invocation.SummonId} native abilities require activation state");

            var skillEntries = new List<(int Index, object Preview, NativeSkillRoleProfile Role)>();
            var skillIndex = 0;
            foreach (var summonSkillId in ReadSequence(Read(summonRow, "skillArr")).Select(ToInt).Where(id => id > 0))
            {
                var summonSkillPreview = InvokeRequiredStaticMany(
                    "SkillData", "CreatePreview", summonSkillId, invocation.Level, summonAttr)
                    ?? throw new InvalidOperationException($"Summon skill {summonSkillId} preview failed.");
                var role = ReadNativeSkillRoleProfile(
                    hero, summonSkillId, invocation.Level, summonAttr, false, null, false);
                if (!role.IsComplete && !string.IsNullOrWhiteSpace(role.Failure)) failures.Add(role.Failure);
                skillEntries.Add((skillIndex++, summonSkillPreview, role));
            }
            skillEntries.Sort((left, right) =>
            {
                var main = (ReadNullableInt(left.Preview, "skillMainType") ?? 0)
                           .CompareTo(ReadNullableInt(right.Preview, "skillMainType") ?? 0);
                if (main != 0) return main;
                var leftAttr = Read(left.Preview, "attrData");
                var rightAttr = Read(right.Preview, "attrData");
                if (leftAttr is null || rightAttr is null) return left.Index.CompareTo(right.Index);
                var cooldownOrder = Convert.ToInt32(Math.Round(
                    (ReadAttrRequired(leftAttr, 2001) - ReadAttrRequired(rightAttr, 2001)) * 100d,
                    MidpointRounding.ToEven), CultureInfo.InvariantCulture);
                return cooldownOrder != 0 ? cooldownOrder : left.Index.CompareTo(right.Index);
            });

            double SkillDamagePerCast(NativeSkillRoleProfile role)
                => role.CastOpportunities > 0d
                    ? role.DamageByType.Values.Where(value => value > 0d).Sum() / role.CastOpportunities
                    : 0d;
            var survivalCapacity = Math.Max(0d,
                ReadAttrRequired(summonAttr, 5)
                + (ReadAttrRequired(summonAttr, 3) + ReadAttrRequired(summonAttr, 4)) * 2d
                + ReadAttrRequired(summonAttr, 7) * 4d
                + (ReadAttrRequired(summonAttr, 32) + ReadAttrRequired(summonAttr, 85)
                   + ReadAttrRequired(summonAttr, 86)) * 2d
                + Enumerable.Range(61, 6).Sum(id => Math.Max(0d, ReadAttrRequired(summonAttr, id))) * 1.5d);
            var passiveSupport = skillEntries.Select(entry => entry.Role.AbilitySupport).DefaultIfEmpty(0d).Max();
            var passiveDefense = skillEntries.Select(entry => entry.Role.AbilityDefense).DefaultIfEmpty(0d).Max();
            var passiveMinion = skillEntries.Select(entry => entry.Role.AbilityMinion).DefaultIfEmpty(0d).Max();
            var totalDamage = 0d;
            var totalSurvival = 0d;
            var totalSupport = 0d;
            var totalDefense = 0d;
            var totalMinion = 0d;
            var schedulerEvents = 0;
            var schedulerExhausted = false;
            foreach (var duration in active.Durations.Where(value => value > 0d))
            {
                totalSurvival += survivalCapacity * Math.Min(1d, duration / 60d);
                totalSupport += passiveSupport * Math.Min(1d, duration / 60d);
                totalDefense += passiveDefense * Math.Min(1d, duration / 60d);
                totalMinion += passiveMinion * Math.Min(1d, duration / 60d);
                if (skillEntries.Count == 0 || schedulerExhausted) continue;

                var nextReady = new double[skillEntries.Count];
                var disabled = new bool[skillEntries.Count];
                var time = 0d;
                for (var guard = 0; guard < 6000 && time < duration; guard++)
                {
                    if (++schedulerEvents > 12000)
                    {
                        schedulerExhausted = true;
                        failures.Add($"summon {invocation.SummonId} shared scheduler exceeded its package event bound");
                        break;
                    }
                    var selected = -1;
                    for (var index = 0; index < skillEntries.Count; index++)
                    {
                        if (!disabled[index] && nextReady[index] <= time + 0.0000001d)
                        {
                            selected = index;
                            break;
                        }
                    }
                    if (selected < 0)
                    {
                        var next = Enumerable.Range(0, skillEntries.Count)
                            .Where(index => !disabled[index])
                            .Select(index => nextReady[index]).DefaultIfEmpty(double.PositiveInfinity).Min();
                        if (!double.IsFinite(next) || next >= duration) break;
                        time = Math.Max(time, next);
                        continue;
                    }

                    var role = skillEntries[selected].Role;
                    if (role.CastOpportunities <= 0d || role.ActionSecondsPerCast <= 0d
                        || !double.IsFinite(role.ActionSecondsPerCast)
                        || role.HpCostPerCast > 0d || role.MpCostPerCast > 0d)
                    {
                        disabled[selected] = true;
                        failures.Add($"summon {invocation.SummonId} skill action requires an unmodeled cast resource or timing");
                        continue;
                    }
                    if (time + role.ActionSecondsPerCast > duration) break;
                    var cycle = 60d / role.CastOpportunities;
                    if (!double.IsFinite(cycle) || cycle <= 0d)
                    {
                        disabled[selected] = true;
                        failures.Add($"summon {invocation.SummonId} skill cycle is invalid");
                        continue;
                    }
                    totalDamage += SkillDamagePerCast(role);
                    nextReady[selected] = time + cycle;
                    time += role.ActionSecondsPerCast;
                    if (guard == 5999)
                        failures.Add($"summon {invocation.SummonId} shared scheduler exceeded its event bound");
                }
            }
            return new NativeSummonEvaluation(
                totalDamage, totalSurvival, totalSupport, totalDefense, totalMinion,
                failures.Count == 0, string.Join("; ", failures.OrderBy(value => value)));
        }
        catch (Exception error)
        {
            failures.Add($"summon {invocation.SummonId} preview failed: {error.GetBaseException().Message}");
            return new NativeSummonEvaluation(0d, 0d, 0d, 0d, 0d, false,
                string.Join("; ", failures.OrderBy(value => value)));
        }
    }

    private static NativeSkillRoleProfile ReadNativeSkillRoleProfile(object hero, int skillId, int level,
        object? attrOverride = null, bool applyCurrentEquipmentVariant = true,
        IEnumerable<GearCandidate>? candidateItems = null, bool includeSummonDetails = true,
        bool applyOwnAlwaysOnAbilityAttrs = true)
    {
        var damage = new Dictionary<int, double>();
        if (skillId <= 0) return new NativeSkillRoleProfile(damage, 0d, 0d, false, 0d, 0d, 0d, 0d, 0d, 0d, 1d);
        var attr = attrOverride ?? Read(hero, "attrData") ?? throw new InvalidOperationException("Hero AttrData is unavailable.");
        var preview = InvokeRequiredStaticMany("SkillData", "CreatePreview", skillId, Math.Max(1, level), attr)
                      ?? throw new InvalidOperationException($"SkillData.CreatePreview({skillId}) returned no skill.");
        var variantEnabled = (candidateItems is not null && CandidateEnablesSkillVariant(candidateItems, skillId))
                             || (applyCurrentEquipmentVariant && CurrentEquipmentEnablesSkillVariant(hero, skillId));
        if (variantEnabled)
            InvokeRequiredInstance(preview, "SetVariant", true);
        var info = Read(preview, "tSkillInfoData") ?? throw new InvalidOperationException($"Skill {skillId} info is unavailable.");
        var explainRows = ReadSequence(Read(info, "infoArr")).Select(ToInt).Where(id => id > 0)
            .Select(id => InvokeStatic("TableData", "getTSkillExplainData", id))
            .Where(row => row is not null).Cast<object>().ToList();
        var powerIds = explainRows
            .Where(explain => (ReadNullableInt(explain, "type") ?? -1) == 2)
            .Select(explain => ReadSequence(Read(explain, "typeParam")).Select(ToInt).FirstOrDefault())
            .Where(id => id > 0).Distinct().ToList();
        var executionGraph = ResolveNativeSkillExecutionGraph(preview, info, Math.Max(1, level), powerIds);
        if (applyOwnAlwaysOnAbilityAttrs)
        {
            var alwaysOn = ReadNativeSkillAlwaysOnPreview(
                preview, info, Math.Max(1, level), executionGraph);
            if (!alwaysOn.IsComplete)
                throw new InvalidOperationException(
                    $"Skill {skillId} strict ability preview is incomplete: {alwaysOn.Failure}");
            var adjusted = ApplyStrictAbilityContributions(attr, alwaysOn.Contributions, out var applied);
            if (applied)
            {
                // Exactly one recursion is permitted: the first pass extracts
                // native signed result301 deltas; the guarded second pass
                // recomputes PowerData/cooldown/summons against that AttrData.
                return ReadNativeSkillRoleProfile(
                    hero, skillId, level, adjusted, applyCurrentEquipmentVariant,
                    candidateItems, includeSummonDetails, false);
            }
        }
        var roleFailures = new HashSet<string>(StringComparer.Ordinal);
        if (!executionGraph.IsComplete && !string.IsNullOrWhiteSpace(executionGraph.Failure))
            roleFailures.Add(executionGraph.Failure);
        var heal = 0d;
        var shield = 0d;
        var abilitySupport = 0d;
        var abilityDefense = 0d;
        var abilityMinion = 0d;
        var timedPowerInvocations = new List<NativePowerInvocation>();
        foreach (var invocation in executionGraph.PowerInvocations
                     .OrderBy(entry => entry.PowerId).ThenBy(entry => entry.EventsPerCast))
        {
            if (invocation.EventTimeline is not null)
            {
                // Emitter-origin power events retain their per-cast offsets until
                // the native cast cycle below is known. Multiplying a full emitter
                // lifetime by every cast would overcount the tail of the 60 s window.
                timedPowerInvocations.Add(invocation);
                continue;
            }
            var powerId = invocation.PowerId;
            var eventsPerCast = invocation.EventsPerCast;
            var power = invocation.PowerPreview;
            var powerType = ReadNullableInt(Read(power, "tPowerData"), "type") ?? 0;
            if (powerType == 1)
            {
                foreach (var entry in ReadEntries(Read(power, "dmgPowerDic")))
                {
                    var type = ToInt(Read(entry, "Key") ?? 0);
                    var value = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture);
                    if (type <= 0 || !double.IsFinite(value) || value <= 0d) continue;
                    damage[type] = damage.GetValueOrDefault(type) + value * eventsPerCast;
                }
            }
            else if (powerType == 2)
            {
                var value = Convert.ToDouble(Read(power, "power") ?? 0d, CultureInfo.InvariantCulture);
                if (double.IsFinite(value) && value > 0d) heal += value * eventsPerCast;
            }
            else if (powerType == 3)
            {
                var value = Convert.ToDouble(Read(power, "power") ?? 0d, CultureInfo.InvariantCulture);
                if (double.IsFinite(value) && value > 0d) shield += value * eventsPerCast;
            }
            else roleFailures.Add($"power {powerId} uses unsupported type {powerType}");
        }
        var previewAttr = Read(preview, "attrData") ?? attr;
        var displayedAbilityLevels = new Dictionary<int, int>();
        foreach (var explain in explainRows.Where(row => (ReadNullableInt(row, "type") ?? -1) is 3 or 4 or 103))
        {
            var parameters = ReadSequence(Read(explain, "typeParam")).Select(ToInt).Where(value => value > 0).ToList();
            if (parameters.Count == 0) continue;
            var abilityId = parameters[0];
            var abilityLevel = parameters.Count > 1 ? Math.Max(1, parameters[1]) : Math.Max(1, level);
            displayedAbilityLevels[abilityId] = Math.Max(displayedAbilityLevels.GetValueOrDefault(abilityId), abilityLevel);
        }
        foreach (var entry in displayedAbilityLevels.OrderBy(entry => entry.Key))
        {
            var abilityId = entry.Key;
            var abilityLevel = entry.Value;
            var abilityPreview = InvokeStaticMany("AbilityData", "CreateByShow", abilityId, abilityLevel, previewAttr);
            var abilityRow = Read(abilityPreview, "tAbilityData") ?? ResolveAbilityTableRow(abilityId, abilityLevel);
            if (abilityRow is null)
                throw new InvalidOperationException($"AbilityData.CreateByShow({abilityId}) returned no ability.");
            if (executionGraph.AbilityInvocations.Any(invocation => invocation.AbilityId == abilityId))
            {
                // Trigger-created abilities need duration/cooldown/condition and
                // battle-target state. Counting their table description as an
                // always-on passive materially overstates buff and immunity skills.
                roleFailures.Add($"triggered ability {abilityId} requires battle-state simulation");
            }
            else if (IsUnconditionalAcquireAbility(abilityPreview ?? abilityRow))
            {
                var utility = GetNativeAbilityRoleUtility(abilityPreview ?? abilityRow);
                abilitySupport += utility.Support;
                abilityDefense += utility.Defense;
                abilityMinion += utility.Minion;
            }
            else
            {
                roleFailures.Add($"display ability {abilityId} is conditional or dynamic");
            }
        }
        foreach (var abilityId in executionGraph.AbilityInvocations.Select(invocation => invocation.AbilityId)
                     .Distinct().Where(id => !displayedAbilityLevels.ContainsKey(id)))
            roleFailures.Add($"triggered ability {abilityId} has no exact display preview");
        var nativeSpeed = Convert.ToDouble(
            InvokeRequiredInstance(previewAttr, "GetSkillSpeedRate", (object)null!)
            ?? throw new InvalidOperationException($"Skill {skillId} returned no native speed."),
            CultureInfo.InvariantCulture);
        if (!double.IsFinite(nativeSpeed) || nativeSpeed <= 0d)
            throw new InvalidOperationException($"Skill {skillId} returned an invalid native speed ({nativeSpeed}).");
        nativeSpeed = Math.Clamp(nativeSpeed, 0.05d, 20d);
        var actionId = ReadNullableInt(info, "actionId") ?? 0;
        var actionRow = actionId > 0 ? InvokeStatic("TableData", "getTActionData", actionId) : null;
        var actionTimingValues = ReadSequence(Read(actionRow, "typeInfo"))
            .Take(2)
            .Select(value =>
            {
                try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
                catch { return 0d; }
            })
            .ToList();
        var validAction = actionRow is not null && (ReadNullableInt(actionRow, "type") ?? 0) == 3
                                             && actionTimingValues.Count == 2
                                             && actionTimingValues.All(value => double.IsFinite(value) && value >= 0d)
                                             && actionTimingValues.Sum() > 0d;
        if (!validAction) roleFailures.Add($"skill action {actionId} has no proven cast timing");
        var nativeActionSeconds = validAction ? actionTimingValues.Sum() : 0d;
        var actionSecondsPerCast = validAction
            ? Math.Clamp(nativeActionSeconds / nativeSpeed, 0.01d, 60d)
            : 60d;
        var cooldown = Math.Max(0d, ReadAttrRequired(previewAttr, 2001));
        if (ReadAttrRequired(previewAttr, 3001) > 0d) cooldown = 0d;
        var cooldownSeconds = cooldown > 0.01d ? cooldown / nativeSpeed : 0d;
        var cycleSeconds = Math.Max(actionSecondsPerCast, cooldownSeconds);
        var spawnOffsetSeconds = validAction
            ? Math.Clamp(actionTimingValues[0] / nativeSpeed, 0d, 60d)
            : 60d;
        var castOpportunities = validAction && spawnOffsetSeconds < 60d
            ? Math.Clamp(1d + Math.Floor(
                Math.Max(0d, 60d - spawnOffsetSeconds - 0.0000001d)
                / Math.Max(0.01d, cycleSeconds)), 0d, 6000d)
            : 0d;
        var castCondition = Clean(ReadString(info, "castCondition") ?? string.Empty);
        if (castCondition.Length > 0)
        {
            roleFailures.Add($"skill cast condition '{castCondition}' needs a live target");
            castOpportunities = 0d;
        }

        var timedDamage = new Dictionary<int, double>();
        var timedHeal = 0d;
        var timedShield = 0d;
        foreach (var invocation in timedPowerInvocations)
        {
            var eventCount60 = CountNativeTimedEvents60(
                invocation.EventTimeline!, castOpportunities, cycleSeconds, spawnOffsetSeconds);
            if (eventCount60 <= 0d) continue;
            var powerId = invocation.PowerId;
            var power = invocation.PowerPreview;
            var powerType = ReadNullableInt(Read(power, "tPowerData"), "type") ?? 0;
            if (powerType == 1)
            {
                foreach (var entry in ReadEntries(Read(power, "dmgPowerDic")))
                {
                    var type = ToInt(Read(entry, "Key") ?? 0);
                    var value = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture);
                    if (type <= 0 || !double.IsFinite(value) || value <= 0d) continue;
                    // Periodic stacks/ticks are not modeled. Preserve one proven
                    // application per independently reached native power branch.
                    var multiplier = type is 7 or 8 ? 1d : eventCount60;
                    timedDamage[type] = timedDamage.GetValueOrDefault(type) + value * multiplier;
                }
            }
            else if (powerType == 2)
            {
                var value = Convert.ToDouble(Read(power, "power") ?? 0d, CultureInfo.InvariantCulture);
                if (double.IsFinite(value) && value > 0d) timedHeal += value * eventCount60;
            }
            else if (powerType == 3)
            {
                var value = Convert.ToDouble(Read(power, "power") ?? 0d, CultureInfo.InvariantCulture);
                if (double.IsFinite(value) && value > 0d) timedShield += value * eventCount60;
            }
            else roleFailures.Add($"power {powerId} uses unsupported type {powerType}");
        }

        var critChance = Math.Clamp(PercentRate(ReadAttrRequired(attr, 31)), 0d, 1d);
        var critDamage = Math.Max(0.5d, 0.5d + PercentRate(ReadAttrRequired(attr, 37)));
        foreach (var type in damage.Keys.ToList())
        {
            var critFactor = type is 7 or 8 ? 1d : Math.Max(0.1d, 1d + critChance * critDamage);
            if (type is 7 or 8)
            {
                // Stack/refresh/tick rules require battle state. Preserve only
                // one proven application as a conservative lower bound.
                roleFailures.Add($"periodic damage type {type} requires stack/tick simulation");
                damage[type] *= castOpportunities > 0d ? 1d : 0d;
            }
            else damage[type] *= castOpportunities * critFactor;
        }
        foreach (var entry in timedDamage)
        {
            var type = entry.Key;
            var critFactor = type is 7 or 8 ? 1d : Math.Max(0.1d, 1d + critChance * critDamage);
            if (type is 7 or 8)
                roleFailures.Add($"periodic damage type {type} requires stack/tick simulation");
            damage[type] = damage.GetValueOrDefault(type) + entry.Value * critFactor;
        }
        heal = heal * castOpportunities + timedHeal;
        shield = shield * castOpportunities + timedShield;
        var summon = executionGraph.SummonInvocations.Count > 0;
        var summonDamage = 0d;
        var summonSurvival = 0d;
        if (summon && !includeSummonDetails)
        {
            roleFailures.Add("nested summon execution is not expanded in a summon-skill preview");
        }
        else if (summon)
        {
            foreach (var invocation in executionGraph.SummonInvocations)
            {
                var evaluation = EvaluateNativeSummonInvocation(
                    hero, preview, invocation, castOpportunities, cycleSeconds, spawnOffsetSeconds);
                summonDamage += evaluation.Damage;
                summonSurvival += evaluation.Survival;
                abilitySupport += evaluation.Support;
                abilityDefense += evaluation.Defense;
                abilityMinion += evaluation.Minion;
                if (!evaluation.IsComplete && !string.IsNullOrWhiteSpace(evaluation.Failure))
                    roleFailures.Add(evaluation.Failure);
            }
        }
        var noCostRate = Math.Clamp(PercentRate(ReadAttrRequired(attr, 181)), 0d, 1d);
        var hpCostPerCast = Math.Max(0d, ReadAttrRequired(previewAttr, 2002)) * (1d - noCostRate);
        var mpCostPerCast = Math.Max(0d, ReadAttrRequired(previewAttr, 2003)) * (1d - noCostRate);
        var hpBudget60 = Math.Max(0d, ReadAttrRequired(attr, 5) * 0.90d
                                      + ReadAttrRequired(attr, 7) * NativeRateFactor(ReadAttrRequired(attr, 9)) * 60d);
        var mpBudget60 = Math.Max(0d, ReadAttrRequired(attr, 6)
                                      + ReadAttrRequired(attr, 8) * NativeRateFactor(ReadAttrRequired(attr, 10)) * 60d);
        return new NativeSkillRoleProfile(
            damage, heal, shield, summon, summonDamage, summonSurvival,
            abilitySupport, abilityDefense, abilityMinion, castOpportunities, actionSecondsPerCast,
            hpCostPerCast, mpCostPerCast, hpBudget60, mpBudget60,
            roleFailures.Count == 0,
            string.Join("; ", roleFailures.OrderBy(value => value)),
            roleFailures.Count == 0 ? 1d : 0.70d);
    }

    private static double ScoreMasteryTalentForObjective(object hero, object talent, HeroFocus focus,
        object objectiveAttr, IReadOnlyCollection<int> objectiveSkillIds,
        IReadOnlyCollection<GearCandidate>? candidateItems = null)
    {
        var definition = Read(talent, "tTalentData") ?? throw new InvalidOperationException("Mastery talent definition is unavailable.");
        var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
        if (masteryId <= 0) return double.NegativeInfinity;
        var cap = Math.Max(1, GetTalentLevelCap(talent));
        var preview = InvokeRequiredStaticMany("MasteryData", "CreateByShow", masteryId, cap, cap)
                      ?? throw new InvalidOperationException($"MasteryData.CreateByShow({masteryId}) returned no mastery.");
        var baseline = InvokeRequiredStaticMany("AttrData", "copyCreate", objectiveAttr)
                       ?? throw new InvalidOperationException("AttrData baseline copy failed.");
        var simulated = InvokeRequiredStaticMany("AttrData", "copyCreate", objectiveAttr)
                        ?? throw new InvalidOperationException("AttrData mastery copy failed.");
        foreach (var affix in ReadList(Read(preview, "affixList")))
        {
            var effectType = ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0;
            if (effectType == 1)
                InvokeRequiredInstance(affix, "SetActiveAttrData", simulated, true);
        }
        var numericDelta = ScoreSkillPackageObjective(
                               hero, objectiveSkillIds, simulated, focus, candidateItems)
                           - ScoreSkillPackageObjective(
                               hero, objectiveSkillIds, baseline, focus, candidateItems);
        return (numericDelta + ScoreMasteryAbilityObjective(preview, focus, simulated)) * 1000d;
    }

    private static double ScoreMasteryTalentCandidateForObjective(
        object hero,
        object talent,
        HeroFocus focus,
        object objectiveAttr,
        IReadOnlyCollection<int> objectiveSkillIds,
        IReadOnlyCollection<GearCandidate>? candidateItems = null)
    {
        try
        {
            return ScoreMasteryTalentForObjective(
                hero, talent, focus, objectiveAttr, objectiveSkillIds, candidateItems);
        }
        catch (Exception error)
        {
            var definition = Read(talent, "tTalentData");
            var talentId = ReadNullableInt(definition, "id") ?? 0;
            var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
            // Masteries are independent candidates. One unavailable strict
            // preview must not discard the otherwise usable skill pool.
            Plugin.DiagDebug(
                $"AUTO-SKILLS MASTERY CANDIDATE REJECTED|talent={talentId}|mastery={masteryId}|{error.GetBaseException().Message}");
            return double.NegativeInfinity;
        }
    }

    private static double ScoreMasteryAbilityObjective(
        object masteryPreview,
        HeroFocus focus,
        object attrData)
    {
        var support = 0d;
        var defense = 0d;
        var minion = 0d;
        var offense = 0d;
        foreach (var rawAffix in ReadList(Read(masteryPreview, "affixList")))
        {
            var affix = ResolveRuntimeAffix(rawAffix);
            var definition = Read(affix, "tAffixData");
            if ((ReadNullableInt(definition, "effectType") ?? 0) != 3) continue;
            var save = Read(affix, "saveData");
            var tableAbility = Read(affix, "tAbilityData");
            var abilityId = ReadNullableInt(save, "abilityId")
                            ?? ReadNullableInt(tableAbility, "id")
                            ?? ReadSequence(Read(definition, "effectParam")).Select(ToInt).FirstOrDefault();
            var abilityLevel = Math.Max(0, ReadNullableInt(save, "level") ?? 0);
            var ability = InvokeStaticMany("AbilityData", "CreateByShow", abilityId, abilityLevel, attrData)
                          ?? tableAbility
                          ?? ResolveAbilityTableRow(abilityId, abilityLevel);
            var utility = GetNativeAbilityRoleUtility(ability);
            support += utility.Support;
            defense += utility.Defense;
            minion += utility.Minion;
            if (ability is not null) offense += ScoreNativeAbilityResultOffense(ability, focus);
        }
        return focus.Key switch
        {
            "defense" => Math.Log10(1d + defense) * 4.5d,
            "support" => Math.Log10(1d + support) * 4.5d,
            "minion" => Math.Log10(1d + minion) * 3.2d,
            _ => Math.Log10(1d + offense) * 4d
        };
    }

    private static double ScoreSkillPackageObjective(
        object hero,
        IEnumerable<int> skillIds,
        object attrData,
        HeroFocus focus,
        IReadOnlyCollection<GearCandidate>? candidateItems = null)
    {
        var selected = skillIds.Where(id => id > 0).Distinct().ToList();
        if (selected.Count == 0) return ScoreHeroAttrObjective(attrData, focus);
        var baseSkillIds = ReadValues(ReadStatic("TableData", "TTalentDict"))
            .Where(IsBaseSkillDefinition)
            .Select(row => ReadNullableInt(row, "skillId") ?? 0).Where(id => id > 0).ToHashSet();
        var baseSkillId = selected.FirstOrDefault(baseSkillIds.Contains);
        if (baseSkillId <= 0) baseSkillId = selected[0];
        var packageAttr = CreateStrictSkillPackageAdjustedAttr(
            hero, selected.Select(id => (SkillId: id, Level: 1)), attrData, false,
            candidateItems, out _);
        var baseRole = ReadNativeSkillRoleProfile(hero, baseSkillId, 1, packageAttr, false,
            candidateItems, true, false);
        var activeRoles = selected.Where(id => id != baseSkillId)
            .Select(id => ReadNativeSkillRoleProfile(hero, id, 1, packageAttr, false,
                candidateItems, true, false));
        return ScoreHeroAttrObjective(packageAttr, focus)
               + ScoreSharedSkillPackage(baseRole, activeRoles, focus);
    }

    private static double ScoreNativeSkillRoleObjective(NativeSkillRoleProfile role, HeroFocus focus)
    {
        // Confidence is a conservative output-retention ratio: unknown branches
        // contribute zero, while the independently proven lower bound remains.
        // Scale native quantities before the logarithmic objective so a single
        // role and the same role inside BuildSharedSkillPackage rank identically.
        var confidence = Math.Clamp(role.Confidence, 0d, 1d);
        var damage = GetManualDamageAmount(role.DamageByType, focus) * confidence;
        var heal = role.Heal * confidence;
        var shield = role.Shield * confidence;
        var summonDamage = role.SummonDamage * confidence;
        var summonSurvival = role.SummonSurvival * confidence;
        var abilitySupport = role.AbilitySupport * confidence;
        var abilityDefense = role.AbilityDefense * confidence;
        var abilityMinion = role.AbilityMinion * confidence;
        var raw = focus.Key switch
        {
            "defense" => Math.Log10(1d + shield * 1.5d + heal + abilityDefense) * 4.5d
                         + Math.Log10(1d + damage) * 0.12d,
            "support" => Math.Log10(1d + heal * 1.4d + shield + abilitySupport) * 4.5d
                         + Math.Log10(1d + damage) * 0.10d,
            "minion" => Math.Log10(1d + summonDamage) * 3.5d
                         + Math.Log10(1d + summonSurvival) * 1.4d
                         + Math.Log10(1d + abilityMinion) * 3.2d
                         + Math.Log10(1d + damage) * 0.10d,
            _ => Math.Log10(1d + damage) * 2d
        };
        return double.IsFinite(raw) ? raw : double.NegativeInfinity;
    }

    private static bool HasProvenNativeMinionSignal(NativeSkillRoleProfile role)
        => role.SummonDamage > 0d || role.SummonSurvival > 0d || role.AbilityMinion > 0d;

    private static bool HasProvenNativeRoleSignal(NativeSkillRoleProfile role)
        => role.DamageByType.Any(entry => entry.Key > 0 && double.IsFinite(entry.Value) && entry.Value > 0d)
           || double.IsFinite(role.Heal) && role.Heal > 0d
           || double.IsFinite(role.Shield) && role.Shield > 0d
           || double.IsFinite(role.SummonDamage) && role.SummonDamage > 0d
           || double.IsFinite(role.SummonSurvival) && role.SummonSurvival > 0d
           || double.IsFinite(role.AbilitySupport) && role.AbilitySupport > 0d
           || double.IsFinite(role.AbilityDefense) && role.AbilityDefense > 0d
           || double.IsFinite(role.AbilityMinion) && role.AbilityMinion > 0d;

    private static bool IsNativeRoleRankable(NativeSkillRoleProfile role, HeroFocus focus)
    {
        if (!HasProvenNativeRoleSignal(role)) return false;
        var score = ScoreNativeSkillRoleObjective(role, focus);
        return double.IsFinite(score) && score > 0.000000001d;
    }

    private static double ScoreCurrentPreferredSkillPackage(object hero, object talentData,
        PreferredTalentPlan preferred, object attrData, HeroFocus focus)
        => ScorePreferredSharedSkillPackage(hero, talentData, preferred, attrData, focus, 0, -1);

    private static double ScorePreferredSharedSkillPackage(
        object hero,
        object talentData,
        PreferredTalentPlan preferred,
        object attrData,
        HeroFocus focus,
        int overrideSkillId,
        int overrideLevel)
    {
        var definitions = preferred.SkillTalentIds
            .Select(id => InvokeStatic("TableData", "getTTalentData", id))
            .Where(row => (ReadNullableInt(row, "skillId") ?? 0) > 0).Cast<object>().ToList();
        var baseSkillId = definitions.Where(IsBaseSkillDefinition)
            .Select(row => ReadNullableInt(row, "skillId") ?? 0).FirstOrDefault(id => id > 0);
        var activeSkillIds = definitions.Where(IsTransformableSkillDefinition)
            .Select(row => ReadNullableInt(row, "skillId") ?? 0).Where(id => id > 0).Distinct().ToList();
        if (baseSkillId <= 0) return ScoreHeroAttrObjective(attrData, focus);
        var selectedSkillIds = activeSkillIds.Append(baseSkillId).ToHashSet();
        var runtimeLevels = ReadValues(Read(talentData, "talentDic"))
            .Where(talent => selectedSkillIds.Contains(ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0))
            .GroupBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
            .Where(group => group.Key > 0)
            .ToDictionary(group => group.Key, group => Math.Max(1, group.Max(GetTalentLevel)));
        int EffectiveLevel(int skillId)
            => skillId == overrideSkillId ? overrideLevel : runtimeLevels.GetValueOrDefault(skillId, 1);
        var packageAttr = CreateStrictSkillPackageAdjustedAttr(
            hero,
            selectedSkillIds.Select(skillId => (SkillId: skillId, Level: EffectiveLevel(skillId)))
                .Where(entry => entry.Level > 0),
            attrData,
            true,
            null,
            out _);
        NativeSkillRoleProfile RoleFor(int skillId)
        {
            var level = EffectiveLevel(skillId);
            return level <= 0
                ? EmptyNativeSkillRole()
                : ReadNativeSkillRoleProfile(hero, skillId, level, packageAttr, true,
                    null, true, false);
        }
        return ScoreHeroAttrObjective(packageAttr, focus)
               + ScoreSharedSkillPackage(RoleFor(baseSkillId), activeSkillIds.Select(RoleFor), focus);
    }

    private static NativeSkillRoleProfile EmptyNativeSkillRole()
        => new(new Dictionary<int, double>(), 0d, 0d, false, 0d, 0d, 0d, 0d, 0d, 0d, 1d);

    private static void ApplyMasteryAttributePreview(object attrData, int masteryId, int level, int cap, bool active)
    {
        if (masteryId <= 0 || level <= 0) return;
        var preview = InvokeRequiredStaticMany("MasteryData", "CreateByShow", masteryId, level, cap)
                      ?? throw new InvalidOperationException($"MasteryData.CreateByShow({masteryId}) returned no mastery.");
        foreach (var affix in ReadList(Read(preview, "affixList")))
        {
            if ((ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0) != 1) continue;
            InvokeRequiredInstance(affix, "SetActiveAttrData", attrData, active);
        }
    }

    private static double ScoreTalentPointMarginalGain(object hero, object talentData, object talent,
        HeroFocus focus, PreferredTalentPlan preferred)
    {
        var definition = Read(talent, "tTalentData")
                         ?? throw new InvalidOperationException("Talent definition is unavailable.");
        var type = ReadNullableInt(definition, "type") ?? 0;
        var currentLevel = Math.Max(0, GetTalentLevel(talent));
        var nextLevel = currentLevel + 1;
        if (nextLevel > GetTalentLevelCap(talent)) return double.NegativeInfinity;
        var heroAttr = Read(hero, "attrData") ?? throw new InvalidOperationException("Hero AttrData is unavailable.");

        if (type == 1)
        {
            var skillId = ReadNullableInt(definition, "skillId") ?? 0;
            if (skillId <= 0 || !preferred.PreferredSkillIds.Contains(skillId)) return double.NegativeInfinity;
            var before = ScorePreferredSharedSkillPackage(
                hero, talentData, preferred, heroAttr, focus, skillId, currentLevel);
            var after = ScorePreferredSharedSkillPackage(
                hero, talentData, preferred, heroAttr, focus, skillId, nextLevel);
            return after - before;
        }

        if (type == 2)
        {
            var talentId = ReadNullableInt(definition, "id") ?? 0;
            if (!preferred.MasteryTalentIds.Contains(talentId)) return double.NegativeInfinity;
            var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
            if (masteryId <= 0) return double.NegativeInfinity;
            var baseline = InvokeRequiredStaticMany("AttrData", "copyCreate", heroAttr)
                           ?? throw new InvalidOperationException("AttrData mastery baseline copy failed.");
            var simulated = InvokeRequiredStaticMany("AttrData", "copyCreate", heroAttr)
                            ?? throw new InvalidOperationException("AttrData mastery simulation copy failed.");
            var cap = GetTalentLevelCap(talent);
            ApplyMasteryAttributePreview(simulated, masteryId, currentLevel, cap, false);
            ApplyMasteryAttributePreview(simulated, masteryId, nextLevel, cap, true);
            var numericDelta = ScoreCurrentPreferredSkillPackage(hero, talentData, preferred, simulated, focus)
                               - ScoreCurrentPreferredSkillPackage(hero, talentData, preferred, baseline, focus);
            var beforeAbility = 0d;
            if (currentLevel > 0)
            {
                var beforePreview = InvokeRequiredStaticMany("MasteryData", "CreateByShow", masteryId, currentLevel, cap)
                                    ?? throw new InvalidOperationException($"MasteryData.CreateByShow({masteryId}) returned no current preview.");
                beforeAbility = ScoreMasteryAbilityObjective(beforePreview, focus, baseline);
            }
            var afterPreview = InvokeRequiredStaticMany("MasteryData", "CreateByShow", masteryId, nextLevel, cap)
                               ?? throw new InvalidOperationException($"MasteryData.CreateByShow({masteryId}) returned no next preview.");
            return numericDelta + ScoreMasteryAbilityObjective(afterPreview, focus, simulated) - beforeAbility;
        }

        return double.NegativeInfinity;
    }

    private static object CreateTalentNeutralObjectiveAttr(object hero, IEnumerable<object> gridTalents)
    {
        var live = Read(hero, "attrData") ?? throw new InvalidOperationException("Hero AttrData is unavailable.");
        var clean = InvokeRequiredStaticMany("AttrData", "copyCreate", live)
                    ?? throw new InvalidOperationException("AttrData talent-neutral copy failed.");
        foreach (var talent in gridTalents)
        {
            var definition = Read(talent, "tTalentData");
            if ((ReadNullableInt(definition, "type") ?? 0) != 2 || GetSavedTalentLevel(talent) <= 0) continue;
            foreach (var affix in ReadList(Read(Read(talent, "masteryData"), "affixList")))
            {
                if ((ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0) != 1) continue;
                InvokeRequiredInstance(affix, "SetActiveAttrData", clean, false);
            }
            // Reset removes only the saved investment. Equipment/inspiration
            // baseLevel remains active, so restore that exact post-reset layer
            // after removing the current effective mastery.
            var baseLevel = Math.Min(GetTalentBaseLevelRequired(talent), GetTalentLevelCap(talent));
            var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
            if (baseLevel <= 0 || masteryId <= 0) continue;
            var basePreview = InvokeRequiredStaticMany("MasteryData", "CreateByShow", masteryId, baseLevel, GetTalentLevelCap(talent))
                              ?? throw new InvalidOperationException($"MasteryData.CreateByShow({masteryId}) returned no base-level mastery.");
            foreach (var affix in ReadList(Read(basePreview, "affixList")))
            {
                if ((ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0) != 1) continue;
                InvokeRequiredInstance(affix, "SetActiveAttrData", clean, true);
            }
        }
        return clean;
    }

    private static double ScoreHeroAttrObjective(object attrData, HeroFocus focus)
    {
        double Positive(int attrId) => Math.Max(0d, ReadAttrRequired(attrData, attrId));
        double Sum(params int[] attrIds) => attrIds.Sum(Positive);

        var physical = Positive(1);
        var elemental = Positive(2);
        var attack = focus.Key is "physical" or "bleed" ? physical
            : focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion" ? elemental
            : Math.Max(physical, elemental);
        var crit = Sum(31, 37);
        var hp = Positive(5);
        var defence = Sum(3, 4);
        var sustain = Sum(7, 220, 222, 224);
        var avoidance = Sum(32, 34, 36, 85, 86);
        var resistance = Sum(61, 62, 63, 64, 65, 66, 130, 131, 132);
        var preventionFlags = new[] { 151, 152, 153, 154, 155, 156, 157, 158, 184, 188, 202 }
            .Sum(id => Math.Min(1d, Positive(id)));
        var defensePenalty = Math.Min(1d, Positive(200)) + Math.Min(1d, Positive(201));
        var support = Sum(81, 82, 83, 84, 91, 92, 93, 94, 185, 191);
        var minion = Positive(25) * 50d + Positive(190);
        // HP/defence/regen are native final GetAttrValue outputs. Their up-rate
        // and conversion buckets must not be added a second time.
        var survival = hp + defence * 2d + sustain * 4d + avoidance * 2d
                       + resistance * 1.5d;
        return focus.Key switch
        {
            "defense" => Math.Log10(1d + survival) * 3.2d + preventionFlags * 0.65d
                         - defensePenalty * 2.5d + Math.Log10(1d + attack) * 0.35d,
            "support" => Math.Log10(1d + support * 10d) * 3.2d
                         + Math.Log10(1d + survival) * 0.8d - defensePenalty,
            "minion" => Math.Log10(1d + minion) * 3d + Math.Log10(1d + attack),
            "crit" => Math.Log10(1d + attack) * 2d + Math.Log10(1d + crit) * 2d,
            _ => Math.Log10(1d + attack) * 2d + Math.Log10(1d + crit)
        };
    }

    private static PreferredTalentPlan SelectPreferredActiveSkillTargets(object hero, object talentData, PreferredTalentPlan preferred, HeroFocus focus)
    {
        var desiredRows = preferred.SkillTalentIds
            .Select((talentId, index) => (TalentId: talentId, Index: index, Definition: InvokeStatic("TableData", "getTTalentData", talentId)))
            .Where(entry => IsTransformableSkillDefinition(entry.Definition))
            .Select(entry => (entry.TalentId, entry.Index, Definition: entry.Definition!, SkillId: ReadNullableInt(entry.Definition, "skillId") ?? 0))
            .Where(entry => entry.SkillId > 0)
            .GroupBy(entry => entry.SkillId)
            .Select(group => group.OrderBy(entry => entry.Index).First())
            .ToList();
        if (desiredRows.Count == 0) return preferred;

        // ShrineWashData.WashHero delegates to HeroTalentData.ReCreateTalent.
        // It can replace only currently unlocked, non-fixed active-skill rows;
        // the number of usable rows grows with hero level and must be measured
        // every run instead of being treated as a fixed four-slot loadout.
        var current = GetTransformableTalents(talentData).ToList();
        var unlocked = current.Where(talent => !IsTalentLockedRequired(talent)).ToList();
        var allDesiredSkillIds = desiredRows.Select(entry => entry.SkillId).ToHashSet();
        var fixedCurrentSkillIds = unlocked
            .Where(IsTalentUnreplaceable)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var fixedUnwantedRows = unlocked.Count(talent =>
        {
            if (!IsTalentUnreplaceable(talent)) return false;
            var skillId = ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0;
            return !allDesiredSkillIds.Contains(skillId);
        });
        var capacity = Math.Min(desiredRows.Count, Math.Max(0, unlocked.Count - fixedUnwantedRows));

        var ranked = desiredRows.Select(entry =>
        {
            return (entry.TalentId, entry.Index, entry.SkillId,
                Fixed: fixedCurrentSkillIds.Contains(entry.SkillId),
                Objective: preferred.ObjectiveScores?.GetValueOrDefault(entry.TalentId) ?? 0d);
        }).ToList();

        var selectedRows = ranked
            // Never discard a matching skill that the user explicitly fixed.
            .OrderByDescending(entry => entry.Fixed)
            .ThenByDescending(entry => entry.Objective)
            .ThenBy(entry => entry.Index)
            .ThenBy(entry => entry.TalentId)
            .Take(capacity)
            .ToList();
        var selectedSkillIds = selectedRows.Select(entry => entry.SkillId).ToHashSet();
        var excludedSkillIds = desiredRows.Select(entry => entry.SkillId).Where(id => !selectedSkillIds.Contains(id)).ToList();
        Plugin.DiagInfo(
            $"AUTO-SKILLS TARGETS|unlocked={unlocked.Count}|fixedUnwanted={fixedUnwantedRows}|desired={string.Join(',', desiredRows.Select(entry => entry.SkillId))}|selected={string.Join(',', selectedRows.Select(entry => entry.SkillId))}|excluded={string.Join(',', excludedSkillIds)}");

        // Keep base/non-washable skills, then order transformable targets by the
        // same priority used above. The shrine can favorite only a limited
        // number of missing rows; preserving the original guide order here used
        // to put a cornerstone skill such as Divine Shelter last again.
        var selectedTalentIds = preferred.SkillTalentIds
            .Where(talentId => !IsTransformableSkillDefinition(InvokeStatic("TableData", "getTTalentData", talentId)))
            .Concat(selectedRows.Select(entry => entry.TalentId))
            .Distinct().ToList();
        var selectedPreferredSkillIds = selectedTalentIds
            .Select(talentId => InvokeStatic("TableData", "getTTalentData", talentId))
            .Select(definition => ReadNullableInt(definition, "skillId") ?? 0)
            .Where(skillId => skillId > 0).ToHashSet();
        return preferred with
        {
            SkillTalentIds = selectedTalentIds,
            // Keep the chosen base skill as well as the row-limited active
            // skills. Base-skill-linked masteries are part of the guide synergy
            // and must not disappear merely because the shrine cannot wash them.
            PreferredSkillIds = selectedPreferredSkillIds
        };
    }

    private static string GetBuildGuideText(object? build)
    {
        if (build is null) return string.Empty;
        return Clean(string.Join(" ",
            ReadString(build, "name") ?? string.Empty,
            ReadString(build, "des") ?? string.Empty,
            EnglishName(build, string.Empty) ?? string.Empty,
            EnglishText(build, "_des", string.Empty) ?? string.Empty)).ToLowerInvariant();
    }

    private static bool BuildDirectlyMentionsSkill(string buildText, object skillTalentDefinition)
    {
        if (string.IsNullOrWhiteSpace(buildText)) return false;
        var skillId = ReadNullableInt(skillTalentDefinition, "skillId") ?? 0;
        var skill = skillId > 0 ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
        var names = new[]
        {
            ReadString(skillTalentDefinition, "name"), EnglishName(skillTalentDefinition, string.Empty),
            ReadString(skill, "name"), EnglishName(skill, string.Empty)
        };
        return names.Select(name => Clean(name ?? string.Empty).ToLowerInvariant())
            .Where(name => name.Length >= 2).Distinct()
            .Any(buildText.Contains);
    }

    private static double ScoreTalent(object talent, HeroFocus focus, PreferredTalentPlan preferred)
    {
        var definition = Read(talent, "tTalentData");
        var skill = Read(talent, "skillData");
        var skillRow = Read(skill, "tSkillData") ?? (ReadNullableInt(definition, "skillId") is > 0 and var skillId ? InvokeStatic("TableData", "getTSkillData", skillId) : null);
        var info = Read(skill, "tSkillInfoData") ?? (ReadNullableInt(skillRow, "infoId") is > 0 and var infoId ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null);
        var masteryData = Read(talent, "masteryData");
        var mastery = Read(masteryData, "tMasteryData") ?? (ReadNullableInt(definition, "masteryId") is > 0 and var masteryId ? InvokeStatic("TableData", "getTMasteryData", masteryId) : null);
        var textParts = new List<string> { GetTalentDefinitionText(definition), EnglishName(skillRow, string.Empty) ?? string.Empty, EnglishText(info, "_des", string.Empty) ?? string.Empty, EnglishName(mastery, string.Empty) ?? string.Empty };
        foreach (var affix in ReadList(Read(masteryData, "affixList")))
        {
            var effectType = ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0;
            // Native MasteryData.GetMasteryEffect applies only attribute and
            // ability affixes. Do not let display-only type 4 metadata reorder
            // point allocation or automatic theme inference.
            if (effectType is 1 or 3) textParts.Add(GetAffixSearchText(affix));
        }
        var text = Clean(string.Join(" ", textParts)).ToLowerInvariant();
        var id = ReadNullableInt(definition, "id") ?? 0;
        var skillKey = ReadNullableInt(definition, "skillId") ?? 0;
        var objective = preferred.ObjectiveScores?.GetValueOrDefault(id) ?? 0d;
        if (objective <= 0d && skillKey > 0 && preferred.ObjectiveScores is not null)
            objective = preferred.SkillTalentIds
                .Where(talentId => (ReadNullableInt(InvokeStatic("TableData", "getTTalentData", talentId), "skillId") ?? 0) == skillKey)
                .Select(talentId => preferred.ObjectiveScores.GetValueOrDefault(talentId))
                .DefaultIfEmpty(0d).Max();
        // Objective scores are already normalized logarithmic 60-second skill
        // values or native attribute deltas. Keeping the raw ordering avoids the
        // old 5,000-point clamp that made nearly every damaging skill tie.
        var performance = Math.Max(0d, objective);
        var focusScore = KeywordScore(text, focus.Keywords) * (focus.IsManual ? 260d : 150d);
        var utility = KeywordScore(text, SupportWords) * (focus.Key == "support" ? 180d : 24d)
                      + KeywordScore(text, TankWords) * (focus.Key == "defense" ? 160d : 16d);
        var floor = ReadNullableInt(definition, "floor") ?? 0;
        return performance + focusScore + utility - floor * 0.1d;
    }

    private static string GetTalentDefinitionText(object? definition)
    {
        if (definition is null) return string.Empty;
        var skillId = ReadNullableInt(definition, "skillId") ?? 0;
        var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
        var skill = skillId > 0 ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
        var infoId = ReadNullableInt(skill, "infoId") ?? 0;
        var info = infoId > 0 ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null;
        var mastery = masteryId > 0 ? InvokeStatic("TableData", "getTMasteryData", masteryId) : null;
        return Clean(string.Join(" ",
            ReadString(definition, "name") ?? string.Empty, EnglishName(definition, string.Empty) ?? string.Empty,
            ReadString(skill, "name") ?? string.Empty, EnglishName(skill, string.Empty) ?? string.Empty,
            ReadString(info, "des") ?? string.Empty, EnglishText(info, "_des", string.Empty) ?? string.Empty,
            ReadString(mastery, "name") ?? string.Empty, EnglishName(mastery, string.Empty) ?? string.Empty));
    }

    private static bool IsNormalTalentNode(object talent)
    {
        var definition = Read(talent, "tTalentData");
        return (ReadNullableInt(definition, "id") ?? 0) > 0 && !ReadBool(Read(talent, "isRuneWords"));
    }

    private static Dictionary<int, object> BuildTalentGridById(IEnumerable<object> talents)
    {
        var groups = talents.Where(IsNormalTalentNode)
            .GroupBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(group => group.Key > 0).ToList();
        var duplicates = groups.Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicates.Count > 0)
            Plugin.DiagWarning($"AUTO-SKILLS DUPLICATE TALENTS|ids={string.Join(',', duplicates)}");
        return groups.ToDictionary(
            group => group.Key,
            group => group.OrderBy(talent => IsTalentLockedRequired(talent) ? 1 : 0)
                .ThenByDescending(GetTalentLevel).First());
    }

    private static List<PreferredActiveSkill> ResolvePreferredActiveSkills(PreferredTalentPlan preferred, IReadOnlyDictionary<int, object> gridById)
    {
        var result = new List<PreferredActiveSkill>();
        var resolvedSkillIds = new HashSet<int>();
        foreach (var guideTalentId in preferred.SkillTalentIds)
        {
            var guideDefinition = InvokeStatic("TableData", "getTTalentData", guideTalentId);
            if (!IsTransformableSkillDefinition(guideDefinition)) continue;
            var desiredSkillId = ReadNullableInt(guideDefinition, "skillId") ?? 0;
            if (desiredSkillId <= 0 || !resolvedSkillIds.Add(desiredSkillId)) continue;
            var talent = gridById.Values
                .Where(candidate => !IsTalentLockedRequired(candidate))
                .Where(candidate => (ReadNullableInt(Read(candidate, "tTalentData"), "skillId") ?? 0) == desiredSkillId)
                .OrderBy(candidate => (ReadNullableInt(Read(candidate, "tTalentData"), "id") ?? 0) == guideTalentId ? 0 : 1)
                .ThenBy(candidate => ReadNullableInt(Read(candidate, "tTalentData"), "id") ?? int.MaxValue)
                .FirstOrDefault();
            if (talent is null) continue;
            var talentId = ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0;
            if (talentId > 0) result.Add(new PreferredActiveSkill(guideTalentId, talentId, desiredSkillId, talent));
        }
        return result;
    }

    [Conditional("POI_DEV_FEATURE")]
    private static void LogPreferredActiveSkillState(string phase, IEnumerable<PreferredActiveSkill> activeSkills, IReadOnlyCollection<int> unresolvedSkillIds)
    {
        var states = activeSkills.Select(entry =>
            $"guide={entry.GuideTalentId}/talent={entry.TalentId}/skill={entry.SkillId}/save={GetSavedTalentLevel(entry.Talent)}/base={GetTalentBaseLevelRequired(entry.Talent)}/effective={GetTalentLevel(entry.Talent)}/cap={GetTalentLevelCap(entry.Talent)}");
        Plugin.DiagInfo($"AUTO-SKILLS ACTIVE {phase}|{string.Join(" ; ", states)}|unresolved={string.Join(',', unresolvedSkillIds)}");
    }

    private static bool IsBaseSkillDefinition(object? definition)
        => (ReadNullableInt(definition, "type") ?? 0) == 1 && (ReadNullableInt(definition, "miniType") ?? 0) == 1;

    private static bool IsTransformableSkillDefinition(object? definition)
        => (ReadNullableInt(definition, "type") ?? 0) == 1 && (ReadNullableInt(definition, "miniType") ?? 0) != 1
           && (ReadNullableInt(definition, "skillId") ?? 0) > 0;

    private static bool IsSkillCompatibleWithEquippedWeapons(object hero, int skillId)
    {
        if (skillId <= 0) return false;
        var skill = InvokeStatic("TableData", "getTSkillData", skillId)
                    ?? throw new InvalidOperationException($"Skill definition {skillId} is unavailable.");
        var weaponArr = Read(skill, "weaponArr");
        if (weaponArr is null) return true; // Native treats a null requirement as unrestricted.
        var heroEquipData = Read(hero, "heroEquipData")
                            ?? throw new InvalidOperationException("Hero equipment data is unavailable.");
        // Pass the game's native array object back to the game's own matcher.
        // It checks every equipped part-1 item (both weapon slots) and succeeds
        // when any required weapon type matches.
        return ReadBool(InvokeRequiredInstance(heroEquipData, "CheckHasWeaponTypeArr", weaponArr));
    }

    private static int GetTalentLevel(object talent)
        => Convert.ToInt32(InvokeRequiredInstance(talent, "GetLevel")
                           ?? throw new InvalidOperationException("The native talent level is unavailable."), CultureInfo.InvariantCulture);

    private static int GetSavedTalentLevel(object talent)
        => Math.Max(0, ReadRequiredIntProperty(
            ReadRequiredProperty(talent, "saveTalentData") ?? throw new InvalidOperationException("SaveTalentData is unavailable."),
            "level"));

    private static int GetTalentLevelCap(object talent)
        => Convert.ToInt32(InvokeRequiredInstance(talent, "GetTalentLevelCap")
                           ?? throw new InvalidOperationException("The native talent level cap is unavailable."), CultureInfo.InvariantCulture);

    private static int GetTalentBaseLevelRequired(object talent)
        => Math.Max(0, ReadRequiredIntProperty(talent, "baseLevel"));

    private static bool IsTalentFixedRequired(object talent)
        => ReadRequiredBoolProperty(
            ReadRequiredProperty(talent, "saveTalentData") ?? throw new InvalidOperationException("SaveTalentData is unavailable."),
            "isFixed");

    private static bool IsTalentAlienRequired(object talent)
        => ReadRequiredBoolProperty(
            ReadRequiredProperty(talent, "saveTalentData") ?? throw new InvalidOperationException("SaveTalentData is unavailable."),
            "isAlien");

    private static bool IsTalentUnreplaceable(object talent)
        => IsTalentFixedRequired(talent) || IsTalentAlienRequired(talent);

    private static int GetSpentTalentPoints(object talent)
    {
        var baseLevel = GetTalentBaseLevelRequired(talent);
        return Math.Max(0, GetTalentLevel(talent) - baseLevel);
    }

    private static int GetResettableTalentPointCount(object talentData, IReadOnlyCollection<object> talents)
    {
        _ = talents;
        // ResetTalentPoint calls this same native counter. If its contract is
        // unavailable, a save-data approximation is not a safe mutation gate.
        var native = InvokeRequiredInstance(talentData, "GetAddTalentPointExcludeStick");
        var count = Convert.ToInt32(native ?? throw new InvalidOperationException("Native talent-point count was null."), CultureInfo.InvariantCulture);
        if (count >= 0) return count;
        throw new InvalidOperationException($"Native talent-point count was negative ({count}).");
    }

    private static int PreviewExactTalentPointBudget(object talentData, object saveHero,
        IReadOnlyCollection<object> talents, int spentBefore)
    {
        _ = talents;
        if (spentBefore <= 0)
            return Math.Max(0, ReadNullableInt(saveHero, "talentRemainPoint") ?? 0);

        var blessBefore = ReadNullableInt(saveHero, "blessTalentPoint")
                          ?? throw new InvalidOperationException("The saved blessing talent-point value is unavailable.");
        int total;
        try
        {
            InvokeRequiredInstance(talentData, "RecalcBlessTalentPointByMilestones");
            total = Convert.ToInt32(
                InvokeRequiredInstance(saveHero, "GetTotalTalentPoint")
                ?? throw new InvalidOperationException("The native total talent point value is unavailable."),
                CultureInfo.InvariantCulture);
        }
        finally
        {
            Write(saveHero, "blessTalentPoint", blessBefore);
        }

        if ((ReadNullableInt(saveHero, "blessTalentPoint") ?? int.MinValue) != blessBefore)
            throw new InvalidOperationException("The blessing talent-point preview snapshot could not be restored.");
        if (total < 0)
            throw new InvalidOperationException($"The native total talent point value was negative ({total}).");
        return total;
    }

    private static bool CanAddTalentPoint(object talent)
    {
        if (GetTalentLevel(talent) >= GetTalentLevelCap(talent)) return false;
        return !IsTalentLockedRequired(talent);
    }

    private static bool IsTalentLockedRequired(object talent)
        => ReadBool(InvokeRequiredInstance(talent, "IsLock"));

    private static bool CanAutoAllocateTalent(object talent, object hero, PreferredTalentPlan preferred)
    {
        if (!CanAddTalentPoint(talent)) return false;
        var definition = Read(talent, "tTalentData");
        if ((ReadNullableInt(definition, "type") ?? 0) != 1) return true;

        // Active/base skill nodes are loadout choices, not generic places to
        // dump leftover points. Restrict them to the selected game guide and
        // prove their weapon requirement before spending anything on them.
        var talentId = ReadNullableInt(definition, "id") ?? 0;
        var skillId = ReadNullableInt(definition, "skillId") ?? 0;
        if (!preferred.SkillTalentIds.Contains(talentId)
            && (skillId <= 0 || !preferred.PreferredSkillIds.Contains(skillId))) return false;
        return skillId > 0 && IsSkillCompatibleWithEquippedWeapons(hero, skillId);
    }

    private static bool TrySpendTalentPoints(object talentData, object saveHero, object talent, int requested, out int spent)
    {
        spent = 0;
        var beforeRemain = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
        var beforeLevel = GetTalentLevel(talent);
        var beforeSavedLevel = GetSavedTalentLevel(talent);
        var cap = GetTalentLevelCap(talent);
        var amount = Math.Min(Math.Max(0, requested), Math.Min(beforeRemain, Math.Max(0, cap - beforeLevel)));
        if (amount <= 0 || !CanAddTalentPoint(talent)) return false;
        var result = Convert.ToInt32(InvokeRequiredInstance(talentData, "AddTalentPoint", talent, amount) ?? 1, CultureInfo.InvariantCulture);
        var afterRemain = ReadNullableInt(saveHero, "talentRemainPoint") ?? beforeRemain;
        var afterLevel = GetTalentLevel(talent);
        var afterSavedLevel = GetSavedTalentLevel(talent);
        var remainDelta = beforeRemain - afterRemain;
        var levelDelta = afterLevel - beforeLevel;
        var savedLevelDelta = afterSavedLevel - beforeSavedLevel;
        if (result == 0 && remainDelta == amount && levelDelta == amount && savedLevelDelta == amount)
        {
            spent = amount;
            return true;
        }
        Plugin.DiagWarning($"AUTO-SKILLS ADD FAILED|talent={ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0}|code={result}|requested={amount}|remainDelta={remainDelta}|levelDelta={levelDelta}|savedLevelDelta={savedLevelDelta}");
        return false;
    }

    private static int SpendTalentToCap(object talentData, object saveHero, object talent, HashSet<int> failedNodes)
    {
        var talentId = ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0;
        var remain = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
        var requested = Math.Min(remain, Math.Max(0, GetTalentLevelCap(talent) - GetTalentLevel(talent)));
        if (requested <= 0) return 0;
        if (!TrySpendTalentPoints(talentData, saveHero, talent, requested, out var spent))
        {
            if (talentId > 0) failedNodes.Add(talentId);
            return 0;
        }
        return spent;
    }

    private static bool ApplyPreferredBaseSkill(object talentData, object saveHero, PreferredTalentPlan preferred, out string skillName)
    {
        skillName = string.Empty;
        var desiredTalentId = preferred.SkillTalentIds.FirstOrDefault(id => IsBaseSkillDefinition(InvokeStatic("TableData", "getTTalentData", id)));
        if (desiredTalentId <= 0) return false;
        var definition = InvokeStatic("TableData", "getTTalentData", desiredTalentId);
        var skillId = ReadNullableInt(definition, "skillId") ?? 0;
        var skill = skillId > 0 ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
        skillName = Clean(ReadString(skill, "name") ?? EnglishName(skill, $"Skill {skillId}") ?? $"Skill {skillId}");
        var current = ReadNullableInt(saveHero, "baseSkillId") ?? 0;
        var desiredTalent = ReadValues(Read(talentData, "talentDic"))
            .FirstOrDefault(talent => (ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0) == desiredTalentId);
        if (desiredTalent is null)
        {
            Plugin.DiagWarning($"AUTO-SKILLS BASE SKIPPED|talent={desiredTalentId}|reason=base skill is not present in the hero talent grid");
            return false;
        }
        // ResetTalentPoint can leave baseSkillId selected while its saved row is
        // zero. In that state the ID alone is not proof that the native free
        // level was materialized; ChangeBaseSkill must be invoked again.
        if (current == desiredTalentId && GetSavedTalentLevel(desiredTalent) > 0) return false;
        InvokeRequiredInstance(talentData, "ChangeBaseSkill", desiredTalentId);
        var applied = ReadNullableInt(saveHero, "baseSkillId") ?? 0;
        if (applied != desiredTalentId)
            throw new InvalidOperationException($"Base skill change was not applied (wanted talent {desiredTalentId}, got {applied}).");
        if (GetSavedTalentLevel(desiredTalent) < 1)
            throw new InvalidOperationException(
                $"Base skill {desiredTalentId} was selected but its native free saved level was not materialized.");
        return true;
    }

    private static SkillTransformResult TransformMissingPreferredSkills(object hero, object talentData, object townData, PreferredTalentPlan preferred, int reservedBlood, int maxAttempts)
    {
        var desiredTalentRows = preferred.SkillTalentIds
            .Select(id => InvokeStatic("TableData", "getTTalentData", id))
            .Where(IsTransformableSkillDefinition).ToList();
        var desiredTalentIds = desiredTalentRows
            .Select(definition => ReadNullableInt(definition, "id") ?? 0)
            .Where(id => id > 0).Distinct().ToList();
        var desiredSkillIds = desiredTalentRows
            .Select(definition => ReadNullableInt(definition, "skillId") ?? 0)
            .Where(id => id > 0).Distinct().ToHashSet();
        if (desiredSkillIds.Count == 0 || maxAttempts <= 0)
            return new SkillTransformResult(0, 0, 0, 0, string.Empty, true);

        var current = GetTransformableTalents(talentData).ToList();
        var unlockedSlots = current.Count(talent => !IsTalentLockedRequired(talent));
        var originalFixedTalentIds = current
            .Where(IsTalentFixedRequired)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var fixedUnwanted = current.Count(talent =>
        {
            var definition = Read(talent, "tTalentData");
            var skillId = ReadNullableInt(definition, "skillId") ?? 0;
            return !IsTalentLockedRequired(talent)
                   && IsTalentUnreplaceable(talent) && !desiredSkillIds.Contains(skillId);
        });
        var target = Math.Min(desiredSkillIds.Count, Math.Max(0, unlockedSlots - fixedUnwanted));
        var matched = CountPreferredTransformedSkills(current, desiredSkillIds);
        if (target <= 0 || matched >= target)
            return new SkillTransformResult(0, matched, target, 0, string.Empty, true);

        var shrineType = CreateEnum("EHouseType", 101);
        var shrineHouseData = shrineType is null ? null : InvokeInstance(townData, "GetHouse", shrineType);
        var shrineHouse = Read(shrineHouseData, "houseShrineData");
        var washData = Read(shrineHouse, "shrineWashData");
        var houseData = Read(shrineHouse, "houseData");
        if (washData is null || houseData is null)
            return new SkillTransformResult(0, matched, target, 0, "shrine unavailable", true);
        if (ReadBool(InvokeRequiredInstance(houseData, "IsLock")))
            return new SkillTransformResult(0, matched, target, 0, "shrine is locked", true);

        var bloodType = CreateEnum("EResType", 2) ?? throw new InvalidOperationException("Blood resource type is unavailable.");
        var bloodBefore = Convert.ToInt32(InvokeRequiredInstance(townData, "GetRes", bloodType) ?? throw new InvalidOperationException("Blood amount is unavailable."), CultureInfo.InvariantCulture);
        var previousSelectedHero = Read(houseData, "selectHeroData");
        var likeSnapshot = ReadTalentLikesRequired(townData);
        var attempts = 0;
        var note = string.Empty;
        var cleanupSucceeded = true;
        var executionSucceeded = true;
        try
        {
            Write(houseData, "selectHeroData", hero);
            if (!SameHeroIdentity(Read(houseData, "selectHeroData"), hero))
                throw new InvalidOperationException("The shrine could not select the target hero for skill transformation.");
            for (; attempts < maxAttempts;)
            {
                current = GetTransformableTalents(talentData).ToList();
                foreach (var talent in current)
                {
                    var definition = Read(talent, "tTalentData");
                    var talentId = ReadNullableInt(definition, "id") ?? 0;
                    var skillId = ReadNullableInt(definition, "skillId") ?? 0;
                    // A desired skill in a locked row must not be temporarily
                    // fixed: ReCreateTalent keeps that exact ID in its used set,
                    // which would make it impossible to roll into an unlocked
                    // slot. Preserve only the user's original lock there.
                    var shouldFix = originalFixedTalentIds.Contains(talentId)
                                    || (!IsTalentLockedRequired(talent) && !IsTalentAlienRequired(talent)
                                        && desiredSkillIds.Contains(skillId));
                    var isFixed = IsTalentFixedRequired(talent);
                    if (talentId > 0 && shouldFix != isFixed)
                        InvokeRequiredInstance(talentData, "SetTalentWashFixed", talentId, shouldFix);
                }

                matched = CountPreferredTransformedSkills(current, desiredSkillIds);
                if (matched >= target) break;
                var currentSkillIds = current
                    .Where(talent => !IsTalentLockedRequired(talent))
                    .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
                    .Where(id => id > 0).ToHashSet();
                var missingTalentIds = desiredTalentRows
                    .Where(row => !currentSkillIds.Contains(ReadNullableInt(row, "skillId") ?? 0))
                    .Select(row => ReadNullableInt(row, "id") ?? 0)
                    .Where(id => id > 0).Distinct().ToList();
                // Target exactly one missing skill per roll. Native favorites are
                // weighted rather than guaranteed, but this proves the selected
                // missing row is actually in the shrine's live target list before
                // any Blood is spent and rotates to the next row once it appears.
                var rollTargetTalentIds = missingTalentIds.Take(1).ToList();
                var appliedLikes = ApplyTemporaryTalentLikes(townData, rollTargetTalentIds);
                if (rollTargetTalentIds.Count > 0 && appliedLikes <= 0)
                {
                    executionSucceeded = false;
                    note = AppendTransformNote(note, "recommended-skill preferences could not be applied");
                    break;
                }
                var liveLikeIds = ReadTalentLikesRequired(townData);
                if (!rollTargetTalentIds.All(liveLikeIds.Contains))
                {
                    executionSucceeded = false;
                    note = AppendTransformNote(note, "recommended-skill preferences could not be applied");
                    break;
                }
                if (!ReadBool(InvokeRequiredInstance(washData, "IsCanWashHero")))
                {
                    note = "no transformable unlocked skill row";
                    break;
                }
                var price = Convert.ToInt32(InvokeRequiredInstance(washData, "GetWashPrice") ?? 0, CultureInfo.InvariantCulture);
                var blood = Convert.ToInt32(InvokeRequiredInstance(townData, "GetRes", bloodType) ?? throw new InvalidOperationException("Blood amount is unavailable."), CultureInfo.InvariantCulture);
                if (price < 0 || blood < price + reservedBlood)
                {
                    note = "not enough Blood after reserving the reset cost";
                    break;
                }
                var result = Convert.ToInt32(InvokeRequiredInstance(washData, "WashHero") ?? 1, CultureInfo.InvariantCulture);
                if (result != 0)
                {
                    note = $"shrine transform returned code {result}";
                    break;
                }
                var bloodAfterRoll = Convert.ToInt32(InvokeRequiredInstance(townData, "GetRes", bloodType)
                                                     ?? throw new InvalidOperationException("Blood amount is unavailable after shrine transformation."), CultureInfo.InvariantCulture);
                if (blood - bloodAfterRoll != price)
                {
                    executionSucceeded = false;
                    note = AppendTransformNote(note, $"shrine transform cost verification failed ({blood - bloodAfterRoll}/{price})");
                    break;
                }
                attempts++;
            }
            current = GetTransformableTalents(talentData).ToList();
            matched = CountPreferredTransformedSkills(current, desiredSkillIds);
            if (matched < target && string.IsNullOrWhiteSpace(note) && attempts >= maxAttempts)
                note = "maximum transform attempts reached";
        }
        catch (Exception error)
        {
            executionSucceeded = false;
            note = AppendTransformNote(note, $"transform error: {error.GetBaseException().Message}");
            Plugin.DiagWarning($"AUTO-SKILLS TRANSFORM THREW|attempts={attempts}|matched={matched}/{target}|{error.GetBaseException().Message}");
        }
        finally
        {
            // Temporary locks only guide this automatic run. Restore the user's
            // original fixed-skill choices and the shrine's selected hero.
            foreach (var talent in GetTransformableTalents(talentData))
            {
                try
                {
                    var talentId = ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0;
                    var shouldFix = originalFixedTalentIds.Contains(talentId);
                    var isFixed = IsTalentFixedRequired(talent);
                    if (talentId > 0 && shouldFix != isFixed)
                        InvokeRequiredInstance(talentData, "SetTalentWashFixed", talentId, shouldFix);
                }
                catch (Exception error)
                {
                    cleanupSucceeded = false;
                    note = AppendTransformNote(note, "temporary fixed-skill state could not be fully restored");
                    Plugin.DiagWarning($"AUTO-SKILLS FIXED RESTORE FAILED|{error.GetBaseException().Message}");
                }
            }
            var restoredFixedTalentIds = GetTransformableTalents(talentData)
                .Where(IsTalentFixedRequired)
                .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
                .Where(id => id > 0).ToHashSet();
            if (!restoredFixedTalentIds.SetEquals(originalFixedTalentIds))
            {
                cleanupSucceeded = false;
                note = AppendTransformNote(note, "temporary fixed-skill state could not be fully restored");
                Plugin.DiagWarning($"AUTO-SKILLS FIXED RESTORE MISMATCH|expected={string.Join(',', originalFixedTalentIds)}|actual={string.Join(',', restoredFixedTalentIds)}");
            }
            try { RestoreTalentLikes(townData, likeSnapshot); }
            catch (Exception error)
            {
                cleanupSucceeded = false;
                note = AppendTransformNote(note, "skill preferences could not be fully restored");
                Plugin.DiagWarning($"AUTO-SKILLS LIKES RESTORE FAILED|{error.GetBaseException().Message}");
            }
            try
            {
                Write(houseData, "selectHeroData", previousSelectedHero);
                if (!NativeStateEquals(Read(houseData, "selectHeroData"), previousSelectedHero))
                    throw new InvalidOperationException("the previous shrine hero selection did not return");
            }
            catch (Exception error)
            {
                cleanupSucceeded = false;
                note = AppendTransformNote(note, "shrine hero selection could not be restored");
                Plugin.DiagWarning($"AUTO-SKILLS SHRINE HERO RESTORE FAILED|{error.GetBaseException().Message}");
            }
        }

        var spentBlood = -1;
        try
        {
            var bloodAfter = Convert.ToInt32(InvokeRequiredInstance(townData, "GetRes", bloodType)
                                             ?? throw new InvalidOperationException("Blood amount is unavailable after transformation."), CultureInfo.InvariantCulture);
            spentBlood = Math.Max(0, bloodBefore - bloodAfter);
        }
        catch (Exception error)
        {
            executionSucceeded = false;
            note = AppendTransformNote(note, $"transform Blood verification failed: {error.GetBaseException().Message}");
            Plugin.DiagWarning($"AUTO-SKILLS TRANSFORM BLOOD UNKNOWN|{error.GetBaseException().Message}");
        }
        return new SkillTransformResult(attempts, matched, target, spentBlood, note, cleanupSucceeded, executionSucceeded);
    }

    private static IEnumerable<object> GetTransformableTalents(object talentData)
        => ReadValues(Read(talentData, "talentDic")).Where(talent => IsTransformableSkillDefinition(Read(talent, "tTalentData")));

    private static int CountPreferredTransformedSkills(IEnumerable<object> talents, HashSet<int> desiredSkillIds)
        => talents.Where(talent => !IsTalentLockedRequired(talent))
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
            .Where(desiredSkillIds.Contains).Distinct().Count();

    private static int ApplyTemporaryTalentLikes(object townData, IReadOnlyCollection<int> desiredTalentIds)
    {
        var codex = Read(townData, "townCodexData");
        if (codex is null) return 0;
        var maxLikes = Math.Max(0, ToInt(InvokeRequiredInstance(codex, "GetLikeTalentMax")));
        if (maxLikes <= 0) return 0;
        var desiredOrdered = desiredTalentIds.Take(maxLikes).Distinct().ToList();
        var desired = desiredOrdered.ToHashSet();
        var current = ReadTalentLikesRequired(townData);
        foreach (var talentId in current.Where(id => !desired.Contains(id)))
            SetTalentLikeState(codex, talentId, false);
        foreach (var talentId in desiredOrdered)
            SetTalentLikeState(codex, talentId, true);
        return ReadTalentLikesRequired(townData).Count(desired.Contains);
    }

    private static void RestoreTalentLikes(object townData, IReadOnlyCollection<int> snapshot)
    {
        var codex = Read(townData, "townCodexData");
        if (codex is null)
        {
            if (snapshot.Count == 0) return;
            throw new InvalidOperationException("Town Codex data is unavailable.");
        }
        var current = ReadTalentLikesRequired(townData);
        foreach (var talentId in current)
            if (!SetTalentLikeState(codex, talentId, false))
                throw new InvalidOperationException($"Could not remove temporary talent preference {talentId}.");
        foreach (var talentId in snapshot)
            if (!SetTalentLikeState(codex, talentId, true))
                throw new InvalidOperationException($"Could not restore talent preference {talentId}.");
        var restored = ReadTalentLikesRequired(townData);
        if (!restored.SequenceEqual(snapshot))
            throw new InvalidOperationException($"Talent preference order mismatch (expected {string.Join(',', snapshot)}, got {string.Join(',', restored)}).");
    }

    private static List<int> ReadTalentLikesRequired(object townData)
    {
        var saveTown = ReadRequiredProperty(townData, "saveTownData")
                       ?? throw new InvalidOperationException("SaveTownData is unavailable.");
        var values = ReadRequiredSequenceProperty(saveTown, "likeTalentList")
            .Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToList();
        if (values.Any(id => id <= 0) || values.Distinct().Count() != values.Count)
            throw new InvalidOperationException($"The saved talent preference list is invalid ({string.Join(',', values)}).");
        return values;
    }

    private static string AppendTransformNote(string current, string addition)
        => string.IsNullOrWhiteSpace(current) ? addition : $"{current}; {addition}";

    private static string LocalizeTransformNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return string.Empty;
        return string.Join("; ", note.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part => part switch
        {
            "shrine unavailable" => UiText.L("신전을 찾을 수 없음", "shrine unavailable", "无法找到神殿", "找不到神殿"),
            "shrine is locked" => UiText.L("신전이 아직 잠겨 있음", "shrine is locked", "神殿尚未解锁", "神殿尚未解鎖"),
            "no transformable unlocked skill row" => UiText.L("변환 가능한 해금 스킬 칸이 없음", "no transformable unlocked skill row", "没有可转换的已解锁技能栏", "沒有可轉換的已解鎖技能欄"),
            "not enough Blood after reserving the reset cost" => UiText.L("초기화 비용을 남기면 피가 부족함", "not enough Blood after reserving the reset cost", "预留重置费用后鲜血不足", "保留重設費用後鮮血不足"),
            "recommended-skill preferences could not be applied" => UiText.L("추천 스킬 선호 설정을 적용할 수 없음", "recommended-skill preferences could not be applied", "无法应用推荐技能偏好", "無法套用推薦技能偏好"),
            "recommended-skill preferences unavailable" => UiText.L("추천 스킬 선호 기능을 사용할 수 없어 일반 확률로 진행", "recommended-skill preferences unavailable; using normal odds", "推荐技能偏好不可用，按普通概率继续", "推薦技能偏好不可用，按一般機率繼續"),
            "maximum transform attempts reached" => UiText.L("최대 변환 횟수에 도달함", "maximum transform attempts reached", "已达到最大转换次数", "已達到最大轉換次數"),
            "temporary fixed-skill state could not be fully restored" => UiText.L("임시 고정 상태 일부를 복구하지 못함", "temporary fixed-skill state could not be fully restored", "部分临时锁定状态未能恢复", "部分臨時鎖定狀態未能復原"),
            "skill preferences could not be fully restored" => UiText.L("스킬 선호 목록 일부를 복구하지 못함", "skill preferences could not be fully restored", "部分技能偏好未能恢复", "部分技能偏好未能復原"),
            "shrine hero selection could not be restored" => UiText.L("신전 영웅 선택을 복구하지 못함", "shrine hero selection could not be restored", "未能恢复神殿英雄选择", "未能復原神殿英雄選擇"),
            _ => part
        }));
    }

    private static bool SetTalentLikeState(object codex, int talentId, bool liked)
    {
        var showTalent = InvokeRequiredStaticMany("ShowTalentData", "Create", talentId, true)
                         ?? throw new InvalidOperationException($"ShowTalentData {talentId} could not be created.");
        var current = ReadBool(InvokeRequiredInstance(codex, "IsLikeTalent", showTalent));
        if (current == liked) return true;
        InvokeRequiredInstance(codex, "SetLikeTalent", showTalent);
        return ReadBool(InvokeRequiredInstance(codex, "IsLikeTalent", showTalent)) == liked;
    }

    private static bool SameHeroIdentity(object? left, object? right)
    {
        if (NativeEquals(left, right)) return true;
        var leftId = ReadNullableInt(Read(left, "saveHeroData"), "uniqueId") ?? 0;
        var rightId = ReadNullableInt(Read(right, "saveHeroData"), "uniqueId") ?? 0;
        return leftId > 0 && leftId == rightId;
    }

    private static HeroEffectProfile BuildHeroEffectProfile(object hero, HeroFocus focus)
    {
        var planned = BuildPlannedSkillTarget(hero, focus);
        return BuildProfileForSkillSelection(
            hero,
            focus,
            planned,
            planned.BaseSkillId,
            planned.ActiveSkillIds,
            planned.BaseSkillLevel,
            null,
            null,
            "planned baseline");
    }

    private static string GetJointSkillPlanHeroKey(object hero)
    {
        var uniqueId = ReadNullableInt(Read(hero, "saveHeroData"), "uniqueId") ?? 0;
        return uniqueId > 0 ? $"hero:{uniqueId}" : NativeObjectKey(hero, hero);
    }

    private static string GetCurrentGearFingerprint(object hero)
        => string.Join("|", GetGearSlots().Select(slot =>
        {
            var item = GetEquippedItem(hero, slot.Part, slot.MainWeapon);
            return item is null
                ? $"{slot.Part}:{slot.MainWeapon}:empty"
                : $"{slot.Part}:{slot.MainWeapon}:{NativeObjectKey(item, item)}";
        }));

    private static Dictionary<int, int> GetCurrentGrantedSkillLevels(object hero)
    {
        const string prefix = "extra-skill:";
        return GetGearSlots()
            .Select(slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon))
            .Where(item => item is not null).Cast<object>()
            .DistinctBy(item => NativeObjectKey(item, item))
            .SelectMany(GetGrantedExtraSkillLevels)
            .Select(entry =>
            {
                var skillId = entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                              && int.TryParse(entry.Key[prefix.Length..], NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;
                return (SkillId: skillId, Level: entry.Value);
            })
            .Where(entry => entry.SkillId > 0 && entry.Level > 0)
            .GroupBy(entry => entry.SkillId)
            .ToDictionary(group => group.Key, group => group.Max(entry => entry.Level));
    }

    private static void RememberJointSkillPlan(object hero, HeroEffectProfile profile)
    {
        if (profile.PreviewBaseSkillId <= 0) return;
        JointSkillPlansByHero[GetJointSkillPlanHeroKey(hero)] = new RememberedJointSkillPlan(
            profile.Focus.Key,
            profile.PreviewBaseSkillId,
            // Equipment-granted extra skills belong to the evaluated combat
            // package, but never to the shrine transformation/save plan.
            profile.PlannedSkills.ActiveSkillIds
                .Where(id => id > 0 && id != profile.PreviewBaseSkillId).ToHashSet(),
            GetCurrentGrantedSkillLevels(hero),
            profile.PlannedSkills.MasteryTalentIds
                .Where(id => id > 0 && profile.PlannedSkills.TargetSavedLevels.GetValueOrDefault(id) > 0)
                .ToHashSet(),
            new Dictionary<int, int>(profile.PlannedSkills.TargetSavedLevels),
            profile.PlannedSkills.TotalTalentPointBudget,
            profile.PlannedSkills.PlanToken,
            GetCurrentGearFingerprint(hero),
            profile.JointSkillReason);
    }

    private static PreferredTalentPlan ApplyRememberedJointSkillPlan(
        object hero,
        HeroFocus focus,
        PreferredTalentPlan preferred)
    {
        var cacheKey = GetJointSkillPlanHeroKey(hero);
        if (!JointSkillPlansByHero.TryGetValue(cacheKey, out var remembered)
            || remembered.FocusKey != focus.Key) return preferred;
        if (!string.Equals(remembered.GearFingerprint, GetCurrentGearFingerprint(hero), StringComparison.Ordinal))
        {
            JointSkillPlansByHero.Remove(cacheKey);
            return preferred;
        }
        var jobId = ReadNullableInt(Read(hero, "saveHeroData"), "jobId") ?? 0;
        var rows = ReadValues(ReadStatic("TableData", "TTalentDict"))
            .Where(row => (ReadNullableInt(row, "jobId") ?? 0) == jobId).ToList();
        var baseTalentId = rows.Where(IsBaseSkillDefinition)
            .Where(row => (ReadNullableInt(row, "skillId") ?? 0) == remembered.BaseSkillId)
            .Select(row => ReadNullableInt(row, "id") ?? 0).FirstOrDefault(id => id > 0);
        var activeTalentIds = remembered.ActiveSkillIds.Select(skillId => rows
                .Where(IsTransformableSkillDefinition)
                .Where(row => (ReadNullableInt(row, "skillId") ?? 0) == skillId)
                .Select(row => ReadNullableInt(row, "id") ?? 0).FirstOrDefault(id => id > 0))
            .Where(id => id > 0).Distinct().ToList();
        // Equipment-granted skills legitimately have no shrine row. Keep them in
        // PreferredSkillIds for performance scoring, but allocate/transform only
        // remembered skills that have a native job talent row.
        if (baseTalentId <= 0)
        {
            JointSkillPlansByHero.Remove(GetJointSkillPlanHeroKey(hero));
            return preferred;
        }
        var skillTalentIds = new[] { baseTalentId }.Concat(activeTalentIds).ToList();
        return preferred with
        {
            SkillTalentIds = skillTalentIds,
            MasteryTalentIds = remembered.MasteryTalentIds.OrderBy(id => id).ToList(),
            PreferredSkillIds = remembered.ActiveSkillIds
                .Concat(remembered.GrantedSkillLevels.Keys)
                .Append(remembered.BaseSkillId).ToHashSet(),
            BuildName = $"{preferred.BuildName} · joint gear plan",
            TargetSavedLevels = new Dictionary<int, int>(remembered.TargetSavedLevels),
            PlanToken = remembered.PlanToken
        };
    }

    private static PreferredTalentPlan ApplyCurrentGearJointSkillPlan(
        object hero,
        HeroFocus focus,
        PreferredTalentPlan preferred)
    {
        var baseline = BuildHeroEffectProfile(hero, focus);
        var currentItems = GetGearSlots()
            .Select(slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon))
            .Where(item => item is not null).Cast<object>()
            .DistinctBy(item => NativeObjectKey(item, item))
            .ToList();
        if (currentItems.Count == 0) return preferred;

        var candidates = currentItems
            .Select(item => DescribeItem(
                item,
                UiText.L("현재 착용", "Equipped", "当前装备", "目前裝備"),
                StorageKind.Inventory,
                StorageSource.Equipped))
            .Select(record => CreateGearCandidate(record, hero, baseline))
            .Where(candidate => candidate is not null).Cast<GearCandidate>()
            .ToList();
        if (candidates.Count != currentItems.Count)
            throw new InvalidOperationException("Current equipment could not be converted to a complete joint-evaluation candidate set.");

        var evaluation = EvaluateCompleteLoadout(candidates, hero, baseline, currentItems, true, true);
        if (!evaluation.IsValid)
            throw new InvalidOperationException($"Current-equipment joint preview failed: {evaluation.Failure}");
        if (evaluation.Profile.Focus.Key != focus.Key)
            throw new InvalidOperationException("The selected hero theme changed during current-equipment evaluation.");
        RememberJointSkillPlan(hero, evaluation.Profile);
        return ApplyRememberedJointSkillPlan(hero, focus, preferred);
    }

    private static HeroEffectProfile BuildProfileForSkillSelection(
        object hero,
        HeroFocus focus,
        PlannedSkillTarget planned,
        int baseSkillId,
        IEnumerable<int> activeSkillIds,
        int baseSkillLevel,
        object? previewAttr,
        IEnumerable<GearCandidate>? candidateItems,
        string reason)
    {
        var heroSave = Read(hero, "saveHeroData");
        var jobRow = Read(hero, "tHeroJobData");
        var jobId = ReadNullableInt(heroSave, "jobId") ?? ReadNullableInt(jobRow, "id") ?? 0;
        var allowedWeapons = ReadSequence(Read(jobRow, "baseWeaponTypeArr"))
            .Select(ToInt).Where(value => value > 0).ToHashSet();
        var selectedActive = activeSkillIds.Where(id => id > 0).Distinct().ToHashSet();
        var selectedSkills = selectedActive.Append(baseSkillId).Where(id => id > 0).ToHashSet();
        var skillInfoIds = new HashSet<int>();
        var talentIds = new HashSet<int>();
        var masteryIds = new HashSet<int>();
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseWeaponRequirement = new HashSet<int>();
        var skillWeaponPreferences = new List<HashSet<int>>();
        var activeSkillMainType = 0;
        var activeSkillTags = new HashSet<int>();

        void AddTerm(string? value)
        {
            var cleaned = Clean(value ?? string.Empty).ToLowerInvariant();
            if (cleaned.Length >= 2 && cleaned.Length <= 80) terms.Add(cleaned);
        }

        foreach (var skillId in selectedSkills.OrderBy(id => id == baseSkillId ? 0 : 1).ThenBy(id => id))
        {
            var row = InvokeStatic("TableData", "getTSkillData", skillId);
            if (row is null) continue;
            var infoId = ReadNullableInt(row, "infoId") ?? 0;
            var info = infoId > 0 ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null;
            if (infoId > 0) skillInfoIds.Add(infoId);
            var required = ReadSequence(Read(row, "weaponArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
            if (skillId == baseSkillId)
            {
                baseWeaponRequirement.UnionWith(required);
                try
                {
                    var attr = previewAttr ?? Read(hero, "attrData");
                    var preview = attr is null ? null : InvokeStaticMany("SkillData", "CreatePreview", skillId, Math.Max(1, baseSkillLevel), attr);
                    if (preview is not null && candidateItems is not null && CandidateEnablesSkillVariant(candidateItems, skillId))
                        InvokeRequiredInstance(preview, "SetVariant", true);
                    var previewInfo = Read(preview, "tSkillInfoData") ?? info;
                    activeSkillMainType = ReadNullableInt(preview, "skillMainType") ?? ReadNullableInt(row, "type") ?? 0;
                    activeSkillTags = ReadSequence(Read(previewInfo, "tagArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
                    var subType = ReadNullableInt(preview, "skillSubType") ?? 0;
                    var rangeType = ReadNullableInt(preview, "skillRangeType") ?? 0;
                    if (subType > 0) activeSkillTags.Add(subType);
                    if (rangeType > 0) activeSkillTags.Add(rangeType);
                }
                catch
                {
                    activeSkillMainType = ReadNullableInt(row, "type") ?? 0;
                    activeSkillTags = ReadSequence(Read(info, "tagArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
                }
            }
            else if (required.Count > 0 && !skillWeaponPreferences.Any(group => group.SetEquals(required)))
            {
                skillWeaponPreferences.Add(required);
            }
            AddTerm(ReadString(row, "name"));
            AddTerm(EnglishName(row, string.Empty));
        }

        foreach (var row in ReadValues(ReadStatic("TableData", "TTalentDict")))
        {
            var talentId = ReadNullableInt(row, "id") ?? 0;
            var skillId = ReadNullableInt(row, "skillId") ?? 0;
            if (selectedSkills.Contains(skillId) || planned.MasteryTalentIds.Contains(talentId))
            {
                if (talentId > 0) talentIds.Add(talentId);
                var masteryId = ReadNullableInt(row, "masteryId") ?? 0;
                if (masteryId > 0) masteryIds.Add(masteryId);
                AddTerm(ReadString(row, "name"));
                AddTerm(EnglishName(row, string.Empty));
                if (masteryId > 0)
                {
                    var mastery = InvokeStatic("TableData", "getTMasteryData", masteryId);
                    AddTerm(ReadString(mastery, "name"));
                    AddTerm(EnglishName(mastery, string.Empty));
                }
            }
        }

        return new HeroEffectProfile(
            focus,
            jobId,
            allowedWeapons,
            baseWeaponRequirement,
            skillWeaponPreferences,
            activeSkillMainType,
            activeSkillTags,
            baseSkillId,
            Math.Max(1, baseSkillLevel),
            selectedSkills,
            skillInfoIds,
            talentIds,
            masteryIds,
            new HashSet<int>(talentIds),
            new HashSet<int>(selectedSkills),
            new HashSet<int>(masteryIds),
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<string>(StringComparer.Ordinal),
            terms.ToArray(),
            planned,
            reason,
            0d);
    }

    private static GearCandidate? CreateGearCandidate(ItemSearchRecord record, object hero, HeroEffectProfile profile)
    {
        var equip = Read(record.ItemData, "itemEquipData");
        var definition = Read(equip, "tEquipData");
        var save = Read(record.ItemData, "saveItemData");
        var part = ReadNullableInt(definition, "part") ?? 0;
        if (part is < 1 or > 7) return null;
        var requiredJob = ReadNullableInt(definition, "jobId") ?? 0;
        if (requiredJob > 0 && requiredJob != profile.JobId) return null;
        var minType = ReadNullableInt(definition, "minType") ?? 0;
        var heroJobRow = Read(hero, "tHeroJobData");
        if (part == 1 && minType > 0)
        {
            if (profile.AllowedWeaponTypes.Count > 0 && !profile.AllowedWeaponTypes.Contains(minType)) return null;
        }
        else if (part >= 4 && minType > 0)
        {
            var armorType = ReadNullableInt(heroJobRow, "baseArmorType") ?? 0;
            if (armorType > 0 && armorType != minType) return null;
        }
        var setData = Read(equip, "tEquipSetsData");
        var setJob = ReadNullableInt(setData, "jobId") ?? 0;
        if (setJob > 0 && setJob != profile.JobId) return null;
        var setId = ReadNullableInt(setData, "id") ?? ReadNullableInt(definition, "setsId") ?? 0;
        var definitionId = ReadNullableInt(definition, "id") ?? 0;
        var score = ScoreEquipment(record.ItemData, profile);
        var numericScore = EstimateEquipmentNumericScore(record.ItemData, profile);
        var nonStackingEffectKeys = GetNonStackingEffectKeys(record.ItemData);
        var rawScore = score.Total + numericScore * 0.08d;
        var deduplicatedScore = ScoreSingleItemWithDeduplicatedEffects(record.ItemData, profile, rawScore);
        return new GearCandidate(record, NativeObjectKey(record.ItemData, record.SourceField ?? record.ItemData), part, setId, definitionId, minType, deduplicatedScore, numericScore, score.DirectMatches, score.ThemeMatches, nonStackingEffectKeys);
    }

    private static EquipmentScore ScoreEquipment(object item, HeroEffectProfile profile)
    {
        var save = Read(item, "saveItemData");
        var equip = Read(item, "itemEquipData");
        var definition = Read(equip, "tEquipData");
        var quality = ReadNullableInt(save, "quality") ?? 0;
        var level = ReadNullableInt(save, "level") ?? 0;
        var forge = ReadNullableInt(save, "forgeLevel") ?? 0;
        var main = ReadNullableInt(save, "mainAttrValue") ?? 0;
        // Quality is only a modest prior. The native AttrData simulation and
        // skill-specific effects below should decide the winner, not rarity by
        // itself.
        var qualityWeight = quality switch { 8 => 125d, 6 => 120d, 5 => 115d, 4 => 92d, 3 => 66d, 2 => 38d, _ => quality * 12d };
        var descriptions = new List<string>();
        var directMatches = 0;
        var behaviorScore = 0d;
        var definitionId = ReadNullableInt(definition, "id") ?? 0;
        var runewordAffix = GetRunewordAffix(item) is { } rawRuneword ? ResolveRuntimeAffix(rawRuneword) : null;
        foreach (var affix in CollectEquipmentAffixes(item).Concat(CollectGrantedMasteryAffixes(item)))
        {
            var runtimeAffix = ResolveRuntimeAffix(affix);
            if ((ReadNullableInt(Read(runtimeAffix, "tAffixData"), "effectType") ?? 0) == 100
                && !NativeEquals(runtimeAffix, runewordAffix)) continue;
            var affixText = GetAffixSearchText(runtimeAffix);
            descriptions.Add(affixText);
            directMatches += CountDirectAffixMatches(runtimeAffix, profile);
            behaviorScore += ScoreAffixBehavior(runtimeAffix, definitionId, profile, affixText);
        }
        descriptions.Add(Clean(string.Join(" ", ReadString(definition, "des") ?? string.Empty, EnglishText(definition, "_des", string.Empty) ?? string.Empty)));
        var text = string.Join(" ", descriptions).ToLowerInvariant();
        var themeMatches = KeywordScore(text, profile.Focus.Keywords);
        // Text is a small discovery hint only. Skill/variant activation below is
        // decided exclusively from native IDs and native ability metadata.
        var focusBonus = themeMatches * (profile.Focus.IsManual ? 14d : 8d);
        var generalBonus = KeywordScore(text, new[] { "all attack", "all defense", "primary attribute", "crit", "speed", "cost", "resist", "health" }) * 12d;
        // Guide equipment is retained in the shortlist for comparison, but it
        // receives no score bonus. Native stats/effects decide the winner.
        const double guideBonus = 0d;
        // Direct ID matches are retained for reporting and shortlist diversity.
        // Variant/extra-skill effects are already applied by SetVariant and the
        // native granted level, so a flat match bonus would count them twice.
        var total = qualityWeight * 0.2d + level * 0.2d + forge * 0.5d + main * 0.002d
                    + focusBonus + generalBonus + behaviorScore + guideBonus;
        return new EquipmentScore(total, directMatches, themeMatches);
    }

    private static double ScoreAffixBehavior(object affix, int equipmentId, HeroEffectProfile profile, string affixText)
    {
        var definition = Read(affix, "tAffixData");
        var effectType = ReadNullableInt(definition, "effectType") ?? 0;
        if (effectType == 1) return 0d; // bodyAttr magnitude is scored numerically.

        var jobId = ReadNullableInt(Read(affix, "tHeroJobData"), "id") ?? 0;
        if (jobId > 0 && jobId != profile.JobId) return -2500d;

        if (effectType == 4)
        {
            // SkillData.SetVariant changes the real native Power/Action preview.
            // The candidate is preserved separately by target skill ID.
            return 0d;
        }

        if (effectType == 100)
        {
            // Native GetSkillList keeps the highest granted level and mastery
            // affixes are previewed directly. No additional flat score belongs
            // here; the source is preserved separately for joint evaluation.
            return 0d;
        }

        if (effectType == 3)
        {
            var ability = Read(affix, "tAbilityData");
            if (ability is null)
            {
                var abilityId = ReadSequence(Read(definition, "effectParam")).Select(ToInt).FirstOrDefault();
                ability = ResolveAbilityTableRow(abilityId, 1);
            }
            var abilityScore = ScoreNativeAbility(ability, profile);
            if (!HasExplicitNativeAbilityTheme(ability, profile.PlannedSkills))
            {
                var fallbackText = string.IsNullOrWhiteSpace(affixText) ? GetAffixSearchText(affix) : affixText;
                abilityScore *= GetOpaqueEffectThemeWeight(fallbackText, profile.Focus);
            }
            return abilityScore;
        }

        return 0d;
    }

    private static object? ResolveAbilityTableRow(int abilityId, int level)
    {
        if (abilityId <= 0) return null;
        var resolved = InvokeStaticMany("AbilityData", "ResolveAbilityTableId", abilityId, Math.Max(0, level));
        var resolvedId = resolved is null ? abilityId : ToInt(resolved);
        return InvokeStatic("TableData", "getTAbilityData", resolvedId)
               ?? InvokeStatic("TableData", "getTAbilityData", abilityId);
    }

    private static bool HasNativeSpecialAbilityHook(int abilityId)
    {
        if (abilityId <= 0) return false;
        try
        {
            gameAssembly ??= AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
            var abilitySys = gameAssembly?.GetType("AbilitySys", false, false);
            // Failure to find the native hook registry is unknown, never proof
            // that a table ability has no special battle implementation.
            if (abilitySys is null) return true;
            var prefix = $"CheckAbility_{abilityId}";
            return abilitySys.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Any(method => method.Name.Equals(prefix, StringComparison.Ordinal)
                               || method.Name.StartsWith(prefix + "_", StringComparison.Ordinal));
        }
        catch
        {
            // Failure to enumerate hooks must never turn an unknown dynamic
            // effect into an invented performance bonus.
            return true;
        }
    }

    private static bool IsStrictAlwaysOnSelfAbility(object ability)
    {
        var row = Read(ability, "tAbilityData") ?? ability;
        if (row is null) return false;
        var type = ReadNullableInt(row, "type") ?? 0;
        var effectiveType = ReadNullableInt(row, "effectiveType") ?? 0;
        var resultIds = ReadSequence(Read(row, "resultArr")).Select(ToInt).Where(id => id > 0).ToList();
        var filterCount = ReadSequence(Read(row, "filtArr")).Select(ToInt).Count(id => id > 0);
        var duration = Math.Max(0d, Convert.ToDouble(Read(row, "duration") ?? 0d, CultureInfo.InvariantCulture));
        var cooldown = Math.Max(0d, Convert.ToDouble(Read(row, "cdTime") ?? 0d, CultureInfo.InvariantCulture));
        var stack = Math.Max(0, ReadNullableInt(row, "stack") ?? 0);
        return type is 1 or 4 or 7
               && effectiveType == 3
               && IsUnconditionalAcquireAbility(ability)
               && resultIds.Count > 0 && resultIds[0] == 301
               && filterCount == 0
               && duration <= 0d && cooldown <= 0d && stack <= 1;
    }

    private static bool IsUnconditionalAcquireAbility(object ability)
    {
        var row = Read(ability, "tAbilityData") ?? ability;
        if (row is null) return false;
        var abilityId = ReadNullableInt(row, "id") ?? ReadNullableInt(ability, "id") ?? 0;
        var type = ReadNullableInt(row, "type") ?? 0;
        var effectiveType = ReadNullableInt(row, "effectiveType") ?? 0;
        var moments = ReadSequence(Read(row, "moment")).Select(ToInt).Where(id => id > 0).ToHashSet();
        var resultIds = ReadSequence(Read(row, "resultArr")).Select(ToInt).Where(id => id > 0).ToList();
        var filterCount = ReadSequence(Read(row, "filtArr")).Select(ToInt).Count(id => id > 0);
        var duration = Math.Max(0d, Convert.ToDouble(Read(row, "duration") ?? 0d, CultureInfo.InvariantCulture));
        var tick = Math.Max(0d, Convert.ToDouble(Read(row, "tick") ?? 0d, CultureInfo.InvariantCulture));
        var cooldown = Math.Max(0d, Convert.ToDouble(Read(row, "cdTime") ?? 0d, CultureInfo.InvariantCulture));
        var stack = Math.Max(0, ReadNullableInt(row, "stack") ?? 0);
        return abilityId > 0
               && type is 1 or 4 or 7
               && effectiveType == 3
               && moments.SetEquals(new[] { 1 })
               && Clean(ReadString(row, "condition") ?? string.Empty).Length == 0
               && !ReadSequence(Read(row, "monitorArr")).Select(ToInt).Any(id => id > 0)
               && filterCount == 0
               && duration <= 0d && tick <= 0d && cooldown <= 0d && stack <= 1
               && resultIds.Count > 0 && resultIds[0] == 301
               && !HasNativeSpecialAbilityHook(abilityId);
    }

    private static Dictionary<int, double> ReadNativeAbilityResultAttrDeltas(object ability)
    {
        var result = new Dictionary<int, double>();
        foreach (var entry in ReadEntries(Read(Read(ability, "resultData"), "combatAttrFinalDic")))
        {
            var attrId = ToInt(Read(entry, "Key") ?? 0);
            double value;
            try { value = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture); }
            catch { continue; }
            if (attrId <= 0 || !double.IsFinite(value) || Math.Abs(value) <= 0d) continue;
            result[attrId] = result.GetValueOrDefault(attrId) + value;
        }
        return result;
    }

    private static object CreateAbilityAdjustedAttr(object attrData, NativeAbilityPackage package)
    {
        var adjusted = InvokeRequiredStaticMany("AttrData", "copyCreate", attrData)
                       ?? throw new InvalidOperationException("AttrData ability-adjusted copy failed.");
        foreach (var entry in package.AlwaysOnAttrDeltas.OrderBy(entry => entry.Key))
        {
            var attrType = CreateEnum("EAttrType", entry.Key)
                           ?? throw new InvalidOperationException($"Unknown ability result attribute {entry.Key}.");
            InvokeRequiredInstance(adjusted, "ChangeAttr", attrType, Convert.ToSingle(entry.Value, CultureInfo.InvariantCulture));
        }
        return adjusted;
    }

    private static (double Support, double Defense, double Minion) GetNativeAbilityRoleUtility(object? ability)
    {
        var row = Read(ability, "tAbilityData") ?? ability;
        if (row is null) return (0d, 0d, 0d);
        var type = ReadNullableInt(row, "type") ?? 0;
        var attrId = ReadNullableInt(row, "attrId") ?? 0;
        var moments = ReadSequence(Read(row, "moment")).Select(ToInt).Where(id => id > 0).ToHashSet();
        // Conditional and AbilitySys-special effects need live combat events.
        // Treat them as an unknown zero lower bound instead of fabricating uptime.
        if (!IsUnconditionalAcquireAbility(row))
            return (0d, 0d, 0d);
        var duration = Math.Max(0d, Convert.ToDouble(
            Read(ability, "duration") ?? Read(row, "duration") ?? 0d, CultureInfo.InvariantCulture));
        var cooldown = Math.Max(0d, Convert.ToDouble(
            Read(ability, "cdTime") ?? Read(row, "cdTime") ?? 0d, CultureInfo.InvariantCulture));
        var uptime = duration > 0d && cooldown > 0d ? Math.Clamp(duration / cooldown, 0.1d, 1d) : 1d;
        var support = type switch { 1 => 650d, 4 => 420d, 6 => 1100d, _ => 0d };
        var defense = type switch { 2 or 3 or 5 => 450d, 7 => 2600d, _ => 0d };
        var minion = 0d;
        if (attrId is 81 or 82 or 83 or 84 or 91 or 92 or 93 or 94 or 185 or 191) support += 950d;
        if (attrId is 3 or 4 or 5 or 7 or 9 or 26 or 27 or 32 or 34 or 36 or 43 or 44 or 45 or 47
            or 61 or 62 or 63 or 64 or 65 or 66 or 85 or 86 or 130 or 131 or 132 or 134
            or 151 or 152 or 153 or 154 or 155 or 156 or 157 or 158 or 184 or 188 or 202 or 212 or 213 or 214 or 219)
            defense += 1100d;
        if (attrId is 25 or 190 or 6001 or 6002) minion += 1300d;
        if (moments.Overlaps(new[] { 8, 11, 13, 15, 100, 102, 104 })) defense += 700d;
        if (moments.Contains(1))
        {
            support += 280d;
            defense += 280d;
        }
        var targetBreadth = ReadSequence(Read(row, "filtArr")).Select(ToInt).Count(value => value > 0);
        if (targetBreadth > 0 && type is 1 or 4 or 6) support += Math.Min(500d, targetBreadth * 125d);

        // AbilityData.CreateByShow computes native-adjusted values for static
        // result code 301 in combatAttrFinalDic. Read that exact preview when it
        // exists, so equal ability types with very different magnitudes do not
        // collapse to the same role score. Dynamic result codes remain structural
        // signals only; executing them would mutate combat state.
        foreach (var entry in ReadEntries(Read(Read(ability, "resultData"), "combatAttrFinalDic")))
        {
            var resultAttrId = ToInt(Read(entry, "Key") ?? 0);
            double signedValue;
            try { signedValue = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture); }
            catch { continue; }
            if (resultAttrId <= 0 || !double.IsFinite(signedValue) || Math.Abs(signedValue) <= 0d) continue;
            var magnitude = Math.Log10(1d + Math.Abs(signedValue)) * 420d;
            var hostileDebuff = type is 2 or 3 && signedValue < 0d;
            if (!hostileDebuff && signedValue > 0d
                && resultAttrId is 81 or 82 or 83 or 84 or 91 or 92 or 93 or 94 or 185 or 191)
                support += magnitude;
            if (!hostileDebuff && signedValue > 0d
                && resultAttrId is 3 or 4 or 5 or 7 or 9 or 26 or 27 or 32 or 34 or 36 or 43 or 44 or 45 or 47
                or 61 or 62 or 63 or 64 or 65 or 66 or 85 or 86 or 130 or 131 or 132 or 134
                or 151 or 152 or 153 or 154 or 155 or 156 or 157 or 158 or 184 or 188 or 202 or 212 or 213 or 214 or 219)
                defense += magnitude;
            if (!hostileDebuff && signedValue > 0d && resultAttrId is 25 or 190 or 6001 or 6002)
                minion += magnitude;
            if (hostileDebuff) support += magnitude * 0.8d;
        }
        return (support * uptime, defense * uptime, minion * uptime);
    }

    private static double ScoreNativeAbility(object? ability, HeroEffectProfile profile)
    {
        var row = Read(ability, "tAbilityData") ?? ability;
        if (row is null) return 0d;
        var type = ReadNullableInt(row, "type") ?? 0;
        var attrId = ReadNullableInt(row, "attrId") ?? 0;
        var moments = ReadSequence(Read(row, "moment")).Select(ToInt).Where(id => id > 0).ToHashSet();
        if (!IsUnconditionalAcquireAbility(row))
            return 0d;
        var duration = Math.Max(0d, Convert.ToDouble(Read(row, "duration") ?? 0d, CultureInfo.InvariantCulture));
        var cooldown = Math.Max(0d, Convert.ToDouble(Read(row, "cdTime") ?? 0d, CultureInfo.InvariantCulture));
        var effectiveType = ReadNullableInt(row, "effectiveType") ?? 0;
        var resultCount = ReadSequence(Read(row, "resultArr")).Select(ToInt).Count(id => id > 0);
        var defense = profile.Focus.Key == "defense";
        var support = profile.Focus.Key == "support";
        var score = type switch
        {
            7 => defense ? 2400d : 480d, // native immunity ability
            1 or 4 or 6 => defense || support ? 520d : 360d,
            2 or 3 or 5 => defense ? 600d : 260d,
            _ => 120d
        };
        if (attrId > 0)
        {
            var physical = profile.Focus.Key is "physical" or "bleed";
            var elemental = profile.Focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion";
            score += Math.Min(900d, Math.Max(0d,
                GetBattleAttrPreScoreWeight(attrId, profile, physical, elemental) * 28d));
            score *= GetNativeAbilityElementWeight(attrId, profile.Focus);
        }
        if (moments.Overlaps(new[] { 8, 11, 13, 15, 100, 102, 104 }))
            score += defense ? 650d : 180d;
        if (moments.Overlaps(new[] { 3, 6, 7, 9, 14 }))
            score += defense ? 120d : 360d;
        if (moments.Contains(1) || effectiveType == 3) score += defense || support ? 420d : 240d;
        score += Math.Min(300d, resultCount * 75d);
        if (duration > 0d && cooldown > 0d)
            score *= Math.Clamp(duration / cooldown, 0.15d, 1d);
        return Math.Clamp(score, 0d, defense ? 4200d : 2200d);
    }

    private static NativeAbilityPackage EvaluateNativeAbilityPackage(
        IEnumerable<GearCandidate> items,
        object hero,
        HeroEffectProfile profile,
        object attrData)
    {
        var structural = 0d;
        var offensive = 0d;
        var support = 0d;
        var defense = 0d;
        var minion = 0d;
        var alwaysOnAttrDeltas = new Dictionary<int, double>();
        var failedAbilityIds = new HashSet<int>();
        var unmodeledAbilityIds = new HashSet<int>();
        var appliedAbilityCounts = new Dictionary<int, int>();
        var complete = true;

        void AddAbility(int abilityId, int level, string themeText)
        {
            if (abilityId <= 0)
            {
                complete = false;
                failedAbilityIds.Add(abilityId);
                return;
            }
            try
            {
                var preview = InvokeStaticMany(
                    "AbilityData", "CreateByShow", abilityId, Math.Max(0, level), attrData);
                var ability = preview ?? ResolveAbilityTableRow(abilityId, level);
                if (ability is null)
                    throw new InvalidOperationException($"AbilityData.CreateByShow({abilityId}) returned no ability.");
                var row = Read(ability, "tAbilityData") ?? ability;
                var resolvedAbilityId = ReadNullableInt(row, "id") ?? ReadNullableInt(ability, "id") ?? abilityId;
                var stackLimit = Math.Max(1, ReadNullableInt(row, "stack") ?? 1);
                var appliedCount = appliedAbilityCounts.GetValueOrDefault(resolvedAbilityId);
                if (appliedCount >= stackLimit) return;
                appliedAbilityCounts[resolvedAbilityId] = appliedCount + 1;

                var resultIds = ReadSequence(Read(row, "resultArr")).Select(ToInt).Where(id => id > 0).ToList();
                if (preview is null && resultIds.Contains(301))
                    throw new InvalidOperationException($"Static result301 preview for ability {resolvedAbilityId} is unavailable.");
                var safeAcquire = IsUnconditionalAcquireAbility(ability);
                if (!safeAcquire || preview is null) unmodeledAbilityIds.Add(resolvedAbilityId);

                if (preview is not null && IsStrictAlwaysOnSelfAbility(preview))
                {
                    // Apply the complete signed delta later to a cloned AttrData.
                    // Positive-only application would hide native trade-offs.
                    var deltas = ReadNativeAbilityResultAttrDeltas(preview);
                    if (deltas.Count == 0)
                        throw new InvalidOperationException($"Static result301 ability {resolvedAbilityId} produced no readable attributes.");
                    foreach (var entry in deltas)
                        alwaysOnAttrDeltas[entry.Key] = alwaysOnAttrDeltas.GetValueOrDefault(entry.Key) + entry.Value;
                    return;
                }

                var abilityScore = ScoreNativeAbility(ability, profile);
                if (!HasExplicitNativeAbilityTheme(ability, profile.PlannedSkills))
                    abilityScore *= GetOpaqueEffectThemeWeight(themeText, profile.Focus);
                structural += abilityScore;
                var utility = GetNativeAbilityRoleUtility(ability);
                support += utility.Support;
                defense += utility.Defense;
                minion += utility.Minion;
                if (safeAcquire) offensive += ScoreNativeAbilityResultOffense(ability, profile);
            }
            catch (Exception error)
            {
                complete = false;
                failedAbilityIds.Add(abilityId);
                Plugin.DiagDebug($"ABILITY PREVIEW FAILED|ability={abilityId}|level={level}|{error.GetBaseException().Message}");
            }
        }

        void AddAffix(object rawAffix)
        {
            var affix = ResolveRuntimeAffix(rawAffix);
            var definition = Read(affix, "tAffixData");
            if ((ReadNullableInt(definition, "effectType") ?? 0) != 3) return;
            var save = Read(affix, "saveData");
            var abilityId = ReadNullableInt(save, "abilityId")
                            ?? ReadNullableInt(Read(affix, "tAbilityData"), "id")
                            ?? ReadSequence(Read(definition, "effectParam")).Select(ToInt).FirstOrDefault();
            var level = Math.Max(0, ReadNullableInt(save, "level") ?? 0);
            AddAbility(abilityId, level, GetAffixSearchText(affix));
        }

        var candidates = items.ToList();
        foreach (var candidate in candidates)
        foreach (var affix in CollectEquipmentAffixes(candidate.Record.ItemData)
                     .Concat(CollectGrantedMasteryAffixes(candidate.Record.ItemData)))
            AddAffix(affix);

        var effectsBySet = GetSetEffectScoreRows();
        foreach (var group in candidates.Where(item => item.SetId > 0).GroupBy(item => item.SetId))
        {
            if (!effectsBySet.TryGetValue(group.Key, out var effects)) continue;
            var themeText = GetSetThemeText(group.Key, effects);
            foreach (var effect in effects.Where(effect => effect.Pieces <= group.Count()))
            {
                // Native HeroEquipData.UpdateSetsEffect removes and adds every
                // active TEquipSetsEffect with HeroData.RemoveAbility/AddAbility
                // (abilityId, 0). Its abilityParam array is payload/description
                // data and is not used as the ability level (GameAssembly RVA
                // 0x6FE3E0; calls at 0x6FE5F0 and 0x6FE8CB).
                AddAbility(effect.AbilityId, 0, themeText);
            }
        }

        var talentData = Read(hero, "heroTalentData");
        var gridTalents = talentData is null
            ? new List<object>()
            : ReadValues(Read(talentData, "talentDic")).ToList();
        foreach (var talentId in profile.PlannedSkills.MasteryTalentIds)
        {
            var talent = gridTalents.FirstOrDefault(value =>
                (ReadNullableInt(Read(value, "tTalentData"), "id") ?? 0) == talentId);
            var definition = Read(talent, "tTalentData")
                             ?? InvokeStatic("TableData", "getTTalentData", talentId);
            var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
            if (definition is null || masteryId <= 0)
                throw new InvalidOperationException($"Planned mastery talent {talentId} is unavailable.");
            var cap = talent is null ? 1 : Math.Max(1, GetTalentLevelCap(talent));
            var baseLevel = talent is null ? 0 : Math.Min(GetTalentBaseLevelRequired(talent), cap);
            var targetLevel = Math.Min(cap, baseLevel + 1);
            if (targetLevel <= baseLevel)
                throw new InvalidOperationException($"Planned mastery talent {talentId} has no investable level.");
            var mastery = InvokeStaticMany("MasteryData", "CreateByShow", masteryId, targetLevel, cap)
                          ?? throw new InvalidOperationException($"MasteryData.CreateByShow({masteryId}) returned no mastery.");
            foreach (var affix in ReadList(Read(mastery, "affixList"))) AddAffix(affix);
        }
        return new NativeAbilityPackage(
            structural, offensive, support, defense, minion,
            alwaysOnAttrDeltas, new Dictionary<int, int>(appliedAbilityCounts),
            complete, failedAbilityIds, unmodeledAbilityIds);
    }

    private static double ScoreNativeAbilityResultOffense(object ability, HeroEffectProfile profile)
        => ScoreNativeAbilityResultOffenseCore(
            ability,
            attrId => Math.Max(0.08d, GetBattleAttrPreScoreWeight(
                attrId,
                profile,
                profile.Focus.Key is "physical" or "bleed",
                profile.Focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion")));

    private static double ScoreNativeAbilityResultOffense(object ability, HeroFocus focus)
        => ScoreNativeAbilityResultOffenseCore(ability, attrId => GetNativeAbilityResultWeight(attrId, focus));

    private static double ScoreNativeAbilityResultOffenseCore(object ability, Func<int, double> getWeight)
    {
        var row = Read(ability, "tAbilityData") ?? ability;
        var abilityType = ReadNullableInt(row, "type") ?? 0;
        var score = 0d;
        foreach (var entry in ReadEntries(Read(Read(ability, "resultData"), "combatAttrFinalDic")))
        {
            var attrId = ToInt(Read(entry, "Key") ?? 0);
            double value;
            try { value = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture); }
            catch { continue; }
            if (attrId <= 0 || !double.IsFinite(value) || Math.Abs(value) <= 0d) continue;
            var positiveOffense = value > 0d && attrId is 1 or 2 or 31 or 33 or 35 or 37 or 41 or 42
                or 51 or 52 or 53 or 54 or 55 or 56 or 71 or 72 or 75 or 76 or 77 or 78
                or 99 or 100 or 101 or 102 or 106 or 107 or 108 or 110 or 111 or 112 or 113 or 114 or 115
                or 121 or 122 or 123 or 124 or 125 or 126 or 133 or 160 or 162 or 164 or 167
                or 170 or 171 or 172 or 181 or 210 or 211 or 218
                or 230 or 231 or 232 or 233 or 234 or 235 or 236 or 237
                or 240 or 241 or 242 or 245 or 246 or 247 or 248 or 250 or 251 or 252 or 253
                or 255 or 256 or 257 or 258 or 700 or 703 or 704 or 705 or 707 or 708 or 709 or 710 or 711 or 712;
            var hostileDefenseReduction = value < 0d && abilityType is 2 or 3
                && attrId is 3 or 4 or 43 or 44 or 61 or 62 or 63 or 64 or 65 or 66 or 130 or 131 or 132 or 134;
            if (!positiveOffense && !hostileDefenseReduction) continue;
            score += Math.Abs(value) * Math.Max(0.08d, getWeight(attrId));
        }
        var duration = Math.Max(0d, Convert.ToDouble(
            Read(ability, "duration") ?? Read(row, "duration") ?? 0d, CultureInfo.InvariantCulture));
        var cooldown = Math.Max(0d, Convert.ToDouble(
            Read(ability, "cdTime") ?? Read(row, "cdTime") ?? 0d, CultureInfo.InvariantCulture));
        if (duration > 0d && cooldown > 0d) score *= Math.Clamp(duration / cooldown, 0.1d, 1d);
        return score;
    }

    private static double GetNativeAbilityResultWeight(int attrId, HeroFocus focus)
        => attrId switch
        {
            1 => focus.Key is "physical" or "bleed" ? 1.4d
                : focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion" ? 0.25d : 1d,
            2 => focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion" ? 1.4d
                : focus.Key is "physical" or "bleed" ? 0.25d : 1d,
            51 or 52 or 53 or 110 or 111 or 112 => GetDamageFamilyResultWeight(focus, "physical"),
            54 or 113 => GetDamageFamilyResultWeight(focus, "fire"),
            55 or 114 => GetDamageFamilyResultWeight(focus, "ice"),
            56 or 115 => GetDamageFamilyResultWeight(focus, "lightning"),
            121 or 123 or 125 => GetDamageFamilyResultWeight(focus, "bleed"),
            122 or 124 or 126 => GetDamageFamilyResultWeight(focus, "corrosion"),
            31 or 37 => focus.Key == "crit" ? 22d : focus.Key is "defense" or "support" or "minion" ? 2d : 12d,
            41 or 42 or 71 or 72 or 75 or 76 or 77 or 78 or 99 or 100 or 101 or 102
                or 106 or 107 or 108 or 133 or 160 or 162 or 164 or 167 or 170 or 171 or 172
                or 181 or 210 or 211 or 218 or 230 or 231 or 232 or 233 or 234 or 235 or 236
                or 237 or 240 or 241 or 242 or 245 or 246 or 247 or 248 or 250 or 251 or 252
                or 253 or 255 or 256 or 257 or 258 or 700 or 703 or 704 or 705 or 707 or 708
                or 709 or 710 or 711 or 712 => focus.Key is "defense" or "support" ? 3d : 16d,
            _ => 0.08d
        };

    private static double GetDamageFamilyResultWeight(HeroFocus focus, string family)
    {
        if (focus.Key is "defense" or "support" or "minion") return 2d;
        if (focus.Key is "crit" or "hybrid") return 16d;
        if (focus.Key == "elemental") return family is "fire" or "ice" or "lightning" ? 16d : 16d * 0.18d;
        if (focus.Key is "physical" or "fire" or "ice" or "lightning" or "bleed" or "corrosion")
            return focus.Key == family ? 16d : 16d * 0.18d;
        return 16d;
    }

    private static double ScoreItemAbilityHeuristics(
        IEnumerable<GearCandidate> items,
        HeroEffectProfile profile)
    {
        var score = 0d;
        foreach (var candidate in items)
        foreach (var rawAffix in CollectEquipmentAffixes(candidate.Record.ItemData)
                     .Concat(CollectGrantedMasteryAffixes(candidate.Record.ItemData)))
        {
            var affix = ResolveRuntimeAffix(rawAffix);
            if ((ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0) != 3) continue;
            score += ScoreAffixBehavior(
                affix, candidate.DefinitionId, profile, GetAffixSearchText(affix));
        }
        return score;
    }

    private static double GetNativeAbilityElementWeight(int attrId, HeroFocus focus)
    {
        var family = attrId switch
        {
            51 or 52 or 53 or 110 or 111 or 112 => "physical",
            54 or 113 => "fire",
            55 or 114 => "ice",
            56 or 115 => "lightning",
            121 or 123 or 125 => "bleed",
            122 or 124 or 126 => "corrosion",
            _ => string.Empty
        };
        if (family.Length == 0 || focus.Key is "defense" or "support" or "minion" or "crit" or "hybrid") return 1d;
        if (focus.Key == "elemental") return family is "fire" or "ice" or "lightning" ? 1d : 0.18d;
        return family == focus.Key ? 1d : 0.18d;
    }

    private static bool HasExplicitNativeAbilityTheme(object? ability, PlannedSkillTarget _)
    {
        var row = Read(ability, "tAbilityData") ?? ability;
        if (row is null) return false;
        var attrId = ReadNullableInt(row, "attrId") ?? 0;
        if (attrId is 51 or 52 or 53 or 54 or 55 or 56
            or 110 or 111 or 112 or 113 or 114 or 115 or 121 or 122 or 123 or 124 or 125 or 126)
            return true;
        return ReadEntries(Read(Read(ability, "resultData"), "combatAttrFinalDic"))
            .Select(entry => ToInt(Read(entry, "Key") ?? 0))
            .Any(id => id is 51 or 52 or 53 or 54 or 55 or 56
                or 110 or 111 or 112 or 113 or 114 or 115
                or 121 or 122 or 123 or 124 or 125 or 126);
    }

    private static double GetOpaqueEffectThemeWeight(string? rawText, HeroFocus focus)
    {
        var text = Clean(rawText ?? string.Empty).ToLowerInvariant();
        if (text.Length == 0 || focus.Key is "defense" or "support" or "minion" or "crit" or "hybrid") return 1d;

        // Use only explicit damage-family words here. Broad discovery aliases
        // such as "weapon", "spell" or "crit" occur in otherwise neutral set
        // descriptions and must not turn them into Physical/Elemental sets.
        var families = new Dictionary<string, string[]>
        {
            ["physical"] = new[] { "physical", "martial", "blunt", "slash", "pierce" },
            ["fire"] = FireWords,
            ["ice"] = IceWords,
            ["lightning"] = LightningWords,
            ["bleed"] = BleedWords,
            ["corrosion"] = CorrosionWords
        };
        var accepted = focus.Key == "elemental"
            ? new HashSet<string>(new[] { "fire", "ice", "lightning" }, StringComparer.Ordinal)
            : new HashSet<string>(new[] { focus.Key }, StringComparer.Ordinal);
        var evidence = families.ToDictionary(entry => entry.Key, entry => KeywordScore(text, entry.Value));
        if (accepted.Any(key => evidence.GetValueOrDefault(key) > 0)) return 1d;
        return evidence.Any(entry => !accepted.Contains(entry.Key) && entry.Value > 0) ? 0.18d : 1d;
    }

    private static double GetRawCandidatePower(GearCandidate candidate)
    {
        var save = Read(candidate.Record.ItemData, "saveItemData");
        var main = ReadNullableInt(save, "mainAttrValue") ?? 0;
        var level = ReadNullableInt(save, "level") ?? 0;
        var forge = ReadNullableInt(save, "forgeLevel") ?? 0;
        return Math.Max(0, main) + level * 50d + forge * 100d + candidate.Record.Quality * 250d;
    }

    private static double EstimateEquipmentNumericScore(object item, HeroEffectProfile profile)
    {
        try
        {
            var equipAttr = Read(Read(item, "itemEquipData"), "equipAttrData")
                            ?? throw new InvalidOperationException("Equipment AttrData is unavailable.");
            var physical = profile.Focus.Key is "physical" or "bleed";
            var elemental = profile.Focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion";
            var score = 0d;
            foreach (var mapping in GetEquipAttrMappings())
            {
                var equipType = CreateEnum("EEquipAttrType", mapping.EquipType)
                                ?? throw new InvalidOperationException($"Unknown equipment attribute type {mapping.EquipType}.");
                var value = Convert.ToDouble(InvokeRequiredInstance(equipAttr, "GetAttrValue", equipType, null!) ?? 0d, CultureInfo.InvariantCulture);
                score += value * GetBattleAttrPreScoreWeight(mapping.BattleAttrType, profile, physical, elemental);
            }

            // The runtime list already contains ordinary rolls plus the table-
            // supplied Legendary, Mythic and Unique affixes, as well as rune
            // affixes. Native AffixData.SetActiveAttrData applies a bodyAttr
            // affix by adding saveData.value to every EAttrType in effectParam.
            // Include that exact magnitude before beam pruning; previously +1
            // and +100 sub-options had the same keyword-only candidate score.
            foreach (var affix in CollectEquipmentAffixes(item).Concat(CollectGrantedMasteryAffixes(item)).Select(ResolveRuntimeAffix))
            {
                var definition = Read(affix, "tAffixData");
                if ((ReadNullableInt(definition, "effectType") ?? 0) != 1) continue;
                var value = Convert.ToDouble(ReadNullableInt(Read(affix, "saveData"), "value") ?? 0, CultureInfo.InvariantCulture);
                foreach (var attrId in ReadSequence(Read(definition, "effectParam")).Select(ToInt).Where(id => id > 0))
                    score += value * GetBattleAttrPreScoreWeight(attrId, profile, physical, elemental);
            }
            return score;
        }
        catch (Exception error)
        {
            if (!numericPreScoreFailureLogged)
            {
                numericPreScoreFailureLogged = true;
                Plugin.DiagWarning($"AUTO-GEAR NUMERIC PRE-SCORE FAILED|raw fallback is active|{error.GetBaseException().Message}");
            }
            return 0d;
        }
    }

    private static double GetBattleAttrPreScoreWeight(int battleAttrType, HeroEffectProfile profile, bool physical, bool elemental)
        => battleAttrType switch
        {
            1 => physical ? 1.4d : elemental ? 0.25d : 1d,
            2 => elemental ? 1.4d : physical ? 0.25d : 1d,
            3 or 4 => profile.Focus.Key == "defense" ? 0.9d : 0.32d,
            5 => profile.Focus.Key == "defense" ? 0.35d : 0.10d,
            7 or 9 => profile.Focus.Key == "defense" ? 1.2d : 0.25d,
            11 or 12 or 13 => 0.55d,
            25 or 190 or 6002 => profile.Focus.Key == "minion" ? 18d : 1.5d,
            81 or 82 or 83 or 84 or 91 or 92 or 93 or 94 or 95 or 96 or 185 or 191
                => profile.Focus.Key == "support" ? 18d : 1.5d,
            26 or 27 or 32 or 34 or 36 or 43 or 44 or 45 or 47 or 61 or 62 or 63 or 64 or 65 or 66
                or 85 or 86 or 130 or 131 or 132 or 134 or 151 or 152 or 153 or 154
                or 155 or 156 or 157 or 158 or 184 or 188 or 202 or 212 or 213 or 214 or 219
                => profile.Focus.Key == "defense" ? 20d : 2d,
            51 or 52 or 53 or 110 or 111 or 112 => GetDamageFamilyPreScoreWeight(profile, "physical"),
            54 or 113 => GetDamageFamilyPreScoreWeight(profile, "fire"),
            55 or 114 => GetDamageFamilyPreScoreWeight(profile, "ice"),
            56 or 115 => GetDamageFamilyPreScoreWeight(profile, "lightning"),
            121 or 123 or 125 => GetDamageFamilyPreScoreWeight(profile, "bleed"),
            122 or 124 or 126 => GetDamageFamilyPreScoreWeight(profile, "corrosion"),
            31 or 37 => profile.Focus.Key == "crit" ? 22d : profile.Focus.Key is "defense" or "support" or "minion" ? 2d : 12d,
            41 => GetGeneralAttackFamilyPreScoreWeight(profile, true),
            42 => GetGeneralAttackFamilyPreScoreWeight(profile, false),
            71 or 72 or 73 or 74 or 75 or 76 or 77 or 78
                or 99 or 100 or 101 or 102 or 103 or 104 or 105 or 106 or 107 or 108
                or 133 or 160 or 161 or 162 or 163 or 164 or 165 or 166 or 167
                or 170 or 171 or 172 or 181 or 210 or 211 or 218
                or 230 or 231 or 232 or 233 or 234 or 235 or 236 or 237 or 238 or 239
                or 240 or 241 or 242 or 243 or 244 or 245 or 246 or 247 or 248 or 249
                or 250 or 251 or 252 or 253 or 254 or 255 or 256 or 257 or 258
                => profile.Focus.Key is "defense" or "support" ? 3d : 16d,
            700 or 701 or 702 or 703 or 704 or 705 or 706 or 707 or 708 or 709 or 710 or 711 or 712
                => profile.Focus.Key is "defense" or "support" ? 1.5d : 4d,
            _ when battleAttrType is >= 800 and <= 924
                => profile.Focus.Key is "defense" or "support" ? 3d : 1d,
            _ => 0.08d
        };

    private static double GetGeneralAttackFamilyPreScoreWeight(HeroEffectProfile profile, bool physicalFamily)
    {
        if (profile.Focus.Key is "defense" or "support" or "minion") return 3d;
        if (profile.Focus.Key is "crit" or "hybrid") return 16d;
        var aligned = physicalFamily
            ? profile.Focus.Key is "physical" or "bleed"
            : profile.Focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion";
        return aligned ? 16d : 16d * 0.18d;
    }

    private static double GetDamageFamilyPreScoreWeight(HeroEffectProfile profile, string family)
    {
        var focus = profile.Focus.Key;
        if (focus is "defense" or "support" or "minion") return 2d;
        if (focus == "crit" || focus == "hybrid") return 16d;
        if (focus == "elemental") return family is "fire" or "ice" or "lightning" ? 16d : 16d * 0.18d;
        if (focus is "physical" or "fire" or "ice" or "lightning" or "bleed" or "corrosion")
            return focus == family ? 16d : 16d * 0.18d;
        return 16d;
    }

    private static List<GearSlot> GetGearSlots() => new()
    {
        new GearSlot(1, true, 0, "main weapon"),
        new GearSlot(1, false, 1, "secondary weapon"),
        new GearSlot(2, true, -1, "necklace"),
        new GearSlot(3, true, -1, "ring"),
        new GearSlot(4, true, -1, "helmet"),
        new GearSlot(5, true, -1, "chest"),
        new GearSlot(6, true, -1, "gloves"),
        new GearSlot(7, true, -1, "boots")
    };

    private static bool HasLegendMythWeaponConflict(IEnumerable<GearCandidate> items)
    {
        var restrictedWeapons = items.Where(item => item.Part == 1 && item.Record.Quality is 4 or 5).ToList();
        for (var left = 0; left < restrictedWeapons.Count - 1; left++)
        for (var right = left + 1; right < restrictedWeapons.Count; right++)
            if (GetLegendMythWeaponFamily(restrictedWeapons[left]).Overlaps(GetLegendMythWeaponFamily(restrictedWeapons[right])))
                return true;
        return false;
    }

    private static HashSet<int> GetLegendMythWeaponFamily(GearCandidate candidate)
    {
        var result = new HashSet<int>();
        var item = candidate.Record.ItemData;
        var saveId = ReadNullableInt(Read(item, "saveItemData"), "id") ?? candidate.DefinitionId;
        var definition = Read(Read(item, "itemEquipData"), "tEquipData");
        var upgradedMythId = ReadNullableInt(definition, "upMyth") ?? 0;
        if (saveId > 0) result.Add(saveId);
        if (upgradedMythId > 0) result.Add(upgradedMythId);
        if (candidate.Record.Quality == 5 && saveId > 0)
        {
            foreach (var row in ReadValues(ReadStatic("TableData", "TEquipDict"))
                         .Where(row => (ReadNullableInt(row, "upMyth") ?? 0) == saveId))
            {
                var id = ReadNullableInt(row, "id") ?? 0;
                if (id > 0) result.Add(id);
            }
        }
        return result;
    }

    private static double EstimatePartialSetSynergy(IEnumerable<GearCandidate> items, HeroEffectProfile profile)
    {
        var score = 0d;
        var effectsBySet = GetSetEffectScoreRows();
        foreach (var group in items.Where(item => item.SetId > 0).GroupBy(item => item.SetId))
        {
            var count = group.Count();
            if (!effectsBySet.TryGetValue(group.Key, out var effects)) continue;
            var activeEffects = effects.Where(effect => effect.Pieces <= count).ToList();
            if (activeEffects.Count == 0) continue;
            var setThemeText = GetSetThemeText(group.Key, effects);
            // Only native effects whose exact 2/4-piece threshold is active are
            // scored. Native ability IDs/conditions decide whether the jointly
            // planned skills can trigger them; localized descriptions do not.
            foreach (var effect in activeEffects)
            {
                // HeroEquipData.UpdateSetsEffect activates a set ability at
                // level zero. TEquipSetsEffect.abilityParam is ability payload,
                // not an ability level.
                var ability = ResolveAbilityTableRow(effect.AbilityId, 0);
                var abilityScore = ScoreNativeAbility(ability, profile);
                if (!HasExplicitNativeAbilityTheme(ability, profile.PlannedSkills))
                    abilityScore *= GetOpaqueEffectThemeWeight(setThemeText, profile.Focus);
                if (abilityScore <= 0d) continue;
                score += abilityScore;
            }
        }
        return score;
    }

    private static Dictionary<int, List<SetEffectScoreRow>> GetSetEffectScoreRows()
    {
        if (setEffectScoreRows is not null) return setEffectScoreRows;
        var rawRows = ReadValues(ReadStatic("TableData", "TEquipSetsEffectDict"))
            .Select(effect => new
            {
                SetId = ReadNullableInt(effect, "sesId") ?? 0,
                EffectId = ReadNullableInt(effect, "id") ?? 0,
                Index = ReadNullableInt(effect, "index") ?? int.MaxValue,
                Text = Clean(string.Join(" ", ReadString(effect, "des") ?? string.Empty, EnglishText(effect, "_des", string.Empty) ?? string.Empty)).ToLowerInvariant(),
                AbilityId = ReadNullableInt(effect, "abilityId") ?? 0,
                AbilityParameters = ReadSequence(Read(effect, "abilityParam")).Select(ToInt).ToList()
            })
            .Where(entry => entry.SetId > 0 && entry.EffectId > 0)
            .ToList();
        var rows = rawRows
            .GroupBy(entry => entry.SetId)
            .ToDictionary(group => group.Key, group =>
            {
                var wearCounts = GetSetEffectWearCounts(group.Key);
                return group.Where(entry => wearCounts.Count == 0 || wearCounts.ContainsKey(entry.EffectId))
                    .Select(entry => new SetEffectScoreRow(
                        entry.EffectId,
                        wearCounts.TryGetValue(entry.EffectId, out var pieces)
                            ? pieces
                            : Math.Max(2, entry.Index == int.MaxValue ? 2 : entry.Index * 2),
                        entry.Text,
                        entry.AbilityId,
                        entry.AbilityParameters))
                    .OrderBy(entry => entry.Pieces).ToList();
            });
        if (rows.Count > 0) setEffectScoreRows = rows;
        return rows;
    }

    private static Dictionary<int, int> GetSetEffectWearCounts(int setId)
    {
        var result = new Dictionary<int, int>();
        var set = InvokeStatic("TableData", "getTEquipSetsData", setId);
        var encoded = ReadString(set, "affixStr") ?? string.Empty;
        // Native InitSetsAffixDic parses semicolon-separated rows where the
        // first integer is wearCount and every remaining integer is an effect
        // id. A row can activate multiple effects at the same threshold.
        foreach (var row in encoded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var values = row.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
                .ToList();
            if (values.Count < 2 || values[0] <= 0) continue;
            var pieces = values[0];
            foreach (var effectId in values.Skip(1).Where(value => value > 0))
                result[effectId] = pieces;
        }
        if (result.Count > 0) return result;

        // Version-tolerant fallback to the game's initialized runtime table.
        foreach (var entry in ReadEntries(ReadStatic("EquipSys", "setsEffectDic")))
        {
            if (ToInt(Read(entry, "Key")) != setId) continue;
            foreach (var effectData in ReadSequence(Read(entry, "Value")))
            {
                var effectId = ReadNullableInt(Read(effectData, "tSetEffectData"), "id") ?? 0;
                var pieces = ReadNullableInt(effectData, "wearCount") ?? 0;
                if (effectId > 0 && pieces > 0) result[effectId] = pieces;
            }
        }
        return result;
    }

    private static string GetSetThemeText(int setId, IReadOnlyCollection<SetEffectScoreRow> effects)
    {
        if (SetThemeTextCache.TryGetValue(setId, out var cached)) return cached;
        var set = InvokeStatic("TableData", "getTEquipSetsData", setId);
        var parts = new List<string>
        {
            ReadString(set, "name") ?? string.Empty,
            EnglishName(set, string.Empty) ?? string.Empty,
            ReadString(set, "des") ?? string.Empty
        };
        foreach (var member in ReadValues(ReadStatic("TableData", "TEquipDict"))
                     .Where(member => (ReadNullableInt(member, "setsId") ?? 0) == setId))
        {
            parts.Add(ReadString(member, "name") ?? string.Empty);
            parts.Add(EnglishName(member, string.Empty) ?? string.Empty);
            parts.Add(ReadString(member, "des") ?? string.Empty);
            parts.Add(EnglishText(member, "_des", string.Empty) ?? string.Empty);
        }
        parts.AddRange(effects.Select(effect => effect.Text));
        var text = Clean(string.Join(" ", parts)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(text)) SetThemeTextCache[setId] = text;
        return text;
    }

    private static bool IsSpecificElementFocus(string focusKey)
        => focusKey is "fire" or "ice" or "lightning";

    private static int GetOpposingElementThemeScore(string text, string focusKey)
        => focusKey switch
        {
            "fire" => Math.Max(KeywordScore(text, IceWords), KeywordScore(text, LightningWords)),
            "ice" => Math.Max(KeywordScore(text, FireWords), KeywordScore(text, LightningWords)),
            "lightning" => Math.Max(KeywordScore(text, FireWords), KeywordScore(text, IceWords)),
            _ => 0
        };

    private static string DescribeSetPlan(IEnumerable<GearCandidate> items, HeroEffectProfile profile)
    {
        var effectsBySet = GetSetEffectScoreRows();
        var parts = new List<string>();
        foreach (var group in items.Where(item => item.SetId > 0).GroupBy(item => item.SetId).OrderBy(group => group.Key))
        {
            effectsBySet.TryGetValue(group.Key, out var effects);
            effects ??= new List<SetEffectScoreRow>();
            var activeEffects = effects.Where(effect => effect.Pieces <= group.Count()).ToList();
            var text = Clean(string.Join(" ", activeEffects.Select(effect => effect.Text))).ToLowerInvariant();
            var active = activeEffects.Select(effect => effect.Pieces).Distinct().OrderBy(value => value);
            parts.Add($"set={group.Key}/pieces={group.Count()}/active={string.Join(',', active)}/theme={KeywordScore(text, profile.Focus.Keywords)}/opposing={GetOpposingElementThemeScore(text, profile.Focus.Key)}/guide={group.Count(item => profile.RecommendedEquipmentIds.Contains(item.DefinitionId))}");
        }
        return parts.Count == 0 ? "none" : string.Join(" ; ", parts);
    }

    private static LoadoutEvaluation EvaluateCompleteLoadout(
        List<GearCandidate> items,
        object hero,
        HeroEffectProfile baselineProfile,
        List<object> currentItems,
        bool rebuildMasteries = false,
        bool fullyOptimizeTalentLevels = false)
    {
        try
        {
            var simulated = CreateJointLoadoutAttr(
                hero, items, currentItems, baselineProfile.PlannedSkills, !rebuildMasteries);
            var profile = ResolveJointSkillProfile(
                items, hero, baselineProfile, simulated, rebuildMasteries, fullyOptimizeTalentLevels);
            if (profile.Focus.Key != baselineProfile.Focus.Key)
                throw new InvalidOperationException($"Hero theme changed from {baselineProfile.Focus.Key} to {profile.Focus.Key} during evaluation.");
            if (profile.PreviewBaseSkillId <= 0 || !double.IsFinite(profile.JointSkillObjective)
                                                  || profile.JointSkillObjective < 0d)
                throw new InvalidOperationException($"Joint skill package is invalid ({profile.JointSkillReason}).");

            var rescoredItems = RescoreLoadoutForProfile(items, profile);
            var nativeAbilities = EvaluateNativeAbilityPackage(rescoredItems, hero, profile, simulated);
            if (!nativeAbilities.IsComplete)
                throw new InvalidOperationException(
                    $"Ability preview failed ({string.Join(',', nativeAbilities.FailedAbilityIds.OrderBy(id => id))}).");
            // Effect-type 3 and Set heuristics are useful during beam pruning, but
            // the finalist replaces them with one native AbilityData package. This
            // prevents the same conditional effect from being added twice.
            var score = ScoreItemsWithDeduplicatedEffects(rescoredItems, profile)
                        - ScoreItemAbilityHeuristics(rescoredItems, profile)
                        + nativeAbilities.StructuralScore;
            score += profile.JointSkillObjective * 1000d;
            // Unknown battle-only hooks receive a zero lower bound. Prefer a fully
            // modeled loadout only as a deterministic tie-break, never by an
            // invented positive bonus.
            score -= nativeAbilities.UnmodeledAbilityIds.Count * 0.000001d;
            var weaponTypes = rescoredItems.Where(item => item.Part == 1).Select(item => item.WeaponType).Where(value => value > 0).ToHashSet();
            if (profile.BaseWeaponRequirement.Count > 0)
                score += profile.BaseWeaponRequirement.Overlaps(weaponTypes) ? 3200d : -30000d;
            foreach (var preference in profile.SkillWeaponPreferences)
                score += preference.Overlaps(weaponTypes) ? 420d : -12000d;

            // Use the game's own AttrData calculations on a temporary copy. A
            // failed native performance preview invalidates this finalist instead
            // of silently retaining a heuristic-only score.
            if (!TryEvaluateFinalPerformance(
                    rescoredItems, hero, profile, currentItems, out var performance, simulated, nativeAbilities))
                throw new InvalidOperationException("The 60-second native preview produced no rankable output.");
            score += performance * 2.4d;
            if (!double.IsFinite(score)) throw new InvalidOperationException("The final loadout score is not finite.");
            return new LoadoutEvaluation(score, profile);
        }
        catch (Exception error)
        {
            var reason = error.GetBaseException().Message;
            Plugin.DiagDebug($"AUTO-GEAR FINALIST REJECTED|{reason}");
            return new LoadoutEvaluation(double.NegativeInfinity, baselineProfile, false, reason);
        }
    }

    private static object CreateJointLoadoutAttr(
        object hero,
        IReadOnlyCollection<GearCandidate> items,
        IReadOnlyCollection<object> currentItems,
        PlannedSkillTarget planned,
        bool includePlannedMasteries = true)
    {
        var talentData = Read(hero, "heroTalentData") ?? throw new InvalidOperationException("HeroTalentData is unavailable.");
        var gridTalents = ReadValues(Read(talentData, "talentDic")).ToList();
        var simulated = CreateTalentNeutralObjectiveAttr(hero, gridTalents);
        foreach (var current in currentItems) ApplyEquipmentToAttr(simulated, current, false);
        foreach (var candidate in items) ApplyEquipmentToAttr(simulated, candidate.Record.ItemData, true);
        if (includePlannedMasteries)
            ApplyMinimumPlannedMasteryPreview(simulated, gridTalents, planned);
        return simulated;
    }

    private static void ApplyMinimumPlannedMasteryPreview(
        object attrData,
        IReadOnlyCollection<object> gridTalents,
        PlannedSkillTarget planned)
    {
        // The neutral copy intentionally removes the outgoing saved build. Give
        // every mastery selected by the native plan its first investable level so
        // finalist gear is not evaluated as if the planned masteries did nothing.
        // Exact remaining-point allocation is still performed by Auto Skills.
        foreach (var talentId in planned.MasteryTalentIds)
        {
            var talent = gridTalents.FirstOrDefault(value =>
                (ReadNullableInt(Read(value, "tTalentData"), "id") ?? 0) == talentId);
            var definition = Read(talent, "tTalentData") ?? InvokeStatic("TableData", "getTTalentData", talentId);
            var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
            if (definition is null || masteryId <= 0)
                throw new InvalidOperationException($"Planned mastery talent {talentId} is unavailable.");
            var cap = talent is null ? 1 : Math.Max(1, GetTalentLevelCap(talent));
            var baseLevel = talent is null ? 0 : Math.Min(GetTalentBaseLevelRequired(talent), cap);
            var targetLevel = Math.Min(cap, baseLevel + 1);
            if (targetLevel <= baseLevel)
                throw new InvalidOperationException($"Planned mastery talent {talentId} has no investable level.");
            ApplyMasteryAttributePreview(attrData, masteryId, targetLevel, cap, true);
        }
    }

    private static void ApplyPlannedMasteryLevelPreview(
        object attrData,
        IReadOnlyCollection<object> gridTalents,
        PlannedSkillTarget planned)
    {
        var gridById = BuildTalentGridById(gridTalents);
        foreach (var talentId in planned.MasteryTalentIds.OrderBy(id => id))
        {
            if (!gridById.TryGetValue(talentId, out var talent))
                throw new InvalidOperationException($"Planned mastery talent {talentId} is unavailable.");
            var definition = Read(talent, "tTalentData");
            var masteryId = ReadNullableInt(definition, "masteryId") ?? 0;
            var cap = GetTalentLevelCap(talent);
            var baseLevel = Math.Min(GetTalentBaseLevelRequired(talent), cap);
            var savedTarget = planned.TargetSavedLevels.GetValueOrDefault(talentId);
            if (savedTarget < 0 || savedTarget > Math.Max(0, cap - baseLevel))
                throw new InvalidOperationException(
                    $"Planned mastery talent {talentId} target {savedTarget} exceeds its native cap {cap} (base {baseLevel}).");
            if (baseLevel > 0)
                ApplyMasteryAttributePreview(attrData, masteryId, baseLevel, cap, false);
            if (baseLevel + savedTarget > 0)
                ApplyMasteryAttributePreview(attrData, masteryId, baseLevel + savedTarget, cap, true);
        }
    }

    private static List<GearCandidate> RescoreLoadoutForProfile(IEnumerable<GearCandidate> items, HeroEffectProfile profile)
        => items.Select(candidate =>
        {
            var equipmentScore = ScoreEquipment(candidate.Record.ItemData, profile);
            var numericScore = EstimateEquipmentNumericScore(candidate.Record.ItemData, profile);
            var rawScore = equipmentScore.Total + numericScore * 0.08d;
            return candidate with
            {
                Score = ScoreSingleItemWithDeduplicatedEffects(candidate.Record.ItemData, profile, rawScore),
                NumericScore = numericScore,
                DirectMatches = equipmentScore.DirectMatches,
                ThemeMatches = equipmentScore.ThemeMatches
            };
        }).ToList();

    private static HeroEffectProfile ResolveJointSkillProfile(
        IReadOnlyCollection<GearCandidate> items,
        object hero,
        HeroEffectProfile baseline,
        object simulatedAttr,
        bool rebuildMasteries = false,
        bool fullyOptimizeTalentLevels = false)
    {
        var planned = baseline.PlannedSkills;
        var weaponTypes = items.Where(item => item.Part == 1)
            .Select(item => item.WeaponType).Where(value => value > 0).ToHashSet();
        var baseSource = fullyOptimizeTalentLevels && planned.BaseSkillId > 0
            ? new[] { planned.BaseSkillId }
            : planned.BaseCandidateSkillIds.Append(planned.BaseSkillId);
        var baseCandidates = baseSource
            .Where(id => id > 0 && IsSkillCompatibleWithWeaponTypes(id, weaponTypes))
            .Distinct().OrderBy(id => id).ToList();
        var grantedActive = new HashSet<int>();
        foreach (var entry in items.SelectMany(candidate => GetGrantedExtraSkillLevels(candidate.Record.ItemData)))
        {
            const string prefix = "extra-skill:";
            if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(entry.Key.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var skillId)
                && skillId > 0 && IsSkillCompatibleWithWeaponTypes(skillId, weaponTypes))
                grantedActive.Add(skillId);
        }
        var ordinaryActiveSource = fullyOptimizeTalentLevels
            ? planned.ActiveSkillIds
            : planned.ActiveCandidateSkillIds.Concat(planned.ActiveSkillIds);
        var ordinaryActiveCandidates = ordinaryActiveSource
            .Where(id => id > 0 && IsSkillCompatibleWithWeaponTypes(id, weaponTypes))
            .Distinct().OrderBy(id => id).ToList();
        if (baseCandidates.Count == 0) return baseline with
        {
            JointSkillObjective = -1000000d,
            JointSkillReason = "no weapon-compatible base skill"
        };
        var talentData = Read(hero, "heroTalentData");
        var fixedGridTalentIdsBySkillId = talentData is null
            ? new Dictionary<int, int>()
            : GetTransformableTalents(talentData)
                .Where(talent => !IsTalentLockedRequired(talent) && IsTalentUnreplaceable(talent))
                .Select(talent => Read(talent, "tTalentData"))
                .Select(row => (
                    TalentId: ReadNullableInt(row, "id") ?? 0,
                    SkillId: ReadNullableInt(row, "skillId") ?? 0,
                    Floor: ReadNullableInt(row, "floor") ?? int.MaxValue,
                    Index: ReadNullableInt(row, "index") ?? int.MaxValue))
                .Where(entry => entry.TalentId > 0 && entry.SkillId > 0)
                .GroupBy(entry => entry.SkillId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(entry => entry.Floor).ThenBy(entry => entry.Index)
                        .ThenBy(entry => entry.TalentId).First().TalentId);
        var fixedGridActive = fixedGridTalentIdsBySkillId.Keys.ToHashSet();
        // Equipment-granted skills are present in native GetSkillList regardless
        // of shrine-row selection, so include them in every package without
        // consuming one of the hero's ordinary active rows.
        var packageFixedActive = fixedGridActive.Concat(grantedActive).ToHashSet();
        if (packageFixedActive.Any(id => !IsSkillCompatibleWithWeaponTypes(id, weaponTypes)))
            return baseline with
            {
                JointSkillObjective = -1000000d,
                JointSkillReason = "candidate weapons cannot use a fixed active skill"
            };
        ordinaryActiveCandidates = ordinaryActiveCandidates.Concat(fixedGridActive)
            .Distinct().OrderBy(id => id).ToList();

        var roleCache = new Dictionary<(int SkillId, int Level), NativeSkillRoleProfile>();
        var strictAbilityPreviewCache = new Dictionary<(int SkillId, int Level), NativeSkillAlwaysOnPreview>();
        NativeSkillRoleProfile RoleFor(
            int skillId,
            int level,
            object? roleAttr = null,
            bool applyOwnAlwaysOnAbilityAttrs = true)
        {
            roleAttr ??= simulatedAttr;
            level = Math.Max(1, Math.Max(level, GetGrantedSkillLevel(items, skillId)));
            var cacheable = ReferenceEquals(roleAttr, simulatedAttr) && applyOwnAlwaysOnAbilityAttrs;
            if (cacheable && roleCache.TryGetValue((skillId, level), out var cached)) return cached;
            var role = ReadNativeSkillRoleProfile(
                hero, skillId, level, roleAttr, false, items, true, applyOwnAlwaysOnAbilityAttrs);
            if (!IsNativeRoleRankable(role, baseline.Focus) && !packageFixedActive.Contains(skillId))
                throw new InvalidOperationException(
                    $"Skill {skillId} level {level} has no rankable joint output: {role.Failure}");
            if (cacheable) roleCache[(skillId, level)] = role;
            return role;
        }
        NativeSkillRoleProfile SeedRoleFor(int skillId)
        {
            var level = planned.TargetSavedLevels.Count > 0
                ? planned.BaseSkillId == skillId ? planned.BaseSkillLevel : 1
                : 1;
            return RoleFor(skillId, level);
        }

        var saveHeroForPlan = ReadRequiredProperty(hero, "saveHeroData")
                              ?? throw new InvalidOperationException("SaveHeroData is unavailable.");
        var jobIdForPlan = ReadRequiredIntProperty(saveHeroForPlan, "jobId");
        var allJobSkillRows = ReadValues(ReadStatic("TableData", "TTalentDict"))
            .Where(row => (ReadNullableInt(row, "jobId") ?? 0) == jobIdForPlan)
            .Where(row => IsBaseSkillDefinition(row) || IsTransformableSkillDefinition(row))
            .ToList();
        var planningGridTalents = talentData is null
            ? new List<object>()
            : ReadValues(ReadRequiredProperty(talentData, "talentDic"))
                .DistinctBy(talent => NativeObjectKey(talent, talent)).ToList();
        List<int> ResolveTalentIds(int baseSkillId, IEnumerable<int> activeSkillIds)
        {
            var selected = activeSkillIds.Append(baseSkillId).Where(id => id > 0).ToHashSet();
            return allJobSkillRows.Where(row => selected.Contains(ReadNullableInt(row, "skillId") ?? 0))
                .GroupBy(row => ReadNullableInt(row, "skillId") ?? 0)
                .Where(group => group.Key > 0)
                .Select(group => fixedGridTalentIdsBySkillId.TryGetValue(group.Key, out var fixedTalentId)
                    ? fixedTalentId
                    : group.OrderBy(row => group.Key == baseSkillId && IsBaseSkillDefinition(row) ? 0 : 1)
                        .ThenBy(row => ReadNullableInt(row, "index") ?? int.MaxValue)
                        .ThenBy(row => ReadNullableInt(row, "id") ?? int.MaxValue)
                        .Select(row => ReadNullableInt(row, "id") ?? 0)
                        .FirstOrDefault(id => id > 0))
                .Where(id => id > 0).ToList();
        }

        var bestBase = baseCandidates[0];
        var bestActive = new List<int>();
        var bestScore = double.NegativeInfinity;
        var bestNativeRoleScore = double.NegativeInfinity;
        var bestRedundantGrantedActiveCount = int.MaxValue;
        var bestMasteryCandidateIds = new List<int>();
        TalentLevelPlan? resolvedBestLevelPlan = null;
        foreach (var baseSkillId in baseCandidates)
        {
            var slotCount = Math.Max(fixedGridActive.Count,
                Math.Min(Math.Max(0, planned.ActiveSlotCount), ordinaryActiveCandidates.Count));
            // A skill supplied by equipment is already present in native
            // GetSkillList and consumes neither a shrine row nor a talent point.
            // Selecting it again as an ordinary learned skill wastes a physical
            // row and can make an otherwise valid exact point plan fail when the
            // granted effect itself is conditional-only. A genuinely fixed grid
            // row is preserved above even when equipment also grants that skill.
            var optional = ordinaryActiveCandidates
                .Where(id => !fixedGridActive.Contains(id) && !grantedActive.Contains(id))
                .ToList();
            var optionalCapacity = Math.Min(Math.Max(0, slotCount - fixedGridActive.Count), optional.Count);
            long packageCount = 0;
            for (var choose = 0; choose <= optionalCapacity && packageCount <= 512; choose++)
                packageCount += GetCombinationCount(optional.Count, choose);
            // The exact screening pass has already compared every retained skill
            // package for this loadout. Its winner is carried in `planned`; the
            // expensive nonlinear point allocator must refine that same package,
            // rather than repeating the full allocator for every alternative.
            // A failed full refinement is handled by the outer finalist fallback.
            var rawCombinations = fullyOptimizeTalentLevels
                ? new[]
                {
                    fixedGridActive.Concat(optional)
                        .Distinct().OrderBy(id => id).ToList()
                }.AsEnumerable()
                : packageCount <= 512
                    ? Enumerable.Range(0, optionalCapacity + 1)
                        .SelectMany(choose => EnumerateSkillCombinations(optional, choose)
                            .Select(selected => fixedGridActive.Concat(selected).Distinct().OrderBy(id => id).ToList()))
                    : Enumerable.Range(0, optionalCapacity + 1)
                        .SelectMany(choose => EnumerateGreedySkillPackages(optional, choose, baseSkillId, SeedRoleFor, baseline.Focus)
                            .Select(selected => fixedGridActive.Concat(selected).Distinct().OrderBy(id => id).ToList()));
            var rankedCombinations = rawCombinations
                .GroupBy(selected => string.Join(",", selected), StringComparer.Ordinal)
                .Select(group =>
                {
                    var selected = group.First();
                    var packageActive = selected.Concat(grantedActive)
                        .Where(id => id > 0 && id != baseSkillId).Distinct().OrderBy(id => id).ToList();
                    var selectedIds = packageActive.Append(baseSkillId).ToHashSet();
                    var cheapProfile = baseline with
                    {
                        SkillIds = selectedIds,
                        PreferredSkillIds = new HashSet<int>(selectedIds)
                    };
                    try
                    {
                        var score = ScoreSharedSkillPackage(
                                        SeedRoleFor(baseSkillId),
                                        packageActive.Select(SeedRoleFor),
                                        baseline.Focus)
                                    + ScoreNativeConditionalPackage(items, cheapProfile) / 1000d;
                        return (Skills: selected, Score: score, Key: group.Key);
                    }
                    catch (InvalidOperationException)
                    {
                        return (Skills: selected, Score: double.NegativeInfinity, Key: group.Key);
                    }
                })
                .Where(entry => double.IsFinite(entry.Score))
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList();
            var combinationLimit = fullyOptimizeTalentLevels ? 1 : rebuildMasteries ? 3 : 2;
            var retainedCombinations = rankedCombinations.Take(Math.Max(1, combinationLimit / 2)).ToList();
            var fixedOnly = rankedCombinations.FirstOrDefault(entry =>
                entry.Skills.Count == fixedGridActive.Count
                && entry.Skills.All(fixedGridActive.Contains));
            if (fixedOnly.Skills is not null
                && retainedCombinations.All(entry => entry.Key != fixedOnly.Key))
                retainedCombinations.Add(fixedOnly);
            foreach (var skillId in optional)
            {
                if (retainedCombinations.Count >= combinationLimit) break;
                var representative = rankedCombinations.FirstOrDefault(entry => entry.Skills.Contains(skillId));
                if (representative.Skills is not null
                    && retainedCombinations.All(entry => entry.Key != representative.Key))
                    retainedCombinations.Add(representative);
            }
            foreach (var entry in rankedCombinations)
            {
                if (retainedCombinations.Count >= combinationLimit) break;
                if (retainedCombinations.All(existing => existing.Key != entry.Key))
                    retainedCombinations.Add(entry);
            }
            foreach (var ordinarySelected in retainedCombinations.Select(entry => entry.Skills))
            {
                try
                {
                    var selectedTalentIds = ResolveTalentIds(baseSkillId, ordinarySelected);
                    var packageActive = ordinarySelected.Concat(grantedActive)
                        .Where(id => id > 0 && id != baseSkillId).Distinct().OrderBy(id => id).ToList();
                    var selectedIds = packageActive.Append(baseSkillId).ToHashSet();
                    var candidateTalentPlan = new PreferredTalentPlan(
                        null,
                        selectedTalentIds,
                        rebuildMasteries ? new List<int>() : planned.MasteryTalentIds.OrderBy(id => id).ToList(),
                        selectedIds,
                        "joint gear/skill objective");
                    if (rebuildMasteries)
                        candidateTalentPlan = RebuildMasteryPlanForSelectedSkills(
                            hero,
                            baseline.Focus,
                            planned.TotalTalentPointBudget,
                            candidateTalentPlan,
                            simulatedAttr,
                            items);
                    var levelPlan = BuildDeterministicTalentLevelPlan(
                        hero,
                        baseline.Focus,
                        selectedTalentIds,
                        candidateTalentPlan.MasteryTalentIds,
                        planned.TotalTalentPointBudget,
                        simulatedAttr,
                        items,
                        fullyOptimizeTalentLevels ? 2 : 1,
                        planned.TargetSavedLevels,
                        !fullyOptimizeTalentLevels);
                    var candidateProfile = baseline with
                    {
                        SkillIds = selectedIds,
                        PreferredSkillIds = new HashSet<int>(selectedIds)
                    };
                    var candidateAttr = InvokeRequiredStaticMany("AttrData", "copyCreate", simulatedAttr)
                                        ?? throw new InvalidOperationException("Joint candidate AttrData copy failed.");
                    var candidatePlanned = planned with
                    {
                        MasteryTalentIds = candidateTalentPlan.MasteryTalentIds
                            .Where(id => levelPlan.SavedLevels.GetValueOrDefault(id) > 0).ToHashSet(),
                        TargetSavedLevels = new Dictionary<int, int>(levelPlan.SavedLevels),
                        PlanToken = levelPlan.Token
                    };
                    ApplyPlannedMasteryLevelPreview(candidateAttr, planningGridTalents, candidatePlanned);
                    var baseLevel = levelPlan.EffectiveSkillLevels.GetValueOrDefault(
                        baseSkillId, Math.Max(1, GetGrantedSkillLevel(items, baseSkillId)));
                    var packageLevels = packageActive
                        .Select(id => (SkillId: id, Level: levelPlan.EffectiveSkillLevels.GetValueOrDefault(
                            id, Math.Max(1, GetGrantedSkillLevel(items, id)))))
                        .Append((SkillId: baseSkillId, Level: baseLevel))
                        .ToList();
                    var candidateStrictAbilityPreviewCache =
                        new Dictionary<(int SkillId, int Level), NativeSkillAlwaysOnPreview>();
                    var packageAttr = CreateStrictSkillPackageAdjustedAttr(
                        hero, packageLevels, candidateAttr, false, items, out _,
                        previewCache: candidateStrictAbilityPreviewCache);
                    var activeRoles = packageLevels.Where(entry => entry.SkillId != baseSkillId)
                        .Select(entry => RoleFor(entry.SkillId, entry.Level, packageAttr, false)).ToList();
                    var package = BuildSharedSkillPackage(
                        RoleFor(baseSkillId, baseLevel, packageAttr, false), activeRoles);
                    if (!IsNativeRoleRankable(package, baseline.Focus))
                        throw new InvalidOperationException($"The joint level-plan has no rankable native output: {package.Failure}");
                    var nativeRoleScore = ScoreNativeSkillRoleObjective(package, baseline.Focus);
                    var strictAbilityAttrDelta = ScoreHeroAttrObjective(packageAttr, baseline.Focus)
                                                 - ScoreHeroAttrObjective(simulatedAttr, baseline.Focus);
                    var packageScore = nativeRoleScore
                                       + strictAbilityAttrDelta
                                       + ScoreNativeConditionalPackage(items, candidateProfile) / 1000d;
                    var packageTie = planned.ObjectiveBySkillId.GetValueOrDefault(baseSkillId)
                                     + ordinarySelected.Sum(id => planned.ObjectiveBySkillId.GetValueOrDefault(id));
                    var bestTie = planned.ObjectiveBySkillId.GetValueOrDefault(bestBase)
                                  + bestActive.Sum(id => planned.ObjectiveBySkillId.GetValueOrDefault(id));
                    var redundantGrantedActiveCount = ordinarySelected.Count(id =>
                        GetGrantedSkillLevel(items, id) > 0
                        && levelPlan.EffectiveSkillLevels.GetValueOrDefault(id)
                        <= GetGrantedSkillLevel(items, id));
                    if (packageScore > bestScore
                        || (Math.Abs(packageScore - bestScore) < 0.000001d
                            && (redundantGrantedActiveCount < bestRedundantGrantedActiveCount
                                || (redundantGrantedActiveCount == bestRedundantGrantedActiveCount
                                    && packageTie > bestTie))))
                    {
                        bestScore = packageScore;
                        bestNativeRoleScore = nativeRoleScore + strictAbilityAttrDelta;
                        bestBase = baseSkillId;
                        bestActive = ordinarySelected.ToList();
                        bestRedundantGrantedActiveCount = redundantGrantedActiveCount;
                        bestMasteryCandidateIds = candidateTalentPlan.MasteryTalentIds
                            .Distinct().OrderBy(id => id).ToList();
                        resolvedBestLevelPlan = levelPlan;
                    }
                }
                catch (InvalidOperationException)
                {
                    // A rejected skill/level vector invalidates only this native
                    // package. Other skill packages for the same equipment set
                    // must still be compared before the loadout itself is rejected.
                }
            }
        }

        if (resolvedBestLevelPlan is null)
            throw new InvalidOperationException("No complete joint skill level plan could be evaluated.");
        var resolvedTalentIds = ResolveTalentIds(bestBase, bestActive);
        var resolvedBaseTalentId = allJobSkillRows
            .Where(IsBaseSkillDefinition)
            .Where(row => (ReadNullableInt(row, "skillId") ?? 0) == bestBase)
            .Select(row => ReadNullableInt(row, "id") ?? 0)
            .FirstOrDefault(id => id > 0);
        var selectedMasteryTalentIds = bestMasteryCandidateIds
            .Where(id => resolvedBestLevelPlan.SavedLevels.GetValueOrDefault(id) > 0)
            .ToHashSet();
        var resolvedPlanned = planned with
        {
            BaseTalentId = resolvedBaseTalentId > 0 ? resolvedBaseTalentId : planned.BaseTalentId,
            BaseSkillId = bestBase,
            ActiveSkillIds = bestActive.ToHashSet(),
            TalentIds = resolvedTalentIds.ToHashSet(),
            MasteryTalentIds = selectedMasteryTalentIds,
            TargetSavedLevels = new Dictionary<int, int>(resolvedBestLevelPlan.SavedLevels),
            PlanToken = resolvedBestLevelPlan.Token,
            BaseSkillLevel = resolvedBestLevelPlan.EffectiveSkillLevels.GetValueOrDefault(bestBase, 1)
        };
        if (rebuildMasteries)
        {
            // Every skill combination above was already evaluated with its own
            // mastery candidate set and exact point vector. Apply the winning
            // vector to the finalist AttrData, then repeat the strict package
            // preview once so all later gear/ability scoring sees that same plan.
            ApplyPlannedMasteryLevelPreview(simulatedAttr, planningGridTalents, resolvedPlanned);
            roleCache.Clear();
            strictAbilityPreviewCache.Clear();
            var exactLevels = bestActive.Concat(grantedActive)
                .Where(id => id > 0 && id != bestBase).Distinct().Select(id => (
                    SkillId: id,
                    Level: resolvedBestLevelPlan.EffectiveSkillLevels.GetValueOrDefault(
                        id, Math.Max(1, GetGrantedSkillLevel(items, id)))))
                .Append((SkillId: bestBase, Level: resolvedPlanned.BaseSkillLevel))
                .ToList();
            var exactPackageAttr = CreateStrictSkillPackageAdjustedAttr(
                hero, exactLevels, simulatedAttr, false, items, out _,
                previewCache: strictAbilityPreviewCache);
            var exactBaseRole = RoleFor(
                bestBase, resolvedPlanned.BaseSkillLevel, exactPackageAttr, false);
            var exactActiveRoles = exactLevels.Where(entry => entry.SkillId != bestBase)
                .Select(entry => RoleFor(entry.SkillId, entry.Level, exactPackageAttr, false)).ToList();
            var exactPackage = BuildSharedSkillPackage(exactBaseRole, exactActiveRoles);
            if (!IsNativeRoleRankable(exactPackage, baseline.Focus))
                throw new InvalidOperationException($"The rebuilt joint level-plan has no rankable native output: {exactPackage.Failure}");
            bestNativeRoleScore = ScoreNativeSkillRoleObjective(exactPackage, baseline.Focus)
                                  + ScoreHeroAttrObjective(exactPackageAttr, baseline.Focus)
                                  - ScoreHeroAttrObjective(simulatedAttr, baseline.Focus);
        }

        var packageBestActive = bestActive.Concat(grantedActive)
            .Where(id => id > 0 && id != bestBase).Distinct().OrderBy(id => id).ToList();
        var reason = DescribeJointSkillSelection(bestBase, packageBestActive, items);
        return BuildProfileForSkillSelection(
            hero,
            baseline.Focus,
            resolvedPlanned,
            bestBase,
            packageBestActive,
            resolvedPlanned.BaseSkillLevel,
            simulatedAttr,
            items,
            reason) with { JointSkillObjective = Math.Max(0d, bestNativeRoleScore) };
    }

    private static long GetCombinationCount(int count, int choose)
    {
        if (choose < 0 || choose > count) return 0;
        choose = Math.Min(choose, count - choose);
        long value = 1;
        for (var index = 1; index <= choose; index++)
        {
            if (value > 20000) return 20001;
            value = value * (count - choose + index) / index;
        }
        return value;
    }

    private static IEnumerable<List<int>> EnumerateSkillCombinations(IReadOnlyList<int> values, int choose)
    {
        var buffer = new List<int>(choose);
        return Walk(0, choose);

        IEnumerable<List<int>> Walk(int start, int remaining)
        {
            if (remaining == 0)
            {
                yield return buffer.ToList();
                yield break;
            }
            for (var index = start; index <= values.Count - remaining; index++)
            {
                buffer.Add(values[index]);
                foreach (var result in Walk(index + 1, remaining - 1)) yield return result;
                buffer.RemoveAt(buffer.Count - 1);
            }
        }
    }

    private static IEnumerable<List<int>> EnumerateGreedySkillPackages(
        IReadOnlyCollection<int> values,
        int choose,
        int baseSkillId,
        Func<int, NativeSkillRoleProfile> roleFor,
        HeroFocus focus)
    {
        if (choose == 0)
        {
            yield return new List<int>();
            yield break;
        }

        // When exhaustive enumeration is too large, keep both the unconstrained
        // greedy result and one greedily completed package forced through every
        // available skill. This bounds the search while preventing a skill with a
        // weak one-step score (but a strong level/set milestone) from disappearing
        // before the exact joint planner can evaluate it.
        var orderedValues = values.Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
        foreach (var seedSkillId in new[] { 0 }.Concat(orderedValues))
        {
            var selected = seedSkillId > 0
                ? new List<int> { seedSkillId }
                : new List<int>();
            while (selected.Count < choose)
            {
                var next = orderedValues.Where(id => !selected.Contains(id))
                    .OrderByDescending(id => ScoreSharedSkillPackage(
                        roleFor(baseSkillId), selected.Append(id).Select(roleFor), focus))
                    .ThenBy(id => id)
                    .FirstOrDefault();
                if (next <= 0) break;
                selected.Add(next);
            }
            if (selected.Count == choose)
                yield return selected.OrderBy(id => id).ToList();
        }
    }

    private static double ScoreNativeConditionalPackage(
        IEnumerable<GearCandidate> items,
        HeroEffectProfile profile)
    {
        var candidates = items.ToList();
        var score = EstimatePartialSetSynergy(candidates, profile);
        foreach (var candidate in candidates)
        foreach (var rawAffix in CollectEquipmentAffixes(candidate.Record.ItemData)
                     .Concat(CollectGrantedMasteryAffixes(candidate.Record.ItemData)))
        {
            var affix = ResolveRuntimeAffix(rawAffix);
            var effectType = ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0;
            if (effectType != 3) continue;
            score += ScoreAffixBehavior(affix, candidate.DefinitionId, profile, string.Empty);
        }
        return score;
    }

    private static double ScoreSharedSkillPackage(
        NativeSkillRoleProfile baseRole,
        IEnumerable<NativeSkillRoleProfile> activeRoles,
        HeroFocus focus)
        => ScoreNativeSkillRoleObjective(BuildSharedSkillPackage(baseRole, activeRoles), focus);

    private static NativeSkillRoleProfile BuildSharedSkillPackage(
        NativeSkillRoleProfile baseRole,
        IEnumerable<NativeSkillRoleProfile> activeRoles)
    {
        const double windowSeconds = 60d;
        var active = activeRoles.ToList();
        var demand = active.Sum(role => Math.Max(0d, role.CastOpportunities) * Math.Max(0.01d, role.ActionSecondsPerCast));
        var hpBudget = new[] { baseRole }.Concat(active)
            .Select(role => role.HpBudget60).Where(double.IsFinite).DefaultIfEmpty(double.MaxValue).Min();
        var mpBudget = new[] { baseRole }.Concat(active)
            .Select(role => role.MpBudget60).Where(double.IsFinite).DefaultIfEmpty(double.MaxValue).Min();
        var requestedActiveHp = active.Sum(role => Math.Max(0d, role.CastOpportunities) * role.HpCostPerCast);
        var requestedActiveMp = active.Sum(role => Math.Max(0d, role.CastOpportunities) * role.MpCostPerCast);
        static double BudgetScale(double budget, double requested)
            => requested <= 0.000001d ? 1d : Math.Clamp(budget / requested, 0d, 1d);
        var activeScale = Math.Min(
            demand > windowSeconds ? windowSeconds / demand : 1d,
            Math.Min(BudgetScale(hpBudget, requestedActiveHp), BudgetScale(mpBudget, requestedActiveMp)));
        var remaining = Math.Max(0d, windowSeconds - demand * activeScale);
        var remainingHp = Math.Max(0d, hpBudget - requestedActiveHp * activeScale);
        var remainingMp = Math.Max(0d, mpBudget - requestedActiveMp * activeScale);
        var baseTimeScale = remaining / windowSeconds;
        var requestedBaseHp = Math.Max(0d, baseRole.CastOpportunities) * baseRole.HpCostPerCast * baseTimeScale;
        var requestedBaseMp = Math.Max(0d, baseRole.CastOpportunities) * baseRole.MpCostPerCast * baseTimeScale;
        var baseScale = baseTimeScale * Math.Min(
            BudgetScale(remainingHp, requestedBaseHp), BudgetScale(remainingMp, requestedBaseMp));
        var damage = new Dictionary<int, double>();
        var heal = 0d;
        var shield = 0d;
        var summon = false;
        var summonDamage = 0d;
        var summonSurvival = 0d;
        var abilitySupport = 0d;
        var abilityDefense = 0d;
        var abilityMinion = 0d;
        var complete = baseRole.IsComplete && active.All(role => role.IsComplete);
        var failures = new[] { baseRole }.Concat(active)
            .Where(role => !role.IsComplete && !string.IsNullOrWhiteSpace(role.Failure))
            .Select(role => role.Failure)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value)
            .ToList();

        void AddRole(NativeSkillRoleProfile role, double scale)
        {
            scale *= Math.Clamp(role.Confidence, 0d, 1d);
            foreach (var entry in role.DamageByType)
                damage[entry.Key] = damage.GetValueOrDefault(entry.Key) + entry.Value * scale;
            heal += role.Heal * scale;
            shield += role.Shield * scale;
            summon |= role.Summon && HasProvenNativeMinionSignal(role) && scale > 0d;
            summonDamage += role.SummonDamage * scale;
            summonSurvival += role.SummonSurvival * scale;
            abilitySupport += role.AbilitySupport * scale;
            abilityDefense += role.AbilityDefense * scale;
            abilityMinion += role.AbilityMinion * scale;
        }

        AddRole(baseRole, baseScale);
        foreach (var role in active) AddRole(role, activeScale);
        return new NativeSkillRoleProfile(
            damage, heal, shield, summon, summonDamage, summonSurvival,
            abilitySupport, abilityDefense, abilityMinion, 0d, 1d,
            IsComplete: complete,
            Failure: string.Join("; ", failures),
            // Component lower bounds were already discounted once in AddRole.
            // Keeping the package confidence at one prevents double penalties.
            Confidence: 1d);
    }

    private static bool IsSkillCompatibleWithWeaponTypes(int skillId, IReadOnlySet<int> weaponTypes)
    {
        var row = InvokeStatic("TableData", "getTSkillData", skillId);
        if (row is null) return false;
        var required = ReadSequence(Read(row, "weaponArr")).Select(ToInt).Where(id => id > 0).ToHashSet();
        return required.Count == 0 || required.Overlaps(weaponTypes);
    }

    private static int GetGrantedSkillLevel(IEnumerable<GearCandidate> items, int skillId)
    {
        var key = $"extra-skill:{skillId}";
        return items.SelectMany(candidate => GetGrantedExtraSkillLevels(candidate.Record.ItemData))
            .Where(entry => entry.Key == key)
            .Select(entry => entry.Value)
            .DefaultIfEmpty(0).Max();
    }

    private static string DescribeJointSkillSelection(int baseSkillId, IReadOnlyCollection<int> activeSkillIds, IReadOnlyCollection<GearCandidate> items)
    {
        string SkillLabel(int id)
        {
            var row = InvokeStatic("TableData", "getTSkillData", id);
            var name = ReadString(row, "name") ?? EnglishName(row, string.Empty) ?? string.Empty;
            return string.IsNullOrWhiteSpace(name) ? $"skill#{id}" : $"{Clean(name)}#{id}";
        }

        var selected = activeSkillIds.Append(baseSkillId).ToHashSet();
        var variants = items.SelectMany(candidate => CollectEquipmentAffixes(candidate.Record.ItemData))
            .Select(ResolveRuntimeAffix)
            .Select(affix => Read(affix, "tAffixData"))
            .Where(definition => (ReadNullableInt(definition, "effectType") ?? 0) == 4)
            .Select(GetSkillVariantId)
            .Where(id => selected.Contains(id)).Distinct().OrderBy(id => id).Select(SkillLabel).ToList();
        var sets = new List<string>();
        var effectsBySet = GetSetEffectScoreRows();
        foreach (var group in items.Where(item => item.SetId > 0).GroupBy(item => item.SetId).OrderBy(group => group.Key))
        {
            if (!effectsBySet.TryGetValue(group.Key, out var effects)) continue;
            var activeEffects = effects.Where(effect => effect.Pieces <= group.Count())
                .Select(effect => effect.EffectId).OrderBy(id => id).ToList();
            if (activeEffects.Count > 0) sets.Add($"{group.Key}:{group.Count()}p[{string.Join(',', activeEffects)}]");
        }
        return $"base={SkillLabel(baseSkillId)}; active={string.Join(',', activeSkillIds.Select(SkillLabel))}; variant={(variants.Count == 0 ? "none" : string.Join(',', variants))}; sets={(sets.Count == 0 ? "none" : string.Join(',', sets))}";
    }

    private static bool IsLoadoutWeaponCompatible(IEnumerable<GearCandidate> items, HeroEffectProfile profile)
    {
        var weaponTypes = items.Where(item => item.Part == 1)
            .Select(item => item.WeaponType).Where(value => value > 0).ToHashSet();
        if (profile.BaseWeaponRequirement.Count > 0
            && !profile.BaseWeaponRequirement.Overlaps(weaponTypes)) return false;
        return profile.SkillWeaponPreferences
            .Where(requirement => requirement.Count > 0)
            .All(requirement => requirement.Overlaps(weaponTypes));
    }

    private static bool TryEvaluateFinalPerformance(
        List<GearCandidate> items,
        object hero,
        HeroEffectProfile profile,
        List<object> currentItems,
        out double score,
        object? preparedAttr = null,
        NativeAbilityPackage? preparedAbilities = null)
    {
        score = 0d;
        try
        {
            var simulated = preparedAttr ?? CreateJointLoadoutAttr(hero, items, currentItems, profile.PlannedSkills);
            var nativeAbilities = preparedAbilities
                                  ?? EvaluateNativeAbilityPackage(items, hero, profile, simulated);
            if (!nativeAbilities.IsComplete) return false;
            var effectiveAttr = CreateAbilityAdjustedAttr(simulated, nativeAbilities);
            var candidateItems = items.ToList();
            var plannedSkillLevels = profile.SkillIds.Where(id => id > 0)
                .Append(profile.PreviewBaseSkillId)
                .Where(id => id > 0).Distinct()
                .Select(id => (SkillId: id, Level: GetPlannedSkillEffectiveLevel(
                    hero, profile.PlannedSkills, id, candidateItems)))
                .ToList();
            effectiveAttr = CreateStrictSkillPackageAdjustedAttr(
                hero,
                plannedSkillLevels,
                effectiveAttr,
                false,
                candidateItems,
                out _,
                nativeAbilities.AppliedAbilityCounts);

            // Prefer the same preview path used by the game UI. PowerData resolves
            // the selected base skill's own physical/elemental mix, damage types,
            // attribute coefficients and class scaling against this temporary
            // AttrData. Nothing on the real hero or save is changed.
            var previewRankable = TryEvaluateJointSkillSustainedPerformance(
                effectiveAttr, hero, profile, items, out var sustainedDamage60s, out var skillHeal60s, out var skillShield60s,
                out var jointRole);
            if (!previewRankable) return false;

            double Positive(int attrId) => Math.Max(0d, ReadAttrRequired(effectiveAttr, attrId));
            double Sum(params int[] attrIds) => attrIds.Sum(Positive);
            var hp = Positive(5);
            var defence = Sum(3, 4);
            // GetAttrValue already folds the corresponding up-rate/conversion
            // buckets into the final HP/defence/regen values. Score each final
            // output once; keep avoidance/resistance as separate EHP dimensions.
            var sustain = Sum(7, 91, 92, 93, 94, 220, 222, 224);
            var skillSustain = Math.Max(0d, skillHeal60s + skillShield60s * 1.25d);
            var avoidanceRate = Sum(32, 34, 36, 85, 86);
            var resistance = Sum(61, 62, 63, 64, 65, 66, 130, 131, 132);
            var protectionFlags = new[] { 151, 152, 153, 154, 155, 156, 157, 158, 184, 188, 202 }
                .Count(id => Positive(id) > 0d);
            var defensePenalty = (Positive(200) > 0d ? 1d : 0d) + (Positive(201) > 0d ? 1d : 0d);
            var effectiveSurvival = hp + defence * 2d + sustain * 4d + skillSustain
                                     + jointRole.AbilityDefense + nativeAbilities.Defense
                                     + hp * Math.Clamp(avoidanceRate / 100d, 0d, 4d)
                                     + defence * Math.Clamp(resistance / 100d, 0d, 4d)
                                     + protectionFlags * Math.Max(250d, hp * 0.12d)
                                    - defensePenalty * Math.Max(500d, (hp + defence) * 0.35d);
            effectiveSurvival = Math.Max(0d, effectiveSurvival);
            var support = Math.Max(0d, Sum(81, 82, 83, 84, 91, 92, 93, 94, 95, 96, 185, 191)
                                                + jointRole.AbilitySupport + nativeAbilities.Support);
            var minion = Math.Max(0d, Positive(25) * 50d + Positive(190)
                                               + jointRole.SummonDamage + jointRole.SummonSurvival * 0.35d
                                               + jointRole.AbilityMinion + nativeAbilities.Minion);
            var roleTheme = profile.Focus.Key is "support" or "defense" or "minion";
            var damageScore = Math.Log10(1d + sustainedDamage60s) * (roleTheme ? 280d : 1700d);
            damageScore += Math.Log10(1d + Math.Max(0d, nativeAbilities.OffensiveAttrValue))
                           * (roleTheme ? 120d : 720d);
            var survivalScore = Math.Log10(1d + effectiveSurvival) * (profile.Focus.Key is "defense" ? 1300d : 420d);
            var utilityScore = Math.Log10(1d + support * 10d) * (profile.Focus.Key == "support" ? 1550d : 180d);
            var minionScore = Math.Log10(1d + minion) * (profile.Focus.Key == "minion" ? 1550d : 120d);
            score = damageScore + survivalScore + utilityScore + minionScore;
            return true;
        }
        catch (Exception error)
        {
            if (!performanceSimulationFailureLogged)
            {
                performanceSimulationFailureLogged = true;
                Plugin.DiagWarning($"AUTO-GEAR ATTR SIMULATION FAILED|heuristic fallback is active|{error.GetBaseException().Message}");
            }
            return false;
        }
    }

    private static bool TryEvaluateJointSkillSustainedPerformance(
        object attrData,
        object hero,
        HeroEffectProfile profile,
        IEnumerable<GearCandidate> items,
        out double sustainedDamage60s,
        out double heal60s,
        out double shield60s,
        out NativeSkillRoleProfile jointRole)
    {
        sustainedDamage60s = 0d;
        heal60s = 0d;
        shield60s = 0d;
        jointRole = new NativeSkillRoleProfile(new Dictionary<int, double>(), 0d, 0d, false, 0d, 0d, 0d, 0d, 0d, 0d, 1d);
        try
        {
            var candidates = items.ToList();
            var baseLevel = GetPlannedSkillEffectiveLevel(
                hero, profile.PlannedSkills, profile.PreviewBaseSkillId, candidates);
            var baseRole = ReadNativeSkillRoleProfile(
                hero, profile.PreviewBaseSkillId, baseLevel, attrData, false, candidates, true, false);
            var activeRoles = profile.SkillIds.Where(id => id > 0 && id != profile.PreviewBaseSkillId)
                .Select(id => ReadNativeSkillRoleProfile(
                    hero, id, GetPlannedSkillEffectiveLevel(hero, profile.PlannedSkills, id, candidates),
                    attrData, false, candidates, true, false))
                .ToList();
            var package = BuildSharedSkillPackage(baseRole, activeRoles);
            jointRole = package;
            sustainedDamage60s = Math.Max(0d, GetManualDamageAmount(package.DamageByType, profile.Focus));
            heal60s = Math.Max(0d, package.Heal);
            shield60s = Math.Max(0d, package.Shield);
            // Unknown conditional branches keep a conservative zero lower
            // bound. The preview remains usable only when some independent
            // native damage/heal/shield/summon/utility signal was proven.
            return IsNativeRoleRankable(package, profile.Focus);
        }
        catch (Exception error)
        {
            if (!nativeSkillPreviewFailureLogged)
            {
                nativeSkillPreviewFailureLogged = true;
                Plugin.DiagWarning($"AUTO-GEAR 60S JOINT SKILL PREVIEW FAILED|attribute fallback is active|{error.GetBaseException().Message}");
            }
            return false;
        }
    }

    private static bool TryEvaluateNativeBaseSkillSustainedDamage(object attrData, object hero, HeroEffectProfile profile, IEnumerable<GearCandidate> items, out double sustainedDamage60s, bool includeCurrentEquipmentVariant = false)
    {
        const double windowSeconds = 60d;
        sustainedDamage60s = 0d;
        try
        {
            var actualSkill = InvokeInstance(hero, "GetNowBaseSkillData");
            // When a guide targets a different base skill, compare candidate gear
            // against that target instead of silently previewing the currently
            // equipped (outgoing) base skill with the target skill's tags.
            var skillId = profile.PreviewBaseSkillId > 0
                ? profile.PreviewBaseSkillId
                : ReadNullableInt(Read(actualSkill, "tSkillData"), "id") ?? 0;
            if (skillId <= 0) throw new InvalidOperationException("The active base skill ID is unavailable.");
            var level = Math.Max(1, profile.PreviewBaseSkillLevel > 0
                ? profile.PreviewBaseSkillLevel
                : ReadNullableInt(actualSkill, "level") ?? 1);
            var preview = InvokeRequiredStaticMany("SkillData", "CreatePreview", skillId, level, attrData)
                          ?? throw new InvalidOperationException("SkillData.CreatePreview returned no skill.");

            // The live skill may be variant-enabled by the currently equipped
            // loadout. Do not copy that state into every candidate. Apply only a
            // variant supplied by the candidate loadout being evaluated.
            if (CandidateEnablesSkillVariant(items, skillId)
                || (includeCurrentEquipmentVariant && CurrentEquipmentEnablesSkillVariant(hero, skillId)))
                InvokeRequiredInstance(preview, "SetVariant", true);

            var skillInfo = Read(preview, "tSkillInfoData")
                            ?? throw new InvalidOperationException("The preview skill info is unavailable.");
            var powerIds = new HashSet<int>();
            foreach (var explainId in ReadSequence(Read(skillInfo, "infoArr")).Select(ToInt).Where(id => id > 0))
            {
                var explain = InvokeStatic("TableData", "getTSkillExplainData", explainId);
                if ((ReadNullableInt(explain, "type") ?? -1) != 2) continue;
                var powerId = ReadSequence(Read(explain, "typeParam")).Select(ToInt).FirstOrDefault();
                if (powerId > 0) powerIds.Add(powerId);
            }
            if (powerIds.Count == 0) return false;

            var directDamagePerEvent = 0d;
            var periodicDamagePerEvent = 0d;
            var damageRows = 0;
            foreach (var powerId in powerIds)
            {
                var power = InvokeRequiredStaticMany("PowerData", "CreateByShow", powerId, level, preview)
                            ?? throw new InvalidOperationException($"PowerData.CreateByShow({powerId}) returned no power.");
                if ((ReadNullableInt(Read(power, "tPowerData"), "type") ?? 0) != 1) continue;
                var rowDamage = 0d;
                var typedValues = 0;
                foreach (var entry in ReadEntries(Read(power, "dmgPowerDic")))
                {
                    var value = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(value) || value <= 0d) continue;
                    var damageType = ToInt(Read(entry, "Key") ?? 0);
                    if (damageType is 7 or 8) periodicDamagePerEvent += value;
                    else directDamagePerEvent += value;
                    rowDamage += value;
                    typedValues++;
                }
                if (typedValues == 0)
                {
                    rowDamage = ReadValues(Read(power, "dmgPowerDic"))
                        .Select(value => Convert.ToDouble(value, CultureInfo.InvariantCulture))
                        .Where(value => double.IsFinite(value) && value > 0d)
                        .Sum();
                    if (rowDamage <= 0d)
                        rowDamage = Convert.ToDouble(Read(power, "power") ?? 0d, CultureInfo.InvariantCulture);
                    if (double.IsFinite(rowDamage) && rowDamage > 0d) directDamagePerEvent += rowDamage;
                }
                if (!double.IsFinite(rowDamage) || rowDamage <= 0d) continue;
                damageRows++;
            }
            var nativeDamagePerEvent = directDamagePerEvent + periodicDamagePerEvent;
            if (damageRows == 0 || !double.IsFinite(nativeDamagePerEvent) || nativeDamagePerEvent <= 0d) return false;

            var previewAttr = Read(preview, "attrData") ?? attrData;
            var nativeSpeed = Convert.ToDouble(
                InvokeRequiredInstance(previewAttr, "GetSkillSpeedRate", (object)null!) ?? 1d,
                CultureInfo.InvariantCulture);
            if (!double.IsFinite(nativeSpeed) || nativeSpeed <= 0d) nativeSpeed = 1d;
            nativeSpeed = Math.Clamp(nativeSpeed, 0.05d, 20d);
            var cooldown = Math.Max(0d, ReadAttrRequired(previewAttr, 2001));
            if (ReadAttrRequired(previewAttr, 3001) > 0d) cooldown = 0d;

            // ActionData advances cooldown/action time by the native skill-speed
            // rate. Use the real cooldown when available. For no-cooldown skills,
            // whose animation/resource gate is not exposed safely, assume a
            // conservative one-second base action. This continuous opportunity
            // count avoids ranking cliffs near a 60-second boundary. Speed is
            // applied here once and is not multiplied into damage again.
            var baseInterval = cooldown > 0.01d ? cooldown : 1d;
            var castOpportunities = Math.Clamp(windowSeconds * nativeSpeed / baseInterval, 1d, 6000d);
            if (!double.IsFinite(castOpportunities) || castOpportunities <= 0d) return false;

            var critChance = Math.Clamp(PercentRate(ReadAttrRequired(attrData, 31)), 0d, 1d);
            var critDamage = Math.Max(0.5d, 0.5d + PercentRate(ReadAttrRequired(attrData, 37)));
            var expectedPerEvent = directDamagePerEvent * Math.Max(0.1d, 1d + critChance * critDamage)
                                   + periodicDamagePerEvent;
            // Do not multiply every Power row by emitter/bullet counts here. Some
            // skills already expose repeated components as separate Power rows,
            // and without an exact row-to-projectile link that would double count.
            sustainedDamage60s = expectedPerEvent * castOpportunities;

            if (!sustainedDamageModelLogged)
            {
                sustainedDamageModelLogged = true;
                Plugin.DiagInfo($"AUTO-GEAR 60S ESTIMATE|skill={skillId}|cooldown={cooldown:0.###}|speed={nativeSpeed:0.###}|cast-opportunities={castOpportunities:0.##}|single-target uptime proxy, not exact battle AI");
            }
            return double.IsFinite(sustainedDamage60s) && sustainedDamage60s > 0d;
        }
        catch (Exception error)
        {
            if (!nativeSkillPreviewFailureLogged)
            {
                nativeSkillPreviewFailureLogged = true;
                Plugin.DiagWarning($"AUTO-GEAR 60S SKILL PREVIEW FAILED|attribute fallback is active|{error.GetBaseException().Message}");
            }
            return false;
        }
    }

    private static bool CandidateEnablesSkillVariant(IEnumerable<GearCandidate> items, int skillId)
        => skillId > 0 && items.SelectMany(candidate => GetItemSkillVariantIds(candidate.Record.ItemData))
            .Any(id => id == skillId);

    private static IEnumerable<int> GetItemSkillVariantIds(object item)
        => CollectEquipmentAffixes(item)
            .Select(ResolveRuntimeAffix)
            .Select(affix => Read(affix, "tAffixData"))
            .Where(definition => (ReadNullableInt(definition, "effectType") ?? 0) == 4)
            .Select(GetSkillVariantId)
            .Where(id => id > 0)
            .Distinct();

    private static IEnumerable<int> GetItemNativeAbilityIds(object item)
        => CollectEquipmentAffixes(item)
            .Concat(CollectGrantedMasteryAffixes(item))
            .Select(ResolveRuntimeAffix)
            .Where(affix => (ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0) == 3)
            .Select(affix => ReadNullableInt(Read(affix, "saveData"), "abilityId")
                             ?? ReadNullableInt(Read(affix, "tAbilityData"), "id")
                             ?? ReadSequence(Read(Read(affix, "tAffixData"), "effectParam"))
                                 .Select(ToInt).FirstOrDefault())
            .Where(id => id > 0)
            .Distinct();

    private static bool CurrentEquipmentEnablesSkillVariant(object hero, int skillId)
        => skillId > 0 && GetGearSlots()
            .Select(slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon))
            .Where(item => item is not null).Cast<object>()
            .SelectMany(CollectEquipmentAffixes)
            .Select(ResolveRuntimeAffix)
            .Select(affix => Read(affix, "tAffixData"))
            .Where(definition => (ReadNullableInt(definition, "effectType") ?? 0) == 4)
            .Select(GetSkillVariantId)
            .Any(id => id == skillId);

    // Native AffixData.SetActiveHeroSkillVariant reads effectParam[0] only.
    // Later parameters can describe the effect but are not additional skills.
    private static int GetSkillVariantId(object? affixDefinition)
        => ReadSequence(Read(affixDefinition, "effectParam")).Select(ToInt).FirstOrDefault();

    private static void ApplyEquipmentToAttr(object attrData, object item, bool active)
    {
        var equip = Read(item, "itemEquipData");
        var equipAttr = Read(equip, "equipAttrData") ?? throw new InvalidOperationException("Equipment AttrData is unavailable.");
        var sign = active ? 1d : -1d;
        foreach (var mapping in GetEquipAttrMappings())
        {
            var equipType = CreateEnum("EEquipAttrType", mapping.EquipType)
                            ?? throw new InvalidOperationException($"Unknown equipment attribute type {mapping.EquipType}.");
            var value = Convert.ToDouble(InvokeRequiredInstance(equipAttr, "GetAttrValue", equipType, null!) ?? 0d, CultureInfo.InvariantCulture);
            if (Math.Abs(value) < double.Epsilon) continue;
            var attrEnum = CreateEnum("EAttrType", mapping.BattleAttrType)
                           ?? throw new InvalidOperationException($"Unknown battle attribute type {mapping.BattleAttrType}.");
            InvokeRequiredInstance(attrData, "ChangeAttr", attrEnum, (float)(value * sign));
        }
        foreach (var affix in CollectEquipmentAffixes(item).Concat(CollectGrantedMasteryAffixes(item)))
            InvokeRequiredInstance(ResolveRuntimeAffix(affix), "SetActiveAttrData", attrData, active);
    }

    private static List<EquipAttrMapping> GetEquipAttrMappings()
    {
        if (equipAttrMappings is not null) return equipAttrMappings;
        var mappings = ReadValues(ReadStatic("TableData", "TEquipAttrDict"))
            .Select(row => new EquipAttrMapping(ReadNullableInt(row, "id") ?? 0, ReadNullableInt(row, "battleAttrId") ?? 0))
            .Where(mapping => mapping.EquipType > 0 && mapping.BattleAttrType > 0)
            .Distinct().ToList();
        if (mappings.Count == 0) throw new InvalidOperationException("TEquipAttr mappings are unavailable.");
        equipAttrMappings = mappings;
        return mappings;
    }

    private static double ReadAttrRequired(object attrData, int id)
    {
        var attrType = CreateEnum("EAttrType", id) ?? throw new InvalidOperationException($"Unknown battle attribute type {id}.");
        return Convert.ToDouble(InvokeRequiredInstance(attrData, "GetAttrValue", attrType) ?? 0d, CultureInfo.InvariantCulture);
    }

    private static int ReadIntAttrRequired(object attrData, int id)
    {
        var attrType = CreateEnum("EAttrType", id) ?? throw new InvalidOperationException($"Unknown battle attribute type {id}.");
        return Convert.ToInt32(InvokeRequiredInstance(attrData, "GetIntAttrValue", attrType) ?? 0, CultureInfo.InvariantCulture);
    }

    private static (double Regular, double Extra) GetDamageTypeBuckets(object attrData, string focusKey)
    {
        var physical = new[] { (Regular: 51, Extra: 110), (Regular: 52, Extra: 111), (Regular: 53, Extra: 112) };
        var elemental = new[] { (Regular: 54, Extra: 113), (Regular: 55, Extra: 114), (Regular: 56, Extra: 115) };
        var pairs = focusKey switch
        {
            "fire" => new[] { elemental[0] },
            "ice" => new[] { elemental[1] },
            "lightning" => new[] { elemental[2] },
            "bleed" => new[] { (Regular: 121, Extra: 0) },
            "corrosion" => new[] { (Regular: 122, Extra: 0) },
            "physical" => physical,
            "elemental" => elemental,
            _ => physical.Concat(elemental).ToArray()
        };
        return pairs.Select(pair => (
                Regular: ReadAttrRequired(attrData, pair.Regular),
                Extra: pair.Extra > 0 ? ReadAttrRequired(attrData, pair.Extra) : 0d))
            .OrderByDescending(pair => NativeRateFactor(pair.Regular) * NativeRateFactor(pair.Extra))
            .FirstOrDefault();
    }

    private static double PercentRate(double value) => value / 100d;
    private static double NativeRateFactor(double value) => value >= 0d ? (100d + value) / 100d : 100d / (100d - value);

    private static List<object> CollectEquipmentAffixes(object item)
    {
        var result = new List<object>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var equip = Read(item, "itemEquipData");
        var save = Read(item, "saveItemData");
        var runtimeAffixes = ReadList(Read(equip, "affixList")).ToList();
        AddMany(runtimeAffixes.Count > 0 ? runtimeAffixes : Read(save, "affixList"));
        var runtimeRunes = ReadList(Read(equip, "slotRuneList")).ToList();
        foreach (var rune in runtimeRunes.Count > 0 ? runtimeRunes : ReadList(Read(save, "slotRuneList")))
            AddMany(Read(rune, "affixList"));
        Add(Read(equip, "runewordsAffixData") ?? Read(save, "runewordsAffixData"));
        return result;

        void AddMany(object? values)
        {
            foreach (var value in ReadList(values)) Add(value);
        }

        void Add(object? value)
        {
            if (value is null) return;
            var pointer = Read(value, "Pointer");
            var key = pointer is null
                ? $"ref:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)}"
                : $"ptr:{pointer}";
            if (keys.Add(key)) result.Add(value);
        }
    }

    private static object? GetRunewordAffix(object item)
    {
        var equip = Read(item, "itemEquipData");
        var save = Read(item, "saveItemData");
        return Read(equip, "runewordsAffixData") ?? Read(save, "runewordsAffixData");
    }

    private static IEnumerable<object> CollectRunewordAffixes(object item)
    {
        if (GetRunewordAffix(item) is { } affix) yield return affix;
    }

    private static IEnumerable<object> CollectGrantedMasteryAffixes(object item)
    {
        // effectType 100 calls HeroTalentData.AddExtraTalent. When that talent
        // is a mastery, native TalentData.Init creates MasteryData at exactly
        // saveData.talentLevel and immediately applies its affixes. Build the
        // game's display-only mastery object to obtain the same scaled affixes
        // without touching the live hero or save.
        foreach (var outer in CollectRunewordAffixes(item).Select(ResolveRuntimeAffix))
        {
            var definition = Read(outer, "tAffixData");
            if ((ReadNullableInt(definition, "effectType") ?? 0) != 100) continue;
            var save = Read(outer, "saveData");
            var talent = Read(outer, "tTalentData")
                         ?? InvokeStatic("TableData", "getTTalentData", ReadNullableInt(save, "talentId") ?? 0);
            var masteryId = ReadNullableInt(talent, "masteryId") ?? 0;
            var talentLevel = ReadNullableInt(save, "talentLevel") ?? 0;
            if (masteryId <= 0 || talentLevel <= 0) continue;
            var preview = InvokeRequiredStaticMany("MasteryData", "CreateByShow", masteryId, talentLevel, talentLevel)
                          ?? throw new InvalidOperationException($"Mastery preview {masteryId} Lv{talentLevel} could not be created.");
            foreach (var affix in ReadList(Read(preview, "affixList")))
            {
                // Native MasteryData.GetMasteryEffect applies only bodyAttr
                // and getAbility affixes. It does not execute nested
                // skillVariant/extraTalent rows even if a future table contains
                // them, so keep the preview contract equally narrow.
                var effectType = ReadNullableInt(Read(ResolveRuntimeAffix(affix), "tAffixData"), "effectType") ?? 0;
                if (effectType is 1 or 3) yield return affix;
            }
        }
    }

    private static HashSet<string> GetNonStackingEffectKeys(object item)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var equip = Read(item, "itemEquipData");
        var equipDefinition = Read(equip, "tEquipData");
        var itemSave = Read(item, "saveItemData");
        var quality = ReadNullableInt(itemSave, "quality") ?? ReadNullableInt(equipDefinition, "quality") ?? 0;
        var uniqueSkillAffix = ReadNullableInt(equipDefinition, "uniqueSkillAffix") ?? 0;
        // Quality-8 Uniques can expose the same named special effect on two
        // physical copies. Native appends both AbilityData instances, but the
        // eventual buff/status may still merge or cap per ability. The user
        // explicitly prefers a different effect over a duplicate Unique, so
        // keep this policy narrow to the Unique's declared special affix rather
        // than incorrectly treating every repeated ability as non-stacking.
        if (quality == 8 && uniqueSkillAffix > 0)
            result.Add($"unique-effect:{uniqueSkillAffix}");

        var runewordAffix = GetRunewordAffix(item) is { } rawRuneword ? ResolveRuntimeAffix(rawRuneword) : null;
        foreach (var affix in CollectEquipmentAffixes(item).Concat(CollectGrantedMasteryAffixes(item)).Select(ResolveRuntimeAffix))
        {
            var definition = Read(affix, "tAffixData");
            var save = Read(affix, "saveData");
            var effectType = ReadNullableInt(definition, "effectType") ?? 0;
            var affixId = ReadNullableInt(definition, "id") ?? ReadNullableInt(save, "id") ?? 0;

            switch (effectType)
            {
                case 4: // skillVariant: enabling the same variant twice is still one boolean state.
                {
                    var skillId = GetSkillVariantId(definition);
                    if (skillId > 0) result.Add($"skill-variant:{skillId}");
                    else if (affixId > 0) result.Add($"skill-variant-affix:{affixId}");
                    break;
                }
                case 100:
                {
                    if (!NativeEquals(affix, runewordAffix)) break;
                    // Extra masteries are independently applied and may stack.
                    // Extra skills are different: GetSkillList deduplicates by
                    // the real TSkill id and keeps only the highest level.
                    var talent = Read(affix, "tTalentData")
                                 ?? InvokeStatic("TableData", "getTTalentData", ReadNullableInt(save, "talentId") ?? 0);
                    var talentType = ReadNullableInt(talent, "type") ?? 0;
                    var skillId = ReadNullableInt(talent, "skillId") ?? 0;
                    if (talentType == 1 && skillId > 0) result.Add($"extra-skill:{skillId}");
                    break;
                }
            }
        }
        return result;
    }

    private static bool IsHardExclusiveEffectKey(string key)
        => key.StartsWith("unique-effect:", StringComparison.Ordinal);

    private static Dictionary<string, int> GetGrantedExtraSkillLevels(object item)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var affix in CollectRunewordAffixes(item).Select(ResolveRuntimeAffix))
        {
            var definition = Read(affix, "tAffixData");
            if ((ReadNullableInt(definition, "effectType") ?? 0) != 100) continue;
            var save = Read(affix, "saveData");
            var talent = Read(affix, "tTalentData")
                         ?? InvokeStatic("TableData", "getTTalentData", ReadNullableInt(save, "talentId") ?? 0);
            if ((ReadNullableInt(talent, "type") ?? 0) != 1) continue;
            var skillId = ReadNullableInt(talent, "skillId") ?? 0;
            if (skillId <= 0) continue;
            var key = $"extra-skill:{skillId}";
            var level = Math.Max(0, ReadNullableInt(save, "talentLevel") ?? 0);
            result[key] = Math.Max(result.GetValueOrDefault(key), level);
        }
        return result;
    }

    private static IEnumerable<DeduplicatedEffectContribution> GetDeduplicatedEffectContributions(object item, HeroEffectProfile profile)
    {
        var equipId = ReadNullableInt(Read(Read(item, "itemEquipData"), "tEquipData"), "id") ?? 0;
        var runewordAffix = GetRunewordAffix(item) is { } rawRuneword ? ResolveRuntimeAffix(rawRuneword) : null;
        foreach (var affix in CollectEquipmentAffixes(item).Concat(CollectGrantedMasteryAffixes(item)).Select(ResolveRuntimeAffix))
        {
            var definition = Read(affix, "tAffixData");
            var save = Read(affix, "saveData");
            var effectType = ReadNullableInt(definition, "effectType") ?? 0;
            string? key = null;
            var level = 1;
            if (effectType == 4)
            {
                var skillId = GetSkillVariantId(definition);
                var affixId = ReadNullableInt(definition, "id") ?? ReadNullableInt(save, "id") ?? 0;
                key = skillId > 0 ? $"skill-variant:{skillId}"
                    : affixId > 0 ? $"skill-variant-affix:{affixId}" : null;
            }
            else if (effectType == 100)
            {
                if (!NativeEquals(affix, runewordAffix)) continue;
                var talent = Read(affix, "tTalentData")
                             ?? InvokeStatic("TableData", "getTTalentData", ReadNullableInt(save, "talentId") ?? 0);
                if ((ReadNullableInt(talent, "type") ?? 0) != 1) continue;
                var skillId = ReadNullableInt(talent, "skillId") ?? 0;
                if (skillId > 0) key = $"extra-skill:{skillId}";
                level = Math.Max(0, ReadNullableInt(save, "talentLevel") ?? 0);
            }
            if (key is null) continue;

            var direct = CountDirectAffixMatches(affix, profile);
            var behavior = ScoreAffixBehavior(affix, equipId, profile, GetAffixSearchText(affix));
            var value = Math.Max(0d, behavior);
            if (value > 0d || direct > 0)
                yield return new DeduplicatedEffectContribution(key, level, value, direct);
        }
    }

    private static IEnumerable<DeduplicatedEffectContribution> SelectEffectiveDeduplicatedEffects(
        IEnumerable<DeduplicatedEffectContribution> contributions)
        => contributions.GroupBy(entry => entry.Key, StringComparer.Ordinal).Select(group =>
            group.Key.StartsWith("extra-skill:", StringComparison.Ordinal)
                ? group.OrderByDescending(entry => entry.Level).ThenByDescending(entry => entry.Value).First()
                : group.OrderByDescending(entry => entry.Value).First());

    private static double ScoreSingleItemWithDeduplicatedEffects(object item, HeroEffectProfile profile, double rawScore)
    {
        var contributions = GetDeduplicatedEffectContributions(item, profile).ToList();
        if (contributions.Count == 0) return rawScore;
        return rawScore - contributions.Sum(entry => entry.Value)
                        + SelectEffectiveDeduplicatedEffects(contributions).Sum(entry => entry.Value);
    }

    private static double ScoreItemsWithDeduplicatedEffects(IReadOnlyCollection<GearCandidate> items, HeroEffectProfile profile)
    {
        var raw = items.Sum(item => item.Score);
        var perItemEffective = items.SelectMany(item => SelectEffectiveDeduplicatedEffects(
            GetDeduplicatedEffectContributions(item.Record.ItemData, profile).ToList())).ToList();
        if (perItemEffective.Count == 0) return raw;
        return raw - perItemEffective.Sum(entry => entry.Value)
                   + SelectEffectiveDeduplicatedEffects(perItemEffective).Sum(entry => entry.Value);
    }

    private static int CountEffectiveDirectMatches(IReadOnlyCollection<GearCandidate> items, HeroEffectProfile profile)
    {
        var raw = items.Sum(item => item.DirectMatches);
        var contributions = items.SelectMany(item => GetDeduplicatedEffectContributions(item.Record.ItemData, profile)).ToList();
        return Math.Max(0, raw - contributions.Sum(entry => entry.DirectMatches)
                           + SelectEffectiveDeduplicatedEffects(contributions).Sum(entry => entry.DirectMatches));
    }

    private static string GetNonStackingEffectSignature(IEnumerable<string> keys)
    {
        var values = keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? "none" : string.Join("+", values);
    }

    private static HashSet<string> UnionEffectKeys(IEnumerable<string> left, IEnumerable<string> right)
    {
        var result = new HashSet<string>(left, StringComparer.Ordinal);
        result.UnionWith(right);
        return result;
    }

    private static object ResolveRuntimeAffix(object affix)
    {
        if (Read(affix, "tAffixData") is not null) return affix;
        var save = Read(affix, "saveData") ?? affix;
        return InvokeStaticMany("AffixData", "Create", save, true, -1) ?? affix;
    }

    private static string GetAffixSearchText(object affix)
    {
        var definition = Read(affix, "tAffixData");
        var ability = Read(affix, "tAbilityData");
        var talent = Read(affix, "tTalentData");
        var skill = ReadNullableInt(talent, "skillId") is > 0 and var skillId ? InvokeStatic("TableData", "getTSkillData", skillId) : null;
        var mastery = ReadNullableInt(talent, "masteryId") is > 0 and var masteryId ? InvokeStatic("TableData", "getTMasteryData", masteryId) : null;
        return Clean(string.Join(" ",
            InvokeString(affix, "GetDesc") ?? string.Empty,
            ReadString(definition, "des") ?? string.Empty, EnglishText(definition, "_des", string.Empty) ?? string.Empty,
            ReadString(ability, "name") ?? string.Empty, EnglishName(ability, string.Empty) ?? string.Empty, EnglishText(ability, "_des", string.Empty) ?? string.Empty,
            ReadString(talent, "name") ?? string.Empty, EnglishName(talent, string.Empty) ?? string.Empty,
            ReadString(skill, "name") ?? string.Empty, EnglishName(skill, string.Empty) ?? string.Empty,
            ReadString(mastery, "name") ?? string.Empty, EnglishName(mastery, string.Empty) ?? string.Empty));
    }

    private static int CountDirectAffixMatches(object affix, HeroEffectProfile profile)
    {
        var matched = false;
        var jobId = ReadNullableInt(Read(affix, "tHeroJobData"), "id") ?? 0;
        if (jobId > 0 && jobId != profile.JobId) return 0;
        var talent = Read(affix, "tTalentData");
        var talentId = ReadNullableInt(talent, "id") ?? 0;
        var skillId = ReadNullableInt(talent, "skillId") ?? 0;
        var masteryId = ReadNullableInt(talent, "masteryId") ?? 0;
        if ((talentId > 0 && profile.TalentIds.Contains(talentId))
            || (skillId > 0 && profile.SkillIds.Contains(skillId))
            || (masteryId > 0 && profile.MasteryIds.Contains(masteryId))) matched = true;
        var definition = Read(affix, "tAffixData");
        if ((ReadNullableInt(definition, "effectType") ?? 0) == 4)
        {
            var variantSkillId = GetSkillVariantId(definition);
            if (profile.SkillIds.Contains(variantSkillId)) matched = true;
        }
        return matched ? 1 : 0;
    }

    private static SkillVariantVerification VerifyEquippedSkillVariants(
        object hero,
        object talentData,
        string scope,
        IReadOnlySet<int>? requiredSkillIds = null)
    {
        var skills = ReadList(InvokeRequiredInstance(talentData, "GetSkillList")).ToList();
        var available = skills.Select(skill => ReadNullableInt(Read(skill, "tSkillData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var actual = skills.Where(skill => ReadBool(Read(skill, "isVariant")))
            .Select(skill => ReadNullableInt(Read(skill, "tSkillData"), "id") ?? 0)
            .Where(id => id > 0).Distinct().ToList();
        var equippedTargets = GetGearSlots().Select(slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon))
            .Where(item => item is not null).Cast<object>()
            .SelectMany(CollectEquipmentAffixes)
            .Select(ResolveRuntimeAffix)
            .Select(affix => Read(affix, "tAffixData"))
            .Where(definition => (ReadNullableInt(definition, "effectType") ?? 0) == 4)
            .Select(GetSkillVariantId)
            .Where(id => id > 0).Distinct().ToHashSet();
        // Gear verification can validate only variants for skills already present.
        // Auto Skills additionally supplies its complete joint target set, so a
        // selected variant item whose target skill failed to transform/learn is
        // reported as missing instead of disappearing from the expected set.
        var expected = equippedTargets
            .Where(id => available.Contains(id) || (requiredSkillIds?.Contains(id) ?? false))
            .ToHashSet();
        var missing = expected.Where(id => !actual.Contains(id)).ToList();
        var unexpected = actual.Where(id => !equippedTargets.Contains(id)).ToList();
        Plugin.DiagInfo($"{scope} VARIANTS|expected={string.Join(',', expected)}|actual={string.Join(',', actual)}|missing={string.Join(',', missing)}|unexpected={string.Join(',', unexpected)}");
        if (missing.Count > 0 || unexpected.Count > 0)
            Plugin.DiagWarning($"{scope} VARIANT MISMATCH|missing={string.Join(',', missing)}|unexpected={string.Join(',', unexpected)}");
        return new SkillVariantVerification(expected.OrderBy(id => id).ToList(), actual.OrderBy(id => id).ToList(), missing.OrderBy(id => id).ToList(), unexpected.OrderBy(id => id).ToList());
    }

    private static Dictionary<int, int> GetHeroAbilityCounts(object hero)
        => ReadList(Read(hero, "abilityList"))
            .Select(ability => ReadNullableInt(Read(ability, "tAbilityData"), "id")
                               ?? ReadNullableInt(ability, "id") ?? 0)
            .Where(id => id > 0)
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

    private static Dictionary<int, int> GetExpectedGearAbilityCounts(IEnumerable<object> sourceItems)
    {
        var items = sourceItems.ToList();
        var abilityIds = items
            .SelectMany(item => CollectEquipmentAffixes(item).Concat(CollectGrantedMasteryAffixes(item)))
            .Select(ResolveRuntimeAffix)
            .Where(affix => (ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0) == 3)
            .Select(affix => ReadNullableInt(Read(affix, "tAbilityData"), "id")
                             ?? ReadNullableInt(Read(affix, "saveData"), "abilityId") ?? 0)
            .Where(id => id > 0).ToList();
        foreach (var group in items.Select(item =>
                 {
                     var equip = Read(item, "itemEquipData");
                     return ReadNullableInt(Read(equip, "tEquipSetsData"), "id")
                            ?? ReadNullableInt(Read(equip, "tEquipData"), "setsId") ?? 0;
                 })
                 .Where(id => id > 0).GroupBy(id => id))
        {
            if (!GetSetEffectScoreRows().TryGetValue(group.Key, out var effects)) continue;
            abilityIds.AddRange(effects.Where(effect => effect.Pieces <= group.Count())
                .Select(effect => effect.AbilityId).Where(id => id > 0));
        }
        return abilityIds.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
    }

    private static GearAbilityBaseline CaptureGearAbilityBaseline(object hero, IEnumerable<object> currentItems)
    {
        var actual = GetHeroAbilityCounts(hero);
        var expectedGear = GetExpectedGearAbilityCounts(currentItems);
        var nonGear = new Dictionary<int, int>();
        foreach (var id in actual.Keys.Union(expectedGear.Keys))
        {
            var actualCount = actual.GetValueOrDefault(id);
            var gearCount = expectedGear.GetValueOrDefault(id);
            if (actualCount < gearCount)
                throw new InvalidOperationException($"Existing gear ability state is stale for {id} (hero {actualCount}, gear expected {gearCount}); no equipment was changed.");
            var baselineCount = actualCount - gearCount;
            if (baselineCount > 0) nonGear[id] = baselineCount;
        }
        Plugin.DiagInfo($"AUTO-GEAR ABILITY BASELINE|hero={actual.Values.Sum()}|oldGear={expectedGear.Values.Sum()}|nonGear={nonGear.Values.Sum()}");
        return new GearAbilityBaseline(nonGear);
    }

    private static GearEffectVerification VerifyCommittedGearEffects(object hero, object talentData, GearAbilityBaseline abilityBaseline, string scope)
    {
        var items = GetGearSlots().Select(slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon))
            .Where(item => item is not null).Cast<object>().ToList();
        var expectedGearAbilityCounts = GetExpectedGearAbilityCounts(items);

        var expectedSetEffectIds = new List<int>();
        foreach (var group in items.Select(item =>
                 {
                     var equip = Read(item, "itemEquipData");
                     return ReadNullableInt(Read(equip, "tEquipSetsData"), "id")
                            ?? ReadNullableInt(Read(equip, "tEquipData"), "setsId") ?? 0;
                 })
                 .Where(id => id > 0).GroupBy(id => id))
        {
            if (!GetSetEffectScoreRows().TryGetValue(group.Key, out var effects)) continue;
            var active = effects.Where(effect => effect.Pieces <= group.Count()).ToList();
            expectedSetEffectIds.AddRange(active.Select(effect => effect.EffectId));
        }

        var expectedAbilityCounts = new Dictionary<int, int>(abilityBaseline.NonGearCounts);
        foreach (var (id, count) in expectedGearAbilityCounts)
            expectedAbilityCounts[id] = expectedAbilityCounts.GetValueOrDefault(id) + count;
        var actualAbilityCounts = GetHeroAbilityCounts(hero);
        var abilityRows = expectedAbilityCounts.Keys.Union(actualAbilityCounts.Keys)
            .Select(id => (Id: id, Expected: expectedAbilityCounts.GetValueOrDefault(id), Actual: actualAbilityCounts.GetValueOrDefault(id)))
            .ToList();
        var missingAbilityRows = abilityRows
            .Where(entry => entry.Actual < entry.Expected)
            .ToList();
        var unexpectedAbilityRows = abilityRows
            .Where(entry => entry.Actual > entry.Expected)
            .ToList();
        var missingAbilities = missingAbilityRows
            .Select(entry => $"{entry.Id}:{entry.Actual}/{entry.Expected}").ToList();
        var unexpectedAbilities = unexpectedAbilityRows
            .Select(entry => $"{entry.Id}:{entry.Actual}/{entry.Expected}").ToList();

        var expectedExtraTalents = items.SelectMany(CollectRunewordAffixes)
            .Select(ResolveRuntimeAffix)
            .Where(affix => (ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0) == 100)
            .Select(affix =>
            {
                var save = Read(affix, "saveData");
                var talent = Read(affix, "tTalentData")
                             ?? InvokeStatic("TableData", "getTTalentData", ReadNullableInt(save, "talentId") ?? 0);
                return (Id: ReadNullableInt(talent, "id") ?? ReadNullableInt(save, "talentId") ?? 0,
                    Level: Math.Max(0, ReadNullableInt(save, "talentLevel") ?? 0));
            })
            .Where(entry => entry.Id > 0).ToList();
        var actualExtraTalents = ReadList(Read(talentData, "extraTalentList"))
            .Where(talent => ReadBool(Read(talent, "isRuneWords")))
            .Select(talent => (Id: ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0,
                Level: GetSavedTalentLevel(talent)))
            .Where(entry => entry.Id > 0).ToList();
        var remainingActualExtra = actualExtraTalents.ToList();
        var missingExtraTalents = new List<string>();
        foreach (var expected in expectedExtraTalents.OrderByDescending(entry => entry.Level))
        {
            var index = remainingActualExtra.FindIndex(actual => actual.Id == expected.Id && actual.Level == expected.Level);
            if (index >= 0) remainingActualExtra.RemoveAt(index);
            else missingExtraTalents.Add($"{expected.Id}@{expected.Level}");
        }
        var unexpectedExtraTalents = remainingActualExtra.Select(entry => $"{entry.Id}@{entry.Level}").ToList();

        var actualSetEffectIds = ReadList(Read(Read(hero, "heroEquipData"), "activeSetsEffectList"))
            .Select(effect => ReadNullableInt(Read(effect, "tSetEffectData"), "id") ?? 0)
            .Where(id => id > 0).ToList();
        var missingSetEffects = expectedSetEffectIds.Where(id => !actualSetEffectIds.Contains(id)).Distinct().ToList();
        var unexpectedSetEffects = actualSetEffectIds.Where(id => !expectedSetEffectIds.Contains(id)).Distinct().ToList();
        Plugin.DiagInfo($"{scope} EFFECTS|abilityExact={actualAbilityCounts.Values.Sum()}/{expectedAbilityCounts.Values.Sum()}|newGearAbilities={expectedGearAbilityCounts.Values.Sum()}|extraTalents={expectedExtraTalents.Count - missingExtraTalents.Count}/{expectedExtraTalents.Count}|setEffects={expectedSetEffectIds.Count - missingSetEffects.Count}/{expectedSetEffectIds.Count}|missingAbilities={string.Join(',', missingAbilities)}|unexpectedAbilities={string.Join(',', unexpectedAbilities)}|missingExtra={string.Join(',', missingExtraTalents)}|unexpectedExtra={string.Join(',', unexpectedExtraTalents)}|missingSets={string.Join(',', missingSetEffects)}|unexpectedSets={string.Join(',', unexpectedSetEffects)}");
        if (missingAbilities.Count > 0 || unexpectedAbilities.Count > 0 || missingExtraTalents.Count > 0 || unexpectedExtraTalents.Count > 0
            || missingSetEffects.Count > 0 || unexpectedSetEffects.Count > 0)
            Plugin.DiagWarning($"{scope} EFFECT VERIFICATION FAILED|missingAbilities={string.Join(',', missingAbilities)}|unexpectedAbilities={string.Join(',', unexpectedAbilities)}|missingExtra={string.Join(',', missingExtraTalents)}|unexpectedExtra={string.Join(',', unexpectedExtraTalents)}|missingSets={string.Join(',', missingSetEffects)}|unexpectedSets={string.Join(',', unexpectedSetEffects)}");
        return new GearEffectVerification(missingAbilities, unexpectedAbilities, missingExtraTalents, unexpectedExtraTalents, missingSetEffects, unexpectedSetEffects);
    }

    private static object? GetEquippedItem(object hero, int part, bool main)
    {
        var partType = CreateEnum("EEquipPart", part);
        return partType is null ? null : InvokeInstance(hero, "GetEquipByPart", partType, main);
    }

    private static bool IsVerifiedHeroEquipField(object hero, object item, object destination)
        => NativeEquals(Read(destination, "itemData"), item)
           && NativeEquals(InvokeStatic("HeroEquipData", "GetHeroDataByField", destination), hero)
           && NativeEquals(Read(item, "ownerHeroData"), hero)
           && NativeEquals(InvokeStatic("ItemSys", "FindHeroEquipFieldByItem", item), destination);

    private static bool TryEquipCandidate(object hero, GearCandidate candidate, GearSlot slot, object? seasonData, List<MoveReceipt> moveJournal, IReadOnlyCollection<object> targetItems, IReadOnlyCollection<object> treasureTargetItems, out int moveCode, out string failure)
    {
        moveCode = int.MinValue;
        failure = string.Empty;
        var item = candidate.Record.ItemData;
        var destination = GetEquipDestinationField(hero, slot);
        if (destination is null)
        {
            failure = "target equip field was not found";
            return false;
        }
        if (NativeEquals(Read(destination, "itemData"), item))
        {
            moveCode = 0;
            if (IsVerifiedHeroEquipField(hero, item, destination)) return true;
            failure = "the item pointer is present, but hero ownership verification failed";
            return false;
        }

        var source = InvokeStatic("ItemSys", "FindHeroEquipFieldByItem", item)
                     ?? InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item);
        source ??= FindTrackedItemField(item, moveJournal);
        if (source is null && candidate.Record.StorageSource == StorageSource.Warehouse
            && candidate.Record.SourceField is not null
            && NativeEquals(Read(candidate.Record.SourceField, "itemData"), item))
        {
            // Store fields can be swapped directly with an exact hero field. This
            // preserves the native store/equip events and does not require a free
            // bag slot for every warehouse candidate.
            source = candidate.Record.SourceField;
        }
        if (source is null)
        {
            if (!TryMoveCandidateToBag(candidate.Record, seasonData, moveJournal))
            {
                failure = "item could not be moved from storage or Vault to the bag";
                return false;
            }
            source = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item);
        }
        if (source is null)
        {
            failure = "source field was not found after moving the item to the bag";
            return false;
        }
        if (NativeEquals(source, destination))
        {
            moveCode = 0;
            if (IsVerifiedHeroEquipField(hero, item, destination)) return true;
            failure = "the source and destination match, but hero ownership verification failed";
            return false;
        }
        var sourceWasBag = NativeEquals(InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item), source);

        var heroEquipData = Read(hero, "heroEquipData") ?? throw new InvalidOperationException("Hero equipment data is unavailable.");
        var validation = ToInt(InvokeRequiredInstance(heroEquipData, "CheckAddItem", source, destination));
        if (validation is not (0 or -2))
        {
            moveCode = validation;
            failure = MoveItemFailure(validation);
            return false;
        }

        var beforeFrom = Read(source, "itemData");
        var beforeTo = Read(destination, "itemData");
        if (!NativeEquals(beforeFrom, item))
        {
            failure = "the source field changed before the move could be applied";
            return false;
        }
        var rawResult = InvokeRequiredStaticMany("ItemSys", "MoveItem", source, destination);
        moveCode = rawResult is null ? int.MinValue : ToInt(rawResult);
        var destinationMatches = NativeEquals(Read(destination, "itemData"), item);
        var sourceMatches = NativeStateEquals(Read(source, "itemData"), beforeTo);
        if (destinationMatches && sourceMatches)
            moveJournal.Add(new MoveReceipt(MoveReceiptKind.FieldMove, source, destination, beforeFrom!, beforeTo));
        if (destinationMatches && sourceMatches && IsVerifiedHeroEquipField(hero, item, destination))
        {
            var needsTreasureBagSpace = treasureTargetItems.Any(target => InvokeStatic("ItemSys", "FindHeroEquipFieldByItem", target) is null
                && InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", target) is null);
            if (sourceWasBag && beforeTo is not null && needsTreasureBagSpace
                && InvokeStaticMany("ItemSys", "FindEmptyLordInventoryField") is null
                && !targetItems.Any(target => NativeEquals(target, beforeTo))
                && !TryClearBridgeBagField(source, beforeTo, seasonData, moveJournal, out failure))
            {
                return false;
            }
            return true;
        }

        failure = destinationMatches && !sourceMatches
            ? "the destination changed, but the source swap did not match"
            : destinationMatches
            ? "the destination changed, but hero ownership verification failed"
            : MoveItemFailure(moveCode);
        return false;
    }

    private static object? FindTrackedItemField(object item, List<MoveReceipt> moveJournal)
    {
        for (var index = moveJournal.Count - 1; index >= 0; index--)
        {
            var receipt = moveJournal[index];
            if (receipt.Kind != MoveReceiptKind.FieldMove || receipt.FromField is null || receipt.ToField is null) continue;
            if (NativeEquals(Read(receipt.FromField, "itemData"), item)) return receipt.FromField;
            if (NativeEquals(Read(receipt.ToField, "itemData"), item)) return receipt.ToField;
        }
        return null;
    }

    private static bool TryClearBridgeBagField(object bagField, object outgoingItem, object? seasonData, List<MoveReceipt> moveJournal, out string failure)
    {
        failure = string.Empty;
        if (!NativeEquals(Read(bagField, "itemData"), outgoingItem))
        {
            failure = "the bridge bag field no longer contains the outgoing equipment";
            return false;
        }
        var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
            .Select(house => Read(house, "houseStoreData")).FirstOrDefault(store => Read(store, "storeBaseData") is not null);
        var storeBaseData = Read(houseStoreData, "storeBaseData");
        var emptyStoreField = storeBaseData is null ? null : InvokeRequiredInstance(storeBaseData, "QuickPutItemToField", outgoingItem);
        if (emptyStoreField is null || Read(emptyStoreField, "itemData") is not null)
        {
            failure = "no empty warehouse field is available for reversible bag staging";
            return false;
        }
        var moveCode = ToInt(InvokeRequiredStaticMany("ItemSys", "MoveItem", bagField, emptyStoreField));
        if (Read(bagField, "itemData") is null && NativeEquals(Read(emptyStoreField, "itemData"), outgoingItem))
        {
            moveJournal.Add(new MoveReceipt(MoveReceiptKind.FieldMove, bagField, emptyStoreField, outgoingItem, null));
            return true;
        }
        failure = MoveItemFailure(moveCode);
        return false;
    }

    private static bool IsVaultRoutedItem(object item, object? seasonData)
    {
        var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
            .Select(house => Read(house, "houseStoreData")).FirstOrDefault(store => Read(store, "storeTreaData") is not null);
        var treasureData = Read(houseStoreData, "storeTreaData");
        return treasureData is not null && ReadBool(InvokeRequiredInstance(treasureData, "IsTreasureEquip", item));
    }

    private static object? GetEquipDestinationField(object hero, GearSlot slot)
    {
        var equipData = Read(hero, "heroEquipData");
        var fields = ReadList(Read(equipData, "fieldList")).ToList();
        if (slot.Part == 1)
        {
            // This is the exact native contract used by HeroData.GetEquipByPart:
            // raw field 0 is main and raw field 1 is secondary.
            var weaponField = fields.ElementAtOrDefault(slot.MainWeapon ? 0 : 1);
            return (ReadNullableInt(Read(weaponField, "tEquipFieldData"), "part") ?? 0) == 1 ? weaponField : null;
        }
        return fields.FirstOrDefault(field => (ReadNullableInt(Read(field, "tEquipFieldData"), "part") ?? 0) == slot.Part);
    }

    private static string MoveItemFailure(int code) => code switch
    {
        -2 => "swap was rejected before the destination changed",
        0 => "the game returned success but the destination did not change",
        1 => "the item or destination is invalid",
        2 => "the item does not match this equipment slot",
        6 => "the hero's Mythic equipment limit would be exceeded",
        7 => "the two Legendary/Mythic weapons conflict",
        int.MinValue => "the game did not return a move result",
        _ => $"the game rejected the move (code {code})"
    };

    private static bool TryStageEquippedWeapon(object hero, GearSlot slot, object? seasonData, List<MoveReceipt> moveJournal, out string failure)
    {
        failure = string.Empty;
        var source = GetEquipDestinationField(hero, slot);
        var oldItem = Read(source, "itemData");
        if (source is null || oldItem is null)
        {
            failure = "the opposite weapon slot is already empty";
            return false;
        }
        var emptyBagField = InvokeStaticMany("ItemSys", "FindEmptyLordInventoryField");
        if (emptyBagField is null)
        {
            var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
                .Select(house => Read(house, "houseStoreData")).FirstOrDefault(store => Read(store, "storeBaseData") is not null);
            var storeBaseData = Read(houseStoreData, "storeBaseData");
            var emptyStoreField = storeBaseData is null ? null : InvokeRequiredInstance(storeBaseData, "QuickPutItemToField", oldItem);
            if (emptyStoreField is null || Read(emptyStoreField, "itemData") is not null)
            {
                failure = "no empty bag or warehouse field is available to stage a conflicting weapon";
                return false;
            }
            var storeCode = ToInt(InvokeRequiredStaticMany("ItemSys", "MoveItem", source, emptyStoreField));
            if (Read(source, "itemData") is null && NativeEquals(Read(emptyStoreField, "itemData"), oldItem))
            {
                moveJournal.Add(new MoveReceipt(MoveReceiptKind.FieldMove, source, emptyStoreField, oldItem, null));
                return true;
            }
            failure = MoveItemFailure(storeCode);
            return false;
        }
        var beforeTo = Read(emptyBagField, "itemData");
        var code = ToInt(InvokeRequiredStaticMany("ItemSys", "MoveItem", source, emptyBagField));
        if (Read(source, "itemData") is null && beforeTo is null
            && NativeEquals(Read(emptyBagField, "itemData"), oldItem)
            && NativeEquals(InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", oldItem), emptyBagField))
        {
            moveJournal.Add(new MoveReceipt(MoveReceiptKind.FieldMove, source, emptyBagField, oldItem, null));
            return true;
        }
        failure = MoveItemFailure(code);
        return false;
    }

    private static int NormalizeCommittedStorage(List<MoveReceipt> moveJournal, object? seasonData, IReadOnlyCollection<object> targetItems)
    {
        var failures = 0;
        var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
            .Select(house => Read(house, "houseStoreData")).FirstOrDefault(store => Read(store, "storeTreaData") is not null);
        var treasureData = Read(houseStoreData, "storeTreaData");
        var handled = new HashSet<string>(StringComparer.Ordinal);

        // Bridge/conflict staging moves BeforeFromItem into an empty bag or
        // Warehouse field. After all eight targets are verified, route every
        // staged non-target through the game's normal storage decision.
        foreach (var staged in moveJournal.Where(receipt => receipt.Kind == MoveReceiptKind.FieldMove)
                     .Select(receipt => receipt.BeforeFromItem)
                     .Where(item => !targetItems.Any(target => NativeEquals(target, item))))
            NormalizeItem(staged, includeBag: true);

        // A direct Warehouse-to-hero swap leaves BeforeToItem in the original
        // Warehouse field. Keep normalizing those Vault-eligible replacements.
        foreach (var outgoing in moveJournal.Where(receipt => receipt.Kind == MoveReceiptKind.FieldMove
                     && receipt.BeforeToItem is not null)
                     .Select(receipt => receipt.BeforeToItem!)
                     .Where(item => !targetItems.Any(target => NativeEquals(target, item))))
            NormalizeItem(outgoing, includeBag: false);

        return failures;

        void NormalizeItem(object item, bool includeBag)
        {
            var key = NativeObjectKey(item, item);
            if (handled.Contains(key)) return;

            var bagField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item);
            if (bagField is not null)
            {
                if (!includeBag) return;
                handled.Add(key);
                try
                {
                    var vaultRouted = IsVaultRoutedItem(item, seasonData);
                    InvokeRequiredStaticMany("ItemSys", "QuickMoveItemFromBagToStore", item);
                    if (InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item) is not null)
                        throw new InvalidOperationException("native storage normalization left the staged item in the bag");
                    var destination = ReadAll(true).FirstOrDefault(record => NativeEquals(record.ItemData, item));
                    if (destination is null || (vaultRouted && destination.StorageSource != StorageSource.Treasure)
                        || (!vaultRouted && destination.StorageSource != StorageSource.Warehouse))
                        throw new InvalidOperationException("native storage normalization used an unexpected destination");
                    Plugin.DiagInfo($"AUTO-GEAR STORAGE NORMALIZED|item={key}|destination={destination.StorageSource}");
                }
                catch (Exception error)
                {
                    failures++;
                    Plugin.DiagWarning($"AUTO-GEAR STORAGE NORMALIZE FAILED|item={key}|{error.GetBaseException().Message}");
                }
                return;
            }

            var storageRecord = ReadAll(true).FirstOrDefault(record => record.StorageSource is StorageSource.Warehouse or StorageSource.Treasure
                && NativeEquals(record.ItemData, item));
            if (storageRecord is null)
            {
                handled.Add(key);
                failures++;
                Plugin.DiagWarning($"AUTO-GEAR STORAGE NORMALIZE FAILED|item={key}|staged item was not found in bag, Warehouse, or Vault");
                return;
            }
            handled.Add(key);
            if (storageRecord.StorageSource == StorageSource.Treasure || !IsVaultRoutedItem(item, seasonData)) return;
            try
            {
                if (treasureData is null || storageRecord.SourceField is null)
                    throw new InvalidOperationException("Vault or Warehouse field is unavailable for native normalization");
                InvokeRequiredInstance(treasureData, "TryAddEquip", storageRecord.SourceField, false);
                var movedToVault = ReadAll(true).Any(record => record.StorageSource == StorageSource.Treasure
                    && NativeEquals(record.ItemData, item));
                if (!movedToVault)
                    throw new InvalidOperationException("native Vault normalization did not move the item");
                Plugin.DiagInfo($"AUTO-GEAR STORAGE NORMALIZED|item={key}|destination=Vault");
            }
            catch (Exception error)
            {
                failures++;
                Plugin.DiagWarning($"AUTO-GEAR STORAGE NORMALIZE FAILED|item={key}|{error.GetBaseException().Message}");
            }
        }
    }

    private static int RollbackMoveJournal(List<MoveReceipt> moveJournal)
    {
        var failures = 0;
        var receiptCount = moveJournal.Count;
        for (var index = moveJournal.Count - 1; index >= 0; index--)
        {
            var receipt = moveJournal[index];
            try
            {
                if (receipt.Kind == MoveReceiptKind.BagToVault)
                {
                    if (receipt.FromField is null || receipt.TreasureData is null || receipt.GroupData is null
                        || Read(receipt.FromField, "itemData") is not null
                        || !IsItemInVaultGroup(receipt.BeforeFromItem, receipt.GroupData))
                        throw new InvalidOperationException("Vault deposit state no longer matches the recorded move");
                    InvokeRequiredStaticMany("ItemSys", "QuickMoveTreasureEquipToBag", receipt.GroupData, receipt.BeforeFromItem);
                    var actualBagField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", receipt.BeforeFromItem)
                                         ?? throw new InvalidOperationException("returned Vault item was not found in the bag");
                    if (!NativeEquals(actualBagField, receipt.FromField))
                    {
                        if (Read(receipt.FromField, "itemData") is not null)
                            throw new InvalidOperationException("original bridge bag field is no longer empty");
                        InvokeRequiredStaticMany("ItemSys", "MoveItem", actualBagField, receipt.FromField);
                    }
                    if (!NativeEquals(Read(receipt.FromField, "itemData"), receipt.BeforeFromItem))
                        throw new InvalidOperationException("Vault deposit reverse move did not restore the bag field");
                    continue;
                }
                if (receipt.Kind == MoveReceiptKind.VaultToBag)
                {
                    var bagField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", receipt.BeforeFromItem);
                    if (bagField is null || receipt.GroupData is null)
                        throw new InvalidOperationException("extracted Vault item is not in the bag before rollback");
                    InvokeRequiredStaticMany("ItemSys", "QuickMoveItemFromBagToStore", receipt.BeforeFromItem);
                    if (InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", receipt.BeforeFromItem) is not null
                        || !IsItemInVaultGroup(receipt.BeforeFromItem, receipt.GroupData))
                        throw new InvalidOperationException("extracted item did not return to its original Vault group");
                    continue;
                }

                if (receipt.FromField is null || receipt.ToField is null
                    || !NativeStateEquals(Read(receipt.ToField, "itemData"), receipt.BeforeFromItem)
                    || !NativeStateEquals(Read(receipt.FromField, "itemData"), receipt.BeforeToItem))
                    throw new InvalidOperationException("field state no longer matches the recorded move");
                InvokeRequiredStaticMany("ItemSys", "MoveItem", receipt.ToField, receipt.FromField);
                if (!NativeStateEquals(Read(receipt.FromField, "itemData"), receipt.BeforeFromItem)
                    || !NativeStateEquals(Read(receipt.ToField, "itemData"), receipt.BeforeToItem))
                    throw new InvalidOperationException("reverse move did not restore both fields");
            }
            catch (Exception error)
            {
                failures++;
                Plugin.DiagWarning($"AUTO-GEAR ROLLBACK FAILED|index={index}|reason={error.GetBaseException().Message}");
            }
        }
        if (receiptCount > 0)
            Plugin.DiagInfo($"AUTO-GEAR ROLLBACK|moves={receiptCount}|failures={failures}");
        moveJournal.Clear();
        return failures;
    }

    private static bool IsItemInVaultGroup(object item, object expectedGroup)
    {
        var record = ReadAll(true).FirstOrDefault(entry => entry.StorageSource == StorageSource.Treasure
            && NativeEquals(entry.ItemData, item));
        return record?.GroupData is not null && SameStorageGroup(record.GroupData, expectedGroup);
    }

    private static bool SameStorageGroup(object left, object right)
    {
        if (NativeEquals(left, right)) return true;
        var leftSave = Read(left, "saveEquipGroupData");
        var rightSave = Read(right, "saveEquipGroupData");
        var leftId = ReadNullableInt(leftSave, "id") ?? ReadNullableInt(Read(left, "tEquipData"), "id") ?? 0;
        var rightId = ReadNullableInt(rightSave, "id") ?? ReadNullableInt(Read(right, "tEquipData"), "id") ?? 0;
        var leftQuality = ReadNullableInt(leftSave, "quality") ?? 0;
        var rightQuality = ReadNullableInt(rightSave, "quality") ?? 0;
        return leftId > 0 && leftId == rightId && leftQuality == rightQuality;
    }

    private static bool TryMoveCandidateToBag(ItemSearchRecord record, object? seasonData, List<MoveReceipt> moveJournal)
    {
        if (InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", record.ItemData) is not null) return true;
        if (record.StorageSource == StorageSource.Equipped)
            return InvokeStatic("ItemSys", "FindHeroEquipFieldByItem", record.ItemData) is not null;
        if (record.StorageSource == StorageSource.Warehouse)
        {
            if (record.SourceField is null || !NativeEquals(Read(record.SourceField, "itemData"), record.ItemData)) return false;
            InvokeRequiredStaticMany("ItemSys", "QuickMoveItemFromStoreToBag", record.ItemData);
            var bagField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", record.ItemData);
            if (bagField is null || Read(record.SourceField, "itemData") is not null) return false;
            moveJournal.Add(new MoveReceipt(MoveReceiptKind.FieldMove, record.SourceField, bagField, record.ItemData, null));
            return true;
        }
        var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
            .Select(house => Read(house, "houseStoreData")).FirstOrDefault(store => Read(store, "storeTreaData") is not null);
        var treasure = Read(houseStoreData, "storeTreaData");
        if (treasure is null || record.GroupData is null) return false;
        InvokeRequiredStaticMany("ItemSys", "QuickMoveTreasureEquipToBag", record.GroupData, record.ItemData);
        var extractedBagField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", record.ItemData);
        if (extractedBagField is null) return false;
        moveJournal.Add(new MoveReceipt(MoveReceiptKind.VaultToBag, null, extractedBagField, record.ItemData, null, treasure, record.GroupData));
        return true;
    }

    private static double ReadHeroAttr(object hero, int id)
    {
        try
        {
            var attr = Read(hero, "attrData");
            var type = CreateEnum("EAttrType", id);
            return attr is null || type is null ? 0d : Convert.ToDouble(InvokeInstance(attr, "GetAttrValue", type) ?? 0d, CultureInfo.InvariantCulture);
        }
        catch { return 0d; }
    }

    private static int KeywordScore(string text, IEnumerable<string> words)
    {
        // Match complete ASCII words/phrases. The old substring check counted
        // `ice` inside justice/sacrifice/price and could select an Ice guide with
        // no ice effect at all. Morphological aliases are one evidence family,
        // preventing burn+burning or crit+critical from double weighting a row.
        var haystack = $" {Regex.Replace((text ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", " ").Trim()} ";
        return words.Select(NormalizeKeyword)
            .Where(word => word.Length > 0)
            .GroupBy(KeywordFamily, StringComparer.Ordinal)
            .Count(group => KeywordFamilyForms(group.Key, group).Any(form =>
                haystack.Contains($" {NormalizeKeyword(form)} ", StringComparison.Ordinal)));
    }

    private static string NormalizeKeyword(string value)
        => Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();

    private static string KeywordFamily(string word) => word switch
    {
        "burn" or "burning" => "burn",
        "freeze" or "frozen" => "freeze",
        "shock" or "shocked" => "shock",
        "summon" or "summoned" => "summon",
        "bleed" or "bleeding" => "bleed",
        "crit" or "critical" or "critical strike" => "critical",
        "corrosion" or "corrode" => "corrosion",
        "immunity" or "immune" => "immune",
        "defense" or "defence" => "defense",
        "armor" or "armour" => "armor",
        _ => word
    };

    private static IEnumerable<string> KeywordFamilyForms(string family, IEnumerable<string> declared) => family switch
    {
        "burn" => new[] { "burn", "burning" },
        "freeze" => new[] { "freeze", "frozen" },
        "shock" => new[] { "shock", "shocked" },
        "summon" => new[] { "summon", "summoned" },
        "bleed" => new[] { "bleed", "bleeding" },
        "critical" => new[] { "crit", "critical", "critical strike" },
        "corrosion" => new[] { "corrosion", "corrode" },
        "immune" => new[] { "immunity", "immune" },
        "defense" => new[] { "defense", "defence" },
        "armor" => new[] { "armor", "armour" },
        _ => declared
    };

    private static readonly string[] PhysicalWords = { "physical", "martial", "strike", "blunt", "slash", "pierce", "strength", "dexterity", "weapon", "crit" };
    private static readonly string[] ElementalWords = { "elemental", "spell", "fire", "frost", "ice", "lightning", "intelligence", "mana", "magic" };
    private static readonly string[] FireWords = { "fire", "burn", "burning", "ignite", "flame", "blaze", "ember", "scorch", "sunfire", "inferno" };
    private static readonly string[] IceWords = { "ice", "frost", "cold", "freeze", "frozen", "chill", "winter", "glacier" };
    private static readonly string[] LightningWords = { "lightning", "shock", "shocked", "thunder", "electric", "thunderbolt", "thundercall" };
    private static readonly string[] MinionWords = { "minion", "summon", "summoned", "pet", "companion" };
    private static readonly string[] BleedWords = { "bleed", "bleeding", "wound" };
    private static readonly string[] CorrosionWords = { "corrosion", "corrode", "poison", "toxic", "acid" };
    private static readonly string[] CriticalWords = { "crit", "critical", "critical strike" };
    private static readonly string[] SupportWords = { "support", "heal", "restore", "aura", "buff", "ally", "shield", "warcry", "recovery" };
    private static readonly string[] TankWords = { "defense", "defence", "health", "resist", "block", "survival", "damage taken", "damage reduction", "toughness", "immunity", "immune", "shield", "barrier", "guard", "ward", "shelter", "armor", "armour", "fortitude", "lifesteal" };

    private static object? CreateEnum(string typeName, int value)
    {
        try { return GameType(typeName) is { } type ? Enum.ToObject(type, value) : null; }
        catch { return null; }
    }

    private static bool ReadBool(object? value)
    {
        try { return value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static bool NativeEquals(object? left, object? right)
    {
        if (left is null || right is null) return false;
        if (ReferenceEquals(left, right)) return true;
        var leftPointer = Read(left, "Pointer");
        var rightPointer = Read(right, "Pointer");
        return leftPointer is not null && rightPointer is not null && Equals(leftPointer, rightPointer);
    }

    private static bool NativeStateEquals(object? left, object? right)
        => left is null ? right is null : right is not null && NativeEquals(left, right);

    private static IEnumerable<object> ReadSequence(object? value)
    {
        if (value is null) yield break;
        var yielded = false;
        foreach (var item in ReadList(value)) { yielded = true; yield return item; }
        if (yielded) yield break;
        foreach (var item in Enumerate(value)) yield return item;
    }

    private static List<int[]> ReadIntMatrixRows(object? value, int expectedColumns)
    {
        if (value is null || expectedColumns <= 0) return new List<int[]>();
        if (value is Il2CppObjectBase native)
        {
            var rank = IL2CPP.il2cpp_class_get_rank(native.ObjectClass);
            if (rank != 2)
                throw new InvalidOperationException($"Expected a rank-2 native matrix, got rank {rank}.");
            var nativeFlat = new Il2CppStructArray<int>(native.Pointer);
            if (nativeFlat.Length is < 0 or > 40000 || nativeFlat.Length % expectedColumns != 0)
                throw new InvalidOperationException($"Native matrix length {nativeFlat.Length} is not divisible by {expectedColumns}.");
            var rows = new List<int[]>(nativeFlat.Length / expectedColumns);
            for (var index = 0; index < nativeFlat.Length; index += expectedColumns)
            {
                var row = new int[expectedColumns];
                for (var column = 0; column < expectedColumns; column++)
                    row[column] = nativeFlat[index + column];
                rows.Add(row);
            }
            return rows;
        }
        if (value is Array managed)
        {
            if (managed.Rank != 2 || managed.GetLength(1) != expectedColumns)
                throw new InvalidOperationException($"Expected a 2D matrix with {expectedColumns} columns.");
            var rows = new List<int[]>(managed.GetLength(0));
            for (var row = 0; row < managed.GetLength(0); row++)
            {
                var values = new int[expectedColumns];
                for (var column = 0; column < expectedColumns; column++)
                    values[column] = ToInt(managed.GetValue(row, column));
                rows.Add(values);
            }
            return rows;
        }

        try
        {
            var type = value.GetType();
            var getLength = type.GetMethod("GetLength", new[] { typeof(int) });
            var getItem = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name is "get_Item" or "Get"
                                          && method.GetParameters().Length == 2
                                          && method.GetParameters().All(parameter => parameter.ParameterType == typeof(int)));
            if (getLength is not null && getItem is not null)
            {
                var rowCount = ToInt(getLength.Invoke(value, new object[] { 0 }));
                var columnCount = ToInt(getLength.Invoke(value, new object[] { 1 }));
                if (rowCount is < 0 or > 20000 || columnCount != expectedColumns)
                    throw new InvalidOperationException($"Native matrix dimensions are invalid ({rowCount}x{columnCount}).");
                var rows = new List<int[]>(rowCount);
                for (var row = 0; row < rowCount; row++)
                {
                    var values = new int[expectedColumns];
                    for (var column = 0; column < expectedColumns; column++)
                        values[column] = ToInt(getItem.Invoke(value, new object[] { row, column }));
                    rows.Add(values);
                }
                return rows;
            }
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException($"Could not read native matrix: {error.InnerException.Message}", error.InnerException);
        }

        var flattened = ReadSequence(value).Select(ToInt).ToList();
        if (flattened.Count % expectedColumns != 0)
            throw new InvalidOperationException($"Native matrix length {flattened.Count} is not divisible by {expectedColumns}.");
        var fallback = new List<int[]>(flattened.Count / expectedColumns);
        for (var index = 0; index < flattened.Count; index += expectedColumns)
            fallback.Add(flattened.Skip(index).Take(expectedColumns).ToArray());
        return fallback;
    }

    private static List<double[]> ReadFloatMatrixRows(object? value, int expectedColumns)
    {
        if (value is null || expectedColumns <= 0) return new List<double[]>();
        if (value is Il2CppObjectBase native)
        {
            var rank = IL2CPP.il2cpp_class_get_rank(native.ObjectClass);
            if (rank != 2)
                throw new InvalidOperationException($"Expected a rank-2 native matrix, got rank {rank}.");
            var flattened = new Il2CppStructArray<float>(native.Pointer);
            if (flattened.Length is < 0 or > 40000 || flattened.Length % expectedColumns != 0)
                throw new InvalidOperationException($"Native matrix length {flattened.Length} is not divisible by {expectedColumns}.");
            var rows = new List<double[]>(flattened.Length / expectedColumns);
            for (var index = 0; index < flattened.Length; index += expectedColumns)
            {
                var row = new double[expectedColumns];
                for (var column = 0; column < expectedColumns; column++)
                    row[column] = flattened[index + column];
                rows.Add(row);
            }
            return rows;
        }
        if (value is Array managed)
        {
            if (managed.Rank != 2 || managed.GetLength(1) != expectedColumns)
                throw new InvalidOperationException($"Expected a 2D matrix with {expectedColumns} columns.");
            var rows = new List<double[]>(managed.GetLength(0));
            for (var rowIndex = 0; rowIndex < managed.GetLength(0); rowIndex++)
            {
                var row = new double[expectedColumns];
                for (var column = 0; column < expectedColumns; column++)
                    row[column] = Convert.ToDouble(managed.GetValue(rowIndex, column) ?? 0d, CultureInfo.InvariantCulture);
                rows.Add(row);
            }
            return rows;
        }

        var flattenedFallback = ReadSequence(value)
            .Select(item => Convert.ToDouble(item, CultureInfo.InvariantCulture)).ToList();
        if (flattenedFallback.Count % expectedColumns != 0)
            throw new InvalidOperationException($"Native matrix length {flattenedFallback.Count} is not divisible by {expectedColumns}.");
        var fallback = new List<double[]>(flattenedFallback.Count / expectedColumns);
        for (var index = 0; index < flattenedFallback.Count; index += expectedColumns)
            fallback.Add(flattenedFallback.Skip(index).Take(expectedColumns).ToArray());
        return fallback;
    }

    private static List<object> ReadRequiredSequenceProperty(object owner, string propertyName)
    {
        var value = ReadRequiredProperty(owner, propertyName)
                    ?? throw new InvalidOperationException($"{owner.GetType().Name}.{propertyName} is null.");
        var getItem = value.GetType().GetMethod("get_Item", new[] { typeof(int) })
                      ?? throw new MissingMethodException(value.GetType().FullName, "get_Item");
        var countProperty = value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? value.GetType().GetProperty("Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? throw new MissingMemberException(value.GetType().FullName, "Count/Length");
        var count = Convert.ToInt32(countProperty.GetValue(value)
                                    ?? throw new InvalidOperationException($"{propertyName} count is null."), CultureInfo.InvariantCulture);
        if (count is < 0 or > 20000) throw new InvalidOperationException($"{propertyName} count is invalid ({count}).");
        var result = new List<object>(count);
        for (var index = 0; index < count; index++)
        {
            try
            {
                var item = getItem.Invoke(value, new object[] { index })
                           ?? throw new InvalidOperationException($"{propertyName}[{index}] is null.");
                result.Add(item);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                throw new InvalidOperationException($"Could not read {propertyName}[{index}]: {error.InnerException.Message}", error.InnerException);
            }
        }
        return result;
    }

    [Conditional("POI_DEV_FEATURE")]
    private static void LogSupportedLanguagesOnce()
    {
        if (languageTableLogged) return;
        try
        {
            var rows = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var id = 0; id <= 16; id++)
            {
                var row = InvokeStatic("TableData", "getTLanguageData", id);
                if (row is null) continue;
                var key = $"{ReadNullableInt(row, "id") ?? id}:{ReadString(row, "shortName") ?? string.Empty}:{ReadString(row, "name") ?? string.Empty}";
                if (seen.Add(key)) rows.Add(key);
            }
            if (rows.Count == 0) return;
            languageTableLogged = true;
            Plugin.DiagInfo($"Game language table: {string.Join(", ", rows)}");
        }
        catch
        {
            // Language auto-detection still falls back safely when the table is not ready yet.
        }
    }

    public static List<ItemSearchRecord> ReadAll(bool includeWarehouse)
    {
        var result = new List<ItemSearchRecord>();
        var dataManager = ReadStatic("Game", "dataMgr") ?? throw new InvalidOperationException("Game.dataMgr is unavailable.");
        var seasonData = Read(dataManager, "nowSeasonData") ?? throw new InvalidOperationException("No active save is loaded.");
        var lordData = Read(seasonData, "lordData");
        var bagData = Read(lordData, "lordBagData");
        var itemType = GameType("EItemType");
        if (bagData is null || itemType is null) throw new InvalidOperationException("Inventory is unavailable.");

        var equipmentType = Enum.ToObject(itemType, 2);
        var inventoryFields = InvokeRequiredInstance(bagData, "GetFieldList", equipmentType);
        foreach (var (field, index) in ReadList(inventoryFields).Select((field, index) => (field, index)))
        {
            var item = Read(field, "itemData");
            // GetFieldList returns the general bag list for equipment/tool enum
            // values; filter by the item's real saved type before describing it.
            if (!IsEquipmentItem(item)) continue;
            result.Add(DescribeItem(item!, UiText.L($"인벤토리 #{index + 1}", $"Inventory #{index + 1}", $"背包 #{index + 1}", $"背包 #{index + 1}"), StorageKind.Inventory, StorageSource.Inventory, field));
        }

        if (!includeWarehouse) return result;

        var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
            .Select(house => Read(house, "houseStoreData"))
            .FirstOrDefault(store => Read(store, "storeBaseData") is not null || Read(store, "storeTreaData") is not null);
        var storeBaseData = Read(houseStoreData, "storeBaseData");
        var pages = ReadEntries(Read(storeBaseData, "storeDic"))
            .Select((entry, ordinal) => new { Page = Read(entry, "Value"), Key = ReadNullableInt(entry, "Key"), Ordinal = ordinal })
            .Where(entry => entry.Page is not null).ToList();
        var zeroBased = pages.Any(page => page.Key == 0);
        foreach (var page in pages)
        {
            var pageNumber = page.Key is { } key ? key + (zeroBased ? 1 : 0) : page.Ordinal + 1;
            foreach (var (field, index) in ReadList(page.Page).Select((field, index) => (field, index)))
            {
                var item = Read(field, "itemData");
                if (!IsEquipmentItem(item)) continue;
                result.Add(DescribeItem(item!, UiText.L($"창고 {pageNumber}페이지 #{index + 1}", $"Warehouse page {pageNumber} #{index + 1}", $"仓库第 {pageNumber} 页 #{index + 1}", $"倉庫第 {pageNumber} 頁 #{index + 1}"), StorageKind.Warehouse, StorageSource.Warehouse, field));
            }
        }

        var storeTreasureData = Read(houseStoreData, "storeTreaData");
        foreach (var groupList in ReadValues(Read(storeTreasureData, "equipGroupDic")))
        foreach (var group in ReadList(groupList))
        {
            var groupId = ReadNullableInt(Read(group, "saveEquipGroupData"), "id") ?? ReadNullableInt(Read(group, "tEquipData"), "id");
            foreach (var (item, index) in ReadList(Read(group, "equipList")).Select((item, index) => (item, index)))
            {
                if (!IsEquipmentItem(item)) continue;
                result.Add(DescribeItem(item, UiText.L($"Vault {groupId?.ToString(CultureInfo.InvariantCulture) ?? "?"} · #{index + 1}", $"Vault {groupId?.ToString(CultureInfo.InvariantCulture) ?? "?"} · #{index + 1}", $"宝库 {groupId?.ToString(CultureInfo.InvariantCulture) ?? "?"} · #{index + 1}", $"寶庫 {groupId?.ToString(CultureInfo.InvariantCulture) ?? "?"} · #{index + 1}"), StorageKind.Warehouse, StorageSource.Treasure, null, group));
            }
        }
        return result;
    }

    public static (bool Success, int EquipmentBoxes, int RuneBoxes) GetBulkToolCounts(int qualityMask, bool atLeast)
    {
        try
        {
            var stacks = ReadBulkToolStacks().Where(stack => QualityFilterLogic.Matches(stack.Quality, qualityMask, atLeast)).ToList();
            bulkCountFailureLogged = false;
            return (true,
                stacks.Where(stack => stack.Kind == BulkToolKind.EquipmentBox).Sum(stack => stack.Count),
                stacks.Where(stack => stack.Kind == BulkToolKind.RuneBox).Sum(stack => stack.Count));
        }
        catch (Exception error)
        {
            if (!bulkCountFailureLogged)
            {
                bulkCountFailureLogged = true;
                Plugin.DiagWarning($"BULK TOOL COUNT FAILED|counts are unavailable, not zero|{error.GetBaseException().Message}");
            }
            return (false, 0, 0);
        }
    }

    public static bool TryBeginBulkOpen(BulkToolKind kind, int qualityMask, bool atLeast,
        bool autoStoreEquipment, string label, out BulkOpenSession session, out string message)
    {
        session = null!;
        try
        {
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData")
                             ?? throw new InvalidOperationException("Season data is unavailable.");
            var lordData = Read(seasonData, "lordData")
                           ?? throw new InvalidOperationException("Lord data is unavailable.");
            var bagData = Read(lordData, "lordBagData")
                          ?? throw new InvalidOperationException("Inventory data is unavailable.");
            var initial = ReadBulkToolStacks()
                .Where(stack => stack.Kind == kind && QualityFilterLogic.Matches(stack.Quality, qualityMask, atLeast))
                .Sum(stack => stack.Count);
            if (initial <= 0)
            {
                message = UiText.L($"열 수 있는 {label}가 없습니다.", $"There are no {label} to open.", $"没有可开启的{label}。", $"沒有可開啟的{label}。");
                return false;
            }

            session = new BulkOpenSession
            {
                Kind = kind,
                QualityMask = qualityMask,
                AtLeast = atLeast,
                AutoStoreEquipment = autoStoreEquipment,
                Label = label,
                Initial = initial,
                SaveIdentity = NativeObjectKey(seasonData, seasonData),
                KnownEquipment = kind == BulkToolKind.EquipmentBox
                    ? new HashSet<string>(ReadInventoryEquipment().Select(entry => entry.Key), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal)
            };
            message = UiText.L(
                $"{label} {initial:N0}개 개봉을 시작했습니다.",
                $"Started opening {initial:N0} {label}.",
                $"已开始开启 {initial:N0} 个{label}。",
                $"已開始開啟 {initial:N0} 個{label}。");
            return true;
        }
        catch (Exception error)
        {
            message = UiText.L(
                $"{label} 개봉을 시작하지 못했습니다: {error.GetBaseException().Message}",
                $"Could not start opening {label}: {error.GetBaseException().Message}",
                $"无法开始开启{label}：{error.GetBaseException().Message}",
                $"無法開始開啟{label}：{error.GetBaseException().Message}");
            return false;
        }
    }

    public static (bool Finished, bool Success, string Message) AdvanceBulkOpen(BulkOpenSession session)
    {
        const int batchCap = 1;
        try
        {
            if (session.CancelRequested)
                return FinishBulkOpenSession(session, true, false, null);

            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData")
                             ?? throw new InvalidOperationException("Season data is unavailable.");
            if (!string.Equals(NativeObjectKey(seasonData, seasonData), session.SaveIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException("The active save changed during bulk opening.");
            var lordData = Read(seasonData, "lordData")
                           ?? throw new InvalidOperationException("Lord data is unavailable.");
            var bagData = Read(lordData, "lordBagData")
                          ?? throw new InvalidOperationException("Inventory data is unavailable.");

            var stacks = ReadBulkToolStacks()
                .Where(stack => stack.Kind == session.Kind && stack.Count > 0
                                && QualityFilterLogic.Matches(stack.Quality, session.QualityMask, session.AtLeast))
                .ToList();
            var remaining = stacks.Sum(stack => stack.Count);
            if (session.Opened >= session.Initial || remaining <= 0)
                return FinishBulkOpenSession(session, false, false, remaining);

            var stack = stacks.Where(entry => !session.BlockedStacks.Contains(entry.Key))
                .OrderByDescending(entry => entry.Count).FirstOrDefault();
            if (stack is null)
                return FinishBulkOpenSession(session, false, false, remaining);

            var remainingTarget = Math.Max(1, session.Initial - session.Opened);
            var request = Math.Min(Math.Min(stack.Count, remainingTarget), batchCap);
            request = Math.Max(1, request);
            var beforeStackCount = stack.Count;
            var beforeItems = CaptureBagWalletItemSnapshot(lordData);
            session.MutationAttempted = true;
            var rewardValue = InvokeRequiredInstance(bagData, "UseToolCount", stack.ItemData, request, true);
            var rewards = ReadList(rewardValue).ToList();
            var afterStacks = ReadBulkToolStacks()
                .Where(entry => entry.Kind == session.Kind
                                && QualityFilterLogic.Matches(entry.Quality, session.QualityMask, session.AtLeast))
                .ToList();
            var afterStackCount = afterStacks.FirstOrDefault(entry => string.Equals(entry.Key, stack.Key, StringComparison.Ordinal))?.Count ?? 0;
            var consumed = Math.Max(0, beforeStackCount - afterStackCount);
            if (consumed > 0)
            {
                session.Opened += consumed;
                if (session.AutoStoreEquipment && session.Kind == BulkToolKind.EquipmentBox)
                    session.AutoStored += AutoStoreNewInventoryEquipment(session.KnownEquipment, session.FailedAutoStore, rewards);
            }
            else
            {
                var afterItems = CaptureBagWalletItemSnapshot(lordData);
                session.BlockedStacks.Add(stack.Key);
                var partialReward = HasPositiveItemDelta(beforeItems, afterItems);
                return FinishBulkOpenSession(session, false, true, afterStacks.Sum(entry => entry.Count),
                    partialReward
                        ? UiText.L("상자는 줄지 않았지만 일부 보상이 추가되어 재시도를 중단했습니다.",
                            "The box was not consumed, but partial rewards were added; retry was stopped to prevent duplication.",
                            "箱子未消耗，但已加入部分奖励；为防止重复，已停止重试。",
                            "箱子未消耗，但已加入部分獎勵；為防止重複，已停止重試。")
                        : UiText.L("게임이 이 상자 개봉을 거부했습니다. 일부 재화 변경 가능성이 있어 재시도 없이 중단했습니다.",
                            "The game refused this box. A partial resource change is possible, so bulk opening stopped without retrying.",
                            "游戏拒绝开启此箱子。可能已有部分资源变化，因此已停止且不会重试。",
                            "遊戲拒絕開啟此箱子。可能已有部分資源變更，因此已停止且不會重試。"));
            }

            var progress = UiText.L(
                $"{session.Label} 개봉 중 · 확인 {session.Opened:N0}/{session.Initial:N0}개",
                $"Opening {session.Label} · confirmed {session.Opened:N0}/{session.Initial:N0}",
                $"正在开启{session.Label} · 已确认 {session.Opened:N0}/{session.Initial:N0}",
                $"正在開啟{session.Label} · 已確認 {session.Opened:N0}/{session.Initial:N0}");
            return (false, true, progress);
        }
        catch (Exception error)
        {
            int? remaining = null;
            try
            {
                remaining = ReadBulkToolStacks()
                    .Where(stack => stack.Kind == session.Kind
                                    && QualityFilterLogic.Matches(stack.Quality, session.QualityMask, session.AtLeast))
                    .Sum(stack => stack.Count);
            }
            catch { }
            return FinishBulkOpenSession(session, false, session.MutationAttempted, remaining, error.GetBaseException().Message);
        }
    }

    private static (bool Finished, bool Success, string Message) FinishBulkOpenSession(BulkOpenSession session,
        bool cancelled, bool uncertain, int? remaining, string? error = null)
    {
        if (remaining is null)
        {
            try
            {
                remaining = ReadBulkToolStacks()
                    .Where(stack => stack.Kind == session.Kind
                                    && QualityFilterLogic.Matches(stack.Quality, session.QualityMask, session.AtLeast))
                    .Sum(stack => stack.Count);
            }
            catch { }
        }
        var remainingText = remaining?.ToString("N0", CultureInfo.CurrentCulture) ?? UiText.L("확인 불가", "unknown", "未知", "未知");
        var prefix = uncertain
            ? UiText.L("작업 상태 확인 불가", "Outcome uncertain", "操作结果无法确认", "操作結果無法確認")
            : cancelled
                ? UiText.L("사용자 중단", "Cancelled", "已取消", "已取消")
                : session.Opened >= session.Initial
                    ? UiText.L("개봉 완료", "Opening complete", "开启完成", "開啟完成")
                    : UiText.L("가능한 만큼 개봉 완료", "Opened as many as possible", "已尽可能开启", "已盡可能開啟");
        var message = UiText.L(
            $"{prefix} · {session.Label} 확인 개봉 {session.Opened:N0}/{session.Initial:N0}개 · 현재 남음 {remainingText}",
            $"{prefix} · confirmed opened {session.Opened:N0}/{session.Initial:N0} {session.Label} · currently left {remainingText}",
            $"{prefix} · 已确认开启 {session.Opened:N0}/{session.Initial:N0} 个{session.Label} · 当前剩余 {remainingText}",
            $"{prefix} · 已確認開啟 {session.Opened:N0}/{session.Initial:N0} 個{session.Label} · 目前剩餘 {remainingText}");
        if (session.AutoStored > 0)
            message += UiText.L($" · 장비 {session.AutoStored:N0}개 자동 보관", $" · auto-stored {session.AutoStored:N0} gear", $" · 自动入库 {session.AutoStored:N0} 件装备", $" · 自動入庫 {session.AutoStored:N0} 件裝備");
        if (session.FailedAutoStore.Count > 0)
            message += UiText.L($" · 자동 보관 미확인 {session.FailedAutoStore.Count:N0}개", $" · storage unverified for {session.FailedAutoStore.Count:N0}", $" · {session.FailedAutoStore.Count:N0} 件入库未确认", $" · {session.FailedAutoStore.Count:N0} 件入庫未確認");
        if (!string.IsNullOrWhiteSpace(error)) message += $" · {error}";
        var success = !uncertain && session.FailedAutoStore.Count == 0 && (cancelled || session.Opened > 0);
        return (true, success, message);
    }

    private static Dictionary<string, int> CaptureBagWalletItemSnapshot(object lordData)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        var bagData = Read(lordData, "lordBagData")
                      ?? throw new InvalidOperationException("Inventory data is unavailable.");
        var walletData = Read(lordData, "lordWalletData")
                         ?? throw new InvalidOperationException("Wallet data is unavailable.");
        AddFields(Read(walletData, "fieldList"));
        AddFields(Read(bagData, "fieldList"));
        var itemType = GameType("EItemType") ?? throw new InvalidOperationException("The game item-type table is unavailable.");
        foreach (var typeId in new[] { 1, 2, 3, 5, 10 })
            AddFields(InvokeRequiredInstance(bagData, "GetFieldList", Enum.ToObject(itemType, typeId)));
        return result;

        void AddFields(object? fields)
        {
            foreach (var field in ReadList(fields))
            {
                var fieldKey = NativeObjectKey(field, field);
                if (!seenFields.Add(fieldKey)) continue;
                var item = Read(field, "itemData");
                if (item is null) continue;
                var save = Read(item, "saveItemData");
                var count = Math.Max(1, ReadNullableInt(save, "count") ?? 1);
                result[NativeObjectKey(item, field)] = count;
            }
        }
    }

    private static bool HasPositiveItemDelta(IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
        => after.Any(entry => entry.Value > before.GetValueOrDefault(entry.Key));

    private static int AutoStoreNewInventoryEquipment(HashSet<string> knownEquipment, HashSet<string> failedEquipment, IReadOnlyCollection<object> rewards)
    {
        var moved = 0;
        var rewardEquipment = rewards.Where(IsEquipmentItem).ToList();
        var pending = ReadInventoryEquipment()
            .Where(entry => !knownEquipment.Contains(entry.Key))
            .Where(entry => rewardEquipment.Count == 0 || rewardEquipment.Any(reward => NativeEquals(reward, entry.ItemData)))
            .ToList();
        foreach (var entry in pending)
        {
            InvokeStaticMany("ItemSys", "QuickMoveItemFromBagToStore", entry.ItemData);
        }

        // Verify raw storage fields instead of rebuilding every translated item
        // description after every box. A cleared bag slot alone is not enough.
        foreach (var entry in pending)
        {
            var sourceCleared = Read(entry.SourceField, "itemData") is null;
            var destinationFound = sourceCleared && IsItemInRawStorage(entry.ItemData);
            if (destinationFound)
            {
                knownEquipment.Add(entry.Key);
                failedEquipment.Remove(entry.Key);
                moved++;
                continue;
            }

            failedEquipment.Add(entry.Key);
            // If the item disappeared from the source but did not appear in storage,
            // do not claim success and do not repeatedly call move on a stale object.
            if (sourceCleared) knownEquipment.Add(entry.Key);
        }
        return moved;
    }

    private static bool IsItemInRawStorage(object item)
    {
        var dataManager = ReadStatic("Game", "dataMgr");
        var seasonData = Read(dataManager, "nowSeasonData");
        var houseStores = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
            .Select(house => Read(house, "houseStoreData"))
            .Where(store => store is not null).ToList();
        foreach (var store in houseStores)
        {
            foreach (var page in ReadEntries(Read(Read(store, "storeBaseData"), "storeDic")).Select(entry => Read(entry, "Value")))
            foreach (var field in ReadList(page))
                if (NativeEquals(Read(field, "itemData"), item)) return true;

            foreach (var groupList in ReadValues(Read(Read(store, "storeTreaData"), "equipGroupDic")))
            foreach (var group in ReadList(groupList))
            foreach (var stored in ReadList(Read(group, "equipList")))
                if (NativeEquals(stored, item)) return true;
        }
        return false;
    }

    private static List<InventoryEquipment> ReadInventoryEquipment()
    {
        var result = new List<InventoryEquipment>();
        var dataManager = ReadStatic("Game", "dataMgr");
        var seasonData = Read(dataManager, "nowSeasonData");
        var lordData = Read(seasonData, "lordData");
        var bagData = Read(lordData, "lordBagData");
        var itemType = GameType("EItemType");
        if (bagData is null || itemType is null)
            throw new InvalidOperationException("Inventory equipment fields are unavailable.");
        var fields = InvokeRequiredInstance(bagData, "GetFieldList", Enum.ToObject(itemType, 2));
        foreach (var field in ReadList(fields))
        {
            var item = Read(field, "itemData");
            if (!IsEquipmentItem(item)) continue;
            result.Add(new InventoryEquipment(NativeObjectKey(item!, field), item!, field));
        }
        return result;
    }

    private static List<BulkToolStack> ReadBulkToolStacks()
    {
        var result = new List<BulkToolStack>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dataManager = ReadStatic("Game", "dataMgr");
        var seasonData = Read(dataManager, "nowSeasonData");
        var lordData = Read(seasonData, "lordData");
        var bagData = Read(lordData, "lordBagData");
        var walletData = Read(lordData, "lordWalletData");

        if (dataManager is null || seasonData is null || lordData is null || bagData is null || walletData is null)
            throw new InvalidOperationException("An active save with initialized bag and wallet data is required.");

        AddToolFields(Read(walletData, "fieldList"));
        var itemType = GameType("EItemType");
        if (itemType is null) throw new InvalidOperationException("The game item-type table is unavailable.");
        AddToolFields(InvokeRequiredInstance(bagData, "GetFieldList", Enum.ToObject(itemType, 10)));
        return result;

        void AddToolFields(object? fields)
        {
            foreach (var field in ReadList(fields))
            {
                var item = Read(field, "itemData");
                var save = Read(item, "saveItemData");
                var tool = Read(item, "itemToolData");
                var definition = Read(tool, "tToolData");
                var toolType = ReadNullableInt(definition, "type") ?? -1;
                var kind = toolType switch
                {
                    1 => BulkToolKind.EquipmentBox,
                    2 => BulkToolKind.RuneBox,
                    _ => BulkToolKind.None
                };
                var count = ReadNullableInt(save, "count") ?? 0;
                var quality = ResolveBulkToolQuality(save, definition);
                if (item is null || kind == BulkToolKind.None || count <= 0) continue;

                var toolId = ReadNullableInt(definition, "id") ?? ReadNullableInt(save, "id") ?? 0;
                if (loggedBulkToolQualities.Add($"{toolId}:{toolType}"))
                    Plugin.DiagInfo($"Bulk tool quality resolved: id={toolId}, type={toolType}, saveQuality={ReadNullableInt(save, "quality") ?? 0}, saveLevel={ReadNullableInt(save, "level") ?? 0}, definitionQuality={ReadNullableInt(definition, "quality") ?? 0}, resolved={quality}");

                var key = NativeObjectKey(item, field);
                if (!seen.Add(key)) continue;
                result.Add(new BulkToolStack(key, kind, item, count, quality));
            }
        }
    }

    private static int ResolveBulkToolQuality(object? save, object? definition)
    {
        // Tool stacks normally keep quality on TTool, while SaveItemData.quality
        // is often zero. The old null-coalescing expression treated that zero as
        // a real value, which made both equipment and rune box filters ineffective.
        var definitionQuality = ReadNullableInt(definition, "quality") ?? 0;
        if (definitionQuality > 0) return definitionQuality;

        var savedQuality = ReadNullableInt(save, "quality") ?? 0;
        if (savedQuality > 0) return savedQuality;

        // Some older tool stacks were created with their tier in SaveItemData.level.
        var savedLevel = ReadNullableInt(save, "level") ?? 0;
        return savedLevel switch
        {
            1 => 3,
            2 => 4,
            3 => 5,
            4 => 6,
            5 => 8,
            _ => definitionQuality > 0 ? definitionQuality : savedQuality
        };
    }

    private static bool IsRankedQuality(int quality) => quality is >= 1 and <= 8;

    private static string NativeObjectKey(object item, object field)
    {
        var itemPointer = Read(item, "Pointer");
        if (itemPointer is not null) return $"item:{itemPointer}";
        var fieldPointer = Read(field, "Pointer");
        if (fieldPointer is not null) return $"field:{fieldPointer}";
        var save = Read(item, "saveItemData");
        return $"fallback:{ReadNullableInt(save, "id")}:{ReadNullableInt(save, "quality")}:{ReadNullableInt(save, "count")}:{ReadNullableInt(field, "index")}";
    }

    private static ItemSearchRecord DescribeItem(object item, string storageLabel, StorageKind storageKind, StorageSource storageSource, object? sourceField = null, object? groupData = null)
    {
        var save = Read(item, "saveItemData") ?? item;
        var equip = Read(item, "itemEquipData");
        var definition = Read(equip, "tEquipData")
            ?? Read(Read(item, "itemRuneData"), "tRuneData")
            ?? Read(Read(item, "itemResData"), "tResData")
            ?? Read(Read(item, "itemToolData"), "tToolData")
            ?? Read(Read(item, "itemCurioData"), "tCurioData")
            ?? Read(item, "tItemData");
        var runtimeName = InvokeString(item, "GetName");
        var baseLocalizedName = Clean(ReadString(definition, "name") ?? string.Empty);
        // ItemData.GetName is the actual in-game display contract: equipment can
        // be renamed by a runeword and forged items gain their enhancement text.
        // Keep the table name as an additional search synonym.
        var localizedName = FirstNonEmpty(Clean(runtimeName ?? string.Empty), baseLocalizedName, UiText.L("이름 없는 아이템", "Unnamed item", "未命名物品", "未命名物品"));
        var englishName = Clean(EnglishName(definition, baseLocalizedName) ?? baseLocalizedName);
        var quality = ReadNullableInt(save, "quality") ?? 0;
        var qualityData = Read(item, "tItemQualityData");
        var qualityName = Clean(ReadString(qualityData, "name") ?? QualityLabel(quality));
        var englishQualityName = Clean(EnglishName(qualityData, qualityName) ?? qualityName);
        var partId = ReadNullableInt(definition, "part");
        var subtypeId = ReadNullableInt(definition, "minType");
        var part = partId is > 0 ? InvokeStatic("TableData", "getTEquipPartData", partId.Value) : null;
        var subtype = subtypeId is > 0 ? InvokeStatic("TableData", "getTWeaponTypeData", subtypeId.Value) : null;
        var partName = Clean(ReadString(part, "name") ?? UiText.L("장비", "Gear", "装备", "裝備"));
        var englishPartName = Clean(EnglishName(part, partName) ?? partName);
        var subtypeName = Clean(ReadString(subtype, "name") ?? string.Empty);
        var englishSubtypeName = Clean(EnglishName(subtype, subtypeName) ?? subtypeName);

        var affixObjects = new List<object>();
        var affixKeys = new HashSet<string>(StringComparer.Ordinal);
        // Use the same complete affix source as Auto Gear: ordinary affixes,
        // every socket rune's affixList, and the runeword affix. The old
        // tooltip/search path silently omitted socket-rune options.
        foreach (var affix in CollectEquipmentAffixes(item)) AddAffix(affix);
        var itemLevel = ReadNullableInt(save, "level") ?? 0;
        var affixes = affixObjects.Select(affix => DescribeAffix(affix, itemLevel))
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key)
            .ToList();
        var affixSummary = string.Join("  ·  ", affixes);
        var multilingualAffixSearch = Clean(string.Join(" ", affixObjects.Select(GetAffixSearchText)));

        void AddAffix(object? value)
        {
            if (value is null) return;
            var key = Read(value, "Pointer") is { } pointer
                ? $"pointer:{pointer}"
                : $"object:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)}";
            if (affixKeys.Add(key)) affixObjects.Add(value);
        }
        var setData = Read(equip, "tEquipSetsData");
        var setId = ReadNullableInt(setData, "id") ?? 0;
        var localizedSetName = Clean(ReadString(setData, "name") ?? string.Empty);
        var englishSetName = Clean(EnglishName(setData, localizedSetName) ?? localizedSetName);
        var setName = FirstNonEmpty(localizedSetName, englishSetName);
        var setJobId = ReadNullableInt(setData, "jobId") ?? 0;
        var setJobRow = setJobId > 0 ? InvokeStatic("TableData", "getTHeroJobData", setJobId) : null;
        var setJob = setJobId <= 0
            ? UiText.L("모든 직업", "All classes", "所有职业", "所有職業")
            : Clean(ReadString(setJobRow, "name") ?? EnglishName(setJobRow, UiText.L($"직업 {setJobId}", $"Class {setJobId}", $"职业 {setJobId}", $"職業 {setJobId}")) ?? string.Empty);
        var englishSetJob = setJobId <= 0 ? "All classes" : Clean(EnglishName(setJobRow, setJob) ?? setJob);
        var setMemberRows = ReadList(Read(equip, "setsNameList")).Select(member => Read(member, "tEquipData")).Where(member => member is not null).ToList();
        if (setMemberRows.Count == 0 && setId > 0)
            setMemberRows = ReadValues(ReadStatic("TableData", "TEquipDict")).Where(member => ReadNullableInt(member, "setsId") == setId).Cast<object?>().ToList();
        var setMembers = setMemberRows.Select(member => Clean(ReadString(member, "name") ?? EnglishName(member, ReadString(member, "name")) ?? string.Empty))
            .Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var englishSetMembers = setMemberRows.Select(member => Clean(EnglishName(member, ReadString(member, "name")) ?? string.Empty))
            .Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var setWearCounts = setId > 0 ? GetSetEffectWearCounts(setId) : new Dictionary<int, int>();
        var setBonusRows = setId <= 0
            ? new List<object>()
            : ReadValues(ReadStatic("TableData", "TEquipSetsEffectDict"))
                .Where(effect => ReadNullableInt(effect, "sesId") == setId)
                .Where(effect => setWearCounts.Count == 0
                                 || setWearCounts.ContainsKey(ReadNullableInt(effect, "id") ?? 0))
                .OrderBy(effect => ReadNullableInt(effect, "index") ?? int.MaxValue).ToList();
        var multilingualSetBonusSearch = new List<string>();
        var setBonuses = setBonusRows.Select(effect =>
        {
            var effectId = ReadNullableInt(effect, "id") ?? 0;
            var effectIndex = ReadNullableInt(effect, "index") ?? int.MaxValue;
            var required = setWearCounts.TryGetValue(effectId, out var exactRequired)
                ? exactRequired
                : Math.Max(2, effectIndex == int.MaxValue ? 2 : effectIndex * 2);
            var current = Clean(ReadString(effect, "des") ?? string.Empty);
            var english = Clean(EnglishText(effect, "_des", current) ?? current);
            if (!string.IsNullOrWhiteSpace(current)) multilingualSetBonusSearch.Add(current);
            if (!string.IsNullOrWhiteSpace(english)) multilingualSetBonusSearch.Add(english);
            var text = FirstNonEmpty(current, english, UiText.L("세트 효과", "Set bonus", "套装效果", "套裝效果"));
            return $"• {(required > 0 ? UiText.L($"[{required}세트] ", $"[{required} pieces] ", $"[{required}件] ", $"[{required}件] ") : string.Empty)}{text}";
        }).Where(value => value.Length > 0).ToList();
        var currentSetDescription = Clean(ReadString(setData, "des") ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(currentSetDescription)) multilingualSetBonusSearch.Add(currentSetDescription);
        var setDescription = currentSetDescription;
        if (!string.IsNullOrWhiteSpace(setDescription)) setBonuses.Insert(0, $"• {setDescription}");
        var setMembersText = setMembers.Count == 0 ? UiText.L("• 구성 장비 정보 없음", "• No set-piece information", "• 无套装部件信息", "• 無套裝部件資訊") : "• " + string.Join("\n• ", setMembers);
        var setBonusesText = setBonuses.Count == 0 ? UiText.L("• 세트 효과 정보 없음", "• No set-bonus information", "• 无套装效果信息", "• 無套裝效果資訊") : string.Join("\n", setBonuses);
        var localizedDescription = Clean(ReadString(definition, "des") ?? string.Empty);
        var englishDescription = Clean(EnglishText(definition, "_des", localizedDescription) ?? localizedDescription);
        var mainAttributeDescription = Clean(InvokeString(equip ?? item, "GetMainAttrDesc") ?? string.Empty);
        var corruptInfo = Clean(InvokeString(save, "GetCorruptInfoText") ?? string.Empty);
        var corruptDescription = string.IsNullOrWhiteSpace(corruptInfo)
            ? string.Empty
            : $"{UiText.L("타락", "Corruption", "腐化", "腐化")} · {corruptInfo}";
        var descriptions = new[] { localizedDescription, englishDescription, mainAttributeDescription, corruptDescription }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
        var description = string.Join("\n", descriptions);
        var storageSearchTerms = storageSource switch
        {
            StorageSource.Inventory => "inventory bag 인벤토리 가방 背包",
            StorageSource.Warehouse => "warehouse storage 창고 仓库 倉庫",
            StorageSource.Treasure => "vault treasure storage 보관함 볼트 宝库 寶庫",
            StorageSource.Equipped => "equipped equipment 장착 装备中 裝備中",
            _ => string.Empty
        };

        var synonyms = QualitySynonyms(quality);
        var searchText = string.Join(" ", new[]
        {
            localizedName, baseLocalizedName, englishName, qualityName, englishQualityName, synonyms, partName, englishPartName, subtypeName, englishSubtypeName, storageLabel,
            storageSearchTerms,
            Read(save, "type")?.ToString() ?? string.Empty, $"level {ReadNullableInt(save, "level") ?? 0}", description, affixSummary, multilingualAffixSearch,
            setName, englishSetName, setJob, englishSetJob, string.Join(" ", setMembers), string.Join(" ", englishSetMembers), string.Join(" ", setBonuses), string.Join(" ", multilingualSetBonusSearch)
        });
        return new ItemSearchRecord
        {
            Name = localizedName,
            Quality = quality,
            QualityLabel = string.IsNullOrWhiteSpace(qualityName) ? QualityLabel(quality) : qualityName,
            Level = ReadNullableInt(save, "level"),
            PartName = string.IsNullOrWhiteSpace(subtypeName) ? partName : $"{partName} · {subtypeName}",
            StorageLabel = storageLabel,
            StorageKind = storageKind,
            AffixSummary = affixSummary,
            AffixSearchText = Clean(string.Join(" ", new[] { affixSummary, multilingualAffixSearch, mainAttributeDescription, corruptDescription, setName, englishSetName, setJob, englishSetJob, string.Join(" ", setBonuses), string.Join(" ", multilingualSetBonusSearch) })),
            Description = description,
            SetName = setName,
            SetJob = setJob,
            SetMembers = setMembersText,
            SetBonuses = setBonusesText,
            SearchText = Clean(searchText),
            ItemData = item,
            SourceField = sourceField,
            GroupData = groupData,
            StorageSource = storageSource
        };
    }

    public static bool TryTransfer(ItemSearchRecord item, out string message)
    {
        try
        {
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData");
            var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
                .Select(house => Read(house, "houseStoreData"))
                .FirstOrDefault(store => Read(store, "storeBaseData") is not null || Read(store, "storeTreaData") is not null);
            if (houseStoreData is null)
            {
                message = UiText.L("창고 데이터를 찾지 못했습니다.", "Storage data was not found.", "未找到仓库数据。", "找不到倉庫資料。");
                return false;
            }

            switch (item.StorageSource)
            {
                case StorageSource.Inventory:
                    if (item.SourceField is null || !NativeEquals(Read(item.SourceField, "itemData"), item.ItemData))
                    {
                        message = UiText.L("아이템이 더 이상 표시된 인벤토리 칸에 없습니다. 새로고침 후 다시 시도하세요.", "The item is no longer in the displayed inventory slot. Refresh and try again.", "物品已不在显示的背包格中。请刷新后重试。", "物品已不在顯示的背包格中。請重新整理後再試。");
                        return false;
                    }
                    InvokeStaticMany("ItemSys", "QuickMoveItemFromBagToStore", item.ItemData);
                    if (Read(item.SourceField, "itemData") is not null)
                    {
                        message = UiText.L("게임의 자동 창고 이동이 적용되지 않았습니다. 창고 또는 Vault 공간을 확인하세요.", "The game's automatic storage move failed. Check warehouse or Vault space.", "游戏自动入库失败。请检查仓库或宝库空间。", "遊戲自動入庫失敗。請檢查倉庫或寶庫空間。");
                        return false;
                    }
                    var storedRecord = ReadAll(true).FirstOrDefault(record =>
                        record.StorageSource is StorageSource.Warehouse or StorageSource.Treasure
                        && NativeEquals(record.ItemData, item.ItemData));
                    if (storedRecord is null)
                    {
                        message = UiText.L("인벤토리에서는 사라졌지만 창고·Vault에서 확인되지 않아 이동 완료로 처리하지 않았습니다.", "The item left the bag but was not found in warehouse/Vault, so the move was not marked complete.", "物品已离开背包，但未在仓库/宝库中找到，因此未标记为完成。", "物品已離開背包，但未在倉庫/寶庫中找到，因此未標記為完成。");
                        return false;
                    }
                    message = storedRecord.StorageSource != StorageSource.Treasure
                        ? UiText.L($"{item.Name} → 일반 창고 이동 완료", $"{item.Name} → moved to warehouse", $"{item.Name} → 已移至仓库", $"{item.Name} → 已移至倉庫")
                        : UiText.L($"{item.Name} → Vault 이동 완료", $"{item.Name} → moved to Vault", $"{item.Name} → 已移至宝库", $"{item.Name} → 已移至寶庫");
                    return true;

                case StorageSource.Warehouse:
                    if (item.SourceField is null || !NativeEquals(Read(item.SourceField, "itemData"), item.ItemData))
                    {
                        message = UiText.L("아이템이 더 이상 표시된 창고 칸에 없습니다. 새로고침 후 다시 시도하세요.", "The item is no longer in the displayed warehouse slot. Refresh and try again.", "物品已不在显示的仓库格中。请刷新后重试。", "物品已不在顯示的倉庫格中。請重新整理後再試。");
                        return false;
                    }
                    InvokeStaticMany("ItemSys", "QuickMoveItemFromStoreToBag", item.ItemData);
                    var warehouseBagField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item.ItemData);
                    if (Read(item.SourceField, "itemData") is not null || warehouseBagField is null
                        || !NativeEquals(Read(warehouseBagField, "itemData"), item.ItemData))
                    {
                        message = UiText.L("인벤토리가 가득 찼거나 현재 꺼낼 수 없습니다.", "The inventory is full or the item cannot be taken now.", "背包已满或当前无法取出该物品。", "背包已滿或目前無法取出該物品。");
                        return false;
                    }
                    message = UiText.L($"{item.Name} → 인벤토리 이동 완료", $"{item.Name} → moved to inventory", $"{item.Name} → 已移至背包", $"{item.Name} → 已移至背包");
                    return true;

                case StorageSource.Treasure:
                    if (Read(houseStoreData, "storeTreaData") is null || item.GroupData is null)
                    {
                        message = UiText.L("Vault의 아이템 그룹을 찾지 못했습니다.", "The Vault item group was not found.", "未找到宝库物品组。", "找不到寶庫物品群組。");
                        return false;
                    }
                    if (!IsItemInVaultGroup(item.ItemData, item.GroupData))
                    {
                        message = UiText.L("아이템이 더 이상 표시된 Vault 그룹에 없습니다. 새로고침 후 다시 시도하세요.", "The item is no longer in the displayed Vault group. Refresh and try again.", "物品已不在显示的宝库组中。请刷新后重试。", "物品已不在顯示的寶庫群組中。請重新整理後再試。");
                        return false;
                    }
                    // Use the public game wrapper rather than StoreTreaData's
                    // internal data-only call. It performs the take and emits the
                    // same bag/storage UI events and audio as a normal quick move.
                    InvokeStaticMany("ItemSys", "QuickMoveTreasureEquipToBag", item.GroupData, item.ItemData);
                    var vaultBagField = InvokeStatic("ItemSys", "FindLordInventoryFieldByItem", item.ItemData);
                    if (vaultBagField is null || !NativeEquals(Read(vaultBagField, "itemData"), item.ItemData))
                    {
                        message = UiText.L("인벤토리가 가득 찼거나 Vault 아이템을 꺼낼 수 없습니다.", "The inventory is full or the Vault item could not be taken.", "背包已满或无法取出宝库物品。", "背包已滿或無法取出寶庫物品。");
                        return false;
                    }
                    message = UiText.L($"{item.Name} → 인벤토리 이동 완료", $"{item.Name} → moved to inventory", $"{item.Name} → 已移至背包", $"{item.Name} → 已移至背包");
                    return true;

                default:
                    message = UiText.L("알 수 없는 보관 위치입니다.", "Unknown storage location.", "未知的存放位置。", "未知的存放位置。");
                    return false;
            }
        }
        catch (Exception error)
        {
            message = UiText.L($"이동 실패: {error.GetBaseException().Message}", $"Move failed: {error.GetBaseException().Message}", $"移动失败：{error.GetBaseException().Message}", $"移動失敗：{error.GetBaseException().Message}");
            return false;
        }
    }

    private static string DescribeAffix(object affix, int itemLevel)
    {
        var save = Read(affix, "saveData") ?? affix;
        var runtimeAffix = ResolveRuntimeAffix(affix);
        var id = ReadNullableInt(save, "id") ?? ReadNullableInt(affix, "id");
        var definition = Read(runtimeAffix, "tAffixData") ?? (id is > 0 ? InvokeStatic("TableData", "getTAffixData", id.Value) : null);
        var display = Clean(InvokeString(runtimeAffix, "GetDesc") ?? string.Empty);
        var current = Clean(ReadString(definition, "des") ?? string.Empty);
        var english = Clean(EnglishText(definition, "_des", current) ?? current);
        var rank = ReadNullableInt(save, "level");
        var description = FirstNonEmpty(display, current, english, id is null ? string.Empty : UiText.L($"옵션 {id.Value}", $"Affix {id.Value}", $"词缀 {id.Value}", $"詞綴 {id.Value}"));
        return rank is > 0 ? $"R{rank.Value} {description}" : description;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    private static bool IsEquipmentItem(object? item) => item is not null && string.Equals(Read(Read(item, "saveItemData"), "type")?.ToString(), "equip", StringComparison.OrdinalIgnoreCase);

    private static string QualityLabel(int quality) => quality switch
    {
        1 => UiText.L("일반", "Common", "普通", "普通"),
        2 => UiText.L("고급", "Fine", "精良", "精良"),
        3 => UiText.L("희귀", "Rare", "稀有", "稀有"),
        4 => UiText.L("전설", "Legendary", "传奇", "傳奇"),
        5 => UiText.L("신화", "Mythic", "神话", "神話"),
        6 => UiText.L("세트", "Set", "套装", "套裝"),
        7 => UiText.L("마법", "Magic", "魔法", "魔法"),
        8 => UiText.L("고유", "Unique", "独特", "獨特"),
        _ => UiText.L("기타", "Other", "其他", "其他")
    };

    private static string QualitySynonyms(int quality) => quality switch
    {
        1 => "일반 common 普通", 2 => "고급 fine 精良", 3 => "희귀 rare 稀有", 4 => "전설 legendary 传奇 傳奇", 5 => "신화 mythic 神话 神話", 6 => "세트 set 套装 套裝", 7 => "마법 magic 魔法", 8 => "고유 unique 独特 獨特", _ => "기타 other 其他"
    };

    private static string Clean(string value) => Regex.Replace(RichText.Replace(value ?? string.Empty, " "), "\\s+", " ").Trim();

    private static IEnumerable<object> ReadValues(object? collection)
    {
        if (collection is null) yield break;
        var values = Read(collection, "Values") ?? collection;
        foreach (var value in Enumerate(values)) yield return value;
    }

    private static IEnumerable<object> ReadEntries(object? dictionary)
    {
        if (dictionary is null) yield break;
        foreach (var value in Enumerate(dictionary)) yield return value;
    }

    private static IEnumerable<object> Enumerate(object value)
    {
        var getEnumerator = value.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
        if (getEnumerator is null) yield break;
        var enumerator = getEnumerator.Invoke(value, null);
        if (enumerator is null) yield break;
        var moveNext = enumerator.GetType().GetMethod("MoveNext", Type.EmptyTypes);
        var current = enumerator.GetType().GetProperty("Current");
        for (var guard = 0; guard < 20000 && moveNext is not null && current is not null && (bool)(moveNext.Invoke(enumerator, null) ?? false); guard++)
        {
            var item = current.GetValue(enumerator);
            if (item is not null) yield return item;
        }
    }

    private static IEnumerable<object> ReadList(object? list)
    {
        if (list is null) yield break;
        var getItem = list.GetType().GetMethod("get_Item", new[] { typeof(int) });
        if (getItem is null) yield break;
        var count = ReadNullableInt(list, "Count") ?? ReadNullableInt(list, "Length") ?? 0;
        for (var index = 0; index < count; index++)
        {
            object? value = null;
            try { value = getItem.Invoke(list, new object[] { index }); } catch { }
            if (value is not null) yield return value;
        }
    }

    private static object? ReadStatic(string typeName, string property)
    {
        try { return GameType(typeName)?.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null); }
        catch { return null; }
    }

    private static object? Read(object? value, string name)
    {
        if (value is null) return null;
        try { return value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value); }
        catch { return null; }
    }

    private static object? ReadRequiredProperty(object value, string name)
    {
        var property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? throw new MissingMemberException(value.GetType().FullName, name);
        try { return property.GetValue(value); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException($"Could not read {value.GetType().Name}.{name}: {error.InnerException.Message}", error.InnerException);
        }
    }

    private static int ReadRequiredIntProperty(object value, string name)
        => Convert.ToInt32(ReadRequiredProperty(value, name)
                           ?? throw new InvalidOperationException($"{value.GetType().Name}.{name} is null."), CultureInfo.InvariantCulture);

    private static bool ReadRequiredBoolProperty(object value, string name)
        => Convert.ToBoolean(ReadRequiredProperty(value, name)
                             ?? throw new InvalidOperationException($"{value.GetType().Name}.{name} is null."), CultureInfo.InvariantCulture);

    private static void Write(object value, string name, object? newValue)
    {
        var property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? throw new MissingMemberException(value.GetType().FullName, name);
        if (!property.CanWrite) throw new InvalidOperationException($"{value.GetType().Name}.{name} is read-only.");
        property.SetValue(value, newValue);
    }

    private static object? InvokeStatic(string typeName, string method, object argument)
    {
        try { return GameType(typeName)?.GetMethod(method, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { argument }); }
        catch { return null; }
    }

    private static object? InvokeStaticMany(string typeName, string method, params object[] arguments)
    {
        try
        {
            var candidate = GameType(typeName)?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(entry => entry.Name == method && entry.GetParameters().Length == arguments.Length);
            return candidate?.Invoke(null, arguments);
        }
        catch { return null; }
    }

    private static object? InvokeRequiredStaticMany(string typeName, string method, params object[] arguments)
    {
        var type = GameType(typeName) ?? throw new MissingMemberException($"Game type {typeName} is unavailable.");
        var candidate = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(entry => entry.Name == method && entry.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(typeName, method);
        try
        {
            return candidate.Invoke(null, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException($"{typeName}.{method} failed: {error.InnerException.Message}", error.InnerException);
        }
    }

    private static object? InvokeInstance(object value, string method, params object[] arguments)
    {
        try
        {
            var candidate = value.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(entry => entry.Name == method && entry.GetParameters().Length == arguments.Length);
            return candidate?.Invoke(value, arguments);
        }
        catch { return null; }
    }

    private static object? InvokeRequiredInstance(object value, string method, params object[] arguments)
    {
        var candidate = value.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(entry => entry.Name == method && entry.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(value.GetType().FullName, method);
        try
        {
            return candidate.Invoke(value, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException($"{value.GetType().Name}.{method} failed: {error.InnerException.Message}", error.InnerException);
        }
    }

    private static string? EnglishName(object? row, string? fallback)
    {
        if (row is null) return fallback;
        var direct = ReadString(row, "name_en");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        var key = ReadString(row, "_name") ?? ReadString(row, "_name_k__BackingField") ?? ReadString(row, "__name_k__BackingField");
        if (string.IsNullOrWhiteSpace(key)) return fallback;
        var translation = InvokeStatic("TableData", "getTLanguage_MultiLangData", key);
        return ReadString(translation, "en") is { Length: > 0 } english ? english : fallback;
    }

    private static string? EnglishText(object? row, string rawProperty, string? fallback)
    {
        var direct = ReadString(row, "des_en");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        var key = ReadString(row, rawProperty) ?? ReadString(row, rawProperty + "_k__BackingField");
        if (string.IsNullOrWhiteSpace(key)) return fallback;
        var translation = InvokeStatic("TableData", "getTLanguage_MultiLangData", key);
        return ReadString(translation, "en") is { Length: > 0 } english ? english : fallback;
    }

    private static string? InvokeString(object value, string method)
    {
        try { return value.GetType().GetMethod(method)?.Invoke(value, null)?.ToString(); }
        catch { return null; }
    }

    private static int? ReadNullableInt(object? value, string name)
    {
        try { return Read(value, name) is { } raw ? Convert.ToInt32(raw, CultureInfo.InvariantCulture) : null; }
        catch { return null; }
    }

    private static string? ReadString(object? value, string name) => Read(value, name)?.ToString();

    private static Type? GameType(string name)
    {
        gameAssembly ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
        return gameAssembly?.GetType(name, false, false);
    }
}
