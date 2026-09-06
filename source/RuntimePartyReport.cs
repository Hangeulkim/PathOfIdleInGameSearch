#if PATHOFIDLE_RUNTIME_TEST
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace PathOfIdleInGameSearch;

public sealed class RuntimePartyReportHook : MonoBehaviour
{
    private const string RequestContents = "EXPORT PARTY DATA v1.1.8";
    private const float ReadinessTimeoutSeconds = 120f;
    private static readonly string RequestPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-party-report.request");
    private static readonly string ResultPath = Path.Combine(
        Path.GetTempPath(), "PathOfIdleInGameSearch-v1.1.8-party-report.result.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private bool armed;
    private float armedAt;
    private float nextProbeAt;
    private Task? writerTask;
    private string lastReadiness = "request not armed";

    public RuntimePartyReportHook(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (writerTask is { IsCompleted: false }) return;
        if (writerTask is not null)
        {
            writerTask = null;
            armed = false;
            lastReadiness = "request not armed";
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
            lastReadiness = "waiting for nowSeasonData/lordData";
            try { File.Delete(RequestPath); } catch { }
            try { File.Delete(ResultPath); } catch { }
        }

        if (GameInventoryReader.TryCreateRuntimePartyReport(out var report, out lastReadiness))
        {
            QueueWrite(report);
            return;
        }

        if (Time.unscaledTime - armedAt < ReadinessTimeoutSeconds) return;
        QueueWrite(new Dictionary<string, object?>
        {
            ["schemaVersion"] = "PathOfIdleInGameSearch.party-report.v1",
            ["pluginVersion"] = Plugin.PluginVersion,
            ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["success"] = false,
            ["readOnly"] = true,
            ["error"] = "readiness timeout",
            ["lastReadiness"] = lastReadiness,
            ["timeoutSeconds"] = ReadinessTimeoutSeconds
        });
    }

    [HideFromIl2Cpp]
    private void QueueWrite(Dictionary<string, object?> payload)
    {
        // The snapshot contains managed primitives only. Keep JSON formatting
        // and disk I/O off Unity's main thread without moving IL2CPP reads there.
        writerTask = Task.Run(() =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                File.WriteAllText(ResultPath, json, new UTF8Encoding(false));
            }
            catch
            {
                // This bounded diagnostic intentionally emits no persistent log.
            }
        });
    }
}

internal static partial class GameInventoryReader
{
    internal static bool TryCreateRuntimePartyReport(
        out Dictionary<string, object?> report,
        out string readiness)
    {
        report = new Dictionary<string, object?>();
        readiness = string.Empty;
        try
        {
            var dataManager = ReadStatic("Game", "dataMgr");
            var seasonData = Read(dataManager, "nowSeasonData");
            var lordData = Read(seasonData, "lordData");
            if (seasonData is null || lordData is null)
            {
                readiness = "nowSeasonData/lordData is not loaded";
                return false;
            }

            var heroFields = ReadList(Read(lordData, "heroFieldList"));
            var loadedHeroes = heroFields
                .Select(field => new { Field = field, Hero = Read(field, "heroData") })
                .Where(entry => entry.Hero is not null)
                .ToList();
            if (loadedHeroes.Count < 12)
            {
                readiness = $"waiting for the 12-hero roster ({loadedHeroes.Count}/12 loaded)";
                return false;
            }

            foreach (var entry in loadedHeroes)
            {
                if (Read(entry.Hero, "saveHeroData") is null
                    || Read(entry.Hero, "heroTalentData") is null
                    || Read(entry.Hero, "heroEquipData") is null)
                {
                    readiness = "a hero save/talent/equipment object is still loading";
                    return false;
                }
            }

            var jobs = TableRows("THeroJobDict");
            var skills = TableRows("TSkillDict");
            var talents = TableRows("TTalentDict");
            var equipment = TableRows("TEquipDict");
            if (jobs.Count == 0 || skills.Count == 0 || talents.Count == 0 || equipment.Count == 0)
            {
                readiness = "recommendation tables are not initialized";
                return false;
            }

            var heroReports = loadedHeroes
                .Select(entry => BuildHeroReport(entry.Hero!, entry.Field))
                .OrderBy(entry => Convert.ToInt32(entry["fieldIndex"] ?? int.MaxValue, CultureInfo.InvariantCulture))
                .ThenBy(entry => Convert.ToInt32(entry["uid"] ?? int.MaxValue, CultureInfo.InvariantCulture))
                .ToList();
            var warnings = new List<string>();
            if (heroReports.Count != 12)
                warnings.Add($"Expected the current 12-hero roster, but observed {heroReports.Count} loaded heroes.");

            report = new Dictionary<string, object?>
            {
                ["schemaVersion"] = "PathOfIdleInGameSearch.party-report.v1",
                ["pluginVersion"] = Plugin.PluginVersion,
                ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["success"] = true,
                ["readOnly"] = true,
                ["expectedHeroCount"] = 12,
                ["actualHeroCount"] = heroReports.Count,
                ["warnings"] = warnings,
                ["season"] = new Dictionary<string, object?>
                {
                    ["seasonId"] = Int(seasonData, "seasonId"),
                    ["saveSlotId"] = Int(seasonData, "saveSlotId")
                },
                ["heroes"] = heroReports,
                ["adventureParties"] = BuildAdventureParties(seasonData),
                ["workshop"] = BuildWorkshopReport(seasonData),
                ["ownedEquipmentSets"] = BuildOwnedEquipmentSetSummary(),
                ["catalogs"] = new Dictionary<string, object?>
                {
                    ["jobs"] = jobs.Select(BuildJobCatalogRow).OrderBy(RowId).ToList(),
                    ["builds"] = TableRows("TBuildsDict").Select(BuildBuildCatalogRow).OrderBy(RowId).ToList(),
                    ["skillTags"] = TableRows("TSkillTagDict").Select(BuildSkillTagCatalogRow).OrderBy(RowId).ToList(),
                    ["skills"] = skills.Select(BuildSkillCatalogRow).OrderBy(RowId).ToList(),
                    ["talents"] = talents.Select(BuildTalentCatalogRow).OrderBy(RowId).ToList(),
                    ["masteries"] = TableRows("TMasteryDict").Select(BuildMasteryCatalogRow).OrderBy(RowId).ToList(),
                    ["equipment"] = equipment.Select(BuildEquipmentCatalogRow).OrderBy(RowId).ToList(),
                    ["sets"] = TableRows("TEquipSetsDict").Select(BuildSetCatalogRow).OrderBy(RowId).ToList(),
                    ["setEffects"] = TableRows("TEquipSetsEffectDict").Select(BuildSetEffectCatalogRow).OrderBy(RowId).ToList(),
                    // These three tables make the exported mastery, equipment,
                    // and set-effect IDs independently interpretable.
                    ["abilities"] = TableRows("TAbilityDict").Select(BuildAbilityCatalogRow).OrderBy(RowId).ToList(),
                    ["affixes"] = TableRows("TAffixDict").Select(BuildAffixCatalogRow).OrderBy(RowId).ToList(),
                    ["miniDescriptions"] = TableRows("TMiniDesDict").Select(BuildMiniDescriptionCatalogRow).OrderBy(RowId).ToList(),
                    ["heroQualities"] = TableRows("THeroQualityDict").Select(BuildHeroQualityCatalogRow).OrderBy(RowId).ToList(),
                    // These are the authoritative common-object tables used by
                    // the game's action -> produce -> summon path. Exporting
                    // them keeps Minion, Ally and Totem diagnosis structural;
                    // tag/name text remains only a final fallback.
                    ["attributes"] = TableRows("TAttrDict").Select(BuildAttributeCatalogRow).OrderBy(RowId).ToList(),
                    ["actions"] = TableRows("TActionDict").Select(BuildActionCatalogRow).OrderBy(RowId).ToList(),
                    ["emitters"] = TableRows("TEmitterDict").Select(BuildEmitterCatalogRow).OrderBy(RowId).ToList(),
                    ["produces"] = TableRows("TProduceDict").Select(BuildProduceCatalogRow).OrderBy(RowId).ToList(),
                    ["summons"] = TableRows("TSummonDict").Select(BuildSummonCatalogRow).OrderBy(RowId).ToList()
                }
            };
            readiness = "party report snapshot created";
            return true;
        }
        catch (Exception error)
        {
            readiness = $"snapshot failed: {error.GetType().Name}: {error.Message}";
            report = new Dictionary<string, object?>();
            return false;
        }
    }

    private static Dictionary<string, object?> BuildHeroReport(object hero, object field)
    {
        var save = Read(hero, "saveHeroData");
        var job = Read(hero, "tHeroJobData");
        var talentData = Read(hero, "heroTalentData");
        var equipData = Read(hero, "heroEquipData");
        var baseSkill = InvokeInstance(hero, "GetNowBaseSkillData");
        var activeSkills = ReadList(InvokeInstance(talentData!, "GetSkillList"));
        var baseSkillId = Int(Read(baseSkill, "tSkillData"), "id");
        var runtimeTalents = BuildRuntimeTalents(talentData);
        var selectedHero = GetSelectedHero();
        var transformableTalents = ReadValues(Read(talentData, "talentDic"))
            .Where(talent => IsTransformableSkillDefinition(Read(talent, "tTalentData")))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["uid"] = Int(save, "uniqueId"),
            ["name"] = Text(save, "name"),
            ["jobId"] = Int(save, "jobId"),
            ["jobName"] = FirstNonEmpty(Text(hero, "jobName"), Text(job, "name")),
            ["jobNameEnglish"] = English(job, Text(job, "name")),
            ["level"] = Int(save, "level"),
            ["quality"] = Int(save, "quality"),
            ["qualityName"] = Text(Read(hero, "tHeroQualityData"), "name"),
            ["selected"] = selectedHero is not null && SameHeroIdentity(hero, selectedHero),
            ["configuredTheme"] = GetHeroBuildTheme(hero),
            ["talentRemainPoint"] = Int(save, "talentRemainPoint"),
            ["savedTalentPointTotal"] = runtimeTalents.Sum(row =>
                Convert.ToInt32(row.GetValueOrDefault("saved") ?? 0, CultureInfo.InvariantCulture)),
            ["transformableSlotCount"] = transformableTalents.Count,
            ["unlockedTransformableSlotCount"] = transformableTalents.Count(talent =>
            {
                try { return !IsTalentLockedRequired(talent); }
                catch { return false; }
            }),
            ["advIndex"] = Int(save, "advIndex", -1),
            ["fieldIndex"] = Int(save, "fieldIndex", Int(Read(field, "saveHeroFieldData"), "index", -1)),
            ["fieldRuntimeAdvIndex"] = Int(field, "advIndex", -1),
            ["baseSkill"] = BuildRuntimeSkill(baseSkill, "base"),
            ["activeSkills"] = activeSkills
                .Where(skill => Int(Read(skill, "tSkillData"), "id") != baseSkillId)
                .GroupBy(skill => Int(Read(skill, "tSkillData"), "id"))
                .Select(group => BuildRuntimeSkill(group.First(), "active"))
                .OrderBy(RowId).ToList(),
            ["conditionalSkillProbes"] = BuildConditionalSkillProbes(hero, save),
            ["talents"] = runtimeTalents,
            ["gear"] = BuildRuntimeGear(hero),
            ["activeWeaponSlot"] = Int(equipData, "nowWeaponIndex"),
            ["activeSetEffects"] = BuildRuntimeSetEffects(equipData),
            ["abilities"] = BuildRuntimeAbilities(hero),
            ["baseAttributes"] = BuildHeroAttributes(hero, save, job)
        };
    }

    private static List<Dictionary<string, object?>> BuildConditionalSkillProbes(object hero, object? save)
    {
        var jobId = Int(save, "jobId");
        var attrData = Read(hero, "attrData");
        if (jobId <= 0 || attrData is null) return new List<Dictionary<string, object?>>();
        return TableRows("TSkillDict")
            .Where(row => Int(row, "job") == jobId)
            .Select(row =>
            {
                var id = Int(row, "id");
                var info = InvokeStatic("TableData", "getTSkillInfoData", Int(row, "infoId"));
                return new { Row = row, Id = id, Info = info, Condition = Text(info, "castCondition") };
            })
            .Where(entry => entry.Id > 0 && !string.IsNullOrWhiteSpace(entry.Condition))
            .Select(entry =>
            {
                var controlledEntityKinds = GameInventoryReader
                    .GetRuntimeControlledEntityKinds(entry.Id);
                var preview = InvokeStaticMany("SkillData", "CreatePreview", entry.Id, 1, attrData);
                var check = preview is null ? null : InvokeInstance(preview, "CheckSpecialCastCondition");
                return new Dictionary<string, object?>
                {
                    ["id"] = entry.Id,
                    ["name"] = Text(entry.Row, "name"),
                    ["nameEnglish"] = English(entry.Row, Text(entry.Row, "name")),
                    ["castCondition"] = entry.Condition,
                    ["tags"] = IntArray(Read(entry.Info, "tagArr")),
                    ["controlledEntityKinds"] = controlledEntityKinds,
                    ["isSummonCategory"] = controlledEntityKinds.Count > 0,
                    ["previewCreated"] = preview is not null,
                    ["nativeCheckAvailable"] = check is not null,
                    ["nativeCheckResult"] = check is null ? null : Bool(check),
                    ["parsedConditionCount"] = preview is null ? 0 : ReadList(Read(preview, "conditionList")).Count
                };
            })
            .OrderBy(RowId)
            .ToList();
    }

    private static Dictionary<string, object?> BuildRuntimeSkill(object? skill, string source)
    {
        var definition = Read(skill, "tSkillData");
        var localized = Text(definition, "name");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(definition, "id"),
            ["name"] = localized,
            ["nameEnglish"] = English(definition, localized),
            ["level"] = Int(skill, "level"),
            ["isVariant"] = Bool(Read(skill, "isVariant")),
            ["source"] = source
        };
    }

    private static List<Dictionary<string, object?>> BuildRuntimeTalents(object? heroTalentData)
    {
        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dictionary = Read(heroTalentData, "talentDic");
        var entries = ReadEntries(dictionary);
        if (entries.Count > 0)
        {
            foreach (var entry in entries)
            {
                var talent = Read(entry, "Value");
                if (talent is not null)
                    Add(talent, "grid", IntRaw(Read(entry, "Key")));
            }
        }
        else
        {
            foreach (var talent in ReadValues(dictionary)) Add(talent, "grid", 0);
        }

        var extraIndex = 0;
        foreach (var talent in ReadList(Read(heroTalentData, "extraTalentList")))
            Add(talent, "extra", extraIndex++);
        return result.OrderBy(row => Convert.ToInt32(row["id"] ?? 0, CultureInfo.InvariantCulture))
            .ThenBy(row => row["source"]?.ToString(), StringComparer.Ordinal).ToList();

        void Add(object talent, string source, int sourceIndex)
        {
            var pointer = Read(talent, "Pointer")?.ToString();
            var definition = Read(talent, "tTalentData");
            var save = Read(talent, "saveTalentData");
            var id = Int(definition, "id", Int(save, "id"));
            var key = !string.IsNullOrWhiteSpace(pointer)
                ? $"talent:{pointer}"
                : $"{source}:{sourceIndex}:{id}";
            if (!seen.Add(key)) return;
            var skillId = Int(definition, "skillId");
            var masteryId = Int(definition, "masteryId");
            var name = TalentName(definition, skillId, masteryId);
            result.Add(new Dictionary<string, object?>
            {
                ["stableKey"] = key,
                ["id"] = id,
                ["name"] = name.Localized,
                ["nameEnglish"] = name.English,
                ["type"] = Int(definition, "type"),
                ["miniType"] = Int(definition, "miniType"),
                ["jobId"] = Int(definition, "jobId"),
                ["advancedId"] = Int(definition, "advancedId"),
                ["skillId"] = skillId,
                ["masteryId"] = masteryId,
                ["saved"] = SafeInt(() => GetSavedTalentLevel(talent), Int(save, "level")),
                ["effective"] = SafeInt(() => GetTalentLevel(talent), Int(save, "level")),
                ["cap"] = SafeInt(() => GetTalentLevelCap(talent), 0),
                ["base"] = SafeInt(() => GetTalentBaseLevelRequired(talent), Int(talent, "baseLevel")),
                ["floor"] = Int(definition, "floor"),
                ["index"] = Int(definition, "index"),
                ["source"] = source,
                ["sourceIndex"] = sourceIndex,
                ["fixed"] = Bool(Read(save, "isFixed")),
                ["alien"] = Bool(Read(save, "isAlien")),
                ["inspired"] = Bool(Read(save, "isInspired")),
                ["runeWords"] = Bool(Read(talent, "isRuneWords"))
            });
        }
    }

    private static List<Dictionary<string, object?>> BuildRuntimeGear(object hero)
    {
        var result = new List<Dictionary<string, object?>>(8);
        foreach (var slot in GetGearSlots())
        {
            var item = GetEquippedItem(hero, slot.Part, slot.MainWeapon);
            var row = new Dictionary<string, object?>
            {
                ["slot"] = slot.Label,
                ["part"] = slot.Part,
                ["mainWeapon"] = slot.MainWeapon,
                ["weaponSlotIndex"] = slot.WeaponSlotIndex,
                ["occupied"] = item is not null
            };
            if (item is not null)
            {
                try
                {
                    var save = Read(item, "saveItemData");
                    var equip = Read(item, "itemEquipData");
                    var definition = Read(equip, "tEquipData");
                    var set = Read(equip, "tEquipSetsData");
                    var described = DescribeItem(
                        item, $"Equipped {slot.Label}", StorageKind.Inventory,
                        StorageSource.Equipped, item);
                    row["stableKey"] = NativeObjectKey(item, item);
                    row["definitionId"] = Int(definition, "id", Int(save, "id"));
                    row["name"] = described.Name;
                    row["nameEnglish"] = English(definition, described.Name);
                    row["quality"] = described.Quality;
                    row["qualityName"] = described.QualityLabel;
                    row["level"] = described.Level;
                    row["forgeLevel"] = Int(save, "forgeLevel");
                    row["savedMainAttributeValue"] = Int(save, "mainAttrValue");
                    row["forgeComparison"] = BuildForgeComparison(equip, save);
                    row["forgePrice"] = ToInt(InvokeInstance(equip!, "GetForgePrice") ?? 0);
                    var forgeTable = Read(equip, "tEquipForgeData");
                    row["forgeRule"] = new Dictionary<string, object?>
                    {
                        ["id"] = Int(forgeTable, "id"),
                        ["priceRate"] = Number(Read(forgeTable, "priceRate")) ?? 0d,
                        ["runeQuality"] = Int(forgeTable, "runeQuality"),
                        ["runeCount"] = Int(forgeTable, "runeCount")
                    };
                    row["nextForgePreview"] = BuildNextForgePreview(item, equip!, save);
                    row["setId"] = Int(set, "id", Int(definition, "setsId"));
                    row["setName"] = described.SetName;
                    row["affixSummary"] = described.AffixSummary;
                    row["description"] = described.Description;
                    row["setBonuses"] = described.SetBonuses;
                }
                catch (Exception error)
                {
                    row["stableKey"] = NativeObjectKey(item, item);
                    row["error"] = $"{error.GetType().Name}: {error.Message}";
                }
            }
            result.Add(row);
        }

        // GetGearSlots is the canonical eight-slot contract. Preserve exactly
        // eight positions even if a future game update makes one getter fail.
        while (result.Count < 8)
            result.Add(new Dictionary<string, object?>
            {
                ["slot"] = $"unknown-{result.Count}", ["occupied"] = false
            });
        return result.Take(8).ToList();
    }

    private static List<Dictionary<string, object?>> BuildForgeComparison(
        object? equip,
        object? save)
    {
        var result = new List<Dictionary<string, object?>>();
        var equipAttr = Read(equip, "equipAttrData");
        if (equip is null || equipAttr is null) return result;
        var forgeLevel = Math.Max(0, Int(save, "forgeLevel"));
        foreach (var mapping in GetEquipAttrMappings())
        {
            var equipType = CreateEnum("EEquipAttrType", mapping.EquipType);
            if (equipType is null) continue;
            var current = Number(InvokeInstance(equipAttr, "GetAttrValue", equipType, null!)) ?? 0d;
            var preForge = forgeLevel > 0
                ? Number(InvokeInstance(equip, "GetCoreAttrValueWithoutForge", equipType)) ?? 0d
                : current;
            if (Math.Abs(current) <= double.Epsilon
                && Math.Abs(preForge) <= double.Epsilon) continue;
            result.Add(new Dictionary<string, object?>
            {
                ["equipmentAttributeType"] = mapping.EquipType,
                ["battleAttributeType"] = mapping.BattleAttrType,
                ["current"] = current,
                ["preForge"] = preForge,
                ["forgeDelta"] = current - preForge
            });
        }
        return result;
    }

    private static Dictionary<string, object?> BuildNextForgePreview(
        object item,
        object equip,
        object? save)
    {
        var result = new Dictionary<string, object?>
        {
            ["beforeLevel"] = Int(save, "forgeLevel"),
            ["price"] = ToInt(InvokeInstance(equip, "GetForgePrice") ?? 0)
        };
        try
        {
            var clonedSave = InvokeRequiredInstance(
                save ?? throw new InvalidOperationException("SaveItemData is unavailable."),
                "Clone") ?? throw new InvalidOperationException("SaveItemData.Clone returned null.");
            var posType = Read(item, "posType") ?? CreateEnum("EItemPosType", 0)
                          ?? throw new InvalidOperationException("EItemPosType.none is unavailable.");
            var clonedItem = InvokeRequiredStaticMany(
                "ItemData", "Create", clonedSave, posType, -1)
                             ?? throw new InvalidOperationException("ItemData.Create returned null.");
            var clonedEquip = Read(clonedItem, "itemEquipData")
                              ?? throw new InvalidOperationException("Cloned ItemEquipData is unavailable.");
            InvokeRequiredInstance(clonedEquip, "AddForgeLevel", 1);
            var clonedRuntimeSave = Read(clonedItem, "saveItemData");
            result["afterLevel"] = Int(clonedRuntimeSave, "forgeLevel");
            result["attributes"] = BuildForgeStepComparison(equip, clonedEquip);
        }
        catch (Exception error)
        {
            result["error"] = $"{error.GetType().Name}: {error.GetBaseException().Message}";
        }
        return result;
    }

    private static List<Dictionary<string, object?>> BuildForgeStepComparison(
        object currentEquip,
        object nextEquip)
    {
        var result = new List<Dictionary<string, object?>>();
        var currentAttr = Read(currentEquip, "equipAttrData");
        var nextAttr = Read(nextEquip, "equipAttrData");
        if (currentAttr is null || nextAttr is null) return result;
        foreach (var mapping in GetEquipAttrMappings())
        {
            var equipType = CreateEnum("EEquipAttrType", mapping.EquipType);
            if (equipType is null) continue;
            var current = Number(InvokeInstance(currentAttr, "GetAttrValue", equipType, null!)) ?? 0d;
            var next = Number(InvokeInstance(nextAttr, "GetAttrValue", equipType, null!)) ?? 0d;
            if (Math.Abs(current) <= double.Epsilon
                && Math.Abs(next) <= double.Epsilon) continue;
            result.Add(new Dictionary<string, object?>
            {
                ["equipmentAttributeType"] = mapping.EquipType,
                ["battleAttributeType"] = mapping.BattleAttrType,
                ["before"] = current,
                ["after"] = next,
                ["delta"] = next - current
            });
        }
        return result;
    }

    private static Dictionary<string, object?> BuildWorkshopReport(object seasonData)
    {
        var townData = Read(seasonData, "townData");
        var workType = CreateEnum("EHouseType", 103);
        var workHouse = townData is null || workType is null
            ? null
            : InvokeInstance(townData, "GetHouse", workType);
        var houseAttr = Read(workHouse, "houseAttrData");

        double HouseAttr(int id)
        {
            if (houseAttr is null) return 0d;
            var attrType = CreateEnum("EHouseAttrType", id);
            return attrType is null
                ? 0d
                : Number(InvokeInstance(houseAttr, "GetAttrValue", attrType, null!)) ?? 0d;
        }

        var equipment = ReadAllEquipmentSources(true);
        var houseWork = Read(workHouse, "houseWorkData");
        var workFields = ReadList(Read(houseWork, "fieldList"));
        var goldType = CreateEnum("EResType", 1);
        var oneGoldPrice = goldType is null
            ? null
            : InvokeStaticMany("PriceData", "CreateRes", goldType, 1);
        var impossibleGoldPrice = goldType is null
            ? null
            : InvokeStaticMany("PriceData", "CreateRes", goldType, int.MaxValue);
        var selectedHero = GetSelectedHero();
        var selectedForgeSlotChecks = selectedHero is null || houseWork is null || workFields.Count != 1
            ? new List<Dictionary<string, object?>>()
            : GetGearSlots().Select(slot =>
            {
                var source = GetEquipDestinationField(selectedHero, slot);
                var item = Read(source, "itemData");
                return new Dictionary<string, object?>
                {
                    ["slot"] = slot.Label,
                    ["occupied"] = item is not null,
                    ["checkAddItem"] = source is null || item is null
                        ? int.MinValue
                        : ToInt(InvokeInstance(houseWork, "CheckAddItem", source, workFields[0])
                                ?? int.MinValue)
                };
            }).ToList();
        var sourceRows = equipment
            .GroupBy(record => record.StorageSource)
            .OrderBy(group => (int)group.Key)
            .Select(group => new Dictionary<string, object?>
            {
                ["source"] = group.Key.ToString(),
                ["equipment"] = group.Count(),
                ["forged"] = group.Count(record =>
                    Int(Read(record.ItemData, "saveItemData"), "forgeLevel") > 0),
                ["legendWithMythTarget"] = group.Count(record =>
                    IsTableEligibleMythRefineCandidate(record)),
                ["socketedLegendWithMythTarget"] = group.Count(record =>
                    IsTableEligibleMythRefineCandidate(record, ignoreSockets: true)
                    && ReadList(Read(Read(record.ItemData, "itemEquipData"), "slotRuneList")).Count > 0)
            }).ToList();

        return new Dictionary<string, object?>
        {
            ["loaded"] = workHouse is not null,
            ["locked"] = workHouse is null || Bool(InvokeInstance(workHouse, "IsLock")),
            ["forgeUnlocked"] = HouseAttr(10300) > 0d,
            ["forgeLevelMax"] = HouseAttr(10313),
            ["refineUnlocked"] = HouseAttr(10302) > 0d,
            ["mythRefineUnlocked"] = HouseAttr(10340) > 0d,
            ["mythRefineRate"] = HouseAttr(10341),
            ["nativeMythEquipmentOpen"] = Bool(InvokeStaticMany("EquipSys", "IsMythEquipOpen")),
            ["houseWorkLoaded"] = houseWork is not null,
            ["houseWorkFieldCount"] = workFields.Count,
            ["houseWorkOccupiedFields"] = workFields.Count(field => Read(field, "itemData") is not null),
            ["houseWorkHasForgeData"] = Read(houseWork, "workForgeData") is not null,
            ["houseWorkHasRefineData"] = Read(houseWork, "workRefineData") is not null,
            ["oneGoldPriceCheck"] = ToInt(InvokeInstance(oneGoldPrice!, "CheckEnough") ?? int.MinValue),
            ["impossibleGoldPriceCheck"] = ToInt(InvokeInstance(impossibleGoldPrice!, "CheckEnough") ?? int.MinValue),
            ["selectedForgeSlotChecks"] = selectedForgeSlotChecks,
            ["sourceSummary"] = sourceRows
        };
    }

    private static bool IsTableEligibleMythRefineCandidate(
        ItemSearchRecord record,
        bool ignoreSockets = false)
    {
        if (record.Quality != 4) return false;
        var equip = Read(record.ItemData, "itemEquipData");
        var definition = Read(equip, "tEquipData");
        if (Int(definition, "upMyth") <= 0) return false;
        return ignoreSockets || ReadList(Read(equip, "slotRuneList")).Count == 0;
    }

    private static List<Dictionary<string, object?>> BuildRuntimeSetEffects(object? equipData)
    {
        return ReadList(Read(equipData, "activeSetsEffectList")).Select(effect =>
        {
            var row = Read(effect, "tSetEffectData");
            var setId = Int(row, "sesId");
            var set = setId > 0 ? InvokeStatic("TableData", "getTEquipSetsData", setId) : null;
            var localized = Text(row, "des");
            return new Dictionary<string, object?>
            {
                ["id"] = Int(row, "id"),
                ["setId"] = setId,
                ["setName"] = Text(set, "name"),
                ["pieces"] = Int(effect, "wearCount"),
                ["description"] = localized,
                ["descriptionEnglish"] = EnglishDescription(row, localized),
                ["abilityId"] = Int(row, "abilityId"),
                ["abilityParameters"] = IntArray(Read(row, "abilityParam"))
            };
        }).OrderBy(RowId).ToList();
    }

    private static List<Dictionary<string, object?>> BuildRuntimeAbilities(object hero)
    {
        return ReadList(Read(hero, "abilityList")).Select(ability =>
        {
            var definition = Read(ability, "tAbilityData");
            var localized = Text(definition, "name");
            return new Dictionary<string, object?>
            {
                ["id"] = Int(definition, "id", Int(ability, "id")),
                ["name"] = localized,
                ["nameEnglish"] = English(definition, localized),
                ["level"] = Int(ability, "level", Int(definition, "level")),
                ["description"] = Text(definition, "des"),
                ["descriptionEnglish"] = EnglishDescription(definition, Text(definition, "des"))
            };
        }).OrderBy(RowId).ToList();
    }

    private static Dictionary<string, object?> BuildHeroAttributes(object hero, object? save, object? job)
    {
        var quality = Read(hero, "tHeroQualityData");
        return new Dictionary<string, object?>
        {
            ["savedMain"] = AttributeDictionary(Read(save, "mainAttrDic")),
            ["savedMainGrowth"] = AttributeDictionary(Read(save, "mainAttrUpDic")),
            ["savedBase"] = AttributeDictionary(Read(save, "baseAttrDic")),
            ["savedBaseGrowth"] = AttributeDictionary(Read(save, "baseAttrUpDic")),
            ["currentFinalNonZero"] = CurrentAttributes(Read(hero, "attrData")),
            ["jobMainScope"] = NumericMatrix(Read(job, "mainScopeArr"), "int", 3),
            ["jobBaseScope"] = NumericMatrix(Read(job, "baseScopeArr"), "int", 3),
            ["jobConstantAttributes"] = NumericMatrix(Read(job, "constAttrArr"), "float", 2),
            ["qualityRules"] = new Dictionary<string, object?>
            {
                ["id"] = Int(quality, "id"),
                ["name"] = Text(quality, "name"),
                ["mainAttrTotal"] = Int(quality, "mainAttrTotal"),
                ["mainAttrGrowth"] = Int(quality, "mainAttrGrow"),
                ["baseAttrTotal"] = Int(quality, "baseAttrTotal"),
                ["baseAttrGrowth"] = Int(quality, "baseAttrGrow"),
                ["talentPoint"] = Int(quality, "talentPoint"),
                ["legendCount"] = Int(quality, "legendCount")
            }
        };
    }

    private static List<Dictionary<string, object?>> BuildAdventureParties(object seasonData)
    {
        var advData = Read(seasonData, "advData");
        return ReadList(Read(advData, "advFieldList")).Select(field =>
        {
            var save = Read(field, "saveAdvFieldData");
            var slots = ReadList(Read(field, "heroFieldList")).Select(heroField =>
            {
                var hero = Read(heroField, "heroData");
                var heroSave = Read(hero, "saveHeroData");
                return new Dictionary<string, object?>
                {
                    ["slotIndex"] = Int(Read(heroField, "saveHeroFieldData"), "index", -1),
                    ["locked"] = Bool(Read(heroField, "isLocked")),
                    ["uid"] = hero is null ? null : Int(heroSave, "uniqueId"),
                    ["name"] = hero is null ? null : Text(heroSave, "name"),
                    ["jobId"] = hero is null ? null : Int(heroSave, "jobId"),
                    ["jobName"] = hero is null ? null : FirstNonEmpty(Text(hero, "jobName"), Text(Read(hero, "tHeroJobData"), "name"))
                };
            }).OrderBy(row => Convert.ToInt32(row["slotIndex"] ?? int.MaxValue, CultureInfo.InvariantCulture)).ToList();
            return new Dictionary<string, object?>
            {
                ["index"] = Int(save, "index"),
                ["adventureType"] = EnumSnapshot(Read(save, "advType")),
                ["state"] = EnumSnapshot(Read(save, "advFieldState")),
                ["battleEndType"] = EnumSnapshot(Read(save, "battleEndType")),
                ["battleTime"] = Number(Read(save, "battleTime")),
                ["killCount"] = Int(save, "killCount"),
                ["chapterId"] = Int(save, "nowChapterId"),
                ["levelId"] = Int(save, "nowLevelId"),
                ["auto"] = Bool(Read(save, "isAuto")),
                ["slots"] = slots
            };
        }).OrderBy(row => Convert.ToInt32(row["index"] ?? 0, CultureInfo.InvariantCulture)).ToList();
    }

    private static Dictionary<string, object?> BuildJobCatalogRow(object row)
    {
        var localized = Text(row, "name");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized,
            ["nameEnglish"] = English(row, localized), ["description"] = Text(row, "des"),
            ["baseWeaponTypes"] = IntArray(Read(row, "baseWeaponTypeArr")),
            ["baseArmorType"] = Int(row, "baseArmorType"),
            ["mainScope"] = NumericMatrix(Read(row, "mainScopeArr"), "int", 3),
            ["baseScope"] = NumericMatrix(Read(row, "baseScopeArr"), "int", 3),
            ["constantAttributes"] = NumericMatrix(Read(row, "constAttrArr"), "float", 2),
            ["actions"] = IntArray(Read(row, "actionArr"))
        };
    }

    private static Dictionary<string, object?> BuildBuildCatalogRow(object row)
    {
        var localized = Text(row, "name");
        var description = Text(row, "des");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized,
            ["nameEnglish"] = English(row, localized), ["description"] = description,
            ["descriptionEnglish"] = EnglishDescription(row, description),
            ["jobId"] = Int(row, "jobId"), ["index"] = Int(row, "index"),
            ["type"] = Int(row, "type"), ["difficultyType"] = Text(row, "diffType"),
            ["locked"] = Bool(Read(row, "isLock")),
            ["skillIds"] = IntArray(Read(row, "skillArr")),
            ["masteryIds"] = IntArray(Read(row, "masteryArr")),
            ["equipmentIds"] = IntArray(Read(row, "equipArr")),
            ["runewords"] = NumericMatrix(Read(row, "runewordArr"), "int", 2)
        };
    }

    private static Dictionary<string, object?> BuildSkillCatalogRow(object row)
    {
        var id = Int(row, "id");
        var infoId = Int(row, "infoId");
        var info = infoId > 0 ? InvokeStatic("TableData", "getTSkillInfoData", infoId) : null;
        var localized = Text(row, "name");
        var description = Text(info, "des");
        var controlledEntityKinds = GameInventoryReader.GetRuntimeControlledEntityKinds(id);
        return new Dictionary<string, object?>
        {
            ["id"] = id, ["name"] = localized, ["nameEnglish"] = English(row, localized),
            ["type"] = Int(row, "type"), ["jobId"] = Int(row, "job"),
            ["index"] = Int(row, "index"), ["castDistance"] = Number(Read(row, "castDis")),
            ["weaponTypes"] = IntArray(Read(row, "weaponArr")),
            ["infoId"] = infoId, ["variantInfoId"] = Int(row, "variantlInfoId"),
            ["description"] = description, ["descriptionEnglish"] = EnglishDescription(info, description),
            ["actionId"] = Int(info, "actionId"), ["levelId"] = Int(info, "levelId"),
            ["tags"] = IntArray(Read(info, "tagArr")), ["otherParameters"] = IntArray(Read(info, "otherParam")),
            ["controlledEntityKinds"] = controlledEntityKinds,
            ["isSummonCategory"] = controlledEntityKinds.Count > 0,
            ["castCondition"] = Text(info, "castCondition"),
            ["keywords"] = NumericMatrix(Read(info, "keywordArr"), "int", 2),
            ["infoParameters"] = IntArray(Read(info, "infoArr"))
        };
    }

    private static Dictionary<string, object?> BuildSkillTagCatalogRow(object row)
    {
        var localized = Text(row, "name");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"),
            ["name"] = localized,
            ["nameEnglish"] = English(row, localized)
        };
    }

    private static Dictionary<string, object?> BuildTalentCatalogRow(object row)
    {
        var skillId = Int(row, "skillId");
        var masteryId = Int(row, "masteryId");
        var name = TalentName(row, skillId, masteryId);
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = name.Localized, ["nameEnglish"] = name.English,
            ["jobId"] = Int(row, "jobId"), ["advancedId"] = Int(row, "advancedId"),
            ["type"] = Int(row, "type"), ["miniType"] = Int(row, "miniType"),
            ["index"] = Int(row, "index"), ["floor"] = Int(row, "floor"),
            ["skillId"] = skillId, ["masteryId"] = masteryId
        };
    }

    private static Dictionary<string, object?> BuildMasteryCatalogRow(object row)
    {
        var localized = Text(row, "name");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized, ["nameEnglish"] = English(row, localized),
            ["jobId"] = Int(row, "jobId"), ["index"] = Int(row, "index"),
            ["explainRows"] = NumericMatrix(Read(row, "explainArr"), "int", 2),
            ["affixRows"] = NumericMatrix(Read(row, "affixArr"), "int", 2)
        };
    }

    private static Dictionary<string, object?> BuildEquipmentCatalogRow(object row)
    {
        var localized = Text(row, "name");
        var description = Text(row, "des");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized, ["nameEnglish"] = English(row, localized),
            ["description"] = description, ["descriptionEnglish"] = EnglishDescription(row, description),
            ["part"] = Int(row, "part"), ["weaponOrArmorType"] = Int(row, "minType"),
            ["baseQuality"] = Int(row, "baseQuality"), ["index"] = Int(row, "index"),
            ["boxLevel"] = Int(row, "boxLevel"), ["specialGet"] = Int(row, "specialGet"),
            ["jobId"] = Int(row, "jobId"), ["chapterId"] = Int(row, "chapId"),
            ["setId"] = Int(row, "setsId"), ["upgradeMythId"] = Int(row, "upMyth"),
            ["uniqueSkillAffixId"] = Int(row, "uniqueSkillAffix"),
            ["baseAffixIds"] = IntArray(Read(row, "baseAffix")),
            ["upgradeLegendIds"] = IntArray(Read(row, "upLegendArr")),
            ["upgradeSetIds"] = IntArray(Read(row, "upSetsArr")),
            ["legendAffixCount"] = Int(row, "legendAffixCount"),
            ["mainAttributeRates"] = NumericMatrix(Read(row, "mainAttrRateArr"), "int", 2),
            ["legendAffixes"] = NumericMatrix(Read(row, "legendAffixArr"), "int", 2),
            ["requiredAttributes"] = NumericMatrix(Read(row, "needAttrArr"), "int", 2)
        };
    }

    private static Dictionary<string, object?> BuildSetCatalogRow(object row)
    {
        var localized = Text(row, "name");
        var setId = Int(row, "id");
        return new Dictionary<string, object?>
        {
            ["id"] = setId, ["name"] = localized, ["nameEnglish"] = English(row, localized),
            ["jobId"] = Int(row, "jobId"), ["index"] = Int(row, "index"),
            ["encodedThresholds"] = Text(row, "affixStr"), ["description"] = Text(row, "des"),
            ["effectWearCounts"] = GetSetEffectWearCounts(setId)
                .OrderBy(pair => pair.Key)
                .Select(pair => new Dictionary<string, object?> { ["effectId"] = pair.Key, ["pieces"] = pair.Value }).ToList()
        };
    }

    private static Dictionary<string, object?> BuildSetEffectCatalogRow(object row)
    {
        var id = Int(row, "id");
        var setId = Int(row, "sesId");
        var set = setId > 0 ? InvokeStatic("TableData", "getTEquipSetsData", setId) : null;
        var localized = Text(row, "des");
        var thresholds = setId > 0 ? GetSetEffectWearCounts(setId) : new Dictionary<int, int>();
        return new Dictionary<string, object?>
        {
            ["id"] = id, ["setId"] = setId, ["setName"] = Text(set, "name"),
            ["setNameEnglish"] = English(set, Text(set, "name")), ["index"] = Int(row, "index"),
            ["pieces"] = thresholds.TryGetValue(id, out var pieces) ? pieces : 0,
            ["abilityId"] = Int(row, "abilityId"), ["abilityParameters"] = IntArray(Read(row, "abilityParam")),
            ["description"] = localized, ["descriptionEnglish"] = EnglishDescription(row, localized)
        };
    }

    private static Dictionary<string, object?> BuildAbilityCatalogRow(object row)
    {
        var localized = Text(row, "name");
        var description = Text(row, "des");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized, ["nameEnglish"] = English(row, localized),
            ["description"] = description, ["descriptionEnglish"] = EnglishDescription(row, description),
            ["type"] = Int(row, "type"), ["typeParameters"] = IntArray(Read(row, "typeParam")),
            ["index"] = Int(row, "index"), ["level"] = Int(row, "level"),
            ["getEffects"] = IntArray(Read(row, "getEffect")), ["bodyEffects"] = IntArray(Read(row, "bodyEffect")),
            ["moments"] = IntArray(Read(row, "moment")), ["condition"] = Text(row, "condition"),
            ["monitor"] = IntArray(Read(row, "monitorArr")), ["filters"] = IntArray(Read(row, "filtArr")),
            ["duration"] = Int(row, "duration"), ["tick"] = Number(Read(row, "tick")),
            ["cooldown"] = Number(Read(row, "cdTime")), ["stack"] = Int(row, "stack"),
            ["effectiveType"] = Int(row, "effectiveType"), ["results"] = IntArray(Read(row, "resultArr")),
            ["attributeId"] = Int(row, "attrId")
        };
    }

    private static Dictionary<string, object?> BuildAffixCatalogRow(object row)
    {
        var description = Text(row, "des");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["type"] = Int(row, "type"), ["subtype"] = Int(row, "minType"),
            ["index"] = Int(row, "index"), ["equipmentLevel"] = Int(row, "equipLevel"),
            ["weight"] = Int(row, "weight"), ["quality"] = Int(row, "quality"),
            ["effectType"] = Int(row, "effectType"), ["effectParameters"] = IntArray(Read(row, "effectParam")),
            ["specialType"] = Int(row, "specialType"), ["levelRates"] = NumberArray(Read(row, "levelRate")),
            ["signType"] = Int(row, "signType"), ["reverseSign"] = Bool(Read(row, "reverseSign")),
            ["descriptionType"] = Int(row, "desType"), ["description"] = description,
            ["descriptionEnglish"] = EnglishDescription(row, description),
            ["equipmentPartParameters"] = NumericMatrix(Read(row, "equipPartParam"), "int", 2)
        };
    }

    private static Dictionary<string, object?> BuildMiniDescriptionCatalogRow(object row)
    {
        var description = Text(row, "des");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = Text(row, "name"),
            ["description"] = description, ["descriptionEnglish"] = EnglishDescription(row, description)
        };
    }

    private static Dictionary<string, object?> BuildHeroQualityCatalogRow(object row)
    {
        var localized = Text(row, "name");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized, ["nameEnglish"] = English(row, localized),
            ["legendCount"] = Int(row, "legendCount"), ["talentPoint"] = Int(row, "talentPoint"),
            ["mainAttrTotal"] = Int(row, "mainAttrTotal"), ["mainAttrGrowth"] = Int(row, "mainAttrGrow"),
            ["baseAttrTotal"] = Int(row, "baseAttrTotal"), ["baseAttrGrowth"] = Int(row, "baseAttrGrow"),
            ["equipmentRate"] = Int(row, "equipRate"),
            ["equipmentQualityRanges"] = NumericMatrix(Read(row, "equipQualityArr"), "int", 2)
        };
    }

    private static Dictionary<string, object?> BuildAttributeCatalogRow(object row)
    {
        var localized = Text(row, "name");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized,
            ["nameEnglish"] = English(row, localized), ["showName"] = Text(row, "showName"),
            ["type"] = Int(row, "type"), ["typeParameters"] = IntArray(Read(row, "typeParam")),
            ["ownerTypes"] = IntArray(Read(row, "ownTypeArr")),
            ["minimum"] = Number(Read(row, "minValue")), ["maximum"] = Number(Read(row, "maxValue")),
            ["default"] = Number(Read(row, "defaultValue")), ["valueType"] = Int(row, "valueType"),
            ["inheritType"] = Int(row, "inheritType")
        };
    }

    private static Dictionary<string, object?> BuildActionCatalogRow(object row)
        => new()
        {
            ["id"] = Int(row, "id"), ["name"] = Text(row, "name"),
            ["type"] = Int(row, "type"), ["subtype"] = Int(row, "miniType"),
            ["emitterId"] = Int(row, "emitterId"), ["produceId"] = Int(row, "produceId"),
            ["triggerIds"] = IntArray(Read(row, "triggerArr"))
        };

    private static Dictionary<string, object?> BuildProduceCatalogRow(object row)
        => new()
        {
            ["id"] = Int(row, "id"), ["name"] = Text(row, "name"),
            ["type"] = Int(row, "type"), ["offset"] = Number(Read(row, "xOffset")),
            ["clip"] = Int(row, "produceClip"),
            ["attributes"] = NumericMatrix(Read(row, "attrArr"), "float", 2)
        };

    private static Dictionary<string, object?> BuildEmitterCatalogRow(object row)
        => new()
        {
            ["id"] = Int(row, "id"), ["name"] = Text(row, "name"),
            ["type"] = Int(row, "type"), ["typeParameters"] = IntArray(Read(row, "typeParam")),
            ["shotClip"] = Int(row, "shotClip"), ["followsOwner"] = Bool(Read(row, "isFollow")),
            ["attributes"] = NumericMatrix(Read(row, "attrArr"), "float", 2)
        };

    private static Dictionary<string, object?> BuildSummonCatalogRow(object row)
    {
        var localized = Text(row, "name");
        return new Dictionary<string, object?>
        {
            ["id"] = Int(row, "id"), ["name"] = localized,
            ["nameEnglish"] = English(row, localized), ["type"] = Int(row, "type"),
            ["lifetime"] = Number(Read(row, "lifeTime")), ["maxCount"] = Int(row, "maxCount"),
            ["inheritRate"] = Number(Read(row, "attrInheritRate")),
            ["inheritParameters"] = NumberArray(Read(row, "attrInheritParam")),
            ["extraInheritance"] = NumericMatrix(Read(row, "extraAttrInheritRateArr"), "float", 2),
            ["skills"] = IntArray(Read(row, "skillArr")), ["actions"] = IntArray(Read(row, "actionArr")),
            ["abilities"] = IntArray(Read(row, "abilityArr")),
            ["attributes"] = NumericMatrix(Read(row, "attrArr"), "float", 2),
            ["levelAttributes"] = NumericMatrix(Read(row, "levelAttrArr"), "float", 2)
        };
    }

    private static List<Dictionary<string, object?>> BuildOwnedEquipmentSetSummary()
    {
        return ReadAllEquipmentSources(true)
            .Select(record =>
            {
                var definition = Read(Read(record.ItemData, "itemEquipData"), "tEquipData");
                return new
                {
                    Source = record.StorageSource,
                    DefinitionId = Int(definition, "id", Int(Read(record.ItemData, "saveItemData"), "id")),
                    SetId = Int(definition, "setsId")
                };
            })
            .Where(entry => entry.SetId > 0 && entry.DefinitionId > 0)
            .GroupBy(entry => new { entry.Source, entry.SetId })
            .Select(group =>
            {
                var set = InvokeStatic("TableData", "getTEquipSetsData", group.Key.SetId);
                return new Dictionary<string, object?>
                {
                    ["source"] = group.Key.Source.ToString(), ["setId"] = group.Key.SetId,
                    ["setName"] = Text(set, "name"), ["setNameEnglish"] = English(set, Text(set, "name")),
                    ["copies"] = group.Count(), ["distinctDefinitions"] = group.Select(entry => entry.DefinitionId).Distinct().Count(),
                    ["definitionIds"] = group.Select(entry => entry.DefinitionId).Distinct().OrderBy(id => id).ToList()
                };
            })
            .OrderBy(row => row["source"]?.ToString(), StringComparer.Ordinal)
            .ThenBy(row => Convert.ToInt32(row["setId"] ?? 0, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static List<object> TableRows(string dictionaryName)
        => ReadValues(ReadStatic("TableData", dictionaryName));

    private static int RowId(Dictionary<string, object?> row)
        => Convert.ToInt32(row.TryGetValue("id", out var value) ? value ?? 0 : 0, CultureInfo.InvariantCulture);

    private static (string Localized, string English) TalentName(object? definition, int skillId, int masteryId)
    {
        object? namedRow = null;
        if (skillId > 0) namedRow = InvokeStatic("TableData", "getTSkillData", skillId);
        if (namedRow is null && masteryId > 0) namedRow = InvokeStatic("TableData", "getTMasteryData", masteryId);
        namedRow ??= definition;
        var localized = FirstNonEmpty(Text(namedRow, "name"), Text(definition, "name"));
        return (localized, English(namedRow, localized));
    }

    private static List<Dictionary<string, object?>> AttributeDictionary(object? dictionary)
    {
        return ReadEntries(dictionary).Select(entry =>
        {
            var key = Read(entry, "Key");
            return new Dictionary<string, object?>
            {
                ["id"] = IntRaw(key), ["name"] = key?.ToString() ?? string.Empty,
                ["value"] = Number(Read(entry, "Value"))
            };
        }).OrderBy(RowId).ToList();
    }

    private static List<Dictionary<string, object?>> CurrentAttributes(object? attrData)
    {
        var result = new List<Dictionary<string, object?>>();
        var type = GameType("EAttrType");
        if (attrData is null || type is null || !type.IsEnum) return result;
        foreach (var value in Enum.GetValues(type))
        {
            var number = Number(InvokeInstance(attrData, "GetAttrValue", value!));
            if (number is null || Math.Abs(number.Value) < 0.0000001d) continue;
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = IntRaw(value), ["name"] = value?.ToString() ?? string.Empty, ["value"] = number
            });
        }
        return result.OrderBy(RowId).ToList();
    }

    private static Dictionary<string, object?> NumericMatrix(object? value, string elementType, int expectedColumns)
    {
        var flat = new List<object?>();
        var rank = 0;
        var dimensions = new List<int>();
        try
        {
            if (value is Array managed)
            {
                rank = managed.Rank;
                for (var i = 0; i < managed.Rank; i++) dimensions.Add(managed.GetLength(i));
                foreach (var item in managed) flat.Add(elementType == "float" ? Number(item) : IntRaw(item));
            }
            else if (value is Il2CppObjectBase native)
            {
                rank = Convert.ToInt32(IL2CPP.il2cpp_class_get_rank(native.ObjectClass), CultureInfo.InvariantCulture);
                if (elementType == "float")
                {
                    var array = new Il2CppStructArray<float>(native.Pointer);
                    for (var i = 0; i < array.Length; i++) flat.Add(Number(array[i]));
                }
                else
                {
                    var array = new Il2CppStructArray<int>(native.Pointer);
                    for (var i = 0; i < array.Length; i++) flat.Add(array[i]);
                }
            }
            else
            {
                foreach (var item in ReadSequence(value))
                    flat.Add(elementType == "float" ? Number(item) : IntRaw(item));
            }
        }
        catch
        {
            flat.Clear();
            foreach (var item in ReadSequence(value))
                flat.Add(elementType == "float" ? Number(item) : IntRaw(item));
        }

        var rows = new List<List<object?>>();
        if (expectedColumns > 0)
            for (var offset = 0; offset < flat.Count; offset += expectedColumns)
                rows.Add(flat.Skip(offset).Take(expectedColumns).ToList());
        return new Dictionary<string, object?>
        {
            ["elementType"] = elementType,
            ["rank"] = rank,
            ["dimensions"] = dimensions,
            ["expectedColumns"] = expectedColumns,
            ["flatValues"] = flat,
            ["rows"] = rows
        };
    }

    private static List<int> IntArray(object? value)
        => ReadSequence(value).Select(IntRaw).ToList();

    private static List<double> NumberArray(object? value)
        => ReadSequence(value).Select(Number).Where(number => number.HasValue).Select(number => number!.Value).ToList();

    private static Dictionary<string, object?>? EnumSnapshot(object? value)
    {
        if (value is null) return null;
        return new Dictionary<string, object?> { ["id"] = IntRaw(value), ["name"] = value.ToString() };
    }

    private static int Int(object? source, string property, int fallback = 0)
        => ReadNullableInt(source, property) ?? fallback;

    private static int IntRaw(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static int SafeInt(Func<int> getter, int fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static bool Bool(object? value)
    {
        try { return value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static double? Number(object? value)
    {
        try
        {
            if (value is null) return null;
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number) ? number : null;
        }
        catch { return null; }
    }

    private static string Text(object? source, string property)
        => Clean(ReadString(source, property) ?? string.Empty);

    private static string English(object? row, string fallback)
        => Clean(EnglishName(row, fallback) ?? fallback);

    private static string EnglishDescription(object? row, string fallback)
        => Clean(EnglishText(row, "_des", fallback) ?? fallback);
}
#endif
