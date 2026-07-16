using Bot.Control;
using Bot.Control.Actions;
using Bot.Navigation;
using Bot.State;

namespace Bot.Tasks.SubTasks;

public sealed class WalkToCoordinateTask : SubTask
{
    private readonly (int x, int y, int z) _target;
    private readonly AStar _astar = new();
    private readonly InputQueue _queue;
    private readonly KeyMover _keyboard;
    private readonly object _owner;

    private ActionHandle? _pending;
    private (int X, int Y)? _expectedTile;
    private int _ticksWaiting;
    private const int MaxWaitTicks = 20;

    private DateTime _nextAllowedMove = DateTime.MinValue;
    private readonly Random _rng = new();
    private TimeSpan MoveCooldown => TimeSpan.FromMilliseconds(142 + _rng.Next(0, 88));

    private (int X, int Y) _lastPlayerPos;
    private DateTime _stableSince = DateTime.UtcNow;
    private static readonly TimeSpan MinStableTime = TimeSpan.FromMilliseconds(200);

    private int _bestManhattan = int.MaxValue;
    private int _ticksSinceProgress;
    private const int MaxTicksSinceProgress = 90;

    public WalkToCoordinateTask((int x, int y, int z) target, InputQueue queue, KeyMover keyboard, object owner)
    {
        _target = target;
        _queue = queue;
        _keyboard = keyboard;
        _owner = owner;
        Name = $"WalkTo({_target.x},{_target.y},{_target.z})";
    }

    protected override void OnStart(BotContext ctx)
    {
        _lastPlayerPos = (ctx.PlayerPosition.X, ctx.PlayerPosition.Y);
        _stableSince = DateTime.UtcNow;
        _bestManhattan = int.MaxValue;
        _ticksSinceProgress = 0;
    }

    protected override void Execute(BotContext ctx)
    {
        if (_pending != null)
        {
            if (!_pending.IsCompleted) return;
            _pending = null;
            _nextAllowedMove = DateTime.UtcNow + MoveCooldown;
            return;
        }

        var player = (ctx.PlayerPosition.X, ctx.PlayerPosition.Y, ctx.PlayerPosition.Z);

        if (player.Item1 == _target.x && player.Item2 == _target.y && player.Item3 == _target.z)
        {
            Complete();
            return;
        }

        if (player.Item3 != _target.z)
        {
            Fail($"Unexpected floor change (Z={player.Item3}, expected {_target.z})");
            return;
        }

        TrackProgress(player);

        if (player.Item1 != _lastPlayerPos.X || player.Item2 != _lastPlayerPos.Y)
        {
            _lastPlayerPos = (player.Item1, player.Item2);
            _stableSince = DateTime.UtcNow;
        }

        if (_expectedTile != null)
        {
            if (player.Item1 == _expectedTile.Value.X && player.Item2 == _expectedTile.Value.Y)
            {
                _expectedTile = null;
                _ticksWaiting = 0;
                return;
            }

            _ticksWaiting++;
            if (_ticksWaiting > MaxWaitTicks)
            {
                Console.WriteLine(
                    $"[{Name}] Step not confirmed at ({_expectedTile.Value.X},{_expectedTile.Value.Y}), retrying");
                _expectedTile = null;
                _ticksWaiting = 0;
            }
            return;
        }

        if (DateTime.UtcNow < _nextAllowedMove)
            return;

        int manhattan = Math.Abs(player.Item1 - _target.x) + Math.Abs(player.Item2 - _target.y);
        bool cardinalAdjacent = manhattan == 1;

        if (!cardinalAdjacent && DateTime.UtcNow - _stableSince < MinStableTime)
            return;

        if (cardinalAdjacent)
        {
            EnqueueStep(ctx, (player.Item1, player.Item2), (_target.x, _target.y));
            return;
        }

        var walkmap = NavigationHelper.BuildDynamicWalkmap(ctx);
        NavigationHelper.EnsureWalkable(walkmap, _target.x, _target.y);

        var path = _astar.FindPath(walkmap, (player.Item1, player.Item2), (_target.x, _target.y));

        if (path.Count > 1)
        {
            var next = path[1];
            EnqueueStep(ctx, (player.Item1, player.Item2), next);
            return;
        }

        Fail($"No path to ({_target.x},{_target.y},{_target.z}) from ({player.Item1},{player.Item2})");
    }

    private void TrackProgress((int X, int Y, int Z) player)
    {
        int manhattan = Math.Abs(player.X - _target.x) + Math.Abs(player.Y - _target.y);
        if (manhattan < _bestManhattan)
        {
            _bestManhattan = manhattan;
            _ticksSinceProgress = 0;
            return;
        }

        _ticksSinceProgress++;
        if (_ticksSinceProgress > MaxTicksSinceProgress)
            Fail($"Stuck at ({player.X},{player.Y}) — cannot reach ({_target.x},{_target.y},{_target.z})");
    }

    private void EnqueueStep(BotContext ctx, (int x, int y) from, (int x, int y) to)
    {
        Console.WriteLine($"[{Name}] Step ({from.x},{from.y}) -> ({to.x},{to.y})");
        _expectedTile = to;
        _ticksWaiting = 0;
        _pending = _queue.Enqueue(
            new StepTowardsAction(_keyboard, from, to, ctx.GameWindowHandle), _owner);
    }
}
