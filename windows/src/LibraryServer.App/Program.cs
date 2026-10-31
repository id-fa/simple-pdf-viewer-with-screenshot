using LibraryServer.Server;

namespace LibraryServer.App;

internal static class Program
{
    private const string MutexName = @"Local\LibraryServer.App.SingleInstance";
    public const string ShowEventName = @"Local\LibraryServer.App.Show";
    public static string OpenRequestPath => Path.Combine(Path.GetDirectoryName(AppLog.Path)!, "open-request.txt");

    [STAThread]
    private static int Main(string[] args)
    {
        var opts = ServerOptions.Parse(args);
        if (opts.Command == "covers")
        {
            // トレイアプリからでも表紙 CLI は使える (コンソールが無いので出力は親コンソールがある場合のみ)
            return CoverCli.Run(opts);
        }
        if (opts.Help || opts.Errors.Count > 0)
        {
            MessageBox.Show((opts.Errors.Count > 0 ? string.Join("\n", opts.Errors) + "\n\n" : "") + ServerOptions.Usage
                            + "\n\nLibraryServer.exe [--minimized] [--config=PATH] [--port=N] [--bind=lan|localhost] [--root=DIR]",
                TrayApp.AppTitle, MessageBoxButtons.OK, opts.Errors.Count > 0 ? MessageBoxIcon.Error : MessageBoxIcon.Information);
            return opts.Errors.Count > 0 ? 2 : 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 既に起動している → 開きたいファイルを書き残してから、そちらのウィンドウを出して終わる
            try
            {
                if (opts.Positional.Count > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OpenRequestPath)!);
                    File.WriteAllLines(OpenRequestPath, opts.Positional.Select(Path.GetFullPath));
                }
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Write("Unhandled (UI thread)", e.Exception);
            MessageBox.Show(e.Exception.ToString(), TrayApp.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled (domain)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Write("Unobserved task exception", e.Exception); e.SetObserved(); };
        using var ctx = new TrayApp(opts);
        Application.Run(ctx);
        return 0;
    }
}
