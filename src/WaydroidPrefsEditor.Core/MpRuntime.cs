using System.Reflection;
using System.Runtime.Loader;
using MemoryPack;

namespace WaydroidPrefsEditor.Core;

/// <summary>
/// Loads the game's compiled save types into a private collectible context and bridges them
/// to the app's bundled MemoryPack. Both sides must share ONE MemoryPack identity, so
/// <c>MemoryPack</c>/<c>MemoryPack.Core</c> are always deferred to the default context.
/// </summary>
public sealed class MpRuntime : IDisposable
{
    const string KeyAttributeTypeName = "TheOne.Extensions.KeyAttribute";
    const string RuntimeInitializeAttributeName = "UnityEngine.RuntimeInitializeOnLoadMethodAttribute";
    const string UnityAssemblyName = "UnityEngine";
    const string UnityResourcePrefix = "WaydroidPrefsEditor.Unity.";

    readonly GameLoadContext _context;
    readonly List<Assembly> _gameAssemblies = new();
    readonly List<string> _warnings = new();
    readonly Dictionary<string, Type> _byKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    bool _indexed;
    bool _disposed;

    MpRuntime(GameLoadContext context) => _context = context;

    public IReadOnlyList<string> LoadWarnings => _warnings;

    /// <summary>Assemblies the game folder yielded — a folder entry stands for many files.</summary>
    public int LoadedAssemblies { get; private set; }

    public static MpRuntime Load(IReadOnlyList<string> gameDlls)
    {
        var runtime = new MpRuntime(new GameLoadContext(gameDlls));
        runtime.LoadGameAssemblies();
        runtime.RunRuntimeRegistrations();
        return runtime;
    }

    /// <summary>
    /// First type carrying <c>[Key(key)]</c>, else the first type whose full or simple name is
    /// <paramref name="key"/>. Frameworks that key their saves by the data type's own name (TheOne's
    /// UITemplate stores, for one) carry no <c>[Key]</c> attribute at all.
    /// </summary>
    public Type? ResolveTypeForKey(string key)
    {
        EnsureIndex();
        return _byKey.GetValueOrDefault(key) ?? _byName.GetValueOrDefault(key);
    }

    /// <summary>
    /// One pass over every type, ever. Reading attributes is expensive and the app resolves a key
    /// per grid row, so a scan per lookup turns a 40-row file into seconds of frozen UI.
    /// </summary>
    void EnsureIndex()
    {
        if (_indexed)
            return;
        _indexed = true;

        foreach (var type in EnumerateTypes())
        {
            if (GetKeyValue(type) is { } key)
                _byKey.TryAdd(key, type);
            if (type.FullName is { } fullName)
                _byName.TryAdd(fullName, type);
            _byName.TryAdd(type.Name, type);
        }
    }

    /// <summary>
    /// Whether the loaded game can encode and decode <paramref name="type"/> with MemoryPack.
    /// The <c>[MemoryPackable]</c> attribute would be the direct answer, but reading attributes loads
    /// the attribute's own assembly: a type whose Unity assembly is not among the imported DLLs
    /// throws there and would be written off even though it loads and round-trips fine. The marker
    /// MemoryPack itself dispatches on — every generated save type implements it — never does.
    /// </summary>
    public static bool IsMemoryPackable(Type type)
    {
        try
        {
            return typeof(IMemoryPackFormatterRegister).IsAssignableFrom(type);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Reading attributes touches the attribute's own assembly, so an assembly with incomplete
    /// dependencies (a Unity member without the Unity DLLs folder) throws here. A type that cannot
    /// be inspected is skipped — one unreadable assembly must never sink the whole package load.
    /// </summary>
    static object[]? TryAttributes(Type type)
    {
        try
        {
            return type.GetCustomAttributes(inherit: true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object? Decode(Type type, byte[] bytes) =>
        MemoryPackSerializer.Deserialize(type, bytes, MemoryPackSerializerOptions.Default);

    public byte[] Encode(Type type, object value) =>
        MemoryPackSerializer.Serialize(type, value, MemoryPackSerializerOptions.Default);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _byKey.Clear();
        _byName.Clear();
        _indexed = false;
        // Deliberately no _context.Unload(): the game's formatters are registered into the
        // process-wide MemoryPackFormatterProvider and would dangle on unload.
    }

    void LoadGameAssemblies()
    {
        var loaded = 0;
        foreach (var (name, path) in _context.GameIndex
                     .OrderBy(kv => Depth(kv.Value))
                     .ThenBy(kv => kv.Value, StringComparer.Ordinal))
        {
            if (IsBridgedAssembly(name))
                continue;
            try
            {
                _gameAssemblies.Add(_context.LoadFromAssemblyPath(path));
                loaded++;
            }
            catch (Exception ex)
            {
                _warnings.Add($"{name}: {ex.Message}");
            }
        }

        if (loaded == 0)
            _warnings.Add(
                "no assemblies loaded — add the game's compiled DLLs (e.g. .../assets/bin/Data/Managed) "
                + "in the DLLs tab");
        LoadedAssemblies = loaded;

        if (_context.CollapsedDuplicates > 0)
            _warnings.Add(
                $"{_context.CollapsedDuplicates} duplicate assembly name(s) among the added DLLs"
                + " — kept the shallowest copy of each");
    }

    static int Depth(string path) => path.Count(c => c is '/' or '\\');

    static bool IsBridgedAssembly(string name) =>
        name is "MemoryPack" or "MemoryPack.Core"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name is "System" or "mscorlib" or "netstandard" or "WindowsBase";

    /// <summary>
    /// MemoryPack self-registers a generated formatter from the save type itself, but a formatter for
    /// a member type the game does not own — R3's <c>ReactiveProperty&lt;T&gt;</c>, say — has no such
    /// path: the game registers it from a Unity <c>[RuntimeInitializeOnLoadMethod]</c> hook, which
    /// never fires outside the player, so the generated formatter dies with "not registered in this
    /// provider". Replay every such hook the game ships, so the MemoryPack provider — the one
    /// instance the game's generated formatters resolve against, since <c>MemoryPack.Core</c> is
    /// bridged to the app — ends up with them. Assemblies that do not reference MemoryPack cannot
    /// register a formatter, so their bootstraps (audio, analytics, …) are left untouched.
    /// </summary>
    void RunRuntimeRegistrations()
    {
        foreach (var method in RuntimeInitializers())
        {
            try
            {
                method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                _warnings.Add($"{method.DeclaringType?.Name}.{method.Name}: {ex.Message}");
            }
        }
    }

    IEnumerable<MethodInfo> RuntimeInitializers()
    {
        foreach (var assembly in _gameAssemblies.Where(ReferencesMemoryPack))
        {
            foreach (var type in TypesOf(assembly))
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    bool registers;
                    try
                    {
                        registers = method.GetParameters().Length == 0
                            && method.CustomAttributes.Any(
                                a => a.AttributeType.FullName == RuntimeInitializeAttributeName);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (registers)
                        yield return method;
                }
            }
        }
    }

    static bool ReferencesMemoryPack(Assembly assembly)
    {
        try
        {
            return assembly.GetReferencedAssemblies()
                .Any(reference => reference.Name is "MemoryPack" or "MemoryPack.Core");
        }
        catch (Exception)
        {
            return false;
        }
    }

    IEnumerable<Type> EnumerateTypes() => _context.Assemblies.SelectMany(TypesOf);

    /// <summary>
    /// Reading attributes touches the attribute's own assembly, so an assembly with incomplete
    /// dependencies (a Unity member without the Unity DLLs folder) throws here. A type that cannot
    /// be inspected is skipped — one unreadable assembly must never sink the whole package load.
    /// </summary>
    static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch (Exception)
        {
            return [];
        }

        return types.OfType<Type>();
    }

    static string? GetKeyValue(Type type)
    {
        foreach (var attribute in TryAttributes(type) ?? [])
        {
            var attributeType = attribute.GetType();
            if (attributeType.FullName != KeyAttributeTypeName)
                continue;

            var named = attributeType.GetProperty("Key") ?? (MemberInfo?)attributeType.GetField("Key");
            if (named is not null)
                return GetString(named, attribute);

            foreach (var property in attributeType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.PropertyType == typeof(string))
                    return (string?)property.GetValue(attribute);

            foreach (var field in attributeType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (field.FieldType == typeof(string))
                    return (string?)field.GetValue(attribute);
        }
        return null;
    }

    static string? GetString(MemberInfo member, object instance) => member switch
    {
        PropertyInfo p => (string?)p.GetValue(instance),
        FieldInfo f => (string?)f.GetValue(instance),
        _ => null,
    };

    sealed class GameLoadContext : AssemblyLoadContext
    {
        readonly Dictionary<string, string> _gameIndex;
        readonly Dictionary<string, Assembly?> _unityAssemblies = new();

        public GameLoadContext(IReadOnlyList<string> gameDlls)
            : base("WaydroidPrefsEditor.GameDlls", isCollectible: true)
        {
            _gameIndex = BuildIndex(gameDlls, out var collapsed);
            CollapsedDuplicates = collapsed;
        }

        public int CollapsedDuplicates { get; }

        public IReadOnlyDictionary<string, string> GameIndex => _gameIndex;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (name is null || IsBridgedAssembly(name))
                return null;

            if (_gameIndex.TryGetValue(name, out var gamePath))
                return LoadFromAssemblyPath(gamePath);

            if (name == UnityAssemblyName || name.StartsWith(UnityAssemblyName + ".", StringComparison.Ordinal))
                return LoadBundledUnity(name);

            return null;
        }

        /// <summary>
        /// Unity's engine assemblies are embedded in this app, so Unity-native members resolve with no
        /// Unity install — and a game folder that ships only its own code (a project's
        /// <c>ScriptAssemblies</c>) still gets the modules its save types reference. A game folder that
        /// carries its own copies wins, because <see cref="Load"/> checks the index first.
        /// </summary>
        Assembly? LoadBundledUnity(string name)
        {
            if (_unityAssemblies.TryGetValue(name, out var cached))
                return cached;

            using var stream = typeof(GameLoadContext).Assembly
                .GetManifestResourceStream($"{UnityResourcePrefix}{name}.dll");
            return _unityAssemblies[name] = stream is null ? null : LoadFromStream(stream);
        }

        /// <summary>
        /// Indexed by file name (the assembly-name heuristic this class already relies on). On a name
        /// collision the shallowest path wins, tie-broken ordinally; <paramref name="collapsed"/>
        /// counts drops. Paths that do not exist are skipped.
        /// </summary>
        static Dictionary<string, string> BuildIndex(IEnumerable<string> paths, out int collapsed)
        {
            collapsed = 0;
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths.SelectMany(Expand))
            {
                if (!File.Exists(path))
                    continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (index.TryGetValue(name, out var existing))
                {
                    collapsed++;
                    if (Depth(existing) < Depth(path)
                        || (Depth(existing) == Depth(path) && StringComparer.Ordinal.Compare(existing, path) <= 0))
                        continue;
                }
                index[name] = path;
            }
            return index;
        }

        /// <summary>
        /// A folder entry stands for the assemblies inside it. The game's compiled types come as a
        /// folder (a project's ScriptAssemblies, a build's Managed), and picking that folder is what
        /// users reach for — 300 individual files is not an import.
        /// </summary>
        static IEnumerable<string> Expand(string path)
        {
            if (!Directory.Exists(path))
                return [path];
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                })
                .Where(file => Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase));
        }
    }
}
