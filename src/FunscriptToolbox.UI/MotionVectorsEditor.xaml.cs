using FunscriptToolbox.Core.MotionVectors;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace FunscriptToolbox.UI
{
    /// <summary>
    /// Interaction logic for MotionVectorsEditor.xaml
    /// </summary>
    public partial class MotionVectorsEditor : Window
    {
        private readonly TimeSpan r_videoTime;
        private readonly MotionVectorsFileReader r_mvsReader;
        private readonly FrameAnalyser r_originalFrameAnalyser;
        private readonly Bitmap r_snapshot;
        private readonly Bitmap[] r_vectorBitmaps;
        private FrameAnalyser m_currentLearnFromScriptFrameAnalyser;
        private FrameAnalyser m_currentManualFrameAnalyser;

        public FrameAnalyser FinalFrameAnalyser { get; private set; }

        public MotionVectorsEditor(byte[] snapshotContent, TimeSpan videoTime, MotionVectorsFileReader mvsReader, FrameAnalyser originalFrameAnalyser)
        {
            using (MemoryStream ms = new MemoryStream(snapshotContent))
            {
                r_snapshot = new Bitmap(ms);
            }
            r_videoTime = videoTime;
            r_mvsReader = mvsReader;
            r_originalFrameAnalyser = originalFrameAnalyser ?? new FrameAnalyser(r_mvsReader.NbBlocX, r_mvsReader.NbBlocY, Array.Empty<BlocAnalyserRule>());
            m_currentLearnFromScriptFrameAnalyser = r_originalFrameAnalyser.Filter(50, 70); // TODO
            m_currentManualFrameAnalyser = r_originalFrameAnalyser;
            this.FinalFrameAnalyser = null;

            InitializeComponent();

            ScreenShot.Width = r_mvsReader.VideoWidth;
            ScreenShot.Height = r_mvsReader.VideoHeight;
            VirtualCanvas.ExtentSize = new System.Windows.Size(r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);

            r_vectorBitmaps = CreateVectorBitmaps(r_mvsReader.BlocSize);
            
            UpdateImage(m_currentLearnFromScriptFrameAnalyser);
        }

        private static Bitmap[] CreateVectorBitmaps(int blocSize)
        {
            var vectorBitmaps = new Bitmap[MotionVectorsHelper.NbBaseDirection];
            for (int i = 0; i < vectorBitmaps.Length; i++)
            {
                var vectorBitmap = new Bitmap(blocSize, blocSize);
                using (var graphics = Graphics.FromImage(vectorBitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    var path = new GraphicsPath();
                    path.AddPolygon(
                        new[] {
                        new PointF(0f, 1f),
                        new PointF(0.88575f, 0.25675f),
                        new PointF(0.684875f, 0.017375f),
                        new PointF(0.1875f, 0.461f),
                        new PointF(0.1875f, -1f),
                        new PointF(-0.1875f, -1f),
                        new PointF(-0.1875f, 0.461f),
                        new PointF(-0.684875f, 0.017375f),
                        new PointF(-0.88575f, 0.25675f),
                        new PointF(0f, 1f)
                        });
                    var matrix = new Matrix();
                    matrix.Scale(0.85f * (blocSize / 2), 0.85f * (blocSize / 2), MatrixOrder.Append);
                    matrix.RotateAt((i + 6) * (360 / vectorBitmaps.Length), new PointF(0, 0), MatrixOrder.Append);
                    matrix.Translate(blocSize / 2, blocSize / 2, MatrixOrder.Append);
                    path.Transform(matrix);

                    var brush = new SolidBrush(Color.Green);
                    graphics.FillPath(brush, path);

                    var pen = new Pen(Color.Green, 0f);
                    graphics.DrawPath(pen, path);
                }
                vectorBitmaps[i] = vectorBitmap;
            }
            return vectorBitmaps;
        }

        private void UpdateImage(FrameAnalyser analyser)
        {
            // TODO Allow to cancel update if they are pilled up in threads.

            var scaledBitmap = new Bitmap(r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);
            using (var graphics = Graphics.FromImage(scaledBitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(r_snapshot, 0, 0, r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);

                foreach (var rule in analyser.Rules)
                {
                    var vectorBitmap = r_vectorBitmaps[rule.Direction];
                    var y = rule.Index / r_originalFrameAnalyser.NbBlocX;
                    var x = rule.Index % r_originalFrameAnalyser.NbBlocX;
                    graphics.DrawImage(
                        vectorBitmap, 
                        new Rectangle(x * vectorBitmap.Width, y * vectorBitmap.Height, vectorBitmap.Width, vectorBitmap.Height));
                }
            }

            using (var memory = new MemoryStream())
            {
                scaledBitmap.Save(memory, ImageFormat.Png);
                memory.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                ScreenShot.Source = bitmapImage;
            }
        }

        private void ActivitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ActivitySlider != null)
            {
                ActivitySlider.Value = Math.Round(ActivitySlider.Value);
            }
        }

        private async void ActivityTextBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (int.TryParse(this.ActivityTextBox.Text, out var value))
            {
                m_currentLearnFromScriptFrameAnalyser = r_originalFrameAnalyser.Filter(value, m_currentLearnFromScriptFrameAnalyser.QualityLevel);
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentLearnFromScriptFrameAnalyser));
                });
            }
        }
        private void QualityTextBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (QualitySlider != null)
            {
                QualitySlider.Value = Math.Round(QualitySlider.Value);
            }
        }

        private async void QualitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (int.TryParse(this.QualityTextBox.Text, out var value))
            {
                m_currentLearnFromScriptFrameAnalyser = r_originalFrameAnalyser.Filter(m_currentLearnFromScriptFrameAnalyser.ActivityLevel, value);
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentLearnFromScriptFrameAnalyser));
                });
            }
        }

        private async void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            tabControl.SelectedItem = ManualTab;
            {
                var selection = VirtualCanvas.SelectionRectangle;

                var direction = m_currentManualFrameAnalyser.Rules.Select(f => f.Direction).FirstOrDefault();
                var rules = new List<BlocAnalyserRule>();
                for (int indexBlocY = 0; indexBlocY < r_mvsReader.NbBlocY; indexBlocY++)
                {
                    var blocY = indexBlocY * r_mvsReader.BlocSize;
                    if (blocY >= selection.Top && blocY < selection.Bottom)
                    {
                        for (int indexBlocX = 0; indexBlocX < r_mvsReader.NbBlocX; indexBlocX++)
                        {
                            var blocX = indexBlocX * r_mvsReader.BlocSize;
                            if (blocX >= selection.Left && blocX < selection.Right)
                            {
                                rules.Add(new BlocAnalyserRule((ushort)(indexBlocY * r_mvsReader.NbBlocX + indexBlocX), 0));
                            }
                        }
                    }
                }

                m_currentManualFrameAnalyser = new FrameAnalyser(r_mvsReader.NbBlocX, r_mvsReader.NbBlocY, rules.ToArray());
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentManualFrameAnalyser));
                });
            }
        }

        private async void AngleTextBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (int.TryParse(this.AngleTextBox.Text, out var value))
            {
                var newDirection = (byte)(value % MotionVectorsHelper.NbBaseDirection);
                m_currentManualFrameAnalyser = new FrameAnalyser(
                    r_mvsReader.NbBlocX, 
                    r_mvsReader.NbBlocY, 
                    m_currentManualFrameAnalyser.Rules.Select(r => new BlocAnalyserRule(r.Index, newDirection)).ToArray());
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentManualFrameAnalyser));
                });
            }
        }

        private void AcceptLearnFromScript_Click(object sender, RoutedEventArgs e)
        {
            this.FinalFrameAnalyser = m_currentLearnFromScriptFrameAnalyser;
            this.Close();
        }

        private void AcceptManual_Click(object sender, RoutedEventArgs e)
        {
            this.FinalFrameAnalyser = m_currentManualFrameAnalyser;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.FinalFrameAnalyser = null;
            this.Close();
        }
    }
}
