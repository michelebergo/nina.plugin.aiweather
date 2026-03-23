using System.ComponentModel.Composition;
using System.Windows;

namespace AIWeather.SequenceItems {
    [Export(typeof(ResourceDictionary))]
    public partial class SequenceItemTemplates : ResourceDictionary {
        public SequenceItemTemplates() {
            InitializeComponent();
        }
    }
}
