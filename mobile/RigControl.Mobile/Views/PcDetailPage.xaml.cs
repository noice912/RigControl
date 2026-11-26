using RigControl.Mobile.ViewModels;

namespace RigControl.Mobile.Views;

public partial class PcDetailPage : ContentPage
{
    private readonly PcDetailViewModel _vm;

    public PcDetailPage(PcDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _vm.StartAsync();
    }

    protected override void OnDisappearing()
    {
        _vm.Stop();
        base.OnDisappearing();
    }
}
