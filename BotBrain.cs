using OpenCvSharp;
using Bot.Chat;
using Bot.State;
using Bot.Tasks;
using Bot.Util;
using Bot.Vision.Mana;
using Bot.Navigation;
using Bot.Tasks.RootTasks;
using Bot.GameEntity;

namespace Bot;

public sealed class BotBrain
{
    private readonly BotRuntime _rt;
    private BotContext Ctx => _rt.Ctx;
    private BotServices Svc => _rt.Svc;

    private readonly TaskOrchestrator _orchestrator;
    private readonly ManaAnalyzer _manaAnalyzer = new();
    private readonly ChatReader? _chatReader;
    private readonly ChatOrchestrator _chatOrchestrator;

    private bool _isRunning = false;
    private DateTime _lastPlayerAlert = DateTime.MinValue;

    public BotBrain(BotRuntime rt)
    {
        _rt = rt;
        _orchestrator = new TaskOrchestrator(rt.Svc.Queue);
        _chatReader = InitChatReader();
        _chatOrchestrator = new ChatOrchestrator();
    }

    private static ChatReader? InitChatReader()
    {
        var tessPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Tessdata");
        if (Directory.Exists(tessPath) && File.Exists(Path.Combine(tessPath, "eng.traineddata")))
            return new ChatReader(tessPath);
        Console.WriteLine("[ChatReader] Tessdata not found, chat OCR disabled.");
        return null;
    }

    public void ProcessFrame(Mat frame)
    {
        Ctx.CurrentFrame?.Dispose();
        Ctx.CurrentFrameGray?.Dispose();

        Ctx.CurrentFrame = frame;
        var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        Ctx.CurrentFrameGray = gray;

        var (player, creatures, corpses) = Svc.Memory.ReadEntities(Ctx.ProcessId, Ctx.ProcessMemoryBaseAddress, Ctx.Profile.PlayerName);

        Ctx.Health = player.HpPercent;
        Ctx.Mana = _manaAnalyzer.ExtractManaPercent(gray);

        Ctx.PreviousPlayerPosition = Ctx.PlayerPosition;
        Ctx.PlayerPosition = new PlayerPosition(x: player.X, player.Y, player.Z, 100);
        Ctx.CurrentFloor = Svc.MapRepo.Get(Ctx.PlayerPosition.Z)
            ?? throw new InvalidOperationException($"No map data for floor Z={Ctx.PlayerPosition.Z}");

        var vettedCreatures = new List<Creature>();
        var unknownCreatures = new List<Creature>();
        foreach (var c in creatures)
        {
            if (c.Z != player.Z) continue;
            if (Math.Abs(c.X - player.X) > 4) continue;
            if (Math.Abs(c.Y - player.Y) > 4) continue;
            if (Ctx.IgnoredCreatures.Contains(c.Id)) continue;


            if (c.IsWhitelisted)
            {
                vettedCreatures.Add(c);
            }
            else
            {
                unknownCreatures.Add(c);
            }

        }

        //Ctx.BlockingCreatures = creatures;
        Ctx.Creatures = vettedCreatures;

        foreach (var c in corpses)
        {
            if (Ctx.Corpses.Contains(c) == false)
            {
                Ctx.Corpses.Push(c);
            }
        }

        if (unknownCreatures.Count > 0)
        {
            if ((DateTime.UtcNow - _lastPlayerAlert).TotalSeconds > 30)
            {
                _lastPlayerAlert = DateTime.UtcNow;
                _ = DiscordNotifier.PlayerOnScreenAsync(unknownCreatures, Ctx.Profile.DiscordWebhookUrl);
            }
        }

        // Chat OCR
        if (_chatReader != null && Ctx.Profile.ChatRect.IsValid)
        {
            Ctx.Chat.ClearRecent();
            var messages = _chatReader.ReadChat(frame, Ctx.Profile.ChatRect);
            foreach (var msg in messages)
                Ctx.Chat.TryAddMessage(msg, Ctx.Profile.PlayerName);
        }

        if (_isRunning == false) return;

        // Chat management
        _chatOrchestrator.Update(Ctx, Svc);

        EvaluateAndSetRootTask();
        _orchestrator.Tick(Ctx);
    }

    private void EvaluateAndSetRootTask()
    {
        BotTask? next = null;

        // 0. Emergency healing with UH when health is critical (highest priority)
        if (Ctx.Health < 50 && !UseUhTask.IsDisabled && Ctx.UhTemplate != null)
        {
            next = new UseUhTask(Svc.Queue, Svc.Mouse);
        }
        // 1. Combat
        else if (Ctx.Creatures.Count > 0 && Ctx.Profile.AttackMonsters)
        {
            next = new AttackClosestCreatureTask(Svc.Queue, Svc.Keyboard, Svc.Mouse);
        }
        // 2. cast light healing spell if mana full
        else if (Ctx.Mana >= 94)
        {
            next = new CastLightHealTask(Svc.Queue, Svc.Keyboard);
        }
        // 3. Looting corpses after combat
        else if (Ctx.Corpses.Count > 0)
        {
            next = new LootCorpseTask(Svc.Queue, Svc.Keyboard, Svc.Mouse);
        }
        // 4. Path following when idle
        else if (Svc.PathRepo.Waypoints.Count > 0)
        {
            next = new FollowPathTask(Svc.PathRepo, Svc.Queue, Svc.Keyboard, Svc.Mouse);
        }

        _orchestrator.MaybeReplaceRoot(next, Ctx);
    }

    public void StartBot()
    {
        if (_isRunning) return;

        _isRunning = true;
        Console.WriteLine("[Bot] Started.");
    }

    public void StopBot()
    {
        if (!_isRunning) return;

        _isRunning = false;
        Console.WriteLine("[Bot] Stopped.");
    }

    public void ResetState()
    {
        _orchestrator.Reset();
        Svc.Queue.ClearAll();
        Svc.PathRepo.Reset();
        _manaAnalyzer.Reset();
        UseUhTask.ResetCooldown();

        Ctx.Corpses.Clear();
        Ctx.FailedAttacks.Clear();
        Ctx.IgnoredCreatures.Clear();
        Ctx.Creatures.Clear();
        Ctx.AvoidTiles.Clear();
        Ctx.Mana = 100;

        Console.WriteLine("[Bot] State reset — path index 0, tasks cleared.");
    }
}
