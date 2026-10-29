using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

/// <summary>A full frame for one controller: one Rgb buffer per zone. Owned by a Mailbox, recycled forever.</summary>
public sealed class Frame
{
    internal Frame(ControllerDescriptor controller)
    {
        Controller = controller.Id;
        Zones = controller.Zones.Select(z => new Rgb[z.LedCount]).ToArray();
    }

    public ControllerId Controller { get; }

    public Rgb[][] Zones { get; }

    public long Index { get; internal set; }

    public double T { get; internal set; }
}

/// <summary>Single-slot mailbox publish contract (IMPLEMENTATION_PLAN §1; loops per ARCHITECTURE-D2 §1).</summary>
public interface IFrameSink
{
    void Publish(ControllerId id, Frame frame);

    /// <summary>Publishes a compiled loop for an UploadedLoop zone; the latest loop wins (upload coalescing).</summary>
    void PublishLoop(ControllerId id, ZoneId zone, LoopFrames loop) =>
        throw new NotSupportedException($"{GetType().Name} does not carry uploaded loops.");
}

/// <summary>A loop waiting in a mailbox, with the zone it is for.</summary>
public sealed record PendingLoop(ZoneId Zone, LoopFrames Loop);

/// <summary>
/// Lock-free triple buffer: the renderer owns "back", the writer owns "front", "middle" is exchanged atomically.
/// Publishing never blocks and never queues — the latest frame wins.
/// </summary>
public sealed class Mailbox : IDisposable
{
    private const int DirtyBit = 4;
    private readonly Frame[] _frames;
    private readonly AutoResetEvent _signal = new(false);
    private int _middle = 1;
    private int _back = 2;
    private int _front;
    private long _published;
    private long _consumed;
    private readonly PendingLoop?[] _loops;
    private CancellationTokenSource _loopSuperseded = new();
    private long _loopsPublished;
    private long _loopsConsumed;

    internal Mailbox(ControllerDescriptor controller)
    {
        Controller = controller;
        _frames = [new Frame(controller), new Frame(controller), new Frame(controller)];
        _loops = new PendingLoop?[controller.Zones.Count];
    }

    public ControllerDescriptor Controller { get; }

    /// <summary>The buffer the renderer may fill next (renderer thread only).</summary>
    public Frame Back => _frames[_back];

    public long Published => Interlocked.Read(ref _published);

    public long Consumed => Interlocked.Read(ref _consumed);

    /// <summary>Frames overwritten before the writer took them (by design under latest-frame-wins).</summary>
    public long Superseded => Published - Consumed;

    public WaitHandle Signal => _signal;

    public long LoopsPublished => Interlocked.Read(ref _loopsPublished);

    public long LoopsConsumed => Interlocked.Read(ref _loopsConsumed);

    /// <summary>Loops replaced by a newer one before the writer took them: the coalesced uploads.</summary>
    public long LoopsSuperseded => LoopsPublished - LoopsConsumed;

    /// <summary>
    /// Cancelled when a newer loop is published. The writer passes it to an upload so an upload still queued behind another
    /// controller's on the same sender is dropped in favour of the newer loop (ARCHITECTURE-D2 §4).
    /// </summary>
    public CancellationToken LoopSuperseded => Volatile.Read(ref _loopSuperseded).Token;

    /// <summary>Replaces the zone's pending loop; never blocks, never queues.</summary>
    internal void PublishLoop(ZoneId zone, LoopFrames loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        var z = -1;
        for (var i = 0; i < Controller.Zones.Count; i++)
        {
            if (Controller.Zones[i].Id == zone)
            {
                z = i;
                break;
            }
        }

        if (z < 0)
        {
            throw new ArgumentException($"{Controller.Id} has no zone {zone}.", nameof(zone));
        }

        Interlocked.Exchange(ref _loops[z], new PendingLoop(zone, loop));
        Interlocked.Increment(ref _loopsPublished);
        var old = Interlocked.Exchange(ref _loopSuperseded, new CancellationTokenSource());
        old.Cancel(); // not disposed: the writer may still hold its token (no timer, so nothing leaks)
        _signal.Set();
    }

    /// <summary>Takes the newest pending loop of the first zone that has one (writer thread only).</summary>
    public bool TryConsumeLoop(out PendingLoop loop)
    {
        for (var i = 0; i < _loops.Length; i++)
        {
            if (Interlocked.Exchange(ref _loops[i], null) is { } l)
            {
                Interlocked.Increment(ref _loopsConsumed);
                loop = l;
                return true;
            }
        }

        loop = null!;
        return false;
    }

    /// <summary>True when <paramref name="zone"/> has a loop waiting.</summary>
    public bool IsLoopPending(ZoneId zone)
    {
        for (var i = 0; i < _loops.Length; i++)
        {
            if (Controller.Zones[i].Id == zone)
            {
                return Volatile.Read(ref _loops[i]) is not null;
            }
        }

        return false;
    }

    /// <summary>True when any zone has a loop waiting.</summary>
    public bool HasPendingLoop
    {
        get
        {
            for (var i = 0; i < _loops.Length; i++)
            {
                if (Volatile.Read(ref _loops[i]) is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal void Publish(Frame frame)
    {
        if (!ReferenceEquals(frame, _frames[_back]))
        {
            throw new InvalidOperationException("Only the mailbox's back buffer can be published.");
        }

        var old = Interlocked.Exchange(ref _middle, _back | DirtyBit);
        _back = old & 3;
        Interlocked.Increment(ref _published);
        _signal.Set();
    }

    /// <summary>Takes the newest published frame if there is one (writer thread only).</summary>
    public bool TryConsume(out Frame frame)
    {
        if ((Volatile.Read(ref _middle) & DirtyBit) == 0)
        {
            frame = _frames[_front];
            return false;
        }

        var old = Interlocked.Exchange(ref _middle, _front);
        _front = old & 3;
        Interlocked.Increment(ref _consumed);
        frame = _frames[_front];
        return true;
    }

    public void Dispose()
    {
        _signal.Dispose();
        _loopSuperseded.Dispose();
    }
}

/// <summary>One mailbox per controller.</summary>
public sealed class FrameBus : IFrameSink, IDisposable
{
    private readonly object _gate = new();
    private Dictionary<ControllerId, Mailbox> _boxes = [];

    public Mailbox Register(ControllerDescriptor controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        lock (_gate)
        {
            if (_boxes.TryGetValue(controller.Id, out var existing))
            {
                return existing;
            }

            var box = new Mailbox(controller);
            _boxes = new Dictionary<ControllerId, Mailbox>(_boxes) { [controller.Id] = box };
            return box;
        }
    }

    public void Unregister(ControllerId id)
    {
        lock (_gate)
        {
            if (_boxes.TryGetValue(id, out var box))
            {
                var copy = new Dictionary<ControllerId, Mailbox>(_boxes);
                copy.Remove(id);
                _boxes = copy;
                box.Dispose();
            }
        }
    }

    public Mailbox? this[ControllerId id] => _boxes.TryGetValue(id, out var b) ? b : null;

    public IReadOnlyCollection<Mailbox> Mailboxes => _boxes.Values;

    /// <summary>
    /// Publish only while this exact render-plan mailbox remains registered. Device teardown can retire a mailbox before
    /// discovery recompiles the plan; the old frame must neither crash the clock nor enter a replacement with the same id.
    /// The short gate also prevents disposal of the signal between the identity check and publication.
    /// </summary>
    internal bool TryPublishCurrent(Mailbox expected, Frame frame, Mailbox? dependency = null)
    {
        lock (_gate)
        {
            if (!_boxes.TryGetValue(expected.Controller.Id, out var current) || !ReferenceEquals(current, expected)) return false;
            // A local follower may not display an old source generation after a detach/replacement. The renderer already
            // owns this back buffer; publish black atomically with this check while discovery recompiles its plan.
            if (dependency is not null && (!_boxes.TryGetValue(dependency.Controller.Id, out var source) || !ReferenceEquals(source, dependency)))
                foreach (var zone in frame.Zones) Array.Clear(zone);
            current.Publish(frame);
            return true;
        }
    }

    /// <summary>The same lifetime check for automatic loop publication; explicit PublishLoop keeps its strict contract.</summary>
    internal bool TryPublishCurrentLoop(Mailbox expected, ZoneId zone, LoopFrames loop)
    {
        lock (_gate)
        {
            if (!_boxes.TryGetValue(expected.Controller.Id, out var current) || !ReferenceEquals(current, expected)) return false;
            current.PublishLoop(zone, loop);
            return true;
        }
    }

    public void Publish(ControllerId id, Frame frame)
    {
        if (!_boxes.TryGetValue(id, out var box))
        {
            throw new KeyNotFoundException($"Controller {id} is not registered on the frame bus.");
        }

        box.Publish(frame);
    }

    public void PublishLoop(ControllerId id, ZoneId zone, LoopFrames loop)
    {
        if (!_boxes.TryGetValue(id, out var box))
        {
            throw new KeyNotFoundException($"Controller {id} is not registered on the frame bus.");
        }

        box.PublishLoop(zone, loop);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var b in _boxes.Values)
            {
                b.Dispose();
            }

            _boxes = [];
        }
    }
}
