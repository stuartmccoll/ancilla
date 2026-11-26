using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ancilla.Core;

/// <summary>The kinds of JSON value a field can hold. A whole number and one with a fraction are told apart.</summary>
[Flags]
public enum JsonKind
{
    None = 0,
    Null = 1,
    Boolean = 2,
    Integer = 4,
    Fraction = 8,
    String = 16,
    Object = 32,
    Array = 64,
    Number = Integer | Fraction,
    Any = Null | Boolean | Number | String | Object | Array,
}

/// <summary>
/// The shape of a JSON document: every field's path and the kinds of value seen there, never the
/// values themselves. Paths look like <c>Players[].Stats.CoreStats.Kills</c>: <c>[]</c> is any
/// element of a list, and <c>*</c> any key of a dictionary. The root is the empty path. Paths
/// compare without regard to case, as the client reads them.
/// </summary>
public sealed partial class JsonShape
{
    private readonly Dictionary<string, JsonKind> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    private JsonShape() { }

    /// <summary>Every path, in the order first seen, with the kinds seen there.</summary>
    public IEnumerable<KeyValuePair<string, JsonKind>> Fields => _order.Select(p => new KeyValuePair<string, JsonKind>(p, _fields[p]));

    public bool Contains(string path) => _fields.ContainsKey(path);

    public JsonKind this[string path] => _fields.GetValueOrDefault(path);

    /// <summary>
    /// The shape of <paramref name="json"/>. Objects at <paramref name="dictionaries"/> are read as
    /// dictionaries, their keys all becoming <c>*</c>. Elsewhere, a key that doesn't look like a
    /// field name (an ID, a number, a XUID, anything with spaces or punctuation) becomes <c>*</c> too,
    /// so a shape never repeats data.
    /// </summary>
    public static JsonShape Of(JsonElement json, IReadOnlySet<string>? dictionaries = null)
    {
        var shape = new JsonShape();
        shape.Walk(json, "", dictionaries ?? new HashSet<string>());
        return shape;
    }

    public static JsonShape Parse(string json, IReadOnlySet<string>? dictionaries = null)
    {
        using var doc = JsonDocument.Parse(json);
        return Of(doc.RootElement, dictionaries);
    }

    private void Walk(JsonElement e, string path, IReadOnlySet<string> dictionaries)
    {
        Add(path, KindOf(e));
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var isDictionary = dictionaries.Contains(path);
                foreach (var p in e.EnumerateObject())
                    Walk(p.Value, Member(path, isDictionary || !FieldName().IsMatch(p.Name) ? "*" : p.Name), dictionaries);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    Walk(item, path + "[]", dictionaries);
                break;
        }
    }

    private void Add(string path, JsonKind kind)
    {
        if (_fields.TryGetValue(path, out var seen)) _fields[path] = seen | kind;
        else { _fields[path] = kind; _order.Add(path); }
    }

    private static JsonKind KindOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => JsonKind.Null,
        JsonValueKind.True or JsonValueKind.False => JsonKind.Boolean,
        JsonValueKind.Number => e.TryGetInt64(out _) || e.TryGetUInt64(out _) ? JsonKind.Integer : JsonKind.Fraction,
        JsonValueKind.String => JsonKind.String,
        JsonValueKind.Object => JsonKind.Object,
        JsonValueKind.Array => JsonKind.Array,
        _ => JsonKind.None,
    };

    internal static string Member(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    /// <summary>The path one level up: <c>A.B</c> → <c>A</c>, <c>A[]</c> → <c>A</c>, <c>A</c> → the root.</summary>
    public static string Parent(string path) =>
        path.EndsWith("[]", StringComparison.Ordinal) ? path[..^2]
        : path.LastIndexOf('.') is var i and >= 0 ? path[..i]
        : "";

    /// <summary>A member path (<c>A.B</c>), as opposed to a list element or a dictionary key.</summary>
    public static bool IsMember(string path) =>
        path.Length > 0 && !path.EndsWith("[]", StringComparison.Ordinal) && !path.EndsWith(".*", StringComparison.Ordinal) && path != "*";

    /// <summary>"text", "a whole number", "a list or null"...</summary>
    public static string Describe(JsonKind kind)
    {
        var names = new List<string>();
        if ((kind & JsonKind.Number) == JsonKind.Number) names.Add("a number");
        else if (kind.HasFlag(JsonKind.Integer)) names.Add("a whole number");
        else if (kind.HasFlag(JsonKind.Fraction)) names.Add("a number with a fraction");
        if (kind.HasFlag(JsonKind.String)) names.Add("text");
        if (kind.HasFlag(JsonKind.Boolean)) names.Add("true or false");
        if (kind.HasFlag(JsonKind.Object)) names.Add("an object");
        if (kind.HasFlag(JsonKind.Array)) names.Add("a list");
        if (kind.HasFlag(JsonKind.Null)) names.Add("null");
        return names.Count == 0 ? "nothing" : string.Join(" or ", names);
    }

    // Halo's field names: letters, digits and underscores, starting with a letter. Not keys like
    // "xuid(...)", GUIDs, team numbers, locales ("fr-FR") or gamertags with spaces.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex FieldName();
}

/// <summary>
/// What a model type reads from JSON, as <see cref="HaloWaypointClient"/> deserialises it: each
/// field's path and the kinds of value that deserialise into it.
/// </summary>
public sealed class ModelShape
{
    private readonly Dictionary<string, JsonKind> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private readonly HashSet<string> _dictionaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly NullabilityInfoContext _nullability = new();   // not thread-safe, so one per shape

    private ModelShape() { }

    public IEnumerable<KeyValuePair<string, JsonKind>> Fields => _order.Select(p => new KeyValuePair<string, JsonKind>(p, _fields[p]));

    /// <summary>Paths that hold dictionaries, whose keys are data rather than field names.</summary>
    public IReadOnlySet<string> Dictionaries => _dictionaries;

    /// <summary>
    /// Paths of objects whose other members the model keeps whole (<c>[JsonExtensionData]</c>), such
    /// as the per-mode stat groups, which differ from one match to the next.
    /// </summary>
    public IReadOnlySet<string> Open => _open;

    public bool Contains(string path) => _fields.ContainsKey(path);

    public JsonKind this[string path] => _fields.GetValueOrDefault(path);

    public static ModelShape Of(Type model)
    {
        var shape = new ModelShape();
        shape.Walk(model, "", JsonKind.None, 0);
        return shape;
    }

    private void Walk(Type type, string path, JsonKind nullable, int depth)
    {
        if (depth > 16) throw new InvalidOperationException($"{type.Name} nests too deeply to describe.");
        if (Nullable.GetUnderlyingType(type) is { } inner) { type = inner; nullable = JsonKind.Null; }

        JsonKind Leaf(JsonKind kind) { Add(path, kind | nullable); return kind; }

        if (type == typeof(string) || type == typeof(DateTimeOffset) || type == typeof(DateTime) || type == typeof(Guid)
            || type == typeof(TimeSpan) || type == typeof(Uri)) { Leaf(JsonKind.String); return; }
        if (type == typeof(bool)) { Leaf(JsonKind.Boolean); return; }
        if (type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte)) { Leaf(JsonKind.Integer); return; }
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) { Leaf(JsonKind.Number); return; }
        if (type == typeof(JsonElement) || type == typeof(object)) { Leaf(JsonKind.Any); return; }

        if (DictionaryValue(type) is { } value)
        {
            Leaf(JsonKind.Object);
            _dictionaries.Add(path);
            Walk(value, JsonShape.Member(path, "*"), NullableValue(value), depth + 1);
            return;
        }
        if (ListElement(type) is { } element)
        {
            Leaf(JsonKind.Array);
            Walk(element, path + "[]", NullableValue(element), depth + 1);
            return;
        }

        Leaf(JsonKind.Object);
        if (type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.IsDefined(typeof(JsonExtensionDataAttribute))))
            _open.Add(path);
        var ctorParams = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()?
            .GetParameters().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || prop.IsDefined(typeof(JsonIgnoreAttribute))
                || prop.IsDefined(typeof(JsonExtensionDataAttribute))) continue;
            // Only what deserialising sets: settable properties and constructor parameters, not computed ones.
            if (prop.SetMethod is not { IsPublic: true } && !ctorParams.Contains(prop.Name)) continue;

            var name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
            var child = JsonShape.Member(path, name);
            var canBeNull = prop.PropertyType.IsValueType
                ? Nullable.GetUnderlyingType(prop.PropertyType) is not null
                : _nullability.Create(prop).ReadState != NullabilityState.NotNull;
            if (prop.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType == typeof(LocalizedStringConverter))
                Add(child, JsonKind.String | JsonKind.Object | JsonKind.Null);   // either shape; its insides aren't checked
            else
                Walk(prop.PropertyType, child, canBeNull ? JsonKind.Null : JsonKind.None, depth + 1);
        }
    }

    // List elements and dictionary values: value types only take null when they're Nullable<T>. A
    // reference type's annotation isn't visible on a generic argument, so null is taken as allowed.
    private static JsonKind NullableValue(Type t) =>
        !t.IsValueType || Nullable.GetUnderlyingType(t) is not null ? JsonKind.Null : JsonKind.None;

    private void Add(string path, JsonKind kind)
    {
        if (_fields.TryAdd(path, kind)) _order.Add(path);
    }

    private static Type? DictionaryValue(Type t) =>
        t.IsGenericType && t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)) is { } d && d.GetGenericArguments()[0] == typeof(string)
            ? d.GetGenericArguments()[1] : null;

    private static Type? ListElement(Type t) =>
        t.IsArray ? t.GetElementType()
        : t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t)
            && t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } e
            ? e.GetGenericArguments()[0] : null;
}

public enum DriftKind { Added, Missing, Retyped }

/// <summary>
/// One difference between a live response's shape and what was expected. <paramref name="Breaks"/>
/// says whether it could break Ancilla: a field a model needs is missing, or holds a kind of value
/// the model can't take.
/// </summary>
/// <paramref name="Inside"/> lists the fields inside an added object or list, with their kinds.
public sealed record DriftItem(DriftKind Kind, string Path, bool Breaks, string Detail, IReadOnlyList<string>? Inside = null);

public static class ShapeDiff
{
    /// <summary>
    /// Compares a live response with what <paramref name="model"/> reads and with the last known
    /// shape (the endpoint's fixture). A field the model reads breaks Ancilla when it holds a kind
    /// of value the model can't take, or when it's missing and the model doesn't allow null (a
    /// nullable field is optional). Everything else is checked against <paramref name="known"/>:
    /// fields added, missing or retyped since it was written. Nulls don't count as a change of
    /// kind there, and neither do whole numbers turning up where fractions were. A field is only
    /// missing when the object that should hold it is there: an empty list or a null object says
    /// nothing about what's inside it, and a member of an open object (a mode's stat group) isn't
    /// missing either, as each match has only its own mode's. Inside <paramref name="untracked"/>
    /// paths (big objects Ancilla doesn't read) nothing is compared.
    /// </summary>
    public static IReadOnlyList<DriftItem> Compare(ModelShape? model, JsonShape? known, JsonShape live,
        IReadOnlyCollection<string>? untracked = null)
    {
        var items = new List<DriftItem>();
        bool ParentIsObject(string path) => JsonShape.IsMember(path) && live[JsonShape.Parent(path)].HasFlag(JsonKind.Object);

        if (model is not null)
            foreach (var (path, allowed) in model.Fields)
            {
                if (!live.Contains(path))
                {
                    if (!allowed.HasFlag(JsonKind.Null) && ParentIsObject(path))
                        items.Add(new(DriftKind.Missing, path, true, $"Ancilla needs {JsonShape.Describe(allowed)}"));
                    continue;
                }
                if ((live[path] & ~allowed) != JsonKind.None)
                    items.Add(new(DriftKind.Retyped, path, true,
                        $"Ancilla takes {JsonShape.Describe(allowed)}, now {JsonShape.Describe(live[path])}"));
            }

        if (known is null) return items;
        bool Read(string path) => model?.Contains(path) == true;
        static JsonKind Settled(JsonKind k) =>
            (k.HasFlag(JsonKind.Fraction) ? (k & ~JsonKind.Fraction) | JsonKind.Integer : k) & ~JsonKind.Null;

        bool Untracked(string path) => untracked?.Any(u => Inside(path, u)) == true;

        foreach (var (path, kind) in live.Fields)
        {
            if (Read(path) || Untracked(path)) continue;
            if (!known.Contains(path))
            {
                if (!known.Contains(JsonShape.Parent(path))) continue;   // inside something already reported as added
                var inside = live.Fields.Where(f => Inside(f.Key, path)).Select(f => $"{f.Key}: {JsonShape.Describe(f.Value)}").ToList();
                items.Add(new(DriftKind.Added, path, false,
                    JsonShape.Describe(kind) + (inside.Count > 0 ? $", {inside.Count} more fields inside" : ""), inside.Count > 0 ? inside : null));
                continue;
            }
            var was = Settled(known[path]);
            var now = Settled(kind);
            if (was != JsonKind.None && now != JsonKind.None && (now & ~was) != JsonKind.None)
                items.Add(new(DriftKind.Retyped, path, false, $"was {JsonShape.Describe(known[path] & ~JsonKind.Null)}, now {JsonShape.Describe(kind)}"));
        }

        foreach (var (path, kind) in known.Fields)
        {
            if (live.Contains(path) || !ParentIsObject(path) || Untracked(path)) continue;
            if (model?.Open.Contains(JsonShape.Parent(path)) == true && !Read(path)) continue;   // another mode's stat group
            if (!Read(path))
                items.Add(new(DriftKind.Missing, path, false, $"was {JsonShape.Describe(kind)}"));
            else if (model![path].HasFlag(JsonKind.Null))   // one Ancilla needs is reported above
                items.Add(new(DriftKind.Missing, path, false, $"was {JsonShape.Describe(kind)}; Ancilla can do without it"));
        }
        return items;
    }

    /// <summary>Whether <paramref name="path"/> is somewhere inside <paramref name="container"/>.</summary>
    private static bool Inside(string path, string container) =>
        path.Length > container.Length && path.StartsWith(container, StringComparison.OrdinalIgnoreCase)
        && (container.Length == 0 || path[container.Length] is '.' or '[');
}
