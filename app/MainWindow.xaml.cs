using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

using XScribeDesktop.PrWalker;

namespace XScribeDesktop;

public partial class MainWindow : Window
{
    // Adjust if you move the stt/ folder relative to this project.
    private static readonly string SttDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "stt"));
    private const string WhisperModel = "small.en";

    private readonly ObservableCollection<Sentence> _sentences = new();
    private readonly List<Process> _workers = new();
    private int _modelsReady;
    private int _modelsExpected;
    private string _contextMode = "brief"; // "brief" | "full"
    private string _scope = "repo"; // "repo" | "general"
    private string _brief = "";

    public MainWindow()
    {
        InitializeComponent();
        SentenceList.ItemsSource = _sentences;
        TranscriptHub.RepoContext = RepoContextBox.Text;
        // Start PR Walker WebSocket hub immediately so Chrome extension can connect
        WalkerHub.Instance.Start();
        Loaded += async (_, _) => await LoadDevicesAsync();
        Loaded += (_, _) => DetectRepoBtn_Click(this, new RoutedEventArgs());
    }

    private void RepoContextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        TranscriptHub.RepoContext = RepoContextBox.Text;

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            PrWalker.AbortAnimation();
    }

    // ── Repo selection ───────────────────────────────────────────────────────
    private void BrowseRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select repo folder",
        };
        if (dialog.ShowDialog() == true)
        {
            var name = Path.GetFileName(dialog.FolderName.TrimEnd('\\', '/'));
            RepoContextBox.Text = $"{name} — {dialog.FolderName}";
            SendStatusText.Text = "✓ repo set";
        }
    }

    private void DetectRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        var path = DetectCurrentRepoPath();
        if (path == null)
        {
            SendStatusText.Text = "⚠ Could not detect repo — edit manually";
            return;
        }
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        RepoContextBox.Text = $"{name} — {path}";
        SendStatusText.Text = "✓ repo detected";
    }

    /// <summary>
    /// Finds the most recently modified Claude Code Desktop session log under
    /// ~/.claude/projects/**/*.jsonl and pulls its "cwd" field — that's the repo
    /// the currently active CCD session actually has open.
    /// </summary>
    private static string? DetectCurrentRepoPath()
    {
        var projectsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(projectsDir)) return null;

        var latest = Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null) return null;

        string text;
        try { text = File.ReadAllText(latest.FullName); }
        catch { return null; }

        var match = Regex.Match(text, "\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!match.Success) return null;

        try { return JsonSerializer.Deserialize<string>($"\"{match.Groups[1].Value}\""); }
        catch { return null; }
    }

    // ── Devices ──────────────────────────────────────────────────────────────
    private async void RefreshDevicesBtn_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private async Task LoadDevicesAsync()
    {
        var workerScript = Path.Combine(SttDir, "worker.py");
        if (!File.Exists(workerScript)) return;

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{workerScript}\" --list-devices",
            WorkingDirectory = SttDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var json = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var list = JsonSerializer.Deserialize<DeviceList>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list == null) return;

            var prevMic = MicCombo.SelectedValue as int?;
            var prevSpeaker = SpeakerCombo.SelectedValue as int?;

            MicCombo.ItemsSource = list.Mics;
            MicCombo.SelectedIndex = prevMic != null && list.Mics.Any(m => m.Index == prevMic) ? list.Mics.FindIndex(m => m.Index == prevMic) : 0;

            var speakersWithNone = new List<AudioDevice> { new() { Index = -1, Name = "(none — mic only)" } };
            speakersWithNone.AddRange(list.Speakers);
            SpeakerCombo.ItemsSource = speakersWithNone;
            SpeakerCombo.SelectedIndex = prevSpeaker != null && speakersWithNone.Any(s => s.Index == prevSpeaker) ? speakersWithNone.FindIndex(s => s.Index == prevSpeaker) : 0;
        }
        catch (Exception ex)
        {
            SetStatus(false, $"⚠ Device list failed: {ex.Message}");
        }
    }

    // ── Start / Stop ─────────────────────────────────────────────────────────
    // "you" and "them" each run as their own Python process (not just a thread)
    // so mic transcription never waits behind system-audio transcription for
    // the GIL — see the comment in worker.py's main() for why that matters.
    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_workers.Count > 0) return;

        var workerScript = Path.Combine(SttDir, "worker.py");
        if (!File.Exists(workerScript))
        {
            SetStatus(false, $"⚠ worker.py not found at {SttDir}");
            return;
        }

        var micIndex = MicCombo.SelectedValue as int?;
        var speakerIndex = SpeakerCombo.SelectedValue as int?;

        _modelsReady = 0;
        _modelsExpected = 0;

        if (micIndex != null)
            StartOneWorker(workerScript, $"--mic-only --mic-index {micIndex}");
        if (speakerIndex != null && speakerIndex != -1)
            StartOneWorker(workerScript, $"--speaker-only --speaker-index {speakerIndex}");

        if (_workers.Count == 0)
        {
            SetStatus(false, "⚠ No mic or system-audio device selected");
            return;
        }

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        SetStatus(true, "Loading model…");
    }

    private void StartOneWorker(string workerScript, string streamArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{workerScript}\" --model {WhisperModel} {streamArgs}",
            WorkingDirectory = SttDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var proc = Process.Start(psi);
        if (proc == null) return;

        _workers.Add(proc);
        _modelsExpected++;
        _ = Task.Run(() => ReadWorkerOutputAsync(proc));
        _ = Task.Run(() => ReadWorkerErrorAsync(proc));
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        StopWorker();
        SetStatus(false, $"Stopped · {_sentences.Count} sentences");
    }

    private void StopWorker()
    {
        foreach (var proc in _workers)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            proc.Dispose();
        }
        _workers.Clear();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
    }

    private async Task ReadWorkerOutputAsync(Process proc)
    {
        string? line;
        try
        {
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                WorkerMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<WorkerMessage>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { continue; }
                if (msg == null || string.IsNullOrWhiteSpace(msg.Text)) continue;

                Dispatcher.Invoke(() =>
                {
                    var sentence = new Sentence { Speaker = msg.Speaker, Text = msg.Text, Ts = msg.Ts, Index = _sentences.Count };
                    _sentences.Add(sentence);
                    CountText.Text = $"{_sentences.Count} sentence{(_sentences.Count == 1 ? "" : "s")}";
                    SentenceList.ScrollIntoView(_sentences[^1]);
                    TranscriptHub.Publish(sentence);
                });
            }
        }
        catch { /* process likely killed */ }
    }

    private async Task ReadWorkerErrorAsync(Process proc)
    {
        string? line;
        try
        {
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                Debug.WriteLine($"[worker] {line}");
                if (line.Contains("model ready"))
                {
                    Dispatcher.Invoke(() =>
                    {
                        _modelsReady++;
                        if (_modelsReady >= _modelsExpected)
                            SetStatus(true, "Listening…");
                    });
                }
            }
        }
        catch { /* process likely killed */ }
    }

    private void SetStatus(bool live, string text)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(live
            ? System.Windows.Media.Color.FromRgb(0xFF, 0x47, 0x57)
            : System.Windows.Media.Color.FromRgb(0x3A, 0x4A, 0x60));
        StatusText.Foreground = new SolidColorBrush(live
            ? System.Windows.Media.Color.FromRgb(0xC0, 0xD8, 0xF0)
            : System.Windows.Media.Color.FromRgb(0x60, 0x80, 0xA0));
    }

    // ── Mode / brief ─────────────────────────────────────────────────────────
    private void ModeBtn_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        BriefModeBtn.IsChecked = clicked == BriefModeBtn;
        FullModeBtn.IsChecked = clicked == FullModeBtn;
        _contextMode = clicked == BriefModeBtn ? "brief" : "full";
    }

    private void ScopeBtn_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        RepoScopeBtn.IsChecked = clicked == RepoScopeBtn;
        GeneralScopeBtn.IsChecked = clicked == GeneralScopeBtn;
        _scope = clicked == RepoScopeBtn ? "repo" : "general";
    }

    private void SetBriefBtn_Click(object sender, RoutedEventArgs e)
    {
        _brief = BriefBox.Text.Trim();
        TranscriptHub.Brief = _brief;
        SendStatusText.Text = string.IsNullOrEmpty(_brief) ? "brief cleared" : "brief set ✓";
    }

    // ── Save / Clear ─────────────────────────────────────────────────────────
    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sentences.Count == 0)
        {
            SendStatusText.Text = "⚠ nothing to save yet";
            return;
        }

        var ts = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save transcript",
            FileName = $"xscribe-{ts}.txt",
            DefaultExt = ".txt",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"XScribe Desktop Transcript — {DateTime.Now}");
        sb.AppendLine(new string('-', 48));
        foreach (var s in _sentences)
            sb.AppendLine($"[{s.Index + 1:D2}] [{s.Speaker}] {s.Text}{(s.Sent ? "  (sent)" : "")}");

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString());
            SendStatusText.Text = "✓ saved";
        }
        catch (Exception ex)
        {
            SendStatusText.Text = $"⚠ save failed: {ex.Message}";
        }
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        _sentences.Clear();
        CountText.Text = "0 sentences";
    }

    // ── Send ─────────────────────────────────────────────────────────────────
    private void SentenceItem_Pin(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Sentence s })
        {
            s.Pinned = true;
            TranscriptHub.RequestPin(s);
            SendStatusText.Text = "Pinned → PR Walker tab";
            e.Handled = true;
        }
    }

    private void SentenceItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Sentence s })
            _ = SendSentenceAsync(s);
    }

    private async Task SendSentenceAsync(Sentence sentence)
    {
        var prompt = BuildPrompt(sentence);
        SendStatusText.Text = "Sending…";

        var (ok, error) = await DesktopSender.SendAsync(prompt);

        if (ok)
        {
            sentence.Sent = true;
            SendStatusText.Text = "Sent ✓";
        }
        else
        {
            SendStatusText.Text = $"⚠ {error}";
        }
    }

    private const string ResponseInstruction =
        "Respond with ONLY the exact words I should say out loud, nothing else. " +
        "No preamble, no \"let me check\", no describing what you're about to do, no meta-commentary, " +
        "no markdown formatting or link syntax — just the spoken answer itself, starting immediately. " +
        "If you need to look something up first, do it silently and still lead with the answer, not a narration of the lookup. " +
        "If the answer makes a factual claim about the repo (why something works a certain way, where a constraint comes from), " +
        "you MUST have actually located it in the code first and name the specific file(s) by name in the spoken answer " +
        "(e.g. \"vercel.json strips the /login prefix\" not \"there are constraints in the routing config\"). " +
        "Vague claims without a named file are not acceptable — if you can't find it, say you're not sure which file, don't guess. " +
        "These rules apply to every message I send for the rest of this conversation, not just this one — don't make me repeat them.";

    private const string ResponseInstructionReminder = "(Same response rules as established earlier this conversation.)";

    private bool _fullInstructionSent;

    private string BuildPrompt(Sentence sentence)
    {
        var responseInstruction = _fullInstructionSent ? ResponseInstructionReminder : ResponseInstruction;
        _fullInstructionSent = true;

        var scopePart = _scope == "repo"
            ? $"REPO CONTEXT: This call relates to the repo you already have open — {RepoContextBox.Text.Trim()}. " +
              "Use that codebase's context when it's relevant to the question.\n\n"
            : "SCOPE: This question is general and not about the repo you have open — don't force a code-specific answer if it doesn't fit.\n\n";

        if (_contextMode == "brief")
        {
            var windowStart = Math.Max(0, sentence.Index - 2);
            var context = string.Join("\n", _sentences
                .Where(s => s.Index >= windowStart && s.Index <= sentence.Index)
                .Select(s => $"[{s.Speaker}] {s.Text}"));

            var briefPart = string.IsNullOrEmpty(_brief)
                ? "No brief set — responding from recent context only.\n\n"
                : $"CALL BRIEF:\n{_brief}\n\n";

            return $"{scopePart}{briefPart}RECENT CONTEXT:\n{context}\n\nSentence to respond to: \"{sentence.Text}\"\n\n{responseInstruction}";
        }

        var transcript = string.Join("\n", _sentences
            .Where(s => s.Index <= sentence.Index)
            .Select(s => $"[{s.Index + 1:D2}] [{s.Speaker}] {s.Text}"));

        return $"{scopePart}You are helping me respond live during a call. Use the full transcript for context.\n\n--- FULL TRANSCRIPT ---\n{transcript}\n--- END ---\n\nSentence to respond to: \"{sentence.Text}\"\n\n{responseInstruction}";
    }

    protected override void OnClosed(EventArgs e)
    {
        StopWorker();
        PrWalker?.Cleanup();
        WalkerHub.Instance.Dispose();
        base.OnClosed(e);
    }
}
