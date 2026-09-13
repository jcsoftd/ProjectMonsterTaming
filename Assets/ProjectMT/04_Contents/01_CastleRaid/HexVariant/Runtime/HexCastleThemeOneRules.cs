using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;

namespace ProjectMT.Contents.CastleRaidHex
{
    public enum HexCastleThemeOneReadiness
    {
        [InspectorName("미리보기 초안")]
        PreviewDraft = 0,
        [InspectorName("외형 승인 · 수치 조정 중")]
        VisualApprovedBalancePending = 1,
        [InspectorName("스테이지 생성 가능")]
        StageReady = 2
    }

    public enum HexCastleBuildingRole
    {
        [InspectorName("없음")]
        None = 0,
        [InspectorName("기사 병영")]
        KnightBarracks = 1,
        [InspectorName("농부 병영")]
        FarmerBarracks = 2,
        [InspectorName("포탑")]
        Turret = 3,
        [InspectorName("연습장")]
        TrainingYard = 4,
        [InspectorName("교회")]
        Church = 5,
        [InspectorName("골드 건물")]
        GoldStorage = 6,
        [InspectorName("장비 건물")]
        EquipmentForge = 7,
        [InspectorName("열쇠 건물")]
        KeyVault = 8,
        [InspectorName("일반 길막 건물")]
        Blocker = 9
    }

    public enum HexCastlePlacementDensity
    {
        [InspectorName("없음")]
        None = 0,
        [InspectorName("밀집")]
        Dense = 1,
        [InspectorName("분산")]
        Sparse = 2
    }

    public enum HexCastleTurretWeaponKind
    {
        [InspectorName("없음")]
        None = 0,
        [InspectorName("대포")]
        Cannon = 1,
        [InspectorName("발리스타")]
        Ballista = 2,
        [InspectorName("화염구")]
        Fireball = 3
    }

    [Serializable]
    public sealed class HexCastleLayerBuildingQuota
    {
        [SerializeField, InspectorName("방어선 수"), Range(2, 4)] private int defenseLayerCount = 2;
        [SerializeField, InspectorName("기사 병영 수"), Min(0)] private int knightBarracksCount = 1;
        [SerializeField, InspectorName("농부 병영 수"), Min(0)] private int farmerBarracksCount = 1;
        [SerializeField, InspectorName("포탑 수"), Min(0)] private int turretCount = 2;
        [SerializeField, InspectorName("연습장 수"), Min(0)] private int trainingYardCount = 1;
        [SerializeField, InspectorName("교회 수"), Min(0)] private int churchCount = 1;
        [SerializeField, InspectorName("일반 길막 최소 등급합"), Min(0)] private int minimumBlockerGradeSum = 3;
        [SerializeField, InspectorName("후속 함정 수 · 현재 미생성"), Min(0)] private int futureTrapCount;
        [SerializeField, InspectorName("후속 초기 수비대 수 · 현재 미생성"), Min(0)] private int futureInitialDefenderCount;

        public int DefenseLayerCount => defenseLayerCount;
        public int KnightBarracksCount => knightBarracksCount;
        public int FarmerBarracksCount => farmerBarracksCount;
        public int TurretCount => turretCount;
        public int TrainingYardCount => trainingYardCount;
        public int ChurchCount => churchCount;
        public int MinimumBlockerGradeSum => minimumBlockerGradeSum;
        public int FutureTrapCount => Mathf.Max(0, futureTrapCount);
        public int FutureInitialDefenderCount => Mathf.Max(0, futureInitialDefenderCount);
        public int RequiredSpecialCount =>
            knightBarracksCount + farmerBarracksCount + turretCount + trainingYardCount + churchCount;

        internal static HexCastleLayerBuildingQuota Create(
            int layers,
            int knightBarracks,
            int farmerBarracks,
            int turrets,
            int trainingYards,
            int churches,
            int minimumBlockerGrade,
            int traps = 0,
            int initialDefenders = 0)
        {
            return new HexCastleLayerBuildingQuota
            {
                defenseLayerCount = layers,
                knightBarracksCount = knightBarracks,
                farmerBarracksCount = farmerBarracks,
                turretCount = turrets,
                trainingYardCount = trainingYards,
                churchCount = churches,
                minimumBlockerGradeSum = minimumBlockerGrade,
                futureTrapCount = traps,
                futureInitialDefenderCount = initialDefenders
            };
        }
    }

    [Serializable]
    public sealed class HexCastleBlockerVariantRule
    {
        [SerializeField, InspectorName("규칙 식별자")] private string id = "HomeA";
        [SerializeField, InspectorName("외형 프리팹 식별자")] private string visualVariantId = "building_home_A_blue";
        [SerializeField, InspectorName("등급"), Range(1, 5)] private int grade = 1;
        [SerializeField, InspectorName("체력"), Min(1f)] private float health = 120f;

        public string Id => id ?? string.Empty;
        public string VisualVariantId => visualVariantId ?? string.Empty;
        public int Grade => Mathf.Max(1, grade);
        public float Health => Mathf.Max(1f, health);

        internal static HexCastleBlockerVariantRule Create(
            string ruleId,
            string visualId,
            int buildingGrade,
            float hitPoints)
        {
            return new HexCastleBlockerVariantRule
            {
                id = ruleId,
                visualVariantId = visualId,
                grade = buildingGrade,
                health = hitPoints
            };
        }
    }

    [Serializable]
    public sealed class HexCastleBuildingGradeRule
    {
        [SerializeField, InspectorName("건물 종류")] private HexCastleBuildingRole role = HexCastleBuildingRole.KnightBarracks;
        [SerializeField, InspectorName("등급"), Range(1, 5)] private int grade = 1;

        public HexCastleBuildingRole Role => role;
        public int Grade => Mathf.Clamp(grade, 1, 5);

        internal static HexCastleBuildingGradeRule Create(
            HexCastleBuildingRole buildingRole,
            int buildingGrade)
        {
            return new HexCastleBuildingGradeRule
            {
                role = buildingRole,
                grade = buildingGrade
            };
        }
    }

    [Serializable]
    public sealed class HexCastleTurretBandLevelRule
    {
        [SerializeField, InspectorName("방어선 수"), Range(2, 4)] private int defenseLayerCount = 2;
        [SerializeField, InspectorName("첫 번째 구간 레벨"), Range(1, 3)] private int firstBandLevel = 1;
        [SerializeField, InspectorName("두 번째 구간 레벨"), Range(1, 3)] private int secondBandLevel = 1;
        [SerializeField, InspectorName("세 번째 구간 레벨"), Range(1, 3)] private int thirdBandLevel = 1;

        public int DefenseLayerCount => Mathf.Clamp(defenseLayerCount, 2, 4);

        public int ResolveLevel(int bandIndex)
        {
            switch (Mathf.Clamp(bandIndex, 0, 2))
            {
                case 0: return Mathf.Clamp(firstBandLevel, 1, 3);
                case 1: return Mathf.Clamp(secondBandLevel, 1, 3);
                default: return Mathf.Clamp(thirdBandLevel, 1, 3);
            }
        }

        internal static HexCastleTurretBandLevelRule Create(
            int layers,
            int firstLevel,
            int secondLevel,
            int thirdLevel)
        {
            return new HexCastleTurretBandLevelRule
            {
                defenseLayerCount = layers,
                firstBandLevel = firstLevel,
                secondBandLevel = secondLevel,
                thirdBandLevel = thirdLevel
            };
        }
    }

    [Serializable]
    public sealed class HexCastleThemeOneTuning
    {
        public const int CurrentDraftVersion = 8;

        [SerializeField, InspectorName("초안 형식 버전")] private int draftVersion = CurrentDraftVersion;
        [SerializeField, InspectorName("중벽 바로 바깥 첫 열 점유율"), Range(0.05f, 1f)] private float denseOccupancy = 1f;
        [SerializeField, InspectorName("그 다음 둘째 열 점유율"), Range(0.05f, 1f)] private float sparseOccupancy = 0.28f;
        [SerializeField, InspectorName("왕궁 칸 체력"), Min(1f)] private float palaceHealth = 700f;
        [SerializeField, InspectorName("왕궁 보상값"), Min(0)] private int palaceRewardValue = 500;
        [SerializeField, InspectorName("성벽 1단계 체력"), Min(1f)] private float wallTier1Health = 100f;
        [SerializeField, InspectorName("성벽 2단계 체력"), Min(1f)] private float wallTier2Health = 180f;
        [SerializeField, InspectorName("성벽 3단계 체력"), Min(1f)] private float wallTier3Health = 300f;
        [FormerlySerializedAs("closedOuterGateHealthMultiplier")]
        [SerializeField, InspectorName("닫힌 성문 체력 배율"), Range(0.5f, 0.95f)] private float closedGateHealthMultiplier = 0.8f;
        [FormerlySerializedAs("closedOuterGateCount")]
        [SerializeField, InspectorName("성벽 둘레당 닫힌 성문 수"), Range(1, 12)] private int closedGateCountPerWallRing = 2;
        [SerializeField, InspectorName("한 면의 닫힌 성문 최대"), Range(1, 2)] private int closedGateMaximumPerFace = 2;
        [SerializeField, InspectorName("격벽 구간당 열린 성문 보장 수"), Range(1, 2)] private int openPartitionGateCountPerBand = 1;
        [SerializeField, InspectorName("열린 성문 한 개 추가 확률"), Range(0f, 1f)] private float openPartitionAdditionalGateChance = 0.8f;
        [SerializeField, InspectorName("격벽 구간당 열린 성문 최대"), Range(1, 2)] private int openPartitionGateMaximumPerBand = 2;
        [SerializeField, InspectorName("보상 건물 체력"), Min(1f)] private float rewardBuildingHealth = 160f;
        [SerializeField, InspectorName("특수 건물 체력"), Min(1f)] private float specialBuildingHealth = 180f;
        [SerializeField, InspectorName("포탑 건물 체력"), Min(1f)] private float defenseBuildingHealth = 220f;
        [SerializeField, InspectorName("골드 건물 보상"), Min(0)] private int goldRewardValue = 30;
        [SerializeField, InspectorName("장비 건물 보상"), Min(0)] private int equipmentRewardValue = 60;
        [SerializeField, InspectorName("열쇠 건물 보상"), Min(0)] private int keyRewardValue = 30;
        [SerializeField, InspectorName("기사 생산 시간"), Min(0.1f)] private float knightRefillInterval = 10f;
        [SerializeField, InspectorName("기사 지역 반경 칸 수"), Min(1)] private int knightSearchRadius = 10;
        [FormerlySerializedAs("knightRefillThreshold")]
        [SerializeField, InspectorName("기사 지역 최대 수"), Min(1)] private int knightMaximumNearbyCount = 8;
        [SerializeField, InspectorName("한 번에 생산할 기사 수"), Min(1)] private int knightsPerRefill = 1;
        [SerializeField, InspectorName("농부 생산 시간"), Min(0.1f)] private float farmerSpawnInterval = 30f;
        [SerializeField, InspectorName("농부 지역 반경 칸 수"), Min(1)] private int farmerSearchRadius = 10;
        [SerializeField, InspectorName("농부 지역 최대 수"), Min(1)] private int farmerMaximumNearbyCount = 4;
        [SerializeField, InspectorName("한 번에 소환할 농부 수"), Min(1)] private int farmersPerSpawn = 1;
        [SerializeField, InspectorName("기사 체력"), Min(1f)] private float knightHealth = 180f;
        [SerializeField, InspectorName("기사 공격력"), Min(0f)] private float knightAttackDamage = 18f;
        [SerializeField, InspectorName("기사 공격 간격"), Min(0.1f)] private float knightAttackInterval = 1.1f;
        [SerializeField, InspectorName("기사 이동속도"), Min(0.1f)] private float knightMoveSpeed = 2.2f;
        [SerializeField, InspectorName("기사 탐지 반경 칸 수"), Min(1)] private int knightDetectionRangeCells = 4;
        [SerializeField, InspectorName("기사 추격 한계 칸 수"), Min(1)] private int knightLeashRangeCells = 7;
        [SerializeField, InspectorName("농부 체력"), Min(1f)] private float farmerHealth = 90f;
        [SerializeField, InspectorName("농부 공격력"), Min(0f)] private float farmerAttackDamage = 8f;
        [SerializeField, InspectorName("농부 공격 간격"), Min(0.1f)] private float farmerAttackInterval = 1.4f;
        [SerializeField, InspectorName("농부 이동속도"), Min(0.1f)] private float farmerMoveSpeed = 2.2f;
        [SerializeField, InspectorName("농부 탐지 반경 칸 수"), Min(1)] private int farmerDetectionRangeCells = 3;
        [SerializeField, InspectorName("농부 추격 한계 칸 수"), Min(1)] private int farmerLeashRangeCells = 5;
        [SerializeField, InspectorName("수비대 순찰 반경 칸 수"), Range(1, 4)] private int garrisonPatrolRadiusCells = 2;
        [SerializeField, InspectorName("수비대 적 탐색 최소 주기"), Min(0.1f)] private float garrisonMinimumTargetSearchInterval = 1f;
        [SerializeField, InspectorName("수비대 적 탐색 최대 주기"), Min(0.1f)] private float garrisonMaximumTargetSearchInterval = 2f;
        [SerializeField, InspectorName("수비대 최소 반응 지연"), Min(0f)] private float garrisonMinimumResponseDelay = 0.4f;
        [SerializeField, InspectorName("수비대 최대 반응 지연"), Min(0f)] private float garrisonMaximumResponseDelay = 1.5f;
        [SerializeField, InspectorName("동일 침입자 최대 대응 인원"), Range(1, 6)] private int garrisonMaximumRespondersPerTarget = 4;
        [SerializeField, InspectorName("피격 건물 경보 유지 시간"), Min(0.1f)] private float garrisonStructureAlertSeconds = 4f;
        [SerializeField, InspectorName("기사 1칸 점프 시간"), Min(0.1f)] private float knightBlockerJumpDuration = 0.42f;
        [SerializeField, InspectorName("기사 1칸 점프 높이"), Min(0.1f)] private float knightBlockerJumpHeight = 0.48f;
        [SerializeField, InspectorName("연습장 공격력 배율"), Min(1f)] private float trainingAttackMultiplier = 1.10f;
        [SerializeField, InspectorName("교회 파괴 시 이동속도 배율"), Min(1f)] private float churchRageMoveSpeedMultiplier = 1.20f;
        [SerializeField, InspectorName("병영 최소 배치 방어선"), Range(2, 3)] private int minimumBarracksDefenseLayer = 2;
        [SerializeField, InspectorName("병영 인접 빈 칸 최소"), Range(2, 6)] private int minimumBarracksOpenNeighbors = 2;
        [SerializeField, InspectorName("병영 선호 최소 간격"), Range(3, 10)] private int preferredBarracksSeparationCells = 6;
        [SerializeField, InspectorName("병영 최소 허용 간격"), Range(2, 6)] private int minimumBarracksSeparationCells = 4;
        [SerializeField, InspectorName("첫 성벽 바깥 포탑 우선 비율"), Range(0.5f, 1f)] private float innerBandTurretShare = 0.6666667f;
        [SerializeField, InspectorName("포탑 최소 사거리 칸 수"), Range(2, 4)] private int turretMinimumRangeCells = 2;
        [SerializeField, InspectorName("포탑 최대 사거리 칸 수"), Range(2, 4)] private int turretMaximumRangeCells = 4;
        [SerializeField, InspectorName("성벽 넘어 공격 허용")] private bool turretsCanAttackAcrossWalls = true;
        [SerializeField, InspectorName("대포 최대 레벨"), Range(1, 3)] private int cannonMaximumLevel = 2;
        [SerializeField, InspectorName("발리스타 최대 레벨"), Range(1, 3)] private int ballistaMaximumLevel = 2;
        [SerializeField, InspectorName("화염구 최대 레벨"), Range(1, 3)] private int fireballMaximumLevel = 3;
        [SerializeField, InspectorName("방어선별 건물 생성 수량")] private List<HexCastleLayerBuildingQuota> layerQuotas =
            new List<HexCastleLayerBuildingQuota>();
        [SerializeField, InspectorName("일반 길막 건물 후보")] private List<HexCastleBlockerVariantRule> blockerVariants =
            new List<HexCastleBlockerVariantRule>();
        [SerializeField, InspectorName("특수·보상 건물 등급")] private List<HexCastleBuildingGradeRule> fixedBuildingGrades =
            new List<HexCastleBuildingGradeRule>();
        [SerializeField, InspectorName("포탑 무기 순환")] private List<HexCastleTurretWeaponKind> turretWeaponCycle =
            new List<HexCastleTurretWeaponKind>();
        [SerializeField, InspectorName("방어선별 포탑 구간 레벨")] private List<HexCastleTurretBandLevelRule> turretBandLevels =
            new List<HexCastleTurretBandLevelRule>();

        public int DraftVersion => draftVersion;
        public float DenseOccupancy => Mathf.Clamp01(denseOccupancy);
        public float SparseOccupancy => Mathf.Clamp01(sparseOccupancy);
        public float PalaceHealth => Mathf.Max(1f, palaceHealth);
        public int PalaceRewardValue => Mathf.Max(0, palaceRewardValue);
        public float WallTier1Health => Mathf.Max(1f, wallTier1Health);
        public float WallTier2Health => Mathf.Max(1f, wallTier2Health);
        public float WallTier3Health => Mathf.Max(1f, wallTier3Health);
        public float ClosedGateHealthMultiplier =>
            Mathf.Clamp(closedGateHealthMultiplier, 0.5f, 0.95f);
        public int ClosedGateMaximumPerFace => Mathf.Clamp(closedGateMaximumPerFace, 1, 2);
        public int ClosedGateCountPerWallRing =>
            Mathf.Clamp(closedGateCountPerWallRing, 1, ClosedGateMaximumPerFace * 6);
        public int OpenPartitionGateCountPerBand =>
            Mathf.Clamp(openPartitionGateCountPerBand, 1, OpenPartitionGateMaximumPerBand);
        public float OpenPartitionAdditionalGateChance =>
            Mathf.Clamp01(openPartitionAdditionalGateChance);
        public int OpenPartitionGateMaximumPerBand =>
            Mathf.Clamp(openPartitionGateMaximumPerBand, 1, 2);
        public float RewardBuildingHealth => Mathf.Max(1f, rewardBuildingHealth);
        public float SpecialBuildingHealth => Mathf.Max(1f, specialBuildingHealth);
        public float DefenseBuildingHealth => Mathf.Max(1f, defenseBuildingHealth);
        public int GoldRewardValue => Mathf.Max(0, goldRewardValue);
        public int EquipmentRewardValue => Mathf.Max(0, equipmentRewardValue);
        public int KeyRewardValue => Mathf.Max(0, keyRewardValue);
        public float KnightRefillInterval => Mathf.Max(0.1f, knightRefillInterval);
        public int KnightSearchRadius => Mathf.Max(1, knightSearchRadius);
        public int KnightMaximumNearbyCount => Mathf.Max(1, knightMaximumNearbyCount);
        public int KnightsPerRefill => Mathf.Max(1, knightsPerRefill);
        public float FarmerSpawnInterval => Mathf.Max(0.1f, farmerSpawnInterval);
        public int FarmerSearchRadius => Mathf.Max(1, farmerSearchRadius);
        public int FarmerMaximumNearbyCount => Mathf.Max(1, farmerMaximumNearbyCount);
        public int FarmersPerSpawn => Mathf.Max(1, farmersPerSpawn);
        public float KnightHealth => knightHealth > 0f ? knightHealth : 180f;
        public float KnightAttackDamage => knightAttackDamage > 0f ? knightAttackDamage : 18f;
        public float KnightAttackInterval => knightAttackInterval > 0f ? knightAttackInterval : 1.1f;
        public float KnightMoveSpeed => knightMoveSpeed > 0f ? knightMoveSpeed : 2.2f;
        public int KnightDetectionRangeCells => knightDetectionRangeCells > 0
            ? knightDetectionRangeCells
            : 4;
        public int KnightLeashRangeCells => Mathf.Max(
            KnightDetectionRangeCells,
            knightLeashRangeCells > 0 ? knightLeashRangeCells : 7);
        public float FarmerHealth => farmerHealth > 0f ? farmerHealth : 90f;
        public float FarmerAttackDamage => farmerAttackDamage > 0f ? farmerAttackDamage : 8f;
        public float FarmerAttackInterval => farmerAttackInterval > 0f ? farmerAttackInterval : 1.4f;
        public float FarmerMoveSpeed => farmerMoveSpeed > 0f ? farmerMoveSpeed : 2.2f;
        public int FarmerDetectionRangeCells => farmerDetectionRangeCells > 0
            ? farmerDetectionRangeCells
            : 3;
        public int FarmerLeashRangeCells => Mathf.Max(
            FarmerDetectionRangeCells,
            farmerLeashRangeCells > 0 ? farmerLeashRangeCells : 5);
        public int GarrisonPatrolRadiusCells => garrisonPatrolRadiusCells > 0
            ? Mathf.Clamp(garrisonPatrolRadiusCells, 1, 4)
            : 2;
        public float GarrisonMinimumTargetSearchInterval =>
            Mathf.Max(0.1f, garrisonMinimumTargetSearchInterval);
        public float GarrisonMaximumTargetSearchInterval => Mathf.Max(
            GarrisonMinimumTargetSearchInterval,
            garrisonMaximumTargetSearchInterval);
        public float GarrisonMinimumResponseDelay => Mathf.Max(0f, garrisonMinimumResponseDelay);
        public float GarrisonMaximumResponseDelay => Mathf.Max(
            GarrisonMinimumResponseDelay,
            garrisonMaximumResponseDelay);
        public int GarrisonMaximumRespondersPerTarget => Mathf.Clamp(garrisonMaximumRespondersPerTarget, 1, 6);
        public float GarrisonStructureAlertSeconds => Mathf.Max(0.1f, garrisonStructureAlertSeconds);
        public float KnightBlockerJumpDuration => Mathf.Max(0.1f, knightBlockerJumpDuration);
        public float KnightBlockerJumpHeight => Mathf.Max(0.1f, knightBlockerJumpHeight);
        public float TrainingAttackMultiplier => Mathf.Max(1f, trainingAttackMultiplier);
        public float ChurchRageMoveSpeedMultiplier => Mathf.Max(1f, churchRageMoveSpeedMultiplier);
        public int MinimumBarracksDefenseLayer => Mathf.Clamp(minimumBarracksDefenseLayer, 2, 3);
        public int MinimumBarracksOpenNeighbors => Mathf.Clamp(minimumBarracksOpenNeighbors, 2, 6);
        public int PreferredBarracksSeparationCells => Mathf.Clamp(preferredBarracksSeparationCells, 3, 10);
        public int MinimumBarracksSeparationCells => Mathf.Clamp(
            minimumBarracksSeparationCells,
            2,
            PreferredBarracksSeparationCells);
        public int PalaceGuardBarracksCount => 1;
        public int PalaceGuardTurretCount => 2;
        public float InnerBandTurretShare => Mathf.Clamp(innerBandTurretShare, 0.5f, 1f);
        public int TurretMinimumRangeCells => Mathf.Clamp(turretMinimumRangeCells, 2, 4);
        public int TurretMaximumRangeCells => Mathf.Clamp(turretMaximumRangeCells, TurretMinimumRangeCells, 4);
        public bool TurretsCanAttackAcrossWalls => turretsCanAttackAcrossWalls;
        public IReadOnlyList<HexCastleLayerBuildingQuota> LayerQuotas => layerQuotas;
        public IReadOnlyList<HexCastleBlockerVariantRule> BlockerVariants => blockerVariants;
        public IReadOnlyList<HexCastleBuildingGradeRule> FixedBuildingGrades => fixedBuildingGrades;
        public IReadOnlyList<HexCastleTurretWeaponKind> TurretWeaponCycle => turretWeaponCycle;
        public IReadOnlyList<HexCastleTurretBandLevelRule> TurretBandLevels => turretBandLevels;

        public static HexCastleThemeOneTuning CreateDraftDefaults()
        {
            return new HexCastleThemeOneTuning
            {
                layerQuotas = new List<HexCastleLayerBuildingQuota>
                {
                    HexCastleLayerBuildingQuota.Create(2, 0, 0, 4, 1, 1, 3),
                    HexCastleLayerBuildingQuota.Create(3, 1, 1, 4, 1, 1, 12),
                    HexCastleLayerBuildingQuota.Create(4, 2, 2, 6, 2, 1, 24)
                },
                blockerVariants = new List<HexCastleBlockerVariantRule>
                {
                    HexCastleBlockerVariantRule.Create("StageB", "building_stage_B", 1, 100f),
                    HexCastleBlockerVariantRule.Create("StageC", "building_stage_C", 1, 110f),
                    HexCastleBlockerVariantRule.Create("HomeA", "building_home_A_blue", 1, 120f),
                    HexCastleBlockerVariantRule.Create("HomeB", "building_home_B_blue", 2, 170f),
                    HexCastleBlockerVariantRule.Create("Shrine", "building_shrine_blue", 2, 160f),
                    HexCastleBlockerVariantRule.Create("Townhall", "building_townhall_blue", 3, 230f),
                    HexCastleBlockerVariantRule.Create("Windmill", "building_windmill_blue", 3, 240f)
                },
                fixedBuildingGrades = new List<HexCastleBuildingGradeRule>
                {
                    HexCastleBuildingGradeRule.Create(HexCastleBuildingRole.KnightBarracks, 2),
                    HexCastleBuildingGradeRule.Create(HexCastleBuildingRole.FarmerBarracks, 1),
                    HexCastleBuildingGradeRule.Create(HexCastleBuildingRole.TrainingYard, 2),
                    HexCastleBuildingGradeRule.Create(HexCastleBuildingRole.Church, 2),
                    HexCastleBuildingGradeRule.Create(HexCastleBuildingRole.GoldStorage, 2),
                    HexCastleBuildingGradeRule.Create(HexCastleBuildingRole.EquipmentForge, 2),
                    HexCastleBuildingGradeRule.Create(HexCastleBuildingRole.KeyVault, 2)
                },
                turretWeaponCycle = new List<HexCastleTurretWeaponKind>
                {
                    HexCastleTurretWeaponKind.Cannon,
                    HexCastleTurretWeaponKind.Ballista,
                    HexCastleTurretWeaponKind.Fireball
                },
                turretBandLevels = new List<HexCastleTurretBandLevelRule>
                {
                    HexCastleTurretBandLevelRule.Create(2, 1, 1, 1),
                    HexCastleTurretBandLevelRule.Create(3, 2, 1, 1),
                    HexCastleTurretBandLevelRule.Create(4, 3, 2, 1)
                }
            };
        }

#if UNITY_EDITOR
        public void EditorApplyFormalizedGarrisonRules()
        {
            knightRefillInterval = 20f;
            knightSearchRadius = 10;
            knightMaximumNearbyCount = 8;
            knightsPerRefill = 1;
            farmerSpawnInterval = 20f;
            farmerSearchRadius = 10;
            farmerMaximumNearbyCount = 8;
            farmersPerSpawn = 1;
            knightMoveSpeed = 2.2f;
            garrisonMinimumTargetSearchInterval = 1f;
            garrisonMaximumTargetSearchInterval = 2f;
            garrisonMinimumResponseDelay = 0.4f;
            garrisonMaximumResponseDelay = 1.5f;
            garrisonMaximumRespondersPerTarget = 4;
            garrisonStructureAlertSeconds = 4f;
            knightBlockerJumpDuration = 0.42f;
            knightBlockerJumpHeight = 0.48f;
            preferredBarracksSeparationCells = 6;
            minimumBarracksSeparationCells = 4;
        }
#endif

        public int ResolveBuildingGrade(HexCastleBuildingRole role)
        {
            var result = fixedBuildingGrades?.FirstOrDefault(value =>
                value != null && value.Role == role);
            return result?.Grade ?? throw new InvalidOperationException(
                $"Theme 1 Draft Rules에 {role} 건물 등급이 없습니다.");
        }

        public HexCastleTurretWeaponKind ResolveTurretWeapon(int seed, int sequence)
        {
            if (turretWeaponCycle == null || turretWeaponCycle.Count == 0)
            {
                throw new InvalidOperationException("Theme 1 Draft Rules에 포탑 무기 순환표가 없습니다.");
            }

            var score = (long)seed + sequence;
            var index = (int)((score % turretWeaponCycle.Count + turretWeaponCycle.Count) %
                              turretWeaponCycle.Count);
            return turretWeaponCycle[index];
        }

        public int ResolveTurretLevel(
            int defenseLayerCount,
            int bandIndex,
            HexCastleTurretWeaponKind weaponKind)
        {
            var rule = turretBandLevels?.FirstOrDefault(value =>
                value != null && value.DefenseLayerCount == defenseLayerCount);
            if (rule == null)
            {
                throw new InvalidOperationException(
                    $"Theme 1 Draft Rules에 {defenseLayerCount}중벽 포탑 레벨표가 없습니다.");
            }

            return Mathf.Min(
                rule.ResolveLevel(bandIndex),
                ResolveTurretMaximumLevel(weaponKind));
        }

        public int ResolveTurretMaximumLevel(HexCastleTurretWeaponKind weaponKind)
        {
            switch (weaponKind)
            {
                case HexCastleTurretWeaponKind.Cannon:
                    return Mathf.Clamp(cannonMaximumLevel, 1, 2);
                case HexCastleTurretWeaponKind.Ballista:
                    return Mathf.Clamp(ballistaMaximumLevel, 1, 2);
                case HexCastleTurretWeaponKind.Fireball:
                    return Mathf.Clamp(fireballMaximumLevel, 1, 3);
                default:
                    return 0;
            }
        }

        public int ResolveTurretRangeCells(
            HexCastleTurretWeaponKind weaponKind,
            int turretLevel)
        {
            var maximumLevel = ResolveTurretMaximumLevel(weaponKind);
            if (maximumLevel == 0)
            {
                return 0;
            }

            var level = Mathf.Clamp(turretLevel, 1, maximumLevel);
            var range = weaponKind == HexCastleTurretWeaponKind.Cannon
                ? level + 1
                : weaponKind == HexCastleTurretWeaponKind.Ballista
                    ? level + 2
                    : weaponKind == HexCastleTurretWeaponKind.Fireball
                        ? level + 2
                        : 0;
            return range == 0
                ? 0
                : Mathf.Clamp(range, TurretMinimumRangeCells, TurretMaximumRangeCells);
        }

        public HexCastleLayerBuildingQuota ResolveLayerQuota(int defenseLayerCount)
        {
            var result = layerQuotas?.FirstOrDefault(value =>
                value != null && value.DefenseLayerCount == defenseLayerCount);
            return result ?? throw new InvalidOperationException(
                $"Theme 1 Draft Rules에 {defenseLayerCount}중벽 건물 수량표가 없습니다.");
        }

        public int ResolveRewardValue(HexCastleLootKind lootKind)
        {
            switch (lootKind)
            {
                case HexCastleLootKind.Gold: return GoldRewardValue;
                case HexCastleLootKind.Equipment: return EquipmentRewardValue;
                case HexCastleLootKind.Key: return KeyRewardValue;
                default: return 0;
            }
        }

        public float ResolveWallHealth(int wallTier)
        {
            switch (Mathf.Clamp(wallTier, 1, 3))
            {
                case 1: return WallTier1Health;
                case 2: return WallTier2Health;
                default: return WallTier3Health;
            }
        }

        public float ResolveClosedGateHealth(int wallTier)
        {
            return ResolveWallHealth(wallTier) * ClosedGateHealthMultiplier;
        }

        public void Validate(int defenseLayerCount)
        {
            if (!Mathf.Approximately(DenseOccupancy, 1f))
            {
                throw new InvalidOperationException(
                    "중벽 바로 바깥 첫 열은 필수 통로를 제외하고 100% 채워야 합니다.");
            }

            if (DenseOccupancy <= SparseOccupancy)
            {
                throw new InvalidOperationException("밀집 열 비율은 분산 열 비율보다 커야 합니다.");
            }

            if (TurretMinimumRangeCells > TurretMaximumRangeCells)
            {
                throw new InvalidOperationException("포탑 최소 사거리는 최대 사거리보다 크면 안 됩니다.");
            }

            if (!TurretsCanAttackAcrossWalls)
            {
                throw new InvalidOperationException("Theme 1 포탑은 성벽을 넘어 공격할 수 있어야 합니다.");
            }

            if (KnightSearchRadius != 10 || FarmerSearchRadius != 10 ||
                KnightMaximumNearbyCount != 8 || FarmerMaximumNearbyCount != 4 ||
                !Mathf.Approximately(KnightRefillInterval, 10f) ||
                !Mathf.Approximately(FarmerSpawnInterval, 30f) ||
                KnightsPerRefill != 1 || FarmersPerSpawn != 1)
            {
                throw new InvalidOperationException("기사 병영은 10칸 안 최대 8명·10초당 1명, 농부병영은 최대 4명·30초당 1명 생산 계약이어야 합니다.");
            }

            if (GarrisonMinimumTargetSearchInterval < 1f ||
                GarrisonMaximumTargetSearchInterval > 2f)
            {
                throw new InvalidOperationException("기사·농부의 새 적 탐색은 1~2초 주기여야 합니다.");
            }

            if (blockerVariants == null || blockerVariants.Count == 0 ||
                blockerVariants.Any(value => value == null || string.IsNullOrWhiteSpace(value.VisualVariantId)))
            {
                throw new InvalidOperationException("Theme 1 일반 길막 건물 후보가 없습니다.");
            }

            var fixedGradeRoles = new[]
            {
                HexCastleBuildingRole.KnightBarracks,
                HexCastleBuildingRole.FarmerBarracks,
                HexCastleBuildingRole.TrainingYard,
                HexCastleBuildingRole.Church,
                HexCastleBuildingRole.GoldStorage,
                HexCastleBuildingRole.EquipmentForge,
                HexCastleBuildingRole.KeyVault
            };
            if (fixedBuildingGrades == null ||
                fixedGradeRoles.Any(role => fixedBuildingGrades.Count(value =>
                    value != null && value.Role == role) != 1))
            {
                throw new InvalidOperationException("Theme 1 특수·보상 건물 등급표가 완전하지 않습니다.");
            }

            if (turretWeaponCycle == null || turretWeaponCycle.Count == 0 ||
                turretWeaponCycle.Any(value => value == HexCastleTurretWeaponKind.None))
            {
                throw new InvalidOperationException("Theme 1 포탑 무기 순환표가 비어 있거나 None을 포함합니다.");
            }

            if (turretBandLevels == null ||
                Enumerable.Range(2, 3).Any(layers => turretBandLevels.Count(value =>
                    value != null && value.DefenseLayerCount == layers) != 1))
            {
                throw new InvalidOperationException("Theme 1 2·3·4중벽 포탑 Band 레벨표가 완전하지 않습니다.");
            }

            var quota = ResolveLayerQuota(defenseLayerCount);
            if (quota.RequiredSpecialCount < 1)
            {
                throw new InvalidOperationException("Theme 1 특수 건물 수량표가 비어 있습니다.");
            }
        }
    }

    [CreateAssetMenu(
        fileName = "HexCastleTheme1Rules",
        menuName = "ProjectMT/Castle Raid Hex/Theme 1 Rules")]
    public sealed class HexCastleThemeOneRules : ScriptableObject
    {
        [SerializeField, InspectorName("정식화 상태")] private HexCastleThemeOneReadiness readiness =
            HexCastleThemeOneReadiness.PreviewDraft;
        [SerializeField, InspectorName("테마 1 생성 규칙")] private HexCastleThemeOneTuning tuning =
            HexCastleThemeOneTuning.CreateDraftDefaults();

        public HexCastleThemeOneReadiness Readiness => readiness;
        public bool IsVisualApproved => readiness >= HexCastleThemeOneReadiness.VisualApprovedBalancePending;
        public bool CanApproveStageLayout => readiness == HexCastleThemeOneReadiness.StageReady;
        public HexCastleThemeOneTuning Tuning =>
            tuning ?? (tuning = HexCastleThemeOneTuning.CreateDraftDefaults());

        public void ResetToDraftDefaults()
        {
            readiness = HexCastleThemeOneReadiness.PreviewDraft;
            tuning = HexCastleThemeOneTuning.CreateDraftDefaults();
        }

#if UNITY_EDITOR
        public void EditorSetReadiness(HexCastleThemeOneReadiness value)
        {
            readiness = value;
        }
#endif
    }
}
