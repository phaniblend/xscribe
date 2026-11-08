using System.Windows;
using System.Windows.Threading;

namespace XScribeDesktop.PrWalker;

public partial class TabPickerDialog : Window
{
    private readonly WalkerSession _session;
    public BrowserTabInfo? Selected { get; private set; }

    public TabPickerDialog(WalkerSession session)
    {
        InitializeComponent();
        _session = session;
        Loaded += async (_, _) =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Tab list failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        TabList.MouseDoubleClick += (_, _) => Ok_Click(TabList, new RoutedEventArgs());
    }

    static string NormalizePrUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url.ToLowerInvariant();
        return $"{u.Scheme}://{u.Host}{u.AbsolutePath}".TrimEnd('/').ToLowerInvariant();
    }

    static bool IsPrFilesTab(BrowserTabInfo t)
    {
        if (string.IsNullOrEmpty(t.Url)) return false;
        var url = t.Url;
        if (!url.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!url.Contains("/pull/", StringComparison.OrdinalIgnoreCase)) return false;
        return url.Contains("/files", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/changes", StringComparison.OrdinalIgnoreCase);
    }

    async Task RefreshAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var tabs = await _session.ListTabsAsync(cts.Token);
            var prTabs = tabs
                .Where(IsPrFilesTab)
                .OrderByDescending(t => t.Active)
                .ThenBy(t => t.Title ?? "")
                .ToList();

            var deduped = prTabs
                .GroupBy(t => NormalizePrUrl(t.Url))
                .Select(g => g.First())
                .ToList();

            await Dispatcher.InvokeAsync(() =>
            {
                TabList.ItemsSource = deduped;
                if (deduped.Count == 1)
                {
                    TabList.SelectedIndex = 0;
                    if (HintText != null)
                        HintText.Text = "One PR tab found — click Bind tab (or double-click the row).";
                }
                else if (deduped.Count == 0)
                {
                    if (HintText != null)
                        HintText.Text = "No GitHub PR Files/Changes tabs open. Open the PR in Chrome first, then Refresh.";
                }
                else if (HintText != null)
                {
                    HintText.Text = $"{deduped.Count} PR tabs — pick one, then Bind tab.";
                }
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (HintText != null)
                    HintText.Text = "Timed out talking to Chrome extension — click Refresh.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(ex.Message, "Tab list failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Tab list failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Selected = TabList.SelectedItem as BrowserTabInfo;
        if (Selected == null)
        {
            System.Windows.MessageBox.Show("Pick a row first, then click Bind tab.", "PR Walker",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
