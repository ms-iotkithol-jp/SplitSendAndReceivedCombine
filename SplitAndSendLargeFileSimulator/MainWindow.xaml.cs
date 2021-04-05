using Microsoft.Azure.Devices.Client;
using Microsoft.Extensions.Configuration;
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
        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var config = new ConfigurationBuilder().
                SetBasePath(Directory.GetCurrentDirectory()).
                AddJsonFile("appsettings.json",optional:true,reloadOnChange:true).Build();
            var csSection = config.GetSection("Connectionstrings");
            this.tbIoTCS.Text = csSection["iothubconnectionstring"];
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
            using (var fs = File.OpenRead(tbFileName.Text))
            {
                int unitSize = CalcUnitSize(fs);
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
                int index = 0;
                while (index < numOfFrags)
                {
                    try
                    {
                        fs.Seek(index * unitSize, SeekOrigin.Begin);
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
                            unitSize = readLength;
                        }
                        var msg = new Message(sendBuf);
                        msg.Properties.Add("msgtype", "split");
                        msg.Properties.Add("dataid", fid);
                        msg.Properties.Add("index", $"{index}");
                        msg.Properties.Add("total", $"{numOfFrags}");
                        msg.Properties.Add("ext", fi.Extension);
                        msg.Properties.Add("unitsize", $"{unitSize}");
                        await deviceClient.SendEventAsync(msg);
                        ShowLog($"Send[{index}]");
                        index++;
                    }
                    catch (Exception ex)
                    {
                        ShowLog($"Failed - {ex.Message}");
                    }
                }
                var endTime = DateTime.Now;
                var deltaTime = endTime.Subtract(startTime);
                ShowLog($"Start:{startTime.ToString("yyyy-MM-dd HH:mm:ss.fff")}-End:{endTime.ToString("yyyy-MM-dd HH:mm:ss.fff")} delta={deltaTime.TotalMilliseconds}");
            }
        }

        private int CalcUnitSize(FileStream fs)
        {
            int unitSize = 0;
            if (cbSpecifiedByNo.IsChecked.Value)
            {
                var aOfFrags = int.Parse(tbAoFrags.Text);
                unitSize = (int)Math.Ceiling((double)fs.Length / (double)aOfFrags);
                tbSendingUnitSize.Text = $"{unitSize}";
            }
            else
            {
                unitSize = int.Parse(tbSendingUnitSize.Text);
            }

            return unitSize;
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

        List<(Dictionary<string, string> requestParams, byte[] data)> sendingFragments = new List<(Dictionary<string, string> requestParams, byte[] data)>();
        List<int> sendingOrder = new List<int>();

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
            sendingFragments.Clear();
            sendingOrder.Clear();

            using (currentFS = File.OpenRead(tbFileName.Text))
            {
                try
                {
                    var fi = new FileInfo(tbFileName.Text);
                    currentDataId = fi.Name;
                    currentExtName = fi.Extension;
                    if (!string.IsNullOrEmpty(currentExtName))
                    {
                        currentDataId = currentDataId.Substring(0, currentDataId.Length - currentExtName.Length);
                    }
                    currentIndex = 0;
                    var contentLengh = currentFS.Length;
                    currentUnitSize = CalcUnitSize(currentFS);
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
                        if (response.StatusCode == System.Net.HttpStatusCode.OK || response.StatusCode == System.Net.HttpStatusCode.Accepted)
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


                            for (int index = 0; index < currentTotal; index++)
                            {
                                var buf = new byte[currentUnitSize];
                                var readSize = await currentFS.ReadAsync(buf, 0, currentUnitSize);
                                if (readSize != currentUnitSize)
                                {
                                    currentUnitSize = readSize;
                                }
                                var requestParams4NOtify = new Dictionary<string, string>()
                    {
                        {"instanceid",currentInstanceId },
                        {"index",$"{index}" },
                        {"total",$"{currentTotal}" },
                        {"size",$"{currentUnitSize}" }
                    };
                                sendingFragments.Add((requestParams4NOtify, buf));
                                sendingOrder.Add(index);
                            }
                            if (cbRandamOrder.IsChecked.Value)
                            {
                                var tempOrder = new List<int>();
                                while (sendingOrder.Count > 0)
                                {
                                    int next = orderRandom.Next(sendingOrder.Count-1);
                                    tempOrder.Add(sendingOrder[next]);
                                    sendingOrder.RemoveAt(next);
                                }
                                sendingOrder = tempOrder;
                            }
                            currentIndex = 0;
                        }
                        else
                        {
                            ShowLog($"Error ? - {contentStr}");
                        }
                    }
                }
                catch(Exception ex)
                {
                    ShowLog($"Exception - {ex.Message}");
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
            if (sendingFragments.Count>0 && currentIndex < currentTotal)
            {
                using (var httpClient = new HttpClient())
                {
                    var requestUri = $"{tbNotifyUri.Text}?{await new FormUrlEncodedContent(sendingFragments[currentIndex].requestParams).ReadAsStringAsync()}";
                    var httpContent = new ByteArrayContent(sendingFragments[currentIndex].data);
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

        private void cbSpecifiedByNo_Checked(object sender, RoutedEventArgs e)
        {
            if (cbSpecifiedByNo.IsChecked.Value)
            {
                tbSendingUnitSize.IsEnabled = false;
                tbAoFrags.IsEnabled = true;
            }
            else
            {
                tbSendingUnitSize.IsEnabled = true;
                tbAoFrags.IsEnabled = false;
            }
        }

        Random orderRandom;
        private void cbRandamOrder_Checked(object sender, RoutedEventArgs e)
        {
            orderRandom = new Random(DateTime.Now.Millisecond);
        }
    }
}