using System.Diagnostics;

namespace OneByOneSlnBuilder.Core;

public static class MsBuildLocator
{
    public static async Task<string?> FindAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            // Prefer VS2022; fall back to a later installed full MSBuild.
            foreach (var version in new[] { "[17.0,18.0)", "[17.0,)" })
            {
                var start = new ProcessStartInfo(vswhere)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-latest", "-products", "*", "-version", version, "-requires",
                    "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe", "-utf8" })
                    start.ArgumentList.Add(arg);
                start.StandardOutputEncoding = System.Text.Encoding.UTF8;
                using var process = new Process { StartInfo = start };
                try
                {
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                    using var registration = cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
                    await process.WaitForExitAsync(cancellationToken);
                    var output = await stdout;
                    await stderr;
                    if (process.ExitCode == 0)
                    {
                        var candidates = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim()).Where(File.Exists).ToArray();
                        // The regular executable also supports x86-only legacy build tasks.
                        var match = candidates.FirstOrDefault(p => !p.Contains(@"\amd64\", StringComparison.OrdinalIgnoreCase))
                            ?? candidates.FirstOrDefault();
                        if (match != null) return match;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { }
            }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "MSBuild.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
