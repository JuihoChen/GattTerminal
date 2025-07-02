using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;

// Disable warning "...execution of the current method continues before the call is completed..."
#pragma warning disable 4014

// Disable warning to "consider using the 'await' operator to await non-blocking API calls"
#pragma warning disable 1998

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace GattTerminal
{
    // <summary>
    /// Helper class that allows you to monitor a property corresponding to a dependency property 
    /// on some object for changes and have an event raised from
    /// the instance of this helper that you can handle.
    /// Usage: Construct an instance, passing in the object and the name of the normal .NET property that
    /// wraps a DependencyProperty, then subscribe to the PropertyChanged event on this helper instance. 
    /// Your subscriber will be called whenever the source DependencyProperty changes.
    /// </summary>
    public class DependencyPropertyChangedHelper : DependencyObject
    {
        /// <summary>
        /// Constructor for the helper. 
        /// </summary>
        /// <param name="source">Source object that exposes the DependencyProperty you wish to monitor.</param>
        /// <param name="propertyPath">The name of the property on that object that you want to monitor.</param>
        public DependencyPropertyChangedHelper(DependencyObject source, string propertyPath)
        {
            // Set up a binding that flows changes from the source DependencyProperty through to a DP contained by this helper 
            Binding binding = new Binding
            {
                Source = source,
                Path = new PropertyPath(propertyPath)
            };
            BindingOperations.SetBinding(this, HelperProperty, binding);
        }

        /// <summary>
        /// Dependency property that is used to hook property change events when an internal binding causes its value to change.
        /// This is only public because the DependencyProperty syntax requires it to be, do not use this property directly in your code.
        /// </summary>
        public static DependencyProperty HelperProperty =
            DependencyProperty.Register("Helper", typeof(object), typeof(DependencyPropertyChangedHelper), new PropertyMetadata(null, OnPropertyChanged));

        /// <summary>
        /// Wrapper property for a helper DependencyProperty used by this class. Only public because the DependencyProperty syntax requires it.
        /// DO NOT use this property directly.
        /// </summary>
        public object Helper
        {
            get { return (object)GetValue(HelperProperty); }
            set { SetValue(HelperProperty, value); }
        }

        // When our dependency property gets set by the binding, trigger the property changed event that the user of this helper can subscribe to
        private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var helper = (DependencyPropertyChangedHelper)d;
            helper.PropertyChanged(d, e);
        }

        /// <summary>
        /// This event will be raised whenever the source object property changes, and carries along the before and after values
        /// </summary>
        public event EventHandler<DependencyPropertyChangedEventArgs> PropertyChanged = delegate { };
    }

    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        const string DIO_GUID_STR = "00005500-D102-11E1-9B23-00025B00A5A5";
        const string DIO_NOTIFICATION_GUID_STR = "00005501-D102-11E1-9B23-00025B00A5A5";
        readonly Guid DIO_GUID = new Guid(DIO_GUID_STR);
        readonly Guid DIO_NOTIFICATION_GUID = new Guid(DIO_NOTIFICATION_GUID_STR);

        private string MACAddress = string.Empty;
        private string PINCode = "000000";

        private DeviceInformationDisplay DeviceInfoConnected = null;
        private GattDeviceService serviceDio = null;
        private GattCharacteristic activeCharacteristic = null;

        //Handlers for device detection
        private DeviceWatcher deviceWatcher = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformation> handlerAdded = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> handlerUpdated = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> handlerRemoved = null;
        private TypedEventHandler<DeviceWatcher, Object> handlerEnumCompleted = null;

        private DeviceWatcher blewatcher = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformation> OnBLEAdded = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> OnBLEUpdated = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> OnBLERemoved = null;

        public TaskCompletionSource<string> syncListViewSrc;
        private TaskCompletionSource<string> syncWatcherTaskSrc;

        private DependencyPropertyChangedHelper helper;

        public ObservableCollection<DeviceInformationDisplay> ResultCollection
        {
            get;
            private set;
        }

        public MainPage()
        {
            this.InitializeComponent();

            ResultCollection = new ObservableCollection<DeviceInformationDisplay>();

            //Set DataContext for Data Binding
            DataContext = this;

            //Start Watcher for pairable/paired devices
            StartWatcher();

            helper = new DependencyPropertyChangedHelper(msgTextBlock, "Text");
            helper.PropertyChanged += msgTextBlock_TextChanged;

            msgTextBlock.Text = string.Empty;
            ShowVersion();
            inpTextBox.TabIndex = 0;        // Set focus to inpTextBox

            // Set the desired remaining view.
            var options = new LauncherOptions();
            options.DesiredRemainingView = ViewSizePreference.UseMore;
            // Launch the URI
            var appView = ApplicationView.GetForCurrentView();
            Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var uri = new Uri(@"ms-settings:bluetooth");
                var success = await Launcher.LaunchUriAsync(uri, options);

                await ApplicationViewSwitcher.TryShowAsStandaloneAsync(appView.Id, ViewSizePreference.UseMore);
            });

        }

        ~MainPage()
        {
            StopWatcher();
        }

        private void ShowVersion()
        {
            msgTextBlock.Text += "BLE GATT Terminal, Ver 0.5.2\r@2016-2017 Pegatron MCPDC\r";
        }

        private void inpTextBox_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                string inpString = inpTextBox.Text.Trim();
                msgTextBlock.Text += $"\u23F5{inpString}\r";
                inpTextBox.Text = string.Empty;

                ProcessCommand(inpString);
            }
        }

        private async void msgTextBlock_TextChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            //var grid = (Grid)VisualTreeHelper.GetChild(msgTextBox, 0);
            //for (var i = 0; i <= VisualTreeHelper.GetChildrenCount(grid) - 1; i++)
            //{
            //    object obj = VisualTreeHelper.GetChild(grid, i);
            //    if (!(obj is ScrollViewer)) continue;
            //    ((ScrollViewer)obj).ChangeView(null, ((ScrollViewer)obj).ExtentHeight, 1.0f, true);
            //    break;
            //}
            scrollViewer_Status.Measure(scrollViewer_Status.RenderSize);
            scrollViewer_Status.ChangeView(null, scrollViewer_Status.ScrollableHeight, null, true);
        }

        private readonly string helpMessage =
            "%help\t\t- display commands and usages\r" +
            "%pair\t\t- [MAC(xx:xx:xx:xx:xx:xx)]\r" +
            "%unpair\t\t- unpair and disconnect the device\r";

        private async void ProcessCommand(string ins)
        {
            if (ins.Length == 0) return;

            string[] argvs = ins.Split(null);

            if (argvs[0].CompareTo("%help") == 0)
            {
                msgTextBlock.Text += helpMessage;
            }
            else if (argvs[0].CompareTo("%pair") == 0)
            {
                try
                {
                    inpTextBox.IsEnabled = false;

                    await SaveMACAddress(argvs);

                    await PairBleDevice();
                }
                catch (Exception e)
                {
                    msgTextBlock.Text += e.Message;
                }
                finally
                {
                    if (!inpTextBox.IsEnabled)
                    {
                        inpTextBox.IsEnabled = true;
                        inpTextBox.Focus(FocusState.Programmatic);
                    }
                }
            }
            else if (argvs[0].CompareTo("%unpair") == 0)
            {
                if (DeviceInfoConnected != null)
                {
                    UnpairDevice(DeviceInfoConnected, serviceDio);
                    StopBLEWatcher();
                    DeviceInfoConnected = null;
                    serviceDio = null;
                    activeCharacteristic = null;
                }
                else
                {
                    msgTextBlock.Text += "No device is connected...\r";
                }
            }
            else if (argvs[0][0] != '%' && activeCharacteristic != null)
            {
                writeTracker(ins);
            }
            else
            {
                msgTextBlock.Text += "Unknown command\r";
            }
        }

        private async Task SaveMACAddress(string[] argvs)
        {
            string formatErr = "MAC address format error\r";
            String pattern = @"^([\dA-F]{2}):([\dA-F]{2}):([\dA-F]{2}):([\dA-F]{2}):([\dA-F]{2}):([\dA-F]{2})$";

            if (argvs.Count() <= 1)
            {
                syncListViewSrc = new TaskCompletionSource<string>();

                ListViewFrame.Navigate(typeof(MyListView), this);
                var msg = await syncListViewSrc.Task;
                syncListViewSrc = null;

                if (string.IsNullOrEmpty(msg))
                {
                    throw new Exception("Warning: Please select one PT1000 to connect...\r");
                }
                else
                {
                    MACAddress = msg;
                    msgTextBlock.Text += $"%pair {MACAddress}\r";
                    return;
                }
            }

            if (argvs[1].Length != 17) throw new Exception(formatErr);

            MatchCollection matches = Regex.Matches(argvs[1], pattern, RegexOptions.IgnoreCase);

            if (matches.Count != 1) throw new Exception(formatErr);

            MACAddress = new String(argvs[1].ToCharArray());
        }

        private void StartWatcher()
        {
            Debug.WriteLine("Start Enumerating Bluetooth LE Devices in Background...");

            ResultCollection.Clear();

            // Request the IsPaired property so we can display the paired status in the UI
            string[] requestedProperties = { "System.Devices.Aep.IsPaired", "System.Devices.Aep.IsPresent" };
            //string[] requestedProperties = { "System.Devices.Aep.IsPaired" };

            //for bluetooth LE Devices
            string aqsFilter = "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";

            deviceWatcher = DeviceInformation.CreateWatcher(
                aqsFilter,
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint
                );

            // Hook up handlers for the watcher events before starting the watcher

            handlerAdded = async (watcher, deviceInfo) =>
            {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    Debug.WriteLine("Watcher Add: " + deviceInfo.Id);
                    ResultCollection.Add(new DeviceInformationDisplay(deviceInfo));
                });
            };
            deviceWatcher.Added += handlerAdded;

            handlerUpdated = async (watcher, deviceInfoUpdate) =>
            {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    Debug.WriteLine("Watcher Update: " + deviceInfoUpdate.Id);
                    // Find the corresponding updated DeviceInformation in the collection and pass the update object
                    // to the Update method of the existing DeviceInformation. This automatically updates the object
                    // for us.
                    foreach (DeviceInformationDisplay deviceInfoDisp in ResultCollection)
                    {
                        if (deviceInfoDisp.Id == deviceInfoUpdate.Id)
                        {
                            deviceInfoDisp.Update(deviceInfoUpdate);
                            break;
                        }
                    }
                });
            };
            deviceWatcher.Updated += handlerUpdated;

            handlerRemoved = async (watcher, deviceInfoUpdate) =>
            {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    Debug.WriteLine("Watcher Remove: " + deviceInfoUpdate.Id);
                    // Find the corresponding DeviceInformation in the collection and remove it
                    foreach (DeviceInformationDisplay deviceInfoDisp in ResultCollection)
                    {
                        if (deviceInfoDisp.Id == deviceInfoUpdate.Id)
                        {
                            ResultCollection.Remove(deviceInfoDisp);
                            if (ResultCollection.Count == 0)
                            {
                                Debug.WriteLine("Searching for Bluetooth LE Devices...");
                            }
                            break;
                        }
                    }
                });
            };
            deviceWatcher.Removed += handlerRemoved;

            handlerEnumCompleted = async (watcher, obj) =>
            {
                Debug.WriteLine($"Found {ResultCollection.Count} Bluetooth LE Devices");
            };
            deviceWatcher.EnumerationCompleted += handlerEnumCompleted;

            deviceWatcher.Start();
        }

        private void StopWatcher()
        {
            if (null != deviceWatcher)
            {
                // First unhook all event handlers except the stopped handler. This ensures our
                // event handlers don't get called after stop, as stop won't block for any "in flight" 
                // event handler calls.  We leave the stopped handler as it's guaranteed to only be called
                // once and we'll use it to know when the query is completely stopped. 
                deviceWatcher.Added -= handlerAdded;
                deviceWatcher.Updated -= handlerUpdated;
                deviceWatcher.Removed -= handlerRemoved;
                deviceWatcher.EnumerationCompleted -= handlerEnumCompleted;

                if (DeviceWatcherStatus.Started == deviceWatcher.Status ||
                    DeviceWatcherStatus.EnumerationCompleted == deviceWatcher.Status)
                {
                    deviceWatcher.Stop();
                }
            }
        }

        private int CompareDevicePT1000(DeviceInformationDisplay deviceInfoDisp)
        {
            if (String.IsNullOrEmpty(MACAddress)) return -1;

            string s = deviceInfoDisp.Id;
            s = s.Substring(s.Length - MACAddress.Length);
            return String.Compare(s, MACAddress, true);         //IgnoreCase
        }

        private async void PairingRequestedHandler(
            DeviceInformationCustomPairing sender,
            DevicePairingRequestedEventArgs args)
        {
            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    // Windows itself will pop the confirmation dialog as part of "consent" if this is running on Desktop or Mobile
                    // If this is an App for 'Windows IoT Core' where there is no Windows Consent UX, you may want to provide your own confirmation.
                    args.Accept();
                    break;

                case DevicePairingKinds.ProvidePin:
                    // A PIN may be shown on the target device and the user needs to enter the matching PIN on 
                    // this Windows device. Get a deferral so we can perform the async request to the user.
                    var collectPinDeferral = args.GetDeferral();

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                    {
                        args.Accept(PINCode);
                        collectPinDeferral.Complete();
                    });
                    break;
                    /*
                       case DevicePairingKinds.DisplayPin:
                           // We just show the PIN on this side. The ceremony is actually completed when the user enters the PIN
                           // on the target device. We automatically except here since we can't really "cancel" the operation
                           // from this side.
                           args.Accept();

                           // No need for a deferral since we don't need any decision from the user
                           await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                           {
                               ShowPairingPanel(
                                   "Please enter this PIN on the device you are pairing with: " + args.Pin,
                                   args.PairingKind);

                           });
                           break;

                    case DevicePairingKinds.ConfirmPinMatch:
                        // We show the PIN here and the user responds with whether the PIN matches what they see
                        // on the target device. Response comes back and we set it on the PinComparePairingRequestedData
                        // then complete the deferral.
                        var displayMessageDeferral = args.GetDeferral();

                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                        {
                            bool accept = await GetUserConfirmationAsync(args.Pin);
                            if (accept)
                            {
                                args.Accept();
                            }

                            displayMessageDeferral.Complete();
                        });
                        break;
                   */
            }

            Debug.WriteLine($"DevicePairingRequestedEventArgs {args.PairingKind}");
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                msgTextBlock.Text += $"DevicePairingRequestedEventArgs {args.PairingKind}\r";
            });
        }

        private async Task PairBleDevice()
        {
            DeviceInformationDisplay deviceInfoDisp = null;
            foreach (DeviceInformationDisplay d in ResultCollection)
            {
                if (CompareDevicePT1000(d) == 0)
                {
                    deviceInfoDisp = d;
                }
            }

            if (deviceInfoDisp == null)
            {
                msgTextBlock.Text += "No Matched Bluetooth LE Devices found\r";
                return;
            }

            bool paired = true;
            if (deviceInfoDisp.IsPaired != true)
            {
                paired = false;
                msgTextBlock.Text += "Pairing device...\r";

                DevicePairingKinds ceremoniesSelected = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch;
                DevicePairingProtectionLevel protectionLevel = DevicePairingProtectionLevel.Default;

                // Specify custom pairing with all ceremony types and protection level EncryptionAndAuthentication
                DeviceInformationCustomPairing customPairing = deviceInfoDisp.DeviceInformation.Pairing.Custom;

                customPairing.PairingRequested += PairingRequestedHandler;
                DevicePairingResult result = await customPairing.PairAsync(ceremoniesSelected, protectionLevel);

                customPairing.PairingRequested -= PairingRequestedHandler;

                if (result.Status == DevicePairingResultStatus.Paired)
                {
                    paired = true;
                }
                else
                {
                    msgTextBlock.Text += $"Pairing Failed {result.Status.ToString()}\r";
                }
            }

            if (paired)
            {
                // device is paired, set up the sensor Tag            
                msgTextBlock.Text += "Connecting device...\r";

                DeviceInfoConnected = deviceInfoDisp;

                syncWatcherTaskSrc = new TaskCompletionSource<string>();

                //Start watcher for Bluetooth LE Services
                StartBLEWatcher();

                // a local msg is necessary to prevent caching msgTextBlock.Text
                var msg = await syncWatcherTaskSrc.Task;
                if (!string.IsNullOrEmpty(msg)) msgTextBlock.Text += msg;

                syncWatcherTaskSrc = null;
            }
        }

        //Watcher for Bluetooth LE Services
        private void StartBLEWatcher()
        {
            // Hook up handlers for the watcher events before starting the watcher
            OnBLEAdded = async (watcher, deviceInfo) =>
            {
                Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
                {
                    Debug.WriteLine("OnBLEAdded: " + deviceInfo.Id);
                    GattDeviceService service = await GattDeviceService.FromIdAsync(deviceInfo.Id);
                    if ((service != null) && (service.Device.DeviceInformation.Id == DeviceInfoConnected.DeviceInformation.Id))
                    {
                        string svcGuid = service.Uuid.ToString().ToUpper();
                        msgTextBlock.Text += $"Found Service: {svcGuid}\r";

                        if (svcGuid == DIO_GUID_STR && activeCharacteristic == null)     // Only the first service is to be accepted
                        {
                            serviceDio = service;
                            try
                            {
                                await enableSensor(serviceDio);
                                msgTextBlock.Text += "Pet Tracker is on!\r";
                            }
                            catch (Exception ex)
                            {
                                msgTextBlock.Text += $"Something wrong! Enable sensor exception: {ex.Message}\r";
                            }
                            finally
                            {
                                syncWatcherTaskSrc.SetResult(string.Empty);
                            }
                        }
                    }
                });
            };

            OnBLEUpdated = async (watcher, deviceInfoUpdate) =>
            {
                Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    Debug.WriteLine($"OnBLEUpdated: {deviceInfoUpdate.Id}");
                });
            };

            OnBLERemoved = async (watcher, deviceInfoUpdate) =>
            {
                Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    Debug.WriteLine("OnBLERemoved");
                });
            };

            string aqs = "(" + GattDeviceService.GetDeviceSelectorFromUuid(DIO_GUID) + ")";
            Debug.WriteLine(aqs);

            blewatcher = DeviceInformation.CreateWatcher(aqs);
            blewatcher.Added += OnBLEAdded;
            blewatcher.Updated += OnBLEUpdated;
            blewatcher.Removed += OnBLERemoved;
            blewatcher.Start();
        }

        private void StopBLEWatcher()
        {
            if (null != blewatcher)
            {
                blewatcher.Added -= OnBLEAdded;
                blewatcher.Updated -= OnBLEUpdated;
                blewatcher.Removed -= OnBLERemoved;

                if (DeviceWatcherStatus.Started == blewatcher.Status ||
                    DeviceWatcherStatus.EnumerationCompleted == blewatcher.Status)
                {
                    blewatcher.Stop();
                }

                blewatcher = null;
            }
        }

        // Enable and subscribe to specified GATT characteristic
        private async Task enableSensor(GattDeviceService service)
        {
            Debug.WriteLine("Begin enable service: " + service.Uuid);
            GattDeviceService gattService = service;
            if (gattService != null)
            {
                // Turn on notifications
                IReadOnlyList<GattCharacteristic> characteristicList;
                characteristicList = gattService.GetCharacteristics(DIO_NOTIFICATION_GUID);

                if (characteristicList != null)
                {
                    GattCharacteristic characteristic = characteristicList[0];
                    if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                    {
                        // While encryption is not required by all devices, if encryption is supported by the device,
                        // it can be enabled by setting the ProtectionLevel property of the Characteristic object.
                        // All subsequent operations on the characteristic will work over an encrypted link.
                        //characteristic.ProtectionLevel = GattProtectionLevel.EncryptionRequired;

                        // Register the event handler for receiving notifications
                        characteristic.ValueChanged += revTracker_ValueChanged;

                        // Save a reference to each active characteristic, so that handlers do not get prematurely killed
                        activeCharacteristic = characteristic;

                        // In order to avoid unnecessary communication with the device, determine if the device is already
                        // correctly configured to send notifications.
                        // By default ReadClientCharacteristicConfigurationDescriptorAsync will attempt to get the current
                        // value from the system cache and communication with the device is not typically required.
                        var currentDescriptorValue = await characteristic.ReadClientCharacteristicConfigurationDescriptorAsync();

                        // Set the notify enable flag
                        GattCommunicationStatus status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    }
                }
            }
            Debug.WriteLine("End enable sensor: " + service.Uuid);
        }

        private string revStr = string.Empty;

        async void revTracker_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs eventArgs)
        {
            byte[] bArray = new byte[eventArgs.CharacteristicValue.Length];
            DataReader.FromBuffer(eventArgs.CharacteristicValue).ReadBytes(bArray);
            string text = System.Text.Encoding.ASCII.GetString(bArray);

            revStr += text;
            if (bArray.Contains<byte>(0))
            {
                text = $"{revStr.Substring(0, revStr.IndexOf('\0'))}\r";
                //text = $"{revStr.Substring(0, revStr.IndexOf('\0')).Replace("\n", string.Empty)}\r";
                revStr = string.Empty;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    msgTextBlock.Text += text;
                });
            }
        }

        private async void writeTracker(string text)
        {
            GattWriteOption writeOption = GattWriteOption.WriteWithResponse;
            if (activeCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            {
                writeOption = GattWriteOption.WriteWithoutResponse;
            }

            using (var writer = new DataWriter())
            {
                var t1 = $"{text}\n\r";
                var t2 = string.Empty;
                do
                {
                    if (t1.Length > 20)
                    {
                        t2 = t1.Substring(20);          // remaining string behind the 20th byte
                        t1 = t1.Substring(0, 20);       // string of first 20 bytes
                    }
                    else t2 = string.Empty;

                    writer.WriteString(t1);
                    await activeCharacteristic.WriteValueAsync(writer.DetachBuffer(), writeOption);
                    t1 = t2;
                }
                while (!String.IsNullOrEmpty(t1));
            }
        }

        private async void UnpairDevice(DeviceInformationDisplay deviceInfoDisp, GattDeviceService service)
        {
            try
            {
                Debug.WriteLine("Disable Sensor");
                await disableSensor(serviceDio);

                Debug.WriteLine("UnpairAsync");
                DeviceUnpairingResult dupr = await deviceInfoDisp.DeviceInformation.Pairing.UnpairAsync();
                string unpairResult = $"Unpairing result = {dupr.Status}";
                Debug.WriteLine(unpairResult);
                msgTextBlock.Text += $"{unpairResult}\r";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Unpair exception = " + ex.Message);
                msgTextBlock.Text += $"Unpair Failed {ex.Message}\r";
            }
        }

        // Disable notifications to specified GATT characteristic
        private async Task disableSensor(GattDeviceService service)
        {
            Debug.WriteLine("Begin disable of sensor");

            // Disable notifications
            if (activeCharacteristic != null)
            {
                GattCharacteristic characteristic = activeCharacteristic;
                if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                {
                    GattCommunicationStatus status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                }
            }

            activeCharacteristic = null;
            service.Dispose();

            Debug.WriteLine("End disable for sensor");
        }

    }
}
