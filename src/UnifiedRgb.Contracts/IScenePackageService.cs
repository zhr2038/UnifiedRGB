using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>
/// External scene packages (OPUS-GENERIC-SCENE-PACKAGES), an additional target on the same trusted pipe as
/// <see cref="IRgbService"/> (method names on the wire drop "Async"). Import, listing, details/preview and binding saves never write
/// hardware or change the running scene; only <see cref="ApplyScenePackageAsync"/> does, through the same gates as SetScene.
/// </summary>
public interface IScenePackageService
{
    Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct = default);

    /// <summary>Validates the whole ZIP and installs it; identical content returns the existing entry. Invalid input throws ArgumentException.</summary>
    Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto package, CancellationToken ct = default);

    Task<ScenePackageDetailsDto> GetScenePackageAsync(string contentId, CancellationToken ct = default);

    Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string contentId, CancellationToken ct = default);

    Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto bindings, CancellationToken ct = default);

    /// <summary>Applies the package with its current valid binding snapshot as one transaction; on failure Applied is false and nothing changed.</summary>
    Task<ScenePackageApplyDto> ApplyScenePackageAsync(string contentId, CancellationToken ct = default);

    /// <summary>Compares the expected binding and applies it under the same service gate. A stale binding is refused without publication.</summary>
    Task<ScenePackageApplyDto> ApplyScenePackageCheckedAsync(ScenePackageApplyRequestDto request, CancellationToken ct = default);

    // Chunked import (additive; an older Host answers with a missing method, and implementations that do not support it throw
    // NotSupportedException). Begin opens a Host-owned staging session with a random token, chunks are appended strictly in order,
    // Finish verifies the exact length and SHA-256 of the whole stream and then validates/installs it exactly like
    // ImportScenePackageAsync. Every failure of Finish ends the session; a client must not resend the package through the legacy
    // method after Finish was attempted.

    /// <summary>Opens an upload session; refused (InvalidOperationException) when the session or staging-space bounds are reached.</summary>
    Task<ScenePackageUploadSessionDto> BeginScenePackageUploadAsync(BeginScenePackageUploadDto upload, CancellationToken ct = default)
        => throw new NotSupportedException("Chunked scene-package import is not supported by this service.");

    /// <summary>Appends one chunk at exactly the current offset. Any refused chunk ends the session.</summary>
    Task<ScenePackageUploadProgressDto> UploadScenePackageChunkAsync(ScenePackageUploadChunkDto chunk, CancellationToken ct = default)
        => throw new NotSupportedException("Chunked scene-package import is not supported by this service.");

    /// <summary>Verifies the complete upload and imports it; the session always ends, installed or not.</summary>
    Task<ScenePackageDetailsDto> FinishScenePackageUploadAsync(ScenePackageUploadTokenDto upload, CancellationToken ct = default)
        => throw new NotSupportedException("Chunked scene-package import is not supported by this service.");

    /// <summary>Discards an upload session and its staged bytes; false when no such session exists (already ended or expired).</summary>
    Task<bool> AbortScenePackageUploadAsync(ScenePackageUploadTokenDto upload, CancellationToken ct = default)
        => throw new NotSupportedException("Chunked scene-package import is not supported by this service.");
}
