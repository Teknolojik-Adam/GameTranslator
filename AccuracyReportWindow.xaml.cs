using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace P5S_ceviri
{
    public partial class AccuracyReportWindow : Window
    {
        private readonly OcrAccuracyReport _report;

        public AccuracyReportWindow(OcrAccuracyReport report)
        {
            InitializeComponent();
            _report = report;
            LoadReportData();
        }

        private void LoadReportData()
        {
            // Set report date
            ReportDateText.Text = $"Generated on: {_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";

            // Load summary
            TotalTestsText.Text = _report.TotalTests.ToString();
            OverallAccuracyText.Text = $"{_report.OverallAccuracy:P1}";
            BestEngineText.Text = _report.BestPerformingEngine.ToString();

            // Load engine performance
            EnginePerformanceListView.ItemsSource = _report.EngineSummaries.Values;

            // Load detailed statistics
            CharacterAccuracyListView.ItemsSource = _report.EngineSummaries.Values;
            WordAccuracyListView.ItemsSource = _report.EngineSummaries.Values;
            LineAccuracyListView.ItemsSource = _report.EngineSummaries.Values;

            // Load recommendations
            var recommendations = _report.Recommendations.Select(kvp => 
                new { Key = kvp.Key, Value = kvp.Value?.ToString() ?? "" }).ToList();
            RecommendationsListView.ItemsSource = recommendations;
        }

        private void ExportReportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export OCR Accuracy Report",
                    Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    DefaultExt = "txt"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    var extension = Path.GetExtension(saveDialog.FileName).ToLower();
                    
                    if (extension == ".csv")
                    {
                        ExportToCsv(saveDialog.FileName);
                    }
                    else
                    {
                        ExportToText(saveDialog.FileName);
                    }

                    MessageBox.Show("Report exported successfully!", "Export Complete", 
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting report: {ex.Message}", "Export Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportToText(string fileName)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("OCR Accuracy Report");
            sb.AppendLine("==================");
            sb.AppendLine($"Generated on: {_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // Summary
            sb.AppendLine("SUMMARY");
            sb.AppendLine("-------");
            sb.AppendLine($"Total Tests: {_report.TotalTests}");
            sb.AppendLine($"Overall Accuracy: {_report.OverallAccuracy:P1}");
            sb.AppendLine($"Best Engine: {_report.BestPerformingEngine}");
            sb.AppendLine();

            // Engine Performance
            sb.AppendLine("ENGINE PERFORMANCE");
            sb.AppendLine("------------------");
            sb.AppendLine("Engine\t\tTests\tAvg Accuracy\tBest Accuracy\tAvg Time (ms)");
            sb.AppendLine("------\t\t-----\t------------\t-------------\t-------------");
            
            foreach (var summary in _report.EngineSummaries.Values)
            {
                sb.AppendLine($"{summary.EngineType}\t\t{summary.TestCount}\t{summary.AverageAccuracy:P1}\t\t{summary.BestAccuracy:P1}\t\t{summary.AverageProcessingTime:F1}");
            }
            sb.AppendLine();

            // Detailed Statistics
            sb.AppendLine("DETAILED STATISTICS");
            sb.AppendLine("-------------------");
            foreach (var summary in _report.EngineSummaries.Values)
            {
                sb.AppendLine($"{summary.EngineType}:");
                sb.AppendLine($"  Character Accuracy: {summary.CharacterAccuracy:P1}");
                sb.AppendLine($"  Word Accuracy: {summary.WordAccuracy:P1}");
                sb.AppendLine($"  Line Accuracy: {summary.LineAccuracy:P1}");
                sb.AppendLine();
            }

            // Recommendations
            sb.AppendLine("RECOMMENDATIONS");
            sb.AppendLine("---------------");
            foreach (var recommendation in _report.Recommendations)
            {
                sb.AppendLine($"{recommendation.Key}: {recommendation.Value}");
            }

            File.WriteAllText(fileName, sb.ToString());
        }

        private void ExportToCsv(string fileName)
        {
            var sb = new StringBuilder();
            
            // Header
            sb.AppendLine("Report Type,Value");
            sb.AppendLine($"Generated Date,{_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Total Tests,{_report.TotalTests}");
            sb.AppendLine($"Overall Accuracy,{_report.OverallAccuracy:P1}");
            sb.AppendLine($"Best Engine,{_report.BestPerformingEngine}");
            sb.AppendLine();

            // Engine Performance
            sb.AppendLine("Engine Performance");
            sb.AppendLine("Engine,Test Count,Average Accuracy,Best Accuracy,Average Processing Time (ms)");
            
            foreach (var summary in _report.EngineSummaries.Values)
            {
                sb.AppendLine($"{summary.EngineType},{summary.TestCount},{summary.AverageAccuracy:P1},{summary.BestAccuracy:P1},{summary.AverageProcessingTime:F1}");
            }
            sb.AppendLine();

            // Detailed Statistics
            sb.AppendLine("Detailed Statistics");
            sb.AppendLine("Engine,Character Accuracy,Word Accuracy,Line Accuracy");
            
            foreach (var summary in _report.EngineSummaries.Values)
            {
                sb.AppendLine($"{summary.EngineType},{summary.CharacterAccuracy:P1},{summary.WordAccuracy:P1},{summary.LineAccuracy:P1}");
            }
            sb.AppendLine();

            // Recommendations
            sb.AppendLine("Recommendations");
            sb.AppendLine("Category,Recommendation");
            
            foreach (var recommendation in _report.Recommendations)
            {
                sb.AppendLine($"{recommendation.Key},{recommendation.Value}");
            }

            File.WriteAllText(fileName, sb.ToString());
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
