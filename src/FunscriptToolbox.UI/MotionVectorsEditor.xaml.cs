using FunscriptToolbox.Core.MotionVectors;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
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
        private FrameAnalyser m_currentFrameAnalyser;

        public MotionVectorsEditor(byte[] snapshotContent, TimeSpan videoTime, MotionVectorsFileReader mvsReader, FrameAnalyser originalFrameAnalyser)
        {
            using (MemoryStream ms = new MemoryStream(snapshotContent))
            {
                r_snapshot = new Bitmap(ms);
            }
            r_videoTime = videoTime;
            r_mvsReader = mvsReader;
            r_originalFrameAnalyser = originalFrameAnalyser;
            m_currentFrameAnalyser = originalFrameAnalyser;

            InitializeComponent();

            PlayerScreenShot.Width = r_mvsReader.VideoWidth;
            PlayerScreenShot.Height = r_mvsReader.VideoHeight;
            VirtualCanvas.Width = r_mvsReader.VideoWidth;
            VirtualCanvas.Width = r_mvsReader.VideoHeight;

            r_vectorBitmaps = CreateVectorBitmaps(r_mvsReader.BlocSize);

            UpdateImage();
        }

        private static Bitmap[] CreateVectorBitmaps(int blocSize)
        {
            var vectorBitmaps = new Bitmap[MotionVectorsHelper.NbBaseAngles];
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
                    matrix.RotateAt(i * (360 / vectorBitmaps.Length), new PointF(0, 0), MatrixOrder.Append);
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

        private void UpdateImage()
        {
            var scaledBitmap = new Bitmap(r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);
            using (var graphics = Graphics.FromImage(scaledBitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(r_snapshot, 0, 0, r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);

                foreach (var rule in m_currentFrameAnalyser.Rules)
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

                PlayerScreenShot.Source = bitmapImage;
            }
        }

        private int currentActivity = 0;
        private int currentQuality = 0;

        private void ActivitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ActivitySlider != null)
            {
                ActivitySlider.Value = Math.Round(ActivitySlider.Value);
            }
        }

        private void ActivityTextBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (int.TryParse(this.ActivityTextBox.Text, out var value))
            {
                currentActivity = value;
                m_currentFrameAnalyser = r_originalFrameAnalyser.Filter(currentActivity, currentQuality);
                UpdateImage();
            }
        }

        private void QualityTextBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (QualitySlider != null)
            {
                QualitySlider.Value = Math.Round(QualitySlider.Value);
            }
        }

        private void QualitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (int.TryParse(this.QualityTextBox.Text, out var value))
            {
                currentQuality = value;
                m_currentFrameAnalyser = r_originalFrameAnalyser.Filter(currentActivity, currentQuality);
                UpdateImage();
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            //if (e.LeftButton == MouseButtonState.Pressed)
            //{
            //    var pos = e.GetPosition(PlayerKindOfOverlay);
            //    var x = Math.Min(pos.X, m_temporaryStartPoint.X);
            //    var y = Math.Min(pos.Y, m_temporaryStartPoint.Y);
            //    var width = Math.Max(pos.X, m_temporaryStartPoint.X) - x;
            //    var height = Math.Max(pos.Y, m_temporaryStartPoint.Y) - y;

            //    m_temporarySelection.Width = width;
            //    m_temporarySelection.Height = height;

            //    Canvas.SetLeft(m_temporarySelection, x);
            //    Canvas.SetTop(m_temporarySelection, y);
            //}
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {

        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {

        }
    }
}
