namespace MusicPlayer
{
    public partial class MainPage : ContentPage
    {
        // The ViewModel does the work. The page only connects it to the UI.
        private readonly MusicVM _musicViewModel;

        public MainPage() {
            InitializeComponent();

            _musicViewModel = new MusicVM();

            // Subscribe to the ViewModel's ShowAlert event.
            // The ViewModel raises the event and the page decides how to show it.
            _musicViewModel.ShowAlert += (title, message, cancel) => DisplayAlertAsync(title, message, cancel);

            // The XAML bindings now look for their properties on the ViewModel.
            BindingContext = _musicViewModel;
        }

        // The user has started dragging the position slider.
        // The slider is bound to MusicPosition, but the timer is also updating MusicPosition while the music plays.
        // If both were allowed to change it at once, the slider would keep snapping back and the player would be asked to seek over and over.
        // So we tell the ViewModel the user is dragging, and the timer stops updating the position until they let go.
        private void Start_Drag(object sender, EventArgs e) {
            _musicViewModel.Dragging = true;
        }

        // The user has let go of the position slider.
        // Seek to the chosen position once (rather than every time the slider value changes while dragging),
        // then allow the timer to update the slider again.
        private void Slider_DragCompleted(object sender, EventArgs e) {
            var slider = (Slider)sender;

            _musicViewModel.SeekTo(slider.Value);
            _musicViewModel.Dragging = false;
        }
    }

}
