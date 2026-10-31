using LibraryServer.Core.Config;

namespace LibraryServer.App;

/// <summary>
/// トレイアプリにはコンソールが無いので、起動・終了・ウィンドウ・WebView2 の要所だけを
/// %LocalAppData%\LibraryServer\app.log に追記する (1MB を超えたら作り直す)。
/// </summary>
internal static class AppLog
{
    private static readonly object Lock = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path == null)
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ConfigStore.AppFolderName);
                _path = System.IO.Path.Combine(dir, "app.log");
            }
            return _path;
        }
    }

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{(ex != null ? Environment.NewLine + ex : "")}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
