using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnifiedRgb.Setup;

/// <summary>An appsettings document that setup refuses to read or write (oversized, malformed or unexpected).</summary>
public sealed class SetupConfigException : Exception
{
    public SetupConfigException()
    {
    }

    public SetupConfigException(string message)
        : base(message)
    {
    }

    public SetupConfigException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Strict appsettings handling. Strict UTF-8 (BOM tolerated on read, never written), bounded size and depth, no comments or
/// trailing commas, object root, and NO duplicate keys even by case (configuration keys are case-insensitive, so a duplicate
/// would make the effective value ambiguous). Unknown fields are kept verbatim through <see cref="JsonNode"/>.
/// </summary>
public static class SetupConfigDocument
{
    public const int MaxBytes = 1 << 20;
    public const int MaxDepth = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static JsonObject Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            throw new SetupConfigException($"The configuration exceeds {MaxBytes} bytes.");
        }

        if (bytes.StartsWith("﻿"u8))
        {
            bytes = bytes[3..];
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new SetupConfigException("The configuration is not valid UTF-8.", ex);
        }

        CheckStructure(bytes);
        try
        {
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth }) as JsonObject
                ?? throw new SetupConfigException("The configuration root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new SetupConfigException($"The configuration is not valid JSON: {ex.Message}", ex);
        }
    }

    public static byte[] Serialize(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return StrictUtf8.GetBytes(root.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    /// <summary>Case-insensitive member lookup; the document has no case duplicates once parsed by <see cref="Parse"/>.</summary>
    public static KeyValuePair<string, JsonNode?>? Member(JsonObject parent, string name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        foreach (var kv in parent)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kv;
            }
        }

        return null;
    }

    private static void CheckStructure(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = MaxDepth,
        });
        var scopes = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        break;
                    case JsonTokenType.StartArray:
                        if (scopes.Count == 0)
                        {
                            throw new SetupConfigException("The configuration root must be a JSON object.");
                        }

                        scopes.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (!scopes.Peek()!.Add(name))
                        {
                            throw new SetupConfigException($"Duplicate configuration key '{name}' (keys are case-insensitive).");
                        }

                        break;
                    default:
                        if (scopes.Count == 0)
                        {
                            throw new SetupConfigException("The configuration root must be a JSON object.");
                        }

                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new SetupConfigException($"The configuration is not valid JSON: {ex.Message}", ex);
        }
    }
}
