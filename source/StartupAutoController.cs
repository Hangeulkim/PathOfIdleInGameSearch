using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace PathOfIdleInGameSearch;

internal enum StartupAutoState
{
    Disabled,
    WaitingForMainOrGame,
    WaitingForGame,
    StabilizingGameScene,
    WaitingForModules,
    EnablingAuto,
    Complete,
    Cancelled,
    TimedOut,
    Failed
}

// This is deliberately a managed static controller rather than another
// injected MonoBehaviour. Only InGameSearchOverlay.Update calls Tick, so every
// Unity/game access stays on the main thread without exposing helper signatures
// to IL2CPP class registration.
internal static class StartupAutoController
{
    private const float ProbeIntervalSeconds = 0.4f;
    private const float OverallTimeoutSeconds = 90f;
    private const float PostContinueTimeoutSeconds = 60f;
    private const int RequiredStableSnapshots = 3;

    private static readonly Dictionary<string, bool> AutoEligibility = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AttemptedFields = new(StringComparer.Ordinal);
    private static readonly HashSet<string> CompletedFields = new(StringComparer.Ordinal);
    private static bool initialized;
    private static bool lastSetting;
    private static bool actionStarted;
    private static bool continueInvoked;
    private static float armedAt;
    private static float continueInvokedAt;
    private static float nextProbeAt;
    private static string stableFingerprint = string.Empty;
    private static int stableSnapshots;
    private static int enabledCount;
    private static int alreadyEnabledCount;
    private static int skippedCount;
    private static int failedCount;
    private static string detail = string.Empty;

    internal static StartupAutoState State { get; private set; } = StartupAutoState.Disabled;

    internal static void Initialize(bool enabled)
    {
        if (initialized)
        {
            lastSetting = enabled;
            return;
        }

        initialized = true;
        lastSetting = enabled;
        if (enabled) Arm();
    }

    internal static void OnSettingChanged(bool enabled)
    {
        if (!initialized) Initialize(enabled);
        lastSetting = enabled;
        if (!enabled)
        {
            if (!IsTerminal(State) && State != StartupAutoState.Disabled)
            {
                State = StartupAutoState.Cancelled;
                detail = UiText.L(
                    "자동 시작 대기 취소됨 (이미 수행한 화면 전환·Auto는 유지)",
                    "Auto Start cancelled; completed scene/Auto changes were kept.",
                    "已取消自动启动；已完成的场景切换和 Auto 保持不变。",
                    "已取消自動啟動；已完成的場景切換和 Auto 保持不變。");
            }
            else if (!actionStarted)
            {
                State = StartupAutoState.Disabled;
                detail = string.Empty;
            }
            return;
        }

        // Once this process has actually pressed Continue or started applying
        // Auto, returning to the main scene must never trigger it a second time.
        if (!actionStarted && State is StartupAutoState.Disabled or StartupAutoState.Cancelled
            or StartupAutoState.TimedOut or StartupAutoState.Failed)
            Arm();
    }

    internal static void Tick(bool enabled)
    {
        if (!initialized) Initialize(enabled);
        if (enabled != lastSetting) OnSettingChanged(enabled);
        if (!enabled || IsTerminal(State) || Time.unscaledTime < nextProbeAt) return;

        nextProbeAt = Time.unscaledTime + ProbeIntervalSeconds;
        if (HasTimedOut())
        {
            State = StartupAutoState.TimedOut;
            detail = UiText.L(
                "90초 안에 게임/파티가 준비되지 않아 자동 시작을 중단함",
                "Auto Start stopped because the game/parties were not ready in time.",
                "游戏或队伍未及时准备好，自动启动已停止。",
                "遊戲或隊伍未及時準備好，自動啟動已停止。");
            return;
        }

        switch (State)
        {
            case StartupAutoState.WaitingForMainOrGame:
                TickWaitingForMainOrGame();
                break;
            case StartupAutoState.WaitingForGame:
            case StartupAutoState.StabilizingGameScene:
            case StartupAutoState.WaitingForModules:
                TickGameStability();
                break;
            case StartupAutoState.EnablingAuto:
                TickEnableAuto();
                break;
        }
    }

    internal static void CancelForShutdown()
    {
        if (!IsTerminal(State) && State != StartupAutoState.Disabled)
        {
            State = StartupAutoState.Cancelled;
            detail = string.Empty;
        }
    }

    internal static string GetStatusLabel()
    {
        if (!string.IsNullOrWhiteSpace(detail)) return detail;
        return State switch
        {
            StartupAutoState.Disabled => UiText.L("자동 시작 꺼짐", "Auto Start is off", "自动启动已关闭", "自動啟動已關閉"),
            StartupAutoState.WaitingForMainOrGame => UiText.L("메인 화면/게임 준비 대기 중", "Waiting for the main/game scene", "正在等待主界面或游戏场景", "正在等待主畫面或遊戲場景"),
            StartupAutoState.WaitingForGame => UiText.L("Continue Game 완료 및 게임 로딩 대기 중", "Continue requested; waiting for the game", "已请求继续游戏，正在等待加载", "已要求繼續遊戲，正在等待載入"),
            StartupAutoState.StabilizingGameScene => UiText.L("파티 데이터 안정화 확인 중", "Checking stable party data", "正在确认队伍数据稳定", "正在確認隊伍資料穩定"),
            StartupAutoState.WaitingForModules => UiText.L("파티 Auto 화면 준비 대기 중", "Waiting for party Auto controls", "正在等待队伍 Auto 控件", "正在等待隊伍 Auto 控制項"),
            StartupAutoState.EnablingAuto => UiText.L("활동 중인 파티의 Auto 적용 중", "Enabling Auto for active parties", "正在为活动队伍启用 Auto", "正在為活動隊伍啟用 Auto"),
            StartupAutoState.Complete => UiText.L(
                $"자동 시작 완료 · 새로 켬 {enabledCount} · 이미 켜짐 {alreadyEnabledCount} · 제외 {skippedCount}",
                $"Auto Start complete · enabled {enabledCount} · already on {alreadyEnabledCount} · skipped {skippedCount}",
                $"自动启动完成 · 新启用 {enabledCount} · 已启用 {alreadyEnabledCount} · 跳过 {skippedCount}",
                $"自動啟動完成 · 新啟用 {enabledCount} · 已啟用 {alreadyEnabledCount} · 略過 {skippedCount}"),
            StartupAutoState.Cancelled => UiText.L("자동 시작 취소됨", "Auto Start cancelled", "自动启动已取消", "自動啟動已取消"),
            StartupAutoState.TimedOut => UiText.L("자동 시작 시간 초과", "Auto Start timed out", "自动启动超时", "自動啟動逾時"),
            StartupAutoState.Failed => UiText.L(
                $"자동 시작 일부 실패 · 실패 {failedCount}",
                $"Auto Start partially failed · failed {failedCount}",
                $"自动启动部分失败 · 失败 {failedCount}",
                $"自動啟動部分失敗 · 失敗 {failedCount}"),
            _ => string.Empty
        };
    }

    private static void Arm()
    {
        AutoEligibility.Clear();
        AttemptedFields.Clear();
        CompletedFields.Clear();
        stableFingerprint = string.Empty;
        stableSnapshots = 0;
        continueInvoked = false;
        armedAt = Time.unscaledTime;
        continueInvokedAt = 0f;
        nextProbeAt = 0f;
        enabledCount = 0;
        alreadyEnabledCount = 0;
        skippedCount = 0;
        failedCount = 0;
        detail = string.Empty;
        State = StartupAutoState.WaitingForMainOrGame;
    }

    private static bool IsTerminal(StartupAutoState state)
        => state is StartupAutoState.Complete or StartupAutoState.Cancelled
            or StartupAutoState.TimedOut or StartupAutoState.Failed;

    private static bool HasTimedOut()
        => Time.unscaledTime - armedAt >= OverallTimeoutSeconds
           || (continueInvoked
               && Time.unscaledTime - continueInvokedAt >= PostContinueTimeoutSeconds);

    private static void TickWaitingForMainOrGame()
    {
        if (TryCaptureGameSnapshot(out _))
        {
            actionStarted = true;
            State = StartupAutoState.StabilizingGameScene;
            return;
        }

        if (!IsScene("MainScene")) return;
        var dataManager = ReadStatic("Game", "dataMgr");
        if (IsUsable(dataManager)
            && TryInvokeExact(dataManager!, "IsDemoBlockedByReleaseSave", Array.Empty<object>(), out var blocked)
            && ToBool(blocked))
        {
            State = StartupAutoState.Failed;
            failedCount = 1;
            detail = UiText.L(
                "현재 저장은 이 버전에서 Continue Game을 사용할 수 없음",
                "This save cannot use Continue Game in the current game edition.",
                "当前存档无法在此版本中继续游戏。",
                "目前存檔無法在此版本中繼續遊戲。");
            return;
        }
        if (!TryGetReadyMainScene(out var mainScene)) return;

        // Mark before invoking: even if the native handler throws or opens an
        // intervening notice, it must never receive a duplicate Continue click.
        actionStarted = true;
        continueInvoked = true;
        continueInvokedAt = Time.unscaledTime;
        if (!TryInvokeExactVoid(mainScene, "OnContinueBtnClick", Array.Empty<object>(), out var failure))
        {
            State = StartupAutoState.Failed;
            failedCount = 1;
            detail = UiText.L(
                $"Continue Game 호출 실패 · {failure}",
                $"Continue Game failed · {failure}",
                $"继续游戏调用失败 · {failure}",
                $"繼續遊戲呼叫失敗 · {failure}");
            return;
        }
        State = StartupAutoState.WaitingForGame;
    }

    private static bool TryGetReadyMainScene(out object mainScene)
    {
        mainScene = null!;
        var dataManager = ReadStatic("Game", "dataMgr");
        var season = Read(dataManager, "nowSeasonData");
        var candidate = ReadStatic("Game", "mainScene");
        if (!IsUsable(dataManager) || !IsUsable(season) || !IsUsable(candidate)) return false;
        if (ReadBool(season, "isError")) return false;
        if (!TryInvokeExact(dataManager!, "HasAnySeasonSave", Array.Empty<object>(), out var hasSave)
            || !ToBool(hasSave)) return false;
        var gameObject = Read(candidate, "gameObject");
        if (!IsUsable(gameObject) || !ReadBool(gameObject, "activeInHierarchy")) return false;
        var hasDataArea = Read(candidate, "hasDataArea");
        if (!IsUsable(hasDataArea) || !ReadBool(hasDataArea, "activeInHierarchy")) return false;
        mainScene = candidate!;
        return true;
    }

    private static void TickGameStability()
    {
        if (!TryCaptureGameSnapshot(out var snapshot))
        {
            State = continueInvoked ? StartupAutoState.WaitingForGame : StartupAutoState.WaitingForModules;
            stableFingerprint = string.Empty;
            stableSnapshots = 0;
            return;
        }

        State = StartupAutoState.StabilizingGameScene;
        if (string.Equals(stableFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
            stableSnapshots++;
        else
        {
            stableFingerprint = snapshot.Fingerprint;
            stableSnapshots = 1;
        }
        if (stableSnapshots >= RequiredStableSnapshots)
            State = StartupAutoState.EnablingAuto;
    }

    private static void TickEnableAuto()
    {
        if (!TryCaptureGameSnapshot(out var snapshot))
        {
            State = StartupAutoState.WaitingForModules;
            return;
        }

        var pendingModuleCount = 0;
        foreach (var cell in snapshot.Cells)
        {
            var field = Read(cell, "advFieldData");
            var save = Read(field, "saveAdvFieldData");
            var key = FieldKey(cell, field, save);
            if (!IsUsable(field) || !IsUsable(save) || CompletedFields.Contains(key)) continue;

            var fieldState = ReadInt(save, "advFieldState", -1);
            if (fieldState is < 1 or > 3
                || !TryInvokeBool(field!, "CheckHasHero", out var hasHero) || !hasHero)
            {
                CompletedFields.Add(key);
                skippedCount++;
                continue;
            }

            var module = GetStateModule(cell, fieldState);
            if (!IsReadyStateModule(module, field))
            {
                pendingModuleCount++;
                continue;
            }

            if (!AutoEligibility.TryGetValue(key, out var eligible))
            {
                eligible = TryInvokeBool(field!, "CheckCanAuto", out var canAuto) && canAuto;
                AutoEligibility[key] = eligible;
            }
            if (!eligible)
            {
                CompletedFields.Add(key);
                skippedCount++;
                continue;
            }

            if (ReadBool(save, "isAuto"))
            {
                var currentModule = GetStateModule(cell, fieldState);
                if (IsUsable(currentModule))
                    TryInvokeExactVoid(currentModule!, "UpdateAutoUI", Array.Empty<object>(), out _);
                CompletedFields.Add(key);
                alreadyEnabledCount++;
                continue;
            }

            if (!AttemptedFields.Add(key)) continue;
            if (!TryInvokeExactVoid(module!, "OnAutoValueChanged", new object[] { true }, out _)
                || !ReadBool(save, "isAuto"))
            {
                CompletedFields.Add(key);
                failedCount++;
                continue;
            }
            TryInvokeExactVoid(module!, "UpdateAutoUI", Array.Empty<object>(), out _);
            CompletedFields.Add(key);
            enabledCount++;
        }

        if (pendingModuleCount > 0)
        {
            State = StartupAutoState.WaitingForModules;
            return;
        }
        if (CompletedFields.Count < snapshot.Cells.Count)
        {
            State = StartupAutoState.WaitingForModules;
            return;
        }

        State = failedCount > 0 ? StartupAutoState.Failed : StartupAutoState.Complete;
    }

    private static object? GetStateModule(object cell, int state)
        => state switch
        {
            1 => Read(cell, "advPrepareMod"),
            2 => Read(cell, "advFightMod"),
            3 => Read(cell, "advEndMod"),
            _ => null
        };

    private static bool TryCaptureGameSnapshot(out GameSnapshot snapshot)
    {
        snapshot = null!;
        if (!IsScene("GameScene")) return false;
        var gameScene = ReadStatic("Game", "gameScene");
        var advMod = Read(gameScene, "advMod");
        var season = Read(ReadStatic("Game", "dataMgr"), "nowSeasonData");
        var advData = Read(season, "advData");
        if (!IsUsable(gameScene) || !IsUsable(advMod) || !IsUsable(season)
            || !IsUsable(advData) || ReadBool(season, "isError")) return false;

        var cells = ReadList(Read(advMod, "advFieldList"));
        var savedFields = ReadList(Read(advData, "advFieldList"));
        if (cells.Count == 0 || savedFields.Count == 0) return false;
        var moduleAdvData = Read(advMod, "advData");
        if (IsUsable(moduleAdvData) && !SameNativeObject(moduleAdvData, advData)) return false;
        if (cells.Count != savedFields.Count) return false;
        if (cells.Any(cell =>
            {
                var field = Read(cell, "advFieldData");
                return !IsUsable(field) || !IsUsable(Read(field, "saveAdvFieldData"));
            })) return false;

        var fingerprintRows = new List<string>(cells.Count);
        foreach (var cell in cells)
        {
            var field = Read(cell, "advFieldData");
            var save = Read(field, "saveAdvFieldData");
            var state = ReadInt(save, "advFieldState", -1);
            if (!TryInvokeBool(field!, "CheckHasHero", out var hasHero)) return false;
            var moduleReady = state is >= 1 and <= 3 && hasHero
                && IsReadyStateModule(GetStateModule(cell, state), field);
            fingerprintRows.Add(
                $"{NativeKey(cell)}:{NativeKey(field)}:{ReadInt(save, "index", -1)}:{state}:{hasHero}:{moduleReady}");
        }
        var fingerprint = string.Join("|",
            NativeKey(gameScene), NativeKey(advMod), cells.Count.ToString(CultureInfo.InvariantCulture),
            string.Join(",", fingerprintRows));
        snapshot = new GameSnapshot(cells, fingerprint);
        return true;
    }

    private static bool IsReadyStateModule(object? module, object? field)
    {
        if (!IsUsable(module) || !IsUsable(field)) return false;
        var boundField = Read(module, "advFieldData");
        // A correctly bound state module is safe to invoke even when a parent
        // adventure panel is temporarily hidden. activeInHierarchy would confuse
        // UI visibility with an active party and can deadlock cold-start Auto.
        return IsUsable(boundField) && SameNativeObject(boundField, field);
    }

    private static bool SameNativeObject(object? left, object? right)
    {
        if (!IsUsable(left) || !IsUsable(right)) return false;
        if (left is Il2CppObjectBase leftNative && right is Il2CppObjectBase rightNative)
            return leftNative.Pointer == rightNative.Pointer;
        return ReferenceEquals(left, right);
    }

    private static bool IsScene(string propertyName)
    {
        var sceneManager = ReadStatic("Game", "sceneMgr");
        var sceneName = ReadStatic("TScene", propertyName);
        return IsUsable(sceneManager) && sceneName is not null
               && TryInvokeExact(sceneManager!, "IsInScene", new[] { sceneName }, out var result)
               && ToBool(result);
    }

    private static string FieldKey(object cell, object? field, object? save)
        => $"{ReadInt(save, "index", -1)}:{NativeKey(cell)}:{NativeKey(field)}";

    private static string NativeKey(object? value)
    {
        if (value is null) return "null";
        if (value is Il2CppObjectBase native)
            return native.Pointer == IntPtr.Zero ? "destroyed" : native.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        return RuntimeHelpers.GetHashCode(value).ToString("X", CultureInfo.InvariantCulture);
    }

    private static bool IsUsable(object? value)
        => value is not null && (value is not Il2CppObjectBase native || native.Pointer != IntPtr.Zero);

    private static object? ReadStatic(string typeName, string propertyName)
    {
        try
        {
            return GameType(typeName)?.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        }
        catch { return null; }
    }

    private static object? Read(object? owner, string propertyName)
    {
        if (!IsUsable(owner)) return null;
        try
        {
            return owner!.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner);
        }
        catch { return null; }
    }

    private static bool ReadBool(object? owner, string propertyName)
        => ToBool(Read(owner, propertyName));

    private static int ReadInt(object? owner, string propertyName, int fallback)
    {
        try { return Convert.ToInt32(Read(owner, propertyName), CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static bool ToBool(object? value)
    {
        try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static List<object> ReadList(object? list)
    {
        var values = new List<object>();
        if (!IsUsable(list)) return values;
        try
        {
            var countValue = Read(list, "Count") ?? Read(list, "Length");
            var count = Convert.ToInt32(countValue, CultureInfo.InvariantCulture);
            if (count is < 0 or > 1000) return values;
            var getter = list!.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "get_Item" && method.GetParameters().Length == 1);
            if (getter is null) return values;
            for (var index = 0; index < count; index++)
            {
                var item = getter.Invoke(list, new object[] { index });
                if (IsUsable(item)) values.Add(item!);
            }
        }
        catch { values.Clear(); }
        return values;
    }

    private static bool TryInvokeBool(object owner, string methodName, out bool result)
    {
        result = false;
        if (!TryInvokeExact(owner, methodName, Array.Empty<object>(), out var value)) return false;
        result = ToBool(value);
        return true;
    }

    private static bool TryInvokeExact(object owner, string methodName, object[] arguments, out object? result)
    {
        result = null;
        if (!IsUsable(owner)) return false;
        try
        {
            var method = FindCompatibleMethod(owner.GetType(), methodName, arguments);
            if (method is null) return false;
            result = method.Invoke(owner, arguments);
            return true;
        }
        catch { return false; }
    }

    private static bool TryInvokeExactVoid(object owner, string methodName, object[] arguments, out string failure)
    {
        failure = string.Empty;
        if (!IsUsable(owner))
        {
            failure = "target unavailable";
            return false;
        }
        try
        {
            var method = FindCompatibleMethod(owner.GetType(), methodName, arguments);
            if (method is null)
            {
                failure = $"{owner.GetType().Name}.{methodName} unavailable";
                return false;
            }
            method.Invoke(owner, arguments);
            return true;
        }
        catch (TargetInvocationException error)
        {
            failure = error.InnerException?.Message ?? error.Message;
            return false;
        }
        catch (Exception error)
        {
            failure = error.Message;
            return false;
        }
    }

    private static MethodInfo? FindCompatibleMethod(Type type, string name, object[] arguments)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .FirstOrDefault(method => method.GetParameters().Select((parameter, index) =>
            {
                var argument = arguments[index];
                return argument is null
                    ? !parameter.ParameterType.IsValueType || Nullable.GetUnderlyingType(parameter.ParameterType) is not null
                    : parameter.ParameterType.IsInstanceOfType(argument)
                      || (parameter.ParameterType == typeof(bool) && argument is bool);
            }).All(matches => matches));
    }

    private static Type? GameType(string name)
        => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp")?.GetType(name);

    private sealed class GameSnapshot
    {
        internal GameSnapshot(List<object> cells, string fingerprint)
        {
            Cells = cells;
            Fingerprint = fingerprint;
        }

        internal List<object> Cells { get; }
        internal string Fingerprint { get; }
    }
}
