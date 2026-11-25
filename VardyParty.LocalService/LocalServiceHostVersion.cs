using System.Reflection;

namespace VardyParty.LocalService;

/// <summary>
/// Package version advertised to clients (matches csproj <c>Version</c> / GitHub release tag).
/// </summary>
public static class LocalServiceHostVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(LocalServiceHostVersion).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational.Trim();
        }

        var version = asm.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
