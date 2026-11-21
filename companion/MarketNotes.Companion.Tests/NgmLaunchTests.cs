using MarketNotes.Companion.Launch;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class NgmLaunchTests
{
    private const string Ours = @"C:\Companion\MarketNotes.Companion.exe";
    private const string Ngm = @"C:\ProgramData\Nexon\NGM\NGM64.exe";

    [Fact]
    public void Launch_argument_is_only_an_ngm_scheme()
    {
        Assert.True(NgmLaunch.IsLaunchArgument("ngm://launch/world"));
        Assert.True(NgmLaunch.IsLaunchArgument("  NGM:launch"));
        Assert.False(NgmLaunch.IsLaunchArgument(null));
        Assert.False(NgmLaunch.IsLaunchArgument(""));
        Assert.False(NgmLaunch.IsLaunchArgument("https://maplestoryworlds.nexon.com/ngm://launch"));
        Assert.False(NgmLaunch.IsLaunchArgument(new string('a', NgmLaunch.MaxArgumentLength + 1)));
    }

    [Fact]
    public void Command_parser_returns_the_executable_and_drops_the_rest()
    {
        Assert.Equal(Ngm, NgmLaunch.ExecutableFromCommand($"\"{Ngm}\" \"%1\""));
        Assert.Equal(Ngm, NgmLaunch.ExecutableFromCommand(Ngm + " %1"));
        Assert.Equal(Ours, NgmLaunch.ExecutableFromCommand($"\"{Ours}\" \"%1\""));
        Assert.Null(NgmLaunch.ExecutableFromCommand("   "));
        Assert.Null(NgmLaunch.ExecutableFromCommand("\"unterminated"));
    }

    [Fact]
    public void A_foreign_ngm_command_is_saved_and_replaced()
    {
        var plan = NgmLaunch.Decide($"\"{Ngm}\" \"%1\"", null, null, Ours);
        Assert.Equal(Ngm, plan.LauncherPath);
        Assert.True(plan.RewriteHandler);
    }

    [Fact]
    public void Our_command_keeps_the_saved_launcher()
    {
        var plan = NgmLaunch.Decide($"\"{Ours}\" \"%1\"", null, Ngm, Ours);
        Assert.Equal(Ngm, plan.LauncherPath);
        Assert.False(plan.RewriteHandler);
    }

    [Fact]
    public void Ngm_taking_the_scheme_back_is_wrapped_again()
    {
        var plan = NgmLaunch.Decide($"\"{Ngm}\" \"%1\"", null, @"C:\old\NGM64.exe", Ours);
        Assert.Equal(Ngm, plan.LauncherPath);
        Assert.True(plan.RewriteHandler);
    }

    [Fact]
    public void Machine_command_is_used_when_the_user_key_is_empty()
    {
        var plan = NgmLaunch.Decide(null, $"\"{Ngm}\" \"%1\"", null, Ours);
        Assert.Equal(Ngm, plan.LauncherPath);
        Assert.True(plan.RewriteHandler);
    }

    [Fact]
    public void A_command_that_is_not_ngm_is_not_the_launcher()
    {
        var plan = NgmLaunch.Decide(
            @"C:\Games\MapleStory.exe %1",
            $"\"{Ngm}\" \"%1\"",
            null,
            Ours);
        Assert.Equal(Ngm, plan.LauncherPath);
        Assert.True(plan.RewriteHandler);
    }

    [Fact]
    public void Hijack_preference_is_off_until_it_is_turned_on()
    {
        var path = Path.Combine(Path.GetTempPath(), "ngm-hijack-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.False(NgmHijackPreference.IsEnabled(path));
            Assert.True(NgmHijackPreference.SetEnabled(true, path));
            Assert.True(NgmHijackPreference.IsEnabled(path));
            Assert.True(NgmHijackPreference.SetEnabled(false, path));
            Assert.False(NgmHijackPreference.IsEnabled(path));
            File.WriteAllText(path, "yes");
            Assert.False(NgmHijackPreference.IsEnabled(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Nothing_to_forward_to_leaves_the_handler_alone()
    {
        var plan = NgmLaunch.Decide(null, null, null, Ours);
        Assert.Null(plan.LauncherPath);
        Assert.False(plan.RewriteHandler);
    }
}
