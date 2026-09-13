using System;
using System.Collections.Generic;
using System.Linq;
using MoreMountains.Feedbacks;
using ProjectMT.Shared.Combat;
using ProjectMT.Shared.Unit;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    public enum HexCastleGarrisonUnitRole
    {
        Knight = 0,
        Farmer = 1
    }

    public enum HexCastleGarrisonState
    {
        Idle = 0,
        Patrol = 1,
        Chase = 2,
        Attack = 3,
        Return = 4,
        Dead = 5,
        Jump = 6
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(HealthComponent))]
    public sealed class HexCastleGarrisonUnit : MonoBehaviour // Cell 경로를 따르는 Hex 전용 수비대
    {
        private const float ArrivalDistance = 0.04f;
        private const float PatrolWaitSeconds = 1.2f;

        [SerializeField] private HexCastleGarrisonUnitRole role;
        [SerializeField] private HexCastleGarrisonState state;
        [SerializeField] private int q;
        [SerializeField] private int r;
        [SerializeField] private int homeQ;
        [SerializeField] private int homeR;
        [SerializeField] private int spawnSequence;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private HealthComponent health;
        [SerializeField, Min(0.1f)] private float moveSpeed;
        [SerializeField, Min(0f)] private float attackDamage;
        [SerializeField, Min(0.1f)] private float attackInterval;
        [SerializeField, Min(1)] private int detectionRangeCells;
        [SerializeField, Min(1)] private int leashRangeCells;
        [SerializeField, Min(1)] private int patrolRadiusCells;
        [SerializeField, Min(0.1f)] private float targetSearchInterval;
        [SerializeField, Min(0f)] private float targetSearchCooldown;
        [SerializeField, Min(0f)] private float minimumResponseDelay;
        [SerializeField, Min(0f)] private float maximumResponseDelay;
        [SerializeField, Min(0.1f)] private float blockerJumpDuration;
        [SerializeField, Min(0.1f)] private float blockerJumpHeight;
        [SerializeField] private bool isJumping;

        private readonly List<HexCoordinates> route = new List<HexCoordinates>();
        private readonly HexRoutePlanner routePlanner = new HexRoutePlanner();
        private IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> cells;
        private HexCastleTurretCombatWorld combatWorld;
        private HexCastleGarrisonWorld garrisonWorld;
        private HexCastleAssaultUnit target;
        private HexCastleAssaultUnit pendingTarget;
        private Vector3 worldOrigin;
        private float cellSize;
        private float attackCooldown;
        private float patrolWait;
        private float responseDelayRemaining;
        private float passiveStaggerRemaining;
        private float jumpElapsed;
        private float baseMoveSpeed;
        private float baseAttackDamage;
        private int routeIndex;
        private int patrolSequence;
        private int jumpCount;
        private Vector3 jumpStartPosition;
        private Vector3 jumpEndPosition;
        private HexCoordinates jumpDestination;
        private MMF_Player jumpFeedback;
        private Renderer[] renderers = Array.Empty<Renderer>();

        public HexCastleGarrisonUnitRole Role => role;
        public HexCastleGarrisonState State => state;
        public HexCoordinates Coordinates => new HexCoordinates(q, r);
        public HexCoordinates HomeCoordinates => new HexCoordinates(homeQ, homeR);
        public int SpawnSequence => spawnSequence;
        public Transform VisualRoot => visualRoot;
        public HealthComponent Health => health;
        public HexCastleAssaultUnit CurrentTarget => target;
        public bool IsJumping => isJumping;
        public int JumpCount => jumpCount;
        public float MoveSpeed => moveSpeed;
        public float AttackDamage => attackDamage;
        public float TargetSearchInterval => targetSearchInterval;
        public float TargetSearchCooldown => targetSearchCooldown;
        public int TargetSearchCount { get; private set; }
        public event Action<HexCastleGarrisonUnit, DamageReport> Damaged;
        public float ResponseDelayRemaining => responseDelayRemaining;
        public bool IsAlive => health != null && health.IsAlive && gameObject.activeInHierarchy;
        public bool IsConfigured => cells != null && health != null;

        public void ApplyBuildingModifiers(float attackMultiplier, float moveSpeedMultiplier)
        {
            attackDamage = baseAttackDamage * Mathf.Max(1f, attackMultiplier);
            moveSpeed = baseMoveSpeed * Mathf.Max(1f, moveSpeedMultiplier);
        }

        public void Configure(
            HexCastleGarrisonUnitRole unitRole,
            HexCoordinates spawnCoordinates,
            int sequence,
            Transform targetVisualRoot,
            IReadOnlyDictionary<HexCoordinates, HexCastleCellRuntime> runtimeCells,
            HexCastleTurretCombatWorld targetCombatWorld,
            Vector3 targetWorldOrigin,
            float targetCellSize,
            HexCastleThemeOneTuning tuning,
            HexCastleGarrisonWorld targetGarrisonWorld = null,
            float healthMultiplier = 1f,
            float attackMultiplier = 1f)
        {
            if (runtimeCells == null)
            {
                throw new ArgumentNullException(nameof(runtimeCells));
            }

            if (tuning == null)
            {
                throw new ArgumentNullException(nameof(tuning));
            }

            role = unitRole;
            q = spawnCoordinates.Q;
            r = spawnCoordinates.R;
            homeQ = q;
            homeR = r;
            spawnSequence = Mathf.Max(0, sequence);
            visualRoot = targetVisualRoot;
            cells = runtimeCells;
            combatWorld = targetCombatWorld;
            garrisonWorld = targetGarrisonWorld;
            worldOrigin = targetWorldOrigin;
            cellSize = Mathf.Max(0.1f, targetCellSize);
            patrolRadiusCells = tuning.GarrisonPatrolRadiusCells;
            targetSearchInterval = ResolveTargetSearchInterval(
                tuning.GarrisonMinimumTargetSearchInterval,
                tuning.GarrisonMaximumTargetSearchInterval);
            minimumResponseDelay = tuning.GarrisonMinimumResponseDelay;
            maximumResponseDelay = tuning.GarrisonMaximumResponseDelay;
            blockerJumpDuration = tuning.KnightBlockerJumpDuration;
            blockerJumpHeight = tuning.KnightBlockerJumpHeight * cellSize;
            health = GetComponent<HealthComponent>();
            healthMultiplier = Mathf.Max(0.01f, healthMultiplier);
            attackMultiplier = Mathf.Max(0.01f, attackMultiplier);
            if (role == HexCastleGarrisonUnitRole.Knight)
            {
                health.Initialize(tuning.KnightHealth * healthMultiplier);
                moveSpeed = tuning.KnightMoveSpeed;
                attackDamage = tuning.KnightAttackDamage * attackMultiplier;
                attackInterval = tuning.KnightAttackInterval;
                detectionRangeCells = tuning.KnightDetectionRangeCells;
                leashRangeCells = tuning.KnightLeashRangeCells;
            }
            else
            {
                health.Initialize(tuning.FarmerHealth * healthMultiplier);
                moveSpeed = tuning.FarmerMoveSpeed;
                attackDamage = tuning.FarmerAttackDamage * attackMultiplier;
                attackInterval = tuning.FarmerAttackInterval;
                detectionRangeCells = tuning.FarmerDetectionRangeCells;
                leashRangeCells = tuning.FarmerLeashRangeCells;
            }

            baseMoveSpeed = moveSpeed;
            baseAttackDamage = attackDamage;

            renderers = visualRoot == null
                ? Array.Empty<Renderer>()
                : visualRoot.GetComponentsInChildren<Renderer>(true);
            health.Died -= HandleDied;
            health.Damaged -= HandleDamaged;
            health.Damaged += HandleDamaged;
            health.Died += HandleDied;
            attackCooldown = attackInterval * ((spawnSequence % 5) / 5f);
            patrolWait = PatrolWaitSeconds * ((spawnSequence % 3) / 3f);
            state = HexCastleGarrisonState.Idle;
            route.Clear();
            routeIndex = 0;
            target = null;
            pendingTarget = null;
            targetSearchCooldown = 0f;
            TargetSearchCount = 0;
            responseDelayRemaining = 0f;
            passiveStaggerRemaining = 0f;
            isJumping = false;
            jumpCount = 0;
            ConfigureJumpFeedback();
        }

        public bool ApplyDamage(float amount, Vector3 hitPoint)
        {
            return IsAlive && amount > 0f &&
                   health.ApplyDamage(new DamageRequest(null, amount, hitPoint));
        }

        public bool TryApplyPassiveStagger(float duration)
        {
            if (!IsAlive || duration <= 0f)
            {
                return false;
            }

            passiveStaggerRemaining = Mathf.Max(passiveStaggerRemaining, Mathf.Min(0.5f, duration));
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (!IsConfigured || !IsAlive)
            {
                return;
            }

            deltaTime = Mathf.Max(0f, deltaTime);
            passiveStaggerRemaining = Mathf.Max(0f, passiveStaggerRemaining - deltaTime);
            if (passiveStaggerRemaining > 0f)
            {
                state = HexCastleGarrisonState.Idle;
                return;
            }
            attackCooldown = Mathf.Max(0f, attackCooldown - deltaTime);
            patrolWait = Mathf.Max(0f, patrolWait - deltaTime);
            targetSearchCooldown = Mathf.Max(0f, targetSearchCooldown - deltaTime);
            responseDelayRemaining = Mathf.Max(0f, responseDelayRemaining - deltaTime);
            RefreshTarget();
            if (target != null)
            {
                TickCombat(deltaTime);
                return;
            }

            TickPatrol(deltaTime);
        }

        public void Shutdown()
        {
            if (health != null)
            {
                health.Damaged -= HandleDamaged;
                health.Died -= HandleDied;
            }

            HideHealthBar();
            ClearTarget();
            pendingTarget = null;
            isJumping = false;
            route.Clear();
            state = IsAlive ? HexCastleGarrisonState.Idle : HexCastleGarrisonState.Dead;
        }

        private void Update()
        {
            if (Application.isPlaying)
            {
                Tick(Time.deltaTime);
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void RefreshTarget()
        {
            if (target != null && (!target.IsAlive ||
                                   HomeCoordinates.DistanceTo(ResolveCoordinates(target.transform.position)) >
                                   leashRangeCells))
            {
                ClearTarget();
            }

            if (target != null || combatWorld == null)
            {
                return;
            }

            if (pendingTarget != null)
            {
                if (!pendingTarget.IsAlive ||
                    HomeCoordinates.DistanceTo(ResolveCoordinates(pendingTarget.transform.position)) >
                    leashRangeCells)
                {
                    pendingTarget = null;
                    responseDelayRemaining = 0f;
                    return;
                }

                if (responseDelayRemaining > 0f)
                {
                    return;
                }

                var pendingCoordinates = ResolveCoordinates(pendingTarget.transform.position);
                if (garrisonWorld != null &&
                    garrisonWorld.TryReserveResponse(this, pendingTarget, pendingCoordinates, out _))
                {
                    target = pendingTarget;
                    pendingTarget = null;
                    route.Clear();
                    return;
                }

                pendingTarget = null;
                targetSearchCooldown = targetSearchInterval;
                return;
            }

            if (targetSearchCooldown > 0f)
            {
                return;
            }

            targetSearchCooldown = targetSearchInterval;
            TargetSearchCount++;

            var candidate = garrisonWorld != null
                ? garrisonWorld.FindResponseCandidate(
                    Coordinates,
                    HomeCoordinates,
                    detectionRangeCells,
                    leashRangeCells)
                : combatWorld.FindNearestAssaultUnit(Coordinates, detectionRangeCells);
            if (candidate == null)
            {
                return;
            }

            if (garrisonWorld == null)
            {
                target = candidate;
                return;
            }

            pendingTarget = candidate;
            responseDelayRemaining = ResolveResponseDelay(candidate);
        }

        private ProjectMT.Shared.Audio.SfxPool attackAudioPool;
        private ProjectMT.Shared.Audio.SfxCue attackSfx;

        public void ConfigureAttackAudio(ProjectMT.Shared.Audio.SfxPool pool, ProjectMT.Shared.Audio.SfxCue cue)
        {
            attackAudioPool = pool;
            attackSfx = cue;
        }

        private void TickCombat(float deltaTime)
        {
            var targetCoordinates = ResolveCoordinates(target.transform.position);
            if (CanAttackTarget(targetCoordinates))
            {
                state = HexCastleGarrisonState.Attack;
                route.Clear();
                FaceTowards(target.transform.position, deltaTime);
                if (attackCooldown <= 0f)
                {
                    attackCooldown = attackInterval;
                    attackAudioPool?.Play(attackSfx, transform.position);
                    target.ApplyDamage(
                        attackDamage,
                        target.transform.position,
                        this,
                        null); // 실제 공격한 수비대를 반격 위협으로 전달한다
                }

                return;
            }

            var movementDestination = targetCoordinates;
            if (garrisonWorld != null &&
                !garrisonWorld.TryReserveResponse(this, target, targetCoordinates, out movementDestination))
            {
                ClearTarget();
                return;
            }

            if (route.Count == 0 || routeIndex >= route.Count || route[route.Count - 1] != movementDestination)
            {
                SetRoute(movementDestination);
            }

            if (route.Count > 1)
            {
                state = HexCastleGarrisonState.Chase;
                MoveAlongRoute(deltaTime);
                return;
            }

            ClearTarget(); // 닫힌 성벽 너머 대상은 공격하지 않는다
        }

        private void TickPatrol(float deltaTime)
        {
            if (route.Count > 1 && routeIndex < route.Count)
            {
                state = Coordinates.DistanceTo(HomeCoordinates) > patrolRadiusCells
                    ? HexCastleGarrisonState.Return
                    : HexCastleGarrisonState.Patrol;
                MoveAlongRoute(deltaTime);
                return;
            }

            route.Clear();
            if (Coordinates.DistanceTo(HomeCoordinates) > patrolRadiusCells)
            {
                SetRoute(HomeCoordinates);
                state = HexCastleGarrisonState.Return;
                return;
            }

            state = HexCastleGarrisonState.Idle;
            if (patrolWait > 0f)
            {
                return;
            }

            patrolWait = PatrolWaitSeconds;
            if (TryResolvePatrolDestination(out var destination))
            {
                SetRoute(destination);
                state = route.Count > 1 ? HexCastleGarrisonState.Patrol : HexCastleGarrisonState.Idle;
            }
        }

        private bool TryResolvePatrolDestination(out HexCoordinates destination)
        {
            var candidates = cells.Keys
                .Where(value => value != Coordinates &&
                                HomeCoordinates.DistanceTo(value) <= patrolRadiusCells &&
                                cells[value] != null &&
                                cells[value].CanTraverse(HexCastleTraversalFaction.Defender))
                .OrderBy(value => value)
                .ToArray();
            if (candidates.Length == 0)
            {
                destination = Coordinates;
                return false;
            }

            var start = PositiveModulo(spawnSequence * 31 + patrolSequence++, candidates.Length);
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[PositiveModulo(start + index, candidates.Length)];
                var candidateRoute = FindRoute(candidate);
                if (candidateRoute.Count <= 1)
                {
                    continue;
                }

                destination = candidate;
                return true;
            }

            destination = Coordinates;
            return false;
        }

        private void SetRoute(HexCoordinates destination)
        {
            route.Clear();
            route.AddRange(FindRoute(destination));
            routeIndex = route.Count > 1 ? 1 : route.Count;
        }

        private IReadOnlyList<HexCoordinates> FindRoute(HexCoordinates destination)
        {
            return role == HexCastleGarrisonUnitRole.Knight
                ? routePlanner.FindTraversalRouteWithSingleBlockerJump(
                    cells,
                    Coordinates,
                    destination,
                    HexCastleTraversalFaction.Defender,
                    CanJumpOver)
                : routePlanner.FindTraversalRoute(
                    cells,
                    Coordinates,
                    destination,
                    HexCastleTraversalFaction.Defender);
        }

        private void MoveAlongRoute(float deltaTime)
        {
            if (isJumping)
            {
                TickJump(deltaTime);
                return;
            }

            if (routeIndex >= route.Count)
            {
                route.Clear();
                return;
            }

            var destinationCoordinates = route[routeIndex];
            var destination = ResolvePosition(destinationCoordinates);
            if (role == HexCastleGarrisonUnitRole.Knight &&
                TryResolveJumpDirection(Coordinates, destinationCoordinates, out _))
            {
                BeginJump(destinationCoordinates, destination);
                return;
            }

            var direction = destination - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    Quaternion.LookRotation(direction.normalized, Vector3.up),
                    540f * deltaTime);
            }

            transform.position = Vector3.MoveTowards(
                transform.position,
                destination,
                moveSpeed * deltaTime);
            if (PlanarDistanceSquared(transform.position, destination) > ArrivalDistance * ArrivalDistance)
            {
                return;
            }

            transform.position = destination;
            q = destinationCoordinates.Q;
            r = destinationCoordinates.R;
            routeIndex++;
            if (routeIndex >= route.Count)
            {
                route.Clear();
            }
        }

        private bool CanAttackTarget(HexCoordinates targetCoordinates)
        {
            if (Coordinates.DistanceTo(targetCoordinates) > 1)
            {
                return false;
            }

            var direction = ResolveNeighborDirection(Coordinates, targetCoordinates);
            if (direction < 0 ||
                !cells.TryGetValue(Coordinates, out var currentCell) || currentCell == null ||
                !cells.TryGetValue(targetCoordinates, out var targetCell) || targetCell == null)
            {
                return false;
            }

            return HexRoutePlanner.CanTraverseStep(
                currentCell,
                targetCell,
                direction,
                HexCastleTraversalFaction.Defender);
        }

        private HexCoordinates ResolveCoordinates(Vector3 position)
        {
            return HexCoordinates.FromWorld(position - worldOrigin, cellSize);
        }

        private Vector3 ResolvePosition(HexCoordinates coordinates)
        {
            return worldOrigin + coordinates.ToWorld(cellSize);
        }

        private void FaceTowards(Vector3 position, float deltaTime)
        {
            var direction = position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                Quaternion.LookRotation(direction.normalized, Vector3.up),
                540f * deltaTime);
        }

        private void HandleDied(DamageReport report)
        {
            HideHealthBar();
            state = HexCastleGarrisonState.Dead;
            ClearTarget();
            pendingTarget = null;
            isJumping = false;
            route.Clear();
            for (var index = 0; index < renderers.Length; index++)
            {
                if (renderers[index] != null)
                {
                    renderers[index].enabled = false;
                }
            }
        }

        private void HandleDamaged(DamageReport report)
        {
            HexCastleOverheadHealthBar.ShowDamage(transform, health);
            Damaged?.Invoke(this, report);
        }

        private void HideHealthBar()
        {
            if (TryGetComponent<HexCastleOverheadHealthBar>(out var healthBar))
            {
                healthBar.HideImmediately();
            }
        }

        private static int ResolveNeighborDirection(HexCoordinates from, HexCoordinates to)
        {
            for (var direction = 0; direction < HexCoordinates.Directions.Length; direction++)
            {
                if (from.Neighbor(direction) == to)
                {
                    return direction;
                }
            }

            return -1;
        }

        private bool CanJumpOver(HexCastleCellRuntime cell)
        {
            return role == HexCastleGarrisonUnitRole.Knight && cell != null && cell.IsAlive &&
                   cell.Kind == HexCastleCellKind.Building &&
                   cell.BuildingRole == HexCastleBuildingRole.Blocker;
        }

        private bool TryResolveJumpDirection(
            HexCoordinates from,
            HexCoordinates to,
            out int resolvedDirection)
        {
            for (var direction = 0; direction < HexCoordinates.Directions.Length; direction++)
            {
                var blockerCoordinates = from.Neighbor(direction);
                if (blockerCoordinates.Neighbor(direction) != to ||
                    !cells.TryGetValue(blockerCoordinates, out var blocker) || !CanJumpOver(blocker))
                {
                    continue;
                }

                resolvedDirection = direction;
                return true;
            }

            resolvedDirection = -1;
            return false;
        }

        private void BeginJump(HexCoordinates destinationCoordinates, Vector3 destination)
        {
            isJumping = true;
            jumpElapsed = 0f;
            jumpStartPosition = transform.position;
            jumpEndPosition = destination;
            jumpDestination = destinationCoordinates;
            jumpCount++;
            state = HexCastleGarrisonState.Jump;
            FaceTowards(destination, blockerJumpDuration);
            if (Application.isPlaying)
            {
                jumpFeedback?.PlayFeedbacks();
            }
        }

        private void TickJump(float deltaTime)
        {
            jumpElapsed += Mathf.Max(0f, deltaTime);
            var ratio = Mathf.Clamp01(jumpElapsed / blockerJumpDuration);
            var position = Vector3.Lerp(jumpStartPosition, jumpEndPosition, ratio);
            position.y += Mathf.Sin(ratio * Mathf.PI) * blockerJumpHeight;
            transform.position = position;
            if (ratio < 1f)
            {
                return;
            }

            transform.position = jumpEndPosition;
            q = jumpDestination.Q;
            r = jumpDestination.R;
            isJumping = false;
            routeIndex++;
            if (routeIndex >= route.Count)
            {
                route.Clear();
            }
        }

        private float ResolveResponseDelay(HexCastleAssaultUnit candidate)
        {
            var range = Mathf.Max(0f, maximumResponseDelay - minimumResponseDelay);
            var score = PositiveModulo(
                spawnSequence * 37 + candidate.StableSpawnOrder * 13,
                1000) / 999f;
            return minimumResponseDelay + range * score;
        }

        private float ResolveTargetSearchInterval(float minimum, float maximum)
        {
            var score = PositiveModulo(
                spawnSequence * 73 + homeQ * 17 + homeR * 31,
                1000) / 999f;
            return Mathf.Lerp(Mathf.Max(0.1f, minimum), Mathf.Max(minimum, maximum), score);
        }

        private void ClearTarget()
        {
            garrisonWorld?.ReleaseResponse(this);
            target = null;
            pendingTarget = null;
            responseDelayRemaining = 0f;
            targetSearchCooldown = targetSearchInterval;
            route.Clear();
        }

        private void ConfigureJumpFeedback()
        {
            if (role != HexCastleGarrisonUnitRole.Knight || visualRoot == null)
            {
                jumpFeedback = null;
                return;
            }

            jumpFeedback = GetComponent<MMF_Player>() ?? gameObject.AddComponent<MMF_Player>();
            jumpFeedback.FeedbacksList = new List<MMF_Feedback>
            {
                new MMF_SquashAndStretch
                {
                    SquashAndStretchTarget = visualRoot,
                    Mode = MMF_SquashAndStretch.Modes.Absolute,
                    Axis = MMF_SquashAndStretch.PossibleAxis.YtoXZ,
                    AnimateScaleDuration = blockerJumpDuration,
                    RemapCurveZero = 1f,
                    RemapCurveOne = 1.14f,
                    DetermineScaleOnPlay = true,
                    AnimateCurve = new AnimationCurve(
                        new Keyframe(0f, 0f),
                        new Keyframe(0.3f, 1f),
                        new Keyframe(0.72f, 0.35f),
                        new Keyframe(1f, 0f))
                }
            };
            if (Application.isPlaying)
            {
                jumpFeedback.Initialization();
            }
        }

        private static float PlanarDistanceSquared(Vector3 left, Vector3 right)
        {
            var offset = left - right;
            offset.y = 0f;
            return offset.sqrMagnitude;
        }

        private static int PositiveModulo(int value, int divisor)
        {
            var result = value % divisor;
            return result < 0 ? result + divisor : result;
        }
    }
}
