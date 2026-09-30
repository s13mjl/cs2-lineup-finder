using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;

namespace CS2LineupFinder.Plugin.Simulation;

// This loader is the integration seam: it picks up CS2LineupFinder.Core and
// CS2LineupFinder.Math from the plugin directory at runtime, so the layers stay
// independently buildable. The plugin references CS2LineupFinder.Abstractions
// (see C-08), and Core/Math reference the same assembly, so interface types match.
/// <summary>
/// Finds the real simulator and solver and installs them into a
/// <see cref="SimulatorBridge"/>.
/// </summary>
/// <remarks>
/// Core and Math are loaded as plugin-local assemblies rather than referenced at
/// compile time, keeping the layer boundaries build-order-free. All three
/// assemblies bind to the shared <c>CS2LineupFinder.Abstractions.dll</c>, so the
/// assignability check below is expected to pass for correctly built drops; it is
/// retained as a diagnostic for stale or mismatched deployments.
/// </remarks>
public static class CoreLoader
{
    /// <summary>File name of the forward simulator assembly.</summary>
    public const string CoreAssembly = "CS2LineupFinder.Core.dll";

    /// <summary>File name of the math and solver assembly.</summary>
    public const string MathAssembly = "CS2LineupFinder.Math.dll";

    /// <summary>
    /// Loads every implementation it can find in the plugin directory.
    /// </summary>
    /// <param name="pluginDirectory">Directory holding the plugin DLL and its siblings.</param>
    /// <param name="bridge">Bridge to install the implementations into.</param>
    /// <param name="messages">Console sink for what was found and what was not.</param>
    /// <returns>A summary suitable for one log line.</returns>
    public static string Install(string pluginDirectory, SimulatorBridge bridge, IPluginMessages messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(messages);

        var simulator = TryInstall<ITrajectorySimulator>(pluginDirectory, CoreAssembly, messages, "simulator");
        var solver = TryInstall<ILineupSolver>(pluginDirectory, MathAssembly, messages, "solver");

        if (simulator is not null)
        {
            bridge.UseCoreSimulator(simulator);
        }

        if (solver is not null)
        {
            bridge.UseCoreSolver(solver);
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"simulator={bridge.Simulator.GetType().Name} solver={bridge.Solver.GetType().Name} stub={bridge.UsingStub}");
    }

    private static T? TryInstall<T>(string directory, string fileName, IPluginMessages messages, string label)
        where T : class
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            messages.Log($"CS2LineupFinder: {fileName} is not installed, keeping the built-in {label}.");
            return null;
        }

        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(path);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            messages.LogWarning($"CS2LineupFinder: {fileName} could not be loaded: {ex.Message}");
            return null;
        }

        var candidates = new List<Type>();
        var rejected = 0;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = Array.Empty<Type>();
            messages.LogWarning($"CS2LineupFinder: {fileName} could not be inspected: {ex.Message}");
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !type.IsClass || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            if (typeof(T).IsAssignableFrom(type))
            {
                candidates.Add(type);
                continue;
            }

            // A type whose name says it implements T but does not is the signature of
            // a duplicate contract assembly, so it is worth counting rather than
            // ignoring: it is the one failure mode that looks like success from here.
            if (type.Name.Contains(label, StringComparison.OrdinalIgnoreCase) &&
                type.GetInterfaces().Length > 0)
            {
                rejected++;
            }
        }

        if (candidates.Count == 0)
        {
            messages.LogWarning(rejected > 0
                ? $"CS2LineupFinder: {fileName} is present but its {label} does not implement {typeof(T).FullName} from this plugin. " +
                  "It was most likely built against CS2LineupFinder.Abstractions.dll instead of compiling contracts/ in, so the two interface types are not interchangeable."
                : $"CS2LineupFinder: {fileName} is present but contains no usable {label}, keeping the built-in one.");
            return null;
        }

        var chosen = candidates[0];
        try
        {
            var instance = (T?)Activator.CreateInstance(chosen);
            if (instance is null)
            {
                messages.LogWarning($"CS2LineupFinder: {chosen.FullName} could not be constructed.");
                return null;
            }

            messages.Log($"CS2LineupFinder: using {chosen.FullName} from {fileName} as the {label}.");
            return instance;
        }
        catch (Exception ex)
        {
            messages.LogWarning($"CS2LineupFinder: {chosen.FullName} could not be constructed: {ex.Message}");
            return null;
        }
    }
}
