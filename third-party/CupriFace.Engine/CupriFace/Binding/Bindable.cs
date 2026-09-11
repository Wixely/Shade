namespace CupriFace.Binding;

/// <summary>
/// Marks a model type as bindable. The CupriFace source generator emits an
/// <see cref="IBindableAccessor"/> implementation for the (partial) type so binding
/// resolves properties with zero reflection — AOT/trim-clean (DESIGN.md §6).
/// Types without this attribute still bind via a reflection fallback (not AOT-safe).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class CupriBindableAttribute : Attribute;

/// <summary>
/// Reflection-free property get/set by name. Implemented by the source generator for
/// <see cref="CupriBindableAttribute"/> types; consumed by the binding engine. The set path
/// is what makes two-way binding (interaction write-back) AOT/trim-clean — a reflection
/// setter's metadata is trimmed away in a published/AOT build, so bound controls stop working.
/// </summary>
public interface IBindableAccessor
{
    /// <summary>Return the value of the named public property, or null if unknown.</summary>
    object? GetBindable(string name);

    /// <summary>Convert <paramref name="value"/> to the named settable property's type and assign
    /// it. Returns false if the name is unknown/read-only or the value can't be converted.</summary>
    bool SetBindable(string name, object? value);
}

/// <summary>Value coercion used by generated <c>SetBindable</c> switches (mirrors the binding
/// engine's reflection setter). AOT-safe: no reflection, only <see cref="Convert"/>.</summary>
public static class BindableConvert
{
    public static bool TryConvert<T>(object? value, out T result)
    {
        try
        {
            if (value is null) { result = default!; return true; }
            var t = typeof(T);
            object c =
                t == typeof(int) ? Convert.ToInt32(value) :
                t == typeof(bool) ? Convert.ToBoolean(value) :
                t == typeof(double) ? Convert.ToDouble(value) :
                t == typeof(float) ? Convert.ToSingle(value) :
                t == typeof(long) ? Convert.ToInt64(value) :
                t == typeof(string) ? (value.ToString() ?? "") :
                Convert.ChangeType(value, t);
            result = (T)c;
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
        {
            result = default!; // invalid partial entry (e.g. "" or "-" into an int) — leave unchanged
            return false;
        }
    }
}
