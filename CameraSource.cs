using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIWeather
{
    public class CameraSource : INotifyPropertyChanged
    {
        private string _username = "";
        private string _password = "";
        private string _protocol = "rtsp://";
        private string _mediaUrl = "";
        private bool _isRunning = false;
        private bool _isLoading = false;

        public string Username
        {
            get => _username;
            set
            {
                if (_username != value)
                {
                    _username = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                if (_password != value)
                {
                    _password = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Protocol
        {
            get => _protocol;
            set
            {
                if (_protocol != value)
                {
                    _protocol = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FullUrl));
                }
            }
        }

        public string MediaUrl
        {
            get => _mediaUrl;
            set
            {
                if (_mediaUrl != value)
                {
                    _mediaUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FullUrl));
                }
            }
        }

        public string FullUrl
        {
            get
            {
                if (string.IsNullOrEmpty(MediaUrl))
                    return "";
                
                // Strip protocol from MediaUrl if user accidentally included it
                var cleanMediaUrl = MediaUrl;
                if (cleanMediaUrl.Contains("://"))
                {
                    var idx = cleanMediaUrl.IndexOf("://");
                    cleanMediaUrl = cleanMediaUrl.Substring(idx + 3);
                }
                
                return $"{Protocol}{cleanMediaUrl}";
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
        }
    }
}
