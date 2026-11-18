using System;
using CLCore.Models;

namespace CLCore.ClientOptions
{
    /// <summary>
    /// Works out what the loader writes into ini\GameSetup.ini and CLHook.ini
    /// for the chosen resolution and fullscreen setting. Both launch paths and
    /// the resolution pickers use this so that they agree.
    ///
    /// WHY FULLSCREEN IS NOT ALWAYS THE CLIENT'S OWN
    /// ========================================================================
    /// The client's own fullscreen mode only supports its two built-in sizes,
    /// 800x600 (ScreenModeRecord 1) and 1024x768 (ScreenModeRecord 3). Asking
    /// for fullscreen at any other size, or pressing the in-game fullscreen
    /// button, drops it back to 1024x768. So for the built-in sizes the client
    /// handles fullscreen itself. For a custom size the client runs windowed
    /// at that size (ScreenModeRecord 4), and ConquerCipherHook switches the
    /// display to the same mode and removes the window frame. The client still
    /// thinks it is windowed, so its back buffer, mouse coordinates and
    /// device handling stay as they are.
    /// </summary>
    public static class ScreenResolution
    {
        public static readonly Size Svga = new Size(800, 600);
        public static readonly Size Xga = new Size(1024, 768);
        public static readonly Size HdPlus = new Size(1600, 900);
        public static readonly Size FullHd = new Size(1920, 1080);

        /// <summary>The client's own sizes, offered for every server version.</summary>
        public static readonly Size[] BuiltIn = { Svga, Xga };

        /// <summary>Sizes that need ScreenModeRecord 4, which only some clients understand.</summary>
        public static readonly Size[] Custom = { HdPlus, FullHd };

        public struct Size : IEquatable<Size>
        {
            public readonly int Width;
            public readonly int Height;

            public Size(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public bool Equals(Size other) => Width == other.Width && Height == other.Height;
            public override bool Equals(object obj) => obj is Size && Equals((Size)obj);
            public override int GetHashCode() => (Width * 397) ^ Height;
            public override string ToString() => Width + "x" + Height;

            public static bool TryParse(string text, out Size size)
            {
                size = default(Size);
                if (string.IsNullOrWhiteSpace(text)) return false;
                string[] parts = text.Trim().Split('x', 'X');
                int width, height;
                if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height)) return false;
                if (width <= 0 || height <= 0) return false;
                size = new Size(width, height);
                return true;
            }
        }

        /// <summary>What has to be written for one launch.</summary>
        public sealed class LaunchSettings
        {
            public Size Size;
            /// <summary>GameSetup.ini [ScreenMode] FullScrType: 0 fullscreen, 1 windowed.</summary>
            public int FullScrType;
            /// <summary>GameSetup.ini [ScreenMode] ScreenModeRecord.</summary>
            public int ScreenModeRecord;
            /// <summary>
            /// CLHook.ini FULLSCREEN: 1 when the hook makes the windowed client
            /// fill the screen at <see cref="Size"/>.
            /// </summary>
            public bool HookFullscreen;
        }

        /// <summary>Whether the client understands ScreenModeRecord 4 (custom sizes).</summary>
        public static bool SupportsCustom(uint serverVersion)
        {
            return serverVersion == 5187 || serverVersion >= 5600;
        }

        public static bool IsBuiltIn(Size size)
        {
            return Array.IndexOf(BuiltIn, size) >= 0;
        }

        /// <summary>
        /// The size saved in the config. <see cref="LoaderConfig.Resolution"/>
        /// takes priority. A config.json written before that field existed only
        /// has the two flags, so those are read as a fallback.
        /// </summary>
        public static Size Resolve(LoaderConfig config)
        {
            if (config == null) return Svga;

            Size size;
            if (Size.TryParse(config.Resolution, out size)
                && (IsBuiltIn(size) || Array.IndexOf(Custom, size) >= 0))
            {
                return size;
            }

            if (config.FHDResolution) return FullHd;
            if (config.HighResolution) return Xga;
            return Svga;
        }

        /// <summary>
        /// Saves a choice. The old flags are updated as well, so an older loader
        /// that reads this config.json gets the closest size it knows about.
        /// </summary>
        public static void Store(LoaderConfig config, Size size)
        {
            if (config == null) return;
            config.Resolution = size.ToString();
            config.FHDResolution = size.Equals(FullHd);
            config.HighResolution = !size.Equals(Svga) && !size.Equals(FullHd);
        }

        /// <summary>
        /// The values to write for <paramref name="config"/> on a client of
        /// <paramref name="serverVersion"/>. A custom size on a client that
        /// cannot show one falls back to 1024x768.
        /// </summary>
        public static LaunchSettings ForLaunch(LoaderConfig config, uint serverVersion)
        {
            Size size = Resolve(config);
            bool fullScreen = config != null && config.FullScreen;

            if (!IsBuiltIn(size) && !SupportsCustom(serverVersion))
            {
                size = Xga;
            }

            LaunchSettings settings = new LaunchSettings { Size = size };

            if (IsBuiltIn(size))
            {
                bool xga = size.Equals(Xga);
                settings.FullScrType = fullScreen ? 0 : 1;
                settings.ScreenModeRecord = xga ? (fullScreen ? 3 : 2) : (fullScreen ? 1 : 0);
                settings.HookFullscreen = false;
            }
            else
            {
                // The client is always windowed here. Fullscreen is done by the hook.
                settings.FullScrType = 1;
                settings.ScreenModeRecord = 4;
                settings.HookFullscreen = fullScreen;
            }

            return settings;
        }
    }
}
