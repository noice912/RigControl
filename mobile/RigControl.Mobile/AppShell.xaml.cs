using RigControl.Mobile.Services;
using RigControl.Mobile.Views;

namespace RigControl.Mobile;

public partial class AppShell : Shell
{
    public const string PcRoute = "pc";
    public const string LinkRoute = "link";
    public const string ScanRoute = "scan";

    public AppShell(DeepLinkRouter links)
    {
        InitializeComponent();
        Routing.RegisterRoute(PcRoute, typeof(PcDetailPage));
        Routing.RegisterRoute(LinkRoute, typeof(LinkPcPage));
        Routing.RegisterRoute(ScanRoute, typeof(ScanPage));
        Loaded += (_, _) => links.Attach(this);
    }
}
