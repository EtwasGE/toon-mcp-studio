using Microsoft.Extensions.Configuration;
using Toon.Core;

namespace Toon.Infrastructure;

public static class Secrets
{
    public static string? ElevenLabsApiKey()
    {
        var key = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            return key;

        if (Environment.GetEnvironmentVariable("TOON_USE_USER_SECRETS") == "0")
            return null;

        try
        {
            // Read on demand so an independent worker sees updates without restarting.
            using var configuration = new ConfigurationManager();
            configuration.AddUserSecrets<Config>(optional: true);
            return configuration["ELEVENLABS_API_KEY"];
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            // Provider exceptions may include fragments of the secrets file. Never forward them.
            throw new ToonException(
                "VOICE_NOT_CONFIGURED",
                "Cannot read User Secrets; check secrets.json format and access"
            );
        }
    }
}
