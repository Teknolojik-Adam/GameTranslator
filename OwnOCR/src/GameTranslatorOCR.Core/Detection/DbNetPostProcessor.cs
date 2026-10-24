using GameTranslatorOCR.Core.Models;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GameTranslatorOCR.Core.Detection;

/// <summary>
/// DBNet olasılık haritasını dikdörtgen metin kutularına dönüştüren başlangıç
/// postprocess'i. Eğimli yazı için daha sonra kontur/poligon sürümü eklenecek.
/// </summary>
public static class DbNetPostProcessor
{
    public static IReadOnlyList<OcrBox> Extract(
        Tensor<float> output, int originalWidth, int originalHeight,
        float pixelThreshold = 0.30f, float boxThreshold = 0.48f)
    {
        var dimensions = output.Dimensions;
        if (dimensions.Length != 4 || dimensions[0] != 1 || dimensions[1] != 1)
            throw new InvalidDataException("Detector çıktısı [1,1,H,W] olmalı.");

        int h = dimensions[2];
        int w = dimensions[3];
        if (h < 1 || w < 1 || (long)h * w > 16_000_000)
            throw new InvalidDataException("Detector çıktısı geçersiz boyutta.");

        int size = h * w;
        var mask = new bool[size];
        var scores = new float[size];
        // ONNX Runtime returns a contiguous DenseTensor in normal operation.
        // Reading its flat buffer avoids repeated bounds/rank indexing in the
        // 2D detector probability map. Fallback preserves other Tensor types.
        if (output is DenseTensor<float> dense)
        {
            dense.Buffer.Span.Slice(0, size).CopyTo(scores);
        }
        else
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                scores[y * w + x] = output[0, 0, y, x];
        }

        for (int i = 0; i < size; i++)
            mask[i] = scores[i] > pixelThreshold;

        // Harf parçalarını birleştirmek için yatay 2px dilation.
        var connected = new bool[size];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int index = y * w + x;
            if (!mask[index]) continue;
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx;
                if (nx >= 0 && nx < w)
                    connected[y * w + nx] = true;
            }
        }

        var seen = new bool[size];
        var queue = new int[size];
        var boxes = new List<OcrBox>();

        for (int start = 0; start < size; start++)
        {
            if (!connected[start] || seen[start]) continue;

            int head = 0, tail = 0;
            queue[tail++] = start;
            seen[start] = true;
            int minX = w, minY = h, maxX = 0, maxY = 0;
            int confidentCount = 0;
            double scoreSum = 0;

            while (head < tail)
            {
                int index = queue[head++];
                int y = index / w, x = index % w;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                if (mask[index])
                {
                    scoreSum += scores[index];
                    confidentCount++;
                }

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int next = ny * w + nx;
                    if (!seen[next] && connected[next])
                    {
                        seen[next] = true;
                        queue[tail++] = next;
                    }
                }
            }

            int bw = maxX - minX + 1;
            int bh = maxY - minY + 1;
            if (confidentCount < 8 || bw < 4 || bh < 4 ||
                scoreSum / Math.Max(1, confidentCount) < boxThreshold)
                continue;

            // DBNet konturunun etrafını crop sırasında pay bırakacak şekilde genişlet.
            int padX = Math.Max(3, bw / 14);
            int padY = Math.Max(2, bh / 5);
            int x1 = Math.Max(0, minX - padX);
            int y1 = Math.Max(0, minY - padY);
            int x2 = Math.Min(w, maxX + padX + 1);
            int y2 = Math.Min(h, maxY + padY + 1);
            boxes.Add(new OcrBox(x1, y1, x2 - x1, y2 - y1));
        }

        // Yan yana harf/kelime bölgelerini satır kutularında birleştir.
        bool merged;
        do
        {
            merged = false;
            boxes.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            for (int i = 0; i < boxes.Count && !merged; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i];
                var b = boxes[j];
                int verticalOverlap = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) -
                    Math.Max(a.Y, b.Y));
                int minHeight = Math.Min(a.Height, b.Height);
                int gap = Math.Max(0, Math.Max(a.X - (b.X + b.Width), b.X - (a.X + a.Width)));
                if (verticalOverlap < minHeight * 0.6 || gap > Math.Max(10, minHeight * 0.9))
                    continue;

                int x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y);
                int x2 = Math.Max(a.X + a.Width, b.X + b.Width);
                int y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
                boxes[i] = new OcrBox(x1, y1, x2 - x1, y2 - y1);
                boxes.RemoveAt(j);
                merged = true;
                break;
            }
        } while (merged);

        return boxes.OrderBy(b => b.Y).ThenBy(b => b.X)
            .Take(200)
            .Select(b =>
            {
                int x1 = Math.Clamp((int)Math.Floor((double)b.X * originalWidth / w), 0, originalWidth);
                int y1 = Math.Clamp((int)Math.Floor((double)b.Y * originalHeight / h), 0, originalHeight);
                int x2 = Math.Clamp((int)Math.Ceiling((double)(b.X + b.Width) * originalWidth / w), 0, originalWidth);
                int y2 = Math.Clamp((int)Math.Ceiling((double)(b.Y + b.Height) * originalHeight / h), 0, originalHeight);
                return new OcrBox(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
            })
            .Where(box => box.IsValid)
            .ToArray();
    }
}
