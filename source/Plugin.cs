using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "local.pathofidle.ingame-search";
    public const string PluginName = "Path of Idle In-Game Search";
    public const string PluginVersion = "1.1.9";

#if PATHOFIDLE_DIAGNOSTICS
    private static ManualLogSource DiagnosticsLogger { get; set; } = null!;
#endif
    internal static ConfigEntry<string> SavedQuery { get; private set; } = null!;
    internal static ConfigEntry<bool> IncludeWarehouse { get; private set; } = null!;
    internal static ConfigEntry<float> WindowX { get; private set; } = null!;
    internal static ConfigEntry<float> WindowY { get; private set; } = null!;
    internal static ConfigEntry<float> WindowWidth { get; private set; } = null!;
    internal static ConfigEntry<float> WindowHeight { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowDetailedStatus { get; private set; } = null!;
    internal static ConfigEntry<bool> StartInBackground { get; private set; } = null!;
    internal static ConfigEntry<bool> ActivateFromBackgroundHotkeys { get; private set; } = null!;
    internal static ConfigEntry<bool> OpenOnStart { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoStart { get; private set; } = null!;
    internal static ConfigEntry<float> GameSpeed { get; private set; } = null!;
    internal static ConfigEntry<bool> SkipBulkConfirmation { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoStoreOpenedEquipment { get; private set; } = null!;
    internal static ConfigEntry<BulkEquipmentDestination> OpenedEquipmentDestination { get; private set; } = null!;
    internal static ConfigEntry<int> BulkQualityMask { get; private set; } = null!;
    internal static ConfigEntry<bool> BulkQualityAtLeast { get; private set; } = null!;
    internal static ConfigEntry<int> QualityFilter { get; private set; } = null!;
    internal static ConfigEntry<int> QualityMask { get; private set; } = null!;
    internal static ConfigEntry<bool> SearchOptionsOnly { get; private set; } = null!;
    internal static ConfigEntry<string> UiLanguage { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoBuildIncludeStorage { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoBuildIncludeForgeBonuses { get; private set; } = null!;
    internal static ConfigEntry<string> AutoBuildTheme { get; private set; } = null!;
    internal static ConfigEntry<string> AutoBuildHeroThemes { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoTransformSkills { get; private set; } = null!;
    internal static ConfigEntry<int> AutoTransformMaxAttempts { get; private set; } = null!;
    internal static ConfigEntry<int> AutoBuildPlanningSeconds { get; private set; } = null!;
    internal static ConfigEntry<int> AutoBuildThemePriority { get; private set; } = null!;

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
        ShowDetailedStatus = Config.Bind(
            "Window", "ShowDetailedStatus", false,
            "Show verbose operation results in the Bulk Open, Auto Build, and Forge pages. Running progress and safety messages remain visible.");
        StartInBackground = Config.Bind(
            "Window", "StartInBackground", false,
            "After the plugin loads, place Path of Idle behind other windows without suspending Unity updates. Applies on the next launch and is opt-in for distributed builds.");
        ActivateFromBackgroundHotkeys = Config.Bind(
            "Window", "ActivateFromBackgroundHotkeys", false,
            "Register system-wide F3 and Ctrl+F while the game runs. Either shortcut brings Path of Idle forward and toggles the panel. This captures those shortcuts from other apps, so it is off by default.");
        OpenOnStart = Config.Bind("Window", "OpenOnStart", false, "Open the search panel when the game starts.");
        AutoStart = Config.Bind("Startup", "AutoStart", false, "Press Continue Game once and enable Auto for eligible active parties after startup.");
        GameSpeed = Config.Bind("Speed", "Multiplier", 1f, "Runtime speed: 0.1 through 100.");
        SkipBulkConfirmation = Config.Bind("BulkOpen", "SkipConfirmation", false, "Open all boxes immediately without a second confirmation click.");
        AutoStoreOpenedEquipment = Config.Bind("BulkOpen", "AutoStoreEquipment", true, "Move equipment received from bulk-opened boxes through the game's automatic warehouse routing.");
        // If the three-state key does not exist yet, its default is derived from
        // the old bool so existing Inventory/Warehouse behavior is preserved.
        OpenedEquipmentDestination = Config.Bind("BulkOpen", "EquipmentDestination",
            AutoStoreOpenedEquipment.Value ? BulkEquipmentDestination.Warehouse : BulkEquipmentDestination.Inventory,
            "Equipment-box reward handling: Inventory, Warehouse/Vault, or keep Vault-eligible gear and immediately sell only the rest.");
        if (!Enum.IsDefined(typeof(BulkEquipmentDestination), OpenedEquipmentDestination.Value))
            OpenedEquipmentDestination.Value = BulkEquipmentDestination.Inventory;
        // Keep the legacy key meaningful for users who later downgrade.
        AutoStoreOpenedEquipment.Value = OpenedEquipmentDestination.Value == BulkEquipmentDestination.Warehouse;
        BulkQualityMask = Config.Bind("BulkOpen", "QualityMask", 0, "Bit mask of box and rune-box qualities. Zero means all.");
        BulkQualityAtLeast = Config.Bind("BulkOpen", "QualityAtLeast", false, "Open the selected quality and every higher quality.");
        QualityFilter = Config.Bind("Search", "QualityFilter", 0, "Equipment quality filter: 0 all, -1 other, or a game quality id.");
        QualityMask = Config.Bind("Search", "QualityMask", 0, "Bit mask of equipment qualities. Zero means all.");
        SearchOptionsOnly = Config.Bind("Search", "OptionsOnly", false, "Search affixes and set bonuses only.");
        UiLanguage = Config.Bind("Window", "Language", "auto", "UI language: auto, ko, en, zh-cn, or zh-tw.");
        AutoBuildIncludeStorage = Config.Bind("AutoBuild", "IncludeStorage", true, "Allow automatic gear selection to use warehouse and Vault equipment.");
        AutoBuildIncludeForgeBonuses = Config.Bind("AutoBuild", "IncludeForgeBonuses", true, "Compare equipment with its current forge bonuses. Disable to compare every item at forge level zero.");
        AutoBuildTheme = Config.Bind("AutoBuild", "Theme", "auto", "Build theme used by automatic gear and skill optimization.");
        AutoBuildHeroThemes = Config.Bind("AutoBuild", "HeroThemes", string.Empty, "Per-hero build themes keyed by the save hero unique ID.");
        AutoTransformSkills = Config.Bind("AutoBuild", "TransformMissingSkills", true, "Use the shrine's normal paid skill transformation to seek missing performance-plan skills before allocating points.");
        AutoTransformMaxAttempts = Config.Bind("AutoBuild", "MaxSkillTransformAttempts", 12, "Maximum paid skill transformations per automatic skill run.");
        AutoBuildPlanningSeconds = Config.Bind("AutoBuild", "PlanningSeconds", 60, "Overall best-candidate search time in seconds. Use 0 for unlimited; finite values have a minimum of 10 seconds.");
        AutoBuildPlanningSeconds.Value = NormalizeAutoBuildPlanningSeconds(AutoBuildPlanningSeconds.Value);
        AutoBuildThemePriority = Config.Bind("AutoBuild", "ThemePriority", 50, "Selected-theme priority from 0 (general performance) through 100 (theme-specific performance). 50 preserves the balanced default.");
        AutoBuildThemePriority.Value = NormalizeAutoBuildThemePriority(AutoBuildThemePriority.Value);
        Application.runInBackground = true;
        if (StartInBackground.Value) QueueBackgroundWindowPlacement();
        _ = ConfigureBackgroundActivationHotkeys(
            ActivateFromBackgroundHotkeys.Value,
            out _,
            out _);
        AddComponent<InGameSearchOverlay>();
        // Register the overlay before Harmony resolves this assembly's managed
        // wheel-prefix methods. Overlay helpers with plugin-only parameter types
        // are hidden from IL2CPP so registration stays limited to Unity callbacks.
        InstallWheelPatch();
#if PATHOFIDLE_RUNTIME_TEST
        // Runtime verification is intentionally allowed to continue while the
        // game window is hidden so automated checks do not steal user focus.
        Application.runInBackground = true;
        AddComponent<RuntimeJointBuildTestHook>();
        AddComponent<RuntimePartyReportHook>();
        AddComponent<RuntimeWarehouseSaleHook>();
        AddComponent<RuntimeBulkRoutingTestHook>();
        AddComponent<RuntimeWindowToggleTestHook>();
        AddComponent<RuntimeForgeOptimizationPreviewHook>();
        AddComponent<RuntimeMythicRefinePreviewHook>();
        AddComponent<RuntimeAutoBuildPreviewHook>();
#endif
        DiagInfo($"{PluginName} {PluginVersion} loaded. Press F3 or Ctrl+F to open it.");
    }

    internal static int NormalizeAutoBuildPlanningSeconds(int seconds)
        => seconds <= 0 ? 0 : Math.Max(10, seconds);

    internal static int NormalizeAutoBuildThemePriority(int priority)
        => Math.Clamp(priority, 0, 100);

    private static readonly IntPtr HwndBottom = new(1);
    private const int SwShowNoActivate = 4;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const int SwRestore = 9;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF3 = 0x72;
    private const uint VkF = 0x46;
    private const int BackgroundF3HotkeyId = 0x504F4901;
    private const int BackgroundCtrlFHotkeyId = 0x504F4902;
    private static readonly object BackgroundActivationHotkeySync = new();
    private static Thread? backgroundActivationHotkeyThread;
    private static ManualResetEventSlim? backgroundActivationHotkeyReady;
    private static uint backgroundActivationHotkeyThreadId;
    private static bool backgroundF3HotkeyRegistered;
    private static bool backgroundCtrlFHotkeyRegistered;
    private static bool backgroundActivationHotkeyStopRequested;
    private static int backgroundActivationToggleRequests;
#if PATHOFIDLE_RUNTIME_TEST
    private static readonly object RuntimeWindowScopeSync = new();
    private static int runtimeWindowScopeDepth;
#endif

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr window,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message,
        IntPtr window,
        uint filterMinimum,
        uint filterMaximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal static bool ConfigureBackgroundActivationHotkeys(
        bool enabled,
        out bool f3Registered,
        out bool ctrlFRegistered)
    {
        if (ActivateFromBackgroundHotkeys is not null)
            ActivateFromBackgroundHotkeys.Value = enabled;

        if (enabled) StartBackgroundActivationHotkeyThread();
        else StopBackgroundActivationHotkeyThread();

        lock (BackgroundActivationHotkeySync)
        {
            f3Registered = backgroundF3HotkeyRegistered;
            ctrlFRegistered = backgroundCtrlFHotkeyRegistered;
            return !enabled || f3Registered || ctrlFRegistered;
        }
    }

    internal static bool TryConsumeBackgroundActivationToggle()
        => (Interlocked.Exchange(ref backgroundActivationToggleRequests, 0) & 1) != 0;

    internal static void GetBackgroundActivationHotkeyRegistration(
        out bool f3Registered,
        out bool ctrlFRegistered)
    {
        lock (BackgroundActivationHotkeySync)
        {
            f3Registered = backgroundF3HotkeyRegistered;
            ctrlFRegistered = backgroundCtrlFHotkeyRegistered;
        }
    }

    internal static void StopBackgroundActivationHotkeys()
        => StopBackgroundActivationHotkeyThread();

    private static void StartBackgroundActivationHotkeyThread()
    {
        ManualResetEventSlim ready;
        ManualResetEventSlim? previousReady;
        lock (BackgroundActivationHotkeySync)
        {
            if (backgroundActivationHotkeyThread?.IsAlive == true) return;
            previousReady = backgroundActivationHotkeyReady;
            backgroundActivationHotkeyStopRequested = false;
            backgroundF3HotkeyRegistered = false;
            backgroundCtrlFHotkeyRegistered = false;
            ready = new ManualResetEventSlim(false);
            backgroundActivationHotkeyReady = ready;
            backgroundActivationHotkeyThread = new Thread(BackgroundActivationHotkeyLoop)
            {
                IsBackground = true,
                Name = "PathOfIdle background activation hotkeys"
            };
            backgroundActivationHotkeyThread.Start();
        }
        previousReady?.Dispose();

        // Registration is normally immediate. A short bounded wait lets the UI
        // report conflicts without ever holding the Unity thread indefinitely.
        _ = ready.Wait(1000);
    }

    private static void StopBackgroundActivationHotkeyThread()
    {
        Thread? thread;
        uint threadId;
        ManualResetEventSlim? ready;
        lock (BackgroundActivationHotkeySync)
        {
            backgroundActivationHotkeyStopRequested = true;
            thread = backgroundActivationHotkeyThread;
            threadId = backgroundActivationHotkeyThreadId;
            ready = backgroundActivationHotkeyReady;
        }

        if (thread is null)
        {
            lock (BackgroundActivationHotkeySync)
            {
                if (ReferenceEquals(backgroundActivationHotkeyReady, ready))
                    backgroundActivationHotkeyReady = null;
            }
            ready?.Dispose();
            return;
        }
        if (threadId != 0)
            _ = PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        var stopped = ReferenceEquals(thread, Thread.CurrentThread) || thread.Join(1000);

        lock (BackgroundActivationHotkeySync)
        {
            if (stopped)
            {
                if (ReferenceEquals(backgroundActivationHotkeyThread, thread))
                    backgroundActivationHotkeyThread = null;
                backgroundActivationHotkeyThreadId = 0;
                backgroundF3HotkeyRegistered = false;
                backgroundCtrlFHotkeyRegistered = false;
                if (ReferenceEquals(backgroundActivationHotkeyReady, ready))
                    backgroundActivationHotkeyReady = null;
            }
        }
        if (stopped) ready?.Dispose();
    }

    private static void BackgroundActivationHotkeyLoop()
    {
        var currentThread = Thread.CurrentThread;
        var threadId = GetCurrentThreadId();
        lock (BackgroundActivationHotkeySync)
        {
            if (!ReferenceEquals(backgroundActivationHotkeyThread, currentThread)
                || backgroundActivationHotkeyStopRequested)
            {
                backgroundActivationHotkeyReady?.Set();
                return;
            }
            backgroundActivationHotkeyThreadId = threadId;
        }

        var f3Registered = false;
        var ctrlFRegistered = false;
        try
        {
            f3Registered = RegisterHotKey(
                IntPtr.Zero,
                BackgroundF3HotkeyId,
                ModNoRepeat,
                VkF3);
            var f3Error = f3Registered ? 0 : Marshal.GetLastWin32Error();
            ctrlFRegistered = RegisterHotKey(
                IntPtr.Zero,
                BackgroundCtrlFHotkeyId,
                ModControl | ModNoRepeat,
                VkF);
            var ctrlFError = ctrlFRegistered ? 0 : Marshal.GetLastWin32Error();
            lock (BackgroundActivationHotkeySync)
            {
                backgroundF3HotkeyRegistered = f3Registered;
                backgroundCtrlFHotkeyRegistered = ctrlFRegistered;
                backgroundActivationHotkeyReady?.Set();
            }
            DiagInfo(
                $"Background activation hotkeys: F3={f3Registered} ({f3Error}), " +
                $"Ctrl+F={ctrlFRegistered} ({ctrlFError}).");

            while (!Volatile.Read(ref backgroundActivationHotkeyStopRequested)
                   && (f3Registered || ctrlFRegistered))
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0) break;
                if (message.Message != WmHotkey) continue;
                var id = unchecked((int)message.WParam.ToUInt64());
                if ((id == BackgroundF3HotkeyId && f3Registered)
                    || (id == BackgroundCtrlFHotkeyId && ctrlFRegistered))
                    QueueBackgroundActivationToggle();
            }
        }
        catch (Exception error)
        {
            DiagWarning($"Background activation hotkeys stopped: {error.Message}");
        }
        finally
        {
            if (f3Registered) _ = UnregisterHotKey(IntPtr.Zero, BackgroundF3HotkeyId);
            if (ctrlFRegistered) _ = UnregisterHotKey(IntPtr.Zero, BackgroundCtrlFHotkeyId);
            lock (BackgroundActivationHotkeySync)
            {
                backgroundActivationHotkeyReady?.Set();
                backgroundF3HotkeyRegistered = false;
                backgroundCtrlFHotkeyRegistered = false;
                backgroundActivationHotkeyThreadId = 0;
                if (ReferenceEquals(backgroundActivationHotkeyThread, currentThread))
                    backgroundActivationHotkeyThread = null;
            }
        }
    }

    private static void QueueBackgroundActivationToggle()
    {
        _ = Interlocked.Increment(ref backgroundActivationToggleRequests);
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero) return;
            if (IsIconic(window)) _ = ShowWindowAsync(window, SwRestore);
            _ = SetForegroundWindow(window);
        }
        catch (Exception error)
        {
            DiagWarning($"Could not activate the game window: {error.Message}");
        }
    }

#if PATHOFIDLE_RUNTIME_TEST
    internal static void BeginRuntimeNoActivateWindowScope()
    {
        lock (RuntimeWindowScopeSync)
        {
            runtimeWindowScopeDepth++;
            MaintainRuntimeNoActivateWindowScopeLocked();
        }
    }

    internal static void MaintainRuntimeNoActivateWindowScope()
    {
        lock (RuntimeWindowScopeSync)
        {
            if (runtimeWindowScopeDepth > 0)
                MaintainRuntimeNoActivateWindowScopeLocked();
        }
    }

    internal static void EndRuntimeNoActivateWindowScope()
    {
        lock (RuntimeWindowScopeSync)
        {
            if (runtimeWindowScopeDepth <= 0) return;
            runtimeWindowScopeDepth--;
        }
    }

    private static void MaintainRuntimeNoActivateWindowScopeLocked()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var window = process.MainWindowHandle;
        if (window == IntPtr.Zero) return;
        // Diagnostic runs may keep an inactive window behind the user's work,
        // but they must never make it impossible to activate. Once the user
        // clicks or invokes a global hotkey, foreground ownership is preserved.
        if (GetForegroundWindow() == window || IsIconic(window)) return;
        _ = SetWindowPos(
            window,
            HwndBottom,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }
#endif

    private static void QueueBackgroundWindowPlacement()
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Unity's top-level window can appear a little after the plugin
                // loads. Poll for a bounded five seconds, then keep it demoted for
                // a short bounded startup window because Unity/Steam can restore
                // it once more while the first scene is loading. No input is
                // synthesized and the window remains available from the taskbar.
                using var process = Process.GetCurrentProcess();
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    process.Refresh();
                    var window = process.MainWindowHandle;
                    if (window != IntPtr.Zero)
                    {
                        for (var settleAttempt = 0; settleAttempt < 100; settleAttempt++)
                        {
                            _ = SetWindowPos(
                                window,
                                HwndBottom,
                                0,
                                0,
                                0,
                                0,
                                SwpNoMove | SwpNoSize | SwpNoActivate);
                            _ = ShowWindowAsync(window, SwShowNoActivate);
                            Thread.Sleep(50);
                        }
                        return;
                    }
                    Thread.Sleep(50);
                }
            }
            catch
            {
                // Window placement is best-effort and must never block startup.
            }
        });
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
        var harmony = new Harmony(PluginGuid);
        var installed = 0;
        void PatchPrefix(MethodBase? target, MethodInfo? prefix, string label)
        {
            try
            {
                if (target is null || prefix is null) throw new MissingMethodException(label);
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                installed++;
            }
            catch (Exception error)
            {
                // Every guard is independent. A renamed optional game method must
                // not prevent the remaining wheel paths from being protected.
                DiagWarning($"Mouse-wheel guard '{label}' unavailable: {error.Message}");
            }
        }

        var gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
        PatchPrefix(
            gameAssembly?.GetType("ScrollRectCustomWheel")?.GetMethod(
                "ReadNormalizedWheelDelta", BindingFlags.Public | BindingFlags.Static),
            typeof(WheelInputPatch).GetMethod(nameof(WheelInputPatch.Prefix), BindingFlags.Public | BindingFlags.Static),
            "ScrollRectCustomWheel.ReadNormalizedWheelDelta");
        PatchPrefix(
            gameAssembly?.GetType("LordMod")?.GetMethod(
                "IsMouseOverBlockHeroWheelScrollArea",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance),
            typeof(WheelInputPatch).GetMethod(
                nameof(WheelInputPatch.HeroWheelBlockAreaPrefix), BindingFlags.Public | BindingFlags.Static),
            "LordMod.IsMouseOverBlockHeroWheelScrollArea");

        var axisPrefix = typeof(WheelInputPatch).GetMethod(
            nameof(WheelInputPatch.AxisPrefix), BindingFlags.Public | BindingFlags.Static);
        PatchPrefix(
            typeof(Input).GetMethod(nameof(Input.GetAxis), BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null),
            axisPrefix,
            "UnityEngine.Input.GetAxis");
        PatchPrefix(
            typeof(Input).GetMethod(nameof(Input.GetAxisRaw), BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null),
            axisPrefix,
            "UnityEngine.Input.GetAxisRaw");
        PatchPrefix(
            typeof(Input).GetProperty(nameof(Input.mouseScrollDelta), BindingFlags.Public | BindingFlags.Static)?.GetMethod,
            typeof(WheelInputPatch).GetMethod(
                nameof(WheelInputPatch.MouseScrollDeltaPrefix), BindingFlags.Public | BindingFlags.Static),
            "UnityEngine.Input.mouseScrollDelta");

        if (installed > 0) DiagInfo($"Installed {installed} focused mouse-wheel input guards.");
    }
}

#if PATHOFIDLE_RUNTIME_TEST
// Opt-in runtime test harness. This type and all of its file-system strings are
// absent from normal Release builds. It does not bind or modify any setting and
// emits exactly one small, overwrite-only result file instead of enabling logs.
public sealed class RuntimeJointBuildTestHook : MonoBehaviour
{
    private const string RequestPrefix = "APPLY JOINT BUILD v1.1.8";
    private const int DefaultTargetHeroUniqueId = 28130;
    private static readonly string RequestPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-runtime-test.request");
    private static readonly string ResultPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-runtime-test.result");

    private bool armed;
    private bool queued;
    private bool queueCancellationRequested;
    private float armedAt;
    private float queuedAt;
    private float nextProbeAt;
    private float lastResponsiveUpdateAt;
    private float maxResponsiveGapMilliseconds;
    private int responsiveUpdates;
    private string lastReadiness = "request not armed";
    private string requestedTheme = "auto";
    private int requestedHeroUniqueId = DefaultTargetHeroUniqueId;
    private bool requestedVerificationFailure;
    private bool selectionLeaseOwned;

    public RuntimeJointBuildTestHook(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (queued)
        {
            var now = Time.realtimeSinceStartup;
            if (lastResponsiveUpdateAt > 0f)
                maxResponsiveGapMilliseconds = Math.Max(
                    maxResponsiveGapMilliseconds,
                    (now - lastResponsiveUpdateAt) * 1000f);
            lastResponsiveUpdateAt = now;
            responsiveUpdates++;

            if (!queueCancellationRequested
                && Time.unscaledTime - queuedAt >= 90f
                && InGameSearchOverlay.TryCancelRuntimeJointBuildTestBeforeMutation(
                    out var cancellationReason))
            {
                queueCancellationRequested = true;
                lastReadiness = cancellationReason;
            }

            if (!InGameSearchOverlay.TryTakeRuntimeJointBuildTestResult(
                    out var result, out var elapsedMilliseconds, out var postState)) return;
            var resultMessage = queueCancellationRequested && !string.IsNullOrWhiteSpace(lastReadiness)
                ? $"{result.Message}; {lastReadiness}"
                : result.Message;
            WriteResult(
                result.Succeeded,
                elapsedMilliseconds,
                resultMessage,
                responsiveUpdates,
                maxResponsiveGapMilliseconds,
                postState);
            return;
        }

        if (Time.unscaledTime < nextProbeAt) return;
        nextProbeAt = Time.unscaledTime + 0.5f;

        if (!armed)
        {
            // Retry any previously failed restore before accepting another test
            // request. Release keeps failed lease references specifically so this
            // path (and teardown) can finish the exact cleanup later.
            if (selectionLeaseOwned
                && GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
            {
                GameInventoryReader.ReleaseRuntimeTestHeroSelection(out _);
                if (GameInventoryReader.RuntimeTestSelectionBlocksUserOperations) return;
                selectionLeaseOwned = false;
            }
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
            if (!request.StartsWith(RequestPrefix, StringComparison.Ordinal)) return;
            requestedVerificationFailure = request.Contains(
                "|verifyFailure=true", StringComparison.Ordinal);
            var themeMarker = "|theme=";
            var markerIndex = request.IndexOf(themeMarker, StringComparison.Ordinal);
            requestedTheme = markerIndex >= 0
                ? GameInventoryReader.NormalizeBuildTheme(request[(markerIndex + themeMarker.Length)..].Split('|')[0])
                : "auto";
            var heroMarker = "|hero=";
            var heroMarkerIndex = request.IndexOf(heroMarker, StringComparison.Ordinal);
            requestedHeroUniqueId = heroMarkerIndex >= 0
                && int.TryParse(request[(heroMarkerIndex + heroMarker.Length)..].Split('|')[0],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeroId)
                && parsedHeroId > 0
                    ? parsedHeroId
                    : DefaultTargetHeroUniqueId;

            armed = true;
            armedAt = Time.unscaledTime;
            queued = false;
            queueCancellationRequested = false;
            queuedAt = 0f;
            responsiveUpdates = 0;
            lastResponsiveUpdateAt = 0f;
            maxResponsiveGapMilliseconds = 0f;
            try { System.IO.File.Delete(RequestPath); } catch { }
            try { System.IO.File.Delete(ResultPath); } catch { }
        }

        // The availability probe and the queue operation both run on Unity's
        // main thread. Never inject the diagnostic hero while a user-owned Auto
        // Build or bulk-open session is already using the overlay.
        if (!InGameSearchOverlay.TryGetRuntimeJointBuildTestQueueAvailability(out lastReadiness))
        {
            if (Time.unscaledTime - armedAt < 90f) return;
            WriteResult(false, 0, $"queue availability timeout: {lastReadiness}", 0, 0f);
            return;
        }

        if (!GameInventoryReader.TryAcquireRuntimeTestHeroSelection(
                requestedHeroUniqueId, out lastReadiness, out var initialLoadPending))
        {
            _ = initialLoadPending;
            if (Time.unscaledTime - armedAt < 90f) return;
            WriteResult(false, 0, $"readiness timeout: {lastReadiness}", 0, 0f);
            return;
        }
        selectionLeaseOwned = true;

        if (!GameInventoryReader.TryGetRuntimeJointBuildTestReadiness(out lastReadiness))
        {
            if (Time.unscaledTime - armedAt < 90f) return;
            WriteResult(false, 0, $"readiness timeout: {lastReadiness}", 0, 0f);
            return;
        }

        GameInventoryReader.SetSelectedHeroBuildTheme(requestedTheme);
        // Arm fault injection only after this hook owns the idle queue. A
        // request waiting behind a user's operation must never affect that run.
        GameInventoryReader.RuntimeRejectJointBuildAfterSkills = requestedVerificationFailure;
        if (InGameSearchOverlay.TryQueueRuntimeJointBuildTest(out lastReadiness))
        {
            queued = true;
            queuedAt = Time.unscaledTime;
            lastResponsiveUpdateAt = Time.realtimeSinceStartup;
            return;
        }
        GameInventoryReader.RuntimeRejectJointBuildAfterSkills = false;
        if (Time.unscaledTime - armedAt < 90f) return;
        WriteResult(false, 0, $"queue timeout: {lastReadiness}", 0, 0f);
    }

    public void OnDestroy()
    {
        // If the worker is already queued, the overlay waits for commit/verify/
        // rollback before releasing this lease. A pre-queue teardown is safe to
        // clean up here because no optimizer can still reference the selection.
        if (!queued && selectionLeaseOwned)
        {
            GameInventoryReader.ReleaseRuntimeTestHeroSelection(out _);
            if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
                selectionLeaseOwned = false;
        }
    }

    private void WriteResult(
        bool success,
        long elapsedMilliseconds,
        string message,
        int observedResponsiveUpdates,
        float observedMaxGapMilliseconds,
        string postState = "")
    {
        GameInventoryReader.RuntimeRejectJointBuildAfterSkills = false;
        var selectionCleanup = string.Empty;
        if (selectionLeaseOwned)
        {
            GameInventoryReader.ReleaseRuntimeTestHeroSelection(out selectionCleanup);
            if (!GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
                selectionLeaseOwned = false;
        }
        if (GameInventoryReader.RuntimeTestSelectionBlocksUserOperations)
        {
            success = false;
            selectionCleanup = string.IsNullOrWhiteSpace(selectionCleanup)
                ? "runtime selection cleanup remains incomplete"
                : selectionCleanup + "; runtime selection cleanup remains incomplete";
        }
        if (!string.IsNullOrWhiteSpace(selectionCleanup))
        {
            success = false;
            message = $"{message}; selection cleanup: {selectionCleanup}";
        }
        armed = false;
        queued = false;
        queueCancellationRequested = false;
        queuedAt = 0f;
        responsiveUpdates = 0;
        lastResponsiveUpdateAt = 0f;
        maxResponsiveGapMilliseconds = 0f;
        var sanitized = (message ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        var sanitizedPostState = (postState ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        try
        {
            System.IO.File.WriteAllText(
                ResultPath,
                $"success={success.ToString().ToLowerInvariant()}\n" +
                $"elapsed_ms={elapsedMilliseconds}\n" +
                $"responsive_updates={observedResponsiveUpdates}\n" +
                $"max_update_gap_ms={observedMaxGapMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"post_state={sanitizedPostState}\n" +
                $"message={sanitized}\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // Broad logging is intentionally disabled for this bounded test hook.
        }
    }
}
#endif

#if PATHOFIDLE_RUNTIME_TEST
// Bounded command-driven check for the game's own F1 hide/restore path. This is
// deliberately absent from public builds and always restores a hidden window
// before it writes its single result file.
public sealed class RuntimeWindowToggleTestHook : MonoBehaviour
{
    private const string RequestText = "TOGGLE GAME WINDOW v1.1.8";
    private const int PlannedTransitions = 12;
    private static readonly string RequestPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-window-toggle.request");
    private static readonly string ResultPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-window-toggle.result");
    private static object? toggleTarget;
    private static MethodInfo? toggleMethod;
    private static PropertyInfo? hiddenProperty;
    private static PropertyInfo? windowHandleProperty;

    private bool running;
    private int completedTransitions;
    private float nextProbeAt;
    private float nextTransitionAt;
    private float minObservedTimeScale = float.MaxValue;
    private float maxObservedTimeScale = float.MinValue;

    public RuntimeWindowToggleTestHook(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (!running)
        {
            if (Time.realtimeSinceStartup < nextProbeAt) return;
            nextProbeAt = Time.realtimeSinceStartup + 0.5f;
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
            if (!string.Equals(request, RequestText, StringComparison.Ordinal)) return;
            try { System.IO.File.Delete(RequestPath); } catch { }
            try { System.IO.File.Delete(ResultPath); } catch { }
            if (!TryResolveToggle(out var error))
            {
                WriteResult(false, error);
                return;
            }
            try
            {
                if (ReadHidden())
                {
                    toggleMethod!.Invoke(toggleTarget, null);
                    if (ReadHidden()) throw new InvalidOperationException("initial window restore did not change state");
                }
            }
            catch (Exception exception)
            {
                WriteResult(false, $"initial restore failed: {exception.GetBaseException().Message}");
                return;
            }
            running = true;
            completedTransitions = 0;
            minObservedTimeScale = float.MaxValue;
            maxObservedTimeScale = float.MinValue;
            nextTransitionAt = Time.realtimeSinceStartup + 0.5f;
            return;
        }

        minObservedTimeScale = Math.Min(minObservedTimeScale, Time.timeScale);
        maxObservedTimeScale = Math.Max(maxObservedTimeScale, Time.timeScale);
        if (Time.realtimeSinceStartup < nextTransitionAt) return;
        nextTransitionAt = Time.realtimeSinceStartup + 0.35f;
        try
        {
            var wasHidden = ReadHidden();
            toggleMethod!.Invoke(toggleTarget, null);
            if (ReadHidden() == wasHidden)
                throw new InvalidOperationException("game window toggle did not change hidden state");
            completedTransitions++;
            if (completedTransitions < PlannedTransitions) return;
            if (ReadHidden()) toggleMethod.Invoke(toggleTarget, null);
            WriteResult(true, "bounded hide/restore transitions completed");
        }
        catch (Exception exception)
        {
            try
            {
                if (ReadHidden()) toggleMethod?.Invoke(toggleTarget, null);
            }
            catch
            {
                // Preserve the original failure in the result below.
            }
            WriteResult(false, exception.GetBaseException().Message);
        }
    }

    public void OnDestroy()
    {
        try
        {
            if (running && ReadHidden()) toggleMethod?.Invoke(toggleTarget, null);
        }
        catch
        {
            // Process teardown owns the window after this point.
        }
    }

    private static bool TryResolveToggle(out string error)
    {
        error = string.Empty;
        if (toggleTarget is not null && toggleMethod is not null && hiddenProperty is not null
            && windowHandleProperty is not null) return true;
        try
        {
            var gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
            var toggleType = gameAssembly?.GetType("WindowToggleIL2CPP", false, false);
            if (toggleType is null) throw new TypeLoadException("WindowToggleIL2CPP was not found");
            var finder = typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == nameof(UnityEngine.Object.FindObjectOfType)
                                          && method.IsGenericMethodDefinition
                                          && method.GetParameters().Length == 0);
            if (finder is null) throw new MissingMethodException("UnityEngine.Object.FindObjectOfType<T>()");
            toggleTarget = finder.MakeGenericMethod(toggleType).Invoke(null, null);
            if (toggleTarget is null) throw new InvalidOperationException("WindowToggleIL2CPP instance is not ready");
            toggleMethod = toggleType.GetMethod(
                "ToggleWindowVisibility", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            hiddenProperty = toggleType.GetProperty(
                "isWindowHidden", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            windowHandleProperty = toggleType.GetProperty(
                "gameWindowHandle",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (toggleMethod is null || hiddenProperty is null || windowHandleProperty is null)
                throw new MissingMethodException("WindowToggleIL2CPP toggle members");
            var currentWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (currentWindowHandle == IntPtr.Zero)
                throw new InvalidOperationException("current game window handle is unavailable");
            windowHandleProperty.SetValue(
                windowHandleProperty.GetMethod?.IsStatic == true ? null : toggleTarget,
                currentWindowHandle);
            return true;
        }
        catch (Exception exception)
        {
            toggleTarget = null;
            toggleMethod = null;
            hiddenProperty = null;
            windowHandleProperty = null;
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static bool ReadHidden() => hiddenProperty is not null
                                        && toggleTarget is not null
                                        && Convert.ToBoolean(hiddenProperty.GetValue(toggleTarget), CultureInfo.InvariantCulture);

    private void WriteResult(bool success, string message)
    {
        var finalHidden = false;
        try { finalHidden = ReadHidden(); } catch { }
        var sanitized = (message ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        try
        {
            System.IO.File.WriteAllText(
                ResultPath,
                $"success={success.ToString().ToLowerInvariant()}\n" +
                $"completed_transitions={completedTransitions}\n" +
                $"planned_transitions={PlannedTransitions}\n" +
                $"final_hidden={finalHidden.ToString().ToLowerInvariant()}\n" +
                $"min_time_scale={(minObservedTimeScale == float.MaxValue ? 0f : minObservedTimeScale).ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"max_time_scale={(maxObservedTimeScale == float.MinValue ? 0f : maxObservedTimeScale).ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"message={sanitized}\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // The external runner also checks process survival and file timeout.
        }
        running = false;
        completedTransitions = 0;
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

    public static bool HeroWheelBlockAreaPrefix(ref bool __result)
    {
        if (!InGameSearchOverlay.ShouldBlockWheel) return true;
        __result = true;
        if (!blockReported)
        {
            blockReported = true;
            Plugin.DiagInfo("Character wheel selection area blocked while the pointer is over the focused overlay.");
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

internal enum AutoBuildPhase
{
    Queued,
    WaitingForSafeState,
    Planning,
    Mutating,
    Finished,
    Cancelled
}

internal enum AutoBuildStep
{
    Preparing,
    WaitingForSafeState,
    CollectingCandidates,
    SearchingLoadouts,
    CoarseEvaluation,
    StructuralScreen,
    FinalRefinement,
    ApplyingGear,
    ApplyingSkills,
    Verifying,
    RollingBack,
    FinalizingStorage,
    Finished,
    Cancelled,
    TimedOut
}

internal sealed class AutoBuildOperation
{
    internal sealed record ProgressState(
        AutoBuildPhase Phase,
        AutoBuildStep Step,
        int Index,
        int Total,
        string Detail);

    // Exactly one transition is allowed out of the planning state. Keeping
    // cancellation and mutation in one atomic word prevents the old race where
    // RequestCancel observed "not mutating" immediately before TryBeginMutation
    // committed the first live write.
    private const int PlanningState = 0;
    private const int CancelledState = 1;
    private const int MutatingState = 2;
    private const int CompletedWithoutMutationState = 4;
    private readonly object progressSync = new();
    private int transitionState = PlanningState;
    private int completed;
    private int planningDeadlineReached;
    private long planningStartedTimestamp;
    private long planningStoppedTimestamp;
    private System.Threading.Timer? planningTimer;
    private ProgressState progress = new(
        AutoBuildPhase.Queued,
        AutoBuildStep.Preparing,
        0,
        0,
        string.Empty);

    public AutoBuildOperation(
        string label,
        bool autoTransformSkills,
        int autoTransformMaxAttempts,
        int planningLimitSeconds,
        int themePriority,
        string defaultTheme,
        string heroThemes,
        string targetHeroKey,
        bool includeForgeBonuses = true)
    {
        Label = label;
        AutoTransformSkills = autoTransformSkills;
        AutoTransformMaxAttempts = autoTransformMaxAttempts;
        PlanningLimitSeconds = Plugin.NormalizeAutoBuildPlanningSeconds(planningLimitSeconds);
        ThemePriority = Plugin.NormalizeAutoBuildThemePriority(themePriority);
        DefaultTheme = defaultTheme;
        HeroThemes = heroThemes;
        TargetHeroKey = targetHeroKey;
        IncludeForgeBonuses = includeForgeBonuses;
        StartedAtUtc = DateTime.UtcNow;
    }

    public string Label { get; }
    public bool AutoTransformSkills { get; }
    public int AutoTransformMaxAttempts { get; }
    public int PlanningLimitSeconds { get; }
    public int ThemePriority { get; }
    public bool HasPlanningLimit => PlanningLimitSeconds > 0;
    public string DefaultTheme { get; }
    public string HeroThemes { get; }
    public string TargetHeroKey { get; }
    public bool IncludeForgeBonuses { get; }
    public DateTime StartedAtUtc { get; }
    public bool CancelRequested => Volatile.Read(ref transitionState) == CancelledState;
    public bool PlanningDeadlineReached
    {
        get
        {
            // The timer is only a wake-up aid. A long-running worker can delay a
            // ThreadPool timer callback, so every observer also refreshes the
            // deadline from the monotonic wall clock before reading the flag.
            CheckPlanningTimeout();
            return Volatile.Read(ref planningDeadlineReached) != 0;
        }
    }
    public bool MutationStarted => Volatile.Read(ref transitionState) == MutatingState;
    public bool CanCancelPlanning => Volatile.Read(ref transitionState) == PlanningState;
    public bool IsCompleted => Volatile.Read(ref completed) != 0;
    public ProgressState Progress => Volatile.Read(ref progress);
    public AutoBuildPhase Phase => Progress.Phase;
    public string WaitReason => Progress.Detail;
    public bool PlanningClockStarted => Volatile.Read(ref planningStartedTimestamp) > 0;
    public double PlanningElapsedSeconds
    {
        get
        {
            var started = Volatile.Read(ref planningStartedTimestamp);
            if (started <= 0) return 0d;
            var stopped = Volatile.Read(ref planningStoppedTimestamp);
            var end = stopped > 0 ? stopped : Stopwatch.GetTimestamp();
            return Math.Max(0d, end - started) / Stopwatch.Frequency;
        }
    }

    public void BeginPlanning()
    {
        if (Volatile.Read(ref transitionState) != PlanningState)
        {
            ThrowIfPlanningStopped();
            return;
        }
        var started = Stopwatch.GetTimestamp();
        if (Interlocked.CompareExchange(ref planningStartedTimestamp, started, 0L) == 0L)
        {
            // The timer only wakes an otherwise quiet operation. Very large
            // finite values are still enforced by the monotonic checks below,
            // without overflowing Timer's millisecond argument.
            var dueMilliseconds = (long)PlanningLimitSeconds * 1000L;
            if (HasPlanningLimit && dueMilliseconds <= int.MaxValue)
            {
                var timer = new System.Threading.Timer(
                    static state => ((AutoBuildOperation)state!).RequestPlanningTimeout(),
                    this,
                    (int)dueMilliseconds,
                    Timeout.Infinite);
                var previous = Interlocked.CompareExchange(ref planningTimer, timer, null);
                if (previous is not null) timer.Dispose();
            }
            if (Volatile.Read(ref transitionState) != PlanningState)
                StopPlanningClock();
        }
        ThrowIfPlanningStopped();
        SetPhase(AutoBuildPhase.Planning);
    }

    public void RequestCancel()
    {
        if (Interlocked.CompareExchange(ref transitionState, CancelledState, PlanningState) == PlanningState)
        {
            StopPlanningClock();
            PublishProgress(new ProgressState(
                AutoBuildPhase.Cancelled,
                AutoBuildStep.Cancelled,
                Progress.Index,
                Progress.Total,
                string.Empty));
        }
        MainThreadNativeGateway.WakeMainThread();
    }

    public void SetPhase(AutoBuildPhase nextPhase, string reason = "")
    {
        var current = Progress;
        var step = nextPhase switch
        {
            AutoBuildPhase.Queued => AutoBuildStep.Preparing,
            AutoBuildPhase.WaitingForSafeState => AutoBuildStep.WaitingForSafeState,
            AutoBuildPhase.Planning => current.Step is AutoBuildStep.Preparing or AutoBuildStep.WaitingForSafeState
                ? AutoBuildStep.Preparing
                : current.Step,
            AutoBuildPhase.Mutating => current.Step is AutoBuildStep.ApplyingGear
                or AutoBuildStep.ApplyingSkills
                or AutoBuildStep.Verifying
                or AutoBuildStep.RollingBack
                or AutoBuildStep.FinalizingStorage
                    ? current.Step
                    : AutoBuildStep.ApplyingGear,
            AutoBuildPhase.Finished => AutoBuildStep.Finished,
            AutoBuildPhase.Cancelled => AutoBuildStep.Cancelled,
            _ => current.Step
        };
        PublishProgress(new ProgressState(
            nextPhase,
            step,
            nextPhase is AutoBuildPhase.Planning or AutoBuildPhase.Mutating ? current.Index : 0,
            nextPhase is AutoBuildPhase.Planning or AutoBuildPhase.Mutating ? current.Total : 0,
            reason ?? string.Empty));
    }

    public void SetProgress(AutoBuildStep step, int index = 0, int total = 0, string detail = "")
    {
        CheckPlanningTimeout();
        var state = Volatile.Read(ref transitionState);
        if (state is CancelledState or CompletedWithoutMutationState) return;
        var phase = state == MutatingState
            ? AutoBuildPhase.Mutating
            : step == AutoBuildStep.WaitingForSafeState
                ? AutoBuildPhase.WaitingForSafeState
                : AutoBuildPhase.Planning;
        PublishProgress(new ProgressState(
            phase,
            step,
            Math.Max(0, index),
            Math.Max(0, total),
            detail ?? string.Empty));
    }

    public bool TryBeginMutation()
    {
        CheckPlanningTimeout();
        ThrowIfPlanningStopped();
        var previous = Interlocked.CompareExchange(ref transitionState, MutatingState, PlanningState);
        if (previous == PlanningState)
        {
            StopPlanningClock();
            SetPhase(AutoBuildPhase.Mutating);
            return true;
        }
        if (previous == CancelledState) throw new OperationCanceledException("Auto Build was cancelled before mutation.");
        return previous == MutatingState;
    }

    public void ThrowIfPlanningStopped()
    {
        CheckPlanningTimeout();
        var state = Volatile.Read(ref transitionState);
        if (state == CancelledState)
            throw new OperationCanceledException("Auto Build was cancelled before mutation.");
    }

    public void MarkCompleted(bool cancelled)
    {
        Interlocked.CompareExchange(
            ref transitionState,
            CompletedWithoutMutationState,
            PlanningState);
        StopPlanningClock();
        SetPhase(CancelRequested || cancelled ? AutoBuildPhase.Cancelled : AutoBuildPhase.Finished);
        Volatile.Write(ref completed, 1);
        MainThreadNativeGateway.WakeMainThread();
    }

    private void CheckPlanningTimeout()
    {
        if (!HasPlanningLimit
            || Volatile.Read(ref transitionState) != PlanningState
            || !PlanningClockStarted) return;
        if (PlanningElapsedSeconds + 0.000000001d < PlanningLimitSeconds) return;
        RequestPlanningTimeout();
    }

    private void RequestPlanningTimeout()
    {
        if (!HasPlanningLimit
            || Volatile.Read(ref transitionState) != PlanningState
            || Interlocked.Exchange(ref planningDeadlineReached, 1) != 0) return;
        StopPlanningClock();
        var current = Progress;
        PublishProgress(new ProgressState(
            AutoBuildPhase.Planning,
            current.Step,
            current.Index,
            current.Total,
            UiText.L(
                $"{PlanningLimitSeconds:N0}초 도달 · 현재 안전 비교를 마무리하는 중",
                $"{PlanningLimitSeconds:N0}s reached · finishing the current safe comparison",
                $"已达 {PlanningLimitSeconds:N0} 秒 · 正在完成当前安全比较",
                $"已達 {PlanningLimitSeconds:N0} 秒 · 正在完成目前安全比較")));
        MainThreadNativeGateway.WakeMainThread();
    }

    private void StopPlanningClock()
    {
        var started = Volatile.Read(ref planningStartedTimestamp);
        if (started > 0)
            Interlocked.CompareExchange(ref planningStoppedTimestamp, Stopwatch.GetTimestamp(), 0L);
        Interlocked.Exchange(ref planningTimer, null)?.Dispose();
    }

    private void PublishProgress(ProgressState next)
    {
        lock (progressSync)
        {
            var state = Volatile.Read(ref transitionState);
            if (next.Phase is AutoBuildPhase.Planning or AutoBuildPhase.WaitingForSafeState
                && state != PlanningState) return;
            if (next.Phase == AutoBuildPhase.Mutating && state != MutatingState) return;
            Volatile.Write(ref progress, next);
        }
    }
}

internal readonly record struct AutoBuildWorkerResult(bool Succeeded, bool Cancelled, string Message);

// Unity and generated IL2CPP wrappers are main-thread-bound. The optimizer is
// allowed to do its managed search on a worker, but every reflection/IL2CPP
// access is synchronously marshalled through this gateway. During a live
// mutation the game clock and overlay input are held, while Drain executes one
// short native step per frame. Rendering and progress therefore remain alive
// without allowing gameplay to advance through a half-applied build.
internal static class MainThreadNativeGateway
{
    private interface IWorkItem
    {
        AutoBuildOperation? Operation { get; }
        void Execute();
        void Fail(Exception error);
    }

    private sealed class WorkItem<T> : IWorkItem, IDisposable
    {
        private readonly Func<T> action;
        private readonly ManualResetEventSlim completed = new(false);
        private T result = default!;
        private Exception? error;

        public WorkItem(Func<T> action, AutoBuildOperation? operation)
        {
            this.action = action;
            Operation = operation;
        }

        public AutoBuildOperation? Operation { get; }

        public T Wait()
        {
            completed.Wait();
            if (error is not null) throw error;
            return result;
        }

        public void Execute()
        {
            var previousExecutingOperation = executingOperation;
            executingOperation = Operation;
            try
            {
                Operation?.ThrowIfPlanningStopped();
                result = action();
                Operation?.ThrowIfPlanningStopped();
            }
            catch (Exception caught)
            {
                error = caught;
            }
            finally
            {
                executingOperation = previousExecutingOperation;
                completed.Set();
            }
        }

        public void Fail(Exception caught)
        {
            error = caught;
            completed.Set();
        }

        public void Dispose() => completed.Dispose();
    }

    private sealed class WorkerScope : IDisposable
    {
        private readonly AutoBuildOperation? previous;

        public WorkerScope(AutoBuildOperation operation)
        {
            previous = workerOperation;
            workerOperation = operation;
        }

        public void Dispose() => workerOperation = previous;
    }

    private static readonly object Sync = new();
    private static readonly Queue<IWorkItem> Pending = new();
    [ThreadStatic] private static AutoBuildOperation? workerOperation;
    [ThreadStatic] private static AutoBuildOperation? executingOperation;
    private static int mainThreadId;
    private static bool shuttingDown;
    private static AutoBuildOperation? exclusiveOperation;

    public static bool IsMainThread => mainThreadId != 0 && Environment.CurrentManagedThreadId == mainThreadId;
    public static AutoBuildOperation? CurrentWorkerOperation => workerOperation ?? executingOperation;

    public static void InitializeMainThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (mainThreadId == 0) mainThreadId = current;
        else if (mainThreadId != current)
            throw new InvalidOperationException("The native gateway was initialized from a non-main thread.");
        shuttingDown = false;
    }

    public static IDisposable AttachWorker(AutoBuildOperation operation) => new WorkerScope(operation);

    public static T Invoke<T>(Func<T> action)
    {
        if (IsMainThread) return action();
        if (mainThreadId == 0)
            throw new InvalidOperationException("The native gateway has not been initialized on Unity's main thread.");

        var operation = workerOperation;
        operation?.ThrowIfPlanningStopped();
        using var item = new WorkItem<T>(action, operation);
        lock (Sync)
        {
            if (shuttingDown)
                throw new OperationCanceledException("The native gateway is shutting down.");
            Pending.Enqueue(item);
            Monitor.PulseAll(Sync);
        }
        var result = item.Wait();
        operation?.ThrowIfPlanningStopped();
        return result;
    }

    public static void Invoke(Action action)
        => Invoke(() =>
        {
            action();
            return true;
        });

    public static void EnterExclusiveMutation(AutoBuildOperation operation)
    {
        Invoke(() =>
        {
            exclusiveOperation = operation;
            return true;
        });
    }

    public static void Drain(double softBudgetMilliseconds)
    {
        InitializeMainThread();
        var exclusive = exclusiveOperation;
        if (exclusive is not null)
        {
            if (exclusive.IsCompleted)
            {
                exclusiveOperation = null;
                return;
            }

            if (TryDequeueFor(exclusive, out var exclusiveItem))
                exclusiveItem.Execute();
            if (exclusive.IsCompleted) exclusiveOperation = null;
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        while (TryDequeue(out var item))
        {
            item.Execute();
            if (exclusiveOperation is not null
                || stopwatch.Elapsed.TotalMilliseconds >= softBudgetMilliseconds) break;
        }
    }

    public static void WakeMainThread()
    {
        lock (Sync) Monitor.PulseAll(Sync);
    }

    public static void Shutdown()
    {
        List<IWorkItem> abandoned;
        lock (Sync)
        {
            shuttingDown = true;
            abandoned = Pending.ToList();
            Pending.Clear();
            Monitor.PulseAll(Sync);
        }
        var error = new OperationCanceledException("The native gateway was shut down.");
        foreach (var item in abandoned) item.Fail(error);
    }

    private static bool TryDequeue(out IWorkItem item)
    {
        lock (Sync)
        {
            if (Pending.Count > 0)
            {
                item = Pending.Dequeue();
                return true;
            }
        }
        item = null!;
        return false;
    }

    private static bool TryDequeueFor(AutoBuildOperation operation, out IWorkItem item)
    {
        lock (Sync)
        {
            var count = Pending.Count;
            for (var index = 0; index < count; index++)
            {
                var candidate = Pending.Dequeue();
                if (ReferenceEquals(candidate.Operation, operation))
                {
                    item = candidate;
                    return true;
                }
                Pending.Enqueue(candidate);
            }
        }
        item = null!;
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
