using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OneByOneSlnBuilder.Core;

namespace OneByOneSlnBuilder.Tests;

internal static partial class Program
{
    private static async Task RunnerParallelismAndDeterministicOutput()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        var lifecycle = Path.Combine(temp.Path, "lifecycle");
        Directory.CreateDirectory(lifecycle);
        var alpha = temp.Write("root/Alpha.vb");
        var zeta = temp.Write("root/Zeta.vb");
        var common = new RawDiagnostic { Kind = "error", File = alpha, Line = 8, Code = "BC1001", Message = "共有診断" };
        var first = new RawDiagnostic { Kind = "warning", File = alpha, Line = 2, Code = "BC2001", Message = "先頭診断" };
        var last = new RawDiagnostic { Kind = "error", File = zeta, Line = 1, Code = "BC1002", Message = "末尾診断" };
        var gates = Enumerable.Range(0, 4).Select(i => Path.Combine(temp.Path, "release-" + i)).ToArray();
        var solutions = new[]
        {
            WriteFixture(temp, "root/A.sln", new FakeBuild { ExitCode = 1, ReleaseGate = gates[0], LifecycleDirectory = lifecycle, Diagnostics = [last, common] }),
            WriteFixture(temp, "root/B.sln", new FakeBuild { ExitCode = 1, ReleaseGate = gates[1], LifecycleDirectory = lifecycle, Diagnostics = [common, first] }),
            WriteFixture(temp, "root/C.sln", new FakeBuild { ReleaseGate = gates[2], LifecycleDirectory = lifecycle, Diagnostics = [first, common] }),
            WriteFixture(temp, "root/D.sln", new FakeBuild { ReleaseGate = gates[3], LifecycleDirectory = lifecycle, Diagnostics = [last] })
        };
        var progress = new ProgressRecorder();
        using var cancellation = new CancellationTokenSource();
        var task = new BuildRunner().RunAsync(new(root, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath(), MaxParallelBuilds: 2), progress, cancellation.Token);
        try
        {
            // A and B must both have emitted diagnostics while their gates are closed.
            // A serial implementation cannot reach this barrier.
            await WaitForMarkersAsync([solutions[0] + ".started", solutions[1] + ".started"], task);
            AssertEx.True(!File.Exists(solutions[0] + ".ended") && !File.Exists(solutions[1] + ".ended"), "The overlap barrier was crossed after a build had finished.");
            AssertEx.True(!File.Exists(solutions[2] + ".started") && !File.Exists(solutions[3] + ".started"), "Queued work started while both slots were occupied.");

            // Keep A blocked. Each remaining solution may start only when the prior
            // occupant finishes, making completion order intentionally B, C, D, A.
            File.WriteAllText(gates[1], "release");
            await WaitForMarkersAsync([solutions[1] + ".ended", solutions[2] + ".started"], task);
            AssertEx.True(!File.Exists(solutions[0] + ".ended"), "A escaped its gate.");
            File.WriteAllText(gates[2], "release");
            await WaitForMarkersAsync([solutions[2] + ".ended", solutions[3] + ".started"], task);
            File.WriteAllText(gates[3], "release");
            await WaitForMarkersAsync([solutions[3] + ".ended"], task);
            File.WriteAllText(gates[0], "release");
            var report = await task.WaitAsync(TimeSpan.FromSeconds(20));

            var events = File.ReadLines(Path.Combine(lifecycle, "events.jsonl"))
                .Select(line => JsonSerializer.Deserialize<FakeProcessEvent>(line, JsonOptions)!).ToArray();
            var active = new HashSet<string>(StringComparer.Ordinal);
            var maximum = 0;
            var completed = new List<string>();
            foreach (var item in events)
            {
                if (item.Kind == "started")
                {
                    AssertEx.True(active.Add(item.Solution), "A solution started twice.");
                    maximum = Math.Max(maximum, active.Count);
                }
                else
                {
                    AssertEx.True(active.Remove(item.Solution), "A solution finished without starting.");
                    completed.Add(Path.GetFileName(item.Solution));
                }
                AssertEx.True(active.Count <= 2, "Real worker overlap exceeded the configured concurrency limit.");
            }
            AssertEx.Equal(2, maximum, "No real process overlap occurred.");
            AssertEx.Equal(0, active.Count);
            AssertEx.Equal("B.sln,C.sln,D.sln,A.sln", string.Join(',', completed));
            AssertEx.Equal("A.sln,B.sln,C.sln,D.sln", string.Join(',', report.Solutions.Select(s => s.Solution)));
            AssertEx.Equal("failed,failed,succeeded,succeeded", string.Join(',', report.Solutions.Select(s => s.Status)));
            AssertEx.Equal(3, report.DiagnosticCount);
            AssertEx.True(report.DiagnosticsFile != null, "Parallel run did not export its diagnostics.");
            var records = File.ReadLines(report.DiagnosticsFile!).Select(line => JsonSerializer.Deserialize<DiagnosticRecord>(line, JsonOptions)!).ToArray();
            AssertEx.Equal(3, records.Length, "Duplicates from separate workers were not removed.");
            AssertEx.Equal("Alpha.vb:2,Alpha.vb:8,Zeta.vb:1", string.Join(',', records.Select(d => d.File + ":" + d.Line)));
            foreach (var solution in solutions)
            {
                var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(solution + ".arguments.json"))!;
                AssertEx.True(arguments.Contains("/m:1"), "Parallel solutions enabled nested MSBuild worker parallelism.");
                AssertEx.True(arguments.Contains("/nr:false"), "MSBuild node reuse must remain disabled.");
            }
            AssertProgress(progress, total: 4, limit: 2, expectedCompleted: 4);
        }
        finally { await StopFixtureRunAsync(task, cancellation, gates); }
    }

    private static async Task RunnerParallelCancellation()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        var source = temp.Write("root/Code.vb");
        var gates = Enumerable.Range(0, 4).Select(i => Path.Combine(temp.Path, "release-" + i)).ToArray();
        var solutions = Enumerable.Range(0, 4).Select(i => WriteFixture(temp, "root/" + (char)('A' + i) + ".sln",
            new FakeBuild
            {
                ReleaseGate = gates[i], SpawnChild = true,
                Diagnostics = [new() { Kind = "error", File = source, Line = i + 1, Code = "BC1001", Message = "partial " + i }]
            })).ToArray();
        var progress = new ProgressRecorder();
        using var cancellation = new CancellationTokenSource();
        var task = new BuildRunner().RunAsync(new(root, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath(), MaxParallelBuilds: 2), progress, cancellation.Token);
        int[] processIds = [];
        try
        {
            await WaitForMarkersAsync([solutions[0] + ".started", solutions[1] + ".started"], task);
            processIds = solutions.Take(2).SelectMany(solution => new[] { solution + ".pid", solution + ".child.pid" })
                .Select(path => int.Parse(File.ReadAllText(path))).ToArray();
            AssertEx.Equal(4, processIds.Distinct().Count());
            AssertEx.True(processIds.All(IsProcessRunning), "The cancellation barrier requires two live workers and their live children.");
            cancellation.Cancel();
            var report = await task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitForProcessesToExitAsync(processIds);
            AssertEx.True(report.Cancelled, "Parallel cancellation was not recorded.");
            AssertEx.Equal("cancelled,cancelled,notRun,notRun", string.Join(',', report.Solutions.Select(s => s.Status)));
            AssertEx.Equal<string?>(null, report.DiagnosticsFile);
            AssertEx.Equal(2, report.DiagnosticCount, "Diagnostics from both running builds must survive cancellation.");
            AssertEx.True(!File.Exists(Path.Combine(report.RunDirectory, "diagnostics.jsonl")), "Cancelled work published complete diagnostics.");
            var partial = Path.Combine(report.RunDirectory, "diagnostics.partial.jsonl");
            AssertEx.Equal(2, File.ReadAllLines(partial).Length);
            foreach (var solution in solutions.Skip(2))
            {
                AssertEx.True(!File.Exists(solution + ".arguments.json"), "A queued process was launched after cancellation.");
                AssertEx.True(!File.Exists(solution + ".started"), "A queued build began after cancellation.");
            }
            AssertEx.True(solutions.All(solution => !File.Exists(solution + ".ended")), "A cancelled fixture escaped its closed gate.");
            AssertProgress(progress, total: 4, limit: 2, expectedCompleted: 2);
        }
        finally
        {
            await StopFixtureRunAsync(task, cancellation, gates);
            foreach (var id in processIds)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task RunnerRejectsInvalidParallelism()
    {
        using var temp = new TemporaryDirectory();
        var solution = WriteFixture(temp, "root/App.sln", new FakeBuild());
        foreach (var limit in new[] { -1, 0, 17, int.MaxValue })
        {
            var rejected = false;
            try
            {
                await new BuildRunner().RunAsync(new(Path.GetDirectoryName(solution)!, Path.Combine(temp.Path, "output"), FakeExecutable(), LoggerPath(), MaxParallelBuilds: limit));
            }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            AssertEx.True(rejected, "Concurrency limit was accepted: " + limit);
            AssertEx.True(!File.Exists(solution + ".arguments.json"), "Invalid limits must be rejected before a process is started.");
        }
    }

    private static void AssertProgress(ProgressRecorder recorder, int total, int limit, int expectedCompleted)
    {
        AssertEx.True(!recorder.HadConcurrentCallbacks, "Progress callbacks were not serialized.");
        var snapshots = recorder.Snapshot();
        AssertEx.True(snapshots.Count > 0, "No progress was reported.");
        var previous = 0;
        foreach (var progress in snapshots)
        {
            AssertEx.True(progress.Completed >= previous, "Completed count moved backwards.");
            AssertEx.True(progress.Completed <= total, "Completed count exceeded solution count.");
            AssertEx.True(progress.Running >= 0 && progress.Running <= limit, "Running count exceeded its bounds.");
            AssertEx.True(progress.Completed + progress.Running <= total, "Completed and running counts overlap.");
            previous = progress.Completed;
        }
        AssertEx.True(snapshots.Any(p => p.Running == limit), "Progress never reported both running builds.");
        AssertEx.Equal(expectedCompleted, snapshots[^1].Completed);
        AssertEx.Equal(total, snapshots[^1].Total);
        AssertEx.Equal(0, snapshots[^1].Running);
    }

    private static async Task WaitForMarkersAsync(string[] paths, Task run)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!paths.All(File.Exists))
        {
            if (run.IsCompleted)
            {
                await run;
                throw new InvalidOperationException("Run ended before fixture markers appeared: " + string.Join(", ", paths.Where(path => !File.Exists(path))));
            }
            try { await Task.Delay(20, deadline.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("Fixture barrier was not reached: " + string.Join(", ", paths.Where(path => !File.Exists(path)))); }
        }
    }

    private static async Task StopFixtureRunAsync(Task run, CancellationTokenSource cancellation, IEnumerable<string> gates)
    {
        cancellation.Cancel();
        foreach (var gate in gates) File.WriteAllText(gate, "cleanup");
        try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* Preserve the original assertion while releasing test processes. */ }
    }

    private static bool IsProcessRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitForProcessesToExitAsync(int[] ids)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (ids.Any(IsProcessRunning))
        {
            try { await Task.Delay(20, deadline.Token); }
            catch (OperationCanceledException) { throw new InvalidOperationException("Cancellation left worker processes alive: " + string.Join(',', ids.Where(IsProcessRunning))); }
        }
    }

    private static async Task WriteProcessIdAsync(string path)
    {
        await File.WriteAllTextAsync(path + ".tmp", Environment.ProcessId.ToString());
        File.Move(path + ".tmp", path);
    }

    private static async Task<Process?> StartFakeChildAsync(FakeBuild fixture, string solution)
    {
        if (!fixture.SpawnChild) return null;
        var gate = fixture.ReleaseGate ?? throw new InvalidOperationException("Child fixtures require a release gate.");
        var pidPath = solution + ".child.pid";
        var start = new ProcessStartInfo(FakeExecutable()) { UseShellExecute = false };
        start.ArgumentList.Add("--wait-child");
        start.ArgumentList.Add(pidPath);
        start.ArgumentList.Add(gate);
        var child = Process.Start(start) ?? throw new InvalidOperationException("Fixture child did not start.");
        try
        {
            await WaitForMarkersAsync([pidPath], child.WaitForExitAsync());
            return child;
        }
        catch
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.Dispose();
            throw;
        }
    }

    private static async Task<int> RunFakeChildAsync(string pidPath, string gate)
    {
        await WriteProcessIdAsync(pidPath);
        await WaitForFakeGateAsync(gate);
        return 0;
    }

    private static async Task WaitForFakeGateAsync(string gate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(gate)) await Task.Delay(20, deadline.Token);
    }

    private static async Task RecordFakeLifecycleAsync(FakeBuild fixture, string solution, string kind)
    {
        if (fixture.LifecycleDirectory == null) return;
        var lockPath = Path.Combine(fixture.LifecycleDirectory, "journal.lock");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        FileStream? exclusive = null;
        while (exclusive == null)
        {
            try { exclusive = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(10, deadline.Token); }
        }
        using (exclusive)
        {
            var line = JsonSerializer.Serialize(new FakeProcessEvent(kind, solution), JsonOptions);
            await File.AppendAllTextAsync(Path.Combine(fixture.LifecycleDirectory, "events.jsonl"), line + "\n", new UTF8Encoding(false));
        }
    }

    private sealed record FakeProcessEvent(string Kind, string Solution);

    private sealed class ProgressRecorder : IProgress<BuildProgress>
    {
        private readonly List<BuildProgress> snapshots = [];
        private int inCallback;
        private int concurrentCallbacks;
        public bool HadConcurrentCallbacks => Volatile.Read(ref concurrentCallbacks) != 0;
        public void Report(BuildProgress value)
        {
            if (Interlocked.Increment(ref inCallback) > 1) Interlocked.Exchange(ref concurrentCallbacks, 1);
            try { lock (snapshots) snapshots.Add(value); }
            finally { Interlocked.Decrement(ref inCallback); }
        }
        public IReadOnlyList<BuildProgress> Snapshot() { lock (snapshots) return snapshots.ToArray(); }
    }
}
