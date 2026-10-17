using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BarotraumaVR
{
    /// <summary>
    /// Phase 4 visual prototype: give cave walls stereo depth without extruding
    /// physics or allocating extra render targets.
    /// Textured walls (RenderWalls / BasicEffect) skip SpriteBatch, so they are
    /// shifted through Camera.shaderTransform. Solid cave faces are then outlined
    /// with GUI.DrawLine in the same layer, behind the submarine.
    /// </summary>
    internal static class VrWallDepth
    {
        public const float TextureDepth = VrDepth.CaveWalls;
        internal const float HullBack = 0.78f;
        internal const float HullFront = 0.46f;

        internal static bool Drawing { get; private set; }
        internal static float OverlayDepth { get; private set; } = VrDepth.CaveWalls;
        internal static string OverlayName { get; private set; } = "CaveWalls";

        private static Harmony? pendingHarmony;
        private static bool applied;
        private static bool cameraBound;
        private static bool drawBound;
        private static bool loggedReady;
        private static bool loggedFailure;
        private static int missingTypeLogs;
        private static object? savedShader;
        private static FieldInfo? shaderField;
        private static MethodInfo? createTranslation;
        private static MethodInfo? multiply;
        private static MethodInfo? getCells;
        private static MethodInfo? drawLine;
        private static MethodInfo? drawLineSprite;
        private static MethodInfo? begin7;
        private static MethodInfo? spriteEnd;
        private static PropertyInfo? transformProp;
        private static PropertyInfo? zoomProp;
        private static PropertyInfo? worldViewCenterProp;
        private static PropertyInfo? loadedLevel;
        private static ConstructorInfo? vector2Ctor;
        private static ConstructorInfo? colorCtor;
        private static ConstructorInfo? nullableMatrixCtor;
        private static FieldInfo? point1Field;
        private static FieldInfo? point2Field;
        private static FieldInfo? isSolidField;
        private static FieldInfo? nextToCaveField;
        private static FieldInfo? edgesField;
        private static FieldInfo? translationField;
        private static FieldInfo? bodyVerticesField;
        private static FieldInfo? vecX;
        private static FieldInfo? vecY;
        private static object? deferred;
        private static object? nonPremultiplied;
        private static object? linearClamp;
        private static object? linearWrap;
        private static object? depthNone;
        private static object? lineColor;
        private static object? fallbackColor;
        private static object? hullColor;
        private static object? roomColor;
        private static object? waterColor;
        private static object? waterBackColor;
        private static object? fireColor;
        private static object? itemColor;
        private static object? focusColor;
        private static object? characterColor;
        private static object? rockColor;
        private static MethodInfo? getNormal;
        private static PropertyInfo? cellCenter;

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
                var rendererType = FindBarotraumaType("Barotrauma.LevelRenderer");
                if (rendererType == null)
                {
                    if (missingTypeLogs++ < 3)
                        VRManager.Log("[VrWallDepth] LevelRenderer type is not loaded yet");
                    return;
                }

                var renderWalls = rendererType.GetMethod("RenderWalls", BindingFlags.Public | BindingFlags.Instance);
                var drawOverlay = rendererType.GetMethod("DrawDebugOverlay", BindingFlags.Public | BindingFlags.Instance);
                if (renderWalls == null || drawOverlay == null)
                {
                    VRManager.Log("[VrWallDepth] LevelRenderer.RenderWalls/DrawDebugOverlay was not found");
                    return;
                }

                harmony.Patch(renderWalls,
                    prefix: new HarmonyMethod(typeof(VrWallDepth).GetMethod(nameof(RenderWalls_Prefix), BindingFlags.Public | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(VrWallDepth).GetMethod(nameof(RenderWalls_Postfix), BindingFlags.Public | BindingFlags.Static)));
                harmony.Patch(drawOverlay,
                    postfix: new HarmonyMethod(typeof(VrWallDepth).GetMethod(nameof(DrawDebugOverlay_Postfix), BindingFlags.Public | BindingFlags.Static)));
                applied = true;
                VrHullExtrude.Apply(harmony);
                VRManager.Log($"[VrDepth] World stereo {VrDepth.Pixels:0} px, cave {VrDepth.CaveWalls:0.00} behind hull {VrDepth.Hull:0.00}");
                VRManager.Log("[VrWallDepth] Cave wall stereo hooked on LevelRenderer");
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrWallDepth] Patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void RenderWalls_Prefix(object cam)
        {
            savedShader = null;
            if (!VRManager.IsVRActive || cam == null)
                return;
            if (!EnsureCamera(cam) || shaderField == null || createTranslation == null || multiply == null)
                return;

            int eye = VrSpriteBatchPatch.CurrentEye;
            if (eye < 0 || eye > 1)
                return;
            object? level = VRManager.FindType("Barotrauma.Level")?.GetProperty("Loaded", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (level == null)
                return;

            try
            {
                savedShader = shaderField.GetValue(cam);
                if (savedShader == null)
                    return;

                float dx = VrDepth.Shift(eye, TextureDepth);
                object translation = createTranslation.Invoke(null, new object[] { dx, 0f, 0f })!;
                shaderField.SetValue(cam, multiply.Invoke(null, new[] { savedShader, translation }));
            }
            catch (Exception ex)
            {
                savedShader = null;
                LogOnce($"RenderWalls shift failed: {ex.Message}");
            }
        }

        public static void RenderWalls_Postfix(object cam)
        {
            if (savedShader == null || shaderField == null || cam == null)
                return;

            try
            {
                shaderField.SetValue(cam, savedShader);
            }
            catch
            {
            }
            finally
            {
                savedShader = null;
            }
        }

        public static void DrawDebugOverlay_Postfix(object spriteBatch, object cam)
        {
        }

        private static void BeginLayer(object spriteBatch, object cam, float depth, string name)
        {
            OverlayDepth = depth;
            OverlayName = name;
            object? transform = transformProp?.GetValue(cam);
            object? nullable = transform != null && nullableMatrixCtor != null
                ? nullableMatrixCtor.Invoke(new[] { transform })
                : null;
            begin7!.Invoke(spriteBatch, new[]
            {
                deferred,
                nonPremultiplied,
                linearWrap ?? linearClamp,
                depthNone,
                null,
                null,
                nullable
            });
        }

        private static void RestoreGameBatch(object spriteBatch, object cam)
        {
            OverlayDepth = VrDepth.CaveWalls;
            OverlayName = "CaveWalls";
            try
            {
                BeginLayer(spriteBatch, cam, 1.05f, "DrawMapRestore");
            }
            catch
            {
            }
        }

        private static List<object> CollectAllCells(object level)
        {
            var raw = new List<object>();
            var getAll = level.GetType().GetMethod("GetAllCells", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getAll?.Invoke(level, null) is IEnumerable all)
            {
                foreach (var cell in all)
                {
                    if (cell != null)
                        raw.Add(cell);
                }
            }

            foreach (var name in new[] { "ExtraWalls", "UnsyncedExtraWalls" })
            {
                object? walls = level.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(level)
                    ?? level.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(level);
                if (walls is not IEnumerable wallList)
                    continue;
                foreach (var wall in wallList)
                {
                    if (wall == null)
                        continue;
                    object? cells = wall.GetType().GetProperty("Cells", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(wall)
                        ?? wall.GetType().GetField("Cells", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(wall);
                    if (cells is not IEnumerable cellList)
                        continue;
                    foreach (var cell in cellList)
                    {
                        if (cell != null)
                            raw.Add(cell);
                    }
                }
            }

            return raw;
        }

        private static void DrawCameraCross(object spriteBatch, object cam)
        {
            GetViewHalfExtents(cam, out float cx, out float cy, out _, out _, out _, out _);
            const float arm = 70f;
            DrawWorldLine(spriteBatch, cx - arm, cy, cx + arm, cy);
            DrawWorldLine(spriteBatch, cx, cy - arm, cx, cy + arm);
        }

        private static void DrawWorldLine(object spriteBatch, float x1, float y1, float x2, float y2, object? color = null, float width = 8f, object? sprite = null)
        {
            object start = vector2Ctor!.Invoke(new object[] { x1, -y1 });
            object end = vector2Ctor.Invoke(new object[] { x2, -y2 });
            object tint = color ?? lineColor!;
            if (sprite != null && drawLineSprite != null)
            {
                drawLineSprite.Invoke(null, new object[] { spriteBatch, sprite, start, end, tint, 0f, (int)MathF.Max(1f, width) });
                return;
            }
            drawLine!.Invoke(null, new[] { spriteBatch, start, end, tint, 0f, width });
        }

        private static object? CaveSprite(object level)
        {
            object? gen = level.GetType().GetProperty("GenerationParams")?.GetValue(level);
            return gen?.GetType().GetProperty("WallEdgeSprite")?.GetValue(gen)
                ?? gen?.GetType().GetProperty("WallSprite")?.GetValue(gen);
        }

        private static object? CaveTint(object level)
        {
            object? color = level.GetType().GetProperty("WallColor")?.GetValue(level);
            if (color != null)
                return color;
            object? gen = level.GetType().GetProperty("GenerationParams")?.GetValue(level);
            return gen?.GetType().GetProperty("WallColor")?.GetValue(gen);
        }

        private static int DrawSubmarineWalls(object spriteBatch, object cam)
        {
            var structureType = FindBarotraumaType("Barotrauma.Structure");
            var mapEntityType = FindBarotraumaType("Barotrauma.MapEntity");
            object? list = mapEntityType?.GetField("MapEntityList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? structureType?.GetField("WallList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? structureType?.GetProperty("WallList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (list is not IEnumerable entities || structureType == null)
                return 0;

            GetViewHalfExtents(cam, out float cx, out float cy, out float halfW, out float halfH, out _, out _);
            int drawn = 0;
            int bodySeen = 0;
            int bgSeen = 0;
            int skippedHidden = 0;

            foreach (var entity in entities)
            {
                if (entity == null || !structureType.IsInstanceOfType(entity))
                    continue;
                var type = entity.GetType();
                if (type.GetProperty("Removed")?.GetValue(entity) is true)
                    continue;
                if (type.GetProperty("HiddenInGame")?.GetValue(entity) is true
                    || type.GetProperty("IsHidden")?.GetValue(entity) is true)
                {
                    skippedHidden++;
                    continue;
                }
                if (type.GetProperty("Submarine")?.GetValue(entity) == null
                    && type.GetField("submarine", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(entity) == null)
                    continue;

                bool hasBody = type.GetProperty("HasBody")?.GetValue(entity) is true;
                object? sections = type.GetProperty("Sections")?.GetValue(entity);
                int sectionDrawn = 0;
                if (sections is Array arr && arr.Length > 0)
                {
                    foreach (var section in arr)
                    {
                        if (section == null)
                            continue;
                        if (!TryWorldRect(section, out float sx, out float sy, out float sw, out float sh))
                            continue;
                        if (!RectOverlapsView(sx, sy, sw, sh, cx, cy, halfW, halfH))
                            continue;
                        DrawRect(spriteBatch, sx, sy, sw, sh, hullColor, hasBody ? 6f : 4f);
                        sectionDrawn++;
                    }
                }

                if (sectionDrawn == 0)
                {
                    if (!TryWorldRect(entity, out float x, out float y, out float w, out float h))
                        continue;
                    if (!RectOverlapsView(x, y, w, h, cx, cy, halfW, halfH))
                        continue;
                    DrawRect(spriteBatch, x, y, w, h, hullColor, hasBody ? 6f : 4f);
                    sectionDrawn = 1;
                }

                if (hasBody)
                    bodySeen++;
                else
                    bgSeen++;
                drawn += sectionDrawn;
            }

            if (!loggedReady)
                VRManager.Log($"[VrWallDepth] Hull structures in view body={bodySeen} background={bgSeen} hidden={skippedHidden}");

            return drawn;
        }

        private static int DrawHullRooms(object spriteBatch, object cam)
        {
            var hullType = FindBarotraumaType("Barotrauma.Hull");
            object? list = hullType?.GetField("HullList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? hullType?.GetProperty("HullList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (list is not IEnumerable rooms)
                return 0;

            GetViewHalfExtents(cam, out float cx, out float cy, out float halfW, out float halfH, out _, out _);
            int drawn = 0;
            foreach (var room in rooms)
            {
                if (room == null)
                    continue;
                var type = room.GetType();
                if (type.GetProperty("Removed")?.GetValue(room) is true)
                    continue;
                if (type.GetProperty("HiddenInGame")?.GetValue(room) is true)
                    continue;
                if (!TryWorldRect(room, out float x, out float y, out float w, out float h))
                    continue;
                if (!RectOverlapsView(x, y, w, h, cx, cy, halfW, halfH))
                    continue;

                DrawRect(spriteBatch, x, y, w, h, roomColor, 5f);
                drawn++;
            }

            return drawn;
        }

        private static bool RectOverlapsView(float x, float y, float w, float h, float cx, float cy, float halfW, float halfH)
        {
            return x < cx + halfW
                && x + w > cx - halfW
                && y > cy - halfH
                && y - h < cy + halfH;
        }

        private static int DrawInteractableItems(object spriteBatch, object cam)
        {
            var itemType = FindBarotraumaType("Barotrauma.Item");
            object? list = itemType?.GetField("ItemList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (list is not IEnumerable items)
                return 0;

            object? controlled = FindBarotraumaType("Barotrauma.Character")?.GetProperty("Controlled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object? focused = controlled?.GetType().GetProperty("FocusedItem")?.GetValue(controlled);
            var isInteractable = itemType?.GetMethod("IsInteractable", BindingFlags.Public | BindingFlags.Instance);

            GetViewHalfExtents(cam, out float cx, out float cy, out float halfW, out float halfH, out _, out _);
            float maxDx = halfW + 200f;
            float maxDy = halfH + 200f;
            int drawn = 0;

            foreach (var item in items)
            {
                if (item == null || drawn > 250)
                    continue;
                var type = item.GetType();
                if (type.GetProperty("ParentInventory")?.GetValue(item) != null)
                    continue;
                if (type.GetProperty("IsHidden")?.GetValue(item) is true)
                    continue;
                if (type.GetProperty("NonInteractable")?.GetValue(item) is true)
                    continue;
                bool special = IsDoorOrContainer(item);
                if (!special && isInteractable != null && controlled != null)
                {
                    try
                    {
                        if (isInteractable.Invoke(item, new[] { controlled }) is not true)
                            continue;
                    }
                    catch
                    {
                    }
                }
                if (!special && !ItemHasUse(item))
                    continue;

                if (!TryWorldRect(item, out float x, out float y, out float w, out float h))
                {
                    object? pos = type.GetProperty("WorldPosition")?.GetValue(item);
                    if (!TryReadXY(pos, out float px, out float py))
                        continue;
                    x = px - 16f;
                    y = py + 16f;
                    w = 32f;
                    h = 32f;
                }

                if (w > 2200f || h > 2200f)
                    continue;
                float mx = x + w * 0.5f;
                float my = y - h * 0.5f;
                if (Math.Abs(mx - cx) > maxDx || Math.Abs(my - cy) > maxDy)
                    continue;

                bool isFocus = focused != null && ReferenceEquals(focused, item);
                DrawRect(spriteBatch, x, y, w, h, isFocus ? focusColor : itemColor, isFocus ? 5f : 3f);
                drawn++;
            }

            return drawn;
        }

        private static int DrawCharacters(object spriteBatch, object cam)
        {
            var characterType = FindBarotraumaType("Barotrauma.Character");
            object? list = characterType?.GetField("CharacterList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object? controlled = characterType?.GetProperty("Controlled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (list is not IEnumerable characters)
                return 0;

            GetViewHalfExtents(cam, out float cx, out float cy, out float halfW, out float halfH, out _, out _);
            int drawn = 0;
            foreach (var character in characters)
            {
                if (character == null || ReferenceEquals(character, controlled))
                    continue;
                if (character.GetType().GetProperty("Removed")?.GetValue(character) is true)
                    continue;
                object? pos = character.GetType().GetProperty("WorldPosition")?.GetValue(character);
                if (!TryReadXY(pos, out float px, out float py))
                    continue;
                if (Math.Abs(px - cx) > halfW + 200f || Math.Abs(py - cy) > halfH + 200f)
                    continue;

                const float arm = 28f;
                DrawWorldLine(spriteBatch, px - arm, py, px + arm, py, characterColor, 4f);
                DrawWorldLine(spriteBatch, px, py - arm * 1.4f, px, py + arm * 0.6f, characterColor, 4f);
                drawn++;
            }

            return drawn;
        }

        private static bool IsDoorOrContainer(object item)
        {
            if (NameLooksInteractable(item))
                return true;
            foreach (var component in EnumerateComponents(item))
            {
                string name = component.GetType().Name;
                if (name is "Door" or "Controller" or "ItemContainer" or "Ladder" or "Terminal" or "CustomInterface" or "Button")
                    return true;
            }
            return false;
        }

        private static bool NameLooksInteractable(object item)
        {
            string text = ItemSearchText(item);
            return text.Contains("door")
                || text.Contains("hatch")
                || text.Contains("button")
                || text.Contains("switch")
                || text.Contains("lever")
                || text.Contains("locker")
                || text.Contains("cabinet")
                || text.Contains("crate")
                || text.Contains("safe")
                || text.Contains("secure")
                || text.Contains("container");
        }

        private static string ItemSearchText(object item)
        {
            var type = item.GetType();
            string tags = type.GetProperty("Tags")?.GetValue(item)?.ToString() ?? "";
            string name = type.GetProperty("Name")?.GetValue(item)?.ToString() ?? "";
            object? prefab = type.GetProperty("Prefab")?.GetValue(item);
            string id = prefab?.GetType().GetProperty("Identifier")?.GetValue(prefab)?.ToString()
                ?? prefab?.GetType().GetProperty("Name")?.GetValue(prefab)?.ToString()
                ?? "";
            return (tags + " " + name + " " + id).ToLowerInvariant();
        }

        private static IEnumerable<object> EnumerateComponents(object item)
        {
            object? components = item.GetType().GetProperty("Components")?.GetValue(item)
                ?? item.GetType().GetField("components", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(item);
            if (components is not IEnumerable list)
                yield break;
            foreach (var component in list)
            {
                if (component != null)
                    yield return component;
            }
        }

        private static bool ItemHasUse(object item)
        {
            if (IsDoorOrContainer(item))
                return true;

            foreach (var component in EnumerateComponents(item))
            {
                var type = component.GetType();
                object? picked = type.GetProperty("CanBePicked")?.GetValue(component)
                    ?? type.GetField("CanBePicked")?.GetValue(component);
                object? selected = type.GetProperty("CanBeSelected")?.GetValue(component)
                    ?? type.GetField("CanBeSelected")?.GetValue(component);
                if (picked is true || selected is true)
                    return true;
            }

            return false;
        }

        private static int DrawWaterVolumes(object spriteBatch, object cam, bool back)
        {
            var hullType = FindBarotraumaType("Barotrauma.Hull");
            object? list = hullType?.GetField("HullList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? hullType?.GetProperty("HullList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (list is not IEnumerable rooms)
                return 0;

            GetViewHalfExtents(cam, out float cx, out float cy, out float halfW, out float halfH, out _, out _);
            object? tint = back ? waterBackColor : waterColor;
            float rim = back ? 10f : 6f;
            int drawn = 0;
            foreach (var room in rooms)
            {
                if (room == null || drawn > 80)
                    continue;
                var type = room.GetType();
                if (type.GetProperty("Removed")?.GetValue(room) is true)
                    continue;
                object? vol = type.GetProperty("WaterVolume")?.GetValue(room);
                if (vol == null || Convert.ToSingle(vol) < 32f)
                    continue;
                if (!TryWorldRect(room, out float x, out float y, out float w, out float h))
                    continue;
                if (!RectOverlapsView(x, y, w, h, cx, cy, halfW, halfH))
                    continue;

                float floor = y - h;
                float surface = floor;
                object? surf = type.GetProperty("WorldSurface")?.GetValue(room);
                if (surf != null)
                    surface = Convert.ToSingle(surf);
                if (surface < floor + 4f)
                    continue;
                if (surface > y)
                    surface = y;

                if (back)
                {
                    DrawWorldLine(spriteBatch, x, surface, x + w, surface, tint, rim);
                    DrawWorldLine(spriteBatch, x, floor, x + w, floor, tint, 4f);
                }
                else
                {
                    DrawWorldLine(spriteBatch, x, floor, x, surface, tint, rim);
                    DrawWorldLine(spriteBatch, x + w, floor, x + w, surface, tint, rim);
                    DrawWorldLine(spriteBatch, x, surface, x + w, surface, tint, 4f);
                }
                drawn++;
            }
            return drawn;
        }

        private static int DrawFireVolumes(object spriteBatch, object cam)
        {
            var hullType = FindBarotraumaType("Barotrauma.Hull");
            object? list = hullType?.GetField("HullList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? hullType?.GetProperty("HullList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (list is not IEnumerable rooms || fireColor == null)
                return 0;

            GetViewHalfExtents(cam, out float cx, out float cy, out float halfW, out float halfH, out _, out _);
            int drawn = 0;
            foreach (var room in rooms)
            {
                if (room == null || drawn > 24)
                    continue;
                drawn += DrawFiresIn(spriteBatch, room.GetType().GetProperty("FireSources")?.GetValue(room), cx, cy, halfW, halfH);
                drawn += DrawFiresIn(spriteBatch, room.GetType().GetProperty("FakeFireSources")?.GetValue(room), cx, cy, halfW, halfH);
            }
            return drawn;
        }

        private static int DrawFiresIn(object spriteBatch, object? sources, float cx, float cy, float halfW, float halfH)
        {
            if (sources is not IEnumerable list)
                return 0;
            int drawn = 0;
            foreach (var fire in list)
            {
                if (fire == null || drawn > 16)
                    continue;
                var type = fire.GetType();
                if (type.GetProperty("Removed")?.GetValue(fire) is true)
                    continue;
                if (!TryReadXY(type.GetProperty("WorldPosition")?.GetValue(fire), out float px, out float py))
                    continue;
                if (!TryReadXY(type.GetProperty("Size")?.GetValue(fire), out float w, out float h))
                    continue;
                if (w < 8f)
                    continue;
                float flame = h;
                object? fh = type.GetProperty("FlameHeight")?.GetValue(fire);
                if (fh != null)
                    flame = Math.Max(h, Convert.ToSingle(fh) * 0.55f);
                if (Math.Abs(px - cx) > halfW + 200f || Math.Abs(py - cy) > halfH + 200f)
                    continue;

                float top = py;
                float bot = py - flame;
                DrawWorldLine(spriteBatch, px, bot, px, top, fireColor, 5f);
                DrawWorldLine(spriteBatch, px + w, bot, px + w, top, fireColor, 5f);
                DrawWorldLine(spriteBatch, px + w * 0.5f, bot, px + w * 0.5f, top, fireColor, 3f);
                drawn++;
            }
            return drawn;
        }

        private static bool TryWorldRect(object entity, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            object? worldRect = entity.GetType().GetProperty("WorldRect")?.GetValue(entity);
            if (worldRect == null)
                return false;
            var rectType = worldRect.GetType();
            return TryReadFloat(rectType, worldRect, "X", out x)
                && TryReadFloat(rectType, worldRect, "Y", out y)
                && TryReadFloat(rectType, worldRect, "Width", out w)
                && TryReadFloat(rectType, worldRect, "Height", out h);
        }

        private static void DrawRect(object spriteBatch, float x, float y, float w, float h, object? color, float width)
        {
            DrawWorldLine(spriteBatch, x, y, x + w, y, color, width);
            DrawWorldLine(spriteBatch, x + w, y, x + w, y - h, color, width);
            DrawWorldLine(spriteBatch, x + w, y - h, x, y - h, color, width);
            DrawWorldLine(spriteBatch, x, y - h, x, y, color, width);
        }

        private static int DrawOnScreenEdges(object spriteBatch, object cam, IEnumerable cells, object level)
        {
            GetViewHalfExtents(cam, out float cx, out float cy, out float halfW, out float halfH, out int screenW, out int screenH);
            int drawn = 0;
            float minWith = float.MaxValue;
            float minWithout = float.MaxValue;
            // Cave faces sit outside a zoomed-in hull view; keep a wide radius so
            // zooming out or looking at the rock still catches the nearest walls.
            float cutoff = Math.Max(halfW, halfH) * 8f;

            foreach (var cell in cells)
            {
                if (cell == null)
                    continue;

                object? translation = translationField?.GetValue(cell);
                TryReadXY(translation, out float tx, out float ty);
                if (edgesField?.GetValue(cell) is not IEnumerable edges)
                    continue;

                foreach (var edge in edges)
                {
                    if (edge == null)
                        continue;
                    bool solid = isSolidField?.GetValue(edge) is true;

                    object? p1 = point1Field?.GetValue(edge);
                    object? p2 = point2Field?.GetValue(edge);
                    if (p1 == null || p2 == null)
                        continue;

                    TryReadXY(p1, out float x1, out float y1);
                    TryReadXY(p2, out float x2, out float y2);
                    float distRaw = DistanceToSegment(cx, cy, x1, y1, x2, y2);
                    float distMoved = DistanceToSegment(cx, cy, x1 + tx, y1 + ty, x2 + tx, y2 + ty);
                    if (distRaw < minWithout)
                        minWithout = distRaw;
                    if (distMoved < minWith)
                        minWith = distMoved;

                    bool useMoved = distMoved <= distRaw;
                    float dist = useMoved ? distMoved : distRaw;
                    if (!solid || dist > cutoff)
                        continue;

                    float ax = useMoved ? x1 + tx : x1;
                    float ay = useMoved ? y1 + ty : y1;
                    float bx = useMoved ? x2 + tx : x2;
                    float by = useMoved ? y2 + ty : y2;
                    OffsetInward(cell, edge, ax, ay, bx, by, 14f, out float ox, out float oy);
                    object? rock = CaveSprite(level);
                    object? tint = CaveTint(level);
                    DrawWorldLine(spriteBatch, ax + ox, ay + oy, bx + ox, by + oy, tint, 28f, rock);
                    DrawWorldLine(spriteBatch, ax, ay, bx, by, tint, 10f, rock);
                    drawn++;
                }
            }

            if (!loggedReady)
            {
                VRManager.Log($"[VrWallDepth] cam=({cx:0},{cy:0}) view=±{halfW:0}x{halfH:0} screen={screenW}x{screenH} nearestWithT={Fmt(minWith)} nearestNoT={Fmt(minWithout)}");
            }

            return drawn;
        }

        private static string Fmt(float value) => value < float.MaxValue ? value.ToString("0") : "-";

        private static void OffsetInward(object cell, object edge, float x1, float y1, float x2, float y2, float amount, out float ox, out float oy)
        {
            ox = oy = 0f;
            try
            {
                if (getNormal != null)
                {
                    object? n = getNormal.Invoke(edge, new[] { cell });
                    if (TryReadXY(n, out ox, out oy))
                    {
                        float len = MathF.Sqrt(ox * ox + oy * oy);
                        if (len > 0.001f)
                        {
                            ox = ox / len * amount;
                            oy = oy / len * amount;
                            return;
                        }
                    }
                }
            }
            catch
            {
            }

            float dx = x2 - x1;
            float dy = y2 - y1;
            float len2 = MathF.Sqrt(dx * dx + dy * dy);
            if (len2 < 0.001f)
                return;
            ox = -dy / len2 * amount;
            oy = dx / len2 * amount;
            object? center = cellCenter?.GetValue(cell);
            if (TryReadXY(center, out float cx, out float cy))
            {
                float mx = (x1 + x2) * 0.5f;
                float my = (y1 + y2) * 0.5f;
                if ((cx - mx) * ox + (cy - my) * oy < 0f)
                {
                    ox = -ox;
                    oy = -oy;
                }
            }
        }

        private static int DrawBodyLoops(object spriteBatch, IEnumerable cells)
        {
            int drawn = 0;
            foreach (var cell in cells)
            {
                if (cell == null || bodyVerticesField?.GetValue(cell) is not IEnumerable verts)
                    continue;

                object? translation = translationField?.GetValue(cell);
                object? first = null;
                object? prev = null;
                foreach (var vertex in verts)
                {
                    if (vertex == null)
                        continue;
                    first ??= vertex;
                    if (prev != null)
                    {
                        drawLine!.Invoke(null, new[]
                        {
                            spriteBatch,
                            FlipY(prev, translation),
                            FlipY(vertex, translation),
                            fallbackColor,
                            0f,
                            2f
                        });
                        drawn++;
                    }
                    prev = vertex;
                }

                if (first != null && prev != null && !ReferenceEquals(first, prev))
                {
                    drawLine!.Invoke(null, new[]
                    {
                        spriteBatch,
                        FlipY(prev, translation),
                        FlipY(first, translation),
                        fallbackColor,
                        0f,
                        2f
                    });
                    drawn++;
                }
            }

            return drawn;
        }

        private static void GetViewHalfExtents(object cam, out float cx, out float cy, out float halfW, out float halfH, out int screenW, out int screenH)
        {
            object? camCenter = cam.GetType().GetProperty("WorldViewCenter", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cam)
                ?? worldViewCenterProp?.GetValue(cam);
            TryReadXY(camCenter, out cx, out cy);
            float zoom = zoomProp != null ? Convert.ToSingle(zoomProp.GetValue(cam)) : 1f;
            if (zoom < 0.01f)
                zoom = 1f;

            screenW = 1920;
            screenH = 1080;
            var gameMain = FindBarotraumaType("Barotrauma.GameMain") ?? VRManager.FindType("Barotrauma.GameMain");
            if (gameMain != null)
            {
                object? w = gameMain.GetProperty("GraphicsWidth", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                object? h = gameMain.GetProperty("GraphicsHeight", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (w != null)
                    screenW = Convert.ToInt32(w);
                if (h != null)
                    screenH = Convert.ToInt32(h);
            }

            halfW = screenW / zoom * 0.5f + 400f;
            halfH = screenH / zoom * 0.5f + 400f;
        }

        private static object? ReadStatic(Type? type, string name)
        {
            if (type == null)
                return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            return type.GetField(name, flags)?.GetValue(null) ?? type.GetProperty(name, flags)?.GetValue(null);
        }

        private static float DistanceToSegment(float px, float py, float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq < 0.0001f)
            {
                dx = px - x1;
                dy = py - y1;
                return MathF.Sqrt(dx * dx + dy * dy);
            }

            float t = ((px - x1) * dx + (py - y1) * dy) / lengthSq;
            t = Math.Clamp(t, 0f, 1f);
            dx = px - (x1 + t * dx);
            dy = py - (y1 + t * dy);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static object FlipY(object point, object? translation)
        {
            TryReadXY(point, out float x, out float y);
            if (TryReadXY(translation, out float tx, out float ty))
            {
                x += tx;
                y += ty;
            }
            return vector2Ctor!.Invoke(new object[] { x, -y });
        }

        private static bool TryReadXY(object? value, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (value == null)
                return false;
            var type = value.GetType();
            if (!TryReadFloat(type, value, "X", out x) || !TryReadFloat(type, value, "Y", out y))
                return false;
            return true;
        }

        private static bool TryReadFloat(Type type, object value, string name, out float result)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            object? member = type.GetField(name, flags)?.GetValue(value) ?? type.GetProperty(name, flags)?.GetValue(value);
            if (member == null)
            {
                result = 0f;
                return false;
            }
            result = Convert.ToSingle(member);
            return true;
        }

        private static void TryEnd(object spriteBatch)
        {
            try
            {
                spriteEnd?.Invoke(spriteBatch, null);
            }
            catch
            {
            }
        }

        private static void LogOnce(string message)
        {
            if (loggedFailure)
                return;
            loggedFailure = true;
            VRManager.Log("[VrWallDepth] " + message);
        }

        private static bool EnsureCamera(object cam)
        {
            if (cameraBound)
                return shaderField != null && createTranslation != null && multiply != null;

            try
            {
                var cameraType = cam.GetType();
                const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                shaderField = cameraType.GetField("shaderTransform", inst);
                transformProp = cameraType.GetProperty("Transform");
                zoomProp = cameraType.GetProperty("Zoom");
                worldViewCenterProp = cameraType.GetProperty("WorldViewCenter");
                if (shaderField == null)
                {
                    LogOnce("Camera.shaderTransform was not found");
                    cameraBound = true;
                    return false;
                }

                var matrixType = shaderField.FieldType;
                createTranslation = matrixType.GetMethod("CreateTranslation", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float), typeof(float), typeof(float) }, null);
                multiply = matrixType.GetMethod("op_Multiply", BindingFlags.Public | BindingFlags.Static, null, new[] { matrixType, matrixType }, null)
                    ?? matrixType.GetMethod("Multiply", BindingFlags.Public | BindingFlags.Static, null, new[] { matrixType, matrixType }, null);
                nullableMatrixCtor = typeof(Nullable<>).MakeGenericType(matrixType).GetConstructor(new[] { matrixType });
                cameraBound = true;
                if (createTranslation == null || multiply == null)
                {
                    LogOnce("Matrix.CreateTranslation/Multiply was not found");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                cameraBound = true;
                LogOnce($"Camera bind failed: {ex.Message}");
                return false;
            }
        }

        private static bool EnsureDraw(object spriteBatch, object cam)
        {
            if (drawBound)
                return begin7 != null && spriteEnd != null && drawLine != null && getCells != null;

            try
            {
                EnsureCamera(cam);
                var spriteBatchType = spriteBatch.GetType();
                var cameraType = cam.GetType();
                var matrixType = shaderField?.FieldType ?? cameraType.GetProperty("Transform")?.PropertyType;
                var vector2Type = worldViewCenterProp?.PropertyType;
                var gdAsm = spriteBatchType.Assembly;
                var colorType = FindBarotraumaType("Microsoft.Xna.Framework.Color") ?? gdAsm.GetType("Microsoft.Xna.Framework.Color");
                var guiType = FindBarotraumaType("Barotrauma.GUI");
                var levelType = FindBarotraumaType("Barotrauma.Level");
                if (matrixType == null || vector2Type == null || colorType == null || guiType == null || levelType == null)
                {
                    LogOnce($"Draw types missing matrix={matrixType != null} vec={vector2Type != null} color={colorType != null} gui={guiType != null} level={levelType != null}");
                    drawBound = true;
                    return false;
                }

                loadedLevel = levelType.GetProperty("Loaded", BindingFlags.Public | BindingFlags.Static);
                getCells = levelType.GetMethod("GetCells", BindingFlags.Public | BindingFlags.Instance, null, new[] { vector2Type, typeof(int) }, null)
                    ?? levelType.GetMethod("GetCells", BindingFlags.Public | BindingFlags.Instance);
                var spriteType = FindBarotraumaType("Barotrauma.Sprite");
                drawLine = guiType.GetMethod("DrawLine", BindingFlags.Public | BindingFlags.Static, null, new[] { spriteBatchType, vector2Type, vector2Type, colorType, typeof(float), typeof(float) }, null);
                if (spriteType != null)
                    drawLineSprite = guiType.GetMethod("DrawLine", BindingFlags.Public | BindingFlags.Static, null, new[] { spriteBatchType, spriteType, vector2Type, vector2Type, colorType, typeof(float), typeof(int) }, null);
                if (drawLine == null)
                {
                    foreach (var method in guiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name != "DrawLine")
                            continue;
                        var p = method.GetParameters();
                        if (drawLineSprite == null && p.Length >= 5 && p[1].ParameterType.Name == "Sprite")
                            drawLineSprite = method;
                        if (p.Length >= 4 && p[1].ParameterType == vector2Type)
                            drawLine ??= method;
                    }
                }

                spriteEnd = spriteBatchType.GetMethod("End", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var nullableMatrix = typeof(Nullable<>).MakeGenericType(matrixType);
                foreach (var method in spriteBatchType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != "Begin")
                        continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 7 && parameters[6].ParameterType == nullableMatrix)
                        begin7 = method;
                }

                vector2Ctor = vector2Type.GetConstructor(new[] { typeof(float), typeof(float) });
                vecX = vector2Type.GetField("X");
                vecY = vector2Type.GetField("Y");
                var intColor = colorType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                var byteColor = colorType.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                colorCtor = intColor ?? byteColor;
                nullableMatrixCtor ??= typeof(Nullable<>).MakeGenericType(matrixType).GetConstructor(new[] { matrixType });

                var sortMode = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.SpriteSortMode");
                var blendState = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.BlendState");
                var samplerState = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.SamplerState");
                var depthState = gdAsm.GetType("Microsoft.Xna.Framework.Graphics.DepthStencilState");
                deferred = sortMode != null ? Enum.ToObject(sortMode, 0) : null;
                nonPremultiplied = ReadStatic(blendState, "NonPremultiplied");
                linearClamp = ReadStatic(samplerState, "LinearClamp");
                linearWrap = ReadStatic(samplerState, "LinearWrap") ?? linearClamp;
                depthNone = ReadStatic(depthState, "None");
                if (colorCtor != null)
                {
                    lineColor = MakeColor(0, 255, 255, 255);
                    fallbackColor = MakeColor(255, 0, 255, 255);
                    hullColor = MakeColor(255, 196, 40, 255);
                    roomColor = MakeColor(255, 120, 40, 255);
                    waterColor = MakeColor(48, 118, 168, 150);
                    waterBackColor = MakeColor(28, 72, 112, 90);
                    fireColor = MakeColor(170, 62, 18, 170);
                    itemColor = MakeColor(80, 255, 90, 255);
                    focusColor = MakeColor(255, 255, 255, 255);
                    characterColor = MakeColor(255, 90, 220, 255);
                    rockColor = MakeColor(36, 42, 38, 230);
                }

                drawBound = true;
                if (drawLine == null || begin7 == null || spriteEnd == null || vector2Ctor == null || lineColor == null)
                {
                    LogOnce($"Draw bind incomplete line={drawLine != null} begin={begin7 != null} end={spriteEnd != null} vec={vector2Ctor != null}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                drawBound = true;
                LogOnce($"Draw bind failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void BindCellTypes(IEnumerable cells)
        {
            if (edgesField != null)
                return;

            foreach (var cell in cells)
            {
                if (cell == null)
                    continue;
                var cellType = cell.GetType();
                const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                edgesField = cellType.GetField("Edges", inst);
                translationField = cellType.GetField("Translation", inst);
                bodyVerticesField = cellType.GetField("BodyVertices", inst);
                cellCenter = cellType.GetProperty("Center", inst);
                if (edgesField?.GetValue(cell) is IEnumerable edges)
                {
                    foreach (var edge in edges)
                    {
                        if (edge == null)
                            continue;
                        var edgeType = edge.GetType();
                        point1Field = edgeType.GetField("Point1", inst);
                        point2Field = edgeType.GetField("Point2", inst);
                        isSolidField = edgeType.GetField("IsSolid", inst);
                        nextToCaveField = edgeType.GetField("NextToCave", inst);
                        getNormal = edgeType.GetMethod("GetNormal", inst, null, new[] { cellType }, null)
                            ?? edgeType.GetMethod("GetNormal", inst);
                        if (point1Field?.GetValue(edge) is object point)
                        {
                            vecX ??= point.GetType().GetField("X");
                            vecY ??= point.GetType().GetField("Y");
                        }
                        break;
                    }
                }
                break;
            }
        }

        private static object MakeColor(int r, int g, int b, int a)
        {
            var parameters = colorCtor!.GetParameters();
            if (parameters[0].ParameterType == typeof(byte))
                return colorCtor.Invoke(new object[] { (byte)r, (byte)g, (byte)b, (byte)a });
            return colorCtor.Invoke(new object[] { r, g, b, a });
        }

        private static Type? FindBarotraumaType(string fullName)
        {
            var named = Type.GetType(fullName + ", Barotrauma", throwOnError: false);
            if (named != null)
                return named;

            var found = VRManager.FindType(fullName);
            if (found != null)
                return found;

            string simple = fullName.Contains('.') ? fullName[(fullName.LastIndexOf('.') + 1)..] : fullName;
            var gameScreen = Type.GetType("Barotrauma.GameScreen, Barotrauma", throwOnError: false) ?? VRManager.FindType("Barotrauma.GameScreen");
            if (gameScreen == null)
                return null;

            var fromAsm = gameScreen.Assembly.GetType(fullName, throwOnError: false) ?? gameScreen.Assembly.GetType(simple, throwOnError: false);
            if (fromAsm != null)
                return fromAsm;

            foreach (var type in gameScreen.Assembly.GetTypes())
            {
                if (type.Name == simple)
                    return type;
            }

            return null;
        }
    }
}
