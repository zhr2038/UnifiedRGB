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
    /// <summary>Status page ON: the case status (or the separately saved, default-off authored frame the selector picks).</summary>
    StatusPage = 2,
    /// <summary>Status page OFF asks for the keyboard's stock animation. No verified in-epoch request shows it with the lever
    /// kept, the existing OFF output is retained (never the status page, an authored frame or black); reported, never claimed.</summary>
    StockOriginalUnavailable = 3,
}

/// <summary>
/// Pure presentation of one owner session whose firmware declared native presentation. A true native-mode entry (the first
/// artwork request after session start or after the native tick last reported not armed) shows the Guangyu logo first, for
/// <see cref="EntryLogoMs"/> from its acknowledged upload; nothing else is offered before it. Afterwards the status-page
/// preference decides: ON presents the bytes <see cref="KeyboardDisplayAnimation.Select"/> picks (the case status unless an
/// authored animation is separately enabled and playable), passed through the optional scene overlay; OFF would be the
/// keyboard's stock animation, which has no verified in-epoch request, so the existing post-logo selection is retained and
/// <see cref="KeyboardNativeArtwork.StockOriginalUnavailable"/> is reported. A preference change inside an entry only re-selects
/// artwork in the same epoch. Repeated mode9 ticks continue the same entry. Native control ownership is a separate input: the
/// native tick decides it and this only answers which bytes it asks for. Owner actor only (status readers use the volatile
/// reports); the actor's artwork comparison and lease wake are its clock, so there is no timer or sender here.
/// </summary>
internal sealed class KeyboardNativePresentation(KeyboardDisplayAnimation animation, byte[]? logo, TimeProvider time,
    Func<byte[], byte[]>? overlay = null, Action<byte[]>? frameAcknowledged = null)
{
    /// <summary>Nominal entry overlay; it ends at this bound after the logo's acknowledged upload, never later.</summary>
    internal const int EntryLogoMs = 1200;

    private bool _entered;
    private long? _logoUntil;
    private byte[]? _chosen, _chosenBase, _shown, _shownBase, _overlayBase, _overlaid;
    private KeyboardNativeArtwork _chosenKind;
    private long _overlayNext;
    private int _shownKind, _reported;
    private string? _overlayProblem;

    /// <summary>Inside one native-mode entry: the legacy selector must not touch the animation's held frame meanwhile.</summary>
    public bool Entered => _entered;

    public bool LogoAvailable => logo is not null;

    /// <summary>What the current entry last had acknowledged (None outside an entry). Any thread.</summary>
    public KeyboardNativeArtwork Shown => (KeyboardNativeArtwork)Volatile.Read(ref _shownKind);

    /// <summary>Why the scene overlay was last unavailable (the ordinary frame was presented instead), or null. Any thread.</summary>
    public string? OverlayProblem => Volatile.Read(ref _overlayProblem);

    /// <summary>The native artwork for now (native tick and lease wake). Without a rendered logo the overlay is skipped.</summary>
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
            return false;
        }
        Acknowledge();
        return true;
    }

    /// <summary>The ordinary owner's completed upload uses the same entry bound without a native ARM or firmware capability.
    /// Returns whether the acknowledged image was an authored animation frame; the logo never counts as one.</summary>
    public bool Acknowledge()
    {
        if (_chosen is not { } chosen) return false;
        var animated = false;
        if (_shown is null || !chosen.AsSpan().SequenceEqual(_shown))
        {
            if (ReferenceEquals(_chosenBase, logo)) _logoUntil ??= time.GetTimestamp() + EntryLogoMs * time.TimestampFrequency / 1000;
            else if (!ReferenceEquals(_chosenBase, _shownBase)) animated = animation.Uploaded();
        }
        _shown = chosen; _shownBase = _chosenBase;
        Volatile.Write(ref _shownKind, (int)_chosenKind);
        frameAcknowledged?.Invoke(chosen);
        return animated;
    }

    /// <summary>True once per change of what the owner status must say (acknowledged artwork kind, overlay availability).</summary>
    public bool TakeChanged()
    {
        var report = Volatile.Read(ref _shownKind) * 2 + (Volatile.Read(ref _overlayProblem) is null ? 0 : 1);
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
        if (ReferenceEquals(selected, _overlayBase) && _overlaid is { } previous && now < _overlayNext) return previous;
        _overlayBase = selected;
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
