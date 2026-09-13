using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    /// <summary>World-space ownership. Different targets cannot reserve the same physical slot.</summary>
    public sealed class HexAttackSlotAllocator
    {
        private sealed class Entry
        {
            public HexSlotLease Lease;
            public HexCastleAssaultUnit Owner;
            public int OwnerGeneration;
            public HexCastleAssaultTarget Target;
            public Vector3 Position;
            public float Radius, ExpiresAt, LastProgress;
            public HexSlotLeaseState State;
            public bool Holding;
        }
        private static long epochCounter;
        private readonly Dictionary<HexAttackSlotKey, Entry> entries = new Dictionary<HexAttackSlotKey, Entry>();
        private readonly Dictionary<int, HexSlotLease> destinations = new Dictionary<int, HexSlotLease>();
        private readonly List<HexAttackSlotKey> scratch = new List<HexAttackSlotKey>();
        private readonly float size, inRadius;
        private readonly Vector3 origin;
        private long nextId;
        private int generation;
        public HexAttackSlotAllocator(float cellSize, Vector3 worldOrigin, int capacity = 3)
        {
            if (!Finite(cellSize) || cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (capacity != 1 && capacity != 3) throw new ArgumentOutOfRangeException(nameof(capacity));
            size = cellSize; inRadius = cellSize * 0.8660254038f; origin = worldOrigin;
            Capacity = capacity; WorldEpoch = Interlocked.Increment(ref epochCounter);
        }
        public long WorldEpoch { get; }
        public int Capacity { get; }
        public int Revision { get; private set; }
        public int AvailabilityRevision { get; private set; }
        public int Count => entries.Count;
        public float Clearance => inRadius * 0.10f;
        public float InRadius => inRadius;
        public Vector3 Centre(HexCoordinates cell) => origin + cell.ToWorld(size);
        public Vector3 Position(HexAttackSlotKey key)
        {
            var centre = Centre(key.Cell);
            if (key.Exclusive || Capacity == 1) return centre;
            var rho = inRadius * 0.5f;
            switch (key.Index)
            {
                case 0: return centre + new Vector3(0f, 0f, rho);
                case 1: return centre + new Vector3(-0.8660254038f * rho, 0f, -rho * 0.5f);
                case 2: return centre + new Vector3(0.8660254038f * rho, 0f, -rho * 0.5f);
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
        }
        public bool Fits(HexAttackSlotKey key, float radius)
        {
            if (!Finite(radius) || radius <= 0f || key.Index < 0 || key.Index > 3) return false;
            if (Capacity == 1 && key.Index != 3) return false;
            var p = Position(key) - Centre(key.Cell);
            for (var d = 0; d < 6; d++)
            {
                var n = HexCoordinates.Directions[d].ToWorld(1f).normalized;
                if (Vector3.Dot(p, n) + radius + Clearance > inRadius + 0.0001f) return false;
            }
            return true;
        }
        public bool NeedsExclusive(float radius)
        {
            var rho = inRadius * 0.5f;
            return Capacity == 1 || radius * 2f + Clearance > 1.7320508076f * rho ||
                   !Fits(new HexAttackSlotKey(default, 0), radius);
        }
        public int CandidateCount(float radius) => NeedsExclusive(radius) ? 1 : 3;
        public HexAttackSlotKey Candidate(HexCoordinates cell, float radius, int index) =>
            new HexAttackSlotKey(cell, NeedsExclusive(radius) ? 3 : index);

        public bool CanReserve(HexCastleAssaultUnit owner, HexAttackSlotKey key, float radius, out HexAssaultFailureReason reason)
        {
            reason = HexAssaultFailureReason.None;
            if (owner == null || !owner.IsAlive) { reason = HexAssaultFailureReason.InvalidInput; return false; }
            if (!Fits(key, radius)) { reason = HexAssaultFailureReason.BodyTooLarge; return false; }
            var p = Position(key);
            foreach (var pair in entries)
            {
                var e = pair.Value;
                if (!Alive(e) || e.Owner == owner) continue;
                if (e.State != HexSlotLeaseState.Departing && pair.Key.Cell == key.Cell &&
                    (key.Exclusive || pair.Key.Exclusive || pair.Key == key))
                { reason = HexAssaultFailureReason.SlotFull; return false; }
                var min = radius + e.Radius + Clearance;
                var occupiedPosition = e.State == HexSlotLeaseState.Departing ? e.Owner.transform.position : e.Position;
                if (SqrPlanar(p - occupiedPosition) < min * min - 0.0001f)
                { reason = HexAssaultFailureReason.SlotFull; return false; }
            }
            return true;
        }
        public bool TryGet(HexCastleAssaultUnit owner, out HexSlotLease lease)
        {
            lease = default;
            return owner != null && destinations.TryGetValue(owner.GetInstanceID(), out lease) && Owns(owner, lease);
        }
        public bool Owns(HexCastleAssaultUnit owner, HexSlotLease lease) =>
            lease.IsValid && lease.WorldEpoch == WorldEpoch && entries.TryGetValue(lease.Key, out var e) &&
            e.Lease == lease && e.Owner == owner && Alive(e) && e.State != HexSlotLeaseState.Departing;
        public bool IsOccupied(HexCastleAssaultUnit owner, HexSlotLease lease) =>
            Owns(owner, lease) && entries[lease.Key].State == HexSlotLeaseState.Occupied;
        public HexSlotLeaseState State(HexSlotLease lease) =>
            entries.TryGetValue(lease.Key, out var e) && e.Lease == lease ? e.State : HexSlotLeaseState.Departing;
        public bool IsHolding(HexSlotLease lease) => entries.TryGetValue(lease.Key, out var e) && e.Lease == lease && e.Holding;

        // Validate before removing the old destination. No user event is invoked inside this method.
        public bool TryCommit(HexCastleAssaultUnit owner, HexCastleAssaultTarget target, HexAttackSlotKey key,
            float radius, float estimatedSeconds, int expectedRevision, bool holding,
            out HexSlotLease lease, out HexAssaultFailureReason reason)
        {
            lease = default;
            if (expectedRevision != Revision) { reason = HexAssaultFailureReason.PlanVersionChanged; return false; }
            if (!holding && !target.IsValid) { reason = HexAssaultFailureReason.TargetInvalid; return false; }
            if (!CanReserve(owner, key, radius, out reason)) return false;
            if (TryGet(owner, out var existing) && existing.Key == key)
            {
                var e = entries[key];
                if (e.Target.InstanceId == target.InstanceId && e.Holding == holding)
                { lease = existing; return true; }
            }
            var state = HexSlotLeaseState.Reserved;
            if (entries.TryGetValue(key, out var same) && same.Owner == owner)
                state = same.State == HexSlotLeaseState.Occupied ? HexSlotLeaseState.Occupied : HexSlotLeaseState.Reserved;
            var next = new Entry { Lease = new HexSlotLease(WorldEpoch, ++nextId, ++generation, key), Owner = owner,
                OwnerGeneration = owner.RuntimeGeneration, Target = target, Radius = radius, Position = Position(key),
                State = state, Holding = holding, ExpiresAt = Time.time + Mathf.Max(2f, estimatedSeconds * 1.5f + 1f),
                LastProgress = owner.LastActualProgressTime };
            scratch.Clear();
            foreach (var p in entries) if (p.Value.Owner == owner && p.Key != key) scratch.Add(p.Key);
            for (var i = 0; i < scratch.Count; i++) CancelEntry(scratch[i], false);
            entries[key] = next;
            destinations[owner.GetInstanceID()] = next.Lease;
            Revision++;
            lease = next.Lease;
            reason = HexAssaultFailureReason.None;
            return true;
        }
        public bool Arrive(HexCastleAssaultUnit owner, HexSlotLease lease)
        {
            if (!Owns(owner, lease)) return false;
            var e = entries[lease.Key];
            if (SqrPlanar(owner.transform.position - e.Position) > 0.0036f) return false;
            if (!CanReserve(owner, lease.Key, e.Radius, out _)) return false;
            if (e.State != HexSlotLeaseState.Occupied) { e.State = HexSlotLeaseState.Occupied; Revision++; }
            return true;
        }
        public void Cancel(HexCastleAssaultUnit owner, HexSlotLease lease, bool removed = false)
        {
            if (!lease.IsValid || lease.WorldEpoch != WorldEpoch || !entries.TryGetValue(lease.Key, out var e) ||
                e.Lease != lease || e.Owner != owner) return;
            CancelEntry(lease.Key, removed); Revision++;
        }
        public void ReleaseOwner(HexCastleAssaultUnit owner, bool removed)
        {
            if (owner == null) return;
            scratch.Clear();
            foreach (var p in entries) if (p.Value.Owner == owner) scratch.Add(p.Key);
            for (var i = 0; i < scratch.Count; i++) CancelEntry(scratch[i], removed);
            destinations.Remove(owner.GetInstanceID());
            if (scratch.Count > 0) Revision++;
        }
        public void ReleaseTarget(int targetId)
        {
            scratch.Clear();
            foreach (var p in entries) if (!p.Value.Holding && p.Value.Target.InstanceId == targetId) scratch.Add(p.Key);
            for (var i = 0; i < scratch.Count; i++) CancelEntry(scratch[i], false);
            if (scratch.Count > 0) Revision++;
        }
        private void CancelEntry(HexAttackSlotKey key, bool removed)
        {
            if (!entries.TryGetValue(key, out var e)) return;
            if (e.Owner != null && destinations.TryGetValue(e.Owner.GetInstanceID(), out var current) && current == e.Lease)
                destinations.Remove(e.Owner.GetInstanceID());
            // Death removes the logical body. Target death does not.
            if (!removed && Alive(e) && (e.State == HexSlotLeaseState.Occupied ||
                SqrPlanar(e.Owner.transform.position - e.Position) < (e.Radius * 2f + Clearance) * (e.Radius * 2f + Clearance)))
                e.State = HexSlotLeaseState.Departing;
            else
            {
                entries.Remove(key);
                AvailabilityRevision++;
            }
        }
        public void Tick(float now)
        {
            scratch.Clear();
            foreach (var pair in entries)
            {
                var e = pair.Value;
                if (!Alive(e)) { scratch.Add(pair.Key); continue; }
                if (e.State == HexSlotLeaseState.Departing)
                {
                    var d = e.Radius * 2f + Clearance;
                    if (SqrPlanar(e.Owner.transform.position - e.Position) > d * d) scratch.Add(pair.Key);
                }
                else if (e.State == HexSlotLeaseState.Reserved)
                {
                    if (e.Owner.LastActualProgressTime > e.LastProgress)
                    { e.LastProgress = e.Owner.LastActualProgressTime; e.ExpiresAt = Mathf.Max(e.ExpiresAt, now + 2f); }
                    if (e.Owner.TrapMovementLockRemaining > 0f)
                        e.ExpiresAt = Mathf.Max(e.ExpiresAt, now + e.Owner.TrapMovementLockRemaining + 1f);
                    if (now > e.ExpiresAt && !e.Owner.IsInSafeMotion) scratch.Add(pair.Key);
                }
            }
            for (var i = 0; i < scratch.Count; i++)
            {
                var key = scratch[i]; var e = entries[key];
                if (e.Owner != null && destinations.TryGetValue(e.Owner.GetInstanceID(), out var lease) && lease == e.Lease)
                    destinations.Remove(e.Owner.GetInstanceID());
                entries.Remove(key); Revision++; AvailabilityRevision++;
            }
        }
        public int CountInCell(HexCoordinates cell)
        {
            var count = 0;
            foreach (var p in entries) if (p.Key.Cell == cell && Alive(p.Value)) count += p.Key.Exclusive ? Capacity : 1;
            return count;
        }
        public bool BodySegmentClear(HexCastleAssaultUnit owner, Vector3 a, Vector3 b, float radius)
        {
            foreach (var e in entries.Values)
            {
                if (e.Owner == owner || !Alive(e) || e.State == HexSlotLeaseState.Reserved) continue;
                var p = e.State == HexSlotLeaseState.Departing ? e.Owner.transform.position : e.Position;
                var sum = radius + e.Radius + Clearance;
                var from = a - p; from.y = 0f;
                var direction = b - a; direction.y = 0f;
                if (from.sqrMagnitude < sum * sum - 0.0001f &&
                    Vector3.Dot(from, direction) >= -0.0001f && SqrPlanar(b - p) > from.sqrMagnitude + 0.0001f)
                    continue; // 이미 겹친 몸체는 더 멀어지는 탈출 구간만 허용한다.
                if (PointSegmentSquared(p, a, b) < sum * sum - 0.0001f) return false;
            }
            return true;
        }
        public void Clear()
        {
            var released = entries.Count > 0;
            entries.Clear(); destinations.Clear(); Revision++;
            if (released) AvailabilityRevision++;
        }
        private static bool Alive(Entry e) => e.Owner != null && e.Owner.IsAlive && e.Owner.RuntimeGeneration == e.OwnerGeneration;
        internal static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        internal static float SqrPlanar(Vector3 v) => v.x * v.x + v.z * v.z;
        internal static float PointSegmentSquared(Vector3 p, Vector3 a, Vector3 b)
        {
            p.y = a.y = b.y = 0f;
            var d = b - a;
            var t = d.sqrMagnitude <= 0.000001f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, d) / d.sqrMagnitude);
            return (p - (a + t * d)).sqrMagnitude;
        }
    }
}
