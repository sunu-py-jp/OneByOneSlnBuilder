namespace OneByOneSlnBuilder.Core;

public static class DiagnosticMapper
{
    public static bool TryMap(RawDiagnostic diagnostic, string rootDirectory, string solutionPath,
        out DiagnosticRecord? mapped, out string? reason)
    {
        mapped = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(diagnostic.File))
            return Fail("ファイルが指定されていない診断です。", out reason);
        if (string.IsNullOrWhiteSpace(diagnostic.Message) || diagnostic.Message.Contains('\0') || diagnostic.Code.Contains('\0'))
            return Fail("メッセージが空か、JSONLで使用できない文字が含まれています。", out reason);
        try
        {
            var root = Path.GetFullPath(rootDirectory);
            var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
            var project = NormalizeSeparators(diagnostic.ProjectFile);
            var baseDirectory = solutionDirectory;
            if (!string.IsNullOrWhiteSpace(project))
                baseDirectory = Path.GetDirectoryName(Path.GetFullPath(project, solutionDirectory))!;
            var fullPath = Path.GetFullPath(NormalizeSeparators(diagnostic.File), baseDirectory);
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
                || relative == "." || relative.IndexOfAny([':', '\0', '\r', '\n']) >= 0)
                return Fail("対象フォルダの外、または対応できないパスです。", out reason);
            if (!System.IO.File.Exists(fullPath))
                return Fail("診断のファイルが存在しません（ツール名などの可能性があります）。", out reason);
            if (OperatingSystem.IsWindows())
                relative = GetDiskRelativePath(root, relative);
            mapped = new DiagnosticRecord(relative, diagnostic.Message, diagnostic.Line > 0 ? diagnostic.Line : null,
                string.IsNullOrEmpty(diagnostic.Code) ? null : diagnostic.Code);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Fail("パスを解決できません: " + ex.Message, out reason);
        }
    }
    // OneByOne's file index is case-sensitive even on Windows. Preserve disk names.
    private static string GetDiskRelativePath(string root, string relative)
    {
        var current = root;
        var parts = relative.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = new DirectoryInfo(current).EnumerateFileSystemInfos(parts[i]).FirstOrDefault();
            if (entry == null) throw new IOException("診断ファイルが見つからなくなりました。");
            parts[i] = entry.Name;
            current = entry.FullName;
        }
        return string.Join("/", parts);
    }
    private static string NormalizeSeparators(string value) => value.Replace('\\', Path.DirectorySeparatorChar);
    private static bool Fail(string message, out string? reason) { reason = message; return false; }
}
