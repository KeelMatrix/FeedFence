using System.Runtime.InteropServices;
using System.Text;

namespace KeelMatrix.FeedFence;

// Type inspection must not open a FIFO/device for data. FileAttributes on Unix
// reports these as ordinary files; path resolution alone is insufficient.
internal static class PolicyFileSystemEntry
{
    public static PolicyEntryType GetType(string resolvedPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(resolvedPath);
            if ((attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                return PolicyEntryType.Other;
            return (attributes & FileAttributes.Directory) != 0 ? PolicyEntryType.Directory : PolicyEntryType.RegularFile;
        }

        var architecture = RuntimeInformation.ProcessArchitecture;
        var linux = OperatingSystem.IsLinux() && architecture is Architecture.X64 or Architecture.Arm64 or Architecture.Arm;
        var mac = OperatingSystem.IsMacOS() && architecture is Architecture.X64 or Architecture.Arm64;
        if ((!linux && !mac) || Stat(Encoding.UTF8.GetBytes(resolvedPath + '\0'), out var metadata) != 0)
            throw new AnalysisException("the policy filesystem entry type could not be inspected.");

        var mode = mac ? metadata.DarwinMode : architecture == Architecture.X64 ? metadata.LinuxX64Mode : metadata.LinuxArmMode;
        return (mode & 0xF000) switch
        {
            0x8000 => PolicyEntryType.RegularFile,
            0x4000 => PolicyEntryType.Directory,
            _ => PolicyEntryType.Other
        };
    }

    // Only st_mode is consumed. The buffer covers native stat structures on the
    // selected ABIs, including their unused tail. No private .NET ABI is used.
    // Layout references: glibc sysdeps/unix/sysv/linux/{x86,arm,aarch64}/bits/struct_stat.h;
    // musl arch/{x86_64,aarch64,arm}/bits/stat.h; Apple bsd/sys/stat.h.
    [StructLayout(LayoutKind.Explicit, Size = 512)]
    private struct StatBuffer
    {
        [FieldOffset(4)] public ushort DarwinMode;
        [FieldOffset(16)] public uint LinuxArmMode;
        [FieldOffset(24)] public uint LinuxX64Mode;
    }

    [DllImport("libc", EntryPoint = "stat", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Stat([In] byte[] path, out StatBuffer metadata);
}

internal enum PolicyEntryType { Other, RegularFile, Directory }
