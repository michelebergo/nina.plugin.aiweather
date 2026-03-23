using AIWeather.Equipment;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.SequenceItems {

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "AI Weather - Stop Monitoring")]
    [ExportMetadata("Description", "Stops AI Weather all-sky camera monitoring")]
    [ExportMetadata("Category", "AI Weather")]
    [ExportMetadata("Icon", "AIWeather_StopIcon")]
    public class StopMonitoringInstruction : SequenceItem {

        [ImportingConstructor]
        public StopMonitoringInstruction() {
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var monitor = AIWeatherSafetyMonitor.Instance;

                if (!monitor.Connected) {
                    Logger.Info("AI Weather monitoring is not running");
                    progress?.Report(new ApplicationStatus() {
                        Status = "AI Weather: Not currently monitoring"
                    });
                    return Task.CompletedTask;
                }

                Logger.Info("AI Weather: Stopping monitoring from sequencer instruction");
                monitor.Disconnect();

                Logger.Info("AI Weather: Monitoring stopped from sequencer");
                progress?.Report(new ApplicationStatus() {
                    Status = "AI Weather: Monitoring stopped"
                });
            } catch (Exception ex) {
                Logger.Error($"AI Weather: Error stopping monitoring from sequencer: {ex.Message}", ex);
            }

            return Task.CompletedTask;
        }

        public override object Clone() {
            return new StopMonitoringInstruction() { Icon = Icon, Name = Name };
        }

        public override string ToString() {
            return "AI Weather - Stop Monitoring";
        }
    }
}
