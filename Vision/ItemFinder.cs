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

    public static (int X, int Y, double Confidence)? FindBestGoldInCorpse(
        Mat frame,
        IEnumerable<Mat> goldTemplates,
        Mat bagTemplate,
        Rect searchRect,
        double minConfidence = 0.80,
        Action<string>? log = null)
    {
        (int X, int Y, double Confidence)? best = null;

        foreach (var goldTemplate in goldTemplates)
        {
            foreach (var match in FindAllItemsInArea(frame, goldTemplate, searchRect, minConfidence))
            {
                double goldConf = GetTemplateConfidenceAt(frame, goldTemplate, match.X, match.Y);
                double bagConf = GetTemplateConfidenceAt(frame, bagTemplate, match.X, match.Y);

                if (bagConf >= goldConf - 0.02 && bagConf >= minConfidence - 0.05)
                {
                    log?.Invoke(
                        $"[Loot] Skip ({match.X},{match.Y}): looks like bag (bag={bagConf:F2}, gold={goldConf:F2})");
                    continue;
                }

                if (best == null || match.Confidence > best.Value.Confidence)
                    best = (match.X, match.Y, match.Confidence);
            }
        }

        return best;
    }

    public static double GetTemplateConfidenceAt(Mat frame, Mat template, int centerX, int centerY)
    {
        int x = centerX - template.Width / 2;
        int y = centerY - template.Height / 2;
        var rect = new Rect(x, y, template.Width, template.Height);

        if (rect.X < 0 || rect.Y < 0 || rect.Right > frame.Width || rect.Bottom > frame.Height)
            return 0;

        using var roi = new Mat(frame, rect);
        if (roi.Width < template.Width || roi.Height < template.Height)
            return 0;

        using var result = new Mat();
        Cv2.MatchTemplate(roi, template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
        return maxVal;
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

        using var result = new Mat();
        Cv2.MatchTemplate(searchArea, targetTemplate, result, TemplateMatchModes.CCoeffNormed);
        using var work = result.Clone();

        for (int i = 0; i < 12; i++)
        {
            Cv2.MinMaxLoc(work, out _, out double maxVal, out _, out Point maxLoc);
            if (maxVal < minConfidence)
                break;

            var localCenter = new Point(
                maxLoc.X + targetTemplate.Width / 2,
                maxLoc.Y + targetTemplate.Height / 2);

            results.Add((
                searchRect.X + localCenter.X,
                searchRect.Y + localCenter.Y,
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

    public static bool IsGoldStackFull(Mat frame, Mat fullStackGp, Rect backPackRect)
    {
        var rect = new Rect(
            backPackRect.X,
            backPackRect.Y,
            40, 40);

        return FindItemInArea(frame, fullStackGp, rect) != null;
    }

    public static bool IsBackpackFull(Mat frame, Mat backpackTemplate, Rect backPackRect)
    {
        var rect = new Rect(
            backPackRect.X + backPackRect.Width - 40,
            backPackRect.Y + backPackRect.Height - 40,
            40, 40);

        return FindItemInArea(frame, backpackTemplate, rect) != null;
    }

    public static bool IsBackpackEmpty(Mat frame, Mat backpackTemplate, Rect backPackRect)
    {
        var rect = new Rect(
            backPackRect.X,
            backPackRect.Y,
            40, 40);

        return FindItemInArea(frame, backpackTemplate, rect) != null;
    }
}
