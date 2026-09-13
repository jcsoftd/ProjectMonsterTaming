using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectMT.Contents.Framework;
using ProjectMT.Core.SceneFlow;
using ProjectMT.Shared.GameData;
using ProjectMT.Shared.Unit;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace ProjectMT.Contents.CastleRaidHex.PlayMode.Tests
{
    public sealed class HexCastleProductionPlayModeTests
    {
        [UnityTest]
        public IEnumerator ProductionScene_InitializesProceduralDifficultyStageWithSharedPartyContract()
        {
            var load = SceneManager.LoadSceneAsync("03_CastleRaidHex", LoadSceneMode.Additive);
            while (load != null && !load.isDone)
            {
                yield return null;
            }

            var sceneRoot = Object.FindFirstObjectByType<HexCastleRaidSceneRoot>();
            var controller = Object.FindFirstObjectByType<HexCastleRaidController>();
            var definition = ScriptableObject.CreateInstance<ContentDefinition>();
            definition.EditorConfigure(
                new ContentId("castle_raid"),
                ContentOpenMode.SeparateScene,
                new SceneId("castle_raid_hex"),
                null,
                null);
            var monsterCatalog = AssetDatabase.LoadAssetAtPath<MonsterCatalog>(
                "Assets/ProjectMT/02_Shared/Unit/Data/MonsterCatalog.asset");
            var rarityCatalog = AssetDatabase.LoadAssetAtPath<MonsterRarityCatalog>(
                "Assets/ProjectMT/02_Shared/Unit/Data/MonsterRarityCatalog.asset");
            Assert.That(monsterCatalog, Is.Not.Null);
            Assert.That(rarityCatalog, Is.Not.Null);
            var party = new BattlePartySnapshotBuilder(
                monsterCatalog,
                rarityCatalog,
                ProjectMT.Shared.Stats.CombatStatConfig.RuntimeDefault).Build(
                new GameProgressView(GameProgressData.CreateDefault()));
            var exit = new RecordingExit();
            var runInfo = new ContentRunInfo(
                new ContentId("castle_raid"),
                "seed",
                ContentRunMode.SeedTest);
            var contentContext = new ContentContext(
                runInfo,
                new TestStartData(party),
                exit);

            try
            {
                Assert.That(sceneRoot, Is.Not.Null);
                Assert.That(controller, Is.Not.Null);
                Assert.That(runInfo.VariantId.IsValid, Is.False, "현행 Hex 단일 진입은 Variant를 사용하지 않음");
                sceneRoot.Initialize(new ContentSceneContext(definition, contentContext));
                yield return null;

                Assert.That(sceneRoot.IsInitialized, Is.True);
                Assert.That(controller.IsRunning, Is.True);
                Assert.That(controller.ActiveStage, Is.Not.Null);
                Assert.That(controller.ActiveStage.IsComplete, Is.True, "절차 Stage 완성 상태");
                var boardRenderer = controller.ActiveStage.transform.Find("00_BoardSurface")
                    ?.GetComponent<MeshRenderer>();
                Assert.That(boardRenderer, Is.Not.Null, "절차 보드 Renderer");
                Assert.That(boardRenderer.enabled, Is.False, "정식 배경 지형을 가리지 않는 숨김 보드");
                Assert.That(boardRenderer.sharedMaterial.shader.name,
                    Is.EqualTo("ProjectMT/CastleRaidHex/GroundShadows"));
                Assert.That(boardRenderer.sharedMaterial.HasProperty("_ShadowOpacity"), Is.True);
                Assert.That(boardRenderer.sharedMaterial.GetFloat("_ShadowOpacity"), Is.InRange(0f, 1f));
                var initialDeploymentAreaVisual = controller.DeploymentAreaVisual;
                Assert.That(initialDeploymentAreaVisual, Is.Not.Null, "육각 배치 가능 영역 표시");
                Assert.That(initialDeploymentAreaVisual.AllowedCellCount,
                    Is.EqualTo(controller.ActiveStage
                        .GetComponentsInChildren<HexCastleCellRuntime>(true)
                        .Count(value => value.Kind == HexCastleCellKind.Deployment && !value.InitialBlocked)));
                Assert.That(initialDeploymentAreaVisual.IsVisible, Is.False, "몬스터 선택 전에는 숨김");
                Assert.That(controller.RemainingDeploymentCount, Is.EqualTo(party.Units.Length * 2));
                Assert.That(Camera.main, Is.Not.Null);
                Assert.That(Camera.main.orthographic, Is.False);
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(4));
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(3));
                var initialProfile = HexCastleDifficultyProfile.Resolve(
                    controller.CurrentDifficultyLevel,
                    controller.CurrentSeed);
                Assert.That(controller.ActiveTrapWorld, Is.Not.Null);
                Assert.That(controller.ActiveTrapWorld.TrapCount, Is.EqualTo(initialProfile.TotalTrapCount));
                var trapRoot = controller.ActiveStage.transform.Find("01_Traps");
                Assert.That(trapRoot, Is.Not.Null);
                Assert.That(trapRoot.GetComponentsInChildren<HexCastleTrapRuntime>(true).Length,
                    Is.EqualTo(initialProfile.TotalTrapCount));
                Assert.That(trapRoot.GetComponentsInChildren<Collider>(true), Is.Empty);
                var trapLayout = new HexCastleGenerationPipeline().GenerateFoundationForDifficulty(
                    controller.CurrentSeed,
                    controller.CurrentDifficultyLevel,
                    controller.ActiveStage.Theme).Layout;
                var trapTestStart = trapLayout.Enumerate(HexCastleCellKind.Deployment)
                    .OrderBy(value => value.Coordinates)
                    .First()
                    .Coordinates;
                var trapRuntimes = trapRoot.GetComponentsInChildren<HexCastleTrapRuntime>(true);
                foreach (var trapType in new[]
                         {
                             HexCastleTrapType.Snare,
                             HexCastleTrapType.SpikePlate,
                             HexCastleTrapType.BlastMine
                         })
                {
                    var trap = trapRuntimes.First(value => value.TrapType == trapType);
                    var route = new HexRoutePlanner().FindMinimumBreachRoute(
                        trapLayout,
                        trapTestStart);
                    var testUnitObject = new GameObject($"TrapEffectTest_{trapType}");
                    var testUnit = testUnitObject.AddComponent<HexCastleAssaultUnit>();
                    testUnit.ConfigureForRoute(
                        route,
                        HexSpatialContract.CellOuterRadius,
                        4f,
                        10f,
                        1f,
                        1000f);
                    var previousHealth = testUnit.CurrentHealth;

                    Assert.That(controller.ActiveTrapWorld.TryTriggerAt(testUnit, trap.Coordinates), Is.True,
                        $"{trapType} 최초 발동");
                    var trapLabel = Object.FindObjectsByType<HexCastleTrapFloatingLabel>(
                            FindObjectsSortMode.None)
                        .Single(value => value.TrapType == trapType);
                    Assert.That(trapLabel.DisplayText,
                        Is.EqualTo(HexCastleTrapFloatingLabel.ResolveDisplayText(trapType)),
                        $"{trapType} 발동 종류 플로팅");
                    if (trapType == HexCastleTrapType.BlastMine)
                    {
                        Assert.That(trap.IsWarning, Is.True, "폭발 지뢰 치지직 예고 시작");
                        Assert.That(testUnit.CurrentHealth, Is.EqualTo(previousHealth));
                        Assert.That(testUnit.TrapMovementLockRemaining, Is.Zero);
                        Assert.That(trap.RemainingCharges, Is.Zero);
                        Assert.That(trap.transform.Find("Crackle_00"), Is.Not.Null);
                        Assert.That(trap.transform.Find("ExplosionRing"), Is.Not.Null);
                        yield return new WaitForSecondsRealtime(trap.Balance.TriggerDelaySeconds * 0.5f);
                        Assert.That(testUnit.CurrentHealth, Is.EqualTo(previousHealth));
                        Assert.That(trap.IsWarning, Is.True, "폭발 지뢰 예고 중간 유지");
                        yield return new WaitForSecondsRealtime(
                            trap.Balance.TriggerDelaySeconds * 0.6f + 0.08f);
                        Assert.That(testUnit.CurrentHealth, Is.LessThan(previousHealth));
                        Assert.That(testUnit.TrapMovementLockRemaining, Is.GreaterThan(0f));
                        Assert.That(trap.IsWarning, Is.False);
                    }
                    else if (trapType == HexCastleTrapType.SpikePlate)
                    {
                        Assert.That(testUnit.CurrentHealth, Is.LessThan(previousHealth));
                        Assert.That(testUnit.CurrentMoveSpeedMultiplier, Is.LessThan(1f));
                        Assert.That(trap.RemainingCharges, Is.EqualTo(2));
                        Assert.That(controller.ActiveTrapWorld.TryTriggerAt(testUnit, trap.Coordinates), Is.False);
                    }
                    else
                    {
                        Assert.That(testUnit.CurrentHealth, Is.LessThan(previousHealth));
                        Assert.That(testUnit.TrapMovementLockRemaining, Is.GreaterThan(0f));
                        Assert.That(trap.RemainingCharges, Is.Zero);
                    }

                    if (trapType == HexCastleTrapType.BlastMine)
                    {
                        Assert.That(trap.UsesImportedVisual, Is.False);
                    }
                    else
                    {
                        Assert.That(trap.UsesImportedVisual, Is.True, $"{trapType} 임포트 모델 연결");
                        Assert.That(trap.VisualVariantId, Is.Not.Empty);
                        Assert.That(trap.GetComponentInChildren<Animator>(true), Is.Not.Null);
                        Assert.That(trap.GetComponentsInChildren<Renderer>(true).All(value =>
                            value.sharedMaterial != null &&
                            value.sharedMaterial.shader != null &&
                            value.sharedMaterial.shader.name == "Universal Render Pipeline/Lit"), Is.True,
                            $"{trapType} URP Material Override");
                    }

                    Object.Destroy(testUnitObject);
                    yield return null;
                }
                Assert.That(controller.ActiveStage
                    .GetComponentsInChildren<HexCastleGarrisonUnit>(true).Count(value => value.IsAlive),
                    Is.EqualTo(initialProfile.InitialKnightCount + initialProfile.InitialFarmerCount));
                var cameraController = Camera.main.GetComponent<HexCastleCameraController>();
                var inputSurface = Object.FindFirstObjectByType<HexCastleDeploymentInputSurface>();
                Assert.That(inputSurface, Is.Not.Null);
                Assert.That(cameraController.UsesExternalPointerInput, Is.True);
                Assert.That(cameraController.InitialZoomRatio, Is.EqualTo(0.70f).Within(0.001f));

                var battleHud = controller.BattleHudView;
                Assert.That(battleHud, Is.Not.Null);
                var aiTagButtons = battleHud.GetComponentsInChildren<Button>(true)
                    .Where(value => value.name == "AITag" && value.gameObject.activeInHierarchy)
                    .OrderBy(value => value.transform.parent.name)
                    .ToArray();
                Assert.That(aiTagButtons.Length, Is.EqualTo(party.Units.Length));
                Assert.That(aiTagButtons.All(value =>
                    value.GetComponentInChildren<TMP_Text>().text != "AI"), Is.True);
                aiTagButtons[0].onClick.Invoke();
                yield return null;
                var aiDescription = battleHud.GetComponentsInChildren<TMP_Text>(true)
                    .Single(value => value.name == "DescriptionText" &&
                                     value.transform.parent.name == "AIProfileDescriptionPanel");
                Assert.That(aiDescription.gameObject.activeInHierarchy, Is.True);
                Assert.That(aiDescription.text, Does.Contain("·"));

                var liveBarracks = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleBarracksRuntime>(true);
                Assert.That(liveBarracks.Any(value =>
                    value.UnitRole == HexCastleGarrisonUnitRole.Knight &&
                    value.IsProducing &&
                    value.RemainingProductionSeconds > 7.5f &&
                    value.RemainingProductionSeconds <= 10f), Is.True,
                    "폭발 지뢰 예고 검증 중에도 기사 병영 생산은 계속 진행돼야 합니다.");

                var liveBallistas = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleTurretRuntime>(true)
                    .Where(value => value.Profile != null &&
                                    value.Profile.WeaponKind == HexCastleTurretWeaponKind.Ballista)
                    .ToArray();
                Assert.That(liveBallistas, Is.Not.Empty);
                Assert.That(liveBallistas.All(value =>
                    value.Profile.Level <= 2 &&
                    value.Profile.Data.impactType == HexCastleTurretImpactType.Direct &&
                    value.Profile.Data.projectileCount == 1 &&
                    value.Profile.Data.pierceCount == 1 &&
                    value.Profile.Data.piercingDamageRatio == 0f), Is.True);
                foreach (var liveBallista in liveBallistas)
                {
                    var visual = liveBallista.GetComponent<HexCastleTurretVisual>();
                    var sourceHead = AssetDatabase.LoadAssetAtPath<GameObject>(
                        $"Assets/ProjectMT/04_Contents/01_CastleRaid/HexVariant/Prefabs/TurretHeads/" +
                        $"PF_CR_TurretHead_Ballista_Lv{liveBallista.Profile.Level}.prefab");
                    var sourceModel = sourceHead.transform.Find("Joint_BodyMount/YawPivot/PitchPivot/Model");
                    var liveModel = visual.PitchPivot.Find("Model");
                    Assert.That(Quaternion.Angle(liveModel.localRotation, sourceModel.localRotation),
                        Is.LessThan(0.01f), "절차 조립이 발리스타 원본 모델 축을 덮어쓰면 안 됩니다.");
                }

                var generationButtons = battleHud.GetComponentsInChildren<Button>(true)
                    .Where(value => Enumerable.Range(1, 10)
                                        .Any(level => value.name == $"Difficulty{level}Button") ||
                                    value.name == "RegenerateCastleButton")
                    .ToDictionary(value => value.name);
                Assert.That(generationButtons.Count, Is.EqualTo(11));
                Assert.That(generationButtons.Values.All(value =>
                    value.gameObject.activeInHierarchy && value.interactable), Is.True);

                var initialTheme = controller.CurrentTheme;
                generationButtons["Difficulty1Button"].onClick.Invoke();
                yield return null;
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(1));
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(2));
                Assert.That(controller.CurrentTheme, Is.Not.EqualTo(initialTheme));
                var firstDoubleWallSeed = controller.CurrentSeed;
                var firstDoubleWallTheme = controller.CurrentTheme;
                generationButtons["Difficulty1Button"].onClick.Invoke();
                yield return null;
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(1));
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(2));
                Assert.That(controller.CurrentSeed, Is.Not.EqualTo(firstDoubleWallSeed));
                Assert.That(controller.CurrentTheme, Is.Not.EqualTo(firstDoubleWallTheme));
                Assert.That(Object.FindObjectsByType<HexCastleProceduralStage>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(1));

                var previousTheme = controller.CurrentTheme;
                generationButtons["Difficulty4Button"].onClick.Invoke();
                yield return null;
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(4));
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(3));
                Assert.That(controller.CurrentTheme, Is.Not.EqualTo(previousTheme));
                var firstTripleWallSeed = controller.CurrentSeed;
                var firstTripleWallTheme = controller.CurrentTheme;
                generationButtons["RegenerateCastleButton"].onClick.Invoke();
                yield return null;
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(3));
                Assert.That(controller.CurrentSeed, Is.Not.EqualTo(firstTripleWallSeed));
                Assert.That(controller.CurrentTheme, Is.Not.EqualTo(firstTripleWallTheme));

                previousTheme = controller.CurrentTheme;
                generationButtons["Difficulty7Button"].onClick.Invoke();
                yield return null;
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(7));
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(4));
                Assert.That(controller.CurrentTheme, Is.Not.EqualTo(previousTheme));
                previousTheme = controller.CurrentTheme;
                generationButtons["Difficulty10Button"].onClick.Invoke();
                yield return null;
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(10));
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(4));
                Assert.That(controller.CurrentTheme, Is.Not.EqualTo(previousTheme));
                Assert.That(cameraController.RequiredShadowDistance, Is.GreaterThan(120f));
                previousTheme = controller.CurrentTheme;
                generationButtons["Difficulty4Button"].onClick.Invoke();
                yield return null;
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(4));
                Assert.That(controller.CurrentDefenseLayerCount, Is.EqualTo(3));
                Assert.That(controller.CurrentTheme, Is.Not.EqualTo(previousTheme));

                var eventSystem = EventSystem.current;
                var screenCenter = new Vector2(Camera.main.pixelWidth * 0.5f, Camera.main.pixelHeight * 0.5f);
                var scrollEvent = new PointerEventData(eventSystem)
                {
                    position = screenCenter,
                    scrollDelta = new Vector2(0f, 120f)
                };
                inputSurface.OnScroll(scrollEvent);
                Assert.That(cameraController.TargetDistance,
                    Is.EqualTo(cameraController.DefaultDistance * Mathf.Exp(-0.18f)).Within(0.001f));
                Assert.That(cameraController.TargetDistance, Is.GreaterThan(cameraController.MinimumDistance));

                cameraController.ResetView();
                var pinchStartDistance = cameraController.TargetDistance;
                var firstTouch = new PointerEventData(eventSystem)
                {
                    pointerId = 1,
                    position = screenCenter + Vector2.left * 50f
                };
                var secondTouch = new PointerEventData(eventSystem)
                {
                    pointerId = 2,
                    position = screenCenter + Vector2.right * 50f
                };
                inputSurface.OnPointerDown(firstTouch);
                inputSurface.OnPointerDown(secondTouch);
                firstTouch.position = screenCenter + Vector2.left * 100f;
                inputSurface.OnDrag(firstTouch);
                Assert.That(cameraController.TargetDistance, Is.LessThan(pinchStartDistance));
                inputSurface.OnPointerUp(firstTouch);
                inputSurface.OnPointerUp(secondTouch);
                cameraController.ResetView();

                var rotateRightButton = battleHud.GetComponentsInChildren<Button>(true)
                    .Single(value => value.name == "RotateCameraRightButton");
                var holdButton = rotateRightButton.GetComponent<HexCastleCameraHoldButton>();
                var cameraPosition = Camera.main.transform.position;
                holdButton.OnPointerDown(new PointerEventData(EventSystem.current));
                yield return new WaitForSecondsRealtime(cameraController.RotationCenteringDuration + 0.15f);
                holdButton.OnPointerUp(new PointerEventData(EventSystem.current));
                var pivotAfterRotation = ResolveGroundCenter(
                    Camera.main,
                    cameraController.VerticalScreenOffset);
                Assert.That(cameraController.YawDegrees, Is.GreaterThan(2f));
                Assert.That(Camera.main.transform.position, Is.Not.EqualTo(cameraPosition));
                Assert.That(pivotAfterRotation.x,
                    Is.EqualTo(cameraController.RotationFocusGroundCenter.x).Within(0.02f));
                Assert.That(pivotAfterRotation.y,
                    Is.EqualTo(cameraController.RotationFocusGroundCenter.y).Within(0.02f));
                var stoppedYaw = cameraController.YawDegrees;
                yield return new WaitForSecondsRealtime(0.10f);
                Assert.That(cameraController.YawDegrees, Is.EqualTo(stoppedYaw).Within(0.01f));
                Assert.That(party.Units.All(value =>
                    value.RuntimeAssetSet != null && value.RuntimeAssetSet.VisualAdapterPrefab != null), Is.True);

                var deploymentCell = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .First(value => value.Kind == HexCastleCellKind.Deployment && !value.InitialBlocked);
                var deploymentAreaVisual = controller.DeploymentAreaVisual;
                Assert.That(deploymentAreaVisual, Is.Not.Null, "재생성된 현재 Stage 배치 표시");
                Assert.That(controller.TrySelectUnit(0), Is.True);
                Assert.That(deploymentAreaVisual.IsVisible, Is.True, "몬스터 선택 후 배치 가능 셀 표시");
                var deploymentRenderer = deploymentAreaVisual.transform
                    .Find("02_DeploymentAreaVisual")
                    ?.GetComponent<MeshRenderer>();
                Assert.That(deploymentRenderer, Is.Not.Null);
                Assert.That(deploymentRenderer.sharedMaterials.Length, Is.EqualTo(2));
                Assert.That(controller.TryDeployAtCell(deploymentCell.Coordinates), Is.True);
                yield return null;
                Assert.That(deploymentAreaVisual.IsVisible, Is.True, "동일 몬스터 잔여 수량이 있으면 표시 유지");

                var assault = Object.FindFirstObjectByType<HexCastleAssaultUnit>();
                Assert.That(assault, Is.Not.Null);
                Assert.That(assault.UsesFormalVisual, Is.True);
                Assert.That(assault.HasFormalAnimation, Is.True);
                Assert.That(controller.ActiveAssaultUnitCount, Is.EqualTo(1));
                Assert.That(controller.ActiveAssaultWorld, Is.Not.Null);
                Assert.That(controller.ActiveAssaultWorld.ActiveUnitCount, Is.EqualTo(1));
                Assert.That(assault.AIProfile, Is.Not.Null);
                assault.RefreshStrategicDecision();
                Assert.That(assault.ExpectedDefenseLayer, Is.EqualTo(3));
                Assert.That(assault.CurrentTarget.IsValid, Is.True);
                Assert.That(assault.CurrentTarget.Structure, Is.Not.Null);
                Assert.That(assault.CurrentTarget.Structure.DefenseLayer, Is.EqualTo(3));
                Assert.That(controller.ActiveAssaultWorld.SharedFrontRouteBuildCount, Is.GreaterThan(0),
                    "정식 배치도 부대 대표 전면 경로를 생성해야 한다.");
                Assert.That(controller.ActiveAssaultWorld.ApproachPlanner.SharedSearchBuildCount, Is.GreaterThan(0),
                    "정식 병력의 실제 접근 탐색이 부대 공유 경로를 사용해야 한다.");

                var wall = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .First(value => value.Kind == HexCastleCellKind.Wall && value.IsAlive);
                Assert.That(wall.ApplyDamage(25f, wall.transform.position), Is.True);
                yield return null;
                Assert.That(wall.TryGetComponent<HexCastleOverheadHealthBar>(out var wallHealthBar), Is.True);
                Assert.That(wallHealthBar.IsVisible, Is.True);
                Assert.That(wallHealthBar.FillColor, Is.EqualTo(HexCastleOverheadHealthBar.HostileColor));
                var floatingNumbers = Object.FindFirstObjectByType<ProjectMT.Shared.Combat.FloatingNumberPresenter>();
                Assert.That(floatingNumbers, Is.Not.Null);
                Assert.That(floatingNumbers.PendingNumberCount, Is.GreaterThan(0));
                Assert.That(wall.ApplyDamage(100000f, wall.transform.position), Is.True);
                Assert.That(Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None).Any(source =>
                    source.isPlaying && source.clip != null &&
                    source.clip.name == "rock_smashable_hit_impact_01"), Is.True);
                yield return new WaitForSecondsRealtime(0.10f);
                Assert.That(floatingNumbers.ActiveNumberCount, Is.GreaterThan(0));

                Assert.That(assault.ApplyDamage(10f, assault.transform.position), Is.True);
                Assert.That(floatingNumbers.PendingNumberCount, Is.GreaterThan(0));
                yield return null;
                Assert.That(assault.TryGetComponent<HexCastleOverheadHealthBar>(out var assaultHealthBar), Is.True);
                Assert.That(assaultHealthBar.IsVisible, Is.True);
                Assert.That(assaultHealthBar.FillColor, Is.EqualTo(HexCastleOverheadHealthBar.FriendlyColor));

                var building = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .First(value => value.IsAlive &&
                                    (value.Kind == HexCastleCellKind.Building ||
                                     value.Kind == HexCastleCellKind.RewardBuilding ||
                                     value.Kind == HexCastleCellKind.DefenseBuilding));
                Assert.That(building.ApplyDamage(
                    Mathf.Min(25f, building.MaxHealth * 0.25f),
                    building.transform.position), Is.True);
                Assert.That(floatingNumbers.PendingNumberCount, Is.GreaterThan(0));
                yield return null;
                Assert.That(building.TryGetComponent<HexCastleOverheadHealthBar>(out var buildingHealthBar), Is.True);
                Assert.That(buildingHealthBar.IsVisible, Is.True);
                Assert.That(buildingHealthBar.FillColor, Is.EqualTo(HexCastleOverheadHealthBar.HostileColor));

                var garrisonWorld = controller.ActiveStage.GetComponent<HexCastleGarrisonWorld>();
                var barracksCell = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .First(value => value.BuildingRole == HexCastleBuildingRole.KnightBarracks);
                Assert.That(garrisonWorld, Is.Not.Null);
                Assert.That(garrisonWorld.Spawn(
                    HexCastleGarrisonUnitRole.Knight,
                    barracksCell.Coordinates,
                    1), Is.EqualTo(1));
                yield return null;
                var garrison = garrisonWorld.Units.Last(value => value != null && value.IsAlive);
                Assert.That(garrison.ApplyDamage(10f, garrison.transform.position), Is.True);
                Assert.That(floatingNumbers.PendingNumberCount, Is.GreaterThan(0));
                yield return null;
                Assert.That(garrison.TryGetComponent<HexCastleOverheadHealthBar>(out var garrisonHealthBar), Is.True);
                Assert.That(garrisonHealthBar.IsVisible, Is.True);
                Assert.That(garrisonHealthBar.FillColor, Is.EqualTo(HexCastleOverheadHealthBar.HostileColor));
                Assert.That(floatingNumbers.ActiveNumberCount + floatingNumbers.PendingNumberCount,
                    Is.GreaterThan(0), "건물·수비대·아군 피해도 실제 데미지 숫자 큐로 들어가야 합니다.");

                var palace = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .Single(value => value.Coordinates == new HexCoordinates(0, 0));
                palace.ApplyDamage(100000f, palace.transform.position);
                Assert.That(Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None).Any(source =>
                    source.isPlaying && source.clip != null &&
                    source.clip.name == "rock_avalanche_landslide_debris_01"), Is.True);
                yield return null;
                Assert.That(exit.CompletedResult, Is.TypeOf<HexCastleRaidResult>());
                Assert.That(((HexCastleRaidResult)exit.CompletedResult).PalaceDestroyed, Is.True);
                Assert.That(controller.IsRunning, Is.False);
            }
            finally
            {
                if (sceneRoot != null)
                {
                    sceneRoot.Shutdown();
                }

                Object.Destroy(definition);
            }

            var productionScene = SceneManager.GetSceneByName("03_CastleRaidHex");
            if (productionScene.IsValid() && productionScene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(productionScene);
                while (unload != null && !unload.isDone)
                {
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator ProgressionStage_UsesSelectedStageRulesAndHidesDevGenerationControls()
        {
            const int stage = CastleRaidStageRules.MaximumStage;
            var load = SceneManager.LoadSceneAsync("03_CastleRaidHex", LoadSceneMode.Additive);
            while (load != null && !load.isDone)
            {
                yield return null;
            }

            var productionScene = SceneManager.GetSceneByName("03_CastleRaidHex");
            var sceneRoot = Object.FindFirstObjectByType<HexCastleRaidSceneRoot>();
            var controller = Object.FindFirstObjectByType<HexCastleRaidController>();
            var definition = ScriptableObject.CreateInstance<ContentDefinition>();
            definition.EditorConfigure(
                new ContentId("castle_raid"),
                ContentOpenMode.SeparateScene,
                new SceneId("castle_raid_hex"),
                null,
                null);
            var monsterCatalog = AssetDatabase.LoadAssetAtPath<MonsterCatalog>(
                "Assets/ProjectMT/02_Shared/Unit/Data/MonsterCatalog.asset");
            var rarityCatalog = AssetDatabase.LoadAssetAtPath<MonsterRarityCatalog>(
                "Assets/ProjectMT/02_Shared/Unit/Data/MonsterRarityCatalog.asset");
            Assert.That(monsterCatalog, Is.Not.Null);
            Assert.That(rarityCatalog, Is.Not.Null);
            var party = new BattlePartySnapshotBuilder(
                monsterCatalog,
                rarityCatalog,
                ProjectMT.Shared.Stats.CombatStatConfig.RuntimeDefault).Build(
                new GameProgressView(GameProgressData.CreateDefault()));
            var exit = new RecordingExit();
            var runInfo = new ContentRunInfo(
                new ContentId("castle_raid"),
                stage.ToString(),
                ContentRunMode.Challenge);

            try
            {
                Assert.That(sceneRoot, Is.Not.Null);
                Assert.That(controller, Is.Not.Null);
                sceneRoot.Initialize(new ContentSceneContext(
                    definition,
                    new ContentContext(runInfo, new TestStartData(party), exit)));
                yield return null;

                Assert.That(controller.IsRunning, Is.True);
                Assert.That(controller.CurrentDifficultyLevel,
                    Is.EqualTo(CastleRaidStageRules.ResolveDifficulty(stage)));
                Assert.That(controller.CurrentSeed,
                    Is.EqualTo(CastleRaidStageRules.ResolveGenerationSeed(stage)));
                Assert.That(controller.CurrentTheme,
                    Is.EqualTo(HexCastleThemeCatalog.Themes[(stage - 1) % HexCastleThemeCatalog.Themes.Count]));
                Assert.That(controller.ActiveStage, Is.Not.Null);
                Assert.That(controller.ActiveStage.IsComplete, Is.True);

                var productionHud = productionScene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Canvas>(true))
                    .Select(value => value.gameObject)
                    .Single(value =>
                        value.activeInHierarchy &&
                        value.name.StartsWith("PF_CastleRaidHexHUD", System.StringComparison.Ordinal));
                var sceneTransforms = productionHud.GetComponentsInChildren<Transform>(true);
                var generationControls = sceneTransforms.Single(value => value.name == "GenerationControls");
                Assert.That(generationControls.gameObject.activeSelf, Is.False,
                    "정식 진행형 입장에서는 DEV 성 생성 컨트롤이 보여서는 안 됩니다.");
                var castleInfo = sceneTransforms.Single(value => value.name == "CastleInfoText")
                    .GetComponent<TMP_Text>();
                Assert.That(castleInfo, Is.Not.Null);
                Assert.That(castleInfo.text, Does.Contain("100단계"));
                Assert.That(castleInfo.text, Does.Contain("난이도 10"));
            }
            finally
            {
                sceneRoot?.Shutdown();
                Object.Destroy(definition);
            }

            if (productionScene.IsValid() && productionScene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(productionScene);
                while (unload != null && !unload.isDone)
                {
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator TimeoutRetry_PreservesRunAndDiscardsDestroyedBuildingLoot()
        {
            const int stage = 37;
            var originalTimeScale = Time.timeScale;
            var load = SceneManager.LoadSceneAsync("03_CastleRaidHex", LoadSceneMode.Additive);
            while (load != null && !load.isDone)
            {
                yield return null;
            }

            var productionScene = SceneManager.GetSceneByName("03_CastleRaidHex");
            var sceneRoot = Object.FindFirstObjectByType<HexCastleRaidSceneRoot>();
            var controller = Object.FindFirstObjectByType<HexCastleRaidController>();
            var definition = ScriptableObject.CreateInstance<ContentDefinition>();
            definition.EditorConfigure(
                new ContentId("castle_raid"),
                ContentOpenMode.SeparateScene,
                new SceneId("castle_raid_hex"),
                null,
                null);
            var monsterCatalog = AssetDatabase.LoadAssetAtPath<MonsterCatalog>(
                "Assets/ProjectMT/02_Shared/Unit/Data/MonsterCatalog.asset");
            var rarityCatalog = AssetDatabase.LoadAssetAtPath<MonsterRarityCatalog>(
                "Assets/ProjectMT/02_Shared/Unit/Data/MonsterRarityCatalog.asset");
            Assert.That(monsterCatalog, Is.Not.Null);
            Assert.That(rarityCatalog, Is.Not.Null);
            var party = new BattlePartySnapshotBuilder(
                monsterCatalog,
                rarityCatalog,
                ProjectMT.Shared.Stats.CombatStatConfig.RuntimeDefault).Build(
                new GameProgressView(GameProgressData.CreateDefault()));
            var startData = new HexCastleRaidStartData(party);
            var progress = new InMemoryGameProgressService();
            var exit = new RecordingExit();
            var runInfo = new ContentRunInfo(
                new ContentId("castle_raid"),
                stage.ToString(),
                ContentRunMode.Challenge);

            try
            {
                Assert.That(sceneRoot, Is.Not.Null);
                Assert.That(controller, Is.Not.Null);
                sceneRoot.Initialize(new ContentSceneContext(
                    definition,
                    new ContentContext(runInfo, startData, exit, progress)));
                yield return null;

                var initialSeed = controller.CurrentSeed;
                var initialDifficulty = controller.CurrentDifficultyLevel;
                var initialStageId = controller.CurrentStageId;
                var rewardCell = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .First(value => value.Kind == HexCastleCellKind.RewardBuilding &&
                                    value.LootKind == HexCastleLootKind.Equipment);
                rewardCell.ApplyDamage(100000f, rewardCell.transform.position);
                yield return null;

                var deploymentCell = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .First(value => value.Kind == HexCastleCellKind.Deployment && !value.InitialBlocked);
                Assert.That(controller.TrySelectUnit(0), Is.True);
                Assert.That(controller.TryDeployAtCell(deploymentCell.Coordinates), Is.True);
                Assert.That(controller.BattleStarted, Is.True);
                Assert.That(controller.RemainingBattleSeconds,
                    Is.LessThanOrEqualTo(HexCastleRaidController.BattleDurationSeconds));

                var remainingField = typeof(HexCastleRaidController).GetField(
                    "remainingBattleSeconds",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(remainingField, Is.Not.Null);
                remainingField.SetValue(controller, 0.001f);
                yield return null;

                Assert.That(controller.IsFailureVisible, Is.True);
                Assert.That(controller.IsRunning, Is.False);
                Assert.That(Time.timeScale, Is.EqualTo(0f));
                Assert.That(exit.CompletedResult, Is.Null);
                Assert.That(exit.FailedResult, Is.Null);
                Assert.That(exit.Cancelled, Is.False);
                var battleHud = controller.BattleHudView;
                Assert.That(battleHud, Is.Not.Null);
                Assert.That(battleHud.IsFailurePanelVisible, Is.True);
                Assert.That(battleHud.DisplayedFailureReason, Is.EqualTo("제한 시간 초과"));
                Assert.That(battleHud.DisplayedTimer, Is.EqualTo("00:00"));

                controller.RetrySameRun();
                yield return null;

                Assert.That(controller.IsRunning, Is.True);
                Assert.That(controller.IsFailureVisible, Is.False);
                Assert.That(battleHud.IsFailurePanelVisible, Is.False);
                Assert.That(controller.BattleStarted, Is.False);
                Assert.That(controller.RemainingBattleSeconds,
                    Is.EqualTo(HexCastleRaidController.BattleDurationSeconds).Within(0.01f));
                Assert.That(controller.CurrentStageId, Is.EqualTo(initialStageId));
                Assert.That(controller.CurrentDifficultyLevel, Is.EqualTo(initialDifficulty));
                Assert.That(controller.CurrentSeed, Is.EqualTo(initialSeed));
                Assert.That(controller.RemainingDeploymentCount, Is.EqualTo(startData.DeploymentLimit));
                Assert.That(Time.timeScale, Is.EqualTo(originalTimeScale));

                var palace = controller.ActiveStage
                    .GetComponentsInChildren<HexCastleCellRuntime>(true)
                    .Single(value => value.Coordinates == new HexCoordinates(0, 0));
                palace.ApplyDamage(100000f, palace.transform.position);
                yield return null;

                Assert.That(exit.CompletedResult, Is.TypeOf<HexCastleRaidResult>());
                var result = (HexCastleRaidResult)exit.CompletedResult;
                Assert.That(result.PalaceDestroyed, Is.True);
                Assert.That(result.LootRewards.IsEmpty, Is.True,
                    "무료 재도전 전 파괴한 건물 보상은 새 Run으로 이월되면 안 됩니다.");
                Assert.That(result.EquipmentRewards, Is.Empty,
                    "무료 재도전 전 파괴한 장비 건물 보상은 새 Run으로 이월되면 안 됩니다.");
            }
            finally
            {
                sceneRoot?.Shutdown();
                Time.timeScale = originalTimeScale;
                Object.Destroy(definition);
            }

            if (productionScene.IsValid() && productionScene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(productionScene);
                while (unload != null && !unload.isDone)
                {
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator KnightConfigure_InitializesFeelJumpFeedbackInPlayMode()
        {
            var groundRoot = new GameObject("GroundCell");
            var unitRoot = new GameObject("Knight");
            try
            {
                var coordinates = new HexCoordinates(0, 0);
                var runtime = groundRoot.AddComponent<HexCastleCellRuntime>();
                var tile = new GameObject("TileVisualRoot").transform;
                tile.SetParent(groundRoot.transform, false);
                var content = new GameObject("ContentVisualRoot").transform;
                content.SetParent(groundRoot.transform, false);
                runtime.Configure(
                    new HexCastleCell(coordinates, HexCastleCellKind.Ground, initialBlocked: false),
                    null,
                    null,
                    tile,
                    content);

                var visual = new GameObject("Visual").transform;
                visual.SetParent(unitRoot.transform, false);
                var unit = unitRoot.AddComponent<HexCastleGarrisonUnit>();
                unit.Configure(
                    HexCastleGarrisonUnitRole.Knight,
                    coordinates,
                    0,
                    visual,
                    new System.Collections.Generic.Dictionary<HexCoordinates, HexCastleCellRuntime>
                    {
                        [coordinates] = runtime
                    },
                    null,
                    Vector3.zero,
                    1f,
                    HexCastleThemeOneTuning.CreateDraftDefaults());
                unit.enabled = false;
                yield return null;

                Assert.That(unit.IsConfigured, Is.True);
                Assert.That(unit.GetComponents<MonoBehaviour>().Any(value =>
                    value != null && value.GetType().FullName == "MoreMountains.Feedbacks.MMF_Player"), Is.True);
            }
            finally
            {
                Object.Destroy(unitRoot);
                Object.Destroy(groundRoot);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator TacticalSupport_HealsThenResumesAdvanceDuringCooldown()
        {
            var owned = new List<GameObject>();
            var cells = new Dictionary<HexCoordinates, HexCastleCellRuntime>();
            var worldRoot = new GameObject("HexAssaultWorld");
            owned.Add(worldRoot);
            try
            {
                foreach (var coordinates in HexCoordinates.EnumerateRadius(4))
                {
                    var cellRoot = new GameObject($"Cell_{coordinates.Q}_{coordinates.R}");
                    owned.Add(cellRoot);
                    var runtime = cellRoot.AddComponent<HexCastleCellRuntime>();
                    var tile = new GameObject("TileVisualRoot").transform;
                    tile.SetParent(cellRoot.transform, false);
                    var content = new GameObject("ContentVisualRoot").transform;
                    content.SetParent(cellRoot.transform, false);
                    var isPalace = coordinates.DistanceFromOrigin <= 1;
                    var cell = new HexCastleCell(
                        coordinates,
                        isPalace ? HexCastleCellKind.Palace : HexCastleCellKind.Ground,
                        hitPoints: isPalace ? 1000f : 0f,
                        initialBlocked: isPalace,
                        placementId: isPalace ? $"PALACE_{coordinates.Q}_{coordinates.R}" : null);
                    if (isPalace)
                    {
                        var health = cellRoot.AddComponent<HealthComponent>();
                        var collider = cellRoot.AddComponent<BoxCollider>();
                        runtime.Configure(cell, health, collider, tile, content);
                    }
                    else
                    {
                        runtime.Configure(cell, null, null, tile, content);
                    }
                    cells.Add(coordinates, runtime);
                }

                var world = worldRoot.AddComponent<HexCastleAssaultWorld>();
                world.Configure(cells, 1f, 2, null, null, 42);
                var supportRoot = new GameObject("Support");
                var allyRoot = new GameObject("Ally");
                owned.Add(supportRoot);
                owned.Add(allyRoot);
                var support = supportRoot.AddComponent<HexCastleAssaultUnit>();
                var ally = allyRoot.AddComponent<HexCastleAssaultUnit>();
                support.ConfigureForPartyUnit(
                    world,
                    new HexCoordinates(4, 0),
                    cells,
                    1f,
                    Vector3.zero,
                    CreateTestUnit("floria_01"));
                ally.ConfigureForPartyUnit(
                    world,
                    new HexCoordinates(3, 0),
                    cells,
                    1f,
                    Vector3.zero,
                    CreateTestUnit("kimhyeona_01"));
                ally.ApplyDamage(50f);
                var damagedHealth = ally.CurrentHealth;
                var supportStartPosition = support.transform.position;

                yield return new WaitForSecondsRealtime(0.25f);

                Assert.That(ally.CurrentHealth, Is.GreaterThan(damagedHealth));
                Assert.That(support.CanPerformSupportAction, Is.False);
                Assert.That(support.CurrentTarget.Kind, Is.Not.EqualTo(HexCastleAssaultTargetKind.Ally));
                Assert.That(support.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.Palace));
                Assert.That(Vector3.SqrMagnitude(support.transform.position - supportStartPosition),
                    Is.GreaterThan(0.01f), "지원 쿨다운 동안에도 왕궁 방향 진격을 계속해야 합니다.");

                var rarity = AssetDatabase.LoadAssetAtPath<MonsterRarityCatalog>(
                    "Assets/ProjectMT/02_Shared/Unit/Data/MonsterRarityCatalog.asset");
                Assert.That(rarity.TryGetSkillLoadout("aru_01", out var entryShield, out _), Is.True);
                var passiveProbeRoot = new GameObject("PassiveProbe");
                owned.Add(passiveProbeRoot);
                var passiveProbe = passiveProbeRoot.AddComponent<HexCastleAssaultUnit>();
                passiveProbe.ConfigureForPartyUnit(
                    world,
                    new HexCoordinates(4, -1),
                    cells,
                    1f,
                    Vector3.zero,
                    CreateTestUnit("aru_01", entryShield, 1));
                Assert.That(passiveProbe.PassiveRuntime.ShieldAmount, Is.EqualTo(3f).Within(.01f),
                    "Hex 수동 배치는 합류 보호막 기본값의 절반을 자기 자신에게 적용해야 합니다.");
            }
            finally
            {
                for (var index = owned.Count - 1; index >= 0; index--)
                {
                    Object.Destroy(owned[index]);
                }
            }

            yield return null;
        }

        private static BattleUnitSnapshot CreateTestUnit(
            string monsterId,
            MonsterPassiveSkill passiveSkill = null,
            int level = 1)
        {
            return new BattleUnitSnapshot(
                monsterId,
                new UnitStatsSnapshot
                {
                    maxHealth = 100f,
                    damage = 20f,
                    moveSpeed = 3f,
                    attackRange = 1.1f,
                    attackInterval = 1f
                },
                passiveSkill: passiveSkill,
                level: level);
        }

        private static Vector2 ResolveGroundCenter(Camera camera, float verticalScreenOffset)
        {
            var screenCenter = new Vector2(
                camera.pixelWidth * 0.5f,
                camera.pixelHeight * (0.5f + verticalScreenOffset));
            var ray = camera.ScreenPointToRay(screenCenter);
            var plane = new Plane(Vector3.up, Vector3.zero);
            Assert.That(plane.Raycast(ray, out var distance), Is.True);
            var point = ray.GetPoint(distance);
            return new Vector2(point.x, point.z);
        }

        private sealed class TestStartData : IPartyDeploymentStartData
        {
            public TestStartData(BattlePartySnapshot party)
            {
                Party = party;
                UnitSlotCount = party.Units.Length;
                SummonsPerSlot = 2;
                DeploymentLimit = UnitSlotCount * SummonsPerSlot;
            }

            public BattlePartySnapshot Party { get; }
            public int UnitSlotCount { get; }
            public int SummonsPerSlot { get; }
            public int DeploymentLimit { get; }
        }

        private sealed class RecordingExit : IContentExit
        {
            public IContentResultData CompletedResult { get; private set; }
            public IContentResultData FailedResult { get; private set; }
            public bool Cancelled { get; private set; }

            public void Complete(IContentResultData result) => CompletedResult = result;
            public void Fail(IContentResultData result = null) => FailedResult = result;
            public void Cancel() => Cancelled = true;
        }
    }
}
