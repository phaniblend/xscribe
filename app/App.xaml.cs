using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace XScribeDesktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                System.Windows.MessageBox.Show(args.Exception.Message, "XScribe error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                System.Windows.MessageBox.Show(ex?.Message ?? "Unknown fatal error", "XScribe fatal",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };
    }
}
