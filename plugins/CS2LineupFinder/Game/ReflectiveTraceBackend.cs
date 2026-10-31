using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Globalization;
using System.Text;
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
internal sealed class ReflectiveTraceBackend : ITraceBackend, ITraceProbe
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
    private readonly PropertyInfo _isAllSolid;
    private readonly PropertyInfo _contents;
    private readonly MethodInfo _hitEntity;
    private readonly PropertyInfo _entityIndex;
    private readonly PropertyInfo _designerName;
    private readonly Type _traceOptionsType;
    private readonly PropertyInfo _traceOptionsInteractsWith;
    private readonly PropertyInfo _traceOptionsInteractsExclude;
    private readonly Type _contentsType;

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
        _isAllSolid = Require(result, "IsAllSolid");
        _contents = Require(result, "Contents");
        _hitEntity = RequireMethod(result, "HitEntity");

        var entity = _hitEntity.ReturnType;
        _entityIndex = Require(entity, "Index");
        _designerName = entity.GetProperty("DesignerName")
            ?? throw new MissingMemberException("CEntityInstance.DesignerName");

        _traceOptionsType = assembly.GetType("CounterStrikeSharp.API.Modules.Utils.TraceOptions", throwOnError: false)
            ?? throw new MissingMemberException("TraceOptions");
        _traceOptionsInteractsWith = Require(_traceOptionsType, "InteractsWith");
        _traceOptionsInteractsExclude = Require(_traceOptionsType, "InteractsExclude");
        _contentsType = _traceOptionsInteractsWith.PropertyType;
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

    /// <inheritdoc />
    public bool SupportsMaskedTrace => true;

    /// <inheritdoc />
    public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex, ulong interactsWith, ulong interactsExclude)
    {
        var ignore = ignoreEntityIndex >= 0 ? Utilities.GetEntityFromIndex<CBaseEntity>(ignoreEntityIndex) : null;
        var from = NewVector(start);
        var to = NewVector(end);

        var options = Activator.CreateInstance(_traceOptionsType)!;
        _traceOptionsInteractsWith.SetValue(options, Enum.ToObject(_contentsType, unchecked((long)interactsWith)));
        _traceOptionsInteractsExclude.SetValue(options, Enum.ToObject(_contentsType, unchecked((long)interactsExclude)));

        var result = hullRadius > 0f
            ? HullShape(from, to, hullRadius, ignore, options)
            : EndShape(from, to, ignore, options);

        return Read(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Dumps every readable public member of the engine
    /// <c>TraceResult</c> verbatim - nothing is selected or interpreted. Which
    /// fields the engine fills in is exactly what this exists to measure, so the
    /// dump must be complete before any code starts trusting a subset.
    /// </remarks>
    public string DescribeTrace(Vec3 start, Vec3 end, float hullRadius)
    {
        object result;
        try
        {
            var from = NewVector(start);
            var to = NewVector(end);
            result = hullRadius > 0f
                ? HullShape(from, to, hullRadius, null)
                : EndShape(from, to, null);
        }
        catch (Exception ex)
        {
            return "raw trace failed: " + ex.Message;
        }

        var b = new StringBuilder();
        b.Append("raw hull=").Append(hullRadius.ToString("0.##", CultureInfo.InvariantCulture));
        AppendVec(b, " start", start);
        AppendVec(b, " end", end);

        var type = result.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value;
            try
            {
                value = property.GetValue(result);
            }
            catch (Exception ex)
            {
                b.Append(' ').Append(property.Name).Append("=<err: ").Append(ex.Message).Append('>');
                continue;
            }

            b.Append(' ').Append(property.Name).Append('=');
            switch (value)
            {
                case null:
                    b.Append("null");
                    break;
                case float f:
                    b.Append(f.ToString("0.######", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    b.Append(d.ToString("0.######", CultureInfo.InvariantCulture));
                    break;
                case Vector vec:
                    AppendVec(b, "", new Vec3(vec.X, vec.Y, vec.Z));
                    break;
                case bool flag:
                    b.Append(flag ? "1" : "0");
                    break;
                default:
                    b.Append(value.ToString());
                    break;
            }
        }

        return b.ToString();
    }

    /// <summary>Appends a labelled vector as comma separated coordinates.</summary>
    private static void AppendVec(StringBuilder b, string label, Vec3 v)
    {
        b.Append(label).Append("=(");
        b.Append(v.X.ToString("0.######", CultureInfo.InvariantCulture));
        b.Append(',');
        b.Append(v.Y.ToString("0.######", CultureInfo.InvariantCulture));
        b.Append(',');
        b.Append(v.Z.ToString("0.######", CultureInfo.InvariantCulture));
        b.Append(')');
    }

    /// <inheritdoc />
    public bool RawHit(Vec3 start, Vec3 end, float hullRadius)
    {
        try
        {
            var from = NewVector(start);
            var to = NewVector(end);
            object result = hullRadius > 0f
                ? HullShape(from, to, hullRadius, null)
                : EndShape(from, to, null);
            return (bool)(_didHit.Invoke(result, null) ?? false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool MaskedHit(Vec3 start, Vec3 end, float hullRadius, ulong interactsWith, ulong interactsExclude)
    {
        try
        {
            var from = NewVector(start);
            var to = NewVector(end);
            var options = Activator.CreateInstance(_traceOptionsType)!;
            _traceOptionsInteractsWith.SetValue(options, Enum.ToObject(_contentsType, unchecked((long)interactsWith)));
            _traceOptionsInteractsExclude.SetValue(options, Enum.ToObject(_contentsType, unchecked((long)interactsExclude)));

            object result = hullRadius > 0f
                ? HullShape(from, to, hullRadius, null, options)
                : EndShape(from, to, null, options);
            return (bool)(_didHit.Invoke(result, null) ?? false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string DescribeHitEntity(Vec3 start, Vec3 end, float hullRadius)
    {
        try
        {
            var from = NewVector(start);
            var to = NewVector(end);
            object result = hullRadius > 0f
                ? HullShape(from, to, hullRadius, null)
                : EndShape(from, to, null);
            if (!(bool)(_didHit.Invoke(result, null) ?? false))
            {
                return "no-hit";
            }

            var entity = _hitEntity.Invoke(result, null);
            if (entity is null)
            {
                return "hit-world(static)";
            }

            string name = (_designerName.GetValue(entity) as string) ?? "unknown-designer";
            int index = Convert.ToInt32(_entityIndex.GetValue(entity), CultureInfo.InvariantCulture);
            return string.Create(CultureInfo.InvariantCulture, $"entity {name} idx {index}");
        }
        catch (Exception ex)
        {
            return "describe-failed: " + ex.Message;
        }
    }

    /// <inheritdoc />
    public string DescribeTraceMasked(Vec3 start, Vec3 end, float hullRadius, ulong interactsWith, ulong interactsExclude)
    {
        object result;
        try
        {
            var from = NewVector(start);
            var to = NewVector(end);
            var options = Activator.CreateInstance(_traceOptionsType)!;
            _traceOptionsInteractsWith.SetValue(options, Enum.ToObject(_contentsType, unchecked((long)interactsWith)));
            _traceOptionsInteractsExclude.SetValue(options, Enum.ToObject(_contentsType, unchecked((long)interactsExclude)));

            result = hullRadius > 0f
                ? HullShape(from, to, hullRadius, null, options)
                : EndShape(from, to, null, options);
        }
        catch (Exception ex)
        {
            return "masked trace failed: " + ex.Message;
        }

        var b = new StringBuilder();
        b.Append("mask with=0x").Append(interactsWith.ToString("X", CultureInfo.InvariantCulture))
            .Append(" excl=0x").Append(interactsExclude.ToString("X", CultureInfo.InvariantCulture));
        b.Append(" hull=").Append(hullRadius.ToString("0.##", CultureInfo.InvariantCulture));
        AppendVec(b, " start", start);
        AppendVec(b, " end", end);

        var type = result.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value;
            try
            {
                value = property.GetValue(result);
            }
            catch (Exception ex)
            {
                b.Append(' ').Append(property.Name).Append("=<err: ").Append(ex.Message).Append('>');
                continue;
            }

            b.Append(' ').Append(property.Name).Append('=');
            switch (value)
            {
                case null:
                    b.Append("null");
                    break;
                case float f:
                    b.Append(f.ToString("0.######", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    b.Append(d.ToString("0.######", CultureInfo.InvariantCulture));
                    break;
                case Vector vec:
                    AppendVec(b, "", new Vec3(vec.X, vec.Y, vec.Z));
                    break;
                case bool flag:
                    b.Append(flag ? "1" : "0");
                    break;
                default:
                    b.Append(value.ToString());
                    break;
            }
        }

        return b.ToString();
    }

    /// <summary>
    /// One line describing the exact Trace API surface this backend bound: method
    /// names, parameter names and parameter types. What the 4th/6th parameters
    /// actually are decides whether a collision mask can be passed at all - this
    /// is measured from the loaded assembly, not assumed from documentation.
    /// </summary>
    public string DescribeApiSignature()
    {
        var b = new StringBuilder();
        foreach (var method in new[] { _traceEndShape, _traceHullShape })
        {
            b.Append(method.DeclaringType!.Name).Append('.').Append(method.Name).Append('(');
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    b.Append(", ");
                }

                b.Append(parameters[i].ParameterType.Name).Append(' ').Append(parameters[i].Name!);
            }

            b.Append(") -> ").Append(method.ReturnType.Name).Append("; ");
        }

        return b.ToString();
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

    private object EndShape(object start, object end, CBaseEntity? ignore, object options) =>
        _traceEndShape.Invoke(null, new object?[] { start, end, ignore, options })
        ?? throw new InvalidOperationException("TraceEndShape returned null.");

    private object HullShape(object start, object end, float radius, CBaseEntity? ignore)
    {
        var mins = NewVector(new Vec3(-radius, -radius, -radius));
        var maxs = NewVector(new Vec3(radius, radius, radius));
        return _traceHullShape.Invoke(null, new object?[] { start, end, mins, maxs, ignore, null })
            ?? throw new InvalidOperationException("TraceHullShape returned null.");
    }

    private object HullShape(object start, object end, float radius, CBaseEntity? ignore, object options)
    {
        var mins = NewVector(new Vec3(-radius, -radius, -radius));
        var maxs = NewVector(new Vec3(radius, radius, radius));
        return _traceHullShape.Invoke(null, new object?[] { start, end, mins, maxs, ignore, options })
            ?? throw new InvalidOperationException("TraceHullShape returned null.");
    }

    private TraceSample Read(object result)
    {
        var didHit = (bool)(_didHit.Invoke(result, null) ?? false);
        var fraction = (float)(_fraction.GetValue(result) ?? 1f);
        var exact = (bool)(_hasExactHitPoint.GetValue(result) ?? false);
        var isAllSolid = (bool)(_isAllSolid.GetValue(result) ?? false);
        var contentsRaw = Convert.ToUInt64(_contents.GetValue(result) ?? 0);

        var point = ReadVector(exact ? _hitPoint.GetValue(result) : _endPos.GetValue(result));
        var normal = ReadVector(_normal.GetValue(result));
        normal = normal.IsZero() ? Vec3.UnitZ : normal.Normalized();

        var (index, name) = Describe(result);
        return new TraceSample(didHit, point, normal, fraction, index, name, contentsRaw, isAllSolid);
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
