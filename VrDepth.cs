using System;

namespace BarotraumaVR
{
    /// <summary>
    /// Shared stereo planes for the diorama. Higher depth = more pixel shift.
    /// Cave sits behind the hull, crew in front of the hull, HUD stays at 0.
    ///
    /// First person also adds positional parallax: the VR head pans the game
    /// camera, and layers compensate by depth so near planes travel further
    /// than far ones (a pinhole camera emulation on top of the flat render).
    /// </summary>
    internal static class VrDepth
    {
        /// <summary>
        /// Pixels per depth unit. 2 was too flat after alignment; 12 ghosted.
        /// </summary>
        public const float Pixels = 6f;

        /// <summary>
        /// First person reads better with more separation; the diorama keeps 1.
        /// </summary>
        public static float Multiplier { get; set; } = 1f;

        public static float Scale => Pixels * Multiplier;

        public const float CaveFar = 1.10f;
        public const float CaveMid = 0.90f;
        public const float CaveFront = 0.75f;
        public const float CaveWalls = 0.95f;
        public const float Outpost = 0.85f;
        public const float Water = 0.70f;
        public const float WaterBack = 0.88f;
        public const float Hull = 0.55f;
        public const float Crew = 0.90f;
        public const float Lights = 0.60f;
        public const float Fire = 0.48f;

        /// <summary>
        /// Camera pan from the tracked head, in game units (0 in the diorama).
        /// Set by VrFirstPerson.FitLocalCamera each frame.
        /// </summary>
        private static float panX;
        private static float panY;
        private static float panZoom = 1f;

        /// <summary>
        /// How fast layer motion falls off with depth: 0 = flat (no parallax),
        /// larger = far layers lag more behind the head.
        /// </summary>
        private const float PanCurve = 1.6f;

        public static void SetPan(float unitsX, float unitsY, float zoom)
        {
            panX = unitsX;
            panY = unitsY;
            panZoom = Math.Max(0.05f, zoom);
        }

        public static void ClearPan()
        {
            panX = 0f;
            panY = 0f;
        }

        /// <summary>
        /// Extra screen-pixel shift for a layer at this depth when the head pans:
        /// the ortho render already moves everything by the pan, far layers take
        /// part of it back so they appear to recede. Same for both eyes.
        /// </summary>
        public static float PanShiftX(float depth)
        {
            if (panX == 0f || depth <= 0f)
                return 0f;
            float f = PanCurve * depth / (1f + PanCurve * depth);
            return -panX * panZoom * f;
        }

        public static float PanShiftY(float depth)
        {
            if (panY == 0f || depth <= 0f)
                return 0f;
            float f = PanCurve * depth / (1f + PanCurve * depth);
            return -panY * panZoom * f;
        }

        public static float Shift(int eye, float depth)
        {
            if (eye < 0 || eye > 1)
                return 0f;
            return (eye == 0 ? -1f : 1f) * Scale * depth;
        }
    }
}
