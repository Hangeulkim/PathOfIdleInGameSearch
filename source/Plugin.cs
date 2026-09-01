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
    public const string PluginVersion = "1.1.0";

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
        GUI.Label(new Rect(rect.x + 340f, rect.y + 318f, 300f, 24f), UiText.L("스킬은 항상 초기화 후 재배분", "Skills always reset, then reallocate", "技能始终重置后重新分配", "技能一律重設後重新分配"), hintStyle!);

        DrawAutoBuildButton(new Rect(rect.x + 24f, rect.y + 356f, (rect.width - 60f) / 2f, 58f), AutoBuildAction.Gear,
            UiText.L("장비 자동 장착", "Auto-equip Gear", "自动装备", "自動裝備"));
        DrawAutoBuildButton(new Rect(rect.x + 36f + (rect.width - 60f) / 2f, rect.y + 356f, (rect.width - 60f) / 2f, 58f), AutoBuildAction.Skills,
            UiText.L("스킬·특성 자동 배분", "Auto-allocate Skills", "自动分配技能", "自動分配技能"));

        GUI.Label(new Rect(rect.x + 24f, rect.y + 428f, rect.width - 48f, 64f), UiText.L(
            "테마는 장비 전체 옵션·세트 효과와 스킬 우선순위에 적용됩니다. 자동은 직업과 현재 스킬을 분석합니다.\n스킬은 게임의 직업별 추천 빌드를 우선하고 현재 특성표 안에서 배분합니다.",
            "The theme affects gear affixes, set bonuses, and skill priority. Auto analyzes the job and current skills.\nSkills prioritize the game's job build guide, then allocate within the current talent grid.",
            "装备：根据实际词缀与职业倾向比较可穿戴候选。\n技能：优先采用游戏的职业构筑指南，并在当前天赋表中分配点数。",
            "裝備：根據實際詞綴與職業傾向比較可穿戴候選。\n技能：優先採用遊戲的職業流派指南，並在目前天賦表中分配點數。"), tooltipBodyStyle!);
        GUI.Label(new Rect(rect.x + 24f, rect.y + 500f, rect.width - 48f, 42f), UiText.L(
            "주의: 특성 초기화를 켜면 게임의 초기화 비용이 적용될 수 있습니다. 실행 버튼은 두 번 눌러야 동작합니다.",
            "Caution: resetting talents may use the game's normal reset cost. Each action requires a second click.",
            "注意：重置天赋可能会消耗游戏正常的重置费用。操作按钮需要点击两次。",
            "注意：重設天賦可能會消耗遊戲正常的重設費用。操作按鈕需要點擊兩次。"), hintStyle!);
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
internal sealed record GearCandidate(ItemSearchRecord Record, string Key, int Part, int SetId, int DefinitionId, int WeaponType, double Score, int DirectMatches, int ThemeMatches);
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
        List<HashSet<int>> WeaponRequirements,
        HashSet<int> SkillIds,
        HashSet<int> SkillInfoIds,
        HashSet<int> TalentIds,
        HashSet<int> MasteryIds,
        HashSet<int> AbilityIds,
        HashSet<int> RecommendedEquipmentIds,
        string[] SkillTerms);

    private sealed record EquipmentScore(double Total, int DirectMatches, int ThemeMatches);
    private sealed record GearSlot(int Part, bool MainWeapon, int WeaponSlotIndex, string Label);
    private sealed record LoadoutState(List<GearCandidate> Items, HashSet<string> UsedKeys, double HeuristicScore);

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
            if (value.Contains("tc") || value.Contains("traditional") || value.Contains("繁")) return "zh-tw";
            if (value.Contains("cn") || value.Contains("simplified") || value.Contains("简")) return "zh-cn";
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
            var beam = new List<LoadoutState> { new(new List<GearCandidate>(), new HashSet<string>(StringComparer.Ordinal), 0d) };
            foreach (var slot in slots)
            {
                var current = currentBySlot[slot];
                var currentKey = current is null ? string.Empty : NativeObjectKey(current, current);
                var options = candidates.Where(candidate => candidate.Part == slot.Part)
                    .OrderByDescending(candidate => candidate.Score)
                    .Take(14).ToList();
                if (current is not null && options.All(candidate => candidate.Key != currentKey))
                {
                    var currentCandidate = candidates.FirstOrDefault(candidate => candidate.Key == currentKey);
                    if (currentCandidate is not null) options.Add(currentCandidate);
                }

                beam = beam.SelectMany(state => options
                        .Where(candidate => !state.UsedKeys.Contains(candidate.Key))
                        .Select(candidate => new LoadoutState(
                            state.Items.Append(candidate).ToList(),
                            new HashSet<string>(state.UsedKeys, StringComparer.Ordinal) { candidate.Key },
                            state.HeuristicScore + candidate.Score)))
                    .OrderByDescending(state => state.HeuristicScore + EstimatePartialSetSynergy(state.Items, profile))
                    .Take(320).ToList();
                if (beam.Count == 0) throw new InvalidOperationException($"No valid loadout remains for {slot.Label}.");
            }

            var currentItems = currentBySlot.Values.Where(item => item is not null).Cast<object>().ToList();
            var winner = beam.Select(state => new { State = state, Score = ScoreCompleteLoadout(state.Items, hero, profile, currentItems) })
                .OrderByDescending(entry => entry.Score).First();

            var changed = 0;
            var unchanged = 0;
            var failed = 0;
            var directMatches = 0;
            var themeMatches = 0;
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                var candidate = winner.State.Items[index];
                var current = GetEquippedItem(hero, slot.Part, slot.MainWeapon);
                if (NativeEquals(current, candidate.Record.ItemData))
                {
                    unchanged++;
                    continue;
                }
                if (!TryMoveCandidateToBag(candidate.Record, seasonData)) { failed++; continue; }
                InvokeInstance(lordData, "QuickWearEquip", candidate.Record.ItemData, slot.WeaponSlotIndex);
                if (!NativeEquals(Read(candidate.Record.ItemData, "ownerHeroData"), hero) && InvokeStatic("ItemSys", "FindHeroEquipFieldByItem", candidate.Record.ItemData) is null)
                {
                    failed++;
                    continue;
                }
                changed++;
                directMatches += candidate.DirectMatches;
                themeMatches += candidate.ThemeMatches;
            }

            message = changed > 0
                ? UiText.L($"8부위 최적화 완료 · {focus.Localized} · 교체 {changed}개 · 스킬 직접 효과 {directMatches} · 테마 효과 {themeMatches} · 유지 {unchanged}개{(failed > 0 ? $" · 실패 {failed}개" : string.Empty)}", $"8-slot loadout optimized · {focus.English} · changed {changed} · direct skill effects {directMatches} · theme effects {themeMatches} · kept {unchanged}{(failed > 0 ? $" · failed {failed}" : string.Empty)}", $"8部位优化完成 · {focus.Localized} · 更换 {changed} · 技能直接效果 {directMatches} · 主题效果 {themeMatches} · 保留 {unchanged}{(failed > 0 ? $" · 失败 {failed}" : string.Empty)}", $"8部位最佳化完成 · {focus.Localized} · 更換 {changed} · 技能直接效果 {directMatches} · 主題效果 {themeMatches} · 保留 {unchanged}{(failed > 0 ? $" · 失敗 {failed}" : string.Empty)}")
                : UiText.L($"{focus.Localized} 테마에서 현재 장비보다 높은 점수의 후보가 없습니다.", $"No {focus.English} candidate scored above the current gear.", $"{focus.Localized} 主题下没有评分更高的装备。", $"{focus.Localized} 主題下沒有評分更高的裝備。");
            return changed > 0 || failed == 0;
        }
        catch (Exception error)
        {
            message = UiText.L($"장비 자동 장착 실패: {error.GetBaseException().Message}", $"Auto-equip failed: {error.GetBaseException().Message}", $"自动装备失败：{error.GetBaseException().Message}", $"自動裝備失敗：{error.GetBaseException().Message}");
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

            var talents = ReadValues(Read(talentData, "talentDic")).Concat(ReadList(Read(talentData, "extraTalentList")))
                .DistinctBy(value => NativeObjectKey(value, value)).ToList();
            if (talents.Count == 0)
            {
                message = UiText.L("현재 영웅의 스킬·특성 표를 찾지 못했습니다.", "The selected hero's skill grid is unavailable.", "找不到当前英雄的技能天赋表。", "找不到目前英雄的技能天賦表。");
                return false;
            }

            var allocatable = talents.Where(talent => (ReadNullableInt(Read(talent, "tTalentData"), "id") ?? 0) > 0 && !ReadBool(InvokeInstance(talent, "IsLock"))).ToList();
            if (allocatable.Count == 0)
            {
                message = UiText.L("배분 가능한 스킬·특성이 없습니다.", "No skills or talents can receive points.", "没有可分配点数的技能或天赋。", "沒有可分配點數的技能或天賦。");
                return false;
            }

            var resetPrice = Convert.ToInt32(InvokeInstance(talentData, "GetResetTalentPrice") ?? 0, CultureInfo.InvariantCulture);
            var bloodType = CreateEnum("EResType", 2) ?? throw new InvalidOperationException("Blood resource type is unavailable.");
            var bloodBefore = Convert.ToInt32(InvokeInstance(townData, "GetRes", bloodType) ?? 0, CultureInfo.InvariantCulture);
            if (bloodBefore < resetPrice)
            {
                message = UiText.L($"초기화 재화 부족 · 필요 피 {resetPrice:N0} / 보유 {bloodBefore:N0}", $"Not enough Blood to reset · need {resetPrice:N0} / have {bloodBefore:N0}", $"重置资源不足 · 需要鲜血 {resetPrice:N0} / 持有 {bloodBefore:N0}", $"重設資源不足 · 需要鮮血 {resetPrice:N0} / 持有 {bloodBefore:N0}");
                return false;
            }

            var spentBefore = talents.Sum(talent => Math.Max(0, Convert.ToInt32(InvokeInstance(talent, "GetLevel") ?? 0, CultureInfo.InvariantCulture) - (ReadNullableInt(talent, "baseLevel") ?? 0)));
            var resetResult = spentBefore > 0
                ? Convert.ToInt32(InvokeInstance(talentData, "ResetTalentPoint") ?? 1, CultureInfo.InvariantCulture)
                : 0;
            if (resetResult == 1)
            {
                message = UiText.L($"특성 초기화 실패 · 필요 피 {resetPrice:N0} / 보유 {bloodBefore:N0}", $"Talent reset failed · need {resetPrice:N0} Blood / have {bloodBefore:N0}", $"天赋重置失败 · 需要鲜血 {resetPrice:N0} / 持有 {bloodBefore:N0}", $"天賦重設失敗 · 需要鮮血 {resetPrice:N0} / 持有 {bloodBefore:N0}");
                return false;
            }
            var remainAfterReset = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
            var spentAfter = talents.Sum(talent => Math.Max(0, Convert.ToInt32(InvokeInstance(talent, "GetLevel") ?? 0, CultureInfo.InvariantCulture) - (ReadNullableInt(talent, "baseLevel") ?? 0)));
            if (spentBefore > 0 && spentAfter >= spentBefore)
            {
                message = UiText.L("특성 초기화가 적용되지 않았습니다. 현재 게임 상태에서 초기화할 수 없습니다.", "Talent reset was not applied in the current game state.", "当前游戏状态下无法重置天赋。", "目前遊戲狀態下無法重設天賦。");
                return false;
            }

            var focus = ResolveHeroFocus(hero, Plugin.AutoBuildTheme.Value);
            var preferred = GetPreferredTalentIds(hero, focus);
            var ranked = allocatable.OrderByDescending(talent => ScoreTalent(talent, focus, preferred)).ToList();
            var beforeAllocation = remainAfterReset;
            var guard = 0;
            while ((ReadNullableInt(saveHero, "talentRemainPoint") ?? 0) > 0 && guard++ < 2000)
            {
                var progressed = false;
                foreach (var talent in ranked)
                {
                    var before = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
                    if (before <= 0) break;
                    InvokeInstance(talentData, "AddTalentPoint", talent, 1);
                    var after = ReadNullableInt(saveHero, "talentRemainPoint") ?? before;
                    if (after < before) progressed = true;
                }
                if (!progressed) break;
            }
            var remaining = ReadNullableInt(saveHero, "talentRemainPoint") ?? 0;
            var allocated = Math.Max(0, beforeAllocation - remaining);
            if (allocated <= 0 && beforeAllocation > 0)
            {
                message = UiText.L("초기화는 완료됐지만 현재 레벨에서 배분 가능한 특성이 없습니다.", "Reset completed, but no talent can receive points at the current level.", "重置已完成，但当前等级没有可分配的天赋。", "重設已完成，但目前等級沒有可分配的天賦。");
                return false;
            }

            var spentBlood = Math.Max(0, bloodBefore - Convert.ToInt32(InvokeInstance(townData, "GetRes", bloodType) ?? bloodBefore, CultureInfo.InvariantCulture));
            message = UiText.L($"스킬 자동 분배 완료 · {focus.Localized} · {allocated:N0}포인트 · 초기화 피 {spentBlood:N0}{(remaining > 0 ? $" · 미사용 {remaining:N0}" : string.Empty)}", $"Skills allocated · {focus.English} · {allocated:N0} points · reset Blood {spentBlood:N0}{(remaining > 0 ? $" · {remaining:N0} unspent" : string.Empty)}", $"技能分配完成 · {focus.Localized} · {allocated:N0} 点 · 重置鲜血 {spentBlood:N0}{(remaining > 0 ? $" · 剩余 {remaining:N0}" : string.Empty)}", $"技能分配完成 · {focus.Localized} · {allocated:N0} 點 · 重設鮮血 {spentBlood:N0}{(remaining > 0 ? $" · 剩餘 {remaining:N0}" : string.Empty)}");
            return true;
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
        var textParts = new List<string>
        {
            ReadString(job, "name") ?? string.Empty,
            ReadString(job, "des") ?? string.Empty,
            EnglishName(job, string.Empty) ?? string.Empty,
            EnglishText(job, "_des", string.Empty) ?? string.Empty
        };
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
                var mastery = Read(Read(talent, "masteryData"), "tMasteryData");
                textParts.Add(EnglishName(definition, string.Empty) ?? string.Empty);
                textParts.Add(EnglishName(skillRow, string.Empty) ?? string.Empty);
                textParts.Add(EnglishText(info, "_des", string.Empty) ?? string.Empty);
                textParts.Add(EnglishName(mastery, string.Empty) ?? string.Empty);
            }
        }
        catch { }
        var text = Clean(string.Join(" ", textParts)).ToLowerInvariant();
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
        var bestSpecialized = specialized.Select(focus => (Focus: focus, Score: KeywordScore(text, focus.Keywords)))
            .OrderByDescending(entry => entry.Score).First();
        if (bestSpecialized.Score >= 2) return bestSpecialized.Focus;
        var phy = ReadHeroAttr(hero, 1) + ReadHeroAttr(hero, 11) * 3d + KeywordScore(text, PhysicalWords) * 30d;
        var ele = ReadHeroAttr(hero, 2) + ReadHeroAttr(hero, 13) * 3d + KeywordScore(text, ElementalWords) * 30d;
        var support = KeywordScore(text, SupportWords) * 50d + ReadHeroAttr(hero, 81) * 10d + ReadHeroAttr(hero, 191) * 10d;
        var tank = KeywordScore(text, TankWords) * 50d + ReadHeroAttr(hero, 3) + ReadHeroAttr(hero, 4) + ReadHeroAttr(hero, 5) * 0.1d;
        if (support > Math.Max(phy, ele) * 0.65d) return new HeroFocus("support", UiText.L("지원·오라", "Support / Aura", "辅助/光环", "輔助/光環"), "Support / Aura", SupportWords);
        if (tank > Math.Max(phy, ele) * 1.15d) return new HeroFocus("tank", UiText.L("생존·방어", "Defense / Survival", "防御/生存", "防禦/生存"), "Defense / Survival", TankWords);
        if (ele > phy * 1.12d) return new HeroFocus("elemental", UiText.L("원소·주문", "Elemental / Spell", "元素/法术", "元素/法術"), "Elemental / Spell", ElementalWords);
        if (phy > ele * 1.12d) return new HeroFocus("physical", UiText.L("물리·무예", "Physical / Martial", "物理/武技", "物理/武技"), "Physical / Martial", PhysicalWords);
        return new HeroFocus("hybrid", UiText.L("균형·혼합", "Balanced / Hybrid", "均衡/混合", "均衡/混合"), "Balanced / Hybrid", PhysicalWords.Concat(ElementalWords).ToArray());
    }

    private static object? GetPreferredBuild(object hero, HeroFocus? focus = null)
    {
        var jobId = ReadNullableInt(Read(hero, "saveHeroData"), "jobId") ?? ReadNullableInt(Read(hero, "tHeroJobData"), "id") ?? 0;
        focus ??= DetermineHeroFocus(hero);
        return ReadValues(ReadStatic("TableData", "TBuildsDict")).Where(build => ReadNullableInt(build, "jobId") == jobId)
            .OrderByDescending(build => KeywordScore(Clean(string.Join(" ", EnglishName(build, string.Empty), EnglishText(build, "_des", string.Empty))).ToLowerInvariant(), focus.Keywords))
            .ThenBy(build => ReadNullableInt(build, "index") ?? int.MaxValue).FirstOrDefault();
    }

    private static HashSet<int> GetPreferredTalentIds(object hero, HeroFocus focus)
    {
        var result = new HashSet<int>();
        var build = GetPreferredBuild(hero, focus);
        if (build is null) return result;
        foreach (var property in new[] { "skillArr", "masteryArr" })
            foreach (var value in ReadSequence(Read(build, property)))
                try { result.Add(Convert.ToInt32(value, CultureInfo.InvariantCulture)); } catch { }
        return result;
    }

    private static double ScoreTalent(object talent, HeroFocus focus, HashSet<int> preferred)
    {
        var definition = Read(talent, "tTalentData");
        var skill = Read(talent, "skillData");
        var skillRow = Read(skill, "tSkillData") ?? (ReadNullableInt(definition, "skillId") is > 0 and var skillId ? InvokeStatic("TableData", "getTSkillData", skillId) : null);
        var info = Read(skill, "tSkillInfoData") ?? (ReadNullableInt(skillRow, "infoId") is > 0 and var infoId ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null);
        var mastery = Read(Read(talent, "masteryData"), "tMasteryData") ?? (ReadNullableInt(definition, "masteryId") is > 0 and var masteryId ? InvokeStatic("TableData", "getTMasteryData", masteryId) : null);
        var text = Clean(string.Join(" ", EnglishName(definition, string.Empty), EnglishName(skillRow, string.Empty), EnglishText(info, "_des", string.Empty), EnglishName(mastery, string.Empty))).ToLowerInvariant();
        var id = ReadNullableInt(definition, "id") ?? 0;
        var skillKey = ReadNullableInt(definition, "skillId") ?? 0;
        var masteryKey = ReadNullableInt(definition, "masteryId") ?? 0;
        var guide = preferred.Contains(id) || preferred.Contains(skillKey) || preferred.Contains(masteryKey) ? 10000d : 0d;
        var focusScore = KeywordScore(text, focus.Keywords) * 120d;
        var utility = KeywordScore(text, SupportWords) * 16d + KeywordScore(text, TankWords) * 10d;
        var floor = ReadNullableInt(definition, "floor") ?? 0;
        return guide + focusScore + utility - floor * 0.1d;
    }

    private static HeroEffectProfile BuildHeroEffectProfile(object hero, HeroFocus focus)
    {
        var heroSave = Read(hero, "saveHeroData");
        var jobRow = Read(hero, "tHeroJobData");
        var jobId = ReadNullableInt(heroSave, "jobId") ?? ReadNullableInt(jobRow, "id") ?? 0;
        var allowedWeapons = ReadSequence(Read(jobRow, "baseWeaponTypeArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
        var weaponRequirements = new List<HashSet<int>>();
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

        void AddSkill(object? skill)
        {
            if (skill is null) return;
            var row = Read(skill, "tSkillData") ?? skill;
            var skillId = ReadNullableInt(row, "id") ?? 0;
            if (skillId > 0) skillIds.Add(skillId);
            var info = Read(skill, "tSkillInfoData") ?? (ReadNullableInt(row, "infoId") is > 0 and var infoId ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null);
            var resolvedInfoId = ReadNullableInt(info, "id") ?? ReadNullableInt(row, "infoId") ?? 0;
            if (resolvedInfoId > 0) skillInfoIds.Add(resolvedInfoId);
            var required = ReadSequence(Read(row, "weaponArr")).Select(ToInt).Where(value => value > 0).ToHashSet();
            if (required.Count > 0 && !weaponRequirements.Any(group => group.SetEquals(required))) weaponRequirements.Add(required);
            AddTerm(ReadString(row, "name"));
            AddTerm(EnglishName(row, string.Empty));
        }

        AddSkill(InvokeInstance(hero, "GetNowBaseSkillData"));
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
        foreach (var value in ReadSequence(Read(preferredBuild, "equipArr")))
        {
            var id = ToInt(value);
            if (id > 0) recommendedEquipment.Add(id);
        }

        return new HeroEffectProfile(focus, jobId, allowedWeapons, weaponRequirements, skillIds, skillInfoIds, talentIds, masteryIds, abilityIds, recommendedEquipment, terms.ToArray());
    }

    private static GearCandidate? CreateGearCandidate(ItemSearchRecord record, object hero, HeroEffectProfile profile)
    {
        var equip = Read(record.ItemData, "itemEquipData");
        var definition = Read(equip, "tEquipData");
        var save = Read(record.ItemData, "saveItemData");
        var part = ReadNullableInt(definition, "part") ?? 0;
        if (part is < 1 or > 7) return null;
        var heroSave = Read(hero, "saveHeroData");
        var heroLevel = ReadNullableInt(heroSave, "level") ?? 0;
        var requiredLevel = ReadNullableInt(Read(equip, "tFiniallEquipLevelData"), "wearLevel") ?? ReadNullableInt(save, "level") ?? 0;
        if (requiredLevel > heroLevel) return null;
        var requiredJob = ReadNullableInt(definition, "jobId") ?? 0;
        if (requiredJob > 0 && requiredJob != profile.JobId) return null;
        var minType = ReadNullableInt(definition, "minType") ?? 0;
        var heroJobRow = Read(hero, "tHeroJobData");
        if (part == 1 && minType > 0)
        {
            if (profile.AllowedWeaponTypes.Count > 0 && !profile.AllowedWeaponTypes.Contains(minType)) return null;
            if (profile.WeaponRequirements.Count > 0 && !profile.WeaponRequirements.Any(group => group.Contains(minType))) return null;
        }
        else if (part >= 4 && minType > 0)
        {
            var armorType = ReadNullableInt(heroJobRow, "baseArmorType") ?? 0;
            if (armorType > 0 && armorType != minType) return null;
        }
        var setId = ReadNullableInt(Read(equip, "tEquipSetsData"), "id") ?? ReadNullableInt(definition, "setsId") ?? 0;
        var definitionId = ReadNullableInt(definition, "id") ?? 0;
        var score = ScoreEquipment(record.ItemData, profile);
        return new GearCandidate(record, NativeObjectKey(record.ItemData, record.SourceField ?? record.ItemData), part, setId, definitionId, minType, score.Total, score.DirectMatches, score.ThemeMatches);
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
        var qualityWeight = quality switch { 8 => 430d, 6 => 410d, 5 => 380d, 4 => 300d, 3 => 220d, 2 => 140d, _ => quality * 45d };
        var descriptions = new List<string>();
        var directMatches = 0;
        foreach (var affix in CollectEquipmentAffixes(item))
        {
            var runtimeAffix = ResolveRuntimeAffix(affix);
            descriptions.Add(GetAffixSearchText(runtimeAffix));
            directMatches += CountDirectAffixMatches(runtimeAffix, profile);
        }
        descriptions.Add(Clean(string.Join(" ", ReadString(definition, "des") ?? string.Empty, EnglishText(definition, "_des", string.Empty) ?? string.Empty)));
        var setData = Read(equip, "tEquipSetsData");
        var setId = ReadNullableInt(setData, "id") ?? 0;
        if (setId > 0)
        {
            var setJobId = ReadNullableInt(setData, "jobId") ?? 0;
            if (setJobId > 0 && setJobId == profile.JobId) directMatches++;
            descriptions.Add(Clean(string.Join(" ", ReadString(setData, "des") ?? string.Empty, EnglishText(setData, "_des", ReadString(setData, "des")) ?? string.Empty)));
            foreach (var effect in ReadValues(ReadStatic("TableData", "TEquipSetsEffectDict")).Where(effect => ReadNullableInt(effect, "sesId") == setId))
                descriptions.Add(Clean(string.Join(" ", ReadString(effect, "des") ?? string.Empty, EnglishText(effect, "_des", ReadString(effect, "des")) ?? string.Empty)));
        }
        var text = string.Join(" ", descriptions).ToLowerInvariant();
        directMatches += Math.Min(3, profile.SkillTerms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)));
        var themeMatches = KeywordScore(text, profile.Focus.Keywords);
        var focusBonus = themeMatches * (profile.Focus.IsManual ? 520d : 240d);
        var generalBonus = KeywordScore(text, new[] { "all attack", "all defense", "primary attribute", "crit", "speed", "cost", "resist", "health" }) * 18d;
        var definitionId = ReadNullableInt(definition, "id") ?? 0;
        var guideBonus = profile.RecommendedEquipmentIds.Contains(definitionId) ? 1700d : 0d;
        var total = qualityWeight + level * 5d + forge * 12d + main * 0.04d + descriptions.Count * 8d + focusBonus + generalBonus + directMatches * 2200d + guideBonus;
        return new EquipmentScore(total, directMatches, themeMatches);
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

    private static double EstimatePartialSetSynergy(IEnumerable<GearCandidate> items, HeroEffectProfile profile)
    {
        var score = 0d;
        foreach (var group in items.Where(item => item.SetId > 0).GroupBy(item => item.SetId))
        {
            var count = group.Count();
            foreach (var effect in ReadValues(ReadStatic("TableData", "TEquipSetsEffectDict"))
                         .Where(effect => ReadNullableInt(effect, "sesId") == group.Key && (ReadNullableInt(effect, "index") ?? int.MaxValue) <= count))
            {
                var text = Clean(string.Join(" ", ReadString(effect, "des") ?? string.Empty, EnglishText(effect, "_des", string.Empty) ?? string.Empty)).ToLowerInvariant();
                var abilityId = ReadNullableInt(effect, "abilityId") ?? 0;
                score += 950d;
                score += KeywordScore(text, profile.Focus.Keywords) * 420d;
                score += Math.Min(3, profile.SkillTerms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))) * 1500d;
                if (abilityId > 0 && profile.AbilityIds.Contains(abilityId)) score += 1800d;
            }
        }
        return score;
    }

    private static double ScoreCompleteLoadout(List<GearCandidate> items, object hero, HeroEffectProfile profile, List<object> currentItems)
    {
        var score = items.Sum(item => item.Score) + EstimatePartialSetSynergy(items, profile);
        var weaponTypes = items.Where(item => item.Part == 1).Select(item => item.WeaponType).Where(value => value > 0).ToHashSet();
        foreach (var requirement in profile.WeaponRequirements)
            score += requirement.Overlaps(weaponTypes) ? 1600d : -30000d;

        // Use the game's own AttrData calculations on a temporary copy. This keeps
        // the user's hero and save untouched while accounting for core stats,
        // ordinary affixes, percentage conversions, crit and skill-speed buckets.
        score += EvaluateFinalPerformance(items, hero, profile, currentItems);
        return score;
    }

    private static double EvaluateFinalPerformance(List<GearCandidate> items, object hero, HeroEffectProfile profile, List<object> currentItems)
    {
        try
        {
            var heroAttr = Read(hero, "attrData");
            if (heroAttr is null) return 0d;
            var simulated = InvokeStatic("AttrData", "copyCreate", heroAttr);
            if (simulated is null) return 0d;
            foreach (var current in currentItems) ApplyEquipmentToAttr(simulated, current, false);
            foreach (var candidate in items) ApplyEquipmentToAttr(simulated, candidate.Record.ItemData, true);

            var phy = ReadAttr(simulated, 1);
            var ele = ReadAttr(simulated, 2);
            var physicalTheme = profile.Focus.Key is "physical" or "bleed" or "crit" or "minion";
            var elementalTheme = profile.Focus.Key is "elemental" or "fire" or "ice" or "lightning" or "corrosion";
            var attack = physicalTheme ? phy : elementalTheme ? ele : Math.Max(phy, ele);
            var allDamage = ToRate(ReadAttr(simulated, 218)) + ToRate(ReadAttr(simulated, 133));
            var familyDamage = physicalTheme ? ToRate(ReadAttr(simulated, 41)) : elementalTheme ? ToRate(ReadAttr(simulated, 42)) : Math.Max(ToRate(ReadAttr(simulated, 41)), ToRate(ReadAttr(simulated, 42)));
            var typeDamage = profile.Focus.Key switch
            {
                "fire" => ReadAttr(simulated, 54) + ReadAttr(simulated, 113),
                "ice" => ReadAttr(simulated, 55) + ReadAttr(simulated, 114),
                "lightning" => ReadAttr(simulated, 56) + ReadAttr(simulated, 115),
                "bleed" => ReadAttr(simulated, 121) + ReadAttr(simulated, 123),
                "corrosion" => ReadAttr(simulated, 122) + ReadAttr(simulated, 124),
                "physical" => Math.Max(ReadAttr(simulated, 51) + ReadAttr(simulated, 110), Math.Max(ReadAttr(simulated, 52) + ReadAttr(simulated, 111), ReadAttr(simulated, 53) + ReadAttr(simulated, 112))),
                _ => 0d
            };
            var critChance = Math.Clamp(ToRate(ReadAttr(simulated, 31)) + ToRate(ReadAttr(simulated, 33)) + ToRate(ReadAttr(simulated, 35)), 0d, 1d);
            var critDamage = Math.Max(0.5d, 0.5d + ToRate(ReadAttr(simulated, 37)));
            var speed = 1d + Math.Max(ToRate(ReadAttr(simulated, 71)), ToRate(ReadAttr(simulated, 72)))
                        + Math.Max(ToRate(ReadAttr(simulated, 99)), Math.Max(ToRate(ReadAttr(simulated, 100)), ToRate(ReadAttr(simulated, 101))));
            var expectedDamage = Math.Max(0d, attack) * Math.Max(0.05d, 1d + allDamage + familyDamage)
                                 * Math.Max(0.05d, 1d + ToRate(typeDamage)) * Math.Max(0.1d, 1d + critChance * critDamage)
                                 * Math.Max(0.1d, speed);

            var hp = Math.Max(0d, ReadAttr(simulated, 5));
            var defence = Math.Max(0d, ReadAttr(simulated, 3) + ReadAttr(simulated, 4));
            var sustain = Math.Max(0d, ReadAttr(simulated, 7) + ReadAttr(simulated, 9) + ReadAttr(simulated, 93) + ReadAttr(simulated, 94));
            var support = Math.Max(0d, ReadAttr(simulated, 81) + ReadAttr(simulated, 82) + ReadAttr(simulated, 191));
            var minion = Math.Max(0d, ReadAttr(simulated, 25) * 50d + ReadAttr(simulated, 190));
            var damageScore = Math.Log10(1d + expectedDamage) * (profile.Focus.Key is "support" or "defense" ? 900d : 1700d);
            var survivalScore = Math.Log10(1d + hp + defence * 2d + sustain * 4d) * (profile.Focus.Key is "defense" ? 1300d : 420d);
            var utilityScore = Math.Log10(1d + support * 10d) * (profile.Focus.Key == "support" ? 1200d : 180d);
            var minionScore = Math.Log10(1d + minion) * (profile.Focus.Key == "minion" ? 1100d : 120d);
            return damageScore + survivalScore + utilityScore + minionScore;
        }
        catch
        {
            return 0d;
        }
    }

    private static void ApplyEquipmentToAttr(object attrData, object item, bool active)
    {
        var equip = Read(item, "itemEquipData");
        var equipAttr = Read(equip, "equipAttrData");
        var sign = active ? 1d : -1d;
        foreach (var entry in ReadEntries(Read(equipAttr, "attrDic")))
        {
            var equipType = ToInt(Read(entry, "Key"));
            var attrType = MapEquipAttrType(equipType);
            if (attrType <= 0) continue;
            double value;
            try { value = Convert.ToDouble(Read(entry, "Value") ?? 0d, CultureInfo.InvariantCulture); }
            catch { continue; }
            var attrEnum = CreateEnum("EAttrType", attrType);
            if (attrEnum is not null) InvokeInstance(attrData, "ChangeAttr", attrEnum, (float)(value * sign));
        }
        foreach (var affix in CollectEquipmentAffixes(item))
            InvokeInstance(ResolveRuntimeAffix(affix), "SetActiveAttrData", attrData, active);
    }

    private static int MapEquipAttrType(int type) => type switch
    {
        11 => 1, 12 => 2, 13 => 3, 14 => 4, 15 => 11, 16 => 12, 17 => 13,
        21 => 41, 22 => 42, 23 => 43, 24 => 44, 25 => 14, 26 => 15, 27 => 16,
        33 => 5, 34 => 6, 35 => 45, 36 => 46,
        _ => 0
    };

    private static double ReadAttr(object attrData, int id)
    {
        try
        {
            var attrType = CreateEnum("EAttrType", id);
            return attrType is null ? 0d : Convert.ToDouble(InvokeInstance(attrData, "GetAttrValue", attrType) ?? 0d, CultureInfo.InvariantCulture);
        }
        catch { return 0d; }
    }

    private static double ToRate(double value) => Math.Abs(value) > 5d ? value / 100d : value;

    private static List<object> CollectEquipmentAffixes(object item)
    {
        var result = new List<object>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var equip = Read(item, "itemEquipData");
        var save = Read(item, "saveItemData");
        AddMany(Read(equip, "affixList"));
        AddMany(Read(save, "affixList"));
        Add(Read(equip, "runewordsAffixData"));
        Add(Read(save, "runewordsAffixData"));
        return result;

        void AddMany(object? values)
        {
            foreach (var value in ReadList(values)) Add(value);
        }

        void Add(object? value)
        {
            if (value is null) return;
            var saved = Read(value, "saveData") ?? value;
            var key = string.Join(":", new[] { "id", "quality", "level", "value", "runewordsId", "talentId", "abilityId" }
                .Select(name => ReadNullableInt(saved, name)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
            if (key.Replace(":", string.Empty, StringComparison.Ordinal).Length == 0) key = $"ptr:{Read(value, "Pointer") ?? value.GetHashCode()}";
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
        var matches = 0;
        var jobId = ReadNullableInt(Read(affix, "tHeroJobData"), "id") ?? 0;
        if (jobId > 0) matches += jobId == profile.JobId ? 1 : -2;
        var abilityId = ReadNullableInt(Read(affix, "tAbilityData"), "id") ?? 0;
        if (abilityId > 0 && profile.AbilityIds.Contains(abilityId)) matches++;
        var talent = Read(affix, "tTalentData");
        if ((ReadNullableInt(talent, "id") is > 0 and var talentId && profile.TalentIds.Contains(talentId))
            || (ReadNullableInt(talent, "skillId") is > 0 and var skillId && profile.SkillIds.Contains(skillId))
            || (ReadNullableInt(talent, "masteryId") is > 0 and var masteryId && profile.MasteryIds.Contains(masteryId))) matches++;
        var definition = Read(affix, "tAffixData");
        if ((ReadNullableInt(definition, "effectType") ?? 0) == 4)
        {
            var parameters = ReadSequence(Read(definition, "effectParam")).Select(ToInt).ToHashSet();
            if (parameters.Overlaps(profile.SkillIds) || parameters.Overlaps(profile.SkillInfoIds) || parameters.Overlaps(profile.TalentIds) || parameters.Overlaps(profile.MasteryIds)) matches++;
        }
        return Math.Clamp(matches, -2, 3);
    }

    private static object? GetEquippedItem(object hero, int part, bool main)
    {
        var partType = CreateEnum("EEquipPart", part);
        return partType is null ? null : InvokeInstance(hero, "GetEquipByPart", partType, main);
    }

    private static bool TryMoveCandidateToBag(ItemSearchRecord record, object? seasonData)
    {
        if (record.StorageSource is StorageSource.Inventory or StorageSource.Equipped) return true;
        if (record.StorageSource == StorageSource.Warehouse)
        {
            InvokeStatic("ItemSys", "QuickMoveItemFromStoreToBag", record.ItemData);
            return record.SourceField is null || Read(record.SourceField, "itemData") is null;
        }
        var houseStoreData = ReadValues(Read(Read(seasonData, "townData"), "houseDic"))
            .Select(house => Read(house, "houseStoreData")).FirstOrDefault(store => Read(store, "storeTreaData") is not null);
        var treasure = Read(houseStoreData, "storeTreaData");
        return treasure is not null && record.GroupData is not null && ReadBool(InvokeInstance(treasure, "TryTakeEquip", record.GroupData, record.ItemData));
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
        return Equals(Read(left, "Pointer"), Read(right, "Pointer"));
    }

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
