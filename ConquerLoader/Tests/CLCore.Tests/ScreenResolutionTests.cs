using CLCore.ClientOptions;
using CLCore.Models;
using Xunit;

namespace CLCore.Tests.ClientOptions
{
    /// <summary>
    /// What the loader writes into GameSetup.ini and CLHook.ini for a chosen
    /// size. The case that matters: fullscreen at a custom size must never
    /// reach the client as its own fullscreen (record 3), because the client
    /// turns that into 1024x768. It has to stay windowed at the size, with the
    /// hook doing the fullscreen.
    /// </summary>
    public sealed class ScreenResolutionTests
    {
        [Fact]
        public void FullscreenAtACustomSizeIsWindowedForTheClientAndFullscreenForTheHook()
        {
            LoaderConfig config = new LoaderConfig { FullScreen = true };
            ScreenResolution.Store(config, ScreenResolution.HdPlus);

            ScreenResolution.LaunchSettings settings = ScreenResolution.ForLaunch(config, 6300);

            Assert.Equal(ScreenResolution.HdPlus, settings.Size);
            Assert.Equal(1, settings.FullScrType);
            Assert.Equal(4, settings.ScreenModeRecord);
            Assert.True(settings.HookFullscreen);
        }

        [Fact]
        public void WindowedAtACustomSizeLeavesTheHookAlone()
        {
            LoaderConfig config = new LoaderConfig { FullScreen = false };
            ScreenResolution.Store(config, ScreenResolution.FullHd);

            ScreenResolution.LaunchSettings settings = ScreenResolution.ForLaunch(config, 6300);

            Assert.Equal(4, settings.ScreenModeRecord);
            Assert.Equal(1, settings.FullScrType);
            Assert.False(settings.HookFullscreen);
        }

        [Theory]
        [InlineData(800, 600, false, 1, 0)]
        [InlineData(800, 600, true, 0, 1)]
        [InlineData(1024, 768, false, 1, 2)]
        [InlineData(1024, 768, true, 0, 3)]
        public void BuiltInSizesUseTheClientsOwnModes(int width, int height, bool fullScreen, int fullScrType, int record)
        {
            LoaderConfig config = new LoaderConfig { FullScreen = fullScreen };
            ScreenResolution.Store(config, new ScreenResolution.Size(width, height));

            ScreenResolution.LaunchSettings settings = ScreenResolution.ForLaunch(config, 6300);

            Assert.Equal(fullScrType, settings.FullScrType);
            Assert.Equal(record, settings.ScreenModeRecord);
            Assert.False(settings.HookFullscreen);
        }

        [Fact]
        public void AConfigFromBeforeTheResolutionFieldStillReadsItsFlags()
        {
            Assert.Equal(ScreenResolution.FullHd, ScreenResolution.Resolve(new LoaderConfig { FHDResolution = true }));
            Assert.Equal(ScreenResolution.Xga, ScreenResolution.Resolve(new LoaderConfig { HighResolution = true }));
            Assert.Equal(ScreenResolution.Svga, ScreenResolution.Resolve(new LoaderConfig()));
        }

        [Fact]
        public void StoringASizeRoundTripsAndKeepsTheOldFlagsSensible()
        {
            LoaderConfig config = new LoaderConfig();
            ScreenResolution.Store(config, ScreenResolution.HdPlus);

            Assert.Equal("1600x900", config.Resolution);
            Assert.Equal(ScreenResolution.HdPlus, ScreenResolution.Resolve(config));
            Assert.True(config.HighResolution);
            Assert.False(config.FHDResolution);
        }

        [Fact]
        public void ACustomSizeOnAnOlderClientFallsBackTo1024()
        {
            LoaderConfig config = new LoaderConfig { FullScreen = true };
            ScreenResolution.Store(config, ScreenResolution.HdPlus);

            ScreenResolution.LaunchSettings settings = ScreenResolution.ForLaunch(config, 5065);

            Assert.Equal(ScreenResolution.Xga, settings.Size);
            Assert.Equal(3, settings.ScreenModeRecord);
            Assert.False(settings.HookFullscreen);
        }

        [Fact]
        public void AnUnknownSavedSizeIsIgnored()
        {
            LoaderConfig config = new LoaderConfig { Resolution = "1234x567", HighResolution = true };

            Assert.Equal(ScreenResolution.Xga, ScreenResolution.Resolve(config));
        }
    }
}
