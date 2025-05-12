﻿using FunscriptToolbox.Core.MotionVectors;
using FunscriptToolbox.Core.MotionVectors.PluginMessages;
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
        private readonly MotionVectorsFileReader r_mvsReader;
        private readonly FrameAnalyser r_originalFrameAnalyser;
        private readonly Bitmap[] r_vectorBitmaps;
        private readonly Bitmap r_snapshot;
        private FrameAnalyser m_currentLearnFromScriptFrameAnalyser;
        private FrameAnalyser m_currentManualFrameAnalyser;

        public FrameAnalyser FinalFrameAnalyser { get; private set; }

        public MotionVectorsEditor(
            Task<byte[]> snapshotContent,
            MotionVectorsFileReader mvsReader, 
            CreateRulesPluginRequest createRulesRequest)
        {
            r_mvsReader = mvsReader;
            r_originalFrameAnalyser = createRulesRequest.CreateInitialFrameAnalyser(mvsReader);
            r_vectorBitmaps = CreateVectorBitmaps(r_mvsReader.FrameLayout.CellWidth, r_mvsReader.FrameLayout.CellHeight);
            
            var snapshotContentResult = snapshotContent.Result;
            if (snapshotContentResult != null)
            {
                using (MemoryStream ms = new MemoryStream(snapshotContent.Result))
                {
                    r_snapshot = new Bitmap(ms);
                }
            }
            else
            {
                r_snapshot = new Bitmap(r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);
            }

            m_currentManualFrameAnalyser = r_originalFrameAnalyser;
            this.FinalFrameAnalyser = null;

            InitializeComponent();

            ActivitySlider.Value = createRulesRequest.LearnFromAction_DefaultActivityFilter;
            QualitySlider.Value = createRulesRequest.LearnFromAction_DefaultQualityFilter;
            MinPercentageSlider.Value = createRulesRequest.LearnFromAction_DefaultMinimumPercentageFilter;
            this.Topmost = createRulesRequest.LearnFromAction_TopMostUI;

            ScreenShot.Width = r_mvsReader.VideoWidth;
            ScreenShot.Height = r_mvsReader.VideoHeight;
            VirtualCanvas.ExtentSize = new System.Windows.Size(r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);
        }

        private static Bitmap[] CreateVectorBitmaps(int cellWidth, int cellHeight)
        {
            var vectorBitmaps = new Bitmap[MotionVectorsHelper.NbBaseDirection];
            for (int i = 0; i < vectorBitmaps.Length; i++)
            {
                var vectorBitmap = new Bitmap(cellWidth, cellHeight);
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
                    matrix.Scale(0.85f * (cellWidth / 2), 0.85f * (cellHeight / 2), MatrixOrder.Append);
                    matrix.RotateAt((i + 6) * (360 / vectorBitmaps.Length), new PointF(0, 0), MatrixOrder.Append);
                    matrix.Translate(cellWidth / 2, cellHeight / 2, MatrixOrder.Append);
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
            if (analyser == null) return;

            var scaledBitmap = new Bitmap(r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);
            using (var graphics = Graphics.FromImage(scaledBitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(r_snapshot, 0, 0, r_mvsReader.VideoWidth, r_mvsReader.VideoHeight);

                foreach (var rule in analyser.Rules)
                {
                    var vectorBitmap = r_vectorBitmaps[rule.Direction];
                    var y = rule.Index / analyser.FrameLayout.NbColumns;
                    var x = rule.Index % analyser.FrameLayout.NbColumns;
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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.Activate();
        }

        private int? GetActivity() => int.TryParse(this.ActivityTextBox.Text, out var value) 
            ? (int?)value 
            : null;
        private int? GetQuality() => int.TryParse(this.QualityTextBox.Text, out var value) 
            ? (int?)value 
            : null;
        private double? GetMinPercentage() => double.TryParse(this.MinPercentageTextBox.Text, out var value)
            ? (double?)value
            : null;
        private byte? GetDirection() => int.TryParse(this.DirectionTextBox.Text, out var value) 
            ? (byte?) (value % MotionVectorsHelper.NbBaseDirection)
            : null;

        private void ActivitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ActivitySlider.Value = Math.Round(ActivitySlider.Value);
        }
        private void QualitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            QualitySlider.Value = Math.Round(QualitySlider.Value);
        }
        private void MinPercentageChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            MinPercentageSlider.Value = Math.Round(MinPercentageSlider.Value, 2);
            MinPercentageSlider.SmallChange = (MinPercentageSlider.Value <= 2) ? 0.1 : 1.0;
        }
        private async void LearnFromScriptFilterChanged(object sender, TextChangedEventArgs e)
        {
            var activity = GetActivity();
            var quality = GetQuality();
            var minPercentage = GetMinPercentage();
            if (activity != null && quality != null && minPercentage != null)
            {
                m_currentLearnFromScriptFrameAnalyser = r_originalFrameAnalyser.Filter(
                    activity.Value,
                    quality.Value,
                    minPercentage.Value);
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentLearnFromScriptFrameAnalyser));
                });
            }
        }

        private async void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (TabControl.SelectedItem == ManualTab)
            {
                var selection = VirtualCanvas.SelectionRectangle;

                var direction = m_currentManualFrameAnalyser.Rules.Select(f => f.Direction).FirstOrDefault();
                var rules = new List<BlocAnalyserRule>();
                for (int indexBlocY = 0; indexBlocY < r_mvsReader.FrameLayout.NbRows; indexBlocY++)
                {
                    var blocY = indexBlocY * r_mvsReader.FrameLayout.CellHeight;
                    if (blocY >= selection.Top && blocY < selection.Bottom)
                    {
                        for (int indexBlocX = 0; indexBlocX < r_mvsReader.FrameLayout.NbColumns; indexBlocX++)
                        {
                            var blocX = indexBlocX * r_mvsReader.FrameLayout.CellWidth;
                            if (blocX >= selection.Left && blocX < selection.Right)
                            {
                                rules.Add(
                                    new BlocAnalyserRule(
                                        (ushort)(indexBlocY * r_mvsReader.FrameLayout.NbColumns + indexBlocX), 
                                        GetDirection().Value));
                            }
                        }
                    }
                }

                m_currentManualFrameAnalyser = new FrameAnalyser(r_mvsReader.FrameLayout, rules.ToArray());
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentManualFrameAnalyser));
                });
            }
            else if (TabControl.SelectedItem == LearnFromScriptTab)
            {
                var selection = VirtualCanvas.SelectionRectangle;
                // TODO Filter
            }
        }

        private async void DirectionTextBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var direction = GetDirection();
            if (direction != null)
            {
                m_currentManualFrameAnalyser = new FrameAnalyser(
                    r_mvsReader.FrameLayout, 
                    m_currentManualFrameAnalyser
                        .Rules
                        .Select(r => new BlocAnalyserRule(r.Index, direction.Value))
                        .ToArray());
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentManualFrameAnalyser));
                });
            }
        }

        private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TabItem selectedTab = (TabItem)TabControl.SelectedItem;
            if (selectedTab == LearnFromScriptTab)
            {
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateImage(m_currentLearnFromScriptFrameAnalyser));
                });
            }
            else if (selectedTab == ManualTab)
            {
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

        private void TestA_Click(object sender, RoutedEventArgs e)
        {

        }

        private void TestB_Click(object sender, RoutedEventArgs e)
        {

        }

        private void AcceptMotionVectors_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}
