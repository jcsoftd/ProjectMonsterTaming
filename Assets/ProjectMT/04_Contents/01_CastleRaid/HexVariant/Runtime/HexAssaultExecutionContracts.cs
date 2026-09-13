using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    public enum HexAssaultExecutionState { Inactive, AwaitInitialPlan, Traversing, Aligning, Attacking, Holding, Recovering }
    public enum HexAssaultPlanKind { Attack, Follow, Move, Hold }
    public enum HexAssaultDecisionStatus { Accepted, Deferred, NoFeasibleAction, InvalidInput }
    public enum HexAssaultFailureReason
    {
        None, InvalidInput, NoStrategicRoute, NoReachableApproach, SlotFull,
        SlotApproachBlocked, AttackLaneBlocked, BreachBudgetFull, TargetInvalid,
        PlanVersionChanged, BodyTooLarge, WaitingForOpening, NoProgress, LocalFallback, PursuitBudgetExceeded
    }
    public enum HexSlotLeaseState { Reserved, Occupied, Departing }
    public enum HexAssaultTraceKind
    {
        CandidateRejected, SlotReserved, SlotArrived, SlotDeparted, PlanCommitted,
        MotionSegmentPreserved, DecisionDeferred, NoProgressEscalated, AttackMarkerApplied,
        FieldBuilt, FieldReused, TopologyChanged, CostChanged, CohortReassigned
    }

    public readonly struct HexAttackSlotKey : IEquatable<HexAttackSlotKey>
    {
        // Index 3 is a cell-exclusive centre. Normal slots are 0..2.
        public HexAttackSlotKey(HexCoordinates cell, int index) { Cell = cell; Index = index; }
        public HexCoordinates Cell { get; }
        public int Index { get; }
        public bool Exclusive => Index == 3;
        public bool Equals(HexAttackSlotKey other) => Cell == other.Cell && Index == other.Index;
        public override bool Equals(object obj) => obj is HexAttackSlotKey other && Equals(other);
        public override int GetHashCode() => unchecked(Cell.GetHashCode() * 397 ^ Index);
        public static bool operator ==(HexAttackSlotKey a, HexAttackSlotKey b) => a.Equals(b);
        public static bool operator !=(HexAttackSlotKey a, HexAttackSlotKey b) => !a.Equals(b);
        public override string ToString() => Cell + ":" + Index;
    }

    public readonly struct HexSlotLease : IEquatable<HexSlotLease>
    {
        internal HexSlotLease(long epoch, long id, int generation, HexAttackSlotKey key)
        { WorldEpoch = epoch; Id = id; Generation = generation; Key = key; }
        public long WorldEpoch { get; }
        public long Id { get; }
        public int Generation { get; }
        public HexAttackSlotKey Key { get; }
        public bool IsValid => Id > 0;
        public bool Equals(HexSlotLease other) => WorldEpoch == other.WorldEpoch && Id == other.Id && Generation == other.Generation && Key == other.Key;
        public override bool Equals(object obj) => obj is HexSlotLease other && Equals(other);
        public override int GetHashCode() => unchecked(WorldEpoch.GetHashCode() * 397 ^ Id.GetHashCode() ^ Generation);
        public static bool operator ==(HexSlotLease a, HexSlotLease b) => a.Equals(b);
        public static bool operator !=(HexSlotLease a, HexSlotLease b) => !a.Equals(b);
    }

    public readonly struct HexAssaultDecisionResult
    {
        public HexAssaultDecisionResult(HexAssaultDecisionStatus status, HexAssaultFailureReason reason,
            HexCastleAssaultDecision plan, float retryAt)
        { Status = status; Reason = reason; Plan = plan; RetryAt = retryAt; }
        public HexAssaultDecisionStatus Status { get; }
        public HexAssaultFailureReason Reason { get; }
        public HexCastleAssaultDecision Plan { get; }
        public float RetryAt { get; }
    }

    public readonly struct HexAssaultTraceEvent
    {
        internal HexAssaultTraceEvent(long sequence, long epoch, HexAssaultTraceKind kind,
            int unitId, int targetId, int planGeneration, HexSlotLease lease,
            HexAssaultFailureReason reason, int topology, int cost)
        { Sequence = sequence; WorldEpoch = epoch; Kind = kind; UnitId = unitId; TargetId = targetId;
          PlanGeneration = planGeneration; Lease = lease; Reason = reason; TopologyRevision = topology;
          CostRevision = cost; Time = UnityEngine.Time.time; Frame = UnityEngine.Time.frameCount; }
        public long Sequence { get; }
        public long WorldEpoch { get; }
        public int Frame { get; }
        public float Time { get; }
        public HexAssaultTraceKind Kind { get; }
        public int UnitId { get; }
        public int TargetId { get; }
        public int PlanGeneration { get; }
        public HexSlotLease Lease { get; }
        public HexAssaultFailureReason Reason { get; }
        public int TopologyRevision { get; }
        public int CostRevision { get; }
    }

    // Fixed-size observation buffer: no callback/re-entry in a reservation commit.
    public sealed class HexAssaultTraceBuffer
    {
        private readonly HexAssaultTraceEvent[] events;
        private long sequence;
        private int count;
        public HexAssaultTraceBuffer(int capacity = 512) { events = new HexAssaultTraceEvent[Math.Max(16, capacity)]; }
        public long LastSequence => sequence;
        public int Count => count;
        public void Add(long epoch, HexAssaultTraceKind kind, int unit, int target, int generation,
            HexSlotLease lease, HexAssaultFailureReason reason, int topology, int cost)
        {
            var seq = ++sequence;
            events[(int)((seq - 1) % events.Length)] = new HexAssaultTraceEvent(seq, epoch, kind, unit, target, generation, lease, reason, topology, cost);
            count = Math.Min(count + 1, events.Length);
        }
        public void CopyAfter(long afterSequence, List<HexAssaultTraceEvent> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            for (var seq = Math.Max(afterSequence + 1, sequence - count + 1); seq <= sequence; seq++)
                destination.Add(events[(int)((seq - 1) % events.Length)]);
        }
        public void Clear() { Array.Clear(events, 0, events.Length); count = 0; }
    }
}
