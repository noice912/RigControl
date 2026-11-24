namespace RigControl.Sensors.Tests;

public class ArmouryCrateTaskTests
{
    [Fact]
    public void Armoury_Crates_start_up_tasks_are_read_the_same_in_every_Windows_language()
    {
        // What "schtasks /Query /XML ONE" prints: one document, a comment with each task's path, then the task.
        // No English words are used.
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Tasks>
            <!-- \ASUS\ArmourySocketServer -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><URI>\ASUS\ArmourySocketServer</URI></RegistrationInfo>
              <Settings><Enabled>true</Enabled></Settings>
            </Task>
            <!-- \ASUS\AcPowerNotification -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Deaktiviert</Description></RegistrationInfo>
              <Settings><Enabled>false</Enabled></Settings>
            </Task>
            <!-- \Microsoft\Windows\Defrag\ScheduledDefrag -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Settings><Enabled>true</Enabled></Settings>
            </Task>
            </Tasks>
            """;

        var tasks = AsusArmouryCrate.AsusTasks(xml);

        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, t => t.Name == @"\ASUS\ArmourySocketServer" && t.Enabled);
        Assert.Contains(tasks, t => t.Name == @"\ASUS\AcPowerNotification" && !t.Enabled);
        Assert.Empty(AsusArmouryCrate.AsusTasks("ERROR: Access is denied."));
    }

    [Fact]
    public void Armoury_Crates_start_up_tasks_are_found_when_Windows_prints_every_task_as_its_own_document()
    {
        // What "schtasks /Query /XML" prints without ONE: each task with its own "<?xml ...?>" line, nothing around them.
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <!-- \ASUS\ArmourySocketServer -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><URI>\ASUS\ArmourySocketServer</URI></RegistrationInfo>
              <Settings><Enabled>true</Enabled></Settings>
            </Task>
            <?xml version="1.0" encoding="UTF-16"?>
            <!-- \ASUS\AcPowerNotification -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Settings><Enabled>false</Enabled></Settings>
            </Task>
            <?xml version="1.0" encoding="UTF-16"?>
            <!-- \Microsoft\Windows\Defrag\ScheduledDefrag -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Settings><Enabled>true</Enabled></Settings>
            </Task>
            """;

        var tasks = AsusArmouryCrate.AsusTasks(xml);

        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, t => t.Name == @"\ASUS\ArmourySocketServer" && t.Enabled);
        Assert.Contains(tasks, t => t.Name == @"\ASUS\AcPowerNotification" && !t.Enabled);
    }
}
