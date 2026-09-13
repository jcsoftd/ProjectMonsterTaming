using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    public enum HexCastleAssaultRoutePolicy
    {
        Balanced = 0,
        ResourceRaider = 1,
        TurretHunter = 2,
        WallBreaker = 3,
        DirectAdvance = 4
    }

    public sealed class HexCastleAssaultRoutePlan
    {
        internal HexCastleAssaultRoutePlan(
            IReadOnlyList<HexCoordinates> path,
            float totalCost,
            HexCoordinates destinationApproach,
            HexCoordinates firstObstacle,
            HexCoordinates firstObstacleApproach,
            bool hasFirstObstacle,
            int routeId,
            int sectorId,
            int topologyVersion,
            IReadOnlyList<float> entryCosts = null)
        {
            EntryCosts = entryCosts ?? Array.Empty<float>();
            Path = path ?? Array.Empty<HexCoordinates>();
            TotalCost = totalCost;
            DestinationApproach = destinationApproach;
            FirstObstacle = firstObstacle;
            FirstObstacleApproach = firstObstacleApproach;
            HasFirstObstacle = hasFirstObstacle;
            RouteId = routeId;
            SectorId = sectorId;
            TopologyVersion = topologyVersion;
        }

        public IReadOnlyList<HexCoordinates> Path { get; }
        public float TotalCost { get; }
        public IReadOnlyList<float> EntryCosts { get; }
        public HexCoordinates DestinationApproach { get; }
        public HexCoordinates FirstObstacle { get; }
        public HexCoordinates FirstObstacleApproach { get; }
        public bool HasFirstObstacle { get; }
        public int RouteId { get; }
        public int SectorId { get; }
        public int TopologyVersion { get; }
        public bool IsComplete => Path.Count > 0;
    }

    public sealed class HexCastleAssaultNavigationSnapshot // 파괴 상태를 반영한 Hex 전략 비용장
    {
        private readonly struct FieldKey : IEquatable<FieldKey>
        {
            public FieldKey(
                HexCastleAssaultRoutePolicy policy,
                int expectedDefenseLayer,
                int damageBand,
                int speedBand,
                int topologyVersion)
            {
                Policy = policy;
                ExpectedDefenseLayer = expectedDefenseLayer;
                DamageBand = damageBand;
                SpeedBand = speedBand;
                TopologyVersion = topologyVersion;
            }

            public HexCastleAssaultRoutePolicy Policy { get; }
            public int ExpectedDefenseLayer { get; }
            public int DamageBand { get; }
            public int SpeedBand { get; }
            public int TopologyVersion { get; }

            public bool Equals(FieldKey other)
            {
                return Policy == other.Policy && ExpectedDefenseLayer == other.ExpectedDefenseLayer &&
                       DamageBand == other.DamageBand && SpeedBand == other.SpeedBand &&
                       TopologyVersion == other.TopologyVersion;
            }

            public override bool Equals(object obj)
            {
                return obj is FieldKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (int)Policy;
                    hash = hash * 397 ^ ExpectedDefenseLayer;
                    hash = hash * 397 ^ DamageBand;
                    hash = hash * 397 ^ SpeedBand;
                    hash = hash * 397 ^ TopologyVersion;
                    return hash;
                }
            }
        }

        private readonly struct QueueNode
        {
            public QueueNode(HexCoordinates coordinates, float cost)
            {
                Coordinates = coordinates;
                Cost = cost;
            }

            public HexCoordinates Coordinates { get; }
            public float Cost { get; }
        }

        private sealed class MinimumHeap
        {
            private readonly List<QueueNode> values = new List<QueueNode>();

            public int Count => values.Count;

            public void Push(QueueNode node)
            {
                values.Add(node);
                var index = values.Count - 1;
                while (index > 0)
                {
                    var parent = (index - 1) / 2;
                    if (values[parent].Cost <= values[index].Cost)
                    {
                        break;
                    }

                    (values[parent], values[index]) = (values[index], values[parent]);
                    index = parent;
                }
            }

            public QueueNode Pop()
            {
                var result = values[0];
                var last = values.Count - 1;
                values[0] = values[last];
                values.RemoveAt(last);
                var index = 0;
                while (true)
                {
                    var left = index * 2 + 1;
                    if (left >= values.Count)
                    {
                        break;
                    }

                    var right = left + 1;
                    var child = right < values.Count && values[right].Cost < values[left].Cost
                        ? right
                        : left;
                    if (values[index].Cost <= values[child].Cost)
                    {
                        break;
                    }

                    (values[index], values[child]) = (values[child], values[index]);
                    index = child;
                }

                return result;
            }
        }

        private readonly IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cells;
        private readonly HashSet<HexCoordinates> palaceFootprint;
        private readonly HashSet<HexCoordinates> palaceApproaches;
        private sealed class CostFieldSnapshot
        {
            public readonly Dictionary<HexCoordinates, float> Costs = new Dictionary<HexCoordinates, float>();
            public readonly Dictionary<HexCoordinates, float> Entry = new Dictionary<HexCoordinates, float>();
            public readonly Dictionary<HexCoordinates, HexCoordinates> Next = new Dictionary<HexCoordinates, HexCoordinates>();
            public long LastUse;
            public bool CostsDirty;
        }
        private readonly Dictionary<FieldKey, CostFieldSnapshot> fields = new Dictionary<FieldKey, CostFieldSnapshot>();
        private long useSequence;
        public int MaxCachedFields { get; set; } = 64;
        public int FieldBuildCount { get; private set; }
        public int FieldReuseCount { get; private set; }
        public bool LastQueryReused { get; private set; }
        private readonly float cellTravelDistance;

        public HexCastleAssaultNavigationSnapshot(
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            float cellSize)
        {
            cells = runtimeCells ?? throw new ArgumentNullException(nameof(runtimeCells));
            cellTravelDistance = Mathf.Max(0.1f, cellSize) * 1.7320508f;
            palaceFootprint = new HashSet<HexCoordinates>(cells
                .Where(pair => pair.Value != null && pair.Value.Kind == HexCastleCellKind.Palace)
                .Select(pair => pair.Key));
            palaceApproaches = new HashSet<HexCoordinates>();
            foreach (var palace in palaceFootprint)
            {
                for (var direction = 0; direction < HexCoordinates.Directions.Length; direction++)
                {
                    var candidate = palace.Neighbor(direction);
                    if (!palaceFootprint.Contains(candidate) && cells.ContainsKey(candidate))
                    {
                        palaceApproaches.Add(candidate);
                    }
                }
            }

            if (palaceFootprint.Count == 0 || palaceApproaches.Count == 0)
            {
                throw new InvalidOperationException("Hex 왕궁 점유 Cell과 외곽 공격 Cell이 필요합니다.");
            }
        }

        public int CachedFieldCount => fields.Count;
        public IReadOnlyCollection<HexCoordinates> PalaceFootprint => palaceFootprint;
        public IReadOnlyCollection<HexCoordinates> PalaceApproaches => palaceApproaches;

        public void Invalidate()
        {
            fields.Clear();
        }

        public void InvalidateCosts()
        {
            foreach (var field in fields.Values) field.CostsDirty = true;
        }

        public int FrontRouteBuildCount { get; private set; }

        public bool TryResolveFrontRoute(HexCoordinates start, float damagePerSecond, float moveSpeed,
            int topologyVersion, out HexCastleAssaultRoutePlan plan)
        {
            plan = null;
            if (!cells.ContainsKey(start) || palaceFootprint.Contains(start) ||
                !HexAttackSlotAllocator.Finite(damagePerSecond) || !HexAttackSlotAllocator.Finite(moveSpeed)) return false;
            FrontRouteBuildCount++;
            var path = new List<HexCoordinates> { start };
            var current = start;
            var axis = start.ToWorld(1f);
            var hasObstacle = false;
            var obstacle = default(HexCoordinates);
            var approach = start;
            var cost = 0f;
            while (!palaceApproaches.Contains(current))
            {
                var found = false;
                var next = default(HexCoordinates);
                var bestDeviation = float.PositiveInfinity;
                for (var direction = 0; direction < 6; direction++)
                {
                    var candidate = current.Neighbor(direction);
                    if (candidate.DistanceFromOrigin >= current.DistanceFromOrigin ||
                        !cells.TryGetValue(candidate, out var cell) || cell == null || palaceFootprint.Contains(candidate)) continue;
                    var point = candidate.ToWorld(1f);
                    var deviation = Mathf.Abs(axis.x * point.z - axis.z * point.x);
                    if (found && (deviation > bestDeviation + .0001f ||
                        Mathf.Abs(deviation - bestDeviation) <= .0001f && candidate.CompareTo(next) >= 0)) continue;
                    next = candidate; bestDeviation = deviation; found = true;
                }
                if (!found) return false;
                var target = cells[next];
                if (!target.CanTraverse(HexCastleTraversalFaction.Assault) && !target.IsDamageable) return false;
                cost += Vector3.Distance(cells[current].transform.position, target.transform.position) / Mathf.Max(.1f, moveSpeed);
                if (target.IsBlocked)
                {
                    if (!hasObstacle) { obstacle = next; approach = current; hasObstacle = true; }
                    cost += target.CurrentHealth / Mathf.Max(.1f, damagePerSecond);
                }
                path.Add(next); current = next;
            }
            var sector = ResolveSector(start);
            var anchor = hasObstacle ? obstacle : current;
            plan = new HexCastleAssaultRoutePlan(path, cost, current, obstacle, approach, hasObstacle,
                ResolveRouteId(sector, anchor), sector, topologyVersion);
            return true; // 소환 측 진입선을 정하며 미래 비용으로 다른 전면을 고르지 않는다.
        }

        public bool TryResolveRoute(
            HexCoordinates start,
            HexCastleAssaultRoutePolicy policy,
            int expectedDefenseLayer,
            float damagePerSecond,
            float moveSpeed,
            int topologyVersion,
            out HexCastleAssaultRoutePlan plan)
        {
            plan = null;
            LastQueryReused = false;
            if (!HexAttackSlotAllocator.Finite(damagePerSecond) || !HexAttackSlotAllocator.Finite(moveSpeed) ||
                !cells.ContainsKey(start) || palaceFootprint.Contains(start))
            {
                return false;
            }

            var damageBand = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(1f, damagePerSecond) / 10f));
            var speedBand = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(0.1f, moveSpeed) * 4f));
            var key = new FieldKey(
                policy,
                Mathf.Max(0, expectedDefenseLayer),
                damageBand,
                speedBand,
                topologyVersion);
            var existing = fields.TryGetValue(key, out var field);
            if (!existing || field.CostsDirty)
            {
                field = BuildReverseField(
                    policy,
                    key.ExpectedDefenseLayer,
                    damageBand * 10f,
                    speedBand / 4f);
                if (!existing && fields.Count >= Math.Max(1, MaxCachedFields))
                {
                    var oldest = fields.OrderBy(p => p.Value.LastUse).First().Key;
                    fields.Remove(oldest);
                }
                fields[key] = field;
                FieldBuildCount++;
            }
            else
            {
                LastQueryReused = true;
                FieldReuseCount++;
            }

            field.LastUse = ++useSequence;
            if (!field.Costs.ContainsKey(start))
            {
                return false;
            }

            var path = ReconstructPath(
                start,
                field,
                policy,
                key.ExpectedDefenseLayer,
                damageBand * 10f,
                speedBand / 4f);
            if (path.Count == 0)
            {
                return false;
            }

            var hasObstacle = false;
            var obstacle = default(HexCoordinates);
            var obstacleApproach = start;
            for (var index = 1; index < path.Count; index++)
            {
                if (!cells.TryGetValue(path[index], out var cell) || cell == null || !cell.IsBlocked)
                {
                    continue;
                }

                hasObstacle = true;
                obstacle = path[index];
                obstacleApproach = path[index - 1];
                break;
            }

            var sector = ResolveSector(start);
            var routeAnchor = hasObstacle ? obstacle : path[path.Count - 1];
            var routeId = ResolveRouteId(sector, routeAnchor);
            plan = new HexCastleAssaultRoutePlan(
                path,
                field.Costs[start],
                path[path.Count - 1],
                obstacle,
                obstacleApproach,
                hasObstacle,
                routeId,
                sector,
                topologyVersion,
                path.Skip(1).Select(c => field.Entry[c]).ToArray());
            return true;
        }

        public bool TryResolveOpenApproachRoute(
            HexCoordinates start,
            HexCoordinates target,
            int maximumRangeCells,
            out IReadOnlyList<HexCoordinates> route,
            out HexCoordinates approach)
        {
            return TryResolveOpenApproachRoute(
                start,
                target,
                maximumRangeCells,
                null,
                out route,
                out approach);
        }

        public bool TryResolveOpenApproachRoute(
            HexCoordinates start,
            HexCoordinates target,
            int maximumRangeCells,
            IReadOnlyCollection<HexCoordinates> excludedApproaches,
            out IReadOnlyList<HexCoordinates> route,
            out HexCoordinates approach)
        {
            return TryResolveOpenApproachRoute(
                start,
                target,
                maximumRangeCells,
                excludedApproaches,
                null,
                out route,
                out approach);
        }

        public bool TryResolveOpenApproachRoute(
            HexCoordinates start, HexCoordinates target, int maximumRangeCells,
            IReadOnlyCollection<HexCoordinates> excludedApproaches, Predicate<HexCoordinates> approachPredicate,
            out IReadOnlyList<HexCoordinates> route, out HexCoordinates approach)
        {
            route = Array.Empty<HexCoordinates>(); approach = start;
            if (!cells.TryGetValue(start, out var startCell) || startCell == null ||
                palaceFootprint.Contains(start) || !startCell.CanTraverse(HexCastleTraversalFaction.Assault)) return false;
            var queue = new Queue<HexCoordinates>();
            var previous = new Dictionary<HexCoordinates, HexCoordinates>();
            var visited = new HashSet<HexCoordinates> { start };
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current.DistanceTo(target) <= Math.Max(1, maximumRangeCells) &&
                    (excludedApproaches == null || !excludedApproaches.Contains(current)) &&
                    (approachPredicate == null || approachPredicate(current)))
                {
                    var path = new List<HexCoordinates> { current };
                    var p = current;
                    while (p != start) { p = previous[p]; path.Add(p); }
                    path.Reverse(); route = path; approach = current; return true;
                }
                for (var d = 0; d < 6; d++)
                {
                    var next = current.Neighbor(d);
                    if (visited.Contains(next) || palaceFootprint.Contains(next) ||
                        !cells.TryGetValue(next, out var nextCell) || nextCell == null ||
                        !HexRoutePlanner.CanTraverseStep(cells[current], nextCell, d, HexCastleTraversalFaction.Assault)) continue;
                    visited.Add(next); previous[next] = current; queue.Enqueue(next);
                }
            }
            return false;
        }

        public bool TryResolveOpenFollowRoute(
            HexCoordinates start,
            HexCoordinates target,
            int stopRangeCells,
            out IReadOnlyList<HexCoordinates> route,
            out HexCoordinates approach)
        {
            route = Array.Empty<HexCoordinates>();
            approach = start;
            if (!cells.TryGetValue(target, out var targetCell) || targetCell == null ||
                !targetCell.CanTraverse(HexCastleTraversalFaction.Assault))
            {
                return false;
            }

            var fullRoute = new HexRoutePlanner().FindTraversalRoute(
                cells,
                start,
                target,
                HexCastleTraversalFaction.Assault);
            if (fullRoute.Count == 0)
            {
                return false;
            }

            var stopIndex = Mathf.Max(0, fullRoute.Count - 1 - Mathf.Max(1, stopRangeCells));
            route = fullRoute.Take(stopIndex + 1).ToArray();
            approach = route[route.Count - 1];
            return true;
        }

        private CostFieldSnapshot BuildReverseField(
            HexCastleAssaultRoutePolicy policy, int expectedDefenseLayer, float damagePerSecond, float moveSpeed)
        {
            var result = new CostFieldSnapshot();
            // HP 구간 변경 때만 무효화하고 실제 질의된 공유 필드만 재생성한다.
            foreach (var pair in cells)
            {
                var entry = ResolveEntryCost(pair.Key, policy, expectedDefenseLayer, damagePerSecond, moveSpeed);
                if (!float.IsPositiveInfinity(entry)) result.Entry.Add(pair.Key, entry);
            }
            var heap = new MinimumHeap();
            foreach (var goal in palaceApproaches.OrderBy(c => c))
            {
                if (!result.Entry.ContainsKey(goal)) continue;
                result.Costs[goal] = 0f;
                heap.Push(new QueueNode(goal, 0f));
            }
            while (heap.Count > 0)
            {
                var current = heap.Pop();
                if (!result.Costs.TryGetValue(current.Coordinates, out var currentCost) || current.Cost > currentCost + 0.0001f) continue;
                var entryCost = result.Entry[current.Coordinates];
                for (var direction = 0; direction < 6; direction++)
                {
                    var predecessor = current.Coordinates.Neighbor(direction);
                    if (!result.Entry.ContainsKey(predecessor)) continue;
                    var candidate = currentCost + entryCost;
                    if (result.Costs.TryGetValue(predecessor, out var known) && candidate >= known - 0.0001f) continue;
                    result.Costs[predecessor] = candidate;
                    result.Next[predecessor] = current.Coordinates;
                    heap.Push(new QueueNode(predecessor, candidate));
                }
            }
            return result;
        }

        private IReadOnlyList<HexCoordinates> ReconstructPath(
            HexCoordinates start, CostFieldSnapshot field, HexCastleAssaultRoutePolicy policy,
            int expectedDefenseLayer, float damagePerSecond, float moveSpeed)
        {
            var result = new List<HexCoordinates> { start };
            var current = start;
            var guard = cells.Count + 1;
            while (!palaceApproaches.Contains(current) && guard-- > 0)
            {
                if (!field.Next.TryGetValue(current, out var next) ||
                    !field.Costs.TryGetValue(next, out var remaining) ||
                    remaining >= field.Costs[current]) return Array.Empty<HexCoordinates>();
                current = next;
                result.Add(current);
            }
            return palaceApproaches.Contains(current) ? result : Array.Empty<HexCoordinates>();
        }

        private bool CanUseCell(HexCoordinates coordinates, int expectedDefenseLayer)
        {
            if (!cells.TryGetValue(coordinates, out var cell) || cell == null ||
                palaceFootprint.Contains(coordinates))
            {
                return false;
            }

            if (!cell.IsBlocked)
            {
                return true;
            }

            if (!cell.IsDamageable || !cell.IsAlive)
            {
                return false;
            }

            return cell.WallRole == HexCastleWallRole.Partition || !IsRingWall(cell) ||
                   expectedDefenseLayer > 0 && cell.DefenseLayer <= expectedDefenseLayer;
        }

        private float ResolveEntryCost(
            HexCoordinates coordinates,
            HexCastleAssaultRoutePolicy policy,
            int expectedDefenseLayer,
            float damagePerSecond,
            float moveSpeed)
        {
            if (!CanUseCell(coordinates, expectedDefenseLayer))
            {
                return float.PositiveInfinity;
            }

            var travel = cellTravelDistance / Mathf.Max(0.1f, moveSpeed);
            var cell = cells[coordinates];
            if (!cell.IsBlocked)
            {
                return travel;
            }

            var destruction = cell.CurrentHealth / Mathf.Max(1f, damagePerSecond);
            return travel + destruction * ResolveDestructionWeight(cell, policy);
        }

        private static float ResolveDestructionWeight(
            HexCastleCellRuntime cell,
            HexCastleAssaultRoutePolicy policy)
        {
            var isWall = cell.Kind == HexCastleCellKind.Wall || cell.Kind == HexCastleCellKind.Tower ||
                         cell.Kind == HexCastleCellKind.Gate;
            var isTurret = cell.BuildingRole == HexCastleBuildingRole.Turret;
            switch (policy)
            {
                case HexCastleAssaultRoutePolicy.ResourceRaider:
                    return isWall ? 1.35f : 0.55f;
                case HexCastleAssaultRoutePolicy.TurretHunter:
                    return isTurret ? 0.45f : isWall ? 1.15f : 1f;
                case HexCastleAssaultRoutePolicy.WallBreaker:
                    return isWall ? 0.45f : 1.2f;
                case HexCastleAssaultRoutePolicy.DirectAdvance:
                    return isWall ? 0.75f : 1.65f;
                default:
                    return isWall ? 1f : 1.2f;
            }
        }

        private static bool IsRingWall(HexCastleCellRuntime cell)
        {
            return cell != null && cell.DefenseLayer > 0 &&
                   cell.WallRole != HexCastleWallRole.None &&
                   cell.WallRole != HexCastleWallRole.Partition;
        }

        private static int ResolveSector(HexCoordinates coordinates)
        {
            var point = coordinates.ToWorld(1f);
            if (point.sqrMagnitude <= 0.0001f)
            {
                return 0;
            }

            point.Normalize();
            var best = 0;
            var bestDot = float.NegativeInfinity;
            for (var direction = 0; direction < HexCoordinates.Directions.Length; direction++)
            {
                var axis = HexCoordinates.Directions[direction].ToWorld(1f).normalized;
                var dot = Vector3.Dot(point, axis);
                if (dot > bestDot)
                {
                    best = direction;
                    bestDot = dot;
                }
            }

            return best;
        }

        private static int ResolveRouteId(int sector, HexCoordinates anchor)
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
    }
}
