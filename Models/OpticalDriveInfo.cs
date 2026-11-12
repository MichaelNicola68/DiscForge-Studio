using System.Windows.Media;

namespace DiscForgeStudio.Models;

public sealed record OpticalDriveInfo(
    string Name,
    string DisplayName,
    bool IsReady,
    string VolumeLabel,
    string FileSystem,
    string MediaType,
    long TotalBytes,
    long FreeBytes,
    bool IsWritable,
    string FreeSpaceDisplay,
    string ProtectionStatus,
    string StatusMessage,
    string StatusLabel,
    Brush StatusBrush);
