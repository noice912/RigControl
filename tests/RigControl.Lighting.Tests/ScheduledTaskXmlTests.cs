using System.Text;
using RigControl.Core.Util;

namespace RigControl.Lighting.Tests;

public class ScheduledTaskXmlTests
{
    private static string Task(string uri, string? enabled, string description = "Starts the program") => $"""
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>{description}</Description>
            <URI>{uri}</URI>
          </RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            {(enabled is null ? "" : $"<Enabled>{enabled}</Enabled>")}
          </Settings>
          <Actions Context="Author"><Exec><Command>C:\Program Files\X\x.exe</Command></Exec></Actions>
        </Task>
        """;

    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n";

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData(null, true)] // Windows leaves the element out when the task is enabled
    public void A_tasks_own_Enabled_setting_is_read_and_a_triggers_is_not(string? setting, bool enabled)
    {
        Assert.Equal(enabled, ScheduledTaskXml.Enabled(Header + Task(@"\GCC", setting)));
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_words_Windows_uses_in_other_languages()
    {
        // German Windows prints "Deaktiviert" in the table and CSV forms; the XML is the same everywhere.
        Assert.False(ScheduledTaskXml.Enabled(Header + Task(@"\GCC", "false", "Deaktiviert / Désactivé / 無効")));
        Assert.True(ScheduledTaskXml.Enabled(Header + Task(@"\GCC", "true", "Disabled")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ERROR: The system cannot find the file specified.")]
    [InlineData("FEHLER: Das System kann die angegebene Datei nicht finden.")]
    [InlineData("<Task><Settings><Enabled>false")]
    public void Text_that_is_not_a_task_gives_no_answer(string output)
    {
        Assert.Null(ScheduledTaskXml.Enabled(output));
        Assert.Empty(ScheduledTaskXml.List(output));
    }

    [Fact]
    public void A_whole_list_gives_every_task_once_with_its_path()
    {
        var xml = Header + "<Tasks>\r\n"
                  + "<!-- \\ASUS\\ArmourySocketServer -->\r\n" + Task(@"\ASUS\ArmourySocketServer", null)
                  + "<!-- \\ASUS\\Framework Service -->\r\n" + Task(@"\ASUS\Framework Service", "false")
                  + "<!-- \\ASUS\\Framework Service -->\r\n" + Task(@"\ASUS\Framework Service", "false")
                  + "<!-- \\Microsoft\\Windows\\Defrag\\ScheduledDefrag -->\r\n" + Task("", "true")
                  + Task(@"\NoCommentBefore", "false")
                  + "</Tasks>";
        var list = ScheduledTaskXml.List(xml);
        Assert.Equal(new[]
        {
            (@"\ASUS\ArmourySocketServer", true),
            (@"\ASUS\Framework Service", false),
            (@"\Microsoft\Windows\Defrag\ScheduledDefrag", true),
            (@"\NoCommentBefore", false),
        }, list.Select(t => (t.Path, t.Enabled)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_list_where_every_task_is_its_own_document_is_read_too(bool commentFirst)
    {
        // Plain "schtasks /Query /XML" (without ONE) prints every task as a document of its own, one after the
        // other: an "<?xml ...?>" line each time and nothing around them. Either order of comment and header works.
        string One(string path, string? enabled) => commentFirst
            ? $"<!-- {path} -->\r\n{Header}{Task(path, enabled)}\r\n"
            : $"{Header}<!-- {path} -->\r\n{Task(path, enabled)}\r\n";
        var xml = "\r\n" + One(@"\ASUS\ArmourySocketServer", null) + One(@"\ASUS\AcPowerNotification", "false") + One(@"\GCC", "true");

        Assert.Equal(new[]
        {
            (@"\ASUS\ArmourySocketServer", true),
            (@"\ASUS\AcPowerNotification", false),
            (@"\GCC", true),
        }, ScheduledTaskXml.List(xml).Select(t => (t.Path, t.Enabled)));
    }

    [Fact]
    public void A_list_without_path_comments_falls_back_to_each_tasks_own_name()
    {
        var xml = Header + Task(@"\ASUS\One", "true") + "\r\n" + Header + Task(@"\ASUS\Two", "false");
        Assert.Equal(new[] { (@"\ASUS\One", true), (@"\ASUS\Two", false) }, ScheduledTaskXml.List(xml).Select(t => (t.Path, t.Enabled)));
    }

    [Fact]
    public void Output_is_understood_as_UTF16_or_as_plain_console_text()
    {
        var xml = Header + Task(@"\GCC", "false");
        Assert.False(ScheduledTaskXml.Enabled(ScheduledTaskXml.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray())));
        Assert.False(ScheduledTaskXml.Enabled(ScheduledTaskXml.Decode(Encoding.Unicode.GetBytes(xml))));
        Assert.False(ScheduledTaskXml.Enabled(ScheduledTaskXml.Decode(Encoding.ASCII.GetBytes(xml))));
        Assert.False(ScheduledTaskXml.Enabled(ScheduledTaskXml.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray())));
        Assert.Equal("", ScheduledTaskXml.Decode(Array.Empty<byte>()));
    }
}
