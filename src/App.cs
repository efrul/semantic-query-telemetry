using System;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

static class App
{
    public const string Name = "שליחת נתוני חיפוש — זית ואוצריא";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest")
            return SelfTest.Run(args[1]);
        if (args.Length >= 1 && args[0] == "--auto")
            return Service.RunAutoOnce() ? 0 : 1;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(e.Exception.Message, Name, MessageBoxButton.OK, MessageBoxImage.Error,
                MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            e.Handled = true;
        };
        return app.Run(new MainWindow());
    }
}
