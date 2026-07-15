using Bot.Navigation;
using System.Runtime.InteropServices;

namespace Bot.Control;

public sealed class KeyMover
{
    private readonly Random _rng = new();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_F1 = 0x70;
    public const ushort VK_F2 = 0x71;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    // Virtual key codes for arrow keys (cardinal-only — no numpad diagonals)
    private const ushort VK_UP = 0x26;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;

    // Map movement deltas → key codes (NSEW only)
    private static readonly Dictionary<(int dx, int dy), ushort> Directions = new()
    {
        { ( 1,  0), VK_RIGHT },
        { (-1,  0), VK_LEFT },
        { ( 0, -1), VK_UP },
        { ( 0,  1), VK_DOWN },
    };

    private const uint WM_CHAR = 0x0102;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;

    public async Task PressKeyAsync(ushort vk, IntPtr handle, CancellationToken ct)
    {
        if (_rng.Next(10) == 0)
            await Task.Delay(_rng.Next(50, 200), ct);

        PostMessage(handle, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
        try
        {
            // Keep hold time short (20-45ms) to avoid double-stepping at high character speeds.
            // Task.Delay has ~15ms jitter on Windows, so actual hold is 20-60ms.
            // This must stay below the game's step delay to prevent two steps per key press.
            await Task.Delay(_rng.Next(20, 45), ct);
        }
        finally
        {
            PostMessage(handle, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
        }
    }

    public async Task StepTowardsAsync((int x, int y) from, (int x, int y) to, IntPtr handle, CancellationToken ct)
    {
        int dx = to.x - from.x;
        int dy = to.y - from.y;

        if (Directions.TryGetValue((dx, dy), out var vk))
            await PressKeyAsync(vk, handle, ct);
    }

    public async Task PressF1Async(IntPtr handle, CancellationToken ct)
    {
        await PressKeyAsync(VK_F1, handle, ct);
    }

    public async Task PressF2Async(IntPtr handle, CancellationToken ct)
    {
        await PressKeyAsync(VK_F2, handle, ct);
    }

    public async Task PressEscapeAsync(IntPtr handle, CancellationToken ct)
    {
        await PressKeyAsync(VK_ESCAPE, handle, ct);
    }

    public async Task StepDirectionAsync(Navigation.Direction dir, IntPtr handle, CancellationToken ct)
    {
        ushort vk = dir switch
        {
            Navigation.Direction.North => VK_UP,
            Navigation.Direction.South => VK_DOWN,
            Navigation.Direction.East => VK_RIGHT,
            Navigation.Direction.West => VK_LEFT,
            _ => throw new ArgumentOutOfRangeException(nameof(dir))
        };
        await PressKeyAsync(vk, handle, ct);
    }

    public async Task PressEnterAsync(IntPtr handle, CancellationToken ct)
    {
        await PressKeyAsync(VK_RETURN, handle, ct);
    }

    public async Task PressTabAsync(IntPtr handle, CancellationToken ct)
    {
        await PressKeyAsync(VK_TAB, handle, ct);
    }

    public async Task TypeCharAsync(char c, IntPtr handle, CancellationToken ct)
    {
        PostMessage(handle, WM_CHAR, (IntPtr)c, IntPtr.Zero);
        await Task.Delay(_rng.Next(30, 65), ct);
    }

    public async Task TypeTextAsync(string text, IntPtr handle, CancellationToken ct)
    {
        foreach (var c in text)
        {
            ct.ThrowIfCancellationRequested();
            await TypeCharAsync(c, handle, ct);
        }
    }
}