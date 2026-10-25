using System.Diagnostics;
using System.Runtime.Versioning;

namespace FileTimeTracker.Agent.Deployment;

/// <summary>
/// Adds or removes the Microsoft Defender folder exclusion described in design.md §6.
/// </summary>
/// <remarks>
/// <para><b>This is a tradeoff, not a free win.</b> An excluded folder is not scanned, so anything
/// written there is unscanned too. That is why the installer ACLs the folder to
/// administrators-write before adding the exclusion, and why the exclusion covers the install
/// directory only — never ProgramData, which the employee can write to.</para>
/// <para>§6's stated upgrade path is the real fix: an Authenticode-signed agent needs no exclusion
/// and raises no SmartScreen prompt. Treat this as scaffolding for the pilot.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DefenderExclusion
{
    public static int Add(string path, TextWriter output) =>
        Run($"Add-MpPreference -ExclusionPath '{Escape(path)}'", $"Added Defender exclusion for '{path}'.", output);

    public static int Remove(string path, TextWriter output) =>
        Run($"Remove-MpPreference -ExclusionPath '{Escape(path)}'", $"Removed Defender exclusion for '{path}'.", output);

    private static string Escape(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static int Run(string command, string successMessage, TextWriter output)
    {
        var powershell = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        if (!File.Exists(powershell))
        {
            output.WriteLine("Defender exclusion skipped: powershell.exe was not found.");
            return 0;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"try {{ {command} -ErrorAction Stop }} catch {{ Write-Error $_; exit 1 }}");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                output.WriteLine("Defender exclusion skipped: could not start powershell.exe.");
                return 0;
            }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);

            if (process.ExitCode == 0)
            {
                output.WriteLine(successMessage);
                return 0;
            }

            // Defender may be replaced by another AV product or managed by policy. That is a
            // normal fleet condition, so it must not fail the whole install.
            output.WriteLine($"Defender exclusion could not be applied (this is not fatal): {stderr.Trim()}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Defender exclusion could not be applied (this is not fatal): {ex.Message}");
            return 0;
        }
    }
}
