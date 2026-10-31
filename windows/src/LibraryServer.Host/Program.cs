using LibraryServer.Core.Config;
using LibraryServer.Server;

try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch (Exception) { }
var opts = ServerOptions.Parse(args);
if (opts.Command == "covers")
{
    if (opts.Errors.Count > 0)
    {
        foreach (var e in opts.Errors) Console.Error.WriteLine(e);
        return 2;
    }
    return CoverCli.Run(opts);
}
if (opts.Help)
{
    Console.WriteLine(ServerOptions.Usage);
    return 0;
}
if (opts.Errors.Count > 0)
{
    foreach (var e in opts.Errors) Console.Error.WriteLine(e);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ServerOptions.Usage);
    return 2;
}

var store = ConfigStore.Resolve(opts.ConfigPath, opts.Service);
LibraryConfig cfg;
try
{
    cfg = store.LoadOrDefault();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"設定ファイルを読めません / Cannot read {store.Path}: {ex.Message}");
    return 1;
}
opts.ApplyOverrides(cfg);

var app = LibraryHost.Create(store, cfg, opts);
app.Run();
return 0;
