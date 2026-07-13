using Bot.Control;
using Bot.State;

namespace Bot.Tasks;

public sealed class TaskOrchestrator
{
    private readonly InputQueue _queue;
    private BotTask? _rootTask;

    public BotTask? Current => _rootTask;

    public TaskOrchestrator(InputQueue queue)
    {
        _queue = queue;
    }

    public void MaybeReplaceRoot(BotTask? candidate, BotContext ctx)
    {
        if (candidate == null)
            return;

        if (_rootTask != null && _rootTask.IsCritical && !_rootTask.IsCompleted)
            return;

        if (_rootTask == null || _rootTask.IsCompleted)
        {
            _rootTask = candidate;
            return;
        }

        if (candidate.Priority > _rootTask.Priority)
        {
            // Remove all queued actions belonging to the old root task
            _queue.RemoveByOwner(_rootTask);
            _rootTask = candidate;
        }
    }

    public void Reset()
    {
        if (_rootTask != null)
        {
            _queue.RemoveByOwner(_rootTask);
            _rootTask = null;
        }
    }

    public void Tick(BotContext ctx)
    {
        if (_rootTask == null)
            return;

        _rootTask.Tick(ctx);

        if (_rootTask.IsCompleted)
        {
            _queue.RemoveByOwner(_rootTask);
            _rootTask = null;
        }
    }
}
