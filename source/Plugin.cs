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
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PathOfIdleInGameSearch;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "local.pathofidle.ingame-search";
    public const string PluginName = "Path of Idle In-Game Search";
    public const string PluginVersion = "1.1.4";

#if PATHOFIDLE_DIAGNOSTICS
    private static ManualLogSource DiagnosticsLogger { get; set; } = null!;
#endif
    internal static ConfigEntry<string> SavedQuery { get; private set; } = null!;
    internal static ConfigEntry<bool> IncludeWarehouse { get; private set; } = null!;
    internal static ConfigEntry<float> WindowX { get; private set; } = null!;
    internal static ConfigEntry<float> WindowY { get; private set; } = null!;
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
        AutoTransformSkills = Config.Bind("AutoBuild", "TransformMissingSkills", true, "Use the shrine's normal paid skill transformation to seek missing performance-plan skills before allocating points.");
        AutoTransformMaxAttempts = Config.Bind("AutoBuild", "MaxSkillTransformAttempts", 12, "Maximum paid skill transformations per automatic skill run.");
        InstallWheelPatch();
        AddComponent<InGameSearchOverlay>();
        DiagInfo($"{PluginName} {PluginVersion} loaded. Press F3 or Ctrl+F to open it.");
    }

    // Diagnostic calls are removed completely from public builds, including
    // argument evaluation and interpolated-string construction. Developers can
    // opt in locally by defining PATHOFIDLE_DIAGNOSTICS at compile time.
    [Conditional("PATHOFIDLE_DIAGNOSTICS")]
    internal static void DiagInfo(string message)
    {
#if PATHOFIDLE_DIAGNOSTICS
        DiagnosticsLogger.LogInfo(message);
#endif
    }

    [Conditional("PATHOFIDLE_DIAGNOSTICS")]
    internal static void DiagWarning(string message)
    {
#if PATHOFIDLE_DIAGNOSTICS
        DiagnosticsLogger.LogWarning(message);
#endif
    }

    [Conditional("PATHOFIDLE_DIAGNOSTICS")]
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
    private const float WindowWidth = 720f;
    private const float WindowHeight = 780f;
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
    private bool dragging;
    private Vector2 dragOffset;
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
        windowRect = new Rect(Plugin.WindowX.Value, Plugin.WindowY.Value, WindowWidth, WindowHeight);
        var configuredSpeed = Plugin.GameSpeed.Value;
        currentSpeed = float.IsFinite(configuredSpeed) ? Mathf.Clamp(configuredSpeed, 0.1f, 100f) : 1f;
        speedInput = currentSpeed.ToString("0.##", CultureInfo.InvariantCulture);
        ApplyGameSpeed();
        ClampWindowToScreen();
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
        EnsureStyles();
        ClampWindowToScreen();
        var pointerOverTransfer = visibleTransferRects.Any(rect => rect.Contains(Event.current.mousePosition));
        var tooltipOwnsPointer = !pointerOverTransfer && hoveredItem is not null && activeTooltipRect.Contains(Event.current.mousePosition);
        if (pointerOverTransfer)
        {
            hoveredItem = null;
            tooltipItemKey = string.Empty;
            activeTooltipRect = default;
        }
        visibleTransferRects.Clear();
        if (!tooltipOwnsPointer) HandleWindowDrag();
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
        var left = windowRect.x + 18f;
        var width = windowRect.width - 36f;
        var pageTitle = selectedPage switch
        {
            OverlayPage.BulkOpen => UiText.L("Path of Idle · 일괄 개봉", "Path of Idle · Bulk Open", "Path of Idle · 批量开启", "Path of Idle · 批次開啟"),
            OverlayPage.AutoBuild => UiText.L("Path of Idle · 자동 빌드", "Path of Idle · Auto Build", "Path of Idle · 自动配装", "Path of Idle · 自動配裝"),
            _ => UiText.L("Path of Idle · 아이템 검색", "Path of Idle · Item Search", "Path of Idle · 物品搜索", "Path of Idle · 物品搜尋")
        };
        GUI.Label(new Rect(left, windowRect.y + 12f, width - 382f, 30f), pageTitle, titleStyle!);
        if (GUI.Button(new Rect(windowRect.xMax - 368f, windowRect.y + 11f, 62f, 29f), LanguageButtonLabel(), compactButtonStyle!)) CycleUiLanguage();
        if (GUI.Button(new Rect(windowRect.xMax - 300f, windowRect.y + 11f, 32f, 29f), "−", buttonStyle!)) ChangeGameSpeed(-1);
        var speedRect = new Rect(windowRect.xMax - 262f, windowRect.y + 11f, 62f, 29f);
        if (!tooltipOwnsPointer) HandleSpeedInput(speedRect);
        GUI.Box(speedRect, GUIContent.none, searchStyle!);
        GUI.Label(new Rect(speedRect.x + 5f, speedRect.y + 4f, speedRect.width - 10f, 22f), speedInput + (focusSpeedInput ? "|" : string.Empty) + "×", badgeStyle!);
        if (GUI.Button(new Rect(windowRect.xMax - 194f, windowRect.y + 11f, 32f, 29f), "+", buttonStyle!)) ChangeGameSpeed(1);
        if (GUI.Button(new Rect(windowRect.xMax - 156f, windowRect.y + 11f, 50f, 29f), UiText.L("적용", "Apply", "应用", "套用"), compactButtonStyle!)) ApplyCustomSpeed();
        if (GUI.Button(new Rect(windowRect.xMax - 100f, windowRect.y + 11f, 42f, 29f), "1×", buttonStyle!)) SetGameSpeed(1f);
        if (GUI.Button(new Rect(windowRect.xMax - 50f, windowRect.y + 10f, 34f, 30f), "×", closeStyle!)) SetVisible(false);
        GUI.Label(new Rect(left, windowRect.y + 43f, 280f, 22f), UiText.L(
            "F3 / Ctrl+F · 공백 AND · | OR · - 제외",
            "F3 / Ctrl+F · space AND · | OR · - exclude",
            "F3 / Ctrl+F · 空格 AND · | OR · - 排除",
            "F3 / Ctrl+F · 空格 AND · | OR · - 排除"), hintStyle!);
        DrawPageTabs();

        if (selectedPage == OverlayPage.BulkOpen)
        {
            DrawBulkOpenPanel(new Rect(left, windowRect.y + 78f, width, 400f));
            GUI.enabled = mainPanelEnabled;
            ConsumeRemainingKeyboardEvent();
            return;
        }

        if (selectedPage == OverlayPage.AutoBuild)
        {
            DrawAutoBuildPanel(new Rect(left, windowRect.y + 78f, width, 610f));
            GUI.enabled = mainPanelEnabled;
            ConsumeRemainingKeyboardEvent();
            return;
        }

        var searchRect = new Rect(left, windowRect.y + 70f, width, 38f);
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
        GUI.Label(new Rect(searchRect.x + 10f, searchRect.y + 7f, searchRect.width - 20f, 24f), searchDisplay, searchTextStyle!);

        var nextWarehouse = GUI.Toggle(new Rect(left, windowRect.y + 116f, 158f, 26f), includeWarehouse, UiText.L(" 창고·보관함 포함", " Include warehouse/vault", " 包含仓库/宝库", " 包含倉庫/寶庫"), toggleStyle!);
        if (nextWarehouse != includeWarehouse)
        {
            includeWarehouse = nextWarehouse;
            Plugin.IncludeWarehouse.Value = includeWarehouse;
            if (!includeWarehouse) selectedStorage = StorageKind.Inventory;
            currentPage = 0;
            RefreshItems();
        }
        if (GUI.Button(new Rect(left + 164f, windowRect.y + 114f, 88f, 28f), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshItems();
        if (GUI.Button(new Rect(left + 258f, windowRect.y + 114f, 105f, 28f), UiText.L("검색어 지우기", "Clear search", "清除搜索", "清除搜尋"), compactButtonStyle!))
        {
            query = string.Empty;
            searchCaret = 0;
            Plugin.SavedQuery.Value = query;
            focusSearch = selectedPage == OverlayPage.Search;
            currentPage = 0;
            ApplyFilter();
        }
        GUI.Label(new Rect(left + 372f, windowRect.y + 114f, width - 372f, 34f), status, hintStyle!);

        DrawQualityFilters(new Rect(left, windowRect.y + 150f, width, 70f));
        var inventoryCount = 0;
        var warehouseCount = 0;
        var selectedMatches = new List<ItemSearchRecord>();
        foreach (var item in matches)
        {
            if (item.StorageKind == StorageKind.Inventory) inventoryCount++; else warehouseCount++;
            if (item.StorageKind == selectedStorage) selectedMatches.Add(item);
        }
        DrawStorageTab(new Rect(left, windowRect.y + 230f, 190f, 32f), StorageKind.Inventory, $"{UiText.L("인벤토리", "INVENTORY", "背包", "背包")}  {inventoryCount}", new Color(0.32f, 0.86f, 0.46f));
        DrawStorageTab(new Rect(left + 198f, windowRect.y + 230f, 190f, 32f), StorageKind.Warehouse, $"{UiText.L("창고", "WAREHOUSE", "仓库", "倉庫")}  {warehouseCount}", new Color(0.25f, 0.78f, 0.92f));

        var resultAreaHeight = Math.Max(76f, windowRect.yMax - 52f - (windowRect.y + 272f));
        var resultsPerPage = Math.Max(1, Math.Min(6, (int)Math.Floor(resultAreaHeight / 76f)));
        var pageCount = Math.Max(1, (int)Math.Ceiling(selectedMatches.Count / (double)resultsPerPage));
        currentPage = Math.Max(0, Math.Min(currentPage, pageCount - 1));
        var resultArea = new Rect(left, windowRect.y + 272f, width, resultAreaHeight);
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
            GUI.Label(new Rect(left + 12f, windowRect.y + 304f, width - 24f, 40f), allItems.Count == 0
                ? UiText.L("인벤토리 데이터를 기다리는 중입니다.", "Waiting for inventory data.", "正在等待背包数据。", "正在等待背包資料。")
                : UiText.L("이 구역에는 검색 조건에 맞는 아이템이 없습니다.", "No matching items in this section.", "此区域没有匹配的物品。", "此區域沒有相符的物品。"), hintStyle!);
        }
        else
        {
            var pageItems = selectedMatches.Skip(currentPage * resultsPerPage).Take(resultsPerPage).ToList();
            for (var index = 0; index < pageItems.Count; index++)
                DrawResult(pageItems[index], new Rect(left, windowRect.y + 272f + index * 76f, width, 70f));
        }

        if (GUI.Button(new Rect(left, windowRect.yMax - 43f, 88f, 28f), UiText.L("◀ 이전", "◀ Previous", "◀ 上一页", "◀ 上一頁"), compactButtonStyle!) && currentPage > 0) currentPage--;
        GUI.Label(new Rect(left + 94f, windowRect.yMax - 43f, 90f, 28f), $"{currentPage + 1} / {pageCount}", pageStyle!);
        if (GUI.Button(new Rect(left + 190f, windowRect.yMax - 43f, 88f, 28f), UiText.L("다음 ▶", "Next ▶", "下一页 ▶", "下一頁 ▶"), compactButtonStyle!) && currentPage + 1 < pageCount) currentPage++;
        GUI.Label(new Rect(left + 294f, windowRect.yMax - 40f, width - 294f, 24f), UiText.L("검색어가 있으면 일치한 아이템만 표시됩니다.", "A search shows matching items only.", "输入搜索词后仅显示匹配物品。", "輸入搜尋詞後僅顯示相符物品。"), hintStyle!);

        GUI.enabled = mainPanelEnabled;
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
        var y = windowRect.y + 42f;
        var x = windowRect.xMax - 394f;
        DrawPageTab(new Rect(x, y, 122f, 27f), OverlayPage.Search, UiText.L("아이템 검색", "Item Search", "物品搜索", "物品搜尋"));
        DrawPageTab(new Rect(x + 128f, y, 122f, 27f), OverlayPage.BulkOpen, UiText.L("일괄 개봉", "Bulk Open", "批量开启", "批次開啟"));
        DrawPageTab(new Rect(x + 256f, y, 122f, 27f), OverlayPage.AutoBuild, UiText.L("자동 빌드", "Auto Build", "自动配装", "自動配裝"));
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
        var header = new Rect(windowRect.x, windowRect.y, windowRect.width - 382f, 40f);
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

    private void ClampWindowToScreen()
    {
        // Keep the entire overlay usable at 720p instead of leaving the bottom
        // navigation outside the screen. Search rows are sized dynamically below.
        windowRect.width = Math.Min(WindowWidth, Math.Max(1f, Screen.width - 20f));
        windowRect.height = Math.Min(WindowHeight, Math.Max(1f, Screen.height - 20f));
        var maxX = Math.Max(0f, Screen.width - windowRect.width);
        var maxY = Math.Max(0f, Screen.height - windowRect.height);
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, maxX);
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, maxY);
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
        GUI.Label(new Rect(rect.x + 10f, rect.y + 5f, rect.width - 260f, 22f), HighlightMatches(item.Name, item.StorageKind), resultNameStyle!);
        GUI.Label(new Rect(rect.xMax - 244f, rect.y + 5f, 98f, 22f), item.QualityLabel, badgeStyle!);
        var transferLabel = item.StorageKind == StorageKind.Inventory
            ? UiText.L("창고로 이동", "Move to storage", "移至仓库", "移至倉庫")
            : UiText.L("인벤토리로 이동", "Move to inventory", "移至背包", "移至背包");
        var transferRect = new Rect(rect.xMax - 140f, rect.y + 4f, 130f, 28f);
        visibleTransferRects.Add(transferRect);
        var previousEnabled = GUI.enabled;
        if (tooltipOwnsPointer) GUI.enabled = false;
        if (GUI.Button(transferRect, transferLabel, compactButtonStyle!)) TransferItem(item);
        GUI.enabled = previousEnabled;
        var level = item.Level is > 0 ? $"Lv.{item.Level}" : UiText.L("레벨 미상", "Unknown level", "等级未知", "等級未知");
        GUI.Label(new Rect(rect.x + 10f, rect.y + 27f, rect.width - 150f, 19f), HighlightMatches($"{item.StorageLabel}  ·  {item.PartName}  ·  {level}", item.StorageKind), resultMetaStyle!);
        var optionPreview = string.IsNullOrWhiteSpace(item.SetName)
            ? item.AffixSummary
            : $"{UiText.L("세트", "Set", "套装", "套裝")} {item.SetName}  ·  {item.AffixSummary}".TrimEnd(' ', '·');
        if (!string.IsNullOrWhiteSpace(optionPreview))
            GUI.Label(new Rect(rect.x + 10f, rect.y + 47f, rect.width - 150f, 18f), HighlightMatches(optionPreview, item.StorageKind), resultAffixStyle!);
        if (!tooltipOwnsPointer && rect.Contains(Event.current.mousePosition) && !transferRect.Contains(Event.current.mousePosition)) hoveredItem = item;
    }

    [HideFromIl2Cpp]
    private void DrawItemTooltip(ItemSearchRecord item)
    {
        var tooltipWidth = Math.Min(620f, Screen.width - 20f);
        var optionText = string.IsNullOrWhiteSpace(item.AffixSummary)
            ? UiText.L("옵션 없음", "No affixes", "无词缀", "無詞綴")
            : "• " + item.AffixSummary.Replace("  ·  ", "\n• ", StringComparison.Ordinal);
        var description = string.IsNullOrWhiteSpace(item.Description) ? UiText.L("별도 아이템 설명 없음", "No item description", "无物品说明", "無物品說明") : item.Description;
        var meta = $"{item.QualityLabel}  ·  {item.StorageLabel}  ·  {item.PartName}  ·  {(item.Level is > 0 ? $"Lv.{item.Level}" : UiText.L("레벨 미상", "Unknown level", "等级未知", "等級未知"))}";
        var setSection = string.IsNullOrWhiteSpace(item.SetName)
            ? string.Empty
            : $"\n\n{UiText.L("세트", "Set", "套装", "套裝")} · {item.SetName}\n{UiText.L("적용 직업", "Class", "适用职业", "適用職業")} · {item.SetJob}\n\n{UiText.L("구성 장비", "Set pieces", "套装部件", "套裝部件")}\n{item.SetMembers}\n\n{UiText.L("세트 효과", "Set bonuses", "套装效果", "套裝效果")}\n{item.SetBonuses}";
        var body = $"{meta}\n\n{UiText.L("설명", "Description", "说明", "說明")}\n{description}\n\n{UiText.L("전체 옵션", "All affixes", "全部词缀", "全部詞綴")}\n{optionText}{setSection}";
        var contentWidth = Math.Max(80f, tooltipWidth - 48f);
        var bodyHeight = tooltipBodyStyle!.CalcHeight(new GUIContent(body), contentWidth);
        var tooltipHeight = Mathf.Clamp(bodyHeight + 58f, 190f, Screen.height - 20f);
        var itemKey = $"{item.StorageLabel}|{item.Name}|{item.Level}|{item.AffixSummary}";
        if (!string.Equals(tooltipItemKey, itemKey, StringComparison.Ordinal))
        {
            tooltipItemKey = itemKey;
            tooltipScroll = Vector2.zero;
            var rightX = windowRect.xMax;
            var leftX = windowRect.x - tooltipWidth;
            var rightSpace = Screen.width - windowRect.xMax;
            var leftSpace = windowRect.x;
            var initialX = rightX + tooltipWidth <= Screen.width - 10f
                ? rightX
                : leftX >= 10f
                    ? leftX
                    : rightSpace >= leftSpace
                        ? Mathf.Clamp(rightX, 10f, Math.Max(10f, Screen.width - tooltipWidth - 10f))
                        : Mathf.Clamp(leftX, 10f, Math.Max(10f, Screen.width - tooltipWidth - 10f));
            var initialY = Mathf.Clamp(Event.current.mousePosition.y - 24f, 10f, Math.Max(10f, Screen.height - tooltipHeight - 10f));
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
        GUI.Label(new Rect(rect.x + 14f, rect.y + 10f, rect.width - 28f, 26f), item.Name, tooltipTitleStyle!);
        var viewport = new Rect(rect.x + 10f, rect.y + 40f, rect.width - 20f, rect.height - 50f);
        var scrollHeight = Math.Max(viewport.height, bodyHeight + 8f);
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
        GUI.Label(new Rect(rect.x + 4f, rect.y + 7f, 54f, 20f), UiText.L("등급", "Quality", "品质", "品質"), utilityTitleStyle!);
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
        var buttonWidth = Math.Max(54f, Math.Min(88f, (rect.width - 76f) / 5f - 6f));
        for (var index = 0; index < entries.Length; index++)
        {
            var row = index / 5;
            var column = index % 5;
            var entry = entries[index];
            DrawQualityButton(new Rect(rect.x + 60f + column * (buttonWidth + 6f), rect.y + 3f + row * 34f, buttonWidth, 28f), entry.Quality, entry.Label);
        }

        var nextOptionsOnly = GUI.Toggle(new Rect(rect.xMax - 128f, rect.y + 39f, 124f, 22f), searchOptionsOnly, UiText.L(" 옵션만 검색", " Affixes only", " 仅搜索词缀", " 僅搜尋詞綴"), toggleStyle!);
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

        GUI.Label(new Rect(rect.x + 18f, rect.y + 14f, rect.width - 36f, 26f), UiText.L("보유 상자 일괄 개봉", "Open Owned Boxes in Bulk", "批量开启持有的箱子", "批次開啟持有的箱子"), titleStyle!);
        GUI.Label(new Rect(rect.x + 18f, rect.y + 44f, rect.width - 36f, 40f), UiText.L(
            "아래 등급은 상자 자체를 거르는 조건입니다. 상자에서 나올 보상 등급을 정하는 기능은 아닙니다.",
            "The quality filter selects the boxes themselves; it does not control reward quality.",
            "下方品质筛选的是箱子本身，并不会决定开出的奖励品质。",
            "下方品質篩選的是箱子本身，並不會決定開出的獎勵品質。"), hintStyle!);

        var panelEnabled = GUI.enabled;
        if (bulkSession is not null) GUI.enabled = false;
        var nextSkip = GUI.Toggle(new Rect(rect.x + 18f, rect.y + 91f, 145f, 24f), Plugin.SkipBulkConfirmation.Value, UiText.L(" 2단계 확인 생략", " Skip second confirm", " 跳过二次确认", " 略過二次確認"), toggleStyle!);
        if (nextSkip != Plugin.SkipBulkConfirmation.Value)
        {
            Plugin.SkipBulkConfirmation.Value = nextSkip;
            armedBulkOpen = BulkToolKind.None;
            status = nextSkip
                ? UiText.L("일괄 개봉 확인을 생략합니다.", "Bulk opening will run with one click.", "批量开启将单击执行。", "批次開啟將單擊執行。")
                : UiText.L("일괄 개봉은 두 번 눌러야 실행됩니다.", "Bulk opening requires two clicks.", "批量开启需要点击两次。", "批次開啟需要點擊兩次。");
        }
        var nextAutoStore = GUI.Toggle(new Rect(rect.x + 180f, rect.y + 91f, 250f, 24f), Plugin.AutoStoreOpenedEquipment.Value, UiText.L(" 개봉 장비 자동 창고·Vault 이동", " Auto-store opened gear", " 开出的装备自动入库", " 開出的裝備自動入庫"), toggleStyle!);
        if (nextAutoStore != Plugin.AutoStoreOpenedEquipment.Value)
        {
            Plugin.AutoStoreOpenedEquipment.Value = nextAutoStore;
            armedBulkOpen = BulkToolKind.None;
            status = nextAutoStore
                ? UiText.L("개봉 장비를 게임 규칙에 따라 자동 보관합니다.", "Opened gear follows the game's automatic storage rules.", "开启的装备将按游戏规则自动入库。", "開啟的裝備將按遊戲規則自動入庫。")
                : UiText.L("개봉 장비를 인벤토리에 남깁니다.", "Opened gear stays in the inventory.", "开启的装备将留在背包。", "開啟的裝備將留在背包。");
        }
        DrawBulkOpenButton(new Rect(rect.x + 18f, rect.y + 130f, (rect.width - 42f) / 2f, 52f), BulkToolKind.EquipmentBox, equipmentBoxCount, UiText.L("장비 상자", "Gear boxes", "装备箱", "裝備箱"));
        DrawBulkOpenButton(new Rect(rect.x + 24f + (rect.width - 42f) / 2f, rect.y + 130f, (rect.width - 42f) / 2f, 52f), BulkToolKind.RuneBox, runeBoxCount, UiText.L("룬 상자", "Rune boxes", "符文箱", "符文箱"));

        GUI.Label(new Rect(rect.x + 18f, rect.y + 202f, 110f, 24f), UiText.L("상자 등급", "Box Quality", "箱子品质", "箱子品質"), utilityTitleStyle!);
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
        var qualityWidth = (rect.width - 60f) / 5f;
        for (var index = 0; index < qualityEntries.Length; index++)
        {
            var row = index / 5;
            var column = index % 5;
            var entry = qualityEntries[index];
            DrawBulkQualityButton(new Rect(rect.x + 18f + column * (qualityWidth + 6f), rect.y + 232f + row * 40f, qualityWidth, 34f), entry.Quality, entry.Label);
        }
        var nextAtLeast = GUI.Toggle(new Rect(rect.x + 18f, rect.y + 318f, 190f, 24f), Plugin.BulkQualityAtLeast.Value, UiText.L(" 선택 등급 이상 모두", " Selected or higher", " 所选品质及以上", " 所選品質以上"), toggleStyle!);
        if (nextAtLeast != Plugin.BulkQualityAtLeast.Value)
        {
            Plugin.BulkQualityAtLeast.Value = nextAtLeast;
            armedBulkOpen = BulkToolKind.None;
            RefreshBulkCounts();
        }
        GUI.enabled = panelEnabled;
        if (bulkSession is not null)
        {
            GUI.Label(new Rect(rect.x + 18f, rect.y + 348f, rect.width - 150f, 42f),
                UiText.L(
                    $"진행 중 · {bulkSession.Opened:N0}/{bulkSession.Initial:N0}개 확인",
                    $"Opening · {bulkSession.Opened:N0}/{bulkSession.Initial:N0} confirmed",
                    $"进行中 · 已确认 {bulkSession.Opened:N0}/{bulkSession.Initial:N0}",
                    $"進行中 · 已確認 {bulkSession.Opened:N0}/{bulkSession.Initial:N0}"), hintStyle!);
            if (GUI.Button(new Rect(rect.xMax - 122f, rect.y + 349f, 104f, 32f), UiText.L("중단", "Cancel", "取消", "取消"), buttonStyle!))
                bulkSession.CancelRequested = true;
        }
        else
        {
            GUI.Label(new Rect(rect.x + 18f, rect.y + 352f, rect.width - 36f, 38f), status, hintStyle!);
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

        GUI.Label(new Rect(rect.x + 20f, rect.y + 18f, rect.width - 140f, 30f), UiText.L("현재 선택 영웅 자동 최적화", "Optimize the Selected Hero", "自动优化当前英雄", "自動最佳化目前英雄"), titleStyle!);
        if (GUI.Button(new Rect(rect.xMax - 112f, rect.y + 16f, 92f, 30f), UiText.L("새로고침", "Refresh", "刷新", "重新整理"), buttonStyle!)) RefreshCurrentPage();
        GUI.Label(new Rect(rect.x + 20f, rect.y + 54f, rect.width - 40f, 42f), UiText.L(
            "게임에서 영웅을 먼저 선택하세요. 공격 테마는 피해 근사치, 방어 테마는 생존 능력치를 우선하며 공식 추천 장비에는 가산점을 주지 않습니다.",
            "Select a hero first. Damage themes prioritize the damage proxy; Defense prioritizes survival stats. Official guide gear gets no score bonus.",
            "请先选择英雄。伤害主题优先伤害近似值，防御主题优先生存属性；官方指南装备不获得评分加成。",
            "請先選擇英雄。傷害主題優先傷害近似值，防禦主題優先生存屬性；官方指南裝備不獲得評分加成。"), hintStyle!);

        GUI.backgroundColor = new Color(0.24f, 0.42f, 0.62f, 0.90f);
        GUI.Box(new Rect(rect.x + 20f, rect.y + 105f, rect.width - 40f, 92f), GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.Label(new Rect(rect.x + 36f, rect.y + 118f, rect.width - 72f, 28f), string.IsNullOrWhiteSpace(selectedHeroSummary)
            ? UiText.L("선택된 영웅 없음", "No hero selected", "未选择英雄", "未選擇英雄")
            : selectedHeroSummary, utilityTitleStyle!);
        GUI.Label(new Rect(rect.x + 36f, rect.y + 151f, rect.width - 72f, 34f), selectedHeroProfile, hintStyle!);

        GUI.Label(new Rect(rect.x + 24f, rect.y + 207f, 150f, 24f), UiText.L("빌드 테마", "Build Theme", "构筑主题", "流派主題"), utilityTitleStyle!);
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
        var themeGap = 6f;
        var themeWidth = (rect.width - 48f - themeGap * 5f) / 6f;
        for (var index = 0; index < themes.Length; index++)
        {
            var row = index / 6;
            var column = index % 6;
            DrawAutoBuildThemeButton(new Rect(rect.x + 24f + column * (themeWidth + themeGap), rect.y + 234f + row * 36f, themeWidth, 30f), themes[index].Key, themes[index].Label);
        }

        var nextStorage = GUI.Toggle(new Rect(rect.x + 24f, rect.y + 316f, 300f, 26f), Plugin.AutoBuildIncludeStorage.Value, UiText.L(" 창고·Vault 장비도 후보에 포함", " Include warehouse and Vault gear", " 包含仓库和宝库装备", " 包含倉庫和寶庫裝備"), toggleStyle!);
        if (nextStorage != Plugin.AutoBuildIncludeStorage.Value)
        {
            Plugin.AutoBuildIncludeStorage.Value = nextStorage;
            armedAutoBuild = AutoBuildAction.None;
        }
        var nextTransform = GUI.Toggle(new Rect(rect.x + 340f, rect.y + 316f, 340f, 26f), Plugin.AutoTransformSkills.Value, UiText.L($" 부족한 성능 계획 스킬 변환 (최대 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)}회)", $" Transform missing plan skills (max {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)})", $" 转换缺少的性能方案技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）", $" 轉換缺少的效能方案技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）"), toggleStyle!);
        if (nextTransform != Plugin.AutoTransformSkills.Value)
        {
            Plugin.AutoTransformSkills.Value = nextTransform;
            armedAutoBuild = AutoBuildAction.None;
        }

        DrawAutoBuildButton(new Rect(rect.x + 24f, rect.y + 356f, (rect.width - 60f) / 2f, 58f), AutoBuildAction.Gear,
            UiText.L("장비 자동 장착", "Auto-equip Gear", "自动装备", "自動裝備"));
        DrawAutoBuildButton(new Rect(rect.x + 36f + (rect.width - 60f) / 2f, rect.y + 356f, (rect.width - 60f) / 2f, 58f), AutoBuildAction.Skills,
            UiText.L("스킬·특성 자동 배분", "Auto-allocate Skills", "自动分配技能", "自動分配技能"));

        GUI.Label(new Rect(rect.x + 24f, rect.y + 428f, rect.width - 48f, 64f), UiText.L(
            "점수는 게임 능력치와 스킬 60초 근사치이며, 전체 전투·조건부 효과의 정확한 DPS가 아닙니다.\n스킬 변환은 게임의 신전 비용을 사용한 뒤 선택한 성능 목표에 집중 배분합니다.",
            "The score uses native attributes plus a 60-second skill proxy; it is not exact full-combat DPS or conditional-effect simulation.\nTransformation uses the shrine's normal cost before allocation to the selected performance objective.",
            "评分使用游戏原生属性与技能 60 秒近似值，并非完整战斗 DPS 或条件效果的精确模拟。\n技能转换会消耗神殿正常费用，再按所选性能目标集中分配。",
            "評分使用遊戲原生屬性與技能 60 秒近似值，並非完整戰鬥 DPS 或條件效果的精確模擬。\n技能轉換會消耗神殿正常費用，再按所選效能目標集中分配。"), tooltipBodyStyle!);
        GUI.Label(new Rect(rect.x + 24f, rect.y + 500f, rect.width - 48f, 42f), UiText.L(
            "주의: 스킬 변환·특성 초기화에는 게임의 정상 비용이 듭니다. 초기화 비용은 남겨 두며, 실행 버튼은 두 번 눌러야 합니다.",
            "Caution: transformation and talent reset use normal game costs. Reset cost is reserved; each action requires a second click.",
            "注意：技能转换与天赋重置会消耗游戏正常费用。系统会预留重置费用；操作需点击两次。",
            "注意：技能轉換與天賦重設會消耗遊戲正常費用。系統會保留重設費用；操作需點擊兩次。"), hintStyle!);
        GUI.Label(new Rect(rect.x + 24f, rect.y + 548f, rect.width - 48f, 52f), status, tooltipBodyStyle!);
    }

    [HideFromIl2Cpp]
    private void DrawAutoBuildThemeButton(Rect rect, string key, string label)
    {
        var selected = string.Equals(GameInventoryReader.NormalizeBuildTheme(Plugin.AutoBuildTheme.Value), key, StringComparison.OrdinalIgnoreCase);
        var previousBackground = GUI.backgroundColor;
        if (selected) GUI.backgroundColor = new Color(0.34f, 0.78f, 0.58f);
        if (GUI.Button(rect, label, compactButtonStyle!))
        {
            Plugin.AutoBuildTheme.Value = key;
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
        var succeeded = action == AutoBuildAction.Gear
            ? GameInventoryReader.TryOptimizeSelectedHeroGear(Plugin.AutoBuildIncludeStorage.Value, out var message)
            : GameInventoryReader.TryOptimizeSelectedHeroSkills(out message);
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
                Input.compositionCursorPos = new Vector2(searchRect.x + 12f, searchRect.yMax + 4f);
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
        if (panelStyle is not null) return;
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
        string[] SkillTerms);

    private sealed record EquipmentScore(double Total, int DirectMatches, int ThemeMatches);
    private sealed record EquipAttrMapping(int EquipType, int BattleAttrType);
    private sealed record SetEffectScoreRow(int EffectId, int Pieces, string Text, int AbilityId);
    private sealed record GearSlot(int Part, bool MainWeapon, int WeaponSlotIndex, string Label);
    private enum MoveReceiptKind { FieldMove, BagToVault, VaultToBag }
    private sealed record MoveReceipt(MoveReceiptKind Kind, object? FromField, object? ToField, object BeforeFromItem, object? BeforeToItem, object? TreasureData = null, object? GroupData = null);
    private sealed record LoadoutState(List<GearCandidate> Items, HashSet<string> UsedKeys, HashSet<string> NonStackingEffectKeys, double HeuristicScore);
    private sealed record PreferredTalentPlan(
        object? Build,
        List<int> SkillTalentIds,
        List<int> MasteryTalentIds,
        HashSet<int> PreferredSkillIds,
        string BuildName,
        Dictionary<int, double>? ObjectiveScores = null);
    private sealed record PreferredActiveSkill(int GuideTalentId, int TalentId, int SkillId, object Talent);
    private sealed record NativeSkillRoleProfile(Dictionary<int, double> DamageByType, double Heal, double Shield, bool Summon);
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
            var requestedTheme = NormalizeBuildTheme(Plugin.AutoBuildTheme.Value);
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

    [Conditional("PATHOFIDLE_DIAGNOSTICS")]
    private static void LogTeamSet(string scope, List<TeamSuggestion> teams)
    {
        for (var index = 0; index < teams.Count; index++)
        {
            var team = teams[index];
            Plugin.DiagInfo($"TEAM-{scope}|{index + 1}|{team.Score:0.0}|{team.A.Name} [{team.A.Job}] + {team.B.Name} [{team.B.Job}] + {team.C.Name} [{team.C.Job}]|{team.Reason}|{team.A.BuildHint} ; {team.B.BuildHint} ; {team.C.BuildHint}");
        }
    }

    public static bool TryOptimizeSelectedHeroGear(bool includeStorage, out string message)
    {
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
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData");
            var lordData = Read(seasonData, "lordData");
            if (lordData is null) throw new InvalidOperationException("Lord data is unavailable.");
            var focus = ResolveHeroFocus(hero, Plugin.AutoBuildTheme.Value);
            var profile = BuildHeroEffectProfile(hero, focus);
            gearTalentData = Read(hero, "heroTalentData");
            var slots = GetGearSlots();
            var currentBySlot = slots.ToDictionary(slot => slot, slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon));
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
                // Both weapon slots are now known. Later armor choices cannot
                // repair an unusable skill package, so remove incompatible
                // pairs before they can occupy the entire 360-state beam.
                if (slot.Part == 1 && !slot.MainWeapon)
                    expandedBeam = expandedBeam.Where(state => IsLoadoutWeaponCompatible(state.Items, profile));
                beam = expandedBeam
                    .OrderByDescending(state => state.HeuristicScore + EstimatePartialSetSynergy(state.Items, profile))
                    .Take(360).ToList();
                if (beam.Count == 0) throw new InvalidOperationException($"No valid loadout remains for {slot.Label}.");
            }

            var currentItems = currentBySlot.Values.Where(item => item is not null).Cast<object>().ToList();
            var finalistStates = beam
                .Where(state => IsLoadoutWeaponCompatible(state.Items, profile))
                .OrderByDescending(state => state.HeuristicScore + EstimatePartialSetSynergy(state.Items, profile))
                .Take(96)
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
            var winner = finalistStates.Select(state => new { State = state, Score = ScoreCompleteLoadout(state.Items, hero, profile, currentItems) })
                .OrderByDescending(entry => entry.Score).First();

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

            Plugin.DiagInfo($"AUTO-GEAR PLAN|focus={focus.English}|score={winner.Score:0.0}|" +
                                  string.Join(" ; ", slots.Select((slot, index) =>
                                  {
                                      var choice = winner.State.Items[index];
                                       return $"{slot.Label}={choice.Record.Name} Q{choice.Record.Quality} Lv{choice.Record.Level ?? 0} itemScore={choice.Score:0.0} numeric={choice.NumericScore:0.0} direct={choice.DirectMatches} theme={choice.ThemeMatches} set={choice.SetId} effectPolicy={GetNonStackingEffectSignature(choice.NonStackingEffectKeys)}";
                                  })));
            Plugin.DiagInfo($"AUTO-GEAR SET PLAN|focus={focus.English}|{DescribeSetPlan(winner.State.Items, profile)}");

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
            directMatches = CountEffectiveDirectMatches(winner.State.Items, profile);

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
                var variantVerification = VerifyEquippedSkillVariants(hero, gearTalentData, "AUTO-GEAR");
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

            var storageNormalizationFailures = NormalizeCommittedStorage(moveJournal, seasonData, targetItems);
            moveJournal.Clear();
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

            message = changed > 0
                ? UiText.L($"8부위 실제 장착 완료 · {focus.Localized} · 교체 {changed}개 · 현재 스킬 효과 일치 {directMatches} · 테마 키워드 일치 {themeMatches} · Mythic {selectedMythics}/{maxMythic} · 유지 {unchanged}개{commitWarningKo}", $"All 8 slots equipped · {focus.English} · changed {changed} · learned-skill effect matches {directMatches} · theme-keyword matches {themeMatches} · Mythic {selectedMythics}/{maxMythic} · kept {unchanged}{commitWarningEn}", $"8部位已实际装备 · {focus.Localized} · 更换 {changed} · 当前技能效果匹配 {directMatches} · 主题关键词匹配 {themeMatches} · 神话 {selectedMythics}/{maxMythic} · 保留 {unchanged}{commitWarningZhCn}", $"8部位已實際裝備 · {focus.Localized} · 更換 {changed} · 目前技能效果相符 {directMatches} · 主題關鍵字符合 {themeMatches} · 神話 {selectedMythics}/{maxMythic} · 保留 {unchanged}{commitWarningZhTw}")
                : UiText.L($"평가한 후보 중 현재 8부위의 {focus.Localized} 추천 점수가 가장 높습니다.{commitWarningKo}", $"The current 8-slot loadout has the highest {focus.English} recommendation score among the evaluated candidates.{commitWarningEn}", $"在已评估候选中，当前 8 部位的 {focus.Localized} 推荐评分最高。{commitWarningZhCn}", $"在已評估候選中，目前 8 部位的 {focus.Localized} 推薦評分最高。{commitWarningZhTw}");
            // Storage tidy-up and the old learned-skill compatibility check run
            // after the target gear is committed. A compatibility warning is
            // actionable by Auto Skills and may be expected during a theme
            // switch, but it must not be reported as a verified success.
            return storageNormalizationFailures == 0 && !unusableSkillFailed;
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

    public static bool TryOptimizeSelectedHeroSkills(out string message)
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
            var focus = ResolveHeroFocus(hero, Plugin.AutoBuildTheme.Value);
            var spentBefore = GetResettableTalentPointCount(talentData, talents);
            var totalTalentPoints = PreviewExactTalentPointBudget(talentData, saveHero, talents, spentBefore);
            var preferred = GetPerformanceTalentPlan(hero, focus, totalTalentPoints);

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
            var gridById = BuildTalentGridById(talents);
            if (gridById.Count == 0)
            {
                message = UiText.L("배분 가능한 스킬·특성이 없습니다.", "No skills or talents can receive points.", "没有可分配点数的技能或天赋。", "沒有可分配點數的技能或天賦。");
                return false;
            }
            var selectedActiveDefinitions = preferred.SkillTalentIds
                .Select(id => InvokeStatic("TableData", "getTTalentData", id))
                .Where(IsTransformableSkillDefinition).ToList();
            if (selectedActiveDefinitions.Count == 0)
            {
                message = UiText.L(
                    "현재 레벨에서 자동 배분에 사용할 해금된 액티브 스킬 슬롯이 없습니다. 초기화하지 않았습니다.",
                    "No unlocked active-skill slot is available for this plan at the current level. No reset was performed.",
                    "当前等级没有可用于此方案的已解锁主动技能槽，未执行重置。",
                    "目前等級沒有可用於此方案的已解鎖主動技能欄，未執行重設。");
                return false;
            }
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
            var unresolvedAfterTransform = selectedActiveSkillIds
                .Where(id => postTransformActive.All(entry => entry.SkillId != id)).ToList();
            var postTransformMasteryIds = preResetGuideMasteryIds
                .Where(gridById.ContainsKey)
                .Where(id => !IsTalentLockedRequired(gridById[id]))
                .ToList();
            var noPointCapacityIds = postTransformActive.Select(entry => entry.Talent)
                .Concat(postTransformMasteryIds.Select(id => gridById[id]))
                .Where(talent => GetTalentLevelCap(talent) - GetTalentBaseLevelRequired(talent) < 1)
                .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
                .Where(id => id > 0).Distinct().ToList();
            if (unresolvedAfterTransform.Count > 0 || postTransformMasteryIds.Count != preResetGuideMasteryIds.Count || noPointCapacityIds.Count > 0)
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

            // 1) Learn every recommended active skill once, so the loadout is
            // usable before points are concentrated into its main damage package.
            // GetLevel includes equipment/base bonuses, so only the saved level
            // can prove that the user has actually invested a point in the node.
            foreach (var entry in activeSkills)
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                if (GetSavedTalentLevel(entry.Talent) > 0) continue;
                if (!TrySpendTalentPoints(talentData, saveHero, entry.Talent, 1, out var spent)) failedNodes.Add(entry.TalentId);
                allocatedByPlan += spent;
            }

            // 2) Unlock relevant guide masteries once before concentrating points.
            // This produces a usable synergy package even when points are scarce.
            foreach (var talentId in relevantMasteryTalentIds
                         .OrderByDescending(id => ScoreTalent(gridById[id], focus, effectivePreferred)))
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                var talent = gridById[talentId];
                if (GetSavedTalentLevel(talent) > 0) continue;
                if (!TrySpendTalentPoints(talentData, saveHero, talent, 1, out var spent)) failedNodes.Add(talentId);
                allocatedByPlan += spent;
            }

            // 3) Choose the primary skill from the official guide order plus
            // theme/text evidence. Mastery effectType 4 rows are display metadata:
            // native GetMasteryEffect applies only type 1 attributes and type 3
            // abilities, so treating type 4 as direct skill synergy was unsound.
            var primaryTalentId = availableBaseSkillTalentIds.Concat(activeSkillTalentIds).Distinct()
                .OrderByDescending(id => ScoreTalent(gridById[id], focus, effectivePreferred))
                .ThenBy(id => preferred.SkillTalentIds.IndexOf(id) is var index && index >= 0 ? index : int.MaxValue)
                .FirstOrDefault();
            if (primaryTalentId > 0)
                allocatedByPlan += SpendTalentToCap(talentData, saveHero, gridById[primaryTalentId], failedNodes);

            foreach (var talentId in relevantMasteryTalentIds
                         .OrderByDescending(id => ScoreTalent(gridById[id], focus, effectivePreferred)))
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                allocatedByPlan += SpendTalentToCap(talentData, saveHero, gridById[talentId], failedNodes);
            }

            // 4) Spend leftovers on the single best currently available node at
            // a time. Maxing a useful node before moving on avoids round-robin
            // equal distribution while native AddTalentPoint verifies each spend.
            var guard = 0;
            while ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) > 0 && guard++ < 512)
            {
                var best = gridById.Values
                    .Where(talent => !failedNodes.Contains(ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0))
                    .Where(talent => CanAutoAllocateTalent(talent, hero, effectivePreferred))
                    .OrderByDescending(talent => ScoreTalent(talent, focus, effectivePreferred))
                    .ThenBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "floor") ?? int.MaxValue)
                    .FirstOrDefault();
                if (best is null) break;
                var talentId = ReadNullableInt(Read(best, "tTalentData"), "id") ?? 0;
                var spent = SpendTalentToCap(talentData, saveHero, best, failedNodes);
                if (spent <= 0) failedNodes.Add(talentId);
                allocatedByPlan += spent;
            }

            // A native rebuild or an effective-level bonus must never make a
            // recommended skill look learned while its saved point remains zero.
            // Retry with any remaining point, then record exact state for support.
            foreach (var entry in activeSkills.Where(entry => GetSavedTalentLevel(entry.Talent) <= 0))
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                if (!TrySpendTalentPoints(talentData, saveHero, entry.Talent, 1, out var spent)) failedNodes.Add(entry.TalentId);
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
            var learnedButUnavailableSkillIds = activeSkills
                .Where(entry => GetSavedTalentLevel(entry.Talent) > 0 && !usableSkillIds.Contains(entry.SkillId))
                .Select(entry => entry.SkillId).Distinct().ToList();
            if (learnedButUnavailableSkillIds.Count > 0)
            {
                missingPreferredSkillIds = missingPreferredSkillIds.Concat(learnedButUnavailableSkillIds).Distinct().ToList();
                Plugin.DiagWarning($"AUTO-SKILLS ACTIVE UNAVAILABLE|skills={string.Join(',', learnedButUnavailableSkillIds)}");
            }
            var hasUnusableSkill = ReadBool(InvokeRequiredInstance(talentData, "IsHasUnusableSkill"));
            var variantVerification = VerifyEquippedSkillVariants(hero, talentData, "AUTO-SKILLS");
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
            Plugin.DiagInfo($"AUTO-SKILLS PLAN|focus={focus.English}|build={preferred.BuildName}|transform={transform.Attempts}:{transform.Matched}/{transform.Target}|transformNote={transform.Note}|baseSkillChanged={baseSkillChanged}:{baseSkillName}|baseTalent={actualBaseTalentId}/{desiredBaseTalentId}|baseSkill={actualBaseSkillId}/{desiredBaseSkillId}|baseSaved={desiredBaseSavedLevel}|usableSkills={string.Join(',', usableSkillIds.OrderBy(id => id))}|hasUnusableSkill={hasUnusableSkill}|variants={string.Join(',', variantSkillIds)}|variantExact={variantVerification.IsExact}|allocated={allocated}|planned={allocatedByPlan}|ledgerExact={allocationLedgerExact}|remaining={remaining}|unspent={unspentPointsRemain}|resetBlood={resetSpentBlood}/{(spentBefore > 0 ? resetPrice : 0)}|unlearnedMasteries={string.Join(',', unlearnedMasteryTalentIds)}|failedNodes={string.Join(',', failedNodes)}");
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
            if (!allocationLedgerExact)
            {
                incompleteKo.Add($"포인트 원장 불일치 {allocated}/{allocatedByPlan}");
                incompleteEn.Add($"point ledger mismatch {allocated}/{allocatedByPlan}");
                incompleteZhCn.Add($"点数记录不匹配 {allocated}/{allocatedByPlan}");
                incompleteZhTw.Add($"點數記錄不符 {allocated}/{allocatedByPlan}");
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
            message = UiText.L(
                $"{completionKo} · {focus.Localized} · {preferred.BuildName}{transformKo}{variantKo} · {allocated:N0}포인트 · 변환 피 {transform.SpentBlood:N0} / 초기화 피 {resetSpentBlood:N0}{(remaining > 0 ? $" · 미사용 {remaining:N0}" : string.Empty)}{transformNoteSuffix}",
                $"{completionEn} · {focus.English} · {preferred.BuildName}{transformEn}{variantEn} · {allocated:N0} points · transform Blood {transform.SpentBlood:N0} / reset Blood {resetSpentBlood:N0}{(remaining > 0 ? $" · {remaining:N0} unspent" : string.Empty)}{transformNoteSuffix}",
                $"{completionZhCn} · {focus.Localized} · {preferred.BuildName}{transformZh}{variantZh} · {allocated:N0} 点 · 转换鲜血 {transform.SpentBlood:N0} / 重置鲜血 {resetSpentBlood:N0}{(remaining > 0 ? $" · 剩余 {remaining:N0}" : string.Empty)}{transformNoteSuffix}",
                $"{completionZhTw} · {focus.Localized} · {preferred.BuildName}{transformZh}{variantZh} · {allocated:N0} 點 · 轉換鮮血 {transform.SpentBlood:N0} / 重設鮮血 {resetSpentBlood:N0}{(remaining > 0 ? $" · 剩餘 {remaining:N0}" : string.Empty)}{transformNoteSuffix}");
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

    private static HeroFocus ResolveHeroFocus(object hero, string? requestedTheme)
    {
        return NormalizeBuildTheme(requestedTheme) switch
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
        var primaryParts = new List<string>
        {
            ReadString(job, "name") ?? string.Empty,
            ReadString(job, "des") ?? string.Empty,
            EnglishName(job, string.Empty) ?? string.Empty,
            EnglishText(job, "_des", string.Empty) ?? string.Empty
        };
        var activeParts = new List<string>();
        AddSkillText(InvokeInstance(hero, "GetNowBaseSkillData"), primaryParts);
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
                activeParts.Add(EnglishName(definition, string.Empty) ?? string.Empty);
                activeParts.Add(EnglishName(skillRow, string.Empty) ?? string.Empty);
                activeParts.Add(EnglishText(info, "_des", string.Empty) ?? string.Empty);
                activeParts.Add(EnglishName(mastery, string.Empty) ?? string.Empty);
                foreach (var affix in ReadList(Read(masteryData, "affixList")))
                {
                    var effectType = ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0;
                    if (effectType is 1 or 3) activeParts.Add(GetAffixSearchText(affix));
                }
            }
        }
        catch { }
        var primaryText = Clean(string.Join(" ", primaryParts)).ToLowerInvariant();
        var activeText = Clean(string.Join(" ", activeParts)).ToLowerInvariant();
        var specialized = new[]
        {
            new HeroFocus("minion", UiText.L("소환수·하수인", "Minion / Summon", "召唤物/仆从", "召喚物/僕從"), "Minion / Summon", MinionWords),
            new HeroFocus("fire", UiText.L("화염·연소", "Fire / Burn", "火焰/燃烧", "火焰/燃燒"), "Fire / Burn", FireWords),
            new HeroFocus("ice", UiText.L("냉기·빙결", "Ice / Freeze", "冰霜/冻结", "冰霜/凍結"), "Ice / Freeze", IceWords),
            new HeroFocus("lightning", UiText.L("번개·감전", "Lightning / Shock", "闪电/感电", "閃電/感電"), "Lightning / Shock", LightningWords),
            new HeroFocus("bleed", UiText.L("출혈·상처", "Bleed / Wound", "流血/创伤", "流血/創傷"), "Bleed / Wound", BleedWords),
            new HeroFocus("corrosion", UiText.L("부식·중독", "Corrosion / Poison", "腐蚀/中毒", "腐蝕/中毒"), "Corrosion / Poison", CorrosionWords),
            new HeroFocus("crit", UiText.L("치명타", "Critical Strike", "暴击", "暴擊"), "Critical Strike", CriticalWords)
        };
        var specializedScores = specialized.Select(focus => (Focus: focus, Score: KeywordScore(primaryText, focus.Keywords) * 4 + KeywordScore(activeText, focus.Keywords)))
            .OrderByDescending(entry => entry.Score).ToList();
        var bestSpecialized = specializedScores
            .OrderByDescending(entry => entry.Score).First();
        var runnerUp = specializedScores.Skip(1).FirstOrDefault().Score;
        if (bestSpecialized.Score >= 4 && bestSpecialized.Score >= runnerUp + 2) return bestSpecialized.Focus;

        // Keyword evidence comes from the hero's base skill and learned
        // masteries. Only compare physical and elemental attack attributes as a
        // tie-breaker; HP and defence use different scales and must not decide an
        // offensive archetype.
        var phyEvidence = KeywordScore(primaryText, PhysicalWords) * 4 + KeywordScore(activeText, PhysicalWords);
        var eleEvidence = KeywordScore(primaryText, ElementalWords) * 4 + KeywordScore(activeText, ElementalWords);
        var supportEvidence = KeywordScore(primaryText, SupportWords) * 4 + KeywordScore(activeText, SupportWords);
        var tankEvidence = KeywordScore(primaryText, TankWords) * 4 + KeywordScore(activeText, TankWords);
        var offenseEvidence = Math.Max(phyEvidence, eleEvidence);
        if (supportEvidence >= 4 && supportEvidence >= offenseEvidence + 2)
            return new HeroFocus("support", UiText.L("지원·오라", "Support / Aura", "辅助/光环", "輔助/光環"), "Support / Aura", SupportWords);
        if (tankEvidence >= 5 && tankEvidence >= offenseEvidence + 3)
            return new HeroFocus("defense", UiText.L("생존·방어", "Defense / Survival", "防御/生存", "防禦/生存"), "Defense / Survival", TankWords);
        if (eleEvidence > phyEvidence + 1)
            return new HeroFocus("elemental", UiText.L("원소·주문", "Elemental / Spell", "元素/法术", "元素/法術"), "Elemental / Spell", ElementalWords);
        if (phyEvidence > eleEvidence + 1)
            return new HeroFocus("physical", UiText.L("물리·무예", "Physical / Martial", "物理/武技", "物理/武技"), "Physical / Martial", PhysicalWords);
        // Live AttrData includes the currently equipped gear. Using it as the
        // last tie-break made Auto focus circular (old Lightning gear could keep
        // selecting Lightning). With no clear job/skill evidence, stay neutral.
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

        // Native GetSkillTalentList uses every same-job, type-1, non-base row.
        // Official builds do not define shrine eligibility and receive no skill
        // score bonus. Their mastery arrays are retained only as a deterministic
        // zero-weight tie-break between otherwise equal native candidates.
        var unlockedBuilds = ReadValues(ReadStatic("TableData", "TBuildsDict"))
            .Where(build => (ReadNullableInt(build, "jobId") ?? 0) == jobId)
            .Where(build => !ReadRequiredBoolProperty(build, "isLock"))
            .ToList();
        var catalogueMasteryTalentIds = unlockedBuilds
            .SelectMany(build => ReadRequiredSequenceProperty(build, "masteryArr"))
            .Select(ToInt).Where(id => id > 0).ToHashSet();

        var baseRows = gridTalents
            .Where(talent => !IsTalentLockedRequired(talent))
            .Select(talent => Read(talent, "tTalentData"))
            .Where(IsBaseSkillDefinition).Cast<object>()
            .Where(row => IsSkillCompatibleWithEquippedWeapons(hero, ReadNullableInt(row, "skillId") ?? 0))
            .Where(row => MatchesManualSkillTheme(hero, row, focus))
            .DistinctBy(row => ReadNullableInt(row, "id") ?? 0)
            .ToList();

        var currentActiveTalents = GetTransformableTalents(talentData)
            .Where(talent => !IsTalentLockedRequired(talent)).ToList();
        var fixedSkillIds = currentActiveTalents.Where(IsTalentUnreplaceable)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
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
            .Where(row =>
            {
                var skillId = ReadNullableInt(row, "skillId") ?? 0;
                return fixedSkillIds.Contains(skillId)
                       || (IsSkillCompatibleWithEquippedWeapons(hero, skillId)
                           && MatchesManualSkillTheme(hero, row, focus));
            })
            .GroupBy(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(group => group.Key > 0)
            .Select(group => group.OrderByDescending(row => currentActiveTalentIds.Contains(ReadNullableInt(row, "id") ?? 0))
                .ThenBy(row => ReadNullableInt(row, "index") ?? int.MaxValue).First())
            .ToList();
        if (baseRows.Count == 0 || activeRows.Count == 0)
            throw new InvalidOperationException($"No compatible unlocked performance skill pool is available (base={baseRows.Count}, active={activeRows.Count}).");

        var currentBaseTalentId = ReadRequiredIntProperty(saveHero, "baseSkillId");
        var profile = BuildHeroEffectProfile(hero, focus);
        var objectiveAttr = CreateTalentNeutralObjectiveAttr(hero, gridTalents);
        const int defaultSkillLevel = 1;
        var objectiveScores = baseRows.Concat(activeRows).ToDictionary(
            row => ReadNullableInt(row, "id") ?? 0,
            row => ScoreSkillDefinitionForObjective(hero, row, focus, profile, currentBaseTalentId, defaultSkillLevel, objectiveAttr));
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
            .ToList();
        var masteryTargetCount = Math.Clamp(totalTalentPoints - currentActiveTalents.Count, 0, 6);
        var masteryTalentIds = masteryRows
            .Select(talent =>
            {
                var id = ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0;
                var score = ScoreMasteryTalentForObjective(hero, talent, focus, objectiveAttr, objectiveSkillIds);
                objectiveScores[id] = score;
                return (Id: id, Score: score, Catalogue: catalogueMasteryTalentIds.Contains(id), Floor: ReadNullableInt(Read(talent, "tTalentData"), "floor") ?? int.MaxValue);
            })
            .Where(entry => entry.Id > 0 && double.IsFinite(entry.Score) && entry.Score > 0d)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Catalogue)
            .ThenBy(entry => entry.Floor)
            .Take(masteryTargetCount)
            .Select(entry => entry.Id).ToList();
        var label = UiText.L("성능 목표", "Performance objective", "性能目标", "效能目標");
        Plugin.DiagInfo($"AUTO-SKILLS PERFORMANCE POOL|job={jobId}|focus={focus.English}|base={selectedBaseId}|activeCandidates={activeRows.Count}|scores={string.Join(',', skillTalentIds.Take(12).Select(id => $"{id}:{objectiveScores[id]:0.0}"))}");
        return new PreferredTalentPlan(null, skillTalentIds, masteryTalentIds, skillIds, label, objectiveScores);
    }

    private static double ScoreSkillDefinitionForObjective(object hero, object definition, HeroFocus focus,
        HeroEffectProfile profile, int currentBaseTalentId, int previewLevel, object objectiveAttr)
    {
        var talentId = ReadNullableInt(definition, "id") ?? 0;
        var skillId = ReadNullableInt(definition, "skillId") ?? 0;
        var text = GetTalentDefinitionText(definition).ToLowerInvariant();
        var theme = KeywordScore(text, focus.Keywords);
        var tank = KeywordScore(text, TankWords);
        var support = KeywordScore(text, SupportWords);
        var semantic = theme * 250d
                       + tank * (focus.Key == "defense" ? 900d : 40d)
                       + support * (focus.Key == "support" ? 700d : focus.Key == "defense" ? 180d : 30d);
        if (!focus.IsManual && talentId == currentBaseTalentId) semantic += 50d;
        try
        {
            var nativeRole = ReadNativeSkillRoleProfile(hero, skillId, Math.Max(1, previewLevel), objectiveAttr);
            var rolePower = focus.Key switch
            {
                "defense" => Math.Log10(1d + Math.Max(0d, nativeRole.Shield * 1.5d + nativeRole.Heal)) * 9000d,
                "support" => Math.Log10(1d + Math.Max(0d, nativeRole.Heal * 1.4d + nativeRole.Shield)) * 8500d,
                "minion" when nativeRole.Summon => 6500d,
                _ => 0d
            };
            var damage60s = nativeRole.DamageByType.Values.Where(value => value > 0d).Sum();
            if (damage60s > 0d)
            {
                damage60s = GetManualDamageAmount(nativeRole.DamageByType, focus);
                var weight = focus.Key is "defense" or "support" ? 1200d : 10000d;
                return Math.Log10(1d + Math.Max(0d, damage60s)) * weight + rolePower + semantic;
            }
            if (rolePower > 0d) return rolePower + semantic;
        }
        catch (Exception error)
        {
            Plugin.DiagDebug($"AUTO-SKILLS OBJECTIVE PREVIEW FAILED|talent={talentId}|skill={skillId}|{error.GetBaseException().Message}");
        }
        return semantic;
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
        if (!focus.IsManual) return total;
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
        return damageByType.Where(entry => allowed.Contains(entry.Key) && entry.Value > 0d).Sum(entry => entry.Value);
    }

    private static NativeSkillRoleProfile ReadNativeSkillRoleProfile(object hero, int skillId, int level, object? attrOverride = null)
    {
        var damage = new Dictionary<int, double>();
        if (skillId <= 0) return new NativeSkillRoleProfile(damage, 0d, 0d, false);
        var attr = attrOverride ?? Read(hero, "attrData") ?? throw new InvalidOperationException("Hero AttrData is unavailable.");
        var preview = InvokeRequiredStaticMany("SkillData", "CreatePreview", skillId, Math.Max(1, level), attr)
                      ?? throw new InvalidOperationException($"SkillData.CreatePreview({skillId}) returned no skill.");
        if (CurrentEquipmentEnablesSkillVariant(hero, skillId))
            InvokeRequiredInstance(preview, "SetVariant", true);
        var info = Read(preview, "tSkillInfoData") ?? throw new InvalidOperationException($"Skill {skillId} info is unavailable.");
        var powerIds = ReadSequence(Read(info, "infoArr")).Select(ToInt).Where(id => id > 0)
            .Select(id => InvokeStatic("TableData", "getTSkillExplainData", id))
            .Where(explain => (ReadNullableInt(explain, "type") ?? -1) == 2)
            .Select(explain => ReadSequence(Read(explain, "typeParam")).Select(ToInt).FirstOrDefault())
            .Where(id => id > 0).Distinct().ToList();
        var heal = 0d;
        var shield = 0d;
        foreach (var powerId in powerIds)
        {
            var power = InvokeRequiredStaticMany("PowerData", "CreateByShow", powerId, Math.Max(1, level), preview)
                        ?? throw new InvalidOperationException($"PowerData.CreateByShow({powerId}) returned no power.");
            var powerType = ReadNullableInt(Read(power, "tPowerData"), "type") ?? 0;
            if (powerType == 1)
            {
                foreach (var entry in ReadEntries(Read(power, "dmgPowerDic")))
                {
                    var type = ToInt(Read(entry, "Key") ?? 0);
                    var value = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture);
                    if (type <= 0 || !double.IsFinite(value) || value <= 0d) continue;
                    damage[type] = damage.GetValueOrDefault(type) + value;
                }
            }
            else if (powerType == 2)
            {
                var value = Convert.ToDouble(Read(power, "power") ?? 0d, CultureInfo.InvariantCulture);
                if (double.IsFinite(value) && value > 0d) heal += value;
            }
            else if (powerType == 3)
            {
                var value = Convert.ToDouble(Read(power, "power") ?? 0d, CultureInfo.InvariantCulture);
                if (double.IsFinite(value) && value > 0d) shield += value;
            }
        }
        var previewAttr = Read(preview, "attrData") ?? attr;
        var nativeSpeed = Convert.ToDouble(
            InvokeRequiredInstance(previewAttr, "GetSkillSpeedRate", (object)null!) ?? 1d,
            CultureInfo.InvariantCulture);
        if (!double.IsFinite(nativeSpeed) || nativeSpeed <= 0d) nativeSpeed = 1d;
        nativeSpeed = Math.Clamp(nativeSpeed, 0.05d, 20d);
        var cooldown = Math.Max(0d, ReadAttrRequired(previewAttr, 2001));
        if (ReadAttrRequired(previewAttr, 3001) > 0d) cooldown = 0d;
        var castOpportunities = Math.Clamp(60d * nativeSpeed / (cooldown > 0.01d ? cooldown : 1d), 1d, 6000d);
        var critChance = Math.Clamp(PercentRate(ReadAttrRequired(attr, 31)), 0d, 1d);
        var critDamage = Math.Max(0.5d, 0.5d + PercentRate(ReadAttrRequired(attr, 37)));
        foreach (var type in damage.Keys.ToList())
        {
            var critFactor = type is 7 or 8 ? 1d : Math.Max(0.1d, 1d + critChance * critDamage);
            damage[type] *= castOpportunities * critFactor;
        }
        heal *= castOpportunities;
        shield *= castOpportunities;
        var summon = previewAttr is not null && (ReadAttrRequired(previewAttr, 6001) > 0d || ReadAttrRequired(previewAttr, 6002) > 0d);
        return new NativeSkillRoleProfile(damage, heal, shield, summon);
    }

    private static double ScoreMasteryTalentForObjective(object hero, object talent, HeroFocus focus,
        object objectiveAttr, IReadOnlyCollection<int> objectiveSkillIds)
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
        var texts = new List<string> { GetTalentDefinitionText(definition) };
        foreach (var affix in ReadList(Read(preview, "affixList")))
        {
            var effectType = ReadNullableInt(Read(affix, "tAffixData"), "effectType") ?? 0;
            if (effectType is 1 or 3) texts.Add(GetAffixSearchText(affix));
            if (effectType == 1)
                InvokeRequiredInstance(affix, "SetActiveAttrData", simulated, true);
        }
        var numericDelta = ScoreSkillPackageObjective(hero, objectiveSkillIds, simulated, focus)
                           - ScoreSkillPackageObjective(hero, objectiveSkillIds, baseline, focus);
        var text = Clean(string.Join(" ", texts)).ToLowerInvariant();
        var semantic = KeywordScore(text, focus.Keywords) * 700d
                       + KeywordScore(text, TankWords) * (focus.Key == "defense" ? 900d : 30d)
                       + KeywordScore(text, SupportWords) * (focus.Key == "support" ? 800d : 35d);
        return numericDelta * 1000d + semantic;
    }

    private static double ScoreSkillPackageObjective(object hero, IEnumerable<int> skillIds, object attrData, HeroFocus focus)
    {
        var score = ScoreHeroAttrObjective(attrData, focus);
        foreach (var skillId in skillIds.Where(id => id > 0).Distinct())
        {
            var role = ReadNativeSkillRoleProfile(hero, skillId, 1, attrData);
            var damage = GetManualDamageAmount(role.DamageByType, focus);
            score += focus.Key switch
            {
                "defense" => Math.Log10(1d + role.Shield * 1.5d + role.Heal) * 3d + Math.Log10(1d + damage) * 0.4d,
                "support" => Math.Log10(1d + role.Heal * 1.4d + role.Shield) * 3d + Math.Log10(1d + damage) * 0.35d,
                "minion" => Math.Log10(1d + damage) * (role.Summon ? 2.5d : 0.5d),
                _ => Math.Log10(1d + damage) * 2d
            };
        }
        return score;
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
        var physical = Math.Max(0d, ReadAttrRequired(attrData, 1));
        var elemental = Math.Max(0d, ReadAttrRequired(attrData, 2));
        var attack = focus.Key is "physical" or "bleed" ? physical
            : focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion" ? elemental
            : Math.Max(physical, elemental);
        var crit = Math.Max(0d, ReadAttrRequired(attrData, 31)) + Math.Max(0d, ReadAttrRequired(attrData, 37));
        var hp = Math.Max(0d, ReadAttrRequired(attrData, 5));
        var defence = Math.Max(0d, ReadAttrRequired(attrData, 3) + ReadAttrRequired(attrData, 4));
        var sustain = Math.Max(0d, ReadAttrRequired(attrData, 7) + ReadAttrRequired(attrData, 9) + ReadAttrRequired(attrData, 93) + ReadAttrRequired(attrData, 94));
        var support = Math.Max(0d, ReadAttrRequired(attrData, 81) + ReadAttrRequired(attrData, 82) + ReadAttrRequired(attrData, 191));
        var minion = Math.Max(0d, ReadAttrRequired(attrData, 25) * 50d + ReadAttrRequired(attrData, 190));
        return focus.Key switch
        {
            "defense" => Math.Log10(1d + hp + defence * 2d + sustain * 4d) * 3d + Math.Log10(1d + attack),
            "support" => Math.Log10(1d + support * 10d) * 3d + Math.Log10(1d + hp + defence),
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
            var definitionText = GetTalentDefinitionText(entry.Definition).ToLowerInvariant();
            double focusMatches = KeywordScore(definitionText, focus.Keywords);
            if (focus.Key == "defense")
                focusMatches += KeywordScore(definitionText, TankWords) * 1.35d + KeywordScore(definitionText, SupportWords) * 0.75d;
            else if (focus.Key == "support")
                focusMatches += KeywordScore(definitionText, SupportWords) * 1.35d + KeywordScore(definitionText, TankWords) * 0.45d;
            return (entry.TalentId, entry.Index, entry.SkillId,
                Fixed: fixedCurrentSkillIds.Contains(entry.SkillId),
                Objective: preferred.ObjectiveScores?.GetValueOrDefault(entry.TalentId) ?? 0d,
                FocusMatches: focusMatches,
                Compatible: IsSkillCompatibleWithEquippedWeapons(hero, entry.SkillId));
        }).ToList();

        var selectedRows = ranked
            // Never discard a matching skill that the user explicitly fixed.
            .OrderByDescending(entry => entry.Fixed)
            .ThenByDescending(entry => entry.Compatible)
            .ThenByDescending(entry => entry.Objective)
            .ThenByDescending(entry => entry.FocusMatches)
            .ThenBy(entry => entry.Index)
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

    [Conditional("PATHOFIDLE_DIAGNOSTICS")]
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
        if (current == desiredTalentId) return false;
        var desiredTalent = ReadValues(Read(talentData, "talentDic"))
            .FirstOrDefault(talent => (ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0) == desiredTalentId);
        if (desiredTalent is null)
        {
            Plugin.DiagWarning($"AUTO-SKILLS BASE SKIPPED|talent={desiredTalentId}|reason=base skill is not present in the hero talent grid");
            return false;
        }
        InvokeRequiredInstance(talentData, "ChangeBaseSkill", desiredTalentId);
        var applied = ReadNullableInt(saveHero, "baseSkillId") ?? 0;
        if (applied != desiredTalentId)
            throw new InvalidOperationException($"Base skill change was not applied (wanted talent {desiredTalentId}, got {applied}).");
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
                var appliedLikes = ApplyTemporaryTalentLikes(townData, missingTalentIds);
                if (missingTalentIds.Count > 0 && appliedLikes <= 0)
                {
                    note = AppendTransformNote(note, "recommended-skill preferences unavailable");
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
        var heroSave = Read(hero, "saveHeroData");
        var jobRow = Read(hero, "tHeroJobData");
        var jobId = ReadNullableInt(heroSave, "jobId") ?? ReadNullableInt(jobRow, "id") ?? 0;
        var allowedWeapons = ReadSequence(Read(jobRow, "baseWeaponTypeArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
        var baseWeaponRequirement = new HashSet<int>();
        var skillWeaponPreferences = new List<HashSet<int>>();
        var activeSkillMainType = 0;
        var activeSkillTags = new HashSet<int>();
        var previewBaseSkillId = 0;
        var currentBaseSkill = InvokeInstance(hero, "GetNowBaseSkillData");
        var previewBaseSkillLevel = Math.Max(1, ReadNullableInt(currentBaseSkill, "level") ?? 1);
        var skillIds = new HashSet<int>();
        var skillInfoIds = new HashSet<int>();
        var talentIds = new HashSet<int>();
        var masteryIds = new HashSet<int>();
        var preferredTalentIds = new HashSet<int>();
        var preferredSkillIds = new HashSet<int>();
        var preferredMasteryIds = new HashSet<int>();
        var abilityIds = new HashSet<int>();
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var equippedItems = GetGearSlots()
            .Select(slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon))
            .Where(item => item is not null).Cast<object>()
            .DistinctBy(item => NativeObjectKey(item, item)).ToList();
        var equippedAffixes = equippedItems
            .SelectMany(item => CollectEquipmentAffixes(item).Concat(CollectGrantedMasteryAffixes(item)))
            .ToList();
        var equipmentAbilityIds = equippedAffixes
            .Select(affix => ReadNullableInt(Read(ResolveRuntimeAffix(affix), "tAbilityData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var equipmentTalentIds = equippedItems.SelectMany(CollectRunewordAffixes)
            .Select(ResolveRuntimeAffix)
            .Select(affix => ReadNullableInt(Read(ResolveRuntimeAffix(affix), "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        foreach (var setEffect in ReadList(Read(Read(hero, "heroEquipData"), "activeSetsEffectList")))
        {
            var id = ReadNullableInt(Read(setEffect, "tSetEffectData"), "abilityId") ?? 0;
            if (id > 0) equipmentAbilityIds.Add(id);
        }

        void AddTerm(string? value)
        {
            var cleaned = Clean(value ?? string.Empty).ToLowerInvariant();
            if (cleaned.Length >= 3 && cleaned.Length <= 80) terms.Add(cleaned);
        }

        void AddSkill(object? skill, bool isBase = false)
        {
            if (skill is null) return;
            var row = Read(skill, "tSkillData") ?? skill;
            var skillId = ReadNullableInt(row, "id") ?? 0;
            if (skillId > 0) skillIds.Add(skillId);
            var info = Read(skill, "tSkillInfoData") ?? (ReadNullableInt(row, "infoId") is > 0 and var infoId ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null);
            var resolvedInfoId = ReadNullableInt(info, "id") ?? ReadNullableInt(row, "infoId") ?? 0;
            if (resolvedInfoId > 0) skillInfoIds.Add(resolvedInfoId);
            var required = ReadSequence(Read(row, "weaponArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
            if (required.Count > 0)
            {
                if (isBase)
                {
                    baseWeaponRequirement.Clear();
                    baseWeaponRequirement.UnionWith(required);
                }
                else if (!skillWeaponPreferences.Any(group => group.SetEquals(required))) skillWeaponPreferences.Add(required);
            }
            if (isBase)
            {
                previewBaseSkillId = skillId;
                previewBaseSkillLevel = Math.Max(1, ReadNullableInt(skill, "level") ?? previewBaseSkillLevel);
                activeSkillMainType = ReadNullableInt(skill, "skillMainType") ?? ReadNullableInt(row, "type") ?? 0;
                activeSkillTags = ReadSequence(Read(info, "tagArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
                var subType = ReadNullableInt(skill, "skillSubType") ?? 0;
                var rangeType = ReadNullableInt(skill, "skillRangeType") ?? 0;
                if (subType > 0) activeSkillTags.Add(subType);
                if (rangeType > 0) activeSkillTags.Add(rangeType);
            }
            AddTerm(ReadString(row, "name"));
            AddTerm(EnglishName(row, string.Empty));
        }

        AddSkill(currentBaseSkill, true);
        var heroTalent = Read(hero, "heroTalentData");
        var gridTalents = ReadValues(Read(heroTalent, "talentDic")).ToList();
        var extraTalents = ReadList(Read(heroTalent, "extraTalentList"))
            .Where(talent => !equipmentTalentIds.Contains(ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0));
        foreach (var talent in gridTalents.Concat(extraTalents)
                     .DistinctBy(value => NativeObjectKey(value, value)))
        {
            if (Convert.ToInt32(InvokeInstance(talent, "GetLevel") ?? 0, CultureInfo.InvariantCulture) <= 0) continue;
            var talentRow = Read(talent, "tTalentData");
            var talentId = ReadNullableInt(talentRow, "id") ?? 0;
            var masteryId = ReadNullableInt(talentRow, "masteryId") ?? ReadNullableInt(Read(Read(talent, "masteryData"), "tMasteryData"), "id") ?? 0;
            if (talentId > 0) talentIds.Add(talentId);
            if (masteryId > 0) masteryIds.Add(masteryId);
            AddTerm(ReadString(talentRow, "name"));
            AddTerm(EnglishName(talentRow, string.Empty));
            var mastery = Read(Read(talent, "masteryData"), "tMasteryData");
            AddTerm(ReadString(mastery, "name"));
            AddTerm(EnglishName(mastery, string.Empty));
            AddSkill(Read(talent, "skillData"));
        }

        foreach (var ability in ReadList(Read(hero, "abilityList")))
        {
            var row = Read(ability, "tAbilityData");
            var id = ReadNullableInt(ability, "id") ?? ReadNullableInt(row, "id") ?? 0;
            // Equipped ability and active set effects describe the current
            // loadout, not the hero's intended build. Feeding them back into a
            // candidate score unfairly locks the optimizer to the old equipment.
            if (id > 0 && equipmentAbilityIds.Contains(id)) continue;
            if (id > 0) abilityIds.Add(id);
            AddTerm(ReadString(row, "name"));
            AddTerm(EnglishName(row, string.Empty));
        }

        // Official build rows are intentionally not fed back into equipment or
        // skill scoring. They are used only as a safe catalogue of transformable
        // talent identities; live stats, learned effects and the chosen objective
        // decide the ranking.
        var recommendedEquipment = new HashSet<int>();
        var recommendedRunewordKeys = new HashSet<string>(StringComparer.Ordinal);

        return new HeroEffectProfile(focus, jobId, allowedWeapons, baseWeaponRequirement, skillWeaponPreferences, activeSkillMainType, activeSkillTags, previewBaseSkillId, previewBaseSkillLevel, skillIds, skillInfoIds, talentIds, masteryIds, preferredTalentIds, preferredSkillIds, preferredMasteryIds, abilityIds, recommendedEquipment, recommendedRunewordKeys, terms.ToArray());
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
        var textHintMatches = Math.Min(3, profile.SkillTerms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)));
        var themeMatches = KeywordScore(text, profile.Focus.Keywords);
        var focusBonus = themeMatches * (profile.Focus.IsManual ? 70d : 38d) + textHintMatches * 28d;
        var generalBonus = KeywordScore(text, new[] { "all attack", "all defense", "primary attribute", "crit", "speed", "cost", "resist", "health" }) * 12d;
        // Guide equipment is retained in the shortlist for comparison, but it
        // receives no score bonus. Native stats/effects decide the winner.
        const double guideBonus = 0d;
        var total = qualityWeight * 0.2d + level * 0.2d + forge * 0.5d + main * 0.002d
                    + focusBonus + generalBonus + directMatches * 350d + behaviorScore + guideBonus;
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
            var skillId = GetSkillVariantId(definition);
            if (skillId > 0 && profile.SkillIds.Contains(skillId)) return 800d;
            return 0d;
        }

        if (effectType == 100)
        {
            var affixSave = Read(affix, "saveData");
            var talent = Read(affix, "tTalentData")
                         ?? InvokeStatic("TableData", "getTTalentData", ReadNullableInt(affixSave, "talentId") ?? 0);
            var talentId = ReadNullableInt(talent, "id") ?? 0;
            var talentType = ReadNullableInt(talent, "type") ?? 0;
            var skillId = ReadNullableInt(talent, "skillId") ?? 0;
            var masteryId = ReadNullableInt(talent, "masteryId") ?? 0;
            var current = (talentId > 0 && profile.TalentIds.Contains(talentId))
                          || (skillId > 0 && profile.SkillIds.Contains(skillId))
                          || (masteryId > 0 && profile.MasteryIds.Contains(masteryId));
            // Native deduplicates granted skills by actual TSkill id and keeps
            // the highest talentLevel. Give that level a bounded heuristic
            // value so Lv10 does not tie Lv1 before the final native proxy.
            var grantedSkillLevel = talentType == 1
                ? Math.Max(0, ReadNullableInt(affixSave, "talentLevel") ?? 0)
                : 0;
            var levelValue = Math.Min(600d, grantedSkillLevel * 30d);
            return (current ? 800d : 0d) + levelValue;
        }

        if (effectType == 3)
        {
            // Ability affixes are conditional combat behaviours and cannot be
            // safely executed on the preview AttrData without a CombatData target.
            // Give only a bounded semantic score; never feed the currently
            // equipped ability ID back into the optimizer as a preference.
            var skillMatches = Math.Min(3, profile.SkillTerms.Count(term =>
                affixText.Contains(term, StringComparison.OrdinalIgnoreCase)));
            var themeMatches = Math.Min(3, KeywordScore(affixText, profile.Focus.Keywords));
            return Math.Min(600d, skillMatches * 160d + themeMatches * 90d);
        }

        return 0d;
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
            7 or 9 or 93 or 94 => profile.Focus.Key == "defense" ? 1.2d : 0.25d,
            11 or 12 or 13 => 0.55d,
            25 or 190 => profile.Focus.Key == "minion" ? 18d : 1.5d,
            81 or 82 or 191 => profile.Focus.Key == "support" ? 18d : 1.5d,
            31 or 37 or 41 or 42 or 51 or 52 or 53 or 54 or 55 or 56
                or 71 or 72 or 75 or 76 or 99 or 100 or 101 or 102 or 106 or 107 or 108
                or 110 or 111 or 112 or 113 or 114 or 115 or 171 or 172 or 218 => 16d,
            _ => 0.08d
        };

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

            // Score only thresholds that are actually active at this piece
            // count. Including a future 4-piece description while evaluating a
            // 2-piece loadout can invert Fire/Lightning theme decisions.
            var setText = Clean(string.Join(" ", activeEffects.Select(effect => effect.Text))).ToLowerInvariant();
            var setThemeMatches = KeywordScore(setText, profile.Focus.Keywords);
            var opposingElementMatches = GetOpposingElementThemeScore(setText, profile.Focus.Key);
            const bool preferredSet = false;
            var activeSkillMatches = activeEffects.Sum(effect => Math.Min(3,
                profile.SkillTerms.Count(term => effect.Text.Contains(term, StringComparison.OrdinalIgnoreCase))));

            // A specific element is a build constraint even when it was inferred
            // automatically. Do not let an unrelated 2-piece bonus win merely
            // because every active set used to receive an unconditional score.
            // One off-theme filler piece is still allowed; the penalty starts only
            // when its set effect turns on. Automatic inference uses a softer cost.
            if (IsSpecificElementFocus(profile.Focus.Key)
                && setThemeMatches == 0 && activeSkillMatches == 0 && !preferredSet
                && opposingElementMatches > 0)
            {
                score -= (profile.Focus.IsManual ? 6500d : 3600d) + activeEffects.Count * 1400d;
                continue;
            }

            foreach (var effect in activeEffects)
            {
                var effectThemeMatches = KeywordScore(effect.Text, profile.Focus.Keywords);
                var effectSkillMatches = Math.Min(3,
                    profile.SkillTerms.Count(term => effect.Text.Contains(term, StringComparison.OrdinalIgnoreCase)));
                var aligned = setThemeMatches > 0 || effectSkillMatches > 0 || preferredSet;
                if (!IsSpecificElementFocus(profile.Focus.Key) || aligned) score += 450d;
                score += effectThemeMatches * 260d;
                score += effectSkillMatches * 700d;
            }

            if (setThemeMatches > 0 || activeSkillMatches > 0)
                score += count * 220d + activeEffects.Count * 650d;
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
                AbilityId = ReadNullableInt(effect, "abilityId") ?? 0
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
                        entry.AbilityId))
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

    private static double ScoreCompleteLoadout(List<GearCandidate> items, object hero, HeroEffectProfile profile, List<object> currentItems)
    {
        var score = ScoreItemsWithDeduplicatedEffects(items, profile) + EstimatePartialSetSynergy(items, profile);
        var weaponTypes = items.Where(item => item.Part == 1).Select(item => item.WeaponType).Where(value => value > 0).ToHashSet();
        if (profile.BaseWeaponRequirement.Count > 0)
            score += profile.BaseWeaponRequirement.Overlaps(weaponTypes) ? 3200d : -30000d;
        foreach (var preference in profile.SkillWeaponPreferences)
            score += preference.Overlaps(weaponTypes) ? 420d : -12000d;

        // Use the game's own AttrData calculations on a temporary copy. This keeps
        // the user's hero and save untouched while accounting for core stats,
        // ordinary affixes, percentage conversions, crit and skill-speed buckets.
        if (TryEvaluateFinalPerformance(items, hero, profile, currentItems, out var performance))
            score += performance * 2.4d;
        return score;
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

    private static bool TryEvaluateFinalPerformance(List<GearCandidate> items, object hero, HeroEffectProfile profile, List<object> currentItems, out double score)
    {
        score = 0d;
        try
        {
            var heroAttr = Read(hero, "attrData") ?? throw new InvalidOperationException("Hero AttrData is unavailable.");
            var simulated = InvokeRequiredStaticMany("AttrData", "copyCreate", heroAttr)
                            ?? throw new InvalidOperationException("AttrData copy could not be created.");
            foreach (var current in currentItems) ApplyEquipmentToAttr(simulated, current, false);
            foreach (var candidate in items) ApplyEquipmentToAttr(simulated, candidate.Record.ItemData, true);

            // Prefer the same preview path used by the game UI. PowerData resolves
            // the selected base skill's own physical/elemental mix, damage types,
            // attribute coefficients and class scaling against this temporary
            // AttrData. Nothing on the real hero or save is changed.
            var hasNativeSkillDamage = TryEvaluateNativeBaseSkillSustainedDamage(simulated, hero, profile, items, out var sustainedDamage60s);
            if (!hasNativeSkillDamage)
            {
                var phy = ReadAttrRequired(simulated, 1);
                var ele = ReadAttrRequired(simulated, 2);
                var physicalTheme = profile.Focus.Key is "physical" or "bleed";
                var elementalTheme = profile.Focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion";
                var attack = physicalTheme ? phy : elementalTheme ? ele : Math.Max(phy, ele);
                // Fallback for passive/support skills without a native damage row.
                // GetAttrValue(phy/ele attack) already applies native attack-up.
                var deliveryAttr = profile.ActiveSkillTags.Contains(11) ? 75 : profile.ActiveSkillTags.Contains(12) ? 76 : 0;
                var categoryAttr = profile.ActiveSkillMainType switch { 1 => 106, 2 => 107, 3 => 108, _ => 0 };
                var deliveryDamage = deliveryAttr > 0 ? ReadAttrRequired(simulated, deliveryAttr) : 0d;
                var categoryDamage = categoryAttr > 0 ? ReadAttrRequired(simulated, categoryAttr) : 0d;
                var rangedDamage = profile.ActiveSkillTags.Contains(22) ? ReadAttrRequired(simulated, 172) : 0d;
                var typeDamage = GetDamageTypeBuckets(simulated, profile.Focus.Key);
                var regularDamageBucket = typeDamage.Regular + deliveryDamage + categoryDamage + rangedDamage;
                var extraDamageBucket = typeDamage.Extra + ReadAttrRequired(simulated, 218);
                var critChance = Math.Clamp(PercentRate(ReadAttrRequired(simulated, 31)), 0d, 1d);
                var critDamage = Math.Max(0.5d, 0.5d + PercentRate(ReadAttrRequired(simulated, 37)));
                var deliverySpeedAttr = profile.ActiveSkillTags.Contains(11) ? 71 : profile.ActiveSkillTags.Contains(12) ? 72 : 0;
                var categorySpeedAttr = profile.ActiveSkillMainType switch { 1 => 100, 2 => 99, 3 => 101, 4 => 102, _ => 0 };
                var speedBucket = (deliverySpeedAttr > 0 ? ReadAttrRequired(simulated, deliverySpeedAttr) : 0d)
                                  + (categorySpeedAttr > 0 ? ReadAttrRequired(simulated, categorySpeedAttr) : 0d)
                                  + (profile.ActiveSkillTags.Contains(22) ? ReadAttrRequired(simulated, 171) : 0d);
                // Approximate one action per second for skills whose native damage
                // preview cannot be resolved, then compare the same 60-second window.
                sustainedDamage60s = Math.Max(0d, attack) * NativeRateFactor(regularDamageBucket)
                                     * NativeRateFactor(extraDamageBucket) * Math.Max(0.1d, 1d + critChance * critDamage)
                                     * NativeRateFactor(speedBucket) * 60d;
            }

            var hp = Math.Max(0d, ReadAttrRequired(simulated, 5));
            var defence = Math.Max(0d, ReadAttrRequired(simulated, 3) + ReadAttrRequired(simulated, 4));
            var sustain = Math.Max(0d, ReadAttrRequired(simulated, 7) + ReadAttrRequired(simulated, 9) + ReadAttrRequired(simulated, 93) + ReadAttrRequired(simulated, 94));
            var support = Math.Max(0d, ReadAttrRequired(simulated, 81) + ReadAttrRequired(simulated, 82) + ReadAttrRequired(simulated, 191));
            var minion = Math.Max(0d, ReadAttrRequired(simulated, 25) * 50d + ReadAttrRequired(simulated, 190));
            var damageScore = Math.Log10(1d + sustainedDamage60s) * (profile.Focus.Key is "support" or "defense" ? 900d : 1700d);
            var survivalScore = Math.Log10(1d + hp + defence * 2d + sustain * 4d) * (profile.Focus.Key is "defense" ? 1300d : 420d);
            var utilityScore = Math.Log10(1d + support * 10d) * (profile.Focus.Key == "support" ? 1200d : 180d);
            var minionScore = Math.Log10(1d + minion) * (profile.Focus.Key == "minion" ? 1100d : 120d);
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
        => skillId > 0 && items.SelectMany(candidate => CollectEquipmentAffixes(candidate.Record.ItemData))
            .Select(ResolveRuntimeAffix)
            .Select(affix => Read(affix, "tAffixData"))
            .Where(definition => (ReadNullableInt(definition, "effectType") ?? 0) == 4)
            .Select(GetSkillVariantId)
            .Any(id => id == skillId);

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
            var value = direct * 350d + Math.Max(0d, behavior);
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

    private static SkillVariantVerification VerifyEquippedSkillVariants(object hero, object talentData, string scope)
    {
        var skills = ReadList(InvokeRequiredInstance(talentData, "GetSkillList")).ToList();
        var available = skills.Select(skill => ReadNullableInt(Read(skill, "tSkillData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var actual = skills.Where(skill => ReadBool(Read(skill, "isVariant")))
            .Select(skill => ReadNullableInt(Read(skill, "tSkillData"), "id") ?? 0)
            .Where(id => id > 0).Distinct().ToList();
        var expected = GetGearSlots().Select(slot => GetEquippedItem(hero, slot.Part, slot.MainWeapon))
            .Where(item => item is not null).Cast<object>()
            .SelectMany(CollectEquipmentAffixes)
            .Select(ResolveRuntimeAffix)
            .Select(affix => Read(affix, "tAffixData"))
            .Where(definition => (ReadNullableInt(definition, "effectType") ?? 0) == 4)
            .Select(GetSkillVariantId)
            .Where(id => id > 0 && available.Contains(id)).Distinct().ToHashSet();
        var missing = expected.Where(id => !actual.Contains(id)).ToList();
        var unexpected = actual.Where(id => !expected.Contains(id)).ToList();
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

    [Conditional("PATHOFIDLE_DIAGNOSTICS")]
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
