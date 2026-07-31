using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public static class JsonCorrectionHelper
    {
        /// <summary>
        /// Pops up a blocking UI. Returns the edited string if the user clicks Retry, or null if they Cancel/Close.
        /// Expected line and column numbers are 1-based.
        /// </summary>
        public static string PromptForFix(string errorMessage, string jsonText)
        {
            // Standardize line endings for Windows forms controls
            string normalizedJson = jsonText;

            using (var form = new Form())
            {
                form.Text = "AI JSON Parsing Error - Manual Correction";
                form.Size = new Size(1000, 700);
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.ShowIcon = false;

                // --- Status Bar (Bottom) ---
                var statusStrip = new StatusStrip();
                var lblPosition = new ToolStripStatusLabel { Text = "Line 1, Position 1" };
                statusStrip.Items.Add(lblPosition);

                // --- Buttons Panel (Bottom, above status strip) ---
                var panelButtons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Height = 40,
                    Padding = new Padding(5)
                };

                var btnCancel = new Button { Text = "Cancel (Fail)", DialogResult = DialogResult.Cancel, Width = 120 };
                var btnRetry = new Button { Text = "Retry Parsing", DialogResult = DialogResult.OK, Width = 120 };

                panelButtons.Controls.Add(btnCancel);
                panelButtons.Controls.Add(btnRetry);

                // --- Error Message (Top) ---
                var txtError = new TextBox
                {
                    Dock = DockStyle.Top,
                    Multiline = true,
                    ReadOnly = true,
                    ForeColor = Color.Red,
                    Font = new Font("Consolas", 10, FontStyle.Bold),
                    Text = errorMessage,
                    Height = 50, // Fixed small height
                    ScrollBars = ScrollBars.Vertical
                };

                // --- JSON Editor (Middle) ---
                var txtJson = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 10),
                    Text = normalizedJson,
                    ScrollBars = RichTextBoxScrollBars.Both,
                    WordWrap = false,
                    AcceptsTab = true,
                    HideSelection = false // Keeps highlight when clicking buttons
                };

                // Track Line & Column position
                txtJson.SelectionChanged += (s, e) =>
                {
                    int index = txtJson.SelectionStart;
                    int line = txtJson.GetLineFromCharIndex(index);
                    int firstCharOfLine = txtJson.GetFirstCharIndexFromLine(line);
                    int column = index - firstCharOfLine;

                    // JsonErrors are typically 1-based index
                    lblPosition.Text = $"Line {line + 1}, Position {column + 1}";
                };

                // Docking order matters (Bottom-most added first)
                form.Controls.Add(txtJson);      // 4. Fills remaining space
                form.Controls.Add(txtError);     // 3. Pinned to Top
                form.Controls.Add(panelButtons); // 2. Pinned to Bottom
                form.Controls.Add(statusStrip);  // 1. Pinned to Bottom-most

                form.CancelButton = btnCancel;

                form.Shown += (s, e) =>
                {
                    // Force the window to the front
                    form.BringToFront();
                    form.Activate();
                    
                    int targetLine = -1;
                    int targetPos = -1;

                    var match = Regex.Match(errorMessage, @"line (\d+),\s*position (\d+)", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out targetLine) && int.TryParse(match.Groups[2].Value, out targetPos))
                    {
                        // Convert from 1-based error to 0-based RichTextBox indices
                        targetLine -= 1;
                        targetPos -= 1;

                        if (targetLine >= 0 && targetLine < txtJson.Lines.Length)
                        {
                            int firstCharIndex = txtJson.GetFirstCharIndexFromLine(targetLine);
                            int targetIndex = firstCharIndex + targetPos;

                            if (targetIndex >= 0 && targetIndex <= txtJson.TextLength)
                            {
                                txtJson.Select(targetIndex, 0); // Put cursor exactly at the character
                            }
                        }
                    }
                    else
                    {
                        // Fallback: put cursor at the end
                        txtJson.SelectionStart = txtJson.Text.Length;
                    }

                    txtJson.ScrollToCaret();
                    txtJson.Focus();
                };

                if (form.ShowDialog() == DialogResult.OK)
                {
                    return txtJson.Text;
                }

                return null;
            }
        }
    }
}