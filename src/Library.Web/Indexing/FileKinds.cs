namespace Library.Web.Indexing;

/// <summary>Coarse file categories, stored in the index and used to pick cards, icons and content types.</summary>
public static class FileKinds
{
    public const string ThreeMf = "3mf";
    public const string Stl = "stl";
    public const string Image = "image";
    public const string Pdf = "pdf";
    public const string Archive = "archive";
    public const string Other = "other";

    public static string FromName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".3mf" => ThreeMf,
        ".stl" => Stl,
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => Image,
        ".pdf" => Pdf,
        ".zip" or ".7z" or ".rar" => Archive,
        _ => Other,
    };

    public static bool IsModel(string kind) => kind is ThreeMf or Stl;

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".3mf" => "model/3mf",
        ".stl" => "model/stl",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
