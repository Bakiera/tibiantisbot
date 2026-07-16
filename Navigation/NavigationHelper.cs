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
