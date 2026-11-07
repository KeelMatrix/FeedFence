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

        var abi = SelectAbi(RuntimeInformation.ProcessArchitecture, OperatingSystem.IsLinux(), OperatingSystem.IsMacOS());
        var path = Encoding.UTF8.GetBytes(resolvedPath + '\0');
        // Literal P/Invoke symbols do not apply Apple's C header aliases. Intel
        // needs stat$INODE64 for the same layout that arm64's plain stat returns.
        var result = SelectStat(abi)(path, out var metadata);
        if (result != 0)
            throw new AnalysisException("the policy filesystem entry type could not be inspected.");

        return DecodeType(metadata, abi);
    }

    internal static PolicyStatAbi SelectAbi(Architecture architecture, bool linux, bool mac) => (architecture, linux, mac) switch
    {
        (Architecture.X64, true, false) => PolicyStatAbi.LinuxX64,
        (Architecture.Arm or Architecture.Arm64, true, false) => PolicyStatAbi.LinuxArm,
        (Architecture.X64, false, true) => PolicyStatAbi.DarwinIntel,
        (Architecture.Arm64, false, true) => PolicyStatAbi.DarwinArm64,
        _ => throw new AnalysisException("the policy filesystem entry type could not be inspected.")
    };

    internal static PolicyEntryType DecodeType(StatBuffer metadata, PolicyStatAbi abi)
    {
        var mode = abi switch
        {
            PolicyStatAbi.DarwinIntel or PolicyStatAbi.DarwinArm64 => metadata.DarwinMode,
            PolicyStatAbi.LinuxX64 => metadata.LinuxX64Mode,
            PolicyStatAbi.LinuxArm => metadata.LinuxArmMode,
            _ => throw new AnalysisException("the policy filesystem entry type could not be inspected.")
        };
        return (mode & 0xF000) switch
        {
            0x8000 => PolicyEntryType.RegularFile,
            0x4000 => PolicyEntryType.Directory,
            _ => PolicyEntryType.Other
        };
    }

    internal delegate int StatReader(byte[] path, out StatBuffer metadata);

    internal static StatReader SelectStat(PolicyStatAbi abi) => abi switch
    {
        PolicyStatAbi.DarwinIntel => StatInode64,
        PolicyStatAbi.DarwinArm64 or PolicyStatAbi.LinuxX64 or PolicyStatAbi.LinuxArm => Stat,
        _ => throw new AnalysisException("the policy filesystem entry type could not be inspected.")
    };

    // Only st_mode is consumed. The buffer covers native stat structures on the
    // selected ABIs, including their unused tail. No private .NET ABI is used.
    // Layout references: glibc sysdeps/unix/sysv/linux/{x86,arm,aarch64}/bits/struct_stat.h;
    // musl arch/{x86_64,arm}/bits/stat.h and arch/generic/bits/stat.h;
    // Apple bsd/sys/stat.h plus libsyscall/Platforms/MacOSX/{x86_64,arm64}/syscall.map.
    [StructLayout(LayoutKind.Explicit, Size = 512)]
    internal struct StatBuffer
    {
        [FieldOffset(4)] public ushort DarwinMode;
        [FieldOffset(16)] public uint LinuxArmMode;
        [FieldOffset(24)] public uint LinuxX64Mode;
    }

    [DllImport("libc", EntryPoint = "stat", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Stat([In] byte[] path, out StatBuffer metadata);

    [DllImport("libc", EntryPoint = "stat$INODE64", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatInode64([In] byte[] path, out StatBuffer metadata);
}

internal enum PolicyEntryType { Other, RegularFile, Directory }
internal enum PolicyStatAbi { LinuxX64, LinuxArm, DarwinIntel, DarwinArm64 }
