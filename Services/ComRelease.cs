using System;
using System.Runtime.InteropServices;

namespace DiscForgeStudio.Services;

internal static class ComRelease
{
    public static void Final(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }
}
