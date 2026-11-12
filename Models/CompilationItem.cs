namespace DiscForgeStudio.Models;

public sealed record CompilationItem(string FullPath, string RelativePath, string Type, long Size)
{
    public string SizeDisplay => Size >= 1_000_000_000 ? $"{Size / 1_000_000_000d:0.00} GB" : Size >= 1_000_000 ? $"{Size / 1_000_000d:0.0} MB" : $"{Size / 1_000d:0.0} KB";
}
