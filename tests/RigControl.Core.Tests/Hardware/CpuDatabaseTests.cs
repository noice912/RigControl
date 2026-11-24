using RigControl.Core.Hardware;

namespace RigControl.Core.Tests.Hardware;

public class CpuDatabaseTests
{
    private static CpuGenerationInfo Intel(int model, int stepping = 1, string? brand = null)
        => CpuDatabase.Lookup(new CpuSignature(CpuDatabase.IntelVendorId, 6, model, stepping), brand);

    private static CpuGenerationInfo Amd(int family, int model, string? brand = null)
        => CpuDatabase.Lookup(new CpuSignature(CpuDatabase.AmdVendorId, family, model, 0), brand);

    [Theory]
    [InlineData(0x000B0671u, 6, 0xB7, 1)]    // Core i9-13900K
    [InlineData(0x000906EDu, 6, 0x9E, 13)]   // Core i9-9900K
    [InlineData(0x000206A7u, 6, 0x2A, 7)]    // Core i7-2600K
    [InlineData(0x00A20F12u, 0x19, 0x21, 2)] // Ryzen 5000 (Vermeer)
    [InlineData(0x00A60F12u, 0x19, 0x61, 2)] // Ryzen 7000 (Raphael)
    [InlineData(0x00B40F40u, 0x1A, 0x44, 0)] // Ryzen 9000 (Granite Ridge)
    [InlineData(0x00800F11u, 0x17, 0x01, 1)] // Ryzen 1000 (Summit Ridge)
    public void The_CPUID_number_is_split_into_family_model_and_stepping(uint eax, int family, int model, int stepping)
    {
        var s = CpuSignature.FromCpuid("x", eax);
        Assert.Equal(family, s.Family);
        Assert.Equal(model, s.Model);
        Assert.Equal(stepping, s.Stepping);
    }

    [Fact]
    public void The_description_Windows_keeps_for_a_processor_is_understood()
    {
        Assert.True(CpuSignature.TryParseWindows("GenuineIntel", "Intel64 Family 6 Model 183 Stepping 1", out var intel));
        Assert.Equal(new CpuSignature("GenuineIntel", 6, 0xB7, 1), intel);
        Assert.True(CpuSignature.TryParseWindows("AuthenticAMD", "AMD64 Family 25 Model 33 Stepping 2", out var amd));
        Assert.Equal(new CpuSignature("AuthenticAMD", 0x19, 0x21, 2), amd);
        Assert.False(CpuSignature.TryParseWindows("GenuineIntel", "something else", out _));
        Assert.False(CpuSignature.TryParseWindows(null, "Intel64 Family 6 Model 183 Stepping 1", out _));
    }

    [Fact]
    public void Raptor_Lake_is_13th_or_14th_gen_and_the_name_settles_which()
    {
        var bare = Intel(0xB7);
        Assert.True(bare.Known);
        Assert.Equal("Intel", bare.Vendor);
        Assert.Equal("Raptor Lake", bare.CodeName);
        Assert.Equal("13th or 14th gen Core", bare.Generation);
        Assert.Equal("Desktop", bare.Line);

        var i13 = Intel(0xB7, 1, "13th Gen Intel(R) Core(TM) i9-13900KF");
        Assert.Equal("13th gen Core", i13.Generation);
        Assert.Equal("13th gen Core (Raptor Lake), desktop", i13.Summary);

        Assert.Equal("14th gen Core", Intel(0xB7, 1, "Intel(R) Core(TM) i9-14900K").Generation);
        Assert.Equal("13th gen Core", Intel(0xBF, 2, "13th Gen Intel(R) Core(TM) i5-13400F").Generation);
        Assert.Equal("13th gen Core", Intel(0xBA, 3, "13th Gen Intel(R) Core(TM) i7-1365U").Generation);
    }

    [Fact]
    public void A_model_number_outside_the_range_of_the_entry_is_not_believed()
    {
        // A Raptor Lake chip can never be 9th gen, whatever a name says.
        Assert.Equal("13th or 14th gen Core", Intel(0xB7, 1, "Intel(R) Core(TM) i9-9900K").Generation);
    }

    [Theory]
    [InlineData(0x2A, 7, "2nd gen Core", "Sandy Bridge")]
    [InlineData(0x3A, 9, "3rd gen Core", "Ivy Bridge")]
    [InlineData(0x3C, 3, "4th gen Core", "Haswell")]
    [InlineData(0x47, 1, "5th gen Core", "Broadwell")]
    [InlineData(0x5E, 3, "6th gen Core", "Skylake")]
    [InlineData(0x9E, 9, "7th gen Core", "Kaby Lake")]
    [InlineData(0x9E, 10, "8th gen Core", "Coffee Lake")]
    [InlineData(0x9E, 13, "9th gen Core", "Coffee Lake Refresh")]
    [InlineData(0xA5, 5, "10th gen Core", "Comet Lake")]
    [InlineData(0x7E, 5, "10th gen Core", "Ice Lake")]
    [InlineData(0xA7, 1, "11th gen Core", "Rocket Lake")]
    [InlineData(0x8C, 1, "11th gen Core", "Tiger Lake")]
    [InlineData(0x97, 2, "12th gen Core", "Alder Lake")]
    [InlineData(0x9A, 3, "12th gen Core", "Alder Lake")]
    [InlineData(0xAA, 4, "Core Ultra series 1", "Meteor Lake")]
    [InlineData(0xC6, 2, "Core Ultra series 2", "Arrow Lake")]
    [InlineData(0xBD, 1, "Core Ultra series 2", "Lunar Lake")]
    [InlineData(0xCC, 1, "Core Ultra series 3", "Panther Lake")]
    [InlineData(0x55, 4, "Core X or Xeon Scalable 1st gen", "Skylake-X")]
    [InlineData(0x55, 7, "Core X or Xeon Scalable 2nd gen", "Cascade Lake")]
    [InlineData(0x8F, 8, "Xeon Scalable 4th gen or Xeon W", "Sapphire Rapids")]
    [InlineData(0xCF, 2, "Xeon Scalable 5th gen", "Emerald Rapids")]
    [InlineData(0xBE, 0, "Intel Processor N-series or Core i3 N-series", "Alder Lake-N")]
    public void Intel_generations_resolve(int model, int stepping, string generation, string codeName)
    {
        var cpu = Intel(model, stepping);
        Assert.True(cpu.Known);
        Assert.Equal(generation, cpu.Generation);
        Assert.Equal(codeName, cpu.CodeName);
        Assert.Contains(codeName, cpu.Summary);
    }

    [Fact]
    public void One_Intel_model_number_shared_by_several_generations_is_told_apart_by_stepping_and_name()
    {
        Assert.Equal("Whiskey Lake / Comet Lake", Intel(0x8E, 12).CodeName);
        Assert.Equal("8th or 10th gen Core", Intel(0x8E, 12).Generation);
        Assert.Equal("10th gen Core", Intel(0x8E, 12, "Intel(R) Core(TM) i7-10510U CPU @ 1.80GHz").Generation);
        Assert.Equal("8th gen Core", Intel(0x8E, 12, "Intel(R) Core(TM) i5-8265U CPU @ 1.60GHz").Generation);
        Assert.Equal("9th gen Core", Intel(0x9E, 11, "Intel(R) Core(TM) i3-9100 CPU @ 3.60GHz").Generation);
    }

    [Theory]
    [InlineData(0x17, 0x01, "Zen", "Summit Ridge")]
    [InlineData(0x17, 0x08, "Zen+", "Pinnacle Ridge")]
    [InlineData(0x17, 0x11, "Zen", "Raven Ridge")]
    [InlineData(0x17, 0x18, "Zen+", "Picasso")]
    [InlineData(0x17, 0x31, "Zen 2", "Castle Peak")]
    [InlineData(0x17, 0x60, "Zen 2", "Renoir")]
    [InlineData(0x17, 0x71, "Zen 2", "Matisse")]
    [InlineData(0x19, 0x01, "Zen 3", "Milan")]
    [InlineData(0x19, 0x21, "Zen 3", "Vermeer")]
    [InlineData(0x19, 0x50, "Zen 3", "Cezanne")]
    [InlineData(0x19, 0x44, "Zen 3+", "Rembrandt")]
    [InlineData(0x19, 0x61, "Zen 4", "Raphael")]
    [InlineData(0x19, 0x74, "Zen 4", "Phoenix")]
    [InlineData(0x19, 0x11, "Zen 4", "Genoa")]
    [InlineData(0x19, 0x18, "Zen 4", "Storm Peak")]
    [InlineData(0x19, 0xA0, "Zen 4c", "Bergamo")]
    [InlineData(0x1A, 0x44, "Zen 5", "Granite Ridge")]
    [InlineData(0x1A, 0x24, "Zen 5", "Strix Point")]
    [InlineData(0x1A, 0x02, "Zen 5", "Turin")]
    [InlineData(0x1A, 0x70, "Zen 5", "Strix Halo")]
    public void AMD_Zen_generations_resolve(int family, int model, string generation, string codeName)
    {
        var cpu = Amd(family, model);
        Assert.True(cpu.Known);
        Assert.Equal("AMD", cpu.Vendor);
        Assert.Equal(generation, cpu.Generation);
        Assert.Contains(codeName, cpu.CodeName);
    }

    [Fact]
    public void Ryzen_5000_7000_and_9000_desktop_chips_say_what_they_are_sold_as()
    {
        Assert.Equal("Zen 3 (Vermeer), Ryzen 5000 desktop", Amd(0x19, 0x21, "AMD Ryzen 7 5800X3D 8-Core Processor").Summary);
        Assert.Equal("Ryzen 7000 desktop or HX laptop", Amd(0x19, 0x61).Line);
        Assert.Equal("Ryzen 9000 desktop or HX laptop", Amd(0x1A, 0x44).Line);
    }

    [Fact]
    public void An_AMD_model_between_the_listed_ones_still_gets_its_Zen_generation_but_no_code_name()
    {
        var cpu = Amd(0x19, 0x30);
        Assert.True(cpu.Known);
        Assert.Equal("Zen 3", cpu.Generation);
        Assert.Null(cpu.CodeName);
        Assert.Equal("Zen 3", cpu.Summary);
    }

    [Fact]
    public void An_unknown_processor_shows_its_own_name_and_says_it_is_newer_than_this_version_knows()
    {
        var intel = Intel(0xFE, 0, "Intel(R) Core(TM) Ultra 9 999K");
        Assert.False(intel.Known);
        Assert.Equal("Intel", intel.Vendor);
        Assert.Null(intel.Generation);
        Assert.Equal("Intel(R) Core(TM) Ultra 9 999K: newer than this version knows", intel.Summary);

        var amd = Amd(0x1B, 0x10, "AMD Ryzen 9 11950X");
        Assert.False(amd.Known);
        Assert.EndsWith(CpuDatabase.UnknownNote, amd.Summary);

        Assert.Equal("This processor: newer than this version knows", Intel(0xFE).Summary);
        // Family 6 model 0x55 has no entry for stepping 9.
        Assert.False(Intel(0x55, 9).Known);
    }

    [Fact]
    public void An_AMD_processor_from_before_Zen_is_called_older_not_newer()
    {
        var fx = Amd(0x15, 0x02, "AMD FX(tm)-8350 Eight-Core Processor");
        Assert.False(fx.Known);
        Assert.EndsWith(CpuDatabase.OlderNote, fx.Summary);
    }

    [Theory]
    [InlineData(6, 0x1E, "Intel(R) Core(TM) i7 CPU 860 @ 2.80GHz")]      // Nehalem (Lynnfield)
    [InlineData(6, 0x1A, "Intel(R) Core(TM) i7 CPU 920 @ 2.67GHz")]      // Nehalem
    [InlineData(6, 0x25, "Intel(R) Core(TM) i5 CPU 650 @ 3.20GHz")]      // Westmere, above no listed model
    [InlineData(6, 0x2C, "Intel(R) Xeon(R) CPU X5650 @ 2.67GHz")]        // Westmere-EP: a higher number than Sandy Bridge
    [InlineData(6, 0x17, "Intel(R) Core(TM)2 Duo CPU E8400 @ 3.00GHz")]  // Core 2
    [InlineData(6, 0x37, "Intel(R) Celeron(R) CPU N2840 @ 2.16GHz")]     // Bay Trail Atom
    [InlineData(6, 0x4C, "Intel(R) Atom(TM) x5-Z8350 CPU @ 1.44GHz")]    // Cherry Trail Atom
    [InlineData(15, 4, "Intel(R) Pentium(R) 4 CPU 3.00GHz")]
    [InlineData(5, 4, "Intel(R) Pentium(R) processor")]
    public void An_Intel_processor_from_before_the_list_starts_is_called_older_not_newer(int family, int model, string brand)
    {
        var old = CpuDatabase.Lookup(new CpuSignature(CpuDatabase.IntelVendorId, family, model, 5), brand);
        Assert.False(old.Known);
        Assert.Equal("Intel", old.Vendor);
        Assert.Equal(brand + ": " + CpuDatabase.OlderNote, old.Summary);
    }

    [Fact]
    public void An_Intel_model_that_is_on_neither_list_is_still_called_newer()
    {
        Assert.EndsWith(CpuDatabase.UnknownNote, Intel(0xFE, 0, "Intel(R) Core(TM) Ultra 9 999K").Summary);
        Assert.EndsWith(CpuDatabase.UnknownNote,
            CpuDatabase.Lookup(new CpuSignature(CpuDatabase.IntelVendorId, 19, 1, 0), "Some future Intel processor").Summary);
    }

    [Theory]
    [InlineData(0x97, "12th Gen Intel(R) Core(TM) i9-12900HX", "12th gen Core (Alder Lake), laptop")]
    [InlineData(0xB7, "13th Gen Intel(R) Core(TM) i9-13980HX", "13th gen Core (Raptor Lake), laptop")]
    [InlineData(0xB7, "Intel(R) Core(TM) i9-14900HX", "14th gen Core (Raptor Lake), laptop")]
    [InlineData(0xC6, "Intel(R) Core(TM) Ultra 9 275HX", "Core Ultra series 2 (Arrow Lake), laptop")]
    public void An_HX_laptop_chip_with_a_desktop_model_number_is_called_a_laptop_chip(int model, string brand, string summary)
    {
        var hx = Intel(model, 1, brand);
        Assert.Equal("Laptop", hx.Line);
        Assert.Equal(summary, hx.Summary);
    }

    [Fact]
    public void A_desktop_chip_stays_a_desktop_chip()
    {
        Assert.Equal("Desktop", Intel(0xB7, 1, "13th Gen Intel(R) Core(TM) i9-13900KF").Line);
        Assert.Equal("Desktop", Intel(0x97, 2, "12th Gen Intel(R) Core(TM) i5-12600K").Line);
        Assert.Equal("Desktop", Intel(0xC6, 2, "Intel(R) Core(TM) Ultra 9 285K").Line);
        Assert.False(CpuDatabase.IsHxLaptopName("Intel(R) Core(TM) i7-13700H"));
        Assert.False(CpuDatabase.IsHxLaptopName(null));
    }

    [Fact]
    public void Another_vendor_is_unknown_and_called_Other()
    {
        var cpu = CpuDatabase.Lookup(new CpuSignature("HygonGenuine", 0x18, 0x00, 1), "Hygon C86 7185");
        Assert.False(cpu.Known);
        Assert.Equal("Other", cpu.Vendor);
    }

    [Fact]
    public void The_table_has_a_sane_number_of_entries()
    {
        var (intel, amd) = CpuDatabase.Counts;
        Assert.InRange(intel, 40, 200);
        Assert.InRange(amd, 30, 200);
    }
}
