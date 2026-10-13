using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneByOneSlnBuilder.Core;

public sealed record BuildOptions(string RootDirectory, string OutputDirectory, string MsBuildPath,
    string LoggerPath, string? Platform = null, bool IncludeWarnings = true, bool Restore = true, int MaxParallelBuilds = 2);
public sealed record BuildProgress(int Completed, int Total, string Message, int Running = 0);
public sealed record SolutionResult(string Solution, string Status, int? ExitCode,
    int ExportedDiagnostics, int UnmappedDiagnostics, string LogFile, string? Detail = null);
public sealed record BuildReport(string RootDirectory, string RunDirectory, string? DiagnosticsFile,
    IReadOnlyList<SolutionResult> Solutions, int DiagnosticCount, int UnmappedCount, bool Cancelled);
public sealed record DiagnosticRecord(string File, string Message, int? Line = null, string? Code = null);
public sealed class RawDiagnostic
{
    public string Kind { get; set; } = "";
    public string File { get; set; } = "";
    public string ProjectFile { get; set; } = "";
    public int Line { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool? Succeeded { get; set; }
}
internal static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
