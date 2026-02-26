using System.Collections.Generic;
using System.ComponentModel.Composition;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;

namespace AIWeather.Equipment
{
    [Export(typeof(IEquipmentProvider))]
    public class AIWeatherSafetyMonitorProvider : IEquipmentProvider<ISafetyMonitor>, IEquipmentProvider
    {
        public string Name => "AI Weather";

        public IList<ISafetyMonitor> GetEquipment()
        {
            return new List<ISafetyMonitor> { AIWeatherSafetyMonitor.Instance };
        }
    }
}
