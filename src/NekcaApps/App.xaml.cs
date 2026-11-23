using System.Windows;
using System.Windows.Threading;

namespace NekcaApps;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Something went wrong: " + args.Exception.Message, "NEKCA Apps", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
