using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using CS2LineupFinder.Contracts;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// Tracer that binds to the managed <c>Trace</c> API at runtime.
/// </summary>
/// <remarks>
/// The trace API landed in CounterStrikeSharp after the last .NET 8 build, so the
/// plugin cannot compile against it. It can, however, call it when it is there: a
/// plugin DLL built for .NET 8 loads into a .NET 10 host, so a server running a
/// recent CounterStrikeSharp exposes the type and this backend uses it by
/// reflection. Every member is looked up once in the constructor, so an unexpected
/// API shape degrades to a missing feature instead of failing at the first trace.
/// </remarks>
internal sealed class ReflectiveTraceBackend : ITraceBackend
{
    private const string TraceTypeName = "CounterStrikeSharp.API.Modules.Utils.Trace";

    private readonly ConstructorInfo _vector3;
    private readonly ConstructorInfo _vector1;
    private readonly MethodInfo _traceEndShape;
    private readonly MethodInfo _traceHullShape;
    private readonly MethodInfo _didHit;
    private readonly PropertyInfo _fraction;
    private readonly PropertyInfo _endPos;
    private readonly PropertyInfo _normal;
    private readonly PropertyInfo _hitPoint;
    private readonly PropertyInfo _hasExactHitPoint;
    private readonly MethodInfo _hitEntity;
    private readonly PropertyInfo _entityIndex;
    private readonly PropertyInfo _designerName;

    private ReflectiveTraceBackend(Assembly assembly)
    {
        var trace = assembly.GetType(TraceTypeName, throwOnError: false)
            ?? throw new MissingMemberException(TraceTypeName + " is not present in the loaded CounterStrikeSharp assembly.");

        var utils = assembly.GetType("CounterStrikeSharp.API.Modules.Utils.Vector", throwOnError: true)!;

        var nullableSingle = typeof(float?);
        _vector3 = utils.GetConstructor(new[] { nullableSingle, nullableSingle, nullableSingle })
            ?? throw new MissingMemberException("Vector(float?, float?, float?)");
        _vector1 = utils.GetConstructor(new[] { typeof(IntPtr) })
            ?? throw new MissingMemberException("Vector(IntPtr)");

        _traceEndShape = Require(trace, "TraceEndShape", 4);
        _traceHullShape = Require(trace, "TraceHullShape", 6);

        // TraceEndShape returns TraceResult by value; its first *parameter* is the
        // start position Vector, so the reflected member surface must come from the
        // return type (this is what the Vector.DidHit misdiagnosis came from).
        var result = _traceEndShape.ReturnType;
        _didHit = RequireMethod(result, "DidHit");
        _fraction = Require(result, "Fraction");
        _endPos = Require(result, "EndPos");
        _normal = Require(result, "Normal");
        _hitPoint = Require(result, "HitPoint");
        _hasExactHitPoint = Require(result, "HasExactHitPoint");
        _hitEntity = RequireMethod(result, "HitEntity");

        var entity = _hitEntity.ReturnType;
        _entityIndex = Require(entity, "Index");
        _designerName = entity.GetProperty("DesignerName")
            ?? throw new MissingMemberException("CEntityInstance.DesignerName");
    }

    /// <inheritdoc />
    public string Name => "reflection";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <summary>
    /// Builds a backend when the running server exposes the managed trace API.
    /// </summary>
    /// <param name="backend">The bound backend, or <see langword="null"/> when the API is absent.</param>
    /// <param name="reason">Why binding failed, for one log line.</param>
    /// <returns><see langword="true"/> when a backend was produced.</returns>
    public static bool TryCreate([NotNullWhen(true)] out ITraceBackend? backend, out string reason)
    {
        backend = null;

        try
        {
            backend = new ReflectiveTraceBackend(typeof(Utilities).Assembly);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex)
    {
        var ignore = ignoreEntityIndex >= 0 ? Utilities.GetEntityFromIndex<CBaseEntity>(ignoreEntityIndex) : null;
        var from = NewVector(start);
        var to = NewVector(end);

        var result = hullRadius > 0f
            ? HullShape(from, to, hullRadius, ignore)
            : EndShape(from, to, ignore);

        return Read(result);
    }

    private static PropertyInfo Require(Type type, string name) =>
        type.GetProperty(name) ?? throw new MissingMemberException(type.FullName + "." + name);

    private static MethodInfo RequireMethod(Type type, string name)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.Name == name && method.GetParameters().Length == 0)
            {
                return method;
            }
        }

        throw new MissingMemberException(type.FullName + "." + name);
    }

    private static MethodInfo Require(Type type, string name, int parameterCount)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name == name && method.GetParameters().Length == parameterCount)
            {
                return method;
            }
        }

        throw new MissingMemberException(type.FullName + "." + name);
    }

    private object NewVector(Vec3 value) => _vector3.Invoke(new object?[] { value.X, value.Y, value.Z });

    private object EndShape(object start, object end, CBaseEntity? ignore) =>
        _traceEndShape.Invoke(null, new object?[] { start, end, ignore, null })
        ?? throw new InvalidOperationException("TraceEndShape returned null.");

    private object HullShape(object start, object end, float radius, CBaseEntity? ignore)
    {
        var mins = NewVector(new Vec3(-radius, -radius, -radius));
        var maxs = NewVector(new Vec3(radius, radius, radius));
        return _traceHullShape.Invoke(null, new object?[] { start, end, mins, maxs, ignore, null })
            ?? throw new InvalidOperationException("TraceHullShape returned null.");
    }

    private TraceSample Read(object result)
    {
        var didHit = (bool)(_didHit.Invoke(result, null) ?? false);
        var fraction = (float)(_fraction.GetValue(result) ?? 1f);
        var exact = (bool)(_hasExactHitPoint.GetValue(result) ?? false);

        var point = ReadVector(exact ? _hitPoint.GetValue(result) : _endPos.GetValue(result));
        var normal = ReadVector(_normal.GetValue(result));
        normal = normal.IsZero() ? Vec3.UnitZ : normal.Normalized();

        var (index, name) = Describe(result);
        return new TraceSample(didHit, point, normal, fraction, index, name);
    }

    private (int Index, string? Name) Describe(object result)
    {
        try
        {
            var entity = _hitEntity.Invoke(result, null);
            if (entity is null)
            {
                return (-1, null);
            }

            var index = _entityIndex.GetValue(entity) as uint?;
            var name = _designerName.GetValue(entity) as string;
            return (index is null ? -1 : (int)index.Value, name);
        }
        catch (Exception)
        {
            // A hit with no entity behind it is static geometry, which is solid.
            return (-1, null);
        }
    }

    private Vec3 ReadVector(object? value)
    {
        if (value is null)
        {
            return Vec3.Zero;
        }

        if (value.GetType() == typeof(IntPtr))
        {
            value = _vector1.Invoke(new object?[] { value });
        }

        var type = value!.GetType();
        var x = (float)(type.GetProperty("X")?.GetValue(value) ?? 0f);
        var y = (float)(type.GetProperty("Y")?.GetValue(value) ?? 0f);
        var z = (float)(type.GetProperty("Z")?.GetValue(value) ?? 0f);
        return new Vec3(x, y, z);
    }
}
