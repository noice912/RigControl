using ZXing.Net.Maui;

namespace RigControl.Mobile.Views;

/// <summary>
/// Reads the PC's pairing QR code with the camera and hands the text back to Link a PC, which checks it. Nothing is
/// linked from here.
/// </summary>
public partial class ScanPage : ContentPage
{
    private int _done;

    public ScanPage()
    {
        InitializeComponent();
        Reader.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormat.QrCode,
            AutoRotate = true,
            Multiple = false,
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _done = 0;
        Reader.IsDetecting = true;
    }

    protected override void OnDisappearing()
    {
        Reader.IsDetecting = false;
        base.OnDisappearing();
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var text = e.Results?.FirstOrDefault(r => !string.IsNullOrEmpty(r.Value))?.Value;
        if (text is null || Interlocked.Exchange(ref _done, 1) == 1)
            return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            Reader.IsDetecting = false;
            await Shell.Current.GoToAsync("..", new Dictionary<string, object> { ["scanned"] = text });
        });
    }

    private async void OnCancel(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
