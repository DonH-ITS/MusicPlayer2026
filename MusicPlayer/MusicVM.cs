using System.ComponentModel;
using Plugin.Maui.Audio;

namespace MusicPlayer
{
    // The ViewModel for the music player page.
    // It holds the audio player, the state of the player (playing, position, volume)
    // and the commands the buttons are bound to.
    // The page only displays things: it never touches the audio player directly.
    public class MusicVM : INotifyPropertyChanged
    {
        // The audio player for the currently opened music file (null until a file is picked).
        private IAudioPlayer? _openedMusic;

        // The stream the player is reading from. It must stay open for as long as the player is in use.
        private Stream? _musicStream;

        private string _pickedFilename = "";
        private double _musicPosition = 0;

        // Remembers the volume so a newly opened file keeps the user's chosen volume.
        private double _volume = 1;

        private bool _showControls = false;
        private bool _musicPlaying = false;

        // Used to update the position slider while the music is playing.
        private IDispatcherTimer _timer;

        // Commands the buttons in the XAML are bound to.
        public Command PickFileCommand { get; }
        public Command StartCommand { get; }
        public Command PauseCommand { get; }
        public Command StopCommand { get; }

        // Raised when the user needs to be told something. The page decides how to show it.
        // The ViewModel never calls DisplayAlertAsync itself because it knows nothing about the UI.
        public event Action<string, string, string>? ShowAlert;

        // True while the user is dragging the position slider.
        // The timer stops updating the position while this is true so the slider does not fight the user.
        public bool Dragging { get; set; } = false;

        // Controls whether the player controls are visible (only after a file has been opened).
        public bool ShowControls
        {
            get => _showControls;
            set
            {
                if (_showControls != value) {
                    _showControls = value;
                    OnPropertyChanged();
                }
            }
        }

        // The name of the file the user picked, displayed in a label.
        public string PickedFileName
        {
            get => _pickedFilename;
            set
            {
                if (_pickedFilename != value) {
                    _pickedFilename = value;
                    OnPropertyChanged();
                }
            }
        }

        // True while the music is playing. Used to enable/disable the buttons.
        public bool MusicPlaying
        {
            get => _musicPlaying;
            set
            {
                if (_musicPlaying != value) {
                    _musicPlaying = value;
                    OnPropertyChanged();

                    // MusicNotPlaying is calculated from MusicPlaying,
                    // so we need to tell the UI it has changed too.
                    OnPropertyChanged(nameof(MusicNotPlaying));
                }
            }
        }

        // The opposite of MusicPlaying. Lets the Start button be bound directly to a bool.
        public bool MusicNotPlaying => !MusicPlaying;

        // The volume (0 to 1). It is stored in our own field and applied to the player if there is one.
        public double MusicVolume
        {
            get => _volume;
            set
            {
                if (_volume != value) {
                    _volume = value;

                    if (_openedMusic != null) {
                        _openedMusic.Volume = _volume;
                    }

                    OnPropertyChanged();
                }
            }
        }

        // The length of the music in seconds, or 0 if no music has been opened.
        // Used as the maximum value of the position slider.
        public double MusicLength
        {
            get
            {
                if (_openedMusic != null) {
                    return _openedMusic.Duration;
                }
                else {
                    return 0;
                }
            }
        }

        // The current position in the music (seconds). The slider is bound to this.
        public double MusicPosition
        {
            get => _musicPosition;
            set
            {
                if (_musicPosition != value) {
                    _musicPosition = value;
                    OnPropertyChanged();

                    // PositionText is calculated from MusicPosition, so it changes too.
                    OnPropertyChanged(nameof(PositionText));
                }
            }
        }

        // The position formatted as minutes:seconds, for example 3:07.
        public string PositionText
        {
            get
            {
                int minutes = (int)(MusicPosition / 60);
                int seconds = (int)(MusicPosition % 60);

                // D2 pads the seconds to two digits (so 7 becomes 07).
                return $"{minutes}:{seconds:D2}";
            }
        }

        public MusicVM() {
            // Connect each command to the method it should run.
            // PickFile is async, so the command awaits it.
            PickFileCommand = new Command(async () => await PickFile());
            StartCommand = new Command(Start);
            PauseCommand = new Command(Pause);
            StopCommand = new Command(Stop);

            // Create a timer that ticks every 100 milliseconds.
            // IDispatcherTimer ticks on the UI thread, so it is safe to update bound properties from it.
            _timer = Application.Current!.Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100);

            // Update the playback position while music is playing.
            _timer.Tick += (s, e) =>
            {
                if (_openedMusic != null && !Dragging && MusicPlaying) {
                    MusicPosition = _openedMusic.CurrentPosition;
                }
            };
        }

        // Lets the user pick a music file and opens it in a new audio player.
        public async Task PickFile() {
            // These are declared before the try so the catch block can clean them up if something fails.
            Stream? newStream = null;
            IAudioPlayer? newPlayer = null;

            try {
                // Only allow audio files. Android uses MIME types, Windows uses file extensions.
                var customFileType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "audio/mpeg", "audio/ogg", "audio/flac" } },
                    { DevicePlatform.WinUI, new[] { ".mp3", ".ogg", ".flac" } }
                });

                PickOptions options = new()
                {
                    PickerTitle = "Please select a music file",
                    FileTypes = customFileType
                };

                FileResult? fileResult = await FilePicker.Default.PickAsync(options);

                // If the user cancels, keep the existing player.
                if (fileResult == null)
                    return;

                // The file type filter is not a guarantee, so check the extension ourselves.
                // Never trust data from the user.
                string extension = Path.GetExtension(fileResult.FileName);

                if (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)) {
                    // Tell the page (through the event) so it can display the message.
                    ShowAlert?.Invoke("Unsupported file", "Please choose an mp3, ogg or flac file.", "OK");
                    return;
                }

                // Open the file as a stream.
                newStream = await fileResult.OpenReadAsync();

                // Create the new player before disposing of the old one.
                // That way, if the new file fails to open, the old music is still available.
                newPlayer = AudioManager.Current.CreatePlayer(newStream);

                // Keep the user's chosen volume for the new player.
                newPlayer.Volume = _volume;

                // The new player was created successfully, so it is now safe to release the old one.
                ReleasePlayer();

                // Keep the stream alive for the lifetime of the player.
                _musicStream = newStream;
                _openedMusic = newPlayer;

                // The fields now own the stream and player, so clear the local variables.
                // This stops the catch block from disposing them if something fails below.
                newStream = null;
                newPlayer = null;

                // Subscribe to the event raised when the music finishes.
                _openedMusic.PlaybackEnded += OpenedMusic_PlaybackEnded;

                PickedFileName = fileResult.FileName;
                MusicPosition = 0;

                // The length depends on the new player, so tell the UI to read it again.
                OnPropertyChanged(nameof(MusicLength));

                // Show the controls now that there is music to play.
                ShowControls = true;
            }
            catch (Exception ex) {
                // Dispose of any partially created resources.
                newPlayer?.Dispose();
                newStream?.Dispose();

                // Write the details to the Output window for debugging, and tell the user something went wrong.
                System.Diagnostics.Debug.WriteLine(ex);

                ShowAlert?.Invoke("Error", "The music file could not be opened.", "OK");
            }
        }

        // Stops and disposes of the current player and its stream, and resets the state.
        private void ReleasePlayer() {
            _timer.Stop();

            if (_openedMusic != null) {
                // Unsubscribe from the event before disposing of the player.
                _openedMusic.PlaybackEnded -= OpenedMusic_PlaybackEnded;

                _openedMusic.Stop();
                _openedMusic.Dispose();

                _openedMusic = null;
            }

            // Dispose of the stream after disposing of the player.
            _musicStream?.Dispose();
            _musicStream = null;

            MusicPlaying = false;
            MusicPosition = 0;

            // There is no player now, so the length has changed.
            OnPropertyChanged(nameof(MusicLength));
        }

        // Event handler: called by the audio player when the music reaches the end.
        private void OpenedMusic_PlaybackEnded(object? sender, EventArgs e) {
            _timer.Stop();

            // Move the slider to the end and update the buttons.
            MusicPosition = MusicLength;
            MusicPlaying = false;
        }

        // Starts (or resumes) playback and starts the timer that updates the position.
        public void Start() {
            if (_openedMusic == null)
                return;

            _openedMusic.Play();

            MusicPlaying = true;
            _timer.Start();
        }

        // Pauses playback. The position is kept so Start will resume from here.
        public void Pause() {
            if (_openedMusic == null)
                return;

            _openedMusic.Pause();

            _timer.Stop();
            MusicPlaying = false;
        }

        // Stops playback and returns to the beginning.
        public void Stop() {
            if (_openedMusic == null)
                return;

            _openedMusic.Stop();

            _timer.Stop();

            MusicPosition = 0;
            MusicPlaying = false;
        }

        // Jumps to a position in the music (in seconds). Called by the page when the user finishes dragging the slider.
        public void SeekTo(double position) {
            _openedMusic?.Seek(position);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string? propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}