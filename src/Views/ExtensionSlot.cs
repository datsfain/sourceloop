using Avalonia;
using Avalonia.Controls;

namespace SourceGit.Views
{
    public class ExtensionSlot : StackPanel
    {
        public static readonly StyledProperty<Extensions.ExtensionPoint> PointProperty =
            AvaloniaProperty.Register<ExtensionSlot, Extensions.ExtensionPoint>(nameof(Point));

        public Extensions.ExtensionPoint Point
        {
            get => GetValue(PointProperty);
            set => SetValue(PointProperty, value);
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();

            foreach (var factory in Extensions.ExtensionHost.GetFactories(Point))
                Children.Add(factory());
        }
    }
}
