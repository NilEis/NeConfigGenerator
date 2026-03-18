using System;

namespace ConfigGenerator;

/// <inheritdoc/>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class EnvConfigElement : ConfigElement
{
    /// <summary>
    /// Convert the loaded value to <see cref="Type"/> using a JSON deserializer
    /// </summary>
    public bool IsJson { get; set; } = true;
}