using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FunscriptToolbox.UI.SpeakerCorrection
{
    public class SpeakerStatsViewModel : INotifyPropertyChanged
    {
        private Key? _assignedKey;

        public string DisplayName { get; set; }
        public int TotalCount { get; set; }
        public int FinalizedCount { get; set; }

        public Key? AssignedKey
        {
            get => _assignedKey;
            set
            {
                if (_assignedKey != value)
                {
                    _assignedKey = value;
                    OnPropertyChanged(); // Notifies the UI that AssignedKey has changed
                    OnPropertyChanged(nameof(AssignedKeyDisplay)); // Notifies the UI that the display text has also changed
                }
            }
        }

        // This property is for formatting the key nicely in the UI
        public string AssignedKeyDisplay
        {
            get
            {
                if (!AssignedKey.HasValue)
                {
                    return "[ ]"; // Display empty brackets if no key is assigned
                }

                // --- MODIFIED: Replaced C# 8.0 switch expression with a C# 7.3 compatible switch statement ---
                switch (AssignedKey.Value)
                {
                    case Key.D1:
                        return "1";
                    case Key.D2:
                        return "2";
                    case Key.D3:
                        return "3";
                    case Key.D4:
                        return "4";
                    case Key.D5:
                        return "5";
                    case Key.Left:
                        return "←";
                    case Key.Right:
                        return "→";
                    case Key.Up:
                        return "↑";
                    default:
                        return AssignedKey.Value.ToString(); // Fallback for other keys
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}