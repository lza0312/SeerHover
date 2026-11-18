namespace SeerHover;

/// <summary>
/// 把扩展名归到几个大类，用于「按类别设定尺寸策略」。
/// 类别 key 同时是 config 里 Geometry.CategoryModes 的键和设置页的行，
/// 新类别只需要在这里登记扩展名 + Label。
/// </summary>
internal static class MediaKind
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Web = "web";
    public const string Text = "text";
    public const string Archive = "archive";
    public const string Document = "document";
    public const string Folder = "folder";
    public const string Other = "other";

    /// <summary>设置页 / 文档用的展示顺序（文件夹在「其他」上面，Other 永远兜底）。</summary>
    public static readonly string[] Categories =
    {
        Image, Video, Audio, Web, Text, Archive, Document, Folder, Other
    };

    private static readonly Dictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string[] image =
        {
            "jpg", "jpeg", "jfif", "png", "gif", "bmp", "webp", "avif", "heic", "heif",
            "tif", "tiff", "ico", "svg", "psd", "ai", "cr2", "cr3", "nef", "arw",
            "dng", "raf", "orf", "rw2", "pef", "srw", "emf", "wmf"
        };
        string[] audio =
        {
            "mp3", "wav", "flac", "aac", "ogg", "oga", "m4a", "wma", "ape", "opus",
            "dsf", "dff", "mid", "midi", "amr", "ac3", "dts"
        };
        string[] web = { "html", "htm", "mhtml", "mht", "url", "svgz" };
        string[] text =
        {
            "txt", "md", "markdown", "log", "ini", "cfg", "conf", "json", "xml", "yaml", "yml",
            "toml", "csv", "tsv", "srt", "ass", "ssa", "nfo", "reg", "sql",
            "cs", "py", "js", "ts", "jsx", "tsx", "java", "kt", "go", "rs", "rb", "php",
            "c", "h", "cpp", "hpp", "cc", "swift", "m", "lua", "pl", "sh", "bat", "cmd", "ps1",
            "vb", "asm", "r", "gradle", "properties", "gitignore", "editorconfig"
        };
        string[] archive = { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "cab", "iso", "jar" };
        string[] document =
        {
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp",
            "epub", "mobi", "azw3", "djvu", "xps", "oxps", "rtf"
        };

        foreach (var e in image) map[e] = Image;
        // VideoExt 的键带点（".mp4"），统一去掉再入表：Of() 查表用无点扩展名，
        // 带点键永远查不到 → 视频全部落到 Other，被 Other 的固定尺寸按竖屏显示（黑边 bug 根因）。
        foreach (var e in MediaSize.VideoExt) map[e.TrimStart('.')] = Video;
        foreach (var e in audio) map[e] = Audio;
        foreach (var e in web) map[e] = Web;
        foreach (var e in text) map[e] = Text;
        foreach (var e in archive) map[e] = Archive;
        foreach (var e in document) map[e] = Document;
        return map;
    }

    /// <summary>扩展名 → 类别 key；目录归 Folder，未登记的归 Other。</summary>
    public static string Of(string path)
    {
        if (!string.IsNullOrEmpty(path) && System.IO.Directory.Exists(path)) return Folder;
        string ext = System.IO.Path.GetExtension(path).TrimStart('.');
        return ext.Length > 0 && Map.TryGetValue(ext, out var kind) ? kind : Other;
    }

    public static string Label(string key) => key switch
    {
        Image => "图片",
        Video => "视频",
        Audio => "音频",
        Web => "网页",
        Text => "文本",
        Archive => "压缩包",
        Document => "文档",
        Folder => "文件夹",
        _ => "其他"
    };
}
