using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 3)
{
    Console.Error.WriteLine(
        "Usage: EffectScorerAudit <plugin.dll> <party-report.json> <game-interop-directory>");
    return 2;
}

var pluginPath = Path.GetFullPath(args[0]);
var reportPath = Path.GetFullPath(args[1]);
var interopDirectory = Path.GetFullPath(args[2]);
if (!File.Exists(pluginPath)) throw new FileNotFoundException("Plugin DLL is missing.", pluginPath);
if (!File.Exists(reportPath)) throw new FileNotFoundException("Party report is missing.", reportPath);
if (!Directory.Exists(interopDirectory))
    throw new DirectoryNotFoundException($"Interop directory is missing: {interopDirectory}");

var gameDirectory = Directory.GetParent(interopDirectory)?.Parent?.FullName
    ?? throw new DirectoryNotFoundException("Game directory could not be resolved.");
var dependencyDirectories = new[]
{
    Path.GetDirectoryName(pluginPath)!,
    Path.Combine(gameDirectory, "BepInEx", "core"),
    interopDirectory
}.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in dependencyDirectories)
    {
        var candidate = Path.Combine(directory, $"{name.Name}.dll");
        if (File.Exists(candidate))
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
    }
    return null;
};

var gameAssemblyPath = Path.Combine(interopDirectory, "Assembly-CSharp.dll");
if (File.Exists(gameAssemblyPath))
    _ = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameAssemblyPath);
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
var reader = plugin.GetType("PathOfIdleInGameSearch.GameInventoryReader", true)!;
var focusType = reader.GetNestedType("HeroFocus", BindingFlags.NonPublic)!;
var profileType = reader.GetNestedType("HeroEffectProfile", BindingFlags.NonPublic)!;
var roleType = reader.GetNestedType("NativeSkillRoleProfile", BindingFlags.NonPublic)!;
var scoreMethod = reader.GetMethod(
    "ScoreOpaqueConditionalEffect",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var tagMethod = reader.GetMethod(
    "InferSkillTagDependenciesFromText",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var themeContractMethod = reader.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
    .Single(method => method.Name == "SatisfiesNativeThemeOutputContract"
                      && method.GetParameters().Length == 5
                      && method.GetParameters()[3].ParameterType == roleType);
var avoidableOffThemeMethod = reader.GetMethod(
    "CountAvoidableOffThemeActiveAttacks",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var genericAvoidableOffThemeMethod = reader.GetMethod(
    "IsAvoidableOffThemeActiveSkill",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var additionalHealingTargetMethod = reader.GetMethod(
    "TryReadAdditionalHealingTargetCount",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var parseNativeTargetMethod = reader.GetMethod(
    "ParseNativeTriggerTargetType",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var guaranteedSelfTargetMethod = reader.GetMethod(
    "IsGuaranteedSelfPowerTarget",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var pressuredSelfTargetMethod = reader.GetMethod(
    "IsSelfRecoveryPowerTargetUnderPressure",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var explicitSelfRecoveryMethod = reader.GetMethod(
    "IsExplicitSelfRecoveryPayload",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var initializeFinalLoadoutScoreMethod = reader.GetMethod(
    "InitializeFinalLoadoutScore",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var coverageGuardMethod = reader.GetMethod(
    "CanCandidateReplaceCurrentUnderCoverage",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var defenseDamageLaneType = reader.GetNestedType(
    "DefenseDamageLane", BindingFlags.NonPublic)!;
var simulateDefenseMethod = reader.GetMethod(
    "SimulateDefenseWindowCore",
    BindingFlags.NonPublic | BindingFlags.Static)!;

object CreateFocus(string key)
{
    var constructor = focusType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .OrderByDescending(value => value.GetParameters().Length)
        .First();
    return constructor.Invoke(new object?[] { key, key, key, Array.Empty<string>(), true });
}

object? DefaultArgument(Type type)
{
    if (type == typeof(string)) return string.Empty;
    if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
    if (type.IsValueType) return Activator.CreateInstance(type);
    if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
        return Activator.CreateInstance(type);
    return null;
}

object CreateProfile(string key)
{
    var constructor = profileType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .OrderByDescending(value => value.GetParameters().Length)
        .First();
    var parameters = constructor.GetParameters();
    var values = parameters.Select(parameter => DefaultArgument(parameter.ParameterType)).ToArray();
    values[0] = CreateFocus(key);
    return constructor.Invoke(values);
}

object CreateRole(
    Dictionary<int, double> damageByType,
    double heal = 0d,
    double shield = 0d,
    double abilitySupport = 0d,
    double abilityDefense = 0d,
    double abilityMinion = 0d,
    int controlledEntityKinds = 0,
    double controlledEntityDamage = 0d,
    double controlledEntityUtility = 0d,
    double controlledEntitySurvival = 0d,
    double? selfHeal = null,
    double? selfShield = null)
{
    var constructor = roleType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .OrderByDescending(value => value.GetParameters().Length)
        .First();
    var parameters = constructor.GetParameters();
    var values = parameters
        .Select(parameter => DefaultArgument(parameter.ParameterType)).ToArray();
    values[0] = damageByType;
    void Set(string name, object value)
    {
        var index = Array.FindIndex(parameters, parameter =>
            string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        values[index] = parameters[index].ParameterType.IsEnum
            ? Enum.ToObject(parameters[index].ParameterType, value)
            : value;
    }
    Set("heal", heal);
    Set("shield", shield);
    Set("abilitySupport", abilitySupport);
    Set("abilityDefense", abilityDefense);
    Set("abilityMinion", abilityMinion);
    Set("controlledEntityKinds", controlledEntityKinds);
    Set("controlledEntityDamage", controlledEntityDamage);
    Set("controlledEntityUtility", controlledEntityUtility);
    Set("controlledEntitySurvival", controlledEntitySurvival);
    Set("selfHeal", selfHeal ?? heal);
    Set("selfShield", selfShield ?? shield);
    return constructor.Invoke(values);
}

int AvoidableOffThemeCount(string key, params object[] roles)
{
    var activeList = (IList)Activator.CreateInstance(
        typeof(List<>).MakeGenericType(roleType))!;
    foreach (var role in roles) activeList.Add(role);
    return Convert.ToInt32(avoidableOffThemeMethod.Invoke(
        null, new object?[] { CreateFocus(key), activeList }));
}

bool IsAvoidableOffThemeSkill(string key, object role)
    => (bool)genericAvoidableOffThemeMethod.Invoke(
        null, new object?[] { role, CreateFocus(key), null })!;

bool ThemeContract(string key, Dictionary<int, double> damageByType, out string reason)
{
    var package = CreateRole(damageByType);
    var activeList = (IList)Activator.CreateInstance(
        typeof(List<>).MakeGenericType(roleType))!;
    foreach (var entry in damageByType)
        activeList.Add(CreateRole(new Dictionary<int, double> { [entry.Key] = entry.Value }));
    var arguments = new object?[]
    {
        CreateFocus(key),
        CreateRole(new Dictionary<int, double>()),
        activeList,
        package,
        null
    };
    var accepted = (bool)themeContractMethod.Invoke(null, arguments)!;
    reason = arguments[4]?.ToString() ?? string.Empty;
    return accepted;
}

bool RoleThemeContract(string key, object role, out string reason)
{
    var activeList = (IList)Activator.CreateInstance(
        typeof(List<>).MakeGenericType(roleType))!;
    activeList.Add(role);
    var arguments = new object?[]
    {
        CreateFocus(key),
        CreateRole(new Dictionary<int, double>()),
        activeList,
        role,
        null
    };
    var accepted = (bool)themeContractMethod.Invoke(null, arguments)!;
    reason = arguments[4]?.ToString() ?? string.Empty;
    return accepted;
}

bool TryReadAdditionalHealingTargets(string text, out double count)
{
    var arguments = new object?[] { text, 0d };
    var accepted = (bool)additionalHealingTargetMethod.Invoke(null, arguments)!;
    count = Convert.ToDouble(arguments[1]);
    return accepted;
}

var profiles = new[]
{
    "physical", "elemental", "fire", "ice", "lightning", "minion",
    "bleed", "corrosion", "crit", "support", "defense"
}.ToDictionary(key => key, CreateProfile, StringComparer.Ordinal);

using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
var catalogs = report.RootElement.GetProperty("catalogs");
var abilities = catalogs.GetProperty("abilities").EnumerateArray()
    .ToDictionary(row => row.GetProperty("id").GetInt32());
var failures = new List<string>();
var structurallyScored = 0;
var relationshipScored = 0;
var totalSetEffects = 0;
var specialAffixStructural = 0;
var specialAffixNative = 0;
var specialAffixCount = 0;

bool HasResult301(JsonElement ability)
{
    var results = ability.GetProperty("results").EnumerateArray().ToList();
    return results.Count >= 3 && results[0].GetInt32() == 301;
}

bool HasExactOrScheduledRelationship(JsonElement effect, string text)
{
    var abilityId = effect.GetProperty("abilityId").GetInt32();
    if (abilities.TryGetValue(abilityId, out var parent) && HasResult301(parent)) return true;
    foreach (var parameter in effect.GetProperty("abilityParameters").EnumerateArray())
        if (abilities.TryGetValue(parameter.GetInt32(), out var child) && HasResult301(child))
            return true;
    var namedSkills = Regex.Matches(text, @"\[[^\]]+\]").Count;
    if (namedSkills >= 2 && Regex.IsMatch(text,
            @"\b(?:trigger|cooldown|effect also applies|duration is extended)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        return true;
    return Regex.IsMatch(text,
        @"\d+(?:\.\d+)?\s*%\s*(?:of\s+)?(?:your\s+)?(?:physical attack|elemental attack|strength|dexterity|intelligence|max health|health|max mana|mana)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

foreach (var effect in catalogs.GetProperty("setEffects").EnumerateArray())
{
    totalSetEffects++;
    var id = effect.GetProperty("id").GetInt32();
    var abilityId = effect.GetProperty("abilityId").GetInt32();
    var text = effect.GetProperty("descriptionEnglish").GetString() ?? string.Empty;
    if (!abilities.ContainsKey(abilityId))
    {
        failures.Add($"set effect {id} references missing ability {abilityId}");
        continue;
    }
    var scores = profiles.ToDictionary(
        entry => entry.Key,
        entry => Convert.ToDouble(scoreMethod.Invoke(null, new[] { text, entry.Value })));
    if (scores.Values.Any(value => double.IsFinite(value) && Math.Abs(value) > 0.000001d))
    {
        structurallyScored++;
        continue;
    }
    if (HasExactOrScheduledRelationship(effect, text))
    {
        relationshipScored++;
        continue;
    }
    failures.Add($"set effect {id} has no structural or exact/scheduled scoring lane: {text}");
}

// Audit every currently authored extra-healing-recipient relationship instead
// of naming a particular set. New rows with the same relationship grammar are
// automatically covered by this catalog test.
foreach (var effect in catalogs.GetProperty("setEffects").EnumerateArray())
{
    var id = effect.GetProperty("id").GetInt32();
    var text = effect.GetProperty("descriptionEnglish").GetString() ?? string.Empty;
    var authored = Regex.Match(
        text,
        @"\bheal(?:s|ed|ing)?\s+(\d+(?:\.\d+)?)\s+additional\s+(?:all(?:y|ies)|allied\s+targets?|targets?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (!authored.Success) continue;
    var expected = double.Parse(authored.Groups[1].Value,
        System.Globalization.CultureInfo.InvariantCulture);
    if (!TryReadAdditionalHealingTargets(text, out var actual)
        || Math.Abs(actual - expected) > 0.000001d)
        failures.Add($"set effect {id} extra-healing target relation was not parsed ({actual} != {expected})");
}
if (!TryReadAdditionalHealingTargets(
        "Heal 1 additional allied target", out var globalHealingTargets)
    || Math.Abs(globalHealingTargets - 1d) > 0.000001d)
    failures.Add("global additional allied-target healing relation was not parsed");

foreach (var affix in catalogs.GetProperty("affixes").EnumerateArray()
             .Where(row => row.GetProperty("effectType").GetInt32() == 3))
{
    specialAffixCount++;
    var id = affix.GetProperty("id").GetInt32();
    var parameters = affix.GetProperty("effectParameters").EnumerateArray().ToList();
    var abilityId = parameters.Count > 0 ? parameters[0].GetInt32() : 0;
    var text = affix.GetProperty("descriptionEnglish").GetString() ?? string.Empty;
    if (abilityId <= 0 || !abilities.TryGetValue(abilityId, out var ability))
    {
        failures.Add($"special affix {id} references missing ability {abilityId}");
        continue;
    }
    var hasStructuralScore = profiles.Values.Any(profile =>
    {
        var value = Convert.ToDouble(scoreMethod.Invoke(null, new[] { text, profile }));
        return double.IsFinite(value) && Math.Abs(value) > 0.000001d;
    });
    if (hasStructuralScore)
    {
        specialAffixStructural++;
        continue;
    }
    if (ability.GetProperty("results").GetArrayLength() > 0)
    {
        specialAffixNative++;
        continue;
    }
    failures.Add($"special affix {id} has neither a structural score nor a native result relation: {text}");
}

var faelen = catalogs.GetProperty("setEffects").EnumerateArray()
    .Single(row => row.GetProperty("id").GetInt32() == 4031);
var faelenText = faelen.GetProperty("descriptionEnglish").GetString() ?? string.Empty;
var faelenTags = ((IEnumerable)tagMethod.Invoke(null, new object[] { faelenText })!)
    .Cast<object>().Select(value => value.ToString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
if (faelenTags.Contains("ally"))
    failures.Add("party-recipient wording was incorrectly classified as a controlled Ally dependency");

var firstMonitorCodes = abilities.Values
    .Where(HasResult301)
    .Select(row => row.GetProperty("monitor").EnumerateArray().Select(value => value.GetInt32()).FirstOrDefault())
    .Where(value => value > 0)
    .Distinct().OrderBy(value => value).ToList();
var supportedMonitorRelations = new HashSet<int> { 200, 201, 400, 401, 402, 403, 404, 405 };
foreach (var code in firstMonitorCodes.Where(code => !supportedMonitorRelations.Contains(code)))
    failures.Add($"result301 monitor relation {code} has no audited evaluator lane");

// Every damage lane uses the same structural contract. Mixed output remains a
// valid complete fallback (so point spending cannot be aborted), while a pure
// off-theme attack is distinguishable from an off-theme skill with an
// independently proven support role. The dynamic case represents a future
// native damage enum value that was not named when the plugin was built.
var damageThemeCases = new[]
{
    (Key: "physical", Aligned: 1, OffTheme: 4),
    (Key: "elemental", Aligned: 4, OffTheme: 1),
    (Key: "fire", Aligned: 4, OffTheme: 5),
    (Key: "ice", Aligned: 5, OffTheme: 6),
    (Key: "lightning", Aligned: 6, OffTheme: 4),
    (Key: "bleed", Aligned: 7, OffTheme: 4),
    (Key: "corrosion", Aligned: 8, OffTheme: 7),
    (Key: "damage:9", Aligned: 9, OffTheme: 4)
};
foreach (var theme in damageThemeCases)
{
    if (!ThemeContract(theme.Key,
            new Dictionary<int, double>
                { [theme.Aligned] = 66.6d, [theme.OffTheme] = 33.4d },
            out var mixedReason))
        failures.Add($"mixed {theme.Key} package was rejected: {mixedReason}");
    if (ThemeContract(theme.Key,
            new Dictionary<int, double> { [theme.OffTheme] = 100d },
            out var missingReason))
        failures.Add($"package without {theme.Key} output was accepted: {missingReason}");

    var directOffTheme = CreateRole(
        new Dictionary<int, double> { [theme.OffTheme] = 100d });
    if (AvoidableOffThemeCount(theme.Key, directOffTheme) != 1)
        failures.Add($"pure off-theme attack was not classified for {theme.Key}");
    var supportingOffTheme = CreateRole(
        new Dictionary<int, double> { [theme.OffTheme] = 100d },
        abilitySupport: 1d);
    if (AvoidableOffThemeCount(theme.Key, supportingOffTheme) != 0)
        failures.Add($"independent support was misclassified as avoidable for {theme.Key}");
    var offThemeSummon = CreateRole(
        new Dictionary<int, double> { [theme.OffTheme] = 100d },
        controlledEntityKinds: 1,
        controlledEntityDamage: 100d,
        controlledEntitySurvival: 100d);
    if (AvoidableOffThemeCount(theme.Key, offThemeSummon) != 1)
        failures.Add($"off-theme summon attack escaped classification for {theme.Key}");
}

// Non-damage role themes use the same native parent-role classifier. These
// cases guard Defense, Support, and controlled-entity themes together so a fix
// for one named hero cannot silently regress the others.
var nonDamageThemeCases = new[]
{
    (Key: "defense", Aligned: CreateRole(
        new Dictionary<int, double>(), shield: 100d, abilityDefense: 25d)),
    (Key: "support", Aligned: CreateRole(
        new Dictionary<int, double>(), heal: 100d, abilitySupport: 25d)),
    (Key: "minion", Aligned: CreateRole(
        new Dictionary<int, double>(), controlledEntityKinds: 1,
        controlledEntityDamage: 100d))
};
foreach (var theme in nonDamageThemeCases)
{
    var pureAttack = CreateRole(new Dictionary<int, double> { [1] = 100d });
    if (!IsAvoidableOffThemeSkill(theme.Key, pureAttack))
        failures.Add($"pure attack was not classified as avoidable for {theme.Key}");
    if (IsAvoidableOffThemeSkill(theme.Key, theme.Aligned))
        failures.Add($"native {theme.Key} role was misclassified as avoidable");
    if (!RoleThemeContract(theme.Key, theme.Aligned, out var reason))
        failures.Add($"native {theme.Key} role failed its output contract: {reason}");
}

// Recovery recipient classification is native-target driven. Team healing
// remains valid Support output but cannot be credited as the wearer's Defense
// survival unless the trigger points at the owning combat unit.
var allyOnlyHeal = CreateRole(
    new Dictionary<int, double>(), heal: 100d, selfHeal: 0d);
if (RoleThemeContract("defense", allyOnlyHeal, out var allyDefenseReason))
    failures.Add($"ally-only healing satisfied the self-survival contract: {allyDefenseReason}");
if (!RoleThemeContract("support", allyOnlyHeal, out var allySupportReason))
    failures.Add($"ally-only healing stopped satisfying Support: {allySupportReason}");

int ParseTarget(string value)
    => Convert.ToInt32(parseNativeTargetMethod.Invoke(null, new object?[] { value }));
bool IsSelfTarget(int value)
    => Convert.ToBoolean(guaranteedSelfTargetMethod.Invoke(null, new object?[] { value }));
bool IsPressuredSelfTarget(string value)
    => Convert.ToBoolean(pressuredSelfTargetMethod.Invoke(
        null, new object?[] { ParseTarget(value), value }));
bool IsSelfRecovery(string text, string kind, bool wearerTriggered)
    => Convert.ToBoolean(explicitSelfRecoveryMethod.Invoke(
        null, new object?[] { text, kind, wearerTriggered }));

if (ParseTarget("10") != 10 || ParseTarget("13,1,1") != 13
    || ParseTarget(string.Empty) != 0)
    failures.Add("native trigger target parsing lost its structural first-field contract");
if (!IsSelfTarget(10) || IsSelfTarget(2) || IsSelfTarget(13))
    failures.Add("native owner/impact/friendly target classification is incorrect");
if (!IsPressuredSelfTarget("10")
    || !IsPressuredSelfTarget("13,1,1")
    || IsPressuredSelfTarget("13,1,0")
    || IsPressuredSelfTarget("11,4")
    || IsPressuredSelfTarget("2"))
    failures.Add("native self-eligible low-health recovery target classification is incorrect");
if (!IsSelfRecovery("When you are hit, restore 10% Health.", "heal", true)
    || IsSelfRecovery("Heal 2 additional allies.", "heal", true))
    failures.Add("conditional self recovery was not separated from ally recovery");

// The native 30-second finalist must not receive the item shortlist prior a
// second time. Changing only that prior therefore cannot change a complete
// finalist score; the fallback lane retains its old behavior for diagnostics.
double InitialFinalScore(
    bool complete,
    double itemScore,
    double itemAbilityScore,
    double nativeStructural,
    double inactivePenalty)
    => Convert.ToDouble(initializeFinalLoadoutScoreMethod.Invoke(
        null,
        new object[]
        {
            complete, itemScore, itemAbilityScore,
            nativeStructural, inactivePenalty
        }));

var completeA = InitialFinalScore(true, 1000d, 900d, 75d, -10d);
var completeB = InitialFinalScore(true, 900000d, -300d, 75d, -10d);
if (Math.Abs(completeA - 65d) > 0.000001d
    || Math.Abs(completeB - completeA) > 0.000001d)
    failures.Add("complete native finalist still double-counts the item shortlist prior");
var fallbackInitial = InitialFinalScore(false, 1000d, 900d, 75d, -10d);
if (Math.Abs(fallbackInitial - 90d) > 0.000001d)
    failures.Add("non-native diagnostic fallback score composition changed unexpectedly");

bool CoverageAllows(
    double currentScore,
    int[] currentUnmodeledSetEffects,
    double candidateScore,
    int[] candidateActiveSetEffects,
    out string reason)
{
    var arguments = new object?[]
    {
        currentScore,
        currentUnmodeledSetEffects,
        candidateScore,
        candidateActiveSetEffects,
        null
    };
    var allowed = (bool)coverageGuardMethod.Invoke(null, arguments)!;
    reason = arguments[4]?.ToString() ?? string.Empty;
    return allowed;
}

// An unknown active set effect is an uncertainty boundary, not a zero-value
// effect. This generic future ID proves the safeguard is relationship-driven
// and does not depend on any currently authored set or hero.
const int futureSetEffectId = 99001;
if (CoverageAllows(
        100d, new[] { futureSetEffectId },
        1000d, Array.Empty<int>(), out _))
    failures.Add("a challenger was allowed to discard an unmodeled incumbent set effect");
if (!CoverageAllows(
        100d, new[] { futureSetEffectId },
        101d, new[] { futureSetEffectId }, out var preservedReason))
    failures.Add($"a challenger preserving the unmodeled set effect was rejected: {preservedReason}");
if (!CoverageAllows(
        100d, Array.Empty<int>(),
        101d, Array.Empty<int>(), out var modeledReason))
    failures.Add($"a higher fully modeled challenger was rejected: {modeledReason}");
if (CoverageAllows(
        100d, Array.Empty<int>(),
        100d, Array.Empty<int>(), out _))
    failures.Add("a score tie was allowed to cause an unnecessary equipment swap");

object DefenseLane(
    double rawDamage,
    double defence = 0d,
    double resistance = 0d,
    double defenceScale = 1000d,
    bool immune = false)
{
    var constructor = defenseDamageLaneType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(candidate => candidate.GetParameters().Length == 6);
    return constructor.Invoke(new object[]
        { 1, rawDamage, defence, resistance, defenceScale, immune });
}

object SimulateDefense(
    double rawDamage,
    double maxHealth = 1000d,
    double regenPerSecond = 0d,
    double dodgeRating = 0d,
    double dodgeScale = 1000d,
    double toughness = 0d,
    bool toughnessDouble = false,
    bool loseDodge = false,
    bool noDefence = false,
    bool noShield = false,
    bool noShieldDecay = true,
    double shieldDecay = 0d,
    double healing = 0d,
    double shield = 0d,
    double deathPrevention = 0d,
    double window = 30d,
    double attacks = 30d,
    double defence = 0d,
    double resistance = 0d,
    double defenceScale = 1000d,
    bool immune = false)
{
    var lanes = (IList)Activator.CreateInstance(
        typeof(List<>).MakeGenericType(defenseDamageLaneType))!;
    lanes.Add(DefenseLane(rawDamage, defence, resistance, defenceScale, immune));
    return simulateDefenseMethod.Invoke(null, new object[]
    {
        maxHealth, regenPerSecond, dodgeRating, dodgeScale, toughness,
        toughnessDouble, loseDodge, noDefence, noShield, noShieldDecay,
        shieldDecay, healing, shield, deathPrevention, window, attacks,
        lanes, "audit-pressure"
    })!;
}

T DefenseValue<T>(object result, string property)
    => (T)result.GetType().GetProperty(property)!.GetValue(result)!;

var unprotected = SimulateDefense(6000d);
var mitigated = SimulateDefense(
    6000d, defence: 1000d, resistance: 100d);
var mitigatedAndDodging = SimulateDefense(
    6000d, dodgeRating: 1000d, defence: 1000d, resistance: 100d);
if (DefenseValue<double>(mitigated, "SecondsAlive")
    <= DefenseValue<double>(unprotected, "SecondsAlive"))
    failures.Add("defence and resistance did not improve time alive");
if (!DefenseValue<bool>(mitigatedAndDodging, "Survived")
    || DefenseValue<double>(mitigatedAndDodging, "ObjectiveScore")
       <= DefenseValue<double>(mitigated, "ObjectiveScore"))
    failures.Add("dodge did not improve the deterministic survival result");

var noDefenceFlag = SimulateDefense(
    6000d, noDefence: true, defence: 100000d);
if (DefenseValue<double>(noDefenceFlag, "SecondsAlive")
    > DefenseValue<double>(unprotected, "SecondsAlive") + 0.000001d)
    failures.Add("noDefence failed to disable the defence rating");

var overheal = SimulateDefense(0d, healing: 1000000d);
if (DefenseValue<double>(overheal, "EffectiveHealing") > 0.000001d)
    failures.Add("unused overheal was credited as effective healing");
var overshield = SimulateDefense(100d, shield: 1000000d);
if (DefenseValue<double>(overshield, "EffectiveShieldGenerated") >= 1000000d
    || DefenseValue<double>(overshield, "RemainingShield") > 1000.000001d)
    failures.Add("over-shield beyond the health cap was credited");

var bloodied = SimulateDefense(1500d);
if (DefenseValue<double>(bloodied, "BloodiedEntries") < 1d
    || DefenseValue<double>(bloodied, "LethalEvents") < 1d
    || DefenseValue<bool>(bloodied, "Survived"))
    failures.Add("bloodied/lethal transitions were not produced by incoming damage");
var deathPrevented = SimulateDefense(1500d, deathPrevention: 5d);
if (DefenseValue<double>(deathPrevented, "SecondsAlive")
    <= DefenseValue<double>(bloodied, "SecondsAlive"))
    failures.Add("a proven death-prevention duration did not extend survival");

var explicitCadence = SimulateDefense(100d, attacks: 12.5d);
if (Math.Abs(DefenseValue<double>(explicitCadence, "HitOpportunities") - 12.5d)
    > 0.000001d)
    failures.Add("incoming hit opportunities did not come from the fixed pressure profile");
var lethalFallback = SimulateDefense(3000d, attacks: 30d);
if (DefenseValue<bool>(lethalFallback, "Survived"))
    failures.Add("nonzero fallback pressure still behaved like an invincible target dummy");

Console.WriteLine(
    $"set_effects={totalSetEffects} "
    + $"structural={structurallyScored} exact_or_scheduled={relationshipScored} "
    + $"special_affixes={specialAffixCount} "
    + $"special_structural={specialAffixStructural} special_native={specialAffixNative} "
    + $"monitor_relations={string.Join(',', firstMonitorCodes)} failures={failures.Count}");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;
