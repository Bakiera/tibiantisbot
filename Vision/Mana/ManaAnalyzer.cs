
using OpenCvSharp;
using System.Runtime.InteropServices;
using Point = OpenCvSharp.Point;

namespace Bot.Vision.Mana;

public class ManaAnalyzer
{
    private readonly Mat _template;
    private Rect? _cachedManaRect;
    private readonly string templatePath = "Assets/Templates/Mana.png";
    private const int ManaBarWidth = 86;
    private const double MinMatchConfidence = 0.65;
    private readonly int[] ManaColor = [130];//[107, 115, 97, 94];
    private int _lastManaPercent = 100;
    private int _missCount;

    public ManaAnalyzer()
    {
        _template = Cv2.ImRead(templatePath, ImreadModes.Grayscale);
        if (_template.Empty())
            throw new FileNotFoundException("Mana template not found", templatePath);
    }

    public int ExtractManaPercent(Mat frame)
    {
        var rect = GetManaRect(frame);
        if (rect == null)
        {
            _missCount++;
            if (_missCount % 30 == 1)
                Console.WriteLine($"[ManaAnalyzer] Mana bar not located, using last value ({_lastManaPercent}%)");
            return _lastManaPercent;
        }

        _missCount = 0;

        using var mana = new Mat(frame, rect.Value);
        //Cv2.ImShow("mana Test", mana);
        //Cv2.WaitKey(0); // Wait until a key is pressed
        //Cv2.DestroyAllWindows();

        byte[] data = new byte[mana.Cols];
        Marshal.Copy(mana.Data, data, 0, mana.Cols);
        int filled = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte px = data[i];

            bool isManaPixel = false;
            foreach (var c in ManaColor)
            {
                if (Math.Abs(px - c) <= 3)
                {
                    isManaPixel = true;
                    break;
                }
            }

            if (isManaPixel)
                filled++;
            else if (filled > 0)
                break;
        }

        int percent = (int)Math.Round((double)filled / ManaBarWidth * 100);
        _lastManaPercent = Math.Clamp(percent, 0, 100);
        return _lastManaPercent;
    }

    private Rect? GetManaRect(Mat frame)
    {
        if (_cachedManaRect.HasValue)
            return _cachedManaRect;

        using var result = new Mat();
        Cv2.MatchTemplate(frame, _template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);

        if (maxVal < MinMatchConfidence)
        {
            Console.WriteLine($"[GetManaRect] Mana not found (conf={maxVal:F2})");
            return null;
        }

        if (maxVal < 0.9)
            Console.WriteLine($"[GetManaRect] Low-confidence mana match (conf={maxVal:F2}), using anyway");

        var rect = new Rect(maxLoc.X + 19, maxLoc.Y + 6, ManaBarWidth, 1);

        _cachedManaRect = rect;
        return rect;
    }

    public void Reset()
    {
        _cachedManaRect = null;
        _missCount = 0;
    }
}
