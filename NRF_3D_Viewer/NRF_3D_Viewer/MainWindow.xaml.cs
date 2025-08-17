using System.Text;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Windows.Storage.Streams;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using HelixToolkit.Wpf;
using System.Text.RegularExpressions;
using System.Diagnostics;


namespace NRF_3D_Viewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 
    
    public partial class MainWindow : Window
    {
        //public variables

        //private variables
        private BluetoothLEDevice? bleDevice;
        private GattCharacteristic? gyroChar;
        private GattCharacteristic? accelChar;
        private CancellationTokenSource? bleCts;
        private BluetoothLEAdvertisementWatcher? bleWatcher;
        private Stopwatch? stopwatch;

        private List<ulong>? deviceAddrs;
        private double? pitch, roll, yaw;
        private double? tilt;
        private bool? isUpsideDown;
        

        public MainWindow()
        {
            //inits
            deviceAddrs = new List<ulong>();
            pitch = null;
            roll = null;
            yaw = 0; //need magnetometer integration
            stopwatch = new Stopwatch();

            InitializeComponent();

            //load model
            ModelImporter importer = new ModelImporter();
            Model3DGroup model = importer.Load(@"D:\Github\NRF_3D_Viewer\NRF_3D_Viewer\NRF_3D_Viewer\3D_Model\model.obj"); 
            if (model != null)
            {
                // Build the combined transform
                var flipY = new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(0, 1, 0), 180) // fix front/back
                );
                var flipX = new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(1, 0, 0), 180) // flip upright
                );

                var group = new Transform3DGroup();
                group.Children.Add(flipY); // applied first
                group.Children.Add(flipX); // then this

                model.Transform = group; // or set modelVisual.Transform = group;

                var modelVisual = new ModelVisual3D { Content = model };
                viewPort.Children.Add(modelVisual);
            }

            //init ble
            //init watcher to scan for devices
            bleWatcher = new BluetoothLEAdvertisementWatcher();
            bleWatcher.Received += BleWatcher_Received;
            bleWatcher.ScanningMode = BluetoothLEScanningMode.Active;
        }
        private void BleWatcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            //init occurred?
            if (deviceAddrs != null)
            {
                string name = args.Advertisement.LocalName;
                ulong address = args.BluetoothAddress;

                if (string.IsNullOrEmpty(name)) return;

                string display = $"{name} ({address:X})";
                if (!Device_ComboBox.Items.Contains(display))
                {
                    Device_ComboBox.Items.Add(display);
                    deviceAddrs.Add(address);
                }
            }
        }
        private async Task SubscribeToCharacteristic(GattCharacteristic characteristic, CancellationToken token, string type)
        {
            characteristic.ValueChanged += NotificationReceived;

            var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (status != GattCommunicationStatus.Success)
                MessageBox.Show($"Failed to subscribe to {type} notifications.");
        }
        private void NotificationReceived(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            double dt;
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            string raw = reader.ReadString(args.CharacteristicValue.Length);
            string[] parts = raw.Split(',');

            if (parts.Length >= 3 &&
                float.TryParse(parts[0], out float x) &&
                float.TryParse(parts[1], out float y) &&
                float.TryParse(parts[2], out float z))
            {
                //get difference of time measurement
                if (stopwatch != null && stopwatch.IsRunning)
                {
                    dt = stopwatch.Elapsed.TotalSeconds;
                    stopwatch.Stop();
                }

                //if the reading is significant...
                if ((x > 0.1) || (y > 0.1) || (z > 0.1))
                {
                    string type = sender.Uuid.ToString().ToLower();

                    if (type.Contains("19b10001")) // Gyroscope
                    {

                    }
                    else if (type.Contains("19b10002")) // Accelerometer
                    {
                        float degreesX = 0;
                        float degreesY = 0;

                        float scaledX = x * 100;
                        float scaledY = y * 100;

                        if (x > 0.1)
                        {
                            degreesX = map((long)scaledX, 0, 97, 0, 90);
                            Console.WriteLine($"Tilting up {degreesX} degrees");
                        }
                        else if (x < -0.1)
                        {
                            degreesX = map((long)scaledX, 0, -100, 0, 90);
                            Console.WriteLine($"Tilting down {degreesX} degrees");
                        }

                        if (y > 0.1)
                        {
                            degreesY = map((long)scaledY, 0, 97, 0, 90);
                            Console.WriteLine($"Tilting left {degreesY} degrees");
                        }
                        else if (y < -0.1)
                        {
                            degreesY = map((long)scaledY, 0, -100, 0, 90);
                            Console.WriteLine($"Tilting right {degreesY} degrees");
                        }
                    }
                }

                //start the stopwatch again
                if (stopwatch != null)
                {
                    stopwatch.Start();
                }
            }
        }
        private long map(long x, long in_min, long in_max, long out_min, long out_max)
        {
            return (x - in_min) * (out_max - out_min) / (in_max - in_min) + out_min;
        }

        private void UpdateOrientation(double ax, double ay, double az, double gx, double gy, double gz, double dt)
        {
            //alpha constatnt
            double alpha = 0.98;

            //conversion constant
            double radianToDegrees = 180.0 / Math.PI;

            //normalize accelerometer reading
            double norm = 1.0 / Math.Sqrt(Math.Pow(ax, 2) + Math.Pow(ay, 2) + Math.Pow(az, 2) + 1e-12);
            ax *= norm; 
            ay *= norm; 
            az *= norm;

            //get degree angles of accelometer reading
            double accelPitch = Math.Atan2(ax, Math.Sqrt(ay * ay + az * az)) * radianToDegrees;
            double accelRoll = Math.Atan2(ay,  Math.Sqrt(ax*ax + az*az)) * radianToDegrees;

            //check if this is the first sample to be recieved...
            if(pitch == null || roll == null)
            {
                //set the current orientation variables...
                pitch = accelPitch;
                roll = accelRoll;
                yaw = 0;
            }

            //integrate gyro reading
            pitch += gx * dt;
            roll += gy * dt;
            yaw += gz * dt;

            //combine readings
            pitch = alpha * pitch + (1.0 - alpha) * accelPitch;
            roll = alpha * roll + (1.0 - alpha) * accelRoll;

            //finally get the tilt
            tilt = Math.Acos(Math.Max(-1.0, Math.Min(1.0, az))) * radianToDegrees;
            isUpsideDown = tilt >= 150.0 ? true : false;
        }

        private void Device_ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private void scanBtn_Click(object sender, RoutedEventArgs e)
        {
            //init occurred?
            if (deviceAddrs != null && bleWatcher != null)
            {
                Device_ComboBox.Items.Clear();
                deviceAddrs.Clear();

                // Start BLE scan
                bleWatcher.Start();

                // Optionally stop scan after 3 seconds
                Task.Delay(3000).ContinueWith(_ =>
                {
                    bleWatcher.Stop();
                    //Invoke(new Action(() => MessageBox.Show("BLE scan complete.")));
                });
            }
        }

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Device_ComboBox.SelectedIndex == -1)
            {
                MessageBox.Show("Select a BLE device first.");
                return;
            }

            // Get BLE device address
            string? selectedItem = Device_ComboBox.SelectedItem.ToString();
            if (selectedItem != null)
            {
                string hexAddress = selectedItem.Split('(')[1].TrimEnd(')');
                ulong address = Convert.ToUInt64(hexAddress, 16);


                // Attempt Connection
                bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (bleDevice == null)
                {
                    MessageBox.Show("Failed to connect.");
                    return;
                }

                // Attempt to get Service
                // Await the async operation to get the actual result object
                GattDeviceServicesResult svcResult = await bleDevice.GetGattServicesAsync();
                var services = svcResult.Services;

                foreach (var service in services)
                {
                    var charsResult = await service.GetCharacteristicsAsync();
                    foreach (var charc in charsResult.Characteristics)
                    {
                        if (charc.Uuid.ToString().Equals("19b10001-e8f2-537e-4f6c-d104768a1214", StringComparison.OrdinalIgnoreCase))
                            gyroChar = charc;
                        else if (charc.Uuid.ToString().Equals("19b10002-e8f2-537e-4f6c-d104768a1214", StringComparison.OrdinalIgnoreCase))
                            accelChar = charc;
                    }
                }

                if (gyroChar == null || accelChar == null)
                {
                    MessageBox.Show("Required characteristics not found.");
                    return;
                }

                bleCts = new CancellationTokenSource();
                await SubscribeToCharacteristic(gyroChar, bleCts.Token, "gyro");
                await SubscribeToCharacteristic(accelChar, bleCts.Token, "accel");

                MessageBox.Show("Subscribed to IMU data.");
            }
        }
    }
}