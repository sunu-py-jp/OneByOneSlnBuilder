using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OneByOneSlnBuilder.Core;

public sealed class BuildRunner
{
    private const long MaxJsonlBytes = 32L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task<BuildReport> RunAsync(BuildOptions options, IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.RootDirectory);
        var output = Path.GetFullPath(options.OutputDirectory);
        var msbuild = Path.GetFullPath(options.MsBuildPath);
        var logger = Path.GetFullPath(options.LoggerPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("対象フォルダが見つかりません。");
        if (!File.Exists(msbuild)) throw new FileNotFoundException("MSBuild.exeが見つかりません。", msbuild);
        if (!File.Exists(logger)) throw new FileNotFoundException("診断ロガーが見つかりません。アプリ一式を展開し直してください。", logger);
        if (logger.IndexOfAny([';', ',', '"']) >= 0)
            throw new ArgumentException("アプリはセミコロン・カンマ・引用符を含まないフォルダに配置してください。");
        if (options.Platform?.IndexOfAny([';', ',', '\r', '\n', '"']) >= 0)
            throw new ArgumentException("プラットフォーム名にセミコロン・カンマ・改行・引用符は使用できません。");

        progress?.Report(new(0, 0, "ソリューションを検索しています…"));
        var solutions = await Task.Run(() => DiscoverSolutions(root, cancellationToken), cancellationToken);
        if (solutions.Count == 0) throw new InvalidOperationException("指定フォルダ以下に.slnファイルがありません。");
        var run = Path.Combine(output, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        var logs = Path.Combine(run, "logs");
        Directory.CreateDirectory(logs);
        var results = new List<SolutionResult>();
        var diagnostics = new HashSet<DiagnosticRecord>();
        var unmappedCount = 0;
        var cancelled = false;
        await File.WriteAllTextAsync(Path.Combine(run, "run.json"), JsonSerializer.Serialize(new
        {
            rootDirectory = root, startedAt = DateTimeOffset.Now, configuration = "Release", target = "Rebuild",
            platform = options.Platform, options.IncludeWarnings, options.Restore, msBuildPath = msbuild,
            solutions = solutions.Select(s => Path.GetRelativePath(root, s).Replace('\\', '/'))
        }, JsonFormat.Options), Utf8);

        for (var index = 0; index < solutions.Count; index++)
        {
            var solution = solutions[index];
            var relative = Path.GetRelativePath(root, solution).Replace('\\', '/');
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new(relative, "notRun", null, 0, 0, "", "中止のため未実行"));
                continue;
            }
            var stem = (index + 1).ToString("D4");
            var logPath = Path.Combine(logs, stem + ".log");
            var rawPath = Path.Combine(logs, stem + ".events.jsonl");
            var unmappedPath = Path.Combine(logs, stem + ".unmapped.jsonl");
            progress?.Report(new(index, solutions.Count, $"[{index + 1}/{solutions.Count}] {relative} をReleaseビルド中…"));
            var startInfo = CreateStartInfo(options, msbuild, logger, solution, rawPath);
            int? exitCode = null;
            string? detail = null;
            string status;
            try
            {
                var outcome = await ExecuteAsync(startInfo, logPath, cancellationToken);
                exitCode = outcome.ExitCode;
                cancelled |= outcome.Cancelled;
                status = outcome.Cancelled ? "cancelled" : exitCode == 0 ? "succeeded" : "failed";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                status = "startFailed";
                detail = ex.Message;
                await File.AppendAllTextAsync(logPath, "\n実行エラー: " + ex + "\n", Utf8);
            }
            var count = 0;
            var unmapped = 0;
            var completed = false;
            bool? succeeded = null;
            try
            {
                if (File.Exists(rawPath))
                {
                    using var reader = new StreamReader(rawPath, Utf8, true);
                    using var rejected = new StreamWriter(unmappedPath, false, Utf8);
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var item = JsonSerializer.Deserialize<RawDiagnostic>(line, JsonFormat.Options)
                            ?? throw new JsonException("空の診断レコード");
                        if (item.Kind == "buildFinished") { completed = true; succeeded = item.Succeeded; continue; }
                        if (item.Kind != "error" && item.Kind != "warning") throw new JsonException("未知の診断レコード: " + item.Kind);
                        if (item.Kind == "warning" && !options.IncludeWarnings) continue;
                        if (DiagnosticMapper.TryMap(item, root, solution, out var mapped, out var reason))
                        {
                            diagnostics.Add(mapped!);
                            count++;
                        }
                        else
                        {
                            unmapped++;
                            await rejected.WriteLineAsync(JsonSerializer.Serialize(new { reason, diagnostic = item }, JsonFormat.Options));
                        }
                    }
                }
                if (status != "cancelled" && status != "startFailed" && (!completed || succeeded == null))
                {
                    status = "incomplete";
                    detail = "診断ロガーの完了を確認できません。ビルドログを確認してください。";
                }
                else if (succeeded == false && status == "succeeded")
                {
                    status = "failed";
                    detail = "MSBuildからビルド失敗が報告されました。";
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                if (status != "cancelled") status = "incomplete";
                detail = "診断ログを最後まで読めません: " + ex.Message;
            }
            unmappedCount += unmapped;
            results.Add(new(relative, status, exitCode, count, unmapped, Path.GetRelativePath(run, logPath).Replace('\\', '/'), detail));
            progress?.Report(new(index + 1, solutions.Count,
                $"{relative}: {StatusText(status)} / 診断 {count}件、取り込み対象外 {unmapped}件"));
        }
        // A cancelled run never produces a file named diagnostics.jsonl.
        cancelled |= cancellationToken.IsCancellationRequested;
        string? diagnosticsFile = null;
        string? exportError = null;
        if (diagnostics.Count > 0)
        {
            var destination = Path.Combine(run, cancelled ? "diagnostics.partial.jsonl" : "diagnostics.jsonl");
            var temporary = destination + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    foreach (var diagnostic in diagnostics.OrderBy(d => d.File, StringComparer.Ordinal)
                        .ThenBy(d => d.Line).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal))
                    {
                        var bytes = Utf8.GetBytes(JsonSerializer.Serialize(diagnostic, JsonFormat.Options) + "\n");
                        if (stream.Position + bytes.Length > MaxJsonlBytes)
                            throw new InvalidOperationException("診断JSONLがOneByOneの上限32 MiBを超えます。対象フォルダを分けるか、警告を除外して再実行してください。");
                        await stream.WriteAsync(bytes);
                    }
                }
                File.Move(temporary, destination);
                if (!cancelled) diagnosticsFile = destination;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                exportError = ex.Message;
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        var report = new BuildReport(root, run, diagnosticsFile, results, diagnostics.Count, unmappedCount, cancelled);
        await File.WriteAllTextAsync(Path.Combine(run, "summary.json"), JsonSerializer.Serialize(new
        {
            report.RootDirectory, report.RunDirectory, report.DiagnosticsFile, report.Solutions,
            report.DiagnosticCount, report.UnmappedCount, report.Cancelled, exportError, finishedAt = DateTimeOffset.Now
        }, new JsonSerializerOptions(JsonFormat.Options) { WriteIndented = true }), Utf8);
        if (exportError != null) throw new InvalidOperationException(exportError + "\nログ保存先: " + run);
        progress?.Report(new(results.Count(r => r.Status != "notRun"), solutions.Count, cancelled ? "中止しました。" : "処理が完了しました。"));
        return report;
    }

    private static List<string> DiscoverSolutions(string root, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            // Explicitly fail on unreadable directories instead of silently missing solutions.
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                        ? new DirectoryInfo(entry) : new FileInfo(entry);
                    // OneDrive placeholders also have ReparsePoint; only skip actual links.
                    if (info.LinkTarget != null) continue;
                }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (string.Equals(Path.GetExtension(entry), ".sln", StringComparison.OrdinalIgnoreCase)) result.Add(entry);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static ProcessStartInfo CreateStartInfo(BuildOptions options, string msbuild, string logger, string solution, string rawPath)
    {
        var start = new ProcessStartInfo(msbuild)
        {
            WorkingDirectory = Path.GetDirectoryName(solution)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { solution, "/nologo", "/t:Rebuild", "/p:Configuration=Release", "/m:1", "/nr:false", "/v:minimal" })
            start.ArgumentList.Add(arg);
        if (options.Restore) { start.ArgumentList.Add("/restore"); start.ArgumentList.Add("/p:RestorePackagesConfig=true"); }
        if (!string.IsNullOrWhiteSpace(options.Platform)) start.ArgumentList.Add("/p:Platform=" + options.Platform.Trim());
        start.ArgumentList.Add("/logger:OneByOneSlnBuilder.Logger.JsonLogger," + logger + ";" + Convert.ToBase64String(Utf8.GetBytes(rawPath)));
        // Override SDK paths inherited when the app is launched from dotnet/VS tooling.
        start.Environment.Remove("MSBUILD_EXE_PATH");
        start.Environment.Remove("MSBuildSDKsPath");
        return start;
    }

    private static async Task<(int ExitCode, bool Cancelled)> ExecuteAsync(ProcessStartInfo start, string logPath, CancellationToken token)
    {
        using var log = new StreamWriter(logPath, false, Utf8) { AutoFlush = true };
        log.WriteLine("Executable: " + start.FileName);
        log.WriteLine("Arguments: " + string.Join(" ", start.ArgumentList.Select(a => "\"" + a + "\"")));
        log.WriteLine("Working directory: " + start.WorkingDirectory);
        using var process = new Process { StartInfo = start };
        process.Start();
        var sync = new object();
        async Task PumpAsync(StreamReader reader, string prefix)
        {
            while (await reader.ReadLineAsync() is { } line)
                lock (sync) log.WriteLine(prefix + line);
        }
        var stdout = PumpAsync(process.StandardOutput, "");
        var stderr = PumpAsync(process.StandardError, "[stderr] ");
        var cancelled = false;
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
        }
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, cancelled);
    }
    private static string StatusText(string value) => value switch
    {
        "succeeded" => "成功", "failed" => "失敗", "incomplete" => "診断の取得が不完全",
        "startFailed" => "実行エラー", "cancelled" => "中止", _ => value
    };
}
