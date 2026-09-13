using System;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex
{
    public sealed class HexCastleDifficultyProfile // 난이도 버튼 하나가 절차 생성 전투 밀도를 모두 결정한다
    {
        private HexCastleDifficultyProfile(
            int level,
            int defenseLayerCount,
            int closedGateCountPerWallRing,
            float openPartitionAdditionalGateChance,
            int knightBarracksCount,
            int farmerBarracksCount,
            int turretCount,
            int trainingYardCount,
            int churchCount,
            int minimumBlockerGradeSum,
            int goldStorageCount,
            int equipmentForgeCount,
            int keyVaultCount,
            int initialKnightCount,
            int initialFarmerCount,
            int knightBarracksGrade,
            int farmerBarracksGrade,
            int snareTrapCount,
            int spikePlateTrapCount,
            int blastMineCount)
        {
            Level = Mathf.Clamp(level, 1, 10);
            DefenseLayerCount = Mathf.Clamp(defenseLayerCount, 2, 4);
            ClosedGateCountPerWallRing = Mathf.Clamp(closedGateCountPerWallRing, 1, 12);
            OpenPartitionAdditionalGateChance = Mathf.Clamp01(openPartitionAdditionalGateChance);
            KnightBarracksCount = Mathf.Max(0, knightBarracksCount);
            FarmerBarracksCount = Mathf.Max(0, farmerBarracksCount);
            TurretCount = Mathf.Max(0, turretCount);
            TrainingYardCount = Mathf.Max(0, trainingYardCount);
            ChurchCount = Mathf.Max(0, churchCount);
            MinimumBlockerGradeSum = Mathf.Max(0, minimumBlockerGradeSum);
            GoldStorageCount = Mathf.Max(1, goldStorageCount);
            EquipmentForgeCount = Mathf.Max(1, equipmentForgeCount);
            KeyVaultCount = Mathf.Max(1, keyVaultCount);
            InitialKnightCount = Mathf.Max(0, initialKnightCount);
            InitialFarmerCount = Mathf.Max(0, initialFarmerCount);
            KnightBarracksGrade = Mathf.Clamp(knightBarracksGrade, 2, 5);
            FarmerBarracksGrade = Mathf.Clamp(farmerBarracksGrade, 1, KnightBarracksGrade - 1);
            SnareTrapCount = Mathf.Max(0, snareTrapCount);
            SpikePlateTrapCount = Mathf.Max(0, spikePlateTrapCount);
            BlastMineCount = Mathf.Max(0, blastMineCount);

            var step = Level - 1;
            FarmerHealthMultiplier = 1f + step * 0.07f;
            FarmerAttackMultiplier = 1f + step * 0.06f;
            KnightHealthMultiplier = 1f + step * 0.08f;
            KnightAttackMultiplier = 1f + step * 0.07f;
        }

        public int Level { get; }
        public int DefenseLayerCount { get; }
        public int ClosedGateCountPerWallRing { get; }
        public int OpenPartitionGateCountPerBand => 1;
        public int OpenPartitionGateMaximumPerBand => 2;
        public float OpenPartitionAdditionalGateChance { get; }
        public int KnightBarracksCount { get; }
        public int FarmerBarracksCount { get; }
        public int TurretCount { get; }
        public int TrainingYardCount { get; }
        public int ChurchCount { get; }
        public int MinimumBlockerGradeSum { get; }
        public int GoldStorageCount { get; }
        public int EquipmentForgeCount { get; }
        public int KeyVaultCount { get; }
        public int InitialKnightCount { get; }
        public int InitialFarmerCount { get; }
        public int KnightBarracksGrade { get; }
        public int FarmerBarracksGrade { get; }
        public int SnareTrapCount { get; }
        public int SpikePlateTrapCount { get; }
        public int BlastMineCount { get; }
        public float FarmerHealthMultiplier { get; }
        public float FarmerAttackMultiplier { get; }
        public float KnightHealthMultiplier { get; }
        public float KnightAttackMultiplier { get; }
        public int RewardBuildingCount => GoldStorageCount + EquipmentForgeCount + KeyVaultCount;
        public int TotalTrapCount => SnareTrapCount + SpikePlateTrapCount + BlastMineCount;
        public int RequiredSpecialCount =>
            KnightBarracksCount + FarmerBarracksCount + TurretCount +
            TrainingYardCount + ChurchCount + RewardBuildingCount;

        public int ResolveBuildingGrade(HexCastleBuildingRole role, int fallbackGrade)
        {
            switch (role)
            {
                case HexCastleBuildingRole.KnightBarracks:
                    return KnightBarracksGrade;
                case HexCastleBuildingRole.FarmerBarracks:
                    return FarmerBarracksGrade;
                default:
                    return Mathf.Clamp(fallbackGrade, 1, 5);
            }
        }

        public float ResolveHealthMultiplier(HexCastleGarrisonUnitRole role)
        {
            return role == HexCastleGarrisonUnitRole.Knight
                ? KnightHealthMultiplier
                : FarmerHealthMultiplier;
        }

        public float ResolveAttackMultiplier(HexCastleGarrisonUnitRole role)
        {
            return role == HexCastleGarrisonUnitRole.Knight
                ? KnightAttackMultiplier
                : FarmerAttackMultiplier;
        }

        public static int ResolveDefenseLayerCount(int level, int seed)
        {
            level = Mathf.Clamp(level, 1, 10);
            if (level <= 3)
            {
                return 2;
            }

            if (level == 4)
            {
                return 3;
            }

            if (level <= 6)
            {
                return PositiveModulo(unchecked(seed * 397 + level * 31), 2) == 0 ? 3 : 4;
            }

            return 4;
        }

        public static HexCastleDifficultyProfile Resolve(int level, int seed)
        {
            level = Mathf.Clamp(level, 1, 10);
            var layers = ResolveDefenseLayerCount(level, seed);
            switch (level)
            {
                case 1: return Create(1, layers, 2, 0f, 0, 0, 3, 1, 0, 3, 1, 1, 1, 1, 1, 2, 1, 2, 1, 1);
                case 2: return Create(2, layers, 2, 0f, 0, 0, 4, 1, 0, 3, 2, 1, 1, 1, 2, 2, 1, 2, 2, 1);
                case 3: return Create(3, layers, 3, 0f, 0, 0, 5, 1, 0, 0, 2, 1, 1, 2, 2, 2, 1, 2, 4, 1);
                case 4: return Create(4, layers, 3, 0.40f, 1, 1, 6, 1, 1, 12, 2, 2, 1, 2, 2, 2, 1, 4, 2, 2);
                case 5: return Create(5, layers, 3, 0.70f, 1, 1, 7, 1, 2, 15, 2, 2, 2, 3, 3, 3, 1, 4, 4, 2);
                case 6: return Create(6, layers, 4, 0.85f, 1, 2, 8, 2, 2, 18, 3, 2, 2, 3, 3, 3, 1, 4, 4, 4);
                case 7: return Create(7, layers, 4, 1f, 2, 2, 9, 2, 2, 21, 3, 3, 2, 4, 4, 3, 2, 4, 6, 4);
                case 8: return Create(8, layers, 4, 1f, 2, 2, 10, 2, 2, 24, 3, 3, 3, 4, 4, 3, 2, 6, 6, 4);
                case 9: return Create(9, layers, 5, 1f, 3, 2, 11, 2, 2, 28, 4, 3, 3, 5, 5, 3, 2, 6, 8, 6);
                default: return Create(10, layers, 6, 1f, 3, 3, 12, 2, 2, 32, 4, 4, 4, 6, 6, 3, 2, 8, 8, 8);
            }
        }

        private static HexCastleDifficultyProfile Create(
            int level,
            int layers,
            int closedGates,
            float openGateChance,
            int knightBarracks,
            int farmerBarracks,
            int turrets,
            int trainingYards,
            int churches,
            int blockerGrade,
            int gold,
            int equipment,
            int keys,
            int initialKnights,
            int initialFarmers,
            int knightGrade,
            int farmerGrade,
            int snareTraps,
            int spikePlateTraps,
            int blastMines)
        {
            return new HexCastleDifficultyProfile(
                level,
                layers,
                closedGates,
                openGateChance,
                knightBarracks,
                farmerBarracks,
                turrets,
                trainingYards,
                churches,
                blockerGrade,
                gold,
                equipment,
                keys,
                initialKnights,
                initialFarmers,
                knightGrade,
                farmerGrade,
                snareTraps,
                spikePlateTraps,
                blastMines);
        }

        private static int PositiveModulo(int value, int divisor)
        {
            var result = value % divisor;
            return result < 0 ? result + divisor : result;
        }
    }
}
