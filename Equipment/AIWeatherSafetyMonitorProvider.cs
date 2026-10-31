using System.Collections.Generic;
using System.ComponentModel.Composition;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
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
        public AIWeatherSafetyMonitorProvider(IImageDataFactory imageDataFactory, IProfileService profileService, IGuiderMediator guiderMediator, IWeatherDataMediator weatherDataMediator)
        {
            AIWeatherSafetyMonitor.Instance.SetImageDataFactory(imageDataFactory);
            AIWeatherSafetyMonitor.Instance.SetProfileService(profileService);
            AIWeatherSafetyMonitor.Instance.SetGuiderMediator(guiderMediator);
            AIWeatherSafetyMonitor.Instance.SetWeatherDataMediator(weatherDataMediator);
        }

        public IList<ISafetyMonitor> GetEquipment()
        {
            return new List<ISafetyMonitor> { AIWeatherSafetyMonitor.Instance };
        }
    }
}
