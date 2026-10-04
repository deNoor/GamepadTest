using System.Globalization;
using GamepadButtons = GamepadTest.GameDevice.GamepadButtons;
using GamepadState = GamepadTest.GameDevice.GamepadState;

namespace GamepadTest;

internal static partial class Program
{
    private static int _renderedLineCount = 3;

    private static async Task<int> Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("GameInput is available here only on Windows.");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var gameInput = new GameDevice();

            Console.WriteLine("Xbox controller demo");
            Console.WriteLine("Press Ctrl+C to exit.");
            Console.WriteLine("Updates on button, trigger, and stick input changes.");
            Console.WriteLine("Buttons (face/system):");
            Console.WriteLine("Buttons (d-pad/shoulders/sticks):");
            Console.WriteLine("Triggers and stick axes:");
            var previousDisplay = "Waiting for the first gamepad reading...";
            WriteStatus(previousDisplay);

            await gameInput.TrackGamepadInputAsync(
                state =>
                {
                    var display = FormatState(state);
                    if (!string.Equals(display, previousDisplay, StringComparison.Ordinal))
                    {
                        WriteStatus(display);
                        previousDisplay = display;
                    }
                },
                cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine(
                "GameInputRedist.dll was not found. Install the GameInput PC redistributable to run this sample.");
            return 1;
        }
        catch (EntryPointNotFoundException)
        {
            Console.Error.WriteLine(
                "GameInputRedist.dll does not provide GameInputInitialize. Install the GameInput 3.5 redistributable.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static string FormatState(GamepadState state)
    {
        var faceAndSystem = GetPressedButtons(
            state.Buttons,
            (GamepadButtons.A, "A"),
            (GamepadButtons.B, "B"),
            (GamepadButtons.X, "X"),
            (GamepadButtons.Y, "Y"),
            (GamepadButtons.Menu, "Menu"),
            (GamepadButtons.View, "View"));
        var dpadAndOther = GetPressedButtons(
            state.Buttons,
            (GamepadButtons.DPadUp, "Up"),
            (GamepadButtons.DPadDown, "Down"),
            (GamepadButtons.DPadLeft, "Left"),
            (GamepadButtons.DPadRight, "Right"),
            (GamepadButtons.LeftShoulder, "LB"),
            (GamepadButtons.RightShoulder, "RB"),
            (GamepadButtons.LeftThumbstick, "L3"),
            (GamepadButtons.RightThumbstick, "R3"));
        var analog = string.Format(
            CultureInfo.InvariantCulture,
            "LT {0:F2}  RT {1:F2}  Left stick ({2:F2}, {3:F2})  Right stick ({4:F2}, {5:F2})",
            state.LeftTrigger,
            state.RightTrigger,
            state.LeftThumbstickX,
            state.LeftThumbstickY,
            state.RightThumbstickX,
            state.RightThumbstickY);

        return $"{faceAndSystem}\n{dpadAndOther}\n{analog}";
    }

    private static string GetPressedButtons(
        GamepadButtons buttons,
        params (GamepadButtons Button, string Name)[] mappings)
    {
        var pressed = mappings
            .Where(mapping => (buttons & mapping.Button) != 0)
            .Select(mapping => mapping.Name);
        var result = string.Join(' ', pressed);
        return result.Length == 0 ? "None" : result;
    }

    private static void WriteStatus(string status)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(string.Join(' ', status.Split('\n')));
            return;
        }

        var lines = status.Split('\n');
        for (var index = 0; index < Math.Max(_renderedLineCount, lines.Length); index++)
        {
            Console.SetCursorPosition(0, index + 6);
            var line = index < lines.Length ? lines[index] : string.Empty;
            Console.Write(line.PadRight(Math.Max(1, Console.WindowWidth - 1)));
        }

        _renderedLineCount = lines.Length;
    }
}
