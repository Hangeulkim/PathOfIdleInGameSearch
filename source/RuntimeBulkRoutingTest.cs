#if PATHOFIDLE_RUNTIME_TEST
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace PathOfIdleInGameSearch;

// Command-driven one-box integration test. It is excluded from public builds
// and deliberately caps the destructive test at exactly one equipment box.
public sealed class RuntimeBulkRoutingTestHook : MonoBehaviour
{
    private const string RequestContents = "OPEN ONE EQUIPMENT BOX WITH VAULT OR SELL v1.1.8";
    private static readonly string RequestPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-bulk-routing.request");
    private static readonly string ResultPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-bulk-routing.result");
    private float nextProbeAt;
    private float armedAt;
    private bool armed;
    private BulkOpenSession? session;
    private HashSet<string> warehouseEquipmentBefore = new(StringComparer.Ordinal);

    public RuntimeBulkRoutingTestHook(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (session is not null)
        {
            var outcome = GameInventoryReader.AdvanceBulkOpen(session);
            if (!outcome.Finished) return;
            Complete(outcome);
            return;
        }

        if (Time.unscaledTime < nextProbeAt) return;
        nextProbeAt = Time.unscaledTime + 0.25f;
        if (!armed)
        {
            try
            {
                if (!File.Exists(RequestPath)
                    || !string.Equals(File.ReadAllText(RequestPath, Encoding.UTF8).Trim(),
                        RequestContents, StringComparison.Ordinal)) return;
                File.Delete(RequestPath);
                try { File.Delete(ResultPath); } catch { }
                armed = true;
                armedAt = Time.unscaledTime;
            }
            catch { return; }
        }

        if (!InGameSearchOverlay.TryGetRuntimeJointBuildTestQueueAvailability(out var readiness))
        {
            if (Time.unscaledTime - armedAt < 120f) return;
            Write(false, false, 0, 0, 0, -1, -1, -1,
                $"readiness timeout: {readiness}");
            Reset();
            return;
        }
        if (!GameInventoryReader.TryBeginSingleEquipmentBoxRoutingTest(
                out var started, out var reason))
        {
            Write(false, false, 0, 0, 0, -1, -1, -1, reason);
            Reset();
            return;
        }
        warehouseEquipmentBefore = GameInventoryReader.CaptureOrdinaryWarehouseEquipmentKeys();
        session = started;
    }

    private void Complete((bool Finished, bool Success, string Message) outcome)
    {
        var active = session!;
        var state = GameInventoryReader.CaptureSingleEquipmentBoxRoutingTestState(
            active, warehouseEquipmentBefore);
        var success = outcome.Success
                      && active.Opened == 1
                      && active.FailedRouting.Count == 0
                      && active.Stored + active.Sold > 0
                      && state.NewBagEquipment == 0
                      && state.WarehouseEquipment == 0
                      && state.SellQueueItems == 0;
        Write(success, false, active.Opened, active.Stored, active.Sold,
            active.FailedRouting.Count, state.NewBagEquipment,
            state.WarehouseEquipment, outcome.Message);
        Reset();
    }

    private static void Write(bool success, bool uncertain, int opened, int stored,
        int sold, int failed, int newBag, int warehouse, string message)
    {
        try
        {
            File.WriteAllText(ResultPath,
                $"success={success.ToString().ToLowerInvariant()}\n" +
                $"uncertain={uncertain.ToString().ToLowerInvariant()}\n" +
                $"opened={opened.ToString(CultureInfo.InvariantCulture)}\n" +
                $"stored={stored.ToString(CultureInfo.InvariantCulture)}\n" +
                $"sold={sold.ToString(CultureInfo.InvariantCulture)}\n" +
                $"failed={failed.ToString(CultureInfo.InvariantCulture)}\n" +
                $"new_bag_equipment={newBag.ToString(CultureInfo.InvariantCulture)}\n" +
                $"warehouse_equipment={warehouse.ToString(CultureInfo.InvariantCulture)}\n" +
                $"message={(message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ')}\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void Reset()
    {
        armed = false;
        session = null;
        armedAt = 0f;
        warehouseEquipmentBefore.Clear();
    }
}

internal static partial class GameInventoryReader
{
    internal readonly record struct RuntimeBulkRoutingState(
        int NewBagEquipment,
        int WarehouseEquipment,
        int SellQueueItems);

    internal static bool TryBeginSingleEquipmentBoxRoutingTest(
        out BulkOpenSession session,
        out string message)
    {
        session = null!;
        try
        {
            if (WarehouseEquipmentSaleActive)
            {
                message = "Warehouse equipment sale is active.";
                return false;
            }
            EnsureBulkSellQueueReadyAndEmpty();
            var seasonData = Read(ReadStatic("Game", "dataMgr"), "nowSeasonData")
                             ?? throw new InvalidOperationException("Season data is unavailable.");
            var stack = ReadBulkToolStacks()
                .Where(entry => entry.Kind == BulkToolKind.EquipmentBox && entry.Count > 0)
            // Exercise the highest-value route first. Low-quality boxes are
            // commonly consumed by the game's own filter before the plugin can
            // observe a reward, which previously produced a false no-op test.
            .OrderByDescending(entry => entry.Quality)
            .ThenBy(entry => entry.Count)
                .FirstOrDefault();
            if (stack is null)
            {
                message = "No equipment box is available for the one-box routing test.";
                return false;
            }
            session = new BulkOpenSession
            {
                Kind = BulkToolKind.EquipmentBox,
                QualityMask = QualityFilterLogic.BitFor(stack.Quality),
                AtLeast = false,
                EquipmentDestination = BulkEquipmentDestination.Sell,
                Label = "equipment box",
                SaveIdentity = NativeObjectKey(seasonData, seasonData),
                Initial = 1,
                KnownEquipment = new HashSet<string>(
                    ReadInventoryEquipment().Select(entry => entry.Key),
                    StringComparer.Ordinal)
            };
            message = $"one equipment box selected (quality={stack.Quality}, stack={stack.Count})";
            return true;
        }
        catch (Exception error)
        {
            message = $"{error.GetType().Name}: {error.GetBaseException().Message}";
            return false;
        }
    }

    internal static RuntimeBulkRoutingState CaptureSingleEquipmentBoxRoutingTestState(
        BulkOpenSession session,
        IReadOnlySet<string> warehouseEquipmentBefore)
    {
        var newBag = ReadInventoryEquipment().Count(entry =>
            !session.KnownEquipment.Contains(entry.Key));
        var warehouse = ReadOrdinaryWarehouseEquipment().Count(entry =>
            !warehouseEquipmentBefore.Contains(entry.Key));
        var sellContext = EnsureBulkSellQueueReadyAndEmpty();
        var queued = ReadBulkSellQueueItems(sellContext.Seller).Count;
        return new RuntimeBulkRoutingState(newBag, warehouse, queued);
    }

    internal static HashSet<string> CaptureOrdinaryWarehouseEquipmentKeys()
        => ReadOrdinaryWarehouseEquipment()
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
}
#endif
