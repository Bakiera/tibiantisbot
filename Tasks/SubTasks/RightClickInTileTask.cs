using Bot.Control;
using Bot.Control.Actions;
using Bot.Navigation;
using Bot.State;

namespace Bot.Tasks.SubTasks;

public sealed class RightClickInTileTask : SubTask
{
    private readonly Waypoint _wp;
    private readonly InputQueue _queue;
    private readonly MouseMover _mouse;
    private readonly object _owner;

    private ActionHandle? _pending;
    private bool _clicked;
    private (int X, int Y, int Z) _startPos;
    private DateTime? _waitStarted;
    private DateTime _lastProgressLog = DateTime.MinValue;

    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// True once click is enqueued until completion - prevents preemption
    /// so the task can detect the Z change and advance the path.
    /// </summary>
    public bool IsCritical => (_pending != null || _clicked) && !IsCompleted;

    public RightClickInTileTask(Waypoint wp, InputQueue queue, MouseMover mouse, object owner)
    {
        _wp = wp;
        _queue = queue;
        _mouse = mouse;
        _owner = owner;
        Name = $"RightClickTile-{wp.Dir}";
    }

    protected override void OnStart(BotContext ctx)
    {
        _startPos = (ctx.PlayerPosition.X, ctx.PlayerPosition.Y, ctx.PlayerPosition.Z);
        var (tx, ty) = GetTargetTile();
        Console.WriteLine(
            $"[{Name}] At ({_wp.X},{_wp.Y},Z={_startPos.Z}), right-clicking {_wp.Dir} toward ({tx},{ty}), waiting for Z change...");
    }

    protected override void Execute(BotContext ctx)
    {
        if (_pending != null)
        {
            if (!_pending.IsCompleted) return;
            _pending = null;
            _clicked = true;
            _waitStarted = DateTime.UtcNow;
            Console.WriteLine($"[{Name}] Clicked, waiting for Z change from {_startPos.Z}...");
            return;
        }

        if (_clicked)
        {
            if (ctx.PlayerPosition.Z != _startPos.Z)
            {
                Complete();
                return;
            }

            var elapsed = DateTime.UtcNow - _waitStarted!.Value;
            if (elapsed >= ProgressLogInterval && DateTime.UtcNow - _lastProgressLog >= ProgressLogInterval)
            {
                _lastProgressLog = DateTime.UtcNow;
                Console.WriteLine(
                    $"[{Name}] Still waiting for Z change ({(int)elapsed.TotalMilliseconds}ms) — player Z={ctx.PlayerPosition.Z}, started Z={_startPos.Z}");
            }

            if (elapsed > MaxWait)
            {
                var (tx, ty) = GetTargetTile();
                Fail(
                    $"Z did not change after {_wp.Dir} right-click — player=({_startPos.X},{_startPos.Y},Z={ctx.PlayerPosition.Z}), target tile=({tx},{ty})");
            }
            return;
        }

        if (ctx.PlayerPosition.X != _wp.X || ctx.PlayerPosition.Y != _wp.Y)
        {
            Fail($"Incorrect position, expected ({_wp.X},{_wp.Y})");
            return;
        }

        var slot = ComputeTileSlot(_wp, ctx);
        _pending = _queue.Enqueue(new RightClickTileAction(_mouse, slot, ctx.Profile), _owner);
    }

    protected override void OnFinish(BotContext ctx)
    {
        if (!Failed && _clicked)
            Console.WriteLine($"[{Name}] Success: Z changed from {_startPos.Z} to {ctx.PlayerPosition.Z}");
    }

    private (int X, int Y) GetTargetTile()
    {
        var (tx, ty) = (_wp.X, _wp.Y);
        switch (_wp.Dir)
        {
            case Direction.North: ty -= 1; break;
            case Direction.South: ty += 1; break;
            case Direction.East: tx += 1; break;
            case Direction.West: tx -= 1; break;
        }
        return (tx, ty);
    }

    private static (int X, int Y) ComputeTileSlot(Waypoint wp, BotContext ctx)
    {
        var (tx, ty) = (wp.X, wp.Y);

        switch (wp.Dir)
        {
            case Direction.North: ty -= 1; break;
            case Direction.South: ty += 1; break;
            case Direction.East: tx += 1; break;
            case Direction.West: tx -= 1; break;
        }

        return (tx - ctx.PlayerPosition.X, ty - ctx.PlayerPosition.Y);
    }
}
