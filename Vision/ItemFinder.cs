using OpenCvSharp;
using Point = OpenCvSharp.Point;

namespace Bot.Vision;

public static class ItemFinder
{
    public static (int X, int Y)? FindItemInArea(Mat frame, Mat targetTemplate, Rect searchRect, double minConfidence = 0.90)
    {
        return FindItemInArea(frame, targetTemplate, searchRect, minConfidence, out _);
    }

    public static (int X, int Y)? FindItemInArea(Mat frame, Mat targetTemplate, Rect searchRect, double minConfidence, out double confidence)
    {
        var matches = FindAllItemsInArea(frame, targetTemplate, searchRect, minConfidence);
        if (matches.Count == 0)
        {
            confidence = 0;
            return null;
        }

        var best = matches[0];
        confidence = best.Confidence;
        return (best.X, best.Y);
    }

    /// <summary>
    /// Finds best template match in loot window. Masks out the corpse bag icon slot first
    /// so gold/food templates cannot false-match on the bag.
    /// </summary>
    public static (int X, int Y, double Confidence)? FindBestLootInCorpse(
        Mat frame,
        IEnumerable<Mat> itemTemplates,
        Mat bagTemplate,
        Rect searchRect,
        double minConfidence,
        Action<string>? log = null)
    {
        if (searchRect.Width <= 0 || searchRect.Height <= 0)
            return null;

        using var searchArea = new Mat(frame, searchRect);
        using var masked = searchArea.Clone();

        var bagMatches = FindAllItemsInArea(frame, bagTemplate, searchRect, 0.72);
        foreach (var bag in bagMatches)
        {
            int lx = bag.X - searchRect.X - bagTemplate.Width / 2;
            int ly = bag.Y - searchRect.Y - bagTemplate.Height / 2;
            var bagRect = new Rect(lx, ly, bagTemplate.Width, bagTemplate.Height);
            Cv2.Rectangle(masked, bagRect, Scalar.All(0), -1);
            log?.Invoke($"[Loot] Masked bag slot at ({bag.X},{bag.Y})");
        }

        (int X, int Y, double Confidence)? best = null;

        foreach (var template in itemTemplates)
        {
            if (masked.Width < template.Width || masked.Height < template.Height)
                continue;

            foreach (var match in FindAllItemsInAreaOnMat(masked, template, minConfidence))
            {
                int absX = searchRect.X + match.X;
                int absY = searchRect.Y + match.Y;

                if (best == null || match.Confidence > best.Value.Confidence)
                    best = (absX, absY, match.Confidence);
            }
        }

        return best;
    }

    public static (int X, int Y)? FindBagInCorpse(Mat frame, Mat bagTemplate, Rect searchRect, double minConfidence = 0.72)
    {
        var matches = FindAllItemsInArea(frame, bagTemplate, searchRect, minConfidence);
        return matches.Count > 0 ? (matches[0].X, matches[0].Y) : null;
    }

    private static List<(int X, int Y, double Confidence)> FindAllItemsInAreaOnMat(
        Mat area, Mat targetTemplate, double minConfidence)
    {
        var results = new List<(int X, int Y, double Confidence)>();

        using var result = new Mat();
        Cv2.MatchTemplate(area, targetTemplate, result, TemplateMatchModes.CCoeffNormed);
        using var work = result.Clone();

        for (int i = 0; i < 12; i++)
        {
            Cv2.MinMaxLoc(work, out _, out double maxVal, out _, out Point maxLoc);
            if (maxVal < minConfidence)
                break;

            results.Add((
                maxLoc.X + targetTemplate.Width / 2,
                maxLoc.Y + targetTemplate.Height / 2,
                maxVal));

            int suppress = Math.Max(targetTemplate.Width, targetTemplate.Height) + 2;
            Cv2.Rectangle(
                work,
                new Rect(maxLoc.X - suppress / 2, maxLoc.Y - suppress / 2, suppress, suppress),
                Scalar.All(0),
                -1);
        }

        return results;
    }

    private static List<(int X, int Y, double Confidence)> FindAllItemsInArea(
        Mat frame, Mat targetTemplate, Rect searchRect, double minConfidence)
    {
        var results = new List<(int X, int Y, double Confidence)>();

        if (searchRect.Width <= 0 || searchRect.Height <= 0)
            return results;

        using var searchArea = new Mat(frame, searchRect);
        if (searchArea.Width < targetTemplate.Width || searchArea.Height < targetTemplate.Height)
            return results;

        foreach (var match in FindAllItemsInAreaOnMat(searchArea, targetTemplate, minConfidence))
        {
            results.Add((searchRect.X + match.X, searchRect.Y + match.Y, match.Confidence));
        }

        return results;
    }

    private const double BpSlotMatchConfidence = 0.72;

    public static Rect FirstSlotRect(Rect backPackRect) =>
        new(backPackRect.X, backPackRect.Y, 40, 40);

    public static Rect LastSlotSearchRect(Rect backPackRect) =>
        new(
            backPackRect.X + backPackRect.Width - 48,
            backPackRect.Y + backPackRect.Height - 48,
            48, 48);

    /// <summary>Nested backpack icon in the last slot (lower threshold + slightly larger search area).</summary>
    public static (int X, int Y, double Confidence)? FindNestedBackpack(
        Mat frame, Mat backpackTemplate, Rect backPackRect)
    {
        var pos = FindItemInArea(
            frame, backpackTemplate, LastSlotSearchRect(backPackRect), BpSlotMatchConfidence, out var conf);
        return pos == null ? null : (pos.Value.X, pos.Value.Y, conf);
    }

    public static (int X, int Y, double Confidence)? FindGoldStackInFirstSlot(
        Mat frame, Mat fullStackGp, Rect backPackRect)
    {
        var pos = FindItemInArea(
            frame, fullStackGp, FirstSlotRect(backPackRect), BpSlotMatchConfidence, out var conf);
        return pos == null ? null : (pos.Value.X, pos.Value.Y, conf);
    }

    public static bool IsGoldStackFull(Mat frame, Mat fullStackGp, Rect backPackRect) =>
        FindGoldStackInFirstSlot(frame, fullStackGp, backPackRect) != null;

    public static bool IsBackpackFull(Mat frame, Mat backpackTemplate, Rect backPackRect) =>
        FindNestedBackpack(frame, backpackTemplate, backPackRect) != null;

    public static bool IsBackpackEmpty(Mat frame, Mat backpackTemplate, Rect backPackRect)
    {
        var rect = FirstSlotRect(backPackRect);
        return FindItemInArea(frame, backpackTemplate, rect, BpSlotMatchConfidence) != null;
    }
}
