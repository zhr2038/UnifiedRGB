namespace UnifiedRgb.Host.Services;

/// <summary>A connected firmware's answer to <see cref="IKeyboardNativePresentationCapability"/>. Any other value is Unavailable.</summary>
public enum KeyboardNativePresentationSupport
{
    Unavailable = 0,
    Available = 1,
}

/// <summary>The one keyboard owner session a capability answer applies to.</summary>
public sealed record KeyboardNativePresentationScope(string DeviceKey, string FirmwareHeader, string ConnectionKind);

/// <summary>
/// Default-off firmware prerequisite: installed native-mode firmware that keeps its lever/case control while it shows
/// Host-authored artwork on its existing native artwork path (entry logo, then the authored keyboard animation/background). No
/// static installation/scope policy alone proves live support: the native actor must still validate fresh protocol2 CAP,
/// input/physical scope and the in-epoch artwork flag before ARM. The registered pure adapter stays Unavailable without exact
/// local installed-image and separately qualified native-route options; legacy lighting verification is not native admission.
/// Only an explicit <see cref="KeyboardNativePresentationSupport.Available"/> for the session opts in; absent, unrecognized or
/// throwing answers are Unavailable. It adds no request, opcode or transport send of its own.
/// </summary>
public interface IKeyboardNativePresentationCapability
{
    KeyboardNativePresentationSupport Evaluate(KeyboardNativePresentationScope scope);
}

/// <summary>What the current native entry last had acknowledged; status and diagnostics only.</summary>
internal enum KeyboardNativeArtwork
{
    None = 0,
    /// <summary>The bounded entry logo.</summary>
    Logo = 1,
    /// <summary>Ordinary page: the case status (or the separately saved, default-off authored frame the selector picks).</summary>
    StatusPage = 2,
    /// <summary>Native idle: no Host artwork, so the keyboard's stored original animation is requested (control stays armed; the
    /// fresh ARM clears the overlay and the session restores the stored selector). Requested, never optically confirmed.</summary>
    DeviceAnimation = 3,
    /// <summary>Native idle: the owner's prepared custom animation frame at the current shared-timeline position.</summary>
    CustomAnimation = 4,
    /// <summary>The transient status/scene-name popup of a valid lever action.</summary>
    InteractionStatus = 5,
}

/// <summary>
/// Pure presentation of one owner session. <see cref="Latest"/> serves the ordinary (non-native) display unchanged: entry logo
/// for <see cref="EntryLogoMs"/> from its acknowledged upload, then the bytes <see cref="KeyboardDisplayAnimation.Select"/> picks
/// through the optional scene overlay.
/// <para>
/// <see cref="Native"/> serves an owner whose firmware declared native presentation. A true native-mode entry (the first request
/// after session start or after the native tick last reported control not armed) shows the Host logo once when one is rendered
/// (none when the keyboard draws its own entry logo). Then idle is either the custom animation (the existing animation
/// preference is on and its prepared frames play on the shared timeline) or, otherwise, no Host artwork at all: the keyboard's
/// stored original animation. Only a valid lever action (<see cref="InteractionBegan"/>) overlays the status page with the scene
/// caption, while the action is pending plus <see cref="InteractionHoldMs"/>, bounded by <see cref="InteractionMaximumMs"/>;
/// afterwards custom idle resumes at the current shared-timeline position. Background status pixels, ARM/CAP, RGB frames and
/// ordinary rendering never open or prolong it. The case status-page preference does not choose native idle.
/// </para>
/// Native control ownership is a separate input: the native tick decides it and this only answers which bytes it asks for. Owner
/// actor only, except the interaction notifications and the volatile status reports; the actor's artwork comparison and lease
/// wake are its clock, so there is no timer or sender here.
/// </summary>
internal sealed class KeyboardNativePresentation(KeyboardDisplayAnimation animation, byte[]? logo, TimeProvider time,
    Func<byte[], byte[]>? overlay = null, Action<byte[]>? frameAcknowledged = null, Func<long>? overlayRevision = null)
{
    /// <summary>Nominal entry overlay; it ends at this bound after the logo's acknowledged upload, never later.</summary>
    internal const int EntryLogoMs = 1200;

    /// <summary>Brief hold of the native interaction popup after a valid action began or its pending outcome arrived.</summary>
    internal const int InteractionHoldMs = 2500;

    /// <summary>The popup never outlasts this bound after the latest valid action, even when an outcome never arrives.</summary>
    internal const int InteractionMaximumMs = 15_000;

    private readonly object _interaction = new();
    private long? _interactionAt; // guarded by _interaction, like the two below
    private long _interactionHold;
    private int _pendingInteractions;
    private bool _entered, _resumeIdle;
    private long? _logoUntil;
    private byte[]? _chosen, _chosenBase, _shown, _shownBase, _overlayBase, _overlaid;
    private KeyboardNativeArtwork _chosenKind;
    private long _overlayNext;
    private long _overlaidRevision;
    private int _shownKind, _idle, _reported;
    private string? _overlayProblem;

    /// <summary>Inside one native-mode entry: the legacy selector must not touch the animation's held frame meanwhile.</summary>
    public bool Entered => _entered;

    public bool LogoAvailable => logo is not null;

    /// <summary>What the current entry last had acknowledged (None outside an entry). Any thread.</summary>
    public KeyboardNativeArtwork Shown => (KeyboardNativeArtwork)Volatile.Read(ref _shownKind);

    /// <summary>The latest native idle selection: None before one, <see cref="KeyboardNativeArtwork.CustomAnimation"/>, or
    /// <see cref="KeyboardNativeArtwork.DeviceAnimation"/> (custom animation off or not playable now). Any thread.</summary>
    public KeyboardNativeArtwork IdleChoice => (KeyboardNativeArtwork)Volatile.Read(ref _idle);

    /// <summary>Why the scene overlay was last unavailable (the ordinary frame was presented instead), or null. Any thread.</summary>
    public string? OverlayProblem => Volatile.Read(ref _overlayProblem);

    /// <summary>
    /// The declared native artwork for now (native tick and lease wake), or null for no Host artwork: the keyboard's stored
    /// original animation. Order: the entry logo, the popup of a valid lever action, the custom animation, else null. The popup
    /// is the status page through the scene overlay; it neither selects nor acknowledges an animation frame, and the shared
    /// timeline keeps its own position meanwhile. After the logo or a popup the custom animation restarts its selection at the
    /// current shared-timeline position (never a held or zero frame). A custom animation that is not playable now (off, not
    /// admitted, preparing, unbound, blocked timeline, tripped breaker) yields null, never the status page or a held frame.
    /// </summary>
    public byte[]? Native(byte[] status, bool newEntry = false)
    {
        // As in Latest: a fresh actor context discards the previous entry even without a separate not-armed tick.
        if (newEntry) Stepped(false);
        if (!_entered) { _entered = true; _logoUntil = null; _resumeIdle = true; }
        var now = time.GetTimestamp();
        if (logo is not null && (_logoUntil is not { } until || now < until))
            return Choose(logo, logo, KeyboardNativeArtwork.Logo);
        if (InteractionOpen(now))
        {
            _resumeIdle = true;
            return Choose(Overlay(status), status, KeyboardNativeArtwork.InteractionStatus);
        }
        if (_resumeIdle)
        {
            animation.EndSession(); // the next selection is the current shared position; no frame held from before
            _resumeIdle = false;
        }
        // The custom idle follows the animation preference alone, independently of the case status-page preference.
        var selected = animation.Select(status, pageEnabled: true, nativeOwnsDisplay: false);
        if (!ReferenceEquals(selected, status))
        {
            Volatile.Write(ref _idle, (int)KeyboardNativeArtwork.CustomAnimation);
            return Choose(selected, selected, KeyboardNativeArtwork.CustomAnimation);
        }
        Volatile.Write(ref _idle, (int)KeyboardNativeArtwork.DeviceAnimation);
        _chosen = _chosenBase = null;
        _chosenKind = KeyboardNativeArtwork.DeviceAnimation;
        return null;
    }

    /// <summary>After each native tick of a declared presentation. Control neither armed nor re-arming for device idle ends the
    /// entry. Active Host artwork was the acknowledged upload of the chosen bytes (see <see cref="Stepped"/>); without it the
    /// keyboard's stored animation was requested. Returns whether Host artwork is active.</summary>
    public bool SteppedNative(bool controlArmed, bool artworkActive)
    {
        if (!controlArmed) return Stepped(false);
        if (artworkActive)
        {
            Acknowledge();
            return true;
        }
        if (_chosen is null)
        {
            _shown = _shownBase = null;
            Volatile.Write(ref _shownKind, (int)KeyboardNativeArtwork.DeviceAnimation);
        }
        return false;
    }

    /// <summary>A valid physical lever action began (centre press accepted or browse step queued). With
    /// <paramref name="awaitsCompletion"/> the popup stays open until <see cref="InteractionCompleted"/>; either way it then
    /// holds <see cref="InteractionHoldMs"/>, within <see cref="InteractionMaximumMs"/> of the latest action. Any thread.</summary>
    public void InteractionBegan(bool awaitsCompletion)
    {
        var now = time.GetTimestamp();
        lock (_interaction)
        {
            if (_interactionAt is not { } at || now - at >= Ticks(InteractionMaximumMs)) _pendingInteractions = 0;
            _interactionAt = now;
            if (awaitsCompletion) _pendingInteractions++;
            _interactionHold = Math.Max(_interactionHold, now + Ticks(InteractionHoldMs));
        }
    }

    /// <summary>The outcome of a pending valid action arrived. Without a pending action it neither opens nor prolongs. Any thread.</summary>
    public void InteractionCompleted()
    {
        var now = time.GetTimestamp();
        lock (_interaction)
        {
            if (_pendingInteractions == 0) return;
            _pendingInteractions--;
            _interactionHold = Math.Max(_interactionHold, now + Ticks(InteractionHoldMs));
        }
    }

    private bool InteractionOpen(long now)
    {
        lock (_interaction)
        {
            if (_interactionAt is not { } at || now - at >= Ticks(InteractionMaximumMs)) return false;
            if (_pendingInteractions > 0 || now < _interactionHold) return true;
        }
        // The scene caption of that action (loading, then its result) may keep the popup within the bound; it never opens one.
        return overlayRevision?.Invoke() is { } revision && revision != 0;
    }

    private long Ticks(int ms) => ms * time.TimestampFrequency / 1000;

    /// <summary>The ordinary (non-native) display artwork for now (upload check and lease wake). Without a rendered logo the
    /// overlay is skipped.</summary>
    public byte[] Latest(byte[] status, bool pageEnabled, bool newEntry = false)
    {
        // The actor chooses bytes before ARM. A fresh actor context must discard the previous entry even when no separate
        // not-armed presentation tick occurred between the old context and this first selection.
        if (newEntry) Stepped(false);
        if (!_entered) { _entered = true; _logoUntil = null; }
        if (logo is not null && (_logoUntil is not { } until || time.GetTimestamp() < until))
            return Choose(logo, logo, KeyboardNativeArtwork.Logo);
        var selected = animation.Select(status, pageEnabled, nativeOwnsDisplay: false); // OFF: drops a held frame, reads no timeline
        return Choose(Overlay(selected), selected, KeyboardNativeArtwork.StatusPage);
    }

    /// <summary>After each native tick. Not armed ends the entry; an armed tick whose bytes differ from the last shown ones was
    /// an acknowledged upload of them: the logo starts its bound, a newly selected animation frame closes the animation's
    /// cadence gate (an overlay-only change of the same frame does not re-time it).</summary>
    public bool Stepped(bool armed)
    {
        if (!armed)
        {
            _entered = false; _logoUntil = null;
            _chosen = _chosenBase = _shown = _shownBase = _overlayBase = _overlaid = null;
            Volatile.Write(ref _shownKind, (int)KeyboardNativeArtwork.None);
            Volatile.Write(ref _idle, (int)KeyboardNativeArtwork.None);
            lock (_interaction) { _interactionAt = null; _interactionHold = 0; _pendingInteractions = 0; }
            return false;
        }
        Acknowledge();
        return true;
    }

    /// <summary>The ordinary owner's completed upload uses the same entry bound without a native ARM or firmware capability.
    /// Returns whether the acknowledged image was an authored animation frame; neither the logo nor a popup counts as one.</summary>
    public bool Acknowledge()
    {
        if (_chosen is not { } chosen) return false;
        var animated = false;
        if (_shown is null || !chosen.AsSpan().SequenceEqual(_shown))
        {
            if (ReferenceEquals(_chosenBase, logo)) _logoUntil ??= time.GetTimestamp() + EntryLogoMs * time.TimestampFrequency / 1000;
            else if (_chosenKind != KeyboardNativeArtwork.InteractionStatus && !ReferenceEquals(_chosenBase, _shownBase))
                animated = animation.Uploaded();
        }
        _shown = chosen; _shownBase = _chosenBase;
        Volatile.Write(ref _shownKind, (int)_chosenKind);
        frameAcknowledged?.Invoke(chosen);
        return animated;
    }

    /// <summary>True once per change of what the owner status must say (acknowledged artwork kind, idle choice, overlay availability).</summary>
    public bool TakeChanged()
    {
        var report = Volatile.Read(ref _shownKind) * 16 + Volatile.Read(ref _idle) * 2 + (Volatile.Read(ref _overlayProblem) is null ? 0 : 1);
        if (report == _reported) return false;
        _reported = report;
        return true;
    }

    private byte[] Choose(byte[] chosen, byte[] source, KeyboardNativeArtwork kind)
    {
        _chosenBase = source; _chosenKind = kind;
        return _chosen = chosen;
    }

    /// <summary>The scene overlay over the selected post-logo frame, never the logo. It sees a copy, so it cannot alter the shared
    /// status or a prepared frame; it re-runs when the selection changes, otherwise at most once per keyboard minimum interval
    /// (never a cadence of its own). A throwing, null or wrongly sized answer keeps the ordinary frame and is reported; the
    /// native control path never sees the fault.</summary>
    private byte[] Overlay(byte[] selected)
    {
        if (overlay is null) return selected;
        var now = time.GetTimestamp();
        var revision = overlayRevision?.Invoke() ?? 0;
        if (ReferenceEquals(selected, _overlayBase) && _overlaid is { } previous && now < _overlayNext
            && revision == _overlaidRevision) return previous;
        _overlayBase = selected;
        _overlaidRevision = revision;
        _overlayNext = now + animation.MinimumIntervalMs * time.TimestampFrequency / 1000;
        byte[]? result = null;
        string? problem = null;
        try { result = overlay((byte[])selected.Clone()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { problem = "场景叠加层异常（" + ex.GetType().Name + "）"; }
        if (problem is null && (result is null || result.Length != selected.Length)) problem = "场景叠加层返回的画面无效";
        Volatile.Write(ref _overlayProblem, problem);
        return _overlaid = problem is null ? result! : selected;
    }
}
