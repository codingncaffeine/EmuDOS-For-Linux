using Avalonia.Input;
using EmuDOS.Core.Infrastructure;
using EmuDOS.Core.Library;
using EmuDOS.Core.Model;

namespace EmuDOS.Tests;

public class GameStateTests
{
    [Fact]
    public void Fullscreen_and_the_learned_program_survive_a_round_trip()
    {
        var box = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(box);
        var store = new GameboxStore();

        store.WriteState(box, new GameUserState
        {
            WindowWidth = 800,
            WindowHeight = 640,
            Fullscreen = true,
            LearnedExecutable = @"ZQ\ZQ.EXE",
        });
        var read = store.ReadState(box);

        Assert.True(read.Fullscreen);
        Assert.Equal(@"ZQ\ZQ.EXE", read.LearnedExecutable);
        Assert.Equal((800, 640), (read.WindowWidth, read.WindowHeight));
    }

    [Fact]
    public void The_default_fullscreen_gesture_parses_as_alt_enter()
    {
        var gesture = KeyGesture.Parse(new UserSettings().FullscreenKey);

        Assert.Equal(Key.Enter, gesture.Key);
        Assert.Equal(KeyModifiers.Alt, gesture.KeyModifiers);
        Assert.Equal(gesture, KeyGesture.Parse(gesture.ToString())); // what Preferences shows reads back
    }
}
