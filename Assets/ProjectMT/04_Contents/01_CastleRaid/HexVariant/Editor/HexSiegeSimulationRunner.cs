using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ProjectMT.Contents.Framework;
using ProjectMT.Core.SceneFlow;
using ProjectMT.Shared.Combat;
using ProjectMT.Shared.Unit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace ProjectMT.Contents.CastleRaidHex.Editor
{
    [InitializeOnLoad]
    public static class HexSiegeSimulationRunner
    {
        private const string PendingKey = "HexSiegeSimulator.Pending";
        private const string RestoreKey = "HexSiegeSimulator.Restore";
        private const string ProductionScenePath = "Assets/ProjectMT/00_Scenes/03_CastleRaidHex.unity";
        private const string MonsterCatalogPath = "Assets/ProjectMT/02_Shared/Unit/Data/MonsterCatalog.asset";
        private const string RarityCatalogPath = "Assets/ProjectMT/02_Shared/Unit/Data/MonsterRarityCatalog.asset";
        public const string LatestKey = "HexSiegeSimulator.Latest";
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly List<HexAssaultTraceEvent> TraceScratch = new List<HexAssaultTraceEvent>();
        private static readonly List<HexCastleAssaultUnit> Units = new List<HexCastleAssaultUnit>();
        private static readonly Dictionary<int, HexSiegeSimulationUnitState> RetiredUnits = new Dictionary<int, HexSiegeSimulationUnitState>();
        private static readonly Dictionary<int, int> UnitIds = new Dictionary<int, int>();
        private static readonly Dictionary<int, HexCoordinates> TargetIds = new Dictionary<int, HexCoordinates>();
        private static readonly Dictionary<string, int> PathIds = new Dictionary<string, int>();
        private static readonly Dictionary<int, int> LastCohorts = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> LastPlanGenerations = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> LastPathUpdateTicks = new Dictionary<int, int>();
        private static readonly Dictionary<int, CohortBefore> CohortsBefore = new Dictionary<int, CohortBefore>();
        private struct CohortBefore { public HexCoordinates cell; public int count, leader, version; }
        private static Dictionary<HexCoordinates, HexCastleCellRuntime> cells;
        private static HexCastleAssaultWorld world;
        private static HexCastleGarrisonWorld garrison;
        private static readonly Dictionary<int, int> DefenderIds = new Dictionary<int, int>();
        private static readonly List<HexCastleBarracksRuntime> Barracks = new List<HexCastleBarracksRuntime>();
        private static HexCastleAssaultAIProfileCatalog profile;
        private static HexCastleRaidController raidController;
        private static ContentDefinition runtimeDefinition;
        private static GameObject root;
        private static PlayerLoopSystem oldLoop;
        private static UnityEngine.Random.State oldRandom;
        private static float oldCapture, oldScale;
        private static bool ownsLoop, finishing;
        private static int tick;
        private static long lastSequence;
        public static HexSiegeSimulationResult Live { get; private set; }
        public static bool Running => world != null && !finishing;
        public static string LatestPath => SessionState.GetString(LatestKey, "");
        public static int NextTick => tick;
        private static long previousFrameStamp;
        private static Unity.Profiling.ProfilerRecorder frameAllocationRecorder;
        private static int previousCollections;
        private const string SuiteKey = "HexSiegeSimulator.Suite";
        [Serializable] private sealed class ScenarioSuite { public List<HexSiegeSimulationScenario> remaining = new List<HexSiegeSimulationScenario>(); }
        public static void LaunchScenarioSuite(int stressCount = 0, bool recordVideo = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("실행 종료와 컴파일 완료 후 묶음 검사를 시작하세요.");
            if (!string.IsNullOrEmpty(SessionState.GetString("HexSiegeSimulator.VerifyInput", "")))
                throw new InvalidOperationException("동일 입력 검증이 진행 중입니다.");
            var suite = new ScenarioSuite();
            for (var index = 0; index < 4; index++)
            {
                var input = stressCount == 0 ? HexSiegeSimulationData.LoadRepresentative(index)
                    : HexSiegeSimulationData.CreateStressScenario(index, stressCount);
                input.recordVideo = recordVideo;
                suite.remaining.Add(input);
            }
            SessionState.SetString(SuiteKey, JsonUtility.ToJson(suite));
            LaunchNextInSuite();
        }
        private static void LaunchNextInSuite()
        {
            var json = SessionState.GetString(SuiteKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var suite = JsonUtility.FromJson<ScenarioSuite>(json);
            if (suite.remaining.Count == 0) { SessionState.EraseString(SuiteKey); return; }
            var next = suite.remaining[0]; suite.remaining.RemoveAt(0);
            SessionState.SetString(SuiteKey, JsonUtility.ToJson(suite));
            try { Launch(next); }
            catch { SessionState.EraseString(SuiteKey); throw; }
        }

        public static HexSiegeSimulationSpawn QueueAISpawn(int pattern, HexCoordinates coordinates)
        {
            if (!Running) throw new InvalidOperationException("실행 중인 시뮬레이션이 없습니다.");
            return HexSiegeSimulationData.AddAISpawn(Live.scenario, pattern, coordinates,
                ResolveQueuedSpawnTick(tick, Live.scenario.durationTicks));
        }

        public static int ResolveQueuedSpawnTick(int currentTick, int durationTicks)
        {
            if (currentTick < 0 || durationTicks < 1 || currentTick >= durationTicks - 1)
                throw new InvalidOperationException("실행 종료 직전에는 병력을 추가할 수 없습니다. 새 실험을 시작하세요.");
            return currentTick + 1; // PlayerLoop 순서와 무관하게 아직 지나지 않은 다음 고정 tick에 투입한다.
        }

        public static void VerifyRepeatedScenario(HexSiegeSimulationScenario scenario)
        {
            scenario.Validate();
            SessionState.SetString("HexSiegeSimulator.VerifyInput", JsonUtility.ToJson(scenario));
            SessionState.EraseString("HexSiegeSimulator.VerifyFirst");
            try { Launch(scenario); }
            catch { SessionState.EraseString("HexSiegeSimulator.VerifyInput"); throw; }
        }

        [Serializable] private sealed class SceneRecord { public string path; public bool loaded, active; }
        [Serializable] private sealed class SceneRestore
        {
            public List<SceneRecord> scenes = new List<SceneRecord>();
            public string startScene;
        }

        static HexSiegeSimulationRunner()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeReload;
            EditorApplication.update += RestoreWhenReady;
        }

        private static void RestoreWhenReady()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            try
            {
                if (!string.IsNullOrEmpty(SessionState.GetString(RestoreKey, ""))) { RestoreScenes(); return; }
                if (!string.IsNullOrEmpty(SessionState.GetString(SuiteKey, ""))) { LaunchNextInSuite(); return; }
                var input = SessionState.GetString("HexSiegeSimulator.VerifyInput", "");
                var first = SessionState.GetString("HexSiegeSimulator.VerifyFirst", "");
                if (!string.IsNullOrEmpty(input) && !string.IsNullOrEmpty(first) && first == LatestPath)
                    Launch(JsonUtility.FromJson<HexSiegeSimulationScenario>(input));
            }
            catch (Exception error)
            {
                EditorApplication.update -= RestoreWhenReady;
                Debug.LogException(error);
            }
        }

        public static void Launch(HexSiegeSimulationScenario scenario)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("PlayMode를 종료하고 컴파일 완료 후 실행하세요.");
            scenario.Validate();
            if (!string.IsNullOrEmpty(SessionState.GetString(RestoreKey, "")))
                throw new InvalidOperationException("이전 시뮬레이션의 씬 복원이 완료되지 않았습니다.");
            var restore = new SceneRestore { startScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene) };
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            {
                var scene = SceneManager.GetSceneByPath(setup.path);
                if (scene.IsValid() && (scene.isDirty || string.IsNullOrEmpty(setup.path) && scene.rootCount > 0))
                    throw new InvalidOperationException("작업 씬을 먼저 저장하세요. 시뮬레이터는 저장하지 않은 씬을 교체하지 않습니다.");
                restore.scenes.Add(new SceneRecord { path = setup.path, loaded = setup.isLoaded, active = setup.isActive });
            }
            SessionState.SetString(RestoreKey, JsonUtility.ToJson(restore));
            SessionState.SetString(PendingKey, JsonUtility.ToJson(scenario));
            SessionState.SetFloat("HexSiegeSimulator.OldCapture", Time.captureDeltaTime);
            SessionState.SetFloat("HexSiegeSimulator.OldScale", Time.timeScale);
            try
            {
                EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ProductionScenePath);
                if (EditorSceneManager.playModeStartScene == null)
                    throw new FileNotFoundException("정식 군단의 역습 씬이 없습니다.", ProductionScenePath);
                Time.captureDeltaTime = scenario.step; Time.timeScale = 1f;
                EditorApplication.isPlaying = true;
            }
            catch
            {
                SessionState.EraseString(PendingKey);
                RestoreScenes();
                throw;
            }
        }

        private static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                var json = SessionState.GetString(PendingKey, "");
                if (string.IsNullOrEmpty(json)) return;
                SessionState.EraseString(PendingKey);
                finishing = false; Live = null;
                try { Begin(JsonUtility.FromJson<HexSiegeSimulationScenario>(json)); }
                catch (Exception e) { Finish(false, e.ToString()); }
            }
            else if (state == PlayModeStateChange.ExitingPlayMode && Running) Finish(false, "사용자가 실행을 중단했습니다.");
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.EraseString(PendingKey);
                EditorApplication.delayCall += RestoreScenes;
            }
        }

        private static void RestoreScenes()
        {
            var json = SessionState.GetString(RestoreKey, "");
            if (string.IsNullOrEmpty(json) || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var restore = JsonUtility.FromJson<SceneRestore>(json);
            Time.captureDeltaTime = SessionState.GetFloat("HexSiegeSimulator.OldCapture", 0f);
            Time.timeScale = SessionState.GetFloat("HexSiegeSimulator.OldScale", 1f);
            var saved = restore.scenes.Where(s => !string.IsNullOrEmpty(s.path)).ToArray();
            if (saved.Length > 0)
                EditorSceneManager.RestoreSceneManagerSetup(saved.Select(s => new SceneSetup
                    { path = s.path, isLoaded = s.loaded, isActive = s.active }).ToArray());
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(restore.startScene)
                ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(restore.startScene);
            SessionState.EraseString(RestoreKey);
            ContinueVerification();
        }

        private static void ContinueVerification()
        {
            var json = SessionState.GetString("HexSiegeSimulator.VerifyInput", "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var latest = HexSiegeSimulationData.ReadResult(LatestPath);
                if (!latest.completed) throw new InvalidOperationException("반복 검증 실행 실패: " + latest.error);
                var first = SessionState.GetString("HexSiegeSimulator.VerifyFirst", "");
                if (string.IsNullOrEmpty(first))
                {
                    SessionState.SetString("HexSiegeSimulator.VerifyFirst", LatestPath);
                    SessionState.SetString("HexSiegeSimulator.Baseline", LatestPath);
                    return;
                }
                var comparison = HexSiegeSimulationData.Compare(HexSiegeSimulationData.ReadResult(first), latest);
                File.WriteAllText(Path.Combine(HexSiegeSimulationData.OutputDirectory, "repeat-verification.txt"),
                    "A=" + first + "\nB=" + LatestPath + "\n" + comparison + "\n", new System.Text.UTF8Encoding(false));
                SessionState.EraseString("HexSiegeSimulator.VerifyInput");
                HexSiegeSimulationWindow.Open();
            }
            catch (Exception error)
            {
                SessionState.EraseString("HexSiegeSimulator.VerifyInput");
                Directory.CreateDirectory(HexSiegeSimulationData.OutputDirectory);
                File.WriteAllText(Path.Combine(HexSiegeSimulationData.OutputDirectory, "repeat-verification.txt"), error.ToString());
                Debug.LogException(error);
            }
        }

        private static void Begin(HexSiegeSimulationScenario scenario)
        {
            BeginProduction(scenario);
        }

        private static void BeginProduction(HexSiegeSimulationScenario scenario)
        {
            scenario.Validate();
            finishing = false; tick = 0; lastSequence = 0;
            Units.Clear(); UnitIds.Clear(); DefenderIds.Clear(); TargetIds.Clear(); PathIds.Clear(); LastCohorts.Clear(); CohortsBefore.Clear();
            RetiredUnits.Clear();
            LastPlanGenerations.Clear(); LastPathUpdateTicks.Clear();
            previousFrameStamp = 0;
            frameAllocationRecorder.Dispose();
            frameAllocationRecorder = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            previousCollections = GC.CollectionCount(0);
            Barracks.Clear();
            Live = new HexSiegeSimulationResult
            {
                scenario = scenario, scenarioHash = HexSiegeSimulationData.Hash(JsonUtility.ToJson(scenario)),
                utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                runtimeHash = HexSiegeSimulationData.SourceHash(HexSiegeSimulationData.RuntimePath, "*.cs"),
                driverHash = HexSiegeSimulationData.SourceHash(HexSiegeSimulationData.RuntimePath + "/../Editor", "HexSiegeSimulation*.cs")
            };
            oldLoop = PlayerLoop.GetCurrentPlayerLoop(); oldRandom = UnityEngine.Random.state;
            oldCapture = SessionState.GetFloat("HexSiegeSimulator.OldCapture", Time.captureDeltaTime);
            oldScale = SessionState.GetFloat("HexSiegeSimulator.OldScale", Time.timeScale); ownsLoop = true;
            Time.captureDeltaTime = scenario.step; Time.timeScale = 1f;
            UnityEngine.Random.InitState(scenario.seed);

            profile = ScriptableObject.CreateInstance<HexCastleAssaultAIProfileCatalog>();
            JsonUtility.FromJsonOverwrite(scenario.profileJson, profile);
            runtimeDefinition = ScriptableObject.CreateInstance<ContentDefinition>();
            runtimeDefinition.EditorConfigure(new ContentId("castle_raid"), ContentOpenMode.SeparateScene,
                new SceneId("castle_raid_hex"), null, null);
            var bootstrap = profile.Entries.OrderBy(e => e.MonsterId, StringComparer.Ordinal).First();
            var firstSnapshot = BuildSnapshot(new HexSiegeSimulationSpawn { monster = bootstrap.MonsterId });
            var startData = new SimulatorStartData(new BattlePartySnapshot(new[] { firstSnapshot }));
            var context = new ContentContext(
                new ContentRunInfo(new ContentId("castle_raid"), "simulator", ContentRunMode.SeedTest),
                startData, new SimulatorExit());
            var sceneRoot = UnityEngine.Object.FindFirstObjectByType<HexCastleRaidSceneRoot>();
            raidController = UnityEngine.Object.FindFirstObjectByType<HexCastleRaidController>();
            if (sceneRoot == null || raidController == null)
                throw new InvalidOperationException("정식 03_CastleRaidHex Scene의 Root/Controller를 찾지 못했습니다.");
            typeof(HexCastleRaidController).GetField("difficultyLevel", Hidden).SetValue(raidController, scenario.difficulty);
            typeof(HexCastleRaidController).GetField("generationSeed", Hidden).SetValue(raidController, scenario.seed);
            typeof(HexCastleRaidController).GetField("stageTheme", Hidden).SetValue(raidController, scenario.theme);
            sceneRoot.Initialize(new ContentSceneContext(runtimeDefinition, context));
            if (!raidController.IsRunning || raidController.ActiveStage == null || raidController.ActiveTrapWorld == null)
                throw new InvalidOperationException("정식 군단의 역습 Runtime 초기화가 완료되지 않았습니다.");

            world = raidController.ActiveAssaultWorld;
            root = raidController.ActiveStage.gameObject;
            cells = root.GetComponentsInChildren<HexCastleCellRuntime>(true).ToDictionary(value => value.Coordinates);
            garrison = root.GetComponent<HexCastleGarrisonWorld>();
            var combat = root.GetComponent<HexCastleTurretCombatWorld>();
            if (world == null || garrison == null || combat == null)
                throw new InvalidOperationException("정식 공격/수비/포탑 World 연결이 없습니다.");
            foreach (var cell in cells.Values) TargetIds[cell.GetInstanceID()] = cell.Coordinates;
            Barracks.AddRange(root.GetComponentsInChildren<HexCastleBarracksRuntime>(true));
            foreach (var defender in garrison.Units) RegisterDefender(defender);
            garrison.UnitSpawned += RegisterDefender;

            var expected = new HexCastleGenerationPipeline().GenerateFoundationForDifficulty(
                scenario.seed, scenario.difficulty, scenario.theme, scenario.garrisonTuning).Layout.LayoutSignature;
            if (!string.Equals(expected, scenario.layoutSignature, StringComparison.Ordinal))
                throw new InvalidOperationException("저장 입력과 정식 Scene이 생성한 성 배치가 다릅니다.");
            var actualLayout = (HexCastleLayout)Field(raidController, "layout");
            if (actualLayout.LayoutSignature != scenario.layoutSignature || actualLayout.DefenseLayerCount != scenario.layers)
                throw new InvalidOperationException("현재 정식 생성 규칙이 저장된 성 배치와 다릅니다. 새 성을 생성하세요.");

            var loop = oldLoop;
            var systems = loop.subSystemList.ToList();
            systems.Add(new PlayerLoopSystem { type = typeof(HexSiegeSimulationRunner), updateDelegate = TickProduction });
            loop.subSystemList = systems.ToArray(); PlayerLoop.SetPlayerLoop(loop);
        }

        private static BattleUnitSnapshot BuildSnapshot(HexSiegeSimulationSpawn input)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MonsterCatalog>(MonsterCatalogPath);
            var rarityCatalog = AssetDatabase.LoadAssetAtPath<MonsterRarityCatalog>(RarityCatalogPath);
            if (catalog == null || !catalog.TryGet(input.monster, out var definition) || definition.RuntimeAssetSet == null)
                throw new InvalidOperationException("정식 몬스터 실행 자산이 없습니다: " + input.monster);
            var rarity = rarityCatalog != null && rarityCatalog.TryGetRarity(input.monster, out var resolved)
                ? resolved : MonsterRarity.Common;
            MonsterPassiveSkill passive = null; MonsterActiveSkill active = null;
            rarityCatalog?.TryGetSkillLoadout(input.monster, out passive, out active);
            var projectile = definition.RuntimeAssetSet.CombatProfile?.Action as ProjectileActionDefinition;
            var stats = new UnitStatsSnapshot
            {
                maxHealth = input.health, damage = input.damage, defense = definition.Defense,
                moveSpeed = input.speed, attackRange = input.range, attackInterval = input.interval,
                projectileSpeed = definition.Ranged ? projectile?.ResolvedSpeed ?? 9f : 0f,
                ranged = definition.Ranged, criticalDamageMultiplier = 1.5f
            };
            return new BattleUnitSnapshot(input.monster, stats, definition.VisualTint, definition.RuntimeAssetKey,
                definition.RuntimeAssetSet, Array.Empty<string>(), definition.DisplayName, passive, active, 1,
                new MonsterBattlePresentationSnapshot(definition.Portrait, rarity, 0));
        }

        private static HexCastleAssaultAIProfile ResolveSimulationProfile(HexSiegeSimulationSpawn input)
        {
            return ResolveSimulationProfile(profile, input);
        }

        public static HexCastleAssaultAIProfile ResolveSimulationProfile(
            HexCastleAssaultAIProfileCatalog catalog,
            HexSiegeSimulationSpawn input)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (input == null) throw new ArgumentNullException(nameof(input));
            var original = catalog.Resolve(input.monster);
            if (input.aiPattern < 0) return original;
            var custom = new HexCastleAssaultAIProfile();
            custom.EditorConfigure(input.monster, (HexCastleAssaultPattern)input.aiPattern, input.supportFocus,
                original.SupportRange, original.SupportCooldown, original.SupportDuration, original.HealRatio,
                original.AttackBuffRate, original.DefenseDamageMultiplier);
            return custom;
        }

        private sealed class SimulatorStartData : IPartyDeploymentStartData
        {
            public SimulatorStartData(BattlePartySnapshot party) { Party = party; }
            public BattlePartySnapshot Party { get; }
            public int UnitSlotCount => 1;
            public int SummonsPerSlot => 1;
            public int DeploymentLimit => 1;
        }

        private sealed class SimulatorExit : IContentExit
        {
            public void Complete(IContentResultData result) { }
            public void Fail(IContentResultData result = null) { }
            public void Cancel() { }
        }

        private static void RegisterDefender(HexCastleGarrisonUnit unit)
        {
            DefenderIds.Add(unit.GetInstanceID(), DefenderIds.Count + 1);
            TargetIds[unit.GetInstanceID()] = unit.Coordinates;
        }

        private static void TickProduction()
        {
            if (!Running) return;
            try
            {
                var scenario = Live.scenario;
                if (tick == 0 && scenario.recordVideo) HexSiegeSimulationVideoRecorder.Begin(Live);
                for (var index = 0; index < scenario.spawns.Count; index++)
                {
                    if (scenario.spawns[index].tick != tick) continue;
                    var input = scenario.spawns[index];
                    var unit = raidController.EditorDeploySimulationUnit(
                        BuildSnapshot(input), new HexCoordinates(input.q, input.r), ResolveSimulationProfile(input));
                    if (unit == null) throw new InvalidOperationException("정식 아군 소환 실패: " + (index + 1));
                    if (UnitIds.TryGetValue(unit.GetInstanceID(), out var previousId))
                    {
                        var previous = Live.frames.LastOrDefault()?.units.FirstOrDefault(value => value.id == previousId);
                        if (previous != null) RetiredUnits[previousId] = previous; // 풀에서 재사용돼도 이전 병력의 기록 ID를 보존한다.
                        Units.Remove(unit);
                    }
                    Units.Add(unit); UnitIds[unit.GetInstanceID()] = index + 1;
                }
                foreach (var damage in scenario.damageEvents)
                    if (damage.tick == tick)
                        cells[new HexCoordinates(damage.q, damage.r)].ApplyDamage(damage.amount,
                            cells[new HexCoordinates(damage.q, damage.r)].transform.position);
                foreach (var input in scenario.defenders)
                {
                    if (input.tick != tick) continue;
                    var defender = garrison.EditorSpawnSimulationUnit(input.role, new HexCoordinates(input.q, input.r));
                    if (defender == null)
                        throw new InvalidOperationException("정식 수비대 소환 실패: " + input.role + " @ " + input.q + "," + input.r);
                }
                var stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                var collections = GC.CollectionCount(0);
                var metrics = new HexSiegeSimulationPerformance { tick = tick,
                    wallFrameMs = previousFrameStamp == 0 ? 0 : (stamp - previousFrameStamp) * 1000d / System.Diagnostics.Stopwatch.Frequency,
                    mainThreadAllocatedBytes = -1, // Mono's thread counter returns an unsupported constant zero here.
                    profilerAllocationTick = tick - 1,
                    profilerFrameAllocatedBytes = tick > 0 && frameAllocationRecorder.Valid && frameAllocationRecorder.Count > 0 ? frameAllocationRecorder.LastValue : -1,
                    gcCollections = collections - previousCollections,
                    aiDecisionMs = world.DecisionMillisecondsThisFrame,
                    aiDecisionAllocatedBytes = -1 };
                previousFrameStamp = stamp; previousCollections = collections;
                var frame = new HexSiegeSimulationFrame { tick = tick };
                Drain(frame); Capture(frame); Live.frames.Add(frame); tick++;
                metrics.recordingMs = (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                metrics.recordingAllocatedBytes = -1;
                var diagnosticStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (tick % 10 == 0) HexSiegeSimulationData.RefreshDiagnostics(Live);
                metrics.diagnosticsMs = (System.Diagnostics.Stopwatch.GetTimestamp() - diagnosticStart) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                Live.performance.Add(metrics);
                if (scenario.recordVideo) HexSiegeSimulationVideoRecorder.Capture(Live, tick - 1);
                if (tick >= scenario.durationTicks || Units.Count == scenario.spawns.Count && !world.PalaceCore.IsAlive)
                    Finish(true, null);
            }
            catch (Exception error) { Finish(false, error.InnerException?.ToString() ?? error.ToString()); }
        }

        private static object Field(object obj, string name)
        {
            var field = obj.GetType().GetField(name, Hidden);
            if (field == null) throw new MissingFieldException(obj.GetType().Name, name);
            return field.GetValue(obj);
        }

        private static void Capture(HexSiegeSimulationFrame frame)
        {
            frame.topology = world.TopologyRevision; frame.cost = world.CostRevision;
            frame.occupancy = world.OccupancyRevision; frame.fields = world.CachedRouteFieldCount;
            frame.fullSearches = world.ApproachPlanner.FullSearchCount;
            frame.sharedSearchBuilds = world.ApproachPlanner.SharedSearchBuildCount;
            frame.sharedPathUses = world.ApproachPlanner.SharedPathUseCount;
            frame.localConnectionSearches = world.ApproachPlanner.LocalConnectionSearchCount;
            frame.fullVisitedCells = world.ApproachPlanner.FullVisitedCellCount;
            frame.localVisitedCells = world.ApproachPlanner.LocalVisitedCellCount;
            frame.frontRouteBuilds = world.SharedFrontRouteBuildCount;
            frame.frontRouteReuses = world.SharedFrontRouteReuseCount;
            var currentCohorts = new Dictionary<int, CohortBefore>();
            foreach (var current in (IEnumerable)Field(world, "cohorts"))
            {
                var type = current.GetType();
                var id = (int)type.GetField("CohortId").GetValue(current);
                var leader = (HexCastleAssaultUnit)type.GetField("Leader").GetValue(current);
                currentCohorts[id] = new CohortBefore
                {
                    cell = leader == null ? default : leader.CurrentCoordinates,
                    count = (int)type.GetProperty("MemberCount").GetValue(current),
                    leader = leader != null && UnitIds.TryGetValue(leader.GetInstanceID(), out var leaderId) ? leaderId : 0,
                    version = (int)type.GetField("RouteVersion").GetValue(current)
                };
            }
            var cohorts = (IDictionary)Field(world, "unitCohorts");
            foreach (var u in Units.OrderBy(u => UnitIds[u.GetInstanceID()]))
            {
                var path = (IReadOnlyList<HexCoordinates>)Field(u, "movementPath");
                var pathKey = path == null ? "" : string.Join(";", path.Select(c => c.Q + "," + c.R));
                if (!PathIds.TryGetValue(pathKey, out var pathId))
                {
                    pathId = Live.paths.Count; PathIds[pathKey] = pathId;
                    Live.paths.Add(new HexSiegeSimulationPath { cells = path == null ? new List<HexSiegeSimulationPoint>() : path.Select(c => new HexSiegeSimulationPoint(c)).ToList() });
                }
                var cohort = cohorts.Contains(u.GetInstanceID()) ? (HexCastleAssaultCohortAssignment)cohorts[u.GetInstanceID()] : default;
                var unitId = UnitIds[u.GetInstanceID()];
                currentCohorts.TryGetValue(cohort.CohortId, out var currentGroup);
                LastCohorts.TryGetValue(unitId, out var lastCohort);
                if (cohort.CohortId > 0 && lastCohort != cohort.CohortId)
                {
                    if (lastCohort <= 0)
                    {
                        var joined = CohortsBefore.TryGetValue(cohort.CohortId, out var before);
                        frame.events.Add(new HexSiegeSimulationEvent { tick = tick, unit = unitId,
                            kind = joined ? "CohortJoined" : "CohortCreated",
                            reason = "부대 " + cohort.CohortId + (joined ? " 합류 · 같은 소환 전면 · 열린 3칸 이내 · 이전 인원 " +
                                before.count + "/6" : " 생성 · 현재 합류 가능한 기존 부대 없음") });
                    }
                    LastCohorts[unitId] = cohort.CohortId;
                }
                var target = u.CurrentTarget;
                var strategicTarget = u.CommittedTarget;
                var slot = u.CurrentSlotLease;
                var active = (HexCastleAssaultDecision)Field(u, "activePlan");
                var slotPosition = slot.IsValid ? world.SlotAllocator.Position(slot.Key) : Vector3.zero;
                if (!LastPlanGenerations.TryGetValue(unitId, out var previousGeneration) || previousGeneration != u.PlanGeneration)
                {
                    LastPlanGenerations[unitId] = u.PlanGeneration;
                    LastPathUpdateTicks[unitId] = tick;
                }
                LastPathUpdateTicks.TryGetValue(unitId, out var pathUpdatedTick);
                frame.units.Add(new HexSiegeSimulationUnitState
                {
                    id = UnitIds[u.GetInstanceID()], q = u.CurrentCoordinates.Q, r = u.CurrentCoordinates.R,
                    x = u.transform.position.x, z = u.transform.position.z, hp = u.CurrentHealth,
                    cohort = cohort.CohortId, route = u.RouteId, layer = u.ExpectedDefenseLayer, generation = u.PlanGeneration,
                    leader = currentGroup.leader, sharedRouteVersion = currentGroup.version,
                    path = pathId, pathIndex = (int)Field(u, "pathIndex"), pathCells = pathKey,
                    state = u.ExecutionState.ToString(), reason = u.LastDecisionReason.ToString(), intent = u.CurrentIntent.ToString(),
                    decisionRequested = (bool)Field(u, "strategicDecisionRequested"),
                    nextDecisionIn = Mathf.Max(0f, (float)Field(u, "nextAwarenessAt") - Time.time),
                    retryIn = Mathf.Max(0f, (float)Field(u, "retryNotBefore") - Time.time),
                    pathUpdatedTick = pathUpdatedTick, pathUpdateReason = u.LastDecisionReason.ToString(),
                    pattern = HexCastleAssaultAIPresentation.ResolveTag(u.AIProfile),
                    hasBreachCost = u.HasBreachCostEvaluation,
                    finalApproachIndex = (int)Field(u, "finalApproachIndex"),
                    advanceMoveMs = u.AdvanceMoveMs, advanceDestroyMs = u.AdvanceDestroyMs,
                    advanceContinuationMs = u.AdvanceContinuationMs, advanceAlternativeMs = u.AdvanceAlternativeMs,
                    hasAdvanceAlternative = u.AdvanceAlternativeTarget.HasValue,
                    advanceAlternativeQ = u.AdvanceAlternativeTarget.GetValueOrDefault().Q,
                    advanceAlternativeR = u.AdvanceAlternativeTarget.GetValueOrDefault().R,
                    breachTargetQ = u.LastBreachTargetCoordinates.Q, breachTargetR = u.LastBreachTargetCoordinates.R,
                    breachMoveSeconds = u.LastBreachMovementSeconds,
                    breachDestroySeconds = u.LastBreachDestructionSeconds,
                    breachTotalSeconds = u.LastBreachTotalSeconds,
                    breachExpectedDps = u.LastBreachExpectedDamagePerSecond,
                    hasAlternativeBreachCost = u.HasAlternativeBreachCost,
                    alternativeTargetQ = u.AlternativeBreachTargetCoordinates.Q,
                    alternativeTargetR = u.AlternativeBreachTargetCoordinates.R,
                    alternativeMoveSeconds = u.AlternativeBreachMovementSeconds,
                    alternativeDestroySeconds = u.AlternativeBreachDestructionSeconds,
                    alternativeTotalSeconds = u.AlternativeBreachTotalSeconds,
                    finalPoints = active.FinalApproach == null ? new List<Vector3>() : active.FinalApproach.ToList(),
                    targetKind = target.IsValid ? target.Kind.ToString() : "없음",
                    strategicTargetKind = strategicTarget.IsValid ? strategicTarget.Kind.ToString() : "없음",
                    hasStrategicTarget = strategicTarget.IsValid,
                    strategicTargetQ = strategicTarget.IsValid ? strategicTarget.Coordinates.Q : 0,
                    strategicTargetR = strategicTarget.IsValid ? strategicTarget.Coordinates.R : 0,
                    isTemporaryWork = u.CurrentIntent == HexCastleAssaultIntentKind.LocalFallback &&
                                      strategicTarget.IsValid && target.IsValid &&
                                      strategicTarget.InstanceId != target.InstanceId,
                    targetUnit = !target.IsValid ? 0 : DefenderIds.TryGetValue(target.InstanceId, out var defenderId) ? defenderId :
                        UnitIds.TryGetValue(target.InstanceId, out var allyId) ? allyId : 0,
                    hasTarget = target.IsValid, targetQ = target.IsValid ? target.Coordinates.Q : 0, targetR = target.IsValid ? target.Coordinates.R : 0,
                    hasSlot = slot.IsValid, slotQ = slot.Key.Cell.Q, slotR = slot.Key.Cell.R, slotIndex = slot.Key.Index,
                    slotX = slotPosition.x, slotZ = slotPosition.z,
                    slotState = slot.IsValid ? world.SlotAllocator.State(slot).ToString() : ""
                });
            }
            frame.units.AddRange(RetiredUnits.Values);
            frame.units.Sort((left, right) => left.id.CompareTo(right.id));
            foreach (var d in garrison.Units)
            {
                TargetIds[d.GetInstanceID()] = d.Coordinates;
                var route = (IReadOnlyList<HexCoordinates>)Field(d, "route");
                var jumpStart = (Vector3)Field(d, "jumpStartPosition");
                var jumpEnd = (Vector3)Field(d, "jumpEndPosition");
                frame.defenders.Add(new HexSiegeSimulationDefenderState
                {
                    id = DefenderIds[d.GetInstanceID()], q = d.Coordinates.Q, r = d.Coordinates.R,
                    homeQ = d.HomeCoordinates.Q, homeR = d.HomeCoordinates.R,
                    x = d.transform.position.x, z = d.transform.position.z, hp = d.Health.CurrentHealth,
                    maxHp = d.Health.MaxHealth, damage = d.AttackDamage, speed = d.MoveSpeed,
                    role = d.Role.ToString(), state = d.State.ToString(), pathIndex = (int)Field(d, "routeIndex"),
                    pathCells = string.Join(";", route.Select(c => c.Q + "," + c.R)),
                    targetUnit = d.CurrentTarget != null && UnitIds.TryGetValue(d.CurrentTarget.GetInstanceID(), out var targetId) ? targetId : 0,
                    jumping = d.IsJumping, jumpCount = d.JumpCount,
                    jumpStartX = jumpStart.x, jumpStartZ = jumpStart.z,
                    jumpEndX = jumpEnd.x, jumpEndZ = jumpEnd.z
                });
            }
            foreach (var b in Barracks)
                frame.barracks.Add(new HexSiegeSimulationBarracksState
                {
                    q = b.Structure.Coordinates.Q, r = b.Structure.Coordinates.R, role = b.UnitRole.ToString(),
                    alive = b.IsRunning, producing = b.IsProducing, remaining = b.RemainingProductionSeconds, spawned = b.TotalSpawned
                });
            if (root != null)
                foreach (var trap in root.GetComponentsInChildren<HexCastleTrapRuntime>(true).OrderBy(value => value.Coordinates))
                    frame.traps.Add(new HexSiegeSimulationTrapState
                    {
                        q = trap.Coordinates.Q,
                        r = trap.Coordinates.R,
                        type = trap.TrapType.ToString(),
                        charges = trap.RemainingCharges,
                        maximumCharges = trap.MaximumCharges,
                        cooldown = trap.CooldownRemaining,
                        warning = trap.WarningRemaining,
                        armed = trap.IsArmed
                    });
            foreach (var c in cells.Values.OrderBy(c => c.Coordinates))
                if (c.IsDamageable && (c.CurrentHealth != c.MaxHealth || !c.IsBlocked))
                    frame.damagedCells.Add(new HexSiegeSimulationCellState { q = c.Coordinates.Q, r = c.Coordinates.R, hp = c.CurrentHealth, blocked = c.IsBlocked });
            frame.digest = HexSiegeSimulationData.Hash(JsonUtility.ToJson(frame));
            CohortsBefore.Clear();
            foreach (var current in currentCohorts) CohortsBefore[current.Key] = current.Value;
        }

        private static void Drain(HexSiegeSimulationFrame frame)
        {
            world.Trace.CopyAfter(lastSequence, TraceScratch);
            foreach (var e in TraceScratch)
            {
                if (e.Sequence > lastSequence + 1) Live.traceLost += (int)(e.Sequence - lastSequence - 1);
                var hasPosition = TargetIds.TryGetValue(e.TargetId, out var target);
                var targetPosition = target.ToWorld(HexSpatialContract.CellOuterRadius);
                var defender = garrison.Units.FirstOrDefault(d => d != null && d.GetInstanceID() == e.TargetId);
                if (defender != null) { target = defender.Coordinates; targetPosition = defender.transform.position; hasPosition = true; }
                DefenderIds.TryGetValue(e.TargetId, out var targetUnit);
                UnitIds.TryGetValue(e.UnitId, out var id);
                frame.events.Add(new HexSiegeSimulationEvent { sequence = e.Sequence, tick = tick, unit = id, targetQ = target.Q, targetR = target.R,
                    kind = e.Kind.ToString(), reason = e.Reason.ToString(), hasTargetPosition = hasPosition,
                    targetX = targetPosition.x, targetZ = targetPosition.z, targetUnit = targetUnit });
                if (e.Kind == HexAssaultTraceKind.AttackMarkerApplied) Live.attacks++;
                if (e.Kind == HexAssaultTraceKind.PlanCommitted) Live.decisions++;
                if (e.Kind == HexAssaultTraceKind.FieldBuilt) Live.fieldBuilds++;
                if (e.Kind == HexAssaultTraceKind.FieldReused) Live.fieldReuses++;
                lastSequence = e.Sequence;
            }
        }

        public static void Stop()
        {
            if (Running) Finish(false, "사용자가 실행을 중단했습니다.");
        }

        private static void BeforeReload()
        {
            if (Running) Finish(false, "컴파일 또는 Editor 종료로 중단되었습니다.");
        }

        private static void Finish(bool completed, string error)
        {
            if (finishing) return;
            if (!completed) SessionState.EraseString(SuiteKey);
            finishing = true;
            try
            {
                if (Live != null)
                {
                    try { HexSiegeSimulationVideoRecorder.Finish(); }
                    catch (Exception videoError) { completed = false; error = (error ?? "") + "\n영상: " + videoError.Message; }
                    if (!completed) SessionState.EraseString(SuiteKey);
                    Live.scenarioHash = HexSiegeSimulationData.Hash(JsonUtility.ToJson(Live.scenario));
                    Live.completed = completed; Live.error = error;
                    Live.palaceDestroyed = world != null && world.PalaceCore != null && !world.PalaceCore.IsAlive;
                    Live.outcome = completed ? Live.palaceDestroyed ? "왕궁 파괴" : "제한 시간 도달" : "실행 중단";
                    HexSiegeSimulationData.RefreshDiagnostics(Live);
                    Directory.CreateDirectory(HexSiegeSimulationData.OutputDirectory);
                    var path = Path.Combine(HexSiegeSimulationData.OutputDirectory, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                    File.WriteAllText(path, JsonUtility.ToJson(Live), new System.Text.UTF8Encoding(false));
                    SessionState.SetString(LatestKey, path);
                }
            }
            finally
            {
                frameAllocationRecorder.Dispose();
                if (ownsLoop)
                {
                    PlayerLoop.SetPlayerLoop(oldLoop); Time.captureDeltaTime = oldCapture;
                    Time.timeScale = oldScale; UnityEngine.Random.state = oldRandom; ownsLoop = false;
                }
                try
                {
                    if (garrison != null) garrison.UnitSpawned -= RegisterDefender;
                    raidController?.Shutdown();
                }
                finally
                {
                    if (profile != null) UnityEngine.Object.DestroyImmediate(profile);
                    if (runtimeDefinition != null) UnityEngine.Object.DestroyImmediate(runtimeDefinition);
                    world = null; root = null; profile = null; raidController = null; runtimeDefinition = null;
                    EditorApplication.isPaused = false;
                    if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
                }
            }
        }
    }
}
