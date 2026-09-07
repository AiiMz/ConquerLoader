using CLCore.ClientOptions;
using CLCore.Models;
using Xunit;

namespace CLCore.Tests.ClientOptions
{
    /// <summary>
    /// The frame cap the loader hands the client.
    ///
    /// WHAT IS ACTUALLY UNDER TEST is the one decision this makes and the one
    /// mistake it must not make:
    ///
    ///   * A config.json written before FpsLimit existed has no FpsLimit, which
    ///     deserialises to 0. Zero is also the number that means "no cap" on the
    ///     wire, so the interesting case is that those two never meet: an absent
    ///     value has to become the default cap, not uncapped. Getting that wrong
    ///     ships a loader that looks like it has a limiter and does not.
    ///   * Unlimited stays reachable, because that is the toggle the loader has
    ///     always drawn and players expect it to still be there.
    /// </summary>
    public sealed class FrameRateLimitTests
    {
        [Fact]
        public void AConfigFromBeforeThisExistedGetsTheDefaultCapRatherThanNone()
        {
            // Every config.json in the wild: FPSUnlock present, FpsLimit absent.
            LoaderConfig config = new LoaderConfig { FPSUnlock = false };

            Assert.Equal(0, config.FpsLimit);
            Assert.Equal(FrameRateLimit.DefaultLimit, FrameRateLimit.Resolve(config));
            Assert.NotEqual(FrameRateLimit.Unlimited, FrameRateLimit.Resolve(config));
        }

        [Fact]
        public void UnlockedMeansUncapped()
        {
            LoaderConfig config = new LoaderConfig { FPSUnlock = true, FpsLimit = 60 };

            Assert.Equal(FrameRateLimit.Unlimited, FrameRateLimit.Resolve(config));
        }

        [Fact]
        public void AConfiguredCapIsUsedAsItStands()
        {
            LoaderConfig config = new LoaderConfig { FPSUnlock = false, FpsLimit = 144 };

            Assert.Equal(144, FrameRateLimit.Resolve(config));
        }

        [Fact]
        public void EveryOfferedChoiceSurvivesBeingConfigured()
        {
            Assert.NotEmpty(FrameRateLimit.Choices);

            foreach (int choice in FrameRateLimit.Choices)
            {
                LoaderConfig config = new LoaderConfig { FPSUnlock = false, FpsLimit = choice };
                Assert.Equal(choice, FrameRateLimit.Resolve(config));
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(FrameRateLimit.MinimumLimit - 1)]
        [InlineData(FrameRateLimit.MaximumLimit + 1)]
        [InlineData(int.MaxValue)]
        public void ACapOutsideTheBoundsFallsBackToTheDefault(int stored)
        {
            LoaderConfig config = new LoaderConfig { FPSUnlock = false, FpsLimit = stored };

            Assert.Equal(FrameRateLimit.DefaultLimit, FrameRateLimit.Resolve(config));
        }

        [Theory]
        [InlineData(FrameRateLimit.MinimumLimit)]
        [InlineData(FrameRateLimit.MaximumLimit)]
        public void TheBoundsThemselvesAreInsideTheRange(int stored)
        {
            LoaderConfig config = new LoaderConfig { FPSUnlock = false, FpsLimit = stored };

            Assert.Equal(stored, FrameRateLimit.Resolve(config));
        }

        [Fact]
        public void NoConfigAtAllIsUncappedRatherThanACrash()
        {
            Assert.Equal(FrameRateLimit.Unlimited, FrameRateLimit.Resolve(null));
        }

        [Fact]
        public void TheDefaultAndEveryOfferedChoiceAreThemselvesInsideTheBounds()
        {
            Assert.InRange(FrameRateLimit.DefaultLimit, FrameRateLimit.MinimumLimit, FrameRateLimit.MaximumLimit);
            Assert.Contains(FrameRateLimit.DefaultLimit, FrameRateLimit.Choices);

            foreach (int choice in FrameRateLimit.Choices)
            {
                Assert.InRange(choice, FrameRateLimit.MinimumLimit, FrameRateLimit.MaximumLimit);
            }
        }
    }
}
