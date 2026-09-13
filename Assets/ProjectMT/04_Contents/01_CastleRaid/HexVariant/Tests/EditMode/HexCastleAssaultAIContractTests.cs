using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectMT.Shared.Unit;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Tests
{
    public sealed class HexCastleAssaultAIContractTests
    {
        private readonly List<GameObject> owned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                if (owned[index] != null)
                {
                    Object.DestroyImmediate(owned[index]);
                }
            }
            owned.Clear();
        }

        [Test]
        public void Navigation_SelectsOuterLayerBeforeInnerLayerAndStopsAtPalacePerimeter()
        {
            var cells = CreateTwoLayerBoard();
            var navigation = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            var start = HexCoordinates.Directions[0] * 7;

            Assert.That(navigation.TryResolveRoute(
                start,
                HexCastleAssaultRoutePolicy.Balanced,
                2,
                50f,
                3f,
                1,
                out var route), Is.True);

            Assert.That(route.HasFirstObstacle, Is.True);
            Assert.That(cells[route.FirstObstacle].DefenseLayer, Is.EqualTo(2));
            Assert.That(cells[route.FirstObstacle].WallRole, Is.EqualTo(HexCastleWallRole.OuterPerimeter));
            Assert.That(route.DestinationApproach.DistanceFromOrigin, Is.EqualTo(2));
            Assert.That(route.Path.All(value => value.DistanceFromOrigin > 1), Is.True);
        }

        [Test]
        public void Navigation_AfterOuterBreachTargetsExactNextInnerLayer()
        {
            var cells = CreateTwoLayerBoard();
            var start = HexCoordinates.Directions[0] * 7;
            var firstNavigation = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(firstNavigation.TryResolveRoute(
                start,
                HexCastleAssaultRoutePolicy.Balanced,
                2,
                50f,
                3f,
                1,
                out var outerRoute), Is.True);

            var outerWall = cells[outerRoute.FirstObstacle];
            Assert.That(outerWall.ApplyDamage(outerWall.MaxHealth, outerWall.transform.position), Is.True);
            var secondNavigation = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(secondNavigation.TryResolveRoute(
                start,
                HexCastleAssaultRoutePolicy.Balanced,
                1,
                50f,
                3f,
                2,
                out var innerRoute), Is.True);

            Assert.That(innerRoute.HasFirstObstacle, Is.True);
            Assert.That(cells[innerRoute.FirstObstacle].DefenseLayer, Is.EqualTo(1));
            Assert.That(innerRoute.Path, Does.Contain(outerWall.Coordinates));
        }

        [Test]
        public void Navigation_ReusesOneCostFieldUntilTopologyInvalidation()
        {
            var cells = CreateTwoLayerBoard();
            var navigation = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            var start = HexCoordinates.Directions[0] * 7;

            for (var index = 0; index < 30; index++)
            {
                Assert.That(navigation.TryResolveRoute(
                    start,
                    HexCastleAssaultRoutePolicy.Balanced,
                    2,
                    50f,
                    3f,
                    1,
                    out _), Is.True);
            }

            Assert.That(navigation.CachedFieldCount, Is.EqualTo(1));
            navigation.Invalidate();
            Assert.That(navigation.CachedFieldCount, Is.Zero);
        }

        [Test]
        public void CellRuntime_PreservesRingAndPartitionWallRolesForAI()
        {
            var ring = CreateRuntime(new HexCastleCell(
                new HexCoordinates(3, 0),
                HexCastleCellKind.Wall,
                defenseLayer: 1,
                hitPoints: 100f,
                wallRole: HexCastleWallRole.CoreDefense,
                initialBlocked: true));
            var partition = CreateRuntime(new HexCastleCell(
                new HexCoordinates(2, 0),
                HexCastleCellKind.Gate,
                defenseLayer: 1,
                hitPoints: 80f,
                wallRole: HexCastleWallRole.Partition,
                initialBlocked: true));

            Assert.That(ring.WallRole, Is.EqualTo(HexCastleWallRole.CoreDefense));
            Assert.That(partition.WallRole, Is.EqualTo(HexCastleWallRole.Partition));
        }

        [Test]
        public void ProfileCatalog_ContainsCurrentHexMonsterPolicyValues()
        {
            var catalog = Resources.Load<HexCastleAssaultAIProfileCatalog>(
                HexCastleAssaultAIProfileCatalog.DefaultResourcesPath);

            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.TryValidate(out var error), Is.True, error);
            Assert.That(catalog.Entries.Count, Is.EqualTo(44));
            Assert.That(catalog.Resolve("aru_01").Pattern, Is.EqualTo(HexCastleAssaultPattern.ThreatSuppressor));
            Assert.That(catalog.Resolve("aru_01").SupportFocus, Is.EqualTo(HexCastleAssaultSupportFocus.Adaptive));
            Assert.That(catalog.Resolve("chamchi_01").Pattern, Is.EqualTo(HexCastleAssaultPattern.DefenderHunter));
            Assert.That(catalog.Resolve("castley_01").Pattern, Is.EqualTo(HexCastleAssaultPattern.WallBreaker));
            Assert.That(catalog.Resolve("floria_01").Pattern, Is.EqualTo(HexCastleAssaultPattern.TacticalSupport));
        }

        [Test]
        public void AIPresentation_ExplainsEveryAssignedPatternAndSupportFocus()
        {
            var catalog = Resources.Load<HexCastleAssaultAIProfileCatalog>(
                HexCastleAssaultAIProfileCatalog.DefaultResourcesPath);

            Assert.That(catalog, Is.Not.Null);
            foreach (var profile in catalog.Entries)
            {
                Assert.That(HexCastleAssaultAIPresentation.ResolveTag(profile), Is.Not.Empty);
                Assert.That(HexCastleAssaultAIPresentation.ResolveDescription(profile), Is.Not.Empty);
            }
            Assert.That(HexCastleAssaultAIPresentation.ResolveTag(catalog.Resolve("floria_01")),
                Is.EqualTo("회복 지원"));
            Assert.That(HexCastleAssaultAIPresentation.ResolveTag(catalog.Resolve("lucy_01")),
                Is.EqualTo("공격 지원"));
        }

        [Test]
        public void AssaultWorld_SequentialUnitsChooseOneOfNearestThreeOuterWalls()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var start = HexCoordinates.Directions[0] * 7;
            var first = CreateAssaultUnit(world, cells, start, "castley_01");
            var second = CreateAssaultUnit(world, cells, start, "castley_01");

            first.RefreshStrategicDecision();
            second.RefreshStrategicDecision();

            Assert.That(first.CurrentTarget.IsValid, Is.True);
            Assert.That(second.CurrentTarget.IsValid, Is.True);
            Assert.That(first.CurrentTarget.Structure.DefenseLayer, Is.EqualTo(2));
            var nearestThree = cells.Values
                .Where(value => value != null && value.IsAlive && value.DefenseLayer == 2 &&
                                value.WallRole != HexCastleWallRole.Partition)
                .OrderBy(value => start.DistanceTo(value.Coordinates))
                .ThenBy(value => value.Coordinates)
                .Take(3)
                .ToArray();
            Assert.That(nearestThree, Does.Contain(first.CurrentTarget.Structure));
            Assert.That(nearestThree, Does.Contain(second.CurrentTarget.Structure));
            Assert.That(first.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.InitialBreach));
            Assert.That(second.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.InitialBreach));
            Assert.That(second.CurrentTarget.Structure, Is.EqualTo(first.CurrentTarget.Structure),
                "합류 가능한 후발 유닛은 기존 공격대의 돌파 벽을 실제로 재사용해야 합니다.");
            Assert.That(second.RouteId, Is.EqualTo(first.RouteId));
            Assert.That(world.ActiveCohortCount, Is.EqualTo(1));
            Assert.That(world.ActiveBreachReservationOwnerCount, Is.EqualTo(2));

            world.UnregisterUnit(first);
            Assert.That(world.ActiveCohortCount, Is.EqualTo(1));
            Assert.That(world.ActiveBreachReservationOwnerCount, Is.EqualTo(1));
            Assert.That(world.ActiveOuterBreachRouteCount, Is.EqualTo(1));
            world.UnregisterUnit(second);
            Assert.That(world.ActiveCohortCount, Is.EqualTo(0));
            Assert.That(world.ActiveBreachReservationOwnerCount, Is.Zero);
            Assert.That(world.ActiveOuterBreachRouteCount, Is.Zero,
                "생존 소유자가 없는 돌파 예약은 즉시 반환해야 합니다.");
        }

        [Test]
        public void AssaultWorld_PartialDamageOnlyReplansNearbyUnitsWithoutDiscardingSharedField()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var unit = CreateAssaultUnit(
                world,
                cells,
                HexCoordinates.Directions[0] * 7,
                "kimhyeona_01");
            unit.RefreshStrategicDecision();
            var wall = unit.CurrentTarget.Structure;
            var fullHealthTopology = world.TopologyVersion;
            var cachedFields = world.CachedRouteFieldCount;
            var initialCostRevision = world.CostRevision;

            Assert.That(wall.ApplyDamage(20f, wall.transform.position), Is.True);
            Assert.That(world.TopologyVersion, Is.EqualTo(fullHealthTopology),
                "같은 25% 체력 구간의 작은 피해는 경로장을 매번 폐기하면 안 됩니다.");

            Assert.That(wall.ApplyDamage(30f, wall.transform.position), Is.True);
            Assert.That(world.TopologyVersion, Is.EqualTo(fullHealthTopology),
                "남은 HP 변화는 통행 연결을 바꾸지 않으므로 공유 위상 버전을 올리면 안 됩니다.");
            Assert.That(world.CostRevision, Is.GreaterThan(initialCostRevision));
            Assert.That(world.CachedRouteFieldCount, Is.EqualTo(cachedFields),
                "남은 HP는 주변 후보에서 정확히 다시 계산하고 공유 비용장은 보존해야 합니다.");
            Assert.That(unit.NeedsStrategicDecision, Is.True);
        }

        [Test]
        public void AssaultWorld_DestroyedCellChangesTopologyAndRetargetsNextLayer()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var unit = CreateAssaultUnit(
                world,
                cells,
                HexCoordinates.Directions[0] * 7,
                "kimhyeona_01");
            unit.RefreshStrategicDecision();
            var outerWall = unit.CurrentTarget.Structure;
            var previousTopology = world.TopologyVersion;

            Assert.That(outerWall.ApplyDamage(outerWall.MaxHealth, outerWall.transform.position), Is.True);
            Assert.That(world.TopologyVersion, Is.GreaterThan(previousTopology));
            Assert.That(unit.NeedsStrategicDecision, Is.True);
            unit.RefreshStrategicDecision();

            Assert.That(unit.CurrentTarget.IsValid, Is.True);
            Assert.That(unit.CurrentTarget.Structure.DefenseLayer, Is.EqualTo(1));
        }

        [Test]
        public void AssaultUnit_InvalidatedTargetContinuesCurrentPathUntilStrategicRefresh()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var unit = CreateAssaultUnit(
                world,
                cells,
                HexCoordinates.Directions[0] * 7,
                "kimhyeona_01");
            unit.RefreshStrategicDecision();
            var outerWall = unit.CurrentTarget.Structure;
            var positionBeforeInvalidation = unit.transform.position;

            Assert.That(outerWall.ApplyDamage(outerWall.MaxHealth, outerWall.transform.position), Is.True);
            Assert.That(unit.CurrentTarget.IsValid, Is.False);
            Assert.That(unit.NeedsStrategicDecision, Is.True);

            var tickDynamicRuntime = typeof(HexCastleAssaultUnit).GetMethod(
                "TickDynamicRuntime",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(tickDynamicRuntime, Is.Not.Null);
            tickDynamicRuntime.Invoke(unit, new object[] { 0.1f });

            Assert.That(unit.transform.position, Is.Not.EqualTo(positionBeforeInvalidation),
                "재탐색을 기다리는 동안에도 기존의 유효한 이동 구간은 계속 따라가야 합니다.");
        }

        [Test]
        public void ResourceRaider_SelectsReachableNearbyRewardBuildingAfterInitialBreach()
        {
            var cells = CreateTwoLayerBoard();
            var start = HexCoordinates.Directions[0] * 7;
            var buildingCoordinates = new HexCoordinates(6, 1);
            var building = CreateRuntime(new HexCastleCell(
                buildingCoordinates,
                HexCastleCellKind.RewardBuilding,
                hitPoints: 90f,
                rewardValue: 100,
                initialBlocked: true,
                lootKind: HexCastleLootKind.Gold,
                placementId: "BUILDING_PRIORITY",
                buildingRole: HexCastleBuildingRole.GoldStorage,
                placementDensity: HexCastlePlacementDensity.Sparse,
                buildingGrade: 1));
            cells[buildingCoordinates] = building;
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var unit = CreateAssaultUnit(world, cells, start, "dubi_01");

            unit.RefreshStrategicDecision();
            var initialWall = unit.CurrentTarget.Structure;
            initialWall.ApplyDamage(initialWall.MaxHealth, initialWall.transform.position);
            unit.RefreshStrategicDecision();

            Assert.That(unit.AIProfile.Pattern, Is.EqualTo(HexCastleAssaultPattern.ResourceRaider));
            Assert.That(unit.CurrentTarget.Structure, Is.EqualTo(building));
        }

        [Test]
        public void RecentTurretThreat_TemporarilyOverridesBalancedRouteTarget()
        {
            var cells = CreateTwoLayerBoard();
            var start = HexCoordinates.Directions[0] * 7;
            var turretCoordinates = new HexCoordinates(6, 1);
            var turret = CreateRuntime(new HexCastleCell(
                turretCoordinates,
                HexCastleCellKind.DefenseBuilding,
                hitPoints: 120f,
                initialBlocked: true,
                placementId: "RECENT_TURRET",
                buildingRole: HexCastleBuildingRole.Turret,
                placementDensity: HexCastlePlacementDensity.Sparse,
                buildingGrade: 1,
                turretWeaponKind: HexCastleTurretWeaponKind.Ballista,
                turretRangeCells: 3,
                turretCanAttackAcrossWalls: true));
            cells[turretCoordinates] = turret;
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var unit = CreateAssaultUnit(world, cells, start, "kimhyeona_01");

            unit.ApplyDamage(5f, unit.transform.position, null, turret);
            unit.RefreshStrategicDecision();

            Assert.That(unit.AIProfile.Pattern, Is.EqualTo(HexCastleAssaultPattern.GeneralAdvance));
            Assert.That(unit.CurrentTarget.Structure, Is.EqualTo(turret));
        }

        [Test]
        public void LongRangeThreatBehindIntactWall_CannotOverrideRequiredBreach()
        {
            var cells = CreateTwoLayerBoard();
            var start = HexCoordinates.Directions[0] * 7;
            var turretCoordinates = new HexCoordinates(4, 0);
            var turret = CreateRuntime(new HexCastleCell(
                turretCoordinates,
                HexCastleCellKind.DefenseBuilding,
                hitPoints: 120f,
                initialBlocked: true,
                placementId: "INNER_TURRET",
                buildingRole: HexCastleBuildingRole.Turret,
                placementDensity: HexCastlePlacementDensity.Dense,
                buildingGrade: 1,
                turretWeaponKind: HexCastleTurretWeaponKind.Ballista,
                turretRangeCells: 3,
                turretCanAttackAcrossWalls: true));
            cells[turretCoordinates] = turret;
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 41);
            var unit = CreateAssaultUnit(world, cells, start, "kimhyeona_01", 4f);

            unit.ApplyDamage(5f, unit.transform.position, null, turret);
            unit.RefreshStrategicDecision();

            Assert.That(unit.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.InitialBreach));
            Assert.That(unit.CurrentTarget.Structure, Is.Not.EqualTo(turret));
            Assert.That(unit.CurrentTarget.Structure.DefenseLayer, Is.EqualTo(2));
            Assert.That(world.IsAttackLaneOpen(
                new HexCoordinates(6, 0),
                new HexCastleAssaultTarget(turret, false)), Is.False);

            var separatingWall = cells[new HexCoordinates(5, 0)];
            separatingWall.ApplyDamage(separatingWall.MaxHealth, separatingWall.transform.position);
            Assert.That(world.IsAttackLaneOpen(
                new HexCoordinates(6, 0),
                new HexCastleAssaultTarget(turret, false)), Is.True);
        }

        [Test]
        public void AttackSlotLease_AssignsDifferentSpatialSlotsToSameWall()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var start = HexCoordinates.Directions[0] * 7;
            var first = CreateAssaultUnit(world, cells, start, "kimhyeona_01");
            var second = CreateAssaultUnit(world, cells, start, "kimhyeona_01");

            Assert.That(world.TryResolveDecision(first, out var firstDecision), Is.True);
            Assert.That(world.TryResolveDecision(second, out var secondDecision), Is.True);
            Assert.That(firstDecision.Target.Structure, Is.EqualTo(secondDecision.Target.Structure));
            Assert.That(firstDecision.SlotLease.IsValid, Is.True);
            Assert.That(secondDecision.SlotLease.IsValid, Is.True);
            Assert.That(firstDecision.SlotLease.Key, Is.Not.EqualTo(secondDecision.SlotLease.Key));
        }

        [Test]
        public void CommittedTarget_WhenAttackSlotsAreFull_UsesRankedInnerAlternativeAndKeepsCohort()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 19319);
            var start = HexCoordinates.Directions[0] * 4;
            var unit = CreateAssaultUnit(world, cells, start, "kimhyeona_01");
            SetPrivateAutoProperty(unit, "ExpectedDefenseLayer", 1);
            SetPrivateAutoProperty(unit, "HasSelectedInitialWall", true);
            unit.RefreshStrategicDecision();

            var saturated = unit.CurrentTarget.Structure;
            Assert.That(saturated, Is.Not.Null);
            Assert.That(saturated.DefenseLayer, Is.EqualTo(1));
            var previousRoute = unit.RouteId;
            var previousCohort = CohortId(world, unit);
            world.SlotAllocator.ReleaseOwner(unit, true);
            var reserved = FillAttackPositions(world, cells, saturated, unit);
            Assert.That(reserved, Is.GreaterThanOrEqualTo(3),
                "주 공격면의 세 자리를 포함해 도달 가능한 공격 자리를 먼저 포화해야 합니다.");

            var temptingOuter = cells.Values
                .Where(value => value.IsAlive && value.DefenseLayer == 2)
                .OrderBy(value => start.DistanceTo(value.Coordinates))
                .First();
            temptingOuter.ApplyDamage(temptingOuter.MaxHealth - 1f, temptingOuter.transform.position);
            unit.RequestStrategicDecision(true);
            unit.RefreshStrategicDecision();

            Assert.That(unit.CurrentTarget.IsValid, Is.True);
            Assert.That(unit.CurrentTarget.Structure, Is.Not.SameAs(saturated));
            Assert.That(unit.CurrentTarget.Structure, Is.Not.SameAs(temptingOuter),
                "자리 포화 대체 목표가 낮은 HP만 보고 이미 지난 바깥 방어층으로 후퇴하면 안 됩니다.");
            Assert.That(unit.CurrentTarget.Structure.DefenseLayer, Is.LessThanOrEqualTo(1));
            Assert.That(unit.RouteId, Is.EqualTo(previousRoute));
            Assert.That(CohortId(world, unit), Is.EqualTo(previousCohort));
            Assert.That(unit.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.Progress));
            var trace = new List<HexAssaultTraceEvent>();
            world.Trace.CopyAfter(0, trace);
            Assert.That(trace.Any(value => value.Kind == HexAssaultTraceKind.CohortReassigned), Is.False,
                "공격 자리 포화로 대체 목표를 선택해도 경로 공유 부대는 유지해야 합니다.");
        }

        [Test]
        public void CommittedTarget_WhenStrategicAlternativesAreUnavailable_UsesTemporaryLocalWorkWithoutReassigningCohort()
        {
            var cells = CreateTwoLayerBoard();
            var start = HexCoordinates.Directions[0] * 4;
            var localWorkCoordinates = new HexCoordinates(4, -1);
            cells[localWorkCoordinates] = CreateRuntime(new HexCastleCell(
                localWorkCoordinates,
                HexCastleCellKind.Building,
                defenseLayer: 1,
                hitPoints: 15f,
                initialBlocked: true,
                buildingRole: HexCastleBuildingRole.Blocker,
                placementDensity: HexCastlePlacementDensity.Sparse,
                buildingGrade: 1,
                placementId: "LOCAL_WORK"));
            var localWork = cells[localWorkCoordinates];
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 19320);
            var unit = CreateAssaultUnit(world, cells, start, "kimhyeona_01");
            SetPrivateAutoProperty(unit, "ExpectedDefenseLayer", 1);
            SetPrivateAutoProperty(unit, "HasSelectedInitialWall", true);
            unit.RefreshStrategicDecision();

            var committed = unit.CommittedTarget;
            Assert.That(committed.Structure, Is.Not.Null);
            Assert.That(IsAliveRingWall(committed.Structure), Is.True);
            var originalCohort = CohortId(world, unit);
            var originalRoute = unit.RouteId;
            world.SlotAllocator.ReleaseOwner(unit, true);
            foreach (var wall in cells.Values.Where(value =>
                         IsAliveRingWall(value) && value != committed.Structure).ToArray())
            {
                wall.ApplyDamage(wall.MaxHealth, wall.transform.position);
            }
            Assert.That(FillAttackPositions(world, cells, committed.Structure, unit), Is.GreaterThanOrEqualTo(3));
            var traceStart = world.Trace.LastSequence;

            unit.RequestStrategicDecision(true);
            unit.RefreshStrategicDecision();

            Assert.That(unit.CurrentTarget.Structure, Is.SameAs(localWork));
            Assert.That(unit.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.LocalFallback));
            Assert.That(unit.CommittedTarget.InstanceId, Is.EqualTo(committed.InstanceId),
                "4순위 지역 공격은 전략 목표를 교체하면 안 됩니다.");
            Assert.That(unit.RouteId, Is.EqualTo(originalRoute));
            Assert.That(CohortId(world, unit), Is.EqualTo(originalCohort),
                "4순위 지역 공격은 기존 부대를 유지해야 합니다.");
            var trace = new List<HexAssaultTraceEvent>();
            world.Trace.CopyAfter(traceStart, trace);
            Assert.That(trace.Any(value => value.Kind == HexAssaultTraceKind.CohortReassigned), Is.False);

            unit.RequestStrategicDecision(true);
            unit.RefreshStrategicDecision();
            Assert.That(unit.CurrentTarget.Structure, Is.SameAs(localWork),
                "주 목표가 계속 포화된 동안에는 같은 임시 공격을 유지해야 합니다.");
            Assert.That(CohortId(world, unit), Is.EqualTo(originalCohort));
        }

        [Test]
        public void TacticalSupport_SelectsAndHealsDamagedNearbyAlly()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null);
            var start = HexCoordinates.Directions[0] * 7;
            var support = CreateAssaultUnit(world, cells, start, "floria_01");
            var ally = CreateAssaultUnit(world, cells, start.Neighbor(2), "kimhyeona_01");
            ally.ApplyDamage(50f);
            var damagedHealth = ally.CurrentHealth;

            Assert.That(world.TryResolveSupportDecision(support, out var target, out var action), Is.True);
            Assert.That(target, Is.EqualTo(ally));
            Assert.That(action, Is.EqualTo(HexCastleAssaultSupportAction.Heal));
            target.ApplySupport(action, support.AIProfile);
            Assert.That(ally.CurrentHealth, Is.GreaterThan(damagedHealth));
        }

        [Test]
        public void TacticalSupport_TentativeClaimPreventsDuplicateTargetActionPileup()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 331);
            var start = HexCoordinates.Directions[0] * 7;
            var firstSupport = CreateAssaultUnit(world, cells, start, "floria_01");
            var secondSupport = CreateAssaultUnit(world, cells, start.Neighbor(2), "floria_01");
            var firstAlly = CreateAssaultUnit(world, cells, start.Neighbor(1), "kimhyeona_01");
            var secondAlly = CreateAssaultUnit(world, cells, start.Neighbor(3), "phoenix_01");
            firstAlly.ApplyDamage(50f);
            secondAlly.ApplyDamage(50f);

            Assert.That(world.TryResolveSupportDecision(
                firstSupport,
                out var firstTarget,
                out var firstAction), Is.True);
            Assert.That(world.TryResolveSupportDecision(
                secondSupport,
                out var secondTarget,
                out var secondAction), Is.True);

            Assert.That(firstAction, Is.Not.EqualTo(HexCastleAssaultSupportAction.None));
            Assert.That(secondAction, Is.Not.EqualTo(HexCastleAssaultSupportAction.None));
            Assert.That(secondTarget == firstTarget && secondAction == firstAction, Is.False,
                "동시에 판단한 지원형 둘이 같은 대상의 같은 효과에 겹치면 안 됩니다.");
            Assert.That(world.ActiveSupportClaimCount, Is.EqualTo(2));
        }

        [Test]
        public void InitialBreach_UsesDeterministicFastestCompletionInsteadOfWeightedRandom()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 19317);
            var start = HexCoordinates.Directions[0] * 7;
            var nearestThree = cells.Values
                .Where(value => value != null && value.IsAlive && value.DefenseLayer == 2 &&
                                value.WallRole != HexCastleWallRole.Partition)
                .OrderBy(value => start.DistanceTo(value.Coordinates))
                .ThenBy(value => value.Coordinates)
                .Take(3)
                .ToArray();
            HexCastleCellRuntime selectedTarget = null;

            for (var index = 0; index < 30; index++)
            {
                var unit = CreateAssaultUnit(world, cells, start, "castley_01");
                unit.RefreshStrategicDecision();
                var selected = System.Array.IndexOf(nearestThree, unit.CurrentTarget.Structure);
                Assert.That(selected, Is.InRange(0, 2));
                if (selectedTarget == null) selectedTarget = unit.CurrentTarget.Structure;
                else Assert.That(unit.CurrentTarget.Structure, Is.SameAs(selectedTarget));
                world.UnregisterUnit(unit);
            }
        }

        [Test]
        public void InitialBreach_MoveSpeedDoesNotOverrideDeploymentFrontForDamagedDistantWall()
        {
            var start = HexCoordinates.Directions[0] * 7;
            var slowCells = CreateTwoLayerBoard();
            var slowFar = slowCells.Values
                .Where(value => value.IsAlive && value.DefenseLayer == 2)
                .OrderByDescending(value => start.DistanceTo(value.Coordinates))
                .ThenBy(value => value.Coordinates)
                .First();
            slowFar.ApplyDamage(slowFar.MaxHealth - 1f, slowFar.transform.position);
            var slowRoot = new GameObject("SlowAssaultWorld");
            owned.Add(slowRoot);
            var slowWorld = slowRoot.AddComponent<HexCastleAssaultWorld>();
            slowWorld.Configure(slowCells, 1f, 2, null, null, 19317);
            var slow = CreateAssaultUnit(slowWorld, slowCells, start, "castley_01", moveSpeed: 0.5f);
            slow.RefreshStrategicDecision();
            Assert.That(slow.CurrentTarget.Structure, Is.Not.SameAs(slowFar),
                "느린 병력은 낮은 HP만 보고 먼 벽까지 우회하면 안 됩니다.");
            Assert.That(slow.HasBreachCostEvaluation, Is.True);
            Assert.That(slow.LastBreachMovementSeconds, Is.GreaterThan(0f));
            Assert.That(slow.LastBreachDestructionSeconds, Is.GreaterThan(0f));
            Assert.That(slow.LastBreachTotalSeconds,
                Is.EqualTo(slow.LastBreachMovementSeconds + slow.LastBreachDestructionSeconds).Within(0.0001f));
            Assert.That(slow.LastBreachMovementSeconds * 1000f,
                Is.EqualTo(Mathf.Round(slow.LastBreachMovementSeconds * 1000f)).Within(0.001f));
            Assert.That(slow.LastBreachDestructionSeconds * 1000f,
                Is.EqualTo(Mathf.Round(slow.LastBreachDestructionSeconds * 1000f)).Within(0.001f),
                "돌파 비교값은 초 단위 부동소수점이 아니라 정수 ms 경계로 고정되어야 합니다.");

            var fastCells = CreateTwoLayerBoard();
            var fastFar = fastCells.Values
                .Where(value => value.IsAlive && value.DefenseLayer == 2)
                .OrderByDescending(value => start.DistanceTo(value.Coordinates))
                .ThenBy(value => value.Coordinates)
                .First();
            fastFar.ApplyDamage(fastFar.MaxHealth - 1f, fastFar.transform.position);
            var fastRoot = new GameObject("FastAssaultWorld");
            owned.Add(fastRoot);
            var fastWorld = fastRoot.AddComponent<HexCastleAssaultWorld>();
            fastWorld.Configure(fastCells, 1f, 2, null, null, 19317);
            var fast = CreateAssaultUnit(fastWorld, fastCells, start, "castley_01", moveSpeed: 12f);
            fast.RefreshStrategicDecision();
            Assert.That(fast.CurrentTarget.Structure, Is.Not.SameAs(fastFar),
                "빠른 병력도 소환 위치의 공략 전면을 버리고 성 반대편으로 이동하면 안 됩니다.");
            Assert.That(fast.CurrentTarget.Coordinates.DistanceTo(new HexCoordinates(5, 0)), Is.LessThanOrEqualTo(1));
            Assert.That(fast.LastBreachMovementSeconds, Is.GreaterThan(0f));
            Assert.That(fast.LastBreachMovementSeconds, Is.LessThan(slow.LastBreachMovementSeconds),
                "같은 전면에서도 실제 이동 속도를 접근 비용에 반영해야 합니다.");
        }

        [Test]
        public void InitialBreach_DistantCommittedDpsDoesNotDiscountUnrelatedWall()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 19318);

            var east = HexCoordinates.Directions[0] * 7;
            var west = HexCoordinates.Directions[3] * 7;
            var distantAttacker = CreateAssaultUnit(
                world, cells, west, "castley_01", moveSpeed: 3f, damage: 10000f);
            distantAttacker.RefreshStrategicDecision();
            var distantTarget = distantAttacker.CurrentTarget.Structure;

            var local = CreateAssaultUnit(
                world, cells, east, "castley_01", moveSpeed: 3f, damage: 1f);
            local.RefreshStrategicDecision();

            Assert.That(east.DistanceTo(distantTarget.Coordinates), Is.GreaterThan(6));
            Assert.That(local.CurrentTarget.Structure, Is.Not.SameAs(distantTarget),
                "성 반대편 병력의 높은 DPS가 새 병력의 후보 비용을 깎아 같은 벽으로 끌어당기면 안 됩니다.");
            Assert.That(local.CurrentTarget.Structure.Coordinates.DistanceTo(east),
                Is.LessThan(distantTarget.Coordinates.DistanceTo(east)));
        }

        [Test]
        public void ThreatInterrupt_AfterThreatDiesResumesCommittedResourceTarget()
        {
            var cells = CreateTwoLayerBoard();
            var start = HexCoordinates.Directions[0] * 7;
            var resource = CreateRuntime(new HexCastleCell(
                new HexCoordinates(6, 1),
                HexCastleCellKind.RewardBuilding,
                hitPoints: 120f,
                rewardValue: 100,
                initialBlocked: true,
                lootKind: HexCastleLootKind.Gold,
                placementId: "GOLD_REWARD",
                buildingRole: HexCastleBuildingRole.GoldStorage,
                placementDensity: HexCastlePlacementDensity.Sparse,
                buildingGrade: 1));
            var turret = CreateRuntime(new HexCastleCell(
                new HexCoordinates(6, -1),
                HexCastleCellKind.DefenseBuilding,
                hitPoints: 80f,
                initialBlocked: true,
                placementId: "THREAT_TURRET",
                buildingRole: HexCastleBuildingRole.Turret,
                placementDensity: HexCastlePlacementDensity.Sparse,
                buildingGrade: 1,
                turretWeaponKind: HexCastleTurretWeaponKind.Ballista,
                turretRangeCells: 3,
                turretCanAttackAcrossWalls: true));
            cells[resource.Coordinates] = resource;
            cells[turret.Coordinates] = turret;
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 13);
            var unit = CreateAssaultUnit(world, cells, start, "dubi_01");

            unit.RefreshStrategicDecision();
            var initialWall = unit.CurrentTarget.Structure;
            initialWall.ApplyDamage(initialWall.MaxHealth, initialWall.transform.position);
            unit.RefreshStrategicDecision();
            Assert.That(unit.CurrentTarget.Structure, Is.EqualTo(resource));
            Assert.That(unit.CommittedTarget.Structure, Is.EqualTo(resource));

            unit.ApplyDamage(5f, unit.transform.position, null, turret);
            unit.RefreshStrategicDecision();
            Assert.That(unit.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.Threat));
            Assert.That(unit.CurrentTarget.Structure, Is.EqualTo(turret));

            turret.ApplyDamage(turret.MaxHealth, turret.transform.position);
            unit.RefreshStrategicDecision();
            Assert.That(unit.CurrentTarget.Structure, Is.EqualTo(resource));
            Assert.That(unit.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.Specialist));
        }

        [Test]
        public void SharedThreat_NearbyGeneralInterruptsThenReturnsToCommittedWall()
        {
            var cells = CreateTwoLayerBoard();
            var start = HexCoordinates.Directions[0] * 7;
            var turret = CreateRuntime(new HexCastleCell(
                new HexCoordinates(6, 1),
                HexCastleCellKind.DefenseBuilding,
                hitPoints: 80f,
                initialBlocked: true,
                placementId: "SHARED_THREAT",
                buildingRole: HexCastleBuildingRole.Turret,
                placementDensity: HexCastlePlacementDensity.Sparse,
                buildingGrade: 1,
                turretWeaponKind: HexCastleTurretWeaponKind.Cannon,
                turretRangeCells: 3,
                turretCanAttackAcrossWalls: true));
            cells[turret.Coordinates] = turret;
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 71);
            var victim = CreateAssaultUnit(world, cells, start, "kimhyeona_01");
            var responder = CreateAssaultUnit(world, cells, start.Neighbor(2), "phoenix_01");
            responder.RefreshStrategicDecision();
            var committedWall = responder.CommittedTarget.Structure;

            victim.ApplyDamage(5f, victim.transform.position, null, turret);
            responder.RefreshStrategicDecision();
            Assert.That(responder.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.Threat));
            Assert.That(responder.CurrentTarget.Structure, Is.EqualTo(turret));

            turret.ApplyDamage(turret.MaxHealth, turret.transform.position);
            responder.RefreshStrategicDecision();
            Assert.That(responder.CurrentTarget.Structure, Is.EqualTo(committedWall));
        }

        [Test]
        public void TacticalSupport_StrategicDecisionTargetsAllyInsteadOfWall()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 5);
            var start = HexCoordinates.Directions[0] * 7;
            var support = CreateAssaultUnit(world, cells, start, "floria_01");
            var ally = CreateAssaultUnit(world, cells, start.Neighbor(2), "kimhyeona_01");
            ally.ApplyDamage(40f);

            support.RefreshStrategicDecision();

            Assert.That(support.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.Support));
            Assert.That(support.CurrentTarget.Kind, Is.EqualTo(HexCastleAssaultTargetKind.Ally));
            Assert.That(support.CurrentTarget.Ally, Is.EqualTo(ally));
            Assert.That(support.CurrentSupportAction, Is.EqualTo(HexCastleAssaultSupportAction.Heal));
            Assert.That(support.HasSelectedInitialWall, Is.False);
        }

        [Test]
        public void TacticalSupport_AfterCastingResumesGeneralAdvanceDuringCooldown()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 13);
            var start = HexCoordinates.Directions[0] * 7;
            var support = CreateAssaultUnit(world, cells, start, "floria_01");
            var ally = CreateAssaultUnit(world, cells, start.Neighbor(2), "kimhyeona_01");
            ally.ApplyDamage(40f);

            support.RefreshStrategicDecision();
            typeof(HexCastleAssaultUnit)
                .GetMethod("TickSupportTarget", System.Reflection.BindingFlags.Instance |
                                                System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(support, null);

            Assert.That(support.CanPerformSupportAction, Is.False, "지원 직후 쿨다운 시작");
            Assert.That(world.TryResolveSupportDecision(support, out _, out _), Is.False,
                "쿨다운 중 아군 목표를 다시 잡아 제자리 대기하면 안 됩니다.");
            support.RefreshStrategicDecision();
            Assert.That(support.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.InitialBreach));
            Assert.That(support.CurrentTarget.Kind, Is.Not.EqualTo(HexCastleAssaultTargetKind.Ally));
        }

        [Test]
        public void TacticalSupport_DoesNotBuffAllyAcrossClosedWallRing()
        {
            var cells = CreateTwoLayerBoard();
            var worldObject = new GameObject("HexAssaultWorld");
            owned.Add(worldObject);
            var world = worldObject.AddComponent<HexCastleAssaultWorld>();
            world.Configure(cells, 1f, 2, null, null, 9);
            var support = CreateAssaultUnit(
                world,
                cells,
                HexCoordinates.Directions[0] * 7,
                "floria_01");
            var ally = CreateAssaultUnit(
                world,
                cells,
                HexCoordinates.Directions[0] * 4,
                "kimhyeona_01");
            ally.ApplyDamage(40f);

            support.RefreshStrategicDecision();

            Assert.That(support.CurrentTarget.Kind, Is.Not.EqualTo(HexCastleAssaultTargetKind.Ally));
            Assert.That(support.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.InitialBreach));
        }

        private Dictionary<HexCoordinates, HexCastleCellRuntime> CreateTwoLayerBoard()
        {
            var result = new Dictionary<HexCoordinates, HexCastleCellRuntime>();
            foreach (var coordinates in HexCoordinates.EnumerateRadius(7))
            {
                var distance = coordinates.DistanceFromOrigin;
                HexCastleCell cell;
                if (distance <= 1)
                {
                    cell = new HexCastleCell(
                        coordinates,
                        HexCastleCellKind.Palace,
                        hitPoints: 1000f,
                        initialBlocked: true,
                        placementId: $"PALACE_{coordinates.Q}_{coordinates.R}");
                }
                else if (distance == 3 || distance == 5)
                {
                    var outer = distance == 5;
                    cell = new HexCastleCell(
                        coordinates,
                        HexCastleCellKind.Wall,
                        defenseLayer: outer ? 2 : 1,
                        hitPoints: outer ? 180f : 140f,
                        wallRole: outer ? HexCastleWallRole.OuterPerimeter : HexCastleWallRole.CoreDefense,
                        initialBlocked: true,
                        placementId: $"WALL_{distance}_{coordinates.Q}_{coordinates.R}");
                }
                else
                {
                    cell = new HexCastleCell(
                        coordinates,
                        distance > 5 ? HexCastleCellKind.Deployment : HexCastleCellKind.Ground,
                        initialBlocked: false);
                }

                result.Add(coordinates, CreateRuntime(cell));
            }

            return result;
        }

        private HexCastleCellRuntime CreateRuntime(HexCastleCell cell)
        {
            var root = new GameObject($"Cell_{cell.Coordinates.Q}_{cell.Coordinates.R}");
            owned.Add(root);
            var runtime = root.AddComponent<HexCastleCellRuntime>();
            var tile = CreateChild("TileVisualRoot", root.transform);
            var content = CreateChild("ContentVisualRoot", root.transform);
            if (!cell.InitialBlocked)
            {
                runtime.Configure(cell, null, null, tile, content);
                return runtime;
            }

            var health = root.AddComponent<HealthComponent>();
            var collider = root.AddComponent<BoxCollider>();
            runtime.Configure(cell, health, collider, tile, content);
            return runtime;
        }

        private HexCastleAssaultUnit CreateAssaultUnit(
            HexCastleAssaultWorld world,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cells,
            HexCoordinates start,
            string monsterId,
            float attackRange = 1.1f,
            float moveSpeed = 3f,
            float damage = 20f,
            float attackInterval = 1f)
        {
            var root = new GameObject($"Assault_{monsterId}_{owned.Count}");
            owned.Add(root);
            var unit = root.AddComponent<HexCastleAssaultUnit>();
            unit.ConfigureForPartyUnit(
                world,
                start,
                cells,
                1f,
                Vector3.zero,
                new BattleUnitSnapshot(
                    monsterId,
                    new UnitStatsSnapshot
                    {
                        maxHealth = 100f,
                        damage = damage,
                        moveSpeed = moveSpeed,
                        attackRange = attackRange,
                        attackInterval = attackInterval
                    }));
            return unit;
        }

        private int FillAttackPositions(
            HexCastleAssaultWorld world,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cells,
            HexCastleCellRuntime target,
            HexCastleAssaultUnit observer)
        {
            var assaultTarget = new HexCastleAssaultTarget(target, false);
            var reserved = 0;
            foreach (var cell in cells.Values
                         .Where(value => value != null && !value.IsBlocked &&
                                         value.Coordinates.DistanceTo(target.Coordinates) <= observer.AttackRangeCells)
                         .OrderBy(value => value.Coordinates))
            {
                for (var index = 0; index < world.SlotAllocator.CandidateCount(observer.SlotBodyRadius); index++)
                {
                    var blocker = CreateAssaultUnit(world, cells, observer.CurrentCoordinates, "kimhyeona_01");
                    var key = world.SlotAllocator.Candidate(cell.Coordinates, blocker.SlotBodyRadius, index);
                    if (!world.CanAttackFromPosition(blocker, assaultTarget, key))
                    {
                        continue;
                    }

                    if (world.SlotAllocator.TryCommit(
                            blocker,
                            assaultTarget,
                            key,
                            blocker.SlotBodyRadius,
                            1f,
                            world.SlotAllocator.Revision,
                            false,
                            out _,
                            out _))
                    {
                        reserved++;
                    }
                }
            }

            return reserved;
        }

        private static bool IsAliveRingWall(HexCastleCellRuntime cell)
        {
            return cell != null && cell.IsAlive &&
                   cell.WallRole != HexCastleWallRole.None &&
                   cell.WallRole != HexCastleWallRole.Partition &&
                   (cell.Kind == HexCastleCellKind.Wall ||
                    cell.Kind == HexCastleCellKind.Tower ||
                    cell.Kind == HexCastleCellKind.Gate);
        }

        private static int CohortId(HexCastleAssaultWorld world, HexCastleAssaultUnit unit)
        {
            var assignments = (Dictionary<int, HexCastleAssaultCohortAssignment>)typeof(HexCastleAssaultWorld)
                .GetField("unitCohorts",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(world);
            return assignments != null && assignments.TryGetValue(unit.GetInstanceID(), out var assignment)
                ? assignment.CohortId
                : 0;
        }

        private static void SetPrivateAutoProperty<T>(HexCastleAssaultUnit unit, string property, T value)
        {
            typeof(HexCastleAssaultUnit)
                .GetField("<" + property + ">k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(unit, value);
        }

        private static Transform CreateChild(string name, Transform parent)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }
    }
}
