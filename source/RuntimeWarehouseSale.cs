using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace PathOfIdleInGameSearch;

#if PATHOFIDLE_RUNTIME_TEST
// A bounded, request-file-driven maintenance action for the current save. It is
// excluded from public builds. The sale engine below is shared with the public
// UI and can only target ordinary Warehouse ItemFieldData entries; inventory,
// equipped gear, and Vault groups are never enumerated as sale candidates.
public sealed class RuntimeWarehouseSaleHook : MonoBehaviour
{
    private const string RequestContents = "SELL ORDINARY WAREHOUSE EQUIPMENT v1.1.8";
    private const float ReadinessTimeoutSeconds = 120f;
    private const float OperationTimeoutSeconds = 300f;
    private const int MaxSalesPerFrame = 8;
    private const double MaxWorkMilliseconds = 8d;
    private static readonly string RequestPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-warehouse-sale.request");
    private static readonly string ResultPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-warehouse-sale.result");

    private bool armed;
    private float armedAt;
    private float operationStartedAt;
    private float nextProbeAt;
    private string lastReadiness = "request not armed";
    private WarehouseEquipmentSaleSession? session;
    private Stopwatch? elapsed;

    public RuntimeWarehouseSaleHook(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (session is not null)
        {
            if (Time.unscaledTime - operationStartedAt >= OperationTimeoutSeconds)
            {
                Complete(GameInventoryReader.AbortWarehouseEquipmentSale(
                    session, "Warehouse sale exceeded its five-minute safety limit."));
                return;
            }

            var slice = Stopwatch.StartNew();
            for (var index = 0; index < MaxSalesPerFrame && session is not null; index++)
            {
                var outcome = GameInventoryReader.AdvanceWarehouseEquipmentSale(session);
                if (outcome.Finished)
                {
                    Complete(outcome);
                    return;
                }
                if (slice.Elapsed.TotalMilliseconds >= MaxWorkMilliseconds) break;
            }
            return;
        }

        if (Time.unscaledTime < nextProbeAt) return;
        nextProbeAt = Time.unscaledTime + 0.5f;

        if (!armed)
        {
            string request;
            try
            {
                if (!File.Exists(RequestPath)) return;
                request = File.ReadAllText(RequestPath, Encoding.UTF8).Trim();
            }
            catch
            {
                return;
            }
            if (!string.Equals(request, RequestContents, StringComparison.Ordinal)) return;

            armed = true;
            armedAt = Time.unscaledTime;
            lastReadiness = "waiting for an active save and idle inventory operations";
            try { File.Delete(RequestPath); } catch { }
            try { File.Delete(ResultPath); } catch { }
        }

        if (!InGameSearchOverlay.TryGetRuntimeJointBuildTestQueueAvailability(out lastReadiness))
        {
            if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
            WriteFailure($"readiness timeout: {lastReadiness}");
            return;
        }

        if (!GameInventoryReader.TryBeginWarehouseEquipmentSale(out var started, out lastReadiness))
        {
            if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
            WriteFailure($"could not start: {lastReadiness}");
            return;
        }

        session = started;
        operationStartedAt = Time.unscaledTime;
        elapsed = Stopwatch.StartNew();
    }

    public void OnDestroy()
    {
        if (session is not null)
            GameInventoryReader.ReleaseWarehouseEquipmentSale(session);
    }

    private void Complete(WarehouseEquipmentSaleOutcome outcome)
    {
        elapsed?.Stop();
        var elapsedMilliseconds = elapsed?.ElapsedMilliseconds ?? 0L;
        try
        {
            File.WriteAllText(
                ResultPath,
                $"success={outcome.Success.ToString().ToLowerInvariant()}\n" +
                $"uncertain={outcome.Uncertain.ToString().ToLowerInvariant()}\n" +
                $"elapsed_ms={elapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}\n" +
                $"initial_equipment={outcome.InitialEquipment.ToString(CultureInfo.InvariantCulture)}\n" +
                $"initial_sellable={outcome.InitialSellable.ToString(CultureInfo.InvariantCulture)}\n" +
                $"sold={outcome.Sold.ToString(CultureInfo.InvariantCulture)}\n" +
                $"unsellable_skipped={outcome.UnsellableSkipped.ToString(CultureInfo.InvariantCulture)}\n" +
                $"failed={outcome.Failed.ToString(CultureInfo.InvariantCulture)}\n" +
                $"remaining_equipment={outcome.RemainingEquipment.ToString(CultureInfo.InvariantCulture)}\n" +
                $"remaining_sellable={outcome.RemainingSellable.ToString(CultureInfo.InvariantCulture)}\n" +
                $"message={Sanitize(outcome.Message)}\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // The verified in-game operation must not be repeated just because
            // writing its diagnostic receipt failed.
        }
        Reset();
    }

    private void WriteFailure(string message)
    {
        var outcome = new WarehouseEquipmentSaleOutcome(
            true, false, false, 0, 0, 0, 0, 0, -1, -1, message);
        Complete(outcome);
    }

    private void Reset()
    {
        armed = false;
        session = null;
        elapsed = null;
        operationStartedAt = 0f;
        lastReadiness = "request not armed";
    }

    private static string Sanitize(string? value) => (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
#endif

internal sealed class WarehouseEquipmentSaleSession
{
    public string SaveIdentity { get; init; } = string.Empty;
    public int InitialEquipment { get; init; }
    public int InitialSellable { get; init; }
    public int UnsellableSkipped { get; init; }
    public int Sold { get; set; }
    public bool CancelRequested { get; set; }
    public Queue<InventoryEquipment> Pending { get; init; } = new();
    public HashSet<string> Known { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Failed { get; } = new(StringComparer.Ordinal);
}

internal readonly record struct WarehouseEquipmentSaleOutcome(
    bool Finished,
    bool Success,
    bool Uncertain,
    int InitialEquipment,
    int InitialSellable,
    int Sold,
    int UnsellableSkipped,
    int Failed,
    int RemainingEquipment,
    int RemainingSellable,
    string Message);

internal static partial class GameInventoryReader
{
    private static WarehouseEquipmentSaleSession? warehouseEquipmentSaleSession;

    internal static bool TryBeginWarehouseEquipmentSale(
        out WarehouseEquipmentSaleSession session,
        out string message)
    {
        session = null!;
        try
        {
            if (warehouseEquipmentSaleSession is not null)
            {
                message = "another Warehouse equipment sale is already active";
                return false;
            }

            var seasonData = Read(ReadStatic("Game", "dataMgr"), "nowSeasonData");
            var lordData = Read(seasonData, "lordData");
            if (seasonData is null || lordData is null)
            {
                message = "nowSeasonData/lordData is not loaded";
                return false;
            }

            // SellAllItems affects the complete native queue. Refuse to start
            // unless that queue is initialized and empty.
            EnsureBulkSellQueueReadyAndEmpty();
            var warehouseEquipment = ReadOrdinaryWarehouseEquipment();
            var sellable = warehouseEquipment
                .Where(entry => ReadBool(Read(Read(entry.ItemData, "tItemData"), "isSell")))
                .ToList();
            session = new WarehouseEquipmentSaleSession
            {
                SaveIdentity = NativeObjectKey(seasonData, seasonData),
                InitialEquipment = warehouseEquipment.Count,
                InitialSellable = sellable.Count,
                UnsellableSkipped = warehouseEquipment.Count - sellable.Count,
                Pending = new Queue<InventoryEquipment>(sellable)
            };
            warehouseEquipmentSaleSession = session;
            message = $"captured {warehouseEquipment.Count} ordinary Warehouse equipment item(s), {sellable.Count} sellable";
            Plugin.DiagInfo($"WAREHOUSE SALE START|equipment={warehouseEquipment.Count}|sellable={sellable.Count}|unsellable={session.UnsellableSkipped}");
            return true;
        }
        catch (Exception error)
        {
            warehouseEquipmentSaleSession = null;
            message = $"{error.GetType().Name}: {error.GetBaseException().Message}";
            return false;
        }
    }

    internal static WarehouseEquipmentSaleOutcome AdvanceWarehouseEquipmentSale(
        WarehouseEquipmentSaleSession session)
    {
        if (!ReferenceEquals(warehouseEquipmentSaleSession, session))
            return FinishWarehouseEquipmentSale(session, false,
                "Warehouse sale session ownership changed before completion.");

        try
        {
            if (session.CancelRequested)
                return FinishWarehouseEquipmentSale(
                    session, false, "Warehouse sale was cancelled by the user.");

            var seasonData = Read(ReadStatic("Game", "dataMgr"), "nowSeasonData")
                             ?? throw new InvalidOperationException("Season data is unavailable.");
            if (!string.Equals(
                    NativeObjectKey(seasonData, seasonData),
                    session.SaveIdentity,
                    StringComparison.Ordinal))
                return FinishWarehouseEquipmentSale(session, false,
                    "The active save changed during Warehouse selling.");

            if (session.Pending.Count == 0)
                return FinishWarehouseEquipmentSale(session, false, null);

            var target = session.Pending.Peek();
            var sale = SellExactEquipmentFromSource(session.Known, session.Failed, target);
            if (sale.Sold == 1)
            {
                session.Sold++;
                session.Pending.Dequeue();
            }

            if (!string.IsNullOrWhiteSpace(sale.Error))
                return FinishWarehouseEquipmentSale(session, sale.Uncertain, sale.Error);
            if (sale.Sold != 1)
            {
                session.Failed.Add(target.Key);
                return FinishWarehouseEquipmentSale(session, true,
                    "One exact Warehouse item was not verified as sold exactly once.");
            }

            return new WarehouseEquipmentSaleOutcome(
                false, true, false,
                session.InitialEquipment,
                session.InitialSellable,
                session.Sold,
                session.UnsellableSkipped,
                session.Failed.Count,
                -1,
                session.Pending.Count,
                $"Warehouse 판매 중 · {session.Sold:N0}/{session.InitialSellable:N0}");
        }
        catch (Exception error)
        {
            return FinishWarehouseEquipmentSale(
                session, true, $"{error.GetType().Name}: {error.GetBaseException().Message}");
        }
    }

    internal static WarehouseEquipmentSaleOutcome AbortWarehouseEquipmentSale(
        WarehouseEquipmentSaleSession session,
        string reason)
        => FinishWarehouseEquipmentSale(session, false, reason);

    internal static void ReleaseWarehouseEquipmentSale(WarehouseEquipmentSaleSession session)
    {
        if (ReferenceEquals(warehouseEquipmentSaleSession, session))
            warehouseEquipmentSaleSession = null;
    }

    private static WarehouseEquipmentSaleOutcome FinishWarehouseEquipmentSale(
        WarehouseEquipmentSaleSession session,
        bool uncertain,
        string? error)
    {
        var remainingEquipment = -1;
        var remainingSellable = -1;
        try
        {
            var remaining = ReadOrdinaryWarehouseEquipment();
            remainingEquipment = remaining.Count;
            remainingSellable = remaining.Count(entry =>
                ReadBool(Read(Read(entry.ItemData, "tItemData"), "isSell")));
        }
        catch (Exception readError)
        {
            uncertain = true;
            error = string.IsNullOrWhiteSpace(error)
                ? $"Could not verify remaining Warehouse equipment: {readError.GetBaseException().Message}"
                : $"{error}; remaining Warehouse verification failed: {readError.GetBaseException().Message}";
        }

        if (ReferenceEquals(warehouseEquipmentSaleSession, session))
            warehouseEquipmentSaleSession = null;

        var success = !uncertain
                      && string.IsNullOrWhiteSpace(error)
                      && session.Failed.Count == 0
                      && remainingSellable == 0
                      && session.Sold == session.InitialSellable;
        var prefix = success ? "Warehouse 판매 완료" : "Warehouse 판매 중단";
        var message =
            $"{prefix} · 최초 장비 {session.InitialEquipment:N0} · 판매 가능 {session.InitialSellable:N0} · " +
            $"판매 확인 {session.Sold:N0} · 판매 불가 건너뜀 {session.UnsellableSkipped:N0} · " +
            $"현재 장비 {(remainingEquipment < 0 ? "확인 불가" : remainingEquipment.ToString("N0", CultureInfo.CurrentCulture))} · " +
            $"현재 판매 가능 {(remainingSellable < 0 ? "확인 불가" : remainingSellable.ToString("N0", CultureInfo.CurrentCulture))} · " +
            $"실패 {session.Failed.Count:N0}";
        if (!string.IsNullOrWhiteSpace(error)) message += $" · {error}";
        Plugin.DiagInfo($"WAREHOUSE SALE FINISH|success={success}|uncertain={uncertain}|initial={session.InitialEquipment}|sellable={session.InitialSellable}|sold={session.Sold}|unsellable={session.UnsellableSkipped}|remaining={remainingEquipment}|remaining-sellable={remainingSellable}|failed={session.Failed.Count}|error={error ?? string.Empty}");
        return new WarehouseEquipmentSaleOutcome(
            true,
            success,
            uncertain,
            session.InitialEquipment,
            session.InitialSellable,
            session.Sold,
            session.UnsellableSkipped,
            session.Failed.Count,
            remainingEquipment,
            remainingSellable,
            message);
    }

    private static List<InventoryEquipment> ReadOrdinaryWarehouseEquipment()
    {
        var dataManager = ReadStatic("Game", "dataMgr")
                          ?? throw new InvalidOperationException("Game.dataMgr is unavailable.");
        var seasonData = Read(dataManager, "nowSeasonData")
                         ?? throw new InvalidOperationException("No active save is loaded.");
        var townData = Read(seasonData, "townData")
                       ?? throw new InvalidOperationException("Town data is unavailable.");
        var result = new List<InventoryEquipment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var houseStoreData in ReadValues(Read(townData, "houseDic"))
                     .Select(house => Read(house, "houseStoreData"))
                     .Where(store => Read(store, "storeBaseData") is not null))
        {
            var storeBaseData = Read(houseStoreData, "storeBaseData");
            foreach (var page in ReadEntries(Read(storeBaseData, "storeDic"))
                         .Select(entry => Read(entry, "Value"))
                         .Where(page => page is not null))
            {
                foreach (var field in ReadList(page))
                {
                    var item = Read(field, "itemData");
                    if (!IsEquipmentItem(item)) continue;
                    var key = NativeObjectKey(item!, field);
                    if (seen.Add(key))
                        result.Add(new InventoryEquipment(key, item!, field));
                }
            }
        }
        return result;
    }
}
