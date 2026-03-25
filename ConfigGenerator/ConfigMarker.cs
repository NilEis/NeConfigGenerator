using System;

namespace ConfigGenerator;

/// <summary>
/// Marks a class as scannable for configs
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class ConfigMarker : Attribute
{
    public bool OutputLog { get; set; } = true;
    public bool LoadEnvFile { get;set; } = true;
}