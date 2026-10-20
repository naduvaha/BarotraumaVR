using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BarotraumaVR
{
    /// <summary>
    /// Harmony patches for SpriteBatch.Begin() to inject VR camera transforms.
    /// Phase 2.1: Intercept camera.Transform and replace with VR perspective matrix.
    ///
    /// Pattern from CasualtyVR: Keep game's Camera object unchanged, but override
    /// the matrix passed to rendering at the last moment.
    ///
    /// Uses dynamic typing to avoid compile-time MonoGame dependency.
    /// </summary>
    public static class VrSpriteBatchPatch
    {
        private static bool patchApplied = false;
        private static string lastStatus = "[VrSpriteBatchPatch] SpriteBatch.Begin is not patched yet";
        private static int currentEyeIndex = -1; // -1 = not rendering VR, 0 = left eye, 1 = right eye
        private static int drawMapBatch;
        private static int caveBatch;
        private static bool billboardBatch;
        private static bool loggedBillboard;
        private static FieldInfo? batchListField;
        private static FieldInfo? batchCountField;
        private static readonly HashSet<string> seenCallers = new();

        /// <summary>
        /// Apply Harmony patch to SpriteBatch.Begin().
        /// </summary>
        public static void Apply(Harmony harmony, object? monoGameAnchor = null)
        {
            if (patchApplied)
                return;

            try
            {
                VRManager.Log("[VrSpriteBatchPatch] Starting patch application...");

                // At server startup MonoGame is not in the assembly list yet.
                // GraphicsDevice's assembly is the one that actually contains SpriteBatch.
                var spriteBatchType = TypeInAssembly(monoGameAnchor, "Microsoft.Xna.Framework.Graphics.SpriteBatch")
                    ?? VRManager.FindType("Microsoft.Xna.Framework.Graphics.SpriteBatch");
                if (spriteBatchType == null)
                {
                    var anchorAsm = monoGameAnchor?.GetType().Assembly.GetName().Name ?? "no GraphicsDevice yet";
                    lastStatus = "[VrSpriteBatchPatch] SpriteBatch type not loaded yet (anchor: " + anchorAsm + ")";
                    VRManager.Log(lastStatus);
                    return;
                }
                VRManager.Log("[VrSpriteBatchPatch] SpriteBatch type found");

                var matrixType = spriteBatchType.Assembly.GetType("Microsoft.Xna.Framework.Matrix")
                    ?? VRManager.FindType("Microsoft.Xna.Framework.Matrix");
                if (matrixType == null)
                {
                    lastStatus = "[VrSpriteBatchPatch] ERROR: Matrix type not found";
                    VRManager.Log(lastStatus);
                    return;
                }
                VRManager.Log("[VrSpriteBatchPatch] Matrix type found");

                // MonoGame's full overload is Begin(..., Matrix? transformMatrix), not Matrix.
                var nullableMatrix = typeof(Nullable<>).MakeGenericType(matrixType);
                MethodInfo? targetMethod = null;
                int beginMethodCount = 0;

                foreach (var method in spriteBatchType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != "Begin")
                        continue;

                    beginMethodCount++;
                    var parameters = method.GetParameters();
                    var described = string.Join(", ", Array.ConvertAll(parameters, p => p.ParameterType.Name));
                    VRManager.Log($"[VrSpriteBatchPatch] Begin({described})");

                    if (parameters.Length == 7 && parameters[6].ParameterType == nullableMatrix)
                        targetMethod = method;
                }

                VRManager.Log($"[VrSpriteBatchPatch] Total Begin overloads found: {beginMethodCount}");

                if (targetMethod == null)
                {
                    lastStatus = "[VrSpriteBatchPatch] ERROR: SpriteBatch.Begin(..., Matrix?) not found";
                    VRManager.Log(lastStatus);
                    return;
                }

                var prefix = CreatePrefix(nullableMatrix);
                VRManager.Log("[VrSpriteBatchPatch] Applying Harmony patch...");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                PatchBillboard(harmony, spriteBatchType);
                patchApplied = true;
                lastStatus = "[VrSpriteBatchPatch] SpriteBatch.Begin patched successfully";
                VRManager.Log(lastStatus);
            }
            catch (Exception ex)
            {
                lastStatus = $"[VrSpriteBatchPatch] EXCEPTION: {ex.GetType().Name}: {ex.Message}";
                VRManager.Log(lastStatus);
            }
        }

        public static void LogStatus()
        {
            VRManager.Log(patchApplied
                ? "[VrSpriteBatchPatch] SpriteBatch.Begin patched successfully"
                : lastStatus);
        }

        private static Type? TypeInAssembly(object? anchor, string fullName)
        {
            return anchor?.GetType().Assembly.GetType(fullName, throwOnError: false);
        }

        /// <summary>
        /// Set which eye is currently being rendered (called by VrStereoRenderer).
        /// -1 = not in VR mode, 0 = left eye, 1 = right eye.
        /// </summary>
        public static int CurrentEye => currentEyeIndex;

        public static void SetCurrentEye(int eyeIndex)
        {
            currentEyeIndex = eyeIndex;
            drawMapBatch = 0;
            caveBatch = 0;
            billboardBatch = false;
        }

        private static void PatchBillboard(Harmony harmony, Type spriteBatchType)
        {
            var itemType = spriteBatchType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.SpriteBatchItem");
            var batcherType = spriteBatchType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.SpriteBatcher");
            if (itemType != null)
                VrFirstPerson.BindBillboard(itemType);

            if (batcherType != null)
            {
                batchListField = batcherType.GetField("_batchItemList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                batchCountField = batcherType.GetField("_batchItemCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var drawBatch = batcherType.GetMethod("DrawBatch", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var prefix = typeof(VrSpriteBatchPatch).GetMethod(nameof(DrawBatch_Prefix), BindingFlags.Public | BindingFlags.Static);
                if (drawBatch != null && prefix != null)
                {
                    harmony.Patch(drawBatch, prefix: new HarmonyMethod(prefix));
                    VRManager.Log("[VrSpriteBatchPatch] First-person billboards on SpriteBatcher.DrawBatch");
                    return;
                }
            }

            if (itemType == null)
            {
                VRManager.Log("[VrSpriteBatchPatch] SpriteBatchItem was not found");
                return;
            }

            var postfix = typeof(VrSpriteBatchPatch).GetMethod(nameof(Billboard_Postfix), BindingFlags.Public | BindingFlags.Static);
            int patched = 0;
            foreach (var method in itemType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "Set" || method.IsAbstract)
                    continue;
                harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                patched++;
            }
            VRManager.Log(patched > 0
                ? $"[VrSpriteBatchPatch] First-person billboards on {patched} SpriteBatchItem.Set"
                : "[VrSpriteBatchPatch] SpriteBatchItem.Set was not found");
        }

        public static void DrawBatch_Prefix(object __instance)
        {
            if (!billboardBatch || __instance == null || batchListField == null)
                return;

            int n = batchCountField != null ? Convert.ToInt32(batchCountField.GetValue(__instance)) : int.MaxValue;
            object? list = batchListField.GetValue(__instance);
            int done = 0;
            if (list is Array arr)
            {
                int m = Math.Min(n, arr.Length);
                for (int i = 0; i < m; i++)
                {
                    object? item = arr.GetValue(i);
                    if (item == null)
                        continue;
                    VrFirstPerson.BillboardSprite(item);
                    done++;
                }
            }
            else if (list is System.Collections.IList ilist)
            {
                int m = Math.Min(n, ilist.Count);
                for (int i = 0; i < m; i++)
                {
                    object? item = ilist[i];
                    if (item == null)
                        continue;
                    VrFirstPerson.BillboardSprite(item);
                    done++;
                }
            }

            if (!loggedBillboard && done > 0)
            {
                loggedBillboard = true;
                VRManager.Log($"[VrSpriteBatchPatch] Billboarded {done} sprites along the corridor");
            }
        }

        public static void Billboard_Postfix(object __instance)
        {
            if (!billboardBatch)
                return;
            VrFirstPerson.BillboardSprite(__instance);
        }

        /// <summary>
        /// Prefix body. Returns a boxed Nullable&lt;Matrix&gt; to write back, or null to leave the argument alone.
        /// The real prefix is emitted so its parameter type is Matrix? and its name is transformMatrix.
        /// </summary>
        public static object? ReplacementOrNull(object? current)
        {
            billboardBatch = false;
            if (!VRManager.IsVRActive || currentEyeIndex < 0 || currentEyeIndex > 1)
                return null;

            try
            {
                if (IsHudPass())
                {
                    GameScreenPatches.NeutralizeHud();
                    return null;
                }
                if (current == null)
                    return null;

                // Boxing Nullable<Matrix> yields the Matrix itself when it has a value, or null when it does not.
                var type = current.GetType();
                object matrixObj = current;
                Type matrixType = type;
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    if (type.GetProperty("HasValue")?.GetValue(current) is not true)
                        return null;
                    matrixObj = type.GetProperty("Value")!.GetValue(current)!;
                    matrixType = type.GetGenericArguments()[0];
                }

                var layer = LayerFromStack();
                if (currentEyeIndex == 0 && seenCallers.Count < 16 && seenCallers.Add(layer.Name))
                    VRManager.Log($"[VrSpriteBatchPatch] {layer.Name} depth {layer.Depth:0.00}");
                if (layer.Depth < 0f)
                    return null;

                float dx = VrDepth.Shift(currentEyeIndex, layer.Depth);
                // First person: the camera already pans with the head; near layers take
                // more of the pan back so depth planes separate as the head moves.
                dx += VrDepth.PanShiftX(layer.Depth);
                float dy = VrDepth.PanShiftY(layer.Depth);
                if (dx == 0f && dy == 0f)
                    return null;
                var shifted = CreateXnaMatrix(ReadMatrix(matrixObj) * System.Numerics.Matrix4x4.CreateTranslation(dx, dy, 0f));
                return Activator.CreateInstance(typeof(Nullable<>).MakeGenericType(matrixType), shifted);
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrSpriteBatchPatch] Error in Begin prefix: {ex.Message}");
                return null;
            }
        }

        private static MethodInfo CreatePrefix(Type nullableMatrix)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BarotraumaVR.SpritePrefix"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("prefix");
            var type = module.DefineType("BeginPrefix", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var method = type.DefineMethod("Begin_Prefix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { nullableMatrix.MakeByRefType() });
            method.DefineParameter(1, ParameterAttributes.None, "transformMatrix");

            var il = method.GetILGenerator();
            var skip = il.DefineLabel();
            var replace = typeof(VrSpriteBatchPatch).GetMethod(nameof(ReplacementOrNull), BindingFlags.Public | BindingFlags.Static)!;

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldobj, nullableMatrix);
            il.Emit(OpCodes.Box, nullableMatrix);
            il.Emit(OpCodes.Call, replace);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brfalse_S, skip);
            il.Emit(OpCodes.Unbox_Any, nullableMatrix);
            il.Emit(OpCodes.Stobj, nullableMatrix);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(skip);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);

            return type.CreateType()!.GetMethod("Begin_Prefix")!;
        }

        private static bool IsHudPass()
        {
            var trace = new StackTrace(false);
            bool draw = false;
            for (int i = 0; i < trace.FrameCount && i < 24; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                var type = method?.DeclaringType;
                if (type == null)
                    continue;
                if (type.Name == "GameScreen" && method!.Name == "DrawMap")
                    return false;
                if (type.Name is "Inventory" or "GUI" or "CharacterHUD")
                    return true;
                if (type.Name == "GameScreen" && method!.Name == "Draw")
                    draw = true;
            }
            return draw;
        }

        private readonly struct Layer
        {
            public Layer(float depth, string name)
            {
                Depth = depth;
                Name = name;
            }

            public float Depth { get; }
            public string Name { get; }
        }

        /// <summary>
        /// Pick a depth from the whole call stack. DrawObjects is inside DrawObjectsBack,
        /// and DrawMap is on every world stack, so the nearest frame alone is the wrong layer.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Layer LayerFromStack()
        {
            if (VrWallDepth.Drawing)
                return new Layer(VrWallDepth.OverlayDepth, VrWallDepth.OverlayName);

            var trace = new StackTrace(false);
            int rank = 0;
            float depth = -1f;
            string name = "?";
            bool light = false;
            bool gameScreen = false;

            for (int i = 0; i < trace.FrameCount && i < 24; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                var type = method?.DeclaringType;
                if (type == null)
                    continue;
                if (type.Assembly == typeof(VrSpriteBatchPatch).Assembly || type.Name is "BeginPrefix" or "SpriteBatch" or "SyncProxy")
                    continue;
                string ns = type.Namespace ?? "";
                if (ns.StartsWith("HarmonyLib", StringComparison.Ordinal) || ns.StartsWith("System", StringComparison.Ordinal))
                    continue;

                string label = type.Name + "." + method!.Name;
                if (label.Contains("DrawSpeechBubbles", StringComparison.Ordinal))
                    return new Layer(-1f, "Speech");

                if (rank < 80 && label.Contains("DrawObjectsBack", StringComparison.Ordinal))
                    (rank, depth, name) = (80, VrDepth.CaveFar, "CaveBack");
                else if (rank < 80 && label.Contains("DrawObjectsMid", StringComparison.Ordinal))
                    (rank, depth, name) = (80, VrDepth.CaveMid, "CaveMid");
                else if (rank < 80 && label.Contains("DrawObjectsFront", StringComparison.Ordinal))
                    (rank, depth, name) = (80, VrDepth.CaveFront, "CaveFront");
                else if (rank < 70 && (label.Contains("DrawForeground", StringComparison.Ordinal) || label.Contains("DrawFront", StringComparison.Ordinal)))
                    (rank, depth, name) = (70, VrDepth.CaveFront, "CaveFront");
                else if (rank < 60 && (label.Contains("DrawBackground", StringComparison.Ordinal) || label.Contains("DrawBack", StringComparison.Ordinal)))
                    (rank, depth, name) = (60, VrDepth.CaveFar, "CaveBack");
                else if (rank < 50 && label.Contains("DrawObjects", StringComparison.Ordinal))
                    (rank, depth, name) = (50, VrDepth.CaveFar, "CaveBack");
                else if (rank < 40 && label.Contains("WaterRenderer", StringComparison.Ordinal))
                    (rank, depth, name) = (40, VrDepth.WaterBack, "Water");

                if (label.Contains("LightManager", StringComparison.Ordinal))
                    light = true;
                if (label.Contains("GameScreen", StringComparison.Ordinal))
                    gameScreen = true;
            }

            if (rank == 60)
            {
                // DrawBackground begins twice: far decorations, then the closer cave sprites.
                int n = caveBatch++;
                return n == 0 ? new Layer(VrDepth.CaveFar, "CaveFar") : new Layer(VrDepth.CaveMid, "CaveMid");
            }
            if (rank > 0)
                return new Layer(depth, name);
            if (light)
                return new Layer(VrDepth.Lights, "Lights");
            if (!gameScreen)
                return new Layer(-1f, name);

            int batch = drawMapBatch++;
            // One plane for the whole solid shell: wall backs, the damageable
            // hull, interior walls and the characters share one Begin batch with
            // structures, and front structures follow. If any of these sat at a
            // different depth, the stereo shift would open seams between the
            // overlapping sprites of a single wall (walls looked see-through).
            float drawDepth = batch switch
            {
                0 => VrDepth.Hull,   // wall backs behind the crew
                1 => VrDepth.Hull,   // damageable hull
                2 => VrDepth.Hull,   // outpost drawn behind the sub
                3 => 0.45f,          // back particles
                4 => 0.45f,
                5 => VrDepth.Hull,   // interior structures and characters (one batch)
                6 => VrDepth.Hull,   // deformable characters
                7 => VrDepth.Hull,
                8 => VrDepth.Hull,
                9 => VrDepth.Hull,
                10 => VrDepth.Hull,  // structures in front of the water
                11 => VrDepth.Hull,
                _ => VrDepth.Hull
            };
            return new Layer(drawDepth, "DrawMap#" + batch);
        }

        private static Matrix4x4 ReadMatrix(object value)
        {
            float F(string name) => (float)value.GetType().GetField(name)!.GetValue(value)!;
            return new Matrix4x4(
                F("M11"), F("M12"), F("M13"), F("M14"),
                F("M21"), F("M22"), F("M23"), F("M24"),
                F("M31"), F("M32"), F("M33"), F("M34"),
                F("M41"), F("M42"), F("M43"), F("M44"));
        }

        private static object CreateXnaMatrix(Matrix4x4 m)
        {
            var matrixType = VRManager.FindType("Microsoft.Xna.Framework.Matrix");
            if (matrixType == null)
                throw new InvalidOperationException("Matrix type not found");

            // Create via constructor: Matrix(m11, m12, ..., m44)
            var constructor = matrixType.GetConstructor(new Type[] {
                typeof(float), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float)
            });

            if (constructor != null)
            {
                return constructor.Invoke(new object[] {
                    m.M11, m.M12, m.M13, m.M14,
                    m.M21, m.M22, m.M23, m.M24,
                    m.M31, m.M32, m.M33, m.M34,
                    m.M41, m.M42, m.M43, m.M44
                });
            }

            var box = Activator.CreateInstance(matrixType)!;
            void Set(string name, float value)
            {
                var field = matrixType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                field?.SetValue(box, value);
            }
            Set("M11", m.M11); Set("M12", m.M12); Set("M13", m.M13); Set("M14", m.M14);
            Set("M21", m.M21); Set("M22", m.M22); Set("M23", m.M23); Set("M24", m.M24);
            Set("M31", m.M31); Set("M32", m.M32); Set("M33", m.M33); Set("M34", m.M34);
            Set("M41", m.M41); Set("M42", m.M42); Set("M43", m.M43); Set("M44", m.M44);
            return box;
        }
    }
}
