using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectMT.Shared.Unit;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Editor
{
    [Serializable]
    public sealed class HexSiegeSimulationSpawn
    {
        public int tick;
        public int q, r;
        public string monster = "argo_01";
        public float health = 100, damage = 20, speed = 3, range = 1.1f, interval = 1;
        public int wave = 1;
        public int aiPattern = -1;
        public HexCastleAssaultSupportFocus supportFocus = HexCastleAssaultSupportFocus.Adaptive;
    }

    [Serializable]
    public sealed class HexSiegeSimulationDefender
    {
        public int tick, q, r;
        public HexCastleGarrisonUnitRole role;
        public float health = 180, damage = 18, speed = 2.2f, interval = 1.1f;
    }

    [Serializable]
    public sealed class HexSiegeSimulationDamage
    {
        public int tick, q, r;
        public float amount;
    }

    [Serializable]
    public sealed class HexSiegeSimulationScenario
    {
        public int schema = 2;
        public bool useProductionRuntime = true;
        public bool useGameGarrison = true;
        public bool stressTest; // Editor-only load test. Never changes the production deployment limit.
        public bool recordVideo; // Capture overhead is intentionally not a benchmark.
        public string name = "공성 AI 실험";
        public int seed = 10801, difficulty = 4, layers;
        public HexCastleTheme theme = HexCastleTheme.PetalBloom;
        public float step = 0.05f;
        public int durationTicks = 1100;
        public string layoutSignature, profileJson, rulesHash;
        public HexCastleThemeOneTuning garrisonTuning = HexCastleThemeOneTuning.CreateDraftDefaults();
        public List<HexCastleCellRecord> cells = new List<HexCastleCellRecord>();
        public List<HexSiegeSimulationSpawn> spawns = new List<HexSiegeSimulationSpawn>();
        public List<HexSiegeSimulationDefender> defenders = new List<HexSiegeSimulationDefender>();
        public List<HexSiegeSimulationDamage> damageEvents = new List<HexSiegeSimulationDamage>();

        public void Validate(bool requireAssaultSpawns = false)
        {
            if (schema != 2) throw new ArgumentException("이전 수동 수비대 기록입니다. 실제 병영 규칙을 사용하는 새 시나리오를 생성하세요. 기존 파일은 보존됩니다.");
            if (!useProductionRuntime) throw new ArgumentException("정식 군단의 역습 Runtime을 사용하는 시나리오만 실행할 수 있습니다.");
            if (!useGameGarrison) throw new ArgumentException("정식 초기 수비대와 병영 생산은 끌 수 없습니다. 군단의 역습과 같은 환경으로 실행하세요.");
            if (difficulty < 1 || difficulty > 10 || !Finite(step) || step < 0.01f || step > 0.1f || durationTicks < 1 || durationTicks > 12000)
                throw new ArgumentException("지원하지 않는 스키마 또는 시간 범위입니다.");
            if (cells == null || cells.Count == 0 || cells.Count > 10000 || spawns == null ||
                requireAssaultSpawns && spawns.Count < 1 || spawns.Count > (stressTest ? 100 : HexSiegeSimulationData.MaximumAssaultSpawns))
                throw new ArgumentException(stressTest ? "스트레스 테스트는 최대 100명입니다." : "일반 실험은 최대 24명입니다.");
            if (string.IsNullOrEmpty(profileJson) || layers < 1) throw new ArgumentException("AI Profile 또는 방어층이 없습니다.");
            var map = cells.Select(c => c.Build()).ToDictionary(c => c.Coordinates);
            foreach (var s in spawns)
            {
                if (s == null || s.tick < 0 || s.tick >= durationTicks || s.aiPattern < -1 || s.aiPattern > 6 || string.IsNullOrWhiteSpace(s.monster) ||
                    !map.TryGetValue(new HexCoordinates(s.q, s.r), out var c) || c.Kind != HexCastleCellKind.Deployment || c.InitialBlocked ||
                    !Positive(s.health) || !Positive(s.damage) || !Positive(s.speed) || !Positive(s.range) || !Positive(s.interval))
                    throw new ArgumentException("소환 시각·배치 Cell·몬스터·능력치 입력이 유효하지 않습니다.");
            }
            if (garrisonTuning == null || defenders == null || defenders.Count > 100) throw new ArgumentException("수비대 규칙과 0~100개의 입력이 필요합니다.");
            foreach (var d in defenders)
                if (d == null || d.tick < 0 || d.tick >= durationTicks || !Enum.IsDefined(typeof(HexCastleGarrisonUnitRole), d.role) ||
                    !map.TryGetValue(new HexCoordinates(d.q, d.r), out var c) ||
                    c.InitialBlocked && c.GateRole != HexCastleGateRole.OpenDefenderPassage ||
                    !Positive(d.health) || !Positive(d.damage) || !Positive(d.speed) || !Positive(d.interval))
                    throw new ArgumentException("수비대 시각·통행 가능한 배치 Cell·역할·능력치가 유효하지 않습니다.");
            if (damageEvents == null || damageEvents.Count > 10000) throw new ArgumentException("피해 입력 목록이 유효하지 않습니다.");
            foreach (var d in damageEvents)
                if (d == null || d.tick < 0 || d.tick >= durationTicks || !Positive(d.amount) ||
                    !map.TryGetValue(new HexCoordinates(d.q, d.r), out var c) || !c.InitialBlocked || c.MaxHealth <= 0)
                    throw new ArgumentException("피해 이벤트는 살아 있는 초기 구조물 좌표와 양수 피해량을 사용해야 합니다.");
            var catalog = ScriptableObject.CreateInstance<HexCastleAssaultAIProfileCatalog>();
            try
            {
                JsonUtility.FromJsonOverwrite(profileJson, catalog);
                if (!catalog.TryValidate(out var error)) throw new ArgumentException(error);
                var ids = new HashSet<string>(catalog.Entries.Select(e => e.MonsterId), StringComparer.OrdinalIgnoreCase);
                if (spawns.Any(s => !ids.Contains(s.monster))) throw new ArgumentException("저장된 AI Profile에 없는 몬스터 ID입니다.");
            }
            finally { UnityEngine.Object.DestroyImmediate(catalog); }
        }

        public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Positive(float v) => Finite(v) && v > 0;
    }

    [Serializable] public sealed class HexSiegeSimulationPoint { public int q, r; public HexSiegeSimulationPoint(HexCoordinates c) { q = c.Q; r = c.R; } }
    [Serializable] public sealed class HexSiegeSimulationPath { public List<HexSiegeSimulationPoint> cells = new List<HexSiegeSimulationPoint>(); }
    [Serializable] public sealed class HexSiegeSimulationCellState { public int q, r; public float hp; public bool blocked; }
    [Serializable]
    public sealed class HexSiegeSimulationUnitState
    {
        public int leader, sharedRouteVersion;
        public int id, q, r, cohort, route, layer, generation, path, pathIndex;
        public float x, z, hp, slotX, slotZ, nextDecisionIn, retryIn;
        public float breachMoveSeconds, breachDestroySeconds, breachTotalSeconds, breachExpectedDps;
        public float alternativeMoveSeconds, alternativeDestroySeconds, alternativeTotalSeconds;
        public string state, reason, intent, pathCells, pattern, targetKind, pathUpdateReason, strategicTargetKind;
        public List<Vector3> finalPoints = new List<Vector3>();
        public int targetUnit;
        public bool hasTarget, hasSlot, decisionRequested, hasBreachCost, hasAlternativeBreachCost;
        public bool hasStrategicTarget, isTemporaryWork;
        public int pathUpdatedTick, finalApproachIndex;
        public int targetQ, targetR, strategicTargetQ, strategicTargetR, slotQ, slotR, slotIndex;
        public int breachTargetQ, breachTargetR, alternativeTargetQ, alternativeTargetR;
        public int advanceMoveMs, advanceDestroyMs, advanceContinuationMs, advanceAlternativeMs;
        public bool hasAdvanceAlternative;
        public int advanceAlternativeQ, advanceAlternativeR;
        public string slotState;
    }
    [Serializable]
    public sealed class HexSiegeSimulationDefenderState
    {
        public int id, q, r, homeQ, homeR, targetUnit, pathIndex, jumpCount;
        public float x, z, hp, maxHp, damage, speed, jumpStartX, jumpStartZ, jumpEndX, jumpEndZ;
        public string role, state, pathCells;
        public bool jumping;
    }
    [Serializable]
    public sealed class HexSiegeSimulationBarracksState
    {
        public int q, r, spawned;
        public float remaining;
        public bool alive, producing;
        public string role;
    }
    [Serializable]
    public sealed class HexSiegeSimulationTrapState
    {
        public int q, r, charges, maximumCharges;
        public float cooldown, warning;
        public bool armed;
        public string type;
    }
    [Serializable]
    public sealed class HexSiegeSimulationEvent
    {
        public long sequence;
        public int unit, targetQ, targetR;
        public int tick;
        public string kind, reason;
        public bool hasTargetPosition;
        public float targetX, targetZ;
        public int targetUnit;
    }
    [Serializable]
    public sealed class HexSiegeSimulationDiagnostic
    {
        public string severity, kind, title, detail;
        public int unit, startTick, endTick;
    }
    [Serializable]
    public sealed class HexSiegeSimulationFrame
    {
        public int fullSearches, sharedSearchBuilds, sharedPathUses, localConnectionSearches;
        public int fullVisitedCells, localVisitedCells, frontRouteBuilds, frontRouteReuses;
        public int tick, topology, cost, occupancy, fields;
        public List<HexSiegeSimulationUnitState> units = new List<HexSiegeSimulationUnitState>();
        public List<HexSiegeSimulationDefenderState> defenders = new List<HexSiegeSimulationDefenderState>();
        public List<HexSiegeSimulationBarracksState> barracks = new List<HexSiegeSimulationBarracksState>();
        public List<HexSiegeSimulationTrapState> traps = new List<HexSiegeSimulationTrapState>();
        public List<HexSiegeSimulationCellState> damagedCells = new List<HexSiegeSimulationCellState>();
        public List<HexSiegeSimulationEvent> events = new List<HexSiegeSimulationEvent>();
        public string digest;
    }
    [Serializable]
    public sealed class HexSiegeSimulationPerformance
    {
        public int tick, gcCollections;
        public double wallFrameMs, aiDecisionMs, recordingMs, diagnosticsMs;
        public long mainThreadAllocatedBytes, aiDecisionAllocatedBytes, recordingAllocatedBytes;
        public int profilerAllocationTick;
        public long profilerFrameAllocatedBytes = -1; // Last completed Unity frame, including Editor and recorder overhead. -1 = unavailable.
    }
    [Serializable]
    public sealed class HexSiegeSimulationResult
    {
        public string videoPath;
        public int schema = 1;
        public string utc, unity, runtimeHash, driverHash, scenarioHash, outcome, error;
        public string scope = "정식 03_CastleRaidHex Scene과 HexCastleRaidController를 실행한다. 실제 절차 성·Cell·공격/수비 AI·정식 몬스터 외형/공격 드라이버·병영·포탑·함정·패시브/스킬을 사용하며 Simulator는 입력·관찰만 담당한다.";
        public HexSiegeSimulationScenario scenario;
        public bool completed, palaceDestroyed;
        public int traceLost, attacks, decisions, fieldBuilds, fieldReuses;
        public List<HexSiegeSimulationPath> paths = new List<HexSiegeSimulationPath>();
        public List<HexSiegeSimulationFrame> frames = new List<HexSiegeSimulationFrame>();
        public List<HexSiegeSimulationDiagnostic> diagnostics = new List<HexSiegeSimulationDiagnostic>();
        public List<HexSiegeSimulationPerformance> performance = new List<HexSiegeSimulationPerformance>(); // 비결정적 계측은 행동 digest와 분리
    }

    public static class HexSiegeSimulationData
    {
        public static readonly int MaximumAssaultSpawns = HexCastleRaidStartData.DeploymentSlotCount *
            HexCastleRaidStartData.ResolveSummonsForAscension(5);
        public static readonly string[] RepresentativeIds = { "focus", "split", "surround", "random" };
        public static readonly string[] RepresentativeLabels = { "한쪽 집중", "양쪽 분산", "사방 배치", "무작위 배치" };
        public static HexSiegeSimulationScenario LoadRepresentative(int index)
        {
            if (index < 0 || index >= RepresentativeIds.Length) throw new ArgumentOutOfRangeException(nameof(index));
            var path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../artifacts/portfolio-ux-20260912/scenario-inputs/", RepresentativeIds[index] + ".json"));
            if (!File.Exists(path)) throw new FileNotFoundException("기술문서와 공유하는 시나리오 입력 파일이 없습니다.", path);
            var input = JsonUtility.FromJson<HexSiegeSimulationScenario>(File.ReadAllText(path, Encoding.UTF8));
            input.Validate(true);
            if (input.name != RepresentativeIds[index] || input.spawns.Count != MaximumAssaultSpawns)
                throw new InvalidOperationException("대표 시나리오 ID 또는 24마리 입력이 일치하지 않습니다.");
            return input;
        }
        public static HexSiegeSimulationScenario CreateStressScenario(int index, int count)
        {
            if (count != 24 && count != 50 && count != 100) throw new ArgumentOutOfRangeException(nameof(count));
            var input = LoadRepresentative(index);
            var templates = input.spawns.ToArray();
            input.spawns.Clear(); input.stressTest = true;
            input.name = "stress-" + RepresentativeIds[index] + "-" + count;
            for (var i = 0; i < count; i++)
            {
                var spawn = JsonUtility.FromJson<HexSiegeSimulationSpawn>(JsonUtility.ToJson(templates[i % templates.Length]));
                spawn.tick = 2; // Same-frame deployment stresses both crowd occupancy and decision bursts.
                input.spawns.Add(spawn);
            }
            input.Validate(true);
            return input;
        }
        private sealed class DiagnosticSample
        {
            public int tick;
            public HexSiegeSimulationUnitState unit;
            public HexSiegeSimulationFrame frame;
            public bool attacked;
            public bool progressed;
        }

        public const string RulesPath = "Assets/ProjectMT/04_Contents/01_CastleRaid/HexVariant/Data/Foundation/HexCastleTheme1Rules.asset";
        public const string RuntimePath = "Assets/ProjectMT/04_Contents/01_CastleRaid/HexVariant/Runtime";
        public static string OutputDirectory => Path.GetFullPath(Path.Combine(Application.dataPath,
            "../../ProjectMT 개인파일/_Temp/ProjectMonsterTaming/HexSiegeSimulator"));

        public static HexSiegeSimulationScenario CreateForWallLayers(int seed, int layers, HexCastleTheme theme)
        {
            if (layers < 2 || layers > 4) throw new ArgumentOutOfRangeException(nameof(layers));
            var difficulty = layers == 2 ? 3 : layers == 3 ? 4 : 7; // 정식 생성에서 시드와 무관하게 층수가 고정된 난이도다.
            var scenario = Create(seed, difficulty, theme, 0, 0, "argo_01");
            if (scenario.layers != layers) throw new InvalidOperationException("정식 생성 결과의 성벽 층수가 다릅니다.");
            scenario.name = ScenarioDisplayName(scenario);
            return scenario;
        }

        public static string ScenarioDisplayName(HexSiegeSimulationScenario scenario)
        {
            if (scenario == null) return "공성 AI 실험";
            return HexCastleThemeCatalog.ResolveKoreanName(scenario.theme) + " · " + scenario.layers + "중벽 · 시드 " + scenario.seed;
        }

        public static HexSiegeSimulationAICard[] GetAICards(HexSiegeSimulationScenario scenario)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            var frozen = ScriptableObject.CreateInstance<HexCastleAssaultAIProfileCatalog>();
            try
            {
                JsonUtility.FromJsonOverwrite(scenario.profileJson, frozen);
                if (!frozen.TryValidate(out var error)) throw new ArgumentException(error);
                return Enum.GetValues(typeof(HexCastleAssaultPattern)).Cast<HexCastleAssaultPattern>()
                    .Select(pattern => new HexSiegeSimulationAICard
                    {
                        pattern = (int)pattern,
                        monsters = frozen.Entries.Where(e => e.Pattern == pattern)
                            .OrderBy(e => e.MonsterId, StringComparer.Ordinal).ToArray()
                    }).ToArray();
            }
            finally { UnityEngine.Object.DestroyImmediate(frozen); }
        }

        public static HexSiegeSimulationSpawn AddAISpawn(HexSiegeSimulationScenario scenario, int pattern,
            HexCoordinates coordinates, int tick)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (tick < 0 || tick >= scenario.durationTicks) throw new ArgumentOutOfRangeException(nameof(tick));
            var pool = GetAICards(scenario).FirstOrDefault(card => card.pattern == pattern)?.monsters;
            if (pool == null || pool.Length == 0) throw new ArgumentException("선택한 AI에 배정된 몬스터가 없습니다.");
            var key = string.Join(":", scenario.seed, scenario.layoutSignature, pattern, scenario.spawns.Count,
                coordinates.Q, coordinates.R, tick);
            var selected = pool[(int)(Convert.ToUInt32(Hash(key).Substring(0, 8), 16) % (uint)pool.Length)];
            var catalog = AssetDatabase.LoadAssetAtPath<MonsterCatalog>(
                "Assets/ProjectMT/02_Shared/Unit/Data/MonsterCatalog.asset");
            if (catalog == null || !catalog.TryGet(selected.MonsterId, out var definition) || definition.RuntimeAssetSet == null)
                throw new InvalidOperationException("정식 몬스터 실행 자산이 없습니다: " + selected.MonsterId);
            var spawn = new HexSiegeSimulationSpawn
            {
                tick = tick, q = coordinates.Q, r = coordinates.R, monster = selected.MonsterId,
                aiPattern = (int)selected.Pattern, supportFocus = selected.SupportFocus, wave = 1,
                health = definition.MaxHealth, damage = definition.AttackPower, speed = definition.MoveSpeed,
                range = definition.AttackRange, interval = 1f / Mathf.Max(0.01f, definition.AttackSpeed)
            };
            AddSingleAssaultSpawn(scenario, spawn, coordinates);
            return scenario.spawns[scenario.spawns.Count - 1];
        }

        public static HexSiegeSimulationScenario Create(int seed, int difficulty, HexCastleTheme theme, int count, int mode, string monster)
        {
            if (count < 0 || count > MaximumAssaultSpawns) throw new ArgumentOutOfRangeException(nameof(count));
            var rules = AssetDatabase.LoadAssetAtPath<HexCastleThemeOneRules>(RulesPath);
            var catalog = Resources.Load<HexCastleAssaultAIProfileCatalog>(HexCastleAssaultAIProfileCatalog.DefaultResourcesPath);
            if (rules == null || catalog == null) throw new InvalidOperationException("정식 생성 규칙/AI 카탈로그가 없습니다.");
            var generated = new HexCastleGenerationPipeline().GenerateFoundationForDifficulty(seed, difficulty, theme, rules.Tuning);
            if (!generated.Validation.IsValid) throw new InvalidOperationException("생성 맵이 검증에 실패했습니다.");
            var layout = generated.Layout;
            var result = new HexSiegeSimulationScenario
            {
                seed = seed, difficulty = difficulty, theme = theme, layers = layout.DefenseLayerCount,
                layoutSignature = layout.LayoutSignature, profileJson = JsonUtility.ToJson(catalog),
                rulesHash = AssetDatabase.GetAssetDependencyHash(RulesPath).ToString(),
                garrisonTuning = JsonUtility.FromJson<HexCastleThemeOneTuning>(JsonUtility.ToJson(rules.Tuning)),
                cells = layout.Cells.Values.OrderBy(c => c.Coordinates).Select(c => new HexCastleCellRecord(c)).ToList()
            };
            result.name = ScenarioDisplayName(result);
            var eligible = layout.Cells.Values.Where(c => c.Kind == HexCastleCellKind.Deployment && !c.InitialBlocked).ToArray();
            var directions = new[] { Vector2.right, Vector2.up, Vector2.left, Vector2.down };
            var sites = directions.Select(d => eligible.OrderBy(c =>
            {
                var p = c.Coordinates.ToWorld(HexSpatialContract.CellOuterRadius);
                return Vector2.SqrMagnitude(new Vector2(p.x, p.z) - d * layout.BattlefieldRadius * 2f);
            }).ThenBy(c => c.Coordinates).First().Coordinates).ToArray();
            for (var i = 0; i < count; i++)
            {
                var site = sites[mode == 0 ? 0 : mode == 2 ? i % 2 * 2 : i % 4];
                result.spawns.Add(new HexSiegeSimulationSpawn { tick = i * 5, q = site.Q, r = site.R, monster = monster });
            }
            result.Validate(count > 0);
            return result;
        }

        public static void AddSingleAssaultSpawn(
            HexSiegeSimulationScenario scenario,
            HexSiegeSimulationSpawn template,
            HexCoordinates coordinates)
        {
            AddAssaultSpawns(scenario, template, coordinates, 1);
        }

        public static void AddAssaultSpawns(
            HexSiegeSimulationScenario scenario,
            HexSiegeSimulationSpawn template,
            HexCoordinates coordinates,
            int count)
        {
            if (scenario == null || template == null) throw new ArgumentNullException();
            if (count < 1 || scenario.spawns.Count + count > MaximumAssaultSpawns)
                throw new ArgumentException("한 번에 1명 이상, 전체 24명 이하로 소환할 수 있습니다.");
            var cell = scenario.cells.Select(value => value.Build()).FirstOrDefault(value => value.Coordinates == coordinates);
            if (cell == null || cell.Kind != HexCastleCellKind.Deployment || cell.InitialBlocked)
                throw new ArgumentException("아군은 청록색 외곽 배치 칸에만 소환할 수 있습니다.");
            for (var index = 0; index < count; index++)
            {
                var spawn = JsonUtility.FromJson<HexSiegeSimulationSpawn>(JsonUtility.ToJson(template));
                spawn.q = coordinates.Q;
                spawn.r = coordinates.R;
                scenario.spawns.Add(spawn);
            }
        }


        public static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
        }

        public static void SetStructureHealth(HexSiegeSimulationScenario scenario, HexCoordinates coordinates, float maximum)
        {
            if (!HexSiegeSimulationScenario.Finite(maximum) || maximum <= 0) throw new ArgumentException("최대 HP는 양수여야 합니다.");
            var record = scenario.cells.FirstOrDefault(c => c.Build().Coordinates == coordinates);
            if (record == null || !record.Build().InitialBlocked || record.Build().MaxHealth <= 0)
                throw new ArgumentException("피해 가능한 구조물만 HP를 바꿀 수 있습니다.");
            typeof(HexCastleCellRecord).GetField("hitPoints", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(record, maximum); // 시뮬레이션 입력 복사본만 변경한다.
            record.Build();
        }

        public static string SourceHash(string directory, string pattern)
        {
            return Hash(string.Join("\n", Directory.GetFiles(directory, pattern, SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal).Select(f => f.Replace('\\', '/') + ":" + Hash(File.ReadAllText(f)))));
        }

        public static string Compare(HexSiegeSimulationResult a, HexSiegeSimulationResult b)
        {
            if (a == null || b == null) return "A와 B 실행 기록을 지정하세요.";
            if (a.scenarioHash != b.scenarioHash) return "입력 다름: 같은 상황의 재현 비교가 아닙니다.";
            var environment = a.unity == b.unity && a.runtimeHash == b.runtimeHash && a.driverHash == b.driverHash;
            var prefix = environment ? "동일 입력·코드 | " : "동일 입력 / 실행 코드·환경 다름 | ";
            var length = Math.Min(a.frames.Count, b.frames.Count);
            for (var i = 0; i < length; i++)
                if (a.frames[i].digest != b.frames[i].digest)
                    return prefix + "최초 차이: " + a.frames[i].tick + " tick (" + (a.frames[i].tick * a.scenario.step).ToString("F2") + "초). " + Difference(a.frames[i], b.frames[i]);
            if (a.frames.Count != b.frames.Count) return prefix + "프레임 길이 다름: " + a.frames.Count + " / " + b.frames.Count;
            if (!a.completed || !b.completed || a.traceLost > 0 || b.traceLost > 0)
                return prefix + "관측 프레임 일치, 실행 중단 또는 Trace 누락으로 전체 재현 판정 불가.";
            return prefix + "기록 일치: " + length + " 프레임 (float 원값 비교, 절대 시각/InstanceID 제외).";
        }

        private static string Difference(HexSiegeSimulationFrame a, HexSiegeSimulationFrame b)
        {
            if (a.units.Count != b.units.Count) return "소환 개수 " + a.units.Count + " / " + b.units.Count;
            for (var i = 0; i < a.units.Count; i++)
                if (JsonUtility.ToJson(a.units[i]) != JsonUtility.ToJson(b.units[i]))
                {
                    var x = a.units[i]; var y = b.units[i];
                    return "몬스터 " + x.id + ": " + x.state + " / " + y.state + ", 목표 (" + x.targetQ + "," + x.targetR + ") / (" + y.targetQ + "," + y.targetR + "). 위치·경로·자리도 비교하세요.";
                }
            return "구조물 HP·점유 revision·사건 순서 또는 비용장 상태가 달라졌습니다.";
        }

        public static void RefreshDiagnostics(HexSiegeSimulationResult result)
        {
            if (result == null) return;
            result.diagnostics = Analyze(result);
        }

        public static List<HexSiegeSimulationDiagnostic> Analyze(HexSiegeSimulationResult result)
        {
            var diagnostics = new List<HexSiegeSimulationDiagnostic>();
            if (result == null) return diagnostics;
            if (result.traceLost > 0)
                AddDiagnostic(diagnostics, "오류", "TraceLost", 0, 0,
                    result.frames.LastOrDefault()?.tick ?? 0, "실행 기록 누락",
                    "Trace " + result.traceLost + "건이 누락돼 목표 선택 원인을 완전히 추적할 수 없습니다.");
            if (!string.IsNullOrEmpty(result.error))
                AddDiagnostic(diagnostics, "오류", "ExecutionStopped", 0, 0,
                    result.frames.LastOrDefault()?.tick ?? 0, "실행 중단",
                    "시뮬레이션이 정상 완료되지 않았습니다. 실행 기록의 오류 내용을 확인하세요.");
            if (result.frames == null || result.frames.Count == 0) return diagnostics;

            var step = Mathf.Max(0.01f, result.scenario?.step ?? 0.05f);
            var idleTicks = Mathf.CeilToInt(1.5f / step);
            var stuckTicks = Mathf.CeilToInt(1.5f / step);
            var temporaryTicks = Mathf.CeilToInt(5f / step);
            var churnWindowTicks = Mathf.CeilToInt(2f / step);
            // Index once: the previous per-unit scan searched every frame's entire roster and
            // event list again, growing quadratically with a 50/100-unit stress load.
            var samplesByUnit = new SortedDictionary<int, List<DiagnosticSample>>();
            var attackedUnits = new HashSet<int>();
            foreach (var frame in result.frames)
            {
                attackedUnits.Clear();
                foreach (var entry in frame.events)
                    if (entry.kind == "AttackMarkerApplied") attackedUnits.Add(entry.unit);
                foreach (var unit in frame.units)
                {
                    if (!samplesByUnit.TryGetValue(unit.id, out var list))
                        samplesByUnit.Add(unit.id, list = new List<DiagnosticSample>());
                    list.Add(new DiagnosticSample
                    {
                        tick = frame.tick,
                        frame = frame,
                        unit = unit,
                        attacked = attackedUnits.Contains(unit.id)
                    });
                }
            }
            foreach (var pair in samplesByUnit)
            {
                var unitId = pair.Key;
                var samples = pair.Value.ToArray();
                if (samples.Length == 0) continue;

                for (var index = 1; index < samples.Length; index++)
                {
                    var previous = samples[index - 1].unit; var current = samples[index].unit;
                    var dx = current.x - previous.x; var dz = current.z - previous.z;
                    samples[index].progressed = dx * dx + dz * dz > 0.000001f;
                }
                FindIntervals(samples, value => !value.attacked && !value.progressed && IsIdleWithoutUsefulWork(value.unit), idleTicks,
                    (start, end) => AddDiagnostic(diagnostics, "주의", "Idle", unitId, start.tick, end.tick,
                        "행동 없는 병력",
                        DurationLabel(start.tick, end.tick, step) + " 동안 살아 있지만 목표·공격·이동이 없습니다."));
                FindStuckMovement(samples, unitId, stuckTicks, step, diagnostics);
                FindIntervals(samples, value => value.unit.hp > 0 && value.unit.isTemporaryWork, temporaryTicks,
                    (start, end) => AddDiagnostic(diagnostics, "정보", "LongTemporaryWork", unitId, start.tick, end.tick,
                        "임시 공격 장기화",
                        DurationLabel(start.tick, end.tick, step) + " 동안 4순위 임시 공격을 수행했습니다. 전략 목표 복귀 시점을 기록에서 확인하세요."));

                for (var index = 0; index < samples.Length; index++)
                {
                    var current = samples[index];
                    if (current.unit.hp <= 0 || !current.unit.isTemporaryWork) continue;
                    if (current.unit.intent != "LocalFallback" || !current.unit.hasTarget)
                        AddDiagnostic(diagnostics, "오류", "TemporaryWorkContract", unitId, current.tick, current.tick,
                            "임시 공격 계약 불일치", "4순위 행동인데 LocalFallback 또는 현재 목표 기록이 없습니다.");
                    if (index == 0 || !samples[index - 1].unit.isTemporaryWork) continue;
                    var previous = samples[index - 1].unit;
                    if (previous.cohort != current.unit.cohort ||
                        previous.strategicTargetQ != current.unit.strategicTargetQ ||
                        previous.strategicTargetR != current.unit.strategicTargetR ||
                        previous.strategicTargetKind != current.unit.strategicTargetKind)
                        AddDiagnostic(diagnostics, "오류", "TemporaryWorkCommitmentChanged", unitId,
                            samples[index - 1].tick, current.tick, "임시 공격 중 전략 소속 변경",
                            "4순위 임시 공격 도중 부대 또는 전략 목표가 바뀌었습니다.");
                }

                var changes = new List<int>();
                HexSiegeSimulationUnitState lastTarget = null;
                foreach (var sample in samples)
                {
                    if (sample.unit.hp <= 0) { lastTarget = null; continue; }
                    if (lastTarget != null && TargetConfirmedDead(lastTarget, sample.frame)) lastTarget = null;
                    if (!sample.unit.hasTarget) continue; // 목표 없는 중간 프레임을 전환 2회로 세지 않음
                    if (lastTarget != null && !SameTarget(lastTarget, sample.unit)) changes.Add(sample.tick);
                    lastTarget = TargetConfirmedDead(sample.unit, sample.frame) ? null : sample.unit;
                }
                for (var index = 0; index + 3 < changes.Count; index++)
                {
                    if (changes[index + 3] - changes[index] > churnWindowTicks) continue;
                    AddDiagnostic(diagnostics, "주의", "TargetChurn", unitId, changes[index], changes[index + 3],
                        "목표 반복 전환", "2초 안에 살아 있는 목표 사이에서 4회 이상 바뀌었습니다. 확인된 목표 파괴·자신의 사망·목표 없는 중간 프레임은 제외합니다.");
                    break;
                }
            }
            return diagnostics.OrderBy(value => SeverityOrder(value.severity)).ThenBy(value => value.startTick)
                .ThenBy(value => value.unit).ToList();
        }

        private static void FindIntervals(DiagnosticSample[] samples, Func<DiagnosticSample, bool> predicate,
            int minimumTicks, Action<DiagnosticSample, DiagnosticSample> onFound)
        {
            var start = -1;
            for (var index = 0; index <= samples.Length; index++)
            {
                if (index < samples.Length && predicate(samples[index]))
                {
                    if (start < 0) start = index;
                    continue;
                }
                if (start >= 0)
                {
                    var first = samples[start];
                    var last = samples[index - 1];
                    if (last.tick - first.tick + 1 >= minimumTicks) onFound(first, last);
                    start = -1;
                }
            }
        }

        private static void FindStuckMovement(DiagnosticSample[] samples, int unitId, int minimumTicks, float step,
            List<HexSiegeSimulationDiagnostic> diagnostics)
        {
            var start = -1;
            for (var index = 0; index <= samples.Length; index++)
            {
                var moving = index < samples.Length && IsMoving(samples[index].unit) && samples[index].unit.hp > 0;
                if (moving)
                {
                    if (start < 0) start = index;
                    continue;
                }
                if (start >= 0)
                {
                    var first = samples[start]; var last = samples[index - 1];
                    var duration = last.tick - first.tick + 1;
                    var dx = last.unit.x - first.unit.x;
                    var dz = last.unit.z - first.unit.z;
                    if (duration >= minimumTicks && dx * dx + dz * dz < 0.01f)
                        AddDiagnostic(diagnostics, "주의", "NoMovementProgress", unitId, first.tick, last.tick,
                            "이동 진척 없음", DurationLabel(first.tick, last.tick, step) + " 동안 이동 상태지만 위치 변화가 거의 없습니다.");
                    start = -1;
                }
            }
        }

        private static bool IsIdleWithoutUsefulWork(HexSiegeSimulationUnitState unit)
        {
            if (unit == null || unit.hp <= 0 || unit.isTemporaryWork || unit.state == "AwaitInitialPlan") return false;
            return !unit.hasTarget || unit.state == "Holding" || unit.state == "Waiting" || unit.state == "HoldPosition";
        }

        private static bool IsMoving(HexSiegeSimulationUnitState unit)
        {
            if (unit == null || !unit.hasTarget) return false;
            return unit.state == "Moving" || unit.state == "FollowingRoute" || unit.state == "Advancing" ||
                   unit.state == "Traversing" || unit.state == "Aligning";
        }

        private static bool TargetConfirmedDead(HexSiegeSimulationUnitState target, HexSiegeSimulationFrame frame)
        {
            if (target.targetKind == "Defender") return frame.defenders.Any(value => value.id == target.targetUnit && value.hp <= 0);
            if (target.targetKind == "Ally") return frame.units.Any(value => value.id == target.targetUnit && value.hp <= 0);
            return frame.damagedCells.Any(value => value.q == target.targetQ && value.r == target.targetR && value.hp <= 0);
        }

        private static bool SameTarget(HexSiegeSimulationUnitState left, HexSiegeSimulationUnitState right)
        {
            if (left.hasTarget != right.hasTarget) return false;
            if (!left.hasTarget) return true;
            if (left.targetKind != right.targetKind) return false;
            if (left.targetKind == "Defender" || left.targetKind == "Ally") return left.targetUnit == right.targetUnit;
            return left.targetQ == right.targetQ && left.targetR == right.targetR;
        }

        private static void AddDiagnostic(List<HexSiegeSimulationDiagnostic> target, string severity, string kind,
            int unit, int startTick, int endTick, string title, string detail)
        {
            if (target.Any(value => value.kind == kind && value.unit == unit && value.startTick == startTick && value.endTick == endTick)) return;
            target.Add(new HexSiegeSimulationDiagnostic
                { severity = severity, kind = kind, unit = unit, startTick = startTick, endTick = endTick, title = title, detail = detail });
        }

        private static string DurationLabel(int startTick, int endTick, float step)
        {
            return Mathf.RoundToInt(Mathf.Max(0, endTick - startTick + 1) * step * 1000f) + "ms";
        }

        private static int SeverityOrder(string severity) => severity == "오류" ? 0 : severity == "주의" ? 1 : 2;

        public static HexSiegeSimulationResult ReadResult(string path)
        {
            var value = JsonUtility.FromJson<HexSiegeSimulationResult>(File.ReadAllText(path, Encoding.UTF8));
            if (value == null || value.schema != 1 || value.scenario == null || value.frames == null)
                throw new ArgumentException("지원하는 실행 기록 파일이 아닙니다.");
            value.scenario.Validate();
            if (value.scenarioHash != Hash(JsonUtility.ToJson(value.scenario))) throw new ArgumentException("시나리오 해시가 일치하지 않습니다.");
            foreach (var frame in value.frames)
            {
                var expected = frame.digest;
                frame.digest = null;
                var actual = Hash(JsonUtility.ToJson(frame));
                frame.digest = expected;
                if (expected != actual) throw new ArgumentException("프레임 해시 불일치: " + frame.tick);
            }
            RefreshDiagnostics(value);
            return value;
        }
    }

    public sealed class HexSiegeSimulationAICard
    {
        public int pattern;
        public HexCastleAssaultAIProfile[] monsters;
    }
}
