using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectMT.Shared.Unit;
using UnityEditor;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Editor
{
    public sealed class HexSiegeSimulationWindow : EditorWindow
    {
        private static readonly HexCastleTheme[] ThemeOptions = HexCastleThemeCatalog.Themes.ToArray();
        private static readonly string[] ThemeLabels = ThemeOptions.Select(HexCastleThemeCatalog.ResolveKoreanName).ToArray();
        [SerializeField] private HexSiegeSimulationScenario scenario;
        [SerializeField] private int seed = 10801, wallLayers = 3, selectedPattern, selectedUnit = 1;
        [SerializeField] private HexCastleTheme theme = HexCastleTheme.PetalBloom;
        [SerializeField] private bool showPaths = true, showSlots = true, showGroups = true;
        [SerializeField] private bool randomSeed = true;
        [SerializeField] private int representativeIndex;
        [SerializeField] private bool showStress;
        [SerializeField] private int stressSize = 1;
        [SerializeField] private bool recordStressVideo;
        private HexSiegeSimulationResult result;
        private string loadedPath, message = "";
        private int selectedDefender;
        private bool showStates;
        private bool showAllPaths, showDead, showDiagnostics, showHistory, showSquads, showScope, showIssues = true;
        private bool focusSelection;
        private int rosterTab;
        private Vector2 rosterScroll;
        private HexSiegeSimulationAICard[] deploymentCards = Array.Empty<HexSiegeSimulationAICard>();
        private string cardsProfileJson;
        private readonly List<Rect> mapLabels = new List<Rect>();
        [SerializeField] private int readabilityVersion;
        private bool inspectMode;
        private float mapZoom = 1;
        private Vector2 mapPan;
        private Vector2 scroll;
        private int frameIndex;
        private float replaySpeed = 1;
        private bool replay, followLive = true;
        private double replayAt;
        private HexCoordinates? selectedCell;
        private Dictionary<HexCoordinates, HexCastleCell> map;
        private string mapKey;
        private string currentRuntimeHash, currentDriverHash;
        private readonly Dictionary<string, string> monsterNames = new Dictionary<string, string>();

        [MenuItem("ProjectMT/군단의 역습/공성 AI 시뮬레이터")]
        public static void Open()
        {
            var window = GetWindow<HexSiegeSimulationWindow>("공성 AI 시뮬레이터");
            window.minSize = new Vector2(1080, 720);
            window.Show();
        }

        internal void PrepareVideoFrame(HexSiegeSimulationResult recording, int tick)
        {
            result = recording; scenario = recording.scenario; frameIndex = tick;
            followLive = true; replay = false; inspectMode = true;
            showGroups = true; showAllPaths = true; showStates = false; showIssues = false;
            showSquads = true; selectedUnit = 0; selectedDefender = 0; Repaint();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            cardsProfileJson = null;
            if (readabilityVersion < 3)
            {
                showStates = false; showAllPaths = false; showDead = false;
                showHistory = false; showDiagnostics = false; showScope = false;
                showPaths = true; showSlots = true; mapZoom = 1; mapPan = Vector2.zero;
                scroll = Vector2.zero; inspectMode = false; readabilityVersion = 3;
            }
            RefreshCodeHashes();
            if (result == null) loadedPath = HexSiegeSimulationRunner.LatestPath;
            EditorApplication.update += Refresh;
            if (scenario == null || scenario.cells.Count == 0) Try(CreateScenario);
        }

        private void OnDisable() { EditorApplication.update -= Refresh; }

        private void Refresh()
        {
            if (HexSiegeSimulationRunner.Running)
            {
                result = HexSiegeSimulationRunner.Live;
                if (followLive) frameIndex = Math.Max(0, result.frames.Count - 1);
                Repaint(); return;
            }
            var path = HexSiegeSimulationRunner.LatestPath;
            if (!string.IsNullOrEmpty(path) && loadedPath != path && File.Exists(path))
            {
                loadedPath = path;
                Try(() => LoadResult(path));
            }
            if (replay && result != null && result.frames.Count > 0)
            {
                var now = EditorApplication.timeSinceStartup;
                var advance = (int)((now - replayAt) * replaySpeed / result.scenario.step);
                if (advance > 0)
                {
                    frameIndex = Math.Min(result.frames.Count - 1, frameIndex + advance);
                    replayAt = now;
                    if (frameIndex == result.frames.Count - 1) replay = false;
                    Repaint();
                }
            }
        }

        private void Try(Action action)
        {
            try { action(); message = ""; }
            catch (Exception e) { message = e.Message; Repaint(); }
        }

        private void CreateScenario()
        {
            if (randomSeed) seed = unchecked((int)DateTime.UtcNow.Ticks);
            scenario = HexSiegeSimulationData.CreateForWallLayers(seed, wallLayers, theme);
            selectedCell = null; mapKey = null; result = null; inspectMode = false; replay = false;
            RefreshCards();
        }

        private void ResetToSetup()
        {
            scenario = HexSiegeSimulationData.CreateForWallLayers(seed, wallLayers, theme);
            selectedCell = null; result = null; inspectMode = false; replay = false; frameIndex = 0;
            selectedUnit = 0; selectedDefender = 0; mapKey = null; followLive = true;
            loadedPath = HexSiegeSimulationRunner.LatestPath;
            RefreshCards();
        }

        private void RefreshCodeHashes()
        {
            try
            {
                currentRuntimeHash = HexSiegeSimulationData.SourceHash(HexSiegeSimulationData.RuntimePath, "*.cs");
                currentDriverHash = HexSiegeSimulationData.SourceHash(HexSiegeSimulationData.RuntimePath + "/../Editor", "HexSiegeSimulation*.cs");
            }
            catch
            {
                currentRuntimeHash = null; currentDriverHash = null;
            }
        }

        private void LoadResult(string path)
        {
            result = HexSiegeSimulationData.ReadResult(path); loadedPath = path; frameIndex = 0; replay = false;
            scenario = JsonUtility.FromJson<HexSiegeSimulationScenario>(JsonUtility.ToJson(result.scenario));
            seed = scenario.seed; wallLayers = scenario.layers; theme = scenario.theme; RefreshCards();
            inspectMode = true;
            selectedUnit = result.frames.Count > 0 ? result.frames[0].units.FirstOrDefault(u => u.hp > 0)?.id ?? 0 : 0;
            selectedDefender = 0; scroll = Vector2.zero;
            mapKey = null;
        }

        private void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove) Repaint();
            var busy = EditorApplication.isPlayingOrWillChangePlaymode;
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("공성 AI · 배치하고 실행하며 관찰", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(busy || scenario == null))
            {
                if (GUILayout.Button("같은 입력 재실행", EditorStyles.toolbarButton)) Try(() =>
                {
                    if (result == null) throw new InvalidOperationException("먼저 실행 기록이 필요합니다.");
                    followLive = true;
                    HexSiegeSimulationRunner.Launch(result.scenario);
                });
            }
            using (new EditorGUI.DisabledScope(!HexSiegeSimulationRunner.Running))
            {
                if (GUILayout.Button(EditorApplication.isPaused ? "계속" : "일시정지", EditorStyles.toolbarButton)) EditorApplication.isPaused = !EditorApplication.isPaused;
                if (GUILayout.Button("한 프레임", EditorStyles.toolbarButton)) { EditorApplication.isPaused = true; EditorApplication.Step(); }
                if (GUILayout.Button("실행 종료", EditorStyles.toolbarButton)) HexSiegeSimulationRunner.Stop();
            }
            EditorGUILayout.EndHorizontal();
            showScope = EditorGUILayout.Foldout(showScope, "사용 안내 / 시뮬레이션 범위", true);
            if (showScope) EditorGUILayout.HelpBox("별도 모사 로직이 아닙니다. 정식 03_CastleRaidHex 씬에서 군단의 역습 컨트롤러·공격 AI·경로탐색·수비대·병영·포탑·함정·패시브·스킬을 그대로 실행합니다.\n이 창은 시드·배치·소환 입력과 실행 상태를 기록하고 시각화만 합니다.", MessageType.Info);
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Error);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Clamp(position.width * 0.29f, 360f, 460f)));
            var panel = GUILayout.Toolbar(inspectMode ? 1 : 0, new[] { "성 생성 · AI 소환", "상태 관찰" });
            inspectMode = panel == 1;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (inspectMode) DrawInspection();
            else DrawSettings(busy);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical();
            DrawPlayback();
            var rect = GUILayoutUtility.GetRect(300, 300, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawMap(rect, result?.scenario ?? scenario, Frame(result), result == null ? "AI 카드 선택 → 청록색 배치 칸 클릭" : HexSiegeSimulationRunner.Running ? "실시간 · AI 카드로 즉시 소환 / 병력 클릭으로 관찰" : "기록 재생 · 병력 클릭으로 관찰", HexSiegeSimulationRunner.Running || !busy && result == null);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettings(bool busy)
        {
            EditorGUILayout.LabelField("기술문서 대표 시나리오", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(busy))
            {
                representativeIndex = EditorGUILayout.Popup("배치", representativeIndex, HexSiegeSimulationData.RepresentativeLabels);
                if (GUILayout.Button("같은 조건으로 24마리 실행", GUILayout.Height(30))) Try(() =>
                {
                    var input = HexSiegeSimulationData.LoadRepresentative(representativeIndex);
                    HexSiegeSimulationRunner.Launch(input);
                    scenario = input; result = null; followLive = true; inspectMode = true; replay = false;
                    selectedUnit = 1; selectedDefender = 0; mapKey = null;
                });
            }
            EditorGUILayout.HelpBox("기술문서와 동일한 성·시드·24마리·소환 시점을 불러옵니다. 아래 자유 실험 설정은 대표 입력을 바꾸지 않습니다.", MessageType.None);
            showStress = EditorGUILayout.Foldout(showStress, "부하 검사 · 24 / 50 / 100명 동시 소환");
            if (showStress)
            {
                using (new EditorGUI.DisabledScope(busy))
                {
                    stressSize = EditorGUILayout.Popup("부하 규모", stressSize, new[] { "24명", "50명", "100명" });
                    recordStressVideo = EditorGUILayout.Toggle("실행 창 영상 저장", recordStressVideo);
                    if (recordStressVideo) EditorGUILayout.HelpBox("실제 실행 창을 MP4로 저장합니다. 시뮬레이션 시간 1배속·10fps이며 녹화 부하가 포함되어 성능 비교에서는 제외됩니다.", MessageType.Warning);
                    if (GUILayout.Button("선택 배치로 스트레스 테스트")) Try(() =>
                    {
                        var input = HexSiegeSimulationData.CreateStressScenario(representativeIndex, new[] { 24, 50, 100 }[stressSize]);
                        input.recordVideo = recordStressVideo;
                        HexSiegeSimulationRunner.Launch(input);
                        scenario = input; result = null; followLive = true; inspectMode = true; replay = false;
                        selectedUnit = 1; selectedDefender = 0; mapKey = null;
                    });
                    if (GUILayout.Button("4개 배치 연속 검사 · 각 기록 자동 저장")) Try(() =>
                    {
                        HexSiegeSimulationRunner.LaunchScenarioSuite(new[] { 24, 50, 100 }[stressSize], recordStressVideo);
                        result = null; followLive = true; inspectMode = true; replay = false;
                    });
                }
                EditorGUILayout.HelpBox("동일 성·능력치로 한 프레임에 소환합니다. 게임의 24명 제한과 별개인 Editor 부하 검사이며, 결과와 성능 수치는 자동 저장됩니다.", MessageType.Warning);
            }
            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUILayout.LabelField("1. 성 생성", EditorStyles.boldLabel);
                var themeIndex = Mathf.Max(0, Array.IndexOf(ThemeOptions, theme));
                theme = ThemeOptions[EditorGUILayout.Popup("테마", themeIndex, ThemeLabels)];
                wallLayers = EditorGUILayout.Popup("성벽", Mathf.Clamp(wallLayers - 2, 0, 2),
                    new[] { "2중벽", "3중벽", "4중벽" }) + 2;
                randomSeed = EditorGUILayout.Toggle("랜덤 시드", randomSeed);
                if (!randomSeed) seed = EditorGUILayout.IntField("시드", seed);
                if (GUILayout.Button("성 생성", GUILayout.Height(30))) Try(CreateScenario);
            }
            var input = HexSiegeSimulationRunner.Running ? HexSiegeSimulationRunner.Live.scenario : scenario;
            if (input == null) return;
            EditorGUILayout.LabelField("생성된 성", HexCastleThemeCatalog.ResolveKoreanName(input.theme), EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("성벽 / 시드", input.layers + "중벽 · " + input.seed);
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("2. AI 카드 선택", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("AI 카드를 고른 뒤 청록색 배치 칸을 클릭하세요. 첫 병력이 소환되는 즉시 전투와 자동 기록이 시작됩니다. 실행 중에는 같은 방식으로 병력을 계속 추가할 수 있습니다.", MessageType.None);
            RefreshCards();
            using (new EditorGUI.DisabledScope(!HexSiegeSimulationRunner.Running && (busy || result != null)))
            {
                for (var index = 0; index < deploymentCards.Length; index += 2)
                {
                    EditorGUILayout.BeginHorizontal();
                    DrawAICard(deploymentCards[index]);
                    if (index + 1 < deploymentCards.Length) DrawAICard(deploymentCards[index + 1]);
                    else GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
            }
            var selectedCard = deploymentCards.FirstOrDefault(card => card.pattern == selectedPattern);
            if (selectedCard != null && selectedCard.monsters.Length > 0)
                EditorGUILayout.HelpBox(AssaultPatternDescription(selectedPattern) + "\n무작위 소환 후보 · " +
                    string.Join(", ", selectedCard.monsters.Take(5).Select(entry => MonsterLabel(entry.MonsterId))) +
                    (selectedCard.monsters.Length > 5 ? " 외 " + (selectedCard.monsters.Length - 5) + "종" : ""), MessageType.Info);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("소환 기록", input.spawns.Count + " / " + HexSiegeSimulationData.MaximumAssaultSpawns + "명");
            if (input.spawns.Count > 0)
            {
                var last = input.spawns[input.spawns.Count - 1];
                EditorGUILayout.LabelField("최근 소환", MonsterLabel(last.monster) + " · " + AssaultPatternName(last.aiPattern), EditorStyles.wordWrappedLabel);
            }
            if (HexSiegeSimulationRunner.Running)
                EditorGUILayout.HelpBox("실행 중 · 소환과 전투 상태를 자동 기록하고 있습니다.", MessageType.Info);
            else if (result != null)
                EditorGUILayout.HelpBox("기록 재생 중입니다. 같은 입력 재실행 또는 성 생성으로 새 실험을 시작하세요.", MessageType.None);
        }

        private void DrawAICard(HexSiegeSimulationAICard card)
        {
            var chosen = selectedPattern == card.pattern;
            var oldColor = GUI.backgroundColor;
            GUI.backgroundColor = chosen ? new Color(0.35f, 0.78f, 0.86f) : Color.white;
            using (new EditorGUI.DisabledScope(card.monsters.Length == 0))
                if (GUILayout.Toggle(chosen, (chosen ? "● " : "") + AssaultPatternName(card.pattern) + "\n" + card.monsters.Length + "종",
                        "Button", GUILayout.Height(46), GUILayout.ExpandWidth(true)) && !chosen)
                    selectedPattern = card.pattern;
            GUI.backgroundColor = oldColor;
        }

        private void RefreshCards()
        {
            var input = HexSiegeSimulationRunner.Running ? HexSiegeSimulationRunner.Live.scenario : scenario;
            if (input == null || input.profileJson == cardsProfileJson && deploymentCards.Length > 0) return;
            deploymentCards = HexSiegeSimulationData.GetAICards(input);
            cardsProfileJson = input.profileJson;
            if (!deploymentCards.Any(c => c.pattern == selectedPattern && c.monsters.Length > 0))
                selectedPattern = deploymentCards.FirstOrDefault(c => c.monsters.Length > 0)?.pattern ?? 0;
        }

        private void SummonAt(HexCoordinates coordinates)
        {
            if (HexSiegeSimulationRunner.Running)
            {
                HexSiegeSimulationRunner.QueueAISpawn(selectedPattern, coordinates);
                followLive = true; replay = false;
            }
            else
            {
                var previousCount = scenario.spawns.Count;
                HexSiegeSimulationData.AddAISpawn(scenario, selectedPattern, coordinates, 0);
                replay = false; followLive = true; inspectMode = false;
                try { HexSiegeSimulationRunner.Launch(scenario); }
                catch
                {
                    if (!EditorApplication.isPlayingOrWillChangePlaymode && scenario.spawns.Count > previousCount)
                        scenario.spawns.RemoveRange(previousCount, scenario.spawns.Count - previousCount);
                    throw;
                }
            }
        }

        private void DrawPlayback()
        {
            EditorGUILayout.BeginHorizontal();
            showPaths = GUILayout.Toggle(showPaths, "선택 경로"); showAllPaths = GUILayout.Toggle(showAllPaths, "전체 경로");
            showGroups = GUILayout.Toggle(showGroups, "부대 색"); showSlots = GUILayout.Toggle(showSlots, "선택 자리");
            showStates = GUILayout.Toggle(showStates, "전체 이름·상태"); showDead = GUILayout.Toggle(showDead, "사망자");
            if (GUILayout.Button("맵 맞춤")) { mapZoom = 1; mapPan = Vector2.zero; }
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("실행 기록 열기")) Try(() =>
            {
                var path = EditorUtility.OpenFilePanel("실행 기록", HexSiegeSimulationData.OutputDirectory, "json");
                if (!string.IsNullOrEmpty(path)) LoadResult(path);
            });
            if (GUILayout.Button("기록 폴더"))
            {
                Directory.CreateDirectory(HexSiegeSimulationData.OutputDirectory);
                EditorUtility.RevealInFinder(HexSiegeSimulationData.OutputDirectory);
            }
            using (new EditorGUI.DisabledScope(HexSiegeSimulationRunner.Running))
                if (GUILayout.Button("새 실험")) Try(ResetToSetup);
            EditorGUILayout.EndHorizontal();
            if (result == null || result.frames.Count == 0) return;
            EditorGUILayout.LabelField((HexSiegeSimulationRunner.Running ? "● REC · " : "기록 · ") +
                HexSiegeSimulationData.ScenarioDisplayName(result.scenario) + (result.scenario.recordVideo ? " · 영상 녹화 포함 (성능 기준에서 제외)" : ""));
            if (!HexSiegeSimulationRunner.Running && !RecordingMatchesCurrentCode())
                EditorGUILayout.HelpBox("이 기록은 현재 Runtime 또는 시뮬레이터 코드와 다릅니다. 재생은 가능하지만 현재 알고리즘 검증에는 ‘같은 입력 재실행’을 사용하세요.", MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(replay ? "재생 멈춤" : "기록 재생", GUILayout.Width(84)))
            { replay = !replay; followLive = false; replayAt = EditorApplication.timeSinceStartup; if (frameIndex >= result.frames.Count - 1) frameIndex = 0; }
            if (GUILayout.Button("◀", GUILayout.Width(26))) { frameIndex = Math.Max(0, frameIndex - 1); followLive = false; }
            if (GUILayout.Button("▶", GUILayout.Width(26))) { frameIndex = Math.Min(result.frames.Count - 1, frameIndex + 1); followLive = false; }
            EditorGUI.BeginChangeCheck();
            frameIndex = EditorGUILayout.IntSlider(frameIndex, 0, result.frames.Count - 1);
            DrawTimelineMarkers(GUILayoutUtility.GetLastRect());
            if (EditorGUI.EndChangeCheck()) followLive = false;
            replaySpeed = EditorGUILayout.FloatField(replaySpeed, GUILayout.Width(45)); replaySpeed = Mathf.Clamp(replaySpeed, 0.1f, 8);
            if (HexSiegeSimulationRunner.Running) followLive = GUILayout.Toggle(followLive, "실시간");
            EditorGUILayout.EndHorizontal();
            var current = Frame(result);
            EditorGUILayout.LabelField((frameIndex * result.scenario.step).ToString("F2") + "초  /  " + ((result.frames.Count - 1) * result.scenario.step).ToString("F2") +
                "초     공격 " + current.units.Count(u => u.hp > 0) + "명 생존  ·  수비 " + current.defenders.Count(u => u.hp > 0) + "명 생존" +
                "  ·  함정 " + current.traps.Count(t => t.armed) + "/" + current.traps.Count + " 작동 가능" +
                (frameIndex == result.frames.Count - 1 ? "     " + (result.outcome ?? "실행 중") : "     기록 관찰 중"));
            var errors = result.diagnostics?.Count(value => value.severity == "오류") ?? 0;
            var warnings = result.diagnostics?.Count(value => value.severity == "주의") ?? 0;
            EditorGUILayout.LabelField("자동 점검 · 오류 " + errors + " · 주의 " + warnings +
                "  (판정 신호이며 최종 판단은 해당 시점의 경로·목표와 함께 확인)", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(result.error)) EditorGUILayout.HelpBox(result.error, MessageType.Error);
            if (result.performance != null && frameIndex < result.performance.Count)
            {
                var p = result.performance[frameIndex];
                EditorGUILayout.LabelField($"프레임 간격 {p.wallFrameMs:F1}ms  ·  AI 판단 {p.aiDecisionMs:F1}ms  ·  기록 {p.recordingMs:F1}ms  ·  진단 {p.diagnosticsMs:F1}ms", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Editor 실측 (고정 시뮬레이션 시간과 다름) · 직전 프레임 할당 " +
                    (p.profilerFrameAllocatedBytes < 0 ? "측정 불가" : (p.profilerFrameAllocatedBytes / 1024d).ToString("F0") + "KB") +
                    " · GC " + p.gcCollections + "회 · 전체 할당에는 Editor·기록 비용 포함", EditorStyles.miniLabel);
            }
        }

        private HexSiegeSimulationFrame Frame(HexSiegeSimulationResult value) => value == null || value.frames.Count == 0
            ? null : value.frames[Mathf.Clamp(frameIndex, 0, value.frames.Count - 1)];

        private void DrawInspection()
        {
            var frame = Frame(result);
            if (frame == null) { EditorGUILayout.HelpBox("실험을 실행하면 양측 상태·경로·목표를 여기서 확인할 수 있습니다.", MessageType.Info); return; }
            var unit = frame.units.FirstOrDefault(v => v.id == selectedUnit);
            var defender = frame.defenders.FirstOrDefault(v => v.id == selectedDefender);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("선택한 대상", EditorStyles.boldLabel);
            if (unit != null)
            {
                EditorGUILayout.LabelField("공격 " + unit.id + " · " + unit.pattern, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("현재 행동", DisplayState(unit.state, unit.hp));
                DrawHealth(unit.hp, result.scenario.spawns[unit.id - 1].health);
                EditorGUILayout.LabelField("부대 / 공유 경로", (unit.cohort > 0 ? unit.cohort + "부대" : "미배정") +
                    (unit.route > 0 ? " / 경로 R" + unit.route : " / 개별 경로"));
                EditorGUILayout.LabelField("팀장 / 전면 경로 버전", unit.leader > 0 ? "공격 " + unit.leader +
                    (unit.leader == unit.id ? " (본인)" : "") + " / " + unit.sharedRouteVersion : "기록 없음");
                EditorGUILayout.LabelField("전략 목표", unit.hp <= 0 ? "없음 (사망)" : StrategicTargetLabel(unit), EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("현재 행동 목표", unit.hp <= 0 ? "없음 (사망)" : TargetLabel(unit), EditorStyles.wordWrappedLabel);
                if (unit.hp > 0 && unit.isTemporaryWork)
                    EditorGUILayout.HelpBox("주 목표의 공격 자리를 기다리는 동안 수행하는 임시 지역 공격입니다. 부대와 전략 목표는 유지됩니다.", MessageType.Info);
                EditorGUILayout.LabelField("다음 이동 칸", NextPathCell(unit), EditorStyles.wordWrappedLabel);
                if (unit.hp > 0)
                {
                    EditorGUILayout.LabelField("다음 경로 판단", unit.decisionRequested ? "즉시 재판단 요청됨" : unit.nextDecisionIn.ToString("F2") + "초 후" +
                        (unit.retryIn > 0.001f ? " · 실패 재시도 " + unit.retryIn.ToString("F2") + "초 후" : ""));
                    EditorGUILayout.LabelField("최근 경로 갱신", (unit.pathUpdatedTick * result.scenario.step).ToString("F2") + "초 · " +
                        DecisionReasonLabel(unit.pathUpdateReason));
                }
                if (unit.hp > 0 && unit.hasSlot) EditorGUILayout.LabelField("공격 자리", DisplayState(unit.slotState, 1));
                if (unit.advanceMoveMs + unit.advanceDestroyMs + unit.advanceContinuationMs > 0)
                {
                    EditorGUILayout.LabelField("왕궁 접근 예상", (unit.advanceMoveMs + unit.advanceDestroyMs + unit.advanceContinuationMs) + "ms");
                    EditorGUILayout.LabelField("계산 내역", "이동·정렬 " + unit.advanceMoveMs + " + 돌파 " + unit.advanceDestroyMs + " + 이후 " + unit.advanceContinuationMs + "ms", EditorStyles.wordWrappedLabel);
                    if (unit.hasAdvanceAlternative) EditorGUILayout.LabelField("비교 후보", "(" + unit.advanceAlternativeQ + ", " + unit.advanceAlternativeR + ") · " + unit.advanceAlternativeMs + "ms");
                }
                else if (unit.hasBreachCost)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("최근 돌파 비용", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("선택 목표", BreachTargetLabel(unit.breachTargetQ, unit.breachTargetR));
                    EditorGUILayout.LabelField("계산 근거", "이동 " + Mathf.RoundToInt(unit.breachMoveSeconds * 1000f) + "ms + 파괴 " +
                        Mathf.RoundToInt(unit.breachDestroySeconds * 1000f) + "ms = 총 " +
                        Mathf.RoundToInt(unit.breachTotalSeconds * 1000f) + "ms · 기대 DPS " +
                        unit.breachExpectedDps.ToString("F1"), EditorStyles.wordWrappedLabel);
                    if (unit.hasAlternativeBreachCost)
                        EditorGUILayout.LabelField("차선 후보", BreachTargetLabel(unit.alternativeTargetQ, unit.alternativeTargetR) + " · 이동 " +
                            Mathf.RoundToInt(unit.alternativeMoveSeconds * 1000f) + "ms + 파괴 " +
                            Mathf.RoundToInt(unit.alternativeDestroySeconds * 1000f) + "ms = 총 " +
                            Mathf.RoundToInt(unit.alternativeTotalSeconds * 1000f) + "ms", EditorStyles.wordWrappedLabel);
                }
            }
            else if (defender != null)
            {
                EditorGUILayout.LabelField("수비 " + defender.id + " · " + DisplayState(defender.role, 1), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("현재 행동", DisplayState(defender.state, defender.hp));
                DrawHealth(defender.hp, defender.maxHp);
                EditorGUILayout.LabelField("공격력 / 이동속도", defender.damage.ToString("F1") + " / " + defender.speed.ToString("F2"));
                EditorGUILayout.LabelField("귀환 거점", defender.homeQ + ", " + defender.homeR);
                EditorGUILayout.LabelField("다음 목표", defender.hp > 0 && defender.targetUnit > 0 ? "공격 병력 " + defender.targetUnit : "없음");
                if (defender.role == "Knight") EditorGUILayout.LabelField("한 칸 장애물 점프", defender.jumping ? "점프 중 · 누적 " + defender.jumpCount + "회" : "가능 · 누적 " + defender.jumpCount + "회");
            }
            else EditorGUILayout.LabelField("맵 또는 아래 목록에서 병력을 선택하세요.", EditorStyles.wordWrappedLabel);
            using (new EditorGUI.DisabledScope(unit == null && defender == null))
                if (GUILayout.Button("선택 병력 가까이 보기", GUILayout.Height(27))) focusSelection = true;
            EditorGUILayout.EndVertical();
            DrawAutomaticReview(frame);
            if (selectedCell.HasValue)
            {
                var cell = result.scenario.cells.Select(c => c.Build()).FirstOrDefault(c => c.Coordinates == selectedCell.Value);
                if (cell != null)
                {
                    var current = frame.damagedCells.FirstOrDefault(c => c.q == cell.Coordinates.Q && c.r == cell.Coordinates.R);
                    EditorGUILayout.LabelField("선택 구조물", HexSiegeSimulationSymbols.CellName(cell));
                    EditorGUILayout.LabelField("위치", cell.Coordinates.ToString());
                    EditorGUILayout.LabelField("남은 HP / 최대 HP", (current?.hp ?? cell.MaxHealth).ToString("F1") + " / " + cell.MaxHealth.ToString("F1"));
                    var barracks = frame.barracks.FirstOrDefault(b => b.q == cell.Coordinates.Q && b.r == cell.Coordinates.R);
                    if (barracks != null)
                    {
                        EditorGUILayout.LabelField("병영 상태", !barracks.alive ? "파괴됨 · 생산 중단" : barracks.producing ? "생산 중 · " + barracks.remaining.ToString("F1") + "초 남음" : "인원 제한 · 생산 대기");
                        EditorGUILayout.LabelField("추가 생산 인원", barracks.spawned.ToString());
                    }
                }
            }
            EditorGUILayout.Space(8);
            rosterTab = GUILayout.Toolbar(rosterTab, new[] { "공격 병력 (" + frame.units.Count + ")", "수비대 (" + frame.defenders.Count + ")" });
            rosterScroll = EditorGUILayout.BeginScrollView(rosterScroll, GUILayout.MaxHeight(240));
            if (rosterTab == 0) foreach (var a in frame.units.OrderBy(a => a.hp <= 0).ThenBy(a => a.id))
            {
                var selected = a.id == selectedUnit;
                if (GUILayout.Toggle(selected, "공격 " + a.id + "   " + DisplayState(a.state, a.hp) + "   ·   HP " + a.hp.ToString("F0"), "Button", GUILayout.Height(26)) && !selected)
                { selectedUnit = a.id; selectedDefender = 0; selectedCell = null; }
            }
            else foreach (var d in frame.defenders.OrderBy(d => d.hp <= 0).ThenBy(d => d.id))
            {
                var selected = d.id == selectedDefender;
                if (GUILayout.Toggle(selected, "수비 " + d.id + " " + DisplayState(d.role, 1) + "   " + DisplayState(d.state, d.hp), "Button", GUILayout.Height(26)) && !selected)
                { selectedDefender = d.id; selectedUnit = 0; selectedCell = null; }
            }
            EditorGUILayout.EndScrollView();
            showSquads = EditorGUILayout.Foldout(showSquads, "부대 편성 확인", true);
            if (showSquads) foreach (var g in frame.units.Where(u => u.hp > 0).GroupBy(u => u.cohort).OrderBy(g => g.Key))
            {
                var routes = string.Join(", ", g.Select(u => u.route > 0 ? "R" + u.route : "개별").Distinct());
                var targets = string.Join(" / ", g.Select(TargetLabel).Distinct());
                EditorGUILayout.LabelField(g.Key == 0 ? "미배정" : g.Key + "부대",
                    g.Count() + "/6명 · 팀장 " + g.First().leader + " · 경로 " + routes + " · 목표 " + targets + "\n" +
                    string.Join(", ", g.Select(u => "공격 " + u.id)), EditorStyles.wordWrappedLabel);
            }
            showDiagnostics = EditorGUILayout.Foldout(showDiagnostics, "개발용 상세 정보", true);
            if (showDiagnostics)
            {
                EditorGUILayout.LabelField("누적 타격 / 계획", result.attacks + " / " + result.decisions);
                EditorGUILayout.LabelField("필드 생성 / 재사용", result.fieldBuilds + " / " + result.fieldReuses);
                EditorGUILayout.LabelField("대표 탐색 / 공유 경로 사용", frame.sharedSearchBuilds + " / " + frame.sharedPathUses);
                EditorGUILayout.LabelField("전체 탐색 / 개인 합류 탐색", frame.fullSearches + " / " + frame.localConnectionSearches);
                EditorGUILayout.LabelField("전체 / 합류 방문 칸", frame.fullVisitedCells + " / " + frame.localVisitedCells);
                EditorGUILayout.LabelField("기록 누락", result.traceLost.ToString());
                if (unit != null)
                {
                    EditorGUILayout.LabelField("원본 상태 / 의도", unit.state + " / " + unit.intent);
                    EditorGUILayout.LabelField("전략 / 행동 목표", StrategicTargetLabel(unit) + " / " + TargetLabel(unit), EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField("판단 사유", unit.reason);
                    EditorGUILayout.LabelField("경로 ID / 계획 세대", unit.route + " / " + unit.generation);
                    EditorGUILayout.LabelField("Cell / 경로 진행", unit.q + "," + unit.r + " / " + unit.pathIndex);
                }
                if (defender != null) EditorGUILayout.LabelField("귀환 Cell / 경로", defender.homeQ + "," + defender.homeR + " / " + defender.pathCells, EditorStyles.wordWrappedLabel);
            }
            showHistory = EditorGUILayout.Foldout(showHistory, "선택 공격 병력의 기록", true);
            if (showHistory && unit != null)
                foreach (var e in result.frames.Take(frameIndex + 1).SelectMany(f => f.events).Where(e => e.unit == selectedUnit).Reverse().Take(16))
                    EditorGUILayout.LabelField((e.tick * result.scenario.step).ToString("F2") + "초 · " +
                        EventKindLabel(e.kind) + " · " + DecisionReasonLabel(e.reason), EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawAutomaticReview(HexSiegeSimulationFrame frame)
        {
            showIssues = EditorGUILayout.Foldout(showIssues, "자동 이상 징후", true);
            if (!showIssues || result == null) return;
            var diagnostics = result.diagnostics ?? new List<HexSiegeSimulationDiagnostic>();
            if (diagnostics.Count == 0)
            {
                EditorGUILayout.HelpBox("기록된 상태에서 장기 대기·이동 정체·목표 반복 전환·임시 공격 계약 위반 신호가 발견되지 않았습니다.", MessageType.Info);
                return;
            }
            foreach (var issue in diagnostics.Take(12))
            {
                var active = frame != null && issue.startTick <= frame.tick && frame.tick <= issue.endTick;
                var prefix = issue.severity == "오류" ? "오류" : issue.severity == "주의" ? "주의" : "정보";
                var unit = issue.unit > 0 ? " · 공격 " + issue.unit : "";
                if (GUILayout.Button((active ? "▶ " : "") + "[" + prefix + "] " +
                        (issue.startTick * result.scenario.step).ToString("F2") + "초" + unit + " · " + issue.title,
                        EditorStyles.miniButton))
                {
                    JumpToTick(issue.startTick);
                    if (issue.unit > 0) { selectedUnit = issue.unit; selectedDefender = 0; rosterTab = 0; }
                }
                EditorGUILayout.LabelField(issue.detail, EditorStyles.wordWrappedMiniLabel);
            }
            if (diagnostics.Count > 12)
                EditorGUILayout.LabelField("그 밖의 점검 신호 " + (diagnostics.Count - 12) + "건", EditorStyles.miniLabel);
        }

        private bool RecordingMatchesCurrentCode()
        {
            if (result == null || string.IsNullOrEmpty(currentRuntimeHash) || string.IsNullOrEmpty(currentDriverHash)) return false;
            return result.unity == Application.unityVersion && result.runtimeHash == currentRuntimeHash && result.driverHash == currentDriverHash;
        }

        private void DrawTimelineMarkers(Rect sliderRect)
        {
            if (result?.diagnostics == null || result.frames.Count < 2 || sliderRect.width <= 0) return;
            var finalTick = Mathf.Max(1, result.frames[result.frames.Count - 1].tick);
            foreach (var issue in result.diagnostics.Where(value => value.severity != "정보"))
            {
                var x = Mathf.Lerp(sliderRect.x + 4, sliderRect.xMax - 4, Mathf.Clamp01((float)issue.startTick / finalTick));
                EditorGUI.DrawRect(new Rect(x - 1, sliderRect.yMax - 5, 2, 5),
                    issue.severity == "오류" ? new Color(1f, 0.25f, 0.22f) : new Color(1f, 0.72f, 0.18f));
            }
        }

        private void JumpToTick(int tick)
        {
            if (result == null || result.frames.Count == 0) return;
            var found = result.frames.FindIndex(frame => frame.tick >= tick);
            frameIndex = found >= 0 ? found : result.frames.Count - 1;
            followLive = false; replay = false;
        }

        private static string EventKindLabel(string kind)
        {
            switch (kind)
            {
                case "CohortCreated": return "부대 생성";
                case "CohortJoined": return "부대 합류";
                case "CohortReassigned": return "부대 재배정";
                case "PlanCommitted": return "목표·경로 확정";
                case "SlotReserved": return "공격 자리 예약";
                case "SlotArrived": return "공격 자리 도착";
                case "DecisionDeferred": return "판단 대기";
                case "NoProgressEscalated": return "이동 정체 재판단";
                default: return string.IsNullOrEmpty(kind) ? "기록" : kind;
            }
        }

        public static string DisplayState(string state, float hp)
        {
            if (hp <= 0) return "사망";
            switch (state)
            {
                case "Attacking": case "Attack": return "공격 중";
                case "Aligning": return "공격 자리로 이동";
                case "Moving": case "FollowingRoute": case "Advancing": case "Traversing": return "이동 중";
                case "Inactive": return "비활성";
                case "AwaitInitialPlan": return "첫 경로 계산 중";
                case "Chase": return "적 추적 중";
                case "Patrol": return "순찰 중";
                case "Return": return "복귀 중";
                case "Idle": case "None": return "대기";
                case "Waiting": case "Holding": case "HoldPosition": return "자리 대기";
                case "Recovering": return "경로 복구 중";
                case "Departing": return "자리 이탈 중";
                case "Reserved": return "자리 예약됨";
                case "Settled": case "Occupied": return "공격 위치 도착";
                case "Jump": return "장애물 넘는 중";
                case "Dead": return "사망";
                case "Knight": return "기사";
                case "Farmer": return "농부";
                default: return state ?? "대기";
            }
        }

        private static string DecisionReasonLabel(string reason)
        {
            switch (reason)
            {
                case "None": return "정상 갱신";
                case "InvalidInput": return "입력 무효";
                case "NoStrategicRoute": return "전략 경로 없음";
                case "NoReachableApproach": return "접근 지점 없음";
                case "SlotFull": return "공격 자리 가득 참";
                case "SlotApproachBlocked": return "공격 자리 접근 차단";
                case "AttackLaneBlocked": return "공격선 차단";
                case "BreachBudgetFull": return "돌파 인원 제한";
                case "TargetInvalid": return "목표 소멸·무효";
                case "PlanVersionChanged": return "주변 경로 상태 변경";
                case "BodyTooLarge": return "통로 폭 부족";
                case "WaitingForOpening": return "자리 열림 대기";
                case "NoProgress": return "이동 진전 없음";
                case "LocalFallback": return "임시 지역 공격";
                default: return string.IsNullOrEmpty(reason) ? "기록 없음" : reason;
            }
        }

        private static string NextPathCell(HexSiegeSimulationUnitState unit)
        {
            if (unit.hp <= 0 || string.IsNullOrEmpty(unit.pathCells)) return "없음";
            var points = unit.pathCells.Split(';');
            return unit.pathIndex >= 0 && unit.pathIndex < points.Length ? points[unit.pathIndex] : "현재 경로 끝";
        }

        private string TargetLabel(HexSiegeSimulationUnitState unit)
        {
            if (!unit.hasTarget) return "탐색 중";
            if (unit.targetKind == "Defender")
            {
                var defender = Frame(result)?.defenders.FirstOrDefault(d => d.id == unit.targetUnit);
                return "수비 " + unit.targetUnit + " · " + DisplayState(defender?.role, 1);
            }
            if (unit.targetKind == "Ally") return "지원할 병력 " + unit.targetUnit;
            var cell = result?.scenario.cells.Select(c => c.Build()).FirstOrDefault(c => c.Coordinates.Q == unit.targetQ && c.Coordinates.R == unit.targetR);
            return (cell != null ? HexSiegeSimulationSymbols.CellName(cell) : "구조물") + " (" + unit.targetQ + ", " + unit.targetR + ")";
        }

        private string StrategicTargetLabel(HexSiegeSimulationUnitState unit)
        {
            if (!unit.hasStrategicTarget) return "없음";
            var cell = result?.scenario.cells.Select(value => value.Build())
                .FirstOrDefault(value => value.Coordinates.Q == unit.strategicTargetQ &&
                                         value.Coordinates.R == unit.strategicTargetR);
            return (cell != null ? HexSiegeSimulationSymbols.CellName(cell) :
                    unit.strategicTargetKind == "Palace" ? "왕궁" : "구조물") +
                   " (" + unit.strategicTargetQ + ", " + unit.strategicTargetR + ")";
        }

        private string BreachTargetLabel(int q, int r)
        {
            var cell = result?.scenario.cells.Select(value => value.Build())
                .FirstOrDefault(value => value.Coordinates.Q == q && value.Coordinates.R == r);
            return (cell != null ? HexSiegeSimulationSymbols.CellName(cell) : "구조물") + " (" + q + ", " + r + ")";
        }

        private static void DrawHealth(float current, float maximum)
        {
            var rect = GUILayoutUtility.GetRect(1, 22, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(rect, Mathf.Clamp01(current / Mathf.Max(1, maximum)), "HP " + current.ToString("F0") + " / " + maximum.ToString("F0"));
        }

        public static string AssaultPatternName(int pattern)
        {
            var names = new[] { "기본 AI", "일반 진격", "자원 약탈", "포탑 사냥", "수비대 사냥", "방벽 돌파", "위협 제압", "전술 지원" };
            return names[Mathf.Clamp(pattern + 1, 0, names.Length - 1)];
        }

        private static string AssaultPatternDescription(int pattern)
        {
            var descriptions = new[]
            {
                "위협을 대응하면서 왕궁으로 전진합니다.",
                "자원 건물을 우선하고 위협 처리 뒤 원래 목표로 복귀합니다.",
                "포탑을 우선 제거하고 필요한 성벽을 먼저 돌파합니다.",
                "도달 가능한 수비대를 우선 추적합니다.",
                "다음 방어층으로 이어지는 성벽 돌파를 우선합니다.",
                "공격 중인 수비대와 포탑을 먼저 제압합니다.",
                "가까운 아군을 지원하면서 공동 진격합니다."
            };
            return descriptions[Mathf.Clamp(pattern, 0, descriptions.Length - 1)];
        }

        private string MonsterLabel(string monsterId)
        {
            if (string.IsNullOrEmpty(monsterId)) return "알 수 없는 몬스터";
            if (monsterNames.TryGetValue(monsterId, out var cached)) return cached;
            var catalog = AssetDatabase.LoadAssetAtPath<MonsterCatalog>("Assets/ProjectMT/02_Shared/Unit/Data/MonsterCatalog.asset");
            var label = catalog != null && catalog.TryGet(monsterId, out var definition) && !string.IsNullOrWhiteSpace(definition.DisplayName)
                ? definition.DisplayName : monsterId;
            monsterNames[monsterId] = label;
            return label;
        }

        private void DrawMap(Rect rect, HexSiegeSimulationScenario input, HexSiegeSimulationFrame frame, string title, bool editable)
        {
            GUI.BeginGroup(rect);
            rect = new Rect(0, 0, rect.width, rect.height);
            if (rect.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.ScrollWheel)
                { mapZoom = Mathf.Clamp(mapZoom * Mathf.Pow(1.1f, -Event.current.delta.y), 0.7f, 5); Event.current.Use(); Repaint(); }
                else if (Event.current.type == EventType.MouseDrag && Event.current.button == 2)
                { mapPan += Event.current.delta; Event.current.Use(); Repaint(); }
            }
            EditorGUI.DrawRect(rect, new Color(0.055f, 0.075f, 0.11f));
            GUI.Label(new Rect(rect.x + 10, rect.y + 6, rect.width - 20, 24), title +
                (input == null ? "" : " · " + HexCastleThemeCatalog.ResolveKoreanName(input.theme)), EditorStyles.whiteBoldLabel);
            if (input == null || input.cells.Count == 0) { GUI.EndGroup(); return; }
            mapLabels.Clear();
            var key = input.layoutSignature + ":" + HexSiegeSimulationData.Hash(JsonUtility.ToJson(input.cells[0]));
            if (map == null || mapKey != key) { map = input.cells.Select(c => c.Build()).ToDictionary(c => c.Coordinates); mapKey = key; }
            var positions = map.Keys.Select(c => c.ToWorld(HexSpatialContract.CellOuterRadius)).ToArray();
            var minX = positions.Min(p => p.x) - HexSpatialContract.CellOuterRadius;
            var maxX = positions.Max(p => p.x) + HexSpatialContract.CellOuterRadius;
            var minZ = positions.Min(p => p.z) - HexSpatialContract.CellOuterRadius;
            var maxZ = positions.Max(p => p.z) + HexSpatialContract.CellOuterRadius;
            var baseScale = Mathf.Min((rect.width - 40) / (maxX - minX), (rect.height - 150) / (maxZ - minZ));
            if (focusSelection && frame != null)
            {
                var u = frame.units.FirstOrDefault(v => v.id == selectedUnit);
                var d = frame.defenders.FirstOrDefault(v => v.id == selectedDefender);
                if (u != null || d != null)
                {
                    mapZoom = 2;
                    mapPan = -new Vector2((u?.x ?? d.x) - (minX + maxX) / 2, -((u?.z ?? d.z) - (minZ + maxZ) / 2)) * baseScale * mapZoom;
                }
                focusSelection = false;
            }
            var scale = baseScale * mapZoom;
            var center = rect.center + new Vector2(-(minX + maxX) / 2, (minZ + maxZ) / 2) * scale + mapPan;
            Func<Vector3, Vector2> screen = p => center + new Vector2(p.x, -p.z) * scale;
            Func<HexCoordinates, Vector2> coord = c => screen(c.ToWorld(HexSpatialContract.CellOuterRadius));
            var damaged = frame?.damagedCells.ToDictionary(c => new HexCoordinates(c.q, c.r));
            string hoveredCell = null;
            foreach (var c in map.Values)
            {
                var p = coord(c.Coordinates);
                var blocked = c.InitialBlocked; var hp = c.MaxHealth;
                if (damaged != null && damaged.TryGetValue(c.Coordinates, out var d)) { blocked = d.blocked; hp = d.hp; }
                var color = blocked ? HexSiegeSimulationSymbols.StructureColor(c) : new Color(0.13f, 0.18f, 0.24f);
                if (c.Kind == HexCastleCellKind.Deployment) color = HexSiegeSimulationSymbols.StructureColor(c);
                if (selectedCell.HasValue && selectedCell.Value == c.Coordinates) color = new Color(0.8f, 0.65f, 0.15f);
                else if (blocked && c.GateRole != HexCastleGateRole.None)
                    color = c.GateRole == HexCastleGateRole.OpenDefenderPassage ? new Color(0.2f, 0.55f, 0.8f) : new Color(0.8f, 0.35f, 0.15f);
                else if (c.Kind == HexCastleCellKind.Palace) color = new Color(0.72f, 0.56f, 0.15f);
                Handles.color = color;
                var radius = HexSpatialContract.CellOuterRadius * scale * 0.94f;
                var vertices = Enumerable.Range(0, 6).Select(i => (Vector3)(p + new Vector2(Mathf.Cos((30 + i * 60) * Mathf.Deg2Rad), Mathf.Sin((30 + i * 60) * Mathf.Deg2Rad)) * radius)).ToArray();
                Handles.DrawAAConvexPolygon(vertices);
                if (blocked) HexSiegeSimulationSymbols.DrawStructure(c, p, radius);
                else if (c.InitialBlocked)
                {
                    Handles.color = new Color(0.48f, 0.46f, 0.43f);
                    Handles.DrawAAPolyLine(2, p + new Vector2(-5, -3), p + new Vector2(0, 3), p + new Vector2(5, -2));
                }
                if (Vector2.Distance(Event.current.mousePosition, p) < radius * 0.8f)
                    hoveredCell = HexSiegeSimulationSymbols.CellName(c) + "  ·  " + c.Coordinates +
                        (c.MaxHealth > 0 ? "  ·  HP " + hp.ToString("F0") + " / " + c.MaxHealth.ToString("F0") : "") +
                        (c.InitialBlocked && !blocked ? "  ·  파괴됨" : "");
                if (blocked && c.MaxHealth > 0 && (hp < c.MaxHealth || selectedCell == c.Coordinates))
                    EditorGUI.DrawRect(new Rect(p.x - radius * 0.7f, p.y + radius * 0.5f, radius * 1.4f * hp / c.MaxHealth, 2), new Color(0.45f, 0.9f, 0.45f));
                var unitUnderPointer = frame != null && (frame.units.Any(u => (u.hp > 0 || showDead || u.id == selectedUnit) && Vector2.Distance(Event.current.mousePosition, screen(new Vector3(u.x, 0, u.z))) < 10) ||
                    frame.defenders.Any(u => (u.hp > 0 || showDead || u.id == selectedDefender) && Vector2.Distance(Event.current.mousePosition, screen(new Vector3(u.x, 0, u.z))) < 10));
                if (!unitUnderPointer && Event.current.type == EventType.MouseDown && Event.current.button == 0 && Vector2.Distance(Event.current.mousePosition, p) < radius * 0.75f)
                {
                    selectedCell = c.Coordinates;
                    if (editable && !inspectMode && c.Kind == HexCastleCellKind.Deployment && !blocked)
                        Try(() => SummonAt(c.Coordinates));
                    Event.current.Use(); Repaint();
                }
            }
            if (frame == null)
            {
                foreach (var s in input.spawns.GroupBy(s => new { cell = new HexCoordinates(s.q, s.r), s.aiPattern }))
                {
                    var p = coord(s.Key.cell); Handles.color = Color.cyan; Handles.DrawSolidDisc(p, Vector3.forward, 6);
                    DrawMapTag(rect, p, s.Count() + "명 · " + AssaultPatternName(s.Key.aiPattern), Color.cyan);
                }
                foreach (var d in input.defenders)
                {
                    var p = coord(new HexCoordinates(d.q, d.r));
                    HexSiegeSimulationSymbols.DrawDefender(p, d.role.ToString(), true, false);
                    DrawMapTag(rect, p, DisplayState(d.role.ToString(), 1), Color.red);
                }
            }
            if (frame != null) foreach (var trap in frame.traps)
            {
                var p = coord(new HexCoordinates(trap.q, trap.r));
                var color = trap.warning > 0 ? new Color(1f, 0.25f, 0.08f) : trap.armed ? new Color(1f, 0.78f, 0.14f) : new Color(0.48f, 0.48f, 0.48f);
                var label = TrapLabel(trap.type);
                Handles.color = color;
                Handles.DrawSolidDisc(p, Vector3.forward, 5);
                GUI.Label(new Rect(p.x - 9, p.y - 9, 18, 18), label.Substring(0, 1), new GUIStyle(EditorStyles.whiteMiniLabel) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold });
                if (Vector2.Distance(Event.current.mousePosition, p) < 9)
                    hoveredCell = label + " · " + trap.charges + "/" + trap.maximumCharges + "회" +
                        (trap.warning > 0 ? " · 폭발 예고 " + trap.warning.ToString("F1") + "초" : trap.cooldown > 0 ? " · 재사용 " + trap.cooldown.ToString("F1") + "초" : trap.armed ? " · 작동 가능" : " · 소진");
            }
            if (frame != null) foreach (var u in frame.units)
            {
                var p = screen(new Vector3(u.x, 0, u.z));
                var chosen = u.id == selectedUnit;
                if (u.hp <= 0 && !showDead && !chosen) continue;
                var color = showGroups && u.cohort > 0 ? Color.HSVToRGB((u.cohort * 0.173f) % 1f, 0.55f, 1) : Color.cyan;
                if (u.hp > 0 && (showAllPaths || showPaths && chosen) && !string.IsNullOrEmpty(u.pathCells))
                {
                    var cells = u.pathCells.Split(';');
                    var points = new[] { (Vector3)p }.Concat(cells.Skip(u.pathIndex + 1).Take(Math.Max(0, cells.Length - u.pathIndex - 2))
                        .Select(v => v.Split(',')).Select(v => (Vector3)coord(new HexCoordinates(int.Parse(v[0]), int.Parse(v[1])))))
                        .Concat(u.finalPoints.Skip(u.finalApproachIndex).Select(v => (Vector3)screen(v))).ToArray();
                    Handles.color = color; if (points.Length > 1) Handles.DrawAAPolyLine(chosen ? 3 : 1, points);
                }
                if (chosen && u.hp > 0 && u.hasTarget)
                {
                    var targetPoint = coord(new HexCoordinates(u.targetQ, u.targetR));
                    Handles.color = Color.green; Handles.DrawDottedLine(p, targetPoint, 3); Handles.DrawWireDisc(targetPoint, Vector3.forward, 13);
                    DrawMapTag(rect, targetPoint, (u.isTemporaryWork ? "임시 공격 · " : "행동 목표 · ") + TargetLabel(u), Color.green);
                }
                if (chosen && u.hp > 0 && u.isTemporaryWork && u.hasStrategicTarget)
                {
                    var strategicPoint = coord(new HexCoordinates(u.strategicTargetQ, u.strategicTargetR));
                    Handles.color = new Color(1f, 0.72f, 0.18f, 0.95f);
                    Handles.DrawDottedLine(p, strategicPoint, 6);
                    Handles.DrawWireDisc(strategicPoint, Vector3.forward, 17);
                    DrawMapTag(rect, strategicPoint, "전략 목표 유지 · " + StrategicTargetLabel(u), Handles.color);
                }
                if (showSlots && chosen && u.hp > 0 && u.hasSlot)
                {
                    var sp = screen(new Vector3(u.slotX, 0, u.slotZ));
                    Handles.color = color; Handles.DrawWireDisc(sp, Vector3.forward, 3);
                }
                Handles.color = u.hp > 0 ? color : Color.gray; Handles.DrawSolidDisc(p, Vector3.forward, chosen ? 8 : 6);
                var hasActiveIssue = result?.diagnostics != null && result.diagnostics.Any(issue => issue.unit == u.id &&
                    issue.severity != "정보" && issue.startTick <= frame.tick && frame.tick <= issue.endTick);
                if (hasActiveIssue)
                {
                    Handles.color = new Color(1f, 0.28f, 0.18f);
                    Handles.DrawWireDisc(p, Vector3.forward, chosen ? 15 : 11);
                }
                if (chosen) { Handles.color = Color.white; Handles.DrawWireDisc(p, Vector3.forward, 11); }
                if (showStates || chosen) DrawMapTag(rect, p, "공격 " + u.id + " · " + (u.cohort > 0 ? "C" + u.cohort : "미배정") + " · " +
                    DisplayState(u.state, u.hp) + (chosen && u.hp > 0 ? " · 재판단 " + (u.decisionRequested ? "요청" : u.nextDecisionIn.ToString("F1") + "초") : ""), color);
                if (Event.current.type == EventType.MouseDown && Vector2.Distance(Event.current.mousePosition, p) < 10)
                { selectedUnit = u.id; selectedDefender = 0; selectedCell = null; rosterTab = 0; inspectMode = true; Event.current.Use(); Repaint(); }
            }
            if (frame != null && result != null)
            {
                var hitTicks = Mathf.CeilToInt(0.25f / input.step);
                foreach (var hitFrame in result.frames.Skip(Math.Max(0, frameIndex - hitTicks + 1)).Take(hitTicks))
                    foreach (var hit in hitFrame.events.Where(e => e.kind == "AttackMarkerApplied" && e.hasTargetPosition))
                    {
                        var attacker = hitFrame.units.FirstOrDefault(u => u.id == hit.unit);
                        if (attacker == null) continue;
                        var age = (frame.tick - hitFrame.tick) / (float)hitTicks;
                        if (age < 0 || age >= 1) continue;
                        var targetPoint = screen(new Vector3(hit.targetX, 0, hit.targetZ));
                        Handles.color = new Color(1f, 0.9f, 0.55f, 1f - age);
                        Handles.DrawAAPolyLine(2f, screen(new Vector3(attacker.x, 0, attacker.z)), targetPoint);
                        Handles.DrawWireDisc(targetPoint, Vector3.forward, 5f + age * 12f);
                    }
            }
            if (frame != null) foreach (var d in frame.defenders)
            {
                if (d.hp <= 0 && !showDead && d.id != selectedDefender) continue;
                var p = screen(new Vector3(d.x, 0, d.z)); Handles.color = d.hp > 0 ? new Color(1, 0.4f, 0.4f) : Color.gray;
                if (d.jumping)
                {
                    Handles.color = new Color(1f, 0.82f, 0.2f);
                    Handles.DrawAAPolyLine(4, screen(new Vector3(d.jumpStartX, 0, d.jumpStartZ)), screen(new Vector3(d.jumpEndX, 0, d.jumpEndZ)));
                }
                if (d.hp > 0 && (showAllPaths || showPaths && d.id == selectedDefender) && !string.IsNullOrEmpty(d.pathCells))
                {
                    var points = d.pathCells.Split(';').Select(v => v.Split(',')).Select(v => (Vector3)coord(new HexCoordinates(int.Parse(v[0]), int.Parse(v[1])))).ToArray();
                    if (points.Length > 1) Handles.DrawAAPolyLine(d.id == selectedDefender ? 3 : 1, points);
                }
                HexSiegeSimulationSymbols.DrawDefender(p, d.role, d.hp > 0, d.id == selectedDefender);
                var target = frame.units.FirstOrDefault(u => u.id == d.targetUnit);
                if (d.id == selectedDefender && target != null) Handles.DrawDottedLine(p, screen(new Vector3(target.x, 0, target.z)), 3);
                DrawMapTag(rect, p, DisplayState(d.role, 1) + " " + d.id +
                    (showStates || d.id == selectedDefender ? " · " + DisplayState(d.state, d.hp) : ""),
                    d.role == "Knight" ? new Color(1, 0.32f, 0.4f) : new Color(1, 0.73f, 0.2f));
                if (Event.current.type == EventType.MouseDown && Vector2.Distance(Event.current.mousePosition, p) < 10)
                { selectedDefender = d.id; selectedUnit = 0; selectedCell = null; rosterTab = 1; inspectMode = true; Event.current.Use(); Repaint(); }
            }
            if (hoveredCell != null)
            {
                EditorGUI.DrawRect(new Rect(8, 30, rect.width - 16, 25), new Color(0.06f, 0.08f, 0.11f, 0.95f));
                GUI.Label(new Rect(16, 33, rect.width - 32, 22), hoveredCell, EditorStyles.whiteLabel);
            }
            DrawStructureLegend(rect, input.layers);
            GUI.Label(new Rect(rect.x + 10, rect.yMax - 38, rect.width - 20, 18), "● 공격 병력  ·  붉은 방패 ‘기’: 기사 수비대  ·  황금 마름모 ‘농’: 농부 수비대  ·  노란 점: 함정", EditorStyles.whiteMiniLabel);
            GUI.Label(new Rect(rect.x + 10, rect.yMax - 20, rect.width - 20, 18), "병력 클릭: 상세 보기   ·   휠: 확대/축소   ·   휠 버튼 드래그: 지도 이동", EditorStyles.whiteMiniLabel);
            GUI.EndGroup();
        }

        private static string TrapLabel(string type)
        {
            switch (type)
            {
                case "Snare": return "덫";
                case "SpikePlate": return "가시 발판";
                case "BlastMine": return "폭발 지뢰";
                default: return "함정";
            }
        }

        private static void DrawStructureLegend(Rect rect, int defenseLayers)
        {
            var left = rect.x + 10f;
            var right = left + Mathf.Clamp(rect.width * 0.22f, 160f, 230f);
            var y = rect.yMax - 112f;
            const float row = 18f;

            DrawWallLegendItem(left, y, defenseLayers);
            DrawLegendItem(right, y, "닫문", new Color(0.8f, 0.35f, 0.15f));
            DrawLegendItem(left, y + row, "격벽", new Color(0.52f, 0.31f, 0.42f));
            DrawLegendItem(right, y + row, "열문", new Color(0.2f, 0.55f, 0.8f));
            DrawLegendItem(left, y + row * 2f, "포탑", new Color(0.66f, 0.25f, 0.22f));
            DrawLegendItem(right, y + row * 2f, "건물", new Color(0.40f, 0.36f, 0.62f));
            DrawLegendItem(left, y + row * 3f, "병영", new Color(0.42f, 0.12f, 0.20f));
            DrawLegendItem(right, y + row * 3f, "농부병영", new Color(0.45f, 0.34f, 0.10f));
        }

        private static void DrawWallLegendItem(float x, float y, int defenseLayers)
        {
            GUI.Label(new Rect(x, y, 70f, 18f), "벽 · " + Mathf.Clamp(defenseLayers, 2, 4) + "중벽", EditorStyles.whiteMiniLabel);
            var chipX = x + 72f;
            for (var layer = 1; layer <= Mathf.Clamp(defenseLayers, 2, 4); layer++)
            {
                EditorGUI.DrawRect(new Rect(chipX, y + 4f, 12f, 10f), HexSiegeSimulationSymbols.WallLayerColor(layer));
                GUI.Label(new Rect(chipX + 2f, y, 10f, 18f), layer.ToString(), new GUIStyle(EditorStyles.whiteMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 8
                });
                chipX += 16f;
            }
        }

        private static void DrawLegendItem(float x, float y, string label, Color color)
        {
            EditorGUI.DrawRect(new Rect(x, y + 4f, 12f, 10f), color);
            GUI.Label(new Rect(x + 17f, y, 110f, 18f), label, EditorStyles.whiteMiniLabel);
        }

        private void DrawMapTag(Rect bounds, Vector2 point, string label, Color color)
        {
            var style = new GUIStyle(EditorStyles.whiteLabel) { fontSize = 12 };
            var width = style.CalcSize(new GUIContent(label)).x + 16;
            for (var i = 0; i < 24; i++)
            {
                var row = i == 0 ? 0 : (i + 1) / 2 * (i % 2 == 0 ? -1 : 1);
                var candidate = new Rect(Mathf.Clamp(point.x + 15, 8, Mathf.Max(8, bounds.width - width - 8)), point.y - 26 + row * 25, width, 22);
                if (candidate.y < 58 || candidate.yMax > bounds.height - 125 || mapLabels.Any(r => r.Overlaps(candidate))) continue;
                mapLabels.Add(candidate);
                Handles.color = color; Handles.DrawLine(point, new Vector3(candidate.x, candidate.center.y));
                EditorGUI.DrawRect(candidate, new Color(0.035f, 0.045f, 0.06f, 0.96f));
                EditorGUI.DrawRect(new Rect(candidate.x, candidate.y, 3, candidate.height), color);
                GUI.Label(new Rect(candidate.x + 8, candidate.y + 2, width - 10, 20), label, style);
                break;
            }
        }
    }
}
