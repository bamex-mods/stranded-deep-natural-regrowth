using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace StrandedDeepNaturalRegrowth
{
    [BepInPlugin("com.bamex.strandeddeep.naturalregrowth", "Stranded Deep Natural Regrowth", "0.2.0")]
    public sealed class NaturalRegrowthPlugin : BaseUnityPlugin
    {
        internal static NaturalRegrowthPlugin Instance;

        private const string ManagedPalmPrefix = "__NR_PALM_";
        private static readonly uint[] PalmPrefabIds = new uint[] { 160u, 159u, 158u, 157u };
        private static readonly string[] PalmPrefabNames = new string[] { "PALM_4", "PALM_3", "PALM_2", "PALM_1" };
        private static readonly string[] PalmResourcePaths = new string[]
        {
            "Prefabs/StrandedObjects/Trees/PALM_4",
            "Prefabs/StrandedObjects/Trees/PALM_3",
            "Prefabs/StrandedObjects/Trees/PALM_2",
            "Prefabs/StrandedObjects/Trees/PALM_1"
        };

        private Harmony _harmony;
        private WorldEcologyState _state;
        private string _worldSeed = String.Empty;
        private string _worldIdentitySource = "unresolved";
        private string _rawWorldSeed = "unknown-world";
        private string _candidateWorldSeed = String.Empty;
        private float _candidateWorldSeedSinceRealtime;
        private const float WorldIdentityStableSeconds = 5.0f;
        private const float PalmDeathConfirmationRadius = 80.0f;
        private string _sidecarPath = String.Empty;
        private bool _dirty;
        private bool _nativeSaveHookAvailable;
        private bool _nativeLoadHookAvailable;
        private bool _managedSaveExclusionAvailable;
        private float _nextScanRealtime;
        private float _nextClockWarningRealtime;
        private float _worldUnavailableSinceRealtime = -1.0f;
        private int _activeWorldInstanceId;
        private double _lastKnownGameDay;
        private bool _clockResolved;
        private readonly NaturalRegrowthClock _clock = new NaturalRegrowthClock();
        private readonly Dictionary<string, ZoneObservation> _zoneObservations = new Dictionary<string, ZoneObservation>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> _managedRuntimeObjects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly HashSet<string> _managedFruitSuppressionPending = new HashSet<string>(StringComparer.Ordinal);

        private const int CoconutUnknown = 0;
        private const int CoconutFruiting = 1;
        private const int CoconutWaiting = 2;
        private const int CoconutPendingSpawn = 3;
        private MethodInfo _palmGenerateFruitMethod;
        private MethodInfo _coconutRangeMethod;
        private bool _coconutNativeSpawnAvailable;
        private bool _managedFruitSaveExclusionAvailable;
        private int _forcedNativeFruitCount;

        private ConfigEntry<float> _scanIntervalSeconds;
        private ConfigEntry<float> _zoneReadySeconds;
        private ConfigEntry<float> _worldUnloadGraceSeconds;
        private ConfigEntry<float> _growthPalm3Days;
        private ConfigEntry<float> _growthPalm2Days;
        private ConfigEntry<float> _growthPalm1Days;
        private ConfigEntry<bool> _reproductionEnabled;
        private ConfigEntry<float> _baseDailyBirthChance;
        private ConfigEntry<float> _smallWeight;
        private ConfigEntry<float> _mediumWeight;
        private ConfigEntry<float> _largeWeight;
        private ConfigEntry<float> _tallWeight;
        private ConfigEntry<float> _spawnRadiusMin;
        private ConfigEntry<float> _spawnRadiusMax;
        private ConfigEntry<float> _minimumPalmSpacing;
        private ConfigEntry<float> _minimumGroundY;
        private ConfigEntry<int> _maxPlacementAttemptsPerScan;
        private ConfigEntry<int> _maxBirthsPerZonePerDay;
        private ConfigEntry<bool> _coconutRegrowthEnabled;
        private ConfigEntry<int> _coconutMinDays;
        private ConfigEntry<int> _coconutMaxDays;
        private ConfigEntry<float> _coconutSuccessChance;
        private ConfigEntry<float> _coconutOneWeight;
        private ConfigEntry<float> _coconutTwoWeight;
        private ConfigEntry<float> _coconutThreeWeight;

        private void Awake()
        {
            Instance = this;
            BindConfig();
            PatchGame();
            Logger.LogInfo("Natural Regrowth v0.2.0 loaded.");
            Logger.LogInfo("Real palm stages: PALM_4(160) -> PALM_3(159) -> PALM_2(158) -> PALM_1(157).");
            Logger.LogInfo("Managed palms are excluded from native palm Save(); ecology persists in a per-world sidecar on native SaveGame().");
            Logger.LogInfo("Zone reconciliation is gated by gameplay PlayerCamera occupancy, not SaveContainer.activeInHierarchy; palm death also requires a nearby gameplay camera.");
        }

        private void BindConfig()
        {
            _scanIntervalSeconds = Config.Bind("Runtime", "ScanIntervalSeconds", 2.0f, "Seconds between loaded-zone reconciliation scans.");
            _zoneReadySeconds = Config.Bind("Runtime", "ZoneReadySeconds", 6.0f, "How long a loaded Zone must stay structurally stable before death/restore reconciliation.");
            _worldUnloadGraceSeconds = Config.Bind("Runtime", "WorldUnloadGraceSeconds", 2.0f, "How long no playable Zone may be present before runtime ecology is discarded and must be reloaded from the last native Save sidecar.");

            _growthPalm3Days = Config.Bind("Growth", "PALM4_to_PALM3_Days", 17.0f, "Age in in-game days when a managed PALM_4 becomes PALM_3.");
            _growthPalm2Days = Config.Bind("Growth", "PALM4_to_PALM2_Days", 34.0f, "Age in in-game days when a managed palm reaches PALM_2.");
            _growthPalm1Days = Config.Bind("Growth", "PALM4_to_PALM1_Days", 50.0f, "Age in in-game days when a managed palm reaches PALM_1.");

            _reproductionEnabled = Config.Bind("Reproduction", "Enabled", true, "Allow initialized island populations to create new palm generations.");
            _baseDailyBirthChance = Config.Bind("Reproduction", "BaseDailyBirthChance", 0.035f, "Per-parent daily chance before stage and deficit multipliers.");
            _smallWeight = Config.Bind("Reproduction", "PALM4Weight", 0.25f, "Reproductive weight of PALM_4.");
            _mediumWeight = Config.Bind("Reproduction", "PALM3Weight", 0.50f, "Reproductive weight of PALM_3.");
            _largeWeight = Config.Bind("Reproduction", "PALM2Weight", 0.75f, "Reproductive weight of PALM_2.");
            _tallWeight = Config.Bind("Reproduction", "PALM1Weight", 1.00f, "Reproductive weight of PALM_1.");
            _maxBirthsPerZonePerDay = Config.Bind("Reproduction", "MaxBirthsPerZonePerDay", 2, "Safety cap on committed births per island per in-game day.");

            _spawnRadiusMin = Config.Bind("Placement", "SpawnRadiusMin", 6.0f, "Minimum child distance from its parent in world units.");
            _spawnRadiusMax = Config.Bind("Placement", "SpawnRadiusMax", 25.0f, "Maximum child distance from its parent in world units.");
            _minimumPalmSpacing = Config.Bind("Placement", "MinimumPalmSpacing", 4.0f, "Minimum spacing between real palm trunks in world units.");
            _minimumGroundY = Config.Bind("Placement", "MinimumGroundY", 0.25f, "Reject candidate ground at or below this world Y value.");
            _maxPlacementAttemptsPerScan = Config.Bind("Placement", "MaxPlacementAttemptsPerScan", 12, "Candidate points tried for one pending birth each time its Zone is loaded.");

            _coconutRegrowthEnabled = Config.Bind("Coconuts", "Enabled", true, "Enable persistent coconut regrowth on real PALM_1/PALM_2/PALM_3/PALM_4.");
            _coconutMinDays = Config.Bind("Coconuts", "MinRegrowthDays", 15, "Minimum in-game days between coconut regrowth checks after a palm becomes empty.");
            _coconutMaxDays = Config.Bind("Coconuts", "MaxRegrowthDays", 25, "Maximum in-game days between coconut regrowth checks after a palm becomes empty.");
            _coconutSuccessChance = Config.Bind("Coconuts", "SuccessChance", 0.15f, "Chance that a coconut regrowth check succeeds. A failed check schedules another 15-25 day cycle.");
            _coconutOneWeight = Config.Bind("Coconuts", "OneCoconutWeight", 88.0f, "Relative weight for 1 coconut after a successful regrowth check.");
            _coconutTwoWeight = Config.Bind("Coconuts", "TwoCoconutWeight", 11.0f, "Relative weight for 2 coconuts after a successful regrowth check.");
            _coconutThreeWeight = Config.Bind("Coconuts", "ThreeCoconutWeight", 1.0f, "Relative weight for 3 coconuts after a successful regrowth check.");
        }

        private void PatchGame()
        {
            _harmony = new Harmony("com.bamex.strandeddeep.naturalregrowth");

            MethodInfo saveGame = typeof(SaveManager).GetMethod(
                "SaveGame",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new Type[] { typeof(Beam.IPlayer) },
                null);

            if (saveGame != null)
            {
                MethodInfo prefix = typeof(NaturalRegrowthPlugin).GetMethod("NativeSavePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(saveGame, new HarmonyMethod(prefix), null, null);
                _nativeSaveHookAvailable = true;
                Logger.LogInfo("Patched SaveManager.SaveGame(Beam.IPlayer).");
            }
            else
            {
                Logger.LogError("SaveManager.SaveGame(Beam.IPlayer) was not found. Sidecar will not commit; ecology remains runtime-only until fixed.");
            }

            int loadHookCount = 0;
            MethodInfo[] saveManagerMethods = typeof(SaveManager).GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int sm;
            for (sm = 0; sm < saveManagerMethods.Length; sm++)
            {
                MethodInfo method = saveManagerMethods[sm];
                if (method == null || !String.Equals(method.Name, "LoadGame", StringComparison.Ordinal) || method.ContainsGenericParameters)
                    continue;
                try
                {
                    MethodInfo loadPrefix = typeof(NaturalRegrowthPlugin).GetMethod("NativeLoadPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    _harmony.Patch(method, new HarmonyMethod(loadPrefix), null, null);
                    loadHookCount++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Could not patch SaveManager.LoadGame overload: " + ex.Message);
                }
            }
            _nativeLoadHookAvailable = loadHookCount > 0;
            if (_nativeLoadHookAvailable)
                Logger.LogInfo("Patched SaveManager.LoadGame overloads: " + loadHookCount.ToString(CultureInfo.InvariantCulture) + ".");
            else
            {
                Logger.LogWarning("No SaveManager.LoadGame overload was patched; verified world-unload detection remains the reload fallback.");
            }

            MethodInfo palmSave = typeof(Beam.InteractiveObject_PALM).GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (palmSave != null)
            {
                MethodInfo prefix = typeof(NaturalRegrowthPlugin).GetMethod("PalmSavePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(palmSave, new HarmonyMethod(prefix), null, null);
                _managedSaveExclusionAvailable = true;
                Logger.LogInfo("Patched InteractiveObject_PALM.Save() for managed-palm native Save exclusion.");
            }
            else
            {
                Logger.LogError("InteractiveObject_PALM.Save() was not found. Managed palm spawning will be disabled for safety.");
            }

            MethodInfo foodSave = typeof(Beam.InteractiveObject_FOOD).GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (foodSave != null)
            {
                MethodInfo foodPrefix = typeof(NaturalRegrowthPlugin).GetMethod("FoodSavePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(foodSave, new HarmonyMethod(foodPrefix), null, null);
                _managedFruitSaveExclusionAvailable = true;
                Logger.LogInfo("Patched InteractiveObject_FOOD.Save() for managed attached-fruit native Save exclusion.");
            }
            else
            {
                Logger.LogWarning("InteractiveObject_FOOD.Save() was not found; managed coconut materialization will be disabled for safety.");
            }

            MethodInfo fruitDetached = typeof(Beam.InteractiveObject_PALM).GetMethod("Fruit_Detached", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fruitDetached != null)
            {
                MethodInfo detachedPostfix = typeof(NaturalRegrowthPlugin).GetMethod("PalmFruitDetachedPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(fruitDetached, null, new HarmonyMethod(detachedPostfix), null);
                Logger.LogInfo("Patched InteractiveObject_PALM.Fruit_Detached() so picked managed coconuts return to vanilla Save ownership.");
            }

            _palmGenerateFruitMethod = typeof(Beam.InteractiveObject_PALM).GetMethod("GenerateFruit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (_palmGenerateFruitMethod != null)
            {
                _coconutRangeMethod = FindCalledMethod(_palmGenerateFruitMethod, "Funlabs.MPExtensions", "Range");
                if (_coconutRangeMethod != null && _coconutRangeMethod.ReturnType == typeof(int))
                {
                    MethodInfo rangePrefix = typeof(NaturalRegrowthPlugin).GetMethod("CoconutRangePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    _harmony.Patch(_coconutRangeMethod, new HarmonyMethod(rangePrefix), null, null);
                    _coconutNativeSpawnAvailable = true;
                    Logger.LogInfo("Coconut native spawn path ready: PALM.GenerateFruit() + exact-count Range override via " + FormatMethodSignature(_coconutRangeMethod));
                }
                else
                {
                    Logger.LogError("Could not resolve the Int32 Funlabs.MPExtensions.Range call used by PALM.GenerateFruit(); coconut regrowth spawning is disabled.");
                }
            }
            else
            {
                Logger.LogError("InteractiveObject_PALM.GenerateFruit() was not found; coconut regrowth spawning is disabled.");
            }
        }

        private static void NativeSavePrefix()
        {
            if (Instance != null)
                Instance.OnNativeSave();
        }

        private static void NativeLoadPrefix()
        {
            if (Instance != null)
                Instance.OnNativeLoadBegin();
        }

        private static bool CoconutRangePrefix(ref int __result)
        {
            if (Instance != null && Instance._forcedNativeFruitCount > 0)
            {
                __result = Math.Max(1, Math.Min(3, Instance._forcedNativeFruitCount));
                return false;
            }
            return true;
        }

        private static void PalmFruitDetachedPostfix(Beam.InteractiveObject_PALM __instance, Beam.IAttachable sender)
        {
            if (Instance != null)
                Instance.OnPalmFruitDetached(__instance, sender);
        }

        private static bool PalmSavePrefix(Beam.InteractiveObject_PALM __instance, ref Beam.Serialization.Json.JObject __result)
        {
            if (__instance == null || __instance.gameObject == null)
                return true;

            NaturalRegrowthManagedPalmTag tag = __instance.gameObject.GetComponent<NaturalRegrowthManagedPalmTag>();
            if (tag == null)
                return true;

            __result = null;
            return false;
        }

        private static bool FoodSavePrefix(Beam.InteractiveObject_FOOD __instance, ref Beam.Serialization.Json.JObject __result)
        {
            if (__instance == null || __instance.gameObject == null)
                return true;

            NaturalRegrowthManagedFruitTag managedFruit = __instance.gameObject.GetComponent<NaturalRegrowthManagedFruitTag>();
            if (managedFruit != null && !managedFruit.Detached)
            {
                __result = null;
                return false;
            }

            return true;
        }

        private void Update()
        {
            try
            {
                if (Time.realtimeSinceStartup < _nextScanRealtime)
                    return;

                _nextScanRealtime = Time.realtimeSinceStartup + Mathf.Max(0.5f, _scanIntervalSeconds.Value);
                TickRuntime(false);
            }
            catch (Exception ex)
            {
                Logger.LogError("Natural Regrowth Update failed: " + ex);
            }
        }

        private static string FormatMethodSignature(MethodInfo method)
        {
            if (method == null) return "<null>";
            StringBuilder sb = new StringBuilder();
            sb.Append(method.IsStatic ? "static " : "instance ");
            sb.Append(method.ReturnType != null ? method.ReturnType.FullName : "void");
            sb.Append(' ').Append(method.DeclaringType != null ? method.DeclaringType.FullName : "<type>");
            sb.Append('.').Append(method.Name).Append('(');
            ParameterInfo[] parameters = method.GetParameters();
            int i;
            for (i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                ParameterInfo parameter = parameters[i];
                sb.Append(parameter.ParameterType != null ? parameter.ParameterType.FullName : "<param>");
                if (!String.IsNullOrEmpty(parameter.Name)) sb.Append(' ').Append(parameter.Name);
            }
            sb.Append(')');
            return sb.ToString();
        }

        private void OnDestroy()
        {
            try { ResetRuntimeState("plugin destroy"); } catch { }
            try
            {
                if (_harmony != null)
                    _harmony.UnpatchSelf();
            }
            catch { }
            if (System.Object.ReferenceEquals(Instance, this)) Instance = null;
        }

        private void TickRuntime(bool forceReconcile)
        {
            StrandedWorld world = FindWorld();
            if (world == null || world.Zones == null || !HasAnyPlayableLoadedZone(world))
            {
                HandleWorldUnavailable();
                return;
            }

            if (_worldUnavailableSinceRealtime >= 0.0f)
            {
                float unavailableFor = Time.realtimeSinceStartup - _worldUnavailableSinceRealtime;
                Logger.LogInfo("Playable Zone available again after " + unavailableFor.ToString("0.00", CultureInfo.InvariantCulture) + "s since no-playable-Zone detection. Check for a preceding runtime reset line to distinguish streaming recovery from reload fallback.");
            }
            _worldUnavailableSinceRealtime = -1.0f;

            int worldInstanceId = world.GetInstanceID();
            if (_activeWorldInstanceId != 0 && _activeWorldInstanceId != worldInstanceId)
            {
                ResetRuntimeState("StrandedWorld instance changed");
            }

            string identitySource;
            string rawSeed;
            string seed = ResolveWorldIdentity(world, out identitySource, out rawSeed);
            if (String.IsNullOrEmpty(seed))
                return;

            _worldIdentitySource = identitySource;
            _rawWorldSeed = rawSeed;

            // Zone names/positions settle during world loading. Never bind a
            // sidecar to the first transient fingerprint we see. A candidate
            // must remain identical for several real seconds before it becomes
            // the active world identity.
            if (!String.Equals(seed, _candidateWorldSeed, StringComparison.Ordinal))
            {
                _candidateWorldSeed = seed;
                _candidateWorldSeedSinceRealtime = Time.realtimeSinceStartup;
                return;
            }

            if (Time.realtimeSinceStartup - _candidateWorldSeedSinceRealtime < WorldIdentityStableSeconds)
                return;

            if (!String.Equals(seed, _worldSeed, StringComparison.Ordinal))
                SwitchWorld(seed, worldInstanceId);
            else if (_activeWorldInstanceId == 0)
                _activeWorldInstanceId = worldInstanceId;

            double gameDay;
            _clockResolved = _clock.TryGetGameDay(out gameDay);
            if (_clockResolved)
                _lastKnownGameDay = gameDay;
            else if (Time.realtimeSinceStartup >= _nextClockWarningRealtime)
            {
                _nextClockWarningRealtime = Time.realtimeSinceStartup + 30.0f;
                Logger.LogWarning("Natural Regrowth game clock is unresolved. Capacity/death tracking continues, but growth and reproduction are paused.");
            }

            ReconcileLoadedZones(world, gameDay, forceReconcile);

            if (_clockResolved)
            {
                RunEcologyCatchUp(gameDay);
                RunCoconutCatchUp(gameDay);
                SynchronizeManagedGrowth(world, gameDay);
                ResolvePendingBirths(world, gameDay);
                MaterializePendingCoconuts(world, gameDay);
            }
        }

        private void SwitchWorld(string seed, int worldInstanceId)
        {
            _worldSeed = seed;
            _activeWorldInstanceId = worldInstanceId;
            _zoneObservations.Clear();
            _managedRuntimeObjects.Clear();
            _managedFruitSuppressionPending.Clear();
            _dirty = false;

            string directory = Path.Combine(Paths.ConfigPath, "StrandedDeepNaturalRegrowth", "Worlds");
            Directory.CreateDirectory(directory);
            _sidecarPath = Path.Combine(directory, SafeFileName(seed) + ".txt");
            _state = NaturalRegrowthPersistence.Load(_sidecarPath, seed, LogPersistence);
            Logger.LogInfo("Natural Regrowth world: " + seed + " | sidecar: " + _sidecarPath + " | zones loaded from sidecar: " + _state.Zones.Count.ToString(CultureInfo.InvariantCulture));
        }

        private bool HasAnyPlayableLoadedZone(StrandedWorld world)
        {
            if (world == null || world.Zones == null)
                return false;

            // In this Stranded Deep build SaveContainer.activeInHierarchy is true
            // for many/all world Zones even when their physical island contents
            // are not currently streamed. Gameplay PlayerCamera presence is a
            // safer world-playability signal for the main-menu reload fallback.
            return GetGameplayCameraPositions().Count > 0;
        }

        private List<Vector3> GetGameplayCameraPositions()
        {
            List<Vector3> positions = new List<Vector3>();
            Camera[] cameras = Camera.allCameras;
            int i;
            for (i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (!IsGameplayCamera(camera))
                    continue;

                Vector3 point = camera.transform.position;
                bool duplicate = false;
                int p;
                for (p = 0; p < positions.Count; p++)
                {
                    if ((positions[p] - point).sqrMagnitude < 1.0f)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    positions.Add(point);
            }
            return positions;
        }

        private bool IsGameplayCamera(Camera camera)
        {
            if (camera == null || camera.gameObject == null || !camera.gameObject.activeInHierarchy)
                return false;

            string path = GetTransformPath(camera.transform);
            if (path.IndexOf("PlayerCamera", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (path.IndexOf("PlayerUI", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (path.IndexOf("GameUI", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return true;
        }

        private string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return String.Empty;

            StringBuilder sb = new StringBuilder(transform.name);
            Transform parent = transform.parent;
            while (parent != null)
            {
                sb.Insert(0, parent.name + "/");
                parent = parent.parent;
            }
            return sb.ToString();
        }

        private bool IsZoneEcologyActive(Zone zone, List<Vector3> gameplayCameraPositions)
        {
            if (zone == null || zone.SaveContainer == null || zone.Terrain == null || zone.Terrain.terrainData == null)
                return false;
            if (gameplayCameraPositions == null || gameplayCameraPositions.Count == 0)
                return false;

            Vector3 min = zone.Terrain.transform.position;
            Vector3 size = zone.Terrain.terrainData.size;
            int i;
            for (i = 0; i < gameplayCameraPositions.Count; i++)
            {
                Vector3 point = gameplayCameraPositions[i];
                if (point.x >= min.x && point.x <= min.x + size.x &&
                    point.z >= min.z && point.z <= min.z + size.z)
                    return true;
            }
            return false;
        }

        private bool CanConfirmPalmDisappearance(PalmRecord record, List<Vector3> gameplayCameraPositions)
        {
            if (record == null || gameplayCameraPositions == null || gameplayCameraPositions.Count == 0)
                return false;

            Vector3 palm = record.GroundPosition;
            float radiusSq = PalmDeathConfirmationRadius * PalmDeathConfirmationRadius;
            int i;
            for (i = 0; i < gameplayCameraPositions.Count; i++)
            {
                Vector3 point = gameplayCameraPositions[i];
                float dx = point.x - palm.x;
                float dz = point.z - palm.z;
                if (dx * dx + dz * dz <= radiusSq)
                    return true;
            }
            return false;
        }

        private void HandleWorldUnavailable()
        {
            if (_worldUnavailableSinceRealtime < 0.0f)
            {
                _worldUnavailableSinceRealtime = Time.realtimeSinceStartup;
                Logger.LogInfo("No playable Zone detected; starting world-unload grace window (" + Mathf.Max(0.5f, _worldUnloadGraceSeconds.Value).ToString("0.0", CultureInfo.InvariantCulture) + "s). No ecology state changed yet.");
                return;
            }

            float grace = Mathf.Max(0.5f, _worldUnloadGraceSeconds.Value);
            if (Time.realtimeSinceStartup - _worldUnavailableSinceRealtime < grace)
                return;

            if (_state != null || !String.IsNullOrEmpty(_worldSeed) || !String.IsNullOrEmpty(_candidateWorldSeed) || _managedRuntimeObjects.Count > 0)
            {
                ResetRuntimeState("no playable Zone for " + grace.ToString("0.0", CultureInfo.InvariantCulture) + "s");
                _worldUnavailableSinceRealtime = Time.realtimeSinceStartup;
            }
        }

        private void OnNativeLoadBegin()
        {
            try
            {
                ResetRuntimeState("SaveManager.LoadGame");
                _worldUnavailableSinceRealtime = Time.realtimeSinceStartup;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Natural Regrowth load reset failed: " + ex.Message);
            }
        }

        private void ResetRuntimeState(string reason)
        {
            StopAllCoroutines();

            foreach (KeyValuePair<string, GameObject> pair in _managedRuntimeObjects)
            {
                GameObject go = pair.Value;
                if (go == null) continue;
                try
                {
                    CleanupPalmFruits(go);
                    go.SetActive(false);
                    UnityEngine.Object.Destroy(go);
                }
                catch { }
            }

            if (_state != null || !String.IsNullOrEmpty(_worldSeed))
                Logger.LogInfo("Natural Regrowth runtime state reset: " + reason + ". Unsaved ecology discarded; next world load will re-read the sidecar.");

            _state = null;
            _worldSeed = String.Empty;
            _worldIdentitySource = "unresolved";
            _rawWorldSeed = "unknown-world";
            _candidateWorldSeed = String.Empty;
            _candidateWorldSeedSinceRealtime = 0.0f;
            _sidecarPath = String.Empty;
            _dirty = false;
            _clockResolved = false;
            _lastKnownGameDay = 0.0;
            _activeWorldInstanceId = 0;
            _zoneObservations.Clear();
            _managedRuntimeObjects.Clear();
            _managedFruitSuppressionPending.Clear();
        }

        private void ReconcileLoadedZones(StrandedWorld world, double gameDay, bool forceReconcile)
        {
            Zone[] zones = world.Zones;
            List<Vector3> gameplayCameraPositions = GetGameplayCameraPositions();
            int i;
            for (i = 0; i < zones.Length; i++)
            {
                Zone zone = zones[i];
                if (zone == null)
                    continue;

                string zoneKey = GetZoneKey(zone);
                bool candidateLoaded = IsZoneEcologyActive(zone, gameplayCameraPositions);

                ZoneObservation observation;
                if (!_zoneObservations.TryGetValue(zoneKey, out observation))
                {
                    observation = new ZoneObservation();
                    _zoneObservations.Add(zoneKey, observation);
                }

                if (!candidateLoaded)
                {
                    if (observation.WasLoadedLastScan)
                    {
                        observation.WasLoadedLastScan = false;
                        observation.WasReady = false;
                        observation.LastChildCount = -1;
                        observation.LastPalmCount = -1;
                        observation.ManagedSeenThisLoad.Clear();
                        RemoveManagedRuntimeReferencesForZone(zoneKey);
                        Logger.LogInfo("Zone ecology deactivate: " + zoneKey + " generation=" + observation.LoadGeneration.ToString(CultureInfo.InvariantCulture) + ". Managed sidecar records remain authoritative; no PalmRecord.Alive state changed.");
                    }
                    continue;
                }

                Beam.InteractiveObject_PALM[] palms = zone.SaveContainer.GetComponentsInChildren<Beam.InteractiveObject_PALM>(true);
                List<Beam.InteractiveObject_PALM> livePalms = new List<Beam.InteractiveObject_PALM>();
                int p;
                for (p = 0; p < palms.Length; p++)
                {
                    Beam.InteractiveObject_PALM palm = palms[p];
                    if (palm == null || palm.gameObject == null || !palm.gameObject.activeInHierarchy)
                        continue;
                    if (StageFromPrefabId(palm.PrefabId) < 0)
                        continue;
                    livePalms.Add(palm);
                }

                if (!observation.WasLoadedLastScan)
                {
                    observation.WasLoadedLastScan = true;
                    observation.LoadGeneration++;
                    observation.ManagedSeenThisLoad.Clear();
                    observation.LastChildCount = zone.SaveContainer.childCount;
                    observation.LastPalmCount = livePalms.Count;
                    observation.StableSinceRealtime = Time.realtimeSinceStartup;
                    observation.WasReady = false;
                    Logger.LogInfo("Zone ecology activate: " + zoneKey + " generation=" + observation.LoadGeneration.ToString(CultureInfo.InvariantCulture) + ". Waiting for ZoneReadySeconds before death/restore reconciliation.");
                }
                else if (observation.LastChildCount != zone.SaveContainer.childCount || observation.LastPalmCount != livePalms.Count)
                {
                    observation.LastChildCount = zone.SaveContainer.childCount;
                    observation.LastPalmCount = livePalms.Count;
                    observation.StableSinceRealtime = Time.realtimeSinceStartup;
                    observation.WasReady = false;
                }

                bool ready = forceReconcile || (Time.realtimeSinceStartup - observation.StableSinceRealtime >= Mathf.Max(1.0f, _zoneReadySeconds.Value));
                if (!ready)
                    continue;
                observation.WasReady = true;

                ZoneEcologyState zoneState;
                if (!_state.Zones.TryGetValue(zoneKey, out zoneState))
                {
                    int nativeCount = CountNativePalms(livePalms);
                    if (nativeCount <= 0)
                        continue;

                    zoneState = new ZoneEcologyState();
                    zoneState.ZoneKey = zoneKey;
                    zoneState.NaturalPalmCapacity = nativeCount;
                    zoneState.InitializedGameDay = _clockResolved ? gameDay : 0.0;
                    zoneState.LastEcologyGameDay = zoneState.InitializedGameDay;
                    _state.Zones.Add(zoneKey, zoneState);
                    _dirty = true;
                    Logger.LogInfo("Initialized palm ecology for " + zoneKey + " capacity=" + nativeCount.ToString(CultureInfo.InvariantCulture));
                }

                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (p = 0; p < livePalms.Count; p++)
                {
                    Beam.InteractiveObject_PALM palm = livePalms[p];
                    NaturalRegrowthManagedPalmTag tag = palm.gameObject.GetComponent<NaturalRegrowthManagedPalmTag>();
                    if (tag != null && !String.IsNullOrEmpty(tag.PalmId))
                    {
                        PalmRecord managed;
                        if (zoneState.Palms.TryGetValue(tag.PalmId, out managed))
                        {
                            managed.Alive = true;
                            managed.Stage = StageFromPrefabId(palm.PrefabId);
                            managed.GroundPosition = EstimateGroundPoint(palm.gameObject);
                            managed.Yaw = palm.transform.rotation.eulerAngles.y;
                            seen.Add(managed.Id);
                            observation.ManagedSeenThisLoad.Add(managed.Id);
                            _managedRuntimeObjects[managed.Id] = palm.gameObject;
                            SynchronizeLoadedPalmCoconuts(managed, palm, gameDay);
                        }
                        continue;
                    }

                    string nativeReference = ReadReferenceId(palm);
                    string id = NativePalmId(zoneKey, nativeReference, palm);
                    PalmRecord original;
                    if (!zoneState.Palms.TryGetValue(id, out original))
                    {
                        original = new PalmRecord();
                        original.Id = id;
                        original.ZoneKey = zoneKey;
                        original.NativeReference = nativeReference;
                        original.Managed = false;
                        original.Alive = true;
                        original.Stage = StageFromPrefabId(palm.PrefabId);
                        original.GroundPosition = EstimateGroundPoint(palm.gameObject);
                        original.Yaw = palm.transform.rotation.eulerAngles.y;
                        original.BirthGameDay = SyntheticBirthDayForOriginal(original.Stage, gameDay);
                        zoneState.Palms.Add(id, original);
                        int originalCount = CountOriginalRecords(zoneState);
                        if (originalCount > zoneState.NaturalPalmCapacity)
                            zoneState.NaturalPalmCapacity = originalCount;
                        _dirty = true;
                    }
                    else
                    {
                        original.Alive = true;
                        original.Stage = StageFromPrefabId(palm.PrefabId);
                        original.GroundPosition = EstimateGroundPoint(palm.gameObject);
                        original.Yaw = palm.transform.rotation.eulerAngles.y;
                    }
                    seen.Add(id);
                    SynchronizeLoadedPalmCoconuts(original, palm, gameDay);
                }

                List<PalmRecord> records = new List<PalmRecord>(zoneState.Palms.Values);
                for (p = 0; p < records.Count; p++)
                {
                    PalmRecord record = records[p];
                    if (!record.Alive || seen.Contains(record.Id))
                        continue;

                    if (record.Managed)
                    {
                        if (observation.ManagedSeenThisLoad.Contains(record.Id))
                        {
                            if (CanConfirmPalmDisappearance(record, gameplayCameraPositions))
                            {
                                record.Alive = false;
                                _managedRuntimeObjects.Remove(record.Id);
                                _dirty = true;
                                Logger.LogInfo("Managed palm died: " + record.Id + " zone=" + zoneKey + " loadGeneration=" + observation.LoadGeneration.ToString(CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                Logger.LogInfo("Managed palm temporarily absent but death not confirmed (no gameplay camera within " + PalmDeathConfirmationRadius.ToString("0", CultureInfo.InvariantCulture) + "m): " + record.Id + " zone=" + zoneKey);
                            }
                        }
                        else
                        {
                            GameObject restored = CreateManagedPalm(zone, record);
                            if (restored != null)
                            {
                                observation.ManagedSeenThisLoad.Add(record.Id);
                                _managedRuntimeObjects[record.Id] = restored;
                                seen.Add(record.Id);
                                Logger.LogInfo("Managed palm restored into active Zone: " + record.Id + " zone=" + zoneKey + " loadGeneration=" + observation.LoadGeneration.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    else
                    {
                        if (CanConfirmPalmDisappearance(record, gameplayCameraPositions))
                        {
                            record.Alive = false;
                            _dirty = true;
                            Logger.LogInfo("Native palm no longer present: " + record.Id + " zone=" + zoneKey);
                        }
                        else
                        {
                            Logger.LogInfo("Native palm temporarily absent but death not confirmed (no gameplay camera within " + PalmDeathConfirmationRadius.ToString("0", CultureInfo.InvariantCulture) + "m): " + record.Id + " zone=" + zoneKey);
                        }
                    }
                }
            }
        }

        private void RemoveManagedRuntimeReferencesForZone(string zoneKey)
        {
            if (_state == null || String.IsNullOrEmpty(zoneKey))
                return;

            ZoneEcologyState zoneState;
            if (!_state.Zones.TryGetValue(zoneKey, out zoneState))
                return;

            foreach (PalmRecord record in zoneState.Palms.Values)
            {
                if (record.Managed)
                    _managedRuntimeObjects.Remove(record.Id);
            }
        }

        private void MarkManagedSeenThisLoad(string zoneKey, string palmId)
        {
            if (String.IsNullOrEmpty(zoneKey) || String.IsNullOrEmpty(palmId))
                return;

            ZoneObservation observation;
            if (_zoneObservations.TryGetValue(zoneKey, out observation) && observation.WasLoadedLastScan)
                observation.ManagedSeenThisLoad.Add(palmId);
        }

        private void SynchronizeLoadedPalmCoconuts(PalmRecord record, Beam.InteractiveObject_PALM palm, double gameDay)
        {
            if (!_coconutRegrowthEnabled.Value || !_clockResolved || record == null || palm == null || palm.gameObject == null || !record.Alive)
                return;
            if (record.Managed && _managedFruitSuppressionPending.Contains(record.Id))
                return;

            List<Beam.InteractiveObject_FOOD> fruits = GetLivePalmFruits(palm);
            int count = fruits.Count;
            if (record.Managed && count > 0)
                TagManagedFruits(record, fruits);

            if (record.CoconutState == CoconutUnknown)
            {
                if (count > 0)
                {
                    record.CoconutState = CoconutFruiting;
                    record.CoconutFruitCount = Math.Min(3, count);
                    record.CoconutNextCheckGameDay = 0.0;
                    _dirty = true;
                    Logger.LogInfo("Coconut state initialized from live fruit: palm=" + record.Id + " count=" + count.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    InitializeCoconutWaiting(record, gameDay, "empty palm first observed");
                }
                return;
            }

            if (count > 0)
            {
                int stored = Math.Min(3, count);
                if (record.CoconutState != CoconutFruiting || record.CoconutFruitCount != stored || record.CoconutNextCheckGameDay != 0.0)
                {
                    record.CoconutState = CoconutFruiting;
                    record.CoconutFruitCount = stored;
                    record.CoconutNextCheckGameDay = 0.0;
                    _dirty = true;
                }
                return;
            }

            if (record.CoconutState == CoconutFruiting)
            {
                InitializeCoconutWaiting(record, gameDay, "last coconut detached/picked");
            }
        }

        private void InitializeCoconutWaiting(PalmRecord record, double fromGameDay, string reason)
        {
            if (record == null || !_coconutRegrowthEnabled.Value)
                return;

            record.CoconutState = CoconutWaiting;
            record.CoconutFruitCount = 0;
            record.CoconutCycle = Math.Max(0, record.CoconutCycle) + 1;
            int waitDays = GetCoconutWaitDays(record, record.CoconutCycle);
            record.CoconutNextCheckGameDay = fromGameDay + waitDays;
            _dirty = true;
            Logger.LogInfo("Coconut wait scheduled: palm=" + record.Id + " cycle=" + record.CoconutCycle.ToString(CultureInfo.InvariantCulture) +
                " reason=" + reason + " wait=" + waitDays.ToString(CultureInfo.InvariantCulture) + "d nextDay=" +
                record.CoconutNextCheckGameDay.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private int GetCoconutWaitDays(PalmRecord record, int cycle)
        {
            int minDays = Math.Max(1, _coconutMinDays.Value);
            int maxDays = Math.Max(1, _coconutMaxDays.Value);
            if (maxDays < minDays)
            {
                int swap = minDays;
                minDays = maxDays;
                maxDays = swap;
            }
            int seed = StableHash(_worldSeed + "|" + record.ZoneKey + "|" + record.Id + "|coconut|" + cycle.ToString(CultureInfo.InvariantCulture) + "|wait");
            System.Random random = new System.Random(seed);
            return random.Next(minDays, maxDays + 1);
        }

        private void RunCoconutCatchUp(double gameDay)
        {
            if (!_coconutRegrowthEnabled.Value || !_clockResolved || _state == null)
                return;

            foreach (ZoneEcologyState zone in _state.Zones.Values)
            {
                foreach (PalmRecord record in zone.Palms.Values)
                {
                    if (!record.Alive || record.CoconutState != CoconutWaiting || record.CoconutNextCheckGameDay <= 0.0)
                        continue;

                    int safety = 0;
                    while (record.CoconutState == CoconutWaiting && gameDay >= record.CoconutNextCheckGameDay && safety < 128)
                    {
                        safety++;
                        double eventDay = record.CoconutNextCheckGameDay;
                        int outcomeSeed = StableHash(_worldSeed + "|" + record.ZoneKey + "|" + record.Id + "|coconut|" + record.CoconutCycle.ToString(CultureInfo.InvariantCulture) + "|outcome");
                        System.Random outcomeRandom = new System.Random(outcomeSeed);
                        double successChance = Math.Max(0.0, Math.Min(1.0, _coconutSuccessChance.Value));
                        if (outcomeRandom.NextDouble() < successChance)
                        {
                            int count = ChooseCoconutCount(record);
                            record.CoconutState = CoconutPendingSpawn;
                            record.CoconutFruitCount = count;
                            record.CoconutNextCheckGameDay = 0.0;
                            _dirty = true;
                            Logger.LogInfo("Coconut regrowth succeeded: palm=" + record.Id + " cycle=" + record.CoconutCycle.ToString(CultureInfo.InvariantCulture) +
                                " eventDay=" + eventDay.ToString("0.###", CultureInfo.InvariantCulture) + " count=" + count.ToString(CultureInfo.InvariantCulture) +
                                ". Materialization waits for the palm's Zone to be active.");
                            break;
                        }

                        Logger.LogInfo("Coconut regrowth check failed: palm=" + record.Id + " cycle=" + record.CoconutCycle.ToString(CultureInfo.InvariantCulture) +
                            " eventDay=" + eventDay.ToString("0.###", CultureInfo.InvariantCulture) + ". Scheduling another cycle.");
                        InitializeCoconutWaiting(record, eventDay, "40% failed regrowth check");
                    }

                    if (safety >= 128 && record.CoconutState == CoconutWaiting && gameDay >= record.CoconutNextCheckGameDay)
                        Logger.LogWarning("Coconut catch-up safety cap reached for palm=" + record.Id + ". Remaining cycles will continue on the next scan.");
                }
            }
        }

        private int ChooseCoconutCount(PalmRecord record)
        {
            double w1 = Math.Max(0.0, _coconutOneWeight.Value);
            double w2 = Math.Max(0.0, _coconutTwoWeight.Value);
            double w3 = Math.Max(0.0, _coconutThreeWeight.Value);
            double total = w1 + w2 + w3;
            if (total <= 0.0)
                return 1;

            int seed = StableHash(_worldSeed + "|" + record.ZoneKey + "|" + record.Id + "|coconut|" + record.CoconutCycle.ToString(CultureInfo.InvariantCulture) + "|count");
            System.Random random = new System.Random(seed);
            double roll = random.NextDouble() * total;
            if (roll < w1) return 1;
            if (roll < w1 + w2) return 2;
            return 3;
        }

        private void ResetCoconutAfterManagedStageChange(PalmRecord record, double gameDay)
        {
            if (!_coconutRegrowthEnabled.Value || record == null)
                return;
            InitializeCoconutWaiting(record, gameDay, "managed palm stage transition");
        }

        private void MaterializePendingCoconuts(StrandedWorld world, double gameDay)
        {
            if (!_coconutRegrowthEnabled.Value || !_coconutNativeSpawnAvailable || _state == null || world == null)
                return;

            foreach (ZoneEcologyState zoneState in _state.Zones.Values)
            {
                ZoneObservation observation;
                if (!_zoneObservations.TryGetValue(zoneState.ZoneKey, out observation) || !observation.WasLoadedLastScan || !observation.WasReady)
                    continue;

                Zone zone = FindZone(world, zoneState.ZoneKey);
                if (zone == null || zone.SaveContainer == null)
                    continue;

                foreach (PalmRecord record in zoneState.Palms.Values)
                {
                    if (!record.Alive || record.CoconutState != CoconutPendingSpawn || record.CoconutFruitCount <= 0)
                        continue;
                    if (record.Managed && _managedFruitSuppressionPending.Contains(record.Id))
                        continue;
                    if (record.Managed && !_managedFruitSaveExclusionAvailable)
                        continue;

                    Beam.InteractiveObject_PALM palm = FindRuntimePalmForRecord(zone, record);
                    if (palm == null)
                        continue;

                    int wanted = Math.Max(1, Math.Min(3, record.CoconutFruitCount));
                    if (SpawnExactNativeFruits(record, palm, wanted))
                    {
                        record.CoconutState = CoconutFruiting;
                        record.CoconutFruitCount = wanted;
                        record.CoconutNextCheckGameDay = 0.0;
                        _dirty = true;
                        Logger.LogInfo("Coconut crop materialized: palm=" + record.Id + " count=" + wanted.ToString(CultureInfo.InvariantCulture) +
                            " managed=" + record.Managed.ToString());
                    }
                }
            }
        }

        private Beam.InteractiveObject_PALM FindRuntimePalmForRecord(Zone zone, PalmRecord record)
        {
            if (zone == null || record == null)
                return null;

            if (record.Managed)
            {
                GameObject managed;
                if (_managedRuntimeObjects.TryGetValue(record.Id, out managed) && managed != null)
                    return managed.GetComponent<Beam.InteractiveObject_PALM>();
                return null;
            }

            Beam.InteractiveObject_PALM[] palms = zone.SaveContainer.GetComponentsInChildren<Beam.InteractiveObject_PALM>(true);
            int i;
            for (i = 0; i < palms.Length; i++)
            {
                Beam.InteractiveObject_PALM palm = palms[i];
                if (palm == null || palm.gameObject == null || !palm.gameObject.activeInHierarchy)
                    continue;
                if (palm.gameObject.GetComponent<NaturalRegrowthManagedPalmTag>() != null)
                    continue;
                string reference = ReadReferenceId(palm);
                if (!String.IsNullOrEmpty(record.NativeReference) && String.Equals(record.NativeReference, reference, StringComparison.Ordinal))
                    return palm;
                if (String.Equals(record.Id, NativePalmId(record.ZoneKey, reference, palm), StringComparison.Ordinal))
                    return palm;
            }
            return null;
        }

        private bool SpawnExactNativeFruits(PalmRecord record, Beam.InteractiveObject_PALM palm, int desiredCount)
        {
            if (!_coconutNativeSpawnAvailable || _palmGenerateFruitMethod == null || palm == null || palm.gameObject == null)
                return false;

            List<Beam.InteractiveObject_FOOD> before = GetLivePalmFruits(palm);
            if (before.Count > 0)
            {
                if (record != null && record.Managed)
                    TagManagedFruits(record, before);
                return before.Count == desiredCount;
            }

            desiredCount = Math.Max(1, Math.Min(3, desiredCount));
            try
            {
                _forcedNativeFruitCount = desiredCount;
                _palmGenerateFruitMethod.Invoke(palm, null);
            }
            catch (Exception ex)
            {
                Logger.LogError("Native coconut GenerateFruit invocation failed for palm=" + (record != null ? record.Id : "<unknown>") + ": " + ex);
                return false;
            }
            finally
            {
                _forcedNativeFruitCount = 0;
            }

            List<Beam.InteractiveObject_FOOD> after = GetLivePalmFruits(palm);
            if (record != null && record.Managed && after.Count > 0)
                TagManagedFruits(record, after);

            if (after.Count != desiredCount)
            {
                Logger.LogWarning("Native coconut GenerateFruit count mismatch for palm=" + (record != null ? record.Id : "<unknown>") +
                    ": wanted=" + desiredCount.ToString(CultureInfo.InvariantCulture) + " actual=" + after.Count.ToString(CultureInfo.InvariantCulture) + ". Removing the partial crop and keeping it pending.");
                CleanupPalmFruits(palm.gameObject);
                return false;
            }
            return true;
        }

        private List<Beam.InteractiveObject_FOOD> GetLivePalmFruits(Beam.InteractiveObject_PALM palm)
        {
            List<Beam.InteractiveObject_FOOD> result = new List<Beam.InteractiveObject_FOOD>();
            object raw = GetPalmFruitsRaw(palm);
            IEnumerable enumerable = raw as IEnumerable;
            if (enumerable == null)
                return result;
            foreach (object item in enumerable)
            {
                Beam.InteractiveObject_FOOD food = item as Beam.InteractiveObject_FOOD;
                if (food != null && food.gameObject != null)
                    result.Add(food);
            }
            return result;
        }

        private void TagManagedFruits(PalmRecord record, List<Beam.InteractiveObject_FOOD> fruits)
        {
            if (record == null || !record.Managed || fruits == null)
                return;
            int i;
            for (i = 0; i < fruits.Count; i++)
            {
                Beam.InteractiveObject_FOOD food = fruits[i];
                if (food == null || food.gameObject == null)
                    continue;
                NaturalRegrowthManagedFruitTag tag = food.gameObject.GetComponent<NaturalRegrowthManagedFruitTag>();
                if (tag == null) tag = food.gameObject.AddComponent<NaturalRegrowthManagedFruitTag>();
                tag.PalmId = record.Id;
                tag.ZoneKey = record.ZoneKey;
                tag.Detached = false;
            }
        }

        private void OnPalmFruitDetached(Beam.InteractiveObject_PALM palm, Beam.IAttachable sender)
        {
            try
            {
                Beam.InteractiveObject_FOOD food = sender as Beam.InteractiveObject_FOOD;
                if (food != null && food.gameObject != null)
                {
                    NaturalRegrowthManagedFruitTag fruitTag = food.gameObject.GetComponent<NaturalRegrowthManagedFruitTag>();
                    if (fruitTag != null)
                    {
                        fruitTag.Detached = true;
                        UnityEngine.Object.Destroy(fruitTag);
                    }
                }

                if (!_coconutRegrowthEnabled.Value || !_clockResolved || palm == null)
                    return;
                PalmRecord record = FindPalmRecordForRuntimePalm(palm);
                if (record == null || !record.Alive)
                    return;
                int remaining = GetLivePalmFruits(palm).Count;
                if (remaining <= 0)
                    InitializeCoconutWaiting(record, _lastKnownGameDay, "last coconut detached event");
                else if (record.CoconutState == CoconutFruiting && record.CoconutFruitCount != remaining)
                {
                    record.CoconutFruitCount = Math.Min(3, remaining);
                    _dirty = true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Coconut detach tracking failed: " + ex.Message);
            }
        }

        private PalmRecord FindPalmRecordForRuntimePalm(Beam.InteractiveObject_PALM palm)
        {
            if (_state == null || palm == null || palm.gameObject == null)
                return null;
            NaturalRegrowthManagedPalmTag managedTag = palm.gameObject.GetComponent<NaturalRegrowthManagedPalmTag>();
            if (managedTag != null && !String.IsNullOrEmpty(managedTag.PalmId))
                return FindPalmRecordById(managedTag.PalmId);

            string reference = ReadReferenceId(palm);
            foreach (ZoneEcologyState zone in _state.Zones.Values)
            {
                foreach (PalmRecord record in zone.Palms.Values)
                {
                    if (!record.Managed && !String.IsNullOrEmpty(record.NativeReference) && String.Equals(record.NativeReference, reference, StringComparison.Ordinal))
                        return record;
                }
            }
            return null;
        }

        private PalmRecord FindPalmRecordById(string palmId)
        {
            if (_state == null || String.IsNullOrEmpty(palmId))
                return null;
            foreach (ZoneEcologyState zone in _state.Zones.Values)
            {
                PalmRecord record;
                if (zone.Palms.TryGetValue(palmId, out record))
                    return record;
            }
            return null;
        }

        private void RunEcologyCatchUp(double gameDay)
        {
            if (!_reproductionEnabled.Value || _state == null || !_managedSaveExclusionAvailable)
                return;

            foreach (ZoneEcologyState zone in _state.Zones.Values)
            {
                if (zone.NaturalPalmCapacity <= 0)
                    continue;

                if (zone.LastEcologyGameDay <= 0.0)
                {
                    zone.LastEcologyGameDay = gameDay;
                    _dirty = true;
                    continue;
                }

                int startDay = (int)Math.Floor(zone.LastEcologyGameDay);
                int endDay = (int)Math.Floor(gameDay);
                if (endDay <= startDay)
                {
                    zone.LastEcologyGameDay = gameDay;
                    continue;
                }

                int maxCatchUpDays = 3650;
                if (endDay - startDay > maxCatchUpDays)
                    startDay = endDay - maxCatchUpDays;

                int day;
                for (day = startDay + 1; day <= endDay; day++)
                    ProcessReproductionDay(zone, day);

                zone.LastEcologyGameDay = gameDay;
                _dirty = true;
            }
        }

        private void ProcessReproductionDay(ZoneEcologyState zone, int eventDay)
        {
            int population = CountCurrentPopulation(zone);
            if (population >= zone.NaturalPalmCapacity)
                return;

            List<PalmRecord> parents = new List<PalmRecord>();
            foreach (PalmRecord palm in zone.Palms.Values)
            {
                if (palm.Alive)
                    parents.Add(palm);
            }

            if (parents.Count == 0)
                return;

            parents.Sort(delegate(PalmRecord a, PalmRecord b) { return String.Compare(a.Id, b.Id, StringComparison.Ordinal); });
            int birthsToday = 0;
            int maxBirths = Math.Max(1, _maxBirthsPerZonePerDay.Value);
            int i;
            for (i = 0; i < parents.Count && population < zone.NaturalPalmCapacity && birthsToday < maxBirths; i++)
            {
                PalmRecord parent = parents[i];
                int stage = parent.Managed ? DesiredManagedStage(parent, eventDay) : parent.Stage;
                double deficit = 1.0 - (population / (double)Math.Max(1, zone.NaturalPalmCapacity));
                double chance = Math.Max(0.0, _baseDailyBirthChance.Value) * StageWeight(stage) * Math.Max(0.0, deficit);
                int seed = StableHash(_worldSeed + "|" + zone.ZoneKey + "|" + eventDay.ToString(CultureInfo.InvariantCulture) + "|" + parent.Id);
                System.Random random = new System.Random(seed);
                if (random.NextDouble() >= chance)
                    continue;

                PendingBirthRecord birth = new PendingBirthRecord();
                birth.Id = "B" + _state.NextBirthSequence.ToString(CultureInfo.InvariantCulture);
                _state.NextBirthSequence++;
                birth.ZoneKey = zone.ZoneKey;
                birth.ParentPalmId = parent.Id;
                birth.BirthGameDay = eventDay;
                birth.Seed = StableHash(_worldSeed + "|" + zone.ZoneKey + "|birth|" + birth.Id + "|" + parent.Id);
                birth.PlacementAttempts = 0;
                zone.PendingBirths.Add(birth);
                population++;
                birthsToday++;
                _dirty = true;
                Logger.LogInfo("Palm birth committed: " + birth.Id + " zone=" + zone.ZoneKey + " parent=" + parent.Id + " day=" + eventDay.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void SynchronizeManagedGrowth(StrandedWorld world, double gameDay)
        {
            if (_state == null) return;

            foreach (ZoneEcologyState zoneState in _state.Zones.Values)
            {
                foreach (PalmRecord record in zoneState.Palms.Values)
                {
                    if (!record.Alive || !record.Managed)
                        continue;

                    int desired = DesiredManagedStage(record, gameDay);
                    if (desired != record.Stage)
                    {
                        int oldStage = record.Stage;
                        record.Stage = desired;
                        ResetCoconutAfterManagedStageChange(record, gameDay);
                        _dirty = true;
                        GameObject current;
                        if (_managedRuntimeObjects.TryGetValue(record.Id, out current) && current != null)
                        {
                            Zone zone = FindZone(world, record.ZoneKey);
                            if (zone != null)
                            {
                                GameObject replacement = ReplaceManagedPalm(zone, record, current);
                                if (replacement != null)
                                    _managedRuntimeObjects[record.Id] = replacement;
                            }
                        }
                        Logger.LogInfo("Managed palm growth: " + record.Id + " " + PalmPrefabNames[Math.Max(0, Math.Min(3, oldStage))] + " -> " + PalmPrefabNames[desired]);
                    }
                }
            }
        }

        private void ResolvePendingBirths(StrandedWorld world, double gameDay)
        {
            if (_state == null) return;

            foreach (ZoneEcologyState zoneState in _state.Zones.Values)
            {
                if (zoneState.PendingBirths.Count == 0)
                    continue;

                Zone zone = FindZone(world, zoneState.ZoneKey);
                if (zone == null || zone.SaveContainer == null || zone.Terrain == null || zone.Terrain.terrainData == null)
                    continue;

                ZoneObservation observation;
                if (!_zoneObservations.TryGetValue(zoneState.ZoneKey, out observation) || !observation.WasReady || !observation.WasLoadedLastScan)
                    continue;

                List<PendingBirthRecord> births = new List<PendingBirthRecord>(zoneState.PendingBirths);
                int b;
                for (b = 0; b < births.Count; b++)
                {
                    PendingBirthRecord birth = births[b];
                    PalmRecord parent;
                    if (!zoneState.Palms.TryGetValue(birth.ParentPalmId, out parent))
                        continue;

                    Vector3 ground;
                    float yaw;
                    if (!TryResolveBirthPosition(zone, zoneState, parent, birth, out ground, out yaw))
                    {
                        birth.PlacementAttempts++;
                        continue;
                    }

                    PalmRecord child = new PalmRecord();
                    child.Id = "P" + _state.NextPalmSequence.ToString(CultureInfo.InvariantCulture);
                    _state.NextPalmSequence++;
                    child.ZoneKey = zoneState.ZoneKey;
                    child.ParentPalmId = birth.ParentPalmId;
                    child.Managed = true;
                    child.Alive = true;
                    child.BirthGameDay = birth.BirthGameDay;
                    child.Stage = DesiredManagedStage(child, gameDay);
                    InitializeCoconutWaiting(child, gameDay, "new managed palm");
                    child.GroundPosition = ground;
                    child.Yaw = yaw;

                    GameObject created = CreateManagedPalm(zone, child);
                    if (created == null)
                    {
                        birth.PlacementAttempts++;
                        continue;
                    }

                    zoneState.Palms.Add(child.Id, child);
                    zoneState.PendingBirths.Remove(birth);
                    MarkManagedSeenThisLoad(child.ZoneKey, child.Id);
                    _managedRuntimeObjects[child.Id] = created;
                    _dirty = true;
                    Logger.LogInfo("Palm birth materialized: " + child.Id + " zone=" + child.ZoneKey + " stage=" + PalmPrefabNames[child.Stage] + " pos=" + FormatVector(ground));
                }
            }
        }

        private bool TryResolveBirthPosition(Zone zone, ZoneEcologyState zoneState, PalmRecord parent, PendingBirthRecord birth, out Vector3 ground, out float yaw)
        {
            ground = Vector3.zero;
            yaw = 0.0f;

            Terrain[] terrains;
            if (zone.Terrain != null && zone.Terrain.terrainData != null)
                terrains = new Terrain[] { zone.Terrain };
            else
                terrains = zone.GetComponentsInChildren<Terrain>(true);
            if (terrains == null || terrains.Length == 0)
                return false;

            int attempts = Math.Max(1, _maxPlacementAttemptsPerScan.Value);
            int baseAttempt = Math.Max(0, birth.PlacementAttempts) * attempts;
            int i;
            for (i = 0; i < attempts; i++)
            {
                int attemptIndex = baseAttempt + i;
                System.Random random = new System.Random(unchecked(birth.Seed + attemptIndex * 7919));
                double angle = random.NextDouble() * Math.PI * 2.0;
                float radius = Mathf.Lerp(Mathf.Max(1.0f, _spawnRadiusMin.Value), Mathf.Max(_spawnRadiusMin.Value + 0.1f, _spawnRadiusMax.Value), (float)random.NextDouble());
                Vector3 candidate = parent.GroundPosition + new Vector3((float)Math.Cos(angle) * radius, 0.0f, (float)Math.Sin(angle) * radius);

                Terrain terrain;
                if (!TryFindTerrainAt(terrains, candidate, out terrain))
                    continue;

                Vector3 local = candidate - terrain.transform.position;
                TerrainData data = terrain.terrainData;
                if (data == null || data.size.x <= 0.0f || data.size.z <= 0.0f)
                    continue;

                float nx = local.x / data.size.x;
                float nz = local.z / data.size.z;
                if (nx < 0.0f || nx > 1.0f || nz < 0.0f || nz > 1.0f)
                    continue;

                float y = terrain.SampleHeight(candidate) + terrain.transform.position.y;
                if (y <= _minimumGroundY.Value)
                    continue;

                float slope = data.GetSteepness(nx, nz);
                if (slope > 35.0f)
                    continue;

                candidate.y = y;
                if (!HasPalmSpacing(zoneState, candidate, Mathf.Max(1.0f, _minimumPalmSpacing.Value)))
                    continue;

                if (IsPlacementBlocked(candidate, terrain))
                    continue;

                ground = candidate;
                yaw = (float)(random.NextDouble() * 360.0);
                return true;
            }

            return false;
        }

        private bool TryFindTerrainAt(Terrain[] terrains, Vector3 worldPoint, out Terrain terrain)
        {
            terrain = null;
            int i;
            for (i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null || t.terrainData == null) continue;
                Vector3 min = t.transform.position;
                Vector3 size = t.terrainData.size;
                if (worldPoint.x >= min.x && worldPoint.x <= min.x + size.x && worldPoint.z >= min.z && worldPoint.z <= min.z + size.z)
                {
                    terrain = t;
                    return true;
                }
            }
            return false;
        }

        private bool HasPalmSpacing(ZoneEcologyState zone, Vector3 candidate, float spacing)
        {
            float sqr = spacing * spacing;
            foreach (PalmRecord palm in zone.Palms.Values)
            {
                if (!palm.Alive) continue;
                Vector3 delta = palm.GroundPosition - candidate;
                delta.y = 0.0f;
                if (delta.sqrMagnitude < sqr)
                    return false;
            }
            return true;
        }

        private bool IsPlacementBlocked(Vector3 ground, Terrain terrain)
        {
            Collider[] colliders = Physics.OverlapSphere(ground + Vector3.up * 1.5f, 2.0f, ~0, QueryTriggerInteraction.Ignore);
            int i;
            for (i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null) continue;
                if (collider is TerrainCollider) continue;
                if (terrain != null && collider.transform != null && collider.transform.IsChildOf(terrain.transform)) continue;
                return true;
            }
            return false;
        }

        private GameObject ReplaceManagedPalm(Zone zone, PalmRecord record, GameObject oldPalm)
        {
            CleanupPalmFruits(oldPalm);
            oldPalm.SetActive(false);
            UnityEngine.Object.Destroy(oldPalm);
            _managedRuntimeObjects.Remove(record.Id);
            return CreateManagedPalm(zone, record);
        }

        private GameObject CreateManagedPalm(Zone zone, PalmRecord record)
        {
            if (!_managedSaveExclusionAvailable)
                return null;
            if (zone == null || zone.SaveContainer == null || record.Stage < 0 || record.Stage > 3)
                return null;

            GameObject template = Resources.Load<GameObject>(PalmResourcePaths[record.Stage]);
            if (template == null)
            {
                Beam.InteractiveObject_PALM fallback = FindPalmTemplate(PalmPrefabIds[record.Stage]);
                if (fallback != null) template = fallback.gameObject;
            }
            if (template == null)
            {
                Logger.LogError("Palm template missing: " + PalmPrefabNames[record.Stage]);
                return null;
            }

            try
            {
                Quaternion rotation = Quaternion.Euler(0.0f, record.Yaw, 0.0f);
                GameObject clone = UnityEngine.Object.Instantiate(template, record.GroundPosition, rotation) as GameObject;
                if (clone == null) return null;

                clone.name = ManagedPalmPrefix + record.Id + "_S" + record.Stage.ToString(CultureInfo.InvariantCulture);
                clone.transform.SetParent(zone.SaveContainer, true);
                clone.SetActive(true);

                Bounds bounds;
                if (TryGetCombinedRendererBounds(clone, out bounds))
                    clone.transform.position += Vector3.up * (record.GroundY - bounds.min.y);

                NaturalRegrowthManagedPalmTag tag = clone.GetComponent<NaturalRegrowthManagedPalmTag>();
                if (tag == null) tag = clone.AddComponent<NaturalRegrowthManagedPalmTag>();
                tag.PalmId = record.Id;
                tag.ZoneKey = record.ZoneKey;
                MarkManagedSeenThisLoad(record.ZoneKey, record.Id);

                _managedFruitSuppressionPending.Add(record.Id);
                CleanupPalmFruits(clone);
                StartCoroutine(SuppressInitialFruits(clone, record.Id));
                return clone;
            }
            catch (Exception ex)
            {
                Logger.LogError("CreateManagedPalm failed for " + record.Id + ": " + ex);
                return null;
            }
        }

        private object GetPalmFruitsRaw(Beam.InteractiveObject_PALM palm)
        {
            FieldInfo field = typeof(Beam.InteractiveObject_PALM).GetField("_fruits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null && palm != null ? field.GetValue(palm) : null;
        }

        private MethodInfo FindCalledMethod(MethodInfo source, string declaringTypeFullName, string methodName)
        {
            if (source == null)
                return null;
            try
            {
                MethodBody body = source.GetMethodBody();
                byte[] il = body != null ? body.GetILAsByteArray() : null;
                if (il == null)
                    return null;

                int pos = 0;
                while (pos < il.Length)
                {
                    OpCode opcode;
                    byte first = il[pos++];
                    if (first == 0xFE)
                    {
                        if (pos >= il.Length) break;
                        opcode = FindOpCode(unchecked((short)(0xFE00 | il[pos++])));
                    }
                    else
                    {
                        opcode = FindOpCode((short)first);
                    }
                    if (opcode.Size == 0) break;

                    int operandStart = pos;
                    int operandSize = GetOperandSize(opcode.OperandType, il, pos);
                    if (operandSize < 0 || pos + operandSize > il.Length) break;

                    if (opcode.OperandType == OperandType.InlineMethod || opcode.OperandType == OperandType.InlineTok)
                    {
                        int token = BitConverter.ToInt32(il, operandStart);
                        try
                        {
                            MemberInfo member = source.Module.ResolveMember(token, source.DeclaringType != null ? source.DeclaringType.GetGenericArguments() : null, source.IsGenericMethod ? source.GetGenericArguments() : null);
                            MethodInfo called = member as MethodInfo;
                            if (called != null && called.DeclaringType != null &&
                                String.Equals(called.DeclaringType.FullName, declaringTypeFullName, StringComparison.Ordinal) &&
                                String.Equals(called.Name, methodName, StringComparison.Ordinal))
                                return called;
                        }
                        catch { }
                    }
                    pos += operandSize;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("FindCalledMethod failed for " + source.Name + ": " + ex.Message);
            }
            return null;
        }

        private static OpCode FindOpCode(short value)
        {
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            int i;
            for (i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(OpCode)) continue;
                OpCode op = (OpCode)fields[i].GetValue(null);
                if (op.Value == value) return op;
            }
            return default(OpCode);
        }

        private static int GetOperandSize(OperandType operandType, byte[] il, int pos)
        {
            switch (operandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (pos + 4 > il.Length) return -1;
                    int count = BitConverter.ToInt32(il, pos);
                    return 4 + count * 4;
                default: return -1;
            }
        }
        private IEnumerator SuppressInitialFruits(GameObject palmRoot, string palmId)
        {
            int pass;
            for (pass = 0; pass < 5; pass++)
            {
                yield return null;
                if (palmRoot == null)
                {
                    _managedFruitSuppressionPending.Remove(palmId);
                    yield break;
                }
                CleanupPalmFruits(palmRoot);
            }
            yield return new WaitForSeconds(0.25f);
            if (palmRoot != null) CleanupPalmFruits(palmRoot);

            PalmRecord record = FindPalmRecordById(palmId);
            Beam.InteractiveObject_PALM palm = palmRoot != null ? palmRoot.GetComponent<Beam.InteractiveObject_PALM>() : null;
            if (record != null && palm != null)
            {
                if (record.CoconutState == CoconutUnknown && _clockResolved)
                    InitializeCoconutWaiting(record, _lastKnownGameDay, "managed palm initialized after native fruit suppression");

                if ((record.CoconutState == CoconutFruiting || record.CoconutState == CoconutPendingSpawn) && record.CoconutFruitCount > 0)
                {
                    int wanted = Math.Max(1, Math.Min(3, record.CoconutFruitCount));
                    if (SpawnExactNativeFruits(record, palm, wanted))
                    {
                        record.CoconutState = CoconutFruiting;
                        record.CoconutFruitCount = wanted;
                        record.CoconutNextCheckGameDay = 0.0;
                        _dirty = true;
                    }
                    else
                    {
                        record.CoconutState = CoconutPendingSpawn;
                        record.CoconutFruitCount = wanted;
                        record.CoconutNextCheckGameDay = 0.0;
                        _dirty = true;
                    }
                }
            }
            _managedFruitSuppressionPending.Remove(palmId);
        }

        private int CleanupPalmFruits(GameObject palmRoot)
        {
            if (palmRoot == null) return 0;
            Beam.InteractiveObject_PALM palm = palmRoot.GetComponent<Beam.InteractiveObject_PALM>();
            if (palm == null) return 0;

            try
            {
                FieldInfo field = typeof(Beam.InteractiveObject_PALM).GetField("_fruits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) return 0;
                object raw = field.GetValue(palm);
                IEnumerable enumerable = raw as IEnumerable;
                if (enumerable == null) return 0;

                List<Beam.InteractiveObject_FOOD> fruits = new List<Beam.InteractiveObject_FOOD>();
                foreach (object item in enumerable)
                {
                    Beam.InteractiveObject_FOOD food = item as Beam.InteractiveObject_FOOD;
                    if (food != null) fruits.Add(food);
                }

                int removed = 0;
                int i;
                for (i = 0; i < fruits.Count; i++)
                {
                    Beam.InteractiveObject_FOOD food = fruits[i];
                    if (food == null || food.gameObject == null) continue;
                    food.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(food.gameObject);
                    removed++;
                }

                System.Collections.IList list = raw as System.Collections.IList;
                if (list != null) list.Clear();
                else
                {
                    MethodInfo clear = raw != null ? raw.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public) : null;
                    if (clear != null) clear.Invoke(raw, null);
                }
                return removed;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Managed palm fruit cleanup failed: " + ex.Message);
                return 0;
            }
        }

        private void OnNativeSave()
        {
            try
            {
                if (String.IsNullOrEmpty(_worldSeed) || _state == null)
                    return;

                TickRuntime(true);
                NaturalRegrowthPersistence.SaveAtomic(_sidecarPath, _state);
                _dirty = false;
                Logger.LogInfo("Natural Regrowth sidecar committed with native SaveGame: " + _sidecarPath);
            }
            catch (Exception ex)
            {
                Logger.LogError("Natural Regrowth sidecar save failed: " + ex);
            }
        }

        private int CountNativePalms(List<Beam.InteractiveObject_PALM> palms)
        {
            int count = 0;
            int i;
            for (i = 0; i < palms.Count; i++)
            {
                Beam.InteractiveObject_PALM palm = palms[i];
                if (palm == null || palm.gameObject == null) continue;
                if (palm.gameObject.GetComponent<NaturalRegrowthManagedPalmTag>() == null)
                    count++;
            }
            return count;
        }

        private int CountOriginalRecords(ZoneEcologyState zone)
        {
            int count = 0;
            foreach (PalmRecord record in zone.Palms.Values)
                if (!record.Managed) count++;
            return count;
        }

        private int CountCurrentPopulation(ZoneEcologyState zone)
        {
            int count = zone.PendingBirths.Count;
            foreach (PalmRecord record in zone.Palms.Values)
                if (record.Alive) count++;
            return count;
        }

        private int CountLivingParents(ZoneEcologyState zone)
        {
            int count = 0;
            foreach (PalmRecord record in zone.Palms.Values)
                if (record.Alive) count++;
            return count;
        }

        private int DesiredManagedStage(PalmRecord record, double gameDay)
        {
            double age = Math.Max(0.0, gameDay - record.BirthGameDay);
            if (age >= Math.Max(_growthPalm2Days.Value, _growthPalm1Days.Value)) return 3;
            if (age >= Math.Max(_growthPalm3Days.Value, _growthPalm2Days.Value)) return 2;
            if (age >= Math.Max(0.0f, _growthPalm3Days.Value)) return 1;
            return 0;
        }

        private double SyntheticBirthDayForOriginal(int stage, double gameDay)
        {
            if (!_clockResolved) return 0.0;
            if (stage <= 0) return gameDay;
            if (stage == 1) return gameDay - Math.Max(0.0f, _growthPalm3Days.Value);
            if (stage == 2) return gameDay - Math.Max(_growthPalm3Days.Value, _growthPalm2Days.Value);
            return gameDay - Math.Max(_growthPalm2Days.Value, _growthPalm1Days.Value);
        }

        private double StageWeight(int stage)
        {
            if (stage <= 0) return Math.Max(0.0f, _smallWeight.Value);
            if (stage == 1) return Math.Max(0.0f, _mediumWeight.Value);
            if (stage == 2) return Math.Max(0.0f, _largeWeight.Value);
            return Math.Max(0.0f, _tallWeight.Value);
        }

        private int StageFromPrefabId(uint prefabId)
        {
            if (prefabId == 160u) return 0;
            if (prefabId == 159u) return 1;
            if (prefabId == 158u) return 2;
            if (prefabId == 157u) return 3;
            return -1;
        }

        private Beam.InteractiveObject_PALM FindPalmTemplate(uint prefabId)
        {
            Beam.InteractiveObject_PALM[] palms = Resources.FindObjectsOfTypeAll<Beam.InteractiveObject_PALM>();
            Beam.InteractiveObject_PALM fallback = null;
            int i;
            for (i = 0; i < palms.Length; i++)
            {
                Beam.InteractiveObject_PALM palm = palms[i];
                if (palm == null || palm.PrefabId != prefabId) continue;
                if (!IsLoadedSceneObject(palm.gameObject)) return palm;
                if (fallback == null) fallback = palm;
            }
            return fallback;
        }

        private StrandedWorld FindWorld()
        {
            StrandedWorld[] worlds = Resources.FindObjectsOfTypeAll<StrandedWorld>();
            int i;
            for (i = 0; i < worlds.Length; i++)
            {
                StrandedWorld world = worlds[i];
                if (world != null && world.gameObject != null && IsLoadedSceneObject(world.gameObject))
                    return world;
            }
            return null;
        }

        private Zone FindZone(StrandedWorld world, string zoneKey)
        {
            if (world == null || world.Zones == null) return null;
            int i;
            for (i = 0; i < world.Zones.Length; i++)
            {
                Zone zone = world.Zones[i];
                if (zone != null && String.Equals(GetZoneKey(zone), zoneKey, StringComparison.Ordinal))
                    return zone;
            }
            return null;
        }

        private string GetZoneKey(Zone zone)
        {
            if (zone == null) return "null-zone";
            if (!String.IsNullOrEmpty(zone.Id)) return zone.Id;
            return "seed-" + zone.Seed.ToString(CultureInfo.InvariantCulture);
        }

        private string ResolveWorldIdentity(StrandedWorld world, out string source, out string rawSeed)
        {
            rawSeed = ReadWorldSeed();
            if (!String.IsNullOrEmpty(rawSeed) && !String.Equals(rawSeed, "unknown-world", StringComparison.OrdinalIgnoreCase))
            {
                source = "StrandedWorld.WORLD_SEED";
                return rawSeed;
            }

            source = "zone-fingerprint";
            if (world == null || world.Zones == null || world.Zones.Length == 0)
                return String.Empty;

            List<Zone> zones = new List<Zone>();
            int i;
            for (i = 0; i < world.Zones.Length; i++)
            {
                Zone zone = world.Zones[i];
                if (zone != null) zones.Add(zone);
            }

            if (zones.Count < 10)
                return String.Empty;

            zones.Sort(delegate(Zone a, Zone b)
            {
                string ak = a == null ? String.Empty : (a.Id ?? String.Empty);
                string bk = b == null ? String.Empty : (b.Id ?? String.Empty);
                int cmp = String.Compare(ak, bk, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
                int aseed = a == null ? 0 : a.Seed;
                int bseed = b == null ? 0 : b.Seed;
                return aseed.CompareTo(bseed);
            });

            StringBuilder signature = new StringBuilder(8192);
            signature.Append("zones=").Append(zones.Count.ToString(CultureInfo.InvariantCulture));
            for (i = 0; i < zones.Count; i++)
            {
                Zone zone = zones[i];
                Vector3 pos = zone.transform.position;
                signature.Append('|').Append(zone.Id ?? String.Empty);
                signature.Append('|').Append(zone.ZoneName ?? String.Empty);
                signature.Append('|').Append(zone.Seed.ToString(CultureInfo.InvariantCulture));
                signature.Append('|').Append(Math.Round(pos.x, 1).ToString(CultureInfo.InvariantCulture));
                signature.Append(',').Append(Math.Round(pos.z, 1).ToString(CultureInfo.InvariantCulture));
            }

            string text = signature.ToString();
            int h1 = StableHash("NR-WORLD-A|" + text);
            int h2 = StableHash("NR-WORLD-B|" + text);
            return "worldfp-" + unchecked((uint)h1).ToString("X8", CultureInfo.InvariantCulture) + "-" + unchecked((uint)h2).ToString("X8", CultureInfo.InvariantCulture);
        }

        private string ReadWorldSeed()
        {
            try
            {
                FieldInfo field = typeof(StrandedWorld).GetField("WORLD_SEED", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    object value = field.GetValue(null);
                    if (value != null) return value.ToString();
                }
            }
            catch { }
            return "unknown-world";
        }

        private string ReadReferenceId(Beam.InteractiveObject_PALM palm)
        {
            try
            {
                PropertyInfo property = palm.GetType().GetProperty("ReferenceId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                {
                    object value = property.GetValue(palm, null);
                    if (value != null) return value.ToString();
                }
            }
            catch { }
            return String.Empty;
        }

        private string NativePalmId(string zoneKey, string reference, Beam.InteractiveObject_PALM palm)
        {
            if (!String.IsNullOrEmpty(reference)) return "N:" + reference;
            Vector3 p = palm.transform.position;
            return "N:" + zoneKey + ":" + palm.PrefabId.ToString(CultureInfo.InvariantCulture) + ":" +
                Math.Round(p.x, 1).ToString(CultureInfo.InvariantCulture) + ":" + Math.Round(p.z, 1).ToString(CultureInfo.InvariantCulture);
        }

        private Vector3 EstimateGroundPoint(GameObject palmRoot)
        {
            Bounds bounds;
            if (TryGetCombinedRendererBounds(palmRoot, out bounds))
                return new Vector3(palmRoot.transform.position.x, bounds.min.y, palmRoot.transform.position.z);
            return palmRoot.transform.position;
        }

        private bool TryGetCombinedRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = new Bounds();
            if (root == null) return false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool has = false;
            int i;
            for (i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                if (!has) { bounds = renderer.bounds; has = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return has;
        }

        private bool IsLoadedSceneObject(GameObject go)
        {
            if (go == null) return false;
            try { return go.scene.IsValid() && go.scene.isLoaded; } catch { return false; }
        }

        private int StableHash(string text)
        {
            unchecked
            {
                int hash = 23;
                int i;
                for (i = 0; i < text.Length; i++) hash = hash * 31 + text[i];
                return hash;
            }
        }

        private string SafeFileName(string value)
        {
            if (String.IsNullOrEmpty(value)) return "unknown-world";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(value.Length);
            int i;
            for (i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool bad = false;
                int j;
                for (j = 0; j < invalid.Length; j++) if (c == invalid[j]) { bad = true; break; }
                sb.Append(bad ? '_' : c);
            }
            return sb.ToString();
        }

        private void LogPersistence(string message)
        {
            Logger.LogWarning(message);
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" + value.x.ToString("0.###", CultureInfo.InvariantCulture) + ", " + value.y.ToString("0.###", CultureInfo.InvariantCulture) + ", " + value.z.ToString("0.###", CultureInfo.InvariantCulture) + ")";
        }
    }
}
