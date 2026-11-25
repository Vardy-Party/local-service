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
    private static readonly string[] PluginFileNames =
    [
        "VardyParty.LocalService.Abstractions.dll",
        "VardyParty.LocalService.V1.dll",
        "VardyParty.LocalService.V2.dll",
        "VardyParty.LocalService.V1.Scrape.dll",
        "VardyParty.LocalService.V2.Scrape.dll"
    ];

    internal static object? ChromeOptions { get; private set; }

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        foreach (var fileName in PluginFileNames)
        {
            Load(fileName);
        }

        var v1 = FindLoaded("VardyParty.LocalService.V1.Scrape");
        if (v1 is not null)
        {
            var options = Create(v1, "VardyParty.LocalService.V1.Scrape.PlaywrightOptions");
            Set(options, "Headless", configuration.GetValue("Playwright:Headless", true));
            Set(options, "IncludeScreenshots", configuration.GetValue("Playwright:IncludeScreenshots", false));
            InvokeStatic(v1, "VardyParty.LocalService.V1.Scrape.ScrapePluginServiceCollectionExtensions", "AddV1ScrapePlugin", services, options);
        }

        var v2 = FindLoaded("VardyParty.LocalService.V2.Scrape");
        if (v2 is not null)
        {
            var options = Create(v2, "VardyParty.LocalService.V2.Scrape.SystemChromeOptions");
            Set(options, "Headless", configuration.GetValue("Chrome:Headless", true));
            Set(options, "ExecutablePath", configuration["Chrome:ExecutablePath"]);
            Set(options, "SegmentWait", TimeSpan.FromSeconds(configuration.GetValue("Chrome:SegmentWaitSeconds", 12)));
            Set(options, "MinRewrittenSamples", configuration.GetValue("Chrome:MinRewrittenSamples", 3));
            Set(options, "DumpScriptBodiesDir", configuration["Chrome:DumpScriptBodiesDir"]);
            ChromeOptions = options;
            InvokeStatic(v2, "VardyParty.LocalService.V2.Scrape.ScrapePluginServiceCollectionExtensions", "AddV2ScrapePlugin", services, options);
        }
    }

    public static object? Find(IServiceProvider services, string assemblyName, string typeName)
    {
        var type = FindLoaded(assemblyName)?.GetType(typeName);
        if (type is null)
        {
            return null;
        }

        var contract = FindLoaded("VardyParty.LocalService.Abstractions")
            ?.GetType("VardyParty.LocalService.Abstractions.IStreamScrapePlugin");
        if (contract is null)
        {
            return services.GetService(type);
        }

        var getServices = typeof(ServiceProviderServiceExtensions)
            .GetMethod(nameof(ServiceProviderServiceExtensions.GetServices))!
            .MakeGenericMethod(contract);
        foreach (var item in (IEnumerable)getServices.Invoke(null, [services])!)
        {
            if (item is not null && type.IsInstanceOfType(item))
            {
                return item;
            }
        }

        return null;
    }

    public static async Task<object> ResolveV1Async(
        object plugin,
        string url,
        string? streamName,
        string? dnsOverHttpsTemplate,
        string? proxyServer,
        string? connectionId,
        CancellationToken cancellationToken)
    {
        object? options = null;
        if (streamName is not null || dnsOverHttpsTemplate is not null || proxyServer is not null || connectionId is not null)
        {
            options = Create(plugin.GetType().Assembly, "VardyParty.LocalService.V1.Scrape.Models.PlayRequestOptions");
            Set(options, "StreamName", streamName);
            Set(options, "DnsOverHttpsTemplate", dnsOverHttpsTemplate);
            Set(options, "ProxyServer", proxyServer);
            Set(options, "ConnectionId", connectionId);
        }

        return await InvokeTaskAsync(plugin, "ResolveAsync", url, options, cancellationToken);
    }

    public static object CreateMpRequest(string pageUrl, string? stream, bool listOnly, bool streamResults)
    {
        var request = Create(Required("VardyParty.LocalService.Abstractions"), "VardyParty.LocalService.Abstractions.Models.MpPlayRequest");
        Set(request, "PageUrl", pageUrl);
        Set(request, "Stream", stream);
        Set(request, "ListOnly", listOnly);
        Set(request, "StreamResults", streamResults);
        return request;
    }

    public static Task<object> ResolveV2Async(object plugin, object request, CancellationToken cancellationToken) =>
        InvokeTaskAsync(plugin, "ResolveAsync", request, cancellationToken);

    public static async IAsyncEnumerable<object> StreamV2Async(
        object plugin,
        object request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var method = plugin.GetType().GetMethod("StreamAsync")
            ?? throw new MissingMethodException(plugin.GetType().FullName, "StreamAsync");
        var enumerable = method.Invoke(plugin, [request, cancellationToken])!;
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

    public static object? GetStatic(Type type, string fieldName)
    {
        var local = type.GetField(fieldName)?.GetValue(null)
            ?? throw new MissingFieldException(type.FullName, fieldName);
        return local.GetType().GetProperty("Value")!.GetValue(local);
    }

    public static void SetStatic(Type type, string fieldName, object? value)
    {
        var local = type.GetField(fieldName)?.GetValue(null)
            ?? throw new MissingFieldException(type.FullName, fieldName);
        local.GetType().GetProperty("Value")!.SetValue(local, value);
    }

    public static object? PhoneChromeOptions(string? popProxy)
    {
        if (ChromeOptions is null || popProxy is null)
        {
            return null;
        }

        var defaults = new ChromeLaunchDefaults
        {
            Headless = (bool)Get(ChromeOptions, "Headless")!,
            ExecutablePath = Get(ChromeOptions, "ExecutablePath") as string,
            CdpReadyTimeout = (TimeSpan)Get(ChromeOptions, "CdpReadyTimeout")!,
            ChipWait = (TimeSpan)Get(ChromeOptions, "ChipWait")!,
            PlaylistWait = (TimeSpan)Get(ChromeOptions, "PlaylistWait")!,
            SegmentWait = (TimeSpan)Get(ChromeOptions, "SegmentWait")!,
            WarmPlaylistWait = (TimeSpan)Get(ChromeOptions, "WarmPlaylistWait")!,
            WarmSegmentWait = (TimeSpan)Get(ChromeOptions, "WarmSegmentWait")!,
            MinRewrittenSamples = (int)Get(ChromeOptions, "MinRewrittenSamples")!,
            DumpScriptBodiesDir = Get(ChromeOptions, "DumpScriptBodiesDir") as string,
            ProxyServer = Get(ChromeOptions, "ProxyServer") as string
        };
        var waits = MpChromeWaitGate.ForRequest(popProxy, defaults);
        if (waits is null)
        {
            return null;
        }

        var created = Activator.CreateInstance(ChromeOptions.GetType())!;
        foreach (var property in ChromeOptions.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanWrite)
            {
                property.SetValue(created, property.GetValue(ChromeOptions));
            }
        }

        Set(created, "ChipWait", waits.ChipWait);
        Set(created, "PlaylistWait", waits.PlaylistWait);
        Set(created, "SegmentWait", waits.SegmentWait);
        Set(created, "WarmPlaylistWait", waits.WarmPlaylistWait);
        Set(created, "WarmSegmentWait", waits.WarmSegmentWait);
        return created;
    }

    public static object? BrowserManager(IServiceProvider services)
    {
        var type = FindLoaded("VardyParty.LocalService.V1.Scrape")
            ?.GetType("VardyParty.LocalService.V1.Scrape.BrowserManager");
        return type is null ? null : services.GetService(type);
    }

    public static Task ApplyDnsAsync(object browser, string? template)
    {
        var task = browser.GetType().GetMethod("ApplyDnsPreferenceAsync")!.Invoke(browser, [template]);
        return (Task)task!;
    }

    public static async Task DisposeBrowserAsync(object browser)
    {
        var pending = browser.GetType().GetMethod("DisposeAsync")!.Invoke(browser, null);
        if (pending is ValueTask valueTask)
        {
            await valueTask;
        }
        else if (pending is Task task)
        {
            await task;
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

    private static Assembly Required(string assemblyName) =>
        FindLoaded(assemblyName) ?? throw new InvalidOperationException($"Plugin assembly {assemblyName} is not loaded.");

    private static Assembly? FindLoaded(string assemblyName) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == assemblyName);

    private static void Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (FindLoaded(name) is not null)
        {
            return;
        }

        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private static object Create(Assembly assembly, string typeName)
    {
        var type = assembly.GetType(typeName) ?? throw new TypeLoadException(typeName);
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Could not create {typeName}.");
    }

    private static void InvokeStatic(Assembly assembly, string typeName, string methodName, params object?[] args)
    {
        var type = assembly.GetType(typeName) ?? throw new TypeLoadException(typeName);
        var method = type.GetMethod(methodName) ?? throw new MissingMethodException(typeName, methodName);
        method.Invoke(null, args);
    }

    private static async Task<object> InvokeTaskAsync(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName) ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        var task = (Task)method.Invoke(target, args)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static object? Get(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);

    private static void Set(object target, string name, object? value) =>
        target.GetType().GetProperty(name)?.SetValue(target, value);
}

internal sealed class HostMpRequest
{
    public string? PageUrl { get; set; }
    public string? Stream { get; set; }
    public bool ListOnly { get; set; }
    public bool StreamResults { get; set; }
}
