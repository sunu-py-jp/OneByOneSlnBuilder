using System.Diagnostics;
using OneByOneSlnBuilder.Core;

namespace OneByOneSlnBuilder.App;

internal sealed class MainForm : Form
{
    private const int MaximumLogLines = 400;
    private readonly TextBox _root = new() { Dock = DockStyle.Fill };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly TextBox _msBuild = new() { Dock = DockStyle.Fill };
    private readonly TextBox _platform = new() { Width = 230, PlaceholderText = "空欄: ソリューションの既定値" };
    private readonly NumericUpDown _parallelBuilds = new()
    {
        Minimum = 1, Maximum = 16, Value = 2, Width = 65, AccessibleName = "同時ビルド数"
    };
    private readonly CheckBox _warnings = new() { AutoSize = true, Checked = true, Text = "警告も診断に含める" };
    private readonly CheckBox _restore = new() { AutoSize = true, Checked = true, Text = "ビルド前にパッケージを復元する" };
    private readonly Button _start = new() { AutoSize = true, Text = "Release ビルド開始", Padding = new Padding(14, 7, 14, 7) };
    private readonly Button _cancel = new() { AutoSize = true, Text = "キャンセル", Enabled = false, Padding = new Padding(8, 7, 8, 7) };
    private readonly Button _openResults = new() { AutoSize = true, Text = "結果フォルダを開く", Enabled = false };
    private readonly Button _copyJsonl = new() { AutoSize = true, Text = "JSONL パスをコピー", Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1, Height = 15 };
    private readonly Label _status = new() { AutoSize = true, Text = "対象フォルダを選択してください。", MaximumSize = new Size(880, 0) };
    private readonly Label _resultNote = new() { AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105), MaximumSize = new Size(880, 0) };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false,
        BackColor = Color.FromArgb(248, 250, 252), BorderStyle = BorderStyle.FixedSingle
    };
    private readonly TableLayoutPanel _settings = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 10, 0, 10) };
    private readonly Queue<string> _logLines = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _buildCts;
    private string? _automaticOutput;
    private BuildReport? _report;
    private BuildProgress? _lastProgress;
    private bool _closeRequested;

    public MainForm()
    {
        Text = "OneByOne Sln Builder";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(980, 720);
        MinimumSize = new Size(840, 650);
        Font = new Font("Yu Gothic UI", 9.5F);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9
        };
        for (var i = 0; i < 9; i++)
            layout.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.AutoSize, i == 6 ? 100 : 0));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = new Label
        {
            Text = "OneByOne Sln Builder", AutoSize = true,
            Font = new Font(Font.FontFamily, 21, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 49, 83), Margin = new Padding(0, 0, 0, 8)
        };
        var introduction = new Label
        {
            Text = "フォルダ内の .sln を並列に Release ビルドし、OneByOneMigration 用の診断 JSONL を出力します。\n対象フォルダは OneByOneMigration と同じルートを指定してください。",
            AutoSize = true, Margin = new Padding(0, 0, 0, 8)
        };
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(introduction, 0, 1);
        layout.Controls.Add(CreateSettings(), 0, 2);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 10) };
        _start.BackColor = Color.FromArgb(28, 74, 124);
        _start.ForeColor = Color.White;
        _start.FlatStyle = FlatStyle.Flat;
        _start.FlatAppearance.BorderSize = 0;
        actions.Controls.AddRange([_start, _cancel]);
        layout.Controls.Add(actions, 0, 3);
        _status.Margin = new Padding(0, 0, 0, 9);
        layout.Controls.Add(_status, 0, 4);
        _progress.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(_progress, 0, 5);
        _log.Margin = new Padding(0);
        layout.Controls.Add(_log, 0, 6);
        var resultActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 6) };
        resultActions.Controls.AddRange([_openResults, _copyJsonl]);
        layout.Controls.Add(resultActions, 0, 7);
        _resultNote.Text = "結果は実行ごとに新しいフォルダへ保存します。";
        layout.Controls.Add(_resultNote, 0, 8);
        Controls.Add(layout);

        _root.TextChanged += (_, _) => UpdateDefaultOutput();
        _start.Click += async (_, _) => await StartBuildAsync();
        _cancel.Click += (_, _) => RequestCancellation();
        _openResults.Click += (_, _) => OpenResults();
        _copyJsonl.Click += (_, _) => CopyJsonlPath();
        Shown += async (_, _) => await DetectMsBuildAsync();
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => { _lifetimeCts.Cancel(); _lifetimeCts.Dispose(); };
    }

    private Control CreateSettings()
    {
        _settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 133));
        _settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 93));
        AddPathRow(0, "対象フォルダ", _root, () => SelectDirectory(_root, "全 .sln を検索するルートフォルダを選択"));
        AddPathRow(1, "出力先フォルダ", _output, () => SelectDirectory(_output, "ビルド結果の保存先を選択"));
        AddPathRow(2, "MSBuild.exe", _msBuild, SelectMsBuild);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        options.Controls.Add(new Label { Text = "Platform", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        options.Controls.Add(_platform);
        options.Controls.Add(new Label { Text = "同時ビルド数", AutoSize = true, Margin = new Padding(20, 6, 8, 0) });
        options.Controls.Add(_parallelBuilds);
        _settings.Controls.Add(options, 0, 3);
        _settings.SetColumnSpan(options, 3);
        var checkboxes = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        _warnings.Margin = new Padding(0, 4, 18, 4);
        _restore.Margin = new Padding(0, 4, 0, 4);
        checkboxes.Controls.AddRange([_warnings, _restore]);
        _settings.Controls.Add(checkboxes, 0, 4);
        _settings.SetColumnSpan(checkboxes, 3);
        var parallelHelp = new Label
        {
            Text = "1 は順次実行。同じプロジェクトや出力先（bin / obj など）を共有する場合は、1 にしてください。",
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 6, 0, 0)
        };
        _settings.Controls.Add(parallelHelp, 0, 5);
        _settings.SetColumnSpan(parallelHelp, 3);
        return _settings;
    }

    private void AddPathRow(int row, string label, TextBox input, Action browse)
    {
        _settings.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 8, 8) }, 0, row);
        input.Margin = new Padding(0, 3, 8, 8);
        _settings.Controls.Add(input, 1, row);
        var button = new Button { Text = "参照…", Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 7) };
        button.Click += (_, _) => browse();
        _settings.Controls.Add(button, 2, row);
    }

    private void SelectDirectory(TextBox input, string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(input.Text)) dialog.SelectedPath = input.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath;
    }

    private void SelectMsBuild()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Visual Studio / Build Tools の MSBuild.exe を選択",
            Filter = "MSBuild.exe|MSBuild.exe|実行ファイル (*.exe)|*.exe", CheckFileExists = true
        };
        if (File.Exists(_msBuild.Text)) dialog.FileName = _msBuild.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _msBuild.Text = dialog.FileName;
    }

    private void UpdateDefaultOutput()
    {
        if (!string.IsNullOrWhiteSpace(_output.Text) && _output.Text != _automaticOutput) return;
        try
        {
            var root = CleanPath(_root.Text);
            if (!Path.IsPathFullyQualified(root)) return;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) ?? root;
            _automaticOutput = Path.Combine(parent, "OneByOne-build-results");
            _output.Text = _automaticOutput;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Let the user finish entering the path; validation runs before building.
        }
    }

    private async Task DetectMsBuildAsync()
    {
        _start.Enabled = false;
        _status.Text = "Visual Studio / Build Tools の MSBuild を検索中…";
        try
        {
            var found = await MsBuildLocator.FindAsync(_lifetimeCts.Token);
            if (IsDisposed) return;
            if (string.IsNullOrWhiteSpace(_msBuild.Text) && found is not null) _msBuild.Text = found;
            _status.Text = found is null
                ? "MSBuild が見つかりません。Visual Studio / Build Tools の MSBuild.exe を指定してください。"
                : "準備完了。対象フォルダを選択してビルドを開始してください。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _status.Text = "MSBuild を自動検出できませんでした。参照から指定してください。";
            AppendLog($"MSBuild 検索: {ex.Message}");
        }
        finally
        {
            if (!IsDisposed) _start.Enabled = true;
        }
    }

    private async Task StartBuildAsync()
    {
        if (_buildCts is not null) return;
        BuildOptions options;
        try
        {
            var root = RequireFullPath(_root.Text, "対象フォルダ");
            var output = RequireFullPath(_output.Text, "出力先フォルダ");
            var msBuild = RequireFullPath(_msBuild.Text, "MSBuild.exe");
            if (!Directory.Exists(root)) throw new ArgumentException("対象フォルダが存在しません。");
            if (!File.Exists(msBuild)) throw new ArgumentException("指定した MSBuild.exe が存在しません。");
            var logger = Path.Combine(AppContext.BaseDirectory, "OneByOneSlnBuilder.Logger.dll");
            if (!File.Exists(logger)) throw new FileNotFoundException("診断ロガーが見つかりません。配布 ZIP をすべて展開して起動してください。", logger);
            options = new BuildOptions(root, output, msBuild, logger,
                string.IsNullOrWhiteSpace(_platform.Text) ? null : _platform.Text.Trim(), _warnings.Checked, _restore.Checked,
                MaxParallelBuilds: (int)_parallelBuilds.Value);
        }
        catch (Exception ex)
        {
            ShowError("入力内容を確認してください", ex);
            return;
        }

        _report = null;
        _lastProgress = null;
        _buildCts = new CancellationTokenSource();
        _logLines.Clear();
        _log.Clear();
        _progress.Maximum = 1;
        _progress.Value = 0;
        _progress.Style = ProgressBarStyle.Marquee;
        _resultNote.Text = "結果フォルダを準備しています…";
        SetRunning(true);
        AppendLog($"対象: {options.RootDirectory}");
        AppendLog($"出力先: {options.OutputDirectory}");
        AppendLog($"構成: Release / Platform: {options.Platform ?? "ソリューションの既定値"}");
        AppendLog($"同時ビルド数: {options.MaxParallelBuilds}");
        _status.Text = "ソリューションを検索しています…";
        var progress = new Progress<BuildProgress>(update =>
        {
            if (IsDisposed || _buildCts is null) return;
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Maximum = Math.Max(1, update.Total);
            _progress.Value = Math.Clamp(update.Completed, 0, _progress.Maximum);
            _lastProgress = update;
            _status.Text = FormatProgressPrefix(update) + (_buildCts.IsCancellationRequested
                ? "キャンセル中… 実行中のすべてのビルドを停止しています。"
                : update.Message);
            AppendLog(update.Message);
        });
        try
        {
            var cancellationToken = _buildCts.Token;
            _report = await Task.Run(() => new BuildRunner().RunAsync(options, progress, cancellationToken));
            if (IsDisposed) return;
            var failedCount = _report.Solutions.Count(solution => solution.Status is "failed" or "incomplete" or "startFailed");
            var completion = _report.Cancelled ? "キャンセルしました" : "完了しました";
            _status.Text = $"{completion}: {_report.Solutions.Count} ソリューション / 診断 {_report.DiagnosticCount} 件 / ビルド失敗 {failedCount} 件";
            _resultNote.Text = _report.Cancelled
                ? "中断された実行です。結果フォルダにある途中までの診断とログを確認してください。"
                : _report.DiagnosticCount == 0 && (failedCount > 0 || _report.UnmappedCount > 0)
                    ? "診断 JSONL はありません。ビルド失敗／対象外の診断があります。結果ログを確認してください。"
                    : _report.DiagnosticCount == 0
                        ? "診断 0 件です。OneByOneMigration 側に以前の診断が残っている場合はクリアしてください。"
                        : "diagnostics.jsonl を OneByOneMigration の診断ファイルとして指定してください。";
            if (_report.UnmappedCount > 0)
                _resultNote.Text += $"  ファイルを特定できない診断 {_report.UnmappedCount} 件は結果フォルダを確認してください。";
            AppendLog(_status.Text);
            AppendLog($"結果フォルダ: {_report.RunDirectory}");
            if (_report.DiagnosticsFile is not null) AppendLog($"診断 JSONL: {_report.DiagnosticsFile}");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "キャンセルしました。";
            _resultNote.Text = "出力先フォルダに保存された実行ログを確認してください。";
            AppendLog(_status.Text);
        }
        catch (Exception ex)
        {
            _status.Text = "ビルド処理を完了できませんでした。";
            _resultNote.Text = "入力内容、MSBuild のインストール状況、出力先のアクセス権を確認してください。";
            AppendLog(ex.ToString());
            if (!_closeRequested) ShowError("ビルド処理に失敗しました", ex);
        }
        finally
        {
            _buildCts.Dispose();
            _buildCts = null;
            if (!IsDisposed)
            {
                _progress.Style = ProgressBarStyle.Blocks;
                SetRunning(false);
                if (_closeRequested) BeginInvoke(new Action(Close));
            }
        }
    }

    private void SetRunning(bool running)
    {
        _settings.Enabled = !running;
        _start.Enabled = !running;
        _cancel.Enabled = running;
        _openResults.Enabled = !running && _report is not null && Directory.Exists(_report.RunDirectory);
        _copyJsonl.Enabled = !running && _report?.DiagnosticsFile is string path && File.Exists(path);
        UseWaitCursor = false;
    }

    private void RequestCancellation()
    {
        if (_buildCts is null || _buildCts.IsCancellationRequested) return;
        _cancel.Enabled = false;
        _status.Text = FormatProgressPrefix(_lastProgress) + "キャンセル中… 実行中のすべてのビルドを停止しています。";
        AppendLog(_status.Text);
        _buildCts.Cancel();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_buildCts is null) return;
        e.Cancel = true;
        _closeRequested = true;
        RequestCancellation();
    }

    private void AppendLog(string message)
    {
        foreach (var line in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var visibleLine = line.Length > 2000 ? line[..2000] + " …（以降省略）" : line;
            _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {visibleLine}");
            while (_logLines.Count > MaximumLogLines) _logLines.Dequeue();
        }
        _log.Text = string.Join(Environment.NewLine, _logLines);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void OpenResults()
    {
        if (_report is null) return;
        try { Process.Start(new ProcessStartInfo(_report.RunDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("結果フォルダを開けませんでした", ex); }
    }

    private void CopyJsonlPath()
    {
        if (_report?.DiagnosticsFile is not string path) return;
        try { Clipboard.SetText(path); _status.Text = "診断 JSONL のパスをコピーしました。"; }
        catch (Exception ex) { ShowError("パスをコピーできませんでした", ex); }
    }

    private void ShowError(string title, Exception error) =>
        MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static string FormatProgressPrefix(BuildProgress? progress) => progress is null
        ? ""
        : $"完了 {progress.Completed} / {progress.Total} 件・実行中 {progress.Running} 件  ";

    private static string CleanPath(string value) => value.Trim().Trim('"');

    private static string RequireFullPath(string value, string label)
    {
        var path = CleanPath(value);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{label}を指定してください。");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException($"{label}には絶対パスを指定してください。");
        return Path.GetFullPath(path);
    }
}
