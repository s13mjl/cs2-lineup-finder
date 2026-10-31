using Xunit;
using CS2LineupFinder.Plugin.Config;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// The R5 velocity-inheritance fix only reaches a live search if the TOML
/// value flows through PluginConfig.BuildEnvironment into the
/// SimulationEnvironment.ThrowerVelocityInheritance the solver reads.
/// Nothing else covers this seam: a dropped or mis-clamped key would
/// silently return the search to the unitary-inheritance behaviour that
/// produced the 75.9u / 132.8u moving-thrower calibration misses.
/// </summary>
public sealed class VelocityInheritanceConfigTests
{
    private static string WriteConfig(string root, string toml)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var path = Path.Combine(root, "config", "config.toml");
        File.WriteAllText(path, toml);
        return path;
    }

    [Fact]
    public void BuildEnvironment_DefaultInheritance_IsLeakConstant()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteConfig(root, "[solver]\nmaxTracesPerSolve = 1\n");
            var config = PluginConfig.Load(root);

            var environment = config.BuildEnvironment();

            Assert.Equal(1.25f, environment.ThrowerVelocityInheritance);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildEnvironment_TomlOverride_FlowsThrough()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteConfig(root, "[physics]\nvelocityInheritance = 1.4\n");
            var config = PluginConfig.Load(root);

            var environment = config.BuildEnvironment();

            Assert.Equal(1.4f, environment.ThrowerVelocityInheritance);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildEnvironment_OutOfRangeValue_ClampsIntoLegalBand()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2lf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteConfig(root, "[physics]\nvelocityInheritance = 99\n");
            var config = PluginConfig.Load(root);

            var environment = config.BuildEnvironment();

            Assert.Equal(3f, environment.ThrowerVelocityInheritance);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
