using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

internal sealed partial class ScreenWorker
{
    /// <summary>
    /// Exactly the worker's inline TURZX overlay bytes (its OverlayCanvas pixel operations, then its EncodeOverlayPng WIC/NONE rules
    /// and budget), callable off the worker for the explicitly opted-in <see cref="TurzxOverlayPreparer"/> lab path. The
    /// layer is only read (the first rotation copies it).
    /// </summary>
    internal static (byte[] Bytes, string Note) PrepareOverlayPng(PixelFrame layer, ScreenAssignmentDto assignment, int internalRotation,
        bool simplePng) => EncodeOverlayPng(OverlayCanvas(layer, assignment, internalRotation), simplePng);
}
