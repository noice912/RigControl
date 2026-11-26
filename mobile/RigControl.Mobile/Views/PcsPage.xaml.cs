using RigControl.Mobile.ViewModels;

namespace RigControl.Mobile.Views;

public partial class PcsPage : ContentPage
{
    private readonly PcsViewModel _vm;

    public PcsPage(PcsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshCommand.Execute(null);
        _vm.StartWatching();
    }

    protected override void OnDisappearing()
    {
        _vm.StopWatching();
        base.OnDisappearing();
    }
}
