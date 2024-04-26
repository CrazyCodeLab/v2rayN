using Microsoft.Win32;
using Splat;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using v2rayN.Models;
using v2rayN.ViewModels;
using v2rayN.Resx;
using System.Windows;

namespace v2rayN.Handler
{
    public sealed class MainFormHandler
    {
        private static readonly Lazy<MainFormHandler> instance = new(() => new());
        public static MainFormHandler Instance => instance.Value;
        private static Config _config = null;
        //定义一个节点 id 的映射表，记录节点 id 对应的延迟、速度、测试时间(long类型)
        public static Dictionary<string, (int delay, string speed, long time)> dictSpeedtest = new();
        private static string _currentServerId = "";

        public Icon GetNotifyIcon(Config config)
        {
            try
            {
                int index = (int)config.sysProxyType;

                //Load from routing setting
                var createdIcon = GetNotifyIcon4Routing(config);
                if (createdIcon != null)
                {
                    return createdIcon;
                }

                //Load from local file
                var fileName = Utils.GetPath($"NotifyIcon{index + 1}.ico");
                if (File.Exists(fileName))
                {
                    return new Icon(fileName);
                }
                return index switch
                {
                    0 => Properties.Resources.NotifyIcon1,
                    1 => Properties.Resources.NotifyIcon2,
                    2 => Properties.Resources.NotifyIcon3,
                    3 => Properties.Resources.NotifyIcon2,
                    _ => Properties.Resources.NotifyIcon1, // default
                };
            }
            catch (Exception ex)
            {
                Logging.SaveLog(ex.Message, ex);
                return Properties.Resources.NotifyIcon1;
            }
        }

        public System.Windows.Media.ImageSource GetAppIcon(Config config)
        {
            int index = 1;
            switch (config.sysProxyType)
            {
                case ESysProxyType.ForcedClear:
                    index = 1;
                    break;

                case ESysProxyType.ForcedChange:
                case ESysProxyType.Pac:
                    index = 2;
                    break;

                case ESysProxyType.Unchanged:
                    index = 3;
                    break;
            }
            return BitmapFrame.Create(new Uri($"pack://application:,,,/Resources/NotifyIcon{index}.ico", UriKind.RelativeOrAbsolute));
        }

        private Icon? GetNotifyIcon4Routing(Config config)
        {
            try
            {
                if (!config.routingBasicItem.enableRoutingAdvanced)
                {
                    return null;
                }

                var item = ConfigHandler.GetDefaultRouting(config);
                if (item == null || Utils.IsNullOrEmpty(item.customIcon) || !File.Exists(item.customIcon))
                {
                    return null;
                }

                Color color = ColorTranslator.FromHtml("#3399CC");
                int index = (int)config.sysProxyType;
                if (index > 0)
                {
                    color = (new[] { Color.Red, Color.Purple, Color.DarkGreen, Color.Orange, Color.DarkSlateBlue, Color.RoyalBlue })[index - 1];
                }

                int width = 128;
                int height = 128;

                Bitmap bitmap = new(width, height);
                Graphics graphics = Graphics.FromImage(bitmap);
                SolidBrush drawBrush = new(color);

                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                //graphics.FillRectangle(drawBrush, new Rectangle(0, 0, width, height));
                graphics.DrawImage(new Bitmap(item.customIcon), 0, 0, width, height);
                graphics.FillEllipse(drawBrush, width / 2, width / 2, width / 2, width / 2);

                Icon createdIcon = Icon.FromHandle(bitmap.GetHicon());

                drawBrush.Dispose();
                graphics.Dispose();
                bitmap.Dispose();

                return createdIcon;
            }
            catch (Exception ex)
            {
                Logging.SaveLog(ex.Message, ex);
                return null;
            }
        }

        public void Export2ClientConfig(ProfileItem item, Config config)
        {
            if (item == null)
            {
                return;
            }
            if (item.configType == EConfigType.Custom)
            {
                Locator.Current.GetService<NoticeHandler>()?.Enqueue(ResUI.NonVmessService);
                return;
            }

            SaveFileDialog fileDialog = new()
            {
                Filter = "Config|*.json",
                FilterIndex = 2,
                RestoreDirectory = true
            };
            if (fileDialog.ShowDialog() != true)
            {
                return;
            }
            string fileName = fileDialog.FileName;
            if (Utils.IsNullOrEmpty(fileName))
            {
                return;
            }
            if (CoreConfigHandler.GenerateClientConfig(item, fileName, out string msg, out string content) != 0)
            {
                Locator.Current.GetService<NoticeHandler>()?.Enqueue(msg);
            }
            else
            {
                msg = string.Format(ResUI.SaveClientConfigurationIn, fileName);
                Locator.Current.GetService<NoticeHandler>()?.SendMessageAndEnqueue(msg);
            }
        }

        public void UpdateTask(Config config, Action<bool, string> update)
        {
            Task.Run(() => UpdateTaskRunSubscription(config, update));
            Task.Run(() => UpdateTaskRunGeo(config, update));
        }

        private async Task UpdateTaskRunSubscription(Config config, Action<bool, string> update)
        {
            _config = config;
            await Task.Delay(60000);
            Logging.SaveLog("UpdateTaskRunSubscription");

            var updateHandle = new UpdateHandle();
            var downloadHandle = new DownloadHandle();
            var mainWindow = MainWindowViewModel.Instance;
            while (true)
            {
                var updateTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
                var lstSubs = LazyConfig.Instance.SubItems()
                            .Where(t => t.autoUpdateInterval > 0)
                            .Where(t => updateTime - t.updateTime >= t.autoUpdateInterval * 60)
                            .ToList();
                var curUrl = "";
                foreach (var item in lstSubs)
                {
                     if (item.id == config.subIndexId)
                     {
                        curUrl = item.url;
                        break;
                     }
                }
                var subStr = await downloadHandle.TryDownloadString(curUrl, false, "");

                foreach (var item in lstSubs)
                {
                    updateHandle.UpdateSubscriptionProcess(config, item.id, true, (bool success, string msg) =>
                    {
                        update(success, msg);
                        if (success)
                        {
                            Logging.SaveLog("subscription" + msg);
                            // 如果是当前配置, 则测速并自动选择
                            if (item.id == config.subIndexId && curUrl != "")
                            {
                                var lstSelecteds = ConfigHandler.AddBatchServers2(config, subStr, config.subIndexId, true);
                                var _coreHandler = new CoreHandler(config, UpdateHandler);
                                new SpeedtestHandler(config, _coreHandler, lstSelecteds, ESpeedActionType.Realping, UpdateSpeedtestHandler);
                            }
                        }
                    });
                    item.updateTime = updateTime;
                    ConfigHandler.AddSubItem(config, item);

                    await Task.Delay(5000);
                }
                await Task.Delay(60000);
            }
        }

        private void UpdateHandler(bool notify, string msg)
        {
            var _noticeHandler = Locator.Current.GetService<NoticeHandler>();
            msg = "CrazyBunQnQ: " + msg;
            _noticeHandler?.SendMessage(msg);
            if (notify)
            {
                _noticeHandler?.Enqueue(msg);
            }
        }

        private void UpdateSpeedtestHandler(string indexId, string delay, string speed)
        {
            if (indexId == null)
                return;
            var mainWindow = MainWindowViewModel.Instance;
            if ("all completed".Equals(indexId) && _config != null)
            {
                mainWindow.SortServer("delayVal");
                // 获取最低延迟的节点
                var lowestIndexId = GetLowestDelayKeyWithin2Hours();
                var lowobj = dictSpeedtest[lowestIndexId];
                if (lowestIndexId.Equals(_currentServerId))
                {
                    return;
                }
                UpdateHandler(false, "测速完成，最低延迟节点：" + lowestIndexId + "，自动切换中...");
                // 设置激活服务器
                mainWindow.SetDefaultServer(lowestIndexId);
                _currentServerId = lowestIndexId;
                return;
            }
            Application.Current?.Dispatcher.Invoke((Action)(() =>
            {
                mainWindow.SetTestResult(indexId, delay, speed);
            }));
            // 判断 delay 是否为数字字符串
            if (decimal.TryParse(delay, out decimal delayNumber) && delayNumber > 0)
            {
                // 如果 dictSpeedtest 中包含 indexId 则更新，否则添加
                if (dictSpeedtest.ContainsKey(indexId))
                {
                    dictSpeedtest[indexId] = ((int)delayNumber, dictSpeedtest[indexId].speed, DateTimeOffset.Now.ToUnixTimeMilliseconds());
                }
                else
                {
                    dictSpeedtest.Add(indexId, ((int)delayNumber, speed, DateTimeOffset.Now.ToUnixTimeMilliseconds()));
                }
            } else if (decimal.TryParse(speed, out decimal speedNumber) && speedNumber > 0)
            {
                // 如果 dictSpeedtest 中包含 indexId 则更新，否则添加
                if (dictSpeedtest.ContainsKey(indexId))
                {
                    dictSpeedtest[indexId] = (dictSpeedtest[indexId].delay, speed, DateTimeOffset.Now.ToUnixTimeMilliseconds());
                }
                else
                {
                    dictSpeedtest.Add(indexId, (0, speed, DateTimeOffset.Now.ToUnixTimeMilliseconds()));
                }
            }
        }

        private string GetLowestDelayKeyWithin2Hours()
        {
            // 获取当前时间的 Unix 时间戳（秒）
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 2小时的秒数
            long twoHoursInSeconds = 2 * 60 * 60 * 1000;

            // 筛选出测试时间在2小时以内的节点
            var recentTests = dictSpeedtest.Where(kvp => (now - kvp.Value.time) <= twoHoursInSeconds);

            // 从筛选结果中找到延迟最低的条目
            var minDelayEntry = recentTests.OrderBy(kvp => kvp.Value.delay).FirstOrDefault();

            // 返回具有最低延迟的 key，如果没有符合条件的条目，返回 null
            return minDelayEntry.Key; // 如果列表为空，Key 将是 null
        }

        private async Task UpdateTaskRunGeo(Config config, Action<bool, string> update)
        {
            var autoUpdateGeoTime = DateTime.Now;

            await Task.Delay(1000 * 120);
            Logging.SaveLog("UpdateTaskRunGeo");

            var updateHandle = new UpdateHandle();
            while (true)
            {
                var dtNow = DateTime.Now;
                if (config.guiItem.autoUpdateInterval > 0)
                {
                    if ((dtNow - autoUpdateGeoTime).Hours % config.guiItem.autoUpdateInterval == 0)
                    {
                        updateHandle.UpdateGeoFileAll(config, (bool success, string msg) =>
                        {
                            update(false, msg);
                        });
                        autoUpdateGeoTime = dtNow;
                    }
                }

                await Task.Delay(1000 * 3600);
            }
        }

        public void RegisterGlobalHotkey(Config config, Action<EGlobalHotkey> handler, Action<bool, string> update)
        {
            HotkeyHandler.Instance.UpdateViewEvent += update;
            HotkeyHandler.Instance.HotkeyTriggerEvent += handler;
            HotkeyHandler.Instance.Load();
        }
    }
}