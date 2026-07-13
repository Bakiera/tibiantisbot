using Bot.Control;
using Bot.Navigation;
using Bot.State;
using Bot.Tasks.SubTasks;

namespace Bot.Tasks.RootTasks;

public sealed class FollowPathTask : BotTask
{
    public override int Priority => TaskPriority.FollowPath;

    private readonly PathRepository _repo;
    private readonly InputQueue _queue;
    private readonly KeyMover _keyboard;
    private readonly MouseMover _mouse;

    private SubTask? _currentSubTask;
    private DateTime? _waitUntil;

    private static readonly Random _rng = new();
    public TimeSpan TransitionDelay { get; init; } = TimeSpan.FromMilliseconds(200 + _rng.Next(0, 150));

    public FollowPathTask(PathRepository repo, InputQueue queue, KeyMover keyboard, MouseMover mouse)
    {
        _repo = repo;
        _queue = queue;
        _keyboard = keyboard;
        _mouse = mouse;
        Name = "FollowPath";
    }

    protected override void OnStart(BotContext ctx)
    {
        StartNextSubTask(ctx);
    }

    protected override void Execute(BotContext ctx)
    {
        // Wait between subtasks
        if (_waitUntil != null)
        {
            if (DateTime.UtcNow < _waitUntil)
                return;
            _waitUntil = null;
        }

        // Start next subtask if none running
        if (_currentSubTask == null)
        {
            StartNextSubTask(ctx);
            return;
        }

        // Tick current subtask
        _currentSubTask.Tick(ctx);

        // Handle subtask completion
        if (_currentSubTask.IsCompleted)
        {
            Console.WriteLine($"[FollowPath] Subtask '{_currentSubTask.Name}' completed.");

            var completedWp = _repo.Current;

            if (_currentSubTask.Failed)
            {
                if (completedWp?.Type is WaypointType.Step or WaypointType.RightClick or WaypointType.UseItem)
                {
                    Console.WriteLine(
                        $"[FollowPath] Subtask failed, retrying waypoint {_repo.CurrentIndex + 1}/{_repo.Waypoints.Count}");
                }
                else
                {
                    Console.WriteLine("[FollowPath] Subtask failed, rewinding path.");
                    _repo.GoBackOne();
                }
            }
            else
            {
                if (!_repo.Advance())
                    _repo.Reset();
            }

            var nextWp = _repo.Current;
            var skipDelay = !_currentSubTask.Failed
                && completedWp?.Type == WaypointType.Move
                && nextWp?.Type is WaypointType.Step or WaypointType.RightClick or WaypointType.UseItem
                && completedWp.X == nextWp.X
                && completedWp.Y == nextWp.Y
                && completedWp.Z == nextWp.Z;

            _currentSubTask = null;
            if (!skipDelay)
                _waitUntil = DateTime.UtcNow + TransitionDelay;
        }
        // Note: This task never completes on its own - it runs until preempted
    }

    public override bool IsCritical
    {
        get
        {
            if (_currentSubTask == null)
                return false;

            return _currentSubTask switch
            {
                StepDirectionTask step => step.IsCritical,
                RightClickInTileTask click => click.IsCritical,
                UseItemOnTileTask use => use.IsCritical,
                _ => false
            };
        }
    }

    private void StartNextSubTask(BotContext ctx)
    {
        var wp = _repo.Current;
        if (wp == null)
        {
            Console.WriteLine("[FollowPath] End of path, restarting.");
            _repo.Reset();
            wp = _repo.Current;
            if (wp == null)
                return;
        }

        // Skip if already at waypoint
        if (wp.IsAt(ctx.PlayerPosition))
        {
            _repo.Advance();
            wp = _repo.Current;
            if (wp == null)
                return;
        }

        Console.WriteLine($"[FollowPath] Next: {wp}");

        _currentSubTask = wp.Type switch
        {
            WaypointType.Move => new WalkToCoordinateTask((wp.X, wp.Y, wp.Z), _queue, _keyboard, this),
            WaypointType.Step => new StepDirectionTask(wp, _queue, _keyboard, this),
            WaypointType.RightClick => new RightClickInTileTask(wp, _queue, _mouse, this),
            WaypointType.UseItem => new UseItemOnTileTask(wp, _queue, _mouse, _keyboard, this),
            _ => null
        };

        if (_currentSubTask == null)
            Console.WriteLine($"[FollowPath] Unsupported waypoint type: {wp.Type}");
    }
}
