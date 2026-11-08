using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WpfListBox = System.Windows.Controls.ListBox;

namespace XScribeDesktop.PrWalker;

public partial class PrWalkerView : System.Windows.Controls.UserControl
{
    private readonly WalkerSession _session = new();
    private readonly CodeLocationResolver _resolver = new();
    private readonly ObservableCollection<WalkerStep> _guideSteps = new();
    private readonly ObservableCollection<WalkerStep> _callSteps = new();
    private readonly List<Sentence> _transcriptBuffer = new();
    private CancellationTokenSource? _debounceCts;
    private bool _hubStarted;

    public PrWalkerView()
    {
        InitializeComponent();
        GuideStepsList.ItemsSource = _guideSteps;
        CallStepsList.ItemsSource = _callSteps;

        Loaded += (_, _) =>
        {
            EnsureHub();
            _ = WaitForExtensionAsync();
            TranscriptHub.OnSentence += OnTranscriptSentence;
            TranscriptHub.OnPinRequested += OnPinRequested;
            _resolver.ApiKey = AppSettings.AnthropicApiKey;
        };
        Unloaded += (_, _) =>
        {
            TranscriptHub.OnSentence -= OnTranscriptSentence;
            TranscriptHub.OnPinRequested -= OnPinRequested;
        };
    }

    void EnsureHub()
    {
        if (_hubStarted) return;
        WalkerHub.Instance.Start();
        WalkerHub.Instance.ExtensionConnectionChanged += OnExtChanged;
        _hubStarted = true;
        UpdateStatus();
    }

    void OnExtChanged(bool connected) =>
        Dispatcher.BeginInvoke(() => UpdateStatus());

    void UpdateStatus()
    {
        var ext = WalkerHub.Instance.ExtensionConnected
            ? "tab helper linked ✓"
            : "tab helper waiting — open puzzle menu → Tab Sync Helper → Refresh link";
        var tab = _session.BoundTabId != null ? $"tab #{_session.BoundTabId}" : "no tab";
        StatusText.Text = $"{ext} · {tab}" +
            (_session.LastError != null ? $" · ⚠ {_session.LastError}" : "");
    }

    async Task WaitForExtensionAsync()
    {
        if (WalkerHub.Instance.ExtensionConnected) return;
        StatusText.Text = "Waiting for Tab Sync Helper…";
        for (var i = 0; i < 20 && !WalkerHub.Instance.ExtensionConnected; i++)
            await Task.Delay(500);
        UpdateStatus();
    }

    private static readonly string ExtensionPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "extension", "pr-walker"));

    private async void ChooseTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureHub();
            StatusText.Text = "Checking tab helper link…";

            // Brief quiet wait — extension may still be reconnecting after reload
            for (var i = 0; i < 12 && !WalkerHub.Instance.ExtensionConnected; i++)
                await Task.Delay(250);

            if (!WalkerHub.Instance.ExtensionConnected)
            {
                var msg =
                    "Tab Sync Helper is not linked to this XScribe window yet.\n\n" +
                    "Quick fix (try this first):\n" +
                    "1. Keep XScribe open\n" +
                    "2. Click the Tab Sync Helper icon in Chrome\n" +
                    "3. Click Refresh link (should say Linked to local app)\n" +
                    "4. Click OK here, then Choose PR tab again\n\n" +
                    "Only if that fails — load unpacked from:\n" +
                    $"{ExtensionPath}";

                System.Windows.MessageBox.Show(msg, "Link Tab Sync Helper",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                StatusText.Text = "Waiting for Tab Sync Helper… click Refresh link in Chrome";
                for (var i = 0; i < 20 && !WalkerHub.Instance.ExtensionConnected; i++)
                    await Task.Delay(500);

                UpdateStatus();
                if (!WalkerHub.Instance.ExtensionConnected) return;
            }

            var dlg = new TabPickerDialog(_session) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Selected == null) return;

            if (await _session.BindTabAsync(dlg.Selected.Id))
            {
                var title = dlg.Selected.Title ?? "PR tab";
                BoundTabText.Text = title.Length > 40 ? title[..40] + "…" : title;
            }
            else
            {
                System.Windows.MessageBox.Show(_session.LastError ?? "Bind failed", "PR Walker");
            }

            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"⚠ {ex.Message}";
            System.Windows.MessageBox.Show(ex.Message, "Choose PR tab failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadGuide_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load PR review guide",
            Filter = "HTML guide (*.html)|*.html|All (*.*)|*.*",
            InitialDirectory = @"E:\SL\guide",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _guideSteps.Clear();
            foreach (var s in GuideParser.ParseFromHtml(dlg.FileName))
                _guideSteps.Add(s);
            StatusText.Text = $"Loaded {_guideSteps.Count} guide steps from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Guide load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RunSelected_Click(object sender, RoutedEventArgs e)
    {
        var list = GuideStepsList.SelectedItem != null ? GuideStepsList
            : CallStepsList.SelectedItem != null ? CallStepsList
            : GuideStepsList;
        await RunStepAsync(list.SelectedItem as WalkerStep);
    }

    private async void RunStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: WalkerStep step })
            await RunStepAsync(step);
    }

    private async Task RunStepAsync(WalkerStep? step)
    {
        try
        {
            if (step == null)
            {
                StatusText.Text = "Select a step first, then click Run selected ▶";
                return;
            }
            if (!step.Navigable)
            {
                StatusText.Text = $"Step not navigable — path='{step.Path}' lines={step.LineRanges.Count} status={step.Status}";
                return;
            }
            if (_session.BoundTabId == null)
            {
                System.Windows.MessageBox.Show(
                    "Bind a PR tab first:\n1. Choose PR tab\n2. Pick the row\n3. Bind tab",
                    "PR Walker", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            StatusText.Text = $"Running on GitHub: {step.FileName} {step.LinesText}…";

            bool ok;
            try
            {
                ok = await _session.RunStepAsync(step);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "⚠ Step timed out — is the PR Files tab still open?";
                return;
            }

            // Stay on UI thread; avoid UpdateStatus wiping the result / racing bindings
            if (ok)
            {
                try { step.Visited = true; } catch { /* ignore */ }
                StatusText.Text = $"✓ {step.FileName} {step.LinesText} — check your PR tab";
            }
            else
            {
                StatusText.Text = $"⚠ {_session.LastError}";
            }
        }
        catch (Exception ex)
        {
            try
            {
                StatusText.Text = $"⚠ {ex.Message}";
            }
            catch { /* ignore */ }
        }
    }

    public async void AbortAnimation()
    {
        try { await _session.AbortStepAsync(); } catch { /* ignore */ }
    }

    private void Abort_Click(object sender, RoutedEventArgs e) => AbortAnimation();

    void OnTranscriptSentence(Sentence s)
    {
        Dispatcher.Invoke(() =>
        {
            _transcriptBuffer.Add(s);
            if (_transcriptBuffer.Count > 50) _transcriptBuffer.RemoveAt(0);
        });

        if (!IntentDetector.IsLocationIntent(s.Text)) return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = DebouncedResolveAsync(s, token);
    }

    async Task DebouncedResolveAsync(Sentence trigger, CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct);
            await Dispatcher.InvokeAsync(() => StartResolve(trigger));
        }
        catch (TaskCanceledException) { }
    }

    void OnPinRequested(Sentence s) =>
        Dispatcher.Invoke(() => StartResolve(s));

    void StartResolve(Sentence trigger)
    {
        var quote = trigger.Text.Trim();
        if (IsDuplicateRecent(quote)) return;

        var step = new WalkerStep
        {
            Source = WalkerStepSource.Call,
            Title = "From call",
            Label = "resolving…",
            TriggerQuote = quote,
            Status = WalkerStepStatus.Resolving,
        };
        _callSteps.Add(step);
        _ = ResolveCallStepAsync(step, quote);
    }

    bool IsDuplicateRecent(string quote)
    {
        var recent = _callSteps.TakeLast(5).ToList();
        return recent.Any(s => s.TriggerQuote != null &&
            string.Equals(s.TriggerQuote, quote, StringComparison.OrdinalIgnoreCase));
    }

    async Task ResolveCallStepAsync(WalkerStep step, string quote)
    {
        try
        {
            IReadOnlyList<string> prFiles = Array.Empty<string>();
            if (_session.BoundTabId != null)
            {
                try { prFiles = await _session.GetPrFileListAsync(); } catch { /* ignore */ }
            }

            var repoRoot = TranscriptHub.ExtractRepoPath(TranscriptHub.RepoContext);
            await _resolver.ResolveCallStepAsync(
                step, quote, _transcriptBuffer, repoRoot, prFiles);
        }
        catch (Exception ex)
        {
            step.Status = WalkerStepStatus.Failed;
            step.Label = ex.Message;
        }
        Dispatcher.Invoke(UpdateStatus);
    }

    public void Cleanup()
    {
        TranscriptHub.OnSentence -= OnTranscriptSentence;
        TranscriptHub.OnPinRequested -= OnPinRequested;
        if (_hubStarted)
            WalkerHub.Instance.ExtensionConnectionChanged -= OnExtChanged;
    }
}
