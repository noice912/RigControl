using RigControl.App;

namespace RigControl.App.Tests;

/// <summary>"Start with Windows" belongs to one Windows user. Nothing here runs Windows' task scheduler.</summary>
public class AutostartOwnerTests
{
    private const string Alice = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Bob = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string Exe = @"C:\Program Files\RigControl\app\RigControl.exe";

    private static Autostart.TaskAnswer TaskOf(string sid, bool enabled = true)
    {
        var xml = Autostart.BuildTaskXml(Exe, sid);
        if (!enabled) xml = xml.Replace("<Enabled>true</Enabled>\r\n    <Hidden>", "<Enabled>false</Enabled>\r\n    <Hidden>").Replace("<Enabled>true</Enabled>\n    <Hidden>", "<Enabled>false</Enabled>\n    <Hidden>");
        return new Autostart.TaskAnswer(true, true, Autostart.ReadTask(xml));
    }

    [Fact]
    public void A_tasks_text_says_who_it_is_for_what_it_starts_and_whether_it_is_on()
    {
        var info = Autostart.ReadTask("\uFEFF" + Autostart.BuildTaskXml(Exe, Alice))!;
        Assert.Equal(Alice, info.UserId);
        Assert.Equal(Exe, info.Command);
        Assert.True(info.Enabled);
        Assert.False(TaskOf(Alice, enabled: false).Info!.Enabled);
        Assert.Null(Autostart.ReadTask("ERROR: The system cannot find the file specified."));
        Assert.Null(Autostart.ReadTask("<Tasks/>"));
    }

    [Fact]
    public void Each_Windows_user_has_a_task_name_of_their_own()
    {
        Assert.Equal("RigControl-" + Alice, Autostart.TaskNameFor(Alice));
        Assert.NotEqual(Autostart.TaskNameFor(Alice), Autostart.TaskNameFor(Bob));
    }

    [Fact]
    public void The_old_shared_task_only_counts_for_the_user_it_was_made_for()
    {
        var legacy = TaskOf(Alice);

        var alice = Autostart.Decide(Alice, Autostart.TaskAnswer.Missing, legacy);
        Assert.True(alice.Known);
        Assert.True(alice.Enabled);
        Assert.Equal(Exe, alice.Command);
        Assert.Equal(Autostart.TaskName, alice.Task);

        // The second user is not told it is "already on": the task would never start RigControl for them.
        var bob = Autostart.Decide(Bob, Autostart.TaskAnswer.Missing, legacy);
        Assert.True(bob.Known);
        Assert.False(bob.Enabled);
        Assert.Null(bob.Command);
    }

    [Fact]
    public void A_users_own_task_wins_and_a_switched_off_one_reads_as_off()
    {
        var own = Autostart.Decide(Bob, TaskOf(Bob), TaskOf(Alice));
        Assert.True(own.Enabled);
        Assert.Equal(Autostart.TaskNameFor(Bob), own.Task);

        Assert.False(Autostart.Decide(Bob, TaskOf(Bob, enabled: false), Autostart.TaskAnswer.Missing).Enabled);
        Assert.False(Autostart.Decide(Alice, Autostart.TaskAnswer.Missing, TaskOf(Alice, enabled: false)).Enabled);
    }

    [Fact]
    public void When_Windows_will_not_say_the_answer_is_unknown_and_never_another_users_task()
    {
        Assert.False(Autostart.Decide(Bob, Autostart.TaskAnswer.Unknown, Autostart.TaskAnswer.Missing).Known);
        Assert.False(Autostart.Decide(Bob, Autostart.TaskAnswer.Missing, Autostart.TaskAnswer.Unknown).Known);
        var state = Autostart.Decide(Bob, Autostart.TaskAnswer.Unknown, TaskOf(Alice));
        Assert.False(state.Enabled);
        // An old task that can't be read names nobody, so it is nobody's.
        Assert.False(Autostart.Decide(Bob, Autostart.TaskAnswer.Missing, new Autostart.TaskAnswer(true, true, null)).Enabled);
    }

    [Fact]
    public void Switching_it_on_writes_the_users_own_task_and_keeps_the_old_name_only_for_its_owner()
    {
        var legacy = TaskOf(Alice);
        Assert.Equal(Autostart.TaskName, Autostart.TaskToWrite(Alice, legacy));          // her task keeps its name
        Assert.Equal(Autostart.TaskNameFor(Bob), Autostart.TaskToWrite(Bob, legacy));    // his never replaces hers
        Assert.Equal(Autostart.TaskNameFor(Bob), Autostart.TaskToWrite(Bob, Autostart.TaskAnswer.Missing));
        Assert.Equal(Autostart.TaskNameFor(Bob), Autostart.TaskToWrite(Bob, Autostart.TaskAnswer.Unknown));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5-18")]
    [InlineData("Nobody\\NoSuchAccount-rigcontrol-test")]
    public void A_task_for_nobody_or_somebody_else_is_not_this_users(string? user)
    {
        Assert.False(Autostart.BelongsTo(new Autostart.TaskInfo(true, Exe, user), Alice));
        Assert.False(Autostart.BelongsTo(null, Alice));
    }
}
