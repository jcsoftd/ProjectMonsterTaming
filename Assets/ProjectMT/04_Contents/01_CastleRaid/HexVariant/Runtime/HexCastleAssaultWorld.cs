using System;
using System.Collections.Generic;
using System.Linq;
using ProjectMT.Shared.Combat;
using UnityEngine;
using Unity.Profiling;

namespace ProjectMT.Contents.CastleRaidHex
{
    public enum HexCastleAssaultTargetKind
    {
        None = 0,
        Structure = 1,
        Defender = 2,
        Palace = 3,
        Ally = 4
    }

    public enum HexCastleAssaultIntentKind
    {
        None = 0,
        InitialBreach = 1,
        Progress = 2,
        Opportunity = 3,
        Specialist = 4,
        Threat = 5,
        Support = 6,
        Palace = 7,
        LocalFallback = 8,
        HoldPosition = 9
    }

    public readonly struct HexCastleAssaultTarget
    {
        public HexCastleAssaultTarget(HexCastleCellRuntime structure, bool palace)
        {
            Structure = structure;
            Defender = null;
            Ally = null;
            Kind = palace ? HexCastleAssaultTargetKind.Palace : HexCastleAssaultTargetKind.Structure;
        }

        public HexCastleAssaultTarget(HexCastleGarrisonUnit defender)
        {
            Structure = null;
            Defender = defender;
            Ally = null;
            Kind = HexCastleAssaultTargetKind.Defender;
        }

        public HexCastleAssaultTarget(HexCastleAssaultUnit ally)
        {
            Structure = null;
            Defender = null;
            Ally = ally;
            Kind = HexCastleAssaultTargetKind.Ally;
        }

        public HexCastleAssaultTargetKind Kind { get; }
        public HexCastleCellRuntime Structure { get; }
        public HexCastleGarrisonUnit Defender { get; }
        public HexCastleAssaultUnit Ally { get; }
        public bool IsValid => Kind != HexCastleAssaultTargetKind.None && IsAlive;
        public bool IsAlive => Structure != null && Structure.IsAlive || Defender != null && Defender.IsAlive ||
                               Ally != null && Ally.IsAlive;
        public HexCoordinates Coordinates => Structure != null
            ? Structure.Coordinates
            : Defender != null
                ? Defender.Coordinates
                : Ally != null
                    ? Ally.CurrentCoordinates
                    : default;
        public int InstanceId => Structure != null
            ? Structure.GetInstanceID()
            : Defender != null
                ? Defender.GetInstanceID()
                : Ally != null
                    ? Ally.GetInstanceID()
                    : 0;
        public float CurrentHealth => Structure != null
            ? Structure.CurrentHealth
            : Defender?.Health == null
                ? Ally != null ? Ally.CurrentHealth : 0f
                : Defender.Health.CurrentHealth;
        public float MaxHealth => Structure != null
            ? Structure.MaxHealth
            : Defender?.Health == null
                ? Ally != null ? Ally.MaxHealth : 0f
                : Defender.Health.MaxHealth;
        public Vector3 Position => Structure != null
            ? Structure.transform.position
            : Defender != null
                ? Defender.transform.position
                : Ally != null
                    ? Ally.transform.position
                    : Vector3.zero;
    }

    public readonly struct HexCastleAssaultDecision
    {
        public HexCastleAssaultDecision(
            HexCastleAssaultTarget target,
            IReadOnlyList<HexCoordinates> movementPath,
            HexCoordinates approach,
            int routeId,
            int sectorId,
            int topologyVersion,
            HexCastleAssaultIntentKind intent,
            HexCastleAssaultSupportAction supportAction = HexCastleAssaultSupportAction.None,
            HexSlotLease slotLease = default, IReadOnlyList<Vector3> finalApproach = null,
            HexAssaultPlanKind planKind = HexAssaultPlanKind.Attack)
        {
            Target = target;
            MovementPath = movementPath ?? Array.Empty<HexCoordinates>();
            Approach = approach;
            RouteId = routeId;
            SectorId = sectorId;
            TopologyVersion = topologyVersion;
            Intent = intent;
            SupportAction = supportAction;
            SlotLease = slotLease;
            FinalApproach = finalApproach ?? Array.Empty<Vector3>();
            PlanKind = planKind;
            TargetCell = target.Coordinates;
        }

        public HexCastleAssaultTarget Target { get; }
        public IReadOnlyList<HexCoordinates> MovementPath { get; }
        public HexCoordinates Approach { get; }
        public int RouteId { get; }
        public int SectorId { get; }
        public int TopologyVersion { get; }
        public HexCastleAssaultIntentKind Intent { get; }
        public HexCastleAssaultSupportAction SupportAction { get; }
        public HexSlotLease SlotLease { get; }
        public IReadOnlyList<Vector3> FinalApproach { get; }
        public HexAssaultPlanKind PlanKind { get; }
        public HexCoordinates TargetCell { get; }
        public bool IsValid => MovementPath != null && MovementPath.Count > 0 &&
            (PlanKind == HexAssaultPlanKind.Hold || PlanKind == HexAssaultPlanKind.Move || Target.IsValid);
    }

    public readonly struct HexCastleAssaultCohortAssignment
    {
        public HexCastleAssaultCohortAssignment(int cohortId, int routeId, int sectorId)
        {
            CohortId = cohortId;
            RouteId = routeId;
            SectorId = sectorId;
        }

        public int CohortId { get; }
        public int RouteId { get; }
        public int SectorId { get; }
    }

    [DisallowMultipleComponent]
    public sealed class HexCastleAssaultWorld : MonoBehaviour // Hex 공격 AI의 전략 판단과 예약을 조율한다
    {
        private const int StrategicDecisionBudgetPerFrame = 1;
        private const int MaximumOuterBreachRoutes = 4;
        private const int MaximumCohortSize = 6;
        private const int CohortJoinDistanceCells = 3;
        private const int SpecializedTargetRadiusCells = 4;
        private const int SharedThreatRadiusCells = 4;
        private const int ThreatSuppressorRadiusCells = 6;
        private const int SupportSearchRadiusCells = 8;
        private const float ThreatRecordSeconds = 3.5f;
        private const float TentativeSupportClaimSeconds = 0.9f;
        private const float SupportClaimPenalty = 2f;
        private const int HealthRouteBandCount = 4;
        private const int CohortBreachJoinToleranceMilliseconds = 1250;
        private const float CohortBreachJoinToleranceRatio = 0.12f;
        private const int MaximumRankedOverflowTargets = 2;
        private const int LocalWorkRadiusCells = 6;
        private const int LocalWorkMovementBandMilliseconds = 250;
        private const float GeneralOpportunityChance = 0.35f;

        private readonly struct BreachTimeEstimate
        {
            public BreachTimeEstimate(
                HexCastleCellRuntime target,
                int movementMilliseconds,
                int destructionMilliseconds,
                float expectedDamagePerSecond)
            {
                Target = target;
                MovementMilliseconds = movementMilliseconds;
                DestructionMilliseconds = destructionMilliseconds;
                ExpectedDamagePerSecond = expectedDamagePerSecond;
            }

            public HexCastleCellRuntime Target { get; }
            public int MovementMilliseconds { get; }
            public int DestructionMilliseconds { get; }
            public float ExpectedDamagePerSecond { get; }
            public int TotalMilliseconds => MovementMilliseconds + DestructionMilliseconds;
        }

        private sealed class CohortRecord
        {
            public int CohortId;
            public int RouteId;
            public int SectorId;
            public readonly List<HexCastleAssaultUnit> Members = new List<HexCastleAssaultUnit>(MaximumCohortSize);
            public HexCastleAssaultUnit Leader;
            public int MemberCount => Members.Count;
            public HexCastleAssaultRoutePlan SharedRoute;
            public int RouteVersion = 1;
        }

        private sealed class ThreatRecord
        {
            public HexCastleAssaultTarget Target;
            public int VictimId;
            public HexCoordinates VictimCoordinates;
            public float ReportedAt;
        }

        private readonly struct SupportClaimKey : IEquatable<SupportClaimKey>
        {
            public SupportClaimKey(int targetId, HexCastleAssaultSupportAction action)
            {
                TargetId = targetId;
                Action = action;
            }

            public int TargetId { get; }
            public HexCastleAssaultSupportAction Action { get; }

            public bool Equals(SupportClaimKey other)
            {
                return TargetId == other.TargetId && Action == other.Action;
            }

            public override bool Equals(object obj)
            {
                return obj is SupportClaimKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return TargetId * 397 ^ (int)Action;
                }
            }
        }

        private sealed class SupportClaimRecord
        {
            public int OwnerId;
            public float ExpiresAt;
        }

        private sealed class PassiveExposureRecord
        {
            public float Rate;
            public float Remaining;
        }

        private readonly struct ThreatClaim : IEquatable<ThreatClaim>
        {
            public ThreatClaim(int targetId, int responderId)
            {
                TargetId = targetId;
                ResponderId = responderId;
            }

            public int TargetId { get; }
            public int ResponderId { get; }

            public bool Equals(ThreatClaim other)
            {
                return TargetId == other.TargetId && ResponderId == other.ResponderId;
            }

            public override bool Equals(object obj)
            {
                return obj is ThreatClaim other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return TargetId * 397 ^ ResponderId;
                }
            }
        }

        private IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cells;
        private readonly List<HexCastleAssaultUnit> units = new List<HexCastleAssaultUnit>();
        private readonly Dictionary<int, float> activeAttackDps = new Dictionary<int, float>();
        private readonly List<CohortRecord> cohorts = new List<CohortRecord>();
        private readonly Dictionary<int, HexCastleAssaultCohortAssignment> unitCohorts =
            new Dictionary<int, HexCastleAssaultCohortAssignment>();
        private readonly Dictionary<ThreatClaim, float> pursuitRetryAfter = new Dictionary<ThreatClaim, float>();
        private HexAttackSlotAllocator slotAllocator;
        private HexAssaultApproachPlanner approachPlanner;
        private readonly HexAssaultTraceBuffer trace = new HexAssaultTraceBuffer();
        private readonly Dictionary<int, bool> observedBlocking = new Dictionary<int, bool>();
        private HexAssaultFailureReason lastCandidateFailure;
        private int topologyRevision = 1;
        private int costRevision = 1;
        public HexAttackSlotAllocator SlotAllocator => slotAllocator;
        public HexAssaultApproachPlanner ApproachPlanner => approachPlanner;
        public HexAssaultTraceBuffer Trace => trace;
        public int TopologyRevision => topologyRevision;
        public int CostRevision => costRevision;
        public int OccupancyRevision => slotAllocator?.Revision ?? 0;
        private readonly Dictionary<int, int> routeBreachTargets = new Dictionary<int, int>();
        private readonly Dictionary<int, HashSet<int>> breachRouteOwners =
            new Dictionary<int, HashSet<int>>();
        private readonly Dictionary<int, int> unitBreachRoutes = new Dictionary<int, int>();
        private readonly HashSet<int> outerBreachTargets = new HashSet<int>();
        private readonly HashSet<ThreatClaim> threatClaims = new HashSet<ThreatClaim>();
        private readonly List<ThreatRecord> threatRecords = new List<ThreatRecord>();
        private readonly Dictionary<SupportClaimKey, SupportClaimRecord> supportClaims =
            new Dictionary<SupportClaimKey, SupportClaimRecord>();
        private readonly Dictionary<int, int> unitSpawnOrders = new Dictionary<int, int>();
        private readonly Dictionary<int, int> cellHealthBands = new Dictionary<int, int>();
        private readonly Dictionary<int, PassiveExposureRecord> passiveExposures =
            new Dictionary<int, PassiveExposureRecord>();
        private readonly HashSet<int> passiveEnhancedDamageTargets = new HashSet<int>();
        private readonly List<int> expiredPassiveExposureKeys = new List<int>();
        private HexCastleAssaultNavigationSnapshot navigation;
        private HexCastleAssaultAIProfileCatalog profileCatalog;
        private HexCastleGarrisonWorld garrisonWorld;
        private ICombatFeedbackPlayer feedback;
        private HexCastleCellRuntime palaceCore;
        private int defenseLayerCount;
        private int topologyVersion = 1;
        private int observedSlotAvailabilityRevision;
        private int strategicCursor;
        private int cohortCursor;
        private readonly Dictionary<int, HexCoordinates> deploymentFronts = new Dictionary<int, HexCoordinates>();
        private readonly Dictionary<HexCoordinates, int> cohortJoinDistances = new Dictionary<HexCoordinates, int>();
        private readonly Queue<HexCoordinates> cohortJoinQueue = new Queue<HexCoordinates>();
        private int nextCohortId = 1;
        private int nextUnitSpawnOrder;
        private int stageSeed;
        private bool configured;

        public int TopologyVersion => topologyVersion;
        public int DefenseLayerCount => defenseLayerCount;
        public int ActiveUnitCount => units.Count(value => value != null && value.IsAlive);
        public int ActiveCohortCount => cohorts.Count;
        public int ActiveOuterBreachRouteCount => outerBreachTargets.Count;
        public int ActiveBreachReservationOwnerCount => unitBreachRoutes.Count;
        public int ActiveSupportClaimCount => supportClaims.Count;
        public int CachedRouteFieldCount => navigation?.CachedFieldCount ?? 0;
        public int SharedFrontRouteBuildCount => navigation?.FrontRouteBuildCount ?? 0;
        public int SharedFrontRouteReuseCount { get; private set; }
        public HexCastleCellRuntime PalaceCore => palaceCore;
        public IReadOnlyList<HexCastleAssaultUnit> RegisteredUnits => units;

        public event Action<HexCastleAssaultUnit> UnitRegistered;
        public event Action<HexCastleAssaultUnit> UnitUnregistered;

        public void Configure(
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            float cellSize,
            int targetDefenseLayerCount,
            HexCastleGarrisonWorld targetGarrisonWorld,
            HexCastleAssaultAIProfileCatalog targetProfileCatalog = null,
            int targetStageSeed = 0,
            ICombatFeedbackPlayer targetFeedback = null,
            int attackCellCapacity = 3)
        {
            Shutdown();
            cells = runtimeCells ?? throw new ArgumentNullException(nameof(runtimeCells));
            defenseLayerCount = Mathf.Clamp(targetDefenseLayerCount, 2, 4);
            garrisonWorld = targetGarrisonWorld;
            feedback = targetFeedback;
            stageSeed = targetStageSeed;
            profileCatalog = targetProfileCatalog != null
                ? targetProfileCatalog
                : Resources.Load<HexCastleAssaultAIProfileCatalog>(
                    HexCastleAssaultAIProfileCatalog.DefaultResourcesPath);
            palaceCore = cells.Values.FirstOrDefault(value =>
                value != null && value.Kind == HexCastleCellKind.Palace &&
                value.Coordinates == new HexCoordinates(0, 0));
            if (palaceCore == null)
            {
                throw new InvalidOperationException("Hex 왕궁 중앙 Cell이 없습니다.");
            }

            navigation = new HexCastleAssaultNavigationSnapshot(cells, cellSize);
            slotAllocator = new HexAttackSlotAllocator(cellSize, palaceCore.transform.position, attackCellCapacity);
            observedSlotAvailabilityRevision = slotAllocator.AvailabilityRevision;
            approachPlanner = new HexAssaultApproachPlanner(cells, slotAllocator, cellSize, palaceCore.transform.position);
            topologyRevision = costRevision = 1;
            foreach (var cell in cells.Values)
            {
                if (cell == null)
                {
                    continue;
                }

                observedBlocking[cell.GetInstanceID()] = cell.IsBlocked;
                cell.Destroyed -= HandleCellDestroyed;
                cell.Destroyed += HandleCellDestroyed;
                cell.BlockingChanged -= HandleBlockingChanged;
                cell.BlockingChanged += HandleBlockingChanged;
                cell.Damaged -= HandleCellDamaged;
                cell.Damaged += HandleCellDamaged;
                if (cell.IsDamageable)
                {
                    cellHealthBands[cell.GetInstanceID()] = ResolveHealthRouteBand(cell);
                }
            }

            topologyVersion = 1;
            configured = true;
        }

        public HexCastleAssaultAIProfile RegisterUnit(HexCastleAssaultUnit unit, string monsterId)
        {
            if (!configured || unit == null)
            {
                throw new InvalidOperationException("Hex 공격 World를 먼저 구성해야 합니다.");
            }

            if (!units.Contains(unit))
            {
                units.Add(unit);
                unitSpawnOrders[unit.GetInstanceID()] = nextUnitSpawnOrder++;
                deploymentFronts[unit.GetInstanceID()] = unit.CurrentCoordinates;
                UnitRegistered?.Invoke(unit);
            }

            return profileCatalog == null ? new HexCastleAssaultAIProfile() : profileCatalog.Resolve(monsterId);
        }

        internal float ResolveDecisionSpread(HexCastleAssaultUnit unit)
        {
            return unit != null && unitSpawnOrders.TryGetValue(unit.GetInstanceID(), out var order)
                ? order % 9 / 8f * 0.08f : 0f; // 재실행에도 같은 소환 순번의 판단 시차를 유지한다.
        }

        internal int ResolveSpawnOrder(HexCastleAssaultUnit unit) =>
            unit != null && unitSpawnOrders.TryGetValue(unit.GetInstanceID(), out var order) ? order : 0;

        public void UnregisterUnit(HexCastleAssaultUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            var removed = units.Remove(unit);
            unitSpawnOrders.Remove(unit.GetInstanceID());
            ReleaseReservations(unit);
            slotAllocator?.ReleaseOwner(unit, true);
            ReleaseCohort(unit);
            deploymentFronts.Remove(unit.GetInstanceID());
            if (removed)
            {
                UnitUnregistered?.Invoke(unit);
            }
        }

        public void Shutdown()
        {
            if (cells != null)
            {
                foreach (var cell in cells.Values)
                {
                    if (cell == null)
                    {
                        continue;
                    }

                    cell.Destroyed -= HandleCellDestroyed;
                    cell.BlockingChanged -= HandleBlockingChanged;
                    cell.Damaged -= HandleCellDamaged;
                }
            }

            foreach (var unit in units.Where(value => value != null).ToArray())
            {
                UnitUnregistered?.Invoke(unit);
            }
            units.Clear();
            cohorts.Clear();
            unitCohorts.Clear();
            deploymentFronts.Clear();
            slotAllocator?.Clear();
            slotAllocator = null;
            approachPlanner = null;
            trace.Clear();
            observedBlocking.Clear();
            routeBreachTargets.Clear();
            breachRouteOwners.Clear();
            unitBreachRoutes.Clear();
            outerBreachTargets.Clear();
            threatClaims.Clear();
            pursuitRetryAfter.Clear();
            threatRecords.Clear();
            supportClaims.Clear();
            unitSpawnOrders.Clear();
            cellHealthBands.Clear();
            passiveExposures.Clear();
            passiveEnhancedDamageTargets.Clear();
            expiredPassiveExposureKeys.Clear();
            cells = null;
            navigation = null;
            profileCatalog = null;
            garrisonWorld = null;
            feedback = null;
            palaceCore = null;
            observedSlotAvailabilityRevision = 0;
            strategicCursor = 0;
            cohortCursor = 0;
            nextCohortId = 1;
            nextUnitSpawnOrder = 0;
            SharedFrontRouteReuseCount = 0;
            stageSeed = 0;
            configured = false;
        }


        public bool TryResolveDecision(HexCastleAssaultUnit unit, out HexCastleAssaultDecision decision)
        {
            var result = ResolveDecision(unit);
            decision = result.Plan;
            return result.Status == HexAssaultDecisionStatus.Accepted;
        }

        private static readonly ProfilerMarker DecisionMarker = new ProfilerMarker("HexSiege.Decision");
        private int measuredFrame = -1;
        private double decisionMs;
        public double DecisionMillisecondsThisFrame => measuredFrame == Time.frameCount ? decisionMs : 0;
        public HexAssaultDecisionResult ResolveDecision(HexCastleAssaultUnit unit)
        {
            if (measuredFrame != Time.frameCount) { measuredFrame = Time.frameCount; decisionMs = 0; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            using (DecisionMarker.Auto())
            {
                try { return ResolveDecisionMeasured(unit); }
                finally
                {
                    decisionMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                }
            }
        }

        private HexAssaultDecisionResult ResolveDecisionMeasured(HexCastleAssaultUnit unit)
        {
            if (!configured || unit == null || !unit.IsAlive || navigation == null)
                return new HexAssaultDecisionResult(HexAssaultDecisionStatus.InvalidInput, HexAssaultFailureReason.InvalidInput, default, Time.time + 1f);
            slotAllocator.Tick(Time.time);
            var cohortAssignment = ResolveCohort(unit, null, null);
            approachPlanner.BeginDecision(unit, cohortAssignment.CohortId, GetCohortLeader(unit));
            RefreshActiveAttackDps(unit);
            lastCandidateFailure = HexAssaultFailureReason.None;
            if (TryResolveDecisionCore(unit, out var decision))
            {
                unit.CompleteRouteCostReview();
                var acceptedReason = decision.Intent == HexCastleAssaultIntentKind.LocalFallback
                    ? HexAssaultFailureReason.LocalFallback
                    : HexAssaultFailureReason.None;
                Record(HexAssaultTraceKind.PlanCommitted, unit, decision.Target.InstanceId, decision.SlotLease, acceptedReason);
                return new HexAssaultDecisionResult(HexAssaultDecisionStatus.Accepted, acceptedReason, decision, 0f);
            }
            var primaryReason = lastCandidateFailure;
            if (TryResolveLocalFallback(unit, out decision))
            {
                unit.CompleteRouteCostReview();
                Record(HexAssaultTraceKind.PlanCommitted, unit, decision.Target.InstanceId, decision.SlotLease, HexAssaultFailureReason.LocalFallback);
                return new HexAssaultDecisionResult(HexAssaultDecisionStatus.Accepted, HexAssaultFailureReason.LocalFallback, decision, 0f);
            }
            var reason = lastCandidateFailure == HexAssaultFailureReason.None ? HexAssaultFailureReason.NoReachableApproach : lastCandidateFailure;
            var retryAt = Time.time + Mathf.Min(1f, 0.25f * Mathf.Pow(2f, Mathf.Min(2, unit.ConsecutiveDecisionFailures)));
            TryCreateHoldPlan(unit, out decision);
            Record(HexAssaultTraceKind.DecisionDeferred, unit, 0, decision.SlotLease, reason);
            return new HexAssaultDecisionResult(decision.IsValid ? HexAssaultDecisionStatus.Deferred : HexAssaultDecisionStatus.NoFeasibleAction,
                reason, decision, retryAt);
        }

        private bool TryResolveLocalFallback(HexCastleAssaultUnit unit, out HexCastleAssaultDecision decision, int beforeTravelMs = int.MaxValue)
        {
            decision = default;
            var hasRoute = TryResolveCohortRoute(unit, out var route);
            if (!hasRoute) route = new HexCastleAssaultRoutePlan(new[] { unit.CurrentCoordinates }, 0f,
                unit.CurrentCoordinates, default, unit.CurrentCoordinates, false, unit.RouteId,
                unit.RouteSector, topologyVersion); // 지역 전투 문맥만 유지
            // A waiting unit keeps fighting the nearby front instead of standing idle.
            // This is local work, not a distant-cohort join or a second strategic search.
            var estimates = new List<BreachTimeEstimate>();
            foreach (var target in cells.Values.Where(c => IsEligibleLocalWorkTarget(unit, c, null, true) &&
                         IsInDeploymentFront(unit, c.Coordinates)))
            {
                if (TryEstimateBreachTime(unit, target, out var estimate)) estimates.Add(estimate);
            }
            var nearest = estimates.Count == 0 ? 0 : estimates.Min(value => value.MovementMilliseconds);
            foreach (var estimate in estimates.Where(value => value.MovementMilliseconds <= nearest + LocalWorkMovementBandMilliseconds)
                         .OrderBy(value => value.Target.CurrentHealth).ThenBy(value => value.TotalMilliseconds).ThenBy(value => value.Target.Coordinates))
            {
                if (beforeTravelMs != int.MaxValue && !ShouldCompleteLocalWorkBeforeTravel(beforeTravelMs, estimate.TotalMilliseconds)) continue;
                var routeId = ResolveDecisionRouteId(route.SectorId, estimate.Target.Coordinates);
                if (TryCreateCellDecision(unit, estimate.Target, false, route, HexCastleAssaultIntentKind.LocalFallback,
                        out decision, routeId))
                {
                    RecordBreachCostEvaluation(unit, estimate, estimates);
                    return true;
                }
            }
            return false;
        }

        private bool TryResolveRankedOverflowTarget(
            HexCastleAssaultUnit unit,
            HexCastleAssaultRoutePlan route,
            HexCastleCellRuntime saturatedTarget,
            HexAssaultFailureReason saturationReason,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            var estimates = new List<BreachTimeEstimate>();
            foreach (var target in cells.Values.Where(value =>
                         IsEligibleLocalWorkTarget(unit, value, saturatedTarget, true) && IsOnAssaultFront(route, value)))
            {
                if (TryEstimateBreachTime(unit, target, out var estimate))
                {
                    estimates.Add(estimate);
                }
            }

            // A reachable local job may finish before a distant strategic alternative can
            // even be reached. In that case keep the commitment and do useful temporary work.
            // This does not compare unlike final objectives as if they were the same goal.
            var minimumMovementMilliseconds = estimates.Count > 0
                ? estimates.Min(value => value.MovementMilliseconds) : int.MaxValue;
            var fourthPriority = estimates
                .Where(value => value.MovementMilliseconds <= minimumMovementMilliseconds + LocalWorkMovementBandMilliseconds)
                .OrderBy(value => value.Target.CurrentHealth)
                .ThenBy(value => value.TotalMilliseconds)
                .ThenBy(value => value.Target.Coordinates)
                .Take(1).ToArray();
            var ranked = estimates
                .Where(value => value.Target.DefenseLayer <= unit.ExpectedDefenseLayer && IsStrategicOverflowTarget(saturatedTarget, value.Target))
                .OrderBy(value => value.TotalMilliseconds)
                .ThenBy(value => value.Target.Coordinates)
                .Take(MaximumRankedOverflowTargets)
                .ToArray();
            for (var index = 0; index < ranked.Length; index++)
            {
                var estimate = ranked[index];
                if (fourthPriority.Length > 0 &&
                    ShouldCompleteLocalWorkBeforeTravel(estimate.MovementMilliseconds, fourthPriority[0].TotalMilliseconds)) continue;
                var routeId = ResolveDecisionRouteId(route.SectorId, estimate.Target.Coordinates);
                if (!TryCreateCellDecision(
                        unit,
                        estimate.Target,
                        false,
                        route,
                        HexCastleAssaultIntentKind.Progress,
                        out decision,
                        routeId,
                        true,
                        saturationReason))
                {
                    continue;
                }

                RecordBreachCostEvaluation(unit, estimate, estimates);
                return true;
            }

            // Fourth priority is temporary local work, not a new strategic objective.
            // Keep only candidates within 250 ms of the nearest reachable job, then finish
            // the lowest-HP target. The original commitment and cohort remain intact.
            if (fourthPriority.Length > 0)
            {
                var estimate = fourthPriority[0];
                var routeId = ResolveDecisionRouteId(route.SectorId, estimate.Target.Coordinates);
                if (TryCreateCellDecision(
                        unit,
                        estimate.Target,
                        false,
                        route,
                        HexCastleAssaultIntentKind.LocalFallback,
                        out decision,
                        routeId))
                {
                    RecordBreachCostEvaluation(unit, estimate, estimates);
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldCompleteLocalWorkBeforeTravel(int travelMilliseconds, int localCompletionMilliseconds)
            => travelMilliseconds > (long)localCompletionMilliseconds + 1000L;

        private static bool IsStrategicOverflowTarget(
            HexCastleCellRuntime saturatedTarget,
            HexCastleCellRuntime candidate)
        {
            if (saturatedTarget == null || candidate == null)
            {
                return false;
            }

            if (IsRingWall(saturatedTarget))
            {
                return IsRingWall(candidate) &&
                       candidate.DefenseLayer == saturatedTarget.DefenseLayer;
            }

            if (saturatedTarget.BuildingRole != HexCastleBuildingRole.None)
            {
                return candidate.BuildingRole == saturatedTarget.BuildingRole;
            }

            return candidate.Kind == saturatedTarget.Kind;
        }

        private static bool IsEligibleLocalWorkTarget(
            HexCastleAssaultUnit unit,
            HexCastleCellRuntime target,
            HexCastleCellRuntime excluded, bool allowRecoveryLayer = false)
        {
            if (unit == null || target == null || target == excluded || !target.IsAlive ||
                !target.IsBlocked || !target.IsDamageable || target.Kind == HexCastleCellKind.Palace ||
                (!allowRecoveryLayer && target.DefenseLayer > unit.ExpectedDefenseLayer) ||
                target.Coordinates.DistanceTo(unit.CurrentCoordinates) > LocalWorkRadiusCells)
            {
                return false;
            }

            var targetPosition = target.Coordinates.ToWorld(1f);
            var unitPosition = unit.CurrentCoordinates.ToWorld(1f);
            return Vector3.Dot(targetPosition, unitPosition) >= 0f;
        }

        private bool TryEstimateBreachTime(
            HexCastleAssaultUnit unit,
            HexCastleCellRuntime target,
            out BreachTimeEstimate estimate)
        {
            estimate = default;
            if (unit == null || target == null || !target.IsAlive || !target.IsBlocked || !target.IsDamageable)
                return false;
            var assaultTarget = new HexCastleAssaultTarget(target, false);
            if (!approachPlanner.TryPlan(unit, assaultTarget, unit.AttackRangeCells,
                    slot => !unit.ShouldAvoidSlot(slot) && CanAttackFromPosition(unit, assaultTarget, slot), false,
                    out var approach, out _)) return false;
            // Compare what this unit can actually finish. Cohort affinity is applied afterwards
            // within a bounded tolerance, so distant attackers never make an unrelated wall look cheap.
            var ownDps = Mathf.Max(0.1f, unit.EstimatedDamagePerSecond);
            var supportingDps = ActiveAttackDps(target, approach.Cost);
            var expectedDps = ownDps + supportingDps;
            estimate = new BreachTimeEstimate(
                target,
                Mathf.CeilToInt(approach.Cost * 1000f),
                EstimateRemainingDestructionMilliseconds(target.CurrentHealth, ownDps, supportingDps, approach.Cost),
                expectedDps);
            return true;
        }

        private static void RecordBreachCostEvaluation(
            HexCastleAssaultUnit unit,
            BreachTimeEstimate selected,
            IEnumerable<BreachTimeEstimate> candidates)
        {
            var alternatives = candidates
                .Where(value => value.Target != selected.Target)
                .OrderBy(value => value.TotalMilliseconds)
                .ThenBy(value => value.Target.Coordinates)
                .Take(1)
                .ToArray();
            var hasAlternative = alternatives.Length > 0;
            var alternative = hasAlternative ? alternatives[0] : default;
            unit.RecordBreachCostEvaluation(
                selected.Target.Coordinates,
                selected.MovementMilliseconds / 1000f,
                selected.DestructionMilliseconds / 1000f,
                selected.ExpectedDamagePerSecond,
                hasAlternative ? alternative.Target.Coordinates : (HexCoordinates?)null,
                hasAlternative ? alternative.MovementMilliseconds / 1000f : 0f,
                hasAlternative ? alternative.DestructionMilliseconds / 1000f : 0f);
        }

        private bool TryCreateHoldPlan(HexCastleAssaultUnit unit, out HexCastleAssaultDecision decision)
        {
            decision = default;
            if (unit.IsInSafeMotion || unit.CurrentTarget.IsValid) return false;
            HexAssaultApproachPlan candidate = null;
            if (TryResolveCohortRoute(unit, out var route))
            {
                var front = route.HasFirstObstacle ? route.FirstObstacleApproach : route.DestinationApproach;
                var distance = unit.CurrentCoordinates.DistanceTo(front);
                for (var progress = Math.Min(3, distance); progress > 0 && candidate == null; progress--)
                {
                    var remaining = distance - progress;
                    approachPlanner.TryPlan(unit, default, 3, slot =>
                        slot.Cell.DistanceTo(front) <= remaining &&
                        slot.Cell.DistanceFromOrigin <= unit.CurrentCoordinates.DistanceFromOrigin &&
                        IsInDeploymentFront(unit, slot.Cell), true, out candidate, out _);
                }
            }
            if (candidate == null && unit.TryRetainHold(topologyVersion, out decision)) return true;
            if (candidate == null && !approachPlanner.TryPlan(unit, default, 3, null, true, out candidate, out _)) return false;
            if (!slotAllocator.TryCommit(unit, default, candidate.Slot, unit.SlotBodyRadius, candidate.Cost,
                    candidate.OccupancyRevision, true, out var lease, out _)) return false;
            ReleaseBreachReservation(unit);
            decision = new HexCastleAssaultDecision(default, candidate.Cells, candidate.Slot.Cell, unit.RouteId,
                unit.RouteSector, topologyVersion, HexCastleAssaultIntentKind.HoldPosition,
                slotLease: lease, finalApproach: candidate.FinalPath, planKind: HexAssaultPlanKind.Hold);
            Record(HexAssaultTraceKind.SlotReserved, unit, 0, lease, HexAssaultFailureReason.WaitingForOpening);
            return true;
        }

        public bool CanAttackFromPosition(HexCastleAssaultUnit unit, HexCastleAssaultTarget target, HexAttackSlotKey slot)
        {
            if (unit == null || !target.IsValid || slotAllocator == null) return false;
            var range = target.Kind == HexCastleAssaultTargetKind.Palace
                ? HexCastleFoundationGenerator.PalaceFootprintRadius + 1 : unit.AttackRangeCells;
            return slot.Cell.DistanceTo(target.Coordinates) <= range &&
                IsAttackLaneOpen(slot.Cell, target) &&
                approachPlanner.IsAttackLineOpen(slotAllocator.Position(slot), target);
        }

        public bool CanAttackFromSlot(HexCastleAssaultUnit unit, HexCastleAssaultTarget target, HexSlotLease lease)
        {
            return slotAllocator != null && slotAllocator.IsOccupied(unit, lease) && !slotAllocator.IsHolding(lease) &&
                HexAttackSlotAllocator.SqrPlanar(unit.transform.position - slotAllocator.Position(lease.Key)) <= 0.0036f &&
                CanAttackFromPosition(unit, target, lease.Key);
        }

        private void ObserveCellRouteState(HexCastleCellRuntime cell)
        {
            if (cell == null) return;
            var id = cell.GetInstanceID(); var band = cell.IsAlive ? ResolveHealthRouteBand(cell) : 0;
            var topologyChanged = !observedBlocking.TryGetValue(id, out var blocked) || blocked != cell.IsBlocked;
            var costChanged = !cellHealthBands.TryGetValue(id, out var previous) || previous != band;
            if (!topologyChanged && !costChanged) return;
            observedBlocking[id] = cell.IsBlocked; cellHealthBands[id] = band;
            if (topologyChanged)
            {
                topologyRevision++;
                topologyVersion++;
                navigation?.Invalidate();
                approachPlanner?.InvalidateSharedSearchesAt(cell.Coordinates);
                foreach (var cohort in cohorts)
                    if (cohort.SharedRoute != null && cohort.SharedRoute.Path.Any(c => c.DistanceTo(cell.Coordinates) <= 1))
                        cohort.SharedRoute = null;
            }
            else if (costChanged) navigation?.InvalidateCosts();
            costRevision++;
            Record(topologyChanged ? HexAssaultTraceKind.TopologyChanged : HexAssaultTraceKind.CostChanged, null, id);
            for (var i = 0; i < units.Count; i++) units[i]?.NotifyWorldChange(cell.Coordinates, topologyChanged,
                units[i] != null && units[i].CurrentTarget.InstanceId == id);
        }

        public void Record(HexAssaultTraceKind kind, HexCastleAssaultUnit unit, int target = 0,
            HexSlotLease lease = default, HexAssaultFailureReason reason = HexAssaultFailureReason.None)
        {
            trace.Add(slotAllocator?.WorldEpoch ?? 0, kind, unit == null ? 0 : unit.GetInstanceID(), target,
                unit == null ? 0 : unit.PlanGeneration, lease, reason, topologyRevision, costRevision);
        }

        private bool TryResolveDecisionCore(
            HexCastleAssaultUnit unit,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            if (!configured || unit == null || !unit.IsAlive || navigation == null)
            {
                return false;
            }

            ReleaseThreatClaims(unit);

            if (!TryResolveCohortRoute(unit, out var route))
            {
                lastCandidateFailure = HexAssaultFailureReason.NoStrategicRoute;
                var local = new HexCastleAssaultRoutePlan(new[] { unit.CurrentCoordinates }, 0f,
                    unit.CurrentCoordinates, default, unit.CurrentCoordinates, false, unit.RouteId, unit.RouteSector, topologyVersion);
                if (TryResolveThreat(unit, local, out decision)) return true;
                return false;
            }

            if (TryResolveThreat(unit, route, out decision))
            {
                return true;
            }

            if (unit.AIProfile?.Pattern == HexCastleAssaultPattern.TacticalSupport &&
                TryResolveSupportTarget(unit, route, out decision))
            {
                return true;
            }

            if (unit.CurrentIntent == HexCastleAssaultIntentKind.LocalFallback && unit.CurrentTarget.IsValid &&
                !unit.RecoveryRequested && TryCreateTargetDecision(unit, unit.CurrentTarget, route,
                    HexCastleAssaultIntentKind.LocalFallback, HexCastleAssaultSupportAction.None, null, out decision)) return true;

            if (unit.CommittedTarget.IsValid &&
                (unit.CommittedTarget.Kind != HexCastleAssaultTargetKind.Defender || CanPursueTarget(unit, unit.CommittedTarget)))
            {
                if (unit.RouteCostReviewRequested &&
                    (unit.CommittedIntent == HexCastleAssaultIntentKind.InitialBreach ||
                     unit.CommittedIntent == HexCastleAssaultIntentKind.Progress || unit.CommittedIntent == HexCastleAssaultIntentKind.Palace) &&
                    TryResolveEfficientAdvance(unit, route, out decision)) return true;
                if (TryCreateTargetDecision(
                        unit,
                        unit.CommittedTarget,
                        route,
                        unit.CommittedIntent,
                        HexCastleAssaultSupportAction.None,
                        null,
                        out decision))
                {
                    return true;
                }

                var commitmentFailure = lastCandidateFailure;
                if ((commitmentFailure == HexAssaultFailureReason.SlotFull ||
                     commitmentFailure == HexAssaultFailureReason.SlotApproachBlocked) &&
                    unit.CommittedTarget.Structure != null &&
                    TryResolveRankedOverflowTarget(
                        unit,
                        route,
                        unit.CommittedTarget.Structure,
                        commitmentFailure,
                        out decision))
                {
                    return true;
                }
                if (TryResolveLocalFallback(unit, out decision)) return true;
                lastCandidateFailure = commitmentFailure;
                return false; // 기존 전선을 버리고 전역 비용장의 먼 벽으로 이동하지 않는다.
            }

            if (!route.HasFirstObstacle)
            {
                if (TryResolveEfficientAdvance(unit, route, out decision)) return true;
                return TryCreateCellDecision(
                    unit,
                    palaceCore,
                    true,
                    route,
                    HexCastleAssaultIntentKind.Palace,
                    out decision);
            }

            if (!unit.HasSelectedInitialWall &&
                (TryResolveEfficientAdvance(unit, route, out decision) || TryResolveInitialBreach(unit, route, out decision)))
            {
                return true;
            }

            if (TryResolveSpecializedTarget(unit, route, out decision))
            {
                return true;
            }

            if (TryResolveGeneralOpportunity(unit, route, out decision))
            {
                return true;
            }

            if (TryResolveEfficientAdvance(unit, route, out decision)) return true;

            if (!cells.TryGetValue(route.FirstObstacle, out var obstacle) || obstacle == null || !obstacle.IsAlive)
            {
                return false;
            }

            var obstacleRouteId = ResolveDecisionRouteId(route.SectorId, obstacle.Coordinates);
            var reservesOuterBreach = IsOuterRingWall(obstacle);
            if (reservesOuterBreach && !CanReserveBreach(obstacleRouteId, obstacle))
            {
                lastCandidateFailure = HexAssaultFailureReason.BreachBudgetFull;
                return false;
            }

            if (!TryCreateCellDecision(
                    unit,
                    obstacle,
                    false,
                    route,
                    HexCastleAssaultIntentKind.Progress,
                    out decision,
                    reservesOuterBreach ? obstacleRouteId : null))
            {
                return false;
            }

            if (reservesOuterBreach)
            {
                CommitBreachReservation(unit, obstacleRouteId, obstacle);
            }

            return true;
        }

        public void ReportThreat(HexCastleAssaultUnit victim, HexCastleAssaultTarget target)
        {
            if (!configured || victim == null || !victim.IsAlive || !target.IsValid)
            {
                return;
            }

            var victimId = victim.GetInstanceID();
            var existing = threatRecords.FirstOrDefault(value =>
                value.VictimId == victimId && value.Target.InstanceId == target.InstanceId);
            if (existing == null)
            {
                existing = new ThreatRecord();
                threatRecords.Add(existing);
            }

            existing.Target = target;
            existing.VictimId = victimId;
            existing.VictimCoordinates = victim.CurrentCoordinates;
            existing.ReportedAt = Time.time;
            for (var index = 0; index < units.Count; index++)
            {
                var responder = units[index];
                if (responder != null && responder.IsAlive &&
                    responder.CurrentCoordinates.DistanceTo(victim.CurrentCoordinates) <= SharedThreatRadiusCells)
                {
                    responder.RequestStrategicDecision(true);
                }
            }
        }

        public void ReleaseReservations(HexCastleAssaultUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            ReleaseAttackSlot(unit);
            ReleaseThreatClaims(unit);
            ReleaseSupportClaims(unit);
            ReleaseBreachReservation(unit);
        }

        public bool IsAttackLaneOpen(HexCoordinates attacker, HexCastleAssaultTarget target)
        {
            if (!configured || !target.IsValid)
            {
                return false;
            }

            return target.Kind == HexCastleAssaultTargetKind.Ally ||
                   !HasIntactWallBetween(attacker, target.Coordinates);
        }

        public bool TryResolveSupportDecision(
            HexCastleAssaultUnit source,
            out HexCastleAssaultUnit target,
            out HexCastleAssaultSupportAction action)
        {
            target = null;
            action = HexCastleAssaultSupportAction.None;
            var profile = source?.AIProfile;
            if (profile == null || profile.Pattern != HexCastleAssaultPattern.TacticalSupport ||
                !source.CanPerformSupportAction)
            {
                return false;
            }

            var bestScore = 0.35f;
            HexCastleAssaultUnit resolvedTarget = null;
            HexCastleAssaultUnit followTarget = null;
            var resolvedAction = HexCastleAssaultSupportAction.None;
            PruneSupportClaims();
            for (var index = 0; index < units.Count; index++)
            {
                var candidate = units[index];
                if (candidate == null || candidate == source || !candidate.IsAlive ||
                    source.CurrentCoordinates.DistanceTo(candidate.CurrentCoordinates) > SupportSearchRadiusCells)
                {
                    continue;
                }

                var missingHealth = 1f - candidate.HealthRatio;
                var healScore = missingHealth * 2f + candidate.RecentDamagePerSecond * 0.02f;
                var defenseScore = candidate.RecentDamagePerSecond * 0.04f +
                                   (candidate.HasDefenseBuff ? -1f : 0.2f);
                var attackScore = candidate.HasCombatTarget ? 0.65f : 0f;
                if (candidate.HasAttackBuff)
                {
                    attackScore -= 1f;
                }

                ApplySupportFocus(profile.SupportFocus, ref healScore, ref defenseScore, ref attackScore);
                Select(HexCastleAssaultSupportAction.Heal, healScore);
                Select(HexCastleAssaultSupportAction.DefenseBuff, defenseScore);
                Select(HexCastleAssaultSupportAction.AttackBuff, attackScore);
                if (followTarget == null && candidate.HasCombatTarget)
                {
                    followTarget = candidate;
                }

                void Select(HexCastleAssaultSupportAction candidateAction, float score)
                {
                    if (IsSupportClaimedByOther(source, candidate, candidateAction))
                    {
                        score -= SupportClaimPenalty;
                    }

                    if (score <= bestScore)
                    {
                        return;
                    }

                    bestScore = score;
                    resolvedTarget = candidate;
                    resolvedAction = candidateAction;
                }
            }

            target = resolvedTarget ?? followTarget;
            action = resolvedTarget != null ? resolvedAction : HexCastleAssaultSupportAction.None;
            if (target != null && action != HexCastleAssaultSupportAction.None)
            {
                ClaimSupport(source, target, action, TentativeSupportClaimSeconds);
            }
            return target != null;
        }

        public void CommitSupportDecision(
            HexCastleAssaultUnit source,
            HexCastleAssaultUnit target,
            HexCastleAssaultSupportAction action,
            float cooldownSeconds)
        {
            ClaimSupport(source, target, action, Mathf.Clamp(cooldownSeconds, 0.5f, 1.5f));
        }

        private bool TryResolveSupportTarget(
            HexCastleAssaultUnit source,
            HexCastleAssaultRoutePlan route,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            if (!TryResolveSupportDecision(source, out var ally, out var action))
            {
                return false;
            }

            if (action == HexCastleAssaultSupportAction.None &&
                source.CurrentCoordinates.DistanceTo(ally.CurrentCoordinates) <= source.SupportRangeCells)
            {
                return false; // 지원할 일이 없는 근거리 추종은 제자리 대기로 만들지 않는다
            }

            if (TryCreateTargetDecision(
                source,
                new HexCastleAssaultTarget(ally),
                route,
                HexCastleAssaultIntentKind.Support,
                action,
                null,
                out decision))
            {
                return true;
            }

            ReleaseSupportClaim(source, ally, action);
            return false;
        }

        private void Update()
        {
            TickPassiveExposures(Time.deltaTime);
            slotAllocator?.Tick(Time.time);
            WakeTemporaryWorkersWhenSlotBecomesAvailable();
            if (!configured || units.Count == 0)
            {
                return;
            }

            PruneUnits();
            PruneThreatRecords();
            foreach (var cohort in cohorts) RefreshCohortLeader(cohort);
            if (units.Count > 0)
            {
                cohortCursor %= units.Count;
                MaintainCohort(units[cohortCursor++]); // 전투 판단과 별도로 한 명씩 소속을 정비한다.
            }
            var remainingBudget = StrategicDecisionBudgetPerFrame;
            for (var offset = 0; offset < units.Count && remainingBudget > 0; offset++)
            {
                var index = (strategicCursor + offset) % units.Count;
                var unit = units[index];
                if (unit == null || !unit.IsAlive || !unit.NeedsStrategicDecision)
                {
                    continue;
                }

                strategicCursor = (index + 1) % Mathf.Max(1, units.Count);
                unit.RefreshStrategicDecision();
                remainingBudget--;
            }
        }

        private void WakeTemporaryWorkersWhenSlotBecomesAvailable()
        {
            if (slotAllocator == null ||
                observedSlotAvailabilityRevision == slotAllocator.AvailabilityRevision)
            {
                return;
            }

            observedSlotAvailabilityRevision = slotAllocator.AvailabilityRevision;
            for (var index = 0; index < units.Count; index++)
            {
                var unit = units[index];
                if (unit == null || !unit.IsAlive ||
                    unit.CurrentIntent != HexCastleAssaultIntentKind.LocalFallback ||
                    !unit.CommittedTarget.IsValid)
                {
                    continue;
                }

                unit.RequestStrategicDecision(true);
            }
        }

        public void ApplyPassiveExposure(HexCastleAssaultTarget target, float rate, float duration)
        {
            if (target.InstanceId == 0 || rate <= 0f || duration <= 0f)
            {
                return;
            }
            if (!passiveExposures.TryGetValue(target.InstanceId, out var record))
            {
                record = new PassiveExposureRecord();
                passiveExposures.Add(target.InstanceId, record);
            }
            record.Rate = Mathf.Max(record.Rate, rate);
            record.Remaining = Mathf.Max(record.Remaining, duration);
        }

        public float ResolvePassiveDamageMultiplier(HexCastleAssaultTarget target)
        {
            return target.InstanceId != 0 &&
                   passiveExposures.TryGetValue(target.InstanceId, out var record) &&
                   record.Remaining > 0f
                ? 1f + Mathf.Max(0f, record.Rate)
                : 1f;
        }

        public void MarkPassiveEnhancedDamage(HexCastleAssaultTarget target)
        {
            if (target.InstanceId != 0)
            {
                passiveEnhancedDamageTargets.Add(target.InstanceId);
            }
        }

        public DamageFeedbackFlags ConsumePassiveDamageFeedback(int targetId)
        {
            return targetId != 0 && passiveEnhancedDamageTargets.Remove(targetId)
                ? DamageFeedbackFlags.PassiveEnhancedNumber
                : DamageFeedbackFlags.None;
        }

        public void QueuePassiveStatus(HexCastleAssaultUnit unit, string text, CombatStatusTextStyle style)
        {
            if (unit != null)
            {
                feedback?.PlayStatusText(
                    HexCastleOverheadHealthBar.ResolveWorldAnchor(unit.transform),
                    text,
                    style,
                    unit.GetInstanceID());
            }
        }

        public void QueuePassiveHeal(HexCastleAssaultUnit unit, float amount)
        {
            if (unit != null && amount > 0f)
            {
                feedback?.PlayFloatingNumber(
                    HexCastleOverheadHealthBar.ResolveWorldAnchor(unit.transform),
                    amount,
                    FloatingNumberStyle.Heal,
                    unit.GetInstanceID());
            }
        }

        private void TickPassiveExposures(float deltaTime)
        {
            if (passiveExposures.Count == 0)
            {
                return;
            }
            expiredPassiveExposureKeys.Clear();
            foreach (var pair in passiveExposures)
            {
                pair.Value.Remaining = Mathf.Max(0f, pair.Value.Remaining - Mathf.Max(0f, deltaTime));
                if (pair.Value.Remaining <= 0f)
                {
                    expiredPassiveExposureKeys.Add(pair.Key);
                }
            }
            for (var index = 0; index < expiredPassiveExposureKeys.Count; index++)
            {
                passiveExposures.Remove(expiredPassiveExposureKeys[index]);
            }
            expiredPassiveExposureKeys.Clear();
        }

        private static float PlanarDistance(Vector3 left, Vector3 right)
        {
            left.y = 0f;
            right.y = 0f;
            return Vector3.Distance(left, right);
        }

        private bool TryResolveThreat(
            HexCastleAssaultUnit unit,
            HexCastleAssaultRoutePlan route,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            var target = unit.RecentThreat;
            if (target.IsValid && CanPursueTarget(unit, target) && TryClaimThreat(unit, target) &&
                TryCreateTargetDecision(
                    unit,
                    target,
                    route,
                    HexCastleAssaultIntentKind.Threat,
                    HexCastleAssaultSupportAction.None,
                    null,
                    out decision))
            {
                return true;
            }

            var pattern = unit.AIProfile?.Pattern ?? HexCastleAssaultPattern.GeneralAdvance;
            var sharedRadius = pattern == HexCastleAssaultPattern.ThreatSuppressor
                ? ThreatSuppressorRadiusCells
                : pattern == HexCastleAssaultPattern.GeneralAdvance ||
                  pattern == HexCastleAssaultPattern.ResourceRaider ||
                  pattern == HexCastleAssaultPattern.TacticalSupport
                    ? SharedThreatRadiusCells
                    : 0;
            if (sharedRadius <= 0)
            {
                return false;
            }

            foreach (var record in threatRecords
                         .Where(value => value.Target.IsValid &&
                                         Time.time - value.ReportedAt <= ThreatRecordSeconds &&
                                         unit.CurrentCoordinates.DistanceTo(value.VictimCoordinates) <= sharedRadius)
                         .OrderBy(value => unit.CurrentCoordinates.DistanceTo(value.Target.Coordinates))
                         .ThenBy(value => value.Target.CurrentHealth))
            {
                if (!CanPursueTarget(unit, record.Target) || !TryClaimThreat(unit, record.Target))
                {
                    continue;
                }

                if (TryCreateTargetDecision(
                        unit,
                        record.Target,
                        route,
                        HexCastleAssaultIntentKind.Threat,
                        HexCastleAssaultSupportAction.None,
                        null,
                        out decision))
                {
                    return true;
                }
            }

            return false;
        }

        private bool CanPursueTarget(HexCastleAssaultUnit unit, HexCastleAssaultTarget target)
        {
            if (!target.IsValid) return false;
            var key = new ThreatClaim(target.InstanceId, unit.GetInstanceID());
            if (pursuitRetryAfter.TryGetValue(key, out var retry) && Time.time < retry) return false;
            if (!approachPlanner.TryPlan(unit, target, unit.AttackRangeCells,
                    slot => !unit.ShouldAvoidSlot(slot) && CanAttackFromPosition(unit, target, slot), false,
                    out var approach, out _)) return false;
            var hunter = unit.AIProfile.Pattern == HexCastleAssaultPattern.DefenderHunter ||
                unit.AIProfile.Pattern == HexCastleAssaultPattern.ThreatSuppressor;
            var maximumMs = hunter ? 8000 : 4000;
            var same = unit.CurrentTarget.InstanceId == target.InstanceId;
            if (approach.Cost * 1000f <= maximumMs &&
                (!same || approach.Cost <= 0.3f || Time.time - unit.LastTargetSwitchTime <= (hunter ? 10f : 6f))) return true;
            pursuitRetryAfter[key] = Time.time + 2f;
            Record(HexAssaultTraceKind.CandidateRejected, unit, target.InstanceId, default, HexAssaultFailureReason.PursuitBudgetExceeded);
            return false;
        }

        private sealed class AdvanceEstimate
        {
            public HexCastleCellRuntime Target;
            public HexAssaultApproachPlan Approach;
            public int MoveMs, DestroyMs, ContinuationMs;
            public float SupportingDps;
            public int TotalMs => MoveMs + DestroyMs + ContinuationMs;
        }

        private bool TryEstimateAdvance(HexCastleAssaultUnit unit, HexCastleCellRuntime target, out AdvanceEstimate estimate)
        {
            estimate = null;
            if (target == null || !target.IsAlive) return false;
            var palace = target == palaceCore;
            var assaultTarget = new HexCastleAssaultTarget(target, palace);
            var range = palace ? HexCastleFoundationGenerator.PalaceFootprintRadius + 1 : unit.AttackRangeCells;
            if (!approachPlanner.TryPlan(unit, assaultTarget, range,
                    slot => !unit.ShouldAvoidSlot(slot) && CanAttackFromPosition(unit, assaultTarget, slot), false,
                    out var approach, out _)) return false; // 즉시 예약 불가 자리는 무한 대기로 취급
            var supportingDps = ActiveAttackDps(target, approach.Cost);
            estimate = new AdvanceEstimate { Target = target, Approach = approach, SupportingDps = supportingDps,
                MoveMs = Mathf.CeilToInt(approach.Cost * 1000f),
                DestroyMs = palace ? 0 : EstimateRemainingDestructionMilliseconds(target.CurrentHealth,
                    unit.EstimatedDamagePerSecond, supportingDps, approach.Cost),
                ContinuationMs = 0 }; // 현재 전면의 작업 비용만 비교한다.
            return true;
        }

        private bool TryResolveEfficientAdvance(HexCastleAssaultUnit unit, HexCastleAssaultRoutePlan route,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            var estimates = new List<AdvanceEstimate>();
            if (!route.HasFirstObstacle && TryEstimateAdvance(unit, palaceCore, out var palace)) estimates.Add(palace);
            foreach (var target in cells.Values.Where(value => value != null).OrderBy(value => value.Coordinates))
            {
                if (!IsOnAssaultFront(route, target) || !target.IsAlive || !target.IsBlocked || !target.IsDamageable || target == palaceCore ||
                    target.Kind == HexCastleCellKind.Palace ||
                    target.DefenseLayer > unit.ExpectedDefenseLayer && target.Coordinates.DistanceTo(unit.CurrentCoordinates) > LocalWorkRadiusCells) continue;
                if (target != unit.CommittedTarget.Structure && target.Coordinates != route.FirstObstacle &&
                    !(IsRingWall(target) && target.DefenseLayer == unit.ExpectedDefenseLayer) &&
                    target.Coordinates.DistanceTo(unit.CurrentCoordinates) > LocalWorkRadiusCells) continue;
                var routeId = ResolveDecisionRouteId(route.SectorId, target.Coordinates);
                if (IsOuterRingWall(target) && !CanReserveBreach(routeId, target)) continue;
                if (TryEstimateAdvance(unit, target, out var estimate)) estimates.Add(estimate);
            }
            if (estimates.Count == 0) return false;
            estimates.Sort((a, b) => a.TotalMs != b.TotalMs ? a.TotalMs.CompareTo(b.TotalMs) : a.Target.Coordinates.CompareTo(b.Target.Coordinates));
            var selected = estimates[0];
            var current = estimates.Find(value => value.Target == unit.CommittedTarget.Structure);
            var retainCurrent = current != null && current != selected && ShouldRetainCurrentAdvance(
                unit.RecoveryRequested, Time.time - unit.LastTargetSwitchTime, current.TotalMs, selected.TotalMs);
            if (retainCurrent && Time.time - unit.LastTargetSwitchTime < 1f)
                unit.DeferRouteCostReview(1f - (Time.time - unit.LastTargetSwitchTime));
            if (retainCurrent) selected = current;
            var intent = selected.Target == palaceCore ? HexCastleAssaultIntentKind.Palace :
                !unit.HasSelectedInitialWall && IsRingWall(selected.Target) && selected.Target.DefenseLayer == unit.ExpectedDefenseLayer
                    ? HexCastleAssaultIntentKind.InitialBreach : HexCastleAssaultIntentKind.Progress;
            var changed = unit.CommittedTarget.IsValid && selected.Target != unit.CommittedTarget.Structure;
            if (!TryCreateCellDecision(unit, selected.Target, selected.Target == palaceCore, route, intent,
                    out decision, null, changed, HexAssaultFailureReason.None)) return false;
            var alternative = estimates.FirstOrDefault(value => value != selected);
            unit.RecordAdvanceCost(selected.MoveMs, selected.DestroyMs, selected.ContinuationMs,
                alternative?.Target.Coordinates, alternative?.TotalMs ?? 0);
            if (selected.SupportingDps > 0 || alternative?.SupportingDps > 0) unit.DeferRouteCostReview(1f);
            if (selected.Target != palaceCore)
                unit.RecordBreachCostEvaluation(selected.Target.Coordinates, selected.MoveMs / 1000f, selected.DestroyMs / 1000f,
                    unit.EstimatedDamagePerSecond + selected.SupportingDps, alternative?.Target.Coordinates, (alternative?.MoveMs ?? 0) / 1000f,
                    (alternative?.DestroyMs ?? 0) / 1000f);
            return true;
        }

        private static bool ShouldRetainCurrentAdvance(bool recovering, float sinceSwitch, int currentMs, int bestMs) =>
            !recovering && (sinceSwitch < 1f || currentMs - bestMs < Math.Max(1000, Mathf.CeilToInt(currentMs * .1f)));

        private void RefreshActiveAttackDps(HexCastleAssaultUnit selectingUnit)
        {
            activeAttackDps.Clear(); // 판단 1회당 실제 정착·공격 중인 병력만 집계
            foreach (var attacker in units)
            {
                if (attacker == null || attacker == selectingUnit || !attacker.IsAlive ||
                    attacker.ExecutionState != HexAssaultExecutionState.Attacking ||
                    attacker.CurrentTarget.Structure == null ||
                    !CanAttackFromSlot(attacker, attacker.CurrentTarget, attacker.CurrentSlotLease)) continue;
                var key = attacker.CurrentTarget.Structure.GetInstanceID();
                activeAttackDps.TryGetValue(key, out var previous);
                activeAttackDps[key] = previous + attacker.EstimatedDamagePerSecond;
            }
        }

        private float ActiveAttackDps(HexCastleCellRuntime target, float approachSeconds) => approachSeconds <= 1f && target != null &&
            activeAttackDps.TryGetValue(target.GetInstanceID(), out var value) ? value : 0f;

        private static int EstimateRemainingDestructionMilliseconds(float hp, float ownDps, float activeDps, float approachSeconds)
        {
            var remaining = Mathf.Max(0f, hp - Mathf.Max(0f, activeDps) * Mathf.Max(0f, approachSeconds));
            return Mathf.CeilToInt(remaining / Mathf.Max(.1f, ownDps + Mathf.Max(0f, activeDps)) * 1000f);
        }

        private bool TryResolveInitialBreach(
            HexCastleAssaultUnit unit,
            HexCastleAssaultRoutePlan route,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            var estimates = new List<BreachTimeEstimate>();
            foreach (var candidate in cells.Values
                         .Where(value => IsOnAssaultFront(route, value) && IsRingWall(value) && value.IsAlive &&
                                         value.DefenseLayer == unit.ExpectedDefenseLayer))
            {
                var routeId = ResolveDecisionRouteId(route.SectorId, candidate.Coordinates);
                if (!CanReserveBreach(routeId, candidate)) continue;
                if (TryEstimateBreachTime(unit, candidate, out var estimate)) estimates.Add(estimate);
            }
            if (estimates.Count == 0)
            {
                return false;
            }

            estimates.Sort((left, right) =>
            {
                var cost = left.TotalMilliseconds.CompareTo(right.TotalMilliseconds);
                return cost != 0 ? cost : left.Target.Coordinates.CompareTo(right.Target.Coordinates);
            });
            var selected = estimates[0];
            if (TryResolveJoinableCohortBreachCandidate(unit, route,
                    estimates.Select(value => value.Target).ToArray(), out var joined))
            {
                var joinedEstimate = estimates.First(value => value.Target == joined);
                var tolerance = Mathf.Max(
                    CohortBreachJoinToleranceMilliseconds,
                    Mathf.CeilToInt(selected.TotalMilliseconds * CohortBreachJoinToleranceRatio));
                if (joinedEstimate.TotalMilliseconds <= selected.TotalMilliseconds + tolerance)
                {
                    selected = joinedEstimate;
                }
            }

            foreach (var estimate in estimates.OrderBy(value => value.Target == selected.Target ? 0 : 1)
                         .ThenBy(value => value.TotalMilliseconds).ThenBy(value => value.Target.Coordinates))
            {
                var candidate = estimate.Target;
                var routeId = ResolveDecisionRouteId(route.SectorId, candidate.Coordinates);
                if (TryCreateCellDecision(
                        unit,
                        candidate,
                        false,
                        route,
                        HexCastleAssaultIntentKind.InitialBreach,
                        out decision,
                        routeId))
                {
                    CommitBreachReservation(unit, routeId, candidate);
                    RecordBreachCostEvaluation(unit, estimate, estimates);
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveGeneralOpportunity(
            HexCastleAssaultUnit unit,
            HexCastleAssaultRoutePlan route,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            if (unit.AIProfile?.Pattern != HexCastleAssaultPattern.GeneralAdvance ||
                unit.HasEvaluatedOpportunity(unit.ExpectedDefenseLayer))
            {
                return false;
            }

            var roll = ResolveDeterministic01(unit, 2000 + unit.ExpectedDefenseLayer);
            unit.MarkOpportunityEvaluated(unit.ExpectedDefenseLayer);
            if (roll >= GeneralOpportunityChance)
            {
                return false;
            }

            foreach (var candidate in cells.Values
                         .Where(value => value != null && value.IsAlive && value.IsBlocked &&
                                         IsGeneralOpportunityStructure(value) &&
                                         value.Coordinates.DistanceTo(unit.CurrentCoordinates) <=
                                         SpecializedTargetRadiusCells)
                         .OrderBy(value => value.Coordinates.DistanceTo(unit.CurrentCoordinates))
                         .ThenBy(value => value.CurrentHealth))
            {
                if (TryCreateCellDecision(
                        unit,
                        candidate,
                        false,
                        route,
                        HexCastleAssaultIntentKind.Opportunity,
                        out decision))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveSpecializedTarget(
            HexCastleAssaultUnit unit,
            HexCastleAssaultRoutePlan route,
            out HexCastleAssaultDecision decision)
        {
            decision = default;
            var pattern = unit.AIProfile?.Pattern ?? HexCastleAssaultPattern.GeneralAdvance;
            var targetLimit = pattern == HexCastleAssaultPattern.ResourceRaider ? 2 : 1;
            if (!unit.CanSelectSpecialistTarget(unit.ExpectedDefenseLayer, targetLimit))
            {
                return false;
            }

            if (pattern == HexCastleAssaultPattern.DefenderHunter && garrisonWorld != null)
            {
                var defenders = garrisonWorld.Units
                    .Where(value => value != null && value.IsAlive &&
                                    unit.CurrentCoordinates.DistanceTo(value.Coordinates) <=
                                    SpecializedTargetRadiusCells)
                    .OrderBy(value => unit.CurrentCoordinates.DistanceTo(value.Coordinates));
                foreach (var defender in defenders)
                {
                    if (!CanPursueTarget(unit, new HexCastleAssaultTarget(defender))) continue;
                    if (TryCreateTargetDecision(
                            unit,
                            new HexCastleAssaultTarget(defender),
                            route,
                            HexCastleAssaultIntentKind.Specialist,
                            HexCastleAssaultSupportAction.None,
                            null,
                            out decision))
                    {
                        return true;
                    }
                }
            }

            if (pattern != HexCastleAssaultPattern.ResourceRaider &&
                pattern != HexCastleAssaultPattern.TurretHunter)
            {
                return false;
            }

            var candidates = cells.Values
                .Where(value => value != null && value.IsAlive && value.IsBlocked &&
                                value.Coordinates.DistanceTo(unit.CurrentCoordinates) <=
                                SpecializedTargetRadiusCells &&
                                IsSpecializedStructure(value, pattern))
                .OrderBy(value => value.Coordinates.DistanceTo(unit.CurrentCoordinates))
                .ThenBy(value => value.CurrentHealth);
            foreach (var candidate in candidates)
            {
                if (TryCreateCellDecision(
                        unit,
                        candidate,
                        false,
                        route,
                        HexCastleAssaultIntentKind.Specialist,
                        out decision))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryCreateCellDecision(
            HexCastleAssaultUnit unit,
            HexCastleCellRuntime target,
            bool palace,
            HexCastleAssaultRoutePlan route,
            HexCastleAssaultIntentKind intent,
            out HexCastleAssaultDecision decision,
            int? routeIdOverride = null,
            bool reassignCohort = false,
            HexAssaultFailureReason reassignmentReason = HexAssaultFailureReason.None)
        {
            return TryCreateTargetDecision(
                unit,
                new HexCastleAssaultTarget(target, palace),
                route,
                intent,
                HexCastleAssaultSupportAction.None,
                routeIdOverride,
                out decision,
                reassignCohort,
                reassignmentReason);
        }

        private bool TryCreateTargetDecision(
            HexCastleAssaultUnit unit, HexCastleAssaultTarget target, HexCastleAssaultRoutePlan route,
            HexCastleAssaultIntentKind intent, HexCastleAssaultSupportAction supportAction,
            int? routeIdOverride, out HexCastleAssaultDecision decision,
            bool reassignCohort = false,
            HexAssaultFailureReason reassignmentReason = HexAssaultFailureReason.None)
        {
            decision = default;
            if (!target.IsValid) { lastCandidateFailure = HexAssaultFailureReason.TargetInvalid; return false; }
            var retained = !unit.RecoveryRequested && unit.CurrentIntent == intent &&
                unit.ActivePlan.SupportAction == supportAction && unit.TryRetainPlan(target, topologyVersion, out decision);
            if (retained && !unit.RouteCostReviewRequested) return true;
            var range = target.Kind == HexCastleAssaultTargetKind.Palace
                ? HexCastleFoundationGenerator.PalaceFootprintRadius + 1
                : target.Kind == HexCastleAssaultTargetKind.Ally ? unit.SupportRangeCells : unit.AttackRangeCells;
            if (target.Kind == HexCastleAssaultTargetKind.Ally)
            {
                if (!navigation.TryResolveOpenFollowRoute(unit.CurrentCoordinates, target.Coordinates,
                        range, out var follow, out var approach))
                { lastCandidateFailure = HexAssaultFailureReason.NoReachableApproach; return false; }
                var group = ResolveCohort(unit, route, routeIdOverride);
                slotAllocator.ReleaseOwner(unit, false);
                decision = new HexCastleAssaultDecision(target, follow, approach, group.RouteId, group.SectorId,
                    topologyVersion, intent, supportAction, planKind: HexAssaultPlanKind.Follow);
                return true;
            }
            var topology = topologyRevision;
            if (!approachPlanner.TryPlan(unit, target, range,
                    slot => !unit.ShouldAvoidSlot(slot) && CanAttackFromPosition(unit, target, slot), false,
                    out var candidate, out var reason,
                    (slot, why) => Record(HexAssaultTraceKind.CandidateRejected, unit, target.InstanceId, default, why)))
            { if (retained) return true; lastCandidateFailure = reason; return false; }
            if (retained && candidate.Cost * 1000f + 500f >= unit.RemainingMovementSeconds() * 1000f) return true;
            if (topology != topologyRevision) { lastCandidateFailure = HexAssaultFailureReason.PlanVersionChanged; return false; }
            var outer = target.Structure != null && IsOuterRingWall(target.Structure) && intent != HexCastleAssaultIntentKind.LocalFallback;
            var targetRoute = routeIdOverride ?? ResolveDecisionRouteId(route.SectorId, target.Coordinates);
            if (outer && !CanReserveBreach(targetRoute, target.Structure))
            { lastCandidateFailure = HexAssaultFailureReason.BreachBudgetFull; return false; }
            if (!slotAllocator.TryCommit(unit, target, candidate.Slot, unit.SlotBodyRadius, candidate.Cost,
                    candidate.OccupancyRevision, false, out var lease, out reason))
            { lastCandidateFailure = reason; return false; }
            // All feasibility checks have succeeded. Publish no callbacks until ownership is complete.
            if (outer) CommitBreachReservation(unit, targetRoute, target.Structure);
            else ReleaseBreachReservation(unit);
            var unitId = unit.GetInstanceID();
            var hadPreviousCohort = unitCohorts.TryGetValue(unitId, out var previousCohort);
            var assignment = ResolveCohort(unit, route, routeIdOverride);
            if (reassignCohort && hadPreviousCohort && previousCohort.CohortId != assignment.CohortId)
            {
                Record(HexAssaultTraceKind.CohortReassigned, unit, target.InstanceId, lease, reassignmentReason);
            }
            decision = new HexCastleAssaultDecision(target, candidate.Cells, candidate.Slot.Cell,
                assignment.RouteId, assignment.SectorId, topologyVersion, intent, supportAction,
                lease, candidate.FinalPath);
            Record(HexAssaultTraceKind.SlotReserved, unit, target.InstanceId, lease);
            return true;
        }

        private bool HasIntactWallBetween(HexCoordinates start, HexCoordinates end)
        {
            var distance = start.DistanceTo(end);
            if (distance <= 1)
            {
                return false;
            }

            for (var step = 1; step < distance; step++)
            {
                var ratio = step / (float)distance;
                var coordinates = RoundAxial(
                    Mathf.Lerp(start.Q, end.Q, ratio),
                    Mathf.Lerp(start.R, end.R, ratio));
                if (cells.TryGetValue(coordinates, out var cell) && IsIntactWallBarrier(cell))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsIntactWallBarrier(HexCastleCellRuntime cell)
        {
            return cell != null && cell.IsAlive && cell.IsBlocked &&
                   (cell.Kind == HexCastleCellKind.Wall ||
                    cell.Kind == HexCastleCellKind.Tower ||
                    cell.Kind == HexCastleCellKind.Gate);
        }

        private static HexCoordinates RoundAxial(float q, float r)
        {
            var s = -q - r;
            var roundedQ = Mathf.RoundToInt(q);
            var roundedR = Mathf.RoundToInt(r);
            var roundedS = Mathf.RoundToInt(s);
            var qDifference = Mathf.Abs(roundedQ - q);
            var rDifference = Mathf.Abs(roundedR - r);
            var sDifference = Mathf.Abs(roundedS - s);

            if (qDifference > rDifference && qDifference > sDifference)
            {
                roundedQ = -roundedR - roundedS;
            }
            else if (rDifference > sDifference)
            {
                roundedR = -roundedQ - roundedS;
            }

            return new HexCoordinates(roundedQ, roundedR);
        }

        private bool TryLeaseAttackSlot(HexCastleAssaultUnit unit, HexCastleAssaultTarget target, HexCoordinates approach)
        {
            // Kept only for binary/source compatibility with diagnostic reflection.
            for (var i = 0; i < slotAllocator.CandidateCount(unit.SlotBodyRadius); i++)
                if (slotAllocator.TryCommit(unit, target, slotAllocator.Candidate(approach, unit.SlotBodyRadius, i),
                    unit.SlotBodyRadius, 2f, slotAllocator.Revision, false, out _, out _)) return true;
            return false;
        }

        private IReadOnlyCollection<HexCoordinates> ResolveOccupiedApproaches(HexCastleAssaultUnit unit, HexCastleAssaultTarget target)
        {
            return cells.Keys.Where(c => slotAllocator.CountInCell(c) >= slotAllocator.Capacity).ToArray();
        }

        private void ReleaseAttackSlot(HexCastleAssaultUnit unit)
        {
            slotAllocator?.ReleaseOwner(unit, false);
        }

        private void ReleaseCohort(HexCastleAssaultUnit unit)
        {
            var unitId = unit.GetInstanceID();
            if (!unitCohorts.TryGetValue(unitId, out var assignment))
            {
                return;
            }

            unitCohorts.Remove(unitId);
            var cohort = cohorts.FirstOrDefault(value => value.CohortId == assignment.CohortId);
            if (cohort == null)
            {
                return;
            }

            cohort.Members.Remove(unit);
            RefreshCohortLeader(cohort);
            if (cohort.MemberCount <= 0)
            {
                approachPlanner?.ForgetCohort(cohort.CohortId);
                cohorts.Remove(cohort);
            }
        }

        private void ReleaseThreatClaims(HexCastleAssaultUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            var unitId = unit.GetInstanceID();
            foreach (var claim in threatClaims.Where(value => value.ResponderId == unitId).ToArray())
            {
                threatClaims.Remove(claim);
            }
        }

        private bool CanReserveBreach(int routeId, HexCastleCellRuntime wall)
        {
            var wallId = wall.GetInstanceID();
            if (routeBreachTargets.TryGetValue(routeId, out var reservedWallId))
            {
                return reservedWallId == wallId;
            }

            if (!outerBreachTargets.Contains(wallId) &&
                outerBreachTargets.Count >= MaximumOuterBreachRoutes)
            {
                return false;
            }

            return true;
        }

        private void CommitBreachReservation(
            HexCastleAssaultUnit unit,
            int routeId,
            HexCastleCellRuntime wall)
        {
            if (unit == null || wall == null)
            {
                return;
            }

            var unitId = unit.GetInstanceID();
            if (unitBreachRoutes.TryGetValue(unitId, out var previousRouteId) && previousRouteId != routeId)
            {
                ReleaseBreachReservation(unit);
            }

            var wallId = wall.GetInstanceID();
            routeBreachTargets[routeId] = wallId;
            outerBreachTargets.Add(wallId);
            if (!breachRouteOwners.TryGetValue(routeId, out var owners))
            {
                owners = new HashSet<int>();
                breachRouteOwners.Add(routeId, owners);
            }

            owners.Add(unitId);
            unitBreachRoutes[unitId] = routeId;
        }

        private void ReleaseBreachReservation(HexCastleAssaultUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            var unitId = unit.GetInstanceID();
            if (!unitBreachRoutes.TryGetValue(unitId, out var routeId))
            {
                return;
            }

            unitBreachRoutes.Remove(unitId);
            if (!breachRouteOwners.TryGetValue(routeId, out var owners))
            {
                return;
            }

            owners.Remove(unitId);
            if (owners.Count > 0)
            {
                return;
            }

            breachRouteOwners.Remove(routeId);
            if (!routeBreachTargets.TryGetValue(routeId, out var wallId))
            {
                return;
            }

            routeBreachTargets.Remove(routeId);
            if (!routeBreachTargets.ContainsValue(wallId))
            {
                outerBreachTargets.Remove(wallId);
            }
        }

        private bool TryResolveJoinableCohortBreachCandidate(
            HexCastleAssaultUnit unit,
            HexCastleAssaultRoutePlan route,
            IReadOnlyList<HexCastleCellRuntime> candidates,
            out HexCastleCellRuntime candidate)
        {
            candidate = null;
            foreach (var cohort in cohorts
                         .Where(value => value.Leader != null && value.MemberCount < MaximumCohortSize &&
                                         CanJoinCohort(unit, value, CohortJoinDistanceCells))
                         .OrderBy(value => value.Leader.CurrentCoordinates.DistanceTo(unit.CurrentCoordinates))
                         .ThenBy(value => value.CohortId))
            {
                if (!routeBreachTargets.TryGetValue(cohort.RouteId, out var wallId))
                {
                    continue;
                }

                candidate = candidates.FirstOrDefault(value =>
                    value != null && value.IsAlive && value.GetInstanceID() == wallId);
                if (candidate != null)
                {
                    return true;
                }
            }

            return false;
        }

        private HexCastleAssaultCohortAssignment ResolveCohort(
            HexCastleAssaultUnit unit,
            HexCastleAssaultRoutePlan route,
            int? routeIdOverride)
        {
            var unitId = unit.GetInstanceID();
            MaintainCohort(unit);
            if (unitCohorts.TryGetValue(unitId, out var existing))
            {
                return existing;
            }

            CohortRecord selected = null;
            for (var index = 0; index < cohorts.Count; index++)
            {
                var cohort = cohorts[index];
                RefreshCohortLeader(cohort);
                if (cohort.MemberCount >= MaximumCohortSize || !CanJoinCohort(unit, cohort, CohortJoinDistanceCells))
                {
                    continue;
                }

                selected = cohort;
                break;
            }

            if (selected == null)
            {
                if (route == null) navigation.TryResolveFrontRoute(unit.CurrentCoordinates,
                    unit.EstimatedDamagePerSecond, unit.EffectiveMoveSpeed, topologyVersion, out route);
                selected = new CohortRecord
                {
                    CohortId = nextCohortId++,
                    RouteId = routeIdOverride ?? route?.RouteId ?? unit.RouteId,
                    SectorId = route?.SectorId ?? unit.RouteSector,
                    SharedRoute = route
                };
                cohorts.Add(selected);
            }

            selected.Members.Add(unit);
            RefreshCohortLeader(selected);
            var assignment = new HexCastleAssaultCohortAssignment(
                selected.CohortId,
                selected.RouteId,
                selected.SectorId);
            unitCohorts[unitId] = assignment;
            return assignment;
        }

        private bool TryResolveCohortRoute(HexCastleAssaultUnit unit, out HexCastleAssaultRoutePlan route)
        {
            var assignment = ResolveCohort(unit, null, null);
            var cohort = cohorts.First(value => value.CohortId == assignment.CohortId);
            var leader = cohort.Leader;
            route = cohort.SharedRoute;
            if (route != null && route.HasFirstObstacle &&
                (!cells.TryGetValue(route.FirstObstacle, out var obstacle) || obstacle == null ||
                 obstacle.CanTraverse(HexCastleTraversalFaction.Assault) ||
                 leader.CurrentCoordinates.DistanceFromOrigin < route.FirstObstacle.DistanceFromOrigin)) route = null;
            if (route != null) { SharedFrontRouteReuseCount++; return true; }
            if (leader == null || !navigation.TryResolveFrontRoute(leader.CurrentCoordinates,
                    leader.EstimatedDamagePerSecond, leader.EffectiveMoveSpeed, topologyVersion, out route)) return false;
            cohort.SharedRoute = route; cohort.RouteVersion++;
            return true;
        }

        public HexCastleAssaultUnit GetCohortLeader(HexCastleAssaultUnit unit)
        {
            if (unit == null || !unitCohorts.TryGetValue(unit.GetInstanceID(), out var assignment)) return null;
            var cohort = cohorts.FirstOrDefault(value => value.CohortId == assignment.CohortId);
            if (cohort == null) return null;
            RefreshCohortLeader(cohort);
            return cohort.Leader;
        }

        private void RefreshCohortLeader(CohortRecord cohort)
        {
            cohort.Members.RemoveAll(member => member == null || !member.IsAlive);
            HexCastleAssaultUnit leader = null;
            foreach (var member in cohort.Members)
                if (leader == null || member.CurrentCoordinates.DistanceFromOrigin < leader.CurrentCoordinates.DistanceFromOrigin ||
                    member.CurrentCoordinates.DistanceFromOrigin == leader.CurrentCoordinates.DistanceFromOrigin &&
                    ResolveSpawnOrder(member) < ResolveSpawnOrder(leader)) leader = member;
            cohort.Leader = leader; // 팀장 교체만으로 기존 경로·자리·목표를 해제하지 않는다.
        }

        private bool CanJoinCohort(HexCastleAssaultUnit unit, CohortRecord cohort, int steps)
        {
            var leader = cohort.Leader;
            if (leader == null || !unit.IsAlive) return false;
            if (leader == unit) return true;
            var from = deploymentFronts.TryGetValue(unit.GetInstanceID(), out var a) ? a : unit.CurrentCoordinates;
            var to = deploymentFronts.TryGetValue(leader.GetInstanceID(), out var b) ? b : leader.CurrentCoordinates;
            if (Vector3.Dot(from.ToWorld(1f).normalized, to.ToWorld(1f).normalized) < .5f) return false;
            return HasShortCohortConnection(unit.CurrentCoordinates, leader.CurrentCoordinates, steps);
        }

        private bool IsInDeploymentFront(HexCastleAssaultUnit unit, HexCoordinates target)
        {
            var start = deploymentFronts.TryGetValue(unit.GetInstanceID(), out var deployed) ? deployed : unit.CurrentCoordinates;
            return Vector3.Dot(start.ToWorld(1f).normalized, target.ToWorld(1f).normalized) >= .5f;
        }

        private bool HasShortCohortConnection(HexCoordinates start, HexCoordinates goal, int steps)
        {
            if (start.DistanceTo(goal) > steps || !cells.TryGetValue(start, out var first) ||
                first == null || !first.CanTraverse(HexCastleTraversalFaction.Assault)) return false;
            if (start == goal) return true;
            cohortJoinDistances.Clear(); cohortJoinQueue.Clear();
            cohortJoinDistances.Add(start, 0); cohortJoinQueue.Enqueue(start);
            while (cohortJoinQueue.Count > 0)
            {
                var at = cohortJoinQueue.Dequeue();
                if (cohortJoinDistances[at] >= steps) continue;
                for (var d = 0; d < 6; d++)
                {
                    var next = at.Neighbor(d);
                    if (cohortJoinDistances.ContainsKey(next) || !cells.TryGetValue(next, out var cell) ||
                        cell == null || !HexRoutePlanner.CanTraverseStep(cells[at], cell, d, HexCastleTraversalFaction.Assault)) continue;
                    if (next == goal) return true;
                    cohortJoinDistances.Add(next, cohortJoinDistances[at] + 1); cohortJoinQueue.Enqueue(next);
                }
            }
            return false;
        }

        private void MaintainCohort(HexCastleAssaultUnit unit)
        {
            if (unit == null || !unitCohorts.TryGetValue(unit.GetInstanceID(), out var assignment)) return;
            var current = cohorts.FirstOrDefault(value => value.CohortId == assignment.CohortId);
            if (current == null) { unitCohorts.Remove(unit.GetInstanceID()); return; }
            RefreshCohortLeader(current);
            if (!unit.IsAlive || !CanJoinCohort(unit, current, CohortJoinDistanceCells + 1))
            {
                ReleaseCohort(unit);
                if (unit.IsAlive) unit.RequestStrategicDecision(true);
                return;
            }
            foreach (var candidate in cohorts)
            {
                RefreshCohortLeader(candidate);
                if (candidate.CohortId >= current.CohortId || candidate.MemberCount + current.MemberCount > MaximumCohortSize ||
                    !CanJoinCohort(unit, candidate, CohortJoinDistanceCells)) continue;
                var canMerge = true;
                foreach (var member in current.Members)
                    if (!CanJoinCohort(member, candidate, CohortJoinDistanceCells)) { canMerge = false; break; }
                if (!canMerge) continue;
                foreach (var member in current.Members)
                {
                    candidate.Members.Add(member);
                    unitCohorts[member.GetInstanceID()] = new HexCastleAssaultCohortAssignment(candidate.CohortId, candidate.RouteId, candidate.SectorId);
                    Record(HexAssaultTraceKind.CohortReassigned, member);
                }
                current.Members.Clear(); approachPlanner?.ForgetCohort(current.CohortId); cohorts.Remove(current);
                RefreshCohortLeader(candidate);
                return; // 더 이른 부대로만 합쳐 왕복 재가입을 막는다.
            }
        }

        private bool TryClaimThreat(HexCastleAssaultUnit unit, HexCastleAssaultTarget target)
        {
            var targetId = target.InstanceId;
            var responderId = unit.GetInstanceID();
            var existing = new ThreatClaim(targetId, responderId);
            if (threatClaims.Contains(existing))
            {
                return true;
            }

            var maximum = target.Kind == HexCastleAssaultTargetKind.Defender ? 2 : 3;
            if (threatClaims.Count(value => value.TargetId == targetId) >= maximum)
            {
                return false;
            }

            threatClaims.Add(existing);
            return true;
        }

        private void HandleCellDestroyed(HexCastleCellRuntime cell)
        {
            if (cell == null)
            {
                return;
            }

            var targetId = cell.GetInstanceID();
            slotAllocator?.ReleaseTarget(targetId);
            foreach (var routeId in routeBreachTargets
                         .Where(pair => pair.Value == targetId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                if (breachRouteOwners.TryGetValue(routeId, out var owners))
                {
                    foreach (var ownerId in owners)
                    {
                        unitBreachRoutes.Remove(ownerId);
                    }
                    breachRouteOwners.Remove(routeId);
                }
                routeBreachTargets.Remove(routeId);
            }
            outerBreachTargets.Remove(targetId);
            ObserveCellRouteState(cell);
        }

        private void HandleBlockingChanged(HexCastleCellRuntime cell, bool blocked)
        {
            ObserveCellRouteState(cell);
        }

        private void HandleCellDamaged(HexCastleCellRuntime cell, ProjectMT.Shared.Combat.DamageReport report)
        {
            ObserveCellRouteState(cell);
        }

        private static int ResolveHealthRouteBand(HexCastleCellRuntime cell)
        {
            if (cell == null || cell.MaxHealth <= 0f)
            {
                return 0;
            }

            return Mathf.CeilToInt(
                Mathf.Clamp01(cell.CurrentHealth / cell.MaxHealth) * HealthRouteBandCount);
        }

        private bool IsSupportClaimedByOther(
            HexCastleAssaultUnit source,
            HexCastleAssaultUnit target,
            HexCastleAssaultSupportAction action)
        {
            if (source == null || target == null || action == HexCastleAssaultSupportAction.None)
            {
                return false;
            }

            var key = new SupportClaimKey(target.GetInstanceID(), action);
            return supportClaims.TryGetValue(key, out var claim) &&
                   claim.OwnerId != source.GetInstanceID() && claim.ExpiresAt > Time.time;
        }

        private void ClaimSupport(
            HexCastleAssaultUnit source,
            HexCastleAssaultUnit target,
            HexCastleAssaultSupportAction action,
            float durationSeconds)
        {
            if (source == null || target == null || action == HexCastleAssaultSupportAction.None)
            {
                return;
            }

            var key = new SupportClaimKey(target.GetInstanceID(), action);
            supportClaims[key] = new SupportClaimRecord
            {
                OwnerId = source.GetInstanceID(),
                ExpiresAt = Time.time + Mathf.Max(0.05f, durationSeconds)
            };
        }

        private void ReleaseSupportClaim(
            HexCastleAssaultUnit source,
            HexCastleAssaultUnit target,
            HexCastleAssaultSupportAction action)
        {
            if (source == null || target == null || action == HexCastleAssaultSupportAction.None)
            {
                return;
            }

            var key = new SupportClaimKey(target.GetInstanceID(), action);
            if (supportClaims.TryGetValue(key, out var claim) && claim.OwnerId == source.GetInstanceID())
            {
                supportClaims.Remove(key);
            }
        }

        private void ReleaseSupportClaims(HexCastleAssaultUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            var ownerId = unit.GetInstanceID();
            foreach (var key in supportClaims
                         .Where(value => value.Value.OwnerId == ownerId)
                         .Select(value => value.Key)
                         .ToArray())
            {
                supportClaims.Remove(key);
            }
        }

        private void PruneSupportClaims()
        {
            foreach (var key in supportClaims
                         .Where(value => value.Value.ExpiresAt <= Time.time)
                         .Select(value => value.Key)
                         .ToArray())
            {
                supportClaims.Remove(key);
            }
        }

        private void IncrementTopology()
        {
            topologyRevision++; costRevision++; topologyVersion++;
            navigation?.Invalidate();
            for (var i = 0; i < units.Count; i++) units[i]?.NotifyWorldChange(default, true, true);
        }

        private void PruneUnits()
        {
            for (var index = units.Count - 1; index >= 0; index--)
            {
                if (units[index] == null)
                {
                    units.RemoveAt(index);
                }
            }

            if (strategicCursor >= units.Count)
            {
                strategicCursor = 0;
            }
        }

        private void PruneThreatRecords()
        {
            for (var index = threatRecords.Count - 1; index >= 0; index--)
            {
                var record = threatRecords[index];
                if (record == null || !record.Target.IsValid ||
                    Time.time - record.ReportedAt > ThreatRecordSeconds)
                {
                    threatRecords.RemoveAt(index);
                }
            }
        }

        private float ResolveDeterministic01(HexCastleAssaultUnit unit, int salt)
        {
            unitSpawnOrders.TryGetValue(unit.GetInstanceID(), out var spawnOrder);
            unchecked
            {
                uint value = (uint)(stageSeed * 73856093 ^ spawnOrder * 19349663 ^ salt * 83492791);
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                value ^= value >> 16;
                return (value & 0x00FFFFFFu) / 16777216f;
            }
        }

        private bool IsOnAssaultFront(HexCastleAssaultRoutePlan route, HexCastleCellRuntime target)
        {
            if (route == null || !route.HasFirstObstacle || target == null) return false;
            if (target.Coordinates == route.FirstObstacle) return true;
            if (target.Coordinates.DistanceTo(route.FirstObstacle) != 1 ||
                !cells.TryGetValue(route.FirstObstacle, out var anchor) || anchor == null) return false;
            return target.DefenseLayer == anchor.DefenseLayer && IsRingWall(target) == IsRingWall(anchor);
        }

        private static int ResolveDecisionRouteId(int sector, HexCoordinates anchor)
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + sector;
                hash = hash * 31 + anchor.Q;
                hash = hash * 31 + anchor.R;
                return hash;
            }
        }

        private static HexCastleAssaultRoutePolicy ResolveRoutePolicy(HexCastleAssaultPattern pattern)
        {
            switch (pattern)
            {
                case HexCastleAssaultPattern.ResourceRaider:
                    return HexCastleAssaultRoutePolicy.ResourceRaider;
                case HexCastleAssaultPattern.TurretHunter:
                    return HexCastleAssaultRoutePolicy.TurretHunter;
                case HexCastleAssaultPattern.WallBreaker:
                    return HexCastleAssaultRoutePolicy.WallBreaker;
                case HexCastleAssaultPattern.ThreatSuppressor:
                    return HexCastleAssaultRoutePolicy.DirectAdvance;
                default:
                    return HexCastleAssaultRoutePolicy.Balanced;
            }
        }

        private bool IsOuterRingWall(HexCastleCellRuntime cell)
        {
            return cell != null && cell.DefenseLayer == defenseLayerCount &&
                   cell.WallRole != HexCastleWallRole.Partition &&
                   (cell.Kind == HexCastleCellKind.Wall || cell.Kind == HexCastleCellKind.Tower ||
                    cell.Kind == HexCastleCellKind.Gate);
        }

        private static bool IsRingWall(HexCastleCellRuntime cell)
        {
            return cell != null && cell.WallRole != HexCastleWallRole.None &&
                   cell.WallRole != HexCastleWallRole.Partition &&
                   (cell.Kind == HexCastleCellKind.Wall || cell.Kind == HexCastleCellKind.Tower ||
                    cell.Kind == HexCastleCellKind.Gate);
        }

        private static bool IsGeneralOpportunityStructure(HexCastleCellRuntime cell)
        {
            return cell != null && cell.BuildingRole != HexCastleBuildingRole.Turret &&
                   (cell.Kind == HexCastleCellKind.Building ||
                    cell.Kind == HexCastleCellKind.RewardBuilding ||
                    cell.Kind == HexCastleCellKind.DefenseBuilding);
        }

        private static bool IsSpecializedStructure(
            HexCastleCellRuntime cell,
            HexCastleAssaultPattern pattern)
        {
            if (pattern == HexCastleAssaultPattern.TurretHunter)
            {
                return cell.BuildingRole == HexCastleBuildingRole.Turret;
            }

            return cell.BuildingRole == HexCastleBuildingRole.GoldStorage ||
                   cell.BuildingRole == HexCastleBuildingRole.EquipmentForge ||
                   cell.BuildingRole == HexCastleBuildingRole.KeyVault;
        }

        private static void ApplySupportFocus(
            HexCastleAssaultSupportFocus focus,
            ref float healScore,
            ref float defenseScore,
            ref float attackScore)
        {
            switch (focus)
            {
                case HexCastleAssaultSupportFocus.AttackBuff:
                    attackScore += 0.45f;
                    break;
                case HexCastleAssaultSupportFocus.DefenseBuff:
                    defenseScore += 0.45f;
                    break;
                case HexCastleAssaultSupportFocus.Recovery:
                    healScore += 0.45f;
                    break;
            }
        }
    }
}
