using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: InstallerBootstrapper <path-to-msix>");
            return 2;
        }

        var msixPath = args[0];
        if (!File.Exists(msixPath))
        {
            Console.Error.WriteLine($"MSIX not found: {msixPath}");
            return 3;
        }

        Console.WriteLine($"Installing MSIX: {msixPath}");

        var addAppx = RunProcess("powershell", $"-NoProfile -Command Add-AppxPackage -Path \"{msixPath}\" -ForceApplicationShutdown -ForceUpdateFromAnyVersion", out var rc1);
        if (rc1 != 0)
        {
            Console.Error.WriteLine("Add-AppxPackage failed");
            return 4;
        }

        Console.WriteLine("Updating firewall rules to specific ports and protocols...");

        // Build a PowerShell script to remove existing VardyParty rules and add explicit, narrow rules with EdgeTraversal allowed
        var psScript = @"
try {
    Get-NetFirewallRule -DisplayName 'VardyParty.LocalService*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName 'VardyParty.LocalService UDP Discovery' -Direction Inbound -Action Allow -Protocol UDP -LocalPort '40129-40189' -Profile Any -EdgeTraversalPolicy Allow -Description 'VardyParty.LocalService UDP Discovery'
    New-NetFirewallRule -DisplayName 'VardyParty.LocalService HTTP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort '5019-5573' -Profile Any -EdgeTraversalPolicy Allow -Description 'VardyParty.LocalService HTTP'
    Write-Output 'Firewall rules updated'
    exit 0
} catch {
    Write-Error $_
    exit 1
}
";

        var tempPath = Path.Combine(Path.GetTempPath(), $"vardyparty_fw_{Guid.NewGuid()}.ps1");
        File.WriteAllText(tempPath, psScript);
        var res = RunProcess("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPath}\"", out var rcFw);
        try { File.Delete(tempPath); } catch {}
        if (rcFw != 0)
        {
            Console.Error.WriteLine("Failed to update firewall rules via PowerShell.");
            return 5;
        }
        Console.WriteLine("Firewall rules updated.");

        Console.WriteLine("Install and firewall setup complete.");
        return 0;
    }

    static bool RunProcess(string file, string args, out int exitCode)
    {
        using var p = new Process();
        p.StartInfo.FileName = file;
        p.StartInfo.Arguments = args;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;
        p.Start();
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        exitCode = p.ExitCode;
        if (!string.IsNullOrEmpty(outp)) Console.WriteLine(outp.Trim());
        if (!string.IsNullOrEmpty(err)) Console.Error.WriteLine(err.Trim());
        return exitCode == 0;
    }
}
