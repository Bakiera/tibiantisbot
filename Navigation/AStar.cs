using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Bot.Navigation;

public sealed class AStar
{
    // Cardinal-only movement (no diagonals — avoids numpad 1/3/7/9 chat leakage).
    // dx, dy, cost
    private readonly (int dx, int dy, int cost)[] moves = new[]
    {
        ( 1,  0, 1), // E
        (-1,  0, 1), // W
        ( 0, -1, 1), // N
        ( 0,  1, 1), // S
    };

    // Reuse buffers to avoid allocations
    private int[,] gScore = new int[32, 32];
    private (int x, int y)[,] cameFrom = new (int, int)[32, 32];
    private bool[,] visited = new bool[32, 32];
    private readonly PriorityQueue<(int x, int y), int> open = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Heur(int ax, int ay, int gx, int gy)
    {
        // Manhattan — matches cardinal-only moves
        return Math.Abs(ax - gx) + Math.Abs(ay - gy);
    }

    public List<(int x, int y)> FindPath(bool[,] walkable, (int x, int y) start, (int x, int y) goal)
    {
        int h = walkable.GetLength(0);
        int w = walkable.GetLength(1);

        // Expand buffers if floor is larger than current allocation
        if (h > gScore.GetLength(0) || w > gScore.GetLength(1))
        {
            gScore = new int[h, w];
            cameFrom = new (int, int)[h, w];
            visited = new bool[h, w];
        }

        open.Clear();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                gScore[y, x] = int.MaxValue;
                visited[y, x] = false;
            }
        }

        gScore[start.y, start.x] = 0;
        open.Enqueue(start, Heur(start.x, start.y, goal.x, goal.y));

        while (open.Count > 0)
        {
            var current = open.Dequeue();
            int cx = current.x, cy = current.y;
            if (visited[cy, cx]) continue;
            visited[cy, cx] = true;

            if (cx == goal.x && cy == goal.y)
                return Reconstruct(start, goal, cameFrom);

            int gCur = gScore[cy, cx];

            foreach (var m in moves)
            {
                int nx = cx + m.dx;
                int ny = cy + m.dy;

                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                if (!walkable[ny, nx]) continue;

                int tentative = gCur + m.cost;

                if (tentative < gScore[ny, nx])
                {
                    gScore[ny, nx] = tentative;
                    cameFrom[ny, nx] = (cx, cy);
                    int fScore = tentative + Heur(nx, ny, goal.x, goal.y);
                    open.Enqueue((nx, ny), fScore);
                }
            }
        }

        return new(); // no path
    }

    private static List<(int x, int y)> Reconstruct((int x, int y) start, (int x, int y) goal, (int x, int y)[,] came)
    {
        var path = new List<(int, int)>(64);
        var cur = goal;
        path.Add(cur);
        while (cur != start)
        {
            var prev = came[cur.y, cur.x];
            if (prev == (0, 0) && cur != start)
                break;
            cur = prev;
            path.Add(cur);
        }
        path.Reverse();
        return path;
    }
}
