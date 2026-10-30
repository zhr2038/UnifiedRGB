using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.Host.Tests;

public sealed class CaseTopologyLifetimeTests
{
    [Fact]
    public void Unloaded_topology_releases_the_scene_collection_and_rejoins_once_when_loaded_again()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var first = new TrackedZones(); var second = new TrackedZones();
                var view = new CaseTopology { Zones = first };
                first.Listeners.Should().Be(0, "an unshown view must not be rooted by its scene");
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                first.Listeners.Should().Be(1);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                first.Listeners.Should().Be(1);
                view.Zones = second;
                first.Listeners.Should().Be(0); second.Listeners.Should().Be(1);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                second.Listeners.Should().Be(0, "navigation must release the scene's reference to the view");
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                second.Listeners.Should().Be(1);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                second.Listeners.Should().Be(0);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TrackedZones : IEnumerable, INotifyCollectionChanged
    {
        private NotifyCollectionChangedEventHandler? _changed;
        public int Listeners => _changed?.GetInvocationList().Length ?? 0;
        public event NotifyCollectionChangedEventHandler? CollectionChanged { add => _changed += value; remove => _changed -= value; }
        public IEnumerator GetEnumerator() => Array.Empty<StudioZone>().GetEnumerator();
    }
}
