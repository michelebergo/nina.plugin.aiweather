using System.Collections.Generic;
using System.ComponentModel.Composition;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

namespace AIWeather.Equipment
{
    [Export(typeof(IEquipmentProvider))]
    public class AIWeatherSafetyMonitorProvider : IEquipmentProvider<ISafetyMonitor>, IEquipmentProvider
    {
        public string Name => "AI Weather";

        [ImportingConstructor]
        public AIWeatherSafetyMonitorProvider(IImageDataFactory imageDataFactory, IProfileService profileService)
        {
            AIWeatherSafetyMonitor.Instance.SetImageDataFactory(imageDataFactory);
            AIWeatherSafetyMonitor.Instance.SetProfileService(profileService);
        }

        public IList<ISafetyMonitor> GetEquipment()
        {
            return new List<ISafetyMonitor> { AIWeatherSafetyMonitor.Instance };
        }
    }
}
