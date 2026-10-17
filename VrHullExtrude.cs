using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BarotraumaVR
{
    /// <summary>
    /// Phase 4: CasualtyVR-style item thickness. The original sprite is the
    /// front face; side walls follow the opaque silhouette, extruded in the
    /// stereo direction. Full extra copies of the sprite are not drawn.
    /// </summary>
    internal static class VrHullExtrude
    {
        private const float Thick = 8f;
        private const float Toward = 0.22f;
        private const int SideLayers = 3;
        private const int MaxOutline = 96;

        private static bool bound;
        private static bool failed;
        private static bool drawing;
        private static bool patched;
        private static MethodInfo? drawTiled;
        private static MethodInfo? drawSprite;
        private static MethodInfo? drawRect;
        private static MethodInfo? drawLineSprite;
        private static MethodInfo? getSpriteColor;
        private static MethodInfo? getDataRect;
        private static ConstructorInfo? vector2Ctor;
        private static ConstructorInfo? colorCtor;
        private static ConstructorInfo? rectCtor;
        private static Type? colorType;
        private static Type? nullableRectType;
        private static readonly Dictionary<string, float[]> outlines = new();
        private static readonly HashSet<string> skipSprites = new();
        private static bool colorBytes;
        private static object? white;
        private static object? outlineDark;
        private static object? outlineFocus;

        public static int DrawWalls(object spriteBatch, object cam) => Safe(spriteBatch, cam, DrawRims);

        public static void Apply(Harmony harmony)
        {
            if (patched)
                return;
            var itemType = Find("Barotrauma.Item");
            if (itemType == null)
                return;

            foreach (var method in itemType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "Draw")
                    continue;
                var p = method.GetParameters();
                if (p.Length < 5 || p[0].ParameterType.Name != "SpriteBatch")
                    continue;
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(VrHullExtrude).GetMethod(nameof(ItemDraw_Prefix), BindingFlags.Public | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(VrHullExtrude).GetMethod(nameof(ItemDraw_Postfix), BindingFlags.Public | BindingFlags.Static)));
                patched = true;
                VRManager.Log("[VrHullExtrude] Item.Draw silhouette sides like CasualtyVR");
                return;
            }
        }

        public static void ItemDraw_Prefix(object __instance, object spriteBatch)
        {
            DrawItemExtra(__instance, spriteBatch, towardCamera: true, outline: false);
        }

        public static void ItemDraw_Postfix(object __instance, object spriteBatch)
        {
        }

        private static void DrawItemExtra(object __instance, object spriteBatch, bool towardCamera, bool outline)
        {
            if (drawing || !patched || failed)
                return;
            if (!VRManager.IsVRActive || spriteBatch == null || __instance == null)
                return;
            if (VrSpriteBatchPatch.CurrentEye < 0)
                return;
            if (!towardCamera && !outline)
                return;
            if (!IsShipProp(__instance))
                return;
            var type = __instance.GetType();
            if (type.GetProperty("Visible")?.GetValue(__instance) is false)
                return;
            if (type.GetProperty("ParentInventory")?.GetValue(__instance) != null)
                return;
            if (type.GetProperty("Removed")?.GetValue(__instance) is true)
                return;
            if (type.GetProperty("IsHidden")?.GetValue(__instance) is true)
                return;

            drawing = true;
            try
            {
                if (!Ensure(spriteBatch))
                    return;
                object? sprite = type.GetProperty("Sprite")?.GetValue(__instance);
                if (sprite == null)
                    return;
                if (!TryRect(__instance, out float x, out float y, out float w, out float h))
                    return;
                object? color = getSpriteColor?.Invoke(__instance, new object?[] { null, false })
                    ?? type.GetProperty("SpriteColor")?.GetValue(__instance)
                    ?? white;
                bool panel = IsSmallPanel(__instance);
                if ((towardCamera && !panel) || outline)
                    PaintItem(spriteBatch, __instance, sprite, color, x, y, w, h, outline, focus: outline, body: false, towardCamera && !panel);
            }
            catch (Exception ex)
            {
                failed = true;
                VRManager.Log($"[VrHullExtrude] Item.Draw failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                drawing = false;
            }
        }

        private static int Safe(object spriteBatch, object cam, Func<object, object, int> draw)
        {
            if (!VRManager.IsVRActive || failed || spriteBatch == null || cam == null)
                return 0;
            if (VrSpriteBatchPatch.CurrentEye < 0)
                return 0;

            try
            {
                if (!Ensure(spriteBatch))
                    return 0;
                return draw(spriteBatch, cam);
            }
            catch (Exception ex)
            {
                failed = true;
                VRManager.Log($"[VrHullExtrude] Draw failed: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        private static int DrawRims(object spriteBatch, object cam)
        {
            var structureType = Find("Barotrauma.Structure");
            var mapEntityType = Find("Barotrauma.MapEntity");
            object? list = mapEntityType?.GetField("MapEntityList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? structureType?.GetField("WallList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? structureType?.GetProperty("WallList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (list is not IEnumerable walls || structureType == null)
                return 0;

            GetView(cam, out float cx, out float cy, out float halfW, out float halfH);
            int drawn = 0;
            foreach (var wall in walls)
            {
                if (wall == null || drawn > 600)
                    continue;
                if (!structureType.IsInstanceOfType(wall))
                    continue;
                var type = wall.GetType();
                if (type.GetProperty("Removed")?.GetValue(wall) is true)
                    continue;
                if (type.GetProperty("HiddenInGame")?.GetValue(wall) is true
                    || type.GetProperty("IsHidden")?.GetValue(wall) is true)
                    continue;
                if (type.GetProperty("Submarine")?.GetValue(wall) == null)
                    continue;
                if (type.GetProperty("HasBody")?.GetValue(wall) is not true)
                    continue;

                object? sprite = type.GetProperty("Sprite")?.GetValue(wall);
                object? color = type.GetProperty("SpriteColor")?.GetValue(wall);
                object? sections = type.GetProperty("Sections")?.GetValue(wall);
                if (sections is Array arr && arr.Length > 0)
                {
                    foreach (var section in arr)
                    {
                        if (section == null || drawn > 600)
                            continue;
                        drawn += Rim(spriteBatch, section, sprite, color, cx, cy, halfW, halfH);
                    }
                }
                else
                {
                    drawn += Rim(spriteBatch, wall, sprite, color, cx, cy, halfW, halfH);
                }
            }

            return drawn;
        }

        private static void PaintItem(object spriteBatch, object item, object sprite, object? color, float x, float y, float w, float h, bool outline, bool focus, bool body, bool towardCamera)
        {
            var type = item.GetType();
            object? prefab = type.GetProperty("Prefab")?.GetValue(item);
            bool tiled = prefab?.GetType().GetProperty("ResizeHorizontal")?.GetValue(prefab) is true
                || prefab?.GetType().GetProperty("ResizeVertical")?.GetValue(prefab) is true;

            float scale = 1f;
            object? scaleObj = type.GetProperty("Scale")?.GetValue(item);
            if (scaleObj != null)
                scale = Convert.ToSingle(scaleObj);
            float rot = 0f;
            object? rotObj = type.GetProperty("RotationRad")?.GetValue(item);
            if (rotObj != null)
                rot = Convert.ToSingle(rotObj);

            object? drawPos = type.GetProperty("DrawPosition")?.GetValue(item);
            if (!TryXY(drawPos, out float px, out float py))
            {
                px = x + w * 0.5f;
                py = y - h * 0.5f;
            }

            object? origin = sprite.GetType().GetProperty("Origin")?.GetValue(sprite);
            object? effects = sprite.GetType().GetField("effects")?.GetValue(sprite)
                ?? sprite.GetType().GetProperty("effects")?.GetValue(sprite);

            if (towardCamera && !tiled && !IsBrightFill(color))
                DrawShapeSides(spriteBatch, sprite, color, px, py, scale, rot, origin, effects);

            if (outline)
                Stroke(spriteBatch, x, y, w, h, focus ? outlineFocus : outlineDark);

            if (body)
                Blit(spriteBatch, sprite, tiled, px, py, x, y, w, h, scale, rot, origin, effects, color, 0f, 0f);
        }

        private static void DrawShapeSides(object spriteBatch, object sprite, object? color, float px, float py, float scale, float rot, object? origin, object? effects)
        {
            float[]? edges = OutlineFor(sprite);
            if (edges == null || edges.Length < 4 || drawLineSprite == null)
                return;

            TryXY(origin, out float ox, out float oy);
            bool flipX = false;
            if (effects != null)
                flipX = (Convert.ToInt32(effects) & 1) != 0;
            float cos = MathF.Cos(rot);
            float sin = MathF.Sin(rot);
            int eye = VrSpriteBatchPatch.CurrentEye;
            float dir = eye == 0 ? 1f : -1f;
            float depth = VrDepth.Pixels * 0.85f;
            int width = Math.Max(2, (int)(scale * 2.5f));

            void Map(float lx, float ly, out float dx, out float dy)
            {
                float x = (lx - ox) * scale;
                float y = (ly - oy) * scale;
                if (flipX)
                    x = -x;
                dx = px + x * cos - y * sin;
                dy = -py + x * sin + y * cos;
            }

            for (int i = 0; i + 3 < edges.Length; i += 4)
            {
                Map(edges[i], edges[i + 1], out float ax, out float ay);
                Map(edges[i + 2], edges[i + 3], out float bx, out float by);
                for (int layer = SideLayers; layer >= 1; layer--)
                {
                    float s = dir * depth * layer / SideLayers;
                    object? tint = Dim(color, 0.55f + 0.1f * (SideLayers - layer));
                    object start = vector2Ctor!.Invoke(new object[] { ax + s, ay });
                    object end = vector2Ctor.Invoke(new object[] { bx + s, by });
                    drawLineSprite.Invoke(null, new object?[] { spriteBatch, sprite, start, end, tint ?? white, 0f, width });
                }
            }
        }

        private static float[]? OutlineFor(object sprite)
        {
            object? texture = sprite.GetType().GetProperty("Texture")?.GetValue(sprite);
            object? src = sprite.GetType().GetProperty("SourceRect")?.GetValue(sprite);
            if (texture == null || src == null)
                return null;
            if (!TryRectFields(src, out int sx, out int sy, out int sw, out int sh))
                return null;
            if (sw < 2 || sh < 2 || sw * sh > 256 * 256)
                return null;

            string key = $"{texture.GetHashCode()}:{sx}:{sy}:{sw}:{sh}";
            if (skipSprites.Contains(key))
                return null;
            if (outlines.TryGetValue(key, out float[]? cached))
                return cached;

            try
            {
                if (!BindGetData(texture.GetType()) || getDataRect == null || rectCtor == null || colorType == null)
                {
                    skipSprites.Add(key);
                    return null;
                }

                int step = Math.Max(1, Math.Max(sw, sh) / 48);
                int gw = Math.Max(1, sw / step);
                int gh = Math.Max(1, sh / step);
                object rect = rectCtor.Invoke(new object[] { sx, sy, sw, sh });
                object box = nullableRectType != null
                    ? Activator.CreateInstance(nullableRectType, rect)!
                    : rect;
                Array pixels = Array.CreateInstance(colorType, sw * sh);
                getDataRect.Invoke(texture, new object[] { 0, box, pixels, 0, sw * sh });

                bool Solid(int gx, int gy)
                {
                    if (gx < 0 || gy < 0 || gx >= gw || gy >= gh)
                        return false;
                    int px = Math.Min(sw - 1, gx * step + step / 2);
                    int py = Math.Min(sh - 1, gy * step + step / 2);
                    object? c = pixels.GetValue(py * sw + px);
                    if (c == null)
                        return false;
                    object? a = c.GetType().GetProperty("A")?.GetValue(c) ?? c.GetType().GetField("A")?.GetValue(c);
                    return a != null && Convert.ToInt32(a) > 128;
                }

                var list = new List<float>(128);
                for (int gx = 0; gx < gw && list.Count / 4 < MaxOutline; gx++)
                {
                    int gy = 0;
                    while (gy < gh)
                    {
                        while (gy < gh && !(Solid(gx, gy) && !Solid(gx - 1, gy)))
                            gy++;
                        if (gy >= gh)
                            break;
                        int y0 = gy;
                        while (gy < gh && Solid(gx, gy) && !Solid(gx - 1, gy))
                            gy++;
                        list.Add(gx * step);
                        list.Add(y0 * step);
                        list.Add(gx * step);
                        list.Add(gy * step);
                    }
                    gy = 0;
                    while (gy < gh)
                    {
                        while (gy < gh && !(Solid(gx, gy) && !Solid(gx + 1, gy)))
                            gy++;
                        if (gy >= gh)
                            break;
                        int y0 = gy;
                        while (gy < gh && Solid(gx, gy) && !Solid(gx + 1, gy))
                            gy++;
                        list.Add((gx + 1) * step);
                        list.Add(y0 * step);
                        list.Add((gx + 1) * step);
                        list.Add(gy * step);
                    }
                }

                float[] edges = list.ToArray();
                outlines[key] = edges;
                if (outlines.Count == 1)
                    VRManager.Log($"[VrHullExtrude] silhouette sides, first sprite {gw}x{gh} edges={edges.Length / 4}");
                return edges;
            }
            catch
            {
                skipSprites.Add(key);
                return null;
            }
        }

        private static bool TryRectFields(object rect, out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            var t = rect.GetType();
            bool Read(string name, out int v)
            {
                object? m = t.GetField(name)?.GetValue(rect) ?? t.GetProperty(name)?.GetValue(rect);
                v = m == null ? 0 : Convert.ToInt32(m);
                return m != null;
            }
            return Read("X", out x) && Read("Y", out y) && Read("Width", out w) && Read("Height", out h);
        }

        private static bool BindGetData(Type textureType)
        {
            if (getDataRect != null)
                return true;
            if (colorType == null || rectCtor == null)
                return false;
            foreach (var method in textureType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "GetData" || !method.IsGenericMethodDefinition)
                    continue;
                var p = method.GetParameters();
                if (p.Length != 5)
                    continue;
                try
                {
                    getDataRect = method.MakeGenericMethod(colorType);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static void Blit(object spriteBatch, object sprite, bool tiled, float px, float py, float x, float y, float w, float h, float scale, float rot, object? origin, object? effects, object? color, float ox, float oy)
        {
            if (tiled)
            {
                Tile(spriteBatch, sprite, x + ox, y + oy, w, h, color);
                return;
            }

            if (drawSprite == null)
                return;
            object pos = vector2Ctor!.Invoke(new object[] { px + ox, -(py + oy) });
            object scl = vector2Ctor.Invoke(new object[] { scale, scale });
            object org = origin ?? vector2Ctor.Invoke(new object[] { 0f, 0f });
            object fx = effects ?? Enum.ToObject(drawSprite.GetParameters()[6].ParameterType, 0);
            drawSprite.Invoke(sprite, new object?[] { spriteBatch, pos, color ?? white, org, rot, scl, fx, null });
        }

        private static bool IsShipProp(object item)
        {
            var type = item.GetType();
            if (type.GetProperty("Submarine")?.GetValue(item) == null)
                return false;

            bool holdable = false;
            bool machine = false;
            object? components = type.GetProperty("Components")?.GetValue(item);
            if (components is IEnumerable list)
            {
                foreach (var component in list)
                {
                    if (component == null)
                        continue;
                    string name = component.GetType().Name;
                    if (name is "Wire" or "Projectile" or "MeleeWeapon" or "RangedWeapon"
                        or "LightComponent" or "ParticleEmitter" or "ElectricalDischarger")
                        return false;
                    if (name is "Holdable" or "Wearable" or "Throwable")
                        holdable = true;
                    if (name is "Door" or "Ladder")
                        return false;
                    if (name is "Controller" or "ItemContainer"
                        or "Terminal" or "CustomInterface" or "Button" or "Pump"
                        or "Engine" or "Reactor" or "Fabricator" or "Deconstructor"
                        or "PowerContainer" or "Turret" or "Sonar"
                        or "Steering" or "OxygenGenerator" or "Vent" or "ConnectionPanel"
                        or "MiniMap" or "WifiComponent" or "Growable" or "TriggerComponent")
                        machine = true;
                }
            }

            if (machine)
                return true;

            object? prefab = type.GetProperty("Prefab")?.GetValue(item);
            if (LooksLikeFire(item))
                return false;
            if (prefab?.GetType().GetProperty("ResizeHorizontal")?.GetValue(prefab) is true
                || prefab?.GetType().GetProperty("ResizeVertical")?.GetValue(prefab) is true)
                return true;

            if (holdable)
                return true;

            if (!TryRect(item, out _, out _, out float w, out float h))
                return false;
            return w >= 28f && h >= 28f;
        }

        private static bool IsDoorOrLadder(object item)
        {
            return HasComponent(item, "Door") || HasComponent(item, "Ladder");
        }

        private static bool HasComponent(object item, string name)
        {
            object? components = item.GetType().GetProperty("Components")?.GetValue(item);
            if (components is not IEnumerable list)
                return false;
            foreach (var component in list)
            {
                if (component?.GetType().Name == name)
                    return true;
            }
            return false;
        }

        private static bool IsSmallPanel(object item)
        {
            if (HasComponent(item, "Controller")
                || HasComponent(item, "Button")
                || HasComponent(item, "ConnectionPanel")
                || HasComponent(item, "CustomInterface")
                || HasComponent(item, "Terminal")
                || HasComponent(item, "MiniMap")
                || HasComponent(item, "WifiComponent")
                || HasComponent(item, "TriggerComponent"))
                return true;
            string text = ItemText(item);
            return text.Contains("switch") || text.Contains("lever") || text.Contains("button")
                || text.Contains("выключател");
        }

        private static bool IsOpaqueCabinet(object item)
        {
            string text = ItemText(item);
            if (text.Contains("steelcabinet") || text.Contains("suppliescabinet")
                || text.Contains("securesteel") || text.Contains("medcab")
                || text.Contains("engcab") || text.Contains("secab")
                || text.Contains("weaponcab"))
                return true;
            if (text.Contains("divingsuit") || text.Contains("window") || text.Contains("oxygentank"))
                return false;
            return text.Contains("locker") && text.Contains("container");
        }

        private static string ItemText(object item)
        {
            var type = item.GetType();
            object? prefab = type.GetProperty("Prefab")?.GetValue(item);
            return string.Join(" ",
                prefab?.GetType().GetProperty("Identifier")?.GetValue(prefab)?.ToString() ?? "",
                type.GetProperty("Tags")?.GetValue(item)?.ToString() ?? "",
                type.GetProperty("Name")?.GetValue(item)?.ToString() ?? "").ToLowerInvariant();
        }

        private static bool LooksLikeFire(object item)
        {
            var type = item.GetType();
            object? prefab = type.GetProperty("Prefab")?.GetValue(item);
            string text = string.Join(" ",
                prefab?.GetType().GetProperty("Identifier")?.GetValue(prefab)?.ToString() ?? "",
                type.GetProperty("Tags")?.GetValue(item)?.ToString() ?? "",
                type.GetProperty("Name")?.GetValue(item)?.ToString() ?? "");
            string lower = text.ToLowerInvariant();
            return lower.Contains("fire") || lower.Contains("flame") || lower.Contains("burn")
                || lower.Contains("torch") || lower.Contains("spark");
        }

        private static bool IsBrightFill(object? color)
        {
            if (color == null)
                return false;
            var t = color.GetType();
            float C(string n) => Convert.ToSingle(t.GetProperty(n)?.GetValue(color) ?? t.GetField(n)?.GetValue(color) ?? 0);
            return C("R") > 220f && C("G") > 220f && C("B") > 200f;
        }

        private static void Stroke(object spriteBatch, float x, float y, float w, float h, object? color)
        {
            if (drawRect == null || color == null)
                return;
            object start = vector2Ctor!.Invoke(new object[] { x, -y });
            object size = vector2Ctor.Invoke(new object[] { w, h });
            drawRect.Invoke(null, new object[] { spriteBatch, start, size, color, false, 0f, 3f });
        }

        private static bool TryXY(object? value, out float x, out float y)
        {
            x = y = 0f;
            if (value == null)
                return false;
            var t = value.GetType();
            object? mx = t.GetField("X")?.GetValue(value) ?? t.GetProperty("X")?.GetValue(value);
            object? my = t.GetField("Y")?.GetValue(value) ?? t.GetProperty("Y")?.GetValue(value);
            if (mx == null || my == null)
                return false;
            x = Convert.ToSingle(mx);
            y = Convert.ToSingle(my);
            return true;
        }

        private static int Rim(object spriteBatch, object entity, object? sprite, object? color, float cx, float cy, float halfW, float halfH)
        {
            if (!TryRect(entity, out float x, out float y, out float w, out float h))
                return 0;
            if (w < 4f || h < 4f)
                return 0;
            if (x > cx + halfW || x + w < cx - halfW || y < cy - halfH || y - h > cy + halfH)
                return 0;

            float t = Math.Min(Thick, Math.Min(w, h) * 0.45f);
            if (t < 2f)
                return 0;

            if (h >= w)
            {
                Tile(spriteBatch, sprite, x, y, t, h, color);
                Tile(spriteBatch, sprite, x + w - t, y, t, h, color);
            }
            else
            {
                Tile(spriteBatch, sprite, x, y, w, t, color);
                Tile(spriteBatch, sprite, x, y - h + t, w, t, color);
            }

            return 1;
        }

        private static object? Dim(object? color, float mul)
        {
            if (color == null || colorCtor == null)
                return color;
            var t = color.GetType();
            float R(string n) => Convert.ToSingle(t.GetProperty(n)?.GetValue(color) ?? t.GetField(n)?.GetValue(color) ?? 0);
            int r = Math.Clamp((int)(R("R") * mul), 0, 255);
            int g = Math.Clamp((int)(R("G") * mul), 0, 255);
            int b = Math.Clamp((int)(R("B") * mul), 0, 255);
            int a = Math.Clamp((int)R("A"), 0, 255);
            return colorBytes
                ? colorCtor.Invoke(new object[] { (byte)r, (byte)g, (byte)b, (byte)a })
                : colorCtor.Invoke(new object[] { r, g, b, a });
        }

        private static void Tile(object spriteBatch, object? sprite, float x, float y, float w, float h, object? color)
        {
            if (sprite == null || drawTiled == null)
                return;
            object pos = vector2Ctor!.Invoke(new object[] { x, -y });
            object size = vector2Ctor.Invoke(new object[] { w, h });
            drawTiled.Invoke(sprite, new object?[] { spriteBatch, pos, size, 0f, null, color ?? white, null, null, null });
        }

        private static bool Ensure(object spriteBatch)
        {
            if (bound)
                return drawTiled != null && vector2Ctor != null;

            try
            {
                var sbType = spriteBatch.GetType();
                var vec2Type = sbType.Assembly.GetType("Microsoft.Xna.Framework.Vector2")
                    ?? Find("Microsoft.Xna.Framework.Vector2");
                colorType = sbType.Assembly.GetType("Microsoft.Xna.Framework.Color")
                    ?? Find("Microsoft.Xna.Framework.Color");
                var spriteType = Find("Barotrauma.Sprite");
                var rectType = sbType.Assembly.GetType("Microsoft.Xna.Framework.Rectangle")
                    ?? Find("Microsoft.Xna.Framework.Rectangle");
                if (vec2Type == null || colorType == null || spriteType == null)
                {
                    failed = true;
                    bound = true;
                    VRManager.Log("[VrHullExtrude] Sprite.DrawTiled types missing");
                    return false;
                }
                if (rectType != null)
                {
                    rectCtor = rectType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                    nullableRectType = typeof(Nullable<>).MakeGenericType(rectType);
                }

                foreach (var method in spriteType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var p = method.GetParameters();
                    if (method.Name == "DrawTiled" && p.Length == 9 && p[3].ParameterType == typeof(float))
                        drawTiled = method;
                    if (method.Name == "Draw" && p.Length == 8 && p[5].ParameterType == vec2Type)
                        drawSprite = method;
                }

                vector2Ctor = vec2Type.GetConstructor(new[] { typeof(float), typeof(float) });
                var gui = Find("Barotrauma.GUI");
                if (gui != null)
                {
                    foreach (var method in gui.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var p = method.GetParameters();
                        if (method.Name == "DrawRectangle" && drawRect == null
                            && p.Length >= 5 && p[1].ParameterType == vec2Type && p[2].ParameterType == vec2Type)
                            drawRect = method;
                        if (method.Name == "DrawLine" && drawLineSprite == null
                            && p.Length >= 5 && p[1].ParameterType == spriteType)
                            drawLineSprite = method;
                    }
                }
                getSpriteColor = Find("Barotrauma.Item")?.GetMethod("GetSpriteColor", BindingFlags.Public | BindingFlags.Instance);
                var intColor = colorType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                var byteColor = colorType.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                colorCtor = intColor ?? byteColor;
                if (colorCtor != null)
                {
                    colorBytes = colorCtor.GetParameters()[0].ParameterType == typeof(byte);
                    object Box(int r, int g, int b, int a) => colorBytes
                        ? colorCtor.Invoke(new object[] { (byte)r, (byte)g, (byte)b, (byte)a })
                        : colorCtor.Invoke(new object[] { r, g, b, a });
                    white = Box(255, 255, 255, 255);
                    outlineDark = Box(12, 14, 16, 230);
                    outlineFocus = Box(255, 220, 90, 255);
                }

                bound = true;
                if (drawTiled == null || vector2Ctor == null)
                {
                    failed = true;
                    VRManager.Log("[VrHullExtrude] Sprite.DrawTiled bind failed");
                    return false;
                }

                VRManager.Log($"[VrHullExtrude] Shape sides line={drawLineSprite != null} getDataRect={rectCtor != null}, stroke={drawRect != null}");
                return true;
            }
            catch (Exception ex)
            {
                bound = true;
                failed = true;
                VRManager.Log($"[VrHullExtrude] Bind failed: {ex.Message}");
                return false;
            }
        }

        private static void GetView(object cam, out float cx, out float cy, out float halfW, out float halfH)
        {
            cx = cy = 0f;
            halfW = halfH = 4000f;
            object? center = cam.GetType().GetProperty("WorldViewCenter")?.GetValue(cam);
            if (center != null)
            {
                var t = center.GetType();
                cx = Convert.ToSingle(t.GetField("X")?.GetValue(center) ?? t.GetProperty("X")?.GetValue(center) ?? 0f);
                cy = Convert.ToSingle(t.GetField("Y")?.GetValue(center) ?? t.GetProperty("Y")?.GetValue(center) ?? 0f);
            }
            float zoom = 1f;
            object? z = cam.GetType().GetProperty("Zoom")?.GetValue(cam);
            if (z != null)
                zoom = Math.Max(0.01f, Convert.ToSingle(z));
            halfW = 1920f / zoom * 0.5f + 400f;
            halfH = 1080f / zoom * 0.5f + 400f;
        }

        private static bool TryRect(object entity, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            object? rect = entity.GetType().GetProperty("WorldRect")?.GetValue(entity);
            if (rect == null)
                return false;
            var t = rect.GetType();
            bool Read(string name, out float v)
            {
                object? m = t.GetField(name)?.GetValue(rect) ?? t.GetProperty(name)?.GetValue(rect);
                v = m == null ? 0f : Convert.ToSingle(m);
                return m != null;
            }
            return Read("X", out x) && Read("Y", out y) && Read("Width", out w) && Read("Height", out h);
        }

        private static Type? Find(string fullName)
        {
            return Type.GetType(fullName + ", Barotrauma", throwOnError: false) ?? VRManager.FindType(fullName);
        }
    }
}
