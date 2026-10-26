using Microsoft.Extensions.Configuration.UserSecrets;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

public static class SecretsAcceptance
{
    public static void Run(string testRoot)
    {
        var names = new[] { "APPDATA", "HOME", "ELEVENLABS_API_KEY", "TOON_USE_USER_SECRETS" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var profile = Path.Combine(testRoot, "isolated-profile");
        Directory.CreateDirectory(profile);
        try
        {
            Environment.SetEnvironmentVariable("APPDATA", profile);
            Environment.SetEnvironmentVariable("HOME", profile);
            Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", null);
            Environment.SetEnvironmentVariable("TOON_USE_USER_SECRETS", "1");
            var path = PathHelper.GetSecretsPathFromSecretsId("toon-mcp-studio");
            J.Require(
                Path.GetFullPath(path)
                    .StartsWith(
                        Path.GetFullPath(profile) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    ),
                "TEST_FAILED",
                "User Secrets test must use an isolated profile"
            );
            Check(Secrets.ElevenLabsApiKey() == null, "absent secrets are optional");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"ELEVENLABS_API_KEY":"offline-secrets-fixture"}""");
            Check(Secrets.ElevenLabsApiKey() == "offline-secrets-fixture", "shared User Secrets are loaded");
            Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "offline-environment-fixture");
            Check(Secrets.ElevenLabsApiKey() == "offline-environment-fixture", "environment takes priority");
            Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", null);
            File.WriteAllText(path, """{"ELEVENLABS_API_KEY":"offline-updated-fixture"}""");
            Check(Secrets.ElevenLabsApiKey() == "offline-updated-fixture", "updates are visible without restart");
            Environment.SetEnvironmentVariable("TOON_USE_USER_SECRETS", "0");
            Check(Secrets.ElevenLabsApiKey() == null, "offline mode ignores stored credentials");
            Environment.SetEnvironmentVariable("TOON_USE_USER_SECRETS", "1");
            File.WriteAllText(path, "{ offline-malformed-fixture");
            var sanitized = false;
            try
            {
                Secrets.ElevenLabsApiKey();
            }
            catch (ToonException exception)
            {
                sanitized =
                    exception.Code == "VOICE_NOT_CONFIGURED"
                    && !exception.ToString().Contains("offline-malformed-fixture", StringComparison.Ordinal);
            }
            Check(sanitized, "malformed secrets produce a sanitized error");
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        J.Require(condition, "TEST_FAILED", message);
        Console.WriteLine("PASS " + message);
    }
}
