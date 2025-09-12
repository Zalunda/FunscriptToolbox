using FunscriptToolbox.Core.Infra;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FunscriptToolbox.UI.SpeakerCorrection
{
    public partial class SpeakerCorrectionTool : Window, INotifyPropertyChanged
    {
        // --- New Fields for Key Assignment ---
        private bool _isWaitingForKeyAssignment = false;
        private SpeakerStatsViewModel _speakerToAssignKey = null;

        private class UndoState
        {
            public SpeakerCorrectionWorkItem Item { get; }
            public string PreviousFinalSpeaker { get; }
            public List<string> PreviousPotentialSpeakers { get; }
            public bool PreviousSavedState { get; }

            public UndoState(SpeakerCorrectionWorkItem item)
            {
                Item = item;
                PreviousFinalSpeaker = item.FinalSpeaker;
                PreviousPotentialSpeakers = new List<string>(item.PotentialSpeakers); // Create a copy
            }
        }
        private readonly List<UndoState> _undoHistory = new List<UndoState>();
        private const int MaxUndoSteps = 20;

        private readonly Func<TimeSpan, (string fullpath, TimeSpan newPosition)> r_getPathAndPositionFunc;
        private readonly Action<SpeakerCorrectionWorkItem> r_saveCallBack;
        private readonly Action<SpeakerCorrectionWorkItem> r_undoCallBack;
        public List<SpeakerCorrectionWorkItem> WorkItems { get; }
        private List<SpeakerCorrectionWorkItem> _validationQueue;
        private int _currentItemIndex = -1;
        private DispatcherTimer _loopTimer;
        private SpeakerStatsViewModel _lastSelectedItem;
        private bool _isMediaReady = false;
        private bool _isPaused = false;

        // --- Properties ---
        public ObservableCollection<SpeakerStatsViewModel> SpeakerStats { get; set; }

        private SpeakerCorrectionWorkItem _currentItem;
        private TimeSpan _extendSegmentDuration = TimeSpan.Zero;

        public SpeakerCorrectionWorkItem CurrentItem
        {
            get => _currentItem;
            set { _currentItem = value; OnPropertyChanged(); UpdateUIForCurrentItem(); }
        }
        private TimeSpan _currentAdjustedStartTime;

        // --- Constructor ---
        public SpeakerCorrectionTool() : this(null, null, null, null) { }

        public SpeakerCorrectionTool(
            IEnumerable<SpeakerCorrectionWorkItem> items,
            Func<TimeSpan, (string fullpath, TimeSpan newPosition)> getPathAndPositionFunc,
            Action<SpeakerCorrectionWorkItem> saveCallBack,
            Action<SpeakerCorrectionWorkItem> undoCallBack)
        {
            InitializeComponent();
            DataContext = this;
            SpeakerStats = new ObservableCollection<SpeakerStatsViewModel>();
            SpeakersAndGroupsList.ItemsSource = SpeakerStats;

            _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _loopTimer.Tick += LoopTimer_Tick;

            VideoPlayer.MediaOpened += VideoPlayer_MediaOpened;
            VideoPlayer.MediaFailed += VideoPlayer_MediaFailed;

            this.Closing += SpeakerCorrectionTool_Closing;
            this.PreviewKeyDown += SpeakerCorrectionTool_PreviewKeyDown;

            r_getPathAndPositionFunc = getPathAndPositionFunc;
            r_saveCallBack = saveCallBack;
            r_undoCallBack = undoCallBack;
            this.WorkItems = new List<SpeakerCorrectionWorkItem>(items ?? Array.Empty<SpeakerCorrectionWorkItem>());

            RefreshSpeakerLists(new[] { Key.D1, Key.D2, Key.D3, Key.D4, Key.D5 });
            StatusText.Text = "Ready. Select a speaker and click 'Start Validation'.";
            UpdateUndoButtonState();
        }

        private void SpeakerCorrectionTool_Closing(object sender, CancelEventArgs e)
        {
            VideoPlayer.Stop();
            VideoPlayer.Source = null;
        }

        private void SpeakerCorrectionTool_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // If the event is coming from a TextBox, ignore all shortcuts and let the user type.
            if (e.OriginalSource is TextBox)
            {
                return;
            }

            // --- Key Assignment Mode ---
            // Handle this first, as it's a special mode.
            if (_isWaitingForKeyAssignment)
            {
                HandleKeyAssignment(e.Key);
                e.Handled = true;
                return;
            }

            // --- Global Shortcuts ---
            // Handle standard shortcuts like Undo.
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                return;
            }

            // --- Validation Mode Shortcuts ---
            // Only process these if we are in the middle of validation.
            if (CurrentItem != null)
            {
                // Check for dynamic speaker hotkeys
                var assignedSpeaker = SpeakerStats.FirstOrDefault(s => s.AssignedKey.HasValue && s.AssignedKey.Value == e.Key);
                if (assignedSpeaker != null)
                {
                    AssignSpeaker(assignedSpeaker.DisplayName);
                    e.Handled = true;
                    return;
                }

                // Check for other validation-specific keys
                switch (e.Key)
                {
                    case Key.S:
                        SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        e.Handled = true;
                        break;
                    case Key.M:
                        ApplyManualButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        e.Handled = true;
                        break;
                    case Key.Space:
                        PauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        e.Handled = true;
                        break;
                }
            }
        }

        #region Key Assignment Logic

        private void AssignKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SpeakerStatsViewModel speaker)
            {
                _isWaitingForKeyAssignment = true;
                _speakerToAssignKey = speaker;
                StatusText.Text = $"Press any key to assign to '{speaker.DisplayName}'. Press Esc to cancel.";
            }
        }

        private void HandleKeyAssignment(Key key)
        {
            // Cancel assignment
            if (key == Key.Escape)
            {
                StatusText.Text = "Key assignment cancelled.";
                _isWaitingForKeyAssignment = false;
                _speakerToAssignKey = null;
                return;
            }

            // Check if another speaker already has this key and remove it
            var existingSpeaker = SpeakerStats.FirstOrDefault(s => s.AssignedKey.HasValue && s.AssignedKey.Value == key);
            if (existingSpeaker != null && existingSpeaker != _speakerToAssignKey)
            {
                // This key is taken; remove the old assignment.
                existingSpeaker.AssignedKey = null;
            }

            // Un-assign if the user presses the same key again
            if (_speakerToAssignKey.AssignedKey.HasValue && _speakerToAssignKey.AssignedKey.Value == key)
            {
                _speakerToAssignKey.AssignedKey = null;
                StatusText.Text = $"Unassigned key from '{_speakerToAssignKey.DisplayName}'.";
            }
            else // Assign the new key
            {
                _speakerToAssignKey.AssignedKey = key;
                StatusText.Text = $"Assigned key '{key}' to '{_speakerToAssignKey.DisplayName}'.";
            }

            _isWaitingForKeyAssignment = false;
            _speakerToAssignKey = null;
        }

        #endregion


        #region Undo Logic

        private void PushUndoState(SpeakerCorrectionWorkItem item)
        {
            if (item == null) return;
            _undoHistory.Add(new UndoState(item));
            if (_undoHistory.Count > MaxUndoSteps)
            {
                _undoHistory.RemoveAt(0); // Remove the oldest entry
            }
            UpdateUndoButtonState();
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_undoHistory.Count == 0) return;

            var lastState = _undoHistory.Last();
            _undoHistory.RemoveAt(_undoHistory.Count - 1);

            var itemToRestore = lastState.Item;
            itemToRestore.FinalSpeaker = lastState.PreviousFinalSpeaker;
            itemToRestore.PotentialSpeakers = new List<string>(lastState.PreviousPotentialSpeakers);
            r_undoCallBack(itemToRestore);

            StatusText.Text = $"Undid action for speaker '{itemToRestore.DetectedSpeaker}'.";
            RefreshSpeakerLists();

            if (_validationQueue != null && _validationQueue.Contains(itemToRestore))
            {
                CurrentItem = itemToRestore;
                _currentItemIndex = _validationQueue.IndexOf(itemToRestore);
                UpdateUIForCurrentItem();
            }
            UpdateUndoButtonState();
        }

        private void UpdateUndoButtonState()
        {
            UndoButton.IsEnabled = _undoHistory.Any();
        }

        #endregion

        #region Video Player Logic & Events
        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isMediaReady = true;
            VideoPlaceholder.Visibility = Visibility.Collapsed;
            StatusText.Text = "Video loaded successfully. Ready for validation.";

            VideoPlayer.Play();
            VideoPlayer.Pause();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _isMediaReady = false;
            VideoPlaceholder.Visibility = Visibility.Visible;
            VideoPlaceholder.Text = "Video failed to load.";
            MessageBox.Show($"Video playback failed: {e.ErrorException.Message}\n\nThis is often caused by missing video codecs. Try installing the K-Lite Codec Pack.", "Media Error");
            StatusText.Text = "Error: Could not play video.";
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMediaReady) return;
            _isPaused = !_isPaused;
            if (_isPaused)
            {
                VideoPlayer.Pause();
                _loopTimer.Stop();
                PauseButton.Content = "▶ Play (Space)";
            }
            else
            {
                VideoPlayer.Position = TimeSpanExtensions.Max(TimeSpan.Zero, _currentAdjustedStartTime - _extendSegmentDuration);
                VideoPlayer.Play();
                _loopTimer.Start();
                PauseButton.Content = "❚❚ Pause (Space)";
            }
        }

        private void PlayCurrentSegment()
        {
            if (CurrentItem == null)
            {
                _loopTimer.Stop();
                VideoPlayer.Stop();
                return;
            }

            // Reset the player's state before playing.
            VideoPlayer.Stop();
            
            _isPaused = false;
            PauseButton.Content = "❚❚ Pause (Space)";

            var (fullpath, adjustedStartTime) = r_getPathAndPositionFunc(CurrentItem.StartTime);
            if (fullpath != FilePathText.Text)
            {
                VideoPlayer.Source = new Uri(Path.GetFullPath(fullpath), UriKind.Absolute);
                VideoPlaceholder.Text = "Loading video...";
                FilePathText.Text = fullpath;
            }
            _currentAdjustedStartTime = adjustedStartTime;

            VideoPlayer.Position = TimeSpanExtensions.Max(TimeSpan.Zero, _currentAdjustedStartTime - _extendSegmentDuration);
            VideoPlayer.Play();
            _loopTimer.Start();
        }

        private void LoopTimer_Tick(object sender, EventArgs e)
        {
            if (_isPaused || CurrentItem == null || !_isMediaReady) return;

            if (VideoPlayer.Position >= _currentAdjustedStartTime + CurrentItem.Duration + _extendSegmentDuration)
            {
                VideoPlayer.Position = TimeSpanExtensions.Max(TimeSpan.Zero, _currentAdjustedStartTime - _extendSegmentDuration);
            }
        }
        #endregion

        #region Data Loading and Preparation
        private void RefreshSpeakerLists(IEnumerable<Key> defaultAssignments = null)
        {
            // Discover names from FinalSpeaker as well
            var potentialSpeakers = this.WorkItems.SelectMany(item => item.PotentialSpeakers).Distinct().ToArray();
            var allNames = potentialSpeakers
                .Concat(this.WorkItems.Select(item => item.DetectedSpeaker))
                .Concat(this.WorkItems.Select(item => item.FinalSpeaker))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct();

            var allKnownNames = allNames.Union(SpeakerStats.Select(s => s.DisplayName)).Distinct().ToList();
            var iterateOnDefaultAssignments = defaultAssignments?.GetEnumerator();

            // Add new speakers or update existing ones
            foreach (var name in allKnownNames)
            {
                int currentTotal = this.WorkItems.Count(item => (item.FinalSpeaker ?? item.DetectedSpeaker) == name);
                int currentFinalized = this.WorkItems.Count(item => item.FinalSpeaker == name);

                var existingStat = SpeakerStats.FirstOrDefault(s => s.DisplayName == name);
                if (existingStat != null)
                {
                    existingStat.TotalCount = currentTotal;
                    existingStat.FinalizedCount = currentFinalized;
                }
                else
                {
                    if (iterateOnDefaultAssignments?.MoveNext() == false)
                    {
                        iterateOnDefaultAssignments = null;
                    }
                    SpeakerStats.Add(new SpeakerStatsViewModel
                    {
                        DisplayName = name,
                        TotalCount = currentTotal,
                        FinalizedCount = currentFinalized,
                        IsManuallyAdded = potentialSpeakers.Contains(name),
                        AssignedKey = iterateOnDefaultAssignments?.Current
                    }); ;
                }
            }

            SpeakerStats.Add(new SpeakerStatsViewModel
            {
                DisplayName = "Unknown",
                TotalCount = this.WorkItems.Count(item => (item.FinalSpeaker ?? item.DetectedSpeaker) == null),
                FinalizedCount = 0
            });

            // After all counts are updated, find any speakers that are empty AND were not manually added.
            var speakersToRemove = SpeakerStats.Where(s => s.TotalCount == 0 && !s.IsManuallyAdded).ToList();
            foreach (var speaker in speakersToRemove)
            {
                SpeakerStats.Remove(speaker);
            }
        }
        #endregion

        #region Speaker Management (Left Panel)
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            string newName = AddSpeakerTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show("Please enter a name for the new speaker.", "Input Error");
                return;
            }
            if (SpeakerStats.Any(s => s.DisplayName.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"A speaker named '{newName}' already exists.", "Duplicate Name");
                return;
            }

            SpeakerStats.Add(new SpeakerStatsViewModel
            {
                DisplayName = newName,
                TotalCount = 0,
                FinalizedCount = 0,
                IsManuallyAdded = true
            });

            AddSpeakerTextBox.Clear();
            StatusText.Text = $"Added new speaker: '{newName}'.";
        }

        private void SpeakersAndGroupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeakersAndGroupsList.SelectedItems.Count == 1)
            {
                var selectedSpeaker = (SpeakerStatsViewModel)SpeakersAndGroupsList.SelectedItem;
                RenameTextBox.Text = selectedSpeaker.DisplayName;
                _lastSelectedItem = selectedSpeaker;
            }
            else
            {
                RenameTextBox.Clear();
            }

            if (e.AddedItems.Count > 0)
            {
                _lastSelectedItem = (SpeakerStatsViewModel)e.AddedItems[e.AddedItems.Count - 1];
            }
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (SpeakersAndGroupsList.SelectedItems.Count != 1)
            {
                MessageBox.Show("Please select a single speaker to rename.", "Rename Error");
                return;
            }

            var selectedSpeaker = (SpeakerStatsViewModel)SpeakersAndGroupsList.SelectedItem;
            string oldName = selectedSpeaker.DisplayName;
            string newName = RenameTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(newName) || oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) return;

            if (SpeakerStats.Any(s => s.DisplayName.Equals(newName, StringComparison.OrdinalIgnoreCase) && s != selectedSpeaker))
            {
                MessageBox.Show($"A speaker named '{newName}' already exists.", "Duplicate Name");
                return;
            }

            foreach (var item in this.WorkItems)
            {
                if (item.DetectedSpeaker == oldName) item.DetectedSpeaker = newName;
                if (item.FinalSpeaker == oldName)
                {
                    r_undoCallBack(item);
                    item.FinalSpeaker = newName;
                    r_saveCallBack(item);
                }
                for (int i = 0; i < item.PotentialSpeakers.Count; i++)
                {
                    if (item.PotentialSpeakers[i] == oldName) item.PotentialSpeakers[i] = newName;
                }
            }

            // Just update the display name directly in the view model
            selectedSpeaker.DisplayName = newName;

            RenameTextBox.Clear();
            RefreshSpeakerLists();
            StatusText.Text = $"Renamed '{oldName}' to '{newName}'.";
        }

        private void MergeButton_Click(object sender, RoutedEventArgs e)
        {
            if (SpeakersAndGroupsList.SelectedItems.Count < 2)
            {
                MessageBox.Show("Please select two or more speakers to merge.", "Merge Error");
                return;
            }

            var mergeTarget = _lastSelectedItem;
            var itemsToMerge = SpeakersAndGroupsList.SelectedItems.Cast<SpeakerStatsViewModel>()
                                     .Where(s => s != mergeTarget).ToList();

            if (mergeTarget == null)
            {
                MessageBox.Show("Could not determine the target speaker for the merge. Please re-select the items.", "Merge Error");
                return;
            }

            string targetName = mergeTarget.DisplayName;
            foreach (var source in itemsToMerge)
            {
                string sourceName = source.DisplayName;
                foreach (var item in this.WorkItems)
                {
                    if (item.DetectedSpeaker == sourceName) item.DetectedSpeaker = targetName;
                    if (item.FinalSpeaker == sourceName) item.FinalSpeaker = targetName;
                }
            }
            RefreshSpeakerLists();
            StatusText.Text = $"Merged {itemsToMerge.Count} speaker(s) into '{targetName}'.";
        }
        #endregion

        #region Validation Workflow

        private void ValidateSpeakerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SpeakerStatsViewModel selectedSpeaker)
            {
                _validationQueue = this.WorkItems
                    .Where(item => (item.DetectedSpeaker == selectedSpeaker.DisplayName)
                                   && string.IsNullOrEmpty(item.FinalSpeaker))
                    .OrderBy(item => item.StartTime)
                    .ToList();

                if (_validationQueue.Count == 0)
                {
                    MessageBox.Show($"There are no unvalidated items for '{selectedSpeaker.DisplayName}'.", "Validation Complete");
                    return;
                }

                _currentItemIndex = -1;
                ValidationProgress.Value = 0;
                ValidationProgress.Maximum = _validationQueue.Count;

                MoveNext();
                StatusText.Text = $"Validation started for speaker: {selectedSpeaker.DisplayName}";
            }
        }

        private void StartAllValidationButton_Click(object sender, RoutedEventArgs e)
        {
            _validationQueue = this.WorkItems
                .Where(item => string.IsNullOrEmpty(item.FinalSpeaker))
                .OrderBy(item => item.StartTime)
                .ToList();

            if (_validationQueue.Count == 0)
            {
                MessageBox.Show("There are no unvalidated items remaining.", "Validation Complete");
                return;
            }

            _currentItemIndex = -1;
            ValidationProgress.Value = 0;
            ValidationProgress.Maximum = _validationQueue.Count;

            MoveNext();
            StatusText.Text = "Validation started for all unvalidated items.";
        }

        private void MoveNext()
        {
            RefreshSpeakerLists();
            _extendSegmentDuration = TimeSpan.Zero;
            _currentItemIndex++;
            if (_currentItemIndex < _validationQueue.Count)
            {
                CurrentItem = _validationQueue[_currentItemIndex];
            }
            else
            {
                CurrentItem = null;
                VideoPlayer.Stop();
                StatusText.Text = "Validation for this group is complete!";
                MessageBox.Show("You have completed the validation for this group.", "Validation Complete");
            }
        }

        private void UpdateUIForCurrentItem()
        {
            if (CurrentItem == null)
            {
                TimeRangeText.Text = string.Empty;
                PotentialSpeakersText.Text = string.Empty;
                AIDetectedText.Text = string.Empty;
                SegmentIndexText.Text = string.Empty;
                ManualSpeakerTextBox.Clear();
                if (ValidationProgress.Maximum > 0) ValidationProgress.Value = ValidationProgress.Maximum;
                _loopTimer.Stop();
                return;
            }

            TimeRangeText.Text = $"{CurrentItem.StartTime:hh\\:mm\\:ss\\.fff} -> {CurrentItem.EndTime:hh\\:mm\\:ss\\.fff}";
            PotentialSpeakersText.Text = string.Join(", ", CurrentItem.PotentialSpeakers);
            AIDetectedText.Text = CurrentItem.DetectedSpeaker;
            SegmentIndexText.Text = $"{_currentItemIndex + 1} / {_validationQueue.Count}";
            ValidationProgress.Value = _currentItemIndex + 1;
            ProgressText.Text = $"{_currentItemIndex + 1} / {_validationQueue.Count}";
            ManualSpeakerTextBox.Clear();

            PlayCurrentSegment();
        }

        private void AssignSpeaker(string speakerName)
        {
            if (CurrentItem == null || string.IsNullOrWhiteSpace(speakerName)) return;
            PushUndoState(CurrentItem);
            CurrentItem.FinalSpeaker = speakerName;
            r_saveCallBack(CurrentItem);
            MoveNext();
        }

        private void ExtendButton_Click(object sender, RoutedEventArgs e)
        {
            _extendSegmentDuration += TimeSpan.FromSeconds(0.5);

        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentItem == null) return;
            MoveNext();
        }

        private void ApplyManualButton_Click(object sender, RoutedEventArgs e)
        {
            AssignSpeaker(ManualSpeakerTextBox.Text);
        }
        #endregion

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        #endregion
    }
}