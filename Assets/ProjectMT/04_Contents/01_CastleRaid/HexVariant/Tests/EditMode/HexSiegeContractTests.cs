using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectMT.Shared.Combat;
using ProjectMT.Shared.Unit;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Tests
{
    public sealed class HexSiegeContractTests
    {
        readonly List<GameObject> owned = new List<GameObject>();
        Dictionary<HexCoordinates, HexCastleCellRuntime> cells;
        HexCastleAssaultWorld world;
        static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        [SetUp] public void SetUp()
        {
            cells = new Dictionary<HexCoordinates, HexCastleCellRuntime>();
            foreach (var c in HexCoordinates.EnumerateRadius(7))
            {
                var d = c.DistanceFromOrigin; var wall = d == 3 || d == 5; var palace = d <= 1;
                var cell = new HexCastleCell(c, palace ? HexCastleCellKind.Palace : wall ? HexCastleCellKind.Wall : d > 5 ? HexCastleCellKind.Deployment : HexCastleCellKind.Ground,
                    defenseLayer: wall ? d == 5 ? 2 : 1 : 0, hitPoints: palace ? 1000 : wall ? 180 : 0,
                    initialBlocked: palace || wall,
                    wallRole: wall ? d == 5 ? HexCastleWallRole.OuterPerimeter : HexCastleWallRole.CoreDefense : HexCastleWallRole.None);
                var go = Own("Cell_" + c); go.transform.position = c.ToWorld(1f);
                var r = go.AddComponent<HexCastleCellRuntime>();
                r.Configure(cell, wall || palace ? go.AddComponent<HealthComponent>() : null,
                    wall || palace ? go.AddComponent<BoxCollider>() : null, Child(go, "Tile"), Child(go, "Content"));
                cells.Add(c, r);
            }
            world = Own("World").AddComponent<HexCastleAssaultWorld>(); world.Configure(cells, 1f, 2, null);
        }
        [TearDown] public void TearDown()
        {
            if (world != null) world.Shutdown();
            for (var i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) UnityEngine.Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }
        GameObject Own(string name) { var go = new GameObject(name); owned.Add(go); return go; }
        static Transform Child(GameObject go, string name) { var child = new GameObject(name).transform; child.SetParent(go.transform, false); return child; }
        HexCastleAssaultUnit Unit(HexCoordinates? at = null, string unitId = "argo_01")
        {
            var u = Own("Assault").AddComponent<HexCastleAssaultUnit>();
            u.ConfigureForPartyUnit(world, at ?? new HexCoordinates(7, 0), cells, 1f, Vector3.zero,
                new BattleUnitSnapshot(unitId, new UnitStatsSnapshot { maxHealth = 100f, damage = 20f, attackInterval = 1f, attackRange = 1.1f, moveSpeed = 3f }));
            return u;
        }
        HexCastleAssaultTarget Wall(int q = 5, int r = 0) => new HexCastleAssaultTarget(cells[new HexCoordinates(q, r)], false);
        HexSlotLease Reserve(HexCastleAssaultUnit u, HexAttackSlotKey key, HexCastleAssaultTarget? target = null)
        {
            Assert.That(world.SlotAllocator.TryCommit(u, target ?? Wall(), key, u.SlotBodyRadius, 5f,
                world.OccupancyRevision, false, out var lease, out var reason), Is.True, reason.ToString());
            return lease;
        }
        static object Field(object obj, string field) => obj.GetType().GetField(field, Hidden).GetValue(obj);
        static void Set(object obj, string field, object value) => obj.GetType().GetField(field, Hidden).SetValue(obj, value);
        static void Tick(HexCastleAssaultUnit u, float dt = 0.05f) => typeof(HexCastleAssaultUnit).GetMethod("TickDynamicRuntime", Hidden).Invoke(u, new object[] { dt });

        [TestCase(7, 0, 5, 0)]
        [TestCase(-7, 0, -5, 0)]
        [TestCase(0, 7, 0, 5)]
        [TestCase(0, -7, 0, -5)]
        public void FrontRoute_UsesDeploymentSideInsteadOfCheaperRemoteWall(int q, int r, int wallQ, int wallR)
        {
            var remote = cells[new HexCoordinates(-wallQ, -wallR)];
            remote.ApplyDamage(179f, Vector3.zero);
            Assert.That(remote.CurrentHealth, Is.EqualTo(1f));
            var navigation = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(navigation.TryResolveFrontRoute(new HexCoordinates(q, r), 20f, 3f, 1, out var route), Is.True);
            Assert.That(route.FirstObstacle, Is.EqualTo(new HexCoordinates(wallQ, wallR)));
            for (var i = 1; i < route.Path.Count; i++)
                Assert.That(route.Path[i].DistanceFromOrigin, Is.LessThan(route.Path[i - 1].DistanceFromOrigin));
        }

        [Test] public void InitialTarget_StaysOnTheWallInFrontOfDeployment()
        {
            var u = Unit();
            var result = world.ResolveDecision(u);
            Assert.That(result.Status, Is.EqualTo(HexAssaultDecisionStatus.Accepted));
            Assert.That(result.Plan.Target.Coordinates.DistanceTo(new HexCoordinates(5, 0)), Is.LessThanOrEqualTo(1));
            Assert.That(result.Plan.Target.Structure.DefenseLayer, Is.EqualTo(2));
        }

        HexCastleAssaultCohortAssignment Join(HexCastleAssaultUnit unit, int routeId = 123)
        {
            var nav = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(nav.TryResolveFrontRoute(unit.CurrentCoordinates, 20f, 3f, world.TopologyVersion, out var route), Is.True);
            return (HexCastleAssaultCohortAssignment)typeof(HexCastleAssaultWorld).GetMethod("ResolveCohort", Hidden)
                .Invoke(world, new object[] { unit, route, routeId });
        }

        static void MoveUnit(HexCastleAssaultUnit unit, HexCoordinates at)
        {
            Set(unit, "<CurrentCoordinates>k__BackingField", at);
            unit.transform.position = at.ToWorld(1f);
        }

        [Test] public void Cohort_NearbyDifferentRoutesJoinButNeverExceedSix()
        {
            var joined = new List<HexCastleAssaultCohortAssignment>();
            for (var i = 0; i < 7; i++) joined.Add(Join(Unit(), 100 + i));
            Assert.That(joined.Take(6).Select(a => a.CohortId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(joined[6].CohortId, Is.Not.EqualTo(joined[0].CohortId));
            Assert.That(world.ActiveCohortCount, Is.EqualTo(2));
        }

        [Test] public void Cohort_ClosestToPalaceLeadsAndDeathImmediatelySucceeds()
        {
            var first = Unit(); var closer = Unit(new HexCoordinates(6, 0)); var tie = Unit(new HexCoordinates(6, 0));
            Join(first); Join(closer); Join(tie);
            Assert.That(world.GetCohortLeader(first), Is.SameAs(closer));
            closer.ApplyDamage(10000f);
            Assert.That(world.GetCohortLeader(first), Is.SameAs(tie));
        }

        [Test] public void Cohort_SmallGroupsMergeUsingCurrentPosition()
        {
            var first = Unit(); var later = Unit(new HexCoordinates(2, 5));
            var original = Join(first); var separate = Join(later, 999);
            Assert.That(separate.CohortId, Is.Not.EqualTo(original.CohortId));
            MoveUnit(later, new HexCoordinates(7, 0));
            Assert.That(Join(later, 777).CohortId, Is.EqualTo(original.CohortId));
            Assert.That(world.ActiveCohortCount, Is.EqualTo(1));
        }

        [Test] public void Cohort_SeparatesAcrossIntactWall()
        {
            var outside = Unit(new HexCoordinates(6, 0)); var inside = Unit(new HexCoordinates(4, 0));
            Assert.That(Join(outside).CohortId, Is.Not.EqualTo(Join(inside).CohortId));
        }

        [Test] public void Cohort_LeavingAndReturningDoesNotDependOnOldJoinRecord()
        {
            var first = Unit(); var other = Unit(new HexCoordinates(6, 0));
            var original = Join(first); Assert.That(Join(other).CohortId, Is.EqualTo(original.CohortId));
            MoveUnit(other, new HexCoordinates(0, 7));
            Assert.That(Join(other).CohortId, Is.Not.EqualTo(original.CohortId));
            MoveUnit(other, new HexCoordinates(6, 0));
            Assert.That(Join(other).CohortId, Is.EqualTo(original.CohortId));
        }

        [Test] public void Cohort_DoesNotStealOppositeDeploymentFrontAfterConvergence()
        {
            var east = Unit(); var west = Unit(new HexCoordinates(-7, 0));
            var original = Join(east); Join(west);
            MoveUnit(west, new HexCoordinates(7, 0));
            Assert.That(Join(west).CohortId, Is.Not.EqualTo(original.CohortId));
        }

        [Test] public void SixNearbyDecisions_BuildOneLeaderSearchAndUseSixLocalConnections()
        {
            var members = new List<HexCastleAssaultUnit>();
            for (var i = 0; i < 6; i++) members.Add(Unit());
            foreach (var member in members) world.ResolveDecision(member);
            Assert.That(world.ActiveCohortCount, Is.EqualTo(1));
            Assert.That(world.ApproachPlanner.FullSearchCount, Is.EqualTo(1));
            Assert.That(world.ApproachPlanner.SharedSearchBuildCount, Is.EqualTo(1));
            Assert.That(world.ApproachPlanner.LocalConnectionSearchCount, Is.EqualTo(6));
            Assert.That(world.SharedFrontRouteBuildCount, Is.EqualTo(1));
        }

        [Test] public void SharedApproach_ConnectsFollowerToLeaderTrunkWithoutFullSearch()
        {
            var leader = Unit(); var follower = Unit(new HexCoordinates(7, -1));
            var planner = world.ApproachPlanner;
            planner.BeginDecision(leader, 1, leader);
            Assert.That(planner.TryPlan(leader, Wall(-5, 0), 1, null, false, out _, out _), Is.True);
            var before = planner.FullSearchCount;
            planner.BeginDecision(follower, 1, leader);
            Assert.That(planner.TryPlan(follower, Wall(-5, 0), 1, null, false, out var plan, out _), Is.True);
            Assert.That(planner.FullSearchCount, Is.EqualTo(before));
            Assert.That(planner.SharedPathUseCount, Is.GreaterThan(0));
            Assert.That(plan.Cells.First(), Is.EqualTo(follower.CurrentCoordinates));
            Assert.That(plan.Cells.Count, Is.GreaterThan(4));
            for (var i = 1; i < plan.Cells.Count; i++)
            {
                var direction = Enumerable.Range(0, 6).Single(d => plan.Cells[i - 1].Neighbor(d) == plan.Cells[i]);
                Assert.That(HexRoutePlanner.CanTraverseStep(cells[plan.Cells[i - 1]], cells[plan.Cells[i]], direction,
                    HexCastleTraversalFaction.Assault), Is.True);
            }
        }

        [Test] public void SharedApproach_SameSixInputsUseOneFullSearchInsteadOfSix()
        {
            var members = Enumerable.Range(0, 6).Select(_ => Unit()).ToArray();
            var individual = new HexAssaultApproachPlanner(cells, world.SlotAllocator, 1f, Vector3.zero);
            var shared = new HexAssaultApproachPlanner(cells, world.SlotAllocator, 1f, Vector3.zero);
            foreach (var unit in members)
            {
                individual.BeginDecision(unit);
                shared.BeginDecision(unit, 123, members[0]);
                Assert.That(individual.TryPlan(unit, Wall(), 1, null, false, out var before, out _), Is.True);
                Assert.That(shared.TryPlan(unit, Wall(), 1, null, false, out var after, out _), Is.True);
                Assert.That(after.Slot, Is.EqualTo(before.Slot));
                Assert.That(after.Cells, Is.EqualTo(before.Cells));
                Assert.That(after.Cost, Is.EqualTo(before.Cost).Within(0.0001f));
            }
            Assert.That(individual.FullSearchCount, Is.EqualTo(6));
            Assert.That(shared.FullSearchCount, Is.EqualTo(1));
            Assert.That(shared.LocalConnectionSearchCount, Is.EqualTo(6));
            Assert.That(individual.FullVisitedCellCount, Is.EqualTo(shared.FullVisitedCellCount * 6));
        }

        [Test] public void WaitingUnit_AdvancesItsHoldingPositionWhenItsFrontHasOpened()
        {
            var unit = Unit();
            var key = new HexAttackSlotKey(unit.CurrentCoordinates, 0);
            Assert.That(world.SlotAllocator.TryCommit(unit, default, key, unit.SlotBodyRadius, 0f,
                world.OccupancyRevision, true, out var lease, out _), Is.True);
            var hold = new HexCastleAssaultDecision(default, new[] { unit.CurrentCoordinates }, unit.CurrentCoordinates,
                unit.RouteId, unit.RouteSector, world.TopologyVersion, HexCastleAssaultIntentKind.HoldPosition,
                slotLease: lease, finalApproach: new[] { unit.transform.position }, planKind: HexAssaultPlanKind.Hold);
            typeof(HexCastleAssaultUnit).GetMethod("ApplyPlan", Hidden).Invoke(unit, new object[] { hold });
            Set(unit, "<ExecutionState>k__BackingField", HexAssaultExecutionState.Holding);
            Assert.That(unit.TryRetainHold(world.TopologyVersion, out _), Is.True);
            Wall().Structure.ApplyDamage(10000f, Vector3.zero);
            Wall(3, 0).Structure.ApplyDamage(10000f, Vector3.zero);
            world.ApproachPlanner.BeginDecision(unit);
            var args = new object[] { unit, default(HexCastleAssaultDecision) };
            Assert.That(typeof(HexCastleAssaultWorld).GetMethod("TryCreateHoldPlan", Hidden).Invoke(world, args), Is.True);
            var next = (HexCastleAssaultDecision)args[1];
            Assert.That(next.Intent, Is.EqualTo(HexCastleAssaultIntentKind.HoldPosition));
            Assert.That(next.Approach.DistanceFromOrigin, Is.LessThan(unit.CurrentCoordinates.DistanceFromOrigin));
            Assert.That(next.MovementPath.Count, Is.LessThanOrEqualTo(4));
            Assert.That(world.ApproachPlanner.FullSearchCount, Is.EqualTo(1));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void WaitingUnit_UsesOpenedRubbleButNeverHoldsInNarrowPassage(bool narrow, bool expected)
        {
            var unit = Unit(new HexCoordinates(6, 0));
            var rubble = new HexCoordinates(5, 0);
            Wall().Structure.ApplyDamage(10000f, Vector3.zero);
            Assert.That(cells[rubble].Kind, Is.EqualTo(HexCastleCellKind.Wall));
            Assert.That(cells[rubble].CanTraverse(HexCastleTraversalFaction.Assault), Is.True);
            if (narrow)
            {
                cells.Remove(new HexCoordinates(6, -1));
                cells.Remove(new HexCoordinates(4, 1));
            }
            world.ApproachPlanner.BeginDecision(unit);
            Assert.That(world.ApproachPlanner.TryPlan(unit, default, 3, slot => slot.Cell == rubble,
                true, out var plan, out _), Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(plan.Slot.Cell, Is.EqualTo(rubble));
                Assert.That(plan.Cells.Count, Is.LessThanOrEqualTo(4));
                Assert.That(world.ApproachPlanner.FullSearchCount, Is.EqualTo(1));
            }
        }

        [Test] public void SharedSearch_OnlyAffectedFrontRebuildsAfterWallDestruction()
        {
            var east = Unit(); var west = Unit(new HexCoordinates(-7, 0));
            world.ResolveDecision(east); world.ResolveDecision(west);
            var built = world.ApproachPlanner.FullSearchCount;
            Wall(-5, 0).Structure.ApplyDamage(10000f, Vector3.zero);
            world.ResolveDecision(east);
            Assert.That(world.ApproachPlanner.FullSearchCount, Is.EqualTo(built));
            world.ResolveDecision(west);
            Assert.That(world.ApproachPlanner.FullSearchCount, Is.EqualTo(built + 1));
        }

        [Test] public void LocalWork_RemainsNearFollowerWhenFrontRouteHasAlreadyOpened()
        {
            Wall().Structure.ApplyDamage(10000f, Vector3.zero);
            Wall(3, 0).Structure.ApplyDamage(10000f, Vector3.zero);
            var unit = Unit(); var nav = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(nav.TryResolveFrontRoute(unit.CurrentCoordinates, 20f, 3f, world.TopologyVersion, out var route), Is.True);
            Assert.That(route.HasFirstObstacle, Is.False);
            world.ApproachPlanner.BeginDecision(unit);
            var args = new object[] { unit, default(HexCastleAssaultDecision), int.MaxValue };
            Assert.That(typeof(HexCastleAssaultWorld).GetMethod("TryResolveLocalFallback", Hidden).Invoke(world, args), Is.True);
            var decision = (HexCastleAssaultDecision)args[1];
            Assert.That(decision.Intent, Is.EqualTo(HexCastleAssaultIntentKind.LocalFallback));
            Assert.That(decision.Target.Coordinates.DistanceTo(unit.CurrentCoordinates), Is.LessThanOrEqualTo(4));
            Assert.That(decision.Target.Coordinates.Q, Is.GreaterThan(0));
        }

        [Test] public void PreparedLocalGeometry_MatchesLiveWallAndBodyChecks()
        {
            var startCell = new HexCoordinates(6, 0); var goal = new HexCoordinates(6, -1);
            var owner = Unit(startCell); var other = Unit(new HexCoordinates(7, -1));
            Reserve(other, new HexAttackSlotKey(goal, 1));
            var start = startCell.ToWorld(1); var end = goal.ToWorld(1);
            var midpoint = HexCoordinates.FromWorld((start + end) * .5f, 1f);
            var blockers = new List<Vector3>();
            foreach (var offset in HexCoordinates.EnumerateRadius(startCell.DistanceTo(goal) + 4))
            {
                var c = midpoint + offset;
                if (!cells.TryGetValue(c, out var cell) || cell.Kind == HexCastleCellKind.Palace ||
                    !cell.CanTraverse(HexCastleTraversalFaction.Assault)) blockers.Add(c.ToWorld(1));
            }
            var method = typeof(HexAssaultApproachPlanner).GetMethod("PreparedSegmentClear", Hidden);
            var random = new System.Random(10801);
            var radius = world.SlotAllocator.InRadius * 1.25f;
            Vector3 Point(Vector3 centre)
            {
                var angle = (float)random.NextDouble() * Mathf.PI * 2;
                return centre + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
            }
            for (var i = 0; i < 600; i++)
            {
                var from = Point(i % 2 == 0 ? start : end); var to = Point(i % 3 == 0 ? start : end);
                var expected = world.ApproachPlanner.SegmentClear(owner, from, to);
                var actual = (bool)method.Invoke(world.ApproachPlanner, new object[] { owner, from, to, blockers,
                    world.SlotAllocator.InRadius + owner.SlotBodyRadius + world.SlotAllocator.Clearance * .25f, owner.SlotBodyRadius });
                Assert.That(actual, Is.EqualTo(expected), "Prepared collision mismatch at sample " + i);
            }
        }

        [Test] public void CachedNormalsAndBounds_PreserveExactHexClipping()
        {
            bool Reference(Vector3 a, Vector3 b, float radius)
            {
                var delta = b - a; var low = 0f; var high = 1f;
                for (var d = 0; d < 6; d++)
                {
                    var normal = HexCoordinates.Directions[d].ToWorld(1).normalized;
                    var denominator = Vector3.Dot(normal, delta); var remaining = radius - Vector3.Dot(normal, a);
                    if (Mathf.Abs(denominator) < .000001f) { if (remaining < 0) return false; continue; }
                    var t = remaining / denominator;
                    if (denominator > 0) high = Mathf.Min(high, t); else low = Mathf.Max(low, t);
                    if (low > high) return false;
                }
                return high >= 0 && low <= 1;
            }
            var random = new System.Random(73);
            Vector3 Point() => new Vector3((float)random.NextDouble() * 8 - 4, 0, (float)random.NextDouble() * 8 - 4);
            for (var i = 0; i < 5000; i++)
            {
                var a = Point(); var b = Point(); var radius = .1f + (float)random.NextDouble() * 2;
                Assert.That(HexAssaultApproachPlanner.IntersectsHex(a, b, Vector3.zero, radius), Is.EqualTo(Reference(a, b, radius)));
            }
        }

        [Test] public void SpatialAttackLineQuery_MatchesExhaustiveWallScan()
        {
            var random = new System.Random(50100);
            for (var i = 0; i < 1200; i++)
            {
                var target = i % 3 == 0 ? new HexCastleAssaultTarget(cells[new HexCoordinates(0, 0)], true) : Wall(5, -(i % 5));
                var centre = target.Coordinates.ToWorld(1);
                var from = centre + new Vector3((float)random.NextDouble() * 12 - 6, 0, (float)random.NextDouble() * 12 - 6);
                var expected = !cells.Any(pair => pair.Value.IsAlive && pair.Value.IsBlocked && pair.Key != target.Coordinates &&
                    !(target.Kind == HexCastleAssaultTargetKind.Palace && pair.Value.Kind == HexCastleCellKind.Palace) &&
                    (pair.Value.Kind == HexCastleCellKind.Wall || pair.Value.Kind == HexCastleCellKind.Gate || pair.Value.Kind == HexCastleCellKind.Tower) &&
                    HexAssaultApproachPlanner.IntersectsHex(from, centre, pair.Key.ToWorld(1), world.SlotAllocator.InRadius - .00001f));
                Assert.That(world.ApproachPlanner.IsAttackLineOpen(from, target), Is.EqualTo(expected), "LOS sample " + i);
            }
        }

        [TestCase(16679, 6003, true)]
        [TestCase(5000, 6003, false)]
        [TestCase(7003, 6003, false)]
        [TestCase(7004, 6003, true)]
        public void OverflowTravel_DoesNotOutrankACompleteLocalJobByTierAlone(int travelMs, int localMs, bool expected)
        {
            var method = typeof(HexCastleAssaultWorld).GetMethod("ShouldCompleteLocalWorkBeforeTravel", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method.Invoke(null, new object[] { travelMs, localMs }), Is.EqualTo(expected));
        }

        [Test] public void ThreeSlots_HaveDistinctOwnersAndContainedBodyPositions()
        {
            var c = new HexCoordinates(6, 0); var allocator = world.SlotAllocator;
            var positions = new List<Vector3>();
            for (var i = 0; i < 3; i++)
            {
                var u = Unit(); var key = new HexAttackSlotKey(c, i); var lease = Reserve(u, key);
                Assert.That(allocator.Owns(u, lease), Is.True); Assert.That(allocator.Fits(key, u.SlotBodyRadius), Is.True);
                foreach (var p in positions) Assert.That(Vector3.Distance(p, allocator.Position(key)), Is.GreaterThan(u.SlotBodyRadius * 2f + allocator.Clearance));
                positions.Add(allocator.Position(key));
            }
            var fourth = Unit();
            for (var i = 0; i < 3; i++) Assert.That(allocator.CanReserve(fourth, new HexAttackSlotKey(c, i), fourth.SlotBodyRadius, out _), Is.False);
            Assert.That(allocator.CountInCell(c), Is.EqualTo(3));
        }

        [TestCase(115f, 20f, 0f, 0f, 5750)]
        [TestCase(115f, 20f, 40f, 0f, 1917)]
        [TestCase(115f, 20f, 40f, 1f, 1250)]
        [TestCase(115f, 20f, 40f, 4f, 0)]
        public void ActiveAttackDamage_OnlyDiscountsRemainingHpAfterArrival(float hp, float own, float active, float approach, int expected)
        {
            var method = typeof(HexCastleAssaultWorld).GetMethod("EstimateRemainingDestructionMilliseconds", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method.Invoke(null, new object[] { hp, own, active, approach }), Is.EqualTo(expected));
        }

        [TestCase(true, .75f, 34012, 23969, false)]
        [TestCase(false, .75f, 34012, 23969, true)]
        [TestCase(false, 1f, 34012, 23969, false)]
        [TestCase(false, 2f, 10000, 9500, true)]
        [TestCase(true, 2f, 10000, 9500, false)]
        public void AdvanceStability_DoesNotProtectFailedApproach(bool recovering, float sinceSwitch, int currentMs, int bestMs, bool expected)
        {
            var method = typeof(HexCastleAssaultWorld).GetMethod("ShouldRetainCurrentAdvance", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method.Invoke(null, new object[] { recovering, sinceSwitch, currentMs, bestMs }), Is.EqualTo(expected));
        }

        [Test] public void ActiveAttackDps_RequiresOccupiedLeaseActualPositionAndAttackState()
        {
            var attacker = Unit(new HexCoordinates(6, 0)); attacker.RefreshStrategicDecision();
            for (var i = 0; i < 200 && attacker.ExecutionState != HexAssaultExecutionState.Attacking; i++) Tick(attacker);
            Assert.That(attacker.ExecutionState, Is.EqualTo(HexAssaultExecutionState.Attacking));
            var target = attacker.CurrentTarget.Structure; Assert.That(target, Is.Not.Null);
            var observer = Unit();
            var refresh = typeof(HexCastleAssaultWorld).GetMethod("RefreshActiveAttackDps", Hidden);
            var query = typeof(HexCastleAssaultWorld).GetMethod("ActiveAttackDps", Hidden);
            float Read(HexCastleAssaultUnit selecting)
            {
                refresh.Invoke(world, new object[] { selecting });
                return (float)query.Invoke(world, new object[] { target, 0f });
            }
            Assert.That(Read(observer), Is.EqualTo(attacker.EstimatedDamagePerSecond));
            Assert.That(query.Invoke(world, new object[] { target, 1f }), Is.EqualTo(attacker.EstimatedDamagePerSecond));
            Assert.That(query.Invoke(world, new object[] { target, 1.001f }), Is.EqualTo(0f), "먼 도착까지 동료 화력을 낙관적으로 예측하지 않음");
            Assert.That(Read(attacker), Is.Zero, "자기 DPS 중복 합산 금지");
            var position = attacker.transform.position; attacker.transform.position += Vector3.right * 50f;
            Assert.That(Read(observer), Is.Zero, "먼 Commit이나 자리 예약만으로 합산 금지");
            attacker.transform.position = position;
            Set(attacker, "<ExecutionState>k__BackingField", HexAssaultExecutionState.Traversing);
            Assert.That(Read(observer), Is.Zero, "이동 중 병력 합산 금지");
            Set(attacker, "<ExecutionState>k__BackingField", HexAssaultExecutionState.Attacking);
            world.SlotAllocator.Cancel(attacker, attacker.CurrentSlotLease, true);
            Assert.That(Read(observer), Is.Zero, "해제된 자리 합산 금지");
        }
        [Test] public void DifferentTargets_StillCannotOwnSamePhysicalSlot()
        {
            var a = Unit(); var b = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); Reserve(a, key);
            Assert.That(world.SlotAllocator.TryCommit(b, Wall(5, -1), key, b.SlotBodyRadius, 1f, world.OccupancyRevision, false, out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(HexAssaultFailureReason.SlotFull));
        }
        [Test] public void FailedReplacement_PreservesPreviousReservation()
        {
            var a = Unit(); var b = Unit(); var first = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var second = new HexAttackSlotKey(new HexCoordinates(6, 0), 1);
            var old = Reserve(a, first); Reserve(b, second);
            Assert.That(world.SlotAllocator.TryCommit(a, Wall(), second, a.SlotBodyRadius, 1f, world.OccupancyRevision, false, out _, out _), Is.False);
            Assert.That(world.SlotAllocator.Owns(a, old), Is.True);
        }
        [Test] public void SameLeaseRefresh_DoesNotChangeOwnershipVersion()
        {
            var u = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var a = Reserve(u, key); var version = world.OccupancyRevision; var b = Reserve(u, key);
            Assert.That(a, Is.EqualTo(b)); Assert.That(world.OccupancyRevision, Is.EqualTo(version));
        }
        [Test] public void StaleOccupancyRevision_RejectsCommitWithoutChangingOwner()
        {
            var a = Unit(); var b = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var revision = world.OccupancyRevision;
            Reserve(a, key);
            Assert.That(world.SlotAllocator.TryCommit(b, Wall(), new HexAttackSlotKey(new HexCoordinates(6, 0), 1), b.SlotBodyRadius, 1f, revision, false, out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(HexAssaultFailureReason.PlanVersionChanged));
        }
        [Test] public void StaleCancellation_CannotCancelNewLease()
        {
            var u = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var old = Reserve(u, key);
            var next = Reserve(u, key, Wall(5, -1));
            world.SlotAllocator.Cancel(u, old);
            Assert.That(world.SlotAllocator.Owns(u, next), Is.True);
        }
        [Test] public void NewWorldEpoch_DoesNotAcceptOldLease()
        {
            var u = Unit(); var lease = Reserve(u, new HexAttackSlotKey(new HexCoordinates(6, 0), 0));
            var other = new HexAttackSlotAllocator(1f, Vector3.zero);
            Assert.That(other.WorldEpoch, Is.Not.EqualTo(lease.WorldEpoch)); Assert.That(other.Owns(u, lease), Is.False);
        }
        [Test] public void OccupiedSlot_IsNotReleasedUntilBodyLeavesAfterTargetDeath()
        {
            var a = Unit(); var b = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var lease = Reserve(a, key);
            a.transform.position = world.SlotAllocator.Position(key); Assert.That(world.SlotAllocator.Arrive(a, lease), Is.True);
            var target = Wall(); target.Structure.ApplyDamage(10000f, Vector3.zero);
            Assert.That(world.SlotAllocator.State(lease), Is.EqualTo(HexSlotLeaseState.Departing));
            Assert.That(world.SlotAllocator.CanReserve(b, key, b.SlotBodyRadius, out _), Is.False);
            a.transform.position = new HexCoordinates(7, -2).ToWorld(1f); world.SlotAllocator.Tick(Time.time);
            Assert.That(world.SlotAllocator.CanReserve(b, key, b.SlotBodyRadius, out _), Is.True);
        }
        [Test] public void DeadOwner_ReleasesItsSpace()
        {
            var a = Unit(); var b = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); Reserve(a, key);
            a.ApplyDamage(10000f); world.SlotAllocator.Tick(Time.time);
            Assert.That(world.SlotAllocator.CanReserve(b, key, b.SlotBodyRadius, out _), Is.True);
        }
        [Test] public void ReleasedSlot_AdvancesAvailabilityRevisionForTemporaryWorkerWakeup()
        {
            var unit = Unit();
            var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0);
            Reserve(unit, key);
            var before = world.SlotAllocator.AvailabilityRevision;

            world.SlotAllocator.ReleaseOwner(unit, true);

            Assert.That(world.SlotAllocator.AvailabilityRevision, Is.GreaterThan(before));
        }
        [Test] public void ReconfiguredOwner_DoesNotInheritPooledLease()
        {
            var u = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var old = Reserve(u, key); var generation = u.RuntimeGeneration;
            u.ShutdownRuntime();
            u.ConfigureForPartyUnit(world, new HexCoordinates(7, 0), cells, 1f, Vector3.zero,
                new BattleUnitSnapshot("argo_01", new UnitStatsSnapshot { maxHealth = 100f, damage = 20f, attackInterval = 1f, attackRange = 1.1f, moveSpeed = 3f }));
            Assert.That(u.RuntimeGeneration, Is.GreaterThan(generation)); Assert.That(world.SlotAllocator.Owns(u, old), Is.False);
            var current = Reserve(u, key); world.SlotAllocator.Cancel(u, old);
            Assert.That(world.SlotAllocator.Owns(u, current), Is.True);
        }
        [Test] public void LargeBody_UsesExclusiveCentreAndBlocksNormalSlots()
        {
            var a = Unit(); a.ConfigureOccupancyRadius(0.65f); var b = Unit(); var c = new HexCoordinates(6, 0);
            Assert.That(world.SlotAllocator.NeedsExclusive(a.SlotBodyRadius), Is.True);
            Reserve(a, new HexAttackSlotKey(c, 3));
            for (var i = 0; i < 3; i++) Assert.That(world.SlotAllocator.CanReserve(b, new HexAttackSlotKey(c, i), b.SlotBodyRadius, out _), Is.False);
        }
        [Test] public void Arrival_RequiresRealWorldPosition()
        {
            var u = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var lease = Reserve(u, key);
            Assert.That(world.SlotAllocator.Arrive(u, lease), Is.False);
            u.transform.position = world.SlotAllocator.Position(key); Assert.That(world.SlotAllocator.Arrive(u, lease), Is.True);
        }
        [Test] public void EvaluatingCandidates_DoesNotAcquireSpaceOrJoinCohort()
        {
            var u = Unit(); var version = world.OccupancyRevision; var count = world.ActiveCohortCount;
            world.ApproachPlanner.BeginDecision(u);
            Assert.That(world.ApproachPlanner.TryPlan(u, Wall(), 1, k => world.CanAttackFromPosition(u, Wall(), k), false, out var plan, out var reason), Is.True, reason.ToString());
            Assert.That(plan.Cells.First(), Is.EqualTo(u.CurrentCoordinates));
            Assert.That(world.OccupancyRevision, Is.EqualTo(version)); Assert.That(world.ActiveCohortCount, Is.EqualTo(count));
        }
        [Test] public void AcceptedPlan_HasActualSlotAndFinalPosition()
        {
            var u = Unit(); u.RefreshStrategicDecision();
            Assert.That(u.CurrentTarget.IsValid, Is.True); Assert.That(u.CurrentSlotLease.IsValid, Is.True);
            Assert.That(u.ActivePlan.FinalApproach.Count, Is.GreaterThan(0));
            var end = u.ActivePlan.FinalApproach.Last(); var expected = world.SlotAllocator.Position(u.CurrentSlotLease.Key);
            Assert.That(end.x, Is.EqualTo(expected.x).Within(0.0001f)); Assert.That(end.z, Is.EqualTo(expected.z).Within(0.0001f));
        }
        [Test] public void FinalAlignment_EndsAtReservedSlotNotCellCentre()
        {
            var u = Unit(); u.RefreshStrategicDecision();
            for (var i = 0; i < 200 && !world.SlotAllocator.IsOccupied(u, u.CurrentSlotLease); i++) Tick(u);
            Assert.That(world.SlotAllocator.IsOccupied(u, u.CurrentSlotLease), Is.True);
            var expected = world.SlotAllocator.Position(u.CurrentSlotLease.Key); var actual = u.transform.position; actual.y = expected.y;
            Assert.That(Vector3.Distance(actual, expected), Is.LessThanOrEqualTo(0.06f));
            Assert.That(actual, Is.Not.EqualTo(u.CurrentCoordinates.ToWorld(1f)));
        }
        [Test] public void SameTargetReplan_PreservesPathProgressAndLease()
        {
            var u = Unit(); u.RefreshStrategicDecision();
            for (var i = 0; i < 15; i++) Tick(u);
            var lease = u.CurrentSlotLease; var position = u.transform.position; var index = (int)Field(u, "pathIndex");
            u.RequestStrategicDecision(true); Assert.That(u.NeedsStrategicDecision, Is.True); u.RefreshStrategicDecision();
            Assert.That(u.NeedsStrategicDecision, Is.False);
            Assert.That(u.CurrentSlotLease, Is.EqualTo(lease)); Assert.That((int)Field(u, "pathIndex"), Is.EqualTo(index)); Assert.That(u.transform.position, Is.EqualTo(position));
        }
        [Test] public void LocalFallback_DoesNotReplaceStrategicCommitment()
        {
            var u = Unit(); u.RefreshStrategicDecision();
            var committed = u.CommittedTarget;
            var fallback = Wall(5, -1);
            var temporary = new HexCastleAssaultDecision(fallback, new[] { u.CurrentCoordinates }, u.CurrentCoordinates,
                u.RouteId, u.RouteSector, world.TopologyVersion, HexCastleAssaultIntentKind.LocalFallback);

            typeof(HexCastleAssaultUnit).GetMethod("ApplyPlan", Hidden).Invoke(u, new object[] { temporary });

            Assert.That(u.CurrentTarget.InstanceId, Is.EqualTo(fallback.InstanceId));
            Assert.That(u.CurrentIntent, Is.EqualTo(HexCastleAssaultIntentKind.LocalFallback));
            Assert.That(u.CommittedTarget.InstanceId, Is.EqualTo(committed.InstanceId),
                "자리 대기 중 주변 구조물을 치더라도 원래 돌파 전선을 버리면 안 됩니다.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LocalWork_KeepsValidApproachWhenStrategicTargetChanges(bool destroyCommitment)
        {
            var unit = Unit(); unit.RefreshStrategicDecision();
            var committed = unit.CommittedTarget; var fallback = Wall(5, -1);
            world.ApproachPlanner.BeginDecision(unit);
            Assert.That(world.ApproachPlanner.TryPlan(unit, fallback, 1,
                slot => world.CanAttackFromPosition(unit, fallback, slot), false, out var approach, out _), Is.True);
            Assert.That(world.SlotAllocator.TryCommit(unit, fallback, approach.Slot, unit.SlotBodyRadius, approach.Cost,
                approach.OccupancyRevision, false, out var lease, out _), Is.True);
            var temporary = new HexCastleAssaultDecision(fallback, approach.Cells, approach.Slot.Cell,
                unit.RouteId, unit.RouteSector, world.TopologyVersion, HexCastleAssaultIntentKind.LocalFallback,
                slotLease: lease, finalApproach: approach.FinalPath);
            typeof(HexCastleAssaultUnit).GetMethod("ApplyPlan", Hidden).Invoke(unit, new object[] { temporary });
            if (destroyCommitment) committed.Structure.ApplyDamage(10000f, Vector3.zero);
            else committed.Structure.ApplyDamage(60f, Vector3.zero);
            unit.RequestStrategicDecision(true);
            var result = world.ResolveDecision(unit);
            Assert.That(result.Status, Is.EqualTo(HexAssaultDecisionStatus.Accepted));
            Assert.That(result.Plan.Target.InstanceId, Is.EqualTo(fallback.InstanceId));
            Assert.That(result.Plan.Intent, Is.EqualTo(HexCastleAssaultIntentKind.LocalFallback));
        }

        [Test] public void LocalWork_BeforeDistantTravelRequiresCompleteJobAndPreservesCommitment()
        {
            var u = Unit(); u.RefreshStrategicDecision();
            var committed = u.CommittedTarget;
            var assignments = (Dictionary<int, HexCastleAssaultCohortAssignment>)Field(world, "unitCohorts");
            var cohort = assignments[u.GetInstanceID()].CohortId;
            world.ApproachPlanner.BeginDecision(u);
            var method = typeof(HexCastleAssaultWorld).GetMethod("TryResolveLocalFallback", Hidden);
            var denied = new object[] { u, default(HexCastleAssaultDecision), 1000 };
            Assert.That(method.Invoke(world, denied), Is.False);
            var accepted = new object[] { u, default(HexCastleAssaultDecision), 30000 };
            Assert.That(method.Invoke(world, accepted), Is.True);
            var plan = (HexCastleAssaultDecision)accepted[1];
            Assert.That(plan.Intent, Is.EqualTo(HexCastleAssaultIntentKind.LocalFallback));
            typeof(HexCastleAssaultUnit).GetMethod("ApplyPlan", Hidden).Invoke(u, new object[] { plan });
            Assert.That(u.CommittedTarget.InstanceId, Is.EqualTo(committed.InstanceId));
            Assert.That(assignments[u.GetInstanceID()].CohortId, Is.EqualTo(cohort));
            Set(u, "<RouteCostReviewRequested>k__BackingField", false);
            var continued = world.ResolveDecision(u);
            Assert.That(continued.Plan.Intent, Is.EqualTo(HexCastleAssaultIntentKind.LocalFallback),
                "사건 없는 정기 판단으로 진행 중인 지역 작업을 즉시 버리지 않음");
        }
        [Test] public void SameSlotNewApproach_ReplacesExecutionPlan()
        {
            var u = Unit(); u.RefreshStrategicDecision(); var current = u.ActivePlan;
            var revisedApproach = new List<Vector3> { u.transform.position + Vector3.left * 0.1f };
            revisedApproach.AddRange(current.FinalApproach);
            var revised = new HexCastleAssaultDecision(current.Target, current.MovementPath, current.Approach,
                current.RouteId, current.SectorId, current.TopologyVersion, current.Intent, current.SupportAction,
                current.SlotLease, revisedApproach, current.PlanKind);
            var generation = u.PlanGeneration;
            typeof(HexCastleAssaultUnit).GetMethod("ApplyPlan", Hidden).Invoke(u, new object[] { revised });
            Assert.That(u.PlanGeneration, Is.GreaterThan(generation));
            Assert.That(u.ActivePlan.FinalApproach, Is.EqualTo(revisedApproach));
        }
        [Test] public void TargetDeathAndFailedRefresh_PreserveSafeMotion()
        {
            var u = Unit(); u.RefreshStrategicDecision(); Tick(u);
            var before = u.transform.position; Assert.That(u.IsInSafeMotion, Is.True);
            u.CurrentTarget.Structure.ApplyDamage(10000f, Vector3.zero);
            Set(world, "configured", false); u.RequestStrategicDecision(true); u.RefreshStrategicDecision();
            Assert.That(u.ConsecutiveDecisionFailures, Is.Zero);
            for (var i = 0; i < 200 && u.IsInSafeMotion; i++) Tick(u);
            Assert.That(u.transform.position, Is.Not.EqualTo(before)); Assert.That(u.IsInSafeMotion, Is.False);
            Assert.That(u.NeedsStrategicDecision, Is.True); u.RefreshStrategicDecision();
            Assert.That(u.ConsecutiveDecisionFailures, Is.EqualTo(1));
            Assert.That(u.LastDecisionReason, Is.EqualTo(HexAssaultFailureReason.InvalidInput));
            Assert.That(u.ExecutionState, Is.EqualTo(HexAssaultExecutionState.Holding));
        }
        [Test] public void TargetDestroyedDuringFinalAlignment_KeepsDepartingBodyOccupied()
        {
            var u = Unit(); var contender = Unit(); u.RefreshStrategicDecision(); var lease = u.CurrentSlotLease;
            var destination = world.SlotAllocator.Position(lease.Key);
            var departingDistance = u.SlotBodyRadius * 2f + world.SlotAllocator.Clearance;
            for (var i = 0; i < 200 && Vector3.Distance(u.transform.position, destination) >= departingDistance; i++) Tick(u);
            Assert.That(u.ExecutionState, Is.EqualTo(HexAssaultExecutionState.Aligning));
            u.CurrentTarget.Structure.ApplyDamage(10000f, Vector3.zero);
            Assert.That(world.SlotAllocator.State(lease), Is.EqualTo(HexSlotLeaseState.Departing));
            Assert.That(world.SlotAllocator.CanReserve(contender, lease.Key, contender.SlotBodyRadius, out _), Is.False);
        }
        [Test] public void MovementLock_DoesNotBlockAttackAtOccupiedSlot()
        {
            var u = Unit(); u.RefreshStrategicDecision();
            for (var i = 0; i < 200 && !world.SlotAllocator.IsOccupied(u, u.CurrentSlotLease); i++) Tick(u);
            Assert.That(world.SlotAllocator.IsOccupied(u, u.CurrentSlotLease), Is.True);
            var target = u.CurrentTarget; var health = target.CurrentHealth;
            Set(u, "trapMovementLockRemaining", 1f); Set(u, "nextAttackTime", 0f); Tick(u);
            Assert.That(target.CurrentHealth, Is.LessThan(health));
        }
        [Test] public void ThreeUnits_ReachDistinctSlotsAndEachDealsOneHit()
        {
            var units = new[] { Unit(unitId: "castley_01"), Unit(unitId: "castley_01"), Unit(unitId: "castley_01") };
            foreach (var u in units) u.RefreshStrategicDecision();
            Assert.That(units.Select(u => u.CurrentTarget.InstanceId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(units.Select(u => u.CurrentSlotLease.Key).Distinct().Count(), Is.EqualTo(3));
            for (var i = 0; i < 400 && !units.All(u => world.SlotAllocator.IsOccupied(u, u.CurrentSlotLease)); i++)
                foreach (var u in units) Tick(u);
            Assert.That(units.All(u => world.SlotAllocator.IsOccupied(u, u.CurrentSlotLease)), Is.True,
                string.Join(" | ", units.Select(u => u.ExecutionState + ":" + u.LastDecisionReason + ":" + u.transform.position)));
            var target = units[0].CurrentTarget; var health = target.CurrentHealth;
            foreach (var u in units) { Set(u, "nextAttackTime", 0f); Tick(u); }
            Assert.That(target.CurrentHealth, Is.EqualTo(health - 60f).Within(0.001f));
        }
        [Test] public void OverlappedBody_CanEscapeOutwardButCannotMoveDeeper()
        {
            var blocker = Unit(); var mover = Unit(); var key = new HexAttackSlotKey(new HexCoordinates(6, 0), 0);
            var lease = Reserve(blocker, key); var centre = world.SlotAllocator.Position(key);
            blocker.transform.position = centre; Assert.That(world.SlotAllocator.Arrive(blocker, lease), Is.True);
            var start = centre + Vector3.right * 0.1f;
            Assert.That(world.SlotAllocator.BodySegmentClear(mover, start, start + Vector3.right, mover.SlotBodyRadius), Is.True);
            Assert.That(world.SlotAllocator.BodySegmentClear(mover, start, centre, mover.SlotBodyRadius), Is.False);
        }
        [Test] public void DepartingBody_UsesItsLivePositionForNewReservations()
        {
            var departing = Unit(); var contender = Unit();
            var oldKey = new HexAttackSlotKey(new HexCoordinates(6, 0), 0); var lease = Reserve(departing, oldKey);
            departing.transform.position = world.SlotAllocator.Position(oldKey); Assert.That(world.SlotAllocator.Arrive(departing, lease), Is.True);
            world.SlotAllocator.Cancel(departing, lease);
            var liveKey = new HexAttackSlotKey(new HexCoordinates(7, 0), 0); departing.transform.position = world.SlotAllocator.Position(liveKey);
            Assert.That(world.SlotAllocator.CanReserve(contender, oldKey, contender.SlotBodyRadius, out _), Is.True);
            Assert.That(world.SlotAllocator.CanReserve(contender, liveKey, contender.SlotBodyRadius, out _), Is.False);
        }
        [Test] public void MissingNextCell_IsRejectedRatherThanWalkedOutsideBoard()
        {
            var u = Unit(); var pass = typeof(HexCastleAssaultUnit).GetMethod("CanAssaultTraverse", Hidden);
            Assert.That((bool)pass.Invoke(u, new object[] { new HexCoordinates(50, 0) }), Is.False);
        }
        [Test] public void PlanFailure_BackoffIsNotBypassedByInvalidTarget()
        {
            var u = Unit(); Set(world, "configured", false); u.RefreshStrategicDecision();
            Assert.That(u.RetryNotBefore, Is.GreaterThan(Time.time)); Assert.That(u.NeedsStrategicDecision, Is.False);
            Tick(u); Assert.That(u.NeedsStrategicDecision, Is.False);
        }
        [Test] public void CostSnapshot_PathSumEqualsReportedCost()
        {
            var n = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(n.TryResolveRoute(new HexCoordinates(7, 0), HexCastleAssaultRoutePolicy.Balanced, 2, 20f, 3f, 1, out var plan), Is.True);
            Assert.That(plan.EntryCosts.Sum(), Is.EqualTo(plan.TotalCost).Within(0.001f));
        }
        [Test] public void CostSnapshot_SameBandDamageDoesNotMixLiveAndCachedCosts()
        {
            var n = new HexCastleAssaultNavigationSnapshot(cells, 1f); var start = new HexCoordinates(7, 0);
            n.TryResolveRoute(start, HexCastleAssaultRoutePolicy.Balanced, 2, 20f, 3f, 1, out var before);
            cells[before.FirstObstacle].ApplyDamage(10f, Vector3.zero);
            n.TryResolveRoute(start, HexCastleAssaultRoutePolicy.Balanced, 2, 20f, 3f, 1, out var after);
            Assert.That(n.LastQueryReused, Is.True); Assert.That(after.Path, Is.EqualTo(before.Path));
            Assert.That(after.EntryCosts.Sum(), Is.EqualTo(after.TotalCost).Within(0.001f)); Assert.That(after.TotalCost, Is.EqualTo(before.TotalCost));
        }
        [Test] public void CostSnapshot_LruCapacityIsBounded()
        {
            var n = new HexCastleAssaultNavigationSnapshot(cells, 1f) { MaxCachedFields = 3 };
            for (var i = 1; i < 10; i++) n.TryResolveRoute(new HexCoordinates(7, 0), HexCastleAssaultRoutePolicy.Balanced, 2, i * 10f, 3f, 1, out _);
            Assert.That(n.CachedFieldCount, Is.EqualTo(3));
        }
        [Test] public void CostSnapshot_InvalidNumericInputFailsClosed()
        {
            var n = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(n.TryResolveRoute(new HexCoordinates(7, 0), HexCastleAssaultRoutePolicy.Balanced, 2, float.NaN, 3f, 1, out _), Is.False);
            Assert.That(n.TryResolveRoute(new HexCoordinates(7, 0), HexCastleAssaultRoutePolicy.Balanced, 2, 20f, float.PositiveInfinity, 1, out _), Is.False);
        }
        [Test] public void SpatialChanges_DoNotInvalidateSharedCostField()
        {
            var u = Unit(); u.RefreshStrategicDecision(); var count = world.CachedRouteFieldCount; var topo = world.TopologyRevision; var cost = world.CostRevision;
            Reserve(Unit(), new HexAttackSlotKey(new HexCoordinates(6, -2), 0));
            Assert.That(world.CachedRouteFieldCount, Is.EqualTo(count)); Assert.That(world.TopologyRevision, Is.EqualTo(topo)); Assert.That(world.CostRevision, Is.EqualTo(cost));
        }
        [Test] public void WorldChange_RequestsImmediateReplanOnlyForNearbyUnit()
        {
            var near = Unit(new HexCoordinates(7, 0));
            var far = Unit(new HexCoordinates(-7, 0));
            foreach (var unit in new[] { near, far })
            {
                Set(unit, "strategicDecisionRequested", false);
                Set(unit, "nextAwarenessAt", Time.time + 100f);
                Set(unit, "retryNotBefore", Time.time + 100f);
            }

            var changed = new HexCoordinates(5, 0);
            near.NotifyWorldChange(changed, true, false);
            far.NotifyWorldChange(changed, true, false);

            Assert.That((bool)Field(near, "strategicDecisionRequested"), Is.True);
            Assert.That(near.RetryNotBefore, Is.Zero);
            Assert.That((bool)Field(far, "strategicDecisionRequested"), Is.False);
            Assert.That(far.RetryNotBefore, Is.GreaterThan(Time.time));
        }
        [Test] public void HealthBandChangesCostNotTopologyRevision()
        {
            var topo = world.TopologyRevision; var cost = world.CostRevision;
            Wall().Structure.ApplyDamage(50f, Vector3.zero);
            Assert.That(world.TopologyRevision, Is.EqualTo(topo)); Assert.That(world.CostRevision, Is.GreaterThan(cost));
        }
        [Test] public void ClosedWallGeometry_RejectsCrossingButAllowsOutsideSegment()
        {
            var a = new Vector3(-2, 0, 0); var b = new Vector3(2, 0, 0);
            Assert.That(HexAssaultApproachPlanner.IntersectsHex(a, b, Vector3.zero, 1f), Is.True);
            Assert.That(HexAssaultApproachPlanner.IntersectsHex(new Vector3(2, 0, 2), new Vector3(3, 0, 3), Vector3.zero, 1f), Is.False);
        }
        [Test] public void NoStrategicRoute_StillAttacksReachableLocalWall()
        {
            foreach (var c in cells.Keys.Where(c => c.DistanceFromOrigin == 4).ToArray()) cells.Remove(c);
            var u = Unit();
            var nav = new HexCastleAssaultNavigationSnapshot(cells, 1f);
            Assert.That(nav.TryResolveFrontRoute(u.CurrentCoordinates,
                u.EstimatedDamagePerSecond, u.EffectiveMoveSpeed, world.TopologyVersion, out _), Is.False);
            var result = world.ResolveDecision(u);
            Assert.That(result.Status, Is.EqualTo(HexAssaultDecisionStatus.Accepted));
            Assert.That(result.Plan.Target.IsValid, Is.True);
            Assert.That(result.Plan.Target.Coordinates.DistanceTo(u.CurrentCoordinates), Is.LessThanOrEqualTo(4));
            Assert.That(result.Plan.Intent, Is.EqualTo(HexCastleAssaultIntentKind.LocalFallback));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void AttackSocketAcrossCastle_DoesNotSendUnitBackOutsideItsAdvanceFront(bool shared)
        {
            foreach (var cell in cells.Values.Where(c => c.Coordinates.DistanceFromOrigin == 5))
                cell.ApplyDamage(1000, Vector3.zero);
            cells[new HexCoordinates(0, 3)].ApplyDamage(1000, Vector3.zero);
            cells.Remove(new HexCoordinates(3, 1));
            cells.Remove(new HexCoordinates(4, -1));
            var unit = Unit(new HexCoordinates(4, 0));
            var planner = world.ApproachPlanner;
            if (shared) planner.BeginDecision(unit, 123, unit); else planner.BeginDecision(unit);
            var opposite = new HexCoordinates(2, 0);
            var openPath = (HexCoordinates[])typeof(HexAssaultApproachPlanner)
                .GetMethod("Reconstruct", Hidden).Invoke(planner, new object[] { opposite });
            Assert.That(openPath.Length, Is.GreaterThan(0), "반대편 자리까지 열린 우회 경로가 실제로 존재해야 한다.");
            Assert.That(openPath.Any(c => c.DistanceFromOrigin > 4), Is.True);
            Assert.That(planner.TryPlan(unit, Wall(3, 0), 1, slot => slot.Cell == opposite,
                false, out var detour, out _), Is.False, "빈자리가 있어도 외곽으로 다시 나가는 공격 계획은 거부한다.");
            Assert.That(detour, Is.Null);
            Assert.That(planner.TryPlan(unit, Wall(3, 0), 1, slot => slot.Cell == unit.CurrentCoordinates,
                false, out var local, out _), Is.True, "같은 전면의 실제 접근과 자리는 유지한다.");
            Assert.That(local.Cells.All(c => c.DistanceFromOrigin <= 4), Is.True);
        }
        [Test] public void DefaultDecision_IsInvalidNotAnException() => Assert.That(default(HexCastleAssaultDecision).IsValid, Is.False);
    }
}
