using System;
using System.Collections.Generic;
using System.Linq;
using ProjectMT.Shared.Combat;
using ProjectMT.Shared.Unit;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    [DisallowMultipleComponent]
    public sealed class HexCastleAssaultUnit : MonoBehaviour // Hex Cell 전략과 실행을 분리한 공격 유닛
    {
        private const float TargetAwarenessInterval = 0.45f;
        private const float ThreatMemorySeconds = 3.5f;

        private IReadOnlyList<HexCoordinates> legacyPath;
        private IReadOnlyList<HexCoordinates> movementPath = Array.Empty<HexCoordinates>();
        private IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cellTargets;
        private HexCastleAssaultWorld assaultWorld;
        private HexCastleAssaultTarget currentTarget;
        private HexCastleAssaultTarget committedTarget;
        private HexCastleAssaultTarget recentThreat;
        private HexCastleAssaultIntentKind currentIntent;
        private HexCastleAssaultIntentKind committedIntent;
        private HexCastleAssaultSupportAction currentSupportAction;
        private HexCastleAssaultAIProfile aiProfile;
        private float cellSize;
        private Vector3 worldOrigin;
        private float moveSpeed;
        private float attackDamage;
        private float attackInterval;
        private float attackRange;
        private float nextAttackTime;
        private int pathIndex;
        private bool active;
        private float maximumHealth;
        private float currentHealth;
        private Renderer unitRenderer;
        private Vector3 baseScale;
        private float groundOffset = 0.42f;
        private MonsterAnimationDriver animationDriver;
        private MonsterRuntimeAssetSet runtimeAssetSet;
        private UnitVisualFeedback visualFeedback;
        private bool usesFormalVisual;
        private bool attackActionRunning;
        private int nextActionSequenceId;
        private HexCoordinates pendingLegacyAttackCoordinates;
        private HexCastleAssaultTarget pendingTarget;
        private Vector3 pendingAttackPosition;
        private bool strategicDecisionRequested;
        private float nextAwarenessAt;
        private int decisionTopologyVersion;
        private float recentThreatRemaining;
        private float recentDamagePerSecond;
        private float attackBuffRemaining;
        private float attackDamageMultiplier = 1f;
        private float defenseBuffRemaining;
        private float incomingDamageMultiplier = 1f;
        private float supportCooldownRemaining;
        private float trapMovementLockRemaining;
        private float trapSlowRemaining;
        private float trapMoveSpeedMultiplier = 1f;
        private bool dynamicRuntime;
        private string unitId = string.Empty;
        private readonly HashSet<int> evaluatedOpportunityLayers = new HashSet<int>();
        private readonly Dictionary<int, int> specialistTargetCounts = new Dictionary<int, int>();
        private readonly HexMonsterPassiveRuntime passiveRuntime = new HexMonsterPassiveRuntime();


        [SerializeField, Range(0.05f, 0.9f)] private float occupancyRadiusRatio = 0.30f;
        private HexCastleAssaultDecision activePlan;
        private int finalApproachIndex;
        private bool motionActive, motionFinal, finishSafeSegmentOnly;
        private HexCoordinates motionFrom, motionTo;
        private Vector3 motionStart, motionEnd;
        private float retryNotBefore, avoidSlotUntil;
        private HexAttackSlotKey avoidSlot;
        private bool hasAvoidSlot;
        private int plannedDamageBand, plannedSpeedBand;
        public int RuntimeGeneration { get; private set; }
        public int PlanGeneration { get; private set; }
        public HexAssaultExecutionState ExecutionState { get; private set; } = HexAssaultExecutionState.Inactive;
        public HexAssaultFailureReason LastDecisionReason { get; private set; }
        public int ConsecutiveDecisionFailures { get; private set; }
        public bool RecoveryRequested { get; private set; }
        public float LastActualProgressTime { get; private set; }
        public float LastDecisionTime { get; private set; }
        public float RetryNotBefore => retryNotBefore;
        public float SlotBodyRadius => Mathf.Max(0.05f, occupancyRadiusRatio) * Mathf.Max(0.1f, cellSize) * 0.8660254038f;
        public float GroundWorldY => worldOrigin.y + groundOffset;
        public float EffectiveMoveSpeed => Mathf.Max(0.1f, moveSpeed * CurrentMoveSpeedMultiplier);
        public HexSlotLease CurrentSlotLease => activePlan.SlotLease;
        public HexCastleAssaultDecision ActivePlan => activePlan;
        public bool IsInSafeMotion => motionActive && CanDynamicStep(motionFrom, motionTo);
        public Vector3 MotionStart => motionStart;
        public Vector3 MotionEnd => motionEnd;
        public HexCoordinates MotionDestinationCell => motionTo;
        public void ConfigureOccupancyRadius(float ratio)
        {
            if (!HexAttackSlotAllocator.Finite(ratio) || ratio < 0.05f || ratio > 0.9f)
                throw new ArgumentOutOfRangeException(nameof(ratio));
            if (activePlan.SlotLease.IsValid) throw new InvalidOperationException("Release the current spatial lease before resizing a unit.");
            occupancyRadiusRatio = ratio;
        }

        public bool ReachedPalace { get; private set; }
        public HexCoordinates CurrentCoordinates { get; private set; }
        public int DestroyedTargets { get; private set; }
        public float CurrentHealth => currentHealth;
        public float MaxHealth => maximumHealth;
        public float HealthRatio => maximumHealth <= 0f ? 0f : Mathf.Clamp01(currentHealth / maximumHealth);
        public bool IsAlive => currentHealth > 0f;
        public bool UsesFormalVisual => usesFormalVisual;
        public bool HasFormalAnimation => usesFormalVisual && animationDriver != null && animationDriver.IsReady;
        public float DeathPresentationDuration { get; private set; } = 0.38f;
        public float MoveSpeed => moveSpeed;
        public float TrapMovementLockRemaining => trapMovementLockRemaining;
        public float TrapSlowRemaining => trapSlowRemaining;
        public float CurrentMoveSpeedMultiplier => trapSlowRemaining > 0f
            ? trapMoveSpeedMultiplier
            : 1f;
        public float EstimatedDamagePerSecond => Mathf.Max(0.1f, attackDamage * attackDamageMultiplier) /
                                                  passiveRuntime.ResolveAttackInterval(attackInterval);
        public float BaseAttackDamage => attackDamage;
        public HexMonsterPassiveRuntime PassiveRuntime => passiveRuntime;
        public float RecentDamagePerSecond => recentDamagePerSecond;
        public bool HasAttackBuff => attackBuffRemaining > 0f;
        public bool HasDefenseBuff => defenseBuffRemaining > 0f;
        public bool CanPerformSupportAction => supportCooldownRemaining <= 0f;
        public bool HasCombatTarget => currentTarget.IsValid &&
                                       currentTarget.Kind != HexCastleAssaultTargetKind.Ally;
        public int AttackRangeCells => Mathf.Max(
            1,
            Mathf.CeilToInt(attackRange / Mathf.Max(0.1f, cellSize * 1.7320508f)));
        public int SupportRangeCells => aiProfile == null
            ? 1
            : Mathf.Max(
                1,
                Mathf.CeilToInt(aiProfile.SupportRange / Mathf.Max(0.1f, cellSize * 1.7320508f)));
        public int ExpectedDefenseLayer { get; private set; }
        public int RouteId { get; private set; }
        internal int StableSpawnOrder => assaultWorld != null ? assaultWorld.ResolveSpawnOrder(this) : 0;
        public int RouteSector { get; private set; }
        public HexCastleAssaultAIProfile AIProfile => aiProfile;
        public HexCastleAssaultTarget CurrentTarget => currentTarget;
        public HexCastleAssaultTarget CommittedTarget => committedTarget.IsValid ? committedTarget : default;
        public HexCastleAssaultIntentKind CurrentIntent => currentIntent;
        public HexCastleAssaultIntentKind CommittedIntent => committedIntent;
        public HexCastleAssaultSupportAction CurrentSupportAction => currentSupportAction;
        public bool HasSelectedInitialWall { get; private set; }
        public bool RouteCostReviewRequested { get; private set; } = true;
        private float routeReviewAt = float.PositiveInfinity;
        internal void DeferRouteCostReview(float delay) => routeReviewAt = Mathf.Min(routeReviewAt, Time.time + delay);
        public float LastTargetSwitchTime { get; private set; }
        public int AdvanceMoveMs { get; private set; }
        public int AdvanceDestroyMs { get; private set; }
        public int AdvanceContinuationMs { get; private set; }
        public int AdvanceAlternativeMs { get; private set; }
        public HexCoordinates? AdvanceAlternativeTarget { get; private set; }
        internal void RecordAdvanceCost(int move, int destroy, int continuation, HexCoordinates? alternative, int alternativeMs)
        {
            AdvanceMoveMs = move; AdvanceDestroyMs = destroy; AdvanceContinuationMs = continuation;
            AdvanceAlternativeTarget = alternative; AdvanceAlternativeMs = alternativeMs;
        }
        internal void CompleteRouteCostReview() => RouteCostReviewRequested = false;

        internal float RemainingMovementSeconds()
        {
            var position = transform.position; var length = 0f;
            for (var i = pathIndex + 1; i < movementPath.Count - 1; i++)
            {
                var next = worldOrigin + movementPath[i].ToWorld(cellSize); next.y = position.y;
                length += Vector3.Distance(position, next); position = next;
            }
            for (var i = finalApproachIndex; i < activePlan.FinalApproach.Count; i++)
            {
                var next = activePlan.FinalApproach[i]; next.y = position.y;
                length += Vector3.Distance(position, next); position = next;
            }
            return length / EffectiveMoveSpeed;
        }
        public bool HasBreachCostEvaluation { get; private set; }
        public HexCoordinates LastBreachTargetCoordinates { get; private set; }
        public float LastBreachMovementSeconds { get; private set; }
        public float LastBreachDestructionSeconds { get; private set; }
        public float LastBreachTotalSeconds { get; private set; }
        public float LastBreachExpectedDamagePerSecond { get; private set; }
        public bool HasAlternativeBreachCost { get; private set; }
        public HexCoordinates AlternativeBreachTargetCoordinates { get; private set; }
        public float AlternativeBreachMovementSeconds { get; private set; }
        public float AlternativeBreachDestructionSeconds { get; private set; }
        public float AlternativeBreachTotalSeconds { get; private set; }
        public HexCastleAssaultTarget RecentThreat => recentThreatRemaining > 0f && recentThreat.IsValid
            ? recentThreat
            : default;
        public bool NeedsStrategicDecision => dynamicRuntime && active && IsAlive && !attackActionRunning &&
            Time.time >= retryNotBefore && !IsInSafeMotion &&
            (strategicDecisionRequested || !currentTarget.IsValid || Time.time >= nextAwarenessAt || Time.time >= routeReviewAt ||
             plannedDamageBand != DamageBand() || plannedSpeedBand != SpeedBand());

        public event Action<HexCastleAssaultUnit, DamageReport> Damaged;
        public event Action<HexCastleAssaultUnit> Died;
        public event Action<HexCastleAssaultUnit, HexCoordinates> EnteredCell;

        internal void RecordBreachCostEvaluation(
            HexCoordinates selectedTarget,
            float movementSeconds,
            float destructionSeconds,
            float expectedDamagePerSecond,
            HexCoordinates? alternativeTarget,
            float alternativeMovementSeconds,
            float alternativeDestructionSeconds)
        {
            HasBreachCostEvaluation = true;
            LastBreachTargetCoordinates = selectedTarget;
            LastBreachMovementSeconds = Mathf.Max(0f, movementSeconds);
            LastBreachDestructionSeconds = Mathf.Max(0f, destructionSeconds);
            LastBreachTotalSeconds = LastBreachMovementSeconds + LastBreachDestructionSeconds;
            LastBreachExpectedDamagePerSecond = Mathf.Max(0f, expectedDamagePerSecond);
            HasAlternativeBreachCost = alternativeTarget.HasValue;
            AlternativeBreachTargetCoordinates = alternativeTarget.GetValueOrDefault();
            AlternativeBreachMovementSeconds = alternativeTarget.HasValue ? Mathf.Max(0f, alternativeMovementSeconds) : 0f;
            AlternativeBreachDestructionSeconds = alternativeTarget.HasValue ? Mathf.Max(0f, alternativeDestructionSeconds) : 0f;
            AlternativeBreachTotalSeconds = AlternativeBreachMovementSeconds + AlternativeBreachDestructionSeconds;
        }

        public void ConfigureForRoute(
            HexRouteResult route,
            float targetCellSize,
            float targetMoveSpeed,
            float targetAttackDamage,
            float targetAttackInterval,
            float targetHealth = 320f)
        {
            ConfigureLegacy(
                route,
                null,
                targetCellSize,
                Vector3.zero,
                targetMoveSpeed,
                targetAttackDamage,
                targetAttackInterval,
                targetHealth,
                0.42f,
                null,
                targetCellSize * 0.82f);
        }

        public void ConfigureForCells(
            HexRouteResult route,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            float targetCellSize,
            Vector3 targetWorldOrigin,
            float targetMoveSpeed,
            float targetAttackDamage,
            float targetAttackInterval,
            float targetHealth = 320f)
        {
            ConfigureLegacy(
                route,
                runtimeCells,
                targetCellSize,
                targetWorldOrigin,
                targetMoveSpeed,
                targetAttackDamage,
                targetAttackInterval,
                targetHealth,
                0.42f,
                null,
                targetCellSize * 0.82f);
        }

        public void ConfigureForPartyUnit(
            HexRouteResult route,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            float targetCellSize,
            Vector3 targetWorldOrigin,
            BattleUnitSnapshot unit)
        {
            if (unit == null)
            {
                throw new ArgumentNullException(nameof(unit));
            }

            var stats = unit.Stats;
            ConfigureLegacy(
                route,
                runtimeCells,
                targetCellSize,
                targetWorldOrigin,
                Mathf.Max(0.1f, stats.moveSpeed),
                Mathf.Max(1f, stats.damage),
                Mathf.Max(0.05f, stats.attackInterval),
                Mathf.Max(1f, stats.maxHealth),
                0.02f,
                unit,
                Mathf.Max(0.35f, stats.attackRange));
        }

        public void ConfigureForPartyUnit(
            HexCastleAssaultWorld world,
            HexCoordinates start,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            float targetCellSize,
            Vector3 targetWorldOrigin,
            BattleUnitSnapshot unit)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }
            if (runtimeCells == null)
            {
                throw new ArgumentNullException(nameof(runtimeCells));
            }
            if (unit == null)
            {
                throw new ArgumentNullException(nameof(unit));
            }

            ShutdownRuntime();
            assaultWorld = world;
            cellTargets = runtimeCells;
            cellSize = Mathf.Max(0.1f, targetCellSize);
            worldOrigin = targetWorldOrigin;
            unitId = unit.UnitId ?? string.Empty;
            var stats = unit.Stats;
            ConfigureCommon(
                Mathf.Max(0.1f, stats.moveSpeed),
                Mathf.Max(1f, stats.damage),
                Mathf.Max(0.05f, stats.attackInterval),
                Mathf.Max(1f, stats.maxHealth),
                0.02f,
                unit,
                Mathf.Max(0.35f, stats.attackRange));
            dynamicRuntime = true;
            legacyPath = null;
            movementPath = new[] { start };
            pathIndex = 0;
            CurrentCoordinates = start;
            ExpectedDefenseLayer = assaultWorld.DefenseLayerCount;
            aiProfile = assaultWorld.RegisterUnit(this, unitId);
            transform.position = ResolvePosition(start);
            passiveRuntime.Initialize(
                this,
                assaultWorld,
                unit.PassiveSkill,
                unit.Level,
                UnitEntryReason.CastleManualDeployment);
            strategicDecisionRequested = true;
            nextAwarenessAt = Time.time + ResolveDecisionSpread();
            decisionTopologyVersion = 0;
            active = true;
        }

        public void RefreshStrategicDecision()
        {
            if (!NeedsStrategicDecision || assaultWorld == null) return;
            if (Time.time >= routeReviewAt) { RouteCostReviewRequested = true; routeReviewAt = float.PositiveInfinity; }
            if (plannedDamageBand != DamageBand() || plannedSpeedBand != SpeedBand()) RouteCostReviewRequested = true;
            strategicDecisionRequested = false;
            LastDecisionTime = Time.time;
            nextAwarenessAt = Time.time + TargetAwarenessInterval + ResolveDecisionSpread();
            var result = assaultWorld.ResolveDecision(this);
            LastDecisionReason = result.Reason;
            if (result.Status == HexAssaultDecisionStatus.Accepted)
            {
                ConsecutiveDecisionFailures = 0; retryNotBefore = 0f; RecoveryRequested = false;
                ApplyPlan(result.Plan);
                return;
            }
            ConsecutiveDecisionFailures++;
            retryNotBefore = result.RetryAt;
            if (result.Plan.IsValid) ApplyPlan(result.Plan);
            else if (CanFinishCurrentEdge())
            {
                finishSafeSegmentOnly = true;
                assaultWorld.Record(HexAssaultTraceKind.MotionSegmentPreserved, this, currentTarget.InstanceId, activePlan.SlotLease, result.Reason);
            }
            else
            {
                // Transform을 이전 논리 셀로 되돌리지 않는다.
                motionActive = false;
                movementPath = new[] { CurrentCoordinates }; pathIndex = 0;
                ExecutionState = RecoveryRequested ? HexAssaultExecutionState.Recovering : HexAssaultExecutionState.Holding;
                animationDriver?.PlayIdle();
            }
        }

        private void ApplyPlan(HexCastleAssaultDecision decision)
        {
            var identical = IsSameExecutionPlan(activePlan, decision);
            decisionTopologyVersion = decision.TopologyVersion;
            plannedDamageBand = DamageBand(); plannedSpeedBand = SpeedBand();
            if (identical) return; // Preserve both pathIndex and the in-cell alignment progress.
            var startsCommitment = IsCommitmentIntent(decision.Intent) &&
                (!committedTarget.IsValid || committedTarget.InstanceId != decision.Target.InstanceId || committedIntent != decision.Intent);
            if (currentTarget.InstanceId != decision.Target.InstanceId) LastTargetSwitchTime = Time.time;
            currentTarget = decision.Target; currentIntent = decision.Intent; currentSupportAction = decision.SupportAction;
            if (startsCommitment)
            {
                committedTarget = decision.Target; committedIntent = decision.Intent;
                if (decision.Intent == HexCastleAssaultIntentKind.InitialBreach || decision.Intent == HexCastleAssaultIntentKind.LocalFallback)
                    HasSelectedInitialWall = true;
                else if (decision.Intent == HexCastleAssaultIntentKind.Specialist)
                {
                    specialistTargetCounts.TryGetValue(ExpectedDefenseLayer, out var count);
                    specialistTargetCounts[ExpectedDefenseLayer] = count + 1;
                }
            }
            activePlan = decision;
            movementPath = decision.MovementPath; pathIndex = 0; finalApproachIndex = 0;
            finishSafeSegmentOnly = false; motionActive = false;
            RouteId = decision.RouteId; RouteSector = decision.SectorId; PlanGeneration++;
            ExecutionState = HexAssaultExecutionState.Traversing;
        }

        private static bool IsSameExecutionPlan(HexCastleAssaultDecision current, HexCastleAssaultDecision next)
        {
            return current.SlotLease.IsValid && current.SlotLease == next.SlotLease &&
                current.Target.InstanceId == next.Target.InstanceId && current.TargetCell == next.TargetCell &&
                current.Approach == next.Approach && current.RouteId == next.RouteId && current.SectorId == next.SectorId &&
                current.Intent == next.Intent && current.SupportAction == next.SupportAction && current.PlanKind == next.PlanKind &&
                SameCoordinates(current.MovementPath, next.MovementPath) && SamePositions(current.FinalApproach, next.FinalApproach);
        }

        private static bool SameCoordinates(IReadOnlyList<HexCoordinates> left, IReadOnlyList<HexCoordinates> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool SamePositions(IReadOnlyList<Vector3> left, IReadOnlyList<Vector3> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++)
                if ((left[i] - right[i]).sqrMagnitude > 0.00000001f) return false;
            return true;
        }

        public void RequestStrategicDecision(bool immediate)
        {
            strategicDecisionRequested = true;
            if (immediate) { nextAwarenessAt = 0f; retryNotBefore = 0f; }
        }

#if UNITY_EDITOR
        public void EditorOverrideAIProfile(HexCastleAssaultAIProfile profile)
        {
            if (profile == null) return;
            aiProfile = profile;
            RequestStrategicDecision(true);
        }
#endif

        public bool HasEvaluatedOpportunity(int defenseLayer)
        {
            return evaluatedOpportunityLayers.Contains(defenseLayer);
        }

        public void MarkOpportunityEvaluated(int defenseLayer)
        {
            evaluatedOpportunityLayers.Add(defenseLayer);
        }

        public bool CanSelectSpecialistTarget(int defenseLayer, int maximumCount)
        {
            specialistTargetCounts.TryGetValue(defenseLayer, out var count);
            return count < Mathf.Max(0, maximumCount);
        }

        public bool ApplyDamage(float amount)
        {
            return ApplyDamage(amount, transform.position + Vector3.up * groundOffset);
        }

        public bool ApplyDamage(float amount, Vector3 hitPoint)
        {
            return ApplyDamage(amount, hitPoint, null, null);
        }

        public bool ApplyDamage(
            float amount,
            Vector3 hitPoint,
            HexCastleGarrisonUnit sourceDefender,
            HexCastleCellRuntime sourceStructure)
        {
            if (!IsAlive || amount <= 0f)
            {
                return false;
            }

            if (sourceDefender != null && sourceDefender.IsAlive)
            {
                recentThreat = new HexCastleAssaultTarget(sourceDefender);
                recentThreatRemaining = ThreatMemorySeconds;
                assaultWorld?.ReportThreat(this, recentThreat);
                RequestStrategicDecision(true);
            }
            else if (sourceStructure != null && sourceStructure.IsAlive)
            {
                recentThreat = new HexCastleAssaultTarget(sourceStructure, false);
                recentThreatRemaining = ThreatMemorySeconds;
                assaultWorld?.ReportThreat(this, recentThreat);
                RequestStrategicDecision(true);
            }

            var requested = passiveRuntime.ResolveIncomingDamage(
                amount * incomingDamageMultiplier,
                out var shieldAbsorbed);
            if (requested <= 0f && shieldAbsorbed > 0f)
            {
                visualFeedback?.PlayHit();
                return true;
            }
            var appliedDamage = Mathf.Min(currentHealth, requested);
            currentHealth = Mathf.Max(0f, currentHealth - appliedDamage);
            recentDamagePerSecond += appliedDamage / 2.5f;
            var killed = currentHealth <= 0f;
            var report = new DamageReport(
                new DamageRequest(null, amount, hitPoint),
                appliedDamage,
                currentHealth,
                killed);
            visualFeedback?.PlayHit();
            HexCastleOverheadHealthBar.ShowDamage(
                transform,
                currentHealth,
                maximumHealth,
                true);
            if (!usesFormalVisual)
            {
                transform.localScale = baseScale * Mathf.Lerp(0.72f, 1f, currentHealth / maximumHealth);
            }
            Damaged?.Invoke(this, report);
            if (!killed)
            {
                return true;
            }

            active = false;
            attackActionRunning = false;
            HideHealthBar();
            assaultWorld?.UnregisterUnit(this);
            if (usesFormalVisual)
            {
                DeathPresentationDuration = animationDriver?.PlayDeath() ?? 0.38f;
            }
            else if (unitRenderer != null)
            {
                unitRenderer.enabled = false;
            }

            Died?.Invoke(this);
            return true;
        }

        public void ApplyTrapMovementLock(float duration)
        {
            if (IsAlive)
            {
                trapMovementLockRemaining = Mathf.Max(trapMovementLockRemaining, Mathf.Max(0f, duration));
            }
        }

        public void ApplyTrapSlow(float movementSpeedMultiplier, float duration)
        {
            if (!IsAlive || duration <= 0f)
            {
                return;
            }

            trapMoveSpeedMultiplier = Mathf.Min(
                trapSlowRemaining > 0f ? trapMoveSpeedMultiplier : 1f,
                Mathf.Clamp(movementSpeedMultiplier, 0.1f, 1f));
            trapSlowRemaining = Mathf.Max(trapSlowRemaining, duration);
        }

        public void ApplySupport(
            HexCastleAssaultSupportAction action,
            HexCastleAssaultAIProfile sourceProfile)
        {
            if (!IsAlive || sourceProfile == null)
            {
                return;
            }

            switch (action)
            {
                case HexCastleAssaultSupportAction.Heal:
                    currentHealth = Mathf.Min(
                        maximumHealth,
                        currentHealth + maximumHealth * sourceProfile.HealRatio);
                    break;
                case HexCastleAssaultSupportAction.AttackBuff:
                    attackDamageMultiplier = Mathf.Max(
                        attackDamageMultiplier,
                        1f + sourceProfile.AttackBuffRate);
                    attackBuffRemaining = Mathf.Max(attackBuffRemaining, sourceProfile.SupportDuration);
                    break;
                case HexCastleAssaultSupportAction.DefenseBuff:
                    incomingDamageMultiplier = Mathf.Min(
                        incomingDamageMultiplier,
                        sourceProfile.DefenseDamageMultiplier);
                    defenseBuffRemaining = Mathf.Max(defenseBuffRemaining, sourceProfile.SupportDuration);
                    break;
            }
        }

        public float HealPassive(float amount)
        {
            if (!IsAlive || amount <= 0f)
            {
                return 0f;
            }

            var before = currentHealth;
            currentHealth = Mathf.Min(maximumHealth, currentHealth + amount);
            return currentHealth - before;
        }

        public void ShutdownRuntime()
        {
            passiveRuntime.Shutdown();
            motionActive = false; activePlan = default; ExecutionState = HexAssaultExecutionState.Inactive;
            HideHealthBar();
            assaultWorld?.UnregisterUnit(this);
            assaultWorld = null;
            currentTarget = default;
            committedTarget = default;
            recentThreat = default;
            currentIntent = HexCastleAssaultIntentKind.None;
            committedIntent = HexCastleAssaultIntentKind.None;
            currentSupportAction = HexCastleAssaultSupportAction.None;
            movementPath = Array.Empty<HexCoordinates>();
            legacyPath = null;
            cellTargets = null;
            aiProfile = null;
            runtimeAssetSet = null;
            evaluatedOpportunityLayers.Clear();
            specialistTargetCounts.Clear();
            HasSelectedInitialWall = false;
            RouteCostReviewRequested = true;
            LastTargetSwitchTime = float.NegativeInfinity;
            routeReviewAt = float.PositiveInfinity;
            AdvanceMoveMs = AdvanceDestroyMs = AdvanceContinuationMs = AdvanceAlternativeMs = 0;
            AdvanceAlternativeTarget = null;
            HasBreachCostEvaluation = false;
            HasAlternativeBreachCost = false;
            dynamicRuntime = false;
            strategicDecisionRequested = false;
            active = false;
            trapMovementLockRemaining = 0f;
            trapSlowRemaining = 0f;
            trapMoveSpeedMultiplier = 1f;
            EnteredCell = null;
        }

        private void OnDisable()
        {
            if (dynamicRuntime) ShutdownRuntime();
        }

        private void HideHealthBar()
        {
            if (TryGetComponent<HexCastleOverheadHealthBar>(out var healthBar))
            {
                healthBar.HideImmediately();
            }
        }

        private void ConfigureLegacy(
            HexRouteResult route,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            float targetCellSize,
            Vector3 targetWorldOrigin,
            float targetMoveSpeed,
            float targetAttackDamage,
            float targetAttackInterval,
            float targetHealth,
            float targetGroundOffset,
            BattleUnitSnapshot unit,
            float targetAttackRange)
        {
            if (route == null || !route.IsComplete)
            {
                throw new ArgumentException("완전한 육각 돌파 경로가 필요합니다.", nameof(route));
            }

            ShutdownRuntime();
            legacyPath = route.Path;
            movementPath = Array.Empty<HexCoordinates>();
            cellTargets = runtimeCells;
            cellSize = Mathf.Max(0.1f, targetCellSize);
            worldOrigin = targetWorldOrigin;
            ConfigureCommon(
                targetMoveSpeed,
                targetAttackDamage,
                targetAttackInterval,
                targetHealth,
                targetGroundOffset,
                unit,
                targetAttackRange);
            dynamicRuntime = false;
            pathIndex = 0;
            CurrentCoordinates = legacyPath[0];
            transform.position = ResolvePosition(CurrentCoordinates);
            active = true;
        }

        private void ConfigureCommon(
            float targetMoveSpeed,
            float targetAttackDamage,
            float targetAttackInterval,
            float targetHealth,
            float targetGroundOffset,
            BattleUnitSnapshot unit,
            float targetAttackRange)
        {
            RuntimeGeneration++;
            PlanGeneration = 0;
            HasBreachCostEvaluation = false;
            HasAlternativeBreachCost = false;
            activePlan = default; finalApproachIndex = 0;
            motionActive = motionFinal = finishSafeSegmentOnly = false;
            retryNotBefore = avoidSlotUntil = 0f; hasAvoidSlot = false;
            ConsecutiveDecisionFailures = 0; RecoveryRequested = false;
            LastActualProgressTime = Time.time; LastDecisionTime = Time.time;
            LastDecisionReason = HexAssaultFailureReason.None;
            ExecutionState = HexAssaultExecutionState.AwaitInitialPlan;
            moveSpeed = Mathf.Max(0.1f, targetMoveSpeed);
            attackDamage = Mathf.Max(1f, targetAttackDamage);
            attackInterval = Mathf.Max(0.05f, targetAttackInterval);
            attackRange = Mathf.Max(0.35f, targetAttackRange);
            maximumHealth = Mathf.Max(1f, targetHealth);
            currentHealth = maximumHealth;
            unitRenderer = GetComponentInChildren<Renderer>();
            baseScale = transform.localScale;
            groundOffset = Mathf.Max(0f, targetGroundOffset);
            usesFormalVisual = unit != null;
            runtimeAssetSet = unit?.RuntimeAssetSet;
            attackActionRunning = false;
            nextActionSequenceId = 0;
            nextAttackTime = Time.time + UnityEngine.Random.Range(0f, attackInterval * 0.35f);
            DeathPresentationDuration = 0.38f;
            ReachedPalace = false;
            DestroyedTargets = 0;
            RouteId = 0;
            RouteSector = 0;
            ExpectedDefenseLayer = 0;
            recentThreatRemaining = 0f;
            recentDamagePerSecond = 0f;
            attackBuffRemaining = 0f;
            attackDamageMultiplier = 1f;
            defenseBuffRemaining = 0f;
            incomingDamageMultiplier = 1f;
            supportCooldownRemaining = 0f;
            trapMovementLockRemaining = 0f;
            trapSlowRemaining = 0f;
            trapMoveSpeedMultiplier = 1f;
            visualFeedback = GetComponent<UnitVisualFeedback>();
            animationDriver = GetComponent<MonsterAnimationDriver>();
            if (!usesFormalVisual)
            {
                return;
            }

            var actor = GetComponent<UnitActor>();
            if (actor != null)
            {
                actor.enabled = false;
            }

            visualFeedback?.SetTint(unit.VisualTint);
            if (animationDriver != null && !animationDriver.Initialize(unit.RuntimeAssetSet))
            {
                animationDriver = null;
            }
        }

        private void Update()
        {
            if (!Application.isPlaying || !active || !IsAlive)
            {
                return;
            }

            TickRuntimeEffects(Time.deltaTime);
            passiveRuntime.Tick(Time.deltaTime);
            if (dynamicRuntime)
            {
                TickDynamicRuntime(Time.deltaTime);
            }
            else
            {
                TickLegacyRuntime(Time.deltaTime);
            }
        }

        private void TickDynamicRuntime(float deltaTime)
        {
            if (attackActionRunning)
            {
                ExecutionState = HexAssaultExecutionState.Attacking;
                TickAttackAction(deltaTime); return;
            }
            if (motionActive)
            {
                if (trapMovementLockRemaining > 0f) { animationDriver?.PlayIdle(); return; }
                if (!CanDynamicStep(motionFrom, motionTo))
                {
                    motionActive = false;
                    MarkNoProgress(HexAssaultFailureReason.NoReachableApproach);
                    return;
                }
                if (motionFinal && !assaultWorld.ApproachPlanner.SegmentClear(this, transform.position, motionEnd))
                {
                    motionActive = false;
                    if (TryRefreshFinalApproach()) { BeginFinalApproach(deltaTime); return; }
                    MarkNoProgress(HexAssaultFailureReason.SlotApproachBlocked);
                    return;
                }
                AdvanceMotion(deltaTime); return;
            }
            if (movementPath != null && pathIndex < movementPath.Count - 1)
            {
                if (trapMovementLockRemaining > 0f) { animationDriver?.PlayIdle(); return; }
                var next = movementPath[pathIndex + 1];
                if (!CanDynamicStep(CurrentCoordinates, next))
                { MarkNoProgress(HexAssaultFailureReason.NoReachableApproach); return; }
                if (!finishSafeSegmentOnly && activePlan.FinalApproach != null && activePlan.FinalApproach.Count > 0 && pathIndex == movementPath.Count - 2)
                { BeginFinalApproach(deltaTime); return; }
                BeginMotion(next, ResolvePosition(next), false);
                AdvanceMotion(deltaTime); return;
            }
            if (activePlan.SlotLease.IsValid && finalApproachIndex < activePlan.FinalApproach.Count)
            {
                if (trapMovementLockRemaining > 0f) { animationDriver?.PlayIdle(); return; }
                BeginFinalApproach(deltaTime); return;
            }
            if (activePlan.PlanKind == HexAssaultPlanKind.Hold)
            {
                ExecutionState = HexAssaultExecutionState.Holding;
                strategicDecisionRequested = true; // 재판단 시점은 대기 시간이 결정한다.
                CheckNoProgress(); animationDriver?.PlayIdle(); return;
            }
            if (!currentTarget.IsValid)
            {
                ExecutionState = HexAssaultExecutionState.Holding;
                strategicDecisionRequested = true;
                CheckNoProgress(); animationDriver?.PlayIdle(); return;
            }
            if (currentTarget.Kind == HexCastleAssaultTargetKind.Ally)
            { TickSupportTarget(); return; }
            if (!CanAttackTarget(currentTarget))
            {
                strategicDecisionRequested = true;
                CheckNoProgress(); animationDriver?.PlayIdle(); return;
            }
            ExecutionState = HexAssaultExecutionState.Attacking;
            animationDriver?.PlayIdle();
            FaceTowards(ResolveTargetPosition(currentTarget), deltaTime);
            if (Time.time >= nextAttackTime) StartDynamicAttack(currentTarget);
        }

        private void BeginMotion(HexCoordinates destination, Vector3 position, bool final)
        {
            motionActive = true; motionFinal = final;
            motionFrom = CurrentCoordinates; motionTo = destination;
            motionStart = transform.position; motionEnd = position; motionEnd.y = GroundWorldY;
            ExecutionState = final ? HexAssaultExecutionState.Aligning : HexAssaultExecutionState.Traversing;
        }

        private void AdvanceMotion(float deltaTime)
        {
            var before = transform.position;
            MoveTowards(motionEnd, deltaTime);
            if (HexAttackSlotAllocator.SqrPlanar(transform.position - before) > 0.0000001f)
                LastActualProgressTime = Time.time;
            if (HexAttackSlotAllocator.SqrPlanar(transform.position - motionEnd) > 0.0025f) return;
            motionActive = false;
            if (motionFinal)
            {
                finalApproachIndex++;
                if (finalApproachIndex < activePlan.FinalApproach.Count) return;
                CompleteCellEntry(motionTo);
                pathIndex = Math.Max(0, movementPath.Count - 1);
                if (assaultWorld.SlotAllocator.Arrive(this, activePlan.SlotLease))
                {
                    LastActualProgressTime = Time.time;
                    assaultWorld.Record(HexAssaultTraceKind.SlotArrived, this, currentTarget.InstanceId, activePlan.SlotLease);
                }
                else { strategicDecisionRequested = true; retryNotBefore = 0f; }
            }
            else
            {
                pathIndex++; CompleteCellEntry(motionTo);
            }
            if (finishSafeSegmentOnly)
            {
                movementPath = new[] { CurrentCoordinates }; pathIndex = 0;
                activePlan = default; finishSafeSegmentOnly = false; strategicDecisionRequested = true;
            }
        }

        private void CompleteCellEntry(HexCoordinates cell)
        {
            if (CurrentCoordinates == cell) return;
            CurrentCoordinates = cell; EnteredCell?.Invoke(this, cell); UpdateDefenseProgress(cell);
        }

        private void BeginFinalApproach(float deltaTime)
        {
            if (finalApproachIndex >= activePlan.FinalApproach.Count) return;
            var destination = activePlan.FinalApproach[finalApproachIndex];
            if (!assaultWorld.ApproachPlanner.SegmentClear(this, transform.position, destination))
            {
                if (!TryRefreshFinalApproach()) { MarkNoProgress(HexAssaultFailureReason.SlotApproachBlocked); return; }
                destination = activePlan.FinalApproach[finalApproachIndex];
                if (!assaultWorld.ApproachPlanner.SegmentClear(this, transform.position, destination))
                { MarkNoProgress(HexAssaultFailureReason.SlotApproachBlocked); return; }
            }
            BeginMotion(activePlan.Approach, destination, true); AdvanceMotion(deltaTime);
        }

        private bool TryRefreshFinalApproach()
        {
            if (!activePlan.SlotLease.IsValid || !assaultWorld.SlotAllocator.Owns(this, activePlan.SlotLease) ||
                activePlan.PlanKind != HexAssaultPlanKind.Hold && !currentTarget.IsValid) return false;
            var destination = assaultWorld.SlotAllocator.Position(activePlan.SlotLease.Key);
            destination.y = GroundWorldY;
            if (!assaultWorld.ApproachPlanner.TryFinalPath(this, transform.position, destination, activePlan.Approach,
                    out var refreshed, out _)) return false;
            activePlan = new HexCastleAssaultDecision(activePlan.Target, movementPath, activePlan.Approach,
                activePlan.RouteId, activePlan.SectorId, activePlan.TopologyVersion, activePlan.Intent,
                activePlan.SupportAction, activePlan.SlotLease, refreshed, activePlan.PlanKind);
            finalApproachIndex = 0; PlanGeneration++;
            assaultWorld.Record(HexAssaultTraceKind.PlanCommitted, this, currentTarget.InstanceId,
                activePlan.SlotLease, HexAssaultFailureReason.SlotApproachBlocked);
            return true; // 예약은 유지하고 현재 점유만 반영해 셀 내부 접근을 다시 잇는다.
        }

        private bool CanDynamicStep(HexCoordinates from, HexCoordinates to)
        {
            if (cellTargets == null || !cellTargets.TryGetValue(from, out var a) || a == null ||
                !cellTargets.TryGetValue(to, out var b) || b == null || b.Kind == HexCastleCellKind.Palace) return false;
            if (from == to) return a.CanTraverse(HexCastleTraversalFaction.Assault);
            for (var d = 0; d < 6; d++)
                if (from.Neighbor(d) == to) return HexRoutePlanner.CanTraverseStep(a, b, d, HexCastleTraversalFaction.Assault);
            return false;
        }

        private bool CanFinishCurrentEdge()
        {
            if (IsInSafeMotion) return true;
            return movementPath != null && pathIndex < movementPath.Count - 1 &&
                CanDynamicStep(CurrentCoordinates, movementPath[pathIndex + 1]);
        }

        private void CheckNoProgress()
        {
            var grace = Mathf.Max(1.5f, Mathf.Max(attackInterval * 2f, cellSize * 3.4641016f / EffectiveMoveSpeed));
            if (Time.time - LastActualProgressTime < grace || Time.time < retryNotBefore) return;
            MarkNoProgress(HexAssaultFailureReason.NoProgress);
        }

        private void MarkNoProgress(HexAssaultFailureReason reason)
        {
            LastDecisionReason = reason;
            if (!RecoveryRequested)
            {
                if (activePlan.SlotLease.IsValid) { avoidSlot = activePlan.SlotLease.Key; hasAvoidSlot = true; avoidSlotUntil = Time.time + 1.5f; }
                assaultWorld.Record(HexAssaultTraceKind.NoProgressEscalated, this, currentTarget.InstanceId, activePlan.SlotLease, reason);
            }
            RecoveryRequested = true; ExecutionState = HexAssaultExecutionState.Recovering;
            strategicDecisionRequested = true;
            // 연속 실패도 재시도 대기 시간을 지킨다.
            animationDriver?.PlayIdle();
        }

        public bool ShouldAvoidSlot(HexAttackSlotKey key) => RecoveryRequested && hasAvoidSlot && Time.time < avoidSlotUntil && key == avoidSlot;
        public void NotifyWorldChange(HexCoordinates changed, bool topologyChanged, bool ownTarget)
        {
            var affectsPath = false;
            if (topologyChanged)
                for (var i = pathIndex; i < movementPath.Count; i++)
                    if (movementPath[i].DistanceTo(changed) <= 1) { affectsPath = true; break; }
            if (!ownTarget && !affectsPath && CurrentCoordinates.DistanceTo(changed) > 6)
            {
                return;
            }

            RouteCostReviewRequested = true;
            strategicDecisionRequested = true;
            retryNotBefore = 0f;
            nextAwarenessAt = 0f;
            if (IsInSafeMotion && !currentTarget.IsValid)
                assaultWorld.Record(HexAssaultTraceKind.MotionSegmentPreserved, this, currentTarget.InstanceId, activePlan.SlotLease, HexAssaultFailureReason.TargetInvalid);
        }

        public bool TryRetainPlan(HexCastleAssaultTarget target, int version, out HexCastleAssaultDecision plan)
        {
            plan = default;
            if (RecoveryRequested || !activePlan.SlotLease.IsValid || activePlan.PlanKind != HexAssaultPlanKind.Attack ||
                activePlan.Target.InstanceId != target.InstanceId || activePlan.TargetCell != target.Coordinates ||
                !assaultWorld.SlotAllocator.Owns(this, activePlan.SlotLease) ||
                !assaultWorld.CanAttackFromPosition(this, target, activePlan.SlotLease.Key)) return false;
            for (var i = pathIndex; i + 1 < movementPath.Count; i++)
                if (!CanDynamicStep(movementPath[i], movementPath[i + 1])) return false;
            plan = new HexCastleAssaultDecision(target, movementPath, activePlan.Approach, RouteId, RouteSector,
                version, currentIntent, currentSupportAction, activePlan.SlotLease, activePlan.FinalApproach, activePlan.PlanKind);
            return true;
        }

        public bool TryRetainHold(int version, out HexCastleAssaultDecision plan)
        {
            plan = default;
            if (!activePlan.SlotLease.IsValid || activePlan.PlanKind != HexAssaultPlanKind.Hold ||
                !assaultWorld.SlotAllocator.Owns(this, activePlan.SlotLease)) return false;
            plan = new HexCastleAssaultDecision(default, movementPath, activePlan.Approach, RouteId, RouteSector,
                version, HexCastleAssaultIntentKind.HoldPosition, slotLease: activePlan.SlotLease,
                finalApproach: activePlan.FinalApproach, planKind: HexAssaultPlanKind.Hold);
            return true;
        }
        private int DamageBand() => Mathf.Max(1, Mathf.RoundToInt(EstimatedDamagePerSecond / 10f));
        private int SpeedBand() => Mathf.Max(1, Mathf.RoundToInt(EffectiveMoveSpeed * 4f));

        private void TickLegacyRuntime(float deltaTime)
        {
            if (legacyPath == null || pathIndex >= legacyPath.Count - 1)
            {
                return;
            }

            var nextCoordinates = legacyPath[pathIndex + 1];
            if (attackActionRunning)
            {
                TickAttackAction(deltaTime);
                return;
            }

            if (!CanAssaultTraverse(nextCoordinates))
            {
                if (!HasAliveTarget(nextCoordinates))
                {
                    return;
                }

                var targetPosition = ResolvePosition(nextCoordinates);
                if (PlanarDistance(transform.position, targetPosition) > attackRange)
                {
                    MoveTowards(targetPosition, deltaTime);
                    return;
                }

                animationDriver?.PlayIdle();
                if (Time.time >= nextAttackTime)
                {
                    StartLegacyAttack(nextCoordinates, targetPosition);
                }
                return;
            }

            var destination = ResolvePosition(nextCoordinates);
            MoveTowards(destination, deltaTime);
            if (Vector3.SqrMagnitude(transform.position - destination) > 0.015f)
            {
                return;
            }

            pathIndex++;
            CurrentCoordinates = nextCoordinates;
            EnteredCell?.Invoke(this, nextCoordinates);
            if (nextCoordinates == new HexCoordinates(0, 0))
            {
                ReachedPalace = true;
                active = false;
                animationDriver?.PlayIdle(true);
            }
        }

        private void TickSupportTarget()
        {
            var ally = currentTarget.Ally;
            if (ally == null || !ally.IsAlive)
            {
                currentTarget = default;
                RequestStrategicDecision(true);
                return;
            }

            if (CurrentCoordinates.DistanceTo(ally.CurrentCoordinates) > SupportRangeCells)
            {
                RequestStrategicDecision(true);
                animationDriver?.PlayIdle();
                return;
            }

            FaceTowards(ally.transform.position, Time.deltaTime);
            if (currentSupportAction == HexCastleAssaultSupportAction.None ||
                supportCooldownRemaining > 0f || aiProfile == null)
            {
                currentTarget = default;
                currentSupportAction = HexCastleAssaultSupportAction.None;
                RequestStrategicDecision(true); // 지원할 일이 없으면 일반 진격으로 즉시 복귀한다
                animationDriver?.PlayIdle();
                return;
            }

            ally.ApplySupport(currentSupportAction, aiProfile);
            LastActualProgressTime = Time.time;
            assaultWorld?.CommitSupportDecision(
                this,
                ally,
                currentSupportAction,
                aiProfile.SupportCooldown);
            supportCooldownRemaining = aiProfile.SupportCooldown;
            currentTarget = default;
            currentSupportAction = HexCastleAssaultSupportAction.None;
            RequestStrategicDecision(true); // 쿨다운 동안 아군 옆에서 멈추지 않는다
            animationDriver?.PlayIdle(true);
        }

        private void UpdateDefenseProgress(HexCoordinates coordinates)
        {
            if (ExpectedDefenseLayer <= 0 || cellTargets == null ||
                !cellTargets.TryGetValue(coordinates, out var cell) || cell == null ||
                cell.WallRole == HexCastleWallRole.Partition ||
                cell.DefenseLayer != ExpectedDefenseLayer)
            {
                return;
            }

            ExpectedDefenseLayer = Mathf.Max(0, ExpectedDefenseLayer - 1);
            HasSelectedInitialWall = false;
            RouteCostReviewRequested = true;
            RequestStrategicDecision(true);
        }

        private bool CanAttackTarget(HexCastleAssaultTarget target)
        {
            if (!target.IsValid)
            {
                return false;
            }

            if (dynamicRuntime && activePlan.SlotLease.IsValid && target.Kind != HexCastleAssaultTargetKind.Ally)
                return assaultWorld != null && assaultWorld.CanAttackFromSlot(this, target, activePlan.SlotLease);

            if (target.Kind == HexCastleAssaultTargetKind.Palace)
            {
                return CurrentCoordinates.DistanceTo(target.Coordinates) <=
                       HexCastleFoundationGenerator.PalaceFootprintRadius + 1;
            }

            if (target.Kind == HexCastleAssaultTargetKind.Ally)
            {
                return CurrentCoordinates.DistanceTo(target.Coordinates) <= SupportRangeCells;
            }

            return CurrentCoordinates.DistanceTo(target.Coordinates) <= AttackRangeCells &&
                   (assaultWorld == null || assaultWorld.IsAttackLaneOpen(CurrentCoordinates, target));
        }

        private void MoveTowards(Vector3 destination, float deltaTime)
        {
            if (trapMovementLockRemaining > 0f)
            {
                animationDriver?.PlayIdle();
                return;
            }

            FaceTowards(destination, deltaTime);
            animationDriver?.PlayMove();
            transform.position = Vector3.MoveTowards(
                transform.position,
                destination,
                moveSpeed * CurrentMoveSpeedMultiplier * deltaTime);
        }

        private void FaceTowards(Vector3 destination, float deltaTime)
        {
            var direction = destination - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.001f)
            {
                return;
            }

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(direction.normalized, Vector3.up),
                deltaTime * 12f);
        }

        private Vector3 ResolvePosition(HexCoordinates coordinates)
        {
            return worldOrigin + coordinates.ToWorld(cellSize) + Vector3.up * groundOffset;
        }

        private Vector3 ResolveTargetPosition(HexCastleAssaultTarget target)
        {
            if (target.Defender != null)
            {
                return target.Defender.transform.position;
            }

            if (target.Ally != null)
            {
                return target.Ally.transform.position;
            }

            return target.Structure != null ? target.Structure.transform.position : transform.position;
        }

        private void StartDynamicAttack(HexCastleAssaultTarget target)
        {
            nextAttackTime = Time.time + passiveRuntime.ResolveAttackInterval(attackInterval);
            pendingTarget = target;
            pendingAttackPosition = ResolveTargetPosition(target);
            BeginAttackAction();
        }

        private void StartLegacyAttack(HexCoordinates coordinates, Vector3 hitPoint)
        {
            nextAttackTime = Time.time + passiveRuntime.ResolveAttackInterval(attackInterval);
            pendingLegacyAttackCoordinates = coordinates;
            pendingAttackPosition = hitPoint;
            pendingTarget = default;
            BeginAttackAction();
        }

        private void BeginAttackAction()
        {
            if (usesFormalVisual && animationDriver != null && animationDriver.IsReady)
            {
                attackActionRunning = true;
                var basicAttackProfile = runtimeAssetSet?.CombatProfile?.Action?.BasicAttackProfile;
                var breathDuration = basicAttackProfile != null && basicAttackProfile.UsesBreathDurationContract
                    ? basicAttackProfile.BreathDuration
                    : 0f;
                if (animationDriver.TryBeginAttack(
                        passiveRuntime.ResolveAttackInterval(attackInterval),
                        ++nextActionSequenceId,
                        HandleAttackMarker,
                        breathDuration))
                {
                    return;
                }

                attackActionRunning = false;
            }

            ApplyPendingAttack(1f);
        }

        private void TickAttackAction(float deltaTime)
        {
            FaceTowards(
                pendingTarget.Kind == HexCastleAssaultTargetKind.None
                    ? ResolvePosition(pendingLegacyAttackCoordinates)
                    : ResolveTargetPosition(pendingTarget),
                deltaTime);
            if (animationDriver == null || animationDriver.TickAttack(deltaTime, HandleAttackMarker))
            {
                attackActionRunning = false;
                animationDriver?.PlayIdle(true);
            }
        }

        private void HandleAttackMarker(int markerIndex, MonsterAttackMarker marker)
        {
            if (attackActionRunning)
            {
                ApplyPendingAttack(marker == null ? 1f : Mathf.Max(0f, marker.PowerRatio));
            }
        }

        private ProjectMT.Shared.Audio.SfxPool attackAudioPool;
        private ProjectMT.Shared.Audio.SfxCue attackSfx;

        public void ConfigureAttackAudio(ProjectMT.Shared.Audio.SfxPool pool, ProjectMT.Shared.Audio.SfxCue cue)
        {
            attackAudioPool = pool;
            attackSfx = cue;
        }

        private void ApplyPendingAttack(float powerRatio)
        {
            var damage = attackDamage * attackDamageMultiplier * Mathf.Max(0f, powerRatio);
            if (pendingTarget.Kind != HexCastleAssaultTargetKind.None)
            {
                if (pendingTarget.Kind == HexCastleAssaultTargetKind.Ally)
                {
                    return;
                }

                if (!CanAttackTarget(pendingTarget))
                {
                    currentTarget = default;
                    RequestStrategicDecision(true);
                    return;
                }

                damage = passiveRuntime.ResolveOutgoingDamage(damage, pendingTarget);
                damage *= assaultWorld?.ResolvePassiveDamageMultiplier(pendingTarget) ?? 1f;
                var wasAlive = pendingTarget.IsAlive;
                var healthBefore = pendingTarget.CurrentHealth;
                if (wasAlive && damage > 0f) attackAudioPool?.Play(attackSfx, transform.position); // 실제 공격 Marker에서만 재생
                if (pendingTarget.Structure != null)
                {
                    pendingTarget.Structure.ApplyDamage(damage, pendingAttackPosition);
                }
                else
                {
                    pendingTarget.Defender?.ApplyDamage(damage, pendingAttackPosition);
                }

                if (pendingTarget.CurrentHealth < healthBefore)
                {
                    LastActualProgressTime = Time.time;
                    assaultWorld?.Record(HexAssaultTraceKind.AttackMarkerApplied, this, pendingTarget.InstanceId, activePlan.SlotLease);
                }
                var destroyed = wasAlive && !pendingTarget.IsAlive;
                passiveRuntime.NotifyBasicAttackHit(pendingTarget, destroyed);
                if (destroyed)
                {
                    DestroyedTargets++;
                    if (committedTarget.InstanceId == pendingTarget.InstanceId)
                    {
                        committedTarget = default;
                        committedIntent = HexCastleAssaultIntentKind.None;
                    }
                    currentTarget = default;
                    RequestStrategicDecision(true);
                }
                return;
            }

            var legacyWasAlive = HasAliveTarget(pendingLegacyAttackCoordinates);
            if (legacyWasAlive && damage > 0f) attackAudioPool?.Play(attackSfx, transform.position);
            ApplyLegacyTargetDamage(pendingLegacyAttackCoordinates, pendingAttackPosition, damage);
            if (legacyWasAlive && !HasAliveTarget(pendingLegacyAttackCoordinates))
            {
                DestroyedTargets++;
            }
        }

        private bool HasAliveTarget(HexCoordinates coordinates)
        {
            return cellTargets != null &&
                   cellTargets.TryGetValue(coordinates, out var cellTarget) &&
                   cellTarget != null && cellTarget.IsAlive;
        }

        private bool CanAssaultTraverse(HexCoordinates coordinates)
        {
            if (dynamicRuntime) return CanDynamicStep(CurrentCoordinates, coordinates);
            return cellTargets == null ||
                   !cellTargets.TryGetValue(coordinates, out var cellTarget) ||
                   cellTarget == null || cellTarget.CanTraverse(HexCastleTraversalFaction.Assault);
        }

        private void ApplyLegacyTargetDamage(
            HexCoordinates coordinates,
            Vector3 hitPoint,
            float damage)
        {
            if (cellTargets != null &&
                cellTargets.TryGetValue(coordinates, out var cellTarget) &&
                cellTarget != null)
            {
                cellTarget.ApplyDamage(damage, hitPoint);
            }
        }

        private void TickRuntimeEffects(float deltaTime)
        {
            recentDamagePerSecond *= Mathf.Exp(-deltaTime / 2.5f);
            recentThreatRemaining = Mathf.Max(0f, recentThreatRemaining - deltaTime);
            supportCooldownRemaining = Mathf.Max(0f, supportCooldownRemaining - deltaTime);
            trapMovementLockRemaining = Mathf.Max(0f, trapMovementLockRemaining - deltaTime);
            trapSlowRemaining = Mathf.Max(0f, trapSlowRemaining - deltaTime);
            if (trapSlowRemaining <= 0f)
            {
                trapMoveSpeedMultiplier = 1f;
            }
            if (recentThreatRemaining <= 0f || !recentThreat.IsValid)
            {
                recentThreat = default;
            }

            if (attackBuffRemaining > 0f)
            {
                attackBuffRemaining = Mathf.Max(0f, attackBuffRemaining - deltaTime);
                if (attackBuffRemaining <= 0f)
                {
                    attackDamageMultiplier = 1f;
                }
            }

            if (defenseBuffRemaining > 0f)
            {
                defenseBuffRemaining = Mathf.Max(0f, defenseBuffRemaining - deltaTime);
                if (defenseBuffRemaining <= 0f)
                {
                    incomingDamageMultiplier = 1f;
                }
            }
        }

        private float ResolveDecisionSpread()
        {
            return assaultWorld != null ? assaultWorld.ResolveDecisionSpread(this) : 0f;
        }

        private static bool IsCommitmentIntent(HexCastleAssaultIntentKind intent)
        {
            return intent == HexCastleAssaultIntentKind.InitialBreach ||
                   intent == HexCastleAssaultIntentKind.Progress ||
                   intent == HexCastleAssaultIntentKind.Opportunity ||
                   intent == HexCastleAssaultIntentKind.Specialist ||
                   intent == HexCastleAssaultIntentKind.Palace;
        }

        private static float PlanarDistance(Vector3 left, Vector3 right)
        {
            left.y = 0f;
            right.y = 0f;
            return Vector3.Distance(left, right);
        }
    }
}
