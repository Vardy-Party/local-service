using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using VardyParty.LocalService.Compute;

namespace VardyParty.LocalService;

/// <summary>
/// Strategy assemblies are plugins. They are not package references of the host.
/// The private release copies the published plugin DLLs next to the host, and
/// this loader calls their registration methods when those files are present.
/// </summary>
internal static class StrategyPlugins
{
    private const string EntryTypeName = "VardyParty.LocalService.Plugins.LocalServicePluginEntry";

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var directory = AppContext.BaseDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "VardyParty.LocalService.*.dll").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var assemblyName = Path.GetFileNameWithoutExtension(path);
            if (assemblyName is "VardyParty.LocalService" or "VardyParty.LocalService.Client")
            {
                continue;
            }

            Load(path);
        }

        foreach (var entry in Entries())
        {
            entry.GetMethod("Register")?.Invoke(null, [services, configuration]);
        }
    }

    public static object? Find(IServiceProvider services, string operation)
    {
        foreach (var entry in Entries())
        {
            var declared = entry.GetField("Operation")?.GetValue(null) as string;
            if (!string.Equals(declared, operation, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return entry.GetMethod("GetPlugin")?.Invoke(null, [services]);
        }

        return null;
    }

    public static IDisposable Begin(IServiceProvider services, string operation, string? dnsOverHttps, string? proxy)
    {
        foreach (var entry in Entries())
        {
            var declared = entry.GetField("Operation")?.GetValue(null) as string;
            if (!string.Equals(declared, operation, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var begin = entry.GetMethod("Begin");
            if (begin is null)
            {
                return EmptyBegin.Instance;
            }

            var args = begin.GetParameters().Length <= 3
                ? new object?[] { services, dnsOverHttps, proxy }
                : new object?[]
                {
                    services,
                    dnsOverHttps,
                    proxy,
                    proxy is null ? null : PhonePathBudget.ChipWait,
                    proxy is null ? null : PhonePathBudget.PlaylistWait,
                    proxy is null ? null : PhonePathBudget.SegmentWait,
                    proxy is null ? null : PhonePathBudget.WarmPlaylistWait,
                    proxy is null ? null : PhonePathBudget.WarmSegmentWait
                };
            return begin.Invoke(null, args) as IDisposable ?? EmptyBegin.Instance;
        }

        return EmptyBegin.Instance;
    }

    private static IEnumerable<Type> Entries() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(EntryTypeName))
            .Where(type => type is not null)!;

    private sealed class EmptyBegin : IDisposable
    {
        public static readonly EmptyBegin Instance = new();
        public void Dispose() { }
    }

    public static string StrategyId(string operation) =>
        Entry(operation)?.GetField("StrategyId")?.GetValue(null) as string ?? operation;

    public static Task<object> ResolveAsync(IServiceProvider services, string operation, params object?[] args) =>
        InvokeReturnedTask(EntryMethod(operation, "ResolveAsync"), WithServices(services, args));

    public static IAsyncEnumerable<object> StreamAsync(
        IServiceProvider services,
        string operation,
        params object?[] args)
    {
        var enumerable = EntryMethod(operation, "StreamAsync").Invoke(null, WithServices(services, args))
            ?? throw new InvalidOperationException($"Plugin {operation} StreamAsync returned null.");
        var cancellationToken = args.OfType<CancellationToken>().LastOrDefault();
        return Enumerate(enumerable, cancellationToken);
    }

    public static async Task ApplyDnsAsync(IServiceProvider services, string? template)
    {
        foreach (var entry in Entries())
        {
            var method = entry.GetMethod("ApplyDnsAsync");
            if (method is null)
            {
                continue;
            }

            await InvokePending(method.Invoke(null, [services, template]));
        }
    }

    public static async Task DisposeAsync(IServiceProvider services)
    {
        foreach (var entry in Entries())
        {
            var method = entry.GetMethod("DisposeAsync");
            if (method is null)
            {
                continue;
            }

            await InvokePending(method.Invoke(null, [services]));
        }
    }

    private static Type? Entry(string operation) =>
        Entries().FirstOrDefault(entry =>
            string.Equals(entry.GetField("Operation")?.GetValue(null) as string, operation, StringComparison.OrdinalIgnoreCase));

    private static MethodInfo EntryMethod(string operation, string methodName)
    {
        var entry = Entry(operation) ?? throw new InvalidOperationException($"{operation} plugin not registered");
        return entry.GetMethod(methodName) ?? throw new MissingMethodException(entry.FullName, methodName);
    }

    private static object?[] WithServices(IServiceProvider services, object?[] args)
    {
        var invoke = new object?[args.Length + 1];
        invoke[0] = services;
        Array.Copy(args, 0, invoke, 1, args.Length);
        return invoke;
    }

    private static async Task<object> InvokeReturnedTask(MethodInfo method, object?[] args)
    {
        var task = (Task)method.Invoke(null, args)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static async Task InvokePending(object? pending)
    {
        switch (pending)
        {
            case Task task:
                await task;
                break;
            case ValueTask valueTask:
                await valueTask;
                break;
        }
    }

    private static async IAsyncEnumerable<object> Enumerate(
        object enumerable,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = enumerable.GetType().GetMethod("GetAsyncEnumerator")!.Invoke(enumerable, [cancellationToken])!;
        try
        {
            var moveNext = enumerator.GetType().GetMethod("MoveNextAsync")!;
            var current = enumerator.GetType().GetProperty("Current")!;
            while (true)
            {
                var pending = moveNext.Invoke(enumerator, null)!;
                var more = pending switch
                {
                    ValueTask<bool> valueTask => await valueTask,
                    Task<bool> task => await task,
                    _ => throw new InvalidOperationException("StreamAsync enumerator did not return a boolean task.")
                };
                if (!more)
                {
                    yield break;
                }

                yield return current.GetValue(enumerator)!;
            }
        }
        finally
        {
            if (enumerator is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }

    public static int StatusCodeOf(object value) => (int)(Get(value, "StatusCode") ?? 500);

    public static string? Text(object value, string name) => Get(value, name) as string;

    public static long? TimingsTotal(object value)
    {
        var timings = Get(value, "Timings");
        return timings is null ? null : (long)Get(timings, "Total")!;
    }

    public static string EventType(object value) => (string)Get(value, "Type")!;

    public static int? EventStatus(object value) => Get(value, "StatusCode") as int?;

    private static Assembly? FindLoaded(string assemblyName) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == assemblyName);

    private static void Load(string pathOrName)
    {
        var path = Path.IsPathRooted(pathOrName)
            ? pathOrName
            : Path.Combine(AppContext.BaseDirectory, pathOrName);
        if (!File.Exists(path))
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (FindLoaded(name) is not null)
        {
            return;
        }

        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private static object? Get(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);

}

internal sealed class HostV2Request
{
    public string? PageUrl { get; set; }
    public string? Stream { get; set; }
    public bool ListOnly { get; set; }
    public bool StreamResults { get; set; }
}
