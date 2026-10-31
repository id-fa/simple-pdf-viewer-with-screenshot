namespace LibraryServer.Core;

/// <summary>library.php の lib_mime / lib_image_mime と同じ表</summary>
public static class Mime
{
    public static string ForBookExt(string ext) => ext switch
    {
        "pdf" => "application/pdf",
        "epub" => "application/epub+zip",
        "cbz" => "application/vnd.comicbook+zip",
        "cbr" => "application/vnd.comicbook-rar",
        "zip" => "application/zip",
        "rar" => "application/vnd.rar",
        "7z" or "cb7" => "application/x-7z-compressed",
        _ => "application/octet-stream",
    };

    public static string ForImageExt(string ext) => ext switch
    {
        "webp" => "image/webp",
        "avif" => "image/avif",
        "png" => "image/png",
        "gif" => "image/gif",
        _ => "image/jpeg",
    };
}
