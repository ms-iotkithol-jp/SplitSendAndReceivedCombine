using Microsoft.Azure.Devices.Client;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            ShowLog("Connected");
        }

        private void buttonFileSelect_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog().Value)
            {
                tbFileName.Text = dialog.FileName;
                ShowLog($"File Selected - {dialog.FileName}");
                buttonSend.IsEnabled = true;
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
                    var readLength =await fs.ReadAsync(buf, 0, unitSize);
                    var msg = new Message(buf);
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

    }
}
