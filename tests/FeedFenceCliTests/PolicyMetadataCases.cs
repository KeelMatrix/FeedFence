using System.Reflection;
using System.Runtime.InteropServices;

namespace KeelMatrix.FeedFence.CliTests;

internal static class PolicyMetadataCases
{
    public static void Run()
    {
        // Replay representations through the shipping decoder. This supplements
        // the required native source/installed CLI cases; it cannot prove an ABI.
        var pairs = new[] { (PolicyStatAbi.LinuxX64, 24, 8), (PolicyStatAbi.LinuxArm, 16, 12), (PolicyStatAbi.DarwinIntel, 4, 8), (PolicyStatAbi.DarwinArm64, 4, 8) };
        foreach (var (architecture, linux, mac, expected) in new[]
        {
            (Architecture.X64, true, false, PolicyStatAbi.LinuxX64),
            (Architecture.Arm, true, false, PolicyStatAbi.LinuxArm),
            (Architecture.Arm64, true, false, PolicyStatAbi.LinuxArm),
            (Architecture.X64, false, true, PolicyStatAbi.DarwinIntel),
            (Architecture.Arm64, false, true, PolicyStatAbi.DarwinArm64)
        })
            if (PolicyFileSystemEntry.SelectAbi(architecture, linux, mac) != expected)
                throw new InvalidOperationException("native metadata ABI selection failed");
        foreach (var architecture in Enum.GetValues<Architecture>())
            foreach (var linux in new[] { false, true })
                foreach (var mac in new[] { false, true })
                {
                    if (linux != mac && (linux && architecture is Architecture.X64 or Architecture.Arm or Architecture.Arm64
                        || mac && architecture is Architecture.X64 or Architecture.Arm64)) continue;
                    try { PolicyFileSystemEntry.SelectAbi(architecture, linux, mac); }
                    catch (AnalysisException) { continue; }
                    throw new InvalidOperationException("unsupported metadata ABI did not fail closed");
                }
        var modes = new[] { 0x1000, 0x2000, 0x4000, 0x6000, 0x8000, 0xA000, 0xC000 };
        var inodeBits = new[] { 0, 1, 0x3FFF, 0x4000, 0x7FFF, 0x8000, 0x8FFF, 0xC000, 0xFFFF };
        var count = 0;
        foreach (var (abi, offset, inodeOffset) in pairs)
        {
            var import = PolicyFileSystemEntry.SelectStat(abi).Method.GetCustomAttribute<DllImportAttribute>()!;
            var expectedEntryPoint = abi == PolicyStatAbi.DarwinIntel ? "stat$INODE64" : "stat";
            if (import.EntryPoint != expectedEntryPoint || !import.ExactSpelling || import.Value != "libc")
                throw new InvalidOperationException($"{abi} native symbol does not match its replayed layout");
            foreach (var mode in modes)
                foreach (var permission in new[] { 0, 0x1A4, 0xFFF })
                    foreach (var inode in inodeBits)
                    {
                        var bytes = new byte[512];
                        BitConverter.GetBytes((uint)inode).CopyTo(bytes, inodeOffset);
                        BitConverter.GetBytes((uint)(mode | permission)).CopyTo(bytes, offset);
                        var buffer = MemoryMarshal.Read<PolicyFileSystemEntry.StatBuffer>(bytes);
                        var actual = PolicyFileSystemEntry.DecodeType(buffer, abi).ToString();
                        var expected = mode == 0x8000 ? "RegularFile" : mode == 0x4000 ? "Directory" : "Other";
                        if (actual != expected) throw new InvalidOperationException($"{abi} mode/inode replay: expected {expected}, actual {actual}");
                        count++;
                    }
        }
        Console.WriteLine($"PASS: metadata representation replay; {count} independent mode/permission/inode combinations (not native execution)");
    }
}
