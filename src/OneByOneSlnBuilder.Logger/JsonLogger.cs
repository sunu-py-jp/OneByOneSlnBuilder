using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;

namespace OneByOneSlnBuilder.Logger;

/// <summary>
/// Captures structured MSBuild diagnostics without parsing localized console output.
/// Parameters contains a single Base64-encoded UTF-8 output path, so delimiters in
/// the output path cannot be confused with MSBuild's logger parameter syntax.
/// </summary>
public sealed class JsonLogger : ILogger
{
    private readonly object gate = new object();
    private readonly Dictionary<(int NodeId, int ProjectContextId), string> projects =
        new Dictionary<(int NodeId, int ProjectContextId), string>();
    private IEventSource? source;
    private StreamWriter? writer;

    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Normal;
    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        if (eventSource == null)
            throw new LoggerException("An MSBuild event source is required.");

        lock (gate)
        {
            if (source != null || writer != null)
                throw new LoggerException("The JSON diagnostic logger is already initialized.");

            try
            {
                if (string.IsNullOrWhiteSpace(Parameters))
                    throw new ArgumentException("The Base64-encoded output path is missing.");

                var path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(Parameters));
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("The output path is empty.");

                // Creating even an empty file records that logger initialization ran.
                // Only the real BuildFinished event may add the completion record.
                writer = new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                projects.Clear();
                source = eventSource;
                source.ProjectStarted += OnProjectStarted;
                source.ErrorRaised += OnError;
                source.WarningRaised += OnWarning;
                source.BuildFinished += OnBuildFinished;
            }
            catch (Exception ex)
            {
                Unsubscribe();
                try { writer?.Dispose(); }
                catch { /* Preserve the original initialization failure. */ }
                writer = null;
                throw new LoggerException("Cannot initialize the JSON diagnostic logger: " + ex.Message, ex);
            }
        }
    }

    public void Shutdown()
    {
        lock (gate)
        {
            Unsubscribe();
            var output = writer;
            writer = null;
            projects.Clear();
            try
            {
                output?.Dispose();
            }
            catch (Exception ex)
            {
                throw new LoggerException("Cannot close the JSON diagnostic log: " + ex.Message, ex);
            }
        }
    }

    private void Unsubscribe()
    {
        if (source == null)
            return;

        source.ProjectStarted -= OnProjectStarted;
        source.ErrorRaised -= OnError;
        source.WarningRaised -= OnWarning;
        source.BuildFinished -= OnBuildFinished;
        source = null;
    }

    private void OnProjectStarted(object sender, ProjectStartedEventArgs e)
    {
        lock (gate)
        {
            if (writer != null && HasProjectContext(e.BuildEventContext) && !string.IsNullOrEmpty(e.ProjectFile))
                projects[(e.BuildEventContext!.NodeId, e.BuildEventContext.ProjectContextId)] = e.ProjectFile!;
        }
    }

    private void OnError(object sender, BuildErrorEventArgs e)
    {
        WriteDiagnostic("error", e.File, e.ProjectFile, e.LineNumber, e.Code, e.Message, e.BuildEventContext);
    }

    private void OnWarning(object sender, BuildWarningEventArgs e)
    {
        WriteDiagnostic("warning", e.File, e.ProjectFile, e.LineNumber, e.Code, e.Message, e.BuildEventContext);
    }

    private void WriteDiagnostic(string kind, string? file, string? projectFile, int line,
        string? code, string? message, BuildEventContext? context)
    {
        lock (gate)
        {
            if (writer == null)
                return;

            if (string.IsNullOrEmpty(projectFile) && HasProjectContext(context))
                projects.TryGetValue((context!.NodeId, context.ProjectContextId), out projectFile);

            var json = new StringBuilder(256);
            json.Append("{\"kind\":");
            AppendString(json, kind);
            json.Append(",\"file\":");
            AppendString(json, file);
            json.Append(",\"projectFile\":");
            AppendString(json, projectFile);
            json.Append(",\"line\":").Append(line.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"code\":");
            AppendString(json, code);
            json.Append(",\"message\":");
            AppendString(json, message);
            json.Append('}');
            WriteLine(json.ToString());
        }
    }

    private void OnBuildFinished(object sender, BuildFinishedEventArgs e)
    {
        lock (gate)
        {
            if (writer != null)
                WriteLine(e.Succeeded
                    ? "{\"kind\":\"buildFinished\",\"succeeded\":true}"
                    : "{\"kind\":\"buildFinished\",\"succeeded\":false}");
        }
    }

    private void WriteLine(string json)
    {
        // The caller holds gate, serializing writes and Shutdown even if events
        // originate from multiple MSBuild nodes.
        try
        {
            writer!.WriteLine(json);
        }
        catch (Exception ex)
        {
            throw new LoggerException("Cannot write the JSON diagnostic log: " + ex.Message, ex);
        }
    }

    private static bool HasProjectContext(BuildEventContext? context)
    {
        return context != null
            && context.NodeId != BuildEventContext.InvalidNodeId
            && context.ProjectContextId != BuildEventContext.InvalidProjectContextId;
    }

    private static void AppendString(StringBuilder json, string? value)
    {
        json.Append('"');
        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    // Escaping each surrogate preserves valid pairs and makes even
                    // malformed UTF-16 safe to write as one JSONL record.
                    if (c < 0x20 || char.IsSurrogate(c))
                        json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        json.Append(c);
                    break;
            }
        }
        json.Append('"');
    }
}
