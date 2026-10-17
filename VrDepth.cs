namespace BarotraumaVR
{
    /// <summary>
    /// Shared stereo planes for the diorama. Higher depth = more pixel shift.
    /// Cave sits behind the hull, crew in front of the hull, HUD stays at 0.
    /// </summary>
    internal static class VrDepth
    {
        /// <summary>
        /// Pixels per depth unit. 2 was too flat after alignment; 12 ghosted.
        /// </summary>
        public const float Pixels = 6f;

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

        public static float Shift(int eye, float depth)
        {
            if (eye < 0 || eye > 1)
                return 0f;
            return (eye == 0 ? -1f : 1f) * Pixels * depth;
        }
    }
}
