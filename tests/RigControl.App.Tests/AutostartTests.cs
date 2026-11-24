using System.Xml.Linq;
using RigControl.App;

namespace RigControl.App.Tests;

public class AutostartTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void Task_definition_is_valid_xml_with_the_expected_settings()
    {
        var xml = Autostart.BuildTaskXml(@"D:\Apps\Rig Control\RigControl.exe", Sid);
        var doc = XDocument.Parse(xml);

        Assert.Equal(@"D:\Apps\Rig Control\RigControl.exe", doc.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("--minimized", doc.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal("HighestAvailable", doc.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(Ns + "LogonType").Single().Value);
        Assert.All(doc.Descendants(Ns + "UserId"), e => Assert.Equal(Sid, e.Value));
        Assert.Equal(2, doc.Descendants(Ns + "UserId").Count());
        Assert.Single(doc.Descendants(Ns + "LogonTrigger"));
        // Never killed after a time limit, never stopped on battery, normal priority (audio engine).
        Assert.Equal("PT0S", doc.Descendants(Ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("4", doc.Descendants(Ns + "Priority").Single().Value);
        Assert.Equal("IgnoreNew", doc.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
    }

    [Fact]
    public void Paths_with_xml_special_characters_are_escaped()
    {
        var path = @"D:\Tom & Jerry's <apps>\RigControl.exe";
        var doc = XDocument.Parse(Autostart.BuildTaskXml(path, Sid));
        Assert.Equal(path, doc.Descendants(Ns + "Command").Single().Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("someone")]
    [InlineData("S-1-5-21-<script>")]
    public void A_user_id_that_is_not_a_sid_is_refused(string sid)
    {
        Assert.False(Autostart.IsSid(sid));
        Assert.Throws<ArgumentException>(() => Autostart.BuildTaskXml(@"C:\x\RigControl.exe", sid));
    }

    [Fact]
    public void An_empty_program_path_is_refused()
        => Assert.Throws<ArgumentException>(() => Autostart.BuildTaskXml(" ", Sid));

    [Fact]
    public void Ordinary_command_lines_are_not_helper_requests()
    {
        Assert.Null(Autostart.HandleCommandLine(Array.Empty<string>(), @"C:\x\RigControl.exe", _ => { }));
        Assert.Null(Autostart.HandleCommandLine(new[] { "--minimized", "--after-pid", "42" }, @"C:\x\RigControl.exe", _ => { }));
    }
}