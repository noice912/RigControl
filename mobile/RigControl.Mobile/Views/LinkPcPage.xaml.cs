using RigControl.Mobile.ViewModels;

namespace RigControl.Mobile.Views;

public partial class LinkPcPage : ContentPage
{
    public LinkPcPage(LinkPcViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
