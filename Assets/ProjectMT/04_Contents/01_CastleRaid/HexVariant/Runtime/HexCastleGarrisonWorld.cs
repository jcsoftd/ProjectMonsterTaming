using System;
using System.Collections.Generic;
using System.Linq;
using ProjectMT.Shared.Unit;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    [DisallowMultipleComponent]
    public sealed class HexCastleGarrisonWorld : MonoBehaviour // Hex 병영 소환과 정식 외형 수명을 관리한다
    {
        private readonly List<HexCastleGarrisonUnit> units = new List<HexCastleGarrisonUnit>();
        private readonly Dictionary<int, ProductionReservation> productionReservations =
            new Dictionary<int, ProductionReservation>();
        private readonly Dictionary<int, ResponseReservation> responseReservations =
            new Dictionary<int, ResponseReservation>();
        private readonly Dictionary<HexCoordinates, float> structureAlerts =
            new Dictionary<HexCoordinates, float>();
        private IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cells;
        private HexCastleGarrisonCatalog catalog;
        private HexCastleTurretCombatWorld combatWorld;
        private HexCastleThemeOneTuning tuning;
        private HexCastleDifficultyProfile difficultyProfile;
        private Transform unitsRoot;
        private Vector3 worldOrigin;
        private float cellSize;
        private int seed;
        private int spawnSequence;
        private float trainingAttackMultiplier = 1f;
        private float churchMoveSpeedMultiplier = 1f;

        public int AliveUnitCount => units.Count(value => value != null && value.IsAlive);
        public IReadOnlyList<HexCastleGarrisonUnit> Units => units;
        public int SpawnSequence => spawnSequence;
        public int ActiveProductionReservationCount => productionReservations.Count;
        public int ActiveResponseReservationCount => responseReservations.Count;
        public bool IsConfigured => catalog != null && catalog.IsComplete && cells != null;
        public float TrainingAttackMultiplier => trainingAttackMultiplier;
        public float ChurchMoveSpeedMultiplier => churchMoveSpeedMultiplier;
        public event Action<HexCastleGarrisonUnit> UnitSpawned;

        public void ApplyBuildingEffects(bool hasActiveTrainingYard, bool churchDestroyed)
        {
            trainingAttackMultiplier = hasActiveTrainingYard ? tuning.TrainingAttackMultiplier : 1f;
            churchMoveSpeedMultiplier = churchDestroyed ? tuning.ChurchRageMoveSpeedMultiplier : 1f;
            for (var index = 0; index < units.Count; index++)
            {
                units[index]?.ApplyBuildingModifiers(trainingAttackMultiplier, churchMoveSpeedMultiplier);
            }
        }

        public void Configure(
            HexCastleGarrisonCatalog targetCatalog,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            Vector3 targetWorldOrigin,
            float targetCellSize,
            int targetSeed,
            HexCastleTurretCombatWorld targetCombatWorld = null,
            HexCastleThemeOneTuning targetTuning = null,
            HexCastleDifficultyProfile targetDifficultyProfile = null)
        {
            UnbindStructureAlerts();
            catalog = targetCatalog != null && targetCatalog.IsComplete
                ? targetCatalog
                : throw new ArgumentException("Hex 수비대 카탈로그가 없거나 불완전합니다.", nameof(targetCatalog));
            cells = runtimeCells ?? throw new ArgumentNullException(nameof(runtimeCells));
            worldOrigin = targetWorldOrigin;
            cellSize = Mathf.Max(0.1f, targetCellSize);
            seed = targetSeed;
            combatWorld = targetCombatWorld;
            tuning = targetTuning ?? HexCastleThemeOneTuning.CreateDraftDefaults();
            difficultyProfile = targetDifficultyProfile;
            EnsureUnitsRoot();
            BindStructureAlerts();
        }

        public void SpawnInitialGarrison(HexCastleDifficultyProfile difficultyProfile)
        {
            SpawnInitial(
                HexCastleGarrisonUnitRole.Knight,
                HexCastleBuildingRole.KnightBarracks,
                difficultyProfile.InitialKnightCount);
            SpawnInitial(
                HexCastleGarrisonUnitRole.Farmer,
                HexCastleBuildingRole.FarmerBarracks,
                difficultyProfile.InitialFarmerCount);

            void SpawnInitial(
                HexCastleGarrisonUnitRole role,
                HexCastleBuildingRole barracksRole,
                int count)
            {
                if (count <= 0)
                {
                    return;
                }

                var origins = cells.Values
                    .Where(value => value != null && value.BuildingRole == barracksRole)
                    .OrderBy(value => value.DefenseLayer)
                    .ThenBy(value => value.Coordinates)
                    .Select(value => value.Coordinates)
                    .ToArray();
                if (origins.Length == 0 && role == HexCastleGarrisonUnitRole.Farmer)
                {
                    // 2중벽의 초기 농부는 별도 농부병영 없이 왕궁 수비용 기사병영에서 주둔을 시작한다.
                    origins = cells.Values
                        .Where(value => value != null &&
                                        value.BuildingRole == HexCastleBuildingRole.KnightBarracks)
                        .OrderBy(value => value.DefenseLayer)
                        .ThenBy(value => value.Coordinates)
                        .Select(value => value.Coordinates)
                        .ToArray();
                }

                if (origins.Length == 0)
                {
                    throw new InvalidOperationException($"초기 {role} 수비대의 병영이 없습니다.");
                }

                var spawned = 0;
                for (var index = 0; index < count; index++)
                {
                    spawned += Spawn(role, origins[index % origins.Length], 1);
                }

                if (spawned != count)
                {
                    throw new InvalidOperationException(
                        $"난이도 {difficultyProfile.Level} 초기 {role} 소환 수가 부족합니다: {spawned}/{count}");
                }
            }
        }

        public int CountAlive(
            HexCastleGarrisonUnitRole role,
            HexCoordinates origin,
            int radius)
        {
            PruneMissingUnits();
            var clampedRadius = Mathf.Max(0, radius);
            return units.Count(value =>
                value != null && value.IsAlive && value.Role == role &&
                origin.DistanceTo(value.Coordinates) <= clampedRadius);
        }

        public int Spawn(
            HexCastleGarrisonUnitRole role,
            HexCoordinates barracksCoordinates,
            int requestedCount)
        {
            if (!IsConfigured || requestedCount <= 0)
            {
                return 0;
            }

            var spawnCells = CollectSpawnCells(barracksCoordinates);
            if (spawnCells.Count == 0)
            {
                return 0;
            }

            var spawned = 0;
            for (var index = 0; index < requestedCount; index++)
            {
                var coordinates = ResolveLeastOccupiedCell(spawnCells, spawnSequence);
                var prefab = role == HexCastleGarrisonUnitRole.Knight
                    ? catalog.ResolveKnight(seed, spawnSequence)
                    : catalog.ResolveFarmer();
                if (prefab == null)
                {
                    continue;
                }

                CreateUnit(role, coordinates, prefab);
                spawned++;
            }

            return spawned;
        }

#if UNITY_EDITOR
        public HexCastleGarrisonUnit EditorSpawnSimulationUnit(
            HexCastleGarrisonUnitRole role,
            HexCoordinates coordinates)
        {
            if (!IsConfigured || !cells.TryGetValue(coordinates, out var cell) || cell == null ||
                cell.IsBlocked && cell.GateRole != HexCastleGateRole.OpenDefenderPassage)
            {
                return null;
            }

            var prefab = role == HexCastleGarrisonUnitRole.Knight
                ? catalog.ResolveKnight(seed, spawnSequence)
                : catalog.ResolveFarmer();
            if (prefab == null)
            {
                return null;
            }

            CreateUnit(role, coordinates, prefab);
            return units.Count > 0 ? units[units.Count - 1] : null;
        }
#endif

        public bool TryReserveProduction(
            HexCastleBarracksRuntime owner,
            HexCastleGarrisonUnitRole role,
            HexCoordinates origin,
            int radius,
            int maximumCount)
        {
            if (!IsConfigured || owner == null || maximumCount <= 0)
            {
                return false;
            }

            PruneMissingUnits();
            PruneProductionReservations();
            var ownerId = owner.GetInstanceID();
            if (productionReservations.ContainsKey(ownerId))
            {
                return true;
            }

            var clampedRadius = Mathf.Max(0, radius);
            var reservedCount = productionReservations.Values.Count(value =>
                value.Role == role && origin.DistanceTo(value.Origin) <= clampedRadius + 1);
            if (CountAlive(role, origin, clampedRadius) + reservedCount >= maximumCount)
            {
                return false;
            }

            productionReservations.Add(ownerId, new ProductionReservation(owner, role, origin));
            return true;
        }

        public void ReleaseProductionReservation(HexCastleBarracksRuntime owner)
        {
            if (owner != null)
            {
                productionReservations.Remove(owner.GetInstanceID());
            }
        }

        public HexCastleAssaultUnit FindResponseCandidate(
            HexCoordinates source,
            HexCoordinates home,
            int directDetectionRange,
            int leashRange)
        {
            if (combatWorld == null)
            {
                return null;
            }

            var direct = combatWorld.FindNearestAssaultUnit(source, directDetectionRange);
            if (direct != null)
            {
                return direct;
            }

            PruneStructureAlerts();
            foreach (var alert in structureAlerts.Keys
                         .Where(value => home.DistanceTo(value) <= leashRange)
                         .OrderBy(source.DistanceTo)
                         .ThenBy(value => value))
            {
                var alertedTarget = combatWorld.FindNearestAssaultUnit(alert, 2);
                if (alertedTarget != null)
                {
                    return alertedTarget;
                }
            }

            return null;
        }

        public bool TryReserveResponse(
            HexCastleGarrisonUnit unit,
            HexCastleAssaultUnit target,
            HexCoordinates targetCoordinates,
            out HexCoordinates approach)
        {
            approach = default;
            if (unit == null || target == null || !target.IsAlive || cells == null)
            {
                return false;
            }

            PruneResponseReservations();
            var unitId = unit.GetInstanceID();
            if (responseReservations.TryGetValue(unitId, out var existing))
            {
                if (existing.Target == target && existing.Approach.DistanceTo(targetCoordinates) == 1 &&
                    IsDefenderTraversable(existing.Approach))
                {
                    approach = existing.Approach;
                    return true;
                }

                responseReservations.Remove(unitId);
            }

            var targetId = target.GetInstanceID();
            if (responseReservations.Values.Count(value =>
                    value.Target != null && value.Target.GetInstanceID() == targetId) >=
                tuning.GarrisonMaximumRespondersPerTarget)
            {
                return false;
            }

            var reservedApproaches = new HashSet<HexCoordinates>(responseReservations.Values
                .Where(value => value.Target != null && value.Target.GetInstanceID() == targetId)
                .Select(value => value.Approach));
            var startDirection = PositiveModulo(unit.SpawnSequence * 5 + target.StableSpawnOrder, HexCoordinates.Directions.Length);
            for (var index = 0; index < HexCoordinates.Directions.Length; index++)
            {
                var direction = PositiveModulo(startDirection + index, HexCoordinates.Directions.Length);
                var candidate = targetCoordinates.Neighbor(direction);
                if (reservedApproaches.Contains(candidate) || !IsDefenderTraversable(candidate))
                {
                    continue;
                }

                approach = candidate;
                responseReservations[unitId] = new ResponseReservation(unit, target, candidate);
                return true;
            }

            return false;
        }

        public void ReleaseResponse(HexCastleGarrisonUnit unit)
        {
            if (unit != null)
            {
                responseReservations.Remove(unit.GetInstanceID());
            }
        }

        public void Shutdown()
        {
            UnbindStructureAlerts();
            productionReservations.Clear();
            responseReservations.Clear();
            structureAlerts.Clear();
            for (var index = units.Count - 1; index >= 0; index--)
            {
                var unit = units[index];
                if (unit == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Destroy(unit.gameObject);
                }
                else
                {
                    DestroyImmediate(unit.gameObject);
                }
            }

            units.Clear();
            spawnSequence = 0;
            UnitSpawned = null;
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void CreateUnit(
            HexCastleGarrisonUnitRole role,
            HexCoordinates coordinates,
            GameObject prefab)
        {
            EnsureUnitsRoot();
            var sequence = spawnSequence++;
            var root = new GameObject($"{role}_{sequence + 1:000}_{coordinates.Q}_{coordinates.R}");
            root.transform.SetParent(unitsRoot, false);
            root.transform.position = worldOrigin + coordinates.ToWorld(cellSize);
            root.transform.position += ResolveStackOffset(coordinates, sequence);

            var visual = Instantiate(prefab, root.transform, false);
            visual.name = $"Visual_{prefab.name}";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            var request = new UnitSpawnRequest(
                $"hex_garrison_{role.ToString().ToLowerInvariant()}_{sequence:000}",
                default,
                UnitTeam.Enemy,
                canMove: true,
                canAttack: true,
                appearanceSeed: seed * 397 ^ sequence);
            var preparations = visual.GetComponentsInChildren<MonoBehaviour>(true);
            for (var index = 0; index < preparations.Length; index++)
            {
                if (preparations[index] is IUnitSpawnPreparation preparation &&
                    !preparation.PrepareForSpawn(request))
                {
                    DestroyCreatedRoot(root);
                    throw new InvalidOperationException($"Hex 수비대 외형 준비에 실패했습니다. Prefab={prefab.name}");
                }
            }

            DisableBorrowedGameplayComponents(visual);
            var unit = root.AddComponent<HexCastleGarrisonUnit>();
            unit.Configure(
                role,
                coordinates,
                sequence,
                visual.transform,
                cells,
                combatWorld,
                worldOrigin,
                cellSize,
                tuning,
                this,
                difficultyProfile?.ResolveHealthMultiplier(role) ?? 1f,
                difficultyProfile?.ResolveAttackMultiplier(role) ?? 1f);
            unit.ApplyBuildingModifiers(trainingAttackMultiplier, churchMoveSpeedMultiplier);
            units.Add(unit);
            UnitSpawned?.Invoke(unit);
        }

        private List<HexCoordinates> CollectSpawnCells(HexCoordinates barracksCoordinates)
        {
            var result = new List<HexCoordinates>();
            var rotation = PositiveModulo(seed ^ barracksCoordinates.GetHashCode(), HexCoordinates.Directions.Length);
            for (var index = 0; index < HexCoordinates.Directions.Length; index++)
            {
                var direction = PositiveModulo(rotation + index, HexCoordinates.Directions.Length);
                var coordinates = barracksCoordinates.Neighbor(direction);
                if (!cells.TryGetValue(coordinates, out var cell) || cell == null ||
                    !cell.CanEnterFrom((direction + 3) % HexCoordinates.Directions.Length,
                        HexCastleTraversalFaction.Defender))
                {
                    continue;
                }

                result.Add(coordinates);
            }

            return result;
        }

        private HexCoordinates ResolveLeastOccupiedCell(
            IReadOnlyList<HexCoordinates> candidates,
            int sequence)
        {
            PruneMissingUnits();
            var minimum = int.MaxValue;
            var selected = candidates[PositiveModulo(sequence, candidates.Count)];
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[PositiveModulo(index + sequence, candidates.Count)];
                var occupancy = units.Count(value =>
                    value != null && value.IsAlive && value.Coordinates == candidate);
                if (occupancy >= minimum)
                {
                    continue;
                }

                minimum = occupancy;
                selected = candidate;
            }

            return selected;
        }

        private Vector3 ResolveStackOffset(HexCoordinates coordinates, int sequence)
        {
            var occupancy = units.Count(value =>
                value != null && value.IsAlive && value.Coordinates == coordinates);
            if (occupancy == 0)
            {
                return Vector3.zero;
            }

            var angle = (sequence * 137.50776f + occupancy * 60f) * Mathf.Deg2Rad;
            var radius = Mathf.Min(cellSize * 0.28f, cellSize * (0.12f + occupancy * 0.035f));
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
        }

        private void EnsureUnitsRoot()
        {
            if (unitsRoot != null)
            {
                return;
            }

            var existing = transform.Find("04_PlayableGarrisonUnits");
            unitsRoot = existing != null
                ? existing
                : new GameObject("04_PlayableGarrisonUnits").transform;
            if (unitsRoot.parent != transform)
            {
                unitsRoot.SetParent(transform, false);
            }
        }

        private void PruneMissingUnits()
        {
            for (var index = units.Count - 1; index >= 0; index--)
            {
                if (units[index] == null)
                {
                    units.RemoveAt(index);
                }
            }
        }

        private bool IsDefenderTraversable(HexCoordinates coordinates)
        {
            return cells.TryGetValue(coordinates, out var cell) && cell != null &&
                   cell.CanTraverse(HexCastleTraversalFaction.Defender);
        }

        private void BindStructureAlerts()
        {
            if (cells == null)
            {
                return;
            }

            foreach (var cell in cells.Values.Where(value => value != null && value.IsDamageable))
            {
                cell.Damaged -= HandleStructureDamaged;
                cell.Damaged += HandleStructureDamaged;
            }
        }

        private void UnbindStructureAlerts()
        {
            if (cells == null)
            {
                return;
            }

            foreach (var cell in cells.Values.Where(value => value != null))
            {
                cell.Damaged -= HandleStructureDamaged;
            }
        }

        private void HandleStructureDamaged(HexCastleCellRuntime structure, ProjectMT.Shared.Combat.DamageReport report)
        {
            if (structure != null)
            {
                structureAlerts[structure.Coordinates] = Time.time + tuning.GarrisonStructureAlertSeconds;
            }
        }

        private void PruneStructureAlerts()
        {
            foreach (var coordinates in structureAlerts
                         .Where(value => value.Value <= Time.time)
                         .Select(value => value.Key)
                         .ToArray())
            {
                structureAlerts.Remove(coordinates);
            }
        }

        private void PruneProductionReservations()
        {
            foreach (var key in productionReservations
                         .Where(value => value.Value.Owner == null || !value.Value.Owner.IsRunning)
                         .Select(value => value.Key)
                         .ToArray())
            {
                productionReservations.Remove(key);
            }
        }

        private void PruneResponseReservations()
        {
            foreach (var key in responseReservations
                         .Where(value => value.Value.Unit == null || !value.Value.Unit.IsAlive ||
                                         value.Value.Target == null || !value.Value.Target.IsAlive)
                         .Select(value => value.Key)
                         .ToArray())
            {
                responseReservations.Remove(key);
            }
        }

        private static void DisableBorrowedGameplayComponents(GameObject visual)
        {
            foreach (var actor in visual.GetComponentsInChildren<UnitActor>(true))
            {
                actor.enabled = false; // Hex 병영 런타임이 수명을 소유한다
            }

            foreach (var health in visual.GetComponentsInChildren<HealthComponent>(true))
            {
                health.enabled = false; // 정식 프리팹은 외형만 빌린다
            }

            foreach (var feedback in visual.GetComponentsInChildren<UnitVisualFeedback>(true))
            {
                feedback.enabled = false;
            }

            foreach (var collider in visual.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }

        }

        private static void DestroyCreatedRoot(GameObject root)
        {
            if (Application.isPlaying)
            {
                Destroy(root);
            }
            else
            {
                DestroyImmediate(root);
            }
        }

        private static int PositiveModulo(int value, int divisor)
        {
            var result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private sealed class ProductionReservation
        {
            public ProductionReservation(
                HexCastleBarracksRuntime owner,
                HexCastleGarrisonUnitRole role,
                HexCoordinates origin)
            {
                Owner = owner;
                Role = role;
                Origin = origin;
            }

            public HexCastleBarracksRuntime Owner { get; }
            public HexCastleGarrisonUnitRole Role { get; }
            public HexCoordinates Origin { get; }
        }

        private sealed class ResponseReservation
        {
            public ResponseReservation(
                HexCastleGarrisonUnit unit,
                HexCastleAssaultUnit target,
                HexCoordinates approach)
            {
                Unit = unit;
                Target = target;
                Approach = approach;
            }

            public HexCastleGarrisonUnit Unit { get; }
            public HexCastleAssaultUnit Target { get; }
            public HexCoordinates Approach { get; }
        }
    }
}
