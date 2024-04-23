using Microsoft.Win32;
using Splat;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using v2rayN.Models;
using v2rayN.Resx;

namespace v2rayN.Handler
{
    public sealed class MainFormHandler
    {
        private static readonly Lazy<MainFormHandler> instance = new(() => new());
        public static MainFormHandler Instance => instance.Value;
        //定义一个节点 id 的映射表，记录节点 id 对应的延迟、速度、测试时间(long类型)
        public static Dictionary<string, (int delay, string speed, long time)> dictSpeedtest = new();

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
            await Task.Delay(60000);
            Logging.SaveLog("UpdateTaskRunSubscription");

            var updateHandle = new UpdateHandle();
            while (true)
            {
                var updateTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
                var lstSubs = LazyConfig.Instance.SubItems()
                            .Where(t => t.autoUpdateInterval > 0)
                            .Where(t => updateTime - t.updateTime >= t.autoUpdateInterval * 60)
                            .ToList();

                foreach (var item in lstSubs)
                {
                    updateHandle.UpdateSubscriptionProcess(config, item.id, true, async (bool success, string msg) =>
                    {
                        update(success, msg);
                        if (success)
                        {
                            Logging.SaveLog("subscription" + msg);
                            // 如果是当前配置, 则测速并自动选择
                            if (item.id == config.subIndexId)
                            {
                                // 控制台输出日志
                                Logging.SaveLog("UpdateTaskRunSubscription Test");
                                var downloadHandle = new DownloadHandle();
                                var subStr = await downloadHandle.TryDownloadString(item.url, false, "");
                                var lstSelecteds = ConfigHandler.AddBatchServers2(config, subStr, config.subIndexId, true);
                                var _coreHandler = new CoreHandler(config, UpdateHandler);
                                new SpeedtestHandler(config, _coreHandler, lstSelecteds, ESpeedActionType.Mixedtest, UpdateSpeedtestHandler);
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
        }

        private void UpdateSpeedtestHandler(string indexId, string delay, string speed)
        {
            // 打印 indexId, delay, speed
            Logging.SaveLog($"UpdateSpeedtestHandler {indexId} {delay} {speed}");
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
            } else if (decimal.TryParse(speed, out decimal speedNumber))
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