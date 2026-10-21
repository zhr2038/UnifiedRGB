namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// Starts a chunked package import. <paramref name="Length"/> is the exact ZIP size (1..64 MiB) and <paramref name="Sha256"/> the
/// lowercase SHA-256 of the whole original ZIP; both are checked before anything is validated or installed. No path is sent: the
/// Host stores the bytes only in its own staging file.
/// </summary>
public sealed record BeginScenePackageUploadDto(string Name, long Length, string Sha256);

/// <summary>An open upload: the random session token, the largest accepted chunk and when the session expires if left idle.</summary>
public sealed record ScenePackageUploadSessionDto(string Token, int MaxChunkBytes, DateTimeOffset ExpiresAt);

/// <summary>The next chunk; <paramref name="Offset"/> must equal the bytes received so far (strictly sequential, no gaps or rewrites).</summary>
public sealed record ScenePackageUploadChunkDto(string Token, long Offset, string Base64);

/// <summary>Bytes received so far in the session.</summary>
public sealed record ScenePackageUploadProgressDto(long Received);

/// <summary>Names one upload session (finish or abort).</summary>
public sealed record ScenePackageUploadTokenDto(string Token);
