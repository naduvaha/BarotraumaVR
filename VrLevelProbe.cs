using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace BarotraumaVR
{
    internal static class VrLevelProbe
    {
        private static object? sampledLevel;
        private static bool loggedFailure;

        public static void SampleOncePerLevel()
        {
            try
            {
                var levelType = VRManager.FindType("Barotrauma.Level");
                var level = levelType?.GetProperty("Loaded", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (level == null || ReferenceEquals(level, sampledLevel))
                    return;

                int wallCount = 0;
                int cellCount = 0;
                int edgeCount = 0;
                int vertexCount = 0;
                var cellTypes = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var wall in EnumerateMembers(level, "ExtraWalls", "UnsyncedExtraWalls"))
                {
                    wallCount++;
                    foreach (var cell in EnumerateMember(wall, "Cells"))
                    {
                        cellCount++;
                        string name = ReadMember(cell, "CellType")?.ToString() ?? "Unknown";
                        cellTypes[name] = cellTypes.TryGetValue(name, out int count) ? count + 1 : 1;
                        edgeCount += CountItems(ReadMember(cell, "Edges"));
                        vertexCount += CountItems(ReadMember(cell, "BodyVertices"));
                    }
                }

                var summary = string.Join(", ", cellTypes);
                VRManager.Log($"[VrLevelProbe] Level loaded: walls={wallCount}, cells={cellCount}, edges={edgeCount}, bodyVertices={vertexCount}, types=[{summary}]");
                LogCellSet(level, "GetAllCells");
                sampledLevel = level;
            }
            catch (Exception ex)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    VRManager.Log($"[VrLevelProbe] Sampling failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void LogCellSet(object level, string methodName)
        {
            var method = level.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method?.Invoke(level, null) is not System.Collections.IEnumerable cells)
                return;

            int cellCount = 0;
            int edgeCount = 0;
            int vertexCount = 0;
            var cellTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cell in cells)
            {
                if (cell == null)
                    continue;
                cellCount++;
                string name = ReadMember(cell, "CellType")?.ToString() ?? "Unknown";
                cellTypes[name] = cellTypes.TryGetValue(name, out int count) ? count + 1 : 1;
                edgeCount += CountItems(ReadMember(cell, "Edges"));
                vertexCount += CountItems(ReadMember(cell, "BodyVertices"));
            }

            VRManager.Log($"[VrLevelProbe] {methodName}: cells={cellCount}, edges={edgeCount}, bodyVertices={vertexCount}, types=[{string.Join(", ", cellTypes)}]");
        }

        private static object? ReadMember(object value, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = value.GetType();
            return type.GetField(name, flags)?.GetValue(value) ?? type.GetProperty(name, flags)?.GetValue(value);
        }

        private static int CountItems(object? member)
        {
            if (member is ICollection collection)
                return collection.Count;
            if (member is not IEnumerable sequence)
                return member == null ? 0 : 1;

            int count = 0;
            foreach (var item in sequence)
            {
                if (item != null)
                    count++;
            }
            return count;
        }

        private static IEnumerable<object> EnumerateMembers(object value, params string[] names)
        {
            foreach (var name in names)
            {
                foreach (var item in EnumerateMember(value, name))
                    yield return item;
            }
        }

        private static IEnumerable<object> EnumerateMember(object value, string name)
        {
            var type = value.GetType();
            object? member = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value)
                ?? type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value);
            if (member is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    if (item != null)
                        yield return item;
                }
            }
            else if (member != null)
            {
                yield return member;
            }
        }
    }
}
