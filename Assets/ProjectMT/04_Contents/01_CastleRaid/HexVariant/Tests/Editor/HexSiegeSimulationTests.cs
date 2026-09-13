using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectMT.Contents.CastleRaidHex.Editor;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Editor.Tests
{
    public sealed class HexSiegeSimulationTests
    {
        private HexSiegeSimulationScenario scenario;

        [OneTimeSetUp]
        public void CreateScenario() => scenario = HexSiegeSimulationData.Create(10801, 4, HexCastleTheme.PetalBloom, 12, 2, "argo_01");

        private HexSiegeSimulationScenario Copy() => JsonUtility.FromJson<HexSiegeSimulationScenario>(JsonUtility.ToJson(scenario));

        [Test] public void RepresentativeInputs_ShareCastleStatsAndTimingWithDistinctLayouts()
        {
            var inputs = Enumerable.Range(0, 4).Select(HexSiegeSimulationData.LoadRepresentative).ToArray();
            var expectedSites = new[] { 1, 2, 4, 24 };
            for (var i = 0; i < inputs.Length; i++)
            {
                var input = inputs[i];
                Assert.AreEqual(24, input.spawns.Count);
                Assert.AreEqual(inputs[0].layoutSignature, input.layoutSignature);
                Assert.AreEqual(inputs[0].profileJson, input.profileJson);
                Assert.AreEqual(JsonUtility.ToJson(inputs[0].garrisonTuning), JsonUtility.ToJson(input.garrisonTuning));
                Assert.AreEqual(expectedSites[i], input.spawns.Select(s => s.q + "," + s.r).Distinct().Count());
                for (var j = 0; j < 24; j++)
                {
                    var spawn = input.spawns[j]; var baseline = inputs[0].spawns[j];
                    Assert.AreEqual(2 + j * 4, spawn.tick);
                    Assert.AreEqual(baseline.monster, spawn.monster); Assert.AreEqual(baseline.aiPattern, spawn.aiPattern);
                    Assert.AreEqual(baseline.speed, spawn.speed); Assert.AreEqual(baseline.damage, spawn.damage);
                    Assert.AreEqual(baseline.health, spawn.health); Assert.AreEqual(baseline.interval, spawn.interval);
                }
            }
        }

        [Test] public void AssaultLimit_Accepts24AndRejects25WithoutPartialAppend()
        {
            var copy = Copy(); copy.spawns.Clear();
            var deployment = copy.cells.Select(c => c.Build()).First(c => c.Kind == HexCastleCellKind.Deployment && !c.InitialBlocked);
            Assert.AreEqual(24, HexSiegeSimulationData.MaximumAssaultSpawns);
            HexSiegeSimulationData.AddAssaultSpawns(copy, new HexSiegeSimulationSpawn(), deployment.Coordinates, 24);
            Assert.DoesNotThrow(() => copy.Validate());
            Assert.Throws<ArgumentException>(() => HexSiegeSimulationData.AddSingleAssaultSpawn(copy, new HexSiegeSimulationSpawn(), deployment.Coordinates));
            Assert.AreEqual(24, copy.spawns.Count);
        }

        [Test] public void Review_MovingSameDefenderIsNotTargetChurn()
        {
            var result = new HexSiegeSimulationResult { scenario = Copy() };
            for (var tick = 0; tick < 40; tick++)
            {
                var frame = new HexSiegeSimulationFrame { tick = tick };
                frame.units.Add(new HexSiegeSimulationUnitState { id = 1, hp = 100, state = "Traversing",
                    hasTarget = true, targetKind = "Defender", targetUnit = 8, targetQ = tick, x = tick * 0.1f });
                result.frames.Add(frame);
            }
            Assert.IsFalse(HexSiegeSimulationData.Analyze(result).Any(d => d.kind == "TargetChurn"));
            foreach (var frame in result.frames) frame.units[0].targetUnit = frame.tick % 2 == 0 ? 8 : 9;
            Assert.IsTrue(HexSiegeSimulationData.Analyze(result).Any(d => d.kind == "TargetChurn"));
        }

        [TestCase(false)] [TestCase(true)]
        public void Review_DestroyedTargetsAndTargetlessTransitionsAreNotChurn(bool defender)
        {
            var result = new HexSiegeSimulationResult { scenario = Copy() };
            for (var tick = 0; tick < 16; tick++)
            {
                var id = tick / 2;
                var frame = new HexSiegeSimulationFrame { tick = tick };
                frame.units.Add(new HexSiegeSimulationUnitState { id = 1, hp = 100, hasTarget = tick % 2 == 0,
                    targetKind = defender ? "Defender" : "Structure", targetQ = id, targetUnit = id + 10 });
                for (var dead = 0; dead < id + tick % 2; dead++)
                {
                    if (defender) frame.defenders.Add(new HexSiegeSimulationDefenderState { id = dead + 10, hp = 0 });
                    else frame.damagedCells.Add(new HexSiegeSimulationCellState { q = dead, hp = 0 });
                }
                result.frames.Add(frame);
            }
            Assert.IsFalse(HexSiegeSimulationData.Analyze(result).Any(d => d.kind == "TargetChurn"));
            foreach (var frame in result.frames) { frame.defenders.Clear(); frame.damagedCells.Clear(); }
            Assert.IsTrue(HexSiegeSimulationData.Analyze(result).Any(d => d.kind == "TargetChurn"));
            foreach (var frame in result.frames) frame.units[0].hp = 0;
            Assert.IsFalse(HexSiegeSimulationData.Analyze(result).Any(d => d.kind == "TargetChurn"));
        }

        [Test] public void StressVideoFlag_RoundTripsWithoutChangingDeployment()
        {
            var input = HexSiegeSimulationData.CreateStressScenario(0, 50);
            Assert.IsFalse(input.recordVideo);
            input.recordVideo = true;
            var copy = JsonUtility.FromJson<HexSiegeSimulationScenario>(JsonUtility.ToJson(input));
            Assert.IsTrue(copy.recordVideo);
            Assert.AreEqual(50, copy.spawns.Count);
            Assert.DoesNotThrow(() => copy.Validate());
        }

        [Test] public void Review_TargetlessRetreatIsNotIdleButStationaryWaitIs()
        {
            var result = new HexSiegeSimulationResult { scenario = Copy() };
            for (var tick = 0; tick < 40; tick++)
            {
                var frame = new HexSiegeSimulationFrame { tick = tick };
                frame.units.Add(new HexSiegeSimulationUnitState { id = 1, hp = 100, state = "Traversing", x = tick * 0.1f });
                result.frames.Add(frame);
            }
            Assert.IsFalse(HexSiegeSimulationData.Analyze(result).Any(d => d.kind == "Idle"));
            foreach (var frame in result.frames) frame.units[0].x = 0;
            Assert.IsTrue(HexSiegeSimulationData.Analyze(result).Any(d => d.kind == "Idle"));
        }

        [Test]
        public void ThemeDisplayUsesOfficialKoreanNamesWithoutChangingLegacyJson()
        {
            foreach (var theme in HexCastleThemeCatalog.Themes)
            {
                var input = Copy(); input.theme = theme; input.name = theme + " legacy";
                var before = JsonUtility.ToJson(input);
                var display = HexSiegeSimulationData.ScenarioDisplayName(input);
                StringAssert.StartsWith(HexCastleThemeCatalog.ResolveKoreanName(theme), display);
                StringAssert.DoesNotContain(theme.ToString(), display);
                Assert.AreEqual(before, JsonUtility.ToJson(input));
                Assert.AreEqual(theme, JsonUtility.FromJson<HexSiegeSimulationScenario>(before).theme);
            }
            var generated = HexSiegeSimulationData.CreateForWallLayers(10801, 3, HexCastleTheme.PetalBloom);
            StringAssert.StartsWith("꽃잎 군락 요새", generated.name);
            Assert.AreEqual(HexSiegeSimulationData.ScenarioDisplayName(generated), generated.name);
        }

        [Test]
        public void DefaultScenarioUsesProductionGarrisonWithoutArtificialOuterInputs()
        {
            Assert.IsTrue(scenario.useGameGarrison);
            Assert.IsEmpty(scenario.defenders);
            var old = Copy(); old.schema = 1;
            Assert.Throws<ArgumentException>(() => old.Validate());
        }

        [Test]
        public void ClickSummonCopiesSelectedMonsterAndAiToDeploymentCell()
        {
            var copy = Copy(); copy.spawns.Clear();
            var deployment = copy.cells.Select(c => c.Build()).First(c => c.Kind == HexCastleCellKind.Deployment && !c.InitialBlocked);
            var template = new HexSiegeSimulationSpawn
            {
                monster = "argo_01", aiPattern = 3, tick = 40, wave = 7,
                health = 321, damage = 45, speed = 2.5f, range = 1.4f, interval = 0.8f
            };
            HexSiegeSimulationData.AddAssaultSpawns(copy, template, deployment.Coordinates, 3);
            Assert.AreEqual(3, copy.spawns.Count);
            Assert.IsTrue(copy.spawns.All(s => s.q == deployment.Coordinates.Q && s.r == deployment.Coordinates.R));
            Assert.IsTrue(copy.spawns.All(s => s.monster == "argo_01" && s.aiPattern == 3 && s.tick == 40 && s.wave == 7));
            Assert.AreEqual("수비대 사냥", HexSiegeSimulationWindow.AssaultPatternName(3));
            Assert.DoesNotThrow(() => copy.Validate());
            var wall = copy.cells.Select(c => c.Build()).First(c => c.InitialBlocked);
            Assert.Throws<ArgumentException>(() => HexSiegeSimulationData.AddAssaultSpawns(copy, template, wall.Coordinates, 1));
        }

        [Test]
        public void DirectMapSummonAddsExactlyOneSelectedMonsterAndAi()
        {
            var copy = Copy(); copy.spawns.Clear();
            var deployment = copy.cells.Select(c => c.Build()).First(c => c.Kind == HexCastleCellKind.Deployment && !c.InitialBlocked);
            var template = new HexSiegeSimulationSpawn { monster = "argo_01", aiPattern = 5, tick = 17, wave = 4 };

            HexSiegeSimulationData.AddSingleAssaultSpawn(copy, template, deployment.Coordinates);

            Assert.That(copy.spawns.Count, Is.EqualTo(1));
            Assert.That(copy.spawns[0].monster, Is.EqualTo("argo_01"));
            Assert.That(copy.spawns[0].aiPattern, Is.EqualTo(5));
            Assert.That(copy.spawns[0].tick, Is.EqualTo(17));
            Assert.That(copy.spawns[0].wave, Is.EqualTo(4));
            Assert.DoesNotThrow(() => copy.Validate());
        }

        [Test]
        public void SelectedAiIsAppliedToEachProductionSpawnInsteadOfOnlyBeingStored()
        {
            var catalog = Resources.Load<HexCastleAssaultAIProfileCatalog>(HexCastleAssaultAIProfileCatalog.DefaultResourcesPath);
            var originalPattern = catalog.Resolve("argo_01").Pattern;
            var first = new HexSiegeSimulationSpawn { monster = "argo_01", aiPattern = (int)HexCastleAssaultPattern.TurretHunter };
            var second = new HexSiegeSimulationSpawn { monster = "argo_01", aiPattern = (int)HexCastleAssaultPattern.WallBreaker };

            var firstProfile = HexSiegeSimulationRunner.ResolveSimulationProfile(catalog, first);
            var secondProfile = HexSiegeSimulationRunner.ResolveSimulationProfile(catalog, second);

            Assert.That(firstProfile.Pattern, Is.EqualTo(HexCastleAssaultPattern.TurretHunter));
            Assert.That(secondProfile.Pattern, Is.EqualTo(HexCastleAssaultPattern.WallBreaker));
            Assert.That(firstProfile, Is.Not.SameAs(secondProfile));
            Assert.That(catalog.Resolve("argo_01").Pattern, Is.EqualTo(originalPattern));
        }

        [Test]
        public void EmptyScenarioCanStartAndRecordBeforeFirstLiveClick()
        {
            var empty = HexSiegeSimulationData.Create(10801, 4, HexCastleTheme.PetalBloom, 0, 0, "argo_01");
            Assert.That(empty.spawns, Is.Empty);
            Assert.DoesNotThrow(() => empty.Validate(false));
            Assert.DoesNotThrow(() => empty.Validate());
            Assert.Throws<ArgumentException>(() => empty.Validate(true));
        }

        [TestCase(2, 17)]
        [TestCase(3, 17)]
        [TestCase(4, 17)]
        [TestCase(2, -93)]
        [TestCase(3, -93)]
        [TestCase(4, -93)]
        public void WallSelectionUsesExactProductionGeneration(int layers, int seed)
        {
            var input = HexSiegeSimulationData.CreateForWallLayers(seed, layers, HexCastleTheme.PetalBloom);
            var production = new HexCastleGenerationPipeline().GenerateFoundationForDifficulty(
                seed, input.difficulty, input.theme, input.garrisonTuning);
            Assert.AreEqual(layers, input.layers);
            Assert.AreEqual(production.Layout.LayoutSignature, input.layoutSignature);
            Assert.AreEqual(layers, production.Layout.DefenseLayerCount);
        }

        [Test]
        public void AiCardsUseFrozenAssignmentsAndDeterministicSingleSpawnRecords()
        {
            var input = HexSiegeSimulationData.CreateForWallLayers(55, 3, HexCastleTheme.PetalBloom);
            var second = JsonUtility.FromJson<HexSiegeSimulationScenario>(JsonUtility.ToJson(input));
            var cell = input.cells.Select(c => c.Build()).First(c => c.Kind == HexCastleCellKind.Deployment && !c.InitialBlocked);
            foreach (var card in HexSiegeSimulationData.GetAICards(input).Where(c => c.monsters.Length > 0))
            {
                var first = HexSiegeSimulationData.AddAISpawn(input, card.pattern, cell.Coordinates, 17);
                var repeated = HexSiegeSimulationData.AddAISpawn(second, card.pattern, cell.Coordinates, 17);
                Assert.AreEqual(JsonUtility.ToJson(first), JsonUtility.ToJson(repeated));
                var assigned = card.monsters.Single(m => m.MonsterId == first.monster);
                Assert.AreEqual((int)assigned.Pattern, first.aiPattern);
                Assert.AreEqual(assigned.SupportFocus, first.supportFocus);
                Assert.AreEqual(17, first.tick);
                Assert.AreEqual(1, first.wave);
            }
            Assert.AreEqual(HexSiegeSimulationData.GetAICards(input).Count(c => c.monsters.Length > 0), input.spawns.Count);
            Assert.DoesNotThrow(() => input.Validate());
        }

        [Test]
        public void FrozenAssignmentOverridesCurrentCatalogAndInvalidClickConsumesNothing()
        {
            var input = HexSiegeSimulationData.CreateForWallLayers(55, 3, HexCastleTheme.PetalBloom);
            var frozen = ScriptableObject.CreateInstance<HexCastleAssaultAIProfileCatalog>();
            try
            {
                JsonUtility.FromJsonOverwrite(input.profileJson, frozen);
                foreach (var entry in frozen.Entries)
                    entry.EditorConfigure(entry.MonsterId, HexCastleAssaultPattern.WallBreaker,
                        HexCastleAssaultSupportFocus.Recovery, entry.SupportRange, entry.SupportCooldown,
                        entry.SupportDuration, entry.HealRatio, entry.AttackBuffRate, entry.DefenseDamageMultiplier);
                input.profileJson = JsonUtility.ToJson(frozen);
                var cards = HexSiegeSimulationData.GetAICards(input);
                Assert.AreEqual(1, cards.Count(c => c.monsters.Length > 0));
                var wall = input.cells.Select(c => c.Build()).First(c => c.InitialBlocked);
                Assert.Throws<ArgumentException>(() => HexSiegeSimulationData.AddAISpawn(input, 4, wall.Coordinates, 0));
                Assert.IsEmpty(input.spawns);
                var cell = input.cells.Select(c => c.Build()).First(c => c.Kind == HexCastleCellKind.Deployment);
                var spawn = HexSiegeSimulationData.AddAISpawn(input, 4, cell.Coordinates, 0);
                Assert.AreEqual(HexCastleAssaultSupportFocus.Recovery, spawn.supportFocus);
            }
            finally { UnityEngine.Object.DestroyImmediate(frozen); }
        }

        [Test]
        public void MapLabelsUseRequestedNamesIncludingBarracks()
        {
            foreach (var cell in scenario.cells.Select(c => c.Build()))
            {
                if (cell.BuildingRole == HexCastleBuildingRole.KnightBarracks || cell.BuildingRole == HexCastleBuildingRole.FarmerBarracks)
                    Assert.AreEqual(cell.BuildingRole == HexCastleBuildingRole.KnightBarracks ? "병영" : "농부병영",
                        HexSiegeSimulationSymbols.CellMark(cell));
                else if (cell.GateRole != HexCastleGateRole.None)
                    Assert.AreEqual(cell.GateRole == HexCastleGateRole.OpenDefenderPassage ? "열문" : "닫문", HexSiegeSimulationSymbols.CellMark(cell));
                else if (cell.Kind == HexCastleCellKind.Wall)
                    Assert.AreEqual(cell.WallRole == HexCastleWallRole.Partition ? "격벽" : "벽",
                        HexSiegeSimulationSymbols.CellMark(cell));
                else if (cell.BuildingRole == HexCastleBuildingRole.Turret)
                    Assert.AreEqual("포탑", HexSiegeSimulationSymbols.CellMark(cell));
            }

            var wallHub = scenario.cells.Select(c => c.Build()).First(c => c.Kind == HexCastleCellKind.Tower);
            Assert.That(HexSiegeSimulationSymbols.CellMark(wallHub), Is.EqualTo("벽"));
        }

        [Test]
        public void StructureColorsSeparateWallLayersPartitionsBuildingsTurretsAndBarracks()
        {
            HexCastleCell Wall(int layer, HexCastleWallRole role = HexCastleWallRole.InnerDefense) =>
                new HexCastleCell(new HexCoordinates(layer, 0), HexCastleCellKind.Wall, layer, 100, role, initialBlocked: true);
            HexCastleCell Building(HexCastleBuildingRole role) =>
                new HexCastleCell(new HexCoordinates((int)role, 1), role == HexCastleBuildingRole.Turret
                        ? HexCastleCellKind.DefenseBuilding : HexCastleCellKind.Building, 1, 100,
                    initialBlocked: true, buildingRole: role,
                    placementDensity: HexCastlePlacementDensity.Sparse, buildingGrade: 1,
                    turretWeaponKind: role == HexCastleBuildingRole.Turret ? HexCastleTurretWeaponKind.Cannon : HexCastleTurretWeaponKind.None,
                    turretRangeCells: role == HexCastleBuildingRole.Turret ? 3 : (int?)null,
                    turretCanAttackAcrossWalls: role == HexCastleBuildingRole.Turret ? true : (bool?)null);

            var wallColors = Enumerable.Range(1, 4).Select(layer => HexSiegeSimulationSymbols.StructureColor(Wall(layer))).ToArray();
            Assert.That(wallColors.Distinct().Count(), Is.EqualTo(4));
            Assert.That(HexSiegeSimulationSymbols.CellMark(Wall(2, HexCastleWallRole.Partition)), Is.EqualTo("격벽"));
            Assert.That(HexSiegeSimulationSymbols.StructureColor(Wall(2, HexCastleWallRole.Partition)),
                Is.Not.EqualTo(wallColors[1]));

            var buildingColors = new[]
            {
                HexSiegeSimulationSymbols.StructureColor(Building(HexCastleBuildingRole.Blocker)),
                HexSiegeSimulationSymbols.StructureColor(Building(HexCastleBuildingRole.Turret)),
                HexSiegeSimulationSymbols.StructureColor(Building(HexCastleBuildingRole.KnightBarracks)),
                HexSiegeSimulationSymbols.StructureColor(Building(HexCastleBuildingRole.FarmerBarracks))
            };
            Assert.That(buildingColors.Distinct().Count(), Is.EqualTo(4));
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(10)]
        public void ProductionInitialGarrisonUsesDifficultyBarracksAndHealth(int difficulty)
        {
            var input = HexSiegeSimulationData.Create(10801, difficulty, HexCastleTheme.PetalBloom, 1, 0, "argo_01");
            var root = new GameObject("SimulationGarrisonTest");
            try
            {
                var map = new System.Collections.Generic.Dictionary<HexCoordinates, HexCastleCellRuntime>();
                foreach (var record in input.cells)
                {
                    var cell = record.Build(); var go = new GameObject("Cell");
                    go.transform.SetParent(root.transform);
                    var runtime = go.AddComponent<HexCastleCellRuntime>();
                    var tile = new GameObject("Tile"); tile.transform.SetParent(go.transform);
                    var content = new GameObject("Content"); content.transform.SetParent(go.transform);
                    var destroyed = new GameObject("Destroyed"); destroyed.transform.SetParent(go.transform);
                    runtime.Configure(cell, cell.InitialBlocked ? go.AddComponent<ProjectMT.Shared.Unit.HealthComponent>() : null,
                        cell.InitialBlocked ? go.AddComponent<BoxCollider>() : null, tile.transform, content.transform, destroyed.transform);
                    map.Add(cell.Coordinates, runtime);
                }
                var world = root.AddComponent<HexCastleGarrisonWorld>();
                var profile = HexCastleDifficultyProfile.Resolve(difficulty, input.seed);
                world.Configure(Resources.Load<HexCastleGarrisonCatalog>(HexCastleGarrisonCatalog.DefaultResourcesPath),
                    map, Vector3.zero, HexSpatialContract.CellOuterRadius, input.seed, null, input.garrisonTuning, profile);
                world.SpawnInitialGarrison(profile);
                Assert.AreEqual(profile.InitialKnightCount, world.Units.Count(u => u.Role == HexCastleGarrisonUnitRole.Knight));
                Assert.AreEqual(profile.InitialFarmerCount, world.Units.Count(u => u.Role == HexCastleGarrisonUnitRole.Farmer));
                foreach (var unit in world.Units)
                {
                    var role = unit.Role == HexCastleGarrisonUnitRole.Knight ? HexCastleBuildingRole.KnightBarracks : HexCastleBuildingRole.FarmerBarracks;
                    if (!map.Values.Any(c => c.BuildingRole == role)) role = HexCastleBuildingRole.KnightBarracks;
                    Assert.IsTrue(map.Values.Any(c => c.BuildingRole == role && c.Coordinates.DistanceTo(unit.HomeCoordinates) == 1));
                    Assert.AreNotEqual(HexCastleCellKind.Deployment, map[unit.HomeCoordinates].Kind);
                    var baseHp = unit.Role == HexCastleGarrisonUnitRole.Knight ? input.garrisonTuning.KnightHealth : input.garrisonTuning.FarmerHealth;
                    Assert.AreEqual(baseHp * profile.ResolveHealthMultiplier(unit.Role), unit.Health.MaxHealth, 0.01f);
                }
                foreach (var unit in world.Units) unit.ApplyDamage(unit.Health.MaxHealth * 2, unit.transform.position);
                var source = map.Values.First(c => c.BuildingRole == HexCastleBuildingRole.KnightBarracks);
                var barracks = source.gameObject.AddComponent<HexCastleBarracksRuntime>();
                barracks.Configure(source, world, input.garrisonTuning);
                Assert.IsTrue(barracks.IsProducing);
                barracks.Tick(input.garrisonTuning.KnightRefillInterval + 0.1f);
                Assert.Greater(barracks.TotalSpawned, 0);
                Assert.AreEqual(barracks.TotalSpawned, world.AliveUnitCount);
                var spawned = barracks.TotalSpawned;
                source.ApplyDamage(source.MaxHealth * 2, source.transform.position);
                barracks.Tick(input.garrisonTuning.KnightRefillInterval * 2);
                Assert.IsFalse(barracks.IsRunning);
                Assert.AreEqual(spawned, barracks.TotalSpawned);
                Assert.AreEqual(0, world.ActiveProductionReservationCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void FrozenMapAndInputsSurviveJsonRoundTrip()
        {
            var copy = Copy(); copy.Validate();
            Assert.AreEqual(JsonUtility.ToJson(scenario), JsonUtility.ToJson(copy));
            Assert.AreEqual(12, copy.spawns.Count);
            Assert.AreEqual(55, copy.spawns.Last().tick);
            Assert.AreNotEqual(copy.spawns[0].q + "," + copy.spawns[0].r, copy.spawns[1].q + "," + copy.spawns[1].r);
            Assert.AreEqual("64A7F8B902366609", copy.layoutSignature);
        }

        [Test]
        public void StructureNamesDistinguishBuildingRolesAndWallFromBuilding()
        {
            var roles = Enum.GetValues(typeof(HexCastleBuildingRole)).Cast<HexCastleBuildingRole>().Where(r => r != HexCastleBuildingRole.None).ToArray();
            Assert.AreEqual(roles.Length, roles.Select(HexSiegeSimulationSymbols.BuildingName).Distinct().Count());
            var wall = scenario.cells.Select(c => c.Build()).First(c => c.Kind == HexCastleCellKind.Wall);
            var building = scenario.cells.Select(c => c.Build()).First(c => c.IsBuildingCell);
            Assert.AreNotEqual(HexSiegeSimulationSymbols.CellName(wall), HexSiegeSimulationSymbols.CellName(building));
            Assert.AreEqual("기사 병영", HexSiegeSimulationSymbols.BuildingName(HexCastleBuildingRole.KnightBarracks));
            Assert.AreEqual("농부 병영", HexSiegeSimulationSymbols.BuildingName(HexCastleBuildingRole.FarmerBarracks));
        }

        [Test]
        public void DisplayStateShowsDeathBeforeStaleAttackStateAndTranslatesExecutionStates()
        {
            Assert.AreEqual("사망", HexSiegeSimulationWindow.DisplayState("Attacking", 0));
            Assert.AreEqual("공격 중", HexSiegeSimulationWindow.DisplayState("Attacking", 10));
            Assert.AreEqual("이동 중", HexSiegeSimulationWindow.DisplayState("Traversing", 10));
            foreach (var state in Enum.GetNames(typeof(HexAssaultExecutionState)))
                Assert.AreNotEqual(state, HexSiegeSimulationWindow.DisplayState(state, 10), state);
        }

        [Test]
        public void DefenderInputsRoundTripAndRejectBlockedCells()
        {
            var copy = Copy(); var c = copy.cells.Select(v => v.Build()).First(v => !v.InitialBlocked);
            copy.defenders.Add(new HexSiegeSimulationDefender { q = c.Coordinates.Q, r = c.Coordinates.R, role = HexCastleGarrisonUnitRole.Farmer });
            var roundTrip = JsonUtility.FromJson<HexSiegeSimulationScenario>(JsonUtility.ToJson(copy));
            Assert.DoesNotThrow(() => roundTrip.Validate());
            Assert.AreEqual(HexCastleGarrisonUnitRole.Farmer, roundTrip.defenders[0].role);
            c = copy.cells.Select(v => v.Build()).First(v => v.InitialBlocked && v.GateRole != HexCastleGateRole.OpenDefenderPassage);
            copy.defenders[0].q = c.Coordinates.Q; copy.defenders[0].r = c.Coordinates.R;
            Assert.Throws<ArgumentException>(() => copy.Validate());
            roundTrip.defenders[0].health = float.NaN;
            Assert.Throws<ArgumentException>(() => roundTrip.Validate());
        }

        [Test]
        public void RejectUnknownMonsterInsteadOfSilentlyUsingDefault()
        {
            var copy = Copy(); copy.spawns[0].monster = "does-not-exist";
            Assert.Throws<ArgumentException>(() => copy.Validate());
        }

        [Test]
        public void RejectInvalidSpawnAndNonFiniteStats()
        {
            var copy = Copy(); copy.spawns[0].q = 999;
            Assert.Throws<ArgumentException>(() => copy.Validate());
            copy = Copy(); copy.spawns[0].damage = float.NaN;
            Assert.Throws<ArgumentException>(() => copy.Validate());
            copy = Copy(); copy.spawns[0].tick = copy.durationTicks;
            Assert.Throws<ArgumentException>(() => copy.Validate());
        }

        [Test]
        public void DamageEventsRequireDamageableCell()
        {
            var copy = Copy(); var s = copy.spawns[0];
            copy.damageEvents.Add(new HexSiegeSimulationDamage { tick = 0, q = s.q, r = s.r, amount = 10 });
            Assert.Throws<ArgumentException>(() => copy.Validate());
            var wall = copy.cells.Select(c => c.Build()).First(c => c.InitialBlocked && c.MaxHealth > 0);
            copy.damageEvents[0].q = wall.Coordinates.Q; copy.damageEvents[0].r = wall.Coordinates.R;
            Assert.DoesNotThrow(() => copy.Validate());
        }

        private HexSiegeSimulationResult Result()
        {
            var result = new HexSiegeSimulationResult { scenario = Copy(), completed = true, unity = "test", runtimeHash = "runtime", driverHash = "driver" };
            result.scenarioHash = HexSiegeSimulationData.Hash(JsonUtility.ToJson(result.scenario));
            var frame = new HexSiegeSimulationFrame { tick = 0 };
            frame.units.Add(new HexSiegeSimulationUnitState { id = 1, x = 1, hp = 100 });
            frame.digest = HexSiegeSimulationData.Hash(JsonUtility.ToJson(frame));
            result.frames.Add(frame); return result;
        }

        [Test]
        public void CompareDistinguishesInputsEnvironmentAndTraceLoss()
        {
            var a = Result(); var b = Result();
            StringAssert.Contains("기록 일치", HexSiegeSimulationData.Compare(a, b));
            b.runtimeHash = "changed";
            StringAssert.Contains("실행 코드·환경 다름", HexSiegeSimulationData.Compare(a, b));
            b.traceLost = 1;
            StringAssert.Contains("판정 불가", HexSiegeSimulationData.Compare(a, b));
            b.scenarioHash = "other";
            StringAssert.Contains("입력 다름", HexSiegeSimulationData.Compare(a, b));
        }

        [Test]
        public void FirstDifferenceReportsUnitAndTick()
        {
            var a = Result(); var b = Result(); b.frames[0].units[0].x = 1.000001f;
            b.frames[0].digest = null; b.frames[0].digest = HexSiegeSimulationData.Hash(JsonUtility.ToJson(b.frames[0]));
            StringAssert.Contains("최초 차이: 0 tick", HexSiegeSimulationData.Compare(a, b));
            StringAssert.Contains("몬스터 1", HexSiegeSimulationData.Compare(a, b));
        }

        [Test]
        public void RecordedFrameTamperingIsRejected()
        {
            var directory = Path.Combine(HexSiegeSimulationData.OutputDirectory, "test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "result.json");
            try
            {
                var value = Result(); File.WriteAllText(path, JsonUtility.ToJson(value));
                Assert.DoesNotThrow(() => HexSiegeSimulationData.ReadResult(path));
                value.frames[0].units[0].hp = 1; File.WriteAllText(path, JsonUtility.ToJson(value));
                Assert.Throws<ArgumentException>(() => HexSiegeSimulationData.ReadResult(path));
            }
            finally { File.Delete(path); Directory.Delete(directory); }
        }

        [Test]
        public void DecisionSpreadDependsOnSpawnOrderNotUnityIdentity()
        {
            var a = new GameObject("SimulationSpreadA"); var b = new GameObject("SimulationSpreadB");
            try
            {
                var world = a.AddComponent<HexCastleAssaultWorld>();
                var first = a.AddComponent<HexCastleAssaultUnit>(); var second = b.AddComponent<HexCastleAssaultUnit>();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var orders = (System.Collections.Generic.Dictionary<int, int>)typeof(HexCastleAssaultWorld).GetField("unitSpawnOrders", flags).GetValue(world);
                var method = typeof(HexCastleAssaultWorld).GetMethod("ResolveDecisionSpread", flags);
                orders[first.GetInstanceID()] = 3; orders[second.GetInstanceID()] = 3;
                Assert.AreEqual(method.Invoke(world, new object[] { first }), method.Invoke(world, new object[] { second }));
                orders[second.GetInstanceID()] = 4;
                Assert.AreNotEqual(method.Invoke(world, new object[] { first }), method.Invoke(world, new object[] { second }));
            }
            finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); }
        }

        [TestCase(24)]
        [TestCase(50)]
        [TestCase(100)]
        public void StressScenarioExplicitlyAllowsLoadWithoutChangingProductionLimit(int count)
        {
            for (var index = 0; index < 4; index++)
            {
                var stress = HexSiegeSimulationData.CreateStressScenario(index, count);
                Assert.That(stress.stressTest, Is.True);
                Assert.That(stress.spawns.Count, Is.EqualTo(count));
                Assert.That(stress.spawns.All(s => s.tick == 2), Is.True);
                Assert.DoesNotThrow(() => stress.Validate(true));
                stress.stressTest = false;
                if (count > 24) Assert.Throws<ArgumentException>(() => stress.Validate(true));
            }
            Assert.That(HexSiegeSimulationData.MaximumAssaultSpawns, Is.EqualTo(24));
            Assert.Throws<ArgumentOutOfRangeException>(() => HexSiegeSimulationData.CreateStressScenario(0, 101));
        }

        [Test]
        public void LiveClickAlwaysQueuesOnAFutureSimulationTick()
        {
            Assert.AreEqual(1, HexSiegeSimulationRunner.ResolveQueuedSpawnTick(0, 100));
            Assert.AreEqual(42, HexSiegeSimulationRunner.ResolveQueuedSpawnTick(41, 100));
            Assert.Throws<InvalidOperationException>(() => HexSiegeSimulationRunner.ResolveQueuedSpawnTick(99, 100));
        }

        [Test]
        public void AutomaticReviewFlagsAliveUnitThatWaitsWithoutUsefulWork()
        {
            var value = new HexSiegeSimulationResult { scenario = Copy() };
            for (var tick = 0; tick < 32; tick++)
            {
                var frame = new HexSiegeSimulationFrame { tick = tick };
                frame.units.Add(new HexSiegeSimulationUnitState
                    { id = 1, hp = 100, state = "Holding", hasTarget = false, x = 1, z = 1 });
                value.frames.Add(frame);
            }

            var diagnostics = HexSiegeSimulationData.Analyze(value);

            Assert.That(diagnostics.Any(item => item.kind == "Idle" && item.unit == 1), Is.True);
        }

        [Test]
        public void AutomaticReviewAcceptsTemporaryWorkThatPreservesCohortAndStrategicTarget()
        {
            var value = new HexSiegeSimulationResult { scenario = Copy() };
            for (var tick = 0; tick < 12; tick++)
            {
                var frame = new HexSiegeSimulationFrame { tick = tick };
                frame.units.Add(new HexSiegeSimulationUnitState
                {
                    id = 1, hp = 100, state = "Attacking", intent = "LocalFallback", isTemporaryWork = true,
                    hasTarget = true, targetKind = "Structure", targetQ = 2, targetR = 1,
                    hasStrategicTarget = true, strategicTargetKind = "Structure", strategicTargetQ = 3, strategicTargetR = 1,
                    cohort = 7, x = 1, z = 1
                });
                value.frames.Add(frame);
            }

            var diagnostics = HexSiegeSimulationData.Analyze(value);

            Assert.That(diagnostics.Any(item => item.kind == "TemporaryWorkContract" ||
                                                item.kind == "TemporaryWorkCommitmentChanged"), Is.False);
        }

        [Test]
        public void AutomaticReviewFlagsTemporaryWorkThatChangesStrategicOwnership()
        {
            var value = new HexSiegeSimulationResult { scenario = Copy() };
            for (var tick = 0; tick < 2; tick++)
            {
                var frame = new HexSiegeSimulationFrame { tick = tick };
                frame.units.Add(new HexSiegeSimulationUnitState
                {
                    id = 1, hp = 100, state = "Attacking", intent = "LocalFallback", isTemporaryWork = true,
                    hasTarget = true, targetKind = "Structure", targetQ = 2, targetR = 1,
                    hasStrategicTarget = true, strategicTargetKind = "Structure",
                    strategicTargetQ = tick == 0 ? 3 : 4, strategicTargetR = 1,
                    cohort = tick == 0 ? 7 : 8, x = 1, z = 1
                });
                value.frames.Add(frame);
            }

            var diagnostics = HexSiegeSimulationData.Analyze(value);

            Assert.That(diagnostics.Any(item => item.kind == "TemporaryWorkCommitmentChanged" && item.unit == 1), Is.True);
        }
    }
}
