using System.Collections.Generic;
using NUnit.Framework;
using ProjectMT.Shared.Unit;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Tests
{
    public sealed class HexCastleBarracksRuntimeTests
    {
        private readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                if (owned[index] != null)
                {
                    Object.DestroyImmediate(owned[index]);
                }
            }

            owned.Clear();
        }

        [Test]
        public void KnightBarracks_ProducesOneEveryTenSecondsUntilLocalEightCap()
        {
            var setup = CreateSetup(HexCastleBuildingRole.KnightBarracks);
            var tuning = HexCastleThemeOneTuning.CreateDraftDefaults();
            Assert.That(tuning.KnightRefillInterval, Is.EqualTo(10f));
            Assert.That(tuning.KnightMaximumNearbyCount, Is.EqualTo(8));
            setup.Barracks.Configure(setup.Structure, setup.World, tuning);

            setup.Barracks.Tick(tuning.KnightRefillInterval);
            Assert.That(setup.World.CountAlive(
                HexCastleGarrisonUnitRole.Knight,
                setup.Structure.Coordinates,
                tuning.KnightSearchRadius), Is.EqualTo(1));

            for (var index = 1; index < tuning.KnightMaximumNearbyCount; index++)
            {
                setup.Barracks.Tick(tuning.KnightRefillInterval);
            }

            Assert.That(setup.World.CountAlive(
                HexCastleGarrisonUnitRole.Knight,
                setup.Structure.Coordinates,
                tuning.KnightSearchRadius), Is.EqualTo(tuning.KnightMaximumNearbyCount));

            setup.Barracks.Tick(tuning.KnightRefillInterval);
            Assert.That(setup.Barracks.TotalSpawned, Is.EqualTo(tuning.KnightMaximumNearbyCount));
            Assert.That(setup.Barracks.IsProducing, Is.False);
        }

        [Test]
        public void FarmerBarracks_ProducesEveryThirtySecondsUntilLocalFourCap()
        {
            var setup = CreateSetup(HexCastleBuildingRole.FarmerBarracks);
            var tuning = HexCastleThemeOneTuning.CreateDraftDefaults();
            Assert.That(tuning.FarmerSpawnInterval, Is.EqualTo(30f));
            Assert.That(tuning.FarmerMaximumNearbyCount, Is.EqualTo(4));
            setup.Barracks.Configure(setup.Structure, setup.World, tuning);

            for (var index = 0; index < tuning.FarmerMaximumNearbyCount + 2; index++)
            {
                setup.Barracks.Tick(tuning.FarmerSpawnInterval);
            }

            Assert.That(setup.World.CountAlive(
                HexCastleGarrisonUnitRole.Farmer,
                setup.Structure.Coordinates,
                tuning.FarmerSearchRadius), Is.EqualTo(tuning.FarmerMaximumNearbyCount));
            Assert.That(setup.Barracks.TotalSpawned, Is.EqualTo(tuning.FarmerMaximumNearbyCount));
            Assert.That(setup.Barracks.IsProducing, Is.False);
        }

        [Test]
        public void DestroyedBarracks_StopsSpawning()
        {
            var setup = CreateSetup(HexCastleBuildingRole.KnightBarracks);
            var tuning = HexCastleThemeOneTuning.CreateDraftDefaults();
            setup.Barracks.Configure(setup.Structure, setup.World, tuning);
            setup.Structure.ApplyDamage(setup.Structure.MaxHealth, Vector3.zero);

            setup.Barracks.Tick(tuning.KnightRefillInterval * 3f);

            Assert.That(setup.Barracks.IsRunning, Is.False);
            Assert.That(setup.World.AliveUnitCount, Is.Zero);
        }

        [Test]
        public void GarrisonCatalog_ResolvesKnightAndFarmerRolesSeparately()
        {
            var firstKnight = Own(new GameObject("PF_Enemy_Knight_T1"));
            var secondKnight = Own(new GameObject("PF_Enemy_Knight_T2"));
            var farmer = Own(new GameObject("PF_Enemy_Peasant"));
            var catalog = Own(ScriptableObject.CreateInstance<HexCastleGarrisonCatalog>());
            catalog.EditorConfigure(new[] { firstKnight, secondKnight }, farmer);

            Assert.That(catalog.IsComplete, Is.True);
            Assert.That(catalog.ResolveKnight(10801, 0),
                Is.EqualTo(firstKnight).Or.EqualTo(secondKnight));
            Assert.That(catalog.ResolveKnight(10801, 1),
                Is.EqualTo(firstKnight).Or.EqualTo(secondKnight));
            Assert.That(catalog.ResolveFarmer(), Is.SameAs(farmer));
        }

        private BarracksSetup CreateSetup(HexCastleBuildingRole role)
        {
            var root = Own(new GameObject("GarrisonWorld"));
            var world = root.AddComponent<HexCastleGarrisonWorld>();
            var knightPrefab = Own(new GameObject("PF_Enemy_Knight_T1"));
            var farmerPrefab = Own(new GameObject("PF_Enemy_Peasant"));
            var catalog = Own(ScriptableObject.CreateInstance<HexCastleGarrisonCatalog>());
            catalog.EditorConfigure(new[] { knightPrefab }, farmerPrefab);

            var structure = CreateRuntime(CreateBarracksCell(role));
            var firstExit = CreateRuntime(new HexCastleCell(
                new HexCoordinates(1, 0),
                HexCastleCellKind.Ground,
                initialBlocked: false));
            var secondExit = CreateRuntime(new HexCastleCell(
                new HexCoordinates(0, 1),
                HexCastleCellKind.Ground,
                initialBlocked: false));
            var cells = new Dictionary<HexCoordinates, HexCastleCellRuntime>
            {
                [structure.Coordinates] = structure,
                [firstExit.Coordinates] = firstExit,
                [secondExit.Coordinates] = secondExit
            };
            world.Configure(catalog, cells, Vector3.zero, 1f, 10801);
            var barracks = structure.gameObject.AddComponent<HexCastleBarracksRuntime>();
            return new BarracksSetup(structure, world, barracks);
        }

        private HexCastleCellRuntime CreateRuntime(HexCastleCell cell)
        {
            var root = Own(new GameObject($"Cell_{cell.Coordinates.Q}_{cell.Coordinates.R}"));
            var runtime = root.AddComponent<HexCastleCellRuntime>();
            var tile = CreateChild("TileVisualRoot", root.transform);
            var content = CreateChild("ContentVisualRoot", root.transform);
            if (!cell.InitialBlocked)
            {
                runtime.Configure(cell, null, null, tile, content);
                return runtime;
            }

            var health = root.AddComponent<HealthComponent>();
            var collider = root.AddComponent<BoxCollider>();
            runtime.Configure(cell, health, collider, tile, content);
            return runtime;
        }

        private static HexCastleCell CreateBarracksCell(HexCastleBuildingRole role)
        {
            return new HexCastleCell(
                new HexCoordinates(0, 0),
                HexCastleCellKind.Building,
                defenseLayer: 1,
                hitPoints: 180f,
                regionId: 1,
                initialBlocked: true,
                noDeploy: true,
                placementId: $"test_{role}",
                visualVariantId: role == HexCastleBuildingRole.KnightBarracks
                    ? "building_barracks_blue"
                    : "building_tent_blue",
                buildingRole: role,
                placementDensity: HexCastlePlacementDensity.Dense,
                buildingGrade: 1);
        }

        private T Own<T>(T value) where T : Object
        {
            owned.Add(value);
            return value;
        }

        private static Transform CreateChild(string name, Transform parent)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }

        private readonly struct BarracksSetup
        {
            public BarracksSetup(
                HexCastleCellRuntime structure,
                HexCastleGarrisonWorld world,
                HexCastleBarracksRuntime barracks)
            {
                Structure = structure;
                World = world;
                Barracks = barracks;
            }

            public HexCastleCellRuntime Structure { get; }
            public HexCastleGarrisonWorld World { get; }
            public HexCastleBarracksRuntime Barracks { get; }
        }
    }
}
