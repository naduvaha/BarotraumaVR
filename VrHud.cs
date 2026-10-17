using System;
using System.Reflection;
using HarmonyLib;

namespace BarotraumaVR
{
    /// <summary>
    /// HUD is not part of the stereo world. Both eyes share one unshifted panel
    /// composited onto the diorama picture after the world copy.
    /// </summary>
    internal static class VrHud
    {
        public static bool SkipWorld { get; set; }

        private static Harmony? pendingHarmony;
        private static bool applied;
        private static bool bound;
        private static bool failed;
        private static bool loggedReady;
        private static object? hudRt;
        private static int rtW;
        private static int rtH;
        private static MethodInfo? setRenderTarget;
        private static MethodInfo? clear;
        private static MethodInfo? begin;
        private static MethodInfo? end;
        private static MethodInfo? drawTexture;
        private static MethodInfo? drawHud;
        private static MethodInfo? guiDraw;
        private static MethodInfo? drawCursor;
        private static MethodInfo? sessionDraw;
        private static MethodInfo? indicators;
        private static ConstructorInfo? vector2Ctor;
        private static object? white;
        private static object? transparent;
        private static object? deferred;
        private static object? nonPremultiplied;
        private static object? sampler;
        private static object? scissor;
        private static PropertyInfo? controlledProp;
        private static PropertyInfo? sessionProp;
        private static PropertyInfo? camProp;

        public static void Retry()
        {
            if (!applied && pendingHarmony != null)
                Apply(pendingHarmony);
        }

        public static void Apply(Harmony harmony)
        {
            pendingHarmony = harmony;
            if (applied)
                return;

            try
            {
                var skip = typeof(VrHud).GetMethod(nameof(SkipPrefix), BindingFlags.Public | BindingFlags.Static);
                PatchByName(harmony, "Barotrauma.Character", "DrawHUD", skip);
                PatchByName(harmony, "Barotrauma.Character", "DrawGUIMessages", skip);
                PatchByName(harmony, "Barotrauma.CharacterHUD", "Draw", skip);
                PatchByName(harmony, "Barotrauma.CharacterHealth", "DrawHUD", skip);
                PatchByName(harmony, "Barotrauma.CharacterInventory", "DrawOwn", skip);
                PatchByName(harmony, "Barotrauma.Inventory", "Draw", skip);
                PatchByName(harmony, "Barotrauma.Inventory", "DrawFront", skip);
                PatchByName(harmony, "Barotrauma.GUI", "Draw", skip);
                PatchByName(harmony, "Barotrauma.GameSession", "Draw", skip);
                PatchByName(harmony, "Barotrauma.GameScreen", "DrawPositionIndicators", skip);
                PatchByName(harmony, "Barotrauma.InteractionLabelManager", "DrawLabels", skip);
                PatchByName(harmony, "Barotrauma.Item", "DrawHUD", skip);
                applied = true;
                VRManager.Log("[VrHud] HUD and interaction draws are skipped in first person");
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrHud] Patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static bool SkipPrefix()
        {
            if (failed)
                return true;
            if (SkipWorld)
                return false;
            if (VrFirstPerson.Active && !VrFirstPerson.HudVisible)
                return false;
            return true;
        }

        public static void Capture(object gameScreen, object graphics, object spriteBatch, bool cursorOnly = false)
        {
            if (failed || gameScreen == null || graphics == null || spriteBatch == null)
                return;

            try
            {
                if (!Ensure(graphics, spriteBatch, gameScreen))
                    return;
                if (!EnsureTarget(graphics))
                    return;

                setRenderTarget!.Invoke(graphics, new[] { hudRt });
                clear!.Invoke(graphics, new[] { transparent });

                begin!.Invoke(spriteBatch, new[] { deferred, nonPremultiplied, sampler, null, scissor, null, null });
                if (cursorOnly)
                    DrawGameCursor(spriteBatch);
                else
                    DrawHudCalls(gameScreen, spriteBatch);
                end!.Invoke(spriteBatch, null);

                setRenderTarget.Invoke(graphics, new object?[] { null });

                IntPtr tex = TexturePointer(hudRt!);
                if (tex != IntPtr.Zero)
                    XrBridge.bvr_xr_set_hud_texture(tex);

                if (!loggedReady)
                {
                    loggedReady = true;
                    VRManager.Log($"[VrHud] Panel {rtW}x{rtH} composited over the diorama, not inside it");
                }
            }
            catch (Exception ex)
            {
                failed = true;
                VRManager.Log($"[VrHud] Capture failed: {ex.GetType().Name}: {ex.Message}");
                try { setRenderTarget?.Invoke(graphics, new object?[] { null }); } catch { }
            }
        }

        public static void DrawHudOnMonitor(object gameScreen, object graphics, object spriteBatch)
        {
            bool cursorOnly = VrFirstPerson.Active && !VrFirstPerson.HudVisible;
            if ((!VrFirstPerson.HudVisible && !cursorOnly) || failed || gameScreen == null || spriteBatch == null)
                return;

            try
            {
                if (!Ensure(graphics, spriteBatch, gameScreen) || begin == null || end == null)
                    return;
                begin.Invoke(spriteBatch, new[] { deferred, nonPremultiplied, sampler, null, scissor, null, null });
                if (cursorOnly)
                    DrawGameCursor(spriteBatch);
                else
                    DrawHudCalls(gameScreen, spriteBatch);
                end.Invoke(spriteBatch, null);
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrHud] Monitor HUD failed: {ex.Message}");
            }
        }

        private static void DrawHudCalls(object gameScreen, object spriteBatch)
        {
            object? cam = camProp?.GetValue(gameScreen);
            object? controlled = controlledProp?.GetValue(null);
            if (controlled != null && drawHud != null && cam != null)
            {
                var args = drawHud.GetParameters();
                if (args.Length >= 3)
                    drawHud.Invoke(controlled, new[] { spriteBatch, cam, true });
                else
                    drawHud.Invoke(controlled, new[] { spriteBatch, cam });
            }

            object? session = sessionProp?.GetValue(null);
            sessionDraw?.Invoke(session, new[] { spriteBatch });

            if (indicators != null && controlled == null)
                indicators.Invoke(gameScreen, new[] { spriteBatch });

            if (guiDraw != null && cam != null)
                guiDraw.Invoke(null, new[] { cam, spriteBatch });
        }

        private static void DrawGameCursor(object spriteBatch)
        {
            try
            {
                if (drawCursor == null)
                {
                    drawCursor = Find("Barotrauma.GUI")?.GetMethod("DrawCursor", BindingFlags.NonPublic | BindingFlags.Static);
                    if (drawCursor == null)
                        return;
                }
                drawCursor.Invoke(null, new[] { spriteBatch });
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrHud] Cursor draw failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool Ensure(object graphics, object spriteBatch, object gameScreen)
        {
            if (bound)
                return setRenderTarget != null && begin != null && !failed;

            try
            {
                var gdType = graphics.GetType();
                var sbType = spriteBatch.GetType();
                var gdAsm = gdType.Assembly;
                var rtType = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.RenderTarget2D");
                var sfType = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.SurfaceFormat");
                var dfType = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.DepthFormat");
                var colorType = gdAsm.GetType("Microsoft.Xna.Framework.Color")
                    ?? Find("Microsoft.Xna.Framework.Color");
                var vec2Type = gdAsm.GetType("Microsoft.Xna.Framework.Vector2")
                    ?? Find("Microsoft.Xna.Framework.Vector2");
                var sortMode = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.SpriteSortMode");
                var blendState = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.BlendState");
                var samplerState = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.SamplerState");
                if (rtType == null || colorType == null || vec2Type == null)
                {
                    failed = true;
                    VRManager.Log("[VrHud] RenderTarget types missing");
                    bound = true;
                    return false;
                }

                setRenderTarget = gdType.GetMethod("SetRenderTarget", new[] { rtType });
                clear = gdType.GetMethod("Clear", new[] { colorType });
                end = sbType.GetMethod("End", Type.EmptyTypes);
                vector2Ctor = vec2Type.GetConstructor(new[] { typeof(float), typeof(float) });
                drawTexture = sbType.GetMethod("Draw", new[] { rtType.BaseType ?? rtType, vec2Type, colorType })
                    ?? FindDraw(sbType, rtType, vec2Type, colorType);

                var nullableMatrix = begin7(sbType);
                begin = nullableMatrix;

                deferred = sortMode != null ? Enum.ToObject(sortMode, 0) : null;
                nonPremultiplied = ReadStatic(blendState, "NonPremultiplied");
                sampler = ReadStatic(samplerState, "LinearClamp")
                    ?? Find("Barotrauma.GUI")?.GetProperty("SamplerState", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                scissor = Find("Barotrauma.GameMain")?.GetField("ScissorTestEnable", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? Find("Barotrauma.GameMain")?.GetProperty("ScissorTestEnable", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

                var intColor = colorType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                var byteColor = colorType.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                var colorCtor = intColor ?? byteColor;
                if (colorCtor != null)
                {
                    bool bytes = colorCtor.GetParameters()[0].ParameterType == typeof(byte);
                    object Box(int r, int g, int b, int a) => bytes
                        ? colorCtor.Invoke(new object[] { (byte)r, (byte)g, (byte)b, (byte)a })
                        : colorCtor.Invoke(new object[] { r, g, b, a });
                    white = Box(255, 255, 255, 255);
                    transparent = Box(0, 0, 0, 0);
                }

                var character = Find("Barotrauma.Character");
                controlledProp = character?.GetProperty("Controlled", BindingFlags.Public | BindingFlags.Static);
                drawHud = character?.GetMethod("DrawHUD", BindingFlags.Public | BindingFlags.Instance);
                var gui = Find("Barotrauma.GUI");
                guiDraw = gui?.GetMethod("Draw", BindingFlags.Public | BindingFlags.Static);
                var session = Find("Barotrauma.GameSession");
                sessionDraw = session?.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance);
                var gameMain = Find("Barotrauma.GameMain");
                sessionProp = gameMain?.GetProperty("GameSession", BindingFlags.Public | BindingFlags.Static);
                camProp = gameScreen.GetType().GetProperty("Cam");
                indicators = gameScreen.GetType().GetMethod("DrawPositionIndicators", BindingFlags.NonPublic | BindingFlags.Instance);

                bound = true;
                if (setRenderTarget == null || clear == null || begin == null || end == null || transparent == null)
                {
                    failed = true;
                    VRManager.Log($"[VrHud] Bind incomplete rt={setRenderTarget != null} begin={begin != null}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                bound = true;
                failed = true;
                VRManager.Log($"[VrHud] Bind failed: {ex.Message}");
                return false;
            }
        }

        private static bool EnsureTarget(object graphics)
        {
            int w = 1920, h = 1080;
            var gameMain = Find("Barotrauma.GameMain");
            object? gw = gameMain?.GetProperty("GraphicsWidth", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object? gh = gameMain?.GetProperty("GraphicsHeight", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (gw != null) w = Convert.ToInt32(gw);
            if (gh != null) h = Convert.ToInt32(gh);
            if (hudRt != null && rtW == w && rtH == h)
                return true;

            var gdAsm = graphics.GetType().Assembly;
            var rtType = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.RenderTarget2D");
            var sfType = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.SurfaceFormat");
            var dfType = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.DepthFormat");
            if (rtType == null || sfType == null || dfType == null)
                return false;

            object color = Enum.ToObject(sfType, 0);
            object none = Enum.ToObject(dfType, 0);
            var ctor = rtType.GetConstructor(new[]
            {
                graphics.GetType(), typeof(int), typeof(int), typeof(bool), sfType, dfType
            });
            if (ctor == null)
            {
                ctor = rtType.GetConstructor(new[] { graphics.GetType(), typeof(int), typeof(int) });
                hudRt = ctor?.Invoke(new object[] { graphics, w, h });
            }
            else
            {
                hudRt = ctor.Invoke(new object[] { graphics, w, h, false, color, none });
            }

            if (hudRt == null)
            {
                failed = true;
                VRManager.Log("[VrHud] RenderTarget2D was not created");
                return false;
            }

            rtW = w;
            rtH = h;
            VRManager.Log($"[VrHud] HUD target {w}x{h}");
            return true;
        }

        private static MethodInfo? begin7(Type spriteBatchType)
        {
            foreach (var method in spriteBatchType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "Begin")
                    continue;
                var p = method.GetParameters();
                if (p.Length == 7)
                    return method;
            }
            return spriteBatchType.GetMethod("Begin", Type.EmptyTypes);
        }

        private static MethodInfo? FindDraw(Type spriteBatchType, Type textureType, Type vec2Type, Type colorType)
        {
            foreach (var method in spriteBatchType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "Draw")
                    continue;
                var p = method.GetParameters();
                if (p.Length == 3 && p[1].ParameterType == vec2Type && p[2].ParameterType == colorType)
                    return method;
            }
            return null;
        }

        private static IntPtr TexturePointer(object texture)
        {
            for (var type = texture.GetType(); type != null; type = type.BaseType)
            {
                object? resource = type.GetField("_texture", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(texture)
                    ?? type.GetField("texture", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(texture);
                if (resource?.GetType().GetProperty("NativePointer")?.GetValue(resource) is IntPtr p && p != IntPtr.Zero)
                    return p;
            }

            object? handle = texture.GetType().GetProperty("Handle")?.GetValue(texture);
            if (handle?.GetType().GetProperty("NativePointer")?.GetValue(handle) is IntPtr h && h != IntPtr.Zero)
                return h;
            return IntPtr.Zero;
        }

        private static void PatchByName(Harmony harmony, string typeName, string methodName, MethodInfo? skip)
        {
            if (skip == null)
                return;
            var type = Find(typeName);
            if (type == null)
                return;
            int n = 0;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.Name != methodName || method.IsAbstract)
                    continue;
                harmony.Patch(method, prefix: new HarmonyMethod(skip));
                n++;
            }
            if (n > 0)
                VRManager.Log($"[VrHud] skip {type.Name}.{methodName}");
        }

        private static object? ReadStatic(Type? type, string name)
        {
            if (type == null)
                return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            return type.GetField(name, flags)?.GetValue(null) ?? type.GetProperty(name, flags)?.GetValue(null);
        }

        private static Type? Find(string fullName)
        {
            var named = Type.GetType(fullName + ", Barotrauma", throwOnError: false);
            return named ?? VRManager.FindType(fullName);
        }
    }
}
