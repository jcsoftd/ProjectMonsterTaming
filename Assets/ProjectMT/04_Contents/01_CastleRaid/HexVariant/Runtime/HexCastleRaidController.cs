using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ProjectMT.Contents.Framework;
using ProjectMT.Shared.Audio;
using ProjectMT.Shared.Combat;
using ProjectMT.Shared.Equipment;
using ProjectMT.Shared.GameData;
using ProjectMT.Shared.Pooling;
using ProjectMT.Shared.Reward;
using ProjectMT.Shared.Unit;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectMT.Contents.CastleRaidHex
{
    public enum HexCastleRaidFailureReason
    {
        None = 0,
        TimeExpired = 1,
        AssaultEliminated = 2
    }

    public sealed class HexCastleRaidResult : IObjectiveCompletionResultData
    {
        public HexCastleRaidResult(
            bool palaceDestroyed,
            RewardBundle lootRewards = null,
            HexCastleRaidFailureReason failureReason = HexCastleRaidFailureReason.None)
            : this(
                palaceDestroyed,
                lootRewards,
                Array.Empty<EquipmentInstanceData>(),
                failureReason)
        {
        }

        public HexCastleRaidResult(
            bool palaceDestroyed,
            RewardBundle lootRewards,
            IReadOnlyList<EquipmentInstanceData> equipmentRewards,
            HexCastleRaidFailureReason failureReason = HexCastleRaidFailureReason.None)
        {
            PalaceDestroyed = palaceDestroyed;
            LootRewards = lootRewards ?? RewardBundle.Empty;
            EquipmentRewards = equipmentRewards ?? Array.Empty<EquipmentInstanceData>();
            FailureReason = failureReason;
        }

        public bool PalaceDestroyed { get; }
        public RewardBundle LootRewards { get; }
        public IReadOnlyList<EquipmentInstanceData> EquipmentRewards { get; }
        public HexCastleRaidFailureReason FailureReason { get; }
        public bool ObjectiveCompleted => PalaceDestroyed;
    }

    [DisallowMultipleComponent]
    public sealed class HexCastleRaidController : MonoBehaviour, IContentController // 육각 배치·전투·결과 총괄
    {
        private const int DefaultDifficultyLevel = 4;
        private const int DefaultGenerationSeed = 10801;
        public const float BattleDurationSeconds = 180f;

        [Header("Stage")]
        [SerializeField] private HexCastleThemeOneRules themeRules;
        [SerializeField] private HexCastleVisualSet visualSet;
        [SerializeField] private HexCastleTurretAttackCatalog turretAttackCatalog;
        [SerializeField] private HexCastleTheme stageTheme = HexCastleTheme.CentralCompartment;
        [SerializeField, Range(1, 10)] private int difficultyLevel = DefaultDifficultyLevel;
        [SerializeField] private int generationSeed = DefaultGenerationSeed;
        [SerializeField] private Transform stageAnchor;
        [SerializeField] private Camera deploymentCamera;
        [SerializeField] private HexCastleCameraController cameraController;

        [Header("Runtime")]
        [SerializeField] private ScenePoolScope poolScope;
        [SerializeField] private SfxPool sfxPool;
        [SerializeField] private CombatFeedbackPlayer combatFeedback;

        [Header("Destruction Audio")]
        [SerializeField] private SfxCue structureDestroyedSfx;
        [SerializeField] private SfxCue palaceDestroyedSfx;

        [Header("Deployment And Trap Audio")]
        [SerializeField] private SfxCue deploymentSfx; // 배치 성공 때만 재생
        [SerializeField] private SfxCue snareTriggeredSfx;
        [SerializeField] private SfxCue spikeTriggeredSfx;
        [SerializeField] private SfxCue mineTriggeredSfx;
        [SerializeField] private GameObject mineExplosionVfx;

        [Header("Combat Audio")]
        [SerializeField] private SfxCue attackSfx;
        [SerializeField] private SfxCue wallHitSfx;
        [SerializeField] private SfxCue buildingHitSfx;

        [Header("HUD")]
        [SerializeField] private TMP_Text deploymentText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text castleInfoText;
        [SerializeField] private Button[] unitButtons = Array.Empty<Button>();
        [SerializeField] private TMP_Text[] unitButtonLabels = Array.Empty<TMP_Text>();
        [SerializeField] private Button[] unitAiTagButtons = Array.Empty<Button>();
        [SerializeField] private TMP_Text[] unitAiTagLabels = Array.Empty<TMP_Text>();
        [SerializeField] private GameObject aiDescriptionPanel;
        [SerializeField] private TMP_Text aiDescriptionText;
        [SerializeField] private Button[] difficultyButtons = Array.Empty<Button>();
        [SerializeField] private Button regenerateCastleButton;
        [SerializeField] private Button rotateCameraLeftButton;
        [SerializeField] private Button rotateCameraRightButton;
        [SerializeField] private Button exitButton;
        [SerializeField] private HexCastleDeploymentInputSurface inputSurface;

        private readonly Dictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells =
            new Dictionary<HexCoordinates, HexCastleCellRuntime>();
        private readonly Dictionary<HexCoordinates, int> deploymentsPerCell =
            new Dictionary<HexCoordinates, int>();
        private readonly List<HexCastleAssaultUnit> activeUnits = new List<HexCastleAssaultUnit>();
        private readonly List<HexCastleBarracksRuntime> barracksRuntimes =
            new List<HexCastleBarracksRuntime>();

        private ContentContext context;
        private IPartyDeploymentStartData startData;
        private HexCastleLayout layout;
        private GameObject stageInstance;
        private HexCastleProceduralStage proceduralStage;
        private HexCastleTurretCombatWorld combatWorld;
        private HexCastleGarrisonWorld garrisonWorld;
        private HexCastleAssaultWorld assaultWorld;
        private HexCastleTrapWorld trapWorld;
        private HexCastleDeploymentAreaVisual deploymentAreaVisual;
        private HexCastleBattleHudView battleHudView;
        private HexCastleLootSession lootSession;
        private HexCastleAssaultAIProfileCatalog aiProfileCatalog;
        private HexCastleCellRuntime palaceCore;
        private UnityAction[] unitButtonActions = Array.Empty<UnityAction>();
        private UnityAction[] unitAiTagActions = Array.Empty<UnityAction>();
        private UnityAction[] difficultyButtonActions = Array.Empty<UnityAction>();
        private int[] remainingDeployments = Array.Empty<int>();
        private int selectedUnitIndex = -1;
        private int deployedCount;
        private bool resultSent;
        private bool generationInProgress;
        private bool progressionStageRun;
        private int progressionStage;
        private bool battleStarted;
        private bool runFailed;
        private bool churchDestroyed;
        private bool ownsTimePause;
        private float previousTimeScale = 1f;
        private float remainingBattleSeconds = BattleDurationSeconds;

        public bool IsRunning { get; private set; }
        public int DeployedCount => deployedCount;
        public int RemainingDeploymentCount => remainingDeployments.Sum();
        public int ActiveAssaultUnitCount => activeUnits.Count(value => value != null && value.IsAlive);
        public HexCastleProceduralStage ActiveStage => proceduralStage;
        public HexCastleAssaultWorld ActiveAssaultWorld => assaultWorld;
        public HexCastleTrapWorld ActiveTrapWorld => trapWorld;
        public HexCastleDeploymentAreaVisual DeploymentAreaVisual => deploymentAreaVisual;
        public int CurrentDifficultyLevel => layout?.DifficultyLevel ?? difficultyLevel;
        public HexCastleTheme CurrentTheme => layout?.Theme ?? stageTheme;
        public int CurrentDefenseLayerCount => layout?.DefenseLayerCount ??
                                               HexCastleDifficultyProfile.ResolveDefenseLayerCount(
                                                   difficultyLevel,
                                                   generationSeed);
        public int CurrentSeed => layout?.Seed ?? generationSeed;
        public bool BattleStarted => battleStarted;
        public bool IsFailureVisible => runFailed;
        public float RemainingBattleSeconds => remainingBattleSeconds;
        public HexCastleBattleHudView BattleHudView => battleHudView;
        private BattleUnitSnapshot[] DeploymentUnits => startData is HexCastleRaidStartData hex
            ? hex.DeploymentUnits
            : startData?.Party?.Units ?? Array.Empty<BattleUnitSnapshot>();
        public string CurrentStageId => layout == null
            ? string.Empty
            : $"HEX_PROC_T{HexCastleThemeCatalog.ResolveCode(layout.Theme)}_D{layout.DifficultyLevel:00}_" +
              $"W{layout.DefenseLayerCount}_{layout.Seed}";

        public void Initialize(ContentContext contentContext)
        {
            Shutdown();
            context = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
            startData = contentContext.StartData as IPartyDeploymentStartData;
            if (startData?.Party == null || startData.UnitSlotCount <= 0)
            {
                throw new ArgumentException("부대 투입 시작값이 필요합니다.", nameof(contentContext));
            }

            if (startData is HexCastleRaidStartData hexStartData)
            {
                hexStartData.EquipmentRewards.Initialize(contentContext.Progress.View);
            }

            ConfigureProgressionStage(contentContext.RunInfo);
            battleHudView = statusText == null
                ? null
                : statusText.GetComponentInParent<HexCastleBattleHudView>(true);
            ValidateSceneReferences();
            CreateStage();
            ConfigureDefenseRuntime();
            ConfigureLootRuntime();
            ConfigureHud();
            selectedUnitIndex = -1;
            deployedCount = 0;
            resultSent = false;
            battleStarted = false;
            runFailed = false;
            churchDestroyed = false;
            remainingBattleSeconds = BattleDurationSeconds;
            IsRunning = true;
            battleHudView.SetTimer(remainingBattleSeconds, false);
            SetStatus("몬스터를 선택한 뒤 외곽 육각 칸에 배치하세요");
            UpdateHud();
            UpdateGenerationHud();
        }

        private void Update()
        {
            if (!IsRunning || resultSent || runFailed || !battleStarted)
            {
                return;
            }

            remainingBattleSeconds = Mathf.Max(0f, remainingBattleSeconds - Time.deltaTime);
            battleHudView?.SetTimer(remainingBattleSeconds, true);
            if (remainingBattleSeconds <= 0f)
            {
                BeginFailure(HexCastleRaidFailureReason.TimeExpired);
            }
        }

        public bool TrySelectUnit(int index)
        {
            if (!IsRunning || index < 0 || index >= remainingDeployments.Length ||
                remainingDeployments[index] <= 0)
            {
                return false;
            }

            selectedUnitIndex = index;
            SetStatus($"{ResolveUnitLabel(index)} 선택 · 외곽 칸을 누르세요");
            UpdateHud();
            return true;
        }

        public bool TryDeployAtScreenPosition(Vector2 screenPosition)
        {
            if (!IsRunning || selectedUnitIndex < 0)
            {
                SetStatus("먼저 몬스터를 선택하세요");
                return false;
            }

            if (deploymentCamera == null || stageInstance == null)
            {
                return false;
            }

            var plane = new Plane(Vector3.up, stageInstance.transform.position);
            var ray = deploymentCamera.ScreenPointToRay(screenPosition);
            if (!plane.Raycast(ray, out var distance))
            {
                return false;
            }

            var localPoint = stageInstance.transform.InverseTransformPoint(ray.GetPoint(distance));
            var coordinates = HexCoordinates.FromWorld(localPoint, HexSpatialContract.CellOuterRadius);
            return TryDeployAtCell(coordinates);
        }

        public bool TryDeployAtCell(HexCoordinates coordinates)
        {
            if (!IsRunning || selectedUnitIndex < 0 || selectedUnitIndex >= remainingDeployments.Length ||
                remainingDeployments[selectedUnitIndex] <= 0 ||
                !runtimeCells.TryGetValue(coordinates, out var deploymentCell) || deploymentCell == null ||
                deploymentCell.Kind != HexCastleCellKind.Deployment || deploymentCell.InitialBlocked)
            {
                SetStatus("외곽 배치 육각 칸에만 배치할 수 있습니다");
                return false;
            }

            var deployedIndex = selectedUnitIndex;
            var snapshot = DeploymentUnits[deployedIndex];
            var unit = DeploySnapshotAtCell(snapshot, coordinates);
            if (unit == null)
            {
                return false;
            }

            remainingDeployments[deployedIndex]--;
            deployedCount++;
            if (!battleStarted)
            {
                battleStarted = true;
                battleHudView?.SetTimer(remainingBattleSeconds, true);
            }
            selectedUnitIndex = remainingDeployments[deployedIndex] > 0 ? deployedIndex : -1;
            SetStatus(remainingDeployments[deployedIndex] > 0
                ? $"{ResolveUnitLabel(deployedIndex)} {remainingDeployments[deployedIndex]}마리 남음"
                : $"{ResolveUnitLabel(deployedIndex)} 배치 완료");
            UpdateHud();
            return true;
        }

        private HexCastleAssaultUnit DeploySnapshotAtCell(
            BattleUnitSnapshot snapshot,
            HexCoordinates coordinates)
        {
            var assaultPrefab = snapshot?.RuntimeAssetSet?.VisualAdapterPrefab;
            if (snapshot == null || assaultPrefab == null)
            {
                Debug.LogError($"Hex Castle Raid에 정식 몬스터 실행 자산이 없습니다. Unit={snapshot?.UnitId}");
                SetStatus("몬스터 실행 자산을 확인해주세요");
                return null;
            }

            var spawnPoint = stageInstance.transform.position + coordinates.ToWorld(HexSpatialContract.CellOuterRadius);
            var rotation = Quaternion.LookRotation(
                Vector3.ProjectOnPlane(stageInstance.transform.position - spawnPoint, Vector3.up).normalized,
                Vector3.up);
            var instance = poolScope.Rent(assaultPrefab, spawnPoint, rotation, stageInstance.transform);
            if (instance == null)
            {
                return null;
            }

            var unit = instance.GetComponent<HexCastleAssaultUnit>() ??
                       instance.AddComponent<HexCastleAssaultUnit>();
            unit.ConfigureForPartyUnit(
                assaultWorld,
                coordinates,
                runtimeCells,
                HexSpatialContract.CellOuterRadius,
                stageInstance.transform.position,
                snapshot);
            unit.transform.position += ResolveDeploymentOffset(coordinates);
            unit.ConfigureAttackAudio(sfxPool, attackSfx);
            unit.Damaged -= HandleUnitDamaged;
            unit.Damaged += HandleUnitDamaged;
            unit.Died -= HandleUnitDied;
            unit.Died += HandleUnitDied;
            combatWorld.RegisterAssaultUnit(unit);
            activeUnits.Add(unit);
            sfxPool?.Play(deploymentSfx, unit.transform.position);
            return unit;
        }

        public void Cancel()
        {
            if (!IsRunning || resultSent)
            {
                return;
            }

            resultSent = true;
            IsRunning = false;
            RestoreTimeScale();
            UpdateDeploymentAreaVisual();
            combatWorld?.SetRunning(false);
            context?.Exit.Cancel();
        }

        public void Shutdown()
        {
            RestoreTimeScale();
            IsRunning = false;
            deploymentAreaVisual?.SetVisible(false);
            StopAllCoroutines();
            UnbindHud();
            battleHudView?.Unbind();
            battleHudView?.HideFailure();
            cameraController?.StopRotation();
            if (palaceCore != null)
            {
                palaceCore.Destroyed -= HandlePalaceDestroyed;
            }

            foreach (var cell in runtimeCells.Values)
            {
                if (cell != null)
                {
                    cell.Damaged -= HandleCellDamaged;
                    cell.Destroyed -= HandleCellDestroyed;
                }
            }

            for (var index = 0; index < barracksRuntimes.Count; index++)
            {
                barracksRuntimes[index]?.Shutdown();
            }
            barracksRuntimes.Clear();
            if (garrisonWorld != null)
            {
                garrisonWorld.UnitSpawned -= HandleGarrisonSpawned;
                foreach (var garrison in garrisonWorld.Units)
                {
                    if (garrison != null)
                    {
                        garrison.Damaged -= HandleGarrisonDamaged;
                    }
                }
                garrisonWorld.Shutdown();
            }
            if (trapWorld != null)
            {
                trapWorld.TrapTriggered -= HandleTrapTriggered;
                trapWorld.TrapDetonated -= HandleTrapDetonated;
                trapWorld.Shutdown();
            }
            assaultWorld?.Shutdown();
            combatWorld?.SetRunning(false);
            combatWorld?.ClearRegistry();

            for (var index = 0; index < activeUnits.Count; index++)
            {
                if (activeUnits[index] != null)
                {
                    activeUnits[index].Damaged -= HandleUnitDamaged;
                    activeUnits[index].Died -= HandleUnitDied;
                }
            }
            activeUnits.Clear();
            poolScope?.ReturnAll();

            if (stageInstance != null)
            {
                stageInstance.SetActive(false);
                Destroy(stageInstance);
            }

            runtimeCells.Clear();
            deploymentsPerCell.Clear();
            remainingDeployments = Array.Empty<int>();
            selectedUnitIndex = -1;
            deployedCount = 0;
            palaceCore = null;
            combatWorld = null;
            garrisonWorld = null;
            assaultWorld = null;
            trapWorld = null;
            deploymentAreaVisual = null;
            battleHudView = null;
            lootSession = null;
            aiProfileCatalog = null;
            proceduralStage = null;
            stageInstance = null;
            layout = null;
            startData = null;
            context = null;
            resultSent = false;
            progressionStageRun = false;
            progressionStage = 0;
            battleStarted = false;
            runFailed = false;
            churchDestroyed = false;
            remainingBattleSeconds = BattleDurationSeconds;
        }

        private void ConfigureProgressionStage(ContentRunInfo runInfo)
        {
            progressionStageRun = runInfo.RunMode != ContentRunMode.SeedTest &&
                                  int.TryParse(runInfo.StageId, out progressionStage) &&
                                  CastleRaidStageRules.IsValidStage(progressionStage);
            if (!progressionStageRun)
            {
                progressionStage = 0;
                return;
            }

            difficultyLevel = CastleRaidStageRules.ResolveDifficulty(progressionStage);
            generationSeed = CastleRaidStageRules.ResolveGenerationSeed(progressionStage);
            var themes = HexCastleThemeCatalog.Themes;
            stageTheme = themes[(progressionStage - 1) % themes.Count];
        }

        private void ValidateSceneReferences()
        {
            if (stageAnchor == null || deploymentCamera == null || cameraController == null ||
                themeRules == null || visualSet == null || !visualSet.IsRuntimeComplete ||
                turretAttackCatalog == null || poolScope == null || sfxPool == null ||
                combatFeedback == null ||
                unitButtons == null || unitButtons.Length == 0 ||
                unitButtonLabels == null || unitButtonLabels.Length != unitButtons.Length ||
                unitAiTagButtons == null || unitAiTagButtons.Length != unitButtons.Length ||
                unitAiTagLabels == null || unitAiTagLabels.Length != unitButtons.Length ||
                aiDescriptionPanel == null || aiDescriptionText == null ||
                difficultyButtons == null || difficultyButtons.Length != 10 ||
                difficultyButtons.Any(value => value == null) || regenerateCastleButton == null ||
                rotateCameraLeftButton == null || rotateCameraRightButton == null ||
                exitButton == null || inputSurface == null || battleHudView == null ||
                !battleHudView.HasRuntimeBindings || battleHudView.ItemCatalog == null ||
                battleHudView.ItemDropVisualCatalog == null ||
                battleHudView.EquipmentBalanceConfig == null ||
                battleHudView.EquipmentDropVisualCatalog == null)
            {
                throw new InvalidOperationException("육각 군단의 역습 정식 씬 참조가 불완전합니다.");
            }
        }

        private void CreateStage()
        {
            var candidate = new HexCastleGenerationPipeline().GenerateFoundationForDifficulty(
                generationSeed,
                difficultyLevel,
                stageTheme,
                themeRules.Tuning);
            if (!candidate.Validation.IsValid)
            {
                throw new InvalidOperationException(string.Join("\n", candidate.Validation.Errors));
            }

            layout = candidate.Layout;
            proceduralStage = HexCastleProceduralStageBuilder.Build(
                layout,
                visualSet,
                turretAttackCatalog,
                stageAnchor);
            stageInstance = proceduralStage.gameObject;
            cameraController.ConfigureBounds(proceduralStage.WorldBounds);
        }

        private void ConfigureDefenseRuntime()
        {
            runtimeCells.Clear();
            foreach (var cell in stageInstance.GetComponentsInChildren<HexCastleCellRuntime>(true))
            {
                cell.InitializeState();
                cell.Damaged -= HandleCellDamaged;
                cell.Damaged += HandleCellDamaged;
                cell.Destroyed -= HandleCellDestroyed;
                cell.Destroyed += HandleCellDestroyed;
                runtimeCells.Add(cell.Coordinates, cell);
            }

            if (runtimeCells.Count != layout.Cells.Count)
            {
                throw new InvalidOperationException("절차 생성 Stage의 Cell 수가 Layout과 다릅니다.");
            }

            deploymentAreaVisual = stageInstance.GetComponent<HexCastleDeploymentAreaVisual>() ??
                                   stageInstance.AddComponent<HexCastleDeploymentAreaVisual>();
            deploymentAreaVisual.Configure(runtimeCells.Values);
            deploymentAreaVisual.SetVisible(false);

            palaceCore = runtimeCells.TryGetValue(new HexCoordinates(0, 0), out var center)
                ? center
                : null;
            if (palaceCore == null || palaceCore.Kind != HexCastleCellKind.Palace)
            {
                throw new InvalidOperationException("왕궁 중심 Cell이 없습니다.");
            }
            cameraController.SetRotationFocus(palaceCore.transform.position);
            palaceCore.Destroyed -= HandlePalaceDestroyed;
            palaceCore.Destroyed += HandlePalaceDestroyed;

            combatWorld = stageInstance.GetComponent<HexCastleTurretCombatWorld>();
            if (combatWorld == null)
            {
                throw new InvalidOperationException("절차 생성 Stage의 Hex 포탑 전투 World가 없습니다.");
            }
            combatWorld.Configure(poolScope, sfxPool, HexSpatialContract.CellOuterRadius, true);
            combatWorld.RebuildRegistry(stageInstance.transform);

            var catalog = Resources.Load<HexCastleGarrisonCatalog>(HexCastleGarrisonCatalog.DefaultResourcesPath);
            if (catalog == null || !catalog.IsComplete)
            {
                throw new InvalidOperationException("Hex 수비대 정식 외형 카탈로그가 불완전합니다.");
            }

            var tuning = themeRules.Tuning;
            var difficultyProfile = HexCastleDifficultyProfile.Resolve(
                CurrentDifficultyLevel,
                CurrentSeed);
            garrisonWorld = stageInstance.GetComponent<HexCastleGarrisonWorld>() ??
                            stageInstance.AddComponent<HexCastleGarrisonWorld>();
            garrisonWorld.UnitSpawned -= HandleGarrisonSpawned;
            garrisonWorld.UnitSpawned += HandleGarrisonSpawned;
            garrisonWorld.Configure(
                catalog,
                runtimeCells,
                stageInstance.transform.position,
                HexSpatialContract.CellOuterRadius,
                CurrentSeed,
                combatWorld,
                tuning,
                difficultyProfile);
            garrisonWorld.ApplyBuildingEffects(HasActiveTrainingYard(), false);

            aiProfileCatalog = Resources.Load<HexCastleAssaultAIProfileCatalog>(
                HexCastleAssaultAIProfileCatalog.DefaultResourcesPath);
            if (aiProfileCatalog == null)
            {
                throw new InvalidOperationException("Hex 공격 AI 프로필 카탈로그가 없습니다.");
            }
            if (!aiProfileCatalog.TryValidate(out var aiProfileError))
            {
                throw new InvalidOperationException(
                    $"Hex 공격 AI 프로필 카탈로그가 불완전합니다. {aiProfileError}");
            }

            assaultWorld = stageInstance.GetComponent<HexCastleAssaultWorld>() ??
                           stageInstance.AddComponent<HexCastleAssaultWorld>();
            assaultWorld.Configure(
                runtimeCells,
                HexSpatialContract.CellOuterRadius,
                CurrentDefenseLayerCount,
                garrisonWorld,
                aiProfileCatalog,
                CurrentSeed,
                combatFeedback);
            trapWorld = stageInstance.GetComponent<HexCastleTrapWorld>();
            if (trapWorld == null || trapWorld.TrapCount != difficultyProfile.TotalTrapCount)
            {
                throw new InvalidOperationException(
                    $"절차 생성 Stage의 함정 수가 난이도 계약과 다릅니다: " +
                    $"{trapWorld?.TrapCount ?? 0}/{difficultyProfile.TotalTrapCount}");
            }
            trapWorld.TrapTriggered -= HandleTrapTriggered;
            trapWorld.TrapTriggered += HandleTrapTriggered;
            trapWorld.TrapDetonated -= HandleTrapDetonated;
            trapWorld.TrapDetonated += HandleTrapDetonated;
            trapWorld.Bind(assaultWorld);

            barracksRuntimes.Clear();
            foreach (var cell in runtimeCells.Values.Where(value =>
                         value.BuildingRole == HexCastleBuildingRole.KnightBarracks ||
                         value.BuildingRole == HexCastleBuildingRole.FarmerBarracks))
            {
                var barracks = cell.GetComponent<HexCastleBarracksRuntime>() ??
                               cell.gameObject.AddComponent<HexCastleBarracksRuntime>();
                barracks.Configure(cell, garrisonWorld, tuning);
                barracksRuntimes.Add(barracks);
            }

            SpawnInitialGarrison(difficultyProfile);
        }

        private void ConfigureLootRuntime()
        {
            var rewardStage = progressionStageRun
                ? progressionStage
                : (CurrentDifficultyLevel - 1) * 10 + 1;
            lootSession = new HexCastleLootSession(
                stageInstance.transform,
                runtimeCells.Values,
                context.Progress,
                battleHudView.ItemCatalog,
                battleHudView.ItemDropVisualCatalog,
                battleHudView.EquipmentBalanceConfig,
                battleHudView.EquipmentDropVisualCatalog,
                stageInstance.transform,
                deploymentCamera,
                rewardStage,
                CurrentDifficultyLevel,
                CurrentSeed,
                (startData as HexCastleRaidStartData)?.EquipmentRewards);
        }

        private void HandleTrapTriggered(HexCastleAssaultUnit unit, HexCastleTrapRuntime trap)
        {
            if (unit == null || trap == null)
            {
                return;
            }

            HexCastleTrapFloatingLabel.Show(
                stageInstance != null ? stageInstance.transform : transform,
                unit.transform.position,
                trap.TrapType,
                deploymentCamera);
            var cue = trap.TrapType switch
            {
                HexCastleTrapType.Snare => snareTriggeredSfx,
                HexCastleTrapType.SpikePlate => spikeTriggeredSfx,
                _ => null
            };
            sfxPool?.Play(cue, trap.transform.position); // 같은 Cue 동시 대상은 풀에서 제한
        }

        private void HandleTrapDetonated(HexCastleTrapRuntime trap)
        {
            if (trap != null && trap.TrapType == HexCastleTrapType.BlastMine)
            {
                sfxPool?.Play(mineTriggeredSfx, trap.transform.position); // 지연 피해·폭발 VFX와 같은 시점
                var instance = combatWorld?.RentObject(mineExplosionVfx, trap.transform.position, Quaternion.identity);
                if (instance != null)
                {
                    var lifetime = instance.GetComponent<HexCastleTurretVfxLifetime>() ?? instance.AddComponent<HexCastleTurretVfxLifetime>();
                    lifetime.Play(combatWorld, 1.7f, 0.55f);
                }
            }
        }

        private void SpawnInitialGarrison(HexCastleDifficultyProfile difficultyProfile)
        {
            garrisonWorld.SpawnInitialGarrison(difficultyProfile);
        }

        private void ConfigureHud()
        {
            remainingDeployments = new int[Mathf.Min(startData.UnitSlotCount, unitButtons.Length)];
            battleHudView.ConfigureDeployment(remainingDeployments.Length);
            unitButtonActions = new UnityAction[unitButtons.Length];
            unitAiTagActions = new UnityAction[unitButtons.Length];
            difficultyButtonActions = new UnityAction[difficultyButtons.Length];
            aiDescriptionPanel.SetActive(false);
            aiDescriptionPanel.GetComponent<Button>()?.onClick.AddListener(HideAiDescription);
            for (var index = 0; index < unitButtons.Length; index++)
            {
                var capturedIndex = index;
                var button = unitButtons[index];
                var aiTagButton = unitAiTagButtons[index];
                var visible = index < remainingDeployments.Length;
                button.gameObject.SetActive(visible);
                aiTagButton.gameObject.SetActive(visible);
                if (!visible)
                {
                    continue;
                }

                if (button.transform is RectTransform slotRect)
                {
                    slotRect.anchoredPosition = new Vector2(
                        (index - (remainingDeployments.Length - 1) * 0.5f) *
                        (battleHudView.HasDeploymentPresentation ? HexCastleBattleHudView.DeploymentSlotSpacing : 164f),
                        slotRect.anchoredPosition.y); // 본부대·예비를 합친 투입 목록을 중앙 정렬
                }

                remainingDeployments[index] = ResolveSummonsForSlot(index);
                unitButtonActions[index] = () => TrySelectUnit(capturedIndex);
                button.onClick.AddListener(unitButtonActions[index]);
                unitAiTagActions[index] = () => ToggleAiDescription(capturedIndex);
                aiTagButton.onClick.AddListener(unitAiTagActions[index]);
            }

            exitButton.onClick.AddListener(Cancel);
            for (var index = 0; index < difficultyButtons.Length; index++)
            {
                var capturedDifficulty = index + 1;
                difficultyButtonActions[index] = () => RestartWithDifficulty(capturedDifficulty);
                difficultyButtons[index].onClick.AddListener(difficultyButtonActions[index]);
            }
            regenerateCastleButton.onClick.AddListener(GenerateAnotherCastle);
            battleHudView.Bind(RetrySameRun, ExitAfterFailure);
            inputSurface.Configure(this, cameraController);
            UpdateGenerationHud();
        }

        private void UnbindHud()
        {
            if (unitButtons != null)
            {
                for (var index = 0; index < unitButtons.Length; index++)
                {
                    if (unitButtons[index] != null && index < unitButtonActions.Length &&
                        unitButtonActions[index] != null)
                    {
                        unitButtons[index].onClick.RemoveListener(unitButtonActions[index]);
                    }
                }
            }

            if (unitAiTagButtons != null)
            {
                for (var index = 0; index < unitAiTagButtons.Length; index++)
                {
                    if (unitAiTagButtons[index] != null && index < unitAiTagActions.Length &&
                        unitAiTagActions[index] != null)
                    {
                        unitAiTagButtons[index].onClick.RemoveListener(unitAiTagActions[index]);
                    }
                }
            }

            exitButton?.onClick.RemoveListener(Cancel);
            if (difficultyButtons != null)
            {
                for (var index = 0; index < difficultyButtons.Length; index++)
                {
                    if (difficultyButtons[index] != null && index < difficultyButtonActions.Length &&
                        difficultyButtonActions[index] != null)
                    {
                        difficultyButtons[index].onClick.RemoveListener(difficultyButtonActions[index]);
                    }
                }
            }
            regenerateCastleButton?.onClick.RemoveListener(GenerateAnotherCastle);
            battleHudView?.Unbind();
            aiDescriptionPanel?.GetComponent<Button>()?.onClick.RemoveListener(HideAiDescription);
            unitButtonActions = Array.Empty<UnityAction>();
            unitAiTagActions = Array.Empty<UnityAction>();
            difficultyButtonActions = Array.Empty<UnityAction>();
            aiDescriptionPanel?.SetActive(false);
        }

        private void GenerateAnotherCastle()
        {
            RestartWithDifficulty(CurrentDifficultyLevel);
        }

        public void RetrySameRun()
        {
            if (!runFailed || context == null)
            {
                return;
            }

            var retryContext = context;
            RestoreTimeScale();
            Shutdown();
            Initialize(retryContext);
        }

        public void ExitAfterFailure()
        {
            if (!runFailed || context == null || resultSent)
            {
                return;
            }

            resultSent = true;
            RestoreTimeScale();
            battleHudView?.HideFailure();
            context.Exit.Cancel(); // 실패창은 콘텐츠 내부에서 이미 표시했으므로 중복 공통 결과창 없이 복귀
        }

        private void RestartWithDifficulty(int targetDifficultyLevel)
        {
            if (!IsRunning || generationInProgress || context == null)
            {
                return;
            }

            targetDifficultyLevel = Mathf.Clamp(targetDifficultyLevel, 1, 10);
            var previousDifficultyLevel = CurrentDifficultyLevel;
            var previousSeed = CurrentSeed;
            var previousTheme = CurrentTheme;
            var restartContext = context;
            generationInProgress = true;
            UpdateGenerationHud();
            SetStatus($"난이도 {targetDifficultyLevel} 절차 성을 생성 중입니다...");
            try
            {
                Shutdown(); // 현재 유닛·수비대·Cell 이벤트를 모두 정리한 뒤 Stage를 바꾼다
                difficultyLevel = targetDifficultyLevel;
                generationSeed = NextGenerationSeed(previousSeed);
                stageTheme = HexCastleThemeCatalog.ResolveNextProceduralTheme(
                    previousTheme,
                    generationSeed,
                    targetDifficultyLevel);
                Initialize(restartContext);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                try
                {
                    difficultyLevel = previousDifficultyLevel;
                    generationSeed = previousSeed;
                    stageTheme = previousTheme;
                    Initialize(restartContext);
                    SetStatus("새 성 생성에 실패해 이전 성으로 돌아왔습니다");
                }
                catch (Exception restoreException)
                {
                    Debug.LogException(restoreException, this);
                    SetStatus("성을 준비하지 못했습니다. 콘텐츠에서 나갔다가 다시 시도해 주세요");
                }
            }
            finally
            {
                generationInProgress = false;
                UpdateGenerationHud();
            }
        }

        private void UpdateGenerationHud()
        {
            if (castleInfoText != null)
            {
                castleInfoText.text = layout == null
                    ? "<size=17><color=#DCC594>전투 준비</color></size>\n" +
                      "<b><size=30>군단의 역습</size></b>\n" +
                      "<size=16>육각 성을 준비하는 중입니다</size>"
                    : progressionStageRun
                        ? $"<size=17><color=#DCC594>{progressionStage}단계</color></size>\n" +
                          $"<b><size=30>군단의 역습</size></b>\n" +
                          $"<size=16>난이도 {layout.DifficultyLevel} · {HexCastleThemeCatalog.ResolveLabel(layout.Theme)} · {layout.DefenseLayerCount}중벽</size>"
                        : "<size=17><color=#DCC594>개발 전투</color></size>\n" +
                          "<b><size=30>군단의 역습</size></b>\n" +
                          $"<size=15>난이도 {layout.DifficultyLevel} · {HexCastleThemeCatalog.ResolveLabel(layout.Theme)} · {layout.DefenseLayerCount}중벽 · Seed {layout.Seed}</size>";
            }

            var canGenerate = IsRunning && !generationInProgress && !progressionStageRun;
            var generationControls = regenerateCastleButton != null
                ? regenerateCastleButton.transform.parent?.gameObject
                : null;
            generationControls?.SetActive(!progressionStageRun);
            if (difficultyButtons != null)
            {
                foreach (var button in difficultyButtons)
                {
                    if (button != null)
                    {
                        button.gameObject.SetActive(!progressionStageRun);
                    }
                    SetGenerationButtonState(button, canGenerate);
                }
            }
            if (regenerateCastleButton != null)
            {
                regenerateCastleButton.gameObject.SetActive(!progressionStageRun);
            }
            SetGenerationButtonState(regenerateCastleButton, canGenerate);
        }

        private static int NextGenerationSeed(int seed)
        {
            return seed == int.MaxValue ? DefaultGenerationSeed : seed + 1;
        }

        private static void SetGenerationButtonState(Button button, bool interactable)
        {
            if (button != null)
            {
                button.interactable = interactable;
            }
        }

        private void HandlePalaceDestroyed(HexCastleCellRuntime destroyedCell)
        {
            if (!IsRunning || resultSent || destroyedCell != palaceCore)
            {
                return;
            }

            sfxPool?.Play(palaceDestroyedSfx, destroyedCell.transform.position);
            resultSent = true;
            IsRunning = false;
            RestoreTimeScale();
            UpdateDeploymentAreaVisual();
            combatWorld?.SetRunning(false);
            SetStatus("왕궁 파괴 완료");
            var loot = lootSession?.CaptureRewards() ?? HexCastleLootCapture.Empty;
            context.Exit.Complete(new HexCastleRaidResult(
                true,
                loot.ItemRewards,
                loot.EquipmentRewards));
        }

        private void HandleUnitDied(HexCastleAssaultUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            unit.Damaged -= HandleUnitDamaged;
            unit.Died -= HandleUnitDied;
            assaultWorld?.UnregisterUnit(unit);
            combatWorld?.UnregisterAssaultUnit(unit);
            StartCoroutine(ReturnDeadUnit(unit));
        }

        private void HandleCellDamaged(HexCastleCellRuntime cell, DamageReport report)
        {
            if (cell == null)
            {
                return;
            }

            combatFeedback?.PlayDamage(
                HexCastleOverheadHealthBar.ResolveWorldAnchor(cell.transform),
                report.AppliedDamage,
                FloatingNumberStyle.EnemyDamage,
                cell.GetInstanceID(),
                assaultWorld?.ConsumePassiveDamageFeedback(cell.GetInstanceID()) ?? DamageFeedbackFlags.None,
                cell.Kind == HexCastleCellKind.Wall ? wallHitSfx : buildingHitSfx);
        }

        private void HandleCellDestroyed(HexCastleCellRuntime cell)
        {
            if (cell == null || resultSent)
            {
                return;
            }

            if (cell.Kind != HexCastleCellKind.Palace)
            {
                sfxPool?.Play(structureDestroyedSfx, cell.transform.position);
            }

            if (cell.BuildingRole == HexCastleBuildingRole.Church)
            {
                churchDestroyed = true;
                SetStatus("교회 파괴 · 수비대가 격분해 이동속도가 증가합니다");
            }

            if (cell.BuildingRole == HexCastleBuildingRole.TrainingYard ||
                cell.BuildingRole == HexCastleBuildingRole.Church)
            {
                garrisonWorld?.ApplyBuildingEffects(HasActiveTrainingYard(), churchDestroyed);
            }

            if (lootSession?.HandleDestroyed(cell) == true)
            {
                SetStatus("보급 건물 파괴 · 전리품은 공략 성공 시 확정됩니다");
            }
        }

        private bool HasActiveTrainingYard()
        {
            return runtimeCells.Values.Any(value =>
                value != null && value.BuildingRole == HexCastleBuildingRole.TrainingYard && value.IsAlive);
        }

        private void HandleUnitDamaged(HexCastleAssaultUnit unit, DamageReport report)
        {
            if (unit == null)
            {
                return;
            }

            combatFeedback?.PlayDamage(
                HexCastleOverheadHealthBar.ResolveWorldAnchor(unit.transform),
                report.AppliedDamage,
                FloatingNumberStyle.PlayerDamage,
                unit.GetInstanceID());
        }

        private void HandleGarrisonSpawned(HexCastleGarrisonUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            unit.Damaged -= HandleGarrisonDamaged;
            unit.Damaged += HandleGarrisonDamaged;
            unit.ConfigureAttackAudio(sfxPool, attackSfx);
        }

        private void HandleGarrisonDamaged(HexCastleGarrisonUnit unit, DamageReport report)
        {
            if (unit == null)
            {
                return;
            }

            combatFeedback?.PlayDamage(
                HexCastleOverheadHealthBar.ResolveWorldAnchor(unit.transform),
                report.AppliedDamage,
                FloatingNumberStyle.EnemyDamage,
                unit.GetInstanceID(),
                assaultWorld?.ConsumePassiveDamageFeedback(unit.GetInstanceID()) ?? DamageFeedbackFlags.None);
        }

        private IEnumerator ReturnDeadUnit(HexCastleAssaultUnit unit)
        {
            yield return new WaitForSeconds(Mathf.Max(0.05f, unit.DeathPresentationDuration + 0.05f));
            activeUnits.Remove(unit);
            if (unit != null)
            {
                poolScope?.Return(unit.gameObject);
            }

            EvaluateFailure();
        }

        private void EvaluateFailure()
        {
            if (!IsRunning || resultSent || RemainingDeploymentCount > 0 ||
                activeUnits.Any(value => value != null && value.IsAlive))
            {
                return;
            }

            BeginFailure(HexCastleRaidFailureReason.AssaultEliminated);
        }

        private void BeginFailure(HexCastleRaidFailureReason reason)
        {
            if (!IsRunning || resultSent || runFailed)
            {
                return;
            }

            runFailed = true;
            IsRunning = false;
            UpdateDeploymentAreaVisual();
            combatWorld?.SetRunning(false);
            SetStatus(reason == HexCastleRaidFailureReason.TimeExpired
                ? "제한 시간 180초가 종료되었습니다"
                : "공격 부대가 전멸했습니다");
            PauseTimeScale();
            battleHudView?.ShowFailure(reason, progressionStage);
        }

        private void PauseTimeScale()
        {
            if (ownsTimePause)
            {
                return;
            }

            previousTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            ownsTimePause = true;
        }

        private void RestoreTimeScale()
        {
            if (!ownsTimePause)
            {
                return;
            }

            Time.timeScale = previousTimeScale;
            ownsTimePause = false;
        }

        private Vector3 ResolveDeploymentOffset(HexCoordinates coordinates)
        {
            deploymentsPerCell.TryGetValue(coordinates, out var count);
            deploymentsPerCell[coordinates] = count + 1;
            if (count == 0)
            {
                return Vector3.zero;
            }

            var angle = Mathf.Deg2Rad * ((count - 1) * 137.5f);
            var radius = Mathf.Min(0.28f, 0.12f + count * 0.035f);
            return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        private string ResolveUnitLabel(int index)
        {
            if (index < 0 || index >= DeploymentUnits.Length)
            {
                return $"유닛 {index + 1}";
            }

            var unit = DeploymentUnits[index];
            return unit == null ? "미편성" : unit.DisplayName;
        }

        private void UpdateHud()
        {
            if (deploymentText != null)
            {
                deploymentText.text = $"남은 병력  {RemainingDeploymentCount} / {startData.DeploymentLimit}";
            }

            for (var index = 0; index < unitButtons.Length; index++)
            {
                if (unitButtons[index] == null || !unitButtons[index].gameObject.activeSelf)
                {
                    continue;
                }

                var remaining = index < remainingDeployments.Length ? remainingDeployments[index] : 0;
                unitButtons[index].interactable = remaining > 0;
                if (index < unitButtonLabels.Length && unitButtonLabels[index] != null)
                {
                    unitButtonLabels[index].text = battleHudView.HasDeploymentPresentation
                        ? ResolveUnitLabel(index)
                        : $"{ResolveUnitLabel(index)}\n{remaining}";
                }

                var snapshot = index < DeploymentUnits.Length
                    ? DeploymentUnits[index]
                    : null;
                var rarity = snapshot != null && snapshot.Presentation.HasRarity
                    ? snapshot.Presentation.Rarity
                    : MonsterRarity.Common;
                battleHudView.SetDeploymentSlot(index, snapshot?.Presentation.Portrait, remaining,
                    index == selectedUnitIndex && IsRunning && !runFailed, rarity, snapshot != null);
                if (index < unitAiTagButtons.Length && unitAiTagButtons[index] != null)
                    unitAiTagButtons[index].gameObject.SetActive(snapshot != null);

                if (index < unitAiTagLabels.Length && unitAiTagLabels[index] != null)
                {
                    unitAiTagLabels[index].text = HexCastleAssaultAIPresentation.ResolveTag(
                        ResolveUnitAiProfile(index));
                }
            }

            UpdateDeploymentAreaVisual();
        }

        private void UpdateDeploymentAreaVisual()
        {
            var hasSelection = IsRunning && selectedUnitIndex >= 0 &&
                               selectedUnitIndex < remainingDeployments.Length &&
                               remainingDeployments[selectedUnitIndex] > 0;
            deploymentAreaVisual?.SetVisible(hasSelection);
        }

        private void ToggleAiDescription(int index)
        {
            if (aiDescriptionPanel == null || aiDescriptionText == null ||
                index < 0 || index >= remainingDeployments.Length)
            {
                return;
            }

            var profile = ResolveUnitAiProfile(index);
            aiDescriptionText.text =
                $"<b>{ResolveUnitLabel(index)} · {HexCastleAssaultAIPresentation.ResolveTag(profile)}</b>\n" +
                HexCastleAssaultAIPresentation.ResolveDescription(profile);
            aiDescriptionPanel.SetActive(true);
        }

        private void HideAiDescription()
        {
            aiDescriptionPanel?.SetActive(false);
        }

        private HexCastleAssaultAIProfile ResolveUnitAiProfile(int index)
        {
            if (aiProfileCatalog == null || index < 0 || index >= DeploymentUnits.Length)
            {
                return null;
            }

            return aiProfileCatalog.Resolve(DeploymentUnits[index]?.UnitId);
        }

        private int ResolveSummonsForSlot(int index)
        {
            return startData is HexCastleRaidStartData hexStartData
                ? hexStartData.ResolveSummonsForSlot(index)
                : Mathf.Max(1, startData.SummonsPerSlot);
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message ?? string.Empty;
            }
        }

#if UNITY_EDITOR
        public HexCastleAssaultUnit EditorDeploySimulationUnit(
            BattleUnitSnapshot snapshot,
            HexCoordinates coordinates,
            HexCastleAssaultAIProfile profileOverride)
        {
            if (!IsRunning || snapshot == null ||
                !runtimeCells.TryGetValue(coordinates, out var cell) || cell == null ||
                cell.Kind != HexCastleCellKind.Deployment || cell.InitialBlocked)
            {
                return null;
            }

            var unit = DeploySnapshotAtCell(snapshot, coordinates);
            if (unit == null) return null;
            unit.EditorOverrideAIProfile(profileOverride);
            deployedCount++;
            if (!battleStarted)
            {
                battleStarted = true;
                battleHudView?.SetTimer(remainingBattleSeconds, true);
            }
            UpdateHud();
            return unit;
        }

        public void EditorConfigure(
            HexCastleThemeOneRules rules,
            HexCastleVisualSet runtimeVisualSet,
            HexCastleTurretAttackCatalog attackCatalog,
            Transform anchor,
            Camera worldCamera,
            HexCastleCameraController hexCamera,
            ScenePoolScope scenePool,
            SfxPool audioPool,
            CombatFeedbackPlayer feedback,
            TMP_Text deployment,
            TMP_Text status,
            TMP_Text info,
            Button[] buttons,
            TMP_Text[] labels,
            Button[] aiTagButtons,
            TMP_Text[] aiTagLabels,
            GameObject descriptionPanel,
            TMP_Text descriptionText,
            Button[] generationDifficultyButtons,
            Button regenerate,
            Button rotateLeft,
            Button rotateRight,
            Button exit,
            HexCastleDeploymentInputSurface surface,
            int targetDifficultyLevel = DefaultDifficultyLevel,
            int targetGenerationSeed = DefaultGenerationSeed)
        {
            themeRules = rules;
            visualSet = runtimeVisualSet;
            turretAttackCatalog = attackCatalog;
            stageAnchor = anchor;
            deploymentCamera = worldCamera;
            cameraController = hexCamera;
            poolScope = scenePool;
            sfxPool = audioPool;
            combatFeedback = feedback;
            deploymentText = deployment;
            statusText = status;
            castleInfoText = info;
            unitButtons = buttons ?? Array.Empty<Button>();
            unitButtonLabels = labels ?? Array.Empty<TMP_Text>();
            unitAiTagButtons = aiTagButtons ?? Array.Empty<Button>();
            unitAiTagLabels = aiTagLabels ?? Array.Empty<TMP_Text>();
            aiDescriptionPanel = descriptionPanel;
            aiDescriptionText = descriptionText;
            difficultyButtons = generationDifficultyButtons ?? Array.Empty<Button>();
            regenerateCastleButton = regenerate;
            rotateCameraLeftButton = rotateLeft;
            rotateCameraRightButton = rotateRight;
            exitButton = exit;
            inputSurface = surface;
            difficultyLevel = Mathf.Clamp(targetDifficultyLevel, 1, 10);
            generationSeed = targetGenerationSeed;
        }
#endif
    }
}
