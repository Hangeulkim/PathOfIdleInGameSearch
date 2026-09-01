using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
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
    public const string PluginVersion = "1.1.2";

    internal static ManualLogSource Logger { get; private set; } = null!;
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
        Logger = Log;
        SavedQuery = Config.Bind("Search", "LastQuery", string.Empty, "Last in-game item search query.");
        IncludeWarehouse = Config.Bind("Search", "IncludeWarehouse", true, "Include warehouse and vault items.");
        WindowX = Config.Bind("Window", "X", 48f, "Search panel X position.");
        WindowY = Config.Bind("Window", "Y", 72f, "Search panel Y position.");
        OpenOnStart = Config.Bind("Window", "OpenOnStart", false, "Open the search panel when the game starts.");
        GameSpeed = Config.Bind("Speed", "Multiplier", 1f, "Runtime speed: 0.5 through 100.");
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
        AutoTransformSkills = Config.Bind("AutoBuild", "TransformMissingSkills", true, "Use the shrine's normal paid skill transformation to seek missing recommended skills before allocating points.");
        AutoTransformMaxAttempts = Config.Bind("AutoBuild", "MaxSkillTransformAttempts", 12, "Maximum paid skill transformations per automatic skill run.");
        InstallWheelPatch();
        AddComponent<InGameSearchOverlay>();
        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Press F3 or Ctrl+F to open it.");
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

            Logger.LogInfo("Focused overlay mouse-wheel input guard installed.");
        }
        catch (Exception error)
        {
            Logger.LogWarning($"Mouse-wheel input guard unavailable: {error.Message}");
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
            Plugin.Logger.LogInfo("Character wheel selection blocked while the pointer is over the focused overlay.");
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
    private const int ResultsPerPage = 6;
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
    private AutoBuildAction armedAutoBuild;
    private float autoBuildConfirmUntil;
    private int equipmentBoxCount;
    private int runeBoxCount;
    private IMECompositionMode previousImeMode;
    private bool imeModeSaved;
    private GameObject? inputBlockerCanvasObject;
    private GameObject? inputBlockerRegionObject;
    private RectTransform? inputBlockerRect;
    private bool keyboardBlockCaptured;
    private bool previousGameKeyboardBlocked;
    private bool navigationBlockCaptured;
    private bool previousNavigationEventsEnabled;
    private bool keyboardReleasePending;
    private bool keyboardBlockUnavailableLogged;
    private bool overlayInputFocused;
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
        currentSpeed = Mathf.Clamp(Plugin.GameSpeed.Value, 0.1f, 100f);
        speedInput = currentSpeed.ToString("0.##", CultureInfo.InvariantCulture);
        ApplyGameSpeed();
        ClampWindowToScreen();
        Plugin.Logger.LogInfo("In-game search overlay started.");
        if (Plugin.OpenOnStart.Value) SetVisible(true);
    }

    public void Update()
    {
        if (Math.Abs(Time.timeScale - currentSpeed) > 0.001f) Time.timeScale = currentSpeed;
        var controlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (Input.GetKeyDown(KeyCode.F3) || (controlDown && Input.GetKeyDown(KeyCode.F)))
        {
            Plugin.Logger.LogInfo("Search hotkey received.");
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

        if (Time.unscaledTime >= nextRefreshAt)
        {
            nextRefreshAt = Time.unscaledTime + 1f;
            RefreshCurrentPage(resetStatus: false);
        }
    }

    public void OnGUI()
    {
        if (!visible) return;
        var focusEvent = Event.current;
        if (focusEvent is not null && focusEvent.type == EventType.MouseDown)
        {
            overlayInputFocused = windowRect.Contains(focusEvent.mousePosition);
            if (!overlayInputFocused)
            {
                focusSearch = false;
                focusSpeedInput = false;
            }
        }
        EnsureStyles();
        ClampWindowToScreen();
        HandleWindowDrag();
        hoveredItem = null;

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
        HandleSpeedInput(speedRect);
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
            DrawBulkOpenPanel(new Rect(left, windowRect.y + 78f, width, 360f));
            ConsumeRemainingKeyboardEvent();
            return;
        }

        if (selectedPage == OverlayPage.AutoBuild)
        {
            DrawAutoBuildPanel(new Rect(left, windowRect.y + 78f, width, 610f));
            ConsumeRemainingKeyboardEvent();
            return;
        }

        var searchRect = new Rect(left, windowRect.y + 70f, width, 38f);
        HandleSearchInput(searchRect);
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
        GUI.Label(new Rect(left + 372f, windowRect.y + 117f, width - 372f, 24f), status, hintStyle!);

        DrawQualityFilters(new Rect(left, windowRect.y + 150f, width, 34f));
        var inventoryCount = 0;
        var warehouseCount = 0;
        var selectedMatches = new List<ItemSearchRecord>();
        foreach (var item in matches)
        {
            if (item.StorageKind == StorageKind.Inventory) inventoryCount++; else warehouseCount++;
            if (item.StorageKind == selectedStorage) selectedMatches.Add(item);
        }
        DrawStorageTab(new Rect(left, windowRect.y + 194f, 190f, 32f), StorageKind.Inventory, $"{UiText.L("인벤토리", "INVENTORY", "背包", "背包")}  {inventoryCount}", new Color(0.32f, 0.86f, 0.46f));
        DrawStorageTab(new Rect(left + 198f, windowRect.y + 194f, 190f, 32f), StorageKind.Warehouse, $"{UiText.L("창고", "WAREHOUSE", "仓库", "倉庫")}  {warehouseCount}", new Color(0.25f, 0.78f, 0.92f));

        var pageCount = Math.Max(1, (int)Math.Ceiling(selectedMatches.Count / (double)ResultsPerPage));
        currentPage = Math.Max(0, Math.Min(currentPage, pageCount - 1));
        var resultArea = new Rect(left, windowRect.y + 236f, width, 470f);
        var currentEvent = Event.current;
        if (currentEvent.type == EventType.ScrollWheel && resultArea.Contains(currentEvent.mousePosition))
        {
            currentPage = Math.Max(0, Math.Min(pageCount - 1, currentPage + (currentEvent.delta.y > 0f ? 1 : -1)));
            currentEvent.Use();
        }

        if (selectedMatches.Count == 0)
        {
            GUI.Label(new Rect(left + 12f, windowRect.y + 268f, width - 24f, 40f), allItems.Count == 0
                ? UiText.L("인벤토리 데이터를 기다리는 중입니다.", "Waiting for inventory data.", "正在等待背包数据。", "正在等待背包資料。")
                : UiText.L("이 구역에는 검색 조건에 맞는 아이템이 없습니다.", "No matching items in this section.", "此区域没有匹配的物品。", "此區域沒有相符的物品。"), hintStyle!);
        }
        else
        {
            var pageItems = selectedMatches.Skip(currentPage * ResultsPerPage).Take(ResultsPerPage).ToList();
            for (var index = 0; index < pageItems.Count; index++)
                DrawResult(pageItems[index], new Rect(left, windowRect.y + 236f + index * 76f, width, 70f));
        }

        if (GUI.Button(new Rect(left, windowRect.yMax - 43f, 88f, 28f), UiText.L("◀ 이전", "◀ Previous", "◀ 上一页", "◀ 上一頁"), compactButtonStyle!) && currentPage > 0) currentPage--;
        GUI.Label(new Rect(left + 94f, windowRect.yMax - 43f, 90f, 28f), $"{currentPage + 1} / {pageCount}", pageStyle!);
        if (GUI.Button(new Rect(left + 190f, windowRect.yMax - 43f, 88f, 28f), UiText.L("다음 ▶", "Next ▶", "下一页 ▶", "下一頁 ▶"), compactButtonStyle!) && currentPage + 1 < pageCount) currentPage++;
        GUI.Label(new Rect(left + 294f, windowRect.yMax - 40f, width - 294f, 24f), UiText.L("검색어가 있으면 일치한 아이템만 표시됩니다.", "A search shows matching items only.", "输入搜索词后仅显示匹配物品。", "輸入搜尋詞後僅顯示相符物品。"), hintStyle!);

        if (hoveredItem is not null) DrawItemTooltip(hoveredItem);

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
        Plugin.Logger.LogInfo($"Search panel visible={visible}.");
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
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Localized input blocker unavailable: {error.Message}");
            DestroyInputBlocker();
        }
    }

    [HideFromIl2Cpp]
    private void SetInputBlockerActive(bool active)
    {
        if (inputBlockerRegionObject is not null) inputBlockerRegionObject.SetActive(active);
    }

    [HideFromIl2Cpp]
    private void DestroyInputBlocker()
    {
        if (inputBlockerCanvasObject is not null) UnityEngine.Object.Destroy(inputBlockerCanvasObject);
        inputBlockerCanvasObject = null;
        inputBlockerRegionObject = null;
        inputBlockerRect = null;
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
                Plugin.Logger.LogWarning("Game keyboard input guard is waiting for Game.keyMgr.");
            }
            return;
        }

        keyboardBlockUnavailableLogged = false;
        if (!keyboardBlockCaptured)
        {
            previousGameKeyboardBlocked = previous;
            keyboardBlockCaptured = true;
            Plugin.Logger.LogInfo($"Game keyboard input blocked (previous={previousGameKeyboardBlocked}).");
        }

        if (!GameInputGuard.TrySetUiNavigationBlocked(true, out var previousNavigation) || navigationBlockCaptured) return;
        previousNavigationEventsEnabled = previousNavigation;
        navigationBlockCaptured = true;
        Plugin.Logger.LogInfo($"Game UI keyboard navigation blocked (previous={previousNavigationEventsEnabled}).");
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
            Plugin.Logger.LogInfo($"Game keyboard input restored to {previousGameKeyboardBlocked}.");
            keyboardBlockCaptured = false;
        }
        if (navigationBlockCaptured && (GameInputGuard.TrySetUiNavigationBlocked(!previousNavigationEventsEnabled, out _) || force))
        {
            Plugin.Logger.LogInfo($"Game UI keyboard navigation restored to {previousNavigationEventsEnabled}.");
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
        if (!visible || !overlayInputFocused) return false;
        var mouse = Input.mousePosition;
        return windowRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
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
        Plugin.Logger.LogInfo($"Mod UI language: {code} (mode={languageMode}).");
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
        RefreshCurrentPage();
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
    }

    private void RefreshCurrentPage(bool resetStatus = true)
    {
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
            RefreshBulkCounts();
            if (resetStatus) status = UiText.L("개봉할 상자 종류와 등급을 선택하세요.", "Choose a box type and quality.", "请选择箱子类型和品质。", "請選擇箱子類型和品質。");
            return;
        }
        RefreshItems();
    }

    private void RefreshBulkCounts()
    {
        var counts = GameInventoryReader.GetBulkToolCounts(Plugin.BulkQualityMask.Value, Plugin.BulkQualityAtLeast.Value);
        equipmentBoxCount = counts.EquipmentBoxes;
        runeBoxCount = counts.RuneBoxes;
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
        currentSpeed = Mathf.Clamp(speed, 0.1f, 100f);
        speedInput = currentSpeed.ToString("0.##", CultureInfo.InvariantCulture);
        ApplyGameSpeed();
    }

    private void ApplyCustomSpeed()
    {
        var normalized = speedInput.Trim().Replace(',', '.');
        if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
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
        Plugin.Logger.LogInfo($"Game speed set to {currentSpeed:0.##}x.");
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
        var header = new Rect(windowRect.x, windowRect.y, windowRect.width - 382f, 52f);
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
        var maxX = Math.Max(0f, Screen.width - WindowWidth);
        var maxY = Math.Max(0f, Screen.height - WindowHeight);
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, maxX);
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, maxY);
    }

    [HideFromIl2Cpp]
    private void DrawResult(ItemSearchRecord item, Rect rect)
    {
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
        if (GUI.Button(new Rect(rect.xMax - 140f, rect.y + 4f, 130f, 28f), transferLabel, compactButtonStyle!)) TransferItem(item);
        var level = item.Level is > 0 ? $"Lv.{item.Level}" : UiText.L("레벨 미상", "Unknown level", "等级未知", "等級未知");
        GUI.Label(new Rect(rect.x + 10f, rect.y + 27f, rect.width - 150f, 19f), HighlightMatches($"{item.StorageLabel}  ·  {item.PartName}  ·  {level}", item.StorageKind), resultMetaStyle!);
        var optionPreview = string.IsNullOrWhiteSpace(item.SetName)
            ? item.AffixSummary
            : $"{UiText.L("세트", "Set", "套装", "套裝")} {item.SetName}  ·  {item.AffixSummary}".TrimEnd(' ', '·');
        if (!string.IsNullOrWhiteSpace(optionPreview))
            GUI.Label(new Rect(rect.x + 10f, rect.y + 47f, rect.width - 150f, 18f), HighlightMatches(optionPreview, item.StorageKind), resultAffixStyle!);
        if (rect.Contains(Event.current.mousePosition)) hoveredItem = item;
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
        var bodyHeight = tooltipBodyStyle!.CalcHeight(new GUIContent(body), tooltipWidth - 28f);
        var tooltipHeight = Mathf.Clamp(bodyHeight + 58f, 190f, Screen.height - 20f);
        var x = windowRect.xMax + 8f;
        if (x + tooltipWidth > Screen.width - 10f) x = Math.Max(10f, Screen.width - tooltipWidth - 10f);
        var y = Mathf.Clamp(Event.current.mousePosition.y - 24f, 10f, Math.Max(10f, Screen.height - tooltipHeight - 10f));
        var rect = new Rect(x, y, tooltipWidth, tooltipHeight);
        var previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.025f, 0.025f, 0.035f, 1f);
        for (var layer = 0; layer < 9; layer++) GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.Box(rect, GUIContent.none, panelStyle!);
        GUI.backgroundColor = previousBackground;
        GUI.Label(new Rect(rect.x + 14f, rect.y + 10f, rect.width - 28f, 26f), item.Name, tooltipTitleStyle!);
        GUI.Label(new Rect(rect.x + 14f, rect.y + 40f, rect.width - 28f, rect.height - 52f), body, tooltipBodyStyle!);
    }

    [HideFromIl2Cpp]
    private void TransferItem(ItemSearchRecord item)
    {
        if (Time.unscaledTime < transferCooldownUntil) return;
        transferCooldownUntil = Time.unscaledTime + 0.75f;
        if (!GameInventoryReader.TryTransfer(item, out var message))
        {
            status = message;
            Plugin.Logger.LogWarning($"Item transfer failed: {message}");
            return;
        }

        status = message;
        Plugin.Logger.LogInfo(message);
        currentPage = 0;
        nextRefreshAt = Time.unscaledTime + 0.25f;
        RefreshItems();
    }

    [HideFromIl2Cpp]
    private void DrawQualityFilters(Rect rect)
    {
        GUI.Label(new Rect(rect.x + 4f, rect.y + 7f, 40f, 20f), UiText.L("등급", "Tier", "品质", "品質"), utilityTitleStyle!);
        var x = rect.x + 46f;
        DrawQualityButton(new Rect(x, rect.y + 3f, 54f, 28f), 0, UiText.L("전체", "All", "全部", "全部")); x += 58f;
        DrawQualityButton(new Rect(x, rect.y + 3f, 54f, 28f), 3, UiText.L("희귀", "Rare", "稀有", "稀有")); x += 58f;
        DrawQualityButton(new Rect(x, rect.y + 3f, 54f, 28f), 4, UiText.L("전설", "Legend", "传奇", "傳奇")); x += 58f;
        DrawQualityButton(new Rect(x, rect.y + 3f, 54f, 28f), 5, UiText.L("신화", "Mythic", "神话", "神話")); x += 58f;
        DrawQualityButton(new Rect(x, rect.y + 3f, 54f, 28f), 6, UiText.L("세트", "Set", "套装", "套裝")); x += 58f;
        DrawQualityButton(new Rect(x, rect.y + 3f, 54f, 28f), 8, UiText.L("고유", "Unique", "独特", "獨特")); x += 58f;
        DrawQualityButton(new Rect(x, rect.y + 3f, 54f, 28f), -1, UiText.L("기타", "Other", "其他", "其他"));

        var nextOptionsOnly = GUI.Toggle(new Rect(rect.xMax - 128f, rect.y + 7f, 124f, 22f), searchOptionsOnly, UiText.L(" 옵션만 검색", " Affixes only", " 仅搜索词缀", " 僅搜尋詞綴"), toggleStyle!);
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
            status = nextAutoStore
                ? UiText.L("개봉 장비를 게임 규칙에 따라 자동 보관합니다.", "Opened gear follows the game's automatic storage rules.", "开启的装备将按游戏规则自动入库。", "開啟的裝備將按遊戲規則自動入庫。")
                : UiText.L("개봉 장비를 인벤토리에 남깁니다.", "Opened gear stays in the inventory.", "开启的装备将留在背包。", "開啟的裝備將留在背包。");
        }
        DrawBulkOpenButton(new Rect(rect.x + 18f, rect.y + 130f, (rect.width - 42f) / 2f, 52f), BulkToolKind.EquipmentBox, equipmentBoxCount, UiText.L("장비 상자", "Gear boxes", "装备箱", "裝備箱"));
        DrawBulkOpenButton(new Rect(rect.x + 24f + (rect.width - 42f) / 2f, rect.y + 130f, (rect.width - 42f) / 2f, 52f), BulkToolKind.RuneBox, runeBoxCount, UiText.L("룬 상자", "Rune boxes", "符文箱", "符文箱"));

        GUI.Label(new Rect(rect.x + 18f, rect.y + 202f, 110f, 24f), UiText.L("상자 등급", "Box Quality", "箱子品质", "箱子品質"), utilityTitleStyle!);
        var x = rect.x + 18f;
        DrawBulkQualityButton(new Rect(x, rect.y + 232f, 78f, 34f), 0, UiText.L("전체", "All", "全部", "全部")); x += 84f;
        DrawBulkQualityButton(new Rect(x, rect.y + 232f, 78f, 34f), 3, UiText.L("희귀", "Rare", "稀有", "稀有")); x += 84f;
        DrawBulkQualityButton(new Rect(x, rect.y + 232f, 78f, 34f), 4, UiText.L("전설", "Legend", "传奇", "傳奇")); x += 84f;
        DrawBulkQualityButton(new Rect(x, rect.y + 232f, 78f, 34f), 5, UiText.L("신화", "Mythic", "神话", "神話")); x += 84f;
        DrawBulkQualityButton(new Rect(x, rect.y + 232f, 78f, 34f), 6, UiText.L("세트", "Set", "套装", "套裝")); x += 84f;
        DrawBulkQualityButton(new Rect(x, rect.y + 232f, 78f, 34f), 8, UiText.L("고유", "Unique", "独特", "獨特")); x += 84f;
        DrawBulkQualityButton(new Rect(x, rect.y + 232f, 78f, 34f), -1, UiText.L("기타", "Other", "其他", "其他"));
        var nextAtLeast = GUI.Toggle(new Rect(rect.x + 18f, rect.y + 281f, 170f, 24f), Plugin.BulkQualityAtLeast.Value, UiText.L(" 선택 등급 이상 모두", " Selected or higher", " 所选品质及以上", " 所選品質以上"), toggleStyle!);
        if (nextAtLeast != Plugin.BulkQualityAtLeast.Value)
        {
            Plugin.BulkQualityAtLeast.Value = nextAtLeast;
            armedBulkOpen = BulkToolKind.None;
            RefreshBulkCounts();
        }
        GUI.Label(new Rect(rect.x + 18f, rect.y + 318f, rect.width - 36f, 28f), status, hintStyle!);
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
            "게임에서 영웅을 먼저 선택하세요. 추천 점수는 직업, 능력치, 스킬 설명과 장비 옵션을 함께 사용합니다.",
            "Select a hero in the game first. Recommendations combine job, attributes, skill descriptions, and gear affixes.",
            "请先在游戏中选择英雄。推荐会综合职业、属性、技能说明与装备词缀。",
            "請先在遊戲中選擇英雄。推薦會綜合職業、屬性、技能說明與裝備詞綴。"), hintStyle!);

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
        var nextTransform = GUI.Toggle(new Rect(rect.x + 340f, rect.y + 316f, 340f, 26f), Plugin.AutoTransformSkills.Value, UiText.L($" 부족한 추천 스킬 변환 (최대 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)}회)", $" Transform missing guide skills (max {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)})", $" 转换缺少的推荐技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）", $" 轉換缺少的推薦技能（最多 {Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50)} 次）"), toggleStyle!);
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
            "테마는 장비 전체 옵션·세트 효과와 기본 스킬의 60초 예상 성능에 적용됩니다.\n스킬 변환을 켜면 게임의 신전 비용을 사용해 추천 스킬을 맞춘 뒤 집중 배분합니다.",
            "Themes affect full-loadout effects and the base skill's estimated 60-second output.\nWhen enabled, transformation uses the shrine's normal cost to seek guide skills before focused allocation.",
            "主题会影响整套装备效果与基础技能的 60 秒预估输出。\n启用转换后，会按神殿正常费用寻找推荐技能，再集中分配点数。",
            "主題會影響整套裝備效果與基礎技能的 60 秒預估輸出。\n啟用轉換後，會按神殿正常費用尋找推薦技能，再集中分配點數。"), tooltipBodyStyle!);
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
        if (succeeded) Plugin.Logger.LogInfo(message); else Plugin.Logger.LogWarning(message);
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
        GUI.enabled = count > 0;
        GUI.backgroundColor = armed ? new Color(1f, 0.48f, 0.18f) : new Color(0.34f, 0.62f, 0.92f);
        var text = count <= 0
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
        if (!GameInventoryReader.TryOpenAll(kind, Plugin.BulkQualityMask.Value, Plugin.BulkQualityAtLeast.Value, Plugin.AutoStoreOpenedEquipment.Value, out var message))
        {
            status = message;
            Plugin.Logger.LogWarning($"Bulk open failed: {message}");
            return;
        }

        status = message;
        Plugin.Logger.LogInfo(message);
        currentPage = 0;
        nextRefreshAt = Time.unscaledTime + 0.25f;
        RefreshBulkCounts();
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

    private void RefreshItems()
    {
        try
        {
            if (languageMode == "auto") UpdateUiLanguage();
            var next = GameInventoryReader.ReadAll(includeWarehouse);
            var toolCounts = GameInventoryReader.GetBulkToolCounts(Plugin.BulkQualityMask.Value, Plugin.BulkQualityAtLeast.Value);
            allItems.Clear();
            allItems.AddRange(next);
            equipmentBoxCount = toolCounts.EquipmentBoxes;
            runeBoxCount = toolCounts.RuneBoxes;
            ApplyFilter();
            UpdateStatus();
        }
        catch (Exception error)
        {
            allItems.Clear();
            matches.Clear();
            equipmentBoxCount = 0;
            runeBoxCount = 0;
            status = UiText.L("데이터 대기 중", "Waiting for data", "等待数据", "等待資料");
            Plugin.Logger.LogDebug($"Inventory refresh deferred: {error.Message}");
        }
    }

    private void ApplyFilter()
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
        UpdateStatus();
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
            Plugin.Logger.LogDebug($"Preferred UI font unavailable: {error.Message}");
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
        foreach (Match match in Regex.Matches(value ?? string.Empty, "\\\"([^\\\"]+)\\\"|\\S+"))
        {
            var token = match.Value.Trim();
            var isExcluded = token.StartsWith("-", StringComparison.Ordinal) && token.Length > 1;
            if (isExcluded) token = token[1..];
            token = token.Trim('"');
            var alternatives = token.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize).Where(part => part.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (alternatives.Length == 0) continue;
            (isExcluded ? excluded : required).Add(alternatives);
        }
        return new SearchQuery(required, excluded);
    }

    public static List<string> HighlightTerms(string value)
    {
        var terms = new List<string>();
        foreach (Match match in Regex.Matches(value ?? string.Empty, "\\\"([^\\\"]+)\\\"|\\S+"))
        {
            var token = match.Value.Trim();
            if (token.StartsWith("-", StringComparison.Ordinal)) continue;
            token = token.Trim('"');
            foreach (var alternative in token.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (alternative.Length > 0 && !terms.Contains(alternative, StringComparer.CurrentCultureIgnoreCase)) terms.Add(alternative);
        }
        return terms;
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
    private static readonly int[] RankedQualities = { 3, 4, 5, 6, 8 };

    public static int BitFor(int quality) => quality switch
    {
        3 => 1 << 0,
        4 => 1 << 1,
        5 => 1 << 2,
        6 => 1 << 3,
        8 => 1 << 4,
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
internal sealed record InventoryEquipment(string Key, object ItemData, object SourceField);
internal sealed record AutoBuildSummary(string Hero, string Profile, string Status);
internal sealed record GearCandidate(ItemSearchRecord Record, string Key, int Part, int SetId, int DefinitionId, int WeaponType, double Score, double NumericScore, int DirectMatches, int ThemeMatches);
internal sealed record TeamCandidate(string Key, string Name, string Job, double Offense, double Defense, double Support, double Control, double Power, HashSet<string> Themes, string BuildHint);
internal sealed record TeamSuggestion(TeamCandidate A, TeamCandidate B, TeamCandidate C, double Score, string Reason);

internal static class GameInventoryReader
{
    private static readonly Regex RichText = new("<[^>]+>", RegexOptions.Compiled);
    private static Assembly? gameAssembly;
    private static bool languageTableLogged;
    private static readonly HashSet<string> loggedBulkToolQualities = new(StringComparer.Ordinal);

    private sealed record HeroEffectProfile(
        HeroFocus Focus,
        int JobId,
        HashSet<int> AllowedWeaponTypes,
        HashSet<int> BaseWeaponRequirement,
        List<HashSet<int>> SkillWeaponPreferences,
        int ActiveSkillMainType,
        HashSet<int> ActiveSkillTags,
        HashSet<int> SkillIds,
        HashSet<int> SkillInfoIds,
        HashSet<int> TalentIds,
        HashSet<int> MasteryIds,
        HashSet<int> AbilityIds,
        HashSet<int> RecommendedEquipmentIds,
        string[] SkillTerms);

    private sealed record EquipmentScore(double Total, int DirectMatches, int ThemeMatches);
    private sealed record EquipAttrMapping(int EquipType, int BattleAttrType);
    private sealed record SetEffectScoreRow(int Pieces, string Text, int AbilityId);
    private sealed record GearSlot(int Part, bool MainWeapon, int WeaponSlotIndex, string Label);
    private enum MoveReceiptKind { FieldMove, BagToVault, VaultToBag }
    private sealed record MoveReceipt(MoveReceiptKind Kind, object? FromField, object? ToField, object BeforeFromItem, object? BeforeToItem, object? TreasureData = null, object? GroupData = null);
    private sealed record LoadoutState(List<GearCandidate> Items, HashSet<string> UsedKeys, double HeuristicScore);
    private sealed record PreferredTalentPlan(
        object? Build,
        List<int> SkillTalentIds,
        List<int> MasteryTalentIds,
        HashSet<int> PreferredSkillIds,
        string BuildName);
    private sealed record SkillTransformResult(int Attempts, int Matched, int Target, int SpentBlood, string Note, bool CleanupSucceeded);
    private static List<EquipAttrMapping>? equipAttrMappings;
    private static Dictionary<int, List<SetEffectScoreRow>>? setEffectScoreRows;
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
            var build = GetPreferredBuild(hero, focus);
            var buildName = build is null ? UiText.L("직업 추천표 없음", "No job guide", "无职业指南", "無職業指南") : Clean(ReadString(build, "name") ?? EnglishName(build, string.Empty) ?? string.Empty);
            var heroLine = $"{name}  ·  {jobName}  ·  Lv.{level}  ·  Q{quality}";
            var profile = requestedTheme == "auto"
                ? UiText.L($"자동 분석: {focus.Localized}  ·  우선 빌드: {buildName}", $"Auto profile: {focus.English}  ·  Preferred guide: {buildName}", $"自动分析：{focus.Localized}  ·  优先流派：{buildName}", $"自動分析：{focus.Localized}  ·  優先流派：{buildName}")
                : UiText.L($"선택 테마: {focus.Localized}  ·  우선 빌드: {buildName}", $"Selected theme: {focus.English}  ·  Preferred guide: {buildName}", $"已选主题：{focus.Localized}  ·  优先流派：{buildName}", $"已選主題：{focus.Localized}  ·  優先流派：{buildName}");
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
            Plugin.Logger.LogDebug($"Team recommendation report deferred: {error.Message}");
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

    private static void LogTeamSet(string scope, List<TeamSuggestion> teams)
    {
        for (var index = 0; index < teams.Count; index++)
        {
            var team = teams[index];
            Plugin.Logger.LogInfo($"TEAM-{scope}|{index + 1}|{team.Score:0.0}|{team.A.Name} [{team.A.Job}] + {team.B.Name} [{team.B.Job}] + {team.C.Name} [{team.C.Job}]|{team.Reason}|{team.A.BuildHint} ; {team.B.BuildHint} ; {team.C.BuildHint}");
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
            var maxMythic = Math.Max(1, ToInt(InvokeStatic("HeroEquipData", "GetMaxMythEquipCount", hero)));
            var beam = new List<LoadoutState> { new(new List<GearCandidate>(), new HashSet<string>(StringComparer.Ordinal), 0d) };
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
                    .Concat(slotCandidates.Where(candidate => profile.RecommendedEquipmentIds.Contains(candidate.DefinitionId)))
                    .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                    .OrderByDescending(candidate => candidate.Score)
                    .Take(36).ToList();
                if (current is not null && options.All(candidate => candidate.Key != currentKey))
                {
                    var currentCandidate = candidates.FirstOrDefault(candidate => candidate.Key == currentKey);
                    if (currentCandidate is not null) options.Add(currentCandidate);
                }

                beam = beam.SelectMany(state => options
                        .Where(candidate => !state.UsedKeys.Contains(candidate.Key))
                        .Where(candidate => state.Items.Count(item => item.Record.Quality == 5) + (candidate.Record.Quality == 5 ? 1 : 0) <= maxMythic)
                        .Where(candidate => !HasLegendMythWeaponConflict(state.Items.Append(candidate)))
                        .Select(candidate => new LoadoutState(
                            state.Items.Append(candidate).ToList(),
                            new HashSet<string>(state.UsedKeys, StringComparer.Ordinal) { candidate.Key },
                            state.HeuristicScore + candidate.Score)))
                    .OrderByDescending(state => state.HeuristicScore + EstimatePartialSetSynergy(state.Items, profile))
                    .Take(360).ToList();
                if (beam.Count == 0) throw new InvalidOperationException($"No valid loadout remains for {slot.Label}.");
            }

            var currentItems = currentBySlot.Values.Where(item => item is not null).Cast<object>().ToList();
            var finalistStates = beam
                .OrderByDescending(state => state.HeuristicScore + EstimatePartialSetSynergy(state.Items, profile))
                .Take(96).ToList();
            var winner = finalistStates.Select(state => new { State = state, Score = ScoreCompleteLoadout(state.Items, hero, profile, currentItems) })
                .OrderByDescending(entry => entry.Score).First();

            Plugin.Logger.LogInfo($"AUTO-GEAR PLAN|focus={focus.English}|score={winner.Score:0.0}|" +
                                  string.Join(" ; ", slots.Select((slot, index) =>
                                  {
                                      var choice = winner.State.Items[index];
                                       return $"{slot.Label}={choice.Record.Name} Q{choice.Record.Quality} Lv{choice.Record.Level ?? 0} itemScore={choice.Score:0.0} numeric={choice.NumericScore:0.0} direct={choice.DirectMatches} theme={choice.ThemeMatches} set={choice.SetId}";
                                  })));

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
                Plugin.Logger.LogInfo("AUTO-GEAR BRIDGE|staged one non-target bag equipment item to storage");
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
                        Plugin.Logger.LogInfo($"AUTO-GEAR STAGE|slot={oppositeSlot.Label}|moved the opposite conflicting weapon to bag");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(stageFailure))
                            Plugin.Logger.LogWarning($"AUTO-GEAR STAGE FAILED|reason={stageFailure}");
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
                Plugin.Logger.LogWarning($"AUTO-GEAR MOVE FAILED|slot={entry.Key}|code={entry.Value.Code}|reason={entry.Value.Reason}");

            var finalMatches = 0;
            var changed = 0;
            var unchanged = 0;
            var directMatches = 0;
            var themeMatches = 0;
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                var candidate = winner.State.Items[index];
                var actual = GetEquippedItem(hero, slot.Part, slot.MainWeapon);
                if (!NativeEquals(actual, candidate.Record.ItemData)) continue;
                finalMatches++;
                if (NativeEquals(currentBySlot[slot], actual)) unchanged++;
                else
                {
                    changed++;
                    directMatches += candidate.DirectMatches;
                    themeMatches += candidate.ThemeMatches;
                }
            }

            var failed = slots.Count - finalMatches;
            if (failed > 0)
            {
                var rollbackFailures = RollbackMoveJournal(moveJournal);
                if (gearTalentData is not null)
                {
                    try { InvokeRequiredInstance(gearTalentData, "ReapplySkillVariantsFromEquippedItems"); }
                    catch (Exception refreshError) { Plugin.Logger.LogWarning($"AUTO-GEAR VARIANT REFRESH FAILED|{refreshError.GetBaseException().Message}"); }
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
            // transformations. Refresh them only after the full eight-slot
            // transaction has committed.
            var storageNormalizationFailures = NormalizeCommittedStorage(moveJournal, seasonData, targetItems);
            moveJournal.Clear();
            var variantRefreshFailed = false;
            try
            {
                InvokeRequiredInstance(gearTalentData ?? throw new InvalidOperationException("Talent data is unavailable."), "ReapplySkillVariantsFromEquippedItems");
                VerifyEquippedSkillVariants(hero, gearTalentData, "AUTO-GEAR");
            }
            catch (Exception refreshError)
            {
                variantRefreshFailed = true;
                Plugin.Logger.LogWarning($"AUTO-GEAR VARIANT REFRESH FAILED|{refreshError.GetBaseException().Message}");
            }
            var commitWarningKo = storageNormalizationFailures > 0 || variantRefreshFailed ? " · 보관/스킬 효과 경고는 로그 확인" : string.Empty;
            var commitWarningEn = storageNormalizationFailures > 0 || variantRefreshFailed ? " · check the log for storage/skill-effect warnings" : string.Empty;
            var commitWarningZhCn = storageNormalizationFailures > 0 || variantRefreshFailed ? " · 请查看日志中的存储/技能效果警告" : string.Empty;
            var commitWarningZhTw = storageNormalizationFailures > 0 || variantRefreshFailed ? " · 請查看日誌中的儲存/技能效果警告" : string.Empty;

            message = changed > 0
                ? UiText.L($"8부위 실제 장착 완료 · {focus.Localized} · 교체 {changed}개 · 스킬 직접 효과 {directMatches} · 테마 효과 {themeMatches} · 유지 {unchanged}개{commitWarningKo}", $"All 8 slots equipped · {focus.English} · changed {changed} · direct skill effects {directMatches} · theme effects {themeMatches} · kept {unchanged}{commitWarningEn}", $"8部位已实际装备 · {focus.Localized} · 更换 {changed} · 技能直接效果 {directMatches} · 主题效果 {themeMatches} · 保留 {unchanged}{commitWarningZhCn}", $"8部位已實際裝備 · {focus.Localized} · 更換 {changed} · 技能直接效果 {directMatches} · 主題效果 {themeMatches} · 保留 {unchanged}{commitWarningZhTw}")
                : UiText.L($"{focus.Localized} 테마에서 현재 8부위가 이미 최고 점수 조합입니다.", $"The current 8-slot loadout is already the highest-scoring {focus.English} combination.", $"当前 8 部位已是 {focus.Localized} 主题下评分最高的组合。", $"目前 8 部位已是 {focus.Localized} 主題下評分最高的組合。");
            return true;
        }
        catch (Exception error)
        {
            var hadMoves = moveJournal.Count > 0;
            var rollbackFailures = RollbackMoveJournal(moveJournal);
            if (gearTalentData is not null)
            {
                try { InvokeRequiredInstance(gearTalentData, "ReapplySkillVariantsFromEquippedItems"); }
                catch (Exception refreshError) { Plugin.Logger.LogWarning($"AUTO-GEAR VARIANT REFRESH FAILED|{refreshError.GetBaseException().Message}"); }
            }
            var rollbackNote = !hadMoves ? string.Empty : rollbackFailures == 0 ? " · previous loadout restored" : $" · rollback failed for {rollbackFailures} move(s)";
            message = UiText.L($"장비 자동 장착 실패: {error.GetBaseException().Message}{(!hadMoves ? string.Empty : rollbackFailures == 0 ? " · 이전 장비 복구 완료" : $" · 복구 실패 {rollbackFailures}건")}", $"Auto-equip failed: {error.GetBaseException().Message}{rollbackNote}", $"自动装备失败：{error.GetBaseException().Message}{(!hadMoves ? string.Empty : rollbackFailures == 0 ? " · 已恢复旧装备" : $" · 回滚失败 {rollbackFailures} 项")}", $"自動裝備失敗：{error.GetBaseException().Message}{(!hadMoves ? string.Empty : rollbackFailures == 0 ? " · 已復原舊裝備" : $" · 復原失敗 {rollbackFailures} 項")}");
            return false;
        }
    }

    public static bool TryOptimizeSelectedHeroSkills(out string message)
    {
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
            var preferred = GetPreferredTalentPlan(hero, focus);
            var gridById = BuildTalentGridById(talents);
            if (gridById.Count == 0)
            {
                message = UiText.L("배분 가능한 스킬·특성이 없습니다.", "No skills or talents can receive points.", "没有可分配点数的技能或天赋。", "沒有可分配點數的技能或天賦。");
                return false;
            }

            var resetPrice = Convert.ToInt32(InvokeRequiredInstance(talentData, "GetResetTalentPrice") ?? throw new InvalidOperationException("Talent reset price is unavailable."), CultureInfo.InvariantCulture);
            var bloodType = CreateEnum("EResType", 2) ?? throw new InvalidOperationException("Blood resource type is unavailable.");
            var bloodBefore = Convert.ToInt32(InvokeInstance(townData, "GetRes", bloodType) ?? 0, CultureInfo.InvariantCulture);
            // The native counter deliberately excludes the mandatory level-1
            // base-skill point. ResetTalentPoint keeps that point, so summing
            // TalentData levels makes a successful reset look like it failed.
            var spentBefore = GetResettableTalentPointCount(talentData, talents);
            if (spentBefore > 0 && bloodBefore < resetPrice)
            {
                message = UiText.L($"초기화 재화 부족 · 필요 피 {resetPrice:N0} / 보유 {bloodBefore:N0}", $"Not enough Blood to reset · need {resetPrice:N0} / have {bloodBefore:N0}", $"重置资源不足 · 需要鲜血 {resetPrice:N0} / 持有 {bloodBefore:N0}", $"重設資源不足 · 需要鮮血 {resetPrice:N0} / 持有 {bloodBefore:N0}");
                return false;
            }

            var transform = Plugin.AutoTransformSkills.Value && Plugin.AutoTransformMaxAttempts.Value > 0
                ? TransformMissingPreferredSkills(hero, talentData, townData, preferred, spentBefore > 0 ? resetPrice : 0, Math.Clamp(Plugin.AutoTransformMaxAttempts.Value, 0, 50))
                : new SkillTransformResult(0, 0, 0, 0, string.Empty, true);
            talents = ReadValues(Read(talentData, "talentDic"))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            if (Plugin.AutoTransformSkills.Value && transform.Target > 0 && transform.Matched < transform.Target)
            {
                Plugin.Logger.LogWarning($"AUTO-SKILLS TRANSFORM INCOMPLETE|attempts={transform.Attempts}|matched={transform.Matched}|target={transform.Target}|spentBlood={transform.SpentBlood}|reason={transform.Note}");
            }

            var resetResult = spentBefore > 0
                ? Convert.ToInt32(InvokeRequiredInstance(talentData, "ResetTalentPoint") ?? 1, CultureInfo.InvariantCulture)
                : 0;
            if (resetResult != 0)
            {
                message = UiText.L($"특성 초기화 실패 · 필요 피 {resetPrice:N0} / 보유 {bloodBefore:N0}", $"Talent reset failed · need {resetPrice:N0} Blood / have {bloodBefore:N0}", $"天赋重置失败 · 需要鲜血 {resetPrice:N0} / 持有 {bloodBefore:N0}", $"天賦重設失敗 · 需要鮮血 {resetPrice:N0} / 持有 {bloodBefore:N0}");
                return false;
            }
            var remainAfterReset = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
            talents = ReadValues(Read(talentData, "talentDic"))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            gridById = BuildTalentGridById(talents);
            var spentAfter = GetResettableTalentPointCount(talentData, talents);
            if (spentBefore > 0 && spentAfter != 0)
            {
                message = UiText.L("특성 초기화가 적용되지 않았습니다. 현재 게임 상태에서 초기화할 수 없습니다.", "Talent reset was not applied in the current game state.", "当前游戏状态下无法重置天赋。", "目前遊戲狀態下無法重設天賦。");
                return false;
            }

            var baseSkillChanged = ApplyPreferredBaseSkill(talentData, saveHero, preferred, out var baseSkillName);
            talents = ReadValues(Read(talentData, "talentDic"))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            gridById = BuildTalentGridById(talents);

            var beforeAllocation = remainAfterReset;
            var allocatedByPlan = 0;
            var failedNodes = new HashSet<int>();
            var activeSkillTalentIds = preferred.SkillTalentIds
                .Where(id => IsTransformableSkillDefinition(InvokeStatic("TableData", "getTTalentData", id)))
                .Where(gridById.ContainsKey)
                .Where(id => !ReadBool(InvokeInstance(gridById[id], "IsLock")))
                .ToList();
            var availablePreferredSkillIds = activeSkillTalentIds
                .Select(id => ReadNullableInt(Read(gridById[id], "tTalentData"), "skillId") ?? 0)
                .Where(id => id > 0).ToHashSet();
            var relevantMasteryTalentIds = preferred.MasteryTalentIds
                .Where(id => (ReadNullableInt(InvokeStatic("TableData", "getTTalentData", id), "type") ?? 0) == 2)
                .Where(gridById.ContainsKey)
                .Where(id => !ReadBool(InvokeInstance(gridById[id], "IsLock")))
                .Where(id => IsMasteryRelevantToSkills(gridById[id], availablePreferredSkillIds))
                .ToList();
            var effectivePreferred = preferred with
            {
                SkillTalentIds = preferred.SkillTalentIds.Where(id => gridById.ContainsKey(id)).ToList(),
                MasteryTalentIds = relevantMasteryTalentIds,
                PreferredSkillIds = availablePreferredSkillIds
            };

            // 1) Learn every recommended active skill once, so the loadout is
            // usable before points are concentrated into its main damage package.
            foreach (var talentId in activeSkillTalentIds)
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                if (!gridById.TryGetValue(talentId, out var talent)) continue;
                var level = GetTalentLevel(talent);
                if (level > 0) continue;
                if (!TrySpendTalentPoints(talentData, saveHero, talent, 1, out var spent)) failedNodes.Add(talentId);
                allocatedByPlan += spent;
            }

            // 2) Unlock relevant guide masteries once before concentrating points.
            // This produces a usable synergy package even when points are scarce.
            foreach (var talentId in relevantMasteryTalentIds
                         .OrderByDescending(id => ScoreTalent(gridById[id], focus, effectivePreferred)))
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                var talent = gridById[talentId];
                if (GetTalentLevel(talent) > 0) continue;
                if (!TrySpendTalentPoints(talentData, saveHero, talent, 1, out var spent)) failedNodes.Add(talentId);
                allocatedByPlan += spent;
            }

            // 3) Choose the primary skill by direct mastery synergy and its
            // effective build score, rather than assuming array order means DPS.
            var primaryTalentId = activeSkillTalentIds
                .OrderByDescending(id => CountMasteriesForSkill(relevantMasteryTalentIds, gridById, ReadNullableInt(Read(gridById[id], "tTalentData"), "skillId") ?? 0))
                .ThenByDescending(id => ScoreTalent(gridById[id], focus, effectivePreferred))
                .FirstOrDefault();
            if (primaryTalentId > 0)
                allocatedByPlan += SpendTalentToCap(talentData, saveHero, gridById[primaryTalentId], failedNodes);

            foreach (var talentId in relevantMasteryTalentIds
                         .OrderByDescending(id => ScoreTalent(gridById[id], focus, effectivePreferred)))
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                allocatedByPlan += SpendTalentToCap(talentData, saveHero, gridById[talentId], failedNodes);
            }

            foreach (var talentId in activeSkillTalentIds.Where(id => id != primaryTalentId))
            {
                if ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) <= 0) break;
                if (gridById.TryGetValue(talentId, out var talent))
                    allocatedByPlan += SpendTalentToCap(talentData, saveHero, talent, failedNodes);
            }

            // 4) Spend leftovers on the single best currently available node at
            // a time. Maxing a useful node before moving on avoids round-robin
            // equal distribution while native AddTalentPoint verifies each spend.
            var guard = 0;
            while ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) > 0 && guard++ < 512)
            {
                var best = gridById.Values
                    .Where(talent => !failedNodes.Contains(ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0))
                    .Where(CanAddTalentPoint)
                    .OrderByDescending(talent => ScoreTalent(talent, focus, effectivePreferred))
                    .ThenBy(talent => ReadNullableInt(Read(talent, "tTalentData"), "floor") ?? int.MaxValue)
                    .FirstOrDefault();
                if (best is null) break;
                var talentId = ReadNullableInt(Read(best, "tTalentData"), "id") ?? 0;
                var spent = SpendTalentToCap(talentData, saveHero, best, failedNodes);
                if (spent <= 0) failedNodes.Add(talentId);
                allocatedByPlan += spent;
            }

            var remaining = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
            var allocated = Math.Max(0, beforeAllocation - remaining);
            if (allocated <= 0 && beforeAllocation > 0)
            {
                message = UiText.L("초기화는 완료됐지만 현재 레벨에서 배분 가능한 특성이 없습니다.", "Reset completed, but no talent can receive points at the current level.", "重置已完成，但当前等级没有可分配的天赋。", "重設已完成，但目前等級沒有可分配的天賦。");
                return false;
            }

            InvokeRequiredInstance(talentData, "ReapplySkillVariantsFromEquippedItems");
            var variantSkillIds = VerifyEquippedSkillVariants(hero, talentData, "AUTO-SKILLS");
            var totalSpentBlood = Math.Max(0, bloodBefore - Convert.ToInt32(InvokeRequiredInstance(townData, "GetRes", bloodType) ?? bloodBefore, CultureInfo.InvariantCulture));
            var resetSpentBlood = Math.Max(0, totalSpentBlood - transform.SpentBlood);
            Plugin.Logger.LogInfo($"AUTO-SKILLS PLAN|focus={focus.English}|build={preferred.BuildName}|transform={transform.Attempts}:{transform.Matched}/{transform.Target}|transformNote={transform.Note}|baseSkillChanged={baseSkillChanged}:{baseSkillName}|variants={string.Join(',', variantSkillIds)}|allocated={allocated}|planned={allocatedByPlan}|remaining={remaining}|failedNodes={string.Join(',', failedNodes)}");
            var transformKo = transform.Target > 0 ? $" · 스킬 변환{(transform.Matched < transform.Target ? " 일부" : string.Empty)} {transform.Attempts}회({transform.Matched}/{transform.Target})" : string.Empty;
            var transformEn = transform.Target > 0 ? $" · {(transform.Matched < transform.Target ? "partially " : string.Empty)}transformed {transform.Attempts}× ({transform.Matched}/{transform.Target})" : string.Empty;
            var transformZh = transform.Target > 0 ? $" · 技能{(transform.Matched < transform.Target ? "部分" : string.Empty)}转换 {transform.Attempts} 次（{transform.Matched}/{transform.Target}）" : string.Empty;
            var variantKo = variantSkillIds.Count > 0 ? $" · 장비 변환형 {variantSkillIds.Count}개 적용" : string.Empty;
            var variantEn = variantSkillIds.Count > 0 ? $" · {variantSkillIds.Count} gear variants applied" : string.Empty;
            var variantZh = variantSkillIds.Count > 0 ? $" · 已应用 {variantSkillIds.Count} 个装备变体" : string.Empty;
            var transformNote = LocalizeTransformNote(transform.Note);
            var transformNoteSuffix = string.IsNullOrWhiteSpace(transformNote) ? string.Empty : $" · {transformNote}";
            var completionKo = transform.CleanupSucceeded ? "스킬 집중 분배 완료" : "스킬 배분 완료 · 임시 설정 복구 확인 필요";
            var completionEn = transform.CleanupSucceeded ? "Focused skill allocation complete" : "Skill allocation complete · temporary-setting recovery needs attention";
            var completionZhCn = transform.CleanupSucceeded ? "技能集中分配完成" : "技能分配完成 · 请检查临时设置恢复";
            var completionZhTw = transform.CleanupSucceeded ? "技能集中分配完成" : "技能分配完成 · 請檢查臨時設定復原";
            message = UiText.L(
                $"{completionKo} · {focus.Localized} · {preferred.BuildName}{transformKo}{variantKo} · {allocated:N0}포인트 · 변환 피 {transform.SpentBlood:N0} / 초기화 피 {resetSpentBlood:N0}{(remaining > 0 ? $" · 미사용 {remaining:N0}" : string.Empty)}{transformNoteSuffix}",
                $"{completionEn} · {focus.English} · {preferred.BuildName}{transformEn}{variantEn} · {allocated:N0} points · transform Blood {transform.SpentBlood:N0} / reset Blood {resetSpentBlood:N0}{(remaining > 0 ? $" · {remaining:N0} unspent" : string.Empty)}{transformNoteSuffix}",
                $"{completionZhCn} · {focus.Localized} · {preferred.BuildName}{transformZh}{variantZh} · {allocated:N0} 点 · 转换鲜血 {transform.SpentBlood:N0} / 重置鲜血 {resetSpentBlood:N0}{(remaining > 0 ? $" · 剩余 {remaining:N0}" : string.Empty)}{transformNoteSuffix}",
                $"{completionZhTw} · {focus.Localized} · {preferred.BuildName}{transformZh}{variantZh} · {allocated:N0} 點 · 轉換鮮血 {transform.SpentBlood:N0} / 重設鮮血 {resetSpentBlood:N0}{(remaining > 0 ? $" · 剩餘 {remaining:N0}" : string.Empty)}{transformNoteSuffix}");
            return transform.CleanupSucceeded;
        }
        catch (Exception error)
        {
            message = UiText.L($"스킬 자동 분배 실패: {error.GetBaseException().Message}", $"Skill allocation failed: {error.GetBaseException().Message}", $"技能自动分配失败：{error.GetBaseException().Message}", $"技能自動分配失敗：{error.GetBaseException().Message}");
            return false;
        }
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
            var activeTalents = ReadValues(Read(talentData, "talentDic")).Concat(ReadList(Read(talentData, "extraTalentList")))
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
                    activeParts.Add(GetAffixSearchText(affix));
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
        var phyAttack = ReadHeroAttr(hero, 1);
        var eleAttack = ReadHeroAttr(hero, 2);
        if (eleAttack > phyAttack * 1.08d)
            return new HeroFocus("elemental", UiText.L("원소·주문", "Elemental / Spell", "元素/法术", "元素/法術"), "Elemental / Spell", ElementalWords);
        if (phyAttack > eleAttack * 1.08d)
            return new HeroFocus("physical", UiText.L("물리·무예", "Physical / Martial", "物理/武技", "物理/武技"), "Physical / Martial", PhysicalWords);
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
        var unlocked = builds.Where(build => !ReadBool(Read(build, "isLock"))).ToList();
        if (unlocked.Count > 0) builds = unlocked;

        var saveHero = Read(hero, "saveHeroData");
        var baseTalentId = ReadNullableInt(saveHero, "baseSkillId") ?? 0;
        var invested = ReadValues(Read(Read(hero, "heroTalentData"), "talentDic"))
            .Where(talent => GetSpentTalentPoints(talent) > 0 || GetTalentLevel(talent) > 0)
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();

        return builds.Select(build =>
            {
                var skillIds = ReadSequence(Read(build, "skillArr")).Select(ToInt).Where(id => id > 0).ToList();
                var masteryIds = ReadSequence(Read(build, "masteryArr")).Select(ToInt).Where(id => id > 0).ToList();
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
                var score = KeywordScore(text, focus.Keywords) * (focus.IsManual ? 900d : 520d);
                score += guideIds.Count(invested.Contains) * 1150d;
                var buildBaseIds = skillIds.Where(id => IsBaseSkillDefinition(InvokeStatic("TableData", "getTTalentData", id))).ToHashSet();
                if (baseTalentId > 0 && buildBaseIds.Contains(baseTalentId)) score += 3600d;
                else if (baseTalentId > 0 && buildBaseIds.Count > 0) score -= focus.IsManual ? 450d : 2600d;
                score -= (ReadNullableInt(build, "index") ?? 0) * 0.01d;
                return (Build: build, Score: score);
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => ReadNullableInt(entry.Build, "index") ?? int.MaxValue)
            .Select(entry => entry.Build).FirstOrDefault();
    }

    private static PreferredTalentPlan GetPreferredTalentPlan(object hero, HeroFocus focus)
    {
        var build = GetPreferredBuild(hero, focus);
        var skillTalentIds = build is null
            ? new List<int>()
            : ReadSequence(Read(build, "skillArr")).Select(ToInt).Where(id => id > 0).Distinct().ToList();
        var masteryTalentIds = build is null
            ? new List<int>()
            : ReadSequence(Read(build, "masteryArr")).Select(ToInt).Where(id => id > 0).Distinct().ToList();
        var preferredSkillIds = skillTalentIds
            .Select(id => InvokeStatic("TableData", "getTTalentData", id))
            .Select(row => ReadNullableInt(row, "skillId") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var buildName = build is null
            ? UiText.L("사용자 빌드", "Custom build", "自定义流派", "自訂流派")
            : Clean(ReadString(build, "name") ?? EnglishName(build, UiText.L("추천 빌드", "Recommended build", "推荐流派", "推薦流派")) ?? UiText.L("추천 빌드", "Recommended build", "推荐流派", "推薦流派"));
        return new PreferredTalentPlan(build, skillTalentIds, masteryTalentIds, preferredSkillIds, buildName);
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
        var directMasteryMatches = 0;
        foreach (var affix in ReadList(Read(masteryData, "affixList")))
        {
            textParts.Add(GetAffixSearchText(affix));
            var affixDefinition = Read(affix, "tAffixData");
            if ((ReadNullableInt(affixDefinition, "effectType") ?? 0) == 4)
            {
                var skillIds = ReadSequence(Read(affixDefinition, "effectParam")).Select(ToInt).Where(id => id > 0).ToHashSet();
                if (skillIds.Overlaps(preferred.PreferredSkillIds)) directMasteryMatches++;
            }
        }
        var text = Clean(string.Join(" ", textParts)).ToLowerInvariant();
        var id = ReadNullableInt(definition, "id") ?? 0;
        var skillKey = ReadNullableInt(definition, "skillId") ?? 0;
        var skillGuideIndex = preferred.SkillTalentIds.IndexOf(id);
        var masteryGuideIndex = preferred.MasteryTalentIds.IndexOf(id);
        var guide = skillGuideIndex >= 0 ? 16000d - skillGuideIndex * 80d
            : masteryGuideIndex >= 0 ? 13500d - masteryGuideIndex * 60d
            : preferred.PreferredSkillIds.Contains(skillKey) ? 6200d : 0d;
        var focusScore = KeywordScore(text, focus.Keywords) * (focus.IsManual ? 260d : 150d);
        var directSkillScore = directMasteryMatches * 2600d;
        var utility = KeywordScore(text, SupportWords) * (focus.Key == "support" ? 180d : 24d)
                      + KeywordScore(text, TankWords) * (focus.Key == "defense" ? 160d : 16d);
        var floor = ReadNullableInt(definition, "floor") ?? 0;
        return guide + focusScore + directSkillScore + utility - floor * 0.1d;
    }

    private static HashSet<int> GetMasteryReferencedSkillIds(object masteryTalent)
    {
        var result = new HashSet<int>();
        foreach (var affix in ReadList(Read(Read(masteryTalent, "masteryData"), "affixList")))
        {
            var definition = Read(affix, "tAffixData");
            if ((ReadNullableInt(definition, "effectType") ?? 0) != 4) continue;
            foreach (var id in ReadSequence(Read(definition, "effectParam")).Select(ToInt).Where(id => id > 0))
                result.Add(id);
        }
        return result;
    }

    private static bool IsMasteryRelevantToSkills(object masteryTalent, HashSet<int> availableSkillIds)
    {
        var referenced = GetMasteryReferencedSkillIds(masteryTalent);
        return referenced.Count == 0 || referenced.Overlaps(availableSkillIds);
    }

    private static int CountMasteriesForSkill(IEnumerable<int> masteryTalentIds, IReadOnlyDictionary<int, object> gridById, int skillId)
        => skillId <= 0 ? 0 : masteryTalentIds.Count(id => gridById.TryGetValue(id, out var talent) && GetMasteryReferencedSkillIds(talent).Contains(skillId));

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
            Plugin.Logger.LogWarning($"AUTO-SKILLS DUPLICATE TALENTS|ids={string.Join(',', duplicates)}");
        return groups.ToDictionary(
            group => group.Key,
            group => group.OrderBy(talent => ReadBool(InvokeInstance(talent, "IsLock")) ? 1 : 0)
                .ThenByDescending(GetTalentLevel).First());
    }

    private static bool IsBaseSkillDefinition(object? definition)
        => (ReadNullableInt(definition, "type") ?? 0) == 1 && (ReadNullableInt(definition, "miniType") ?? 0) == 1;

    private static bool IsTransformableSkillDefinition(object? definition)
        => (ReadNullableInt(definition, "type") ?? 0) == 1 && (ReadNullableInt(definition, "miniType") ?? 0) != 1
           && (ReadNullableInt(definition, "skillId") ?? 0) > 0;

    private static int GetTalentLevel(object talent)
        => Convert.ToInt32(InvokeInstance(talent, "GetLevel") ?? ReadNullableInt(Read(talent, "saveTalentData"), "level") ?? 0, CultureInfo.InvariantCulture);

    private static int GetTalentLevelCap(object talent)
        => Convert.ToInt32(InvokeInstance(talent, "GetTalentLevelCap") ?? GetTalentLevel(talent), CultureInfo.InvariantCulture);

    private static int GetSpentTalentPoints(object talent)
    {
        var baseLevel = ReadNullableInt(talent, "baseLevel") ?? 0;
        return Math.Max(0, GetTalentLevel(talent) - baseLevel);
    }

    private static int GetResettableTalentPointCount(object talentData, IReadOnlyCollection<object> talents)
    {
        try
        {
            var native = InvokeRequiredInstance(talentData, "GetAddTalentPointExcludeStick");
            var count = Convert.ToInt32(native ?? throw new InvalidOperationException("Native talent-point count was null."), CultureInfo.InvariantCulture);
            if (count >= 0) return count;
            throw new InvalidOperationException($"Native talent-point count was negative ({count}).");
        }
        catch (Exception error)
        {
            // Match the native loop as a version-tolerant fallback: it sums the
            // saved levels, except that the job's mandatory base skill
            // (type=1, miniType=1) contributes level-1.
            Plugin.Logger.LogWarning($"Native resettable talent-point count unavailable; using save-data fallback: {error.GetBaseException().Message}");
            return talents.Sum(talent =>
            {
                var level = Math.Max(0, ReadNullableInt(Read(talent, "saveTalentData"), "level") ?? 0);
                var definition = Read(talent, "tTalentData");
                var mandatoryBaseSkill = (ReadNullableInt(definition, "type") ?? 0) == 1
                                         && (ReadNullableInt(definition, "miniType") ?? 0) == 1;
                return mandatoryBaseSkill ? Math.Max(0, level - 1) : level;
            });
        }
    }

    private static bool CanAddTalentPoint(object talent)
    {
        if (GetTalentLevel(talent) >= GetTalentLevelCap(talent)) return false;
        return !ReadBool(InvokeInstance(talent, "IsLock"));
    }

    private static bool TrySpendTalentPoints(object talentData, object saveHero, object talent, int requested, out int spent)
    {
        spent = 0;
        var beforeRemain = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
        var beforeLevel = GetTalentLevel(talent);
        var cap = GetTalentLevelCap(talent);
        var amount = Math.Min(Math.Max(0, requested), Math.Min(beforeRemain, Math.Max(0, cap - beforeLevel)));
        if (amount <= 0 || !CanAddTalentPoint(talent)) return false;
        var result = Convert.ToInt32(InvokeRequiredInstance(talentData, "AddTalentPoint", talent, amount) ?? 1, CultureInfo.InvariantCulture);
        var afterRemain = ReadNullableInt(saveHero, "talentRemainPoint") ?? beforeRemain;
        var afterLevel = GetTalentLevel(talent);
        var remainDelta = beforeRemain - afterRemain;
        var levelDelta = afterLevel - beforeLevel;
        if (result == 0 && remainDelta == amount && levelDelta == amount)
        {
            spent = amount;
            return true;
        }
        Plugin.Logger.LogWarning($"AUTO-SKILLS ADD FAILED|talent={ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0}|code={result}|requested={amount}|remainDelta={remainDelta}|levelDelta={levelDelta}");
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
            Plugin.Logger.LogWarning($"AUTO-SKILLS BASE SKIPPED|talent={desiredTalentId}|reason=base skill is not present in the hero talent grid");
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
        var unlockedSlots = current.Count(talent => !ReadBool(InvokeInstance(talent, "IsLock")));
        var originalFixedTalentIds = current
            .Where(talent => ReadBool(Read(Read(talent, "saveTalentData"), "isFixed")))
            .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
            .Where(id => id > 0).ToHashSet();
        var fixedUnwanted = current.Count(talent =>
        {
            var definition = Read(talent, "tTalentData");
            var talentId = ReadNullableInt(definition, "id") ?? 0;
            var skillId = ReadNullableInt(definition, "skillId") ?? 0;
            return !ReadBool(InvokeInstance(talent, "IsLock"))
                   && originalFixedTalentIds.Contains(talentId) && !desiredSkillIds.Contains(skillId);
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
        var likeSnapshot = ReadSequence(Read(Read(townData, "saveTownData"), "likeTalentList"))
            .Select(ToInt).Where(id => id > 0).Distinct().ToList();
        var attempts = 0;
        var note = string.Empty;
        var cleanupSucceeded = true;
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
                    var shouldFix = originalFixedTalentIds.Contains(talentId) || desiredSkillIds.Contains(skillId);
                    var isFixed = ReadBool(Read(Read(talent, "saveTalentData"), "isFixed"));
                    if (talentId > 0 && shouldFix != isFixed)
                        InvokeRequiredInstance(talentData, "SetTalentWashFixed", talentId, shouldFix);
                }

                matched = CountPreferredTransformedSkills(current, desiredSkillIds);
                if (matched >= target) break;
                var currentSkillIds = current
                    .Where(talent => !ReadBool(InvokeInstance(talent, "IsLock")))
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
                if (price <= 0 || blood < price + reservedBlood)
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
                    var isFixed = ReadBool(Read(Read(talent, "saveTalentData"), "isFixed"));
                    if (talentId > 0 && shouldFix != isFixed)
                        InvokeRequiredInstance(talentData, "SetTalentWashFixed", talentId, shouldFix);
                }
                catch (Exception error)
                {
                    cleanupSucceeded = false;
                    note = AppendTransformNote(note, "temporary fixed-skill state could not be fully restored");
                    Plugin.Logger.LogWarning($"AUTO-SKILLS FIXED RESTORE FAILED|{error.GetBaseException().Message}");
                }
            }
            var restoredFixedTalentIds = GetTransformableTalents(talentData)
                .Where(talent => ReadBool(Read(Read(talent, "saveTalentData"), "isFixed")))
                .Select(talent => ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0)
                .Where(id => id > 0).ToHashSet();
            if (!restoredFixedTalentIds.SetEquals(originalFixedTalentIds))
            {
                cleanupSucceeded = false;
                note = AppendTransformNote(note, "temporary fixed-skill state could not be fully restored");
                Plugin.Logger.LogWarning($"AUTO-SKILLS FIXED RESTORE MISMATCH|expected={string.Join(',', originalFixedTalentIds)}|actual={string.Join(',', restoredFixedTalentIds)}");
            }
            try { RestoreTalentLikes(townData, likeSnapshot); }
            catch (Exception error)
            {
                cleanupSucceeded = false;
                note = AppendTransformNote(note, "skill preferences could not be fully restored");
                Plugin.Logger.LogWarning($"AUTO-SKILLS LIKES RESTORE FAILED|{error.GetBaseException().Message}");
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
                Plugin.Logger.LogWarning($"AUTO-SKILLS SHRINE HERO RESTORE FAILED|{error.GetBaseException().Message}");
            }
        }

        var bloodAfter = Convert.ToInt32(InvokeRequiredInstance(townData, "GetRes", bloodType) ?? bloodBefore, CultureInfo.InvariantCulture);
        return new SkillTransformResult(attempts, matched, target, Math.Max(0, bloodBefore - bloodAfter), note, cleanupSucceeded);
    }

    private static IEnumerable<object> GetTransformableTalents(object talentData)
        => ReadValues(Read(talentData, "talentDic")).Where(talent => IsTransformableSkillDefinition(Read(talent, "tTalentData")));

    private static int CountPreferredTransformedSkills(IEnumerable<object> talents, HashSet<int> desiredSkillIds)
        => talents.Where(talent => !ReadBool(InvokeInstance(talent, "IsLock")))
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
        var current = ReadSequence(Read(Read(townData, "saveTownData"), "likeTalentList"))
            .Select(ToInt).Where(id => id > 0).Distinct().ToList();
        foreach (var talentId in current.Where(id => !desired.Contains(id)))
            SetTalentLikeState(codex, talentId, false);
        foreach (var talentId in desiredOrdered)
            SetTalentLikeState(codex, talentId, true);
        return ReadSequence(Read(Read(townData, "saveTownData"), "likeTalentList"))
            .Select(ToInt).Count(desired.Contains);
    }

    private static void RestoreTalentLikes(object townData, IReadOnlyCollection<int> snapshot)
    {
        var codex = Read(townData, "townCodexData");
        if (codex is null)
        {
            if (snapshot.Count == 0) return;
            throw new InvalidOperationException("Town Codex data is unavailable.");
        }
        var current = ReadSequence(Read(Read(townData, "saveTownData"), "likeTalentList"))
            .Select(ToInt).Where(id => id > 0).Distinct().ToList();
        foreach (var talentId in current)
            if (!SetTalentLikeState(codex, talentId, false))
                throw new InvalidOperationException($"Could not remove temporary talent preference {talentId}.");
        foreach (var talentId in snapshot)
            if (!SetTalentLikeState(codex, talentId, true))
                throw new InvalidOperationException($"Could not restore talent preference {talentId}.");
        var restored = ReadSequence(Read(Read(townData, "saveTownData"), "likeTalentList"))
            .Select(ToInt).Where(id => id > 0).Distinct().ToList();
        if (!restored.SequenceEqual(snapshot))
            throw new InvalidOperationException($"Talent preference order mismatch (expected {string.Join(',', snapshot)}, got {string.Join(',', restored)}).");
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
        var showTalent = InvokeStaticMany("ShowTalentData", "Create", talentId, true);
        if (showTalent is null) return false;
        var current = ReadBool(InvokeInstance(codex, "IsLikeTalent", showTalent));
        if (current == liked) return true;
        InvokeRequiredInstance(codex, "SetLikeTalent", showTalent);
        return ReadBool(InvokeInstance(codex, "IsLikeTalent", showTalent)) == liked;
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
        var skillIds = new HashSet<int>();
        var skillInfoIds = new HashSet<int>();
        var talentIds = new HashSet<int>();
        var masteryIds = new HashSet<int>();
        var abilityIds = new HashSet<int>();
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        AddSkill(InvokeInstance(hero, "GetNowBaseSkillData"), true);
        var heroTalent = Read(hero, "heroTalentData");
        foreach (var talent in ReadValues(Read(heroTalent, "talentDic")).Concat(ReadList(Read(heroTalent, "extraTalentList")))
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
            if (id > 0) abilityIds.Add(id);
            AddTerm(ReadString(row, "name"));
            AddTerm(EnglishName(row, string.Empty));
        }

        var recommendedEquipment = new HashSet<int>();
        var preferredBuild = GetPreferredBuild(hero, focus);
        foreach (var talentId in ReadSequence(Read(preferredBuild, "skillArr")).Select(ToInt).Where(id => id > 0))
        {
            var talentRow = InvokeStatic("TableData", "getTTalentData", talentId);
            if (talentRow is null) continue;
            talentIds.Add(talentId);
            var skillId = ReadNullableInt(talentRow, "skillId") ?? 0;
            if (skillId > 0) AddSkill(InvokeStatic("TableData", "getTSkillData", skillId), IsBaseSkillDefinition(talentRow));
        }
        foreach (var talentId in ReadSequence(Read(preferredBuild, "masteryArr")).Select(ToInt).Where(id => id > 0))
        {
            var talentRow = InvokeStatic("TableData", "getTTalentData", talentId);
            if (talentRow is null) continue;
            talentIds.Add(talentId);
            var masteryId = ReadNullableInt(talentRow, "masteryId") ?? 0;
            if (masteryId <= 0) continue;
            masteryIds.Add(masteryId);
            var mastery = InvokeStatic("TableData", "getTMasteryData", masteryId);
            AddTerm(ReadString(mastery, "name"));
            AddTerm(EnglishName(mastery, string.Empty));
        }
        foreach (var value in ReadSequence(Read(preferredBuild, "equipArr")))
        {
            var id = ToInt(value);
            if (id > 0) recommendedEquipment.Add(id);
        }

        return new HeroEffectProfile(focus, jobId, allowedWeapons, baseWeaponRequirement, skillWeaponPreferences, activeSkillMainType, activeSkillTags, skillIds, skillInfoIds, talentIds, masteryIds, abilityIds, recommendedEquipment, terms.ToArray());
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
        return new GearCandidate(record, NativeObjectKey(record.ItemData, record.SourceField ?? record.ItemData), part, setId, definitionId, minType, score.Total + numericScore * 0.08d, numericScore, score.DirectMatches, score.ThemeMatches);
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
        foreach (var affix in CollectEquipmentAffixes(item))
        {
            var runtimeAffix = ResolveRuntimeAffix(affix);
            descriptions.Add(GetAffixSearchText(runtimeAffix));
            directMatches += CountDirectAffixMatches(runtimeAffix, profile);
        }
        descriptions.Add(Clean(string.Join(" ", ReadString(definition, "des") ?? string.Empty, EnglishText(definition, "_des", string.Empty) ?? string.Empty)));
        var text = string.Join(" ", descriptions).ToLowerInvariant();
        var textHintMatches = Math.Min(3, profile.SkillTerms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)));
        var themeMatches = KeywordScore(text, profile.Focus.Keywords);
        var focusBonus = themeMatches * (profile.Focus.IsManual ? 70d : 38d) + textHintMatches * 28d;
        var generalBonus = KeywordScore(text, new[] { "all attack", "all defense", "primary attribute", "crit", "speed", "cost", "resist", "health" }) * 12d;
        var definitionId = ReadNullableInt(definition, "id") ?? 0;
        var guideBonus = profile.RecommendedEquipmentIds.Contains(definitionId) ? 420d : 0d;
        var total = qualityWeight * 0.2d + level * 0.2d + forge * 0.5d + main * 0.002d + focusBonus + generalBonus + directMatches * 350d + guideBonus;
        return new EquipmentScore(total, directMatches, themeMatches);
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
                var weight = mapping.BattleAttrType switch
                {
                    1 => physical ? 1.4d : elemental ? 0.25d : 1d,
                    2 => elemental ? 1.4d : physical ? 0.25d : 1d,
                    3 or 4 => profile.Focus.Key == "defense" ? 0.9d : 0.32d,
                    5 => profile.Focus.Key == "defense" ? 0.35d : 0.10d,
                    11 or 12 or 13 => 0.55d,
                    31 or 37 or 41 or 42 or 51 or 52 or 53 or 54 or 55 or 56
                        or 71 or 72 or 75 or 76 or 99 or 100 or 101 or 102 or 106 or 107 or 108
                        or 110 or 111 or 112 or 113 or 114 or 115 or 171 or 172 or 218 => 16d,
                    _ => 0.08d
                };
                score += value * weight;
            }
            return score;
        }
        catch (Exception error)
        {
            if (!numericPreScoreFailureLogged)
            {
                numericPreScoreFailureLogged = true;
                Plugin.Logger.LogWarning($"AUTO-GEAR NUMERIC PRE-SCORE FAILED|raw fallback is active|{error.GetBaseException().Message}");
            }
            return 0d;
        }
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
            foreach (var effect in effects.Where(effect => effect.Pieces <= count))
            {
                score += 450d;
                score += KeywordScore(effect.Text, profile.Focus.Keywords) * 180d;
                score += Math.Min(3, profile.SkillTerms.Count(term => effect.Text.Contains(term, StringComparison.OrdinalIgnoreCase))) * 600d;
                if (effect.AbilityId > 0 && profile.AbilityIds.Contains(effect.AbilityId)) score += 800d;
            }
        }
        return score;
    }

    private static Dictionary<int, List<SetEffectScoreRow>> GetSetEffectScoreRows()
    {
        if (setEffectScoreRows is not null) return setEffectScoreRows;
        var rows = ReadValues(ReadStatic("TableData", "TEquipSetsEffectDict"))
            .Select(effect => new
            {
                SetId = ReadNullableInt(effect, "sesId") ?? 0,
                Row = new SetEffectScoreRow(
                    ReadNullableInt(effect, "index") ?? int.MaxValue,
                    Clean(string.Join(" ", ReadString(effect, "des") ?? string.Empty, EnglishText(effect, "_des", string.Empty) ?? string.Empty)).ToLowerInvariant(),
                    ReadNullableInt(effect, "abilityId") ?? 0)
            })
            .Where(entry => entry.SetId > 0)
            .GroupBy(entry => entry.SetId)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Row).ToList());
        if (rows.Count > 0) setEffectScoreRows = rows;
        return rows;
    }

    private static double ScoreCompleteLoadout(List<GearCandidate> items, object hero, HeroEffectProfile profile, List<object> currentItems)
    {
        var score = items.Sum(item => item.Score) + EstimatePartialSetSynergy(items, profile);
        var weaponTypes = items.Where(item => item.Part == 1).Select(item => item.WeaponType).Where(value => value > 0).ToHashSet();
        if (profile.BaseWeaponRequirement.Count > 0)
            score += profile.BaseWeaponRequirement.Overlaps(weaponTypes) ? 3200d : -30000d;
        foreach (var preference in profile.SkillWeaponPreferences)
            if (preference.Overlaps(weaponTypes)) score += 420d;

        // Use the game's own AttrData calculations on a temporary copy. This keeps
        // the user's hero and save untouched while accounting for core stats,
        // ordinary affixes, percentage conversions, crit and skill-speed buckets.
        if (TryEvaluateFinalPerformance(items, hero, profile, currentItems, out var performance))
            score += performance * 2.4d;
        return score;
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
            var hasNativeSkillDamage = TryEvaluateNativeBaseSkillSustainedDamage(simulated, hero, items, out var sustainedDamage60s);
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
                Plugin.Logger.LogWarning($"AUTO-GEAR ATTR SIMULATION FAILED|heuristic fallback is active|{error.GetBaseException().Message}");
            }
            return false;
        }
    }

    private static bool TryEvaluateNativeBaseSkillSustainedDamage(object attrData, object hero, IEnumerable<GearCandidate> items, out double sustainedDamage60s)
    {
        const double windowSeconds = 60d;
        sustainedDamage60s = 0d;
        try
        {
            var actualSkill = InvokeRequiredInstance(hero, "GetNowBaseSkillData")
                              ?? throw new InvalidOperationException("The selected hero has no active base skill.");
            var skillId = ReadNullableInt(Read(actualSkill, "tSkillData"), "id") ?? 0;
            if (skillId <= 0) throw new InvalidOperationException("The active base skill ID is unavailable.");
            var level = Math.Max(1, ReadNullableInt(actualSkill, "level") ?? 1);
            var preview = InvokeRequiredStaticMany("SkillData", "CreatePreview", skillId, level, attrData)
                          ?? throw new InvalidOperationException("SkillData.CreatePreview returned no skill.");

            // The live skill may be variant-enabled by the currently equipped
            // loadout. Do not copy that state into every candidate. Apply only a
            // variant supplied by the candidate loadout being evaluated.
            if (CandidateEnablesSkillVariant(items, skillId))
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
                Plugin.Logger.LogInfo($"AUTO-GEAR 60S ESTIMATE|skill={skillId}|cooldown={cooldown:0.###}|speed={nativeSpeed:0.###}|cast-opportunities={castOpportunities:0.##}|single-target uptime proxy, not exact battle AI");
            }
            return double.IsFinite(sustainedDamage60s) && sustainedDamage60s > 0d;
        }
        catch (Exception error)
        {
            if (!nativeSkillPreviewFailureLogged)
            {
                nativeSkillPreviewFailureLogged = true;
                Plugin.Logger.LogWarning($"AUTO-GEAR 60S SKILL PREVIEW FAILED|attribute fallback is active|{error.GetBaseException().Message}");
            }
            return false;
        }
    }

    private static bool CandidateEnablesSkillVariant(IEnumerable<GearCandidate> items, int skillId)
        => skillId > 0 && items.SelectMany(candidate => CollectEquipmentAffixes(candidate.Record.ItemData))
            .Select(ResolveRuntimeAffix)
            .Select(affix => Read(affix, "tAffixData"))
            .Where(definition => (ReadNullableInt(definition, "effectType") ?? 0) == 4)
            .SelectMany(definition => ReadSequence(Read(definition, "effectParam")).Select(ToInt))
            .Any(id => id == skillId);

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
        foreach (var affix in CollectEquipmentAffixes(item))
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
        var abilityId = ReadNullableInt(Read(affix, "tAbilityData"), "id") ?? 0;
        if (abilityId > 0 && profile.AbilityIds.Contains(abilityId)) matched = true;
        var talent = Read(affix, "tTalentData");
        if ((ReadNullableInt(talent, "id") is > 0 and var talentId && profile.TalentIds.Contains(talentId))
            || (ReadNullableInt(talent, "skillId") is > 0 and var skillId && profile.SkillIds.Contains(skillId))
            || (ReadNullableInt(talent, "masteryId") is > 0 and var masteryId && profile.MasteryIds.Contains(masteryId))) matched = true;
        var definition = Read(affix, "tAffixData");
        if ((ReadNullableInt(definition, "effectType") ?? 0) == 4)
        {
            var parameters = ReadSequence(Read(definition, "effectParam")).Select(ToInt).ToHashSet();
            if (parameters.Overlaps(profile.SkillIds)) matched = true;
        }
        return matched ? 1 : 0;
    }

    private static List<int> VerifyEquippedSkillVariants(object hero, object talentData, string scope)
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
            .SelectMany(definition => ReadSequence(Read(definition, "effectParam")).Select(ToInt))
            .Where(id => id > 0 && available.Contains(id)).Distinct().ToHashSet();
        var missing = expected.Where(id => !actual.Contains(id)).ToList();
        var unexpected = actual.Where(id => !expected.Contains(id)).ToList();
        Plugin.Logger.LogInfo($"{scope} VARIANTS|expected={string.Join(',', expected)}|actual={string.Join(',', actual)}|missing={string.Join(',', missing)}|unexpected={string.Join(',', unexpected)}");
        if (missing.Count > 0 || unexpected.Count > 0)
            Plugin.Logger.LogWarning($"{scope} VARIANT MISMATCH|missing={string.Join(',', missing)}|unexpected={string.Join(',', unexpected)}");
        return actual;
    }

    private static object? GetEquippedItem(object hero, int part, bool main)
    {
        var partType = CreateEnum("EEquipPart", part);
        return partType is null ? null : InvokeInstance(hero, "GetEquipByPart", partType, main);
    }

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
            return true;
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
            return NativeEquals(Read(destination, "itemData"), item);
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
        var destinationHeroMatches = NativeEquals(InvokeStatic("HeroEquipData", "GetHeroDataByField", destination), hero);
        var ownerMatches = NativeEquals(Read(item, "ownerHeroData"), hero);
        var resolvedFieldMatches = NativeEquals(InvokeStatic("ItemSys", "FindHeroEquipFieldByItem", item), destination);
        if (destinationMatches && sourceMatches && destinationHeroMatches && ownerMatches && resolvedFieldMatches)
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
                    Plugin.Logger.LogInfo($"AUTO-GEAR STORAGE NORMALIZED|item={key}|destination={destination.StorageSource}");
                }
                catch (Exception error)
                {
                    failures++;
                    Plugin.Logger.LogWarning($"AUTO-GEAR STORAGE NORMALIZE FAILED|item={key}|{error.GetBaseException().Message}");
                }
                return;
            }

            var storageRecord = ReadAll(true).FirstOrDefault(record => record.StorageSource is StorageSource.Warehouse or StorageSource.Treasure
                && NativeEquals(record.ItemData, item));
            if (storageRecord is null)
            {
                handled.Add(key);
                failures++;
                Plugin.Logger.LogWarning($"AUTO-GEAR STORAGE NORMALIZE FAILED|item={key}|staged item was not found in bag, Warehouse, or Vault");
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
                Plugin.Logger.LogInfo($"AUTO-GEAR STORAGE NORMALIZED|item={key}|destination=Vault");
            }
            catch (Exception error)
            {
                failures++;
                Plugin.Logger.LogWarning($"AUTO-GEAR STORAGE NORMALIZE FAILED|item={key}|{error.GetBaseException().Message}");
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
                    if (!ReadBool(InvokeRequiredInstance(receipt.TreasureData, "TryTakeEquip", receipt.GroupData, receipt.BeforeFromItem)))
                        throw new InvalidOperationException("Vault item could not be returned to the bag");
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
                Plugin.Logger.LogWarning($"AUTO-GEAR ROLLBACK FAILED|index={index}|reason={error.GetBaseException().Message}");
            }
        }
        if (receiptCount > 0)
            Plugin.Logger.LogInfo($"AUTO-GEAR ROLLBACK|moves={receiptCount}|failures={failures}");
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
        if (!ReadBool(InvokeRequiredInstance(treasure, "TryTakeEquip", record.GroupData, record.ItemData))) return false;
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
        => words.Count(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] PhysicalWords = { "physical", "martial", "strike", "blunt", "slash", "pierce", "strength", "dexterity", "weapon", "crit" };
    private static readonly string[] ElementalWords = { "elemental", "spell", "fire", "frost", "ice", "lightning", "intelligence", "mana", "magic" };
    private static readonly string[] FireWords = { "fire", "burn", "burning", "ignite", "flame" };
    private static readonly string[] IceWords = { "ice", "frost", "cold", "freeze", "frozen", "chill" };
    private static readonly string[] LightningWords = { "lightning", "shock", "shocked", "thunder", "electric" };
    private static readonly string[] MinionWords = { "minion", "summon", "summoned", "pet", "companion" };
    private static readonly string[] BleedWords = { "bleed", "bleeding", "wound" };
    private static readonly string[] CorrosionWords = { "corrosion", "corrode", "poison", "toxic", "acid" };
    private static readonly string[] CriticalWords = { "crit", "critical", "critical strike" };
    private static readonly string[] SupportWords = { "support", "heal", "restore", "aura", "buff", "ally", "shield", "warcry", "recovery" };
    private static readonly string[] TankWords = { "defense", "defence", "health", "resist", "block", "survival", "damage taken", "toughness", "immunity" };

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
            Plugin.Logger.LogInfo($"Game language table: {string.Join(", ", rows)}");
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
        var inventoryFields = InvokeInstance(bagData, "GetFieldList", equipmentType);
        foreach (var (field, index) in ReadList(inventoryFields).Select((field, index) => (field, index)))
        {
            var item = Read(field, "itemData");
            if (item is null) continue;
            result.Add(DescribeItem(item, UiText.L($"인벤토리 #{index + 1}", $"Inventory #{index + 1}", $"背包 #{index + 1}", $"背包 #{index + 1}"), StorageKind.Inventory, StorageSource.Inventory, field));
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

    public static (int EquipmentBoxes, int RuneBoxes) GetBulkToolCounts(int qualityMask, bool atLeast)
    {
        try
        {
            var stacks = ReadBulkToolStacks().Where(stack => QualityFilterLogic.Matches(stack.Quality, qualityMask, atLeast)).ToList();
            return (
                stacks.Where(stack => stack.Kind == BulkToolKind.EquipmentBox).Sum(stack => stack.Count),
                stacks.Where(stack => stack.Kind == BulkToolKind.RuneBox).Sum(stack => stack.Count));
        }
        catch
        {
            return (0, 0);
        }
    }

    public static bool TryOpenAll(BulkToolKind kind, int qualityMask, bool atLeast, bool autoStoreEquipment, out string message)
    {
        var label = kind == BulkToolKind.RuneBox
            ? UiText.L("룬 상자", "rune boxes", "符文箱", "符文箱")
            : UiText.L("장비 상자", "gear boxes", "装备箱", "裝備箱");
        try
        {
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData");
            var lordData = Read(seasonData, "lordData");
            var bagData = Read(lordData, "lordBagData");
            if (bagData is null)
            {
                message = UiText.L("인벤토리 데이터를 찾지 못했습니다.", "Inventory data was not found.", "未找到背包数据。", "找不到背包資料。");
                return false;
            }

            var initial = ReadBulkToolStacks().Where(stack => stack.Kind == kind && QualityFilterLogic.Matches(stack.Quality, qualityMask, atLeast)).Sum(stack => stack.Count);
            if (initial <= 0)
            {
                message = UiText.L($"열 수 있는 {label}가 없습니다.", $"There are no {label} to open.", $"没有可开启的{label}。", $"沒有可開啟的{label}。");
                return false;
            }

            var opened = 0;
            var autoStored = 0;
            var knownEquipment = new HashSet<string>(ReadInventoryEquipment().Select(entry => entry.Key), StringComparer.Ordinal);
            var blockedStacks = new HashSet<string>(StringComparer.Ordinal);
            for (var guard = 0; guard < 512; guard++)
            {
                var available = ReadBulkToolStacks()
                    .Where(stack => stack.Kind == kind && stack.Count > 0 && QualityFilterLogic.Matches(stack.Quality, qualityMask, atLeast) && !blockedStacks.Contains(stack.Key))
                    .OrderByDescending(stack => stack.Count)
                    .ToList();
                if (available.Count == 0) break;

                var stack = available[0];
                var request = stack.Count;
                var progressed = false;
                while (request >= 1)
                {
                    var before = ReadBulkToolStacks().Where(entry => entry.Kind == kind && QualityFilterLogic.Matches(entry.Quality, qualityMask, atLeast)).Sum(entry => entry.Count);
                    InvokeInstance(bagData, "UseToolCount", stack.ItemData, request, true);
                    var after = ReadBulkToolStacks().Where(entry => entry.Kind == kind && QualityFilterLogic.Matches(entry.Quality, qualityMask, atLeast)).Sum(entry => entry.Count);
                    if (after < before)
                    {
                        opened += before - after;
                        if (autoStoreEquipment && kind == BulkToolKind.EquipmentBox)
                            autoStored += AutoStoreNewInventoryEquipment(knownEquipment);
                        progressed = true;
                        break;
                    }

                    if (request == 1) break;
                    request = Math.Max(1, request / 2);
                }

                if (!progressed) blockedStacks.Add(stack.Key);
            }

            var remaining = ReadBulkToolStacks().Where(stack => stack.Kind == kind && QualityFilterLogic.Matches(stack.Quality, qualityMask, atLeast)).Sum(stack => stack.Count);
            if (opened <= 0)
            {
                message = UiText.L($"{label}를 열 수 없습니다. 인벤토리 공간을 확인하세요.", $"Could not open {label}. Check inventory space.", $"无法开启{label}。请检查背包空间。", $"無法開啟{label}。請檢查背包空間。");
                return false;
            }

            message = remaining > 0
                ? UiText.L(
                    $"{label} {opened:N0}개 개봉 완료 · 공간 부족으로 {remaining:N0}개 남음",
                    $"Opened {opened:N0} {label} · {remaining:N0} left due to limited space",
                    $"已开启 {opened:N0} 个{label} · 空间不足，剩余 {remaining:N0} 个",
                    $"已開啟 {opened:N0} 個{label} · 空間不足，剩餘 {remaining:N0} 個")
                : UiText.L(
                    $"{label} {opened:N0}개 모두 개봉 완료",
                    $"Opened all {opened:N0} {label}",
                    $"已开启全部 {opened:N0} 个{label}",
                    $"已開啟全部 {opened:N0} 個{label}");
            if (autoStored > 0) message += UiText.L($" · 새 장비 {autoStored:N0}개 자동 창고", $" · auto-stored {autoStored:N0} new gear", $" · 自动入库 {autoStored:N0} 件新装备", $" · 自動入庫 {autoStored:N0} 件新裝備");
            return true;
        }
        catch (Exception error)
        {
            message = UiText.L($"{label} 일괄 개봉 실패: {error.GetBaseException().Message}", $"Bulk opening {label} failed: {error.GetBaseException().Message}", $"批量开启{label}失败：{error.GetBaseException().Message}", $"批次開啟{label}失敗：{error.GetBaseException().Message}");
            return false;
        }
    }

    private static int AutoStoreNewInventoryEquipment(HashSet<string> knownEquipment)
    {
        var moved = 0;
        foreach (var entry in ReadInventoryEquipment())
        {
            if (!knownEquipment.Add(entry.Key)) continue;
            InvokeStaticMany("ItemSys", "QuickMoveItemFromBagToStore", entry.ItemData);
            if (Read(entry.SourceField, "itemData") is null) moved++;
        }
        return moved;
    }

    private static List<InventoryEquipment> ReadInventoryEquipment()
    {
        var result = new List<InventoryEquipment>();
        var dataManager = ReadStatic("Game", "dataMgr");
        var seasonData = Read(dataManager, "nowSeasonData");
        var lordData = Read(seasonData, "lordData");
        var bagData = Read(lordData, "lordBagData");
        var itemType = GameType("EItemType");
        if (bagData is null || itemType is null) return result;
        var fields = InvokeInstance(bagData, "GetFieldList", Enum.ToObject(itemType, 2));
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

        AddToolFields(Read(walletData, "fieldList"));
        var itemType = GameType("EItemType");
        if (bagData is not null && itemType is not null)
            AddToolFields(InvokeInstance(bagData, "GetFieldList", Enum.ToObject(itemType, 10)));
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
                    Plugin.Logger.LogInfo($"Bulk tool quality resolved: id={toolId}, type={toolType}, saveQuality={ReadNullableInt(save, "quality") ?? 0}, saveLevel={ReadNullableInt(save, "level") ?? 0}, definitionQuality={ReadNullableInt(definition, "quality") ?? 0}, resolved={quality}");

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
        if (IsRankedQuality(definitionQuality)) return definitionQuality;

        var savedQuality = ReadNullableInt(save, "quality") ?? 0;
        if (IsRankedQuality(savedQuality)) return savedQuality;

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

    private static bool IsRankedQuality(int quality) => quality is 3 or 4 or 5 or 6 or 8;

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
        var localizedName = Clean(ReadString(definition, "name") ?? runtimeName ?? UiText.L("이름 없는 아이템", "Unnamed item", "未命名物品", "未命名物品"));
        var englishName = Clean(EnglishName(definition, localizedName) ?? localizedName);
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
        AddAffixes(Read(equip, "affixList"));
        AddAffixes(Read(save, "affixList"));
        AddAffix(Read(equip, "runewordsAffixData"));
        AddAffix(Read(save, "runewordsAffixData"));
        var itemLevel = ReadNullableInt(save, "level") ?? 0;
        var affixes = affixObjects.Select(affix => DescribeAffix(affix, itemLevel)).Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var affixSummary = string.Join("  ·  ", affixes);

        void AddAffixes(object? values)
        {
            foreach (var value in ReadList(values)) AddAffix(value);
        }

        void AddAffix(object? value)
        {
            if (value is null) return;
            var savedAffix = Read(value, "saveData") ?? value;
            var key = string.Join(":", new object?[]
            {
                ReadNullableInt(savedAffix, "id"), ReadNullableInt(savedAffix, "quality"), ReadNullableInt(savedAffix, "level"),
                ReadNullableInt(savedAffix, "value"), ReadNullableInt(savedAffix, "runewordsId"),
                ReadNullableInt(savedAffix, "talentId"), ReadNullableInt(savedAffix, "abilityId")
            }.Select(part => part?.ToString() ?? string.Empty));
            if (key.Replace(":", string.Empty, StringComparison.Ordinal).Length == 0)
                key = $"object:{Read(value, "Pointer") ?? value.GetHashCode()}";
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
        var setMemberRows = ReadList(Read(equip, "setsNameList")).Select(member => Read(member, "tEquipData")).Where(member => member is not null).ToList();
        if (setMemberRows.Count == 0 && setId > 0)
            setMemberRows = ReadValues(ReadStatic("TableData", "TEquipDict")).Where(member => ReadNullableInt(member, "setsId") == setId).Cast<object?>().ToList();
        var setMembers = setMemberRows.Select(member => Clean(ReadString(member, "name") ?? EnglishName(member, ReadString(member, "name")) ?? string.Empty))
            .Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var englishSetMembers = setMemberRows.Select(member => Clean(EnglishName(member, ReadString(member, "name")) ?? string.Empty))
            .Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var setBonusRows = setId <= 0
            ? new List<object>()
            : ReadValues(ReadStatic("TableData", "TEquipSetsEffectDict"))
                .Where(effect => ReadNullableInt(effect, "sesId") == setId)
                .OrderBy(effect => ReadNullableInt(effect, "index") ?? int.MaxValue).ToList();
        var setBonuses = setBonusRows.Select(effect =>
        {
            var required = ReadNullableInt(effect, "index") ?? 0;
            var current = Clean(ReadString(effect, "des") ?? string.Empty);
            var english = Clean(EnglishText(effect, "_des", current) ?? current);
            var text = FirstNonEmpty(current, english, UiText.L("세트 효과", "Set bonus", "套装效果", "套裝效果"));
            return $"• {(required > 0 ? UiText.L($"[{required}세트] ", $"[{required} pieces] ", $"[{required}件] ", $"[{required}件] ") : string.Empty)}{text}";
        }).Where(value => value.Length > 0).ToList();
        var currentSetDescription = Clean(ReadString(setData, "des") ?? string.Empty);
        var englishSetDescription = Clean(EnglishText(setData, "_des", currentSetDescription) ?? currentSetDescription);
        var setDescription = FirstNonEmpty(currentSetDescription, englishSetDescription);
        if (!string.IsNullOrWhiteSpace(setDescription)) setBonuses.Insert(0, $"• {setDescription}");
        var setMembersText = setMembers.Count == 0 ? UiText.L("• 구성 장비 정보 없음", "• No set-piece information", "• 无套装部件信息", "• 無套裝部件資訊") : "• " + string.Join("\n• ", setMembers);
        var setBonusesText = setBonuses.Count == 0 ? UiText.L("• 세트 효과 정보 없음", "• No set-bonus information", "• 无套装效果信息", "• 無套裝效果資訊") : string.Join("\n", setBonuses);
        var localizedDescription = Clean(ReadString(definition, "des") ?? string.Empty);
        var englishDescription = Clean(EnglishText(definition, "_des", localizedDescription) ?? localizedDescription);
        var mainAttributeDescription = Clean(InvokeString(equip ?? item, "GetMainAttrDesc") ?? string.Empty);
        var descriptions = new[] { localizedDescription, englishDescription, mainAttributeDescription }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
        var description = string.Join("\n", descriptions);

        var synonyms = QualitySynonyms(quality);
        var searchText = string.Join(" ", new[]
        {
            localizedName, englishName, qualityName, englishQualityName, synonyms, partName, englishPartName, subtypeName, englishSubtypeName, storageLabel,
            Read(save, "type")?.ToString() ?? string.Empty, $"level {ReadNullableInt(save, "level") ?? 0}", description, affixSummary,
            setName, englishSetName, setJob, string.Join(" ", setMembers), string.Join(" ", englishSetMembers), string.Join(" ", setBonuses)
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
            AffixSearchText = Clean(string.Join(" ", new[] { affixSummary, setName, englishSetName, string.Join(" ", setBonuses) })),
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

            object? result;
            switch (item.StorageSource)
            {
                case StorageSource.Inventory:
                    var preferredVaultData = Read(houseStoreData, "storeTreaData");
                    if (item.SourceField is null)
                    {
                        message = UiText.L("아이템의 현재 인벤토리 칸을 찾지 못했습니다.", "The item's current inventory slot was not found.", "未找到物品当前所在的背包格。", "找不到物品目前所在的背包格。");
                        return false;
                    }
                    InvokeStaticMany("ItemSys", "QuickMoveItemFromBagToStore", item.ItemData);
                    if (Read(item.SourceField, "itemData") is not null)
                    {
                        message = UiText.L("게임의 자동 창고 이동이 적용되지 않았습니다. 창고 또는 Vault 공간을 확인하세요.", "The game's automatic storage move failed. Check warehouse or Vault space.", "游戏自动入库失败。请检查仓库或宝库空间。", "遊戲自動入庫失敗。請檢查倉庫或寶庫空間。");
                        return false;
                    }
                    var vaultGroup = preferredVaultData is null ? null : InvokeInstance(preferredVaultData, "FindEquipGroup", item.ItemData);
                    message = vaultGroup is null
                        ? UiText.L($"{item.Name} → 일반 창고 이동 완료", $"{item.Name} → moved to warehouse", $"{item.Name} → 已移至仓库", $"{item.Name} → 已移至倉庫")
                        : UiText.L($"{item.Name} → Vault 이동 완료", $"{item.Name} → moved to Vault", $"{item.Name} → 已移至宝库", $"{item.Name} → 已移至寶庫");
                    return true;

                case StorageSource.Warehouse:
                    if (item.SourceField is null)
                    {
                        message = UiText.L("아이템의 현재 창고 칸을 찾지 못했습니다.", "The item's current warehouse slot was not found.", "未找到物品当前所在的仓库格。", "找不到物品目前所在的倉庫格。");
                        return false;
                    }
                    InvokeStaticMany("ItemSys", "QuickMoveItemFromStoreToBag", item.ItemData);
                    if (Read(item.SourceField, "itemData") is not null)
                    {
                        message = UiText.L("인벤토리가 가득 찼거나 현재 꺼낼 수 없습니다.", "The inventory is full or the item cannot be taken now.", "背包已满或当前无法取出该物品。", "背包已滿或目前無法取出該物品。");
                        return false;
                    }
                    message = UiText.L($"{item.Name} → 인벤토리 이동 완료", $"{item.Name} → moved to inventory", $"{item.Name} → 已移至背包", $"{item.Name} → 已移至背包");
                    return true;

                case StorageSource.Treasure:
                    var treasureData = Read(houseStoreData, "storeTreaData");
                    result = treasureData is null || item.GroupData is null
                        ? null
                        : InvokeInstance(treasureData, "TryTakeEquip", item.GroupData, item.ItemData);
                    if (result is not bool taken || !taken)
                    {
                        message = UiText.L("인벤토리가 가득 찼거나 보관함에서 꺼낼 수 없습니다.", "The inventory is full or the item cannot be taken from the Vault.", "背包已满或无法从宝库取出该物品。", "背包已滿或無法從寶庫取出該物品。");
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
        3 => UiText.L("희귀", "Rare", "稀有", "稀有"),
        4 => UiText.L("전설", "Legendary", "传奇", "傳奇"),
        5 => UiText.L("신화", "Mythic", "神话", "神話"),
        6 => UiText.L("세트", "Set", "套装", "套裝"),
        8 => UiText.L("고유", "Unique", "独特", "獨特"),
        _ => UiText.L("기타", "Other", "其他", "其他")
    };

    private static string QualitySynonyms(int quality) => quality switch
    {
        3 => "희귀 rare 稀有", 4 => "전설 legendary 传奇 傳奇", 5 => "신화 mythic 神话 神話", 6 => "세트 set 套装 套裝", 8 => "고유 unique 独特 獨特", _ => "기타 other 其他"
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
