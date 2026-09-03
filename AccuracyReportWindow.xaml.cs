using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace GameTranslatorUltimate
{
    public partial class AccuracyReportWindow : Window
    {
        private readonly OcrAccuracyReport _report;

        public AccuracyReportWindow(
            OcrAccuracyReport report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(
                    nameof(report));
            }

            InitializeComponent();

            _report =
                report;

            LoadReportData();
        }

        private void LoadReportData()
        {
            ReportDateText.Text =
                $"Generated on: {_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";

            TotalTestsText.Text =
                _report.TotalTests.ToString();

            OverallAccuracyText.Text =
                _report.OverallAccuracy.ToString("P1");

            BestEngineText.Text =
                _report.BestPerformingEngine.ToString();

            List<EngineAccuracySummary> summaries =
                GetOrderedEngineSummaries();

            EnginePerformanceListView.ItemsSource =
                summaries;

            CharacterAccuracyListView.ItemsSource =
                summaries;

            WordAccuracyListView.ItemsSource =
                summaries;

            LineAccuracyListView.ItemsSource =
                summaries;

            RecommendationsListView.ItemsSource =
                GetRecommendationItems();
        }

        private List<EngineAccuracySummary> GetOrderedEngineSummaries()
        {
            if (_report.EngineSummaries == null ||
                _report.EngineSummaries.Count == 0)
            {
                return new List<EngineAccuracySummary>();
            }

            return _report
                .EngineSummaries
                .Values
                .Where(
                    summary =>
                        summary != null)
                .OrderBy(
                    summary =>
                        summary.EngineType.ToString())
                .ToList();
        }

        private List<RecommendationItem> GetRecommendationItems()
        {
            var items =
                new List<RecommendationItem>();

            if (_report.Recommendations == null ||
                _report.Recommendations.Count == 0)
            {
                return items;
            }

            foreach (KeyValuePair<string, object> pair
                     in _report.Recommendations
                         .OrderBy(
                             item => item.Key))
            {
                items.Add(
                    new RecommendationItem
                    {
                        Key =
                            pair.Key ??
                            string.Empty,

                        Value =
                            pair.Value != null
                                ? pair.Value.ToString()
                                : string.Empty
                    });
            }

            return items;
        }

        private void ExportReportButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                var saveDialog =
                    new Microsoft.Win32.SaveFileDialog
                    {
                        Title =
                            "Export OCR Accuracy Report",

                        Filter =
                            "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",

                        DefaultExt =
                            ".txt",

                        AddExtension =
                            true,

                        OverwritePrompt =
                            true
                    };

                bool? result =
                    saveDialog.ShowDialog(
                        this);

                if (result != true)
                {
                    return;
                }

                string extension =
                    Path.GetExtension(
                            saveDialog.FileName)
                        .ToLowerInvariant();

                if (extension == ".csv")
                {
                    ExportToCsv(
                        saveDialog.FileName);
                }
                else
                {
                    ExportToText(
                        saveDialog.FileName);
                }

                MessageBox.Show(
                    this,
                    "Report exported successfully!",
                    "Export Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Error exporting report: {ex.Message}",
                    "Export Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExportToText(
            string fileName)
        {
            if (string.IsNullOrWhiteSpace(
                fileName))
            {
                throw new ArgumentException(
                    "Dosya adı boş olamaz.",
                    nameof(fileName));
            }

            List<EngineAccuracySummary> summaries =
                GetOrderedEngineSummaries();

            List<RecommendationItem> recommendations =
                GetRecommendationItems();

            var sb =
                new StringBuilder();

            sb.AppendLine(
                "OCR Accuracy Report");

            sb.AppendLine(
                "===================");

            sb.AppendLine(
                $"Generated on: {_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");

            sb.AppendLine();

            sb.AppendLine(
                "SUMMARY");

            sb.AppendLine(
                "-------");

            sb.AppendLine(
                $"Total Tests: {_report.TotalTests}");

            sb.AppendLine(
                $"Overall Accuracy: {_report.OverallAccuracy:P1}");

            sb.AppendLine(
                $"Best Engine: {_report.BestPerformingEngine}");

            sb.AppendLine();

            sb.AppendLine(
                "ENGINE PERFORMANCE");

            sb.AppendLine(
                "------------------");

            sb.AppendLine(
                "Engine\tTests\tAvg Accuracy\tBest Accuracy\tAvg Time (ms)");

            foreach (EngineAccuracySummary summary
                     in summaries)
            {
                sb.AppendLine(
                    string.Format(
                        "{0}\t{1}\t{2:P1}\t{3:P1}\t{4:F1}",
                        summary.EngineType,
                        summary.TestCount,
                        summary.AverageAccuracy,
                        summary.BestAccuracy,
                        summary.AverageProcessingTime));
            }

            sb.AppendLine();

            sb.AppendLine(
                "DETAILED STATISTICS");

            sb.AppendLine(
                "-------------------");

            foreach (EngineAccuracySummary summary
                     in summaries)
            {
                sb.AppendLine(
                    $"{summary.EngineType}:");

                sb.AppendLine(
                    $"  Character Accuracy: {summary.CharacterAccuracy:P1}");

                sb.AppendLine(
                    $"  Word Accuracy: {summary.WordAccuracy:P1}");

                sb.AppendLine(
                    $"  Line Accuracy: {summary.LineAccuracy:P1}");

                sb.AppendLine();
            }

            sb.AppendLine(
                "RECOMMENDATIONS");

            sb.AppendLine(
                "---------------");

            foreach (RecommendationItem recommendation
                     in recommendations)
            {
                sb.AppendLine(
                    $"{recommendation.Key}: {recommendation.Value}");
            }

            File.WriteAllText(
                fileName,
                sb.ToString(),
                new UTF8Encoding(false));
        }

        private void ExportToCsv(
            string fileName)
        {
            if (string.IsNullOrWhiteSpace(
                fileName))
            {
                throw new ArgumentException(
                    "Dosya adı boş olamaz.",
                    nameof(fileName));
            }

            List<EngineAccuracySummary> summaries =
                GetOrderedEngineSummaries();

            List<RecommendationItem> recommendations =
                GetRecommendationItems();

            var sb =
                new StringBuilder();

            AppendCsvRow(
                sb,
                "Report Type",
                "Value");

            AppendCsvRow(
                sb,
                "Generated Date",
                _report.GeneratedAt.ToString(
                    "yyyy-MM-dd HH:mm:ss"));

            AppendCsvRow(
                sb,
                "Total Tests",
                _report.TotalTests.ToString());

            AppendCsvRow(
                sb,
                "Overall Accuracy",
                _report.OverallAccuracy.ToString(
                    "P1"));

            AppendCsvRow(
                sb,
                "Best Engine",
                _report.BestPerformingEngine.ToString());

            sb.AppendLine();

            AppendCsvRow(
                sb,
                "Engine Performance");

            AppendCsvRow(
                sb,
                "Engine",
                "Test Count",
                "Average Accuracy",
                "Best Accuracy",
                "Average Processing Time (ms)");

            foreach (EngineAccuracySummary summary
                     in summaries)
            {
                AppendCsvRow(
                    sb,
                    summary.EngineType.ToString(),
                    summary.TestCount.ToString(),
                    summary.AverageAccuracy.ToString("P1"),
                    summary.BestAccuracy.ToString("P1"),
                    summary.AverageProcessingTime.ToString("F1"));
            }

            sb.AppendLine();

            AppendCsvRow(
                sb,
                "Detailed Statistics");

            AppendCsvRow(
                sb,
                "Engine",
                "Character Accuracy",
                "Word Accuracy",
                "Line Accuracy");

            foreach (EngineAccuracySummary summary
                     in summaries)
            {
                AppendCsvRow(
                    sb,
                    summary.EngineType.ToString(),
                    summary.CharacterAccuracy.ToString("P1"),
                    summary.WordAccuracy.ToString("P1"),
                    summary.LineAccuracy.ToString("P1"));
            }

            sb.AppendLine();

            AppendCsvRow(
                sb,
                "Recommendations");

            AppendCsvRow(
                sb,
                "Category",
                "Recommendation");

            foreach (RecommendationItem recommendation
                     in recommendations)
            {
                AppendCsvRow(
                    sb,
                    recommendation.Key,
                    recommendation.Value);
            }

            File.WriteAllText(
                fileName,
                sb.ToString(),
                new UTF8Encoding(true));
        }

        private static void AppendCsvRow(
            StringBuilder builder,
            params string[] values)
        {
            if (builder == null)
            {
                return;
            }

            if (values == null ||
                values.Length == 0)
            {
                builder.AppendLine();
                return;
            }

            for (int i = 0;
                 i < values.Length;
                 i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(
                    EscapeCsvValue(
                        values[i]));
            }

            builder.AppendLine();
        }

        private static string EscapeCsvValue(
            string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            bool requiresQuotes =
                value.IndexOf(',') >= 0 ||
                value.IndexOf('"') >= 0 ||
                value.IndexOf('\r') >= 0 ||
                value.IndexOf('\n') >= 0;

            if (!requiresQuotes)
            {
                return value;
            }

            return "\"" +
                   value.Replace(
                       "\"",
                       "\"\"") +
                   "\"";
        }

        private void CloseButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }

        private sealed class RecommendationItem
        {
            public string Key { get; set; }

            public string Value { get; set; }

            public RecommendationItem()
            {
                Key =
                    string.Empty;

                Value =
                    string.Empty;
            }
        }
    }
}