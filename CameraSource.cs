using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIWeather.Models;

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
        private CaptureMode _captureMode = CaptureMode.RTSPStream;
        private string _indiDeviceName = "";
        private string _folderPath = "";

        public CaptureMode CaptureMode
        {
            get => _captureMode;
            set
            {
                if (_captureMode != value)
                {
                    _captureMode = value;
                    OnPropertyChanged();
                }
            }
        }

        public string INDIDeviceName
        {
            get => _indiDeviceName;
            set
            {
                if (_indiDeviceName != value)
                {
                    _indiDeviceName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FolderPath
        {
            get => _folderPath;
            set
            {
                if (_folderPath != value)
                {
                    _folderPath = value;
                    OnPropertyChanged();
                }
            }
        }

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
                    System.Diagnostics.Debug.WriteLine($"[CameraSource] IsRunning changed to: {value} for {MediaUrl}");
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
