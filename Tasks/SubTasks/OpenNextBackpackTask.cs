using Bot.Control;
using Bot.Control.Actions;
using Bot.State;

namespace Bot.Tasks.SubTasks;

public sealed class OpenNextBackpackTask : SubTask
{
    private readonly ProfileSettings _profile;
    private readonly InputQueue _queue;
    private readonly MouseMover _mouse;
    private readonly object _owner;
    private readonly (int X, int Y)? _clickTarget;

    private ActionHandle? _pending;
    private bool _clicked;
    private DateTime _clickTime;

    private static readonly Random _rng = new();
    private readonly TimeSpan PostClickDelay = TimeSpan.FromMilliseconds(350 + _rng.Next(0, 100));

    public OpenNextBackpackTask(
        ProfileSettings profile,
        InputQueue queue,
        MouseMover mouse,
        object owner,
        (int X, int Y)? clickTarget = null)
    {
        _profile = profile;
        _queue = queue;
        _mouse = mouse;
        _owner = owner;
        _clickTarget = clickTarget;
        Name = "OpenNextBackpack";
    }

    protected override void OnStart(BotContext ctx)
    {
        Console.WriteLine("[Loot] Preparing to open next backpack...");
    }

    protected override void Execute(BotContext ctx)
    {
        if (_pending != null)
        {
            if (!_pending.IsCompleted) return;
            _pending = null;
            _clicked = true;
            _clickTime = DateTime.UtcNow;
            return;
        }

        if (_clicked)
        {
            if (DateTime.UtcNow - _clickTime > PostClickDelay)
                Complete();
            return;
        }

        var bp = _profile.BpRect;
        int pixelX = _clickTarget?.X ?? bp.X + bp.W - 10;
        int pixelY = _clickTarget?.Y ?? bp.Y + bp.H - 10;

        Console.WriteLine($"[Loot] Right-clicking nested backpack at ({pixelX},{pixelY})");
        _pending = _queue.Enqueue(new RightClickScreenAction(_mouse, pixelX, pixelY), _owner);
    }
}
