using Bot.Control;
using Bot.Control.Actions;
using Bot.Navigation;
using Bot.State;

namespace Bot.Tasks.SubTasks;

public sealed class StepDirectionTask : SubTask
{
    private readonly Waypoint _waypoint;
    private readonly InputQueue _queue;
    private readonly KeyMover _keyboard;
    private readonly object _owner;

    private ActionHandle? _pending;
    private bool _requestedStep;
    private (int X, int Y, int Z) _startPos;
    private DateTime? _waitStarted;
    private DateTime _lastProgressLog = DateTime.MinValue;

    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// True once step is enqueued until completion - prevents preemption
    /// so the task can detect the Z change and advance the path.
    /// </summary>
    public bool IsCritical => (_pending != null || _requestedStep) && !IsCompleted;

    public StepDirectionTask(Waypoint waypoint, InputQueue queue, KeyMover keyboard, object owner)
    {
        if (waypoint.Type != WaypointType.Step)
            throw new ArgumentException("StepDirectionTask requires a Step waypoint");

        _waypoint = waypoint;
        _queue = queue;
        _keyboard = keyboard;
        _owner = owner;
        Name = $"Step-{waypoint.Dir}";
    }

    protected override void OnStart(BotContext ctx)
    {
        _startPos = (ctx.PlayerPosition.X, ctx.PlayerPosition.Y, ctx.PlayerPosition.Z);
        var (tx, ty) = GetStepTile();
        Console.WriteLine(
            $"[{Name}] At ({_waypoint.X},{_waypoint.Y},Z={_startPos.Z}), stepping {_waypoint.Dir} toward ({tx},{ty}), waiting for Z change...");
    }

    protected override void Execute(BotContext ctx)
    {
        if (_pending != null)
        {
            if (!_pending.IsCompleted) return;
            _pending = null;
            _requestedStep = true;
            _waitStarted = DateTime.UtcNow;
            Console.WriteLine($"[{Name}] Key pressed, waiting for Z change from {_startPos.Z}...");
            return;
        }

        if (_requestedStep)
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
                var (tx, ty) = GetStepTile();
                Fail(
                    $"Z did not change after {_waypoint.Dir} step — player=({_startPos.X},{_startPos.Y},Z={ctx.PlayerPosition.Z}), step tile=({tx},{ty})");
            }
            return;
        }

        if (ctx.PlayerPosition.X != _waypoint.X || ctx.PlayerPosition.Y != _waypoint.Y)
        {
            Fail($"Not at required position ({_waypoint.X},{_waypoint.Y})");
            return;
        }

        _pending = _queue.Enqueue(
            new StepDirectionAction(_keyboard, _waypoint.Dir, ctx.GameWindowHandle), _owner);
    }

    protected override void OnFinish(BotContext ctx)
    {
        if (!Failed && _requestedStep)
            Console.WriteLine($"[{Name}] Success: Z changed from {_startPos.Z} to {ctx.PlayerPosition.Z}");
    }

    private (int X, int Y) GetStepTile()
    {
        var (dx, dy) = _waypoint.Dir switch
        {
            Direction.North => (0, -1),
            Direction.South => (0, 1),
            Direction.East => (1, 0),
            Direction.West => (-1, 0),
            _ => (0, 0)
        };
        return (_waypoint.X + dx, _waypoint.Y + dy);
    }
}
