using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OneByOneSlnBuilder.Core;

namespace OneByOneSlnBuilder.Tests;

internal static partial class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] AllowedFields = ["file", "message", "line", "code"];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--wait-child")
            return await RunFakeChildAsync(args[1], args[2]);
        if (args.Length > 0 && Path.GetExtension(args[0]).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            return await RunFakeMsBuildAsync(args);

        (string Name, Func<Task> Run)[] tests =
        [
            ("Mapper resolves project-relative Windows separators and Japanese paths", MapperResolvesProjectRelativePaths),
            ("Mapper rejects outside, missing, and invalid diagnostics", MapperRejectsInvalidDiagnostics),
            ("Mapper preserves actual filesystem path casing (Windows)", MapperNormalizesWindowsPathCasing),
            ("Actual MSBuild logger captures structured warning and error events", ActualLoggerCapturesEvents),
            ("Actual MSBuild logger preserves restore and build phase diagnostics", ActualLoggerCapturesRestoreEvents),
            ("Visual Studio MSBuild loads the logger in its desktop host (Windows)", DesktopLoggerCapturesEvents),
            ("Runner finds nested solutions, continues failures, deduplicates, and writes strict JSONL", RunnerCollectsAcrossSolutions),
            ("Runner can omit warnings and reports zero diagnostics without a stale file", RunnerOmitsWarnings),
            ("Runner marks missing logger completion as incomplete", RunnerDetectsMissingCompletion),
            ("Runner cancellation kills the process and preserves only partial diagnostics", RunnerCancellation),
            ("Parallel runner overlaps work within its limit and aggregates in discovery order", RunnerParallelismAndDeterministicOutput),
            ("Parallel cancellation kills every running process tree and leaves queued solutions unstarted", RunnerParallelCancellation),
            ("Runner rejects concurrency limits outside 1 through 16", RunnerRejectsInvalidParallelism),
            ("Runner rejects an empty solution directory", RunnerRejectsEmptyDirectory)
        ];
        var failed = 0;
        var skipped = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (TestSkippedException ex) { skipped++; Console.WriteLine("SKIP " + test.Name + ": " + ex.Message); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + ex); }
        }
        Console.WriteLine($"{tests.Length - failed - skipped} passed, {skipped} skipped, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task MapperResolvesProjectRelativePaths()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "日本語 空白");
        Directory.CreateDirectory(root);
        var source = temp.Write("日本語 空白/project/src/注文.vb", "Class Order\nEnd Class\n");
        var solution = temp.Write("日本語 空白/solutions/Example.sln");
        var project = temp.Write("日本語 空白/project/Example.vbproj");
        var diagnostic = new RawDiagnostic
        {
            Kind = "error", File = "src\\注文.vb", ProjectFile = project,
            Line = 12, Code = "BC30451", Message = "名前 '商品' は宣言されていません。\n次の行も保持"
        };
        AssertEx.True(DiagnosticMapper.TryMap(diagnostic, root, solution, out var mapped, out var reason), reason ?? "Mapping failed.");
        AssertEx.Equal("project/src/注文.vb", mapped!.File);
        AssertEx.Equal(12, mapped.Line);
        AssertEx.Equal(diagnostic.Message, mapped.Message);
        AssertEx.Equal("BC30451", mapped.Code);

        diagnostic.File = source;
        diagnostic.Line = 0;
        diagnostic.Code = "";
        AssertEx.True(DiagnosticMapper.TryMap(diagnostic, root, solution, out mapped, out reason), reason ?? "Absolute path mapping failed.");
        AssertEx.Equal<int?>(null, mapped!.Line);
        AssertEx.Equal<string?>(null, mapped.Code);

        diagnostic.File = "src/注文.vb";
        diagnostic.ProjectFile = "../project/Example.vbproj";
        AssertEx.True(DiagnosticMapper.TryMap(diagnostic, root, solution, out mapped, out reason), reason ?? "Relative project mapping failed.");
        AssertEx.Equal("project/src/注文.vb", mapped!.File);
        return Task.CompletedTask;
    }

    private static Task MapperRejectsInvalidDiagnostics()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var solution = temp.Write("root/App.sln");
        temp.Write("root/Code.cs");
        var outside = temp.Write("outside.cs");
        RawDiagnostic[] invalid =
        [
            new() { File = outside, Message = "outside root" },
            new() { File = "../outside.cs", Message = "parent escape" },
            new() { File = "", Message = "environment error" },
            new() { File = "VBC", Message = "tool instead of source" },
            new() { File = "missing.vb", Message = "missing source" },
            new() { File = "Code.cs", Message = " \t " },
            new() { File = "Code.cs", Message = "NUL\0message" },
            new() { File = "Code.cs", Message = "bad code", Code = "CS\0" },
            new() { File = "bad\0path", Message = "bad path" }
        ];
        foreach (var diagnostic in invalid)
        {
            AssertEx.True(!DiagnosticMapper.TryMap(diagnostic, root, solution, out var mapped, out var reason), "Invalid diagnostic was accepted: " + diagnostic.Message);
            AssertEx.Equal<DiagnosticRecord?>(null, mapped);
            AssertEx.True(!string.IsNullOrWhiteSpace(reason), "Rejection must have a reason.");
        }
        return Task.CompletedTask;
    }

    private static Task MapperNormalizesWindowsPathCasing()
    {
        if (!OperatingSystem.IsWindows())
            throw new TestSkippedException("Filesystem path casing normalization requires Windows.");
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        var solution = temp.Write("root/App.sln");
        temp.Write("root/Src/Order.vb");
        var diagnostic = new RawDiagnostic { Kind = "error", File = "src/ORDER.VB", Message = "case mismatch" };
        AssertEx.True(DiagnosticMapper.TryMap(diagnostic, root, solution, out var mapped, out var reason), reason ?? "Mapping failed.");
        AssertEx.Equal("Src/Order.vb", mapped!.File);
        return Task.CompletedTask;
    }

    private static Task ActualLoggerCapturesEvents() => VerifyActualLogger(restore: false, desktop: false);

    private static Task ActualLoggerCapturesRestoreEvents() => VerifyActualLogger(restore: true, desktop: false);

    private static Task DesktopLoggerCapturesEvents()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new TestSkippedException("Visual Studio desktop MSBuild host requires Windows.");
        }
        return VerifyActualLogger(restore: true, desktop: true);
    }

    private static async Task VerifyActualLogger(bool restore, bool desktop)
    {
        using var temp = new TemporaryDirectory();
        var project = temp.Write("日本語 空白/Fixture.proj", """
            <Project>
              <Target Name="Restore">
                <Warning Code="TSTR001" File="src/注文.vb" Text="復元フェーズの診断" />
              </Target>
              <Target Name="Build">
                <Warning Code="TST1001" File="src/注文.vb" Text="警告: &quot;商品&quot; &amp; 改行&#10;次の行" />
                <Error Code="TST2001" File="src/注文.vb" Text="エラー: 参照が見つかりません" />
              </Target>
            </Project>
            """);
        temp.Write("日本語 空白/src/注文.vb");
        var output = Path.Combine(temp.Path, "日本語 空白", "diagnostics; raw.jsonl");
        var loggerPath = LoggerPath();
        var executable = desktop
            ? await MsBuildLocator.FindAsync() ?? throw new InvalidOperationException("Visual Studio MSBuild was not found on this Windows machine.")
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(project)!
        };
        if (!desktop) start.ArgumentList.Add("msbuild");
        if (restore) start.ArgumentList.Add("/restore");
        foreach (var arg in new[] { project, "/nologo", "/m:1", "/nr:false", "/t:Build", "/logger:OneByOneSlnBuilder.Logger.JsonLogger," + loggerPath + ";" + Convert.ToBase64String(Encoding.UTF8.GetBytes(output)) })
            start.ArgumentList.Add(arg);
        var (exitCode, console) = await ExecuteAsync(start);
        AssertEx.True(exitCode != 0, "The Error task must fail the MSBuild fixture. " + console);
        AssertEx.True(File.Exists(output), "MSBuild did not write logger output. " + console);
        var events = File.ReadLines(output).Select(line => JsonSerializer.Deserialize<RawDiagnostic>(line, JsonOptions)!).ToList();
        var warning = events.Single(e => e.Kind == "warning" && e.Code == "TST1001");
        var error = events.Single(e => e.Kind == "error" && e.Code == "TST2001");
        AssertEx.Equal("src/注文.vb", warning.File.Replace('\\', '/'));
        AssertEx.Equal("警告: \"商品\" & 改行\n次の行", warning.Message);
        AssertEx.Equal(Path.GetFullPath(project), Path.GetFullPath(warning.ProjectFile));
        AssertEx.Equal(0, warning.Line);
        AssertEx.Equal("エラー: 参照が見つかりません", error.Message);
        AssertEx.Equal(false, events.Last(e => e.Kind == "buildFinished").Succeeded);
        if (restore)
            AssertEx.Equal("復元フェーズの診断", events.Single(e => e.Kind == "warning" && e.Code == "TSTR001").Message);
        AssertEx.True(DiagnosticMapper.TryMap(warning, Path.GetDirectoryName(project)!, project, out var mapped, out var reason), reason ?? "Actual logger warning was not mappable.");
        AssertEx.Equal("src/注文.vb", mapped!.File);
    }

    private static async Task RunnerCollectsAcrossSolutions()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root 日本語");
        var source = temp.Write("root 日本語/shared/Code.vb");
        var sharedError = new RawDiagnostic { Kind = "error", File = source, Line = 3, Code = "BC1001", Message = "商品がありません\n追加情報" };
        var warning = new RawDiagnostic { Kind = "warning", File = source, Line = 0, Code = "BC2001", Message = "廃止予定" };
        WriteFixture(temp, "root 日本語/A.sln", new FakeBuild { ExitCode = 1, Diagnostics = [sharedError, sharedError, warning, new() { Kind = "error", File = "", Code = "MSB9999", Message = "環境エラー" }] });
        WriteFixture(temp, "root 日本語/nested/B.SLN", new FakeBuild { Diagnostics = [sharedError] });
        temp.Write("root 日本語/ignored.slnx");
        var report = await new BuildRunner().RunAsync(new(root, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath(), "Any CPU"));
        AssertEx.Equal(2, report.Solutions.Count);
        AssertEx.Equal("failed", report.Solutions[0].Status);
        AssertEx.Equal("succeeded", report.Solutions[1].Status);
        AssertEx.Equal(2, report.DiagnosticCount);
        AssertEx.Equal(1, report.UnmappedCount);
        AssertEx.True(!report.Cancelled, "Run unexpectedly cancelled.");
        AssertEx.True(report.DiagnosticsFile != null && File.Exists(report.DiagnosticsFile), "Expected JSONL export.");
        var lines = File.ReadAllLines(report.DiagnosticsFile!);
        AssertEx.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                AssertEx.True(AllowedFields.Contains(property.Name), "Unexpected interchange field: " + property.Name);
                AssertEx.True(property.Value.ValueKind != JsonValueKind.Null, "Null interchange field: " + property.Name);
            }
            AssertEx.Equal("shared/Code.vb", document.RootElement.GetProperty("file").GetString());
            if (document.RootElement.GetProperty("code").GetString() == "BC2001")
                AssertEx.True(!document.RootElement.TryGetProperty("line", out _), "Zero line number must be omitted.");
        }
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "A.sln.arguments.json")))!;
        AssertEx.True(arguments.Contains("/p:Configuration=Release"), "Release property missing.");
        AssertEx.True(arguments.Contains("/t:Rebuild"), "Rebuild target missing.");
        AssertEx.True(arguments.Contains("/restore"), "Restore switch missing.");
        AssertEx.True(arguments.Contains("/p:RestorePackagesConfig=true"), "Legacy NuGet restore missing.");
        AssertEx.True(arguments.Contains("/p:Platform=Any CPU"), "Platform was split or omitted.");
        AssertEx.True(File.Exists(Path.Combine(report.RunDirectory, "summary.json")), "Summary missing.");
        AssertEx.Contains("environment stderr", File.ReadAllText(Path.Combine(report.RunDirectory, report.Solutions[0].LogFile)));
    }

    private static async Task RunnerOmitsWarnings()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        var source = temp.Write("root/Code.cs");
        WriteFixture(temp, "root/App.sln", new FakeBuild { Diagnostics = [new() { Kind = "warning", File = source, Message = "warning" }] });
        var options = new BuildOptions(root, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath(), IncludeWarnings: false, Restore: false);
        var report = await new BuildRunner().RunAsync(options);
        AssertEx.Equal(0, report.DiagnosticCount);
        AssertEx.Equal<string?>(null, report.DiagnosticsFile);
        AssertEx.Equal("succeeded", report.Solutions.Single().Status);
        AssertEx.True(!File.Exists(Path.Combine(report.RunDirectory, "diagnostics.jsonl")), "Zero diagnostics must not leave an importable file.");
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "App.sln.arguments.json")))!;
        AssertEx.True(!arguments.Contains("/restore"), "Restore=false was ignored.");
        var second = await new BuildRunner().RunAsync(options);
        AssertEx.True(report.RunDirectory != second.RunDirectory, "Runs must have separate output directories.");
    }

    private static async Task RunnerDetectsMissingCompletion()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        WriteFixture(temp, "root/App.sln", new FakeBuild { OmitCompletion = true });
        var report = await new BuildRunner().RunAsync(new(root, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath()));
        AssertEx.Equal("incomplete", report.Solutions.Single().Status);
        AssertEx.True(!string.IsNullOrEmpty(report.Solutions.Single().Detail), "Incomplete capture must explain the issue.");
    }

    private static async Task RunnerCancellation()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        var source = temp.Write("root/Code.cs");
        var solution = WriteFixture(temp, "root/A.sln", new FakeBuild { DelayMilliseconds = 30000, Diagnostics = [new() { Kind = "error", File = source, Message = "partial evidence" }] });
        WriteFixture(temp, "root/B.sln", new FakeBuild());
        using var cancellation = new CancellationTokenSource();
        var task = new BuildRunner().RunAsync(new(root, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath(), MaxParallelBuilds: 1), cancellationToken: cancellation.Token);
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(solution + ".started"))
        {
            if (task.IsCompleted) { await task; throw new InvalidOperationException("Fake build exited before signalling startup."); }
            if (timeout.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Fake build did not start.");
            await Task.Delay(25);
        }
        cancellation.Cancel();
        var report = await task.WaitAsync(TimeSpan.FromSeconds(15));
        AssertEx.True(report.Cancelled, "Cancelled run was not marked cancelled.");
        AssertEx.Equal("cancelled", report.Solutions[0].Status);
        AssertEx.Equal("notRun", report.Solutions[1].Status);
        AssertEx.Equal<string?>(null, report.DiagnosticsFile);
        AssertEx.True(!File.Exists(Path.Combine(report.RunDirectory, "diagnostics.jsonl")), "Cancellation published a complete diagnostic file.");
        AssertEx.True(File.Exists(Path.Combine(report.RunDirectory, "diagnostics.partial.jsonl")), "Partial diagnostics were lost.");
        AssertEx.True(!File.Exists(Path.Combine(root, "B.sln.started")), "The second solution ran after cancellation.");
    }

    private static async Task RunnerRejectsEmptyDirectory()
    {
        using var temp = new TemporaryDirectory();
        try
        {
            await new BuildRunner().RunAsync(new(temp.Path, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath()));
            throw new Exception("An empty solution directory was accepted.");
        }
        catch (InvalidOperationException ex) { AssertEx.Contains(".sln", ex.Message); }
    }

    private static string LoggerPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "OneByOneSlnBuilder.Logger.dll");
        AssertEx.True(File.Exists(path), "Logger assembly is missing: " + path);
        return path;
    }

    private static string FakeExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "OneByOneSlnBuilder.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        AssertEx.True(File.Exists(path), "Test apphost is missing: " + path);
        return path;
    }

    private static string WriteFixture(TemporaryDirectory temp, string path, FakeBuild build) => temp.Write(path, JsonSerializer.Serialize(build, JsonOptions));

    private static async Task<int> RunFakeMsBuildAsync(string[] args)
    {
        var fixture = JsonSerializer.Deserialize<FakeBuild>(await File.ReadAllTextAsync(args[0]), JsonOptions)!;
        await File.WriteAllTextAsync(args[0] + ".arguments.json", JsonSerializer.Serialize(args));
        var loggerArgument = args.Single(arg => arg.StartsWith("/logger:", StringComparison.Ordinal));
        var output = Encoding.UTF8.GetString(Convert.FromBase64String(loggerArgument[(loggerArgument.LastIndexOf(';') + 1)..]));
        await WriteProcessIdAsync(args[0] + ".pid");
        await RecordFakeLifecycleAsync(fixture, args[0], "started");
        using var child = await StartFakeChildAsync(fixture, args[0]);
        await using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
        {
            foreach (var diagnostic in fixture.Diagnostics)
                await writer.WriteLineAsync(JsonSerializer.Serialize(diagnostic, JsonOptions));
            await writer.FlushAsync();
            await File.WriteAllTextAsync(args[0] + ".started", "started");
            Console.WriteLine("fake MSBuild stdout");
            Console.Error.WriteLine("environment stderr");
            if (fixture.ReleaseGate != null) await WaitForFakeGateAsync(fixture.ReleaseGate);
            if (fixture.DelayMilliseconds > 0) await Task.Delay(fixture.DelayMilliseconds);
            if (!fixture.OmitCompletion)
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { kind = "buildFinished", succeeded = fixture.ExitCode == 0 }, JsonOptions));
        }
        await RecordFakeLifecycleAsync(fixture, args[0], "finished");
        await File.WriteAllTextAsync(args[0] + ".ended", "finished");
        return fixture.ExitCode;
    }

    private static async Task<(int ExitCode, string Output)> ExecuteAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("MSBuild fixture timed out.");
        }
        return (process.ExitCode, await stdout + "\n" + await stderr);
    }

    private sealed class TestSkippedException(string message) : Exception(message) { }

    private sealed class FakeBuild
    {
        public int ExitCode { get; set; }
        public int DelayMilliseconds { get; set; }
        public bool OmitCompletion { get; set; }
        public string? ReleaseGate { get; set; }
        public string? LifecycleDirectory { get; set; }
        public bool SpawnChild { get; set; }
        public RawDiagnostic[] Diagnostics { get; set; } = [];
    }
}
