using RigControl.Core.Hardware;

namespace RigControl.Core.Tests.Hardware;

public class GpuDatabaseTests
{
    [Theory]
    // NVIDIA, several generations
    [InlineData(0x10DE, 0x2782, "NVIDIA GeForce RTX 4070 Ti", "GeForce RTX 40 series", "Ada Lovelace")]
    [InlineData(0x10DE, 0x2684, "NVIDIA GeForce RTX 4090", "GeForce RTX 40 series", "Ada Lovelace")]
    [InlineData(0x10DE, 0x2B85, "NVIDIA GeForce RTX 5090", "GeForce RTX 50 series", "Blackwell")]
    [InlineData(0x10DE, 0x2204, "NVIDIA GeForce RTX 3090", "GeForce RTX 30 series", "Ampere")]
    [InlineData(0x10DE, 0x1B80, "NVIDIA GeForce GTX 1080", "GeForce GTX 10 series", "Pascal")]
    [InlineData(0x10DE, 0x13C2, "NVIDIA GeForce GTX 970", "GeForce 900 series", "Maxwell")]
    [InlineData(0x10DE, 0x0640, "NVIDIA GeForce 9500 GT", "GeForce 9 series", null)]
    // AMD
    [InlineData(0x1002, 0x7550, "AMD Radeon RX 9070/9070 XT/9070 GRE", "Radeon RX 9000 series", "RDNA 4")]
    [InlineData(0x1002, 0x744C, "AMD Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M", "Radeon RX 7000 series", "RDNA 3")]
    [InlineData(0x1002, 0x73BF, "AMD Radeon RX 6800/6800 XT / 6900 XT", "Radeon RX 6000 series", "RDNA 2")]
    [InlineData(0x1002, 0x731F, "AMD Radeon RX 5600 OEM/5600 XT / 5700/5700 XT", "Radeon RX 5000 series", "RDNA")]
    [InlineData(0x1002, 0x15BF, "AMD Radeon 740M / 760M / 780M Graphics", "Radeon integrated graphics", null)]
    // Intel
    [InlineData(0x8086, 0x56A0, "Intel Arc A770", "Arc A-series", "Alchemist")]
    [InlineData(0x8086, 0xE20B, "Intel Arc B580", "Arc B-series", "Battlemage")]
    [InlineData(0x8086, 0xA780, "Intel UHD Graphics 770", "UHD Graphics", null)]
    [InlineData(0x8086, 0x3E92, "Intel UHD Graphics 630", "UHD Graphics", null)]
    [InlineData(0x8086, 0x7D55, "Intel Arc Graphics", "Arc integrated graphics", null)]
    public void Known_cards_of_several_generations_resolve(int vendor, int device, string name, string family, string? architecture)
    {
        var gpu = GpuDatabase.Lookup(vendor, device, "name from Windows");
        Assert.True(gpu.Known);
        Assert.Equal(name, gpu.Name);
        Assert.Equal(family, gpu.Family);
        Assert.Equal(architecture, gpu.Architecture);
        Assert.Equal(GpuDatabase.VendorName(vendor), gpu.Vendor);
        Assert.Equal(vendor, gpu.VendorId);
        Assert.Equal(device, gpu.DeviceId);
    }

    [Fact]
    public void A_Windows_device_id_finds_the_RTX_4070_Ti()
    {
        var gpu = GpuDatabase.Lookup(@"PCI\VEN_10DE&DEV_2782&SUBSYS_00000000&REV_A1\4&00000000&0&0008", "NVIDIA GeForce RTX 4070 Ti");
        Assert.True(gpu.Known);
        Assert.Equal("NVIDIA", gpu.Vendor);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti", gpu.Name);
        Assert.Equal("AD104", gpu.Chip);
        Assert.Equal("GeForce RTX 40 series", gpu.Family);
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2782&SUBSYS_00000000&REV_A1", 0x10DE, 0x2782)]
    [InlineData(@"pci\ven_1002&dev_744c&subsys_00000000", 0x1002, 0x744C)]
    [InlineData("VEN_8086&DEV_A780", 0x8086, 0xA780)]
    public void Device_ids_are_read_whatever_their_case(string id, int vendor, int device)
    {
        Assert.True(GpuDatabase.TryParsePnpId(id, out var v, out var d));
        Assert.Equal(vendor, v);
        Assert.Equal(device, d);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"ROOT\BasicDisplay\0000")]
    [InlineData(@"USB\VID_1E71&PID_300C")]
    public void A_device_id_without_PCI_ids_is_refused_and_falls_back(string? id)
    {
        Assert.False(GpuDatabase.TryParsePnpId(id, out _, out _));
        var gpu = GpuDatabase.Lookup(id, "Microsoft Basic Display Adapter");
        Assert.False(gpu.Known);
        Assert.Equal("Unknown", gpu.Vendor);
        Assert.Equal("Microsoft Basic Display Adapter", gpu.Name);
        Assert.Equal("Unknown graphics card", GpuDatabase.Lookup(id).Name);
    }

    [Fact]
    public void An_unknown_device_falls_back_to_the_name_Windows_gives()
    {
        var gpu = GpuDatabase.Lookup(0x10DE, 0xFFFE, "NVIDIA GeForce RTX 9090");
        Assert.False(gpu.Known);
        Assert.Equal("NVIDIA", gpu.Vendor);
        Assert.Equal("NVIDIA GeForce RTX 9090", gpu.Name);
        Assert.Null(gpu.Family);
        Assert.Null(gpu.Chip);

        Assert.Equal("Unknown graphics card (10DE:FFFE)", GpuDatabase.Lookup(0x10DE, 0xFFFE).Name);
        Assert.Equal("Unknown", GpuDatabase.Lookup(0x1234, 0x1111, "QEMU display").Vendor);
    }

    [Fact]
    public void An_entry_with_only_a_chip_name_shows_the_name_Windows_gives()
    {
        // 10DE:1C00 is listed as "GP106" with no product name.
        var gpu = GpuDatabase.Lookup(0x10DE, 0x1C00, "NVIDIA GeForce GTX 1060");
        Assert.True(gpu.Known);
        Assert.Equal("NVIDIA GeForce GTX 1060", gpu.Name);
        Assert.Equal("GP106", gpu.Chip);
        Assert.Equal("Pascal", gpu.Architecture);
        Assert.Equal("NVIDIA GP106", GpuDatabase.Lookup(0x10DE, 0x1C00).Name);
    }

    [Fact]
    public void Devices_that_are_not_graphics_cards_are_left_out()
    {
        Assert.False(GpuDatabase.Lookup(0x10DE, 0x22BC).Known); // NVIDIA HDMI sound of the RTX 4070 Ti
        Assert.False(GpuDatabase.Lookup(0x1002, 0xAB28).Known); // AMD HDMI sound
        Assert.False(GpuDatabase.Lookup(0x8086, 0x7A84).Known); // Intel chipset (Z690 LPC)
        Assert.False(GpuDatabase.Lookup(0x8086, 0xA703).Known); // Intel Raptor Lake host bridge
    }

    [Fact]
    public void The_table_is_read_once_and_its_counts_are_sane()
    {
        var info = GpuDatabase.Info;
        GpuDatabase.Lookup(0x10DE, 0x2782);
        GpuDatabase.Lookup(0x1002, 0x744C);
        Assert.Same(info, GpuDatabase.Info);
        Assert.Equal(1, GpuDatabase.LoadCount);

        Assert.InRange(info.Nvidia, 1000, 4000);
        Assert.InRange(info.Amd, 500, 3000);
        Assert.InRange(info.Intel, 250, 2000);
        Assert.Equal(info.Devices, info.Nvidia + info.Amd + info.Intel);
    }

    [Fact]
    public void The_table_says_which_snapshot_it_was_made_from()
    {
        var info = GpuDatabase.Info;
        Assert.Matches(@"^\d{4}\.\d{2}\.\d{2}$", info.Version);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}", info.Date);
        Assert.Matches("^[0-9a-f]{64}$", info.Sha256);
    }
}
