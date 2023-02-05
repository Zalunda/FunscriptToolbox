using FunscriptToolbox.Core;
using Mpv.NET.Player;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace FunscriptToolbox.UI
{
    /// <summary>
    /// Interaction logic for MotionVectorsEditor.xaml
    /// </summary>
    public partial class MotionVectorsEditor : Window
    {
        private readonly string r_videoFileName;
        private readonly string r_outputParametersFileName;
        private readonly Input r_input;
        private readonly MotionVectorsFileReader r_reader;
        private readonly MpvPlayer r_player;

        private System.Windows.Point m_temporaryStartPoint;
        private System.Windows.Shapes.Rectangle m_temporarySelection;
        private FunscriptAction[] m_currentActions;

        private class Input
        {
            public double CurrentTime { get; set; }
            public double EndTime { get; set; }
            public FunscriptAction[] Actions { get; set; }
        };

        public MotionVectorsEditor(string videoFileName, string inputParametersFileName, string outputParametersFileName)
        {
            InitializeComponent();

            r_videoFileName = videoFileName;
            r_input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(inputParametersFileName));
            r_outputParametersFileName = outputParametersFileName;

            // TODO: Use funscript name
            var mvsFile = Path.ChangeExtension(
                videoFileName.Replace(".mvs-visual", ""), ".mvs");
            r_reader = new MotionVectorsFileReader(
                mvsFile,
                1000);

            var applicationFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            r_player = new MpvPlayer(PlayerHost.Handle, Path.Combine(applicationFolder, "mpv-1.dll"));

            PlayerGrid.Width = r_reader.VideoWidth;
            PlayerGrid.Height = r_reader.VideoHeight;
            PlayerScreenShot.Width = r_reader.VideoWidth;
            PlayerScreenShot.Height = r_reader.VideoHeight;
            PlayerKindOfOverlay.Width = r_reader.VideoWidth;
            PlayerKindOfOverlay.Width = r_reader.VideoHeight;
        }

        private void WindowsOnLoaded(object sender, RoutedEventArgs e)
        {
            r_player.MediaLoaded += (s, arg) => {
                r_player.Position = TimeSpan.FromMilliseconds(r_input.CurrentTime);
            };
            r_player.Load(r_videoFileName);
            r_player.Resume();
            r_player.Pause();
        }

        private void WindowOnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            r_player.Dispose();

            if (m_currentActions != null)
            {
                var content = JsonConvert.SerializeObject(new
                {
                    Actions = m_currentActions.ToArray()
                });
                File.WriteAllText(r_outputParametersFileName, content);
            }
        }

        private void playButton_Click(object sender, RoutedEventArgs e)
        {
            Player.Visibility = Visibility.Visible;
            PlayerKindOfOverlay.Visibility = Visibility.Collapsed;
            r_player.Resume();
        }

        private void frameButton_Click(object sender, RoutedEventArgs e)
        {
            Player.Visibility = Visibility.Visible;
            PlayerKindOfOverlay.Visibility = Visibility.Collapsed;
            r_player.NextFrame();
        }

        private void PlayerHost_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            SwitchToKindOfOverlay();
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            m_temporaryStartPoint = e.GetPosition(PlayerKindOfOverlay);
            m_temporarySelection = new System.Windows.Shapes.Rectangle
            {
                Stroke = System.Windows.Media.Brushes.Black,
                StrokeThickness = 2
            };
            Canvas.SetLeft(m_temporarySelection, m_temporaryStartPoint.X);
            Canvas.SetTop(m_temporarySelection, m_temporaryStartPoint.Y);

            PlayerKindOfOverlay.Children.Add(m_temporarySelection);
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

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(PlayerKindOfOverlay);
            var x = Math.Min(pos.X, m_temporaryStartPoint.X);
            var y = Math.Min(pos.Y, m_temporaryStartPoint.Y);
            var width = Math.Max(pos.X, m_temporaryStartPoint.X) - x;
            var height = Math.Max(pos.Y, m_temporaryStartPoint.Y) - y;

            CreateActions(x, y, width, height);
        }

        private void CreateActions(double x, double y, double width, double height)
        {
            var rules = new List<BlocAnalyserRule>();
            for (int indexBlocY = 0; indexBlocY < r_reader.NbBlocY; indexBlocY++)
            {
                var blocY = indexBlocY * r_reader.BlocSize;
                if (blocY >= y && blocY < y + height)
                {
                    for (int indexBlocX = 0; indexBlocX < r_reader.NbBlocX; indexBlocX++)
                    {
                        var blocX = indexBlocX * r_reader.BlocSize;
                        if (blocX >= x && blocX < x + width)
                        {
                            rules.Add(new BlocAnalyserRule((ushort)(indexBlocY * r_reader.NbBlocX + indexBlocX), 0));
                        }
                    }
                }
            }

            var frameAnalyser = new FrameAnalyser(rules.ToArray());
            foreach (var frame in r_reader.ReadFrames(r_player.Position, TimeSpan.FromMilliseconds(r_input.EndTime)))
            {
                frameAnalyser.AddFrameData(frame);
            }
            m_currentActions = frameAnalyser.GetFinalActions();
        }

        private void SwitchToKindOfOverlay()
        {
            r_player.Pause();

            var size = new System.Drawing.Size(PlayerHost.Width, PlayerHost.Height);
            Bitmap bmpScreenshot = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);

            // Create a Graphics object from the Bitmap
            Graphics gfxScreenshot = Graphics.FromImage(bmpScreenshot);

            var ptscreen = PlayerHost.PointToScreen(new System.Drawing.Point(0, 0));

            // Take a screenshot of the window and save it to the Bitmap
            gfxScreenshot.CopyFromScreen(ptscreen.X,
                                         ptscreen.Y,
                                         0, 0,
                                         size,
                                         CopyPixelOperation.SourceCopy);

            var bitmaps = new Bitmap[MotionVectorsHelper.NbBaseAngles];
            for (int i = 0; i < bitmaps.Length; i++)
            {
                var bitmap = new Bitmap(16, 16);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    var path = new GraphicsPath();
                    var pen = new Pen(Color.Green, 0f);
                    var brush = new SolidBrush(Color.Green);
                    path.AddPolygon(
                        new[] {
                        new PointF(0, 8),
                        new PointF(7.086f, 2.054f),
                        new PointF(5.479f, 0.139f),
                        new PointF(1.5f, 3.688f),
                        new PointF(1.5f, -8f),
                        new PointF(-1.5f, -8f),
                        new PointF(-1.5f, 3.688f),
                        new PointF(-5.479f, 0.139f),
                        new PointF(-7.086f, 2.054f),
                        new PointF(0, 8)
                        });
                    var matrix = new Matrix();
                    matrix.Scale(0.85f, 0.85f, MatrixOrder.Append);
                    matrix.RotateAt(i * (360 / bitmaps.Length), new PointF(0, 0), MatrixOrder.Append);
                    matrix.Translate(8, 8, MatrixOrder.Append);
                    path.Transform(matrix);
                    graphics.FillPath(brush, path);
                    graphics.DrawPath(pen, path);
                }
                bitmaps[i] = bitmap;
            }

            var analyse = FrameAnalyserGenerator.CreateFromScriptSequence(r_reader, r_input.Actions);

            var scaledBitmap = new Bitmap(r_reader.VideoWidth, r_reader.VideoHeight);
            using (var graphics = Graphics.FromImage(scaledBitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(bmpScreenshot, 0, 0, r_reader.VideoWidth, r_reader.VideoHeight);
                for (int y = 0, index = 0; y < r_reader.NbBlocY; y++)
                {
                    for (int x = 0; x < r_reader.NbBlocX; x++, index++)
                    {
                        var rule = analyse.Rules[index];
                        if (rule.Activity > 50 && rule.Quality > 85)
                        {
                            var smallBitmap = bitmaps[rule.Direction];
                            graphics.DrawImage(smallBitmap, new Rectangle(x * 16, y * 16, 16, 16));
                        }
                    }
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
                Player.Visibility = Visibility.Collapsed;
                PlayerKindOfOverlay.Visibility = Visibility.Visible;
            }
        }
    }
}
