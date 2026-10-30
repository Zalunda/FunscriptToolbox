using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace FunscriptToolbox.UI
{
    public partial class JsonCorrectionWindow : Window
    {
        public string ResultJson { get; private set; }

        public JsonCorrectionWindow(string errorMessage, string jsonText)
        {
            InitializeComponent();

            TxtError.Text = errorMessage ?? string.Empty;
            TxtJson.Text = jsonText ?? string.Empty;

            Loaded += JsonCorrectionWindow_Loaded;
        }

        private void JsonCorrectionWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtJson.Focus();

            var match = Regex.Match(TxtError.Text, @"line (\d+),\s*position (\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int line) && int.TryParse(match.Groups[2].Value, out int pos))
            {
                line -= 1; // 1-based to 0-based
                pos -= 1;

                if (line >= 0 && line < TxtJson.LineCount)
                {
                    int lineIndex = TxtJson.GetCharacterIndexFromLineIndex(line);
                    int targetIndex = lineIndex + pos;
                    if (targetIndex >= 0 && targetIndex <= TxtJson.Text.Length)
                    {
                        TxtJson.Select(targetIndex, 0);
                        return;
                    }
                }
            }

            TxtJson.Select(TxtJson.Text.Length, 0);
        }

        private void TxtJson_SelectionChanged(object sender, RoutedEventArgs e)
        {
            int index = TxtJson.SelectionStart;
            int line = TxtJson.GetLineIndexFromCharacterIndex(index);
            int lineStart = TxtJson.GetCharacterIndexFromLineIndex(line);
            int col = index - lineStart;

            LblPosition.Text = $"Line {line + 1}, Column {col + 1}";
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            ResultJson = TxtJson.Text;
            DialogResult = true;
            Close();
        }

        private void BtnEmpty_Click(object sender, RoutedEventArgs e)
        {
            ResultJson = "[]";
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            ResultJson = null;
            DialogResult = false;
            Close();
        }
    }
}
