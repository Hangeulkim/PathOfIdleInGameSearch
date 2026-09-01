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
    public const string PluginVersion = "1.0.0";

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
            new Harmony(PluginGuid).Patch(target, prefix: new HarmonyMethod(prefix));
            Logger.LogInfo("Localized mouse-wheel input guard installed.");
        }
        catch (Exception error)
        {
            Logger.LogWarning($"Mouse-wheel input guard unavailable: {error.Message}");
        }
    }
}

internal static class WheelInputPatch
{
    public static bool Prefix(ref float __result)
    {
        if (!InGameSearchOverlay.ShouldBlockWheel) return true;
        __result = 0f;
        return false;
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
    private const int ResultsPerPage = 5;
    private static readonly float[] SpeedSteps = { 0.5f, 1f, 2f, 3f, 5f, 10f, 20f, 50f, 100f };
    private readonly List<ItemSearchRecord> allItems = new();
    private readonly List<ItemSearchRecord> matches = new();
    private ItemSearchRecord? hoveredItem;
    private Rect windowRect;
    private StorageKind selectedStorage = StorageKind.Inventory;
    private int currentPage;
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
    private int equipmentBoxCount;
    private int runeBoxCount;
    private IMECompositionMode previousImeMode;
    private bool imeModeSaved;
    private GameObject? inputBlockerCanvasObject;
    private GameObject? inputBlockerRegionObject;
    private RectTransform? inputBlockerRect;

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

    internal static bool ShouldBlockWheel { get; private set; }

    public InGameSearchOverlay(IntPtr pointer) : base(pointer) { }

    public void Start()
    {
        query = Plugin.SavedQuery.Value ?? string.Empty;
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
            ShouldBlockWheel = false;
            return;
        }
        var mouse = Input.mousePosition;
        ShouldBlockWheel = windowRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
        UpdateInputBlocker();
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            SetVisible(false);
            return;
        }

        if (Time.unscaledTime >= nextRefreshAt)
        {
            nextRefreshAt = Time.unscaledTime + 1f;
            RefreshItems();
        }
    }

    public void OnGUI()
    {
        if (!visible) return;
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
        GUI.Label(new Rect(left, windowRect.y + 12f, width - 382f, 30f), UiText.L("Path of Idle · 아이템 검색", "Path of Idle · Item Search", "Path of Idle · 物品搜索", "Path of Idle · 物品搜尋"), titleStyle!);
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
        GUI.Label(new Rect(left, windowRect.y + 43f, width, 22f), UiText.L(
            "F3 / Ctrl+F 열기·닫기   ·   공백: AND   |: OR   -단어: 제외",
            "F3 / Ctrl+F open·close   ·   space: AND   |: OR   -word: exclude",
            "F3 / Ctrl+F 打开·关闭   ·   空格: AND   |: OR   -词: 排除",
            "F3 / Ctrl+F 開啟·關閉   ·   空格: AND   |: OR   -詞: 排除"), hintStyle!);

        var searchRect = new Rect(left, windowRect.y + 70f, width, 38f);
        HandleSearchInput(searchRect);
        GUI.Box(searchRect, GUIContent.none, searchStyle!);
        var composition = Input.compositionString ?? string.Empty;
        var searchDisplay = string.IsNullOrEmpty(query) && string.IsNullOrEmpty(composition)
            ? UiText.L("아이템 이름, 등급, 부위, 옵션 검색…", "Search name, quality, slot, or affix…", "搜索名称、品质、部位或词缀…", "搜尋名稱、品質、部位或詞綴…")
            : query + composition + (focusSearch ? "|" : string.Empty);
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
            Plugin.SavedQuery.Value = query;
            focusSearch = true;
            currentPage = 0;
            ApplyFilter();
        }
        GUI.Label(new Rect(left + 372f, windowRect.y + 117f, width - 372f, 24f), status, hintStyle!);

        DrawQualityFilters(new Rect(left, windowRect.y + 150f, width, 34f));
        DrawBulkOpenPanel(new Rect(left, windowRect.y + 190f, width, 92f));

        var inventoryCount = 0;
        var warehouseCount = 0;
        var selectedMatches = new List<ItemSearchRecord>();
        foreach (var item in matches)
        {
            if (item.StorageKind == StorageKind.Inventory) inventoryCount++; else warehouseCount++;
            if (item.StorageKind == selectedStorage) selectedMatches.Add(item);
        }
        DrawStorageTab(new Rect(left, windowRect.y + 290f, 190f, 32f), StorageKind.Inventory, $"{UiText.L("인벤토리", "INVENTORY", "背包", "背包")}  {inventoryCount}", new Color(0.32f, 0.86f, 0.46f));
        DrawStorageTab(new Rect(left + 198f, windowRect.y + 290f, 190f, 32f), StorageKind.Warehouse, $"{UiText.L("창고", "WAREHOUSE", "仓库", "倉庫")}  {warehouseCount}", new Color(0.25f, 0.78f, 0.92f));

        var pageCount = Math.Max(1, (int)Math.Ceiling(selectedMatches.Count / (double)ResultsPerPage));
        currentPage = Math.Max(0, Math.Min(currentPage, pageCount - 1));
        var resultArea = new Rect(left, windowRect.y + 332f, width, 378f);
        var currentEvent = Event.current;
        if (currentEvent.type == EventType.ScrollWheel && resultArea.Contains(currentEvent.mousePosition))
        {
            currentPage = Math.Max(0, Math.Min(pageCount - 1, currentPage + (currentEvent.delta.y > 0f ? 1 : -1)));
            currentEvent.Use();
        }

        if (selectedMatches.Count == 0)
        {
            GUI.Label(new Rect(left + 12f, windowRect.y + 364f, width - 24f, 40f), allItems.Count == 0
                ? UiText.L("인벤토리 데이터를 기다리는 중입니다.", "Waiting for inventory data.", "正在等待背包数据。", "正在等待背包資料。")
                : UiText.L("이 구역에는 검색 조건에 맞는 아이템이 없습니다.", "No matching items in this section.", "此区域没有匹配的物品。", "此區域沒有相符的物品。"), hintStyle!);
        }
        else
        {
            var pageItems = selectedMatches.Skip(currentPage * ResultsPerPage).Take(ResultsPerPage).ToList();
            for (var index = 0; index < pageItems.Count; index++)
                DrawResult(pageItems[index], new Rect(left, windowRect.y + 332f + index * 76f, width, 70f));
        }

        if (GUI.Button(new Rect(left, windowRect.yMax - 43f, 88f, 28f), UiText.L("◀ 이전", "◀ Previous", "◀ 上一页", "◀ 上一頁"), compactButtonStyle!) && currentPage > 0) currentPage--;
        GUI.Label(new Rect(left + 94f, windowRect.yMax - 43f, 90f, 28f), $"{currentPage + 1} / {pageCount}", pageStyle!);
        if (GUI.Button(new Rect(left + 190f, windowRect.yMax - 43f, 88f, 28f), UiText.L("다음 ▶", "Next ▶", "下一页 ▶", "下一頁 ▶"), compactButtonStyle!) && currentPage + 1 < pageCount) currentPage++;
        GUI.Label(new Rect(left + 294f, windowRect.yMax - 40f, width - 294f, 24f), UiText.L("검색어가 있으면 일치한 아이템만 표시됩니다.", "A search shows matching items only.", "输入搜索词后仅显示匹配物品。", "輸入搜尋詞後僅顯示相符物品。"), hintStyle!);

        if (hoveredItem is not null) DrawItemTooltip(hoveredItem);

    }

    public void OnDestroy()
    {
        SaveWindowPosition();
        RestoreImeMode();
        DestroyInputBlocker();
        ShouldBlockWheel = false;
        Time.timeScale = 1f;
    }

    private void SetVisible(bool value)
    {
        if (visible == value) return;
        visible = value;
        if (!visible) ShouldBlockWheel = false;
        Plugin.Logger.LogInfo($"Search panel visible={visible}.");
        dragging = false;
        if (visible)
        {
            if (!imeModeSaved)
            {
                previousImeMode = Input.imeCompositionMode;
                imeModeSaved = true;
            }
            Input.imeCompositionMode = IMECompositionMode.On;
            focusSearch = true;
            nextRefreshAt = 0f;
            UpdateInputBlocker();
            RefreshItems();
        }
        else
        {
            SaveWindowPosition();
            RestoreImeMode();
            SetInputBlockerActive(false);
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
        RefreshItems();
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
            : $"\n\n{UiText.L("세트", "Set", "套装", "套裝")} · {item.SetName}\n{UiText.L("구성 장비", "Set pieces", "套装部件", "套裝部件")}\n{item.SetMembers}\n\n{UiText.L("세트 효과", "Set bonuses", "套装效果", "套裝效果")}\n{item.SetBonuses}";
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

        GUI.Label(new Rect(rect.x + 12f, rect.y + 3f, 100f, 19f), UiText.L("일괄 개봉", "Bulk Open", "批量开启", "批次開啟"), utilityTitleStyle!);
        var nextSkip = GUI.Toggle(new Rect(rect.x + 10f, rect.y + 22f, 106f, 19f), Plugin.SkipBulkConfirmation.Value, UiText.L(" 확인 생략", " Skip confirm", " 跳过确认", " 略過確認"), toggleStyle!);
        if (nextSkip != Plugin.SkipBulkConfirmation.Value)
        {
            Plugin.SkipBulkConfirmation.Value = nextSkip;
            armedBulkOpen = BulkToolKind.None;
            status = nextSkip
                ? UiText.L("일괄 개봉 확인을 생략합니다.", "Bulk opening will run with one click.", "批量开启将单击执行。", "批次開啟將單擊執行。")
                : UiText.L("일괄 개봉은 두 번 눌러야 실행됩니다.", "Bulk opening requires two clicks.", "批量开启需要点击两次。", "批次開啟需要點擊兩次。");
        }
        var nextAutoStore = GUI.Toggle(new Rect(rect.x + 10f, rect.y + 41f, 112f, 19f), Plugin.AutoStoreOpenedEquipment.Value, UiText.L(" 자동 창고", " Auto storage", " 自动入库", " 自動入庫"), toggleStyle!);
        if (nextAutoStore != Plugin.AutoStoreOpenedEquipment.Value)
        {
            Plugin.AutoStoreOpenedEquipment.Value = nextAutoStore;
            status = nextAutoStore
                ? UiText.L("개봉 장비를 게임 규칙에 따라 자동 보관합니다.", "Opened gear follows the game's automatic storage rules.", "开启的装备将按游戏规则自动入库。", "開啟的裝備將按遊戲規則自動入庫。")
                : UiText.L("개봉 장비를 인벤토리에 남깁니다.", "Opened gear stays in the inventory.", "开启的装备将留在背包。", "開啟的裝備將留在背包。");
        }
        DrawBulkOpenButton(new Rect(rect.x + 124f, rect.y + 14f, 259f, 36f), BulkToolKind.EquipmentBox, equipmentBoxCount, UiText.L("장비 상자", "Gear boxes", "装备箱", "裝備箱"));
        DrawBulkOpenButton(new Rect(rect.x + 391f, rect.y + 14f, 265f, 36f), BulkToolKind.RuneBox, runeBoxCount, UiText.L("룬 상자", "Rune boxes", "符文箱", "符文箱"));

        GUI.Label(new Rect(rect.x + 12f, rect.y + 65f, 74f, 20f), UiText.L("개봉 Tier", "Open Tier", "开启品质", "開啟品質"), utilityTitleStyle!);
        var x = rect.x + 88f;
        DrawBulkQualityButton(new Rect(x, rect.y + 60f, 46f, 27f), 0, UiText.L("전체", "All", "全部", "全部")); x += 49f;
        DrawBulkQualityButton(new Rect(x, rect.y + 60f, 46f, 27f), 3, UiText.L("희귀", "Rare", "稀有", "稀有")); x += 49f;
        DrawBulkQualityButton(new Rect(x, rect.y + 60f, 46f, 27f), 4, UiText.L("전설", "Legend", "传奇", "傳奇")); x += 49f;
        DrawBulkQualityButton(new Rect(x, rect.y + 60f, 46f, 27f), 5, UiText.L("신화", "Mythic", "神话", "神話")); x += 49f;
        DrawBulkQualityButton(new Rect(x, rect.y + 60f, 46f, 27f), 6, UiText.L("세트", "Set", "套装", "套裝")); x += 49f;
        DrawBulkQualityButton(new Rect(x, rect.y + 60f, 46f, 27f), 8, UiText.L("고유", "Unique", "独特", "獨特")); x += 49f;
        DrawBulkQualityButton(new Rect(x, rect.y + 60f, 46f, 27f), -1, UiText.L("기타", "Other", "其他", "其他"));
        var nextAtLeast = GUI.Toggle(new Rect(rect.xMax - 102f, rect.y + 64f, 96f, 22f), Plugin.BulkQualityAtLeast.Value, UiText.L(" 이상", " Or higher", " 及以上", " 以上"), toggleStyle!);
        if (nextAtLeast != Plugin.BulkQualityAtLeast.Value)
        {
            Plugin.BulkQualityAtLeast.Value = nextAtLeast;
            armedBulkOpen = BulkToolKind.None;
            RefreshItems();
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
        if (GUI.Button(rect, label, compactButtonStyle!))
        {
            Plugin.BulkQualityMask.Value = quality == 0 ? 0 : mask ^ QualityFilterLogic.BitFor(quality);
            armedBulkOpen = BulkToolKind.None;
            RefreshItems();
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
        RefreshItems();
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
            if (query.Length > 0)
            {
                query = query[..^1];
                changed = true;
            }
        }
        else if (controlDown && current.keyCode == KeyCode.V)
        {
            var clipboard = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(clipboard))
            {
                query = (query + clipboard).Length > 200 ? (query + clipboard)[..200] : query + clipboard;
                changed = true;
            }
        }
        else if (controlDown && current.keyCode == KeyCode.A)
        {
            query = string.Empty;
            changed = true;
        }
        else if (!controlDown && current.character >= ' ' && current.character != '\u007f' && query.Length < 200)
        {
            query += current.character;
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
    Treasure
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

internal static class GameInventoryReader
{
    private static readonly Regex RichText = new("<[^>]+>", RegexOptions.Compiled);
    private static Assembly? gameAssembly;
    private static bool languageTableLogged;

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
                var quality = ReadNullableInt(save, "quality") ?? ReadNullableInt(definition, "quality") ?? 0;
                if (item is null || kind == BulkToolKind.None || count <= 0) continue;

                var key = NativeObjectKey(item, field);
                if (!seen.Add(key)) continue;
                result.Add(new BulkToolStack(key, kind, item, count, quality));
            }
        }
    }

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

        var affixObjects = ReadList(Read(equip, "affixList")).ToList();
        if (affixObjects.Count == 0) affixObjects = ReadList(Read(save, "affixList")).ToList();
        var affixes = affixObjects.Select(DescribeAffix).Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var affixSummary = string.Join("  ·  ", affixes);
        var setData = Read(equip, "tEquipSetsData");
        var setId = ReadNullableInt(setData, "id") ?? 0;
        var localizedSetName = Clean(ReadString(setData, "name") ?? string.Empty);
        var englishSetName = Clean(EnglishName(setData, localizedSetName) ?? localizedSetName);
        var setName = FirstNonEmpty(localizedSetName, englishSetName);
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
            setName, englishSetName, string.Join(" ", setMembers), string.Join(" ", englishSetMembers), string.Join(" ", setBonuses)
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

    private static string DescribeAffix(object affix)
    {
        var save = Read(affix, "saveData") ?? affix;
        var id = ReadNullableInt(save, "id") ?? ReadNullableInt(affix, "id");
        var definition = Read(affix, "tAffixData") ?? (id is > 0 ? InvokeStatic("TableData", "getTAffixData", id.Value) : null);
        var display = Clean(InvokeString(affix, "GetDesc") ?? string.Empty);
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
        var count = ReadNullableInt(list, "Count") ?? 0;
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
