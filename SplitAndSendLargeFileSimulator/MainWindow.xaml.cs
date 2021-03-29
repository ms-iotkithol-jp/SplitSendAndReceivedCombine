using Microsoft.Azure.Devices.Client;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace SplitAndSendLargeFileSimulator
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly string iothubCS = "<- Device Connection String ->";
        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            this.tbIoTCS.Text = iothubCS;
        }

        private DeviceClient deviceClient;

        private async void buttonConnect_Click(object sender, RoutedEventArgs e)
        {
            deviceClient = DeviceClient.CreateFromConnectionString(tbIoTCS.Text);
            await deviceClient.OpenAsync();
            buttonFileSelect.IsEnabled = true;
            buttonConnect.IsEnabled = false;
            ShowLog("Connected");
        }

        private void buttonFileSelect_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog().Value)
            {
                tbFileName.Text = dialog.FileName;
                ShowLog($"File Selected - {dialog.FileName}");
                if (!buttonConnect.IsEnabled)
                {
                    buttonSend.IsEnabled = true;
                }
                else
                {
                    tbDFDataId.Text = "";
                    tbDFExt.Text = "";
                    tbDFFileSize.Text = "";
                    tbDFIndex.Text = "";
                    tbDFSize.Text = "";
                    tbDFTotal.Text = "";
                    tbDFInstanceId.Text = "";
                }
            }
        }

        private async void buttonSend_Click(object sender, RoutedEventArgs e)
        {
            var unitSize = int.Parse(tbSendingUnitSize.Text);
            using (var fs = File.OpenRead(tbFileName.Text))
            {
                if (unitSize >= fs.Length)
                {
                    MessageBox.Show("selected file is small!");
                    return;
                }
                int numOfFrags = (int)Math.Ceiling((double)fs.Length / (double)unitSize);
                string fid = Guid.NewGuid().ToString();
                var buf = new byte[unitSize];
                var fi = new FileInfo(tbFileName.Text);
                ShowLog($"uploading {fid} by {numOfFrags}");
                var startTime = DateTime.Now;
                for (int i = 0; i < numOfFrags; i++)
                {
                    var readLength = await fs.ReadAsync(buf, 0, unitSize);
                    var sendBuf = buf;
                    if (readLength < unitSize)
                    {
                        sendBuf = new byte[readLength];
                        using (var memStream = new MemoryStream(sendBuf))
                        {
                            await memStream.WriteAsync(buf, 0, readLength);
                            await memStream.FlushAsync();
                        }
                    }
                    var msg = new Message(sendBuf);
                    msg.Properties.Add("msgtype", "split");
                    msg.Properties.Add("dataid", fid);
                    msg.Properties.Add("index", $"{i}");
                    msg.Properties.Add("total", $"{numOfFrags}");
                    msg.Properties.Add("ext", fi.Extension);
                    await deviceClient.SendEventAsync(msg);
                }
                var endTime = DateTime.Now;
                var deltaTime = endTime.Subtract(startTime);
                ShowLog($"Start:{startTime.ToString("yyyy-MM-dd HH:mm:ss.fff")}-End:{endTime.ToString("yyyy-MM-dd HH:mm:ss.fff")} delta={deltaTime.TotalMilliseconds}");
            }
        }

        private void ShowLog(string log)
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
            {
                writer.WriteLine($"[{now}]{log}");
                writer.Write(tbLog.Text);
            }
            tbLog.Text = sb.ToString();
        }

        FileStream currentFS;
        int currentIndex;
        int currentTotal;
        int currentUnitSize;
        string currentInstanceId;
        string currentDataId;
        string currentExtName;

        private async void buttonDFStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(tbStartUri.Text))
            {
                if (string.IsNullOrEmpty(tbBaseUri.Text) || string.IsNullOrEmpty(tbStartUriPart.Text))
                    return;
                tbStartUri.Text = tbBaseUri.Text + tbStartUriPart.Text;
            }
            if (currentFS != null && currentFS.IsAsync)
            {
                currentFS.Close();
                await currentFS.DisposeAsync();
            }
            currentUnitSize = int.Parse(tbSendingUnitSize.Text);
            currentFS = File.OpenRead(tbFileName.Text);
            var fi = new FileInfo(tbFileName.Text);
            currentDataId = fi.Name;
            currentExtName = fi.Extension;
            if (!string.IsNullOrEmpty(currentExtName))
            {
                currentDataId = currentDataId.Substring(0, currentDataId.Length - currentExtName.Length);
            }
            currentIndex = 0;
            var contentLengh = currentFS.Length;
            currentTotal = (int)Math.Ceiling((double)contentLengh / (double)currentUnitSize);

            using (var httpClient = new HttpClient())
            {
                var requestParams = new Dictionary<string, string>()
                {
                    {"dataname",currentDataId },
                    {"extname", currentExtName },
                    {"total",$"{currentTotal}" },
                    {"unitsize",$"{currentUnitSize}" }
                };
                var requestUri = $"{tbStartUri.Text}?{await new FormUrlEncodedContent(requestParams).ReadAsStringAsync()}";
                ShowLog($"Invoking Start - {requestUri}");
                var response = await httpClient.GetAsync(requestUri);
                ShowLog($"Response Code - {response.StatusCode}");
                var contentStr = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == System.Net.HttpStatusCode.OK||response.StatusCode==  System.Net.HttpStatusCode.Accepted)
                {
                    dynamic contentJson = Newtonsoft.Json.JsonConvert.DeserializeObject(contentStr);
                    currentInstanceId = contentJson["id"];
                    ShowLog($"Response - {response.StatusCode}");
                    tbDFDataId.Text = currentDataId;
                    tbDFIndex.Text = $"{currentIndex}";
                    tbDFInstanceId.Text = currentInstanceId;
                    tbDFSize.Text = $"{currentUnitSize}";
                    tbDFTotal.Text = $"{currentTotal}";
                    tbDFExt.Text = currentExtName;
                    tbDFFileSize.Text = $"{fi.Length}";
                    buttonDFNotify.IsEnabled = true;
                    buttonDFStart.IsEnabled = false;
                }
                else
                {
                    ShowLog($"Error ? - {contentStr}");
                }
            }
        }

        private void cbSF_Checked(object sender, RoutedEventArgs e)
        {
            buttonFileSelect.IsEnabled = true;
            buttonDFStart.IsEnabled = true;
        }

        private async void buttonDFNotify_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(tbNotifyUri.Text))
            {
                if(string.IsNullOrEmpty(tbNotifyUriPart.Text) || string.IsNullOrEmpty(tbBaseUri.Text))
                return;
                tbNotifyUri.Text = tbBaseUri.Text + tbNotifyUriPart.Text;
            }
            if (currentFS != null && currentIndex < currentTotal)
            {
                using (var httpClient = new HttpClient())
                {
                    var buf = new byte[currentUnitSize];
                    var readSize = await currentFS.ReadAsync(buf, 0, currentUnitSize);
                    if (readSize != currentUnitSize)
                    {
                        currentUnitSize = readSize;
                    }
                    var requestParams = new Dictionary<string, string>()
                    {
                        {"instanceid",currentInstanceId },
                        {"index",$"{currentIndex++}" },
                        {"total",$"{currentTotal}" },
                        {"size",$"{currentUnitSize}" }
                    };
                    var requestUri = $"{tbNotifyUri.Text}?{await new FormUrlEncodedContent(requestParams).ReadAsStringAsync()}";
                    var httpContent = new ByteArrayContent(buf);
                    ShowLog($"Invoke Notify - {requestUri}");
                    var response = await httpClient.PostAsync(requestUri, httpContent);
                    if (response.StatusCode== System.Net.HttpStatusCode.OK)
                    {
                        ShowLog($"Notify Succeeded");
                        tbDFIndex.Text = $"{currentIndex}";
                    }
                    else
                    {
                        ShowLog($"Notify Error? - {response.StatusCode} - '{await response.Content.ReadAsStringAsync()}'");
                    }
                    
                }
            }
            if (currentIndex >= currentTotal)
            {
                buttonDFNotify.IsEnabled = false;
                buttonDFStart.IsEnabled = true;
            }
        }
    }
}
