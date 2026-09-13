using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    public sealed class HexAssaultApproachPlan
    {
        internal HexAssaultApproachPlan(HexAttackSlotKey slot, HexCoordinates[] cells, Vector3[] finalPath,
            float cost, int revision, int visited)
        { Slot = slot; Cells = cells; FinalPath = finalPath; Cost = cost; OccupancyRevision = revision; VisitedCells = visited; }
        public HexAttackSlotKey Slot { get; }
        public IReadOnlyList<HexCoordinates> Cells { get; }
        public IReadOnlyList<Vector3> FinalPath { get; }
        public Vector3 Position => FinalPath[FinalPath.Count - 1];
        public float Cost { get; }
        public int OccupancyRevision { get; }
        public int VisitedCells { get; }
    }

    /// <summary>부대의 팀장 탐색을 재사용하고 개인은 짧은 합류와 마지막 자리를 계산한다.</summary>
    public sealed class HexAssaultApproachPlanner
    {
        private readonly IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cells;
        private readonly HexAttackSlotAllocator slots;
        private readonly float size, pitch;
        private readonly Vector3 origin;
        private readonly Dictionary<HexCoordinates, int> distance = new Dictionary<HexCoordinates, int>();
        private readonly Dictionary<HexCoordinates, HexCoordinates> previous = new Dictionary<HexCoordinates, HexCoordinates>();
        private readonly Queue<HexCoordinates> queue = new Queue<HexCoordinates>();
        private readonly List<HexCoordinates> order = new List<HexCoordinates>();
        private readonly Dictionary<HexCoordinates, HexCoordinates[]> paths = new Dictionary<HexCoordinates, HexCoordinates[]>();
        private static readonly Vector3[] ClipNormals = BuildClipNormals();
        private static Vector3[] BuildClipNormals()
        {
            var normals = new Vector3[6];
            for (var i = 0; i < normals.Length; i++) normals[i] = HexCoordinates.Directions[i].ToWorld(1f).normalized;
            return normals;
        }
        private HexCastleAssaultUnit actor;
        private sealed class SharedSearch
        {
            public HexCoordinates Root;
            public HexCoordinates[] Order;
            public Dictionary<HexCoordinates, HexCoordinates> Previous;
            public readonly Dictionary<HexCoordinates, HexCoordinates[]> Paths = new Dictionary<HexCoordinates, HexCoordinates[]>();
            public readonly HashSet<HexCoordinates> Dependencies = new HashSet<HexCoordinates>();
        }
        private readonly Dictionary<int, SharedSearch> sharedSearches = new Dictionary<int, SharedSearch>();
        private SharedSearch sharedSearch;
        private int activeCohort;
        private HexCastleAssaultUnit activeLeader;
        private bool retriedSharedSearch;
        public int FullSearchCount { get; private set; }
        public int SharedSearchBuildCount { get; private set; }
        public int SharedPathUseCount { get; private set; }
        public int LocalConnectionSearchCount { get; private set; }
        public int FullVisitedCellCount { get; private set; }
        public int LocalVisitedCellCount { get; private set; }
        public HexAssaultApproachPlanner(IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            HexAttackSlotAllocator allocator, float cellSize, Vector3 worldOrigin)
        { cells = runtimeCells ?? throw new ArgumentNullException(nameof(runtimeCells)); slots = allocator ?? throw new ArgumentNullException(nameof(allocator));
          size = cellSize; pitch = cellSize * 1.7320508076f; origin = worldOrigin; }
        public int LastVisitedCells => order.Count;
        public void BeginDecision(HexCastleAssaultUnit unit)
        {
            sharedSearch = null;
            actor = unit; distance.Clear(); previous.Clear(); queue.Clear(); order.Clear(); paths.Clear();
            if (unit == null || !Open(unit.CurrentCoordinates)) return;
            FullSearchCount++;
            SearchOpenCells(unit.CurrentCoordinates, int.MaxValue);
            FullVisitedCellCount += order.Count;
        }

        public void BeginDecision(HexCastleAssaultUnit unit, int cohortId, HexCastleAssaultUnit leader)
        {
            if (cohortId <= 0 || leader == null || !leader.IsAlive) { BeginDecision(unit); return; }
            if (!sharedSearches.TryGetValue(cohortId, out var search))
            {
                BeginDecision(leader);
                search = new SharedSearch { Root = leader.CurrentCoordinates, Order = order.ToArray(),
                    Previous = new Dictionary<HexCoordinates, HexCoordinates>(previous) };
                search.Dependencies.Add(search.Root);
                sharedSearches.Add(cohortId, search); SharedSearchBuildCount++;
            }
            actor = unit; activeLeader = leader; activeCohort = cohortId; sharedSearch = search; retriedSharedSearch = false;
            distance.Clear(); previous.Clear(); queue.Clear(); order.Clear(); paths.Clear();
            if (unit == null || !Open(unit.CurrentCoordinates)) return;
            LocalConnectionSearchCount++;
            SearchOpenCells(unit.CurrentCoordinates, 3);
            LocalVisitedCellCount += order.Count;
            foreach (var c in search.Order) if (!distance.ContainsKey(c)) order.Add(c);
        }

        public void ForgetCohort(int cohortId) => sharedSearches.Remove(cohortId);

        public void InvalidateSharedSearchesAt(HexCoordinates changed)
        {
            var affected = new List<int>();
            foreach (var pair in sharedSearches)
            {
                var touches = pair.Value.Dependencies.Contains(changed);
                for (var d = 0; d < 6 && !touches; d++) touches = pair.Value.Dependencies.Contains(changed.Neighbor(d));
                if (touches) affected.Add(pair.Key);
            }
            foreach (var id in affected) sharedSearches.Remove(id);
        }

        private void SearchOpenCells(HexCoordinates start, int maximumSteps)
        {
            distance.Add(start, 0); queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue(); order.Add(c);
                if (distance[c] >= maximumSteps) continue;
                for (var d = 0; d < 6; d++)
                {
                    var next = c.Neighbor(d);
                    if (distance.ContainsKey(next) || !Open(next) ||
                        !HexRoutePlanner.CanTraverseStep(cells[c], cells[next], d, HexCastleTraversalFaction.Assault)) continue;
                    distance.Add(next, distance[c] + 1); previous.Add(next, c); queue.Enqueue(next);
                }
            }
        }
        public bool TryPlan(HexCastleAssaultUnit unit, HexCastleAssaultTarget target, int range,
            Func<HexAttackSlotKey, bool> attackPredicate, bool holding,
            out HexAssaultApproachPlan plan, out HexAssaultFailureReason failure,
            Action<HexAttackSlotKey, HexAssaultFailureReason> rejected = null)
        {
            plan = null; failure = HexAssaultFailureReason.NoReachableApproach;
            if (actor != unit) BeginDecision(unit);
            if (unit == null || (!holding && !target.IsValid)) { failure = HexAssaultFailureReason.TargetInvalid; return false; }
            var best = float.PositiveInfinity;
            var radius = unit.SlotBodyRadius;
            var revision = slots.Revision;
            var hasReachablePath = false;
            for (var oi = 0; oi < order.Count; oi++)
            {
                var c = order[oi];
                if (holding)
                {
                    if (!distance.TryGetValue(c, out var holdingDistance) || holdingDistance > 3 || !HoldingCell(c)) continue;
                }
                else if (c.DistanceTo(target.Coordinates) > Math.Max(1, range)) continue;
                var path = Reconstruct(c);
                if (path.Length == 0) continue;
                if (!holding && !StaysOnAdvanceFront(unit, target, range, path))
                { failure = HexAssaultFailureReason.SlotApproachBlocked; continue; }
                hasReachablePath = true;
                var lowerBound = Math.Max(0, path.Length - 3) * pitch / unit.EffectiveMoveSpeed;
                if (lowerBound > best + 0.0001f) continue;
                var entry = path.Length <= 2 ? unit.transform.position : Centre(path[path.Length - 2]);
                var prefixLength = 0f; var prefixPosition = unit.transform.position;
                for (var pi = 1; pi < path.Length - 1; pi++)
                {
                    var next = Centre(path[pi]); next.y = prefixPosition.y;
                    prefixLength += Vector3.Distance(prefixPosition, next); prefixPosition = next;
                }
                for (var si = 0; si < slots.CandidateCount(radius); si++)
                {
                    var key = slots.Candidate(c, radius, si);
                    if (!slots.CanReserve(unit, key, radius, out var reason))
                    { failure = reason; rejected?.Invoke(key, reason); continue; }
                    if (attackPredicate != null && !attackPredicate(key))
                    { failure = HexAssaultFailureReason.AttackLaneBlocked; rejected?.Invoke(key, failure); continue; }
                    var destination = slots.Position(key); destination.y = unit.GroundWorldY;
                    var flatEntry = entry; flatEntry.y = destination.y;
                    // A local detour cannot be shorter than this straight segment. Keep ties
                    // in the existing candidate order; never prune by an estimated detour.
                    if ((prefixLength + Vector3.Distance(flatEntry, destination)) / unit.EffectiveMoveSpeed >= best - 0.0001f) continue;
                    if (!TryFinalPath(unit, entry, destination, c, out var local, out var localLength))
                    { failure = HexAssaultFailureReason.SlotApproachBlocked; rejected?.Invoke(key, failure); continue; }
                    var cost = (prefixLength + localLength) / unit.EffectiveMoveSpeed;
                    if (cost >= best - 0.0001f) continue;
                    best = cost;
                    plan = new HexAssaultApproachPlan(key, path, local, cost, revision, order.Count);
                }
            }
            if (plan == null)
            {
                if (!hasReachablePath && sharedSearch != null && !retriedSharedSearch && activeLeader != null &&
                    sharedSearch.Root != activeLeader.CurrentCoordinates)
                {
                    ForgetCohort(activeCohort);
                    BeginDecision(unit, activeCohort, activeLeader); retriedSharedSearch = true;
                    return TryPlan(unit, target, range, attackPredicate, holding, out plan, out failure, rejected);
                }
                return false;
            }
            failure = HexAssaultFailureReason.None;
            return true;
        }
        private static bool StaysOnAdvanceFront(HexCastleAssaultUnit unit, HexCastleAssaultTarget target,
            int range, HexCoordinates[] path)
        {
            if (target.Structure == null) return true; // 위협 대응과 아군 지원의 이동은 제한하지 않는다.
            var outerRing = Math.Max(unit.CurrentCoordinates.DistanceFromOrigin,
                target.Coordinates.DistanceFromOrigin + Math.Max(1, range));
            foreach (var cell in path)
                if (cell.DistanceFromOrigin > outerRing) return false; // 반대편 빈자리를 위해 공략 중인 전면 밖으로 후퇴하지 않는다.
            return true;
        }
        private bool HoldingCell(HexCoordinates c)
        {
            if (!Open(c)) return false; // 파괴되어 열린 구조물 자리도 전진 후보에 포함한다.
            var open = 0;
            for (var d = 0; d < 6; d++) if (Open(c.Neighbor(d))) open++;
            return open >= 4; // Do not deliberately hold in a one-cell corridor.
        }
        private HexCoordinates[] Reconstruct(HexCoordinates goal)
        {
            if (paths.TryGetValue(goal, out var cached)) return cached;
            if (sharedSearch != null)
            {
                var best = distance.ContainsKey(goal) ? ReconstructLocal(goal) : Array.Empty<HexCoordinates>();
                if (!sharedSearch.Paths.TryGetValue(goal, out var trunk))
                {
                    var reverse = new List<HexCoordinates> { goal }; var cursor = goal;
                    while (cursor != sharedSearch.Root && sharedSearch.Previous.TryGetValue(cursor, out cursor)) reverse.Add(cursor);
                    reverse.Reverse();
                    trunk = reverse.Count > 0 && reverse[0] == sharedSearch.Root ? reverse.ToArray() : Array.Empty<HexCoordinates>();
                    sharedSearch.Paths[goal] = trunk;
                }
                var joined = false;
                for (var i = 0; i < trunk.Length; i++)
                {
                    if (!distance.TryGetValue(trunk[i], out var steps) ||
                        best.Length > 0 && steps + trunk.Length - i >= best.Length) continue;
                    var approach = ReconstructLocal(trunk[i]);
                    var combined = new HexCoordinates[approach.Length + trunk.Length - i - 1];
                    Array.Copy(approach, combined, approach.Length);
                    Array.Copy(trunk, i + 1, combined, approach.Length, trunk.Length - i - 1);
                    if (!IsOpenPath(combined)) continue;
                    best = combined; joined = i + 1 < trunk.Length;
                }
                if (joined) SharedPathUseCount++;
                foreach (var c in best) sharedSearch.Dependencies.Add(c);
                paths[goal] = best;
                return best;
            }
            var path = ReconstructLocal(goal); paths.Add(goal, path); return path;
        }

        private bool IsOpenPath(HexCoordinates[] path)
        {
            for (var i = 0; i < path.Length; i++)
            {
                if (!Open(path[i])) return false;
                if (i == 0) continue;
                var traversable = false;
                for (var d = 0; d < 6; d++)
                    if (path[i - 1].Neighbor(d) == path[i])
                    { traversable = HexRoutePlanner.CanTraverseStep(cells[path[i - 1]], cells[path[i]], d, HexCastleTraversalFaction.Assault); break; }
                if (!traversable) return false;
            }
            return true;
        }

        private HexCoordinates[] ReconstructLocal(HexCoordinates goal)
        {
            var result = new List<HexCoordinates> { goal };
            var c = goal;
            while (c != actor.CurrentCoordinates)
            {
                if (!previous.TryGetValue(c, out c)) return Array.Empty<HexCoordinates>();
                result.Add(c);
            }
            result.Reverse(); return result.ToArray();
        }
        public bool TryFinalPath(HexCastleAssaultUnit owner, Vector3 start, Vector3 end, HexCoordinates goal,
            out Vector3[] path, out float length)
        {
            path = Array.Empty<Vector3>(); length = 0f;
            start.y = end.y = owner.GroundWorldY;
            if (SegmentClear(owner, start, end)) { path = new[] { end }; length = Vector3.Distance(start, end); return true; }
            // Small deterministic local graph. More complex passages are rejected, not teleported through.
            var nodes = new List<Vector3> { start, end };
            var startCell = HexCoordinates.FromWorld(start - origin, size);
            var centres = new List<HexCoordinates> { startCell };
            if (goal != startCell) centres.Add(goal);
            for (var ci = 0; ci < centres.Count; ci++)
            {
                var centre = Centre(centres[ci]); centre.y = start.y;
                nodes.Add(centre);
                for (var ring = 0; ring < 2; ring++)
                {
                    var localRadius = slots.InRadius * (ring == 0 ? 0.58f : 1.25f);
                    for (var i = 0; i < 12; i++)
                    {
                        var angle = i * Mathf.PI / 6f;
                        nodes.Add(centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * localRadius);
                    }
                }
            }
            // Geometry is immutable during this synchronous local search. Collect blockers
            // once instead of looking up the same cells for every visibility-graph edge.
            // Node rings extend at most two cells from either anchor; four extra rings also
            // include inflated wall bodies. Exact hex clipping and live body checks remain.
            var blockers = new List<Vector3>();
            var blockerSpan = startCell.DistanceTo(goal) + 4;
            var blockerMidpoint = HexCoordinates.FromWorld((start + end) * 0.5f - origin, size);
            for (var q = -blockerSpan; q <= blockerSpan; q++)
            for (var r = Math.Max(-blockerSpan, -q - blockerSpan); r <= Math.Min(blockerSpan, -q + blockerSpan); r++)
            {
                var c = blockerMidpoint + new HexCoordinates(q, r);
                if (!Open(c)) blockers.Add(Centre(c));
            }
            var bodyRadius = owner.SlotBodyRadius;
            var inflatedRadius = slots.InRadius + bodyRadius + slots.Clearance * 0.25f;
            var costs = new float[nodes.Count]; var parents = new int[nodes.Count]; var used = new bool[nodes.Count];
            for (var i = 0; i < costs.Length; i++) { costs[i] = float.PositiveInfinity; parents[i] = -1; }
            costs[0] = 0f;
            for (var step = 0; step < nodes.Count; step++)
            {
                var at = -1;
                for (var i = 0; i < nodes.Count; i++) if (!used[i] && (at < 0 || costs[i] < costs[at])) at = i;
                if (at < 0 || float.IsPositiveInfinity(costs[at])) break;
                if (at == 1)
                {
                    var result = new List<Vector3>();
                    for (var j = 1; j > 0; j = parents[j]) result.Add(nodes[j]);
                    result.Reverse(); path = result.ToArray(); length = costs[1]; return true;
                }
                used[at] = true;
                for (var next = 1; next < nodes.Count; next++)
                {
                    if (used[next]) continue;
                    var candidate = costs[at] + Vector3.Distance(nodes[at], nodes[next]);
                    if (candidate >= costs[next] - 0.0001f ||
                        !PreparedSegmentClear(owner, nodes[at], nodes[next], blockers, inflatedRadius, bodyRadius)) continue;
                    costs[next] = candidate; parents[next] = at;
                }
            }
            return false;
        }
        private bool PreparedSegmentClear(HexCastleAssaultUnit owner, Vector3 from, Vector3 to,
            List<Vector3> blockers, float inflatedRadius, float bodyRadius)
        {
            for (var i = 0; i < blockers.Count; i++)
                if (IntersectsHex(from, to, blockers[i], inflatedRadius)) return false;
            return slots.BodySegmentClear(owner, from, to, bodyRadius);
        }
        public bool SegmentClear(HexCastleAssaultUnit owner, Vector3 from, Vector3 to)
        {
            var radius = owner.SlotBodyRadius;
            var midpoint = HexCoordinates.FromWorld((from + to) * 0.5f - origin, size);
            var span = Math.Max(2, HexCoordinates.FromWorld(from - origin, size).DistanceTo(HexCoordinates.FromWorld(to - origin, size)) + 2);
            // Same axial traversal order without allocating an iterator for every graph edge.
            for (var q = -span; q <= span; q++)
            for (var r = Math.Max(-span, -q - span); r <= Math.Min(span, -q + span); r++)
            {
                var c = midpoint + new HexCoordinates(q, r);
                if (Open(c)) continue;
                if (IntersectsHex(from, to, Centre(c), slots.InRadius + radius + slots.Clearance * 0.25f)) return false;
            }
            return slots.BodySegmentClear(owner, from, to, radius);
        }
        public bool IsAttackLineOpen(Vector3 from, HexCastleAssaultTarget target)
        {
            if (!target.IsValid) return false;
            var to = Centre(target.Coordinates);
            var sourceCell = HexCoordinates.FromWorld(from - origin, size);
            var span = sourceCell.DistanceTo(target.Coordinates) + 2;
            if (1L + 3L * span * (span + 1L) < cells.Count)
            {
                var midpoint = HexCoordinates.FromWorld((from + to) * .5f - origin, size);
                for (var q = -span; q <= span; q++)
                for (var r = Math.Max(-span, -q - span); r <= Math.Min(span, -q + span); r++)
                {
                    var key = midpoint + new HexCoordinates(q, r);
                    if (cells.TryGetValue(key, out var cell) && BlocksAttackLine(cell, key, from, to, target)) return false;
                }
                return true;
            }
            foreach (var pair in cells)
            {
                if (BlocksAttackLine(pair.Value, pair.Key, from, to, target)) return false;
            }
            return true;
        }
        private bool BlocksAttackLine(HexCastleCellRuntime cell, HexCoordinates key, Vector3 from, Vector3 to, HexCastleAssaultTarget target)
        {
            if (cell == null || !cell.IsAlive || !cell.IsBlocked || key == target.Coordinates ||
                target.Kind == HexCastleAssaultTargetKind.Palace && cell.Kind == HexCastleCellKind.Palace) return false;
            if (cell.Kind != HexCastleCellKind.Wall && cell.Kind != HexCastleCellKind.Gate && cell.Kind != HexCastleCellKind.Tower) return false;
            return IntersectsHex(from, to, Centre(key), slots.InRadius - 0.00001f);
        }
        // Cyrus-Beck clipping against the six outward normals. Also covers edge/vertex contacts.
        public static bool IntersectsHex(Vector3 a, Vector3 b, Vector3 centre, float inRadius)
        {
            // Conservative enclosing box; the exact clipping below remains authoritative.
            var extent = inRadius * 1.154701f;
            if (Mathf.Max(a.x, b.x) < centre.x - extent || Mathf.Min(a.x, b.x) > centre.x + extent ||
                Mathf.Max(a.z, b.z) < centre.z - extent || Mathf.Min(a.z, b.z) > centre.z + extent) return false;
            a -= centre; b -= centre; a.y = b.y = 0f;
            var delta = b - a; var low = 0f; var high = 1f;
            for (var d = 0; d < 6; d++)
            {
                var normal = ClipNormals[d];
                var denominator = Vector3.Dot(normal, delta);
                var remaining = inRadius - Vector3.Dot(normal, a);
                if (Mathf.Abs(denominator) < 0.000001f) { if (remaining < 0f) return false; continue; }
                var t = remaining / denominator;
                if (denominator > 0f) high = Mathf.Min(high, t); else low = Mathf.Max(low, t);
                if (low > high) return false;
            }
            return high >= 0f && low <= 1f;
        }
        private Vector3 Centre(HexCoordinates c) => origin + c.ToWorld(size);
        private bool Open(HexCoordinates c) => cells.TryGetValue(c, out var cell) && cell != null &&
            cell.Kind != HexCastleCellKind.Palace && cell.CanTraverse(HexCastleTraversalFaction.Assault);
    }
}
