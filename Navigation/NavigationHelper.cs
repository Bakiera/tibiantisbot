using Bot.GameEntity;
using Bot.State;

namespace Bot.Navigation;

public static class NavigationHelper
{
    public static bool[,] BuildDynamicWalkmap(BotContext ctx)
    {
        var walk = (bool[,])ctx.CurrentFloor.Walkable.Clone();

        foreach (var c in ctx.BlockingCreatures)
        {
            if (!c.IsDead)
            {
                int x = c.X;
                int y = c.Y;

                if (y >= 0 && x >= 0 &&
                    y < walk.GetLength(0) &&
                    x < walk.GetLength(1))
                {
                    walk[y, x] = false;
                }
            }
        }

        BlockFloorChangeTiles(walk, ctx);
        return walk;
    }

    public static bool[,] BuildWalkmapWithBlocked(BotContext ctx, IEnumerable<IPositional> blockedPositions)
    {
        var walk = (bool[,])ctx.CurrentFloor.Walkable.Clone();
        int h = walk.GetLength(0);
        int w = walk.GetLength(1);

        foreach (var bp in blockedPositions)
        {
            int x = bp.X;
            int y = bp.Y;

            if (y >= 0 && x >= 0 && y < h && x < w)
            {
                walk[y, x] = false;
            }
        }

        BlockFloorChangeTiles(walk, ctx);
        return walk;
    }

    private static void BlockFloorChangeTiles(bool[,] walk, BotContext ctx)
    {
        int h = walk.GetLength(0);
        int w = walk.GetLength(1);

        foreach (var (x, y, z) in ctx.AvoidTiles)
        {
            if (z != ctx.PlayerPosition.Z) continue;
            if (y >= 0 && x >= 0 && y < h && x < w)
                walk[y, x] = false;
        }
    }

    public static (int X, int Y)? PickBestAdjacentTile(BotContext ctx, bool[,] walk, int targetX, int targetY)
    {
        int h = walk.GetLength(0);
        int w = walk.GetLength(1);

        (int X, int Y)? best = null;
        int bestScore = int.MaxValue;

        foreach (var d in Adj8)
        {
            int nx = targetX + d.dx;
            int ny = targetY + d.dy;

            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
            if (!walk[ny, nx]) continue;
            if (IsOccupiedByCreature(ctx, nx, ny)) continue;

            // Chebyshev distance to player (diagonal-aware)
            int score = Math.Max(Math.Abs(nx - ctx.PlayerPosition.X), Math.Abs(ny - ctx.PlayerPosition.Y));
            if (score < bestScore)
            {
                bestScore = score;
                best = (nx, ny);
            }
        }

        return best;
    }

    public static bool IsCardinalAdjacent(int ax, int ay, int bx, int by) =>
        Math.Abs(ax - bx) + Math.Abs(ay - by) == 1;

    /// <summary>Waypoint tiles may be mis-tagged on the minimap — allow pathing onto the goal.</summary>
    public static void EnsureWalkable(bool[,] walk, int x, int y)
    {
        int h = walk.GetLength(0);
        int w = walk.GetLength(1);
        if (x >= 0 && y >= 0 && x < w && y < h)
            walk[y, x] = true;
    }

    /// <summary>
    /// One cardinal step toward <paramref name="to"/>. Used when A* fails (e.g. recorded path
    /// skips tiles like 1301→1303). Prefers walkable tiles; falls back to trusting the recording.
    /// </summary>
    public static (int x, int y)? PickGreedyCardinalStep(
        (int x, int y) from, (int x, int y) to, bool[,]? walk = null)
    {
        int dx = Math.Sign(to.x - from.x);
        int dy = Math.Sign(to.y - from.y);
        if (dx == 0 && dy == 0)
            return null;

        var candidates = new List<(int x, int y)>(2);
        if (dx != 0 && dy != 0)
        {
            if (Math.Abs(to.x - from.x) >= Math.Abs(to.y - from.y))
            {
                candidates.Add((from.x + dx, from.y));
                candidates.Add((from.x, from.y + dy));
            }
            else
            {
                candidates.Add((from.x, from.y + dy));
                candidates.Add((from.x + dx, from.y));
            }
        }
        else if (dx != 0)
            candidates.Add((from.x + dx, from.y));
        else
            candidates.Add((from.x, from.y + dy));

        foreach (var c in candidates)
        {
            if (walk == null)
                return c;

            int h = walk.GetLength(0);
            int w = walk.GetLength(1);
            if (c.x < 0 || c.y < 0 || c.x >= w || c.y >= h)
                continue;
            if (walk[c.y, c.x])
                return c;
        }

        return candidates[0];
    }

    public static bool IsAdjacent(int px, int py, int tx, int ty) =>
        Math.Abs(px - tx) <= 1 && Math.Abs(py - ty) <= 1;

    public static readonly (int dx, int dy)[] Adj8 =
    {
        (-1,-1), (0,-1), (1,-1),
        (-1, 0),         (1, 0),
        (-1, 1), (0, 1), (1, 1),
    };

    public static bool IsOccupiedByCreature(BotContext ctx, int x, int y)
    {
        foreach (var c in ctx.Creatures)
            if (c.X == x && c.Y == y)
                return true;
        return false;
    }
}
