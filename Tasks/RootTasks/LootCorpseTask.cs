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

    private bool _waitingCorpseOpen;
    private bool _opened;
    private bool _ate;
    private bool _goldLooted;
    private bool _floorLootDone;
    private bool _bagChecked;
    private bool _waitedNextToCorpse;
    private bool _corpseThrown;
    private int _goldMissStreak;
    private int _foodEatAttempts;

    /// <summary>Once set, remaining gold on this corpse goes to Backpack 2.</summary>
    private bool _useBp2ForGold;
    private (int X, int Y)? _lastBp1GoldFrom;
    private int _bp1DepositFailStreak;

    private static readonly TimeSpan MaxLootTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CorpseSettleDelay = TimeSpan.FromMilliseconds(400);
    private static readonly Random _rng = new();

    private const int ShortDelay = 100;
    private const int MediumDelay = 300;
    private const int LongDelay = 500;
    private const double LootMatchConfidence = 0.80;
    private const int GoldMissesBeforeDone = 4;
    /// <summary>Initial eat + one retry. Stops looping when full (food stays on corpse).</summary>
    private const int MaxFoodEatAttempts = 2;
    /// <summary>Same gold still in corpse after this many BP1 drags → switch to BP2.</summary>
    private const int Bp1FailsBeforeBp2 = 2;
    private const int SameGoldTolerancePx = 24;

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
        Console.WriteLine($"[Loot] Target corpse at {_targetCorpse.X},{_targetCorpse.Y} (OpenBags={ctx.Profile.OpenBags})");
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
                Console.WriteLine("[Loot] Corpse opened — phase 1: gold, phase 2: food, phase 3: bags (if enabled)");
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

        // Phase 1: surface gold FIRST (before food — food right-click can open bag)
        if (!_goldLooted)
        {
            ExecuteGoldLooting(ctx);
            return;
        }

        // Phase 2: food
        if (!_ate)
        {
            ExecuteEating(ctx);
            return;
        }

        // Phase 3+ only when OpenBags enabled
        if (!ctx.Profile.OpenBags)
        {
            ExecuteCleanup(ctx);
            return;
        }

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

        var food = ItemFinder.FindBestLootInCorpse(
                ctx.CurrentFrameGray, ctx.FoodTemplates, ctx.BagTemplate, lootRect, LootMatchConfidence, Console.WriteLine)
            ?? ItemFinder.FindBestLootInCorpse(
                ctx.CurrentFrameGray, ctx.FoodTemplates, ctx.BagTemplate, lootRect, 0.72, Console.WriteLine);

        if (food != null)
        {
            // Character full: food stays visible — limit retries instead of looping until loot timeout.
            if (_foodEatAttempts >= MaxFoodEatAttempts)
            {
                Console.WriteLine(
                    $"[Loot] Food still present after {_foodEatAttempts} eat attempts (likely full) — skipping");
                _ate = true;
                _nextStep = RandomDelayFrom(ShortDelay);
                return;
            }

            _foodEatAttempts++;
            Console.WriteLine(
                $"[Loot] Eating food at ({food.Value.X},{food.Value.Y}), conf={food.Value.Confidence:F2} " +
                $"(attempt {_foodEatAttempts}/{MaxFoodEatAttempts})");
            _pending = _queue.Enqueue(new RightClickScreenAction(_mouse, food.Value.X, food.Value.Y), this);
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
        bool bp1FullIcon = ItemFinder.IsBackpackFull(ctx.CurrentFrameGray, ctx.BackpackTemplate, bpRect);
        bool hasBp2 = ctx.Profile.BpRect2.IsValid;

        var gold = ItemFinder.FindBestLootInCorpse(
                ctx.CurrentFrameGray, ctx.LootTemplates, ctx.BagTemplate, lootRect, LootMatchConfidence, Console.WriteLine)
            ?? ItemFinder.FindBestLootInCorpse(
                ctx.CurrentFrameGray, ctx.LootTemplates, ctx.BagTemplate, lootRect, 0.72, Console.WriteLine);

        if (gold != null)
        {
            _goldMissStreak = 0;

            // IsBackpackFull only matches a backpack icon in the last slot — not "no free slots".
            // Detect full BP1 by gold still sitting in the corpse after a BP1 drag, then switch to BP2.
            if (!_useBp2ForGold && hasBp2)
            {
                if (bp1FullIcon)
                {
                    _useBp2ForGold = true;
                    Console.WriteLine("[Loot] BP1 full (nested-bp icon) — switching gold to Backpack 2");
                }
                else if (_lastBp1GoldFrom is { } last && Near(gold.Value.X, gold.Value.Y, last.X, last.Y))
                {
                    _bp1DepositFailStreak++;
                    Console.WriteLine(
                        $"[Loot] Gold still in corpse after BP1 drag ({_bp1DepositFailStreak}/{Bp1FailsBeforeBp2})");
                    if (_bp1DepositFailStreak >= Bp1FailsBeforeBp2)
                    {
                        _useBp2ForGold = true;
                        Console.WriteLine("[Loot] BP1 rejected gold — switching to Backpack 2");
                    }
                }
                else
                {
                    _bp1DepositFailStreak = 0;
                }
            }

            if (_useBp2ForGold && hasBp2)
            {
                var bp2 = ctx.Profile.BpRect2;
                bool bp2Empty = ItemFinder.IsBackpackEmpty(
                    ctx.CurrentFrameGray, ctx.BackpackTemplate, bp2.ToCvRect());
                var (dropX2, dropY2) = GoldDropPoint(bp2, bp2Empty);
                Console.WriteLine(
                    $"[Loot] Dragging gold from ({gold.Value.X},{gold.Value.Y}) to bp2 ({dropX2},{dropY2}), conf={gold.Value.Confidence:F2}");
                _pending = _queue.Enqueue(new CtrlDragAction(_mouse, gold.Value.X, gold.Value.Y, dropX2, dropY2), this);
                _afterDelay = MediumDelay;
                return;
            }

            if (ctx.Profile.OpenBags &&
                bp1FullIcon &&
                ItemFinder.IsGoldStackFull(ctx.CurrentFrameGray, ctx.OneHundredGold, bpRect))
            {
                _openBagSub = new OpenNextBackpackTask(ctx.Profile, _queue, _mouse, this);
                _openBagSub.Tick(ctx);
                return;
            }

            bool backpackEmpty = ItemFinder.IsBackpackEmpty(ctx.CurrentFrameGray, ctx.BackpackTemplate, bpRect);
            var (dropX, dropY) = GoldDropPoint(ctx.Profile.BpRect, backpackEmpty);

            Console.WriteLine($"[Loot] Dragging gold from ({gold.Value.X},{gold.Value.Y}) to bp ({dropX},{dropY}), conf={gold.Value.Confidence:F2}");
            _lastBp1GoldFrom = (gold.Value.X, gold.Value.Y);
            _pending = _queue.Enqueue(new CtrlDragAction(_mouse, gold.Value.X, gold.Value.Y, dropX, dropY), this);
            _afterDelay = MediumDelay;
            return;
        }

        _goldMissStreak++;
        if (_goldMissStreak < GoldMissesBeforeDone)
        {
            Console.WriteLine($"[Loot] No gold yet ({_goldMissStreak}/{GoldMissesBeforeDone}), retrying...");
            _nextStep = RandomDelayFrom(ShortDelay);
            return;
        }

        Console.WriteLine("[Loot] Surface gold done");
        _goldLooted = true;
        _nextStep = RandomDelayFrom(ShortDelay);
    }

    private static bool Near(int x1, int y1, int x2, int y2) =>
        Math.Abs(x1 - x2) <= SameGoldTolerancePx && Math.Abs(y1 - y2) <= SameGoldTolerancePx;

    private static (int X, int Y) GoldDropPoint(RectDto bp, bool empty) =>
        empty
            ? (bp.X + bp.W - 20, bp.Y + bp.H - 20)
            : (bp.X + 20, bp.Y + 20);

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
        if (!ctx.Profile.OpenBags)
        {
            Console.WriteLine("[Loot] OpenBags=false, skipping corpse bag");
            _bagChecked = true;
            return;
        }

        var lootRect = ctx.Profile.LootRect.ToCvRect();
        var bagLoc = ItemFinder.FindBagInCorpse(ctx.CurrentFrameGray, ctx.BagTemplate, lootRect);

        if (bagLoc != null)
        {
            _pending = _queue.Enqueue(
                new RightClickScreenAction(_mouse, bagLoc.Value.X, bagLoc.Value.Y), this);
            _afterDelay = LongDelay;
            Console.WriteLine($"[Loot] Phase 3: opening corpse bag at ({bagLoc.Value.X},{bagLoc.Value.Y})");

            _goldLooted = false;
            _goldMissStreak = 0;
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
