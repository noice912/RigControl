using AudioController.Core.Devices;
using Xunit;

namespace AudioController.Tests;

/// <summary>SPEC 4.1 / 7 "Device model": Bluetooth devices identified from the device id / enumerator name.</summary>
public sealed class BluetoothDetectorTests
{
    [Theory]
    [InlineData("BTHENUM")]
    [InlineData(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0002000A_PID&0001\7&2B5C7E3A&0&00A0C9A1B2C3_C00000000")]
    [InlineData(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&1a2b3c4d&0&001122334455_C00000000")]
    [InlineData("BTHHFENUM")]
    [InlineData(@"BTHHFENUM\BthHFPAudio\8&3A4B5C6D&0&97")]
    [InlineData("bthenum")]
    [InlineData(@"bthenum\{0000110b-0000-1000-8000-00805f9b34fb}")]
    [InlineData("BthEnum")]
    [InlineData("bthhfenum")]
    [InlineData(@"BTHLEDEVICE\{0000184e-0000-1000-8000-00805f9b34fb}_DEV_VID&02054c_PID&0e5f\8&1f2e3d4c&0&a0b1c2d3e4f5")]
    public void IsBluetooth_BluetoothEnumeratorIds_ReturnsTrue(string id)
    {
        Assert.True(BluetoothDetector.IsBluetooth(id));
    }

    [Theory]
    [InlineData(@"USB\VID_046D&PID_0A44&MI_00\7&2A5B3C4D&0&0000")]
    [InlineData("USB")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280A7F&REV_1000\4&1C2D3E4F&0&0001")]
    [InlineData("HDAUDIO")]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{5c1f2b3a-0000-4a4b-8c8d-112233445566}")]
    [InlineData(@"ROOT\MEDIA\0000")]
    [InlineData(@"INTELAUDIO\FUNC_01&VEN_8086&DEV_2812")]
    [InlineData("{0.0.0.00000000}.{5c1f2b3a-0000-4a4b-8c8d-112233445566}")] // an MMDevice endpoint id
    [InlineData("VB-Audio Virtual Cable")]
    public void IsBluetooth_NonBluetoothIds_ReturnsFalse(string id)
    {
        Assert.False(BluetoothDetector.IsBluetooth(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void IsBluetooth_NullEmptyOrWhitespace_ReturnsFalse(string? id)
    {
        Assert.False(BluetoothDetector.IsBluetooth(id));
    }
}
