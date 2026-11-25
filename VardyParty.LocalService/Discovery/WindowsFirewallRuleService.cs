using System.Diagnostics;

namespace VardyParty.LocalService.Discovery;

public sealed class FirewallRuleService(
    IConfiguration config,
    DiscoveryRuntimeState runtimeState,
    ILogger<FirewallRuleService> logger) : IHostedService, IFirewallRuleService
{
    private const string MacOsAnchorName = "com.vardyparty.localservice";

    private const string UdpDiscoveryRange = "40129-40189";
    private const string HttpPortRange = "5019-5573";

    private string? _httpRuleName;
    private string? _httpRangeRuleName;
    private string? _udpRuleName;
    private string? _udpRangeRuleName;
    private bool _enabled;
    private readonly HashSet<(string Protocol, int Port)> _macOsRules = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _enabled = config.GetValue<bool?>("Discovery:ManageFirewallRules")
            ?? config.GetValue("Discovery:ManageWindowsFirewallRules", true);

        if (!_enabled)
        {
            logger.LogInformation("Firewall rule management is disabled by configuration.");
            return;
        }

        // Remove any existing app-scoped or preexisting VardyParty firewall rules so we can create explicit port-based rules
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var removeCmd = "Get-NetFirewallRule -DisplayName 'VardyParty*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue";
                var removeResult = await RunProcessAsync("powershell", $"-NoProfile -Command \"{removeCmd.Replace("\"","\\\"")}\"", cancellationToken);
                logger.LogInformation("Removed preexisting VardyParty firewall rules (ExitCode: {ExitCode}) Output: {Output}", removeResult.ExitCode, removeResult.Output);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove preexisting VardyParty firewall rules at startup.");
            }
        }

        // Create explicit HTTP rule for the actual runtime port
        _httpRuleName = BuildHttpRuleName(runtimeState.HttpPort);
        await EnsureRuleAsync(
            _httpRuleName,
            "TCP",
            runtimeState.HttpPort,
            "Allow inbound HTTP traffic for VardyParty.LocalService",
            cancellationToken);

        // Create a conservative HTTP port-range rule to cover clients expecting the wider range
        _httpRangeRuleName = BuildHttpRangeRuleName();
        await EnsureRangeRuleAsync(
            _httpRangeRuleName,
            "TCP",
            HttpPortRange,
            "Allow inbound HTTP traffic for VardyParty.LocalService (port range)",
            cancellationToken);

        // Create a conservative UDP discovery port-range rule so discovery works before UDP socket binds
        _udpRangeRuleName = BuildUdpRangeRuleName();
        await EnsureRangeRuleAsync(
            _udpRangeRuleName,
            "UDP",
            UdpDiscoveryRange,
            "Allow inbound UDP discovery traffic for VardyParty.LocalService (port range)",
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
            return;

        // Remove any specific UDP rule created during runtime
        if (!string.IsNullOrWhiteSpace(_udpRuleName))
        {
            await RemoveRuleAsync(_udpRuleName, "UDP", TryGetPortFromRuleName(_udpRuleName), cancellationToken);
            _udpRuleName = null;
        }

        // Remove HTTP specific rule
        if (!string.IsNullOrWhiteSpace(_httpRuleName))
        {
            await RemoveRuleAsync(_httpRuleName, "TCP", runtimeState.HttpPort, cancellationToken);
            _httpRuleName = null;
        }

        // Remove range-based rules created at startup
        if (!string.IsNullOrWhiteSpace(_udpRangeRuleName))
        {
            await RemoveRangeRuleAsync(_udpRangeRuleName, "UDP", UdpDiscoveryRange, cancellationToken);
            _udpRangeRuleName = null;
        }

        if (!string.IsNullOrWhiteSpace(_httpRangeRuleName))
        {
            await RemoveRangeRuleAsync(_httpRangeRuleName, "TCP", HttpPortRange, cancellationToken);
            _httpRangeRuleName = null;
        }

        if (OperatingSystem.IsMacOS() && _macOsRules.Count > 0)
        {
            _macOsRules.Clear();
            await ApplyMacOsRulesAsync(cancellationToken);
        }
    }

    public async Task EnsureUdpDiscoveryRuleAsync(int udpPort, CancellationToken cancellationToken)
    {
        if (!_enabled || udpPort <= 0)
            return;

        var newRuleName = BuildUdpRuleName(udpPort);
        if (string.Equals(_udpRuleName, newRuleName, StringComparison.Ordinal))
            return;

        if (!string.IsNullOrWhiteSpace(_udpRuleName))
        {
            await RemoveRuleAsync(_udpRuleName, "UDP", TryGetPortFromRuleName(_udpRuleName), cancellationToken);
        }

        _udpRuleName = newRuleName;
        await EnsureRuleAsync(
            _udpRuleName,
            "UDP",
            udpPort,
            "Allow inbound UDP discovery traffic for VardyParty.LocalService",
            cancellationToken);
    }

    public async Task RemoveUdpDiscoveryRuleAsync(int udpPort, CancellationToken cancellationToken)
    {
        if (!_enabled || udpPort <= 0)
            return;

        var ruleName = BuildUdpRuleName(udpPort);
        await RemoveRuleAsync(ruleName, "UDP", udpPort, cancellationToken);

        if (string.Equals(_udpRuleName, ruleName, StringComparison.Ordinal))
            _udpRuleName = null;
    }

    private async Task EnsureRuleAsync(string ruleName, string protocol, int port, string description, CancellationToken cancellationToken)
    {
        var platformResult = await ApplyPlatformRuleAsync(ruleName, protocol, port, description, isAdd: true, cancellationToken);

        if (platformResult.Success)
        {
            logger.LogInformation("✓ Firewall rule created: '{RuleName}' | {Protocol}/{Port} | {Mechanism} | {Output}",
                ruleName,
                protocol,
                port,
                platformResult.Mechanism,
                platformResult.Output);
            return;
        }

        logger.LogWarning("✗ Failed to create firewall rule: '{RuleName}' | {Protocol}/{Port} | {Mechanism} | {Output}",
            ruleName,
            protocol,
            port,
            platformResult.Mechanism,
            platformResult.Output);
    }

    private async Task RemoveRuleAsync(string ruleName, string protocol, int? port, CancellationToken cancellationToken)
    {
        if (port is null or <= 0)
            return;

        var platformResult = await ApplyPlatformRuleAsync(ruleName, protocol, port.Value, description: string.Empty, isAdd: false, cancellationToken);

        if (platformResult.Success)
        {
            logger.LogInformation("✓ Firewall rule removed: '{RuleName}' | {Protocol}/{Port} | {Mechanism}",
                ruleName,
                protocol,
                port,
                platformResult.Mechanism);
            return;
        }

        logger.LogWarning("✗ Failed to remove firewall rule: '{RuleName}' | {Protocol}/{Port} | {Mechanism} | {Output}",
            ruleName,
            protocol,
            port,
            platformResult.Mechanism,
            platformResult.Output);
    }

    private async Task EnsureRangeRuleAsync(string ruleName, string protocol, string portRange, string description, CancellationToken cancellationToken)
    {
        var platformResult = await ApplyPlatformRangeRuleAsync(ruleName, protocol, portRange, description, isAdd: true, cancellationToken);

        if (platformResult.Success)
        {
            logger.LogInformation("✓ Firewall range rule created: '{RuleName}' | {Protocol}/{PortRange} | {Mechanism} | {Output}",
                ruleName,
                protocol,
                portRange,
                platformResult.Mechanism,
                platformResult.Output);
            return;
        }

        logger.LogWarning("✗ Failed to create firewall range rule: '{RuleName}' | {Protocol}/{PortRange} | {Mechanism} | {Output}",
            ruleName,
            protocol,
            portRange,
            platformResult.Mechanism,
            platformResult.Output);
    }

    private async Task RemoveRangeRuleAsync(string ruleName, string protocol, string portRange, CancellationToken cancellationToken)
    {
        var platformResult = await ApplyPlatformRangeRuleAsync(ruleName, protocol, portRange, description: string.Empty, isAdd: false, cancellationToken);

        if (platformResult.Success)
        {
            logger.LogInformation("✓ Firewall range rule removed: '{RuleName}' | {Protocol}/{PortRange} | {Mechanism}",
                ruleName,
                protocol,
                portRange,
                platformResult.Mechanism);
            return;
        }

        logger.LogWarning("✗ Failed to remove firewall range rule: '{RuleName}' | {Protocol}/{PortRange} | {Mechanism} | {Output}",
            ruleName,
            protocol,
            portRange,
            platformResult.Mechanism,
            platformResult.Output);
    }

    private async Task<(bool Success, string Mechanism, string Output)> ApplyPlatformRuleAsync(
        string ruleName,
        string protocol,
        int port,
        string description,
        bool isAdd,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (isAdd)
            {
                // Create a single rule covering all profiles (Domain, Private, Public) so domain-joined machines are also covered
                var addArgs = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow profile=any protocol={protocol} localport={port} description=\"{description}\"";

                var addResult = await RunProcessAsync("netsh", addArgs, cancellationToken);

                logger.LogDebug("netsh add rule result - ExitCode: {ExitCode}, Output: {Output}", addResult.ExitCode, addResult.Output);

                var success = addResult.ExitCode == 0;
                var output = addResult.Output;
                return (success, "netsh", output);
            }
            else
            {
                // Delete rule across profiles by name
                var deleteArgs = $"advfirewall firewall delete rule name=\"{ruleName}\"";

                var deleteResult = await RunProcessAsync("netsh", deleteArgs, cancellationToken);

                logger.LogDebug("netsh delete rule result - ExitCode: {ExitCode}, Output: {Output}", deleteResult.ExitCode, deleteResult.Output);

                var success = deleteResult.ExitCode == 0;
                var output = deleteResult.Output;
                return (success, "netsh", output);
            }
        }

        if (OperatingSystem.IsLinux())
        {
            var protocolLower = protocol.ToLowerInvariant();

            var ufw = await RunShellAsync("command -v ufw >/dev/null 2>&1", cancellationToken);
            if (ufw.ExitCode == 0)
            {
                var command = isAdd
                    ? $"ufw allow in proto {protocolLower} to any port {port} comment '{ruleName.Replace("'", string.Empty)}'"
                    : $"ufw delete allow in proto {protocolLower} to any port {port}";
                var result = await RunShellAsync(command, cancellationToken);
                return (result.ExitCode == 0, "ufw", result.Output);
            }

            var firewallCmd = await RunShellAsync("command -v firewall-cmd >/dev/null 2>&1", cancellationToken);
            if (firewallCmd.ExitCode == 0)
            {
                var command = isAdd
                    ? $"firewall-cmd --quiet --add-port={port}/{protocolLower}"
                    : $"firewall-cmd --quiet --remove-port={port}/{protocolLower}";
                var result = await RunShellAsync(command, cancellationToken);
                return (result.ExitCode == 0, "firewall-cmd", result.Output);
            }

            var iptables = await RunShellAsync("command -v iptables >/dev/null 2>&1", cancellationToken);
            if (iptables.ExitCode == 0)
            {
                var command = isAdd
                    ? $"iptables -C INPUT -p {protocolLower} --dport {port} -j ACCEPT || iptables -I INPUT -p {protocolLower} --dport {port} -j ACCEPT"
                    : $"iptables -D INPUT -p {protocolLower} --dport {port} -j ACCEPT";
                var result = await RunShellAsync(command, cancellationToken);
                return (result.ExitCode == 0, "iptables", result.Output);
            }

            return (false, "linux", "No supported firewall tool found (ufw, firewall-cmd, iptables).");
        }

        if (OperatingSystem.IsMacOS())
        {
            if (isAdd)
                _macOsRules.Add((protocol.ToLowerInvariant(), port));
            else
                _macOsRules.Remove((protocol.ToLowerInvariant(), port));

            var result = await ApplyMacOsRulesAsync(cancellationToken);
            return (result.ExitCode == 0, "pfctl", result.Output);
        }

        return (false, "unsupported", "Unsupported operating system for automatic firewall rule management.");
    }

    private async Task<(bool Success, string Mechanism, string Output)> ApplyPlatformRangeRuleAsync(
        string ruleName,
        string protocol,
        string portRange,
        string description,
        bool isAdd,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (isAdd)
            {
                var addArgs = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow profile=any protocol={protocol} localport={portRange} description=\"{description}\"";
                var addResult = await RunProcessAsync("netsh", addArgs, cancellationToken);
                logger.LogDebug("netsh add range rule result - ExitCode: {ExitCode}, Output: {Output}", addResult.ExitCode, addResult.Output);
                return (addResult.ExitCode == 0, "netsh", addResult.Output);
            }
            else
            {
                var deleteArgs = $"advfirewall firewall delete rule name=\"{ruleName}\"";
                var deleteResult = await RunProcessAsync("netsh", deleteArgs, cancellationToken);
                logger.LogDebug("netsh delete range rule result - ExitCode: {ExitCode}, Output: {Output}", deleteResult.ExitCode, deleteResult.Output);
                return (deleteResult.ExitCode == 0, "netsh", deleteResult.Output);
            }
        }

        // Fallback to existing single-port behaviors for non-Windows platforms
        if (OperatingSystem.IsLinux())
        {
            var protocolLower = protocol.ToLowerInvariant();
            var ports = portRange.Split('-');
            if (ports.Length == 2 && int.TryParse(ports[0], out var start) && int.TryParse(ports[1], out var end))
            {
                var results = new List<string>();
                for (int p = start; p <= end; p++)
                {
                    var command = isAdd
                        ? $"ufw allow in proto {protocolLower} to any port {p} comment '{ruleName.Replace("'", string.Empty)}'"
                        : $"ufw delete allow in proto {protocolLower} to any port {p}";
                    var result = await RunShellAsync(command, cancellationToken);
                    results.Add(result.Output);
                }
                return (true, "ufw", string.Join(Environment.NewLine, results));
            }

            return (false, "linux", "Unsupported port range format for range rule on Linux fallback.");
        }

        if (OperatingSystem.IsMacOS())
        {
            // pfctl approach: expand range into multiple pass rules
            var ports = portRange.Split('-');
            if (ports.Length == 2 && int.TryParse(ports[0], out var start) && int.TryParse(ports[1], out var end))
            {
                _macOsRules.Clear();
                for (int p = start; p <= end; p++) _macOsRules.Add((protocol.ToLowerInvariant(), p));
                var result = await ApplyMacOsRulesAsync(cancellationToken);
                return (result.ExitCode == 0, "pfctl", result.Output);
            }

            return (false, "pfctl", "Unsupported port range format for MacOS fallback.");
        }

        return (false, "unsupported", "Unsupported platform for range firewall rules.");
    }

    private async Task<(int ExitCode, string Output)> ApplyMacOsRulesAsync(CancellationToken cancellationToken)
    {
        var rules = _macOsRules
            .OrderBy(r => r.Protocol)
            .ThenBy(r => r.Port)
            .Select(r => $"pass in inet proto {r.Protocol} from any to any port {r.Port}")
            .ToArray();

        var tempFile = Path.Combine(Path.GetTempPath(), $"vardyparty-localservice-pf-{Guid.NewGuid():N}.conf");

        try
        {
            await File.WriteAllLinesAsync(tempFile, rules, cancellationToken);

            var loadResult = await RunProcessAsync("pfctl", $"-a {MacOsAnchorName} -f \"{tempFile}\"", cancellationToken);
            if (loadResult.ExitCode != 0)
                return loadResult;

            var enableResult = await RunProcessAsync("pfctl", "-E", cancellationToken);
            if (enableResult.ExitCode != 0)
                return enableResult;

            return (0, string.IsNullOrWhiteSpace(enableResult.Output) ? loadResult.Output : enableResult.Output);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static int? TryGetPortFromRuleName(string ruleName)
    {
        var segments = ruleName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = segments.LastOrDefault(s => int.TryParse(s, out _));
        return int.TryParse(candidate, out var value) ? value : null;
    }

    private static string BuildHttpRuleName(int httpPort) => $"VardyParty.LocalService HTTP {httpPort} (Inbound)";

    private static string BuildUdpRuleName(int udpPort) => $"VardyParty.LocalService UDP Discovery {udpPort} (Inbound)";

    private static string BuildHttpRangeRuleName() => $"VardyParty.LocalService HTTP {HttpPortRange} (Inbound)";

    private static string BuildUdpRangeRuleName() => $"VardyParty.LocalService UDP Discovery {UdpDiscoveryRange} (Inbound)";

    private static Task<(int ExitCode, string Output)> RunShellAsync(string command, CancellationToken cancellationToken)
        => RunProcessAsync("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"", cancellationToken);

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        var output = string.IsNullOrWhiteSpace(stdErr) ? stdOut : $"{stdOut}{Environment.NewLine}{stdErr}";

        return (process.ExitCode, output.Trim());
    }
}
