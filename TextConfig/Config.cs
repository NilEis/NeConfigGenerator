using System.Text.RegularExpressions;
using ConfigGenerator;

namespace TextConfig;

// lVGH1t2wImdtCPE0OjTwYtLe
[ConfigMarker]
[EnvConfigElement(
    Type = typeof(string),
    Name = "VaultIp",
    Default = "http://10.0.0.128:0101")]
[EnvConfigElement(
    Type = typeof(string),
    Name = "VaultToken",
    Default = "abcdef")]
[EnvConfigElement(
    Type = typeof(string),
    Name = "VaultUnsealKey",
    Default = "abefg=")]
[EnvConfigElement(
    Type = typeof(string),
    Name = "DataProtectionDir",
    Default = "SomeWhere")]
[VaultConfigElement(
    Type = typeof(string),
    Name = "DeeplApi",
    Default = "string.Empty",
    FromVaultPath = "data/apis",
    Anonymise = true)]
[EnvConfigElement(
    Type = typeof(string),
    Name = "HostUrl",
    Default = "http://localhost:5200")]
[EnvConfigElement(
    Type = typeof(string),
    Name = "DatabaseIp",
    Default = "SomewhereOverTheRainbow")]
[EnvConfigElement(
    Type = typeof(int),
    Name = "DatabasePort",
    Default = 6776)]
[ConstantConfigElement(
    Type = typeof(string),
    Name = "DbConnectionString",
    Init =
        "$\"{DatabaseIp}:{DatabasePort}\"")]
public static partial class Config
{
    [GeneratedRegex(" #.*$")]
    private static partial Regex CommentMatcher();
}