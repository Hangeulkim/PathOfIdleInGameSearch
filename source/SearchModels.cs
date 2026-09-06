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
    public string StorageLabel { get; set; } = string.Empty;
    public StorageKind StorageKind { get; set; }
    public string AffixSummary { get; init; } = string.Empty;
    public string AffixSearchText { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SetName { get; init; } = string.Empty;
    public string SetJob { get; init; } = string.Empty;
    public string SetMembers { get; init; } = string.Empty;
    public string SetBonuses { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    public object ItemData { get; init; } = null!;
    public object? SourceField { get; set; }
    public object? GroupData { get; set; }
    public StorageSource StorageSource { get; set; }
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
    AutoBuild,
    Workshop
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

internal enum BulkEquipmentDestination
{
    Inventory,
    Warehouse,
    Sell
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
    public BulkEquipmentDestination EquipmentDestination { get; init; }
    public string Label { get; init; } = string.Empty;
    public string SaveIdentity { get; init; } = string.Empty;
    public int Initial { get; init; }
    public int Opened { get; set; }
    public int Stored { get; set; }
    public int Sold { get; set; }
    public bool CancelRequested { get; set; }
    public bool MutationAttempted { get; set; }
    public HashSet<string> KnownEquipment { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> FailedRouting { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> BlockedStacks { get; init; } = new(StringComparer.Ordinal);
}
internal sealed record InventoryEquipment(string Key, object ItemData, object SourceField);
internal sealed record AutoBuildSummary(string Hero, string Profile, string Status);
internal sealed record ForgeAttributeComparison(
    int EquipmentAttributeType,
    int BattleAttributeType,
    string Label,
    double PreForge,
    double Current,
    double Next);
internal sealed record EquipmentForgeCandidatePreview(
    string ItemKey,
    string ItemName,
    string Slot,
    int CurrentLevel,
    int NextLevel,
    int Price,
    double CurrentPerformance,
    double NextPerformance,
    double PerformanceGain,
    double Efficiency,
    IReadOnlyList<ForgeAttributeComparison> Attributes);
internal readonly record struct EquipmentForgeOptimizationResult(
    bool Succeeded,
    bool Cancelled,
    bool MutationApplied,
    string Message,
    EquipmentForgeCandidatePreview? BestCandidate);
internal sealed record MythicRefineCandidatePreview(
    string ItemKey,
    string ItemName,
    string StorageLabel,
    StorageSource Source,
    int DefinitionId,
    int MythicDefinitionId,
    int Level,
    int ForgeLevel,
    int MainAttributeValue,
    int DuplicateCount,
    int EligibleCount,
    double SuccessRatePercent,
    string CandidateSetToken = "");
internal readonly record struct MythicRefineResult(
    bool Succeeded,
    bool Cancelled,
    bool MutationAttempted,
    bool Upgraded,
    bool ItemDestroyed,
    bool Uncertain,
    string Message,
    MythicRefineCandidatePreview? Candidate,
    int AttemptCount = 0,
    int SuccessCount = 0,
    int FailureCount = 0,
    int SkippedCount = 0);
internal readonly record struct AutoBuildPlanningPreviewResult(
    bool Succeeded,
    bool Cancelled,
    string Message);
internal sealed record DeduplicatedEffectContribution(string Key, int Level, double Value, int DirectMatches);
internal sealed record EquipmentScoreBreakdown(
    double BaseItemPrior,
    double NativeAndAffixStats,
    double TextAffinity,
    double RelevantSpecialEffects,
    double DeduplicationAdjustment,
    double Total,
    int ActiveSpecialEffectCount,
    IReadOnlyList<string> InactiveSpecialEffects);
internal sealed record SelectableGearFacts(
    IReadOnlyList<DeduplicatedEffectContribution> DeduplicatedContributions,
    IReadOnlyDictionary<string, int> GrantedSkillLevels,
    IReadOnlyList<int> VariantIds,
    IReadOnlyList<int> NativeAbilityIds,
    IReadOnlySet<int> LegendMythWeaponFamily,
    IReadOnlySet<string> RelevantHardExclusiveKeys);
internal sealed record GearCandidate(
    ItemSearchRecord Record,
    string Key,
    string StableOrderKey,
    int Part,
    int SetId,
    int DefinitionId,
    int WeaponType,
    double Score,
    double NumericScore,
    double RawPower,
    int DirectMatches,
    int ThemeMatches,
    HashSet<string> NonStackingEffectKeys,
    SelectableGearFacts SelectableFacts,
    EquipmentScoreBreakdown ScoreBreakdown);
internal sealed record TeamCandidate(string Key, string Name, string Job, double Offense, double Defense, double Support, double Control, double Power, HashSet<string> Themes, string BuildHint);
internal sealed record TeamSuggestion(TeamCandidate A, TeamCandidate B, TeamCandidate C, double Score, string Reason);
