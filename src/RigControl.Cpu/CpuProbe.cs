using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32;
using RigControl.Core.Hardware;

namespace RigControl.Cpu;

/// <summary>
/// What the processor says about itself. Cores and Threads are 0 when Windows did not say. PerformanceCores and
/// EfficiencyCores are set only on a processor with two kinds of cores.
/// </summary>
public sealed record CpuFacts(string Name, CpuSignature Signature, int Cores, int Threads, int? PerformanceCores, int? EfficiencyCores);

/// <summary>Reads the processor's identity. Behind an interface so tests never depend on the PC they run on.</summary>
public interface ICpuProbe
{
    CpuFacts Read();
}

/// <summary>
/// The real processor: its name and numbers from the CPUID instruction (no driver needed), and the core counts from
/// Windows. Only reads.
/// </summary>
public sealed class WindowsCpuProbe : ICpuProbe
{
    public CpuFacts Read()
    {
        var (name, signature) = X86Base.IsSupported ? FromCpuid() : FromRegistry();
        if (string.IsNullOrWhiteSpace(name)) name = FromRegistry().Name;
        if (string.IsNullOrWhiteSpace(name)) name = "Unknown processor";
        var (cores, threads, performance, efficiency) = CountCores();
        if (threads <= 0) threads = Environment.ProcessorCount;
        return new CpuFacts(name, signature, cores, threads, performance, efficiency);
    }

    private static (string Name, CpuSignature Signature) FromCpuid()
    {
        var leaf0 = X86Base.CpuId(0, 0);
        Span<byte> vendor = stackalloc byte[12];
        // The vendor text is spread over EBX, EDX, ECX, in that order.
        BinaryPrimitives.WriteInt32LittleEndian(vendor, leaf0.Ebx);
        BinaryPrimitives.WriteInt32LittleEndian(vendor[4..], leaf0.Edx);
        BinaryPrimitives.WriteInt32LittleEndian(vendor[8..], leaf0.Ecx);
        var vendorId = Encoding.ASCII.GetString(vendor).TrimEnd('\0', ' ');
        var signature = CpuSignature.FromCpuid(vendorId, (uint)X86Base.CpuId(1, 0).Eax);

        var name = "";
        if ((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax >= 0x80000004)
        {
            Span<byte> brand = stackalloc byte[48];
            for (int i = 0; i < 3; i++)
            {
                var r = X86Base.CpuId(unchecked((int)0x80000002) + i, 0);
                BinaryPrimitives.WriteInt32LittleEndian(brand[(i * 16)..], r.Eax);
                BinaryPrimitives.WriteInt32LittleEndian(brand[(i * 16 + 4)..], r.Ebx);
                BinaryPrimitives.WriteInt32LittleEndian(brand[(i * 16 + 8)..], r.Ecx);
                BinaryPrimitives.WriteInt32LittleEndian(brand[(i * 16 + 12)..], r.Edx);
            }
            name = CleanName(Encoding.ASCII.GetString(brand));
        }
        return (name, signature);
    }

    /// <summary>Cuts the text at its first zero byte and removes the padding some processors put around their name.</summary>
    internal static string CleanName(string raw)
    {
        var end = raw.IndexOf('\0');
        if (end >= 0) raw = raw[..end];
        return string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>For a PC without CPUID (Windows on Arm): the name and numbers Windows keeps.</summary>
    private static (string Name, CpuSignature Signature) FromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = CleanName(key?.GetValue("ProcessorNameString") as string ?? "");
            var vendor = key?.GetValue("VendorIdentifier") as string;
            CpuSignature.TryParseWindows(vendor, key?.GetValue("Identifier") as string, out var signature);
            if (signature.VendorId is null) signature = new CpuSignature(vendor?.Trim() ?? "", 0, 0, 0);
            return (name, signature);
        }
        catch
        {
            return ("", new CpuSignature("", 0, 0, 0));
        }
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, [Out] byte[]? buffer, ref uint returnedLength);

    private static (int Cores, int Threads, int? Performance, int? Efficiency) CountCores()
    {
        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
            if (length == 0) return (0, 0, null, null);
            var buffer = new byte[length];
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return (0, 0, null, null);
            return ParseCores(buffer.AsSpan(0, (int)length));
        }
        catch
        {
            return (0, 0, null, null);
        }
    }

    /// <summary>
    /// Counts cores and threads in the list Windows gives (one record per core). A higher "efficiency class" means a
    /// faster core, so with two classes the higher one is the performance cores.
    /// </summary>
    internal static (int Cores, int Threads, int? Performance, int? Efficiency) ParseCores(ReadOnlySpan<byte> data)
    {
        int cores = 0, threads = 0;
        var perClass = new SortedDictionary<int, int>();
        int at = 0;
        while (at + 8 <= data.Length)
        {
            int relationship = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
            if (size < 8 || at + size > data.Length) break;
            // Record: flags (1), efficiency class (1), reserved (20), group count (2), then 16 bytes per group
            // starting with the 8-byte mask of that core's threads.
            if (relationship == RelationProcessorCore && size >= 8 + 24)
            {
                cores++;
                int efficiencyClass = data[at + 9];
                perClass[efficiencyClass] = perClass.GetValueOrDefault(efficiencyClass) + 1;
                int groups = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 30)..]);
                for (int g = 0; g < groups && at + 32 + g * 16 + 8 <= at + size; g++)
                    threads += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(data[(at + 32 + g * 16)..]));
            }
            at += size;
        }
        if (perClass.Count != 2) return (cores, threads, null, null);
        return (cores, threads, perClass.Last().Value, perClass.First().Value);
    }
}
