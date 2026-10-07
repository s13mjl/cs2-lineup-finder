using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;

namespace CS2LineupFinder.Plugin.Simulation;

// This loader is the integration seam: it picks up CS2LineupFinder.Core and
// CS2LineupFinder.Math from the plugin directory at runtime, so the layers stay
// independently buildable. All three assemblies bind to the shared
// CS2LineupFinder.Abstractions.dll (docs/contract-issues.md C-08), and the
// loader below makes that guarantee hold at runtime.
/// <summary>
/// Finds the real simulator and solver and installs them into a
/// <see cref="SimulatorBridge"/>.
/// </summary>
/// <remarks>
/// <para>
/// Core and Math are loaded as plugin-local assemblies rather than referenced at
/// compile time, keeping the layer boundaries build-order-free. They must end up
/// with the same <c>CS2LineupFinder.Contracts</c> interface instances as this
/// plugin, otherwise the assignability check below rejects them and the plugin
/// silently falls back to its stubs.
/// </para>
/// <para>
/// <c>Assembly.LoadFrom</c> cannot provide that: it loads into the default
/// context, where re-resolving <c>CS2LineupFinder.Abstractions.dll</c> can yield
/// a second instance of the contract types even though the plugin's own copy is
/// already loaded. The loader therefore uses a custom
/// <see cref="AssemblyLoadContext"/> whose <c>Load</c> override reuses the
/// plugin's contract assembly by identity and loads the sibling layer assemblies
/// into the same context. Everything else (the BCL, CounterStrikeSharp itself)
/// falls through to the default context as usual. The context is collectible and
/// rooted only while the loaded instances live, so it is released together with
/// them on a plugin reload.
/// </para>
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

        // The plugin's loaded copy of the contract assembly is the single source
        // of truth for interface identity; ITrajectorySimulator lives in it.
        var loader = new PluginDependencyLoader(pluginDirectory, typeof(ITrajectorySimulator).Assembly);
        var simulator = TryInstall<ITrajectorySimulator>(loader, CoreAssembly, messages, "simulator");
        var solver = TryInstall<ILineupSolver>(loader, MathAssembly, messages, "solver");

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

    private static T? TryInstall<T>(PluginDependencyLoader loader, string fileName, IPluginMessages messages, string label)
        where T : class
    {
        var path = Path.Combine(loader.Directory, fileName);
        if (!File.Exists(path))
        {
            messages.Log($"CS2LineupFinder: {fileName} is not installed, keeping the built-in {label}.");
            return null;
        }

        Assembly assembly;
        try
        {
            assembly = loader.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            messages.LogWarning($"CS2LineupFinder: {fileName} could not be loaded: {ex.Message}");
            return null;
        }

        var candidates = new List<(Type Type, object?[] Args)>();
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
            if (type.IsAbstract || !type.IsClass)
            {
                continue;
            }

            var constructible = FindConstructible(type);
            if (constructible is null)
            {
                continue;
            }

            if (typeof(T).IsAssignableFrom(type))
            {
                candidates.Add((type, constructible.Value.Args));
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
                  "It was most likely built against a duplicate copy of the contracts instead of sharing this plugin's, so the two interface types are not interchangeable."
                : $"CS2LineupFinder: {fileName} is present but contains no usable {label}, keeping the built-in one.");
            return null;
        }

        var chosen = candidates[0];
        try
        {
            var instance = (T?)Activator.CreateInstance(chosen.Type, chosen.Args);
            if (instance is null)
            {
                messages.LogWarning($"CS2LineupFinder: {chosen.Type.FullName} could not be constructed.");
                return null;
            }

            messages.Log($"CS2LineupFinder: using {chosen.Type.FullName} from {fileName} as the {label}.");
            return instance;
        }
        catch (Exception ex)
        {
            messages.LogWarning($"CS2LineupFinder: {chosen.Type.FullName} could not be constructed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Finds a constructor that can be invoked without caller-supplied arguments.
    /// A parameterless constructor is preferred; otherwise a constructor whose
    /// parameters are all optional works too, because the layers ship
    /// <c>GrenadeSimulator(PhysicsParameters? = null)</c> and
    /// <c>LineupSolver(IVDataProvider? = null, SolverOptions? = null)</c> exactly
    /// for this loader. Passing the declared defaults keeps those optional
    /// dependencies unset, as intended.
    /// </summary>
    private static (ConstructorInfo Ctor, object?[] Args)? FindConstructible(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0)
        {
            return null;
        }

        foreach (var ctor in ctors)
        {
            if (ctor.GetParameters().Length == 0)
            {
                return (ctor, Array.Empty<object?>());
            }
        }

        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            if (parameters.All(p => p.HasDefaultValue))
            {
                return (ctor, parameters.Select(p => (object?)p.DefaultValue).ToArray());
            }
        }

        return null;
    }

    /// <summary>
    /// Isolation context for the runtime-loaded layer assemblies. It keeps the
    /// contract types single-instance across plugin, Core and Math.
    /// </summary>
    private sealed class PluginDependencyLoader : AssemblyLoadContext
    {
        private readonly Assembly _contractsAssembly;

        public PluginDependencyLoader(string directory, Assembly contractsAssembly)
            : base("CS2LineupFinder.Dependencies", isCollectible: true)
        {
            Directory = directory;
            _contractsAssembly = contractsAssembly;
        }

        public string Directory { get; }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null)
            {
                return null;
            }

            // The plugin's own copy of the contracts is the single source of truth:
            // reusing it keeps interface identity stable across every layer.
            if (name.Name.Equals("CS2LineupFinder.Abstractions", StringComparison.OrdinalIgnoreCase))
            {
                return _contractsAssembly;
            }

            if (name.Name.StartsWith("CS2LineupFinder.", StringComparison.OrdinalIgnoreCase))
            {
                var path = Path.Combine(Directory, name.Name + ".dll");
                if (File.Exists(path))
                {
                    return LoadFromAssemblyPath(Path.GetFullPath(path));
                }
            }

            // Everything else resolves in the default context, exactly as it would
            // for the plugin itself.
            return null;
        }
    }
}
