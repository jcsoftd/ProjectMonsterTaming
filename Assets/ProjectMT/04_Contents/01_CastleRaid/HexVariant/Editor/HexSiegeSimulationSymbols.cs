using UnityEditor;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Editor
{
    public static class HexSiegeSimulationSymbols
    {
        public static string CellName(HexCastleCell cell)
        {
            if (cell.GateRole != HexCastleGateRole.None)
                return cell.GateRole == HexCastleGateRole.OpenDefenderPassage ? "열린 문 (수비대 통행)" : "닫힌 문";
            if (cell.Kind == HexCastleCellKind.Palace) return "왕궁";
            if (cell.Kind == HexCastleCellKind.Wall || cell.Kind == HexCastleCellKind.Tower)
                return cell.WallRole == HexCastleWallRole.Partition ? "격벽" : cell.DefenseLayer + "선 성벽";
            if (cell.BuildingRole != HexCastleBuildingRole.None) return BuildingName(cell.BuildingRole);
            switch (cell.Kind)
            {
                case HexCastleCellKind.Gate: return "성문";
                case HexCastleCellKind.DefenseBuilding: return "방어 건물";
                case HexCastleCellKind.RewardBuilding: return "보상 건물";
                case HexCastleCellKind.Building: return "일반 건물";
                case HexCastleCellKind.Deployment: return "공격 병력 배치 구역";
                case HexCastleCellKind.Ground: return "통행로";
                default: return "예약 구역";
            }
        }

        public static string BuildingName(HexCastleBuildingRole role)
        {
            switch (role)
            {
                case HexCastleBuildingRole.KnightBarracks: return "기사 병영";
                case HexCastleBuildingRole.FarmerBarracks: return "농부 병영";
                case HexCastleBuildingRole.Turret: return "포탑";
                case HexCastleBuildingRole.TrainingYard: return "연습장";
                case HexCastleBuildingRole.Church: return "교회";
                case HexCastleBuildingRole.GoldStorage: return "골드 건물";
                case HexCastleBuildingRole.EquipmentForge: return "장비 건물";
                case HexCastleBuildingRole.KeyVault: return "열쇠 건물";
                default: return "일반 건물";
            }
        }

        public static string BuildingMark(HexCastleBuildingRole role)
        {
            if (role == HexCastleBuildingRole.KnightBarracks) return "병영";
            if (role == HexCastleBuildingRole.FarmerBarracks) return "농부병영";
            return "건물";
        }

        public static string CellMark(HexCastleCell cell)
        {
            if (cell.GateRole != HexCastleGateRole.None) return cell.GateRole == HexCastleGateRole.OpenDefenderPassage ? "열문" : "닫문";
            if (cell.Kind == HexCastleCellKind.Gate) return "닫문";
            if (cell.Kind == HexCastleCellKind.Wall || cell.Kind == HexCastleCellKind.Tower)
                return cell.WallRole == HexCastleWallRole.Partition ? "격벽" : "벽";
            if (cell.BuildingRole == HexCastleBuildingRole.Turret) return "포탑";
            if (cell.Kind == HexCastleCellKind.Palace) return "왕궁";
            return cell.IsBuildingCell ? BuildingMark(cell.BuildingRole) : "";
        }

        public static Color StructureColor(HexCastleCell cell)
        {
            if (cell == null) return new Color(0.13f, 0.18f, 0.24f);
            if (cell.Kind == HexCastleCellKind.Deployment) return new Color(0.12f, 0.36f, 0.36f);
            if (cell.GateRole != HexCastleGateRole.None)
                return cell.GateRole == HexCastleGateRole.OpenDefenderPassage
                    ? new Color(0.18f, 0.56f, 0.82f)
                    : new Color(0.86f, 0.31f, 0.10f);
            if (cell.Kind == HexCastleCellKind.Palace) return new Color(0.72f, 0.56f, 0.15f);
            if (cell.Kind == HexCastleCellKind.Wall || cell.Kind == HexCastleCellKind.Tower)
                return cell.WallRole == HexCastleWallRole.Partition
                    ? new Color(0.52f, 0.31f, 0.42f)
                    : WallLayerColor(cell.DefenseLayer);
            switch (cell.BuildingRole)
            {
                case HexCastleBuildingRole.Turret: return new Color(0.66f, 0.25f, 0.22f);
                case HexCastleBuildingRole.KnightBarracks: return new Color(0.42f, 0.12f, 0.20f);
                case HexCastleBuildingRole.FarmerBarracks: return new Color(0.45f, 0.34f, 0.10f);
                default: return cell.IsBuildingCell ? new Color(0.40f, 0.36f, 0.62f) : new Color(0.13f, 0.18f, 0.24f);
            }
        }

        public static Color WallLayerColor(int defenseLayer)
        {
            switch (Mathf.Clamp(defenseLayer, 1, 4))
            {
                case 1: return new Color(0.64f, 0.54f, 0.36f);
                case 2: return new Color(0.52f, 0.43f, 0.27f);
                case 3: return new Color(0.40f, 0.34f, 0.24f);
                default: return new Color(0.31f, 0.29f, 0.25f);
            }
        }

        public static void DrawStructure(HexCastleCell cell, Vector2 point, float radius)
        {
            var mark = CellMark(cell);
            if (string.IsNullOrEmpty(mark) || radius < 10) return;
            var barracks = mark == "병영" || mark == "농부병영";
            if (barracks)
            {
                EditorGUI.DrawRect(new Rect(point.x - radius * 0.76f, point.y - 10, radius * 1.52f, 20), new Color(0.4f, 0.12f, 0.18f));
            }
            var style = new GUIStyle(EditorStyles.whiteLabel) { alignment = TextAnchor.MiddleCenter, fontSize = radius >= 16 ? 11 : 9, fontStyle = barracks ? FontStyle.Bold : FontStyle.Normal };
            GUI.Label(new Rect(point.x - radius, point.y - 10, radius * 2, 20), mark, style);
        }

        public static void DrawDefender(Vector2 point, string role, bool alive, bool selected)
        {
            var knight = role == "Knight";
            var size = selected ? 11f : 9f;
            Handles.color = !alive ? Color.gray : knight ? new Color(1, 0.32f, 0.4f) : new Color(1, 0.73f, 0.2f);
            if (knight)
                Handles.DrawAAConvexPolygon(point + new Vector2(-size, -size), point + new Vector2(size, -size),
                    point + new Vector2(size * 0.8f, size * 0.4f), point + new Vector2(0, size), point + new Vector2(-size * 0.8f, size * 0.4f));
            else Handles.DrawAAConvexPolygon(point + Vector2.up * size, point + Vector2.right * size, point + Vector2.down * size, point + Vector2.left * size);
            var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 10 };
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(point.x - size, point.y - size, size * 2, size * 2), knight ? "기" : "농", style);
            if (selected) { Handles.color = Color.white; Handles.DrawWireDisc(point, Vector3.forward, size + 3); }
        }
    }
}
