using System;
using System.IO;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The default status-file path used to be the author's own Documents folder, baked
    /// into the settings designer; on any other PC that folder does not exist (#19).
    /// </summary>
    [TestFixture]
    public class SafetyStatusFileTest
    {
        private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        public void NothingConfiguredMeansTheCurrentUsersDocuments(string? configured) {
            SafetyStatusFile.Resolve(configured).Should().Be(Path.Combine(Documents, "N.I.N.A", "sky_conditions.txt"));
        }

        [Test]
        public void TheOldBakedInPathIsTreatedAsNothingConfigured() {
            SafetyStatusFile.Resolve(@"C:\Users\miche\Documents\N.I.N.A\sky_conditions.txt")
                .Should().Be(Path.Combine(Documents, "N.I.N.A", "sky_conditions.txt"));
        }

        [Test]
        public void AConfiguredPathIsKept() {
            SafetyStatusFile.Resolve(@"D:\obs\status.txt").Should().Be(@"D:\obs\status.txt");
        }
    }
}
