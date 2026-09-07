using CLCore.Models;

namespace CLCore.ClientOptions
{
    /// <summary>
    /// Works out the frame cap to hand the client, in one place, because two
    /// launch paths and two settings screens all have to agree on it.
    ///
    /// WHY THERE IS A CAP AT ALL
    /// ========================================================================
    /// The client has no frame limiter of its own, so it draws as fast as the
    /// machine allows - 700 FPS on a current GPU. It also advances animations a
    /// step per frame rather than per unit of time, so that is not just wasted
    /// electricity: everything on screen moves at ten times the speed it was
    /// drawn for. A cap is what makes the game run at its intended pace.
    ///
    /// This is why the "FPS Unlock" toggle appeared to do nothing. It did do
    /// nothing: the loader stored it in config.json and read it back only to
    /// draw the toggle. Nothing else ever looked at it, and there was no cap in
    /// the client for it to remove. It is now the switch that turns this cap
    /// off, and <see cref="LoaderConfig.FpsLimit"/> carries the value used when
    /// it is left on.
    ///
    /// The cap itself is enforced in ConquerCipherHook, which the loader injects
    /// into the client; the number reaches it as MAX_FPS in CLHook.ini.
    /// </summary>
    public static class FrameRateLimit
    {
        /// <summary>What <see cref="Resolve"/> returns for "do not cap".</summary>
        public const int Unlimited = 0;

        /// <summary>
        /// Used when nothing usable is configured. 60 rather than the monitor
        /// refresh rate: the cap sets animation speed here, so it wants to be
        /// the same number for everyone rather than one per player's monitor.
        /// </summary>
        public const int DefaultLimit = 60;

        /// <summary>
        /// The bounds a stored value has to fall inside to be used. Below 10 the
        /// game is unplayable rather than slow, and above 1000 the cap is not
        /// reachable on any hardware this client runs on, so either is treated
        /// as a typed-in mistake and falls back to <see cref="DefaultLimit"/>.
        /// </summary>
        public const int MinimumLimit = 10;
        public const int MaximumLimit = 1000;

        /// <summary>
        /// The values offered in the UI. Not a constraint - a config.json may
        /// hold any number inside the bounds above and it will be honoured.
        /// </summary>
        public static readonly int[] Choices = { 30, 60, 75, 100, 120, 144 };

        /// <summary>
        /// The cap to write into CLHook.ini, or <see cref="Unlimited"/>.
        /// </summary>
        public static int Resolve(LoaderConfig config)
        {
            if (config == null || config.FPSUnlock)
            {
                return Unlimited;
            }

            if (config.FpsLimit >= MinimumLimit && config.FpsLimit <= MaximumLimit)
            {
                return config.FpsLimit;
            }

            // Covers the 0 that every config.json written before this existed
            // has, and anything else out of range.
            return DefaultLimit;
        }
    }
}
