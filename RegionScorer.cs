using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameTranslatorUltimate
{
    public static class RegionScorer
    {
        private static readonly ConcurrentDictionary<string, int> _textRepeatCount =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, DateTime> _textLastSeen =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex _timestampRegex =
            new Regex(@"\b\d{1,2}:\d{2}(:\d{2})?\b", RegexOptions.Compiled);

        private static readonly Regex _percentRegex =
            new Regex(@"^\s*\d{1,3}\s*%\s*$", RegexOptions.Compiled);

        public static double ScoreRegion(Rectangle region, string text, int frameWidth, int frameHeight, Rectangle? manualRegion)
        {
            if (region.IsEmpty || frameWidth <= 0 || frameHeight <= 0)
                return -100;

            double yCenter = region.Y + region.Height / 2.0;
            double normalizedY = yCenter / frameHeight;
            double widthRatio = region.Width / (double)frameWidth;
            double areaRatio = (region.Width * (double)region.Height) / (frameWidth * (double)frameHeight);

            double score = 0;

            if (normalizedY >= 0.60)
                score += 30;
            if (normalizedY >= 0.72)
                score += 20;
            if (normalizedY >= 0.78)
                score += 10;
            if (normalizedY <= 0.20)
                score -= 50;
            else if (normalizedY <= 0.30)
                score -= 25;

            if (widthRatio >= 0.25)
                score += 15;
            if (widthRatio >= 0.45)
                score += 10;
            if (widthRatio >= 0.75)
                score -= 5;
            if (widthRatio < 0.10)
                score -= 10;
            if (areaRatio > 0.50)
                score -= 40;
            if (areaRatio > 0.85)
                score -= 40;

            if (!string.IsNullOrWhiteSpace(text))
            {
                string trimmed = text.Trim();
                if (trimmed.Length >= 5 && trimmed.Length <= 160)
                    score += 15;
                else if (trimmed.Length < 3)
                    score -= 15;
                else if (trimmed.Length > 180)
                    score -= 10;

                int wordCount = trimmed.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (wordCount >= 2 && wordCount <= 25)
                    score += 10;
                else if (wordCount > 30)
                    score -= 10;
                if (wordCount >= 3 && trimmed.Contains(" "))
                    score += 5;

                if (IsLikelyStaticUiText(trimmed))
                    score -= 30;
                if (IsRecentlyRepeatedStaticText(trimmed))
                    score -= 25;
            }
            else
            {
                score -= 20;
            }

            if (manualRegion.HasValue)
            {
                double inter = IntersectionRatio(region, manualRegion.Value);
                if (inter > 0.50)
                    score += 100;
                else if (inter > 0.20)
                    score += 60;
                else if (inter > 0.05)
                    score += 20;
                else
                    score -= 80;
            }

            bool isTopCorner = normalizedY < 0.25 && (region.X < frameWidth * 0.20 || region.Right > frameWidth * 0.80);
            if (isTopCorner && widthRatio < 0.30)
                score -= 20;

            return score;
        }

        public static bool IsLikelyStaticUiText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();

            if (_timestampRegex.IsMatch(trimmed))
                return true;
            if (_percentRegex.IsMatch(trimmed))
                return true;

            if (trimmed.Length > 65)
                return true;

            string lower = trimmed.ToLowerInvariant();
            if (lower.Contains("ultimate") && trimmed.Length > 25)
                return true;
            if (lower.Contains("despicable") && trimmed.Length > 20)
                return true;
            if (lower.Contains("movie moments") || lower.Contains("special |"))
                return true;

            string[] hudKeywords = { "subscribe", "views", "channel", "hd ", "live", "playlist", "watch later", "share" };
            foreach (var kw in hudKeywords)
            {
                if (lower.Contains(kw))
                    return true;
            }

            if (trimmed.Length > 45)
            {
                int wordCount = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (wordCount >= 7 && char.IsUpper(trimmed[0]))
                {
                    int upperWords = 0;
                    foreach (var w in trimmed.Split(' '))
                    {
                        if (!string.IsNullOrEmpty(w) && char.IsUpper(w[0]))
                            upperWords++;
                    }
                    if (upperWords >= wordCount * 0.6)
                        return true;
                }
            }

            if (Regex.IsMatch(trimmed, @"^\d+\s*/\s*\d+$"))
                return true;

            return false;
        }

        public static bool IsRecentlyRepeatedStaticText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string key = text.Trim().ToLowerInvariant();
            if (key.Length < 4)
                return false;

            DateTime now = DateTime.UtcNow;

            _textLastSeen.AddOrUpdate(key, now, (k, v) => now);
            int count = _textRepeatCount.AddOrUpdate(key, 1, (k, v) => v + 1);

            CleanupOldEntries(now);

            if (count >= 3)
            {
                if (_textLastSeen.TryGetValue(key, out DateTime last))
                {
                    if ((now - last).TotalSeconds < 60)
                        return true;
                }
                return count >= 5;
            }

            return false;
        }

        public static void RegisterTextSeen(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            string key = text.Trim().ToLowerInvariant();
            DateTime now = DateTime.UtcNow;
            _textLastSeen.AddOrUpdate(key, now, (k, v) => now);
            _textRepeatCount.AddOrUpdate(key, 1, (k, v) => v + 1);
            CleanupOldEntries(now);
        }

        private static void CleanupOldEntries(DateTime now)
        {
            if (_textLastSeen.Count > 200)
            {
                var toRemove = _textLastSeen.Where(kv => (now - kv.Value).TotalSeconds > 120).Select(kv => kv.Key).ToList();
                foreach (var k in toRemove)
                {
                    _textLastSeen.TryRemove(k, out _);
                    _textRepeatCount.TryRemove(k, out _);
                }
            }
        }

        public static List<RegionProcessResult> RankAndFilter(
            List<RegionProcessResult> candidates,
            double keepThreshold = 0)
        {
            if (candidates == null || candidates.Count == 0)
                return new List<RegionProcessResult>();

            var ordered = candidates.OrderByDescending(c => c.Score).ToList();
            double max = ordered[0].Score;

            var filtered = ordered.Where(c => c.Score > keepThreshold && c.Score >= max - 30).ToList();

            if (filtered.Count == 0)
            {
                filtered = ordered.Take(1).ToList();
                if (filtered[0].Score < -80)
                    return new List<RegionProcessResult>();
            }

            return filtered.OrderBy(c => c.Region.Top).ThenBy(c => c.Region.Left).ToList();
        }

        public static double IntersectionRatio(Rectangle a, Rectangle b)
        {
            Rectangle inter = Rectangle.Intersect(a, b);
            if (inter.Width <= 0 || inter.Height <= 0)
                return 0;
            double interArea = inter.Width * (double)inter.Height;
            double smaller = Math.Min(a.Width * (double)a.Height, b.Width * (double)b.Height);
            if (smaller <= 0) return 0;
            return interArea / smaller;
        }

        public static double IntersectionOverUnion(Rectangle a, Rectangle b)
        {
            Rectangle inter = Rectangle.Intersect(a, b);
            if (inter.Width <= 0 || inter.Height <= 0) return 0;
            double interArea = inter.Width * (double)inter.Height;
            double union = a.Width * (double)a.Height + b.Width * (double)b.Height - interArea;
            if (union <= 0) return 0;
            return interArea / union;
        }
    }
}
