using OpenCvSharp;
using Bot.Control;
using Bot.Control.Actions;
using Bot.State;
using Bot.Navigation;
using Bot.Vision;
using Bot.GameEntity;
using Bot.Tasks.SubTasks;

namespace Bot.Tasks.RootTasks;

public sealed class LootCorpseTask : BotTask
{
    public override int Priority => TaskPriority.LootCorpse;

    private readonly InputQueue _queue;
    private readonly KeyMover _keyboard;
    private readonly MouseMover _mouse;

    private Corpse? _targetCorpse;
    private WalkToCoordinateTask? _walkSub;
    private (int x, int y, int z)? _walkGoal;
    private OpenNextBackpackTask? _openBagSub;
    private ActionHandle? _pending;
    private int _afterDelay;

    private DateTime _nextStep = DateTime.MinValue;
    private DateTime _startedAt;
    private DateTime _corpseOpenedAt = DateTime.MinValue;

    // Phase flags
    private bool _waitingCorpseOpen;
    private bool _opened;
    private bool _ate;
    private bool _goldLooted;
    private bool _floorLootDone;
    private bool _bagChecked;
    private bool _waitedNextToCorpse;
    private bool _corpseThrown;

    private static readonly TimeSpan MaxLootTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CorpseSettleDelay = TimeSpan.FromMilliseconds(400);
    private static readonly Random _rng = new();

    private const int ShortDelay = 100;
    private const int MediumDelay = 300;
    private const int LongDelay = 500;
    private const double LootMatchConfidence = 0.80;

    public LootCorpseTask(InputQueue queue, KeyMover keyboard, MouseMover mouse)
    {
        _queue = queue;
        _keyboard = keyboard;
        _mouse = mouse;
        Name = "LootCorpse";
    }

    protected override void OnStart(BotContext ctx)
    {
        _startedAt = DateTime.UtcNow;

        if (ctx.Corpses.Count == 0)
        {
            Fail("No corpses available");
            return;
        }

        _targetCorpse = ctx.Corpses.Peek();
        Console.WriteLine($"[Loot] Target corpse at {_targetCorpse.X},{_targetCorpse.Y}");
    }

    protected override void Execute(BotContext ctx)
    {
        if (_targetCorpse == null)
        {
            Complete();
            return;
        }

        if (_pending != null)
        {
            if (!_pending.IsCompleted) return;
            _pending = null;

            if (_waitingCorpseOpen)
            {
                _waitingCorpseOpen = false;
                _opened = true;
                _corpseOpenedAt = DateTime.UtcNow;
                Console.WriteLine("[Loot] Corpse opened, scanning for loot...");
            }

            _nextStep = RandomDelayFrom(_afterDelay);
            return;
        }

        if (DateTime.UtcNow - _startedAt > MaxLootTime)
        {
            Console.WriteLine($"[Loot] Timeout, skipping corpse at {_targetCorpse.X},{_targetCorpse.Y}");
            ctx.Corpses.Pop();
            Complete();
            return;
        }

        if (DateTime.UtcNow < _nextStep)
            return;

        if (!_opened)
        {
            ExecuteWalkAndOpen(ctx);
            return;
        }

        if (DateTime.UtcNow - _corpseOpenedAt < CorpseSettleDelay)
            return;

        if (!_ate)
        {
            ExecuteEating(ctx);
            return;
        }

        if (!_goldLooted)
        {
            ExecuteGoldLooting(ctx);
            return;
        }

        if (ctx.Profile.OpenBags)
        {
            if (!_floorLootDone)
            {
                ExecuteFloorLootDrop(ctx);
                return;
            }

            if (!_bagChecked)
            {
                ExecuteBagCheck(ctx);
                return;
            }
        }

        ExecuteCleanup(ctx);
    }

    private void ExecuteWalkAndOpen(BotContext ctx)
    {
        var player = (x: ctx.PlayerPosition.X, y: ctx.PlayerPosition.Y, z: ctx.PlayerPosition.Z);
        var corpse = (x: _targetCorpse!.X, y: _targetCorpse.Y, z: player.z);

        if (!NavigationHelper.IsAdjacent(player.x, player.y, corpse.x, corpse.y))
        {
            var walk = NavigationHelper.BuildDynamicWalkmap(ctx);
            var bestTile = NavigationHelper.PickBestAdjacentTile(ctx, walk, corpse.x, corpse.y);

            if (bestTile == null)
            {
                Console.WriteLine("[Loot] No walkable path to corpse");
                ctx.Corpses.Pop();
                Complete();
                return;
            }

            var goal = (x: bestTile.Value.X, y: bestTile.Value.Y, z: corpse.z);

            if (_walkSub == null || _walkGoal != goal)
            {
                _walkGoal = goal;
                _walkSub = new WalkToCoordinateTask(goal, _queue, _keyboard, this);
            }

            _walkSub.Tick(ctx);
            if (_walkSub.IsCompleted)
                _walkSub = null;

            return;
        }

        _walkSub = null;
        _walkGoal = null;

        if (!_waitedNextToCorpse)
        {
            _waitedNextToCorpse = true;
            _nextStep = RandomDelayFrom(LongDelay);
            return;
        }

        if (DateTime.UtcNow - _targetCorpse.DetectedAt < TimeSpan.FromMilliseconds(1000))
        {
            _nextStep = RandomDelayFrom(ShortDelay);
            return;
        }

        var relTile = (_targetCorpse.X - ctx.PlayerPosition.X, _targetCorpse.Y - ctx.PlayerPosition.Y);
        _pending = _queue.Enqueue(new RightClickTileAction(_mouse, relTile, ctx.Profile), this);
        _afterDelay = LongDelay;
        _waitingCorpseOpen = true;
    }

    private void ExecuteEating(BotContext ctx)
    {
        var lootRect = ctx.Profile.LootRect.ToCvRect();

        foreach (var food in ctx.FoodTemplates)
        {
            var loc = ItemFinder.FindItemInArea(
                ctx.CurrentFrameGray, food, lootRect, LootMatchConfidence, out var conf);
            if (loc == null) continue;

            Console.WriteLine($"[Loot] Eating food at ({loc.Value.X},{loc.Value.Y}), conf={conf:F2}");
            _pending = _queue.Enqueue(new RightClickScreenAction(_mouse, loc.Value.X, loc.Value.Y), this);
            _afterDelay = MediumDelay;
            return;
        }

        Console.WriteLine("[Loot] No food found in corpse window");
        _ate = true;
        _nextStep = RandomDelayFrom(ShortDelay);
    }

    private void ExecuteGoldLooting(BotContext ctx)
    {
        if (_openBagSub != null)
        {
            _openBagSub.Tick(ctx);
            if (_openBagSub.IsCompleted)
            {
                _openBagSub = null;
                _nextStep = RandomDelayFrom(MediumDelay);
            }
            return;
        }

        var lootRect = ctx.Profile.LootRect.ToCvRect();
        var bpRect = ctx.Profile.BpRect.ToCvRect();
        bool backpackEmpty = ItemFinder.IsBackpackEmpty(ctx.CurrentFrameGray, ctx.BackpackTemplate, bpRect);

        foreach (var loot in ctx.LootTemplates)
        {
            var loc = ItemFinder.FindItemInArea(
                ctx.CurrentFrameGray, loot, lootRect, LootMatchConfidence, out var conf);
            if (loc == null) continue;

            if (ctx.Profile.OpenBags &&
                ItemFinder.IsBackpackFull(ctx.CurrentFrameGray, ctx.BackpackTemplate, bpRect) &&
                ItemFinder.IsGoldStackFull(ctx.CurrentFrameGray, ctx.OneHundredGold, bpRect))
            {
                _openBagSub = new OpenNextBackpackTask(ctx.Profile, _queue, _mouse, this);
                _openBagSub.Tick(ctx);
                return;
            }

            int dropX = backpackEmpty
                ? ctx.Profile.BpRect.X + ctx.Profile.BpRect.W - 20
                : ctx.Profile.BpRect.X + 20;
            int dropY = backpackEmpty
                ? ctx.Profile.BpRect.Y + ctx.Profile.BpRect.H - 20
                : ctx.Profile.BpRect.Y + 20;

            Console.WriteLine($"[Loot] Dragging gold from ({loc.Value.X},{loc.Value.Y}) to bp ({dropX},{dropY}), conf={conf:F2}");
            _pending = _queue.Enqueue(new CtrlDragAction(_mouse, loc.Value.X, loc.Value.Y, dropX, dropY), this);
            _afterDelay = MediumDelay;
            return;
        }

        Console.WriteLine("[Loot] No gold found in corpse window");
        _goldLooted = true;
        _nextStep = RandomDelayFrom(ShortDelay);
    }

    private void ExecuteFloorLootDrop(BotContext ctx)
    {
        var lootRect = ctx.Profile.LootRect.ToCvRect();

        foreach (var template in ctx.FloorLootTemplates)
        {
            var loc = ItemFinder.FindItemInArea(ctx.CurrentFrameGray, template, lootRect, LootMatchConfidence);
            if (loc != null)
            {
                var walkmap = NavigationHelper.BuildWalkmapWithBlocked(ctx, ctx.Corpses);
                var dropTile = NavigationHelper.PickBestAdjacentTile(ctx, walkmap, ctx.PlayerPosition.X, ctx.PlayerPosition.Y);

                var toTile = dropTile is { } t
                    ? (t.X - ctx.PlayerPosition.X, t.Y - ctx.PlayerPosition.Y)
                    : (0, 0);

                _pending = _queue.Enqueue(
                    DragLeftAction.ToTile(_mouse, loc.Value.X, loc.Value.Y, toTile, ctx.Profile), this);
                _afterDelay = MediumDelay;
                Console.WriteLine($"[Loot] Dropped floor loot at ({toTile.Item1},{toTile.Item2})");
                return;
            }
        }

        _floorLootDone = true;
        _nextStep = RandomDelayFrom(ShortDelay);
    }

    private void ExecuteBagCheck(BotContext ctx)
    {
        var lootRect = ctx.Profile.LootRect.ToCvRect();
        var bagLoc = ItemFinder.FindItemInArea(ctx.CurrentFrameGray, ctx.BagTemplate, lootRect, LootMatchConfidence);

        if (bagLoc != null)
        {
            _pending = _queue.Enqueue(
                new RightClickScreenAction(_mouse, bagLoc.Value.X, bagLoc.Value.Y), this);
            _afterDelay = LongDelay;
            Console.WriteLine("[Loot] Opened bag, re-looting");

            _goldLooted = false;
            _floorLootDone = false;
            return;
        }

        _bagChecked = true;
    }

    private void ExecuteCleanup(BotContext ctx)
    {
        bool stacked = ctx.Corpses.Count(c => c.X == _targetCorpse!.X && c.Y == _targetCorpse.Y) > 1;

        if (stacked && !_corpseThrown)
        {
            var walkmap = NavigationHelper.BuildWalkmapWithBlocked(ctx, ctx.Corpses);
            var dropTile = NavigationHelper.PickBestAdjacentTile(ctx, walkmap, _targetCorpse!.X, _targetCorpse.Y);

            var toTile = dropTile is { } t
                ? (t.X - ctx.PlayerPosition.X, t.Y - ctx.PlayerPosition.Y)
                : (0, 0);

            var fromTile = (_targetCorpse.X - ctx.PlayerPosition.X, _targetCorpse.Y - ctx.PlayerPosition.Y);
            _pending = _queue.Enqueue(DragLeftAction.FromTiles(_mouse, fromTile, toTile, ctx.Profile), this);
            _afterDelay = ShortDelay;
            _corpseThrown = true;
            return;
        }

        ctx.Corpses.Pop();
        Console.WriteLine($"[Loot] Done with corpse at {_targetCorpse!.X},{_targetCorpse.Y}");
        Complete();
    }

    private static DateTime RandomDelayFrom(int delayBase)
    {
        return DateTime.UtcNow.AddMilliseconds(delayBase + _rng.Next(-25, 101));
    }
}
