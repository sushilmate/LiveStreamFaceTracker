using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using Windows.System.Threading;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace LiveStreamFaceTracker
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private FaceTracker faceTracker;
        private SemaphoreSlim frameProcessingSemaphore = new SemaphoreSlim(1);
        private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);
        private readonly double lineThickness = 2.0;
        private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);
        private MediaCapture mediaCapture;
        private VideoEncodingProperties videoProperties;
        private ThreadPoolTimer frameProcessingTimer;

        public MainPage()
        {
            this.InitializeComponent();
            Application.Current.Suspending += Current_Suspending;
        }

        private async void Button_Click(object sender, RoutedEventArgs e)
        {
            if (this.mediaCapture == null)
                this.mediaCapture = new MediaCapture();

            MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
            settings.StreamingCaptureMode = StreamingCaptureMode.Video;
            await this.mediaCapture.InitializeAsync(settings);
            this.mediaCapture.Failed += this.MediaCapture_CameraStreamFailed;

            var deviceController = this.mediaCapture.VideoDeviceController;
            this.videoProperties = deviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

            this.CamPreview.Source = this.mediaCapture;
            await this.mediaCapture.StartPreviewAsync();
        }

        private void SetupVisualization(Size framePizelSize, IList<DetectedFace> foundFaces)
        {
            this.VisualizationCanvas.Children.Clear();

            double actualWidth = this.VisualizationCanvas.ActualWidth;
            double actualHeight = this.VisualizationCanvas.ActualHeight;

            if (foundFaces != null && actualWidth != 0 && actualHeight != 0)
            {
                double widthScale = framePizelSize.Width / actualWidth;
                double heightScale = framePizelSize.Height / actualHeight;

                foreach (DetectedFace face in foundFaces)
                {
                    Rectangle box = new Rectangle();
                    box.Width = (uint)(face.FaceBox.Width / widthScale);
                    box.Height = (uint)(face.FaceBox.Height / heightScale);
                    box.Fill = this.fillBrush;
                    box.Stroke = this.lineBrush;
                    box.StrokeThickness = this.lineThickness;
                    box.Margin = new Thickness((uint)(face.FaceBox.X / widthScale), (uint)(face.FaceBox.Y / heightScale), 0, 0);

                    this.VisualizationCanvas.Children.Add(box);
                }
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await CleanupCameraAsync();
        }

        private async void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (mediaCapture == null)
                return;

            const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;

            using (VideoFrame previewFrame = new VideoFrame(InputPixelFormat, (int)this.videoProperties.Width, (int)this.videoProperties.Height))
            {
                await this.mediaCapture.GetPreviewFrameAsync(previewFrame);
                var softwareBitmap = previewFrame?.SoftwareBitmap;

                if (softwareBitmap != null)
                {
                    if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
                    {
                        softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    }

                    var savePicker = new FileSavePicker
                    {
                        SuggestedStartLocation = PickerLocationId.Desktop
                    };
                    savePicker.FileTypeChoices.Add("Jpg Image", new[] { ".jpg" });
                    savePicker.SuggestedFileName = "example.jpg";
                    StorageFile sFile = await savePicker.PickSaveFileAsync();

                    await WriteToStorageFile(softwareBitmap, sFile);

                    await SetCapturedImage(sFile);
                }
            }
        }

        private async Task SetCapturedImage(StorageFile sFile)
        {
            using (var fileStream = await sFile.OpenAsync(FileAccessMode.Read))
            {
                var bitmapImage = new BitmapImage();

                bitmapImage.SetSource(fileStream);
                imageControl.Source = bitmapImage;
            }
        }

        private static async Task<FileUpdateStatus> WriteToStorageFile(SoftwareBitmap bitmap, StorageFile file)
        {
            StorageFile sFile = file;
            if (sFile != null)
            {
                CachedFileManager.DeferUpdates(sFile);

                using (var fileStream = await sFile.OpenAsync(FileAccessMode.ReadWrite))
                {
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, fileStream);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                }

                FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(sFile);
                return status;
            }
            return FileUpdateStatus.Failed;
        }

        private async void DetectButton_Click(object sender, RoutedEventArgs e)
        {
            if (mediaCapture == null)
                return;

            if (this.faceTracker == null)
            {
                this.faceTracker = await FaceTracker.CreateAsync();
            }

            TimeSpan timerInterval = TimeSpan.FromMilliseconds(66); // 15 fps
            this.frameProcessingTimer = ThreadPoolTimer.CreatePeriodicTimer
                (new TimerElapsedHandler(ProcessCurrentVideoFrame), timerInterval);
        }

        private async void MediaCapture_CameraStreamFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            MessageDialog messageDialog = new MessageDialog("Starting of Web Cam failed, Reason: " + errorEventArgs.Message);
            await messageDialog.ShowAsync();
        }

        public async void ProcessCurrentVideoFrame(ThreadPoolTimer timer)
        {
            if (!frameProcessingSemaphore.Wait(0))
            {
                return;
            }

            IList<DetectedFace> detectedFaces = null;
            try
            {
                const BitmapPixelFormat faceDetectionPixelFormat = BitmapPixelFormat.Nv12;

                using (VideoFrame previewFrame = new VideoFrame(faceDetectionPixelFormat, (int)this.videoProperties.Width, (int)this.videoProperties.Height))
                {
                    await this.mediaCapture.GetPreviewFrameAsync(previewFrame);

                    if (FaceDetector.IsBitmapPixelFormatSupported(previewFrame.SoftwareBitmap.BitmapPixelFormat))
                    {
                        detectedFaces = await this.faceTracker.ProcessNextFrameAsync(previewFrame);
                    }
                    else
                    {
                        frameProcessingSemaphore.Release();
                        return;
                    }

                    var previewFrameSize = new Size(previewFrame.SoftwareBitmap.PixelWidth, previewFrame.SoftwareBitmap.PixelHeight);
                    var ignored = this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        this.SetupVisualization(previewFrameSize, detectedFaces);
                    });
                }
            }
            catch (Exception e)
            {
                // Face tracking failed
            }
            finally
            {
                frameProcessingSemaphore.Release();
            }
        }

        private async Task CleanupCameraAsync()
        {
            if (this.frameProcessingTimer != null)
            {
                this.frameProcessingTimer.Cancel();
            }

            if (mediaCapture != null)
            {
                await mediaCapture.StopPreviewAsync();

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    CamPreview.Source = null;

                    mediaCapture.Dispose();
                    mediaCapture = null;
                });
            }
            this.frameProcessingTimer = null;
            this.CamPreview.Source = null;
            this.mediaCapture = null;
            this.VisualizationCanvas.Children.Clear();
        }

        protected async override void OnNavigatedFrom(NavigationEventArgs e)
        {
            await CleanupCameraAsync();
        }

        private async void Current_Suspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                var deferral = e.SuspendingOperation.GetDeferral();
                await CleanupCameraAsync();
                deferral.Complete();
            }
        }
    }
}
