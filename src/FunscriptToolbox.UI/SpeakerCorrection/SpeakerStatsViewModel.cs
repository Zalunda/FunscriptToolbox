using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FunscriptToolbox.UI.SpeakerCorrection
{
    public class SpeakerStatsViewModel : INotifyPropertyChanged
    {
        private Key? _assignedKey;
        private int _finalizedCount;
        private int _totalCount;

        public string DisplayName { get; set; }

        // This flag will prevent the speaker from being auto-removed if its count is zero.
        public bool IsManuallyAdded { get; set; }

        public int TotalCount
        {
            get => _totalCount;
            set
            {
                if (_totalCount != value)
                {
                    _totalCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public int FinalizedCount
        {
            get => _finalizedCount;
            set
            {
                if (_finalizedCount != value)
                {
                    _finalizedCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public Key? AssignedKey
        {
            get => _assignedKey;
            set
            {
                if (_assignedKey != value)
                {
                    _assignedKey = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AssignedKeyDisplay));
                }
            }
        }

        // This property is for formatting the key nicely in the UI
        public string AssignedKeyDisplay
        {
            get
            {
                // ... No changes in this property ...
                if (!AssignedKey.HasValue)
                {
                    return "[ ]";
                }

                Key key = AssignedKey.Value;

                if (key >= Key.D0 && key <= Key.D9)
                {
                    return key.ToString().Substring(1);
                }

                if (key >= Key.NumPad0 && key <= Key.NumPad9)
                {
                    return "Num" + key.ToString().Substring(6);
                }

                switch (key)
                {
                    case Key.Left:
                        return "←";
                    case Key.Right:
                        return "→";
                    case Key.Up:
                        return "↑";
                    case Key.Down:
                        return "↓";
                    case Key.OemMinus:
                        return "-";
                    case Key.OemPlus:
                        return "=";
                    case Key.Subtract:
                        return "Num-";
                    case Key.Add:
                        return "Num+";
                }

                return key.ToString();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}